using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

/// <summary>
/// 横断的パターン④ Unicode 制御文字の未サニタイズを塞ぐ共通ヘルパ SanitizeForDisplay を固定する。
/// BK-L-4(RestoreDialog RLO)/UIA-M-1(CSV セル値)/BK-L-5(Trace CRLF)/CSV-L-5(prompt 生パス) の
/// 共通依存(呼び出し差し込みは後続タスク)。
/// テスト内の制御文字はほぼすべて \uXXXX エスケープで書く(生の U+0085 / U+2028 等は
/// C# ソースの line terminator として解釈されリテラルが途中で切れるため)。
/// </summary>
public class SanitizeForDisplayTests
{
    // ---------- OneLine: null / 空 / 純 ASCII invariant ----------

    [Fact]
    public void OneLine_Null_ReturnsEmpty() =>
        Assert.Equal(string.Empty, SanitizeForDisplay.OneLine(null));

    [Fact]
    public void OneLine_Empty_ReturnsEmpty() =>
        Assert.Equal(string.Empty, SanitizeForDisplay.OneLine(""));

    [Theory]
    [InlineData("hello world")]
    [InlineData("abc123")]
    [InlineData("path/to/file.txt")]
    [InlineData("a")]
    public void OneLine_PureAscii_IsInvariant(string s) =>
        Assert.Equal(s, SanitizeForDisplay.OneLine(s));

    // ---------- OneLine: BiDi / Format 除去 ----------

    [Fact]
    public void OneLine_RemovesRlo_U202E()
    {
        // BK-L-4 スプーフィング原型: evil-{RLO}txt.exe → evil-txt.exe
        Assert.Equal("evil-txt.exe", SanitizeForDisplay.OneLine("evil-‮txt.exe"));
    }

    [Theory]
    [InlineData("‪")] // LRE
    [InlineData("‫")] // RLE
    [InlineData("‬")] // PDF
    [InlineData("‭")] // LRO
    [InlineData("‮")] // RLO
    [InlineData("⁦")] // LRI
    [InlineData("⁧")] // RLI
    [InlineData("⁨")] // FSI
    [InlineData("⁩")] // PDI
    [InlineData("؜")] // ALM
    [InlineData("‎")] // LRM
    [InlineData("‏")] // RLM
    public void OneLine_RemovesAllBidiFormatChars(string bidi)
    {
        Assert.Equal("ab", SanitizeForDisplay.OneLine("a" + bidi + "b"));
    }

    [Theory]
    [InlineData("​")] // ZWSP
    [InlineData("‌")] // ZWNJ
    [InlineData("‍")] // ZWJ
    public void OneLine_RemovesZeroWidthChars(string zw)
    {
        Assert.Equal("ab", SanitizeForDisplay.OneLine("a" + zw + "b"));
    }

    [Fact]
    public void OneLine_RemovesBom_UFEFF()
    {
        Assert.Equal("hello", SanitizeForDisplay.OneLine("﻿hello"));
    }

    [Fact]
    public void OneLine_RemovesLineSeparator_U2028()
    {
        // M-1: U+2028 (Zl / LINE SEPARATOR) は Format 相当扱いで drop
        // (空白畳みではなく完全除去。改行意図があれば呼び出し側が CR/LF を直接使う)
        Assert.Equal("ab", SanitizeForDisplay.OneLine("a\u2028b"));
    }

    [Fact]
    public void OneLine_RemovesParagraphSeparator_U2029()
    {
        // M-1: U+2029 (Zp / PARAGRAPH SEPARATOR) も drop
        Assert.Equal("ab", SanitizeForDisplay.OneLine("a\u2029b"));
    }

    // ---------- OneLine: C0 / C1 / DEL → 空白 + 連続畳み ----------

    [Fact]
    public void OneLine_NewlineBecomesSingleSpace()
    {
        Assert.Equal("a b c", SanitizeForDisplay.OneLine("a\nb\nc"));
    }

    [Fact]
    public void OneLine_TabBecomesSpace()
    {
        Assert.Equal("a b", SanitizeForDisplay.OneLine("a\tb"));
    }

    [Fact]
    public void OneLine_CrLf_CollapsesToSingleSpace()
    {
        // BK-L-5 CRLF injection の代表形: 2 文字が単一空白へ
        Assert.Equal("a b", SanitizeForDisplay.OneLine("a\r\nb"));
    }

    [Fact]
    public void OneLine_ConsecutiveSpaces_Collapse()
    {
        Assert.Equal("a b", SanitizeForDisplay.OneLine("a   b"));
    }

