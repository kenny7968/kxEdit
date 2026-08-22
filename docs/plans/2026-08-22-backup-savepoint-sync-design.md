# 保存点・破棄の即時反映と復元時の陳腐化検出(A-1 / M-31)設計書

- 日付: 2026-08-22
- 対象: `docs/plans/2026-08-22-v0.2-release-bug-audit.md` の **A-1**(優先度 1)と **M-31**(§6)
- ブランチ: `feature/backup-savepoint-sync`
- ベース: main `ffd9a05`
- 本書は**策定時スナップショット**(CLAUDE.md §8)。実装時の精密化と実施記録のみ追記する。

## 1. 目的

保存直後・破棄直後の**クラッシュ窓**で、バックアップとディスクの整合が壊れる 2 件を根治する。

| ID | 症状 | 害 |
|----|------|-----|
| A-1 | 保存成功 → 次 tick(既定 300 秒)までにクラッシュ → 再起動で**保存前の本文**が `* file.txt` として dirty 復元 → ユーザーが Ctrl+S → **保存済みの新内容が無警告で消える** | データ喪失 |
| M-31 | Ctrl+W の未保存確認で「いいえ」→ 次 tick までにクラッシュ → **破棄したタブが復活** | 破棄意図の無視 |

共通の構造欠陥は「**保存点到達とタブクローズが `BackupCoordinator` に即時通知されない**」こと。
`Reconcile` の契機が Timer と `ActiveDocumentChanged` の 2 つしかない(`BackupCoordinator.cs:123-124`)。

A-1 はこれに加えて「**復元側がディスクの更新時刻を見ていない**」という第 2 の欠陥を持つ。
背景ライター(`SerialBackupWriter`)は非同期直列キューなので、即時削除を投入しても
**削除がディスクに届く前にクラッシュする残余窓**が原理的に残る。二層で塞ぐ。

## 2. 非目標(YAGNI)

- 監査 §8 が同テーマとした A-4 / A-7 / A-8 / A-19(`FileController` の保存経路)は**含めない**。別ブランチ。
- M-18(外部プロセスによる変更検知・保存時の mtime 比較)は**含めない**。本書は**復元時**のみ扱う。
- バックアップ間隔の契約(既定 300 秒)は変えない。**dirty 化での即時書込は行わない**(§3.2)。

## 3. 設計 (a): 保存点・破棄の即時反映

### 3.1 契機 — `DocumentManager` の既存配線から新イベントを出す

`DocumentManager.CreateNew` は既に全 Document の `SavePointLeft` / `SavePointReached` を
`OnDirtyChanged(doc)` に集約している(`DocumentManager.cs:76-77`)。ここに**任意の文書**を伝える
イベントを 1 本足す(既存 `ActiveDirtyChanged` はアクティブ分のみ=非アクティブタブの保存を拾えない)。

```csharp
public event EventHandler<Document>? DocumentDirtyChanged;   // 新設(任意の文書)

private void OnDirtyChanged(Document doc)
{
    UpdateLabel(doc);
    if (ReferenceEquals(doc, Active))
        ActiveDirtyChanged?.Invoke(this, EventArgs.Empty);
    DocumentDirtyChanged?.Invoke(this, doc);                 // 追加
}
```

M-31 側は**新設不要**。`DocumentManager.DocumentClosed`(`:62`)が既に「閉じた」の唯一の通知源として
存在し、`TryClose` から発火する(`:130`)。`BackupCoordinator` が購読するだけでよい。

`BackupCoordinator` ctor で 2 本購読する:

```csharp
_docs.DocumentDirtyChanged += (_, doc) => OnDocumentSavePointOrClose(clean: !doc.Editor.Modified);
_docs.DocumentClosed       += (_, _)   => OnDocumentSavePointOrClose(clean: true);
```

**dirty 化(clean=false)では何もしない**。ここを対称にすると 1 打鍵目ごとにバックアップを書き、
ユーザーが設定した間隔の契約を変えてしまう(かつ M-21 の全文 string 化を高頻度で誘発する)。
即時反映が要るのは「**ディスクと一致した/文書が消えた**=バックアップが不要になった」側だけである。

