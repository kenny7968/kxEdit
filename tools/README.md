# tools/ スクリプト一覧

`tools/` 配下は「ローカルで手動起動する運用スクリプト」の置き場。CI が自動で回すものは `.github/workflows/` にある。ここは「いつ・何のために叩くか」に絞って各スクリプトを説明する。

## 一覧

| スクリプト | 役割 | 実行タイミング |
|---|---|---|
| `pre-merge-check.ps1` | main マージ前のローカルゲート(CSharpier check + Release 0 警告 + 全テスト緑 + 3 テストプロジェクトの Debug 実行) | **main マージ前 必須** |
| `sr-regression.ps1` | SR 呼び出しパターン回帰スイート。`verify-uia-editor.ps1` + `word-sim.ps1` を一括実行するアグリゲータ | a11y 系変更のマージ前 + リリース前 |
| `verify-uia-editor.ps1` | `kxEdit.Editor.Smoke --uia` を起動し、UIA クライアントとして TextPattern / GetSelection / RangeFromPoint の疎通を PASS/FAIL 判定 | 通常は `sr-regression.ps1` 経由で呼ばれる |
| `word-sim.ps1` | 同じく `--uia` 起動先に対し NVDA の TextUnit.Word 呼び出しパターン(Expand/Move span/MoveEndpointByUnit)6 ケースを再現 | 通常は `sr-regression.ps1` 経由で呼ばれる |
| `perf-harness.ps1` | publish 済みの kxEdit を SendInput で操作し、1 操作あたりのプロセス CPU 時間を測る(体感値) | 性能改善の変更前後(§3) |

## §1 `pre-merge-check.ps1`

main マージ前の**必須**ゲート。中身:

1. `dotnet tool restore`(CSharpier 等の local tool)
2. `dotnet csharpier check`(フォーマット検証)
3. Release ビルド 0 警告(`-warnaserror`)
4. Core / Editor / App の 3 テストプロジェクトを Release で実行(フィルタなし・LocalOnly を含む全数)
5. 同じ 3 テストプロジェクトを **Debug でもう一度**実行

