using Xunit;
using yEdit.Core.Buffers;
using yEdit.Core.Search;

namespace yEdit.Core.Tests.Search;

/// <summary>
/// P6 Task 11: <see cref="SnapshotSearcher"/> の閾値二層化テスト。
/// 閾値以下 (=<see cref="TextSearcher"/> 委譲)と閾値超(=窓照合/行単位 regex)を
/// 同一データで叩き、結果一致=既存挙動 100% 一致 と 大容量経路の正しさを検証する。
/// 閾値・窓サイズはコンストラクタ注入するため、テスト間で共有状態を持たない。
/// </summary>
public class SnapshotSearcherTests
{
    /// <summary>既定コンストラクタ(本番用の 32M chars 閾値)。閾値以下経路のテスト用。</summary>
    private static SnapshotSearcher Make(
        string pattern,
        bool matchCase = false,
        bool wholeWord = false,
        bool useRegex = false
    ) => new(new SearchOptions(pattern, matchCase, wholeWord, useRegex));

    /// <summary>閾値・窓サイズをテスト用に小さくした SnapshotSearcher。閾値超経路のテスト用。</summary>
    private static SnapshotSearcher MakeLarge(
        string pattern,
        bool matchCase = false,
        bool wholeWord = false,
        bool useRegex = false,
        int threshold = 4,
        int window = 8
    ) => new(new SearchOptions(pattern, matchCase, wholeWord, useRegex), threshold, window);

    private static TextSnapshot Snap(string text) => TextBuffer.FromString(text).Current;

    // ==============================
    // 閾値以下 = TextSearcher と完全一致
    // ==============================

    [Fact]
    public void BelowThreshold_delegates_to_TextSearcher_for_all_apis()
    {
        var snap = Snap("ab ab ab");
        var s = Make("ab");
        Assert.Equal(3, s.Count(snap));
        Assert.Equal(new MatchSpan(0, 2), s.FindNext(snap, 0));
        Assert.Equal(new MatchSpan(3, 2), s.FindPrev(snap, 6));
        Assert.Equal((2, 3), s.Locate(snap, new MatchSpan(3, 2)));
        Assert.Equal("X", s.ReplacementAt(snap, new MatchSpan(0, 2), "X"));
        var (fragment, count) = s.ReplaceInRange(snap, 0, snap.CharLength, "X");
        Assert.Equal("X X X", fragment);
        Assert.Equal(3, count);
    }

    // ==============================
    // Count: 閾値超で挙動一致(リテラル/IgnoreCase/WholeWord/regex 行内)
    // ==============================

    [Fact]
    public void Count_LiteralAboveThreshold_matches_below()
    {
        var snap = Snap("ab ab ab ab ab");
        var below = Make("ab", matchCase: true);
        var above = MakeLarge("ab", matchCase: true, threshold: 4, window: 6);
        Assert.Equal(below.Count(snap), above.Count(snap));
    }

    [Fact]
    public void Count_LiteralIgnoreCaseAboveThreshold_matches_below()
    {
        var snap = Snap("ab AB Ab ab");
        var below = Make("ab"); // MatchCase=false (default)
        var above = MakeLarge("ab", threshold: 4, window: 6);
        Assert.Equal(below.Count(snap), above.Count(snap));
    }

    [Fact]
    public void Count_WholeWordAboveThreshold_matches_below()
    {
        var snap = Snap("cat category cat-dog cat");
        var below = Make("cat", matchCase: true, wholeWord: true);
        var above = MakeLarge("cat", matchCase: true, wholeWord: true, threshold: 4, window: 6);
        Assert.Equal(below.Count(snap), above.Count(snap));
    }

    [Fact]
    public void Count_RegexInsideLineAboveThreshold_matches_below()
    {
        var snap = Snap("a1 a2 a3\nb1 b2\nc9 c8 c7");
        var below = Make(@"[abc]\d", useRegex: true, matchCase: true);
        var above = MakeLarge(@"[abc]\d", useRegex: true, matchCase: true, threshold: 4, window: 6);
        Assert.Equal(below.Count(snap), above.Count(snap));
    }

