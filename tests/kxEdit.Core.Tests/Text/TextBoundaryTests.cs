using kxEdit.Core.Buffers;
using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

/// <summary>
/// 2026-07-31 文字アクセス seam Task 1: コードポイント単位(サロゲートのみ atomic)と
/// 論理文字単位(サロゲート + CRLF atomic)の 2 系統が、それぞれ独立に正しく歩進することを固定する。
///
/// 2 系統を分けている理由: キャレット / UIA は CRLF を 1 論理文字として扱うが、
/// WordBoundary の内部歩進は CR と LF を別々の LineBreak として数える前提になっている。
/// 統一すると WordBoundary の挙動が変わるため、名前で分離したまま集約する。
/// </summary>
public class TextBoundaryTests
{
    private static TextSnapshot Snap(string s) => TextBuffer.FromString(s).Current;

    // ===== コードポイント単位: サロゲートのみ atomic・CRLF は 1 ずつ =====

    [Fact]
    public void NextCodePoint_SkipsSurrogatePair()
    {
        var s = Snap("a😀b"); // CharLength=4
        Assert.Equal(1, TextBoundary.NextCodePoint(s, 0));
        Assert.Equal(3, TextBoundary.NextCodePoint(s, 1)); // 😀 を 1 歩で越える
        Assert.Equal(4, TextBoundary.NextCodePoint(s, 3));
        Assert.Equal(4, TextBoundary.NextCodePoint(s, 4)); // 末尾で no-op
    }

    [Fact]
    public void PrevCodePoint_SkipsSurrogatePair()
    {
        var s = Snap("a😀b");
        Assert.Equal(3, TextBoundary.PrevCodePoint(s, 4));
        Assert.Equal(1, TextBoundary.PrevCodePoint(s, 3));
        Assert.Equal(0, TextBoundary.PrevCodePoint(s, 1));
        Assert.Equal(0, TextBoundary.PrevCodePoint(s, 0)); // 先頭で no-op
    }

    [Fact]
    public void PrevCodePoint_SurrogatePairAtDocumentStart_IsAtomic()
    {
        // 文書先頭のペア = prev > 0 の境界(prefix 付き fixture "a😀b" では prev == 1 に当たらない)
        Assert.Equal(0, TextBoundary.PrevCodePoint(Snap("😀b"), 2));
    }

    [Fact]
    public void NextCodePoint_DoesNotSkipCrlf()
    {
        // 論理文字版との差を固定する = CRLF は 2 歩かかる
        var s = Snap("a\r\nb");
        Assert.Equal(2, TextBoundary.NextCodePoint(s, 1)); // CR の前 → CR と LF の間
        Assert.Equal(3, TextBoundary.NextCodePoint(s, 2));
    }

    [Fact]
    public void PrevCodePoint_DoesNotSkipCrlf()
    {
        var s = Snap("a\r\nb");
        Assert.Equal(2, TextBoundary.PrevCodePoint(s, 3));
        Assert.Equal(1, TextBoundary.PrevCodePoint(s, 2));
    }

    [Theory]
    [InlineData("ab", 0, 1)] // BMP
    [InlineData("😀", 0, 2)] // astral
    [InlineData("\r\n", 0, 1)] // CR は単独で 1
    public void CodePointLengthAt_Snapshot(string text, int pos, int expected) =>
        Assert.Equal(expected, TextBoundary.CodePointLengthAt(Snap(text), pos));

    [Fact]
    public void CodePointLengthAt_Snapshot_LoneHighSurrogateIsNormalizedByBuffer()
    {
        // バッファ層が孤立サロゲートを U+FFFD へ正規化するため、Snapshot 版では
        // 「high サロゲートの直後に low が続かない」形に到達しない。その前提ごと固定する。
        var lone = Snap("a\ud83d");
        Assert.Equal('\uFFFD', lone.GetChar(1));
        Assert.Equal(1, TextBoundary.CodePointLengthAt(lone, 1));
        // 対が揃っていれば 2
        Assert.Equal(2, TextBoundary.CodePointLengthAt(Snap("a😀"), 1));
    }

    [Fact]
    public void CodePointLengthAt_Snapshot_OutOfRange_Throws()
    {
        var s = Snap("ab");
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBoundary.CodePointLengthAt(s, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBoundary.CodePointLengthAt(s, -1));
    }

    // ===== 論理文字単位: サロゲート + CRLF atomic =====

    [Fact]
    public void NextLogicalChar_SkipsSurrogatePairAndCrlf()
    {
        Assert.Equal(3, TextBoundary.NextLogicalChar(Snap("a😀b"), 1));
        Assert.Equal(3, TextBoundary.NextLogicalChar(Snap("a\r\nb"), 1)); // CRLF を 1 歩で越える
    }

    [Fact]
    public void PrevLogicalChar_SkipsSurrogatePairAndCrlf()
    {
        Assert.Equal(1, TextBoundary.PrevLogicalChar(Snap("a😀b"), 3));
        Assert.Equal(1, TextBoundary.PrevLogicalChar(Snap("a\r\nb"), 3));
    }

