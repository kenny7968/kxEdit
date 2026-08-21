# yEdit → kxEdit 全面改名 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** アプリケーション名を `yEdit` から `kxEdit` へ全面改名する(挙動不変)。

**Architecture:** 機械置換が主体。`yEdit`(大文字 E)と `yedit`(全小文字)を**別々の工程**として
扱う。前者は識別子・表示名・パスで Task 1 が一括処理し、後者はプレビューのセキュリティ定数として
Task 2 が単独で扱う。各コミットはビルドとテストが通る状態を保つ。

**Tech Stack:** .NET 9 / C# / Git Bash(sed・git mv) / PowerShell(`tools/pre-merge-check.ps1`)

**設計書:** [2026-08-21-rename-to-kxedit-design.md](./2026-08-21-rename-to-kxedit-design.md)

---

## TDD についての例外

本作業は**新しい挙動を一切足さない**。したがって「失敗するテストを先に書く」工程は存在しない。
正しさの証明は**既存テストスイート**が担う。テストのアサーションとロジックは 1 行も変えず、
名前空間 `using` の機械置換のみを許す。Task 0 で取得するベースラインとの**件数一致**が、
置換事故が起きていないことの証拠になる。

## 全タスクで守る不変条件

1. **`docs/plans/` を絶対に置換対象に含めない**(CLAUDE.md §8 の策定時スナップショット)
2. **大文字小文字を区別する**。`sed` は常に `s/yEdit/kxEdit/g` とし、`(?i)` 相当を使わない
3. `git commit` に `--no-verify` を付けない(CLAUDE.md §6)
4. コミットメッセージ本文は日本語

---

## Task 0: ベースライン取得

**Files:** なし(計測のみ)

**Step 1: 作業ツリーが clean であることを確認**

```bash
git status --porcelain
```

Expected: 出力なし

**Step 2: ベースラインのゲートを実行**

PowerShell で実行する。

```powershell
tools/pre-merge-check.ps1
```

Expected: 最終行が EXIT 0。3 つのテストプロジェクトそれぞれについて `Failed: 0` を含む行が出る。

**Step 3: テスト件数を記録**

出力から 3 プロジェクトの `Passed:` の数値を控え、スクラッチパッドに書き出す。

```powershell
"Core=<N> Editor=<N> App=<N>" | Out-File -Encoding utf8 "$env:TEMP\kxedit-baseline.txt"
```

この 3 つの数値は Task 1 以降で**完全一致**しなければならない。1 件でもずれたら置換事故を疑い、
原因を特定するまで先に進まない。

**Step 4: コミットは行わない**

計測のみのため commit しない。

---

## Task 1: 構造改名(識別子・ディレクトリ・sln・CI・ツール)

`yEdit`(大文字 E)の全出現を `kxEdit` へ置換する。名前空間・アセンブリ名・プロジェクト名・
ディレクトリ名・表示文字列・`%AppData%` フォルダ名・CI のパス参照がここで一斉に変わる。
小文字 `yedit` は触らない。

**Files:**

- Modify: `src/**/*.cs` `tests/**/*.cs` `src/**/*.csproj` `tests/**/*.csproj`
- Rename: `src/yEdit.{Accessibility,App,Core,Editor}/` → `src/kxEdit.*/`
- Rename: `tests/yEdit.{App.Tests,Core.Bench,Core.Tests,Editor.Smoke,Editor.Tests}/` → `tests/kxEdit.*/`
- Rename: `yEdit.sln` → `kxEdit.sln`
- Modify: `.github/workflows/{ci,bench,release}.yml`
- Modify: `tools/*.ps1`

**Step 1: ビルド成果物を消す**

`git mv` がディレクトリごと動かすため、ロックされうる `bin/` `obj/` を先に消す。

```bash
find src tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
```

Expected: エラーなく終了

**Step 2: `.cs` と `.csproj` の識別子を置換**

```bash
grep -rl --binary-files=without-match 'yEdit' src tests --include='*.cs' --include='*.csproj' | xargs sed -i 's/yEdit/kxEdit/g'
```

**Step 3: 置換されてはいけないものが無傷か確認**

```bash
grep -n 'needle = ' tests/yEdit.Core.Tests/Accessibility/TextRangeProviderV2Tests.cs
```

