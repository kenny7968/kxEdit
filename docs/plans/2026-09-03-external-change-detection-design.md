# 外部変更の検知と読み直し確認(M-18)設計書

策定日: 2026-09-03 / ベース: main `37bb5be`(PR #66 = B6 マージ後)

一次資料は `docs/plans/2026-08-22-v0.2-release-bug-audit.md` §6 の M-18
「外部変更検知なし(mtime 比較なし)。他アプリで更新したファイルを Ctrl+S で無言上書き」。
傘設計書 `2026-08-31-v0.2-remaining-work-design.md` §5 は「新機能であり UI 設計判断を伴う」として
次リリースへ送っていたが、**2026-09-03 のリリース可否評価でユーザーが v0.2 に入れると判断した**
(同日、CSV F2 の NUL 切り詰めは「対応しない」と判断済み)。
本書は**策定時スナップショット**(CLAUDE.md §8)。実装時の精密化と実施記録の追記のみ行う。

## 1. 対象

kxEdit で開いているファイルを別のプロセスが書き換えたとき、次の 2 つを行う。

1. **検知して読み直すか確認する**(ユーザー要件)。未保存の編集があるタブでも聞くが、
   文言で損失を伝え、既定ボタンを「読み直さない」側に置く。
2. **保存の直前にも検知して上書きを確認する**(監査 M-18 の本体 = 無言上書きの防止)。

ユーザーの判断(2026-09-03):
- 未保存タブでも警告付きで確認する(「未保存タブでは聞かない」は不採用)。
- 保存直前の確認を入れる。
- 検知の仕組みは案 A(復帰時チェック)。案 B(定期ポーリング)と案 C(`FileSystemWatcher`)は不採用(§5)。

## 2. 現状の機構(実コードで確認)

| 事実 | 場所 |
|------|------|
| 更新時刻の境界付き取得は A-1 で導入済み。リモートは 5 秒プローブを前置し、到達不能ルートを**プロセス寿命**で記憶する | `App/FileTimestampProvider.cs`(`_unreachableRoots` は `HashSet<string>`。「唯一の呼び出し元が起動時復元なので、プロセス寿命で保持して構わない」と doc に明記) |
| 比較の純関数 `BackupStaleness.IsDiskNewer` は「M-18 での再利用を申し送っている」と doc に書かれているが、**片方向(ディスクが新しい)かつ 2 秒許容差付き** | `Core/Backup/BackupStaleness.cs` |
| 読み直しの実体は「文字コードを指定して開き直す」が使う `LoadInto`。読取専用解除・CSV キャッシュ破棄・`CsvMode=false`・`SetOrReplaceSource`・`EmptyUndoBuffer`・`SetSavePoint` まで面倒を見る。`SetOrReplaceSource` はキャレットを 0 へ戻す | `App/FileController.cs` `LoadInto` / `Editor/EditorControl.cs` |
| `DocumentState` はディスクの更新時刻を持っていない | `App/DocumentState.cs` |
| `IUserPrompt` は `OkCancel(text, caption, bool defaultCancel)` と `YesNoCancel` のみ。`OkCancel` は既定側を**呼出ごとにコンパイラが選ばせる**(S-12 / 最終品質パス I-5) | `App/Abstractions/IUserPrompt.cs` |
| ウィンドウ復帰は `MainForm.OnActivated` が `BeginInvoke` でフォーカスを戻している。タブ切替は `DocumentManager.ActiveDocumentChanged` | `App/MainForm.cs:660-677` / `:272` |
| `SaveDocument`(Ctrl+S)は重複タブ検査 → 符号化劣化確認(A-10)→ `WriteToPath`。`WriteToPath` はリモートの 5 秒プローブ → `ConvertEols` → `TextFileService.Save` → `SetSavePoint` | `App/FileController.cs` `SaveDocument` / `WriteToPath` |
| `SetCaretCharOffset` は `SnapAndClamp` で長さに収め、`BringCaretIntoView` まで行う。IME 合成中なら確定してから動く | `Editor/EditorControl.Caret.cs:158-175` |
| `CsvController.TryEnterMode(doc)` は public。パース不能なら入らず発声する | `App/CsvController.cs` |
| `FileTimestampProvider` の生成は `MainForm.cs:302` の 1 か所 | — |

## 3. 設計

### 3.1 状態 —— 観測した更新時刻をタブが持つ

`DocumentState` に `DateTime? LastKnownWriteTimeUtc` を足す。
「本文がディスクと一致していた(と kxEdit が信じている)時点のディスク側 mtime」。
無題タブ・取得失敗(到達不能・権限・例外)は null。

### 3.2 観測点 —— 3 か所

| 経路 | 取るタイミング | 理由 |
|------|----------------|------|
| `LoadInto`(開く・開き直し・読み直し) | **本文を読む前** | 読んでいる最中に書き換えられた場合、mtime は本文より古い値になり**次回の検知で拾える**。読んだ後に取ると、その 1 回の変更を永久に見落とす |
| `WriteToPath`(保存) | **書いた後**(`TextFileService.Save` 成功直後) | 自分の保存で mtime が変わるので、保存後の値を「一致」の基準にする。書込と取得の間に外部が書く窓は残る(§9) |
| `RestoreDirtyFromBackup`(バックアップ復元) | A-1 の `NoteIfBackupStale` が既に取っている値を流用 | 追加 I/O を作らない。復元本文はディスクと一致していないが、「復元時点のディスク」を基準にしておけば以後の外部変更は検知できる |

`LoadInto` の取得は `TryProbeFileExists`(リモート 5 秒プローブ)の後に置く。到達可能と判った直後なので
プロバイダ内の 2 度目のプローブは ms で返る。

### 3.3 比較 —— 完全一致

```
changed = known is DateTime k && disk is DateTime d && d != k
```

- **許容差を置かない。** A-1 の 2 秒は「自前の時計(`DateTime.UtcNow`)とディスク mtime」を比べるための
  もの(FAT の 2 秒粒度・NTP)。本件は**同じファイルシステムが同じファイルに返す mtime 同士**の比較で、
  変更が無ければ同じ値が返る。許容差を置くと「保存の 1 秒後に他プロセスが書いた」を見落とす。
- **両方向。** ディスクが古くなる変更(別ツールで旧版を復元した)も「変更」。`IsDiskNewer` は使わない。
- **どちらかが null なら判定しない**(= 何もしない)。削除・到達不能・無題・取得失敗はすべてここに落ちる。
  削除の通知は v0.2 では行わない(§9)。

### 3.4 検知点 —— 3 か所

| 検知点 | 配線 | 対象 |
|--------|------|------|
| ウィンドウ復帰 | `MainForm.OnActivated` の既存 `BeginInvoke` の**末尾**(既存ガード `IsDisposed / ActiveForm != this / _menuActive` を通過した後) | アクティブタブ |
| タブ切替 | `ActiveDocumentChanged` から `BeginInvoke`。**TabControl の選択変更ハンドラの中でモーダルを出さない**(WinForms の再入)。実行時に「まだそのタブがアクティブ」「フォームがアクティブ」を再確認する | 新しくアクティブになったタブ |
| 保存直前 | `SaveDocument` の重複タブ検査の**後**、A-10 の符号化確認の**前** | 保存しようとしているタブ |

前 2 つは `FileController.CheckExternalChange(Document doc)` に集約する。戻り値:

| 値 | 意味 |
|----|------|
| `Skipped` | 判定しなかった(無題・null・再入中) |
| `NoChange` | 一致 |
| `Reloaded` | 変更あり → 読み直した |
| `Kept` | 変更あり → 読み直さなかった(観測値をディスクの値へ更新し、次の変更まで聞かない) |

**再入ガードを 1 本持つ。** 確認ダイアログはモーダルなので、その message loop の中で
`BeginInvoke` 済みの検知(切替由来)が届きうる。ガード中は `Skipped` を返し 2 枚目を出さない。
ダイアログを閉じると `OnActivated` が再び発火するが、`Reloaded` / `Kept` のどちらでも観測値は
更新済みなので `NoChange` で終わる(= ループしない)。

保存直前は `CheckExternalChange` を通さない(読み直しではなく上書きの確認だから)。
`SaveDocument` 内で観測値とディスクを比べ、変更ありなら `OkCancel(..., defaultCancel: true)`。
キャンセルなら**保存せず false** を返す。`ConfirmDiscardIfDirty` の「はい → SaveDocument」経路でも
false が伝播してタブを閉じない(既存の保存失敗と同じ扱い)。
保存が成功すれば `WriteToPath` が観測値を更新するので、その後の復帰では聞かれない。

`SaveAs` には入れない。同じパスへの SaveAs は A-7 の上書き確認(ファイルが存在すれば無条件に聞く)が
既に掛かる。

### 3.5 読み直し

1. `int caret = doc.Editor.CaretCharOffset;` を先に取る。
2. `LoadInto(doc, path, forcedCodePage: doc.State.Encoding.CodePage)`。
   - 自動判定に戻さないのは、ユーザーが「開き直す」で直した文字コードを勝手に覆さないため。
     外で文字コードが変わっていれば `LoadInto` の U+FFFD 警告が出る(既存)ので気づける。
   - `LoadInto` が false(読めない・ロック中)なら `_prompt.Error` は `LoadInto` が出す。観測値は
     更新されないので次の復帰で再度聞く(ユーザーは失敗を知っているので妥当)。
3. `doc.Editor.SetCaretCharOffset(caret)`。クランプと可視化は `SetCaretCharOffset` が持つ。選択は解除される。
4. `_openedFresh(doc)`(開き直しと同じ: 設定次第で .csv の自動モード)。
5. `Reloaded` を返す。**発声と CSV モードの復帰は MainForm が行う**(§3.7)。Undo 履歴は消える(開き直しと同じ)。

### 3.6 `IUserPrompt.YesNo`

```csharp
/// <summary>はい/いいえ(警告アイコン)。はいで true。既定側は呼出ごとに明示させる(OkCancel と同じ機構)。</summary>
bool YesNo(string text, string caption, bool defaultNo);
```

`MessageBoxUserPrompt` は `defaultNo ? Button2 : Button1`。`FakePrompt` は `YesNoResult` と
`YesNoCalls`(caption, defaultNo)を記録する(`OkCancelCalls` と対称)。

### 3.7 MainForm の配線

```csharp
private void CheckExternalChangeOnActive()
{
    var doc = _docs.Active;
    if (doc is null) return;
    bool wasCsv = doc.State.CsvMode;
    var outcome = _file.CheckExternalChange(doc);
    if (outcome != ExternalChangeOutcome.Reloaded) return;
    if (wasCsv && !doc.State.CsvMode)
        _csv.TryEnterMode(doc);      // LoadInto が false に落とすので戻す。自動モードで既に入っていれば no-op
    _announcer.Say("読み直しました");
}
```

CSV のセル位置は `TryEnterMode` がキャレットから導出するので、§3.5-3 でキャレットを戻しておけば
近い位置に戻る。F2 編集中は起こらない(ウィンドウを離れた時点で `OnLostFocus → CancelEdit`、
タブ切替は `BeforeActiveChange` が `AbortEdit`)。

### 3.8 `FileTimestampProvider` の到達不能記憶を TTL 化

`_unreachableRoots` を `Dictionary<string, DateTimeOffset>`(期限)にし、既定 **60 秒**。
`TimeProvider` を ctor で受ける(既定 `TimeProvider.System`)。

- 従来のプロセス寿命のままだと、一度落ちた共有の文書は**再起動まで検知が黙って止まる**。
- TTL 無しだと、落ちた共有の文書を開いたまま Alt+Tab するたびに 5 秒止まる。
- 60 秒なら最悪「1 分に 1 回 5 秒」。A-1 の起動時復元は数秒で終わるので挙動は変わらない
  (既存テスト `UnreachableUncRoot_IsProbedOnlyOnce` は TTL 内の話としてそのまま成立する)。

## 4. 文言

名前を先頭に置き、問いを末尾に置く(SR は頭から読む。A-10 と同じ語順)。名前は
`SanitizeForDisplay.OneLine(doc.State.DisplayName, 80)` を通す(ファイル名は外部由来)。

| 場面 | 文言 | caption | 既定 |
|------|------|---------|------|
| 復帰・切替、未保存なし | `'name' は kxEdit の外で変更されました。読み直しますか?` | `ファイルの変更` | はい |
| 復帰・切替、未保存あり | `'name' は kxEdit の外で変更されました。読み直すと、このタブの未保存の変更は失われます。読み直しますか?` | `ファイルの変更` | いいえ |
| 保存直前 | `'name' は kxEdit で開いた後に外で変更されています。上書きすると、その変更は失われます。上書きしますか?` | `上書きの確認` | キャンセル |
| 読み直し後の発声 | `読み直しました` | — | — |

未保存なしで既定を「はい」にする理由: 失うものが Undo 履歴とキャレット位置だけで、
`IUserPrompt.OkCancel` の doc が言う「押し間違えても失うものが無い」側に当たる。
未保存ありは本文を失うので「いいえ」。保存直前は他人の変更を失うので「キャンセル」。

## 5. 採らなかった案

| 案 | 落とした理由 |
|----|--------------|
| B: バックアップ tick(既定 300 秒)に相乗りして全タブを定期チェック | ユーザー操作と無関係な瞬間にモーダルが出る。SR ユーザーには入力中の割り込み。リモートの複数タブで 5 秒プローブが積み上がる |
| C: `FileSystemWatcher` | ネットワーク共有・クラウド同期フォルダーで通知が来ない/重複する。背景スレッドからの UI 同期と監視オブジェクトの寿命管理が増える。v0.2 の最後に入れる変更として重い |
| 比較に 2 秒許容差(`BackupStaleness.IsDiskNewer` 流用) | §3.3。同じ FS の mtime 同士なので不要で、置くと見落としを作る |
| 読み直しを自動判定(`forcedCodePage: null`) | §3.5。ユーザーが直した文字コードを覆す |
| 未保存タブでは聞かない | ユーザー判断で不採用 |
| サイズ(`Length`)も比較する | mtime が同一のまま内容だけ変わる書込は、NTFS では同一クロックティック(数 ms)内、FAT では同一 2 秒粒度内の書換に限られ、人の操作では実質起きない(**未実測**。残余として §9 に置く)。`IFileTimestampProvider` の契約を広げる代償に見合わない |
| 設定で OFF にできるようにする | YAGNI。§9 |
| 削除されたときに通知する | 「変更」と「削除」で文言と選択肢が別になる。§9 |

## 6. テスト(CLAUDE.md §5)

L3 = `tests/kxEdit.App.Tests`。`FileControllerTests` の既存ハーネス(実ファイル + `FakePrompt` /
`FakeFileTimestampProvider` / `FakeReachabilityProbe`)を使う。**ミューテーション検証はしない**
(CLAUDE.md §4-A: ファイル I/O とイベント配線は禁止対象)。

### 6.1 観測値の捕捉

- 開くと `LastKnownWriteTimeUtc` がプロバイダの値になる。無題は null。
- **読む前に取ること**: プロバイダ Fake を「問い合わせられた瞬間にファイルへ追記し、追記前の時刻を返す」
  ようにする。開いた本文は追記後、観測値は追記前 → 次の `CheckExternalChange` が(ディスクを追記後の
  時刻にして)`Reloaded` になる。取る順序を読んだ後へ変える変異でだけ落ちる。
- 保存後に観測値が保存後の値へ更新される(保存前後でプロバイダの返す値を変える)。
- バックアップ復元(dirty)で A-1 の値が入る。パス検証 NG の復元は null。

### 6.2 `CheckExternalChange`

- 無題 → `Skipped`、観測値 null → `Skipped`、ディスク null → `Skipped`(いずれもプロンプト無し)。
- 一致 → `NoChange`(プロンプト無し)。
- 変更あり・未保存なし・はい → `Reloaded`。本文が新しい内容、`Modified=false`、観測値が更新、
  キャレットが元の位置(**非 0 の位置から始める**。CLAUDE.md §4-B)、長さを超えていたらクランプ。
  文言に「失われます」を含まず `defaultNo=false`。
- 変更あり・未保存あり → 文言に「未保存の変更は失われます」を含み `defaultNo=true`。いいえ → `Kept`、
  本文と `Modified` は不変、観測値がディスクの値になり、**2 回目の呼出は `NoChange` でプロンプトが出ない**。
- はい → `LoadInto` 失敗(ファイルをロックして読めなくする)→ `Error` が出て観測値は不変。
- 再入: `FakePrompt` の `YesNo` の中から再度 `CheckExternalChange` を呼ぶ → 内側は `Skipped`。

### 6.3 保存直前

- 変更あり・キャンセル → false、ディスクの内容が不変、`Modified` のまま、`OkCancelCalls` の
  `DefaultCancel=true`。
- 変更あり・OK → 保存され観測値が更新。
- 一致 → プロンプト無しで保存。
- `ConfirmDiscardIfDirty` の「はい」→ キャンセル → false(閉じない)。

### 6.4 `FileTimestampProvider` の TTL

`FakeTimeProvider` で時刻を進める。TTL 内は再プローブしない(既存テストの意味)、TTL を過ぎたら
再プローブする、到達可能に戻れば値が返る。

### 6.5 MainForm

`MainFormSmokeTests` の流儀で `CheckExternalChangeOnActive` を internal seam から叩く:
`Reloaded` で `読み直しました` が発声される / CSV モードだったタブがモードへ戻る / `Kept` では発声しない。

## 7. L5(実機 SR 検証)—— 必要

ダイアログと発声を足す = App の Speech 系に触れる(CLAUDE.md §5)。チェックリストは実装計画の
最終タスクで起こす。項目の骨子:

1. 別プロセス(メモ帳)で書換 → kxEdit へ戻る → NVDA が文言を逐語で読む(未保存なし)。Enter で読み直され `読み直しました` が読まれる。
2. 未保存ありの文言。**Enter で読み直されない**(既定がいいえ)。いいえ後に Alt+Tab を往復しても聞かれない。
3. 保存直前の文言。**Enter で上書きされない**(既定がキャンセル)。ディスクの内容が無傷。
4. タブ切替で検知される。
5. CSV モードのタブを読み直すとモードのまま、セル位置が近い位置に戻る。キャレット行が保たれる。
6. OneDrive 配下のファイルで自分の保存の直後に Alt+Tab を往復しても聞かれない(同期クライアントが mtime を触らないことの観測)。
7. 到達不能な共有の文書を開いたまま Alt+Tab → 1 回目は最大 5 秒、直後の往復は待たない(TTL)。

## 8. セキュリティ観点

- パスは `State.Path`(正規化済み・検証済み)のみ。新しい入力面は無い。
- 文言に載る名前は無害化する(§4)。
- mtime はセキュリティ制御ではない(`BackupStaleness` の doc と同じ)。攻撃者がファイルを書けるなら
  mtime も自由に付けられる = 検知を抑止できるが、それは「本文を上書きされる」以上の能力ではない。
- 読み直しは既存の `LoadInto`(サイズ上限・プローブ・例外フィルタ込み)を通る。
- CLAUDE.md §3-4 の前倒し**脆弱性レビュー**を `CheckExternalChange` のタスクで行う(ファイル読込 +
  パス由来の文言)。

## 9. 申し送り

- **削除の通知**。ディスクから消えたときは何も言わない(null → 判定しない)。次に保存すれば作り直される。
- **設定で OFF**。要望が出てから。
- **定期ポーリング / `FileSystemWatcher`**(案 B / C)。前面のまま変更されたケースは保存直前の確認が受ける。
- **保存後の観測窓**: `TextFileService.Save` と mtime 取得の間に外部が書くと、その 1 回は見落とす
  (観測値が本文より新しくなる)。逆順(書く前に取る)は自分の保存を「変更」と誤検知するので採れない。
- **保存直前の TOCTOU**: 確認して OK → `WriteToPath` の間に外部が書けば上書きする。原理的に閉じない。
- **mtime が同一値のまま内容が変わる書換**(同一クロックティック内 / FAT の同一 2 秒粒度内)は検知しない(§5)。
- 本書は完了をもって役目を終え、知見は実施記録節へ書く(CLAUDE.md §8)。

## 10. 工程

CLAUDE.md §3 の通常工程(3 プロジェクトに跨らないが、新しい seam と配線を足す)。

1. Task 1: `IUserPrompt.YesNo` + `MessageBoxUserPrompt` + `FakePrompt`
2. Task 2: `DocumentState.LastKnownWriteTimeUtc` と 3 観測点(§3.2)+ §6.1
3. Task 3: `CheckExternalChange` + `ExternalChangeOutcome` + §6.2 —— **前倒し脆弱性レビュー**
4. Task 4: 保存直前ガード + §6.3
5. Task 5: `FileTimestampProvider` の TTL + §6.4
6. Task 6: MainForm 配線(復帰・切替・発声・CSV 復帰)+ §6.5 + L5 チェックリスト
7. 最終ブランチレビュー 2 パス(別エージェント)→ `tools/pre-merge-check.ps1` EXIT 0 → PR
