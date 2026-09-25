using System.Text.RegularExpressions;
using kxEdit.Core.Buffers;
using kxEdit.Core.Search;
using Xunit;

namespace kxEdit.Core.Tests.Search;

/// <summary>
/// 一致位置表(P-14)。正解は旧実装=<see cref="TextSearcher"/> の <c>Locate</c> / <c>FindPrev</c> /
/// <c>Count</c>(全件列挙)で、表を使う <see cref="MaterializedSearchStrategy"/> がそれと一致することを見る。
/// </summary>
public class MatchPositionsTests
{
    // ---- 表そのもの(合成した開始位置で、二分探索と線形走査の両方を旧実装の規則と比べる) ----

    /// <summary>旧 <c>TextSearcher.Locate</c> のループを、列挙の代わりに配列へ適用したもの。</summary>
    private static (int, int)? RefLocate(int[] starts, int[] lengths, MatchSpan span)
    {
        int ordinal = 0,
            total = 0;
        bool found = false;
        for (int k = 0; k < starts.Length; k++)
        {
            total++;
            if (starts[k] == span.Start && lengths[k] == span.Length)
            {
                ordinal = total;
                found = true;
            }
        }
        return found ? (ordinal, total) : null;
    }

    /// <summary>旧 <c>TextSearcher.FindPrev</c> のループ(同じ break 規則)。</summary>
    private static MatchSpan? RefFindPrev(int[] starts, int[] lengths, int before)
    {
        MatchSpan? last = null;
        for (int k = 0; k < starts.Length; k++)
        {
            if (starts[k] >= before)
                break;
            last = new MatchSpan(starts[k], lengths[k]);
        }
        return last;
    }

    private static void AssertMatchesReference(int[] starts, int[] lengths)
    {
        var p = new MatchPositions(starts, lengths);
        Assert.Equal(starts.Length, p.Count);
        int max = starts.Length == 0 ? 0 : starts.Max();
        for (int s = -1; s <= max + 2; s++)
        {
            for (int l = 0; l <= 3; l++)
            {
                Assert.Equal(RefLocate(starts, lengths, new(s, l)), p.Locate(new(s, l)));
            }
        }
        for (int before = -1; before <= max + 3; before++)
        {
            Assert.Equal(RefFindPrev(starts, lengths, before), p.FindPrev(before));
        }
    }

    [Fact]
    public void Empty_table_matches_reference() => AssertMatchesReference([], []);

    [Fact]
    public void Strictly_increasing_tables_match_reference()
    {
        var rnd = new Random(20260925);
        for (int t = 0; t < 300; t++)
        {
            int n = rnd.Next(0, 12);
            var starts = new int[n];
            var lengths = new int[n];
            int pos = rnd.Next(0, 3);
            for (int k = 0; k < n; k++)
            {
                starts[k] = pos;
                lengths[k] = rnd.Next(0, 3);
                pos += 1 + rnd.Next(0, 3); // 狭義単調増加
            }
            Assert.True(new MatchPositions(starts, lengths).IsStrictlyIncreasingForTest);
            AssertMatchesReference(starts, lengths);
        }
    }

    [Theory]
    [InlineData(new[] { 0, 2, 2, 5 }, new[] { 1, 0, 0, 1 })] // 同じ (Index, Length) が 2 回=Locate は最後
    [InlineData(new[] { 0, 2, 2, 5 }, new[] { 1, 0, 1, 1 })] // 同じ Index で長さ違い
    [InlineData(new[] { 0, 3, 1, 5 }, new[] { 1, 2, 2, 0 })] // 減少(startat より前のマッチ)=FindPrev は break 規則
    [InlineData(new[] { 4, 1 }, new[] { 0, 0 })]
    public void Non_monotone_tables_fall_back_to_linear_rules(int[] starts, int[] lengths)
    {
        Assert.False(new MatchPositions(starts, lengths).IsStrictlyIncreasingForTest);
        AssertMatchesReference(starts, lengths);
    }

    // ---- 戦略(実際の正規表現で、旧実装と比べる) ----

