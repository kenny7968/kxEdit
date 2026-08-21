using kxEdit.Accessibility;
using kxEdit.App.Speech;
using Microsoft.Extensions.Time.Testing;
using FakeUiaTraceSink = kxEdit.App.Tests.Fakes.FakeUiaTraceSink;

namespace kxEdit.App.Tests;

/// <summary>
/// UiaAnnouncer の通知契約「視覚表示(label.Text)は無条件・発声(Raise)は非空のときだけ」と、
/// UIA 通知非対応環境でも握りつぶして視覚のみに縮退する安全性を特徴付ける。
/// Core が検証済みの文言生成は対象外(責務=Speech 層の契約)。
/// UIA-M-4 (v0.11): 50 ms throttle + trailing timer + dedupe-off の契約もここで pin する。
/// </summary>
public class AnnouncerTests
{
    /// <summary>Raise 呼び出しを記録する UiaAnnouncer 派生(実 UIA 呼び出しを抑止して契約検証用)。
    /// UIA-M-4: trailing callback の UI-thread marshaling を同期に置換し、メッセージポンプなしで観測可能にする。</summary>
    private sealed class RecordingAnnouncer : UiaAnnouncer
    {
        public List<string> Spoken { get; } = new();

        public RecordingAnnouncer(Label label, TimeProvider? clock = null)
            : base(label, clock) { }

        protected override void Raise(string message) => Spoken.Add(message);

        // trailing callback の UI-thread marshal(_label.BeginInvoke)を同期呼び出しに置き換える。
        // BeginInvoke は Label にハンドルが無い/メッセージポンプが無い STA テストでは走らないため。
        protected override void InvokeOnUiThread(Action action) => action();
    }

    // ===== UiaAnnouncer の通知契約 =====

    [Fact]
    public void Say_NonEmpty_UpdatesLabel_AndSpeaksOnce() =>
        Sta.Run(() =>
        {
            using var label = new Label();
            var announcer = new RecordingAnnouncer(label);
            announcer.Say("3 件中 1 件目");
            Assert.Equal("3 件中 1 件目", label.Text); // 視覚は無条件(晴眼/弱視も第一級)
            Assert.Equal(new[] { "3 件中 1 件目" }, announcer.Spoken);
        });

    [Fact]
    public void Say_Empty_ClearsLabel_WithoutSpeaking() =>
        Sta.Run(() =>
        {
            using var label = new Label { Text = "前回の通知" };
            var announcer = new RecordingAnnouncer(label);
            announcer.Say("");
            Assert.Equal("", label.Text); // 空=視覚クリアのみ
            Assert.Empty(announcer.Spoken); // 発声なし
        });

    [Fact]
    public void Say_Null_ClearsLabel_WithoutSpeaking() =>
        Sta.Run(() =>
        {
            using var label = new Label { Text = "前回の通知" };
            var announcer = new RecordingAnnouncer(label);
            announcer.Say(null!); // 防御(message ?? "")の特徴付け
            Assert.Equal("", label.Text);
            Assert.Empty(announcer.Spoken);
        });

    [Fact]
    public void Say_WhitespaceOnly_UpdatesLabel_AndSpeaks() =>
        Sta.Run(() =>
        {
            using var label = new Label { Text = "前回の通知" };
            var announcer = new RecordingAnnouncer(label);
            announcer.Say(" ");
            // ガードは IsNullOrEmpty であって IsNullOrWhiteSpace ではない: 空白のみは表示・発声される。
            // 空白 1 文字を「クリア扱い」に変えると SR の読み上げ挙動が変わるため、変えるなら意図的に。
            // (AnnouncerBase 畳み込み後は UiaAnnouncer.Say の IsNullOrEmpty ガードを pin する契約)。
            Assert.Equal(" ", label.Text);
            Assert.Equal(new[] { " " }, announcer.Spoken);
        });

    // ===== UiaAnnouncer の安全性 =====

    [Fact]
    public void UiaAnnouncer_Say_SetsLabelText_AndDoesNotThrow_WithoutUiaSupport() =>
        Sta.Run(() =>
        {
            using var label = new Label(); // ハンドル未生成=UIA 通知は失敗し得る環境
            var announcer = new UiaAnnouncer(label);
            announcer.Say("検索結果 3 件"); // 握りつぶし契約=例外を漏らさない
            Assert.Equal("検索結果 3 件", label.Text);
        });

    [Fact]
    public void UiaAnnouncer_Say_Empty_ClearsLabel() =>
        Sta.Run(() =>
        {
            using var label = new Label { Text = "前回の通知" };
            var announcer = new UiaAnnouncer(label);
            announcer.Say("");
            Assert.Equal("", label.Text);
        });

    // ===== UIA-M-4: 50 ms throttle + trailing timer + dedupe-off の契約 =====

