# フェーズ 0: 計測基盤(perf-bench) 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans(または subagent-driven-development)で、タスク単位に実装する。

**Goal:** 以降の全フェーズの変更前後を同じ手順で比べられるように、Smoke の `--perf` ベンチと実アプリ用の `tools/perf-harness.ps1` を用意し、手順を tools/README に載せる。製品コード(`src/`)は変更しない。

**設計書:** `docs/plans/2026-09-24-general-perf-improvements-design.md` §3・§5(以下「設計書」)
**仕様の一次資料:** `docs/plans/2026-09-24-general-perf-audit.md` §9.5(以下「調査記録」)

**Architecture:**
- Smoke `--perf`: 画面内の Form に EditorControl を置き、「操作 → `editor.Update()`」を 1 回として Stopwatch で測る。入力は WndProc(`__TestProcessMessage`)と既存のテストフックから入れる。
- perf-harness: publish 済みの kxEdit.exe を起動し、SendInput で操作してプロセスの CPU 時間の増分を測る。利用者の `%APPDATA%\kxEdit` はコピーで退避し、計測中は空のプロフィールで走らせ、終了時にハッシュで検証して復元する。

**Tech Stack:** C# / WinForms(.NET 9)、PowerShell 7(pwsh)、Win32 SendInput(Add-Type)、System.Windows.Automation。

---

## 0. 前提と決定事項

### 0.1 「変更前の計測」(設計書 §3.1 の 3)

フェーズ 0 は製品コードを変えないので、変更前後の比較はない。代わりに、完成した 2 つの道具で main の現状値を採り、調査記録 §9 と同程度の値が再現することを確かめる(設計書 §5.4)。これを最後のタスク(Task 7)で行い、結果を本書の末尾「実施記録」に書く。以後のフェーズは、この値を「変更前」の参照にできる(同じマシンで採り直すのが原則)。

### 0.2 Smoke 側の入力経路

| シナリオ | 入力 | 理由 |
|---|---|---|
| S1 / S2 / S3 の BackSpace | `WM_KEYDOWN`(`__TestProcessMessage`) | WndProc → `OnKeyDown` → `InputRouter` の実経路を通る。リフレクションの抑止(S3011)が要らない |
| S3 の文字 | `WM_CHAR` | `OnKeyPress` → `InsertConfirmedText` の実経路 |
| S4 | `__TestApplyComposition` / `__TestApplyResult("")` | IME の未確定更新の既存フック |
| S5 の貼り付け片 | `__TestApplyResult(片)` | 確定文字列 → `InsertConfirmedText`。クリップボードを使わずに、貼り付けと同じ 1 回の挿入になる |
| S6 | `TopLine` setter | 設計書どおり |
| S7 | `Invalidate()` + `Update()` | 設計書どおり |
| S8 | `((IUiaTextHost)editor).GetBoundingRectangles` | UI スレッドから直接呼ぶので Invoke を通らない |

- `editor.Focus()` を必須とする(`PositionCaret` は `_hasFocus` で早期 return する。`WrapScrollBench` と同じ理由)。フォーカスが取れなかったときは警告を出す。
- 各シナリオの前に自己チェックを行う(キャレット・本文長・TopLine・矩形数が実際に変わること)。入力が効いていないのに「速い」と報告するのを防ぐ(`WrapScrollBench` と同じ考え方)。

### 0.3 perf-harness 側のプロフィールの扱い(調査記録からの精密化)

調査記録では利用者のプロフィールを復元した状態で測った。本ハーネスは**空のプロフィール**(既定設定)で測る。
- 理由: 利用者の設定(セッション復元・フォント・折り返し)で数値が変わり、再現性が落ちるため。
- 既定設定は「ＭＳ ゴシック 12pt・セッション復元 OFF・バックアップ ON」で、Smoke と揃う。
- シナリオの終了時は、未保存の確認ダイアログを避けるためプロセスを強制終了する。残ったバックアップはプロフィールごと消す(次のシナリオの前に空へ戻す)。

### 0.4 ファイルを開く手段

kxEdit はコマンドライン引数でファイルを開けない(`CommandLineOptions` は `--new-instance` だけ)。ハーネスは Ctrl+O → ファイルを開くダイアログにフルパスを Unicode で打ち → Enter で開く。

