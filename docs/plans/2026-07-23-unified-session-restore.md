# セッション復元 hot exit 型統合 実装計画

> **For Claude:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development(本セッション実行時)
> または superpowers:executing-plans(別セッション実行時)で本計画をタスク単位に実施する。

**Goal:** バックアップ復元と前回セッション復元を hot exit 型に統合する
(設計書: `docs/plans/2026-07-23-unified-session-restore-design.md`・§10 精密化込み)。

**Architecture:** 未保存内容は既存バックアップストア(`backups/session-*/<Id>.json`)に一本化し、
タブレイアウトは `session-state.json` へ Reconcile 相乗りで定期退避する。起動時復元は
`FileController.RestoreSession` の 1 経路に統合し、設定 ON では終わり方を区別せず silent 復元する。
消費済みバックアップは adopt-move で現セッション dir へ引き取る(BK-M-2 由来の再提案バグ根治)。

**Tech Stack:** .NET 9 / WinForms / System.Text.Json / xUnit(既存テスト基盤:
FakeBackupWriter・FakeRestorePrompt・MainForm internal ctor seam)。

**検証コマンド(全タスク共通)**:

```powershell
dotnet build -warnaserror                                  # 0 warning 維持
dotnet test tests/yEdit.Core.Tests                         # L1
dotnet test tests/yEdit.App.Tests                          # L3
pwsh tools/pre-merge-check.ps1                             # 最終ゲート(マージ前・EXIT 0)
```

**レビュー計画(CLAUDE.md §3・§4)**: 各タスク完了時に仕様レビュー(別エージェント)。
下表の★は前倒しレビューを追加する:

| Task | 内容 | 前倒しレビュー |
|---|---|---|
| 1 | SessionLayout / SessionLayoutStore | ★脆弱性(外部入力パース)+★品質(後続依存の新 seam) |
| 2 | BackupStore.TryMoveToSessionDir | ★脆弱性(パス操作) |
| 3 | Coordinator レイアウト退避+Shutdown(keep)+新 API | ★品質(後続依存の新 seam) |
| 4 | OfferRestoreOnStartup の adopt-move | ★脆弱性(復元+ファイル移動) |
| 5 | RestoreSession 統合復元+LegacySessionConverter | ★脆弱性(外部入力由来パスの復元) |
| 6 | MainForm 配線(OnShown/OnFormClosing/OnFormClosed) | (仕様レビューのみ) |
| 7 | 旧機構の退役+テスト移植 | (仕様レビューのみ) |
| 8 | 説明書・docs 同期 | (最終 2 パスに含める) |

最終: ブランチ全体をコード品質パス(ミューテーション検証スポットチェック込み)+脆弱性パスの
2 パスで別エージェントレビュー → fixup → pre-merge-check → push → PR。

---

## Task 1: Core `SessionLayout` / `SessionLayoutStore`

**Files:**
- Create: `src/yEdit.Core/Session/SessionLayout.cs`
- Create: `src/yEdit.Core/Session/SessionLayoutStore.cs`
- Test: `tests/yEdit.Core.Tests/Session/SessionLayoutStoreTests.cs`

**Step 1: 失敗するテストを書く**(`LastSessionBuffersStoreTests.cs` の TempDir パターンを踏襲)

テストケース(設計 §6.1):
1. Save→Load 往復(空 Tabs / 1 件 / 多件・日本語パス・SavedAtUtc 保存)
2. Load: ファイル未存在 → null / 破損 JSON → null / 空ファイル → null / 4 MB 超 → null
3. Save: 親ディレクトリ未作成 → 作成される / AtomicFile 経由(書込後に `*.tmp` 残骸なし)
4. Delete: 冪等(未存在で no-op)
5. Normalize 防御(内部 `Normalize` を InternalsVisibleTo 経由で直接検証):
   - Tabs null → 空 / null 要素 skip / 201 件 → 200 件(`MaxTabs` 切り詰め)
   - Path 空白のみ → record skip(Path null の無題は保持)
   - BackupId 不正形式(`"../evil"`, 大文字 hex, 33 桁)→ null 化
   - BackupId 重複 → 2 個目以降 null 化
   - IsActive 複数 true → 先頭のみ true
   - UntitledNumber / CaretLine / CaretColumn / LineEnding 負値 → 0 clamp

**Step 2: 実行して失敗を確認**

```powershell
dotnet test tests/yEdit.Core.Tests --filter "FullyQualifiedName~SessionLayoutStore"
```

**Step 3: 実装**

`SessionLayout.cs`:

```csharp
namespace yEdit.Core.Session;

/// <summary>
/// hot exit 統合(設計 2026-07-23 §2.1)のタブ 1 個のレイアウト表現。dirty 文書の本文・
/// エンコーディングは BackupId が参照する BackupRecord 側が持つ(重複して持たない)。
/// LineEnding は「空の無題タブの枠」復元にのみ使う(パスありは復元時 auto-detect)。
/// </summary>
public sealed record SessionLayoutRecord(
    string? Path,
    int UntitledNumber,
    string? BackupId,
    bool IsActive,
    int CaretLine,
    int CaretColumn,
    int LineEnding
);

/// <summary>タブ列レイアウトのスナップショット(順序=タブ順)。SavedAtUtc は診断用。</summary>
public sealed record SessionLayout(List<SessionLayoutRecord> Tabs, DateTime SavedAtUtc);
```

`SessionLayoutStore.cs`(要点。Load は例外を漏らさず null・Save は例外を漏らす=
SerialBackupWriter のジョブ catch が受ける契約・`BackupStore.Write` と対称):

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;
using yEdit.Core.Backup;

namespace yEdit.Core.Session;

