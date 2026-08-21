using kxEdit.Core.Buffers;
using kxEdit.Core.Layout;

namespace kxEdit.Core.Editing;

/// <summary>
/// TextSnapshot 上のキャレット上下移動(視覚行ベース・純ロジック)。
/// desired X(px)を保持することで、短い行を跨いでも「同じ列」に戻れる(Vim/一般エディタ相当)。
/// EditorControl は Task 6 でここを呼ぶ。呼び出し側は caret を [0, CharLength] にクランプ済み前提。
/// </summary>
/// <remarks>
/// 前提違反時(caret が範囲外/サロゲート中間)は、TextSnapshot 側から
/// <see cref="ArgumentOutOfRangeException"/> が透過的に伝播する(NavigationCommands と同方針)。
/// <paramref name="currentDesiredPx"/> が負値のとき、現在の caret 位置から desired X を新規計算する
/// (慣例的に -1 を渡す=キャレット移動系以外の起点=マウスクリック直後 / 編集直後 / 起動直後)。
/// 判定は実装上 <c>currentDesiredPx &gt;= 0</c>=有効値・それ以外=新規計算。
/// wrapColumns &lt;= 0 は「折り返しなし」= 各論理行 1 視覚行として扱う。
/// </remarks>
public static class VerticalNavigation
{
    /// <summary>下方向 1 視覚行の移動。desired X を保持したまま次の視覚行へ移す。</summary>
    /// <param name="currentDesiredPx">前回移動時の desired X(px)。負値なら現在 caret から新規計算(慣例的に -1 を使う)。</param>
    /// <returns>(移動先 caret, 次回に渡す desiredPx)</returns>
    public static (int caret, int desiredPx) MoveDown(
        TextSnapshot snap,
        int caret,
        int currentDesiredPx,
        int wrapColumns,
        ICharMetrics metrics
    ) => MoveVerticalRelative(snap, caret, currentDesiredPx, wrapColumns, metrics, deltaRows: +1);

    /// <summary>上方向 1 視覚行の移動。</summary>
    /// <param name="currentDesiredPx">前回移動時の desired X(px)。負値なら現在 caret から新規計算(慣例的に -1 を使う)。</param>
    public static (int caret, int desiredPx) MoveUp(
        TextSnapshot snap,
        int caret,
        int currentDesiredPx,
        int wrapColumns,
        ICharMetrics metrics
    ) => MoveVerticalRelative(snap, caret, currentDesiredPx, wrapColumns, metrics, deltaRows: -1);

    /// <summary>下方向 visibleRows 視覚行の移動(PageDown)。visibleRows &lt;= 0 は 1 に丸める。</summary>
    /// <param name="currentDesiredPx">前回移動時の desired X(px)。負値なら現在 caret から新規計算(慣例的に -1 を使う)。</param>
    public static (int caret, int desiredPx) PageDown(
        TextSnapshot snap,
        int caret,
        int currentDesiredPx,
        int wrapColumns,
        int visibleRows,
        ICharMetrics metrics
    ) =>
        MoveVerticalRelative(
            snap,
            caret,
            currentDesiredPx,
            wrapColumns,
            metrics,
            deltaRows: Math.Max(1, visibleRows)
        );

    /// <summary>上方向 visibleRows 視覚行の移動(PageUp)。</summary>
    /// <param name="currentDesiredPx">前回移動時の desired X(px)。負値なら現在 caret から新規計算(慣例的に -1 を使う)。</param>
    public static (int caret, int desiredPx) PageUp(
        TextSnapshot snap,
        int caret,
        int currentDesiredPx,
        int wrapColumns,
        int visibleRows,
        ICharMetrics metrics
    ) =>
        MoveVerticalRelative(
            snap,
            caret,
            currentDesiredPx,
            wrapColumns,
            metrics,
            deltaRows: -Math.Max(1, visibleRows)
        );

