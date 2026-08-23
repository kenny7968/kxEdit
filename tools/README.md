# tools/ スクリプト一覧

`tools/` 配下は「ローカルで手動起動する運用スクリプト」の置き場。CI が自動で回すものは `.github/workflows/` にある。ここは「いつ・何のために叩くか」に絞って各スクリプトを説明する。

## 一覧

| スクリプト | 役割 | 実行タイミング |
|---|---|---|
| `pre-merge-check.ps1` | main マージ前のローカルゲート(CSharpier check + Release 0 警告 + 全テスト緑 + App.Tests の Debug 実行) | **main マージ前 必須** |
| `sr-regression.ps1` | SR 呼び出しパターン回帰スイート。`verify-uia-editor.ps1` + `word-sim.ps1` を一括実行するアグリゲータ | a11y 系変更のマージ前 + リリース前 |
| `verify-uia-editor.ps1` | `kxEdit.Editor.Smoke --uia` を起動し、UIA クライアントとして TextPattern / GetSelection / RangeFromPoint の疎通を PASS/FAIL 判定 | 通常は `sr-regression.ps1` 経由で呼ばれる |
| `word-sim.ps1` | 同じく `--uia` 起動先に対し NVDA の TextUnit.Word 呼び出しパターン(Expand/Move span/MoveEndpointByUnit)6 ケースを再現 | 通常は `sr-regression.ps1` 経由で呼ばれる |

## §1 `pre-merge-check.ps1`

main マージ前の**必須**ゲート。中身:

1. `dotnet tool restore`(CSharpier 等の local tool)
2. `dotnet csharpier check`(フォーマット検証)
3. Release ビルド 0 警告(`-warnaserror`)
4. Core / Editor / App の 3 テストプロジェクトを Release で実行(フィルタなし・LocalOnly を含む全数)
5. App.Tests を **Debug でもう一度**実行

5 が要る理由(Issue #48 最終レビュー Q-I-4): `Debug.Assert` は `[Conditional("DEBUG")]` なので
Release バイナリに残らない。`DocumentState.Path` の不変条件のように `Debug.Assert` で守っている網は、
Release だけのゲートでは 1 行も走らない=「網に見えるがゲート上は無効」になる。
**Core.Tests の Debug は含めない**(既知 S-5 で 4 件赤になり、ゲートが常時 NG になるため)。

CI(`.github/workflows/ci.yml`)は `Category!=LocalOnly` フィルタで走るため、`LocalOnly` の実ファイル I/O テストはこのローカルゲートでしか回らない。

```powershell
powershell -File tools\pre-merge-check.ps1
```

典拠: `docs/plans/2026-07-13-test-strategy-design.md` §2.1。

## §2 SR 呼び出しパターン回帰スイート(Phase 3)

**a11y 関連変更**(`kxEdit.Accessibility` / `EditorControl` の UIA 部 / Speech 系)のマージ前+リリース前に、UIA プロバイダが SR 呼び出しに正しく応答するかを一次スクリーニングする。**pre-merge には組み込まない**(UIA クライアントはフォアグラウンドのデスクトップセッションが必要で、常時実行に向かない)。

### `sr-regression.ps1`(アグリゲータ・通常はこれだけ叩く)

1. `kxEdit.Editor.Smoke` を Release ビルド(1 回のみ)
2. `verify-uia-editor.ps1` を実行
3. `word-sim.ps1` を実行

全 PASS で EXIT 0・1 つでも FAIL なら EXIT 1。

```powershell
# pwsh(PowerShell 7+)推奨。理由は下記 word-sim.ps1 の注意事項を参照。
pwsh -File tools\sr-regression.ps1
# 未インストール環境では WinPS 5.1 フォールバック(警告バナー表示)。
powershell -File tools\sr-regression.ps1
```

### `verify-uia-editor.ps1`(子スクリプト)

`kxEdit.Editor.Smoke --uia` を起動 → UIA クライアントとして `AutomationId="editor"` を掴む → TextPattern / GetSelection / RangeFromPoint の疎通を PASS/FAIL 判定。P5 Task 14 で導入。

### `word-sim.ps1`(子スクリプト)

同じく `--uia` 起動先に対し、NVDA の TextUnit.Word 呼び出しパターン(Expand / Move span / MoveEndpointByUnit)を 6 ケース再現。

**注意**: このスクリプトは日本語コメントが BOMless UTF-8 で入っており、WinPS 5.1 の日本語ロケール環境ではパーサが Shift-JIS 誤解釈してエラーになる。`sr-regression.ps1` は `pwsh` があれば優先使用してこれを回避する。単体実行するときも `pwsh` 推奨。

### 判定の限界(重要)

このスイートは「UIA プロバイダが SR 呼び出しに**正しく応答するか**」までしか検証しない。「SR が**実際に発声するか**」は検出できない。**PASS でも L5 の人手確認は省略しない**。

典拠: `docs/plans/2026-07-13-test-strategy-phase3-sr-perf-design.md` §1・`docs/plans/2026-07-16-test-strategy-phase3.md`。
