# SR 個別対応レガシー除去(案 A: PC-Talker 記述の一掃)設計書

策定日: 2026-07-25 / 対象ブランチ: `feature/sr-legacy-cleanup`

## 1. 背景・目的

yEdit は当初 PC-Talker にも対応する方針で、App レイヤーに SR ごとの差異を吸収する機構
(`AnnouncerFactory` の SR 別分岐・`PcTalkerAnnouncer` / `PcTalkerSpeech`・SR ルート選択・
`PreferredScreenReader` 設定)を持っていた。2026-07-13 に PC-Talker サポート廃止を決定し
(`docs/plans/2026-07-13-pctalker-removal-design.md`・マージ `75d2817`)、機構本体は削除済み。

その後「**個別のスクリーンリーダーへの対応はしない**」を確定方針とした。本作業はこの方針を
リポジトリの記述面に反映し、当時の経緯に由来する陳腐化した記述を一掃する。

**本作業は挙動を一切変更しない。** コード上の変更はコメントとドキュメントの文言のみ。

## 2. 現状調査結果(2026-07-25 実測)

### 2.1 既に削除済み(残骸なし)

`src` 全体の grep により、**SR ベンダを実行時に判定・分岐するコードはゼロ**であることを確認した。

| 旧機構 | 現状 |
|--------|------|
| `AnnouncerFactory` の SR 別分岐 | なし |
| `PcTalkerAnnouncer` / `PcTalkerSpeech` | なし |
| `ISrRoute` / `SrRouteSelector` | なし |
| SR 検出(プロセス名 / `SPI_GETSCREENREADER` / `FindWindow`) | なし |
| `PreferredScreenReader` 設定 | なし(墓標コメントのみ `AppSettings.cs:4`) |
| MSAA 抑制の SR 別扱い | なし(「自前 MSAA プロキシを作らない」汎用ポリシーのみ) |

すなわち **`README.md:151` の「SR ルート選択、Announcer 層の分岐 等が今も残っている」という記述は
事実に反する**。この誤記が「レガシーが残っている」という認識の主要な出どころだった。

### 2.2 `IAnnouncer` の評価(= 案 B を採らない根拠)

出自は SR ベンダ多態のシームだが、現在は **テストシームとして稼働中**であり、単一実装の
インターフェースであること以外にレガシー性はない。

- 本番実装は `UiaAnnouncer` のみ。`MainForm.cs:40` は既に具象型で保持(CA1859 対応済み)
- 注入先: `CsvController` / `SearchController` / `KinsokuFormatController` / `GrepDialog`
- `FakeAnnouncer`(14 行)経由で通知文言を検証しているテストが **138 件**
  (Csv 44 / Search 36 / Grep 33 / Announcer 15 / Kinsoku 10)

インターフェース削除で得るのは 11 行の削減、失うのは上記 138 件の検証手段
(実 `UiaAnnouncer` + ハンドル生成済み `Label` への依存、または派生クラスの新設が必要)。
さらに Speech 経路の変更となり CLAUDE.md §5 により L5 実機検証が必須になる。
**費用対効果が見合わないため案 B は採らない**(理由付き却下)。

`UiaAnnouncer` 本体(`RaiseAutomationNotification`)は UIA 標準であり、SR ベンダ固有ではない。
削除すると UIA 対応 SR すべてで能動通知が失われ、CLAUDE.md §2 の a11y 鉄則に反する。

### 2.3 PC-Talker 由来の「根拠」で書かれた挙動判断 3 箇所

コメントだけでなく実挙動が PC-Talker 由来ではないかを個別に検証した結果:

| 箇所 | 挙動 | 判定 |
|------|------|------|
| `TextRangeProviderV2.cs:208-211` Move スパン保持 | 非退化レンジの `Move` 後に unit へ再展開 | **UIA 仕様どおり**。根拠コメントのみ陳腐化。`TextRangeProviderV2Tests.cs:156` で固定済み |
| `EditorControl.cs:1381-1387` フォーカス時 UIA イベント明示発火 | `RaiseFocusChanged` + `RaiseSelectionChanged` | **UIA 標準の good practice**。UIA 対応 SR のフォーカス追跡に必要(`cd8b526` で実証済み)。根拠コメントのみ陳腐化 |
| `TextRangeProviderV2.cs:285-288` `ScrollIntoView` | **no-op** | 根拠が「PC-Talker はテキスト歩きで読める」= **ベンダ固有の理由で UIA 標準メソッドを未実装にしている**。挙動面の宿題 → §7 申し送り(案 C) |