/// <summary>
/// session-state.json(タブレイアウトの定期スナップショット)の Load/Save/Delete。
/// 外部入力(改竄可能)として扱い、Load 時に Normalize で防御する(設計 §2.3)。
/// 書込は AtomicFile(temp→Replace)。単一ファイル・last-writer-wins(複数インスタンスは M9+)。
/// </summary>
public static class SessionLayoutStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 日本語パスを生 UTF-8 で
    };

    /// <summary>タブ数上限(設計 §2.3)。超過は先頭 MaxTabs 件で切り詰め+Trace 警告。</summary>
    internal const int MaxTabs = 200;

    /// <summary>Load 時 size cap。レイアウトは本文を含まない(数 KB 想定)ため 4 MB で攻撃 JSON を遮断。</summary>
    internal const long MaxLoadFileSizeBytes = 4L * 1024 * 1024;

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "yEdit",
            "session-state.json"
        );

    public static SessionLayout? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var info = new FileInfo(path);
            if (info.Length > MaxLoadFileSizeBytes)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "yEdit: session-state.json too large ({0} bytes); ignoring.", info.Length);
                return null;
            }
            var raw = JsonSerializer.Deserialize<SessionLayout>(File.ReadAllText(path), Options);
            return raw is null ? null : Normalize(raw);
        }
        catch
        {
            return null; // 破損=レイアウトなし扱い(E5'。extras 復元は呼び出し側で継続)
        }
    }

    /// <summary>設計 §2.3 の防御的補正。Load 経路専用(Save 側は自プロセス生成値=補正不要)。</summary>
    internal static SessionLayout Normalize(SessionLayout raw)
    {
        var source = raw.Tabs ?? new List<SessionLayoutRecord>();
        var cleaned = new List<SessionLayoutRecord>(Math.Min(source.Count, MaxTabs));
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        bool activeSeen = false;
        foreach (var t in source)
        {
            if (cleaned.Count >= MaxTabs)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "yEdit: session-layout-tabs-capped ({0} -> {1})", source.Count, MaxTabs);
                break;
            }
            if (t is null)
                continue;
            if (t.Path is not null && string.IsNullOrWhiteSpace(t.Path))
                continue;
            string? backupId = t.BackupId;
            if (backupId is not null && !BackupIdValidator.IsValid(backupId))
                backupId = null; // 不正 Id はパストラバーサル痕跡の可能性 → 参照ごと捨てる
            if (backupId is not null && !seenIds.Add(backupId))
                backupId = null; // 1 バックアップ 1 タブの不変(重複参照は 2 個目以降を demote)
            bool isActive = t.IsActive && !activeSeen;
            if (isActive)
                activeSeen = true;
            cleaned.Add(
                t with
                {
                    BackupId = backupId,
                    IsActive = isActive,
                    UntitledNumber = Math.Max(0, t.UntitledNumber),
                    CaretLine = Math.Max(0, t.CaretLine),
                    CaretColumn = Math.Max(0, t.CaretColumn),
                    LineEnding = Math.Max(0, t.LineEnding),
                }
            );
        }
        return new SessionLayout(cleaned, raw.SavedAtUtc);
    }

    public static void Save(string path, SessionLayout layout)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        IO.AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(layout, Options));
    }

    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        { /* 削除失敗は無害(次回上書き) */
        }
    }
}
```

**Step 4: テスト緑を確認 → Step 5: Commit**

```
feat(core): SessionLayout / SessionLayoutStore を新設(hot exit 統合 §2)
```

**Step 6: 仕様レビュー+★脆弱性レビュー+★品質レビュー**(指摘は fixup commit)

---

## Task 2: Core `BackupStore.TryMoveToSessionDir`(adopt-move)

**Files:**
- Modify: `src/yEdit.Core/Backup/BackupStore.cs`(`DeleteAll` の後に追加)
- Test: `tests/yEdit.Core.Tests/Backup/BackupStoreTests.cs`(既存に追記)

**Step 1: 失敗するテストを書く**

1. flat 配置(`baseDir` 直下)→ session dir へ移動・移動後に元が消え先に存在・内容不変
2. 別 session dir → 自 session dir へ移動+**空になった元 session dir が削除される**
3. 移動先に既に存在(自 dir 管理下)→ true・移動元の重複ファイルは削除される
4. どこにも無い → false
5. 不正 Id(`"..\\evil"` / 大文字)→ `ArgumentException`(HIGH-1 対称)
6. baseDir 未存在 → false
7. 移動先 dir 未作成 → 作成される

**Step 2: 失敗確認 → Step 3: 実装**

```csharp
/// <summary>設計 2026-07-23 統合 §3.4(adopt-move): baseDir 直下(flat)と session-* subdir から
/// <paramref name="id"/>.json を探し、<paramref name="targetSessionDir"/> へ原子的に移動する。
/// 復元で消費したバックアップを現セッションの管理下へ引き取り、M5 の「同一ファイル継続使用」
/// 不変条件を BK-M-2 の session-dir 構成下で回復する(消費済みバックアップが旧 dir に残り
/// 再提案・silent 複製される潜在バグの根治)。見つからない/移動失敗は false(呼び出し側は
/// trace のみで復元続行=最悪でも従来同様の再提案に退化するだけでデータは失わない)。
/// HIGH-1 対称: id は白リスト検証してから Path.Combine に流す。</summary>
public static bool TryMoveToSessionDir(string baseDir, string id, string targetSessionDir)
{
    if (!BackupIdValidator.IsValid(id))
        throw new ArgumentException(
            $"Backup id must be a canonical GUID N (32 hex chars). Got: '{id}'",
            nameof(id)
        );

    string fileName = id + ".json";
    string targetFull = Path.GetFullPath(targetSessionDir);
    string target = Path.Combine(targetFull, fileName);

    // 検索対象: baseDir 直下(flat 後方互換)+ session-*(自 dir は除外=自分自身の移動を防ぐ)
    var searchDirs = new List<string>();
    if (Directory.Exists(baseDir))
    {
        searchDirs.Add(baseDir);
        foreach (string sub in Directory.EnumerateDirectories(baseDir, "session-*"))
            if (!string.Equals(Path.GetFullPath(sub), targetFull, StringComparison.OrdinalIgnoreCase))
                searchDirs.Add(sub);
    }

    bool alreadyAtTarget = File.Exists(target);
    foreach (string dir in searchDirs)
    {
        string src = Path.Combine(dir, fileName);
        if (!File.Exists(src))
            continue;
        try
        {
            if (alreadyAtTarget)
            {
                File.Delete(src); // 自 dir 管理下に統一(別 dir の stale 重複を掃除)
            }
            else
            {
                Directory.CreateDirectory(targetFull);
                File.Move(src, target); // 同一ボリューム内=原子的
                alreadyAtTarget = true;
            }
            // 空になった session-* は掃除(flat の baseDir 自体は消さない)
            if (!string.Equals(Path.GetFullPath(dir), Path.GetFullPath(baseDir), StringComparison.OrdinalIgnoreCase))
                TryDeleteEmptySessionDir(dir);
        }
        catch
        {
            return alreadyAtTarget; // 移動/削除失敗は致命でない(次回起動で再挑戦)
        }
    }
    return alreadyAtTarget;
}

private static void TryDeleteEmptySessionDir(string sessionDir)
{
    try
    {
        if (!Directory.EnumerateFileSystemEntries(sessionDir).Any())
            Directory.Delete(sessionDir);
    }
    catch
    { /* ロック中等=無害。30 日 sweep で最終回収 */
    }
}
```

(`using System.Linq;` が BackupStore.cs に無ければ追加。)

**Step 4: 緑確認 → Step 5: Commit**

```
feat(core): BackupStore.TryMoveToSessionDir を新設(adopt-move・§3.4)
```

**Step 6: 仕様レビュー+★脆弱性レビュー**

---

## Task 3: BackupCoordinator レイアウト退避+Shutdown(keep)+統合復元 API

**Files:**
- Modify: `src/yEdit.App/Abstractions/IBackupWriter.cs`
- Modify: `src/yEdit.App/SerialBackupWriter.cs`
- Modify: `src/yEdit.App/BackupCoordinator.cs`
- Modify: `tests/yEdit.App.Tests/Fakes/FakeBackupWriter.cs`
- Test: `tests/yEdit.App.Tests/BackupCoordinatorTests.cs`(既存 harness に追記)

**Step 1: IBackupWriter 拡張**

```csharp
/// <summary>レイアウト書込失敗の通知(次 Reconcile で強制再書込)。</summary>
Action? OnLayoutWriteFailed { get; set; }

