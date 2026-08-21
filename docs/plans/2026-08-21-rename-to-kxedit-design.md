# yEdit → kxEdit 全面改名 — 設計書

- 日付: 2026-08-21
- 対象: リポジトリ全体(`docs/plans/` を除く)
- 出自: 「yEdit」が PC-Talker 同梱の「MyEdit」と聞き間違えられるという利用者の指摘

## 0. 要旨

アプリケーション名を `yEdit` から `kxEdit` へ全面改名する。名前空間・プロジェクト名・
ディレクトリ・ソリューション・実行ファイル・配布 zip・表示名・ユーザーデータ格納先・
CI / ツール・文書までを一括で改める。`docs/plans/` の日付付き文書は §8 の
「策定時スナップショット」規範に従い対象外とする。

挙動不変の改名である。テストのアサーションとロジックは一切変えず、既存テストの全緑と
件数一致をもって証明する。

## 1. 名称選定の経緯

### 1.1 問題の構造

`yEdit` が `MyEdit` と混同される原因は、**先頭の `y` が単独の文字であること**にある。
日本語 TTS は語頭の単独文字を弱く短く読むため、`yEdit`(ワイエディット) と
`MyEdit`(マイエディット) は先頭 1 モーラの差しか持たない。文字を差し替えても
この構造は変わらない。

### 1.2 検討し脱落した候補

意味が素直な「英語の平易な語 + Edit」は、ほぼすべて既存ソフトに占有されていた。

| 候補 | 脱落理由 |
|---|---|
| `ClearEdit` | Windows のメモ帳代替エディタが既存(+ 文章校正ソフト ClearEdits) |
| `PlainEdit` | PlainEdit.NET(Windows 用テキストエディタ)が既存 |
| `CleanEdit` | cleanEdit(Windows 8/RT のミニマルエディタ)/ Clean Editor が既存 |
| `CrispEdit` | CRiSP Programmers Editor と音が衝突 |
| `BasicEdit` / `SmoothEdit` / `SolidEdit` | 画像エディタ / 動画編集 / CAD コマンドが既存 |
| `PaperEdit` | 映像編集の実務用語「ペーパーエディット」と衝突 |
| `HandyEdit` | 衝突はないが「ハンディキャップ」を連想させ、全盲専用エディタに見える。§2 の
  「晴眼・弱視ユーザーも第一級」に反するため除外 |
| `kEditor` | KEDIT(Mansfield Software・Windows 10/11 対応で現役)とほぼ同名。
  さらに K2Editor(日本語圏の定番フリーテキストエディタ)と「ケー…エディタ」の骨格を共有 |
| `klEditor` | `kl` が日本語に存在しない子音連続で TTS ごとに読みが割れる。
  加えて小文字 `l` は大文字 `I` / 数字 `1` と判別できず、弱視ユーザーへの配慮と自己矛盾する |
| `KiteEdit` | KDE の定番エディタ Kate と「カイト / ケイト」の一母音差 |
| `jxEdit` | Java 製の著名エディタ jEdit と 1 文字違いかつ同カテゴリ |
| `Penguin Editor` | 同名の軽量コードエディタが既存 |

### 1.3 kxEdit を選んだ理由

- 検索した範囲で既存のテキストエディタと衝突しない
- `x` は `l` と違い大文字 `I` / 数字 `1` と混同しない字形を持つ
- 「ケーエックスエディット」は 11 モーラあり、「マイエディット」と情報量から異なる
- 現行 `yEdit` の「小文字始まり + Edit」という表記の連続性を保つ

## 2. 命名規則(確定値)

