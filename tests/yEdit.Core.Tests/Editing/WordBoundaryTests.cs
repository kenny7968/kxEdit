using yEdit.Core.Buffers;
using yEdit.Core.Editing;

namespace yEdit.Core.Tests.Editing;

public class WordBoundaryTests
{
    private static TextSnapshot S(string s) => TextBuffer.FromString(s).Current;

    // ===== NextWordStart: ASCII =====
    [Theory]
    [InlineData("hello world", 0, 6)] // 'hello' の後 → 空白スキップ → 'w'
    [InlineData("hello world", 5, 6)] // 空白位置 → 空白スキップ → 'w'
    [InlineData("hello world", 6, 11)] // 'world' → EOF
    [InlineData("aaa bbb ccc", 3, 4)] // 空白 → 'b'
    [InlineData("abc\r\ndef", 3, 5)] // CRLF まとめて skip → 'd'
    [InlineData("abc\ndef", 3, 4)] // LF skip → 'd'
    [InlineData("hello", 5, 5)] // EOF 停留
    public void NextWordStart_Ascii(string text, int from, int expected) =>
        Assert.Equal(expected, WordBoundary.NextWordStart(S(text), from, WordBoundary.NoScanLimit));

    // ===== PrevWordStart: ASCII =====
    [Theory]
    [InlineData("hello world", 11, 6)] // EOF → 'w' の頭
    [InlineData("hello world", 6, 0)] // 'w' → 'h' の頭
    [InlineData("hello world", 5, 0)] // 空白位置 → 'h'
    [InlineData("hello", 0, 0)] // BOF 停留
    [InlineData("hello", 3, 0)] // 'l' → 'h'
    public void PrevWordStart_Ascii(string text, int from, int expected) =>
        Assert.Equal(expected, WordBoundary.PrevWordStart(S(text), from, WordBoundary.NoScanLimit));

    // ===== 文字クラス切替(CJK 混在) =====
    [Fact]
    public void NextWordStart_ClassSwitch_CJK()
    {
        // "あいう漢字abc123" → ひらがな→漢字→英字→数字
        var s = S("あいう漢字abc123");
        Assert.Equal(3, WordBoundary.NextWordStart(s, 0, WordBoundary.NoScanLimit)); // "あいう" の後 → 漢字頭
        Assert.Equal(5, WordBoundary.NextWordStart(s, 3, WordBoundary.NoScanLimit)); // "漢字" の後 → 英字頭
        Assert.Equal(8, WordBoundary.NextWordStart(s, 5, WordBoundary.NoScanLimit)); // "abc" の後 → 数字頭
        Assert.Equal(11, WordBoundary.NextWordStart(s, 8, WordBoundary.NoScanLimit)); // "123" の後 → EOF
    }

    [Fact]
    public void PrevWordStart_ClassSwitch_CJK()
    {
        var s = S("あいう漢字abc123");
        Assert.Equal(8, WordBoundary.PrevWordStart(s, 11, WordBoundary.NoScanLimit)); // 数字末尾 → 数字頭
        Assert.Equal(5, WordBoundary.PrevWordStart(s, 8, WordBoundary.NoScanLimit)); // 英字頭 → 英字頭(=数字頭の前)は "abc" 頭=5
        Assert.Equal(3, WordBoundary.PrevWordStart(s, 5, WordBoundary.NoScanLimit)); // 英字頭 → 漢字頭
        Assert.Equal(0, WordBoundary.PrevWordStart(s, 3, WordBoundary.NoScanLimit)); // 漢字頭 → ひらがな頭
    }

    // ===== 記号(Other クラス) =====
    [Fact]
    public void NextWordStart_TreatsPunctuationAsSeparateClass()
    {
        var s = S("abc,def");
        Assert.Equal(3, WordBoundary.NextWordStart(s, 0, WordBoundary.NoScanLimit)); // 'abc' → ','
        Assert.Equal(4, WordBoundary.NextWordStart(s, 3, WordBoundary.NoScanLimit)); // ',' → 'd'
    }

    // ===== カタカナ =====
    [Fact]
    public void WordBoundary_Katakana_ClassifiesCorrectly()
    {
        var s = S("アイウエオ漢字");
        Assert.Equal(5, WordBoundary.NextWordStart(s, 0, WordBoundary.NoScanLimit)); // カタカナ → 漢字
    }

    // ===== 空文書 =====
    [Fact]
    public void NextWordStart_OnEmptyBuffer_ReturnsZero()
    {
        var s = S("");
        Assert.Equal(0, WordBoundary.NextWordStart(s, 0, WordBoundary.NoScanLimit));
    }

    [Fact]
    public void PrevWordStart_OnEmptyBuffer_ReturnsZero()
    {
        var s = S("");
        Assert.Equal(0, WordBoundary.PrevWordStart(s, 0, WordBoundary.NoScanLimit));
    }

