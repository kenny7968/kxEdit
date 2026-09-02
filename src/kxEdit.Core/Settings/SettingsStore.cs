using System.Text.Json;
using kxEdit.Core.Session;
using kxEdit.Core.Text;

namespace kxEdit.Core.Settings;

/// <summary>
/// settings.json の読み書き。壊れていれば既定値で続行するが、<b>なぜ既定値なのか</b>は
/// <see cref="SettingsLoadStatus"/> で呼出側へ返す(退避・通知の判断は呼出側が持つ)。
/// </summary>
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

    /// <summary>
    /// 設定を読む(M-11・設計 2026-09-02 §5.2)。<b>どの状態でも既定値を返して起動は続行する</b>が、
    /// 「ファイルが無い」「壊れている」「読めない」を <paramref name="status"/> で区別する。
    /// 旧実装は 1 つの catch-all でこの 3 つを同じ結果へ潰しており、設定が壊れても無言で
    /// 既定値へ戻り、次の保存で確定していた(= 無音リセット)。
    /// <para>
    /// <b>ここでは判定して返すだけで、ディスクは書き換えない</b>(設計 §5.4)。退避
    /// (<c>settings.json</c> → <c>.bad</c>)と通知は呼出側の担当で、Task 8 / Task 9 で配線する。
    /// </para>
    /// <para>
    /// <b>status を落とせるオーバーロードは置かない</b>(設計 §5.3)。置くと、将来の呼出側が
    /// 黙って破損の信号を捨てられる状態が復活する —— CLAUDE.md §6 / Issue #48 の教訓
    /// 「網に見えるがゲート上は無効」と同型の<b>嘘の安全宣言</b>である。status を見ない呼出は
    /// <c>out _</c> と書くことで、見ていないことがコード上に残る。
    /// </para>
    /// <para>
    /// <b>catch は両方とも catch-all のまま残す。</b>握ってよい例外を前置で列挙するのは
    /// 監査 §9 V-7 の「前置の列挙は原理的に漏れる」に触れる上、ここで例外を素通しすると
    /// <c>Program.Main</c> が壊れる —— 起動時の <c>Load</c> は
    /// <c>Program.CreateMainForm</c>(<c>Program.cs:30</c>)から
    /// <c>SettingsStartup.Prepare</c> 経由で 1 回だけ呼ばれ、そこは
    /// <c>CrashHandler</c> の配線(<c>Program.cs:31</c>)<b>より前</b>である。
    /// <b>そして <c>Application.Run</c>(<c>:61</c>)より前でもある</b> ——
    /// <c>Application.SetUnhandledExceptionMode</c>(<c>:28</c>)は<b>この呼出より手前</b>に
    /// あるが、それが効くのは <c>Application.ThreadException</c>
    /// = メッセージループ内の例外だけなので、<b>ここを抜けた例外はその管轄外</b>である。
    /// つまり抜ければハンドラも記録もダイアログも無いまま起動を落とす。
    /// <c>OutOfMemoryException</c> のような「握ってはいけない例外」も同じ理由でここでは握る。
    /// <para>
    /// <b>順序ではなく「まだ受け皿が無い」が理由である。</b>行番号は動く ——
    /// 実際 §10.14 指摘 2 は「<c>Load</c> は <c>SetUnhandledExceptionMode</c> より前」を
    /// 事実として認定したが、同じブランチの Task 9(<c>CreateMainForm</c> の切り出し)が
    /// 読込を後ろへ移して<b>その順序を偽にした</b>(最終レビュー(品質パス)I-1・§10.21)。
    /// 結論が変わらないのは、依拠しているのが順序ではなく
    /// <b>「<c>Application.Run</c> 前に投げた例外を拾う配線がまだ 1 つも無い」</b>ことだからである。
    /// </para>
    /// </para>
    /// <para>
    /// <b>ただし OOM の落ち先は段によって違う。</b><c>File.ReadAllText</c> 段(try #1)の OOM は
    /// <c>Unreadable</c> = <b>退避しない</b>側へ落ちるので原本を改名する事故にならないが、
    /// <c>Deserialize</c> 段(try #2)の OOM は <c>Corrupt</c> = <b>退避対象</b>へ落ちる。
    /// <c>ReadAllText</c> は約 1GB 未満なら成功するため、<b>読めたがトランスコード/グラフ構築で
    /// 落ちる帯は原理的に存在する</b>(多 GB の fixture が要るので未実測・コード構造からの確定)。
    /// そのサイズの settings.json を「壊れている」扱いにするのは受容できるので分岐は足さないが、
    /// <b>「OOM なら退避しない」は無条件には成立しない</b>。
    /// </para>
    /// <para>
    /// <b>区別しきれないケース</b>: <c>File.Exists</c> は失敗理由を返さないので、
    /// <b>それが <c>false</c> を返す事由すべて</b>が <c>Missing</c>(通知しない)へ落ちる ——
    /// 親ディレクトリの ACL 拒否・パスがディレクトリ・パスが長すぎる・不正なパス文字・空文字列パス
    /// (レビュアー実測の 4 例を含む)。<b>安全側</b>ではある(退避も通知もしないので原本は動かない)が、
    /// ユーザーには何も伝わらない —— ACL で設定を扱えない件は本ブランチ対象外の M-14 の担当で、
    /// ここで <c>ReadAllText</c> の例外種別に判定を移すと、その分類を先取りすることになるので
    /// 設計 §5.2 の形(存在判定 → 読込)のまま残した。
    /// </para>
    /// </summary>
    public static AppSettings Load(string path, out SettingsLoadStatus status)
    {
        string json;
        try
        {
            if (!File.Exists(path))
            {
                status = SettingsLoadStatus.Missing;
                return new AppSettings();
            }
            json = File.ReadAllText(path);
        }
        catch
        {
            // 読めなかっただけ。中身は正常かもしれないので Corrupt と区別する(退避しない)。
            status = SettingsLoadStatus.Unreadable;
            return new AppSettings();
        }

        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (s is null)
            {
                // 内容が "null" の 4 文字。JSON としては妥当だが設定は失われている。
                status = SettingsLoadStatus.Corrupt;
                return new AppSettings();
            }
            var normalized = Normalize(s);
            status = SettingsLoadStatus.Ok;
            return normalized;
        }
        catch
        {
            // パース失敗に加えて Normalize 中の例外もここへ来る。旧実装の catch-all が
            // 持っていた保護(破損 JSON 由来の NRE で起動時クラッシュしない)をそのまま残す。
            status = SettingsLoadStatus.Corrupt;
            return new AppSettings();
        }
    }

    /// <summary>
    /// 壊れた settings.json を <c>&lt;path&gt;.bad</c> へ退避する(M-11・設計 2026-09-02 §5.4)。
    /// <b><see cref="Load"/> には副作用を持たせず、退避は呼出側が明示的に行う。</b>
    /// 既存の <c>.bad</c> は上書きする(最新の破損コピーだけを残す)。
    /// <b>退避できなくても投げない</b> —— 起動を止める理由にはならないので、成否を返して
    /// 呼出側に判断させる。<b><c>.bad</c> の掃除はしない</b>(自動削除すると「壊れた設定を
    /// 後から見る」という退避の目的を自分で潰す。設計 §9)。
    /// <para>
    /// <b><paramref name="quarantinePath"/> は失敗時も「試みた宛先」として返る</b>(実在は
    /// 意味しない)。<c>false</c> のときにこれをユーザーへ案内すると、実在しない場所を
    /// 案内することになる(設計 §10.6 (c) で潰した欠陥と同型)。
    /// </para>
    /// <para>
    /// <b>この API 自体は <see cref="SettingsLoadStatus"/> を見ない。</b>「<c>Corrupt</c> のときだけ
    /// 退避する」は<b>構造的に強制していない</b> —— 位置(<c>SettingsStartup.Prepare</c> の
    /// <c>Corrupt</c> 分岐がソリューション唯一の呼出)と網
    /// (<c>SettingsStartupTests.Prepare_warns_but_never_renames_an_unreadable_file</c>)で保っており、
    /// <b>現状の呼出数では十分</b>と判断した。status を引数に取って構造的に封じる形も設計としては
    /// 成立する(その場合は戻り値を <c>bool</c> ではなく「退避した / 対象外 / 失敗」の 3 値にして、
    /// 対象外と失敗を呼出側が区別できるようにすること)。呼出が増えたら再考する。
    /// </para>
    /// <para>
    /// <b>宛先はどんな <paramref name="path"/> でも同じディレクトリに落ちる</b> ——
    /// 区切り文字を挟まない suffix 連結なので、<c>path</c> が <c>..</c> を含んでいても
    /// 解決先は <c>path</c> と同じ親である。加えて <c>Corrupt</c> は
    /// <c>File.ReadAllText(path)</c> が成功した後にしか出ないので、到達時点の <c>path</c> は
    /// <b>実在する読めるファイル</b>を指していた(末尾が区切り文字・ディレクトリ・予約デバイス名の
    /// パスは <see cref="Load"/> の存在判定か読込で <c>Missing</c> / <c>Unreadable</c> へ落ちる)。
    /// 長すぎるパス(<c>+".bad"</c> で MAX_PATH を越える等)は <c>File.Move</c> が投げて
    /// <c>false</c> になるだけで、原本は動かない。
    /// </para>
    /// <para>
    /// <c>overwrite: true</c> が消しうるのは <c>&lt;path&gt;.bad</c> という<b>決め打ちの 1 名</b>だけで、
    /// 名前は入力から生成されない。その名前がディレクトリだった場合は <c>File.Move</c> が失敗して
    /// <c>false</c> を返す(消さない)。
    /// </para>
    /// <para>
    /// 退避した <c>.bad</c> は <c>%APPDATA%\kxEdit\</c> 直下に残り、どの sweeper の視野にも入らない
    /// (<see cref="Save"/> の tmp と同じ性質・設計 §10.11)。これは仕様である ——
    /// 消すのはユーザーの判断。
    /// </para>
    /// </summary>
    public static bool TryQuarantineCorrupt(string path, out string quarantinePath)
    {
        quarantinePath = path + ".bad";
        try
        {
            File.Move(path, quarantinePath, overwrite: true);
            return true;
        }
        catch
        {
            // 退避の失敗で起動を落とさない(catch-all の理由は Load と同じ。設計 §10.13)。
            return false;
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
        // AtomicFile はディレクトリを作らないので、ここは残す(初回起動 = %APPDATA%\kxEdit\ が
        // 無い状態で保存できなくなる)。この 1 行を落とすと
        // SettingsStoreTests.Save_writes_the_same_bytes_as_the_previous_writer が
        // DirectoryNotFoundException で落ちる —— 網が無い間は落としても全緑だった(§10.12 指摘 3)。
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        IO.AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(settings, Options));
    }
}
