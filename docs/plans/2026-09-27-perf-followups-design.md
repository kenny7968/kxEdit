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
- 元の設計書 §6.6 の「リサイズ中のちらつき」の目視(未確認のまま残っている)。自動の画面取得では写らないので、フェーズ 9 の実機確認と同じく、ユーザーの実機確認に回す
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

**調査してから決めるフェーズ**(7 の 5・8 の 3・3 の 14 と G-2・9〜11)は、元の設計書のフェーズ 12 と同じ形で進める。
1. 最初のタスクで調査する。
2. 結果と結論(直す / 閉じる)を、本書の実施記録に書く。
3. 直す場合は、次の区別に従って設計し、ユーザーの承認を得てから実装する。
   - **本書に方針まで書いてある修正**(フェーズ 3 の件数の上限・G-2 の判定、フェーズ 7 の定数の統一、フェーズ 8 の override、フェーズ 9 のキー、フェーズ 10 の項目 7 の発火順)は、精密化として実装計画に書く(CLAUDE.md §8 で許される範囲)。
   - **新しい抽象や seam を伴う修正**(フェーズ 11 の共通基底、フェーズ 10 の項目 6 で Move や Expand を直す場合など)は、新しい日付の設計書 `docs/plans/YYYY-MM-DD-<topic>-design.md` に書く。本書へ新しい設計を追記しない(元の設計書 §17.1 と同じ扱い)。

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

調査の結論が「直す」になった場合は、次を追加する。
- フェーズ 9: 部分無効化の経路に触れるので、Smoke `--paint-transition` か目視で確かめる。
- フェーズ 11: WinForms 標準のアクセシブルオブジェクトの経路に触れるので L5 を行う。新しい共通基底を入れるので、前倒しのコード品質レビューも行う。

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
| 7 | フォントの大きさを 0.5pt 単位に丸めて保存する(FontDialog が返す 20.25 → 20)。中点は整数 pt に寄せるので、96 DPI で 11.5pt を選ぶと 11 になる(同じ 15px なので描画は同じ) |
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
| 7 | タブ切替でフォーカス移動が二重化している疑い | §11.1.1・§11.3・§11.6 | 10 |
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
- 関係するコメント(`BackupCoordinator.cs:598-599`、`BackupPlanner.cs` の xmldoc 17-21 行と 32 行)を直す。

### 5.2 テスト

- Core: `Decide` に「modified・同じ sig・hasBackup=false・force=false → Write」を足す。既存の `BackupPlannerTests` に、この変更で期待値が変わる組み合わせはない。
- App: 次の 2 つ以上を足す。
  - 保存 → Undo で退避済みの内容へ戻す → 退避が書かれる(P-6 の省略経路を通る形も含める)
  - 同じスナップショットのまま dirty になる(`ClearSavePoint`)→ 退避が書かれる
- 陰性対照: 変更前のコードで、上のテストが落ちることを確かめる。

### 5.3 完了条件

- hot exit で、Undo 後の内容が復元されることを手動で 1 回確かめる。
- PR で Issue #93 を閉じる。

### 5.4 実施記録(2026-09-27・PR #101)

- **成果物**
  - `BackupPlanner.Decide` の modified の分岐を `forceWrite || !hasBackup || currentSig != lastSig` にした。
  - P-6 の省略条件を `info.HasBackup && modified && !info.ForceWrite && IsRemembered(...)` にした。
  - 実装計画は `docs/plans/2026-09-27-backup-undo-gap.md`。
- **完了条件**
  - **テスト**: 計 6 件を足した。
    - Core 1 件: `Decide` の「modified・同じ sig・hasBackup=false・force=false → Write」
    - App 5 件
      - clean 化の 2 経路(Delete 分岐と即時反映)それぞれの後に、Undo で戻す形(2 件)
      - 同じスナップショットのまま `ClearSavePoint` する形(P-6 の省略経路)
      - 登録時にクリーンで、同じ内容のまま dirty になる形(同じく P-6 の省略経路)
      - hot exit の最終 flush で、レイアウトの `BackupId` が埋まる形
  - **陰性対照**
    - 変更前のコードで、6 件すべてが期待どおりの理由(書込の件数)で落ちた。
    - P-6 の条件だけを外すと、P-6 の省略経路を通る 2 件だけが落ちた。
  - **手動確認**: 実アプリの hot exit で、Undo 後の内容が未保存のまま復元された(windows-mcp で操作。PASS)。
    - 設定: 自動バックアップ ON・間隔 5 秒・起動時に前回のファイルを開く ON
    - 手順: `base` を開く → `1` を追記して退避される → `2` を追記して保存(退避は消える)→ Undo で `base1` に戻す → 退避される → Alt+F4 → 再起動
  - **品質ゲート**: `tools/pre-merge-check.ps1` が EXIT 0。
  - **レビュー**: 製品コードは 2 ファイル・十数行の小変更なので、CLAUDE.md §3 の簡略化の基準に沿うと判断し、最終レビューの 2 パスを別エージェント 1 回に統合した。Critical・Important はなかった。
    - Minor 2 件は fixup で直した。1 件目は、既存テスト `Dirty_unchanged_but_forced_writes` を `hasBackup: true` にしたこと(`forceWrite` の効きを単独で確かめるため)。2 件目は、`OnBackupBecameUnneeded` の remarks の更新。
    - 残りの Minor 1 件は受容した(P-6 経路の新しいテストに「書いた後に増えない」の確認がない。既存の `Reconcile_SameSnapshot_DoesNotMaterialize` が押さえている)。
- **本節からの精密化・訂正**
  - **§5.3 の「hot exit を手動で 1 回確かめる」**: kxEdit はコマンドライン引数でファイルを開かないので、Ctrl+O で開いた。
  - **§5 の原因の 3 つ目**(「Undo は以前のスナップショットの参照を返しうる」): 今の実装では当たらない。
    - Undo/Redo は毎回新しい `TextSnapshot` を作る(`TextBuffer.Undo` の `new TextSnapshot(e.Value.RootBefore)`)。木のルートは以前のものを使い回すが、P-6 の `IsRemembered` はスナップショットの参照を比べるので、Undo の後は必ず不一致になる。
    - 陰性対照でも、P-6 の条件を外して落ちたのは Undo 系以外の 2 件だけだった。
    - そのため、Undo の経路を止めていたのは `Decide` のほうである。P-6 の省略経路を実際に通るのは、原因の 4 つ目の経路(`ClearSavePoint`・エンコーディングの変更)と、登録時にクリーンな文書である。
    - 将来 Undo が `TextSnapshot` を使い回すか、`IsRemembered` をルートの比較に変えても、P-6 の条件で守られる。
- **申し送り**(以前からある挙動。本フェーズの範囲外。回収先は未定で、次に申し送りを回収するときに扱う)
  - 退避済みのままエンコーディングだけを変えると、署名が同じなので再退避されず、バックアップの CodePage/HasBom が古いまま残る。本文は失われない。
  - 起動時の復元の途中で Reconcile が走ると、新しい Id で登録された文書を後から `AdoptRestored` が上書きし、孤児ファイルが残りうる。
    - 上書き自体は `BackupCoordinator.cs:329` のコメントで認識済みだが、孤児が残る点には触れていない。
    - 孤児は `_map` に載らないので、クリーン終了でも消えない。次回の起動で復元の候補に出うる(未確認)。最終的には 30 日の sweep で消える。

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

- 窓はほぼ teardown(タブを閉じる・アプリの終了)に限られる。
- 最悪の場合の実害は重い。幅メモは非スレッドセーフな Dictionary なので、UI スレッドと同時に書くと構造が壊れ、`TryGetValue` の無限ループで UI スレッドが戻らなくなる(`UiaTextHostAdapter.cs:662-664` のコメント)。
- ただし teardown 中の UI スレッドは同じ幅メモに書かないので、現実に起きやすいのは、破棄済みのフォントで測ろうとして UIA の呼び出しが失敗するか、App の購読者が RPC スレッドで動く程度。

### 6.1 変更

- `UiaTextHostAdapter` に `TryRunOnUi<T>(Func<T> body, T fallback)` を置く(投函する系には戻り値のない版)。
  - Handle がなければ fallback を返す。
  - `InvokeRequired` なら `Invoke` し、`ObjectDisposedException` と `InvalidOperationException` を catch して fallback を返す。
  - それ以外は、UI スレッドの ID と今のスレッドを比べる。違えば fallback を返す(投函する系は捨てる)。
  - UI スレッドの ID は、**EditorControl の ctor(UI スレッドで生成される)で 1 回だけ記録し、以後は書き換えない**。`OnHandleCreated` で覚えると、本フェーズが防ぎたい「RPC スレッドでの Handle の作り直し」が一度起きたとき、ID が RPC スレッドの値で上書きされ、ガードが逆向きに効く(`UiaTextHostAdapter.cs:630-632`)。
- 上の表の 7 経路をすべて `TryRunOnUi` に寄せる。通常時の応答は変えない。
- **10**: 折り返しの行キャッシュへ書き込む直前(`:502`)に、`ReferenceEquals(snap, _bufferSnapshot) && wrap == _host.WrapColumns` のガードを足す。
- **11(保険)**: `SetTopPosition` で、折り返し OFF(`_wrapColumns <= 0`)ならセグメントを 0 に丸める。到達可能な状態での挙動は変わらない(§15 の 11 を参照)。

