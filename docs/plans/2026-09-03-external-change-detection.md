# 外部変更の検知と読み直し確認(M-18)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 他プロセスが書き換えたファイルを、ウィンドウ復帰・タブ切替・保存直前の 3 点で検知し、読み直し / 上書きをユーザーに確認する。

**Architecture:** `DocumentState` にディスクの更新時刻の観測値を 1 つ持たせ、`LoadInto`(読む前)/ `WriteToPath`(書いた後)/ バックアップ復元で更新する。`FileController.CheckExternalChange` が観測値とディスクを完全一致で比べ、`IUserPrompt.YesNo`(新設)で確認して `LoadInto` で読み直す。`SaveDocument` は同じ比較を `OkCancel` で行う。MainForm は `OnActivated` と `ActiveDocumentChanged` から `BeginInvoke` 越しに呼び、読み直し後の発声と CSV モード復帰だけを担う。

**Tech Stack:** .NET 9 / WinForms / xUnit(L3 = `tests/kxEdit.App.Tests`)。設計書: `docs/plans/2026-09-03-external-change-detection-design.md`。

**共通ルール(計画全体に効く)**

- 計画のコードは**検証すべき案**であって正解ではない(`plan-code-is-not-ground-truth`)。コンパイルエラー・アナライザ error(`-warnaserror` 稼働中)・テスト失敗が出たら、計画ではなく実物に合わせて直し、逸脱を設計書 §11(実施記録・Task 7 で作る)へ書く。
- **ミューテーション検証はしない**(CLAUDE.md §4-A: ファイル I/O とイベント配線は禁止対象)。
- テストの実行は必ず `dotnet test ... -c Release`(ビルド込み。`--no-build` は使わない)。**落ちたテスト名と合格件数まで読む**(`mutation-harness-exit-code-trap`: ビルドが割れると 0 件実行で赤になる)。
- コミット前に `dotnet csharpier check <変更ファイル>`。pre-commit フックが整形するが、差分を自分で確認する。
- コミットメッセージは `feat|fix|test|docs(scope): 要約` + 日本語本文。`--no-verify` は使わない。
- 文言・キー名・ファイル名は設計書 §4 の表と**逐語一致**させる。

---

## Task 1: `IUserPrompt.YesNo` seam

**Files:**
- Modify: `src/kxEdit.App/Abstractions/IUserPrompt.cs`(`YesNoCancel` の直後)
- Modify: `src/kxEdit.App/MessageBoxUserPrompt.cs`
- Modify: `tests/kxEdit.App.Tests/Fakes/FakePrompt.cs`

`IUserPrompt` の実装は `MessageBoxUserPrompt` と `FakePrompt` の 2 つだけ(2026-09-03 に
`grep -rln ": IUserPrompt\|, IUserPrompt" src tests --include=*.cs` で確認)。着手時に同じ grep で再確認する。

**Step 1: interface にメソッドを足す**

`IUserPrompt.cs` の `DialogResult YesNoCancel(string text, string caption);` の直後に追加:

```csharp

    /// <summary>
    /// はい/いいえ(警告アイコン)。はいで true。M-18 の読み直し確認(設計 2026-09-03 §3.6)。
    /// <paramref name="defaultNo"/> = true でフォーカス既定を「いいえ」側に置く。
    /// <see cref="OkCancel"/> と同じく**既定値を持たせない**: 破壊的(未保存の変更を捨てる)なら true、
    /// 押し間違えても失うものが無いなら false を、呼出のたびにコンパイラが選ばせる。
    /// </summary>
    bool YesNo(string text, string caption, bool defaultNo);
```

**Step 2: 本番実装**

`MessageBoxUserPrompt.cs` の `YesNoCancel` の直後に追加:

```csharp

    public bool YesNo(string text, string caption, bool defaultNo) =>
        MessageBox.Show(
            text,
            caption,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            defaultNo ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1
        ) == DialogResult.Yes;
```

**Step 3: Fake**

`FakePrompt.cs` の `YesNoCancel` の直後に追加:

```csharp

    public bool YesNoResult { get; set; } = true;

    public List<(string Caption, bool DefaultNo)> YesNoCalls { get; } = new();

    /// <summary>M-18 再入テスト用: 応答を返す直前に呼ぶ(ダイアログのモーダルループの中で
    /// 別の検知が届く状況を再現する)。</summary>
    public Action? OnYesNo { get; set; }

    public bool YesNo(string text, string caption, bool defaultNo)
    {
        Log.Add(("YesNo", text, caption));
        YesNoCalls.Add((caption, defaultNo));
        OnYesNo?.Invoke();
        return YesNoResult;
    }
```

**Step 4: ビルド**

Run: `dotnet build kxEdit.sln -c Release -warnaserror`
Expected: `0 個の警告 / 0 エラー`。

**Step 5: Commit**

```
feat(app): IUserPrompt に YesNo を足す(M-18 準備)
```

---

## Task 2: 観測値の捕捉(`LastKnownWriteTimeUtc`)

**Files:**
- Modify: `src/kxEdit.App/DocumentState.cs`(`CsvCol` の直後)
- Modify: `src/kxEdit.App/FileController.cs`(`NoteIfBackupStale` / `LoadInto` / `WriteToPath` / `RestoreFromBackup` / `RestoreDirtyFromBackup`)
- Modify: `tests/kxEdit.App.Tests/Fakes/FakeFileTimestampProvider.cs`
- Create: `tests/kxEdit.App.Tests/FileControllerExternalChangeTests.cs`

**Step 1: Fake に問い合わせフックを足す**

`FakeFileTimestampProvider.cs` を次に置き換える:

```csharp
namespace kxEdit.App.Tests.Fakes;

public sealed class FakeFileTimestampProvider : IFileTimestampProvider
{
    public Dictionary<string, DateTime> Times { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Queries { get; } = new();

    /// <summary>M-18: 問い合わせの瞬間に呼ぶ(「読む前に取る」「書いた後に取る」の順序を、
    /// この中でファイルを書き換える / 読むことで観測する)。</summary>
    public Action<string>? OnQuery { get; set; }

    public DateTime? GetLastWriteTimeUtc(string path)
    {
        Queries.Add(path);
        OnQuery?.Invoke(path);
        return Times.TryGetValue(path, out var t) ? t : null;
    }
}
```

**Step 2: 失敗するテストを書く**

`tests/kxEdit.App.Tests/FileControllerExternalChangeTests.cs` を作る。Host は `FileControllerTests` の
private Host の縮約(seam の配線は同じ)。

