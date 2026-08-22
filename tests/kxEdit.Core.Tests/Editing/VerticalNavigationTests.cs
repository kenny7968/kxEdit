using kxEdit.Core.Buffers;
using kxEdit.Core.Editing;
using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Editing;

public class VerticalNavigationTests
{
    private static TextSnapshot Snap(string s) => TextBuffer.FromString(s).Current;

    // half=8px, lineHeight=20px。全角は自動的に half*2=16px。
    // ASCII 1 文字 = 8px なので、行内 col=N の caret の px = N*8。
    private static readonly ICharMetrics M = new MonoCharMetrics(halfWidthPx: 8, lineHeightPx: 20);

    // ===== MoveDown =====
    [Fact]
    public void MoveDown_MovesToSameColumn_WhenNextLineLonger()
    {
        // 行0: "abcdef"(6文字), 行1: "xyzuvw"(6文字)。col=3(caret=3)から Down → 行1の col=3=caret=10(改行1文字含む)
        var s = Snap("abcdef\nxyzuvw");
        var (t, d) = VerticalNavigation.MoveDown(
            s,
            caret: 3,
            currentDesiredPx: -1,
            wrapColumns: 0,
            M
        );
        Assert.Equal(10, t);
        Assert.Equal(24, d); // 3 * 8
    }

    [Fact]
    public void MoveDown_ClampsToShorterLineEnd_KeepsDesired()
    {
        // 行0: "abcdef"(6), 行1: "xy"(2), 行2: "long line here"(14)
        var s = Snap("abcdef\nxy\nlong line here");
        // col=5 の caret=5 から Down 1 回目 → 行1(len=2)で末尾クランプ(=7+2=9)、desired=40(=5*8)
        var (t1, d1) = VerticalNavigation.MoveDown(
            s,
            caret: 5,
            currentDesiredPx: -1,
            wrapColumns: 0,
            M
        );
        Assert.Equal(9, t1);
        Assert.Equal(40, d1);
        // 2 回目(次の行が長い) → desired=40 を保持したまま col=5、行2 の先頭は 10 → 15
        var (t2, d2) = VerticalNavigation.MoveDown(
            s,
            caret: t1,
            currentDesiredPx: d1,
            wrapColumns: 0,
            M
        );
        Assert.Equal(15, t2);
        Assert.Equal(40, d2);
    }

    [Fact]
    public void MoveDown_AtLastLine_ReturnsSameLineOrEol()
    {
        var s = Snap("abc"); // 1 論理行のみ
        var (t, d) = VerticalNavigation.MoveDown(
            s,
            caret: 1,
            currentDesiredPx: -1,
            wrapColumns: 0,
            M
        );
        // deltaRows=+1 の Clamp で targetLogicalLine=0(変化なし)。desired px=8 → 行0 の col=1 位置=1
        Assert.Equal(1, t);
        Assert.Equal(8, d);
    }

    // ===== MoveUp =====
    [Fact]
    public void MoveUp_AtTopLine_NoOp()
    {
        var s = Snap("abc\ndef");
        var (t, _) = VerticalNavigation.MoveUp(
            s,
            caret: 1,
            currentDesiredPx: -1,
            wrapColumns: 0,
            M
        );
        Assert.Equal(1, t); // Clamp で targetLogicalLine=0(変化なし)、desired=8 → 行0 の col=1=1
    }

    [Fact]
    public void MoveUp_FromSecondLine_MovesToFirstLineSameColumn()
    {
        // 行0: "abcdef"(6), 行1: "xyzuvw"(6)。行1 の col=3(caret=10)から Up → 行0 の col=3=caret=3
        var s = Snap("abcdef\nxyzuvw");
        var (t, _) = VerticalNavigation.MoveUp(
            s,
            caret: 10,
            currentDesiredPx: -1,
            wrapColumns: 0,
            M
        );
        Assert.Equal(3, t);
    }

    // ===== PageDown / PageUp =====
    [Fact]
    public void PageDown_MovesByVisibleRows()
    {
        // 10 論理行、visibleRows=3 → 3 論理行下へ
        var s = Snap("l0\nl1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9");
        var (t, _) = VerticalNavigation.PageDown(
            s,
            caret: 0,
            currentDesiredPx: -1,
            wrapColumns: 0,
            visibleRows: 3,
            M
        );
        // 行3 の先頭。"l0\nl1\nl2\n"=9 文字後 → 9
        Assert.Equal(9, t);
    }