前 2 件は挙動を維持しコメントの根拠だけを書き換える。3 件目は挙動を本作業で変更せず、
コメントを「未実装(申し送り)」へ正直化するにとどめる。

## 3. 方針(判断済み事項)

ユーザー判断(2026-07-25):

1. **`docs/report-pctalker-speech/` は歴史記録として残す**(削除しない)。
2. **PC-Talker の記述を落とし、「UIA 対応 SR」とする。**
3. **NVDA / ナレーター等の固有名は残す。** どのスクリーンリーダーで使えるかが分かりにくくなるため。
   「一部 UIA 対応 SR」等への一般化がより適切な箇所もありうるが、**今回は触れない**。

### 3.1 編集ルール(本作業の唯一の規則)

> **`PC-Talker` の文字列を落とす。それ以外の SR 固有名(NVDA / ナレーター)は現状維持。
> 一般化・言い換えは行わない。**

例: `PC-Talker/NVDA が各項目をネイティブに読む` → `NVDA が各項目をネイティブに読む`

PC-Talker が主語で単純削除では文が成立しない箇所(§4.1 の ★)のみ、代替根拠を書き起こす。

## 4. スコープ

### 4.1 IN(15 ファイル)

コメント・ドキュメントのみ。ビルド成果物への影響なし。

**src(7 ファイル / 9 箇所)**

| ファイル:行 | 変更 |
|---|---|
| `src/yEdit.Core/Editing/NavigationCommands.cs:102` | `NVDA/PC-Talker/ナレーターが` → `NVDA/ナレーターが` |
| `src/yEdit.Accessibility/TextRangeProviderV2.cs:9` | `(PC-Talker の文字歩きが動く条件)` → `(文字歩きが動く条件)` |
| `src/yEdit.Accessibility/TextRangeProviderV2.cs:208` | `PC-Talker の文字歩き=Expand(Char)→…` → `文字歩き=Expand(Char)→…` |
| ★ `src/yEdit.Accessibility/TextRangeProviderV2.cs:287` | `/* PC-Talker はテキスト歩きで読めるため省略(v1 挙動踏襲) */` → 未実装であることと申し送り先(§7 案 C)を明記 |
| ★ `src/yEdit.Editor/EditorControl.cs:1382` | `PC-Talker は 2 秒ポーリングで選択を追う既知挙動(HANDOFF §13.6)があるため、` → UIA 標準の good practice を根拠にした記述へ(§2.3) |
| `src/yEdit.App/GrepResultsWindow.cs:8` | `PC-Talker/NVDA が各項目を` → `NVDA が各項目を` |
| `src/yEdit.App/RestoreDialog.cs:9` | `PC-Talker/NVDA が` → `NVDA が` |
| `src/yEdit.App/Speech/UiaAnnouncer.cs:21` | `NVDA/PC-Talker の実機観測から` → `NVDA の実機観測から` |

**tests(3 ファイル・コメントのみ・テストコード不変)**

| ファイル:行 | 変更 |
|---|---|
| `tests/yEdit.App.Tests/FileControllerTests.cs:622` | `PC-Talker 廃止後も温存の UIA 配線` → `個別 SR 対応廃止後も温存の UIA 配線` |
| `tests/yEdit.Editor.Tests/RaiseUiaSelectionEventsTests.cs:6` | `PC-Talker サポート廃止=` → `個別 SR 対応廃止=`(典拠ファイルパスは §4.2 により保持) |
| `tests/yEdit.Core.Tests/Accessibility/TextRangeProviderV2Tests.cs:156` | `PC-Talker の文字歩き挙動:` → `文字歩き挙動:` |

**tools(3 ファイル / 4 箇所)**

| ファイル:行 | 変更 |
|---|---|
| ★ `tools/word-sim.ps1:3` | `# Background: PC-Talker never calls TextUnit.Word (proven by P0 trace); NVDA relies on it.` → `# Background: NVDA relies on TextUnit.Word for word navigation.` |
| `tools/sr-regression.ps1:25` | `(PC-Talker の空行問題のような発声側事象)` → `(発声側で起きる事象)` |
| `tools/README.md:57` | 末尾 1 文 `PC-Talker は TextUnit.Word を呼ばない(P0 trace で確認済)ため、これは主に NVDA 用の回帰。` → `これは NVDA 用の回帰。` |
| `tools/README.md:63` | `(PC-Talker の空行問題のような発声側事象)` → `(発声側で起きる事象)` |