    // ==============================
    // FindNext / FindPrev: 閾値超で挙動一致
    // ==============================

    [Fact]
    public void FindNext_LiteralAboveThreshold_matches_below()
    {
        var snap = Snap("hello world wonderful world");
        var below = Make("world", matchCase: true);
        var above = MakeLarge("world", matchCase: true, threshold: 4, window: 6);
        Assert.Equal(below.FindNext(snap, 0), above.FindNext(snap, 0));
        // from が既ヒット末尾を超えた場合も一致
        Assert.Equal(new MatchSpan(22, 5), above.FindNext(snap, 12));
    }

    [Fact]
    public void FindPrev_LiteralAboveThreshold_matches_below()
    {
        var snap = Snap("ab XY ab XY ab");
        var below = Make("ab", matchCase: true);
        var above = MakeLarge("ab", matchCase: true, threshold: 4, window: 6);
        Assert.Equal(below.FindPrev(snap, 12), above.FindPrev(snap, 12));
        Assert.Equal(new MatchSpan(0, 2), above.FindPrev(snap, 3));
        Assert.Null(above.FindPrev(snap, 0));
    }

    [Fact]
    public void FindNext_RegexInsideLineAboveThreshold_matches_below()
    {
        var snap = Snap("line1: abc123\nline2: def456\nline3: xyz789");
        var below = Make(@"\d+", useRegex: true, matchCase: true);
        var above = MakeLarge(@"\d+", useRegex: true, matchCase: true, threshold: 4, window: 6);
        Assert.Equal(below.FindNext(snap, 0), above.FindNext(snap, 0));
        Assert.Equal(below.FindNext(snap, 15), above.FindNext(snap, 15));
    }

    // ==============================
    // 改行跨ぎ regex = 閾値超で「見つからない」契約(壊れる契約§2-8)
    // ==============================

    [Fact]
    public void Regex_CrossLine_pattern_matches_below_but_never_matches_above()
    {
        var snap = Snap("foo\nbar\nfoo\nbar");
        var below = Make(@"foo\r?\nbar", useRegex: true, matchCase: true);
        var above = MakeLarge(
            @"foo\r?\nbar",
            useRegex: true,
            matchCase: true,
            threshold: 4,
            window: 6
        );

        // 閾値以下(TextSearcher)は改行またぎ regex を検出できる
        Assert.NotNull(below.FindNext(snap, 0));

        // 閾値超(行単位 regex)は改行またぎヒットを絶対に返さない=壊れる契約
        Assert.Null(above.FindNext(snap, 0));
        Assert.Equal(0, above.Count(snap));
    }

    // ==============================
    // Locate / ReplacementAt / ReplaceInRange
    // ==============================

    [Fact]
    public void Locate_LiteralAboveThreshold_matches_below()
    {
        var snap = Snap("ab XY ab XY ab");
        var below = Make("ab", matchCase: true);
        var above = MakeLarge("ab", matchCase: true, threshold: 4, window: 6);
        var span = new MatchSpan(6, 2);
        Assert.Equal(below.Locate(snap, span), above.Locate(snap, span));
        // 該当なし= null
        Assert.Null(above.Locate(snap, new MatchSpan(1, 2)));
    }

    [Fact]
    public void ReplacementAt_LiteralAboveThreshold_matches_below()
    {
        var snap = Snap("ab XY ab XY ab");
        var s = MakeLarge("ab", matchCase: true, threshold: 4, window: 6);
        Assert.Equal("REPL", s.ReplacementAt(snap, new MatchSpan(6, 2), "REPL"));
        // ヒットでない場所は null
        Assert.Null(s.ReplacementAt(snap, new MatchSpan(3, 2), "X"));
        // リテラルは $ 展開なし
        Assert.Equal("X$1", s.ReplacementAt(snap, new MatchSpan(0, 2), "X$1"));
    }

