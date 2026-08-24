# 復元ダイアログ「すべて破棄」が孤児バックアップを消さない(E-2)設計書

- 対象: `docs/plans/2026-08-22-v0.2-release-bug-audit.md` §4 E-2
- 前提 main: `168f497`(PR #50 マージ後)
- 本書は**策定時スナップショット**(CLAUDE.md §8)。実装中の精密化と実施記録の追記のみ行う。

## 1. 目的

起動時の復元ダイアログで「すべて破棄」を選んでも、提示された孤児バックアップは**一件も消えない**。
次回起動でまた同じ一覧が出る。ユーザーの明示的な破棄意図が実行されないうえ、未保存本文が
**平文の JSON として最大 30 日間**(`SessionSweepMaxAge`)`%APPDATA%\kxEdit\backups` に残り続ける。

本ブランチのゴールは、**「すべて破棄」が一覧に出したものを実際に消す**ことに絞る。

## 2. 現行 main での実在確認(2026-08-24)

監査は `119ae33` に対するものだが、その後の PR #42〜#50 は E-2 経路に触れていない。
現行コードで全段を追い、**実在を確認した**。

| 段 | 場所 | 事実 |
|---|---|---|
| ① 一覧の生成 | `BackupCoordinator.LoadAllForRestore` → `BackupStore.LoadAll`(`BackupStore.cs:59-76`) | `baseDir` 直下(flat 後方互換)**と配下の `session-*` 全部**を列挙する。他インスタンス/前回クラッシュ由来も候補に上げる |
| ② 破棄の実行 | `BackupCoordinator.cs:289-290` | `_writer?.DeleteAll()` を呼ぶだけ |
| ③ 削除の実体 | `SerialBackupWriter.cs:68-78` → `BackupStore.DeleteSessionDir`(`BackupStore.cs:235-249`) | 消すのは `_sessionDir` = **自セッション用 subdir 一個だけ** |
| ④ 自セッション dir の中身 | `BackupCoordinator` ctor(`_sessionDir = Path.Combine(_dir, "session-" + Guid.NewGuid())`) | プロセスごとに新規 Guid。起動直後・復元提案の時点では**ほぼ常に空** |

**結果**: ①が集めた孤児は③の削除範囲に一つも入らない。「すべて破棄」は事実上 no-op で、
`session-*` は 30 日 sweep(`SweepOldSessions`)まで残る。flat 配置(v0.3.0-sec 由来)に至っては
sweep 対象外なので**永久に残る**。

### 2.1 なぜこうなっているか(BK-M-2 の意図)

`BackupStore.LoadAll` の xmldoc が明記している:

> 他インスタンス/前回クラッシュ由来の session-* も全部復元候補に上げるため、
> 「別インスタンスが『すべて破棄』を選ぶと自インスタンスのライブ backup が消える」問題を回避する
> (削除は DeleteSessionDir 経由で自セッション限定に切り替える)。

**一覧は広く・削除は狭く**という非対称は意図的なもので、多重起動時に他インスタンスのライブ
バックアップを巻き添えにしないための設計だった。E-2 はその安全側への倒し方が行き過ぎて、
「孤児が永久に消えない」側の実害を生んでいる、という構図である。したがって修正は
**「自セッションか否か」ではなく「ユーザーに提示したか否か」で削除範囲を決める**ことになる。

### 2.2 既存テストが欠陥を覆い隠している

`OfferRestore_ConfirmTrue_DiscardAll_InvokesWriterDeleteAll`(`BackupCoordinatorTests.cs:940`)は
Fake writer の `DeleteAllCount == 1` しか見ない。Fake は in-memory なので「どの dir を消したか」を
持たず、実 writer が別 dir を消していても緑になる。**このテストは E-2 の存在下で成立する**。
修正では実 `SerialBackupWriter` を使った統合テストを足し、この盲点自体を塞ぐ。

## 3. 中核設計 — 削除範囲を「提示した record」に合わせる

削除の単位を dir から **record** へ移す。

```
一覧に出した records の Id 集合
  − 自セッションが現在保護中の Id
  = 破棄対象。flat + 全 session-* を横断して <Id>.json を消す
```

`LoadAll` が集める範囲と `DiscardAll` が消す範囲が同じ集合になり、①と③の非対称が解消する。
dir 単位の再帰削除は使わないので、一覧に出ていないファイル(=ダイアログ表示後に他インスタンスが
書いた分)には構造的に触れない。

### 3.1 Core: `BackupStore.DeleteByIds(string baseDir, IReadOnlyCollection<string> ids)`

`TryMoveToSessionDir`(`BackupStore.cs:147-`)と**対称の探索**にする。同じ場所を探し、
同じ比較規約(`Path.TrimEndingDirectorySeparator(Path.GetFullPath(...))` + `OrdinalIgnoreCase`)を使う。

- 探索対象 = `baseDir` 直下(flat 後方互換)+ `Directory.EnumerateDirectories(baseDir, "session-*")` 全部。
- 各 dir で `<id>.json` を `TryDelete`(既存の握り潰し helper)。
- **削除が発生した `session-*` dir** に `*.json` が一つも残らなければ、その dir の `*.tmp` を
  `SweepTempFiles` してから `TryDeleteEmptySessionDir`。
- `baseDir` 自体は削除しない(`TryMoveToSessionDir` と同じ扱い)。`baseDir` 直下の `*.tmp` にも触らない
  (起動時に `LoadAllForRestore` が既に掃除している)。
- 戻り値 = 実際に削除した `*.json` の件数(テストの証人)。

**id の検証**: `BackupIdValidator.IsValid` を満たさない id は**その 1 件だけ skip** し、残りの削除は続行する。
`Write` / `Delete` が `ArgumentException` を投げる契約(BK-L-7)とここだけ極性を変える理由は、
**一括削除で 1 件の不正が全破棄を巻き添えにするのは安全側ではない**ため。実運用では `LoadAll` が
既に invalid-id を捨てているので到達しないが、Path.Combine への流入前段を塞ぐ意図は同じ。

**触っていない dir の `*.tmp` に手を出さない理由**: `AtomicFile.Write` は temp に書いてから
`File.Replace`/`Move` する。他インスタンスが書込中の `*.tmp` を消すと、そのインスタンスの
書込が失敗する(致命ではなく `OnWriteFailed` → 次 tick で `ForceWrite` 再試行に落ちる)。
掃除を「破棄対象を実際に含んでいた dir」に限れば、巻き添えの範囲がユーザーの破棄意図の中に収まる。

### 3.2 App: `IBackupWriter.DeleteAll()` → `DeleteAcrossSessions(string baseDir, IReadOnlyList<string> ids)`

`DeleteAll()` の呼び出し元は `BackupCoordinator.cs:290` の 1 箇所だけなので置換で済む。

名前で「**自セッション限定という既存契約を意図的に外れる**」ことを呼び出し側に明示する。
`Write` / `Delete` は ctor で受けた `_dir`(session dir)に束縛されており、そのスコープを
取り違えたことが E-2 の原因そのものだった。`baseDir` を引数で受ける形は既存の
`WriteLayout(path, layout)` / `DeleteLayout(path)` と同型。

`ids` は投入時に不変リストへ複写して渡す(背景スレッドが後で読むため、呼び出し側の
可変コレクションをそのまま渡さない)。

### 3.3 BackupCoordinator: `case RestoreAction.DiscardAll:`

```
ordered の Id 集合 − (_map の中で HasBackup=true の Id) → writer へ
```

自セッションが現在保護中の文書のバックアップは、たとえ一覧に載っていても消さない。

ダイアログ表示中に自分が書いた分は `LoadAll` 時点で存在しないので元から `ids` に入らない。
守るのは **`LoadAll` の直前に `Reconcile` が走って書かれた分**で、これを消すと `_map` は
`HasBackup=true` のまま実ファイルだけが無くなり、次に内容が変わるまで**無保護窓**ができる。
現行の起動シーケンス(`OnShown` より前に dirty 文書は存在しない)では実質発生しないが、
A-1 / M-31 で潰したのと同型の窓なので、ガードを明示的に置く。

## 4. 意図的に変えないこと

- **一覧に出ていないファイルは消さない**。ダイアログ表示後に他インスタンスが書いた分は対象外。
- 「あとで」「チェックしなかった項目」は据え置き(現状どおり・次回再提案)。
- **削除失敗は無音**のまま(次回再提案 → 30 日 sweep が最終回収)。「消えたつもり」の通知 UI は作らない。
- `confirm=false`(silent 全件復元)経路は `DiscardAll` を通らないため変更なし。
- `session-state.json`(レイアウト)は本文を持たず、OFF 経路の stale 掃除は
  `MainForm.OnFormClosing` の `Shutdown(keepForRestore:false)` が既に担う。本ブランチの対象外。

## 5. 受容するトレードオフ

**同時起動している別インスタンスのライブバックアップが一覧に載っていれば、消える。**

BK-M-2 が避けようとした事象が、「一覧に出た範囲で」復活する。受容する理由:

- 現状でも「復元」を選べば `AdoptRestored` → `TryMoveToSessionDir` が同じファイルを
  他インスタンスの dir から**奪う**。E-2 修正で新しく生まれる区別ではない。
- 消える範囲はユーザーが画面で見て「すべて破棄」と判断した集合に一致する。
- 根治には「そのセッションが生きているか」の判定機構(lock ファイル等)が要り、
  `LoadAll` の意味論(生存セッションを一覧から除外するか)まで変わる。v0.2 前の
  E-2 単独修正としては範囲が過大。→ **申し送り S-E2-1**。

## 6. テスト設計

### 6.1 Core(`kxEdit.Core.Tests` / 実 I/O・TempDir)

| # | 検証 |
|---|---|
| 1 | flat(`baseDir` 直下)の `<id>.json` が消える |
| 2 | 他 `session-*` の `<id>.json` が消える |
| 3 | **ids に無い `<other>.json` は残る**(他インスタンスのライブ保護) |
| 4 | 削除で `*.json` が空になった `session-*` dir は `*.tmp` ごと消える |
| 5 | `*.json` が残る `session-*` dir は消えず、その `*.tmp` も残る |
| 6 | 削除対象を含まなかった `session-*` dir には触れない(`*.tmp` も残る) |
| 7 | 不正 id を混ぜても `ArgumentException` を投げず、valid な残りは消える |
| 8 | `baseDir` 自体は削除されない / 存在しない `baseDir` でも例外なし |
| 9 | 同一 id が flat と `session-*` の両方にある(adopt-move 失敗の残骸)場合、両方消える |

### 6.2 App(`kxEdit.App.Tests`)

| # | 検証 |
|---|---|
| 10 | 既存 `OfferRestore_ConfirmTrue_DiscardAll_...` を強化: Fake が受け取った `baseDir` と `ids` が一覧全件と一致する(件数だけでなく**中身**を assert) |
| 11 | **実 `SerialBackupWriter` を使った統合テスト**: 孤児 `session-*` に record を植える → `DiscardAll` → `WaitForPendingJobs` 後に実ファイルが消えている(§2.2 の盲点を塞ぐ本命) |
| 12 | 自セッションが保護中の id は `ids` から除外される(`Reconcile` を internal 直呼びして dirty 文書の backup を作ってから破棄) |
| 13 | 「あとで」では何も消えない(既存維持) |

### 6.3 ミューテーション(最終品質パスのスポットチェック)

- 探索対象から flat を落とす → #1 が赤。
- 探索対象から `session-*` を落とす → #2 が赤。
- ids フィルタを外して dir 内全 `*.json` を消す → #3 が赤。
- 空 dir 削除の行を消す → #4 が赤。
- 「削除が発生した dir のみ」条件を外して全 dir の `*.tmp` を掃除 → #6 が赤。
- §3.3 の除外集合を空にする → #12 が赤。

## 7. 工程

CLAUDE.md §3 に従う。パス操作 + ファイル削除に触れるため、Core タスク時に**脆弱性レビューを前倒し**する。

| Task | 内容 | レビュー |
|---|---|---|
| 1 | Core `BackupStore.DeleteByIds` + Core テスト(#1-#9) | 仕様 + **脆弱性** |
| 2 | App 配線(`IBackupWriter` / `SerialBackupWriter` / Fake / `BackupCoordinator`)+ App テスト(#10-#13) | 仕様 |

最終ブランチレビュー 2 パス(コード品質 / 脆弱性・別エージェント)→ `tools/pre-merge-check.ps1` EXIT 0 → PR。

**L5 実機 SR 検証は省略可**と判断する。`kxEdit.Accessibility` / `EditorControl` の UIA 部 /
App の Speech 系のいずれにも触れず、復元ダイアログの文言・構造も変えないため。
代わりに手動スモーク手順(孤児を作る → 「すべて破棄」→ `%APPDATA%\kxEdit\backups` が空 →
再起動で再提案されない)を PR に残す。

## 8. 申し送り

- **S-E2-1**: 多重起動時の生存セッション判定(lock ファイル)。`LoadAll` が生存中の
  `session-*` を一覧から除外し、`DiscardAll` は死んだ dir を丸ごと消せるようになる。
  E-2 と多重起動問題を同時に根治するが、範囲が大きく別テーマ。
- **S-E2-2**: 一度も `*.json` を書けずにクラッシュした `session-*`(`*.tmp` だけが残る dir)は
  復元候補に上がらないため「すべて破棄」の対象外。平文の部分本文が 30 日 sweep まで残る。
  E-2 の契約(提示したものを消す)とは独立の問題として別途判断する。

### 8.1 実装時の追記(最終ブランチレビュー 2 パス由来)

策定時スナップショットである上記 2 件は変更せず、実装・レビューで判明した分を以下に追記する
(CLAUDE.md §8「実装時の精密化・実施記録の追記」)。

- **S-E2-3(実装時に新設)**: ファイル名と JSON 内の `Id` が食い違うファイル(外部から植えられた・
  手でコピーされた等)は、`LoadAll` が JSON の `Id` を読むので一覧には出るのに、`DeleteByIds` は
  ファイル名で照合するため消せない。結果として「すべて破棄」を何度押しても同じ候補が再提案され、
  未保存本文が平文のまま残り続ける。根治は `LoadAll` が実ファイルパスも返す形にして、削除を
  「一覧が示した実体」に対して行うこと。現状この事実は `BackupStore.DeleteByIds` の xmldoc に
  しか書かれておらず、台帳に載せないと回収されないためここに記録する。

- **S-E2-1 に追記(脆弱性パス M-1)**: 生存判定が入るまでの間、他インスタンスのライブ backup を
  「すべて破棄」で消すと、消された側の `_map` は `HasBackup=true` と `LastSig` を保持したままになる。
  次の Reconcile で `BackupPlanner.Decide(modified: true, currentSig == lastSig, …)` が `None` を返すため、
  **その文書の内容が次に変わる(または ForceWrite が立つ)まで再書込が走らず、保護が復帰しない**。
  その窓の中でクラッシュすると、その文書は復元候補にすら上がらない。

- **S-E2-2 に追記(脆弱性パス L-1)**: 削除が 1 件も成立しなかった dir では `DeleteTargetTempsIn` が
  呼ばれない(`n == 0` で continue)。このため、`<id>.json` が adopt-move(`TryMoveToSessionDir`)で
  別 dir へ移った後に旧 dir へ取り残された `<id>.json.<乱数>.tmp` も「すべて破棄」では消えず、
  30 日 sweep まで平文の部分本文として残る。S-E2-2 と同じ「一覧に出ないので破棄対象外」の族。

- **整理候補(コード品質パス L-6)**: `BackupStore.DeleteAll(string)` と `DeleteSessionDir(string)` は
  E-2 の配線変更で src 側の呼び出し元がゼロになった(残る利用は Core テストのみ)。とくに
  `DeleteSessionDir` は、E-2 の原因となったスコープ意味論(自セッション dir 限定)と、F-2 で
  「間違い」と判定した無差別 `*.tmp` 掃除の両方を今も持っている。将来「すべて破棄」を誤って
  ここへ配線し直すと E-2 と F-2 が同時に再導入されるが、それを捕まえる網は Core 層に無い
  (検出できるのは App 側の 2 本のみ)。削除するか、少なくとも「配線先にしてはならない」旨を
  API 表面に残すかを判断する。
