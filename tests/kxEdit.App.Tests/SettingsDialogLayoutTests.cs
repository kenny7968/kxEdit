using kxEdit.App.Settings;
using kxEdit.Core.Settings;

namespace kxEdit.App.Tests;

/// <summary>
/// 設定ダイアログの寸法決定を固定する(Issue #68)。
/// TabControl はページの中身から推奨サイズを算出せず常に既定 200x100 を返すため、
/// Form の AutoSize に委ねると 136x89 に潰れてマウス操作できなくなっていた。
/// キーボード / UIA 経路は生きたままなので SR 中心の検証では見逃される
/// (CLAUDE.md §2「晴眼・弱視ユーザーも第一級」)。
/// ピクセル即値ではなく包含関係で見るので DPI・フォントに依らない
/// (既存 EditSettingsTabTests と同じ流儀)。
/// 実際の見え方・読み上げは L5 実機検証でしか確認できない(CLAUDE.md §2 a11y 鉄則)。
/// </summary>
public class SettingsDialogLayoutTests
{
    /// <summary>フォームを画面外に可視化する(レイアウト確定に Show が要る)。</summary>
    private static SettingsDialog ShowOffScreen()
    {
        var dlg = new SettingsDialog(new AppSettings())
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ShowInTaskbar = false,
        };
        dlg.Show();
        return dlg;
    }

    private static T Child<T>(Control root)
        where T : Control => root.Controls.OfType<T>().Single();

    [Fact]
    public void Every_tab_page_fits_its_content() =>
        Sta.Run(() =>
        {
            using var dlg = ShowOffScreen();
            var tabs = Child<TabControl>(dlg);
            Assert.NotEmpty(tabs.TabPages);

            foreach (TabPage page in tabs.TabPages)
            {
                // 未選択のページはレイアウトされない(実測: 選択するまで 112x22 のまま)。
                // 全ページを検査するには必ず選択してから測る。
                tabs.SelectedTab = page;
                dlg.PerformLayout();

                var body = page.Controls.OfType<Control>().Single();
                var want = body.GetPreferredSize(Size.Empty);
                Assert.True(
                    page.ClientSize.Width >= want.Width && page.ClientSize.Height >= want.Height,
                    $"タブ「{page.Text}」の表示領域 {page.ClientSize} が本体の希望サイズ {want} を収められない"
                );
            }
        });

    [Fact]
    public void Tab_control_and_button_row_do_not_overlap() =>
        Sta.Run(() =>
        {
            // Dock は子インデックスの大きい方から確定する。Dock=Bottom のボタン列を先に Add すると
            // Dock=Fill の TabControl がクライアント全面を取りボタン列を覆う。
            // (潰れていた間は表面化していなかった 2 つ目の欠陥。DocumentInfoDialog は正しい順序。)
            using var dlg = ShowOffScreen();
            var tabs = Child<TabControl>(dlg);
            var buttons = Child<FlowLayoutPanel>(dlg);
            var client = new Rectangle(Point.Empty, dlg.ClientSize);

            Assert.False(
                tabs.Bounds.IntersectsWith(buttons.Bounds),
                $"TabControl {tabs.Bounds} とボタン列 {buttons.Bounds} が重なっている"
            );
            Assert.True(
                client.Contains(tabs.Bounds),
                $"TabControl {tabs.Bounds} がクライアント領域 {client} からはみ出している"
            );
            Assert.True(
                client.Contains(buttons.Bounds),
                $"ボタン列 {buttons.Bounds} がクライアント領域 {client} からはみ出している"
            );

            // 潰れの直接の被害(OK が画面外・キャンセルが半分だけ)をそのまま固定する。
            foreach (Control b in buttons.Controls)
            {
                var abs = new Rectangle(
                    b.Left + buttons.Left,
                    b.Top + buttons.Top,
                    b.Width,
                    b.Height
                );
                Assert.True(
                    client.Contains(abs),
                    $"ボタン「{b.Text}」{abs} がクライアント領域 {client} の外にある"
                );
            }
        });

    /// <summary>
    /// 寸法がフォントに追従することを固定する。ダイアログの ClientSize は ctor で確定するため、
    /// フォントを差し替えて測り直す網はヘルパ経由でしか張れない。
    /// 「両方の倍率で内容を包含する」だけでは即値実装(十分大きい定数)が生き残るので、
    /// 「倍率を上げたら寸法も増える」ことまで見る。
    /// </summary>
    [Fact]
    public void Client_size_follows_the_font_rather_than_hard_coded_pixels() =>
        Sta.Run(() =>
        {
            var normal = MeasureWithFontScale(1.0f);
            var large = MeasureWithFontScale(1.5f);

            Assert.True(
                large.Width > normal.Width,
                $"フォントを 1.5 倍にしても幅が増えない ({normal.Width} → {large.Width})"
            );
            Assert.True(
                large.Height > normal.Height,
                $"フォントを 1.5 倍にしても高さが増えない ({normal.Height} → {large.Height})"
            );
        });

    /// <summary>指定倍率のフォントでヘルパを呼び、戻り値が内容を包含することを確かめて返す。</summary>
    private static Size MeasureWithFontScale(float scale)
    {
        using var font = new Font(Control.DefaultFont.FontFamily, Control.DefaultFont.Size * scale);
        using var tabs = new TabControl { Font = font };
        using var buttons = new FlowLayoutPanel { AutoSize = true, Font = font };

        var page = new TabPage("ページ");
        var body = new Label
        {
            AutoSize = true,
            Text = "設定項目のラベル",
            Font = font,
        };
        page.Controls.Add(body);
        tabs.TabPages.Add(page);
        buttons.Controls.Add(
            new Button
            {
                Text = "OK",
                AutoSize = true,
                Font = font,
            }
        );

        var size = SettingsTabLayoutHelper.ComputeDialogClientSize(tabs, buttons);
        var wantBody = body.GetPreferredSize(Size.Empty);
        var wantButtons = buttons.GetPreferredSize(Size.Empty);

        Assert.True(
            size.Width >= wantBody.Width,
            $"幅 {size.Width} が本体の希望幅 {wantBody.Width} に足りない"
        );
        Assert.True(
            size.Height >= wantBody.Height + wantButtons.Height,
            $"高さ {size.Height} が本体 {wantBody.Height} + ボタン列 {wantButtons.Height} に足りない"
        );
        return size;
    }
}