```csharp
using kxEdit.App.Tests.Fakes;
using kxEdit.Core.Backup;
using kxEdit.Core.Settings;
using File2 = System.IO.File;

namespace kxEdit.App.Tests;

/// <summary>
/// M-18(設計 2026-09-03): 外部変更の観測値の捕捉・検知・読み直し・保存直前の確認。
/// 実 DocumentManager+実 EditorControl+実ファイル I/O を使い、Form/OS 境界
/// (FakePrompt / FakeFileTimestampProvider)だけを偽物にする(FileControllerTests と同じ思想)。
/// ミューテーション検証は行わない(CLAUDE.md §4-A: ファイル I/O は禁止対象)。
/// </summary>
public class FileControllerExternalChangeTests
{
    private sealed class Host : IDisposable
    {
        public Form Form { get; }
        public DocumentManager Docs { get; }
        public FileController File { get; }
        public AppSettings Settings = new();
        public FakePrompt Prompt { get; } = new();
        public FakeFileDialogService Dialogs { get; } = new();
        public FakeReachabilityProbe Probe { get; } = new();
        public FakeFileTimestampProvider Timestamps { get; } = new();
        public List<Document> OpenedFresh { get; } = new();

        public Host()
        {
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
            File = new FileController(
                docs: Docs,
                owner: Form,
                settings: () => Settings,
                saveSettings: () => { },
                recentChanged: () => { },
                metaChanged: () => { },
                openedFresh: d => OpenedFresh.Add(d),
                prompt: Prompt,
                fileDialogs: Dialogs,
                reachabilityProbe: Probe,
                fileTimestamps: Timestamps
            );
        }

        public void Dispose() => Form.Dispose();
    }

    private static readonly DateTime T0 = new(2026, 09, 03, 10, 00, 00, DateTimeKind.Utc);
    private static readonly DateTime T1 = T0.AddMinutes(1);
    private static readonly DateTime T2 = T0.AddMinutes(2);

    /// <summary>ファイルを作り、観測値 <paramref name="stamp"/> で開く。</summary>
    private static (Document Doc, string Path) Open(
        Host host,
        TempDir tmp,
        string name,
        string content,
        DateTime? stamp
    )
    {
        string path = tmp.File(name);
        File2.WriteAllText(path, content);
        if (stamp is DateTime s)
            host.Timestamps.Times[path] = s;
        var doc = host.File.TryOpenOrActivate(path);
        Assert.NotNull(doc);
        return (doc!, path);
    }

    /// <summary>外部プロセスの書換を模す: 本文と、プロバイダが返す更新時刻の両方を変える。</summary>
    private static void ExternalWrite(Host host, string path, string content, DateTime stamp)
    {
        File2.WriteAllText(path, content);
        host.Timestamps.Times[path] = stamp;
    }

    // ===== Task 2: 観測値の捕捉 =====

    /// <summary>開くときは本文を読む<b>前</b>に取る(設計 §3.2)。問い合わせの瞬間に外部が書くと、
    /// 本文は書換後・観測値は書換前になる。読んだ後に取る実装では本文が "before" のまま。</summary>
    [Fact]
    public void Open_CapturesTimestampBeforeReading() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "before");
            host.Timestamps.Times[path] = T0;
            host.Timestamps.OnQuery = p => File2.WriteAllText(p, "after");

            var doc = host.File.TryOpenOrActivate(path)!;

            Assert.Equal("after", doc.Editor.Text);
            Assert.Equal(T0, doc.State.LastKnownWriteTimeUtc);
        });

    /// <summary>保存は書いた<b>後</b>に取る(設計 §3.2)。問い合わせの瞬間にディスクを読むと
    /// 新しい本文が見える。書く前に取る実装では "old" が見える。</summary>
    [Fact]
    public void Save_CapturesTimestampAfterWriting() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "old", T0);
            doc.Editor.ReplaceCharRange(0, 3, "new");
            host.Timestamps.Times[path] = T1; // 保存後のディスクが返す値
            string? seenAtQuery = null;
            host.Timestamps.OnQuery = p => seenAtQuery = File2.ReadAllText(p);

            Assert.True(host.File.SaveDocument(doc));

            Assert.Equal("new", seenAtQuery);
            Assert.Equal(T1, doc.State.LastKnownWriteTimeUtc);
        });

    /// <summary>バックアップ復元は A-1 の陳腐化判定が取った値を流用する(設計 §3.2)。</summary>
    [Fact]
    public void RestoreFromBackup_CapturesDiskTimestamp() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            string path = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kxEdit-m18-restore.txt")
            );
            host.Timestamps.Times[path] = T1;

            var doc = host.File.RestoreFromBackup(
                new BackupRecord(
                    Id: Guid.NewGuid().ToString("N"),
                    OriginalPath: path,
                    UntitledNumber: 0,
                    CodePage: 65001,
                    HasBom: false,
                    LineEndingId: 0,
                    Content: "backup content",
                    TimestampUtc: T0
                )
            );

            Assert.Equal(T1, doc.State.LastKnownWriteTimeUtc);
            Assert.Single(host.Timestamps.Queries); // A-1 の 1 回だけ。追加 I/O を作らない
        });
}
```

**Step 3: 落ちることを確かめる**

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~FileControllerExternalChangeTests"`
Expected: **ビルドエラー**(`LastKnownWriteTimeUtc` が無い)。

**Step 4: `DocumentState` にプロパティを足す**

`DocumentState.cs` の `public int CsvCol { get; set; }` の直後:

```csharp

    /// <summary>
    /// M-18(設計 2026-09-03 §3.1): 本文がディスクと一致していた(と kxEdit が信じている)時点の
    /// ディスク側 LastWriteTimeUtc。無題・取得失敗(到達不能・権限)は null = 判定しない。
    /// 開くときは本文を読む<b>前</b>に、保存は書いた<b>後</b>に取る(§3.2)。
    /// 比較は完全一致(同じ FS が同じファイルに返す値同士なので許容差を置かない。§3.3)。
    /// </summary>
    public DateTime? LastKnownWriteTimeUtc { get; set; }
```

**Step 5: `NoteIfBackupStale` が値を返すようにする**

`FileController.cs` の `NoteIfBackupStale` を置き換える(xmldoc はそのまま。remarks の末尾に 1 文足す):

```csharp
    /// ... 既存 summary / remarks ...
    /// <para>M-18(設計 2026-09-03 §3.2): 取ったディスク側の値をそのまま返す。復元タブの
    /// <see cref="DocumentState.LastKnownWriteTimeUtc"/> に流用し、追加の I/O を作らない。</para>
    private DateTime? NoteIfBackupStale(string validatedPath, BackupRecord bk)
    {
        DateTime? disk = _fileTimestamps.GetLastWriteTimeUtc(validatedPath);
        if (BackupStaleness.IsDiskNewer(disk, bk.TimestampUtc, BackupStaleness.DefaultTolerance))
            _staleRestoredPaths.Add(validatedPath);
        return disk;
    }
```

`RestoreFromBackup`: `string? safePath = null;` の次の行に `DateTime? stamp = null;` を足し、
`NoteIfBackupStale(normalized, rec);` を `stamp = NoteIfBackupStale(normalized, rec);` に変える。
`doc.State.Path = safePath;` の直後に `doc.State.LastKnownWriteTimeUtc = stamp;` を足す。

`RestoreDirtyFromBackup`: `NoteIfBackupStale(normalized, bk);` を
`doc.State.LastKnownWriteTimeUtc = NoteIfBackupStale(normalized, bk);` に変える。

**Step 6: `LoadInto` — 読む前に取る**

`if (!TryProbeFileExists(path)) return false;` の直後(`var loaded = TextFileService.LoadAsBufferAuto(...)` の前):

```csharp

            // M-18(設計 2026-09-03 §3.2): 更新時刻は本文を読む**前**に取る。読んでいる最中に外部が
            // 書き換えた場合、観測値は本文より古くなり次回の検知で拾える。読んだ後に取ると、
            // その 1 回の変更を永久に見落とす(観測値が本文より新しくなる)。
            // TryProbeFileExists の後なので、リモートでもプロバイダ内の 2 度目のプローブは ms で返る。
            DateTime? stamp = _fileTimestamps.GetLastWriteTimeUtc(path);
```