    [Fact]
    public void Say_ThrottlesRaise_When50msWithinPrevious() =>
        Sta.Run(() =>
        {
            using var label = new Label();
            var clock = new FakeTimeProvider();
            var announcer = new RecordingAnnouncer(label, clock);
            announcer.Say("first"); // 初回は throttle 対象外(常に Raise される)
            clock.Advance(TimeSpan.FromMilliseconds(25)); // 50 ms 未満で 2 度目
            announcer.Say("second");
            // 直後の assertion 時点では trailing timer は未発火(進めていない)。Raise は 1 件のみ。
            Assert.Equal(new[] { "first" }, announcer.Spoken);
            // 視覚 (Label.Text) は throttle 対象外=常に最新を反映する(晴眼/弱視も第一級)。
            Assert.Equal("second", label.Text);
        });

    [Fact]
    public void Say_RaisesTrailingMessage_AfterThrottleWindow() =>
        Sta.Run(() =>
        {
            using var label = new Label();
            var clock = new FakeTimeProvider();
            var announcer = new RecordingAnnouncer(label, clock);
            announcer.Say("first"); // 即 Raise
            clock.Advance(TimeSpan.FromMilliseconds(25));
            announcer.Say("throttled"); // 25 ms=window 内 → skip + pending buffer
            // trailing timer は Say 時点 (T+25 ms) から更に 50 ms 後 (T+75 ms) に発火する。
            // T+25 ms から更に 50 ms 進めると発火し、pending の "throttled" が Raise される。
            clock.Advance(TimeSpan.FromMilliseconds(50));
            Assert.Equal(new[] { "first", "throttled" }, announcer.Spoken);
        });

    [Fact]
    public void Say_RepeatsIdenticalMessage_WhenOutsideThrottleWindow() =>
        Sta.Run(() =>
        {
            using var label = new Label();
            var clock = new FakeTimeProvider();
            var announcer = new RecordingAnnouncer(label, clock);
            announcer.Say("cell A1");
            clock.Advance(TimeSpan.FromMilliseconds(60)); // 窓外(50 ms 超)
            announcer.Say("cell A1"); // 同一メッセージだが dedupe しない契約 → Raise
            // SR で同一セルを意図的に読み直すのは基本操作。沈黙させてはならない。
            Assert.Equal(new[] { "cell A1", "cell A1" }, announcer.Spoken);
        });

    [Fact]
    public void Say_TrailingMessage_IsLastReceived_NotFirstThrottled() =>
        Sta.Run(() =>
        {
            using var label = new Label();
            var clock = new FakeTimeProvider();
            var announcer = new RecordingAnnouncer(label, clock);
            announcer.Say("a"); // 即 Raise
            clock.Advance(TimeSpan.FromMilliseconds(10));
            announcer.Say("b"); // window 内 → skip + pending=b
            clock.Advance(TimeSpan.FromMilliseconds(10));
            announcer.Say("c"); // window 内 → skip + pending=c (b を上書き=last-write-wins)
            // trailing timer は "b" 時点 (T+10 ms) にスケジュールされ、T+60 ms に発火する。
            // T+20 ms から 50 ms 進めると T+70 ms=trailing 発火済み。pending の "c" が Raise される。
            clock.Advance(TimeSpan.FromMilliseconds(50));
            Assert.Equal(new[] { "a", "c" }, announcer.Spoken);
        });

