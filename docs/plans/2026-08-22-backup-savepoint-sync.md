# 保存点・破棄の即時反映と復元時の陳腐化検出(A-1 / M-31)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 保存直後・タブ破棄直後のクラッシュ窓で、古いバックアップが新しいディスク内容を上書きする導線(A-1)と破棄タブが復活する導線(M-31)を、即時反映(第 1 層)と復元時の陳腐化検出(第 2 層)で塞ぐ。

**Architecture:** `DocumentManager` に「任意の文書の dirty 状態が変わった」イベントを 1 本足し、`BackupCoordinator` が **clean 化とタブクローズのときだけ** 既存の `ReconcileMapMaintenance` + `ReconcileLayout` を即時実行する。加えて `FileController` の 2 つの復元経路で、検証済みパスのディスク更新時刻と `BackupRecord.TimestampUtc` を比較し、ディスクが新しければ復元後に集約警告を 1 個出す。

**Tech Stack:** C# / .NET 9 / WinForms / xUnit。設計書 = `docs/plans/2026-08-22-backup-savepoint-sync-design.md`。

**共通の検証コマンド:**

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~<TestClass>"
dotnet test tests/kxEdit.App.Tests  -c Release --no-build --filter "FullyQualifiedName~<TestClass>"
```

`--no-build` は**直前に同じ構成でビルドしたときだけ**使う(変異バイナリの誤認事故が過去にある)。
commit 時は pre-commit フック(CSharpier 整形+ローカルパス検出)が走る。`--no-verify` は使わない。

---

## Task 1: Core 純粋関数 `BackupStaleness`

**Files:**
- Create: `src/kxEdit.Core/Backup/BackupStaleness.cs`
- Test: `tests/kxEdit.Core.Tests/Backup/BackupStalenessTests.cs`

**Step 1: 失敗するテストを書く**

`tests/kxEdit.Core.Tests/Backup/BackupStalenessTests.cs`:

```csharp
using kxEdit.Core.Backup;

namespace kxEdit.Core.Tests.Backup;

/// <summary>
/// A-1 第 2 層の判定核(設計 2026-08-22 §4.1)。ディスク mtime とバックアップ取得時刻の
/// 新旧比較を、境界・DateTimeKind・攻撃者 JSON 由来の極値まで純粋関数として固定する。
/// </summary>
public class BackupStalenessTests
{
    private static readonly DateTime Backup = new(2026, 08, 22, 12, 00, 00, DateTimeKind.Utc);

    [Fact]
    public void DefaultTolerance_IsTwoSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(2), BackupStaleness.DefaultTolerance);

    [Fact]
    public void NullDisk_ReturnsFalse() =>
        Assert.False(BackupStaleness.IsDiskNewer(null, Backup, TimeSpan.FromSeconds(2)));

    [Fact]
    public void DiskOlder_ReturnsFalse() =>
        Assert.False(
            BackupStaleness.IsDiskNewer(
                Backup.AddMinutes(-1),
                Backup,
                TimeSpan.FromSeconds(2)
            )
        );

    [Fact]
    public void SameInstant_ReturnsFalse() =>
        Assert.False(BackupStaleness.IsDiskNewer(Backup, Backup, TimeSpan.FromSeconds(2)));

    [Fact]
    public void WithinTolerance_ReturnsFalse() =>
        Assert.False(
            BackupStaleness.IsDiskNewer(Backup.AddSeconds(1), Backup, TimeSpan.FromSeconds(2))
        );

    /// <summary>境界: ちょうど許容ぶん新しいだけでは陳腐化と見なさない(厳密な &gt; で判定する)。</summary>
    [Fact]
    public void ExactlyAtTolerance_ReturnsFalse() =>
        Assert.False(
            BackupStaleness.IsDiskNewer(Backup.AddSeconds(2), Backup, TimeSpan.FromSeconds(2))
        );

    [Fact]
    public void BeyondTolerance_ReturnsTrue() =>
        Assert.True(
            BackupStaleness.IsDiskNewer(Backup.AddSeconds(3), Backup, TimeSpan.FromSeconds(2))
        );

    /// <summary>Unspecified(JSON 由来で Kind が落ちた場合)は契約どおり UTC とみなす。
    /// ToUniversalTime に素通しすると Local 扱いで最大 ±14 時間ずれ、判定が反転する。</summary>
    [Fact]
    public void UnspecifiedKind_TreatedAsUtc_NotLocal()
    {
        var backupUnspecified = DateTime.SpecifyKind(Backup, DateTimeKind.Unspecified);
        var diskUnspecified = DateTime.SpecifyKind(
            Backup.AddSeconds(3),
            DateTimeKind.Unspecified
        );
        Assert.True(
            BackupStaleness.IsDiskNewer(
                diskUnspecified,
                backupUnspecified,
                TimeSpan.FromSeconds(2)
            )
        );
        Assert.False(
            BackupStaleness.IsDiskNewer(
                DateTime.SpecifyKind(Backup.AddSeconds(-3), DateTimeKind.Unspecified),
                backupUnspecified,
                TimeSpan.FromSeconds(2)
            )
        );
    }

    /// <summary>Local Kind は UTC へ変換してから比較する(同一瞬間なら false)。</summary>
    [Fact]
    public void LocalKindDisk_ConvertedToUtc()
    {
        var diskLocal = Backup.ToLocalTime(); // Kind=Local・同一瞬間
        Assert.False(BackupStaleness.IsDiskNewer(diskLocal, Backup, TimeSpan.FromSeconds(2)));
        Assert.True(
            BackupStaleness.IsDiskNewer(
                diskLocal.AddSeconds(3),
                Backup,
                TimeSpan.FromSeconds(2)
            )
        );
    }

    /// <summary>攻撃者 JSON が TimestampUtc=DateTime.MaxValue を持つ場合、
    /// 素の `backup + tolerance` は ArgumentOutOfRangeException で復元経路ごと落ちる。</summary>
    [Fact]
    public void BackupAtMaxValue_ReturnsFalse_WithoutOverflow() =>
        Assert.False(
            BackupStaleness.IsDiskNewer(
                DateTime.MaxValue,
                DateTime.MaxValue,
                TimeSpan.FromSeconds(2)
            )
        );

    [Fact]
    public void NegativeTolerance_ClampedToZero() =>
        Assert.False(
            BackupStaleness.IsDiskNewer(Backup, Backup, TimeSpan.FromSeconds(-5))
        );
}
```

**Step 2: 失敗を確認する**

```
dotnet build kxEdit.sln -c Release -warnaserror
```
Expected: FAIL — `CS0103: 現在のコンテキストに 'BackupStaleness' という名前は存在しません`

**Step 3: 最小の実装を書く**

`src/kxEdit.Core/Backup/BackupStaleness.cs`:

```csharp
namespace kxEdit.Core.Backup;