### 3.2 走らせる処理 — `ReconcileMapMaintenance` + `ReconcileLayout(force:false)`

full `Reconcile()` は呼ばない。必要なのは

1. clean 化した文書のバックアップ削除
2. 閉じた文書のバックアップ削除と `_map` からの除去
3. レイアウト(`session-state.json`)の更新

だけで、1+2 は既存 `ReconcileMapMaintenance()` の意味論そのものである
(layout-only モード用に書かれた関数だが、要求が完全に一致する)。

full `Reconcile()` を避ける理由は性能。`ReconcileContent` は他の dirty タブに対して
`doc.Editor.SnapshotText`(全文 string 化)を走らせる。これを Ctrl+S ごとに呼ぶと、
巨大な dirty タブが同居しているときに**保存の応答時間が悪化**する(M-21 の増幅)。
`ReconcileMapMaintenance` は `SnapshotText` を一切呼ばない。

```csharp
private void OnDocumentSavePointOrClose(bool clean)
{
    if (!clean)
        return;
    if (_shutDown || !_startupRestoreDone || (!_enabled && !_sessionRestoreEnabled))
        return;
    ReconcileMapMaintenance();
    if (_sessionRestoreEnabled)
        ReconcileLayout(force: false);
}
```

**確認済みの副作用**: `ReconcileMapMaintenance` は `info.ForceWrite` を落とさない
(`ReconcileContent` の Delete 分岐は落とす)。`BackupPlanner.Decide` は
`modified=false` なら `hasBackup` のみで判定し `forceWrite` を見ないため、
残留した `ForceWrite=true` は無害(次に dirty 化したとき 1 回余分に書くだけ=安全側)。

### 3.3 起動時ゲート `_startupRestoreDone`(必須)

`MainForm` ctor は `_backup` 生成の**後**に `_file.NewFile()` を呼ぶ。ここで
`SetSavePoint()`(`EditorControl.cs:1248-1255`)が**無条件に** `SavePointReached` を発火する。
新イベントは C# の直接呼び出しなので、ゲートが無ければ

> 起動 → 空無題 1 タブのレイアウトを `session-state.json` へ書込 → `OnShown` が
> `CollectForSilentRestore()` でそれを読む → **前回セッションが復元されない**

という退行になる。既存の `ActiveDocumentChanged → Reconcile` が同じ事故を起こしていないのは、
ctor 時点で `TabControl` のハンドルが未生成で WinForms の `Selected` が発火しないため
(=偶然に守られている)。新経路は明示的にゲートする。

- `BackupCoordinator` に `private bool _startupRestoreDone;` と
  `public void MarkStartupRestoreComplete() => _startupRestoreDone = true;` を足す。
- `MainForm.OnShown` の**復元処理の直後**(ON 経路の `RestoreUnifiedSession()` 後、
  OFF 経路の `OfferRestoreOnStartup` 後)で呼ぶ。
- ゲートは**新経路のみ**に掛ける。Timer と `ActiveDocumentChanged` の既存挙動は不変。

ゲート閉鎖中の取りこぼしは無い: `_map` は Reconcile が走るまで空、復元タブは
`AdoptRestored` で `Modified=true` 登録されるため `ReconcileMapMaintenance` の削除条件に当たらない。

### 3.4 終了経路との関係(確認済み・変更不要)

- `OnFormClosing` はタブごとの `TryClose` を**呼ばない**(Form の破棄でタブが消える)ため
  `DocumentClosed` は飛ばない。`FinalFlushForRestore` と競合しない。
- 非 silent 経路の確認ループで「はい(保存)」を選ぶと `SetSavePoint` 経由で新経路が動くが、
  その後の `FinalFlushForRestore(force:true)` が最終レイアウトを上書きする(直列ライターは順序保存)。
- `Shutdown()` 後は既存の `_shutDown` ガードで無反応。

## 4. 設計 (b): 復元時の陳腐化検出

### 4.1 判定 — Core の純粋関数