    [Fact]
    public void OneLine_MixedControlAndSpace_Collapse()
    {
        // 空白 + タブ + 改行 + の連鎖はすべて単一空白へ
        Assert.Equal("a b", SanitizeForDisplay.OneLine("a \t\r\nb"));
    }

    [Fact]
    public void OneLine_C1Control_U0085_BecomesSpace()
    {
        // U+0085 (NEL) は C1
        Assert.Equal("a b", SanitizeForDisplay.OneLine("a\u0085b"));
    }

    [Fact]
    public void OneLine_C1Control_U009F_BecomesSpace()
    {
        // U+009F (APC) は C1 の末端
        Assert.Equal("a b", SanitizeForDisplay.OneLine("a\u009Fb"));
    }

    [Fact]
    public void OneLine_DelChar_BecomesSpace()
    {
        Assert.Equal("a b", SanitizeForDisplay.OneLine("a\u007Fb"));
    }

    [Fact]
    public void OneLine_NullByte_BecomesSpace()
    {
        Assert.Equal("a b", SanitizeForDisplay.OneLine("a\0b"));
    }

    [Fact]
    public void OneLine_OtherC0_BecomesSpace()
    {
        // U+0001 (SOH), U+001F (US) — CR/LF/TAB 以外の C0 各種
        Assert.Equal("a b", SanitizeForDisplay.OneLine("a\u0001b"));
        Assert.Equal("a b", SanitizeForDisplay.OneLine("a\u001Fb"));
    }

    // ---------- OneLine: 末尾 trim / 先頭は残す ----------

    [Fact]
    public void OneLine_LeadingWhitespace_Preserved()
    {
        // M-2: 先頭空白は仕様上 trim しない(実装 line 66 / XML doc line 25 の意図宣言を lock in)
        Assert.Equal(" hello", SanitizeForDisplay.OneLine(" hello"));
    }

    [Fact]
    public void OneLine_TrailingSpaces_Trimmed()
    {
        Assert.Equal("hello", SanitizeForDisplay.OneLine("hello   "));
    }

    [Fact]
    public void OneLine_TrailingNewlines_Trimmed()
    {
        Assert.Equal("hello", SanitizeForDisplay.OneLine("hello\n\n"));
    }

    [Fact]
    public void OneLine_OnlyControls_BecomesEmpty()
    {
        Assert.Equal(string.Empty, SanitizeForDisplay.OneLine("\r\n\t\0"));
    }

    // ---------- OneLine: maxLength 省略 ----------

    [Fact]
    public void OneLine_MaxLength_TruncatesWithEllipsis()
    {
        // "abcdef" (len 6) → "abc…" (len 4)
        var result = SanitizeForDisplay.OneLine("abcdef", maxLength: 4);
        Assert.Equal("abc…", result);
        Assert.Equal(4, result.Length);
    }

    [Fact]
    public void OneLine_ExactFit_NoTruncation()
    {
        Assert.Equal("abcd", SanitizeForDisplay.OneLine("abcd", maxLength: 4));
    }

    [Fact]
    public void OneLine_OneOver_TruncatedAtMaxLength()
    {
        // "abcde" (len 5), maxLength=4 → "abc…" (len 4)
        var result = SanitizeForDisplay.OneLine("abcde", maxLength: 4);
        Assert.Equal("abc…", result);
        Assert.Equal(4, result.Length);
    }

    [Fact]
    public void OneLine_Shorter_ThanMaxLength_NotTruncated()
    {
        Assert.Equal("abc", SanitizeForDisplay.OneLine("abc", maxLength: 100));
    }

    [Fact]
    public void OneLine_MaxLength_Default_NoTruncation()
    {
        var big = new string('x', 10_000);
        Assert.Equal(big, SanitizeForDisplay.OneLine(big));
    }

    [Fact]
    public void OneLine_MaxLength_AppliedAfterSanitization()
    {
        // "a{RLO}bcdef" (RLO 除去後は "abcdef" len 6) を maxLength=4 で丸めると "abc…"
        // (RLO 込みの元 length ではなく、サニタイズ後の length で判定される)
        var result = SanitizeForDisplay.OneLine("a‮bcdef", maxLength: 4);
        Assert.Equal("abc…", result);
        Assert.Equal(4, result.Length);
    }

    [Fact]
    public void OneLine_MaxLength_Zero_ReturnsEmpty()
    {
        // 定義域外の防御。最小 1 未満は空文字。
        Assert.Equal(string.Empty, SanitizeForDisplay.OneLine("hello", maxLength: 0));
    }