/// <summary>セッションレイアウトを path へ書き込むジョブを投入する(SessionLayoutStore.Save)。</summary>
void WriteLayout(string path, yEdit.Core.Session.SessionLayout layout);

/// <summary>セッションレイアウトを削除するジョブを投入する(OFF 終了時の stale 掃除)。</summary>
void DeleteLayout(string path);
```

SerialBackupWriter 実装(既存 Write/Delete と同型の Enqueue ジョブ):

```csharp
public Action? OnLayoutWriteFailed { get; set; }

public void WriteLayout(string path, yEdit.Core.Session.SessionLayout layout) =>
    Enqueue(() =>
    {
        try
        {
            yEdit.Core.Session.SessionLayoutStore.Save(path, layout);
        }
        catch
        {
            OnLayoutWriteFailed?.Invoke();
        }
    });

public void DeleteLayout(string path) =>
    Enqueue(() =>
    {
        try
        {
            yEdit.Core.Session.SessionLayoutStore.Delete(path);
        }
        catch
        { /* 削除失敗は致命でない・無音 */
        }
    });
```

FakeBackupWriter: `List<SessionLayout> LayoutWrites`・`List<string> LayoutWritePaths`・
`int LayoutDeletes`・`bool FailNextLayoutWrite`(true なら書込を記録せず OnLayoutWriteFailed を
同期発火して false へ戻す)を既存スタイルで追加。

**Step 2: 失敗するテストを書く**(既存 harness `new BackupCoordinator(...)` に引数追加)

1. `restoreSessionEnabled=true` で Reconcile → FakeWriter.LayoutWrites に 1 件・Tabs が現タブ列と
   一致(Path/UntitledNumber/IsActive/Caret/LineEnding)・dirty 文書の BackupId=map の Id・
   クリーン文書の BackupId=null
2. 変化なしで再 Reconcile → 書込増えない(**非既定状態から開始**=先に 1 回書かせてから検証)
3. タブ追加/アクティブ切替/カーソル移動 → 署名変化で再書込
4. `restoreSessionEnabled=false` → LayoutWrites 常に 0
5. `enabled=false, restoreSessionEnabled=true` → 本文バックアップは書かれずレイアウトのみ書かれる
   (writer は生成される・timer 起動)
6. FailNextLayoutWrite → 次 Reconcile で強制再書込
7. `FinalFlushForRestore()` → 署名不変でもレイアウトが書かれる+dirty 本文の未退避分が書かれる
8. `Shutdown(keepForRestore: true)` → 自セッション分の Delete が投入されない+DeleteLayout されない
9. `Shutdown(keepForRestore: false)`(既定)→ 現行どおり削除+DeleteLayout 投入
10. `UpdateSettings(..., restoreSessionEnabled: true)` へ切替 → 即 Reconcile でレイアウト書込
11. `CollectForSilentRestore()` → TempDir に置いた session-state.json+backups が返る
    (`enabled=false` でも動く)
12. `AdoptRestored(doc, rec)` → _map 登録(以後の clean 化で Delete(rec.Id) が飛ぶ)+
    TempDir の旧 session dir から CapturedSessionDir へファイル移動

**Step 3: 実装**(構造の要点)

- ctor 末尾に optional 引数追加: `bool restoreSessionEnabled = false, string? sessionLayoutPath = null`。
  フィールド `_sessionRestoreEnabled` / `_layoutPath`(null → `SessionLayoutStore.DefaultPath`)/
  `_lastLayoutSig`(long)/ `_layoutForceWrite`(bool)/ `_layoutWriteFailed`(int・Interlocked)。
- writer 生成+timer start 条件を `_enabled` から `_enabled || _sessionRestoreEnabled` へ変更。
  `CreateWriter` で `w.OnLayoutWriteFailed = () => Interlocked.Exchange(ref _layoutWriteFailed, 1);` を配線。
- `Reconcile()` を再構成:

```csharp
internal void Reconcile()
{
    if (_shutDown || (!_enabled && !_sessionRestoreEnabled))
        return;
    if (_enabled)
        ReconcileContent();   // 現行 Reconcile の本体を private へ切り出し(挙動不変)
    if (_sessionRestoreEnabled)
        ReconcileLayout(force: false);
}

private void ReconcileLayout(bool force)
{
    if (Interlocked.Exchange(ref _layoutWriteFailed, 0) == 1)
        _layoutForceWrite = true;
    var layout = BuildLayout();
    long sig = LayoutSig(layout);
    if (!force && !_layoutForceWrite && sig == _lastLayoutSig)
        return;
    _layoutForceWrite = false;
    _lastLayoutSig = sig;
    _writer?.WriteLayout(_layoutPath, layout);
}

private yEdit.Core.Session.SessionLayout BuildLayout()
{
    var tabs = new List<yEdit.Core.Session.SessionLayoutRecord>();
    var active = _docs.Active;
    foreach (var doc in _docs.Documents)
    {
        string? backupId =
            _map.TryGetValue(doc, out var info) && info.HasBackup ? info.Id : null;
        tabs.Add(new yEdit.Core.Session.SessionLayoutRecord(
            Path: doc.State.Path,
            UntitledNumber: doc.State.Path is null ? doc.State.UntitledNumber : 0,
            BackupId: backupId,
            IsActive: ReferenceEquals(doc, active),
            CaretLine: doc.Editor.CurrentLine,
            CaretColumn: doc.Editor.GetColumn(doc.Editor.CurrentPosition),
            LineEnding: (int)doc.State.LineEnding));
    }
    return new yEdit.Core.Session.SessionLayout(tabs, _clock.GetUtcNow().UtcDateTime);
}