### 0.5 ミューテーション検証

対象外(計測道具であり、CLAUDE.md §4-A の列挙に入らない)。

---

## Task 1: Smoke `--perf` の骨格と S7

**Files:**
- Create: `tests/kxEdit.Editor.Smoke/PerfBench.cs`
- Modify: `tests/kxEdit.Editor.Smoke/Program.cs`(`--perf` 分岐を追加)

**内容**
- `PerfBench.Run(string[] args)`。オプション: `--n <int>`(既定 200)、`--warmup <int>`(既定 20)、`--scenario S1,S3,...`(既定は全部)、`--json <path>`。
- 文書の生成(調査記録 §9.5、CRLF):
  - `ja10k`: `{0:D5}: 吾輩は猫である。名前はまだ無い。kxEdit の性能計測 sample 行です。` × i=1..10,000
  - `en10k`: `{0:D5}: The quick brown fox jumps over the lazy dog; perf sample line.` × i=1..10,000
  - 貼り付け片: `{0:D3}: 吾輩は猫である。名前はまだ無い。どこで生れたかとんと見当がつかぬ。` × i=1..100
  - 生成後に UTF-8 バイト数を検査する(990,000 / 710,000 / 10,600)。違えば例外(仕様からのずれを黙って測らない)。
- Form: 900×700、画面内 (100,100)、`ShowInTaskbar=false`。EditorControl は `Dock=Fill`、`ApplyAppearance(new AppSettings())`(既定 = ＭＳ ゴシック 12pt・全角表記)。
- 計測の核: `Measure(string id, string doc, int n, int warmup, Action op)`。各回 `op()` → `editor.Update()` を Stopwatch で囲む。平均・中央値・p95・最小・最大を出す。
- 出力: 表形式のテキスト(`id,doc,n,mean,median,p95,min,max`)。`--json` なら同じ内容を JSON 配列で書く(UTF-8)。
- 判定はしない(常に EXIT 0)。ただし自己チェックの失敗は EXIT 1(測れていない値を出さない)。

**S7**: `editor.Invalidate(); editor.Update();`(ja10k・en10k、TopLine=0)。

**検証**
```
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --perf --scenario S7
```
Expected: ja10k の S7 が数 ms〜十数 ms、en10k がそれより小さい。

**Commit:** `test(perf): Smoke に --perf ベンチの骨格と全面再描画 S7 を追加`

## Task 2: S1〜S6

**Files:** Modify: `tests/kxEdit.Editor.Smoke/PerfBench.cs`

| ID | 事前状態 | 1 回の操作 | 自己チェック |
|---|---|---|---|
| S1 | 行 10 の先頭 + 5 にキャレット、TopLine=0 | 偶数回 →、奇数回 ←(`WM_KEYDOWN` VK_RIGHT/VK_LEFT) | → の後でキャレットが +1 |
| S2 | 同上 | 偶数回 ↓、奇数回 ↑ | ↓ の後で行が +1 |
| S3a | 同上 | `WM_CHAR 'x'` | 本文長 +1 |
| S3b | S3a と交互 | `WM_KEYDOWN VK_BACK` | 本文長 −1 |
| S4 | 同上 | 未確定を「あ」「あい」で交互に更新 | `__TestIsComposing()` が true。終了時に `__TestApplyResult("")` で解除 |
| S5 | 新規の空文書 | 下記 | 挿入後の本文長 |
| S6a | TopLine=100 | TopLine を +1 / −1 で交互 | TopLine が変わる |
| S6b | TopLine=100 | TopLine を +可視行数 / −可視行数 で交互 | 同上 |

- S3 は a と b を交互に行い、それぞれ別に集計する(本文が伸び続けないように)。
- S5(F-6): 空文書に貼り付け片を `__TestApplyResult` で 1 回ずつ入れ、そのつど `x` を 40 回(`WM_CHAR`)打って 1 打鍵を測る。これを 10 回(位置 0〜約 96KB)。出力は位置ごとの行 `S5@<バイト位置>`。
  - 位置は `CurrentBuffer.Current` の UTF-8 バイト長(打鍵した `x` を含む)で表す。
  - ブロック 64KB の境界をまたぐので、のこぎり型が見える(調査記録 M-3)。
