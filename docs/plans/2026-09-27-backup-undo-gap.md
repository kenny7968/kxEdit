# バックアップの退避漏れ(Issue #93)実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 保存後に Undo 等で「最後に退避した内容」と同じ未保存状態になったとき、その状態が退避されず hot exit・クラッシュで失われる欠陥(Issue #93)を直す。

**Architecture:** 判定の純関数 `BackupPlanner.Decide` の modified 分岐に `!hasBackup` を足し、「未保存なのにディスクに退避がない」ならいつでも書く。`BackupCoordinator.ReconcileContent` の P-6 省略条件(同じスナップショットなら全文化・ハッシュを省く)にも `info.HasBackup` を足し、修正が省略経路で迂回されないようにする。

**Tech Stack:** C# / .NET / xUnit(Core.Tests・App.Tests)

**Spec:** `docs/plans/2026-09-27-perf-followups-design.md` §3(共通規約)・§5(フェーズ 1)

## Global Constraints

- 意図的な挙動差(設計書 §3.5): 未保存なのに退避がない状態では、内容の署名が前回と同じでも退避を書く。PR description に記載する。
- 0 warning(`-warnaserror`)。pre-commit フックを飛ばさない。
- L5 不要・変異検証は行わない(設計書 §3.3)。
- レビュー由来の修正は fixup commit で積む。
- コミットメッセージ本文・PR は日本語。

## Review Focus

1. **clean 化経路の 2 種**: 削除は `ReconcileContent` の Delete 分岐(次 tick)と `ReconcileMapMaintenance`(保存直後の即時反映)の 2 経路で起きる。どちらの後でも Undo で退避済み内容に戻れば書かれること → Task 1 のテスト 1・2。
2. **同じスナップショットのまま dirty**(`ClearSavePoint`・エンコーディング変更): P-6 の省略経路を通る。Delete 後に同じ参照のまま dirty になっても書かれること → テスト 3。
3. **登録時クリーン → 同じ内容のまま dirty**: `RegisterNew` は `HasBackup=false`・`LastSig=現内容` で登録する。そのまま dirty になっても書かれること → テスト 4。
4. **hot exit の最終 flush**: `FinalFlushForRestore` が退避を書き、レイアウトの `BackupId` が null でないこと(Issue の手順 5 の事象そのもの) → テスト 5。
5. **書きすぎない**: 修正後も、退避済み(`HasBackup=true`)で同じ内容なら 2 回目以降は書かない(既存 `Reconcile_SameContentTwice_WritesOnlyOnce`・`Reconcile_SameSnapshot_DoesNotMaterialize` が固定)。新テストでも、書いた後の次 Reconcile で増えないことを確かめる。

---

### Task 1: `Decide` と P-6 省略条件に「退避がない」を足す

**Files:**
- Modify: `src/kxEdit.Core/Backup/BackupPlanner.cs:17-34`
- Modify: `src/kxEdit.App/BackupCoordinator.cs:597-602`
- Test: `tests/kxEdit.Core.Tests/Backup/BackupPlannerTests.cs`
- Test: `tests/kxEdit.App.Tests/BackupCoordinatorTests.cs`(P-6 ブロックの末尾=`Reconcile_CleanDocument_DoesNotMaterialize` の直後に追加)

**Interfaces:**
- Consumes: なし
- Produces: `BackupPlanner.Decide` のシグネチャは不変。意味だけが変わる(modified かつ `!hasBackup` → `Write`)。

- [ ] **Step 1: Core の失敗するテストを書く**

`BackupPlannerTests.cs` の `Dirty_unchanged_but_forced_writes` の直後に追加:

```csharp
    [Fact]
    public void Dirty_unchanged_without_backup_writes() =>
        // Issue #93: 保存でバックアップを消した後(hasBackup=false・lastSig は残る)に Undo で
        // 退避済みの内容へ戻ると、署名が lastSig と一致する。退避がディスクにないので書くこと。
        Assert.Equal(
            BackupAction.Write,
            BackupPlanner.Decide(
                modified: true,
                currentSig: 5,
                lastSig: 5,
                hasBackup: false,
                forceWrite: false
            )
        );
```

- [ ] **Step 2: App の失敗するテストを書く**

`BackupCoordinatorTests.cs` の `Reconcile_CleanDocument_DoesNotMaterialize` の直後に追加:

```csharp
    // ===== Issue #93: 未保存なのに退避がない状態は、署名が前回と同じでも書く =====
    // 保存(clean 化)でバックアップを消しても LastSig は残る。そこから同じ内容の dirty に戻る経路で
    // 「署名が同じ = None」と判定すると、その状態はどこにも退避されない。

    [Fact]
    public void Reconcile_UndoAfterSaveToBackedUpContent_WritesBackup() =>
        Sta.Run(() =>
        {
            // 削除が ReconcileContent の Delete 分岐で起きる経路(即時反映のゲートは閉じたまま)。
            using var host = new Host();
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile(); // "hello" を退避
            doc.Editor.ReplaceCharRange(5, 0, "!");
            doc.Editor.SetSavePoint(); // "hello!" を保存
            host.Backup.Reconcile(); // clean → Delete(HasBackup=false・LastSig=sig("hello"))
            Assert.Single(host.Writer.Deletes);
            int writesBefore = host.Writer.Writes.Count;

            doc.Editor.Undo(); // "hello" に戻す。ディスクの "hello!" とは違う未保存状態
            Assert.True(doc.Editor.Modified); // 前提: Undo で保存点を越えて dirty になる
            host.Backup.Reconcile();

            Assert.Equal(writesBefore + 1, host.Writer.Writes.Count);
            Assert.Equal("hello", host.Writer.Writes[^1].Content);

            host.Backup.Reconcile(); // 退避済みになったので、以後は書かない
            Assert.Equal(writesBefore + 1, host.Writer.Writes.Count);
        });

    [Fact]
    public void SavePoint_ThenUndoToBackedUpContent_WritesBackup() =>
        Sta.Run(() =>
        {
            // 削除が保存直後の即時反映(ReconcileMapMaintenance)で起きる経路。
            using var host = new Host();
            host.Backup.MarkStartupRestoreComplete();
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile(); // "hello" を退避
            doc.Editor.ReplaceCharRange(5, 0, "!");
            doc.Editor.SetSavePoint(); // 保存 → 即時に Delete(Reconcile は呼ばない)
            Assert.Single(host.Writer.Deletes);
            int writesBefore = host.Writer.Writes.Count;

            doc.Editor.Undo();
            Assert.True(doc.Editor.Modified);
            host.Backup.Reconcile();

            Assert.Equal(writesBefore + 1, host.Writer.Writes.Count);
            Assert.Equal("hello", host.Writer.Writes[^1].Content);
        });

    [Fact]
    public void Reconcile_SameSnapshotDirtiedAfterDelete_WritesBackup() =>
        Sta.Run(() =>
        {
            // P-6 の省略経路: 覚えている参照と同じスナップショットのまま dirty になる
            // (ClearSavePoint・エンコーディングの変更)。省略条件に HasBackup がないと迂回される。
            using var host = new Host();
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile(); // 退避(ここで参照を覚える)
            doc.Editor.SetSavePoint(); // 内容は変えずに保存
            host.Backup.Reconcile(); // Delete
            Assert.Single(host.Writer.Deletes);
            int writesBefore = host.Writer.Writes.Count;

            doc.Editor.ClearSavePoint(); // 同じ参照のまま dirty
            host.Backup.Reconcile();

            Assert.Equal(writesBefore + 1, host.Writer.Writes.Count);
            Assert.Equal("hello", host.Writer.Writes[^1].Content);
        });

    [Fact]
    public void Reconcile_RegisteredCleanThenDirtiedSameContent_WritesBackup() =>
        Sta.Run(() =>
        {
            // RegisterNew はクリーンな文書を HasBackup=false・LastSig=現内容で登録する。
            // 内容を変えずに dirty になった場合も書くこと。
            using var host = new Host();
            var doc = host.NewDoc("hello", dirty: false);
            host.Backup.Reconcile(); // クリーンで登録(書かない)
            Assert.Empty(host.Writer.Writes);

            doc.Editor.ClearSavePoint();
            host.Backup.Reconcile();

            var write = Assert.Single(host.Writer.Writes);
            Assert.Equal("hello", write.Content);
        });

    [Fact]
    public void FinalFlushForRestore_AfterUndoToBackedUpContent_WritesBackupAndLayoutBackupId() =>
        Sta.Run(() =>
        {
            // Issue #93 の手順 5: hot exit の最終 flush が None を返し、レイアウトの BackupId が
            // null になる(次回起動で Undo 後の内容が失われる)。
            using var host = new Host(restoreSessionEnabled: true);
            host.Backup.MarkStartupRestoreComplete();
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile();
            doc.Editor.ReplaceCharRange(5, 0, "!");
            doc.Editor.SetSavePoint();
            doc.Editor.Undo();
            int writesBefore = host.Writer.Writes.Count;

            host.Backup.FinalFlushForRestore();

            Assert.Equal(writesBefore + 1, host.Writer.Writes.Count);
            var written = host.Writer.Writes[^1];
            Assert.Equal("hello", written.Content);
            var tab = Assert.Single(host.Writer.LayoutWrites[^1].Tabs);
            Assert.Equal(written.Id, tab.BackupId);
        });
```

- [ ] **Step 3: テストが落ちることを確かめる(陰性対照)**

Run:
```
dotnet build kxEdit.sln -c Release
dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~BackupPlannerTests"
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~BackupCoordinatorTests"
```
Expected: ビルド成功。新しいテスト 6 件だけが FAIL(Core 1・App 5。App は Writes の件数不一致、FinalFlush は BackupId が null)。既存テストは PASS。
`Assert.True(doc.Editor.Modified)` で落ちた場合は前提(Undo で保存点を越えると dirty)が違うので、止めて報告する。

