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
            Assert.False(string.IsNullOrWhiteSpace(t.DisplayName));
        }
    }
}
