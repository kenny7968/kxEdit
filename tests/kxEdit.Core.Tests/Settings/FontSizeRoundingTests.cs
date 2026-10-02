using kxEdit.Core.Settings;
using Xunit;

namespace kxEdit.Core.Tests.Settings;

/// <summary>
/// フォントダイアログが返した大きさを 0.5pt 単位に丸める規則(2 倍して ToEven で整数に丸め、2 で割る)。
/// 中点のケースと中点でないケースを両方入れる(中点でない値だけでは AwayFromZero や切り捨てと区別できない)。
/// </summary>
public class FontSizeRoundingTests
{
    [Theory]
    [InlineData(20.25f, 20f)] // 96 DPI の 27px。中点 → 偶数(整数 pt)。AwayFromZero なら 20.5
    [InlineData(11.25f, 11f)] // 15px。AwayFromZero なら 11.5
    [InlineData(9.75f, 10f)] // 13px。切り捨てなら 9.5
    [InlineData(12.75f, 13f)] // 17px
    [InlineData(10.5f, 10.5f)] // 14px ちょうど。中点でないので残る
    [InlineData(12f, 12f)]
    [InlineData(19.8f, 20f)] // 120 DPI の 33px。2 進で割り切れない値
    public void ToHalfPoint_rounds_to_half_point_with_midpoint_to_even(
        float input,
        float expected
    ) => Assert.Equal(expected, FontSizeRounding.ToHalfPoint(input));

    [Theory]
    [InlineData(0.25f)] // 288 DPI の 1px。2 倍の中点 0.5 が ToEven で 0 になる
    [InlineData(0.1f)]
    public void ToHalfPoint_never_returns_below_half_point(float input) =>
        Assert.Equal(0.5f, FontSizeRounding.ToHalfPoint(input));
}