    private static readonly SearchOptions[] Conditions =
    [
        new("a", MatchCase: true),
        new("aa", MatchCase: true),
        new("ab"),
        new("ab", WholeWord: true),
        new("😀", MatchCase: true),
        new("a*", UseRegex: true),
        new("b*", UseRegex: true),
        new(@"\b", UseRegex: true),
        new("(?=a)", UseRegex: true),
        new("a|ab", UseRegex: true),
        new("[ab]+?", UseRegex: true),
        new("$", UseRegex: true),
        new("(?m)^", UseRegex: true),
        new(@"\r?\n", UseRegex: true),
        new(".", UseRegex: true),
        new("(?:b(?!a)+?)*", UseRegex: true), // Match(text, startat) が startat より前を返す例(TextSearcher の doc)
    ];

    private static string RandomText(Random rnd)
    {
        string[] parts = ["a", "b", "A", " ", "\r", "\n", "😀"];
        var sb = new System.Text.StringBuilder();
        int n = rnd.Next(0, 10);
        for (int i = 0; i < n; i++)
        {
            sb.Append(parts[rnd.Next(parts.Length)]);
        }
        return sb.ToString();
    }

    [Fact]
    public void Strategy_matches_old_implementation_for_random_texts()
    {
        var rnd = new Random(925);
        foreach (var opts in Conditions)
        {
            for (int t = 0; t < 60; t++)
            {
                string text = RandomText(rnd);
                var snap = TextBuffer.FromString(text).Current;
                var reference = new TextSearcher(opts);
                var s = new MaterializedSearchStrategy(new TextSearcher(opts));

                Assert.Equal(reference.Count(text), s.Count(snap)); // 未構築(Regex.Count)
                for (int before = 1; before <= text.Length + 2; before++)
                {
                    Assert.Equal(reference.FindPrev(text, before), s.FindPrev(snap, before));
                }
                for (int st = -1; st <= text.Length + 1; st++)
                {
                    for (int l = 0; l <= 3; l++)
                    {
                        Assert.Equal(
                            reference.Locate(text, new(st, l)),
                            s.Locate(snap, new(st, l))
                        );
                    }
                }
                Assert.Equal(reference.Count(text), s.Count(snap)); // 構築済み(表の件数)
            }
        }
    }

    /// <summary>
    /// 正規表現モード・単語単位なしの照合条件に限り、<see cref="TextSearcher"/> と同じ Regex を作る
    /// (<c>CultureInvariant</c>+大小無視なら <c>IgnoreCase</c>。それ以外の条件は対象外)。
    /// </summary>
    private static Regex ReferenceRegex(SearchOptions o)
    {
        Assert.True(o.UseRegex && !o.WholeWord); // 前提: パターンがそのまま Regex 本体になる条件だけ
        var ro = RegexOptions.CultureInvariant;
        if (!o.MatchCase)
        {
            ro |= RegexOptions.IgnoreCase;
        }
        return new Regex(o.Pattern, ro, TimeSpan.FromSeconds(1));
    }

