# 通常利用における性能調査 記録

策定日: 2026-09-24 / 調査ブランチ: `feature/general-perf-audit`

## 1. 背景・目的・範囲

kxEdit 全体で、性能を改善できる箇所を洗い出す。後続の改善タスクの着手順と単位を決める材料にする。

**対象**: 一般的な利用。
- 数 KB〜数 MB・〜10 万行程度のファイル
- 数枚のタブ
- 打鍵、IME 入力、キャレット移動、選択、スクロール、タブ切替、検索、grep、起動と終了
- SR(UIA)経由の読み上げ

**対象外**: 巨大ファイル(500MB〜1GB 級)を開いたときの性能。長大行の既知課題は
`2026-08-02-large-line-resilience-design.md` / `2026-08-02-large-line-wrap-perf-design.md` の申し送りで扱う。

本調査では `src/` を 1 行も変更していない。

## 2. 方法

- コードを読むだけの静的調査として、次の 4 経路を並列に調べた。
  1. Editor の入力・レイアウト・描画
  2. UIA / SR
  3. App 層の編集・タイマー・タブ切替
  4. 起動・ファイル I/O・検索・grep
- 各経路の指摘は、主担当が該当行を読んで照合した。
  - §4 の「確度」欄の **照合済** は、コード上の挙動を確認したという意味である。コストは実測していない。
  - **報告** は、調査担当の読解のみで、主担当は照合していない。
- **§4 の ms 値はすべて推定である。** 実測値として参照できるのは次の 1 つだけ。
  - `GdiBench`(900×700・MS ゴシック 12pt・可視 33 行・画面内で実描画)の **34 ms/フレーム**
  - 出典: `2026-08-02-large-line-wrap-perf-design.md` §9.7
  - 1 行あたり: ASCII 約 0.3 ms / CJK 約 1.0 ms / ASCII と CJK が交互に並ぶ行 4.4 ms
- 推定を実測で確かめる計画は §8 に置く。

## 3. 結論

**土台のデータ構造は十分に速い。**
- ピース木の行⇔オフセット変換は O(log n)。
- UIA へのスナップショット公開は O(1)。
- 打鍵ごとに文書全体を走査する処理は、App 層にも UIA 層にもない。

**改善余地は主に次の 3 つに集中している。**
1. **描画の回数と、1 行あたりの GDI 計測回数**(P-1〜P-4)
   - キャレット移動や IME の 1 打鍵でも全面を再描画している。
   - 非 ASCII を含む行の幅は、毎回 GDI で測り直している。
   - 日本語文書の 1 フレームは実測 34 ms で、60fps の予算 16 ms を超えている。
2. **一部の操作で全文を文字列化し直していること**(P-5・P-6)
   - 検索語の打鍵
   - タブ切替時のバックアップ照合
3. **起動時の JIT**(P-7)
   - 配布物が ReadyToRun になっていない。

## 4. 指摘一覧

凡例:
- **頻度**: どの操作で何回走るか。
- **推定コスト**: 未実測。
- **L5**: 改善したときに実機検証が要るか(CLAUDE.md §5)。

### 4.1 日常操作で体感に効くもの

#### P-1 表示が変わらない操作でも全面を再描画する(描画はクリップ領域を見ない)

- **場所**
  - `EditorControl.Caret.cs:170` / `:214` / `:246` / `:281`(キャレット移動・選択の各経路の末尾で `Invalidate()`)
  - `ImeController.cs:74` / `:342`(IME の未確定更新)
  - `EditorControl.Paint.cs:17-`(`OnPaint` は `e.ClipRectangle` を見ずに全可視行を Build して描く)
- **何が起きるか**
  - 既定設定(折り返し OFF・現在行強調 OFF・選択なし)で矢印キーを押しても、フレームの内容は 1 ピクセルも変わらない。キャレットは OS のシステムキャレットだからである。
  - それでも毎回、全可視行の GDI 計測と描画が走る。
  - IME の未確定更新で変わるのはキャレット行だけだが、全面を描き直す。
