using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Layout;

/// <summary>
/// <see cref="ICharMetrics.MeasureAdditive"/> の既定実装(設計書 2026-10-06 §3.2)。
/// 1 コードポイントずつ <see cref="ICharMetrics.MeasureRun"/> で測った和になる。
/// </summary>
public class CharMetricsAdditiveTests
{
#pragma warning disable CA1859 // reason: 既定実装(interface の既定メソッド)は具象型からは呼べないため、interface 型で持つ必要がある
    private static readonly ICharMetrics G = new GdiLikeMetrics();
#pragma warning restore CA1859

    [Fact]
    public void Default_sums_code_point_widths_instead_of_measuring_the_run_at_once()
    {
        // "あいう" は一括だと 2*3-1 = 5(GdiLikeMetrics の模擬カーニング)。足し算は 6。
        Assert.Equal(5, G.MeasureRun("あいう"));
        Assert.Equal(6, G.MeasureAdditive("あいう"));
    }

    [Fact]
    public void Default_counts_a_surrogate_pair_once()
    {
        // a(1) + 😀(2) + b(1) = 4。ペアを 2 つの単独サロゲートとして測ると 2+2 になる。
        Assert.Equal(4, G.MeasureAdditive("a😀b"));
    }

    [Fact]
    public void Default_does_not_fall_to_zero_beyond_the_gdi_limit()
    {
        string s = new('あ', GdiLikeMetrics.GdiRunLimit + 1);
        Assert.Equal(0, G.MeasureRun(s)); // 前提: 一括計測は 0 になる
        Assert.Equal(2 * s.Length, G.MeasureAdditive(s));
    }

    [Fact]
    public void Default_of_empty_is_zero()
    {
        Assert.Equal(0, G.MeasureAdditive(ReadOnlySpan<char>.Empty));
    }
}
