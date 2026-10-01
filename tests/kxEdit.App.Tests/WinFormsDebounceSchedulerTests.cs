using System.Diagnostics;

namespace kxEdit.App.Tests;

/// <summary>
/// 本番の間引き: 最後の予約だけが 1 回走ること・取り消せること・遅延を最後の予約から数え直すこと
/// (時間は片側の粗い下限だけを見る。精度は検証しない)。
/// </summary>
public class WinFormsDebounceSchedulerTests
{
    /// <summary>条件が満たされるか上限時間が過ぎるまでメッセージを汲む。</summary>
    private static void PumpUntil(Func<bool> done, int maxMs)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < maxMs)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// メッセージを汲まずに待つ。S2925(Fact 本体での Thread.Sleep 直呼び禁止)は本体だけを見るので、
    /// PreviewUserDataFolderTests.SleepMs と同じくヘルパー経由にする。
    /// </summary>
    private static void SleepWithoutPumping(int milliseconds) => Thread.Sleep(milliseconds);

    [Fact]
    public void Schedule_Twice_RunsOnlyLatestOnce() =>
        Sta.Run(() =>
        {
            using var s = new WinFormsDebounceScheduler(30);
            var ran = new List<string>();
            s.Schedule(() => ran.Add("first"));
            s.Schedule(() => ran.Add("second"));

            PumpUntil(() => ran.Count > 0, 3000);
            PumpUntil(() => false, 150); // 余計な 2 回目が来ないこと

            Assert.Equal(new[] { "second" }, ran);
        });

    [Fact]
    public void Cancel_PreventsRun() =>
        Sta.Run(() =>
        {
            using var s = new WinFormsDebounceScheduler(30);
            bool ran = false;
            s.Schedule(() => ran = true);
            s.Cancel();

            PumpUntil(() => ran, 300);

            Assert.False(ran);
        });

    [Fact]
    public void Schedule_Again_RestartsDelayFromLatestCall() =>
        Sta.Run(() =>
        {
            // A を予約し、汲まずに遅延より長く待つ(WM_TIMER は汲まない限り配送されない)。
            // そこで B を予約すると、遅延は B から数え直す=汲み始めてすぐには発火しない。
            // Schedule の Stop() が無いと、A の時点で満了したタイマーがそのまま発火する(約 0 ms)。
            // 片側の不等式だけを見る: 遅いマシンでは発火が遅れるだけなので偽の失敗になりにくい。
            const int delayMs = 200;
            using var s = new WinFormsDebounceScheduler(delayMs);
            var ran = new List<string>();
            s.Schedule(() => ran.Add("A"));
            SleepWithoutPumping(delayMs * 2);

            // 計測は予約の前から始める(予約との間でスレッドが止まっても、計測値が実際の経過を下回らない)
            var sw = Stopwatch.StartNew();
            s.Schedule(() => ran.Add("B"));
            PumpUntil(() => ran.Count > 0, 5000);
            long elapsedMs = sw.ElapsedMilliseconds;

            Assert.Equal(new[] { "B" }, ran);
            Assert.True(
                elapsedMs >= delayMs / 2,
                $"再予約から {elapsedMs} ms で発火した(遅延 {delayMs} ms の半分未満)"
            );
        });

    [Fact]
    public void Schedule_AfterDispose_DoesNotRun() =>
        Sta.Run(() =>
        {
            // WinForms の Timer は Dispose 後でも Start すると動き出す。解放後の予約は無視する。
            var s = new WinFormsDebounceScheduler(30);
            s.Dispose();
            bool ran = false;
            s.Schedule(() => ran = true);

            PumpUntil(() => ran, 300);

            Assert.False(ran);
            s.Dispose(); // 2 回目も安全
        });
}