    [Fact]
    public void ReplacementAt_RegexAboveThreshold_expands_groups_per_line()
    {
        var snap = Snap("ab AB\nab cd");
        var below = Make("(a)(b)", useRegex: true, matchCase: true);
        var above = MakeLarge("(a)(b)", useRegex: true, matchCase: true, threshold: 4, window: 6);
        Assert.Equal("ba", below.ReplacementAt(snap, new MatchSpan(0, 2), "$2$1"));
        Assert.Equal("ba", above.ReplacementAt(snap, new MatchSpan(0, 2), "$2$1"));
    }

    [Fact]
    public void ReplaceInRange_LiteralAboveThreshold_matches_below()
    {
        var snap = Snap("ab_ab_ab_ab");
        var below = Make("ab", matchCase: true);
        var above = MakeLarge("ab", matchCase: true, threshold: 4, window: 6);
        var (belowFrag, belowCount) = below.ReplaceInRange(snap, 0, snap.CharLength, "X");
        var (aboveFrag, aboveCount) = above.ReplaceInRange(snap, 0, snap.CharLength, "X");
        Assert.Equal(belowFrag, aboveFrag);
        Assert.Equal(belowCount, aboveCount);
    }

    [Fact]
    public void ReplaceInRange_LiteralExcludesStraddlingBoundary_above_threshold()
    {
        // [0,5) には index 0 と index 3 の "ab" が完全包含 → 2 件置換
        var snap = Snap("ab_ab_ab");
        var s = MakeLarge("ab", matchCase: true, threshold: 4, window: 6);
        var (frag, count) = s.ReplaceInRange(snap, 0, 5, "X");
        Assert.Equal("X_X", frag);
        Assert.Equal(2, count);
    }

    [Fact]
    public void ReplaceInRange_RegexPerLine_above_threshold_preserves_line_breaks_and_non_target_lines()
    {
        var snap = Snap("aaa\nbbb\naaa");
        var below = Make("a", useRegex: true, matchCase: true);
        var above = MakeLarge("a", useRegex: true, matchCase: true, threshold: 4, window: 6);
        var (belowFrag, belowCount) = below.ReplaceInRange(snap, 0, snap.CharLength, "X");
        var (aboveFrag, aboveCount) = above.ReplaceInRange(snap, 0, snap.CharLength, "X");
        Assert.Equal(belowFrag, aboveFrag);
        Assert.Equal(belowCount, aboveCount);
    }

    // ==============================
    // C-1 回帰: partial-range regex ReplaceInRange は Fragment に範囲外を混入しない
    // ==============================

    [Fact]
    public void ReplaceInRange_RegexPartialRange_singleLine_matches_below()
    {
        // 単一行の途中で切った範囲 [0,5) は "ab_ab" のみ対象。前置/後置に範囲外文字が
        // 混入していた C-1 バグ(閾値超 regex 経路のみ)の回帰テスト。
        var snap = Snap("ab_ab_ab");
        var below = Make("ab", useRegex: true, matchCase: true);
        var above = MakeLarge("ab", useRegex: true, matchCase: true, threshold: 4, window: 6);
        var (belowFrag, belowCount) = below.ReplaceInRange(snap, 0, 5, "X");
        var (aboveFrag, aboveCount) = above.ReplaceInRange(snap, 0, 5, "X");
        Assert.Equal(belowFrag, aboveFrag); // = "X_X"
        Assert.Equal(belowCount, aboveCount); // = 2
    }

