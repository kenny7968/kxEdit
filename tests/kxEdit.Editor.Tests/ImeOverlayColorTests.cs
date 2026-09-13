// ImeOverlayColorTests.cs
// Issue #72: IME 未確定 overlay の文字色がテーマに連動しない(Control.ForeColor 固定)不具合の網。
// 2 段で固定する:
//   (1) seam の値      : ApplyAppearance 後の OverlayForeColor がテーマ前景・OverlayTargetForeColor が黒
//   (2) Draw の呼び分け: 通常節 / 1 節扱いは OverlayForeColor、対象節は OverlayTargetForeColor で描く
// (2) は実 GDI で Bitmap に描いて画素を見る。ClearType のサブピクセル描画で厳密な色一致は
// 揺れるため、期待色に「十分近い」画素の有無で判定する(色距離のしきい値方式)。
using System.Drawing;
using kxEdit.Core.Buffers;
using kxEdit.Core.Editing;
using kxEdit.Core.Settings;
using kxEdit.Editor.Tests.Fakes;

namespace kxEdit.Editor.Tests;

public class ImeOverlayColorTests
{
    // === (1) seam の値がテーマに連動する ===

    [Theory]
    [InlineData("default", 0x000000)]
    [InlineData("white-on-black", 0xFFFFFF)]
    [InlineData("yellow-on-black", 0xFFFF00)]
    [InlineData("green-on-black", 0x00FF00)]
    public void OverlayForeColor_FollowsThemeForeground(string themeId, int expectedRgb) =>
        Sta.Run(() =>
        {
            using var f = new Form { Visible = false };
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            c.SetSource(TextBuffer.FromString("abc"));

            c.ApplyAppearance(new AppSettings { Theme = themeId });

            var expected = Color.FromArgb(
                255,
                (expectedRgb >> 16) & 0xFF,
                (expectedRgb >> 8) & 0xFF,
                expectedRgb & 0xFF
            );
            Assert.Equal(expected, ((IImeOverlayHost)c).OverlayForeColor);
        });

    // 対象節は固定水色 (0xADD8E6) の選択背景の上に描くため、テーマ色に連動させると
    // 黒地テーマで低コントラストになる(設計書 §3)。全テーマで黒のままであることを固定する。
    [Theory]
    [InlineData("default")]
    [InlineData("white-on-black")]
    [InlineData("yellow-on-black")]
    [InlineData("green-on-black")]
    public void OverlayTargetForeColor_StaysBlackForAllThemes(string themeId) =>
        Sta.Run(() =>
        {
            using var f = new Form { Visible = false };
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            c.SetSource(TextBuffer.FromString("abc"));

            c.ApplyAppearance(new AppSettings { Theme = themeId });

            Assert.Equal(Color.Black, ((IImeOverlayHost)c).OverlayTargetForeColor);
        });

    // === (2) Draw がどちらの色を使うか ===

    // 通常節(Attrs が Input)は OverlayForeColor で描く=黒地テーマで背景に溶けない。
    [Fact]
    public void Draw_NormalClause_UsesOverlayForeColor()
    {
        using var bmp = DrawOverlay(
            attrs: [ImeAttribute.Input, ImeAttribute.Input],
            clauses: [0, 2]
        );

        Assert.True(HasPixelNear(bmp, ForeMarker), "通常節が OverlayForeColor で描かれていない");
    }

    // 節境界が 2 未満のときの 1 節扱い経路も通常節と同じ色。
    [Fact]
    public void Draw_SingleClauseFallback_UsesOverlayForeColor()
    {
        using var bmp = DrawOverlay(attrs: [ImeAttribute.Input, ImeAttribute.Input], clauses: []);

        Assert.True(
            HasPixelNear(bmp, ForeMarker),
            "1 節扱い経路が OverlayForeColor で描かれていない"
        );
    }

    // 変換対象節は OverlayTargetForeColor で描く(選択背景に対するコントラスト確保)。
    [Fact]
    public void Draw_TargetClause_UsesOverlayTargetForeColor()
    {
        using var bmp = DrawOverlay(
            attrs: [ImeAttribute.TargetConverted, ImeAttribute.TargetConverted],
            clauses: [0, 2]
        );

        Assert.True(
            HasPixelNear(bmp, TargetMarker),
            "対象節が OverlayTargetForeColor で描かれていない"
        );
        Assert.False(HasPixelNear(bmp, ForeMarker), "対象節に通常節の色が使われている");
    }

    // === ヘルパ ===

    // 2 色を実運用と違う識別しやすい値にして「どちらの seam が使われたか」を画素で判別する。
    private static readonly Color ForeMarker = Color.FromArgb(255, 0, 255, 0); // 通常節
    private static readonly Color TargetMarker = Color.FromArgb(255, 255, 0, 255); // 対象節

    /// <summary>
    /// FakeImeOverlayHost + 実 GDI で overlay を Bitmap に描く。背景は黒(黒地テーマ相当)。
    /// </summary>
    private static Bitmap DrawOverlay(byte[] attrs, int[] clauses)
    {
        var host = new FakeImeOverlayHost
        {
            HasBuffer = true,
            LineHeightPx = 28,
            OverlayForeColor = ForeMarker,
            OverlayTargetForeColor = TargetMarker,
            SelectionBackColor = Color.LightBlue,
        };
        // 細い線がアンチエイリアスで期待色から離れるのを避けるため大きめのフォントで描く。
        using var font = new Font("ＭＳ ゴシック", 24f);
        using var underline = new Font(font, font.Style | FontStyle.Underline);
        using var target = new Font(font, font.Style | FontStyle.Underline | FontStyle.Bold);
        host.Font = font;
        host.UnderlineFont = underline;
        host.TargetFont = target;

        var caret = new CaretController();
        var ctrl = new ImeController(() => new FakeImeContext(), caret, host, _ => { });
        ctrl.__TestApplyComposition("あい", 2, attrs, clauses);

        var bmp = new Bitmap(400, 60);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            ctrl.Draw(g);
        }
        return bmp;
    }

    /// <summary>期待色に十分近い画素が 1 つでもあるか(ClearType の滲みを許容する)。</summary>
    private static bool HasPixelNear(Bitmap bmp, Color expected)
    {
        // 入れ子ループは波括弧を明示する。省略すると Sonar S3973(条件実行の範囲が字面から
        // 読めない)で -warnaserror ビルドが止まる(EditorControlReplaceExactTests.cs:363 と同じ轍)。
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                int dr = p.R - expected.R,
                    dg = p.G - expected.G,
                    db = p.B - expected.B;
                if (dr * dr + dg * dg + db * db <= 30 * 30)
                    return true;
            }
        }
        return false;
    }
}