- ウォームアップは S1〜S4・S6 で `--warmup` 回。S5 は位置ごとの前に 5 回。

**検証**
```
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --perf
```
Expected: 自己チェックがすべて通り、EXIT 0。S5 で位置 10KB→53KB の増加が見える。

**Commit:** `test(perf): Smoke --perf に S1〜S6(移動・打鍵・IME・F-6・スクロール)を追加`

## Task 3: S8(UIA の矩形)とコード品質の前倒しレビュー

**Files:** Modify: `tests/kxEdit.Editor.Smoke/PerfBench.cs`

- ja10k・TopLine=0 で、範囲 [0, lineStart(k)) を k = 1 / 40 / 1,000 と全文で `GetBoundingRectangles` する。
- 回数は 1 行・40 行が `--n`、1,000 行が 10 回、全文が 3 回(1 回 3.5 秒級のため)。ウォームアップは 1 回。
- `Update()` は呼ばない(描画は起きない)。`Measure` に「Update しない」指定を足す。
- 自己チェック: 矩形数(配列長 / 4)が 1 / 可視行数 / 可視行数 / 可視行数 であること。
- **前倒しのコード品質レビュー**(設計書 §3.4・§5.4): 後続の全フェーズが依存するため、この時点で PerfBench 全体を別エージェントでレビューする。

**Commit:** `test(perf): Smoke --perf に UIA 矩形の S8 を追加`

## Task 4: perf-harness の安全装置(退避・復元)と前倒しの脆弱性レビュー

**Files:** Create: `tools/perf-harness.ps1`(BOM 付き UTF-8)

**退避・復元の仕様**(設計書 §5.2)
- 対象 `$Profile = Join-Path $env:APPDATA 'kxEdit'`。
- 作業ルート `$HarnessRoot = Join-Path $env:LOCALAPPDATA 'kxEdit-perf-harness'`。退避先 `stash\kxEdit`、目印 `STASH-MARKER.json`(日時・元のパス・元が存在したか・全ファイルの相対パスと SHA256)。
- **開始前の中止条件**(いずれも何も変更せずに中止し、理由を表示):
  1. `kxEdit` プロセスが起動している。
  2. `$Profile\backups` の下にファイルが 1 件でもある。
  3. 目印ファイルがある(前回の異常終了)。復元の手順を表示する。
- **退避**: 目印を「copying」で書く → コピー → 退避側のハッシュを元と照合 → 目印を「stashed」に更新。照合が合わなければ何も消さずに中止。
- **計測中**: シナリオの前ごとに `$Profile` の中身を空にする(退避の照合が済んでいることが前提)。
- **復元**(try/finally): ハーネスが起動した kxEdit を停止 → `$Profile` を空にして退避から戻す → 元のマニフェストとハッシュを照合 → 一致したら退避と目印を消す。一致しなければ退避と目印を残し、手順を表示して非 0 で終わる。元が存在しなかった場合は `$Profile` を消す。
- 退避・復元の関数はルートを引数に取り、`-SelfTest` で一時フォルダーの偽プロフィールに対して「退避 → 中身の破壊 → 復元 → 照合」と中止条件 3 つを確かめる。
- ローカルパスを書かない(`$env:APPDATA` / `$env:LOCALAPPDATA` / `$PSScriptRoot` / `$env:TEMP`)。
- **前倒しの脆弱性レビュー**(設計書 §3.4): 利用者データの退避・復元とプロセスの起動・終了を扱うので、Task 5 の前に別エージェントでレビューする。

**検証**
```
pwsh -File tools\perf-harness.ps1 -SelfTest
```
Expected: `[PASS]` が並び EXIT 0。実 `%APPDATA%\kxEdit` に触れない。

**Commit:** `feat(tools): perf-harness に利用者プロフィールの退避・復元と自己テストを追加`

## Task 5: perf-harness の計測(M-1〜M-7)

**Files:** Modify: `tools/perf-harness.ps1`