### 6.2 テスト

- Handle を破棄した後、未 Dispose の状態で、worker スレッドから各経路を呼ぶ。fallback が返り、幅メモの件数が増えないことを確かめる。窓そのものは再現できないので、ガードより後ろを直接通す seam を使う(既存の `UiaScreenCoordinateTests.ComputePaths_AfterHandleDestroyed_DoNotRecreateHandle` と同じ形)。
- 10: worker から `LineEnd` を呼んで Invoke で待たせる。STA 側は `TestHook_LineSegsInvokeCount` が 1 になるまで(ポンプせずに)待ち、編集する。そのあと、**worker が戻るまでポンプを回し続ける**(カウンタは `Invoke` を呼ぶ前に加算される(`:460`)ので、`DoEvents` 1 回では Invoke がまだ届いていないことがある)。キャッシュが空のままであることを確かめる。
- 11: 折り返し OFF で、`SetTopPosition` に 0 以外のセグメントを渡しても 0 になることを確かめる。fixture は、行数が十分あって**行がクランプされない**位置にする(行がクランプされるとセグメントも 0 に落ち(`EditorControl.cs:953`)、修正がなくても通ってしまう。CLAUDE.md §4-B)。

### 6.3 完了条件

- `sr-regression` が EXIT 0。
- NVDA で、行移動・選択・タブを閉じる操作を簡易に確認する。

### 6.4 実施記録(2026-10-01・PR #102)

- **成果物**
  - `UiaTextHostAdapter` に `TryRunOnUi<T>`(同期・戻り値あり)と `TryPostToUi`(投函)を置き、§6 の表の 7 経路をすべて寄せた。`InvokeRequired` が false でも、今のスレッドが UI スレッドでなければ縮退値を返す(投函する系は捨てる)。
  - 項目 10: `TryFindVisualSegmentCore` の書き込みに、`ReferenceEquals(snap, _bufferSnapshot) && wrap == _host.WrapColumns` のガードを足した。
  - 項目 11: `SetTopPosition` で、折り返し OFF ならセグメントを 0 に丸める。
  - 実装計画は `docs/plans/2026-10-01-uia-thread-guard.md`。
- **完了条件**
  - **テスト**: 計 7 件を足した。
    - `UiaThreadGuardTests` 5 件: Handle を破棄し、親から外す。その結果、worker から見て `InvokeRequired=false` になる。この状態で、worker から 7 経路を呼ぶ。Handle のガードはテストフック `TestHook_UiaAssumeHandleCreated` で通過させる。4 件は、UI スレッドで同じ問い合わせを行う陽性対照を持つ。
    - 項目 10・11 に 1 件ずつ。項目 10 は、編集の直後に「本体はまだ走っていない」ことを前提として assert する(最終レビューで追加)。
  - **陰性対照**
    - スレッドの照合を外すと、陽性対照を持つ 4 件が FAIL した(LineStartOf 11→15、GetVisibleRange (0,0)→(0,3)、SetSelection (3,5)→(0,1)、TopLine 0→9)。
    - 項目 10・11 のテストは、修正前に FAIL した。
    - 最初は照合の 2 行を消した。するとアナライザー S4487(読まれないフィールド)でビルドが失敗したのに、古い DLL のテストが走って緑に見えた。条件を無効化する形(フィールドは読む)で取り直した(§3.3 の注意の実例)。
  - **品質ゲート**: `tools/pre-merge-check.ps1` が EXIT 0。
  - **L5**: `tools/sr-regression.ps1` が EXIT 0。NVDA の実機で、行の移動(折り返し ON / OFF)・選択・タブを閉じる操作を確認した(PASS)。
  - **レビュー**: タスクごとのレビュー(Task 1 は前倒しのコード品質レビューを兼ねる)と、最終レビューの 2 パス(コード品質 / 脆弱性。別エージェント)を行った。Critical・Important はなかった。Minor のうち 3 件を fixup で直し、残りは PR #102 に記載して受容した。
- **本節からの精密化**
  - **UI スレッドの ID**: adapter の ctor で `readonly` のフィールドに記録した。adapter は EditorControl の ctor の中でだけ生成されるので、§6.1 の「EditorControl の ctor で 1 回だけ記録し、以後は書き換えない」と同じ意味になる。
  - **SetFocus**: 投函したものが UI スレッドに届いた時点で、もう一度判定するようになった(SetSelection / ScrollRangeIntoView と揃った)。届いた時点で Handle が無ければ `Focus()` は元々何もしないので、観測できる差はない。
  - **例外の扱い**: `GetBoundingRectangles` / `OffsetFromScreenPoint` も Invoke の `ObjectDisposedException` / `InvalidOperationException` を縮退値に落とすようになった。この catch は、UI スレッドで body 自身が投げた同じ型の例外も縮退値にする。§6.1 どおりなので受容した。書き込み系では `BeginInvoke` 自体が投げる例外も捨てる。
  - **既存テストの fixture**: `ComputeCaretPointNoWrapShortcutTests` の 1 ケース (10,1,37,3,10) は、折り返し OFF の `SetTopPosition` で古いセグメントを作っていた。項目 11 でその形が作れなくなったので、リフレクションで `_topSegment` を書く形に変えた。§15 の 11 の判断(製品の経路では到達しない)とは矛盾しない。このテストは、到達しない状態に対する `ComputeCaretPoint` の防御コードの回帰網として残す。
- **申し送り**(以前からある問題。本フェーズの範囲外。回収先は未定で、次に申し送りを回収するときに扱う)
  - 同期 Invoke を待つ間に UI スレッドが終了すると、`InvalidAsynchronousStateException`(ArgumentException の派生)が catch されない。UIA の境界で HRESULT に変わるだけで、プロセスは落ちない。
  - 書き込み系の投函に上限がない。UIA で高頻度に Select / ScrollIntoView を叩くと、invoke キューが伸びる。同じ整合性レベルのプロセスに限られ、信頼境界は越えない。
  - 自分の Handle が無く、親の Handle がある状態では、`InvokeRequired` が親で判定されて true になる。そのため Invoke した body は、UI スレッドで `IsUiBound` を判定し直さない。確認した範囲では無害。直す場合は、body を `() => IsUiBound ? body() : fallback` で包めば、`TryPostToUi` と対称になる。

## 7. フェーズ 3: grep の堅牢化(`grep-hardening`)

**目的**: grep の結果一覧に出る外部由来の文字列を無害化する。正規表現モードのキャンセルを効くようにする。メモリとシンボリックリンクの懸念は、調査してから決める。

**前倒しの脆弱性レビュー**を行う。

### 7.1 変更

- **12・G-1(表示の無害化)**
  - `GrepResultsWindow.Format`(`:67-79`)を internal static に切り出す。
  - 行の本文は、**先に span で数百字に切ってから** Trim と `SanitizeForDisplay.OneLine` を通す。今の `Format` は切る前に `LineText.Trim()` を呼んでいる(`GrepResultsWindow.cs:70`)ので、巨大な行では全体のコピーが起きうる。`OneLine` も全体を走査して StringBuilder を確保するので、64MB の行をそのまま渡さない。
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
  - 直す場合の第一候補は、**`FileStream` で開いてから `fs.Length` で上限を判定し、そのストリームから読む**形。リンクを辿った後の長さで判定でき、長さを見てから読むまでの間にファイルが変わる窓(TOCTOU)も閉じられる。
  - reparse point のファイルを飛ばす案は採らない。OneDrive のオンデマンドファイルや重複除去されたファイルも reparse point なので、それらが黙って対象から外れる(grep の偽陰性)。
  - UNC への SMB 接続を防ぎたい場合は、リンク先を見る別の判定として分けて設計する。

### 7.3 テスト

- `Format` の単体テスト: NUL・U+202E・C1 制御文字・200 字を超える行・巨大な行。
- キャンセル: 既存の internal 4 引数 `Search` のプリフィルタの差し替え口で、デリゲートの中で CTS をキャンセルして `true` を返す。多数の行が一致するファイルで、ヒット数がファイルの行数より少なく、`Cancelled=true` になることを確かめる。時間に依存しない。
  - 注意: プリフィルタの差し替え口はリテラル検索でしか呼ばれない(`GrepService.cs:122-124`)。項目 13 は正規表現モードの問題だが、このテストは両モードが共有する `CollectLineHits` の行ループを通すことで確かめている。計画にその旨を書く。

### 7.4 完了条件

- NVDA で、結果一覧の読み上げ(NUL 入りの行・長い行)を軽く確かめる。

### 7.5 実施記録(2026-10-02・PR #104)

- **調査の結論**(詳細は実装計画 `docs/plans/2026-10-02-grep-hardening.md` §0)
  - **G-2**: 直した。ファイルのシンボリックリンクを作るには管理者権限が要り、この環境では作れなかったので、**実測はしていない**(ユーザーの判断)。`FileInfo.Length` は Win32 の `GetFileAttributesEx` でリンクを辿らないので、§7.2 の第一候補で直した。
  - **14**: 直した。合成データ(1 行 1〜10MB の minified 風 JS を 40 本)では、ヒット 40 件で 420 MiB を保持した。保持量は「ヒットした行の総文字数 × 2 バイト」と一致し、§7.2 の第一候補(件数の上限)だけでは防げない。ユーザーの判断で、件数と文字数の 2 つの上限を置いた。
