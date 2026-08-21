using kxEdit.Core.Settings;

namespace kxEdit.App.Settings.Tabs;

/// <summary>「バックアップ」タブ。自動バックアップの有効/間隔と起動時復元の確認有無を扱う。</summary>
public sealed class BackupSettingsTab : ISettingsTab
{
    public string Title => "バックアップ";

    private readonly CheckBox _enabled = new()
    {
        Text = "文書のバックアップを有効にする(&B)",
        AutoSize = true,
    };
    private readonly NumericUpDown _interval = new()
    {
        Minimum = 5,
        Maximum = 3600,
        Width = 100,
        AccessibleName = "バックアップ間隔（秒）",
    };
    private readonly CheckBox _confirmRestore = new()
    {
        Text = "起動時にバックアップを復元するか確認する(&C)",
        AutoSize = true,
    };

    public Control BuildPage()
    {
        _enabled.CheckedChanged += (_, _) => _interval.Enabled = _enabled.Checked;

        var root = SettingsTabLayoutHelper.NewRoot();

        // 1 行目: 有効チェック（ラベル兼用）。
        _enabled.TabIndex = 0;
        root.Controls.Add(_enabled, 0, 0);
        root.SetColumnSpan(_enabled, 2);

        // 2 行目: 「バックアップ間隔（秒）(&I):」ラベル ＋ NumericUpDown。
        var intervalPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            TabIndex = 1,
        };
        var intervalLbl = new Label
        {
            Text = "バックアップ間隔（秒）(&I):",
            AutoSize = true,
            TabIndex = 1,
            Anchor = AnchorStyles.Left,
        };
        _interval.TabIndex = 2;
        intervalPanel.Controls.Add(intervalLbl);
        intervalPanel.Controls.Add(_interval);
        root.Controls.Add(intervalPanel, 0, 1);
        root.SetColumnSpan(intervalPanel, 2);

        // 3 行目: 復元確認（OFF は確認なしで全復元）。
        _confirmRestore.TabIndex = 3;
        root.Controls.Add(_confirmRestore, 0, 2);
        root.SetColumnSpan(_confirmRestore, 2);

        return root;
    }

    public void LoadFrom(AppSettings s)
    {
        _enabled.Checked = s.BackupEnabled;
        _interval.Value = Math.Clamp(
            s.BackupIntervalSeconds,
            (int)_interval.Minimum,
            (int)_interval.Maximum
        );
        _interval.Enabled = _enabled.Checked; // 初期状態でも ON/OFF を反映
        _confirmRestore.Checked = s.ConfirmRestoreOnStartup;
    }

    public void SaveTo(AppSettings r)
    {
        r.BackupEnabled = _enabled.Checked;
        r.BackupIntervalSeconds = (int)_interval.Value;
        r.ConfirmRestoreOnStartup = _confirmRestore.Checked;
    }

    // CA1001 対応(Sub 3.4-B): BuildPage() を通じて Form の Controls ツリーに接続された
    // 場合は Form.Dispose 経由で二重に呼ばれるが、Control.Dispose は冪等なので安全。
    // BuildPage 未呼び出しで破棄された場合(異常系/テスト)のリーク防止が本 Dispose の主目的。
    public void Dispose()
    {
        _enabled.Dispose();
        _interval.Dispose();
        _confirmRestore.Dispose();
    }
}