- **頻度**: 矢印キー 1 回ごと、IME の 1 打鍵ごと、文字入力 1 回ごと
- **推定コスト**: 日本語文書で 1 回 34 ms(900×700 の実測)〜約 55 ms(最大化時の推定)。ASCII は約 10〜18 ms。
- **確度**: 照合済
- **改善案**
  - (a) 選択の前後・現在行強調の対象行・IME 状態・スクロール位置がいずれも変わらないときは、`Invalidate` を省略する(小規模)。
    - 前提として、UIA のスクリーン座標キャッシュの更新契機を見直す必要がある(§6 S-1)。現状は `OnPaint` 末尾と `OnBoundsChanged` でしか更新されない。
  - (b) FrameBuilder と RenderFrame が、クリップ領域に交差する行だけを処理するようにする。変化した行だけを `Invalidate(rect)` する(中規模)。
- **L5**: 必須。目視(描画が古いまま残らないか)と SR(座標)の両方を確認する。

#### P-2 非 ASCII を含む行の幅を、描画のたびに GDI で測り直す

- **場所**: `GdiCharMetrics.cs:85-92`
  - 1 コードポイントの幅はメモ化されている(2026-08-02 変更 A)。
  - 複数コードポイントの run に非 ASCII が 1 文字でも含まれると、`text.ToString()` の後に `TextRenderer.MeasureText` を毎回呼ぶ。
- **呼び出し元**
  - `FrameBuilder.cs:467`(各行 × 描画ごと)
  - `EditorControl.cs:1551`(`UpdateHorizontalScrollbar`: 各行 × 打鍵ごと)
  - `PixelMapper.OffsetToPx`(キャレット X・選択矩形)
- **頻度**: 描画のたびに全可視行
- **推定コスト**: 日本語文書の 1 フレームの 3〜5 割(未計測)
- **確度**: 照合済
- **改善案**: 文字列の内容をキーにした上限付きのメモを追加する(例: 1024 件・1 件 4K 文字まで・溢れたら全消去)。
  - 返す値は `MeasureText` の結果そのものなので、完全に同一になる。
  - キャッシュの寿命はフォントの寿命と一致させる(既存の幅メモと同じ規約)。
  - 小規模・低リスク。
- **L5**: 目視の確認を推奨する。

#### P-3 打鍵・貼り付けしたテキストへの文字アクセスが、ブロック内の位置に比例して重くなる(既知の申し送り F-6)

- **場所**
  - `AppendBuffer.cs:25`: 格子は `gridBytes: BlockBytes` で先頭 1 点だけ。コメントのとおり、書き込み中のブロックを細かい格子で包めない事情による。
  - `TextChunk.cs` の `CumAt` / `CharToByte` / `NthBreakEndChar`: ブロック先頭から線形に走査する。
- **何が起きるか**
  - F-6 は 1 回あたり最大 40〜80 µs と実測されている(`2026-08-02-large-line-resilience-design.md` §2.2)。当時は「単独では割に合わない」と判断された。
  - 今回の新しい観察は、**描画経路が 1 打鍵あたりこれを数百回呼ぶ**ことである。1 行あたり `GetLineStart` / `GetLineEnd` / `GetText` で約 6 回になり、`UpdateHorizontalScrollbar` と `OnPaint` の両方で可視行ぶん走る。
- **頻度**: 新規文書に長文を書く、32KB 以下の貼り付けを重ねる、といった普通の使い方で起きる。遅延はブロックが 64KB 埋まるたびにリセットされる、のこぎり型になる。
- **推定コスト**: ブロック内の位置が 30〜60KB のとき、1 打鍵あたり 10〜40 ms の上乗せ
  - 状況証拠: `p2-editor-control` 計画書の「splice/typing 後の 2 万ピース木で 32 ms/frame」
