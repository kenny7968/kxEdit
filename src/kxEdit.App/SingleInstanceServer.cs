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
/// <b>時間予算の入れ子(ここが唯一の正)</b> —— 4 つの期限は次の順序を保つこと。
/// 1 つでも逆転すると「前面化は成功しているのにエラーダイアログが出る」等の誤診になる。
/// <code>
///   ActivateTimeout  &lt;  ackTimeout  &lt;  PerConnectionTimeout  &lt;  Dispose の待ち
///   (Task 7 の       ) (クライアント) (本クラス・既定 5 秒 ) (PerConnection + 余裕)
///   (前面化待ち      ) (ACK 待ち    )
/// </code>
/// <list type="bullet">
/// <item><c>ActivateTimeout &lt; ackTimeout</c>: 逆だと、前面化が成功しているのに
/// クライアントが待ちきれず <c>NoResponse</c> を返す。</item>
/// <item><c>ackTimeout &lt; PerConnectionTimeout</c>: 逆だと、サーバが先に接続を畳んで
/// クライアントが正常な ACK を取り逃す。</item>
/// <item><c>PerConnectionTimeout &lt; Dispose の待ち</c>: 逆だと、終了時に処理中の接続を
/// 待ちきれず <c>_loop</c> を取り残す。本クラスはこれを自動で満たす
/// (<see cref="Dispose"/> は <c>PerConnectionTimeout + 2 秒</c> 待つ)。</item>
/// </list>
/// 具体値は Task 6 / 7 が決める。本クラスは既定値と上の関係だけを定める。
/// </para>
/// </remarks>
internal sealed class SingleInstanceServer : IDisposable
{
    /// <summary>
    /// 1 要求の上限。改行が来ないまま超えたら不正として切る。
    /// </summary>
    /// <remarks>
    /// 【設計 §6 でファイルパスを載せる際は必ず見直すこと】Windows のパスは長く、
    /// 複数選択なら 4096 バイトは容易に超える。超えた要求は<b>黙って ACK 無し</b>になり、
    /// ユーザーには「応答しません」としか見えない —— 原因が極めて追いにくい失敗の仕方をする。
    /// </remarks>
    private const int MaxRequestBytes = 4096;

    /// <summary>既定の 1 接続あたり上限。</summary>
    internal static readonly TimeSpan DefaultPerConnectionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// accept が連続して失敗したときに、パイプを作り直す回数の上限。
    /// </summary>
    /// <remarks>
    /// 作り直しが毎回失敗する状況(名前を奪われた等)で無限ループにしないための歯止め。
    /// accept が 1 回でも成功したらカウンタは 0 に戻る。
    /// </remarks>
    private const int MaxConsecutiveAcceptFailures = 3;

    private readonly string _pipeName;
    private readonly Func<CancellationToken, bool> _onActivate;
    private readonly TimeSpan _perConnectionTimeout;
    private readonly CancellationTokenSource _cts = new();

    /// <summary><see cref="_pipe"/> の差し替えと破棄を直列化する。</summary>
    private readonly object _gate = new();

    private NamedPipeServerStream? _pipe;
    private Task? _loop;
    private bool _disposed;

    /// <param name="pipeName">待ち受けるパイプ名。</param>
    /// <param name="onActivate">
    /// 前面化要求の受理ハンドラ。<b>パイプスレッドから呼ばれる</b> —— UI スレッドへの
    /// マーシャルは呼び出し側(Task 7 の <c>PendingActivation</c>)の責務。
    /// <para>
    /// <b>戻り値は「要求を引き受けたか(= ACK を返してよいか)」</b>であって
    /// 「前面化が完了したか」ではない。Task 7 の <c>PendingActivation.Request()</c> は
    /// ウィンドウ未生成時に保留へ積んで <c>true</c> を返すが、その時点で前面化は
    /// 起きていない —— それでも引き渡しは成立しているので ACK を返すのが正しい。
    /// <c>false</c> は「引き受けられなかった」を意味し、ACK を返さない。2 つ目はそれを見て
    /// 「応答しません」と判断する(設計 D4)。
    /// </para>
    /// <para>
    /// 渡される <see cref="CancellationToken"/> は<b>この接続の期限</b>。UI スレッドへの
    /// マーシャル待ちは必ずこれで打ち切ること。打ち切らないと、UI がハングした瞬間に
    /// 待受ループごと無期限停止する。
    /// </para>
    /// </param>
    /// <param name="perConnectionTimeout">
    /// 1 接続あたりの上限時間。悪意ある / 壊れた相手で待受を詰まらせない。
    /// 省略時は <see cref="DefaultPerConnectionTimeout"/>(テストから短縮するための引数)。
    /// </param>
    internal SingleInstanceServer(
        string pipeName,
        Func<CancellationToken, bool> onActivate,
        TimeSpan? perConnectionTimeout = null
    )
    {
        _pipeName = pipeName;
        _onActivate = onActivate;
        _perConnectionTimeout = perConnectionTimeout ?? DefaultPerConnectionTimeout;
    }

