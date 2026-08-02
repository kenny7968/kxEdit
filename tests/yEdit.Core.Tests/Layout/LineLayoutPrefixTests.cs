using yEdit.Core.Layout;

namespace yEdit.Core.Tests.Layout;

/// <summary>
/// 2026-08-02 巨大 1 行対応(変更 B)。LineLayout.WrapPrefix の契約テスト。
/// 守る性質は 2 つ。
/// (1) 打ち切り結果は完全な Wrap 結果の prefix と厳密に一致する
/// (2) ReachedLineEnd は「打ち切っていない」と同値である
/// Wrap は左から右への貪欲な走査でセグメント境界が先行内容だけで決まるため
/// (1) が成り立つ。これが ViewportLayout / ComputeCaretPoint の挙動不変の根拠になる。
/// </summary>
public class LineLayoutPrefixTests
{
    private static MonoCharMetrics M => new(halfWidthPx: 1, lineHeightPx: 10);

    /// <summary>MeasureRun の呼び出し回数を数える decorator。打ち切りが効いていることの直接証拠。</summary>
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

    private static string LongAsciiLine(int chars) => new('a', chars);

    [Theory]
    // (minSegments, minCoverOffset)
    [InlineData(1, -1)]
    [InlineData(3, -1)]
    [InlineData(100, -1)]
    [InlineData(0, 0)]
    [InlineData(0, 7)]
    [InlineData(0, 99)]
    [InlineData(2, 25)]
    public void WrapPrefix_result_is_a_strict_prefix_of_full_Wrap(
        int minSegments,
        int minCoverOffset
    )
    {
        var line = LongAsciiLine(100);
        var full = LineLayout.Wrap(line, 10, M);
        var pre = LineLayout.WrapPrefix(line, 10, M, minSegments, minCoverOffset);

        Assert.True(pre.Segments.Count <= full.Count);
        for (int i = 0; i < pre.Segments.Count; i++)
            Assert.Equal(full[i], pre.Segments[i]);

        // 打ち切っていない <=> ReachedLineEnd。末尾セグメントはループ後に足されるため、
        // 早期打ち切り時は必ず full より短くなる。
        Assert.Equal(pre.Segments.Count == full.Count, pre.ReachedLineEnd);
    }

    [Fact]
    public void WrapPrefix_stops_after_minSegments_segments()
    {
        var line = LongAsciiLine(1000);
        var pre = LineLayout.WrapPrefix(line, 10, M, minSegments: 4, minCoverOffset: -1);

        Assert.Equal(4, pre.Segments.Count);
        Assert.False(pre.ReachedLineEnd);
    }

    [Fact]
    public void WrapPrefix_covers_the_requested_offset()
    {
        var line = LongAsciiLine(1000);
        var pre = LineLayout.WrapPrefix(line, 10, M, minSegments: 0, minCoverOffset: 35);

        int covered = 0;
        foreach (var s in pre.Segments)
            covered += s.Length;
        Assert.True(covered > 35, $"covered={covered} は 35 を超えていなければならない");
        Assert.False(pre.ReachedLineEnd);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(10)] // セグメント境界ちょうど
    [InlineData(11)]
    [InlineData(19)]
    [InlineData(20)] // セグメント境界ちょうど
    [InlineData(35)]
    public void WrapPrefix_includes_the_segment_that_contains_the_requested_offset(int offset)
    {
        // 幅 10・ASCII 1px なので境界は 10 の倍数。offset が境界ちょうどのケースを必ず含める。
        // 「minCoverOffset を超えてカバー」を「以上」に緩めると、境界ちょうどのときだけ
        // offset を含まない結果が返る(offset の文字は次のセグメント側にある)。
        // Task 4 の ComputeCaretPoint は「キャレット位置を含むセグメント」を必要とするため、
        // この 1 文字ぶんの緩みがそのままキャレット Y のずれになる。
        var line = LongAsciiLine(1000);
        var pre = LineLayout.WrapPrefix(line, 10, M, minSegments: 0, minCoverOffset: offset);

        int covered = 0;
        foreach (var s in pre.Segments)
            covered += s.Length;
        Assert.True(
            covered > offset,
            $"covered={covered} は offset={offset} を厳密に超えていなければならない"
        );

        // 同時に、完全結果の prefix であることも崩れていないこと
        var full = LineLayout.Wrap(line, 10, M);
        for (int i = 0; i < pre.Segments.Count; i++)
            Assert.Equal(full[i], pre.Segments[i]);
    }