    [Fact]
    public void ReplaceInRange_RegexPartialRange_multiLine_matches_below()
    {
        // 複数行にまたがる範囲 [3, snap.CharLength - 2) を切る。
        // 行内 substring [rangeInLineStart, rangeInLineEnd) の中身のみが Fragment
        // に入る契約=行外(左端の前3文字・右端の後2文字)は Fragment に混入しない。
        var snap = Snap("abcde\nfghij\nklmno"); // 5+1+5+1+5 = 17
        int start = 3,
            end = snap.CharLength - 2; // = 15
        var below = Make("[a-z]", useRegex: true, matchCase: true);
        var above = MakeLarge("[a-z]", useRegex: true, matchCase: true, threshold: 4, window: 6);
        var (belowFrag, belowCount) = below.ReplaceInRange(snap, start, end - start, "X");
        var (aboveFrag, aboveCount) = above.ReplaceInRange(snap, start, end - start, "X");
        Assert.Equal(belowFrag, aboveFrag);
        Assert.Equal(belowCount, aboveCount);
    }

    // ==============================
    // 端点 / 空 / IsValid
    // ==============================

    [Fact]
    public void EmptySnapshot_does_not_throw_and_yields_no_hits()
    {
        // 空 snap は CharLength=0 なので閾値 0 でも IsLarge=false=下位経路。
        // 空 snap を上位経路で扱うことはそもそもありえない(threshold は非負)。
        // 本テストは全 API が空 snap に対して例外を投げないことを確認する。
        var snap = Snap("");
        var s = MakeLarge("ab", threshold: 0, window: 4);
        Assert.Equal(0, s.Count(snap));
        Assert.Null(s.FindNext(snap, 0));
        Assert.Null(s.FindPrev(snap, 0));
        var (frag, count) = s.ReplaceInRange(snap, 0, 0, "X");
        Assert.Equal("", frag);
        Assert.Equal(0, count);
    }

    [Fact]
    public void MinimalNonEmptySnapshot_uses_above_path_with_zero_threshold_and_does_not_throw()
    {
        // 最小の non-empty snap で「上位経路が確実に走る」ことを確認(threshold=0 → 1>0 で IsLarge=true)。
        var snap = Snap("a");
        var s = MakeLarge("z", threshold: 0, window: 4);
        Assert.Equal(0, s.Count(snap));
        Assert.Null(s.FindNext(snap, 0));
        Assert.Null(s.FindPrev(snap, snap.CharLength));
        var (frag, count) = s.ReplaceInRange(snap, 0, snap.CharLength, "X");
        Assert.Equal("a", frag);
        Assert.Equal(0, count);
    }

    [Fact]
    public void InvalidRegex_returns_null_and_zero_above_threshold()
    {
        var snap = Snap("hello world");
        var s = MakeLarge("(", useRegex: true, threshold: 4, window: 6);
        Assert.False(s.IsValid);
        Assert.Equal(0, s.Count(snap));
        Assert.Null(s.FindNext(snap, 0));
    }

    [Fact]
    public void FindNext_ClampsNegativeFrom_above_threshold()
    {
        var snap = Snap("ab ab ab");
        var s = MakeLarge("ab", matchCase: true, threshold: 4, window: 6);
        Assert.Equal(new MatchSpan(0, 2), s.FindNext(snap, -5));
    }

    [Fact]
    public void FindNext_PastEnd_returns_null_above_threshold()
    {
        var snap = Snap("ab");
        var s = MakeLarge("ab", matchCase: true, threshold: 1, window: 4);
        Assert.Null(s.FindNext(snap, snap.CharLength));
    }

    // ==============================
    // 窓境界を跨ぐヒットの取りこぼしなし(overlap = plen - 1)
    // ==============================

    [Fact]
    public void FindNext_LiteralAcrossWindowBoundary_still_hits_above_threshold()
    {
        // window=6 でパターン長=4 のとき、overlap=3 で境界跨ぎを拾えるかの検証
        var snap = Snap("xxxx1234xxx");
        var below = Make("1234", matchCase: true);
        var above = MakeLarge("1234", matchCase: true, threshold: 4, window: 6);
        Assert.Equal(below.FindNext(snap, 0), above.FindNext(snap, 0));
    }