/// <summary>
/// バックアップ本文とディスク上のファイルの新旧を比較する純粋関数(設計 2026-08-22 §4.1)。
/// A-1 の第 2 層: 保存成功後にバックアップの即時削除を投入しても、背景ライターの削除が
/// ディスクへ届く前にクラッシュする残余窓が原理的に残るため、復元側でも陳腐化を検出する。
/// UI/スレッド/ファイルシステム非依存=Core で単体テストできる。
/// </summary>
public static class BackupStaleness
{
    /// <summary>既定の許容差。FAT の 2 秒粒度と NTP の微調整を吸収する。</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// ディスク側がバックアップ取得時刻より新しい(=バックアップが陳腐化している疑いがある)か。
    /// <paramref name="diskLastWriteUtc"/> が null(ファイル無し・取得失敗)なら false
    /// =「判定しない」に倒す(呼び出し側は従来どおり復元する)。
    /// </summary>
    /// <remarks>
    /// Kind の扱い: <see cref="DateTimeKind.Unspecified"/> は契約どおり UTC とみなす。
    /// ToUniversalTime へ素通しすると Local 扱いで最大 ±14 時間ずれ、判定が反転する
    /// (BackupRecord は JSON 経由で Kind が落ちうる)。
    /// オーバーフロー: 攻撃者 JSON の TimestampUtc=<see cref="DateTime.MaxValue"/> で
    /// `backup + tolerance` が例外になり復元経路ごと落ちるのを防ぐため、加算前に判定する。
    /// </remarks>
    public static bool IsDiskNewer(
        DateTime? diskLastWriteUtc,
        DateTime backupTimestampUtc,
        TimeSpan tolerance
    )
    {
        if (diskLastWriteUtc is not DateTime disk)
            return false;
        if (tolerance < TimeSpan.Zero)
            tolerance = TimeSpan.Zero;

        DateTime backupUtc = AsUtc(backupTimestampUtc);
        if (backupUtc > DateTime.MaxValue - tolerance)
            return false; // 加算がオーバーフローする=これより新しいディスクは存在しない

        return AsUtc(disk) > backupUtc + tolerance;
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}
```

**Step 4: テストが通ることを確認する**

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~BackupStalenessTests"
```
Expected: PASS(11 tests)・0 warning

**Step 5: commit**

```
git add src/kxEdit.Core/Backup/BackupStaleness.cs tests/kxEdit.Core.Tests/Backup/BackupStalenessTests.cs
git commit -m "feat(core): バックアップ陳腐化判定の純粋関数 BackupStaleness を追加"
```

**Step 6: 仕様レビュー**

別エージェントに「設計書 §4.1 と実装・テストが一致しているか」をレビューさせる(CLAUDE.md §3 工程 4)。

---

## Task 2: ファイル更新時刻の DI シーム

**Files:**
- Create: `src/kxEdit.App/Abstractions/IFileTimestampProvider.cs`
- Create: `src/kxEdit.App/FileTimestampProvider.cs`
- Create: `tests/kxEdit.App.Tests/Fakes/FakeFileTimestampProvider.cs`
- Test: `tests/kxEdit.App.Tests/FileTimestampProviderTests.cs`

**Step 1: 失敗するテストを書く**

`tests/kxEdit.App.Tests/FileTimestampProviderTests.cs`:

```csharp
using System.IO;

namespace kxEdit.App.Tests;

/// <summary>
/// <see cref="FileTimestampProvider"/> の実 I/O 契約(設計 2026-08-22 §4.3)。
/// Fake で固定値を返すテストでは「実装が本当に null を返すか」が検証できないため、
/// 実ファイルで存在/不在の 2 分岐だけを固定する(FakeReachabilityProbe の教訓)。
/// </summary>
public class FileTimestampProviderTests
{
    [Fact]
    public void ExistingFile_ReturnsUtcTimestamp()
    {
        var dir = Directory.CreateTempSubdirectory("kxEditTs_").FullName;
        try
        {
            var path = Path.Combine(dir, "a.txt");
            File.WriteAllText(path, "x");
            var before = DateTime.UtcNow.AddMinutes(-1);

            var actual = new FileTimestampProvider().GetLastWriteTimeUtc(path);

            Assert.NotNull(actual);
            Assert.Equal(DateTimeKind.Utc, actual!.Value.Kind);
            Assert.True(actual.Value > before);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MissingFile_ReturnsNull()
    {
        var dir = Directory.CreateTempSubdirectory("kxEditTs_").FullName;
        try
        {
            var path = Path.Combine(dir, "missing.txt");
            Assert.Null(new FileTimestampProvider().GetLastWriteTimeUtc(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>不正なパス文字列でも例外を投げず null を返す(復元経路を落とさない契約)。</summary>
    [Fact]
    public void InvalidPath_ReturnsNull_WithoutThrowing() =>
        Assert.Null(new FileTimestampProvider().GetLastWriteTimeUtc("::invalid::\0path"));
}
```

**Step 2: 失敗を確認する**

```
dotnet build kxEdit.sln -c Release -warnaserror
```
Expected: FAIL — `CS0246: 'FileTimestampProvider' が見つかりません`

**Step 3: 実装を書く**

`src/kxEdit.App/Abstractions/IFileTimestampProvider.cs`:

```csharp
namespace kxEdit.App;

/// <summary>
/// ファイルの最終更新時刻(UTC)を取得する DI シーム(設計 2026-08-22 §4.3)。
/// 本番は <see cref="FileTimestampProvider"/> / テストは Fake を差し込む。
/// 取得できない(ファイル不在・アクセス不可・I/O 失敗・不正パス)場合は null を返す契約で、
/// 呼び出し側は「判定しない=従来どおり復元する」に倒す。
/// </summary>
public interface IFileTimestampProvider
{
    /// <summary>最終更新時刻(UTC)。取得できなければ null。例外は投げない。</summary>
    DateTime? GetLastWriteTimeUtc(string path);
}
```

`src/kxEdit.App/FileTimestampProvider.cs`:

```csharp
using System.IO;

namespace kxEdit.App;

/// <summary>
/// <see cref="IFileTimestampProvider"/> の本番実装。復元経路から呼ばれるため、
/// どんな入力でも例外を上位へ伝播させない(1 件の異常で全タブの復元を巻き添えにしない
/// =FileController.RestoreFromBackup のフォールバック方針と同じ)。
/// </summary>
public sealed class FileTimestampProvider : IFileTimestampProvider
{
    public DateTime? GetLastWriteTimeUtc(string path)
    {
        try
        {
            // 不在時の File.GetLastWriteTimeUtc は 1601-01-01 を返す(例外を投げない)。
            // そのまま返すと「非常に古いディスク」に見えて判定が黙って歪むため明示的に弾く。
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex)
            when (ex
                is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException
                    or System.Security.SecurityException
            )
        {
            return null;
        }
    }
}
```

`tests/kxEdit.App.Tests/Fakes/FakeFileTimestampProvider.cs`:

```csharp
namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IFileTimestampProvider"/> のテスト用フェイク。<see cref="Times"/> に載せた
/// パスだけ時刻を返し、それ以外は null(=不在)。<see cref="Queries"/> は
/// 「検証 NG のパスへ I/O しない」契約(設計 §4.3)を assert するための観測点。
/// </summary>
public sealed class FakeFileTimestampProvider : IFileTimestampProvider
{
    public Dictionary<string, DateTime> Times { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>問い合わせを受けたパスの履歴(順序保持)。</summary>
    public List<string> Queries { get; } = new();

    public DateTime? GetLastWriteTimeUtc(string path)
    {
        Queries.Add(path);
        return Times.TryGetValue(path, out var t) ? t : null;
    }
}
```

**Step 4: テストが通ることを確認する**

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~FileTimestampProviderTests"
```
Expected: PASS(3 tests)

**Step 5: commit**

```
git add src/kxEdit.App/Abstractions/IFileTimestampProvider.cs src/kxEdit.App/FileTimestampProvider.cs tests/kxEdit.App.Tests/Fakes/FakeFileTimestampProvider.cs tests/kxEdit.App.Tests/FileTimestampProviderTests.cs
git commit -m "feat(app): ファイル更新時刻の DI シーム IFileTimestampProvider を追加"
```

**Step 6: 仕様レビュー**(Task 1 と同様)

---

## Task 3: `DocumentManager.DocumentDirtyChanged` の新設

**Files:**
- Modify: `src/kxEdit.App/DocumentManager.cs`(`:46-62` のイベント群 / `:193-198` の `OnDirtyChanged`)
- Test: `tests/kxEdit.App.Tests/DocumentManagerTests.cs`

**Step 1: 失敗するテストを書く**

`DocumentManagerTests.cs` の末尾へ追加:

```csharp
    // ===== A-1 / M-31: 任意の文書の dirty 遷移を伝えるイベント(設計 2026-08-22 §3.1) =====