- **成果物**
  - 項目 12・G-1: `GrepResultsWindow.Format` を internal static に切り出し、行の本文とファイル名を `SanitizeForDisplay.OneLine` で無害化した。行の本文は、先頭の空白を span のまま飛ばし、1,024 字の窓で切ってから無害化する。パスも末尾 1,024 字の窓で切ってから無害化し、260 字を超えたら先頭を「…」にして末尾(ファイル名と拡張子)を残す。grep からジャンプした直後の通知のファイル名も無害化した。
  - 項目 13: `CollectLineHits` で行ごとに `CancellationToken` を確かめる。
  - G-2: `GrepService.ReadAllBytesBounded` を足した。開いたストリームの長さで 64MB の上限を判定し、そのストリームから判定した長さまで読む。
  - 項目 14: `GrepLimits`(ヒット **10,000 件**・保持する行の総文字数 64 Mi 字)と `GrepOutcome.Truncated` を足した。打ち切りは結果窓の題名と発声で知らせる(ヒットがあっても必ず発声する)。
- **完了条件**
  - **テスト**: 計 31 件を足した(Core 12 件・App 19 件)。
  - **陰性対照**: 各修正の条件を無効化して、対応するテストが FAIL することを確かめた。そのたびにビルドの成功も確かめた(§3.3 の注意)。この過程で、xUnit の `Assert.StartsWith` / `EndsWith` の既定がカルチャ依存の比較で U+202E を無視し、テストが空振りしていたことが分かった。テストは序数比較に直した。
  - **品質ゲート**: `tools/pre-merge-check.ps1` が EXIT 0。
  - **L5**: `tools/sr-regression.ps1` が EXIT 0。NVDA の実機で次の 4 点を確かめ、すべて PASS(windows-mcp とスピーチビューアーで採取)。
    1. 8000 バイトより後ろに NUL を含む行が、途切れずに読まれる。
    2. 200 字を超える行が「…」で終わる。
    3. U+202E で偽装したファイル名が、一覧とジャンプ後の通知で除去された順で読まれ、表示される。
    4. 10,001 行が一致するファイルで「10000 行 / 1 ファイル・上限に達したため打ち切り」が発声され、題名に「（上限で打ち切り）」が出る。表示まで約 2.2 秒。
  - **レビュー**: タスクの時点で前倒しの脆弱性レビューを行った(Task 1・2)。最終レビューは 2 パス(コード品質 / 脆弱性。別々のエージェント)で行った。Critical はなかった。Important と、Important に再判定した指摘は、すべて fixup で直した。
    - 前倒しのレビュー I-1: ジャンプ後の通知のファイル名を無害化していなかった。
    - 最終レビュー(脆弱性)I-1: 長いパスで、表示文字列が上限の内側で GB 級に膨らんだ。
    - 最終レビュー(脆弱性)I-2: 上限 100,000 件でも、一覧への追加で UI スレッドが約 54 秒止まった。ユーザーの判断で上限を 10,000 件(約 2.6 秒)に下げた。
    - 最終レビュー(品質)M-1・M-2(Important に再判定): ちょうど 201 字の行と、行末の全角空白の表示が、従来と変わっていた。
- **本節からの精密化**
  - 行の本文は、窓で切る前に先頭の空白を span のまま飛ばす(§7.1 は「切ってから Trim」)。先頭の空白が窓より長い行が、空に見えないようにするため。コピーは作らない。
  - パスの表示に長さの上限(260 字)を置いた(最終レビューの I-1)。
  - grep からジャンプした直後の通知のファイル名も無害化した(G-1 の続き)。
- **意図的な挙動差**(§3.5 の 2 行に足す)
  - ヒットが 10,000 件、または保持する行の総文字数が 64 Mi 字に達したら、走査を打ち切る。打ち切りは、結果窓の題名と発声で知らせる。
  - ファイルの大きさの上限は、開いた後の長さ(リンクを辿った後の長さ)で判定する。判定した後に伸びたファイルは、判定した長さまでしか読まない。長さを 0 と報告するのに中身のある仮想ファイル(`\\wsl$` の `/proc` など)は、0 バイトとして扱う(従来は EOF まで読んでいた)。
  - 一覧のパスが 260 字を超えたら、先頭を「…」にして表示する。
- **申し送り**(回収先は未定。次に申し送りを回収するときに扱う)
  - 窓の題名とタブの `DisplayName` は無害化されていない(L5 で、U+202E の偽装が題名とタブに出ることを確認した)。grep 以外の「開く」経路にも共通する。
  - 結果一覧の仮想化。10,000 件で約 2.6 秒、UI スレッドが止まる。
  - `SanitizeForDisplay.OneLine` は、Hangul Filler・U+3000 などの Zs・異体字セレクタを残す。また、RTL 文字だけで起きる暗黙の並べ替えは防げない。
  - `fs.Length` が負なら `OverflowException` が grep 全体から抜ける。NTFS では起きない。アプリは落ちない。
  - `GrepError` の蓄積に上限がない(以前からの挙動)。
  - キャンセル不能な時間の上限(約 1 秒 + プリフィルタ約 1 秒)は、照合の部分に限った値。読み込み・文字コード判定・復号(最大 64MB)はキャンセルを確かめない。
  - シンボリックリンクのテストは、リンクを作れない環境では何も確かめずに PASS と表示される。

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

### 8.3 実施記録(2026-10-02・PR #105)

- **成果物**
  - 項目 15: `TextSearcher.FindPrev` / `Locate` の列挙を `Matches` から `EnumerateMatches` に置き換えた。返す (Index, Length) の列・順序・例外の伝播は変わらない(挙動差なし)。
  - 項目 28: `WinFormsDebounceSchedulerTests` に、遅延を最後の予約から数え直すことのテストを足した(製品コードは変えていない)。
  - 実装計画は `docs/plans/2026-10-02-search-legacy-enum.md`。
- **完了条件**
  - **テスト**: 計 3 件を足した(Core 2 件・App 1 件)。
    - `Legacy_paths_match_Matches_reference`: `Matches` を正解にした旧ループと照合する。置き換えると既存の `Strategy_matches_old_implementation_for_random_texts` の正解(`TextSearcher`)も新しい列挙になるので、別に置いた。
    - `Legacy_paths_do_not_allocate_per_match`: `,` が 10 万件ある文字列で、`Locate` と `FindPrev` の確保量が **45,795,248 バイト → 0 バイト**になった(上限 64KiB で判定)。
    - `Schedule_Again_RestartsDelayFromLatestCall`: §8.2 の片側の不等式。
  - **変異検証**(スポット 2 個。各回ビルドの成功を確かめた): `FindPrev` の `>=` → `>`、`break` → `continue`。どちらも `Legacy_paths_match_Matches_reference` で殺された。`continue` の変異を殺せたのは、病的パターン `(?:b(?!a)+?)*` が .NET 9 で減る列を返すため(単調な列なら等価変異になる)。
  - **陰性対照**(項目 28): `Schedule` の `Stop()` を `if (_pending is null)` で無効化すると、再予約から 15〜16 ms で発火して FAIL した。
  - **品質ゲート**: `tools/pre-merge-check.ps1` が EXIT 0。
  - **L5**: 不要(§3.3。SR 経路に触れない)。
  - **レビュー**: 製品コードは 1 ファイル・数行の置き換えなので、CLAUDE.md §3 の簡略化の基準に沿って、最終レビューの 2 パスを別エージェント 1 回に統合した。Critical・Important はなかった。Minor 3 件(デバウンスのテストで Stopwatch を予約の前から始める、xmldoc とテストクラスの summary の更新)は fixup で直した。残りの 1 件は下の申し送りに書いて受容した。
- **本節からの精密化**
  - テスト本体の `Thread.Sleep` はアナライザー(S2925)でビルドが落ちるので、private ヘルパー経由にした(`PreviewUserDataFolderTests.SleepMs` と同じ慣例)。
- **申し送り**(回収先は未定。次に申し送りを回収するときに扱う)
  - `Locate` の「同じ (Index, Length) が複数あれば最後の序数を返す」規則は、.NET 9 では重複する一致を作れないので、テストで確かめられていない(今回は数え方を変えていないので退行の危険はない)。
  - 病的パターン `(?:b(?!a)+?)*` が startat より前の一致を返す挙動は、.NET 10 では起きない(レビューで確認)。ランタイムを上げると、この fixture で `FindPrev` の break 規則を区別する効き目がなくなる。

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

### 9.4 実施記録(2026-10-02)

- **成果物**
  - 項目 27: `AppendBufferGridTests` に、約 3KB の塊を貼り付けて格子点が 3・4 バイト文字の途中に来る形を、元の文字列と照合するテストを足した(位相 4 通り)。前方スナップされた格子点があることを assert して、空振りしないようにした。
  - 項目 16: `TextChunk.SharesBytesWith`(`ReadOnlyMemory<byte>` の等値)を足した。`TextBuffer.Splice` の左マージは、下地が同じでバイトが連続していれば結合し、結合したピースは新しい包み(`first.Chunk`)を採る。右側マージは変えていない(§9.1 の対象外)。
  - 項目 20: `WordBoundary.cs` の実測値コメントを、再計測の値で書き直した。
  - 実装計画は `docs/plans/2026-10-02-append-merge.md`。
