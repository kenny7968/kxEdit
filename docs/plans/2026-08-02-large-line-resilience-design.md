# 巨大 1 行 / 長大トークン耐性 設計書

策定日: 2026-08-02 / 調査ブランチ: `feature/large-line-triage`

調査計画は `docs/plans/2026-08-02-large-line-triage.md`、調査の設計は
`docs/plans/2026-08-02-large-line-triage-design.md` を参照。

## 1. 背景・目的

(Task 5 で記述)

## 2. 現状調査結果(2026-08-02)

### 2.1 「ファイルを開く」経路の呼び出し回数(Task 1・静的読み)

App 層のファイルオープンは `FileController.cs:209` の `SetOrReplaceSource` を通る。
新規タブなら `SetSource`(`EditorControl.cs:209-234`)、開き直しなら
`ReplaceSource`(`:265-299`)へ振り分けられる。**両者とも次の 3 経路を呼ぶ。**

| # | 箇所 | 契機 | 1 回のオープンでの呼び出し回数 | 巨大 1 行でのコスト | 折り返し |
|---|---|---|---|---|---|
| 1 | `UpdateHorizontalScrollbar`<br>(`EditorControl.cs:1060-1105`) | `SetSource` / `ReplaceSource` | 1(以後リサイズ・設定変更ごと) | `ViewportLayout.Build(wrapColumns: 0)` の各行に対し `GetText`(**行全体 = 500K 文字の string**)→ `MeasureRun` へ**一括投入** | **OFF のみ**。ON は `:1062` で即 `HideAndResetHScroll` |
| 2 | `ComputeCaretPoint`<br>(`EditorControl.cs:1517-1584`) | `PositionCaret`(フォーカス時) | 1(以後**キャレット移動のたび**) | `GetText`(行全体)+ `LineLayout.Wrap` | 両方(ON が重い) |
| 3 | `OnPaint` → `ViewportLayout.Build`<br>(`EditorControl.Paint.cs:34`) | `Invalidate()` 後の描画 | **フレームごと(キャッシュなし)** | `ViewportLayout.cs:53-55` で `GetText`(行全体)+ `Wrap` | 両方 |
| — | `UpdateVerticalScrollbar`<br>(`EditorControl.cs:1030-1049`) | 同上 | 1 | `LineCount` を見るだけ = **O(1)。無関係** | — |
| 4 | `_uia.RaiseTextChanged()`<br>(`EditorControl.cs:294`) | `ReplaceSource` 末尾のみ | 1 | SR がこれを受けて UIA 経路で読みに来る(`UiaTextHostAdapter.cs:482` の `Wrap`) | 両方 |

**したがって 1 回のファイルオープンで、論理行全体を舐める処理が最低 3 回走る**
(折り返し OFF なら 1・2・3、ON なら 2・3 が各 1 回 + 以後フレームごとに 3)。

#### キャッシュの有無

**`OnPaint` の `ViewportLayout.Build` にキャッシュはない。** `EditorControl.Paint.cs:31-34`
のコメントは「ローカルへ 1 度だけ受ける」= 同一フレーム内で 2 箇所が同じ値を使うことの保証で
あって、フレームをまたぐキャッシュではない。`ComputeCaretPoint` にもキャッシュはない。
UIA 側の `_lastLineSegs` だけがキャッシュだが、`OnSnapshotChanged` で破棄される。

#### 折り返し ON と OFF で主犯が異なる