    /// <summary>既存 ActiveDirtyChanged はアクティブ分しか飛ばないため、非アクティブタブの
    /// 保存(別タブで作業中の Ctrl+S 相当)を BackupCoordinator が取りこぼす。
    /// DocumentDirtyChanged は文書を引数に取り、非アクティブでも飛ぶことを固定する。</summary>
    [Fact]
    public void DocumentDirtyChanged_FiresForNonActiveDocument() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var first = host.Docs.CreateNew();
            var second = host.Docs.CreateNew(); // second がアクティブになる
            Assert.Same(second, host.Docs.Active);

            var seen = new List<Document>();
            host.Docs.DocumentDirtyChanged += (_, d) => seen.Add(d);

            first.Editor.Text = "x";
            first.Editor.ClearSavePoint(); // 非アクティブ文書を dirty 化

            Assert.Contains(first, seen);
        });

    /// <summary>dirty 化(SavePointLeft)と clean 化(SavePointReached)の両方で飛ぶ。
    /// 片方だけの配線だと、購読側が「clean 化のみ処理する」フィルタを持てない。</summary>
    [Fact]
    public void DocumentDirtyChanged_FiresOnBothLeftAndReached() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "x";

            var states = new List<bool>();
            host.Docs.DocumentDirtyChanged += (_, d) => states.Add(d.Editor.Modified);

            doc.Editor.ClearSavePoint(); // → dirty
            doc.Editor.SetSavePoint(); // → clean

            Assert.Contains(true, states);
            Assert.Contains(false, states);
        });
```

**Step 2: 失敗を確認する**

```
dotnet build kxEdit.sln -c Release -warnaserror
```
Expected: FAIL — `CS1061: 'DocumentManager' に 'DocumentDirtyChanged' の定義がありません`

**Step 3: 実装を書く**

`src/kxEdit.App/DocumentManager.cs` の `DocumentClosed`(`:62` 付近)の直後にイベントを追加:

```csharp
    /// <summary>任意の文書の dirty 状態が変化した(SavePointLeft / SavePointReached の両方)。
    /// <see cref="ActiveDirtyChanged"/> は**アクティブ分しか飛ばない**ため、非アクティブタブの
    /// 保存を購読側が取りこぼす。BackupCoordinator が「clean 化=バックアップ不要」を
    /// 即時に知るための通知源(設計 2026-08-22 §3.1・A-1 / M-31)。</summary>
    public event EventHandler<Document>? DocumentDirtyChanged;
```

`OnDirtyChanged`(`:193`)へ 1 行追加:

```csharp
    private void OnDirtyChanged(Document doc)
    {
        UpdateLabel(doc);
        if (ReferenceEquals(doc, Active))
            ActiveDirtyChanged?.Invoke(this, EventArgs.Empty);
        DocumentDirtyChanged?.Invoke(this, doc); // 非アクティブ分も含めて購読側へ(A-1 / M-31)
    }
```

**Step 4: テストが通ることを確認する**

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~DocumentManagerTests"
```
Expected: PASS(既存分もすべて緑)

**Step 5: commit**

```
git add src/kxEdit.App/DocumentManager.cs tests/kxEdit.App.Tests/DocumentManagerTests.cs
git commit -m "feat(app): 任意の文書の dirty 遷移を伝える DocumentDirtyChanged を追加"
```

**Step 6: 仕様レビュー**

---

## Task 4: `BackupCoordinator` の即時反映と起動時ゲート

> **このタスクは CLAUDE.md §3 の「前倒しレビュー」対象**: 後続タスク(Task 6)が依存する新しい
> seam(`MarkStartupRestoreComplete`)を導入するため、仕様レビューに加えて**コード品質レビュー**を行う。

**Files:**
- Modify: `src/kxEdit.App/BackupCoordinator.cs`(ctor `:121-126` / `_shutDown` 付近のフィールド / `Reconcile` の後ろ)
- Test: `tests/kxEdit.App.Tests/BackupCoordinatorTests.cs`

**Step 1: 失敗するテストを書く**

