using kxEdit.Core.Csv;
using kxEdit.Editor;

namespace kxEdit.App;

/// <summary>
/// F2 セル編集のオーバーレイ TextBox。EditorControl 本文は読取専用のまま、セル値だけを
/// 通常の EDIT コントロールで編集する（カーソルはセル内のみ＝この TextBox 内のみ）。
/// 確定文字列の本文反映は呼び出し元（CsvController）が CSV 直列化して行う。本クラスは
/// TextBox の生成・配置・キー処理（Enter=確定 / Alt+Enter=改行 / Esc=取消）・フォーカス復帰のみ担う。
/// フォーカスの復帰先は呼び出し元が指定する(P6: Document.FocusTarget=Editor 固定)。
/// アクセシビリティ: 唯一の呼び出し元は同一アセンブリ内の <see cref="CsvController"/> のみで
/// アセンブリ外部への表面ではないため <c>internal</c>。テストは <c>InternalsVisibleTo</c> 経由で
/// F2 経路(<see cref="Commit"/>/<see cref="CancelEdit"/>)を直接ドライブする(Task 7)。
/// </summary>
internal sealed class CsvCellEditor : IDisposable
{
    private TextBox? _box;
    private bool _closing;
    private Control? _refocus;
    private Action<string>? _onCommit;
    private Action? _onCancel;

    internal bool IsEditing => _box is not null;

    /// <summary>セル編集を開始する。onCommit は確定値（改行は \n 正規化済み）、onCancel は取消で呼ぶ。</summary>
    internal void Begin(
        EditorControl ed,
        CsvField field,
        Control refocusTarget,
        Action<string> onCommit,
        Action onCancel
    )
    {
        if (IsEditing)
            return;
        _refocus = refocusTarget;
        _onCommit = onCommit;
        _onCancel = onCancel;
        _closing = false;

        var host = ed.Parent ?? ed; // 親(TabPage 等)に重ねる
        var clientPt = ed.PointFromCharOffset(field.Start); // EditorControl クライアント座標
        var local = host.PointToClient(ed.PointToScreen(clientPt));

        _box = new TextBox
        {
            Multiline = true,
            AcceptsReturn = true, // Enter は KeyDown で自前処理
            AcceptsTab = false,
            WordWrap = false,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            Text = field.Value,
            Location = local,
            Width = Math.Max(140, ed.ClientSize.Width / 4),
            Height = Math.Max(ed.LineHeightPx + 6, 24),
            AccessibleName = "セル編集",
            ImeMode = ImeMode.NoControl,
        };
        _box.KeyDown += OnKeyDown;
        _box.LostFocus += OnLostFocus;

        host.Controls.Add(_box);
        _box.BringToFront();
        _box.Focus();
        _box.SelectAll(); // 全選択（即上書きしやすく）
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_box is null)
            return;
        if (e.KeyCode == Keys.ProcessKey)
            return; // IME 変換確定の Enter 等は無視（誤コミット防止）
        if (e.KeyCode == Keys.Return && e.Alt) // Alt+Enter → セル内改行
        {
            int at = _box.SelectionStart;
            _box.Text = _box.Text.Remove(at, _box.SelectionLength).Insert(at, "\r\n");
            _box.SelectionStart = at + 2;
            _box.SelectionLength = 0;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.Return) // Enter → 確定
        {
            e.SuppressKeyPress = true;
            Commit();
            return;
        }
        if (e.KeyCode == Keys.Escape) // Esc → 取消
        {
            e.SuppressKeyPress = true;
            CancelEdit();
        }
    }

    // フォーカス喪失は取消扱い（誤変更を避ける）。確定/取消処理中は無視。
    private void OnLostFocus(object? sender, EventArgs e)
    {
        if (!_closing)
            CancelEdit();
    }

    /// <summary>Enter 相当=編集内容を確定する。<see cref="_box"/> の現在テキストを
    /// (\r\n/\r を \n に正規化した上で) <c>onCommit</c> へ渡し、オーバーレイを閉じる。
    /// Task 7 でテストからも呼べるよう internal 化(F2 経路の観測ポイント)。</summary>
    internal void Commit()
    {
        if (_box is null || _closing)
            return;
        string text = _box.Text.Replace("\r\n", "\n").Replace("\r", "\n");
        var cb = _onCommit;
        Close();
        cb?.Invoke(text);
    }

    /// <summary>Esc 相当=編集を破棄する。<c>onCancel</c> を呼びオーバーレイを閉じる。
    /// Task 7 でテストからも呼べるよう internal 化(F2 経路の観測ポイント)。</summary>
    internal void CancelEdit()
    {
        if (_box is null || _closing)
            return;
        var cb = _onCancel;
        Close();
        cb?.Invoke();
    }

    private void Close() => Teardown(refocus: true);

    /// <summary>進行中の編集を強制破棄する（タブ閉じ/切替時）。コールバックは呼ばず、
    /// フォーカスも戻さない純粋な破棄。編集していなければ何もしない（冪等）。</summary>
    internal void Abort()
    {
        if (!IsEditing)
            return;
        Teardown(refocus: false);
    }

    /// <summary>オーバーレイの後始末（イベント解除・親から除去・破棄・参照解放）。
    /// refocus 時のみ指定された復帰先へフォーカスを戻す。Close=true / Abort=false。</summary>
    private void Teardown(bool refocus)
    {
        _closing = true;
        var box = _box;
        _box = null;
        if (box is not null)
        {
            box.KeyDown -= OnKeyDown;
            box.LostFocus -= OnLostFocus;
            box.Parent?.Controls.Remove(box);
            box.Dispose();
        }
        if (refocus)
            _refocus?.Focus();
        _onCommit = null;
        _onCancel = null;
        _refocus = null;
    }

    // CA1001 対応(Sub 3.4-B): 進行中の編集オーバーレイ(_box=TextBox)は Teardown で
    // 個別 Dispose されるが、CsvCellEditor インスタンス自身がドロップされる際に
    // Abort(冪等) を呼ぶことで、編集中(_box!=null)のまま破棄されるケースの
    // TextBox リークを防ぐ。コールバックは呼ばず、フォーカスも戻さない Abort 経路。
    public void Dispose() => Abort();
}