**引数**: `-PublishDir <dir>`(必須。`kxEdit.exe` を含む)、`-Scenario M-1,M-2,...`(既定は全部)、`-OutCsv <path>`(既定は `$HarnessRoot\results-<日時>.csv`)、`-WorkDir`(既定は `$env:TEMP\kxEdit-perf-harness`。文書を生成する)。

**共通部品**(調査記録 §9.5 のとおり)
- `SendInput`(矢印・Home・End・PageUp・PageDown に `KEYEVENTF_EXTENDEDKEY`。文字は `KEYEVENTF_UNICODE`)。
- 前面化: `SetForegroundWindow` を最大 20 回リトライし戻り値を読む(`AttachThreadInput` 併用)。**ダイアログへ送るシナリオでは前面化を呼ばない。**
- エディタ HWND: メインウィンドウの子孫で、可視・クラス名 `WindowsForms10.Window.8.*`・タイトル空・面積最大。
- `Measure-Op`: 静穏待ち(100 ms ごとに CPU 増分 < 2 ms が 3 回連続)→ 開始値 → n 回の操作(各回の後に interval)→ 静穏待ち → (終了 − 開始) / n。
- 起動: プロフィールを空に → `Start-Process` → メインウィンドウ待ち → `WaitForInputIdle` → 窓を 900×700 に。ファイルは Ctrl+O で開く(§0.4)。
- 終了: `Stop-Process -Force`(§0.3)。

**シナリオ**(n と interval は調査記録 §9.5 の表)
- M-1 起動 ×6(1 回目を除く中央値): 窓の表示まで・入力受付まで・0.8 秒時点の CPU・ワーキングセット。
- M-2 ja10k / en10k: →←・↓↑・Shift+→・x・BackSpace・PageDown/PageUp・`RedrawWindow` 基準・文書先頭の ←・Shift 単押し(設計書 §5.2 の必須の組)。
- M-3 新規タブに貼り付け片を Ctrl+V ×9、その都度 `x` ×40。クリップボードのテキストは開始前に保存して終了時に戻す(ベストエフォート)。
- M-4 ja10k: ホイール ±120 ×60。
- M-5 空 / ja10k / ja30k: Ctrl+F → 16 回周期(7 文字・BackSpace 7・空 2)×4。値は ×16/14 で補正。
- M-6 空タブ 4 枚 / 3 ファイル + 空 / 3 ファイル未保存 + 空: Ctrl+Tab ×40。スレッド別の CPU 上位 2 本も出す。
- M-7 ja10k: UIA で 1 行・40 行・1,000 行・全文の `GetBoundingRectangles` の所要時間。

**出力 CSV**: `scenario,condition,doc,n,value,unit`(UTF-8)。

**Commit:** `feat(tools): perf-harness に M-1〜M-7 の計測シナリオを追加`

## Task 6: tools/README

**Files:** Modify: `tools/README.md`

- 一覧表に `perf-harness.ps1` を追加。§3「性能計測」を新設し、次を書く。
  - Smoke `--perf` の使い方(シナリオ表・Release・3 回の中央値・他の重い処理を止める)。
  - perf-harness の使い方(publish の作り方・安全装置・中止条件と復元の手順・クリップボードへの影響・画面を触らないこと)。
  - dotnet-trace: `dotnet tool install dotnet-trace --tool-path <作業フォルダ>`、`dotnet-sampled-thread-time`・Speedscope 出力・「UI スレッドで管理コードに帰属する区間」を見る集計の観点(調査記録 §9.5 の集計規則)。
  - WPR: `wpr -start CPU -start GeneralProfile` → 操作 → `wpr -stop <file>.etl`、WPA で開く。管理者権限が要る。
  - 判断基準(設計書 §3.2): 改善が揺れ(3 回の最小〜最大)を超えること。

**Commit:** `docs(tools): 性能計測の手順(Smoke --perf・perf-harness・dotnet-trace・WPR)を追加`

## Task 7: 現状値の採取と再現性の確認

- Smoke `--perf` を Release で 3 回走らせ、中央値を本書の「実施記録」に書く。
- `dotnet publish src/kxEdit.App -c Release -r win-x64 --self-contained false -o <作業フォルダ>` で publish し、perf-harness を全シナリオで 1 回走らせる。
- 調査記録 §9 と比べ、同程度(桁と大小関係が一致)であることを確かめる。ずれた場合は原因を記録する。

