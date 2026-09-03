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

## 11. 実施記録(2026-09-03)

本節は追記であり、§1〜§10 の策定時スナップショットは書き換えていない(CLAUDE.md §8)。

### 11.1 結果

Task 1〜6 を計画どおり 1 タスク 1 実装エージェントで実施し、各タスクで仕様レビュー(別エージェント)、
Task 1 / 2 / 3 / 5 でコード品質レビュー、Task 3 で前倒し脆弱性レビューを行った。指摘はすべて
別 fixup commit で反映した(却下は §11.4)。**L5 は未実施**(§11.6)。

テスト件数は `dotnet test tests/kxEdit.App.Tests -c Release` の実行結果が正であり、本書には引用しない
(数字は網の増減で腐る。PR #62 §8.45 の教訓)。ビルドは `-warnaserror` で 0 警告。

### 11.2 計画からの逸脱(すべてレビューまたは実装者のセルフレビューで判明・実測で確定)

| # | 逸脱 | 理由 |
|---|------|------|
| 1 | `ExternalChangeOutcome` に **`ReloadFailed`** を足した(§3.4 は 4 値) | `LoadInto` 失敗時に `Reloaded` を返すと MainForm が「読み直しました」を発声して虚偽になり、`Kept` を返すと「次まで聞かない」と読める。観測値は両方とも不変なので、エラーダイアログを閉じた直後の `OnActivated` で同じ確認が再び出る(一過性ロックの再試行。「いいえ」で止まる) |
| 2 | **`DocumentState.AcknowledgedWriteTimeUtc` を分離**した(§3.4 は「いいえ」で観測値を更新) | 実装者のセルフレビューで、「いいえ」が `LastKnownWriteTimeUtc` を更新すると直後の Ctrl+S が保存直前の確認を素通りし、**未編集タブでも古い本文が新しいディスクを無言で上書きする**ことが判明した(M-18 が塞ぐ喪失が 1 手ずれて残る)。「聞き直さない」ための値と「本文の基準」を分け、保存直前の確認は基準だけを見る。基準が更新される(開く・読み直す・保存する)たびに憶えた値は null へ戻す |
| 3 | `FileTimestampProvider` のプローブを **`ProbeSaveTargetWithTimeout`** に替えた(§3.8 は TTL 化のみ) | 脆弱性レビュー L-1: `ProbeFileExistsWithTimeout` は `File.Exists` 意味論で「到達不能」と「到達できるが不在」を区別できず、別ツールの delete→recreate や rename 保存の途中でルート全体が記憶され、その共有の全文書の検知が黙って止まる。`(Reachable, FileExists)` を分けて返す保存先用プローブを読む側でも使い、`!Reachable` だけを記憶する。残余: `Reachable` は「ファイルまたは親フォルダーが在る」なので、親フォルダーごと消えた/改名された場合は TTL の間記憶される |
| 4 | 到達できたら記憶を **`Remove`** する(計画は「消さなくてよい」) | 計画の根拠「期限後は必ず上書きされる」は復旧時には偽(上書きは到達不能分岐でしか起きない)。壁時計の逆行で期限切れの記録が復活する窓も同時に閉じる(ただし一度も再照会されていない根は残る。§11.5) |
| 5 | `Check_PromptSanitizesDisplayName` を **`StringComparison.Ordinal`** に | xUnit の `Assert.DoesNotContain(string, string)` は culture 比較で U+202E を無視可能文字として扱い、**どんな文字列でも位置 0 で空一致する** = 計画の assertion は無害化の有無に関わらず常に赤で、網として成立していなかった |
| 6 | 計画 Task 3 の `Assert.Contains(doc, host.OpenedFresh)` を回数固定に | `Open` ヘルパの `TryOpenOrActivate` が初回に `_openedFresh` を呼ぶため、検知前から入っていて空振りだった(仕様レビューが発見) |
| 7 | Task 2 のテスト `Save_CapturesTimestampAfterWriting` の fixture を修正 | 保存前に Fake の更新時刻を進めていたため、Task 4 のガードには「開いた後に外で変更された」と見えていた(Fake 既定 OK で素通り)。実ファイルの意味論(新しい本文が載ってから mtime が動く)に揃え、`Assert.Empty(OkCancelCalls)` を足した |
| 8 | `FileTimestampProviderTests` のリモート実 I/O を `\\localhost\<無い共有>` に | `\\unreachable-host` は名前解決で 1〜3 秒かかる(実測)。localhost は 5〜13 ms |
| 9 | Task 4 のテスト 15 行が **`\uXXXX` エスケープ**で書かれていた(仕様レビューが発見・fixup で復号) | Edit ツールが、ファイル側が U+202E をバックスラッシュ u 形式のエスケープで持つ行を生の文字で照合したときに、new_string の非 ASCII を全部エスケープして書く。傘設計書 §11.1 の `BackupCoordinatorTests` 207 行の破損と同型。以後は commit 後に `grep -c '\\u[3-9]'` = 0 を確認する手順にした |
| 10 | CSV モードの読み直し後、**セル位置を `(CsvRow, CsvCol)` で戻す**(`CsvController.TryGoToCell` を新設し MainForm が呼ぶ) | §3.7 の「`TryEnterMode` がキャレットから導出するので近い位置に戻る」は**偽**だった(Task 6 の仕様レビューが L5 項目 5 を 1 手ずつ追って発見)。CSV モード中はキャレットがセルに追従しない(`ApplyCell` は強調と可視化だけ。キャレットを動かすのは `ExitMode` のみ)ので、キャレット由来では常に先頭セルへ戻る。L5 項目 5 は正しいビルドで FAIL する形だった(PR #62 Critical-1 の裏返し)。自動 CSV モード ON の経路でも `_openedFresh` が先頭セルへ入り直すので同じ手で戻す。セルが無くなっていれば先頭セルのまま(`TryEnterMode` の発声が残る)。発声は「読み直しました → CSVモード オン … → セル」の順で同期に連続するため、`UiaAnnouncer` の窓により SR に届くのは 1 件目と最後(実測は L5) |
| 11 | `IFileTimestampProvider` を **`GetLastWriteTimeUtc`(記憶を使う)/ `ProbeLastWriteTimeUtc`(記憶を無視して有界プローブ・到達できたら記憶を捨てる)** の 2 本に分け、開く・保存・保存直前の確認は後者を使う | 最終脆弱性レビュー V-1(Low): 共有が落ちて記憶 → 復旧 → 60 秒以内に開く/保存、の順で基準が **null** になり、その文書は TTL が切れても次の基準捕捉まで復帰・切替の確認も保存直前の確認も出ない(main と同じ無防備状態へ戻る)。基準を取る経路は直前・直後に実 I/O を成功させているので記憶を信じる理由が無い。記憶を使うのは復帰・切替の検知と A-1 の起動時復元だけ。落ちた共有への Ctrl+S は保存直前 5 秒 + `TryInspectSaveTarget` 5 秒 = 最悪 10 秒(受容) |
| 12 | 自動 CSV モード ON の読み直し中は `AutoEnterCsvMode` を抑止し(`_reloadingCsv`)、MainForm が発声の後にモードへ戻す | 最終コード品質パス Q-1: 自動モードでは `LoadInto` 内の `_openedFresh` が先に「CSVモード オン …」を発声し、`UiaAnnouncer` の 50 ms 窓で「読み直しました」が間引かれていた(手動モードと発声列が違う)。加えて解析不能時に `TryEnterMode` が 2 回走っていた。抑止で手動 / 自動の発声列を「読み直しました → CSVモード オン … → セル」に揃える |

### 11.3 「網が張れない」と判断したもの

- **`OnActivated` / `ActiveDocumentChanged` からの実配線**。実際のウィンドウ活性化が要り、テストハーネスでは
  `Form.ActiveForm` が null なので `BeginInvoke` 先が早期 return する。MainForm 側のテストは seam
  (`CheckExternalChangeOnActiveForTest`)で本体(発声・CSV モード復帰)だけを叩く。配線は L5 項目 1 / 4 が担う。
- **`MessageBox` の既定ボタン**(はい / いいえ / キャンセルのどちらが Enter で選ばれるか)。CLAUDE.md §4-A
  により GUI は変異検証禁止で、`FakePrompt` は `defaultNo` / `defaultCancel` の値を記録するだけ。L5 項目 2 / 3。

### 11.4 却下・受容した指摘

| 指摘 | 扱い |
|------|------|
| `NoteIfBackupStale` の戻り値+副作用(command-query 混在) | YAGNI で受容。private・呼出 2 か所・`Assert.Single(Queries)` が追加 I/O なしを守る |
| `_checkingExternalChange` をフィールド群の先頭へ / 基準更新と憶えた値のリセットをヘルパへ集約 | 現状維持。`AcknowledgedWriteTimeUtc` の xmldoc が「基準更新のたびに null へ戻す」を明記しており、代入 4 か所のうち復元 2 か所は新規 doc への代入(構築時 null) |
| 読む側が保存先プローブの親フォルダー存在確認を余分に払う | 受容。同じ 5 秒境界の内側で SMB 往復が 1 回増えるだけ |
| `YesNoResult` の既定 true(許可側) | 受容。`OkCancelResult` と同じ流儀で、拒否経路は明示的に false を入れる(doc に明記) |
| `Kept_DoesNotAnnounce` が既定の空文字列から始まる no-change テスト(最終品質パス Q-7) | 受容。MainForm の発声を先に鳴らす seam が無く、変異「Kept でも発声する」は捕まる |
| `CheckExternalChange` は読み直しまで行うので照会に読める名前(Q-12) | 受容。xmldoc が明示している |
| 同じパスへの SaveAs は M-18 を通らず A-7 の汎用文言だけ(最終脆弱性パス V-4) | §3.4 の決定どおり受容。`defaultCancel` の確認自体はある |
| 復元タブの基準が「復元時点のディスク」で本文(バックアップ)と一致しない(V-5) | §3.2 の決定。A-1 の集約警告が通知を担う |

### 11.5 残余・申し送り(§9 への追加)

- **到達不能記憶の効き方(逸脱 11 の後)**: 記憶を見るのは復帰・タブ切替の検知と A-1 の起動時復元だけ。
  共有が落ちて記憶されている 60 秒間は復帰・切替の確認が出ない(判定しない)が、開く・保存・保存直前の確認は
  記憶を無視して有界プローブするので基準が null に汚染されず、保存直前の確認も生きている。残るのは
  「到達可能だが遅い共有で保存直前のプローブが 5 秒でタイムアウトした 1 回の保存」で、その保存は確認なしで
  上書きする(A-1 の復元では null = 従来どおり復元で安全側だが、M-18 では安全側ではない)。
- **壁時計の逆行**: 期限切れ後に一度も再照会されていない根は `until` を持ったまま残るので、経過時間より大きい
  逆行(手動変更・大幅 NTP 補正)があると再び抑止される。単調時計にすれば閉じるが Fake の改修が要り割に合わない。
- **読み直しは `LoadInto` の副作用を継承する**: `RegisterRecent` で最近使ったファイルの先頭へ動く(ユーザー操作
  でない「ウィンドウ復帰」で設定が永続化される)。開き直しと同じ挙動として受容。U+FFFD 警告が YesNo の直後に
  2 枚目のダイアログとして出うる(既存挙動)。
- **既存の穴の露出頻度が上がる**: ローカルディレクトリの junction / symlink が UNC を指す場合、
  `RemotePathDetector.IsRemote` は `DriveType.Fixed` を見て false → プローブ無しで SMB タイムアウト(約 60 秒)。
  従来は「開く/保存」時だけだったのが Alt+Tab ごとになる。A-15〜A-17 の残余で本ブランチでは扱わない。
- **`FileTimestampProvider.cs` の「壁時計が逆行しても期限切れの記録が復活しない」**は「復旧を確認した根」に
  限れば真、一般命題としては上記のとおり範囲が広い(Task 5 再確認の任意指摘)。最終レビューで文言を狭める。
- Fake の `TimeProvider` 既定は `TimeProvider.System`(`UiaAnnouncer` と同型。`BackupCoordinator` は必須引数)。

### 11.6 セキュリティ(前倒し脆弱性レビュー・Task 3)

**Critical / High / Medium ゼロ。GHSA 不要**(新しい入力面・権限昇格・情報漏えい・無言喪失経路のいずれも
生まれていない)。Low 2 件(不在と到達不能の混同 / 陳腐化コメント)は Task 5 に同梱して解消した。
検証済み: 文言の外部由来文字列は `DisplayName` のみで `SanitizeForDisplay.OneLine(…, 80)` を通る /
読み直しは `LoadInto` の防御(プローブ・512 MB 上限・例外フィルタ)をすべて通り、パスは検証済み `State.Path`
を呼出冒頭でローカルに固定する / 新しい非有界 I/O は無い(リモートは 5 秒プローブ前置) / mtime は
「本文を書ける」以上の能力を攻撃者に与えない(`AcknowledgedWriteTimeUtc` の抑止も同様) / 再入ガードは
`try/finally` で戻り、モーダル中に文書を閉じる posted 経路は無い。
レビュー時点で「まだ存在しない保護」(保存直前の確認)を現在形で書いていたコメント 4 か所は、Task 4 の着地で
真になった(最終レビューで再確認する)。

### 11.7 L5(実機 SR 検証)—— 未実施

チェックリストは `docs/plans/2026-09-03-external-change-detection-l5-checklist.md`(9 項目)。§7 の 7 項目に、
ロック中ファイルでの再試行ループ(逸脱 1)と、「開き直す」で直した文字コードが読み直し後も保たれること(§3.5-2)
を足した。項目 7(OneDrive)と項目 8(TTL の秒数)は修正前と弁別できない観測項目として明記してある。
傘設計書 §7.1 の台帳へは**このファイルの実数**を記録すること。

### 11.8 最終ブランチレビュー(2 パス・別エージェント別起動)

- **コード品質パス**: Critical ゼロ / Important 3(自動 CSV モードの発声順と xmldoc の偽 = 逸脱 12 /
  記憶中の保存で基準が null になる記述漏れ = 逸脱 11 で解消 / `Remove` の根拠文言が広すぎる+網なし)/
  Minor 9。Release と Debug の両構成で全緑を実測。ミューテーション検証のスポットチェックは CLAUDE.md §4-A
  の禁止対象(ファイル I/O・プロンプト・イベント配線)のため免除し、代わりに網の有無を読解で確認した。
- **脆弱性パス**: Critical / High / Medium ゼロ。Low 1(V-1 = 逸脱 11)/ Info 4(受容。§11.4)。
  **GHSA 不要**。検証済み: 保存直前ガードの網羅(`WriteToPath` の呼出元は `SaveDocument` / `SaveAsDocument`
  のみ)/ mtime 偽装は FILE_WRITE_ATTRIBUTES が要り本文を書ける主体と同じ / `Form.ActiveForm` はネイティブ
  `MessageBox` 中は null なので posted 検知が別モーダルに重ならない / テスト seam は `InternalsVisibleTo`
  (`kxEdit.App.Tests`)のみで本番は常に `MessageBoxUserPrompt` / 読むのは `State.Path` だけ。
- 指摘はすべて 1 つの fixup commit と L5 チェックリストの docs commit で反映し、レビュアーが再確認した。
