using kxEdit.Core.Buffers;
using kxEdit.Core.Editing;
using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Editing;

public class NavigationCommandsTests
{
    private static TextSnapshot Snap(string s) => TextBuffer.FromString(s).Current;

    // ASCII=8px・全角=16px(視覚行テスト用)
    private static readonly ICharMetrics M = new MonoCharMetrics(halfWidthPx: 8, lineHeightPx: 20);

    [Fact]
    public void MoveLeftChar_SkipsSurrogatePair()
    {
        var s = Snap("a😀b"); // "a" + "😀"(surrogate pair) + "b"、CharLength=4
        Assert.Equal(3, NavigationCommands.MoveLeftChar(s, 4)); // 'b' の前 → surrogate high の後にキャレット
        Assert.Equal(1, NavigationCommands.MoveLeftChar(s, 3)); // surrogate high → 'a' の後
        Assert.Equal(0, NavigationCommands.MoveLeftChar(s, 1));
        Assert.Equal(0, NavigationCommands.MoveLeftChar(s, 0)); // 先頭で no-op
    }

    [Fact]
    public void MoveRightChar_SkipsSurrogatePair()
    {
        var s = Snap("a😀b");
        Assert.Equal(1, NavigationCommands.MoveRightChar(s, 0));
        Assert.Equal(3, NavigationCommands.MoveRightChar(s, 1)); // 'a' の後 → 😀 の後
        Assert.Equal(4, NavigationCommands.MoveRightChar(s, 3));
        Assert.Equal(4, NavigationCommands.MoveRightChar(s, 4)); // 末尾で no-op
    }

    [Fact]
    public void MoveLeftChar_SkipsCrlfPair()
    {
        var s = Snap("a\r\nb");
        Assert.Equal(1, NavigationCommands.MoveLeftChar(s, 3));
    }

    [Fact]
    public void MoveLeftChar_LoneCr_MovesOneStep()
    {
        var s = Snap("a\rb");
        Assert.Equal(1, NavigationCommands.MoveLeftChar(s, 2));
    }

    [Fact]
    public void MoveLeftChar_LoneLf_MovesOneStep()
    {
        var s = Snap("a\nb");
        Assert.Equal(1, NavigationCommands.MoveLeftChar(s, 2));
    }

    [Fact]
    public void MoveLeftChar_OnEmptyBuffer_ReturnsZero()
    {
        var s = Snap("");
        Assert.Equal(0, NavigationCommands.MoveLeftChar(s, 0));
    }

    [Fact]
    public void MoveRightChar_SkipsCrlfPair()
    {
        var s = Snap("a\r\nb");
        Assert.Equal(3, NavigationCommands.MoveRightChar(s, 1));
    }

    [Fact]
    public void MoveRightChar_LoneCr_MovesOneStep()
    {
        var s = Snap("a\rb");
        Assert.Equal(2, NavigationCommands.MoveRightChar(s, 1));
    }

    [Fact]
    public void MoveRightChar_LoneLf_MovesOneStep()
    {
        var s = Snap("a\nb");
        Assert.Equal(2, NavigationCommands.MoveRightChar(s, 1));
    }

    [Fact]
    public void MoveRightChar_OnEmptyBuffer_ReturnsZero()
    {
        var s = Snap("");
        Assert.Equal(0, NavigationCommands.MoveRightChar(s, 0));
    }

    [Fact]
    public void MoveHome_ReturnsLineStart()
    {
        var s = Snap("abc\r\ndef");
        Assert.Equal(0, NavigationCommands.MoveHome(s, 2)); // 行0内
        Assert.Equal(0, NavigationCommands.MoveHome(s, 0)); // 行0の先頭
        Assert.Equal(5, NavigationCommands.MoveHome(s, 7)); // "def" の 'e' → 行1の先頭=5
    }

