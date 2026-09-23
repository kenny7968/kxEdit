# モードメニュー項目へのショートカットキー追加（設計書）

策定日: 2026-09-23

## 背景・目的

モードメニュー（`MainForm.BuildMenu`）の 2 項目「マークダウンプレビュー」「CSVモード」は
ショートカットキーを持たず、`Alt+M` → `P` / `C` のメニュー操作でしか起動できない。
他の主要機能（新規・開く・保存・検索・置換・grep・折り返し整形・行へ移動・現在位置の読み上げ）
はいずれもキー単独で到達できるので、モードだけが動線として一段深い。

SR ユーザーはメニューを開く間ずっとメニュー項目を読み上げられるため、モード切替の
コストが相対的に大きい。キー単独の動線を与える。

## 決定事項

### キー割り当て

| キー | 機能 | 通常編集中 | CSVモード中 |
|---|---|---|---|
| `Ctrl+Shift+M` | マークダウンプレビュー | プレビューを開く | **無反応**（発声もしない） |
| `Ctrl+Shift+K` | CSVモード | CSVモードへ入る | **「現在CSVモードです」を発声するだけ** |

どちらも `Esc` で閉じられる（プレビュー = JS 経由の `close`、CSVモード = PR #79 の `ExitMode`）
ため、**モード切替のトグルにはしない**。既にそのモードにいるときは「今どのモードか」を
伝えるだけに留める。

#### `Ctrl+Shift+J` は使えない（ブレストで判明・当初案からの変更）

当初案は「マークダウンプレビュー = `Ctrl+Shift+J`」だったが、**`Ctrl+Shift+J` は既存の
「折り返し整形（禁則処理）」が使用中**である（`MainForm.cs` 編集メニュー、
`説明書/kxEdit説明書.md` のキー表・本文の計 3 箇所に記載済み、2026-06-28 設計書で
「既存ホットキー一覧と未衝突」として選定されたもの）。

同一の `ShortcutKeys` を 2 つの `ToolStripMenuItem` に登録しても WinForms は例外を出さず、
**どちらか一方だけが発火して片方が黙って死ぬ**。既存機能のキーと説明書を壊さないため、
プレビューは `Ctrl+Shift+M`（Markdown の M）に変更する。`Ctrl+Shift+K` は未使用で衝突なし。

`Ctrl+Shift+P` も未使用だが、「現在位置を読み上げ」の `Ctrl+Alt+P` と指の形が近く
誤打を誘うため採らない。

### 実装方式: `ToolStripMenuItem.ShortcutKeys` に登録する

本リポジトリには 2 つの既存パターンがある。

- **A**: `ShortcutKeys` に登録（`Ctrl+N` / `Ctrl+O` / `Ctrl+F` / `Ctrl+Shift+J` 等）
- **B**: `ShortcutKeyDisplayString` で表示だけして `ProcessCmdKey` で処理
  （`F3` / `Shift+F3` / `Ctrl+G` / `Ctrl+Alt+P`。二重発火の回避が目的）

本件は **A** を採る。`ShortcutKeys` はそのメニュー項目の `Click` を起こすので、
**キーとメニュークリックが必ず同一挙動になる**（B だと「キーではモードへ入るだけ・
メニューではトグル」のようにズレが残り、SR ユーザーが 2 つの動線で別の結果を得る）。
メニューへのキー表示も自動で付く。

ガードは**すべてハンドラ側**に置く（後述）。

### 意図的な挙動変更（2 件）

CLAUDE.md §2「意図的な挙動変更は設計書または PR description に必ず文書化する」に基づき明記する。

1. **モードメニュー「CSVモード」の再クリックで OFF にできなくなる。**
   ハンドラが `ToggleMode()` → `EnterMode()` に変わるため。CSVモードの終了は
   PR #79 で入った `Esc` に一本化される。`Checked`（モード中のチェック表示）は
   状態の提示として残すので、「今 CSVモードにいる」ことはメニューからも読める。
2. **CSVモード中はマークダウンプレビューが開けなくなる**（キー・メニュークリックとも）。
   従来は CSVモード中でもメニューから開けた。CSV として読んでいる本文を
   マークダウンとしてレンダリングする意味は薄く、「モード中は今のモードに留まる」
   という本件の規則に揃える。

### `Enabled` を CSVモードで落とさない（重要）

「CSVモード中はプレビュー項目を `Enabled=false` にして灰色で見せる」は**採らない**。

- 無効な `ToolStripMenuItem` は `ShortcutKeys` が発火しない
  （`ToolStripManager.ProcessShortcut` が `Enabled` を見る）。
