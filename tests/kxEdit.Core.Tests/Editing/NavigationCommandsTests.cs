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
    public void MoveLineHome_Smart_TogglesBetweenFirstNonWsAndLineStart()
    {
        var s = Snap("  hello");
        Assert.Equal(2, NavigationCommands.MoveLineHome(s, 4, skipIndent: true)); // 本文内(4='l')→ firstNonWs(2)
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 2, skipIndent: true)); // firstNonWs → lineStart
        Assert.Equal(2, NavigationCommands.MoveLineHome(s, 0, skipIndent: true)); // lineStart → firstNonWs
    }

    [Fact]
    public void MoveLineHome_Smart_TabsAsWhitespace()
    {
        var s = Snap("\t\thello");
        Assert.Equal(2, NavigationCommands.MoveLineHome(s, 4, skipIndent: true));
    }

    [Fact]
    public void MoveLineHome_Smart_EmptyLine_ReturnsLineStart()
    {
        var s = Snap("abc\n\nxyz"); // 行1 は空行(lineStart=lineEnd=4)
        Assert.Equal(4, NavigationCommands.MoveLineHome(s, 4, skipIndent: true)); // firstNonWs=lineEnd=4=lineStart相当
    }

    [Fact]
    public void MoveLineHome_Smart_LineWithOnlyWhitespace_TogglesLineStartLineEnd()
    {
        var s = Snap("   "); // 空白のみ(firstNonWs=lineEnd=3)
        Assert.Equal(3, NavigationCommands.MoveLineHome(s, 0, skipIndent: true)); // lineStart(0) → firstNonWs(3)
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 3, skipIndent: true)); // firstNonWs(3) → lineStart(0)
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
    public void MoveLineHome_Smart_OnEmptyBuffer_ReturnsZero()
    {
        // 空文書での不変性(暗黙成立を明文化)
        var s = Snap("");
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 0, skipIndent: true));
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

    // ===== 2026-09-04: skipIndent=false(常に行頭)モード =====

    [Fact]
    public void MoveLineHome_NoSkipIndent_AlwaysReturnsLineStart()
    {
        // 非既定位置(本文内)から始める。1 回目で行頭へ、2 回目は動かない
        // (行頭から始めると「既定位置と同じ」なのか「動かなかった」のか区別できない)。
        var s = Snap("  hello");
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 4, skipIndent: false));
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 0, skipIndent: false)); // no-op(トグルしない)
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 2, skipIndent: false)); // firstNonWs でもトグルしない
    }

    [Fact]
    public void MoveLineHome_NoSkipIndent_SecondLine_ReturnsThatLineStart()
    {
        // 論理行 1 本目だけで検証すると lineStart==0 になり、"0 を返すだけ" の実装と区別できない。
        var s = Snap("abc\r\n  def");
        Assert.Equal(5, NavigationCommands.MoveLineHome(s, 8, skipIndent: false)); // 行1 の先頭
        Assert.Equal(5, NavigationCommands.MoveLineHome(s, 7, skipIndent: false)); // 行1 の firstNonWs でも
    }

    [Fact]
    public void MoveLineHome_NoSkipIndent_LineWithOnlyWhitespace_ReturnsLineStart()
    {
        // スマート版はここで lineStart ⇔ lineEnd をトグルする。非トグルであることを固定する。
        var s = Snap("   ");
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 3, skipIndent: false));
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 0, skipIndent: false));
    }

    [Fact]
    public void MoveLineHome_NoSkipIndent_EmptyLine_ReturnsLineStart()
    {
        // 注意: 空行では smart 側も同じ 4 を返す(MoveLineHome_Smart_EmptyLine_ReturnsLineStart と
        // fixture・caret・期待値が同一)。よって本テストは<b>モード差を区別しない</b>——
        // false 側の早期 return を空行(lineStart==lineEnd)で踏んでも throw しない、という
        // 境界の網である。false モードの挙動差の網として数えないこと。
        var s = Snap("abc\n\nxyz"); // 行1 は空行(lineStart=lineEnd=4)
        Assert.Equal(4, NavigationCommands.MoveLineHome(s, 4, skipIndent: false));
    }

    [Fact]
    public void MoveLineHome_NoSkipIndent_WithWrap_FirstVisualSegment_ReturnsLineStart()
    {
        // "  hello world" を wrapColumns=8 で折り返し(seg 0=[0..8) / seg 1=[8..13))。
        // 第 1 セグメントでは firstNonWs(2)へ行かず lineStart(0)へ。
        var s = Snap("  hello world");
        Assert.Equal(
            0,
            NavigationCommands.MoveLineHome(s, 4, wrapColumns: 8, M, skipIndent: false)
        );
        Assert.Equal(
            0,
            NavigationCommands.MoveLineHome(s, 2, wrapColumns: 8, M, skipIndent: false)
        );
    }

    [Fact]
    public void MoveLineHome_NoSkipIndent_WithWrap_SecondVisualSegment_StaysOnVisualSegmentStart()
    {
        // 「常に行頭」でも継続セグメントでは論理行頭(0)へ飛ばず視覚行頭(8)に留まる。
        // = P8-1a(NVDA が視覚行の先頭から読む)の特性が両モードで保たれる、が本テストの主張。
        var s = Snap("  hello world");
        Assert.Equal(
            8,
            NavigationCommands.MoveLineHome(s, 10, wrapColumns: 8, M, skipIndent: false)
        );
        Assert.Equal(
            8,
            NavigationCommands.MoveLineHome(s, 8, wrapColumns: 8, M, skipIndent: false)
        );
    }

    [Fact]
    public void MoveLineHome_NoSkipIndent_WithWrap_Disabled_SameAsLogicalLine()
    {
        // wrapColumns<=0 は論理行版へ委譲される(委譲が skipIndent を落としていないことの網)。
        var s = Snap("  hello");
        Assert.Equal(
            0,
            NavigationCommands.MoveLineHome(s, 4, wrapColumns: 0, M, skipIndent: false)
        );
    }

    [Fact]
    public void MoveLineHome_WithWrap_NonFirstLogicalLine_UsesThatLineStart()
    {
        // 折り返し版のテストが全て論理行 0(lineStart==0)だと、visualStart / visualEnd の
        // "lineStart +" を落とした実装(seg.OffsetInLine をそのまま返す)と区別できない。
        // 行頭が 0 でない論理行 1 で検証する。
        //
        // "abc\r\n    hello world"
        //   論理行 1: lineStart=5 / lineEnd=20 / 本文 "    hello world"(15 文字)
        //   wrapColumns=8(=64px・ASCII 8px)→ seg 0=行内[0..8)="    hell"、seg 1=行内[8..15)="o world"
        //   絶対 offset では seg 0=[5..13)、seg 1=[13..20)
        //   先頭空白は 4 文字(5,6,7,8)なので firstNonWs=9。
        //   ※ インデントを 4 文字にしているのは意図的 —— 2 文字だと firstNonWs(7)が
        //     seg.Length(8)未満に収まり、visualEnd の "lineStart +" 落ちを検出できない。
        var s = Snap("abc\r\n    hello world");
        Assert.Equal(20, s.CharLength);

        // 継続セグメント(caret=15='w')→ 視覚 seg 1 先頭=13。両モードとも。
        Assert.Equal(
            13,
            NavigationCommands.MoveLineHome(s, 15, wrapColumns: 8, M, skipIndent: true)
        );
        Assert.Equal(
            13,
            NavigationCommands.MoveLineHome(s, 15, wrapColumns: 8, M, skipIndent: false)
        );

        // 第 1 セグメント(caret=11='l')→ smart は firstNonWs(9)、常に行頭は lineStart(5)。
        Assert.Equal(
            9,
            NavigationCommands.MoveLineHome(s, 11, wrapColumns: 8, M, skipIndent: true)
        );
        Assert.Equal(
            5,
            NavigationCommands.MoveLineHome(s, 11, wrapColumns: 8, M, skipIndent: false)
        );
    }

    [Fact]
    public void MoveLineHome_WithWrap_ContinuationSegmentStartingWithSpaces_StaysOnSegmentStart()
    {
        // LineLayout.Wrap は語境界も空白トリムも持たない純 code-point 貪欲なので、
        // 継続セグメントが空白で始まることは普通に起きる。そのとき smart モードでも
        // firstNonWs へ飛ばず視覚 seg 先頭に留まる = P8-1a を smart トグルより優先する、が本テストの主張。
        //   "aaaaaaaa    bbbb"(16 文字)→ seg 0=[0..8)="aaaaaaaa"、seg 1=[8..16)="    bbbb"
        //   seg 1 内の firstNonWs は 12('b')。ガードが無ければ 12 が返る。
        var s = Snap("aaaaaaaa    bbbb");
        Assert.Equal(
            8,
            NavigationCommands.MoveLineHome(s, 10, wrapColumns: 8, M, skipIndent: true)
        );
        Assert.Equal(
            8,
            NavigationCommands.MoveLineHome(s, 10, wrapColumns: 8, M, skipIndent: false)
        );
    }

    [Fact]
    public void MoveLineHome_WithWrap_AtCharLength_ReturnsLastVisualSegmentStart()
    {
        // EOF キャレット(caret == CharLength)で throw しない契約を折り返し版でも固定する。
        // 行末位置は最終セグメント扱い(VisualSegments.FindContaining 契約)なので seg 1 先頭=8。
        var s = Snap("  hello world");
        Assert.Equal(13, s.CharLength);
        Assert.Equal(
            8,
            NavigationCommands.MoveLineHome(s, s.CharLength, wrapColumns: 8, M, skipIndent: true)
        );
        Assert.Equal(
            8,
            NavigationCommands.MoveLineHome(s, s.CharLength, wrapColumns: 8, M, skipIndent: false)
        );
    }

    // ===== P8-1a: 視覚行ベース Home キー(wrapColumns/metrics 版) =====

    [Fact]
    public void MoveLineHome_Smart_WithWrap_WrapDisabled_SameAsLogicalLine()
    {
        // wrapColumns<=0 は既存論理行挙動と同じ=既存 3 パターンを再現
        var s = Snap("  hello");
        Assert.Equal(2, NavigationCommands.MoveLineHome(s, 4, wrapColumns: 0, M, skipIndent: true));
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 2, wrapColumns: 0, M, skipIndent: true));
        Assert.Equal(2, NavigationCommands.MoveLineHome(s, 0, wrapColumns: 0, M, skipIndent: true));
    }

    [Fact]
    public void MoveLineHome_Smart_WithWrap_FirstVisualSegment_TogglesFirstNonWsAndLineStart()
    {
        // "  hello world"(ASCII 8px×13=104px)を wrapColumns=8(=64px)で折り返し
        // 視覚 seg 0: [0..8)="  hello "、視覚 seg 1: [8..13)="world"
        // 第 1 セグメントは既存 smart 挙動(firstNonWs=2 ⇔ lineStart=0)
        var s = Snap("  hello world");
        Assert.Equal(2, NavigationCommands.MoveLineHome(s, 4, wrapColumns: 8, M, skipIndent: true)); // 'l'→firstNonWs(2)
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 2, wrapColumns: 8, M, skipIndent: true)); // firstNonWs→lineStart(0)
        Assert.Equal(2, NavigationCommands.MoveLineHome(s, 0, wrapColumns: 8, M, skipIndent: true)); // lineStart→firstNonWs
    }

    [Fact]
    public void MoveLineHome_Smart_WithWrap_SecondVisualSegment_GoesToVisualSegmentStart()
    {
        // 継続セグメント(seg 1=[8..13)="world")では常に視覚 seg 先頭へ・トグルなし
        // 論理行先頭(0)へ行かない=これが N-3 の本質的修正
        var s = Snap("  hello world");
        Assert.Equal(
            8,
            NavigationCommands.MoveLineHome(s, 10, wrapColumns: 8, M, skipIndent: true)
        ); // 'r'→seg 1 先頭(8)
        Assert.Equal(8, NavigationCommands.MoveLineHome(s, 8, wrapColumns: 8, M, skipIndent: true)); // 'w'(既に seg 先頭)→動かず
        Assert.Equal(
            8,
            NavigationCommands.MoveLineHome(s, 12, wrapColumns: 8, M, skipIndent: true)
        ); // 末尾直前→seg 1 先頭
    }

    [Fact]
    public void MoveLineHome_Smart_WithWrap_EmptyLine_ReturnsLineStart()
    {
        // 空行は視覚セグメントも [(0,0)] 1 個(LineLayout 契約)=lineStart そのまま
        var s = Snap("abc\n\ndef");
        Assert.Equal(4, NavigationCommands.MoveLineHome(s, 4, wrapColumns: 8, M, skipIndent: true)); // 空行(line 1)先頭
    }

    [Fact]
    public void MoveLineHome_Smart_WithWrap_ThirdVisualSegment_GoesToVisualSegmentStart()
    {
        // 3 セグメント跨ぎ=第 3 セグメントでも同じ挙動を保証
        // "aaaaaaaabbbbbbbbcccccccc"(24 chars ASCII=192px)を wrapColumns=8(=64px)で
        // 視覚 seg 0: [0..8)="aaaaaaaa"、seg 1: [8..16)="bbbbbbbb"、seg 2: [16..24)="cccccccc"
        var s = Snap("aaaaaaaabbbbbbbbcccccccc");
        Assert.Equal(
            16,
            NavigationCommands.MoveLineHome(s, 20, wrapColumns: 8, M, skipIndent: true)
        ); // 'c'→seg 2 先頭
        Assert.Equal(
            8,
            NavigationCommands.MoveLineHome(s, 12, wrapColumns: 8, M, skipIndent: true)
        ); // 'b'→seg 1 先頭
    }
}