    [Fact]
    public void MoveEnd_ReturnsLineEnd_ExcludingBreak()
    {
        var s = Snap("abc\r\ndef");
        Assert.Equal(3, NavigationCommands.MoveEnd(s, 1)); // 行0の末尾(\r の前)
        Assert.Equal(3, NavigationCommands.MoveEnd(s, 3)); // 既に末尾でも同じ
        Assert.Equal(8, NavigationCommands.MoveEnd(s, 6)); // 行1(EOF・改行なし)
    }

    [Fact]
    public void MoveEnd_LfOnly_ExcludesLf()
    {
        var s = Snap("abc\ndef");
        Assert.Equal(3, NavigationCommands.MoveEnd(s, 1));
    }

    [Fact]
    public void MoveHomeSmart_TogglesBetweenFirstNonWsAndLineStart()
    {
        var s = Snap("  hello");
        Assert.Equal(2, NavigationCommands.MoveHomeSmart(s, 4)); // 本文内(4='l')→ firstNonWs(2)
        Assert.Equal(0, NavigationCommands.MoveHomeSmart(s, 2)); // firstNonWs → lineStart
        Assert.Equal(2, NavigationCommands.MoveHomeSmart(s, 0)); // lineStart → firstNonWs
    }

    [Fact]
    public void MoveHomeSmart_TabsAsWhitespace()
    {
        var s = Snap("\t\thello");
        Assert.Equal(2, NavigationCommands.MoveHomeSmart(s, 4));
    }

    [Fact]
    public void MoveHomeSmart_EmptyLine_ReturnsLineStart()
    {
        var s = Snap("abc\n\nxyz"); // 行1 は空行(lineStart=lineEnd=4)
        Assert.Equal(4, NavigationCommands.MoveHomeSmart(s, 4)); // firstNonWs=lineEnd=4=lineStart相当
    }

    [Fact]
    public void MoveHomeSmart_LineWithOnlyWhitespace_TogglesLineStartLineEnd()
    {
        var s = Snap("   "); // 空白のみ(firstNonWs=lineEnd=3)
        Assert.Equal(3, NavigationCommands.MoveHomeSmart(s, 0)); // lineStart(0) → firstNonWs(3)
        Assert.Equal(0, NavigationCommands.MoveHomeSmart(s, 3)); // firstNonWs(3) → lineStart(0)
    }

    [Fact]
    public void MoveHome_AtCharLength_ReturnsLastLineStart()
    {
        // EOF キャレット(caret == CharLength)で throw しない契約
        var s = Snap("abc\r\ndef");
        Assert.Equal(5, NavigationCommands.MoveHome(s, s.CharLength)); // 最終行の先頭
    }

    [Fact]
    public void MoveHome_AfterTrailingCrLf_ReturnsEmptyLastLineStart()
    {
        // "abc\r\n"(末尾改行あり)の caret=5(空の最終行)は 5 を返す
        // GetLineIndexOfChar の CRLF 分岐(prefix.LastIsCr の使い方)への回帰保険
        var s = Snap("abc\r\n");
        Assert.Equal(5, NavigationCommands.MoveHome(s, 5));
    }

    [Fact]
    public void MoveHomeSmart_OnEmptyBuffer_ReturnsZero()
    {
        // 空文書での不変性(暗黙成立を明文化)
        var s = Snap("");
        Assert.Equal(0, NavigationCommands.MoveHomeSmart(s, 0));
    }

    [Fact]
    public void MoveRightChar_OrphanHighSurrogateAtEof_DoesNotThrow()
    {
        // "a\uD83D"(孤立ハイサロゲート・末尾)で MoveRightChar(1) が CharLength を返し throw しない
        // 契約「code-point 境界前提だが arbitrary UTF-16 でも throw しない」の明文化
        var s = Snap("a\uD83D");
        Assert.Equal(2, s.CharLength);
        Assert.Equal(2, NavigationCommands.MoveRightChar(s, 1));
    }