- `Enabled` の更新は `mode.DropDownOpening` でしか走らない。よって
  **CSVモード中にモードメニューを開く → `Esc` でモードを抜ける → `Ctrl+Shift+M`**
  の順で操作すると `Enabled=false` が残ったままで、プレビューが**黙って開かなくなる**。

したがって `mdPreview.Enabled = _docs.Active is not null` は**現状のまま据え置き**、
CSVモード判定はハンドラ内だけで行う。

既存の `Active is not null` も同じ陳腐化を持つが、**最後のタブを閉じるとアプリが終了する**
（`MainForm.CloseActiveTab` が `_docs.Count == 0` で `Close()`）ため実行時に `Active` が
null になることはなく、実害はない。

### 発声

`CsvAnnounceFormatter`（`kxEdit.Core`）に定数を 1 本追加する。既存の発声文言はすべて
ここに集約されており、L1 から参照できる。

```csharp
/// <summary>既に CSVモード中に CSVモードのショートカットを押したときの読み上げ。</summary>
public const string ModeAlreadyOn = "現在CSVモードです";
```

### 公開 API は `EnterMode()` を新設する（`ToggleMode()` を流用しない）

`Esc` の `ExitMode()` を新設したとき（2026-09-23 csv-escape-exit 設計書）と同じ方針。
「このキーは入る専用」という意図をコードに残す。

```csharp
/// <summary>CSVモードへ入る（Ctrl+Shift+K）。既にモード中なら現在のモードを発声するだけで
/// トグルしない。アクティブ文書なし・F2 編集中は何もしない（冪等）。</summary>
public void EnterMode()
{
    var doc = _docs.Active;
    if (doc is null || _editor.IsEditing)
        return;
    if (doc.State.CsvMode)
    {
        _announcer.Say(CsvAnnounceFormatter.ModeAlreadyOn);
        return;
    }
    TryEnterMode(doc);
}
```

- **F2 セル編集中は発声もしない**。`ToggleMode()` / `ExitMode()` の既存ガードと揃える
  （編集オーバーレイ中にモード系の発声を割り込ませない）。
- 進入処理は既存の `TryEnterMode(Document)` をそのまま使う。読取専用化・UIA 抑止・
  初期セル確定・`ModeOn` 発声・解析不能時の `ParseError` 発声は**すべて現状のまま**。

### プレビュー窓側の「現在マークダウンプレビューです」は実装しない

要件では「各モードがアクティブなときは現在のモードを発声する」だが、
**マークダウンプレビュー側は到達不能**である。

- プレビューは `ShowDialog(this)` の**モーダル窓**なので、開いている間 `MainForm` は
  キーを受け取れない。
- プレビュー窓の中でも WebView2 がフォーカスを持つため、WinForms の
  `ProcessCmdKey` にキーが届かない。`Esc` ですら
  `AddScriptToExecuteOnDocumentCreatedAsync` で JS の `keydown` を拾い
  `postMessage('close')` で戻している。

発声のために JS 注入経路と `IAnnouncer` の注入を増やすコストに対し、
モーダルゆえ「二度押しで開き直る」事故も起きないため、**実装しない**。
CSVモード側の発声だけを実装する。

### CSVモード中の `Ctrl+Shift+M` は発声もしない

「現在CSVモードです」と発声する案もあったが、**無反応**にする。要件の
「他は何もしない」をそのまま適用し、モード中に反応するのは**そのモード自身のキーだけ**
という規則に揃える。

## キー経路の確認（既存ガードとの関係）

`Ctrl+Shift+M` / `Ctrl+Shift+K` はいずれも `CsvCommands.ByKey` に無いので、
`MainForm.ProcessCmdKey` 冒頭の CSV 素キー横取り分岐を素通りし、`base.ProcessCmdKey`
→ メニューショートカット処理へ落ちる。したがって以下が成立する。

- CSVモード中でもメニューショートカットは届く（`EnterMode()` が発声だけして返す）。
- F2 編集オーバーレイ表示中（`_csv.IsEditing`）も届くが、`EnterMode()` と
  `ShowMarkdownPreview()` の両ガードで何もしない。
- フォーカスがタブ列にあるときもメニューショートカットは届く（フォーム全域で有効）。
  `Active` があれば通常どおり動く。

## 実装