    // ==============================
    // 閾値境界 (CharLength == thresholdChars ちょうど)
    // ==============================

    [Fact]
    public void AtExactThreshold_uses_below_path_not_above()
    {
        // 契約: IsLarge は `CharLength > thresholdChars`。ちょうど一致は「閾値以下」= 材質化経路。
        // 経路差が観測できる形として改行跨ぎ regex を使う(材質化経路だけがヒットする)。
        // このテストが無いと `>` → `>=` の変異が既存テストを全て生き延びる
        // (空文書だけが境界に当たるが、空文書は両経路とも同じ値を返すため差が出ない)。
        var snap = Snap("ab\ncd"); // CharLength == 5
        Assert.Equal(5, snap.CharLength);

        var s = MakeLarge(@"b\nc", useRegex: true, matchCase: true, threshold: 5, window: 6);

        // 閾値以下経路 = 文書全体をひとつの入力として regex 適用 → 改行を跨いでヒットする。
        // 6 つのディスパッチ箇所すべてを個別に押さえる(1 経路だけ条件を書き損じても
        // 検出できるようにする)。
        Assert.Equal(1, s.Count(snap));
        Assert.Equal(new MatchSpan(1, 3), s.FindNext(snap, 0));
        Assert.Equal(new MatchSpan(1, 3), s.FindPrev(snap, 5));
        Assert.Equal((1, 1), s.Locate(snap, new MatchSpan(1, 3)));
        Assert.Equal("X", s.ReplacementAt(snap, new MatchSpan(1, 3), "X"));
        var (frag, count) = s.ReplaceInRange(snap, 0, 5, "X");
        Assert.Equal("aXd", frag); // "ab\ncd" の [1,4) が置換される
        Assert.Equal(1, count);
    }

    [Fact]
    public void OneCharAboveThreshold_uses_above_path()
    {
        // 境界の反対側。閾値 +1 文字で行単位経路へ切り替わり、改行跨ぎは取れなくなる。
        // AtExactThreshold_uses_below_path_not_above と対で境界を挟む。
        var snap = Snap("ab\ncd"); // CharLength == 5
        var s = MakeLarge(@"b\nc", useRegex: true, matchCase: true, threshold: 4, window: 6);

        // 否定 assert だけだと「正しく見つからない」と「パターンが壊れて何も動いていない」を
        // 区別できないため、まず照合条件が生きていることを確かめる。
        Assert.True(s.IsValid);

        // 閾値超経路は 6 API すべてで改行跨ぎヒットを返さない。
        Assert.Equal(0, s.Count(snap));
        Assert.Null(s.FindNext(snap, 0));
        Assert.Null(s.FindPrev(snap, 5));
        Assert.Null(s.Locate(snap, new MatchSpan(1, 3)));
        Assert.Null(s.ReplacementAt(snap, new MatchSpan(1, 3), "X"));
        var (frag, count) = s.ReplaceInRange(snap, 0, 5, "X");
        Assert.Equal("ab\ncd", frag); // 置換ゼロ=元の範囲がそのまま返る(改行も復元される)
        Assert.Equal(0, count);
    }

    // ==============================
    // ゼロ幅ヒット (a* / \b / (?=...) 系) の網
    // ==============================

