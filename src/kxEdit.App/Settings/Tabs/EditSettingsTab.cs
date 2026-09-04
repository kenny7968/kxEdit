using kxEdit.App.Settings;
using kxEdit.Core.Settings;

namespace kxEdit.App.Settings.Tabs;

/// <summary>「編集」タブ。表示折り返しの ON/OFF と桁数、タブ幅・タブ→スペース、
/// Home キーの動作を扱う。</summary>
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
    private readonly GroupBox _homeGroup = new()
    {
        Text = "Home キーの動作",
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
    };
    private readonly RadioButton _homeSmart = new()
    {
        Text = "行の最初の文字へ移動する(もう一度押すと行頭)(&F)",
        AutoSize = true,
    };
    private readonly RadioButton _homeLineStart = new()
    {
        Text = "常に行頭へ移動する(&B)",
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

        // 4 行目: Home キーの動作(2 択)。
        // ① 排他を与えているのは直下の homePanel。WinForms の RadioButton の排他は
        //    「直上の親の Controls コレクション」内でのみ働くため、既存 CheckBox 群と同じ
        //    TableLayoutPanel に直置きせず専用コンテナに閉じる(将来別のラジオを足しても混線しない)。
        // ② GroupBox が担うのはグループ名。SR がフォーカス時に GroupBox.Text を読む。
        var homePanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            // Dock は必須。省くと FlowLayoutPanel が GroupBox の (0,0) に置かれ、
            // 1 つ目のラジオがキャプション帯(DisplayRectangle より上の領域)に重なって
            // グループ名の文字を潰す(既定 DPI での実測では、省略時のラジオ上端が Y=3、
            // DisplayRectangle は Y=19 から)。AutoSize な GroupBox でも Dock=Fill の子の
            // 推奨サイズは正しく伝わるため、グループは中身に合わせて伸縮する。
            // 退行は EditSettingsTabTests.Radios_are_laid_out_below_the_group_caption が拾う。
            Dock = DockStyle.Fill,
        };
        _homeSmart.TabIndex = 0;
        _homeLineStart.TabIndex = 1;
        homePanel.Controls.Add(_homeSmart);
        homePanel.Controls.Add(_homeLineStart);
        _homeGroup.Controls.Add(homePanel);
        _homeGroup.TabIndex = 6; // 既存の末尾(5 = タブ→スペース)に続く
        root.Controls.Add(_homeGroup, 0, 3);
        root.SetColumnSpan(_homeGroup, 2);

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
        _homeSmart.Checked = s.SmartHome;
        _homeLineStart.Checked = !s.SmartHome;
    }

    public void SaveTo(AppSettings r)
    {
        r.WrapColumnEnabled = _wrapEnabled.Checked;
        r.WrapColumn = (int)_wrapColumn.Value;
        r.TabWidth = (int)_tabWidth.Value;
        r.TabsToSpaces = _tabsToSpaces.Checked;
        r.SmartHome = _homeSmart.Checked;
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
        _homeSmart.Dispose();
        _homeLineStart.Dispose();
        _homeGroup.Dispose();
    }
}
