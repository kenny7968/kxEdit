# 性能改善の申し送り回収 設計書

策定日: 2026-09-27
策定ブランチ: `feature/perf-followups-design`
前提資料: `docs/plans/2026-09-24-general-perf-improvements-design.md`(以下「元の設計書」)と、その各フェーズの実施記録・実装計画

## 1. 背景・目的

元の設計書のフェーズ 0〜12 は、すべて終わった(12 は評価したうえで不採用)。各フェーズの実施記録には「申し送り」が残っている。本書はそれを**全件回収する**。

回収とは、全項目に次のどれかを付けて、その結論まで進めることをいう。
- **直す**
- **調査してから決める**: 調査の結果によって、直すか閉じるかを決める
- **閉じる**: 理由を付けて閉じる

各フェーズは、**別々のセッションで、本書 §3 と該当節だけを読めば着手できる**ように書く。

**範囲外**
- 元の設計書 §18 の保留項目(再開の条件を満たしていない)
- フェーズ 9 の実機確認(実 IME の操作と NVDA のハイライト矩形。ユーザーが実施する)
- F-1 の案 B(可視範囲の切り出し。`2026-09-14-l5-followup-fixes-design.md` の申し送り)。フェーズ 9 の計測で必要と分かった場合は、その時点で提案する

## 2. 決定事項(ブレインストーミングでの合意)

| 論点 | 決定 |
|---|---|
| 文書の形 | 本書 1 本を傘にし、項目をフェーズに振り分ける。1 フェーズ = 1 ブランチ・1 PR |
| 閉じ方 | 項目ごとに「直す / 調査してから決める / 閉じる」を判定してよい。閉じる項目は §15 に理由を残す |
| F-1 の案 B | 含めない(§1 の範囲外) |
| 変異検証 | フェーズ 4(検索エンジンの中核)はスポットで行う。フェーズ 5(Splice)は列挙を広げる扱いで行う(ユーザー承認済み) |

## 3. 全フェーズ共通の規約

### 3.1 進め方

1. 本書 §3 と該当フェーズの節を読む。元の設計書の関連する節(§4 の対応表で引く)も読む。
2. main から `feature/<topic>` を切る(topic は §4 のフェーズ表を参照)。
3. superpowers:writing-plans で実装計画 `docs/plans/YYYY-MM-DD-<topic>.md` を作る。
4. CLAUDE.md §3 の 4〜6 に従って、実装・レビュー・品質ゲートを通す。
5. PR description に、意図的な挙動差(§3.5)・計測値(あれば)・L5 の結果を載せる。
6. マージ後、本書の該当フェーズ節の末尾に「実施記録」を追記する(CLAUDE.md §8 で許される追記)。

**調査してから決めるフェーズ**(7 の 5・8 の 3・3 の 14・9〜11)は、元の設計書のフェーズ 12 と同じ形で進める。
1. 最初のタスクで調査する。
2. 結果と結論(直す / 閉じる)を、本書の実施記録に書く。
3. 直す場合は、その場で設計を詳しくし、ユーザーの承認を得てから実装する。

不採用で終わってもよい。調査だけで終わるフェーズも、記録の PR は出す(docs のみなら CLAUDE.md §6 の例外に当たる)。

### 3.2 順序と依存

**推奨の順序**: 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 9 → 11 → 10

- 1 はデータ損失なので最初に行う。
- 5 の中では、27(テスト)を先に入れてから 16 を直す。
- 8 の中では、3 の経路を調べてから 2 の直し方を決める。
- 10 はユーザーの立ち会い(NVDA の実機)が要る。日程に合わせて前後させてよい。
- 依存のないフェーズ同士は、順序を入れ替えてよい。

### 3.3 レビュー・L5・変異検証

**前倒しレビュー**(CLAUDE.md §3 の 4)
- **脆弱性レビュー**
  - フェーズ 3: 外部ファイル由来の文字列の表示、シンボリックリンク
  - フェーズ 8: WebView・プレビュー
- **コード品質レビュー**
  - フェーズ 2: UI スレッドへの委譲の共通関数(新しい seam)
  - フェーズ 6: `Fakes/` の共通のテスト補助

**L5**(CLAUDE.md §5)

| 要否 | フェーズ | 内容 |
|---|---|---|
| 必須 | 2 | 簡易。`sr-regression` と、行移動・選択・タブを閉じる操作 |
| 必須 | 3 | 軽い。grep の結果一覧の読み上げ |
| 必須 | 8 | 閉じた後のフォーカス復帰、本文が先に読まれること |
| 必須 | 10 | 調査そのものが実機で行う |
| 目視だけ | 7 | PrintWindow による実解像度の PNG で判定する |
| 不要 | 1・4・5・6・9・11 | — |

