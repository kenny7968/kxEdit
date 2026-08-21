using kxEdit.App.Settings;
using kxEdit.Core.Settings;
using kxEdit.Core.Text;

namespace kxEdit.App.Settings.Tabs;

/// <summary>「基本」タブ。既定の文字コードと既定の改行を扱う。</summary>
public sealed class BasicSettingsTab : ISettingsTab
{
    public string Title => "基本";

    private static readonly IReadOnlyList<EncodingCatalog.EncodingOption> Encodings =
        EncodingCatalog.SelectableEncodings;
    private static readonly (string Name, int Id)[] Eols =
    {
        ("CRLF（Windows）", 0),
        ("LF（Unix）", 1),
        ("CR（旧 Mac）", 2),
    };

    private readonly ComboBox _encoding = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 240,
        AccessibleName = "既定の文字コード",
    };
    private readonly ComboBox _eol = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 240,
        AccessibleName = "既定の改行",
    };
    private readonly CheckBox _csvAutoMode = new()
    {
        Text = ".csvファイルを開いたとき自動的にCSVモードにする(&V)",
        AutoSize = true,
    };
    private readonly CheckBox _restoreOnStartup = new()
    {
        Text = "起動時に前回開いていたファイルを開く(&R)",
        AutoSize = true,
    };

    public Control BuildPage()
    {
        foreach (var e in Encodings)
            _encoding.Items.Add(e.DisplayName);
        foreach (var (name, _) in Eols)
            _eol.Items.Add(name);

        var root = SettingsTabLayoutHelper.NewRoot();
        SettingsTabLayoutHelper.AddRow(root, 0, "既定の文字コード(&E):", _encoding, tabBase: 0);
        SettingsTabLayoutHelper.AddRow(root, 1, "既定の改行(&L):", _eol, tabBase: 2);

        _csvAutoMode.TabIndex = 4;
        root.Controls.Add(_csvAutoMode, 0, 2);
        root.SetColumnSpan(_csvAutoMode, 2);

        _restoreOnStartup.TabIndex = 5;
        root.Controls.Add(_restoreOnStartup, 0, 3);
        root.SetColumnSpan(_restoreOnStartup, 2);
        return root;
    }

    public void LoadFrom(AppSettings s)
    {
        int encSel = 0;
        for (int i = 0; i < Encodings.Count; i++)
            if (Encodings[i].CodePage == s.DefaultCodePage)
            {
                encSel = i;
                break;
            }
        _encoding.SelectedIndex = encSel;

        int eolSel = 0;
        for (int i = 0; i < Eols.Length; i++)
            if (Eols[i].Id == s.DefaultLineEnding)
            {
                eolSel = i;
                break;
            }
        _eol.SelectedIndex = eolSel;

        _csvAutoMode.Checked = s.CsvAutoModeOnOpen;
        _restoreOnStartup.Checked = s.RestoreOpenFilesOnStartup;
    }

    public void SaveTo(AppSettings r)
    {
        r.DefaultCodePage = Encodings[_encoding.SelectedIndex].CodePage;
        r.DefaultLineEnding = Eols[_eol.SelectedIndex].Id;
        r.CsvAutoModeOnOpen = _csvAutoMode.Checked;
        r.RestoreOpenFilesOnStartup = _restoreOnStartup.Checked;
    }

    // CA1001 対応(Sub 3.4-B): BuildPage() 経由で Form の Controls ツリーに接続された
    // 場合は Form.Dispose 経由で二重に呼ばれるが、Control.Dispose は冪等なので安全。
    // BuildPage 未呼び出しで破棄された場合(異常系/テスト)のリーク防止が本 Dispose の主目的。
    public void Dispose()
    {
        _encoding.Dispose();
        _eol.Dispose();
        _csvAutoMode.Dispose();
        _restoreOnStartup.Dispose();
    }
}
