# Home キーの動作を設定で切り替える 設計書

策定日: 2026-09-04 / ベース: main `ad2dc56`(PR #67 マージ後)

v0.2 リリース前に入れる最後の機能追加。本書は**策定時スナップショット**(CLAUDE.md §8)。
実装時の精密化と実施記録の追記のみ行う。

## 1. 背景と目的

現在の Home キーは「行頭の空白を飛ばす」スマート挙動に固定されている
(Notepad++ / Visual Studio Code と同じ)。インデントを多用しない文書では
「Home を押したのに行頭に行かない」ほうが直感に反する、という利用形態がある。

**目的**: Home キーの動作を 2 択にし、設定ダイアログの[編集]タブで切り替えられるようにする。
既定は現状のスマート挙動(既存ユーザーの挙動不変)。

## 2. 現状の実装(調査結果)

| 要素 | 位置 |
|------|------|
| 移動先の算出 | `NavigationCommands.MoveHomeSmart`(`src/kxEdit.Core/Editing/NavigationCommands.cs:55` = 論理行版 / `:89` = 折り返し対応版) |
| キー処理 | `InputRouter.HandleHome`(`src/kxEdit.Editor/InputRouter.cs:183`) |
| 設定の伝播 | `AppSettings` → `EditorAppearance.Apply` → `EditorControl.ApplyAppearance`(`MainForm.cs:412` が設定確定時に呼ぶ) |
| [編集]タブ | `src/kxEdit.App/Settings/Tabs/EditSettingsTab.cs` |

現状の挙動(ユーザー記述と実装は一致している):

- 折り返し OFF: `firstNonWs` ⇔ `lineStart` のトグル。それ以外の位置からは `firstNonWs` へ。
- 折り返し ON: 判定の基準が**視覚行(折り返しセグメント)**になる。第 1 セグメントは同じトグル、
  継続セグメント(2 つ目以降)は視覚行の先頭に固定(トグルなし)。これは P8-1a の a11y 対応で、
  NVDA が視覚行の先頭から読むようにするための挙動。
- 空白判定は半角空白 `' '` とタブ `'\t'` のみ(全角空白 U+3000 は非空白扱い)。

**Ctrl+Home は本件の対象外**。`HandleHome` は `ctrl` のとき `MoveHomeSmart` を呼ばず
`0`(文書先頭)を返すため、設定の影響を受けない。**Shift+Home は対象**で、移動先の算出を
共有しているため設定に自動追従する(`ApplyNavMove` が Shift 時に選択を拡張する)。

**CSV モードは対象外**。CSV モード中の Home は `MainForm` の `ProcessCmdKey` が
`CsvCommands.ByKey`(`Keys.Home` → `CsvController.MoveRowStart`)で横取りしており、
`InputRouter.HandleHome` に到達しない。

## 3. 変更内容

### 3.1 設定モデル

`AppSettings`(`src/kxEdit.Core/Settings/AppSettings.cs`)に 1 キー追加する。

```csharp
/// <summary>Home キーで行頭の空白(インデント)を飛ばすか(true=スマート・既定)。
/// false のときは常に行頭(折り返し ON では視覚行の先頭)へ移動する。</summary>
public bool SmartHome { get; set; } = true;
```

既定 `true` = 現状挙動。既存の settings.json にキーが無い場合も System.Text.Json の
既定挙動で既定値が効くため、データ移行は不要。

### 3.2 Core ロジック

既存 `MoveHomeSmart` の 2 オーバーロードに `bool skipIndent` を足す
(既存の呼び出し側は 1 箇所=`InputRouter` のみなので、既定値引数は付けず**明示的に渡す**。
既定値引数を付けると新しい呼び出し元が黙ってスマート側に倒れるため)。

| モード | 折り返し OFF | 折り返し ON |
|--------|--------------|-------------|
| スマート(`skipIndent=true`・既定) | `firstNonWs` ⇔ `lineStart` トグル(現状のまま) | 第 1 セグメントは視覚行内でトグル / 継続セグメントは視覚行頭(現状のまま) |
| 常に行頭(`skipIndent=false`) | 常に `lineStart` | 常に視覚行の先頭 |

実装は `firstNonWs` の探索ループを飛ばすだけ。継続セグメントの扱い(=NVDA が視覚行頭から
読む P8-1a 特性)は両モードで保たれる。

**メソッド名**: `MoveHomeSmart` は「スマート挙動」を名前に含むが、`skipIndent=false` は
スマートではない。名前を `MoveHome`(既存の別メソッド)と衝突させずに実態へ寄せるため、
実装時に `MoveLineHome(TextSnapshot, int, bool)` / `MoveLineHome(TextSnapshot, int, int, ICharMetrics, bool)`
へ改名する(挙動不変のリネーム。既存テストの呼び出しも追随する)。

### 3.3 伝播

- `EditorControl` に `public bool SmartHome { get; set; } = true;` を追加。
- `ApplyAppearance` で `settings.SmartHome` を反映する。`ApplyAppearance` の doc コメントは
  「フォント/テーマ/表示設定」と書かれているため、入力挙動を含むよう更新する。
  (`TabWidth` / `TabsToSpaces` が未接続である旨の既存コメントはそのまま残す。)
- `InputRouter.HandleHome` が `ctx.Host.SmartHome` を `skipIndent` として渡す。

### 3.4 UI([編集]タブ 4 行目)

```
Home キーの動作
  (•) 行の最初の文字へ移動する(もう一度押すと行頭)(&F)
  ( ) 常に行頭へ移動する(&B)
```

- `GroupBox` でくくる。WinForms の RadioButton は同一コンテナ内で排他になるため、
  既存 CheckBox 群と同じ `TableLayoutPanel` に直置きしない。
- グループ名は `GroupBox.Text` = 「Home キーの動作」。SR はフォーカス時にグループ名を読む。
- アクセスキーは既存[編集]タブで未使用の `F` / `B` を使う(既使用: `W` `K` `T` `S`)。
- `TabIndex` は既存の末尾(5 = タブ→スペース)に続けて 6 以降。
- RadioButton はアプリ全体で初出。`ISettingsTab.Dispose` で GroupBox と両 RadioButton を破棄する
  (既存タブと同じ CA1001 対応方針)。

`LoadFrom` / `SaveTo` は `SmartHome` の真偽と 2 つの `Checked` を対応させる。

## 4. テスト

| 層 | 内容 |
|----|------|
| L1 (Core) | `skipIndent=false` で ①本文内 → 行頭 ②行頭で押しても動かない ③空白のみの行 ④空行 ⑤折り返し ON の第 1 / 継続セグメント。既存のスマート側テストは**期待値を変えずに**通ること(挙動不変の証明) |
| L2 (Editor) | `EditorControl.SmartHome` の既定値 true / `ApplyAppearance` が `AppSettings.SmartHome` を反映する / Home キー押下で実際の移動先が切り替わる / Shift+Home が設定に追従し選択範囲が変わる |
| L3 (App) | `EditSettingsTab` の `LoadFrom` → `SaveTo` ラウンドトリップ(true / false 両方向) |
| L4 | 対象外(性能に影響しない) |
| L5 | **必要**。§5 参照 |

**ミューテーション検証**: CLAUDE.md §4-A の「カーソル移動」に該当するため実施対象。
`skipIndent` の分岐と `firstNonWs` 探索ループの条件にスポットチェックを掛ける。
UI 側(RadioButton の配線)は §4-A の禁止領域なので対象外。

テスト設計の教訓(CLAUDE.md §4-B)の適用:
- 「常に行頭で動かない」を確かめるテストは、**行頭以外から始めて 2 回押す**形にする
  (1 回目で行頭へ、2 回目で動かない)。行頭から始めると既定位置と区別できない。
- 折り返しの継続セグメントのテストは、論理行頭と視覚行頭が**別の offset** になる fixture にする。

## 5. L5(実機 SR 検証)の要否

**必要**。Home はキャレット移動であり、移動先が変われば NVDA の発声内容が変わる。
CLAUDE.md §5 の「判定に迷ったら必要に倒す」に従う。

チェックリストは `docs/plans/2026-09-04-home-key-behavior-l5-checklist.md` に作成する。
最低限の項目:

1. 折り返し OFF・スマート: インデント行で Home → 最初の文字から読む / もう一度 Home → 行頭
2. 折り返し OFF・常に行頭: インデント行で Home → 行頭から読む / もう一度 Home で動かない
3. 折り返し ON・常に行頭: 継続セグメント上で Home → 視覚行の先頭から読む(論理行頭へ飛ばない)
4. Shift+Home の選択範囲が両モードで正しく読み上げられる
5. 設定ダイアログ[編集]タブで GroupBox 名「Home キーの動作」と各ラジオが読み上げられ、
   矢印キーで排他選択できる / アクセスキー F・B が効く

## 6. タスク分割

| # | 内容 | 層 |
|---|------|----|
| 1 | Core: `MoveLineHome` へのリネーム + `skipIndent` 追加、L1 テスト | Core |
| 2 | Editor: `EditorControl.SmartHome`、`ApplyAppearance` 反映、`HandleHome` 配線、L2 テスト | Editor |
| 3 | App: `AppSettings.SmartHome`、[編集]タブ UI、L3 テスト | App |

タスク 1 は後続が依存する seam の変更なので、**タスク時にコード品質レビュー**を行う
(CLAUDE.md §3-4 前倒しレビュー)。外部入力のパース・パス操作・プロセス起動には
触れないため、脆弱性レビューの前倒しは不要(最終ブランチレビューの脆弱性パスでは扱う)。

## 7. 却下した案

- **App 層で分岐する**: `InputRouter` は Editor 層にあり、App からキー処理へ差し込む
  seam が無い。新設は本件に見合わない。
- **`MoveHomeVisual` を別関数として新設する**: 折り返しセグメントの算出が丸ごと重複する。
  差分は `firstNonWs` ループ 1 つなのでパラメータ化のほうが腐りにくい。
- **「常に行頭」を論理行の先頭に倒す**: 折り返し ON のとき視覚行頭から読まれなくなり、
  P8-1a で解消した a11y 退行(N-3)を再導入する。ユーザー確認済みで視覚行頭を採用。

## 8. 申し送り

- `TabWidth` / `TabsToSpaces` が `ApplyAppearance` で未反映のまま残っている
  (`EditorControl.cs:2689` のコメント / `InputRouter.cs:461`)。本件の範囲外。
- End キーには対応するトグルが無い(`MoveEnd` は常に行末)。左右対称にする要求が出たら
  別件として扱う。
