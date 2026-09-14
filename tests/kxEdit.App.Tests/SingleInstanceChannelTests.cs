using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;

namespace kxEdit.App.Tests;

/// <summary>
/// 引き渡しトランスポート(設計 2026-09-14 §4)。
/// <para>
/// 本テストは<b>実パイプを使う</b>。ウィンドウは作らないので STA も UI も不要。
/// パイプ名は <see cref="UniqueName"/> で毎回変える —— 実運用の kxEdit が同じ PC で
/// 動いていても干渉させないため。
/// </para>
/// <para>
/// <b>前面化の回数は <c>Interlocked</c> / <c>Volatile</c> で数える</b>。ハンドラは
/// パイプの待受スレッドから呼ばれ、assertion はテストスレッドで読むので、生の
/// <c>++</c> と生読みではスレッド間の可視性が保証されない。<c>SingleInstanceGateTests</c> も
/// 同じ作法に揃えてある(隣接するテストで観測の書き方を変えないこと)。
/// </para>
/// </summary>
public class SingleInstanceChannelTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(800);

    private static string UniqueName() => $"kxEditTest.{Environment.ProcessId}.{Guid.NewGuid():N}";

    /// <summary>接続・ACK とも <see cref="ShortTimeout"/> で引き渡す(大半のテストの既定)。</summary>
    private static HandoffResult HandOff(string pipeName) =>
        SingleInstanceClient.TryHandOff(pipeName, ShortTimeout, ShortTimeout);

    /// <summary>テスト用の生クライアント(サーバ実装の作法に合わせて開く)。</summary>
    private static NamedPipeClientStream RawClient(string pipeName) =>
        new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    [Fact]
    public void HandOff_InvokesActivationOnServer_AndReportsSuccess()
    {
        string pipe = UniqueName();
        int activations = 0;
        using var server = new SingleInstanceServer(
            pipe,
            _ =>
            {
                Interlocked.Increment(ref activations);
                return true;
            }
        );
        Assert.True(server.Start());

        Assert.Equal(HandoffResult.Success, HandOff(pipe));
        Assert.Equal(1, Volatile.Read(ref activations));
    }

    [Fact]
    public void HandOff_ServesMultipleRequestsInSequence()
    {
        // 1 接続で待受ループが終わる変異を殺す(2 回目以降が NoResponse になる)。
        string pipe = UniqueName();
        int activations = 0;
        using var server = new SingleInstanceServer(
            pipe,
            _ =>
            {
                Interlocked.Increment(ref activations);
                return true;
            }
        );
        Assert.True(server.Start());

        Assert.Equal(HandoffResult.Success, HandOff(pipe));
        Assert.Equal(HandoffResult.Success, HandOff(pipe));
        Assert.Equal(2, Volatile.Read(ref activations));
    }

    [Fact]
    public void Server_SurvivesClientThatDisconnectsBeforeReadingAck()
    {
        // C-1(脆弱性レビュー Critical)の回帰。
        // ACK を読まずに切る相手が 1 回来ただけで待受が恒久停止していた:
        //   WriteAsync(ack) が IOException → .NET が State=Broken にする
        //   → IsConnected=false → finally の Disconnect() がスキップ
        //   → カーネル側は接続済みのまま → 次の accept も IOException → ループ終了。
        // 攻撃以前に正常運用で踏む(B 側の期限が A の前面化完了より先に切れた場合)。
        string pipe = UniqueName();
        int activations = 0;
        using var server = new SingleInstanceServer(
            pipe,
            _ =>
            {
                Interlocked.Increment(ref activations);
                return true;
            }
        );
        Assert.True(server.Start());

        using (var rude = RawClient(pipe))
        {
            rude.Connect((int)ShortTimeout.TotalMilliseconds);
            byte[] payload = Encoding.UTF8.GetBytes("KXEDIT1 ACTIVATE\n");
            rude.Write(payload, 0, payload.Length);
            // ACK を読まずに切断する(using を抜ける)。
        }

        // 待受は生きていなければならない。
        Assert.Equal(HandoffResult.Success, HandOff(pipe));
        Assert.Equal(2, Volatile.Read(ref activations));
    }

    [Fact]
    public void HandOff_WithoutServer_ReportsNoResponse()
    {
        Assert.Equal(HandoffResult.NoResponse, HandOff(UniqueName()));
    }

    [Fact]
    public void HandOff_WhenActivationFails_ReportsNoResponse()
    {
        // 既存インスタンスの UI スレッドがハングしている状況(設計 D4)。
        // ACK を返さないので、B 側は「応答しない」と判定しなければならない。
        string pipe = UniqueName();
        using var server = new SingleInstanceServer(pipe, _ => false);
        Assert.True(server.Start());

        Assert.Equal(HandoffResult.NoResponse, HandOff(pipe));
    }

    [Fact]
    public void Server_RejectsMalformedRequest_WithoutInvokingActivation()
    {
        // 他プロセスが書いた不正なバイト列で前面化を起こさせない(設計 §4 ★3)。
        string pipe = UniqueName();
        int activations = 0;
        using var server = new SingleInstanceServer(
            pipe,
            _ =>
            {
                Interlocked.Increment(ref activations);
                return true;
            }
        );
        Assert.True(server.Start());

        using (var client = RawClient(pipe))
        {
            client.Connect((int)ShortTimeout.TotalMilliseconds);
            byte[] junk = Encoding.UTF8.GetBytes("KXEDIT1 QUIT\n");
            client.Write(junk, 0, junk.Length);
            client.Flush();
            // ACK は返らない。読もうとすると 0 バイトで切れる。
            Assert.Equal(0, client.Read(new byte[16], 0, 16));
        }

        Assert.Equal(0, Volatile.Read(ref activations));
        // 不正要求のあとも待受は生きている(1 件で死ぬ変異を殺す)。
        Assert.Equal(HandoffResult.Success, HandOff(pipe));
        Assert.Equal(1, Volatile.Read(ref activations));
    }

    [Fact]
    public async Task Server_CutsOffRequestThatExceedsSizeLimit()
    {
        // 改行を送らずに延々と書き続ける相手でメモリを食わせない。
        //
        // 【変異を殺す作り】1 接続上限をわざと長く(30 秒)取る。こうすると
        // 「上限に達したのでサーバが切った」と「per-connection の期限切れで切れた」を
        // 区別できる —— MaxRequestBytes を 4096 から大きい値へ変異させると、
        // サーバは続きを待ち続けて下の Read が 2 秒以内に返らず、テストが落ちる。
        // (クライアントを閉じてしまうと read==0 で null になり、上限を通らなくても
        //  同じ結果になってしまうので、閉じずに保持するのも要点。)
        string pipe = UniqueName();
        int activations = 0;
        using var server = new SingleInstanceServer(
            pipe,
            _ =>
            {
                Interlocked.Increment(ref activations);
                return true;
            },
            perConnectionTimeout: TimeSpan.FromSeconds(30)
        );
        Assert.True(server.Start());

        using (var client = RawClient(pipe))
        {
            await client.ConnectAsync((int)ShortTimeout.TotalMilliseconds);

            // 上限ちょうどを改行なしで送る。バッファクォータ 0 なので
            // 相手が読むのに合わせて進む = 同期 Write では詰まりうるため非同期で送る。
            byte[] flood = Encoding.UTF8.GetBytes(new string('x', 4096));
            using var writeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.WriteAsync(flood, writeDeadline.Token);

            // サーバは上限到達で「不正」と判断し、ACK を返さず切断する。
            using var readDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            int read = await client.ReadAsync(new byte[16].AsMemory(), readDeadline.Token);
            Assert.Equal(0, read);
        }

        Assert.Equal(0, Volatile.Read(ref activations));
    }

    [Fact]
    public async Task Server_AbandonsConnectionThatExceedsPerConnectionTimeout()
    {
        // PerConnectionTimeout が実際に効いていること。
        // deadline.CancelAfter(...) を削除する変異を殺す —— 削除すると、
        // 下の無言クライアントを掴んだまま待受が止まり、2 本目の引き渡しが
        // (インスタンス数 1 なので)接続すらできず NoResponse になる。
        string pipe = UniqueName();
        int activations = 0;
        using var server = new SingleInstanceServer(
            pipe,
            _ =>
            {
                Interlocked.Increment(ref activations);
                return true;
            },
            perConnectionTimeout: TimeSpan.FromSeconds(1)
        );
        Assert.True(server.Start());

        using var mute = RawClient(pipe);
        await mute.ConnectAsync((int)ShortTimeout.TotalMilliseconds);
        // 改行を送らないので、サーバは期限まで読み続けることになる。
        byte[] partial = Encoding.UTF8.GetBytes("KXEDIT1 ACTIV");
        using (var writeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await mute.WriteAsync(partial, writeDeadline.Token);
        }

        // 期限が来れば待受は解放され、次の引き渡しが通る。
        // (接続待ちは 1 接続上限より長く取る。)
        Assert.Equal(
            HandoffResult.Success,
            SingleInstanceClient.TryHandOff(pipe, TimeSpan.FromSeconds(10), ShortTimeout)
        );
        Assert.Equal(1, Volatile.Read(ref activations));
    }

    [Fact]
    public void HandOff_WhenPeerAcceptsButNeverAnswers_ReportsNoResponse_WithinTimeout()
    {
        // ★重要: 接続だけ受けて黙り込む相手(ハングした kxEdit・悪意ある squatter)に対し、
        // クライアントが永久にブロックしないこと。同一プロセス内の生パイプなので
        // ★3 の実行ファイルパス検証は通過する = 検証の先にあるブロックを狙い撃ちできる。
        string pipe = UniqueName();
        using var mute = NamedPipeServerStreamAcl.Create(
            pipe,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            PipeAcl.CurrentUserOnly()
        );
        // 接続だけは受け付ける(黙り込む相手の再現)。完了は待たないので破棄する。
        _ = mute.WaitForConnectionAsync();

        var sw = Stopwatch.StartNew();
        var result = HandOff(pipe);
        sw.Stop();

        Assert.Equal(HandoffResult.NoResponse, result);
        // 期限の 4 倍まで見る(CI の揺れを許容しつつ「無制限ブロック」は確実に落とす)。
        Assert.True(
            sw.Elapsed < TimeSpan.FromMilliseconds(ShortTimeout.TotalMilliseconds * 4),
            $"ACK 待ちが期限内に返らなかった: {sw.Elapsed}"
        );
    }

    [Fact]
    public async Task HandOff_ToDifferentExecutable_ReportsForeignPeer_WithoutSendingPayload()
    {
        // ★3 のなりすまし検証。別の実行ファイル(powershell.exe)が同名パイプを
        // 立てている場合、ForeignPeer を返し、かつ<b>ペイロードを一切送らない</b>こと。
        // 送ってしまうと、設計 §6 でファイルパスを載せた時点で情報漏洩になる。
        string pipe = UniqueName();
        // powershell 側: パイプを立てて READY を出し、接続後 1.5 秒だけ読み、
        // 受け取ったバイト数を GOT= で報告する。
        string script =
            "$s = New-Object System.IO.Pipes.NamedPipeServerStream("
            + $"'{pipe}','InOut',1,'Byte','Asynchronous'); "
            + "[Console]::Out.WriteLine('READY'); [Console]::Out.Flush(); "
            + "$s.WaitForConnection(); "
            + "$b = New-Object byte[] 64; $t = $s.ReadAsync($b,0,64); "
            + "$g = 0; if ($t.Wait(1500)) { $g = $t.Result }; "
            + "[Console]::Out.WriteLine(\"GOT=$g\"); [Console]::Out.Flush(); $s.Dispose();";

        using var peer = new Process
        {
            StartInfo = new ProcessStartInfo("powershell.exe")
            {
                Arguments =
                    "-NoProfile -NonInteractive -Command \"" + script.Replace("\"", "\\\"") + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        Assert.True(peer.Start());
        try
        {
            // 起動待ちは寛大に(powershell の初回起動は遅い)。READY で待ち合わせる。
            string? ready = await ReadLineWithTimeoutAsync(
                peer.StandardOutput,
                TimeSpan.FromSeconds(60)
            );
            if (ready?.Trim() != "READY")
            {
                // 【stderr を読むのは診断性のため】RedirectStandardError しておきながら
                // 一度も読まないと、子側が失敗した場合に 60 秒待ったうえで
                // Assert.Equal("READY", null) としか出ず、落ちた理由がどこにも残らない
                // (スクリプトの構文エラー・実行ポリシー・powershell.exe 不在など)。
                string error = await ReadToEndWithTimeoutAsync(
                    peer.StandardError,
                    TimeSpan.FromSeconds(5)
                );
                Assert.Fail(
                    $"powershell 側が READY を出さなかった: stdout='{ready ?? "(なし)"}' / stderr='{error}'"
                );
            }

            // 実行ファイルが違う(testhost.exe vs powershell.exe)ので拒否されること。
            Assert.Equal(
                HandoffResult.ForeignPeer,
                SingleInstanceClient.TryHandOff(pipe, TimeSpan.FromSeconds(5), ShortTimeout)
            );

            // そして 1 バイトも渡っていないこと(検証が送信より前に走る)。
            string? got = await ReadLineWithTimeoutAsync(
                peer.StandardOutput,
                TimeSpan.FromSeconds(30)
            );
            Assert.Equal("GOT=0", got?.Trim());
        }
        finally
        {
            try
            {
                if (!peer.WaitForExit(5000))
                    peer.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            { /* 既に終了している */
            }
        }
    }

    /// <summary>子プロセスの stdout を 1 行、期限付きで読む(ハングさせない)。</summary>
    private static async Task<string?> ReadLineWithTimeoutAsync(
        StreamReader reader,
        TimeSpan timeout
    )
    {
        var line = reader.ReadLineAsync();
        var completed = await Task.WhenAny(line, Task.Delay(timeout));
        return completed == (Task)line ? await line : null;
    }

    /// <summary>
    /// 子プロセスの出力を末尾まで、期限付きで読む(診断メッセージ用)。
    /// </summary>
    /// <remarks>
    /// <c>ReadToEndAsync</c> は子がストリームを閉じるまで返らないので、期限は必須。
    /// 失敗経路でしか呼ばないため、期限切れでもテストを落とさずその旨を文字列で返す。
    /// </remarks>
    private static async Task<string> ReadToEndWithTimeoutAsync(
        StreamReader reader,
        TimeSpan timeout
    )
    {
        var all = reader.ReadToEndAsync();
        var completed = await Task.WhenAny(all, Task.Delay(timeout));
        return completed == (Task)all ? (await all).Trim() : "(期限内に読めなかった)";
    }

    [Fact]
    public void Start_ReturnsFalse_WhenPipeNameIsAlreadyTaken()
    {
        // 悪意あるプロセスが先回りして同名パイプを作った場合(設計 §4 ★3 の逆向き)。
        // A は待受なしで通常起動を続ける = 起動を止めない。
        string pipe = UniqueName();
        using var squatter = new SingleInstanceServer(pipe, _ => true);
        Assert.True(squatter.Start());

        using var second = new SingleInstanceServer(pipe, _ => true);
        Assert.False(second.Start());
    }

    [Fact]
    public void Start_ReturnsFalse_WhenSquatterAllowsUnlimitedInstances()
    {
        // 上のテストの squatter は maxNumberOfServerInstances: 1 なので
        // 2 本目は ERROR_PIPE_BUSY で弾かれる。本当に危ないのは、攻撃者が
        // 「無制限インスタンス」で先に名前を取った場合 —— ここで相乗りできてしまうと、
        // 接続が攻撃者と我々にランダムに振り分けられ、将来ファイルパスを載せた時点で
        // 情報漏洩になる(設計 §4 ★3)。
        // Win32 は全インスタンスで nMaxInstances の一致を要求するため、
        // maxNumberOfServerInstances: 1 のままで弾ける(FILE_FLAG_FIRST_PIPE_INSTANCE の
        // 明示は不要)。その事実をここで固定する。
        string pipe = UniqueName();
        using var squatter = NamedPipeServerStreamAcl.Create(
            pipe,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            PipeAcl.CurrentUserOnly()
        );

        using var ours = new SingleInstanceServer(pipe, _ => true);
        Assert.False(ours.Start());
    }

    [Fact]
    public void Start_ReturnsFalse_WhenPipeNameIsInvalid()
    {
        // 設計 §7 の不変条件「単一インスタンス機構は起動時例外にしてはならない」。
        // 名前が不正でも投げずに false を返し、呼び出し側は通常起動を続けられること。
        //
        // "anonymous" は .NET が予約名として ArgumentOutOfRangeException で弾く
        // (IOException でも UnauthorizedAccessException でもない)。catch を
        // この 2 種に絞る変異を殺す —— 絞ると Start() から例外が漏れ、kxEdit が
        // まったく起動しなくなる。バックスラッシュ入りの名前はパイプ名前空間では
        // 合法(サブディレクトリ扱い)なので、不正名の代表には使えない。
        using var server = new SingleInstanceServer("anonymous", _ => true);
        Assert.False(server.Start());
    }

    [Fact]
    public void Dispose_StopsListening()
    {
        string pipe = UniqueName();
        var server = new SingleInstanceServer(pipe, _ => true);
        Assert.True(server.Start());
        server.Dispose();

        Assert.Equal(HandoffResult.NoResponse, HandOff(pipe));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        // IDisposable の契約。ガードが無いと 2 回目の Cancel() が
        // ObjectDisposedException を投げ、終了処理の後続が飛ぶ。
        // Start 済み / 未 Start の両方を見る(未 Start は _pipe / _loop が null の経路)。
        var started = new SingleInstanceServer(UniqueName(), _ => true);
        Assert.True(started.Start());
        started.Dispose();
        started.Dispose();

        var neverStarted = new SingleInstanceServer(UniqueName(), _ => true);
        neverStarted.Dispose();
        neverStarted.Dispose();
    }
}
