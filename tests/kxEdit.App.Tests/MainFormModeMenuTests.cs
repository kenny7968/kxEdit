using System.Reflection;
using kxEdit.Core.Csv;
using kxEdit.Core.Settings;

namespace kxEdit.App.Tests;

/// <summary>
/// モードメニュー(モード(&amp;M))のショートカットキー配線(2026-09-23 設計書 + 最終レビュー I-2)。
/// マークダウンプレビュー = Ctrl+Shift+M / CSVモード = Ctrl+Shift+K。
/// <para>
/// <b>2 項目で登録方式が違う</b>: プレビューは <c>ShortcutKeys</c>(キーとメニュークリックが
/// 同一ハンドラ)。CSVモードは<b>メニューはトグル・キーは進入専用</b>という非対称な要件のため
/// <c>ShortcutKeys</c> では両立できず、表示は <c>ShortcutKeyDisplayString</c> のみで
/// キーは <c>MainForm.ProcessCmdKey</c> が処理する。
/// </para>
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
    private const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

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

    /// <summary>protected override の <c>MainForm.ProcessCmdKey</c> へキーを直接届ける
    /// (既存パターン: FindReplaceDialogTests)。戻り値 true=MainForm が自分で処理した。
    /// false なら配線が無く、「効いていない」を空振りで観測してしまうためテストごと落とす。</summary>
    private static void SendCmdKey(MainForm form, Keys keyData)
    {
        var m = typeof(MainForm).GetMethod("ProcessCmdKey", Priv);
        Assert.NotNull(m);
        object?[] args = { default(Message), keyData };
        Assert.True((bool)m!.Invoke(form, args)!, $"{keyData} が MainForm で処理されていない");
    }

    /// <summary>
    /// <c>MainForm.ProcessCmdKey</c> が <c>base</c> 呼出より前に食うキー（switch と対で保つ）。
    /// ここに載るキーを <c>ShortcutKeys</c> に登録すると、メニュー項目の Click は黙って死ぬ
    /// （Ctrl+Shift+J 衝突と同じ失敗モード。最終レビュー I-4）。
    /// </summary>
    private static readonly Keys[] OwnedByProcessCmdKey =
    [
        Keys.Control | Keys.Tab,
        Keys.Control | Keys.Shift | Keys.Tab,
        Keys.F3,
        Keys.Shift | Keys.F3,
        Keys.Control | Keys.Alt | Keys.P,
        Keys.Control | Keys.G,
        Keys.Control | Keys.Shift | Keys.K,
        Keys.Insert,
        Keys.Control | Keys.D1,
        Keys.Control | Keys.D2,
        Keys.Control | Keys.D3,
        Keys.Control | Keys.D4,
        Keys.Control | Keys.D5,
        Keys.Control | Keys.D6,
        Keys.Control | Keys.D7,
        Keys.Control | Keys.D8,
        Keys.Control | Keys.D9,
    ];

    // プレビューは ShortcutKeys（キー＝メニュークリックと同一ハンドラ）。
    // CSVモードは ShortcutKeys を持たず表示だけ = メニューのトグル(OFF)が生きている状態。
    // ShortcutKeys へ戻す退行は、メニューからの OFF を黙って殺すのでここで赤くする（最終レビュー I-2）。
    [Fact]
    public void Mode_menu_items_have_expected_shortcut_wiring() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(tmp);

            Assert.Equal(
                Keys.Control | Keys.Shift | Keys.M,
                ItemOf(form, "マークダウンプレビュー(&P)").ShortcutKeys
            );

            var csvItem = ItemOf(form, "CSVモード(&C)");
            Assert.Equal(Keys.None, csvItem.ShortcutKeys);
            Assert.Equal("Ctrl+Shift+K", csvItem.ShortcutKeyDisplayString);
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
            Assert.Equal(shortcuts.Count, shortcuts.Distinct().Count());
        });

    [Fact]
    public void Menu_shortcuts_do_not_collide_with_ProcessCmdKey_keys() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(tmp);

            var shortcuts = AllMenuItems(form)
                .Select(mi => mi.ShortcutKeys)
                .Where(k => k != Keys.None)
                .ToHashSet();

            // 陽性対照: 走査が空／表が空だと「衝突なし」が空虚に緑になる。
            Assert.NotEmpty(shortcuts);
            Assert.Contains(Keys.Control | Keys.Shift | Keys.K, OwnedByProcessCmdKey);

            Assert.Empty(shortcuts.Intersect(OwnedByProcessCmdKey));
        });

    // メニューの「CSVモード」は従来どおりトグル: 再選択で OFF になる（最終レビュー I-2 で維持を決定）。
    // CSVモードの視覚的表示はこのチェックマークだけで Esc はどこにも出ていないため、
    // メニューからの OFF は晴眼・弱視ユーザーの唯一の出口。EnterMode 配線への退行は
    // 2 回目で ModeAlreadyOn になるのでここで赤くなる。
    // 起動時の無題タブは本文が空 = CsvParser.Parse("") は Ok かつ Rows.Count==0 なので
    // TryEnterMode の「データ無し」分岐を通り ModeOn だけを発声する(前提として assert する)。
    [Fact]
    public void Csv_mode_menu_item_toggles_off_on_reselect() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(tmp);
            var csvItem = ItemOf(form, "CSVモード(&C)");

            csvItem.PerformClick(); // 1 回目: 進入
            Assert.Equal(CsvAnnounceFormatter.ModeOn, form.LastAnnouncementForTest);

            csvItem.PerformClick(); // 2 回目: 解除

            Assert.Equal(CsvAnnounceFormatter.ModeOff, form.LastAnnouncementForTest);
        });

    // キー Ctrl+Shift+K は ProcessCmdKey 経由で EnterMode(進入専用)へ繋がる。
    // メニューと違いトグルしない = 2 回目は ModeAlreadyOn を言うだけでモードが落ちないこと。
    // 非既定状態(モード ON)から 2 回目を撃つので、既定値との区別が付く(CLAUDE.md §4-B)。
    [Fact]
    public void Ctrl_shift_k_enters_csv_mode_and_does_not_toggle_off() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(tmp);

            SendCmdKey(form, Keys.Control | Keys.Shift | Keys.K); // 1 回目: 進入
            Assert.Equal(CsvAnnounceFormatter.ModeOn, form.LastAnnouncementForTest);

            SendCmdKey(form, Keys.Control | Keys.Shift | Keys.K); // 2 回目: トグルしない

            // ToggleMode 配線への退行なら ModeOff になり、モードも落ちる。
            Assert.Equal(CsvAnnounceFormatter.ModeAlreadyOn, form.LastAnnouncementForTest);
        });
}