`doc.State.LineEnding = loaded.LineEnding;` の直後:

```csharp
            doc.State.LastKnownWriteTimeUtc = stamp;
```

**Step 7: `WriteToPath` — 書いた後に取る**

`TextFileService.Save(path, doc.Editor.CurrentBuffer, doc.State.Encoding, doc.State.HasBom);` の直後、
`doc.Editor.SetSavePoint();` の前:

```csharp
            // M-18(設計 2026-09-03 §3.2): 自分の保存で mtime が変わるので、書いた**後**の値を
            // 一致の基準にする。書込と取得の間に外部が書く窓は残る(設計 §9)。
            doc.State.LastKnownWriteTimeUtc = _fileTimestamps.GetLastWriteTimeUtc(path);
```

**Step 8: 通ることを確かめる**

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~FileControllerExternalChangeTests"`
Expected: 合格 3 / 合計 3。

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~FileControllerTests"`
Expected: 全緑(A-1 の `Restore*Stale*` 群が `NoteIfBackupStale` の戻り値化で壊れていないこと。
特に `RestoreFromBackup_RejectedPath_DoesNotQueryTimestamp` の `Queries` 空が保たれること)。

**Step 9: Commit**

```
feat(app): ディスクの更新時刻を開く前・保存後・復元時に観測する(M-18 準備)
```

---

## Task 3: `CheckExternalChange` —— **前倒し脆弱性レビュー対象**

**Files:**
- Create: `src/kxEdit.App/ExternalChangeOutcome.cs`
- Modify: `src/kxEdit.App/FileController.cs`(`// ==================== 確認 / 復元 ====================` の直前に節を足す)
- Modify: `tests/kxEdit.App.Tests/FileControllerExternalChangeTests.cs`

**Step 1: 失敗するテストを書く**

テストクラスの末尾(`RestoreFromBackup_CapturesDiskTimestamp` の後)に追加:

```csharp
    // ===== Task 3: CheckExternalChange =====

    [Fact]
    public void Check_Untitled_Skipped_NoPrompt() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "x";

            Assert.Equal(ExternalChangeOutcome.Skipped, host.File.CheckExternalChange(doc));
            Assert.Empty(host.Prompt.YesNoCalls);
        });

    /// <summary>観測値が無い(開いた時に取れなかった)なら判定しない。ディスク側に値が有っても聞かない。</summary>
    [Fact]
    public void Check_NoKnownStamp_Skipped() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", stamp: null);
            host.Timestamps.Times[path] = T1;

            Assert.Equal(ExternalChangeOutcome.Skipped, host.File.CheckExternalChange(doc));
            Assert.Empty(host.Prompt.YesNoCalls);
        });

    /// <summary>ディスク側が取れない(削除・到達不能)なら判定しない(設計 §3.3 / §9)。</summary>
    [Fact]
    public void Check_DiskUnavailable_Skipped() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", T0);
            host.Timestamps.Times.Remove(path);

            Assert.Equal(ExternalChangeOutcome.Skipped, host.File.CheckExternalChange(doc));
            Assert.Empty(host.Prompt.YesNoCalls);
        });

    [Fact]
    public void Check_SameStamp_NoChange_NoPrompt() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", T0);
            File2.WriteAllText(path, "v2"); // 本文だけ変えても mtime(観測値)が同じなら変更とみなさない

            Assert.Equal(ExternalChangeOutcome.NoChange, host.File.CheckExternalChange(doc));
            Assert.Empty(host.Prompt.YesNoCalls);
            Assert.Equal("v1", doc.Editor.Text);
        });

    /// <summary>未保存なし・はい: 読み直し、観測値更新、キャレット位置は非既定位置から保たれる。
    /// 文言に「失われます」を含まず、既定は「はい」(defaultNo=false)。</summary>
    [Fact]
    public void Check_Changed_Clean_Yes_ReloadsAndKeepsCaret() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1 text", T0);
            doc.Editor.SetCaretCharOffset(3); // 非既定位置(CLAUDE.md §4-B)
            ExternalWrite(host, path, "v2 longer text", T1);
            host.Prompt.YesNoResult = true;

            Assert.Equal(ExternalChangeOutcome.Reloaded, host.File.CheckExternalChange(doc));

            Assert.Equal("v2 longer text", doc.Editor.Text);
            Assert.False(doc.Editor.Modified);
            Assert.Equal(T1, doc.State.LastKnownWriteTimeUtc);
            Assert.Equal(3, doc.Editor.CaretCharOffset);
            var call = Assert.Single(host.Prompt.YesNoCalls);
            Assert.Equal(("ファイルの変更", false), call);
            var text = Assert.Single(host.Prompt.Log, e => e.Kind == "YesNo").Text;
            Assert.Equal("'a.txt' は kxEdit の外で変更されました。読み直しますか?", text);
            Assert.Contains(doc, host.OpenedFresh); // 開き直しと同じく .csv 自動モードの対象
        });

    /// <summary>新しい本文が短ければキャレットは末尾へクランプされる。</summary>
    [Fact]
    public void Check_Changed_Clean_Yes_ClampsCaret() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "0123456789", T0);
            doc.Editor.SetCaretCharOffset(8);
            ExternalWrite(host, path, "ab", T1);
            host.Prompt.YesNoResult = true;

            Assert.Equal(ExternalChangeOutcome.Reloaded, host.File.CheckExternalChange(doc));

            Assert.Equal(2, doc.Editor.CaretCharOffset);
        });

    /// <summary>未保存あり・いいえ: 文言が損失を伝え、既定は「いいえ」。本文も Modified も不変。
    /// 観測値はディスクの値になり、<b>2 回目は聞かない</b>。</summary>
    [Fact]
    public void Check_Changed_Dirty_No_KeepsAndAcknowledges() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", T0);
            doc.Editor.ReplaceCharRange(0, 0, "mine ");
            Assert.True(doc.Editor.Modified);
            ExternalWrite(host, path, "theirs", T1);
            host.Prompt.YesNoResult = false;

            Assert.Equal(ExternalChangeOutcome.Kept, host.File.CheckExternalChange(doc));

            Assert.Equal("mine v1", doc.Editor.Text);
            Assert.True(doc.Editor.Modified);
            Assert.Equal(T1, doc.State.LastKnownWriteTimeUtc);
            var call = Assert.Single(host.Prompt.YesNoCalls);
            Assert.Equal(("ファイルの変更", true), call);
            var text = Assert.Single(host.Prompt.Log, e => e.Kind == "YesNo").Text;
            Assert.Equal(
                "'a.txt' は kxEdit の外で変更されました。読み直すと、このタブの未保存の変更は失われます。読み直しますか?",
                text
            );

            Assert.Equal(ExternalChangeOutcome.NoChange, host.File.CheckExternalChange(doc));
            Assert.Single(host.Prompt.YesNoCalls); // 2 回目は出ない
        });

    [Fact]
    public void Check_Changed_Dirty_Yes_DiscardsEdits() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", T0);
            doc.Editor.ReplaceCharRange(0, 0, "mine ");
            ExternalWrite(host, path, "theirs", T1);
            host.Prompt.YesNoResult = true;

            Assert.Equal(ExternalChangeOutcome.Reloaded, host.File.CheckExternalChange(doc));

            Assert.Equal("theirs", doc.Editor.Text);
            Assert.False(doc.Editor.Modified);
        });

    /// <summary>読み直しに失敗(ロック中)したら <see cref="ExternalChangeOutcome.ReloadFailed"/>。
    /// 観測値は更新しない(次の復帰でまた聞く)。本文は不変。エラーは LoadInto が出す。</summary>
    [Fact]
    public void Check_Changed_Yes_ReloadFails_KeepsStampAndText() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", T0);
            ExternalWrite(host, path, "theirs", T1);
            host.Prompt.YesNoResult = true;
            using var locker = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None
            );

            Assert.Equal(ExternalChangeOutcome.ReloadFailed, host.File.CheckExternalChange(doc));

            Assert.Equal("v1", doc.Editor.Text);
            Assert.Equal(T0, doc.State.LastKnownWriteTimeUtc);
            Assert.Contains(host.Prompt.Log, e => e.Kind == "Error");
        });

    /// <summary>確認ダイアログのモーダルループの中で届いた 2 本目は何もしない(設計 §3.4 再入ガード)。</summary>
    [Fact]
    public void Check_Reentrant_InnerCallSkipped() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", T0);
            ExternalWrite(host, path, "v2", T1);
            host.Prompt.YesNoResult = true;
            ExternalChangeOutcome? inner = null;
            host.Prompt.OnYesNo = () => inner = host.File.CheckExternalChange(doc);

            Assert.Equal(ExternalChangeOutcome.Reloaded, host.File.CheckExternalChange(doc));

            Assert.Equal(ExternalChangeOutcome.Skipped, inner);
            Assert.Single(host.Prompt.YesNoCalls);
        });

    /// <summary>文言に載るファイル名は無害化する(BiDi 制御文字はファイル名に使える)。</summary>
    [Fact]
    public void Check_PromptSanitizesDisplayName() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a\u202Eb.txt", "v1", T0);
            ExternalWrite(host, path, "v2", T1);
            host.Prompt.YesNoResult = false;

            _ = host.File.CheckExternalChange(doc);

            var text = Assert.Single(host.Prompt.Log, e => e.Kind == "YesNo").Text;
            Assert.DoesNotContain("\u202E", text);
        });
```

