// UiaTextHostAdapterTests.cs
// Phase 3 Task 3d の pure テスト: Adapter の内部契約 (通知経路 + field 更新) に絞る。
// RPC スレッド境界のテストは既存 EditorControlUiaHostTests (305 行) が担う (Adapter 経由に
// 自動で切り替わり=EditorControl の public API 契約が同じため)。
//
// 対象契約:
//   - OnSnapshotChanged が _bufferSnapshot 更新 + _lastLineSegs 破棄を 1 経路で実施 (元 6 箇所の統一)
//   - GetSelection が Task 3b Obs 4 復旧の 2 field 読み (local capture) で torn-read 窓を最小化
//   - TextLength / GetTextRange が _bufferSnapshot に応答 (null/非 null 分岐)
//   - RaiseTextChanged / RaiseSelectionChanged / RaiseFocusChanged が _provider null で no-op
//   - RaiseXxx が _provider 生成後にカウンタを増やす (TestHook_ForceUiaListen ON 前提)
//   - OnHandleDestroyed が _hwnd を Zero に戻す (bit-perfect=_provider は触らない)
using System.Reflection;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Forms;
using kxEdit.Accessibility;
using kxEdit.Core.Buffers;
using kxEdit.Editor;
using kxEdit.Editor.Tests.Fakes;

namespace kxEdit.Editor.Tests;

public class UiaTextHostAdapterTests
{
    // ---------- OnSnapshotChanged (通知経路の統一) ----------

    [Fact]
    public void OnSnapshotChanged_InvalidatesLastLineSegs_AndUpdatesBufferSnapshot() =>
        Sta.Run(() =>
        {
            using var f = new HostForm();
            using var c = new EditorControl { WrapColumns = 20 };
            f.Controls.Add(c);
            _ = f.Handle;
            c.SetSource(TextBuffer.FromString("Hello, world!"));

            // Prime cache: LineStartOf を呼んで _lastLineSegs を埋める
            var host = (IUiaTextHost)c;
            _ = host.LineStartOf(5);
            _ = host.LineStartOf(5);
            c.TestHook_ResetLastLineSegsCounters();

            // ReplaceCharRange 経由の編集 (AfterEdit → _uia.OnSnapshotChanged)。
            // 直後の LineStartOf は cache miss になる=OnSnapshotChanged が _lastLineSegs を破棄した証拠。
            c.ReplaceCharRange(0, 0, "X");
            _ = host.LineStartOf(5);
            Assert.Equal(1, c.TestHook_LastLineSegsMissCount);
            Assert.Equal(0, c.TestHook_LastLineSegsHitCount);
        });

    // ---------- GetSelection (Task 3b Obs 4 復旧) ----------

    [Fact]
    public void GetSelection_ReturnsMinMaxOfCaretAndAnchor() =>
        Sta.Run(() =>
        {
            using var f = new HostForm();
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            c.SetSource(TextBuffer.FromString("abcdefg"));

            // Task 3b Obs 4 復旧: caret / anchor を local capture して Min/Max
            // → shift+左方向 (anchor > caret) でも (min, max) = (caret, anchor) が返る
            c.SetSelectionAnchored(anchor: 5, caret: 2);
            Assert.Equal((2, 5), ((IUiaTextHost)c).GetSelection());

            // shift+右方向 (anchor < caret) でも同じ結果 (Start < End 契約)
            c.SetSelectionAnchored(anchor: 1, caret: 6);
            Assert.Equal((1, 6), ((IUiaTextHost)c).GetSelection());
        });

    // ---------- TextLength / GetTextRange (_bufferSnapshot 参照契約) ----------

    [Fact]
    public void TextLength_UsesSnapshotCharCount() =>
        Sta.Run(() =>
        {
            using var c = new EditorControl();
            // SetSource 前: _bufferSnapshot=null → TextLength=0 (元コードと bit-perfect)
            Assert.Equal(0, ((IUiaTextHost)c).TextLength);

            c.SetSource(TextBuffer.FromString("hello"));
            Assert.Equal(5, ((IUiaTextHost)c).TextLength);
        });