- **確度**: 構造は照合済。量は推定。
- **改善案**: `_pos` が 4KB 境界を越えるたびに、書き込み済みの範囲だけを格子化した新しい `TextChunk` でブロックを包み直す。
  - チャンクは生成後に不変なので、RPC スレッドから読んでも安全性は変わらない。
  - 中規模・中リスク。char⇔byte 対応の中核なので、等価性テストを厚くする。
  - **先に計測が必要**(§8 M-3)。
- **L5**: SR の読み上げ位置に影響しうるため、必要。

#### P-4 スクロールでも全面を再描画する(ScrollWindowEx を使っていない)

- **場所**: `TopLine` / `SetTopPosition` / `ScrollX` の setter(`EditorControl.cs:873` / `:904` / `:972` の `Invalidate()`)。`src/` に `ScrollWindow` の使用はない。
- **頻度**: ホイール 1 ノッチ、スクロールバーのドラッグ、キャレット追従のたび
- **推定コスト**: 1 回 34 ms 以上(P-1 と同じ)。日本語文書ではスクロールがカクつく。
- **確度**: 照合済
- **改善案**: P-1(b) の上に ScrollWindowEx を載せ、新しく露出した行だけを描く(中規模)。
- **L5**: 目視が必須。

#### P-5 検索ダイアログで検索語を 1 文字打つたびに、全文の文字列を作り直す

- **場所**
  - `FindReplaceDialog` の `_pattern.TextChanged` → `UpdateCount`
  - `SearchController.cs:143-147`: 条件が変わると `new SnapshotSearcher`
  - `MaterializedSearchStrategy.cs:61-72`: 全文キャッシュが searcher ごとにある
- **何が起きるか**: 検索語が変わると searcher ごと作り直されるため、全文キャッシュも捨てられる。その結果、1 打鍵ごとに UI スレッドで全文の UTF-8 デコードと大きな文字列の確保が起き、そのうえで全件を数える。
- **頻度**: 検索語の 1 打鍵ごと
- **推定コスト**: 数 MB の文書で 1 打鍵あたり 10〜30 ms。LOH への確保が続き、Gen2 GC も起きやすくなる。
- **確度**: 照合済
- **改善案**: 全文キャッシュ(スナップショットの参照が同一かで判定)を searcher から切り離し、`SearchController` 側で持って新しい searcher へ渡す。
  - 破棄のきっかけは既存の `DropSearcher` と同じにする。
  - 小規模・挙動不変。
  - 件数を数える処理そのものは残る。正規表現モードでは最長 1 秒のタイムアウト待ちもありうる。入力の間引き(debounce)は件数表示のタイミングを変えるので、別途判断する。
- **L5**: 不要(検索件数は画面表示のみ)。

#### P-6 タブ切替のたびに、未保存タブ全部を全文化してハッシュを取る

- **場所**
  - `BackupCoordinator.cs:168`: `ActiveDocumentChanged` → `Reconcile`
  - `BackupCoordinator.cs:588-589`: 未保存の全文書に `SnapshotText` と `ContentSignature.Of`
- **何が起きるか**
  - 内容が前回と同じなら `BackupPlanner.Decide` は何もしないと判定する。しかし全文化とハッシュ計算は、その判定の前に毎回走る。
  - 起動時の復元では、タブを 1 枚作るたびに `ActiveDocumentChanged` が起きるので、全文化の回数は復元タブ数の 2 乗に比例する。
- **頻度**: Ctrl+Tab・タブのクリック・ファイルを開く・新規作成、起動時の復元
- **推定コスト**: 1〜2MB 級の未保存タブ 1 枚につき 5〜10 ms。復元 10 枚なら数百 ms。
- **確度**: 照合済
- **改善案**: `DocBackup` に、最後に署名を計算したスナップショットの参照を持たせる。参照が同一で、かつ強制書込でなければ、全文化もハッシュも省く。
  - スナップショットは不変なので、結果は同一になる。
  - 同じ手法は `Document.ParseCsv` と `MaterializedSearchStrategy` で既に使われている。
  - 小規模だが、データ保護の中核なので §3 のフローに乗せる。
