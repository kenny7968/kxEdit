using yEdit.Core.Buffers;
using yEdit.Core.Layout;

namespace yEdit.Core.Tests.Layout;

/// <summary>
/// 2026-08-02 巨大 1 行対応(変更 B-2)。ViewportLayout.Build が
/// 「可視分だけ Wrap する」ようになったことを、MeasureRun の呼び出し回数で直接検証する。
/// 併せて返す VisualRow が変更前と同一であること(挙動不変)も確認する。
/// </summary>
public class ViewportLayoutPrefixTests
{
    private static MonoCharMetrics M => new(halfWidthPx: 1, lineHeightPx: 10);

    private sealed class CountingMetrics(ICharMetrics inner) : ICharMetrics
    {
        private readonly ICharMetrics _inner = inner;

        public int Calls { get; private set; }

        public int LineHeightPx => _inner.LineHeightPx;

        public int MeasureRun(ReadOnlySpan<char> text)
        {
            Calls++;
            return _inner.MeasureRun(text);
        }
    }

    [Fact]
    public void Build_over_a_huge_single_line_measures_only_the_visible_part()
    {
        // 空白・改行を含まない 10 万文字の 1 行 = 調査で問題になった形状。
        var snap = TextBuffer.FromString(new string('a', 100_000)).Current;
        var counting = new CountingMetrics(M);

        var rows = ViewportLayout.Build(snap, 0, heightPx: 400, wrapColumns: 10, counting);

        Assert.Equal(40, rows.Count);
        // 可視 40 行 × 10 文字 = 400 文字ぶん + maxWidthPx 算出の 1 回 + 余裕。
        // 10 万文字を舐めていたら 100,000 を超えるので、ここで確実に落ちる。
        Assert.True(counting.Calls < 1_000, $"Calls={counting.Calls} は 1,000 未満のはず");
    }

    [Fact]
    public void Build_returns_the_same_rows_as_a_full_Wrap_would()
    {
        var snap = TextBuffer.FromString(new string('a', 100_000)).Current;
        var rows = ViewportLayout.Build(snap, 0, heightPx: 400, wrapColumns: 10, M);

        var full = LineLayout.Wrap(new string('a', 100_000), 10 * M.MeasureRun("0"), M);
        Assert.Equal(40, rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(full[i].OffsetInLine, rows[i].SegmentStartChar);
            Assert.Equal(full[i].Length, rows[i].SegmentLength);
            Assert.Equal(i, rows[i].SegmentIndex);
            Assert.Equal(i * M.LineHeightPx, rows[i].YPx);
        }
    }

    [Fact]
    public void Build_across_multiple_logical_lines_is_unchanged()
    {
        var snap = TextBuffer.FromString("aaaaaaaaaaaaaaa\nbbbbb\nccccccccccccccc").Current;
        var rows = ViewportLayout.Build(snap, 0, heightPx: 1000, wrapColumns: 10, M);

        // 行 0: 15 文字 → 2 seg / 行 1: 5 文字 → 1 seg / 行 2: 15 文字 → 2 seg
        Assert.Equal(5, rows.Count);
        Assert.Equal((0, 0), (rows[0].LogicalLine, rows[0].SegmentIndex));
        Assert.Equal((0, 1), (rows[1].LogicalLine, rows[1].SegmentIndex));
        Assert.Equal((1, 0), (rows[2].LogicalLine, rows[2].SegmentIndex));
        Assert.Equal((2, 0), (rows[3].LogicalLine, rows[3].SegmentIndex));
        Assert.Equal((2, 1), (rows[4].LogicalLine, rows[4].SegmentIndex));
    }

