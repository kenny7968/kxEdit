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
- 外観は製品の既定(ＭＳ ゴシック 12pt・行番号なし・折り返しなし)。文書はメモリ上で生成する(ja10k / en10k。書式は調査記録 §9.5)。
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
| S5@<バイト> | F-6: 空文書に約 10.6KB を入れるたびに、末尾で x を 40 回。位置は計測直前の UTF-8 バイト数 |
| S6a / S6b | `TopLine` を ±1 行 / ±1 ページで交互 |
| S7 | 全面再描画だけ(`Invalidate` + `Update`) |
| S8-1 / 40 / 1000 / all | UIA `GetBoundingRectangles`(文書先頭から n 行)を UI スレッドから直接。描画なし |

- キーは WndProc へ直接入れるので、IME/TSF の費用(調査記録 §9 の S-2)は乗らない。体感値は perf-harness で見る。

### `perf-harness.ps1`

```powershell
dotnet publish src/kxEdit.App -c Release -r win-x64 --self-contained false -o <作業フォルダ>\publish
pwsh -File tools\perf-harness.ps1 -PublishDir <作業フォルダ>\publish
pwsh -File tools\perf-harness.ps1 -PublishDir <作業フォルダ>\publish -Scenario M-2,M-7
```

- **pwsh(PowerShell 7)専用**。計測中(全シナリオで十数分)は画面・キーボード・マウスに触らない。前面の窓が計測対象でなくなったら中止する。
- シナリオ M-1〜M-7 と n / interval は調査記録 §9.5 のとおり。結果は CSV(`scenario,condition,doc,n,value,unit`)で、既定の出力先は `%LOCALAPPDATA%\kxEdit-perf-harness\results-<日時>.csv`。
- 文書(ja10k / en10k / ja30k)は `%TEMP%\kxEdit-perf-harness` に生成する(`-WorkDir` で変更可)。
- **空のプロフィール(既定設定)で測る**。利用者の設定(セッション復元・フォント・折り返し)で値が変わらないようにするため。
- M-3 はクリップボードを使う。テキストは開始前に保存して終了時に戻すが、テキスト以外の内容は失われる。
- M-4 はマウスカーソルを本文の中央へ動かす。

**利用者プロフィール(`%APPDATA%\kxEdit`)の保全**

- 開始前に次のどれかに当たると、**何も変えずに中止する**(EXIT 2): kxEdit が起動中 / `backups` にファイルがある(未保存の本文が残っている可能性)/ 前回の退避が残っている / プロフィールにシンボリックリンク等がある。
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

管理者のコマンドプロンプトで実行する。

```powershell
wpr -start CPU -start GeneralProfile
# ここで対象の操作(例: Ctrl+Tab × 40)
wpr -stop <作業フォルダ>\trace.etl
```

- WPA(Windows Performance Analyzer)で開き、CPU Usage (Sampled) をプロセス → スレッド → スタックで見る。
- kxEdit のスレッドの正体(UI スレッド以外)や、カーネル時間の内訳を見るときに使う(設計書フェーズ 6)。

典拠: `docs/plans/2026-09-24-general-perf-improvements-design.md` §3.2・§5、`docs/plans/2026-09-24-general-perf-audit.md` §9、`docs/plans/2026-09-24-perf-bench.md`。