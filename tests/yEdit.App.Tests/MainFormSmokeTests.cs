using System.Collections.Generic;
using System.IO;
using yEdit.Core.Backup;
using yEdit.Core.Session;
using yEdit.Core.Settings;
using Directory = System.IO.Directory;
using File2 = System.IO.File;
using IOException = System.IO.IOException;

namespace yEdit.App.Tests;

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
        public string Root { get; } = Directory.CreateTempSubdirectory("yEditAppSmoke_").FullName;

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
    private static MainForm ShowMainForm(AppSettings settings, TempDir tmp)
    {
        var form = new MainForm(
            settings,
            tmp.SettingsPath,
            backupDirectory: tmp.BackupDir,
            sessionLayoutPath: tmp.LayoutPath
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
    private static MainForm ShowMainForm_Unified(AppSettings settings, TempDir tmp)
    {
        var form = ShowMainForm(settings, tmp);
        form.SetSuppressFailedRestoreDialogForTest(true);
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
            // 拡張子は大文字 DATA.CSV: MainForm:114 の StringComparison.OrdinalIgnoreCase が
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
            // MainForm:113 の設定 ON ガード(!_settings.CsvAutoModeOnOpen return)を削除する変異を kill:
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
            // MainForm:114 の拡張子ガード(.csv 判定 return)を削除する変異を kill:
            // 削除されると拡張子に関わらず TryEnterMode を呼び CsvMode=true になり本 assertion が赤化する。
            Assert.False(doc!.State.CsvMode);
        });

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

            form.OpenAndSelect(path, offset: 2, length: 3);

            // OpenAndSelect 後の Active タブを取り戻す: 既に開いているため FileController.TryOpenOrActivate は
            // 既存タブ再利用の fast path(FindByPath ヒット)を通り _openedFresh を呼ばない=
            // 観測対象(CsvMode/Path/選択レンジ)への副作用はない(内部で RegisterRecent →
            // recentChanged/saveSettings は走るが観測外・実 %APPDATA% は tmp 隔離で汚染ゼロ)。
            var doc = form.FileForTest.TryOpenOrActivate(path);
            Assert.NotNull(doc);

            // MainForm:416 の suppressAutoCsv: true → false 変異を kill:
            // false だと _openedFresh 経路で AutoEnterCsvMode を通し CsvMode=true 化=本 assertion が赤化する。
            Assert.False(doc!.State.CsvMode);
            Assert.Equal(path, doc.State.Path);
            // SelectCharRange(2, 3): start=2 / end=2+3=5(EditorControl:323-324 のエイリアス経由)
            Assert.Equal((2, 5), doc.Editor.GetSelectionCharRange());
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
                form.Close(); // hot exit(ON×BackupON・dirty ≤32M → silent)
                Assert.Equal(true, form.LastCloseTookSilentPathForTest);
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

            var loaded = SettingsStore.Load(tmp.SettingsPath);
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
                    yEdit.Core.Buffers.TextBuffer.FromString(
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
                yEdit.Core.Buffers.TextBuffer.FromString(
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
            big.Editor.SetOrReplaceSource(yEdit.Core.Buffers.TextBuffer.FromString("tiny"));
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

            var loaded = SettingsStore.Load(tmp.SettingsPath);
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

    [Fact]
    public void MainForm_ControllerFields_AreReadOnly()
    {
        // Task 1a: null! 代入経路を止め、6 Controller を readonly 化する契約を固定。
        // 実装後は宣言時か ctor 初期化リストで確定代入 = readonly が復活する。
        var type = typeof(MainForm);
        var flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        string[] controllerFields = { "_file", "_search", "_grep", "_backup", "_csv", "_kinsoku" };
        foreach (var name in controllerFields)
        {
            var field = type.GetField(name, flags);
            Assert.NotNull(field);
            Assert.True(field!.IsInitOnly, $"{name} must be readonly");
        }
    }
}
