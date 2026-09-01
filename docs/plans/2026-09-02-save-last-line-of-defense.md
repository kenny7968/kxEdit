# B4: 保存の最終防衛線(M-12 / M-13 / M-11) 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** `AtomicFile` が「どの段階で失敗してもディスク上のデータを失わない」約束を実際に守るようにし、
設定ファイルだけがその防衛線の外にある状態を解消する。

**Architecture:** 差替段(`File.Replace` / `File.Move`)を 1 か所へ集約し、失敗時に**エラーコードではなく
事後条件(原本が消えたか)**で分岐する。原本が消えていたら `File.Move` で復旧を試み、失敗したときだけ
tmp を残して専用例外でパスを伝える。ステージング段には `Flush(flushToDisk: true)` を足す。
`SettingsStore` は保存を `AtomicFile` 経由へ移し、読込は `Ok` / `Missing` / `Corrupt` / `Unreadable` の
4 状態を返して、破損だけ退避と通知に繋ぐ。

**Tech Stack:** C# / .NET 9 / xUnit / CSharpier(pre-commit)/ Roslynator + SonarAnalyzer(`-warnaserror`)

**設計書:** `docs/plans/2026-09-02-save-last-line-of-defense-design.md`(節番号はすべてこれを指す)

---

## 実装者への前置き(読まないと確実に踏む)

1. **この計画のコードは正解ではなく「検証すべき案」である。** 実装時に実物と食い違ったら、
   計画ではなく実物を正とし、**食い違いを設計書 §10 の実施記録へ書く**。特にテストの期待値と
   fixture は、書いた本人が実行していない(CLAUDE.md の教訓 [[plan-code-is-not-ground-truth]])。
2. **`kxEdit.Core.Tests` はテストクラスを並列実行する。**(`kxEdit.App.Tests` だけが
   `GlobalUsings.cs:12` の `CollectionBehavior(DisableTestParallelization = true)` で直列。)
   したがって Task 2 の seam は**素の `static` にしてはいけない**。並列で走る別クラス
   (`SettingsStoreTests` / `BackupStore` 系 / `TextFileService` 系)が同時に `AtomicFile.Write` を
   呼ぶため、グローバルなフックは他クラスのテストを壊す。`[ThreadStatic]` にする。
3. **`-warnaserror` が効いている。** `Nullable enable` なので、例外クラスの標準 ctor
   (RCS1194 が要求する)を足すと `string` プロパティが CS8618 で**エラーになる**。
   プロパティ初期化子で `= string.Empty` を与えること(Task 1 に具体形あり)。
4. **commit 前に `--no-verify` を使わない。** pre-commit フック(CSharpier 整形+ローカルパス検出)が
   `.cs` を整形する。整形結果が commit に含まれることを確認する。
5. **各タスクの終わりに別エージェントの仕様レビューを行う**(CLAUDE.md §3-4)。
   **前倒しレビュー該当**: Task 2 = コード品質(新しい seam)/ Task 3 = 脆弱性(ACL 継承の変化)/
   Task 8 = 脆弱性(パス操作)。

### 共通の検証コマンド

```powershell
# ビルド(警告=エラー)
dotnet build kxEdit.sln -c Debug -warnaserror

# 個別テスト(反復中はこれ)
dotnet test tests/kxEdit.Core.Tests -c Debug --no-build --filter "FullyQualifiedName~AtomicFile"
dotnet test tests/kxEdit.Core.Tests -c Debug --no-build --filter "FullyQualifiedName~SettingsStore"
dotnet test tests/kxEdit.App.Tests  -c Debug --no-build --filter "FullyQualifiedName~SettingsStartup"

# 3 プロジェクト全体(タスク完了時)
dotnet test tests/kxEdit.Core.Tests   -c Debug --no-build
dotnet test tests/kxEdit.Editor.Tests -c Debug --no-build
dotnet test tests/kxEdit.App.Tests    -c Debug --no-build

# 最終ゲート(PR 前・EXIT 0 を確認)
pwsh tools/pre-merge-check.ps1
```