1. `src/kxEdit.Core/Csv/CsvAnnounceFormatter.cs` — `ModeAlreadyOn` を追加。
2. `src/kxEdit.App/CsvController.cs` — 公開 `EnterMode()` を追加。
3. `src/kxEdit.App/MainForm.cs`
   - `mdPreview` に `ShortcutKeys = Keys.Control | Keys.Shift | Keys.M`。
   - CSVモード項目に `ShortcutKeys = Keys.Control | Keys.Shift | Keys.K`、
     ハンドラを `_csv.ToggleMode()` → `_csv.EnterMode()`。変数名も実態に合わせる
     （`csvToggle` はトグルでなくなる）。
   - `ShowMarkdownPreview()` 冒頭に CSVモードの早期 return を追加。
   - `mode.DropDownOpening` は現状維持（`mdPreview.Enabled` / `Checked`）。
4. `tests/kxEdit.App.Tests/` — 後述。

CLAUDE.md §3 の簡略化基準（数十行・少数ファイル＋テスト）に乗せ、実装 1 タスク・
単一 commit・最終レビューは別エージェント 1 回に統合する。

## テスト戦略

### L1（kxEdit.Core.Tests）
- `ModeAlreadyOn` の文言。既存の `ModeOn` / `ModeOff` の文言テストに倣う。

### L3（kxEdit.App.Tests）
- **モードメニューの 2 項目に期待の `ShortcutKeys` が付いていること**。
  `form.MainMenuStrip` から辿る（既存テストが同じ経路でメニューを走査している）。
- **メニュー全体を走査して `ShortcutKeys` の重複が無いこと**。
  今回の `Ctrl+Shift+J` 衝突は「登録しても例外が出ず片方が黙って死ぬ」ため
  静的初期化では捕まらない。`CsvCommands.ByKey` が `Add` 形式の初期化子で
  キー重複を検出しているのと同じ網をメニュー側にも張る（**再発防止ネット**）。
- **`EnterMode()` のガード 3 通り**
  - 通常編集中 → `CsvMode == true` / `ReadOnly == true` / `ModeOn` 系の発声。
  - CSVモード中 → `ModeAlreadyOn` を発声し、**モードは維持**（`CsvMode` が true のまま・
    `ReadOnly` も true のまま = トグルしていないこと）。
  - F2 編集中 → 何もしない（発声なし・モード維持）。
- **CSVモード中は `ShowMarkdownPreview` 経路でプレビュー窓が開かないこと**。
  通常編集中は従来どおり開けること（no-change 側の対照）。

CLAUDE.md §4-B の教訓に従い、「CSVモード中に `Ctrl+Shift+K` を押してもモードが落ちない」
テストは**非既定状態（モード ON・`ReadOnly` ON）から始めて**、既定値との区別が付くようにする。

### ミューテーション検証は行わない
CLAUDE.md §4-A の**禁止**領域（キーバインドのイベントマッピング・GUI レイアウト）に
そのまま該当する。

### L5（実機 SR 検証）は必須
発声を 1 本追加し、CSVモードは SR ユーザーの主要動線なので「必要」に倒す。確認項目:

- 通常編集中に `Ctrl+Shift+K` → CSVモードに入り「CSVモード オン …」が読み上げられる。
- CSVモード中に `Ctrl+Shift+K` → **「現在CSVモードです」が読み上げられ、モードが維持される**
  （直後に矢印キーでセル移動が続けられること = 本文のキャレット移動になっていないこと）。
- CSVモード中に `Ctrl+Shift+M` → **何も起きず、何も読み上げない**。直後にセル移動が続けられる。
- 通常編集中に `Ctrl+Shift+M` → プレビューが開き、`Esc` で閉じて編集領域へ戻る。
- `Ctrl+Shift+J`（折り返し整形）が**従来どおり動く**こと（衝突回避の確認）。
  CSVモード中は従来どおり「CSVモード中は実行できません」と読み上げること。
- F2 セル編集中に `Ctrl+Shift+K` / `Ctrl+Shift+M` → 編集オーバーレイが維持され、
  余計な発声が割り込まないこと。
- モードメニューを開いたとき、2 項目にキーが表示され SR がそれを読むこと。
  CSVモード中は「CSVモード」項目がチェック状態として読まれること。
- **モードメニューの「CSVモード」再クリックで OFF にならなくなったこと**（挙動変更の確認）。
  `Esc` で抜けられることを続けて確認する。

## 申し送り

- `説明書/kxEdit説明書.md` のキー表への追記が必要。**ユーザー編集版が正**（CLAUDE.md §8）
  なので本ブランチでは書き換えず、差分案の提示のみ行う。案（11 章のキー表に「モード」節を新設）:

  ```
  ### モード

  | キー | 機能 |
  |---|---|
  | Ctrl+Shift+M | マークダウンプレビューを開く |
  | Ctrl+Shift+K | CSVモードにする(CSVモード中は現在のモードを読み上げるだけ) |
  ```

  併せて「CSVモードはモードメニューの再選択では終了せず、`Esc` で終了する」旨の
  修正も必要（挙動変更 1 件目）。

