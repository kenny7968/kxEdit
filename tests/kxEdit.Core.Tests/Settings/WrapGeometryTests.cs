using kxEdit.Core.Settings;
using Xunit;

namespace kxEdit.Core.Tests.Settings;

public class WrapGeometryTests
{
    [Theory]
    [InlineData(80, 8, 640)]
    [InlineData(40, 7, 280)]
    [InlineData(10, 10, 100)]
    public void TargetWidthPx_is_columns_times_halfwidth(
        int columns,
        int halfWidth,
        int expected
    ) => Assert.Equal(expected, WrapGeometry.TargetWidthPx(columns, halfWidth));

    [Theory]
    [InlineData(1000, 640, 360)] // 広い: 余剰を右マージンへ
    [InlineData(640, 640, 0)] // ぴったり: マージン0
    [InlineData(300, 640, 0)] // 狭い: 負にならず0（ウィンドウ幅で折る）
    public void RightMargin_is_nonnegative_surplus(int textAreaPx, int targetPx, int expected) =>
        Assert.Equal(expected, WrapGeometry.RightMargin(textAreaPx, targetPx));

    [Theory]
    [InlineData(80, 80)]
    [InlineData(10, 10)] // 下限境界
    [InlineData(1000, 1000)] // 上限境界
    [InlineData(5, 10)] // 下限クランプ
    [InlineData(0, 10)]
    [InlineData(-3, 10)]
    [InlineData(99999, 1000)] // 上限クランプ
    public void ClampColumns_bounds_to_10_1000(int input, int expected) =>
        Assert.Equal(expected, WrapGeometry.ClampColumns(input));
}
