# 通常利用における性能改善 設計書

策定日: 2026-09-24
策定ブランチ: `feature/general-perf-audit`
前提資料: `docs/plans/2026-09-24-general-perf-audit.md`(以下「調査記録」)

## 1. 背景・目的

調査記録では、通常利用(巨大ファイルを除く)の性能を静的に調べて指摘 P-1〜P-25 を挙げ、windows-mcp による実測で主要な指摘を確認した(調査記録 §9)。本書はそれらを**フェーズ単位の改善**に落とし込む。

各フェーズは、**別々のセッションで、本書の該当節だけを読めば着手できる**ように書く。

**目的**
- キャレット移動・打鍵・スクロール・タブ切替・SR からの問い合わせといった日常操作の応答を改善する。
- 各改善は挙動不変を原則とする。意図的に挙動を変えるものは §3.5 に列挙する。

**範囲外**: 巨大ファイル(500MB〜1GB 級)の性能。長大行の既知課題もここでは扱わない。

## 2. 決定事項(ブレインストーミングでの合意)

| 論点 | 決定 |
|---|---|
| 統合単位 | **フェーズごとに PR**。main から `feature/perf-<topic>` を切り、pre-merge-check → PR → マージ |
| 描画の範囲 | P-1(a)(無変化時の再描画省略)と、P-1(b) + P-4(部分再描画・ScrollWindowEx)を**別フェーズで両方**行う |
| 挙動・形式・セキュリティに関わる候補 | 次の 4 つを採用する。いずれも §3.5 に記載する |
| | P-12: バックアップ JSON を生の UTF-8 にする |
| | P-7: ReadyToRun |
| | P-5: 検索件数の入力間引き |
| | P-21(b): プレビュー環境の使い回し(評価が先。不採用で終わることもある) |
| 効果の確認 | **両方**用意する: Smoke の `--perf` ベンチ(再現性)と、tools/ の実環境ハーネス(体感値) |
| フェーズの並べ方 | 効果÷リスクの順に並べ、依存関係で調整する |

## 3. 全フェーズ共通の規約

### 3.1 セッションの進め方

各フェーズの実装セッションは、次の順で進める。

1. 本書 §3 と該当フェーズの節を読む。調査記録の関連箇所(指摘 ID で引く)も読む。
2. main から `feature/perf-<topic>` を切る(topic はフェーズ表を参照)。
3. superpowers:writing-plans で実装計画 `docs/plans/YYYY-MM-DD-perf-<topic>.md` を作る。計画には「変更前の計測(§3.2)」を最初のタスクとして入れる。
4. CLAUDE.md §3 の 4〜6 に従って実装・レビュー・品質ゲートを通す。
5. PR description に、変更前後の計測値・意図的な挙動差・L5 の結果を載せる。
6. マージ後、本書の該当フェーズ節の末尾に「実施記録」を追記する(CLAUDE.md §8 で許される追記)。

**規模に応じた注意**
- 本書の設計が実装時に誤りと判明した場合は、計画書で精密化し、逸脱を PR に書く。
- フェーズ 9 は 2 セッション(9a / 9b)に分けてよい。

### 3.2 計測プロトコル(フェーズ 0 の完成後)

- **再現性の数値**: `dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --perf`
  - 画面内の窓で実描画させて測る。Release ビルドを使い、他の重い処理を止めた状態で走らせる。
  - 変更前と変更後を同じマシンで各 3 回走らせ、中央値を比べる。
- **体感値**: `tools/perf-harness.ps1`
  - 実アプリを SendInput で操作し、プロセスの CPU 時間を測る。
  - 変更前と変更後の publish を同じ条件で測る。
- **内訳が必要なとき**: dotnet-trace(`dotnet-sampled-thread-time`)を使う。ネイティブ側の内訳は WPR を使う。手順は tools/README に置く(フェーズ 0)。
- **判断基準**: フェーズの改善対象の数値が、改善前の揺れ(3 回の最小〜最大)を超えて改善していること。改善が揺れの範囲に収まる場合は、PR に記載したうえで採否をユーザーに判断してもらう。

### 3.3 L5(実機 SR 検証)と目視

CLAUDE.md §5 に従う。フェーズ表の「L5」列が「必須」のフェーズは、次を行う。
- NVDA 実機での確認
- `tools/sr-regression.ps1`
- 目視の場合は、PrintWindow による実解像度の画面取得で判定する(縮小スクリーンショットで判定しない)

### 3.4 レビューとミューテーション検証

**前倒しレビュー**(CLAUDE.md §3 の 4)
- **脆弱性レビュー**: フェーズ 7(パス操作・ファイル削除)、フェーズ 8(外部ファイルのパース)、フェーズ 12(WebView)
- **コード品質レビュー**: 新しい共通の seam を導入するフェーズ 0(計測基盤)、フェーズ 3(フレーム入力の集約)、フェーズ 9(クリップ対応の描画)

**ミューテーション検証**(CLAUDE.md §4-A)
- **対象としてよい**:
  - フェーズ 2 の `ComputeCaretPoint` 短絡(キャレット位置の算出)
  - フェーズ 4 の格子(文字⇔バイトの対応。カーソルと選択の算出の土台)
  - フェーズ 5 の一致位置キャッシュ(検索エンジンの中核)
- **禁止**: GUI レイアウト・描画・テーマ、ファイル I/O(フェーズ 1・3・7・9・11 の該当部分)

### 3.5 意図的な挙動差の一覧(各 PR に記載する)

| フェーズ | 挙動差 |
|---|---|
| 2 | `GetBoundingRectangles` の範囲先頭から 10 万行より先に可視域がある場合、従来は空配列だったが、矩形を返すようになる |
| 4 | 追記ブロック 4KB ごとにピースが 1 つ増える(内部の診断値 `PieceCount` だけに現れる) |
| 5 | 検索語の打鍵では、件数表示が最後の打鍵から 200 ms 後に更新される |
| 6 | (採用した場合のみ)セッション復元 ON のとき、タブ切替だけによるレイアウトの即時書込をまとめる |
| 7 | バックアップ JSON の非 ASCII がエスケープされなくなる(読込は新旧互換) |
| 7 | 最近使ったファイルの一覧が変わらない場合は、settings.json を保存しない |
| 8 | リテラル検索のプリフィルタでは、正規表現のタイムアウト(1 秒)が 1 行単位ではなく全文単位で効く |
| 11 | 配布物が ReadyToRun になり、zip が約 2.3MB 大きくなる |

## 4. フェーズ一覧

| # | フェーズ(topic) | 指摘 | 現状値(調査記録 §9) | L5 | 依存 |
|---|---|---|---|---|---|
| 0 | 計測基盤(`perf-bench`) | — | — | 不要 | — |
| 1 | 描画の固定費削減(`perf-paint-cost`) | P-24・P-17(背景)・P-20・P-2 | 全面再描画 8.9 ms(ja) | 目視と NVDA の簡易確認 | 0 |
| 2 | UIA の座標と矩形(`perf-uia-rects`) | S-1・P-9 | 1 万行の矩形取得 3,534 ms | **必須** | 0 |
| 3 | 無変化時の再描画省略(`perf-skip-invalidate`) | P-1(a) | キャレット移動 17.5 ms(ja) | **必須** | 2 |
| 4 | 追記ブロックの格子(`perf-append-grid`) | P-3(F-6) | ブロック後半で +9.4 ms/打鍵 | **必須** | 0 |
| 5 | 検索(`perf-search`) | P-13・P-5・P-14 | 3MB で全文化 2.4 ms/打鍵 | 不要 | 0 |
| 6 | タブ切替とバックアップ(`perf-tab-switch`) | P-25・P-6 | Ctrl+Tab 97〜109 ms | 調査後に判断 | 0 |
| 7 | I/O と設定(`perf-io-settings`) | P-11・P-22・P-12・P-15・P-21(a) | 未計測(リモートで約 200 ms/復帰と推定) | 不要 | — |
| 8 | grep(`perf-grep`) | P-8 | 未計測 | 不要 | 0 |
| 9 | 部分再描画とスクロール(`perf-partial-paint`) | P-1(b)・P-4 | 打鍵 22 ms・PageDown 21.6 ms(ja) | **必須(目視が重い)** | 1・3 |
| 10 | 折り返し ON の UIA(`perf-uia-wrap-cache`) | P-10 | 1 行の読みごとに Invoke 2〜5 回 | **必須** | 2 |
| 11 | 起動・生成(`perf-startup`) | P-7・P-16(前半) | 起動 213 ms | 起動確認 | — |
| 12 | プレビュー環境の使い回し(`perf-preview-env`) | P-21(b) | 未計測(0.5〜2 秒/表示と推定) | 必要 | 7 |