**Commit:** `docs(perf): フェーズ 0 の実施記録(現状値)を追記`

## Task 8: 最終レビューと品質ゲート

- 最終ブランチレビュー: コード品質パスと脆弱性パスを別エージェントで(CLAUDE.md §3 の 5)。指摘は fixup commit。
- `tools/pre-merge-check.ps1` を EXIT 0 で通す(Smoke は Release ビルドの対象なので 0 warning が必要)。
- L5 は不要(設計書 §4)。
- PR を作る(日本語。現状値・レビュー経緯・申し送り)。マージ後に設計書 §5 の末尾へ実施記録を追記する。

---

## 実施記録

### 計画からの精密化・逸脱(レビュー由来を含む)

- **Smoke の窓の位置**: 計画は画面内 (100,100) としたが、1024×767 の画面では 900×700 の窓の下端がはみ出す。作業領域の左上に置き、作業領域に収まることを自己チェックする。
- **Smoke の自己チェックの追加**(前倒しのコード品質レビュー I-1〜I-4)
  - 1 操作あたりの WM_PAINT 回数を数えて `paints_per_op` 列に出す。S7 は毎回ちょうど 1 回であることを検査する。実際に、画面から外れた状態で S7 が 0.001 ms を出して EXIT 0 になる事象が、レビューの反映前に 1 回起きた。
  - フォーカスが無ければ中止する。
  - S8 の矩形数の期待値は「範囲の行数。ただし可視域で頭打ち」。可視域の行数は DPI で変わるので、実測値を使う。範囲は `[0, lineStart(k) − 2)` とし、行末の CRLF を含めない(ちょうど k 行)。
  - 未知のシナリオと引数の誤りは EXIT 2 にする。
- **Smoke の出力**: JSON を `{ meta, results }`(snake_case)にした。S5 の Id は `S5-r<回>` とし、書込位置は `param` 列に分けた。全体のウォームアップ(約 1.5 秒)と、シナリオごとの GC を入れた。
- **harness の保全**(前倒しの脆弱性レビュー C-1・I-1〜I-6)
  - プロフィールの場所は、kxEdit と同じく既知フォルダーで解決する。
  - 目印は CreateNew で作り、ハーネスの二重起動は mutex で弾く。
  - 復元は、一時フォルダーへコピーして照合してから名前の付け替えで行う。
  - `-Recover` は、その時点のプロフィールを `displaced-<日時>` へ退かせて残す。
  - 入力の直前に、前面の窓が計測対象のプロセスであることを確かめる。
- **harness の `-Scenario`**: `pwsh -File` ではカンマ区切りが 1 つの文字列で届くので、スクリプト内で分割する。
- **harness の M-4**: 本文の中央が最前面の窓(NVDA のスピーチビューアー)に覆われていると、ホイールが届かずに 0.26 ms を出した。覆われていない点を探してカーソルを置き、縦スクロールバーの位置が動くことを自己チェックする。

### 現状値(2026-09-24・main `3514fd3` と同一の src)

**環境**: 調査記録 §9.1 と同じ VM(4 vCPU・1024×767・96 DPI)。**ただし NVDA が起動していた**(調査記録は NVDA なし)。NVDA は UIA クライアントなので、UIA イベントとフォーカス変更の費用が乗りうる。以後のフェーズで比べるときは、NVDA の有無を揃えること(harness は CSV の `env` 行に記録する)。

**Smoke `--perf`**(Release・3 回の中央値〔最小〜最大〕・ms/操作・paints_per_op はすべて 1、S8 は 0)

| ID | ja10k | en10k |
|---|---|---|
| S1 →← | 9.23〔9.10〜9.87〕 | 7.55〔7.47〜7.61〕 |
| S2 ↓↑ | 9.32〔9.19〜9.32〕 | 7.54〔7.46〜7.58〕 |
| S3a 挿入 | 11.17〔10.92〜11.17〕 | 7.84〔7.82〜8.09〕 |
| S3b BackSpace | 11.15〔10.90〜11.23〕 | 8.00〔7.82〜8.18〕 |
| S4 IME 未確定 | 9.60〔9.42〜9.70〕 | 7.77〔7.60〜7.85〕 |
| S6a 1 行スクロール | 9.33〔9.26〜9.36〕 | 7.70〔7.57〜7.73〕 |
| S6b 1 ページスクロール | 9.41〔9.14〜9.81〕 | 7.72〔7.70〜7.77〕 |
| S7 全面再描画 | 9.05〔8.83〜9.21〕 | 7.19〔7.08〜7.24〕 |