- **L5**: 不要。

### 4.2 起動・grep・特定条件で効くもの

#### P-7 配布物が ReadyToRun になっていない

- **場所**: `.github/workflows/release.yml:92` の publish(`-r win-x64 --self-contained false`)。csproj にも `PublishReadyToRun` / `TieredPGO` などの指定はない。
- **何が起きるか**: 自前の DLL と Markdig・UtfUnknown が、起動のたびに JIT される。単一インスタンスで既存の窓へ引き渡すだけの 2 つ目のプロセスも、同じコストを払う。
- **推定コスト**: コールド起動で 100〜300 ms
- **確度**: 照合済(設定がないこと)。効果は推定。
- **改善案**: publish に `-p:PublishReadyToRun=true` を足す。
  - 定常時の性能は変わらない。TieredCompilation / TieredPGO は .NET 9 の既定で有効。
  - 配布 zip は 1〜2MB 増える。
  - `-warnaserror` のもとで crossgen が警告を出さないかは要確認。
  - InvariantGlobalization・トリミング・単一ファイル化は採らない。前者は挙動が変わり、後の 2 つは WinForms が非対応か起動に効かない。
- **L5**: 起動確認のみ。

#### P-8 grep がバイナリ判定より前に、ファイル全体へ文字コード判定をかけている

- **場所**: `GrepService.cs:98-103`。`File.ReadAllBytes` → `EncodingDetector.Detect(bytes)` → `ContainsNul` の順。
- **何が起きるか**: バイナリファイルも、厳格 UTF-8 判定が失敗した後に `CharsetDetector.DetectFromBytes` がファイル全体(上限 64MB)を走査する。そのあとでようやく NUL で読み飛ばされる。ファイルを開く経路は先頭 64KB だけで判定しているが、grep は全体を渡している。
- **頻度**: grep 1 回につき、対象のバイナリファイル数だけ
- **推定コスト**: bin/obj・画像・zip を含むフォルダーでは grep 時間の大半と推定
- **確度**: 順序は照合済。コストは推定。
- **改善案**
  - `ContainsNul` の判定を `Detect` の前へ移す。`Detect` は副作用のない関数なので、挙動は完全に不変。数行の変更。
  - 追加案: 非正規表現の検索で、全文に対する `IsMatch` が false ならそのファイルの行分割を丸ごと省く。

#### P-9 UIA の `GetBoundingRectangles` に複数行の範囲を渡すと、O(範囲の行数 × 可視行数) の処理を UI スレッドで行う

- **場所**
  - `UiaTextHostAdapter.cs:590-639`(`ComputeBoundingRectangles` のループ 612-637)
  - `EditorControl.cs:2573-2605`(`ComputeCaretPoint` の、TopLine からの積み上げループ)
- **何が起きるか**
  - 範囲の各論理行について `ComputeCaretPoint` を 2 回呼ぶ。
  - `ComputeCaretPoint` は、`_topLine` から対象行まで各行を `LineTextOf` で文字列化しながら数える。
  - 可視域より下の行は不可視と分かるまでに約 V 行(V = 可視行数)を取り出す。
  - 上限は `safety` の 10 万反復だけである。
- **推定コスト**: 1 万行の全選択で 2〜4 秒 UI スレッドが止まる。その間、SR の RPC スレッドも `Invoke` で待たされる。
  - 設計書 `2026-07-06-p5-uia-connect-design.md` の目標は「任意範囲で 1 ms 未満/回」。
