using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Layout;

public class LineLayoutTests
{
    private static MonoCharMetrics M => new(halfWidthPx: 1, lineHeightPx: 10);

    [Fact]
    public void Wrap_off_returns_single_segment()
    {
        var r = LineLayout.Wrap("abcde", 0, M);
        Assert.Single(r);
        Assert.Equal((0, 5), (r[0].OffsetInLine, r[0].Length));
    }

    [Fact]
    public void Wrap_ascii_at_boundary()
    {
        // 幅=3 → "abc"/"de"
        var r = LineLayout.Wrap("abcde", 3, M);
        Assert.Equal(2, r.Count);
        Assert.Equal((0, 3), (r[0].OffsetInLine, r[0].Length));
        Assert.Equal((3, 2), (r[1].OffsetInLine, r[1].Length));
    }

    [Fact]
    public void Wrap_never_splits_surrogate_pair()
    {
        // 幅=3 で "a😀b" → "a"+"😀" (1+2=3) / "b"
        var r = LineLayout.Wrap("a😀b", 3, M);
        Assert.Equal(2, r.Count);
        Assert.Equal((0, 3), (r[0].OffsetInLine, r[0].Length)); // 'a'+high+low
        Assert.Equal((3, 1), (r[1].OffsetInLine, r[1].Length));
    }

    [Fact]
    public void Wrap_forces_progress_when_single_codepoint_exceeds_width()
    {
        // 幅=1 で "😀" → 幅 2 だが強制前進で 1 セグメントに 😀 全体を入れる
        var r = LineLayout.Wrap("😀", 1, M);
        Assert.Single(r);
        Assert.Equal((0, 2), (r[0].OffsetInLine, r[0].Length));
    }

    [Fact]
    public void Empty_line_yields_one_empty_segment()
    {
        var r = LineLayout.Wrap("", 10, M);
        Assert.Single(r);
        Assert.Equal((0, 0), (r[0].OffsetInLine, r[0].Length));
    }

    [Fact]
    public void Segments_cover_the_whole_line()
    {
        var line = "あいうえお漢字ABC";
        var r = LineLayout.Wrap(line, 5, M);
        int sum = 0;
        foreach (var s in r)
            sum += s.Length;
        Assert.Equal(line.Length, sum);
    }

    [Fact]
    public void Lone_high_surrogate_is_treated_as_single_code_unit()
    {
        // 単独 high-surrogate(不正 UTF-16)。ペアとして扱わず 1 code-unit 前進する
        // (無限ループしない・幅計測は MonoCharMetrics 側で非 ASCII=2 と評価される)。
        // 幅=1・"\uD83Da": [\uD83D] は幅 2 > 1 だが強制前進で seg0 に入る → 次の 'a' は幅 1 で seg1 開始
        var r = LineLayout.Wrap("\uD83Da", 1, M);
        Assert.Equal(2, r.Count);
        Assert.Equal((0, 1), (r[0].OffsetInLine, r[0].Length)); // lone high-surrogate だけ
        Assert.Equal((1, 1), (r[1].OffsetInLine, r[1].Length)); // 'a'
    }
}