    [Fact]
    public void PageUp_MovesByVisibleRows()
    {
        var s = Snap("l0\nl1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9");
        // 行9(caret=27)から PageUp visibleRows=3 → 行6 の先頭=18
        var (t, _) = VerticalNavigation.PageUp(
            s,
            caret: 27,
            currentDesiredPx: -1,
            wrapColumns: 0,
            visibleRows: 3,
            M
        );
        Assert.Equal(18, t);
    }

    // ===== 折り返し ON =====
    [Fact]
    public void MoveDown_WithWrap_StaysInSameLogicalLine_ForNextVisualRow()
    {
        // wrapColumns=3 → maxWidthPx=24。1 文字=8px なので視覚行1本に 3 文字。
        // 行0: "abcdef" → 視覚 [(0,3)="abc", (3,3)="def"]
        var s = Snap("abcdef");
        // col=1(caret=1)から Down → 同論理行の次の視覚行 = "def" の col=1 位置。
        // desired=8, targetSeg=(3,3) → localOffset=1 → caret = 0 + 3 + 1 = 4
        var (t, d) = VerticalNavigation.MoveDown(
            s,
            caret: 1,
            currentDesiredPx: -1,
            wrapColumns: 3,
            M
        );
        Assert.Equal(4, t);
        Assert.Equal(8, d);
    }

    // ===== 補足エッジケース =====
    [Fact]
    public void MoveDown_OnEmptyBuffer_NoOp()
    {
        // 空文書。LineCount=1、GetLineStart(0)=0=GetLineEnd(0,false)。
        // Wrap は [(0,0)]、desired=0、下方向でも Clamp で行 0 のまま、caret=0。
        var s = Snap("");
        var (t, d) = VerticalNavigation.MoveDown(
            s,
            caret: 0,
            currentDesiredPx: -1,
            wrapColumns: 0,
            M
        );
        Assert.Equal(0, t);
        Assert.Equal(0, d);
    }

    [Fact]
    public void MoveUp_OnEmptyBuffer_NoOp()
    {
        var s = Snap("");
        var (t, d) = VerticalNavigation.MoveUp(
            s,
            caret: 0,
            currentDesiredPx: -1,
            wrapColumns: 0,
            M
        );
        Assert.Equal(0, t);
        Assert.Equal(0, d);
    }

    [Fact]
    public void MoveDown_KeepsCurrentDesiredPx_WhenProvided()
    {
        // currentDesiredPx>=0 の場合、現在 caret の X を再計算せずそのまま採用する。
        // 行0: "abc"(len=3), 行1: "abcdefgh"(len=8)。caret=0 で desired=48 を渡す → 行1 の col=6=caret=4+6=10。
        var s = Snap("abc\nabcdefgh");
        var (t, d) = VerticalNavigation.MoveDown(
            s,
            caret: 0,
            currentDesiredPx: 48,
            wrapColumns: 0,
            M
        );
        Assert.Equal(10, t);
        Assert.Equal(48, d);
    }

    // ===== S-1: サロゲート/CJK 統合(PixelMapper の code-point 対応の回帰保険) =====
    [Fact]
    public void MoveDown_SurrogatePair_DoesNotSplit()
    {
        // 行0: "a😀b"(surrogate: a=1, 😀=2(high@1,low@2), b=1 → CharLength=4)
        // 行1: "wxyz"(4 code units)
        // MonoCharMetrics: a=8, 😀=16(サロゲートペア=half*2=16px), b=8
        // caret=1(a の直後)から Down → desired=8 → 行1 の col=1 に相当(px=8)→ 5+1=6
        var s = Snap("a😀b\nwxyz");
        var (t, d) = VerticalNavigation.MoveDown(
            s,
            caret: 1,
            currentDesiredPx: -1,
            wrapColumns: 0,
            M
        );
        Assert.Equal(6, t);
        Assert.Equal(8, d);

        // caret=3(😀 の直後=high と low の後)から Down → desired=24(=a 8 + 😀 16)
        // 行1 col=3 位置(px=24)→ 5+3=8
        var (t2, d2) = VerticalNavigation.MoveDown(
            s,
            caret: 3,
            currentDesiredPx: -1,
            wrapColumns: 0,
            M
        );
        Assert.Equal(8, t2);
        Assert.Equal(24, d2);
    }