`BackupCoordinatorTests.cs` の `Reconcile_DirtyThenSaved_DeletesBackup` の直後へ追加:

```csharp
    // ===== A-1 / M-31: 保存点・破棄の即時反映(設計 2026-08-22 §3) =====

    /// <summary>A-1 回帰網: 保存成功(SetSavePoint)で、**次の Reconcile を待たずに**
    /// バックアップが消えること。既存 Reconcile_DirtyThenSaved_DeletesBackup は
    /// 「次 Reconcile で消える」しか固定しておらず、保存〜次 tick(既定 300 秒)の
    /// クラッシュ窓を検出できなかった。</summary>
    [Fact]
    public void SavePoint_DeletesBackup_WithoutReconcile() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.Backup.MarkStartupRestoreComplete();
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile(); // Write 発生
            var id = host.Writer.Writes[0].Id;

            doc.Editor.SetSavePoint(); // 保存相当。Reconcile は呼ばない

            Assert.Contains(id, host.Writer.Deletes);
            Assert.False(host.Writer.Store.ContainsKey(id));
        });

    /// <summary>ゲート(設計 §3.3): 起動時復元が終わるまでは即時反映を動かさない。
    /// MainForm ctor の NewFile が SetSavePoint 経由でここへ到達し、復元より前に
    /// session-state.json を上書きしてしまうのを防ぐ。</summary>
    [Fact]
    public void SavePoint_BeforeStartupRestoreComplete_DoesNothing() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile();
            int deletesBefore = host.Writer.Deletes.Count;

            doc.Editor.SetSavePoint(); // ゲート閉鎖中=何も起きない

            Assert.Equal(deletesBefore, host.Writer.Deletes.Count);
        });

    /// <summary>M-31 回帰網: 非アクティブタブを閉じても即座にバックアップが消えること。
    /// **非アクティブ**タブで検証するのは、アクティブタブのクローズだと TabControl の
    /// 選択変更 → ActiveDocumentChanged → 既存 Reconcile が走り、即時経路を通らなくても
    /// テストが緑になってしまうため(網を「実際に分岐する場所」へ置く)。</summary>
    [Fact]
    public void ClosingNonActiveDocument_DeletesBackup_WithoutReconcile() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.Backup.MarkStartupRestoreComplete();
            var target = host.NewDoc("hello");
            _ = host.NewDoc("other"); // これがアクティブ=target のクローズで選択は動かない
            host.Backup.Reconcile();
            var id = host
                .Writer.Writes.First(w => w.Content == "hello")
                .Id;

            Assert.True(host.Docs.TryClose(target, _ => true));

            Assert.Contains(id, host.Writer.Deletes);
        });

    /// <summary>M-31 の対照: ゲート閉鎖中は従来どおり次 Reconcile まで残る。
    /// (上のテストが即時経路ではなく既存経路で緑になっていないことの証明)</summary>
    [Fact]
    public void ClosingNonActiveDocument_BeforeStartupRestoreComplete_KeepsBackup() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var target = host.NewDoc("hello");
            _ = host.NewDoc("other");
            host.Backup.Reconcile();
            int deletesBefore = host.Writer.Deletes.Count;

            Assert.True(host.Docs.TryClose(target, _ => true));

            Assert.Equal(deletesBefore, host.Writer.Deletes.Count);
        });

    /// <summary>M-31: レイアウトからもタブが消えること(復元時に空枠が復活しない)。</summary>
    [Fact]
    public void ClosingNonActiveDocument_RemovesTabFromLayout() =>
        Sta.Run(() =>
        {
            using var host = new Host(restoreSessionEnabled: true);
            host.Backup.MarkStartupRestoreComplete();
            var target = host.NewDoc("hello");
            _ = host.NewDoc("other");
            host.Backup.Reconcile();
            var id = host.Writer.Writes.First(w => w.Content == "hello").Id;

            Assert.True(host.Docs.TryClose(target, _ => true));

            Assert.NotEmpty(host.Writer.LayoutWrites);
            Assert.DoesNotContain(host.Writer.LayoutWrites[^1].Tabs, t => t.BackupId == id);
        });

    /// <summary>間隔契約の保存: dirty 化では即時書込をしない。ここを対称に配線すると
    /// 1 打鍵目ごとにバックアップを書き、ユーザーが設定した間隔の意味が消える。</summary>
    [Fact]
    public void DirtyTransition_DoesNotWriteBackup_Immediately() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.Backup.MarkStartupRestoreComplete();
            var doc = host.Docs.CreateNew();
            host.Backup.Reconcile(); // clean のまま登録(HasBackup=false)
            int writesBefore = host.Writer.Writes.Count;

            doc.Editor.Text = "x";
            doc.Editor.ClearSavePoint(); // dirty 化

            Assert.Equal(writesBefore, host.Writer.Writes.Count);
        });

    /// <summary>Shutdown 後は即時経路も無反応(既存 _shutDown ガードの共有)。</summary>
    [Fact]
    public void SavePoint_AfterShutdown_DoesNothing() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.Backup.MarkStartupRestoreComplete();
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile();
            host.Backup.Shutdown(keepForRestore: true);
            int deletesBefore = host.Writer.Deletes.Count;

            doc.Editor.SetSavePoint();

            Assert.Equal(deletesBefore, host.Writer.Deletes.Count);
        });
```

**Step 2: 失敗を確認する**

```
dotnet build kxEdit.sln -c Release -warnaserror
```
Expected: FAIL — `CS1061: 'BackupCoordinator' に 'MarkStartupRestoreComplete' の定義がありません`

**Step 3: 実装を書く**

`src/kxEdit.App/BackupCoordinator.cs`。`private bool _shutDown;` の直後にフィールドを追加:

```csharp
    /// <summary>起動時復元(MainForm.OnShown)が完了したか。完了までは保存点/クローズの
    /// 即時反映を止める(設計 2026-08-22 §3.3)。MainForm ctor の NewFile は
    /// SetSavePoint 経由でここへ到達するため、ゲートが無いと空無題 1 タブのレイアウトを
    /// 復元前に session-state.json へ書き込み、前回セッションを失う。既存の
    /// ActiveDocumentChanged 経路が同じ事故を起こしていないのは、ctor 時点で TabControl の
    /// ハンドルが未生成で WinForms の Selected が発火しないため(=偶然に守られている)。</summary>
    private bool _startupRestoreDone;
```

ctor のイベント配線(`_docs.ActiveDocumentChanged += ...` の直後)へ追加:

```csharp
        // A-1 / M-31(設計 2026-08-22 §3.1): 「バックアップが不要になった」瞬間を即時反映する。
        // Timer と ActiveDocumentChanged だけでは、保存直後 / 破棄直後〜次 tick(既定 300 秒)の
        // クラッシュ窓で、古いバックアップが dirty 復元され Ctrl+S で新内容を上書きする。
        _docs.DocumentDirtyChanged += (_, doc) => OnCleanedOrClosed(!doc.Editor.Modified);
        _docs.DocumentClosed += (_, _) => OnCleanedOrClosed(clean: true);
```

`Reconcile()` の直後にメソッドを追加:

```csharp
    /// <summary>
    /// A-1 / M-31(設計 2026-08-22 §3.2): clean 化・クローズだけを即時反映する。
    /// </summary>
    /// <remarks>
    /// dirty 化(clean=false)では何もしない。対称に配線するとユーザーが設定した
    /// バックアップ間隔の契約が消え、M-21(dirty 文書の全文 string 化)を高頻度で誘発する。
    /// 走らせるのが full <see cref="Reconcile"/> ではなく <see cref="ReconcileMapMaintenance"/>
    /// なのも同じ理由: ReconcileContent は他の dirty タブに対して SnapshotText(全文 string 化)を
    /// 走らせるため、Ctrl+S ごとに呼ぶと巨大 dirty タブ同居時に保存の応答時間が悪化する。
    /// 必要なのは「clean 化 / 閉じた文書のバックアップ削除+レイアウト更新」だけで、
    /// これは ReconcileMapMaintenance の意味論そのもの。
    /// ReconcileMapMaintenance は info.ForceWrite を落とさないが、BackupPlanner.Decide は
    /// modified=false のとき forceWrite を見ないため無害(次に dirty 化したとき 1 回余分に
    /// 書くだけ=安全側)。
    /// </remarks>
    private void OnCleanedOrClosed(bool clean)
    {
        if (!clean)
            return;
        if (_shutDown || !_startupRestoreDone || (!_enabled && !_sessionRestoreEnabled))
            return;
        ReconcileMapMaintenance();
        if (_sessionRestoreEnabled)
            ReconcileLayout(force: false);
    }

    /// <summary>起動時復元(MainForm.OnShown)が終わったことを通知する。これ以降のみ
    /// 保存点・クローズの即時反映が働く(設計 2026-08-22 §3.3)。呼び忘れると A-1 の修正が
    /// 丸ごと死ぬため、MainFormSmokeTests が実経路で固定する(Task 6)。</summary>
    public void MarkStartupRestoreComplete() => _startupRestoreDone = true;
```

**Step 4: テストが通ることを確認する**

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~BackupCoordinatorTests"
```
Expected: PASS(既存分もすべて緑=挙動不変が保たれている)

**Step 5: commit**

```
git add src/kxEdit.App/BackupCoordinator.cs tests/kxEdit.App.Tests/BackupCoordinatorTests.cs
git commit -m "fix(app): 保存点到達とタブクローズをバックアップへ即時反映する(A-1 / M-31)"
```

**Step 6: 仕様レビュー + コード品質レビュー(2 エージェント・別々に起動)**

---

## Task 5: `FileController` の復元時陳腐化検査

> **このタスクは CLAUDE.md §3 の「前倒しレビュー」対象**: 外部入力(バックアップ JSON 由来のパス)を
> 使ったファイル I/O を追加するため、仕様レビューに加えて**脆弱性レビュー**を行う。

**Files:**
- Modify: `src/kxEdit.App/FileController.cs`(ctor `:38-60` / `RestoreFromBackup` / `RestoreDirtyFromBackup` `:752-`)
- Modify: `src/kxEdit.App/MainForm.cs`(`:128-145` の `new FileController(...)` に引数追加のみ。配線の本体は Task 6)
- Test: `tests/kxEdit.App.Tests/FileControllerTests.cs`

**Step 1: 失敗するテストを書く**

`FileControllerTests.cs` の `Host` に `FakeFileTimestampProvider` を足し(`Probe` と同じ形)、
`new FileController(...)` へ `fileTimestamps: Timestamps` を渡す。そのうえで
`RestoreFromBackup` テスト群の末尾に追加:

```csharp
    // ===== A-1 第 2 層: 復元時の陳腐化検出(設計 2026-08-22 §4) =====

    /// <summary>ディスク側がバックアップ取得後に更新されていれば、警告対象として記録する。
    /// A-1 の害は「Ctrl+S で**無警告**に新内容が消える」ことなので、記録=警告が出れば害は消える。</summary>
    [Fact]
    public void RestoreFromBackup_DiskNewerThanBackup_RecordsStalePath() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var path = tmp.Path("a.txt");
            File2.WriteAllText(path, "disk");
            var backupTime = new DateTime(2026, 08, 22, 12, 00, 00, DateTimeKind.Utc);
            host.Timestamps.Times[path] = backupTime.AddMinutes(5); // ディスクの方が新しい

            _ = host.File.RestoreFromBackup(BackupRec(path, "backup", backupTime));

            Assert.Equal(new[] { path }, host.File.TakeStaleRestoredPaths());
        });

    /// <summary>ディスクが古い(通常のクラッシュ復元)なら記録しない=警告を出さない。</summary>
    [Fact]
    public void RestoreFromBackup_DiskOlderThanBackup_RecordsNothing() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var path = tmp.Path("a.txt");
            File2.WriteAllText(path, "disk");
            var backupTime = new DateTime(2026, 08, 22, 12, 00, 00, DateTimeKind.Utc);
            host.Timestamps.Times[path] = backupTime.AddMinutes(-5);

            _ = host.File.RestoreFromBackup(BackupRec(path, "backup", backupTime));

            Assert.Empty(host.File.TakeStaleRestoredPaths());
        });

    /// <summary>パス検証 NG(攻撃者 JSON 由来)では**そもそも I/O しない**。
    /// 検証していないパスへ触らないのは HIGH-2 の思想。Queries が空であることで固定する。</summary>
    [Fact]
    public void RestoreFromBackup_RejectedPath_DoesNotQueryTimestamp() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var rec = BackupRec(
                @"C:\Windows\System32\evil.txt",
                "backup",
                new DateTime(2026, 08, 22, 12, 00, 00, DateTimeKind.Utc)
            );

            _ = host.File.RestoreFromBackup(rec);

            Assert.Empty(host.Timestamps.Queries);
            Assert.Empty(host.File.TakeStaleRestoredPaths());
        });

    /// <summary>Take は読み取りと同時にクリアする=同じ警告を二度出さない。</summary>
    [Fact]
    public void TakeStaleRestoredPaths_ClearsAfterRead() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var path = tmp.Path("a.txt");
            File2.WriteAllText(path, "disk");
            var backupTime = new DateTime(2026, 08, 22, 12, 00, 00, DateTimeKind.Utc);
            host.Timestamps.Times[path] = backupTime.AddMinutes(5);

            _ = host.File.RestoreFromBackup(BackupRec(path, "backup", backupTime));

            Assert.Single(host.File.TakeStaleRestoredPaths());
            Assert.Empty(host.File.TakeStaleRestoredPaths()); // 2 回目は空
        });

    /// <summary>ON(hot exit silent)経路も同じ判定を通ること。A-1 の主経路はこちら
    /// (RestoreSession → RestoreDirtyFromBackup)なので、OFF 経路のテストだけでは網にならない。</summary>
    [Fact]
    public void RestoreSession_DiskNewerThanBackup_RecordsStalePath() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var path = tmp.Path("a.txt");
            File2.WriteAllText(path, "disk");
            var backupTime = new DateTime(2026, 08, 22, 12, 00, 00, DateTimeKind.Utc);
            host.Timestamps.Times[path] = backupTime.AddMinutes(5);
            var bk = BackupRec(path, "backup", backupTime);
            var layout = new SessionLayout(
                new List<SessionLayoutRecord>
                {
                    new(
                        Path: path,
                        UntitledNumber: 0,
                        BackupId: bk.Id,
                        IsActive: true,
                        CaretLine: 0,
                        CaretColumn: 0,
                        LineEnding: 0
                    ),
                },
                backupTime
            );

            _ = host.File.RestoreSession(layout, new[] { bk }, initialEmpty: null, adoptRestored: null);

            Assert.Equal(new[] { path }, host.File.TakeStaleRestoredPaths());
        });
