using kxEdit.Core.Buffers;

namespace kxEdit.Editor.Tests;

/// <summary>
/// Phase 3 Task 3b で抽出した <see cref="CaretController"/> の pure ロジックテスト。
/// EditorControl 依存なし=STA 不要・HostForm 不要・buffer は
/// <see cref="TextBuffer.FromString(string)"/> のスナップショットで済ませる。
/// </summary>
public class CaretControllerTests
{
    private static TextSnapshot Snap(string s) => TextBuffer.FromString(s).Current;

    [Fact]
    public void SetTo_ClampsBelowZero_ToZero()
    {
        var c = new CaretController();
        c.SetTo(-5, Snap("hello"));
        Assert.Equal(0, c.Caret);
        Assert.Equal(0, c.Anchor);
    }

    [Fact]
    public void SetTo_ClampsAboveLength_ToLength()
    {
        var c = new CaretController();
        c.SetTo(999, Snap("hello"));
        Assert.Equal(5, c.Caret);
        Assert.Equal(5, c.Anchor);
    }

    [Fact]
    public void SetTo_ClearsAnchor()
    {
        var c = new CaretController();
        var snap = Snap("hello");
        c.SetSelection(1, 3, snap);
        Assert.True(c.HasSelection);
        c.SetTo(4, snap);
        Assert.False(c.HasSelection);
        Assert.Equal(4, c.Caret);
        Assert.Equal(4, c.Anchor);
    }

    [Fact]
    public void MoveTo_Extend_KeepsAnchor()
    {
        var c = new CaretController();
        var snap = Snap("hello");
        c.SetTo(1, snap);
        c.MoveTo(3, extend: true, snap);
        Assert.Equal(3, c.Caret);
        Assert.Equal(1, c.Anchor);
        Assert.Equal((1, 3), c.Selection);
    }

    [Fact]
    public void MoveTo_NoExtend_CollapsesAnchor()
    {
        var c = new CaretController();
        var snap = Snap("hello");
        c.SetSelection(1, 3, snap);
        c.MoveTo(4, extend: false, snap);
        Assert.False(c.HasSelection);
        Assert.Equal(4, c.Caret);
        Assert.Equal(4, c.Anchor);
    }

    [Fact]
    public void SnapAndClamp_SurrogatePair_SnapsToBoundary()
    {
        // "a😀b" = 'a' (offset 0) + high surrogate (offset 1) + low surrogate (offset 2) + 'b' (offset 3)
        var snap = Snap("a😀b");
        // 低サロゲート位置 (offset 2) は前方 (high, offset 1) にスナップされる
        Assert.Equal(1, CaretController.SnapAndClamp(2, snap));
        // high surrogate 位置はそのまま(low ではないので snap 対象外)
        Assert.Equal(1, CaretController.SnapAndClamp(1, snap));
        // 通常位置は変化なし
        Assert.Equal(0, CaretController.SnapAndClamp(0, snap));
        Assert.Equal(3, CaretController.SnapAndClamp(3, snap));
        // CharLength 位置は EOF 境界としてそのまま許可
        Assert.Equal(4, CaretController.SnapAndClamp(4, snap));
    }

    [Fact]
    public void ClearSelection_KeepsCaret()
    {
        var c = new CaretController();
        var snap = Snap("hello");
        c.SetSelection(1, 4, snap);
        Assert.Equal(4, c.Caret);
        Assert.Equal(1, c.Anchor);
        c.ClearSelection();
        Assert.Equal(4, c.Caret); // caret は保持
        Assert.Equal(4, c.Anchor); // anchor は caret に揃う
        Assert.False(c.HasSelection);
    }

    [Fact]
    public void Selection_OrderNormalized_EvenWhenAnchorAfterCaret()
    {
        var c = new CaretController();
        var snap = Snap("hello");
        // anchor > caret のケース(shift+← で作られる)
        c.SetSelection(anchor: 4, caret: 1, snap);
        Assert.Equal(1, c.Caret);
        Assert.Equal(4, c.Anchor);
        Assert.Equal((1, 4), c.Selection); // Start <= End で正規化
        Assert.True(c.HasSelection);
    }

    [Fact]
    public void DesiredXpx_RoundTrip()
    {
        var c = new CaretController();
        Assert.Equal(-1, c.DesiredXpx); // 初期値
        c.DesiredXpx = 42;
        Assert.Equal(42, c.DesiredXpx);
        c.DesiredXpx = -1;
        Assert.Equal(-1, c.DesiredXpx);
    }
}
