// ImeControllerTests.cs
// Phase 3 (Task 3a) の ImeController pure テスト。FakeImeContext + FakeImeOverlayHost で
// state 遷移と context 呼び出しを検証する (実 IME / GDI 不要)。
using kxEdit.Core.Buffers;
using kxEdit.Core.Editing;
using kxEdit.Editor.Abstractions;
using kxEdit.Editor.Tests.Fakes;

namespace kxEdit.Editor.Tests;

public class ImeControllerTests
{
    // === OnStartComposition ===

    [Fact]
    public void OnStartComposition_WhenCanCompose_InitializesImeStart_AndCallsHost()
    {
        var host = new FakeImeOverlayHost { CanImeCompose = true };
        var caret = new CaretController();
        caret.SetTo(3, TextBuffer.FromString("abcdef").Current); // Caret=3
        var ctx = new FakeImeContext();
        var ctrl = new ImeController(() => ctx, caret, host, _ => { });

        ctrl.OnStartComposition();

        Assert.Equal(3, ctrl.State.Start); // _ime.Start = Caret
        Assert.False(ctrl.IsActive); // Text 空 = IsActive false
        Assert.Equal(1, host.DeleteSelectionCallCount);
        Assert.Equal(1, host.PositionCaretCallCount);
        Assert.Equal(1, host.InvalidateCallCount);
    }

    [Fact]
    public void OnStartComposition_WhenCannotCompose_IsNoOp()
    {
        var host = new FakeImeOverlayHost { CanImeCompose = false };
        var caret = new CaretController();
        var ctx = new FakeImeContext();
        var ctrl = new ImeController(() => ctx, caret, host, _ => { });

        ctrl.OnStartComposition();

        Assert.Equal(0, host.DeleteSelectionCallCount);
        Assert.Equal(0, host.PositionCaretCallCount);
        Assert.Equal(0, host.InvalidateCallCount);
        Assert.False(ctrl.IsActive);
    }

    // === OnComposition ===

    [Fact]
    public void OnComposition_GcsResultStr_CallsInsertConfirmedText_AndClearsState()
    {
        var host = new FakeImeOverlayHost();
        var caret = new CaretController();
        var ctx = new FakeImeContext();
        ctx.Strings[NativeMethods.GCS_RESULTSTR] = "漢字";
        var inserted = new System.Text.StringBuilder();
        var ctrl = new ImeController(() => ctx, caret, host, s => inserted.Append(s));

        ctrl.OnComposition(NativeMethods.GCS_RESULTSTR);

        Assert.Equal("漢字", inserted.ToString());
        Assert.False(ctrl.IsActive); // ApplyResult で Empty へ
        Assert.True(host.InvalidateCallCount >= 1);
    }

    [Fact]
    public void OnComposition_GcsCompStr_UpdatesState_AndSetsStartToCaret()
    {
        var host = new FakeImeOverlayHost();
        var caret = new CaretController();
        caret.SetTo(2, TextBuffer.FromString("abcdef").Current);
        var ctx = new FakeImeContext();
        ctx.Strings[NativeMethods.GCS_COMPSTR] = "あい";
        ctx.Ints[NativeMethods.GCS_CURSORPOS] = 2;
        var ctrl = new ImeController(() => ctx, caret, host, _ => { });

        ctrl.OnComposition(NativeMethods.GCS_COMPSTR | NativeMethods.GCS_CURSORPOS);

        Assert.True(ctrl.IsActive);
        Assert.Equal("あい", ctrl.State.Text);
        Assert.Equal(2, ctrl.State.CursorPos);
        Assert.Equal(2, ctrl.State.Start); // 未 Active → _caret.Caret から取り直し
    }

    [Fact]
    public void OnComposition_NullContextStrings_UsesEmpty()
    {
        var host = new FakeImeOverlayHost();
        var caret = new CaretController();
        var ctx = new FakeImeContext();
        // Strings/Bytes は未設定 = null 返却 = ?? "" / [] で空扱い
        var inserted = new System.Text.StringBuilder();
        var ctrl = new ImeController(() => ctx, caret, host, s => inserted.Append(s));

        ctrl.OnComposition(NativeMethods.GCS_RESULTSTR);

        Assert.Equal("", inserted.ToString()); // 空文字列 = InsertConfirmedText 呼ばれず (ApplyResult 内で length > 0 ガード)
        Assert.False(ctrl.IsActive);
    }

