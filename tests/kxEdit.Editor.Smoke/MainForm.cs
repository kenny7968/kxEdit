using System.Text;
using kxEdit.Core.Buffers;
using kxEdit.Core.Text;
using kxEdit.Editor;

namespace kxEdit.Editor.Smoke;

/// <summary>
/// P3 Task 15 の smoke 起動器メインフォーム。EditorControl を Dock=Fill で置き、
/// メニューから UTF-8 / Shift_JIS / EUC-JP のファイルを開いて eye check する用途。
/// P3 で編集入力が有効化された(キー入力・マウス操作・クリップボード・Undo/Redo すべて動作)
/// ため、開いたファイルに対して実際に打鍵/選択/コピペ/元に戻す等の操作を試せる。
/// 変化する状態(CurrentLine/Modified/Overtype/EolMode)は 200ms Timer で
/// ステータスバーへポーリング反映(EditorControl は TextChanged 相当を提供しないため)。
/// SetSource は 1 度限りなので、開き直しごとに EditorControl を差し替える。
/// </summary>
public sealed class MainForm : Form
{
    private EditorControl _editor;
#pragma warning disable S1450 // reason: IDisposable field は Form.Controls 経由の連鎖 Dispose を担保する(local 化は安全ではない)
    private readonly StatusStrip _status;
#pragma warning restore S1450
    private readonly ToolStripStatusLabel _statusFile;
    private readonly ToolStripStatusLabel _statusCaret;
#pragma warning disable S1450 // reason: WinForms.Timer(=Component、Controls 対象外)は Tick で form 状態を継続参照するため field 保持。smoke tool のライフサイクル=プロセス終了までフォーム生存で Dispose 不要
    private readonly System.Windows.Forms.Timer _statusTimer;
#pragma warning restore S1450
    private readonly ToolStripMenuItem _wrapOff;
    private readonly ToolStripMenuItem _wrap40;
    private readonly ToolStripMenuItem _wrap80;
    private readonly ToolStripMenuItem _showLn;
    private readonly ToolStripMenuItem _showWs;
    private readonly ToolStripMenuItem _hlLine;
    private readonly ToolStripMenuItem _readOnly;
    private readonly ToolStripMenuItem _overtype;

    // 開き直し時に前回の表示設定を復元するための保持
    private int _currentWrap;
    private bool _currentShowLn;
    private bool _currentShowWs;
    private bool _currentHlLine;
    private bool _currentReadOnly;
    private bool _currentOvertype;
    private string? _currentPath;
    private string _encodingLabel = "UTF-8";