    /// <summary>
    /// 待ち受けを開始する。<b>失敗しても起動は止めない</b>(呼び出し側は <c>false</c> を
    /// 受けても通常起動を続ける)。名前を先回りされた場合がここに来る。
    /// </summary>
    /// <remarks>
    /// <b>本メソッドは例外を投げない</b>(失敗は <c>false</c> で返す)。これは実装の都合では
    /// なく契約である —— <see cref="SingleInstanceGate"/> は Mutex を取ってから本メソッドを
    /// 呼び、戻り値を見てゲートを組み立てる間に <c>try</c>/<c>finally</c> を置いていない。
    /// ここが throw するようになると、Mutex ハンドルのリークではなく
    /// <b>起動時クラッシュ</b>(= ゲートが D4 のエラーを出せずに落ちる)という重い形で壊れる。
    /// </remarks>
    internal bool Start()
    {
        NamedPipeServerStream pipe;
        try
        {
            pipe = CreatePipe(_pipeName);
        }
        // 【意図的に全例外を捕まえる】設計 §7 の不変条件「単一インスタンス機構は
        // 起動時例外にしてはならない」を守るため。IOException / UnauthorizedAccessException
        // (名前を先回りされた)以外にも、SID を取れなければ InvalidOperationException、
        // 名前が不正なら ArgumentException 系が飛ぶ。どれも「待受なしで通常起動」が正解で、
        // ここで投げると kxEdit がまったく起動しなくなる。型名は Trace に残す。
        catch (Exception ex)
        {
            Trace.TraceWarning(
                $"single-instance: listen failed: {ex.GetType().Name}: {ex.Message}"
            );
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                pipe.Dispose();
                return false;
            }
            _pipe = pipe;
        }
        _loop = Task.Run(() => RunAsync(pipe));
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

