using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Layout;

public class MonoCharMetricsTests
{
    private static MonoCharMetrics M => new(halfWidthPx: 1, lineHeightPx: 10);

    [Fact]
    public void Empty_is_zero() => Assert.Equal(0, M.MeasureRun(""));

    [Fact]
    public void Ascii_counts_half_per_char() => Assert.Equal(3, M.MeasureRun("abc"));

    [Fact]
    public void Cjk_counts_full_per_char() => Assert.Equal(4, M.MeasureRun("あ亜"));

    [Fact]
    public void Surrogate_pair_counts_full() => Assert.Equal(2, M.MeasureRun("😀")); // 2*1

    [Fact]
    public void Mixed() => Assert.Equal(1 + 2 + 2 + 1, M.MeasureRun("aあ😀b"));

    [Fact]
    public void Line_height_is_configured() => Assert.Equal(10, M.LineHeightPx);
}
