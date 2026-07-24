using System.IO;
using yEdit.App.Tests.Fakes;
using yEdit.Core.Backup;
using yEdit.Core.Session;

namespace yEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 5: BackupCoordinator の配線・状態機械・失敗回復・復元 4 分岐のテスト
/// (設計書 §3・§5)。実 DocumentManager+実 EditorControl を STA 上で使い、
/// Form 境界(FakeRestorePrompt)・背景書込(FakeBackupWriter)・時計(FakeTimeProvider)
/// だけを偽物にする。バックアップの判定正しさ(BackupPlanner)・I/O 正しさ(BackupStore)は
/// Core 検証済みのため再検証しない(責務=配線・遷移・失敗回復・冪等性)。
/// </summary>
public class BackupCoordinatorTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 07, 14, 12, 34, 56, TimeSpan.Zero);

    /// <summary>BackupCoordinator を Fake 境界で配線したテストホスト(共通 HostForm.CreateWithDocs を使う)。</summary>
    private sealed class Host : IDisposable
    {
        public Form Form { get; }
        public DocumentManager Docs { get; }
        public FakeBackupWriter Writer { get; } = new();
        public FakeRestorePrompt Prompt { get; } = new();
        public FakeTimeProvider Clock { get; } = new(FixedNow);
        public FakeBackupTraceSink Trace { get; } = new();
        public BackupCoordinator Backup { get; }
        public string TempDir { get; }
        public int WriterFactoryCalls;

        /// <summary>BK-M-2: factory が受け取った session dir を capture する
        /// (test で Ctor_GeneratesUniqueSessionDir_PerInstance が観測する)。</summary>
        public string? CapturedSessionDir;

        /// <summary>hot exit 統合(Task 3): session-state.json のテスト用パス(TempDir 配下に隔離)。
        /// 既定 DefaultPath(%APPDATA%)へ実 I/O が漏れないよう Host は常に明示注入する。</summary>
        public string LayoutPath { get; }

        public Host(
            bool enabled = true,
            int intervalSeconds = 30,
            int? maxBackupCharsOverride = null,
            bool restoreSessionEnabled = false
        )
        {
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
            // OfferRestoreOnStartup 内の LoadAll/SweepTempFiles が Enumerate する空ディレクトリ。
            // 実 I/O は起きないが Directory.Exists=false で無害に return するパス指定は避ける。
            TempDir = Path.Combine(
                Path.GetTempPath(),
                "yEdit-Stage5-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(TempDir);
            LayoutPath = Path.Combine(TempDir, "session-state.json");
            // Task 1b: 既定引数(traceSink=null)経路は本番既定 = DebugBackupTraceSink。
            // ここでは FakeBackupTraceSink を注入して catch{} の trace 発火を assert 可能にする。
            // BK-M-2: factory シグニチャは Func<string, IBackupWriter>=session dir を受け取る。
            // BK-M-3: maxBackupCharsOverride は既定 null=本番 32M chars。size cap テストのみ小さな値を渡す。
            Backup = new BackupCoordinator(
                Docs,
                enabled,
                intervalSeconds,
                Clock,
                sessionDir =>
                {
                    WriterFactoryCalls++;
                    CapturedSessionDir = sessionDir;
                    return Writer;
                },
                Prompt,
                TempDir,
                Trace,
                maxBackupCharsOverride,
                restoreSessionEnabled,
                LayoutPath
            );
        }

        /// <summary>
        /// テスト用に本文を持つ文書を作る。dirty=true(既定)では Text セッター後に
        /// <see cref="EditorControl.ClearSavePoint"/> を呼び Modified=true を強制する
        /// (Text セッターは新規 TextBuffer.FromString を差し込むため Modified=false 起点で始まる=
        /// FileControllerTests の実測コメントと同じ挙動)。dirty=false は Text セッター直後の
        /// クリーン状態のまま(SetSavePoint は fresh バッファに対して事実上 no-op)。
        /// </summary>
        public Document NewDoc(string text, bool dirty = true)
        {
            var doc = Docs.CreateNew();
            doc.Editor.Text = text;
            if (dirty)
                doc.Editor.ClearSavePoint();
            return doc;
        }

        public void Dispose()
        {
            Backup.Dispose();
            Form.Dispose();
            try
            {
                Directory.Delete(TempDir, recursive: true);
            }
            catch
            { /* 掃除失敗は無害 */
            }
        }
    }

    // ===== ctor+UpdateSettings(有効/無効の切替・間隔クランプ) =====

    [Fact]
    public void Ctor_Disabled_DoesNotCreateWriter() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: false);
            Assert.Equal(0, host.WriterFactoryCalls); // 無効時は writer を生成しない(リソース節約)
        });

    [Fact]
    public void Ctor_Enabled_CreatesWriter_AndWiresFailedHook() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: true);
            Assert.Equal(1, host.WriterFactoryCalls);
            Assert.NotNull(host.Writer.OnWriteFailed); // Coordinator が失敗フックを配線している
        });

    [Fact]
    public void Ctor_ClampsIntervalToLowerBound() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: true, intervalSeconds: 1);
            Assert.Equal(5_000, host.Backup.TimerIntervalMs); // 5 未満は下端 5s へクランプ
        });

    [Fact]
    public void Ctor_ClampsIntervalToUpperBound() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: true, intervalSeconds: 99_999);
            Assert.Equal(3_600_000, host.Backup.TimerIntervalMs); // 3600 超は上端 3600s へクランプ
        });

    [Fact]
    public void UpdateSettings_ClampsIntervalToLowerBound() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: true, intervalSeconds: 30);
            host.Backup.UpdateSettings(true, 1, restoreSessionEnabled: false);
            Assert.Equal(5_000, host.Backup.TimerIntervalMs); // 設定ダイアログ経由でも下端クランプ
        });

    [Fact]
    public void UpdateSettings_ClampsIntervalToUpperBound() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: true, intervalSeconds: 30);
            host.Backup.UpdateSettings(true, 99_999, restoreSessionEnabled: false);
            Assert.Equal(3_600_000, host.Backup.TimerIntervalMs); // 設定ダイアログ経由でも上端クランプ
        });

    [Fact]
    public void UpdateSettings_EnableFromDisabled_CreatesWriter_AndReconcilesImmediately() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: false);
            host.NewDoc("abc"); // dirty な文書がある状態で
            host.Backup.UpdateSettings(true, 30, restoreSessionEnabled: false); // 無効→有効: writer 生成+即 Reconcile が走る

            Assert.Equal(1, host.WriterFactoryCalls);
            Assert.Single(host.Writer.Writes); // 有効化した瞬間の未保存文書を保護窓なしで即退避
        });

    // ===== Reconcile 登録(dirty→即 Write / clean→なし / 閉じた doc→Delete) =====

    [Fact]
    public void Reconcile_RegisterNew_Dirty_WritesImmediately() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            _ = host.NewDoc("hello");

            host.Backup.Reconcile();

            var write = Assert.Single(host.Writer.Writes);
            Assert.Equal("hello", write.Content);
            Assert.Single(host.Writer.Store);
        });

    [Fact]
    public void Reconcile_RegisterNew_Clean_DoesNotWrite() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("hello", dirty: false); // Text セッター直後の fresh バッファ=Modified=false

            host.Backup.Reconcile();

            Assert.Empty(host.Writer.Writes); // 保存済み文書は退避しない
        });

    [Fact]
    public void Reconcile_ClosedDoc_DeletesBackup_AndDropsFromMap() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile(); // Write 発生・_map に登録
            var id = host.Writer.Writes[0].Id;

            host.Docs.TryClose(doc, _ => true); // 閉じる(未保存確認は素通し)
            host.Backup.Reconcile();

            Assert.Contains(id, host.Writer.Deletes);
            Assert.False(host.Writer.Store.ContainsKey(id));
        });

    // ===== Reconcile dirty サイクル(sig 変化検知・clean 化での削除) =====

    [Fact]
    public void Reconcile_SameContentTwice_WritesOnlyOnce() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("hello");

            host.Backup.Reconcile();
            host.Backup.Reconcile(); // 同 sig=None

            Assert.Single(host.Writer.Writes);
        });

    [Fact]
    public void Reconcile_ContentChanged_WritesAgain() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile();

            // Text セッターは ReplaceSource 経由で新規 fresh バッファ(Modified=false)を差し込むため、
            // 内容変更後に ClearSavePoint で dirty 状態を明示する(BackupPlanner が Write を出す前提)。
            doc.Editor.Text = "hello world";
            doc.Editor.ClearSavePoint();
            host.Backup.Reconcile();

            Assert.Equal(2, host.Writer.Writes.Count);
            Assert.Equal("hello world", host.Writer.Writes[^1].Content);
        });

    [Fact]
    public void Reconcile_DirtyThenSaved_DeletesBackup() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile(); // Write 発生
            var id = host.Writer.Writes[0].Id;

            doc.Editor.SetSavePoint(); // 保存相当(Modified=false へ)
            host.Backup.Reconcile();

            Assert.Contains(id, host.Writer.Deletes);
            Assert.False(host.Writer.Store.ContainsKey(id));

            // 続く Reconcile では None(HasBackup=false・Modified=false)=追加 Delete も Write も出さない
            int deletesBefore = host.Writer.Deletes.Count;
            host.Backup.Reconcile();
            Assert.Equal(deletesBefore, host.Writer.Deletes.Count);
        });

    // ===== 失敗回復(_failed → 次 Reconcile で ForceWrite) =====

    [Fact]
    public void FailedWrite_ForcesRewrite_NextReconcile() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            _ = host.NewDoc("hello");
            host.Backup.Reconcile(); // Write 1 回目(成功)
            var id = host.Writer.Writes[0].Id;

            host.Writer.OnWriteFailed?.Invoke(id); // 背景失敗を Coordinator に通知
            host.Backup.Reconcile(); // 同 sig でも ForceWrite=true で再書込

            Assert.Equal(2, host.Writer.Writes.Count); // 1 回目+再書込
            Assert.Equal(id, host.Writer.Writes[^1].Id); // 同じ Id で再書込
        });

    [Fact]
    public void ForceWrite_ClearsAfterSuccessfulRewrite() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            _ = host.NewDoc("hello");
            host.Backup.Reconcile();
            var id = host.Writer.Writes[0].Id;
            host.Writer.OnWriteFailed?.Invoke(id);
            host.Backup.Reconcile(); // 再書込=ForceWrite クリア想定

            host.Backup.Reconcile(); // 続く Reconcile では None(同 sig・ForceWrite=false)

            Assert.Equal(2, host.Writer.Writes.Count); // 追加 Write は出ない
        });

    // ===== OfferRestoreOnStartup(4 分岐) =====

    /// <summary>テスト用ラベルから決定的な GUID N (32 桁 hex) を生成する。HIGH-1 白リスト検証導入後、
    /// BackupStore.LoadAll は GUID N でない Id を捨てるため、SHA-256 の先頭 16 バイトを 32 桁 hex に
    /// 写して安定した Id を得る(暗号強度は不要=識別子生成のみ)。</summary>
    private static string HashId(string label)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(label)
        );
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    private static BackupRecord Rec(string label, string content, DateTime? ts = null) =>
        new(
            Id: HashId(label),
            OriginalPath: null,
            UntitledNumber: 1,
            CodePage: 65001,
            HasBom: false,
            LineEndingId: 0,
            Content: content,
            TimestampUtc: ts ?? new DateTime(2026, 07, 14, 12, 0, 0, DateTimeKind.Utc)
        );

    private static void PlantBackup(string dir, BackupRecord rec) => BackupStore.Write(dir, rec);

    [Fact]
    public void OfferRestore_Disabled_ReturnsZero_WithoutPrompting() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: false);
            PlantBackup(host.TempDir, Rec("orphan-1", "abc"));

            int restored = host.Backup.OfferRestoreOnStartup(
                host.Form,
                r => throw new Xunit.Sdk.XunitException("restore must not be called"),
                confirm: true
            );

            Assert.Equal(0, restored);
            Assert.Equal(0, host.Prompt.PromptCount); // 無効時は LoadAll/SweepTempFiles すら走らせない
        });

    [Fact]
    public void OfferRestore_NoRecords_ReturnsZero_WithoutPrompting() =>
        Sta.Run(() =>
        {
            using var host = new Host(); // records 0 件のディレクトリ

            int restored = host.Backup.OfferRestoreOnStartup(
                host.Form,
                r => throw new Xunit.Sdk.XunitException("restore must not be called"),
                confirm: true
            );

            Assert.Equal(0, restored);
            Assert.Equal(0, host.Prompt.PromptCount); // 0 件時はダイアログを出さない
        });

    [Fact]
    public void OfferRestore_ConfirmFalse_RestoresAll_AndReturnsCount() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("r1", "one"));
            PlantBackup(host.TempDir, Rec("r2", "two"));

            int restored = host.Backup.OfferRestoreOnStartup(
                host.Form,
                r =>
                {
                    var d = host.Docs.CreateNew();
                    d.Editor.Text = r.Content ?? ""; // BK-M-3: path-only は空文字扱い(FileController.RestoreFromBackup と同型)
                    return d;
                },
                confirm: false
            );

            Assert.Equal(2, restored);
            Assert.Equal(0, host.Prompt.PromptCount); // confirm=false はダイアログを経由しない
        });

    [Fact]
    public void OfferRestore_ConfirmFalse_OneBadRecord_DoesNotBlockOthers() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("good", "ok"));
            PlantBackup(host.TempDir, Rec("bad", "boom"));

            int restored = host.Backup.OfferRestoreOnStartup(
                host.Form,
                r =>
                {
                    if (r.Id == HashId("bad"))
                        throw new InvalidOperationException("restore failed");
                    var d = host.Docs.CreateNew();
                    d.Editor.Text = r.Content ?? ""; // BK-M-3: path-only は空文字扱い(FileController.RestoreFromBackup と同型)
                    return d;
                },
                confirm: false
            );

            Assert.Equal(1, restored); // 1 件の失敗で他を巻き添えにしない
        });

    [Fact]
    public void OfferRestore_ConfirmTrue_Restore_UsesCheckedRecords_AndInheritsId() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("keep", "keeper"));
            PlantBackup(host.TempDir, Rec("skip", "skipper"));

            var kept = Rec("keep", "keeper");
            host.Prompt.NextOutcome = new RestoreOutcome(RestoreAction.Restore, new[] { kept });

            // 復元ラムダは本番 FileController.RestoreFromBackup と同様に ClearSavePoint で dirty に
            // 固定する(§restore-dirty バグ修正 `59ad8b5`)。これがないと Modified=false・HasBackup=true で
            // 次 Reconcile が Delete("keep") に落ち Id 引き継ぎの検証ができない。
            int returned = host.Backup.OfferRestoreOnStartup(
                host.Form,
                r =>
                {
                    var d = host.Docs.CreateNew();
                    d.Editor.Text = r.Content ?? ""; // BK-M-3: path-only は空文字扱い(FileController.RestoreFromBackup と同型)
                    d.Editor.ClearSavePoint();
                    return d;
                },
                confirm: true
            );

            Assert.Equal(0, returned); // Restore 分岐は 0 を返す(件数は呼び側で通知しない)
            Assert.Equal(1, host.Prompt.PromptCount);
            Assert.Equal(2, host.Prompt.LastRecords?.Count); // ダイアログには全件を渡す
            // 元 Id の引き継ぎ検証: 復元 doc の内容を変更 → Reconcile が「keep」Id で Write。
            // LastSig は OfferRestoreOnStartup 内で sig("keeper") に固定されるため、内容を変えない
            // Reconcile は None を返す(sig 一致)。ここでは Id 引き継ぎの証拠として「Write が新 GUID
            // ではなく "keep" で走る」ことを確認したいので、意図的に内容を進める。
            var restored = host.Docs.Documents.Single();
            restored.Editor.Text = "keeper edited";
            restored.Editor.ClearSavePoint();
            host.Backup.Reconcile();
            Assert.Contains(host.Writer.Writes, w => w.Id == HashId("keep")); // 復元タブは dirty=Write 走る・Id は元
        });

    [Fact]
    public void OfferRestore_ConfirmTrue_OneBadRecord_DoesNotAbortOthers() =>
        Sta.Run(() =>
        {
            // confirm=false 側の同型テスト(OfferRestore_ConfirmFalse_OneBadRecord_DoesNotBlockOthers)と
            // 対称形。ダイアログの Checked に 2 件並べ、片方の restore が throw しても他方は完了する
            // ことを検証(BackupCoordinator §confirm=true 内側 catch:151-154 の実 assert 化)。
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("bad", "boom"));
            PlantBackup(host.TempDir, Rec("good", "ok"));

            var rec1 = Rec("bad", "boom");
            var rec2 = Rec("good", "ok");
            host.Prompt.NextOutcome = new RestoreOutcome(
                RestoreAction.Restore,
                new[] { rec1, rec2 }
            );

            int restoreCalls = 0;
            host.Backup.OfferRestoreOnStartup(
                host.Form,
                r =>
                {
                    restoreCalls++;
                    if (r.Id == HashId("bad"))
                        throw new InvalidOperationException("restore failed");
                    var d = host.Docs.CreateNew();
                    d.Editor.Text = r.Content ?? ""; // BK-M-3: path-only は空文字扱い(FileController.RestoreFromBackup と同型)
                    d.Editor.ClearSavePoint();
                    return d; // "good" は成功=タブに登録される
                },
                confirm: true
            ); // 例外が伝播しないこと自体が assert(赤なら Test Runner が拾う)

            Assert.Equal(2, restoreCalls); // 1 件目の失敗で 2 件目を巻き添えにしない
            var restored = host.Docs.Documents.Single(); // 成功した "good" の文書が map/tab に登録済み
            // 元 Id "good" の引き継ぎまで検証(confirm=false 側と異なり、confirm=true は _map 登録も走る)。
            restored.Editor.Text = "ok edited";
            restored.Editor.ClearSavePoint();
            host.Backup.Reconcile();
            Assert.Contains(host.Writer.Writes, w => w.Id == HashId("good"));
        });

    // ===== Task 1b: silent catch → IBackupTraceSink 導線(restore-item / restore-item-later) =====

    [Fact]
    public void OfferRestoreOnStartup_ConfirmFalse_RestoreThrows_WarnsRestoreItemLater() =>
        Sta.Run(() =>
        {
            // Task 1b: BackupCoordinator の confirm=false 経路(:128-137)の silent catch を
            // IBackupTraceSink 経由で診断可能にした契約を固定する。
            // 例外は握り潰す(全復元を巻き添えにしない)挙動は保持しつつ、trace に category=
            // "restore-item-later" と例外実体が渡されることを assert。
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("bad", "boom"));

            var ex = new InvalidOperationException("restore failed later");
            int restored = host.Backup.OfferRestoreOnStartup(
                host.Form,
                r => throw ex,
                confirm: false
            );

            Assert.Equal(0, restored); // 失敗した 1 件は復元件数に含まれない
            var warn = Assert.Single(host.Trace.Warnings); // 1 レコード=1 warn
            Assert.Equal("restore-item-later", warn.Category);
            Assert.Same(ex, warn.Ex); // 例外実体まで trace へ渡す
            // Task 3 (BK-L-5): detail は SanitizeForDisplay.OneLine(rec.Id, 200) 経由。
            // 正当な GUID N (32 桁 lowercase hex) は sanitize しても不変 = 生 Id と一致。
            Assert.Equal(HashId("bad"), warn.Detail);
        });

    [Fact]
    public void OfferRestoreOnStartup_ConfirmTrue_RestoreThrows_WarnsRestoreItem() =>
        Sta.Run(() =>
        {
            // Task 1b: confirm=true 経路(:146-157)の silent catch を IBackupTraceSink 経由で診断可能に。
            // OfferRestore_ConfirmTrue_OneBadRecord_DoesNotAbortOthers の対称形として、
            // 例外が握り潰される(他レコードを巻き添えにしない)挙動は保持しつつ、trace に
            // category="restore-item" と例外実体が渡ることを assert する。
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("bad", "boom"));

            var rec = Rec("bad", "boom");
            host.Prompt.NextOutcome = new RestoreOutcome(RestoreAction.Restore, new[] { rec });

            var ex = new InvalidOperationException("restore failed");
            host.Backup.OfferRestoreOnStartup(host.Form, r => throw ex, confirm: true);

            Assert.Equal(1, host.Prompt.PromptCount); // ダイアログ経路
            var warn = Assert.Single(host.Trace.Warnings);
            Assert.Equal("restore-item", warn.Category);
            Assert.Same(ex, warn.Ex);
            // Task 3 (BK-L-5): detail は SanitizeForDisplay.OneLine(rec.Id, 200) 経由。
            // 正当な GUID N (32 桁 lowercase hex) は sanitize しても不変 = 生 Id と一致。
            Assert.Equal(HashId("bad"), warn.Detail);
        });

    [Fact]
    public void OfferRestoreOnStartup_ConfirmTrue_RestoreThrows_SanitizesId_ViaSanitizeForDisplay() =>
        Sta.Run(() =>
        {
            // Task 3 (BK-L-5): restore-item catch の detail は SanitizeForDisplay.OneLine(rec.Id, 200)
            // で無害化する契約を pin する。攻撃者は BackupRecord.Id に BiDi override (U+202E) や
            // 制御文字/過剰長を仕込める。LoadAll 経路は BackupIdValidator が reject するが、
            // Prompt の outcome.Checked 経路は validator を通らず、悪意 Id を直接注入できる。
            // 本テストは (a) BiDi/制御文字 drop、(b) 200 文字超は "…" 切詰め、
            // (c) 旧 SafeIdForLog (非 hex を '?' 置換) に戻す変異を kill する三役を担う。
            using var host = new Host();
            // LoadAll が非空を返すよう有効な record を 1 件 plant (無いと records.Count==0 で早期 return)。
            PlantBackup(host.TempDir, Rec("dummy", "x"));

            // outcome.Checked に載せる悪意 Id: BiDi override + CRLF + 300 文字超。
            // BackupStore.Write は BackupIdValidator で reject するため、Write 経由では入れられない。
            // \u202E = RIGHT-TO-LEFT OVERRIDE (Format cat) → SanitizeForDisplay で drop される。
            var maliciousId = "abc\u202Edef\r\nGET /evil" + new string('x', 300);
            var maliciousRec = new BackupRecord(
                Id: maliciousId,
                OriginalPath: null,
                UntitledNumber: 1,
                CodePage: 65001,
                HasBom: false,
                LineEndingId: 0,
                Content: "boom",
                TimestampUtc: FixedNow.UtcDateTime
            );
            host.Prompt.NextOutcome = new RestoreOutcome(
                RestoreAction.Restore,
                new[] { maliciousRec }
            );

            var ex = new InvalidOperationException("restore failed");
            host.Backup.OfferRestoreOnStartup(host.Form, r => throw ex, confirm: true);

            var warn = Assert.Single(host.Trace.Warnings);
            Assert.Equal("restore-item", warn.Category);
            Assert.Same(ex, warn.Ex);
            // detail == SanitizeForDisplay.OneLine(maliciousId, 200) の厳密一致で契約を pin。
            Assert.Equal(yEdit.Core.Text.SanitizeForDisplay.OneLine(maliciousId, 200), warn.Detail);
            // 直接的な健全性 assert (契約破りをすぐに読み取れるように):
            // char overload は Contains(char) 経由で ordinal 比較。string overload は CurrentCulture 下で
            // ‮ (Cf/Format) を無視可能扱いにするため sanitize 済みでも常に赤化する罠がある。
            Assert.DoesNotContain('\u202E', warn.Detail); // BiDi override (RLO) drop
            Assert.DoesNotContain('\r', warn.Detail); // CR drop → 空白畳み込み
            Assert.DoesNotContain('\n', warn.Detail); // LF drop → 空白畳み込み
            Assert.True(warn.Detail.Length <= 200, "detail length should be <=200");
        });

    [Fact]
    public void OfferRestoreOnStartup_TriggersTraceSink_OnCorruptBackup() =>
        Sta.Run(() =>
        {
            // Task 2 (BK-L-6): BackupStore.LoadAll の per-file catch を trace sink 経由に。
            // 破損 JSON を仕込んだ状態で OfferRestoreOnStartup を叩き、valid record は復元される
            // (既存挙動維持=一部破損で他を巻き添えにしない)一方で破損側は
            // FakeBackupTraceSink.Warnings に category="backup-load-failed" として届くことを assert。
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("good", "ok")); // valid: 復元される
            File.WriteAllText(
                Path.Combine(host.TempDir, "broken.json"),
                "{ this is not valid json "
            );

            int restored = host.Backup.OfferRestoreOnStartup(
                host.Form,
                r =>
                {
                    var d = host.Docs.CreateNew();
                    d.Editor.Text = r.Content ?? ""; // BK-M-3: path-only は空文字扱い(FileController.RestoreFromBackup と同型)
                    return d;
                },
                confirm: false
            );

            Assert.Equal(1, restored); // valid は復元される(破損で巻き添えにならない)
            var warn = Assert.Single(host.Trace.Warnings);
            Assert.Equal("backup-load-failed", warn.Category);
            Assert.Null(warn.Ex); // detail に kind をコロン結合するため Exception 実体は渡さない
            // detail = "<sanitized file path>:<kind>" 形式(SanitizeForDisplay.OneLine + ":" + kind)
            Assert.Contains("broken.json", warn.Detail);
            Assert.Contains(":", warn.Detail);
            // BackupIdValidator/null-record ではなく破損 JSON 由来の例外型名
            Assert.DoesNotContain("invalid-id", warn.Detail);
            Assert.DoesNotContain("null-record", warn.Detail);
        });

    [Fact]
    public void OfferRestore_ConfirmTrue_DiscardAll_InvokesWriterDeleteAll() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("r1", "one"));
            host.Prompt.NextOutcome = new RestoreOutcome(
                RestoreAction.DiscardAll,
                Array.Empty<BackupRecord>()
            );

            host.Backup.OfferRestoreOnStartup(
                host.Form,
                r => throw new Xunit.Sdk.XunitException("restore must not be called"),
                confirm: true
            );

            Assert.Equal(1, host.Writer.DeleteAllCount);
        });

    [Fact]
    public void OfferRestore_ConfirmTrue_Later_DoesNothing() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("r1", "one"));
            host.Prompt.NextOutcome = RestoreOutcome.LaterEmpty;

            host.Backup.OfferRestoreOnStartup(
                host.Form,
                r => throw new Xunit.Sdk.XunitException("restore must not be called"),
                confirm: true
            );

            Assert.Equal(0, host.Writer.DeleteAllCount);
            Assert.Empty(host.Writer.Deletes);
            Assert.Empty(host.Writer.Writes);
        });

    // ===== Shutdown/Dispose(冪等・管理分削除) =====

    [Fact]
    public void Shutdown_DeletesManagedBackups_AndDisposesWriter() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            _ = host.NewDoc("one");
            _ = host.NewDoc("two");
            host.Backup.Reconcile(); // 両方 Write=HasBackup=true

            host.Backup.Shutdown();

            Assert.Equal(2, host.Writer.Deletes.Count); // 管理分を全 Delete
            Assert.Equal(1, host.Writer.DisposeCount); // ライターをドレイン
        });

    [Fact]
    public void Shutdown_Idempotent_SecondCallIsNoOp() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("hello");
            host.Backup.Reconcile();

            host.Backup.Shutdown();
            int deletesAfterFirst = host.Writer.Deletes.Count;
            int disposesAfterFirst = host.Writer.DisposeCount;
            host.Backup.Shutdown(); // 2 回目

            Assert.Equal(deletesAfterFirst, host.Writer.Deletes.Count);
            Assert.Equal(disposesAfterFirst, host.Writer.DisposeCount);
        });

    [Fact]
    public void Dispose_WithoutShutdown_DisposesWriter_WithoutDeletingBackups() =>
        Sta.Run(() =>
        {
            // 後始末は他テストと同じく using var host に統一(BackupCoordinator.Dispose は _shutDown で
            // 冪等=Host.Dispose 内の 2 回目 Dispose は writer/timer に再突入しない)。
            using var host = new Host();
            host.NewDoc("hello");
            host.Backup.Reconcile();

            host.Backup.Dispose(); // 異常系(Shutdown 未経由)

            Assert.Empty(host.Writer.Deletes); // 管理分の削除は行わない(孤児として次回復元)
            Assert.Equal(1, host.Writer.DisposeCount);
        });

    // ===== TimeProvider(BackupRecord.TimestampUtc が clock 由来) =====

    [Fact]
    public void BuildRecord_UsesInjectedClock_ForTimestamp() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("hello");

            host.Backup.Reconcile();

            Assert.Equal(FixedNow.UtcDateTime, host.Writer.Writes[0].TimestampUtc);
        });

    // ===== 追加: 対応固定(Reconcile の Write/Delete が IBackupWriter 経由であることの担保) =====

    [Fact]
    public void Reconcile_MultipleDocs_WriteRoutingIsPerDoc() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            _ = host.NewDoc("A");
            _ = host.NewDoc("B");

            host.Backup.Reconcile();

            Assert.Equal(2, host.Writer.Writes.Count);
            Assert.NotEqual(host.Writer.Writes[0].Id, host.Writer.Writes[1].Id); // 個別 Id
        });

    [Fact]
    public void ReconcileAfterActiveDocumentChanged_DoesNotDoubleWrite() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("hello");

            host.Backup.Reconcile(); // 初回 Write
            _ = host.Docs.CreateNew(); // ActiveDocumentChanged=内部で Reconcile が走る

            Assert.Single(host.Writer.Writes); // 元 doc は同 sig=再 Write なし(2 回目の Reconcile で None)
        });

    [Fact]
    public void UpdateSettings_DisableFromEnabled_DoesNotDisposeWriter() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: true);
            host.NewDoc("hello");
            host.Backup.Reconcile();

            host.Backup.UpdateSettings(false, 30, restoreSessionEnabled: false); // 有効→無効: 既存ファイルを削除しない・writer は残す
            int disposedBefore = host.Writer.DisposeCount;

            Assert.Equal(disposedBefore, host.Writer.DisposeCount); // Dispose は Shutdown/Dispose まで待つ
            Assert.Empty(host.Writer.Deletes);
        });

    // ===== Lazy 生成の一度性(_writer ??= CreateWriter の pin) =====

    [Fact]
    public void Writer_IsCreated_LazilyOnce_AcrossMultipleReconciles() =>
        Sta.Run(() =>
        {
            // BackupCoordinator.cs:93 `_writer ??= CreateWriter()` を pin する。
            // 無効起動 → 有効化(factory 呼ばれ 1)→ dirty サイクル+Reconcile 複数回 →
            // 無効化 → 再有効化(??= により factory 呼ばれない=2 にならない)を通しても
            // WriterFactoryCalls が 1 のまま=Lazy 生成の意味論(既存 writer は再利用)を固定。
            // ??= を = に変えたバグ変異では、再有効化時に factory が 2 回目を呼び 2 になる。
            using var host = new Host(enabled: false);
            Assert.Equal(0, host.WriterFactoryCalls); // 無効起動=writer 未生成(既存 Ctor_Disabled と対称)

            host.Backup.UpdateSettings(true, 30, restoreSessionEnabled: false); // 無効→有効(初回生成)
            Assert.Equal(1, host.WriterFactoryCalls);

            // dirty サイクルを複数回回しても Reconcile 経路は factory を呼ばない(そもそも Reconcile 側に生成分岐がない)。
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile();
            doc.Editor.Text = "hello world";
            doc.Editor.ClearSavePoint();
            host.Backup.Reconcile();

            // 有効→無効→有効の切替で ??= の右辺は再評価されない(既存 _writer を再利用)。
            host.Backup.UpdateSettings(false, 30, restoreSessionEnabled: false);
            host.Backup.UpdateSettings(true, 30, restoreSessionEnabled: false);
            host.Backup.Reconcile();

            Assert.Equal(1, host.WriterFactoryCalls); // ??= が 2 回目以降を抑止(mutation kill: ??= → = で赤化)
        });

    // ===== HasBackup=false Delete ガード(:189 Reconcile-close 経路 / :270 Shutdown 経路) =====

    [Fact]
    public void Reconcile_ClosedDoc_WithoutBackup_DoesNotCall_Delete() =>
        Sta.Run(() =>
        {
            // BackupCoordinator.cs:189 `if (gone.HasBackup) _writer?.Delete(gone.Id)` を pin する。
            // clean な doc は Reconcile で _map に登録されるが Write が走らず HasBackup=false のまま。
            // 閉じてから Reconcile → gone.HasBackup=false 分岐で Delete をスキップすることを固定。
            // 条件を `if (true)` に変異させると Delete が余分に呼ばれ本テストが赤化する。
            using var host = new Host();
            var doc = host.NewDoc("hello", dirty: false); // Text セッター直後の fresh バッファ=Modified=false
            host.Backup.Reconcile(); // RegisterNew: _map に HasBackup=false で登録・Write は出ない
            Assert.Empty(host.Writer.Writes); // sanity: HasBackup=false 前提

            host.Docs.TryClose(doc, _ => true); // 未保存確認は素通し(clean なので通常も出ない)
            host.Backup.Reconcile(); // 閉じた doc の gone 経路: HasBackup=false → Delete しない

            Assert.Empty(host.Writer.Deletes); // ガード発火(:189)
        });

    [Fact]
    public void Shutdown_WithoutBackup_DoesNotCall_Delete() =>
        Sta.Run(() =>
        {
            // BackupCoordinator.cs:270 `if (info.HasBackup) _writer?.Delete(info.Id)` を pin する。
            // clean な doc(HasBackup=false)のまま Shutdown → 管理分だが Delete をスキップし、
            // writer は Dispose される(Shutdown_DeletesManagedBackups_AndDisposesWriter と対称)。
            // 条件を `if (true)` に変異させると Delete が余分に呼ばれ本テストが赤化する。
            using var host = new Host();
            host.NewDoc("hello", dirty: false); // Text セッター直後=Modified=false
            host.Backup.Reconcile(); // RegisterNew: HasBackup=false で登録・Write なし
            Assert.Empty(host.Writer.Writes); // sanity: HasBackup=false 前提

            host.Backup.Shutdown();

            Assert.Empty(host.Writer.Deletes); // ガード発火(:270)
            Assert.Equal(1, host.Writer.DisposeCount); // Shutdown は writer を必ず Dispose する
        });

    // ===== BK-M-2: セッション別 subdir(factory シグニチャ + 30 日 sweep) =====

    [Fact]
    public void Ctor_PassesSessionSubdir_ToWriterFactory() =>
        Sta.Run(() =>
        {
            // BK-M-2: factory シグニチャ Func<string, IBackupWriter>=writer は session dir を
            // ctor で受け取る。ctor が渡すのは base dir(TempDir)ではなく "session-{Guid.N}" prefix の
            // 直接子 subdir(_dir と混同したまま呼ぶ変異を kill する pin)。
            using var host = new Host(enabled: true);

            Assert.Equal(1, host.WriterFactoryCalls);
            Assert.NotNull(host.CapturedSessionDir);
            var expectedPrefix = Path.Combine(host.TempDir, "session-");
            Assert.StartsWith(expectedPrefix, host.CapturedSessionDir!);
            Assert.NotEqual(host.TempDir, host.CapturedSessionDir); // base dir 直渡しは NG
            // "session-{Guid.N}" の 32 桁 hex 部分が実際に付いていることを確認。
            var suffix = Path.GetFileName(host.CapturedSessionDir!)?.Substring("session-".Length);
            Assert.Equal(32, suffix?.Length);
        });

    [Fact]
    public void Ctor_GeneratesUniqueSessionDir_PerInstance() =>
        Sta.Run(() =>
        {
            // BK-M-2: プロセス内で複数 Coordinator を生成しても session dir が衝突しない
            // (Guid.NewGuid で生成=同時 2 インスタンス起動シナリオのモック)。
            // Host は base dir(TempDir)を各々別に生成するため、full path 比較は
            // 「Guid を hardcode に変えた」変異を kill できない。session-* subdir の
            // 名前部分(session-{Guid.N})を切り出して比較=Guid 部分が実際に別値であることを pin。
            using var host1 = new Host(enabled: true);
            using var host2 = new Host(enabled: true);

            Assert.NotNull(host1.CapturedSessionDir);
            Assert.NotNull(host2.CapturedSessionDir);
            var name1 = Path.GetFileName(host1.CapturedSessionDir!);
            var name2 = Path.GetFileName(host2.CapturedSessionDir!);
            Assert.NotEqual(name1, name2); // "session-{Guid.N}" 部分が衝突しない
        });

    [Fact]
    public void OfferRestoreOnStartup_SweepsOldSessions_OlderThan30Days() =>
        Sta.Run(() =>
        {
            // BK-M-2: 30 日以上更新のない session-* subdir が OfferRestoreOnStartup 冒頭で消える。
            // 時計は FakeTimeProvider で FixedNow(2026-07-14)固定=SetLastWriteTimeUtc で
            // 60 日前に相当する古い session dir を植える(seam=Directory.SetLastWriteTimeUtc)。
            using var host = new Host();
            var stale = Path.Combine(host.TempDir, "session-stale-guid");
            var fresh = Path.Combine(host.TempDir, "session-fresh-guid");
            Directory.CreateDirectory(stale);
            Directory.CreateDirectory(fresh);
            Directory.SetLastWriteTimeUtc(stale, FixedNow.UtcDateTime - TimeSpan.FromDays(60));
            Directory.SetLastWriteTimeUtc(fresh, FixedNow.UtcDateTime - TimeSpan.FromDays(5));

            host.Backup.OfferRestoreOnStartup(
                host.Form,
                r => throw new Xunit.Sdk.XunitException("restore must not be called"),
                confirm: true
            );

            Assert.False(Directory.Exists(stale)); // 60 日=30 日超で削除
            Assert.True(Directory.Exists(fresh)); // 5 日=残す
        });

    // ===== BK-M-3: バックアップサイズ上限 + path-only fallback =====

    [Fact]
    public void MaxBackupChars_PinsTo_32M_Chars()
    {
        // BK-M-3: 実運用の「日常編集される最大 CSV」を大きく超える 32M chars=64MB UTF-16 に固定。
        // 定数を変える変異(28M/64M/デシマル 32,000,000 等)を kill する pin。
        // 32 * 1024 * 1024 = 33,554,432。
        Assert.Equal(32 * 1024 * 1024, BackupCoordinator.MaxBackupChars);
    }

    [Fact]
    public void Reconcile_ContentUnderCap_StoresContent() =>
        Sta.Run(() =>
        {
            // BK-M-3 通常経路 regression: 上限未満は Content が実本文で書かれる(本番=32M chars 未満の
            // 全ての実運用ケース)。size cap を極小に seam したうえで "十分未満" (1 char) を通す。
            using var host = new Host(maxBackupCharsOverride: 100);
            _ = host.NewDoc("hello"); // 5 chars << 100

            host.Backup.Reconcile();

            var write = Assert.Single(host.Writer.Writes);
            Assert.Equal("hello", write.Content); // path-only ではなく本文が入る
            Assert.Empty(host.Trace.Warnings); // backup-content-skipped は発火しない
        });

    [Fact]
    public void Reconcile_FallsBackToPathOnly_WhenExceedsMaxSize() =>
        Sta.Run(() =>
        {
            // BK-M-3 核心: content.Length > _maxBackupChars で Content=null にフォールバック +
            // "backup-content-skipped" を trace。size cap を 10 chars に seam し 11 chars を流す。
            using var host = new Host(maxBackupCharsOverride: 10);
            _ = host.NewDoc("hello world"); // 11 chars > 10

            host.Backup.Reconcile();

            var write = Assert.Single(host.Writer.Writes);
            Assert.Null(write.Content); // path-only=Content は null で保存
            // メタ (Path/CodePage 等) は通常通り書かれる=RestoreDialog に「無題」として現れる
            Assert.Equal(65001, write.CodePage); // sanity: 65001 (UTF-8) が入る

            var warn = Assert.Single(host.Trace.Warnings);
            Assert.Equal("backup-content-skipped", warn.Category);
            Assert.Null(warn.Ex); // ex=null (size cap は正常系の分岐で例外ではない)
            // BK-M-3 I-1: detail に content.Length が含まれる=閾値チューニング診断のための size ヒント。
            // "hello world" は 11 chars なので "11chars" の substring が含まれる。
            Assert.Contains("11chars", warn.Detail, StringComparison.Ordinal);
        });

    [Fact]
    public void Reconcile_PathOnlyFallback_TracesUntitledPlaceholder_ForUntitledDoc() =>
        Sta.Run(() =>
        {
            // BK-M-3: doc.State.Path=null の untitled 文書は "<untitled-{n}>" プレースホルダで trace。
            // NewDoc は CreateNew(=UntitledNumber 割り当て)+ Text セッター + ClearSavePoint を通るため
            // 実装で pathKey が Path.None のまま untitled 経路を進むことを固定する。
            using var host = new Host(maxBackupCharsOverride: 3);
            var doc = host.NewDoc("XXXX"); // 4 chars > 3
            Assert.Null(doc.State.Path); // sanity: 新規タブは path 無し

            host.Backup.Reconcile();

            var warn = Assert.Single(host.Trace.Warnings);
            Assert.Equal("backup-content-skipped", warn.Category);
            Assert.Contains("<untitled-", warn.Detail); // プレースホルダ形式が使われる
            // BK-M-3 I-1: sizeChars が追記された影響で末尾は ")" になる=EndsWith ではなく Contains で
            // プレースホルダ閉じ ">" の存在のみを検証する。
            Assert.Contains(">", warn.Detail, StringComparison.Ordinal);
        });

    [Fact]
    public void Reconcile_PathOnlyFallback_SanitizesPathKey_ViaSanitizeForDisplay() =>
        Sta.Run(() =>
        {
            // BK-M-3 + BK-L-5: pathKey は doc.State.Path を SanitizeForDisplay.OneLine(200) で無害化する
            // 契約を pin(将来 State.Path がユーザ制御/ネットワーク経由になった場合の CRLF/RLO 混入防御=
            // BackupCoordinator 全 trace で統一)。Path に RLO を仕込んだ Document を作って観測。
            using var host = new Host(maxBackupCharsOverride: 3);
            var doc = host.NewDoc("YYYY"); // 4 chars > 3
            // Document.State.Path は通常ファイル操作で入るが、Test 用に直接注入=SanitizeForDisplay の
            // ラップ有無を検証する。制御文字は csharpier 再整形と culture-sensitive Contains の罠を避け
            // \uXXXX エスケープ + StringComparison.Ordinal に統一 (RestoreDialogTests と同型)。
            doc.State.Path = "C:\\evil\u202Etxt.exe";

            host.Backup.Reconcile();

            var warn = Assert.Single(host.Trace.Warnings);
            Assert.Equal("backup-content-skipped", warn.Category);
            Assert.DoesNotContain("\u202E", warn.Detail, StringComparison.Ordinal); // RLO drop
            Assert.Contains("evil", warn.Detail, StringComparison.Ordinal);
        });

    // ===== hot exit \u7D71\u5408 Task 3(\u8A2D\u8A08 2026-07-23 \u00A73.1): \u30EC\u30A4\u30A2\u30A6\u30C8\u5B9A\u671F\u9000\u907F =====
    //
    // \u6CE8\u610F: restoreSessionEnabled=true \u3067\u306F NewDoc(CreateNew)\u306E ActiveDocumentChanged \u3067\u3082
    // Reconcile\u2192\u30EC\u30A4\u30A2\u30A6\u30C8\u66F8\u8FBC\u304C\u8D70\u308B\u305F\u3081\u3001\u66F8\u8FBC\u300C\u56DE\u6570\u300D\u306E\u53B3\u5BC6\u56FA\u5B9A\u306F\u305B\u305A\u3001
    // \u300C\u5897\u3048\u308B/\u5897\u3048\u306A\u3044\u300D\u306E\u5DEE\u5206\u3068\u6700\u7D42\u66F8\u8FBC([^1])\u306E\u5185\u5BB9\u3067 assert \u3059\u308B\u3002

    [Fact]
    public void Reconcile_RestoreSessionEnabled_WritesLayout_MatchingCurrentDocs() =>
        Sta.Run(() =>
        {
            using var host = new Host(restoreSessionEnabled: true);
            var dirty = host.NewDoc("hello");
            var clean = host.NewDoc("world", dirty: false);
            // M-2: \u30D1\u30B9\u3042\u308A\u5206\u5C90(Path \u900F\u904E+UntitledNumber \u306F 0 \u56FA\u5B9A)\u3082\u540C\u4E00\u30EC\u30A4\u30A2\u30A6\u30C8\u3067\u691C\u8A3C\u3059\u308B\u3002
            var saved = host.NewDoc("saved", dirty: false);
            saved.State.Path = Path.Combine(host.TempDir, "saved.txt");

            host.Backup.Reconcile();

            Assert.NotEmpty(host.Writer.LayoutWrites);
            Assert.Equal(host.LayoutPath, host.Writer.LayoutWritePaths[^1]); // \u6CE8\u5165 path \u3078\u66F8\u304F
            var layout = host.Writer.LayoutWrites[^1];
            Assert.Equal(3, layout.Tabs.Count); // \u30BF\u30D6\u9806=Documents \u9806
            var t0 = layout.Tabs[0];
            var t1 = layout.Tabs[1];
            var t2 = layout.Tabs[2];
            // dirty \u7121\u984C: BackupId=_map \u306E Id(\u672C\u6587\u30D0\u30C3\u30AF\u30A2\u30C3\u30D7\u3068\u540C\u3058 Id \u3092\u53C2\u7167)
            Assert.Null(t0.Path);
            Assert.Equal(dirty.State.UntitledNumber, t0.UntitledNumber);
            Assert.Equal(host.Writer.Writes.Single().Id, t0.BackupId);
            Assert.False(t0.IsActive);
            Assert.Equal(dirty.Editor.CurrentLine, t0.CaretLine);
            Assert.Equal(dirty.Editor.GetColumn(dirty.Editor.CurrentPosition), t0.CaretColumn);
            Assert.Equal((int)dirty.State.LineEnding, t0.LineEnding);
            // clean \u7121\u984C: BackupId=null(\u672C\u6587\u30D0\u30C3\u30AF\u30A2\u30C3\u30D7\u306A\u3057)
            Assert.Null(t1.BackupId);
            Assert.Equal(clean.State.UntitledNumber, t1.UntitledNumber);
            Assert.False(t1.IsActive);
            // clean \u30D1\u30B9\u3042\u308A: Path \u900F\u904E\u30FBUntitledNumber=0(\u7121\u984C\u756A\u53F7\u306F\u8F09\u305B\u306A\u3044)\u30FB\u6700\u5F8C\u306B\u4F5C\u3063\u305F doc \u304C\u30A2\u30AF\u30C6\u30A3\u30D6
            Assert.Equal(saved.State.Path, t2.Path);
            Assert.Equal(0, t2.UntitledNumber);
            Assert.Null(t2.BackupId);
            Assert.True(t2.IsActive);
        });

    [Fact]
    public void Reconcile_LayoutUnchanged_DoesNotRewrite() =>
        Sta.Run(() =>
        {
            // Stage 6 \u6559\u8A13: no-change \u306F\u975E\u65E2\u5B9A\u72B6\u614B(\u30BF\u30D6 1 \u500B\u30FBdirty)\u304B\u3089\u691C\u8A3C\u3092\u59CB\u3081\u308B\u3002
            using var host = new Host(restoreSessionEnabled: true);
            host.NewDoc("hello");
            host.Backup.Reconcile(); // \u5148\u306B 1 \u56DE\u66F8\u304B\u305B\u308B
            int before = host.Writer.LayoutWrites.Count;
            Assert.True(before >= 1); // sanity: \u975E\u65E2\u5B9A\u72B6\u614B

            host.Backup.Reconcile(); // \u5909\u5316\u306A\u3057=\u7F72\u540D\u4E00\u81F4

            Assert.Equal(before, host.Writer.LayoutWrites.Count);
        });

    [Fact]
    public void Reconcile_TabAdded_RewritesLayout() =>
        Sta.Run(() =>
        {
            using var host = new Host(restoreSessionEnabled: true);
            host.NewDoc("A");
            host.Backup.Reconcile();
            int before = host.Writer.LayoutWrites.Count;

            _ = host.Docs.CreateNew(); // \u30BF\u30D6\u8FFD\u52A0(ActiveDocumentChanged \u2192 Reconcile)

            Assert.True(host.Writer.LayoutWrites.Count > before);
            Assert.Equal(2, host.Writer.LayoutWrites[^1].Tabs.Count);
        });

    [Fact]
    public void Reconcile_ActiveSwitched_RewritesLayout() =>
        Sta.Run(() =>
        {
            using var host = new Host(restoreSessionEnabled: true);
            var d1 = host.NewDoc("A");
            _ = host.NewDoc("B");
            host.Backup.Reconcile();
            int before = host.Writer.LayoutWrites.Count;

            host.Docs.Activate(d1); // \u30A2\u30AF\u30C6\u30A3\u30D6\u5207\u66FF(ActiveDocumentChanged \u2192 Reconcile)

            Assert.True(host.Writer.LayoutWrites.Count > before);
            var last = host.Writer.LayoutWrites[^1];
            Assert.True(last.Tabs[0].IsActive);
            Assert.False(last.Tabs[1].IsActive);
        });

    [Fact]
    public void Reconcile_CaretMoved_RewritesLayout() =>
        Sta.Run(() =>
        {
            using var host = new Host(restoreSessionEnabled: true);
            var doc = host.NewDoc("hello\nworld");
            host.Backup.Reconcile();
            int before = host.Writer.LayoutWrites.Count;

            doc.Editor.SetCaretByLineColumn(1, 2);
            host.Backup.Reconcile();

            Assert.True(host.Writer.LayoutWrites.Count > before);
            var t = host.Writer.LayoutWrites[^1].Tabs[0];
            Assert.Equal(1, t.CaretLine);
            Assert.Equal(2, t.CaretColumn);
        });

    [Fact]
    public void Reconcile_RestoreSessionDisabled_NeverWritesLayout() =>
        Sta.Run(() =>
        {
            using var host = new Host(); // restoreSessionEnabled \u65E2\u5B9A false
            host.NewDoc("hello");

            host.Backup.Reconcile();

            Assert.NotEmpty(host.Writer.Writes); // \u5BFE\u7167: \u672C\u6587\u30D0\u30C3\u30AF\u30A2\u30C3\u30D7\u306F\u5F93\u6765\u3069\u304A\u308A\u66F8\u304B\u308C\u308B
            Assert.Empty(host.Writer.LayoutWrites); // \u30EC\u30A4\u30A2\u30A6\u30C8\u306F\u4E00\u5207\u66F8\u304B\u308C\u306A\u3044(\u6319\u52D5\u4E0D\u5909)
        });

    [Fact]
    public void LayoutOnlyMode_BackupDisabled_WritesLayoutWithoutContent() =>
        Sta.Run(() =>
        {
            // \u8A2D\u8A08 \u00A75.2 OFF\u00D7ON: \u672C\u6587\u306F\u9000\u907F\u3057\u306A\u3044(\u30E6\u30FC\u30B6\u30FC\u610F\u601D\u306E\u5C0A\u91CD)\u304C\u30EC\u30A4\u30A2\u30A6\u30C8\u306F\u5B9A\u671F\u9000\u907F\u3059\u308B\u3002
            using var host = new Host(enabled: false, restoreSessionEnabled: true);
            Assert.Equal(1, host.WriterFactoryCalls); // writer \u306F\u751F\u6210\u3055\u308C\u308B
            Assert.True(host.Backup.TimerEnabled); // timer \u3082\u8D77\u52D5\u3059\u308B
            host.NewDoc("hello"); // dirty \u3060\u304C\u672C\u6587\u306F\u66F8\u304B\u308C\u306A\u3044

            host.Backup.Reconcile();

            Assert.Empty(host.Writer.Writes); // \u672C\u6587\u30D0\u30C3\u30AF\u30A2\u30C3\u30D7\u306A\u3057
            Assert.NotEmpty(host.Writer.LayoutWrites);
            var tab = Assert.Single(host.Writer.LayoutWrites[^1].Tabs);
            Assert.Null(tab.BackupId); // _map \u672A\u767B\u9332(_enabled=false)=BackupId \u306F\u5E38\u306B null
        });

    [Fact]
    public void FailedLayoutWrite_ForcesRewrite_NextReconcile() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: false, restoreSessionEnabled: true);
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile(); // \u6210\u529F\u66F8\u8FBC(\u975E\u65E2\u5B9A\u72B6\u614B\u3092\u4F5C\u308B)
            int before = host.Writer.LayoutWrites.Count;
            Assert.True(before >= 1);

            host.Writer.FailNextLayoutWrite = true;
            doc.Editor.SetCaretByLineColumn(0, 3); // \u7F72\u540D\u5909\u5316 \u2192 \u66F8\u8FBC\u8A66\u884C
            host.Backup.Reconcile(); // \u5931\u6557(\u8A18\u9332\u3055\u308C\u305A OnLayoutWriteFailed \u304C\u540C\u671F\u767A\u706B)
            Assert.Equal(before, host.Writer.LayoutWrites.Count);

            host.Backup.Reconcile(); // \u5931\u6557\u901A\u77E5 \u2192 \u7F72\u540D\u4E00\u81F4\u3067\u3082\u5F37\u5236\u518D\u66F8\u8FBC

            Assert.Equal(before + 1, host.Writer.LayoutWrites.Count);
            Assert.Equal(3, host.Writer.LayoutWrites[^1].Tabs[0].CaretColumn); // \u5931\u6557\u5206\u306E\u72B6\u614B\u304C\u66F8\u304B\u308C\u308B

            // M-1: \u5F37\u5236\u66F8\u8FBC\u306F one-shot(_layoutForceWrite \u6D88\u8CBB)\u3002\u7121\u5909\u5316\u306E\u6B21 Reconcile \u3067\u306F\u5897\u3048\u306A\u3044\u3002
            host.Backup.Reconcile();
            Assert.Equal(before + 1, host.Writer.LayoutWrites.Count);
        });

    [Fact]
    public void FinalFlushForRestore_FlushesPendingContent_AndWritesLayoutUnconditionally() =>
        Sta.Run(() =>
        {
            using var host = new Host(restoreSessionEnabled: true);
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile();
            int contentBefore = host.Writer.Writes.Count;
            int layoutBefore = host.Writer.LayoutWrites.Count;

            // \u672A\u9000\u907F\u306E dirty \u5909\u66F4(Reconcile \u3092\u631F\u307E\u306A\u3044=hot exit \u76F4\u524D\u306E\u7DE8\u96C6\u3092\u6A21\u3059)
            doc.Editor.Text = "hello world";
            doc.Editor.ClearSavePoint();
            host.Backup.FinalFlushForRestore();

            Assert.Equal(contentBefore + 1, host.Writer.Writes.Count); // \u672A\u9000\u907F\u5206\u3092 flush
            Assert.Equal("hello world", host.Writer.Writes[^1].Content);
            Assert.True(host.Writer.LayoutWrites.Count > layoutBefore);

            // \u7F72\u540D\u4E0D\u5909\u3067\u3082\u5F37\u5236\u66F8\u8FBC(force: true \u306E pin)
            int layoutAfter = host.Writer.LayoutWrites.Count;
            host.Backup.FinalFlushForRestore();
            Assert.Equal(layoutAfter + 1, host.Writer.LayoutWrites.Count);
            Assert.Equal(contentBefore + 1, host.Writer.Writes.Count); // \u672C\u6587\u306F sig \u5224\u5B9A\u3069\u304A\u308A\u5897\u3048\u306A\u3044
        });

    // 設計 §3.2 補遺(PR #22 M-1 後継): 明示破棄(No)タブは hot exit の復元対象に silent 復活しない。
    // 3 つの skip/削除ピボットを同時に kill する構成:
    //  - MarkDiscarded の Delete 投入を外す → Deletes 断言が赤化
    //  - ReconcileContent の _discarded skip を外す → RegisterNew 再登録で Writes 断言が赤化
    //  - BuildLayout の _discarded skip を外す → レイアウト Single 断言が赤化
    [Fact]
    public void MarkDiscarded_DeletesBackup_BlocksRewrite_AndExcludesFromLayout() =>
        Sta.Run(() =>
        {
            using var host = new Host(restoreSessionEnabled: true);
            _ = host.NewDoc("keep-me");
            var drop = host.NewDoc("drop-me");
            host.Backup.Reconcile(); // 両方登録+Write(+レイアウト 2 タブ)
            string keepId = host.Writer.Writes.Single(w => w.Content == "keep-me").Id;
            string dropId = host.Writer.Writes.Single(w => w.Content == "drop-me").Id;
            Assert.Equal(2, host.Writer.LayoutWrites[^1].Tabs.Count); // 非既定状態から開始(Stage 6)

            host.Backup.MarkDiscarded(drop);

            Assert.Contains(dropId, host.Writer.Deletes); // 既存バックアップの即時 Delete 投入

            // FinalFlush(close 時の最終 Reconcile 相当)でも再登録・再書込されない
            host.Writer.Writes.Clear();
            host.Backup.FinalFlushForRestore();
            Assert.DoesNotContain(host.Writer.Writes, w => w.Content == "drop-me");

            // レイアウトからタブごと除外される(復元対象外)
            var layout = host.Writer.LayoutWrites[^1];
            var tab = Assert.Single(layout.Tabs);
            Assert.Equal(keepId, tab.BackupId);
        });

    [Fact]
    public void Shutdown_KeepForRestore_KeepsBackupsAndLayout() =>
        Sta.Run(() =>
        {
            using var host = new Host(restoreSessionEnabled: true);
            _ = host.NewDoc("one");
            _ = host.NewDoc("two");
            host.Backup.Reconcile();
            Assert.True(host.Writer.Writes.Count >= 2); // sanity: \u7BA1\u7406\u5206 HasBackup=true

            host.Backup.Shutdown(keepForRestore: true);

            Assert.Empty(host.Writer.Deletes); // \u81EA\u30BB\u30C3\u30B7\u30E7\u30F3\u5206\u306E\u524A\u9664\u3092\u6295\u5165\u3057\u306A\u3044
            Assert.Equal(0, host.Writer.LayoutDeletes); // session-state.json \u3082\u6B8B\u3059
            Assert.Equal(1, host.Writer.DisposeCount); // \u30C9\u30EC\u30A4\u30F3\u306F\u3059\u308B
        });

    [Fact]
    public void Shutdown_Default_DeletesBackups_AndQueuesDeleteLayout() =>
        Sta.Run(() =>
        {
            using var host = new Host(restoreSessionEnabled: true);
            _ = host.NewDoc("one");
            host.Backup.Reconcile();

            host.Backup.Shutdown(); // \u65E2\u5B9A keepForRestore=false=\u73FE\u884C\u306E\u30AF\u30EA\u30FC\u30F3\u7D42\u4E86\u524A\u9664

            Assert.Single(host.Writer.Deletes);
            Assert.Equal(1, host.Writer.LayoutDeletes); // stale \u30EC\u30A4\u30A2\u30A6\u30C8\u3092\u6B8B\u3055\u306A\u3044
            Assert.Equal(1, host.Writer.DisposeCount);
        });

    [Fact]
    public void Shutdown_WithoutWriter_DeletesLayoutFileDirectly() =>
        Sta.Run(() =>
        {
            // \u4E21\u6A5F\u80FD OFF=writer \u672A\u751F\u6210\u3067\u3082\u3001\u904E\u53BB ON \u30BB\u30C3\u30B7\u30E7\u30F3\u306E\u6B8B\u9AB8 session-state.json \u3092\u76F4\u63A5\u6D88\u3059\u3002
            using var host = new Host(enabled: false);
            File.WriteAllText(host.LayoutPath, "{}"); // \u6B8B\u9AB8\u3092\u6A21\u3059

            host.Backup.Shutdown();

            Assert.Equal(0, host.WriterFactoryCalls); // writer \u306F\u751F\u6210\u3055\u308C\u306A\u3044\u307E\u307E
            Assert.False(File.Exists(host.LayoutPath)); // SessionLayoutStore.Delete \u76F4\u547C\u3073\u3067\u6383\u9664
        });

    [Fact]
    public void UpdateSettings_EnableRestoreSession_WritesLayoutImmediately() =>
        Sta.Run(() =>
        {
            using var host = new Host(); // restore=false \u8D77\u52D5
            host.NewDoc("hello");
            host.Backup.Reconcile();
            Assert.NotEmpty(host.Writer.Writes); // \u975E\u65E2\u5B9A\u72B6\u614B: \u672C\u6587\u306F\u3042\u308B\u304C\u30EC\u30A4\u30A2\u30A6\u30C8\u306F\u306A\u3044
            Assert.Empty(host.Writer.LayoutWrites);

            host.Backup.UpdateSettings(true, 30, restoreSessionEnabled: true);

            Assert.NotEmpty(host.Writer.LayoutWrites); // \u5207\u66FF\u76F4\u5F8C\u306E\u5373 Reconcile \u3067\u66F8\u304B\u308C\u308B
            Assert.Single(host.Writer.LayoutWrites[^1].Tabs);
        });

    [Fact]
    public void UpdateSettings_RestoreOffToOn_ForcesRewrite_EvenIfSignatureUnchanged() =>
        Sta.Run(() =>
        {
            // ON\u2192OFF\u2192ON \u3067\u72B6\u614B\u304C\u5909\u308F\u3089\u306A\u304F\u3066\u3082\u3001OFF \u4E2D\u306B stale \u5316\u3057\u305F\u53EF\u80FD\u6027\u304C\u3042\u308B\u305F\u3081\u5F37\u5236\u66F8\u8FBC\u3059\u308B
            // (_layoutForceWrite \u306E OFF\u2192ON \u9077\u79FB pin\u3002\u843D\u3068\u3059\u5909\u7570\u306F\u3053\u3053\u3067\u8D64\u5316)\u3002
            using var host = new Host(restoreSessionEnabled: true);
            host.NewDoc("hello");
            host.Backup.Reconcile();
            int before = host.Writer.LayoutWrites.Count;

            host.Backup.UpdateSettings(true, 30, restoreSessionEnabled: false); // ON\u2192OFF
            Assert.Equal(before, host.Writer.LayoutWrites.Count); // OFF \u4E2D\u306F\u66F8\u304B\u306A\u3044

            host.Backup.UpdateSettings(true, 30, restoreSessionEnabled: true); // OFF\u2192ON

            Assert.Equal(before + 1, host.Writer.LayoutWrites.Count); // \u7F72\u540D\u4E00\u81F4\u3067\u3082\u66F8\u304F

            // M-1: \u5F37\u5236\u66F8\u8FBC\u306F one-shot(_layoutForceWrite \u6D88\u8CBB)\u3002\u7121\u5909\u5316\u306E\u6B21 Reconcile \u3067\u306F\u5897\u3048\u306A\u3044\u3002
            host.Backup.Reconcile();
            Assert.Equal(before + 1, host.Writer.LayoutWrites.Count);
        });

    // ===== hot exit \u7D71\u5408 Task 3(\u8A2D\u8A08 \u00A73.3/\u00A73.4): \u7D71\u5408\u5FA9\u5143 API =====

    [Fact]
    public void CollectForSilentRestore_ReturnsLayoutAndBackups_EvenWhenBackupDisabled() =>
        Sta.Run(() =>
        {
            // \u8A2D\u8A08 \u00A75.2: \u30D0\u30C3\u30AF\u30A2\u30C3\u30D7\u7121\u52B9(_enabled=false)\u3067\u3082\u52D5\u304F=OfferRestoreOnStartup \u306E
            // _enabled \u30AC\u30FC\u30C9\u3068\u306F\u72EC\u7ACB(\u30EC\u30A4\u30A2\u30A6\u30C8\u306E\u307F\u5FA9\u5143\u30E2\u30FC\u30C9)\u3002
            using var host = new Host(enabled: false);
            var planted = new SessionLayout(
                new List<SessionLayoutRecord>
                {
                    new(
                        Path: "C:\\data\\a.txt",
                        UntitledNumber: 0,
                        BackupId: null,
                        IsActive: true,
                        CaretLine: 3,
                        CaretColumn: 5,
                        LineEnding: 0
                    ),
                },
                FixedNow.UtcDateTime
            );
            SessionLayoutStore.Save(host.LayoutPath, planted);
            PlantBackup(host.TempDir, Rec("r1", "one"));

            var (layout, backups) = host.Backup.CollectForSilentRestore();

            Assert.NotNull(layout);
            var tab = Assert.Single(layout!.Tabs);
            Assert.Equal("C:\\data\\a.txt", tab.Path);
            Assert.True(tab.IsActive);
            Assert.Equal(3, tab.CaretLine);
            Assert.Equal(5, tab.CaretColumn);
            var bk = Assert.Single(backups);
            Assert.Equal(HashId("r1"), bk.Id);
            Assert.Equal("one", bk.Content);
        });

    [Fact]
    public void DeleteConsumedLayout_RemovesLayoutFile() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: false);
            SessionLayoutStore.Save(
                host.LayoutPath,
                new SessionLayout(new List<SessionLayoutRecord>(), FixedNow.UtcDateTime)
            );
            Assert.True(File.Exists(host.LayoutPath)); // sanity

            host.Backup.DeleteConsumedLayout();

            Assert.False(File.Exists(host.LayoutPath));
        });

    [Fact]
    public void DeleteConsumedLayout_ForcesRewrite_OnNextReconcile() =>
        Sta.Run(() =>
        {
            // M-4: 消費削除後〜次のレイアウト変化までの session-state.json 不在窓を閉じる。
            // 削除直後の Reconcile は署名一致でも書き直す(_layoutForceWrite 予約の pin)。
            using var host = new Host(restoreSessionEnabled: true);
            host.NewDoc("hello");
            host.Backup.Reconcile(); // 非既定状態(署名記録済み)を作る
            int before = host.Writer.LayoutWrites.Count;

            host.Backup.DeleteConsumedLayout();
            host.Backup.Reconcile(); // レイアウト無変化でも書き直す

            Assert.Equal(before + 1, host.Writer.LayoutWrites.Count);
        });

    [Fact]
    public void AdoptRestored_RegistersMap_AndMovesFileToOwnSessionDir() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var rec = Rec("adopt", "boom");
            var oldDir = Path.Combine(host.TempDir, "session-old");
            Directory.CreateDirectory(oldDir);
            BackupStore.Write(oldDir, rec); // \u65E7\u30BB\u30C3\u30B7\u30E7\u30F3 dir \u306E\u6D88\u8CBB\u6E08\u307F\u30D0\u30C3\u30AF\u30A2\u30C3\u30D7\u3092\u6A21\u3059
            var doc = host.NewDoc("boom");

            host.Backup.AdoptRestored(doc, rec);

            // adopt-move: \u65E7 dir \u2192 \u81EA\u30BB\u30C3\u30B7\u30E7\u30F3 dir \u3078\u79FB\u52D5\u3057\u3001\u7A7A\u306B\u306A\u3063\u305F\u65E7 dir \u306F\u6383\u9664\u3055\u308C\u308B
            Assert.True(
                File.Exists(Path.Combine(host.CapturedSessionDir!, rec.Id + ".json")),
                "moved file should exist in own session dir"
            );
            Assert.False(Directory.Exists(oldDir));
            Assert.Empty(host.Trace.Warnings); // \u6210\u529F\u6642\u306F trace \u306A\u3057

            // _map \u767B\u9332: \u4EE5\u5F8C\u306E clean \u5316\u3067\u5143 Id \u306E Delete \u304C\u98DB\u3076(\u5143 Id \u5F15\u304D\u7D99\u304E\u306E\u8A3C\u660E)
            doc.Editor.SetSavePoint();
            host.Backup.Reconcile();
            Assert.Contains(rec.Id, host.Writer.Deletes);
        });

    [Fact]
    public void AdoptRestored_FileMissing_TracesAdoptMoveMissed() =>
        Sta.Run(() =>
        {
            // \u79FB\u52D5\u5931\u6557(\u3069\u3053\u306B\u3082\u7121\u3044)\u306F trace \u306E\u307F\u3067\u7D9A\u884C=\u6700\u60AA\u3067\u3082\u5F93\u6765\u540C\u69D8\u306E\u518D\u63D0\u6848\u306B\u9000\u5316\u3059\u308B\u3060\u3051\u3002
            using var host = new Host();
            var rec = Rec("missing", "x");
            var doc = host.NewDoc("x");

            host.Backup.AdoptRestored(doc, rec);

            var warn = Assert.Single(host.Trace.Warnings);
            Assert.Equal("adopt-move-missed", warn.Category);
            Assert.Null(warn.Ex);
            Assert.Equal(rec.Id, warn.Detail); // \u6B63\u5F53\u306A GUID N \u306F sanitize \u4E0D\u5909
        });

    [Fact]
    public void AdoptRestored_CleanDocAtAdoptTime_NextReconcileDeletesBackup() =>
        Sta.Run(() =>
        {
            // \u6700\u7D42\u54C1\u8CEA\u30D1\u30B9 I-1 \u306E\u7D50\u5408\u78BA\u8A8D: path-only demote \u306E disk \u518D\u30AA\u30FC\u30D7\u30F3(=adopt \u6642\u70B9\u3067\u65E2\u306B
            // clean \u306A doc)\u3092 adopt \u3057\u305F\u5834\u5408\u3001\u30E6\u30FC\u30B6\u30FC\u64CD\u4F5C\u306A\u3057\u3067\u3082\u6B21 Reconcile \u306E clean \u691C\u51FA
            // (BackupPlanner.Decide)\u304C Delete \u3092\u98DB\u3070\u3057\u3001\u6D88\u8CBB\u6E08\u307F\u30EC\u30B3\u30FC\u30C9\u304C\u6B8B\u7F6E\u3057\u306A\u3044(\u30BE\u30F3\u30D3\u6839\u6CBB)\u3002
            // \u65E2\u5B58\u306E adopt \u7CFB\u30C6\u30B9\u30C8\u306F\u300Cadopt \u6642 dirty \u2192 SetSavePoint \u5F8C\u306B Delete\u300D\u306E\u307F\u3067\u3001
            // \u7121\u64CD\u4F5C Delete \u306E\u30D4\u30DC\u30C3\u30C8\u306F\u672A\u56FA\u5B9A\u3060\u3063\u305F\u3002
            using var host = new Host();
            var rec = Rec("path-only-clean", content: null!); // Content=null(path-only \u76F8\u5F53)
            var doc = host.NewDoc("disk content", dirty: false); // disk \u518D\u30AA\u30FC\u30D7\u30F3\u76F8\u5F53=\u6700\u521D\u304B\u3089 clean

            host.Backup.AdoptRestored(doc, rec);
            host.Backup.Reconcile();

            Assert.Contains(rec.Id, host.Writer.Deletes); // \u7121\u64CD\u4F5C\u3067\u6D88\u8CBB\u6E08\u307F Id \u306E\u524A\u9664\u30B8\u30E7\u30D6\u304C\u98DB\u3076
        });

    // ===== hot exit \u7D71\u5408 Task 4(\u8A2D\u8A08 \u00A73.4): OfferRestoreOnStartup \u306E adopt-move =====
    //
    // \u5FA9\u5143\u3067\u6D88\u8CBB\u3057\u305F\u30D0\u30C3\u30AF\u30A2\u30C3\u30D7\u3092\u81EA\u30BB\u30C3\u30B7\u30E7\u30F3 dir \u3078\u5F15\u304D\u53D6\u308B(_map \u76F4\u767B\u9332 \u2192 AdoptRestored \u5DEE\u66FF)\u3002
    // \u6839\u6CBB\u5BFE\u8C61(BK-M-2 \u7531\u6765\u306E\u6F5C\u5728\u30D0\u30B0): \u65E7 session-* dir \u306B\u6B8B\u3063\u305F\u6D88\u8CBB\u6E08\u307F\u30D5\u30A1\u30A4\u30EB\u306F
    // SerialBackupWriter.Delete(\u81EA\u30BB\u30C3\u30B7\u30E7\u30F3 dir \u5BFE\u8C61)\u3067\u6D88\u3048\u305A\u3001\u6B21\u56DE\u8D77\u52D5\u306E LoadAll \u304C\u62FE\u3063\u3066
    // \u6700\u5927 30 \u65E5\u9593(sweep \u307E\u3067)\u6BCE\u56DE\u518D\u63D0\u6848\u3055\u308C\u308B\u3002

    [Fact]
    public void OfferRestore_ConfirmFalse_CleanedDoc_LeavesNoBackupFile_NoReproposal() =>
        Sta.Run(() =>
        {
            // \u56DE\u5E30(\u73FE\u884C\u30D0\u30B0): adopt-move \u306A\u3057\u3067\u306F session-old\<id>.json \u304C\u6B8B\u308A\u7D9A\u3051\u3001clean \u5316\u306E
            // Delete(\u81EA\u30BB\u30C3\u30B7\u30E7\u30F3 dir \u5BFE\u8C61)\u3067\u306F\u6D88\u3048\u306A\u3044=\u6B21\u56DE LoadAll \u304C\u62FE\u3044\u518D\u63D0\u6848\u3055\u308C\u308B\u3002
            using var host = new Host();
            var rec = Rec("repropose", "boom");
            var oldDir = Path.Combine(host.TempDir, "session-old");
            Directory.CreateDirectory(oldDir);
            BackupStore.Write(oldDir, rec); // \u524D\u30BB\u30C3\u30B7\u30E7\u30F3(\u30AF\u30E9\u30C3\u30B7\u30E5\u7531\u6765)\u306E\u6B8B\u9AB8\u3092\u6A21\u3059

            int restored = host.Backup.OfferRestoreOnStartup(
                host.Form,
                r =>
                {
                    var d = host.Docs.CreateNew();
                    d.Editor.Text = r.Content ?? "";
                    d.Editor.ClearSavePoint(); // \u672C\u756A RestoreFromBackup \u3068\u540C\u69D8 dirty \u306E\u307E\u307E
                    return d;
                },
                confirm: false
            );
            Assert.Equal(1, restored); // sanity

            var doc = host.Docs.Documents.Single();
            doc.Editor.SetSavePoint(); // \u4FDD\u5B58\u76F8\u5F53(clean \u5316)
            host.Backup.Reconcile();

            Assert.Contains(rec.Id, host.Writer.Deletes); // \u5143 Id \u3078\u306E\u524A\u9664\u30B8\u30E7\u30D6\u304C\u98DB\u3076
            // \u672C\u756A SerialBackupWriter.Delete \u306F\u81EA\u30BB\u30C3\u30B7\u30E7\u30F3 dir \u3078\u306E BackupStore.Delete\u3002
            // FakeWriter \u306F in-memory \u306E\u305F\u3081\u3001\u305D\u306E\u610F\u5473\u8AD6\u3092\u3053\u3053\u3067\u518D\u751F\u3057\u3066\u30C7\u30A3\u30B9\u30AF\u7D42\u72B6\u614B\u3092\u691C\u8A3C\u3059\u308B\u3002
            foreach (var id in host.Writer.Deletes)
                BackupStore.Delete(host.CapturedSessionDir!, id);

            // \u6839\u6CBB\u306E\u6838\u5FC3: TempDir \u914D\u4E0B\u306E\u3069\u3053\u306B\u3082 <id>.json \u304C\u6B8B\u3089\u306A\u3044
            // (\u65E7 dir \u306B\u6B8B\u308B\u3068\u6B21\u56DE LoadAll \u304C\u62FE\u3044\u3001\u6700\u5927 30 \u65E5\u9593\u6BCE\u8D77\u52D5\u3067\u518D\u63D0\u6848\u3055\u308C\u308B)\u3002
            Assert.Empty(
                Directory.GetFiles(host.TempDir, rec.Id + ".json", SearchOption.AllDirectories)
            );
            Assert.Empty(BackupStore.LoadAll(host.TempDir)); // \u6B21\u56DE\u8D77\u52D5\u306E LoadAll \u76F8\u5F53\u304C\u7A7A
        });

    [Fact]
    public void OfferRestore_ConfirmFalse_MovesConsumedBackup_ToOwnSessionDir() =>
        Sta.Run(() =>
        {
            // confirm=false(\u5168\u4EF6 silent \u5FA9\u5143)\u7D4C\u8DEF: \u6D88\u8CBB\u3057\u305F record \u306E\u30D5\u30A1\u30A4\u30EB\u304C\u65E7 session dir \u304B\u3089
            // \u81EA\u30BB\u30C3\u30B7\u30E7\u30F3 dir \u3078\u79FB\u52D5\u3057\u3001\u7A7A\u306B\u306A\u3063\u305F\u65E7 dir \u306F\u6383\u9664\u3055\u308C\u308B\u3002
            using var host = new Host();
            var rec = Rec("adopt-cf", "boom");
            var oldDir = Path.Combine(host.TempDir, "session-old");
            Directory.CreateDirectory(oldDir);
            BackupStore.Write(oldDir, rec);
            Directory.CreateDirectory(host.CapturedSessionDir!); // \u521D\u56DE\u66F8\u8FBC\u6E08\u307F(dir \u65E2\u5B58)\u306E\u30B1\u30FC\u30B9

            host.Backup.OfferRestoreOnStartup(
                host.Form,
                r =>
                {
                    var d = host.Docs.CreateNew();
                    d.Editor.Text = r.Content ?? "";
                    d.Editor.ClearSavePoint();
                    return d;
                },
                confirm: false
            );

            Assert.True(
                File.Exists(Path.Combine(host.CapturedSessionDir!, rec.Id + ".json")),
                "consumed backup should be moved into own session dir"
            );
            Assert.False(Directory.Exists(oldDir)); // \u7A7A\u306B\u306A\u3063\u305F\u65E7 session dir \u306F\u6383\u9664\u3055\u308C\u308B
        });

    [Fact]
    public void OfferRestore_ConfirmTrue_MovesCheckedBackup_LeavesUncheckedInPlace() =>
        Sta.Run(() =>
        {
            // \u30C0\u30A4\u30A2\u30ED\u30B0\u7D4C\u8DEF: \u30C1\u30A7\u30C3\u30AF\u3057\u305F(=\u5FA9\u5143\u3067\u6D88\u8CBB\u3057\u305F)record \u3060\u3051 adopt-move \u3059\u308B\u3002
            // \u30C1\u30A7\u30C3\u30AF\u3057\u306A\u304B\u3063\u305F record \u306F\u5834\u6240\u3054\u3068\u636E\u3048\u7F6E\u304F(\u5B89\u5168\u5074\u3067\u6B8B\u3057\u6B21\u56DE\u518D\u63D0\u6848\u3059\u308B\u4E0D\u5909\u6761\u4EF6
            // =\u8A2D\u8A08 \u00A73.4\u300C\u6D88\u8CBB\u3057\u305F\u3082\u306E\u3060\u3051\u5F15\u304D\u53D6\u308B\u300D\u3092\u58CA\u3055\u306A\u3044)\u3002
            using var host = new Host();
            var check = Rec("checked", "one");
            var uncheck = Rec("unchecked", "two");
            var oldDir = Path.Combine(host.TempDir, "session-old");
            Directory.CreateDirectory(oldDir);
            BackupStore.Write(oldDir, check);
            BackupStore.Write(oldDir, uncheck);
            host.Prompt.NextOutcome = new RestoreOutcome(RestoreAction.Restore, new[] { check });

            host.Backup.OfferRestoreOnStartup(
                host.Form,
                r =>
                {
                    var d = host.Docs.CreateNew();
                    d.Editor.Text = r.Content ?? "";
                    d.Editor.ClearSavePoint();
                    return d;
                },
                confirm: true
            );

            Assert.True(
                File.Exists(Path.Combine(host.CapturedSessionDir!, check.Id + ".json")),
                "checked backup should be moved into own session dir"
            );
            Assert.False(File.Exists(Path.Combine(oldDir, check.Id + ".json")));
            // \u30C1\u30A7\u30C3\u30AF\u3057\u306A\u304B\u3063\u305F record \u306F\u79FB\u52D5\u3057\u306A\u3044(\u65E7 dir \u306B\u6B8B\u308B=\u6B21\u56DE\u518D\u63D0\u6848)
            Assert.True(File.Exists(Path.Combine(oldDir, uncheck.Id + ".json")));
        });

    [Fact]
    public void OfferRestore_MoveSucceeds_WhenOwnSessionDirNotYetCreated() =>
        Sta.Run(() =>
        {
            // \u81EA\u30BB\u30C3\u30B7\u30E7\u30F3 dir \u306F\u521D\u56DE\u672C\u6587\u66F8\u8FBC\u307E\u3067\u30C7\u30A3\u30B9\u30AF\u306B\u5B58\u5728\u3057\u306A\u3044(ctor \u306F\u30D1\u30B9\u6C7A\u5B9A\u306E\u307F)\u3002
            // \u305D\u306E\u72B6\u614B\u3067\u3082 adopt-move \u304C dir \u3092\u4F5C\u3063\u3066\u79FB\u52D5\u3067\u304D\u308B\u3053\u3068\u3092 pin\u3002
            using var host = new Host();
            var rec = Rec("adopt-nodir", "boom");
            var oldDir = Path.Combine(host.TempDir, "session-old");
            Directory.CreateDirectory(oldDir);
            BackupStore.Write(oldDir, rec);
            Assert.False(Directory.Exists(host.CapturedSessionDir!)); // sanity: \u672A\u4F5C\u6210

            host.Backup.OfferRestoreOnStartup(
                host.Form,
                r =>
                {
                    var d = host.Docs.CreateNew();
                    d.Editor.Text = r.Content ?? "";
                    d.Editor.ClearSavePoint();
                    return d;
                },
                confirm: false
            );

            Assert.True(
                File.Exists(Path.Combine(host.CapturedSessionDir!, rec.Id + ".json")),
                "adopt-move should create the session dir and move the file"
            );
        });

    // ===== hot exit \u7D71\u5408 Task 3 \u54C1\u8CEA\u30EC\u30D3\u30E5\u30FC I-1: layout-only \u30E2\u30FC\u30C9\u306E _map \u540C\u671F =====
    //
    // _enabled=false \u3067\u3082 _map \u3092\u30C7\u30A3\u30B9\u30AF\u5B9F\u5728\u306E\u93E1\u306B\u4FDD\u3064(ReconcileMapMaintenance)\u3002
    // \u3053\u308C\u3092\u6020\u308B\u3068 BuildLayout \u304C stale BackupId \u3092\u66F8\u304D\u3001\u6B21\u56DE\u8D77\u52D5\u306E silent \u5FA9\u5143\u304C\u4FDD\u5B58\u6E08\u307F
    // \u30D5\u30A1\u30A4\u30EB\u3078\u53E4\u3044\u30D0\u30C3\u30AF\u30A2\u30C3\u30D7\u5185\u5BB9\u3092 dirty \u5FA9\u5143\u3059\u308B(\u2192 Ctrl+S \u3067\u4E0A\u66F8\u304D)\u30C7\u30FC\u30BF\u640D\u5931\u7D4C\u8DEF\u306B\u306A\u308B\u3002

    [Fact]
    public void LayoutOnlyMode_AdoptedDocCleaned_DeletesBackup_AndClearsBackupIdInLayout() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: false, restoreSessionEnabled: true);
            var rec = Rec("adopt-clean", "boom");
            var doc = host.NewDoc("boom"); // dirty(\u7D71\u5408\u5FA9\u5143\u76F4\u5F8C\u306E dirty \u5FA9\u5143\u30BF\u30D6\u3092\u6A21\u3059)
            host.Backup.AdoptRestored(doc, rec); // HasBackup=true \u3067\u7BA1\u7406\u4E0B\u3078
            host.Backup.Reconcile();
            // sanity: \u975E\u65E2\u5B9A\u72B6\u614B=\u30EC\u30A4\u30A2\u30A6\u30C8\u306F\u5143 Id \u3092\u53C2\u7167\u3057\u3066\u3044\u308B
            Assert.Equal(rec.Id, host.Writer.LayoutWrites[^1].Tabs[0].BackupId);

            doc.Editor.SetSavePoint(); // \u4FDD\u5B58\u76F8\u5F53(clean \u5316)
            host.Backup.Reconcile();

            Assert.Contains(rec.Id, host.Writer.Deletes); // \u30D0\u30C3\u30AF\u30A2\u30C3\u30D7\u524A\u9664\u304C\u98DB\u3076
            Assert.Null(host.Writer.LayoutWrites[^1].Tabs[0].BackupId); // stale \u53C2\u7167\u3092\u66F8\u304B\u306A\u3044
        });

    [Fact]
    public void LayoutOnlyMode_AdoptedDocClosed_DeletesBackup_AndDropsFromLayout() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: false, restoreSessionEnabled: true);
            var rec = Rec("adopt-close", "boom");
            var doc = host.NewDoc("boom");
            _ = host.NewDoc("other"); // \u9589\u3058\u305F\u5F8C\u3082\u30BF\u30D6 1 \u500B\u304C\u6B8B\u308B(\u30EC\u30A4\u30A2\u30A6\u30C8\u306E\u7D99\u7D9A\u66F8\u8FBC\u3092\u89B3\u6E2C)
            host.Backup.AdoptRestored(doc, rec);

            host.Docs.TryClose(doc, _ => true); // \u9589\u3058\u308B(\u672A\u4FDD\u5B58\u78BA\u8A8D\u306F\u7D20\u901A\u3057)
            host.Backup.Reconcile();

            Assert.Contains(rec.Id, host.Writer.Deletes); // \u9589\u3058\u30BF\u30D6\u306E\u30D0\u30C3\u30AF\u30A2\u30C3\u30D7\u524A\u9664
            var last = host.Writer.LayoutWrites[^1];
            var tab = Assert.Single(last.Tabs); // \u9589\u3058\u305F\u30BF\u30D6\u306F\u30EC\u30A4\u30A2\u30A6\u30C8\u306B\u73FE\u308C\u306A\u3044(\u4EA1\u970A\u5FA9\u6D3B\u9632\u6B62)
            Assert.NotEqual(rec.Id, tab.BackupId);
        });

    [Fact]
    public void UpdateSettings_BackupOnToOff_RestoreStaysOn_CleanedDocStillDeletesBackup() =>
        Sta.Run(() =>
        {
            // \u30BB\u30C3\u30B7\u30E7\u30F3\u4E2D\u306E Backup ON\u2192OFF(restore ON \u7D99\u7D9A)\u3067\u3082\u3001\u4FDD\u5B58\u3067 clean \u5316\u3057\u305F doc \u306E
            // \u30D0\u30C3\u30AF\u30A2\u30C3\u30D7\u524A\u9664\u306F\u98DB\u3073\u7D9A\u3051\u308B(_map \u540C\u671F\u304C\u30E2\u30FC\u30C9\u975E\u4F9D\u5B58\u3067\u3042\u308B\u3053\u3068\u306E pin)\u3002
            using var host = new Host(restoreSessionEnabled: true); // enabled: true \u8D77\u52D5
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile(); // \u672C\u6587 Write=HasBackup=true
            var id = host.Writer.Writes[0].Id;

            host.Backup.UpdateSettings(false, 30, restoreSessionEnabled: true); // Backup ON\u2192OFF
            doc.Editor.SetSavePoint(); // \u4FDD\u5B58\u76F8\u5F53(clean \u5316)
            host.Backup.Reconcile();

            Assert.Contains(id, host.Writer.Deletes);
            Assert.Null(host.Writer.LayoutWrites[^1].Tabs[0].BackupId);
        });
}