**着手順の推奨**: 0 → 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 10 → 11 → 9 → 12

**順序の理由**
- 9 は目視の L5 が最も重く、1 と 3 の上に積むので後ろに回す。
- 12 はセキュリティ評価が先に立つので最後に置く。
- 依存のないフェーズ同士は、順序を入れ替えてよい。

## 5. フェーズ 0: 計測基盤(`perf-bench`)

**目的**: 以降の全フェーズの変更前後を、同じ手順で比べられるようにする。製品コードは変更しない。

### 5.1 Smoke `--perf`

`tests/kxEdit.Editor.Smoke` に `--perf` サブコマンドを追加する(新ファイル `PerfBench.cs`)。

**構成**
- 既存の `GdiBench` と同じく**画面内**に Form を出し、EditorControl を Dock=Fill で置く。画面外の窓には WM_PAINT が配送されないため(`2026-08-02-large-line-wrap-perf-design.md` §3)。
- 窓は 900×700、フォントは「ＭＳ ゴシック」12pt(全角表記。`ApplyAppearance` 相当を必ず通す)。
- 文書はメモリ上で生成する。調査記録 §9.1 の `ja10k` / `en10k` と同じ行形式にする。

**計測方法**
- 各シナリオで「操作 → `editor.Update()`(同期 WM_PAINT)」を 1 回として、Stopwatch で N 回(既定 200)測る。
- ウォームアップを 20 回入れる。
- 出力は平均・中央値・p95。

**シナリオ**

| ID | 内容 | 使う API |
|---|---|---|
| S1 | → / ← の交互(選択なし・スクロールなし) | 公開 API または internal(InternalsVisibleTo を確認する) |
| S2 | ↓ / ↑ の交互 | 同上 |
| S3 | 1 文字挿入 | `InsertConfirmedText` 相当 |
| S4 | IME の未確定更新 | 既存のテストフック(`__TestApplyComposition` 等)を探して使う |
| S5 | F-6: 新規文書に約 10KB ずつ挿入し、位置ごとに S3 を 40 回 | ブロック内位置 0〜96KB |
| S6 | 1 行スクロールと PageDown 相当 | `TopLine` の setter |
| S7 | 全面再描画だけ | `Invalidate()` + `Update()` |
| S8 | UIA の矩形取得: 1 行・40 行・1,000 行・全文 | `UiaTextHostAdapter.GetBoundingRectangles` を UI スレッドから直接呼ぶ(COM を通さない) |

- 判定はしない(EXIT 0)。基準値を固定するのはデータがたまってからとする。
- `--perf --json <path>` で機械可読な出力も出せるようにする。

### 5.2 tools/perf-harness.ps1

調査記録 §9.1 の手順を tools/ に固定する。作業用に作った使い捨てのハーネスを整理したもので、BOM 付き UTF-8、pwsh 推奨とする。

**入力と出力**
- 入力: 対象の publish フォルダー(`-PublishDir`)と、シナリオの選択(`-Scenario`)。
- 出力: CSV(1 行 = シナリオ × 条件 × CPU ms/回)。

**前提チェック**
- kxEdit が起動中なら中止する。
- `%APPDATA%\kxEdit` を退避し、各シナリオの前と終了時に復元する(try/finally)。
- ローカルパスは書かない(`$env:APPDATA` と `$PSScriptRoot` を使う)。

**シナリオ**
- M-1: 起動
- M-2: キャレット・打鍵・何も変わらないキーの基準
- M-3: F-6
- M-4: スクロール
- M-5: 検索語の打鍵
- M-6: タブ切替
- M-7: UIA の矩形
- IME/TSF の下限(調査記録 §9 の S-2)を測る「何も変わらないキー」と「Shift の単押し」を必ず同時に測る。

**罠**(メモリーの L5 自動化ノートから)
- SetForegroundWindow は失敗することがあるので、リトライして戻り値を読む。
- ダイアログにキーを送るときは、ハーネスがメインウィンドウを前面に戻さないようにする。
- 矢印キーには `KEYEVENTF_EXTENDEDKEY` を立てる。

### 5.3 tools/README

dotnet-trace と WPR の手順を追記する。
- dotnet-trace は `dotnet tool install dotnet-trace --tool-path <作業フォルダ>` でグローバルを汚さずに入れる。
- 集計の観点は「UI スレッドで管理コードに帰属する区間」を見ること。

### 5.4 完了条件

- 調査記録 §9 と同程度の数値が再現する。
- 手順が tools/README に載っている。
- レビュー: コード品質レビューを前倒しで行う(以後の全フェーズが依存するため)。

## 6. フェーズ 1: 描画の固定費削減(`perf-paint-cost`)

**目的**: 全面再描画 1 回あたりのコストを下げる。描画結果はピクセル単位で不変とする。

**背景**: ja10k の全面再描画は 8.9 ms。内訳は DrawText 4.8、MeasureText 1.4、GetWindowText 0.2〜1.2、背景 0.5。

### 6.1 P-24: 描画ごとの GetWindowText をやめる

**現状**(.NET 9 WinForms の実ソースで確認済み)
- `Control.WmPaint` が、背景層と前景層の 2 回 `PaintWithErrorHandling` を呼ぶ。
- それぞれが `CacheTextInternal = true` を経て `WindowText` を読み、WM_GETTEXTLENGTH と WM_GETTEXT を自 HWND に送る。描画 1 回あたり SendMessage が 4 往復になる。
- EditorControl は WM_GETTEXT に 0 を返し(`EditorControl.cs:2101-2105`)、`Text` を `new` で隠蔽しているので(`:257-262`)、結果は常に "" である。

**変更**: ctor で `SetStyle(ControlStyles.CacheText, true)` とする。
- `set_CacheTextInternal` は冒頭で return し、`WindowText` を読まなくなる。

**等価性**
- base の `Control.Text` getter は `_text ?? ""` を返す。`_text` は誰も設定しないので、結果は現状と同じ "" になる。
- WM_GETTEXT の抑止は WndProc 側にあるので不変。
- UIA の Name は自前のプロバイダが返す固定値「本文」で、`Text` とは無関係。

**確認すること**
- src 内に `((Control)editor).Text = ...` のような base の Text への書き込みがないこと(grep)。
- レイアウト(`:8656`)と GetPreferredSize(`:2183`)の経路でも WindowText を読まなくなることを確認する。

**テスト**
- WndProc に WM_GETTEXT / WM_GETTEXTLENGTH の受信回数を数えるテストフックを追加する(既存の `TestHook_*` の流儀に合わせる)。
- 描画に相当する呼び出しで 0 回であることを確認する。
- `((Control)editor).Text == ""` を確認する。

### 6.2 P-17: 背景の三重塗りを二重にする