`tools/word-sim.ps1` は BOMless UTF-8 のまま維持する(`tools/README.md:59` の既知問題・
`sr-regression.ps1` の pwsh 優先ロジックが前提にしている)。
`tools/sr-regression.ps1` は BOM 付き UTF-8(ユーザーが直接起動する ps1)を維持する。

**README.md(「現在」を説明する文書・CLAUDE.md §8 の同期対象)**

★ `README.md:149-151` §補足 を全面書き換えする。現行文は §2.1 のとおり**事実に反する**
(削除済みの機構を「今も残っている」と説明している)ため、修正は必須。

提案文:

```markdown
## 補足

当初は PC-Talker への対応も予定しており、App レイヤーでスクリーンリーダーごとの差異を
吸収する機構(SR ルート選択・Announcer 層の SR 別分岐)を持っていた。方針変更により
PC-Talker 対応は見送り、2026-07-13 にこの機構は削除済み。現在は UIA 単一経路のみで、
実行時に SR を判定・分岐するコードは存在しない。

`IAnnouncer` は当時の多態シームに由来する単一実装のインターフェースだが、現在は
通知文言を検証するテストシームとして機能しているため意図的に残している。
```

この節は開発者向けの経緯説明であり、履歴を辿るうえで `docs/plans/` や git 履歴と
突き合わせる必要があるため、**PC-Talker の固有名は残す**(判断 2 は利用者向け記述が対象)。
利用者向けの記述からの除去は下記 説明書 で行う。

**説明書(CLAUDE.md §8: ユーザー編集版が正 → 改稿し校閲を依頼する)**

| ファイル:行 | 変更 |
|---|---|
| `説明書/yEdit説明書.md:34` | 末尾 `PC-Talker はサポート対象外です。` を削除。`NVDA で動作確認しています。UIA(UI オートメーション)に対応したスクリーンリーダーであれば読み上げできます。` を残す |
| `説明書/yEdit説明書.md:131` | 行 `PC-Talker はサポート対象外です。` と直前の空行を削除(§6.1 は 129 行の 1 段落構成になる) |
| `説明書/yEdit説明書.md:304` | 箇条書き `- **PC-Talker**: …` を項目ごと削除(§12.1 制限事項から 1 項目減) |

説明書の他 3 箇所(26 / 129 / 142 行)は NVDA / ナレーターのみで PC-Talker を含まないため
**変更しない**(判断 3)。

### 4.2 OUT(理由付き)

| 対象 | 除外理由 |
|---|---|
| NVDA / ナレーターの固有名(全箇所) | **判断 3**。`README.md:7,102` / `CLAUDE.md:86` / `tools/README.md:12` / `tools/sr-regression.ps1:12` / `src/yEdit.Editor/EditorControl.cs:16` / `src/yEdit.Editor/InputRouter.cs:181` / `tests/README.md:18` / `tests/yEdit.Editor.Smoke/Program.cs:32` / `説明書:26,129,142` / `.github/ISSUE_TEMPLATE/bug_report.yml:85` は現状維持 |
| `src/yEdit.Core/Settings/AppSettings.cs:4` | `PC-Talker` 文字列を含まない(`P7 撤去: PreferredScreenReader…` の墓標コメント)。編集ルール §3.1 の対象外 → §7 申し送り |
| `docs/plans/**` | CLAUDE.md §8「日付付き文書は策定時スナップショット・後日書き換えない」。PC-Talker 参照は当時の記録として正当 |
| `docs/plans/2026-07-13-pctalker-removal-design.md` 等への**典拠ポインタ** | 上記によりファイル名は不変。ゆえに典拠として引用する箇所には `pctalker` 文字列が残る(不可避・意図的) |
| `docs/report-pctalker-speech/` | **判断 1**: 歴史記録として残す |
| `IAnnouncer` / `UiaAnnouncer` の構造 | §2.2 のとおり案 B は却下 |
| `yEdit.Accessibility` の UIA プロバイダ一式 | UIA 標準。ベンダ固有ではない |
| `EditorControl` の MSAA 非提供ポリシー | 汎用ポリシー + 情報漏洩対策(`NativeMethods.cs:13`) |
| `ScrollIntoView` の実装 | 挙動追加であり本作業(挙動不変)の範囲外 → §7 案 C |
| `src/yEdit.UiaProbe/` `src/yEdit.ScintillaProbe/` | git 未追跡のビルド残骸(bin/obj のみ・ソースなし・sln 未登録)。リポジトリ変更ではないためユーザーの手動削除に委ねる |