    [Fact]
    public void MoveDown_CjkFullWidth_KeepsPxColumn()
    {
        // "あいう"(全角3・各16px) → 行末 caret=3 の px = 48
        // 行1: "abcdef"(半角6・各8px)。desired=48 は "abcdef" 全長=48 とちょうど一致 → 行末 caret=4+6=10
        var s = Snap("あいう\nabcdef");
        var (t, d) = VerticalNavigation.MoveDown(
            s,
            caret: 3,
            currentDesiredPx: -1,
            wrapColumns: 0,
            M
        );
        Assert.Equal(10, t);
        Assert.Equal(48, d);
    }

    // ===== P8-1b: 折り返し行の視覚行単位移動(N-3 検証追加分) =====

    [Fact]
    public void MoveUp_WithWrap_FromContinuationSeg_GoesToPreviousVisualRowSameLogicalLine()
    {
        // "abcdef" wrapColumns=3 → 視覚 [(0,3)="abc",(3,3)="def"]
        // caret=4('e' in seg 1)から Up → 同論理行の前 seg の col=1='b'=caret=1
        var s = Snap("abcdef");
        var (t, d) = VerticalNavigation.MoveUp(
            s,
            caret: 4,
            currentDesiredPx: -1,
            wrapColumns: 3,
            M
        );
        Assert.Equal(1, t);
        Assert.Equal(8, d);
    }

    [Fact]
    public void MoveDown_WithWrap_FromLastSegOfLogicalLine_GoesToNextLogicalLineFirstSeg()
    {
        // 行0: "abcdef" 視覚 [(0,3)="abc",(3,3)="def"]
        // 行1: "ghijkl" 視覚 [(0,3)="ghi",(3,3)="jkl"]
        // caret=4('e' in seg 1 of line 0)から Down → 行1 の seg 0 の col=1='h'=caret=8
        //   (改行分 +1 で行1先頭は 7、seg 0 col=1 → 7+0+1=8)
        var s = Snap("abcdef\nghijkl");
        var (t, d) = VerticalNavigation.MoveDown(
            s,
            caret: 4,
            currentDesiredPx: -1,
            wrapColumns: 3,
            M
        );
        Assert.Equal(8, t);
        Assert.Equal(8, d);
    }

    [Fact]
    public void MoveDown_WithWrap_MultipleVisualSegments_TraversesEachSegOnEachDown()
    {
        // "aaaaaaaaa"(9 文字)wrapColumns=3 → 視覚 [(0,3),(3,3),(6,3)]
        // caret=0 から Down 1 → seg 1 の col=0 = 3
        var s = Snap("aaaaaaaaa");
        var (t1, d1) = VerticalNavigation.MoveDown(
            s,
            caret: 0,
            currentDesiredPx: -1,
            wrapColumns: 3,
            M
        );
        Assert.Equal(3, t1);
        Assert.Equal(0, d1);
        // Down 2 → seg 2 の col=0 = 6
        var (t2, d2) = VerticalNavigation.MoveDown(
            s,
            caret: t1,
            currentDesiredPx: d1,
            wrapColumns: 3,
            M
        );
        Assert.Equal(6, t2);
        Assert.Equal(0, d2);
    }

    [Fact]
    public void MoveUp_WithWrap_FromFirstSegOfLogicalLine_GoesToPreviousLogicalLineLastSeg()
    {
        // 行0: "abcdef" 視覚 [(0,3),(3,3)]、行1: "ghijkl" 視覚 [(0,3),(3,3)]
        // caret=7('g' in seg 0 of line 1)から Up → 行0 の seg 1(=最後の視覚行)の col=0='d'=caret=3
        var s = Snap("abcdef\nghijkl");
        var (t, d) = VerticalNavigation.MoveUp(
            s,
            caret: 7,
            currentDesiredPx: -1,
            wrapColumns: 3,
            M
        );
        Assert.Equal(3, t);
        Assert.Equal(0, d);
    }

    // ===== A-5(2026-08-22 監査): 視覚行の右端に着地するケース =====
    // 不変条件 I-1: 非最終セグメントへの着地は segEnd 未満でなければならない。
    // segEnd は描画・照会の双方で「次の視覚行の先頭」を意味するため、そこへ着地すると
    // ↓ は 1 行飛んで見え、↑ は同じ値へ着地し続けて動かなくなる。
    //
    // fixture: wrapColumns=4 / 半角 8px → maxWidthPx=32。
    //   行 0 "abcd"          → 視覚 [(0,4)]                     … 幅ぴったり 32px = 1 本
    //   行 1 "xxxxyyyyzzzz"  → 視覚 [(0,4),(4,4),(8,4)]         … 絶対 offset 5,9,13
    //   行 2 "end"           → 視覚 [(0,3)]                     … 絶対 offset 18
    private const string WrapFixture = "abcd\nxxxxyyyyzzzz\nend";