**現状**: 1 回の描画で、次の 3 つが全面を塗っている。
1. WinForms の背景層(`OnPaintBackground`)
2. `OnPaint` 冒頭の `g.Clear`(`Paint.cs:20`)
3. FrameBuilder の工程 1 の全面 FillRect

**変更**: ctor で `SetStyle(ControlStyles.Opaque, true)` とし、1 を消す。
- 2 は残す。`_scrollX` シフトで生じる右端の隙間と、スクロールバーの交差部の角を埋めているため。
- 3 も残す。

**副次効果**: 背景層の `PaintWithErrorHandling` が呼ばれなくなる。`InvalidateRect` の bErase も false になる。

**確認すること**: 両方のスクロールバーが出ているときの右下の角と、リサイズ中のちらつき(目視)。

### 6.3 P-20: バックバッファの毎回確保をやめる

**現状**
- `WmPaint` は毎回 `BufferedGraphicsManager.Current.Allocate(dc, ClientRectangle)` を呼ぶ。
- エディタの面積は `MaximumBuffer`(既定 225×96)を超えるので、一時コンテキストと DIB を毎回作って破棄している。
- コストは未計測で、推定 1〜3 ms/描画。

**進め方**: **先に測る**。Smoke の S7 で、`BufferedGraphicsManager.Current.MaximumBuffer` を仮想画面サイズにした場合と比べる。改善が 0.5 ms/描画以上あれば採用する。

**採用する場合の形**
- Editor 層に、UI スレッド共有の `BufferedGraphicsContext`(static・`MaximumBuffer` = 仮想画面サイズ)を 1 つ持つ。
- EditorControl は `OptimizedDoubleBuffer` を外し、`OnPaint` の中でこのコンテキストから Allocate → 描画 → Render を明示的に行う。
- 共有にするのは、タブごとに画面サイズの DIB を抱えないため。
- `BufferedGraphicsManager.Current` の MaximumBuffer をプロセス全体で変える案は、他コントロールへの影響が読めないので採らない。

**注意**: 6.2 の `Opaque` とあわせて、WinForms 側の二重バッファ経路を通らなくなる。`AllPaintingInWmPaint` の意味が変わらないことを確認する。

### 6.4 P-2: 非 ASCII を含む run の幅をメモ化する

**現状**: `GdiCharMetrics.MeasureRun`(`:71-95`)がメモ化しているのは、非 ASCII の単一コードポイントだけ。非 ASCII を含む 2 コードポイント以上の run は、毎回 `TextRenderer.MeasureText` を呼ぶ。

**影響している呼び出し元**
- `FrameBuilder.cs:467`: 行全体・描画ごと
- `EditorControl.cs:1546-1554`: `UpdateHorizontalScrollbar` が全可視行を毎打鍵測る。実測 1.8 ms/打鍵
- `PixelMapper.OffsetToPx`: キャレット X・選択矩形

**変更**
- `GdiCharMetrics` に、文字列をキーにした上限付きのメモを足す。
  - `Dictionary<string, int>` に .NET 9 の `GetAlternateLookup<ReadOnlySpan<char>>()` を使い、ヒット時は文字列を割り当てない。
  - 上限: 1 件 4,096 文字以下、4,096 件。溢れたら全消去する。
- 対象は「先頭が非 ASCII の単一コードポイント」以外で、非 ASCII を 1 つ以上含む run。
- ASCII だけの run と単一コードポイントの経路は変えない。

**等価性**
- 格納するのは同じフォント・同じ文字列に対する `MeasureText` の結果そのものなので、返す値は同一。
- キャッシュの寿命はインスタンス(= フォント)の寿命と一致する(既存の幅メモと同じ規約)。

**スレッド**: UI スレッド専用の契約(`GdiCharMetrics.cs:13-29`)を引き継ぐ。RPC スレッドから直接呼ぶ経路はない(Invoke 後に呼ばれる)。

**テスト**: `GdiCharMetricsCacheTests` に次を追加する。
- キャッシュなしの参照実装との一致
- 上限超えで全消去
- 4,096 文字を超える run はキャッシュしない
- span キーと string キーの一致

### 6.5 完了条件

- **計測**: S7(全面再描画)・S1・S3 が改善していること。
- **目視**
  - テーマ(既定・黒地)
  - 選択
  - 現在行強調
  - 行番号
  - 空白表示
  - 水平スクロール時の右端
  - 右下の角
  - IME の未確定表示
- **NVDA 簡易確認**: フォーカス時の名前「本文」、行の読み上げ、ハイライト矩形の位置。
- ピクセル不変の確認: 変更前後で同じ操作列の PrintWindow 画像を比べる(差分 0 を期待する)。

## 7. フェーズ 2: UIA の座標と矩形(`perf-uia-rects`)

**目的**
- S-1: UIA のスクリーン座標が古くなる問題を解消する。
- P-9: 複数行範囲の `GetBoundingRectangles` の O(行数 × 可視行数) を解消する。
- フェーズ 3 の前提を整える。

### 7.1 S-1: 座標を問い合わせ時に求める

**現状**
- `_clientToScreenX/Y` は `OnPaint` 末尾(`RefreshClientToScreenOrigin`)と `OnBoundsChanged` でしか更新されない。
- `_bounds` は `OnBoundsChanged`(SizeChanged / LocationChanged / HandleCreated)でしか更新されない。
- `LocationChanged` は親から見た位置の変化なので、メインウィンドウを動かしても発火しない。src に Move / WM_WINDOWPOSCHANGED のハンドラはない。
- 陳腐化は実在する(監査 M-10 と同じ)。

**変更**
- `ComputeBoundingRectangles` と `ComputeOffsetFromScreenPoint` は Invoke 後の UI スレッドで走る。その場で `_host.PointToScreen(Point.Empty)` を求め、キャッシュを使わない。
- `IUiaTextHost.BoundingRectangle`(RPC スレッドから読む)は、キャッシュ済みの `_hwnd` に対して Win32 の `GetClientRect` + `ClientToScreen` を P/Invoke でその場で求める。
  - これらはスレッド安全な Win32 API で、エディタ内部の状態に触れないので、a11y 鉄則に反しない。
  - `_hwnd == 0`、あるいは Win32 呼び出しが失敗した場合は、従来と同じ既定値(空矩形)を返す。
- `RefreshClientToScreenOrigin` と `_clientToScreenX/Y` のキャッシュは削除する。`_bounds` も削除するか、失敗時の既定値だけに縮める。

**等価性**
- 従来の値は「更新直後の `RectangleToScreen(ClientRectangle)` と `PointToScreen(0,0)`」だった。新しい値は常にそれと一致し、古い値だけがなくなる。

**テスト**(画面外の HostForm では OnPaint が走らないので、陳腐化を観測できる)
- `form.Location` を動かした後、`GetBoundingRectangles` / `OffsetFromScreenPoint` / `BoundingRectangle` が `PointToScreen` と一致すること。

### 7.2 P-9: 範囲の行ループを可視域に限定する

`UiaTextHostAdapter.ComputeBoundingRectangles`(`:590-639`)に (a)(b) を、`EditorControl.ComputeCaretPoint`(`:2514-2618`)に (c) を入れる。

**(a) 可視域より上を飛ばす**
- ループの前で `_topLine < snap.LineCount` を確認し、`pos < GetLineStart(_topLine)` なら `pos = GetLineStart(_topLine)` にする。
- 上の行は必ず即座に不可視を返し、副作用もないので、厳密に等価である。
- CRLF の中間は前の行に属する規約でも成り立つ。
- 前提: adapter の `_bufferSnapshot` と host の `_buffer.Current` が同一であること。UI スレッド上では編集経路が同期的に `OnSnapshotChanged` を呼ぶので成り立つ。計画書で再確認する。