Expected: `const string needle = "yedit";`(**小文字のまま**)

```bash
grep -rn 'yedit' src tests --include='*.cs' | wc -l
```

Expected: **59 行 / 13 ファイル**(Task 2 の対象。2026-08-21 の実測値。当初「20 前後」と
見積もっていたが約 3 倍だった)。**0 になっていたら小文字まで巻き込んだ事故**なので、
`git checkout -- src tests` で戻してやり直す。

**Step 4: sln を置換して改名**

```bash
sed -i 's/yEdit/kxEdit/g' yEdit.sln && git mv yEdit.sln kxEdit.sln
```

**Step 5: ディレクトリと csproj ファイル名を改名**

csproj を先に改名してからディレクトリを動かす(逆順だとパスが解決できない)。

```bash
for p in Accessibility App Core Editor; do
  git mv "src/yEdit.$p/yEdit.$p.csproj" "src/yEdit.$p/kxEdit.$p.csproj"
  git mv "src/yEdit.$p" "src/kxEdit.$p"
done
for p in App.Tests Core.Bench Core.Tests Editor.Smoke Editor.Tests; do
  git mv "tests/yEdit.$p/yEdit.$p.csproj" "tests/yEdit.$p/kxEdit.$p.csproj"
  git mv "tests/yEdit.$p" "tests/kxEdit.$p"
done
```

Expected: エラーなし。`ls -d src/*/ tests/*/` で `kxEdit.*` になる。

`src/yEdit.ScintillaProbe` と `src/yEdit.UiaProbe` は **git 管理外の残骸**(過去のプローブ
プロジェクトの `bin/` `obj/` だけが残ったもの)で、Step 1 でほぼ空になる。**改名しない**。
気になる場合は手で削除してよい。

**Step 6: CI とツールのパス参照を置換**

```bash
sed -i 's/yEdit/kxEdit/g' .github/workflows/*.yml tools/*.ps1
```

**Step 7: ローカルパス検出 regex を両対応に戻す**

Step 6 で `check-no-local-paths.ps1` の検出対象が新名だけになった。作業ディレクトリは
ユーザーが手で rename するまで旧名なので、**両方を検出**する形に直す。

`tools/check-no-local-paths.ps1` の該当行を次のように変える。

```powershell
    '(?i)([a-z]:|/[a-z])[\\/]src[\\/](yEdit|kxEdit)\b'
```

ヘルプ出力の行も `kxEdit / yEdit の両方` と読める文面へ直す。

**Step 8: ps1 の BOM が生きているか確認**

CLAUDE.md §10 のとおり、ユーザーが直接起動する ps1 は BOM 付き UTF-8 でなければならない。

```bash
for f in tools/*.ps1; do printf '%s: ' "$f"; head -c 3 "$f" | od -An -tx1; done
```

Expected: 元から BOM(`ef bb bf`)を持っていたファイルが**引き続き** BOM を持つ。
失われていたら `sed` が壊したので復元する。

**Step 9: ビルド**

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
```

Expected: `0 Warning(s)` `0 Error(s)`

**Step 10: テストとベースライン照合**

```powershell
tools/pre-merge-check.ps1
```

Expected: EXIT 0。かつ 3 プロジェクトの `Passed:` が `$env:TEMP\kxedit-baseline.txt` の
数値と**完全一致**。

**Step 11: 表示面と AppData が実際に変わったか確認**

```bash
grep -rn '"kxEdit"' src --include='*.cs'
```

Expected: 9 行。内訳は表示・キャプション 4 行(`FileController.cs` ×2 / `MainForm.cs` ×2)と
`%AppData%` フォルダ名 5 行(`SettingsStore` / `BackupStore` / `LastSessionBuffersStore` /
`SessionLayoutStore` / `PreviewUserDataFolder`)。

```bash
grep -n 'AssemblyName' src/kxEdit.App/kxEdit.App.csproj
grep -n 'ZIP:' .github/workflows/release.yml
```

Expected: `<AssemblyName>kxEdit</AssemblyName>` と
`ZIP: kxEdit-${{ github.ref_name }}-win-x64.zip`

**Step 12: コミット**

```bash
git add -A
git commit -m "refactor(all): 識別子・プロジェクト・CI を kxEdit へ改名"
```

本文には次を含める。

```
名前空間・アセンブリ名・プロジェクト名・ディレクトリ・ソリューション・
表示名・%AppData% フォルダ名・CI/ツールのパス参照を一括で改めた。

