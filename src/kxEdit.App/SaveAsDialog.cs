using kxEdit.Core.Text;

namespace kxEdit.App;

/// <summary>
/// 名前を付けて保存ダイアログ。パス・文字コード・改行コードを 1 画面で収集する。
/// 参照ボタン内部で SaveFileDialog を呼びパスを取得する(拡張子フィルタは従来どおり)。
/// アクセシビリティ: TabIndex は パス→参照→エンコード→改行→OK→キャンセル の順。
/// </summary>
public sealed class SaveAsDialog : Form
{
    private readonly TextBox _path = new() { Width = 320, AccessibleName = "ファイル名" };
    private readonly ComboBox _encoding = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 240,
        AccessibleName = "文字コード",
    };
    private readonly ComboBox _lineEnding = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 160,
        AccessibleName = "改行コード",
    };

    private static readonly IReadOnlyList<EncodingCatalog.SaveAsEncodingOption> EncodingChoices =
        EncodingCatalog.SaveAsSelectableEncodings;

    // 改行の選択肢。表示名/値のペア。
    private static readonly (string Label, LineEnding Value)[] LineEndingChoices = new[]
    {
        ("CRLF (Windows)", LineEnding.Crlf),
        ("LF (Unix)", LineEnding.Lf),
        ("CR (Old Mac)", LineEnding.Cr),
    };

    public string SelectedPath => _path.Text;
    public int SelectedCodePage => EncodingChoices[_encoding.SelectedIndex].CodePage;
    public bool SelectedHasBom => EncodingChoices[_encoding.SelectedIndex].HasBom;
    public LineEnding SelectedLineEnding => LineEndingChoices[_lineEnding.SelectedIndex].Value;

    public SaveAsDialog(
        string? initialPath,
        int currentCodePage,
        bool currentHasBom,
        LineEnding currentLineEnding
    )
    {
        Text = "名前を付けて保存";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _path.Text = initialPath ?? "";

        int encSel = 0;
        for (int i = 0; i < EncodingChoices.Count; i++)
        {
            var e = EncodingChoices[i];
            _encoding.Items.Add(e.DisplayName);
            // (codePage, hasBom) 完全一致の行を初期選択。UTF-8 では BOM 有無で 2 行あるので厳密一致が必要。
            // 非 UTF-8 は HasBom=false 固定のエントリしか無いので実質 CodePage 一致で決まる。
            if (e.CodePage == currentCodePage && e.HasBom == currentHasBom)
                encSel = i;
        }
        _encoding.SelectedIndex = encSel;

        int leSel = 0;
        for (int i = 0; i < LineEndingChoices.Length; i++)
        {
            _lineEnding.Items.Add(LineEndingChoices[i].Label);
            if (LineEndingChoices[i].Value == currentLineEnding)
                leSel = i;
        }
        _lineEnding.SelectedIndex = leSel;

        BuildLayout();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(12),
        };

        var pathLabel = new Label
        {
            Text = "ファイル名(&F):",
            AutoSize = true,
            TabIndex = 0,
        };
        var browseButton = new Button
        {
            Text = "参照(&B)...",
            AutoSize = true,
            TabIndex = 2,
        };
        _path.TabIndex = 1;
        root.Controls.Add(pathLabel, 0, 0);
        root.Controls.Add(_path, 1, 0);
        root.Controls.Add(browseButton, 2, 0);

        var encLabel = new Label
        {
            Text = "文字コード(&E):",
            AutoSize = true,
            TabIndex = 3,
        };
        _encoding.TabIndex = 4;
        root.Controls.Add(encLabel, 0, 1);
        root.Controls.Add(_encoding, 1, 1);
        root.SetColumnSpan(_encoding, 2);

        var leLabel = new Label
        {
            Text = "改行コード(&L):",
            AutoSize = true,
            TabIndex = 5,
        };
        _lineEnding.TabIndex = 6;
        root.Controls.Add(leLabel, 0, 2);
        root.Controls.Add(_lineEnding, 1, 2);
        root.SetColumnSpan(_lineEnding, 2);

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            TabIndex = 7,
        };
        var cancel = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            TabIndex = 8,
        };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
        };
        buttons.Controls.AddRange(cancel, ok);
        root.Controls.Add(buttons, 0, 3);
        root.SetColumnSpan(buttons, 3);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;

        browseButton.Click += (_, _) => OnBrowseClicked();
    }

    private void OnBrowseClicked()
    {
        using var dlg = new SaveFileDialog
        {
            Filter =
                "テキスト ファイル (*.txt)|*.txt|マークダウン ファイル (*.md)|*.md|CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
        };
        if (!string.IsNullOrEmpty(_path.Text))
            dlg.FileName = System.IO.Path.GetFileName(_path.Text);
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _path.Text = dlg.FileName;
    }
}
