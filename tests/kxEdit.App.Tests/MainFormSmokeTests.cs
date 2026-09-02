using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using kxEdit.App.Tests.Fakes;
using kxEdit.Core.Backup;
using kxEdit.Core.Search;
using kxEdit.Core.Session;
using kxEdit.Core.Settings;
using Directory = System.IO.Directory;
using File2 = System.IO.File;
using IOException = System.IO.IOException;

namespace kxEdit.App.Tests;

/// <summary>
/// Task 1-8: コンポジションルート(MainForm)結線のスモーク。
/// 中間区間=AutoEnterCsvMode の 2 ガード(設定 ON/拡張子)+OpenAndSelect の
/// suppressAutoCsv 配線を、実 MainForm を可視状態まで作って観測する。
/// FileController 個別の挙動(ロールバック等)は FileControllerTests で担保済み=再検証しない。
/// 責務: MainForm↔FileController の配線が生きているか(=AutoEnterCsvMode を通す/通さない)
/// の 4 分岐だけを固定する。
/// 前提: public <see cref="MainForm(AppSettings)"/> 経路は internal <see cref="MainForm(AppSettings, string)"/>
/// へチェーンする=このスモークでは internal ctor 経路のみ検証(public ctor 空化変異は
/// Release=warnaserror の CS8618 か Program.cs 起動時クラッシュが拾うため対象外)。
/// </summary>
public class MainFormSmokeTests
{
    /// <summary>テスト毎に使い捨てる一時フォルダ(settings.json とテスト対象ファイルの隔離先)。</summary>
    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("kxEditAppSmoke_").FullName;

        public string File(string name) => Path.Combine(Root, name);

        /// <summary>SaveSettingsSafe の書込先(実 %APPDATA% を汚さないための隔離パス)。実際に書かれても構わない。</summary>
        public string SettingsPath => Path.Combine(Root, "settings.json");

        /// <summary>hot exit 統合(設計 2026-07-23 統合)テスト用の隔離先。実 %APPDATA% の
        /// backups / session-state.json / last-session-buffers.json を絶対に触らない。</summary>
        public string BackupDir => Path.Combine(Root, "backups");

        public string LayoutPath => Path.Combine(Root, "session-state.json");

