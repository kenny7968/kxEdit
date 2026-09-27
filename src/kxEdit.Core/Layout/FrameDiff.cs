using kxEdit.Core.Buffers;

namespace kxEdit.Core.Layout;

/// <summary>
/// 1 本の視覚行の絵を決める値(フェーズ 9・計画 §0.2)。行の外に効く入力(幅・フォント・テーマ・行番号幅・
/// 空白表示・折り返し・背景色・水平スクロール)は含めない。それらが同じ 2 つの描画で記述子が等しい行は、
/// 同じ絵になる(<see cref="FrameBuilder.Build"/> の各工程が行ごとに読む値の全部)。
/// </summary>
/// <param name="LogicalLine">行番号の文字列と、現在行の判定に使う。</param>
/// <param name="SegmentIndex">行番号を描くか(0 のときだけ)。</param>
/// <param name="Text">その視覚行の本文(改行なし)。</param>
/// <param name="IsCurrentLine">現在行の強調(工程 2)と、行番号の色(工程 7)。</param>
/// <param name="SelectionStart">選択の行内オフセット(工程 3・5)。交差なしは -1。</param>
/// <param name="SelectionEnd">同上。</param>
/// <param name="CellStart">セル強調の行内オフセット(工程 4・8)。交差なしは -1。</param>
/// <param name="CellEnd">同上。</param>
internal readonly record struct RowPaintKey(
    int LogicalLine,
    int SegmentIndex,
    string Text,
    bool IsCurrentLine,
    int SelectionStart,
    int SelectionEnd,
    int CellStart,
    int CellEnd
)
{
    public bool HasCell => CellStart >= 0;
}

/// <summary>
/// 前回描いた絵と今の絵で、描き直しが要る縦の帯を求める(フェーズ 9・計画 §0.2)。
/// </summary>
internal static class FrameDiff
{
    /// <summary>可視行ごとの記述子。<paramref name="rows"/> は <see cref="ViewportLayout.Build"/> の結果。</summary>
    public static RowPaintKey[] Describe(
        TextSnapshot snapshot,
        IReadOnlyList<VisualRow> rows,
        int currentLineLogical,
        SelectionRange? selection,
        SelectionRange? cellHighlight
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rows);
        var keys = new RowPaintKey[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            string text =
                row.SegmentLength == 0
                    ? string.Empty
                    : snapshot.GetText(row.SegmentStartChar, row.SegmentLength);
            var (selS, selE) = InRow(row, selection);
            var (cellS, cellE) = InRow(row, cellHighlight);
            keys[i] = new RowPaintKey(
                row.LogicalLine,
                row.SegmentIndex,
                text,
                currentLineLogical >= 0 && row.LogicalLine == currentLineLogical,
                selS,
                selE,
                cellS,
                cellE
            );
        }
        return keys;
    }

    /// <summary>
    /// 描き直しが要る縦の帯 [Top, Bottom)(px・昇順・重なりなし・[0, <paramref name="heightPx"/>) で切る)。
    /// 新しい行 i の位置には、古い行 i + <paramref name="shift"/> の画素がある
    /// (<paramref name="shift"/> &gt; 0 = 内容が上へ動いた。スクロールしていなければ 0)。
    /// </summary>
    /// <remarks>
    /// <para>行 i の帯は [i·LH, (i+1)·LH)。新旧のどちらかでセル強調を持つ行は、枠の下辺のために 1 画素下へ延ばす。</para>
    /// <para>
    /// 新しい先頭行の先頭画素(y = 0)には、古い行 shift − 1 の枠の下辺が運ばれてくることがある。
    /// その行がセル強調を持つときだけ [0, 1) を汚れにする(i = −1 の番)。
    /// </para>
    /// <para>両側とも行がない位置は、どちらも背景なので汚れにしない。</para>
    /// </remarks>
    public static List<(int Top, int Bottom)> DirtyBands(
        RowPaintKey[] old,
        RowPaintKey[] now,
        int shift,
        int lineHeight,
        int heightPx
    )
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(now);
        var bands = new List<(int Top, int Bottom)>();
        if (At(old, shift - 1) is { HasCell: true })
            Add(bands, 0, 1, heightPx);

        int end = Math.Max(now.Length, old.Length - shift);
        int runStart = -1;
        bool runHasCell = false;
        for (int i = 0; i <= end; i++)
        {
            RowPaintKey? a = i < end ? At(old, i + shift) : null;
            RowPaintKey? b = i < end ? At(now, i) : null;
            bool dirty = i < end && !Nullable.Equals(a, b);
            if (dirty)
            {
                if (runStart < 0)
                    runStart = i;
                runHasCell = (a?.HasCell ?? false) || (b?.HasCell ?? false);
                continue;
            }
            if (runStart >= 0)
            {
                Add(bands, runStart * lineHeight, i * lineHeight + (runHasCell ? 1 : 0), heightPx);
                runStart = -1;
                runHasCell = false;
            }
        }
        return bands;
    }

    private static RowPaintKey? At(RowPaintKey[] keys, int i) =>
        i >= 0 && i < keys.Length ? keys[i] : null;

    private static void Add(List<(int Top, int Bottom)> bands, int top, int bottom, int heightPx)
    {
        top = Math.Max(0, top);
        bottom = Math.Min(heightPx, bottom);
        if (top >= bottom)
            return;
        // 直前の帯と重なる・接するなら 1 つにする(i = −1 の [0,1) と行 0 の帯など)。
        if (bands.Count > 0 && bands[^1].Bottom >= top)
            bands[^1] = (bands[^1].Top, Math.Max(bands[^1].Bottom, bottom));
        else
            bands.Add((top, bottom));
    }

    private static (int Start, int End) InRow(VisualRow row, SelectionRange? range) =>
        range is SelectionRange r
        && r.Start < r.End
        && FrameBuilder.TryComputeRowIntersection(row, r, out int s, out int e)
            ? (s, e)
            : (-1, -1);
}