    [Fact]
    public void OneLine_MaxLength_One_ReturnsEllipsisOnly()
    {
        // M-4: corner - maxLength=1 は "…" 単独(cutTo=0 で本体は空、末尾 "…" のみ)
        Assert.Equal("…", SanitizeForDisplay.OneLine("abc", maxLength: 1));
    }

    // ---------- OneLine: surrogate pair ----------

    [Fact]
    public void OneLine_SurrogatePair_Preserved()
    {
        // U+1F600 (😀) は 2 UTF-16 code units の surrogate pair
        Assert.Equal("😀", SanitizeForDisplay.OneLine("😀"));
    }

    [Fact]
    public void OneLine_SurrogatePair_NotSplitByTruncation()
    {
        // "a😀b" は length 4 (a + high + low + b)。maxLength=3 で単純に length 2 で切ると
        // "a" + 高サロゲート単独 = 破損。実装は surrogate 検出で back-off が必要。
        var input = "a😀b";
        Assert.Equal(4, input.Length);
        var result = SanitizeForDisplay.OneLine(input, maxLength: 3);
        // lone high surrogate が末尾に残らないこと
        Assert.DoesNotContain('\uD83D', result);
        Assert.EndsWith("…", result);
    }

    // ---------- MultiLine: null / 空 / 純 ASCII invariant ----------

    [Fact]
    public void MultiLine_Null_ReturnsEmpty() =>
        Assert.Equal(string.Empty, SanitizeForDisplay.MultiLine(null));

    [Fact]
    public void MultiLine_Empty_ReturnsEmpty() =>
        Assert.Equal(string.Empty, SanitizeForDisplay.MultiLine(""));

    [Theory]
    [InlineData("hello world")]
    [InlineData("abc123")]
    [InlineData("path/to/file.txt")]
    public void MultiLine_PureAscii_IsInvariant(string s) =>
        Assert.Equal(s, SanitizeForDisplay.MultiLine(s));

    // ---------- MultiLine: CR/LF/TAB は保持 ----------

    [Fact]
    public void MultiLine_PreservesCr() =>
        Assert.Equal("a\rb", SanitizeForDisplay.MultiLine("a\rb"));

    [Fact]
    public void MultiLine_PreservesLf() =>
        Assert.Equal("a\nb", SanitizeForDisplay.MultiLine("a\nb"));

    [Fact]
    public void MultiLine_PreservesCrLf() =>
        Assert.Equal("a\r\nb", SanitizeForDisplay.MultiLine("a\r\nb"));

    [Fact]
    public void MultiLine_PreservesTab() =>
        Assert.Equal("a\tb", SanitizeForDisplay.MultiLine("a\tb"));

    [Fact]
    public void MultiLine_MultilineText_Preserved()
    {
        Assert.Equal("line1\nline2\n", SanitizeForDisplay.MultiLine("line1\nline2\n"));
    }

    // ---------- MultiLine: BiDi / Format / C0 (非 CR/LF/TAB) / C1 / DEL を除去 ----------

    [Fact]
    public void MultiLine_RemovesRlo_U202E()
    {
        // OneLine と対称に RLO は落ちる
        Assert.Equal("ab", SanitizeForDisplay.MultiLine("a‮b"));
    }

    [Theory]
    [InlineData("‪")] // LRE
    [InlineData("‫")] // RLE
    [InlineData("‬")] // PDF
    [InlineData("‭")] // LRO
    [InlineData("‮")] // RLO
    [InlineData("‎")] // LRM
    [InlineData("‏")] // RLM
    [InlineData("​")] // ZWSP
    [InlineData("﻿")] // BOM
    public void MultiLine_RemovesFormatCategoryChars(string fmt)
    {
        Assert.Equal("ab", SanitizeForDisplay.MultiLine("a" + fmt + "b"));
    }

    [Fact]
    public void MultiLine_RemovesC1Control_U0085()
    {
        Assert.Equal("ab", SanitizeForDisplay.MultiLine("a\u0085b"));
    }

    [Fact]
    public void MultiLine_RemovesDelChar()
    {
        Assert.Equal("ab", SanitizeForDisplay.MultiLine("a\u007Fb"));
    }

    [Fact]
    public void MultiLine_RemovesOtherC0Controls()
    {
        // U+0001 (SOH), U+001F (US) — CR/LF/TAB 以外の C0 は落ちる。
        Assert.Equal("ab", SanitizeForDisplay.MultiLine("a\u0001b"));
        Assert.Equal("ab", SanitizeForDisplay.MultiLine("a\u001Fb"));
    }

    [Fact]
    public void MultiLine_RemovesNullByte()
    {
        Assert.Equal("ab", SanitizeForDisplay.MultiLine("a\0b"));
    }

