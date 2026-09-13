# 本文選択表示のテーマ連動 設計書

- 日付: 2026-09-14
- 対象: 黒地テーマで本文の選択中テキストが読めない不具合
- 出自: PR #74(Issue #72: IME overlay 文字色のテーマ連動)の申し送り
  (`docs/plans/2026-09-13-ime-overlay-theme-color-design.md` §7)
- Issue: 起票しない(ユーザー判断 2026-09-14)。経緯は本設計書と PR に残す。

## 1. 事象

黒地 3 テーマ(`white-on-black` / `yellow-on-black` / `green-on-black`)で本文のテキストを
選択すると **「水色地に白 / 黄 / 緑」** になり読めない。弱視ユーザー向けハイコントラスト
テーマの目的(CLAUDE.md §2)を選択操作のあいだだけ損なう。SR の読み上げには影響しない。

## 2. 原因

2 箇所の合わせ技である。

1. **選択背景がテーマ非連動**。`BuildStyle`(`src/kxEdit.Editor/EditorControl.Paint.cs:167`)と
   `DefaultStyle`(同 `:142`)の `SelectionBack` が固定値 `0xADD8E6`(薄い水色)。
   同じ `BuildStyle` の中で `CurrentLineBack` / `LineNumberFore` / `WhitespaceGlyph` は
   `BlendRgb` でテーマ連動しており、選択背景だけが取り残されている。
2. **選択中の文字色という概念が無い**。`FrameBuilder.Build`
   (`src/kxEdit.Core/Layout/FrameBuilder.cs`)は選択矩形を `style.SelectionBack` で塗った
   あと(`:128`)、本文 `DrawText` を**選択の内外で分けず常に `style.Foreground`** で発行する
   (`:177`)。`ViewportStyle`(`src/kxEdit.Core/Layout/Frame.cs:48`)に選択時前景色に相当する
   メンバーが無い。

## 3. 先行事例の調査

### 3.1 Visual Studio Code

選択色の既定値(`src/vs/platform/theme/common/colors/editorColors.ts` のレジストリ登録):

| | `editor.selectionBackground` | `editor.selectionForeground` |
|---|---|---|
| Light | `#ADD6FF` | `null`(指定しない) |
| Dark | `#264F78` | `null`(指定しない) |
| HC Dark | `#f3f518` | `#000000` |
| HC Light | `#0F4A85` | `#FFFFFF` |

`editor.selectionForeground` の公式説明は **"Color of the selected text for high contrast."**。
つまり **通常テーマでは選択中の文字色を変えず、選択背景をテーマの明暗に合わせる**ことで
元の文字色のまま読めるようにし、**ハイコントラストテーマでだけ文字色を明示する**。
既定の HC Black テーマ(`extensions/theme-defaults/themes/hc_black.json`)はレジストリ既定を
上書きして `editor.selectionBackground: #FFFFFF` にしているため、**実物の HC Black の選択は
「白地に黒」= 完全反転**である。

なお **VS Code の Light の選択背景 `#ADD6FF` は kxEdit の固定値 `0xADD8E6` とほぼ同じ薄水色**で
ある。kxEdit は事実上「明るいテーマ用の選択色だけを持ち、暗いテーマ用の対になる値を
用意しなかった」状態にある。

### 3.2 Notepad++(Scintilla)

長年**選択テキストの前景色を変更できない**設計だった(Style Configurator の
「Selected text colour」は背景のみ有効・前景欄は無効化され、`SCI_SETSELFORE` を直接
送る裏技が共有されていた)。v8.8 で「Apply custom color to selected text foreground」
オプションが追加され、前景色を指定できるようになった(既定はオフ=背景のみ)。
ダークモードでは選択背景の方を暗い色に差し替えている。

### 3.3 結論

両者とも **「背景をテーマに合わせる」が基本で、前景の指定はハイコントラスト向けの例外**
という構図。kxEdit の 4 テーマは *標準 = 通常の明るいテーマ / 黒地 3 種 =
ハイコントラストテーマ* という構成なので、この方針をそのまま写像できる。

## 4. 方針(ユーザー承認 2026-09-14)

VS Code に追従した混成とする。

| テーマ | 選択背景 | 選択中の文字 |
|---|---|---|
| 標準(白地に黒) | `0xADD8E6` のまま | **指定しない**(本文色=黒のまま)→ 現状と完全に同じ |
| 黒地に白 | 白 | 黒 |
| 黒地に黄 | 黄 | 黒 |
| 黒地に緑 | 緑 | 黒 |

