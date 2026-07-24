// EditorControl.Caret.cs
// Phase 2 (Task 2b) で切り出したキャレット/選択分割。
// Phase 3 (Task 3b) で _caret/_anchor/_desiredXpx の所有権を CaretController へ移譲済み。
// 本ファイルの public API 群 (SetCaretCharOffset/SelectCharRange/GoToLine 等) は
// Controller 呼び出しの薄いラッパとして残存(外部契約=Editor.Tests + MainForm は不変)。
// 副作用 (PositionCaret/Invalidate/UIA イベント/UpdateUI) は本ファイル側に残置=
// Controller は state 操作(SnapAndClamp + 選択セマンティクス)のみ。
using System.ComponentModel;
using yEdit.Core.Buffers;

namespace yEdit.Editor;

public sealed partial class EditorControl
{
    // Caret 位置操作 + 選択範囲 API + view-into scroll

    /// <summary>P6 Task 2: 長さベースの選択設定エイリアス(App 層互換)。</summary>
    public void SelectCharRange(int start, int length) =>
        SetSelectionCharRange(start, start + Math.Max(0, length));

    /// <summary>
    /// P6 Task 2: <see cref="SetCaretCharOffset"/> のエイリアス(App 層互換)。
    /// <paramref name="offset"/> はキャレット位置の絶対値(現在位置からの差分ではない)。
    /// </summary>
    public void MoveCaretCharOffset(int offset) => SetCaretCharOffset(offset);

    /// <summary>P6 Task 3: 指定 0-based 行の先頭にキャレット移動(App 層互換=`Lines[i].Goto()` 相当)。</summary>
    public void GoToLine(int line)
    {
        if (_buffer is null)
            return;
        var snap = _buffer.Current;
        int clamped = Math.Clamp(line, 0, snap.LineCount - 1);
        SetCaretCharOffset(snap.GetLineStart(clamped));
    }

    /// <summary>
    /// 0-based の行/桁を指定してキャレットを移動する。復元経路(FileController.RestoreSession)で
    /// 前回終了時のカーソル位置を再現するために追加。line/col とも実バッファ範囲へクランプ、
    /// col は行末(改行手前)を上限とする=次行に食み出さない。設計書 2026-07-23 §3.4。
    /// </summary>
    public void SetCaretByLineColumn(int line, int col)
    {
        if (_buffer is null)
            return;
        var snap = _buffer.Current;
        int lc = snap.LineCount;
        if (lc <= 0)
            return;
        int clampedLine = Math.Clamp(line, 0, lc - 1);
        int lineStart = snap.GetLineStart(clampedLine);
        int lineEnd = snap.GetLineEnd(clampedLine, includeBreak: false);
        int maxCol = Math.Max(0, lineEnd - lineStart);
        int clampedCol = Math.Clamp(col, 0, maxCol);
        SetCaretCharOffset(lineStart + clampedCol);
    }

    /// <summary>P6 Task 3: `CaretCharOffset` の別名(App 層互換=Scintilla の `CurrentPosition`)。</summary>
    public int CurrentPosition => _caretCtrl.Caret;

    /// <summary>キャレット位置(UTF-16 文字オフセット)。書き込みは <see cref="SetCaretCharOffset"/>。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CaretCharOffset => _caretCtrl.Caret;

    /// <summary>
    /// 現在キャレットの論理行番号(0 始まり)。SetSource 前は 0(P6 の
    /// <c>ScintillaHost.CurrentLine</c> と同名=App 層互換 API・設計書 §2-8)。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CurrentLine =>
        _buffer is null ? 0 : _buffer.Current.GetLineIndexOfChar(_caretCtrl.Caret);

    /// <summary>
    /// 指定オフセットの論理行内オフセット(0 始まり)。SetSource 前は 0・範囲外は
    /// [0, CharLength] にクランプ・サロゲート中間位置は前方(high)へスナップ。
    /// P6 の <c>ScintillaHost.GetColumn</c> と同名(App 層互換 API・設計書 §2-8)。
    /// </summary>
    /// <remarks>
    /// 「行内オフセット」= オフセット - その行の GetLineStart。UTF-16 コード単位で数えるため、
    /// タブ幅展開・全角=2 換算などは行わない(Scintilla の SCI_GETCOLUMN は幅展開するが、
    /// 本 API は簡潔さを優先=P6 移植で問題になれば拡張する)。
    /// </remarks>
    public int GetColumn(int offset)
    {
        if (_buffer is null)
            return 0;
        int snapped = SnapAndClamp(offset);
        var snap = _buffer.Current;
        int line = snap.GetLineIndexOfChar(snapped);
        return snapped - snap.GetLineStart(line);
    }