B1(PR #60)で Debug 構成もゲートに入っている(`tools/pre-merge-check.ps1:60-66`)。
**Debug で開発する**(`Debug.Assert` が効く)。

---

## Task 1: `AtomicReplaceFailedException` を追加する

差替が失敗し、かつ原本が失われたときに投げる例外を用意する。この時点では**誰も投げない**。
Task 3 で使う。

**Files:**
- Create: `src/kxEdit.Core/IO/AtomicReplaceFailedException.cs`
- Create: `tests/kxEdit.Core.Tests/IO/AtomicReplaceFailedExceptionTests.cs`

**Step 1: 失敗するテストを書く**

`tests/kxEdit.Core.Tests/IO/AtomicReplaceFailedExceptionTests.cs`:

```csharp
using kxEdit.Core.IO;
using Xunit;

namespace kxEdit.Core.Tests.IO;

public class AtomicReplaceFailedExceptionTests
{
    /// <summary>
    /// 設計 §3.4: この例外を共有/ロック違反と誤認させない。誤認すると
    /// TextFileService の `when (AtomicFile.IsShareOrLockViolation(ex))` が真になり、
    /// 原本が消えた後に in-place 上書きフォールバックへ流れて、AtomicFile の復旧と
    /// 同じ仕事を別経路で二重に行うことになる(どちらが効いたか判らなくなる)。
    /// </summary>
    [Fact]
    public void Is_not_classified_as_share_or_lock_violation()
    {
        var ex = new AtomicReplaceFailedException(
            @"C:\dir\doc.txt",
            @"C:\dir\doc.txt.abc.tmp",
            new IOException("replace failed"),
            new IOException("recover failed")
        );

        Assert.False(AtomicFile.IsShareOrLockViolation(ex));
    }

    /// <summary>残した tmp のパスと、失われた差替先のパスを呼出側が読めること。</summary>
    [Fact]
    public void Carries_target_and_preserved_paths_and_both_errors()
    {
        var replaceError = new IOException("replace failed");
        var recoveryError = new IOException("recover failed");

        var ex = new AtomicReplaceFailedException(
            @"C:\dir\doc.txt",
            @"C:\dir\doc.txt.abc.tmp",
            replaceError,
            recoveryError
        );

        Assert.Equal(@"C:\dir\doc.txt", ex.TargetPath);
        Assert.Equal(@"C:\dir\doc.txt.abc.tmp", ex.PreservedTempPath);
        Assert.Same(replaceError, ex.InnerException); // 差替そのものの失敗
        Assert.Same(recoveryError, ex.RecoveryError); // 復旧の失敗
        Assert.Contains(@"C:\dir\doc.txt.abc.tmp", ex.Message); // 呼出側が message でも伝えられる
    }

    /// <summary>RCS1194 準拠の標準 ctor が CS8618 を出さずに使えること(null 参照を作らない)。</summary>
    [Fact]
    public void Standard_constructors_leave_paths_empty_not_null()
    {
        var ex = new AtomicReplaceFailedException();

        Assert.Equal(string.Empty, ex.TargetPath);
        Assert.Equal(string.Empty, ex.PreservedTempPath);
        Assert.Null(ex.RecoveryError);
    }
}
```

**Step 2: 失敗を確認する**

```powershell
dotnet build kxEdit.sln -c Debug -warnaserror
```
期待: `error CS0246: The type or namespace name 'AtomicReplaceFailedException' could not be found`

> **注意**: ビルドエラーの grep は `grep -E " error [A-Z]+[0-9]+"` を使うこと。
> `grep "error CS"` は Sonar の `error S###` を見落として古い DLL を叩く
> ([[mutation-harness-exit-code-trap]])。

**Step 3: 実装する**

`src/kxEdit.Core/IO/AtomicReplaceFailedException.cs`:

```csharp
namespace kxEdit.Core.IO;

/// <summary>
/// 原子的差替(File.Replace / File.Move)が失敗し、<b>かつ差替先の原本が失われた</b>ときに投げる。
/// このとき <see cref="PreservedTempPath"/> がディスク上の唯一のコピーであり、
/// <see cref="AtomicFile"/> は例外的に tmp を掃除せず残す(消すと内容が完全に失われるため)。
/// <para>
/// <b>HResult を共有/ロック違反(0x80070020 / 0x80070021)と一致させてはならない。</b>
/// 一致すると <see cref="AtomicFile.IsShareOrLockViolation"/> が真になり、呼出側
/// (<c>TextFileService.Save</c>)の in-place 上書きフォールバックへ流れる。原本が消えた後に
/// そこへ流すと、AtomicFile 側の復旧と同じ仕事を別経路で二重に行うことになり、
/// どちらが効いたのかがテストからも実機からも判らなくなる(設計 2026-09-02 §3.4)。
/// 既定の <see cref="IOException"/> の HResult をそのまま使い、
/// <c>AtomicReplaceFailedExceptionTests.Is_not_classified_as_share_or_lock_violation</c> で固定する。
/// </para>
/// </summary>
public sealed class AtomicReplaceFailedException : IOException
{
    /// <summary>失われた差替先のパス。</summary>
    public string TargetPath { get; } = string.Empty;

    /// <summary>掃除せず残した tmp のパス(= ディスク上の唯一のコピー)。</summary>
    public string PreservedTempPath { get; } = string.Empty;

    /// <summary>復旧(tmp を元の名前へリネーム)が失敗した理由。
    /// <see cref="Exception.InnerException"/> は差替そのものの失敗。</summary>
    public Exception? RecoveryError { get; }

    public AtomicReplaceFailedException(
        string targetPath,
        string preservedTempPath,
        Exception replaceError,
        Exception recoveryError
    )
        : base(
            $"保存先 '{targetPath}' が失われました。書き込んだ内容は '{preservedTempPath}' に残してあります。",
            replaceError
        )
    {
        TargetPath = targetPath;
        PreservedTempPath = preservedTempPath;
        RecoveryError = recoveryError;
    }

    // 以下は RCS1194 (Implement exception constructors) 準拠のための標準 ctor。
    // DocumentTooLargeException と同じ扱い(呼び出し実績は無いが将来の汎用パターン用)。
    // プロパティ初期化子で string.Empty を与えているのは、Nullable enable 下で
    // これらの ctor が CS8618 になるため(= null を持つ例外を作らせない)。
    public AtomicReplaceFailedException() { }

    public AtomicReplaceFailedException(string message)
        : base(message) { }

    public AtomicReplaceFailedException(string message, Exception innerException)
        : base(message, innerException) { }
}
```

**Step 4: 通ることを確認する**

```powershell
dotnet build kxEdit.sln -c Debug -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Debug --no-build --filter "FullyQualifiedName~AtomicReplaceFailedException"
```
期待: 3 件 PASS。

**Step 5: commit**

```powershell
git add src/kxEdit.Core/IO/AtomicReplaceFailedException.cs tests/kxEdit.Core.Tests/IO/AtomicReplaceFailedExceptionTests.cs
git commit -m "feat(core/io): 原本喪失を伝える AtomicReplaceFailedException を追加"
```

---

## Task 2: 差替段を 1 か所へ集約し、テスト用 seam を開ける(挙動不変)

**★ 前倒しレビュー該当(CLAUDE.md §3-4): 後続タスクが依存する seam を導入する → コード品質レビュー。**

**Files:**
- Modify: `src/kxEdit.Core/IO/AtomicFile.cs`(`:38-50` と `:80-92` の差替 catch を共通化)
- Modify: `tests/kxEdit.Core.Tests/IO/AtomicFileTests.cs`(seam の後始末を固定するテストを追加)

**Step 1: 失敗するテストを書く**

`tests/kxEdit.Core.Tests/IO/AtomicFileTests.cs` の末尾(クラス内)に追加:

```csharp
    /// <summary>
    /// seam は差替段だけを差し替え、スコープを抜けたら必ず既定実装へ戻ること。
    /// 戻らないと、並列実行される他のテストクラス(SettingsStoreTests / BackupStore 系)が
    /// 巻き添えになる。ThreadStatic なので他スレッドへは漏れないが、同一スレッド上での
    /// 後始末はこのテストでしか固定できない。
    /// </summary>
    [Fact]
    public void Commit_override_applies_only_inside_scope()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            int calls = 0;
            using (
                AtomicFile.OverrideCommitForTest(
                    (tmp, dest, destExists) =>
                    {
                        calls++;
                        File.Move(tmp, dest, overwrite: true);
                    }
                )
            )
            {
                AtomicFile.Write(path, new byte[] { 1 });
            }
            Assert.Equal(1, calls);

            // スコープ外は既定実装(= フックは呼ばれない)。
            AtomicFile.Write(path, new byte[] { 2 });
            Assert.Equal(1, calls);
            Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
```

**Step 2: 失敗を確認する**

```powershell
dotnet build kxEdit.sln -c Debug -warnaserror
```
期待: `error CS0117: 'AtomicFile' does not contain a definition for 'OverrideCommitForTest'`

**Step 3: 実装する**

`src/kxEdit.Core/IO/AtomicFile.cs` の**両 `Write`** の差替ブロックを、次の 1 行に置き換える:

```csharp
        // ② tmp は完全に書けている。原子的に差し替える。
        CommitStaged(tmp, path);
```

そして private メンバとして次を追加(`TryDelete` の直前あたり):

```csharp
    /// <summary>
    /// ステージング済み tmp を path へ差し替える。<b>この時点の挙動は Task 2 では不変</b>
    /// (失敗したら tmp を掃除して伝播する)。Task 3 で事後条件による復旧を足す。
    /// </summary>
    private static void CommitStaged(string tmp, string path)
    {
        // destExists は差替の分岐条件そのもの。Task 3 の「原本が消えたか」の判定でも
        // この同じ値を使う(別途 File.Exists を採り直すと TOCTOU 窓が広がる)。
        bool destExists = File.Exists(path);
        try
        {
            Commit(tmp, path, destExists);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>差替の実処理。テストからのみ <see cref="OverrideCommitForTest"/> で置換できる。</summary>
    private static void Commit(string tmp, string path, bool destExists)
    {
        var hook = t_commitOverride;
        if (hook is not null)
        {
            hook(tmp, path, destExists);
            return;
        }
        if (destExists)
            File.Replace(tmp, path, destinationBackupFileName: null); // ACL/属性を保持・バックアップ無し
        else
            File.Move(tmp, path);
    }

    // ===== テスト専用 seam =====
    // File.Replace の部分失敗(差替先が消える)は実環境で決定的に起こせないため、差替段だけを
    // 差し替えられるようにする。production では常に null=既定実装しか走らない。
    //
    // [ThreadStatic] であることが必須: kxEdit.Core.Tests はテストクラスを並列実行する
    // (直列化しているのは kxEdit.App.Tests だけ)。素の static にすると、フックを張っている間に
    // 別スレッドで走る SettingsStoreTests / BackupStore 系 / TextFileService 系の書込まで
    // 差し替わり、無関係なテストが壊れる。
    [ThreadStatic]
    private static Action<string, string, bool>? t_commitOverride;

    /// <summary>差替段をテスト用に差し替える(<b>呼んだスレッドにのみ効く</b>)。
    /// 戻り値を Dispose するまで有効。</summary>
    internal static IDisposable OverrideCommitForTest(Action<string, string, bool> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        var previous = t_commitOverride;
        t_commitOverride = hook;
        return new CommitOverrideScope(previous);
    }

    private sealed class CommitOverrideScope : IDisposable
    {
        private readonly Action<string, string, bool>? _previous;

        internal CommitOverrideScope(Action<string, string, bool>? previous) => _previous = previous;

        public void Dispose() => t_commitOverride = _previous;
    }
```

**Step 4: 通ることを確認する**

```powershell
dotnet build kxEdit.sln -c Debug -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Debug --no-build
dotnet test tests/kxEdit.App.Tests  -c Debug --no-build
```
期待: **既存テストが 1 件も落ちない**(挙動不変)+ 新規 1 件 PASS。
特に `AtomicFileTests` / `AtomicFileStreamWriteTests` / `SerialBackupWriterTests` / `FileControllerTests`。

**Step 5: commit**

```powershell
git add src/kxEdit.Core/IO/AtomicFile.cs tests/kxEdit.Core.Tests/IO/AtomicFileTests.cs
git commit -m "refactor(core/io): 差替段を CommitStaged へ集約しテスト seam を開ける(挙動不変)"
```

**Step 6: 前倒しコード品質レビュー(別エージェント)**

観点を明示して依頼する:
- seam が production の分岐を増やしていないか(`t_commitOverride` の null 判定 1 個に留まっているか)
- `destExists` を差替直前に 1 回だけ採る形が、両 `Write` で同一になっているか
- `[ThreadStatic]` の理由がコメントで説明されているか(素の static に戻す変更を将来止められるか)

---

## Task 3: 原本が消えたら復旧し、駄目なら tmp を残す(M-12)

**★ 前倒しレビュー該当: 設計 §3.3 の ACL 非継承を伴う → 脆弱性レビュー。**

**Files:**
- Modify: `src/kxEdit.Core/IO/AtomicFile.cs`(`CommitStaged` とクラス xmldoc)
- Create: `tests/kxEdit.Core.Tests/IO/AtomicFileRecoveryTests.cs`

**Step 1: 失敗するテストを書く**

`tests/kxEdit.Core.Tests/IO/AtomicFileRecoveryTests.cs`:

```csharp
using kxEdit.Core.IO;
using Xunit;

namespace kxEdit.Core.Tests.IO;

/// <summary>
/// 設計 §3 / §6.1。File.Replace の部分失敗(差替先が消え、tmp だけが残る)は実環境で
/// 決定的に起こせないため、差替段の seam に「例外を投げ、かつ原本を消す」偽装を注入して
/// 事後条件の分岐を固定する。
///
/// seam が実物とずれていないことは、実失敗経路を使う既存テストが緑のままであることで見る
/// (AtomicFileTests の共有違反 / SerialBackupWriterTests の同名ディレクトリ /
/// FileControllerTests の ReadOnly 属性)。この 3 本は「原本が残る」側の分岐を実物で押さえている。
/// </summary>
public class AtomicFileRecoveryTests
{
    private static string NewTempPath() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    private static string[] TmpLeftovers(string path) =>
        Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*tmp*");

    // 差替先を消してから失敗する = ERROR_UNABLE_TO_MOVE_REPLACEMENT 相当の状態を作る。
    private static void DestroyDestinationThenFail(string tmp, string dest, bool destExists)
    {
        File.Delete(dest);
        throw new IOException("simulated partial replace failure");
    }

    // 差替先に触れずに失敗する = 従来どおり tmp を掃除してよい状態。
    private static void FailWithoutTouchingDestination(string tmp, string dest, bool destExists) =>
        throw new IOException("simulated replace failure");

    [Fact]
    public void Bytes_recovers_when_replace_loses_the_original()
    {
        string path = NewTempPath();
        try
        {
            File.WriteAllText(path, "old");

            using (AtomicFile.OverrideCommitForTest(DestroyDestinationThenFail))
            {
                // 復旧(tmp → path のリネーム)が効くので、Write は成功として返る。
                AtomicFile.Write(path, new byte[] { 0x6E, 0x65, 0x77 }); // "new"
            }

            Assert.Equal("new", File.ReadAllText(path));
            Assert.Empty(TmpLeftovers(path));
        }
        finally
        {
            foreach (string f in TmpLeftovers(path))
                File.Delete(f);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Bytes_preserves_tmp_when_recovery_also_fails()
    {
        string path = NewTempPath();
        string? preserved = null;
        try
        {
            File.WriteAllText(path, "old");

            // 復旧の File.Move も失敗させるため、差替先と同名のディレクトリを置いておく
            // (SerialBackupWriterTests:231 と同じ決定的失敗)。
            void DestroyThenBlockRecovery(string tmp, string dest, bool destExists)
            {
                File.Delete(dest);
                Directory.CreateDirectory(dest); // 以降 File.Move(tmp, dest) は必ず失敗する
                throw new IOException("simulated partial replace failure");
            }

            using (AtomicFile.OverrideCommitForTest(DestroyThenBlockRecovery))
            {
                var ex = Assert.Throws<AtomicReplaceFailedException>(
                    () => AtomicFile.Write(path, new byte[] { 0x6E, 0x65, 0x77 })
                );

                preserved = ex.PreservedTempPath;
                Assert.Equal(path, ex.TargetPath);
                // tmp は消されていない = ディスク上の唯一のコピーが残っている。
                Assert.True(File.Exists(preserved));
                Assert.Equal(new byte[] { 0x6E, 0x65, 0x77 }, File.ReadAllBytes(preserved));
                // in-place フォールバックへ流れない(設計 §3.4)。
                Assert.False(AtomicFile.IsShareOrLockViolation(ex));
            }
        }
        finally
        {
            if (preserved is not null && File.Exists(preserved))
                File.Delete(preserved);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Bytes_still_deletes_tmp_when_original_survives()
    {
        string path = NewTempPath();
        try
        {
            File.WriteAllText(path, "old");

            using (AtomicFile.OverrideCommitForTest(FailWithoutTouchingDestination))
            {
                var ex = Assert.Throws<IOException>(
                    () => AtomicFile.Write(path, new byte[] { 9 })
                );
                Assert.IsNotType<AtomicReplaceFailedException>(ex);
            }

            Assert.Equal("old", File.ReadAllText(path)); // 原本は不変
            Assert.Empty(TmpLeftovers(path)); // 残骸を残さない(従来どおり)
        }
        finally
        {
            foreach (string f in TmpLeftovers(path))
                File.Delete(f);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// 新規作成(差替先が元から無い)の失敗では tmp を残さない。事後条件だけで判定すると
    /// ここも「!File.Exists(path)」が真になり、失われた原本が無いのに残骸を残す
    /// = 誤検出になる(設計 §3.1)。
    /// </summary>
    [Fact]
    public void Bytes_deletes_tmp_when_creating_a_new_file_fails()
    {
        string path = NewTempPath();
        try
        {
            using (AtomicFile.OverrideCommitForTest(FailWithoutTouchingDestination))
            {
                var ex = Assert.Throws<IOException>(
                    () => AtomicFile.Write(path, new byte[] { 9 })
                );
                Assert.IsNotType<AtomicReplaceFailedException>(ex);
            }

            Assert.False(File.Exists(path));
            Assert.Empty(TmpLeftovers(path));
        }
        finally
        {
            foreach (string f in TmpLeftovers(path))
                File.Delete(f);
        }
    }
}
```

**Stream 版にも同じ 4 本を書く。** `AtomicFile.Write(path, s => s.Write(payload, 0, payload.Length))`
に置き換えるだけで、期待値は同一。**両版を書くこと** —— 片方だけ直す変異が生存する
(実際に `AtomicFile.cs` は同じコードが 2 か所にある形をずっと持っていた)。

**Step 2: 失敗を確認する**

```powershell
dotnet test tests/kxEdit.Core.Tests -c Debug --filter "FullyQualifiedName~AtomicFileRecovery"
```
期待: `Bytes_recovers_when_replace_loses_the_original` は
「`File.ReadAllText(path)` が FileNotFound」または投げた `IOException` で FAIL。
`Bytes_preserves_tmp_when_recovery_also_fails` は
「`AtomicReplaceFailedException` ではなく `IOException` が飛んだ」で FAIL。
残り 2 本は**修正前から PASS する**(従来挙動の回帰網なので、それが正しい)。

**Step 3: 実装する**

`CommitStaged` を差し替える:

```csharp
    /// <summary>
    /// ステージング済み tmp を path へ差し替える。失敗したときは<b>エラーコードではなく
    /// 事後条件</b>で分岐する —— 差替前に原本があったのに、失敗後に無くなっているなら、
    /// tmp がディスク上の唯一のコピーである(File.Replace の部分失敗)。この場合だけ
    /// tmp を掃除せず、まず元の名前へのリネームで復旧を試みる。
    /// <para>
    /// エラーコードを列挙しないのは、前置の列挙が原理的に漏れるため(監査 §9 V-7)。
    /// 事後条件なら未知の失敗でも同じ判定が効く。
    /// </para>
    /// <para>
    /// <b>復旧が成功した場合、原本の ACL / 属性 / 作成日時は引き継がれない</b>
    /// (File.Replace は引き継ぐが File.Move は引き継がない)。元ファイルに個別の厳しい ACL が
    /// あった場合、復旧は権限を広げる方向へ倒す。比較しているのは「権限が広がったファイルが残る」と
    /// 「ファイルが消える」であり、後者の方が回復不能なので前者を受容する(設計 2026-09-02 §3.3)。
    /// </para>
    /// </summary>
    private static void CommitStaged(string tmp, string path)
    {
        bool destExists = File.Exists(path);
        try
        {
            Commit(tmp, path, destExists);
        }
        catch (Exception replaceError)
        {
            // 原本があったのに消えている = tmp が唯一のコピー。新規作成(destExists=false)の
            // 失敗はここに入らない(失われた原本が無いので、残骸を残すだけになる)。
            if (destExists && !File.Exists(path))
            {
                try
                {
                    File.Move(tmp, path);
                    return; // 復旧成功 = 保存は成立している
                }
                catch (Exception recoveryError)
                {
                    throw new AtomicReplaceFailedException(
                        path,
                        tmp,
                        replaceError,
                        recoveryError
                    );
                }
            }
            TryDelete(tmp);
            throw;
        }
    }
```

**クラス xmldoc(`AtomicFile.cs:4-9`)も直す。** 現在の
「どの段階で失敗しても原本には一切触れず、tmp の掃除だけ試みて例外を伝播する」は
② の部分失敗を織り込んでいない。次の趣旨へ書き換える:

> ①(ステージング)の失敗は原本に一切触れず tmp を掃除して伝播する。
> ②(差替)の失敗は、原本が残っていれば同様。**原本が失われていた場合だけは tmp を掃除せず**、
> リネームによる復旧を試み、それも失敗したときに `AtomicReplaceFailedException` で
> 残した tmp のパスを伝える。

**Step 4: 通ることを確認する**

```powershell
dotnet build kxEdit.sln -c Debug -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Debug --no-build
dotnet test tests/kxEdit.App.Tests  -c Debug --no-build
```
期待: 新規 8 件(byte[] 4 + Stream 4)PASS。既存テストは 1 件も落ちない。

**Step 5: ミューテーション スポット確認(2 変異のみ)**

CLAUDE.md §4-A はファイル I/O へのミューテーション検証を**禁止**している。ただし §3.1 の判定は
2 つの条件の**組**で成立しており、片方を落とすとデータ喪失に直結する。ユーザー規範の例外条件
(「厳密な挙動を保証する必要がある場合」)として、**次の 2 変異だけ**手で当てて赤を確認する。
それ以外には広げない。

| # | 変異 | 殺すテスト |
|---|------|-----------|
| 1 | `if (destExists && !File.Exists(path))` → `if (!File.Exists(path))` | `Bytes_deletes_tmp_when_creating_a_new_file_fails`(新規作成の失敗で tmp が残る) |
| 2 | `if (destExists && !File.Exists(path))` → `if (destExists)` | `Bytes_still_deletes_tmp_when_original_survives`(原本が残っているのに tmp も残る) |

**変異を戻すのを忘れないこと。** 実測結果(どのテストが赤になったか)を設計書 §10 へ書く。
**「殺せた」と書く前に、実際にそのテストが赤になった出力を見ること**
([[rationale-not-just-conclusion]]・kill 主張の偽装は本リポジトリで 3 回再発している)。

**Step 6: commit**

```powershell
git add src/kxEdit.Core/IO/AtomicFile.cs tests/kxEdit.Core.Tests/IO/AtomicFileRecoveryTests.cs
git commit -m "fix(core/io): 差替の部分失敗で唯一のコピーを消さない(M-12)"
```

**Step 7: 前倒し脆弱性レビュー(別エージェント)**

観点:
- 復旧成功時の ACL / 属性非継承(設計 §3.3)を、この実装のまま受容してよいか
- `AtomicReplaceFailedException` が in-place フォールバックへ流れないことが、
  `TextFileService.cs:342` / `:448` の両方で成立しているか
- 残した tmp が第三者に読める場所に残らないか(tmp は原本と同ディレクトリ = 原本と同じ露出面)

---

## Task 4: 保存先が失われたことをユーザーへ伝える

Task 3 で tmp を残しても、それを伝える経路が無ければユーザーからは「保存に失敗した」としか見えない。

**Files:**
- Modify: `src/kxEdit.Core/kxEdit.Core.csproj`(`InternalsVisibleTo` に `kxEdit.App.Tests` を追加)
- Modify: `src/kxEdit.App/FileController.cs`(`WriteToPath` の catch 節・`:900` 付近)
- Modify: `tests/kxEdit.App.Tests/FileControllerTests.cs`

**Step 1: 失敗するテストを書く**

`FileControllerTests.cs` に追加(既存の ReadOnly 属性テスト = `:1620` 付近の形を流用する。
`FakeUserPrompt` の実名・API は既存テストから確認して合わせること):

```csharp
    /// <summary>
    /// 設計 §3.2 / Task 4: 差替の部分失敗で原本が失われ、復旧もできなかったとき、
    /// エラー文言が「残してある tmp の場所」を含むこと。これが無いと、AtomicFile が
    /// tmp を残す意味がユーザーに届かない(残っていることを知る手段が無い)。
    /// </summary>
    [Fact]
    public void Save_reports_preserved_copy_when_original_is_lost()
    {
        // ... 既存テストと同じ形で controller / doc / path を用意する ...

        using (
            kxEdit.Core.IO.AtomicFile.OverrideCommitForTest(
                (tmp, dest, destExists) =>
                {
                    File.Delete(dest);
                    Directory.CreateDirectory(dest); // 復旧の File.Move も失敗させる
                    throw new IOException("simulated partial replace failure");
                }
            )
        )
        {
            Assert.False(controller.SaveActive()); // 実際の保存導線に合わせる
        }

        Assert.Contains(".tmp", prompt.LastErrorMessage);
        Assert.Contains("残", prompt.LastErrorMessage); // 「残してあります」旨が出ている
        Assert.True(doc.Modified); // 保存できていないので dirty のまま
    }
```

**Step 2: 失敗を確認する**

```powershell
dotnet build kxEdit.sln -c Debug -warnaserror
```
期待: `error CS0122: 'AtomicFile.OverrideCommitForTest(...)' is inaccessible due to its protection level`
(= `InternalsVisibleTo` がまだ無い)

**Step 3: 実装する**

`src/kxEdit.Core/kxEdit.Core.csproj` の `ItemGroup` に追加:

```xml
    <!-- Task 4(設計 2026-09-02 §6.1): AtomicFile.OverrideCommitForTest を App.Tests から使う。
         File.Replace の部分失敗は実環境で決定的に起こせないため、FileController →
         TextFileService → AtomicFile の end-to-end 経路を固定するには App.Tests 側から
         差替段を差し替える必要がある。kxEdit.Editor が既に App.Tests へ internal を
         可視化しているのと同じ扱い。 -->
    <InternalsVisibleTo Include="kxEdit.App.Tests" />
```

`src/kxEdit.App/FileController.cs` の `WriteToPath` の catch 節、
`DocumentTooLargeException` の分岐の**直後**に追加:

```csharp
            // 設計 2026-09-02 §3.2: 差替の部分失敗で保存先が失われ、復旧もできなかったケース。
            // 共通文言(ex.Message を 200 字で切る)に任せると、肝心の tmp のパスが末尾で
            // 切れて消えうるので専用文言にする。パスは外部入力を含むため SanitizeForDisplay を通す。
            if (ex is kxEdit.Core.IO.AtomicReplaceFailedException lost)
            {
                _prompt.Error(
                    "保存できませんでした: 保存先のファイルが失われましたが、書き込んだ内容は次の場所に残してあります:\n  "
                        + SanitizeForDisplay.OneLine(lost.PreservedTempPath, 260)
                        + "\n\nこのファイルの名前を元のファイル名に変更すると、内容を取り戻せます。",
                    "エラー"
                );
                return false;
            }
```

> `AtomicReplaceFailedException` は `IOException` 派生なので、既存の `when` フィルタ
> (`ex is IOException or ...`)に**そのまま一致する**。フィルタ側の変更は要らない。
> 一致することを実装時に確認すること(一致しなければ catch に入らず素通りする)。

**Step 4: 通ることを確認する**

```powershell
dotnet build kxEdit.sln -c Debug -warnaserror
dotnet test tests/kxEdit.App.Tests -c Debug --no-build
```

**Step 5: commit**

```powershell
git add src/kxEdit.Core/kxEdit.Core.csproj src/kxEdit.App/FileController.cs tests/kxEdit.App.Tests/FileControllerTests.cs
git commit -m "feat(app): 保存先が失われたとき残した内容の場所を伝える"
```

---

## Task 5: rename 前に `Flush(flushToDisk: true)` する(M-13)

**Files:**
- Modify: `src/kxEdit.Core/IO/AtomicFile.cs`(両 `Write` のステージング段・クラス xmldoc)
- Modify: `tests/kxEdit.Core.Tests/IO/AtomicFileTests.cs`

**Step 1: 失敗するテストを書く**

`Flush(true)` が実際にディスクへ届いたことは自動テストでは検証できない(電源を落とせない)。
固定できるのは byte[] 版が `CreateNew` になったことだけである。**これを「fsync の網」と呼ばないこと。**

```csharp
    /// <summary>
    /// byte[] 版のステージングが FileStream(CreateNew) になったこと。Stream 版と形が揃い、
    /// 既存の tmp を黙って上書きしなくなる(乱数名なので実際には衝突しないが、
    /// 衝突したときに他者の書込中ファイルを潰すより失敗する方が正しい)。
    ///
    /// 注意: このテストは Flush(flushToDisk: true) を検証していない。電源断を再現できないため
    /// fsync が効いたことは自動テストでは観測できない(設計 §6.2)。
    /// </summary>
    [Fact]
    public void Bytes_staging_uses_CreateNew_and_does_not_clobber_an_existing_tmp()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string? tmp = null;
        try
        {
            // 乱数 tmp 名を固定できないので、seam で「tmp を作らせてから確認する」形にする。
            using (
                AtomicFile.OverrideCommitForTest(
                    (t, dest, destExists) =>
                    {
                        tmp = t;
                        throw new IOException("stop before commit");
                    }
                )
            )
            {
                Assert.Throws<IOException>(() => AtomicFile.Write(path, new byte[] { 1 }));
            }
            Assert.NotNull(tmp);
            Assert.EndsWith(".tmp", tmp);
        }
        finally
        {
            if (tmp is not null && File.Exists(tmp))
                File.Delete(tmp);
            if (File.Exists(path))
                File.Delete(path);
        }
    }
```

> **実装時に判断すること**: 上のテストは `CreateNew` を直接には固定していない
> (tmp の名前しか見ていない)。`FileMode.Create` へ退化させる変異を殺せないなら、
> **殺せないと実施記録に書く**。書けるはずの網を「書けない」と宣言するのは嘘だが、
> 張れていない網を張ったと書くのも同じ嘘である([[net-absence-claims-are-also-verifiable]])。
> 殺す形が作れるなら作る(例: 同名 tmp を先に置ける seam を足すのは過剰なので、
> 既存の Stream 版が `CreateNew` で書かれている事実と揃った、という論拠に留めてよい)。

**Step 2 / 3: 実装する**

byte[] 版のステージング:

```csharp
        try
        {
            using var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            fs.Write(payload, 0, payload.Length);
            // M-13: rename する前にディスクへ届かせる。これをしないと、差し替わったファイルの
            // 中身が保存直後の電源断で不完全になりうる。
            fs.Flush(flushToDisk: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
```

Stream 版のステージング:

```csharp
            using (
                var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            )
            {
                writer(fs);
                fs.Flush(flushToDisk: true); // M-13
            }
```

**クラス xmldoc に限界を書く**(§4.3):

> `Flush(flushToDisk: true)` が保証するのは「そのファイルの中身がディスクに届いたこと」であり、
> **その後の rename が届いたことではない**(Windows にはディレクトリエントリを明示 flush する
> API が .NET から無い)。したがって本実装が消すのは「差し替わったファイルの中身が不完全」という
> 失敗であって、「rename 自体が失われる」失敗は残る。後者が起きた場合、原本は無傷のまま残る。

**Step 4: 通ることを確認し、保存時間を実測する**

```powershell
dotnet build kxEdit.sln -c Debug -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Debug --no-build
dotnet test tests/kxEdit.App.Tests  -c Debug --no-build
```

**実測(設計 §4.2 の申し送り)**: 大きめのファイル(例: 100 MB 前後)を `AtomicFile.Write` で
書く小さな計測を Debug ビルドで 3 回ずつ回し、**この commit の前後で秒数を比べて設計書 §10 へ書く**。
計測スクリプトはスクラッチパッドに置き、リポジトリへは commit しない
(稼働中のサブエージェントがリポジトリを触ると commit が混ざる: [[subagent-side-effect-guardrails]])。

**Step 5: commit**

```powershell
git add src/kxEdit.Core/IO/AtomicFile.cs tests/kxEdit.Core.Tests/IO/AtomicFileTests.cs
git commit -m "fix(core/io): rename 前に flushToDisk する(M-13)"
```

---

## Task 6: 設定の保存を `AtomicFile` 経由にする(M-11 前半)

**Files:**
- Modify: `src/kxEdit.Core/Settings/SettingsStore.cs:130-136`
- Modify: `tests/kxEdit.Core.Tests/Settings/SettingsStoreTests.cs`

**Step 1: 失敗するテストを書く**

```csharp
    /// <summary>
    /// 設計 §5.1: 書き手を File.WriteAllText から AtomicFile.Write へ替えても、
    /// ディスク上のバイト列は変わらない(エスケープと整形は JsonSerializerOptions が決めており、
    /// 書き手は関与しない。どちらも UTF-8・BOM なし)。想定のまま進めず、ここで固定する。
    /// </summary>
    [Fact]
    public void Save_writes_utf8_without_bom_and_keeps_the_same_bytes()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var s = new AppSettings { FontName = "BIZ UDゴシック" };
            SettingsStore.Save(path, s);

            byte[] actual = File.ReadAllBytes(path);
            Assert.NotEqual(0xEF, actual[0]); // BOM を書かない
            // 期待は「JsonSerializer が作る文字列の UTF-8 表現」そのもの。
            Assert.Equal(File.ReadAllText(path), Encoding.UTF8.GetString(actual));
            Assert.Contains("BIZ UD", File.ReadAllText(path)); // 日本語が \uXXXX でも読める形で残る
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>差替の部分失敗で設定ファイルを失わない(= AtomicFile を通っている)。</summary>
    [Fact]
    public void Save_goes_through_AtomicFile()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            SettingsStore.Save(path, new AppSettings());
            bool commitCalled = false;
            using (
                kxEdit.Core.IO.AtomicFile.OverrideCommitForTest(
                    (tmp, dest, destExists) =>
                    {
                        commitCalled = true;
                        File.Move(tmp, dest, overwrite: true);
                    }
                )
            )
            {
                SettingsStore.Save(path, new AppSettings { FontSize = 20 });
            }
            Assert.True(commitCalled);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
```

**Step 2: 失敗を確認する**

```powershell
dotnet test tests/kxEdit.Core.Tests -c Debug --filter "FullyQualifiedName~Save_goes_through_AtomicFile"
```
期待: `Assert.True(commitCalled)` が FAIL(まだ `File.WriteAllText` を通っている)。

**Step 3: 実装する**

```csharp
    /// <summary>
    /// settings.json を原子的に書く(M-11)。<see cref="IO.AtomicFile"/> を通すことで、
    /// 書込中の電源断やクラッシュで settings.json が半端な状態になることを防ぐ。
    /// Directory.CreateDirectory は AtomicFile が行わないため、ここで残す。
    /// </summary>
    public static void Save(string path, AppSettings settings)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        IO.AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(settings, Options));
    }
```

**Step 4 / 5: 確認して commit**

```powershell
dotnet build kxEdit.sln -c Debug -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Debug --no-build
dotnet test tests/kxEdit.App.Tests  -c Debug --no-build
git add src/kxEdit.Core/Settings/SettingsStore.cs tests/kxEdit.Core.Tests/Settings/SettingsStoreTests.cs
git commit -m "fix(core/settings): 設定の保存を AtomicFile 経由にする(M-11)"
```

---

## Task 7: 読込を 4 状態にする(M-11 後半)

**Files:**
- Create: `src/kxEdit.Core/Settings/SettingsLoadStatus.cs`
- Modify: `src/kxEdit.Core/Settings/SettingsStore.cs:20-32`
- Modify: `tests/kxEdit.Core.Tests/Settings/SettingsStoreTests.cs`(既存 21 か所を `out _` へ)
- Modify: `tests/kxEdit.App.Tests/MainFormSmokeTests.cs`(既存 2 か所を `out _` へ)

**Step 1: 失敗するテストを書く**

```csharp
    [Fact]
    public void Missing_file_reports_Missing_and_does_not_warn()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        // 「ファイルが無い」状態そのものを作る(既定値と区別するために、非既定値を書いてから
        // 消す等はしない。ここで見たいのは status であって設定値ではない)。
        var s = SettingsStore.Load(path, out var status);

        Assert.Equal(SettingsLoadStatus.Missing, status);
        Assert.Equal(new AppSettings().FontName, s.FontName);
    }

    [Fact]
    public void Broken_json_reports_Corrupt()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{ this is not json");
            SettingsStore.Load(path, out var status);
            Assert.Equal(SettingsLoadStatus.Corrupt, status);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// 内容が "null" の 4 文字。JSON としては妥当だが設定は失われるので、破損と同じ扱いにする
    /// (現状は `?? new AppSettings()` で成功扱いになっており、これが無音リセットの本体)。
    /// </summary>
    [Fact]
    public void Json_null_document_reports_Corrupt()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "null");
            SettingsStore.Load(path, out var status);
            Assert.Equal(SettingsLoadStatus.Corrupt, status);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// 読めないだけのファイルを Corrupt にしてはいけない。Corrupt は Task 8 で
    /// 退避(改名)に繋がるため、中身が正常なファイルを改名してしまう。
    /// </summary>
    [Fact]
    public void Locked_file_reports_Unreadable_not_Corrupt()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            SettingsStore.Save(path, new AppSettings());
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                SettingsStore.Load(path, out var status);
                Assert.Equal(SettingsLoadStatus.Unreadable, status);
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Valid_file_reports_Ok()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            SettingsStore.Save(path, new AppSettings { FontSize = 18 });
            var s = SettingsStore.Load(path, out var status);
            Assert.Equal(SettingsLoadStatus.Ok, status);
            Assert.Equal(18, s.FontSize);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
```

**Step 2: 失敗を確認する** → `SettingsLoadStatus` が無くてビルドエラー。

**Step 3: 実装する**

`src/kxEdit.Core/Settings/SettingsLoadStatus.cs`:

```csharp
namespace kxEdit.Core.Settings;

/// <summary>
/// settings.json の読込結果(設計 2026-09-02 §5.2)。旧実装は catch-all で
/// この 4 状態を 1 つに潰しており、破損しても無言で既定値へ戻っていた。
/// </summary>
public enum SettingsLoadStatus
{
    /// <summary>読めて、解釈できた。</summary>
    Ok,

    /// <summary>ファイルが無い(初回起動)。通知しない。</summary>
    Missing,

    /// <summary>読めたが JSON として解釈できない(内容が "null" の場合を含む)。通知し、退避する。</summary>
    Corrupt,

    /// <summary>I/O で読めない(ロック・権限)。通知するが<b>退避しない</b>
    /// —— 中身が正常なファイルを改名してしまうため。</summary>
    Unreadable,
}
```

`SettingsStore.Load` を差し替える:

```csharp
    /// <summary>
    /// settings.json を読む。壊れていれば既定値で続行するが、<b>その事実を
    /// <paramref name="status"/> で返す</b>(設計 2026-09-02 §5.2)。
    /// <para>
    /// <b>status を落とせるオーバーロードは意図的に用意していない。</b> 用意すると、
    /// 呼出側が破損の信号を黙って捨てられる状態が復活する(CLAUDE.md §6 / Issue #48 の
    /// 「網に見えるがゲート上は無効」と同型の、嘘の安全宣言)。status を見ない呼出は
    /// <c>out _</c> と書くことで、見ていないことがコード上に残る。
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
```

**既存の呼出 23 か所を `out _` にする**:
`tests/kxEdit.Core.Tests/Settings/SettingsStoreTests.cs`(21)/
`tests/kxEdit.App.Tests/MainFormSmokeTests.cs`(2)。
本番の呼出(`src/kxEdit.App/Program.cs:20`)は Task 8 で置き換えるので、
このタスクでは一旦 `out _` にしておく。

**Step 4 / 5: 確認して commit**

```powershell
dotnet build kxEdit.sln -c Debug -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Debug --no-build
dotnet test tests/kxEdit.App.Tests  -c Debug --no-build
git add src/kxEdit.Core/Settings/ tests/kxEdit.Core.Tests/Settings/SettingsStoreTests.cs tests/kxEdit.App.Tests/MainFormSmokeTests.cs src/kxEdit.App/Program.cs
git commit -m "fix(core/settings): 読込結果を 4 状態で返す(M-11)"
```

---

## Task 8: 破損ファイルの退避と起動時の判定

**★ 前倒しレビュー該当: パス操作(`File.Move`)を伴う → 脆弱性レビュー。**

**Files:**
- Modify: `src/kxEdit.Core/Settings/SettingsStore.cs`(`TryQuarantineCorrupt` を追加)
- Create: `src/kxEdit.App/SettingsStartup.cs`
- Create: `tests/kxEdit.App.Tests/SettingsStartupTests.cs`
- Modify: `tests/kxEdit.Core.Tests/Settings/SettingsStoreTests.cs`

**Step 1: 失敗するテストを書く**

`SettingsStoreTests.cs`:

```csharp
    [Fact]
    public void Quarantine_moves_the_broken_file_aside_and_keeps_its_content()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        string bad = path + ".bad";
        try
        {
            File.WriteAllText(path, "{ broken");
            Assert.True(SettingsStore.TryQuarantineCorrupt(path, out string quarantined));

            Assert.Equal(bad, quarantined);
            Assert.False(File.Exists(path));
            Assert.Equal("{ broken", File.ReadAllText(bad)); // 中身を失わない
        }
        finally
        {
            foreach (string f in new[] { path, bad })
                if (File.Exists(f))
                    File.Delete(f);
        }
    }

    /// <summary>2 回目の破損は 1 回目の退避を上書きする(最新の破損コピーだけ残す)。</summary>
    [Fact]
    public void Quarantine_overwrites_an_existing_bad_file()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        string bad = path + ".bad";
        try
        {
            File.WriteAllText(bad, "first");
            File.WriteAllText(path, "second");
            Assert.True(SettingsStore.TryQuarantineCorrupt(path, out _));
            Assert.Equal("second", File.ReadAllText(bad));
        }
        finally
        {
            foreach (string f in new[] { path, bad })
                if (File.Exists(f))
                    File.Delete(f);
        }
    }

    /// <summary>退避に失敗しても投げない(起動は続行する)。</summary>
    [Fact]
    public void Quarantine_returns_false_when_it_cannot_move()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{ broken");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.False(SettingsStore.TryQuarantineCorrupt(path, out _));
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
```

`tests/kxEdit.App.Tests/SettingsStartupTests.cs`(新規):

```csharp
using kxEdit.App;
using kxEdit.Core.Settings;
using Xunit;

namespace kxEdit.App.Tests;

/// <summary>
/// 設計 §5.4。Program.Main は STAThread + Application.Run のため直接テストできない。
/// 判定と文言の組み立てを SettingsStartup へ切り出し、Program.Main は 2 行にする。
/// </summary>
public class SettingsStartupTests
{
    [Fact]
    public void Missing_file_produces_no_warning()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        var (settings, warning) = SettingsStartup.Prepare(path);

        Assert.Null(warning); // 初回起動で警告を出さない
        Assert.Equal(new AppSettings().FontName, settings.FontName);
    }

    [Fact]
    public void Valid_file_produces_no_warning()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            SettingsStore.Save(path, new AppSettings { FontSize = 18 });
            var (settings, warning) = SettingsStartup.Prepare(path);

            Assert.Null(warning);
            Assert.Equal(18, settings.FontSize);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Corrupt_file_is_quarantined_and_the_warning_names_where_it_went()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        string bad = path + ".bad";
        try
        {
            File.WriteAllText(path, "{ broken");
            var (settings, warning) = SettingsStartup.Prepare(path);

            Assert.NotNull(warning);
            Assert.Contains(".bad", warning);
            Assert.True(File.Exists(bad));
            Assert.False(File.Exists(path));
            Assert.Equal(new AppSettings().FontName, settings.FontName);
        }
        finally
        {
            foreach (string f in new[] { path, bad })
                if (File.Exists(f))
                    File.Delete(f);
        }
    }

    /// <summary>
    /// 退避できなかったときは文言が変わる(「上書きされる」旨)。退避成功時と同じ文言だと、
    /// 実際には残っていない場所を案内することになる。
    /// </summary>
    [Fact]
    public void Corrupt_file_that_cannot_be_quarantined_warns_about_overwrite()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{ broken");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var (_, warning) = SettingsStartup.Prepare(path);
                Assert.NotNull(warning);
                Assert.DoesNotContain(".bad", warning);
                Assert.Contains("上書き", warning);
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>読めないだけのファイルは退避せず、通知だけする。</summary>
    [Fact]
    public void Unreadable_file_warns_but_is_not_quarantined()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        string bad = path + ".bad";
        try
        {
            SettingsStore.Save(path, new AppSettings { FontSize = 18 });
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var (_, warning) = SettingsStartup.Prepare(path);
                Assert.NotNull(warning);
                Assert.Contains("読み取れ", warning);
            }
            Assert.False(File.Exists(bad)); // 正常な中身のファイルを改名していない
            Assert.True(File.Exists(path));
        }
        finally
        {
            foreach (string f in new[] { path, bad })
                if (File.Exists(f))
                    File.Delete(f);
        }
    }
}
```

**Step 2: 失敗を確認する** → `TryQuarantineCorrupt` / `SettingsStartup` が無くてビルドエラー。

**Step 3: 実装する**

`SettingsStore` に追加:

```csharp
    /// <summary>
    /// 壊れた settings.json を <c>&lt;path&gt;.bad</c> へ退避する(設計 2026-09-02 §5.4)。
    /// <b><see cref="Load"/> には副作用を持たせず、退避は呼出側が明示的に行う。</b>
    /// 既存の .bad は上書きする(最新の破損コピーだけを残す)。
    /// 退避できなくても投げない —— 起動を止める理由にはならないため、成否を返して呼出側に判断させる。
    /// <b>.bad の掃除はしない</b>(自動削除すると「壊れた設定を後から見る」という退避の目的を潰す)。
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
            return false;
        }
    }
```

`src/kxEdit.App/SettingsStartup.cs`:

```csharp
using kxEdit.Core.Settings;
using kxEdit.Core.Text;

namespace kxEdit.App;

/// <summary>
/// 起動時の設定読込(設計 2026-09-02 §5.4)。判定と文言の組み立てをここへ寄せる
/// —— Program.Main は STAThread + Application.Run のため自動テストから叩けないので、
/// テストできる場所へ出す。
/// </summary>
internal static class SettingsStartup
{
    /// <summary>設定を読み、必要なら破損ファイルを退避し、起動後に出す警告文言を返す
    /// (警告不要なら null)。</summary>
    internal static (AppSettings Settings, string? Warning) Prepare(string path)
    {
        var settings = SettingsStore.Load(path, out var status);

        switch (status)
        {
            case SettingsLoadStatus.Corrupt:
                return (
                    settings,
                    SettingsStore.TryQuarantineCorrupt(path, out string quarantined)
                        ? "設定ファイルが壊れていたため、既定の設定で起動しました。\n\n"
                            + "壊れたファイルは次の場所に退避しました:\n  "
                            + SanitizeForDisplay.OneLine(quarantined, 260)
                        : "設定ファイルが壊れていたため、既定の設定で起動しました。\n\n"
                            + "壊れたファイルを退避できませんでした。設定を変更すると上書きされます。"
                );

            case SettingsLoadStatus.Unreadable:
                // 退避しない: 中身が正常なファイルを改名してしまうため(設計 §5.2)。
                // 保存も止めない: 止めると「設定を適用しました」が虚偽になり、B5 が潰す欠陥を
                // ここで新設することになる(設計 §5.5)。先に伝えることで代える。
                return (
                    settings,
                    "設定ファイルを読み取れなかったため、既定の設定で起動しました。\n\n"
                        + "設定を変更すると、読み取れなかったファイルは上書きされます。"
                );

            default:
                return (settings, null);
        }
    }
}
```

**Step 4 / 5: 確認して commit**

```powershell
dotnet build kxEdit.sln -c Debug -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Debug --no-build --filter "FullyQualifiedName~SettingsStore"
dotnet test tests/kxEdit.App.Tests  -c Debug --no-build --filter "FullyQualifiedName~SettingsStartup"
git add src/kxEdit.Core/Settings/SettingsStore.cs src/kxEdit.App/SettingsStartup.cs tests/
git commit -m "feat(app): 壊れた設定ファイルを退避し起動時の警告文言を組み立てる(M-11)"
```

**Step 6: 前倒し脆弱性レビュー(別エージェント)**

観点:
- `path + ".bad"` が、`path` が想定外(末尾のドット・予約名・長大パス)でも危険な位置を指さないか
- `overwrite: true` が消しうるのは自分が前に置いた `.bad` だけか
- `Unreadable` で退避しない判断が、実際にコード上で `Corrupt` とだけ結び付いているか

---

## Task 9: 起動時に 1 回通知する

**Files:**
- Modify: `src/kxEdit.App/Program.cs:20, 32`
- Modify: `src/kxEdit.App/MainForm.cs`(ctor 2 つ・`OnShown` `:261-300`・テスト seam)
- Modify: `tests/kxEdit.App.Tests/MainFormSmokeTests.cs`

**Step 1: 失敗するテストを書く**

`MainFormSmokeTests.cs` に追加(既存の `ShowMainForm` / `PumpUntilShown` / `Sta` を使う):

```csharp
    /// <summary>
    /// 設計 §5.4: 破損警告が OnShown で 1 回だけ出ること。MessageBox は blocking で
    /// 観測できないため、到達回数だけを数える(StaleBackupWarningCountForTest と同じ方式)。
    /// </summary>
    [Fact]
    public void Settings_warning_is_shown_once_on_startup()
    {
        Sta(() =>
        {
            using var tmp = new TempDir();
            var form = new MainForm(
                NewSettings(csvAutoModeOnOpen: false),
                tmp.SettingsPath,
                backupDirectory: tmp.BackupDir,
                sessionLayoutPath: tmp.LayoutPath,
                settingsWarning: "設定ファイルが壊れていました"
            );
            form.SetSuppressRestoreDialogsForTest(true);
            form.SetLastSessionBuffersPathForTest(tmp.BuffersPath);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-32000, -32000);
            form.ShowInTaskbar = false;
            form.Show();
            PumpUntilShown();

            Assert.Equal(1, form.SettingsWarningCountForTest);
            form.Close();
        });
    }

    /// <summary>警告が無いときは何も出さない(初回起動が警告を出さないことの網)。</summary>
    [Fact]
    public void No_settings_warning_means_no_dialog()
    {
        Sta(() =>
        {
            using var tmp = new TempDir();
            var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);
            form.SetSuppressRestoreDialogsForTest(true);
            PumpUntilShown();

            Assert.Equal(0, form.SettingsWarningCountForTest);
            form.Close();
        });
    }
```

**Step 2: 失敗を確認する** → `settingsWarning` / `SettingsWarningCountForTest` が無くてビルドエラー。

**Step 3: 実装する**

`MainForm` の ctor 2 つに末尾の任意引数を足す:

```csharp
    public MainForm(AppSettings settings, string? settingsWarning = null)
        : this(settings, SettingsStore.DefaultPath, settingsWarning: settingsWarning) { }

    internal MainForm(
        AppSettings settings,
        string settingsPath,
        string? backupDirectory = null,
        string? sessionLayoutPath = null,
        string? settingsWarning = null
    )
```

ctor 本体で `_settingsWarning = settingsWarning;`。フィールドと seam:

```csharp
    // 設計 2026-09-02 §5.4: 起動時の設定読込で破損/読取不能だったときの警告文言(null=正常)。
    // OnShown で 1 回だけ出し、そこで null に落とす。
    private string? _settingsWarning;

    // テスト用: 警告に到達した回数(抑止中でも数える)。MessageBox は blocking で観測できないため
    // 到達を数だけ観測して配線を固定する(StaleBackupWarningCountForTest と同じ方式)。
    private int _settingsWarningCountForTest;

    internal int SettingsWarningCountForTest => _settingsWarningCountForTest;
```

`OnShown` の**復元ブロックの `finally` を抜けた直後**、陳腐化警告より**前**に置く:

```csharp
        // 設計 2026-09-02 §5.4: 設定の破損/読取不能を 1 回だけ知らせる。
        // 復元より前に出さないのは、モーダルダイアログがメッセージをポンプするため
        // (MarkStartupRestoreComplete が走る前に再入経路を開くことになる。A-8 と同型のリスク)。
        // 陳腐化警告より前に出すのは、設定が既定値に戻っている事実が復元の挙動(ON/OFF)の
        // 説明にもなるため。
        if (_settingsWarning is { } settingsWarning)
        {
            _settingsWarning = null; // 二度出さない
            _settingsWarningCountForTest++;
            if (!_suppressRestoreDialogsForTest)
                MessageBox.Show(
                    this,
                    settingsWarning,
                    "設定を読み込めませんでした",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
        }
```

`Program.cs`:

```csharp
        // 設定は起動で1回だけ読む（起動時確定方針）。破損していれば退避し、
        // 警告文言は MainForm.OnShown が 1 回出す（この時点では発声手段がまだ無い）。
        var (settings, settingsWarning) = SettingsStartup.Prepare(SettingsStore.DefaultPath);
```

```csharp
        var form = new MainForm(settings, settingsWarning);
```

**Step 4 / 5: 確認して commit**

```powershell
dotnet build kxEdit.sln -c Debug -warnaserror
dotnet test tests/kxEdit.App.Tests -c Debug --no-build
git add src/kxEdit.App/MainForm.cs src/kxEdit.App/Program.cs tests/kxEdit.App.Tests/MainFormSmokeTests.cs
git commit -m "feat(app): 設定の破損/読取不能を起動時に 1 回知らせる(M-11)"
```

---

## Task 10: L5 チェックリストを起こす

**Files:**
- Create: `docs/plans/2026-09-02-save-last-line-of-defense-l5-checklist.md`

設計書 §7 の 5 項目を実施可能な手順へ落とす。**次の 2 点を必ず守る**:

1. **修正前でも全行 PASS する形になっていないこと。** 各項目に「この修正が無ければどう見えるか」を
   書き、それと観測が食い違うことを確認する(PR #62 の最終レビュー Critical-1 で踏んだ形)。
   例: 項目 1 は修正前だと**ダイアログが一切出ない**ので、「ダイアログが読まれる」は弁別できる。
2. **手順どおり操作すると必ず主張を踏むこと。** 例えば項目 4(設定の維持)は、
   **非既定の値**(フォントサイズ等)に変えてから再起動する —— 既定値のままだと
   「維持された」と「既定に戻った」が区別できない(CLAUDE.md §4-B)。

観測面は**本文・選択・発声文言・キャレット**の順で探す([[net-absence-claims-are-also-verifiable]])。
本件は発声文言(ダイアログのタイトルと本文)が主な観測面になる。

項目 5(大きい文書の Ctrl+S)は「体感で待たされない」という主観判定なので、
**Task 5 で採った実測値を併記**し、実機での判断材料にする。

```powershell
git add docs/plans/2026-09-02-save-last-line-of-defense-l5-checklist.md
git commit -m "docs(plans): B4 の L5 チェックリストを追加"
```

---

## 完了条件

1. Task 1〜10 が commit 済み。
2. **最終ブランチレビュー 2 パス**(CLAUDE.md §3-5): コード品質パスと脆弱性パスを
   **別々のエージェントで**起動する。指摘は fixup commit で反映(元 commit を書き換えない)。
3. `pwsh tools/pre-merge-check.ps1` が **EXIT 0**。
4. 設計書 §10「実施記録」に次を書く:
   - Task 3 のミューテーション 2 変異の実測結果(どのテストが赤になったか)
   - Task 5 の保存時間の実測値(fsync 前後)
   - Task 5 で「網が張れない」と判断した箇所
   - 計画と実物が食い違った点(この計画のコードは検証すべき案である)
5. L5 は**このブランチでは実施しない**(傘設計書 §7: B1〜B6 完了後にまとめて 1 回)。
   PR description にその旨と、起こした L5 項目数を書く。
6. PR 作成(日本語 description・目的 / レビュー経緯 / 申し送り)。