private static long LayoutSig(yEdit.Core.Session.SessionLayout layout)
{
    var sb = new System.Text.StringBuilder();
    foreach (var t in layout.Tabs)
        sb.Append(t.Path).Append('\x1').Append(t.UntitledNumber).Append('\x1')
          .Append(t.BackupId).Append('\x1').Append(t.IsActive ? 1 : 0).Append('\x1')
          .Append(t.CaretLine).Append('\x1').Append(t.CaretColumn).Append('\x1')
          .Append(t.LineEnding).Append('\x2');
    return ContentSignature.Of(sb.ToString()); // SavedAtUtc は署名に含めない(毎 tick 書込を防ぐ)
}
```

- `UpdateSettings(bool enabled, int intervalSeconds, bool restoreSessionEnabled)` へ拡張
  (旧 2 引数オーバーロードは残さない=呼び出し元は MainForm と tests のみ)。
  切替時: いずれか有効なら `_writer ??= CreateWriter(); _timer.Start();`+`Reconcile()`、
  両方無効なら `_timer.Stop()`。restoreSession が OFF→ON なら `_layoutForceWrite = true`。
- `FinalFlushForRestore()`(public・UI スレッド・OnFormClosing から):

```csharp
/// <summary>hot exit 終了時の最終 flush(設計 §3.2)。dirty 本文の未退避分を退避し、
/// レイアウトを署名判定なしで確定書込する。docs が生きている OnFormClosing 中に呼ぶこと。</summary>
public void FinalFlushForRestore()
{
    if (_shutDown)
        return;
    if (_enabled)
        ReconcileContent();
    if (_sessionRestoreEnabled)
        ReconcileLayout(force: true);
}
```

- `Shutdown(bool keepForRestore = false)`: 既存 `Shutdown()` を差し替え。
  `keepForRestore=false` の従来経路に `DeleteLayout` を追加:

```csharp
public void Shutdown(bool keepForRestore = false)
{
    if (_shutDown)
        return;
    _shutDown = true;
    _timer.Stop();
    if (!keepForRestore)
    {
        foreach (var info in _map.Values)
            if (info.HasBackup)
                _writer?.Delete(info.Id);
        // stale レイアウトを残さない(後日 ON に切替えた際の亡霊復元を防ぐ)。
        // writer 未生成(両機能 OFF)でも直接消す=過去 ON セッションの残骸掃除。
        if (_writer is not null)
            _writer.DeleteLayout(_layoutPath);
        else
            yEdit.Core.Session.SessionLayoutStore.Delete(_layoutPath);
    }
    _writer?.Dispose(); // 保留ジョブ(削除/レイアウト含む)をドレイン
    _timer.Dispose();
}
```

- 統合復元 API(OnShown から使用・Task 6 で配線):

```csharp
/// <summary>silent 統合復元の入力収集(設計 §3.3)。sweep → レイアウト Load → LoadAll。
/// バックアップ無効でも動く(レイアウトのみ復元モード=設計 §5.2)。</summary>
public (yEdit.Core.Session.SessionLayout? Layout, IReadOnlyList<BackupRecord> Backups)
    CollectForSilentRestore()
{
    var layout = yEdit.Core.Session.SessionLayoutStore.Load(_layoutPath);
    var backups = LoadAllForRestore(); // OfferRestoreOnStartup から抽出した private(sweep+LoadAll+trace)
    return (layout, backups);
}

/// <summary>復元成功後にレイアウトを消費する(次回は今セッションの新レイアウトが正)。</summary>
public void DeleteConsumedLayout() =>
    yEdit.Core.Session.SessionLayoutStore.Delete(_layoutPath);

/// <summary>復元した文書を元 Id で管理下へ引き取る(設計 §3.4 adopt-move)。
/// _map 登録により以後の clean 化 Delete・クリーン終了削除が正しく効き、ファイル本体は
/// 自セッション dir へ移動して「同一ファイル継続使用」を回復する。移動失敗は trace のみ。</summary>
public void AdoptRestored(Document doc, BackupRecord rec)
{
    _map[doc] = new DocBackup
    {
        Id = rec.Id,
        LastSig = ContentSignature.Of(doc.Editor.SnapshotText),
        HasBackup = true,
    };
    try
    {
        if (!BackupStore.TryMoveToSessionDir(_dir, rec.Id, _sessionDir))
            _trace.Warn("adopt-move-missed", SanitizeForDisplay.OneLine(rec.Id, 200), ex: null);
    }
    catch (Exception ex)
    {
        _trace.Warn("adopt-move", SanitizeForDisplay.OneLine(rec.Id, 200), ex);
    }
}
```

- `OfferRestoreOnStartup` 冒頭の sweep+LoadAll ブロックを `LoadAllForRestore()` へ抽出
  (挙動不変・`_enabled` ガードは OfferRestoreOnStartup 側に残す)。

**Step 4: 緑確認 → Step 5: Commit**

```
feat(app): BackupCoordinator にレイアウト定期退避と hot exit 用 API を追加(§3.1-§3.3)
```

**Step 6: 仕様レビュー+★品質レビュー**

---

## Task 4: OfferRestoreOnStartup の adopt-move(潜在バグ根治)

**Files:**
- Modify: `src/yEdit.App/BackupCoordinator.cs`(`OfferRestoreOnStartup` 内 2 箇所)
- Test: `tests/yEdit.App.Tests/BackupCoordinatorTests.cs`

**Step 1: 失敗するテストを書く**

1. **回帰(現行バグ)**: TempDir\session-old\<id>.json を置く → confirm=false で復元 →
   文書 clean 化 → Reconcile → FakeWriter.Deletes に id が入り、**TempDir 配下のどこにも
   <id>.json が存在しない**(=次回 LoadAll が空・再提案されない)
2. confirm=false 復元 → 旧 dir から CapturedSessionDir へ移動済み・旧 session dir は消滅
3. ダイアログ経路(FakeRestorePrompt が Restore+Checked)でも同様に移動する
4. 移動先未作成でも成功する(session dir は初回書込前は存在しない)

**Step 2: 失敗確認 → Step 3: 実装**

`OfferRestoreOnStartup` の confirm=false ループと `RestoreAction.Restore` ループの
`_map[doc] = new DocBackup {...}` を `AdoptRestored(doc, rec);` に差し替える(2 箇所・
既存の per-record try/catch はそのまま)。

**Step 4: 緑確認 → Step 5: Commit**

```
fix(app): 復元したバックアップを自セッション dir へ adopt-move する(BK-M-2 再提案バグ根治)
```

**Step 6: 仕様レビュー+★脆弱性レビュー**

---

## Task 5: `FileController.RestoreSession` 統合復元+`LegacySessionConverter`

**Files:**
- Create: `src/yEdit.Core/Session/LegacySessionConverter.cs`
- Modify: `src/yEdit.App/FileController.cs`(`RestoreLastSession` 群の後に追加。旧メソッドは
  Task 7 まで残置=既存テストを緑のまま保つ)
- Test: `tests/yEdit.Core.Tests/Session/LegacySessionConverterTests.cs`
- Test: `tests/yEdit.App.Tests/FileControllerTests.cs`(追記)

**Step 1: LegacySessionConverter のテスト → 実装**(Core 純関数)

```csharp
using yEdit.Core.Backup;

namespace yEdit.Core.Session;

