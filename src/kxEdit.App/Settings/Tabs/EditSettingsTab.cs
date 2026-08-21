using kxEdit.App.Settings;
using kxEdit.Core.Settings;

namespace kxEdit.App.Settings.Tabs;

/// <summary>「編集」タブ。表示折り返しの ON/OFF と桁数を扱う。</summary>
public sealed class EditSettingsTab : ISettingsTab
{
    public string Title => "編集";

    private readonly CheckBox _wrapEnabled = new()
    {
        Text = "指定文字数で折り返す(&W)",
        AutoSize = true,
    };
    private readonly NumericUpDown _wrapColumn = new()
    {
        Minimum = 10,
        Maximum = 1000,
        Width = 100,
        AccessibleName = "折り返し桁数",
    };
    private readonly NumericUpDown _tabWidth = new()
    {
        Minimum = 1,
        Maximum = 16,
        Width = 100,
        AccessibleName = "タブ幅",
    };
    private readonly CheckBox _tabsToSpaces = new()
    {
        Text = "タブをスペースに変換(&S)",
        AutoSize = true,
    };

    public Control BuildPage()
    {
        _wrapEnabled.CheckedChanged += (_, _) => _wrapColumn.Enabled = _wrapEnabled.Checked;

        var root = SettingsTabLayoutHelper.NewRoot();

        // 1 行目: チェックボックス（ラベル兼用）。TabIndex=0。
        _wrapEnabled.TabIndex = 0;
        root.Controls.Add(_wrapEnabled, 0, 0);

        // 2 行目: 「折り返し桁数(&K):」ラベル ＋ NumericUpDown。
        var wrapPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            TabIndex = 1,
        };
        var wrapLbl = new Label
        {
            Text = "折り返し桁数(&K):",
            AutoSize = true,
            TabIndex = 1,
            Anchor = AnchorStyles.Left,
        };
        _wrapColumn.TabIndex = 2;
        wrapPanel.Controls.Add(wrapLbl);
        wrapPanel.Controls.Add(_wrapColumn);
        root.Controls.Add(wrapPanel, 1, 0);

        // 2 行目: 「タブ幅(&T):」ラベル ＋ NumericUpDown。
        var tabPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            TabIndex = 3,
        };
        var tabLbl = new Label
        {
            Text = "タブ幅(&T):",
            AutoSize = true,
            TabIndex = 3,
            Anchor = AnchorStyles.Left,
        };
        _tabWidth.TabIndex = 4;
        tabPanel.Controls.Add(tabLbl);
        tabPanel.Controls.Add(_tabWidth);
        root.Controls.Add(tabPanel, 0, 1);

        // 3 行目: タブ→スペース変換（新規 Tab 入力にのみ効く）。
        _tabsToSpaces.TabIndex = 5;
        root.Controls.Add(_tabsToSpaces, 0, 2);

        return root;
    }

    public void LoadFrom(AppSettings s)
    {
        _wrapEnabled.Checked = s.WrapColumnEnabled;
        _wrapColumn.Value = Math.Clamp(
            s.WrapColumn,
            (int)_wrapColumn.Minimum,
            (int)_wrapColumn.Maximum
        );
        _wrapColumn.Enabled = _wrapEnabled.Checked; // 初期状態でも ON/OFF を反映
        _tabWidth.Value = Math.Clamp(s.TabWidth, (int)_tabWidth.Minimum, (int)_tabWidth.Maximum);
        _tabsToSpaces.Checked = s.TabsToSpaces;
    }

    public void SaveTo(AppSettings r)
    {
        r.WrapColumnEnabled = _wrapEnabled.Checked;
        r.WrapColumn = (int)_wrapColumn.Value;
        r.TabWidth = (int)_tabWidth.Value;
        r.TabsToSpaces = _tabsToSpaces.Checked;
    }

    // CA1001 対応(Sub 3.4-B): BuildPage() 経由で Form の Controls ツリーに接続された
    // 場合は Form.Dispose 経由で二重に呼ばれるが、Control.Dispose は冪等なので安全。
    // BuildPage 未呼び出しで破棄された場合(異常系/テスト)のリーク防止が本 Dispose の主目的。
    public void Dispose()
    {
        _wrapEnabled.Dispose();
        _wrapColumn.Dispose();
        _tabWidth.Dispose();
        _tabsToSpaces.Dispose();
    }
}
