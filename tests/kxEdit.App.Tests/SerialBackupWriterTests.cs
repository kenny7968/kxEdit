using System.IO;
using kxEdit.Core.Backup;

namespace kxEdit.App.Tests;

/// <summary>
/// Phase 2 レビュー Critical 級の回収: SerialBackupWriter の実書込パイプライン統合テスト。
/// BackupCoordinator の Fake 差し替えテストでは触れられない「実 I/O(BackupStore.Write)・
/// 実背景スレッド(BlockingCollection+ Thread)・実 Dispose ドレイン(CompleteAdding+Join)」を
/// 統合レベルで固定する。責務=ワーカー外側 catch(ワーカー死の防波堤)・Dispose ドレイン契約・
/// Enqueue 締切/破棄後ガード(xmldoc「破棄後は無視」)・Write catch→OnWriteFailed 実発火。
///
/// 決定化の原則:待ちは一切入れない。全テストは「投入 → Dispose(=CompleteAdding+Join で
/// ドレイン完了が同期確定)→ ディスク/コールバックを assert」の形に統一。Sleep/リトライループ
/// 禁止。ディレクトリはテスト毎に <see cref="Directory.CreateTempSubdirectory"/> で完全隔離。
/// </summary>
public class SerialBackupWriterTests
{
    /// <summary>テスト毎に使い捨ての一時フォルダ(BackupStore の実 I/O が触るディスク領域)。
    /// FileControllerTests.TempDir と同じ流儀。掃除失敗はテスト失敗にしない(ReadOnly 属性等)。</summary>
    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("kxEditSbw_").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { /* 掃除失敗は無害 */
            }
        }
    }

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

    /// <summary>テスト用の BackupRecord ファクトリ(BackupCoordinatorTests.Rec と同じ形)。
    /// TimestampUtc は固定値でロードバック検証を deterministic に。
    /// label は HashId で 32 桁 hex GUID N に写す(BackupStore.LoadAll の白リストを通過させる)。</summary>
    private static BackupRecord Rec(string label, string content) =>
        new(
            Id: HashId(label),
            OriginalPath: null,
            UntitledNumber: 1,
            CodePage: 65001,
            HasBom: false,
            LineEndingId: 0,
            Content: content,
            TimestampUtc: new DateTime(2026, 07, 15, 12, 0, 0, DateTimeKind.Utc)
        );

    // ===== ドレイン契約(CompleteAdding+Join で保留ジョブがディスクに現れる) =====

    /// <summary>
    /// Dispose ドレイン契約の核。Write を 2 件投入して Dispose で締切→ Join(15s)で
    /// 背景スレッドが CompleteAdding 後の残ジョブを全消化 → BackupStore.LoadAll が両方見える。
    /// これが崩れると「終了直前の未保存文書が退避漏れ」の重篤バグに直結する。
    /// </summary>
    [Fact]
    public void Write_ThenDispose_DrainsToDisk()
    {
        using var tmp = new TempDir();
        var r1 = Rec("id-1", "one");
        var r2 = Rec("id-2", "two");

        using (var w = new SerialBackupWriter(tmp.Root))
        {
            w.Write(r1);
            w.Write(r2);
        } // using 脱出 = Dispose = CompleteAdding + Join でドレイン完了が同期確定

        var loaded = BackupStore.LoadAll(tmp.Root);
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, r => r.Id == HashId("id-1") && r.Content == "one");
        Assert.Contains(loaded, r => r.Id == HashId("id-2") && r.Content == "two");
    }

    /// <summary>
    /// 投入順の逐次実行契約(BlockingCollection は FIFO・単一 worker なので Write→Delete は必ずこの順)。
    /// Write→Delete(同 Id)→Dispose の後、ディスクに残らないこと=Delete が Write の後に実行された証拠。
    /// これが崩れると「削除ジョブが書込ジョブを追い越し=消したはずのバックアップが残る」に直結。
    /// </summary>
    [Fact]
    public void WriteThenDelete_SameId_EndsAbsent()
    {
        using var tmp = new TempDir();
        var rec = Rec("id-x", "will-be-deleted");

        using (var w = new SerialBackupWriter(tmp.Root))
        {
            w.Write(rec);
            w.Delete(rec.Id);
        } // Dispose で両ジョブとも投入順に消化

        Assert.Empty(BackupStore.LoadAll(tmp.Root));
    }

    /// <summary>
    /// DeleteAll ジョブが投入順に BackupStore.DeleteAll に到達し、既存の *.json を全削除する。
    /// 責務=「復元ダイアログの『すべて破棄』分岐」に対する統合パイプ担保。
    /// </summary>
    [Fact]
    public void DeleteAll_RemovesEverything()
    {
        using var tmp = new TempDir();

        using (var w = new SerialBackupWriter(tmp.Root))
        {
            w.Write(Rec("a", "1"));
            w.Write(Rec("b", "2"));
            w.Write(Rec("c", "3"));
            w.DeleteAll();
        }

        Assert.Empty(BackupStore.LoadAll(tmp.Root));
    }

    // ===== 失敗回復 & ワーカー生存(1 件の書込失敗が後続ジョブを巻き添えにしない=内側 catch 経路) =====

    /// <summary>
    /// BackupStore.Write の実失敗経路を OnWriteFailed が実発火し、かつ後続ジョブが処理される
    /// (=Run の内側 catch=`try { job(); } catch { }` により、失敗ジョブの後も worker が生存して
    /// 後続ジョブを実行できる)ことを固定する。
    ///
    /// 失敗経路の設計: BackupStore.Write は AtomicFile.Write を呼び、tmp を書いてから
    /// File.Move(tmp, "&lt;id&gt;.json") する(初回書込=File.Exists=false 分岐)。ターゲットの
    /// "&lt;id&gt;.json" が既にディレクトリとして存在すると File.Move は決定的に IOException を
    /// 投げる(実測=「既に存在するファイルを作成することはできません」)。他の失敗経路
    /// (権限/ディスクフル)は環境依存で不安定なため、これが最も deterministic。
    ///
    /// ワーカー生存検証: 失敗ジョブの後に Delete("harmless") を投入し、Dispose のドレインが
    /// 15s 以内に戻る(=worker が生きていて CompleteAdding 後に foreach を抜けた)ことを暗黙に確認。
    ///
    /// 未固定領域(将来の別テスト): Run の外側 catch(MoveNext-vs-Dispose race の防波堤=
    /// GetConsumingEnumerable の MoveNext 側で出る ObjectDisposedException を握り潰す)は、
    /// 現行の Dispose 順序(_worker.Join → finished 時のみ _queue.Dispose)では race が
    /// 実質発生せず、本テストからは直接固定できない。Dispose 順序を変更するリファクタが
    /// 入るタイミングで、外側 catch を kill する別テストを立てる。
    /// </summary>
    [Fact]
    public void WriteFailure_InvokesOnWriteFailed_AndWorkerSurvives()
    {
        using var tmp = new TempDir();
        // "<HashId(will-fail)>.json" と同名のディレクトリを事前作成 → File.Move が決定的に失敗する経路
        // (HIGH-1 導入後は Id が GUID N になるためファイル名も HashId 経由で組み立てる)。
        Directory.CreateDirectory(Path.Combine(tmp.Root, HashId("will-fail") + ".json"));

        var failures = new List<string>();
        var lockObj = new object();
        // OnWriteFailed は SerialBackupWriter.cs:22 のフックで、背景スレッドから発火する
        // (Write の catch:33-34 内で Invoke)。テスト側の記録は lock で保護する。
        Exception? disposeException = null;

        using (
            var w = new SerialBackupWriter(tmp.Root)
            {
                OnWriteFailed = id =>
                {
                    lock (lockObj)
                        failures.Add(id);
                },
            }
        )
        {
            var badRec = Rec("will-fail", "boom");
            w.Write(badRec);
            // 失敗後に別ジョブを投入して worker が動いていることを確認(Delete は正常な dir 内で no-op)。
            w.Delete("nonexistent-id");

            // Dispose が 15s Join 上限内に戻ること(=worker が生きていて素直に終わった)を後段で確認。
            try
            {
                w.Dispose();
            }
            catch (Exception ex)
            {
                disposeException = ex;
            }
        }

        Assert.Null(disposeException); // Dispose が例外なく戻る=worker 死んでいない
        lock (lockObj)
            Assert.Contains(HashId("will-fail"), failures); // 失敗コールバックが Id 付きで発火
        // 失敗した *.json は書き込まれていない(BackupStore.LoadAll は will-fail ディレクトリを *.json glob で拾うが
        // Directory.EnumerateFiles はディレクトリを列挙しないためスキップされる=空)。
        Assert.Empty(BackupStore.LoadAll(tmp.Root));
    }

    /// <summary>
    /// Task 2 追加: <see cref="SerialBackupWriter.OnWriteFailed"/> が record.Id を引数として
    /// 背景スレッドから発火する契約を、<see cref="ManualResetEventSlim"/> で「発火その場」で
    /// 直接観測して固定する。決定性: sleep/リトライ 0=イベント駆動のみ(MRE.Set の memory
    /// barrier が capturedId の可視性も保証するため lock 不要)。
    ///
    /// <see cref="WriteFailure_InvokesOnWriteFailed_AndWorkerSurvives"/>(複合テスト)との差:
    /// - 複合側は Dispose ドレイン完了後に failures リストを検査(post-drain observation)。
    /// - 本テストは発火の瞬間に MRE.Set → その場で assert(during-drain observation)。
    /// 実装が「失敗を記録して Dispose 時にまとめて発火」に変質した回帰を本テストが検出する
    /// (複合側は post-drain 観測のため見逃す)。SerialBackupWriter.cs:34 の
    /// `OnWriteFailed?.Invoke(record.Id)` を null 差替/削除に変異させれば、本テストが red
    /// 化することを実測確認済み。
    ///
    /// 失敗機構の注記: 計画書は「TempDir を削除して I/O 失敗を起こす」を提案していたが、
    /// <see cref="BackupStore.Write"/> は先頭で <see cref="Directory.CreateDirectory(string)"/>
    /// を呼ぶため dir 削除では失敗しない(=書込は成功してしまう)。決定的に失敗を起こせるのは
    /// <c>&lt;id&gt;.json</c> 同名ディレクトリで File.Move をブロックする経路(複合テストと
    /// 同じ)なので、そちらを流用する。タイムアウト 15s は Dispose の Join 上限と揃えた完全な
    /// 保険値(実測では ms オーダーで発火)。
    /// </summary>
    [Fact]
    public void Write_Failure_Invokes_OnWriteFailed_WithRecordId()
    {
        using var tmp = new TempDir();
        // 対象パス "<HashId(id-mre)>.json" と同名のディレクトリを事前作成 → BackupStore.Write の
        // 新規経路(AtomicFile.Write 内の File.Move)が決定的に IOException を投げる
        // (HIGH-1 導入後は Id が GUID N になるためファイル名も HashId 経由で組み立てる)。
        Directory.CreateDirectory(Path.Combine(tmp.Root, HashId("id-mre") + ".json"));

        string? capturedId = null;
        var doneEvent = new ManualResetEventSlim(initialState: false);
        // OnWriteFailed は背景スレッドから同期発火する(SerialBackupWriter.cs:34 の
        // Write catch 内 Invoke)。capturedId=id → doneEvent.Set の順で書けば、
        // Wait 側の後続参照は Set の memory barrier で確実に可視化される=lock 不要。
        using var writer = new SerialBackupWriter(tmp.Root)
        {
            OnWriteFailed = id =>
            {
                capturedId = id;
                doneEvent.Set();
            },
        };

        writer.Write(Rec("id-mre", "boom"));

        Assert.True(
            doneEvent.Wait(TimeSpan.FromSeconds(15)),
            "OnWriteFailed が背景スレッドから発火しなかった(タイムアウト)"
        );
        Assert.Equal(HashId("id-mre"), capturedId);
    }

    // ===== Enqueue 締切後ガード(xmldoc「破棄後は無視」契約) =====

    /// <summary>
    /// Dispose 後の Enqueue は xmldoc の契約どおり呼び出し元に例外を伝播させないことを固定する
    /// (bbb51c9 で一時的に pin していた src バグを直近コミットで修正=Enqueue 冒頭の
    /// `if (_disposed) return;` 早期リターン)。
    ///
    /// 元のバグ: Enqueue が `if (_queue.IsAddingCompleted) return;` を try/catch 外で読み、
    /// _queue.Dispose 後は getter 自体が ObjectDisposedException を投げるため呼び出し元に伝播。
    /// 修正後: _disposed 早期リターンと、内側 catch(InvalidOperationException) の二重防御の
    /// どちらでも「呼び出し元に例外を伝播させない」契約は満たせる。本テストはこの契約
    /// (=呼び出し元の無例外)を固定するのみで、どちらの防御が働いているかは区別しない。
    ///
    /// _disposed early-return 自体は Dispose_IsIdempotent(_disposed による Dispose の
    /// 二重呼び出し early-return)で間接的に守られる=フラグが役に立たなくなれば
    /// 冪等テストが red 化するアンカー。
    ///
    /// LoadAll に影響なし=worker は Dispose 時点で既に foreach を抜けているためどのみち
    /// 何も起きない(=このアサートは _disposed guard の kill には寄与しない・観察補助)。
    /// </summary>
    [Fact]
    public void Enqueue_AfterDispose_DoesNotPropagateException()
    {
        using var tmp = new TempDir();
        var w = new SerialBackupWriter(tmp.Root);
        w.Dispose();

        // 3 呼び出しとも呼び出し元に例外を伝播させない(_disposed early-return または
        // catch (InvalidOperationException) の二重防御のいずれかで達成)。
        var writeEx = Record.Exception(() => w.Write(Rec("z", "zzz")));
        var deleteEx = Record.Exception(() => w.Delete("y"));
        var deleteAllEx = Record.Exception(() => w.DeleteAll());

        Assert.Null(writeEx);
        Assert.Null(deleteEx);
        Assert.Null(deleteAllEx);

        // 補助観察: ディスクに何も書かれていない(worker は既に foreach を抜けているため当然)。
        Assert.Empty(BackupStore.LoadAll(tmp.Root));
    }

    // ===== Dispose 冪等(_disposed early-return) =====

    /// <summary>
    /// Dispose 先頭「if (_disposed) return;」の冪等契約。2 回目以降の Dispose が例外なく戻る
    /// (2 回目に _queue.CompleteAdding や _worker.Join に再突入して ObjectDisposedException を
    /// 起こさないこと=BackupCoordinator.Dispose が using 内・using 外の二経路から呼ぶ現実対応)。
    /// </summary>
    [Fact]
    public void Dispose_IsIdempotent()
    {
        using var tmp = new TempDir();
        var w = new SerialBackupWriter(tmp.Root);

        w.Dispose();
        // 2 回目・3 回目とも無例外(_disposed early-return)。
        var second = Record.Exception(() => w.Dispose());
        var third = Record.Exception(() => w.Dispose());

        Assert.Null(second);
        Assert.Null(third);
    }
}
