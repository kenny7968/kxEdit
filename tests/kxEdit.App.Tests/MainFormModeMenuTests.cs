using kxEdit.Core.Csv;
using kxEdit.Core.Settings;

namespace kxEdit.App.Tests;

/// <summary>
/// モードメニュー(モード(&amp;M))のショートカットキー配線(2026-09-23 設計書)。
/// マークダウンプレビュー = Ctrl+Shift+M / CSVモード = Ctrl+Shift+K。
/// <para>
/// <b>なぜメニュー全体の重複走査が要るか</b>: 当初案の Ctrl+Shift+J は既存の
/// 「折り返し整形(禁則処理)」が使用中だった。<b>同一 ShortcutKeys を 2 項目に登録しても
/// WinForms は例外を出さず、どちらか一方だけが発火して片方が黙って死ぬ</b>ため、
/// 重複は静的初期化でも実行時例外でも捕まらない。<c>CsvCommands.ByKey</c> が Add 形式の
/// 初期化子でキー重複を検出しているのと同じ網を、メニュー側にも張る。
/// </para>
/// </summary>
public class MainFormModeMenuTests
{
    /// <summary>MainForm を可視状態まで作る(TempDir 隔離は MainFormSmokeTests と同方式)。</summary>
    private static MainForm ShowMainForm(TempDir tmp)
    {
        var form = new MainForm(
            new AppSettings { BackupEnabled = false, CsvAutoModeOnOpen = false },
            System.IO.Path.Combine(tmp.Root, "settings.json"),
            backupDirectory: System.IO.Path.Combine(tmp.Root, "backups"),
            sessionLayoutPath: System.IO.Path.Combine(tmp.Root, "session-state.json")
        );
        form.SetLastSessionBuffersPathForTest(
            System.IO.Path.Combine(tmp.Root, "last-session-buffers.json")
        );
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Show();
        return form;
    }

    /// <summary>メニュー全体(入れ子を含む)の ToolStripMenuItem を列挙する。</summary>
    private static List<ToolStripMenuItem> AllMenuItems(MainForm form)
    {
        var acc = new List<ToolStripMenuItem>();
        void Walk(ToolStripItemCollection items)
        {
            foreach (var mi in items.OfType<ToolStripMenuItem>())
            {
                acc.Add(mi);
                Walk(mi.DropDownItems);
            }
        }
        Walk(form.MainMenuStrip!.Items);
        return acc;
    }

    private static ToolStripMenuItem ItemOf(MainForm form, string text) =>
        AllMenuItems(form).Single(mi => mi.Text == text);

    [Fact]
    public void Mode_menu_items_have_expected_shortcuts() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(tmp);

            Assert.Equal(
                Keys.Control | Keys.Shift | Keys.M,
                ItemOf(form, "マークダウンプレビュー(&P)").ShortcutKeys
            );
            Assert.Equal(
                Keys.Control | Keys.Shift | Keys.K,
                ItemOf(form, "CSVモード(&C)").ShortcutKeys
            );
        });

    // 陽性対照: 既存の Ctrl+Shift+J(折り返し整形)を奪っていないこと。
    // 当初案どおり J を割り当てると、この 2 本のどちらかが必ず赤くなる。
    [Fact]
    public void Kinsoku_format_keeps_ctrl_shift_j() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(tmp);

            Assert.Equal(
                Keys.Control | Keys.Shift | Keys.J,
                ItemOf(form, "折り返し整形（禁則処理）(&K)").ShortcutKeys
            );
        });

    [Fact]
    public void Menu_shortcut_keys_are_unique_across_whole_menu() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(tmp);

            var shortcuts = AllMenuItems(form)
                .Where(mi => mi.ShortcutKeys != Keys.None)
                .Select(mi => mi.ShortcutKeys)
                .ToList();

            // 陽性対照: 走査が空だと「重複なし」が空虚に緑になる。
            Assert.Contains(Keys.Control | Keys.Shift | Keys.M, shortcuts);
            Assert.Contains(Keys.Control | Keys.Shift | Keys.K, shortcuts);
            Assert.Equal(shortcuts.Count, shortcuts.Distinct().Count());
        });

    // 挙動変更(設計書): モードメニューの「CSVモード」再選択では OFF にならない。
    // 起動時の無題タブは本文が空 = CsvParser.Parse("") は Ok かつ Rows.Count==0 なので
    // TryEnterMode の「データ無し」分岐を通り ModeOn だけを発声する(前提として assert する)。
    [Fact]
    public void Csv_mode_menu_item_enters_but_does_not_toggle_off() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(tmp);
            var csvItem = ItemOf(form, "CSVモード(&C)");

            csvItem.PerformClick(); // 1 回目: 進入
            Assert.Equal(CsvAnnounceFormatter.ModeOn, form.LastAnnouncementForTest);

            csvItem.PerformClick(); // 2 回目: トグルしない

            // ToggleMode 配線への退行なら ModeOff になる。
            Assert.Equal(CsvAnnounceFormatter.ModeAlreadyOn, form.LastAnnouncementForTest);
        });
}
