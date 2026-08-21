using System.Text;
using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Layout;

/// <summary>
/// 2026-08-02 巨大 1 行対応(変更 B)。LineLayout.WrapFirstSegments / WrapThroughOffset の契約テスト。
/// 守る性質は 2 つ。
/// (1) 打ち切り結果は完全な Wrap 結果の prefix と厳密に一致する
/// (2) ReachedLineEnd は「打ち切っていない」と同値である
/// Wrap は左から右への貪欲な走査でセグメント境界が先行内容だけで決まるため
/// (1) が成り立つ。これが ViewportLayout / ComputeCaretPoint の挙動不変の根拠になる。
///
/// fixture は設計書 §5 の L1 軸(ASCII / CJK / サロゲート / 混在)を通す。ASCII 固定幅だけだと
/// (a) 境界が必ず 10 の倍数 (b) cpLen が常に 1、という 2 つの縮退が起きて、
/// 打ち切り版でこそ効く「打ち切り位置がコードポイント境界に乗る」性質が試されないため。
/// </summary>
public class LineLayoutPrefixTests
{
    private static MonoCharMetrics M => new(halfWidthPx: 1, lineHeightPx: 10);

    // 文字種の識別子。InlineData には ASCII 文字列だけを置く
    // (xUnit v2 は InlineData の string を UTF-8 → Base64 で直列化して test case ID を作るため、
    //  非 BMP を直接並べると ID が衝突して片方が黙ってスキップされる)。
    private const string Ascii = "ascii";
    private const string Cjk = "cjk";
    private const string Emoji = "emoji";
    private const string Mixed = "mixed";

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

    /// <summary>
    /// 指定した文字種で <paramref name="codePoints"/> コードポイント分の行を作る
    /// (char 数ではない。MonoCharMetrics では ASCII=1px・CJK=2px・サロゲートペア=2px)。
    /// </summary>
    private static string LineOf(string kind, int codePoints)
    {
        if (kind == Ascii)
            return new string('a', codePoints);

        (string unit, int unitCodePoints) = kind switch
        {
            Cjk => ("あ", 1),
            Emoji => ("\U0001F600", 1),
            Mixed => ("a\U0001F600あb", 4),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知の文字種"),
        };

        var sb = new StringBuilder();
        for (int n = 0; n < codePoints; n += unitCodePoints)
            sb.Append(unit);
        return sb.ToString();
    }

    private static void AssertIsPrefixOf(IReadOnlyList<WrapSegment> full, WrapResult pre)
    {
        Assert.True(
            pre.Segments.Count <= full.Count,
            $"pre={pre.Segments.Count} は full={full.Count} 以下でなければならない"
        );
        for (int i = 0; i < pre.Segments.Count; i++)
            Assert.Equal(full[i], pre.Segments[i]);

        // 打ち切っていない <=> ReachedLineEnd。末尾セグメントはループ後に足されるため、
        // 早期打ち切り時は必ず full より短くなる。
        Assert.Equal(pre.Segments.Count == full.Count, pre.ReachedLineEnd);
    }

    private static int CoveredChars(WrapResult r)
    {
        int covered = 0;
        foreach (var s in r.Segments)
            covered += s.Length;
        return covered;
    }

    [Theory]
    [InlineData(Ascii, 1)]
    [InlineData(Ascii, 3)]
    [InlineData(Ascii, 100)]
    [InlineData(Cjk, 1)]
    [InlineData(Cjk, 3)]
    [InlineData(Cjk, 100)]
    [InlineData(Emoji, 1)]
    [InlineData(Emoji, 3)]
    [InlineData(Emoji, 100)]
    [InlineData(Mixed, 1)]
    [InlineData(Mixed, 3)]
    [InlineData(Mixed, 100)]
    public void WrapFirstSegments_matches_the_prefix_of_full_Wrap(string kind, int segmentCount)
    {
        var line = LineOf(kind, 100);
        var full = LineLayout.Wrap(line, 10, M);
        var pre = LineLayout.WrapFirstSegments(line, 10, M, segmentCount);

        AssertIsPrefixOf(full, pre);
    }

    [Theory]
    [InlineData(Ascii)]
    [InlineData(Cjk)]
    [InlineData(Emoji)]
    [InlineData(Mixed)]
    public void WrapThroughOffset_matches_the_prefix_of_full_Wrap_and_covers_the_offset(string kind)
    {
        // 行内の全オフセットを掃く。これでセグメント境界ちょうどのケースが必ず含まれる。
        // 境界ちょうどこそが「offset を超えてカバー」を「以上」に緩めたときに壊れる唯一の位置で、
        // Task 4 の ComputeCaretPoint はキャレット位置を含むセグメントを必要とするため、
        // この 1 文字ぶんの緩みがそのままキャレット Y のずれになる。
        var line = LineOf(kind, 60);
        var full = LineLayout.Wrap(line, 10, M);

        for (int offset = 0; offset < line.Length; offset++)
        {
            var pre = LineLayout.WrapThroughOffset(line, 10, M, offset);
            AssertIsPrefixOf(full, pre);

            int covered = CoveredChars(pre);
            Assert.True(
                covered > offset,
                $"kind={kind} offset={offset}: covered={covered} が offset を厳密に超えていない"
            );
        }
    }