/// <summary>
/// PR #22 形式(LastSessionSnapshot + buffers)を統合復元の入力へ一回限り変換する(設計 §8/§10)。
/// BufferKey(Guid.N)を合成 BackupRecord の Id に流用する。合成レコードは in-memory のみで
/// ディスクへ書かない(復元後の Reconcile が新セッション dir へ通常書込して保護する)。
/// 旧「無題かつ BufferKey なし」(cap 超過の枠だけ保存)は skip する=PR #22 E4 の意味論を保存
/// (新形式の「無題 BackupId なし=空枠」とは意味が異なる)。
/// </summary>
public static class LegacySessionConverter
{
    public static (SessionLayout Layout, List<BackupRecord> Backups) Convert(
        LastSessionSnapshot snap,
        IReadOnlyDictionary<string, string> buffers,
        DateTime nowUtc)
    {
        var tabs = new List<SessionLayoutRecord>();
        var backups = new List<BackupRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in snap.Tabs)
        {
            if (t is null)
                continue;
            string? backupId = null;
            if (
                t.BufferKey is not null
                && BackupIdValidator.IsValid(t.BufferKey)
                && seen.Add(t.BufferKey)
                && buffers.TryGetValue(t.BufferKey, out var content)
            )
            {
                backupId = t.BufferKey;
                backups.Add(new BackupRecord(
                    Id: t.BufferKey,
                    OriginalPath: t.Path,
                    UntitledNumber: t.UntitledNumber,
                    CodePage: t.CodePage,
                    HasBom: t.HasBom,
                    LineEndingId: t.LineEnding,
                    Content: content,
                    TimestampUtc: nowUtc));
            }
            if (t.Path is null && backupId is null)
                continue; // 旧形式: 本文の無い無題は skip(E4 意味論の保存)
            tabs.Add(new SessionLayoutRecord(
                t.Path, t.UntitledNumber, backupId, t.IsActive,
                t.CaretLine, t.CaretColumn, t.LineEnding));
        }
        return (SessionLayoutStore.Normalize(new SessionLayout(tabs, nowUtc)), backups);
    }
}
```

テスト: dirty パスあり/非 dirty パスあり/無題(content あり)/無題(BufferKey null → skip)/
BufferKey が buffers に欠落 → BackupId null(パスありは disk 再オープンに demote される形)/
不正 BufferKey → 合成されない/WasModified は変換で使われない(統合後 Modified=true 固定)。

**Step 2: RestoreSession のテストを書く**(設計 §6.2 の統合復元マトリクス全件)

既存 `FileControllerTests` の fixture(FakePrompt / TempDir / harness)を踏襲。
`adoptRestored` は記録用の `List<(Document, BackupRecord)>` に積むテスト delegate を渡す。

**Step 3: RestoreSession 実装**(FileController・`RestoreFromBackup` の直後に配置)

```csharp
/// <summary>
/// hot exit 統合復元(設計 2026-07-23 統合 §3.3)。レイアウトのタブ順に復元し、レイアウト外の
/// バックアップ(extras)を追加復元する。silent 経路=ダイアログは一切出さない(失敗パスは
/// 集約用に返す)。per-record try/catch で「一つの悪いレコードが他を壊さない」不変を維持。
/// adoptRestored: バックアップ由来の復元文書を Coordinator 管理下へ引き取る callback
/// (silent 経路=BackupCoordinator.AdoptRestored。レガシー移行の合成レコードでは null=
/// 通常の RegisterNew 経路で次 Reconcile が保護する)。
/// </summary>
public IReadOnlyList<string> RestoreSession(
    yEdit.Core.Session.SessionLayout? layout,
    IReadOnlyList<BackupRecord> backups,
    Document? initialEmpty,
    Action<Document, BackupRecord>? adoptRestored)
{
    var failedPaths = new List<string>();
    Document? activeDoc = null;
    int openedCount = 0;

    // Id → record(重複 Id は新しい方を採用=別 session dir の stale と競合した場合)
    var byId = new Dictionary<string, BackupRecord>(StringComparer.Ordinal);
    foreach (var b in backups)
        if (!byId.TryGetValue(b.Id, out var prev) || b.TimestampUtc > prev.TimestampUtc)
            byId[b.Id] = b;
    var consumed = new HashSet<string>(StringComparer.Ordinal);

    WithLoadErrorPromptSuppressed(() =>
    {
        if (layout is not null)
        {
            foreach (var rec in layout.Tabs)
            {
                try
                {
                    var doc = RestoreLayoutRecord(rec, byId, consumed, failedPaths, adoptRestored);
                    if (doc is null)
                        continue;
                    openedCount++;
                    if (rec.IsActive)
                        activeDoc = doc;
                }
                catch (Exception ex)
                {
                    if (rec.Path is not null)
                        failedPaths.Add(rec.Path);
                    System.Diagnostics.Trace.TraceWarning(
                        "yEdit: restore-record-failed: {0}",
                        yEdit.Core.Text.SanitizeForDisplay.OneLine(ex.Message, 200));
                }
            }
        }
        // extras: レイアウト外バックアップ=クラッシュ直前に開いたタブ・他インスタンス遺物・
        // 旧「あとで」孤児。安全側=拾って開く(設計 §3.3 手順 4)。
        foreach (
            var bk in byId.Values
                .Where(b => !consumed.Contains(b.Id))
                .OrderByDescending(b => b.TimestampUtc)
        )
        {
            try
            {
                var doc = RestoreExtraBackup(bk, failedPaths, adoptRestored);
                if (doc is not null)
                    openedCount++;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "yEdit: restore-extra-failed: {0}",
                    yEdit.Core.Text.SanitizeForDisplay.OneLine(ex.Message, 200));
            }
        }
    });

    if (activeDoc is not null)
        _docs.Activate(activeDoc);
    if (openedCount > 0 && initialEmpty is not null)
        _docs.TryClose(initialEmpty, _ => true); // 空無題タブは無条件破棄
    _metaChanged();
    return failedPaths;
}

private Document? RestoreLayoutRecord(
    yEdit.Core.Session.SessionLayoutRecord rec,
    Dictionary<string, BackupRecord> byId,
    HashSet<string> consumed,
    List<string> failedPaths,
    Action<Document, BackupRecord>? adoptRestored)
{
    BackupRecord? bk =
        rec.BackupId is not null && byId.TryGetValue(rec.BackupId, out var found) ? found : null;
    bool demotedPathOnly = false;
    if (bk is not null && bk.Content is null)
    {
        // E11: path-only(>32M)を silent で「空 dirty+実パス」に載せない(Ctrl+S 切り詰め事故の遮断)。
        // consumed に積んで extras での空 dirty 復活も防ぐ。
        System.Diagnostics.Trace.TraceWarning(
            "yEdit: restore-path-only-demote: {0}",
            yEdit.Core.Text.SanitizeForDisplay.OneLine(rec.Path ?? bk.Id, 200));
        consumed.Add(bk.Id);
        bk = null;
        demotedPathOnly = true;
    }

    if (rec.Path is not null)
    {
        if (bk is not null)
        {
            var doc = RestoreDirtyFromBackup(rec, bk);
            consumed.Add(bk.Id);
            adoptRestored?.Invoke(doc, bk);
            return doc;
        }
        if (rec.BackupId is not null && !demotedPathOnly)
            // E9': dirty 参照はあるがバックアップ欠落=編集は失われている → disk 再オープンへ demote
            System.Diagnostics.Trace.TraceWarning(
                "yEdit: dirty-backup-missing, demoting to disk reopen: {0}",
                yEdit.Core.Text.SanitizeForDisplay.OneLine(rec.Path, 200));
        var opened = TryOpenOrActivate(rec.Path);
        if (opened is null)
        {
            failedPaths.Add(rec.Path);
            return null;
        }
        opened.Editor.SetCaretByLineColumn(rec.CaretLine, rec.CaretColumn);
        return opened;
    }

    // 無題
    if (bk is not null)
    {
        var doc = RestoreUntitledFromBackup(rec, bk);
        consumed.Add(bk.Id);
        adoptRestored?.Invoke(doc, bk);
        return doc;
    }
    if (rec.BackupId is not null)
    {
        // E4': 無題の編集内容が欠落 → 空タブを作らず skip(誤解を招く空枠を出さない)
        System.Diagnostics.Trace.TraceWarning(
            "yEdit: untitled-backup-missing, skipping record (untitled-{0})", rec.UntitledNumber);
        return null;
    }
    return RestoreUntitledFrame(rec); // BackupId=null=「空だった無題タブ」の枠を復元
}

