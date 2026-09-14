// SingleInstanceClient.cs
// 設計 2026-09-14 §4: 引き渡しの送り側。2 つ目のインスタンスが使う。
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
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
    /// <summary>
    /// 既存インスタンスへ前面化を依頼する。
    /// </summary>
    /// <param name="pipeName">接続先パイプ名。</param>
    /// <param name="connectTimeout">
    /// 接続確立までの上限。<b>ACK 待ちとは別予算</b>にしてある —— 1 つの値を両方に使うと
    /// 最悪で 2 倍の時間がかかり、呼び出し側が全体の所要時間を見積もれない。
    /// </param>
    /// <param name="ackTimeout">
    /// 要求送信 + ACK 受信の上限。<b>サーバ側の前面化待ち(Task 7 の <c>ActivateTimeout</c>)
    /// より長くすること</b>。短いと、前面化が成功しているのに待ちきれず
    /// <see cref="HandoffResult.NoResponse"/> を返し、ユーザーには
    /// 「ウィンドウは前面に出たのにエラーも出る」と見える。
    /// 予算の入れ子の全体像は <see cref="SingleInstanceServer"/> の remarks を参照。
    /// </param>
    internal static HandoffResult TryHandOff(
        string pipeName,
        TimeSpan connectTimeout,
        TimeSpan ackTimeout
    )
    {
        try
        {
            // TokenImpersonationLevel.Identification: これを指定しないと Windows 既定の
            // SecurityImpersonation が適用され、なりすましサーバが
            // ImpersonateNamedPipeClient で kxEdit の完全な偽装トークンを得られる
            // (実測: 無指定 => level=Impersonation / 本指定 => level=Identification)。
            // 昇格した kxEdit が被害者になるとローカル権限昇格に化ける。
            // Identification は「誰であるかの確認」までしか許さない。
            // Anonymous は使えない(サーバ側が ERROR_BAD_IMPERSONATION_LEVEL になる)。
            //
            // PipeOptions.CurrentUserOnly: .NET が接続後にパイプオブジェクトの
            // 所有者 SID を比較する。所有者はカーネルが管理していて他ユーザーは詐称
            // できないので、下の MainModule.FileName 比較より強い保証になる。
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                TokenImpersonationLevel.Identification
            );
            client.Connect((int)connectTimeout.TotalMilliseconds);

            // ★3: 相手が本当に自分と同じ kxEdit か。今日のペイロードは空なので
            // 実害は無いが、将来ファイルパスを載せた時点で漏洩経路になる(設計 §6)。
            // 【順序が本質】この検証は要求を送る前に行う。後ろに置くと、
            // 検証で弾く相手にペイロードを渡した後になる。
            if (!TryVerifyPeer(client, out uint peerPid))
                return HandoffResult.ForeignPeer;

            // ★1: フォアグラウンド権を相手へ譲渡してから依頼する。
            // 失敗しても続行する(前面化されないだけで、引き渡し自体は成立しうる)。
            if (!NativeMethods.AllowSetForegroundWindow(peerPid))
                Trace.TraceWarning("single-instance: AllowSetForegroundWindow failed");

            // ★1: ACK を待たずに終了すると、譲渡した権利ごと消えて前面化が失敗する。
            // 送信も受信も同じ期限の下で行う(理由は <see cref="SendAndAwaitAck"/>)。
            return SendAndAwaitAck(client, ackTimeout)
                ? HandoffResult.Success
                : HandoffResult.NoResponse;
        }
        catch (UnauthorizedAccessException ex)
        {
            // PipeOptions.CurrentUserOnly の所有者 SID 不一致がここに来る。
            // これは「応答が無い」ではなく「相手が別人」なので ForeignPeer が正しい。
            Trace.TraceWarning($"single-instance: peer owned by another user: {ex.Message}");
            return HandoffResult.ForeignPeer;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            Trace.TraceWarning($"single-instance: handoff failed: {ex.Message}");
            return HandoffResult.NoResponse;
        }
    }

    /// <summary>
    /// パイプのサーバ側プロセスが、自分と同じ実行ファイルで動いているか。
    /// <b>確かめられない場合は拒否側へ倒す</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本メソッドは「検証して<b>別物だった</b>」と「そもそも<b>検証できなかった</b>」を
    /// どちらも <c>false</c> に潰す。拒否側へ倒す判断としては正しいが、Task 6 が出す文言
    /// 「別の場所にある kxEdit が既に起動しています」は後者では誤診になる。
    /// 区別が要るときのために、Trace のメッセージを 2 系統に分けてある
    /// (<c>peer image mismatch</c> / <c>cannot verify</c>)。文言の調整は Task 7 の担当。
    /// </para>
    /// <para>
    /// パス比較は <see cref="StringComparison.OrdinalIgnoreCase"/> で、正規化はしない。
    /// 8.3 短縮名・シンボリックリンク・マップドライブ経由で起動された同一の実行ファイルは
    /// 別物と判定されうる(<c>ForeignPeer</c> の誤診)。<b>fail-closed なのでセキュリティ上の
    /// 穴ではない</b> —— 誤診の向きは常に「拒否」であり、なりすましを通す方向には倒れない。
    /// </para>
    /// </remarks>
    private static bool TryVerifyPeer(NamedPipeClientStream client, out uint peerPid)
    {
        peerPid = 0;
        if (!NativeMethods.GetNamedPipeServerProcessId(client.SafePipeHandle, out peerPid))
        {
            Trace.TraceWarning("single-instance: cannot verify peer: unresolved pipe server pid");
            return false;
        }
        try
        {
            using var peer = Process.GetProcessById((int)peerPid);
            string? peerImage = peer.MainModule?.FileName;
            string? selfImage = Environment.ProcessPath;
            if (peerImage is null || selfImage is null)
            {
                Trace.TraceWarning("single-instance: cannot verify peer: image path unavailable");
                return false;
            }
            if (!string.Equals(peerImage, selfImage, StringComparison.OrdinalIgnoreCase))
            {
                Trace.TraceWarning($"single-instance: peer image mismatch: {peerImage}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
            when (ex
                    is ArgumentException
                        or InvalidOperationException
                        or Win32Exception
                        or NotSupportedException
            )
        {
            Trace.TraceWarning($"single-instance: cannot verify peer: {ex.Message}");
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
    /// <para>
    /// <b><c>ConfigureAwait(false)</c> を外してはならない</b>。この <c>GetAwaiter().GetResult()</c>
    /// は UI スレッドから呼ばれうる。1 か所でも同期コンテキストへ戻す設定にすると、
    /// 継続が UI スレッドを待ち、UI スレッドが結果を待つデッドロックになる(実測で追認済み)。
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

    /// <summary>
    /// ACK を 1 行読む。
    /// </summary>
    /// <remarks>
    /// バッファ 64 バイトの根拠: ACK は <c>SingleInstanceRequest.Ack</c> + <c>"\n"</c> の
    /// 11 バイト固定で、将来応答にフィールドが増えても同じ桁に収まる。行が 64 バイトに
    /// 収まらなければ ACK ではないので、読み捨てずに <c>false</c>(= 応答なし扱い)でよい。
    /// </remarks>
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
