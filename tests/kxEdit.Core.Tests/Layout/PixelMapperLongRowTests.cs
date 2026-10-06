using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Layout;

/// <summary>
/// 長い行(<see cref="PixelMapper.LongRowThreshold"/> を超える視覚行)の座標(設計書 2026-10-06 §3.3)。
/// <see cref="GdiLikeMetrics"/> は、非 ASCII の一括計測が足し算より 1 小さく、43,679 字を超えると 0 になる。
/// 短い行は一括計測(今まで)、長い行は足し算になることを、この差で見分ける。
/// </summary>
public class PixelMapperLongRowTests
{
    private static readonly ICharMetrics G = new GdiLikeMetrics();
    private const int T = PixelMapper.LongRowThreshold;

    [Fact]
    public void Threshold_is_the_text_op_limit()
    {
        Assert.Equal(FrameBuilder.MaxCharsPerTextOp, PixelMapper.LongRowThreshold);
    }

    // ---- 閾値の境界(短い行は今までどおり一括計測) ----

    [Fact]
    public void Row_at_exactly_the_threshold_keeps_the_one_shot_measure()
    {
        string row = new('あ', T);
        Assert.False(PixelMapper.IsLongRow(row));
        Assert.Equal(2 * T - 1, PixelMapper.OffsetToPx(row, T, G));
        Assert.Equal(19, PixelMapper.OffsetToPx(row, 10, G)); // prefix 10 字の一括計測 = 20-1
        Assert.Equal(2 * T - 1, PixelMapper.RowWidthPx(row, G));
    }

    [Fact]
    public void Row_over_the_threshold_uses_the_additive_measure()
    {
        string row = new('あ', T + 1);
        Assert.True(PixelMapper.IsLongRow(row));
        Assert.Equal(2 * (T + 1), PixelMapper.OffsetToPx(row, T + 1, G));
        // 判定は行の長さで決まる(prefix が短くても足し算)。
        Assert.Equal(20, PixelMapper.OffsetToPx(row, 10, G));
        Assert.Equal(2 * (T + 1), PixelMapper.RowWidthPx(row, G));
    }

    // ---- GDI の上限を超える行(今は 0 になる = 設計書 §1.2 の不具合) ----

    [Fact]
    public void OffsetToPx_beyond_the_gdi_limit_is_not_zero()
    {
        string row = new('あ', 50_000);
        Assert.Equal(90_000, PixelMapper.OffsetToPx(row, 45_000, G));
        Assert.Equal(100_000, PixelMapper.OffsetToPx(row, 50_000, G));
        Assert.Equal(100_000, PixelMapper.RowWidthPx(row, G));
    }

    [Fact]
    public void OffsetToPx_in_a_long_row_snaps_a_low_surrogate_forward()
    {
        string row = string.Concat(Enumerable.Repeat("😀", 10_000)); // 20,000 単位
        Assert.True(PixelMapper.IsLongRow(row));
        Assert.Equal(PixelMapper.OffsetToPx(row, 4, G), PixelMapper.OffsetToPx(row, 5, G));
        Assert.Equal(4, PixelMapper.OffsetToPx(row, 4, G));
    }

    [Theory]
    [InlineData(90_000, 45_000)] // ちょうど境界 = その直前のコードポイントを含めた直後
    [InlineData(89_999, 45_000)] // コードポイントの途中 = そのコードポイントの直後
    [InlineData(89_998, 44_999)]
    [InlineData(1, 1)]
    [InlineData(100_000, 50_000)]
    [InlineData(200_000, 50_000)] // 行末より右 = 行末
    public void PxToOffset_in_a_long_row_inverts_OffsetToPx(int px, int expected)
    {
        string row = new('あ', 50_000);
        Assert.Equal(expected, PixelMapper.PxToOffset(row, px, G));
    }

    // ---- SliceForWindow ----

    [Fact]
    public void SliceForWindow_from_the_row_start()
    {
        string row = new('あ', 20_000); // 1 文字 2px
        var s = PixelMapper.SliceForWindow(row, 0, 10, G);
        // 開始 X が 10 未満の文字は 0..4。最初に 10 以上になる 5 の次まで含める。
        Assert.Equal(new PixelMapper.RowSlice(0, 6, 0), s);
    }

    [Fact]
    public void SliceForWindow_starts_at_the_code_point_containing_the_left_edge()
    {
        string row = new('あ', 20_000);
        var s = PixelMapper.SliceForWindow(row, 11, 21, G);
        // 11 は文字 5([10,12))の中。開始 X が 21 未満の最後は文字 10(X=20)。その次の 11 まで含める。
        Assert.Equal(new PixelMapper.RowSlice(5, 12, 10), s);
        Assert.Equal(PixelMapper.OffsetToPx(row, s.Start, G), s.StartPx);
    }