- **完了条件**
  - **計測**(`--typing`、NVDA 起動中、3 回の中央値): **0.71 → 0.53 µs/insert、ピース数 247 → 16**。元の設計書 §3.5 のフェーズ 4 の挙動差(追記ブロックで格子点が増えるたびにピースが 1 つ増える)は解消した。打鍵の処理量もフェーズ 4 の前(0.52 µs・16 ピース)に戻った。
  - **項目 20 の再計測**(使い捨てベンチ。500K 字の単一クラス行・cap 128・expand 1 回 = `WordStart` + `WordEnd`。1 ブロックを 64 字刻みで全位相、3 回)
    - 本ブランチ: 貼り付け由来 中央値 0.35〜0.40 ms・p99 0.74〜0.83 ms。ファイル読み込み直後 中央値 0.40〜0.45 ms・p99 2.16〜2.27 ms。
    - main(フェーズ 4 の後・マージなし): 貼り付け由来はほぼ同じ(中央値 0.34〜0.40 ms)。**差を解消したのはフェーズ 4 の格子で、本フェーズのマージではない。**
    - 較正: フェーズ 4 の前(PR #90 のマージの第 1 親)で同じ計測をすると、貼り付け由来は中央値 5.3〜6.3 ms・最大 10.8〜13.0 ms で、ファイル読み込み直後との中央値比は約 15 倍だった。当時の「約 17 倍・22.5 ms」と同じ現象を再現できたが、最大値は当時の値に届かない(当時の位置の採り方は記録がない)。
    - 報告の値には max ではなく p99 を使った。ファイル読み込み直後の max が外れ値(3〜17 ms)で揺れたため。
  - **テスト**: Core に計 9 件を足した(項目 27 の 4 ケース、項目 16 の 4 件、`SharesBytesWith` の 1 件)。既存の 1 件は、期待するピース数を 2 → 1 に変えて置き換えた。
  - **変異検証**(§3.3。各回ビルドの成功を確かめた)
    - 実行者の 5 個(新しい包みの代わりに古い包みを採る、`ReferenceEquals` に戻す、下地の判定を外す、連続の判定を外す、内容の等値にする)は、すべて殺された。
    - 最終レビュー(品質)のスポットチェック 6 個のうち、非等価の 2 個が生き残った。ブロック末尾をまたぐ貼り付けで、新ブロック側の包みを採る変異(テキストが壊れる)と、`newPieces.Count == 1` のときだけ結合する変異である。テストを足して(fixup)、両方とも殺した。残る 1 個(連続の判定 `==` → `>=`)は等価変異。同じ下地の先行ピースの終端は、常に書込前の `_pos` 以下だからである。
  - **品質ゲート**: `tools/pre-merge-check.ps1` が EXIT 0。
  - **L5**: 不要(§3.3)。
  - **レビュー**
    - Task 2 は、タスクの時点で別エージェントの仕様レビューを受けた。承認。Minor 1 件(「Undo の後は結合は起きない」は言い過ぎ)を fixup で直した。
    - Task 1(テストのみ)と Task 3(コメントのみ)は、タスク単位のレビューを最終レビューで兼ねた。
    - 最終レビューは 2 パス(コード品質 / 脆弱性)を別々のエージェントで行った。
      - 脆弱性パス: 指摘なし。並行ストレス(約 9.5 万編集・約 74 万照合)で不一致は 0。連続の判定を外した陰性対照は 10 秒以内に検出された。
      - 品質パス: Important 1 件(上の、ブロックをまたぐ貼り付けのテストがない)を fixup で直した。Minor 3 件のうち、M-1(コメントの値が上の段落と別のベンチであることを書く)を fixup で直した。M-2(計測方法の記録)は本節に書いた。M-3(下地が違うテストに `PieceCount` の assert がない)は受容した。テキストの照合だけで変異を殺せるためである。
- **本節からの精密化**
  - §9.2 の「結合したピースの `Chunk` が、最新の包みと同じ参照であること」: `AppendBuffer` の今の包みを外から見る手段がないので、格子点の列(最新の包みだけが持つ点を含むこと)で確かめた。
  - §9.1 の「`first` は常に最新の包み」: ブロック末尾をまたぐ貼り付けでは、`first` は旧ブロックの最新の包みである(新ブロックの包みは `newPieces[^1]`)。
  - §9.1 の「Undo の後はバイトが連続しないので、結合は起きない」: これが成り立つのは、追記を取り消した場合だけである。それ以外の Undo / Redo の後では結合が起きうる。その場合もバイトが同じブロック上で連続しているので、結合は正しい。
- **申し送り**: なし。

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
| `Composite`・`DiffBounds` | `SkipInvalidateOracleTests` の internal(`ScrollPixelsTests` が 8 回呼んでいる) |
| `OnScreen` | `SkipInvalidateOracleTests.cs:86` の private |

  `MakeHosted`(8 クラス)と `Line()`(3 クラス)は、サイズや引数がクラスごとに違うので寄せない。

- **22**: `EditorControl.Paint.cs` の 131〜344 行(`InvalidateChangedRows` の 2 つの版・`TryPlanScroll`・`IndexOfRow`・`ExposedStrip`・`TestHook_SetPaintSurface`・`InvalidateBands`・`InvalidateImeRows`・`InvalidateRectIfAny`・`InvalidateAndForgetPaintedFrame`)を、`EditorControl.Invalidation.cs` へそのまま移す。使うフィールドは本体(`EditorControl.cs:154-161`)にあるので、純粋な移動で済む。
- **30**: `DeleteWithRetryAsync`(`PreviewUserDataFolder.cs:100-131`)に、internal の任意引数 `onAttemptFailed` を足す。テスト(`PreviewUserDataFolderTests.cs:241-270`)は、1 回失敗した合図を待ってからロックを放す。
- **25**: 製品コードは変えず、`ProbeSaveTargetWithTimeout`(`FileReachabilityProbe.cs:284-307`)の今の結果を、ケースごとにテストで固定する。等価性の網(`FileReachabilityProbeTests.cs:296-307`)は 2 つのプローブの一致しか見ていないので、結果の値そのものを足す。
  - ケース: 「ファイル・ディレクトリ・不在」×「末尾区切りあり・なし」と、NUL 入りの名前。
  - 例: `...\a.txt\` は到達不能、`...\dir\` はディレクトリがあれば到達可能、NUL 入りは親があれば到達可能・不在。
  - 明示的な判定(`Path.EndsInDirectorySeparator(Path.GetFullPath(path))`)を足す案は採らない。`GetFullPath` は NUL で `ArgumentException` を投げて catch に落ち、NUL 入りの結果が「到達不能」に変わる。「末尾区切り = 到達不能」とすると、ディレクトリの場合の結果も変わる。どちらも挙動不変を破る。
  - 到達不能に落ちる理由(`Path.GetDirectoryName` がファイル自身を親として返す性質)を、コメントに書く。
- **26**: `DeleteWithRetryAsync` の最後に `catch (Exception)` を足し、Trace を残して諦める。アナライザーの抑止が要る場合は `docs/lint-format-setup.md` の規約に従う。

### 10.2 完了条件

- 21・22 は、移動の前後でテストの件数と結果が同じであること。
- 30 は、合図を待つ形になったことで、`SleepMs(300)` への依存がなくなっていること。

### 10.3 実施記録(2026-10-02)

- **成果物**
  - 項目 21: `tests/kxEdit.Editor.Tests/Fakes/PaintTestHelpers.cs` を足し、`Pixels`・`PaintPixels`・`PaintRecorded`・`PaintAndAssumeRecorded`・`Rects`・`SameOnScreen`・`DiffBounds`・`Composite`(と private の `OnScreen`)を寄せた。5 つのテストクラスから重複を消した。`tests/README.md` に置き場所を 1 段落で書いた。
  - 項目 22: `EditorControl.Paint.cs` の 131〜344 行を `EditorControl.Invalidation.cs` へ移した。中身は 1 字も変えていない。Paint.cs は 672 → 458 行、Invalidation.cs は 225 行。
  - 項目 30: `PreviewUserDataFolder.DeleteWithRetryAsync` に、テスト用の任意引数 `onAttemptFailed` を足した。`DeleteWithRetry_RetriesAfterFailure` は、失敗の合図を待ってからロックを放す。`SleepMs` は消した。
  - 項目 26: `DeleteWithRetryAsync` のループ全体を `catch (Exception)` で包み、Trace 警告を残して諦める。
  - 項目 25: 製品のロジックは変えず、`ProbeSaveTargetWithTimeout` の結果を 7 通り(ファイル・ディレクトリ・不在 × 末尾区切りの有無と、NUL 入り)のテストで固定した。末尾区切りで到達不能に落ちる理由を、製品のコメントに書いた。
  - 実装計画は `docs/plans/2026-10-02-tidy-up.md`。
- **完了条件**
  - **件数の比較**(項目 21・22): Editor.Tests は、変更前・Task 1 の後・Task 2 の後のどれも 合格 726 / スキップ 0 / 合計 726。
  - **純粋な移動の確認**(項目 22): 移した範囲と新しいファイルの本体を比べて一致した。commit の後(CSharpier の後)にも確かめた。
  - **テスト**: App に計 8 件を足した(項目 26 の 1 件、項目 25 の 7 ケース)。項目 30 は既存の 1 件を書き換えた。App.Tests は 1,106 件すべて合格。
  - **陰性対照**(項目 26): 外側の catch を `when (ex is OperationCanceledException)` で無効化すると、ビルドが成功したうえで `DeleteWithRetry_UnexpectedException_GivesUpWithoutFaulting` が `AggregateException`(`ArgumentOutOfRangeException`)で FAIL した。
  - **項目 25 の期待値**: 計画の時点では pwsh(.NET 10)で実測した。.NET 9 のテストでも 7 ケースすべてが同じ値になった。
  - **品質ゲート**: `tools/pre-merge-check.ps1` が EXIT 0。
  - **L5**: 不要(§3.3)。
  - **レビュー**
    - タスクごとに別のエージェントでレビューした。Task 1 は前倒しのコード品質レビューを、Task 3 は前倒しの脆弱性レビュー(ファイル削除のパス操作に触れるため)を兼ねた。どれも承認で、Critical・Important はなかった。
    - 最終レビューは 2 パス(コード品質 / 脆弱性)を別々のエージェントで行った。Critical・Important はなかった。
    - 品質パスの Minor 2(明示判定を採らない理由に、ディレクトリの結果が変わることも書く)は fixup で直した。
    - 残りの Minor は受容した。主なものは次のとおり。
      - `Pixels` に doc がない(旧コードにもない)。
      - `Oracle_detects_a_missing_rectangle` に `Rects` と同じ採取がインラインで残っている(陽性対照の形を変えないため)。
      - 補助は Fake ではないが `Fakes/` に置いた(§10.1 の前例 `ScreenSurface` と同居させる)。
- **本節からの精密化**
  - 項目 21: `Paint` は、画素を返す版を `PaintPixels(c, record)`、描画を記録するだけの版を `PaintRecorded(c)` に分けた(同じ名前に寄せると record の取り違えが起きる)。呼び出しはクラス名で修飾する(リポジトリに `using static` の前例がない)。
  - 項目 26: §10.1 の「最後に `catch (Exception)`」は、ループ全体を包む外側の catch として実装した。IO 系の catch の中で投げる例外(`Task.Delay` の引数不正・合図)も拾うためである。テストは負の遅延で `Task.Delay` に投げさせる。
  - 項目 30: 合図の引数は `Action`(試行の番号は渡さない)。IO 系の例外で失敗するたびに、諦める回も含めて、待つ前に呼ぶ。
  - 項目 22: 計画の比較コマンド(`sed`・`awk` と `diff`)は、Git Bash で CRLF のファイルに使うと全行が差分になる。両側を `tr -d '\r'` に通して比べた。
- **申し送り**(回収先は未定。次に申し送りを回収するときに扱う)
  - 項目 25 の値の固定は、§10.1 のケースだけにした。等価性の網にある「ファイルの後ろに `\ `・`\. `」の形(正規化で末尾区切りになる)は、値では固定していない。
  - `onAttemptFailed` が例外を投げると、外側の catch が受けてリトライが打ち切られる。テスト専用の引数で、製品(`Dispose`)は渡さない。

## 11. フェーズ 7: フォント(`font-size-and-default`)

### 11.1 項目 4: 20pt が 20.3 pt になる(直す)

- `DisplaySettingsTab`(`:161-162`)で、ダイアログが返した `dlg.Font.Size`(単位は Point)を 0.5pt 単位に丸めて保存する。
- **丸めの規則**: 値を 2 倍して `MidpointRounding.ToEven` で整数に丸め、2 で割る。
  - 96 DPI で FontDialog が返す値は 0.75pt の倍数なので、2 倍すると奇数ピクセルのとき必ず x.5 の中点になる。規則を決めないと結果が決まらない(`AwayFromZero` なら 20.25 → 20.5 になる)。
  - ToEven は、中点で偶数(= 整数 pt)に寄せる。例: 20.25 → 20、11.25 → 11、9.75 → 10、12.75 → 13。
  - 中点でない値はそのまま残る。例: 10.5(14px ちょうど)→ 10.5。
  - 96 DPI では 11pt と 11.5pt が同じ 15px になり区別できないので、11.5pt を選ぶと 11 になる(描画は同じ)。§3.5 に記載済み。
- 丸めは Core の純関数にし、単体テストで確かめる。**中点のケース**(20.25・11.25・9.75・12.75)と、中点でないケース(10.5)を両方入れる(中点でない値だけでは規則を区別できない。CLAUDE.md §4-B)。
- 丸めるのはダイアログで選んだときだけ。すでに設定に保存されている 20.25 のような値は、読み込み時には丸めない(次にダイアログで選び直したときに丸まる)。
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

### 11.3 実施記録(2026-10-02)

- **調査の結論**(項目 5。詳細は実装計画 `docs/plans/2026-10-02-font-size-and-default.md` §0)
  - **定数を 1 つに寄せた**(§11.2 の第 1 の結論)。
  - ctor の既定名を全角にしたビルドで Editor.Tests を流すと、失敗は 0 件(726 件すべて合格)だった。元の設計書 §16.2 の「固定した箇所 231」は、どれも ctor のフォントに依存していなかった。
  - CI(windows-latest)には MS ゴシックがある。ブランチを push したときの CI で確かめた。MS Gothic は Windows の基本のデスクトップフォントセットに入っている(FOD ではない)。
- **成果物**
  - 項目 4: Core に `FontSizeRounding.ToHalfPoint` を足した(2 倍して ToEven で丸め、2 で割る。下限 0.5pt)。`DisplaySettingsTab` は、ダイアログで選んだときだけ丸める(`ApplyPickedFont` に切り出した)。読み込んだ設定値は丸めない。
  - 項目 5: `AppSettings.DefaultFontName` / `DefaultFontSize` を足した。設定の初期値・EditorControl の ctor・`ApplyAppearance` の補完・`DisplaySettingsTab` の既定値が、この定数を使う。
- **完了条件**
  - **テスト**: 計 12 件を足した(Core 9 件・App 2 件・Editor 1 件)。既存の 2 件は、既定値の文字列を定数に書き換えた。
  - **陰性対照**
    - 項目 4: 丸めを外すと、App のテストが 20.25 で FAIL した(ビルドは成功)。
    - 項目 5: 定数だけを足した状態(ctor は半角のまま)で、ctor のテストが Microsoft Sans Serif で FAIL した。
  - **目視**(§11.1): 96 DPI で、設定を 20.25pt にした場合と 20pt にした場合の描画を PrintWindow(900×600)で撮って比べた。差分は 0 画素だった。また、実際のフォントダイアログで 20 を選ぶと、表示とボタンの AccessibleName が「ＭＳ ゴシック, 20 pt」になった(UIA で読み、画面の取得で確かめた)。
  - **品質ゲート**: `tools/pre-merge-check.ps1` が EXIT 0。
  - **L5**: 不要(§3.3。目視だけ)。
  - **レビュー**: 製品コードは数十行だが、複数のファイルにまたがるので、最終レビューは 2 パス(コード品質 / 脆弱性。別々のエージェント)で行った。Critical・Important はなかった。Minor 3 件は fixup で直した。
    - `DisplaySettingsTab` に残っていた 12f を定数にした。xmldoc から箇所の数を消した。
    - 下限のテストの期待値を、定数ではなくリテラルの 0.5 にした(定数が 0 に変わったときも落ちるように)。
    - `ToHalfPoint` が有限の値を前提にすることを xmldoc に書いた(NaN と +∞ はそのまま返る。今の呼び出し元は有限の値しか渡さない)。
- **本節からの精密化**
  - 大きさの既定値(12f)も、名前と同じ定数に寄せた。
  - 丸めの結果に下限 0.5pt を置いた。288 DPI では 1px = 0.25pt が 0 に丸まり、0 は設定の読み込みで 12pt に化けるため。
  - **CI でのフォントの確かめ方**: 最初は `Font.Name` と比べていたが、英語の CI では同じ MS ゴシックが「MS Gothic」を返して FAIL した。`Font.Name` は UI のカルチャの言語で名前を返すためである。計画で「閉じる」に切り替える条件は、Microsoft Sans Serif で落ちること(フォントが無い)だったので、この失敗は条件に当たらない。比べ方を、日本語の LANGID(0x0411)で取り出したファミリー名に変えた。英語 UI で、旧 assert が「MS Gothic」で FAIL し、新 assert が PASS することを確かめた。半角に戻すと、新 assert も Microsoft Sans Serif で FAIL する。
  - 設定ダイアログの「20 pt」表示は、PNG ではなく UIA の名前と画面の取得で確かめた(PrintWindow の対象を名前で探したところ、Windows の設定アプリの窓を拾ったため)。
- **意図的な挙動差**(§3.5 の行に足す)
  - ボタンの AccessibleName の読み上げも「20 pt」になる(SR 経路の外)。
  - `ApplyAppearance` を呼ばずに `new EditorControl` を使う Editor.Tests と Smoke は、Microsoft Sans Serif ではなく MS ゴシック 12pt で走る。製品の挙動は変わらない。
- **申し送り**(回収先は未定。次に申し送りを回収するときに扱う)
  - settings.json の `FontSize` に `1e39` のような値を書くと、System.Text.Json が +∞ に変換する。+∞ は `SettingsStore.Normalize` の `<= 0` の補正を通り抜け、`new Font` が `ArgumentException` を投げる。起動時に落ちると見込まれる(レビューでの .NET 10 の実測。以前からの挙動で、本フェーズでは変わらない)。`Normalize` に `!float.IsFinite` の補正を足す案がある。

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

### 12.4 実施記録(2026-10-02)

- **調査の結論**(詳細は実装計画 `docs/plans/2026-10-02-preview-keys.md` §0)
  - **項目 3**: 実際のキー入力では再現しなかった。WebView2 にフォーカスがあると、主窓のスレッドにキーが届かない。初期化中はプレビューの `ProcessCmdKey` に届くが、ToolStrip のルート HWND の照合で弾かれる。再現したのは、無効化された主窓を外部から `SetForegroundWindow` で前面化した場合だけだった。キーは主窓に直接届き、プレビューが入れ子で開いた。経路はプレビューのフォームを通らないので、§12.2 の第 2 案(主窓側のガード)を採った。Alt+Tab とタスクバーのクリックで前面に来るのはプレビューだった。§4.3 の「WebView2 から `ProcessCmdKey` への参照はない」は正しかった。
  - **項目 2**: WebView2 にフォーカスがあると、Alt+C はフォームの `ProcessCmdKey`・`ProcessDialogKey`・ニーモニックのどれにも届かない。WinForms の WebView2 は `AcceleratorKeyPressed` を自分の `KeyDown` に変換し、`Handled` を書き戻す(IL と実測で確認)。§12.2 の「届く方を override する」は当たらないので、`_web.KeyDown` で拾う形にした。
- **成果物**
  - 項目 3: `MainForm.ProcessCmdKey` の先頭に、主窓が Win32 で無効(`NativeMethods.IsWindowEnabled(Handle) == false`)ならキーを食うガードを置いた。`ShowMarkdownPreview` の先頭に再入ガード(`_previewShowing`。`try/finally` で戻す)を入れた。
  - 項目 2: `MarkdownPreviewForm` が `_web.KeyDown` を購読し、`KeyData == Alt+C` の完全一致で `Handled = true` にしてから閉じる。`OnNavCompleted` の `_web.Focus()` は残した。注入スクリプトは変えていない。
- **完了条件**
  - **テスト**: App に計 8 件を足した。
    - `MainFormModalGuardTests` 3 件。テストの中で実際に `ShowDialog` を開き、その表示中にキーを渡す。モーダルを閉じた後に同じキーが効くことの陽性対照と、CSV モードの文書を使う再入ガードのテストを含む。再入ガードのテストは、ガードの退行で `ShowDialog` に入らず、固まらずに赤くなる。
    - `MarkdownPreviewFormKeyTests` 5 件。Handle だけを作り、`_web` の `OnKeyDown` を呼ぶ。Ctrl+C・素の C・Alt+Shift+C・Alt+X では閉じない。
  - **陰性対照**: TDD の赤で取った。いずれもビルドの成功を確かめたうえで、次の形で FAIL した。
    - 主窓のガードなし: Ctrl+Shift+Tab でタブが a に移った。
    - 再入ガードなし: `BlockedInCsvMode` が発声された。
    - Alt+C の処理なし: `Handled` が false だった。
  - **品質ゲート**: `tools/pre-merge-check.ps1` が EXIT 0。
  - **L5**: `tools/sr-regression.ps1` が EXIT 0。NVDA の実機で次を確かめ、すべて PASS した(SendInput の実キー入力・スピーチビューアーと UIA で採取)。
    1. 表示直後は、ダイアログの名前に続いて本文が読まれる。
    2. Alt+C で閉じ、エディタにフォーカスが戻る(「本文 ドキュメント …」)。メニューバーは活性化しない。
    3. Esc と「閉じる」ボタンでも閉じる。
    4. プレビューの表示中に送った Ctrl+Shift+U・Ctrl+W・Ctrl+S では何も起きない。
    5. 無効な主窓を `SetForegroundWindow` で前面化してから Ctrl+Shift+U・Ctrl+W・Alt+F4 を送っても、何も起きない(修正前は入れ子で開いた)。
  - **レビュー**
    - Task 1: 仕様と品質のレビュー。
    - Task 2: 仕様と品質のレビューに加えて、前倒しの脆弱性レビュー。
    - 最終レビュー: 2 パス(コード品質 / 脆弱性。別々のエージェント)。
    - Critical・Important はなかった。Minor のうち、コメントの網羅性の過大表現と、テストのコメントの紛らわしさの 2 件は fixup で直した。あわせて、モーダルの表示中に主窓が無効であることの前提 assert を足した。残りは受容した(PR に記載)。
- **本節からの精密化**
  - 項目 2 は override ではなく `_web.KeyDown` で拾う(上の調査の結論)。
  - 項目 3 のガードの条件は「主窓が Win32 で無効」にした。実装計画 §0.4 は効く範囲を「検索と置換・grep を含むすべてのモーダル」と書いたが、正しくは次の 2 つである(最終レビューの M-1)。
    - `ShowDialog` と `MessageBox` で開くモーダル
    - モードレスの窓から開くモーダル(`ShowDialog` はスレッドのすべての窓を無効にする)
    - 検索と置換・grep のダイアログ・grep の結果一覧はモードレスで、主窓を無効にしないので、ガードは効かない(挙動も変わらない)。
  - 主窓のガードのテストは、実装計画の Ctrl+Tab ではなく Ctrl+Shift+Tab を使った。起動時の無題のタブが先頭に残り、Ctrl+Tab は b から折り返して無題のタブへ移るので、陽性対照が成り立たないためである。
- **意図的な挙動差**(§3.5 の 2 行に足す)
  - 主窓を外部から前面化された場合などに、主窓へ届くキーをモーダルの表示中はすべて食う。対象はショートカットに限らない。Alt によるメニューの活性化と Alt+F4 も効かなくなる(最終レビューの M-2・L5 の 5 で確認)。通常の操作では、モーダルの表示中に主窓へキーは届かない。
- **申し送り**(回収先は未定。次に申し送りを回収するときに扱う)
  - Esc も `_web.KeyDown` に届く。Esc の処理を C# の `KeyDown` に寄せれば、注入スクリプトと `WebMessageReceived` による閉じる経路(WebMessage の面)をなくせる。挙動の変更を伴うので、本フェーズの範囲外とした。
  - (既存)UIA の Invoke でメニュー項目を実行すると、`ProcessCmdKey` を通らない。モーダルの表示中でも、プレビュー以外のモーダル(設定・開く・grep など)は入れ子で開きうる。同じ整合性レベルのプロセスに限られる。回収案は、モーダルを開く入口に共通のガード(`IsWindowEnabled(Handle)` の判定)を置くこと。
  - (既存)別のプロセスから投げられた `WM_CHAR`・`WM_SYSCOMMAND`・`WM_CLOSE` は `ProcessCmdKey` を通らない。同じ整合性レベルのプロセスに限られる。記録だけ残す。

## 13. フェーズ 9: 長大行の描画コスト(`long-line-paint-cost`)

**目的**: `RowPaintKey.Text`(17)と `FrameRowCache` の保持(19)が長大行で問題になるかを測り、直すか閉じるかを決める。18 の計測も同じセッションで行う。

### 13.1 調査

- Smoke `--perf` に、長大行の打鍵シナリオ(1M 字の 1 行・折り返し OFF)を足す。シナリオは結論によらず残す。
- 1 回の打鍵で、`RowPaintKey` の本文の取り出し(`FrameDiff.cs:52-55`)と比較にかかる時間を、描画全体と比べる。参考: 1 回の描画では、すでに行全体を 3〜4 回取り出している(`ViewportLayout.cs:66`・`FrameBuilder.cs:253,286,424`)。
- **18**: dotnet-trace で、en と ja の S1・S3 を採る。en の S1(描画 0 回で 0.55 ms。ja は 0.08 ms)の固定費を主な対象にする。結果を記録するだけで、コードは変えない。

### 13.2 判断の基準

- キーの本文にかかる時間が、打鍵 1 回の描画全体の **1 割を超える**なら直す。超えなければ、理由を付けて閉じる。
- 直し方: キーを `(スナップショットの参照, 開始, 長さ)` にし、本文を無条件に取り出さない(今は `FrameDiff.cs:52-55` が比較の前に行全体を取り出している)。比べる手順は次のとおり。
  1. スナップショットが同じ参照なら、範囲の一致だけで等しいと判定する(スクロールや選択の変化)。
  2. 参照が違えば、まず長さを比べ、違えば「違う」と判定する(打鍵で行が伸びた場合)。
  3. 長さが同じときだけ、本文を遅延して取り出して比べる。
  - 打鍵ではスナップショットが毎回新しい参照になるので、1 の短絡は打鍵には効かない。打鍵で効くのは 2 と、変わっていない行での 3 の費用。
- 19 も一緒に解消する(キーが本文を持たなくなるため)。
- **完了条件(直す場合)**: 長大行の打鍵シナリオで、キーの本文にかかる時間が実際に下がったことを測って確かめる。下がらなければ、変更を取りやめて閉じる。

### 13.3 実施記録(2026-10-07)

- **調査の結論**(詳細は実装計画 `docs/plans/2026-10-06-long-line-paint-cost.md` §0)
  - 長大行の打鍵シナリオ S11(1M 字の 1 行・折り返し OFF)を Smoke `--perf` に足して測った。1 打鍵は、英字で 135 ms、日本語で 2.7 秒。行の記述子の本文の取り出しは、英字 0.19 ms(0.14%)・日本語約 1 ms(0.04%)だった。比較(`DirtyBands`)は 0.002 ms で、打鍵で行の長さが変わるので文字列の比較は長さの段階で終わる。
  - **17 は閉じる**: §13.2 の基準(描画全体の 1 割)を大きく下回る。
  - **19 は閉じる**: 古い件が握るのは可視行の本文 1 部(1M 字の行で 2MB)。一方、描画の経路は 1 打鍵ごとに行全体の文字列を 6〜7 部作っている。
  - **18 の内訳**(記録のみ。コードは変えない): en の S1・S3 の固定費の最大は、UIA イベントの発火(`RaiseAutomationEvent`。NVDA が購読しているため)だった。S1 で 7 割強、S3 で 3 割弱を占める。ja と en の差もほぼこれで説明できる(S3 では差 943 ms のうち 620 ms、S1 では差 350 ms のうち 300 ms。いずれも 2,000 回の合計)。表は実装計画 §0.3。
- **調査で分かったこと**: 長大行の打鍵が重いのは、記述子ではなく描画の本体だった(`DrawText` 約 6 割・区切りごとの `MeasureRun` 5 割強)。F-1 の案 A は、16,384 字ごとの区切りを全部測って全部描く。あわせて、GDI の `MeasureText` は 43,679 字を超える文字列にエラーを出さずに幅 0 を返す。そのため、非 ASCII の長い行では、横スクロールバーが出ない・キャレットの X が 0 になる・クリックが行末に飛ぶ不具合があった(晴眼・弱視ユーザーは最初の 1 画面より右を見られない)。空白の表示も、長い行では行の長さの 2 乗のコストだった。
- **ユーザーの決定**(2026-10-06): F-1 の案 B を、座標の修正と合わせて本フェーズで行う。§1 の範囲外の記述(F-1 の案 B は含めない)は、この決定で上書きした。新しい抽象を伴うので、§3.1 に従い新しい設計書 `docs/plans/2026-10-06-long-row-geometry-design.md` に書いた。
- **成果物**(PR に記載)
  - Smoke `--perf` の S11(結論によらず残す)。
  - `ICharMetrics.MeasureAdditive`(コードポイント幅の足し算。`GdiCharMetrics` は BMP の幅を配列で引く高速版)。
  - `PixelMapper` の長い行(16,384 字超)の経路: 座標を足し算で求める。`RowWidthPx`・`SliceForWindow` を新設。横スクロールバーの幅は `RowWidthPx` で求める。
  - `FrameBuilder.Build` の横の窓: 長い行は、窓にかかる文字だけを本文と空白のグリフにする。本文はタブで区切り、各区間を足し算の X に置く。
- **完了条件**
  - **計測**(Smoke・NVDA 起動中・3 回の中央値): S11a は英字 126.8 → 2.45 ms、日本語 2,847 → 8.6 ms。S11b は英字 127.4 → 2.61 ms、日本語 2,831 → 8.5 ms。S3・S7(ja10k・en10k)は、ja の S3 の中央値が変更前の範囲の上限を 0.01〜0.05 ms 上回った。それ以外は変更前の揺れの範囲に収まった。短い行の経路で増えたのは行ごとの長さの比較 1 回だけなので、揺れと判断した(数値は実装計画の実施記録)。
  - **テスト**: Core と Editor にテストを足した。日本語 5 万字の行の Editor のテストは、修正前に赤だった。
  - **変異検証**(スポット 2 個・§4-A の有効側): `IsLongRow` の境界(`>` → `>=`)と、長い行の `PxToOffset` の境界(`>=` → `>`)。どちらも、ビルドの成功を確かめたうえで殺された。
  - **品質ゲート**: `pre-merge-check` は EXIT 0。`--paint-transition --expect-skip` は EXIT 0(25 遷移)。
  - **L5**: `sr-regression` は EXIT 0。実アプリ(publish)で PrintWindow によって目視し、すべて PASS した。
    - 日本語 5 万字の行で横スクロールバーが出る。End で行末(桁 50001)が見える。
    - 行末の選択矩形が文字と一致する。クリックした文字の直後にキャレットが来る。
    - 黒地テーマと空白の表示で、空白・タブのグリフが正しい位置に出る。タブをまたぐ選択の矩形が文字と一致する。
    - NVDA で、長い行の末尾で ← を押すと 1 文字ずつ正しく読み上げる。
    - NVDA のハイライト矩形は確かめていない(ユーザーの実機確認に回す)。
  - **レビュー**
    - タスクごとのレビュー: Task 2・3 は前倒しのコード品質レビューを兼ねた。
    - L5 の再確認後のスコープを絞った再レビュー。
    - 最終レビューは 2 パス(コード品質 / 脆弱性)で、パスごとに別のエージェントが行った。指摘(Important 1 件: 足し算の幅の int の溢れ。ほかに Minor)は fixup commit で直した。
- **本節からの精密化**
  - §13.2 の「キーを `(スナップショット, 開始, 長さ)` にする」直し方は採らなかった(17 を閉じたため)。
  - L5 で、タブを含む長い行の不整合を見つけ、同じブランチで直した。GDI はタブを幅 0 で測って描くが、足し算はスペース幅で数える。本文をタブで区切り、各区間を足し算の X に置くことで解消した。タブはスペース幅の空きとして描かれる(`GdiCharMetrics` の doc「TAB は半角スペース幅」と一致)。
- **意図的な挙動差**(§3.5 の表に足す行。設計書 §3.6)
  - 9: 16,384 字を超える視覚行の座標は、コードポイント幅の足し算になる。既定のＭＳ ゴシックでは一括計測と同じ値。比例フォントや結合文字では数 px ずれうる。
  - 9: 43,679 字を超える非 ASCII の行で、横スクロールバー・キャレット・クリック・選択矩形・上下移動・UIA の矩形が正しく動くようになる(不具合の修正)。
  - 9: 長い行のタブは、スペース幅の空きとして描かれる。短い非 ASCII の行では従来どおり幅 0 で描かれる(GDI の挙動)。
- **申し送り**
  - 設計書 §6(行全体の `GetText` の回数、約 800 万字を超える全角の行での GDI の座標の範囲、窓の中の足し算の座標とグリフのずれ)。
  - (既存・要確認)短い ASCII だけの行のタブは、`MeasureRun` ではスペース幅で測るが、GDI はタブを幅 0 で描くはずで、キャレットがずれうる。本フェーズは短い行を変えないので扱っていない。
  - S11 の数値は、行頭付近で打鍵したときの値である。1M 字の行の末尾では、行頭からの足し算の walk が 1 フレームに数回残る(変更前の費用よりは桁違いに小さい)。測っていない。
  - (最終レビュー M-1)幅 0 の文字(U+200B など)が続くと、`SliceForWindow` が窓の右端に届かず、行の全体を描く。変更前も長い行は全体を描いていたので、劣化ではない。
  - (最終レビュー M-2)折り返し OFF で、別個のコードポイントが約 110 万種ある行を開くと、初回は新しいコードポイントごとに GDI で 1 回測る。折り返し ON の既存の費用と同じ種類である。
  - レビューで受容した Minor は PR に記載する。

## 14. フェーズ 10・11: 実機での調査

### 14.1 フェーズ 10: SR の実機調査(`sr-investigation`)

ユーザーの立ち会いのもとで、NVDA の実機セッションを 1 回行う。

- **6(say all が止まる)**: 次の 3 つで切り分ける。
  - 同じ文書を折り返し OFF で試す(最も効く切り分け)
  - メモ帳の折り返しで試す
  - NVDA の debug ログと、provider の呼び出し列のトレース(scratchpad の計測ビルド)を採る
  - 候補は、NVDA の say all の継続条件と `Select()` やイベントとの干渉、Paragraph を視覚行として返すこと(`TextRangeProviderV2.cs:47-95`)、NVDA 側の問題。
  - 結論: 原因が kxEdit 側で、修正が小さければ直す。Move や Expand の意味を変えるなど大きければ、新しい日付の設計書に書く(§3.1)。NVDA 側なら閉じる。Move や Expand を直す場合は、§4-A の変異検証の対象になる。
- **7(フォーカス移動の二重化)**:
  - `TabControl.SelectedIndex` のセッターが、発声(`KeyBasedSwitch`)より先にエディタへフォーカスを移している疑いが強い(`DocumentManager.cs:168-206`)。後の `FocusActiveEditor()` は 2 回目の no-op になる。
  - スピーチビューアーで発声の順序を確かめる。App.Tests でも、GotFocus と KeyBasedSwitch の発火順を記録する。
  - 崩れていれば、移動先の文書を先に求め、`KeyBasedSwitch` を `SelectedIndex` の変更より前に発火させる(10〜20 行とテスト)。

### 14.2 フェーズ 11: 破棄時の競合(`dispose-race-investigation`)

- 現状: Smoke 側は `PaintSnapshot.CloseQuietly` で吸収済み。`GdiBench.cs:86` は未対処。これまでの再現は 0/30。
- 製品で同じ形になる箇所: `using` + `ShowDialog` のダイアログ群、タブを閉じる処理(`DocumentManager.cs:157-160`)、モードレス窓(`GrepResultsWindow`・`MarkdownPreviewForm`)。仮説どおりなら、UI スレッドの `Dispose` から例外が出て、クラッシュダイアログになりうる。
- 調査:
  - NVDA 起動中に、ダイアログとタブの開閉を数百回繰り返すハーネスを作る。
  - first-chance 例外と、RPC スレッドで `CreateHandle` が走ったときのスタックを採る。
  - `ShowDialog` が戻った時点での Handle の状態と、WinForms 標準のアクセシブルオブジェクトが本当に RPC スレッドで呼ばれるかを確かめる。
- 結論:
  - **再現しない**: `GdiBench` に `CloseQuietly` を適用して閉じる。
  - **再現する**: 共通の基底で、UI スレッド以外からの `CreateHandle` をガードする設計を、新しい日付の設計書に書く(§3.1)。ユーザーの承認を得てから実装する。

### 14.3 実施記録(2026-10-07)

- **調査の結論**(詳細は実装計画 `docs/plans/2026-10-07-dispose-race-investigation.md` の実施記録)
  - **再現しない**(§14.2 の第 1 の結論)。**項目 9 は閉じる**(§15)。
  - NVDA 2026.2jp の起動中に、scratchpad の調査用ハーネス(コミットしない)を Win+R で起動し、`all 300` を 5 回走らせた。製品の窓は dialog 1,500 回・tabs 1,500 回・grep 1,500 回・preview 250 回、汎用 Form は 2 つの形で各 1,500 回、開閉した。製品のシナリオで、`Dispose` の失敗と UI スレッド以外での WinForms の窓の生成はどれも 0 件だった。前面率はすべて 100% だった。汎用 Form でも、`Close` の後の二重の `Dispose`、UI スレッド以外での `CreateHandle`、first-chance 例外は 0 件だった。陽性対照(別スレッドで Handle を作る)では 3 つの検出器がすべて発火した。
  - `ShowDialog` が戻った時点で、Handle は全回で破棄済みだった(dialog・preview とも 100%)。`using` の末尾の `Dispose` は Handle の破棄を伴わない。
  - 言えるのは「NVDA 起動中のこの条件では症状が出なかった」までである。§14.2 の「WinForms 標準のアクセシブルオブジェクトが本当に RPC スレッドで呼ばれるか」は確かめていない。NVDA の問い合わせ(WM_GETOBJECT)が窓に届いていたことは確かめたが、その後のメソッドの呼び出しスレッドと、問い合わせと破棄の重なりは測っていない。仮説は確かめられても否定されてもいない。
  - **判定の限界**(詳細は実装計画の実施記録「限界」)
    - 計画の検出器は、UI スレッド以外で作られてすぐ壊れた窓を取りこぼしうる(所有スレッドを引けず、WinForms 以外に分類される)。この経路は、追加走行で WinEvent の生成スレッドを採って塞いだ。判定はこの診断に依っている。
    - grep は破棄の経路を通っていない。製品の grep の結果一覧は、閉じると隠すだけで 1 枚を使い回す(`GrepResultsWindow.OnFormClosing`・`GrepController`)。grep の 0 件は表示・非表示の開閉の確認にとどまる。
    - tabs は証拠が弱い。新しいタブのエディタに問い合わせが届いたのは 40/300 回だけで、WinForms 標準の経路(TabPage・TabControl)には計測を掛けていない。
    - 問い合わせには UIA と MSAA の両方が含まれていた可能性がある(分類が不確か)。送り手が NVDA だとは断定できない。
- **成果物**
  - `GdiBench` の後片付けを `PaintSnapshot.CloseQuietly` にした。form と editor の `using` を外し、finally で 1 度だけ閉じる(Smoke だけ。製品コードは変えていない)。
  - ハーネスはコミットしない。手順・条件・集計は実装計画の実施記録に残した。
- **完了条件**
  - **調査**: 本走 2 回(計画の Step 6)と、診断を足した追加走行 3 回。どれも前面率 9 割以上で、判定不能の条件に当たらない。
  - **Smoke**: `--bench --mb 16` が例外なく終わった(平均 7.50 ms・PASS。破棄の競合の警告も出ない)。
  - **品質ゲート・最終レビュー**: `tools/pre-merge-check.ps1` と別エージェントの最終レビューを行い、結果は PR に記載する。
  - **L5**: 不要(§3.3)。
  - **変異検証**: 行わない(§3.3)。
- **本節からの精密化**
  - §14.2 は `MarkdownPreviewForm` をモードレス窓に分類しているが、製品は `using` + `ShowDialog` で開く(`MainForm.ShowMarkdownPreview`)。調査は製品の形(モーダルで開いて `Dispose`)で行った。
  - 「ファイル名を指定して実行」の入力欄は 259 文字で切れるので、実装計画の Step 5 のコマンド(exe とログの両方をフルパス)はそのままでは使えない。ログのパスを短い相対パスにした。
  - 計画の検出器に、診断(WinEvent の生成スレッド・前面の窓が対象の窓か・WM_GETOBJECT の到着)を追加走行で足した。開閉の形は変えていない。
- **意図的な挙動差**: なし(Smoke の後片付けのみ)。
- **申し送り**(回収先は未定。次に申し送りを回収するときに扱う)
  - MainForm の終了時に、モードレス窓(grep の結果一覧)が所有者とともに破棄される経路は試していない。
  - 仮説の仕組み(WinForms 標準のアクセシブルオブジェクトのメソッドが RPC スレッドで呼ばれるか、問い合わせが破棄と重なるか)を確かめるなら、`CreateAccessibilityInstance` を上書きして呼び出しスレッドを数え、TabPage・TabControl にも計測を掛ける。
  - 同種の調査で UI スレッド以外の窓を数えるときは、所有スレッドではなく WinEvent の生成スレッド(`evThread`)で判定する。

## 15. 閉じる項目と理由

| 項目 | 理由 |
|---|---|
| 9 | NVDA 起動中に製品の窓を開閉し(ダイアログ 1,500 回・タブ 1,500 回・プレビュー 250 回は `Dispose` まで。grep の結果一覧 1,500 回は表示と非表示だけ)、`Dispose` の失敗も UI スレッド以外での WinForms の窓の生成も 0 件だった。この条件で症状が出なかったことまでで、仮説の仕組みは確かめていない。MainForm の終了時の grep の結果一覧の破棄は未検証(実施記録 §14.3)。Smoke 側は `CloseQuietly` で吸収する |
| 11 | `_topSegment` への書き込みを全数確認し、到達しないと判断した。0 に戻す箇所は `EditorControl.cs:274,340,926,990,1556,2859`。`SetTopPosition` に折り返し OFF で 0 以外が渡る経路はない(EOL 変換は同じモードで保存した値を戻すだけ、ホイールは TopLine のセッターに委ねる、`Caret.cs` のセグメントは OFF では常に 0)。保険の丸めはフェーズ 2 で入れる |
| 23 | 冗長であることは、フェーズ 9 の Task 5 とレビューの故障注入で確認済み。不変条件 2(自分で無効化する)の例外を作らないための防御で、費用はほぼない(スクロールしなければ `Equals` で即座に return する) |
| 24 | 利用者は今も 2 つ(`PaintSnapshot.cs`・`PaintTransition.cs`)。「3 つ目の利用者が出たら共通の型へ」という条件を満たしていない。一部(`BuildBody`・`ReadPixels`・`CloseQuietly` など)はすでに共有している |
| 29 | 先頭の破棄が単独で効くのは、RPC スレッドが `ApplyAppearance` の途中で問い合わせたときだけ。キーの照合により、そのときの答えも「前の答え」か「ミス」のどちらかになる。外から観測できず、テストには製品側に途中で止める seam が要る |
| 31 | trx を使っていない(ci.yml・release.yml・`pre-merge-check.ps1` はどれも既定のロガー)。trx を導入するときの確認項目として残す |
| 32 | 横方向の画素移動は、`ScrollPixelsTests`(Form を表示して合成比較)と Smoke が受け持っている。欠けているのは、横スクロールと選択・編集がランダムに組み合わさる場合だけ |
| 33 | 回収済み。`2026-09-27-perf-partial-paint.md:2554`(Task 6)に記録がある |
| 34 | 本文では起こらない。バッファは挿入の時点で単独サロゲートを U+FFFD に置き換え(`AppendBuffer.cs:44` の `Encoding.UTF8.GetBytes`。説明は `TextBuffer.cs:289` のコメント)、読み込みでも `Utf8Sanitizer` が置き換えるので、バックアップとファイル保存に差はない。`BackupRecord.OriginalPath` に単独サロゲートが入る可能性(NTFS はそういうファイル名を許す)は本文とは別の問題なので、本書の PR をマージした後に Issue として起票する |