設計書 §9.8(PR #33)は主因を「折り返し ON × 改行なし巨大 1 行」と推定していたが、
コードを読むと **ON と OFF で別の経路が重い**。

- **OFF**: 経路 1 が効く。`LineLayout.Wrap` は `maxWidthPx <= 0` で単一セグメントを即返す
  (`LineLayout.cs:35-36`)ためループしないが、`EditorControl.cs:1079-1080` が
  **500K 文字の string を作って `MeasureRun` に一括投入**する。`GdiCharMetrics.MeasureRun`
  は非 ASCII を 1 文字でも含むと `text.ToString()`(span からの**追加コピー**)+
  `TextRenderer.MeasureText` へ落ちる(`GdiCharMetrics.cs:43-46`)。
- **ON**: 経路 2・3 が効く。`LineLayout.Wrap` は**1 コードポイントごとに `MeasureRun` を呼ぶ**
  (`LineLayout.cs:50-52`)ため、非 ASCII なら GDI 呼び出しが文字数分だけ発生する。

#### 既存の実測コメントが桁を裏づけている

`EditorControl.Caret.cs:408-415` に、PR #29(UIA ScrollIntoView)のレビュー時の実測が
既にコメントとして残っている。

> `Wrap` は非 ASCII で code point ごとに GDI `MeasureText` を呼ぶため、折り返し ON の
> CJK 長行では 1 回で秒オーダーになる(レビュー実測: **20,000 文字の CJK 単一論理行・
> `WrapColumns=80` で 1,584 ms**)。

**20,000 文字で 1,584 ms。線形と仮定すると 500,000 文字で約 39.6 秒/回**になる。
経路 2・3 が各 1 回走るだけで約 79 秒、描画が数フレーム走れば **240 秒の桁に届く**。
§9.8 のハングは、この既知のコストが「巨大 1 行 × 複数経路 × キャッシュなし」で
積み上がった結果である可能性が高い。

**ただしこれは静的読みと既存コメントからの推定にとどまる。** 線形性の確認と
文字種による分岐の確定は Task 2(構造コスト)・Task 3(GDI 込み)の実測で行う。

#### Task 2 / Task 3 への申し送り

1. **測定は `SetOrReplaceSource` を使う。** 実装計画の Smoke ベンチは `SetSource` と
   書いたが、App 層の実経路は `SetOrReplaceSource`(`FileController.cs:209`)であり、
   `ReplaceSource` は追加で `_uia.RaiseTextChanged()` と `UpdateUI` を発火する。
2. **ベンチで明示的に `editor.Focus()` を呼ぶ。** 経路 2(`ComputeCaretPoint`)は
   `_hasFocus` が false だとスキップされる(`EditorControl.cs:281-284`)ため、
   フォーカスを与えないと主犯の 1 つを測り落とす。
3. **折り返し ON / OFF は必ず両方測る。** 上記のとおり主犯が異なる。

### 2.2 構造コスト(GDI 抜き・Task 2)

`tests/yEdit.Core.Bench --largeline`。`MonoCharMetrics`(固定幅)で `ViewportLayout.Build`
を測る = GDI を経路から外した基準線。可視 40 行相当・`topLine = 0`。

| 文字種 | 100K 文字 | 500K 文字 | 2M 文字 | 伸び(100K → 2M) |
|---|---|---|---|---|
| ascii / wrap OFF | 0.2 ms | 0.2 ms | 1.3 ms | ×6.5 |
| ascii / wrap 80 | 0.5 ms | 1.5 ms | 6.5 ms | ×13 |
| cjk / wrap OFF | 0.3 ms | 1.6 ms | 7.7 ms | ×26 |
| cjk / wrap 80 | 0.7 ms | 3.4 ms | 13.2 ms | ×19 |
| mixed / wrap OFF | 0.7 ms | 3.4 ms | 14.7 ms | ×21 |
| mixed / wrap 80 | 1.4 ms | 6.8 ms | **29.7 ms** | ×21 |

**行長 20 倍に対して伸びは 6.5〜26 倍 = 線形。O(n²) ではない。**
そして**最悪でも 29.7 ms** であり、§9.8 の 240 秒とは **4 桁違う**。

#### 結論: 240 秒ハングは構造由来ではない

`ViewportLayout.Build` / `LineLayout.Wrap` / `TextSnapshot.GetText` というデータ構造側の
アルゴリズムは、巨大 1 行に対して素直に線形でミリ秒級に収まっている。
**したがって主因は `ICharMetrics` の実装差 = `GdiCharMetrics` の GDI 呼び出しである。**
同じ `Wrap` を `MonoCharMetrics` から `GdiCharMetrics` に替えるだけで 4 桁増える計算になり、
これは §2.1 の既存コメント(20,000 文字 CJK・`WrapColumns=80` で 1,584 ms)と整合する。
Task 3 で GDI 込みの実測を取り、この推論を確定させる。

なお本ベンチは `MonoCharMetrics` を使うため**文字種で分岐しない**。上表の文字種差
(ascii < cjk < mixed)は `MeasureRun` ではなく `GetText` の UTF-8 デコードコスト
(1 バイト / 3 バイト / 混在)に由来する。

#### ファイル読み込み経路は無害

Smoke ベンチは `TextBuffer.FromString` で文書を作るため、「ファイルを開く」の前半
(読み込み・エンコーディング判定・行末検出)を測っていない。同ベンチで
`TextFileService.LoadAsBufferAuto` を実ファイルに対して測った。

| 文字種 | 100K 文字 | 500K 文字 | 2M 文字 |
|---|---|---|---|
| ascii | 13.9 ms | 12.0 ms | 15.1 ms |
| cjk | 10.0 ms | 10.1 ms | 16.8 ms |

**全条件 10〜17 ms でほぼ一定。** 長大 1 行でも `EncodingDetector` /
`TextBufferBuilder` は非線形にならない。**読み込み側は主因ではない。**

これは「§9.8 の fixture が ASCII だった場合」の代替仮説を 1 つ潰す意味がある。
ASCII 500KB を折り返し ON で開いた場合の合計は Load 12 ms + setSource 6.2 ms +
paint 15.5 ms ≒ **34 ms** にしかならず、240 秒とは 4 桁違う。

#### F-6 は実測で裏づけられた

同ベンチの後半で `AppendBuffer` 現ブロック経路(`TextBuffer.FromString` ではなく
1 文字ずつ `Insert` して育てた文書)を測った。70,000 文字 / ピース数 2。

| 位置 | `GetChar` |
|---|---|
| pos = 8,750 | 13,799 ns/回 |
| pos = 26,250 | 34,009 ns/回 |
| pos = 61,250 | **79,380 ns/回** |

**位置にほぼ比例して増える**(ブロック先頭からの線形走査そのもの)。比較のため、
PR #33 §9.2 の読み込み済み文書(`TextBufferBuilder` 由来チャンク・4KB 格子)では
**133 ns(格子点)〜 2,742 ns(格子セル末尾)**だった。

**わずか 70,000 文字の編集中文書が、1M 文字の読み込み済み文書より約 29〜600 倍遅い。**
文書サイズではなくブロック内オフセットがコストを決めるため、こうした逆転が起きる。
`WordBoundary.PrevWordStart` も **0.0960 ms/回**で、PR #33 の DoD 基準 0.05 ms
(1M 文字 ASCII・読み込み済み)の約 2 倍にあたる。

F-6 の申し送り「1 回の `GetChar` あたり最大 64KB 走査という着手前の姿がこの領域だけ残る」は
**推定ではなく実測された事実**になった。棚卸し表の F-6「まず実測で空白を埋める」は完了。

### 2.3 GDI 込みの実経路(Task 3)

`tests/yEdit.Editor.Smoke --largeline`。実 `EditorControl` を Form に載せ、
`SetOrReplaceSource`(経路 ①②)と `Invalidate` + `Update`(経路 ③)を分けて測る。
`editor.Focused=True` / `ClientSize=884×661` を毎回出力し、経路 ② ③ を測れる条件を
満たしていることを実行時に確認している。

| 文字種 / 折り返し | 100K | 500K | 2M |
|---|---|---|---|
| ascii / OFF | 11.5 + 4.2 ms | 4.7 + 6.1 ms | 9.2 + 12.3 ms |
| ascii / ON(80) | 3.2 + 11.4 ms | 6.2 + 15.5 ms | 25.1 + 33.2 ms |
| cjk / OFF | 4.4 + 5.0 ms | 12.1 + 11.4 ms | 42.7 + 34.9 ms |
| **cjk / ON(80)** | **8,192 + 8,232 ms** | **41,032 + 41,047 ms** | (スキップ) |
| mixed / OFF | 3.9 + 4.6 ms | 13.3 + 12.1 ms | 49.5 + 39.8 ms |
| **mixed / ON(80)** | **4,079 + 4,244 ms** | **20,546 + 20,766 ms** | (スキップ) |

表記は `setSourceMs + paintMsPerFrame`。

#### 真因(確定)

**`LineLayout.Wrap` が非 ASCII の文字に対して 1 コードポイントごとに GDI
`TextRenderer.MeasureText` を呼ぶこと**(`LineLayout.cs:50-52` × `GdiCharMetrics.cs:43-46`)。
発火条件は 3 つすべてが揃ったとき。

1. **折り返し ON**(OFF なら `Wrap` は単一セグメントを即返す = ループしない)
2. **非 ASCII を含む**(全 ASCII なら `_asciiWidths` の配列加算で済む)
3. **論理行が長い**(コストは行長に比例する)

伸びは 100K → 500K で cjk ×5.0 / mixed ×4.9 = **完全に線形**。O(n²) ではなく、
1 文字あたり約 80 μs という**係数の大きさ**が問題である。

#### 240 秒ハングの機序

500KB の CJK 1 行を折り返し ON で開いた場合:

- 経路 ②(`ComputeCaretPoint`)で **41 秒**
- 経路 ③(初回 `OnPaint`)で **41 秒**
- **合計 82 秒で初回表示が終わる**が、経路 ③ は**キャッシュを持たない**ため、
  以後ウィンドウのリサイズ・再描画・キャレット移動のたびに**毎回 41 秒**かかる

§9.8 が観測した「240 秒経っても応答しない」は、この再計算が数回積み上がった状態である。
**事実上、閉じるまで応答しない。**

なお折り返し OFF は全条件でミリ秒級(最悪 mixed 2M で 49.5 + 39.8 ms)。§2.1 で
「OFF も主犯候補」とした推定は**外れ**だった。経路 ① は `MeasureRun` へ行全体を
一括投入するが、GDI 呼び出しは 1 回で済むため速い。

#### ★ 副産物: offscreen Form では経路 ③ を測れない

**最初の測定では全条件の paint が約 1.0 ms しか出なかった。** ASCII 2M / wrap 80 は
§2.2 の構造コストだけで 6.5 ms かかるはずで、この値はあり得ない。
Form の位置を `(-32000, -32000)` から `(100, 100)` へ移して測り直したところ、
同条件の paint が **1.0 ms → 33.2 ms** に変わった。

| 条件 | offscreen | 画面内 |
|---|---|---|
| ascii 2M / ON | 1.0 ms | 12.3 ms |
| cjk 2M / OFF | 0.9 ms | 34.9 ms |
| cjk 500K / ON | 1.2 ms | **41,047 ms** |

完全に画面外のウィンドウは可視領域が空になり、`Control.Update`(`UpdateWindow`)が
**WM_PAINT を配送しない**。`Invalidate` が無効領域を作っても描画は起きない。

**これは既存の性能ゲートに波及する。** `tests/yEdit.Editor.Smoke/GdiBench.cs:50` は
`Location = new Point(-32000, -32000)` を使っており、同じ条件で「平均フレーム時間 &lt; 16ms」
を PASS 判定している(CLAUDE.md §5 の L4)。**描画していないから速い**値でゲートを
通している可能性が高く、L4 の性能ゲートが機能していないことになる。
本調査のスコープ外のため修正はしないが、申し送りとして §4 に記録する。

#### この節が説明するもの / しないもの(§2.4 で確定)

**§9.8 の fixture は純 ASCII だった**(§2.4)。上表のとおり ASCII は折り返し ON でも
2M 文字で 33 ms にすぎない。したがって本節が特定した「折り返し ON × 非 ASCII × 長大行」は
**§9.8 が観測した 240 秒の説明ではない**。

本節の内容は、調査の過程で新たに見つかった**独立した実在の問題**として扱う。
実機の既定設定は `WrapColumnEnabled=false` のため既定では踏まないが、
**折り返しを ON にして日本語の長い 1 行(日本語を含む 1 行 JSON・整形前のログ等)を開けば
誰でも踏む**。再現手順とコストは上表のとおりで、推定ではなく実測である。

### 2.4 実機での裏取り(Task 4)

#### fixture は純 ASCII だった

PR #33 の L5 セッションで使われた fixture が、前回セッションの scratchpad
(`l5/longtoken.txt`)に残っていた。**500,000 バイト・改行 0 個・小文字 a-z のみの純 ASCII。**
§2.3 の結論(非 ASCII 前提)はこの fixture には適用されない。

#### §9.8 の「240 秒ハング」は再現しない

新規起動した yEdit(Release ビルド・実機設定 `WrapColumnEnabled=false`)で
`longtoken.txt` を Ctrl+O から開いた結果:

```
[open] elapsed=0.41 s busySeen=False opened=True responding=True title=[longtoken.txt - yEdit]
```

**0.41 秒で開き、一度も `Responding=false` にならなかった。** タイトルバーが
`longtoken.txt - yEdit` に変わったことで、実際に開けたことも確認している。
これは §2.2 / §2.3 の実測(ASCII 500K・折り返し OFF = Load 12 ms + setSource 4.7 ms +
paint 6.1 ms ≒ 23 ms)と整合する。

#### 「モーダルダイアログによる誤検出」という仮説は外れ

`Process.Responding` がファイルダイアログ表示中に false を返すのではないか、と疑って
ダイアログを開いたまま 3 回プローブしたが、**いずれも `Responding=True`** だった。
モーダルダイアログは `Responding` を false にしない。

#### 240 秒が出た原因は特定できていない

前回の測定スクリプト `open-and-measure.ps1` の `Wait-Idle` は、一度 `Responding=false` を
観測すると true に戻るまで break せず timeout(240 秒)まで回る構造になっている。
何らかの理由で false が継続したことは確かだが、その理由は再現できていない。

**したがって §9.8 の副産物は「一度観測されたが再現しない事象」として扱う。**
現時点で「長大 1 行ファイルは開けない」という記述は**事実に反する**。

#### F-5 の実機実害は未確認(測定に穴がある)

Ctrl+End → Ctrl+Left × 10 を送って `Responding` をポーリングしたところ、合計 0.02 秒・
`busySeen=False` だった。しかし**キーが実際にエディタへ届いたかを確認していない**
(キャレット位置を読んでいない)。PR #33 §9.8 の教訓「`SendKeys` の所要時間は処理完了までの
時間ではない」と同種の穴であり、**この数値から F-5 の実害有無を結論することはできない。**
F-5 の実機確認は未消化のまま残る。

#### F-3 の実害採取は未実施

NVDA を用いる作業のため本セッションでは実施していない。
