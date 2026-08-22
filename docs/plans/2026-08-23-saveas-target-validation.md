# 「名前を付けて保存」の保存先確定を健全化する 実装計画(A-7 / A-4 / A-19)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** `SaveAsDocument` がダイアログの戻り値を無検証で保存先に使う穴を塞ぎ、上書き確認・他タブ重複検知・相対パス正規化・ネットワーク共有への新規保存を成立させる。

**Architecture:** `IReachabilityProbe` に「保存先を 1 回の境界付き I/O で調べる」メソッドを足し、到達性(A-4)と既存有無(A-7 (a))を同時に得る。`SaveAsDocument` を「警告したらダイアログへ戻す」ループへ組み替え、正規化(A-19)・重複タブ照合(A-7 (b))・上書き確認(A-7 (a))をそのループ内の検証段として順に積む。

**Tech Stack:** C# / .NET 9 / WinForms / xUnit。設計書 = `docs/plans/2026-08-23-saveas-target-validation-design.md`(**着手前に必読**)。

---

## 事前に頭へ入れること

> **Task 1 のレビューで seam 名が変わりました(2026-08-23)。**
> `ProbeWithTimeout` → **`ProbeFileExistsWithTimeout`**(A-4 の機構は「到達性の名前で存在確認を
> 実装したメソッドを書き込み側が名前を信じて再利用した」ことなので、述語をそのまま名前にした)、
> `SaveTargetProbe` → **`SaveTargetProbeResult`**(同フォルダーの `Result`/`Outcome` 接尾に合わせた)。
> Task 2 以降のコードブロックは新名に更新済み。**Task 1 節だけは策定時のまま**残してある
> (何を指示したかの記録)ので、そこを読むときは読み替えること。
> 実装は `WaitBounded<T>` / `RunSaveTargetProbe` という internal seam も持つ(タイムアウト経路を
> 決定的にテストするため)。詳細は commit `c9193da` を参照。

