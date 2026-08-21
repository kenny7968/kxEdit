// ImeController.cs
// Phase 3 (Task 3a) で EditorControl.Ime.cs / EditorControl.Paint.cs (DrawImeOverlay) から
// IME 状態機械 + Imm32 P/Invoke ラップ + overlay 描画を bit-perfect 移設した controller。
//
// 責務:
//   - _ime (ImeCompositionState) の所有 (state 単一箇所)
//   - WM_IME_STARTCOMPOSITION / WM_IME_COMPOSITION / WM_IME_ENDCOMPOSITION / WM_IME_SETCONTEXT の
//     ハンドリング (P/Invoke は IImeContext 経由 = pure テスト可能)
//   - CPS_CANCEL / CPS_COMPLETE 経路 (Cancel / Complete)
//   - overlay 描画 (Draw) + 候補窓通知 (NotifyCandidateWindow) + composition font 通知
//     (NotifyCompositionFont)
//
// 非責務 (host 側に残す):
//   - system caret 追跡 (SetCaretPos) = host.PositionCaret() 経由
//   - Invalidate = host.Invalidate() 経由
//   - buffer 差替 (AfterEdit / _buffer.Replace) = host.DeleteSelectionForImeStart() 経由
//
// テスト = FakeImeContext + FakeImeOverlayHost で pure に検証可能。実 IME/GDI 不要。
using System.Drawing;
using System.Windows.Forms;
using kxEdit.Core.Editing;
using kxEdit.Editor.Abstractions;

namespace kxEdit.Editor;

/// <summary>
/// Task 3a: IME 状態機械 + Imm32 ラップ + overlay 描画。旧 EditorControl.Ime.cs のロジックを
/// メソッド単位で bit-perfect 移設。副作用 (Invalidate / PositionCaret / AfterEdit) は
/// <see cref="IImeOverlayHost"/> 経由で host に委譲する。
/// </summary>
internal sealed class ImeController
{
    // === 状態 (このクラスが単一所有) ===
    private ImeCompositionState _ime = ImeCompositionState.Empty;

    // === 依存 (readonly = 差替不可) ===
    private readonly Func<IImeContext> _contextFactory;
    private readonly CaretController _caret;
    private readonly IImeOverlayHost _host;
    private readonly Action<string> _insertConfirmedText;

    /// <summary>未確定期間中か (=<c>_ime.IsActive</c>)。</summary>
    public bool IsActive => _ime.IsActive;

    /// <summary>state 読取専用アクセサ (host の PositionCaret / __Test* から _ime.Start/CursorPos/Text 参照用)。</summary>
    public ImeCompositionState State => _ime;