/// <summary>dirty パスあり復元(E12): パスは rec.Path 側を検証して採用し、bk.OriginalPath は
/// 信用しない。検証 NG は無題フォールバック(HIGH-2 踏襲・silent 経路のため Warn は出さず trace)。
/// 本文・エンコーディング・改行は BackupRecord から(SafeEncodingOrFallback / E10 と同じ防御)。</summary>
private Document RestoreDirtyFromBackup(
    yEdit.Core.Session.SessionLayoutRecord rec, BackupRecord bk)
{
    var doc = _docs.CreateNew();
    var status = OriginalPathValidator.Check(rec.Path!, out var normalized);
    if (status == PathValidation.Ok)
    {
        doc.State.Path = normalized;
        doc.State.UntitledNumber = 0;
    }
    else
    {
        doc.State.Path = null;
        doc.State.UntitledNumber = ++_untitledSeq;
        System.Diagnostics.Trace.TraceWarning(
            "yEdit: restore-invalid-path-fallback-to-untitled: {0}",
            yEdit.Core.Text.SanitizeForDisplay.OneLine(rec.Path, 200));
    }
    doc.State.Encoding = SafeEncodingOrFallback(bk.CodePage);
    doc.State.HasBom = bk.HasBom;
    doc.State.LineEnding = SafeLineEndingOrFallback(bk.LineEndingId);
    doc.Editor.SetOrReplaceSource(TextBuffer.FromString(bk.Content ?? string.Empty));
    ApplyEol(doc);
    doc.Editor.EmptyUndoBuffer();
    doc.Editor.ClearSavePoint(); // Modified=true(RestoreFromBackup と同パターン)
    doc.Editor.SetCaretByLineColumn(rec.CaretLine, rec.CaretColumn);
    DocumentManager.UpdateLabel(doc);
    return doc;
}

/// <summary>無題 dirty 復元。統合後は WasModified を持たず常に Modified=true で復元する
/// (無題の本文=バックアップ存在=dirty 相当。設計 §10)。</summary>
private Document RestoreUntitledFromBackup(
    yEdit.Core.Session.SessionLayoutRecord rec, BackupRecord bk)
{
    var s = _settings();
    var doc = _docs.CreateNew();
    doc.State.Path = null;
    doc.State.UntitledNumber = rec.UntitledNumber > 0 ? rec.UntitledNumber : ++_untitledSeq;
    if (rec.UntitledNumber > _untitledSeq)
        _untitledSeq = rec.UntitledNumber;
    doc.State.Encoding = EncodingCatalog.Get(s.DefaultCodePage);
    doc.State.HasBom = false;
    doc.State.LineEnding = SafeLineEndingOrFallback(bk.LineEndingId);
    doc.Editor.SetOrReplaceSource(TextBuffer.FromString(bk.Content ?? string.Empty));
    ApplyEol(doc);
    doc.Editor.EmptyUndoBuffer();
    doc.Editor.ClearSavePoint();
    doc.Editor.SetCaretByLineColumn(rec.CaretLine, rec.CaretColumn);
    DocumentManager.UpdateLabel(doc);
    return doc;
}

/// <summary>空の無題タブの枠を復元する(BackupId=null=終了時に空だったタブ。設計 §2.1)。</summary>
private Document RestoreUntitledFrame(yEdit.Core.Session.SessionLayoutRecord rec)
{
    var s = _settings();
    var doc = _docs.CreateNew();
    doc.State.Path = null;
    doc.State.UntitledNumber = rec.UntitledNumber > 0 ? rec.UntitledNumber : ++_untitledSeq;
    if (rec.UntitledNumber > _untitledSeq)
        _untitledSeq = rec.UntitledNumber;
    doc.State.Encoding = EncodingCatalog.Get(s.DefaultCodePage);
    doc.State.HasBom = false;
    doc.State.LineEnding = SafeLineEndingOrFallback(rec.LineEnding);
    DocumentManager.UpdateLabel(doc);
    return doc; // fresh バッファ=Modified=false・本文なし
}

