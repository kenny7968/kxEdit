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

    // ハイコントラスト(黒地)テーマだけが選択文字色を持つ、という方針そのものの網。
    // 標準テーマに文字色が入ると「標準テーマは描画不変」という不変条件(設計書 §5.2)が崩れる。
    [Fact]
    public void Only_non_default_themes_specify_selection_foreground()
    {
        foreach (var t in AppearanceThemes.All)
        {
            if (t.Id == "default")
                Assert.Null(t.SelectionForeRgb);
            else
                Assert.NotNull(t.SelectionForeRgb);
        }
    }
}
