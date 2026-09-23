using kxEdit.Core.Text;

namespace kxEdit.App;

/// <summary>
/// 名前を付けて保存ダイアログ。パス・文字コード・改行コードを 1 画面で収集する。
/// 参照ボタン内部で SaveFileDialog を呼びパスを取得する(初期の種類は元パスの拡張子で選ぶ)。
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

    // FilterIndexFor の戻り値(1 始まり)はこの並びに対応する(対応は SaveAsDialogTests で固定)。
    // テスト都合で internal。
    internal const string SaveFilter =
        "テキスト ファイル (*.txt)|*.txt|マークダウン ファイル (*.md)|*.md|CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*";

    private void OnBrowseClicked()
    {
        using var dlg = new SaveFileDialog
        {
            // 上書き確認は FileController が全経路で 1 回だけ行う(A-7 (a))。
            // ここで OverwritePrompt(既定 true)を有効なままにすると参照経由だけ 2 回確認が出る
            // (A-7 の訴えは「経路によって確認が出たり出なかったりする非対称」そのもの)。
            // 参照で選んだ後にテキストボックスを編集できる以上、ここでの確認は保存先の確定でもない。
            // 本ファイルは Form=自動テスト対象外。この行の網は L5 の手動確認のみ。
            OverwritePrompt = false,
            Filter = SaveFilter,
            // 判定は FilterIndexFor で L3 検証済み。この代入行自体の網は手動確認のみ(Form=自動テスト対象外)。
            FilterIndex = FilterIndexFor(_path.Text),
        };
        if (!string.IsNullOrEmpty(_path.Text))
            dlg.FileName = System.IO.Path.GetFileName(_path.Text);
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _path.Text = dlg.FileName;
    }

    /// <summary>
    /// 参照ダイアログの初期「ファイルの種類」(<see cref="SaveFilter"/> の 1 始まり index)を
    /// 元パスの拡張子(大文字小文字無視)から決める。
    /// ファイル名部分なし(null・空・空白のみ・末尾が区切り文字)=1(テキスト。従来どおり)/
    /// .txt=1 / .md=2 / .csv=3 / それ以外・拡張子なし=4(すべて)。
    /// 拡張子なしを「すべて」にするのは、*.txt のままだと AddExtension で .txt が付与されるため。
    /// テスト都合で <c>internal</c>(<c>InternalsVisibleTo kxEdit.App.Tests</c>)。
    /// </summary>
    internal static int FilterIndexFor(string? path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name))
            return 1;
        return System.IO.Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".txt" => 1,
            ".md" => 2,
            ".csv" => 3,
            _ => 4,
        };
    }
}
