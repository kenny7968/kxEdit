// FakeImeOverlayHost.cs
// Phase 3 (Task 3a) で ImeController の pure テスト用に IImeOverlayHost を差し替える fake。
// state 系 (CanImeCompose/HasBuffer/HasFocus) は set 可能・副作用系は呼び出し回数を記録。
// Draw に必要な Font/Color も個別に差し替え可能 (ImeOverlayColorTests は本 fake に識別しやすい色を
// 差し込み、実 GDI で Bitmap に描いて画素からどちらの色 seam が使われたかを検証する)。
using System.Drawing;

namespace kxEdit.Editor.Tests.Fakes;

internal sealed class FakeImeOverlayHost : IImeOverlayHost
{
    public bool CanImeCompose { get; set; } = true;
    public int DeleteSelectionCallCount { get; private set; }
    public int PositionCaretCallCount { get; private set; }
    public int InvalidateCallCount { get; private set; }
    public bool HasBuffer { get; set; } = true;
    public bool HasFocus { get; set; } = true;
    public int ScrollX { get; set; }
    public int LineHeightPx { get; set; } = 20;

    // IMP-2 fixup: Metrics プロパティは interface から削除 (使用 0 件)。
    public Func<int, (int X, int Y, bool Visible)>? CaretPointResolver { get; set; }
    public Font Font { get; set; } = SystemFonts.DefaultFont;
    public Font UnderlineFont { get; set; } = SystemFonts.DefaultFont;
    public Font TargetFont { get; set; } = SystemFonts.DefaultFont;
    public Color OverlayForeColor { get; set; } = Color.Black;
    public Color OverlayTargetForeColor { get; set; } = Color.Black;
    public Color SelectionBackColor { get; set; } = Color.LightBlue;

    public void DeleteSelectionForImeStart() => DeleteSelectionCallCount++;

    public void PositionCaret() => PositionCaretCallCount++;

    public void Invalidate() => InvalidateCallCount++;

    public (int X, int Y, bool Visible) ComputeCaretPoint(int offset) =>
        CaretPointResolver?.Invoke(offset) ?? (0, 0, true);
}