    [Fact]
    public void Say_Empty_CancelsPendingTrailingMessage() =>
        Sta.Run(() =>
        {
            using var label = new Label();
            var clock = new FakeTimeProvider(
                new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero)
            );
            var announcer = new RecordingAnnouncer(label, clock);
            announcer.Say("見つかりました"); // 即 Raise
            clock.Advance(TimeSpan.FromMilliseconds(25));
            announcer.Say("2 件目"); // 窓内=throttled/buffered → pending 保持
            clock.Advance(TimeSpan.FromMilliseconds(5));
            announcer.Say(""); // 視覚クリア=pending trailing も同時にキャンセル
            clock.Advance(TimeSpan.FromMilliseconds(100)); // trailing が発火し得た時間帯を大きく越えて進める
            // trailing は起動しないはず=pending は Say("") でクリア済み。SR/視覚 parity。
            Assert.Equal(new[] { "見つかりました" }, announcer.Spoken);
            Assert.Equal("", label.Text);
        });

    // ===== UIA-L-2 (PR-G Task 5): Raise catch の可観測化 =====

    /// <summary>UIA-L-2: <see cref="UiaAnnouncer.RaiseCore"/> が例外を投げても
    /// <see cref="UiaAnnouncer.Raise"/> の catch で <see cref="IUiaTraceSink"/> に落ちる契約を pin する。
    /// (RaiseAutomationNotification の Windows UIA インフラは opaque なため、RaiseCore seam を
    /// override して deterministically に「投げる」ケースを作る。)</summary>
    private sealed class ThrowingRaiseAnnouncer : UiaAnnouncer
    {
        public ThrowingRaiseAnnouncer(Label label, IUiaTraceSink? trace)
            : base(label, clock: null, trace: trace) { }

        // 意図的に「Raise の catch が拾わない」型 (=最終 base Exception catch に落ちる) を投げる。
        // ObjectDisposedException / InvalidOperationException は個別 catch で silent 継続する契約
        // (視覚のみに縮退) のため、trace には来ない=別テストで暗黙的に pin 済み (UiaAnnouncer_Say_...)。
        protected override void RaiseCore(string message) =>
            throw new NotSupportedException("simulated UIA notification failure");
    }

    [Fact]
    public void Say_RaiseThrowsUnexpected_TraceSinkReceivesWarning() =>
        Sta.Run(() =>
        {
            using var label = new Label();
            var trace = new FakeUiaTraceSink();
            var announcer = new ThrowingRaiseAnnouncer(label, trace);

            announcer.Say("hello"); // Raise 経路で例外 → catch → trace.Warn

            Assert.Single(trace.Warnings);
            Assert.Equal("raise-automation-notification", trace.Warnings[0].Category);
            Assert.Equal("label UIA notification failed", trace.Warnings[0].Detail);
            Assert.IsType<NotSupportedException>(trace.Warnings[0].Exception);
            // silent 継続の pin=Say 自体は例外を漏らさない (Assert.Single まで到達している時点で確認済み)。
            // 視覚 (Label.Text) は Raise が失敗しても更新されている契約 (晴眼/弱視も第一級)。
            Assert.Equal("hello", label.Text);
        });

    [Fact]
    public void Say_RaiseThrows_WithoutTraceSink_StillSwallowsSilently() =>
        Sta.Run(() =>
        {
            using var label = new Label();
            var announcer = new ThrowingRaiseAnnouncer(label, trace: null);
            // trace=null でも Say は例外を漏らさない=UIA-L-2 の後方互換性 pin
            // (既存 caller `new UiaAnnouncer(label)` 経路は trace 未指定=null で本番挙動不変)。
            announcer.Say("hello");
            Assert.Equal("hello", label.Text);
        });

    /// <summary>UIA-L-2 I-1 fixup: <see cref="UiaAnnouncer.RaiseCore"/> が
    /// <see cref="ObjectDisposedException"/> を投げる=Label 破棄直後 race の想定内経路を pin。
    /// 個別 catch (silent 継続) が拾って trace には落とさない invariant を機械固定する。</summary>
    private sealed class ObjectDisposedThrowingAnnouncer : UiaAnnouncer
    {
        public ObjectDisposedThrowingAnnouncer(Label label, IUiaTraceSink trace)
            : base(label, clock: null, trace: trace) { }

        protected override void RaiseCore(string message) =>
            throw new ObjectDisposedException("test");
    }

    [Fact]
    public void Say_RaiseThrowsObjectDisposedException_DoesNotFireTraceSink() =>
        Sta.Run(() =>
        {
            // teardown race (ObjectDisposedException) は期待済み経路=trace に載せない invariant を pin。
            // 将来の refactor で ObjectDisposedException が Exception catch に落ちて silent 継続 →
            // trace 発火に変わっても、この test が落として気づけるようにする (UIA-L-2 I-1 fixup)。
            using var label = new Label();
            var trace = new FakeUiaTraceSink();
            var announcer = new ObjectDisposedThrowingAnnouncer(label, trace);
            announcer.Say("hello");
            Assert.Empty(trace.Warnings);
        });

    [Fact]
    public void Say_ThirdCall_AfterTrailingFires_RaisesImmediately() =>
        Sta.Run(() =>
        {
            using var label = new Label();
            var clock = new FakeTimeProvider();
            var announcer = new RecordingAnnouncer(label, clock);
            // T=0: Say → Raise. T+25 ms: Say → throttled/buffered. T+75 ms: trailing 発火 → Raise。
            announcer.Say("a");
            clock.Advance(TimeSpan.FromMilliseconds(25));
            announcer.Say("b");
            clock.Advance(TimeSpan.FromMilliseconds(50)); // T+75 ms=trailing 発火 (Raise "b")
            // trailing 発火直後 (T+75 ms) では _lastSaidUtc も T+75 ms 相当まで進んでいる。
            // 更に 50 ms 以上進めれば次の Say は throttle 対象外で即 Raise される契約を pin する。
            clock.Advance(TimeSpan.FromMilliseconds(60));
            announcer.Say("c");
            Assert.Equal(new[] { "a", "b", "c" }, announcer.Spoken);
        });
}