    /// <summary>
    /// 選択アンカー(UTF-16 文字オフセット)。<c>Anchor == Caret</c> のときは選択なし。
    /// 書き込みは <see cref="SetSelectionAnchored(int, int)"/> または <see cref="SetSelectionCharRange(int, int)"/>。
    /// P3 Task 2 で導入(shift+左方向の選択保持=キャレット &lt; アンカーのケース)。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectionAnchor => _caretCtrl.Anchor;

    /// <summary>
    /// キャレット位置を UTF-16 文字オフセットで設定する(選択はクリアされる=Anchor=Caret=snapped)。
    /// サロゲートペア中間位置(low)は前方(high)にスナップ。範囲外は [0, CharLength] にクランプ。
    /// SetSource 前の呼び出しは no-op(_buffer が null のため)。
    /// </summary>
    public void SetCaretCharOffset(int offset)
    {
        if (IsComposing)
            CancelCompositionAndDefault();
        if (_buffer is null)
            return;
        int snapped = SnapAndClamp(offset);
        if (_caretCtrl.Caret == snapped && _caretCtrl.Anchor == snapped)
            return;
        _caretCtrl.SetTo(snapped, _buffer.Current); // 単純キャレット移動は選択解除
        PositionCaret();
        Invalidate();
        // P5 Task 8: 純粋な選択/キャレット移動での UIA イベント発火
        if (RaiseUiaSelectionEvents)
            _uia.RaiseSelectionChanged();
        // P6 Task 4: キャレット位置変化は App 層のステータスバー更新契機
        UpdateUI?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>現在の選択範囲(UTF-16 文字オフセット・Start &lt;= End で返す)。</summary>
    /// <remarks>
    /// 内部状態(<c>_caretCtrl.Anchor</c>/<c>_caretCtrl.Caret</c>)は非対称=どちらが Min/Max かは
    /// 選択方向で変わる。呼び出し側が「アンカーはどこか」を知りたい場合は
    /// <see cref="SelectionAnchor"/> を使う。
    /// </remarks>
    public (int Start, int End) GetSelectionCharRange() => _caretCtrl.Selection;

    /// <summary>
    /// 選択範囲を設定する(対称版=方向を持たない)。<paramref name="start"/> &gt; <paramref name="end"/>
    /// の場合は内部で正規化。両端はサロゲートペア中間位置なら前方スナップ・範囲外はクランプ。
    /// 内部では <c>Anchor = Min(start, end)</c>・<c>Caret = Max(start, end)</c> にマップする
    /// (=キャレットは選択末尾=右方向の選択)。SetSource 前の呼び出しは no-op。
    /// </summary>
    /// <remarks>
    /// 非対称版(キャレット位置を明示指定=shift+左方向の選択)は <see cref="SetSelectionAnchored(int, int)"/>
    /// を使う。既存呼び出し側の挙動を変えないためこの API はキャレット末尾固定のまま維持する。
    /// </remarks>
    public void SetSelectionCharRange(int start, int end)
    {
        if (IsComposing)
            CancelCompositionAndDefault();
        if (_buffer is null)
            return;
        int s = SnapAndClamp(Math.Min(start, end));
        int e = SnapAndClamp(Math.Max(start, end));
        if (_caretCtrl.Anchor == s && _caretCtrl.Caret == e)
            return;
        _caretCtrl.SetSelection(s, e, _buffer.Current);
        PositionCaret();
        Invalidate();
        // P5 Task 8: 純粋な選択/キャレット移動での UIA イベント発火
        if (RaiseUiaSelectionEvents)
            _uia.RaiseSelectionChanged();
        // P6 Task 4: 選択範囲変化は App 層のステータスバー更新契機
        UpdateUI?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// アンカー保持でキャレットのみを <paramref name="newCaret"/> に移動する(shift+移動系の共通経路)。
    /// サロゲートペア中間位置は前方スナップ・範囲外はクランプ。
    /// <c>newCaret == Anchor</c> のとき選択が消える(=アンカーと同位置)。
    /// SetSource 前の呼び出しは no-op。
    /// </summary>
    public void MoveCaretWithSelection(int newCaret)
    {
        if (IsComposing)
            CancelCompositionAndDefault();
        if (_buffer is null)
            return;
        int snapped = SnapAndClamp(newCaret);
        if (_caretCtrl.Caret == snapped)
            return;
        _caretCtrl.MoveTo(snapped, extend: true, _buffer.Current);
        PositionCaret();
        Invalidate();
        // P5 Task 8: 純粋な選択/キャレット移動での UIA イベント発火
        if (RaiseUiaSelectionEvents)
            _uia.RaiseSelectionChanged();
        // P6 Task 4: shift+移動系の共通経路。App 層のステータスバー更新契機として UpdateUI 発火
        UpdateUI?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// アンカーとキャレットを個別指定して選択範囲を設定する(非対称版)。
    /// <paramref name="anchor"/> &gt; <paramref name="caret"/> のときはキャレットが Min=選択先頭
    /// (=shift+左方向の選択)。両端はサロゲートペア中間位置なら前方スナップ・範囲外はクランプ。
    /// SetSource 前の呼び出しは no-op。
    /// </summary>
    public void SetSelectionAnchored(int anchor, int caret)
    {
        if (IsComposing)
            CancelCompositionAndDefault();
        if (_buffer is null)
            return;
        int a = SnapAndClamp(anchor);
        int c = SnapAndClamp(caret);
        if (_caretCtrl.Anchor == a && _caretCtrl.Caret == c)
            return;
        _caretCtrl.SetSelection(a, c, _buffer.Current);
        PositionCaret();
        Invalidate();
        // P5 Task 8: 純粋な選択/キャレット移動での UIA イベント発火
        if (RaiseUiaSelectionEvents)
            _uia.RaiseSelectionChanged();
        // P6 Task 4: 非対称版の選択範囲変化も App 層のステータスバー更新契機
        UpdateUI?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// [0, CharLength] にクランプし、UTF-16 low サロゲート位置なら 1 前方(high 側)へスナップ。
    /// CharLength 位置(=EOF)はキャレットが立てる境界なのでクランプ後もそのまま許可。
    /// Task 3b で純ロジックを <see cref="CaretController.SnapAndClamp"/> へ移設。本メソッドは
    /// _buffer null ガードを付けた薄いラッパ(内部呼び出しは snap 引数の受け渡しを省略できる)。
    /// </summary>
    private int SnapAndClamp(int offset)
    {
        if (_buffer is null)
            return 0;
        return CaretController.SnapAndClamp(offset, _buffer.Current);
    }

    /// <summary>
    /// UTF-16 文字オフセットのクライアント座標(px)を返す(P2 計画 §1 の公開 API)。
    /// SetSource 前 / 可視外(TopLine 未到達 / 下端超過)は <see cref="Point.Empty"/> を返す。
    /// 返す座標は <see cref="ScrollX"/> 反映後の実描画位置(折り返し OFF 時は -_scrollX された値)。
    /// サロゲート中間位置・範囲外は内部で <see cref="SnapAndClamp"/> により正規化する。
    /// </summary>
    public Point PointFromCharOffset(int offset)
    {
        if (_buffer is null)
            return Point.Empty;
        int snapped = SnapAndClamp(offset);
        var (x, y, visible) = ComputeCaretPoint(snapped);
        if (!visible)
            return Point.Empty;
        return new Point(x - _scrollX, y);
    }

    /// <summary>
    /// 現在のキャレット位置(<c>_caretCtrl.Caret</c>)を可視領域内に収める(P3 Task 7)。
    /// - 垂直: キャレットの論理行が [TopLine, TopLine + visibleRows) の外なら <see cref="TopLine"/> を調整。
    /// - 水平: 折り返し OFF かつ <see cref="_hscroll"/> 表示中で、キャレット X が
    ///   [ScrollX, ScrollX + paintWidth) の外なら <see cref="ScrollX"/> を調整。
    /// - SetSource 前 / 折り返し ON では水平調整は no-op。
    /// - Task 6 で仕込んだ OnKeyDown 経路の呼び出しはそのまま本実装へ流れる(Ctrl+A は
    ///   慣例に合わせて意図的に呼び出さない=Task 6 レビュー I-1 の判断)。
    /// - App 層 Task 7 送り(検索ジャンプ・GoTo)から <see cref="EnsureVisibleCharRange"/>
    ///   経由で呼ばれることを想定して public 昇格。
    /// </summary>
    /// <remarks>
    /// <see cref="ComputeCaretPoint"/> は <c>_scrollX</c> 適用前の X を返す(=行番号マージン含む
    /// 描画原点座標)。現在の可視ウィンドウは [<c>_scrollX</c>, <c>_scrollX + paintWidth</c>)。
    /// 右端に張り付かせるときは caret を可視領域末尾から半角 1 文字幅分内側に置く
    /// (キャレットが右端ぎりぎりで隠れるのを防ぐ)。paintWidth が半角 1 文字幅より狭い極小
    /// ウィンドウでは、この 1 サイクル余分にスクロールしても届かず ScrollX がクランプで
    /// 頭打ちになるが、そのケースは表示自体が破綻しているので実害なし(S-3 レビュー)。
    /// 垂直方向の可視行数は <c>paintHeight = ClientSize.Height - hscroll.Height</c> ベースで
    /// 計算する(<see cref="ComputeCaretPoint"/> の可視性判定と一致させる=Task 7 レビュー I-1)。
    /// 一致していないと、最下論理行にキャレットが来たとき「垂直はまだ可視」と誤判断され
    /// hscroll の下に張り付いたまま TopLine が動かないケースが発生する。
    /// </remarks>
    public void BringCaretIntoView()
    {
        if (_buffer is null)
            return;
        var snap = _buffer.Current;

        int logicalLine = snap.GetLineIndexOfChar(_caretCtrl.Caret);
        // I-1 対応: paintHeight ベースで可視行数を算出(ComputeCaretPoint の可視性判定と一致)。
        int paintHeight = Math.Max(0, ClientSize.Height - (_hscroll.Visible ? _hscroll.Height : 0));
        int visibleRows = Math.Max(1, paintHeight / Math.Max(1, _metrics.LineHeightPx));

        // 垂直: caret 論理行が [TopLine, TopLine+visibleRows) に入るように TopLine 調整
        if (logicalLine < _topLine)
        {
            TopLine = logicalLine;
        }
        else if (logicalLine >= _topLine + visibleRows)
        {
            TopLine = logicalLine - visibleRows + 1;
        }

        // 水平: 折り返し OFF かつ HScroll 表示中のみ有効
        // (ScrollX setter 自体も _wrapColumns>0 / !_hscroll.Visible で no-op だが、
        //  ここで先に判定することで ComputeCaretPoint の無駄呼びを回避する)
        if (_wrapColumns == 0 && _hscroll.Visible)
        {
            var (x, _, visible) = ComputeCaretPoint(_caretCtrl.Caret);
            if (visible)
            {
                int paintWidth = Math.Max(0, ClientSize.Width - _vscroll.Width);
                if (x < _scrollX)
                {
                    ScrollX = x;
                }
                else if (x >= _scrollX + paintWidth)
                {
                    // 右端に張り付かせる: caret を可視領域末尾から 1 半角幅分内側に置く
                    ScrollX = x - paintWidth + _metrics.MeasureRun("0");
                }
            }
        }
    }

    /// <summary>
    /// 指定範囲 <c>[start, start+length)</c> の末尾を可視化する(検索ジャンプ・GoTo・
    /// EnsureVisible 相当)。内部でキャレットを一時的に end に置いて
    /// <see cref="BringCaretIntoView"/> を呼ぶが、呼び出し前のキャレット位置と
    /// アンカーは try/finally で必ず復元する(=装飾スクロールなので選択・
    /// キャレット状態は変えない)。SetSource 前は no-op。
    /// </summary>
    /// <param name="start">開始 UTF-16 文字オフセット。範囲末尾 (<c>start+length</c>) の
    /// 計算にのみ使い、start 単独位置の可視化は行わない(=start は <see cref="SnapAndClamp"/>
    /// を通さない)。</param>
    /// <param name="length">長さ(UTF-16 コード単位)。負値は 0 として扱う。</param>
    /// <remarks>
    /// C-1 対応(Task 7 レビュー): finally で <see cref="PositionCaret"/> を呼び、
    /// OS 側システムキャレット位置を savedCaret に戻す。これをやらないと
    /// <see cref="TopLine"/>/<see cref="ScrollX"/> setter が内部で呼ぶ
    /// <see cref="PositionCaret"/> が <c>Caret = end</c> のまま SetCaretPos を発火し、
    /// field 復元後も blinking caret / IME 変換開始位置 / UIA フォーカスキャレット位置が
    /// end に取り残される(P5 の UIA 接続で顕在化する)。
    /// try/finally 化は I-2(将来 <see cref="BringCaretIntoView"/> に throw を混ぜ込んだ
    /// ときの state 残留防止)も兼ねる。
    /// </remarks>
    public void EnsureVisibleCharRange(int start, int length)
    {
        if (_buffer is null)
            return;
        // 範囲末尾のみ可視化する仕様(start 側は SnapAndClamp 不要=BringCaretIntoView は
        // end のみに反応する)。start + length は int 加算オーバーフロー対策で long 経由。
        long endLong = (long)start + Math.Max(0, length);
        int endInt = endLong > int.MaxValue ? int.MaxValue : (int)endLong;
        int end = SnapAndClamp(endInt);

        int savedCaret = _caretCtrl.Caret;
        int savedAnchor = _caretCtrl.Anchor;
        try
        {
            _caretCtrl.SetTo(end, _buffer.Current);
            BringCaretIntoView();
        }
        finally
        {
            _caretCtrl.SetSelection(savedAnchor, savedCaret, _buffer.Current);
            // OS 側キャレットを savedCaret の座標に戻す(TopLine/ScrollX setter が
            // 内部で Caret=end のまま SetCaretPos を発火した副作用を上書きする)。
            // 末尾 Invalidate は削除: setter が Invalidate 発行済み、no-op ケースなら不要。
            PositionCaret();
        }
    }
}