    [Fact]
    public void GetTextRange_ClampsToSnapshotBounds() =>
        Sta.Run(() =>
        {
            using var c = new EditorControl();
            // SetSource 前: 空文字返却 (bit-perfect)
            Assert.Equal("", ((IUiaTextHost)c).GetTextRange(0, 100));

            c.SetSource(TextBuffer.FromString("hello"));
            Assert.Equal("hello", ((IUiaTextHost)c).GetTextRange(0, 100)); // length clamp
            Assert.Equal("hel", ((IUiaTextHost)c).GetTextRange(0, 3));
        });

    // ---------- RaiseXxx (provider null で no-op / provider 有りでカウント) ----------

    [Fact]
    public void RaiseTextChanged_NullProvider_IsNoOp() =>
        Sta.Run(() =>
        {
            // WM_GETOBJECT 未受信=_provider が null=RaiseUia は早期 return するはず。
            // カウンタは 0 のまま (テストが緑ならそう)。
            EditorControl.TestHook_ForceUiaListen = true;
            try
            {
                using var c = new EditorControl();
                c.SetSource(TextBuffer.FromString("hi"));
                EditorControl.TestHook_ResetUiaEventCounts(c);

                // 編集経路で RaiseTextChanged / RaiseSelectionChanged が呼ばれるが _provider=null で no-op
                c.ReplaceCharRange(0, 0, "X");

                var (textChanged, selChanged, focusChanged) = EditorControl.TestHook_UiaEventCounts(
                    c
                );
                Assert.Equal(0, textChanged);
                Assert.Equal(0, selChanged);
                Assert.Equal(0, focusChanged);
            }
            finally
            {
                EditorControl.TestHook_ForceUiaListen = false;
            }
        });

    // ---------- UIA-L-2: RaiseUia catch の可観測化 (trace 経路) ----------

    /// <summary>UIA-L-2: <see cref="UiaTextHostAdapter.PerformRaiseAutomationEvent"/> を override して
    /// 「AutomationInteropProvider.RaiseAutomationEvent が投げた」状況を deterministically に再現する
    /// テスト seam (Windows UIA インフラは opaque なため本物の失敗を作れない)。
    /// UIA-L-2 で sealed を外した=Editor.Tests から subclass できる (InternalsVisibleTo 経由)。</summary>
    private sealed class ThrowingAdapter : UiaTextHostAdapter
    {
        public ThrowingAdapter(EditorControl host, CaretController caret, IUiaTraceSink trace)
            : base(host, caret, trace) { }

        protected internal override void PerformRaiseAutomationEvent(
            AutomationEvent ev,
            IRawElementProviderSimple provider,
            AutomationEventArgs args
        ) => throw new NotSupportedException("simulated UIA RaiseAutomationEvent failure");
    }