    public ImeController(
        Func<IImeContext> contextFactory,
        CaretController caret,
        IImeOverlayHost host,
        Action<string> insertConfirmedText
    )
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _caret = caret ?? throw new ArgumentNullException(nameof(caret));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _insertConfirmedText =
            insertConfirmedText ?? throw new ArgumentNullException(nameof(insertConfirmedText));
    }

    /// <summary>
    /// WM_IME_STARTCOMPOSITION 相当。旧 <c>OnImeStartComposition</c> bit-perfect 移設。
    /// 選択があれば削除して 1 Splice=1 Undo → <c>_ime.Start</c> をキャレットで初期化 →
    /// system caret 追従 (Task 11 レビュー I-1) → overlay 再描画。
    /// </summary>
    public void OnStartComposition()
    {
        if (!_host.CanImeCompose)
            return;
        _host.DeleteSelectionForImeStart();
        _ime = ImeCompositionState.Empty with { Start = _caret.Caret };
        _host.PositionCaret();
        _host.Invalidate();
    }

    /// <summary>
    /// WM_IME_COMPOSITION 相当。旧 <c>OnImeComposition</c> bit-perfect 移設。GCS_COMPSTR と
    /// GCS_RESULTSTR を独立に評価する (COMPSTR → RESULTSTR の順)。<see cref="IImeContext"/> の
    /// null 返却 (himc == 0) は空文字列 / 空配列で吸収し既存挙動と等価。
    /// </summary>
    public void OnComposition(long gcsFlags)
    {
        if (!_host.CanImeCompose)
            return;
        using var ctx = _contextFactory();
        // IMP-1 fixup: 元 EditorControl.Ime.cs:122 の `if (hIMC == IntPtr.Zero) return;` と bit-perfect。
        // himc==0 で ApplyComposition("", ...) / ApplyResult("") まで流すと _ime state が Empty に
        // 上書きされる (元は保持) + PositionCaret/Invalidate 副作用が漏れる=IME transient 失敗時に
        // 未確定 overlay が消えて非 IME キャレットへジャンプする退行を招く (rare edge case)。
        if (!ctx.IsAvailable)
            return;
        if ((gcsFlags & NativeMethods.GCS_COMPSTR) != 0)
        {
            string compStr = ctx.GetCompositionString(NativeMethods.GCS_COMPSTR) ?? "";
            byte[] attrs =
                (gcsFlags & NativeMethods.GCS_COMPATTR) != 0
                    ? ImeCompositionState.ParseAttrs(
                        ctx.GetCompositionBytes(NativeMethods.GCS_COMPATTR) ?? []
                    )
                    : [];
            int[] clauses =
                (gcsFlags & NativeMethods.GCS_COMPCLAUSE) != 0
                    ? ImeCompositionState.ParseClauses(
                        ctx.GetCompositionBytes(NativeMethods.GCS_COMPCLAUSE) ?? []
                    )
                    : [];
            int cursor =
                (gcsFlags & NativeMethods.GCS_CURSORPOS) != 0
                    ? ImeCompositionState.SnapCursorPos(
                        compStr,
                        ctx.GetCompositionInt(NativeMethods.GCS_CURSORPOS)
                    )
                    : 0;
            ApplyComposition(compStr, cursor, attrs, clauses);
        }
        if ((gcsFlags & NativeMethods.GCS_RESULTSTR) != 0)
        {
            string resultStr = ctx.GetCompositionString(NativeMethods.GCS_RESULTSTR) ?? "";
            ApplyResult(resultStr);
        }
    }

    /// <summary>
    /// WM_IME_ENDCOMPOSITION 相当。旧 <c>OnImeEndComposition</c> bit-perfect 移設。
    /// 冗長 no-op (ApplyResult ですでに Empty) 経路でも害はない=このメッセージは終端イベントで
    /// 取消経路からも来るため無条件クリアが正しい。
    /// </summary>
    public void OnEndComposition()
    {
        _ime = ImeCompositionState.Empty;
        _host.Invalidate();
    }

    /// <summary>
    /// WM_IME_SETCONTEXT 相当。旧 <c>OnImeSetContext</c> の lParam マスク部分のみ移設。
    /// base.WndProc(ref m) 呼び出しは host (EditorControl) 側 (base 参照は EditorControl 内でしか取れない)。
    /// </summary>
    /// <remarks>
    /// ISC_SHOWUICOMPOSITIONWINDOW = 0x80000000 は符号ビット。long に上げてマスクし nint に戻す
    /// (旧実装と同じ)。
    /// </remarks>
    public static void MaskSetContextLParam(ref Message m)
    {
        long lp = m.LParam.ToInt64();
        lp &= ~(long)NativeMethods.ISC_SHOWUICOMPOSITIONWINDOW;
        m.LParam = new nint(lp);
    }

    /// <summary>
    /// 未確定期間中に強制取消 (CPS_CANCEL)。旧 <c>CancelCompositionAndDefault</c> bit-perfect 移設。
    /// <see cref="IsActive"/>=false なら no-op (既存 IsComposing 早期 return と等価)。
    /// context 側で himc == 0 なら ImmNotifyIME を skip する (=旧 hIMC != 0 分岐と等価)。
    /// </summary>
    public void Cancel()
    {
        if (!IsActive)
            return;
        using (var ctx = _contextFactory())
        {
            ctx.CancelComposition();
        }
        _ime = ImeCompositionState.Empty;
        _host.Invalidate();
    }

    /// <summary>
    /// OnLostFocus 経路の確定試行 (CPS_COMPLETE)。旧 <c>OnLostFocus</c> の IsComposing 分岐 bit-perfect 移設。
    /// <see cref="IsActive"/>=false なら no-op。ImmNotifyIME が届かない環境でも <c>_ime</c> は必ずクリア。
    /// </summary>
    public void Complete()
    {
        if (!IsActive)
            return;
        using (var ctx = _contextFactory())
        {
            ctx.CompleteComposition();
        }
        _ime = ImeCompositionState.Empty;
        _host.Invalidate();
    }

    /// <summary>
    /// 未確定文字列 overlay 描画。旧 <c>EditorControl.DrawImeOverlay</c> bit-perfect 移設。
    /// 節 (<c>_ime.Clauses[i]..[i+1]</c>) ごとに Attrs を見て target 節 (TargetConverted) を
    /// SelectionBack + Underline|Bold で強調、それ以外は Underline のみで通常前景色。
    /// Clauses が空 or 節境界が 2 未満なら全体を通常下線 1 度描画。
    /// </summary>
    /// <remarks>
    /// <see cref="TextRenderer"/> を使う理由と Attrs 長不整合防御は旧 DrawImeOverlay と同じ (§3-3 / Task 2 M-5)。
    /// </remarks>
    public void Draw(Graphics g)
    {
        if (!_host.HasBuffer || _ime.Text.Length == 0)
            return;
        var (x, y, visible) = _host.ComputeCaretPoint(_ime.Start);
        if (!visible)
            return;

        int curX = x - _host.ScrollX;

        // Clauses が空 or 節境界が 2 未満なら 1 節扱い (Task 9 と同挙動)
        if (_ime.Clauses.Length < 2)
        {
            TextRenderer.DrawText(
                g,
                _ime.Text,
                _host.UnderlineFont,
                new Point(curX, y),
                _host.ForeColor,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
            );
            return;
        }

        for (int i = 0; i < _ime.Clauses.Length - 1; i++)
        {
            int s = _ime.Clauses[i],
                e = _ime.Clauses[i + 1];
            if (s < 0)
                continue; // 悪意/誤動作 IME の負値防御 (Task 10 M-2)
            if (e > _ime.Text.Length)
                e = _ime.Text.Length;
            if (s >= e)
                continue;
            string clause = _ime.Text[s..e];

            // 節先頭の Attr を代表値として採用 (Attrs 長不整合防御 = Task 2 M-5)
            byte attr = s < _ime.Attrs.Length ? _ime.Attrs[s] : ImeAttribute.Input;
            bool isTarget = attr == ImeAttribute.TargetConverted;

            // 描画フォントで測って背景 rect 幅と curX 進み幅を一致させる (Task 10 I-1)。
            Font drawFont = isTarget ? _host.TargetFont : _host.UnderlineFont;
            Size sz = TextRenderer.MeasureText(
                g,
                clause,
                drawFont,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
            );

            if (isTarget)
            {
                using var brush = new SolidBrush(_host.SelectionBackColor);
                g.FillRectangle(brush, curX, y, sz.Width, _host.LineHeightPx);
                TextRenderer.DrawText(
                    g,
                    clause,
                    drawFont,
                    new Point(curX, y),
                    _host.ForeColor,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
                );
            }
            else
            {
                TextRenderer.DrawText(
                    g,
                    clause,
                    drawFont,
                    new Point(curX, y),
                    _host.ForeColor,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
                );
            }
            curX += sz.Width;
        }
    }

    /// <summary>
    /// 候補窓位置を IME に通知。旧 <c>EditorControl.NotifyCandidateWindow</c> bit-perfect 移設。
    /// 座標は「未確定 Start の client 座標 - _scrollX / y + LineHeightPx」(キャレット行の直下)。
    /// 可視外 / not composing / no buffer / no focus / himc==0 は no-op。
    /// </summary>
    /// <remarks>
    /// IMP-1 fixup で <see cref="IImeContext.IsAvailable"/> 判定を追加=旧実装の
    /// <c>if (hIMC == IntPtr.Zero) return;</c> と bit-perfect に揃えた。
    /// </remarks>
    public void NotifyCandidateWindow()
    {
        if (!IsActive || !_host.HasBuffer || !_host.HasFocus)
            return;
        using var ctx = _contextFactory();
        // IMP-1 fixup (対称): himc==0 で ComputeCaretPoint を呼ばずに早期 return。元
        // EditorControl.NotifyCandidateWindow の `if (hIMC == IntPtr.Zero) return;` と bit-perfect。
        // OnComposition と同じガードを揃えることで意図が明確 (SetCandidateWindow 側の no-op 吸収に頼らない)。
        if (!ctx.IsAvailable)
            return;
        var (x, y, visible) = _host.ComputeCaretPoint(_ime.Start);
        if (!visible)
            return;
        ctx.SetCandidateWindow(x - _host.ScrollX, y + _host.LineHeightPx);
    }

    /// <summary>
    /// composition font を IME に通知。旧 <c>EditorControl.NotifyCompositionFont</c> bit-perfect 移設。
    /// himc == 0 は WinImeContext.SetCompositionFont 側で no-op 吸収。
    /// </summary>
    public void NotifyCompositionFont()
    {
        using var ctx = _contextFactory();
        ctx.SetCompositionFont(_host.Font);
    }

    // === Test hooks (Ime.cs の __TestApply* のバッキング。実 IME を介さない状態遷移テスト用) ===

    internal void __TestApplyComposition(string text, int cursorPos, byte[] attrs, int[] clauses) =>
        ApplyComposition(text, cursorPos, attrs, clauses);

    internal void __TestApplyResult(string text) => ApplyResult(text);

    // === private helpers (旧 EditorControl.Ime.cs から bit-perfect 移設) ===

    /// <summary>
    /// 確定文字列の適用本体。旧 <c>ApplyResult</c> bit-perfect 移設。
    /// 先に _ime を Empty へ落として InsertConfirmedText の Invalidate/OnPaint 経路で
    /// 偽の未確定描画が重ならないようにする (§0-2 の 1 Splice=1 Undo 契約)。
    /// </summary>
    private void ApplyResult(string text)
    {
        _ime = ImeCompositionState.Empty; // overlay を先に外して Insert 経路と競合させない
        if (text.Length > 0)
            _insertConfirmedText(text);
        _host.Invalidate();
    }

    /// <summary>
    /// 未確定文字列の適用本体。旧 <c>ApplyComposition</c> bit-perfect 移設。
    /// 多重メッセージ自己防衛: IsActive=true なら既存 Start 維持、そうでなければ現キャレットから取り直す。
    /// </summary>
    private void ApplyComposition(string text, int cursorPos, byte[] attrs, int[] clauses)
    {
        _ime = new ImeCompositionState(
            Start: _ime.IsActive ? _ime.Start : _caret.Caret,
            Text: text,
            CursorPos: cursorPos,
            Attrs: attrs,
            Clauses: clauses
        );
        _host.PositionCaret();
        _host.Invalidate();
    }
}
