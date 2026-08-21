// EditorControl.Ime.cs
// Phase 3 (Task 3a) で IME 状態機械 / Imm32 P/Invoke ラップ / overlay 描画を ImeController /
// WinImeContext / DrawImeOverlay→ImeController.Draw に移譲した後の残置ファイル。
// 本ファイルは partial EditorControl の以下 3 種類を提供する:
//   (1) IImeOverlayHost の explicit interface 実装 (ImeController → host 副作用の seam)
//   (2) CancelCompositionAndDefault の 1 行 wrapper (旧 API 保持=既存 IsComposing 呼び出し不変)
//   (3) __Test* / __Smoke* 診断アクセサ (EditorControlImeTests / kxEdit.Editor.Smoke から呼ぶ)
// ロジック本体は ImeController.cs / WinImeContext.cs 側にある。
using System.Drawing;

namespace kxEdit.Editor;

public sealed partial class EditorControl : IImeOverlayHost
{
    /// <summary>
    /// 未確定期間の強制取消 (CPS_CANCEL) の旧 API。呼び出し元 (ReadOnly setter・ReplaceSource・
    /// Undo/Redo/Cut/Paste・ReplaceCharRange・MouseDown・Caret 系) は unchanged で残置。
    /// 内部は <see cref="ImeController.Cancel"/> に委譲 (IsActive ガード + Ctx.Cancel + _ime クリア +
    /// host.Invalidate まで一括担う)。
    /// </summary>
    private void CancelCompositionAndDefault() => _imeCtrl.Cancel();

    // === IImeOverlayHost 実装 (explicit) ================================================
    // explicit interface implementation を使うことで public API に IME 内部露出を漏らさない。
    // 各メンバは既存 EditorControl フィールドを bit-perfect に反映する薄いブリッジ。

    /// <summary><c>_buffer is not null &amp;&amp; !ReadOnly</c> (旧 OnImeStart/OnImeComposition の早期 return と等価)。</summary>
    bool IImeOverlayHost.CanImeCompose => _buffer is not null && !ReadOnly;

    /// <summary>
    /// 旧 OnImeStartComposition の選択削除分岐 bit-perfect: 選択があれば 1 Splice 削除 →
    /// SetTo(s) → DesiredXpx=-1 → AfterEdit。無選択なら no-op。
    /// </summary>
    void IImeOverlayHost.DeleteSelectionForImeStart()
    {
        var (s, en) = GetSelectionCharRange();
        if (s != en)
        {
            // CanImeCompose が事前ガード=_buffer は非 null 保証。
            _buffer!.Replace(s, en - s, "");
            _caretCtrl.SetTo(s, _buffer.Current);
            _caretCtrl.DesiredXpx = -1;
            AfterEdit();
        }
    }

    void IImeOverlayHost.PositionCaret() => PositionCaret();

    void IImeOverlayHost.Invalidate() => Invalidate();

    bool IImeOverlayHost.HasBuffer => _buffer is not null;
    bool IImeOverlayHost.HasFocus => _hasFocus;
    int IImeOverlayHost.ScrollX => _scrollX;
    int IImeOverlayHost.LineHeightPx => _metrics.LineHeightPx;

    // IMP-2 fixup: Metrics プロパティは interface から削除 (使用 0 件)。

    (int X, int Y, bool Visible) IImeOverlayHost.ComputeCaretPoint(int offset) =>
        ComputeCaretPoint(offset);

    Font IImeOverlayHost.Font => _font;
    Font IImeOverlayHost.UnderlineFont => _underlineFontCache;
    Font IImeOverlayHost.TargetFont => _targetFontCache;
    Color IImeOverlayHost.ForeColor => ForeColor;

    // 旧 DrawImeOverlay で使う target 節背景色。EditorControl.Paint.cs の ToColor(_style.SelectionBack) と等価。
    Color IImeOverlayHost.SelectionBackColor => ToColor(_style.SelectionBack);

    // === __Test* / __Smoke* 診断アクセサ ==================================================
    // 旧 EditorControl.Ime.cs では _ime.Xxx を直接返していたが、Task 3a 以降は _imeCtrl.State
    // 経由で読む (State は internal readonly property=readonly record struct のコピー返却)。

    // P4 Task 5〜8: EditorControlImeTests から呼ぶ IME 未確定状態検査 (__Test* 規約)。
    internal int __TestImeStart() => _imeCtrl.State.Start;

    internal bool __TestIsComposing() => _imeCtrl.IsActive;

    internal string __TestImeText() => _imeCtrl.State.Text;

    // P4 Task 14: smoke タイトルバー表示用の状態ポーリング (__Smoke* 規約)。
    internal bool __SmokeIsComposing() => _imeCtrl.IsActive;

    internal string __SmokeImeText() => _imeCtrl.State.Text;

    // Task 6: 実 IME を介さずに ApplyComposition の状態遷移だけを検証するための受け口。
    // Task 3a で ImeController に移設した ApplyComposition/ApplyResult を internal __TestApply* 経由で叩く。
    internal void __TestApplyComposition(string text, int cursorPos, byte[] attrs, int[] clauses) =>
        _imeCtrl.__TestApplyComposition(text, cursorPos, attrs, clauses);

    // Task 7: 実 IME を介さずに ApplyResult (GCS_RESULTSTR 確定通知) の適用だけを検証する受け口。
    internal void __TestApplyResult(string text) => _imeCtrl.__TestApplyResult(text);
}