- **発火条件**: 非退化の範囲(選択)に対してクライアントが矩形を問うとき。拡大鏡・ナレーターのハイライト・検査ツールが候補だが、どれも推測で、実機で要確認。
- **確度**: 照合済
- **改善案**
  - (a) 範囲が可視域より上にある部分は `GetLineStart(_topLine)` まで一気に飛ばす。
  - (b) `line > _topLine` で不可視になった時点で `break` する。以降の行の y は単調増加なので、必ず不可視になる。
  - (c) 折り返し OFF の `ComputeCaretPoint` では、積み上げを `logicalLine - _topLine` で即答する(厳密に等価)。
    - (c) は `PositionCaret` / `BringCaretIntoView` 経由で毎打鍵走る経路にも効く。
  - 唯一の差分: 従来は「可視域が範囲先頭から 10 万行より先にある」と空配列を返していたが、改善後は矩形が返る(実質的な修正)。PR に記載する。
- **L5**: 必要(SR 経路)。`tools/sr-regression.ps1` も実行する。

#### P-10 折り返し ON のとき、行単位の UIA 操作 1 回ごとに UI スレッドへ同期 Invoke する

- **場所**: `UiaTextHostAdapter.cs:424-451`(`TryFindVisualSegment`)。キャッシュ `_lastLineSegs`(`:71`)は UI スレッドでしか読めないため、キャッシュにヒットしても毎回 Invoke する。
- **頻度**: 折り返し ON のときの `ExpandToEnclosingUnit(Line)` は 2 回、`Move(Line)` は 3 回。NVDA の上下矢印や say all の 1 行ごとに起きる。
- **推定コスト**: UI がアイドルなら 1 往復数十〜数百 µs。say all では描画処理の後ろに並ぶので、数 ms〜数十 ms 待つ可能性がある。既定の折り返し OFF ではコストゼロ(`:427` で即 return)。
- **確度**: 照合済
- **改善案**: キャッシュを不変オブジェクトにし、volatile な参照として公開する。キーはスナップショット・行・折り返し桁・metrics 世代。RPC スレッドはヒットならそのまま答え、Invoke は未ヒット時だけにする(中規模)。
- **L5**: 必須。

#### P-11 リモートのファイルでは、ウィンドウに戻るたびに UI スレッドでネットワーク往復が約 4 回起きる

- **場所**: `FileTimestampProvider.cs:95-112`
  - 期限付きプローブ(`File.Exists` + `Directory.Exists`)の後に、UI スレッドで上限なしの `File.Exists` + `File.GetLastWriteTimeUtc` を再び実行する。
  - 呼び出し元は `MainForm.OnActivated` とタブ切替。
- **推定コスト**: VPN 越しの往復が 50 ms なら、復帰のたびに約 200 ms。ローカルでは問題にならない。
- **確度**: 照合済
- **改善案**: プローブのワーカー内で `FileInfo` を 1 回だけ使い、(到達できるか・存在するか・更新時刻)をまとめて返す(小規模)。

#### P-12 バックアップ JSON が、非 ASCII を `\uXXXX` にエスケープしている

- **場所**: `BackupStore.cs:20`(`JsonSerializerOptions` にエンコーダーの指定がない)。`SessionLayoutStore.cs:18` は `UnsafeRelaxedJsonEscaping` を指定している。
- **影響**: 日本語の本文はファイル上で約 2 倍になる。書込は背景スレッドだが、終了時の最終書込待ち(UI スレッドで最大 5 秒)が長くなる。
- **確度**: 照合済
- **改善案**: `SessionLayoutStore` と同じエンコーダーにする。読み込みは新旧どちらの形式でも互換だが、ディスク上のバイト列は変わるので PR に明記する(小規模)。

### 4.3 小さな改善(まとめて扱う候補)