### 4.3 完了条件

`docs/plans/**` と `docs/report-pctalker-speech/**` を除外した grep で
`PC-Talker` / `PcTalker` / `PCTalker` / `pctk` の残存が、§4.2 で意図的に残すと決めた
典拠ファイルパス(`docs/plans/2026-07-13-pctalker-removal-design.md` への参照)と
`README.md` §補足 のみになること。

```
rg -i 'pc-?talker|pctk' --glob '!docs/plans/**' --glob '!docs/report-pctalker-speech/**'
```

## 5. 実装単位

CLAUDE.md §3「簡略化の基準」を適用する。挙動変更ゼロ・コメントとドキュメントのみのため
**実装を 1 タスクに統合し単一 commit** とし、最終ブランチレビューも **1 回に統合**する。
ただし別エージェントレビューと品質ゲートは省略しない。

説明書の改稿は §8 によりユーザー校閲が前提のため、同一 commit に含めた上で
PR description に「校閲依頼」として明記する。

## 6. 検証

| 項目 | 内容 |
|---|---|
| ビルド | `tools/pre-merge-check.ps1` で EXIT 0(0 warning 維持) |
| テスト | L1/L2/L3 全緑。テストコードは不変のためテスト数は増減しない |
| 完了条件 | §4.3 の grep |
| L5 実機 SR 検証 | **不要**。SR 経路(`yEdit.Accessibility` / `EditorControl` の UIA 部 / App の Speech 系)の**挙動は不変**でコメントのみの変更。CLAUDE.md §5「SR 経路不変の挙動不変リファクタは省略可」に該当 |
| `tools/sr-regression.ps1` | UIA 応答は不変のため回帰目的では不要。ただし `sr-regression.ps1` / `word-sim.ps1` 自身のコメントを編集するため、**構文が壊れていないことの確認として 1 回実行**する(`word-sim.ps1` は BOMless UTF-8 のため `pwsh` を用いる) |
| ミューテーション検証 | 対象なし(挙動変更ゼロ・テスト不変) |

## 7. 申し送り(follow-up)

### 案 C: `ScrollIntoView` の未実装解消(本ブランチ対象外)

`src/yEdit.Accessibility/TextRangeProviderV2.cs:285-288` の `ITextRangeProvider.ScrollIntoView`
は現在 no-op であり、その根拠は「PC-Talker はテキスト歩きで読めるため省略(v1 挙動踏襲)」という
**ベンダ固有の理由**である。UIA 対応 SR はレビューカーソル移動・検索結果へのジャンプ等で
このメソッドを呼ぶため、レガシーな根拠が潜在的な機能欠落を隠している可能性がある。

- 性質: **除去ではなく挙動追加**。CLAUDE.md §3 のフル工程(設計書 → タスク分割 → レビュー)が必要
- L5: **必須**(SR 経路の挙動変更)
- 前提調査: `EditorControl` が常にキャレットを可視域に保つ設計であれば実害が小さい可能性がある。
  実害の有無(NVDA のレビューカーソルが画面外テキストを読むときにスクロールしないか)を
  実機で確認してから実装可否を判断する
- 対応: 別 Issue として登録し、本ブランチでは §4.1 のとおりコメントを「未実装(申し送り)」へ
  正直化するだけに留める

### 軽微: `AppSettings.cs:4` の墓標コメント

`// P7 撤去: PreferredScreenReader フィールドは削除（SR 二系統機構の実質死・優先 SR タブも削除済み）。`
は削除済み機構の墓標で、現状の説明としては不要。`PC-Talker` 文字列を含まないため §3.1 の
編集ルールの対象外として今回は残す。次に `AppSettings.cs` を触る機会に削除を検討する。

## 8. 実施記録(2026-07-25 追記)

### 8.1 調査漏れ 1 件と、その判断

§4.3 の完了条件を `-i`(case-insensitive)付きで再実行したところ、策定時の調査
(case-sensitive)では拾えなかった 1 箇所が判明した。

`tests/yEdit.Core.Tests/Settings/SettingsStoreTests.cs:240`

```csharp
File.WriteAllText(path, "{\"PreferredScreenReader\":\"pctalker\",\"TabWidth\":8}");
```

策定時のパターン `PC-Talker|PcTalker|PCTalker|pctk|PCTK` は小文字連結の `pctalker` に
マッチしない(`pctk` は部分列ではない)。**以後この種の棚卸しでは `rg -i` を既定とする。**