        public string BuffersPath => Path.Combine(Root, "last-session-buffers.json");

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { /* 掃除失敗はテスト失敗にしない(バックアップ Timer 等の掴みが残る可能性) */
            }
        }
    }

    /// <summary>
    /// スモーク用の既定設定。BackupEnabled=false により OnShown 内の
    /// <see cref="BackupCoordinator.OfferRestoreOnStartup"/> が先頭の !_enabled ガードで no-op となり、
    /// 実バックアップ処理(SweepTempFiles/LoadAll)が走らないため、テストが実 backup ディレクトリを触らない。
    /// </summary>
    private static AppSettings NewSettings(bool csvAutoModeOnOpen) =>
        new() { BackupEnabled = false, CsvAutoModeOnOpen = csvAutoModeOnOpen };

    /// <summary>
    /// MainForm を可視状態まで作る(OnShown は BeginInvoke 経由のため non-pumping STA では
    /// 発火しない=必要なら <see cref="ShowMainForm_Unified"/> を使う)。MainForm は sealed のため
    /// <see cref="Form.ShowWithoutActivation"/> を注入できない=Show() が一時的にアクティブ化するが、
    /// xUnit の並列実行は <see cref="Xunit.CollectionBehaviorAttribute"/> で無効化済(GlobalUsings.cs)
    /// のため実害なし。StartPosition/Location/ShowInTaskbar は Show() 前に上書きして
    /// 画面外(-32000,-32000)配置=デスクトップ上のチラつきを最小化する
    /// (ctor 内で StartPosition=CenterScreen が指定されているが、Show() 時に評価されるため上書きが効く)。
    /// Task 7 テスト衛生: 2 引数 ctor は backups / session-state.json / last-session-buffers.json が
    /// 実 %APPDATA% に落ち、close 時の Shutdown(keepForRestore:false) 等が開発機の実ファイルを
    /// 消してしまうため、全テスト共通で 4 引数 ctor+seam により TempDir へ隔離する。
    /// </summary>
    private static MainForm ShowMainForm(
        AppSettings settings,
        TempDir tmp,
        string? settingsWarning = null
    )
    {
        var form = new MainForm(
            settings,
            tmp.SettingsPath,
            backupDirectory: tmp.BackupDir,
            sessionLayoutPath: tmp.LayoutPath,
            settingsWarning: settingsWarning
        );
        form.SetLastSessionBuffersPathForTest(tmp.BuffersPath);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Show();
        return form;
    }

    /// <summary>
    /// OnShown は <see cref="Control.BeginInvoke(Delegate)"/> でキューされるため、
    /// <see cref="Sta"/> の non-pumping STA スレッドでは Show() 単独では走らない。
    /// Task 7 テストは OnShown を明示的に動かす必要があるため
    /// <see cref="Application.DoEvents"/> でメッセージを 1 サイクルだけ処理する。
    /// </summary>
    private static void PumpUntilShown()
    {
        // Application.DoEvents を数回回して、CallShownEvent(BeginInvoke)+続く再入 (OnActivated 内の
        // BeginInvoke など) をすべて処理する。回数は安全側の 4 サイクル(実測 1〜2 で足りる)。
        for (int i = 0; i < 4; i++)
        {
            Application.DoEvents();
        }
    }

    /// <summary>
    /// hot exit 統合(設計 2026-07-23 統合 §3.2/§3.3)テスト用: <see cref="ShowMainForm"/> の
    /// TempDir 隔離に加えて OnShown(ON なら RestoreUnifiedSession・OFF なら従来提案)を
    /// pump で発火させる。失敗パスの集約 Warn は既定で抑止
    /// (MessageBox がテストをブロックしないように。実運用経路では出る)。
    /// </summary>
    private static MainForm ShowMainForm_Unified(
        AppSettings settings,
        TempDir tmp,
        string? settingsWarning = null
    )
    {
        var form = ShowMainForm(settings, tmp, settingsWarning);
        form.SetSuppressRestoreDialogsForTest(true);
        PumpUntilShown();
        return form;
    }

    /// <summary>前回セッション相当の session-state.json を TempDir に植える(統合復元の入力)。</summary>
    private static void PlantLayout(TempDir tmp, params SessionLayoutRecord[] tabs) =>
        SessionLayoutStore.Save(
            tmp.LayoutPath,
            new SessionLayout(new List<SessionLayoutRecord>(tabs), DateTime.UtcNow)
        );

    /// <summary>前回セッション相当のバックアップを TempDir の base dir 直下(flat 後方互換配置)へ植える。</summary>
    private static void PlantBackup(TempDir tmp, BackupRecord rec) =>
        BackupStore.Write(tmp.BackupDir, rec);

    private static BackupRecord Rec(string id, string? path, int untitledNumber, string content) =>
        new(
            Id: id,
            OriginalPath: path,
            UntitledNumber: untitledNumber,
            CodePage: 65001,
            HasBom: false,
            LineEndingId: 0,
            Content: content,
            TimestampUtc: DateTime.UtcNow
        );

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>タブ列 TabControl 上でアクティブ(選択中)のタブか。</summary>
    private static bool IsActiveTab(Document doc) =>
        ReferenceEquals(((TabControl)doc.Page.Parent!).SelectedTab, doc.Page);

    /// <summary>
    /// レガシー(PR #22)形式の buffers.json を植える(移行復元の入力)。ストア側の Save は
    /// Task 7 で退役済みのため、旧 Save 出力と互換の生 JSON(string→string マップ)を直接書く。
    /// </summary>
    private static void PlantLegacyBuffers(TempDir tmp, Dictionary<string, string> map) =>
        File2.WriteAllText(tmp.BuffersPath, System.Text.Json.JsonSerializer.Serialize(map));

    // ===== AutoEnterCsvMode: 2 ガード(設定 ON/拡張子)の kill =====

    [Fact]
    public void AutoCsv_On_OpensCsvIntoCsvMode() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            // 拡張子は大文字 DATA.CSV: MainForm.AutoEnterCsvMode の拡張子ガード(MainForm.cs:250-257)の
            // StringComparison.OrdinalIgnoreCase(:254)が
            // Ordinal に変異したら小文字 .csv と不一致=AutoEnterCsvMode を素通り=CsvMode=false で赤化する。
            string path = tmp.File("DATA.CSV");
            File2.WriteAllText(path, "a,b\n1,2");
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: true), tmp);

            var doc = form.FileForTest.TryOpenOrActivate(path);

            Assert.NotNull(doc);
            Assert.True(doc!.State.CsvMode); // .csv 判定+自動 CSV モード配線の kill(=結線が生きている)
        });

    [Fact]
    public void AutoCsv_SettingOff_StaysNormalMode() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string path = tmp.File("data.csv");
            File2.WriteAllText(path, "a,b\n1,2");
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);

            var doc = form.FileForTest.TryOpenOrActivate(path);

            Assert.NotNull(doc);
            // MainForm.AutoEnterCsvMode の設定 ON ガード
            // (MainForm.cs:248-249 の !_settings.CsvAutoModeOnOpen return)を削除する変異を kill:
            // 削除されると .csv 判定を通り抜けて CsvMode=true になり本 assertion が赤化する。
            Assert.False(doc!.State.CsvMode);
        });

    [Fact]
    public void AutoCsv_NonCsvExtension_StaysNormalMode() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string path = tmp.File("data.txt"); // .csv でない
            File2.WriteAllText(path, "a,b\n1,2");
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: true), tmp);

            var doc = form.FileForTest.TryOpenOrActivate(path);

            Assert.NotNull(doc);
            // MainForm.AutoEnterCsvMode の拡張子ガード
            // (MainForm.cs:250-257 の .csv 判定 return)を削除する変異を kill:
            // 削除されると拡張子に関わらず TryEnterMode を呼び CsvMode=true になり本 assertion が赤化する。
            Assert.False(doc!.State.CsvMode);
        });

    /// <summary>
    /// grep が返すのと同じ形のヒットを組み立てる(A-18 のテスト用)。
    /// <paramref name="absoluteOffset"/> の既定は L1 の <c>GrepJumpResolverTests.Hit</c> と同じ流儀で
    /// わざと「ありえない値」にする: ジャンプ経路が <c>AbsoluteOffset</c> を読んでいれば必ず破綻する。
    /// 実値を渡すのは陽性対照が要るテスト(dirty)だけ。
    /// </summary>
    private static GrepHit GrepHitFor(
        string path,
        int lineNumber,
        string lineText,
        int matchStart,
        int matchLength,
        int absoluteOffset = int.MaxValue
    ) =>
        new(
            FilePath: path,
            LineNumber: lineNumber,
            Column: matchStart + 1,
            LineText: lineText,
            MatchStartInLine: matchStart,
            MatchLength: matchLength,
            AbsoluteOffset: absoluteOffset
        );

    // ===== OpenAndSelect: suppressAutoCsv 配線+選択レンジ =====

    [Fact]
    public void OpenAndSelect_OpensSelectsAndSuppressesAutoCsv() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string path = tmp.File("data.csv"); // 拡張子だけでは判定できない=auto 設定 ON でも CSV へ入らないことを固定する
            File2.WriteAllText(path, "a,b,c\n1,2,3");
            // auto ON のまま OpenAndSelect: suppressAutoCsv=true が抜けると AutoEnterCsvMode が発火して赤化する
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: true), tmp);

            form.OpenAndSelect(GrepHitFor(path, 1, "a,b,c", 2, 3));

            // OpenAndSelect 後の Active タブを取り戻す: 既に開いているため FileController.TryOpenOrActivate は
            // 既存タブ再利用の fast path(FindByPath ヒット)を通り _openedFresh を呼ばない=
            // 観測対象(CsvMode/Path/選択レンジ)への副作用はない(内部で RegisterRecent →
            // recentChanged/saveSettings は走るが観測外・実 %APPDATA% は tmp 隔離で汚染ゼロ)。
            var doc = form.FileForTest.TryOpenOrActivate(path);
            Assert.NotNull(doc);

            // MainForm.OpenAndSelect(MainForm.cs:1102)の suppressAutoCsv: true → false 変異を kill:
            // false だと _openedFresh 経路で AutoEnterCsvMode を通し CsvMode=true 化=本 assertion が赤化する。
            Assert.False(doc!.State.CsvMode);
            Assert.Equal(path, doc.State.Path);
            // SelectCharRange(2, 3): start=2 / end=2+3=5
            // (EditorControl.SelectCharRange=EditorControl.Caret.cs:32-37 のエイリアス経由)
            Assert.Equal((2, 5), doc.Editor.GetSelectionCharRange());
        });

    // A-3(2026-08-22): grep 結果からのジャンプで画面が追従することの固定。
    [Fact]
    public void OpenAndSelect_ScrollsTargetIntoView() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string path = tmp.File("many-lines.txt");
            var lines = Enumerable.Range(0, 200).Select(i => $"line{i}").ToArray();
            File2.WriteAllText(path, string.Join("\r\n", lines));
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);

            form.OpenAndSelect(GrepHitFor(path, 200, "line199", 0, 4));

            var doc = form.FileForTest.TryOpenOrActivate(path);
            Assert.NotNull(doc);
            int visibleRows = Math.Max(
                1,
                doc!.Editor.ClientSize.Height / Math.Max(1, doc.Editor.LineHeightPx)
            );
            int hitLine = doc.Editor.CurrentLine;
            // 行番号だけでは不足する: 選択が行 199 のどこを指していても CurrentLine==199 は
            // 成立してしまう。選択開始が行頭(桁 0)であることまで見て、ヒットの
            // MatchStartInLine=0 が「行頭からの桁」として尊重されていることを固定する。
            // A-18(2026-08-31)でこの網の意味は変わった: 移植前は生オフセットの
            // +2(直前の CRLF 分)落ちを捕まえる網だったが、現 OpenAndSelect は resolver 経由で
            // AbsoluteOffset を読まない(=このヒットの AbsoluteOffset は既定の毒値)ため、
            // その失敗モードは到達不能になっている。毒値を読む退行はこの桁 0 が捕まえる。
            Assert.Equal(199, hitLine);
            Assert.Equal(0, doc.Editor.GetColumn(doc.Editor.GetSelectionCharRange().Start));
            Assert.True(doc.Editor.TopLine > 0, $"expected TopLine > 0, got {doc.Editor.TopLine}");
            Assert.InRange(hitLine, doc.Editor.TopLine, doc.Editor.TopLine + visibleRows - 1);
        });

    /// <summary>
    /// 設計書 §3.3(A-3 同型): SetSelectionCharRange は Anchor/Caret 無変化で早期 return する
    /// (EditorControl.Caret.cs:209)ため、同じヒットへ再ジャンプするとスクロールが追従しない。
    /// ホイールでのスクロール退避を TopLine の代入で再現する(キャレットは動かない)。
    /// <para>
    /// <b>本テストは <c>MainForm.OpenAndSelect</c> の <c>BringCaretIntoView()</c> 明示呼び出し
    /// (設計書 §3.3 の belt)を守る唯一の網である。</b> ミューテーション検証で実測済み
    /// (2026-08-31・設計書 §5.3 変異 #15): その 1 行を削除すると赤化するのは本テストだけで、
    /// 他の <c>OpenAndSelect_*</c> 4 件は緑のまま通る(初回ジャンプは
    /// <c>SetCaretCharOffset</c> 側の追従だけで可視域に入るため)。
    /// したがって<b>「同じヒットへ 2 回ジャンプする」という筋書きを崩すリファクタは、
    /// belt を静かに無網化する</b>。1 回目のジャンプ・<c>TopLine = 0</c> による退避・
    /// 2 回目の同一ヒットでのジャンプ、の 3 点は本テストの本質なので削らないこと。
    /// </para>
    /// </summary>
    [Fact]
    public void OpenAndSelect_SameHitTwice_ScrollsBackIntoView() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string path = tmp.File("many-lines.txt");
            var lines = Enumerable.Range(0, 200).Select(i => $"line{i}").ToArray();
            File2.WriteAllText(path, string.Join("\r\n", lines));
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);

            var hit = GrepHitFor(
                path,
                lineNumber: 200,
                lineText: "line199",
                matchStart: 0,
                matchLength: 4
            );

            form.OpenAndSelect(hit);
            var doc = form.FileForTest.TryOpenOrActivate(path);
            Assert.NotNull(doc);
            Assert.True(doc!.Editor.TopLine > 0, "1 回目のジャンプで既にスクロールしている前提");

            // ホイールでのスクロール退避を再現: TopLine だけ動きキャレットは不動。
            doc.Editor.TopLine = 0;
            var selBefore = doc.Editor.GetSelectionCharRange();

            form.OpenAndSelect(hit); // 同じヒットへ再ジャンプ

            // 選択は無変化(=SetSelectionCharRange は早期 return する経路に入っている)。
            Assert.Equal(selBefore, doc.Editor.GetSelectionCharRange());
            // それでもスクロールは追従する。
            int visibleRows = Math.Max(
                1,
                doc.Editor.ClientSize.Height / Math.Max(1, doc.Editor.LineHeightPx)
            );
            Assert.True(
                doc.Editor.TopLine > 0,
                $"expected TopLine > 0 after re-jump, got {doc.Editor.TopLine}"
            );
            Assert.InRange(
                doc.Editor.CurrentLine,
                doc.Editor.TopLine,
                doc.Editor.TopLine + visibleRows - 1
            );
        });

    // ===== A-18: 未保存編集のあるタブへのジャンプ =====

    /// <summary>
    /// grep のヒットはディスク基準。既に開いているタブに未保存の編集があると、
    /// AbsoluteOffset をそのまま使えば別の行に着地して「その行」を発声してしまう。
    /// 行番号+行内容で解決していれば、編集後の正しい行に着地し発声も一致する。
    /// </summary>
    [Fact]
    public void OpenAndSelect_DirtyTab_ResolvesByLineContentNotDiskOffset() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string path = tmp.File("doc.txt");
            var lines = new[] { "alpha", "beta", "gamma needle delta", "epsilon" };
            File2.WriteAllText(path, string.Join("\r\n", lines));
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);

            // ディスク基準のヒット(grep が返すのと同じ値)。
            int diskOffset = "alpha\r\nbeta\r\n".Length + 6; // "gamma " の直後
            var hit = GrepHitFor(path, 3, "gamma needle delta", 6, 6, absoluteOffset: diskOffset);

            // タブを開いてから、ヒット行より前に 3 行挿入して未保存にする。
            var opened = form.FileForTest.TryOpenOrActivate(path);
            Assert.NotNull(opened);
            opened!.Editor.ReplaceCharRange(0, 0, "ins1\r\nins2\r\nins3\r\n");
            Assert.True(opened.Editor.Modified);

            form.OpenAndSelect(hit);

            var doc = form.FileForTest.TryOpenOrActivate(path);
            Assert.NotNull(doc);
            var snap = doc!.Editor.CurrentBuffer.Current;

            // 陽性対照: 旧実装(AbsoluteOffset 直渡し)なら別の行に着地する fixture であること。
            // これが無いと「たまたま同じ行」でも緑になる。
            // 実測(2026-08-31): diskOffset=19 は編集後バッファでは行 index 3("alpha" の内側)を指す。
            // 旧実装を実際に復元して確認済み(選択が [19,25) になり CurrentLine は 4 で赤化した)。
            Assert.NotEqual(5, snap.GetLineIndexOfChar(diskOffset));

            // 正しい着地: 挿入 3 行ぶん下がった index 5(=6 行目)。
            Assert.Equal(5, doc.Editor.CurrentLine);
            var sel = doc.Editor.GetSelectionCharRange();
            Assert.Equal("needle", snap.GetText(sel.Start, sel.End - sel.Start));
            // 発声も着地行と一致する(A-18 の症状は「誤った行を発声」なので発声側も固定する)。
            // Contains ではなく完全一致: "6 行目" は "16 行目" にも含まれるうえ、
            // 余計な接尾辞(Stale 文言等)の混入も同時に塞ぐ。
            Assert.Equal($"{doc.State.DisplayName} 6 行目", form.LastAnnouncementForTest);
        });

    /// <summary>
    /// ヒット行の内容が変わっていれば、黙って別の行へ飛ばず「内容が変わっています」と伝え、
    /// 選択もしない(キャレットを置くだけ)。
    /// </summary>
    [Fact]
    public void OpenAndSelect_StaleHit_AnnouncesContentChangedAndSelectsNothing() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string path = tmp.File("doc.txt");
            var lines = new[] { "alpha", "beta", "gamma needle delta", "epsilon" };
            File2.WriteAllText(path, string.Join("\r\n", lines));
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);

            var opened = form.FileForTest.TryOpenOrActivate(path);
            Assert.NotNull(opened);
            // ヒット行(index 2)を丸ごと別内容に差し替える=近傍にも一致行が無くなる。
            var snap0 = opened!.Editor.CurrentBuffer.Current;
            int start = snap0.GetLineStart(2);
            int len = snap0.GetLineEnd(2, includeBreak: false) - start;
            opened.Editor.ReplaceCharRange(start, len, "totally different");

            form.OpenAndSelect(
                GrepHitFor(path, 3, "gamma needle delta", 6, 6, absoluteOffset: start + 6)
            );

            var doc = form.FileForTest.TryOpenOrActivate(path);
            Assert.NotNull(doc);
            // Stale でも「読み上げる行番号は着地行」であることまで固定する
            // (A-18 の症状は行番号の嘘なので、Stale 文言の有無だけでは網として不足)。
            Assert.Equal(
                $"{doc!.State.DisplayName} 3 行目 内容が変わっています",
                form.LastAnnouncementForTest
            );
            var sel = doc.Editor.GetSelectionCharRange();
            Assert.Equal(sel.Start, sel.End); // 選択しない
            Assert.Equal(2, doc.Editor.CurrentLine); // 行頭へ寄せる
            // 「行頭へ寄せる」の no-change 化を防ぐ: 直前の ReplaceCharRange がキャレットを
            // 置換末尾(行 2 の桁 17)へ置いているので、桁まで見ないと「何もしなかった」でも
            // CurrentLine==2 / 選択ゼロ幅が成立してしまう(CLAUDE.md §4-B)。
            Assert.Equal(0, doc.Editor.GetColumn(sel.Start));
        });

    // ===== hot exit 統合: OnShown の silent 統合復元(設計 §3.3/§8) =====

    // 統合復元 e2e: layout(パスあり clean+無題 dirty+アクティブ指定)+backups →
    // タブ順・本文・Modified・アクティブ・caret・initialEmpty クローズ。ダイアログなし。
    [Fact]
    public void OnShown_UnifiedOn_LayoutAndBackups_RestoredSilently_E2E() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string p1 = tmp.File("a.txt");
            File2.WriteAllText(p1, "AAA\r\nBBB\r\nCCC");
            string dirtyId = NewId();
            PlantBackup(tmp, Rec(dirtyId, path: null, untitledNumber: 2, "unsaved-text"));
            PlantLayout(
                tmp,
                new SessionLayoutRecord(p1, 0, null, false, CaretLine: 1, CaretColumn: 2, 0),
                new SessionLayoutRecord(null, 2, dirtyId, IsActive: true, 0, 0, 0)
            );

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm_Unified(settings, tmp);

            var docs = form.FileForTest.DocsForTest;
            Assert.Equal(2, docs.Count); // initialEmpty(起動時空無題タブ)は閉じられている
            Assert.Equal(p1, docs[0].State.Path); // タブ順=レイアウト順
            Assert.False(docs[0].Editor.Modified);
            Assert.Equal(1, docs[0].Editor.CurrentLine); // caret 復元
            Assert.Equal(2, docs[0].Editor.GetColumn(docs[0].Editor.CurrentPosition));
            Assert.Null(docs[1].State.Path); // 無題 dirty はバックアップ本文で復元
            Assert.Equal(2, docs[1].State.UntitledNumber);
            Assert.Equal("unsaved-text", docs[1].Editor.SnapshotText);
            Assert.True(docs[1].Editor.Modified);
            Assert.True(IsActiveTab(docs[1])); // IsActive 反映
        });

    // A-3(2026-08-22): 復元したキャレットが可視域に入ること。復元経路は
    // FileController の SetCaretByLineColumn(=SetCaretCharOffset 委譲)なので、
    // Task 2 の setter 追従がここまで届いていることの確認。非アクティブタブ(disk 再オープン)と
    // アクティブタブ(バックアップからの無題復元)の両方を 1 本で観測する。
    //
    // 設計書 §3 は「TabControl は非アクティブページのハンドル生成を遅らせるので、復元時点の
    // ClientSize が暫定値で TopLine が最適にならないのでは」と懸念していたが、これは
    // 到達しない: DocumentManager.CreateNew() が生成直後に _tabs.SelectedTab = page を
    // 行うため、どのタブも「作られた瞬間は選択中」で ClientSize が実値になった状態で
    // SetCaretByLineColumn を受ける(その後で次のタブへ選択が移り非アクティブになる)。
    // したがって本テストが固定しているのは「非アクティブでも正しい」ではなく
    // 「作成時選択のおかげで両タブとも実 ClientSize で追従できている」という状態。
    // 将来 CreateNew() から作成時選択を外すとこの前提は黙って崩れる。
    [Fact]
    public void OnShown_UnifiedOn_RestoredCaret_ScrollsIntoView() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string p1 = tmp.File("many.txt");
            var lines = Enumerable.Range(0, 200).Select(i => $"line{i}").ToArray();
            File2.WriteAllText(p1, string.Join("\r\n", lines));
            string dirtyId = NewId();
            PlantBackup(tmp, Rec(dirtyId, path: null, untitledNumber: 2, string.Join("\n", lines)));
            // caret を末尾付近(190)に置く=既定の TopLine=0 では絶対に見えない位置。
            PlantLayout(
                tmp,
                new SessionLayoutRecord(p1, 0, null, false, CaretLine: 190, CaretColumn: 0, 0),
                new SessionLayoutRecord(null, 2, dirtyId, IsActive: true, 190, 0, 0)
            );
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm_Unified(settings, tmp);

            var docs = form.FileForTest.DocsForTest;
            Assert.Equal(2, docs.Count);
            Assert.Equal(p1, docs[0].State.Path); // disk 再オープン経路
            Assert.Null(docs[1].State.Path); // バックアップからの無題復元経路
            Assert.False(IsActiveTab(docs[0])); // 非アクティブ側も観測対象であることを固定
            Assert.True(IsActiveTab(docs[1]));
            foreach (var d in docs)
            {
                int visibleRows = Math.Max(
                    1,
                    d.Editor.ClientSize.Height / Math.Max(1, d.Editor.LineHeightPx)
                );
                int caretLine = d.Editor.CurrentLine;
                Assert.Equal(190, caretLine); // 復元 caret 自体は従来どおり
                Assert.True(d.Editor.TopLine > 0, $"expected TopLine > 0, got {d.Editor.TopLine}");
                Assert.InRange(caretLine, d.Editor.TopLine, d.Editor.TopLine + visibleRows - 1);
            }
        });

    // E5'(layout null=クラッシュ等でレイアウト喪失)+ ON は OfferRestore を呼ばない pin:
    // 孤児バックアップは extras として silent 復元される。ConfirmRestoreOnStartup=true でも
    // RestoreDialog は出ない(出ればモーダルで pump がハング=テスト完走自体が証明)。
    [Fact]
    public void OnShown_UnifiedOn_NoLayout_OrphanBackup_RestoredAsExtra_NoOfferDialog() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string orphanId = NewId();
            PlantBackup(tmp, Rec(orphanId, path: null, untitledNumber: 1, "orphan-body"));

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.ConfirmRestoreOnStartup = true; // OFF 経路ならダイアログが出る設定
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm_Unified(settings, tmp);

            var docs = form.FileForTest.DocsForTest;
            var doc = Assert.Single(docs); // extras 復元+initialEmpty クローズ
            Assert.Null(doc.State.Path);
            Assert.Equal("orphan-body", doc.Editor.SnapshotText);
            Assert.True(doc.Editor.Modified);

            // silent 経路=announcer 無発声(OFF 経路の「バックアップを N 件復元しました」が出ない)
            var announce = form.Controls.OfType<Label>().Single(l => l.AccessibleName == "通知");
            Assert.True(string.IsNullOrEmpty(announce.Text));

            // adopt-move 配線(設計 §3.4): flat 配置の孤児は自セッション dir へ移動済み
            Assert.False(File2.Exists(Path.Combine(tmp.BackupDir, orphanId + ".json")));
            Assert.Single(
                Directory.GetFiles(tmp.BackupDir, orphanId + ".json", SearchOption.AllDirectories)
            );
        });

    // layout があるとき stale な LastSession(レガシー)はレイアウト優先で無視され、
    // 復元後にレガシー残骸(LastSession/buffers.json)が掃除される。
    // session-state.json 自体の消滅は背景ライターの再書込とレースするためここでは固定しない
    // (BackupCoordinatorTests.DeleteConsumedLayout_RemovesLayoutFile が決定的に担保)。
    [Fact]
    public void OnShown_UnifiedOn_LayoutPresent_IgnoresStaleLastSession_AndCleansArtifacts() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string p1 = tmp.File("layout.txt");
            File2.WriteAllText(p1, "from-layout");
            string p2 = tmp.File("stale.txt");
            File2.WriteAllText(p2, "from-legacy");
            PlantLayout(tmp, new SessionLayoutRecord(p1, 0, null, true, 0, 0, 0));
            File2.WriteAllText(tmp.BuffersPath, "{}"); // レガシー残骸

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;
            settings.LastSession = new LastSessionSnapshot(
                new List<SessionTabRecord> { new(p2, 0, null, true, 0, 0) }
            );

            using var form = ShowMainForm_Unified(settings, tmp);

            var docs = form.FileForTest.DocsForTest;
            var doc = Assert.Single(docs);
            Assert.Equal(p1, doc.State.Path); // layout 側が復元される
            Assert.DoesNotContain(docs, d => d.State.Path == p2); // 移行パス不発=stale 無視
            Assert.Null(settings.LastSession); // レガシー残骸の掃除(同一インスタンスを直接観測)
            Assert.False(File2.Exists(tmp.BuffersPath));
        });

    // レガシー移行(設計 §8): session-state.json なし+LastSession あり+buffers.json あり →
    // 3 形(dirty パスあり/無題 dirty/非 dirty パスあり)が復元され、旧残骸が掃除される。
    [Fact]
    public void OnShown_UnifiedOn_LegacyMigration_RestoresThreeForms_AndCleansArtifacts() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string p1 = tmp.File("dirty.txt");
            File2.WriteAllText(p1, "disk-old");
            string p2 = tmp.File("clean.txt");
            File2.WriteAllText(p2, "clean-body");
            string k1 = NewId();
            string k2 = NewId();
            PlantLegacyBuffers(
                tmp,
                new Dictionary<string, string> { [k1] = "edited-dirty", [k2] = "untitled-body" }
            );

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;
            settings.LastSession = new LastSessionSnapshot(
                new List<SessionTabRecord>
                {
                    new(p1, 0, k1, false, 0, 5, CodePage: 65001, WasModified: true),
                    new(null, 3, k2, false, 0, 0, WasModified: true),
                    new(p2, 0, null, IsActive: true, 0, 0),
                }
            );

            using var form = ShowMainForm_Unified(settings, tmp);

            var docs = form.FileForTest.DocsForTest;
            Assert.Equal(3, docs.Count); // initialEmpty は閉じられている
            Assert.Equal(p1, docs[0].State.Path); // dirty パスあり=buffers 本文で復元
            Assert.Equal("edited-dirty", docs[0].Editor.SnapshotText);
            Assert.True(docs[0].Editor.Modified);
            Assert.Equal(5, docs[0].Editor.GetColumn(docs[0].Editor.CurrentPosition));
            Assert.Null(docs[1].State.Path); // 無題 dirty
            Assert.Equal(3, docs[1].State.UntitledNumber);
            Assert.Equal("untitled-body", docs[1].Editor.SnapshotText);
            Assert.True(docs[1].Editor.Modified);
            Assert.Equal(p2, docs[2].State.Path); // 非 dirty パスあり=disk から
            Assert.Equal("clean-body", docs[2].Editor.SnapshotText);
            Assert.False(docs[2].Editor.Modified);
            Assert.True(IsActiveTab(docs[2]));
            Assert.Null(settings.LastSession); // 旧残骸の掃除
            Assert.False(File2.Exists(tmp.BuffersPath));
        });

    // レガシー移行で復元不能パス(ファイル消失)は集約 failedPaths に落ち、起動時空無題タブが残る
    // (=通常起動と等価)。旧 OnShown_RestoreEnabled_MissingFile_KeepsStartupEmpty の統合経路移植。
    [Fact]
    public void OnShown_UnifiedOn_LegacyMigration_MissingFile_KeepsStartupEmpty() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string missing = tmp.File("no-such.txt");

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;
            settings.LastSession = new LastSessionSnapshot(
                new List<SessionTabRecord> { new(missing, 0, null, true, 0, 0) }
            );

            using var form = ShowMainForm_Unified(settings, tmp);

            var doc = Assert.Single(form.FileForTest.DocsForTest);
            Assert.Null(doc.State.Path); // startup empty がそのまま残る
        });

    // 移行 → hot exit 終了で、移行 dirty 文書の本文バックアップが新セッション dir へ実書込される
    // (合成レコードを adopt しない設計=RegisterNew/FinalFlush 経路の保護が生きている pin。
    //  仮に合成 Id を AdoptRestored すると BackupPlanner が None を返し続けて一切書かれず赤化する)。
    [Fact]
    public void OnShown_UnifiedOn_LegacyMigration_ThenHotExitClose_WritesRealBackup() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string p1 = tmp.File("dirty.txt");
            File2.WriteAllText(p1, "disk-old");
            string k1 = NewId();
            PlantLegacyBuffers(tmp, new Dictionary<string, string> { [k1] = "edited-dirty" });

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;
            settings.LastSession = new LastSessionSnapshot(
                new List<SessionTabRecord>
                {
                    new(p1, 0, k1, true, 0, 0, CodePage: 65001, WasModified: true),
                }
            );

            using (var form = ShowMainForm_Unified(settings, tmp))
            {
                var doc = Assert.Single(form.FileForTest.DocsForTest);
                Assert.Equal("edited-dirty", doc.Editor.SnapshotText); // 移行復元済み
                // A-8: silent path を外れると確認ループが実 MessageBox を出し、テストホストが
                // 不可視のモーダルで永久停止する(ミューテーション検証中に実際に踏んだ)。
                // override を置いて「ハング」を「clean な失敗」へ変え、ついでに
                // 呼ばれないこと自体を silent path の証拠として固定する。
                int confirmCalls = 0;
                form.SetConfirmDiscardOverrideForTest(_ =>
                {
                    confirmCalls++;
                    return true;
                });
                form.Close(); // hot exit(ON×BackupON・dirty ≤32M → silent)
                Assert.Equal(true, form.LastCloseTookSilentPathForTest);
                Assert.Equal(0, confirmCalls); // 確認は一度も出ない
            }

            // FinalFlush → Shutdown(keep) 後: レイアウトが dirty タブを実バックアップ Id で参照し、
            // そのバックアップ本文がディスクに存在する=次回起動で移行内容が復元できる。
            var layout = SessionLayoutStore.Load(tmp.LayoutPath);
            Assert.NotNull(layout);
            var tab = Assert.Single(layout!.Tabs, t => t.Path == p1);
            Assert.NotNull(tab.BackupId);
            var records = BackupStore.LoadAll(tmp.BackupDir);
            Assert.Contains(records, r => r.Id == tab.BackupId && r.Content == "edited-dirty");
            Assert.DoesNotContain(records, r => r.Id == k1); // 合成 Id はディスクに書かれない
        });

    [Fact]
    public void OnShown_RestoreDisabled_DoesNotRestore() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string p1 = tmp.File("a.txt");
            File2.WriteAllText(p1, "AAA");

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.RestoreOpenFilesOnStartup = false; // OFF
            settings.LastSession = new LastSessionSnapshot(
                new List<SessionTabRecord> { new(p1, 0, null, true, 0, 0) }
            );

            // ShowMainForm_Unified は Application.DoEvents で OnShown を発火させる=
            // 「復元経路の gate 判定」が実際に評価される(Task 7 review: pump しない純 ShowMainForm
            // では vacuous になり RestoreOpenFilesOnStartup gate を mutation 検証できない)。
            using var form = ShowMainForm_Unified(settings, tmp);
            Assert.DoesNotContain(form.FileForTest.DocsForTest, d => d.State.Path == p1);
        });

    // ===== A-1 / M-31: 起動時ゲートと陳腐化警告の配線(設計 2026-08-22 §3.3 / §4.2) =====

    /// <summary>MainForm.OnShown が MarkStartupRestoreComplete を呼び忘れると、A-1 / M-31 の
    /// 修正が丸ごと死ぬ(BackupCoordinatorTests は seam を直接叩くため配線漏れを検出できない)。
    /// ON 経路で実際にゲートが開くことを固定する。</summary>
    [Fact]
    public void OnShown_UnifiedOn_OpensImmediateReconcileGate() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm_Unified(settings, tmp);

            Assert.True(form.StartupRestoreGateOpenForTest);
        });

    /// <summary>OFF 経路(従来の復元提案)でもゲートは開く。ON 側だけに置くと、
    /// バックアップのみ有効なユーザーで A-1 が直らない。</summary>
    [Fact]
    public void OnShown_UnifiedOff_OpensImmediateReconcileGate() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.RestoreOpenFilesOnStartup = false;

            using var form = ShowMainForm_Unified(settings, tmp);

            Assert.True(form.StartupRestoreGateOpenForTest);
        });

    /// <summary>ゲートは復元<b>後</b>に開くこと。復元より前に開くと、ctor の NewFile →
    /// SetSavePoint が空無題 1 タブのレイアウトを session-state.json へ書き、
    /// OnShown がそれを読んで前回セッションを失う。ここでは「植えたレイアウトが
    /// 復元される」ことで順序を固定する(OnShown_UnifiedOn_LayoutPresent_... の姉妹)。</summary>
    [Fact]
    public void OnShown_UnifiedOn_GateDoesNotClobberPlantedLayout() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string p1 = tmp.File("planted.txt");
            File2.WriteAllText(p1, "planted-body");
            PlantLayout(tmp, new SessionLayoutRecord(p1, 0, null, true, 0, 0, 0));

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm_Unified(settings, tmp);

            var doc = Assert.Single(form.FileForTest.DocsForTest);
            Assert.Equal(p1, doc.State.Path);
            Assert.True(form.StartupRestoreGateOpenForTest);
        });

    /// <summary>A-1 第 2 層の配線: ディスクがバックアップより新しいまま復元されたら、
    /// OnShown が陳腐化警告へ到達し、FileController の記録を回収(クリア)すること。
    /// 判定そのものは FileControllerTests が固定する(ここは配線=回収点の網)。</summary>
    [Fact]
    public void OnShown_UnifiedOn_StaleBackup_WarnsAndDrainsRecord() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string p1 = tmp.File("stale.txt");
            // バックアップ取得時刻より後にディスクが更新された状態を作る
            // (= 保存成功 → 削除がディスクへ届く前にクラッシュ、の再現)。
            var backupTime = DateTime.UtcNow.AddMinutes(-10);
            File2.WriteAllText(p1, "disk-newer");
            var bk = Rec(NewId(), p1, 0, "backup-older") with { TimestampUtc = backupTime };
            PlantBackup(tmp, bk);
            PlantLayout(tmp, new SessionLayoutRecord(p1, 0, bk.Id, true, 0, 0, 0));

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm_Unified(settings, tmp);

            Assert.Equal(1, form.StaleBackupWarningCountForTest); // 警告へ到達した
            Assert.Empty(form.FileForTest.TakeStaleRestoredPaths()); // OnShown が回収済み
            var doc = Assert.Single(form.FileForTest.DocsForTest);
            Assert.Equal("backup-older", doc.Editor.SnapshotText); // 本文は捨てない(§4.2)
            Assert.True(doc.Editor.Modified);
        });

    /// <summary>対照: ディスクがバックアップより古い通常のクラッシュ復元では警告を出さない。
    /// (上のテストが「常に警告する」実装で緑になっていないことの証明)</summary>
    [Fact]
    public void OnShown_UnifiedOn_FreshBackup_DoesNotWarn() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string p1 = tmp.File("fresh.txt");
            File2.WriteAllText(p1, "disk-older");
            var bk = Rec(NewId(), p1, 0, "backup-newer") with
            {
                TimestampUtc = DateTime.UtcNow.AddMinutes(10),
            };
            PlantBackup(tmp, bk);
            PlantLayout(tmp, new SessionLayoutRecord(p1, 0, bk.Id, true, 0, 0, 0));

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm_Unified(settings, tmp);

            Assert.Equal(0, form.StaleBackupWarningCountForTest);
            // レビュー M-6: 復元自体が起きなくなっても 0 件で緑になるため、
            // 「バックアップ本文で復元された」ことを併せて固定する(自己検証性)。
            var doc = Assert.Single(form.FileForTest.DocsForTest);
            Assert.Equal("backup-newer", doc.Editor.SnapshotText);
        });

    // ===== M-11(設計 2026-09-02 §5.4): 設定の破損 / 読取不能を起動時に 1 回知らせる =====

    /// <summary>文言そのものは <see cref="SettingsStartupTests"/> が固定する。ここは配線=
    /// 「渡した文言が起動時に 1 回だけ回収される」ことだけを見るので、中身は何でもよい。</summary>
    private const string WarningForTest =
        "設定ファイルが壊れていたため、既定の設定で起動しました。";

    /// <summary>
    /// 警告があるとき、<c>OnShown</c> が<b>1 回だけ</b>到達する。加えて<b>到達位置</b>を固定する:
    /// ①復元ブロックの <c>finally</c>(<c>MarkStartupRestoreComplete</c>)を抜けた後
    /// ②陳腐化警告より前。
    /// <para>
    /// 到達<b>数</b>だけでは位置は固定できない —— 復元より前へ動かしても、陳腐化警告より後ろへ
    /// 動かしても、どちらの到達数も 1 のままである。そこで <c>MainForm</c> 側に到達した瞬間の
    /// スナップショット(<c>SettingsWarningReachedAtForTest</c>)を持たせ、
    /// <b>周囲の状態で位置を観測する</b>。
    /// </para>
    /// <para>
    /// ①が守る不変条件: <c>MessageBox</c> はメッセージをポンプするので、ゲートが開く前に
    /// モーダルを出すと復元の最中に再入経路を開く(A-8 と同型)。既存の陳腐化警告が
    /// 復元の<b>後</b>に居るのも同じ理由。
    /// </para>
    /// <para>
    /// fixture は<b>陳腐化警告も同時に出る</b>状態にしてある(ディスクがバックアップより新しい)。
    /// 片方しか出ない fixture では ② の観測面が常に 0 で、順序を入れ替える変異が生存する。
    /// </para>
    /// </summary>
    [Fact]
    public void OnShown_SettingsWarning_ReachesOnce_AfterRestoreGate_AndBeforeStaleWarning() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string p1 = tmp.File("stale.txt");
            var backupTime = DateTime.UtcNow.AddMinutes(-10);
            File2.WriteAllText(p1, "disk-newer");
            var bk = Rec(NewId(), p1, 0, "backup-older") with { TimestampUtc = backupTime };
            PlantBackup(tmp, bk);
            PlantLayout(tmp, new SessionLayoutRecord(p1, 0, bk.Id, true, 0, 0, 0));

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm_Unified(settings, tmp, WarningForTest);

            Assert.Equal(1, form.SettingsWarningCountForTest);
            // fixture の検算: 対になる陳腐化警告も実際に出ている(= ② の観測面が生きている)。
            Assert.Equal(1, form.StaleBackupWarningCountForTest);

            var reachedAt = form.SettingsWarningReachedAtForTest;
            Assert.NotNull(reachedAt);
            Assert.True(
                reachedAt!.Value.RestoreGateOpen,
                "設定警告は復元ブロックの finally(MarkStartupRestoreComplete)より後で出すこと"
            );
            Assert.Equal(0, reachedAt.Value.StaleWarningCount); // 陳腐化警告より前
        });

    /// <summary>
    /// 対照: 警告が無ければ<b>1 回も</b>到達しない(初回起動=<c>Missing</c> で警告を読まされない)。
    /// <c>OnShown</c> が実際に走ったことを <c>StartupRestoreGateOpenForTest</c> で併せて固定する
    /// —— これが無いと「OnShown が動かなくなった」実装でも 0 件で緑になる。
    /// </summary>
    [Fact]
    public void OnShown_NoSettingsWarning_NeverReaches() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm_Unified(settings, tmp); // settingsWarning: null

            Assert.Equal(0, form.SettingsWarningCountForTest);
            Assert.Null(form.SettingsWarningReachedAtForTest);
            Assert.True(form.StartupRestoreGateOpenForTest); // OnShown は実際に走った
        });

    /// <summary>
    /// <b>環境仮定の記録</b>(kxEdit 側の網ではない)。<see cref="Form.Shown"/> は 1 つのフォーム
    /// インスタンスにつき 1 回しか上がらない —— <c>Hide()</c>→<c>Show()</c> も
    /// <see cref="Form.ShowInTaskbar"/> の切替によるハンドル再生成も、追加の <c>Shown</c> を
    /// 1 件も起こさない(実測)。
    /// <para>
    /// <b>この事実は「2 回目が出ないことを観測できない」を意味しない。</b> 観測は
    /// <c>OnShown</c> を直接呼べばよく、それは下の
    /// <see cref="OnShown_SettingsWarning_IsIdempotent_WhenInvokedAgain"/> がやっている。
    /// ここが固定しているのは<b>WinForms 側の前提</b>だけで、kxEdit のどの変異でも赤にならない。
    /// </para>
    /// </summary>
    [Fact]
    public void Shown_never_fires_twice_for_the_same_form_instance() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm_Unified(NewSettings(csvAutoModeOnOpen: false), tmp);
            int shown = 0;
            form.Shown += (_, _) => shown++; // 初回発火は購読前に済んでいる

            form.Hide();
            form.Show();
            PumpUntilShown();
            form.ShowInTaskbar = true; // ハンドル再生成
            PumpUntilShown();

            Assert.Equal(0, shown);
        });

    /// <summary>
    /// 警告は<b>起動時に 1 回だけ</b>。<c>OnShown</c> をもう 2 回呼んでも到達数は増えない。
    /// <para>
    /// <see cref="Form.Shown"/> が 1 度しか上がらないので<b>WinForms 経由では 2 周させられない</b>が、
    /// <c>OnShown</c> は <c>protected override</c> で、このアセンブリは <c>InternalsVisibleTo</c> を
    /// 持つ。リフレクションで直接呼べば観測できる(このファイルは既に <c>Program.Main</c> の IL 走査で
    /// リフレクションを使っている)。
    /// </para>
    /// <para>
    /// <b>空振りではない</b>: 冪等性は完全に <c>MainForm.OnShown</c> 冒頭の
    /// <c>_restoreOffered = true;</c> に依っており、<b>その 1 行を落とす変異は他の 750 本を 1 本も
    /// 落とさなかった</b>(実測・§10.19)。この網だけが「到達数 3」で赤くなる。
    /// </para>
    /// </summary>
    [Fact]
    public void OnShown_SettingsWarning_IsIdempotent_WhenInvokedAgain() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm_Unified(
                NewSettings(csvAutoModeOnOpen: false),
                tmp,
                WarningForTest
            );
            Assert.Equal(1, form.SettingsWarningCountForTest); // 前提: 起動で 1 回出た

            var onShown = typeof(MainForm).GetMethod(
                "OnShown",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.NotNull(onShown);
            onShown!.Invoke(form, new object[] { EventArgs.Empty });
            onShown.Invoke(form, new object[] { EventArgs.Empty });

            Assert.Equal(1, form.SettingsWarningCountForTest);
            // 同じガードが陳腐化警告と復元も守っている(道連れで壊れていないことの併記)。
            Assert.Equal(0, form.StaleBackupWarningCountForTest);
        });

    /// <summary>
    /// 起動経路の<b>端から端まで</b>。壊れた settings.json を置いて実際の合成点
    /// (<c>Program.CreateMainForm</c>)からフォームを作り、<c>OnShown</c> が警告へ到達すること
    /// を観測する。これが <c>Prepare</c> → 文言 → ctor → <c>OnShown</c> の値の流れを固定する
    /// 唯一の網である(下の IL テストでは<b>流れは観測できない</b>)。
    /// <para>
    /// 退避が実際に起きた(<c>.bad</c> に元の中身がある)ことも併せて見る —— 警告だけ出して
    /// 退避しない実装と区別が付く。
    /// </para>
    /// <para>
    /// <c>backupDirectory</c> / <c>sessionLayoutPath</c> を渡すのは必須。破損 settings.json から
    /// 作られる既定設定は <c>BackupEnabled=true</c> なので、渡さないと実 <c>%APPDATA%</c> の
    /// バックアップを走査・削除しうる。
    /// </para>
    /// </summary>
    [Fact]
    public void CreateMainForm_warns_once_when_the_settings_file_is_corrupt() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            File2.WriteAllText(tmp.SettingsPath, "{ これは JSON ではない");

            using var form = ShowCreatedMainForm(tmp);

            Assert.Equal(1, form.SettingsWarningCountForTest);
            Assert.True(form.StartupRestoreGateOpenForTest); // OnShown は実際に走った
            // 退避も実際に起きている(Prepare の副作用まで通っている)。
            Assert.False(File2.Exists(tmp.SettingsPath));
            Assert.Equal("{ これは JSON ではない", File2.ReadAllText(tmp.SettingsPath + ".bad"));
        });

    /// <summary>
    /// 対照: 正常な settings.json では同じ経路で<b>1 回も</b>警告に到達しない。
    /// 「常に警告する」実装が上のテストだけでは緑になるため、こちらが必要。
    /// </summary>
    [Fact]
    public void CreateMainForm_does_not_warn_for_a_valid_settings_file() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            SettingsStore.Save(tmp.SettingsPath, new AppSettings { BackupEnabled = false });

            using var form = ShowCreatedMainForm(tmp);

            Assert.Equal(0, form.SettingsWarningCountForTest);
            Assert.True(form.StartupRestoreGateOpenForTest);
        });

    /// <summary>
    /// <see cref="Program.CreateMainForm"/> の<b>隔離引数が実際に効いている</b>こと。
    /// <para>
    /// これが無いと、<c>backupDirectory</c> / <c>sessionLayoutPath</c> の配線が将来切れても
    /// CI は緑のままで、<b>開発機の実 <c>%APPDATA%\kxEdit\backups</c> を走査・削除しうる</b>
    /// —— 破損 settings.json から作られる既定設定は <c>BackupEnabled=true</c> だからである。
    /// 実際、この網を張る前は「隔離引数を無視して別のディレクトリを使う」変異が App 750 全 PASS で
    /// 生存していた(実測・§10.19)。<b>xmldoc だけが防御という状態</b>だった。
    /// </para>
    /// <para>
    /// 観測は<b>復元の中身</b>で行う。<c>tmp</c> にレイアウトとバックアップを植て、
    /// 「植えたバックアップの本文で復元される」ことを見る —— 引数がどこか別の場所を指していれば
    /// レイアウトもバックアップも見つからず、復元は 0 件になる。
    /// </para>
    /// </summary>
    [Fact]
    public void CreateMainForm_routes_the_isolation_paths_into_the_form() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string p1 = tmp.File("planted.txt");
            File2.WriteAllText(p1, "disk-body");
            var bk = Rec(NewId(), p1, 0, "backup-body") with
            {
                TimestampUtc = DateTime.UtcNow.AddMinutes(10),
            };
            PlantBackup(tmp, bk); // ← backupDirectory が効いていなければ見つからない
            PlantLayout(tmp, new SessionLayoutRecord(p1, 0, bk.Id, true, 0, 0, 0)); // ← 同 sessionLayoutPath
            SettingsStore.Save(
                tmp.SettingsPath,
                new AppSettings { BackupEnabled = true, RestoreOpenFilesOnStartup = true }
            );

            using var form = ShowCreatedMainForm(tmp);

            var doc = Assert.Single(form.FileForTest.DocsForTest);
            Assert.Equal(p1, doc.State.Path); // sessionLayoutPath 経由でタブが復元された
            Assert.Equal("backup-body", doc.Editor.SnapshotText); // backupDirectory 経由で本文が来た
        });

    /// <summary>実際の合成点(<see cref="Program.CreateMainForm"/>)からフォームを作り、
    /// <c>OnShown</c> まで進める。隔離は <see cref="ShowMainForm"/> と同じ 4 点
    /// (settings / backups / session-state / last-session-buffers)。</summary>
    private static MainForm ShowCreatedMainForm(TempDir tmp)
    {
        var form = Program.CreateMainForm(tmp.SettingsPath, tmp.BackupDir, tmp.LayoutPath);
        form.SetLastSessionBuffersPathForTest(tmp.BuffersPath);
        form.SetSuppressRestoreDialogsForTest(true);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Show();
        PumpUntilShown();
        return form;
    }

    /// <summary>
    /// 残る 1 ホップ(<c>Main</c> → <c>CreateMainForm</c>)。<c>Main</c> は <c>[STAThread]</c> +
    /// <c>Application.Run</c> のため<b>実行して観測することはできない</b>ので、IL を読んで
    /// 「どのメソッドを呼んでいるか」だけを固定する。
    /// <para>
    /// これで殺せるのは <b>Task 8 以前の形へ戻す変異</b>(<c>SettingsStore.Load(..., out _)</c> を
    /// 直接呼び、破損を無言で捨てる)である。<b>殺せないこと</b>も明示しておく:
    /// 値の流れ(どの引数を渡したか)は呼出集合では観測できない。だから合成点そのものを
    /// <c>CreateMainForm</c> へ切り出し、上の 2 本で<b>実行して</b>固定してある ——
    /// この IL テストが守るのは「その合成点を通っていること」だけである。
    /// </para>
    /// <para>
    /// 向きに注意(<see cref="IlCallees"/> の xmldoc と同じ話): 走査に残るのは<b>偽陽性</b>
    /// (オペランドのバイト列がたまたまメソッドトークンとして解決できる)だけで、<b>偽陰性は無い</b>。
    /// 偽陽性は集合へ「呼んでいないもの」を足す向きにしか働かないので、健全なのは
    /// <b><c>DoesNotContain</c> の側</b>である(偽陽性で緑になることはない)。逆に
    /// <c>Contains</c> は偽陽性で緑になりうるので、下の 1 本は変異
    /// (合成点を迂回する M-E')を当てて実際に赤くなることを確認してある。
    /// </para>
    /// </summary>
    [Fact]
    public void ProgramMain_builds_the_form_through_the_tested_composition_point()
    {
        var main = typeof(Program).GetMethod(
            "Main",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
        );
        Assert.NotNull(main);
        // newobj まで拾う入口を使う: 下の「MainForm を直に組み立てない」は ctor を見る主張。
        var called = IlCallees.OfIncludingNewobj(main!);

        Assert.Contains(
            called,
            m => m.DeclaringType == typeof(Program) && m.Name == nameof(Program.CreateMainForm)
        );
        // ★ 破損の信号を捨てる旧形(SettingsStore.Load を直接叩く)へ戻っていない。
        Assert.DoesNotContain(
            called,
            m => m.DeclaringType == typeof(SettingsStore) && m.Name == "Load"
        );
        // ★ MainForm を Main から直接組み立てる形(= CreateMainForm を迂回する)へ戻っていない。
        Assert.DoesNotContain(
            called,
            m => m is ConstructorInfo && m.DeclaringType == typeof(MainForm)
        );
    }

    // ===== hot exit 統合: OnFormClosing / OnFormClosed(設計 §3.2/§5.2/§10) =====

    // ON×BackupON+dirty → 確認なし(silent close)+FinalFlush が本文バックアップとレイアウトを
    // TempDir へ確定書込し、Shutdown(keepForRestore:true) が次回起動用に残す。
    [Fact]
    public void OnFormClosing_UnifiedOn_BackupOn_Dirty_SilentClose_FlushesLayoutAndBackup() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;

            int overrideCalls = 0;
            using (var form = ShowMainForm_Unified(settings, tmp))
            {
                var doc = form.FileForTest.DocsForTest[0];
                doc.Editor.ReplaceCharRange(0, 0, "dirty-body");
                Assert.True(doc.Editor.Modified); // pre-condition: dirty タブがあることを固定

                form.SetConfirmDiscardOverrideForTest(_ =>
                {
                    overrideCalls++;
                    return true;
                });
                form.Close();
                Assert.Equal(true, form.LastCloseTookSilentPathForTest);
            }

            Assert.Equal(0, overrideCalls); // silent path=ConfirmDiscardIfDirty 呼ばれない

            // Close() は OnFormClosed→Shutdown(keep) の writer ドレインまで同期完了している=決定的
            var layout = SessionLayoutStore.Load(tmp.LayoutPath);
            Assert.NotNull(layout); // FinalFlushForRestore がレイアウトを確定書込
            var tab = Assert.Single(layout!.Tabs);
            Assert.Null(tab.Path);
            Assert.NotNull(tab.BackupId); // dirty 本文はバックアップ参照で保存
            var records = BackupStore.LoadAll(tmp.BackupDir);
            Assert.Contains(records, r => r.Id == tab.BackupId && r.Content == "dirty-body");

            var loaded = SettingsStore.Load(tmp.SettingsPath, out _);
            Assert.Null(loaded.LastSession); // 統合後は旧形式を書かない
        });

    // ON×BackupOFF+dirty → 従来の確認あり(設計 §5.2: 内容を退避できないため silent close しない)。
    // No(破棄)を選んだ無題タブはレイアウトからも除外され、空枠として復活しない(PR #22 M-1 後継)。
    [Fact]
    public void OnFormClosing_UnifiedOn_BackupOff_Dirty_FallsThroughToConfirm_LayoutOnly() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false); // BackupEnabled=false
            settings.RestoreOpenFilesOnStartup = true;

            int overrideCalls = 0;
            using (var form = ShowMainForm_Unified(settings, tmp))
            {
                var doc = form.FileForTest.DocsForTest[0];
                doc.Editor.ReplaceCharRange(0, 0, "dirty");

                form.SetConfirmDiscardOverrideForTest(_ =>
                {
                    overrideCalls++;
                    return true; // No=破棄で閉じる(Modified 維持)
                });
                form.Close();
                Assert.Equal(false, form.LastCloseTookSilentPathForTest);
            }

            Assert.Equal(1, overrideCalls); // fall-through=dirty タブ 1 個に確認 1 回

            var layout = SessionLayoutStore.Load(tmp.LayoutPath);
            Assert.NotNull(layout); // レイアウトのみモードでも FinalFlush はレイアウトを書く
            Assert.Empty(layout!.Tabs); // No'd 無題タブは空枠として復活しない(MarkDiscarded)
            Assert.Empty(BackupStore.LoadAll(tmp.BackupDir)); // 本文は書かれない
        });

    // 設計 §3.2 補遺(PR #22 M-1 後継): ON×BackupON で 32M 超 dirty により fall-through した close で
    // No(破棄)を選んだタブは、レイアウトからもバックアップからも消える=次回起動で silent 復活しない。
    // 32M 超は SetOrReplaceSource(undo 履歴なしの一括差し込み)で 64 MB string 1 個に留める。
    // HasOversizedDirtyDoc の true 側 gate もここで e2e 検証される(silent seam=false)。
    [Fact]
    public void OnFormClosing_UnifiedOn_OversizedFallThrough_DiscardedTabsNotRevived() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;

            int overrideCalls = 0;
            using (var form = ShowMainForm_Unified(settings, tmp))
            {
                var normal = form.FileForTest.DocsForTest[0];
                normal.Editor.ReplaceCharRange(0, 0, "normal-body"); // No 対象(≤32M dirty)

                form.FileForTest.NewFile();
                var big = form.FileForTest.DocsForTest[^1];
                big.Editor.SetOrReplaceSource(
                    kxEdit.Core.Buffers.TextBuffer.FromString(
                        new string('x', BackupCoordinator.MaxBackupChars + 1)
                    )
                );
                big.Editor.ClearSavePoint(); // Modified=true
                Assert.True(form.HasOversizedDirtyDocForTest()); // 32M gate true 側の pre-condition

                form.SetConfirmDiscardOverrideForTest(_ =>
                {
                    overrideCalls++;
                    return true; // No=破棄して続行(Modified 維持)
                });
                form.Close();
                Assert.Equal(false, form.LastCloseTookSilentPathForTest); // oversized で fall-through
            }

            Assert.Equal(2, overrideCalls); // dirty 2 タブに確認

            // No'd タブはレイアウトに現れず、バックアップも残らない=silent 復活経路なし
            var layout = SessionLayoutStore.Load(tmp.LayoutPath);
            Assert.NotNull(layout);
            Assert.Empty(layout!.Tabs);
            Assert.Empty(BackupStore.LoadAll(tmp.BackupDir));
        });

    /// <summary>A-8(設計 2026-08-24): hot exit の確認なしクローズは、本文バックアップが
    /// 実際に書けなかったときは従来の未保存確認へ倒れる。
    /// 失敗注入は Fake ではなく**実 SerialBackupWriter**に対して行う: backupDirectory の位置に
    /// ファイルを置くと BackupStore.Write の Directory.CreateDirectory が IOException を投げ、
    /// OnWriteFailed 経由で BackupCoordinator の失敗キューに積まれる。起動側(LoadAll は
    /// Directory.Exists=false で空・sweep 2 種は try/catch)がこの状況に耐えることは
    /// 2026-08-24 のスパイクで実測済み。
    /// 修正前はこのテストで確認が 0 回・silent=true になり、session-state.json に
    /// 実体の無い BackupId が残る(=次回起動で E4′ タブごと消失)。</summary>
    [Fact]
    public void OnFormClosing_UnifiedOn_BackupWriteFails_FallsThroughToConfirm() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            // backups ディレクトリの位置を「ファイル」で塞ぐ=実 writer の書込が必ず失敗する。
            File2.WriteAllText(tmp.BackupDir, "occupied");

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;

            int confirmCalls = 0;
            using (var form = ShowMainForm_Unified(settings, tmp))
            {
                // Single: dirty タブが 1 個だけであることを固定する(後段の confirmCalls==1 の前提)。
                var doc = Assert.Single(form.FileForTest.DocsForTest);
                doc.Editor.ReplaceCharRange(0, 0, "unsaved-body");
                Assert.True(doc.Editor.Modified);
                Assert.False(form.HasOversizedDirtyDocForTest()); // oversized 経路ではないことを固定

                form.SetConfirmDiscardOverrideForTest(_ =>
                {
                    confirmCalls++;
                    return true; // No=破棄して続行
                });
                form.Close();

                // 事後条件が偽。false の一般的な意味は「退避できたと言い切れない」(timeout も false)
                // だが、この fixture では実 writer の OnWriteFailed が積んだ失敗 Id が根拠になる。
                Assert.Equal(false, form.LastCloseFinalFlushOkForTest);
                Assert.Equal(false, form.LastCloseTookSilentPathForTest); // → 確認経路へ倒れた
            }

            Assert.Equal(1, confirmCalls); // dirty 1 タブに確認 1 回(silent close なら 0 回)

            // A-8 の実害(実体の無い BackupId をレイアウトへ残すこと)が起きていない: No と答えた
            // タブは MarkDiscarded でレイアウトから外れる=次回起動で空枠にも亡霊にもならない。
            // 同型の assertion は BackupOff / oversized の fall-through テストにもあり、
            // BuildLayout の _discarded ガードを落とす変異はそれらも同時に殺す。本行の新規性は
            // 「fall-through の原因が書込失敗のときも同様である」点=末尾 flush を落とす退行を
            // 実測で捕まえる(A-8 の実害そのものを直接見る唯一の assertion)。
            // なお「バックアップが 1 バイトも書けていない」ことは assert しない: base dir 位置が
            // ファイルである以上 Directory.Exists / LoadAll は常に空で、何も主張しないため。
            var layout = SessionLayoutStore.Load(tmp.LayoutPath);
            Assert.NotNull(layout);
            Assert.Empty(layout!.Tabs);
        });

    /// <summary>A-8 の対照群: 書込が成功する通常構成では事後条件が真になり、
    /// 従来どおり確認なしで閉じる(設計 §6.3「ON×BackupON×書込成功=変化なし」)。
    /// ミューテーション被覆の増分は小さい: WaitForFinalFlush が常に false を返す変異は、
    /// 既存の OnFormClosing_UnifiedOn_BackupOn_Dirty_SilentClose_FlushesLayoutAndBackup と
    /// OnFormClosing_CanceledClose_DoesNotPersistDiscardMarks が(より強い assertion で)
    /// 既に殺す。本テストの実効は (a) (silent, flushOk)=(true, true) の隅を固定する唯一の行
    /// (下の LastCloseFinalFlushOkForTest)と、(b) 上の失敗テストが赤くなったときに
    /// 「機構が壊れたのか失敗注入が壊れたのか」を切り分ける診断価値の 2 点。
    /// 対照群が空虚にならないよう、事後条件が真である根拠(実 writer が本文を実ファイルへ
    /// 書き切ったこと)まで見る。</summary>
    [Fact]
    public void OnFormClosing_UnifiedOn_BackupWriteSucceeds_StaysSilent() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;

            int confirmCalls = 0;
            using (var form = ShowMainForm_Unified(settings, tmp))
            {
                var doc = Assert.Single(form.FileForTest.DocsForTest);
                doc.Editor.ReplaceCharRange(0, 0, "unsaved-body");
                Assert.True(doc.Editor.Modified); // 失敗テストと同じ dirty 前提から始める

                form.SetConfirmDiscardOverrideForTest(_ =>
                {
                    confirmCalls++;
                    return true;
                });
                form.Close();

                // Note: この 1 行だけは実時間に依存する(実 writer のバリアが
                // BackupCoordinator.FinalFlushWait=5 秒以内に完了する必要がある。実測は 1 秒未満だが、
                // 極端に遅い CI では timeout→false で赤化しうる)。対の失敗テストは
                // timeout でも書込失敗でも false のため、この依存を持たない。
                Assert.Equal(true, form.LastCloseFinalFlushOkForTest);
                Assert.Equal(true, form.LastCloseTookSilentPathForTest);
            }

            Assert.Equal(0, confirmCalls); // 確認なしで閉じる(hot exit 本来の挙動)
            // Close() は Shutdown(keep) の writer ドレインまで同期完了している=決定的。
            Assert.Contains(BackupStore.LoadAll(tmp.BackupDir), r => r.Content == "unsaved-body");
        });

    // MarkDiscarded の確定は確認ループ完走後(MainForm 側の遅延適用): 途中キャンセルで close が
    // 中止された場合、既に No と答えたタブの破棄マークが残留しない=以後も通常どおり保護される。
    [Fact]
    public void OnFormClosing_CanceledClose_DoesNotPersistDiscardMarks() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm_Unified(settings, tmp);
            var normal = form.FileForTest.DocsForTest[0];
            normal.Editor.ReplaceCharRange(0, 0, "normal-body");

            form.FileForTest.NewFile();
            var big = form.FileForTest.DocsForTest[^1];
            big.Editor.SetOrReplaceSource(
                kxEdit.Core.Buffers.TextBuffer.FromString(
                    new string('x', BackupCoordinator.MaxBackupChars + 1)
                )
            );
            big.Editor.ClearSavePoint(); // oversized dirty → fall-through 強制

            int calls = 0;
            form.SetConfirmDiscardOverrideForTest(_ => ++calls == 1); // 1 回目 No・2 回目キャンセル
            form.Close();
            Assert.True(form.Visible); // e.Cancel=true で閉じられなかった
            Assert.Equal(2, calls);

            // oversized を解消して再 close → silent 経路。No と答えた normal が保護対象のまま
            // (マーク残留の変異=ループ内即時 MarkDiscarded 化はここで赤化する)。
            big.Editor.SetOrReplaceSource(kxEdit.Core.Buffers.TextBuffer.FromString("tiny"));
            form.Close();
            Assert.Equal(true, form.LastCloseTookSilentPathForTest);

            var layout = SessionLayoutStore.Load(tmp.LayoutPath);
            Assert.NotNull(layout);
            Assert.Equal(2, layout!.Tabs.Count); // normal+big の両タブが残る
            var dirtyTab = Assert.Single(layout.Tabs, t => t.BackupId is not null);
            Assert.Contains(
                BackupStore.LoadAll(tmp.BackupDir),
                r => r.Id == dirtyTab.BackupId && r.Content == "normal-body"
            );
        });

    /// <summary>H-1(設計 2026-08-24 §10.7): OnFormClosing の再入ガード。
    /// 実運用の再入は「STA スレッドの管理ブロッキング待機(A-8 で入れた
    /// <see cref="BackupCoordinator.WaitForFinalFlush()"/>)が **SENT メッセージを配送する**」ために
    /// WM_CLOSE(タスクマネージャーの「タスクの終了」)/ WM_QUERYENDSESSION(ログオフ)から起きる。
    /// それをそのまま再現するには別スレッドからの <c>SendMessage</c> と実際に止まる writer が要るが、
    /// 機構(= OnFormClosing の入れ子実行)は同じなので、ここでは確認ループの中から
    /// <see cref="Form.Close"/> を呼んで決定的に作る(<see cref="Form.Close"/> は
    /// <c>SendMessage(WM_CLOSE)</c>=同期再入)。
    /// ガードなしでは入れ子 close が最後まで完走して Form を破棄し(=外側の
    /// <c>e.Cancel</c> は無視される)、外側は破棄済み Form の上で確認ループを続行する
    /// =同じ文書に 2 回確認し、その回答は全て捨てられる。</summary>
    [Fact]
    public void OnFormClosing_ReentrantClose_IsCanceled_OuterCloseSurvives() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false); // BackupEnabled=false
            settings.RestoreOpenFilesOnStartup = true; // 前提ゲートが落ちて確認ループへ入る

            int confirmCalls = 0;
            bool disposedUnderOuterCall = false;
            int closedEvents = 0;
            var closingCancelFlags = new List<bool>();
            using (var form = ShowMainForm_Unified(settings, tmp))
            {
                // Single: dirty タブが 1 個だけ=「同じ文書への確認は 1 回」の前提を固定する。
                var doc = Assert.Single(form.FileForTest.DocsForTest);
                doc.Editor.ReplaceCharRange(0, 0, "dirty-body");
                Assert.True(doc.Editor.Modified);
                form.FormClosed += (_, _) => closedEvents++;
                form.FormClosing += (_, args) => closingCancelFlags.Add(args.Cancel);

                form.SetConfirmDiscardOverrideForTest(_ =>
                {
                    confirmCalls++;
                    if (confirmCalls == 1)
                    {
                        // 入れ子クローズ。1 回目だけに限るのは必須: ガードが無い実装では入れ子側も
                        // 同じ確認ループへ入るため、無条件に呼ぶと無限再帰でテストホストごと落ちる
                        // (赤ではなくクラッシュになり、何が起きたか読めなくなる)。
                        form.Close();
                        disposedUnderOuterCall = form.IsDisposed;
                    }
                    return true; // No=破棄して続行
                });
                form.Close();
            }

            Assert.Equal(1, confirmCalls); // 同じ文書への確認は 1 回だけ(回答が捨てられていない)
            Assert.Equal(1, closedEvents); // close の完走はちょうど 1 回
            Assert.False(disposedUnderOuterCall); // 入れ子が外側の足元で Form を破棄していない
            // ガードは購読者へも WinForms の契約どおり通知する: 入れ子=取消(Cancel=true)、
            // 外側=続行(Cancel=false)の順。ガード内の base.OnFormClosing(e) を落とすと
            // 1 要素だけになりここで赤化する。
            Assert.Equal(new[] { true, false }, closingCancelFlags);
        });

    // 設計 §10: 32M cap 判定の中核(IsOversizedDirty)。32M chars の実バッファを alloc せず
    // 閾値境界を検証する(MaxBackupChars ちょうど=可・+1=不可・clean は常に可)。
    // OnFormClosing の gate 合成(silentPath の !HasOversizedDirtyDoc())はこの純関数+
    // 下の wiring テストで担保する(実 32M 文書の e2e は行わない)。
    [Fact]
    public void IsOversizedDirty_PivotsAtMaxBackupChars_AndRequiresDirty()
    {
        Assert.False(
            MainForm.IsOversizedDirty(modified: true, textLength: BackupCoordinator.MaxBackupChars)
        );
        Assert.True(
            MainForm.IsOversizedDirty(
                modified: true,
                textLength: BackupCoordinator.MaxBackupChars + 1
            )
        );
        Assert.False(
            MainForm.IsOversizedDirty(
                modified: false,
                textLength: BackupCoordinator.MaxBackupChars + 1
            )
        );
    }

    // 32M gate の wiring: 通常サイズの dirty タブでは HasOversizedDirtyDoc=false
    // (=silent close を妨げない)。true 側は上の純関数テストが担保する。
    [Fact]
    public void HasOversizedDirtyDoc_SmallDirtyDoc_False() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm_Unified(settings, tmp);
            var doc = form.FileForTest.DocsForTest[0];
            doc.Editor.ReplaceCharRange(0, 0, "small dirty");
            Assert.True(doc.Editor.Modified);
            Assert.False(form.HasOversizedDirtyDocForTest());
        });

    // OFF 終了 → Shutdown(keepForRestore:false) が自セッションのバックアップと stale レイアウトを
    // 掃除する(keep/delete ピボットの wiring kill: true 化すると両ファイルが残って赤化)。
    [Fact]
    public void OnFormClosed_RestoreOff_CleansSessionBackupsAndLayout() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string p = tmp.File("b.txt");
            File2.WriteAllText(p, "B");
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = false; // OFF
            PlantLayout(tmp, new SessionLayoutRecord(p, 0, null, true, 0, 0, 0)); // stale 残骸

            using (var form = ShowMainForm_Unified(settings, tmp))
            {
                // 起動時空無題タブを dirty 化 → 2 個目のタブを開いて Reconcile を発火させ、
                // dirty タブのバックアップを実書込させる(非既定状態から開始=Stage 6 教訓)。
                var doc = form.FileForTest.DocsForTest[0];
                doc.Editor.ReplaceCharRange(0, 0, "to-be-dropped");
                form.FileForTest.TryOpenOrActivate(p);

                form.SetConfirmDiscardOverrideForTest(_ => true);
                form.Close();
                Assert.Equal(false, form.LastCloseTookSilentPathForTest);
            }

            Assert.False(File2.Exists(tmp.LayoutPath)); // stale レイアウト掃除(亡霊復元の防止)
            Assert.Empty(BackupStore.LoadAll(tmp.BackupDir)); // 自セッション分のバックアップ削除
        });

    // OFF 終了は stale な LastSession(レガシー)を常に null 化し、レガシー buffers.json も
    // 掃除する(Task 7: OFF 恒久ユーザーは移行パスを一度も通らないため、ここで消さないと
    // 本文入り orphan が永久に残る。ON 側の掃除は起動時の移行パスが担う)。
    [Fact]
    public void OnFormClosing_RestoreDisabled_ClearsStaleLastSession_AndLegacyBuffers() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.RestoreOpenFilesOnStartup = false;
            settings.LastSession = new LastSessionSnapshot(
                new List<SessionTabRecord> { new(@"C:\stale.txt", 0, null, true, 0, 0) }
            );
            PlantLegacyBuffers(tmp, new Dictionary<string, string> { ["k"] = "orphan-body" });

            using (var form = ShowMainForm_Unified(settings, tmp))
            {
                form.Close();
            }

            var loaded = SettingsStore.Load(tmp.SettingsPath, out _);
            Assert.False(loaded.RestoreOpenFilesOnStartup);
            Assert.Null(loaded.LastSession);
            Assert.False(File2.Exists(tmp.BuffersPath)); // OFF 終了で orphan 掃除(Task 7)
        });

    // Test 3: 設定 OFF → 従来経路(dirty タブに ConfirmDiscardIfDirty が発火)
    [Fact]
    public void OnFormClosing_RestoreDisabled_DirtyPromptsAsBefore() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.RestoreOpenFilesOnStartup = false; // OFF = 従来経路

            // OFF 終了は OnFormClosing がレガシー buffers も掃除する(Task 7)=ShowMainForm が
            // seam で TempDir へ隔離済みのため実 %APPDATA% は触らない。
            using var form = ShowMainForm(settings, tmp);

            var doc = form.FileForTest.DocsForTest[0];
            doc.Editor.ReplaceCharRange(0, 0, "dirty");

            int overrideCalls = 0;
            form.SetConfirmDiscardOverrideForTest(_ =>
            {
                overrideCalls++;
                return true;
            });

            form.Close();

            Assert.Equal(false, form.LastCloseTookSilentPathForTest);
            Assert.Equal(1, overrideCalls);
        });

    // Test 3b (I-1): 設定 OFF + ユーザーキャンセル → e.Cancel=true で閉じない(cancel-path mutation kill)
    [Fact]
    public void OnFormClosing_RestoreDisabled_UserCancels_AbortsClose() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.RestoreOpenFilesOnStartup = false; // OFF = 従来経路(dialog fires)

            // §8 補遺 I-1 (preventive): 現状 e.Cancel=true でレガシー buffers の Delete 前に
            // return するため実害はないが、将来 cancel 前後の順序変更で regress しても
            // ShowMainForm の seam 隔離により実 %APPDATA% は守られる。
            using var form = ShowMainForm(settings, tmp);
            var doc = form.FileForTest.DocsForTest[0];
            doc.Editor.ReplaceCharRange(0, 0, "dirty");

            int overrideCalls = 0;
            form.SetConfirmDiscardOverrideForTest(_ =>
            {
                overrideCalls++;
                return false; // ユーザーキャンセル=閉じない
            });

            form.Close();

            Assert.True(form.Visible); // e.Cancel=true で閉じられなかった
            Assert.Equal(1, overrideCalls);
            Assert.Equal(false, form.LastCloseTookSilentPathForTest);
            // Note: form は using で自動 Dispose(テスト終了時に真の Close)
        });

    // ===== OFF 経路: バックアップ復元時に初期無題タブを閉じる(設計 2026-07-24) =====

    // 現状バグの回帰テスト: OFF+ConfirmRestore=false+バックアップ 1 件 →
    // 復元後は「復元 doc 1 個のみ」(起動時の initialEmpty は自動的に閉じる)。
    // 修正前は docs.Count=2(起動時無題1+復元無題2)で赤化する。
    [Fact]
    public void OnShown_UnifiedOff_ConfirmFalse_BackupRestored_ClosesInitialEmptyTab() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string bkId = NewId();
            PlantBackup(tmp, Rec(bkId, path: null, untitledNumber: 2, "restored-body"));

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = false; // OFF 経路
            settings.ConfirmRestoreOnStartup = false; // silent 自動復元

            using var form = ShowMainForm_Unified(settings, tmp);

            var docs = form.FileForTest.DocsForTest;
            var doc = Assert.Single(docs); // 修正前は 2 件(初期無題1+復元)になる
            Assert.Null(doc.State.Path);
            Assert.Equal("restored-body", doc.Editor.SnapshotText);
            Assert.True(doc.Editor.Modified);
        });

    // 復元 0 件のとき初期無題タブは残る(復元失敗時にユーザーの作業台=空タブを消さない不変)。
    // 修正前後で共に緑=対称保存テスト。
    [Fact]
    public void OnShown_UnifiedOff_NoBackups_KeepsInitialEmptyTab() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            // バックアップ 0 件

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = false;
            settings.ConfirmRestoreOnStartup = false;

            using var form = ShowMainForm_Unified(settings, tmp);

            var docs = form.FileForTest.DocsForTest;
            var doc = Assert.Single(docs); // 初期無題1 が残る
            Assert.Null(doc.State.Path);
            Assert.False(doc.Editor.Modified); // 初期空タブは Modified=false
        });

    // Announcer 挙動 pin: OFF+ConfirmFalse+復元成功=「バックアップを N 件復元しました」発話。
    // 修正で新設した !_settings.ConfirmRestoreOnStartup ゲートの真側=既存挙動維持。
    [Fact]
    public void OnShown_UnifiedOff_ConfirmFalse_AnnouncesRestoredCount() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string bkId = NewId();
            PlantBackup(tmp, Rec(bkId, path: null, untitledNumber: 2, "restored-body"));

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = false;
            settings.ConfirmRestoreOnStartup = false; // silent 自動復元=発話する経路

            using var form = ShowMainForm_Unified(settings, tmp);

            var announce = form.Controls.OfType<Label>().Single(l => l.AccessibleName == "通知");
            Assert.Contains("バックアップを 1 件復元しました", announce.Text);
        });

    // ===== AnnouncePosition: 位置のみを読む(2026-07-25 文書情報ダイアログ導入) =====

    // 設計 2026-07-25 §0: 位置照会からは「文字数 M」も「選択 K 文字」も削除し、
    // 文字数の詳細は [ファイル]>文書情報 へ集約する。ここでは
    // (a) 行/全/桁 が読まれること (b) 文字数・選択が読まれないこと の両側を固定する。
    // 「選択あり」「非先頭行」という非既定状態から検証を始める(既定状態だと選択削除の
    // 変異が vacuous に通ってしまうため)。
    [Fact]
    public void AnnouncePosition_ReadsLineTotalAndColumnOnly() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);

            var doc = form.FileForTest.DocsForTest[0];
            doc.Editor.ReplaceCharRange(0, 0, "abc\r\ndef"); // 2 行・CharLength=8
            doc.Editor.SelectCharRange(0, 8); // 全選択(旧仕様なら「選択 7 文字」が付いた)

            // AnnouncePosition は private=リフレクションで呼ぶ(Ctrl+Alt+P/メニューの薄いラッパ)。
            var method = typeof(MainForm).GetMethod(
                "AnnouncePosition",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
            );
            Assert.NotNull(method);
            method!.Invoke(form, null);

            var announce = form.Controls.OfType<Label>().Single(l => l.AccessibleName == "通知");
            Assert.Equal("行 2 / 全 2、桁 4", announce.Text);
        });

    // ===== [ファイル] > 文書情報(設計 2026-07-25) =====

    /// <summary>メニュー項目テキストのアクセラレータ("...(&amp;I)" の I)。持たなければ null。</summary>
    private static char? AccelOf(string text)
    {
        int i = text.IndexOf('&');
        return i >= 0 && i + 1 < text.Length ? char.ToUpperInvariant(text[i + 1]) : null;
    }

    // 文書情報は [タブを閉じる] の直上に置く(設計 §0)。位置が動くとキーボード操作の
    // 手順記憶(Alt→F→↑↑ 等)が崩れるため、隣接関係を機械固定する。
    [Fact]
    public void File_menu_contains_document_info_directly_above_close_tab() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);

            var file = form.MainMenuStrip!.Items.OfType<ToolStripMenuItem>()
                .First(mi => mi.Text!.StartsWith("ファイル", StringComparison.Ordinal));
            var items = file.DropDownItems.OfType<ToolStripMenuItem>().ToList();
            int docInfoIdx = items.FindIndex(mi => mi.Text == "文書情報(&I)");
            int closeTabIdx = items.FindIndex(mi => mi.Text == "タブを閉じる(&W)");

            Assert.True(docInfoIdx >= 0, "文書情報 メニューが見つからない");
            Assert.True(closeTabIdx >= 0, "タブを閉じる メニューが見つからない");
            Assert.Equal(closeTabIdx - 1, docInfoIdx); // 直上
        });

    // アクセラレータ &I が [ファイル] 内で衝突しないこと(衝突すると Alt→F→I で選べず巡回になる)。
    [Fact]
    public void File_menu_accelerators_are_unique() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);

            var file = form.MainMenuStrip!.Items.OfType<ToolStripMenuItem>()
                .First(mi => mi.Text!.StartsWith("ファイル", StringComparison.Ordinal));
            var keys = file
                .DropDownItems.OfType<ToolStripMenuItem>()
                .Select(mi => AccelOf(mi.Text!))
                .Where(c => c is not null)
                .Select(c => c!.Value)
                .ToList();

            Assert.Contains('I', keys); // 文書情報(&I) が居る
            Assert.Equal(keys.Count, keys.Distinct().Count()); // 重複なし
        });

    [Fact]
    public void MainForm_ControllerFields_AreReadOnly()
    {
        // Task 1a: null! 代入経路を止め、Controller 群を readonly 化する契約を固定。
        // 実装後は宣言時か ctor 初期化リストで確定代入 = readonly が復活する。
        // 2026-07-25: 文書情報ダイアログの _documentInfo を 7 個目として追加。
        var type = typeof(MainForm);
        var flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        string[] controllerFields =
        {
            "_file",
            "_search",
            "_grep",
            "_backup",
            "_csv",
            "_kinsoku",
            "_documentInfo",
        };
        foreach (var name in controllerFields)
        {
            var field = type.GetField(name, flags);
            Assert.NotNull(field);
            Assert.True(field!.IsInitOnly, $"{name} must be readonly");
        }
    }

    // ===== M-1(2026-08-29): 未処理例外からの退避と前提ゲート =====

    /// <summary>通常構成(BackupON)では実際に退避でき、その本文が実ファイルに載る。
    /// 戻り値 true は「次回起動で復元できる」と言い切る宣言なので、根拠まで見る。</summary>
    [Fact]
    public void FlushBackupsForCrash_BackupOn_Dirty_ReturnsTrue_AndPersistsContent() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm_Unified(settings, tmp);
            var doc = Assert.Single(form.FileForTest.DocsForTest);
            doc.Editor.ReplaceCharRange(0, 0, "crash-body");
            Assert.True(doc.Editor.Modified);

            Assert.True(form.FlushBackupsForCrash());
            Assert.Contains(BackupStore.LoadAll(tmp.BackupDir), r => r.Content == "crash-body");
        });

    /// <summary>BackupOFF では本文が書かれないのに <c>WaitForFinalFlush</c> は
    /// 「書くものが無い=失敗も無い」の true を返す。ゲートを外すと
    /// <b>何も退避していないのに「復元できます」</b>という嘘の安全宣言になる。</summary>
    [Fact]
    public void FlushBackupsForCrash_BackupOff_ReturnsFalse() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = false;
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm_Unified(settings, tmp);
            var doc = Assert.Single(form.FileForTest.DocsForTest);
            doc.Editor.ReplaceCharRange(0, 0, "crash-body");

            Assert.False(form.FlushBackupsForCrash());
        });

    /// <summary>
    /// hot exit OFF(<c>RestoreOpenFilesOnStartup=false</c>)でも BackupON なら
    /// 次回起動の <c>OfferBackupRestoreOnStartup</c> が復元を提案できる=
    /// <b>true を返す</b>(Task 4 レビュー Major-3)。ここを false にすると、
    /// 実際には復元できる支持された構成に嘘の悲観を出すことになる。
    /// hot exit の silent path のゲートと<b>意図的に違う</b>ことを固定する。
    /// </summary>
    [Fact]
    public void FlushBackupsForCrash_RestoreOff_ButBackupOn_ReturnsTrue() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = false;

            using var form = ShowMainForm_Unified(settings, tmp);
            var doc = Assert.Single(form.FileForTest.DocsForTest);
            doc.Editor.ReplaceCharRange(0, 0, "crash-body");

            Assert.True(form.FlushBackupsForCrash());
            Assert.Contains(BackupStore.LoadAll(tmp.BackupDir), r => r.Content == "crash-body");
        });

    /// <summary>書込が実際に失敗する構成では false(= 事後条件検査が効いている)。
    /// 「ゲートさえ通れば true」に丸める変異を kill する。</summary>
    [Fact]
    public void FlushBackupsForCrash_BackupWriteFails_ReturnsFalse() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            // backups ディレクトリの位置を「ファイル」で塞ぐ=実 writer の書込が必ず失敗する。
            File2.WriteAllText(tmp.BackupDir, "occupied");

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;

            using var form = ShowMainForm_Unified(settings, tmp);
            var doc = Assert.Single(form.FileForTest.DocsForTest);
            doc.Editor.ReplaceCharRange(0, 0, "crash-body");
            Assert.False(form.HasOversizedDirtyDocForTest()); // oversized 経路ではない

            Assert.False(form.FlushBackupsForCrash());
        });

    // ===== A-13(2026-08-29): クリップボード失敗の SR 通知 =====

    /// <summary>
    /// 実 MainForm + 実 EditorControl + Fake IClipboard で、Ctrl+C 相当が失敗したときに
    /// SR 向けの通知が実際に出ることを端から端まで固定する
    /// (Editor の捕捉 → DocumentManager の再送 → MainForm の Announcer 呼び出し)。
    /// <c>UiaAnnouncer.Say</c> は視覚表示を無条件で行うため、通知ラベルの文言=Say した文言。
    /// </summary>
    [Fact]
    public void CopyFailure_AnnouncesWriteMessage() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);
            var doc = form.FileForTest.DocsForTest[0];
            doc.Editor.SetClipboardForTest(new FailingClipboard());
            doc.Editor.Text = "hello";
            doc.Editor.SetSelectionCharRange(1, 4);

            doc.Editor.Copy();

            Assert.Equal(
                MainForm.ClipboardFailureMessage(ClipboardFailureKind.Write),
                form.LastAnnouncementForTest
            );
            Assert.Equal("hello", doc.Editor.SnapshotText); // 本文は無傷
        });

    /// <summary>貼り付け失敗は別文言(操作が聞き分けられること)。
    /// Write 側の文言をそのまま流用する変異を kill する。</summary>
    [Fact]
    public void PasteFailure_AnnouncesReadMessage() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);
            var doc = form.FileForTest.DocsForTest[0];
            doc.Editor.SetClipboardForTest(new FailingClipboard());
            doc.Editor.Text = "hello";
            doc.Editor.SetCaretCharOffset(2);

            doc.Editor.Paste();

            string said = form.LastAnnouncementForTest;
            Assert.Equal(MainForm.ClipboardFailureMessage(ClipboardFailureKind.Read), said);
            Assert.NotEqual(MainForm.ClipboardFailureMessage(ClipboardFailureKind.Write), said);
            Assert.Equal("hello", doc.Editor.SnapshotText); // 本文は無傷
        });

    /// <summary>Cut は「クリップボードに書けなければ本文を消さない」(A-13 の核心)。
    /// MainForm 経路でもその不変条件が生きていることを、通知と併せて固定する。</summary>
    [Fact]
    public void CutFailure_KeepsTextAndAnnounces() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);
            var doc = form.FileForTest.DocsForTest[0];
            doc.Editor.SetClipboardForTest(new FailingClipboard());
            doc.Editor.Text = "hello";
            doc.Editor.SetSelectionCharRange(1, 4);

            doc.Editor.Cut();

            Assert.Equal("hello", doc.Editor.SnapshotText);
            Assert.Equal((1, 4), doc.Editor.GetSelectionCharRange());
            Assert.Equal(
                MainForm.ClipboardFailureMessage(ClipboardFailureKind.Write),
                form.LastAnnouncementForTest
            );
        });

    /// <summary>文言そのものの契約(SR で聞いて意味が通る短文・読み書きで別文言)。
    /// <see cref="MainForm.ClipboardFailureMessage"/> は上の 3 テストの期待値の出所でもあるため、
    /// ここで実文字列を 1 か所だけ固定する(3 テストが同時に無意味化するのを防ぐ)。</summary>
    [Fact]
    public void ClipboardFailureMessage_DiffersByKind()
    {
        Assert.Equal(
            "クリップボードにコピーできません。他のアプリが使用中の可能性があります",
            MainForm.ClipboardFailureMessage(ClipboardFailureKind.Write)
        );
        Assert.Equal(
            "クリップボードから貼り付けられません。他のアプリが使用中の可能性があります",
            MainForm.ClipboardFailureMessage(ClipboardFailureKind.Read)
        );
    }
}