- **説明書の未反映が計 4 件になった**。本件の 2 キーに加え、PR #79 の `Esc`（CSVモード終了）と
  PR #78 の `Ctrl+矢印`（行/列の端へ移動）が 8.1 のキー表に未反映のまま。
  ユーザー校閲の際は 4 件まとめて検討してもらう。

- マークダウンプレビューを非モーダル化すれば「現在マークダウンプレビューです」の発声も
  実装できるが、フォーカス管理・多重開き・タブ切替時の同期という別課題が生える。
  必要になった時点で独立した設計として起票する（本件では扱わない）。

- CSVモードのキー一覧を持つアプリ内ヘルプは未実装（`MainForm` のコメントに
  「キー一覧は将来のヘルプに記載する」とある）。実装時に本件の 2 キーも載せる。

## 最終レビューの反映（2026-09-23）

策定時スナップショットである上記本文は書き換えず、最終レビュー（コード品質パス / 脆弱性パスを
別エージェントで実施・Critical なし）の結論をここに追記する。**本文と食い違う箇所は本節が正**。

### I-1（反映）: CSVモード中のプレビュー抑止は「無音」ではなく理由を発声する

`ShowMarkdownPreview()` の CSVモードガードで `CsvAnnounceFormatter.BlockedInCsvMode`
（「CSVモード中は実行できません」）を発声する。理由は 2 つ:

- このリポジトリには**既存の慣例と定数がある**。置換（`SearchController`）と
  折り返し整形（`KinsokuFormatController`）が CSVモード中に同じ文言を発声している。
- 無音は SR 利用者にとって「キーが効いていない／ハングした」と区別できない。

→ 本文「### CSVモード中の `Ctrl+Shift+M` は発声もしない」および「意図的な挙動変更（2 件）」の
2 件目にある「発声もしない」は、**本節で上書きされる**。ガードの**位置**（`doc is null` の直後・
`ExceedsMaxChars` より前）は本文どおり変えていない。

### I-2（反映・決定の差し戻し）: モードメニュー「CSVモード」のトグル OFF は維持する

レビューで判明した事実: **CSVモードの視覚的な表示はこのメニューのチェックマークだけ**である
（`MainForm.UpdateStatus()` は行桁・文字コード・改行のみでモード表示を持たない）。そして `Esc` は
ステータスバー・アプリ内ヘルプ（未実装）・`説明書`（未反映）のどこにも出ていない。
メニューから OFF にできなくすると、**`Esc` を知らない晴眼・弱視ユーザーが CSVモードの出口を
完全に失う**（本文は読取専用）。CLAUDE.md §2「晴眼・弱視ユーザーも第一級」に反するため、
メニュー項目のハンドラは `ToggleMode()` のまま（変数名も `csvToggle`）とした。

一方、キー `Ctrl+Shift+K` は要件どおり**進入専用**（トグルしない）で維持する。`ShortcutKeys` は
メニュー項目の `Click` を起こすため両立できないので、キーは `MainForm.ProcessCmdKey` の
`switch` で `_csv.EnterMode()` へ振り、メニューには `ShortcutKeyDisplayString` で**表示だけ**出す。
既存パターン（`F3` / `Shift+F3` / `Ctrl+G` / `Ctrl+Alt+P`）と同方式で、二重発火も起きない。

→ これにより本文「意図的な挙動変更（2 件）」の **1 件目（メニュー再クリックでの OFF 廃止）は撤回**され、
残る挙動変更は **2 件目（CSVモード中はプレビューを開かない・ただし I-1 により理由を発声する）だけ**になる。
本文「### 実装方式: `ToolStripMenuItem.ShortcutKeys` に登録する」も CSVモード項目には当てはまらない。

`mdPreview` は `ShortcutKeys`（`Ctrl+Shift+M`）のまま変えていない。方式が 2 項目で分かれる理由:

| 項目 | 方式 | 理由 |
|---|---|---|
| マークダウンプレビュー | `ShortcutKeys` | OFF 状態を持たない単発コマンド。CSVモード中の挙動（I-1 の発声）もキーとメニュークリックで同一であるべきなので、同一ハンドラを通すのが正しい |
| CSVモード | `ShortcutKeyDisplayString` + `ProcessCmdKey` | メニューはトグル・キーは進入専用という非対称。`ShortcutKeys` では両立できない |

