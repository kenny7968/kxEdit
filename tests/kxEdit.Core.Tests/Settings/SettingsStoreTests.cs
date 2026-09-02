using System.Linq;
using System.Text;
using System.Text.Json;
using kxEdit.Core.IO;
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
        var s = SettingsStore.Load(path, out _);
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
            var loaded = SettingsStore.Load(path, out _);
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
            var s = SettingsStore.Load(path, out _);
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
            var s = SettingsStore.Load(path, out _);
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
            var loaded = SettingsStore.Load(path, out _);
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
            var s = SettingsStore.Load(path, out _);
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
            var loaded = SettingsStore.Load(path, out _);
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
            var s = SettingsStore.Load(path, out _);
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
            var s = SettingsStore.Load(path, out _);
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
            var s = SettingsStore.Load(path, out _);
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
            var s = SettingsStore.Load(path, out _);
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

            var s = SettingsStore.Load(path, out _);
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
            var s = SettingsStore.Load(path, out _);
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
            var s = SettingsStore.Load(path, out _);
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
            var s = SettingsStore.Load(path, out _);
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
            var s = SettingsStore.Load(path, out _);
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
            var loaded = SettingsStore.Load(path, out _);
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
            var loaded = SettingsStore.Load(path, out _);
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
            var s = SettingsStore.Load(path, out _);
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
            var loaded = SettingsStore.Load(path, out _);
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
            var s = SettingsStore.Load(path, out _);
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

    // ===== M-11 前半: Save を AtomicFile 経由にする(設計 2026-09-02 §5.1 / §6.3) =====

    /// <summary>
    /// 旧 <c>SettingsStore.Save</c> が使っていた <c>JsonSerializerOptions</c> の複製
    /// (private なので同じ内容を組み直す)。CA1869 によりシリアル化ごとの new を避けて保持する。
    /// </summary>
    private static readonly JsonSerializerOptions LegacyOptions = new() { WriteIndented = true };

    /// <summary>日本語・エスケープ対象文字・サロゲートペアを含む設定。バイト列比較用。</summary>
    private static AppSettings JapaneseHeavySettings() =>
        new()
        {
            FontName = "BIZ UDゴシック",
            FontSize = 14.5f,
            WindowWidth = 1234,
            WindowHeight = 777,
            Theme = "高コントラスト",
            // エスケープが要る文字("' < > & \ 制御文字 NBSP 絵文字)を意図的に混ぜる。
            KinsokuHangChars = "、。，．\"'<>&\\\u0007\u00a0\ud83d\ude00",
            RecentFiles = new List<string>
            {
                @"C:\テスト\日本語 <&>'""ファイル.txt",
                @"C:\a\b\タブ\t.txt",
            },
        };

    /// <summary>
    /// M-11: <c>Save</c> を <c>AtomicFile</c> 経由にしてもディスク上のバイト列が変わらないこと
    /// (設計 2026-09-02 §5.1)。<b>旧実装のレシピ</b>
    /// (<c>File.WriteAllText(path, JsonSerializer.Serialize(settings, Options))</c>)を毎回
    /// 実際に走らせて突き合わせる。
    /// <para>
    /// <b>レシピ比較は「<c>Options</c> の変更」と「書き手の変更」を弁別できない。</b>
    /// このテストは <c>SettingsStore.Options</c>(private)には届かず複製 <c>LegacyOptions</c> を
    /// 持つので、<c>Options</c> を変えると<b>左辺だけが動いて赤くなる</b> —— snapshot と同じ
    /// 壊れ方をする(実測: <c>Options</c> に <c>Encoder = UnsafeRelaxedJsonEscaping</c> を足すと
    /// <c>Assert.Equal() Failure: Collections differ</c>)。それでも snapshot を採らない利点は、
    /// 失敗したときに<b>差分の由来が両辺の生成過程から読める</b>ことと、期待値の更新が
    /// 「レシピの再実行」で済むこと。
    /// </para>
    /// <para>
    /// 実測(Task 6): 既定の <c>JavaScriptEncoder</c> が非 ASCII を <c>\uXXXX</c> へ逃がすため、
    /// 出力バイトは<b>全て ASCII</b> になる。つまり「UTF-8 の符号化が食い違う」余地は元から無く、
    /// 実際に危なかったのは <c>File.WriteAllText</c> の既定が BOM を付けるか否かだけだった
    /// (実測: 先頭バイトは <c>0x7B</c> = <c>{</c>・BOM なし)。ASCII のみであること自体は
    /// Options 次第で変わるので assert しない。
    /// </para>
    /// <para>
    /// <b>fixture が兼ねる網 2 本</b>(仕様レビュー指摘 3 / 4)。
    /// <list type="bullet">
    /// <item><b>親ディレクトリが存在しない</b>ところから始める。<c>AtomicFile</c> はディレクトリを
    /// 作らないので、<c>Save</c> の <c>Directory.CreateDirectory</c> を落とすと死ぬ
    /// (落としても全緑だった = 初回起動で <c>%APPDATA%\kxEdit\</c> が無い経路が無網だった)。</item>
    /// <item><b><c>Save</c> を 2 回呼ぶ。</b>1 回目は新規作成(<c>File.Move</c> 枝)、2 回目は
    /// 既存の上書き(<c>File.Replace</c> 枝)。本番の settings.json は通常「既存」なので、
    /// 最も踏まれる枝がここまで無網だった。1 回目を<b>別内容</b>(既定値)で書くので、
    /// 差替が実際に起きていなければバイト列比較が落ちる。</item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact]
    public void Save_writes_the_same_bytes_as_the_previous_writer()
    {
        // 未作成の親ディレクトリから始める(Save 側の Directory.CreateDirectory を網に掛ける)。
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string path = Path.Combine(dir, "settings.json");
        string legacyPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var s = JapaneseHeavySettings();
            SettingsStore.Save(path, new AppSettings()); // 1 回目: 新規作成 = File.Move 枝
            SettingsStore.Save(path, s); // 2 回目: 既存の上書き = File.Replace 枝

            // 旧実装そのもの(string へ直列化 → File.WriteAllText)。
            File.WriteAllText(legacyPath, JsonSerializer.Serialize(s, LegacyOptions));

            byte[] actual = File.ReadAllBytes(path);
            Assert.Equal(File.ReadAllBytes(legacyPath), actual);

            // 相対比較だけでは「両辺そろって BOM が付いた」を捕まえられないので絶対条件も見る。
            Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, actual.Take(3).ToArray());

            // BOM なし UTF-8 として読める(File.ReadAllText の既定デコードと一致する)。
            Assert.Equal(new UTF8Encoding(false).GetString(actual), File.ReadAllText(path));

            // バイト列が同じでも読み戻せなければ意味がない。
            var loaded = SettingsStore.Load(path, out _);
            Assert.Equal(s.FontName, loaded.FontName);
            Assert.Equal(s.Theme, loaded.Theme);
            Assert.Equal(s.KinsokuHangChars, loaded.KinsokuHangChars);
            Assert.Equal(s.RecentFiles, loaded.RecentFiles);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            if (File.Exists(legacyPath))
                File.Delete(legacyPath);
        }
    }

    /// <summary>
    /// M-11: <c>Save</c> が <c>AtomicFile</c> の差替段を通ること。差替が失敗したとき原本は
    /// 無傷で残り、tmp は掃除される(= 旧実装の <c>File.WriteAllText</c> 直書きなら、この時点で
    /// 原本は既に上書きされている)。
    /// </summary>
    [Fact]
    public void Save_goes_through_AtomicFile_and_leaves_the_original_when_the_replace_step_fails()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        const string original = "{\"FontName\":\"元の設定\"}";
        try
        {
            File.WriteAllText(path, original);

            using (
                var scope = AtomicFile.OverrideReplaceStepForTest(
                    (tmp, dest, destExists) =>
                        throw new IOException(
                            $"simulated replace failure: '{dest}' untouched (destExists={destExists}); staged copy is '{tmp}'"
                        )
                )
            )
            {
                Assert.Throws<IOException>(() =>
                    SettingsStore.Save(path, new AppSettings { FontName = "新しい設定" })
                );

                // seam は [ThreadStatic]。張ったスレッドと書込スレッドがずれると黙って既定実装が
                // 走るため、事後状態(原本が残っている)だけでは不発と区別できない
                // —— AtomicFile の xmldoc が「フックを張るテストは必ず Invocations を assert」と
                // 定めている。
                Assert.Equal(1, scope.Invocations);
            }

            Assert.Equal(original, File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // ---- M-11 後半: 読込の 4 状態(設計 2026-09-02 §5.2) ----

    /// <summary>
    /// ファイルが無い = <see cref="SettingsLoadStatus.Missing"/>(初回起動)。**通知しない側**の網。
    /// <para>
    /// 見たいのは status であって設定値ではないので、<b>非既定値を書いてから消す</b>のような
    /// 準備はしない(CLAUDE.md §4-B の no-change 原則は「既定値と区別が付かない観測点を避けろ」
    /// であって、ここでの観測点 <c>status</c> は既定値 <c>Ok</c> ではなく <c>Missing</c> なので
    /// 既定と衝突しない)。併せて <c>Load</c> が副作用を持たない —— 読めなかったからといって
    /// ファイルを作らない —— ことも押さえる(退避は Task 8 の呼出側が明示的に行う。設計 §5.4)。
    /// </para>
    /// </summary>
    [Fact]
    public void Load_reports_Missing_when_the_file_does_not_exist()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        Assert.False(File.Exists(path)); // 前提: 「無い」状態そのもの

        var s = SettingsStore.Load(path, out var status);

        Assert.Equal(SettingsLoadStatus.Missing, status);
        Assert.Equal(new AppSettings().FontName, s.FontName); // 既定で続行する
        Assert.False(File.Exists(path)); // Load は読むだけ(生成も退避もしない)
    }

    /// <summary>
    /// <b>親ディレクトリごと無い</b>初回起動も <see cref="SettingsLoadStatus.Missing"/>。
    /// 本番の <c>%APPDATA%\kxEdit\settings.json</c> は初回にこの形で不在なので、ここが
    /// <c>Unreadable</c> へ倒れると<b>初回起動のたびに警告が出る</b>(Task 8 の通知は
    /// <c>Unreadable</c> でも出る)。
    /// </summary>
    [Fact]
    public void Load_reports_Missing_when_the_parent_directory_does_not_exist()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string path = Path.Combine(dir, "settings.json");
        Assert.False(Directory.Exists(dir)); // 前提: 親ごと無い(初回起動の形)

        SettingsStore.Load(path, out var status);

        Assert.Equal(SettingsLoadStatus.Missing, status);
        Assert.False(Directory.Exists(dir)); // Load はディレクトリも作らない
    }

    /// <summary>
    /// JSON として解釈できない = <see cref="SettingsLoadStatus.Corrupt"/>。
    /// 旧実装はここを <c>Missing</c> / <c>Unreadable</c> と同じ「既定値を返す」に潰していた。
    /// </summary>
    [Fact]
    public void Load_reports_Corrupt_for_unparsable_json()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            const string original = "{ this is not json";
            File.WriteAllText(path, original);

            var s = SettingsStore.Load(path, out var status);

            Assert.Equal(SettingsLoadStatus.Corrupt, status);
            Assert.Equal(new AppSettings().FontName, s.FontName); // 起動は止めず既定で続行
            // 副作用ゼロ(設計 §5.4)。退避=改名は Task 8 の呼出側に明示的に書かせる方針なので、
            // ここで原本を消す/動かすと、その判断ごと Load に密輸されたことになる。
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// 内容が <c>null</c> の 4 文字 —— <b>現状バグの本体</b>(設計 §5.2)。
    /// JSON としては妥当なので <c>Deserialize</c> は例外を投げず <c>null</c> を返し、
    /// 旧実装の <c>?? new AppSettings()</c> がこれを<b>成功扱い</b>にしていた。
    /// 設定が失われている点では破損と同じなので <c>Corrupt</c> へ倒す。
    /// </summary>
    [Fact]
    public void Load_reports_Corrupt_when_the_content_is_the_json_null_literal()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            const string original = "null";
            File.WriteAllText(path, original);

            var s = SettingsStore.Load(path, out var status);

            Assert.Equal(SettingsLoadStatus.Corrupt, status);
            Assert.Equal(new AppSettings().FontName, s.FontName);
            // 副作用ゼロ(設計 §5.4)。ここは Task 8 が退避=改名を足す枝そのものなので、
            // 「Load 側が先に触っていない」ことをこの網で押さえておく。
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// I/O で読めない = <see cref="SettingsLoadStatus.Unreadable"/>。
    /// <b><c>Corrupt</c> に落ちてはいけない</b> —— Task 8 の退避(<c>settings.json</c> →
    /// <c>settings.json.bad</c>)は <c>Corrupt</c> で走るので、ここを取り違えると
    /// <b>中身が正常なファイルを改名する</b>(設計 §5.2)。
    /// <para>
    /// fixture は<b>中身が正常な</b> settings.json を <c>FileShare.None</c> で掴んだままにする
    /// (非既定の <c>FontName</c> を書いておくので、ロックを外した後の再読込で「原本が無傷」
    /// であることまで見える —— 既定値のまま書くと Unreadable 時の戻り値と区別が付かない)。
    /// </para>
    /// </summary>
    [Fact]
    public void Load_reports_Unreadable_when_the_file_is_locked()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            SettingsStore.Save(path, new AppSettings { FontName = "BIZ UDゴシック" });

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var s = SettingsStore.Load(path, out var status);

                Assert.Equal(SettingsLoadStatus.Unreadable, status);
                Assert.Equal(new AppSettings().FontName, s.FontName); // 読めていないので既定で続行
            }

            // ロックが外れれば元の設定へ戻れる = 退避してはいけないファイルだった。
            var reread = SettingsStore.Load(path, out var afterUnlock);
            Assert.Equal(SettingsLoadStatus.Ok, afterUnlock);
            Assert.Equal("BIZ UDゴシック", reread.FontName);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>正常なファイル = <see cref="SettingsLoadStatus.Ok"/>。設定値も読めていること。</summary>
    [Fact]
    public void Load_reports_Ok_and_reads_the_values_for_a_valid_file()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            SettingsStore.Save(
                path,
                new AppSettings { FontName = "BIZ UDゴシック", WindowWidth = 1000 }
            );
            string original = File.ReadAllText(path);

            var s = SettingsStore.Load(path, out var status);

            Assert.Equal(SettingsLoadStatus.Ok, status);
            Assert.Equal("BIZ UDゴシック", s.FontName);
            Assert.Equal(1000, s.WindowWidth);
            // 副作用ゼロ(設計 §5.4)。正常に読めた場合こそ、原本を触る理由が 1 つも無い。
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// <b>妥当だが敵対的な JSON は <c>Ok</c> で通る</b> —— <c>Normalize</c> の防御が例外ではなく
    /// 補正で片付けていることの網。
    /// <para>
    /// これは「<c>Normalize</c> が投げたら <c>Corrupt</c>」を<b>直接には張れなかった</b>ことの
    /// 裏返しでもある(実施記録 §10.13)。<c>Normalize</c> は全枝が null 合体・<c>Math.Max</c>・
    /// <c>Math.Clamp</c>・null 要素 skip で書かれており、<c>Deserialize</c> が返しうるどの
    /// オブジェクトでも投げない —— 例外経路は<b>現在の入力空間から到達できない</b>ので、
    /// 直接の網は <c>Normalize</c> に seam を掘る(仮定のための production 面を増やす)以外に無い。
    /// </para>
    /// <para>
    /// 代わりにここで固定するのは<b>その逆側</b>で、実害があるのはこちらである:
    /// 「補正が要っただけの正常なファイル」を <c>Corrupt</c> へ倒すと、Task 8 の退避が
    /// <b>中身が正常な settings.json を <c>.bad</c> へ改名する</b>。<c>Normalize</c> に
    /// 補正しきれない枝が足された日、この網が先に落ちる。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"RecentFiles\":[null,null]}")]
    [InlineData("{\"LastSession\":null}")]
    [InlineData("{\"LastSession\":{}}")]
    [InlineData("{\"LastSession\":{\"Tabs\":null}}")]
    [InlineData("{\"LastSession\":{\"Tabs\":[null]}}")]
    [InlineData("{\"LastSession\":{\"Tabs\":[{\"Path\":\"   \"}]}}")]
    [InlineData(
        "{\"LastSession\":{\"Tabs\":[{\"Path\":\"a\",\"UntitledNumber\":-2147483648,\"CaretLine\":-1,\"CaretColumn\":-1,\"CodePage\":-1,\"LineEnding\":-1}]}}"
    )]
    [InlineData(
        "{\"Theme\":null,\"FontName\":null,\"KinsokuLineStartChars\":null,\"KinsokuLineEndChars\":null,\"KinsokuHangChars\":null}"
    )]
    [InlineData(
        "{\"WrapColumn\":-2147483648,\"TabWidth\":0,\"CaretWidth\":0,\"FontSize\":-1,\"WindowWidth\":0,\"WindowHeight\":0,\"BackupIntervalSeconds\":0,\"DefaultCodePage\":-1,\"DefaultLineEnding\":-1}"
    )]
    [InlineData("{\"UnknownProperty\":{\"nested\":[1,2,3]}}")]
    public void Load_reports_Ok_for_valid_but_hostile_json(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, json);

            SettingsStore.Load(path, out var status);

            Assert.Equal(SettingsLoadStatus.Ok, status);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// <c>Normalize</c> が値を補正しただけのファイルは <c>Ok</c>。「補正が要った」を「破損」と
    /// 混同すると、Task 8 が<b>正常なファイルを退避</b>してしまう。
    /// <para>
    /// 返り値が<b>補正後</b>であることも併せて見る。ここが殺すのは
    /// <b><c>Normalize</c> の呼出そのものを落とす</b>変異(このファイルの補正系テスト 10 本が落ちる)。
    /// <b>「status を先に確定して補正前を返す実装で落ちる」とは書けない</b>(仕様レビュー指摘 5)——
    /// <c>Normalize</c> は <c>s</c> を in-place で変異させて<b>同じ参照</b>を返すので「補正前を返す」
    /// 実装が書けず、<c>status</c> の確定を前に出す変異も <c>return s</c> にする変異も
    /// <b>等価変異</b>で生存する(レビュアー実測)。
    /// </para>
    /// </summary>
    [Fact]
    public void Load_reports_Ok_when_the_values_only_needed_normalizing()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{\"WrapColumn\":99999,\"RecentFiles\":null,\"Theme\":null}");

            var s = SettingsStore.Load(path, out var status);

            Assert.Equal(SettingsLoadStatus.Ok, status);
            Assert.Equal(1000, s.WrapColumn); // 補正後(上限クランプ)が返っている
            Assert.NotNull(s.RecentFiles);
            Assert.Equal(new AppSettings().Theme, s.Theme);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