    [Fact]
    public void SliceForWindow_with_a_negative_left_edge_starts_at_zero()
    {
        string row = new('あ', 20_000);
        Assert.Equal(new PixelMapper.RowSlice(0, 3, 0), PixelMapper.SliceForWindow(row, -50, 3, G));
    }

    [Fact]
    public void SliceForWindow_is_empty_right_of_the_row()
    {
        string row = new('あ', 20_000);
        var s = PixelMapper.SliceForWindow(row, 40_000, 40_010, G);
        Assert.Equal(new PixelMapper.RowSlice(20_000, 20_000, 40_000), s);
    }

    [Fact]
    public void SliceForWindow_does_not_split_a_surrogate_pair()
    {
        string row = string.Concat(Enumerable.Repeat("😀", 10_000)); // 1 ペア 2px
        var s = PixelMapper.SliceForWindow(row, 3, 5, G);
        // 3 はペア 1(単位 2..3・[2,4))の中。開始 X が 5 未満の最後はペア 2(X=4)。その次のペア 3 まで含める。
        Assert.Equal(new PixelMapper.RowSlice(2, 8, 2), s);
    }

    [Fact]
    public void SliceForWindow_does_not_start_on_a_zero_width_code_point()
    {
        // 幅 0 のコードポイントを持つ偽のメトリクスで、基底文字(1px) + 結合文字(0px)を並べる。
        var m = new ZeroWidthMarkMetrics();
        string row = string.Concat(Enumerable.Repeat("a\u0301", 10_000)); // 20,000 単位
        var s = PixelMapper.SliceForWindow(row, 3, 5, m);
        // 左端 3 は基底文字 3(単位 6)の中。結合文字(単位 7)から始めない。
        Assert.Equal(6, s.Start);
        Assert.Equal(3, s.StartPx);
        // 左端がちょうど基底文字 3 の終わり(X=4)= 結合文字 3 の位置でも、結合文字からは始めない。
        var t = PixelMapper.SliceForWindow(row, 4, 6, m);
        Assert.Equal(8, t.Start); // 基底文字 4
        Assert.Equal(4, t.StartPx);
    }

    // ---- 幅の和が int を超える行(最終レビュー I-1) ----
    // HugeWidthMetrics は 1 コードポイント 2^17 px。20,000 字の行は約 26 億 px で、int で足すと負へ回り込む。

    private static readonly ICharMetrics Huge = new HugeWidthMetrics();
    private const int HugePx = HugeWidthMetrics.CodePointPx;
    private static readonly string HugeRow = new('a', 20_000);

    [Fact]
    public void Width_beyond_int_saturates_instead_of_going_negative()
    {
        Assert.True(PixelMapper.IsLongRow(HugeRow));
        Assert.Equal(int.MaxValue, PixelMapper.RowWidthPx(HugeRow, Huge));
        Assert.Equal(int.MaxValue, PixelMapper.OffsetToPx(HugeRow, HugeRow.Length, Huge));
        Assert.Equal(int.MaxValue, PixelMapper.OffsetToPx(HugeRow, 16_384, Huge)); // ちょうど 2^31
        Assert.Equal(16_383 * HugePx, PixelMapper.OffsetToPx(HugeRow, 16_383, Huge));
    }

    [Fact]
    public void PxToOffset_beyond_int_does_not_wrap()
    {
        // int.MaxValue は文字 16,383([16,383 × 2^17, 2^31))の中 = その直後。
        Assert.Equal(16_384, PixelMapper.PxToOffset(HugeRow, int.MaxValue, Huge));
        Assert.Equal(16_383, PixelMapper.PxToOffset(HugeRow, (16_383 * HugePx) - 1, Huge));
    }

    [Fact]
    public void SliceForWindow_beyond_int_stops_at_the_window()
    {
        // 窓の右端が int.MaxValue でも、和が右端に届いたところで止まる(行末まで歩かない)。
        Assert.Equal(
            new PixelMapper.RowSlice(0, 16_385, 0),
            PixelMapper.SliceForWindow(HugeRow, 0, int.MaxValue, Huge)
        );
        // 窓が int.MaxValue の手前なら、左端を含む文字 16,383 から始まり、StartPx は負にならない。
        Assert.Equal(
            new PixelMapper.RowSlice(16_383, 16_385, 16_383 * HugePx),
            PixelMapper.SliceForWindow(HugeRow, int.MaxValue - 1000, int.MaxValue, Huge)
        );
    }

    /// <summary>U+0301 だけ幅 0、それ以外は 1px。</summary>
    private sealed class ZeroWidthMarkMetrics : ICharMetrics
    {
        public int LineHeightPx => 10;

        public int MeasureRun(ReadOnlySpan<char> text)
        {
            int px = 0;
            foreach (char c in text)
                px += c == '\u0301' ? 0 : 1;
            return px;
        }
    }
}
