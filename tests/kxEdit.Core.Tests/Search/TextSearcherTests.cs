using System;
using System.Threading.Tasks;
using kxEdit.Core.Search;
using Xunit;

namespace kxEdit.Core.Tests.Search;

public class TextSearcherTests
{
    private static TextSearcher Make(
        string pattern,
        bool matchCase = false,
        bool wholeWord = false,
        bool useRegex = false
    ) => new(new SearchOptions(pattern, matchCase, wholeWord, useRegex));

    // ----- Count（照合件数） -----

    [Fact]
    public void Count_literal_is_case_insensitive_by_default() =>
        Assert.Equal(3, Make("ab").Count("ab AB Ab xy"));

    [Fact]
    public void Count_with_match_case_distinguishes() =>
        Assert.Equal(1, Make("ab", matchCase: true).Count("ab AB Ab xy"));

    [Fact]
    public void Count_whole_word_excludes_substrings() =>
        Assert.Equal(2, Make("cat", wholeWord: true).Count("cat category cat-dog"));

    [Fact]
    public void Count_literal_treats_meta_chars_literally()
    {
        // リテラルモードでは "a.b" は正規表現メタでなくドットそのもの。
        Assert.Equal(1, Make("a.b").Count("a.b axb a-b"));
    }

    [Fact]
    public void Count_regex_mode_interprets_meta_chars()
    {
        // 正規表現モードでは "a.b" の "." は任意 1 文字。
        Assert.Equal(3, Make("a.b", useRegex: true).Count("a.b axb a-b"));
    }

    // ----- 妥当性（不正な正規表現・空パターン） -----

    [Fact]
    public void Invalid_regex_is_not_valid_and_reports_error()
    {
        var s = Make("(", useRegex: true);
        Assert.False(s.IsValid);
        Assert.NotNull(s.Error);
        Assert.Equal(0, s.Count("any text"));
    }

    [Fact]
    public void Empty_pattern_is_not_valid()
    {
        var s = Make("");
        Assert.False(s.IsValid);
        Assert.NotNull(s.Error);
    }

    // ----- FindNext（from 以降・折り返しなし） -----

    [Fact]
    public void FindNext_returns_first_hit_at_or_after_from()
    {
        var s = Make("ab");
        Assert.Equal(new MatchSpan(0, 2), s.FindNext("ab ab ab", 0));
        Assert.Equal(new MatchSpan(3, 2), s.FindNext("ab ab ab", 1));
        Assert.Equal(new MatchSpan(3, 2), s.FindNext("ab ab ab", 3));
    }

    [Fact]
    public void FindNext_does_not_wrap_and_returns_null_past_end()
    {
        var s = Make("ab");
        Assert.Null(s.FindNext("ab ab ab", 7));
    }

    [Fact]
    public void FindNext_clamps_negative_from_to_zero() =>
        Assert.Equal(new MatchSpan(0, 2), Make("ab").FindNext("ab ab", -5));

    // ----- FindPrev（厳密前・折り返しなし） -----

    [Fact]
    public void FindPrev_returns_last_hit_strictly_before()
    {
        var s = Make("ab");
        Assert.Equal(new MatchSpan(0, 2), s.FindPrev("ab ab ab", 3));
        Assert.Equal(new MatchSpan(3, 2), s.FindPrev("ab ab ab", 6));
    }

    [Fact]
    public void FindPrev_is_strict_and_excludes_hit_at_before() =>
        Assert.Equal(new MatchSpan(0, 2), Make("ab").FindPrev("ab ab ab", 3));

    [Fact]
    public void FindPrev_returns_null_when_no_hit_before() =>
        Assert.Null(Make("ab").FindPrev("ab ab ab", 0));

    // ----- CJK オフセットは UTF-16 文字位置 -----

    [Fact]
    public void Offsets_are_utf16_char_positions_for_cjk() =>
        Assert.Equal(new MatchSpan(5, 2), Make("世界").FindNext("こんにちは世界", 0));

    // ----- Locate（何件目か・1始まり） -----

    [Fact]
    public void Locate_returns_one_based_ordinal_and_total() =>
        Assert.Equal((2, 3), Make("ab").Locate("ab ab ab", new MatchSpan(3, 2)));

    [Fact]
    public void Locate_returns_null_when_span_is_not_a_hit() =>
        Assert.Null(Make("ab").Locate("ab ab ab", new MatchSpan(1, 2)));

    // ----- ReplacementAt（当該ヒットの置換後文字列） -----

