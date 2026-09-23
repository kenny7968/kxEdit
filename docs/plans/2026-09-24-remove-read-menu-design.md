# 「読み上げ」メニュー廃止 設計書

- 日付: 2026-09-24
- ブランチ: `feature/remove-read-menu`
- 規模: 小変更(§3 簡略化基準を適用。実装 1 commit・最終レビュー 1 回統合)

## 目的

メニューバーの「読み上げ(&R)」を廃止する。中の 2 項目は次のように扱う。

- 「行へ移動」: 「検索」メニューの一番下へ移す。
- 「現在位置」: 機能ごと廃止する(メニュー項目・Ctrl+Alt+P・読み上げ処理)。

行と桁の読み上げがなくなる点は、「行へ移動」ダイアログが現在行を初期表示するため、それで代替する
(ユーザー判断)。桁は分からなくなるが、ダイアログへ桁の指定を追加する予定(申し送り参照)。

## 現状

| 箇所 | 現状 |
|------|------|
| `MainForm.BuildMenu` | 「読み上げ(&R)」= 現在位置(&P) [表示 Ctrl+Alt+P] / 行へ移動(&G)... [表示 Ctrl+G] |
| `MainForm.ProcessCmdKey` | `Ctrl+Alt+P` → `AnnouncePosition()`、`Ctrl+G` → `GoToLine()` |
| `MainForm.AnnouncePosition` | `PositionFormatter.Format` で「行 L / 全 N、桁 C(、上書き)」を発声 |
| `kxEdit.Core/Reading/PositionFormatter.cs` | 上記の文字列組み立て。他に利用箇所なし |
| 「検索」メニュー | 検索(&F) / 置換(&H) / 次を検索(&N) / 前を検索(&B) / ─ / フォルダ検索(grep)(&G) |

## 設計

### 1. メニュー構成

```
ファイル(F)  編集(E)  検索(S)  モード(M)  オプション(O)  ヘルプ(H)

検索(S)
  検索(F)...               Ctrl+F
  置換(H)...               Ctrl+H
  次を検索(N)              F3
  前を検索(B)              Shift+F3
  ──────────
  フォルダ検索(grep)(G)... Ctrl+Shift+F
  ──────────
  行へ移動(J)...           Ctrl+G
```

- アクセスキーは G → **J** に変える。「検索」内では G を「フォルダ検索(grep)」が使っており、
  重複すると Alt→S→G で選べず巡回になるため(ユーザー判断。J = Jump)。
- 「行へ移動」の直前に区切り線を置く(検索系と移動系の区切り)。
- Ctrl+G の配線(`ProcessCmdKey` で処理し、メニューは `ShortcutKeyDisplayString` の表示のみ)と、
  CSV モード中はセル指定へ読み替える挙動は不変。

### 2. 「現在位置」の廃止

- `MainForm`: メニュー項目・`ProcessCmdKey` の `Ctrl+Alt+P` case・`AnnouncePosition()` を削除。
  Ctrl+Alt+P は未割り当てになる。
- `kxEdit.Core/Reading/PositionFormatter.cs` と `PositionFormatterTests` を削除(利用箇所がなくなる)。
- コメント中の `Ctrl+Alt+P` への言及(`MainForm` の 2 箇所・`CharacterCounter.cs`)を実態に合わせる。

### 3. テスト

- 削除: `MainFormSmokeTests.AnnouncePosition_ReadsLineTotalAndColumnOnly`、`PositionFormatterTests`。
- 更新: `MainFormModeMenuTests.OwnedByProcessCmdKey` から `Ctrl+Alt+P` を外す(switch と対で保つ表)。
- 追加(L3):
  - メニューバーに「読み上げ」が存在しないこと。
  - 「行へ移動(&J)...」が「検索」メニューの最後の項目で、直前が区切り線であること。
    表示ショートカットが `Ctrl+G` のままで、`ShortcutKeys` は `None`(ProcessCmdKey 所有)であること。
  - 「検索」メニュー内のアクセスキーが重複しないこと(既存 `File_menu_accelerators_are_unique` と同形)。

ミューテーション検証は CLAUDE.md §4.A の禁止範囲(GUI のメニュー構成・キーバインドのマッピング)のため実施しない。

## 意図的な挙動変更

- メニューバーから「読み上げ(&R)」が消える。
- 「行へ移動」の位置が 検索メニュー末尾 に、アクセスキーが Alt→R→G から Alt→S→J に変わる。
- Ctrl+Alt+P(現在位置の読み上げ)が廃止され、行・総行・桁を照会する手段がなくなる。
- 現在位置の読み上げは上書きモード中に「、上書き」を付けていたため、**挿入/上書きモードの照会手段もなくなる**。
  今後は Insert キー押下時の「上書きモード / 挿入モード」の発声のみが手がかりになる(ユーザー承認済み)。

## L5 要否

App の発声経路(Speech 系への呼び出し元)とメニュー構成に触れるため**必要**。確認項目:

- Alt でメニューバーを巡回したとき「読み上げ」が現れない。
- Alt→S→J で「行へ移動」ダイアログが開き、現在行が初期表示される。
- Ctrl+G が従来どおり動く(CSV モード中はセル指定)。
- Ctrl+Alt+P で何も発声しない(従来の位置読み上げが残っていない)。

## 申し送り

- 「行へ移動」ダイアログへ桁(ジャンプ先の桁)の入力を追加する(ユーザー予定)。
- `説明書/kxEdit説明書.md` の 51 行目(メニューバー一覧)・137 行目(現在位置の表)・286 行目(Ctrl+Alt+P)が
  変更後の挙動と合わなくなる。説明書はユーザー編集版が正のため本変更では書き換えず、PR に文案を提示する。