        // 【重要・バッファサイズ 0 は性能設定ではなく正しさの前提】
        // out バッファのクォータを 0 にすると、書き込みは「相手が読むまで完了しない」
        // 同期的な受け渡しになる。ACK は書いた直後に Disconnect するので、
        // クォータを持たせると未読の ACK がカーネルに溜まったまま破棄され、
        // 引き渡しが確率的に失敗する。実測:
        //   outBuf=   0, 相手の読み出しを 300ms 遅延 => 11 バイト 'KXEDIT1 OK\n' が届く
        //   outBuf=4096, 同上                        =>  0 バイト(ACK が捨てられた)
        // ユーザーから見ると「ウィンドウは前面に出たのにエラーダイアログも出る」になる。
        // in バッファ側も同じ理由で 0 のまま揃える。変更してはならない。
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
        int consecutiveAcceptFailures = 0;

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                consecutiveAcceptFailures = 0;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return; // Dispose された
            }
            catch (IOException ex)
            {
                // 【一発退場にしない】待受を諦めると、このプロセスは以後ずっと引き渡し
                // 不能になる。設計 D4 はフォールバック起動をしないので、それは
                // 「2 つ目を二度と起動できない」を意味する —— C-1 で実際に起きた壊れ方。
                // パイプインスタンスを作り直して自己修復を試みる(回数上限つき)。
                Trace.TraceWarning($"single-instance: accept failed: {ex.Message}");
                if (++consecutiveAcceptFailures > MaxConsecutiveAcceptFailures)
                {
                    Trace.TraceWarning(
                        "single-instance: giving up listening after repeated accept failures"
                    );
                    return;
                }
                if (!TryRecreatePipe(out var fresh))
                    return;
                pipe = fresh;
                continue;
            }

            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                deadline.CancelAfter(_perConnectionTimeout);
                await HandleAsync(pipe, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 1 件の失敗で待受を終わらせない(以後の起動が全部エラーになるため)。
                Trace.TraceWarning($"single-instance: handoff failed: {ex.Message}");
            }
            finally
            {
                // 【C-1: IsConnected で条件を付けてはならない】ACK 書き込みが
                // IOException(Pipe is broken)で落ちると .NET は State=Broken にするので
                // IsConnected は false になる。しかしカーネル側のパイプインスタンスは
                // 接続済みのままなので、Disconnect を飛ばすと次の accept が必ず失敗し、
                // 待受が恒久停止する。無条件に呼ぶこと。
                // Disconnect は未接続 / 二重呼び出しで InvalidOperationException を投げる
                // (実測)ので、それも捕まえる。ここから例外を出すとループ Task が
                // 無言で faulted になり、やはり待受が死ぬ。
                try
                {
                    pipe.Disconnect();
                }
                catch (Exception ex)
                    when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    Trace.TraceWarning($"single-instance: disconnect failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// パイプインスタンスを作り直す。<see cref="Dispose"/> と競合しないよう
    /// <see cref="_gate"/> の下で行う。作り直せなければ <c>false</c>(呼び出し側は待受終了)。
    /// </summary>
    private bool TryRecreatePipe(out NamedPipeServerStream fresh)
    {
        fresh = null!;
        lock (_gate)
        {
            if (_disposed || _cts.IsCancellationRequested)
                return false;

            try
            {
                _pipe?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Trace.TraceWarning($"single-instance: stale pipe dispose failed: {ex.Message}");
            }
            _pipe = null;

            try
            {
                fresh = CreatePipe(_pipeName);
            }
            catch (Exception ex)
            {
                // 名前を奪われた等。待受は諦めるが起動は止めない(設計 §7)。
                Trace.TraceWarning(
                    $"single-instance: pipe recreate failed: {ex.GetType().Name}: {ex.Message}"
                );
                return false;
            }
            _pipe = fresh;
            return true;
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
        // ct を渡すのが要点 —— UI スレッドへのマーシャル待ちが無期限になると、
        // 待受ループごと止まる(この呼び出しは PerConnectionTimeout の内側にある)。
        if (!_onActivate(ct))
        {
            Trace.TraceWarning("single-instance: activation not accepted; no ack");
            return;
        }

        // FlushAsync は呼ばない。PipeStream は FlushAsync を override しないため
        // Stream 既定実装(ブロッキング Flush をスレッドプールで実行)になり、
        // 開始後は ct を尊重しない = 5 秒期限の外に出る唯一の I/O になってしまう。
        // out バッファのクォータが 0 なので、WriteAsync が完了した時点で
        // 相手は既に読んでおり、そもそも Flush の必要が無い(CreatePipe のコメント参照)。
        byte[] ack = Encoding.UTF8.GetBytes(SingleInstanceRequest.Ack + "\n");
        await pipe.WriteAsync(ack, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 改行までを読む。<see cref="MaxRequestBytes"/> を超えたら <c>null</c>(=不正)。
    /// <c>StreamReader</c> を使わないのは、上限を自分で決めるため。
    /// </summary>
    /// <remarks>
    /// <b><c>ConfigureAwait(false)</c> を外してはならない</b>。本クラスの待受ループは
    /// UI スレッドから <c>GetAwaiter().GetResult()</c> で待ち合わせられうる経路に繋がる。
    /// 1 か所でも同期コンテキストへ戻すと、そこでデッドロックする(実測で追認済み)。
    /// </remarks>
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
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _cts.Cancel();

        lock (_gate)
        {
            try
            {
                _pipe?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Trace.TraceWarning($"single-instance: pipe dispose failed: {ex.Message}");
            }
            _pipe = null;
        }

        try
        {
            // 【予算の入れ子】処理中の 1 接続は最長 _perConnectionTimeout かかるので、
            // それより長く待つ(クラス remarks の入れ子関係の最外周)。
            // 待ちきれなかったことは握り潰さず Trace に残す —— 取り残した _loop は
            // 破棄済みパイプに触れて faulted になるため、原因追跡の手掛かりが要る。
            if (_loop is not null && !_loop.Wait(_perConnectionTimeout + TimeSpan.FromSeconds(2)))
                Trace.TraceWarning("single-instance: listen loop did not finish within timeout");
        }
        catch (AggregateException ex)
        { /* 待受ループの終了例外は握る(終了処理を止めない) */
            Trace.TraceWarning($"single-instance: listen loop faulted: {ex.InnerException?.Message}");
        }

        _cts.Dispose();
    }
}
