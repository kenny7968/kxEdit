# kxEdit

スクリーンリーダー対応の Windows 用テキストエディタ。

## 特徴

- **スクリーンリーダー対応**: NVDA など UIA (UI Automation) 対応 SR での読み上げに最適化。
- **タブ / 検索・置換 / grep / 自動バックアップ** など日常編集機能を一通り搭載。
- **マークダウンプレビュー** (WebView2 + Markdig)。
- **CSV モード**: CSV ファイルを表として移動しながら読み書き。
- **禁則処理を意識した整形** など日本語テキスト編集向け配慮。

エンドユーザ向けの使い方は [`説明書/kxEdit説明書.md`](./説明書/kxEdit説明書.md) を参照。

## 動作要件 (利用側)

- Windows 11 (x64)
- [.NET 9 デスクトップランタイム](https://dotnet.microsoft.com/ja-jp/download/dotnet/9.0)
- WebView2 ランタイム (マークダウンプレビューで使用。Windows 11 に標準搭載)

配布物は GitHub Releases から `kxEdit-vX.Y.Z-win-x64.zip` として取得可能 (zip 展開のみ、インストーラなし)。

## 技術スタック

- **言語 / ランタイム**: C# / .NET 9 (`net9.0` / `net9.0-windows`)
- **UI**: Windows Forms + WPF (UIA 型導入のため一部プロジェクトで `UseWPF`)
- **アクセシビリティ**: UI Automation (v2 プロバイダを内製)
- **主要ライブラリ**
  - [Markdig](https://github.com/xoofx/markdig) — マークダウン → HTML
  - [Microsoft.Web.WebView2](https://learn.microsoft.com/microsoft-edge/webview2/) — プレビュー描画
  - [UTF.Unknown](https://github.com/CharsetDetector/UTF-unknown) — 文字コード判定
- **開発サポート**
  - [CSharpier](https://csharpier.com/) — フォーマッタ (dotnet local tool)
  - [Husky.Net](https://github.com/alirezanet/Husky.Net) — Git pre-commit
  - [Roslynator](https://github.com/dotnet/roslynator) / [SonarAnalyzer.CSharp](https://github.com/SonarSource/sonar-dotnet) — 静的解析
- **CI**: GitHub Actions (`.github/workflows/{ci,bench,release}.yml`)
- **テスト**: xUnit (Core / Editor / App + Smoke + Bench の 5 プロジェクト)

## アーキテクチャ

4 レイヤ構成。上位レイヤのみ下位に依存し、逆向きは禁止。

| Layer | Project | TargetFramework | 役割 |
|-------|---------|-----------------|------|
| App | `kxEdit.App` | `net9.0-windows` (WinExe) | エントリポイント / WinForms UI / ダイアログ / コントローラ / SR 通知 |
| Editor | `kxEdit.Editor` | `net9.0-windows` | 自作 `EditorControl` (描画・キー入力・IME・キャレット・UIA テキスト提供) |
| Accessibility | `kxEdit.Accessibility` | `net9.0-windows` | UIA v2 プロバイダ (`TextControlProviderV2` / `TextRangeProviderV2` 等) |
| Core | `kxEdit.Core` | `net9.0` | テキストバッファ / レイアウト / 検索 / CSV / 設定 / IO / バックアップ (UI 非依存) |

```
App  ──▶  Editor  ──▶  Accessibility
 └──────▶  Core  ◀───────────┘
```

- `EditorControl` は WinForms `Control` を継承する **自作エディットコントロール** (Scintilla 等の外部エンジンは使用しない)。
- SR への能動通知は App 層の `UiaAnnouncer` から UIA (`RaiseAutomationNotification`) に一本化
- `EditorControl` は partial 分割 (`.Caret` / `.Ime` / `.Input` / `.Paint` / `.Uia`) + Adapter/Controller 委譲で責務分離。

## 開発環境セットアップ

### 前提

- Windows 11
- [.NET 9 SDK](https://dotnet.microsoft.com/ja-jp/download/dotnet/9.0) (9.0.x 系)
- Git for Windows
- IDE: Visual Studio 2022 (17.9 以降) / Rider / VS Code いずれか

### 初回セットアップ (clone 後 1 回だけ)

リポジトリルートで、以下を上から順に実行:

```powershell
dotnet tool restore
dotnet husky install
git config blame.ignoreRevsFile .git-blame-ignore-revs
```

- `dotnet tool restore` — CSharpier / Husky.Net を `.config/dotnet-tools.json` から復元
- `dotnet husky install` — `.husky/pre-commit` を Git hook として有効化 (commit 時に staged `*.cs` を自動整形+staged ファイルのローカルパス検出)
- `git config blame.ignoreRevsFile ...` — 一括整形 commit を `git blame` の対象から除外

CI (`.github/workflows/ci.yml`) でも `dotnet csharpier check` が実行されるため、
上記 3 コマンドを実行せずに commit すると整形漏れで CI が落ちる可能性がある。
手動で確認したい場合は `dotnet csharpier check .` を、まとめて再整形したい場合は
`dotnet csharpier format .` を実行する。

Lint / Format の詳細ルール (CSharpier の運用、アナライザ抑止方針、`.editorconfig` 変更時の理由コメント必須ルール 等) は
[`docs/lint-format-setup.md`](./docs/lint-format-setup.md) を参照。

### ビルドとテスト

```powershell
# 全ビルド (警告=エラー)
dotnet build kxEdit.sln -c Release -warnaserror

# 個別プロジェクトのテスト
dotnet test tests/kxEdit.Core.Tests   -c Release --no-build
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests    -c Release --no-build
```

`Category=LocalOnly` のテストは実 SR (NVDA 等) が必要なため、CI では除外している。ローカルでは `--filter` を外して実行可能。

### main マージ前ゲート

`tools/pre-merge-check.ps1` を実行し、以下がすべて緑であることを確認する:

```powershell
powershell -File tools\pre-merge-check.ps1
```

Format check → Release ビルド (0 警告) → 3 テストプロジェクト全緑 → App.Tests を Debug で再実行、で PASS。

最後の Debug 実行は `Debug.Assert` (`[Conditional("DEBUG")]` で Release バイナリに残らない) を
使った網をゲートに載せるためのステップ。Core.Tests の Debug は既知の失敗があるため含めない。

CI (`ci.yml`) とほぼ同一のゲートだが、次の 2 点だけ異なる:

- ローカルパス検出 (`tools/check-no-local-paths.ps1`) は本スクリプトに含まれない (CI と pre-commit フックが実行する)。
- `Category=LocalOnly` のテストは CI では除外されるが、本スクリプトはフィルタなしで全件実行する。

### 実行

```powershell
dotnet run --project src/kxEdit.App -c Release
```

## リポジトリ構成

```
kxEdit/
├── .config/dotnet-tools.json    CSharpier / Husky 版指定
├── .github/workflows/           CI (ci.yml / bench.yml / release.yml)
├── .husky/                      pre-commit フック定義
├── Directory.Build.props        共通ビルド設定 (警告=エラー、アナライザ)
├── docs/                        設計書・plans・セットアップ手引き
├── src/                         プロダクションコード (Core / Editor / Accessibility / App)
├── tests/                       テスト (Core / Editor / App + Smoke + Bench)
├── tools/                       pre-merge-check.ps1 等の運用スクリプト
└── 説明書/kxEdit説明書.md        エンドユーザ向けマニュアル (配布物同梱)
```

## リリース

タグ `v*` を push すると `.github/workflows/release.yml` が起動し、
フレームワーク依存ビルド (win-x64) + 説明書を zip にまとめて GitHub Releases に自動アップロードする。

**タグを打つ前に、次の 2 つをタグと同じバージョンに揃えた commit を積むこと。**
揃っていないとリリース CI が最初のステップで失敗する。

| 対象 | 内容 |
|------|------|
| `Directory.Build.props` の `<Version>` | バージョンの唯一の管理点。バージョン情報ダイアログの表示もここから来る |
| `変更履歴.txt` の見出し | `## vX.Y.Z` 形式。zip に同梱されるため見出しだけ古いまま出荷されるのを防ぐ |

配布物のアセンブリバージョンはタグから `-p:Version` で渡されるので、
`Directory.Build.props` の値は開発ビルドの既定値として働く。

## 開発について

本プロジェクトは [Claude Code](https://claude.com/claude-code) (Anthropic) + [superpowers プラグイン](https://github.com/obra/superpowers) を主要な実装手段として開発している。

## 補足

当初は PC-Talker への対応も予定しており、App レイヤーでスクリーンリーダーごとの差異を吸収する仕組み (SR ルート選択・Announcer 層の SR 別分岐) を導入していた。しかし諸々の理由で方針変更して PC-Talker の対応は見送ることとし、2026-07-13 にこの仕組みは削除済み。現在は UIA 単一経路のみで、**実行時に SR を判定・分岐するコードは存在しない**。

`IAnnouncer` は当時の多態シームに由来する単一実装のインターフェースだが、現在は通知文言を検証するテストシームとして機能しているため意図的に残している。

## ライセンス

MIT License — 詳細は [`LICENSE`](./LICENSE) を参照。

サードパーティ依存ライブラリのライセンスは各パッケージの表記に従う。