/// <summary>extras(レイアウト外バックアップ)の復元。Content=null(path-only)は
/// E11 と同方針で disk 再オープン(パス正当時のみ)・無題 path-only は skip。</summary>
private Document? RestoreExtraBackup(
    BackupRecord bk, List<string> failedPaths, Action<Document, BackupRecord>? adoptRestored)
{
    if (bk.Content is null)
    {
        if (bk.OriginalPath is null)
        {
            System.Diagnostics.Trace.TraceWarning(
                "yEdit: extra-path-only-untitled-skipped: {0}",
                yEdit.Core.Text.SanitizeForDisplay.OneLine(bk.Id, 200));
            return null;
        }
        if (OriginalPathValidator.Check(bk.OriginalPath, out var normalized) != PathValidation.Ok)
        {
            System.Diagnostics.Trace.TraceWarning(
                "yEdit: extra-path-only-invalid-path-skipped: {0}",
                yEdit.Core.Text.SanitizeForDisplay.OneLine(bk.OriginalPath, 200));
            return null;
        }
        var opened = TryOpenOrActivate(normalized);
        if (opened is null)
        {
            failedPaths.Add(bk.OriginalPath);
            return null;
        }
        return opened;
    }
    var doc = RestoreFromBackup(bk); // 既存経路(HIGH-2 検証・dirty 復元・無題連番)
    adoptRestored?.Invoke(doc, bk);
    return doc;
}
```

追加修正: `RestoreFromBackup` 内の invalid-path `_prompt.Warn` を
`if (!_suppressLoadErrorPrompt)` でガードする(silent 経路=WithLoadErrorPromptSuppressed 内では
ダイアログを出さない。ダイアログ経路=OfferRestoreOnStartup からの呼出は非抑止スコープ=挙動不変)。

**Step 4: 緑確認(既存テスト含む全緑)→ Step 5: Commit**

```
feat(app): FileController.RestoreSession 統合復元と LegacySessionConverter を追加(§3.3/§8)
```

**Step 6: 仕様レビュー+★脆弱性レビュー**

---

## Task 6: MainForm 配線(OnShown / OnFormClosing / OnFormClosed / OpenSettings)

**Files:**
- Modify: `src/yEdit.App/MainForm.cs`
- Test: `tests/yEdit.App.Tests/MainFormSmokeTests.cs`(追記。既存の削除は Task 7)

**Step 1: 失敗するテストを書く**(設計 §6.2)

MainForm internal ctor に `string? backupDirectory = null, string? sessionLayoutPath = null` を
追加する前提で ShowMainForm ヘルパを拡張(TempDir 隔離)。

1. **OnShown 分岐**: ON → silent 復元発火(TempDir の session-state.json+backups からタブ再現・
   ダイアログなし)/OFF → 従来の OfferRestoreOnStartup 経路(既存テスト維持)
2. **統合復元 e2e**: layout(パスあり clean+無題 dirty+アクティブ指定)+backups →
   タブ順・本文・Modified・アクティブ・caret・initialEmpty クローズ
3. **layout null+backups あり(=クラッシュ・レイアウト喪失 E5')** → extras として復元される
4. **復元後**: session-state.json が削除される・`LastSession=null`・buffers.json 削除
5. **レガシー移行**: session-state.json なし+`LastSession` あり+buffers.json あり →
   復元される+旧残骸削除(パスあり dirty / 無題 / 非 dirty の 3 形)
6. **OnFormClosing**: ON×BackupON+dirty → 確認なし(silent seam=true)+
   FinalFlushForRestore でレイアウト書込/ON×BackupOFF+dirty → 確認あり/
   ON×BackupON+**32M 超 dirty**(`maxBackupCharsOverride` 相当の seam は使えないため
   `BackupCoordinator.MaxBackupChars` を跨ぐ TextLength を持つ fake は非現実的 →
   `HasOversizedDirtyDoc` を internal seam 化して直接検証)/OFF → 従来確認(既存テスト維持)
7. **OnFormClosed**: ON → `Shutdown(keepForRestore: true)`(バックアップ・レイアウト残存)/
   OFF → 削除(既存挙動)

**Step 2: 失敗確認 → Step 3: 実装**

- internal ctor 拡張+`_backup` 生成に `directory: backupDirectory,
  restoreSessionEnabled: settings.RestoreOpenFilesOnStartup, sessionLayoutPath: sessionLayoutPath`
  を渡す。
- `OpenSettings`: `_backup.UpdateSettings(_settings.BackupEnabled, _settings.BackupIntervalSeconds,
  _settings.RestoreOpenFilesOnStartup);`
- `OnShown` 差し替え:

```csharp
if (_restoreOffered)
    return;
_restoreOffered = true;

if (_settings.RestoreOpenFilesOnStartup)
{
    // hot exit 統合復元(設計 §3.3): クラッシュ/正常終了を区別せず silent 復元。
    RestoreUnifiedSession();
    return;
}

// OFF: 従来どおり異常終了バックアップの復元提案のみ。
int restored;
if (_restoredCountOverrideForTest is int overrideValue)
    restored = overrideValue;
else
{
    restored = _backup.OfferRestoreOnStartup(
        this, _file.RestoreFromBackup, _settings.ConfirmRestoreOnStartup);
    if (restored > 0)
        _announcer.Say($"バックアップを {restored} 件復元しました");
}
```

- `RestoreUnifiedSession`(新設・`TryRestoreLastSession` を置換):

```csharp
/// <summary>hot exit 統合復元(設計 §3.3/§8)。レイアウト+バックアップを silent 復元し、
/// レガシー(PR #22)形式が残っていれば一回限り読み替える。失敗パスは集約 Warn 1 個。
/// 想定外例外は通常起動へフォールバック(E8)。</summary>
private void RestoreUnifiedSession()
{
    try
    {
        var (layout, backups) = _backup.CollectForSilentRestore();
        IReadOnlyList<BackupRecord> allBackups = backups;
        if (layout is null && _settings.LastSession is { Tabs.Count: > 0 } legacy)
        {
            // レガシー移行(設計 §8): 旧形式を統合復元の入力へ一回限り変換。
            var buffers = yEdit.Core.Session.LastSessionBuffersStore.Load(LastSessionBuffersPath);
            var (converted, synthetic) =
                yEdit.Core.Session.LegacySessionConverter.Convert(legacy, buffers, DateTime.UtcNow);
            layout = converted;
            if (synthetic.Count > 0)
            {
                var merged = new List<BackupRecord>(backups.Count + synthetic.Count);
                merged.AddRange(backups);
                merged.AddRange(synthetic);
                allBackups = merged;
            }
        }
        var failed = _file.RestoreSession(layout, allBackups, _startupEmptyDoc, _backup.AdoptRestored);
        _backup.DeleteConsumedLayout();
        _settings.LastSession = null; // レガシー残骸の掃除(次回 Save で消える)
        yEdit.Core.Session.LastSessionBuffersStore.Delete(LastSessionBuffersPath);
        if (failed.Count > 0 && !_suppressFailedRestoreDialogForTest)
            ShowFailedRestoreDialog(failed);
    }
    catch (Exception ex)
    {
        System.Diagnostics.Trace.TraceWarning(
            "yEdit: unified-restore failed: {0}",
            yEdit.Core.Text.SanitizeForDisplay.OneLine(ex.Message, 200));
    }
}
```

- `OnFormClosing` 差し替え(cap 系・BuildLastSessionSnapshot・discardedDocs を全廃):

```csharp
protected override void OnFormClosing(FormClosingEventArgs e)
{
    _grep.BeginClose();

    // hot exit(設計 §3.2/§10): ON かつ内容の定期退避が生きている(BackupEnabled)かつ
    // 全 dirty がバックアップ可能(≤32M chars)なら、未保存確認なしで閉じる。
    // BackupEnabled=false は「内容を永続化しない」ユーザー意思の尊重、32M 超は path-only
    // バックアップ(内容なし)による無断喪失の防止=いずれも従来の確認経路へ fall-through。
    bool silentPath =
        _settings.RestoreOpenFilesOnStartup && _settings.BackupEnabled && !HasOversizedDirtyDoc();
    _lastCloseTookSilentPathForTest = silentPath;

    if (!silentPath)
    {
        foreach (var doc in _docs.Documents.ToArray())
        {
            if (!doc.Editor.Modified)
                continue;
            _docs.Activate(doc); // どのファイルの確認かを SR/視覚で示す
            bool keepClosing = _confirmDiscardOverrideForTest is not null
                ? _confirmDiscardOverrideForTest(doc)
                : _file.ConfirmDiscardIfDirty(doc);
            if (!keepClosing)
            {
                e.Cancel = true;
                _grep.CancelClose();
                base.OnFormClosing(e);
                return;
            }
        }
    }

    var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
    _settings.WindowWidth = b.Width;
    _settings.WindowHeight = b.Height;

    // ON: docs が生きているうちに最終 flush(本文+レイアウト)。OFF の stale layout 掃除は
    // OnFormClosed の Shutdown(keepForRestore:false) が担う。
    if (_settings.RestoreOpenFilesOnStartup)
        _backup.FinalFlushForRestore();

    _settings.LastSession = null; // 統合後は旧形式を書かない
    SaveSettingsSafe();
    base.OnFormClosing(e);
}