```csharp
// src/kxEdit.Core/Backup/BackupStaleness.cs
public static class BackupStaleness
{
    /// FAT の 2 秒粒度と NTP 微調整を吸収する既定許容。
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(2);

    /// ディスク側がバックアップ取得時刻より新しい(=バックアップが陳腐化している疑い)か。
    /// diskLastWriteUtc が null(取得不能・ファイル無し)なら false(判定しない=従来どおり復元)。
    public static bool IsDiskNewer(
        DateTime? diskLastWriteUtc,
        DateTime backupTimestampUtc,
        TimeSpan tolerance);
}
```

`bk.TimestampUtc` は `BuildRecord` が `_clock.GetUtcNow().UtcDateTime` で打つ。
ディスク mtime と同一マシンの UTC 時計なので比較可能。

### 4.2 扱い — dirty 復元は維持し、集約警告 1 個で通知する

**ディスク版を優先してバックアップを捨てる案は採らない。** ディスクが新しい理由は
(1) kxEdit 自身が保存した(A-1・バックアップは捨ててよい)か
(2) 他アプリが後から更新した(バックアップに未保存編集が残っている)か区別できず、
捨てる実装は**新しい無言喪失経路**を作る。

採用: 従来どおり dirty 復元し、**復元完了後に集約警告ダイアログを 1 個**出す。
A-1 の害は「Ctrl+S で**無警告**で消える」ことなので、警告が出た時点で害は消える。
どちらの内容も失われず、ユーザーが情報を得たうえで選べる。

文言(案):

> 次のファイルは、バックアップを取った後にディスク側が更新されています。
> 復元したタブを上書き保存すると、ディスク上の新しい内容が失われます。

既存 `MainForm.ShowFailedRestoreDialog`(最大 10 件表示・`SanitizeForDisplay.OneLine` で無害化)と
同じ形にし、兄弟メソッド `ShowStaleBackupWarning` として実装する。

### 4.3 検査の置き場所 — `FileController` に一本化

ON(hot exit silent)経路と OFF(起動時確認)経路の**両方**が `FileController` を通る:

| 経路 | 復元メソッド | 検証済みパス |
|------|--------------|--------------|
| ON silent | `RestoreDirtyFromBackup`(`:752`) | `OriginalPathValidator.Check(rec.Path)` の `normalized` |
| OFF(確認 ON/OFF とも) | `RestoreFromBackup` | `OriginalPathValidator.Check(rec.OriginalPath)` の `normalized` |

どちらも「検証 Ok のときだけ」`normalized` に対して mtime を取り、陳腐化なら
`FileController` 内のリストへ積む。`IRestorePrompt` / `OfferRestoreOnStartup` /
`RestoreSession` の**シグニチャは変えない**。

- 追加 seam: `IFileTimestampProvider`(`src/kxEdit.App/Abstractions/`・`IReachabilityProbe` と同じ置き場)
  と実装 `FileTimestampProvider`(例外は捕捉して `null` を返す)。`FileController` ctor に注入する。
- 公開 API: `public IReadOnlyList<string> TakeStaleRestoredPaths()`(取得と同時にクリア)。
- `MainForm.OnShown` は ON / OFF いずれの復元後にも回収し、非空なら `ShowStaleBackupWarning` を出す。
- **検証 NG(無題フォールバック)では mtime を見ない**。パスが信用できない時点で判定材料にならず、
  攻撃者 JSON 由来のパスへ I/O させない(HIGH-2 の思想を維持)。
- 新たな UI 凍結クラスは作らない: mtime を取るのは `OriginalPathValidator.Check` が
  既に同期 I/O で触れた後のパスのみ(A-16 の既存リスクを超えない)。

## 5. 変更ファイル一覧