挙動不変。テストのアサーションとロジックは変更しておらず、
テスト件数は改名前と完全一致する。

小文字 yedit(プレビューの仮想ホストと CSP)は本コミットでは触れていない。
```

---

## Task 2: プレビューのセキュリティ定数

小文字 `yedit` を扱う。**仮想ホストと CSP と遷移 Block 判定は 3 点セットで同期している**ため、
1 つでも取り残すと MD-H-1 の対策が素通しになる。CLAUDE.md §3 の前倒し条件(WebView / プレビュー)
に該当するので、このタスク完了時に**脆弱性レビューを実施**する。

**Files:**

- Modify: `src/kxEdit.Core/Text/MarkdownRenderer.cs`
- Modify: `src/kxEdit.App/PreviewNavigationPolicy.cs`
- Modify: `src/kxEdit.App/MarkdownPreviewForm.cs`
- Modify: `src/kxEdit.Editor/EditorControl.cs`(コメント内の原則名)
- Test: `tests/kxEdit.App.Tests/PreviewCspHeaderInjectorTests.cs`
- Test: `tests/kxEdit.App.Tests/PreviewNavigationPolicyTests.cs`
- Modify: `tests/kxEdit.Core.Bench/Program.cs`(一時ファイル名の接頭辞)

**注意: `sed -i` は CRLF を LF に潰す。** Task 1 で 342 ファイルを壊し、CSharpier ゲートで
発覚して修復する羽目になった。`.gitattributes` の `* text=auto eol=crlf` と
`.csharpierrc.json` の `"endOfLine": "crlf"` が CRLF を要求しているためである。
置換後に必ず `dotnet csharpier format` を掛け、`git ls-files --eol` で対象が
`i/lf w/crlf` になっていることを確認する(ODB が LF・作業ツリーが CRLF が**正常**)。

**Step 1: 対象を数える**

```bash
grep -rn 'yedit' src tests --include='*.cs' | wc -l
```

Expected: **59**(Task 1 Step 3 と同じ件数)。上位は `MarkdownRendererTests.cs` 19 /
`PreviewCspHeaderInjectorTests.cs` 11 / `PreviewNavigationPolicyTests.cs` 7

**Step 2: 一括置換**

`_yedit`(スタイルシートのパス片)・`yedit.preview`(仮想ホスト)・
`yedit-sighted-users-first-class`(原則名)・`yedit-largeline-`(Bench の一時ファイル名)を
すべて `kxedit` へ寄せる。いずれも全小文字で一貫している。

```bash
grep -rl --binary-files=without-match 'yedit' src tests --include='*.cs' | xargs sed -i 's/yedit/kxedit/g'
```

**Step 3: テストデータの扱いを確認**

```bash
grep -n 'needle = ' tests/kxEdit.Core.Tests/Accessibility/TextRangeProviderV2Tests.cs
```

Expected: `const string needle = "kxedit";`

この 1 行は**アプリ名の参照ではなく単なる検索対象データ**である。同じ文字列を探して同じ結果を
期待する自己完結したテストなので、値が変わっても意味は変わらない。**もしこの行が原因でテストが
落ちたら**、テストが needle の具体的な値に依存している設計上の問題なので、値を `"yedit"` に戻す。

**Step 4: 3 点セットが揃って変わったか確認**

```bash
grep -n 'PreviewVirtualHost\|PreviewStylesheetPath\|style-src' src/kxEdit.Core/Text/MarkdownRenderer.cs
```

Expected: `"kxedit.preview"` / `"/_kxedit/styles.css"` / CSP コメントの
`style-src 'self' https://kxedit.preview` が揃っている

```bash
grep -rn 'yedit' src tests --include='*.cs'
```

Expected: 出力なし

**Step 5: ビルドとテスト**

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
tools/pre-merge-check.ps1
```

Expected: 0 Warning / EXIT 0 / テスト件数がベースラインと一致

**Step 6: コミット**

```bash
git add -A
git commit -m "refactor(preview): プレビューの仮想ホストと CSP を kxedit へ改名"
```

**Step 7: 脆弱性レビューを依頼**

CLAUDE.md §3 の前倒し例外に従い、このタスクだけを対象に**別エージェントで脆弱性レビュー**を
実施する。観点は次の 2 つ。

- 仮想ホスト・CSP の `style-src`・遷移 Block 判定のいずれかが旧名のまま残っていないか
- 大文字小文字を区別しない比較(`https://yedit.preview` を大小混在で書いた攻撃 URL への耐性)が
  新名でも維持されているか

