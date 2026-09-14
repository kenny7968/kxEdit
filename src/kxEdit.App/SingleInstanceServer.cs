// SingleInstanceServer.cs
// 設計 2026-09-14 §4: 引き渡しの受け側。1 つ目のインスタンスが起動直後に開始する。
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace kxEdit.App;

/// <summary>
/// 2 つ目のインスタンスからの前面化要求を待ち受ける。
/// </summary>
/// <remarks>
/// <para>
/// <b>待受は Mutex 取得直後・<c>MainForm</c> 構築より前に始める</b>(設計 §4「起動中レース」)。
/// <c>MainForm</c> の構築は重く、ここを後ろに置くと「Mutex は取られているのにパイプが無い」
/// 窓が広がって、2 つ目が誤って「応答しません」エラーになる。
/// </para>
/// <para>
/// <b>インスタンス数は 1 に固定する</b>。名前付きパイプの名前空間は OS 全体で共有されるので、
/// 1 に固定して名前を専有し、他プロセスが同名の追加インスタンスを作れないようにする。
/// 引き渡しは一瞬で終わるので直列化のコストは問題にならない。
/// </para>
/// <para>
/// <c>onActivate</c> は<b>パイプスレッドから呼ばれる</b>。UI スレッドへの
/// マーシャルは呼び出し側(Task 7 の <c>PendingActivation</c>)の責務。戻り値 <c>false</c> は
/// 「前面化できなかった」を意味し、その場合 ACK を返さない —— 2 つ目はそれを見て
/// 「応答しません」と判断する(設計 D4)。
/// </para>
/// </remarks>
internal sealed class SingleInstanceServer : IDisposable
{
    /// <summary>1 要求の上限。改行が来ないまま超えたら不正として切る。</summary>
    private const int MaxRequestBytes = 4096;

    /// <summary>1 接続あたりの上限時間。悪意ある / 壊れた相手で待受を詰まらせない。</summary>
    private static readonly TimeSpan PerConnectionTimeout = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly Func<bool> _onActivate;
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeServerStream? _pipe;
    private Task? _loop;
    private bool _disposed;

    internal SingleInstanceServer(string pipeName, Func<bool> onActivate)
    {
        _pipeName = pipeName;
        _onActivate = onActivate;
    }

    /// <summary>
    /// 待ち受けを開始する。<b>失敗しても起動は止めない</b>(呼び出し側は <c>false</c> を
    /// 受けても通常起動を続ける)。名前を先回りされた場合がここに来る。
    /// </summary>
    internal bool Start()
    {
        try
        {
            _pipe = CreatePipe(_pipeName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 名前を他プロセスに専有されている。攻撃者へデータを渡さない方向へ倒れる
            // (こちらからは一切接続しない)。以後の 2 つ目起動はエラーになる。
            Trace.TraceWarning($"single-instance: listen failed: {ex.Message}");
            return false;
        }
        _loop = Task.Run(() => RunAsync(_pipe));
        return true;
    }

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        // ACL は現在のユーザーのみ。パイプ名前空間は OS 全体共有なので、
        // 既定の DACL に頼らず明示的に絞る(設計 §4 ★3)。
        var user =
            WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("現在のユーザーの SID を取得できない");
        var security = new PipeSecurity();
        security.AddAccessRule(
            new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow)
        );
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security
        );
    }

    private async Task RunAsync(NamedPipeServerStream pipe)
    {
        while (!_cts.IsCancellationRequested)
        {
            // Disconnect が失敗したらパイプはもう使えないので待受を終える。
            // finally 句からは return できない(CS0157)ため、フラグで外へ伝える。
            bool pipeBroken = false;
            try
            {
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return; // Dispose された
            }
            catch (IOException ex)
            {
                Trace.TraceWarning($"single-instance: accept failed: {ex.Message}");
                return;
            }

            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                deadline.CancelAfter(PerConnectionTimeout);
                await HandleAsync(pipe, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 1 件の失敗で待受を終わらせない(以後の起動が全部エラーになるため)。
                Trace.TraceWarning($"single-instance: handoff failed: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (pipe.IsConnected)
                        pipe.Disconnect();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    Trace.TraceWarning($"single-instance: disconnect failed: {ex.Message}");
                    pipeBroken = true;
                }
            }

            if (pipeBroken)
                return;
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        string? wire = await ReadLineAsync(pipe, ct).ConfigureAwait(false);
        if (SingleInstanceRequest.TryParse(wire) is null)
        {
            // ACK を返さずに切る。前面化もしない。
            Trace.TraceWarning("single-instance: rejected malformed request");
            return;
        }

        // ACK より前に前面化する。2 つ目は ACK を待ってから終了するので、
        // ここで前面化を終えておかないと譲渡されたフォアグラウンド権が消える(設計 §4 ★1)。
        if (!_onActivate())
        {
            Trace.TraceWarning("single-instance: activation reported failure; no ack");
            return;
        }

        byte[] ack = Encoding.UTF8.GetBytes(SingleInstanceRequest.Ack + "\n");
        await pipe.WriteAsync(ack, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 改行までを読む。<see cref="MaxRequestBytes"/> を超えたら <c>null</c>(=不正)。
    /// <c>StreamReader</c> を使わないのは、上限を自分で決めるため。
    /// </summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[MaxRequestBytes];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)
                .ConfigureAwait(false);
            if (read == 0)
                break; // 相手が閉じた
            int newline = Array.IndexOf(buffer, (byte)'\n', 0, total + read);
            total += read;
            if (newline >= 0)
                return Encoding.UTF8.GetString(buffer, 0, newline);
        }
        return null;
    }

    /// <summary>
    /// 待受を止める。<b>2 回以上呼んでも安全</b>(<see cref="IDisposable"/> の契約)。
    /// ガードが無いと 2 回目の <c>_cts.Cancel()</c> が
    /// <see cref="ObjectDisposedException"/> を投げる(実測)。終了処理の途中で
    /// 例外を出すと、後続の後始末が丸ごと飛ぶ。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _cts.Cancel();
        try
        {
            _pipe?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Trace.TraceWarning($"single-instance: pipe dispose failed: {ex.Message}");
        }
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        { /* 待受ループの終了例外は握る(終了処理を止めない) */
        }
        _cts.Dispose();
    }
}