**(b) 下で打ち切る**
- `line > _topLine` で不可視になったら `break` する。
- 後続行の積み上げ行数は単調に増えるので、後続行も必ず不可視になる。
- `line == _topLine` のときは `_topSegment` による上方向のはみ出しがあるので、打ち切ってはならない。

**(c) 折り返し OFF の短絡**
- 折り返し OFF(`_wrapColumns <= 0`)では、積み上げループを使わずに次で求める。
  - `n = logicalLine - _topLine`
  - `(long)n * lineHeight >= paintHeight` なら不可視
  - そうでなければ `visualRowsBeforeThisLine = n`
- **long で計算する**(int だと溢れうる)。
- `lineHeight <= 0` は従来のループに戻す。
- 従来ループとの同値性: OFF では `WrapCore` が常に 1 セグメントを返し、`eff = 0` なので、ループは「k 行目で `k * lh >= ph`」を判定するだけである。
- 効果: 毎打鍵の `PositionCaret` / `BringCaretIntoView` でも走る経路なので、行ごとの `LineTextOf`(P-3 の重い経路)も消える。

**意図的な挙動差**: 範囲先頭から 10 万行より先に可視域がある場合、従来は `safety` で打ち切られて空配列だった。改善後は矩形が返る(§3.5)。

**テスト**
- `EditorControlBoundingRectsTests` に次を追加する。
  - TopLine > 0
  - 範囲が可視域の上・中・下にまたがる場合
  - 下端超過
  - 折り返し ON(`_topSegment > 0` を含む)
  - CRLF・空行・改行なしの最終行
  - PaintHeightPx = 0
- 折り返し OFF の `ComputeCaretPoint` について、新旧実装を突き合わせる Theory テストを追加する(境界: 最終可視行、1 行はみ出し、lineHeight の端数)。旧実装はテスト内に参照実装として残す。
- ミューテーション検証: (c) の判定式にスポットチェックを行う(§3.4)。

### 7.3 完了条件

- **計測**: S8 の 1 万行が 1 行と同程度(十数 ms)になること。
- **L5**
  - NVDA のフォーカスハイライト(視覚的ハイライト)が、メインウィンドウを動かした後も正しい位置を指すこと。
  - 全選択時の矩形。
  - `RangeFromPoint`(マウス追従の読み上げ)。
  - `tools/sr-regression.ps1`。

## 8. フェーズ 3: 無変化時の再描画省略(`perf-skip-invalidate`)

**目的**: 描画内容が変わらないキャレット移動で、全面再描画をしない。

**背景**
- ja10k のキャレット移動は 17.5 ms/回。うち全面再描画が 8.9 ms。
- 既定設定(選択なし・現在行強調 OFF)では、キャレットだけの移動でフレームは 1 ピクセルも変わらない。キャレットは OS のシステムキャレットである。

**依存**: フェーズ 2(S-1)。現在は `OnPaint` 末尾が座標更新の実質的な契機になっており、描画を省くとそれが消えるため。

### 8.1 フレーム入力の集約(seam)

`OnPaint` が FrameBuilder / RenderFrame に渡している入力を、1 つの不変値 `FrameInputs` に集める。`OnPaint` 自身もこの値から描く。こうすれば、描画に新しい入力を足すと必ず `FrameInputs` に入る。

**FrameInputs に入れるもの**(調査で列挙した全入力)
- 本文スナップショットの参照
- `_topLine` / `_topSegment` / `_scrollX` / `_wrapColumns`
- paintWidth / paintHeight(`_hscroll.Visible` を含む)
- 行番号の表示と行番号幅(LineCount の桁数)
- `currentLineLogical`(`_highlightCurrentLine && !HasSelection ? キャレットの論理行 : -1`)
- 選択範囲(選択がないときは空)
- `_cellHighlight`
- `ShowWhitespace`
- `_style` / `_metrics` / `_font` の参照
- `BackColor`
- IME 未確定状態の参照(`ImeCompositionState` の配列メンバーは参照比較になるので、打鍵ごとに「変化あり」となる。安全側)

**描画に影響しないので入れないもの**: `_hasFocus`、`_caretWidthPx`、`Overtype`、`ReadOnly`、`DesiredXpx`、IME の `CursorPos`。

### 8.2 条件付き Invalidate

- `OnPaint` は、描いたフレームの `FrameInputs` を `_lastPaintedInputs` に保存する。
- キャレット・選択の 4 経路(`EditorControl.Caret.cs:170/214/246/281`)の `Invalidate()` を `InvalidateIfFrameChanged()` に置き換える。
  - 現在の `FrameInputs` が `_lastPaintedInputs` と等しければ何もしない。
  - 異なれば `Invalidate()` する。
- **正しさの根拠**
  - 画面に出ているのは `_lastPaintedInputs` から決定的に描いたフレームである。現在の入力が同じなら、再描画しても同じ絵になる。
  - 未処理の無効領域がある場合でも、その描画は現在の入力で描かれるので問題ない。
  - スクロールのセッターは自前で無条件に Invalidate するので、この比較の外にある。
- **ほかの Invalidate 呼び出しは変えない**: 編集・IME・外観・CSV 強調・スクロールのセッター・リサイズは無条件のまま。変更範囲を最小にするためである。

### 8.3 対象外

- IME の未確定更新は、打鍵ごとにフレームが変わるので効果はない(フェーズ 9 の部分再描画で扱う)。
- 現在行強調 ON で行が変わる移動は、従来どおり全面再描画になる(フェーズ 9 で旧行と新行だけにする)。
- `BringCaretIntoView` の二重呼び出し(P-17 の後半)は既知の申し送り(`Caret.cs:393-407`)で、本フェーズでは扱わない。

### 8.4 テスト

**Invalidate の回数**: `Control.Invalidated` イベントで数える(既存の先例は `EditorControlConvertEolsTests.cs:992-1012`。ホストは `MakeHosted` を使う)。

| 操作 | 期待する Invalidate の回数 |
|---|---|
| 強調 OFF・選択なしのキャレット移動 | 0 |
| 強調 ON で行が変わる | 1 |
| 選択の変化 | 1 |
| スクロールを伴う移動 | 1 |
| 選択の解除 | 1 |

**オラクル**
- ランダムな操作列(移動・選択・編集・スクロール・設定変更・IME)を流す。
- Invalidate を省いた時点ごとに、`FrameBuilder.Build(現在の FrameInputs)` の op 列が `Build(_lastPaintedInputs)` と一致することを確かめる。FrameBuilder は Core の純関数なので、GDI なしで検証できる。
- 画面外の HostForm では WM_PAINT が来ないので、`_lastPaintedInputs` の更新はテストフックで模擬する。

**網羅性**: `FrameInputs` の各メンバーを 1 つずつ変えて、等値比較が「変化あり」になることを表形式で確認する。

### 8.5 完了条件

- **計測**: S1 と S2(強調 OFF)が、全面再描画の分だけ下がること(期待値は約 −9 ms)。
- **L5**
  - 目視: 強調 ON/OFF、選択の開始と解除、IME、スクロール、テーマ変更の直後。
  - NVDA: キャレット移動の読み上げ、ハイライト矩形。

## 9. フェーズ 4: 追記ブロックの格子(`perf-append-grid`)

**目的**: 打鍵や貼り付けで入力したテキストへの文字アクセスが、書込位置に比例して重くなる問題(F-6)を解消する。

**背景**
- 実測で、ブロック内の位置 10KB → 53KB で +9.4 ms/打鍵。新しいブロックに移ると戻る、のこぎり型である。
- 過去の判断「単独では割に合わない」(`2026-08-02-large-line-resilience-design.md` §3)を、**この実測を根拠に覆す**。
- 採る案は、`2026-07-31-char-access-seam-design.md` §8 F-6 が挙げた 2 案(F-1 CharCursor / ピースに char オフセットを持たせる)とは別の第 3 案で、ピース木の不変条件には触れない。