| ID | 内容 | 場所 | 確度 |
|---|---|---|---|
| P-13 | `TextSnapshot.GetText` が、ピースごとの一時文字列 → StringBuilder → `ToString` と、全文を 3 回コピーする。`string.Create` で 1 回にできる。P-5・P-6・プレビューの全文化が軽くなる | `TextSnapshot.cs:39-54` / `:259-281` | 照合済 |
| P-14 | 「N 件中 M 件目」のために、F3 のたびに全件を列挙する。一致位置をスナップショット単位でキャッシュし、二分探索にする | `SearchController.cs:255` / `:471`、`TextSearcher.cs:100` | 照合済 |
| P-15 | 起動時に、掃除対象の有無を見る前に OS の全プロセスを列挙する。先に `preview-*` を列挙し、1 件以上のときだけプロセス判定する | `PreviewUserDataSweeper.cs:53` / `:65-78` | 照合済 |
| P-16 | EditorControl の ctor で作るフォント 3 個と文字幅表(`MeasureText` 129 回)を、直後の `ApplyAppearance` で作り直す。非 ASCII の幅メモもタブごとに別で、フォント単位でタブ間共有できる | `EditorControl.cs:151-154` | 照合済 |
| P-17 | ナビゲーションキーで `BringCaretIntoView` が 2 回呼ばれる(`SetCaretCharOffset` 内と `ApplyNavMove`) | `InputRouter.cs:149` | 照合済 |
| P-18 | System.Text.Json をリフレクション経由で使う。起動時の初回逆シリアル化で 20〜60 ms と推定。ソース生成(`JsonSerializerContext`)にできる | `SettingsStore.cs` / `SessionLayoutStore.cs` / `BackupStore.cs` | 報告 |
| P-19 | 空白表示が ON のとき、非 ASCII 行では空白 1 つごとに、行頭からの接頭辞を GDI で測る | `FrameBuilder.cs:408` | 照合済 |
| P-20 | `OptimizedDoubleBuffer` が、既定の `MaximumBuffer`(225×96)を超えるため描画ごとにバックバッファを一時確保している疑い | `EditorControl` の ControlStyles | 報告(要計測) |
| P-21 | Markdown プレビューを開くたびに、WebView2 の環境とプロファイルを新規作成する。閉じるときは UI スレッドで再帰削除する。削除の背景化は低リスク。環境の使い回しはセキュリティ設計(MD-M-4)の変更になる | `MarkdownPreviewForm.cs:83-86`、`PreviewUserDataFolder.cs:70-73` | 報告 |
| P-22 | 最近使ったファイルの登録のたびに、`settings.json` を UI スレッドで fsync 付き書込する。grep から既に開いているタブへ飛ぶときも書く | `FileController.cs:1709-1715`、`SettingsStore.cs:389` | 報告 |
| P-23 | CSV で F2 を確定するたびに、全体を再解析して全フィールドの文字列を確保する | `CsvController.cs:411` | 報告 |

## 5. すでに十分に最適化されている点(再提案しない)

- **バッファ**: 永続 AVL のピース木。`CharLength` / `LineCount` は O(1)。行⇔オフセット変換は O(log n) と 4KB 格子 1 マス。連続した打鍵はピースを結合し、Undo もまとめる。
- **レイアウト**: `ViewportLayout` は可視行だけを処理する。Wrap は必要な位置で打ち切る(`WrapFirstSegments` / `WrapThroughOffset`)。`SegmentCountCapped` / `LocateVisualRow` には折り返し OFF の短絡がある。
- **描画の仕組み**: キャレットの点滅は OS のキャレットで、再描画しない。`Invalidate` は非同期で、WM_PAINT はまとめられる(`Update` / `Refresh` は使わない)。
- **UIA**
  - スナップショットは参照の差し替えで公開する。
  - 読み取り系(`GetText`・文字・単語・折り返し OFF の行)は RPC スレッドで直接答える。
  - 書き込み系(`Select` / `SetFocus` / `ScrollIntoView`)は `BeginInvoke` で、RPC スレッドを待たせない。
  - イベントはリスナーがいるときだけ発火する。1 打鍵あたり TextChanged 1 回と SelectionChanged 1 回。
  - 単語の走査には上限 128 がある。