---

## Task 3: 文書・課題テンプレート・変更履歴

**Files:**

- Modify: `README.md` `CLAUDE.md` `SECURITY.md` `tests/README.md`
  `docs/lint-format-setup.md` `tools/README.md`
- Modify: `.github/ISSUE_TEMPLATE/*.yml`(`bug_report.yml` `feature_request.yml` `config.yml`)
- Modify: `.editorconfig`(コメント内の旧パス参照 9 行)
- Modify: `.gitattributes`(コメント 1 行)
- Modify: `変更履歴.txt`

`.editorconfig` と `.gitattributes` の `yEdit` はすべて `#` コメント内で、セクション
ヘッダは glob なので**機能への影響はない**。ただし Task 5 Step 3 の残存確認 grep に
引っかかるため、ここで回収する。

**GitHub URL の扱い(要注意)**: `.github/ISSUE_TEMPLATE/` には
`github.com/kenny7968/yEdit` を指す URL が 2 つある。これを新名へ書き換えると、
**GitHub リポジトリ自体を改名するまで 404 になる**(GitHub のリダイレクトは
旧名 → 新名の向きにしか効かない)。新名へ書き換えたうえで、**リポジトリ改名を
マージ前の前提条件として PR description に明記する**(Task 5 Step 6)。

**Step 1: 「現在」を説明する文書だけを置換**

CLAUDE.md §8 が同期更新の対象と定めているのはこの範囲のみ。`docs/plans/` は含めない。

```bash
sed -i 's/yEdit/kxEdit/g; s/yedit/kxedit/g' README.md CLAUDE.md SECURITY.md tests/README.md docs/lint-format-setup.md tools/README.md .github/ISSUE_TEMPLATE/*.yml .editorconfig .gitattributes
```

**Step 2: `docs/plans/` が無傷であることを確認**

```bash
git status --porcelain docs/plans/
```

Expected: 出力なし(本計画と設計書は既にコミット済みのため)。既存のスナップショット文書が
1 つでも出てきたら誤爆なので戻す。

**Step 3: 変更履歴に改名を追記**

`変更履歴.txt` の**過去のエントリは書き換えない**。実在した zip 名を改変すると履歴が嘘になる。
先頭に新エントリを足し、改名の事実と `%AppData%` の非移行を明記する。

```
- アプリケーション名を yEdit から kxEdit へ変更しました。
  設定・バックアップ・セッション復元データの保存先が
  %AppData%\yEdit から %AppData%\kxEdit へ変わります。
  自動移行は行いません。旧フォルダの内容が必要な場合は手動でコピーしてください。
  異常終了時の未保存バックアップが旧フォルダに残っている場合があります。
```

**Step 4: 残存確認**

```bash
grep -rn 'yEdit' README.md CLAUDE.md SECURITY.md tests/README.md tools/README.md docs/lint-format-setup.md
```

Expected: 出力なし。`変更履歴.txt` には過去エントリと Step 3 の新エントリに `yEdit` が
残る(意図的)。

**Step 5: コミット**

```bash
git add -A
git commit -m "docs: 現況文書と課題テンプレートを kxEdit へ更新"
```

---

## Task 4: 説明書(ユーザー校閲が前提)

**Files:**

- Rename: `説明書/yEdit説明書.md` → `説明書/kxEdit説明書.md`
- Modify: 同ファイル本文(14 箇所)

CLAUDE.md §8 は `説明書/yEdit説明書.md` を**ユーザー編集版が正**と定めており、勝手に改稿しては
ならない。

**Step 1: 変更案を提示する**

ファイル名変更と本文の `yEdit` → `kxEdit` 置換、および `%AppData%` 非移行の注記追加を
**diff の形でユーザーに見せ、承認を得てから**適用する。

**Step 2: 承認後に適用**

```bash
git mv 説明書/yEdit説明書.md 説明書/kxEdit説明書.md
sed -i 's/yEdit/kxEdit/g' 説明書/kxEdit説明書.md
```