| 対象 | 新しい値 |
|---|---|
| 製品名・表示名 | `kxEdit` |
| 名前空間 / アセンブリ | `kxEdit.Core` / `kxEdit.Editor` / `kxEdit.Accessibility` / `kxEdit.App` |
| テストプロジェクト | `kxEdit.Core.Tests` / `kxEdit.Editor.Tests` / `kxEdit.App.Tests` / `kxEdit.Editor.Smoke` / Bench |
| ソリューション | `kxEdit.sln` |
| 実行ファイル | `kxEdit.exe` (`AssemblyName=kxEdit`) |
| 配布 zip | `kxEdit-vX.Y.Z-win-x64.zip` |
| ユーザーデータ | `%AppData%\kxEdit\` |
| プレビュー仮想ホスト | `kxedit.preview` (全小文字。現行 `yedit.preview` と同形式) |

名前空間が小文字始まりなのは C# の慣例から外れるが、現行 `yEdit.Core` が
`TreatWarningsAsErrors` 下で 0 warning を維持している以上、同形の `kxEdit.Core` も通る。
**現状踏襲**とし、改名と命名規則の是正を混ぜない。

## 3. 影響範囲(実測)

| 面 | 実体 |
|---|---|
| 表示名 | `MainForm.cs:110` ウィンドウタイトル / `MainForm.cs:700` About の `yEdit v0.1.1` / `MainForm.cs:750` タイトル組み立て / `FileController.cs` の MessageBox キャプション |
| ユーザーデータ | `SettingsStore` / `BackupStore` / `LastSessionBuffersStore` / `SessionLayoutStore` / `PreviewUserDataFolder` の 5 系統 |
| 配布物 | `src/yEdit.App/yEdit.App.csproj` の `AssemblyName` / `release.yml` の `ZIP` 変数 |
| 内部識別子 | 名前空間 4 つ・プロジェクト / ディレクトリ名・`yEdit.sln`(src 2,955 + tests 3,984 箇所) |
| セキュリティ定数 | `MarkdownRenderer.PreviewVirtualHost` + CSP の `style-src` 文字列 + `PreviewNavigationPolicy` の Block 判定 |
| 文書・CI・ツール | README / CLAUDE.md / SECURITY.md / tests/README.md / workflows 3 本 / `check-no-local-paths.ps1` の regex |

### 3.1 SR 経路への露出は表示名のみ

`yEdit.Accessibility` と `yEdit.Editor` を調査した結果、UIA が公開する値に `yEdit` は
含まれていない(`AutomationId` は `"editor"` 固定)。読み上げ文字列としての `yEdit` は
App 層のウィンドウタイトル・ダイアログキャプション・About の 3 箇所に限られる。

## 4. タスク分割

一括 sed 一発は採らない。ディレクトリ移動は git の履歴追跡と噛み合わせる必要があり、
セキュリティ定数は独立したレビューを要するため。

1. **コード識別子の置換** — `src/` `tests/` の `.cs` で `yEdit` → `kxEdit`。
   **大文字小文字を区別**し、小文字 `yedit` は触らない(T4 の管轄)
2. **プロジェクト / ディレクトリ / sln** — `git mv` で 9 ディレクトリ + csproj ファイル名、
   sln のパス、`ProjectReference`、`InternalsVisibleTo`
3. **表示名・AppData・配布物** — ウィンドウタイトル / About / MessageBox キャプション /
   `AssemblyName` / `release.yml` の ZIP 名 / AppData 5 系統
4. **セキュリティ定数** — `PreviewVirtualHost` と CSP 文字列と遷移 Block 判定の 3 点を同時に。
   CLAUDE.md §3 の前倒し条件(WebView / プレビュー)に該当するため**脆弱性レビューを実施**
5. **文書・CI・ツール** — README / CLAUDE.md / SECURITY.md / tests/README.md /
   `docs/lint-format-setup.md` / workflows / `check-no-local-paths.ps1`
6. **説明書** — `説明書/yEdit説明書.md`。CLAUDE.md §8 で「ユーザー編集版が正・勝手に改稿しない」
   と定めているため、ファイル名変更と本文置換の**案を出してユーザー校閲を受ける**

## 5. 機械置換の安全策

### 5.1 除外するもの

- `docs/plans/` — §8 の策定時スナップショット
- `obj/` `bin/` — 生成物(再生成される)
- 小文字 `yedit` — T4 の管轄。T1 で巻き込むと CSP と Block 判定の同期が崩れる
- `変更履歴.txt` — 過去のリリースで実在した zip 名を書き換えると履歴が嘘になる。
  据え置き、改名の事実を新規エントリとして追記する

### 5.2 ローカルパス検出 regex

`check-no-local-paths.ps1` は `src[\/]yEdit` をリポジトリ絶対パスの検出に使っている。
作業ディレクトリ `<repo>` はユーザーが手で rename するまで旧名のままなので、
当面 **`yEdit|kxEdit` の両方を検出**する形にする。片方だけにすると検出が抜ける。

## 6. 挙動不変の証明

名前空間が変わる以上テストの `using` は変わるが、**アサーションとテスト本体のロジックは
1 行も変えない**。証拠を 3 つ置く。

- `tools/pre-merge-check.ps1` が EXIT 0(Release 0 warning + 3 プロジェクト全緑)
- **テスト件数が改名前後で完全一致**すること。増減があれば置換事故の証拠になる
- `dotnet publish` 出力の DLL 構成が改名前後で一致(名前を除いて)

## 7. AppData は移行しない

`%AppData%\yEdit\` から `%AppData%\kxEdit\` へ移す処理は**実装しない**(ユーザー判断)。
帰結を PR description とリリースノートに明記する。

- 既存ユーザーの設定は初期化される
- 異常終了時の未保存バックアップが `%AppData%\yEdit\backups\` に**孤児として残る**
- 旧フォルダの削除処理は入れない。ユーザーが自分で判断できる状態に留める

## 8. L5 実機 SR 検証(必須)

**`kxEdit` を NVDA が実際にどう読むかは、実機で確認するまで分からない。**
「ケーエックスエディット」を想定しているが、TTS が `kx` を綴り読みするか音節化するかは
実装依存である。読みが想定と違っても MyEdit との衝突は解消されるが、本改名の目的に
直結する項目なので最優先で確認する。

1. `kxEdit` の読みと、**MyEdit と聞き分けられること**(本改名の目的そのもの)
2. ダイアログキャプションの読み
3. エディタ本体の文字 / 行 / 単語読みに退行がないこと

## 9. スコープ外・申し送り

- **GitHub リポジトリ名の変更** — ユーザー操作。GitHub がリダイレクトを張るため既存 clone は動作継続
- **ローカル `<repo>` の rename** — ユーザー操作
- **バージョン番号** — 改名を機に上げるか(`v0.1.1` 据え置き / `v0.2.0`)はリリース時のユーザー判断
- **命名規則の是正** — 名前空間の小文字始まりを PascalCase に改める話は本改名に混ぜない