    // ===== サロゲート(絵文字は Other クラス扱い) =====
    [Fact]
    public void NextWordStart_SurrogatePair_TreatedAsSingleCp()
    {
        var s = S("😀😀abc");
        // 絵文字(サロゲート)は Other クラス。連続 2 個 = 4 code units
        Assert.Equal(4, WordBoundary.NextWordStart(s, 0, WordBoundary.NoScanLimit)); // 絵文字 2 個 → 'a'
    }

    [Fact]
    public void PrevWordStart_SurrogatePair_TreatedAsSingleCp()
    {
        var s = S("a😀b");
        // caret=4('b' の後)から Prev → 'b' 頭=3
        // その前は 😀(Other)= class 切替 → 3 で止まる
        Assert.Equal(3, WordBoundary.PrevWordStart(s, 4, WordBoundary.NoScanLimit));
    }

    // ===== 境界ケース(レビュー S-5 追加分) =====

    [Fact]
    public void PrevWordStart_FromLineBreak_ReturnsPrevWordStart()
    {
        // "abc\r\ndef" の caret=5(改行直後 'd' の頭)から Prev
        // MoveLeftCp=4('\n') → LineBreak → 3('\r')→ LineBreak → 2('c')Latin で停止
        // wordCls=Latin, prev=1 も Latin → prev=0 も Latin → 返却 0
        var s = S("abc\r\ndef");
        Assert.Equal(0, WordBoundary.PrevWordStart(s, 5, WordBoundary.NoScanLimit));
    }

    [Fact]
    public void WordBoundary_FullwidthSpace_TreatedAsOther()
    {
        // "あ　い"(全角空白 U+3000 挟み)
        // ひらがな→全角空白(Other)→ひらがな の切替を認識
        // caret=0 から Next → "あ" の後 → Other(全角空白)頭=1
        var s = S("あ　い");
        Assert.Equal(1, WordBoundary.NextWordStart(s, 0, WordBoundary.NoScanLimit));
        // caret=1(全角空白) → Other の後(全角空白は 1 文字連続) → "い" 頭=2
        Assert.Equal(2, WordBoundary.NextWordStart(s, 1, WordBoundary.NoScanLimit));
    }

    [Fact]
    public void PrevWordStart_OnlyWhitespace_ReturnsZero()
    {
        // "   "(空白のみ)caret=3 → 空白連続だけの Prev 経路
        // MoveLeftCp=2 → Whitespace → ループで pos=0 まで
        // wordCls=Whitespace, pos=0 なので追加ループなし → 0
        var s = S("   ");
        Assert.Equal(0, WordBoundary.PrevWordStart(s, 3, WordBoundary.NoScanLimit));
    }

    // ===== 2026-08-04 F-3 修正: WordStart / WordEnd(ダブルクリック単語選択と同一規則) =====

    /// <summary>
    /// 期待値は <c>InputRouter.PrevWordBoundary</c> / <c>NextWordBoundary</c>(移設元)の
    /// 現行挙動そのもの。<b>この表は挙動不変の網</b>なので、実装に合わせて書き換えてはならない。
    /// 生成手順は実装計画 Task 2 Step 1(反射で移設元を全 pos 照合)。
    /// </summary>
    [Theory]
    // "hello world": Latin run 2 つ + 空白 1
    [InlineData("hello world", 0, 0, 5)]
    [InlineData("hello world", 3, 0, 5)]
    [InlineData("hello world", 5, 0, 5)] // 空白の上 = 前単語(キャレットを含まない・現行仕様)
    [InlineData("hello world", 6, 6, 11)]
    [InlineData("hello world", 11, 6, 11)] // EOF
    // "今日は晴れです。": クラス境界で刻む(現状の空白規則なら [0,8) = 行全体だった)
    [InlineData("今日は晴れです。", 0, 0, 2)]
    [InlineData("今日は晴れです。", 1, 0, 2)]
    [InlineData("今日は晴れです。", 2, 2, 3)]
    [InlineData("今日は晴れです。", 3, 3, 4)]
    [InlineData("今日は晴れです。", 7, 7, 8)] // 句点 = Other
    // "abc123def": Latin / Digit / Latin
    [InlineData("abc123def", 0, 0, 3)]
    [InlineData("abc123def", 4, 3, 6)]
    [InlineData("abc123def", 8, 6, 9)]
    // "今日　は": 全角空白は Other クラス = それ自体が 1 単語
    [InlineData("今日　は", 2, 2, 3)]
    public void WordStart_WordEnd_MatchDoubleClickRule(string text, int pos, int start, int end)
    {
        var snap = TextBuffer.FromString(text).Current;
        Assert.Equal(start, WordBoundary.WordStart(snap, pos, WordBoundary.NoScanLimit));
        Assert.Equal(end, WordBoundary.WordEnd(snap, pos, WordBoundary.NoScanLimit));
    }

