using kxEdit.Core.Buffers;

namespace kxEdit.Core.Layout;

/// <summary>1 本の視覚行の情報(絶対 char offset + クライアント座標 Y)。</summary>
public readonly record struct VisualRow(
    int LogicalLine, // 論理行(0 始まり)
    int SegmentIndex, // その論理行内の視覚行インデックス(0=論理行の先頭視覚行)
    int SegmentStartChar, // その視覚行が担う開始 char offset(絶対・文書先頭から)
    int SegmentLength, // その視覚行の char 長(改行は含まない)
    int YPx // クライアント座標 Y(TopLine の先頭視覚行が Y=0)
);

/// <summary>
/// TopLine + 表示高さ + 折り返し設定から可視の視覚行を列挙する純関数(設計書 §2-3)。
/// 可視外は含めない=フレームコストが O(可視行数) に閉じる。EditorControl / FrameBuilder から呼ぶ。
/// </summary>
internal static class ViewportLayout
{
    /// <summary>
    /// TopLine 以降を積み上げて heightPx を満たす分だけ VisualRow を返す。
    /// - wrapColumns&lt;=0: 折り返し OFF(1 論理行=1 視覚行)
    /// - wrapColumns&gt;0: 半角 wrapColumns 文字分の px を max として折り返しを各行に適用
    ///   (各行は「まだ積める視覚行数」までで打ち切って Wrap する=巨大 1 行でも O(可視行数))
    /// - 空文書(LineCount=1・CharLength=0)は topLine=0 なら "1 個空の視覚行"(EOF キャレット用)を返す
    /// - topLine が LineCount 以上なら空リスト
    /// </summary>
    public static IReadOnlyList<VisualRow> Build(
        TextSnapshot snapshot,
        int topLine,
        int heightPx,
        int wrapColumns,
        ICharMetrics metrics
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(metrics);

        var result = new List<VisualRow>();
        if (topLine < 0 || topLine >= snapshot.LineCount || heightPx <= 0)
            return result;

        // wrap ON なら max px を 1 度だけ計算(GdiCharMetrics 想定でホットパスを守る)。
        // OFF のときは WrapFirstSegments に 0 を渡せば単一セグメントが返る。
        int maxWidthPx = wrapColumns > 0 ? wrapColumns * metrics.MeasureRun("0") : 0;
        int lineHeight = metrics.LineHeightPx;

        int y = 0;
        for (int line = topLine; line < snapshot.LineCount; line++)
        {
            // ビューポートが埋まっていたら、この論理行のテキスト取得も Wrap もしない。
            // 内側ループの先頭にも同じ判定があるため挙動は変わらないが、そちらに任せると
            // 「埋まった次の論理行」に対して 1 セグメントぶんだけ余分に Wrap してしまう
            // (巨大 1 行が次に来ると、その 1 セグメントぶんの GDI 実測が丸ごと無駄になる)。
            if (y >= heightPx)
                return result;

            int lineStart = snapshot.GetLineStart(line);
            int lineEndNoBreak = snapshot.GetLineEnd(line, includeBreak: false);
            int lineLen = lineEndNoBreak - lineStart;
            string lineText = lineLen == 0 ? string.Empty : snapshot.GetText(lineStart, lineLen);

            // 2026-08-02 変更 B: この論理行から実際に使える視覚行数だけ Wrap する。
            // 以前は論理行全体を Wrap してから可視分だけ使っていたため、巨大 1 行では
            // 1 フレームあたり行全体を舐めていた
            // (docs/plans/2026-08-02-large-line-resilience-design.md §2.3)。
            // 内側ループは y >= heightPx で即 return するので、この行から使うのは高々
            // ceil((heightPx - y) / lineHeight) 本。それより多く Wrap しても捨てるだけ。
            // 打ち切り結果は完全な Wrap 結果の prefix なので、返す VisualRow は変わらない。
            // Math.Max(1, ...) は上の早期 return がある限り効かない(y < heightPx なので
            // rowsNeeded >= 1)。WrapFirstSegments の事前条件(1 以上)を呼び出し側で
            // 保証しておくための防御であり、早期 return を外すと実際に効き出す。
            int rowsNeeded =
                lineHeight > 0 ? (heightPx - y + lineHeight - 1) / lineHeight : int.MaxValue;
            var segments = LineLayout
                .WrapFirstSegments(lineText, maxWidthPx, metrics, Math.Max(1, rowsNeeded))
                .Segments;
            for (int si = 0; si < segments.Count; si++)
            {
                if (y >= heightPx)
                    return result;
                var seg = segments[si];
                result.Add(
                    new VisualRow(
                        LogicalLine: line,
                        SegmentIndex: si,
                        SegmentStartChar: lineStart + seg.OffsetInLine,
                        SegmentLength: seg.Length,
                        YPx: y
                    )
                );
                y += lineHeight;
            }
        }
        return result;
    }
}