    [Fact]
    public void WrapThroughOffset_never_truncates_inside_a_surrogate_pair()
    {
        // 絵文字だけの行なら、正しい打ち切り位置は必ず偶数 char(= ペア境界)になる。
        // Task 4 は minCoverOffset に code unit 単位の caretInLine を渡すため、
        // ペアの内側を要求されても境界がずれないことが前提になる。
        var line = LineOf(Emoji, 200);

        for (int offset = 0; offset < line.Length; offset++)
        {
            var pre = LineLayout.WrapThroughOffset(line, 10, M, offset);
            foreach (var s in pre.Segments)
            {
                Assert.Equal(0, s.OffsetInLine % 2);
                Assert.Equal(0, s.Length % 2);
            }
            Assert.True(CoveredChars(pre) > offset);
        }
    }

    [Theory]
    [InlineData(Ascii)]
    [InlineData(Cjk)]
    [InlineData(Emoji)]
    [InlineData(Mixed)]
    public void WrapFirstSegments_stops_after_the_requested_number_of_segments(string kind)
    {
        var line = LineOf(kind, 1000);
        var pre = LineLayout.WrapFirstSegments(line, 10, M, segmentCount: 4);

        Assert.Equal(4, pre.Segments.Count);
        Assert.False(pre.ReachedLineEnd);
    }

    [Fact]
    public void WrapFirstSegments_reaches_the_line_end_when_the_line_is_shorter_than_requested()
    {
        var line = LineOf(Ascii, 25); // 幅 10 → 3 セグメント
        var pre = LineLayout.WrapFirstSegments(line, 10, M, segmentCount: 100);

        Assert.True(pre.ReachedLineEnd);
        Assert.Equal(LineLayout.Wrap(line, 10, M).Count, pre.Segments.Count);
    }

    [Theory]
    [InlineData(Ascii)]
    [InlineData(Cjk)]
    [InlineData(Emoji)]
    public void WrapThroughOffset_at_end_of_line_cannot_truncate(string kind)
    {
        // 行末キャレット相当。行末まで走らないと offsetInLine を超えられない
        // = B は効かない(設計書 §2 の非対称性。そこは変更 A が受け持つ)。
        var line = LineOf(kind, 100);
        var pre = LineLayout.WrapThroughOffset(line, 10, M, offsetInLine: line.Length);

        Assert.True(pre.ReachedLineEnd);
        Assert.Equal(LineLayout.Wrap(line, 10, M).Count, pre.Segments.Count);
    }

    [Fact]
    public void WrapFirstSegments_measures_only_what_it_needs()
    {
        // 打ち切りが効いていることの直接証拠。10 万文字の行から 4 セグメントだけ要求する。
        var line = LineOf(Ascii, 100_000);

        var counting = new CountingMetrics(M);
        _ = LineLayout.WrapFirstSegments(line, 10, counting, segmentCount: 4);
        int truncated = counting.Calls;

        var countingFull = new CountingMetrics(M);
        _ = LineLayout.Wrap(line, 10, countingFull);
        int fullCalls = countingFull.Calls;

        Assert.Equal(100_000, fullCalls);
        Assert.True(truncated < 100, $"truncated={truncated} は 100 未満でなければならない");
    }

    [Fact]
    public void WrapThroughOffset_measures_only_what_it_needs()
    {
        var line = LineOf(Ascii, 100_000);

        var counting = new CountingMetrics(M);
        _ = LineLayout.WrapThroughOffset(line, 10, counting, offsetInLine: 35);

        Assert.True(counting.Calls < 100, $"Calls={counting.Calls} は 100 未満でなければならない");
    }

    [Fact]
    public void Wrap_off_and_empty_line_report_reaching_the_line_end()
    {
        var off = LineLayout.WrapThroughOffset("abcde", 0, M, offsetInLine: 0);
        Assert.True(off.ReachedLineEnd);
        Assert.Single(off.Segments);
        Assert.Equal((0, 5), (off.Segments[0].OffsetInLine, off.Segments[0].Length));

        var offBySeg = LineLayout.WrapFirstSegments("abcde", 0, M, segmentCount: 1);
        Assert.True(offBySeg.ReachedLineEnd);
        Assert.Single(offBySeg.Segments);

        var empty = LineLayout.WrapThroughOffset("", 10, M, offsetInLine: 0);
        Assert.True(empty.ReachedLineEnd);
        Assert.Single(empty.Segments);
        Assert.Equal((0, 0), (empty.Segments[0].OffsetInLine, empty.Segments[0].Length));

        var emptyBySeg = LineLayout.WrapFirstSegments("", 10, M, segmentCount: 1);
        Assert.True(emptyBySeg.ReachedLineEnd);
        Assert.Single(emptyBySeg.Segments);
        Assert.Equal((0, 0), (emptyBySeg.Segments[0].OffsetInLine, emptyBySeg.Segments[0].Length));
    }

    [Theory]
    [InlineData(0)] // Wrap は必ず 1 個以上返す契約なので 0 要求は呼び出し側のバグ
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void WrapFirstSegments_rejects_a_non_positive_segment_count(int requested)
    {
        // paramName まで固定する。型だけ見ていると、ガードの対象引数を取り違える変異
        // (別の引数を検証する / 別の名前で投げる)が素通りする。
        // ここは意図的に nameof ではなくリテラル: 本番側の引数名を直接ピン留めしたいのであって、
        // テスト側の引数名を写したいのではない(nameof だと本番のリネームを取り逃がす)。
        Assert.Throws<ArgumentOutOfRangeException>(
            "segmentCount",
            () =>
            {
                _ = LineLayout.WrapFirstSegments(LineOf(Ascii, 100), 10, M, requested);
            }
        );
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void WrapThroughOffset_rejects_a_negative_offset(int requested)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            "offsetInLine",
            () =>
            {
                _ = LineLayout.WrapThroughOffset(LineOf(Ascii, 100), 10, M, requested);
            }
        );
    }
}
