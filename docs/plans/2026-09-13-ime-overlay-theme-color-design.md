# IME 未確定文字列 overlay の文字色をテーマに連動させる 設計書

- 日付: 2026-09-13
- 対象 Issue: [#72](https://github.com/kenny7968/kxEdit/issues/72)
- ブランチ: `feature/ime-overlay-theme-color`

## 1. 背景と症状

配色テーマを「黒地に白」「黒地に黄」「黒地に緑」にすると、IME 変換中の未確定文字列のうち
**通常節(下線のみの節)が黒地に黒で描かれ、見えない**。変換対象節(太字・選択背景つき)は
背景があるので見える。

弱視ユーザー向けハイコントラストテーマに直接関わる(CLAUDE.md §2「晴眼・弱視ユーザーも第一級」)。
SR の読み上げには影響しない。

## 2. 根本原因(コードで確認)

overlay の文字色は seam `IImeOverlayHost.ForeColor` 経由で `Control.ForeColor` を返している。

| 場所 | 内容 |
|---|---|
| `src/kxEdit.Editor/EditorControl.cs:150` | ctor で `ForeColor = Color.Black` |
| `src/kxEdit.Editor/EditorControl.cs:2733` | `ApplyAppearance` はテーマから `_style` と `BackColor` しか更新しない(`ForeColor` は更新しない) |
| `src/kxEdit.Editor/EditorControl.Ime.cs:64` | `Color IImeOverlayHost.ForeColor => ForeColor;` |
| `src/kxEdit.Editor/ImeController.cs:210,251,262` | `Draw` が全節を `_host.ForeColor` で描く |

本文はテーマ色 `_style.Foreground`(`FrameBuilder` の `Fore: style.Foreground`)で描かれるため、
**本文と overlay の前景色が一致しない**。黒地テーマでは `_style.Foreground` が白/黄/緑なのに
overlay は黒のままとなり、通常節が背景に溶ける。

発見の経緯は Issue #72 のとおり(WPF 移行ブランチ Task 1b のコード品質レビュー)。

## 3. 単純な 1 行修正を採らない理由(対象節の退行)

Issue 本文の修正案は「`ForeColor` を `_style.Foreground` に揃える(1 行)」だが、これは
**変換対象節を退行させる**。

`BuildStyle`(`src/kxEdit.Editor/EditorControl.Paint.cs:167`)の `SelectionBack` は
テーマ非連動の固定値 `0xADD8E6`(薄い水色)である。対象節はこの水色で塗った上に文字を描くため、
前景をテーマ色に変えると:

| テーマ | 現状の対象節 | 1 行修正後の対象節 |
|---|---|---|
| 標準(白地に黒) | 水色地に黒 | 水色地に黒(不変) |
| 黒地に白 | 水色地に黒 | 水色地に**白**(低コントラスト) |
| 黒地に黄 | 水色地に黒 | 水色地に**黄**(低コントラスト) |
| 黒地に緑 | 水色地に黒 | 水色地に**緑**(低コントラスト) |

通常節を救って対象節を壊すことになる。そこで **overlay の色 seam を通常節用と対象節用に
分ける**(ユーザー承認 2026-09-13)。

## 4. 設計

### 4.1 seam を 2 色に分ける

`src/kxEdit.Editor/IImeOverlayHost.cs`:

- `Color ForeColor` を **`Color OverlayForeColor` に改名**(通常節・1 節扱い経路で使う)。
  名前が `Control.ForeColor` と同一であること自体が本不具合の根因(WinForms の意味論が
  seam に漏れていた)なので、意味を名前で固定する。WPF 移行ブランチも Task 1b で同名
  `OverlayForeColor` を採用しており、移行を再開したときの突き合わせも容易になる。
- **`Color OverlayTargetForeColor` を追加**(変換対象節で使う)。

### 4.2 実装(`src/kxEdit.Editor/EditorControl.Ime.cs`)

```csharp
Color IImeOverlayHost.OverlayForeColor => ToColor(_style.Foreground);
Color IImeOverlayHost.OverlayTargetForeColor => Color.Black;
```

- `OverlayForeColor` がテーマ前景を返すことが #72 の修正本体。本文と同じ色になる。
- `OverlayTargetForeColor` は現状の挙動(黒)を維持する。背景が固定水色 `0xADD8E6` である限り
  黒が全テーマで最良で、テーマ色に連動させると §3 の退行が起きる。`SelectionBack` を
  テーマ連動にする際に同時に見直す旨をコメントで残す(§7 の申し送り)。

`EditorControl.cs:150` の `ForeColor = Color.Black` は**触らない**。`ControlStyles.UserPaint`
のため WinForms 既定描画の消費者がおらず、overlay 以外に影響が無い。範囲外とする。

### 4.3 呼び出し側(`src/kxEdit.Editor/ImeController.cs`)

`Draw` の 3 箇所を使い分ける。

| 経路 | 使う色 |
|---|---|
| `Clauses.Length < 2` の 1 節扱い(`:210`) | `OverlayForeColor` |
| 対象節 `attr == ImeAttribute.TargetConverted`(`:251`) | `OverlayTargetForeColor` |
| 通常節(`:262`) | `OverlayForeColor` |

フォント(`UnderlineFont` / `TargetFont`)・背景塗り・節分割・防御(負値・長さ不整合)の
ロジックは一切変えない。

### 4.4 意図的挙動変更

「黒地テーマで IME 未確定の通常節がテーマ前景色で描かれる(従来は黒固定)」は意図的な挙動変更。
CLAUDE.md §2 に従い PR description に明記する。対象節は全テーマで挙動不変。

## 5. テスト

### 5.1 主網: seam のテーマ連動(`kxEdit.Editor.Tests`)

4 テーマ(`default` / `white-on-black` / `yellow-on-black` / `green-on-black`)について
`ApplyAppearance` 後の値を Theory で固定する:

- `OverlayForeColor` == テーマの `ForeRgb`(= 本文と同じ色)
- `OverlayTargetForeColor` == 黒(全テーマ。退行検知)

`IImeOverlayHost` は internal・explicit 実装だが、`InternalsVisibleTo` により
テストから `((IImeOverlayHost)control).OverlayForeColor` で読める
(既存 `FakeImeOverlayHost` が同じ前提で成立している)。

### 5.2 症状網: overlay が背景に溶けないこと

`ImeController.Draw` は `Graphics` 依存で現状無網
(`FakeImeOverlayHost` のヘッダコメント「Draw テストは Graphics 依存のため本 fake では扱わない」)。
`Bitmap` + `Graphics.FromImage` に黒地テーマ相当の設定で描画し、

- 通常節の描画領域に**背景色と異なる画素が存在する**(= 黒地に黒ではない)
- 対象節の描画領域に**選択背景色と異なる画素が存在する**

を固定する。ClearType により厳密な色一致は揺れるため、**色の一致ではなく症状
(背景と区別できるか)** を assert する。#72 の症状そのものを網にする。

### 5.3 既存テスト

`ForeColor` 改名に伴う `tests/kxEdit.Editor.Tests/Fakes/FakeImeOverlayHost.cs` の
プロパティ更新のみ。`ImeControllerTests` / `EditorControlImeTests` は無改変で通る想定
(通らなければ挙動不変の前提が崩れているので原因を追う)。

### 5.4 ミューテーション検証

**実施しない**。CLAUDE.md §4-A の禁止領域「テーマ(配色)の適用処理」に該当する。

## 6. 検証と手動確認

- `tools/pre-merge-check.ps1` で **EXIT 0** / 0 warning。
- `sr-regression.ps1` は**不要**。SR 経路(`kxEdit.Accessibility` / `EditorControl` の UIA 部 /
  App の Speech 系)に触れない。
- **実機目視は必要**(見た目の変更)。黒地 3 テーマで実際に IME 変換し、通常節・対象節の
  両方が読めることを確認する L5 チェックリストを起こしてユーザーに依頼する。
  自動テストは「背景と異なる画素がある」までしか言えず、実際の可読性は目視でしか判定できない。

## 7. 申し送り

- **本文の選択表示も同じ問題を抱えている**(別 Issue に切る)。`SelectionBack` が
  テーマ非連動の固定水色で、`FrameBuilder` は選択範囲内のテキストも常に `style.Foreground` で
  描く(`src/kxEdit.Core/Layout/FrameBuilder.cs:128,177`)。このため黒地テーマでは
  選択したテキストが「水色地に白/黄/緑」となり読めない。#72 より影響が広く、
  `ViewportStyle` への `SelectionFore` 追加と `FrameBuilder` のテキスト op 分割が要るため
  範囲が別。v0.2 の優先度判断にかける。その修正時に §4.2 の `OverlayTargetForeColor` の
  固定黒も合わせて見直す。
- **WPF 移行ブランチとの関係**: `feature/wpf-migration` では WPF 版が
  `OverlayForeColor => _style.Foreground` を返しており #72 は修正済み(設計書 §6 差異 10)。
  ただし対象節は §3 の退行を抱えたままなので、移行を再開する際は本設計の 2 色分割を
  WPF 版にも反映する。移行ブランチでは WinForms 版 `EditorControl` が削除済みのため、
  本修正とのマージ衝突は「削除を採る」で解消できる。
