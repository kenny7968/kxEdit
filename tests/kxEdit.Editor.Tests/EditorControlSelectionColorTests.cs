// EditorControlSelectionColorTests.cs
// 2026-09-14 設計書: 選択色がテーマ表から ViewportStyle に載ることを固定する。
// _style を直接読むアクセサは無いので、IImeOverlayHost の seam 経由で読む
// (SelectionBackColor = _style.SelectionBack / OverlayTargetForeColor = SelectionFore ?? Foreground)。
//
// SelectionFore だけは seam 経由で読めない: OverlayTargetForeColor は
// `SelectionFore ?? Foreground` なので null 性が潰れ、標準テーマでは SelectionFore が null でも
// 黒でも同じ値になる。「標準テーマは選択文字色を持たない=描画完全不変」(設計書 §5.2)は
// 本件の目玉の約束なので、そこだけ TestHook_ViewportStyle で直接観測する。
using System.Drawing;
using kxEdit.Core.Buffers;
using kxEdit.Core.Layout;
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

    // 選択文字色はテーマ表どおりに ViewportStyle へ載ること。標準テーマの null が要点で、
    // ここが非 null に化けると標準テーマでも本文 op が分割され、「標準テーマは描画完全不変」
    // (設計書 §5.2)が崩れる。OverlayTargetForeColor 経由ではこの違いが見えない。
    [Theory]
    [InlineData("default", null)]
    [InlineData("white-on-black", 0x000000)]
    [InlineData("yellow-on-black", 0x000000)]
    [InlineData("green-on-black", 0x000000)]
    public void SelectionFore_follows_theme(string themeId, int? expectedRgb) =>
        Sta.Run(() =>
        {
            using var f = new Form { Visible = false };
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            c.SetSource(TextBuffer.FromString("abc"));

            c.ApplyAppearance(new AppSettings { Theme = themeId });

            var actual = EditorControl.TestHook_ViewportStyle(c).SelectionFore;

            if (expectedRgb is int rgb)
                Assert.Equal(new PaintColor(rgb), actual);
            else
                Assert.Null(actual);
        });

    // ApplyAppearance 前(ctor 直後の DefaultStyle)は標準テーマと同じ選択色であること。
    // DefaultStyle はテーマ表と別に固定値を持つので、表を変えたときのドリフトをここで捕まえる。
    //
    // 期待値を「表から引いた値」ではなく<b>リテラル</b>で書き、表の側を別 assert で固定する。
    // 表から引くと、将来 ctor が ApplyAppearance(既定設定)を呼ぶようになったとき「表 vs 表」の
    // 恒真式に黙って化け、DefaultStyle のリテラルを誰も見なくなる。
    [Fact]
    public void Selection_colors_before_ApplyAppearance_match_default_theme() =>
        Sta.Run(() =>
        {
            using var f = new Form { Visible = false };
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            c.SetSource(TextBuffer.FromString("abc"));

            Assert.Equal(Opaque(0xADD8E6), ((IImeOverlayHost)c).SelectionBackColor.ToArgb());
            Assert.Null(EditorControl.TestHook_ViewportStyle(c).SelectionFore);

            // 表の側。上のリテラルと食い違ったらここで落ちる。
            var defaultTheme = AppearanceThemes.ById("default");
            Assert.Equal(0xADD8E6, defaultTheme.SelectionBackRgb);
            Assert.Null(defaultTheme.SelectionForeRgb);
        });

    private static int Opaque(int rgb) =>
        Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF).ToArgb();
}