    // ===== P8-1a: 視覚行ベース Home キー(wrapColumns/metrics 版) =====

    [Fact]
    public void MoveHomeSmart_WithWrap_WrapDisabled_SameAsLogicalLine()
    {
        // wrapColumns<=0 は既存論理行挙動と同じ=既存 3 パターンを再現
        var s = Snap("  hello");
        Assert.Equal(2, NavigationCommands.MoveHomeSmart(s, 4, wrapColumns: 0, M));
        Assert.Equal(0, NavigationCommands.MoveHomeSmart(s, 2, wrapColumns: 0, M));
        Assert.Equal(2, NavigationCommands.MoveHomeSmart(s, 0, wrapColumns: 0, M));
    }

    [Fact]
    public void MoveHomeSmart_WithWrap_FirstVisualSegment_TogglesFirstNonWsAndLineStart()
    {
        // "  hello world"(ASCII 8px×13=104px)を wrapColumns=8(=64px)で折り返し
        // 視覚 seg 0: [0..8)="  hello "、視覚 seg 1: [8..13)="world"
        // 第 1 セグメントは既存 smart 挙動(firstNonWs=2 ⇔ lineStart=0)
        var s = Snap("  hello world");
        Assert.Equal(2, NavigationCommands.MoveHomeSmart(s, 4, wrapColumns: 8, M)); // 'l'→firstNonWs(2)
        Assert.Equal(0, NavigationCommands.MoveHomeSmart(s, 2, wrapColumns: 8, M)); // firstNonWs→lineStart(0)
        Assert.Equal(2, NavigationCommands.MoveHomeSmart(s, 0, wrapColumns: 8, M)); // lineStart→firstNonWs
    }

    [Fact]
    public void MoveHomeSmart_WithWrap_SecondVisualSegment_GoesToVisualSegmentStart()
    {
        // 継続セグメント(seg 1=[8..13)="world")では常に視覚 seg 先頭へ・トグルなし
        // 論理行先頭(0)へ行かない=これが N-3 の本質的修正
        var s = Snap("  hello world");
        Assert.Equal(8, NavigationCommands.MoveHomeSmart(s, 10, wrapColumns: 8, M)); // 'r'→seg 1 先頭(8)
        Assert.Equal(8, NavigationCommands.MoveHomeSmart(s, 8, wrapColumns: 8, M)); // 'w'(既に seg 先頭)→動かず
        Assert.Equal(8, NavigationCommands.MoveHomeSmart(s, 12, wrapColumns: 8, M)); // 末尾直前→seg 1 先頭
    }

    [Fact]
    public void MoveHomeSmart_WithWrap_EmptyLine_ReturnsLineStart()
    {
        // 空行は視覚セグメントも [(0,0)] 1 個(LineLayout 契約)=lineStart そのまま
        var s = Snap("abc\n\ndef");
        Assert.Equal(4, NavigationCommands.MoveHomeSmart(s, 4, wrapColumns: 8, M)); // 空行(line 1)先頭
    }

    [Fact]
    public void MoveHomeSmart_WithWrap_ThirdVisualSegment_GoesToVisualSegmentStart()
    {
        // 3 セグメント跨ぎ=第 3 セグメントでも同じ挙動を保証
        // "aaaaaaaabbbbbbbbcccccccc"(24 chars ASCII=192px)を wrapColumns=8(=64px)で
        // 視覚 seg 0: [0..8)="aaaaaaaa"、seg 1: [8..16)="bbbbbbbb"、seg 2: [16..24)="cccccccc"
        var s = Snap("aaaaaaaabbbbbbbbcccccccc");
        Assert.Equal(16, NavigationCommands.MoveHomeSmart(s, 20, wrapColumns: 8, M)); // 'c'→seg 2 先頭
        Assert.Equal(8, NavigationCommands.MoveHomeSmart(s, 12, wrapColumns: 8, M)); // 'b'→seg 1 先頭
    }
}