```

補助(既存のヘルパ命名に合わせて `FileControllerTests` 内へ):

```csharp
    private static BackupRecord BackupRec(string? path, string content, DateTime timestampUtc) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            OriginalPath: path,
            UntitledNumber: 0,
            CodePage: 65001,
            HasBom: false,
            LineEndingId: 0,
            Content: content,
            TimestampUtc: timestampUtc
        );
```

> 実装時の注意: `TempDir` / `File2` / `Path(...)` は既存 `FileControllerTests` のヘルパ名に合わせること
> (このファイルには既に一時フォルダのヘルパがある。名前が違えば既存に合わせて読み替える)。
> `OriginalPathValidator` がユーザープロファイル配下しか Ok にしない場合は、既存の
> `RestoreFromBackup_KeepsOriginalPath_WhenPathIsSafe` が使っている「安全と判定されるパス」の
> 作り方をそのまま流用する(検証 Ok に載らないと判定経路へ到達しない)。

**Step 2: 失敗を確認する**

```
dotnet build kxEdit.sln -c Release -warnaserror
```
Expected: FAIL — `CS1061: 'FileController' に 'TakeStaleRestoredPaths' の定義がありません`

**Step 3: 実装を書く**

`src/kxEdit.App/FileController.cs`。フィールドを `_reachabilityProbe` の直後に追加:

```csharp
    private readonly IFileTimestampProvider _fileTimestamps; // A-1: 復元時の陳腐化検出(テストでは Fake)

    /// <summary>A-1 第 2 層(設計 2026-08-22 §4): 直近の復元で「ディスク側がバックアップより
    /// 新しい」と判定されたパス。MainForm が起動時に回収して集約警告を 1 個出す。</summary>
    private readonly List<string> _staleRestoredPaths = new();
```

ctor へ引数を追加(名前付き引数で呼ばれているので末尾でよい):

```csharp
        IReachabilityProbe reachabilityProbe,
        IFileTimestampProvider fileTimestamps
    )
    {
        ...
        _reachabilityProbe = reachabilityProbe;
        _fileTimestamps = fileTimestamps;
    }
```

公開 API とヘルパを `RestoreFromBackup` の近くへ追加:

```csharp
    /// <summary>A-1 第 2 層: 陳腐化が疑われるパスを取り出す(取得と同時にクリア=
    /// 同じ警告を二度出さない)。MainForm.OnShown が ON / OFF いずれの復元後にも回収する。</summary>
    public IReadOnlyList<string> TakeStaleRestoredPaths()
    {
        var result = _staleRestoredPaths.ToArray();
        _staleRestoredPaths.Clear();
        return result;
    }

    /// <summary>
    /// 検証済みパスに対してのみディスクの更新時刻を見て、バックアップが陳腐化していれば記録する
    /// (設計 2026-08-22 §4.3)。
    /// </summary>
    /// <remarks>
    /// **検証 NG のパスへは呼ばないこと**: 攻撃者 JSON 由来のパスへ I/O させない(HIGH-2 の思想)。
    /// ディスク版を優先してバックアップを捨てる判断はしない。ディスクが新しい理由が
    /// 「kxEdit 自身の保存(A-1)」か「他アプリの更新」かを区別できず、捨てる実装は
    /// 新しい無言喪失経路になるため(設計 §4.2)。
    /// </remarks>
    private void NoteIfBackupStale(string validatedPath, BackupRecord bk)
    {
        if (
            BackupStaleness.IsDiskNewer(
                _fileTimestamps.GetLastWriteTimeUtc(validatedPath),
                bk.TimestampUtc,
                BackupStaleness.DefaultTolerance
            )
        )
            _staleRestoredPaths.Add(validatedPath);
    }
```

`RestoreFromBackup` の検証 Ok 分岐へ 1 行:

```csharp
            if (status == PathValidation.Ok)
            {
                safePath = normalized;
                NoteIfBackupStale(normalized, rec); // A-1 第 2 層
            }
```

`RestoreDirtyFromBackup` の検証 Ok 分岐へ 1 行:

```csharp
        if (status == PathValidation.Ok)
        {
            doc.State.Path = normalized;
            doc.State.UntitledNumber = 0;
            NoteIfBackupStale(normalized, bk); // A-1 第 2 層
        }
```

`src/kxEdit.App/MainForm.cs` の `new FileController(...)` へ引数を 1 つ足す(ビルドを通すためだけ):

```csharp
            reachabilityProbe: new FileReachabilityProbe(),
            fileTimestamps: new FileTimestampProvider()
        );
```

**Step 4: テストが通ることを確認する**

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~FileControllerTests"
```
Expected: PASS

**Step 5: commit**

```
git add src/kxEdit.App/FileController.cs src/kxEdit.App/MainForm.cs tests/kxEdit.App.Tests/FileControllerTests.cs
git commit -m "fix(app): 復元時にディスクの更新時刻を見てバックアップの陳腐化を検出する(A-1)"
```

