using kxEdit.Editor;

namespace kxEdit.Editor.Tests;

/// <summary>
/// 2026-08-02 巨大 1 行対応(変更 A)。GdiCharMetrics のコードポイント幅メモ化が
/// 「同じ MeasureText の結果を返す」= 挙動不変であることを確認する契約テスト。
/// メモ化の効果(速度)は L4(Editor.Smoke --largeline)で測る。
/// 本テストが守るのはキャッシュキーの正しさ = サロゲートペアと単独サロゲートの
/// 取り違え・衝突が起きないことである。
/// </summary>
public class GdiCharMetricsCacheTests
{
    private const TextFormatFlags MeasureFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
    private static readonly Size MaxSize = new(int.MaxValue, int.MaxValue);

    /// <summary>キャッシュを経由しない参照実装(現行 GdiCharMetrics の非 ASCII 経路と同一)。</summary>
    private static int Reference(string s, Font font) =>
        TextRenderer.MeasureText(s, font, MaxSize, MeasureFlags).Width;

    [Theory]
    [InlineData("a")] // ASCII
    // タブ。ここでは冪等性しか見ていない(ASCII は参照比較のガードで除外される)。
    // 「ASCII 経路を通ること」自体は Tab_keeps_the_ascii_path_and_reads_as_a_space_width が守る。
    [InlineData("\t")]
    [InlineData("あ")] // BMP CJK
    [InlineData("漢")] // BMP CJK
    [InlineData("😀")] // astral(サロゲートペア)
    public void MeasureRun_single_codepoint_matches_uncached_reference(string cp) =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);

            int first = m.MeasureRun(cp);
            int second = m.MeasureRun(cp); // キャッシュヒット経路

            Assert.Equal(second, first); // 何度呼んでも同じ
            if (cp[0] >= 128)
                Assert.Equal(Reference(cp, font), first);
        });

    /// <summary>
    /// 単独サロゲート(不正 UTF-16)も参照実装と一致すること。
    /// </summary>
    /// <remarks>
    /// 上の Theory へ <c>[InlineData("\uD83D")]</c> / <c>[InlineData("\uDE00")]</c> として
    /// 足さずに Fact へ分けているのは、xUnit v2 が InlineData の string を UTF-8 → Base64 で
    /// 直列化して test case ID を作るため、<b>単独 high / low サロゲートがともに U+FFFD へ潰れて
    /// ID が衝突し、片方が "Skipping test case with duplicate ID" で黙って実行されなくなる</b>
    /// から(2026-08-02 に実測。ログを見ない限り気付けず、通ったテスト数だけ 1 減る)。
    /// </remarks>
    [Fact]
    public void MeasureRun_lone_surrogates_match_uncached_reference() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);

            foreach (string cp in new[] { "\uD83D", "\uDE00" })
            {
                int first = m.MeasureRun(cp);
                int second = m.MeasureRun(cp); // キャッシュヒット経路

                Assert.Equal(second, first); // 何度呼んでも同じ
                Assert.Equal(Reference(cp, font), first);
            }
        });

    [Fact]
    public void Surrogate_pair_and_lone_high_surrogate_do_not_share_a_cache_entry() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);

            // 先に単独 high サロゲートを測って、そのキーがペアを汚染しないことを見る。
            // (naive な「先頭 char をキーにする」実装はここで落ちる)
            int lone = m.MeasureRun("\uD83D");
            int pair = m.MeasureRun("😀");

            Assert.Equal(Reference("\uD83D", font), lone);
            Assert.Equal(Reference("😀", font), pair);
        });

    [Fact]
    public void Multi_codepoint_run_still_goes_through_MeasureText_as_a_whole() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);

            // 複数コードポイントの run は一括計測の既存挙動を維持する
            // (コードポイント幅の和と一致するとは限らないため、コードポイント幅の和で代用してはならない。
            // run 全体の MeasureText の結果を使う。run 単位のメモは可)。
            Assert.Equal(Reference("あいうえお", font), m.MeasureRun("あいうえお"));
            Assert.Equal(Reference("あa", font), m.MeasureRun("あa"));
        });

    /// <summary>
    /// キャッシュを引く条件が「非 ASCII のときだけ」であることを守る。
    /// TAB は <c>_asciiWidths['\t'] = _asciiWidths[' ']</c> で意図的にスペース幅へ
    /// 読み替えられており、ASCII 経路を外れると <c>MeasureText("\t").Width</c>(= 0)へ
    /// 変わるため、この 1 本だけが両経路を区別できる。
    /// </summary>
    /// <remarks>
    /// 仕様レビューのミューテーション検証で見つかった穴。<c>MeasureRun</c> の
    /// <c>text[0] &gt;= 128</c> を <c>&gt;= 9</c> に変異させると TAB がキャッシュ経路へ流れ込むが、
    /// この 1 本を足す前は Editor 層 320 件が全 PASS のまま通過していた(等価変異ではない)。
    /// </remarks>
    [Fact]
    public void Tab_keeps_the_ascii_path_and_reads_as_a_space_width() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);

            Assert.Equal(m.MeasureRun(" "), m.MeasureRun("\t"));
            // スペース幅そのものが 0 だと上の等値が無意味になるため、前提を明示しておく。
            Assert.True(m.MeasureRun(" ") > 0);
        });

    /// 2026-09-24 性能改善フェーズ 1(P-2): 非 ASCII を含む複数コードポイントの run も
    /// MeasureText の結果そのものを返す(キャッシュ経由でも参照実装と一致)。
    /// 先頭が ASCII で途中から非 ASCII になる run(行番号付きの行など)も含める。
    [Theory]
    [InlineData("あいうえお")]
    [InlineData("あa")]
    [InlineData("00001: 吾輩は猫である。")]
    [InlineData("𠮷野家")]
    [InlineData("é")] // 結合文字
    public void MeasureRun_multi_codepoint_run_matches_reference_and_is_cached(string run) =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);
            int first = m.MeasureRun(run);
            int second = m.MeasureRun(run);
            Assert.Equal(Reference(run, font), first);
            Assert.Equal(first, second);
            Assert.Equal(1, m.TestHook_RunCacheCount);
            Assert.Equal(run.Length, m.TestHook_RunCacheChars);
        });

    /// span キー(部分 span)と string キーが同じエントリを引くこと。
    [Fact]
    public void Span_slice_and_string_share_an_entry() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);
            string line = "xxあいうえおyy";
            int bySpan = m.MeasureRun(line.AsSpan(2, 5));
            int byString = m.MeasureRun("あいうえお");
            Assert.Equal(Reference("あいうえお", font), bySpan);
            Assert.Equal(bySpan, byString);
            Assert.Equal(1, m.TestHook_RunCacheCount);
        });

    /// 1 件の上限(4,096 文字)を超える run は格納しない。ちょうど上限は格納する。
    [Fact]
    public void Runs_longer_than_the_per_entry_limit_are_not_cached() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);
            string atLimit = new('あ', GdiCharMetrics.MaxCachedRunChars);
            string over = new('い', GdiCharMetrics.MaxCachedRunChars + 1);
            Assert.Equal(Reference(over, font), m.MeasureRun(over));
            Assert.Equal(0, m.TestHook_RunCacheCount);
            Assert.Equal(Reference(atLimit, font), m.MeasureRun(atLimit));
            Assert.Equal(1, m.TestHook_RunCacheCount);
        });

    /// 合計文字数の上限を超える格納で全消去し、その run だけが残る。
    /// 上限ちょうどまでは消去しない(境界を両側から見る)。
    [Fact]
    public void Exceeding_the_total_char_budget_clears_the_cache() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);
            int per = GdiCharMetrics.MaxCachedRunChars;
            Assert.Equal(0, GdiCharMetrics.RunCacheBudgetChars % per); // 前提
            int fit = GdiCharMetrics.RunCacheBudgetChars / per;
            for (int i = 0; i < fit; i++)
                m.MeasureRun(UniqueRun(i, per));
            Assert.Equal(fit, m.TestHook_RunCacheCount);
            Assert.Equal(GdiCharMetrics.RunCacheBudgetChars, m.TestHook_RunCacheChars);
            m.MeasureRun("あい"); // 予算を 2 文字超える
            Assert.Equal(1, m.TestHook_RunCacheCount);
            Assert.Equal(2, m.TestHook_RunCacheChars);
        });

    /// i ごとに内容の異なる、長さ len の非 ASCII run。
    private static string UniqueRun(int i, int len)
    {
        var s = new string('あ', len).ToCharArray();
        s[0] = (char)('ア' + (i % 80));
        s[1] = (char)('亜' + (i / 80));
        return new string(s);
    }

    /// ASCII だけの run と単一コードポイントは、run のメモに入らない(経路不変)。
    [Fact]
    public void Ascii_runs_and_single_codepoints_do_not_use_the_run_cache() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);
            m.MeasureRun("hello world");
            m.MeasureRun("あ");
            m.MeasureRun("😀");
            Assert.Equal(0, m.TestHook_RunCacheCount);
        });
}