    public MainForm(string? initialPath)
    {
        Text = "kxEdit.Editor.Smoke";
        Width = 900;
        Height = 700;

        // フィールドを先に初期化してから View メニューのクリックハンドラを配線する
        // (逆順にすると nullable 解析が「_editor が未初期化のままハンドラを capture 」と
        // 誤警告 CS8602 を出す。実行時は capture 後の invocation なので問題ないが黙らせる)。
        _editor = new EditorControl { Dock = DockStyle.Fill };

        // ---- メニュー ----
        var menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("ファイル(&F)");
        var openUtf8 = new ToolStripMenuItem(
            "開く UTF-8(&U)...",
            null,
            (_, _) => OpenFile(Encoding.UTF8, "UTF-8")
        );
        var openSjis = new ToolStripMenuItem(
            "開く Shift_JIS(&S)...",
            null,
            (_, _) => OpenFile(Encoding.GetEncoding(932), "SJIS")
        );
        var openEuc = new ToolStripMenuItem(
            "開く EUC-JP(&E)...",
            null,
            (_, _) => OpenFile(Encoding.GetEncoding(51932), "EUC-JP")
        );
        var quit = new ToolStripMenuItem("終了(&X)", null, (_, _) => Close());
        fileMenu.DropDownItems.AddRange(
            openUtf8,
            openSjis,
            openEuc,
            new ToolStripSeparator(),
            quit
        );
        menu.Items.Add(fileMenu);

        // ---- 編集メニュー(P3 で編集操作が有効化されたため追加) ----
        // ショートカット(Ctrl+Z/Y/X/C/V/A)は EditorControl.OnKeyDown 経由でも動くが、
        // メニューから閲覧しつつ実行できると eye check がラク。ハンドラは EditorControl の
        // 対応メソッド(Undo/Redo/Cut/Copy/Paste/SelectAll)をそのまま呼ぶだけ。
        var editMenu = new ToolStripMenuItem("編集(&E)");
        var undo = new ToolStripMenuItem("元に戻す(&U)", null, (_, _) => _editor.Undo())
        {
            ShortcutKeys = Keys.Control | Keys.Z,
        };
        var redo = new ToolStripMenuItem("やり直し(&R)", null, (_, _) => _editor.Redo())
        {
            ShortcutKeys = Keys.Control | Keys.Y,
        };
        var cut = new ToolStripMenuItem("切り取り(&T)", null, (_, _) => _editor.Cut())
        {
            ShortcutKeys = Keys.Control | Keys.X,
        };
        var copy = new ToolStripMenuItem("コピー(&C)", null, (_, _) => _editor.Copy())
        {
            ShortcutKeys = Keys.Control | Keys.C,
        };
        var paste = new ToolStripMenuItem("貼り付け(&P)", null, (_, _) => _editor.Paste())
        {
            ShortcutKeys = Keys.Control | Keys.V,
        };
        var selectAll = new ToolStripMenuItem("すべて選択(&A)", null, (_, _) => _editor.SelectAll())
        {
            ShortcutKeys = Keys.Control | Keys.A,
        };
        _overtype = new ToolStripMenuItem("上書きモード(&O)") { CheckOnClick = true };
        _overtype.Click += (_, _) =>
        {
            _currentOvertype = _overtype.Checked;
            _editor.Overtype = _currentOvertype;
        };
        editMenu.DropDownItems.AddRange(
            undo,
            redo,
            new ToolStripSeparator(),
            cut,
            copy,
            paste,
            new ToolStripSeparator(),
            selectAll,
            new ToolStripSeparator(),
            _overtype
        );
        menu.Items.Add(editMenu);

        var viewMenu = new ToolStripMenuItem("表示(&V)");
        _wrapOff = new ToolStripMenuItem("折り返し OFF") { Checked = true };
        _wrap40 = new ToolStripMenuItem("折り返し 40 桁");
        _wrap80 = new ToolStripMenuItem("折り返し 80 桁");
        _showLn = new ToolStripMenuItem("行番号") { CheckOnClick = true };
        _showWs = new ToolStripMenuItem("空白可視化") { CheckOnClick = true };
        _hlLine = new ToolStripMenuItem("現在行強調") { CheckOnClick = true };
        _readOnly = new ToolStripMenuItem("読み取り専用") { CheckOnClick = true };
        _wrapOff.Click += (_, _) => SetWrap(0);
        _wrap40.Click += (_, _) => SetWrap(40);
        _wrap80.Click += (_, _) => SetWrap(80);
        _showLn.Click += (_, _) =>
        {
            _currentShowLn = _showLn.Checked;
            _editor.ShowLineNumbers = _currentShowLn;
        };
        _showWs.Click += (_, _) =>
        {
            _currentShowWs = _showWs.Checked;
            _editor.ShowWhitespace = _currentShowWs;
        };
        _hlLine.Click += (_, _) =>
        {
            _currentHlLine = _hlLine.Checked;
            _editor.HighlightCurrentLine = _currentHlLine;
        };
        _readOnly.Click += (_, _) =>
        {
            _currentReadOnly = _readOnly.Checked;
            _editor.ReadOnly = _currentReadOnly;
        };
        viewMenu.DropDownItems.AddRange(
            _wrapOff,
            _wrap40,
            _wrap80,
            new ToolStripSeparator(),
            _showLn,
            _showWs,
            _hlLine,
            new ToolStripSeparator(),
            _readOnly
        );
        menu.Items.Add(viewMenu);

        MainMenuStrip = menu;

        // ---- ステータス ----
        // _statusFile = ファイル名 / 行数 / エンコーディング / 行高(開き直し時に更新)
        // _statusCaret = 現在行 / Modified フラグ / Overtype / EolMode(200ms タイマーで更新)
        _status = new StatusStrip();
        _statusFile = new ToolStripStatusLabel("(未読込)")
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _statusCaret = new ToolStripStatusLabel("")
        {
            Spring = false,
            TextAlign = ContentAlignment.MiddleRight,
            AutoSize = true,
        };
        _status.Items.Add(_statusFile);
        _status.Items.Add(_statusCaret);

        // Dock 順: 中央(Fill)= Editor は最後に追加=最も後段=残余を占有
        // MenuStrip(Top)/StatusStrip(Bottom)/Editor(Fill) の順に Controls.Add する。
        Controls.Add(_editor);
        Controls.Add(_status);
        Controls.Add(menu);

        // ---- ステータス反映タイマー(200ms ポーリング) ----
        // EditorControl は TextChanged / CaretMoved 等のイベントを提供しない(SR 対策で
        // 意図的に外している)ため、状態変化は Timer で拾う。手打ちで確認する用途としては
        // 200ms のラグは十分許容範囲。
        _statusTimer = new System.Windows.Forms.Timer { Interval = 200 };
        // P4 Task 14: IME 状態(未確定文字列)もタイトルバーに反映する。UpdateCaretStatus と
        // 同じ Tick に相乗り(200ms ラグは目視の eye check 用途で十分許容範囲)。
        _statusTimer.Tick += (_, _) =>
        {
            UpdateCaretStatus();
            UpdateTitle();
        };
        _statusTimer.Start();

        if (initialPath is not null && File.Exists(initialPath))
        {
            OpenFilePath(initialPath, Encoding.UTF8, "UTF-8");
        }
    }

