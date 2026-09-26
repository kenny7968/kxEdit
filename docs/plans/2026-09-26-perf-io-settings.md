# フェーズ 7: I/O と設定(perf-io-settings) 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ファイル I/O と設定保存の無駄な往復・同期書込を減らす(P-11・P-22・P-12・P-15・P-21(a))。

**Architecture:**
- **P-11**: `IReachabilityProbe` に更新時刻取得用の `ProbeTimestampWithTimeout` を足す。work は `FileInfo` を 1 回だけ作り、(Reachable, Exists, LastWriteUtc, Error)を返す。`FileTimestampProvider` のリモート経路は、この結果だけで答える(UI スレッドでの再 I/O をなくす)。ローカル経路も `FileInfo` 1 回にする。
- **P-22**: `FileController.RegisterRecent` で、`RecentFilesList.Add` の前後を `SequenceEqual(StringComparer.Ordinal)` で比べ、等しければ保存とメニュー再構築を省く。
- **P-12**: `BackupStore` の `JsonSerializerOptions` に `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping` を足す。
- **P-15**: `PreviewUserDataSweeper` は、先に `preview-*` を列挙し、1 件以上あるときだけプロセスを列挙する。
- **P-21(a)**: `PreviewUserDataFolder.Dispose` の再帰削除を背景タスクに移し、短い間隔で数回リトライする。

**Tech Stack:** C#(.NET 9)、xUnit、WinForms、System.Text.Json、PowerShell 7。

**Spec:** `docs/plans/2026-09-24-general-perf-improvements-design.md` §3・§12(以下「設計書」)
**調査記録:** `docs/plans/2026-09-24-general-perf-audit.md` P-11・P-12・P-15・P-21・P-22(以下「調査記録」)
**前フェーズの計画:** `docs/plans/2026-09-25-perf-tab-switch.md`(実施記録は設計書 §11.6 に同梱済み。本フェーズで転記するものはない)

## Global Constraints

- 挙動不変が原則。意図的な挙動差は設計書 §3.5 のフェーズ 7 の 4 行だけ(下の 0.5)。
- 0 warning(`-warnaserror`)。pre-commit フック(CSharpier・ローカルパス検出)を `--no-verify` で飛ばさない。
- テストにローカルの絶対パスを書かない(一時フォルダーから導出する)。
- ユーザーの `%APPDATA%\kxEdit` を読み書きしない。テストの `%LOCALAPPDATA%\kxEdit\WebView2` は、自分で作った `preview-<guid>` だけを触る(既存テストと同じ)。
- コメント・コミットメッセージ本文は日本語。
- ミューテーション検証は行わない(設計書 §3.4: ファイル I/O は禁止対象)。陰性対照(実装を戻してテストが落ちることの確認)は行う。

## Review Focus

- **リモートの文書の名前に NUL などの不正な文字が入っている** — 更新時刻プローブの (Reachable, Exists) が、保存先プローブの (Reachable, FileExists) と一致すること(=到達不能の記憶の判定が変わらない)。Task 2 の等価性の Theory で押さえる。
- **リモートで、存在は確認できたが更新時刻の取得だけが失敗する** — null を返し、その共有を到達不能として記憶しないこと。Task 2 の `RemoteTimestampError_…` で押さえる。
- **最近使ったファイルの先頭と、大文字小文字だけが違うパスを開く** — 一覧の表記が置き換わるので保存されること(`PathKey` で比べると取りこぼす)。Task 3 で押さえる。
- **本文に単独サロゲートが入っている** — バックアップは従来と同じく U+FFFD に置き換わる(旧 Encoder でも同じ。2026-09-26 に実測)。Encoder の変更で例外や別の文字にならないこと。Task 4 で押さえる。
- **WebView2 のプロセスがプロファイルを掴んだまま、プレビューを閉じる** — `Dispose` は待たずに戻り、ロックが外れた後のリトライで消えること。消せないまま終わっても例外を投げないこと。Task 6 で押さえる。

---

## 0. 前提と決定事項

### 0.1 計測(設計書 §3.2・§12.6)

- 設計書 §12.6 の「リモート(SMB 共有)が用意できれば」について: この開発機では **管理共有 `\\localhost\C$` にループバックで届く**(2026-09-26 に `Test-Path` で確認)。`RemotePathDetector.IsRemote` は先頭 `\\` で true になるので、リモートの経路をそのまま通せる。
  - ただし往復の遅延はループバック(サブミリ秒)で、VPN 越し(調査記録の見積もりは 1 往復 50 ms)を再現しない。そこで **往復の回数はテスト(Fake の呼び出し回数)で確認し**、ループバックの実測は補助にする(設計書 §12.6 の「用意できなければテストで代える」を併用する)。
  - harness M-6 のリモート版は作らない(ループバックでは差が揺れに埋もれるため。逸脱として PR に書く)。
- 計測は scratchpad の使い捨てベンチ `io-bench`(リポジトリに入れない)で、変更前後の Release ビルドに対して各 3 回走らせる(Task 1・Task 7)。項目:
  - `ts-local` / `ts-unc-loopback`: `FileTimestampProvider.GetLastWriteTimeUtc` の 1 回あたりの時間(P-11)
  - `backup-bytes` / `backup-write`: 日本語 1 万行の `BackupRecord` の JSON の大きさと書込時間(P-12)
  - `settings-save`: `SettingsStore.Save` 1 回の時間 = P-22 で省ける費用
  - `is-sole-instance`: プロセス列挙 1 回の時間 = P-15 で省ける費用
  - `preview-dispose`: 中身入りの `PreviewUserDataFolder` の `Dispose` が UI スレッドを止める時間(P-21(a))
- NVDA は起動したままでよい(ユーザー判断 2026-09-25。本フェーズは UI の計測をしない)。他のエージェントの重い処理と並走させない。

### 0.2 設計書からの精密化・逸脱

- **P-11 の結果の型** は `TimestampProbeResult(bool Reachable, bool Exists, DateTime? LastWriteUtc, bool Error)`(readonly record struct)。`default` が「到達不能」(= タイムアウト時の値)になるよう、フィールドの並びと意味を選ぶ(兄弟の `SaveTargetProbeResult` と同じ原則)。
- **P-11 の work の Exists は `File.Exists` の意味論に揃える**: `FileInfo` の生成(不正な文字で `ArgumentException`)や `Exists` の例外は、`File.Exists` と同じく「存在しない」として扱い、親フォルダーの確認へ進む。設計書の「Exists 系の例外は到達不能として記憶」は、`File.Exists` 自身は例外を投げない(内部で握って false を返す)ので、**従来の保存先プローブと同じ結果になる形**として精密化する。外側の try(`GetDirectoryName`・`Directory.Exists` の予期しない例外)は従来どおり到達不能に倒す。
  - 根拠: 名前に NUL を含むパスは、従来 `File.Exists`=false・親あり → (Reachable, 不在) = 記憶しない。`FileInfo` の例外を到達不能に倒すと、共有全体を 60 秒記憶する挙動差になる。
- **P-11 の work は `FileReachabilityProbe.ReadTimestamp` に切り出す**(タイムアウトを決定的にテストできるよう、骨格 `RunTimestampProbe` と分ける。既存の `Run*Probe` と同じ構成)。
- **更新時刻の取得は、`FileInfo.Exists` が読んだ属性のキャッシュから返る**(`FileSystemInfo` は `Exists` の時点で `WIN32_FILE_ATTRIBUTE_DATA` を取り込む)。リモートで存在するファイルの往復は、従来の 4 回(プローブの `File.Exists`・`Directory.Exists`、UI スレッドの `File.Exists`・`GetLastWriteTimeUtc`)から 1 回になる。`Error` の分岐は実際にはほぼ通らないが、設計書どおり残す。
- **P-21(a) のリトライ間隔は 100・200・400・800 ms**(4 回。合計 1.5 秒)。テスト用に、親フォルダーとリトライ間隔を受け取る internal コンストラクターを足す。公開コンストラクターの親フォルダーは `PreviewUserDataSweeper.DefaultRoot` を使う(同じ値を 2 か所で組み立てていたのを 1 か所にする。値の一致は既存テスト `DefaultRoot_PointsAtPreviewParentUnderLocalAppData` が固定している)。
- **P-15 は判定部を `SweepIfAnyAndSole(string root, Func<bool> isSoleInstance)` に切り出す**。IL テストの目印 `SweepIfSoleInstance`(引数なし)と `Program.cs` の呼び出しは変えない。オーバーロードではなく別名にするのは、IL テストの名前照合に紛れを作らないため。

### 0.3 レビューとミューテーション検証

- **前倒しの脆弱性レビュー**(設計書 §3.4: パス操作・ファイル削除)
  - Task 2(P-11: リモートパスへの I/O と到達不能の記憶)
  - Task 4(P-12: Encoder。設計書 §12.3「脆弱性レビューで確認する」)
  - Task 5(P-15)と Task 6(P-21(a): 背景での再帰削除・リパースポイント)
- 前倒しのコード品質レビュー: 該当なし(新しい型 `TimestampProbeResult` は既存の `SaveTargetProbeResult` と同じ型の並びで、後続タスクが依存する seam ではない)。
- **ミューテーション検証: 行わない**(Global Constraints)。各タスクの最後に陰性対照を 1 つ行う。

### 0.4 L5

- **不要**(設計書 §4・§12.6)。SR の経路(`kxEdit.Accessibility`・UIA・Speech)に触れない。

### 0.5 意図的な挙動差(設計書 §3.5 のフェーズ 7 の行。PR に載せる)

- バックアップ JSON の非 ASCII がエスケープされなくなる(読込は新旧互換)。
- 最近使ったファイルの一覧が変わらない場合は、settings.json を保存しない。
- リモートのファイルの更新時刻の取得も期限付きになる。タイムアウト時は「到達不能として記憶」になる。
- プレビューの `Dispose` から戻った時点で、作業フォルダーがまだ残っていることがある。

