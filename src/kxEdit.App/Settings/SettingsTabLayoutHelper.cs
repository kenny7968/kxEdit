namespace kxEdit.App.Settings;

/// <summary>タブ内 2 列 TableLayoutPanel の行追加ヘルパ。全 4 タブで共用する。</summary>
internal static class SettingsTabLayoutHelper
{
    /// <summary>ラベル＋任意コントロールを 1 行として追加する。TabIndex はラベル→コントロールの順に採番。</summary>
    public static void AddRow(
        TableLayoutPanel root,
        int row,
        string label,
        Control control,
        int tabBase
    )
    {
        var lbl = new Label
        {
            Text = label,
            AutoSize = true,
            TabIndex = tabBase,
        };
        control.TabIndex = tabBase + 1;
        root.Controls.Add(lbl, 0, row);
        root.Controls.Add(control, 1, row);
    }

    /// <summary>タブ内 TableLayoutPanel の共通生成。2 列・AutoSize・Padding 統一。</summary>
    public static TableLayoutPanel NewRoot() =>
        new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(12),
        };
}