本文「## 申し送り」の `説明書` 差分案のうち、「CSVモードはモードメニューの再選択では終了せず、
`Esc` で終了する」旨の修正は**撤回**する（メニューの再選択で従来どおり終了できる）。キー表への
2 行の追記はそのまま有効。

### I-3（却下）: `ModeAlreadyOn` の文言テスト（L1）は実施しない

本文「### L1（kxEdit.Core.Tests）」の「既存の `ModeOn` / `ModeOff` の文言テストに倣う」は
**倣う先が実在しない**。`tests/kxEdit.Core.Tests/Csv/CsvAnnounceFormatterTests.cs` に `Mode*` の
文言テストは無く、`Cell` / `Header` の整形ロジックのみを検証している。文言の正は Core 定数に
一元化されており、他の assert はすべて定数参照なので、`const` のリテラル固定は同語反復で
価値が薄い。よって却下する。

### I-4（反映）: 重複検出ネットを `ProcessCmdKey` 側にも広げた

既存の `Menu_shortcut_keys_are_unique_across_whole_menu` はメニュー項目の `ShortcutKeys` しか
走査せず、`MainForm.ProcessCmdKey` が `base` 呼出より**前**に食うキーとの衝突を検出できなかった。
`ProcessCmdKey` で食われたキーを `ShortcutKeys` に登録すると、症状は `Ctrl+Shift+J` 衝突と同一
（メニュー側が黙って死ぬ）。I-2 で `Ctrl+Shift+K` が `ProcessCmdKey` 側に移ったのでこの網を追加した
（`MainFormModeMenuTests.Menu_shortcuts_do_not_collide_with_ProcessCmdKey_keys`）。
キーの表は `ProcessCmdKey` の `switch` と対で保つ運用（表から漏れる＝検出しないだけ、
表に余分＝赤くなって気付く、という安全方向にしか壊れない）。

### M-3（受容・L5 で確認）: フォーカスが編集領域の外にあるときの非対称

CSVモード中にフォーカスが編集領域の外（マウスでタブをクリックした後など）にあると、
`Ctrl+Shift+K` で「現在CSVモードです」と発声されるのに**セル移動ができない**
（`ProcessCmdKey` の CSV 素キー横取りが `Editor.ContainsFocus` を要求するため）。
進入方向は `TryEnterMode` が `FocusTarget.Focus()` を呼ぶので、**進入時と「既に ON」時で非対称**。

「既に ON」枝にも `Focus()` を足せば対称になるが、`Focus()` は**フォーカス変化の UIA イベントを
飛ばす**（CSVモード中に抑止されるのは選択変化の経路だけ）ため、SR 雑音のリスクを実機で
確かめずには入れられない。よって現状維持とし、L5 の確認項目に追加する。

### 脆弱性 Low（受容）: 解析不能な巨大 CSV での `Ctrl+Shift+K` 連打

解析不能で巨大な CSV を開いた状態で `Ctrl+Shift+K` を押しっぱなしにすると、失敗パースは
キャッシュを捨てる設計（`TryEnterMode` の `ClearCsvCache()`）のため毎回フルパースが走り、
UI が一時的に無応答になり得る。1 回のパースは `CsvParser` のハードキャップで有界であり、
押下をやめれば回復し、クラッシュはしない。恒久対策（`Document` に「このスナップショットは
パース失敗した」記録を持たせて短絡する）は本件の範囲を超えるため**申し送り**とする。

### L5（実機 SR 検証）の追加項目

本文「### L5（実機 SR 検証）は必須」の項目に次を追加する。

- マウスでタブをクリックして**フォーカスがタブ列にある状態**で CSVモード中に `Ctrl+Shift+K`
  → 発声の直後にセル移動が続けられるか（M-3）。
- **IME 変換中（未確定文字列あり）**に `Ctrl+Shift+K` / `Ctrl+Shift+M`
  → 未確定文字列の残留・余計な発声が起きないか。メニュー経路では `Alt` で変換が確定/取消される
  ため到達しなかった状態に、キー経路で初めて到達できるようになった。
- **晴眼目視**: チェック済みの「CSVモード」を再クリックして OFF になること、メニューに
  `Ctrl+Shift+K` / `Ctrl+Shift+M` が表示されること（I-2）。
- CSVモード中に `Ctrl+Shift+M` → **「CSVモード中は実行できません」と読み上げ**、プレビューが
  開かないこと（I-1 の反映で無音から変わった。本文 L5 の「何も読み上げない」は本項が正）。
