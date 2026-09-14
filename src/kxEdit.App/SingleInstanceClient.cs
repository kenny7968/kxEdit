// SingleInstanceClient.cs
// 設計 2026-09-14 §4: 引き渡しの送り側。2 つ目のインスタンスが使う。
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;

namespace kxEdit.App;

/// <summary>引き渡しの結果。失敗は 2 種類に分かれる(設計 §4「失敗理由を 2 つに分ける」)。</summary>
internal enum HandoffResult
{
    /// <summary>既存インスタンスが前面化し、ACK を返した。</summary>
    Success,

    /// <summary>接続できない / ACK が返らない。既存インスタンスがハングしている等。</summary>
    NoResponse,

    /// <summary>接続先が自分と同じ実行ファイルではない(別ビルドの kxEdit・またはなりすまし)。</summary>
    ForeignPeer,
}

/// <summary>
/// 既存インスタンスへ前面化を依頼する。
/// </summary>
internal static class SingleInstanceClient
{
    internal static HandoffResult TryHandOff(string pipeName, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            );
            client.Connect((int)timeout.TotalMilliseconds);

            // ★3: 相手が本当に自分と同じ kxEdit か。今日のペイロードは空なので
            // 実害は無いが、将来ファイルパスを載せた時点で漏洩経路になる(設計 §6)。
            if (!TryVerifyPeer(client, out uint peerPid))
                return HandoffResult.ForeignPeer;

            // ★1: フォアグラウンド権を相手へ譲渡してから依頼する。
            // 失敗しても続行する(前面化されないだけで、引き渡し自体は成立しうる)。
            if (!NativeMethods.AllowSetForegroundWindow(peerPid))
                Trace.TraceWarning("single-instance: AllowSetForegroundWindow failed");

            // ★1: ACK を待たずに終了すると、譲渡した権利ごと消えて前面化が失敗する。
            // 送信も受信も同じ期限の下で行う(理由は <see cref="SendAndAwaitAck"/>)。
            return SendAndAwaitAck(client, timeout)
                ? HandoffResult.Success
                : HandoffResult.NoResponse;
        }
        catch (Exception ex)
            when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"single-instance: handoff failed: {ex.Message}");
            return HandoffResult.NoResponse;
        }
    }

    /// <summary>
    /// パイプのサーバ側プロセスが、自分と同じ実行ファイルで動いているか。
    /// <b>確かめられない場合は拒否側へ倒す</b>。
    /// </summary>
    private static bool TryVerifyPeer(NamedPipeClientStream client, out uint peerPid)
    {
        peerPid = 0;
        if (!NativeMethods.GetNamedPipeServerProcessId(client.SafePipeHandle, out peerPid))
        {
            Trace.TraceWarning("single-instance: cannot resolve pipe server pid");
            return false;
        }
        try
        {
            using var peer = Process.GetProcessById((int)peerPid);
            string? peerImage = peer.MainModule?.FileName;
            string? selfImage = Environment.ProcessPath;
            if (peerImage is null || selfImage is null)
                return false;
            return string.Equals(peerImage, selfImage, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
            when (ex
                    is ArgumentException
                        or InvalidOperationException
                        or Win32Exception
                        or NotSupportedException
            )
        {
            Trace.TraceWarning($"single-instance: cannot verify pipe peer: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 要求を送り、ACK を読む。<b>送受信の両方に同じ期限を掛ける</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>同期 <c>Write</c> / <c>Read</c> を使わないのが要点</b>。名前付きパイプに
    /// <c>ReadTimeout</c> / <c>WriteTimeout</c> は効かないため、接続だけ受けて黙り込む相手
    /// (ハングした kxEdit・先回りした squatter)に対して<b>無制限にブロック</b>する。
    /// </para>
    /// <para>
    /// <b>受信だけでなく送信も危ない</b>(実測で確認)。<c>PipeOptions.Asynchronous</c> で
    /// 開いたストリームの同期 <c>Write</c> は、内部で
    /// <c>WriteAsync(…, CancellationToken.None).GetAwaiter().GetResult()</c> を呼ぶだけなので
    /// 打ち切れない。さらにサーバ側のバッファクォータが 0 のとき、書き込みは相手が読むまで
    /// 完了しない —— つまり「接続だけ受けて読まない」相手に対して、ACK を待つ以前に
    /// <c>Write</c> で永久に止まる。
    /// </para>
    /// <para>
    /// <c>WriteAsync</c> / <c>ReadAsync</c> は <see cref="CancellationToken"/> を尊重するので、
    /// それで期限を掛ける。呼び出し側は起動直後の同期経路なので、ここで待ち合わせる。
    /// </para>
    /// </remarks>
    private static bool SendAndAwaitAck(NamedPipeClientStream client, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            return SendAndAwaitAckAsync(client, deadline.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            Trace.TraceWarning($"single-instance: ack not received: {ex.GetType().Name}");
            return false;
        }
    }

    private static async Task<bool> SendAndAwaitAckAsync(
        NamedPipeClientStream client,
        CancellationToken ct
    )
    {
        byte[] payload = Encoding.UTF8.GetBytes(SingleInstanceRequest.Activate.Serialize() + "\n");
        await client.WriteAsync(payload, ct).ConfigureAwait(false);
        return await ReadAckAsync(client, ct).ConfigureAwait(false);
    }

    private static async Task<bool> ReadAckAsync(Stream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[64];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            int newline = Array.IndexOf(buffer, (byte)'\n', 0, total + read);
            total += read;
            if (newline >= 0)
            {
                string line = Encoding.UTF8.GetString(buffer, 0, newline);
                return string.Equals(line, SingleInstanceRequest.Ack, StringComparison.Ordinal);
            }
        }
        return false;
    }
}
