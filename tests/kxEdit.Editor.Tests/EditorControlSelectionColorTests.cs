// EditorControlSelectionColorTests.cs
// 2026-09-14 設計書: 選択色がテーマ表から ViewportStyle に載ることを固定する。
// _style を直接読むアクセサは無いので、IImeOverlayHost の seam 経由で読む
// (SelectionBackColor = _style.SelectionBack / OverlayTargetForeColor = SelectionFore ?? Foreground)。
using System.Drawing;
using kxEdit.Core.Buffers;
using kxEdit.Core.Settings;

namespace kxEdit.Editor.Tests;

public class EditorControlSelectionColorTests
{
    [Theory]
    [InlineData("default", 0xADD8E6)]
    [InlineData("white-on-black", 0xFFFFFF)]
    [InlineData("yellow-on-black", 0xFFFF00)]
    [InlineData("green-on-black", 0x00FF00)]
    public void SelectionBack_follows_theme(string themeId, int expectedRgb) =>
        Sta.Run(() =>
        {
            using var f = new Form { Visible = false };
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            c.SetSource(TextBuffer.FromString("abc"));

            c.ApplyAppearance(new AppSettings { Theme = themeId });

            // 色の比較を ToArgb() に統一する理由は ImeOverlayColorTests のコメント参照。
            Assert.Equal(Opaque(expectedRgb), ((IImeOverlayHost)c).SelectionBackColor.ToArgb());
        });

    // ApplyAppearance 前(ctor 直後の DefaultStyle)は標準テーマと同じ選択背景であること。
    // DefaultStyle はテーマ表と別に固定値を持つので、表を変えたときのドリフトをここで捕まえる。
    [Fact]
    public void SelectionBack_before_ApplyAppearance_matches_default_theme() =>
        Sta.Run(() =>
        {
            using var f = new Form { Visible = false };
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;

            int expected = AppearanceThemes.ById("default").SelectionBackRgb;

            Assert.Equal(Opaque(expected), ((IImeOverlayHost)c).SelectionBackColor.ToArgb());
        });

    private static int Opaque(int rgb) =>
        Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF).ToArgb();
}