    private static void AssertCollectMatchesEqualsMatches(SearchOptions o, string text)
    {
        var expected = ReferenceRegex(o).Matches(text).Select(m => (m.Index, m.Length)).ToArray();
        var positions = new TextSearcher(o).CollectMatches(text, int.MaxValue);
        Assert.NotNull(positions);
        var starts = positions.StartsForTest.ToArray();
        var lengths = positions.LengthsForTest.ToArray();
        var actual = starts.Zip(lengths).ToArray();
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// 表の構築(<c>EnumerateMatches</c>)が <c>Matches</c> と同じ (Index, Length) の列を同じ順序で返す
    /// (I-1: MatchCollection の Match 保持によるメモリのピークを避けるため列挙を差し替えた)。
    /// startat より前のマッチを返す病的パターンとゼロ幅パターンを含む。
    /// </summary>
    [Fact]
    public void CollectMatches_yields_same_sequence_as_Matches()
    {
        var pathological = new SearchOptions("(?:b(?!a)+?)*", UseRegex: true); // IgnoreCase
        foreach (var text in new[] { "abbbb", "bab", "", "babbab", "BBaB" })
        {
            AssertCollectMatchesEqualsMatches(pathological, text);
        }

        SearchOptions[] conditions =
        [
            pathological,
            new("a*", UseRegex: true),
            new(@"\b", UseRegex: true),
            new("(?=a)", UseRegex: true),
            new("a|ab", MatchCase: true, UseRegex: true),
        ];
        var rnd = new Random(20260925);
        foreach (var o in conditions)
        {
            for (int t = 0; t < 100; t++)
            {
                AssertCollectMatchesEqualsMatches(o, RandomText(rnd));
            }
        }
    }

    [Fact]
    public void Overlapping_candidates_follow_matches_not_match_at()
    {
        // "aaa" を "aa" で探すと Matches は (0,2) だけ。(1,2) は Match(text, 1) なら返るがヒットではない。
        var snap = TextBuffer.FromString("aaa").Current;
        var s = new MaterializedSearchStrategy(
            new TextSearcher(new SearchOptions("aa", MatchCase: true))
        );
        Assert.Equal((1, 1), s.Locate(snap, new(0, 2)));
        Assert.Null(s.Locate(snap, new(1, 2)));
        Assert.Equal(new MatchSpan(0, 2), s.FindPrev(snap, 3));
    }

    [Fact]
    public void Count_does_not_build_table_but_uses_it_once_built()
    {
        var buffer = TextBuffer.FromString("ab ab ab");
        var s = new MaterializedSearchStrategy(
            new TextSearcher(new SearchOptions("ab", MatchCase: true))
        );

        Assert.Equal(3, s.Count(buffer.Current));
        Assert.Equal(0, s.BuildCountForTest); // Count は構築を始めない(M-5 を悪化させない)

        Assert.Equal((2, 3), s.Locate(buffer.Current, new(3, 2)));
        Assert.Equal(1, s.BuildCountForTest);
        Assert.True(s.HasPositionsForTest);
        Assert.Equal(new MatchSpan(3, 2), s.FindPrev(buffer.Current, 6));
        Assert.Equal(3, s.Count(buffer.Current));
        Assert.Equal(1, s.BuildCountForTest); // 同じスナップショットでは作り直さない

        buffer.Insert(8, " ab");
        Assert.Equal(4, s.Count(buffer.Current)); // 古い表の件数を返さない
        Assert.Equal(1, s.BuildCountForTest); // Count は新しいスナップショットでも構築しない
        Assert.Equal((4, 4), s.Locate(buffer.Current, new(9, 2)));
        Assert.Equal(2, s.BuildCountForTest);
    }

    [Theory]
    [InlineData(2, false)] // 3 件 > 上限 2 → 表を作らない
    [InlineData(3, true)] // 3 件 = 上限 3 → 作る
    public void Table_is_not_built_beyond_limit(int limit, bool built)
    {
        var snap = TextBuffer.FromString("a a a").Current;
        var s = new MaterializedSearchStrategy(
            new TextSearcher(new SearchOptions("a", MatchCase: true)),
            new SnapshotTextCache(),
            maxCachedMatches: limit
        );

        Assert.Equal((2, 3), s.Locate(snap, new(2, 1))); // どちらでも答えは同じ
        Assert.Equal(new MatchSpan(2, 1), s.FindPrev(snap, 4));
        Assert.Equal(built, s.HasPositionsForTest);
        Assert.Equal(1, s.BuildCountForTest); // 失敗も記憶する=作り直しを試みない
    }

    [Fact]
    public void Timeout_while_building_falls_back_to_old_path()
    {
        // "xyx" の後ろに破滅的なバックトラックを起こす区間を置く。旧実装の FindPrev(2) は
        // 3 件目の x(Index 2 >= 2)で break するので、その区間を照合しない=成功する。
        // 表の構築は全件を列挙するので、その区間でタイムアウトする。
        var opts = new SearchOptions("x|y|(a|aa)+$", UseRegex: true);
        string text = "xyx" + new string('a', 40) + "b";
        var timeout = TimeSpan.FromMilliseconds(50);
        // 前提: 全件の列挙がタイムアウトすること(しなければテストの前提違い=パターンを選び直す)
        Assert.Throws<RegexMatchTimeoutException>(() =>
            new TextSearcher(opts, timeout).Count(text)
        );
        Assert.Equal(new MatchSpan(1, 1), new TextSearcher(opts, timeout).FindPrev(text, 2));

        var snap = TextBuffer.FromString(text).Current;
        var s = new MaterializedSearchStrategy(new TextSearcher(opts, timeout));

        Assert.Equal(new MatchSpan(1, 1), s.FindPrev(snap, 2)); // 従来の経路で答える
        Assert.False(s.HasPositionsForTest);
        Assert.Throws<RegexMatchTimeoutException>(() => s.Locate(snap, new(0, 1))); // 伝播も従来どおり
        Assert.Equal(1, s.BuildCountForTest); // タイムアウトも記憶する
    }
}