    [Fact]
    public void PrevLogicalChar_SurrogatePairAtDocumentStart_IsAtomic()
    {
        // CRLF 側(LogicalChar_CrlfAtDocumentEdges_IsAtomic)と対称の prev > 0 境界
        Assert.Equal(0, TextBoundary.PrevLogicalChar(Snap("😀b"), 2));
    }

    [Fact]
    public void LogicalChar_LoneCrAndLoneLf_MoveOneStep()
    {
        Assert.Equal(2, TextBoundary.NextLogicalChar(Snap("a\rb"), 1));
        Assert.Equal(2, TextBoundary.NextLogicalChar(Snap("a\nb"), 1));
        Assert.Equal(1, TextBoundary.PrevLogicalChar(Snap("a\rb"), 2));
        Assert.Equal(1, TextBoundary.PrevLogicalChar(Snap("a\nb"), 2));
        // 改行が EOF に来る文書(Mac 形式 = 末尾が孤立 CR)。CRLF 判定の先読みが
        // CharLength ちょうどを踏まないことを固定する(踏むと GetChar が throw する)。
        Assert.Equal(2, TextBoundary.NextLogicalChar(Snap("a\r"), 1));
        Assert.Equal(2, TextBoundary.NextLogicalChar(Snap("a\n"), 1));
    }

    [Fact]
    public void LogicalChar_EmptyDocument_IsNoOp()
    {
        var s = Snap("");
        Assert.Equal(0, TextBoundary.NextLogicalChar(s, 0));
        Assert.Equal(0, TextBoundary.PrevLogicalChar(s, 0));
    }

    [Fact]
    public void LogicalChar_CrlfAtDocumentEdges_IsAtomic()
    {
        // 文書先頭・末尾の CRLF(前後に文字がない)でも pair として 1 歩で跨ぐ
        var s = Snap("\r\n");
        Assert.Equal(2, TextBoundary.NextLogicalChar(s, 0));
        Assert.Equal(0, TextBoundary.PrevLogicalChar(s, 2));
    }

    // ===== 中間位置スナップ =====

