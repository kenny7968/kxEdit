using System.Text.Json;
using kxEdit.Core.Session;
using kxEdit.Core.Text;

namespace kxEdit.Core.Settings;

/// <summary>settings.json の読み書き。壊れていれば既定値で続行（握り潰さず既定へ）。</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>既定の設定ファイルパス（%APPDATA%\kxEdit\settings.json）。</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "kxEdit",
            "settings.json"
        );

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new AppSettings();
            string json = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            return Normalize(s);
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// JSON で明示的に null が入った参照型フィールドを既定へ補正する（"RecentFiles": null 等でも
    /// 後段の NRE を起こさないため）。System.Text.Json は欠落キーは初期化子を残すが、明示 null は上書きする。
    /// </summary>
    private static AppSettings Normalize(AppSettings s)
    {
        var def = new AppSettings();
        s.RecentFiles ??= def.RecentFiles;
        // CSV-L-4: 攻撃 settings.json が 10 万件級の RecentFiles を持っていても、ここで
        // MaxItems (=10) にキャップし後段(メニュー再構築・RecentFilesList.Add の PathKey 走査)を
        // O(MaxItems) に押し込める。Truncate は null 耐性を持つため上の null 補正順序と独立に安全。
        s.RecentFiles = RecentFilesList.Truncate(s.RecentFiles);
        if (string.IsNullOrEmpty(s.Theme))
            s.Theme = def.Theme;
        if (string.IsNullOrEmpty(s.FontName))
            s.FontName = def.FontName;

        // 禁則文字セットは明示 null のみ既定へ補正する。空文字 "" は「そのルール無効」の意図なので保持する。
        if (s.KinsokuLineStartChars is null)
            s.KinsokuLineStartChars = def.KinsokuLineStartChars;
        if (s.KinsokuLineEndChars is null)
            s.KinsokuLineEndChars = def.KinsokuLineEndChars;
        if (s.KinsokuHangChars is null)
            s.KinsokuHangChars = def.KinsokuHangChars;

        // 数値の健全化（手編集等で壊れた設定が起動時クラッシュ／不可視を招かないように）。
        if (!IsSelectableCodePage(s.DefaultCodePage))
            s.DefaultCodePage = def.DefaultCodePage;
        if (s.DefaultLineEnding is < 0 or > 2)
            s.DefaultLineEnding = def.DefaultLineEnding;
        if (s.FontSize <= 0f)
            s.FontSize = def.FontSize;
        if (s.WindowWidth < 200)
            s.WindowWidth = def.WindowWidth;
        if (s.WindowHeight < 150)
            s.WindowHeight = def.WindowHeight;
        if (s.BackupIntervalSeconds < 5)
            s.BackupIntervalSeconds = def.BackupIntervalSeconds;
        if (s.TabWidth is < 1 or > 16)
            s.TabWidth = def.TabWidth;
        if (s.CaretWidth is < 1 or > 5)
            s.CaretWidth = def.CaretWidth;
        s.WrapColumn = WrapGeometry.ClampColumns(s.WrapColumn); // 範囲外/破損値を 10〜1000 へ
        NormalizeLastSession(s);
        return s;
    }

    /// <summary>
    /// LastSession の防御的補正。
    /// - Tabs が null → 空リスト
    /// - Path が IsNullOrWhiteSpace → その SessionTabRecord を skip(復元経路で空タブ追加を避ける)
    /// - UntitledNumber&lt;0 / CaretLine&lt;0 / CaretColumn&lt;0 → 0 に clamp
    /// - CodePage&lt;0 / LineEnding&lt;0 → 0 に clamp(§8 追加フィールド; 0=未指定として復元側で fallback)
    /// 設計書 §2.3 / §8。
    /// </summary>
    private static void NormalizeLastSession(AppSettings s)
    {
        if (s.LastSession is null)
            return;
        if (s.LastSession.Tabs is null)
        {
            s.LastSession = new LastSessionSnapshot(new List<SessionTabRecord>());
            return;
        }
        var cleaned = new List<SessionTabRecord>(s.LastSession.Tabs.Count);
        foreach (var t in s.LastSession.Tabs)
        {
            if (t is null)
                continue; // I-3: 攻撃/破損 JSON 由来の null 要素で NRE→全設定既定リセットを防ぐ
            // Path があるが空白のみ=不完全レコード → skip
            if (t.Path is not null && string.IsNullOrWhiteSpace(t.Path))
                continue;
            cleaned.Add(
                t with
                {
                    UntitledNumber = Math.Max(0, t.UntitledNumber),
                    CaretLine = Math.Max(0, t.CaretLine),
                    CaretColumn = Math.Max(0, t.CaretColumn),
                    CodePage = Math.Max(0, t.CodePage),
                    LineEnding = Math.Max(0, t.LineEnding),
                }
            );
        }
        s.LastSession = new LastSessionSnapshot(cleaned);
    }

    private static bool IsSelectableCodePage(int codePage)
    {
        foreach (var e in EncodingCatalog.SelectableEncodings)
            if (e.CodePage == codePage)
                return true;
        return false;
    }

    public static void Save(string path, AppSettings settings)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string json = JsonSerializer.Serialize(settings, Options);
        File.WriteAllText(path, json);
    }
}