/// <summary>設計 §10: BK-M-3 の 32M cap を超える dirty 文書があるか(path-only バックアップは
/// 内容を持たないため silent close 不可=確認経路へ fall-through する判定)。O(docs) の
/// TextLength 参照のみで全文コピーはしない。</summary>
private bool HasOversizedDirtyDoc()
{
    foreach (var doc in _docs.Documents)
        if (doc.Editor.Modified && doc.Editor.TextLength > BackupCoordinator.MaxBackupChars)
            return true;
    return false;
}

internal bool HasOversizedDirtyDocForTest() => HasOversizedDirtyDoc();
```

- `OnFormClosed`: `_backup.Shutdown(keepForRestore: _settings.RestoreOpenFilesOnStartup);`

**Step 4: 緑確認(この時点で旧経路テストの一部が赤になる場合、赤の削除・移植は Task 7 で行う
ため、本タスクでは旧経路を壊さない=旧メソッド残置・呼び出しのみ切替。赤が出たら実装を見直す)**

注: `SetRestoredCountOverrideForTest` を使う既存テストのうち「ON+restored>0 → スキップ」系は
仕様ごと消えるため Task 7 で削除する。本タスク終了時は
`dotnet test tests/yEdit.App.Tests` で新テスト+既存 OFF 系が緑であること
(ON 系旧テストの赤は Task 7 冒頭で仕様変更として削除することを commit メッセージに明記)。

**Step 5: Commit**

```
feat(app): MainForm を hot exit 統合へ配線(silent close / 統合復元 / レガシー移行)
```

**Step 6: 仕様レビュー**

---

## Task 7: 旧機構の退役+テスト移植

**Files:**
- Modify: `src/yEdit.App/FileController.cs` — `RestoreLastSession` / `RestorePathDirty` /
  `RestoreUntitledTab` を削除(呼び出し元ゼロ確認後)
- Modify: `src/yEdit.App/MainForm.cs` — `MaxSessionUntitledContentChars` /
  `MaxSessionTotalUntitledChars` / `NeedsContentSave` / `BuildLastSessionSnapshot`(+ForTest)/
  `WillDirtyContentFitInCaps`(+ForTest)/ `SaveLastSessionBuffersSafe` /
  `DeleteLastSessionBuffersSafe` / `TryRestoreLastSession` を削除。
  `SetLastSessionBuffersPathForTest` と `_suppressFailedRestoreDialogForTest` は残す(移行テスト用)
- Modify: `src/yEdit.Core/Session/LastSessionBuffersStore.cs` — `Save` を削除し
  xmldoc を「レガシー移行の Load/Delete 専用(次リリースで完全削除)」へ更新
- Modify: `src/yEdit.Core/Settings/AppSettings.cs` — `LastSession` xmldoc を
  「旧形式・移行読取専用(設計 2026-07-23 統合 §8。次リリースで削除)」へ更新
- Test: `tests/yEdit.Core.Tests/Session/LastSessionBuffersStoreTests.cs` — Save 系テスト削除
- Test: `tests/yEdit.App.Tests/MainFormSmokeTests.cs` / `FileControllerTests.cs` —
  cap / fall-through / No-discard / BuildLastSessionSnapshot / 旧 RestoreLastSession 系を削除。
  復元セマンティクスとして価値の残るケース(caret clamp・RecentFiles 非汚染・failedPaths 集約等)は
  Task 5/6 の新テストでカバー済みであることを突き合わせ、漏れがあれば新経路へ移植

**Steps:** 削除 → `dotnet build -warnaserror` で参照ゼロ確認 → 全テスト緑 →
`SettingsStore.NormalizeLastSession` は**残す**(レガシー deserialize の防御は移行が生きている限り必要)。

**Commit:**

```
refactor(app,core): PR #22 の buffers/cap 機構を退役(hot exit 統合 §0)
```

**レビュー: 仕様レビュー**(「消しすぎ/消し漏れ」の確認を明示依頼)

---

## Task 8: 説明書・docs 同期

**Files:**
- Modify: `説明書/yEdit説明書.md`(たたき台として。ユーザー校閲前提=CLAUDE.md §8)
  - 基本タブ表の「起動時に前回開いていたファイルを開く」説明を設計 §5.3 の文面へ
  - バックアップ節の優先順位段落を設計 §5.3 の文面へ
  - PR #22 §8.6 の cap 超過文(「100 万文字を超える場合は…」)を削除し、
    32M 超で確認が出る旨へ差し替え
- Modify: `tests/README.md` — buffers/cap 系 seam の記述があれば統合後の姿へ同期
- 確認: README.md のアーキテクチャ節に session-state.json / hot exit の 1 行追記が必要か判断

**Commit:**

```
docs: hot exit 統合の説明書・テスト文書同期
```

---

## 最終工程(CLAUDE.md §3-5〜§7)

1. **最終ブランチレビュー(2 パス・別エージェント各 1 起動)**:
   - コード品質パス(§6.4 のミューテーション検証スポットチェック込み:
     Shutdown keep ピボット / E11 demote 条件 / adopt-move Id 検証)
   - 脆弱性パス(session-state.json パース・パス検証・adopt-move・E11/E12)
   - 指摘は fixup commit で反映(3 択明示: fixup / PR 記載受容 / 理由付き却下)
2. `pwsh tools/pre-merge-check.ps1` → **EXIT 0** 確認
3. push → PR 作成(日本語 description: 目的・レビュー経緯・申し送り・手動テスト計画)
4. 手動テスト計画(PR に記載):
   - ON: 複数タブ(dirty 含む)→ ×ボタン → 確認なし終了 → 再起動で完全復元(タブ順・*・caret)
   - ON: タスクマネージャで強制終了 → 再起動で同じく silent 復元(ダイアログなし)
   - ON→OFF 切替後の終了 → 次回起動で復元されない(stale layout 掃除)
   - OFF: 従来どおり(dirty 確認+クラッシュ時のみ復元提案)
   - BackupEnabled=OFF×ON: 終了時確認あり・ファイルタブのみ復元
   - PR #22 版の settings.json+buffers.json を残した状態で新版起動 → 移行復元+残骸掃除
   - L5(推奨・非必須): NVDA で ON 終了→起動の読み(タブ名・caret 位置・「*」)
5. マージはユーザー判断(PR レビュー後)