    [Fact]
    public void SnapToLogicalCharStart_SnapsMidSurrogateAndMidCrlf()
    {
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart(Snap("a😀b"), 2)); // low サロゲート位置
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart(Snap("a\r\nb"), 2)); // CR と LF の間
    }

    [Fact]
    public void SnapToLogicalCharStart_LeavesBoundariesAlone()
    {
        var s = Snap("a😀b");
        Assert.Equal(0, TextBoundary.SnapToLogicalCharStart(s, 0));
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart(s, 1));
        Assert.Equal(3, TextBoundary.SnapToLogicalCharStart(s, 3));
        Assert.Equal(4, TextBoundary.SnapToLogicalCharStart(s, 4)); // EOF は許可
    }

    [Fact]
    public void SnapToLogicalCharStart_LoneLfIsNotSnapped()
    {
        // 直前が CR でない LF は行頭ではなくそれ自体が論理文字の先頭
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart(Snap("a\nb"), 1));
    }

    [Fact]
    public void CrlfRule_NonLfAfterLoneCr_IsNotTreatedAsPairEnd()
    {
        // 孤立 CR(Mac 改行)の直後の通常文字は CRLF pair の後半ではない。
        // SnapToLogicalCharStart_LoneLfIsNotSnapped の鏡像(CR 側から見た no-snap)。
        // これが崩れると キャレットが 'b' から CR へ引き戻され、← が 1 回で 2 文字戻る。
        var s = Snap("a\rb");
        Assert.Equal(2, TextBoundary.SnapToLogicalCharStart(s, 2));
        Assert.Equal(2, TextBoundary.PrevLogicalChar(s, 3));
    }

    [Fact]
    public void CrlfRule_NonCrBeforeLoneLf_IsNotTreatedAsPairStart()
    {
        // 孤立 LF(Unix 改行)の直前の通常文字は CRLF pair の前半ではない。
        // CrlfRule_NonLfAfterLoneCr_IsNotTreatedAsPairEnd の鏡像(前進側)。
        // これが崩れると → が 1 回で 'a' と LF をまとめて飛び越える(LF 改行の文書で常時再現)。
        Assert.Equal(1, TextBoundary.NextLogicalChar(Snap("a\nb"), 0));
    }

    [Fact]
    public void SnapToLogicalCharStart_LfAtDocumentStart_DoesNotReadBeforeStart()
    {
        // 先頭が LF の文書(空行始まり)。pos == 0 は CRLF 判定へ進まず、GetChar(-1) を読まない。
        Assert.Equal(0, TextBoundary.SnapToLogicalCharStart(Snap("\nabc"), 0));
    }

    [Fact]
    public void SnapToLogicalCharStart_ClampsOutOfRange()
    {
        var s = Snap("abc");
        Assert.Equal(0, TextBoundary.SnapToLogicalCharStart(s, -5));
        Assert.Equal(3, TextBoundary.SnapToLogicalCharStart(s, 99));
    }

    // ===== 範囲外入力の契約(クラス doc の表を固定する) =====
    // 家族ごとに非対称なのは「進めない側」だけを no-op として許容しているため。
    // クランプで隠すと呼び出し側のスナップ漏れが発見できなくなるので throw 側は throw のまま守る。

    [Fact]
    public void OutOfRange_NextFamily_ThrowsBelowZero_ClampsAboveEnd()
    {
        var s = Snap("ab");
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBoundary.NextCodePoint(s, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBoundary.NextLogicalChar(s, -1));
        Assert.Equal(2, TextBoundary.NextCodePoint(s, 99));
        Assert.Equal(2, TextBoundary.NextLogicalChar(s, 99));
    }

    [Fact]
    public void OutOfRange_PrevFamily_ClampsBelowZero_ThrowsAboveEnd()
    {
        var s = Snap("ab");
        Assert.Equal(0, TextBoundary.PrevCodePoint(s, -1));
        Assert.Equal(0, TextBoundary.PrevLogicalChar(s, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBoundary.PrevCodePoint(s, 99));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBoundary.PrevLogicalChar(s, 99));
    }

    [Fact]
    public void OutOfRange_PrevFamily_DegenerateNoReadCase_DoesNotThrow()
    {
        // 実装は必要な位置しか読まない。prev == 0 になる範囲外(空文書 + pos == 1)は
        // 読取が起きないため throw せず 0 を返す。クラス doc の但し書きを固定する。
        var empty = Snap("");
        Assert.Equal(0, TextBoundary.PrevCodePoint(empty, 1));
        Assert.Equal(0, TextBoundary.PrevLogicalChar(empty, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBoundary.PrevCodePoint(empty, 2));
    }

    // ===== span 版(Layout / 描画) =====

    [Theory]
    [InlineData("ab", 0, 1)]
    [InlineData("😀", 0, 2)]
    [InlineData("a😀", 1, 2)]
    public void CodePointLengthAt_Span(string text, int i, int expected) =>
        Assert.Equal(expected, TextBoundary.CodePointLengthAt(text.AsSpan(), i));

    [Fact]
    public void CodePointLengthAt_Span_HighSurrogateAtEnd_IsOne()
    {
        // 対の相手が span の外にある場合は 1(呼び出し側の無限ループを防ぐ)
        ReadOnlySpan<char> lone = "a😀".AsSpan(0, 2); // 'a' + high サロゲートのみ
        Assert.Equal(1, TextBoundary.CodePointLengthAt(lone, 1));
    }

    [Fact]
    public void CodePointLengthAt_Span_HighSurrogateFollowedByNonLow_IsOne()
    {
        // span 版はバッファ層の U+FFFD 正規化を通らないため、対の揃わない high サロゲートを
        // 構築できる(snapshot 版と違い到達可能=テストで塞げる穴)
        ReadOnlySpan<char> text = "a\ud83db".AsSpan();
        Assert.Equal(1, TextBoundary.CodePointLengthAt(text, 1));
    }

    [Fact]
    public void SnapToCodePointStart_Span_LoneLowSurrogateIsNotSnapped()
    {
        // 直前が high サロゲートでない low サロゲートは pair ではない=動かさない
        ReadOnlySpan<char> text = "a\udc00b".AsSpan();
        Assert.Equal(1, TextBoundary.SnapToCodePointStart(text, 1));
    }

    [Fact]
    public void CodePointLengthAt_Span_OutOfRange_ThrowsIndexOutOfRange()
    {
        // snapshot 版(ArgumentOutOfRangeException)と例外型が違う契約を固定する
        Assert.Throws<IndexOutOfRangeException>(() =>
            TextBoundary.CodePointLengthAt("ab".AsSpan(), 2)
        );
        Assert.Throws<IndexOutOfRangeException>(() =>
            TextBoundary.CodePointLengthAt("ab".AsSpan(), -1)
        );
    }

    [Fact]
    public void SnapToCodePointStart_Span()
    {
        var text = "a😀b".AsSpan();
        Assert.Equal(1, TextBoundary.SnapToCodePointStart(text, 2)); // low サロゲート位置
        Assert.Equal(1, TextBoundary.SnapToCodePointStart(text, 1));
        Assert.Equal(0, TextBoundary.SnapToCodePointStart(text, 0));
        Assert.Equal(4, TextBoundary.SnapToCodePointStart(text, 4)); // 末尾は動かさない
    }

    [Fact]
    public void SnapToCodePointStart_Span_LowSurrogateAtIndexZero_DoesNotReadBeforeStart()
    {
        // スライスが pair を割って low サロゲートから始まる場合。i == 0 は text[-1] を読まない。
        ReadOnlySpan<char> text = "\udc00b".AsSpan();
        Assert.Equal(0, TextBoundary.SnapToCodePointStart(text, 0));
    }

    [Fact]
    public void SnapToCodePointStart_Span_ClampsOutOfRange()
    {
        var text = "abc".AsSpan();
        Assert.Equal(0, TextBoundary.SnapToCodePointStart(text, -5));
        Assert.Equal(3, TextBoundary.SnapToCodePointStart(text, 99));
    }
}
