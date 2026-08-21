using System.Linq;
using System.Text;
using kxEdit.Core.Session;
using kxEdit.Core.Settings;
using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Settings;

public class SettingsStoreTests
{
    [Fact]
    public void Missing_file_returns_defaults()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        var s = SettingsStore.Load(path);
        Assert.Equal(new AppSettings().FontName, s.FontName);
    }

    [Fact]
    public void Save_then_load_roundtrips()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var s = new AppSettings
            {
                FontName = "BIZ UDゴシック",
                FontSize = 14,
                WindowWidth = 1000,
            };
            SettingsStore.Save(path, s);
            var loaded = SettingsStore.Load(path);
            Assert.Equal("BIZ UDゴシック", loaded.FontName);
            Assert.Equal(14, loaded.FontSize);
            Assert.Equal(1000, loaded.WindowWidth);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Corrupt_file_returns_defaults()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{ this is not json");
            var s = SettingsStore.Load(path);
            Assert.Equal(new AppSettings().FontSize, s.FontSize);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_normalizes_corrupt_numeric_and_null_fields()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            // 有効な JSON だが値が壊れている（未対応コードページ・範囲外改行・0サイズ・null参照型）。
            File.WriteAllText(
                path,
                "{\"DefaultCodePage\":99999,\"DefaultLineEnding\":7,\"FontSize\":0,\"WindowWidth\":1,\"RecentFiles\":null,\"Theme\":null}"
            );
            var s = SettingsStore.Load(path);
            var def = new AppSettings();
            Assert.Equal(def.DefaultCodePage, s.DefaultCodePage); // 未対応CP→既定
            Assert.Equal(def.DefaultLineEnding, s.DefaultLineEnding); // 範囲外→既定
            Assert.True(s.FontSize > 0); // 0→既定
            Assert.True(s.WindowWidth >= 200); // 極小→既定
            Assert.NotNull(s.RecentFiles); // null→空リスト
            Assert.False(string.IsNullOrEmpty(s.Theme)); // null→default
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Defaults_wrap_is_disabled_with_80_columns()
    {
        var def = new AppSettings();
        Assert.False(def.WrapColumnEnabled);
        Assert.Equal(80, def.WrapColumn);
    }

    [Fact]
    public void Save_then_load_roundtrips_wrap_settings()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var s = new AppSettings { WrapColumnEnabled = true, WrapColumn = 60 };
            SettingsStore.Save(path, s);
            var loaded = SettingsStore.Load(path);
            Assert.True(loaded.WrapColumnEnabled);
            Assert.Equal(60, loaded.WrapColumn);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_clamps_out_of_range_wrap_column()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{\"WrapColumnEnabled\":true,\"WrapColumn\":99999}");
            var s = SettingsStore.Load(path);
            Assert.Equal(1000, s.WrapColumn); // 上限へクランプ
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Defaults_kinsoku_sets_are_conservative_symbols()
    {
        var def = new AppSettings();
        Assert.Contains("、", def.KinsokuLineStartChars);
        Assert.Contains("）", def.KinsokuLineStartChars);
        Assert.DoesNotContain("ー", def.KinsokuLineStartChars); // 長音は既定で入れない
        Assert.Contains("（", def.KinsokuLineEndChars);
        Assert.Equal("、。，．", def.KinsokuHangChars);
    }

    [Fact]
    public void Save_then_load_roundtrips_kinsoku_settings()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var s = new AppSettings
            {
                KinsokuLineStartChars = ")】",
                KinsokuLineEndChars = "(【",
                KinsokuHangChars = "。",
            };
            SettingsStore.Save(path, s);
            var loaded = SettingsStore.Load(path);
            Assert.Equal(")】", loaded.KinsokuLineStartChars);
            Assert.Equal("(【", loaded.KinsokuLineEndChars);
            Assert.Equal("。", loaded.KinsokuHangChars);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_restores_default_kinsoku_when_null()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(
                path,
                "{\"KinsokuLineStartChars\":null,\"KinsokuLineEndChars\":null,\"KinsokuHangChars\":null}"
            );
            var s = SettingsStore.Load(path);
            var def = new AppSettings();
            Assert.Equal(def.KinsokuLineStartChars, s.KinsokuLineStartChars); // null→既定
            Assert.Equal(def.KinsokuLineEndChars, s.KinsokuLineEndChars);
            Assert.Equal(def.KinsokuHangChars, s.KinsokuHangChars);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_preserves_empty_kinsoku_as_disabled()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(
                path,
                "{\"KinsokuLineStartChars\":\"\",\"KinsokuLineEndChars\":\"\",\"KinsokuHangChars\":\"\"}"
            );
            var s = SettingsStore.Load(path);
            Assert.Equal("", s.KinsokuLineStartChars); // 空文字＝そのルール無効。保持する
            Assert.Equal("", s.KinsokuLineEndChars);
            Assert.Equal("", s.KinsokuHangChars);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_normalizes_new_keys_out_of_range()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{\"TabWidth\":0,\"CaretWidth\":99}");
            var s = SettingsStore.Load(path);
            Assert.Equal(4, s.TabWidth); // 範囲外→既定
            Assert.Equal(1, s.CaretWidth); // 範囲外→既定
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_ignores_unknown_removed_keys()
    {
        // P7 撤去: PreferredScreenReader は削除済み。settings.json に残っていても
        // System.Text.Json の既定挙動で未知プロパティは無視され起動失敗しない（前方互換）。
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{\"PreferredScreenReader\":\"pctalker\",\"TabWidth\":8}");
            var s = SettingsStore.Load(path);
            Assert.Equal(8, s.TabWidth); // 既知キーは通常通り反映
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    // CSV-L-4: 攻撃 settings.json に 10 万件の RecentFiles を仕込まれても Load の後段
    // (RecentFilesList.Add / メニュー再構築 / 各所の走査)は O(MaxItems) を維持する。
    // Deserialize 自体は System.Text.Json の仕様で O(N)(緩和不能)。Normalize 段階で Truncate してそれ以降を封じる。
    [Fact]
    public void Load_truncates_recent_files_over_max_items()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            // 10 万件の "C:\\a0.txt".."C:\\a99999.txt" を持つ JSON を組み立てる(直書きで
            // JsonSerializer.Serialize のセットアップコストを避けつつ、Load の防御を素直に検証する)。
            var sb = new StringBuilder();
            sb.Append("{\"RecentFiles\":[");
            for (int i = 0; i < 100_000; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append("\"C:\\\\a").Append(i).Append(".txt\"");
            }
            sb.Append("]}");
            File.WriteAllText(path, sb.ToString());

            var s = SettingsStore.Load(path);
            Assert.NotNull(s.RecentFiles);
            Assert.Equal(RecentFilesList.MaxItems, s.RecentFiles!.Count);
            Assert.Equal(@"C:\a0.txt", s.RecentFiles[0]);
            Assert.Equal($@"C:\a{RecentFilesList.MaxItems - 1}.txt", s.RecentFiles[^1]);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_preserves_valid_new_keys()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(
                path,
                "{\"TabWidth\":8,\"CaretWidth\":5,"
                    + "\"CsvAutoModeOnOpen\":true,\"TabsToSpaces\":true,\"ShowLineNumbers\":true,"
                    + "\"HighlightCurrentLine\":true,\"ShowWhitespace\":true,\"ConfirmRestoreOnStartup\":false}"
            );
            var s = SettingsStore.Load(path);
            Assert.Equal(8, s.TabWidth);
            Assert.Equal(5, s.CaretWidth);
            Assert.True(s.CsvAutoModeOnOpen);
            Assert.True(s.TabsToSpaces);
            Assert.True(s.ShowLineNumbers);
            Assert.True(s.HighlightCurrentLine);
            Assert.True(s.ShowWhitespace);
            Assert.False(s.ConfirmRestoreOnStartup);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_Normalizes_LastSession_Skips_BlankPath_And_Clamps_NegativeNumbers()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            // Tabs=[有効パス, 空白パス(=skip), 無題+負値カレット(=clamp), 無題+負連番(=clamp)]
            File.WriteAllText(
                path,
                "{\"LastSession\":{\"Tabs\":["
                    + "{\"Path\":\"C:\\\\a.txt\",\"UntitledNumber\":0,\"BufferKey\":null,\"IsActive\":true,\"CaretLine\":10,\"CaretColumn\":5},"
                    + "{\"Path\":\"   \",\"UntitledNumber\":0,\"BufferKey\":null,\"IsActive\":false,\"CaretLine\":0,\"CaretColumn\":0},"
                    + "{\"Path\":null,\"UntitledNumber\":1,\"BufferKey\":\"k1\",\"IsActive\":false,\"CaretLine\":-1,\"CaretColumn\":-5},"
                    + "{\"Path\":null,\"UntitledNumber\":-3,\"BufferKey\":\"k2\",\"IsActive\":false,\"CaretLine\":0,\"CaretColumn\":0}"
                    + "]}}"
            );
            var s = SettingsStore.Load(path);
            Assert.NotNull(s.LastSession);
            Assert.Equal(3, s.LastSession!.Tabs.Count); // 空白 Path はスキップ
            Assert.Equal(@"C:\a.txt", s.LastSession.Tabs[0].Path);
            Assert.Null(s.LastSession.Tabs[1].Path);
            Assert.Equal(0, s.LastSession.Tabs[1].CaretLine); // 負値→0
            Assert.Equal(0, s.LastSession.Tabs[1].CaretColumn); // 負値→0
            Assert.Equal(0, s.LastSession.Tabs[2].UntitledNumber); // 負値→0
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_LastSession_TabsWithNullElement_SkipsAndKeepsOtherSettings()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            // 攻撃/破損: Tabs に null 要素が混入
            File.WriteAllText(
                path,
                "{\"FontName\":\"TestFont\",\"LastSession\":{\"Tabs\":["
                    + "null,"
                    + "{\"Path\":\"C:\\\\a.txt\",\"UntitledNumber\":0,\"BufferKey\":null,\"IsActive\":true,\"CaretLine\":0,\"CaretColumn\":0}"
                    + "]}}"
            );
            var s = SettingsStore.Load(path);
            // 全設定既定リセットが起きていない=FontName が保持されている
            Assert.Equal("TestFont", s.FontName);
            // null 要素は skip され、有効な 1 件だけ残る
            Assert.NotNull(s.LastSession);
            Assert.Single(s.LastSession!.Tabs);
            Assert.Equal(@"C:\a.txt", s.LastSession.Tabs[0].Path);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_LastSession_NullTabs_BecomesEmptyList()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{\"LastSession\":{\"Tabs\":null}}");
            var s = SettingsStore.Load(path);
            Assert.NotNull(s.LastSession);
            Assert.Empty(s.LastSession!.Tabs);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Roundtrip_RestoreEnabled_WithNullLastSession()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var s = new AppSettings
            {
                RestoreOpenFilesOnStartup = true,
                LastSession = null, // opt-in 済み・初回終了前の中間状態
            };
            SettingsStore.Save(path, s);
            var loaded = SettingsStore.Load(path);
            Assert.True(loaded.RestoreOpenFilesOnStartup);
            Assert.Null(loaded.LastSession);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Roundtrip_LastSession_And_RestoreFlag()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var s = new AppSettings
            {
                RestoreOpenFilesOnStartup = true,
                LastSession = new LastSessionSnapshot(
                    new List<SessionTabRecord>
                    {
                        new(
                            Path: @"C:\a.txt",
                            UntitledNumber: 0,
                            BufferKey: null,
                            IsActive: true,
                            CaretLine: 3,
                            CaretColumn: 7
                        ),
                        new(
                            Path: null,
                            UntitledNumber: 2,
                            BufferKey: "abc",
                            IsActive: false,
                            CaretLine: 0,
                            CaretColumn: 0
                        ),
                    }
                ),
            };
            SettingsStore.Save(path, s);
            var loaded = SettingsStore.Load(path);
            Assert.True(loaded.RestoreOpenFilesOnStartup);
            Assert.NotNull(loaded.LastSession);
            Assert.Equal(2, loaded.LastSession!.Tabs.Count);
            Assert.Equal(@"C:\a.txt", loaded.LastSession.Tabs[0].Path);
            Assert.True(loaded.LastSession.Tabs[0].IsActive);
            Assert.Equal(3, loaded.LastSession.Tabs[0].CaretLine);
            Assert.Equal(7, loaded.LastSession.Tabs[0].CaretColumn);
            Assert.Equal("abc", loaded.LastSession.Tabs[1].BufferKey);
            Assert.Equal(2, loaded.LastSession.Tabs[1].UntitledNumber);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_Normalizes_LastSession_Clamps_NegativeCodePageAndLineEnding()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(
                path,
                "{\"LastSession\":{\"Tabs\":["
                    + "{\"Path\":\"C:\\\\a.txt\",\"UntitledNumber\":0,\"BufferKey\":\"k1\","
                    + "\"IsActive\":true,\"CaretLine\":0,\"CaretColumn\":0,"
                    + "\"CodePage\":-5,\"HasBom\":true,\"LineEnding\":-1,\"WasModified\":true}"
                    + "]}}"
            );
            var s = SettingsStore.Load(path);
            Assert.NotNull(s.LastSession);
            var r = Assert.Single(s.LastSession!.Tabs);
            Assert.Equal(0, r.CodePage);
            Assert.Equal(0, r.LineEnding);
            Assert.True(r.HasBom);
            Assert.True(r.WasModified);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Roundtrip_LastSession_WithEncodingAndModifiedFields()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var s = new AppSettings
            {
                RestoreOpenFilesOnStartup = true,
                LastSession = new LastSessionSnapshot(
                    new List<SessionTabRecord>
                    {
                        new(
                            Path: @"C:\a.txt",
                            UntitledNumber: 0,
                            BufferKey: "k1",
                            IsActive: true,
                            CaretLine: 3,
                            CaretColumn: 7,
                            CodePage: 65001,
                            HasBom: true,
                            LineEnding: 1,
                            WasModified: true
                        ),
                    }
                ),
            };
            SettingsStore.Save(path, s);
            var loaded = SettingsStore.Load(path);
            var r = loaded.LastSession!.Tabs[0];
            Assert.Equal(65001, r.CodePage);
            Assert.True(r.HasBom);
            Assert.Equal(1, r.LineEnding);
            Assert.True(r.WasModified);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_LegacyLastSession_WithoutNewFields_UsesDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(
                path,
                "{\"LastSession\":{\"Tabs\":["
                    + "{\"Path\":\"C:\\\\a.txt\",\"UntitledNumber\":0,\"BufferKey\":null,"
                    + "\"IsActive\":true,\"CaretLine\":0,\"CaretColumn\":0}"
                    + "]}}"
            );
            var s = SettingsStore.Load(path);
            var r = s.LastSession!.Tabs[0];
            Assert.Equal(0, r.CodePage);
            Assert.False(r.HasBom);
            Assert.Equal(0, r.LineEnding);
            Assert.False(r.WasModified);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
