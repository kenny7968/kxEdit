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
            // Color.Equals は ARGB だけでなく known color 同一性も見る (Color.Black.Equals(
            // Color.FromArgb(255, 0, 0, 0)) は false)。実装が挙動同一のまま返し方を変えたときに
            // 偽の赤を出さないよう、色の比較は常に ToArgb() で行う。
            Assert.Equal(expected.ToArgb(), ((IImeOverlayHost)c).OverlayForeColor.ToArgb());
        });

    // 対象節は選択背景 (_style.SelectionBack) の上に描くため、文字色は選択中テキストと
    // 同じ色 (_style.SelectionFore ?? _style.Foreground) を使う。2026-09-14 時点のテーマ表では
    // 4 テーマとも結果が黒になり、#74 の固定黒から挙動は変わらない(設計書 §5.3)。
    // 全テーマで黒のままであることを固定する。
    //
    // no-change テストなので「テーマが確かに効いている」アンカーを同居させる (CLAUDE.md §4-B)。
    // 実装が Color.Black リテラルである以上、アンカーが無いと ApplyAppearance の行を丸ごと
    // 削っても 4 ケース緑のままで、このテストは何も主張しなくなる。
    // themeForeIsNonBlack: default テーマは本文前景自体が黒なのでアンカーの対象外にする。
    [Theory]
    [InlineData("default", false)]
    [InlineData("white-on-black", true)]
    [InlineData("yellow-on-black", true)]
    [InlineData("green-on-black", true)]
    public void OverlayTargetForeColor_StaysBlackForAllThemes(
        string themeId,
        bool themeForeIsNonBlack
    ) =>
        Sta.Run(() =>
        {
            using var f = new Form { Visible = false };
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            c.SetSource(TextBuffer.FromString("abc"));

            c.ApplyAppearance(new AppSettings { Theme = themeId });

            var host = (IImeOverlayHost)c;
            // 比較を ToArgb() に統一する理由は OverlayForeColor_FollowsThemeForeground のコメント参照。
            Assert.Equal(Color.Black.ToArgb(), host.OverlayTargetForeColor.ToArgb());

            // アンカー: 同じ ApplyAppearance がもう一方の seam は確かに動かしている。
            if (themeForeIsNonBlack)
                Assert.NotEqual(Color.Black.ToArgb(), host.OverlayForeColor.ToArgb());
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
        // 設計書 §3 の前提(対象節は選択背景の上に描く=だから前景は黒を維持する)自体の網。
        // これが無いと FillRectangle が消えても緑のままで、前提が崩れたことに気づけない。
        Assert.True(HasPixelNear(bmp, Color.LightBlue), "対象節の選択背景が塗られていない");
        // 対象節だけの bitmap に通常節の色が混ざらないことの念押し(不変条件の明文化)。
        // 単独でこれだけが落ちる実装ミスは構成しにくく、「2 色が同値」を実際に捕まえているのは
        // OverlayTargetForeColor_StaysBlackForAllThemes の方であることに注意。
        Assert.False(HasPixelNear(bmp, ForeMarker), "対象節に通常節の色が使われている");
    }

    /// <summary>
    /// Issue #72 の実症状そのものの構成: 1 つの未確定文字列に通常節と対象節が混在し、
    /// 「対象節は見えるのに通常節だけ消える」状態。<c>ImeController.Draw</c> のループが
    /// 2 回以上回るのはこのケースだけなので、
    /// (a) 節ごとに <c>_ime.Attrs[s]</c> を引き直して色を選んでいること(節先頭固定の
    ///     <c>Attrs[0]</c> に退化していないこと)と、
    /// (b) 節を描くたびに <c>curX</c> を進めて 2 節目を別位置に置いていること
    /// をまとめて固定する。節が 1 個の 3 テストではこの 2 点は網に掛からない。
    /// </summary>
    [Fact]
    public void Draw_MixedNormalAndTargetClauses_UsesBothColors()
    {
        using var bmp = DrawOverlay(
            attrs: [ImeAttribute.Input, ImeAttribute.TargetConverted],
            clauses: [0, 1, 2]
        );

        Assert.True(
            HasPixelNear(bmp, ForeMarker),
            "混在時に通常節が OverlayForeColor で描かれていない"
        );
        Assert.True(
            HasPixelNear(bmp, TargetMarker),
            "混在時に対象節が OverlayTargetForeColor で描かれていない"
        );
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
        // 検証対象は「どちらの色 seam が使われたか」であって字形ではないので、本文は ASCII にする。
        // 和文文字にすると、和文グリフを持つフォントが無い環境 (CI の英語ロケール windows ランナー)
        // で何も描かれず偽の赤になる。2 文字なのは clauses を [0,1,2] に割れるようにするため。
        ctrl.__TestApplyComposition("Wm", 2, attrs, clauses);

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
