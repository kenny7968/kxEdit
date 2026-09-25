// FrameInputs.cs
// 2026-09-25 性能改善フェーズ 3(設計書 §8.1): 描画が読む状態の全部を 1 つの不変値に集めたもの。
// 製品コードでは EditorControl.CaptureFrameInputs() だけが作り、OnPaint はこの値だけからフレームを組み立てて描く。
// 描画に新しい入力を足すときは、必ずここにメンバーを足し、Equals と FrameInputsTests も直すこと
// (足さずに描画から生の状態を読むと、キャレット移動で再描画を省いたときに古い絵が画面に残る)。
// ここに入る状態を書き換える経路は必ず自分で Invalidate する規則は EditorControl._lastPaintedInputs のコメントを参照。
using kxEdit.Core.Buffers;
using kxEdit.Core.Editing;
using kxEdit.Core.Layout;
using SelectionRange = kxEdit.Core.Layout.SelectionRange;

namespace kxEdit.Editor;

/// <summary>
/// 1 回の描画の入力(設計書 §8.1)。等しい 2 つの値からは、同じ絵が描かれる。
/// </summary>
/// <remarks>
/// <para>
/// <b>比べ方</b>: <see cref="Snapshot"/>・<see cref="Metrics"/>・フォント 3 つは<b>参照</b>で比べる
/// (スナップショットは編集ごとに新しい参照になる。<see cref="System.Drawing.Font"/> の <c>Equals</c> は値比較で、
/// 破棄済みのフォントに対しても走ってしまうので使わない)。<see cref="BackColor"/> は ARGB で比べる
/// (<c>Color.Equals</c> は名前の有無まで見るが、<c>g.Clear</c> の画素は同じ)。それ以外は値で比べる。
/// <see cref="Ime"/> は record struct の既定の等値で、配列(Attrs / Clauses)は参照比較になる
/// =打鍵ごとに「変化あり」(安全側)。
/// 比較は <c>Equals</c> を使う。null(記録なし)は常に「変化あり」として扱う
/// (record の <c>==</c> は null 同士を true にするので使わない)。
/// </para>
/// <para>
/// <b>IME の未確定表示だけは例外</b>: <see cref="ImeController.Draw"/> は host 経由で生の状態を読む。
/// 描画は <c>CaptureFrameInputs()</c> と同じ同期処理の中で走るので値は一致し、読む状態
/// (<see cref="Ime"/>・<see cref="ScrollX"/>・<c>ComputeCaretPoint</c> の入力・フォント・<see cref="Style"/>)は
/// すべてここに入っている(実装計画 docs/plans/2026-09-25-perf-skip-invalidate.md §0.2)。
/// </para>
/// </remarks>
internal sealed record FrameInputs
{
    public required TextSnapshot Snapshot { get; init; }
    public required int TopLine { get; init; }
    public required int TopSegment { get; init; }
    public required int ScrollX { get; init; }
    public required int WrapColumns { get; init; }

    /// <summary>
    /// 描画は直接読まない。描画先(バックバッファ = ClientRectangle = <c>g.Clear</c> の範囲)の大きさ。
    /// ResizeRedraw があるので比較上は冗長だが、<see cref="PaintWidth"/> / <see cref="PaintHeight"/> に
    /// 現れない変化も「変化あり」にするため、安全側で残す。
    /// </summary>
    public required Size ClientSize { get; init; }

    public required int PaintWidth { get; init; }
    public required int PaintHeight { get; init; }
    public required bool ShowLineNumbers { get; init; }

    /// <summary>行番号の非表示時は 0。</summary>
    public required int LineNumberWidth { get; init; }

    /// <summary>現在行の強調が効く論理行。強調 OFF・選択中は -1。</summary>
    public required int CurrentLineLogical { get; init; }

    public required SelectionRange? Selection { get; init; }
    public required SelectionRange? CellHighlight { get; init; }
    public required bool ShowWhitespace { get; init; }
    public required ViewportStyle Style { get; init; }
    public required GdiCharMetrics Metrics { get; init; }
    public required Font Font { get; init; }
    public required Font UnderlineFont { get; init; }
    public required Font TargetFont { get; init; }
    public required Color BackColor { get; init; }
    public required ImeCompositionState Ime { get; init; }

    public bool Equals(FrameInputs? other) =>
        other is not null
        && ReferenceEquals(Snapshot, other.Snapshot)
        && TopLine == other.TopLine
        && TopSegment == other.TopSegment
        && ScrollX == other.ScrollX
        && WrapColumns == other.WrapColumns
        && ClientSize == other.ClientSize
        && PaintWidth == other.PaintWidth
        && PaintHeight == other.PaintHeight
        && ShowLineNumbers == other.ShowLineNumbers
        && LineNumberWidth == other.LineNumberWidth
        && CurrentLineLogical == other.CurrentLineLogical
        && Selection == other.Selection
        && CellHighlight == other.CellHighlight
        && ShowWhitespace == other.ShowWhitespace
        && Style.Equals(other.Style)
        && ReferenceEquals(Metrics, other.Metrics)
        && ReferenceEquals(Font, other.Font)
        && ReferenceEquals(UnderlineFont, other.UnderlineFont)
        && ReferenceEquals(TargetFont, other.TargetFont)
        && BackColor.ToArgb() == other.BackColor.ToArgb()
        && Ime.Equals(other.Ime);

    // 等しい値は同じハッシュになる(Equals が見るメンバーの部分集合から作る)。
    public override int GetHashCode() =>
        HashCode.Combine(
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Snapshot),
            TopLine,
            TopSegment,
            ScrollX,
            CurrentLineLogical,
            Selection
        );
}
