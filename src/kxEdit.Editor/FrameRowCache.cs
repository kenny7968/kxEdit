// FrameRowCache.cs
// 2026-09-27 性能改善フェーズ 9(計画 §0.2): 描画の入力 → 可視行と行の記述子のキャッシュ。
// 差分の無効化(前回描いた入力と今の入力)と、直後の描画(今の入力)が同じ可視行を 2 度作らないためのもの。
using kxEdit.Core.Layout;

namespace kxEdit.Editor;

/// <summary>
/// 直近 2 つの描画の入力について、可視行(<see cref="EditorControl.BuildVisibleRows"/>)と記述子を持つ。
/// 入力は <see cref="FrameInputs.Equals(FrameInputs?)"/> で引く。UI スレッド専用。
/// </summary>
/// <remarks>
/// 2 件なのは「画面の絵の入力」と「今の入力」の 2 つが同時に要るため。入力はスナップショットを参照するので、
/// 本文やフォントを丸ごと差し替える経路では <see cref="Clear"/> で捨てる(古い本文を握らない)。
/// </remarks>
internal sealed class FrameRowCache
{
    private sealed class Entry(FrameInputs inputs, IReadOnlyList<VisualRow> rows)
    {
        public FrameInputs Inputs { get; } = inputs;
        public IReadOnlyList<VisualRow> Rows { get; } = rows;
        public RowPaintKey[]? Keys { get; set; }
    }

    private Entry? _recent;
    private Entry? _older;

    public IReadOnlyList<VisualRow> Rows(FrameInputs inputs) => Get(inputs).Rows;

    public RowPaintKey[] Keys(FrameInputs inputs)
    {
        var e = Get(inputs);
        return e.Keys ??= FrameDiff.Describe(
            inputs.Snapshot,
            e.Rows,
            inputs.CurrentLineLogical,
            inputs.Selection,
            inputs.CellHighlight
        );
    }

    public void Clear()
    {
        _recent = null;
        _older = null;
    }

    private Entry Get(FrameInputs inputs)
    {
        if (_recent is not null && _recent.Inputs.Equals(inputs))
            return _recent;
        if (_older is not null && _older.Inputs.Equals(inputs))
        {
            (_recent, _older) = (_older, _recent);
            return _recent;
        }
        var e = new Entry(inputs, EditorControl.BuildVisibleRows(inputs));
        _older = _recent;
        _recent = e;
        return e;
    }
}