**Step 6: 仕様レビュー + 脆弱性レビュー(2 エージェント・別々に起動)**

---

## Task 6: `MainForm` の配線と警告ダイアログ

**Files:**
- Modify: `src/kxEdit.App/MainForm.cs`(`:71-76` の抑止シーム / `OnShown` / `ShowFailedRestoreDialog` の隣)
- Test: `tests/kxEdit.App.Tests/MainFormSmokeTests.cs`

**Step 1: 失敗するテストを書く**

`MainFormSmokeTests.cs` へ追加。**Task 4 のゲートが実経路で開くこと**を、観測可能な挙動で固定する:

```csharp
    // ===== A-1 / M-31: 起動時ゲートが実経路で開くこと(設計 2026-08-22 §3.3) =====

    /// <summary>MainForm.OnShown が MarkStartupRestoreComplete を呼び忘れると、A-1 の修正が
    /// 丸ごと死ぬ(Coordinator 側テストは seam を直接叩くため気づけない)。起動後にタブを
    /// 閉じてバックアップが即座に消えることで、配線を実経路から固定する。</summary>
    [Fact]
    public void Startup_OpensImmediateReconcileGate() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var settings = NewSettings(csvAutoModeOnOpen: false);
            settings.BackupEnabled = true;
            settings.RestoreOpenFilesOnStartup = true;
            using var form = ShowMainForm_Unified(settings, tmp);

            // dirty な無題タブを作り、tick 相当の Reconcile でバックアップを書かせる。
            var doc = form.DocsForTest.Last();
            doc.Editor.Text = "hello";
            doc.Editor.ClearSavePoint();
            form.ReconcileBackupForTest();
            Assert.NotEmpty(Directory.GetFiles(tmp.BackupDir, "*.json", SearchOption.AllDirectories));

            // 破棄(Ctrl+W の「いいえ」相当)。Reconcile を呼ばずにバックアップが消えること。
            Assert.True(form.DocsForTest.Count > 0);
            form.CloseDocumentForTest(doc);

            Assert.Empty(Directory.GetFiles(tmp.BackupDir, "*.json", SearchOption.AllDirectories));
        });
```

> 実装時の注意: `DocsForTest` / `ReconcileBackupForTest` / `CloseDocumentForTest` は
> **既存の観測シームがあればそれを使う**。無ければ `internal` の最小シームを MainForm へ足す
> (`BackupCoordinator.Reconcile` は既に internal なので、MainForm 側は
> `internal void ReconcileBackupForTest() => _backup.Reconcile();` の 1 行で足りる)。
> シームを増やす前に、既存 MainFormSmokeTests が同種の観測をどう行っているかを必ず読むこと。

**Step 2: 失敗を確認する**

```
dotnet build kxEdit.sln -c Release -warnaserror
```
Expected: FAIL(シーム未定義、またはゲート未配線でバックアップが残る)

**Step 3: 実装を書く**

(1) 抑止シームの名前を実態へ合わせる(`:71-76`)。復元まわりのダイアログが 2 種になるため:

```csharp
    // Form 派生上の bool プロパティは WFO1000 を誘発するため、field + setter method で seam を作る
    // (SetLastSessionBuffersPathForTest と同じ方式)。復元まわりのダイアログ(失敗集約・
    // 陳腐化警告)をまとめて抑止する。
    private bool _suppressRestoreDialogsForTest;

    internal void SetSuppressRestoreDialogsForTest(bool value) =>
        _suppressRestoreDialogsForTest = value;
```

`RestoreUnifiedSession` 内の参照(`:315`)と `MainFormSmokeTests.ShowMainForm_Unified`
(`form.SetSuppressFailedRestoreDialogForTest(true);`)を新名へ置換する。

(2) `OnShown` の復元分岐を書き換える。現行:

```csharp
        if (_settings.RestoreOpenFilesOnStartup)
        {
            // hot exit 統合復元(設計 §3.3): クラッシュ/正常終了を区別せず silent 復元。
            RestoreUnifiedSession();
            return;
        }

        // OFF: 従来どおり異常終了バックアップの復元提案のみ。
        ...(以降のブロック全体)
    }
```

を、OFF ブロックを `OfferBackupRestoreOnStartup()` へ**そのまま**切り出したうえで:

```csharp
        if (_settings.RestoreOpenFilesOnStartup)
            // hot exit 統合復元(設計 §3.3): クラッシュ/正常終了を区別せず silent 復元。
            RestoreUnifiedSession();
        else
            OfferBackupRestoreOnStartup();

        // A-1 / M-31(設計 2026-08-22 §3.3): ここから先だけ、保存点・クローズの即時反映が働く。
        // 復元より前に有効化すると、ctor の NewFile → SetSavePoint が空無題 1 タブの
        // レイアウトを session-state.json へ書き、前回セッションを失う。
        _backup.MarkStartupRestoreComplete();

        // A-1 第 2 層(設計 §4.2): 復元したタブのうちディスク側が新しかったものを 1 個の警告に
        // まとめて通知する。ON / OFF どちらの復元経路も FileController を通るため回収点は 1 つ。
        var stale = _file.TakeStaleRestoredPaths();
        if (stale.Count > 0 && !_suppressRestoreDialogsForTest)
            ShowStaleBackupWarning(stale);
    }

    /// <summary>OFF 経路(RestoreOpenFilesOnStartup=false)の従来どおりの復元提案。
    /// OnShown から切り出しただけで挙動は不変(早期 return を無くし、復元後の共通処理=
    /// ゲート開放と陳腐化警告を ON / OFF 双方で通すため)。</summary>
    private void OfferBackupRestoreOnStartup()
    {
        ...(現行 OFF ブロックをそのまま移設)
    }
```

(3) `ShowFailedRestoreDialog` の直後に兄弟メソッドを追加:

```csharp
    /// <summary>
    /// A-1 第 2 層(設計 2026-08-22 §4.2): バックアップ取得後にディスク側が更新されていた
    /// ファイルを 1 個の警告にまとめて通知する。
    /// </summary>
    /// <remarks>
    /// バックアップを捨てて「ディスク版を優先」はしない。ディスクが新しい理由が
    /// kxEdit 自身の保存(A-1)か他アプリの更新かを区別できず、捨てる実装は新しい
    /// 無言喪失経路になるため。表示規約(最大 10 件・SanitizeForDisplay.OneLine)は
    /// <see cref="ShowFailedRestoreDialog"/> と揃える。
    /// </remarks>
    private void ShowStaleBackupWarning(IReadOnlyList<string> paths)
    {
        const int Cap = 10;
        var shown = paths
            .Take(Cap)
            .Select(p => kxEdit.Core.Text.SanitizeForDisplay.OneLine(p, 200));
        var body =
            "次のファイルは、バックアップを取った後にディスク側が更新されています:\n\n  "
            + string.Join("\n  ", shown);
        if (paths.Count > Cap)
            body += $"\n  ... 他 {paths.Count - Cap} 件";
        body +=
            "\n\n復元したタブを上書き保存すると、ディスク上の新しい内容が失われます。"
            + "\n内容を確認してから保存してください。";
        MessageBox.Show(
            this,
            body,
            "復元した内容が古い可能性があります",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning
        );
    }
```

**Step 4: テストが通ることを確認する**

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~MainFormSmokeTests"
```
Expected: PASS(既存の hot exit 復元テストがすべて緑=ゲートが復元を壊していない)

**Step 5: commit**

```
git add src/kxEdit.App/MainForm.cs tests/kxEdit.App.Tests/MainFormSmokeTests.cs
git commit -m "fix(app): 起動時復元後に即時反映を有効化し、陳腐化バックアップを警告する(A-1)"
```

**Step 6: 仕様レビュー**

---

## Task 7: 最終ブランチレビュー(2 パス)と品質ゲート

**Step 1: ミューテーション検証(スポットチェック)**

CLAUDE.md §4 に従い、実装行を一時的に変異させて対象テストが**赤になること**を確認してから復元する。
変異後は `--no-build` を使わない(変異前バイナリでの誤認を防ぐ)。

| # | 変異箇所 | 変異内容 | 赤になるべきテスト |
|---|----------|----------|--------------------|
| 1 | `BackupCoordinator.OnCleanedOrClosed` | `if (!clean) return;` を削除 | `DirtyTransition_DoesNotWriteBackup_Immediately` |
| 2 | `BackupCoordinator.OnCleanedOrClosed` | `!_startupRestoreDone` を条件から外す | `SavePoint_BeforeStartupRestoreComplete_DoesNothing` / `ClosingNonActiveDocument_BeforeStartupRestoreComplete_KeepsBackup` |
| 3 | `MainForm.OnShown` | `_backup.MarkStartupRestoreComplete();` を削除 | `Startup_OpensImmediateReconcileGate` |
| 4 | `BackupStaleness.IsDiskNewer` | `>` を `>=` に変える | `ExactlyAtTolerance_ReturnsFalse` |
| 5 | `BackupStaleness.IsDiskNewer` | `+ tolerance` を削除 | `WithinTolerance_ReturnsFalse` |
| 6 | `FileController.NoteIfBackupStale` の呼び出し | 検証 Ok 分岐の外へ移す | `RestoreFromBackup_RejectedPath_DoesNotQueryTimestamp` |

**kill できなかった変異があれば、テストではなく網の位置を疑う**(過去に「指示が正しいのに網が
変異を殺せない」事態が繰り返し起きている)。修正はテストを足して再確認する。

**Step 2: 最終ブランチレビュー 2 パス**

`superpowers:requesting-code-review` で **コード品質パス**と**脆弱性パス**を**別々のエージェント**として
起動する(1 起動に混載しない)。レビュー対象は `git diff main...HEAD` の全体。

重点:
- 品質パス — 起動時ゲートの状態機械(開き忘れ・二重開放・Shutdown との相互作用)、
  `ReconcileMapMaintenance` 再利用の妥当性、既存テストが 1 件も書き換わっていないこと。
- 脆弱性パス — 攻撃者 JSON 由来の `TimestampUtc`(極値・Kind 欠落)、検証 NG パスへの I/O 不到達、
  警告ダイアログの表示文字列(`SanitizeForDisplay.OneLine` 経由になっているか)。

指摘は `superpowers:receiving-code-review` で受け、3 択(① fixup commit / ② PR に記載して受容 /
③ 理由付き却下)を明示する。修正は**元 commit を書き換えず fixup commit** で積む。

**Step 3: 品質ゲート**

```
powershell -File tools\pre-merge-check.ps1
```
Expected: **EXIT 0**(Core / Editor / App 全緑・0 warning)

**Step 4: L5(実機 SR 検証)のチェックリストを作る**

`docs/plans/2026-08-22-backup-savepoint-sync-l5-checklist.md` に以下を書き、ユーザーへ実機検証を依頼する。
監査 §5 の未実施分(PR #36〜#39)と**同じセッションでまとめて 1 回**実施する。

1. バックアップ ON・復元 ON で編集 → tick を待つ → Ctrl+S → タスクマネージャで強制終了 → 再起動。
   **保存後の内容**でタブが開き、`*`(未保存)が付かないこと。
2. 同じ手順を、バックアップファイルを手動で残した状態(削除が届く前のクラッシュを模擬)で行い、
   **警告ダイアログが NVDA で読み上げられる**こと(タイトル+本文+OK ボタン)。
3. 2 つ以上のタブを開き、非アクティブなタブを Ctrl+W →「いいえ」→ 強制終了 → 再起動。
   破棄したタブの**本文が復活しない**こと。
4. 復元 ON で通常終了 → 再起動し、**前回のタブ構成が従来どおり復元される**こと(ゲートの退行確認)。

**Step 5: PR(CLAUDE.md §7)**

```
git push -u origin feature/backup-savepoint-sync
gh pr create --base main --title "fix: 保存点・破棄の即時反映と復元時の陳腐化検出(A-1 / M-31)"
```

PR description(日本語)に記載する:
- 目的(監査 A-1 / M-31)と 2 層の設計
- レビュー経緯(前倒しレビュー 2 件+最終 2 パス+ミューテーション結果)
- 申し送り: A-4 / A-7 / A-8 / A-19 は別ブランチ・M-18 は未対応・背景ライターの残余窓は
  第 2 層で受ける設計であること
- **L5 の実施状況**(未実施ならその旨と手順書へのリンク)

---

## 実装順序の理由

Task 1 → 2(依存なしの土台・純粋関数と seam)→ 3(イベント新設)→ 4(即時反映=A-1 第 1 層と M-31)
→ 5(陳腐化検出=A-1 第 2 層)→ 6(配線・ここで初めてユーザーから見える挙動が変わる)→ 7(レビューとゲート)。

Task 4 と 5 は互いに独立しているため順序を入れ替えてもよいが、**6 は必ず最後**にする
(ゲートを開く配線が入るまで、新経路は本番で一切動かない=途中の commit でも main は安全)。
