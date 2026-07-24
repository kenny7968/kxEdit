# セッション復元の hot exit 型統合 設計書

- **作成日**: 2026-07-23
- **対象**: `src/yEdit.Core`(Session 再設計・Backup 拡張)+ `src/yEdit.App`(BackupCoordinator・MainForm・FileController)+ 説明書
- **区分**: 機能統合(バックアップ復元と前回セッション復元の復元経路を一本化)。既定 OFF 時は挙動不変
- **前提**: PR #22(起動時に前回開いていたファイルを開く)マージ後の再設計。ユーザーとの
  ブレインストーミング(2026-07-23)で「Notepad++ / Windows メモ帳型の hot exit モデルへの統合」を承認済み

## 0. スコープと決定事項サマリ

**採用方針**: hot exit 型統合 ―― 未保存内容の永続化は既存バックアップストア
(`%APPDATA%\yEdit\backups\session-*\`)に一本化し、タブレイアウト(パス・順序・アクティブ・
カーソル)は軽量な定期スナップショット `%APPDATA%\yEdit\session-state.json` に保存する。
起動時の復元は 1 本の経路に統合し、**設定 ON ではクラッシュか正常終了かを区別せず silent に
前回の状態を復元**する。

**ユーザーから見える姿**:
- 設定は現行どおり「起動時に前回開いていたファイルを開く」1 個・既定 OFF。
- ON の意味 = 「どんな終わり方をしても、前回の状態(タブ構成・カーソル・未保存の編集)が
  そのまま黙って戻る」。終了時の未保存確認は出ない。
- OFF の挙動は完全に現状維持(終了時確認+異常終了時のみバックアップ復元提案)。

**退役するもの**:
- `last-session-buffers.json` と `LastSessionBuffersStore`(内容はバックアップストアへ一本化)。
- 終了時の cap 判定(`WillDirtyContentFitInCaps`・per-tab 1M / total 15M chars)と
  cap 超過 fall-through・No-discard 追跡(`discardedDocs`)。終了時の一括本文書込自体が
  なくなるため不要(内容はセッション中に定期退避済み)。
- `AppSettings.LastSession`(移行のための読み取りは 1 バージョン残す=§8)。
- `SessionTabRecord.BufferKey / WasModified`(新レコード型へ移行)。

**非対象(scope out)**:
- 複数インスタンス同時起動時のレイアウト競合(session-state.json は last-writer-wins。
  settings.json の既存制約と同水準・M9+ 申し送りを継承)。
- スクロール位置・選択範囲・分割等の復元(従来どおり非対象)。
- 非 dirty パスありタブのエンコーディング再現(従来どおり auto-detect に任せる)。

## 1. アーキテクチャ

```
[セッション中(UI スレッド)] BackupCoordinator.Reconcile(既定 30 秒 tick + ActiveDocumentChanged)
   ├─ 従来: dirty 文書の本文スナップショット → 背景直列ライターで backups/session-{id}/<Id>.json
   └─ 新設: レイアウト署名(パス列+無題番号+BackupId+アクティブ+カーソル)が変化していたら
      SessionLayout を構築 → 背景直列ライターで session-state.json を原子的書込
[正常終了・設定 ON]  OnFormClosing: 未保存確認なし → 最終 Reconcile(本文+レイアウトの最終 flush)
                     OnFormClosed: Shutdown(keepForRestore: true) → バックアップ削除せずドレイン
[正常終了・設定 OFF] 現行どおり: 確認 → Shutdown() が自セッション分を削除 + session-state.json 削除
[起動時] RestoreSession(統合経路):
   設定 ON  → session-state.json + BackupStore.LoadAll を照合し silent 復元(ダイアログなし)
   設定 OFF → 現行どおり孤児バックアップの復元提案(RestoreDialog / 確認 OFF なら自動)
```

**鉄則順守**: スナップショット取得(SCI_* 由来)は従来どおり UI スレッドの Reconcile のみ。
背景スレッドは Core の純 I/O のみ。レイアウト書込も同じ直列ライターに載せる(書込順序の直列性を保存)。

**不変条件**: 「未保存内容がディスク上のどこにも存在しない瞬間」を、ユーザーが明示的に
破棄(No / すべて破棄)した場合以外に作らない。旧設計の「終了時に buffers.json 書込が失敗すると
バックアップ削除済みで内容消失」の窓は、内容の永続化を定期退避(バックアップ)側に寄せることで
構造的に閉じる。

## 2. データモデル

### 2.1 SessionLayout(Core 新規・`yEdit.Core.Session`)

```csharp
public sealed record SessionLayoutRecord(
    string? Path,          // 保存済ファイルなら絶対パス・無題なら null
    int UntitledNumber,    // 無題タブの連番(パスありは 0)
    string? BackupId,      // dirty 文書のバックアップ Id(Guid.N)。クリーン文書・空の無題は null
    bool IsActive,         // 終了時にアクティブだったか(高々 1 タブが true)
    int CaretLine,         // 0-based
    int CaretColumn,       // 0-based
    int LineEnding         // 無題の空枠復元用(パスありは復元時未使用=auto-detect)
);

public sealed record SessionLayout(
    List<SessionLayoutRecord> Tabs,
    DateTime SavedAtUtc    // 診断用(復元判定には使わない)
);
```

`BackupId` が本設計の要: dirty 文書の本文は**バックアップレコードそのもの**を参照する。
本文・CodePage・HasBom・LineEnding は `BackupRecord` が既に持っているため、レイアウト側に
重複して持たない(パスありの非 dirty は auto-detect、無題の空枠のみ LineEnding を使う)。

### 2.2 SessionLayoutStore(Core 新規)

```csharp
public static class SessionLayoutStore
{
    public static string DefaultPath { get; }  // %APPDATA%\yEdit\session-state.json
    public static SessionLayout? Load(string path);   // 破損・欠落は null(例外を漏らさない)
    public static void Save(string path, SessionLayout layout);  // AtomicFile.Write(temp→Replace)
    public static void Delete(string path);           // OFF 終了時・復元消費後
}
```

### 2.3 Load 側の防御(Normalize 相当を Store 内に持つ)

session-state.json は外部入力(改竄可能)として扱う:

- `Tabs` null → 空 List。null 要素 → skip。**要素数上限 200**(超過分は先頭から 200 で切り
  トレース警告。PR #22 申し送り「Tabs 上限なし」の回収)。
- `Path` 非 null かつ空白のみ → record skip。
- `BackupId` 非 null かつ `BackupIdValidator.IsValid`=false → **BackupId=null に落とす**+トレース
  (パストラバーサル入口の遮断。ただし §3.3 のとおり照合は辞書ベースであり、レイアウト由来の
  Id を Path.Combine に流す経路は設計上存在させない=二重防御)。
- 同一 `BackupId` の重複参照 → 2 個目以降を null に落とす(1 バックアップ 1 タブの不変)。
- `UntitledNumber<0` → 0、`CaretLine/CaretColumn<0` → 0 に clamp。`LineEnding` 未知値は
  復元側の `SafeLineEndingOrFallback` で吸収(現行 §8.4 E10 と同じ)。
- `IsActive` が複数 true → 最初の 1 個のみ true。

### 2.4 BackupStore 拡張(Core・adopt-move)

```csharp
/// <summary>baseDir 配下(flat + session-*)から id.json を探し、targetSessionDir へ原子的に
/// 移動する。見つからない・移動失敗は false(呼び出し側はトレースのみ・復元は続行)。</summary>
public static bool TryMoveToSessionDir(string baseDir, string id, string targetSessionDir);
```

Id は `BackupIdValidator` で検証してから `Path.Combine` に流す(Write/Delete と同じ HIGH-1 対称)。
移動は `File.Move`(同一ボリューム内=原子的)。移動元 session-* dir が空になったら
`DeleteSessionDir` で掃除(失敗は無害・30 日 sweep が最終回収)。

## 3. データフロー

### 3.1 セッション中(Reconcile へのレイアウト書込追加)

`BackupCoordinator.Reconcile` の末尾に追加:

1. `_sessionRestoreEnabled`(後述の実行時フラグ)が false なら何もしない。
2. 全文書を走査して `SessionLayout` を構築。dirty 文書(`_map[doc].HasBackup=true`)は
   `BackupId=_map[doc].Id`、それ以外は `BackupId=null`。
3. レイアウト署名(Tabs の内容から計算する 64bit ハッシュ=`ContentSignature` 流用)が
   前回書込時と同じなら書かない(tick 抑止=バックアップ本文と同じ方針)。
4. 変化していれば `IBackupWriter` に新設する `WriteLayout(SessionLayout)` ジョブを投入
   (SerialBackupWriter → `SessionLayoutStore.Save`・失敗は `OnWriteFailed("layout")` 相当で
   次 tick 強制再書込)。

`_sessionRestoreEnabled` は `RestoreOpenFilesOnStartup` 設定を ctor + `UpdateSettings` で受け取る
(設定ダイアログ OK で即追従=バックアップ有効/間隔と同じ経路)。

**レイアウトが書かれる条件**: `_sessionRestoreEnabled` のみに依存し、`_enabled`
(バックアップ有効)とは独立(§5.3 の組合せ表を参照)。ただし書込は `_writer` 経由のため、
両方 false のときは writer 自体を生成しない(現行の遅延生成を維持)。

### 3.2 正常終了

`MainForm.OnFormClosing`(§8.2 の silent path を置き換え):

```
if (_settings.RestoreOpenFilesOnStartup && _settings.BackupEnabled) {
    // hot exit: 未保存確認なし。最終 flush は Coordinator に委譲
    _backup.FinalFlushForRestore();   // UI スレッド: 最終 Reconcile(本文+レイアウト)
    ... WindowWidth/Height 反映・SaveSettingsSafe() ...
    base.OnFormClosing(e);  return;
}
// 従来経路(設定 OFF、または BackupEnabled=false): 全 dirty タブに Yes/No/Cancel
foreach (...) { ConfirmDiscardIfDirty ... e.Cancel ... }
// 確認通過後: ON(だが BackupEnabled=false)ならレイアウトのみ最終保存、OFF なら残骸削除
if (_settings.RestoreOpenFilesOnStartup) _backup.FinalFlushForRestore();
else                                     SessionLayoutStore.Delete(...);
SaveSettingsSafe(); base.OnFormClosing(e);
```

`MainForm.OnFormClosed`:

```
_backup.Shutdown(keepForRestore: _settings.RestoreOpenFilesOnStartup);
```

`BackupCoordinator` の変更:

- `FinalFlushForRestore()`(新設・UI スレッド): `Reconcile()` を 1 回実行(dirty 本文の最終退避)
  + レイアウトを署名判定なしで強制書込ジョブ投入。`_shutDown` 後は no-op。
- `Shutdown(bool keepForRestore)`: `keepForRestore=true` なら**自セッション分の削除をスキップ**し、
  タイマー停止+ドレインのみ(バックアップファイルと session-state.json を次回起動用に残す)。
  `false` なら現行どおり削除+`SessionLayoutStore.Delete` ジョブを投入(OFF 終了で stale レイアウトを
  残さない=後日 ON に切り替えた際に古いセッションが亡霊復元されるのを防ぐ)。

**No-discard(破棄「いいえ」)の意味論**: 従来経路で No を選んだ dirty タブはアプリが閉じない
(Cancel 扱い)ため、PR #22 §8 の「No-discard 除外」に相当する状況は発生しない
(Yes=保存で clean 化・No=終了中止・従来どおり)。silent path では確認自体が出ない。

### 3.3 起動時の統合復元

`MainForm.OnShown`(現行の `OfferRestoreOnStartup` + `TryRestoreLastSession` を置き換え):

```
if (_settings.RestoreOpenFilesOnStartup)
    restored = RestoreUnifiedSession();          // silent・ダイアログなし
else
    restored = _backup.OfferRestoreOnStartup(this, _file.RestoreFromBackup,
                                             _settings.ConfirmRestoreOnStartup);  // 現行どおり
```

`RestoreUnifiedSession` の骨子(実体は `FileController.RestoreSession` に置く。
sweep・LoadAll・消費処理は Coordinator の新設メソッド `CollectForSilentRestore` が担う):

```
1. layout = SessionLayoutStore.Load(...)         // null なら空扱い
2. backups = BackupStore.LoadAll(_dir) を Id → BackupRecord の辞書化
   (重複 Id は TimestampUtc が新しい方を採用)
3. foreach (rec in layout.Tabs):                 // タブ順を保存
   a. rec.BackupId があり辞書に存在:
      - bk.Content が null(>32M path-only)→ (c) の disk 再オープンへ demote+トレース
        (silent 経路で「空 dirty を実パスに載せる」ことは絶対にしない=Ctrl+S での
         ファイル切り詰め事故を構造的に排除)
      - rec.Path あり → RestorePathDirty 相当(本文・CodePage・HasBom・LineEnding は
        BackupRecord から。Path は rec.Path を OriginalPathValidator で検証=HIGH-2 対称。
        不一致(bk.OriginalPath≠rec.Path)は bk.OriginalPath を信頼せず rec.Path 側を検証して使う)
      - rec.Path なし(無題)→ RestoreUntitledTab 相当(Modified=true 固定)
      - 消費記録: consumedIds に追加
   b. rec.BackupId があるが辞書に無い → §4 E9 と同じ demote:
      パスあり=disk 再オープン+トレース / 無題=skip+トレース(編集内容は失われている)
   c. rec.BackupId なし:
      パスあり=TryOpenOrActivate(失敗は failedPaths 集約=現行 E1〜E3 と同じ) /
      無題=空枠を復元(UntitledNumber+LineEnding のみ。§2.1 のとおり「空だった無題タブ」を意味する)
4. extras = 辞書のうち consumedIds に無いレコード(TimestampUtc 降順):
   RestoreFromBackup で追加復元(HIGH-2 検証込み)。ただし Content=null の path-only は
   OriginalPathValidator Ok なら disk 再オープン・無題なら skip+トレース。
   ※ extras = レイアウトより後に開かれたタブ(クラッシュ前 1 tick 未満)・他インスタンスの遺物・
     OFF 時代の「あとで」孤児。silent 経路でも安全側=「拾って開く」に倒す。
5. 復元後処理:
   - IsActive の doc を Activate(なければ最初の復元タブ)
   - openedCount>0 なら initialEmpty(起動時空無題タブ)を TryClose
   - 復元した dirty 文書を Coordinator に再登録(元 Id 引き継ぎ)+ §3.4 の adopt-move
   - SessionLayoutStore.Delete(消費済み=次回は今セッションの新レイアウトが正)
   - failedPaths があれば集約 Warn 1 個(現行 ShowFailedRestoreDialog を流用)
6. 全体を try/catch で包み、想定外例外は通常起動へフォールバック(現行 E8 と同じ)
```

per-record try/catch(1 レコードの破損が全復元を巻き添えにしない)は現行
`RestoreLastSession` の不変条件をそのまま維持する。

### 3.4 消費済みバックアップの adopt-move(発見した潜在バグの修正)

**発見した潜在バグ(現行 main・BK-M-2 以降)**: 復元した文書は「元 Id を引き継ぎ既存バックアップを
継続使用する」設計だが、BK-M-2 でバックアップが session 別 subdir に分かれたため、復元後の
書込・削除は**新セッションの dir** に対して行われ、**旧 session dir の元ファイルは誰も消さない**。
結果、復元(または復元後に保存)したバックアップが最長 30 日間、起動のたびに再提案される。
確認ダイアログ経路では「毎回同じ提案が出る」煩わしさで済むが、統合後の silent 経路では
**同じタブが起動のたびに複製される**ため、修正が必須になる。

**修正**: 復元でバックアップを文書に載せ Coordinator へ再登録した直後、
`BackupStore.TryMoveToSessionDir(baseDir, id, 現セッション dir)` で元ファイルを自セッション dir へ
原子的に移動する(=M5 の「同一ファイル継続使用」の不変条件を session-dir 構成下で復元する)。

- 移動後は通常ライフサイクル(clean 化で Delete・ON 終了で keep・OFF 終了で削除)に乗る。
- 移動前後のどの時点でクラッシュしても、ファイルは新旧どちらかの dir に必ず 1 個存在
  (File.Move は同一ボリューム内で原子的)=無保護窓なし・複製なし。
- 移動失敗(ロック等)はトレースのみで続行(最悪は現行と同じ「再提案/再復元」に退化するだけで、
  データは失わない)。
- この修正は **OFF の確認ダイアログ経路にも適用**する(`OfferRestoreOnStartup` の
  Restore/確認 OFF 自動復元の両方)=現行バグの根治。

## 4. エラー処理

現行設計(PR #22 §4)の E1〜E10 を統合経路へ引き継ぐ。差分のみ:

| ケース | 現象 | ハンドリング |
|---|---|---|
| E4'(無題・BackupId が辞書に無い) | 編集内容の喪失 | skip+トレース(空タブを作らない=現行 E4 踏襲) |
| E5'(session-state.json 破損/欠落) | Load が null | レイアウトなし扱い。**extras 復元は実施**(dirty 内容はバックアップにあるため「レイアウトだけ失いタブ順が崩れる」に留める=内容は守る) |
| E9'(dirty パスあり・バックアップ欠落) | 同上 | disk 再オープンへ demote+トレース(現行 E9 踏襲) |
| E11(BackupRecord.Content=null の path-only を silent 復元) | >32M 文書 | **disk 再オープンへ demote**(空 dirty を実パスに載せない=§3.3)。無題は skip+トレース |
| E12(レイアウトの BackupId と BackupRecord.OriginalPath の不一致) | 改竄・ズレ | rec.Path 側を OriginalPathValidator で検証して採用(bk 側のパスは信用しない)。検証 NG は無題フォールバック(HIGH-2 踏襲) |
| E13(session-state.json 書込失敗) | ディスクフル等 | 背景ライター経由で失敗通知 → 次 tick 強制再書込(本文の ForceWrite と同方針)。終了時の最終書込失敗はトレースのみ(バックアップ本体は残っているため次回起動は extras 復元で内容は守られる) |

保存経路(§4.3)の cap 系エラーは cap 廃止に伴い削除。バックアップ本文の 32M cap
(BK-M-3・path-only fallback)は現行のまま。

## 5. UI / 設定 / 説明書

### 5.1 設定項目

新設なし。既存の意味が変わる:

- 「起動時に前回開いていたファイルを開く」(基本タブ・既定 OFF): ON で hot exit 一式
  (silent close + silent 統合復元)が有効化。
- 「起動時に復元するか確認する」(`ConfirmRestoreOnStartup`): **OFF 経路(上記設定が OFF)での
  異常終了復元にのみ作用**。ON 時は復元確認自体が存在しないため無効(UI は変更せず説明書で明記
  =タブ間依存の disable 配線は複雑化に見合わない)。

### 5.2 組合せ表(BackupEnabled × RestoreOpenFilesOnStartup)

| Backup | Restore | セッション中 | 終了時 | 起動時 |
|---|---|---|---|---|
| ON | ON | 本文+レイアウト定期退避 | 確認なし(hot exit) | silent 統合復元 |
| ON | OFF | 本文のみ定期退避(現行) | 確認あり(現行) | 孤児提案(現行) |
| OFF | ON | レイアウトのみ定期退避 | **確認あり**(内容を退避できないため) | レイアウトのクリーンタブ+extras(通常は無し)を silent 復元 |
| OFF | OFF | 何もしない(現行) | 確認あり(現行) | 何もしない(現行) |

方針: **バックアップを切ったユーザーの「内容を永続化しない」意思を尊重**し、OFF×ON では
dirty 確認を従来どおり出す(silent close しない)。レイアウト(どのファイルを開いていたか)のみ
復元される。

### 5.3 説明書(たたき台・ユーザー編集版が正の原則で校閲前提)

- 基本タブ表の「起動時に前回開いていたファイルを開く」説明を差し替え:
  「前回終了時に開いていたタブと未保存の変更を、次回起動時にそのまま復元する。
  有効にすると終了時の保存確認は表示されない。アプリが異常終了した場合も同じように復元される」
- バックアップ節の優先順位の段落(「バックアップからの復元が優先されます」)を差し替え:
  「『起動時に前回開いていたファイルを開く』が有効な場合、異常終了時も含めて前回の状態が
  自動的に復元されるため、復元の確認画面は表示されません(『起動時に復元するか確認する』は
  この設定が無効のときにのみ働きます)」
- PR #22 §8.6 で追記した cap 超過時の文(「100 万文字を超える場合は…」)を削除(cap 廃止)。

## 6. テスト計画(CLAUDE.md §5)

### 6.1 L1(Core.Tests)

- `SessionLayoutStore`: Save/Load 往復(空・1 件・多件・日本語パス・SavedAtUtc)/破損 JSON→null/
  欠落→null/Delete 冪等/AtomicFile 経由の原子性(temp 残骸なし)。
- Load 防御: Tabs null→空/null 要素 skip/**201 件→200 件+切り詰め**/Path 空白 skip/
  不正 BackupId→null 化/重複 BackupId→2 個目 null 化/IsActive 複数→先頭のみ/負値 clamp。
- `BackupStore.TryMoveToSessionDir`: flat→session 移動/session→session 移動/欠落→false/
  不正 Id→ArgumentException(HIGH-1 対称)/移動元 dir が空になったら削除される。
- 旧形式移行(§8): 旧 `LastSessionSnapshot`+buffers → 新構造への変換純関数の全分岐
  (dirty パスあり/非 dirty/無題/BufferKey 欠落)。

### 6.2 L3(App.Tests)

- **Reconcile レイアウト書込**: 有効時に書かれる/署名不変なら書かれない(非既定状態から開始=
  Stage 6 教訓)/タブ開閉・アクティブ切替・カーソル移動で署名が変わる/`_sessionRestoreEnabled`
  false なら書かれない/書込失敗→次 tick 再書込。
- **Shutdown(keepForRestore)**: true→自セッション分が残る+layout 残る/false→現行どおり削除+
  layout 削除(**keep/delete のピボットをミューテーション検証重点にする**)。
- **OnFormClosing 分岐**: ON×BackupON→確認なし/ON×BackupOFF→確認あり/OFF→現行(既存テスト維持)。
- **統合復元マトリクス**: dirty パスあり(本文・encoding・Modified=true)/非 dirty パスあり
  (disk オープン・caret)/無題 dirty/無題空枠(LineEnding)/E9' demote/E11 path-only demote/
  E12 パス不一致→rec.Path 検証/extras 追加復元/extras の path-only skip/失敗パス集約/
  IsActive 反映/initialEmpty クローズ/layout null でも extras 復元(E5')。
- **adopt-move 統合**: silent 復元後に旧 session dir が空になり自 dir へ移動済み/
  ダイアログ経路(確認 OFF 自動復元)でも移動する/**移動後に clean 化→Delete でファイルが消える**
  (=現行バグの回帰テスト: 復元→保存→再起動で再提案されない)。
- **旧形式移行**: 旧 LastSession+buffers.json がある初回起動で復元される+旧ファイル削除+
  `LastSession=null` 化。
- 既存の PR #22 テストのうち cap/fall-through/buffers 系は仕様ごと削除し、残り(復元セマンティクス)は
  新経路へ移植する。**設定 OFF の既存テストは全数無変更で緑を維持**(挙動不変の証明)。

### 6.3 L4 / L5

- L4: 対象外(起動/終了時 1 回+30 秒毎の軽量書込。レイアウト JSON は数 KB)。
- L5: SR 経路(UIA プロバイダ・Speech 系)に変更なし=CLAUDE.md §5 基準では省略可。ただし
  終了・起動の UX が変わるため、**NVDA での軽い実機スモーク(ON で閉じる→起動→タブ・
  カーソル位置・「*」表示の読み)を推奨**としてユーザーへ依頼する(必須ではない)。

### 6.4 ミューテーション検証の重点(最終品質パスのスポットチェック)

- Shutdown の keepForRestore ピボット(true/false 取り違え=「消すべきを残す/残すべきを消す」)。
- E11 の demote 条件(`Content is null` の否定変異で「空 dirty 上書き」が復活しないこと)。
- adopt-move の Id 検証(validator バイパス変異が ArgumentException で止まること)。

## 7. セキュリティ考慮(§3 前倒し脆弱性レビュー対象)

- **外部入力パース**: session-state.json は改竄可能入力。§2.3 の防御一式+「レイアウト由来の Id を
  Path.Combine に流さない(照合は LoadAll 結果の辞書のみ)」を不変条件にする。
- **パス検証**: 復元でタブに載せるパスは全経路で `OriginalPathValidator`(HIGH-2)を通す。
  E12 のとおりレイアウトとバックアップのパスが食い違う場合も検証済みの側のみ採用。
- **ファイル切り詰め事故**: E11(path-only を silent で空 dirty 復元しない)。
- **表示無害化**: トレース・集約 Warn に載せるパス/Id は `SanitizeForDisplay.OneLine(200)` 統一
  (BK-L-5 の invariant を維持)。
- 該当タスク(SessionLayoutStore・統合復元・adopt-move)は実装時に**前倒し脆弱性レビュー**を行う。

## 8. 移行(PR #22 形式からの一回限り読み替え)

起動時、`session-state.json` が無く `_settings.LastSession` が非 null の場合のみ:

1. 旧 `LastSessionSnapshot` + `last-session-buffers.json` を読み、純関数
   `LegacySessionConverter.Convert(snap, buffers)` で(SessionLayout, 合成 BackupRecord 群)へ変換
   (BufferKey→合成 Id、本文・CodePage・HasBom・LineEnding・WasModified を対応付け)。
2. 統合復元経路へそのまま流す(合成 BackupRecord は in-memory のみ・ディスクに書かない
   =復元後の Reconcile が新セッション dir へ通常書込する)。
3. 復元後に `LastSession=null`+`LastSessionBuffersStore.Delete`(旧残骸の掃除)。

`AppSettings.LastSession` プロパティと旧 record 型は移行読み取りのためだけに 1 リリース残し、
次のリリースで削除する(申し送りに記録)。`LastSessionBuffersStore` は移行の Load/Delete のみに
縮退させ、Save 経路を削除する。

## 9. 申し送り(follow-up 候補)

- `AppSettings.LastSession`・旧 `SessionTabRecord`・`LastSessionBuffersStore` の完全削除
  (次リリース)。
- 復元件数の Announcer 通知(「前回のタブを N 件復元しました」)は本統合でも見送り
  (PR #22 申し送りを継承)。
- E9'/E11 demote のユーザー通知(「編集途中の内容を復元できませんでした」型)は
  トレースのみを継続(PR #22 §8 final review Minor-3 を継承)。
- 複数インスタンス: session-state.json は last-writer-wins(M9+ 申し送りを継承)。
- **(Task 4 脆弱性レビュー指摘 1・受容)** adopt-move は複数インスタンス同時起動時に他ライブ
  インスタンスのバックアップファイルを物理的に引き抜き得る(引き抜かれた側は内容変化まで再書込
  しない=クラッシュ保護の silent 消失。内容自体は移動先タブに保全)。複数インスタンスは非サポート
  構成(M9+)のため受容し、M9+ でのインスタンス識別(lock ファイル等で LoadAll がライブ session
  dir をスキップ)とセットで解消する。
- **(Task 4 脆弱性レビュー指摘 2・既存)** `AdoptRestored` は Id 検証前に `_map` 登録するため、
  prompt 経路由来の不正 Id が毎 tick の背景書込失敗ループになる(封じ込め済み・traversal なし)。
  堅牢化 follow-up: AdoptRestored 冒頭で `BackupIdValidator.IsValid` を確認し、不正なら新規 Guid
  を採番して登録。
- **(Task 4 脆弱性レビュー指摘 3・既存の姉妹バグ)** 「すべて破棄」(DiscardAll)は自セッション
  dir のみ掃除するため、旧 dir の提案中孤児が物理削除されず最大 30 日再提案+本文残留する
  (BK-M-2 由来)。follow-up: DiscardAll 時に提案 record 群の出所 dir を特定して削除
  (M9+ のライブ判定と同時に設計)。
- **(Task 5 脆弱性レビュー・既存)** OFF 経路の `OfferRestoreOnStartup` confirm=false(確認なし
  自動復元)は Content=null(path-only >32M)レコードを「空 dirty+実パス」で復元し得る
  (E11 相当のハザード・BK-M-3 導入時からの既存挙動)。本統合は「OFF 挙動完全不変」を不変条件と
  するため本ブランチでは変更せず、follow-up として ON 経路と同じ demote(パス正当時 disk
  再オープン)の適用を別途検討する。ダイアログ経路は「本文なし」表示で人間が判断できるため対象外。
- **(Task 5 仕様レビュー・受容)** 設計 §3.3 手順 5 の「IsActive がなければ最初の復元タブを
  Activate」は旧経路と同じく未実装(タブ生成順で最後がアクティブ)。実害はアクティブレコードの
  復元失敗時のみのため受容し、PR description に記載する。
- `MessageBoxUserPrompt` の DI 化(PR #22 Task 13 review Important)は本統合のテスト実装時に
  必要になった時点で先行対応してよい。
- **(最終品質パス M-4)** `MarkDiscarded` の一括適用は `base.OnFormClosing(e)` より前のため、将来
  FormClosing の外部購読者が Cancel する経路が生まれるとマーク残留(解除 API なし)で当該文書が
  保護対象から外れる。現状購読者ゼロ・契約はコード内コメントに明記済み。購読者を追加する際は
  マーク適用位置の再検討が必要。
- Trace observability seam の一括 hook 化(PR #22 Task 14 review Minor-2 を継承)。

## 10. 実装時精密化(2026-07-23 実装計画作成時の追記)

- **§3.2 補強(silent close の安全条件)**: silent close の前提条件に「全 dirty 文書の本文が
  バックアップ可能(TextLength ≤ BK-M-3 の 32M chars)」を追加する。超過 dirty 文書がある場合は
  従来の確認経路へ fall-through する。path-only バックアップ(Content=null)は内容を持たないため、
  silent close → E11 demote の組合せでは編集が**無断で**失われる。旧 cap fall-through の安全原則
  (ユーザーに無断で内容を失わない)を 32M 境界で継承する。
- **§3.3 の API 配置**: レイアウトの Load/Delete・LoadAll・adopt-move は BackupCoordinator の新 API
  (`CollectForSilentRestore` / `DeleteConsumedLayout` / `AdoptRestored`)に集約し、layout パスの知識を
  Coordinator に閉じる(MainForm はパスを知らない)。
- **§8 移行の精密化**: 旧形式で「無題かつ BufferKey なし(cap 超過で枠だけ保存された分)」の
  レコードは変換で skip する(PR #22 E4「空の無題タブを追加しない」の意味論を保存)。統合後の
  新レイアウトにおける「無題かつ BackupId なし=空枠を復元」とは意味が異なることに注意。
  旧 WasModified=false の無題本文は統合後 Modified=true で復元される(軽微な挙動変更・受容)。
- **§3.2 の訂正(Task 6 仕様レビュー発見)**: §3.2 の「No を選んだ dirty タブはアプリが閉じない
  (Cancel 扱い)ため No-discard 除外に相当する状況は発生しない」は誤り。実際の
  `ConfirmDiscardIfDirty` は **No=破棄して続行(Modified 維持)・Cancel=終了中止**。このため
  fall-through 経路で No を選んだタブは定期バックアップ+最終レイアウト経由で次回起動時に
  silent 復活し得る(ON×BackupOFF では空枠復活)。対処として `BackupCoordinator.MarkDiscarded`
  を新設し、明示破棄タブのバックアップ即削除+ReconcileContent 再登録除外+BuildLayout 除外を
  行う(PR #22 M-1「No-discard 除外」の hot exit 機構への移植)。
- **§8 移行の精密化 2(Task 6 実装時発見)**: レガシー移行の合成 BackupRecord はディスクに実体が
  ないため **adopt しない**(adopt すると HasBackup=true なのに実体なし → BackupPlanner が None を
  返し続け、無編集のまま hot exit → 次回起動で E9'/E4' silent 消失になる)。移行パスでは合成 Id
  のみ adopt 対象から除外し、同時に存在する実バックアップ(前回クラッシュ由来の extras 等)の
  adopt-move は維持する(BK-M-2 再提案バグ修正の保存)。合成レコード由来の文書は通常の
  RegisterNew 経路で次 Reconcile が新セッション dir へ書き込んで保護する(§8 の元設計どおり)。
