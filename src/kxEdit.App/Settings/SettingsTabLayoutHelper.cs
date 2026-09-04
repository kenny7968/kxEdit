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

    /// <summary>
    /// 設定ダイアログのクライアント寸法を、全タブページの希望サイズから算出する。
    /// <see cref="TabControl"/> はページの中身から推奨サイズを算出せず常に既定の 200x100 を
    /// 返すため、Form の AutoSize に委ねると 136x89 に潰れる(Issue #68・実測)。
    /// Form.AutoSize は Dock=Fill の子の希望サイズを見ないので、TabControl 側に
    /// MinimumSize を与えても直らない(実測で否定済み)。
    /// 即値は一切使わず枠も実測するため、フォント・DPI に自動追従する。
    /// </summary>
    public static Size ComputeDialogClientSize(TabControl tabs, Control buttons)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(buttons);

        var body = Size.Empty;
        foreach (TabPage page in tabs.TabPages)
        {
            foreach (Control c in page.Controls)
            {
                var want = c.GetPreferredSize(Size.Empty);
                body = new Size(
                    Math.Max(body.Width, want.Width),
                    Math.Max(body.Height, want.Height)
                );
            }
        }

        var frame = MeasureTabFrame(tabs.Font);
        var buttonRow = buttons.GetPreferredSize(Size.Empty);
        return new Size(
            Math.Max(body.Width + frame.Width, buttonRow.Width),
            body.Height + frame.Height + buttonRow.Height
        );
    }

    /// <summary>
    /// タブ枠(ヘッダ帯＋境界)の実測。親に接続していない probe で測るのは、Dock 済みの実物へ
    /// Size を代入してもレイアウトが即座に上書きしてしまい測れないため(実測)。
    /// 枠はフォントだけで決まり、ページ枚数にもキャプション文字列にも依存しない
    /// (実測: 1 枚と 5 枚・文字列違いで同値)。ただし 0 枚だとヘッダ帯が現れず測れないので 1 枚載せる。
    /// Multiline=false(既定)なのでヘッダは常に 1 段であり、幅による段数変動は起きない。
    /// </summary>
    private static Size MeasureTabFrame(Font font)
    {
        const int Probe = 1000; // 枠より十分大きければ測定値は変わらない
        using var probe = new TabControl { Font = font, Size = new Size(Probe, Probe) };
        using var page = new TabPage("A"); // probe.Dispose でも解放されるが二重解放は安全
        probe.TabPages.Add(page);
        var display = probe.DisplayRectangle;
        return new Size(Probe - display.Width, Probe - display.Height);
    }
}