| ファイル | 変更 |
|----------|------|
| `src/kxEdit.Core/Backup/BackupStaleness.cs` | 新規(純粋関数) |
| `src/kxEdit.App/Abstractions/IFileTimestampProvider.cs` | 新規(seam) |
| `src/kxEdit.App/FileTimestampProvider.cs` | 新規(実装) |
| `src/kxEdit.App/DocumentManager.cs` | `DocumentDirtyChanged` 新設+`OnDirtyChanged` で発火 |
| `src/kxEdit.App/BackupCoordinator.cs` | 2 本購読+`OnDocumentSavePointOrClose`+`MarkStartupRestoreComplete` |
| `src/kxEdit.App/FileController.cs` | ctor に seam 追加・2 つの復元経路で陳腐化検査・`TakeStaleRestoredPaths` |
| `src/kxEdit.App/MainForm.cs` | seam 配線・`MarkStartupRestoreComplete` 呼出・`ShowStaleBackupWarning` |

## 6. テスト戦略(CLAUDE.md §5)

### L1 `kxEdit.Core.Tests`

- `BackupStalenessTests`: disk=null / disk が古い / 同時刻 / 許容内で新しい / 許容超で新しい / `DateTimeKind` の扱い。

### L3 `kxEdit.App.Tests`

- `BackupCoordinatorTests`
  - **A-1 回帰網**: dirty → tick で書込 → `SetSavePoint` → **Reconcile を呼ばずに**バックアップが削除されていること
    (現状の `Reconcile_DirtyThenSaved_DeletesBackup` は「次 Reconcile で消える」しか固定していない)。
  - **M-31 回帰網**: dirty 文書を `TryClose` → **Reconcile を呼ばずに**削除+レイアウトからも消えること。
  - **dirty 化では動かない**こと(clean→dirty で書込が発生しない=間隔契約の保存)。
  - **ゲート**: `MarkStartupRestoreComplete` 前は保存点/クローズでレイアウトを書かないこと。
  - `Shutdown` 後は無反応。
- `FileControllerTests`
  - ON / OFF 双方の復元で、disk mtime が新しければ `TakeStaleRestoredPaths` に載る / 古ければ載らない。
  - パス検証 NG(無題フォールバック)では timestamp provider が**呼ばれない**こと(Fake の呼出記録で固定)。
  - `TakeStaleRestoredPaths` が 2 回目で空になること。
- `MainFormSmokeTests`
  - 起動 → `session-state.json` の前回レイアウトが復元されること(ゲート退行の検出網)。

### ミューテーション検証(最終品質パスのスポットチェック)

`!doc.Editor.Modified` の否定・`_startupRestoreDone` ガードの除去・`IsDiskNewer` の比較演算子と
許容加算の 3 点を変異させ、対象テストが赤になることを確認してから復元する。

### L4 / L5