    [Fact]
    public void WrapPrefix_reaches_line_end_when_the_line_is_shorter_than_requested()
    {
        var line = LongAsciiLine(25); // 幅 10 → 3 セグメント
        var pre = LineLayout.WrapPrefix(line, 10, M, minSegments: 100, minCoverOffset: -1);

        Assert.True(pre.ReachedLineEnd);
        Assert.Equal(LineLayout.Wrap(line, 10, M).Count, pre.Segments.Count);
    }

    [Fact]
    public void WrapPrefix_at_end_of_line_cannot_truncate()
    {
        // 行末キャレット相当。行末まで走らないと minCoverOffset を超えられない
        // = B は効かない(設計書 §2 の非対称性。そこは変更 A が受け持つ)。
        var line = LongAsciiLine(100);
        var pre = LineLayout.WrapPrefix(line, 10, M, minSegments: 0, minCoverOffset: 100);

        Assert.True(pre.ReachedLineEnd);
        Assert.Equal(LineLayout.Wrap(line, 10, M).Count, pre.Segments.Count);
    }

    [Fact]
    public void WrapPrefix_measures_only_what_it_needs()
    {
        // 打ち切りが効いていることの直接証拠。10 万文字の行から 4 セグメントだけ要求する。
        var line = LongAsciiLine(100_000);

        var counting = new CountingMetrics(M);
        _ = LineLayout.WrapPrefix(line, 10, counting, minSegments: 4, minCoverOffset: -1);
        int truncated = counting.Calls;

        var countingFull = new CountingMetrics(M);
        _ = LineLayout.Wrap(line, 10, countingFull);
        int fullCalls = countingFull.Calls;

        Assert.Equal(100_000, fullCalls);
        Assert.True(truncated < 100, $"truncated={truncated} は 100 未満でなければならない");
    }

    [Fact]
    public void Wrap_off_and_empty_line_report_reaching_the_line_end()
    {
        var off = LineLayout.WrapPrefix("abcde", 0, M, minSegments: 0, minCoverOffset: -1);
        Assert.True(off.ReachedLineEnd);
        Assert.Single(off.Segments);

        var empty = LineLayout.WrapPrefix("", 10, M, minSegments: 0, minCoverOffset: -1);
        Assert.True(empty.ReachedLineEnd);
        Assert.Single(empty.Segments);
        Assert.Equal((0, 0), (empty.Segments[0].OffsetInLine, empty.Segments[0].Length));
    }

    [Theory]
    // 「要求なし」のセンチネルが 2 引数で違う(0 と -1)ため、片方の流儀をもう片方に
    // 持ち込む誤用が起きうる。とくに minSegments: -1 は「無制限」に見えて実際は
    // 「要求なし」= 最初のセグメント境界で打ち切り、という静かな誤りになる。
    // 範囲外は即座に例外にして、その誤用を実行時に露出させる。
    [InlineData(-1, -1)] // minSegments に「無制限」のつもりで -1
    [InlineData(int.MinValue, -1)]
    [InlineData(0, -2)] // minCoverOffset が下限 -1 を下回る
    public void WrapPrefix_rejects_out_of_range_requests(int minSegments, int minCoverOffset)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = LineLayout.WrapPrefix(LongAsciiLine(100), 10, M, minSegments, minCoverOffset);
        });
    }
}
