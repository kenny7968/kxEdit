using kxEdit.Core.Settings;
using Xunit;

namespace kxEdit.Core.Tests.Settings;

public class AppSettingsTests
{
    [Fact]
    public void Clone_copies_values()
    {
        var s = new AppSettings
        {
            FontName = "テスト",
            FontSize = 14f,
            Theme = "white-on-black",
            DefaultCodePage = 932,
            WrapColumn = 40,
            RecentFiles = new List<string> { @"C:\a.txt", @"C:\b.txt" },
        };
        var c = s.Clone();
        Assert.Equal("テスト", c.FontName);
        Assert.Equal(14f, c.FontSize);
        Assert.Equal("white-on-black", c.Theme);
        Assert.Equal(932, c.DefaultCodePage);
        Assert.Equal(40, c.WrapColumn);
        Assert.Equal(s.RecentFiles, c.RecentFiles);
    }

    [Fact]
    public void Clone_is_independent_of_original()
    {
        var s = new AppSettings { RecentFiles = new List<string> { @"C:\a.txt" } };
        var c = s.Clone();
        c.FontName = "変更後";
        c.RecentFiles.Add(@"C:\b.txt");
        Assert.NotEqual("変更後", s.FontName);
        Assert.Single(s.RecentFiles); // クローン側の追加が元へ波及しない
    }

    [Fact]
    public void New_setting_keys_have_expected_defaults()
    {
        var def = new AppSettings();
        Assert.False(def.CsvAutoModeOnOpen);
        Assert.Equal(4, def.TabWidth);
        Assert.False(def.TabsToSpaces);
        Assert.False(def.ShowLineNumbers);
        Assert.False(def.HighlightCurrentLine);
        Assert.Equal(1, def.CaretWidth);
        Assert.False(def.ShowWhitespace);
        Assert.True(def.BackupEnabled);
        Assert.Equal(300, def.BackupIntervalSeconds); // 30→300 へ変更（設計 2026-07-04）
        Assert.True(def.ConfirmRestoreOnStartup);
    }

    [Fact]
    public void Clone_copies_new_setting_keys()
    {
        var s = new AppSettings
        {
            CsvAutoModeOnOpen = true,
            TabWidth = 8,
            TabsToSpaces = true,
            ShowLineNumbers = true,
            HighlightCurrentLine = true,
            CaretWidth = 3,
            ShowWhitespace = true,
            ConfirmRestoreOnStartup = false,
        };
        var c = s.Clone();
        Assert.True(c.CsvAutoModeOnOpen);
        Assert.Equal(8, c.TabWidth);
        Assert.True(c.TabsToSpaces);
        Assert.True(c.ShowLineNumbers);
        Assert.True(c.HighlightCurrentLine);
        Assert.Equal(3, c.CaretWidth);
        Assert.True(c.ShowWhitespace);
        Assert.False(c.ConfirmRestoreOnStartup);
    }

    [Fact]
    public void Defaults_RestoreOpenFilesOnStartup_IsFalse()
    {
        var s = new AppSettings();
        Assert.False(s.RestoreOpenFilesOnStartup);
    }

    [Fact]
    public void Defaults_LastSession_IsNull()
    {
        var s = new AppSettings();
        Assert.Null(s.LastSession);
    }
}
