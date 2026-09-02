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

    /// <summary>
    /// 設定を原子的に書き込む(M-11・設計 2026-09-02 §5.1)。<c>File.WriteAllText</c> の直書きから
    /// <see cref="IO.AtomicFile"/> 経由へ移した。直書きは書込中の失敗(ディスクフル・電源断)で
    /// <b>原本を切り詰めた状態で残す</b>——設定は全永続化の中で唯一この防衛線の外にいた。
    /// <para>
    /// <b>ディスク上のバイト列は変わらない。</b> エスケープと整形は <c>Options</c> が決めており、
    /// 書き手(<c>File.WriteAllText</c> か <c>SerializeToUtf8Bytes</c> か)は関与しない。どちらも
    /// BOM なし UTF-8。<c>SettingsStoreTests.Save_writes_the_same_bytes_as_the_previous_writer</c>
    /// が旧実装のレシピを毎回走らせて突き合わせている。
    /// </para>
    /// <para>
    /// <b>差替に失敗して残した tmp は恒久残留する</b>(実測・設計 §10.5 / §10.11)。
    /// <c>*.tmp</c> を掃除するコードは <c>BackupStore.SweepTempFiles</c> しか無く、起動時に走る
    /// <c>BackupCoordinator</c> の 2 呼出はどちらも <c>%APPDATA%\kxEdit\backups</c> と
    /// その配下の <c>session-*</c> の<b>各 1 階層だけ</b>を見る(再帰なし)。
    /// <see cref="DefaultPath"/> は <c>%APPDATA%\kxEdit\settings.json</c> なので、その tmp は
    /// <c>%APPDATA%\kxEdit\</c> <b>直下</b>に落ち、どの sweeper の視野にも入らない
    /// (<c>session-state.json</c> と同じ性質)。「静かに消える」ではなく<b>残留する</b>。
    /// 中身は設定で、<b>最近使ったファイルの一覧(パス)を含む</b>——本文は含まない。
    /// </para>
    /// <para>
    /// <b>ここでの保証はユーザーへ届かない。</b> 唯一の呼出側 <c>MainForm.SaveSettingsSafe</c> が
    /// 例外を握り潰すため、<c>AtomicReplaceFailedException.PreservedTempPath</c>(= 残した tmp の
    /// 場所)は誰にも伝わらない。届くのは<b>原本を壊さない</b>ところまでで、「退避先を案内する」
    /// M-12 の回収は文書保存経路(<c>TextFileService.Save</c>)にしか効いていない
    /// (<c>BackupStore.Write</c> / <c>SessionLayoutStore.Save</c> と同じ形)。
    /// 握り潰しの解消は B5(M-22)の担当で、本修正の射程外。
    /// </para>
    /// </summary>
    public static void Save(string path, AppSettings settings)
    {
        // AtomicFile はディレクトリを作らないので、ここは残す。
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        IO.AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(settings, Options));
    }
}
