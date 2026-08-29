using System.Linq;

namespace kxEdit.App.Tests;

/// <summary>
/// M-1(監査 2026-08-22 / 設計 2026-08-29 §5): 未処理例外を握って
/// 「退避 → 通知 → 終了」の順に処理する。
/// </summary>
/// <remarks>
/// 実機での必要性: WinForms 既定の未処理例外ダイアログの「終了」は保存確認のキャンセルを
/// 無視して落ち、hot exit バックアップが書かれないことがある(設計書 §2.1・3 回中 2 回)。
/// 「続行」は出さない=壊れた状態で走り続けるより、退避して落ちるほうが結果が読める。
/// 本体(<see cref="CrashHandler.Handle"/>)の順序と再入だけをここで固定し、
/// 実際の退避/通知/終了は <see cref="ICrashSink"/> の向こう側(Program の本番実装)に置く。
/// </remarks>
public class CrashHandlerTests
{
    private sealed class FakeSink : ICrashSink
    {
        public List<string> Calls { get; } = new();
        public bool FlushResult { get; set; } = true;
        public bool? NotifiedFlushed { get; private set; }
        public Exception? NotifiedException { get; private set; }

        public bool FlushBackups()
        {
            Calls.Add("flush");
            return FlushResult;
        }

        public void Notify(bool flushed, Exception? ex)
        {
            Calls.Add("notify");
            NotifiedFlushed = flushed;
            NotifiedException = ex;
        }

        public void Exit() => Calls.Add("exit");
    }

    private sealed class ThrowingFlushSink : ICrashSink
    {
        public List<string> Calls { get; } = new();
        public bool? NotifiedFlushed { get; private set; }

        public bool FlushBackups() => throw new InvalidOperationException("flush failed");

        public void Notify(bool flushed, Exception? ex)
        {
            Calls.Add("notify");
            NotifiedFlushed = flushed;
        }

        public void Exit() => Calls.Add("exit");
    }

    private sealed class ThrowingNotifySink : ICrashSink
    {
        public List<string> Calls { get; } = new();

        public bool FlushBackups()
        {
            Calls.Add("flush");
            return true;
        }

        public void Notify(bool flushed, Exception? ex) =>
            throw new InvalidOperationException("notify failed");

        public void Exit() => Calls.Add("exit");
    }

    [Fact]
    public void Handle_CallsFlushThenNotifyThenExit()
    {
        // 順序は入れ替えられない: 通知の文面が退避結果に依存し、終了は両方の後でなければ
        // 「退避も通知もされずに落ちる」= WinForms 既定より悪くなる。
        var sink = new FakeSink();
        new CrashHandler(sink).Handle(new InvalidOperationException("boom"));
        Assert.Equal(new[] { "flush", "notify", "exit" }, sink.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Handle_PassesFlushResultToNotify(bool flushed)
    {
        // 「退避できた」と嘘をつかないこと。false を true に丸める変異を kill する。
        var sink = new FakeSink { FlushResult = flushed };
        new CrashHandler(sink).Handle(new InvalidOperationException("boom"));
        Assert.Equal(flushed, sink.NotifiedFlushed);
    }

    [Fact]
    public void Handle_PassesExceptionToNotify()
    {
        // 例外は post-mortem のため sink へ渡す(本番実装は Trace へ落とすだけで
        // MessageBox には出さない=Program.MainFormCrashSink.Notify 参照)。
        var sink = new FakeSink();
        var ex = new InvalidOperationException("boom");
        new CrashHandler(sink).Handle(ex);
        Assert.Same(ex, sink.NotifiedException);
    }

    [Fact]
    public void Handle_NullException_StillFlushesNotifiesExits()
    {
        // AppDomain.UnhandledException の ExceptionObject は Exception とは限らない
        // (as Exception が null になりうる)。null でも退避と終了は行う。
        var sink = new FakeSink();
        new CrashHandler(sink).Handle(null);
        Assert.Equal(new[] { "flush", "notify", "exit" }, sink.Calls);
        Assert.Null(sink.NotifiedException);
    }

    [Fact]
    public void Handle_Twice_ExitsOnlyOnce()
    {
        // 再入ガード。ハンドラ内で再び例外が出ても無限ループしない。
        var sink = new FakeSink();
        var h = new CrashHandler(sink);
        h.Handle(new InvalidOperationException("first"));
        h.Handle(new InvalidOperationException("second"));
        Assert.Equal(1, sink.Calls.Count(x => x == "exit"));
        Assert.Equal(1, sink.Calls.Count(x => x == "flush"));
        Assert.Equal(1, sink.Calls.Count(x => x == "notify"));
    }

    [Fact]
    public void Handle_FlushThrows_StillNotifiesAndExits()
    {
        // 退避で落ちても通知と終了までは必ず到達する(ここで止まると
        // 「例外ダイアログも出ずに固まる」= 既定挙動より悪くなる)。
        var sink = new ThrowingFlushSink();
        new CrashHandler(sink).Handle(new InvalidOperationException("boom"));
        Assert.Equal(new[] { "notify", "exit" }, sink.Calls);
        Assert.False(sink.NotifiedFlushed); // 退避できたと言い切れない = false
    }

    [Fact]
    public void Handle_NotifyThrows_StillExits()
    {
        // 通知に失敗しても終了は行う(UIA/MessageBox が出せない環境でプロセスが残らない)。
        var sink = new ThrowingNotifySink();
        new CrashHandler(sink).Handle(new InvalidOperationException("boom"));
        Assert.Equal(new[] { "flush", "exit" }, sink.Calls);
    }

    [Fact]
    public void Ctor_NullSink_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new CrashHandler(null!));
}
