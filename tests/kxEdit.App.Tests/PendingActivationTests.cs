using System.Diagnostics;
using System.Threading.Tasks;

namespace kxEdit.App.Tests;

/// <summary>
/// 前面化要求の受け皿(設計 2026-09-14 §4「起動中レース」)。
/// フォーム生成前に来た要求を取りこぼさないこと・接続の期限を尊重することを固定する。
/// <para>
/// <b>戻り値の意味は「前面化できたか」ではなく「要求を引き受けたか(ACK を返してよいか)」</b>
/// である(<see cref="SingleInstanceServer"/> の <c>onActivate</c> の契約)。
/// ウィンドウ未生成で <c>true</c> を返すのは、この定義では正しい。
/// </para>
/// </summary>
public class PendingActivationTests
{
    /// <summary>
    /// フォーカスを奪わず画面外に作る(テスト実行中のチラつき防止。
    /// <c>MainFormSmokeTests.ShowMainForm</c> と同じ流儀)。
    /// </summary>
    private static Form NewOffScreenForm() =>
        new()
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000),
        };

    /// <summary>
    /// <c>BeginInvoke</c> のマーシャル先を走らせるためにメッセージをポンプしながら、
    /// パイプスレッド役のタスクの完了を待つ。
    /// <para>
    /// 待ちの上限は安全側の 10 秒。<c>PendingActivation</c> の <c>ActivateTimeout</c> は 3 秒なので、
    /// 前面化が走らない実装でも 3 秒で <c>false</c> が返って抜ける(= ハングではなく赤で終わる)。
    /// </para>
    /// </summary>
    private static void PumpUntilCompleted(Task task)
    {
        var sw = Stopwatch.StartNew();
        // Wait(5ms) は Thread.Sleep の代わり(S2925)。ポンプと待機を交互に回す。
        while (!task.Wait(TimeSpan.FromMilliseconds(5)) && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            Application.DoEvents();
        }
        Assert.True(task.IsCompleted, "Request が返ってこない");
    }

    [Fact]
    public void Request_BeforeFormExists_IsAcceptedAndDeferred()
    {
        // ウィンドウがまだ無い時点の要求は「失敗」ではない。
        // false を返す変異は、起動直後の 2 つ目起動を誤ってエラーにする(設計 §4 起動中レース)。
        var pending = new PendingActivation();
        Assert.True(pending.Request(CancellationToken.None));
    }

    /// <summary>
    /// 接続の期限が切れているときは受理しない。
    /// <para>
    /// <b>このテストは <c>ct</c> を無視する変異(<c>WaitOne(ActivateTimeout)</c> だけにする)を
    /// 殺すためにある</b> —— Task 4 の C-1(UI が一度詰まると待受ループごと止まり、以後その
    /// kxEdit は引き渡し不能になる)と同じ壊れ方への唯一の防波堤である。
    /// </para>
    /// <para>
    /// 決定論の根拠: <see cref="Sta"/> はメッセージをポンプしないので、
    /// <c>BeginInvoke</c> したデリゲートは<b>走らない</b>=その完了ハンドルは永久に非シグナル。
    /// キャンセル済みトークン側だけがシグナル済みなので <c>WaitAny</c> は必ず 1 を返す。
    /// </para>
    /// <para>
    /// <b>戻り値だけでは変異を殺せない</b>(実測)。<c>ct</c> を無視して
    /// <c>AsyncWaitHandle.WaitOne(ActivateTimeout)</c> だけにした変異も、ポンプが無い以上
    /// <c>false</c> を返す —— 違うのは<b>3 秒ブロックしてから</b>返す点だけである。
    /// そして C-1 の実害は戻り値ではなく<b>待受スレッドを止めること</b>なので、
    /// <b>所要時間まで見ないとこのテストは空虚になる</b>。上限 1.5 秒は
    /// <c>ActivateTimeout</c>(3 秒)の半分 —— 変異は必ず超え、正しい実装は即座に返る。
    /// </para>
    /// </summary>
    [Fact]
    public void Request_WhenConnectionDeadlineAlreadyExpired_DoesNotAccept_AndReturnsPromptly() =>
        Sta.Run(() =>
        {
            int activated = 0;
            var pending = new PendingActivation(_ => activated++);
            using var form = NewOffScreenForm();
            form.Show();
            pending.Attach(form);

            using var expired = new CancellationTokenSource();
            expired.Cancel();

            var sw = Stopwatch.StartNew();
            bool accepted = pending.Request(expired.Token);
            sw.Stop();

            Assert.False(accepted);
            Assert.True(
                sw.Elapsed < TimeSpan.FromSeconds(1.5),
                $"接続の期限切れは即座に打ち切ること(ct を無視して活性化の期限まで待っている)。実測: {sw.Elapsed}"
            );
            // 期限切れでも前面化そのものはキューされている(BeginInvoke は取り消せない)。
            // ここで見たいのは「ACK を返さない」ことだけなので、発火数は問わない。
            Assert.InRange(activated, 0, 1);
        });

    /// <summary>
    /// 陽性対照: ウィンドウがあり期限も生きていれば、<b>UI スレッドで</b>前面化してから受理する。
    /// <para>
    /// スレッドまで見るのは Task 5 の申し送り —— <c>Control.Handle</c> を UI スレッド以外から
    /// 初回に触ると、そのスレッドでハンドルが生成されメッセージポンプの所属を誤る。
    /// <c>BeginInvoke</c> を外して直呼びする変異はここで赤くなる。
    /// </para>
    /// <para>
    /// 「前面化が<b>完了してから</b> <c>true</c> を返す」ことも同時に固定する(設計 §4 ★1)。
    /// 投げっぱなしで即 <c>true</c> にすると、2 つ目は ACK を受けて即終了し、譲渡した
    /// フォアグラウンド権ごと消える —— A の <c>SetForegroundWindow</c> は拒否され、
    /// タスクバーが点滅するだけになる。実装計画が「Task 4 のテストはこの破壊を一切検出
    /// できない」と明記したとおり、<b>ここが唯一の網</b>である。
    /// </para>
    /// <para>
    /// <b>観測点は「<c>Request</c> が返った瞬間の発火数」でなければならない</b>(レビュー指摘 I-1)。
    /// <c>PumpUntilCompleted</c> の<b>後</b>で <c>activated</c> を見ると、ポンプ自身が
    /// デリゲートを走らせてしまうので、投げっぱなし変異でも Task の完了が少し遅れれば
    /// <c>1</c> になって<b>緑で生存する</b>(タイミング依存)。Task の中で戻り時点の値を
    /// 捕まえれば決定論的になる —— 正しい実装では <c>WaitAny</c> が 0 を返すのは
    /// <c>ThreadMethodEntry.Complete()</c>(<c>finally</c> でコールバックの<b>後</b>に呼ばれる)
    /// の後なので必ず <c>1</c>、投げっぱなし変異では必ず <c>0</c> になる。
    /// </para>
    /// </summary>
    [Fact]
    public void Request_WhenFormExists_ActivatesOnUiThreadBeforeAccepting() =>
        Sta.Run(() =>
        {
            int uiThread = Environment.CurrentManagedThreadId;
            int activated = 0;
            int activatedOn = 0;
            var pending = new PendingActivation(_ =>
            {
                activated++;
                activatedOn = Environment.CurrentManagedThreadId;
            });
            using var form = NewOffScreenForm();
            form.Show();
            pending.Attach(form);

            // 要求はパイプスレッド(=UI スレッド以外)から来る。
            // 戻り時点の発火数を Task の中で捕まえる(下の assertion の決定論はこれに依る)。
            int activatedAtReturn = -1;
            var request = Task.Run(() =>
            {
                bool accepted = pending.Request(CancellationToken.None);
                activatedAtReturn = Volatile.Read(ref activated);
                return accepted;
            });

            PumpUntilCompleted(request);

            Assert.True(request.Result);
            Assert.Equal(1, activatedAtReturn); // ★1: 前面化を終えてから受理している
            Assert.Equal(1, activated);
            Assert.Equal(uiThread, activatedOn);
        });

    [Fact]
    public void Attach_ConsumesDeferredRequest()
    {
        int activated = 0;
        var pending = new PendingActivation(_ => activated++);
        Assert.True(pending.Request(CancellationToken.None));

        pending.Attach(form: null!);
        Assert.Equal(1, activated);
    }

    [Fact]
    public void Attach_WithoutDeferredRequest_DoesNotActivate()
    {
        // 起動のたびに勝手に前面化しない(通常起動では要求が無い)。
        int activated = 0;
        var pending = new PendingActivation(_ => activated++);

        pending.Attach(form: null!);
        Assert.Equal(0, activated);
    }

    [Fact]
    public void Attach_ConsumesDeferredRequestOnlyOnce()
    {
        int activated = 0;
        var pending = new PendingActivation(_ => activated++);
        Assert.True(pending.Request(CancellationToken.None));

        pending.Attach(form: null!);
        pending.Attach(form: null!);
        Assert.Equal(1, activated);
    }

    /// <summary>
    /// 保留の消化で前面化が投げた例外は、<b>起動を止めてはならない</b>
    /// (Task 5 の申し送り: <c>WindowActivator.Activate</c> は <c>form.Handle</c> で
    /// <see cref="ObjectDisposedException"/> を投げうる)。<c>Attach</c> は <c>MainForm.OnShown</c>
    /// から呼ばれるので、包み忘れると<b>起動シーケンスごと道連れ</b>になる ——
    /// 設計の「前面化の失敗で起動を止めない」に直接抵触する。
    /// </summary>
    [Fact]
    public void Attach_WhenActivationThrows_DoesNotPropagate()
    {
        var pending = new PendingActivation(_ => throw new ObjectDisposedException("form"));
        Assert.True(pending.Request(CancellationToken.None));

        pending.Attach(form: null!); // 例外が漏れればここで赤くなる

        // 保留は消化済み扱い(次の Attach で二度目を投げない)。
        pending.Attach(form: null!);
    }

    /// <summary>
    /// <c>BeginInvoke</c> したデリゲートの中で前面化が投げても、<b>ACK 経路は壊さない</b>。
    /// <para>
    /// 包み忘れると実測で次が起きる: WinForms が例外を <c>Application.ThreadException</c> へ
    /// 回送 → <c>Program.cs</c> の配線が <c>CrashHandler</c> → <c>Environment.Exit(1)</c>。
    /// 一方 <c>WaitAny</c> は 0(完了)を返すので 2 つ目は ACK を受けて静かに終了する ——
    /// ユーザーから見ると<b>「2 つ目を起動したら 1 つ目が消えた」</b>。
    /// </para>
    /// </summary>
    [Fact]
    public void Request_WhenActivationThrows_StillAccepts_AndDoesNotEscapeToThreadException() =>
        Sta.Run(() =>
        {
            var pending = new PendingActivation(_ => throw new ObjectDisposedException("form"));
            using var form = NewOffScreenForm();
            form.Show();
            pending.Attach(form);

            Exception? escaped = null;
            void OnThreadException(object? sender, ThreadExceptionEventArgs e) =>
                escaped = e.Exception;

            Application.ThreadException += OnThreadException;
            try
            {
                var request = Task.Run(() => pending.Request(CancellationToken.None));
                PumpUntilCompleted(request);

                // 要求は引き受けている(前面化の成否は ACK の条件ではない)。
                Assert.True(request.Result);
                Assert.Null(escaped);
            }
            finally
            {
                Application.ThreadException -= OnThreadException;
            }
        });
}
