using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

public class SaveAsSelectableEncodingsTests
{
    [Fact]
    public void HasExpectedFourEntries()
    {
        var opts = EncodingCatalog.SaveAsSelectableEncodings;
        Assert.Equal(4, opts.Count);
    }

    [Fact]
    public void FirstIsUtf8NoBom()
    {
        var e = EncodingCatalog.SaveAsSelectableEncodings[0];
        Assert.Equal(65001, e.CodePage);
        Assert.False(e.HasBom);
        Assert.Equal("UTF-8 (BOM なし)", e.DisplayName);
    }

    [Fact]
    public void SecondIsUtf8WithBom()
    {
        var e = EncodingCatalog.SaveAsSelectableEncodings[1];
        Assert.Equal(65001, e.CodePage);
        Assert.True(e.HasBom);
        Assert.Equal("UTF-8 (BOM)", e.DisplayName);
    }

    [Fact]
    public void ThirdIsShiftJisNoBom()
    {
        var e = EncodingCatalog.SaveAsSelectableEncodings[2];
        Assert.Equal(932, e.CodePage);
        Assert.False(e.HasBom);
        Assert.Equal("Shift_JIS", e.DisplayName);
    }

    [Fact]
    public void FourthIsEucJpNoBom()
    {
        var e = EncodingCatalog.SaveAsSelectableEncodings[3];
        Assert.Equal(51932, e.CodePage);
        Assert.False(e.HasBom);
        Assert.Equal("EUC-JP", e.DisplayName);
    }

    [Fact]
    public void OnlyUtf8HasBomVariant()
    {
        var opts = EncodingCatalog.SaveAsSelectableEncodings;
        int utf8Count = 0;
        int nonUtf8HasBomCount = 0;
        foreach (var o in opts)
        {
            if (o.CodePage == 65001)
                utf8Count++;
            else if (o.HasBom)
                nonUtf8HasBomCount++;
        }
        Assert.Equal(2, utf8Count);
        Assert.Equal(0, nonUtf8HasBomCount);
    }
}
