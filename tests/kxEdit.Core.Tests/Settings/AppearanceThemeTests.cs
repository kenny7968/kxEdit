using kxEdit.Core.Settings;
using Xunit;

namespace kxEdit.Core.Tests.Settings;

public class AppearanceThemeTests
{
    [Theory]
    [InlineData("default")]
    [InlineData("white-on-black")]
    [InlineData("yellow-on-black")]
    [InlineData("green-on-black")]
    public void Known_ids_resolve_to_themselves(string id) =>
        Assert.Equal(id, AppearanceThemes.ById(id).Id);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("does-not-exist")]
    public void Unknown_id_falls_back_to_default(string? id) =>
        Assert.Equal("default", AppearanceThemes.ById(id).Id);

    [Fact]
    public void All_themes_have_distinct_ids_and_rgb_in_range()
    {
        var ids = new HashSet<string>();
        foreach (var t in AppearanceThemes.All)
        {
            Assert.True(ids.Add(t.Id), $"重複 Id: {t.Id}");
            Assert.InRange(t.ForeRgb, 0x000000, 0xFFFFFF);
            Assert.InRange(t.BackRgb, 0x000000, 0xFFFFFF);
            Assert.InRange(t.SelectionBackRgb, 0x000000, 0xFFFFFF);
            if (t.SelectionForeRgb is int selFore)
                Assert.InRange(selFore, 0x000000, 0xFFFFFF);
            Assert.False(string.IsNullOrWhiteSpace(t.DisplayName));
        }
    }

    // 選択色はテーマ表に明示的に持たせる(設計書 §5.1)。値そのものを固定して、
    // 「黒地テーマの選択が水色のまま」という退行を表の時点で捕まえる。
    [Theory]
    [InlineData("default", 0xADD8E6, null)]
    [InlineData("white-on-black", 0xFFFFFF, 0x000000)]
    [InlineData("yellow-on-black", 0xFFFF00, 0x000000)]
    [InlineData("green-on-black", 0x00FF00, 0x000000)]
    public void Selection_colors_are_defined_per_theme(
        string id,
        int expectedSelBack,
        int? expectedSelFore
    )
    {
        var t = AppearanceThemes.ById(id);

        Assert.Equal(expectedSelBack, t.SelectionBackRgb);
        Assert.Equal(expectedSelFore, t.SelectionForeRgb);
    }

    // 本件の不具合(黒地テーマで選択中テキストが読めない)そのものの網。
    // 「選択中に実際に使われる文字色」と選択背景のコントラストだけを見るので、テーマを Id や
    // 地の色でカテゴリ分けせずに済む = 設計書 §5.1 が想定する将来テーマ(「濃紺地に白」のような
    // 中間的な地の色)が増えても、正しい値を入れた瞬間に落ちる、ということが起きない。
    //
    // 旧版はこれを「default 以外は SelectionForeRgb が非 null」で表現していたが、それは
    // 「Id != default ⇒ ハイコントラスト」という暗黙の概念をテストへ焼き込むもので、
    // 暗黙の概念を作らずに済むことを採用理由に挙げた設計書 §5.1 と矛盾していた(レビュー指摘)。
    //
    // しきい値は WCAG 2.x の本文テキスト基準 4.5:1。
    [Fact]
    public void Selected_text_is_readable_on_its_selection_background()
    {
        foreach (var t in AppearanceThemes.All)
        {
            // SelectionForeRgb が null なら選択範囲も本文色のまま描かれる(設計書 §5.1 の null 意味論)。
            int selectedTextRgb = t.SelectionForeRgb ?? t.ForeRgb;
            double ratio = ContrastRatio(selectedTextRgb, t.SelectionBackRgb);

            Assert.True(
                ratio >= 4.5,
                $"{t.Id}: 選択中テキストのコントラスト比が {ratio:F2}:1 で 4.5:1 未満"
            );
        }
    }

    // 選択していない本文が読めることの対。選択色を入れ替えたときに本文側を巻き込んでいないか。
    [Fact]
    public void Body_text_is_readable_on_its_background()
    {
        foreach (var t in AppearanceThemes.All)
        {
            double ratio = ContrastRatio(t.ForeRgb, t.BackRgb);

            Assert.True(ratio >= 4.5, $"{t.Id}: 本文のコントラスト比が {ratio:F2}:1 で 4.5:1 未満");
        }
    }

    /// <summary>WCAG 2.x のコントラスト比 (L1+0.05)/(L2+0.05)。1.0〜21.0。</summary>
    private static double ContrastRatio(int rgbA, int rgbB)
    {
        double la = RelativeLuminance(rgbA);
        double lb = RelativeLuminance(rgbB);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>WCAG 2.x の相対輝度(sRGB のガンマを戻してから係数を掛ける)。</summary>
    private static double RelativeLuminance(int rgb) =>
        0.2126 * Linearize((rgb >> 16) & 0xFF)
        + 0.7152 * Linearize((rgb >> 8) & 0xFF)
        + 0.0722 * Linearize(rgb & 0xFF);

    private static double Linearize(int channel)
    {
        double c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
