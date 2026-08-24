# hot exit のバックアップ書込失敗を確認経路へ倒す(A-8)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** hot exit(未保存確認なしのクローズ)が、本文バックアップの書込に失敗したまま無警告で終了するのをやめ、従来の Yes/No/Cancel 確認へフォールバックさせる。

**Architecture:** silent close の判定を「前提ゲート(既存)」+「事後条件(新規)」の 2 段にする。`IBackupWriter` にキュー完了を待つ `WaitForPendingJobs` を足し、`BackupCoordinator.WaitForFinalFlush()` が「最終 flush で投入した本文書込が全部成功したか」に答える。`MainForm.OnFormClosing` は確認スキップを決める前にこれを問い、偽なら既存の確認ループへ倒す。

**Tech Stack:** C# / .NET 9 (net9.0-windows) / WinForms / xUnit。設計書 = `docs/plans/2026-08-24-hot-exit-backup-failure-confirm-design.md`。

**前提 main:** `33f2d3c`(PR #49 マージ後)。ブランチ = `feature/hot-exit-backup-failure-confirm`。

---

## 背景: 実測済みの再現(2026-08-24 スパイク)

計画着手前に、使い捨てテストで **実 `SerialBackupWriter` を使った e2e 再現に成功している**。
`backupDirectory` に**ファイル**を置くと `BackupStore.Write` の
`Directory.CreateDirectory(<BackupDir>/session-xxx)` が `IOException` を投げ、本番実装が本当に失敗する。

```
confirmCalls=0                 ← 確認ダイアログは一度も出ない
LastCloseTookSilentPath=true   ← silent close が成立
sessionDirExists=False         ← 実 writer は 1 バイトも書けていない
layoutWritten=True tabs=1 backupId=2159d3e31cc547bdb55edd76223b949b
                               ← 存在しないバックアップを指す session-state.json が確定書込された
```

無題タブなので次回起動は `FileController.cs:1114-1120` の E4′ = **タブごと消失**。
Form の起動・終了は正常に完走した(= 起動時の `LoadAllForRestore` / `SweepOldSessions` /
`SweepTempFiles` はこの不正パスに耐える。`BackupStore.LoadAll` は `Directory.Exists(dir)` が
false で空を返し、sweep 2 種は try/catch + trace で握られる)。

**この失敗注入手段が Task 4 の e2e テストの土台になる。** 設計書 §7.1 が挙げていた代替手段
((a) 存在しないドライブ / (b) writerFactory 注入 seam)は**不要**と確定した。

---

## Task 1: `IBackupWriter.WaitForPendingJobs` を追加する

**Files:**
- Modify: `src/kxEdit.App/Abstractions/IBackupWriter.cs`
- Modify: `src/kxEdit.App/SerialBackupWriter.cs:43-126, 150-177`
- Modify: `tests/kxEdit.App.Tests/Fakes/FakeBackupWriter.cs`
- Test: `tests/kxEdit.App.Tests/SerialBackupWriterTests.cs`

**Step 1: 失敗するテストを書く**

`tests/kxEdit.App.Tests/SerialBackupWriterTests.cs` は既存。末尾へ追記する。
ヘルパー `SbwTempDir` / `Rec(label, content)` / `HashId(label)` は同ファイルに既にある。

失敗注入は**このファイルの既存流儀を流用する**: `<HashId(label)>.json` と同名のディレクトリを
先に作ると `AtomicFile.Write` 内の `File.Move` が決定的に `IOException` を投げる
(`BackupStore.Write` は先頭で `Directory.CreateDirectory` を呼ぶので「dir を消す」では失敗しない
= 同ファイル `Write_Failure_Invokes_OnWriteFailed_WithRecordId` の xmldoc に実測記録あり)。

```csharp
    // ===== A-8: WaitForPendingJobs(投入済みジョブの待ち合わせ) =====

    /// <summary>A-8: 投入済みジョブが全部実行し終わってから true を返す。
    /// 「待たずに true」だと事後条件検査が空手形になるため、実ファイルの存在で
    /// 「本当に待った」ことを assert する。</summary>
    [Fact]
    public void WaitForPendingJobs_ReturnsTrue_AfterQueuedJobsRan()
    {
        using var tmp = new SbwTempDir();
        using var writer = new SerialBackupWriter(tmp.Root);

        writer.Write(Rec("wait-ok", "body"));

        Assert.True(writer.WaitForPendingJobs(TimeSpan.FromSeconds(15)));
        Assert.Single(BackupStore.LoadAll(tmp.Root)); // 待った証拠=実ファイルが在る
    }

    /// <summary>A-8 §5.3: ワーカーが返らないうちは timeout で false。
    /// 呼び出し側(WaitForFinalFlush)はこれを「確認できない=安全側で失敗扱い」に使う。
    ///
    /// 本ファイルの「待ちは一切入れない」原則の唯一の例外: timeout そのものが被検査対象。
    /// 決定性は保たれている — 直列ワーカーは FIFO なので、塞がれた Write ジョブより先に
    /// バリアが走ることは原理的にない(200 ms がどう転んでも false)。</summary>
    [Fact]
    public void WaitForPendingJobs_ReturnsFalse_WhenWorkerIsBlocked()
    {
        using var tmp = new SbwTempDir();
        // 書込を決定的に失敗させ、その失敗コールバックの中でワーカーを塞ぐ。
        Directory.CreateDirectory(Path.Combine(tmp.Root, HashId("wait-block") + ".json"));
        using var gate = new ManualResetEventSlim(initialState: false);
        var writer = new SerialBackupWriter(tmp.Root)
        {
            // OnWriteFailed は背景スレッドから同期発火する=ここで止めればワーカーが止まる。
            OnWriteFailed = _ => gate.Wait(TimeSpan.FromSeconds(15)),
        };
        try
        {
            writer.Write(Rec("wait-block", "boom"));

            Assert.False(writer.WaitForPendingJobs(TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            gate.Set(); // 先に開けないと Dispose の Join が 15 秒待つ
            writer.Dispose();
        }
    }

    /// <summary>A-8: Dispose 済み(締切済み)なら待たずに true。
    /// 締切後は Enqueue が捨てられるので、素朴に待つと必ず timeout 全長ブロックしてしまう。</summary>
    [Fact]
    public void WaitForPendingJobs_ReturnsTrue_Immediately_AfterDispose()
    {
        using var tmp = new SbwTempDir();
        var writer = new SerialBackupWriter(tmp.Root);
        writer.Dispose();

        Assert.True(writer.WaitForPendingJobs(TimeSpan.FromMilliseconds(50)));
    }
```

ファイル先頭の using に `using System.Threading;` が無ければ足す。

**Step 2: 失敗を確認する**

```
dotnet test tests/kxEdit.App.Tests -c Debug --filter "FullyQualifiedName~SerialBackupWriterTests.WaitForPendingJobs" -p:TreatWarningsAsErrors=false
```

期待: `CS1061` 相当のコンパイルエラー(`WaitForPendingJobs` が無い)。

**Step 3: インターフェイスへ追加する**

`src/kxEdit.App/Abstractions/IBackupWriter.cs` の `DeleteLayout` 宣言の下へ:

```csharp
    /// <summary>A-8: 投入済みジョブが全て実行し終わるまで待つ(<see cref="IDisposable.Dispose"/>
    /// はしない=終了がキャンセルされてもライターは生き続ける)。<paramref name="timeout"/> 内に
    /// 完了を確認できたら true。締切済み(Dispose 後)は待たずに true。
    /// 呼び出しは UI スレッド前提。hot exit の確認スキップ前に「本当に書けたか」を
    /// 事後条件として検査するための seam。</summary>
    bool WaitForPendingJobs(TimeSpan timeout);
```

**Step 4: `SerialBackupWriter` に実装する**

まず `Enqueue` を `bool` 返しへ変える(`src/kxEdit.App/SerialBackupWriter.cs:109-126`)。
既存呼び出し 6 箇所は戻り値を捨てるだけなので式本体のまま変更不要。

```csharp
    /// <summary>ジョブを投入する(締め切り後・破棄後は無視)。投入できたら true。実装詳細。
    /// 呼び出しは UI スレッド前提。</summary>
    // _disposed は volatile 不要: 書き込み(Dispose)も読み取り(Enqueue)も UI スレッドのみ。
    private bool Enqueue(Action job)
    {
        // Dispose 開始後は無視(_disposed=true → CompleteAdding → Join → _queue.Dispose の順で進むため
        // この一読で破棄済み・締切済みの両方をカバー)。従来は _queue.IsAddingCompleted を try 外で読んで
        // いたが、_queue.Dispose 後は getter 自体が ObjectDisposedException を投げるため
        // 呼び出し元に伝播していた(xmldoc「破棄後は無視」の意図との乖離)。_disposed で先に遮断する。
        if (_disposed)
            return false;
        // 競合で AddingCompleted 済み／破棄済み(ObjectDisposedException は InvalidOperationException 派生
        // のため 1 つの catch で両方拾える)。UI スレッド前提のため race window はごく狭いが防御的に残す。
        try
        {
            _queue.Add(job);
            return true;
        }
        catch (InvalidOperationException)
        { /* AddingCompleted 済み or 破棄済み。UI スレッド前提の狭 race・無視 */
            return false;
        }
    }
```

次に `Dispose` の直前へ本体を足す。

```csharp
    /// <inheritdoc/>
    public bool WaitForPendingJobs(TimeSpan timeout)
    {
        // キュー末尾にバリアジョブを積み、それが走り終わるのを待つ。直列ワーカーなので
        // バリアが走った時点で先行ジョブは全て実行済み=失敗通知(OnWriteFailed)も発火済み。
        // 同期プリミティブに TaskCompletionSource を使う理由: ManualResetEventSlim + using だと
        // timeout 後にバリアが破棄済みインスタンスへ Set を打ち、ワーカー側 catch に例外を
        // 吸わせる設計になる。TrySetResult は timeout 後に呼ばれても無害。
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Enqueue(() => barrier.TrySetResult()))
            return true; // 締切済み=これ以上の書込は無い(待つと timeout 全長ブロックするだけ)
        return barrier.Task.Wait(timeout);
    }
```

**Step 5: `FakeBackupWriter` を追随させる**

`tests/kxEdit.App.Tests/Fakes/FakeBackupWriter.cs` の `Dispose` の直前へ:

```csharp
    /// <summary>A-8: Fake は同期実行なので保留ジョブは常に無い。
    /// <see cref="WaitReturnsFalse"/> を立てると「完了を確認できない」= timeout 相当を再現する。</summary>
    public bool WaitReturnsFalse;

    /// <summary>WaitForPendingJobs の呼び出し回数(配線の kill に使う)。</summary>
    public int WaitCalls;

    public bool WaitForPendingJobs(TimeSpan timeout)
    {
        WaitCalls++;
        return !WaitReturnsFalse;
    }
```

**Step 6: テストが通ることを確認する**

```
dotnet test tests/kxEdit.App.Tests -c Debug --filter "FullyQualifiedName~SerialBackupWriterTests.WaitForPendingJobs" -p:TreatWarningsAsErrors=false
```

期待: 3 件 PASS。

**Step 7: commit**

```bash
git add src/kxEdit.App/Abstractions/IBackupWriter.cs src/kxEdit.App/SerialBackupWriter.cs tests/kxEdit.App.Tests/Fakes/FakeBackupWriter.cs tests/kxEdit.App.Tests/SerialBackupWriterTests.cs
git commit -m "feat(app): IBackupWriter に投入済みジョブの待ち合わせ API を足す(A-8 Task 1)"
```

---

## Task 2: `BackupCoordinator.WaitForFinalFlush` を追加する

**Files:**
- Modify: `src/kxEdit.App/BackupCoordinator.cs`(定数は `:37` の `MaxBackupChars` 近傍、本体は `FinalFlushForRestore`(`:679`)の直後)
- Test: `tests/kxEdit.App.Tests/BackupCoordinatorTests.cs`

**Step 1: 失敗するテストを書く**

`FakeBackupWriter` は `Write` を成功させるだけなので、失敗注入は
「`OnWriteFailed` をテスト側から直接 Invoke する」既存流儀(FakeBackupWriter の xmldoc 参照)に乗せる。

`tests/kxEdit.App.Tests/BackupCoordinatorTests.cs` の末尾付近へ:

```csharp
    // ===== A-8: hot exit の事後条件検査(WaitForFinalFlush) =====

    /// <summary>A-8: 書込が全部成功していれば true=silent close を許す。</summary>
    [Fact]
    public void WaitForFinalFlush_NoFailure_ReturnsTrue() =>
        Sta.Run(() =>
        {
            using var host = new Host(restoreSessionEnabled: true);
            host.NewDoc("body");
            host.Backup.FinalFlushForRestore();

            Assert.True(host.Backup.WaitForFinalFlush());
            Assert.Equal(1, host.Writer.WaitCalls); // writer へ本当に問い合わせている
        });

    /// <summary>A-8 の中核: 背景書込が失敗していたら false=確認経路へ倒す根拠を返す。</summary>
    [Fact]
    public void WaitForFinalFlush_WriteFailed_ReturnsFalse() =>
        Sta.Run(() =>
        {
            using var host = new Host(restoreSessionEnabled: true);
            host.NewDoc("body");
            host.Backup.FinalFlushForRestore();
            var written = Assert.Single(host.Writer.Writes);
            host.Writer.OnWriteFailed!(written.Id); // 背景スレッドからの失敗通知を再現

            Assert.False(host.Backup.WaitForFinalFlush());
        });

    /// <summary>A-8 §5.3: ジョブの完了を確認できない(timeout)なら安全側で false。</summary>
    [Fact]
    public void WaitForFinalFlush_WaitTimesOut_ReturnsFalse() =>
        Sta.Run(() =>
        {
            using var host = new Host(restoreSessionEnabled: true);
            host.NewDoc("body");
            host.Backup.FinalFlushForRestore();
            host.Writer.WaitReturnsFalse = true;

            Assert.False(host.Backup.WaitForFinalFlush());
        });

    /// <summary>A-8 §5.1: 失敗 Id を dequeue しない。dequeue すると終了キャンセル後に
    /// 既存の ForceWrite 再試行機構(ReconcileContent 冒頭)が失敗を見失い、
    /// A-8 と同じ握り潰しを新設してしまう。「次の Reconcile が再書込する」ことで固定する。</summary>
    [Fact]
    public void WaitForFinalFlush_DoesNotConsumeFailure_NextReconcileStillForceWrites() =>
        Sta.Run(() =>
        {
            using var host = new Host(restoreSessionEnabled: true);
            var doc = host.NewDoc("body");
            host.Backup.FinalFlushForRestore();
            var written = Assert.Single(host.Writer.Writes);
            host.Writer.OnWriteFailed!(written.Id);

            Assert.False(host.Backup.WaitForFinalFlush());

            // 終了がキャンセルされた想定。本文は変えない=署名は同じ。ForceWrite が
            // 生きていなければ Decide が「書込不要」と判断して Writes は増えない。
            host.Backup.Reconcile();

            Assert.Equal(2, host.Writer.Writes.Count);
            Assert.Equal(written.Id, host.Writer.Writes[1].Id);
        });

    /// <summary>A-8: writer が無い(両機能 OFF)/ shutdown 済みは「書くものが無い=失敗も無い」。</summary>
    [Fact]
    public void WaitForFinalFlush_NoWriter_ReturnsTrue() =>
        Sta.Run(() =>
        {
            using var host = new Host(enabled: false, restoreSessionEnabled: false);

            Assert.True(host.Backup.WaitForFinalFlush());
            Assert.Equal(0, host.Writer.WaitCalls);
        });
```

**Step 2: 失敗を確認する**

```
dotnet test tests/kxEdit.App.Tests -c Debug --filter "FullyQualifiedName~BackupCoordinatorTests.WaitForFinalFlush" -p:TreatWarningsAsErrors=false
```

期待: コンパイルエラー(`WaitForFinalFlush` が無い)。

**Step 3: 定数を足す**

`src/kxEdit.App/BackupCoordinator.cs:37` の `MaxBackupChars` の直後へ:

```csharp
    /// <summary>A-8: hot exit の確認スキップ前に最終 flush の完了を待つ上限。
    /// Windows のシャットダウン猶予に収め、既存 <see cref="Shutdown"/> の Join(15 秒)より
    /// 短くすることで**新しい最悪ブロック時間を作らない**。正常時はバリアが即返るため
    /// 終了の体感は不変。</summary>
    internal static readonly TimeSpan FinalFlushWait = TimeSpan.FromSeconds(5);
```

**Step 4: 本体を足す**

`FinalFlushForRestore` の直後へ:

```csharp
    /// <summary>
    /// A-8(設計 2026-08-24 §5): <see cref="FinalFlushForRestore"/> で投入した本文書込が
    /// 全て成功したかを待ち合わせて答える。hot exit の確認スキップを決める**前**に呼ぶ事後条件検査。
    /// </summary>
    /// <remarks>
    /// false の意味は「未保存本文が永続化されたと言い切れない」であって「失敗が確定した」ではない
    /// (timeout も false)。呼び出し側は従来の未保存確認へ倒すこと。
    /// </remarks>
    public bool WaitForFinalFlush() => WaitForFinalFlush(FinalFlushWait);

    /// <summary>timeout をテストから明示するためのオーバーロード。</summary>
    internal bool WaitForFinalFlush(TimeSpan timeout)
    {
        if (_shutDown || _writer is null)
            return true; // 書くものが無い=失敗も無い
        if (!_writer.WaitForPendingJobs(timeout))
            return false; // 完了を確認できない=安全側で失敗扱い
        // 意図的に dequeue しない: ここで吸い出すと、終了がキャンセルされたときに
        // ReconcileContent 冒頭の ForceWrite 再試行が失敗を見失う(A-8 と同じ握り潰しの新設)。
        // 代償は「書込は失敗したがその後 clean になった文書」の Id が残っている場合の
        // 安全側の偽陽性(申し送り S-A8-1)。確認ループは clean タブを skip するため
        // 実害は他の dirty タブへの余分な確認 1 回に留まる。
        return _failed.IsEmpty;
    }
```

**Step 5: テストが通ることを確認する**

```
dotnet test tests/kxEdit.App.Tests -c Debug --filter "FullyQualifiedName~BackupCoordinatorTests.WaitForFinalFlush" -p:TreatWarningsAsErrors=false
```

期待: 5 件 PASS。

**Step 6: commit**

```bash
git add src/kxEdit.App/BackupCoordinator.cs tests/kxEdit.App.Tests/BackupCoordinatorTests.cs
git commit -m "feat(app): 最終 flush の成否を答える WaitForFinalFlush を足す(A-8 Task 2)"
```

---

## Task 3: `MainForm.OnFormClosing` を事後条件検査つきに組み替える

**Files:**
- Modify: `src/kxEdit.App/MainForm.cs:451-462`(silentPath 判定)、`:496-499`(末尾 flush)、`:92-94`(テスト seam 近傍)

**Step 1: 観測 seam を足す**

`src/kxEdit.App/MainForm.cs:93-94` の `_lastCloseTookSilentPathForTest` の直後へ:

```csharp
    /// <summary>A-8: 直近のクローズで、hot exit の事後条件検査(最終 flush の成否)が
    /// どう出たか。null=検査に到達しなかった(前提ゲートで既に silent close ではない)。
    /// oversized による fall-through と「バックアップ書込失敗」による fall-through を
    /// テストが弁別するための seam。</summary>
    private bool? _lastCloseFinalFlushOkForTest;

    internal bool? LastCloseFinalFlushOkForTest => _lastCloseFinalFlushOkForTest;
```

**Step 2: 判定を 2 段にする**

`src/kxEdit.App/MainForm.cs` の

```csharp
        bool silentPath =
            _settings.RestoreOpenFilesOnStartup
            && _settings.BackupEnabled
            && !HasOversizedDirtyDoc();
        _lastCloseTookSilentPathForTest = silentPath;
```

を次で置き換える(直前のコメントブロックは残す)。

```csharp
        bool silentPath =
            _settings.RestoreOpenFilesOnStartup
            && _settings.BackupEnabled
            && !HasOversizedDirtyDoc();
        _lastCloseFinalFlushOkForTest = null;
        if (silentPath)
        {
            // A-8(設計 2026-08-24 §3): 前提ゲートだけでは「退避できる条件が揃っている」しか
            // 言えない。確認をスキップしてよいのは**実際に退避できたとき**だけなので、
            // ここで最終 flush を投入し完了を待って事後条件を検査する。
            // 投入した本文書込が 1 件でも失敗している / 完了を確認できない場合は、
            // hot exit の交換条件が成立していない=従来の未保存確認へ倒す。
            _backup.FinalFlushForRestore();
            bool flushOk = _backup.WaitForFinalFlush();
            _lastCloseFinalFlushOkForTest = flushOk;
            if (!flushOk)
                silentPath = false;
        }
        _lastCloseTookSilentPathForTest = silentPath;
```

**Step 3: 末尾 flush の二重実行を止める**

`src/kxEdit.App/MainForm.cs:496-499` の

```csharp
        // ON: docs が生きているうちに最終 flush(本文+レイアウト)。OFF の stale layout 掃除は
        // OnFormClosed の Shutdown(keepForRestore:false) が担う。
        if (_settings.RestoreOpenFilesOnStartup)
            _backup.FinalFlushForRestore();
```

を次で置き換える。

```csharp
        // ON: docs が生きているうちに最終 flush(本文+レイアウト)。OFF の stale layout 掃除は
        // OnFormClosed の Shutdown(keepForRestore:false) が担う。
        // A-8: silentPath は true → false へしか遷移しないので、ここで true =「上の事後条件検査で
        // 既に flush 済み」と同値。二重に走らせない理由は速度: ReconcileContent は dirty 文書ごとに
        // SnapshotText(全文 string 化)を走らせるため、巨大 dirty タブ同居時の終了が目に見えて遅くなる。
        // フォールバック時(silentPath=false)は確認ループの保存/破棄をレイアウトへ反映するため
        // ここで改めて走らせる必要がある。
        if (_settings.RestoreOpenFilesOnStartup && !silentPath)
            _backup.FinalFlushForRestore();
```

**Step 4: ビルドが通ることを確認する**

```
dotnet build kxEdit.sln -c Debug -warnaserror
```

期待: 0 warning / 0 error。

**Step 5: 既存テストが割れていないことを確認する**

```
dotnet test tests/kxEdit.App.Tests -c Debug -p:TreatWarningsAsErrors=false
```

期待: 全緑。とくに `MainFormSmokeTests` の
`OnFormClosing_UnifiedOn_*`(`:532, :710, :750, :796, :842, :919, :977, :1008`)が緑であること
= 設計書 §6.3 の「OFF / BackupOFF / oversized / 書込成功の 4 経路は挙動不変」の実地確認。
`:717` の `Assert.NotNull(layout)`(FinalFlushForRestore がレイアウトを確定書込)が
Step 3 の条件追加で割れないことを特に見る。

**Step 6: commit**

```bash
git add src/kxEdit.App/MainForm.cs
git commit -m "fix(app): hot exit の確認スキップ前にバックアップ書込の成否を検査する(A-8 Task 3)"
```

---

## Task 4: e2e 回帰テスト(実 SerialBackupWriter を証人にする)

**Files:**
- Modify: `tests/kxEdit.App.Tests/MainFormSmokeTests.cs`

Fake ではなく**本番実装が本当に失敗する**状況を作る。PR #47 の教訓
(「Fake を注入するテストは本番実装の性質を証人にできない」)に従う。

**Step 1: 失敗するテストを書く**

`OnFormClosing_UnifiedOn_OversizedFallThrough_DiscardedTabsNotRevived`(`:764` 付近)の
直後へ追記する。

```csharp
    /// <summary>A-8(設計 2026-08-24): hot exit の確認なしクローズは、本文バックアップが
    /// 実際に書けなかったときは従来の未保存確認へ倒れる。
    /// 失敗注入は Fake ではなく**実 SerialBackupWriter**に対して行う: backupDirectory の位置に
    /// ファイルを置くと BackupStore.Write の Directory.CreateDirectory が IOException を投げる。
    /// 起動側(LoadAll は Directory.Exists=false で空・sweep 2 種は try/catch)はこの状況に耐える
    /// ことを 2026-08-24 のスパイクで実測済み。
    /// 修正前はこのテストで確認が 0 回・silent=true になり、session-state.json に
    /// 実体の無い BackupId が残る(=次回起動で E4′ タブごと消失)。</summary>
    [Fact]
    public void OnFormClosing_UnifiedOn_BackupWriteFails_FallsThroughToConfirm() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            // backups ディレクトリの位置を「ファイル」で塞ぐ=実 writer の書込が必ず失敗する。
            File.WriteAllText(tmp.BackupDir, "occupied");

            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;

            int confirmCalls = 0;
            using (var form = ShowMainForm_Unified(settings, tmp))
            {
                var doc = form.FileForTest.DocsForTest[0];
                doc.Editor.ReplaceCharRange(0, 0, "unsaved-body");
                Assert.True(doc.Editor.Modified);
                Assert.False(form.HasOversizedDirtyDocForTest()); // oversized 経路ではないことを固定

                form.SetConfirmDiscardOverrideForTest(_ =>
                {
                    confirmCalls++;
                    return true; // No=破棄して続行
                });
                form.Close();

                Assert.Equal(false, form.LastCloseFinalFlushOkForTest); // 事後条件が偽
                Assert.Equal(false, form.LastCloseTookSilentPathForTest); // → 確認経路へ倒れた
            }

            Assert.Equal(1, confirmCalls); // dirty タブに確認が出た
            Assert.False(Directory.Exists(tmp.BackupDir)); // 実 writer は 1 バイトも書けていない
        });

    /// <summary>A-8 の対照群: 書込が成功する通常構成では事後条件が真になり、
    /// 従来どおり確認なしで閉じる(挙動不変の側を固定する)。
    /// これが無いと「常に false を返す」実装でも上のテストが緑になる。</summary>
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
                var doc = form.FileForTest.DocsForTest[0];
                doc.Editor.ReplaceCharRange(0, 0, "unsaved-body");

                form.SetConfirmDiscardOverrideForTest(_ =>
                {
                    confirmCalls++;
                    return true;
                });
                form.Close();

                Assert.Equal(true, form.LastCloseFinalFlushOkForTest);
                Assert.Equal(true, form.LastCloseTookSilentPathForTest);
            }

            Assert.Equal(0, confirmCalls); // 確認なしで閉じる(hot exit 本来の挙動)
        });
```

**Step 2: 対照群だけが先に通ることを確認する**

Task 3 が入っているので両方緑になるはず。順序の都合で先に確認したい場合は
`git stash` で Task 3 を外し、失敗テストが赤・対照群が緑になることを見てから戻す。

```
dotnet test tests/kxEdit.App.Tests -c Debug --filter "FullyQualifiedName~MainFormSmokeTests.OnFormClosing_UnifiedOn_BackupWrite" -p:TreatWarningsAsErrors=false
```

期待: 2 件 PASS。

**Step 3: commit**

```bash
git add tests/kxEdit.App.Tests/MainFormSmokeTests.cs
git commit -m "test(app): 実 writer の書込失敗で確認経路へ倒れる e2e を固定する(A-8 Task 4)"
```

---

## Task 5: ミューテーション検証(前倒しの脆弱性/品質チェック)

**Files:** 変更なし(検証のみ。変異は必ず復元する)

CLAUDE.md §4 のミューテーション検証。実装行を一時的に変異させ、対象テストが**赤になること**を
確認してから復元する。各変異ごとに `git diff` が空に戻ったことを確認する。

| # | 変異 | 対象 | 期待 |
|---|------|------|------|
| M1 | `MainForm.cs` の `if (!flushOk) silentPath = false;` の本体を削除 | `MainFormSmokeTests.OnFormClosing_UnifiedOn_BackupWriteFails_FallsThroughToConfirm` | 赤 |
| M2 | `BackupCoordinator.WaitForFinalFlush` の `return _failed.IsEmpty;` → `return true;` | `BackupCoordinatorTests.WaitForFinalFlush_WriteFailed_ReturnsFalse` + Task 4 の失敗側 | 赤 |
| M3 | `if (!_writer.WaitForPendingJobs(timeout)) return false;` を削除 | `BackupCoordinatorTests.WaitForFinalFlush_WaitTimesOut_ReturnsFalse` | 赤 |
| M4 | `WaitForFinalFlush` の `return _failed.IsEmpty;` の前に `while (_failed.TryDequeue(out _)) { }` を挿入(= §5.1 が禁じた dequeue を実際にやる) | `BackupCoordinatorTests.WaitForFinalFlush_DoesNotConsumeFailure_NextReconcileStillForceWrites` | 赤 |
| M5 | `SerialBackupWriter.WaitForPendingJobs` の `return barrier.Task.Wait(timeout);` → `return true;` | `SerialBackupWriterTests.WaitForPendingJobs_ReturnsFalse_WhenJobBlocksPastTimeout` | 赤 |
| M6 | `MainForm.cs` 末尾 flush の `&& !silentPath` を削除 | 既存 `MainFormSmokeTests.OnFormClosing_UnifiedOn_*` | **緑のまま**の可能性が高い(挙動等価な最適化のため)。緑なら「網が無い」と記録し、Task 6 の申し送りへ回すか、layout 書込回数を数える網を足すか判断する |
| M7 | `MainForm.cs` の `_lastCloseFinalFlushOkForTest = flushOk;` を `= true;` に変異 | Task 4 の失敗側 | 赤(seam が観測点として生きている確認) |

各変異の手順:

```bash
# 例: M2
# 1) 該当行を編集
dotnet test tests/kxEdit.App.Tests -c Debug --filter "FullyQualifiedName~WaitForFinalFlush_WriteFailed" -p:TreatWarningsAsErrors=false   # 赤を確認
git checkout -- src/kxEdit.App/BackupCoordinator.cs                                                                                       # 復元
git diff --stat                                                                                                                           # 空を確認
```

**注意(過去の事故):** レビューエージェントが変異を戻さずに返したことがある
(メモリ `backup-savepoint-sync`)。各変異の後に必ず `git diff` が空であることを確認する。
`--no-build` は使わない(変異前バイナリを走らせて誤った緑を得る事故がある。メモリ `uia-scrollintoview`)。

M6 が緑だった場合の判断:
- レイアウト書込が 1 回か 2 回かを数える網を足すのは、`FakeBackupWriter.LayoutWrites` を使えば
  `BackupCoordinatorTests` 側で可能だが、`MainForm` 経路では writer が実物なので難しい。
- **推奨**: 挙動等価であることを確認したうえで「性能のための条件であり挙動の網は無い」と
  設計書の実施記録へ明記する(S-A8-6 として §9 へ追記)。

**Step: 結果を設計書へ記録して commit**

`docs/plans/2026-08-24-hot-exit-backup-failure-confirm-design.md` に `## 10. 実施記録` を足し、
M1〜M7 の結果表と、スパイクの実測値(本計画の「背景」節)を転記する。

```bash
git add docs/plans/2026-08-24-hot-exit-backup-failure-confirm-design.md
git commit -m "docs(plans): A-8 のスパイク実測とミューテーション検証結果を設計書へ記録する"
```

---

## Task 6: 最終ブランチレビュー(2 パス)

CLAUDE.md §3 工程 5。**パスごとに独立した別エージェントを起動する**(1 起動に混載しない)。
両パスは作業ツリーの担当を分ける(メモリ `preview-base-uri-anchor-tradeoff` の教訓)。

**パス A: コード品質**
- ブランチ全体の差分を対象。
- Task 5 のミューテーション結果表を渡し、スポットチェックの妥当性も見てもらう。
- 重点: `WaitForFinalFlush` の false が持つ 2 つの意味(失敗確定 / 確認不能)を呼び出し側が
  取り違えていないか。`silentPath` の遷移が本当に単調(true→false のみ)か。

**パス B: 脆弱性**
- 対象が「終了経路 + 背景スレッド + ファイル I/O」なので該当(CLAUDE.md §3 工程 4 の前倒し条件)。
- 重点: UI スレッドでの `Task.Wait` がデッドロックを作らないか(ワーカーは純 I/O で UI へ
  マーシャリングしないことを確認する)。WM_QUERYENDSESSION 経路で 5 秒待つことの是非。
  `TaskCompletionSource` が timeout 後に解放されず残らないか。

指摘は 3 択で明示する: ① fixup commit で修正 / ② PR description に記載して受容 / ③ 理由付き却下。
レビュー由来の修正は元 commit を書き換えず**別 fixup commit** で積む。

---

## Task 7: 品質ゲート + L5 チェックリスト作成

**Step 1: ゲート**

```
pwsh tools/pre-merge-check.ps1
```

期待: **EXIT 0**、0 warning。

**Step 2: L5 チェックリストを作る**

`docs/plans/2026-08-24-hot-exit-backup-failure-confirm-l5-checklist.md` を作成する
(既存 `docs/plans/2026-08-23-*-l5-checklist.md` の書式に合わせる)。

SR 経路(`kxEdit.Accessibility` / `EditorControl` の UIA 部 / App の Speech 系)そのものは
不変だが、**SR ユーザーが遭遇する終了確認が新しい条件で出る**ため、CLAUDE.md §5
「判定に迷ったら必要に倒す」に従い **L5 必要** と判定する。

項目:

1. **失敗時に確認が出て読み上げられる** — `%APPDATA%\kxEdit\backups` を読み取り専用にする
   (または同名のファイルで塞ぐ)→ kxEdit を起動 → 無題タブに何か入力 → ウィンドウ X →
   「保存しますか」確認が出て NVDA が読み上げること。設定を戻すのを忘れないこと。
2. **正常時は従来どおり無確認で閉じる** — 上を元に戻し、バックアップ ON + 復元 ON で
   未保存タブがある状態で X → 確認が出ずに閉じ、再起動で内容が復元されること(退行が無いこと)。
3. **終了の体感が変わっていない** — 正常時の X から終了までに目立つ待ちが無いこと。

**Step 3: commit**

```bash
git add docs/plans/2026-08-24-hot-exit-backup-failure-confirm-l5-checklist.md
git commit -m "docs(plans): A-8 の L5 実機 SR 検証チェックリストを追加する"
```

---

## Task 8: PR

CLAUDE.md §7。`pre-merge-check` EXIT 0 → push → PR 作成。

PR description(日本語)に含めるもの:

- **目的**: A-8。hot exit の確認なしクローズが、バックアップ書込失敗を確認せずに終了する問題。
- **実在の証拠**: スパイクの実測値(`confirmCalls=0` / `sessionDirExists=False` /
  実体の無い `BackupId` を持つ `session-state.json`)。
- **設計の要点**: 前提ゲート → 事後条件。`_failed` を dequeue しない理由。timeout=5 秒の根拠。
- **挙動不変の範囲**: OFF / BackupOFF / oversized / 書込成功の 4 経路(§6.3 の表)。
- **レビュー経緯**: Task 6 の 2 パスの指摘と 3 択の判断。
- **ミューテーション検証**: Task 5 の結果表(M6 が緑だった場合はその受容理由も)。
- **申し送り**: S-A8-1〜S-A8-5(+ M6 由来の S-A8-6 があれば)。
- **L5 未実施**: チェックリストの 3 項目。マージ前にユーザーへ実機検証を依頼する。