- **CLAUDE.md §3 が本計画に優先する。** 各タスク = 実装 → 仕様レビュー。前倒しレビューの指定があるタスク(Task 1 / Task 5)はそこで追加レビューを行う。
- **`File.Exists` を素で書かない。** 切断済み SMB 共有では UI スレッドが 60 秒固まる(PR #42 の脆弱性レビュー H-1 で踏んだ罠)。保存先への I/O は `TryInspectSaveTarget` を通す。
- **`--filter` を絞ったままミューテーション検証の結論を出さない**(過去に誤った結論を出した実績あり)。変異の生死は App プロジェクト全体を走らせて判定する。
- App 層のパス操作は `System.IO.Path` / `System.IO.File` と**完全修飾**で書く(`SaveAsDialog.cs` の既存慣習に合わせる)。
- 日本語のユーザー可視文字列に含めるパスは必ず `SanitizeForDisplay.OneLine(path, 200)` を通す(CSV-L-5)。

**共通の検証コマンド:**

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

---

## Task 1: 保存先プローブの seam を作る

> **このタスクは後続 5 タスクが依存する新しい抽象を導入する。CLAUDE.md §3 の前倒し例外に該当するので、実装後に「コード品質レビュー」を別エージェントで実施すること。**

**Files:**
- Modify: `src/kxEdit.App/Abstractions/IReachabilityProbe.cs`
- Modify: `src/kxEdit.App/FileReachabilityProbe.cs`
- Modify: `tests/kxEdit.App.Tests/Fakes/FakeReachabilityProbe.cs`
- Create: `tests/kxEdit.App.Tests/TempDir.cs`
- Modify: `tests/kxEdit.App.Tests/FileControllerTests.cs`(入れ子 `TempDir` の削除のみ)
- Create: `tests/kxEdit.App.Tests/FileReachabilityProbeTests.cs`

### Step 1: `TempDir` を共有クラスへ切り出す

`FileControllerTests` の入れ子 private クラスを名前空間直下へ移すだけ。**呼出側は 1 行も変えない**(同名・同名前空間なので `new TempDir()` はそのまま解決する)。

Create `tests/kxEdit.App.Tests/TempDir.cs`:

```csharp
using Directory = System.IO.Directory;
using IOException = System.IO.IOException;

namespace kxEdit.App.Tests;

/// <summary>テスト毎に使い捨ての一時フォルダ(実ファイル I/O 用)。</summary>
internal sealed class TempDir : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("kxEditAppTests_").FullName;

    public string File(string name) => System.IO.Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { /* 掃除失敗はテスト失敗にしない(読み取り専用属性等は UnauthorizedAccessException) */
        }
    }
}
```

`FileControllerTests.cs` から入れ子の `TempDir` クラス定義(`/// <summary>テスト毎に使い捨ての…` から対応する閉じ括弧まで)を削除する。

### Step 2: ビルドして「呼出側 0 変更」を確認

Run: `dotnet build kxEdit.sln -c Release -warnaserror`
Expected: 成功・0 warning。**ここで `FileControllerTests` に赤や修正が要るなら移設に失敗している**(名前解決を壊した)。

### Step 3: 失敗するテストを書く(本番プローブの意味論)

Create `tests/kxEdit.App.Tests/FileReachabilityProbeTests.cs`:

```csharp
using Directory = System.IO.Directory;
using File2 = System.IO.File;

namespace kxEdit.App.Tests;

/// <summary>
/// 本番プローブ <see cref="FileReachabilityProbe"/> の意味論テスト。
/// v0.2 監査 A-4 が「FakeReachabilityProbe で固定値を返すため実 Probe の意味論は未検証」と
/// 名指しした穴を塞ぐ。<c>Reachable = FileExists || 親フォルダー存在</c> の <c>||</c> を
/// kill できるのはこのファイルだけ(FileControllerTests は Fake 経由なので届かない)。
/// </summary>
public class FileReachabilityProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void ProbeSaveTarget_ExistingFile_ReachableAndExists()
    {
        using var tmp = new TempDir();
        string path = tmp.File("a.txt");
        File2.WriteAllText(path, "x");

        var result = new FileReachabilityProbe().ProbeSaveTargetWithTimeout(path, Timeout);

        Assert.True(result.Reachable);
        Assert.True(result.FileExists); // 上書き確認(A-7 (a))の入力
    }

    [Fact]
    public void ProbeSaveTarget_NewNameInExistingDir_ReachableAndNotExists()
    {
        // A-4 の核。旧 ProbeWithTimeout(File.Exists 意味論)はここで false を返し、
        // 「ネットワークパスに到達できません」でネットワーク共有への新規保存を止めていた。
        using var tmp = new TempDir();

        var result = new FileReachabilityProbe()
            .ProbeSaveTargetWithTimeout(tmp.File("not-yet.txt"), Timeout);

        Assert.True(result.Reachable);
        Assert.False(result.FileExists);
    }

    [Fact]
    public void ProbeSaveTarget_UnderMissingDir_NotReachable()
    {
        using var tmp = new TempDir();

        var result = new FileReachabilityProbe()
            .ProbeSaveTargetWithTimeout(
                System.IO.Path.Combine(tmp.Root, "no-such-dir", "a.txt"),
                Timeout
            );

        Assert.False(result.Reachable);
        Assert.False(result.FileExists);
    }

    [Fact]
    public void ProbeSaveTarget_DriveRoot_NotReachable()
    {
        // ルート自体("C:\")はファイルとして保存できない=親フォルダーが無い。
        // ローカルパスをハードコードしない(pre-commit の no-local-paths 対策)ため
        // 一時フォルダのルートから導出する。
        using var tmp = new TempDir();
        string root = System.IO.Path.GetPathRoot(tmp.Root)!;
        Assert.True(Directory.Exists(root)); // 前提の自己検証(root が空なら以下は無意味)

        var result = new FileReachabilityProbe().ProbeSaveTargetWithTimeout(root, Timeout);

        Assert.False(result.Reachable);
    }
}
```

**タイムアウト経路のテストは書かない(意図的)**。`task.Wait(TimeSpan.Zero)` は「タスクが先に完了する」レースを持ち、確実に false にできない。フレーキーなテストを増やすより、5 秒契約は Task 2 の `#12`(コントローラが渡す値の pin)で固定し、`task.Wait(timeout) ? … : (false, false)` は読解で担保する。

### Step 4: 実行して失敗を確認

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~FileReachabilityProbeTests"`
Expected: **コンパイルエラー** `'IReachabilityProbe' does not contain a definition for 'ProbeSaveTargetWithTimeout'`

### Step 5: 型とインターフェースを足す

`src/kxEdit.App/Abstractions/IReachabilityProbe.cs` を次の内容にする:

```csharp
namespace kxEdit.App;

/// <summary>
/// 保存先を 1 回の境界付き I/O で調べた結果(A-4 / A-7)。
/// <paramref name="Reachable"/> = 書き込み先が確定できる(ファイルが在る、または親フォルダーが在る)。
/// <paramref name="FileExists"/> = 上書きになる。タイムアウト時は (false, false)。
/// </summary>
public readonly record struct SaveTargetProbe(bool Reachable, bool FileExists);

/// <summary>
/// パスへの到達可否を短時間で判定する DI シーム(HIGH-6)。
/// 本番は <see cref="FileReachabilityProbe"/> / テストは Fake を差し込む。
/// UNC ロード時の 60 秒 UI 凍結を 5 秒プローブで回避するために FileController が使う。
/// </summary>
public interface IReachabilityProbe
{
    /// <summary>到達確認済 = true / タイムアウトまたは到達不可 = false。**読み取り側専用**。</summary>
    bool ProbeWithTimeout(string path, TimeSpan timeout);

    /// <summary>
    /// 保存先の到達性と既存有無を 1 回の境界付き I/O で得る(A-4 / A-7)。
    /// <see cref="ProbeWithTimeout"/> は File.Exists 意味論なので、存在しない新規パスを
    /// 到達不能と誤判定する(= A-4 の機構)。**書き込み側はこちらを使う**。
    /// 2 つの述語を 1 タスクにまとめてあるのは、遠隔共有での待ちを 5 秒 1 回に収めるため。
    /// </summary>
    SaveTargetProbe ProbeSaveTargetWithTimeout(string path, TimeSpan timeout);
}
```

### Step 6: 本番実装を足す

`src/kxEdit.App/FileReachabilityProbe.cs` の `ProbeWithTimeout` の下に追記:

```csharp
    /// <inheritdoc />
    public SaveTargetProbe ProbeSaveTargetWithTimeout(string path, TimeSpan timeout)
    {
        var task = Task.Run(() =>
        {
            try
            {
                bool fileExists = File.Exists(path);
                string? dir = System.IO.Path.GetDirectoryName(path);
                // dir が null/空 = ルート自体("C:\")を指す入力。ファイルとしては保存できないので
                // 到達不能側に落とす(親フォルダーが無い=書き込み先が確定しない)。
                bool dirExists = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
                return new SaveTargetProbe(fileExists || dirExists, fileExists);
            }
            catch
            {
                // File.Exists / Directory.Exists は通常投げないが、UNC 未到達などで稀に
                // IOException 系が出る可能性を吸って「到達不能」に倒す(ProbeWithTimeout と同方針)。
                return new SaveTargetProbe(false, false);
            }
        });
        return task.Wait(timeout) ? task.Result : new SaveTargetProbe(false, false);
    }
```

ファイル冒頭の `using System.IO;` はそのまま(`Directory` もこれで解決する)。

### Step 7: Fake を更新する

`tests/kxEdit.App.Tests/Fakes/FakeReachabilityProbe.cs` に追記:

```csharp
    /// <summary>
    /// <c>ProbeSaveTargetWithTimeout</c> の応答。既定は「到達可能・未存在」= 新規保存が通る形。
    /// 旧 <see cref="Result"/>(bool)とは**独立**に設定できる必要がある: 同値に縛ると
    /// A-4 の本質(到達可能かつ非存在)を表現できない。
    /// </summary>
    public SaveTargetProbe SaveTargetResult { get; set; } = new(Reachable: true, FileExists: false);

    public int SaveTargetCallCount { get; private set; }

    /// <summary>直近の <c>ProbeSaveTargetWithTimeout</c> 呼出で渡された timeout(5s 契約の pin)。</summary>
    public TimeSpan SaveTargetLastTimeout { get; private set; }

    public SaveTargetProbe ProbeSaveTargetWithTimeout(string path, TimeSpan timeout)
    {
        SaveTargetCallCount++;
        SaveTargetLastTimeout = timeout;
        return SaveTargetResult;
    }
```

### Step 8: テストを実行して緑を確認

Run: `dotnet build kxEdit.sln -c Release -warnaserror` → 0 warning
Run: `dotnet test tests/kxEdit.App.Tests -c Release --no-build`
Expected: 全緑(新規 4 件を含む)

### Step 9: コミット

```bash
git add src/kxEdit.App/Abstractions/IReachabilityProbe.cs src/kxEdit.App/FileReachabilityProbe.cs tests/kxEdit.App.Tests/
git commit -m "feat(app): 保存先の到達性と既存有無を 1 回の境界付き I/O で得る seam を追加"
```

### Step 10: コード品質レビュー(前倒し・別エージェント)

新 seam の命名・契約・Fake の表現力・`TempDir` 移設の副作用を見てもらう。指摘は fixup commit で反映する。

---

## Task 2: A-4 — 書き込み経路を保存先意味論へ切り替える

**Files:**
- Modify: `src/kxEdit.App/FileController.cs`
- Modify: `tests/kxEdit.App.Tests/FileControllerTests.cs`

### Step 1: 失敗するテストを書く

`FileControllerTests` の SaveAs 節末尾に追記:

```csharp
    // ===== A-4: ネットワーク共有への新規保存(保存先意味論のプローブ) =====

    /// <summary>
    /// A-4 の回帰。読み取り側の ProbeFileExistsWithTimeout(File.Exists 意味論)を使い続けていると、
    /// 存在しない新規パスは到達可能でも常に false=「ネットワークパスに到達できません」で止まる。
    /// Fake の Result(旧)を false・SaveTargetResult(新)を到達可能にすることで、
    /// **どちらのメソッドを使っているか**を判別する(同値だと判別できない)。
    /// 実ネットワークは無いので書込自体は失敗する。検証するのは「止まった理由」。
    /// </summary>
    [Fact]
    public void SaveAs_NewFileOnUncPath_PassesReachabilityGate() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Probe.Result = false; // 旧メソッドを使っていたら到達性エラーで止まる
            host.Probe.SaveTargetResult = new SaveTargetProbeResult(Reachable: true, FileExists: false);
            host.Dialogs.SaveAs = new SaveAsResult(
                @"\\no-such-server\share\a.txt",
                65001,
                HasBom: false,
                LineEnding.Crlf
            );

            Assert.False(host.File.SaveAs()); // 実ネットワーク不在なので書込は失敗する

            Assert.DoesNotContain(
                host.Prompt.Log,
                e => e.Text.StartsWith("ネットワークパスに到達できません", StringComparison.Ordinal)
            );
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "Error" && e.Text.StartsWith("保存できませんでした", StringComparison.Ordinal)
            );
        });

    /// <summary>5 秒契約の pin(旧 LastTimeout の観測点と対称)。</summary>
    [Fact]
    public void SaveAs_UncPath_ProbesSaveTargetWithFiveSecondTimeout() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAs = new SaveAsResult(
                @"\\no-such-server\share\a.txt",
                65001,
                HasBom: false,
                LineEnding.Crlf
            );

            host.File.SaveAs();

            Assert.True(host.Probe.SaveTargetCallCount >= 1);
            Assert.Equal(TimeSpan.FromSeconds(5), host.Probe.SaveTargetLastTimeout);
        });

    /// <summary>
    /// 設計書 §3.3: ローカルパスはリモートゲートで素通りする(挙動不変)。
    /// ゲートを外すと「存在しないフォルダー配下への保存」がプローブで弾かれ、
    /// SaveAs_WriteFailure_RollsBackEncodingBomEol_AndKeepsPath が WriteToPath に届かなくなる。
    /// </summary>
    [Fact]
    public void SaveAs_LocalNewFile_DoesNotProbe() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAs = new SaveAsResult(
                tmp.File("a.txt"),
                65001,
                HasBom: false,
                LineEnding.Crlf
            );

            Assert.True(host.File.SaveAs());

            Assert.Equal(0, host.Probe.SaveTargetCallCount);
        });
```

### Step 2: 実行して失敗を確認

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~SaveAs_NewFileOnUncPath"`
Expected: FAIL(`SaveTargetCallCount` は 0 のまま / 到達性エラーが出る)

### Step 3: `FileController` にヘルパを 2 本足す

`TryProbeReachability`(`FileController.cs:965` 付近)のすぐ下に追記:

```csharp
    /// <summary>
    /// 保存先の既存有無を得る。到達不能なら false(エラー表示済み)。
    /// リモート(UNC / マップドネットワークドライブ)だけを 5 秒プローブに載せる。
    /// ローカルを素通りさせるのは意図的(設計書 §3.3): ローカルには従来から到達性検査が無く、
    /// ここでゲートを外すと「存在しないフォルダー配下への保存」が WriteToPath の
    /// ロールバック導線に届かなくなる=挙動が変わる。
    /// </summary>
    private bool TryInspectSaveTarget(string path, out bool exists)
    {
        if (RemotePathDetector.IsRemote(path))
        {
            var probe = _reachabilityProbe.ProbeSaveTargetWithTimeout(path, TimeSpan.FromSeconds(5));
            exists = probe.FileExists;
            if (!probe.Reachable)
            {
                ReportUnreachable(path);
                return false;
            }
            return true;
        }
        // ローカルは SMB 60 秒凍結の懸念がない。
        exists = System.IO.File.Exists(path);
        return true;
    }

    /// <summary>
    /// 到達不能の通知。CSV-L-5: path は外部入力(SR ユーザーの直入力・grep / BackupRecord 由来)なので
    /// SanitizeForDisplay で無害化する。Task 4: 復元経路は per-file ダイアログを抑止する。
    /// </summary>
    private void ReportUnreachable(string path)
    {
        if (_suppressLoadErrorPrompt)
            return;
        _prompt.Error(
            $"ネットワークパスに到達できません: {SanitizeForDisplay.OneLine(path, 200)}",
            "エラー"
        );
    }
```

`TryProbeReachability` 本体の `_prompt.Error(...)` ブロックを `ReportUnreachable(path);` に置き換える(**文言・抑止条件は同一**=挙動不変)。

### Step 4: `WriteToPath` 冒頭を差し替える

```csharp
        // CSV-M-2 → A-4: リモートは 5 秒プローブ。存在確認ではなく「書き込み先が確定できるか」を見る
        // (旧 TryProbeReachability は File.Exists 意味論で、新規ファイルを常に到達不能と誤判定した)。
        // exists は書込側では使わない(上書き確認は SaveAsDocument が事前に済ませる)。
        if (!TryInspectSaveTarget(path, out _))
            return false;
```

### Step 5: 実行して緑を確認

Run: `dotnet build kxEdit.sln -c Release -warnaserror` → 0 warning
Run: `dotnet test tests/kxEdit.App.Tests -c Release --no-build`
Expected: 全緑。**特に `SaveAs_WriteFailure_RollsBackEncodingBomEol_AndKeepsPath` が緑のままであること**(赤ならリモートゲートを外してしまっている)。

### Step 6: コミット

```bash
git add src/kxEdit.App/FileController.cs tests/kxEdit.App.Tests/FileControllerTests.cs
git commit -m "fix(app): ネットワーク共有へ新規ファイルを保存できない問題を直す(A-4)"
```

---

## Task 3: テスト基盤 — ダイアログ Fake に結果キューを持たせる

**Files:**
- Modify: `tests/kxEdit.App.Tests/Fakes/FakeFileDialogService.cs`

src は変更しない。Task 4 以降のループテストが**無限ループにならない構造**を先に用意する。

### Step 1: Fake を書き換える

```csharp
    public string? OpenPath { get; set; }

    /// <summary>単一値の応答(従来 API)。**1 回目の呼出でだけ**返し、以降はキャンセル扱い。</summary>
    public SaveAsResult? SaveAs { get; set; }

    /// <summary>
    /// 複数回の応答(ダイアログ再表示のテスト用)。先頭から 1 件ずつ払い出す。
    /// **枯渇したらキャンセル(null)**にすることで、網の書き間違いが無限ループではなく
    /// 「PickSaveAsCount が想定と違う」という失敗として出る。
    /// </summary>
    public Queue<SaveAsResult?> SaveAsQueue { get; } = new();

    public int? EncodingCodePage { get; set; }

    public List<SaveAsRequest> SaveAsRequests { get; } = new();
    public int PickSaveAsCount => SaveAsRequests.Count;
    public int PickOpenCount;
    public int PickEncodingCount;

    public SaveAsResult? PickSaveAs(IWin32Window owner, SaveAsRequest current)
    {
        SaveAsRequests.Add(current); // 再表示時の初期値(seed)を検証する観測点
        if (SaveAsQueue.Count > 0)
            return SaveAsQueue.Dequeue();
        return SaveAsRequests.Count == 1 ? SaveAs : null;
    }
```

### Step 2: 既存テストが緑のままであることを確認

Run: `dotnet test tests/kxEdit.App.Tests -c Release`
Expected: 全緑(既存テストは `PickSaveAs` を 1 回しか呼ばないので挙動不変)

### Step 3: コミット

```bash
git add tests/kxEdit.App.Tests/Fakes/FakeFileDialogService.cs
git commit -m "test(app): SaveAs ダイアログ Fake に応答キューを足す(枯渇=キャンセルで無限ループを防ぐ)"
```

---

## Task 4: `SaveAsDocument` をループにする

**Files:**
- Modify: `src/kxEdit.App/FileController.cs`
- Modify: `tests/kxEdit.App.Tests/FileControllerTests.cs`

この段階では**検証段は増やさない**。既存の「空白パス」警告だけを再表示へ変え、ループの骨格を入れる。

### Step 1: 失敗するテストを書く

```csharp
    // ===== ダイアログ再表示ループ =====

    /// <summary>
    /// 空白パスの警告後に SaveAs 全体を中止せず、入力し直せるようにダイアログを再表示する。
    /// 「Warn が出たこと」だけを見ると continue → return false の変異が生き残るので、
    /// PickSaveAsCount で再表示そのものを固定する。
    /// </summary>
    [Fact]
    public void SaveAs_BlankPath_WarnsAndReopensDialog() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            string path = tmp.File("a.txt");
            host.Dialogs.SaveAsQueue.Enqueue(new SaveAsResult("   ", 65001, false, LineEnding.Crlf));
            host.Dialogs.SaveAsQueue.Enqueue(new SaveAsResult(path, 65001, false, LineEnding.Crlf));

            Assert.True(host.File.SaveAs()); // 2 回目の入力で保存が成立する

            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            Assert.Contains(host.Prompt.Log, e => e.Kind == "Warn" && e.Text.Contains("ファイル名"));
            Assert.True(File2.Exists(path));
        });

    /// <summary>キャンセルはループの唯一の途中出口。再表示しない。</summary>
    [Fact]
    public void SaveAs_Cancelled_WritesNothingAndDoesNotReopen() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAs = null; // キャンセル

            Assert.False(host.File.SaveAs());

            Assert.Equal(1, host.Dialogs.PickSaveAsCount);
            Assert.Null(doc.State.Path);
        });

    /// <summary>再表示のとき、直前に入力した値が初期値として戻る(打ち直しを強いない)。</summary>
    [Fact]
    public void SaveAs_Reopened_SeedsDialogWithPreviousInput() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc"; // 既定 State = UTF-8 / BOM なし / CRLF
            // 非既定のエンコード・改行で入力する(既定と同値だと seed の伝播を検証できない)。
            host.Dialogs.SaveAsQueue.Enqueue(new SaveAsResult("   ", 932, HasBom: true, LineEnding.Lf));

            host.File.SaveAs(); // 2 回目はキュー枯渇=キャンセル

            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            var second = host.Dialogs.SaveAsRequests[1];
            Assert.Equal("   ", second.Path);
            Assert.Equal(932, second.CodePage);
            Assert.True(second.HasBom);
            Assert.Equal(LineEnding.Lf, second.LineEnding);
        });
```

### Step 2: 実行して失敗を確認

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~SaveAs_BlankPath|FullyQualifiedName~SaveAs_Reopened"`
Expected: FAIL(`PickSaveAsCount` が 1)

### Step 3: `SaveAsDocument` を組み替える

`FileController.cs` の `SaveAsDocument` を次に置き換える(検証段は後続タスクで積む):

```csharp
    /// <summary>
    /// 指定ドキュメントを名前を付けて保存。成功で State.Path/Encoding/LineEnding とラベルを更新する。
    /// A-7 / A-4 / A-19(2026-08-23): 保存先を確定するまでダイアログを繰り返し表示する。
    /// 「ダイアログの中で選んだ値への警告なら、そのダイアログへ戻す」= 打ち直しを強いない
    /// (SR ユーザーの主経路はテキストボックス直入力なので、中止して開き直させる代償が大きい)。
    /// ループの途中出口はキャンセルだけで、すべての continue は PickSaveAs(ユーザー操作)を挟む。
    /// </summary>
    private bool SaveAsDocument(Document doc)
    {
        var seed = new SaveAsRequest(
            doc.State.Path,
            doc.State.Encoding.CodePage,
            doc.State.HasBom,
            doc.State.LineEnding
        );

        while (true)
        {
            var picked = _fileDialogs.PickSaveAs(_owner, seed);
            if (picked is null)
                return false;
            // 入力を次回の初期値として保つ。
            seed = new SaveAsRequest(
                picked.Path,
                picked.CodePage,
                picked.HasBom,
                picked.LineEnding
            );

            if (string.IsNullOrWhiteSpace(picked.Path))
            {
                _prompt.Warn("ファイル名を指定してください。", "エラー");
                continue;
            }

            var newEncoding = EncodingCatalog.Get(picked.CodePage);

            // C-2 追補 I-2: 選択エンコードで表せない文字があれば警告して続行/中止を選ばせる。
            // Load 経路の HadReplacementChar 警告と対称。UTF-8(65001) は BMP+astral 全表現可でスキップ。
            if (
                picked.CodePage != 65001
                && !CanEncodeBuffer(doc.Editor.CurrentBuffer, newEncoding)
                && !_prompt.OkCancel(
                    "選択した文字コードで表せない文字が含まれています。'?' として保存されデータが失われます。続行しますか?",
                    "文字コードの警告"
                )
            )
            {
                return false; // Task 8 で continue へ変える
            }

            // 新エンコード/改行/BOM を State に反映してから WriteToPath へ(既存 WriteToPath は State を参照する)。
            // C-2 追補 I-1: WriteToPath 失敗時は元の Encoding/LineEnding/HasBom へロールバック
            // (State だけ更新済で Path が旧のままだと後続の Ctrl+S が元ファイルを別エンコードで
            // サイレント上書きする=データ破損)。
            var oldEncoding = doc.State.Encoding;
            var oldLineEnding = doc.State.LineEnding;
            var oldHasBom = doc.State.HasBom;
            doc.State.Encoding = newEncoding;
            doc.State.LineEnding = picked.LineEnding;
            doc.State.HasBom = picked.HasBom;

            if (!WriteToPath(doc, picked.Path))
            {
                doc.State.Encoding = oldEncoding;
                doc.State.LineEnding = oldLineEnding;
                doc.State.HasBom = oldHasBom;
                return false;
            }
            doc.State.Path = picked.Path;
            DocumentManager.UpdateLabel(doc);
            _metaChanged();
            RegisterRecent(picked.Path); // 保存先も最近のファイルへ
            return true;
        }
    }
```

### Step 4: 緑を確認してコミット

Run: `dotnet build kxEdit.sln -c Release -warnaserror` → 0 warning
Run: `dotnet test tests/kxEdit.App.Tests -c Release --no-build` → 全緑

```bash
git add src/kxEdit.App/FileController.cs tests/kxEdit.App.Tests/FileControllerTests.cs
git commit -m "feat(app): SaveAs を「警告したらダイアログへ戻す」ループにする"
```

---

## Task 5: A-19 — 保存先パスを絶対パスへ正規化する

> **外部入力(SR ユーザーの直入力)のパス操作に触れる。CLAUDE.md §3 の前倒し例外に該当するので、実装後に「脆弱性レビュー」を別エージェントで実施すること。**

**Files:**
- Modify: `src/kxEdit.App/FileController.cs`
- Modify: `tests/kxEdit.App.Tests/FileControllerTests.cs`

### Step 1: 失敗するテストを書く

```csharp
    // ===== A-19: 保存先パスの正規化 =====

    /// <summary>
    /// A-19。相対パスを未正規化のまま State.Path に残すと保存先が CWD 依存になり、
    /// hot exit 復元で無言の無題化を招く。Environment.CurrentDirectory を触るが、
    /// App.Tests は GlobalUsings.cs で並列実行を無効化済み(CollectionBehavior)なので安全。
    /// </summary>
    [Fact]
    public void SaveAs_RelativePath_StoresAbsolutePath() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            string saved = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = tmp.Root;
                host.Dialogs.SaveAs = new SaveAsResult("memo.txt", 65001, false, LineEnding.Crlf);

                Assert.True(host.File.SaveAs());
            }
            finally
            {
                Environment.CurrentDirectory = saved;
            }

            // CreateTempSubdirectory は 8.3 名や symlink 経由のパスを返しうるので、
            // 期待値も GetFullPath を通してから比較する(区切り・大小の揺れは吸収しない)。
            string expected = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(tmp.Root, "memo.txt")
            );
            Assert.Equal(expected, doc.State.Path);
            Assert.Equal(expected, host.Settings.RecentFiles[0]); // RegisterRecent も正規化済みを使う
            Assert.True(File2.Exists(expected));
        });

    /// <summary>正規化不能な入力は握って「入力し直し」に落とす(未捕捉例外ダイアログにしない)。</summary>
    [Fact]
    public void SaveAs_UnnormalizablePath_WarnsAndReopens() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAsQueue.Enqueue(
                new SaveAsResult("bad\0name.txt", 65001, false, LineEnding.Crlf)
            );

            Assert.False(host.File.SaveAs()); // 2 回目はキュー枯渇=キャンセル

            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "Warn" && e.Text.StartsWith("パスが正しくありません", StringComparison.Ordinal)
            );
            Assert.Null(doc.State.Path);
        });
```

### Step 2: 実行して失敗を確認

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~SaveAs_RelativePath|FullyQualifiedName~SaveAs_Unnormalizable"`
Expected: FAIL(`State.Path` が `"memo.txt"` のまま / null 文字入力で未捕捉の `ArgumentException`)

### Step 3: 正規化ヘルパを足す

`CanEncodeBuffer` の下あたりに追記:

```csharp
    /// <summary>
    /// A-19: 直入力の相対パス(memo.txt)を絶対パスへ正規化する。未正規化のまま State.Path に
    /// 残すと保存先が起動時のカレントディレクトリに依存し、hot exit 復元で無言の無題化を招く。
    /// 例外(null 文字・無効文字・長大パス)は握って呼出側で「入力し直し」に落とす:
    /// SR ユーザーの直入力がそのまま届く面なので未捕捉例外ダイアログにしない。
    /// PathKey.For も内部で GetFullPath するが、あちらは失敗時に空文字へ落として dedup キーを
    /// 1 件へ集約する契約(CSV-L-8)= ユーザーに直させる本メソッドとは契約が違うので流用しない。
    /// </summary>
    private static bool TryNormalizeSavePath(string input, out string full)
    {
        try
        {
            full = System.IO.Path.GetFullPath(input);
            return true;
        }
        catch (Exception ex)
            when (ex
                    is ArgumentException
                        or NotSupportedException
                        or System.IO.PathTooLongException
                        or System.Security.SecurityException
            )
        {
            full = string.Empty;
            return false;
        }
    }
```

### Step 4: ループへ組み込む

空白チェックの直後に挿入:

```csharp
            if (!TryNormalizeSavePath(picked.Path, out string full))
            {
                _prompt.Warn(
                    $"パスが正しくありません: {SanitizeForDisplay.OneLine(picked.Path, 200)}",
                    "エラー"
                );
                continue;
            }
            // 以降の判定・保存・State 反映はすべて正規化済みの full を使う。
            // 再表示時も絶対パスを見せる(どこへ保存されるかが読み上げで分かる)。
            seed = seed with { Path = full };
```

`WriteToPath(doc, picked.Path)` → `WriteToPath(doc, full)`、`doc.State.Path = picked.Path` → `= full`、`RegisterRecent(picked.Path)` → `RegisterRecent(full)` に置き換える。**`picked.Path` の残りがないことを grep で確認する。**

Run: `grep -n "picked.Path" src/kxEdit.App/FileController.cs`
Expected: 空白チェックと正規化呼出・警告文言の 3 箇所のみ

### Step 5: 緑を確認してコミット

Run: `dotnet build kxEdit.sln -c Release -warnaserror` → 0 warning
Run: `dotnet test tests/kxEdit.App.Tests -c Release --no-build` → 全緑

```bash
git add src/kxEdit.App/FileController.cs tests/kxEdit.App.Tests/FileControllerTests.cs
git commit -m "fix(app): 名前を付けて保存の相対パスを絶対パスへ正規化する(A-19)"
```

### Step 6: 脆弱性レビュー(前倒し・別エージェント)

観点: 例外フィルタの網羅(握り漏れで未捕捉例外ダイアログが出ないか)・`SanitizeForDisplay` の適用漏れ・正規化前後で判定に使うパスが混ざっていないか(TOCTOU 的な取り違え)・`GetFullPath` が返す予約デバイス名(`CON` 等)の扱い。

---

## Task 6: A-7 (b) — 他タブで開いているファイルへの保存を止める

**Files:**
- Modify: `src/kxEdit.App/FileController.cs`
- Modify: `tests/kxEdit.App.Tests/FileControllerTests.cs`

### Step 1: 失敗するテストを書く

```csharp
    // ===== A-7 (b): 他タブ重複の検知 =====

    [Fact]
    public void SaveAs_PathOpenInAnotherTab_ShowsErrorAndReopens() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string occupied = tmp.File("occupied.txt");
            File2.WriteAllText(occupied, "original");
            Assert.NotNull(host.File.TryOpenOrActivate(occupied)); // タブ A

            var doc = host.Docs.CreateNew(); // タブ B(無題)
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAsQueue.Enqueue(new SaveAsResult(occupied, 65001, false, LineEnding.Crlf));

            Assert.False(host.File.SaveAs()); // 2 回目はキュー枯渇=キャンセル

            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "Error" && e.Text.Contains("別のタブで開いています")
            );
            Assert.Equal("original", File2.ReadAllText(occupied)); // 上書きされていない
            Assert.Null(doc.State.Path);
        });

    /// <summary>
    /// 自分自身のパスへの上書き保存は正当な操作。
    /// **非既定状態から始めるのが要点**: 無題タブ(State.Path == null)から始めると
    /// FindByPath は常に null を返し、「null が返った」と「自分が返った」を区別できない
    /// =自タブ除外(!ReferenceEquals)を落とす変異が生存する。
    /// パス確定済みの doc + 別パスの他タブ、という配置にする。
    /// </summary>
    [Fact]
    public void SaveAs_OwnPath_IsNotTreatedAsDuplicate() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string other = tmp.File("other.txt");
            File2.WriteAllText(other, "other");
            Assert.NotNull(host.File.TryOpenOrActivate(other)); // 別パスの他タブを在席させる

            string mine = tmp.File("mine.txt");
            File2.WriteAllText(mine, "old");
            var doc = host.File.TryOpenOrActivate(mine); // パス確定済みの自タブ
            Assert.NotNull(doc);
            doc!.Editor.Text = "new";
            host.Dialogs.SaveAs = new SaveAsResult(mine, 65001, false, LineEnding.Crlf);

            Assert.True(host.File.SaveAs());

            Assert.DoesNotContain(host.Prompt.Log, e => e.Text.Contains("別のタブで開いています"));
            Assert.Contains("new", File2.ReadAllText(mine));
        });
```

### Step 2: 実行して失敗を確認

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~SaveAs_PathOpenInAnotherTab"`
Expected: FAIL(エラーが出ず `occupied.txt` が上書きされる)

### Step 3: ループへ組み込む

`seed = seed with { Path = full };` の直後に挿入:

```csharp
            // A-7 (b): 同一ファイルを 2 タブで編集させない。片方の Ctrl+S が
            // もう片方の内容を無警告で消す導線(hot exit レイアウトにも同一 Path が 2 件並ぶ)。
            // FindByPath は PathKey(GetFullPath + ToLowerInvariant)照合なので
            // 大小・区切りの揺れも同一と見なす。自分自身への上書きは正当なので除外する。
            var other = _docs.FindByPath(full);
            if (other is not null && !ReferenceEquals(other, doc))
            {
                _prompt.Error(
                    $"このファイルは別のタブで開いています。そのタブで保存してください: {SanitizeForDisplay.OneLine(full, 200)}",
                    "エラー"
                );
                continue;
            }
```

### Step 4: 緑を確認してコミット

Run: `dotnet build kxEdit.sln -c Release -warnaserror` → 0 warning
Run: `dotnet test tests/kxEdit.App.Tests -c Release --no-build` → 全緑

```bash
git add src/kxEdit.App/FileController.cs tests/kxEdit.App.Tests/FileControllerTests.cs
git commit -m "fix(app): 他タブで開いているファイルへの名前を付けて保存を止める(A-7 (b))"
```

---

## Task 7: A-7 (a) — 上書き確認を全経路に付ける

> **着手前に申し送り S-5 を判断すること(Task 2 のレビューで追加)。**
> `TryInspectSaveTarget` のローカル枝は素の `System.IO.File.Exists` を打つ。Task 2 時点では
> 「直後に必ず同じパスへ書きに行く」ので待ちが相殺され問題にならなかったが、**本タスクで
> 上書き確認が入ると相殺されなくなる**(「いいえ」を選ぶと書き込みが発生しない)。さらに
> ループなので N 回繰り返せる。固定ドライブ上のジャンクションがネットワーク先を指す場合、
> UI が上限なくブロックしうる。
> 選択肢は (a) 現状維持 + PR に記載、(b) ローカル枝も `ProbeSaveTargetWithTimeout` を通し
> `Reachable` を無視して `exists` だけ採る(制御フローも文言も不変のまま 5 秒上限になる)。
> **(b) を採ると本タスクの上書き確認テストが「実ファイルがディスクに在る → 確認が出る」から
> 「Fake が在ると言った → 確認が出る」に変わり、本ブランチで最も重要な網が弱まる。**
> 判断と理由を報告に書くこと。設計書 §9 S-5 を参照。
>
> **あわせて既存テスト 1 本を強化すること(Tasks 3+4 のレビューで判明)。**
> `SaveAs_UncPath_ProbesSaveTargetWithFiveSecondTimeout` は `SaveTargetCallCount >= 1` としか
> assert していない。本タスクで上書き確認が入ると、Fake の `FileExists` 既定が変わったり
> テストが設定したりした瞬間に確認が発火 → `continue` → 2 回目のダイアログ → キュー枯渇で
> `null`、という**別の経路を通りながら緑のまま**になる。`Assert.Equal(1, …SaveTargetCallCount)`
> と `Assert.Equal(1, host.Dialogs.PickSaveAsCount)` を足して 1 周であることを固定する。

**Files:**
- Modify: `src/kxEdit.App/FileController.cs`
- Modify: `src/kxEdit.App/SaveAsDialog.cs`
- Modify: `tests/kxEdit.App.Tests/FileControllerTests.cs`

### Step 1: 失敗するテストを書く

```csharp
    // ===== A-7 (a): 上書き確認 =====

    [Fact]
    public void SaveAs_ExistingFile_AsksOverwriteConfirmation() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "original");
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "new";
            host.Dialogs.SaveAs = new SaveAsResult(path, 65001, false, LineEnding.Crlf);

            Assert.True(host.File.SaveAs()); // FakePrompt.OkCancelResult 既定 true = 上書き承諾

            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "OkCancel" && e.Caption == "上書きの確認"
            );
            Assert.Contains("new", File2.ReadAllText(path));
        });

    [Fact]
    public void SaveAs_OverwriteDeclined_KeepsFileAndReopensDialog() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "original");
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "new";
            host.Prompt.OkCancelResult = false; // 「いいえ」
            host.Dialogs.SaveAsQueue.Enqueue(new SaveAsResult(path, 65001, false, LineEnding.Crlf));

            Assert.False(host.File.SaveAs()); // 2 回目はキュー枯渇=キャンセル

            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            Assert.Equal("original", File2.ReadAllText(path)); // 上書きされていない
            Assert.Null(doc.State.Path);
        });

    /// <summary>
    /// 新規ファイルでは確認しない。FakePrompt.OkCancelResult の既定は true なので
    /// 「保存が成功した」だけでは確認の有無を区別できない(vacuous になる)。
    /// Log に OkCancel が**出ないこと**で固定する。
    /// </summary>
    [Fact]
    public void SaveAs_NewFile_DoesNotAskOverwrite() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAs = new SaveAsResult(tmp.File("fresh.txt"), 65001, false, LineEnding.Crlf);

            Assert.True(host.File.SaveAs());

            Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "OkCancel");
        });

    /// <summary>到達不能なリモート保存先はエラーにして再表示する(書込を試みない)。</summary>
    [Fact]
    public void SaveAs_UnreachableUncPath_ShowsErrorAndReopens() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Probe.SaveTargetResult = new SaveTargetProbeResult(Reachable: false, FileExists: false);
            host.Dialogs.SaveAsQueue.Enqueue(
                new SaveAsResult(@"\\no-such-server\share\a.txt", 65001, false, LineEnding.Crlf)
            );

            Assert.False(host.File.SaveAs());

            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "Error" && e.Text.StartsWith("ネットワークパスに到達できません", StringComparison.Ordinal)
            );
            // 書込は試みていない = WriteToPath の失敗エラーは出ない
            Assert.DoesNotContain(
                host.Prompt.Log,
                e => e.Text.StartsWith("保存できませんでした", StringComparison.Ordinal)
            );
        });
```

### Step 2: 実行して失敗を確認

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~SaveAs_ExistingFile|FullyQualifiedName~SaveAs_OverwriteDeclined"`
Expected: FAIL(確認が出ず無条件に上書きされる)

### Step 3: ループへ組み込む

重複タブ判定の直後、文字コード警告の前に挿入:

```csharp
            // A-4 / A-7 (a): 到達性と既存有無を 1 回の境界付き I/O で得る。
            // 素の File.Exists は切断済み SMB 共有で UI を 60 秒固める(PR #42 H-1 の罠)。
            if (!TryInspectSaveTarget(full, out bool targetExists))
                continue; // エラー表示は TryInspectSaveTarget の中

            if (
                targetExists
                && !_prompt.OkCancel(
                    $"{SanitizeForDisplay.OneLine(full, 200)} は既に存在します。上書きしますか?",
                    "上書きの確認"
                )
            )
            {
                continue;
            }
```

### Step 4: `SaveAsDialog` の二重確認を消す

`src/kxEdit.App/SaveAsDialog.cs` の `OnBrowseClicked` を変更:

```csharp
        using var dlg = new SaveFileDialog
        {
            // 上書き確認は FileController が全経路で 1 回だけ行う(A-7)。
            // ここで OverwritePrompt を有効にすると参照経由だけ 2 回確認が出る
            // (A-7 の訴えは「経路によって確認が出たり出なかったりする非対称」そのもの)。
            OverwritePrompt = false,
            Filter =
                "テキスト ファイル (*.txt)|*.txt|マークダウン ファイル (*.md)|*.md|CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
        };
```

### Step 5: 緑を確認してコミット

Run: `dotnet build kxEdit.sln -c Release -warnaserror` → 0 warning
Run: `dotnet test tests/kxEdit.App.Tests -c Release --no-build` → 全緑

```bash
git add src/kxEdit.App/FileController.cs src/kxEdit.App/SaveAsDialog.cs tests/kxEdit.App.Tests/FileControllerTests.cs
git commit -m "fix(app): 名前を付けて保存の全経路に上書き確認を付ける(A-7 (a))"
```

---

## Task 8: 文字コード劣化警告も再表示にする

> **無限ループのハザードが 1 つ増える(Tasks 3+4 のレビューで判明)。**
> 文字コード警告が `continue` になると、**テストで無限ループを止めているのはキューの枯渇だけ**に
> なる。`FakePrompt.OkCancelResult` は固定フィールドで永久に同じ答を返すため、
> 「警告 → キャンセル → 再表示 → 同じ入力 → 警告 → …」が自力では終わらない。
> **`SaveAsQueue` に「最後の値を繰り返す」モードを絶対に足さないこと。**
> 枯渇=キャンセルという Task 3 の設計は、この時点で唯一の停止保証になる。

**Files:**
- Modify: `src/kxEdit.App/FileController.cs`
- Modify: `tests/kxEdit.App.Tests/FileControllerTests.cs`

設計書 §4.5 の意図的な挙動変更。文字コードのコンボボックスは SaveAs ダイアログの中にあるので、
中止して開き直させるより戻すほうが自然。

### Step 1: 失敗するテストを書く

```csharp
    /// <summary>
    /// 文字コード劣化警告のキャンセルもダイアログへ戻す(選び直せる場所がそのダイアログだから)。
    /// 保存先は新規ファイルにして、上書き確認と OkCancelResult を取り合わないようにする。
    /// </summary>
    [Fact]
    public void SaveAs_EncodingWarningDeclined_ReopensDialog() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "絵文字 \U0001F600"; // SJIS(932)で表せない
            host.Prompt.OkCancelResult = false; // 警告に「キャンセル」
            host.Dialogs.SaveAsQueue.Enqueue(
                new SaveAsResult(tmp.File("a.txt"), 932, false, LineEnding.Crlf)
            );

            Assert.False(host.File.SaveAs()); // 2 回目はキュー枯渇=キャンセル

            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            Assert.Contains(host.Prompt.Log, e => e.Caption == "文字コードの警告");
            Assert.False(File2.Exists(tmp.File("a.txt")));
            Assert.Equal(65001, doc.State.Encoding.CodePage); // State は書き換わっていない
        });
```

### Step 2: 実行して失敗を確認

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~SaveAs_EncodingWarningDeclined"`
Expected: FAIL(`PickSaveAsCount` が 1)

### Step 3: `return false` を `continue` にする

文字コード警告ブロックの `return false;` を `continue;` に変え、コメントを更新する:

```csharp
            {
                continue; // 文字コードはこのダイアログで選び直せるので戻す(設計書 §4.5)
            }
```

### Step 4: 緑を確認してコミット

Run: `dotnet build kxEdit.sln -c Release -warnaserror` → 0 warning
Run: `dotnet test tests/kxEdit.App.Tests -c Release --no-build` → 全緑

```bash
git add src/kxEdit.App/FileController.cs tests/kxEdit.App.Tests/FileControllerTests.cs
git commit -m "feat(app): 文字コード劣化警告のキャンセルで SaveAs ダイアログへ戻す"
```

---

## Task 9: L5 チェックリストと設計書の実施記録

**Files:**
- Create: `docs/plans/2026-08-23-saveas-target-validation-l5-checklist.md`
- Modify: `docs/plans/2026-08-23-saveas-target-validation-design.md`(§10 実施記録を追記)

### Step 1: L5 チェックリストを書く

設計書 §8 の 4 項目を、NVDA での操作手順・期待読み上げ・PASS/FAIL 欄付きで起こす。
既存の `2026-08-22-*-l5-checklist.md` の書式に合わせる。

### Step 2: 設計書に §10 実施記録を足す

策定時スナップショットは書き換えず、末尾に追記する(CLAUDE.md §8)。最低限:

- §7.3 の P4(タイムアウト経路)は**書かなかった**。`task.Wait(TimeSpan.Zero)` はタスクが
  先に完了するレースを持ち、確実に false にできない。5 秒契約は `#12` で pin した。
- 実装時に増減した検証段・テスト名の実際の対応。

### Step 3: コミット

```bash
git add docs/plans/
git commit -m "docs(plans): L5 チェックリストを作成し設計書に実施記録を追記する"
```

---

## 仕上げ(CLAUDE.md §3 工程 5〜6)

1. **最終ブランチレビュー 2 パス**。**パスごとに独立した別エージェント**を起動する(混載しない)。
   - **コード品質パス**: ミューテーション検証のスポットチェックを設計書 §7.5 の 7 点で実施。
     変異の生死判定は `dotnet test tests/kxEdit.App.Tests -c Release`(**`--filter` で絞らない**)。
     変異は必ず元に戻すこと(戻し忘れの実績あり)。
   - **脆弱性パス**: 保存先パスの取り扱い(正規化前後の取り違え・TOCTOU・表示の無害化)、
     プローブの例外握り、`OverwritePrompt = false` にしたことで確認が抜ける経路がないか。
2. 指摘は **fixup commit** で積む(元 commit を書き換えない)。3 択(修正 / 受容して PR 記載 / 理由付き却下)を明示する。
3. **品質ゲート**: `powershell -File tools\pre-merge-check.ps1` → **EXIT 0**。
4. **PR 作成**(日本語)。description に書くこと:
   - 目的(A-7 / A-4 / A-19 と、束ねた理由=機構が依存する)
   - 設計書 §4.5 の**意図的な挙動変更 4 件**
   - 設計書 §3.3 の**策定時の誤りと訂正**(commit `78a858f`)
   - レビュー経緯・**L5 未実施**(4 項目・マージ前に必須)
   - 申し送り(設計書 §9 の S-1〜S-4)
