using System.IO;
using kxEdit.App.Speech;

namespace kxEdit.App;

/// <summary>
/// grep の入力収集モードレスダイアログ。検索文字列・フォルダ・フィルタ・各オプションを集め、
/// 操作は生成時に受け取るコールバック(<see cref="GrepCallbacks"/>)経由。実行中は入力を無効化し
/// 中止のみ可能にする。
/// </summary>
public sealed class GrepDialog : Form, IGrepView
{
    private readonly GrepCallbacks _cb;

    private readonly TextBox _pattern = new() { Width = 320 };
    private readonly TextBox _folder = new() { Width = 320 };
    private readonly Button _browse = new() { Text = "参照(&B)...", AutoSize = true };
    private readonly TextBox _filter = new() { Width = 320, Text = "*.*" };
    private readonly CheckBox _recursive = new()
    {
        Text = "サブフォルダを含む(&S)",
        AutoSize = true,
        Checked = true,
    };
    private readonly CheckBox _matchCase = new()
    {
        Text = "大文字と小文字を区別(&C)",
        AutoSize = true,
    };
    private readonly CheckBox _wholeWord = new() { Text = "単語単位(&W)", AutoSize = true };
    private readonly CheckBox _useRegex = new() { Text = "正規表現(&E)", AutoSize = true };
    private readonly Button _run = new() { Text = "検索(&F)", AutoSize = true };
    private readonly Button _stop = new()
    {
        Text = "中止(&T)",
        AutoSize = true,
        Enabled = false,
    };
    private readonly Button _close = new() { Text = "閉じる(&X)", AutoSize = true };
    private readonly Label _status = new()
    {
        AutoSize = true,
        Text = "",
        AccessibleName = "状態",
    };
    private readonly IAnnouncer _announcer;

    public GrepDialog(GrepCallbacks callbacks, IAnnouncer announcer)
    {
        _cb = callbacks;
        _announcer = announcer;
        Text = "フォルダ検索 (grep)";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        BuildLayout();

        _browse.Click += (_, _) => BrowseFolder();
        _run.Click += async (_, _) => await _cb.RunAsync(); // fire-and-forget=UI 都合(戻り値は捨てる・例外は Controller 内で処理済み)
        _stop.Click += (_, _) => _cb.Cancel();
        _close.Click += (_, _) => HideAndCancel();
        AcceptButton = _run;
    }

    public string Pattern => _pattern.Text;
    public string Folder => _folder.Text;
    public string Filter => _filter.Text;
    public bool Recursive => _recursive.Checked;
    public bool MatchCase => _matchCase.Checked;
    public bool WholeWord => _wholeWord.Checked;
    public bool UseRegex => _useRegex.Checked;

    public void SetFolder(string path) => _folder.Text = path;

    private void FocusPattern()
    {
        _pattern.Focus();
        _pattern.SelectAll();
    }

    /// <summary>従来 Open 側で行っていた表示手順(非表示なら Show→Activate→検索語フォーカス)の集約。順序を変えない(Stage 4 と同型)。</summary>
    public void ShowAndFocus(IWin32Window owner)
    {
        if (!Visible)
            Show(owner);
        Activate();
        FocusPattern();
    }

    /// <summary>実行中は入力/検索を無効化し中止のみ可能に。完了で元に戻す。</summary>
    public void SetRunning(bool running)
    {
        _run.Enabled = !running;
        _stop.Enabled = running;
        _pattern.Enabled =
            _folder.Enabled =
            _browse.Enabled =
            _filter.Enabled =
            _recursive.Enabled =
            _matchCase.Enabled =
            _wholeWord.Enabled =
            _useRegex.Enabled =
                !running;
    }

    public void SetStatus(string text) => _status.Text = text;

    /// <summary>ステータス Label を視覚表示しつつ SR 発声も上げる。
    /// Batch D Task 12: `new UiaAnnouncer(_status)` の直生成を廃止して IAnnouncer を注入する構成に変更。
    /// 従来は UiaAnnouncer 側の視覚副作用で `_status.Text` が更新されていたが、共有 Announcer
    /// (MainForm._announceLabel 経路)注入後は視覚更新も明示する必要があるため、
    /// SearchController.Announce と同型の「SR 発声は共有 Announcer / 視覚は view 内 label」パターンに揃える。</summary>
    public void RaiseNotification(string message)
    {
        _status.Text = message;
        _announcer.Say(message);
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog();
        if (Directory.Exists(_folder.Text))
            dlg.SelectedPath = _folder.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _folder.Text = dlg.SelectedPath;
    }

    /// <summary>ダイアログを隠す際は実行中の grep も中止する（隠れたまま走り続けるのを防ぐ）。</summary>
    private void HideAndCancel()
    {
        _cb.Cancel();
        Hide();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            HideAndCancel();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideAndCancel();
            return;
        }
        base.OnFormClosing(e);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(8),
        };

        root.Controls.Add(new Label { Text = "検索する文字列(&P):", AutoSize = true }, 0, 0);
        root.Controls.Add(_pattern, 1, 0);
        root.SetColumnSpan(_pattern, 2);

        root.Controls.Add(new Label { Text = "フォルダ(&D):", AutoSize = true }, 0, 1);
        root.Controls.Add(_folder, 1, 1);
        root.Controls.Add(_browse, 2, 1);

        root.Controls.Add(new Label { Text = "ファイル(&I):", AutoSize = true }, 0, 2);
        root.Controls.Add(_filter, 1, 2);
        root.SetColumnSpan(_filter, 2);

        var opts = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
        };
        opts.Controls.AddRange(_recursive, _matchCase, _wholeWord, _useRegex);
        root.Controls.Add(opts, 0, 3);
        root.SetColumnSpan(opts, 3);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
        };
        buttons.Controls.AddRange(_run, _stop, _close);
        root.Controls.Add(buttons, 0, 4);
        root.SetColumnSpan(buttons, 3);

        root.Controls.Add(_status, 0, 5);
        root.SetColumnSpan(_status, 3);

        Controls.Add(root);
    }
}