**Step 2: 落ちることを確かめる**

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~FileControllerExternalChangeTests"`
Expected: ビルドエラー(`ExternalChangeOutcome` / `CheckExternalChange` が無い)。

**Step 3: enum**

`src/kxEdit.App/ExternalChangeOutcome.cs`:

```csharp
namespace kxEdit.App;

/// <summary>
/// M-18(設計 2026-09-03 §3.4): <see cref="FileController.CheckExternalChange"/> の結果。
/// ゼロ値を <see cref="Skipped"/> に置く(初期化漏れが「読み直した」に転ばないように。
/// <c>PathNormalizeStatus.TimedOut</c> と同じ流儀)。
/// </summary>
public enum ExternalChangeOutcome
{
    /// <summary>判定しなかった(無題・観測値なし・ディスク側取得失敗・再入中)。</summary>
    Skipped,

    /// <summary>ディスクの更新時刻が観測値と一致。</summary>
    NoChange,

    /// <summary>変更あり → 読み直した。呼出側は発声と CSV モードの復帰を行う。</summary>
    Reloaded,

    /// <summary>変更あり → 読み直さなかった(観測値をディスクの値へ更新済み = 次の変更まで聞かない)。</summary>
    Kept,

    /// <summary>変更あり → 読み直そうとして失敗(<c>LoadInto</c> がエラーを出した)。観測値は不変。</summary>
    ReloadFailed,
}
```

設計書 §3.4 の 4 値に `ReloadFailed` を足している。`Reloaded` を返すと MainForm が「読み直しました」を
発声して虚偽になり(B5 の主題)、`Kept` を返すと「次まで聞かない」と読める。実施記録に逸脱として書く。

**Step 4: `FileController` に節を足す**

`// ==================== 確認 / 復元 ====================` の直前に挿入:

```csharp
    // ==================== 外部変更(M-18) ====================

    /// <summary>M-18 の再入ガード。確認ダイアログ(モーダル)の message loop の中で、タブ切替由来の
    /// BeginInvoke 済み検知が届いても 2 枚目を出さない(設計 2026-09-03 §3.4)。</summary>
    private bool _checkingExternalChange;

    /// <summary>
    /// ウィンドウ復帰・タブ切替時の外部変更検知(設計 2026-09-03 §3.4)。ディスクの更新時刻が
    /// 観測値(<see cref="DocumentState.LastKnownWriteTimeUtc"/>)と違えば読み直すか確認する。
    /// 比較は完全一致(§3.3)。「いいえ」なら観測値をディスクの値へ更新し、次の変更まで聞き直さない。
    /// 読み直しは <see cref="LoadInto"/>(現在の文字コードで固定)+キャレット位置の復元(§3.5)。
    /// 発声と CSV モードの復帰は呼出側(MainForm)が <see cref="ExternalChangeOutcome.Reloaded"/> を見て行う。
    /// 保存直前の確認は別経路(<see cref="SaveDocument"/>)。
    /// </summary>
    public ExternalChangeOutcome CheckExternalChange(Document doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_checkingExternalChange)
            return ExternalChangeOutcome.Skipped;
        if (
            doc.State.Path is not string path
            || doc.State.LastKnownWriteTimeUtc is not DateTime known
        )
            return ExternalChangeOutcome.Skipped;

        _checkingExternalChange = true;
        try
        {
            // 削除・到達不能・取得失敗は判定しない(設計 §3.3 / §9)。
            if (_fileTimestamps.GetLastWriteTimeUtc(path) is not DateTime disk)
                return ExternalChangeOutcome.Skipped;
            if (disk == known)
                return ExternalChangeOutcome.NoChange;

            // 名前を先頭・問いを末尾に置く(SR は頭から読む。A-10 と同じ語順)。名前は外部由来なので無害化。
            string name = SanitizeForDisplay.OneLine(doc.State.DisplayName, 80);
            bool reload = doc.Editor.Modified
                ? _prompt.YesNo(
                    $"'{name}' は kxEdit の外で変更されました。読み直すと、このタブの未保存の変更は失われます。読み直しますか?",
                    "ファイルの変更",
                    defaultNo: true // 本文を失う側の確認 = 既定は安全側
                )
                : _prompt.YesNo(
                    $"'{name}' は kxEdit の外で変更されました。読み直しますか?",
                    "ファイルの変更",
                    defaultNo: false // 失うのは Undo 履歴とキャレット位置だけ
                );
            if (!reload)
            {
                doc.State.LastKnownWriteTimeUtc = disk; // 次の変更まで聞き直さない
                return ExternalChangeOutcome.Kept;
            }
            return ReloadFromDisk(doc, path)
                ? ExternalChangeOutcome.Reloaded
                : ExternalChangeOutcome.ReloadFailed;
        }
        finally
        {
            _checkingExternalChange = false;
        }
    }

    /// <summary>設計 §3.5。キャレットの文字位置を先に取り、読み直した後にクランプして戻す。
    /// 失敗時は <see cref="LoadInto"/> がエラーを出し、State は変わらない(読込前に throw する)。</summary>
    private bool ReloadFromDisk(Document doc, string path)
    {
        int caret = doc.Editor.CaretCharOffset;
        // 自動判定に戻さない: ユーザーが「開き直す」で直した文字コードを勝手に覆さないため。
        // 外で文字コードが変わっていれば LoadInto の U+FFFD 警告が出る(既存)。
        if (!LoadInto(doc, path, forcedCodePage: doc.State.Encoding.CodePage))
            return false;
        doc.Editor.SetCaretCharOffset(caret); // SnapAndClamp + BringCaretIntoView を内蔵
        _openedFresh(doc); // 開き直し(ReopenWithEncoding)と同じ: 設定次第で .csv の自動モード
        return true;
    }
```

