using System.Diagnostics;

namespace kxEdit.App.Tests;

/// <summary>
/// M-1(設計 2026-08-29 §5.3): <see cref="UiCrashSink"/> の marshal・タイムアウト・文面選択。
/// </summary>
/// <remarks>
/// <see cref="CrashHandler"/> 側のテストは「順序と再入」しか見ない。ここが無いと、
/// このブランチが潰そうとしている<b>嘘の安全宣言</b>(退避できていないのに「復元できます」)を
/// 作る変異 — true/false の文面入れ替え・タイムアウトを true に丸める — が全部緑のまま通る
/// (Task 4 レビュー Major-2)。
/// </remarks>
public class UiCrashSinkTests
{
    private sealed class FakeHost : ICrashUiHost
    {
        public bool CanMarshal { get; set; } = true;
        public bool InvokeRequired { get; set; }

        /// <summary>Post されたアクションを走らせるか。false = UI スレッドが死んでいる/詰まっている。</summary>
        public bool RunPosted { get; set; } = true;

        /// <summary>Post が投げる例外(ハンドル破棄との競合を模す)。</summary>
        public Exception? PostThrows { get; set; }

        public bool FlushResult { get; set; } = true;
        public Exception? FlushThrows { get; set; }

        public int FlushCalls { get; private set; }
        public List<string> Messages { get; } = new();

        public void Post(Action action)
        {
            if (PostThrows is not null)
                throw PostThrows;
            if (RunPosted)
                action();
        }

        public bool FlushBackups()
        {
            FlushCalls++;
            if (FlushThrows is not null)
                throw FlushThrows;
            return FlushResult;
        }

        public void ShowMessage(string text) => Messages.Add(text);
    }

    private static UiCrashSink Sink(FakeHost host) => new(host, TimeSpan.FromMilliseconds(200)); // タイムアウト経路を短時間で踏む

    // ===== 文面(取り違えると嘘の安全宣言になる)=====

    [Fact]
    public void CrashMessage_Flushed_SaysRestorable()
    {
        string msg = UiCrashSink.CrashMessage(flushed: true);
        Assert.Contains("次回起動時に復元できます", msg);
        Assert.DoesNotContain("退避できなかった", msg);
    }

    [Fact]
    public void CrashMessage_NotFlushed_SaysMayHaveLost()
    {
        string msg = UiCrashSink.CrashMessage(flushed: false);
        Assert.Contains("退避できなかった可能性があります", msg);
        Assert.DoesNotContain("復元できます", msg);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Notify_ShowsMessageMatchingFlushResult(bool flushed)
    {
        // 三項の true/false を入れ替える変異を kill する。
        var host = new FakeHost();
        Sink(host).Notify(flushed, new InvalidOperationException("boom"));
        Assert.Equal(new[] { UiCrashSink.CrashMessage(flushed) }, host.Messages);
    }

    [Fact]
    public void Notify_DoesNotLeakExceptionTextIntoMessage()
    {
        // 例外メッセージ(パス等が混じりうる)を MessageBox に出さない契約。
        var host = new FakeHost();
        Sink(host).Notify(true, new InvalidOperationException(@"C:\secret\path\notes.txt"));
        Assert.DoesNotContain("secret", host.Messages[0]);
    }

    // ===== marshal =====

    [Fact]
    public void FlushBackups_OnUiThread_CallsHostDirectly()
    {
        var host = new FakeHost { InvokeRequired = false, FlushResult = true };
        Assert.True(Sink(host).FlushBackups());
        Assert.Equal(1, host.FlushCalls);
    }

    [Fact]
    public void FlushBackups_OnUiThread_PropagatesFalse()
    {
        // 「退避できなかった」を true に丸める変異を kill する。
        var host = new FakeHost { InvokeRequired = false, FlushResult = false };
        Assert.False(Sink(host).FlushBackups());
    }

    [Fact]
    public void FlushBackups_OffUiThread_MarshalsAndReturnsResult()
    {
        var host = new FakeHost { InvokeRequired = true, FlushResult = true };
        Assert.True(Sink(host).FlushBackups());
        Assert.Equal(1, host.FlushCalls);
    }

    [Fact]
    public void FlushBackups_NoHandle_ReturnsFalse_WithoutTouchingBackup()
    {
        // marshal 先が無い=退避できたと言い切れない。かつ BackupCoordinator に触らない
        // (UI スレッド専有クラスを別スレッドから叩かない)。
        var host = new FakeHost { CanMarshal = false, InvokeRequired = true };
        Assert.False(Sink(host).FlushBackups());
        Assert.Equal(0, host.FlushCalls);
    }

    [Fact]
    public void FlushBackups_UiThreadNeverRuns_TimesOutToFalse()
    {
        // UI スレッドが死んでいる/ブロックされている。無期限に待つと通知も終了もできず
        // プロセスが固まる=既定挙動より悪くなるので、諦めて false を返す(設計 §5.3)。
        var host = new FakeHost { InvokeRequired = true, RunPosted = false };
        var sw = Stopwatch.StartNew();
        Assert.False(Sink(host).FlushBackups());
        sw.Stop();
        Assert.Equal(0, host.FlushCalls);
        // タイムアウトを 0 にする / 待たずに返す変異と、無期限待ちの変異の両方を弁別する。
        Assert.InRange(sw.ElapsedMilliseconds, 150, 5000);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ObjectDisposedException))]
    public void FlushBackups_PostThrows_ReturnsFalse(Type exceptionType)
    {
        // ハンドル破棄との競合。ここで例外を漏らすと通知も終了もされない。
        var host = new FakeHost
        {
            InvokeRequired = true,
            PostThrows =
                exceptionType == typeof(ObjectDisposedException)
                    ? new ObjectDisposedException("form")
                    : new InvalidOperationException("handle not created"),
        };
        Assert.False(Sink(host).FlushBackups());
    }

    [Fact]
    public void FlushBackups_MarshalledFlushThrows_ReturnsFalse_WithoutEscaping()
    {
        // marshal 先で落ちても「UI スレッド上の新しい未処理例外」にしない
        // (再入ガードに握り潰されて無音で消えるため)。呼び出し側へは false で返す。
        var host = new FakeHost
        {
            InvokeRequired = true,
            FlushThrows = new InvalidOperationException("flush failed"),
        };
        Assert.False(Sink(host).FlushBackups());
    }

    [Fact]
    public void Ctor_NullHost_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new UiCrashSink(null!));
}