- **標準テーマの見た目は完全に不変**。黒地 3 テーマだけが反転で救われる。
- 副次的に、`OverlayTargetForeColor` の黒が**全テーマで妥当なまま**になる
  (選択背景が常に明るい色=白 / 黄 / 緑 / 水色になるため)。

### 4.1 検討した代案

- **全テーマ一律に反転**: 規則が 1 つで実装もテストも最も単純だが、標準テーマの選択が
  慣れた水色から「黒地に白」に変わる。見た目の意図的変更を標準テーマに持ち込む理由が無い
  ため不採用。
- **背景だけテーマ連動(前景は触らない)**: `FrameBuilder` のテキスト op 分割が不要で
  Core 層の変更が最小。ただし「黒地に黄 / 緑」で十分なコントラストを保つ選択背景色を
  自前で選定・検証する必要があり、色決めの根拠が弱い。不採用。

### 4.2 黒地テーマの選択背景に何色を使うか

**そのテーマの前景色**(白 / 黄 / 緑)を使う。テーマを選んだ意図(黄が見やすい・緑が見やすい)が
選択時にも保たれる。VS Code の HC Black のように**白固定**にする案もあり、L5 の実機目視で
「黄・緑の選択背景が眩しすぎないか」を判定してから最終決定する(§7)。切り替えはテーマ表
2 行の書き換えで済む。

## 5. 設計

### 5.1 データ層 — `AppearanceTheme` に選択色を持たせる

`src/kxEdit.Core/Settings/AppearanceTheme.cs` に `SelectionBackRgb`(`int`)と
`SelectionForeRgb`(`int?`・null = 指定しない)を追加し、プリセット表に書き下す。

| Id | ForeRgb | BackRgb | SelectionBackRgb | SelectionForeRgb |
|---|---|---|---|---|
| `default` | `0x000000` | `0xFFFFFF` | `0xADD8E6` | **null** |
| `white-on-black` | `0xFFFFFF` | `0x000000` | `0xFFFFFF` | `0x000000` |
| `yellow-on-black` | `0xFFFF00` | `0x000000` | `0xFFFF00` | `0x000000` |
| `green-on-black` | `0x00FF00` | `0x000000` | `0x00FF00` | `0x000000` |

- 選択文字色は結果的に `BackRgb` と同値だが、**独立したデータとして書く**。将来
  「濃紺地に白」のような純黒でないテーマが来たときに `BackRgb` 流用だと壊れるため。
- `AppearanceTheme` はプリセット表でのみ構築され、永続化されるのは `Id` だけ
  (`AppSettings.Theme`)なので、フィールド追加は設定互換に影響しない。
- 採用理由: 「どのテーマがハイコントラストか」という暗黙の概念を作らずに済み、
  §4.2 の微調整をデータだけで行える。VS Code のテーマ定義と同じ構造でもある。

### 5.2 Core 層 — `ViewportStyle` と `FrameBuilder`

`ViewportStyle`(`src/kxEdit.Core/Layout/Frame.cs:48`)に **`PaintColor? SelectionFore`** を
追加する。`CurrentLineBack` の Alpha=0 運用ではなく **null 許容**にする
(レコードの「全色を明示指定する」規約と VS Code の `null` 意味論の両方に素直に乗るため)。

`FrameBuilder` の本文 op(重なり順 5)を、**`SelectionFore` が非 null かつその視覚行が選択と
交差するときだけ**分割する。1 視覚行あたり最大 3 op:

| 区間 | 色 |
|---|---|
| `[rowStart, interStart)` | `Foreground` |
| `[interStart, interEnd)` | `SelectionFore` |
| `[interEnd, rowEnd)` | `Foreground` |

- 空の区間は op を出さない(行頭からの選択なら 2 op)。
- 交差 `[interStart, interEnd)` は選択矩形と同じ計算(`TryComputeRowRangeRect` 相当)で求め、
  矩形と文字が必ず一致するようにする。
- **X 座標と幅はすべて `PixelMapper.OffsetToPx` の差分で出す**。部分文字列の `MeasureRun` は
  使わない — `GdiCharMetrics.MeasureRun`(`src/kxEdit.Editor/GdiCharMetrics.cs:71`)は非 ASCII を
  含む run を一括計測するため加算的でなく、差分で取らないと選択矩形と文字がずれる。