**Step 3: 参照元の追随**

`README.md` が説明書へリンクしている。Task 3 の置換でパスが `説明書/kxEdit説明書.md` に
なっているはずなので、実ファイル名と一致するか確認する。

```bash
grep -n '説明書/' README.md && ls 説明書/
```

Expected: 両者が一致

**Step 4: コミット**

```bash
git add -A
git commit -m "docs(manual): 説明書を kxEdit へ改名(ユーザー校閲済み)"
```

---

## Task 5: 最終ゲートとレビュー

**Step 1: 品質ゲート**

```powershell
tools/pre-merge-check.ps1
```

Expected: EXIT 0 / 0 Warning / テスト件数がベースラインと完全一致

**Step 2: 配布物の構成をベースラインと比較**

```powershell
dotnet publish src/kxEdit.App -c Release -r win-x64 --self-contained false
```

Expected: 出力ディレクトリの DLL 構成が改名前と一致(名前の `yEdit.*` → `kxEdit.*` を除いて)。
`kxEdit.exe` が生成されていること。

**Step 3: 残存確認**

```bash
grep -rn 'yEdit\|yedit' --exclude-dir=.git --exclude-dir=obj --exclude-dir=bin --exclude-dir=plans . | grep -v '変更履歴.txt' | grep -v 'check-no-local-paths.ps1' | grep -v 'docs/report-pctalker-speech'
```

Expected: 出力なし。意図的に旧名を残すのは次の 3 つだけ。

- `変更履歴.txt` — 過去のリリースで実在した zip 名。書き換えると履歴が嘘になる
- `tools/check-no-local-paths.ps1` — `yEdit|kxEdit` の両対応 regex
- `docs/report-pctalker-speech/` — 過去の調査記録。`docs/plans/` と同じ扱いの歴史文書

**Step 4: 最終ブランチレビュー(2 パス)**

CLAUDE.md §3 工程 5 に従い、**独立した別エージェントを 2 回起動**する。1 起動に混載しない。

- **コード品質パス** — 観点は「機械置換が意味を壊した箇所がないか」(コメント内の固有名詞・
  テストデータ・URL・履歴文書)。ミューテーション検証は本作業に**適用しない**。挙動を変えて
  いないため、変異させる対象の実装行が存在しない。
- **脆弱性パス** — 観点はプレビュー経路(Task 2)と `%AppData%` パス構築。特に**旧フォルダ
  `%AppData%\yEdit` を参照するコードが 1 つも残っていないこと**(残っていると新旧が混ざり、
  バックアップ整合性が壊れる)。

指摘は fixup commit で反映する(元 commit を書き換えない)。

**Step 5: L5 チェックリストを作成**

`docs/plans/2026-08-21-rename-to-kxedit-l5-checklist.md` を作り、設計書 §8 の 3 項目を実機 SR
検証の手順として書き下す。最優先は **「NVDA が kxEdit をどう読むか」と「MyEdit と聞き分けられるか」**。

**Step 6: PR 作成**

```bash
git push -u origin feature/rename-to-kxedit
```

PR description に必ず含める申し送り:

- **マージ前の前提条件**: GitHub リポジトリ名を `kxEdit` へ変更すること。
  `.github/ISSUE_TEMPLATE/` の URL が新名を指すため、改名前にマージすると
  課題テンプレートのリンクが 404 になる
- `%AppData%` は**移行しない**。既存ユーザーの設定は初期化され、異常終了時の未保存バックアップが
  旧フォルダに孤児として残る
- ローカル作業ディレクトリの rename は**未実施**(ユーザー操作)。
  `tools/check-no-local-paths.ps1` が両対応 regex なので旧名のままでも検出は効く
- バージョン番号を上げるかは**未決**(`v0.1.1` 据え置き / `v0.2.0`)
- L5 実機 SR 検証の実施状況

---

## 完了の定義

- `tools/pre-merge-check.ps1` が EXIT 0
- テスト件数が Task 0 のベースラインと完全一致
- Task 5 Step 3 の残存確認で `yEdit` / `yedit` が意図した 2 ファイル以外に出ない
- 2 パスのレビュー指摘が fixup commit で解消済み
- L5 チェックリストが作成され、ユーザーの実機検証を待てる状態