**変異検証**(CLAUDE.md §4-A、ユーザーのグローバル規約「原則実施しない」)
- フェーズ 4: 検索エンジンの中核。スポットで 1〜2 個(break の条件)。
- フェーズ 5: バッファの `Splice` の左マージ。§4-A の列挙にはないが、カーソル・選択・Undo の土台なので、列挙を広げる扱いで行う(2026-09-27 にユーザー承認)。
- そのほかのフェーズでは行わない。
- 注意: `--no-build` やビルド失敗で古い DLL が走ると、変異が当たらずに「生存」に見える。変異のたびにビルドの成功を確かめる。

### 3.4 計測

元の設計書 §3.2 に従う(Smoke `--perf` を 3 回走らせて中央値を取る。NVDA 起動中で測る)。

### 3.5 意図的な挙動差の一覧(各 PR に記載する)

| フェーズ | 挙動差 |
|---|---|
| 1 | 未保存なのに退避がない状態では、内容の署名が前回と同じでも退避を書く(Issue #93 の修正) |
| 2 | Handle の破棄と UIA の問い合わせが競合したとき、RPC スレッドで計算せずに縮退値(空の範囲・失敗)を返す |
| 3 | grep の結果一覧で、行の本文とファイル名を無害化して表示する(C0/C1 の制御文字は空白に、BiDi・書式文字は除去、連続する空白は 1 つに畳む) |
| 3 | grep をキャンセルしたとき、途中のファイルで見つかっていたヒットが結果に残る(タイムアウト時の既存の扱いと揃う) |
| 7 | フォントの大きさを 0.5pt 単位に丸めて保存する(FontDialog が返す 20.25 → 20) |
| 8 | プレビューが Alt+C で閉じる |
| 8 | プレビューを表示している間は、主窓のショートカットが動かない |

調査の結果として挙動差が増えた場合は、そのフェーズの実施記録と PR に書く。

## 4. フェーズ一覧と項目の対応

### 4.1 フェーズ一覧

| # | フェーズ(topic) | 項目 | 判定 | L5 |
|---|---|---|---|---|
| 1 | バックアップの退避漏れ(`backup-undo-gap`) | 1 | 直す | 不要 |
| 2 | UIA のスレッド境界(`uia-thread-guard`) | 8・10・11(保険) | 直す | 必須(簡易) |
| 3 | grep の堅牢化(`grep-hardening`) | 12・13・G-1・14・G-2 | 直す / 14・G-2 は調査してから | 必須(軽い) |
| 4 | 検索の旧経路(`search-legacy-enum`) | 15・28 | 直す | 不要 |
| 5 | 追記ブロックのマージ(`append-merge`) | 27・16・20 | 直す | 不要 |
| 6 | 小さな整理(`tidy-up`) | 21・22・30・25・26 | 直す | 不要 |
| 7 | フォント(`font-size-and-default`) | 4・5 | 4 は直す / 5 は調査してから | 目視 |
| 8 | プレビューのキー操作(`preview-keys`) | 3・2 | 3 を調査してから 2 を直す | 必須 |
| 9 | 長大行の描画コスト(`long-line-paint-cost`) | 17・19・18 | 調査してから | 不要 |
| 10 | SR の実機調査(`sr-investigation`) | 6・7 | 調査してから | 必須 |
| 11 | 破棄時の競合(`dispose-race-investigation`) | 9 | 調査してから | 不要 |
| — | 閉じる(§15) | 11・23・24・29・31・32・33・34 | 閉じる | — |

### 4.2 項目の対応表

「出典」は元の設計書の節番号。G-1・G-2 は本書の調査で新たに見つけた項目。

| 項目 | 内容 | 出典 | フェーズ |
|---|---|---|---|
| 1 | 保存後に Undo で退避済みの内容へ戻すと退避されない(Issue #93) | §11.6 | 1 |
| 2 | プレビューが Alt+C で閉じない | §17.3 | 8 |
| 3 | モーダルのプレビュー越しに、主窓のショートカットが動く | §17.3 | 8 |
| 4 | フォントダイアログで 20pt を選ぶと 20.3 pt と表示される | §16.4 | 7 |
| 5 | ctor の既定フォント名が半角「MS ゴシック」 | §15.3・§16.4 | 7 |
| 6 | 折り返し ON で say all が論理行 2 行ぶんで止まる | §15.3 | 10 |
| 7 | タブ切替でフォーカス移動が二重化している疑い | §11.1・§11.6 | 10 |
| 8 | Handle の破棄の窓で、RPC スレッドが幅メモに書き込みうる | §7.4 | 2 |
| 9 | 窓の破棄で「CreateHandle の実行中は Dispose できない」 | §6.6 | 11 |
| 10 | `OnSnapshotChanged` の直後に、古いキーのキャッシュが書かれる | §15.3 | 2 |
| 11 | 折り返し OFF の `SetTopPosition` で `_topSegment` が古いまま残る | §7.4 | 閉じる(保険は 2) |
| 12 | grep: 8000 バイトより後ろの NUL が `LineText` に入る | §13.4 | 3 |
| 13 | grep: 正規表現モードで、1 ファイル内のキャンセル不能時間に上限がない | §13.4 | 3 |
| 14 | grep: ヒット行の `LineText` が巨大な行全体を保持する | §13.4 | 3 |
| 15 | 検索の旧経路が一致ごとに Match を確保する | §10.5 | 4 |
| 16 | `Splice` の左マージを、同じ下地の別の包み同士に広げる | §9.5 | 5 |
| 17 | `RowPaintKey.Text` が長い行でコストを持つ | §14.4 | 9 |
| 18 | S3 の残り(ja 1.47 ms)の内訳 | §14.4 | 9 |
| 19 | `FrameRowCache` の古い件が本文を 1 世代長く握る | §14.4 | 9 |
| 20 | `WordBoundary.cs` の「約 17 倍・22.5 ms」の再計測 | §9.5 | 5 |
| 21 | テスト補助が複数のテストクラスに散らばっている | §14.4 | 6 |
| 22 | `EditorControl.Paint.cs` が 672 行 | §14.4 | 6 |
| 23 | `EnsureVisibleCharRange` の finally の冗長な無効化 | §14.4 | 閉じる |
| 24 | PaintSnapshot と `--paint-transition` の重複 | §8.6 | 閉じる |
| 25 | `ProbeSaveTargetWithTimeout` の末尾区切りの到達不能 | §12.7 | 6 |
| 26 | `DeleteWithRetryAsync` の観測されない faulted Task | §12.7 | 6 |
| 27 | 貼り付けで格子点が多バイト文字の途中に来る形のテスト | §9.5 | 5 |
| 28 | デバウンスが遅延を数え直すことのテストがない | §10.5 | 4 |
| 29 | `ApplyAppearance` 先頭のキャッシュ破棄の専用テストがない | §15.3 | 閉じる |
| 30 | `DeleteWithRetry_RetriesAfterFailure` が `Sleep(300)` に依存 | §12.7 | 6 |
| 31 | NUL 入りの `[InlineData]` と trx | §12.7 | 閉じる |
| 32 | オラクルが hscroll の出る配置を通らない | §14.4 | 閉じる |
| 33 | 窓を重ねた状態で撮れることの対照 | §8.6 | 閉じる |
| 34 | バックアップで単独サロゲートが U+FFFD になる | §12.7 | 閉じる |
| G-1 | grep の結果一覧のファイル名が無害化されていない(U+202E で拡張子を偽装できる) | 本書 | 3 |
| G-2 | ファイルのシンボリックリンクで 64MB 上限を迂回できるか | 本書 | 3 |

**実施済みのため対象外**
- 元の設計書 §5.5 の計測上の注意(以後のフェーズで適用済み)
- §6.6 と §8.6 からフェーズ 3・9 への申し送り(フェーズ 9 で回収済み。PaintSnapshot の共通化は 24)
- §7.4 の `_lastFrame` のコメント(フェーズ 3 で対応済み。`EditorControl.cs:131-133`)

### 4.3 調査で分かった、元の記述の訂正

- **5**: 製品の初期表示には影響しない。製品で EditorControl を作るのは `MainForm.CreateEditor` だけで、直後の `ApplyAppearance` が全角の名前か設定値に置き換える。影響を受けるのは、`new EditorControl` を直接使うテストと Smoke(Microsoft Sans Serif の比例フォントで走る)だけ。
- **3**: 元の設計書の「WebView2 がアクセラレーターを `ProcessCmdKey` に回す」という推定には根拠がない。WebView2.WinForms.dll(1.0.4022.49)のメタデータに `ProcessCmdKey` の参照はない。経路は不明。
- **4**: 原因が確定した。FontDialog は LOGFONT の整数ピクセルの高さから Font を作り直す。96 DPI では 20pt → 26.67px → 27px → 20.25pt になる。

## 5. フェーズ 1: バックアップの退避漏れ(`backup-undo-gap`)

**目的**: 保存後に Undo で「最後に退避した内容」へ戻したとき、その状態が退避されず、hot exit やクラッシュで失われる欠陥(Issue #93)を直す。

**原因**
- 削除の分岐(`BackupCoordinator.cs:617-625`)と `ReconcileMapMaintenance`(`:705-709`)は、`HasBackup=false` にしても `LastSig` を残す。
- `BackupPlanner.Decide`(`BackupPlanner.cs:30-31`)は、modified のとき `forceWrite || currentSig != lastSig` だけを見て、`hasBackup` を見ない。
- P-6 の省略条件(`BackupCoordinator.cs:601`。弱参照のスナップショットが同じなら全文化もハッシュも省く)も同じ穴を持つ。Undo は以前のスナップショットの参照を返しうるので、この経路でも迂回される。
- 同じ穴は、登録時にクリーンで、同じ内容のまま Modified=true になる経路(`ClearSavePoint`・エンコーディングの変更など)にもある。

### 5.1 変更

- `BackupPlanner.Decide` の modified の分岐を `forceWrite || !hasBackup || currentSig != lastSig` にする。
- P-6 の省略条件を `info.HasBackup && modified && !info.ForceWrite && IsRemembered(...)` にする。
- 関係するコメント(`BackupCoordinator.cs:598-599`・`BackupPlanner.cs:31-36`)を直す。

### 5.2 テスト

- Core: `Decide` に「modified・同じ sig・hasBackup=false・force=false → Write」を足す。既存の `BackupPlannerTests` に、この変更で期待値が変わる組み合わせはない。
- App: 次の 2 つ以上を足す。
  - 保存 → Undo で退避済みの内容へ戻す → 退避が書かれる(P-6 の省略経路を通る形も含める)
  - 同じスナップショットのまま dirty になる(`ClearSavePoint`)→ 退避が書かれる
- 陰性対照: 変更前のコードで、上のテストが落ちることを確かめる。

### 5.3 完了条件

- hot exit で、Undo 後の内容が復元されることを手動で 1 回確かめる。
- PR で Issue #93 を閉じる。

## 6. フェーズ 2: UIA のスレッド境界(`uia-thread-guard`)

**目的**: a11y の鉄則(RPC スレッドからエディタ内部に触らない)を、Handle の破棄の窓でも守る。あわせて、UI スレッドへの委譲の書き方を 1 か所にまとめる。

**背景**
- `UiaTextHostAdapter` の各経路は `IsHandleCreated` → `InvokeRequired` → `Invoke` の形をとる。`IsHandleCreated` と `InvokeRequired` の間で Handle が破棄されると、`InvokeRequired` が false になり、計算が RPC スレッドで走る。
- 経路の現状:

| 経路 | 場所 | 状態 |
|---|---|---|
| `GetBoundingRectangles` | `:669-673` | 無害化済み(`_hwnd==0` で先に抜ける)。ただし Invoke の例外を catch していない |
| `OffsetFromScreenPoint` | `:763-767` | 同上 |
| `TryFindVisualSegment` | `:447-478` | **残存**。幅メモと `_lastLineSegs` に書く |
| `GetVisibleRange` | `:836-853` | **残存**。折り返しを計算して幅メモに書く |
| `SetSelection` | `:341-348` | **残存**。キャレットの書き換えと `UpdateUI` の発火を RPC スレッドで行う |
| `ScrollRangeIntoView` | `:800-817` | **残存**。同上 |
| `SetFocus` | `:872-879` | 実害なし |

- 窓はほぼ teardown(タブを閉じる・アプリの終了)に限られる。実害は、破棄済みのフォントで測ろうとして UIA の呼び出しが失敗するか、App の購読者が RPC スレッドで動く程度。クラッシュやデータ破損に至る経路は見つかっていない。

### 6.1 変更

- `UiaTextHostAdapter` に `TryRunOnUi<T>(Func<T> body, T fallback)` を置く(投函する系には戻り値のない版)。
  - Handle がなければ fallback を返す。
  - `InvokeRequired` なら `Invoke` し、`ObjectDisposedException` と `InvalidOperationException` を catch して fallback を返す。
  - それ以外は、`OnHandleCreated` で覚えた UI スレッドの ID と今のスレッドを比べる。違えば fallback を返す(投函する系は捨てる)。
- 上の表の 7 経路をすべて `TryRunOnUi` に寄せる。通常時の応答は変えない。
- **10**: 折り返しの行キャッシュへ書き込む直前(`:502`)に、`ReferenceEquals(snap, _bufferSnapshot) && wrap == _host.WrapColumns` のガードを足す。
- **11(保険)**: `SetTopPosition` で、折り返し OFF(`_wrapColumns <= 0`)ならセグメントを 0 に丸める。到達可能な状態での挙動は変わらない(§15 の 11 を参照)。

### 6.2 テスト

- Handle を破棄した後、未 Dispose の状態で、worker スレッドから各経路を呼ぶ。fallback が返り、幅メモの件数が増えないことを確かめる。窓そのものは再現できないので、ガードより後ろを直接通す seam を使う(既存の `UiaScreenCoordinateTests.ComputePaths_AfterHandleDestroyed_DoNotRecreateHandle` と同じ形)。
- 10: worker から `LineEnd` を呼んで Invoke で待たせる。STA 側は `TestHook_LineSegsInvokeCount` が 1 になるまで(ポンプせずに)待ち、編集してから `DoEvents` する。キャッシュが空のままであることを確かめる。
- 11: 折り返し OFF で、`SetTopPosition` に 0 以外のセグメントを渡しても 0 になることを確かめる。

### 6.3 完了条件

- `sr-regression` が EXIT 0。
- NVDA で、行移動・選択・タブを閉じる操作を簡易に確認する。

## 7. フェーズ 3: grep の堅牢化(`grep-hardening`)

**目的**: grep の結果一覧に出る外部由来の文字列を無害化する。正規表現モードのキャンセルを効くようにする。メモリとシンボリックリンクの懸念は、調査してから決める。

**前倒しの脆弱性レビュー**を行う。

### 7.1 変更

- **12・G-1(表示の無害化)**
  - `GrepResultsWindow.Format`(`:67-79`)を internal static に切り出す。
  - 行の本文は、**先に 200 字に切ってから** `SanitizeForDisplay.OneLine` を通す(`OneLine` は全体を走査して StringBuilder を確保するので、64MB の行をそのまま渡さない)。
  - `RelativePath` にも同じ無害化をかける(RestoreDialog の BK-L-4 と同じ類型)。
  - **`LineText` 自体は変えない**。`LineText` はジャンプの照合キー(A-18。`GrepTypes.cs:14-36`・`GrepJumpResolver.cs:80-147`)で、バッファ側にも NUL がそのまま残るため。
- **13(キャンセル)**
  - `CollectLineHits`(`GrepService.cs:196-235`)に `CancellationToken` を渡し、毎行 `IsCancellationRequested` を確認する(volatile 読み 1 回で、行ごとの Regex 呼び出しに比べて無視できる)。
  - キャンセル不能な時間の上限は、約 1 秒(1 行ぶん)にリテラルのプリフィルタの 1 秒を足した値になる。

### 7.2 調査(結果によって直すか閉じる)

- **14**: minified の JS/JSON(1 行 1〜10MB)を含む実際のフォルダーで grep し、結果を保持したときのメモリを測る。
  - 直す場合の第一候補は、**ヒット件数の上限**(例: 10 万件で打ち切り、打ち切ったことを表示する)。UI スレッドでの全件追加(`GrepResultsWindow.Populate`)も同時に抑えられる。
  - `LineText` を切り詰める案は採らない。照合キーの不変条件(`MatchStartInLine + MatchLength <= LineText.Length`)が壊れ、ジャンプの意味が変わるため。
- **G-2**: ファイルのシンボリックリンクで、`new FileInfo(path).Length`(`GrepService.cs:97`)がリンク自体の長さを返すかを確かめる。返すなら、64MB 上限を迂回して `File.ReadAllBytes` で読める。リンク先が UNC なら、外向きの SMB 接続も起こりうる。
  - 直す場合は、reparse point のファイルを飛ばすか、リンク先を解決した長さで判定する。

### 7.3 テスト

- `Format` の単体テスト: NUL・U+202E・C1 制御文字・200 字を超える行・巨大な行。
- キャンセル: 既存の internal 4 引数 `Search` のプリフィルタの差し替え口で、デリゲートの中で CTS をキャンセルして `true` を返す。多数の行が一致するファイルで、ヒット数がファイルの行数より少なく、`Cancelled=true` になることを確かめる。時間に依存しない。

### 7.4 完了条件

- NVDA で、結果一覧の読み上げ(NUL 入りの行・長い行)を軽く確かめる。

## 8. フェーズ 4: 検索の旧経路(`search-legacy-enum`)

**目的**: 表の上限を超えたときの従来経路で、一致ごとに Match を確保しないようにする。

**背景**
- `TextSearcher.FindPrev`(`:108-120`)と `Locate`(`:126-143`)は、今も `Matches`(MatchCollection)で列挙している。`Count`(`:60`)は `Regex.Count` なので対象外。
- 呼び出し元は `MaterializedSearchStrategy.cs:146-158`(表の上限 100 万件を超えたか、タイムアウトしたとき)と、`RegexPerLineSearchStrategy.cs:102,113`(32M 字を超える文書の行単位)。
- 設計書の想定(`.` × 32M 字)より現実的に起きる。20MB の CSV で `,` を検索して F3 を押すだけで 100 万件を超え、失敗は記憶されるので、以後の F3 は毎回この経路で 200MB 超を一時的に確保する。

### 8.1 変更

- 2 か所の `foreach (Match m in _regex.Matches(text))` を `foreach (var m in _regex.EnumerateMatches(text))` にする(`Index` と `Length` しか使っていない)。
- **28**: `WinFormsDebounceSchedulerTests` に、遅延を最後の予約から数え直すことのテストを足す。

### 8.2 テスト

- 旧実装(`Matches`)と同じ列を返すことを照合する(`MatchPositionsTests.CollectMatches_yields_same_sequence_as_Matches` の形を流用する)。
- 確保量: `GC.GetAllocatedBytesForCurrentThread` の差分が、一致件数に比例しない上限以下であることを確かめる。
- 28: 片側だけの不等式にする。
  - `Schedule(A)` の後、メッセージを汲まずに遅延より長く Sleep する(WM_TIMER は汲まない限り配送されない)。
  - 続けて `Schedule(B)` し、そこから発火までの時間が「遅延の半分以上」であることを確かめる。
  - `Stop()` を消す変異では、汲み始めた直後(約 0 ms)に発火するので区別できる。遅いマシンでは発火が遅れるだけなので、偽の失敗になりにくい。
- 変異検証: スポットで 1〜2 個(break の条件)。

## 9. フェーズ 5: 追記ブロックのマージ(`append-merge`)

**目的**: フェーズ 4(元の設計書)で増えたピースを、隣接マージで元に戻す。先に照合テストを入れて安全網にする。

**背景**
- `TextBuffer.Splice` の左マージ(`TextBuffer.cs:237-258`)は、`ReferenceEquals(last.Chunk, first.Chunk)` で判定している。追記ブロックは格子点が増えるたびに包み直す(`AppendBuffer.cs:91-99`)ので、同じ下地の別の包み同士はマージされない。
- 現状: `--typing` は 0.71 µs/insert、1M 字の打鍵でピース数は 247(フェーズ 4 の前は 0.52 µs・16)。実験では、隣接マージで 0.51 µs・16 ピースに戻った(`2026-09-25-perf-append-grid.md:900,930`)。正しさの問題はない。

### 9.1 変更(この順で行う)

1. **27(テスト)**: `TextBuffer` 経由で、数 KB の塊の境界付近に 3 バイト・4 バイトの文字を置き、名目の格子点が文字の途中に来るように位相をずらした文字列を何度か貼り付ける。既存の `AssertMatchesSource` で元の文字列と照合する。
2. **16**:
   - `TextChunk` に「同じ下地か」を判定する API を足す(2 つの包みはどちらもブロック全体の `ReadOnlyMemory<byte>` を包むので、Memory の等値で判定できる)。
   - `Splice` の左マージで、下地が同じでバイトが連続していれば、`first.Chunk`(新しい包み)で結合する。
   - 安全性: `first` は常に今回の `Append` の結果なので、最新の包みになる。Undo の後はバイトが連続しないので、結合は起きない。
3. **20**: 同じセッションで、`WordBoundary.cs:172-177` の「約 17 倍・22.5 ms」を V-3 の条件(空白のない単一クラスを 32KB 以下で貼り付け・cap 128)で再計測し、コメントの数値を更新する。一時的なベンチコードは commit しない。

### 9.2 テスト

- ピース数(`PieceCount`)が減ること。
- 結合したピースの `Chunk` が、最新の包みと同じ参照であること。古い包みを採る変異は、正しさの上では等価で格子が粗くなるだけなので、この確認がないと生き残る。
- 変異検証を行う(§3.3)。

### 9.3 完了条件

- `--typing` の µs/insert とピース数を、変更前後で測る。
- 元の設計書 §3.5 のフェーズ 4 の挙動差(ピースの増加)が解消したことを、PR に書く。

## 10. フェーズ 6: 小さな整理(`tidy-up`)

**目的**: テスト補助の重複と大きすぎるファイルを整理し、不安定なテストと小さな堅牢化の穴を塞ぐ。製品の挙動は変えない。

### 10.1 変更

- **21**: `tests/kxEdit.Editor.Tests/Fakes/PaintTestHelpers.cs`(静的クラス)に次を寄せる。前例は `Fakes/ScreenSurface.cs`。

| 補助 | 今の場所 |
|---|---|
| `Pixels` | `SkipInvalidateOracleTests.cs:53`・`ClipPaintTests.cs:38`(完全な複製) |
| `Paint` | `SkipInvalidateOracleTests.cs:73`(int[] を返す版)・`ScrollPixelsTests.cs:32`・`EditorControlSkipInvalidateTests.cs:51` |
| `PaintAndAssumeRecorded` | `EditorControlSkipInvalidateTests.cs:55`・`EditorControlPartialInvalidateTests.cs:33` |
| `Rects` | `ScrollPixelsTests.cs:35`・`EditorControlPartialInvalidateTests.cs:39` |
| `Composite`・`DiffBounds`・`OnScreen` | `SkipInvalidateOracleTests` の internal(`ScrollPixelsTests` が 8 回呼んでいる) |

  `MakeHosted`(7 クラス)と `Line()`(3 クラス)は、サイズや引数がクラスごとに違うので寄せない。

- **22**: `EditorControl.Paint.cs` の 131〜344 行(`InvalidateChangedRows` の 2 つの版・`TryPlanScroll`・`IndexOfRow`・`ExposedStrip`・`TestHook_SetPaintSurface`・`InvalidateBands`・`InvalidateImeRows`・`InvalidateRectIfAny`・`InvalidateAndForgetPaintedFrame`)を、`EditorControl.Invalidation.cs` へそのまま移す。使うフィールドは本体(`EditorControl.cs:154-161`)にあるので、純粋な移動で済む。
- **30**: `DeleteWithRetryAsync`(`PreviewUserDataFolder.cs:100-131`)に、internal の任意引数 `onAttemptFailed` を足す。テスト(`PreviewUserDataFolderTests.cs:241-270`)は、1 回失敗した合図を待ってからロックを放す。
- **25**: `ProbeSaveTargetWithTimeout`(`FileReachabilityProbe.cs:284-307`)で、`Path.EndsInDirectorySeparator(Path.GetFullPath(path))` を明示的に判定し、今と同じ結果を返す。等価性の網(`FileReachabilityProbeTests.cs:296-307`)に、結果の値そのものの固定を足す。
- **26**: `DeleteWithRetryAsync` の最後に `catch (Exception)` を足し、Trace を残して諦める。アナライザーの抑止が要る場合は `docs/lint-format-setup.md` の規約に従う。

### 10.2 完了条件

- 21・22 は、移動の前後でテストの件数と結果が同じであること。
- 30 は、合図を待つ形になったことで、`SleepMs(300)` への依存がなくなっていること。

## 11. フェーズ 7: フォント(`font-size-and-default`)

### 11.1 項目 4: 20pt が 20.3 pt になる(直す)

- `DisplaySettingsTab`(`:161-162`)で、ダイアログが返した `dlg.Font.SizeInPoints` を 0.5pt 単位に丸めて保存する(20.25 → 20、9.75 → 10、11.25 → 11)。
- 丸めは Core の純関数にし、単体テストで確かめる(10.5 のような 0.5pt の値が保たれることも)。
- 目視: 同じ DPI では GDI の lfHeight が同じなので、描画は同じになる見込み。ただし行高の float 計算で 1px 変わりうるので、変更前後を PrintWindow の実解像度 PNG で比べる。
- ボタンの AccessibleName の読み上げも「20 pt」に変わる(SR 経路の外。L5 は不要)。

### 11.2 項目 5: 既定フォント名(調査してから決める)

- `EditorControl.cs:199` は半角「MS ゴシック」、`:2810` と `AppSettings.cs:9` は全角「ＭＳ ゴシック」。
- 製品には影響しない(§4.3)。
- 調査:
  - scratchpad で既定名を全角にしたビルドを作り、Editor.Tests が何件落ちるかを数える(元の設計書 §16.2 では、折り返し境界やピクセル値を固定した箇所が 231 あった)。
  - windows-latest の CI ランナーに MS ゴシックが入っているかを確かめる。入っていなければ、ローカルと CI で結果が割れる。
- 結論:
  - **定数を 1 つに寄せる**: 落ちる件数が少なく、CI にもフォントがある場合。
  - **理由を付けて閉じる**: それ以外。製品に影響しないこと、テストが比例フォントで安定して動いていることを、ctor のコメントに残す。

## 12. フェーズ 8: プレビューのキー操作(`preview-keys`)

**前倒しの脆弱性レビュー**を行う(WebView・プレビュー)。

### 12.1 調査(最初のタスク)

- 項目 3 を、実際のキー入力で再現させる(SendInput による計測スクリプトで 1 回観測しただけで、送り方によって生じた可能性も否定できない)。
- scratchpad の計測ビルドで、`ShowMarkdownPreview`(`MainForm.cs:1796-1855`)の入口でスタックトレースを採り、主窓のショートカットがどの経路で届いたかを特定する。
- 項目 2 について、フォーカスが WebView2 にあるとき Alt+C がプレビューのフォームの `ProcessCmdKey` と `ProcessDialogKey` のどちらに届くかを確かめる。

### 12.2 修正の方針

- **2**: プレビューのフォームで、12.1 で届くと分かった方(`ProcessCmdKey` か `ProcessDialogKey`)を override し、Alt+C のとき Close する。`OnNavCompleted` の `_web.Focus()` は残す(SR に本文を先に読ませるため)。
- **3**:
  - 経路がプレビューのフォームを通るなら、同じ override で主窓向けのショートカットを食う。
  - 通らないなら、主窓側に「モーダルを表示中はメニューのコマンドを無視する」ガードを置く。
  - どちらの場合も、`ShowMarkdownPreview` に再入ガードを保険として入れる。
- **採らない案**: 注入スクリプトで Alt+C を拾う案。WebMessage の面を広げるため。C# 側で処理できない場合だけ再検討する。

### 12.3 完了条件

- Alt+C でプレビューが閉じ、主窓のエディタへフォーカスが戻ること。
- プレビューを表示している間、Ctrl+Shift+U・Ctrl+W・Ctrl+S などが動かないこと。
- NVDA で、閉じた後のフォーカス復帰と、表示直後に本文が先に読まれることを確かめる。

## 13. フェーズ 9: 長大行の描画コスト(`long-line-paint-cost`)

**目的**: `RowPaintKey.Text`(17)と `FrameRowCache` の保持(19)が長大行で問題になるかを測り、直すか閉じるかを決める。18 の計測も同じセッションで行う。

### 13.1 調査

- Smoke `--perf` に、長大行の打鍵シナリオ(1M 字の 1 行・折り返し OFF)を足す。シナリオは結論によらず残す。
- 1 回の打鍵で、`RowPaintKey` の本文の取り出し(`FrameDiff.cs:52-55`)と比較にかかる時間を、描画全体と比べる。参考: 1 回の描画では、すでに行全体を 3〜4 回取り出している(`ViewportLayout.cs:66`・`FrameBuilder.cs:253,286,424`)。
- **18**: dotnet-trace で、en と ja の S1・S3 を採る。en の S1(描画 0 回で 0.55 ms。ja は 0.08 ms)の固定費を主な対象にする。結果を記録するだけで、コードは変えない。

### 13.2 判断の基準

- キーの本文にかかる時間が、打鍵 1 回の描画全体の **1 割を超える**なら直す。直し方は、キーを `(スナップショットの参照, 開始, 長さ)` にし、スナップショットが同じ参照なら範囲の一致だけで等しいと判定する形。19 も一緒に解消する。
- 超えなければ、理由を付けて閉じる。

## 14. フェーズ 10・11: 実機での調査

### 14.1 フェーズ 10: SR の実機調査(`sr-investigation`)

ユーザーの立ち会いのもとで、NVDA の実機セッションを 1 回行う。

- **6(say all が止まる)**: 次の 3 つで切り分ける。
  - 同じ文書を折り返し OFF で試す(最も効く切り分け)
  - メモ帳の折り返しで試す
  - NVDA の debug ログと、provider の呼び出し列のトレース(scratchpad の計測ビルド)を採る
  - 候補は、NVDA の say all の継続条件と `Select()` やイベントとの干渉、Paragraph を視覚行として返すこと(`TextRangeProviderV2.cs:47-95`)、NVDA 側の問題。
  - 結論: 原因が kxEdit 側で、修正が小さければ直す。大きければ別の設計書に回す。NVDA 側なら閉じる。Move や Expand を直す場合は、§4-A の変異検証の対象になる。
- **7(フォーカス移動の二重化)**:
  - `TabControl.SelectedIndex` のセッターが、発声(`KeyBasedSwitch`)より先にエディタへフォーカスを移している疑いが強い(`DocumentManager.cs:168-206`)。後の `FocusActiveEditor()` は 2 回目の no-op になる。
  - スピーチビューアーで発声の順序を確かめる。App.Tests でも、GotFocus と KeyBasedSwitch の発火順を記録する。
  - 崩れていれば、移動先の文書を先に求め、`KeyBasedSwitch` を `SelectedIndex` の変更より前に発火させる(10〜20 行とテスト)。

### 14.2 フェーズ 11: 破棄時の競合(`dispose-race-investigation`)

- 現状: Smoke 側は `PaintSnapshot.CloseQuietly` で吸収済み。`GdiBench.cs:87` は未対処。これまでの再現は 0/30。
- 製品で同じ形になる箇所: `using` + `ShowDialog` のダイアログ群、タブを閉じる処理(`DocumentManager.cs:157-160`)、モードレス窓(`GrepResultsWindow`・`MarkdownPreviewForm`)。仮説どおりなら、UI スレッドの `Dispose` から例外が出て、クラッシュダイアログになりうる。
- 調査:
  - NVDA 起動中に、ダイアログとタブの開閉を数百回繰り返すハーネスを作る。
  - first-chance 例外と、RPC スレッドで `CreateHandle` が走ったときのスタックを採る。
  - `ShowDialog` が戻った時点での Handle の状態と、WinForms 標準のアクセシブルオブジェクトが本当に RPC スレッドで呼ばれるかを確かめる。
- 結論:
  - **再現しない**: `GdiBench` に `CloseQuietly` を適用して閉じる。
  - **再現する**: 共通の基底で、UI スレッド以外からの `CreateHandle` をガードする設計を追記し、ユーザーの承認を得てから実装する。

## 15. 閉じる項目と理由

| 項目 | 理由 |
|---|---|
| 11 | `_topSegment` への書き込みを全数確認し、到達しないと判断した。0 に戻す箇所は `EditorControl.cs:274,340,926,990,1556,2859`。`SetTopPosition` に折り返し OFF で 0 以外が渡る経路はない(EOL 変換は同じモードで保存した値を戻すだけ、ホイールは TopLine のセッターに委ねる、`Caret.cs` のセグメントは OFF では常に 0)。保険の丸めはフェーズ 2 で入れる |
| 23 | 冗長であることは、フェーズ 9 の Task 5 とレビューの故障注入で確認済み。不変条件 2(自分で無効化する)の例外を作らないための防御で、費用はほぼない(スクロールしなければ `Equals` で即座に return する) |
| 24 | 利用者は今も 2 つ(`PaintSnapshot.cs`・`PaintTransition.cs`)。「3 つ目の利用者が出たら共通の型へ」という条件を満たしていない。一部(`BuildBody`・`ReadPixels`・`CloseQuietly` など)はすでに共有している |
| 29 | 先頭の破棄が単独で効くのは、RPC スレッドが `ApplyAppearance` の途中で問い合わせたときだけ。キーの照合により、そのときの答えも「前の答え」か「ミス」のどちらかになる。外から観測できず、テストには製品側に途中で止める seam が要る |
| 31 | trx を使っていない(ci.yml・release.yml・`pre-merge-check.ps1` はどれも既定のロガー)。trx を導入するときの確認項目として残す |
| 32 | 横方向の画素移動は、`ScrollPixelsTests`(Form を表示して合成比較)と Smoke が受け持っている。欠けているのは、横スクロールと選択・編集がランダムに組み合わさる場合だけ |
| 33 | 回収済み。`2026-09-27-perf-partial-paint.md:2554`(Task 6)に記録がある |
| 34 | 本文では起こらない。バッファは挿入の時点で単独サロゲートを U+FFFD に置き換え(`TextBuffer.cs:289`)、読み込みでも `Utf8Sanitizer` が置き換えるので、バックアップとファイル保存に差はない。`BackupRecord.OriginalPath` に単独サロゲートが入る可能性は、別件として扱う |