- **App 層**
  - Editor にタイマーはなく、`Application.Idle` の購読もない。常駐タイマーはバックアップ用(既定 300 秒)だけ。
  - ステータスバーの行・桁は O(log n)。
  - メニューの有効/無効は `DropDownOpening` のときだけ更新する。
  - タイトルは dirty の切り替わりのときだけ更新する。
  - CSV の解析結果はスナップショットの参照で覚える。
- **I/O**
  - 文字コード判定は先頭 64KB だけ。UTF-8 の検査は `Utf8.IsValid` でベクトル化されている。
  - 保存はチャンク単位のストリーム書込で、改行が既に統一されていれば変換しない。
- **検索**: 同一条件の間は searcher と全文を再利用する。全置換は 1 回の走査と 1 回の編集で済む。`RegexOptions.Compiled` を使わないのは妥当。
- **バックアップ**: ディスク書込は背景の直列ライターが行う。保存済みの文書は全文化しない。
- **起動**: WebView2 はプレビューを開いたときだけ初期化する。単一インスタンスのパイプ待受は非同期。

## 6. 性能以外の副次発見(未検証)

- **S-1** UIA のスクリーン座標キャッシュ(`_clientToScreenX/Y` と `_bounds`)は、次の 2 つの契機でしか更新されない。
  - `OnPaint` 末尾(`UiaTextHostAdapter.RefreshClientToScreenOrigin`)
  - `OnBoundsChanged`(HandleCreated / SizeChanged / LocationChanged)
  - `LocationChanged` は親に対する相対位置の変化なので、トップレベルのウィンドウを動かしただけでは発火しない疑いがある。その場合、次に描画されるまで NVDA のハイライト矩形などが古い位置を指す。
  - P-1(a) で描画回数を減らすと、この窓が広がる。P-1 の前提として実機で確かめる。

## 7. 着手順の提案

1. **計測を先に足す(§8)。** 打鍵遅延を測るベンチがない。`GdiBench` は TopLine を変えた描画しか測らない。起動時間を測る手段もない。
2. **小さく挙動不変なもの**: P-7・P-8・P-9(a)(b)(c)・P-2・P-5・P-15
3. **設計書を起こすもの**
   - P-1・P-4: 描画範囲の削減。目視と SR の L5 が必須。
   - P-3(F-6)
   - P-6: バックアップ
   - P-10
4. **残り**: 計測結果を見て判断する。

## 8. 計測計画

windows-mcp でデスクトップ上の kxEdit を操作し、推定値を実測で確かめる。手順と結果は本節の末尾に実施記録として追記する。

| ID | 対象 | 測り方(概要) | 確かめる指摘 |
|---|---|---|---|
| M-1 | 起動時間 | 通常の publish と ReadyToRun の publish を、それぞれコールドとウォームで起動する。起動からメインウィンドウが入力を受け付けるまでの時間を測る | P-7 |
| M-2 | 矢印キー・打鍵の CPU 時間 | 日本語文書と ASCII 文書で、一定間隔のキー送出を N 回行う。プロセスの CPU 時間の増分 ÷ N を求める。ウィンドウは 900×700 と最大化 | P-1・P-2 |
| M-3 | 新規文書への連続入力 | 64KB ブロック内の位置ごとに、1 文字あたりの CPU 時間を測る | P-3 |
| M-4 | スクロール | PageDown とホイールの 1 回あたりの CPU 時間を測る | P-4 |
| M-5 | 検索語の打鍵 | 数 MB の文書で、検索ダイアログに 1 文字打つごとの CPU 時間を測る | P-5 |
| M-6 | タブ切替 | 大きめの未保存タブ数枚で、Ctrl+Tab 1 回あたりの CPU 時間を測る | P-6 |
| M-7 | UIA の矩形取得 | 全選択の範囲に `GetBoundingRectangles` をかけ、所要時間を測る | P-9 |
