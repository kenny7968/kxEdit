# hot exit の確認なしクローズがバックアップ書込失敗を確認しない(A-8)設計書

- 対象: `docs/plans/2026-08-22-v0.2-release-bug-audit.md` §3 A-8(優先度 1)
- 前提 main: `33f2d3c`(PR #49 マージ後)
- 本書は**策定時スナップショット**(CLAUDE.md §8)。実装中の精密化と実施記録の追記のみ行う。

## 1. 目的

hot exit(未保存確認なしのクローズ)は「本文はバックアップに退避されるから確認は要らない」
という交換で成立している。**その交換の片側が成立したかを誰も確かめていない。**
書込が失敗しても警告なく終了し、次回起動で編集が消える。

本ブランチのゴールは、**確認をスキップする前に「実際に書けたこと」を確認する**ことに絞る。

## 2. 現行 main での実在確認(2026-08-24)

監査は `119ae33` に対するものだが、その後の PR #42〜#49 は A-8 経路に触れていない。
現行コードで全段を追い、**実在を確認した**。

| 段 | 場所 | 事実 |
|---|---|---|
| ① 確認スキップ判定 | `MainForm.cs:456-460` | `RestoreOpenFilesOnStartup && BackupEnabled && !HasOversizedDirtyDoc()` で silent close 確定。この時点でバックアップの成否は一切見ていない |
| ② 最終 flush | `MainForm.cs:498-499` → `BackupCoordinator.cs:679-689` | `ReconcileContent()` は Write ジョブを**キューへ投入するだけ**で戻る(非同期) |
| ③ 書込失敗 | `SerialBackupWriter.cs:46-53` | `BackupStore.Write` の例外を握り `OnWriteFailed?.Invoke(id)` のみ |
| ④ 失敗の回収 | `BackupCoordinator.cs:163` → `:465-468` | `_failed` を読むのは `ReconcileContent` 冒頭**だけ**。②の後に Reconcile は二度と走らない=**失敗は永久に観測されない** |
| ⑤ レイアウトは書かれる | `BackupCoordinator.cs:563-565`、`:634` | `info.HasBackup` は投入時に楽観的に true。本文書込が失敗しても `session-state.json` は `BackupId` 付きで確定書込される |
| ⑥ 次回起動 | `FileController.cs:1078-1082` / `:1114-1120` | E9′=**ディスクの古い版を無言で開く** / E4′(無題)=**タブごと消える**。`Trace.TraceWarning` のみ |

再現条件: ディスクフル / AV ロック / `%APPDATA%` 書込不可の状態で編集 → ウィンドウ X。

### 2.1 監査記述の補正

- **監査「Trace 以外に通知なし」は一段甘い。** `SerialBackupWriter.Write` の catch は
  Trace を出さない。書込失敗そのものの痕跡はプロセス内に**一切残らない**
  (⑥の起動時 Trace だけが事後の痕跡)。
- **ジョブ自体は取りこぼされない。** `Shutdown` → `_writer.Dispose()` が `CompleteAdding`
  + `Join(15s)` でドレインする(`SerialBackupWriter.cs:150-177`)。壊れているのは
  「書けたか」を誰も見ないことだけで、「書きに行かない」ではない。
- **前 tick の失敗は既に回復している。** ②の `ReconcileContent` が `_failed` を drain して
  `ForceWrite` で再試行する。壊れているのは**最後の 1 回の結果**だけ。
- **レイアウト書込失敗は本文喪失にならない。** `session-state.json` が欠けても extras 経路
  (`FileController.cs:990-1025` → `RestoreExtraBackup` → `RestoreFromBackup`)が
  バックアップを拾って dirty 復元する。引き金にすべきは**本文書込の失敗のみ**。
- 監査 A-8 が前提に敷いていた A-1 は既に修正済み(`OnBackupBecameUnneeded` が存在)。

## 3. 中核設計 — 前提ゲートから事後条件へ

silent close の判定を 2 段にする。

```
① 前提ゲート  RestoreOpenFilesOnStartup && BackupEnabled && !oversized   … 既存のまま
② 事後条件    最終 flush を投入 → 完了を待つ → 本文書込が全て成功したか  … 新規
```

②が偽なら `silentPath = false` へ倒し、**既存の Yes/No/Cancel 確認ループをそのまま走らせる**。
新しい確認 UI は作らない(既存の SR 経路・テスト済みコードをそのまま再利用する)。

「前置ガードの列挙は原理的に漏れる → 事後条件で検査する」(PR #43 の教訓)の適用。
A-8 の本質は「交換条件の片側を検査していない」ことであり、書込失敗の原因を列挙して
先回りする形(ディスク残量チェック等)は原理的に漏れる。

## 4. `IBackupWriter` に待ち合わせ API を追加

```csharp
/// <summary>投入済みジョブが全て実行し終わるまで待つ(Dispose はしない)。
/// timeout 内に完了を確認できたら true。締切済みなら待たずに true。</summary>
bool WaitForPendingJobs(TimeSpan timeout);
```

`SerialBackupWriter` はキュー末尾にバリアジョブを積んで待つ。

- 同期プリミティブは **`TaskCompletionSource`**。`ManualResetEventSlim` + `using` は
  timeout 後にバリアジョブが破棄済みインスタンスへ `Set` を打つ形になり、
  ワーカー側 catch に例外を吸わせる設計になるため採らない。
- `Enqueue` を `bool` 返しへ変更する。締切済みで捨てられたジョブを待つと必ず
  timeout 全長ブロックしてしまうため、投入できなかったら待たずに true を返す。
- `Dispose` との関係: `WaitForPendingJobs` は `Dispose` しない。終了がキャンセルされた
  場合にライターが死んでいてはいけない。

実装フェイクは `tests/kxEdit.App.Tests/Fakes/FakeBackupWriter.cs` の 1 個のみ
(`grep -rn "IBackupWriter" tests/` で確認済み)。

## 5. `BackupCoordinator` の観測 API

```csharp
/// <summary>hot exit の確認スキップ前に呼ぶ事後条件検査(A-8)。
/// FinalFlushForRestore で投入した本文書込が全て成功したかを待ち合わせて答える。</summary>
public bool WaitForFinalFlush() => WaitForFinalFlush(FinalFlushWait); // 既定 5 秒

internal bool WaitForFinalFlush(TimeSpan timeout)
{
    if (_shutDown || _writer is null) return true;           // 書くものが無い=失敗も無い
    if (!_writer.WaitForPendingJobs(timeout)) return false;  // 確認できない=安全側で失敗扱い
    return _failed.IsEmpty;                                  // dequeue しない(下記)
}
```

### 5.1 `_failed` を dequeue しない理由

ここで吸い出すと、終了をキャンセルされたときに既存の
`ReconcileContent` → `ForceWrite` 再試行機構(`BackupCoordinator.cs:465-468`)が
失敗を見失う。**A-8 と同じ「握り潰し」を新設することになる。** 読むだけにして
回復機構は無傷のまま残す。

代償: 「書込は失敗したがその後 clean になった文書」の Id が `_failed` に残っていると、
安全側の偽陽性で確認へ倒れる(§9 S-A8-1)。確認ループは `!doc.Editor.Modified` を
skip するので、そのタブに対して確認は出ない。実害は他の dirty タブへの余分な確認 1 回。

### 5.2 happens-before

バリアジョブはワーカースレッド上で先行ジョブの後に実行される。`OnWriteFailed` は
失敗ジョブの内部で同期的に呼ばれ `_failed` へ積まれる。よってバリア完了を待った時点で、
先行する全失敗は `_failed` に反映済み。`ConcurrentQueue` + バリアの Wait/Set が
必要なメモリバリアを与える。

### 5.3 timeout = 5 秒

- Windows のシャットダウン猶予に収める(WM_QUERYENDSESSION 経路で長時間待つと
  強制終了の対象になる)。
- 既存 `Shutdown` の `Join(15s)` より短い=**新しい最悪ブロック時間を作らない**。
  正常時はバリアが即返るので終了の体感は不変。
- 定数は `BackupCoordinator.FinalFlushWait` に置き、テスト用に `TimeSpan` 明示の
  internal オーバーロードを併設する。

## 6. `MainForm.OnFormClosing` の順序変更

```csharp
bool silentPath = _settings.RestoreOpenFilesOnStartup
    && _settings.BackupEnabled
    && !HasOversizedDirtyDoc();
if (silentPath)
{
    _backup.FinalFlushForRestore();          // ← 確認判定より前へ移動
    if (!_backup.WaitForFinalFlush())
        silentPath = false;                  // A-8: 書けていない → 従来の確認へ
}
_lastCloseTookSilentPathForTest = silentPath;

if (!silentPath) { /* 既存の確認ループ・MarkDiscarded … 無変更 */ }
   ⋮
if (_settings.RestoreOpenFilesOnStartup && !silentPath)   // ← silent 成功時は flush 済み
    _backup.FinalFlushForRestore();
```

### 6.1 flush を前倒しして安全な根拠

`SessionLayout` / `SessionLayoutRecord` にウィンドウ寸法は入らない(`BuildLayout` を確認)。
寸法は `settings.json` 側で `SaveSettingsSafe()` が別に保存する。よって flush を
寸法保存より前へ動かしてもレイアウトの記録内容は変わらない。

### 6.2 二重 flush を避ける条件

末尾の flush 条件に `&& !silentPath` を足す。`silentPath` は true → false へしか
遷移しないので、「末尾で `silentPath == true`」は「前段で flush 済み」と同値。
フォールバック時は `silentPath == false` になるので確認ループ後にもう一度 flush が走り、
保存 / 破棄の結果がレイアウトへ反映される。

`ReconcileContent` は dirty 文書ごとに `SnapshotText`(全文 string 化)を走らせるため、
巨大 dirty タブ同居時に二重に走らせると終了が目に見えて遅くなる。この条件はその回避も兼ねる。

### 6.3 経路ごとの挙動

| 経路 | 変化 |
|---|---|
| OFF(`RestoreOpenFilesOnStartup=false`) | なし |
| ON × BackupOFF | なし(前提ゲートで silentPath=false) |
| ON × BackupON × oversized | なし(前提ゲートで silentPath=false) |
| ON × BackupON × 書込成功 | layout 書込が数十 μs 早まるだけ。内容・件数は同一 |
| ON × BackupON × **書込失敗** | **新規**: 従来の確認ループへフォールバック |
| フォールバック後にキャンセル | flush 済み(バックアップは書けているほど良い)。ライターは生存し継続運用 |

## 7. テスト設計

### 7.1 L3 e2e(本命 — 本番実装を証人にする)

`backupDirectory` に**ファイル**を置いてから `MainForm` を構築すると、
`BackupStore.Write` の `Directory.CreateDirectory(<BackupDir>/session-xxx)` が
`IOException` を投げる。**Fake ではなく実 `SerialBackupWriter` が本当に失敗する。**

PR #47 の教訓(「Fake を注入するテストは本番実装の性質を証人にできない」)に従い、
これを第一候補にする。`ShowMainForm_Unified(settings, tmp)` の既存パターンに乗せる。

検証: dirty タブ → `Close()` → `SetConfirmDiscardOverrideForTest` が呼ばれること
+ `LastCloseTookSilentPathForTest == false`。

**実装前に実測する前提条件**: 起動時の `LoadAllForRestore` / `SweepOldSessions` が
同じ不正パスで例外を出さないこと。壊れる場合の代替は (a) 存在しないドライブ文字を
`backupDirectory` に渡す、(b) それも駄目なら `MainForm` に writerFactory 注入 seam を
足す(この順で倒す。(b) は本番実装を証人にできなくなるため最後)。

### 7.2 L3 単体

- `BackupCoordinatorTests`: `FakeBackupWriter` に失敗モードを追加し、
  `FinalFlushForRestore` → `WaitForFinalFlush()` が失敗時 false / 成功時 true。
  **`_failed` を消費していないこと**(直後の `Reconcile` が `ForceWrite` で再書込すること)
  を独立に固定する=§5.1 の設計判断そのものを網にする。
- `SerialBackupWriter`: `WaitForPendingJobs` の 完了 true / 締切(Dispose 後)true /
  ブロックするジョブを積んで timeout false。

### 7.3 ミューテーション検証(最終品質パスのスポットチェック)

| 変異 | 期待 |
|---|---|
| `if (!_backup.WaitForFinalFlush()) silentPath = false;` の本体を削除 | 7.1 が赤 |
| `return _failed.IsEmpty;` → `return true;` | 7.1 と 7.2 が赤 |
| `if (!_writer.WaitForPendingJobs(timeout)) return false;` を削除 | 7.2 の timeout ケースが赤 |
| 末尾 flush の `&& !silentPath` を削除 | (挙動不変側)既存の layout 件数テストで検出できるかを確認し、できなければ網を足す |

### 7.4 L5(実機 SR 検証)

SR 経路(`kxEdit.Accessibility` / `EditorControl` の UIA 部 / App の Speech 系)そのものは
不変だが、**SR ユーザーが遭遇する終了確認が新しい条件で出る**。CLAUDE.md §5
「判定に迷ったら必要に倒す」に従い **必要** と判定する。

項目: `%APPDATA%\kxEdit\backups` を読み取り専用にして書込失敗を作り、
未保存タブがある状態でウィンドウ X → 終了確認ダイアログが NVDA で読み上げられること。

## 8. 非目標(YAGNI)

- **M-20** — セッション中(tick)の書込失敗をユーザーに一度も知らせない件。A-8 の遠因だが、
  通知頻度・SR 発声設計というまったく別の議論が要る。別テーマ(§9 S-A8-4)。
- 書込失敗時の代替保存先へのリトライ。
- レイアウト書込失敗を引き金にすること(§2.1 で本文喪失にならないことを確認済み)。
- クラッシュ・電源断(`OnFormClosing` を通らない経路)。原理的に本設計の対象外。

## 9. 申し送り

| ID | 内容 |
|---|---|
| S-A8-1 | 失敗 Id が「その後 clean になった文書」のものでも確認へ倒す(安全側の偽陽性・実害は余分な確認 1 回) |
| S-A8-2 | timeout(5 秒)超過は失敗扱い。極端に遅いディスクでは正常時にも確認が出る |
| S-A8-3 | レイアウト書込失敗は引き金にしない。extras 経路が本文を拾う前提に依存しているので、extras 経路を変更するときは本判断を再検証する |
| S-A8-4 | M-20(セッション中の書込失敗を一度も通知しない)は別テーマとして未回収 |
| S-A8-5 | クラッシュ・電源断は対象外 |

## 10. 実施記録

策定時スナップショットの本体(§1-§9)は書き換えず、実装中に判明した訂正と実測をここへ積む
(CLAUDE.md §8)。

### 10.1 §5.3 の根拠は誤りだった — 最悪ブロック時間は 15 秒 → 20 秒に伸びる

§5.3 は timeout=5 秒の根拠として「既存 `Shutdown` の `Join(15s)` より短い=**新しい最悪ブロック
時間を作らない**」と書いた。**これは誤り。**

`WaitForPendingJobs` は `Dispose` しないため、5 秒待った**後で** `OnFormClosed` →
`Shutdown` → `_writer.Dispose()` → `Join(15s)` が**別途**走る。つまり待ちは既存の 15 秒を
**置き換えるのではなく直列に足す**。ワーカーが固まっているケースの UI スレッド最悪ブロックは
**15 秒 → 最大 20 秒**になる(Task 1 コード品質レビューが指摘)。

**判断: ② 受容(PR description に記載)。** 理由:

- `Shutdown` の `Join(15s)` は「終了時にバックアップ書込を取りこぼさない」保証そのもの。
  これを縮めるのは「稀な長い凍結」を「稀なバックアップ喪失」と交換することであり、
  データ喪失を直す本ブランチでは向きが逆。
- 5 秒を使い切るのは、ワーカーが固まった**失敗ケース**と、S-A8-2 の**極端に遅いケース**
  (巨大 dirty 文書を遅いディスク / UNC 上の `%APPDATA%` へ書く)だけ。正常時はバリアが
  ms オーダーで返る。
- 変更前から既に `Join(15s)` が WM_QUERYENDSESSION の猶予を超えていたので、
  これは新しい種類の問題ではなく既存の露出が 1.33 倍になるだけ。
- **5 秒と 15 秒は素直には積み上がらない。** フォールバック時は 2 つの待ちの間に
  未保存確認のモーダルが挟まり、ユーザーが答えている間もワーカーは走り続ける。
  現実的な追加凍結は 5 秒。20 秒が背中合わせで出るのは「silentPath が真だったのに
  ワーカーが固まり、かつ `Modified` な文書が無くて確認が 1 枚も出ない」狭い 1 ケース
  (詰まっているのが delete / layout ジョブの場合など)。
- **2 つの凍結は体感の質が違う。** 15 秒側は `OnFormClosed`=ウィンドウが消えた後の凍結、
  5 秒側はまだ見えているウィンドウの凍結。

timeout は 5 秒のまま据え置く(3 秒に縮めても最悪 18 秒で、遅いディスクでの偽陽性が増える割に
得るものが小さい)。

### 10.2 §7.1 の代替手段は不要と確定(スパイク実測・2026-08-24)

`backupDirectory` の位置にファイルを置くと `BackupStore.Write` の
`Directory.CreateDirectory(<BackupDir>/session-xxx)` が IOException を投げ、
**実 `SerialBackupWriter` が本当に失敗する**。使い捨てテストでの実測:

```
confirmCalls=0  LastCloseTookSilentPath=true  sessionDirExists=False
layoutWritten=True tabs=1 backupId=2159d3e31cc547bdb55edd76223b949b
```

無題タブなので次回起動は E4′ = タブごと消失。Form の起動・終了は完走する
(`BackupStore.LoadAll` は `Directory.Exists` false で空・sweep 2 種は try/catch)。
§7.1 が挙げていた代替((a) 存在しないドライブ / (b) writerFactory 注入 seam)は**不要**。

### 10.3 申し送りの追加

| ID | 内容 |
|---|---|
| S-A8-6 | 終了時の UI スレッド最悪ブロックが 15 秒 → 20 秒(§10.1)。ワーカーが固まった失敗ケースのみ |