    [Fact(Timeout = 10000)]
    public async Task Locate_RegexZeroWidthHits_terminates_and_counts_above_threshold()
    {
        // 閾値超 regex 経路の LocateRegexPerLine は行内を FindNext で歩進するため、
        // ゼロ幅ヒットで前進しないと無限ループになる(TextSearcher の契約=呼び出し側が
        // max(1, Length) 分進める)。前進ロジックを壊すとハングするので Timeout を付ける。
        // xUnit v2 の Timeout は async テストにしか適用できない
        // ("Tests marked with Timeout are only supported for async tests")ため、
        // 中身は同期のまま Task.Yield() で async 化している。
        await Task.Yield();

        var snap = Snap("a\nb"); // CharLength == 3 / 行 0 = "a" (ls=0) / 行 1 = "b" (ls=2)
        var s = MakeLarge("a*", useRegex: true, matchCase: true, threshold: 2, window: 4);
        Assert.True(s.IsValid);

        // 行 0 "a": off=0 → (0,1) / off=1 → (1,0) / off=2 > 行長 1 で打ち切り = 2 件
        // 行 1 "b": off=0 → (0,0) / off=1 → (1,0) / off=2 > 行長 1 で打ち切り = 2 件
        // → total = 4。行内 offset は行頭 ls を足して絶対位置になる。
        Assert.Equal((1, 4), s.Locate(snap, new MatchSpan(0, 1))); // 行 0 の "a"
        Assert.Equal((2, 4), s.Locate(snap, new MatchSpan(1, 0))); // 行 0 の行末ゼロ幅
        Assert.Equal((3, 4), s.Locate(snap, new MatchSpan(2, 0))); // 行 1 の行頭ゼロ幅
        Assert.Equal((4, 4), s.Locate(snap, new MatchSpan(3, 0))); // 行 1 の行末ゼロ幅
        Assert.Null(s.Locate(snap, new MatchSpan(2, 1))); // ヒットでない span
    }

    // ==============================
    // 閾値以下の端点入力 (既存は閾値超しか固定していない)
    // ==============================

    [Fact]
    public void FindNext_ClampsNegativeFrom_below_threshold()
    {
        var snap = Snap("ab ab ab");
        var s = Make("ab", matchCase: true);
        Assert.Equal(new MatchSpan(0, 2), s.FindNext(snap, -5));
    }

    [Fact]
    public void FindNext_PastEnd_returns_null_below_threshold()
    {
        var snap = Snap("ab");
        var s = Make("ab", matchCase: true);
        Assert.Null(s.FindNext(snap, snap.CharLength + 1));
    }

    [Fact]
    public void FindPrev_AtOrBeforeZero_returns_null_below_threshold()
    {
        var snap = Snap("ab ab");
        var s = Make("ab", matchCase: true);
        Assert.Null(s.FindPrev(snap, 0));
        Assert.Null(s.FindPrev(snap, -3));
    }

    [Fact]
    public void FindPrev_BeforePastEnd_is_not_clamped_below_threshold()
    {
        // 現行仕様: 閾値以下経路は before を生のまま TextSearcher へ渡す
        // (Math.Min(before, CharLength) のクランプは閾値超経路にしか掛かっていない)。
        // ゼロ幅ヒットを生むパターンでは「文書長超の before」と「文書長ちょうどの before」で
        // 結果が変わるため、位置引数のクランプをファサードへ集約すると挙動が変わる。
        // その挙動差をここで凍結する。
        var snap = Snap("ab"); // CharLength == 2
        var s = Make("b*", useRegex: true, matchCase: true);
        Assert.True(s.IsValid);

        // "ab" 上の b* のヒットは (0,0) / (1,1) / (2,0) の 3 件。
        // before = CharLength + 1 → 文書末尾のゼロ幅ヒット (2,0) まで到達する
        Assert.Equal(new MatchSpan(2, 0), s.FindPrev(snap, snap.CharLength + 1));
        // before = CharLength → Start=2 は「厳密に前」でないので対象外 → 直前の "b" を返す
        Assert.Equal(new MatchSpan(1, 1), s.FindPrev(snap, snap.CharLength));
    }

    [Fact]
    public void ReplaceInRange_ClampsOutOfRangeArgs_below_threshold()
    {
        var snap = Snap("ab_ab");
        var s = Make("ab", matchCase: true);
        // start が負・length が文書長を超える → 文書全体へクランプされる
        var (frag, count) = s.ReplaceInRange(snap, -10, 999, "X");
        Assert.Equal("X_X", frag);
        Assert.Equal(2, count);
    }
}