**Step 5: 通ることを確かめる**

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~FileControllerExternalChangeTests"`
Expected: 合格 14 / 合計 14。

落ちたら実物を疑う順: (1) `LoadInto` の `forcedCodePage` で BOM 付き UTF-8 がどう扱われるか
(`LoadAsBufferAuto` の実装を読む)/ (2) `SetCaretCharOffset` の前提(`_buffer` 非 null)/
(3) ロックテストで `LoadAsBufferAuto` が投げる例外型が `LoadInto` の catch フィルタに入っているか。

**Step 6: Commit**

```
feat(app): 外部変更を検知して読み直しを確認する CheckExternalChange(M-18)
```

**Step 7: 前倒し脆弱性レビュー(CLAUDE.md §3-4)**

別エージェントに次を渡してレビューさせる: 設計書 §8、本 Task の差分、
`FileController.LoadInto` / `SanitizeForDisplay.OneLine`。観点: (a) 文言の外部由来文字列の無害化と長さ、
(b) 読み直しが既存の `LoadInto` の防御(サイズ上限・プローブ・例外フィルタ)を迂回していないか、
(c) 再入ガードの取りこぼし、(d) 観測値の更新タイミングで「攻撃者が mtime を戻して検知を抑止できる」以上の
能力が生まれていないか。指摘は ① fixup / ② 受容 / ③ 却下 を明示し、fixup は別 commit で積む。

---

## Task 4: 保存直前の上書き確認

**Files:**
- Modify: `src/kxEdit.App/FileController.cs`(`SaveDocument`: 重複タブガードの直後・A-10 コメント塊の直前)
- Modify: `tests/kxEdit.App.Tests/FileControllerExternalChangeTests.cs`

**Step 1: 失敗するテストを書く**

テストクラスの末尾に追加:

```csharp
    // ===== Task 4: 保存直前の上書き確認 =====

    [Fact]
    public void Save_DiskChanged_Cancel_DoesNotWrite() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", T0);
            doc.Editor.ReplaceCharRange(0, 0, "mine "); // Text セッターは SetOrReplaceSource で Modified が立たない
            ExternalWrite(host, path, "theirs", T1);
            host.Prompt.OkCancelResult = false;

            Assert.False(host.File.SaveDocument(doc));

            Assert.Equal("theirs", File2.ReadAllText(path));
            Assert.True(doc.Editor.Modified);
            Assert.Equal(T0, doc.State.LastKnownWriteTimeUtc); // 確認で止めたので観測値は動かさない
            var call = Assert.Single(host.Prompt.OkCancelCalls);
            Assert.Equal(("上書きの確認", true), call);
            var text = Assert.Single(host.Prompt.Log, e => e.Kind == "OkCancel").Text;
            Assert.Equal(
                "'a.txt' は kxEdit で開いた後に外で変更されています。上書きすると、その変更は失われます。上書きしますか?",
                text
            );
        });

    [Fact]
    public void Save_DiskChanged_Ok_WritesAndRefreshesStamp() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", T0);
            doc.Editor.ReplaceCharRange(0, 0, "mine ");
            ExternalWrite(host, path, "theirs", T1);
            host.Prompt.OkCancelResult = true;

            Assert.True(host.File.SaveDocument(doc));

            Assert.Equal("mine v1", File2.ReadAllText(path));
            Assert.False(doc.Editor.Modified);
            Assert.Equal(T1, doc.State.LastKnownWriteTimeUtc); // 保存後の再取得(Fake は T1 を返し続ける)
            Assert.Single(host.Prompt.OkCancelCalls);
        });

    [Fact]
    public void Save_DiskUnchanged_NoPrompt() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", T0);
            doc.Editor.ReplaceCharRange(0, 0, "mine ");

            Assert.True(host.File.SaveDocument(doc));

            Assert.Empty(host.Prompt.OkCancelCalls);
            Assert.Equal("mine v1", File2.ReadAllText(path));
        });

    /// <summary>観測値が無ければ判定しない(ディスク側に値が有っても)。</summary>
    [Fact]
    public void Save_NoKnownStamp_NoPrompt() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", stamp: null);
            doc.Editor.ReplaceCharRange(0, 0, "mine ");
            host.Timestamps.Times[path] = T1;

            Assert.True(host.File.SaveDocument(doc));

            Assert.Empty(host.Prompt.OkCancelCalls);
        });

    /// <summary>タブを閉じる確認の「はい」→ 保存 → 上書き確認でキャンセル → 閉じない(false)。</summary>
    [Fact]
    public void ConfirmDiscardIfDirty_Yes_ThenOverwriteCancelled_ReturnsFalse() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", T0);
            doc.Editor.ReplaceCharRange(0, 0, "mine ");
            ExternalWrite(host, path, "theirs", T1);
            host.Prompt.YesNoCancelResult = DialogResult.Yes;
            host.Prompt.OkCancelResult = false;

            Assert.False(host.File.ConfirmDiscardIfDirty(doc));

            Assert.Equal("theirs", File2.ReadAllText(path));
        });
```

**Step 2: 落ちることを確かめる**

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~FileControllerExternalChangeTests"`
Expected: `Save_DiskChanged_Cancel_DoesNotWrite` と `ConfirmDiscardIfDirty_Yes_ThenOverwriteCancelled_ReturnsFalse` が
赤(確認が無いので保存が通る)。`Save_DiskChanged_Ok_*` は緑でよい(Ok が既定)。

**Step 3: `SaveDocument` にガードを足す**

重複タブガードの `return false; }` の直後、`// A-10: 上書き保存経路にも符号化劣化の事前確認を置く` の直前に挿入:

```csharp
        // M-18(設計 2026-09-03 §3.4): 保存直前の外部変更検知 = 無言上書きの最終防衛線。
        // 位置は重複タブガードの後(重複は保存させないので I/O を無駄打ちしない)、A-10 の
        // 符号化確認の前(「上書きするか」を先に決めてから劣化の確認に進む)。
        // 観測値かディスク側が null(無題・到達不能・削除)なら判定しない(§3.3)。
        // 完全一致で比べる(§3.3)。キャンセルでは観測値を動かさない: 次の復帰で読み直しの確認が
        // 出るのが正しい(ユーザーは「上書きしない」と決めただけで、ディスクの内容はまだ見ていない)。
        // リモートではここで 5 秒プローブが 1 本増える(WriteToPath の TryInspectSaveTarget と合わせて
        // 最悪 10 秒)。FileTimestampProvider の到達不能記憶(60 秒 TTL・Task 5)が 2 回目以降を省く。
        if (
            doc.State.LastKnownWriteTimeUtc is DateTime known
            && _fileTimestamps.GetLastWriteTimeUtc(doc.State.Path) is DateTime disk
            && disk != known
            && !_prompt.OkCancel(
                $"'{SanitizeForDisplay.OneLine(doc.State.DisplayName, 80)}' は kxEdit で開いた後に外で変更されています。"
                    + "上書きすると、その変更は失われます。上書きしますか?",
                "上書きの確認",
                defaultCancel: true
            )
        )
        {
            // 保存しない。ConfirmDiscardIfDirty の「はい」経路でも false が伝播してタブを閉じない。
            return false;
        }

```