    [Fact]
    public void OnComposition_ImeContextUnavailable_IsFullyNoOp_AndPreservesActiveState()
    {
        // IMP-1 fixup regression: himc==0 (IsAvailable=false) 時は元 EditorControl.Ime.cs:122 の早期 return と等価。
        // 事前に active な _ime state を組んでから IsAvailable=false に切替→OnComposition→
        // state 保持 (Empty へ上書きされない) + host 副作用 (PositionCaret/Invalidate) 発生なし + confirmed 挿入なし。
        var ctx = new FakeImeContext();
        var host = new FakeImeOverlayHost();
        var caret = new CaretController();
        var confirmed = new List<string>();
        var ctrl = new ImeController(() => ctx, caret, host, confirmed.Add);

        // 事前 active 化 (__TestApplyComposition で直接 state を組む=context 消費なし)
        ctrl.__TestApplyComposition("あい", 2, new byte[] { 0, 0 }, new int[] { 0, 2 });
        Assert.True(ctrl.IsActive);

        // baseline カウンタ (事前 active 化で +1 発生している分をゼロ点にする)
        int posBefore = host.PositionCaretCallCount;
        int invBefore = host.InvalidateCallCount;

        // IME 無効化して OnComposition 発火
        ctx.IsAvailable = false;
        ctrl.OnComposition(NativeMethods.GCS_COMPSTR | NativeMethods.GCS_RESULTSTR);

        // 元 hIMC == IntPtr.Zero 早期 return と等価挙動:
        Assert.True(ctrl.IsActive); // state 保持 (Empty に潰されない)
        Assert.Equal("あい", ctrl.State.Text); // Text 保持
        Assert.Equal(posBefore, host.PositionCaretCallCount); // PositionCaret 発火なし
        Assert.Equal(invBefore, host.InvalidateCallCount); // Invalidate 発火なし
        Assert.Empty(confirmed); // ApplyResult 経路も発火なし
    }

    [Fact]
    public void OnComposition_WhenCannotCompose_IsNoOp_AndDoesNotCreateContext()
    {
        var host = new FakeImeOverlayHost { CanImeCompose = false };
        var caret = new CaretController();
        int ctxCount = 0;
        var ctrl = new ImeController(
            () =>
            {
                ctxCount++;
                return new FakeImeContext();
            },
            caret,
            host,
            _ => { }
        );

        ctrl.OnComposition(NativeMethods.GCS_COMPSTR);

        Assert.Equal(0, ctxCount); // ガードで factory 呼ばれず
        Assert.False(ctrl.IsActive);
    }

    // === Cancel / Complete ===

    [Fact]
    public void Cancel_WhenActive_CallsContextCancel_AndResetsState()
    {
        var host = new FakeImeOverlayHost();
        var caret = new CaretController();
        var ctx = new FakeImeContext();
        var ctrl = new ImeController(() => ctx, caret, host, _ => { });
        // まず active 化
        ctx.Strings[NativeMethods.GCS_COMPSTR] = "あ";
        ctrl.OnComposition(NativeMethods.GCS_COMPSTR);
        Assert.True(ctrl.IsActive);
        int invalidateBefore = host.InvalidateCallCount;

        ctrl.Cancel();

        Assert.True(ctx.CancelCalled);
        Assert.False(ctrl.IsActive);
        Assert.Equal(invalidateBefore + 1, host.InvalidateCallCount);
        Assert.True(ctx.Disposed); // using で必ず Dispose
    }

    [Fact]
    public void Cancel_WhenNotActive_IsNoOp()
    {
        var host = new FakeImeOverlayHost();
        var caret = new CaretController();
        int ctxCount = 0;
        var ctrl = new ImeController(
            () =>
            {
                ctxCount++;
                return new FakeImeContext();
            },
            caret,
            host,
            _ => { }
        );

        ctrl.Cancel();

        Assert.Equal(0, ctxCount); // ガードで factory 呼ばれず
        Assert.Equal(0, host.InvalidateCallCount);
    }

    [Fact]
    public void Complete_WhenActive_CallsContextComplete_AndResetsState()
    {
        var host = new FakeImeOverlayHost();
        var caret = new CaretController();
        var ctx = new FakeImeContext();
        var ctrl = new ImeController(() => ctx, caret, host, _ => { });
        ctx.Strings[NativeMethods.GCS_COMPSTR] = "い";
        ctrl.OnComposition(NativeMethods.GCS_COMPSTR);
        Assert.True(ctrl.IsActive);

        ctrl.Complete();

        Assert.True(ctx.CompleteCalled);
        Assert.False(ctx.CancelCalled); // Cancel と混同しない
        Assert.False(ctrl.IsActive);
    }

    // === OnEndComposition / MaskSetContextLParam ===

    [Fact]
    public void OnEndComposition_ResetsState()
    {
        var host = new FakeImeOverlayHost();
        var caret = new CaretController();
        var ctx = new FakeImeContext();
        var ctrl = new ImeController(() => ctx, caret, host, _ => { });
        ctx.Strings[NativeMethods.GCS_COMPSTR] = "う";
        ctrl.OnComposition(NativeMethods.GCS_COMPSTR);
        Assert.True(ctrl.IsActive);

        ctrl.OnEndComposition();

        Assert.False(ctrl.IsActive);
    }

    // === Dispose (Func<IImeContext> factory は毎回 new / Dispose される) ===

    [Fact]
    public void Cancel_UsesFactoryAndDisposesEachCall()
    {
        var host = new FakeImeOverlayHost();
        var caret = new CaretController();
        var contexts = new List<FakeImeContext>();
        var ctrl = new ImeController(
            () =>
            {
                var c = new FakeImeContext();
                contexts.Add(c);
                return c;
            },
            caret,
            host,
            _ => { }
        );

        // active 化のためだけに直接 __TestApplyComposition を叩く (context 消費なし)
        ctrl.__TestApplyComposition("え", 1, new byte[] { 0 }, new int[] { 0, 1 });
        Assert.True(ctrl.IsActive);
        Assert.Empty(contexts);

        // Cancel → factory 1 回消費 + Dispose
        ctrl.Cancel();
        Assert.Single(contexts);
        Assert.True(contexts[0].Disposed);
    }
}