| S8(ja10k・UI スレッドから直接) | ms |
|---|---|
| 1 行 | 0.03 |
| 40 行 | 6.10〔5.91〜6.35〕 |
| 1,000 行 | 395〔379〜419〕 |
| 全文 | 4,022〔3,893〜4,331〕 |

| S5 書込位置(バイト) | 5 | 10,650 | 21,295 | 31,940 | 42,585 | 53,230 | 63,875 | 74,520 | 85,165 | 95,810 |
|---|---|---|---|---|---|---|---|---|---|---|
| ms/打鍵 | 3.64 | 12.89 | 15.26 | 18.07 | 20.68 | 23.67 | **26.03** | **11.77** | 14.90 | 17.26 |

**perf-harness**(publish・各 1 回・CPU ms/操作。括弧内は調査記録 §9.2 の値)

| シナリオ | 条件 | ja10k | en10k |
|---|---|---|---|
| M-2 | →← の交互 | 19.4(17.5) | 16.1(13.6) |
| | ↓↑ の交互 | 17.3(15.0) | 13.8(15.8) |
| | Shift+→ | 19.1(17.2) | 15.2 |
| | 文字入力 x | 18.2(22.4) | 16.9(14.6) |
| | BackSpace | 17.2(16.7) | 14.3(14.8) |
| | PageDown/PageUp の交互 | 24.1(21.6/21.9) | 24.7(20.6) |
| | 基準: 全面の再描画 | 8.8(8.9) | 7.0(6.3) |
| | 基準: 文書先頭での ← | 5.5(6.8) | 5.0(8.9) |
| | 基準: Shift の単押し | 2.3(1.6) | 2.0(3.3) |
| M-4 | ホイール下 / 上 | 17.5 / 16.4(16.7 / 12.8) | — |

| シナリオ | 値 |
|---|---|
| M-1 起動 | 窓の表示 182 ms(192)・入力受付 202 ms(213)・0.8 秒時点の CPU 297 ms(344)・WS 59MB(63) |
| M-3 F-6(位置 0→約 64KB→約 74KB) | 24.2 → 27.3 → **14.8**(のこぎり型を再現。位置 0 は画面がほぼ空なので比較対象外) |
| M-5 検索語の打鍵 | 空 10.3(7.5)・ja10k 14.8(11.2)・ja30k 20.4(13.1) |
| M-6 Ctrl+Tab | 空 4 枚 94.9(96.9)・3 ファイル + 空 109.8(99.2)・未保存 123.8(109.0)。UI スレッド 58.6 / 別スレッド 30.9(55.5 / 35.2) |
| M-7 UIA 矩形(COM 越し) | 1 行 0.3 ms(13.3)・40 行 28 ms(13.2)・1,000 行 276 ms(360)・全文 2,742 ms(3,534) |

**再現性の判定**: 主要な値(全面再描画・キャレット移動・F-6 ののこぎり型・M-7 の行数比例・タブ切替の固定費・起動)は、桁も大小関係も調査記録 §9 と一致した(設計書 §5.4 の完了条件)。

**ずれの記録**
- M-5 は調査記録より 3〜7 ms 高い。NVDA の起動(検索ダイアログの入力欄に対する UIA の問い合わせ)が疑われる。フェーズ 5 で比べるときは NVDA の有無を揃えること。
- M-7 の 1 行が 0.3 ms(調査記録は 13.3 ms)。調査記録の値は、初回の UIA 接続の費用を含んでいた可能性がある。本ハーネスは同じ範囲を 10 回測った中央値を取る。
- M-7 の可視矩形は 36(Smoke は 42)。harness の窓はタブ・メニュー・ステータスバーのぶん本文が低いため。