**Step 4: 通ることを確かめる**

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~FileControllerExternalChangeTests"`
Expected: 合格 19 / 合計 19。

Run: `dotnet test tests/kxEdit.App.Tests -c Release`
Expected: 全緑。特に `FileControllerTests` の Ctrl+S 系(`SaveDocument_*` / `Save_*`)で `Timestamps.Queries` や
`Probe.*CallCount` を数えているテストが、追加した 1 回の問い合わせで赤になっていないか読む。
赤なら**テストの前提を確かめてから**数を直す(観測値が null の既存テストでは問い合わせ自体が増えない ——
`is DateTime known` が先に短絡するため。増えるのは開いたファイルを Ctrl+S するテストだけ)。

**Step 5: Commit**

```
feat(app): 保存直前に外部変更を検知して上書きを確認する(M-18)
```

---

## Task 5: `FileTimestampProvider` の到達不能記憶を TTL 化

**Files:**
- Modify: `src/kxEdit.App/FileTimestampProvider.cs`
- Modify: `tests/kxEdit.App.Tests/FileTimestampProviderTests.cs`

**Step 1: 失敗するテストを書く**

`FileTimestampProviderTests` の末尾に追加(`using kxEdit.App.Tests.Fakes;` が無ければ足す):

```csharp
    // ===== M-18(設計 2026-09-03 §3.8): 到達不能の記憶は 60 秒で切れる =====

    /// <summary>プロセス寿命のままだと、一度落ちた共有の文書は再起動まで検知が黙って止まる。
    /// TTL 無しだと Alt+Tab のたびに 5 秒止まる。60 秒で「最悪 1 分に 1 回 5 秒」。</summary>
    [Fact]
    public void UnreachableRoot_IsProbedAgainAfterTtl()
    {
        var probe = new FakeReachabilityProbe { Result = false };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero));
        var sut = new FileTimestampProvider(probe, clock);
        const string path = @"\\unreachable-host\share\a.txt";

        Assert.Null(sut.GetLastWriteTimeUtc(path));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(sut.GetLastWriteTimeUtc(path));
        Assert.Equal(1, probe.CallCount); // TTL 内は記憶が効く(既存テストの意味は保たれる)

        clock.Advance(TimeSpan.FromSeconds(2)); // 計 61 秒
        Assert.Null(sut.GetLastWriteTimeUtc(path));
        Assert.Equal(2, probe.CallCount); // 期限切れ → 再プローブ
    }

    /// <summary>TTL は ctor で差し替えられる(既定 60 秒が唯一の値ではないことの配線確認)。</summary>
    [Fact]
    public void UnreachableTtl_IsInjectable()
    {
        var probe = new FakeReachabilityProbe { Result = false };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero));
        var sut = new FileTimestampProvider(probe, clock, unreachableTtl: TimeSpan.FromSeconds(5));
        const string path = @"\\unreachable-host\share\a.txt";

        Assert.Null(sut.GetLastWriteTimeUtc(path));
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Null(sut.GetLastWriteTimeUtc(path));

        Assert.Equal(2, probe.CallCount);
    }

    /// <summary>期限切れ後に到達できれば値の取得へ進む(記憶が到達可能を塞がない)。</summary>
    [Fact]
    public void UnreachableRoot_AfterTtl_ReachableAgain_ProceedsToRead()
    {
        var probe = new FakeReachabilityProbe { Result = false };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero));
        var sut = new FileTimestampProvider(probe, clock);
        const string path = @"\\unreachable-host\share\a.txt";

        Assert.Null(sut.GetLastWriteTimeUtc(path));
        clock.Advance(TimeSpan.FromSeconds(61));
        probe.Result = true;

        // 到達できても実ファイルは無いので null。プローブが走ったこと(=読みに進んだこと)だけを見る。
        Assert.Null(sut.GetLastWriteTimeUtc(path));
        Assert.Equal(2, probe.CallCount);
    }
```

**Step 2: 落ちることを確かめる**

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~FileTimestampProviderTests"`
Expected: ビルドエラー(ctor の引数が無い)。

**Step 3: 実装**

`FileTimestampProvider.cs` のフィールド・ctor・`GetLastWriteTimeUtc` 冒頭を置き換える
(xmldoc の `_unreachableRoots` の説明は TTL の話に書き換える。「プロセス寿命で保持して構わない」の 1 文は
**削る**——M-18 で呼び出し元が増え、前提が偽になった):

```csharp
    /// <summary>HIGH-6 / CSV-M-1 と同じ 5 秒契約(FileController.TryProbeFileExists・
    /// FileMetaProvider と対称)。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>到達不能ルートを憶えておく長さ(M-18・設計 2026-09-03 §3.8)。
    /// A-1 の起動時復元だけならプロセス寿命でよかったが、M-18 がウィンドウ復帰のたびに呼ぶため
    /// (1) 永久に憶えると一度落ちた共有の文書は再起動まで検知が黙って止まり、
    /// (2) 憶えないと Alt+Tab のたびに 5 秒止まる。60 秒で「最悪 1 分に 1 回 5 秒」。</summary>
    public static readonly TimeSpan DefaultUnreachableTtl = TimeSpan.FromSeconds(60);

    private readonly IReachabilityProbe _probe;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _unreachableTtl;

    /// <summary>到達不能と判明したリモートルート(<c>\\server\share</c> / <c>Z:\</c>)→ 記憶の期限。
    /// 起動時復元は同じ共有上の文書を何件も含みうるため、これを憶えないと
    /// 「5 秒 × レコード数」が積み上がる(レビュー H-1 の増幅点)。
    /// 記録の効果は「その根の判定をあきらめる = null を返す」だけで、安全側にしか倒れない。</summary>
    private readonly Dictionary<string, DateTimeOffset> _unreachableUntil = new(
        StringComparer.OrdinalIgnoreCase
    );

    public FileTimestampProvider(
        IReachabilityProbe? probe = null,
        TimeProvider? clock = null,
        TimeSpan? unreachableTtl = null
    )
    {
        _probe = probe ?? new FileReachabilityProbe();
        _clock = clock ?? TimeProvider.System;
        _unreachableTtl = unreachableTtl ?? DefaultUnreachableTtl;
    }

    public DateTime? GetLastWriteTimeUtc(string path)
    {
        try
        {
            if (RemotePathDetector.IsRemote(path))
            {
                string root = RootKey(path);
                DateTimeOffset now = _clock.GetUtcNow();
                if (_unreachableUntil.TryGetValue(root, out var until) && now < until)
                    return null;
                if (!_probe.ProbeFileExistsWithTimeout(path, ProbeTimeout))
                {
                    _unreachableUntil[root] = now + _unreachableTtl;
                    return null;
                }
                // 期限切れの記録は消さなくてよい: 期限内は上で return し、期限後は必ずここへ来て
                // 上書きされるので、残っていても判定に影響しない。
            }

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
```