    [Fact]
    public void MoveDown_WithWrap_FromLineEnd_LandsInFirstVisualRow_NotSecond()
    {
        // 行 0 の行末(caret=4 / desiredPx=32=右端)から ↓。
        // 移動先は行 1 の視覚行 0 = "xxxx"(絶対 [5,9))。
        // 修正前は 9(= "yyyy" の行頭 = 視覚行 1 の先頭)に着地して 1 行飛んでいた。
        var s = Snap(WrapFixture);
        var (t, d) = VerticalNavigation.MoveDown(
            s,
            caret: 4,
            currentDesiredPx: -1,
            wrapColumns: 4,
            M
        );
        Assert.Equal(32, d);
        Assert.InRange(t, 5, 8); // 視覚行 0 の内側(9=次の視覚行の先頭 ではない)
        Assert.Equal(8, t); // 右端 = 最後のコードポイントの先頭
    }

    [Fact]
    public void MoveUp_WithWrap_FromRightEdge_ActuallyMovesEveryTime()
    {
        // A-5 の主症状。右端の desiredPx を保ったまま ↑ を 3 回押し、毎回 caret が動くこと。
        // 修正前は 1 回目以降ずっと同じ値に着地して「↑ が効かない」状態だった。
        var s = Snap(WrapFixture);
        // 行 1 の視覚行 2("zzzz")の右端に相当する位置=行末(17)から開始する。
        int caret = 17;
        int desired = -1;
        var visited = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            int before = caret;
            (caret, desired) = VerticalNavigation.MoveUp(s, caret, desired, wrapColumns: 4, M);
            Assert.True(
                caret < before,
                $"↑ {i + 1} 回目で caret が動いていない (before={before}, after={caret})"
            );
            visited.Add(caret);
        }
        // 視覚行 1 の右端 → 視覚行 0 の右端 → 行 0 の行末
        Assert.Equal(new[] { 12, 8, 4 }, visited);
    }

    [Fact]
    public void MoveDown_WithWrap_RightEdge_TraversesEachVisualRowOnce()
    {
        // ↓ 側の対称テスト。右端を保ったまま 1 視覚行ずつ降りること(飛ばさないこと)。
        var s = Snap(WrapFixture);
        int caret = 4; // 行 0 の行末
        int desired = -1;
        var visited = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            (caret, desired) = VerticalNavigation.MoveDown(s, caret, desired, wrapColumns: 4, M);
            visited.Add(caret);
        }
        // 視覚行 0 の右端 → 視覚行 1 の右端 → 視覚行 2 は最終セグメント=行末(17)まで行ける
        Assert.Equal(new[] { 8, 12, 17 }, visited);
    }

    [Fact]
    public void MoveDown_WithWrap_LastSegment_StillLandsAtLogicalLineEnd()
    {
        // クランプの過剰適用防止。最終セグメントは segEnd(=行末)に着地してよい
        // (そこは「次の視覚行の先頭」ではないため)。
        var s = Snap(WrapFixture);
        // 行 2 "end" は 1 セグメント=最終。右端 desiredPx で ↓ すると行末(21)。
        var (t, _) = VerticalNavigation.MoveDown(
            s,
            caret: 13,
            currentDesiredPx: 32,
            wrapColumns: 4,
            M
        );
        Assert.Equal(21, t); // 18 + 3 = "end" の行末
    }

    [Fact]
    public void MoveDownThenUp_WithWrap_RightEdge_ReturnsToOriginalVisualRow()
    {
        // 往復。desiredPx を保持しているので元の視覚行へ戻る。
        var s = Snap(WrapFixture);
        var (down, d1) = VerticalNavigation.MoveDown(
            s,
            caret: 4,
            currentDesiredPx: -1,
            wrapColumns: 4,
            M
        );
        var (up, _) = VerticalNavigation.MoveUp(s, down, d1, wrapColumns: 4, M);
        Assert.Equal(4, up);
    }
}
