using kxEdit.App.Settings.Tabs;
using kxEdit.Core.Settings;

namespace kxEdit.App.Tests;

/// <summary>
/// [表示]タブのフォントの反映(フェーズ 7 の項目 4)。FontDialog は LOGFONT の整数ピクセル高から
/// Font を作り直すので、96 DPI で 20pt を選ぶと 20.25pt で返る。ダイアログで選んだときだけ
/// 0.5pt 単位に丸め、読み込んだ設定値は丸めない。
/// </summary>
public class DisplaySettingsTabTests
{
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c))
                yield return d;
        }
    }

    private static Button FontButton(Control page) =>
        Descendants(page)
            .OfType<Button>()
            .Single(b => b.Text.StartsWith("変更(&F)", StringComparison.Ordinal));

    [Fact]
    public void Picked_font_size_is_rounded_to_half_point() =>
        Sta.Run(() =>
        {
            using var tab = new DisplaySettingsTab();
            var page = tab.BuildPage();
            // 読み込んだ値と違う名前・大きさを選ぶ(名前の書き込み漏れも赤にする)。
            tab.LoadFrom(new AppSettings { FontName = "Arial", FontSize = 9f });

            using var picked = new Font("Consolas", 20.25f);
            tab.ApplyPickedFont(picked);

            var r = new AppSettings();
            tab.SaveTo(r);
            Assert.Equal("Consolas", r.FontName);
            Assert.Equal(20f, r.FontSize);
            Assert.Equal("フォント変更 現在 Consolas, 20 pt", FontButton(page).AccessibleName);
        });

    [Fact]
    public void Loaded_font_size_is_not_rounded() =>
        Sta.Run(() =>
        {
            using var tab = new DisplaySettingsTab();
            _ = tab.BuildPage();
            // 非既定の 20.25 から始める(既定の 12 のままでは、丸めても丸めなくても同じ)。
            tab.LoadFrom(new AppSettings { FontName = "Consolas", FontSize = 20.25f });

            var r = new AppSettings();
            tab.SaveTo(r);
            Assert.Equal(20.25f, r.FontSize);
        });
}