---

## Task 0: 本計画を commit する(docs のみ)

- [ ] **Step 1: commit**

```powershell
git add docs/plans/2026-09-26-perf-io-settings.md
git commit -m "docs(perf): フェーズ 7(I/O と設定)の実装計画"
```

---

## Task 1: 変更前の計測(scratchpad のみ・commit しない)

**Files:**
- Create: `<scratchpad>\io-bench\io-bench.csproj`
- Create: `<scratchpad>\io-bench\Program.cs`

`<scratchpad>` はセッションの scratchpad ディレクトリ。以下 `$S` と書く。

- [ ] **Step 1: ベンチのプロジェクトを作る**

`$S\io-bench\io-bench.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <!-- KxBin = kxEdit.App を -o で出力したフォルダー。マネージドの 4 本を参照して出力へコピーする
         (ワイルドカードにするとネイティブ DLL まで参照に入り、ビルドが失敗しうる) -->
    <Reference Include="$(KxBin)\kxEdit.App.dll" />
    <Reference Include="$(KxBin)\kxEdit.Core.dll" />
    <Reference Include="$(KxBin)\kxEdit.Editor.dll" />
    <Reference Include="$(KxBin)\kxEdit.Accessibility.dll" />
  </ItemGroup>
</Project>
```

`$S\io-bench\Program.cs`:

```csharp
using System.Diagnostics;
using System.Reflection;
using System.Text;
using kxEdit.App;
using kxEdit.Core.Backup;
using kxEdit.Core.Settings;

string work = Path.Combine(Path.GetTempPath(), "kxedit-io-bench");
Directory.CreateDirectory(work);
string file = Path.Combine(work, "a.txt");
File.WriteAllText(file, "x");
// ループバックの管理共有経由の同じファイル(IsRemote は先頭 \\ で true)
string unc = @"\\localhost\" + file[0] + "$" + file.Substring(2);

var ts = new FileTimestampProvider();
if (ts.GetLastWriteTimeUtc(unc) is null)
    throw new InvalidOperationException("UNC で更新時刻が取れない: " + unc);
Measure("ts-local", 200, () => ts.GetLastWriteTimeUtc(file));
Measure("ts-unc-loopback", 200, () => ts.GetLastWriteTimeUtc(unc));

var sb = new StringBuilder();
for (int i = 0; i < 10000; i++)
    sb.Append("日本語のテキストの行です。番号 ").Append(i).Append("\r\n");
var rec = new BackupRecord(
    Guid.NewGuid().ToString("N"),
    Path.Combine(work, "日本語.txt"),
    0,
    65001,
    false,
    0,
    sb.ToString(),
    DateTime.UtcNow
);
string bdir = Path.Combine(work, "backups");
Measure("backup-write", 20, () => BackupStore.Write(bdir, rec));
Console.WriteLine($"backup-bytes {new FileInfo(Path.Combine(bdir, rec.Id + ".json")).Length}");

string spath = Path.Combine(work, "settings.json");
var settings = new AppSettings();
Measure("settings-save", 50, () => SettingsStore.Save(spath, settings));

Assembly app = typeof(FileTimestampProvider).Assembly;
var sweeper = app.GetType("kxEdit.App.PreviewUserDataSweeper")!;
var isSole = sweeper.GetMethod("IsSoleInstance", BindingFlags.NonPublic | BindingFlags.Static)!;
Measure("is-sole-instance", 50, () => isSole.Invoke(null, null));

// 中身入りのプレビュー作業フォルダー(WebView2 のプロファイル相当: 20 フォルダー × 20 ファイル × 16KB)。
// 自分で作った preview-<guid> だけを消す(利用者の他のフォルダーには触れない)。
var folderType = app.GetType("kxEdit.App.PreviewUserDataFolder")!;
var samples = new List<double>();
for (int run = 0; run < 8; run++)
{
    var folder = (IDisposable)Activator.CreateInstance(folderType, nonPublic: true)!;
    string root = (string)folderType.GetProperty("Path")!.GetValue(folder)!;
    var blob = new byte[16 * 1024];
    for (int d = 0; d < 20; d++)
    {
        string sub = Path.Combine(root, "Default", "Cache" + d);
        Directory.CreateDirectory(sub);
        for (int f = 0; f < 20; f++)
            File.WriteAllBytes(Path.Combine(sub, "f" + f), blob);
    }
    var sw = Stopwatch.StartNew();
    folder.Dispose();
    sw.Stop();
    // 変更後は背景で消えるので、次の回の前に完了を待つ(計測には含めない)
    if (folderType.GetProperty("DeletionTask", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(folder) is Task t)
        t.Wait(TimeSpan.FromSeconds(10));
    if (Directory.Exists(root))
        throw new InvalidOperationException("作業フォルダーが消えていない: " + root);
    if (run >= 3)
        samples.Add(sw.Elapsed.TotalMilliseconds); // 最初の 3 回は暖機
}
Report("preview-dispose", samples);

static void Measure(string name, int n, Action a)
{
    for (int i = 0; i < 3; i++)
        a(); // 暖機
    var s = new List<double>();
    for (int i = 0; i < n; i++)
    {
        var sw = Stopwatch.StartNew();
        a();
        s.Add(sw.Elapsed.TotalMilliseconds);
    }
    Report(name, s);
}

static void Report(string name, List<double> s)
{
    s.Sort();
    Console.WriteLine($"{name} median={s[s.Count / 2]:F3}ms min={s[0]:F3}ms max={s[^1]:F3}ms n={s.Count}");
}
```

- [ ] **Step 2: 変更前(main と同じ HEAD)の Release をビルドし、ベンチを 3 回走らせる**

```powershell
$S = '<scratchpad>'
dotnet build src/kxEdit.App -c Release -o "$S\bin-before"
dotnet build "$S\io-bench" -c Release -p:KxBin="$S\bin-before" -o "$S\bench-before"
1..3 | ForEach-Object { dotnet "$S\bench-before\io-bench.dll" } | Out-File -Encoding utf8 "$S\before.txt"
Get-Content "$S\before.txt"
```

Expected: 3 回ぶんの 7 行(`ts-local`・`ts-unc-loopback`・`backup-write`・`backup-bytes`・`settings-save`・`is-sole-instance`・`preview-dispose`)。例外で止まった場合は値を使わず、原因を直してから採り直す。

- [ ] **Step 3: 値を本計画の末尾の「実施記録」に書く**(Task 7 で変更後と並べる。commit は Task 7 で行う)

---

## Task 2: P-11 更新時刻のプローブを 1 回にする(前倒しの脆弱性レビュー)

**Files:**
- Modify: `src/kxEdit.App/Abstractions/IReachabilityProbe.cs`(型 `TimestampProbeResult` とメンバー `ProbeTimestampWithTimeout` を足す)
- Modify: `src/kxEdit.App/FileReachabilityProbe.cs`(`RunTimestampProbe`・`ReadTimestamp`・`ProbeTimestampWithTimeout`)
- Modify: `src/kxEdit.App/FileTimestampProvider.cs:77-125`(`GetCore`)
- Modify: `tests/kxEdit.App.Tests/Fakes/FakeReachabilityProbe.cs`
- Modify: `tests/kxEdit.App.Tests/FileTimestampProviderTests.cs`
- Modify: `tests/kxEdit.App.Tests/FileReachabilityProbeTests.cs`

**Interfaces:**
- Produces:
  - `public readonly record struct TimestampProbeResult(bool Reachable, bool Exists, DateTime? LastWriteUtc, bool Error);`
  - `IReachabilityProbe.ProbeTimestampWithTimeout(string path, TimeSpan timeout) : TimestampProbeResult`
  - `internal static TimestampProbeResult FileReachabilityProbe.RunTimestampProbe(Func<TimestampProbeResult> work, TimeSpan timeout)`
  - `internal static TimestampProbeResult FileReachabilityProbe.ReadTimestamp(string path)`
  - Fake: `TimestampResult`(既定 `(true, false, null, false)`)・`TimestampCallCount`・`TimestampLastTimeout`

- [ ] **Step 1: 型とインターフェースのメンバーを足す**(テストがコンパイルできるようにするための最小限。実装は Step 5)

`IReachabilityProbe.cs` の `SaveTargetProbeResult` の直後に:

```csharp
/// <summary>
/// 外部変更チェック用に、更新時刻を 1 回の境界付き I/O で調べた結果(性能改善フェーズ 7・P-11)。
/// <c>Reachable</c> と <c>Exists</c> の意味は <see cref="SaveTargetProbeResult"/> の
/// <c>Reachable</c> / <c>FileExists</c> と同じ(ファイルが在る、または親フォルダーが在る)。
/// <b><c>default</c> は「到達不能」</b>で、タイムアウト時の値と同じ(ゼロ値をフェイルセーフ側に置く)。
/// </summary>
/// <param name="Reachable">ファイルが在る、または親フォルダーが在る。false のとき他の値は無意味。</param>
/// <param name="Exists">ファイルが在る(<c>File.Exists</c> の意味論。フォルダーは false)。</param>
/// <param name="LastWriteUtc">更新時刻。<c>Exists</c> が true で、取得に成功したときだけ値を持つ。</param>
/// <param name="Error">
/// 存在は確認できたが、更新時刻の取得で例外が出た。呼出側は null を返し、到達不能として記憶しない
/// (従来の UI スレッド側の扱いと同じ)。
/// </param>
public readonly record struct TimestampProbeResult(
    bool Reachable,
    bool Exists,
    DateTime? LastWriteUtc,
    bool Error
);
```

`IReachabilityProbe` の `ProbeSaveTargetWithTimeout` の直後に:

```csharp
    /// <summary>
    /// 外部変更チェック用に、到達性・存在・更新時刻を 1 回の境界付き I/O で得る(性能改善フェーズ 7・P-11)。
    /// 従来は <see cref="ProbeSaveTargetWithTimeout"/> の後に、UI スレッドで境界なしの
    /// <c>File.Exists</c> + <c>GetLastWriteTimeUtc</c> を再び呼んでいた(リモートで往復 4 回)。
    /// <see cref="ProbeSaveTargetWithTimeout"/> は保存経路と共用なので戻り値の型を変えず、別メソッドにした。
    /// 呼出側は正規化済みの絶対パスを渡す(ファイル系プローブの契約)。
    /// </summary>
    TimestampProbeResult ProbeTimestampWithTimeout(string path, TimeSpan timeout);
```

`FileReachabilityProbe.cs` には、ビルドを通すための仮の実装を置かない。Step 5 で本実装を書く(Step 2〜4 のテストは、この時点ではコンパイルエラーで落ちる=RED)。

- [ ] **Step 2: Fake にメンバーを足す**

`FakeReachabilityProbe.cs` の `ProbeSaveTargetWithTimeout` の後に:

```csharp
    /// <summary>
    /// <c>ProbeTimestampWithTimeout</c> の応答。既定は「到達可能・不在」
    /// (<see cref="SaveTargetResult"/> の既定と同じ形。FileExists ゲートで止まり、実 I/O へ進まない)。
    /// </summary>
    public TimestampProbeResult TimestampResult { get; set; } =
        new(Reachable: true, Exists: false, LastWriteUtc: null, Error: false);

    public int TimestampCallCount { get; private set; }

    /// <summary>直近の <c>ProbeTimestampWithTimeout</c> 呼出で渡された timeout(5s 契約の pin)。</summary>
    public TimeSpan TimestampLastTimeout { get; private set; }

    public TimestampProbeResult ProbeTimestampWithTimeout(string path, TimeSpan timeout)
    {
        TimestampCallCount++;
        TimestampLastTimeout = timeout;
        return TimestampResult;
    }
```

クラスの doc の箇条書きに 1 項目足す:

```csharp
/// <item><c>ProbeTimestampWithTimeout</c> — 既定は「到達可能・不在」(P-11。外部変更チェックの更新時刻)。</item>
```

- [ ] **Step 3: `FileTimestampProviderTests` を新メソッドに移す**

リモートのテスト 11 本(`UncPath_ProbesWithFiveSecondTimeout` 〜 `ProbeLastWriteTimeUtc_Unreachable_RemembersRoot`)で、次のとおり機械的に置き換える(意味は変えない):

| 旧 | 新 |
|---|---|
| `SaveTargetResult = new(Reachable: false, FileExists: false)` | `TimestampResult = new(Reachable: false, Exists: false, LastWriteUtc: null, Error: false)` |
| `SaveTargetResult = new(Reachable: true, FileExists: false)` | `TimestampResult = new(Reachable: true, Exists: false, LastWriteUtc: null, Error: false)` |
| `probe.SaveTargetCallCount` | `probe.TimestampCallCount` |
| `probe.SaveTargetLastTimeout` | `probe.TimestampLastTimeout` |

`LocalPath_DoesNotProbe` は、既存の 2 行の後に 1 行足す:

```csharp
            Assert.Equal(0, probe.TimestampCallCount);
```

`UncPath_ProbesWithFiveSecondTimeout` は、末尾に 1 行足す(保存先プローブへ戻す退行を殺す):

```csharp
        Assert.Equal(0, probe.SaveTargetCallCount); // 更新時刻は専用のプローブ 1 回で取る(P-11)
```

- [ ] **Step 4: `FileTimestampProviderTests` に新しいテストを足す**(ファイル末尾、`ProbeLastWriteTimeUtc_Unreachable_RemembersRoot` の後)

```csharp
    // ===== 性能改善フェーズ 7(P-11): 更新時刻はプローブの結果だけで答える =====

    /// <summary>リモートで存在するファイルは、プローブが返した更新時刻をそのまま返す。
    /// UI スレッドで <c>File.Exists</c> + <c>GetLastWriteTimeUtc</c> を呼び直さない(従来はリモートで往復 4 回)。
    /// パスは即答する存在しない共有なので、実 I/O に進めば null になり、固定の時刻は返らない
    /// (=呼び直していないことの証人)。保存先プローブと読み取り側プローブも呼ばない。</summary>
    [Fact]
    public void RemoteExistingFile_ReturnsProbedTimestamp_WithoutUiThreadIo()
    {
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var probe = new FakeReachabilityProbe
        {
            TimestampResult = new(Reachable: true, Exists: true, LastWriteUtc: stamp, Error: false),
        };
        var sut = new FileTimestampProvider(probe);
        const string path = @"\\localhost\kxedit-no-such-share\a.txt";

        Assert.Equal(stamp, sut.GetLastWriteTimeUtc(path));
        Assert.Equal(stamp, sut.ProbeLastWriteTimeUtc(path));

        Assert.Equal(2, probe.TimestampCallCount);
        Assert.Equal(0, probe.SaveTargetCallCount);
        Assert.Equal(0, probe.CallCount);
    }

    /// <summary>存在は確認できたが更新時刻の取得に失敗した(Error)ときは null を返し、
    /// その共有を到達不能として記憶しない(従来の UI スレッド側の例外の扱いと同じ)。</summary>
    [Fact]
    public void RemoteTimestampError_ReturnsNull_WithoutRememberingRoot()
    {
        var probe = new FakeReachabilityProbe
        {
            TimestampResult = new(Reachable: true, Exists: true, LastWriteUtc: null, Error: true),
        };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var sut = new FileTimestampProvider(probe, clock);
        const string path = @"\\reachable-host\share\a.txt";

        Assert.Null(sut.GetLastWriteTimeUtc(path));
        Assert.Null(sut.GetLastWriteTimeUtc(path));

        Assert.Equal(2, probe.TimestampCallCount); // 2 回目も記憶に阻まれずプローブされる
    }

    /// <summary>ローカルで存在するファイルは、更新時刻を実際の値で返す(<c>FileInfo</c> 1 回に変えた後も、
    /// 既定値ではない固定の時刻がそのまま返ること)。</summary>
    [Fact]
    public void LocalExistingFile_ReturnsExactLastWriteTime()
    {
        var dir = Directory.CreateTempSubdirectory("kxEditTs_").FullName;
        try
        {
            var path = Path.Combine(dir, "a.txt");
            File.WriteAllText(path, "x");
            var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, stamp);

            Assert.Equal(stamp, new FileTimestampProvider().GetLastWriteTimeUtc(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>ローカルでフォルダーを渡したときは null(<c>File.Exists</c> の意味論: フォルダーは false)。
    /// <c>FileInfo</c> に変えても、フォルダーの更新時刻を返さないこと。</summary>
    [Fact]
    public void LocalDirectory_ReturnsNull()
    {
        var dir = Directory.CreateTempSubdirectory("kxEditTs_").FullName;
        try
        {
            Assert.Null(new FileTimestampProvider().GetLastWriteTimeUtc(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
```

- [ ] **Step 5: `FileReachabilityProbeTests` にテストを足す**(`RunSaveTargetProbe_WorkCompletes_ReturnsWorkResult` の後)

```csharp
    // ===== 更新時刻のプローブ(性能改善フェーズ 7・P-11)=====

    [Fact]
    public void ProbeTimestamp_ExistingFile_ReturnsExactLastWriteTime()
    {
        using var tmp = new TempDir();
        string path = tmp.File("a.txt");
        File2.WriteAllText(path, "x");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File2.SetLastWriteTimeUtc(path, stamp);

        var result = new FileReachabilityProbe().ProbeTimestampWithTimeout(path, Timeout);

        Assert.Equal(
            new TimestampProbeResult(Reachable: true, Exists: true, LastWriteUtc: stamp, Error: false),
            result
        );
    }

    [Fact]
    public void ProbeTimestamp_MissingFileInExistingDir_ReachableNotExists()
    {
        // 不在なら更新時刻を持たない(1601-01-01 を返さない)。
        using var tmp = new TempDir();

        var result = new FileReachabilityProbe().ProbeTimestampWithTimeout(
            tmp.File("missing.txt"),
            Timeout
        );

        Assert.Equal(
            new TimestampProbeResult(Reachable: true, Exists: false, LastWriteUtc: null, Error: false),
            result
        );
    }

    /// <summary>
    /// 等価性の網(設計書 §12.1「Reachable の定義」): 更新時刻のプローブの (Reachable, Exists) は、
    /// 従来 <c>FileTimestampProvider</c> が使っていた保存先プローブの (Reachable, FileExists) と一致する。
    /// ずれると到達不能の記憶の判定が変わる(共有全体を 60 秒黙らせる/黙らせない)。
    /// NUL 入りの名前は <c>FileInfo</c> の生成が例外を投げる入力で、これを到達不能に倒すと
    /// ここで落ちる(<c>File.Exists</c> は例外を投げずに false を返すので、従来は「親あり・不在」)。
    /// </summary>
    [Theory]
    [InlineData("existing")]
    [InlineData("missing")]
    [InlineData("missing-dir")]
    [InlineData("directory")]
    [InlineData("drive-root")]
    [InlineData("nul-in-name")]
    [InlineData("trailing-separator")]
    public void ProbeTimestamp_MatchesSaveTargetProbe_OnReachableAndExists(string kind)
    {
        using var tmp = new TempDir();
        string existing = tmp.File("a.txt");
        File2.WriteAllText(existing, "x");
        string path = kind switch
        {
            "existing" => existing,
            "missing" => tmp.File("missing.txt"),
            "missing-dir" => System.IO.Path.Combine(tmp.Root, "no-such-dir", "a.txt"),
            "directory" => tmp.Root,
            "drive-root" => System.IO.Path.GetPathRoot(tmp.Root)!,
            "nul-in-name" => tmp.File("a\0b.txt"),
            "trailing-separator" => tmp.Root + System.IO.Path.DirectorySeparatorChar,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var probe = new FileReachabilityProbe();

        var save = probe.ProbeSaveTargetWithTimeout(path, Timeout);
        var ts = probe.ProbeTimestampWithTimeout(path, Timeout);

        Assert.Equal(save.Reachable, ts.Reachable);
        Assert.Equal(save.FileExists, ts.Exists);
        Assert.False(ts.Error);
        Assert.Equal(ts.Exists, ts.LastWriteUtc.HasValue); // 在るときだけ時刻を持つ
    }

    [Fact]
    public void RunTimestampProbe_WorkExceedsTimeout_FailsSafeToUnreachable()
    {
        // フェイルセーフ値は「到達不能」。work は (true,true,時刻) を返すので、
        // (false,false,null,false) が返ったならフェイルセーフ由来と確定する(既存の Run*Probe と対称)。
        var gate = new TaskCompletionSource();
        try
        {
            var result = FileReachabilityProbe.RunTimestampProbe(
                () =>
                {
                    gate.Task.Wait();
                    return new TimestampProbeResult(true, true, DateTime.UtcNow, false);
                },
                TimeSpan.FromMilliseconds(50)
            );

            Assert.Equal(new TimestampProbeResult(false, false, null, false), result);
            Assert.Equal(default, result); // ゼロ値がフェイルセーフ側にある(型の doc の契約)
        }
        finally
        {
            gate.SetResult(); // 退避スレッドを解放する(テスト後に leak させない)
        }
    }

    [Fact]
    public void RunTimestampProbe_WorkCompletes_ReturnsWorkResult()
    {
        // 対照群。常にフェイルセーフ値を返す実装を kill する。
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var result = FileReachabilityProbe.RunTimestampProbe(
            () => new TimestampProbeResult(true, true, stamp, false),
            Timeout
        );

        Assert.Equal(new TimestampProbeResult(true, true, stamp, false), result);
    }
```