- L4: 不要(性能特性を変えない。むしろ full Reconcile を避けている)。
- **L5: 必要**。新規の警告ダイアログは SR で読まれる必要がある(CLAUDE.md §5「迷ったら必要に倒す」)。
  監査 §5 の未実施分(PR #36〜#39)と合わせて 1 回で実施する。チェック項目は実装計画で定義する。

## 7. リスクと対策

| リスク | 対策 |
|--------|------|
| 起動時にレイアウトを先行書込して前回セッションを失う | §3.3 のゲート+`MainFormSmokeTests` の復元網 |
| 保存の応答時間の悪化 | `SnapshotText` を呼ばない `ReconcileMapMaintenance` を使う(§3.2) |
| 陳腐化判定の誤検知で不要な警告 | 許容 2 秒+検証 Ok のパスのみ+取得失敗は false(従来動作) |
| ディスク版優先による新しい喪失経路 | 採らない(§4.2) |

## 8. 申し送り

- 監査 A-4 / A-7 / A-8 / A-19(`FileController` の保存経路)は未着手のまま。監査書 §8 の 1 番を分割した残り。
- M-18(外部変更検知)は本書の (b) と隣接する。将来「保存時にも mtime を比較する」を検討する際は、
  本書で導入した `IFileTimestampProvider` と `BackupStaleness` を再利用できる。
- 背景ライターの削除がディスクに届く前のクラッシュ窓は原理的に残る((b) が第 2 層として受ける)。
  完全に閉じるには保存経路での同期削除が要るが、保存の応答時間と引き換えになるため採らない。

---

## 9. 実施記録(2026-08-22・追記)

本節は策定後の追記(CLAUDE.md §8 の「実装時の精密化・実施記録の追記」)。上の §1〜§8 は策定時のまま。

### 9.1 §4.3 の前提が誤りだった(脆弱性レビュー H-1)

§4.3 に「新たな UI 凍結クラスは作らない: mtime を取るのは `OriginalPathValidator.Check` が
既に同期 I/O で触れた後のパスのみ(A-16 の既存リスクを超えない)」と書いたが、**UNC では成立しない**。

`OriginalPathValidator.Check` は `isUnc` のとき `RejectIfReparsePresent`(唯一の I/O)を
スキップし、残るのは `Path.GetFullPath` と文字列比較だけである。復元本文は `BackupRecord.Content`
由来でディスクを読まないため、UNC では **`FileTimestampProvider` の `File.Exists` が復元経路で
最初の同期 I/O** になる。切断済み共有では SMB タイムアウト(約 60 秒)まで UI スレッドが返らず、
タブ数ぶん直列に発生する(HIGH-6 / CSV-M-1 / `FileMetaProvider` が既に踏んだ罠の再導入)。

対応: `FileTimestampProvider` に `IReachabilityProbe` を注入し、`RemotePathDetector.IsRemote` が
true のパスには 5 秒プローブを前置する(`FileMetaProvider` と同型)。到達不能なら seam の既定契約
どおり null を返す(= 判定しない = 従来どおり復元)。さらに、到達不能と判明したリモートルートを
記録して同一共有の 2 件目以降のプローブを省く(「5 秒 × レコード数」の積み上がりを断つ)。

マップドネットワークドライブ(`Z:\`)は `isUnc=false` で reparse 検査の I/O が既に走るため
新しい凍結クラスではないが、同じ修正で stat 2 回分も消える。

### 9.2 ミューテーション検証で網の位置を 1 件修正

`OnCleanedOrClosed` の `if (!clean) return;` を除去する変異が**生き残った**。
`ReconcileMapMaintenance` は削除しかしないため「dirty 化でバックアップが書かれない」を
assert してもガードの有無で差が出ない(網が分岐に当たっていない)。実際に差が出るのは
`ReconcileLayout`(キャレット位置が署名に載る)なので、観測点をレイアウト書込へ移した。
§3.2 の「1 打鍵目ごとにバックアップを書き」という記述も、この実装では正しくない
(正しくは「レイアウト書込の churn が増える」)。実装側のコメントは訂正済み。

### 9.3 その他のレビュー反映

- `BackupStaleness.IsDiskNewer`: オーバーフロー防護に使う `DateTime.MaxValue - tolerance` 自身が、
  DateTime の全幅を超える tolerance で例外になる。現行呼出元は 2 秒固定で到達しないが、
  public API かつ M-18 での再利用を申し送っているため上限クランプを追加(レビュー L-1)。
- `MainForm.OnShown` の `MarkStartupRestoreComplete()` を `finally` へ移動。OFF 経路
  (`OfferBackupRestoreOnStartup`)は ON 経路と違い全体 try/catch を持たず、例外が抜けると
  ゲートが二度と開かずプロセスの間ずっと A-1 の修正が死ぬため(レビュー L-3)。
- `BackupStaleness` の XML doc に「セキュリティ制御ではない」と明記。攻撃者が JSON の
  `TimestampUtc` を未来にすれば判定を確実に抑止できるため、将来この関数を防御として
  頼る誤用を防ぐ(レビュー L-2・§4.2 の設計意図の再確認であり挙動変更なし)。

### 9.4 申し送りの追加

- `BackupCoordinator.OfferRestoreOnStartup` の `confirm=false` 分岐はレコード件数が**無制限**
  (ON 経路の `SessionLayoutStore.MaxTabs=200` に相当する打ち切りがない)。本 PR の範囲外の
  既存事項だが、リモート I/O やタブ生成の増幅器になる。次テーマで上限の要否を判断する。