    /// <summary>
    /// 打ち切り数の境界。heightPx が lineHeight の倍数ちょうど / 倍数 +1 / 倍数 -1 /
    /// lineHeight 未満のそれぞれで、返る視覚行数が変更前と同じであることを固定する。
    /// Build は「積んでから y 加算」なので判定は <c>y &lt; heightPx</c> = ceil 相当。
    /// rowsNeeded を 1 でも小さく取ると倍数 +1 のケース(下端が切れる行)が落ちる。
    /// </summary>
    [Theory]
    [InlineData(10, 1)] // lineHeight ちょうど
    [InlineData(11, 2)] // 倍数 +1 → 下端が切れる行を 1 本余分に積む
    [InlineData(9, 1)] // lineHeight 未満でも 1 行は積む
    [InlineData(1, 1)]
    [InlineData(399, 40)] // 倍数 -1
    [InlineData(400, 40)] // 倍数ちょうど
    [InlineData(401, 41)] // 倍数 +1
    public void Build_row_count_follows_the_height_boundary(int heightPx, int expectedRows)
    {
        var snap = TextBuffer.FromString(new string('a', 100_000)).Current;
        var rows = ViewportLayout.Build(snap, 0, heightPx, wrapColumns: 10, M);

        Assert.Equal(expectedRows, rows.Count);

        // 打ち切っても内容は完全な Wrap の prefix と一致する。
        var full = LineLayout.Wrap(new string('a', 100_000), 10 * M.MeasureRun("0"), M);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(full[i].OffsetInLine, rows[i].SegmentStartChar);
            Assert.Equal(full[i].Length, rows[i].SegmentLength);
            Assert.Equal(i * M.LineHeightPx, rows[i].YPx);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Build_with_non_positive_height_yields_no_rows(int heightPx)
    {
        var snap = TextBuffer.FromString(new string('a', 100_000)).Current;
        var counting = new CountingMetrics(M);

        var rows = ViewportLayout.Build(snap, 0, heightPx, wrapColumns: 10, counting);

        Assert.Empty(rows);
        // 1 文字も測らずに返す(maxWidthPx の算出にすら到達しない)。
        Assert.Equal(0, counting.Calls);
    }

    /// <summary>
    /// ビューポートが埋まった時点で、次の論理行には一切踏み込まない。
    /// 行 0(10 文字)= 1 視覚行で heightPx=10 を埋め、行 1(10 万文字)は測らない。
    /// </summary>
    [Fact]
    public void Build_does_not_touch_the_line_after_the_viewport_is_full()
    {
        var snap = TextBuffer
            .FromString(new string('a', 10) + "\n" + new string('b', 100_000))
            .Current;
        var counting = new CountingMetrics(M);

        var rows = ViewportLayout.Build(snap, 0, heightPx: 10, wrapColumns: 10, counting);

        Assert.Single(rows);
        Assert.Equal(0, rows[0].LogicalLine);
        // maxWidthPx 算出 1 回 + 行 0 の 10 文字 = 11 回。行 1 に 1 セグメントでも
        // 踏み込むと 11 文字ぶん余分に測るため 12 以上になる。
        Assert.Equal(11, counting.Calls);
    }

    /// <summary>
    /// 折り返し OFF(wrapColumns=0)でも打ち切りの導入で挙動が変わらないこと。
    /// OFF は maxWidthPx=0 → LineLayout が単一セグメントを即返す経路。
    /// </summary>
    [Fact]
    public void Build_with_wrap_off_is_unchanged_over_a_huge_line()
    {
        var snap = TextBuffer.FromString(new string('a', 100_000) + "\nb").Current;
        var counting = new CountingMetrics(M);

        var rows = ViewportLayout.Build(snap, 0, heightPx: 100, wrapColumns: 0, counting);

        Assert.Equal(2, rows.Count);
        Assert.Equal(
            new VisualRow(
                LogicalLine: 0,
                SegmentIndex: 0,
                SegmentStartChar: 0,
                SegmentLength: 100_000,
                YPx: 0
            ),
            rows[0]
        );
        Assert.Equal(
            new VisualRow(
                LogicalLine: 1,
                SegmentIndex: 0,
                SegmentStartChar: 100_001,
                SegmentLength: 1,
                YPx: 10
            ),
            rows[1]
        );
        Assert.Equal(0, counting.Calls);
    }
}
