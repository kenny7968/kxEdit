# 設定ダイアログの寸法決定 設計書 (Issue #68)

- 日付: 2026-09-04
- 対象 Issue: [#68](https://github.com/kenny7968/yEdit/issues/68) 設定ダイアログが 136x89 px に潰れて表示される
- ブランチ: `feature/settings-dialog-size`

## 1. 背景と症状

`オプション → 設定` で開く `SettingsDialog` が **136×89 px** に潰れ、タイトルバーと
「キャンセル」ボタンの一部しか見えない。マウスでは操作できない。キーボード / UIA 経路は
生きているため、SR 中心の検証では見逃されていた(CLAUDE.md §2「晴眼・弱視ユーザーも第一級」)。

タブ化コミット `9f97fc3`(設定ダイアログをタブ骨格へ置換)以降ずっと壊れていた既存不具合。
`feature/home-key-behavior` の実装中に目視で発見した(同ブランチ由来ではないことを確認済み)。

## 2. 実測(このブランチの設計根拠)

いずれも **実測**。スクラッチのコンソールプローブから `new SettingsDialog(new AppSettings())` を
画面外で `Show` して計測した(96 DPI / Yu Gothic UI 9pt)。

### 2.1 現状の寸法

```
dialog Size={136, 89} Client={120, 50} Preferred={16, 89}
  TabControl Size={120,50} Display={4,24,112,22} Preferred={200, 100}  ← ページ内容と無関係
  page '基本'         body 希望 {382, 138}
  page '編集'         body 希望 {400, 206}
  page '禁則処理'     body 希望 {451, 111}
  page '表示'         body 希望 {488, 212}
  page 'バックアップ' body 希望 {278, 115}
```

`TabControl` はページの中身から推奨サイズを算出せず、常に既定の `200×100` を返す。

### 2.2 Issue の修正案は効かない(否定された仮説)

Issue の「修正案」は `_tabControl.MinimumSize` に全ページ希望サイズの最大を与えるものだった。
外から当てて実測したところ **Form は 136×89 のまま**だった。

```
[fix-A] tc.MinimumSize={496, 240}
[fix-A] dlg.Size={136, 89} Client={120, 50}      ← 変わらない
[fix-A] tc.Size={496, 240}                       ← TabControl だけ育ってフォーム外へはみ出す
```

**Form の `AutoSize` は `Dock=Fill` の子の希望サイズを見ない**。`dlg.GetPreferredSize` は
`MinimumSize` を与えた後も `{16, 89}` を返し続けた。したがって Form 側の寸法を明示的に
決める以外に解決しない。

### 2.3 Dock の追加順が逆(同居していた 2 つ目の不具合)

`BuildLayout` は `Controls.Add(buttons)`(Dock=Bottom)→ `Controls.Add(_tabControl)`(Dock=Fill)
の順で、コメントは「Dock.Bottom を先に Add してから Dock.Fill を Add する順で下部固定＋残り全部を
実現」と書いていた。**WinForms は子インデックスの大きい方からドックを確定する**ため、
この順では最後に Add した `_tabControl` が最初にクライアント全面を取り、ボタン列を覆う。

正しい寸法を与えた瞬間に表面化した:

```
dlg Client={496, 290}
tab.Bounds={0,0,496,290}  buttons.Bounds={0,240,496,50}  overlap=True
```

現状は全体が潰れているため露見していないだけ。`DocumentInfoDialog` は逆順(Fill を先に Add)で
実装されており、`DocumentInfoDialogTests.Fill_textbox_and_bottom_button_panel_do_not_overlap`
がその契約を固定している。

### 2.4 採用案の成立確認

Dock 順を是正し、`AutoSize` を外して算出値を `ClientSize` に与えると、フォント倍率
1.0 / 1.5 / 2.0(高 DPI・弱視相当)のすべてで成立した。

| フォント倍率 | 算出 Client | Form Size | 全 5 ページ収まる | ボタン列と重なる |
|---|---|---|---|---|
| 1.0 | 496×324 | 512×363 | Yes | No |
| 1.5 | 558×397 | 574×436 | Yes | No |
| 2.0 | 684×468 | 700×507 | Yes | No |

OK / キャンセルの矩形がいずれもクライアント領域内に入ることも確認した。

### 2.5 テスト設計に効く実測

- `TabControl.DisplayRectangle` は**ハンドル未生成でも算出される**(96 DPI で
  ハンドル有無とも枠 = 幅 8 / 高さ 28 で一致)。枠を即値で書く必要はない。
- **未選択の `TabPage` はレイアウトされない**(選択済みページ以外は `ClientSize` が
  `112×22` のまま)。全ページを検査するテストは**各ページを選択してから**測る必要がある。

## 3. 方針(採用案)

`SettingsDialog.BuildLayout` を 2 点変更する。

### 3.1 Dock の追加順を是正

```csharp
// Dock は子インデックスの大きい方から確定する。Fill を先に Add し Bottom を後に Add する
// (逆順にすると Fill の TabControl がクライアント全面を取りボタン列を覆う)。
Controls.Add(_tabControl);
Controls.Add(buttons);
```

コメントも実際の規則に合わせて書き換える(現行コメントは事実と逆)。

### 3.2 `AutoSize` を外し `ClientSize` を算出値で与える

`AutoSize` / `AutoSizeMode` の指定を削除し、`BuildLayout` の末尾で算出値を代入する。

```
幅 = max(全ページ本体の希望幅の最大 + タブ枠幅, ボタン列の希望幅)
高 = 全ページ本体の希望高の最大 + タブ枠高 + ボタン列の希望高
```

**タブ枠(ヘッダ帯 + 境界)は即値で書かず `_tabControl.Size - _tabControl.DisplayRectangle.Size`
で実測する**。測定は 2 段階にする:

1. 十分広い仮寸法で測って境界幅を得て、確定幅を求める。
2. **確定幅で測り直して枠の高さを得る** —— 幅が狭くタブヘッダが 2 段に折り返す場合を
   取りこぼさないため。

即値を一切書かないので、フォント / DPI に自動追従する(§2.4)。

### 3.3 テスト可能性のための薄い seam

寸法計算を `SettingsTabLayoutHelper.ComputeDialogClientSize(TabControl tabs, Control buttons)`
として切り出す(既存の共用ヘルパ・`internal`、`InternalsVisibleTo` は既設)。

理由: `SettingsDialog` の `ClientSize` は ctor(`BuildLayout`)で一度確定するため、
**この seam が無いと「大きいフォントでも成立する」網が原理的に張れない**
(テストから ctor 前の `Font` を差し替える手段がない)。

## 4. テスト(L3: `tests/kxEdit.App.Tests/SettingsDialogLayoutTests.cs` 新規)

| # | 固定する契約 | kill する退行 |
|---|---|---|
| 1 | 画面外 `Show` 後、**全 5 ページを順に選択**して `page.ClientSize` が本体の希望サイズを包含する | 潰れ(136×89)の再発 |
| 2 | `_tabControl.Bounds` と `buttons.Bounds` が重ならず、双方が `ClientRectangle` に収まる | Dock 追加順の逆転(§2.3) |
| 3 | ヘルパ単体: フォント 1.5 倍の `TabControl` を渡した戻り値が、全ページ希望サイズ + 枠 + ボタン列を包含する | 枠の即値化・高 DPI での切れ |

- ピクセル即値ではなく**包含関係**で見る(既存 `EditSettingsTabTests` と同じ流儀・DPI 非依存)。
- テスト 1 は §2.5 の実測を踏まえ、必ず各ページを選択してから測る。
- ミューテーション検証は行わない(CLAUDE.md §4-A: GUI レイアウトは禁止領域)。

## 5. 工程

規模は単一ファイル + テスト 1 ファイルのため CLAUDE.md §3「簡略化の基準」に該当する。

- 実装は 1 タスク・単一 commit。最終レビューは 2 パス統合の 1 回。
- **別エージェントレビューと品質ゲート(`tools/pre-merge-check.ps1` EXIT 0)は省略しない**。

### L5(実機 SR 検証)の要否: **必要**

Dock の追加順を変えると `Controls` の z-order が変わる。本ダイアログはボタン・TabControl に
`TabIndex` を明示していないため、**Tab キーの巡回順と UIA ツリーの子順序が変わりうる**
(現在「ボタン列 → TabControl」→ 変更後「TabControl → ボタン列」)。
チェックリストに次を含める:

- 開いた直後のフォーカスが先頭タブ「基本」にあり、そう読まれる。
- Tab キーで タブ → ページ内コントロール → OK / キャンセル の順に回る。
- Shift+Tab で逆順に回る。
- Enter / Esc が従来どおり OK / キャンセルとして働く。
- 各タブの内容が従来どおり読まれる(タブ切替でカテゴリ名が読まれる)。

## 6. 対象外・申し送り

- **Issue #69**(`tools/check-no-local-paths.ps1` が 1 行ファイルを走査できない)は別件。
  本ブランチに含めない。
- 画面に収まらない場合のクランプ / `AutoScroll` は入れない。フォント 2.0 倍相当でも
  700×507 で実害が観測されないため(YAGNI)。必要が観測された時点で追加する。
- 本修正のマージ後、**PR #70(Home キーの動作切替)のマウス目視確認が可能になる**。
  同 PR は本不具合のため機械検証で代替していた。回収すること。
