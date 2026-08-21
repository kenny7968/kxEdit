# バージョンをアセンブリで一元管理する — 設計書

- 日付: 2026-08-22
- 対象: `Directory.Build.props` / `src/kxEdit.Core/Text/` / `src/kxEdit.App/` / `.github/workflows/release.yml`
- 出自: PR #39(kxEdit 改名)で表示バージョンを手で書き換えた際、
  ハードコードが唯一の管理点になっていることが露呈した

## 0. 要旨

バージョン情報ダイアログの表示文字列はソース中にハードコードされていた
(`"kxEdit v0.1.1"`)。これをアセンブリ属性からの実行時読み取りに置き換え、
バージョンの管理点を `Directory.Build.props` の `<Version>` 一箇所に集約する。

## 1. 調査 — 当初の想定は 1 点間違っていた

### 1.1 リリース成果物は既にタグからバージョンを受け取っている

着手前は「タグから作る zip 名と、ソースにハードコードされた表示バージョンが
independently にずれ、`zip は v0.3.0 なのに About は v0.2.0` という状態が出荷されうる」と
考えていた。**これは誤りだった。**

`release.yml` は配布ビルドで既にタグを渡している。

```yaml
$v = $env:TAG.TrimStart('v')      # v0.2.0 → 0.2.0
...
dotnet publish src/kxEdit.App ... "-p:Version=$env:VERSION"
```

したがって**リリース成果物のアセンブリバージョンは常にタグと一致する**。
ずれていたのは、そのアセンブリバージョンを誰も読んでいなかったことだけである。
実行時に読むようにすれば、リリース版の表示は自動的にタグへ追随する。

### 1.2 .NET 8 以降は InformationalVersion に commit SHA が付く

実測(SDK 10.0.301):

```
[assembly: AssemblyInformationalVersionAttribute("1.0.0+03ffae3ca8ec50b1acf916f30f5002042d8ec604")]
[assembly: AssemblyFileVersionAttribute("1.0.0.0")]
[assembly: AssemblyVersionAttribute("1.0.0.0")]
```

`IncludeSourceRevisionInInformationalVersion` が既定 true のため、
`InformationalVersion` は `<version>+<commit sha>` になる。素直に表示すると
`kxEdit v1.0.0+03ffae3ca8ec...` となるので、**最初の `+` 以降を落とす**必要がある。

`AssemblyVersion` / `FileVersion` を使えば SHA は付かないが、4 部構成
(`0.2.0.0`)になり、プレリリース識別子(`0.2.0-rc.1`)も落ちる。表示には
`InformationalVersion` を採り、整形する方を選ぶ。

## 2. 設計

### 2.1 管理点は `Directory.Build.props` の `<Version>`

リポジトリ直下の `Directory.Build.props` に `<Version>0.2.0</Version>` を置く。
全 9 プロジェクトが継承する。リリース時は `release.yml` が `-p:Version` で上書きするため、
props の値は**開発ビルドの既定値**として働く。

CLAUDE.md §9 は「リリース前に表示バージョン更新 commit を積む」と定めている。
本変更後、その commit が触るのは props の 1 行だけになる。

### 2.2 整形は Core の純ロジック

`kxEdit.Core.Text.VersionText.FromInformationalVersion(string?)` を新設する。
`+` 以降の除去だけを行う 1 関数で、UI にも I/O にも依存しない。
null / 空 / 空白のみ / 先頭 `+` / `+` 複数 / プレリリース識別子付きを
Core のテストで固定する。

### 2.3 App は属性を読んで組み立てる

`kxEdit.App.AppVersion`(internal)が
`AssemblyInformationalVersionAttribute` を読み、`VersionText` に渡して
表示文字列 `kxEdit v0.2.0` を作る。属性が無い・空のときは
バージョンを付けず `kxEdit` だけを返す。

組み立て部分(`Compose`)は文字列を受け取る純関数に切り出し、App のテストで固定する。
`InternalsVisibleTo` が既にあるため追加の可視性変更は不要。

**テストはバージョン番号そのものを assert しない。** `0.2.0` を pin すると
バージョンを上げるたびにテストが落ちる。`kxEdit v` で始まり、続く部分が
空でないことだけを検証する。

### 2.4 アプリ名はハードコードのまま残す

`AssemblyProduct` は既に `kxEdit` なので技術的には読めるが、**採用しない**。

この文字列は `%AppData%\kxEdit\` の**フォルダ名**にも使われている
(`SettingsStore` / `BackupStore` / `SessionLayoutStore` /
`LastSessionBuffersStore` / `PreviewUserDataFolder`)。ユーザーデータの置き場所が
アセンブリ属性に依存すると、プロジェクト名の変更やビルド構成の差で
**データの場所が黙って移動する**。アプリ名は不変条件として固定すべきで、
動的化しない。

### 2.5 リリース時のバージョン整合チェック

§1.1 のとおり、タグと成果物のバージョンはずれない。残る実際のずれは
**「タグを打つ前に props の更新 commit を忘れる」**で、これは CLAUDE.md §9 が
要求している手順そのものである。

`release.yml` の先頭付近(checkout 直後・ビルド前)で、タグと props の `<Version>` を
突き合わせ、不一致なら**その場で落とす**。ビルドを走らせる前に失敗させることで、
30 分のジョブを無駄にしない。

ローカルゲート(`tools/pre-merge-check.ps1`)には対応ステップを置かない。
ローカルにはタグが無く、比較対象が存在しないためである
(CLAUDE.md §6 のステップ名一致は、同種のステップが両方にある場合の規約)。

## 3. 挙動の変化

**本変更は挙動不変ではない。** 意図した変更は 1 点のみ。

- バージョン情報ダイアログの表示が、ソース埋め込みの固定文字列から
  アセンブリ属性由来の値になる。開発ビルドでは props の値、
  リリースビルドではタグの値が出る

それ以外の挙動は変えない。

## 4. スコープ外

- `AssemblyVersion` / `FileVersion` の体系整備(現状 `<Version>` からの既定導出のままとする)
- プレリリース版(`-rc.1` 等)の運用ルール。整形は対応するが運用は定めない
- アプリ名の動的化(§2.4 のとおり意図的に行わない)
