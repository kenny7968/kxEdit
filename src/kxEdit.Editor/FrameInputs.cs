// FrameInputs.cs
// 2026-09-25 性能改善フェーズ 3(設計書 §8.1): 描画が読む状態の全部を 1 つの不変値に集めたもの。
// 製品コードでは EditorControl.CaptureFrameInputs() だけが作り、OnPaint はこの値だけからフレームを組み立てて描く。
// 描画に新しい入力を足すときは、必ずここにメンバーを足し、Equals と FrameInputsTests も直すこと
// (足さずに描画から生の状態を読むと、キャレット移動で再描画を省いたときに古い絵が画面に残る)。
// ここに入る状態を書き換える経路は必ず自分で Invalidate() か InvalidateChangedRows()(フェーズ 9: 変わった行だけ)を
// 呼ぶ規則は EditorControl._lastPaintedInputs のコメントを参照。
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
/// 未確定表示の原点は <see cref="ImeOrigin"/> で受け取る。<see cref="ImeController.Draw(Graphics, Point)"/> が
/// host から読むのはフォント・色・行高で、いずれもフォント 3 つ・<see cref="Style"/>・<see cref="Metrics"/> としてここにある。
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

    /// <summary>
    /// 未確定表示の原点(<see cref="ImeController.ComputeOrigin"/>)。表示しないときは null。
    /// フェーズ 9: 無効化する帯を原点から求める。
    /// </summary>
    public required Point? ImeOrigin { get; init; }

    /// <summary>
    /// 行の外に効く入力(画面全体の描き方)が等しいか。等しくなければ、全面を描き直す(フェーズ 9・計画 §0.2)。
    /// 行の中身(<see cref="Snapshot"/>・<see cref="CurrentLineLogical"/>・<see cref="Selection"/>・
    /// <see cref="CellHighlight"/>・<see cref="Ime"/>・<see cref="ImeOrigin"/>)とスクロール位置
    /// (<see cref="TopLine"/>・<see cref="TopSegment"/>・<see cref="ScrollX"/>)は比べない。
    /// <see cref="Snapshot"/> が変わっても、行番号幅と hscroll の表示は <see cref="LineNumberWidth"/>・
    /// <see cref="PaintHeight"/> としてここで比べる。
    /// </summary>
    public bool SameLayoutAs(FrameInputs other) =>
        WrapColumns == other.WrapColumns
        && ClientSize == other.ClientSize
        && PaintWidth == other.PaintWidth
        && PaintHeight == other.PaintHeight
        && ShowLineNumbers == other.ShowLineNumbers
        && LineNumberWidth == other.LineNumberWidth
        && ShowWhitespace == other.ShowWhitespace
        && Style.Equals(other.Style)
        && ReferenceEquals(Metrics, other.Metrics)
        && ReferenceEquals(Font, other.Font)
        && ReferenceEquals(UnderlineFont, other.UnderlineFont)
        && ReferenceEquals(TargetFont, other.TargetFont)
        && BackColor.ToArgb() == other.BackColor.ToArgb();

    public bool Equals(FrameInputs? other) =>
        other is not null
        && SameLayoutAs(other)
        && ReferenceEquals(Snapshot, other.Snapshot)
        && TopLine == other.TopLine
        && TopSegment == other.TopSegment
        && ScrollX == other.ScrollX
        && CurrentLineLogical == other.CurrentLineLogical
        && Selection == other.Selection
        && CellHighlight == other.CellHighlight
        && Ime.Equals(other.Ime)
        && ImeOrigin == other.ImeOrigin;

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