**不変条件**: `SelectionFore` が null、または選択と交差しない行では、PaintOp 列が現行と
1 op も変わらない。これにより標準テーマの描画は完全に不変になる。

### 5.3 Editor 層

- `BuildStyle` / `DefaultStyle`(`src/kxEdit.Editor/EditorControl.Paint.cs:142,167`)の固定
  `0xADD8E6` を、テーマ表からの供給に置き換える(`DefaultStyle` は標準テーマ相当=
  `SelectionFore: null`)。
- `OverlayTargetForeColor`(`src/kxEdit.Editor/EditorControl.Ime.cs:72`)を
  `_style.SelectionFore ?? _style.Foreground` に繋ぎ換える。IME 変換対象節は選択背景の上に
  描くので選択文字色を使う、という対応をコードで明示する。4 テーマとも結果は黒なので
  **挙動不変**。#74 で入れた「固定水色を前提にした黒」というコメントを新しい根拠に差し替える。

### 5.4 意図的挙動変更

「黒地 3 テーマで本文の選択表示が『水色地に本文色』から『テーマ前景色地に黒』へ変わる」は
意図的な挙動変更。CLAUDE.md §2 に従い PR description に明記する。標準テーマは挙動不変。

## 6. テスト

### L1(`kxEdit.Core.Tests`)

- `FrameBuilderTests`: `SelectionFore` 指定時の分割を Text / Fore / X / Width で固定する。
  - 部分選択の fixture は **prefix と suffix を両方持つ**ものにする(全選択と区別・§4-B)。
  - 行頭からの選択 / 行末までの選択(2 op・空 op を出さない)。
  - 折り返しで複数視覚行にまたがる選択(各視覚行で分割される)。
  - 空選択(`Start == End`)で分割が起きない。
- no-change 網: `SelectionFore == null` なら本文 op が 1 本のまま。**選択がある状態**から
  検証を始める(既定値と区別する・§4-B)。
- `AppearanceThemeTests`: 4 テーマの選択色を固定(黒地 3 種は `SelectionForeRgb` が非 null)。

### L2(`kxEdit.Editor.Tests`)

- `BuildStyle` が 4 テーマで `SelectionBack` / `SelectionFore` をテーマ表どおりに載せること。
- `OverlayTargetForeColor` が 4 テーマとも黒であること(挙動不変の退行検知)。

### ミューテーション検証

**実施しない**。主眼はテーマ配色の適用(CLAUDE.md §4-A の禁止領域)であり、分割境界の算出は
既存の選択矩形算出(`TryComputeRowRangeRect`)の再利用で新規アルゴリズムではない。
上記の境界テスト(行頭 / 行末 / 行またぎ / 空選択)で代替する。

## 7. 検証と手動確認

- `tools/pre-merge-check.ps1` で **EXIT 0** / 0 warning。
- `sr-regression.ps1` は**不要**。SR 経路(`kxEdit.Accessibility` / `EditorControl` の UIA 部 /
  App の Speech 系)に触れない。
- **L5 実機目視は必須**(見た目の変更)。黒地 3 テーマで次を確認するチェックリストを起こす:
  1. 本文を選択したテキストが読めること。
  2. IME 変換中の対象節・通常節が読めること(#74 の退行が無いこと)。
  3. **黄・緑の選択背景が眩しすぎないか**。眩しければ黒地 3 テーマの選択背景を白固定に
     切り替える(§4.2・テーマ表 2 行の書き換え)。

## 8. 申し送り

- **WPF 移行ブランチへの反映**: Core 層(`ViewportStyle` / `FrameBuilder` / `AppearanceTheme`)の
  変更は共有されるが、WPF 版の `BuildStyle` 相当に選択色の供給を足す必要がある。移行を
  再開する際に回収する。
- **空白可視化グリフとセルハイライト(CSV)は選択色に連動させない**(現状維持・スコープ外)。
  黒地テーマでは空白グリフ色(`Blend(Back, Fore, 0.3)`)が暗色になるため、明るい選択背景の
  上でも読める。
- **非 ASCII 混在行での ±1px ずれ**: 本文を分割描画することで、run 一括計測との差により
  理論上 1px 程度のずれが起こりうる。空白可視化グリフの重ね描画で既に踏んでいる前提と
  同じであり、許容する。L5 で目視確認する。
