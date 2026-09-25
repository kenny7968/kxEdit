using System.Diagnostics;

namespace kxEdit.App.Tests;

/// <summary>本番の間引き: 最後の予約だけが 1 回走ること・取り消せること(時間の精度は検証しない)。</summary>
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