- [ ] **Step 6: テストが落ちることを確かめる**

```powershell
dotnet build tests/kxEdit.App.Tests -c Debug
```

Expected: FAIL(`FileReachabilityProbe` が `IReachabilityProbe.ProbeTimestampWithTimeout` を実装していない・`RunTimestampProbe` がない、のコンパイルエラー)。

- [ ] **Step 7: `FileReachabilityProbe` を実装する**

`RunNormalizeProbe` の後に:

```csharp
    /// <summary>
    /// 更新時刻プローブの骨格(性能改善フェーズ 7・P-11)。<paramref name="work"/> をバックグラウンドへ退避し、
    /// 期限内に終わらなければ「到達不能」へ倒す。フェイルセーフ値をここに置く理由は
    /// <see cref="RunSaveTargetProbe"/> と同じ(work を差し替えてタイムアウト経路を決定的にテストするため)。
    /// </summary>
    internal static TimestampProbeResult RunTimestampProbe(
        Func<TimestampProbeResult> work,
        TimeSpan timeout
    ) =>
        WaitBounded(
            Task.Run(work),
            timeout,
            new TimestampProbeResult(Reachable: false, Exists: false, LastWriteUtc: null, Error: false)
        );

    /// <summary>
    /// 更新時刻プローブの work(P-11)。<see cref="FileInfo"/> を 1 回だけ作り、
    /// <see cref="FileSystemInfo.Exists"/> が取り込んだ属性から更新時刻も読む(往復 1 回)。
    /// (Reachable, Exists) は <see cref="ProbeSaveTargetWithTimeout"/> と同じ結果になるよう組む
    /// (<c>FileReachabilityProbeTests.ProbeTimestamp_MatchesSaveTargetProbe_OnReachableAndExists</c>):
    /// <list type="bullet">
    /// <item>存在の確認は <c>File.Exists</c> の意味論に揃える。<c>File.Exists</c> は例外を投げずに false を返すので、
    /// <see cref="FileInfo"/> の生成(名前に NUL があると <see cref="ArgumentException"/>)や
    /// <c>Exists</c> の例外も「不在」として親フォルダーの確認へ進む。到達不能に倒すと、
    /// 共有全体を到達不能として記憶する挙動差になる。</item>
    /// <item>親フォルダーの確認と、それ以外の予期しない例外は、従来の保存先プローブと同じく到達不能へ倒す。</item>
    /// <item>更新時刻の取得の例外は <c>Error</c> で返す(呼出側は null にし、記憶しない)。</item>
    /// </list>
    /// </summary>
    internal static TimestampProbeResult ReadTimestamp(string path)
    {
        try
        {
            FileInfo? info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists)
                    info = null;
            }
            catch
            {
                info = null; // File.Exists と同じく「不在」
            }

            if (info is null)
            {
                string? dir = Path.GetDirectoryName(path);
                // dir が null / 空は親が無い(ProbeSaveTargetWithTimeout と同じ扱い)。
                bool dirExists = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
                return new TimestampProbeResult(dirExists, false, null, false);
            }

            try
            {
                return new TimestampProbeResult(true, true, info.LastWriteTimeUtc, false);
            }
            catch
            {
                return new TimestampProbeResult(true, true, null, true);
            }
        }
        catch
        {
            // Directory.Exists 等は通常投げないが、UNC 未到達などで稀に出る例外を吸って
            // 「到達不能」に倒す(ProbeSaveTargetWithTimeout と同方針)。
            return new TimestampProbeResult(false, false, null, false);
        }
    }

    /// <inheritdoc />
    public TimestampProbeResult ProbeTimestampWithTimeout(string path, TimeSpan timeout) =>
        RunTimestampProbe(() => ReadTimestamp(path), timeout);
```

クラスの doc(冒頭の summary)の「それぞれ」の列挙に、更新時刻版を 1 行足す:

```csharp
/// 更新時刻版の <see cref="ProbeTimestampWithTimeout"/>(性能改善フェーズ 7・P-11)は
/// <see cref="System.IO.FileInfo"/> 1 回(不在なら親フォルダーの <see cref="System.IO.Directory.Exists"/>)を、
```

(「4 本とも」「プローブ 3 本は」の数も 5 本・4 本に直す。)

- [ ] **Step 8: `FileTimestampProvider.GetCore` を書き換える**

`:77-125` の `GetCore` を次に置き換える:

```csharp
    private DateTime? GetCore(string path, bool useMemo)
    {
        try
        {
            if (!RemotePathDetector.IsRemote(path))
            {
                // 性能改善フェーズ 7(P-11): FileInfo 1 回(Exists が取り込んだ属性から更新時刻も読む)。
                // 不在時の LastWriteTimeUtc は 1601-01-01 を返す(例外を投げない)。
                // そのまま返すと「非常に古いディスク」に見えて判定が黙って歪むため、Exists で弾く。
                // Exists は File.Exists と同じくフォルダーに false を返す。
                var info = new FileInfo(path);
                return info.Exists ? info.LastWriteTimeUtc : null;
            }

            string root = RootKey(path);
            DateTimeOffset now = _clock.GetUtcNow();
            if (useMemo && _unreachableUntil.TryGetValue(root, out var until) && now < until)
                return null;
            // 脆弱性レビュー L-1(2026-09-03): 「到達不能」と「到達できるが不在」を区別する。
            // 到達可能な共有上の一時的な不在(別ツールの delete→recreate・rename 保存の途中)で
            // ルート全体を到達不能として記憶すると、以後その共有の全文書で検知が黙って止まる。
            // Reachable は「ファイルが在る、または親フォルダーが在る」(FileReachabilityProbe の定義)なので、
            // 記憶しないのは「ファイルは無いが親フォルダーは在る」場合だけ。
            // 親フォルダーごと消えた/改名された場合は到達不能として TTL の間記憶される残余がある。
            // 性能改善フェーズ 7(P-11): 更新時刻も同じ境界付きプローブの中で取る。従来は保存先プローブの後に、
            // UI スレッドで境界なしの File.Exists + GetLastWriteTimeUtc を再び呼んでいた(リモートで往復 4 回・無期限)。
            // 更新時刻の取得も期限の内側に入ったので、タイムアウト時は到達不能として記憶する(設計書 §3.5)。
            var probe = _probe.ProbeTimestampWithTimeout(path, ProbeTimeout);
            if (!probe.Reachable)
            {
                _unreachableUntil[root] = now + _unreachableTtl;
                return null;
            }
            // 復旧を確認した根の記憶をここで捨てる。期限切れの記録は到達不能なら上の分岐で上書きされるが、
            // 復旧したときは上書きされないので明示的に消す。これで**この根については**壁時計が逆行しても
            // 期限切れの記録が復活しない。期限切れ後に一度も再照会されていない根は until を持ったまま残るので、
            // 一般命題としては閉じていない(設計 2026-09-03 §11.5「壁時計の逆行」)。
            _unreachableUntil.Remove(root);
            // 不在は記憶しない(次の問い合わせで再び見る)。更新時刻の取得に失敗した(Error)ときも
            // LastWriteUtc は null で、記憶しない(従来の UI スレッド側の例外の扱いと同じ)。
            return probe.Exists ? probe.LastWriteUtc : null;
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

クラスの remarks の「<see cref="File.Exists"/> が(プローブ無しでは)復元経路で最初の同期 I/O になる」は経緯の記述なので残す。

- [ ] **Step 9: テストが通ることを確かめる**

```powershell
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~FileTimestampProviderTests|FullyQualifiedName~FileReachabilityProbeTests|FullyQualifiedName~FileMetaProviderTests|FullyQualifiedName~FileControllerExternalChangeTests|FullyQualifiedName~MainFormExternalChangeTests" -c Debug
```

Expected: PASS(全件)。

- [ ] **Step 10: commit**

```powershell
git add src/kxEdit.App/Abstractions/IReachabilityProbe.cs src/kxEdit.App/FileReachabilityProbe.cs src/kxEdit.App/FileTimestampProvider.cs tests/kxEdit.App.Tests/Fakes/FakeReachabilityProbe.cs tests/kxEdit.App.Tests/FileTimestampProviderTests.cs tests/kxEdit.App.Tests/FileReachabilityProbeTests.cs
git commit -m "perf(app): 外部変更チェックの更新時刻を境界付きプローブ 1 回で取る(P-11)"
```

commit 後の状態で `dotnet build kxEdit.sln -c Debug` と Step 9 のテストを再実行する(pre-commit の CSharpier が整形した結果で壊れていないことの確認)。

- [ ] **Step 11: 陰性対照**(commit しない。Step 10 の commit の後に行う)

`ReadTimestamp` の内側の `catch { info = null; }` を `catch { return new TimestampProbeResult(false, false, null, false); }` に替えてビルドし(`-p:TreatWarningsAsErrors=false`)、`ProbeTimestamp_MatchesSaveTargetProbe_OnReachableAndExists(kind: "nul-in-name")` が落ちることを確かめる。確認後に `git checkout -- src/kxEdit.App/FileReachabilityProbe.cs` で commit 済みの内容へ戻す。

- [ ] **Step 12: 前倒しの脆弱性レビュー**(別エージェント)

観点: 不正な文字・長大パス・フォルダー・ルート・リパースポイントで、(Reachable, Exists) が従来と一致するか。タイムアウトと例外で UI スレッドが無期限に止まる経路が残っていないか。到達不能の記憶が誤って書かれる/書かれない入力がないか。スレッドの leak の見積もり(クラスの doc)が変わらないか。

---

## Task 3: P-22 最近使ったファイルが変わらなければ保存しない

**Files:**
- Modify: `src/kxEdit.App/FileController.cs:1708-1715`(`RegisterRecent`)
- Test: `tests/kxEdit.App.Tests/FileControllerTests.cs`

**Interfaces:**
- Consumes: なし(既存の `Host.SaveSettingsCount` / `RecentChangedCount` と `TryOpenOrActivate` を使う)
- Produces: なし

- [ ] **Step 1: テストを書く**(`TryOpenOrActivate_AlreadyOpen_ActivatesExistingTab_WithoutReload` の前に置く)

```csharp
    // ===== 性能改善フェーズ 7(P-22): 最近使ったファイルが変わらなければ settings.json を保存しない =====

    /// <summary>
    /// 先頭が既に同じパスなら、一覧は変わらないので保存もメニュー再構築もしない。
    /// 既定(空の一覧)と区別するため、2 件入った非既定の一覧から始める(CLAUDE.md §4-B)。
    /// </summary>
    [Fact]
    public void TryOpenOrActivate_PathAlreadyFirstInRecent_DoesNotSaveSettings() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            string other = tmp.File("b.txt");
            File2.WriteAllText(path, "x");
            host.Settings.RecentFiles = new List<string> { path, other };

            Assert.NotNull(host.File.TryOpenOrActivate(path));

            Assert.Equal(new[] { path, other }, host.Settings.RecentFiles);
            Assert.Equal(0, host.SaveSettingsCount);
            Assert.Equal(0, host.RecentChangedCount);
        });

    /// <summary>既に開いているタブへ切り替えるだけの経路(grep の結果から飛ぶ等)でも、2 回目は保存しない。
    /// 1 回目は一覧が変わるので保存する(対照)。</summary>
    [Fact]
    public void TryOpenOrActivate_ActivatingAlreadyOpenTab_SavesOnlyOnFirstOpen() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "x");

            var first = host.File.TryOpenOrActivate(path);
            Assert.Equal(1, host.SaveSettingsCount);
            Assert.Equal(1, host.RecentChangedCount);

            Assert.Same(first, host.File.TryOpenOrActivate(path));

            Assert.Equal(1, host.SaveSettingsCount);
            Assert.Equal(1, host.RecentChangedCount);
        });

    /// <summary>
    /// 先頭が大文字小文字だけ違う同じファイルなら、先頭の表記が置き換わる=一覧は変わるので保存する。
    /// 比較を <c>PathKey</c>(大文字小文字を無視)にすると、この変化を取りこぼす(設計書 §12.2)。
    /// </summary>
    [Fact]
    public void TryOpenOrActivate_FirstEntryDiffersOnlyInCase_SavesSettings() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "x");
            string upper = path.ToUpperInvariant();
            Assert.NotEqual(path, upper); // 前提の自己検証(大文字小文字の差がある)
            host.Settings.RecentFiles = new List<string> { upper };

            Assert.NotNull(host.File.TryOpenOrActivate(path));

            Assert.Equal(new[] { path }, host.Settings.RecentFiles);
            Assert.Equal(1, host.SaveSettingsCount);
            Assert.Equal(1, host.RecentChangedCount);
        });

    /// <summary>既にある項目が先頭へ移るだけ(件数も集合も同じ)でも、順序が変わるので保存する。
    /// 集合として比べる実装を殺す。</summary>
    [Fact]
    public void TryOpenOrActivate_ExistingEntryMovesToFront_SavesSettings() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            string other = tmp.File("b.txt");
            File2.WriteAllText(path, "x");
            host.Settings.RecentFiles = new List<string> { other, path };

            Assert.NotNull(host.File.TryOpenOrActivate(path));

            Assert.Equal(new[] { path, other }, host.Settings.RecentFiles);
            Assert.Equal(1, host.SaveSettingsCount);
            Assert.Equal(1, host.RecentChangedCount);
        });
```

- [ ] **Step 2: テストが落ちることを確かめる**

```powershell
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~FileControllerTests.TryOpenOrActivate_" -c Debug
```

Expected: `PathAlreadyFirstInRecent_DoesNotSaveSettings` と `ActivatingAlreadyOpenTab_SavesOnlyOnFirstOpen` が FAIL(SaveSettingsCount が 1 / 2)。他の 2 本は PASS(対照)。

- [ ] **Step 3: 実装する**

```csharp
    /// <summary>開いた/保存したファイルを最近のファイルへ登録し、永続化＆メニュー再構築を促す。
    /// 性能改善フェーズ 7(P-22): 一覧が変わらなければ、settings.json の fsync 付き書込とメニューの再構築を省く
    /// (既に開いているタブへ grep の結果から飛ぶとき・先頭のファイルを開き直すとき)。
    /// 比較は Ordinal: <c>PathKey</c> で比べると、先頭項目の表記(大文字小文字など)だけが置き換わった変化を
    /// 取りこぼす。省いた場合も、終了時には設定が必ず保存される(設計書 §12.2 の影響の整理)。</summary>
    private void RegisterRecent(string path)
    {
        var s = _settings();
        var updated = RecentFilesList.Add(s.RecentFiles, path, RecentFilesList.MaxItems);
        if (updated.SequenceEqual(s.RecentFiles, StringComparer.Ordinal))
            return;
        s.RecentFiles = updated;
        _saveSettings();
        _recentChanged();
    }
