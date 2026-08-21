// IImeOverlayHost.cs
// Phase 3 (Task 3a) で ImeController が host (EditorControl) から状態/描画メトリクス/座標を取得
// するための seam。実装は EditorControl (explicit interface implementation で外部に露出させない)。
// Task 3a 境界事例判断: Plan で明記された Metrics/Font に加え、DrawImeOverlay 移設に必要な色/
// スクロール/座標系情報を実測で追加している (詳細=Task 3a Report の「境界事例判断」)。
// IMP-2 fixup: Metrics プロパティは使用 0 件だったため削除 (LineHeightPx は個別露出済)。
using System.Drawing;

namespace kxEdit.Editor;

/// <summary>
/// Task 3a: ImeController が host に依頼する副作用 / 状態読取 / 描画リソース取得の seam。
/// テストでは FakeImeOverlayHost で pure に置き換えられる (Draw を除く state ロジックは Graphics 不要)。
/// </summary>
internal interface IImeOverlayHost
{
    // === state 系副作用 (OnStartComposition / OnComposition から呼ぶ) ===

    /// <summary><c>_buffer is not null &amp;&amp; !ReadOnly</c>。false なら IME 開始/更新は no-op (bit-perfect)。</summary>
    bool CanImeCompose { get; }

    /// <summary>OnStartComposition 前処理: 選択があれば削除 + キャレット寄せ + AfterEdit。無選択なら no-op。</summary>
    void DeleteSelectionForImeStart();

    /// <summary>system caret を再配置 (composition 更新後の追従用)。</summary>
    void PositionCaret();

    /// <summary>overlay 再描画。ImeController は副作用 (Invalidate) を持たず host に委譲する。</summary>
    void Invalidate();

    // === 状態 / メトリクス (Draw / NotifyCandidateWindow から参照) ===

    /// <summary><c>_buffer is not null</c>。</summary>
    bool HasBuffer { get; }

    /// <summary><c>_hasFocus</c>。</summary>
    bool HasFocus { get; }

    /// <summary>水平スクロール px (Draw / NotifyCandidateWindow の座標補正)。</summary>
    int ScrollX { get; }

    /// <summary><c>_metrics.LineHeightPx</c>。候補窓 y オフセット / target 節背景高さに使う。</summary>
    int LineHeightPx { get; }

    /// <summary>UTF-16 offset → client 座標 (px)。ComputeCaretPoint と同契約 (visible=false で不可視)。</summary>
    (int X, int Y, bool Visible) ComputeCaretPoint(int offset);

    // === 描画リソース (Draw / NotifyCompositionFont から参照) ===

    /// <summary>本文フォント (NotifyCompositionFont が IME に流す)。</summary>
    Font Font { get; }

    /// <summary>Underline のみの overlay フォント (通常節)。</summary>
    Font UnderlineFont { get; }

    /// <summary>Underline|Bold の overlay フォント (target 節)。</summary>
    Font TargetFont { get; }

    /// <summary>overlay 文字色 (通常は <c>Control.ForeColor</c>)。</summary>
    Color ForeColor { get; }

    /// <summary>target 節背景色 (通常は <c>_style.SelectionBack</c> を Color 化したもの)。</summary>
    Color SelectionBackColor { get; }
}