### 9.1 変更

**`TextChunk` の ctor**
- `gridLimit` 引数を追加する。格子点は `< gridLimit` の位置にだけ置く(**厳密に未満**)。
- フロンティアちょうどに置くと、RPC スレッドの読みと UI スレッドの書込が同じバイトで競合する余地があるため。
- 既存の呼び出しは `gridLimit = bytes.Length` と等価にする。

**`AppendBuffer`**
- 書込み後に `_pos` が 4KB 境界(既存の読込チャンクと同じ格子幅)をまたいだら、同じ `_block` を `gridLimit = _pos` の新しい `TextChunk` で包み直し、`_chunk` を差し替える。
- **順序は「書込 → 包み直し → 新しいチャンクでピースを作る」**。逆にすると、32KB 以下の貼り付けピースが古い格子を参照し、長い走査が残る。
- 包み直しの構築コストは、前の包みの格子を引き継いで差分だけ走査する形にしてもよい(計画で判断する)。全体を走査しても 1 ブロック 16 回 × 平均 32KB で、無視できる規模である。

**ピースの結合は変えない**
- `TextBuffer.cs:243-280` の `ReferenceEquals(chunk)` はそのまま残す。
- 4KB 境界をまたいだ直後の打鍵は、新しいピースになる。1 ブロックで最大 16 ピースの増加(§3.5)。
- `TextBuffer.Splice` の中核に触れないための選択である。

### 9.2 等価性と安全性

- **CR/LF の規約**: 格子点の直後に LF が後から書かれても、クエリ時の補正(ScanForward の `br--`、NthBreakEndChar、SplitStats の `f--`)が効く。したがって書込済み範囲の末尾に置いた格子点は規約上安全である(`TextChunk.cs:11`, `:183-184`, `:251`, `:335-336`)。
- **細かい格子が壊れた原因**は、未書込のゼロ領域を数えたことだった(`AppendBuffer.cs:21-24`)。`gridLimit` によってゼロ領域には格子点を置かないので、この原因を踏まない。
- **スレッド**: 新しいチャンクは、スナップショットの公開前に UI スレッドで作る。公開は volatile 参照なので、可視性の保証は既存と同じである。
- **Undo**: 古い包みを参照するルートも、書込済みの範囲が不変なのでそのまま有効である。
- **メモリ**: 包み 1 個あたり object と 3 配列で、1 ブロックあたり約 3.7KB。

### 9.3 テスト(正解は元の文字列に取る)

GetChar と GetText の相互比較では格子の破損を検出できない(seam 設計書 §9.4)ので、正解は元の文字列にする。

- 1 字ずつの打鍵で 4KB 境界をまたぐ。CR をフロンティアの最後のバイトに置き、LF は次の打鍵で書く。
- 多バイト文字を名目 4KB 位置にまたがせる。
- 書込み後も、古いスナップショットの GetText / GetLineStart / GetLineIndexOfChar が不変であること。
- 包み直しをまたいだ Undo / Redo。
- `FuzzTests` に、追記量を 4KB 境界を何度もまたぐ規模にしたケースを追加する。
- 既存の `TextSnapshotGetCharEquivalenceTests`(ゼロ領域の網を含む)は全件通すこと。

**更新するコメント**(格子 1 エントリ前提の記述)
- `AppendBuffer.cs:5-11,21-24`
- `TextChunk.cs:37`
- `TextSnapshot.cs:95-97`
- `TextSnapshotGetCharEquivalenceTests.cs:159-160`

**ミューテーション検証**: 格子点の上限判定と包み直しの条件にスポットチェックを行う(§3.4)。

### 9.4 完了条件

- **計測**
  - S5 ののこぎり型が消えること(位置によらず、ほぼ一定)。
  - Core.Bench `--largeline` の F-6 節(打鍵で育てた 70,000 字の GetChar)が位置によらなくなること。
  - `--typing` のゲート(5 µs/insert)を通ること。
- **L5**: 新規文書に長文(64KB 以上)を打ち込んだ後、NVDA で行・単語・文字単位に読み、読み上げ位置がずれないこと。

## 10. フェーズ 5: 検索(`perf-search`)

**目的**
- 検索語の打鍵と F3 の、全文化と全件列挙を減らす。
- 全文化そのもののコストも下げる。

### 10.1 P-13: `TextSnapshot.GetText` のコピーを 1 回にする

**現状**(`TextSnapshot.cs:39-54` / `:259-281`): ピースごとの string → StringBuilder → ToString と、3 回コピーしている。

**変更**
- `string.Create(length, …)` で、ピース全体は `Encoding.UTF8.GetChars(span, dest)` を使って宛先へ直接デコードする。
- 始端がサロゲートの中間の場合: そのコード点を 2 char の一時バッファへデコードして `[1]` だけ書く。
- 終端がサロゲートの中間の場合: そのコード点をデコードして `[0]` だけ書く。
- `Debug.Assert(書込数 == length)` を置く。

**前提**: 本文に不正な UTF-8 がないこと(Utf8Sanitizer 済み)と、ピース境界がコード点境界であること。

**テスト**
- 既存テストを全件通す。
  - `TextSnapshotTests.GetText_random_windows_match_substring_including_surrogate_middles`
  - `FuzzTests.DeepVerify`
  - `SnapshotIoTests`
- `TextChunk.GetSubstring` の端点について、直接のテストを追加する。

**効果**: バックアップ照合・プレビュー・検索の全文化がすべて軽くなる。

### 10.2 P-5: 全文キャッシュの共有と、件数表示の入力間引き

**(a) 全文キャッシュの共有**(挙動不変)
- 全文キャッシュ(スナップショット参照 → 文字列を 1 枠)を `MaterializedSearchStrategy` から切り離し、public の型 `SnapshotTextCache` にする。
- public にするのは、Core の InternalsVisibleTo に kxEdit.App が含まれないため。
- `SearchController` がこれを所有し、`new SnapshotSearcher(opts, cache)` で注入する。
- 破棄のきっかけは既存の `DropSearcher` と同じにする: タブ切替、タブを閉じる、ダイアログを閉じる、ビューの再生成、検索語が空。
- 代入順「text が先、snapshot が後」(`MaterializedSearchStrategy.cs:61-72`)を保つ。

**(b) 件数表示の入力間引き**(意図的な挙動変更・§3.5)
- 検索語の `TextChanged` で即 `UpdateCount` を呼ばず、200 ms の単発タイマーを再始動し、満了時に `UpdateCount` する。
- チェックボックス(大文字小文字・単語単位・正規表現・選択範囲内)の変化は、従来どおり即時に更新する。
- **次の場合は保留中のタイマーを止める**: 検索の実行(Find / Replace)、ダイアログを閉じる、タブ切替、タブを閉じる。
  - 検索を実行する経路は、自分で件数を出す(Locate)ので、保留中の更新は不要。
- タイマーは差し替えられる抽象(例: `IDebounceScheduler`)にして、テストでは即時に発火させられるようにする。
- SR への影響: 件数は画面表示だけで、発声しない(調査記録 §4 P-5)。L5 は不要。

### 10.3 P-14: 一致位置のキャッシュ

**変更**: `MaterializedSearchStrategy` の中に、スナップショット参照をキーにした一致位置の配列(`int[] starts` / `int[] lengths`)を遅延構築する。
- `Locate` / `FindPrev` / `Count` をこの配列で答える(二分探索)。
- **FindNext は置き換えない**。`Regex.Match(text, from)` は Matches の集合と一致しない(例: "aa" を "aaa" で探すと、Matches は (0,2) だけだが、`Match(text, 1)` は (1,2) を返す)。