    [Fact]
    public void ReplacementAt_literal_does_not_expand_dollar()
    {
        // リテラルモードでは "X$1" は素のまま返る。
        Assert.Equal("X$1", Make("ab").ReplacementAt("ab", new MatchSpan(0, 2), "X$1"));
    }

    [Fact]
    public void ReplacementAt_regex_expands_groups()
    {
        // 正規表現モードでは $2$1 が展開される。
        var s = Make("(a)(b)", useRegex: true);
        Assert.Equal("ba", s.ReplacementAt("ab", new MatchSpan(0, 2), "$2$1"));
    }

    [Fact]
    public void ReplacementAt_returns_null_when_span_is_not_a_hit() =>
        Assert.Null(Make("ab").ReplacementAt("ab", new MatchSpan(1, 1), "X"));

    // ----- ReplaceAll（全文置換） -----

    [Fact]
    public void ReplaceAll_replaces_every_hit()
    {
        var (fragment, count) = Make("ab").ReplaceAll("ab_ab_ab", "X");
        Assert.Equal("X_X_X", fragment);
        Assert.Equal(3, count);
    }

    [Fact]
    public void ReplaceAll_regex_expands_groups()
    {
        var (fragment, count) = Make("(a)(b)", useRegex: true).ReplaceAll("ab ab", "$2$1");
        Assert.Equal("ba ba", fragment);
        Assert.Equal(2, count);
    }

    // ----- ReplaceInRange（範囲内に完全に収まるヒットのみ） -----

    [Fact]
    public void ReplaceInRange_replaces_only_hits_fully_inside_range()
    {
        // [0,5) には index0 と index3 の "ab" が収まる。
        var (fragment, count) = Make("ab").ReplaceInRange("ab_ab_ab", 0, 5, "X");
        Assert.Equal("X_X", fragment);
        Assert.Equal(2, count);
    }

    [Fact]
    public void ReplaceInRange_excludes_hit_straddling_end_boundary()
    {
        // [0,4) は index3 の "ab"(3-5) が範囲をまたぐため対象外。
        var (fragment, count) = Make("ab").ReplaceInRange("ab_ab_ab", 0, 4, "X");
        Assert.Equal("X_a", fragment);
        Assert.Equal(1, count);
    }

    [Fact]
    public void ReplaceInRange_excludes_hit_before_start()
    {
        // [3,8) は index0 の "ab" を対象外とし、index3 と index6 のみ置換。
        var (fragment, count) = Make("ab").ReplaceInRange("ab_ab_ab", 3, 5, "X");
        Assert.Equal("X_X", fragment);
        Assert.Equal(2, count);
    }

    [Fact]
    public void ReplaceInRange_clamps_out_of_range_arguments_without_throwing()
    {
        // 負の start・length 超過でも例外を投げず、text 範囲へクランプして全文を扱う。
        var (fragment, count) = Make("ab").ReplaceInRange("ab_ab", -3, 100, "X");
        Assert.Equal("X_X", fragment);
        Assert.Equal(2, count);
    }

    [Theory]
    // 全文置換(s == 0)でも**走査規則の変更(Matches → Match(text, scan) の再アンカー)に
    // ついては**挙動不変であること。メソッド名の "KeepsMatchesSemantics" もこの走査規則を指す。
    // <b>s == 0 なら何も変わらない、ではない</b>: ゼロ幅マッチが論理文字の内側に立つ場合は
    // ゼロ幅後退により意図的に変わる(_ZeroWidthInsideCrlf_RetreatsToBoundary が
    // まさに s == 0 の全文置換で "a\rX\nb" → "aX\r\nb" の変化を固定している)。
    // 期待値は**変更前の src での実測値**(計画の値ではない)。
    [InlineData("ab_ab_ab", "ab", false, "X", "X_X_X", 3)]
    [InlineData("aaaa", "aa", true, "X", "XX", 2)] // 非重複・左端優先
    [InlineData("abc", "x*", true, "-", "-a-b-c-", 4)] // ゼロ幅は各位置で 1 件
    // 空マッチと実マッチの混在。"b*" は 0(空)・1("b")・2(空)の 3 件で、
    // index1 の実マッチが 'b' を消費するので出力に 'b' は残らない。
    [InlineData("ab", "b*", true, "-", "-a--", 3)]
    [InlineData("a\r\nb", "\\r\\n", true, "X", "aXb", 1)] // CRLF 丸ごと
    [InlineData("aaa", "a", false, "", "", 3)] // 空置換
    public void ReplaceInRange_WholeText_KeepsMatchesSemantics(
        string text,
        string pattern,
        bool useRegex,
        string repl,
        string expected,
        int expectedCount
    )
    {
        var (fragment, count) = Make(pattern, useRegex: useRegex)
            .ReplaceInRange(text, 0, text.Length, repl);
        Assert.Equal(expected, fragment);
        Assert.Equal(expectedCount, count);
    }