    [Fact]
    public void MultiLine_MixedPreserveAndDrop()
    {
        // "line1\r\nline{RLO}2\tend" → "line1\r\nline2\tend"
        Assert.Equal("line1\r\nline2\tend", SanitizeForDisplay.MultiLine("line1\r\nline‮2\tend"));
    }

    [Fact]
    public void MultiLine_RemovesLineAndParagraphSeparators()
    {
        // M-1: U+2028 (Zl) と U+2029 (Zp) は「CR/LF/TAB のみ通す」invariant を守るため drop
        Assert.Equal("abc", SanitizeForDisplay.MultiLine("a\u2028b\u2029c"));
    }

    // ---------- MultiLine: surrogate pair ----------

    [Fact]
    public void MultiLine_SurrogatePair_Preserved()
    {
        Assert.Equal("😀", SanitizeForDisplay.MultiLine("😀"));
    }

    [Fact]
    public void MultiLine_SurrogatePair_InMultilineContext_Preserved()
    {
        Assert.Equal("a😀b\nc", SanitizeForDisplay.MultiLine("a😀b\nc"));
    }

    // ---------- ContainsSanitizableControlChar ----------
    // UIA-M-1 で「制御文字を含むかどうか」を明確に判定するためのヘルパ。
    // Format / LineSeparator / ParagraphSeparator / Control のいずれかを含めば true。

    [Fact]
    public void ContainsSanitizableControlChar_Null_ReturnsFalse() =>
        Assert.False(SanitizeForDisplay.ContainsSanitizableControlChar(null));

    [Fact]
    public void ContainsSanitizableControlChar_Empty_ReturnsFalse() =>
        Assert.False(SanitizeForDisplay.ContainsSanitizableControlChar(""));

    [Theory]
    [InlineData("hello world")]
    [InlineData("abc123")]
    [InlineData("path/to/file.txt")]
    [InlineData("日本語")]
    [InlineData(" leading and trailing ")]
    public void ContainsSanitizableControlChar_CleanText_ReturnsFalse(string s) =>
        Assert.False(SanitizeForDisplay.ContainsSanitizableControlChar(s));

    [Fact]
    public void ContainsSanitizableControlChar_Rlo_ReturnsTrue()
    {
        // U+202E (RLO) は Format カテゴリ
        Assert.True(SanitizeForDisplay.ContainsSanitizableControlChar("a‮b"));
    }

    [Fact]
    public void ContainsSanitizableControlChar_Cr_ReturnsTrue() =>
        Assert.True(SanitizeForDisplay.ContainsSanitizableControlChar("a\rb"));

    [Fact]
    public void ContainsSanitizableControlChar_Lf_ReturnsTrue() =>
        Assert.True(SanitizeForDisplay.ContainsSanitizableControlChar("a\nb"));

    [Fact]
    public void ContainsSanitizableControlChar_Tab_ReturnsTrue() =>
        Assert.True(SanitizeForDisplay.ContainsSanitizableControlChar("a\tb"));

    [Fact]
    public void ContainsSanitizableControlChar_LineSeparator_U2028_ReturnsTrue() =>
        Assert.True(SanitizeForDisplay.ContainsSanitizableControlChar("a\u2028b"));

    [Fact]
    public void ContainsSanitizableControlChar_ParagraphSeparator_U2029_ReturnsTrue() =>
        Assert.True(SanitizeForDisplay.ContainsSanitizableControlChar("a\u2029b"));

    [Fact]
    public void ContainsSanitizableControlChar_Bom_UFEFF_ReturnsTrue() =>
        Assert.True(SanitizeForDisplay.ContainsSanitizableControlChar("﻿hello"));

    [Fact]
    public void ContainsSanitizableControlChar_NullByte_ReturnsTrue() =>
        Assert.True(SanitizeForDisplay.ContainsSanitizableControlChar("a\0b"));

    [Fact]
    public void ContainsSanitizableControlChar_Del_ReturnsTrue() =>
        Assert.True(SanitizeForDisplay.ContainsSanitizableControlChar("ab"));

    [Fact]
    public void ContainsSanitizableControlChar_C1_U0085_ReturnsTrue() =>
        Assert.True(SanitizeForDisplay.ContainsSanitizableControlChar("a\u0085b"));

    [Fact]
    public void ContainsSanitizableControlChar_SurrogatePair_Clean_ReturnsFalse() =>
        // Emoji (U+1F600) は制御ではない
        Assert.False(SanitizeForDisplay.ContainsSanitizableControlChar("a😀b"));
}