**構築時の条件**
- Matches の単調性を検査する。単調でない場合は、配列を線形に走査する形で従来と同じ順序・同じ break 規則にする。
- 構築途中で `RegexMatchTimeoutException` が起きたら、キャッシュを確定させずに従来の経路へ戻す(例外の伝播も従来どおり)。
- 件数の上限を 1,000,000 件とし、超えたら従来の経路に戻す(配列の肥大化を防ぐ)。

**対象外**: 32M 字を超える場合の 2 戦略(`LiteralWindowSearchStrategy` / `RegexPerLineSearchStrategy`)。

**テスト**
- 旧実装を参照として残し、ランダムな正規表現とテキストで `Count` / `Locate` / `FindPrev` が一致することを確かめるプロパティテスト。
- 重なり("aa" と "aaa")、単調でない場合、タイムアウト、上限超え。
- ミューテーション検証: 二分探索の境界にスポットチェックを行う(§3.4)。

### 10.4 完了条件

- **計測**: M-5(検索語の打鍵、3MB)と F3 の連打で改善していること。
- **L5**: 不要。ただし件数表示の遅延は目視で確認する。

## 11. フェーズ 6: タブ切替とバックアップ(`perf-tab-switch`)

**目的**
- タブ切替の固定費(約 97 ms)の原因を特定して減らす。
- バックアップ照合の全文化(P-6)をなくす。

### 11.1 調査(フェーズの最初のタスク)

調査記録 §9 の P-25 では、UI スレッドが 55 ms(うちカーネル 42 ms)、正体不明の別スレッドが 35 ms で、管理コードに帰属できるのは約 21 ms だった。

**手順**
- WPR(`wpr -start CPU -start GeneralProfile` など)で、Ctrl+Tab × 40 の ETW を採る。
- 次の 4 条件で比べる。
  - NVDA あり / なし
  - windows-mcp 常駐あり / なし
  - セッション復元の設定 ON / OFF
  - 空タブ / 実ファイル
- 採った ETW は WPA または TraceEvent で集計し、別スレッドの正体とカーネル時間の内訳を特定する。

**仮説**(検証順)
1. タブページの表示切替による窓管理(SetVisibleCore / SetWindowPos の連鎖、子のスクロールバー)
2. フォーカス変更に伴う TSF / IME のコンテキスト切替
3. UIA のフォーカス変更イベントを受けるクライアント側の問い合わせ(`WM_GETOBJECT` は約 3〜4 ms/回を確認済み)
4. セッション復元 ON のときの `session-state.json` の fsync 付き書込
   - レイアウトの署名に `IsActive` が入っているので、切替のたびに必ず書く(`BackupCoordinator.cs:697-708, 752`)。
   - 背景ライタースレッドの仕事なので、「別スレッド」の候補になる。

**成果物**: 本書のこの節の末尾に「調査結果」を追記し、対処の採否を決める。原因が OS / 環境にあって kxEdit では減らせない場合は、そう記録して対処を行わない。

### 11.2 コードから明らかな冗長処理(調査結果によらず行う・挙動不変)

- **P-6**: `BackupCoordinator.DocBackup` に、最後に署名を計算したスナップショットへの **弱参照**を持たせる。
  - modified かつ参照が同じ かつ `!ForceWrite` なら、全文化もハッシュも省く。この条件では、従来も必ず `BackupAction.None` になる。
  - 不変条件: 覚えている参照 S について、常に `LastSig == sig(S)` が成り立つこと。そのため RegisterNew(`:770`)・AdoptRestored(`:459`)・Write 分岐(`:597`)の 3 箇所で S を同時に更新する。Delete 分岐では S を残してよい。
  - 全文化とハッシュの対象と、覚える参照は、同じ snapshot(`doc.Editor.CurrentBuffer.Current`)から取る。
  - 弱参照にするのは、TextBuffer の差し替え後も旧文書全体の木を保持し続けないため。
  - 起動時の復元も、タブ数の 2 乗から線形になる。
- **表示の値比較**: `UpdateTitle`(`MainForm.cs:1283-1289`)と `UpdateStatus`(`:1271-1281`)で、値が同じなら代入しない。
- **外部変更チェック**: パスのないタブでは、`BeginInvoke` を投げない(`MainForm.cs:290-295`)。即 Skipped になる処理なので、結果は同じである。

### 11.3 調査結果しだいで行うもの

- **レイアウト書込のまとめ**(仮説 4 が有力な場合・意図的な挙動差 §3.5)
  - `IsActive` だけが変わったレイアウトの即時書込をやめ、次のタイマー Reconcile か終了時の FinalFlush で書く。
  - 本文の保全に関する不変条件(`2026-07-23-unified-session-restore-design.md` §1)は本文についてのものなので、影響しない。
  - テスト `Reconcile_ActiveSwitched_RewritesLayout`(`BackupCoordinatorTests.cs:1474`)の期待値を変える必要があるため、採否はユーザーに確認する。
- **フォーカス移動の二重化**
  - WinForms の `UpdateTabSelection` が先にフォーカスを移していると、`AnnounceThenFocus` の「発声してからフォーカス」という意図が崩れている疑いがある(調査段階の未確認事項)。
  - 確認できたら a11y の不具合として別途扱う。性能の対処は、発声の順序を変えない範囲に限る。

### 11.4 テスト

- **P-6**
  - 同じスナップショットで Reconcile を繰り返したとき、`SnapshotText` を呼ばないこと(呼び出し回数のテストフック)。
  - ForceWrite のときは全文化すること。
  - 参照が変わって内容が同じなら、ハッシュ計算に進むこと。
  - 弱参照が回収された後も正しく動くこと。
  - 既存の `BackupCoordinatorTests` と `FileControllerTests.RestoreSession_*` を全件通すこと。
- **値比較**: 同じ値の再設定で `TextChanged` が発火しないこと。

### 11.5 完了条件

- **計測**: M-6(空タブ・実ファイル・未保存)の 3 条件で改善していること。調査で「減らせない」と結論した分は、その旨を記録する。
- **L5**: フォーカス移動や発声の順序に触れた場合は NVDA で確認する(タブ切替時の読み上げ)。

## 12. フェーズ 7: I/O と設定(`perf-io-settings`)

**目的**: ファイル I/O と設定保存の無駄な往復・同期書込を減らす。

**前倒しの脆弱性レビュー**を行う(パス操作・ファイル削除)。

### 12.1 P-11: 外部変更チェックのタイムスタンプ取得を 1 回にする

**現状**(`FileTimestampProvider.cs:77-125`): リモートでは、境界付きプローブの後に、UI スレッドで境界なしの `File.Exists` + `GetLastWriteTimeUtc` を再び呼ぶ。

**変更**
- プローブの work 内で `FileInfo` を 1 回だけ使い、結果を `(Reachable, Exists, LastWriteUtc, Error)` で返す。
- ローカルも `FileInfo` 1 回にする。

**等価性の要点**
- work 内の例外をすべて `Reachable=false` にしてはならない。現状は「例外 → null で、到達不能としては記憶しない」なので、これを保つために `Error` を返す。
- 1601 年のガードを保持する。
- `File.Exists` の意味論(ディレクトリは false)を保持する。`FileInfo.Exists` も同じである。

**テスト**
- `FileTimestampProviderTests` を全件通す。
- `FakeReachabilityProbe` にメンバーを追加する。

### 12.2 P-22: MRU が変わらなければ settings.json を保存しない

**変更**: `RegisterRecent`(`FileController.cs:1709-1715`)で、`RecentFilesList.Add` の前後の一覧を比べる。等しければ、保存とメニューの再構築を省く。
- 比較は `SequenceEqual(StringComparer.Ordinal)` で行う。`PathKey` で比べると、先頭項目の表記(大文字小文字など)が置き換わった変化を取りこぼす。