    [Fact]
    public void WordStart_WordEnd_Empty_ReturnsZero()
    {
        var snap = TextBuffer.FromString("").Current;
        Assert.Equal(0, WordBoundary.WordStart(snap, 0, WordBoundary.NoScanLimit));
        Assert.Equal(0, WordBoundary.WordEnd(snap, 0, WordBoundary.NoScanLimit));
    }

    // ===== 走査上限 =====

    [Fact]
    public void WordStart_WithMaxScan_StopsWithinWindow()
    {
        var snap = TextBuffer.FromString(new string('a', 5000)).Current;
        int start = WordBoundary.WordStart(snap, 4000, maxScan: 100);
        // WordStart は PrevWordStart(pos + 1) を呼ぶため最初の 1 歩で予算を 1 消費する。
        // ゆえに pos から左へ実際に走れるのは maxScan - 1 歩 = 4000 - 99 = 3901(WordEnd との非対称)。
        Assert.Equal(3901, start);
        Assert.True(start > 0, "上限が効かず行頭まで走っている");
    }

    [Fact]
    public void WordEnd_WithMaxScan_StopsWithinWindow()
    {
        var snap = TextBuffer.FromString(new string('a', 5000)).Current;
        int end = WordBoundary.WordEnd(snap, 1000, maxScan: 100);
        // 右側は pos から maxScan 歩ぶん走れる(WordStart 側だけ 1 狭い)。
        Assert.Equal(1100, end);
        Assert.True(end < snap.CharLength, "上限が効かず行末まで走っている");
    }

    /// <summary>
    /// <c>ExpandToEnclosingUnit</c> の呼び順(<c>WordStart(pos)</c> → <c>WordEnd(pos)</c>)で
    /// 上限が効いても<b>スパンがキャレットを含む</b>こと。
    /// </summary>
    [Fact]
    public void WordSpan_WithMaxScan_ContainsCaret()
    {
        var snap = TextBuffer.FromString(new string('a', 500_000)).Current;
        const int Pos = 250_000;
        int start = WordBoundary.WordStart(snap, Pos, maxScan: 100);
        int end = WordBoundary.WordEnd(snap, Pos, maxScan: 100);
        Assert.True(start <= Pos, $"start={start} が pos を超えている");
        Assert.True(end > Pos, $"end={end} が pos を含んでいない");
    }

    [Fact]
    public void NextWordStart_WithMaxScan_StopsMidRun()
    {
        var snap = TextBuffer.FromString(new string('a', 5000)).Current;
        int next = WordBoundary.NextWordStart(snap, 0, maxScan: 100);
        Assert.Equal(100, next);
    }

    [Fact]
    public void PrevWordStart_WithMaxScan_StopsMidRun()
    {
        var snap = TextBuffer.FromString(new string('a', 5000)).Current;
        int prev = WordBoundary.PrevWordStart(snap, 5000, maxScan: 100);
        Assert.InRange(prev, 4900, 4901);
    }

    /// <summary>上限は 1 呼び出し全体の予算。空白 run をまたいでも合算で頭打ちになる。</summary>
    [Fact]
    public void NextWordStart_MaxScan_IsBudgetAcrossRuns()
    {
        var snap = TextBuffer.FromString("aaaa" + new string(' ', 100) + "bbbb").Current;
        int next = WordBoundary.NextWordStart(snap, 0, maxScan: 10);
        Assert.Equal(10, next);
    }

    /// <summary>
    /// <see cref="NextWordStart_MaxScan_IsBudgetAcrossRuns"/> の対称形。
    /// <c>PrevWordStart</c> でも予算は空白 run と単語 run で<b>共有</b>され、run をまたいでも
    /// 合算で頭打ちになる(空白 run のスキャンが無料になっていない)。
    /// </summary>
    /// <remarks>
    /// 空白ゼロの fixture(<c>new string('a', 5000)</c>)では空白 run を一度も通らないため、
    /// 「単語 run の前で予算を張り直す」変異を素通ししてしまう。区切りを挟んだ fixture が要る。
    /// 内訳は 1(最初の 1 歩)+ 4(空白 run 4 個)+ 5(単語 run の残り)= 10 = maxScan。
    /// </remarks>
    [Fact]
    public void PrevWordStart_MaxScan_IsBudgetAcrossRuns()
    {
        var snap = TextBuffer.FromString(new string('a', 20) + new string(' ', 4)).Current;
        int prev = WordBoundary.PrevWordStart(snap, snap.CharLength, maxScan: 10);
        Assert.Equal(14, prev);
        Assert.Equal(10, snap.CharLength - prev); // 消費した総歩数 = maxScan
    }
}