5 が要る理由(Issue #48 最終レビュー Q-I-4): `Debug.Assert` は `[Conditional("DEBUG")]` なので
Release バイナリに残らない。`DocumentState.Path` の不変条件のように `Debug.Assert` で守っている網は、
Release だけのゲートでは 1 行も走らない=「網に見えるがゲート上は無効」になる。

2026-09-01 (B1) に **3 本すべて**へ揃えた。Core を外していた理由(既知 S-5 で 4 件赤)は
`WordBoundary` の `Debug.Assert` 削除で解消。`kxEdit.Editor` 自身に `Debug.Assert` は現状 0 件だが、
プロジェクト単位で歯抜けにすると「assert を足したのにゲートステップを足し忘れる」= Q-I-4 が
踏んだ失敗モードが再発するため揃えている。

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

## §3 性能計測

性能改善(`docs/plans/2026-09-24-general-perf-improvements-design.md` の各フェーズ)で、変更前後を同じ手順で比べるための道具。判断基準は設計書 §3.2: **改善対象の数値が、改善前の揺れ(3 回の最小〜最大)を超えて改善していること**。揺れの範囲に収まる場合は PR に書いて採否をユーザーに判断してもらう。

| 道具 | 測るもの | 向き |
|---|---|---|
| Smoke `--perf` | 「操作 → 同期描画」1 回の実時間(プロセス内) | 再現性の数値。変更前後の比較の主役 |
| `perf-harness.ps1` | 実アプリへ SendInput したときのプロセス CPU 時間 | 体感値。IME/TSF など kxEdit の外の費用も乗る |
| dotnet-trace / WPR | 内訳 | 原因の特定 |

### Smoke `--perf`

```powershell
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --perf
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --perf --scenario S1,S7 --n 200 --json perf.json
```

- **画面内**に 900×700 の窓が出る(画面外の窓には WM_PAINT が配送されず、描画を測り落とすため)。計測中は触らない。
- 外観は製品の既定(ＭＳ ゴシック 12pt・行番号なし・折り返しなし。S9 だけ折り返し 40 桁)。文書はメモリ上で生成する(ja10k / en10k。書式は調査記録 §9.5)。
- 変更前と変更後を**同じマシンで各 3 回**走らせ、中央値を比べる。Release ビルドで、他の重い処理を止めて走らせる。
- 判定はしない(EXIT 0)。**EXIT 1 は自己チェックの失敗**=入力が効いていない・描画が届いていない・フォーカスが無い(測れていない)ことを意味するので、値を使わない。EXIT 2 は引数の誤り。
- 出力の `paints_per_op` は 1 操作あたりの WM_PAINT 回数。再描画の省略(フェーズ 3)や部分再描画(フェーズ 9)の効果の証拠になる。
- `--json` は `{ meta, results }`(snake_case)。meta に DPI・窓の大きさ・Release かどうか・ランタイムを記録する。
- **変更前後は同じ `--scenario` の集合で比べる**(JIT やキャッシュの状態がシナリオの順序に依存するため)。
- オプション: `--n`(既定 200)、`--warmup`(既定 20)、`--scenario`(カンマ区切り)、`--json <path>`(UTF-8)。

| ID | 内容 |
|---|---|
| S1 / S2 | →← / ↓↑ の交互(行 10・選択なし・スクロールなし) |
| S3a / S3b | 1 文字挿入(WM_CHAR)と BackSpace を交互に行い、別々に集計 |
| S4 | IME の未確定を「あ」「あい」で交互に更新 |
| S5-r0〜r9 | F-6: 空文書に約 10.6KB を入れるたびに、末尾で x を 40 回。位置(計測直前の UTF-8 バイト数)は `param` 列 |
| S6a / S6b | `TopLine` を ±1 行 / ±1 ページで交互 |
| S7 | 全面再描画だけ(`Invalidate` + `Update`) |
| S8-1 / 40 / 1000 / all | UIA `GetBoundingRectangles`(文書先頭から n 行)を UI スレッドから直接。描画なし |
| S9a / S9b | P-10: 折り返し ON(40 桁)の ja10k で、say all 相当の行読み(`LineEnd` → `LineStartOf` → `LineEndNoBreakOf` を 1 歩)を**ワーカースレッドから**。S9a は UI スレッドがメッセージを汲むだけ、S9b は汲む合間に全面再描画を挟む。`param` 列は 1 歩あたりの UI スレッドへの Invoke 回数 |
| S10 | P-16: 同じ設定(フォントも同じ)で `ApplyAppearance` + `Update`。設定ダイアログの OK の 1 タブぶん |

- キーは WndProc へ直接入れるので、IME/TSF の費用(調査記録 §9 の S-2)は乗らない。体感値は perf-harness で見る。

### Smoke `--paint-transition`(画面に古い絵が残らないことの確認)

```powershell
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --paint-transition                  # 再描画を省く前のコード
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --paint-transition --expect-skip    # 省いた後のコード
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --paint-transition --out <dir>      # 失敗した遷移の絵を PNG で残す
```

- 再描画の省略(フェーズ 3)や部分再描画(フェーズ 9)で、**画面に古い絵が残る**不具合を見る。遷移(キャレット移動・選択・スクロール・IME・テーマ変更など)ごとに、操作してメッセージを流しただけの画面の絵と、全面を描き直した正解の絵を、同じ実行の中で画素で比べる。基準画像は要らない。
- `--paint-snapshot` との違いは**撮影で描き直すかどうか**。`--paint-snapshot` は `PrintWindow` で撮るので撮影の中で全面が描き直され、古い絵が残る不具合は写らない(描画処理そのもののピクセル不変を見る道具)。`--paint-transition` は窓の DC から `BitBlt` で読むだけで描き直さない。撮り方の前提(描画を起こさずに画面の絵を読めること)は陽性対照で確かめる。撮影の前後で描画回数が変わらないことも毎回確かめるが、これは撮り方を変えたときの回帰を防ぐためのもの。
- 出力は遷移ごとに `名前: 描画 N 回・一致 [期待]` または `名前: 描画 N 回・差 K 画素(最初の座標 (x,y)) [期待]`。期待の欄は `Paint` / `Skip` / `Any`、陽性対照は `[陽性対照]`。失敗した遷移には ` → 失敗: 理由` が付く。期待が `Paint` の遷移で描画 0 回なら失敗。`Skip` の遷移で描画 1 回以上なら、**`--expect-skip` を付けたときだけ**失敗(省く前のコードは描き直すので付けない。省いた後のコードで付けると、省略が効いていることも確かめられる)。
- **陽性対照** `control-stale`(表の先頭で最初に走る): 空白の表示を ON にした直後に無効領域を取り消し、差が**出る**ことを要求する。差が出なければ撮り方が画面の絵を読めていないので、自己チェックの失敗としてそこで止める(EXIT 1)。
- EXIT 0 = 全遷移が期待どおり、1 = 失敗または自己チェックの失敗(準備が効かない・描画が起きない・撮影で描画が起きた・撮影が決定的でない・例外)、2 = 引数の誤り。窓は画面内に出るので、画面がロック中だと EXIT 1 になる。計測中は触らない。
- 遷移を足すときは `PaintTransition.Transitions` に 1 要素足す。スクロールした状態から始める遷移は、準備の後の `(TopLine, ScrollX)` を `ArrangedScroll` に書く(既定は (0, 0))。

### `perf-harness.ps1`

```powershell
dotnet publish src/kxEdit.App -c Release -r win-x64 --self-contained false -o <作業フォルダ>\publish
pwsh -File tools\perf-harness.ps1 -PublishDir <作業フォルダ>\publish
pwsh -File tools\perf-harness.ps1 -PublishDir <作業フォルダ>\publish -Scenario M-2,M-7
```

- 配布物と同じ条件で測るときは、publish に `-p:PublishReadyToRun=true -p:DebugType=embedded` を足す(release.yml と同じ。性能改善フェーズ 11 以降)。
- **pwsh(PowerShell 7)専用**。計測中(全シナリオで十数分)は画面・キーボード・マウスに触らない。前面の窓が計測対象でなくなったら中止する。
- シナリオ M-1〜M-7 と n / interval は調査記録 §9.5 のとおり。結果は CSV(`scenario,condition,doc,n,value,unit,flags`)で、既定の出力先は `%LOCALAPPDATA%\kxEdit-perf-harness\results-<日時>.csv`。`-OutCsv` の既存ファイルは上書きしない(中止する)。
- 設計書 §3.2 の判断基準(3 回の最小〜最大)を当てるときは、改善対象のシナリオを 3 回走らせる(`-OutCsv` を毎回別名にするか、既定の日時付きの名前に任せる)。
- `scenario=env` の行は条件の記録: NVDA の有無・kxEdit の版と exe の SHA256・画面の解像度・実行したシナリオ・`status`(`completed` / `aborted(exit N)`)。**`status` が `completed` でない CSV は途中までの結果**なので、比較に使わない。
- `flags` 列の `quiet_timeout` は、静穏待ち(CPU の増分が 2 ms 未満 × 3 回)が 30 秒でタイムアウトし、背景の CPU が混ざった可能性を示す。
- M-2 の `アイドル` は何も送らない周期の費用(キャレットの点滅など)。各操作の値には interval ぶん乗るので、差し引きの目安にする。
- NVDA が起動していると UIA の費用が乗る(調査記録 §9 は NVDA なし)。前後比較では NVDA の有無を揃える。
- 各シナリオは入力の効果を自己チェックする(フォーカス・変更済みの表示・本文長・スクロール位置・矩形数)。効かなければ値を出さずに中止する(EXIT 1)。
- 文書(ja10k / en10k / ja30k)は `%TEMP%\kxEdit-perf-harness` に生成する(`-WorkDir` で変更可)。
- **空のプロフィール(既定設定)で測る**。利用者の設定(セッション復元・フォント・折り返し)で値が変わらないようにするため。
- `-SessionRestore`: 「起動時に前回開いていたファイルを開く」を ON にした空のプロフィールで測る(既定は OFF)。起動のたびに、空にした計測用のプロフィールへ `settings.json` を置く(利用者の退避には触れない)。CSV の `env` 行 `session_restore` に記録する。
- **M-3 はクリップボードを上書きし、終了時に空にする(元の内容は戻さない)**。戻すと、パスワードマネージャーが履歴・同期から外すための印を付けずにパスワードを再投入してしまうため。
- M-4 はマウスカーソルを、本文のうち他の窓(NVDA のスピーチビューアー等の最前面の窓)に覆われていない点へ動かす。

**利用者プロフィール(`%APPDATA%\kxEdit`)の保全**

- 開始前に次のどれかに当たると、**何も変えずに中止する**(EXIT 2): kxEdit が起動中 / `backups` にファイルがある(未保存の本文が残っている可能性)/ 前回の退避が残っている / プロフィールにシンボリックリンク等がある / プロフィールがフォルダーでない。
- 終了コード: 0 = 完走・復元済み、1 = 計測の中止(復元済み)、2 = 開始できない(何も変更していない)、3 = 復元を見送った・失敗した(退避を残している。表示される案内に従う)。
- 起動した kxEdit は Job オブジェクトに入れ、pwsh が終わる(窓を閉じる・強制終了)と一緒に終わる。空のプロフィールのままの kxEdit が手元に残らない。
- 計測がプロフィールを空にする前に止まった場合は、プロフィールに触れずに退避だけを片づける。
- 退避は `%LOCALAPPDATA%\kxEdit-perf-harness\stash` へのコピー。目印 `STASH-MARKER.json` に全ファイルの SHA256 を記録し、照合してから計測に入る。
- 終了時(中止時も)に元へ戻し、照合できたら退避と目印を消す。照合できなければ退避を残して EXIT 3。
- 異常終了で退避が残ったときは、kxEdit を終了してから `pwsh -File tools\perf-harness.ps1 -Recover`。その時点のプロフィールの中身は消さずに `%LOCALAPPDATA%\kxEdit-perf-harness\displaced-<日時>` へ退かせて残す(異常終了の後に kxEdit を使っていた場合の本文・設定を失わないため)。不要なら手で消す。
- ハーネスの二重起動は拒否する。計測中に kxEdit を起動しないこと(計測中の窓に入り、打った内容は強制終了で失われる)。
- 入力の直前に、前面の窓が計測対象のプロセスであることを確かめる。違えば入力を送らずに中止する。
- `%LOCALAPPDATA%\kxEdit`(プレビューの WebView2 用の作業フォルダー)は退避の対象外。利用者データは入っていない。
- 退避・復元と中止条件の検証: `pwsh -File tools\perf-harness.ps1 -SelfTest`(一時フォルダーだけを使う)。

### 内訳を採る: dotnet-trace

グローバルを汚さないよう、作業フォルダーに入れる。

```powershell
dotnet tool install dotnet-trace --tool-path <作業フォルダ>\tools
<作業フォルダ>\tools\dotnet-trace collect -p <kxEdit の pid> --profile dotnet-sampled-thread-time --format Speedscope --duration 00:00:00:16
```

- 採取中に perf-harness の該当シナリオか手操作を行う。
- 集計の観点は「**UI スレッドで管理コードに帰属する区間**」。speedscope の evented プロファイルで、スタックに `RunMessageLoop` を含むスレッドを UI スレッドとし、`GetMessage` / `WaitMessage` / `MsgWaitForMultipleObjects` / `PeekMessage` を含む区間を待機として除き、残りをフレーム名ごとの包含時間で合計する(調査記録 §9.5)。
- 限界: `RunMessageLoopInner` 直下の `UNMANAGED_CODE_TIME` は、待機かネイティブ処理(IME/TSF など)かを区別できない。

### 内訳を採る: WPR(ネイティブ側・カーネル時間)

管理者の PowerShell で実行する。

```powershell
wpr -start CPU -start GeneralProfile
# ここで対象の操作(例: Ctrl+Tab × 40)
wpr -stop <作業フォルダ>\trace.etl
```

- WPA(Windows Performance Analyzer)で開き、CPU Usage (Sampled) をプロセス → スレッド → スタックで見る。
- kxEdit のスレッドの正体(UI スレッド以外)や、カーネル時間の内訳を見るときに使う(設計書フェーズ 6)。

典拠: `docs/plans/2026-09-24-general-perf-improvements-design.md` §3.2・§5、`docs/plans/2026-09-24-general-perf-audit.md` §9、`docs/plans/2026-09-24-perf-bench.md`。