**影響の整理**(意図的な挙動差 §3.5)
- `MainForm.cs:572` の `LastSession = null` の永続化が遅れるのは、「クラッシュまでに設定が一度も保存されなかった」場合の窓だけである。
  - 終了時は必ず保存される。
  - 同じ性質の窓は、今も「新しいファイルを開かない限り保存されない」形で存在する。
- `--new-instance` で複数起動している場合、他インスタンスの設定を上書きする頻度が減る。

### 12.3 P-12: バックアップ JSON の Encoder

**変更**: `BackupStore.cs:20` の Options に `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping` を足す(意図的な挙動差 §3.5)。
- 前例: 本文を保存する `LastSessionBuffersStore` は、すでに同じ設定である。
- 読込は新旧どちらの形式も可能(Encoder は書込にしか効かない)。
- セキュリティ: `%APPDATA%` のローカルファイルで、HTML や JS に埋め込まれないので問題ない。脆弱性レビューで確認する。

**対象外**: SettingsStore。バイト列を固定したテスト(`SettingsStoreTests.cs:630`)があり、変更しないことが既定の方針である。

**テスト**
- 日本語・絵文字・制御文字・`"`・`\` の往復。
- 単独サロゲートを含む文字列の往復(Encoder によって出力が変わりうるため)。
- 旧形式のファイルを読めること。

### 12.4 P-15: PreviewUserDataSweeper の順序

**変更**: 先に `preview-*` ディレクトリを列挙し、1 件以上あるときだけ `IsSoleInstance`(全プロセスの列挙)を呼ぶ。
- ディレクトリがなければ結果は同じである。TOCTOU の窓の性質も変わらない。
- `Program.cs` での呼び出し位置と、IL テスト(`MainFormSmokeTests.cs:1590-1662`)の目印 `SweepIfSoleInstance` は変えない。

### 12.5 P-21(a): プレビューを閉じるときの削除を背景で行う

**変更**: `PreviewUserDataFolder.Dispose` の `Directory.Delete(recursive)` を背景タスクに移す。
- WebView2 のブラウザプロセスは非同期に終了するので、短い間隔のリトライを数回行う。
- 最終的に失敗したら、従来どおり Trace に残し、次回起動の sweeper に任せる。
- アプリ終了時に背景タスクが打ち切られた場合も、sweeper が回収する。

**脆弱性レビューの観点**: 削除対象のパスが常に自分で作ったフォルダーであること(リパースポイントを辿らないこと)を維持する。

**テスト**: `PreviewUserDataFolderTests` を、非同期削除の完了を待てる形に直す。

### 12.6 完了条件

- **計測**: リモート(SMB 共有)が用意できれば、M-6 のリモート版とウィンドウ復帰の時間を測る。用意できなければ、Win32 の呼び出し回数をテストで確認することで代える。
- **L5**: 不要。

## 13. フェーズ 8: grep(`perf-grep`)

**目的**: バイナリと、一致しないファイルに対する無駄な処理をなくす。

**前倒しの脆弱性レビュー**を行う(外部ファイルのパース)。

### 13.1 変更

1. **NUL 判定を先に行う**
   - `GrepService.cs:98-103` の順序を `ReadAllBytes → ContainsNul → Detect` にする。
   - NUL でスキップする枝では、`Detect` の結果を使っていない。`Detect` の副作用も冪等な `EnsureRegistered` だけなので、厳密に等価である。
2. **grep では改行コード判定を省く**
   - `DecodeBytes` の中の `LineEndingDetector.Detect`(`TextFileService.cs:277`)は、grep では使わないのに全文を走査している。改行コード判定をしないデコードの経路を足す。
3. **リテラル検索の全文プリフィルタ**
   - `UseRegex=false` のとき、同じ Regex(`Regex.Escape` と、単語単位なら `\b`)の `IsMatch` を全文にかけ、false ならそのファイルの行分割を丸ごと省く。
   - `IndexOf` で代用しないこと(大文字小文字の扱いがずれるため)。
   - **正規表現モードには適用しない**。`^` / `$`、行の境界に接する先読み・後読み、タイムアウトの意味の違いで偽陰性が出るため。
   - タイムアウトの意味は「全文単位」に変わる(意図的な挙動差 §3.5)。リテラルなので、破局的なバックトラックは起きない。
4. **行ごとの Substring をやめる**
   - `CollectLineHits`(`:145-184`)の `Substring` を、span 上の `Regex.EnumerateMatches(ReadOnlySpan<char>)` に置き換える。一致した行だけ `LineText` を作る。
   - span 入力では、アンカーや後読みが範囲の外を見ないので、Substring と同じ意味になる。計画の段階で等価性テストを先に書いて確かめる。

### 13.2 テスト

- `GrepServiceTests` を全件通す。
- 途中の行の `^` / `$` の一致(既存の `Regex_line_anchors_match_line_boundaries` は 1 行目と最終行しか見ていない)。
- 行の境界に接する先読み・後読み。
- バイナリの判定。
- リテラルと正規表現のそれぞれで、プリフィルタのあり・なしの結果が一致すること。

### 13.3 完了条件

- **計測**: bin/obj を含むリポジトリを grep した所要時間(ハーネスに grep のシナリオを足すか、手動で計測する)。
- **L5**: 不要。

## 14. フェーズ 9: 部分再描画とスクロール(`perf-partial-paint`)

**目的**
- 打鍵・IME・選択・現在行強調の変化・スクロールで、変わった行だけを描く。

**依存**: フェーズ 1 と 3(`FrameInputs` の seam を使う)

**規模**: 2 セッション(9a / 9b)に分けてよい。

### 14.1 9a: クリップ対応の描画と差分の無効化

**クリップ対応の描画**
- `FrameBuilder.Build` に、可視行の範囲を指定する引数を足す。`OnPaint` は `e.ClipRectangle` に交差する行だけを作って描く。
- 行の高さは一定(`LineHeightPx`)なので、行 i は `[i·LH, (i+1)·LH)` を占める。
- **例外 1**: セル強調枠の下辺は次の行の先頭ピクセルに引かれる(`FrameBuilder.cs:308-309`)。そのため範囲を 1 行上へ広げる。
- **例外 2**: IME の overlay はクリップなしで右へはみ出す。縦は行の帯に収まるが、太字フォントのディセンダが帯を越えないかは実機で確認する。
- `ViewportLayout.Build` は、全可視行ぶん作るコストが残る。行で絞るかどうかは計測を見て判断する。

**差分の無効化**
- `FrameInputs` の差から、無効化する行の矩形を求める。

| 変化 | 無効化する行 |
|---|---|
| 現在行強調の移動 | 旧行と新行の全視覚行 |
| 選択の変化 | 旧範囲と新範囲の対称差にかかる行 |
| IME 未確定の更新 | 未確定文字列の行(前回の行を含む) |
| 1 論理行の中の編集で、行数・行番号幅・hscroll の表示が変わらない場合 | その論理行の視覚行 |

- それ以外は全面にする: 行数の変化、行番号幅の変化、`_scrollX` の変化、折り返し ON での段落の組み替え、外観の変化など。
- `Invalidate(Rectangle.Empty)` は全面の Invalidate に化けるので、空の差分は呼ばずに捨てる。

**テスト**
- `InvalidateEventArgs.InvalidRect` で、無効化された矩形を検証する。
- クリップ版の op 列が「全体ビルドの op 列を行で絞ったもの」と一致することを確かめる(Core の純関数テスト)。

### 14.2 9b: ScrollWindowEx

**変更**: `TopLine` / `SetTopPosition` / `ScrollX` のセッターで、移動量が可視行数未満かつ未処理の無効領域がない場合(`GetUpdateRect` で確認)に `ScrollWindowEx` で既存の画素を移し、露出した帯だけを無効化する。
- スクロール範囲は、スクロールバーの子ウィンドウを除いた `paintWidth × PaintHeightPx` にする。
- 途中で切れていた旧最下行も無効化する。
- PageUp / PageDown のように可視行数以上動く場合は、全面にする。

**実機で確認すること**
- システムキャレットの補正が二重にかからないこと(セッターは先に `PositionCaret` している)。
- 兄弟コントロールの CSV セル編集用 TextBox が重なった状態。
- スクロールと同時に起きる選択・現在行の変化の無効化。

**全面のまま残すもの**: スクロール位置を直接代入するクランプ(`UpdateVerticalScrollbar` など)、ReplaceSource、ApplyAppearance、WrapColumns。

### 14.3 完了条件

- **計測**: S3(打鍵)・S4(IME)・S6(スクロール)が改善していること。
- **L5(目視が重い)**: 描画が古いまま残らないことを、次の組み合わせで確かめる。
  - テーマ 2 種 × 折り返し ON/OFF × 行番号 ON/OFF × 現在行強調 ON/OFF × 空白表示
  - 操作: 打鍵、IME、選択のドラッグ、ホイール、スクロールバーのドラッグ、PageDown、水平スクロール、CSV のセル強調と F2
  - l5-checklist を作って PrintWindow で判定する。
- NVDA のハイライト矩形も確認する。

## 15. フェーズ 10: 折り返し ON の UIA(`perf-uia-wrap-cache`)

**目的**: 折り返し ON のとき、行単位の UIA 操作で毎回 UI スレッドへ同期 Invoke しない。

**依存**: フェーズ 2

### 15.1 変更

- `_lastLineSegs`(nullable の構造体タプル)を、不変クラス `LineSegsCache(Snap, Line, Wrap, Metrics, WrapSegment[] Segs)` の volatile 参照に替える。
- RPC スレッドの手順:
  1. **Handle のガードを、キャッシュを見るより前に維持する**(teardown 時に null を返す挙動を保つため)。
  2. キーが一致すれば、`VisualSegments.FindContaining` で即答する。
  3. 一致しなければ、従来どおり Invoke する。
- ミスした場合に RPC スレッドで計算することはできない。`GdiCharMetrics.MeasureRun` の非 ASCII 経路が、非スレッドセーフな Dictionary と GDI を使うためである。
- **無効化**: `ApplyAppearance` の**先頭と末尾の両方**で無効化する。現在は末尾だけなので、途中にヒットすると古い分割を返す窓がある。先頭でも無効化すれば、「ApplyAppearance より前の時点の答え」として線形化できる。
- **任意**: キャッシュを数件の LRU にする。say all の「次の行」に効く。計画の段階で判断する。

### 15.2 テスト・完了条件

- 既存の `EditorControlCacheTests` / `UiaTextHostAdapterTests` を全件通す。
- ヒット時に Invoke が走らないこと(Invoke の回数を数えるテストフック)。
- `TestHook_LastLineSegs*Count` は、RPC スレッドから加算されることになるので `Interlocked` にする。
- **L5 必須**: 折り返し ON で、NVDA の上下矢印と say all の読み上げが不変であること。

## 16. フェーズ 11: 起動・生成(`perf-startup`)

### 16.1 P-7: ReadyToRun

**変更**: `release.yml` の publish に `-p:PublishReadyToRun=true` を足す(意図的な挙動差 §3.5: zip +2.3MB)。

**確認すること**
- `-warnaserror` のもとで crossgen が警告を出さないこと。ローカルで同じ publish を実行して確かめる。
- 配布物の起動と、WebView2Loader.dll が同梱されていること(publish の出力から取る。手でコピーしない)。

**CI**: ci.yml と pre-merge は publish しないので、手順を README の「リリース」に追記する。

### 16.2 P-16(前半): 同じフォントなら作り直さない

**変更**: `ApplyAppearance`(`EditorControl.cs:2701-2776`)で、フォント名・サイズ・スタイルが現在と同じなら、フォント 3 個と `GdiCharMetrics` を作り直さない。
- これで設定変更(`MainForm.cs:1441-1445`)のたびに全タブの幅メモを捨てることがなくなる。
- テーマなど他の外観は、従来どおり毎回適用する。

**対象外**
- ctor の既定フォントの生成(半角の "MS ゴシック" で Microsoft Sans Serif に解決される既知の件)。231 箇所のテストのピクセル値に影響しうるので、別件として起票する。
- フォントと幅メモのタブ間共有(§17 の保留)。

### 16.3 完了条件

- **計測**: M-1(起動)と、設定変更の適用時間。
- 起動の確認: 配布 zip を展開して起動し、プレビューを表示する。

## 17. フェーズ 12: プレビュー環境の使い回し(`perf-preview-env`)

**目的**: Markdown プレビューを開くたびに WebView2 の環境とプロファイルを新規作成するコスト(推定 0.5〜2 秒)を減らす。

**進め方**: **評価が先。不採用で終わることもある**。

### 17.1 評価(最初のタスク・脆弱性レビュー必須)

- 現在のセキュリティ設計を整理する。
  - MD-M-4: フォームごとに一意のプロファイルを作り、破棄で消す。
  - 仮想ホストのマッピング、CSP、ナビゲーション制限(`2026-09-03-preview-csp-virtual-host-design.md` など)。
- 使い回すと、プロセス内の複数のプレビューの間で次の状態を共有することになる。この共有による脅威を評価する。
  - HTTP キャッシュ
  - localStorage / IndexedDB
  - Service Worker
  - Cookie
  - 仮想ホストのマッピング
  - 例: 悪意ある Markdown の文書 A が、後で開く文書 B のプレビューに影響する。
- 選択肢を比べる。
  - (i) 環境もプロファイルもプロセス単位で使い回す。
  - (ii) 環境は使い回し、開くたびにプロファイルの閲覧データを消す。
  - (iii) 現状維持(ブラウザプロセスのウォームアップだけ行う、など)。
- まず実測で、開く時間の内訳(環境作成・プロセス起動・描画)を取る。

**成果物**: 本節の末尾に評価結果を追記する。採用するならその案の設計を追記し、ユーザーの承認を得てから実装する。

### 17.2 完了条件

- 評価結果と採否の記録。
- 採用した場合は、脆弱性レビューの 2 パス、L5(プレビューの表示・リンク・画像・SR での読み上げ)、M-1 相当の計測。

## 18. 保留(理由と再開条件)

| 指摘 | 保留の理由 | 再開条件 |
|---|---|---|
| P-18 JSON のソース生成 | 起動全体が 213 ms で、ReadyToRun の効果も −13 ms。得られる分が小さい | 起動時間が問題になったとき(遅い PC での実測など) |
| P-19 空白表示の計測 | 空白ごとの prefix 計測をやめると、描画結果が変わる(非加算性) | 空白表示 ON の利用者から遅さの報告があったとき |
| P-23 CSV の差分パース | 規模が大きく、効くのは数 MB の CSV の F2 確定だけ | CSV モードの利用が増え、遅さが報告されたとき |
| P-16(後半)フォントと幅メモのタブ間共有 | 各エディタが `_font` を Dispose するので、所有権の再設計が要る | タブ生成の遅さが問題になったとき |
| P-17(後半)`BringCaretIntoView` の二重呼び出し | コストが小さく、既知の申し送り(`Caret.cs:393-407`)で扱っている | その申し送りを回収するとき |
| grep の並列化と結果一覧の一括追加 | キャンセル・進捗・エラー順の設計が要る | フェーズ 8 の後も grep が遅いとき |