    private static (int caret, int desiredPx) MoveVerticalRelative(
        TextSnapshot snap,
        int caret,
        int currentDesiredPx,
        int wrapColumns,
        ICharMetrics metrics,
        int deltaRows
    )
    {
        int logicalLine = snap.GetLineIndexOfChar(caret);
        int lineStart = snap.GetLineStart(logicalLine);
        int lineEnd = snap.GetLineEnd(logicalLine, includeBreak: false);
        string lineText =
            lineEnd == lineStart ? string.Empty : snap.GetText(lineStart, lineEnd - lineStart);
        int maxWidthPx = wrapColumns > 0 ? wrapColumns * metrics.MeasureRun("0") : 0;
        var segs = LineLayout.Wrap(lineText, maxWidthPx, metrics);
        int caretInLine = caret - lineStart;
        int segIdx = FindSegIndex(segs, caretInLine);
        var curSeg = segs[segIdx];
        int localOffset = caretInLine - curSeg.OffsetInLine;
        int desiredPx =
            currentDesiredPx >= 0
                ? currentDesiredPx
                : PixelMapper.OffsetToPx(
                    lineText.AsSpan(curSeg.OffsetInLine, curSeg.Length),
                    localOffset,
                    metrics
                );

        int targetLogicalLine,
            targetSegIdx;
        if (wrapColumns > 0)
        {
            (targetLogicalLine, targetSegIdx) = WalkVisualRows(
                snap,
                logicalLine,
                segIdx,
                segs.Count,
                deltaRows,
                maxWidthPx,
                metrics
            );
        }
        else
        {
            // 折り返しなし: 論理行 = 視覚行なので単純クランプ
            targetLogicalLine = Math.Clamp(logicalLine + deltaRows, 0, snap.LineCount - 1);
            targetSegIdx = 0;
        }

        int targetLineStart = snap.GetLineStart(targetLogicalLine);
        int targetLineEnd = snap.GetLineEnd(targetLogicalLine, includeBreak: false);
        string targetLineText =
            targetLineEnd == targetLineStart
                ? string.Empty
                : snap.GetText(targetLineStart, targetLineEnd - targetLineStart);
        var targetSegs = LineLayout.Wrap(targetLineText, maxWidthPx, metrics);
        // WalkVisualRows はセグメント数を歩きながら渡すので通常はここでのクランプは不要だが、
        // 折り返しなし経路(targetSegIdx=0 固定)と併せて防御的にクランプする。
        var targetSeg = targetSegs[Math.Min(targetSegIdx, targetSegs.Count - 1)];
        var targetSpan = targetLineText.AsSpan(targetSeg.OffsetInLine, targetSeg.Length);
        int localTarget = PixelMapper.PxToOffset(targetSpan, desiredPx, metrics);
        int newCaret = targetLineStart + targetSeg.OffsetInLine + localTarget;
        return (newCaret, desiredPx);
    }

    /// <summary>caretInLine を含む視覚セグメントの index を返す。行末位置(=最終 segEnd)は最終セグメント扱い。</summary>
    private static int FindSegIndex(IReadOnlyList<WrapSegment> segs, int caretInLine) =>
        VisualSegments.FindContaining(segs, caretInLine).Index;

    /// <summary>
    /// 視覚行を deltaRows 分だけ歩き、(論理行, セグメント index) を返す。
    /// 文書端に達したらそこで打ち切る(文書外へは出ない)。
    /// </summary>
    private static (int line, int seg) WalkVisualRows(
        TextSnapshot snap,
        int startLine,
        int startSeg,
        int startSegCount,
        int deltaRows,
        int maxWidthPx,
        ICharMetrics metrics
    )
    {
        int line = startLine,
            seg = startSeg,
            count = startSegCount;
        int step = Math.Sign(deltaRows);
        int remain = Math.Abs(deltaRows);
        while (remain > 0)
        {
            if (step > 0)
            {
                if (seg + 1 < count)
                {
                    seg++;
                }
                else
                {
                    if (line + 1 >= snap.LineCount)
                        return (line, seg); // 文書末で打ち切り
                    line++;
                    seg = 0;
                    count = SegmentCount(snap, line, maxWidthPx, metrics);
                }
            }
            else
            {
                if (seg > 0)
                {
                    seg--;
                }
                else
                {
                    if (line == 0)
                        return (line, seg); // 文書頭で打ち切り
                    line--;
                    count = SegmentCount(snap, line, maxWidthPx, metrics);
                    seg = count - 1;
                }
            }
            remain--;
        }
        return (line, seg);
    }

    private static int SegmentCount(
        TextSnapshot snap,
        int line,
        int maxWidthPx,
        ICharMetrics metrics
    )
    {
        int ls = snap.GetLineStart(line);
        int le = snap.GetLineEnd(line, includeBreak: false);
        string t = le == ls ? string.Empty : snap.GetText(ls, le - ls);
        return LineLayout.Wrap(t, maxWidthPx, metrics).Count;
    }
}
