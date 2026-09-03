using kxEdit.App.Settings.Tabs;
using kxEdit.Core.Settings;

namespace kxEdit.App.Tests;

/// <summary>
/// [編集]タブの Home キー動作ラジオ(2026-09-04)の構造と往復を固定する。
/// RadioButton はアプリ全体で初出のため、①排他が実際に効く配置になっているか
/// ②アクセスキーが既存項目と衝突していないか ③ラジオがグループのキャプションに
/// 重ならないか を機械で見る。
/// ここで見られるのは構造と包含関係まで。実際の読み上げ文言・見え方は
/// L5 実機検証でしか確認できない(CLAUDE.md §2 a11y 鉄則)。
/// </summary>
public class EditSettingsTabTests
{
    private static (EditSettingsTab tab, Control page) Build()
    {
        var tab = new EditSettingsTab();
        var page = tab.BuildPage();
        return (tab, page);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c))
                yield return d;
        }
    }

    private static RadioButton Radio(Control page, string startsWith) =>
        Descendants(page)
            .OfType<RadioButton>()
            .Single(r => r.Text.StartsWith(startsWith, StringComparison.Ordinal));

    [Fact]
    public void Loads_smart_home_true_as_the_first_radio() =>
        Sta.Run(() =>
        {
            var (tab, page) = Build();
            using (tab)
            {
                tab.LoadFrom(new AppSettings { SmartHome = true });
                Assert.True(Radio(page, "行の最初の文字").Checked);
                Assert.False(Radio(page, "常に行頭").Checked);

                // SaveTo が「読み込んだ値」ではなく「ラジオの状態」を書いていることを見る。
                // 非既定 false から始めることで、書き込み漏れなら false のまま赤くなる。
                var saved = new AppSettings { SmartHome = false };
                tab.SaveTo(saved);
                Assert.True(saved.SmartHome);
            }
        });

    [Fact]
    public void Loads_smart_home_false_as_the_second_radio() =>
        Sta.Run(() =>
        {
            var (tab, page) = Build();
            using (tab)
            {
                tab.LoadFrom(new AppSettings { SmartHome = false });
                Assert.False(Radio(page, "行の最初の文字").Checked);
                Assert.True(Radio(page, "常に行頭").Checked);

                // 既定 true の AppSettings に対して SaveTo が false を書けること
                // (書き込み漏れだと既定値のまま緑になるので、非既定側から検証する)
                var saved = new AppSettings();
                Assert.True(saved.SmartHome);
                tab.SaveTo(saved);
                Assert.False(saved.SmartHome);
            }
        });

    [Fact]
    public void Loading_false_after_true_clears_the_first_radio() =>
        Sta.Run(() =>
        {
            // LoadFrom は同じインスタンスに対して繰り返し呼ばれる(設定ダイアログを
            // 開き直すたび)。2 本目の代入が片方向でも、初回ロードだけならラジオの
            // 自動排他が結果を取り繕ってしまうため、true → false の遷移で固定する。
            var (tab, page) = Build();
            using (tab)
            {
                tab.LoadFrom(new AppSettings { SmartHome = true });
                tab.LoadFrom(new AppSettings { SmartHome = false });
                Assert.False(Radio(page, "行の最初の文字").Checked);
                Assert.True(Radio(page, "常に行頭").Checked);
            }
        });

    [Fact]
    public void The_two_radios_are_mutually_exclusive() =>
        Sta.Run(() =>
        {
            // 同一コンテナに置かれていることの機械的な証明(WinForms のラジオ排他は
            // 直上の親の Controls コレクション内でのみ働く)。別々のコンテナに散ると
            // 両方 Checked になり、設定が意味を失う。
            var (tab, page) = Build();
            using (tab)
            {
                var smart = Radio(page, "行の最初の文字");
                var always = Radio(page, "常に行頭");

                always.Checked = true;
                Assert.False(smart.Checked);
                smart.Checked = true;
                Assert.False(always.Checked);
            }
        });

    [Fact]
    public void Radios_live_inside_a_named_group_box() =>
        Sta.Run(() =>
        {
            // SR がフォーカス時にグループ名を読むための前提。
            var (tab, page) = Build();
            using (tab)
            {
                var group = Descendants(page).OfType<GroupBox>().Single();
                Assert.Equal("Home キーの動作", group.Text);
                Assert.Contains(Radio(page, "行の最初の文字"), Descendants(group));
                Assert.Contains(Radio(page, "常に行頭"), Descendants(group));
            }
        });

    [Fact]
    public void The_home_group_is_the_last_stop_in_the_tab_order() =>
        Sta.Run(() =>
        {
            // 既存の末尾「タブをスペースに変換」(TabIndex=5)の後に来ること。
            var (tab, page) = Build();
            using (tab)
            {
                var group = Descendants(page).OfType<GroupBox>().Single();
                var siblings = page.Controls.Cast<Control>().Where(c => c != group);
                Assert.NotEmpty(siblings);
                Assert.All(siblings, c => Assert.True(c.TabIndex < group.TabIndex));
                // グループ内はスマート → 常に行頭 の順。
                Assert.True(
                    Radio(page, "行の最初の文字").TabIndex < Radio(page, "常に行頭").TabIndex
                );
            }
        });

    [Fact]
    public void Radios_are_laid_out_below_the_group_caption() =>
        Sta.Run(() =>
        {
            // レイアウトを実際に走らせないと現れない退行を 1 本だけ固定する。
            // FlowLayoutPanel の Dock=DockStyle.Fill を外すと、パネルが GroupBox の
            // (0,0) に置かれ 1 つ目のラジオがキャプション帯に重なる。
            // ピクセル値ではなく「DisplayRectangle に収まる」という包含関係で見るため、
            // DPI やフォント(= GroupBox が AutoSize で追随する)に依らない。
            var (tab, page) = Build();
            using (tab)
            using (var host = new Form { Size = new Size(800, 600) })
            {
                host.Controls.Add(page);
                host.CreateControl();
                host.PerformLayout();

                var group = Descendants(page).OfType<GroupBox>().Single();
                var display = group.DisplayRectangle;
                foreach (var radio in Descendants(group).OfType<RadioButton>())
                {
                    // ラジオの座標はパネル基準なので GroupBox 基準へ移す。
                    var bounds = radio.Bounds;
                    bounds.Offset(radio.Parent!.Left, radio.Parent!.Top);
                    Assert.True(
                        display.Contains(bounds),
                        $"{radio.Text} が {bounds} でグループの表示領域 {display} に収まらない"
                    );
                }
            }
        });

    [Fact]
    public void Access_keys_in_the_tab_are_unique() =>
        Sta.Run(() =>
        {
            // 新規の &F / &B が既存(&W &K &T &S)と衝突していないこと。
            var (tab, page) = Build();
            using (tab)
            {
                var keys = Descendants(page)
                    .Select(c => c.Text)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .SelectMany(AccessKeysOf)
                    .ToList();
                Assert.Equal(keys.Count, keys.Distinct().Count());
                Assert.Contains('F', keys);
                Assert.Contains('B', keys);
            }
        });

    private static IEnumerable<char> AccessKeysOf(string text)
    {
        int i = 0;
        while (i + 1 < text.Length)
        {
            if (text[i] != '&')
            {
                i++;
                continue;
            }
            if (text[i + 1] == '&')
            {
                i += 2; // "&&" はリテラルの & (アクセスキーではない)
                continue;
            }
            yield return char.ToUpperInvariant(text[i + 1]);
            i += 2;
        }
    }
}
