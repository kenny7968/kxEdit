using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

public class EastAsianWidthTests
{
    [Theory]
    [InlineData('A', 1)] // ASCII
    [InlineData('0', 1)]
    [InlineData('あ', 2)] // ひらがな
    [InlineData('漢', 2)] // 漢字
    [InlineData('Ａ', 2)] // 全角英字
    [InlineData('。', 2)] // 全角句読点
    [InlineData('ｱ', 1)] // 半角カタカナ
    [InlineData('　', 2)] // 全角スペース(U+3000)
    [InlineData(' ', 1)] // 半角スペース
    public void ColumnWidth_basic(char c, int expected) =>
        Assert.Equal(expected, EastAsianWidth.ColumnWidth(c));

    [Fact]
    public void ColumnWidth_combining_is_zero() =>
        Assert.Equal(0, EastAsianWidth.ColumnWidth(0x0301)); // 結合アクセント

    [Fact]
    public void ColumnWidth_astral_cjk_is_two() =>
        Assert.Equal(2, EastAsianWidth.ColumnWidth(0x20000)); // 拡張B
}