    // ----- M-29: スコープ内での再照合(2026-09-01 B2 Task 2) -----

    [Fact]
    public void ReplaceInRange_ReMatchesInsideRange_WhenWholeTextHitEatsRangeStart()
    {
        // M-29: 文書全体の照合では [0,2) の "aa" しか出ず m.Index < 1 で捨てられ 0 件だった。
        // 範囲始端へ再アンカーすれば [1,3) の "aa" が見つかる。
        // 単発置換(FindNext)は Match(text, from) なので元から当たっており、両者が食い違っていた。
        var (fragment, count) = Make("aa").ReplaceInRange("aaa", 1, 2, "X");
        Assert.Equal("X", fragment);
        Assert.Equal(1, count);
    }

    [Fact]
    public void ReplaceInRange_ReAnchorDoesNotCutInputContext()
    {
        // Match(text, startat) は入力を切らないので \b は全文文脈で評価される
        // = 位置 1 は語中なので \b は成立しない。
        // 範囲を substring("aa") へ切って照合する実装なら 1 件になる
        // = この fixture は「再アンカー」と「substring 化」を弁別できる形の 1 つ。
        // 弁別できる形は他にもあり、逆向きにも作れる(実測: (?<=a)aa は再アンカーで 1 件・
        // substring 化で 0 件。substring 化では左文脈の 'a' が切り落とされて後読みが不成立)。
        // ただし<b>現に存在する網としてはこの 1 本だけ</b>=消すと弁別網がゼロになる。
        // 他の ReplaceInRange テストは s == 0 か、両実装で同じ答えになる形しかない。
        var (fragment, count) = Make(@"\baa", useRegex: true).ReplaceInRange("aaa", 1, 2, "X");
        Assert.Equal("aa", fragment);
        Assert.Equal(0, count);
    }

    // ----- 停止性: scan の前進をエンジンに賭けない(2026-09-01 B2 Task 2 脆弱性レビュー) -----

    [Theory]
    // Regex.Match(text, startat) は Index >= startat を保証しない(.NET 9.0.8 実測:
    // new Regex("(?:b(?!a)+?)*").Match("abbbb", 4) が Index=3 Length=2 を返す)。
    // scan = m.Index + ... を素直に受けると scan が据え置きになり、個々の Match は 0ms で
    // 返るため RegexMatchTimeoutException も出ないまま永久にスピンする(UI 完全凍結)。
    // 期待値は**実測値**。ゼロ幅後退と再アンカーを入れた時点で病的入力の出力は
    // 変更前(Matches ベース)と一致しない=一致は要求しない。要求は「止まること」と
    // 「断片が範囲の中身+置換だけであること」。
    [InlineData(@"(?:b(?!a)+?)*", "abbbb", 0, 5, "XabXXbX", 4)]
    [InlineData(@"(b(?!a)+?)*", "abb", 1, 2, "bbX", 1)]
    [InlineData(@"(a(?!b)+?)*", "aaa", 1, 2, "aaX", 1)]
    public async Task ReplaceInRange_PathologicalRegex_Terminates(
        string pattern,
        string text,
        int start,
        int length,
        string expected,
        int expectedCount
    )
    {
        // ハングするテストは CI を止めるので、待ち時間を assert する形にする
        // ([Fact(Timeout=...)] もスレッドを殺さないため、どちらにせよ観測用の網)。
        // Task.Wait / .Result は xUnit1031(-warnaserror でビルド不能)なので WhenAny で待つ。
        var t = Task.Run(() =>
            Make(pattern, useRegex: true).ReplaceInRange(text, start, length, "X")
        );
        var finished = await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(
            ReferenceEquals(finished, t),
            $"ReplaceInRange が停止しない(Match が Index < startat を返す入力): {pattern}"
        );
        var (fragment, count) = await t;
        Assert.Equal(expected, fragment);
        Assert.Equal(expectedCount, count);
    }

    // ----- ゼロ幅マッチの挿入点を論理文字境界へ後退させる(2026-09-01 B2 Task 2) -----