    [Fact]
    public void RaiseUia_WhenPerformThrows_TraceSinkReceivesWarning_PerEvent() =>
        Sta.Run(() =>
        {
            using var f = new HostForm();
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            c.SetSource(TextBuffer.FromString("hi"));

            // 既存 EditorControl の CaretController を借りて subclass に渡す (Editor.Tests は
            // InternalsVisibleTo=CaretController 直参照可)。
            var caretField = typeof(EditorControl).GetField(
                "_caretCtrl",
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;
            var caret = (CaretController)caretField.GetValue(c)!;

            var trace = new FakeUiaTraceSink();
            var adapter = new ThrowingAdapter(c, caret, trace);

            // _provider != null を作らないと RaiseUia は早期 return する。
            // TextControlProviderV2 は public=Accessibility から直接 new できる。
            var providerField = typeof(UiaTextHostAdapter).GetField(
                "_provider",
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;
            providerField.SetValue(adapter, new TextControlProviderV2(adapter));

            EditorControl.TestHook_ForceUiaListen = true;
            try
            {
                adapter.RaiseTextChanged();
                adapter.RaiseSelectionChanged();
                adapter.RaiseFocusChanged();
            }
            finally
            {
                EditorControl.TestHook_ForceUiaListen = false;
            }

            // 3 発全てで trace が発火する=RaiseUia の catch が Warn を呼んでいる証拠。
            Assert.Equal(3, trace.Warnings.Count);
            Assert.All(
                trace.Warnings,
                w =>
                {
                    Assert.Equal("raise-automation-event", w.Category);
                    Assert.IsType<NotSupportedException>(w.Exception);
                    Assert.False(string.IsNullOrEmpty(w.Detail)); // event 名 or ID が入る
                }
            );

            // カウンタは try 内で increment されるため、例外で to catch へ抜けた場合は上がらない。
            // これで「Perform... 呼出成功時のみ counter++」の順序契約も暗黙的に pin する。
            Assert.Equal((0, 0, 0), adapter.UiaEventCounts);
        });

    [Fact]
    public void RaiseUia_WhenPerformThrows_WithoutTraceSink_StillSwallowsSilently() =>
        Sta.Run(() =>
        {
            using var f = new HostForm();
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            c.SetSource(TextBuffer.FromString("hi"));

            var caretField = typeof(EditorControl).GetField(
                "_caretCtrl",
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;
            var caret = (CaretController)caretField.GetValue(c)!;

            // trace=null で subclass 経由 (base ctor の optional param を null 明示)
            var adapter = new NullTraceThrowingAdapter(c, caret);

            var providerField = typeof(UiaTextHostAdapter).GetField(
                "_provider",
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;
            providerField.SetValue(adapter, new TextControlProviderV2(adapter));

            EditorControl.TestHook_ForceUiaListen = true;
            try
            {
                // trace=null でも例外を漏らさない=UIA-L-2 後方互換 pin
                // (既存 caller `new UiaTextHostAdapter(this, _caretCtrl)` 経路の本番挙動不変)。
                // 例外が漏れれば xUnit がテスト失敗として拾うため、明示的な Assert なしでも契約は
                // pin される。それだと sonar S2699 が引っ掛かるので、silent 継続の副作用も併せて assert する
                // (counter は Perform... 例外で increment されない=元 catch 契約と一致)。
                adapter.RaiseTextChanged();
                adapter.RaiseSelectionChanged();
                adapter.RaiseFocusChanged();
                Assert.Equal((0, 0, 0), adapter.UiaEventCounts);
            }
            finally
            {
                EditorControl.TestHook_ForceUiaListen = false;
            }
        });

    /// <summary>UIA-L-2: trace=null 経路の subclass。</summary>
    private sealed class NullTraceThrowingAdapter : UiaTextHostAdapter
    {
        public NullTraceThrowingAdapter(EditorControl host, CaretController caret)
            : base(host, caret, trace: null) { }

        protected internal override void PerformRaiseAutomationEvent(
            AutomationEvent ev,
            IRawElementProviderSimple provider,
            AutomationEventArgs args
        ) => throw new NotSupportedException("simulated UIA failure");
    }

    // ---------- OnHandleDestroyed (bit-perfect: _hwnd を Zero に戻す・_provider は触らない) ----------

    [Fact]
    public void OnHandleDestroyed_SetsHwndToZero_ButKeepsProvider() =>
        Sta.Run(() =>
        {
            // 元コードでは OnHandleDestroyed は _hwnd = IntPtr.Zero のみ (provider は触らない)。
            // これを reflection で機械固定する (Adapter のフィールドを直接観測)。
            using var f = new HostForm();
            var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            _ = c.Handle; // Handle 生成→OnHandleCreated が動く→_hwnd がセットされる

            var adapterType = typeof(EditorControl).Assembly.GetType(
                "kxEdit.Editor.UiaTextHostAdapter"
            )!;
            var uiaField = typeof(EditorControl).GetField(
                "_uia",
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;
            var adapter = uiaField.GetValue(c)!;

            var hwndField = adapterType.GetField(
                "_hwnd",
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;
            var providerField = adapterType.GetField(
                "_provider",
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;

            // Handle 生成後: _hwnd 非ゼロ、_provider は WM_GETOBJECT 未受信で null
            Assert.NotEqual(IntPtr.Zero, (nint)hwndField.GetValue(adapter)!);
            Assert.Null(providerField.GetValue(adapter));

            // Handle 破棄後: _hwnd=Zero に戻る、_provider は null のまま (元コード=触らない)
            c.Dispose();
            Assert.Equal(IntPtr.Zero, (nint)hwndField.GetValue(adapter)!);
            Assert.Null(providerField.GetValue(adapter));
        });
}