```

- [ ] **Step 4: テストが通ることを確かめる**

```powershell
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~FileControllerTests|FullyQualifiedName~MainFormSmokeTests" -c Debug
```

Expected: PASS(全件)。

- [ ] **Step 5: commit**

```powershell
git add src/kxEdit.App/FileController.cs tests/kxEdit.App.Tests/FileControllerTests.cs
git commit -m "perf(app): 最近使ったファイルが変わらなければ settings.json を保存しない(P-22)"
```

commit 後の状態で Step 4 を再実行する。

- [ ] **Step 6: 陰性対照**(commit しない)

比較を `updated.SequenceEqual(s.RecentFiles, StringComparer.OrdinalIgnoreCase)` に替え、`FirstEntryDiffersOnlyInCase_SavesSettings` が落ちることを確かめる。`git checkout -- src/kxEdit.App/FileController.cs` で戻す。

---

## Task 4: P-12 バックアップ JSON を生の UTF-8 にする(前倒しの脆弱性レビュー)

**Files:**
- Modify: `src/kxEdit.Core/Backup/BackupStore.cs:1-20`
- Test: `tests/kxEdit.Core.Tests/Backup/BackupStoreTests.cs`

**Interfaces:**
- Consumes / Produces: なし

- [ ] **Step 1: テストを書く**(`Untitled_record_roundtrips_with_null_path` の後)

```csharp
    // ===== 性能改善フェーズ 7(P-12): 非 ASCII を \uXXXX にエスケープしない =====

    [Fact]
    public void Write_stores_non_ascii_as_raw_utf8()
    {
        using var t = new TempDir();
        var rec = Rec("id-raw", @"C:\文書\メモ.txt", "日本語の本文");
        BackupStore.Write(t.Root, rec);

        string json = System.Text.Encoding.UTF8.GetString(
            File.ReadAllBytes(Path.Combine(t.Root, rec.Id + ".json"))
        );
        Assert.Contains("日本語の本文", json, StringComparison.Ordinal);
        Assert.Contains("メモ.txt", json, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\u65E5", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("日本語とかな")]
    [InlineData("絵文字 😀 と結合 👨‍👩‍👧")]
    [InlineData("制御文字 \u0001\u001F と DEL \u007F")]
    [InlineData("引用符 \" とバックスラッシュ \\ と C:\\path\\")]
    [InlineData("HTML 的な <script>&amp;'</script>")]
    [InlineData("行区切り \u2028 と段落区切り \u2029")]
    [InlineData("改行 \r\n と LF \n と CR \r とタブ \t")]
    [InlineData("NUL \u0000 入り")]
    public void Write_then_LoadAll_roundtrips_special_characters(string content)
    {
        using var t = new TempDir();
        var rec = Rec("id-special", @"C:\docs\a.txt", content);
        BackupStore.Write(t.Root, rec);

        Assert.Equal(content, Assert.Single(BackupStore.LoadAll(t.Root)).Content);
    }

    /// <summary>
    /// 単独サロゲートは、書込で U+FFFD に置き換わる。これは Encoder を変える前(既定のエスケープ)でも
    /// 同じ挙動で、System.Text.Json のライターが不正な UTF-16 を置き換えるため(2026-09-26 に実測。
    /// 旧: <c>"a\uFFFDb"</c> / 新: 同じ)。Encoder の変更で例外や別の文字にならないことを固定する。
    /// 本文の単独サロゲートがバックアップで失われること自体は既存の性質(設計書 §12 の実施記録の申し送り)。
    /// </summary>
    [Theory]
    [InlineData("a\uD800b")]
    [InlineData("a\uDC00b")]
    public void Write_replaces_lone_surrogate_with_replacement_char_as_before(string content)
    {
        using var t = new TempDir();
        var rec = Rec("id-lone", @"C:\docs\a.txt", content);
        BackupStore.Write(t.Root, rec);

        Assert.Equal("a\uFFFDb", Assert.Single(BackupStore.LoadAll(t.Root)).Content);
    }

    /// <summary>旧形式(非 ASCII を \uXXXX にエスケープ)のファイルも読める(Encoder は書込にしか効かない)。
    /// 旧形式は、Encoder を指定しない既定のオプションで作る(変更前の BackupStore と同じ設定)。</summary>
    [Fact]
    public void LoadAll_reads_legacy_escaped_format()
    {
        using var t = new TempDir();
        var rec = Rec("id-legacy", @"C:\文書\メモ.txt", "日本語\r\n\"引用\" 😀");
        byte[] legacy = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(rec);
        Assert.Contains(
            @"\u65E5",
            System.Text.Encoding.UTF8.GetString(legacy),
            StringComparison.OrdinalIgnoreCase
        ); // 前提の自己検証(本当に旧形式)
        File.WriteAllBytes(Path.Combine(t.Root, rec.Id + ".json"), legacy);

        Assert.Equal(rec, Assert.Single(BackupStore.LoadAll(t.Root)));
    }
```

- [ ] **Step 2: テストが落ちることを確かめる**

```powershell
dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~BackupStoreTests" -c Debug
```

Expected: `Write_stores_non_ascii_as_raw_utf8` だけが FAIL(他は現状でも通る=等価性の網)。

- [ ] **Step 3: 実装する**

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;
```

```csharp
    /// <summary>性能改善フェーズ 7(P-12): 非 ASCII を \uXXXX にエスケープせず、生の UTF-8 で書く
    /// (日本語の本文はファイルが約半分になり、終了時の最終書込待ちが短くなる)。前例は
    /// <c>SessionLayoutStore</c> / <c>LastSessionBuffersStore</c>。読込は新旧どちらの形式も読める
    /// (Encoder は書込にしか効かない)。<c>UnsafeRelaxed</c> は HTML/JS に埋め込む文脈で危険という意味で、
    /// このファイルは %APPDATA% のローカルファイルで、HTML にも JS にも埋め込まない。
    /// 単独サロゲートは、この変更の前後とも U+FFFD に置き換わる(ライターの性質)。</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
```

- [ ] **Step 4: テストが通ることを確かめる**

```powershell
dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~Backup" -c Debug
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~BackupCoordinatorTests|FullyQualifiedName~SerialBackupWriterTests" -c Debug
```

Expected: PASS(全件)。

- [ ] **Step 5: commit**

```powershell
git add src/kxEdit.Core/Backup/BackupStore.cs tests/kxEdit.Core.Tests/Backup/BackupStoreTests.cs
git commit -m "perf(core): バックアップ JSON の非 ASCII をエスケープしない(P-12)"
```

commit 後の状態で Step 4 を再実行する。陰性対照は Step 2 の RED で代える(Encoder の 1 行を消すと `Write_stores_non_ascii_as_raw_utf8` が落ちることを Step 2 で見ている)。

- [ ] **Step 6: 前倒しの脆弱性レビュー**(別エージェント)

観点: バックアップファイルが HTML/JS/ログ表示に埋め込まれる経路がないか(`BackupCoordinator` の Trace・復元ダイアログの表示は `SanitizeForDisplay` を通るか)。`<`・`>`・`&`・`'`・U+2028 がエスケープされなくなることで壊れる読み手がないか。

---

## Task 5: P-15 掃除対象があるときだけプロセスを列挙する(前倒しの脆弱性レビュー)

**Files:**
- Modify: `src/kxEdit.App/PreviewUserDataSweeper.cs:45-62`
- Test: `tests/kxEdit.App.Tests/PreviewUserDataSweeperTests.cs`

**Interfaces:**
- Produces: `internal static int PreviewUserDataSweeper.SweepIfAnyAndSole(string root, Func<bool> isSoleInstance)`(削除できた数を返す)

- [ ] **Step 1: テストを書く**(`DefaultRoot_PointsAtPreviewParentUnderLocalAppData` の前)

```csharp
    // ===== 性能改善フェーズ 7(P-15): 掃除対象があるときだけプロセスを列挙する =====

    [Fact]
    public void SweepIfAnyAndSole_NoPreviewDirs_DoesNotAskForProcesses()
    {
        // 空ではない(紛らわしい名前のフォルダーがある)ルートから始める。
        using var tmp = new TempRoot();
        string keep = tmp.Dir("EBWebView");
        int asked = 0;

        int deleted = PreviewUserDataSweeper.SweepIfAnyAndSole(
            tmp.Path,
            () =>
            {
                asked++;
                return true;
            }
        );

        Assert.Equal(0, deleted);
        Assert.Equal(0, asked); // 全プロセスの列挙を省く(P-15 の本体)
        Assert.True(Directory.Exists(keep));
    }

    [Fact]
    public void SweepIfAnyAndSole_MissingRoot_DoesNotAskForProcesses()
    {
        using var tmp = new TempRoot();
        int asked = 0;

        int deleted = PreviewUserDataSweeper.SweepIfAnyAndSole(
            Path.Combine(tmp.Path, "does-not-exist"),
            () =>
            {
                asked++;
                return true;
            }
        );

        Assert.Equal(0, deleted);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void SweepIfAnyAndSole_WithPreviewDir_AndSoleInstance_Sweeps()
    {
        using var tmp = new TempRoot();
        string a = tmp.Dir("preview-aaaaaaaa");
        int asked = 0;

        int deleted = PreviewUserDataSweeper.SweepIfAnyAndSole(
            tmp.Path,
            () =>
            {
                asked++;
                return true;
            }
        );

        Assert.Equal(1, deleted);
        Assert.Equal(1, asked);
        Assert.False(Directory.Exists(a));
    }

    [Fact]
    public void SweepIfAnyAndSole_WithPreviewDir_AndOtherInstance_KeepsEverything()
    {
        // 並行インスタンスが居るときは消さない(従来の不変条件)。
        using var tmp = new TempRoot();
        string a = tmp.Dir("preview-aaaaaaaa");

        int deleted = PreviewUserDataSweeper.SweepIfAnyAndSole(tmp.Path, () => false);

        Assert.Equal(0, deleted);
        Assert.True(Directory.Exists(a));
    }
```

- [ ] **Step 2: テストが落ちることを確かめる**

```powershell
dotnet build tests/kxEdit.App.Tests -c Debug
```

Expected: FAIL(`SweepIfAnyAndSole` がない、のコンパイルエラー)。

- [ ] **Step 3: 実装する**

`SweepIfSoleInstance` を次に置き換え、直後に `SweepIfAnyAndSole` を足す:

```csharp
    /// <summary>
    /// 自分以外の kxEdit プロセスが居ないときだけ <see cref="Sweep"/> する。
    /// 起動経路から 1 回だけ呼ぶ(失敗しても起動は続ける)。
    /// </summary>
    internal static void SweepIfSoleInstance()
    {
        try
        {
            SweepIfAnyAndSole(DefaultRoot, IsSoleInstance);
        }
        catch (Exception ex)
        {
            // 掃除は best-effort。ここで起動を止めない(残骸が残るだけ)。
            Trace.TraceWarning($"preview sweep skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// 性能改善フェーズ 7(P-15): 先に <c>preview-*</c> を探し、1 件以上あるときだけ
    /// <paramref name="isSoleInstance"/>(OS の全プロセスの列挙)を呼ぶ。掃除対象がなければ結果は同じで、
    /// 起動のたびの全プロセスの列挙を省ける。判定と削除の間の TOCTOU の窓の性質は変わらない
    /// (従来も「プロセスの判定 → 列挙 → 削除」の間に別インスタンスが起動しうる)。
    /// </summary>
    /// <returns>削除できた数(テスト用)。</returns>
    internal static int SweepIfAnyAndSole(string root, Func<bool> isSoleInstance)
    {
        if (!Directory.Exists(root) || !Directory.EnumerateDirectories(root, Pattern).Any())
            return 0;
        return isSoleInstance() ? Sweep(root) : 0;
    }
```

- [ ] **Step 4: テストが通ることを確かめる**

```powershell
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~PreviewUserDataSweeperTests|FullyQualifiedName~MainFormSmokeTests.ProgramMain_gates_single_instance_before_touching_shared_state" -c Debug
```

Expected: PASS(全件。IL テストの目印 `SweepIfSoleInstance` は `Main` に残る)。

- [ ] **Step 5: commit**

```powershell
git add src/kxEdit.App/PreviewUserDataSweeper.cs tests/kxEdit.App.Tests/PreviewUserDataSweeperTests.cs
git commit -m "perf(app): プレビューの残骸があるときだけ起動時にプロセスを列挙する(P-15)"
```

commit 後の状態で Step 4 を再実行する。

- [ ] **Step 6: 陰性対照**(commit しない)

`SweepIfAnyAndSole` の先頭の `if` を消し、`NoPreviewDirs_DoesNotAskForProcesses` が落ちることを確かめる。`git checkout -- src/kxEdit.App/PreviewUserDataSweeper.cs` で戻す。

- [ ] **Step 7: 前倒しの脆弱性レビュー**(別エージェント。Task 6 と 1 回にまとめてよい)

観点: 列挙の順序を変えたことで、並行インスタンスのプロファイルを消す窓が広がらないか。`EnumerateDirectories` の例外が起動を止めないか(外側の catch)。

---

## Task 6: P-21(a) プレビューを閉じるときの削除を背景で行う(前倒しの脆弱性レビュー)

**Files:**
- Modify: `src/kxEdit.App/PreviewUserDataFolder.cs`
- Test: `tests/kxEdit.App.Tests/PreviewUserDataFolderTests.cs`

**Interfaces:**
- Consumes: `PreviewUserDataSweeper.DefaultRoot`(既存)
- Produces:
  - `internal PreviewUserDataFolder(string parentDir, IReadOnlyList<TimeSpan> retryDelays)`
  - `internal Task? DeletionTask { get; }`(`Dispose` の前は null)
  - `internal static Task<int> DeleteWithRetryAsync(string path, IReadOnlyList<TimeSpan> retryDelays)`(試行の回数を返す)

- [ ] **Step 1: 既存テストを非同期削除に合わせて直す**

`Dispose_RemovesDirectory` / `Dispose_Idempotent` / `Dispose_RemovesEmptyBaseFolder` で、`sut.Dispose();` の後(Idempotent は 2 回目の後)に次の 1 行を入れる:

```csharp
            Assert.True(sut.DeletionTask!.Wait(TimeSpan.FromSeconds(10))); // 削除は背景で行う(P-21(a))
```

`Dispose_Idempotent` にはさらに、2 回目の `Dispose` の前後で同じタスクであることを足す:

```csharp
            sut.Dispose();
            var first = sut.DeletionTask;
            // 2 回目でも throw せず、削除を二重に投げない。
            sut.Dispose();
            Assert.Same(first, sut.DeletionTask);
```

クラスの doc の「L5 検証項目」の前に 1 行足す:

```csharp
/// 性能改善フェーズ 7(P-21(a)): 削除は背景で行う。Dispose の直後はフォルダーが残っていることがあるので、
/// 削除を確かめるテストは <c>DeletionTask</c> の完了を待つ。
```

- [ ] **Step 2: 新しいテストを書く**(`SafeCleanup` の前)

```csharp
    // ===== 性能改善フェーズ 7(P-21(a)): 削除は背景で行い、短い間隔でリトライする =====

    [Fact]
    public void Dispose_WhileLocked_ReturnsWithoutWaiting_AndDeletesAfterUnlock()
    {
        // WebView2 のブラウザプロセスがプロファイルを掴んだまま閉じた形。Dispose は削除を待たずに戻り、
        // ロックが外れた後のリトライで消える。
        using var tmp = new TempDir();
        var sut = new PreviewUserDataFolder(
            tmp.Root,
            Enumerable.Repeat(TimeSpan.FromMilliseconds(50), 100).ToArray()
        );
        string locked = System.IO.Path.Combine(sut.Path, "held");
        var fs = new System.IO.FileStream(
            locked,
            System.IO.FileMode.CreateNew,
            System.IO.FileAccess.Write,
            System.IO.FileShare.None
        );
        try
        {
            sut.Dispose();

            Assert.NotNull(sut.DeletionTask);
            Assert.False(sut.DeletionTask!.IsCompleted); // 掴まれている間は終わらない=Dispose は待っていない
            Assert.True(System.IO.Directory.Exists(sut.Path));
        }
        finally
        {
            fs.Dispose();
        }

        Assert.True(sut.DeletionTask!.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(System.IO.Directory.Exists(sut.Path));
    }

    [Fact]
    public void DeleteWithRetry_RetriesAfterFailure()
    {
        using var tmp = new TempDir();
        string dir = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tmp.Root, "preview-x"))
            .FullName;
        var fs = new System.IO.FileStream(
            System.IO.Path.Combine(dir, "held"),
            System.IO.FileMode.CreateNew,
            System.IO.FileAccess.Write,
            System.IO.FileShare.None
        );
        Task<int> task;
        try
        {
            task = PreviewUserDataFolder.DeleteWithRetryAsync(
                dir,
                Enumerable.Repeat(TimeSpan.FromMilliseconds(50), 100).ToArray()
            );
            Thread.Sleep(300); // 掴まれている間に少なくとも 1 回失敗させる
        }
        finally
        {
            fs.Dispose();
        }

        Assert.True(task.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(task.Result >= 2, $"attempts={task.Result}"); // 失敗の後にリトライして消した
        Assert.False(System.IO.Directory.Exists(dir));
    }

    [Fact]
    public void DeleteWithRetry_GivesUpAfterRetries_WithoutThrowing()
    {
        // 最後まで消せなければ諦める(残骸は次回起動の sweeper が回収する)。例外は外へ出さない。
        using var tmp = new TempDir();
        string dir = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tmp.Root, "preview-y"))
            .FullName;
        using var fs = new System.IO.FileStream(
            System.IO.Path.Combine(dir, "held"),
            System.IO.FileMode.CreateNew,
            System.IO.FileAccess.Write,
            System.IO.FileShare.None
        );

        var task = PreviewUserDataFolder.DeleteWithRetryAsync(
            dir,
            new[] { TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10) }
        );

        Assert.True(task.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(3, task.Result); // 初回 + リトライ 2 回
        Assert.True(System.IO.Directory.Exists(dir));
    }

    [Fact]
    public void DeleteWithRetry_MissingDirectory_IsNoOp()
    {
        using var tmp = new TempDir();
        var task = PreviewUserDataFolder.DeleteWithRetryAsync(
            System.IO.Path.Combine(tmp.Root, "preview-missing"),
            new[] { TimeSpan.FromMilliseconds(10) }
        );

        Assert.True(task.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, task.Result);
    }
```

ファイル先頭に `TempDir`(`tests/kxEdit.App.Tests/TempDir.cs` の internal クラス)を使うための using は不要(同じ名前空間)。

- [ ] **Step 3: テストが落ちることを確かめる**

```powershell
dotnet build tests/kxEdit.App.Tests -c Debug
```

Expected: FAIL(`DeletionTask`・internal コンストラクター・`DeleteWithRetryAsync` がない、のコンパイルエラー)。

- [ ] **Step 4: 実装する**

`PreviewUserDataFolder.cs` を次のとおりにする(`EnsureEmptyBaseFolder` とその doc は変えない)。クラスの doc の「削除失敗 (WebView2 プロセスが直後まで残るケース等) は…」の段落は次に置き換える:

```csharp
/// <para>
/// 性能改善フェーズ 7(P-21(a)): 削除は背景で行う(UI スレッドで再帰削除を待たない)。
/// WebView2 のブラウザプロセスは非同期に終了し、それまでプロファイルを掴んでいるので、
/// 短い間隔で数回リトライする。最後まで消せなければ Trace 警告を残して諦め、次回起動の
/// <see cref="PreviewUserDataSweeper"/> に任せる。アプリの終了で背景の削除が打ち切られた場合も同じ。
/// </para>
```

本体:

```csharp
internal sealed class PreviewUserDataFolder : IDisposable
{
    /// <summary>削除のリトライの間隔(P-21(a))。合計 1.5 秒待っても消せなければ、次回起動の sweeper に任せる。</summary>
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
    ];

    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private int _disposed;

    /// <summary>WebView2 の <c>userDataFolder</c> に渡す絶対パス。</summary>
    public string Path { get; }

    /// <summary>背景の削除(<see cref="Dispose"/> の前は null)。テストが完了を待つための観測点。</summary>
    internal Task? DeletionTask { get; private set; }

    public PreviewUserDataFolder()
        : this(PreviewUserDataSweeper.DefaultRoot, DefaultRetryDelays) { }

    /// <summary>テスト用: 親フォルダーとリトライの間隔を差し替える。</summary>
    internal PreviewUserDataFolder(string parentDir, IReadOnlyList<TimeSpan> retryDelays)
    {
        _retryDelays = retryDelays;
        // Guid.NewGuid().ToString("N") = 32 桁小文字 hex (ハイフン無し)。
        // ファイルシステム安全かつ per-form 一意性を担保。
        Path = System.IO.Path.Combine(parentDir, "preview-" + Guid.NewGuid().ToString("N"));
        // idempotent: 既存でも throw しない。
        System.IO.Directory.CreateDirectory(Path);
    }

    // (EnsureEmptyBaseFolder は現状のまま)

    public void Dispose()
    {
        // 2 回目以降は何もしない(削除を二重に投げない)。
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        // 削除するのは ctor で自分が作った Path だけ(不変・外部入力を含まない)。
        DeletionTask = DeleteWithRetryAsync(Path, _retryDelays);
    }

    /// <summary>
    /// <paramref name="path"/> を再帰削除する。<see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/> なら <paramref name="retryDelays"/> の間隔で再試行し、
    /// 使い切ったら Trace 警告を残して諦める(例外は外へ出さない)。
    /// <see cref="System.IO.Directory.Delete(string, bool)"/> はリパースポイント(ジャンクション・
    /// シンボリックリンク)の先を辿らず、リンク自体だけを消す(従来の同期削除と同じ API・同じ性質)。
    /// </summary>
    /// <returns>試行の回数(テスト用)。</returns>
    internal static Task<int> DeleteWithRetryAsync(string path, IReadOnlyList<TimeSpan> retryDelays) =>
        Task.Run(async () =>
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (System.IO.Directory.Exists(path))
                        System.IO.Directory.Delete(path, recursive: true);
                    return attempt + 1;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= retryDelays.Count)
                    {
                        // 次回起動の sweeper(PreviewUserDataSweeper)が回収する。
                        System.Diagnostics.Trace.TraceWarning(
                            $"PreviewUserDataFolder 削除失敗: {ex.Message} ({path})"
                        );
                        return attempt + 1;
                    }
                    await Task.Delay(retryDelays[attempt]).ConfigureAwait(false);
                }
            }
        });
}
```

`MarkdownPreviewForm.Dispose` の doc(`:261-266`)の「逆順にすると WebView2 側のロックにかかって Delete が Trace 警告に落ちる」に、「(P-21(a) 以後は背景でリトライするが、先に WebView2 を破棄する順は変えない)」を足す。

- [ ] **Step 5: テストが通ることを確かめる**

```powershell
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~PreviewUserDataFolderTests|FullyQualifiedName~PreviewUserDataSweeperTests|FullyQualifiedName~MarkdownPreviewForm" -c Debug
```

Expected: PASS(全件)。テスト後に `%LOCALAPPDATA%\kxEdit\WebView2\preview-*` が増えていないことを目で確かめる(既存テストは実フォルダーを使う)。

- [ ] **Step 6: commit**

```powershell
git add src/kxEdit.App/PreviewUserDataFolder.cs src/kxEdit.App/MarkdownPreviewForm.cs tests/kxEdit.App.Tests/PreviewUserDataFolderTests.cs
git commit -m "perf(app): プレビューを閉じるときの作業フォルダーの削除を背景で行う(P-21(a))"
```

commit 後の状態で Step 5 を再実行する。

- [ ] **Step 7: 陰性対照**(commit しない)

`catch` の中の `await Task.Delay(...)` の前に `return attempt + 1;` を入れて(リトライしない)、`DeleteWithRetry_RetriesAfterFailure` が落ちることを確かめる。`git checkout -- src/kxEdit.App/PreviewUserDataFolder.cs` で戻す。

- [ ] **Step 8: 前倒しの脆弱性レビュー**(別エージェント。Task 5 と 1 回にまとめてよい)

観点: 削除対象が常に自分で作った `preview-<guid>` か(引数・フィールドに外部入力が混ざらないか)。リパースポイントを辿らないか(`Directory.Delete(recursive)` の性質)。リトライの間に同じパスへ別の何かが作られる経路がないか(GUID の一意性)。アプリ終了時に背景の削除が打ち切られても、次回の sweeper が回収するか。`Dispose` を 2 回呼んでも二重に削除しないか。

---

## Task 7: 変更後の計測と記録(docs のみ)

**Files:**
- Modify: `docs/plans/2026-09-26-perf-io-settings.md`(末尾の「実施記録」)

- [ ] **Step 1: 変更後の Release をビルドし、ベンチを 3 回走らせる**

```powershell
$S = '<scratchpad>'
dotnet build src/kxEdit.App -c Release -o "$S\bin-after"
dotnet build "$S\io-bench" -c Release -p:KxBin="$S\bin-after" -o "$S\bench-after"
1..3 | ForEach-Object { dotnet "$S\bench-after\io-bench.dll" } | Out-File -Encoding utf8 "$S\after.txt"
Get-Content "$S\after.txt"
```

- [ ] **Step 2: 変更前後を表にして実施記録へ書く**

判断基準(設計書 §3.2): 変更後の中央値が、変更前の 3 回の最小〜最大を超えて改善していること。

| 項目 | 期待 |
|---|---|
| `ts-unc-loopback` | 改善(往復 4 → 1)。ループバックなので差は小さい。揺れの範囲なら PR に書く |
| `ts-local` | 同等(`File.Exists`+`GetLastWriteTimeUtc` の 2 回 → `FileInfo` 1 回で、わずかに改善しうる) |
| `backup-bytes` | 約半分 |
| `backup-write` | 改善 |
| `settings-save` / `is-sole-instance` | 変わらない(省ける 1 回あたりの費用として記録する) |
| `preview-dispose` | 大きく改善(削除を待たない) |

- [ ] **Step 3: commit**

```powershell
git add docs/plans/2026-09-26-perf-io-settings.md
git commit -m "docs(perf): フェーズ 7 の計測の記録"
```

---

## Task 8: 最終レビュー・品質ゲート・PR

- [ ] **Step 1: 最終ブランチレビュー(2 パス・別エージェント)**(CLAUDE.md §3 の 5)
  - コード品質パス(ミューテーション検証のスポットチェックは行わない=Global Constraints。陰性対照の記録を確かめる)
  - 脆弱性パス
  - 指摘は fixup commit で反映し、対応を 3 択(修正 / PR に記載して受容 / 理由付き却下)で記録する。

- [ ] **Step 2: 設計書 §12 の末尾に「12.7 実施記録」を追記する**(フェーズ 4〜6 と同じく PR に同梱する)

書く内容: 成果物・完了条件(計測・L5 不要・レビュー)・本節からの精密化と逸脱(本計画 0.2)・意図的な挙動差(0.5)・以後への申し送り。

```powershell
git add docs/plans/2026-09-24-general-perf-improvements-design.md
git commit -m "docs(perf): 設計書 §12 にフェーズ 7 の実施記録を追記"
```

- [ ] **Step 3: 品質ゲート**

```powershell
pwsh -File tools/pre-merge-check.ps1
```

Expected: EXIT 0。

- [ ] **Step 4: push と PR**(ユーザーの了承を得てから)

PR description(日本語): 目的・変更点(P-11・P-22・P-12・P-15・P-21(a))・変更前後の計測値・意図的な挙動差(0.5 の 4 行)・逸脱(0.1 の harness M-6 リモート版なし・0.2)・レビュー経緯・L5 不要の理由・申し送り。

---

## 実施記録

### 計測条件(Task 7・2026-09-26)

- 機体・条件は Task 1(変更前)と同一: Release ビルド、NVDA は起動したまま(§0.1 の方針どおり)、管理共有 `\\localhost\C$` へのループバックで UNC 経路を通した。他の重い処理と並走させていない。
- ベンチ csproj の訂正(Task 1 から踏襲): ブリーフ記載は `$(KxBin)\kxEdit.App.dll` だが、`src/kxEdit.App/kxEdit.App.csproj` は `<AssemblyName>kxEdit</AssemblyName>` のため実際の出力は `kxEdit.dll`。変更後ビルド(`$S\bin-after`)でも参照は `$(KxBin)\kxEdit.dll` のまま(scratchpad の使い捨てプロジェクトのみの修正・リポジトリへの commit なし)。
- 変更後の `kxEdit.App` の Release ビルドは 0 警告・0 エラー。ベンチ側は Task 1 と同じ `WindowsBase`(v4.0.0.0/v9.0.0.0)バージョン競合の warning MSB3277 が 1 種類出るのみで実行に影響なし。3 回とも例外なし(各回 7 行、期待どおり)。

### 変更前後(3 run の median の中央値、および 3 run 通した min–max)

判断基準(設計書 §3.2): 変更後の中央値が、変更前の 3 回の最小〜最大を超えて改善していること。`settings-save`・`is-sole-instance` は P-22・P-15 が「呼ぶかどうか」を変えるだけで呼出自体のコストは変えない項目のためこの基準の対象外とし、1 回あたりの省ける費用として記録する。

| 指標 | 変更前 中央値 | 変更前 min–max | 変更後 中央値 | 変更後 min–max | 判定 |
|---|---|---|---|---|---|
| `ts-local` | 0.016 ms | 0.015–0.147 ms | 0.008 ms | 0.008–0.143 ms | 改善(基準を満たす。変更後中央値が変更前の最小を下回る) |
| `ts-unc-loopback` | 2.488 ms | 1.678–27.709 ms | 0.672 ms | 0.518–1.018 ms | 改善(基準を満たす。往復 4→1 の効果がループバックでも揺れを超えて出た) |
| `backup-write` | 5.748 ms | 4.987–9.079 ms | 4.491 ms | 4.238–5.359 ms | 改善(基準を満たす) |
| `backup-bytes` | 989154 bytes(3 回とも固定) | — | 539145 bytes | 539144–539145 bytes | 期待どおり(約 55%に縮小。ほぼ半分) |
| `settings-save` | 2.739 ms | 1.960–3.944 ms | 2.729 ms | 2.298–4.489 ms | 変わらない(基準の対象外。呼出コスト自体は不変。一覧が変わらないときは 1 回あたり約 2.7 ms がまるごと省ける) |
| `is-sole-instance` | 0.434 ms | 0.418–0.616 ms | 0.400 ms | 0.382–1.299 ms | 変わらない(基準の対象外。呼出コスト自体は不変。残骸なしのときは 1 回あたり約 0.4 ms がまるごと省ける) |
| `preview-dispose` | 54.562 ms | 35.312–61.785 ms | 0.026 ms | 0.024–0.046 ms | 大きく改善(基準を大きく上回って達成。削除を待たずに UI スレッドへ戻る) |

- `preview-dispose` はフェーズ 7 で最も効果が大きい項目で、50〜60 ms 台から 0.03 ms 未満へ落ちた(削除を背景タスクへ逃した分、そのまま UI スレッドの停止時間から消えた)。
- `ts-unc-loopback`・`ts-local`・`backup-write` は基準を満たして改善。`ts-unc-loopback` はループバックのため絶対値の差は小さいが、変更後の最大(1.018 ms)が変更前の最小(1.678 ms)を下回っており、揺れの範囲では説明できない。
- `backup-bytes` は日本語混じり本文で約 55%(≒半分)に縮小し、期待どおり。
- `settings-save` は変更前後で範囲が重なり、実質不変。`is-sole-instance` は変更後中央値(0.400 ms)が変更前の最小(0.418 ms)をわずかに(0.018 ms)下回るが、この項目自体(プロセス列挙)のコードは P-15 で変えておらず、変更後の run 自身にも 1.299 ms という外れ値があることから、この差は測定ノイズと判断する(設計書の期待どおり「変わらない」)。P-15 の効果は、残骸がないときにこの呼出そのものを丸ごと省くこと。