- [ ] **Step 4: `BackupPlanner.Decide` を直す**

`src/kxEdit.Core/Backup/BackupPlanner.cs` の 17-34 行を次に置き換える:

```csharp
    /// <summary>
    /// 次に行うべきバックアップ操作を返す。
    /// <para>modified: 現在未保存（dirty）か。currentSig: 現内容の署名。lastSig: 前回退避時の署名。</para>
    /// <para>hasBackup: ディスクに当文書のバックアップが存在するか。forceWrite: 前回書込失敗等で強制再書込か。</para>
    /// <para>未保存なのに退避がない（hasBackup=false）ときは、署名が lastSig と同じでも書く。
    /// clean 化でバックアップを消しても lastSig は残るため、保存後に Undo で退避済みの内容へ戻ると
    /// 署名が一致する。署名だけで判定すると、その状態がどこにも退避されない（Issue #93）。</para>
    /// </summary>
    public static BackupAction Decide(
        bool modified,
        long currentSig,
        long lastSig,
        bool hasBackup,
        bool forceWrite
    )
    {
        if (modified)
            return (forceWrite || !hasBackup || currentSig != lastSig)
                ? BackupAction.Write
                : BackupAction.None;
        // クリーン（保存済み等）→ 既存バックアップは不要（内容はディスクと一致）。
        return hasBackup ? BackupAction.Delete : BackupAction.None;
    }
```

- [ ] **Step 5: P-6 の省略条件を直す**

`src/kxEdit.App/BackupCoordinator.cs` の 597-602 行を次に置き換える:

```csharp
            bool modified = doc.Editor.Modified;
            // P-6: 覚えている参照と同じなら、署名は LastSig に等しい(不変条件)。退避があり ForceWrite で
            // なければ Decide は必ず None を返すので、全文化もハッシュも省く。HasBackup を見るのは、
            // 退避がないときは署名が同じでも Write になるため(Issue #93。clean 化で消した後に同じ参照の
            // まま dirty になる経路 = ClearSavePoint・エンコーディングの変更)。
            var snap = doc.Editor.CurrentBuffer.Current;
            if (info.HasBackup && modified && !info.ForceWrite && IsRemembered(info, snap))
                continue;
```

あわせて同ファイルの P-6 テストブロック見出しに相当するコメントはテスト側にしかないので、製品コードの他のコメントは変えない(`OnBackupBecameUnneeded` の remarks「次に dirty 化したとき 1 回余分に書くだけ = 安全側」は修正後も正しい)。

- [ ] **Step 6: テストが通ることを確かめる**

Run:
```
dotnet build kxEdit.sln -c Release
dotnet test tests/kxEdit.Core.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```
Expected: ビルド成功(0 warning)・全件 PASS。

- [ ] **Step 7: 陰性対照の分担を確かめる(P-6 条件の効き)**

Step 5 の `info.HasBackup &&` だけを一時的に外してビルドし、`Reconcile_SameSnapshotDirtiedAfterDelete_WritesBackup` と `Reconcile_RegisteredCleanThenDirtiedSameContent_WritesBackup` が FAIL することを確かめる(Decide を直しても省略経路で迂回されることの確認)。ビルド成功を必ず確かめてからテストを走らせる。確認後に元へ戻し、`git diff` で戻したことを確かめる。

- [ ] **Step 8: Commit**

```bash
git add src/kxEdit.Core/Backup/BackupPlanner.cs src/kxEdit.App/BackupCoordinator.cs tests/kxEdit.Core.Tests/Backup/BackupPlannerTests.cs tests/kxEdit.App.Tests/BackupCoordinatorTests.cs
git commit -m "fix(backup): 未保存なのに退避がない状態は署名が同じでも退避する

保存でバックアップを消しても LastSig が残るため、保存後に Undo で退避済みの
内容へ戻すと署名が一致し、その状態が退避されなかった。hot exit やクラッシュで
Undo 後の内容が失われる(Issue #93)。

- BackupPlanner.Decide: modified のとき !hasBackup でも Write
- P-6 の省略条件に info.HasBackup を追加(同じスナップショットのまま dirty に
  なる経路で修正が迂回されないように)

Closes #93"
```

---

### 完了条件(設計書 §5.3)

- [ ] **手動確認**: 実アプリで hot exit(起動時に前回のファイルを開く+自動バックアップ ON)を使い、編集 → tick で退避 → 追記して保存 → Undo → 終了 → 再起動で、Undo 後の内容が dirty で復元されることを 1 回確かめる。tick を待つ代わりに間隔を最短(5 秒)にしてよい。
- [ ] CLAUDE.md §3 の 5(最終レビュー。小変更なので 2 パスを 1 回に統合してよいが別エージェントで行う)→ `tools/pre-merge-check.ps1` EXIT 0 → PR(Issue #93 を閉じる。§3.5 の挙動差を記載)。
- [ ] マージ後、設計書 §5 の末尾に実施記録を追記する。