`MainForm.cs:302` の `new FileTimestampProvider()` は既定値のままでよい(変更しない)。

**Step 4: 通ることを確かめる**

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~FileTimestampProviderTests"`
Expected: 合格 10 / 合計 10(既存 7 + 新規 3)。

**Step 5: Commit**

```
fix(app): FileTimestampProvider の到達不能記憶を 60 秒 TTL にする(M-18)
```

---

## Task 6: MainForm 配線 + L5 チェックリスト

**Files:**
- Modify: `src/kxEdit.App/MainForm.cs`(internal ctor / `_file = new FileController(...)` の `prompt:` /
  `ActiveDocumentChanged` 購読 / `OnActivated` / 新メソッドと seam)
- Create: `tests/kxEdit.App.Tests/MainFormExternalChangeTests.cs`
- Create: `docs/plans/2026-09-03-external-change-detection-l5-checklist.md`

**Step 1: 失敗するテストを書く**

`tests/kxEdit.App.Tests/MainFormExternalChangeTests.cs`:

```csharp
using kxEdit.App.Tests.Fakes;
using kxEdit.Core.Settings;
using File2 = System.IO.File;

namespace kxEdit.App.Tests;

/// <summary>
/// M-18(設計 2026-09-03 §3.7): MainForm 側の配線 —— 読み直した後の発声と CSV モードの復帰。
/// 判定と確認は <see cref="FileControllerExternalChangeTests"/> が固定する。
/// <c>OnActivated</c> / <c>ActiveDocumentChanged</c> からの起動は実際のウィンドウ活性化が要り
/// L3 では再現できないため、L5 チェックリスト項目 1 / 4 が担う。ここは seam 経由で本体だけを叩く。
/// 更新時刻は本物の <c>FileTimestampProvider</c> なので、外部変更は実ファイルの mtime を明示的に進めて表す。
/// </summary>
public class MainFormExternalChangeTests
{
    private static MainForm ShowMainForm(AppSettings settings, TempDir tmp, FakePrompt prompt)
    {
        var form = new MainForm(
            settings,
            System.IO.Path.Combine(tmp.Root, "settings.json"),
            backupDirectory: System.IO.Path.Combine(tmp.Root, "backups"),
            sessionLayoutPath: System.IO.Path.Combine(tmp.Root, "session-state.json"),
            prompt: prompt
        );
        form.SetLastSessionBuffersPathForTest(System.IO.Path.Combine(tmp.Root, "last-session-buffers.json"));
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Show();
        return form;
    }

    private static AppSettings NewSettings() =>
        new() { BackupEnabled = false, CsvAutoModeOnOpen = false };

    /// <summary>実ファイルを外部から書き換える。mtime は同一ティック内で同じ値になりうるので明示的に進める。</summary>
    private static void ExternalWrite(string path, string content)
    {
        File2.WriteAllText(path, content);
        File2.SetLastWriteTimeUtc(path, File2.GetLastWriteTimeUtc(path).AddMinutes(1));
    }

    [Fact]
    public void Reloaded_AnnouncesAndRefreshesText() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var prompt = new FakePrompt { YesNoResult = true };
            using var form = ShowMainForm(NewSettings(), tmp, prompt);
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "v1");
            var doc = form.FileForTest.TryOpenOrActivate(path)!;
            ExternalWrite(path, "v2");

            Assert.Equal(ExternalChangeOutcome.Reloaded, form.CheckExternalChangeOnActiveForTest());

            Assert.Equal("v2", doc.Editor.Text);
            Assert.Equal("読み直しました", form.LastAnnouncementForTest);
        });

    [Fact]
    public void Kept_DoesNotAnnounce() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var prompt = new FakePrompt { YesNoResult = false };
            using var form = ShowMainForm(NewSettings(), tmp, prompt);
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "v1");
            var doc = form.FileForTest.TryOpenOrActivate(path)!;
            ExternalWrite(path, "v2");
            string before = form.LastAnnouncementForTest;

            Assert.Equal(ExternalChangeOutcome.Kept, form.CheckExternalChangeOnActiveForTest());

            Assert.Equal("v1", doc.Editor.Text);
            Assert.Equal(before, form.LastAnnouncementForTest);
        });

    /// <summary>手動で入った CSV モード(自動モード OFF)は読み直し後も保たれる。
    /// LoadInto が CsvMode を false に落とすので、MainForm が TryEnterMode で戻す(設計 §3.7)。</summary>
    [Fact]
    public void Reloaded_ManualCsvMode_ReentersCsvMode() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var prompt = new FakePrompt { YesNoResult = true };
            using var form = ShowMainForm(NewSettings(), tmp, prompt);
            string path = tmp.File("t.csv");
            File2.WriteAllText(path, "a,b\r\nc,d\r\n");
            var doc = form.FileForTest.TryOpenOrActivate(path)!;
            Assert.True(form.CsvForTest.TryEnterMode(doc));
            Assert.True(doc.State.CsvMode);
            ExternalWrite(path, "a,b\r\nc,d\r\ne,f\r\n");

            Assert.Equal(ExternalChangeOutcome.Reloaded, form.CheckExternalChangeOnActiveForTest());

            Assert.True(doc.State.CsvMode);
            Assert.Equal("a,b\r\nc,d\r\ne,f\r\n", doc.Editor.Text);
        });

    [Fact]
    public void NoActiveDocument_Skipped() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(NewSettings(), tmp, new FakePrompt());
            // 起動直後の無題タブは Path=null → FileController 側が Skipped を返す
            Assert.Equal(ExternalChangeOutcome.Skipped, form.CheckExternalChangeOnActiveForTest());
        });
}
```

**Step 2: 落ちることを確かめる**

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~MainFormExternalChangeTests"`
Expected: ビルドエラー(ctor の `prompt` / `CheckExternalChangeOnActiveForTest` / `CsvForTest` が無い)。

**Step 3: internal ctor に `prompt` を足す**

`internal MainForm(` の引数リスト末尾 `bool quarantineSettingsBeforeFirstSave = false` の後に
`, IUserPrompt? prompt = null` を足す。xmldoc の `<para>` に 1 文足す:
「`prompt` = 確認ダイアログの seam(テスト用。null = `MessageBoxUserPrompt`)。M-18 の MainForm 側配線を
モーダル無しで検証するため」。`Program.CreateMainForm` は位置指定 6 引数なので影響しない。

`_file = new FileController(` の `prompt: new MessageBoxUserPrompt(),` を
`prompt: prompt ?? new MessageBoxUserPrompt(),` に変える。

**Step 4: 本体メソッドと seam**

`FileForTest` の直後に追加:

```csharp

    /// <summary>テスト専用: CSV モードの手動切替(M-18 の読み直し後のモード復帰を検証する)。</summary>
    internal CsvController CsvForTest => _csv;

    /// <summary>
    /// M-18(設計 2026-09-03 §3.4 / §3.7): ウィンドウ復帰・タブ切替時の外部変更検知。
    /// 判定と確認は <see cref="FileController.CheckExternalChange"/>。ここは配線と、読み直した後の
    /// 発声・CSV モードの復帰(<c>LoadInto</c> が CsvMode を false に落とすため)だけを担う。
    /// F2 編集中は起こらない: ウィンドウを離れた時点で OnLostFocus → CancelEdit、タブ切替は
    /// BeforeActiveChange が AbortEdit する。
    /// 発声を TryEnterMode より先に出すのは、パース不能で入れなかったときの通知(TryEnterMode が出す)を
    /// 最後に残すため(1 行の発声チャネルは最後の 1 件が残る。B5 の教訓)。
    /// </summary>
    private ExternalChangeOutcome CheckExternalChangeOnActive()
    {
        var doc = _docs.Active;
        if (doc is null)
            return ExternalChangeOutcome.Skipped;
        bool wasCsv = doc.State.CsvMode;
        var outcome = _file.CheckExternalChange(doc);
        if (outcome != ExternalChangeOutcome.Reloaded)
            return outcome;
        _announcer.Say("読み直しました");
        if (wasCsv && !doc.State.CsvMode)
            _csv.TryEnterMode(doc); // 自動モードで既に入っていれば来ない。パース不能なら入らず TryEnterMode が発声する
        return outcome;
    }

    /// <summary>テスト専用: <see cref="CheckExternalChangeOnActive"/> を活性化イベント無しで叩く。</summary>
    internal ExternalChangeOutcome CheckExternalChangeOnActiveForTest() => CheckExternalChangeOnActive();
```

**Step 5: `OnActivated`**

既存の `BeginInvoke` 本体を次に置き換える(フォーカス復帰の条件は不変。早期 return を畳んだだけ):

```csharp
        BeginInvoke(() =>
        {
            if (IsDisposed || ActiveForm != this || _menuActive)
                return;
            if (!_docs.TabHost.ContainsFocus)
                _docs.Active?.FocusTarget.Focus();
            // M-18(設計 2026-09-03 §3.4): フォーカスを戻した後に外部変更を見る。確認ダイアログが閉じると
            // OnActivated が再び来るが、Reloaded / Kept のどちらでも観測値は更新済みなので NoChange で終わる。
            CheckExternalChangeOnActive();
        });
```

既存コメント(「他ウィンドウから戻ったとき…」)はそのまま残す。

**Step 6: `ActiveDocumentChanged`**

`_docs.ActiveDocumentChanged += (_, _) => { UpdateTitle(); UpdateStatus(); };` を置き換える:

```csharp
        _docs.ActiveDocumentChanged += (_, _) =>
        {
            UpdateTitle();
            UpdateStatus();
            // M-18(設計 2026-09-03 §3.4): TabControl の選択変更ハンドラの中でモーダルを出さない
            // (WinForms の再入)。BeginInvoke 先で「まだそのタブがアクティブ」「フォームがアクティブ」を
            // 再確認する。ctor 中(ハンドル未生成)の発火は BeginInvoke できないので見送る
            // (起動直後の無題タブは Path=null で判定対象にならない)。
            var doc = _docs.Active;
            if (doc is null || !IsHandleCreated)
                return;
            BeginInvoke(() =>
            {
                if (IsDisposed || ActiveForm != this || !ReferenceEquals(_docs.Active, doc))
                    return;
                CheckExternalChangeOnActive();
            });
        };
```

**Step 7: 通ることを確かめる**

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~MainFormExternalChangeTests"`
Expected: 合格 4 / 合計 4。

Run: `dotnet test tests/kxEdit.App.Tests -c Release`
Expected: 全緑。`MainFormSmokeTests` は `Form.ActiveForm` が null のため新しい `BeginInvoke` 先が
早期 return する = 既存挙動不変のはず。赤が出たら「どのテストが・何で」を読んでから直す。

**Step 8: L5 チェックリスト**

`docs/plans/2026-09-03-external-change-detection-l5-checklist.md` を起こす。設計書 §7 の 7 項目を、
既存の `2026-09-02-save-last-line-of-defense-l5-checklist.md` の形式(前提・手順・期待発声・
「修正前ならどう見えるか」)で書く。各項目に**修正前でも PASS してしまわないか**を検算した 1 行を付ける
(PR #62 Critical-1 の教訓)。

| # | 項目 | 修正前との弁別 |
|---|------|----------------|
| 1 | メモ帳で書換 → kxEdit へ戻る → NVDA が「'a.txt' は kxEdit の外で変更されました。読み直しますか?」を逐語で読む → Enter → 本文が新しくなり「読み直しました」 | 修正前はダイアログが出ない |
| 2 | 未保存ありの文言。Enter で**読み直されない**(既定いいえ)。いいえ後に Alt+Tab 往復で聞かれない | 既定ボタンは L5 でしか確かめられない |
| 3 | 保存直前: 「上書きしますか?」→ Enter で**上書きされない**(既定キャンセル)。メモ帳側の内容が無傷 | 修正前は無言で上書き |
| 4 | 2 タブ開き、非アクティブ側をメモ帳で書換 → Ctrl+Tab → 確認が出る | 修正前は出ない |
| 5 | CSV モードのタブを読み直す → モードのまま・セル位置が近い位置。通常タブはキャレット行が保たれる | 修正前は比較対象なし(退行確認) |
| 6 | OneDrive 配下で保存 → Alt+Tab 往復 → **聞かれない**(同期クライアントが mtime を触らないことの観測) | 誤検知の有無を見る |
| 7 | 到達不能な共有の文書を開いたまま Alt+Tab → 1 回目は最大 5 秒、直後の往復は待たない(TTL) | 修正前は復帰時に I/O しない |

**Step 9: Commit**

```
feat(app): 復帰・タブ切替時に外部変更を検知し、読み直し後に発声する(M-18)
docs(plans): M-18 の L5 チェックリスト
```

(2 commit に分ける。)

---

## Task 7: 実施記録・最終レビュー・品質ゲート・PR

1. 設計書に `## 11. 実施記録(2026-09-03)` を追記する: 結果(テスト件数はプロジェクトの実行結果を書き、
   文書の数字を引き回さない)/ 計画からの逸脱(`ReloadFailed` の追加・その他)/ 前倒し脆弱性レビューの
   結果 / 「網が張れない」と判断したもの(OnActivated / ActiveDocumentChanged の実配線 = L5 送り)/
   L5 未実施の明記。
2. 最終ブランチレビュー 2 パス(**別エージェントを別々に起動**): コード品質パス / 脆弱性パス。
   指摘は ① fixup / ② PR description に記載 / ③ 理由付き却下。fixup は別 commit。
3. `powershell -File tools\pre-merge-check.ps1` → **EXIT 0** を確認(Debug 構成の再実行まで含む)。
4. push → PR 作成(日本語。目的・レビュー経緯・申し送り・**L5 未実施 7 項目**を明記)。
5. セッションメモリーを更新(`v02-release-readiness-assessment` に M-18 対応の記録と、L5 台帳への追加)。

## 完了条件

- Task 1〜6 の commit がすべて積まれ、`tests/kxEdit.App.Tests` 全緑・`-warnaserror` で 0 警告。
- 設計書 §4 の 4 文言がテストで逐語固定されている。
- L5 チェックリストが存在し、PR description が L5 未実施を明記している。
- `tools/pre-merge-check.ps1` EXIT 0。