**判断: このファイルは変更しない(§4.2 の OUT へ追加)。**

当該テスト `Load_ignores_unknown_removed_keys` は、P7 で削除した `PreferredScreenReader`
キーが**実ユーザーのディスク上の `settings.json` に残っていても起動失敗しない**ことを固定する
前方互換テストである。`"pctalker"` は陳腐化した記述ではなく、**その実データを表す fixture**
であり、書き換えるとテストが守っている実シナリオとの対応が崩れる。
`:235` の説明コメントも、テストの存在理由を述べるうえで削除済みフィールド名の明示が必要。

### 8.2 `AppSettings.cs:4` の評価を訂正

§7「軽微」で「削除を検討する」とした
`// P7 撤去: PreferredScreenReader フィールドは削除（…）。`
は、§8.1 の前方互換テストと**対になる互換仕様の説明**であり、陳腐化した墓標ではない。
「なぜフィールドが無いのか」「なぜ未知キーを無視してよいのか」を実装側に残す記述として
機能している。**今後も削除しない**(§7 の当該記述を撤回する)。

### 8.3 完了条件の実測結果

`rg -i 'pc-?talker|pctk' --glob '!docs/plans/**'` の残存は次の 4 種のみ。すべて意図的。

| 残存箇所 | 根拠 |
|---|---|
| `README.md:151` | §4.1: 開発者向け経緯説明として固有名を残す |
| `docs/report-pctalker-speech/**` | 判断 1: 歴史記録として残す |
| `tests/yEdit.Editor.Tests/RaiseUiaSelectionEventsTests.cs:7` | §4.2: 典拠スナップショットのファイルパス(§8 により不変) |
| `tests/yEdit.Core.Tests/Settings/SettingsStoreTests.cs:235,240` | §8.1: 前方互換テストの fixture と説明 |

### 8.4 §6 からの逸脱: `sr-regression.ps1` は実行せず構文検証に置換

§6 は「構文が壊れていないことの確認として 1 回実行する(`word-sim.ps1` は BOMless UTF-8 のため
`pwsh` を用いる)」としていたが、**本環境に `pwsh` が未インストール**だった
(`Get-Command pwsh` → 未検出)。WinPS 5.1 で実行すると `word-sim.ps1` が
`tools/README.md:59` の既知問題(BOMless UTF-8 の日本語コメントを Shift-JIS 誤解釈)で
落ちるため、**本変更とは無関係な pre-existing FAIL** がノイズとして出る。

§6 の目的は構文検証(UIA 応答は不変のため回帰目的ではない)であるため、
ファイルのエンコーディング判定を迂回して構文のみを検証する方法に置き換えた。

```powershell
$src = [System.IO.File]::ReadAllText($full, [System.Text.UTF8Encoding]::new($false))
[System.Management.Automation.Language.Parser]::ParseInput($src, [ref]$null, [ref]$errors)
```

結果: `word-sim.ps1` / `sr-regression.ps1` ともに **parse errors = 0**。
加えて `word-sim.ps1` の編集行(3 行目)が**非 ASCII 文字を 1 つも含まない**ことを確認し、
当該編集が上記既知問題を新たに誘発しないことを担保した。

**申し送り**: `tools/sr-regression.ps1` の機能実行(UIA クライアントとしての疎通確認)は
未実施。実行にはフォアグラウンドのデスクトップセッションと、`word-sim.ps1` のために
`pwsh` のインストールが必要。本ブランチは UIA 応答を変更しないため回帰目的では不要だが、
次に a11y 関連の挙動変更を行うときまでに `pwsh` を導入しておくのが望ましい。

### 8.5 ps1 のエンコーディング維持を確認

| ファイル | 先頭 3 バイト | 判定 |
|---|---|---|
| `tools/word-sim.ps1` | `23 20 53`(`# S`) | BOMless UTF-8 維持(§4.1 のとおり) |
| `tools/sr-regression.ps1` | `ef bb bf` | BOM 付き UTF-8 維持(ユーザーが直接起動する ps1) |

## 9. 挙動不変の担保(CLAUDE.md §2)

本作業の変更は以下に限られ、コンパイル結果に影響しない。

- C# コメント(`//` / `///`)の文言変更
- PowerShell コメント(`#` / `<# … #>`)の文言変更
- Markdown 本文の変更

実行可能なコード行(ステートメント・宣言・属性)は一切変更しない。
意図的な挙動変更は**なし**。