    [Fact]
    public void ReplaceInRange_ZeroWidthInsideCrlf_RetreatsToBoundary()
    {
        // 修正前は "a\rX\nb"=CRLF が 2 個の改行へ分裂した。
        // 単発置換(ReplaceCharRangeExact)は挿入点を 1 へ後退させるので、そちらへ揃える。
        var (fragment, count) = Make(@"(?<=\r)", useRegex: true)
            .ReplaceInRange("a\r\nb", 0, 4, "X");
        Assert.Equal("aX\r\nb", fragment);
        Assert.Equal(1, count);
    }

    [Fact]
    public void ReplaceInRange_ZeroWidthInsideSurrogatePair_RetreatsToBoundary()
    {
        // 修正前はペアの内側へ挿入し、書き戻し時に孤立サロゲート 2 個が U+FFFD へ潰れた
        // = 無警告のデータ破壊。"a😀b" = a(0) high(1) low(2) b(3)。
        var (fragment, count) = Make(@"(?<=\ud83d)", useRegex: true)
            .ReplaceInRange("a\U0001F600b", 0, 4, "X");
        Assert.Equal("aX\U0001F600b", fragment);
        Assert.Equal(1, count);
        Assert.DoesNotContain('�', fragment);
    }

    [Fact]
    public void ReplaceInRange_ZeroWidthRetreatingBeforeRangeStart_IsSkippedAndNotCounted()
    {
        // 範囲 [2,4) の始端 2 は CRLF の内側。(?<=\r) はそこにヒットするが挿入点は 1 へ
        // 後退する=範囲外。断片は範囲の中身だけを返す契約なので書かずにスキップし、
        // 件数にも数えない("N 件置換しました" を嘘にしない)。
        var (fragment, count) = Make(@"(?<=\r)", useRegex: true)
            .ReplaceInRange("a\r\nb", 2, 2, "X");
        Assert.Equal("\nb", fragment);
        Assert.Equal(0, count);
    }

    [Fact]
    public void ReplaceInRange_ZeroWidthRetreatingBeforeEmittedPosition_IsSkipped()
    {
        // "\r" を消費した直後、同じ位置(2)にゼロ幅が立つ。後退先 1 は既に出力済みなので
        // 書けない=スキップ。後退の判定は**元テキスト**で行う(出力側には既に CRLF が無い)。
        var (fragment, count) = Make(@"\r|(?<=\r)", useRegex: true)
            .ReplaceInRange("a\r\nb", 0, 4, "X");
        Assert.Equal("aX\nb", fragment);
        Assert.Equal(1, count);
    }

    // ----- ゼロ幅マッチ（I-1） -----

    [Fact]
    public void Count_zero_width_pattern_counts_empty_matches()
    {
        // 正規表現 "a*" は 'a' 連と空文字位置の双方でヒットする。
        Assert.Equal(4, Make("a*", useRegex: true).Count("aba"));
    }

    [Fact]
    public void FindNext_zero_width_lookahead_returns_zero_length_span()
    {
        // 先読み "(?=b)" は 'b' の直前で長さ 0 のヒットを返す。
        Assert.Equal(new MatchSpan(1, 0), Make("(?=b)", useRegex: true).FindNext("ab", 0));
    }

    // ----- 単語単位 × 正規表現 × 選択（\b(?:...)\b のグループ化, I-3） -----

    [Fact]
    public void Whole_word_regex_alternation_is_grouped_at_boundaries()
    {
        // \b(?:cat|dog)\b として括られるため "category" 内の "cat" は除外され 2 件。
        Assert.Equal(2, Make("cat|dog", wholeWord: true, useRegex: true).Count("cat dog category"));
    }

    // ----- 端点・値の契約 -----

    [Fact]
    public void FindNext_at_exact_end_returns_null() => Assert.Null(Make("ab").FindNext("ab", 2));

    [Fact]
    public void MatchSpan_End_is_start_plus_length() => Assert.Equal(7, new MatchSpan(5, 2).End);

    // ----- サロゲートペア（astral 文字, I-1） -----

    [Fact]
    public void Astral_literal_match_spans_full_surrogate_pair()
    {
        // 𠮷 = U+20BB7 はサロゲートペア（2 UTF-16 code unit）。リテラル一致は全体を覆う。
        var span = Make("𠮷").FindNext("x𠮷y", 0);
        Assert.Equal(new MatchSpan(1, 2), span);
    }

    [Fact]
    public void Regex_dot_matches_per_utf16_code_unit_for_astral()
    {
        // .NET の正規表現 . はコードユニット単位（サロゲート半分にマッチし得る）。
        // 仕様として固定し、Editor 側の境界スナップで破損を防ぐ前提を明示する。
        var span = Make(".", useRegex: true).FindNext("𠮷", 0);
        Assert.Equal(new MatchSpan(0, 1), span);
    }
}