    /// <summary>
    /// P4 Task 14: --ime サブコマンド用のコンストラクタ。ファイルを開かずにメモリ上で
    /// 生成した <see cref="TextBuffer"/> を直接流し込んで起動する(ATOK 実機で未確定色/下線を
    /// 目視できる状態から eye check を始めるため)。既存の <see cref="MainForm(string?)"/> を
    /// this(null) で走らせて menu/status/timer を組み立てた後、SetSource だけこちらで行う。
    /// </summary>
    public MainForm(TextBuffer buf, string label)
        : this((string?)null)
    {
        _editor.SetSource(buf);
        _currentPath = label;
        _encodingLabel = "(in-memory)"; // Task 14 レビュー M-3: in-memory サンプルは encoding 非該当
        UpdateFileStatus(buf.Current.LineCount);
        UpdateCaretStatus();
    }

    // P5 Task 13: smoke --uia モードの目印。UIA プロバイダは EditorControl に常時配線済みのため、
    // このフラグの実態は「タイトルバーへ [UIA] を付けて起動モードを判別できるようにする」だけ。
    // 起動側(Program.cs)が MainForm 生成後に true にする。
    // (WFO1000 回避 = デザイナ非対応の宣言)
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden
    )]
    public bool MarkUiaTitle { get; set; }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (MarkUiaTitle && !Text.StartsWith("[UIA] ", StringComparison.Ordinal))
            Text = $"[UIA] {Text}";
    }

    private void SetWrap(int cols)
    {
        _wrapOff.Checked = cols == 0;
        _wrap40.Checked = cols == 40;
        _wrap80.Checked = cols == 80;
        _currentWrap = cols;
        _editor.WrapColumns = cols;
    }

    private void OpenFile(Encoding encoding, string label)
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "テキスト (*.txt;*.md;*.csv;*.log)|*.txt;*.md;*.csv;*.log|すべて (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        OpenFilePath(dlg.FileName, encoding, label);
    }

    /// <summary>
    /// 指定パスを指定エンコーディングで読み、TextBuffer に流し込んで新しい EditorControl に差し替える。
    /// EditorControl.SetSource は 1 度きり契約のため、開き直しごとに旧コントロールを Dispose して
    /// 新しく生成する(=フォーカスは新コントロールに再バインドされる)。
    /// </summary>
    private void OpenFilePath(string path, Encoding encoding, string label)
    {
        try
        {
            string text = File.ReadAllText(path, encoding);
            var buf = TextBuffer.FromString(text);

            var old = _editor;
            Controls.Remove(old);
            old.Dispose();

            _editor = new EditorControl { Dock = DockStyle.Fill };
            Controls.Add(_editor);
            // Dock=Fill は Controls コレクション上「先頭(index=0)」に置いた子=最後に dock 処理される=
            // 残余(menu と status を引いた領域)を占有する。Controls コレクションは Add で末尾に
            // 追加されるため、明示的に index=0 へ移して ctor と同じ [editor, status, menu] 順にする。
            Controls.SetChildIndex(_editor, 0);

            _editor.SetSource(buf);
            // 前回の表示設定を復元
            _editor.WrapColumns = _currentWrap;
            _editor.ShowLineNumbers = _currentShowLn;
            _editor.ShowWhitespace = _currentShowWs;
            _editor.HighlightCurrentLine = _currentHlLine;
            _editor.ReadOnly = _currentReadOnly;
            _editor.Overtype = _currentOvertype;
            _editor.Focus();

            _currentPath = path;
            _encodingLabel = label;
            UpdateFileStatus(buf.Current.LineCount);
            UpdateCaretStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "読み込みエラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }

    private void UpdateFileStatus(int lineCount)
    {
        _statusFile.Text =
            $"{_currentPath ?? "(無題)"}  |  行数: {lineCount:N0}  |  Encoding: {_encodingLabel}  |  行高: {_editor.LineHeightPx}px";
    }

    /// <summary>
    /// キャレット行/Modified/Overtype/EolMode をステータスバーへ反映する。200ms Timer から呼ばれる。
    /// CurrentLine は 0 始まりなので表示は +1。EolMode は enum の名前をそのまま出す。
    /// </summary>
    private void UpdateCaretStatus()
    {
        int line1 = _editor.CurrentLine + 1;
        string mark = _editor.Modified ? "*" : "";
        string mode = _editor.Overtype ? "上書き" : "挿入";
        string eol = _editor.EolMode.ToDisplayString();
        _statusCaret.Text = $"L{line1}{mark}  |  {mode}  |  {eol}";
    }

    /// <summary>
    /// P4 Task 14: IME 未確定期間中はタイトルバーに未確定文字列を表示する。200ms Timer から
    /// 呼ばれる(UpdateCaretStatus と同じ Tick)。IsComposing=false のときは既定タイトルに戻す。
    /// ATOK 実機検証で「WM_IME_COMPOSITION が届いているか(=タイトルバーに反映されるか)」の
    /// 一次切り分けに使う(NG 時の diagnostics=docs/plans/2026-07-06-p4-ime-checklist.md 参照)。
    /// </summary>
    private void UpdateTitle()
    {
        if (_editor.__SmokeIsComposing())
            Text = $"kxEdit.Editor.Smoke [IME: {_editor.__SmokeImeText()}]";
        else
            Text = "kxEdit.Editor.Smoke";
    }
}
