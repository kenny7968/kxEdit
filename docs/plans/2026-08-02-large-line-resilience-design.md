# 巨大 1 行 / 長大トークン耐性 設計書

策定日: 2026-08-02 / 調査ブランチ: `feature/large-line-triage`

調査計画は `docs/plans/2026-08-02-large-line-triage.md`、調査の設計は
`docs/plans/2026-08-02-large-line-triage-design.md` を参照。

## 1. 背景・目的

PR #33(文字アクセス seam 集約・マージコミット `a12513c`)の L5 実機 SR 検証で、
「**500KB の空白・改行を一切含まない 1 行ファイルを開くと UI が 240 秒経っても応答しない**」
という副産物が記録された(`docs/plans/2026-07-31-char-access-seam-design.md` §9.8)。
同時に申し送り F-1〜F-6 が積み上がっていた。本調査はこの 2 つを対象に、
対応要否・順序・単位を判断できる材料を揃えることを目的とした
(調査の設計は `2026-08-02-large-line-triage-design.md`、計画は `2026-08-02-large-line-triage.md`)。

**結論として、当初の前提が 2 つとも覆った。**

1. **§9.8 の副産物は「未再現」である(取り下げではない)。** fixture は純 ASCII
   (小文字 a-z のみ・500,000 バイト・改行 0)で、実機で開くと **0.41 秒**で完了し
   一度も応答不能にならなかった(§2.4)。
   **ただしこの試行は元の観測条件を再現していない。** §9.8 は
   「L5 実機 SR 検証(NVDA スピーチビューアー経由)」の副産物、すなわち **NVDA が
   動作中の観測**だが、本調査の試行は **SR 非接続**で行われており、
   SR だけが踏む経路(§2.1 の経路 ④)を丸ごと落としている。
   その経路には **1 回 1.4 秒**の実測コストが存在する(§2.2 の F-5)。
   したがって現時点で言えるのは「**SR 非接続では再現しない**」までであり、
   240 秒の否定にはなっていない。§9.8 自体は策定時スナップショットとして触らず
   (CLAUDE.md §8)、この限定つきの訂正を本書に記録する。
2. **代わりに、独立した実在の問題が実測で見つかった。** 折り返し ON × 非 ASCII × 長い
   論理行で `LineLayout.Wrap` が 1 コードポイントごとに GDI を呼び、CJK 500K 文字で
   **1 フレームあたり 41 秒**かかる(§2.3)。既定設定(`WrapColumnEnabled=false`)では
   踏まないが、折り返しを ON にすれば日本語の長い 1 行で確実に踏む。

副次的に、**既存の L4 性能ゲート(`GdiBench`)が描画を測れていない疑い**が濃厚になった
(§2.3)。また申し送り **F-5 / F-6 が推定から実測になった**(§2.2)。とくに F-5 は
空白ゼロ 500K 文字で `PrevWordStart` が **1 回 1,393.5 ms**、SR の単語単位読みでは
その約 2 倍という実害が確定した。

本調査は `src/` を 1 行も変更していない。

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
| 4 | `_uia.RaiseTextChanged()`<br>(`EditorControl.cs:294`) | `ReplaceSource` 末尾のみ | 1(以後 **SR の読み上げ操作のたび**) | SR がこれを受けて UIA 経路で読みに来る。`UiaTextHostAdapter.cs:529-556` の `WordStart` / `WordEnd` は空白を 1 文字ずつ探すため、**空白の無い長大行では行頭まで全走査**する | 両方 |

> **⚠️ 経路 ④ は本調査で一度も測定していない。** §2.2 は Core 単体、§2.3 は UIA
> クライアント不在、§2.4 は SR 非接続である。この経路のコストは §2.2 の F-5 に
> 間接的な実測(`PrevWordStart` = 同じ「空白を 1 文字ずつ探す」構造)があるのみで、
> **SR 接続下での実挙動は未検証**。これが §2.4 の限界の根拠になっている。
>
> なお `TextRangeProviderV2.cs:60-61` の `ExpandToEnclosingUnit(TextUnit.Word)` は
> `WordStart` と `WordEnd` を**両方**呼ぶため、SR の単語単位読み 1 回あたりのコストは
> F-5 の実測値の概ね 2 倍になる。加えて `UiaTextHostAdapter.cs:578` / `:708` は
> `GetBoundingRectangles` / `ComputeOffsetFromScreenPoint` を **UI スレッドへ同期
> `Invoke`** するため、SR 由来の負荷が UI スレッドを塞ぎ得る
> (= `Process.Responding` を false にし得る)。

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

**上表の文字種差(ascii < cjk < mixed)の原因は特定していない。** 当初この節には
「`MonoCharMetrics` は文字種で分岐しないので差は `GetText` の UTF-8 デコードコストに
由来する」と書いたが、レビュー指摘により**両方とも誤り**と判明したため撤回した。
`MonoCharMetrics.cs:28` は `cpLen == 1 && (c < 0x80 || c == '\t')` で分岐するし、
mixed は cjk よりバイト数が少ない(2 バイト/文字 対 3 バイト/文字)のに約 2 倍遅く、
バイト量では説明できない。結論(ミリ秒級・線形)は変わらないため原因究明は行わない。

また「伸び」列は `F1` 書式(0.1 ms 刻み)の n=1 測定から算出しており、100K 側が
0.2〜0.7 ms と小さいため相対誤差が大きい。**線形性の判定には §2.3 の
100K → 500K(×5.0 / ×4.9・長さ比 ×5.0 とよく一致)を使うべきで、本表の比は目安である。**

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
(ビルド直後の初回セルのみ JIT を含んで 113 ms 程度になる。上表は暖まった状態の値。)

これは「§9.8 の fixture が ASCII だった場合」の代替仮説を 1 つ潰す意味がある。
ASCII 500KB を折り返し ON で開いた場合の合計は Load 12 ms + setSource 6.2 ms +
paint 15.5 ms ≒ **34 ms** にしかならず、240 秒とは 4 桁違う。

#### ★ F-5 は実測で裏づけられた(実害あり)

`--largeline` に「空白・改行を一切含まない長大トークン」での `WordBoundary.PrevWordStart`
を追加した。既存 `--characcess` は `MakeWordDoc`(空白・改行あり)を使うため、
**区切りが 1 つも無い最悪ケースを測っていなかった**。

| 行長 | `PrevWordStart` 1 回 |
|---|---|
| 100K 文字 | 274.0 ms |
| **500K 文字(§9.8 の fixture と同形状)** | **1,393.5 ms** |
| 2M 文字 | 5,587.7 ms |

3 回の最小値。行長に線形(×5 で ×5.1、×20 で ×20.4)。

**SR 経路ではこの約 2 倍になる。** `TextRangeProviderV2.cs:60-61` の
`ExpandToEnclosingUnit(TextUnit.Word)` は `WordStart` と `WordEnd` を両方呼び、
`UiaTextHostAdapter.cs:529-556` の実装は `PrevWordStart` と同じ「空白を 1 文字ずつ探す」
構造だからである。**§9.8 の fixture では SR の単語単位読み 1 回あたり約 2.8 秒。**

これにより F-5 の位置づけが変わる。調査前の棚卸しでは「実機実害が未確認」としていたが、
**Core レベルでは実害が確定した**。§2.4 の実機測定(Ctrl+Left × 10 が 0.02 秒)は、
上表に照らすと **1 回目だけで約 1.4 秒かかるはず**であり、観測値は約 70 倍ずれている。
これは「測定が無効だった(キーがエディタに届いていなかった)」ことの積極的な証拠である。

#### F-6 は実測で裏づけられた

同ベンチの後半で `AppendBuffer` 現ブロック経路(`TextBuffer.FromString` ではなく
1 文字ずつ `Insert` して育てた文書)を測った。70,000 文字 / ピース数 2。

| 位置 | 1 回目 | 2 回目 |
|---|---|---|
| pos = 8,750 | 13,799 ns/回 | 5,769 ns/回 |
| pos = 26,250 | 34,009 ns/回 | 17,190 ns/回 |
| pos = 61,250 | **79,380 ns/回** | **39,880 ns/回** |
| `PrevWordStart` | 0.0960 ms/回 | 0.0490 ms/回 |

**2 回の測定で絶対値が約 2 倍振れた**(1 回目は他のベンチと並行実行していた)。
PR #33 の教訓「マシン負荷で絶対値が 10 倍ずれた」どおりで、**絶対値は参考値として扱う**。
一方**位置にほぼ比例して増える**という性質は両方で一貫しており、これが F-6 の主張
(ブロック先頭からの線形走査)を支える。

比較のため、PR #33 §9.2 の読み込み済み文書(`TextBufferBuilder` 由来チャンク・4KB 格子)
では **133 ns(格子点)〜 2,742 ns(格子セル末尾)**だった。
**わずか 70,000 文字の編集中文書が、1M 文字の読み込み済み文書より 1〜2 桁遅い。**
文書サイズではなくブロック内オフセットがコストを決めるため、こうした逆転が起きる。

F-6 の申し送り「1 回の `GetChar` あたり最大 64KB 走査という着手前の姿がこの領域だけ残る」は
**推定ではなく実測された事実**になった。棚卸し表の F-6「まず実測で空白を埋める」は完了。

### 2.3 GDI 込みの実経路(Task 3)

`tests/yEdit.Editor.Smoke --largeline`。実 `EditorControl` を Form に載せ、
`SetOrReplaceSource`(経路 ①②)と `Invalidate` + `Update`(経路 ③)を分けて測る。
`editor.Focused=True` / `ClientSize=884×661` を毎回出力し、経路 ② ③ を測れる条件を
満たしていることを実行時に確認している。

| 文字種 / 折り返し | 100K | 500K | 2M |
|---|---|---|---|
| ascii / OFF | 4.6 + 3.7 ms | 5.2 + 5.5 ms | 9.7 + 11.5 ms |
| ascii / ON(80) | 3.4 + 10.9 ms | 6.6 + 15.9 ms | 9.7 + 17.0 ms |
| cjk / OFF | 3.4 + 4.6 ms | 12.3 + 11.6 ms | 46.1 + 37.5 ms |
| **cjk / ON(80)** | **8,007 + 7,908 ms** | **39,820 + 39,837 ms** | (スキップ) |
| mixed / OFF | 5.3 + 4.8 ms | 12.2 + 11.8 ms | 48.5 + 38.5 ms |
| **mixed / ON(80)** | **4,001 + 4,195 ms** | **19,847 + 20,064 ms** | (スキップ) |

表記は `setSourceMs + paintMsPerFrame`。ミリ秒級の条件は n=1 のため個体差があり、
**細かい大小は意味を持たない**(桁だけを読むこと)。秒級の条件は 2 回の独立測定で
cjk 500K/ON が 41.0 秒 → 39.8 秒、mixed 500K/ON が 20.5 秒 → 19.8 秒と再現している。

初版の測定にはウォームアップが無く、最初の 1 条件(ascii/OFF/100K)だけ JIT を含んで
11.5 ms となり 500K の 4.7 ms より遅いという逆転が出ていた。レビュー指摘を受けて
捨て打ちを 1 条件加え(`LargeLineBench.cs:59-64`)、上表は逆転が解消した状態の値である。

#### 真因(確定)

**`LineLayout.Wrap` が非 ASCII の文字に対して 1 コードポイントごとに GDI
`TextRenderer.MeasureText` を呼ぶこと**(`LineLayout.cs:50-52` × `GdiCharMetrics.cs:43-46`)。
発火条件は 3 つすべてが揃ったとき。

1. **折り返し ON**(OFF なら `Wrap` は単一セグメントを即返す = ループしない)
2. **非 ASCII を含む**(全 ASCII なら `_asciiWidths` の配列加算で済む)
3. **論理行が長い**(コストは行長に比例する)

伸びは 100K → 500K で cjk ×5.0 / mixed ×4.9 = **完全に線形**。O(n²) ではなく、
1 文字あたり約 80 μs という**係数の大きさ**が問題である。

#### コストの積み上がり方(これは §9.8 の説明ではない)

500KB の CJK 1 行を折り返し ON で開いた場合:

- 経路 ②(`ComputeCaretPoint`)で **約 40 秒**
- 経路 ③(初回 `OnPaint`)で **約 40 秒**
- **合計 約 80 秒で初回表示が終わる**が、経路 ③ は**キャッシュを持たない**ため、
  以後ウィンドウのリサイズ・再描画・キャレット移動のたびに**毎回 約 40 秒**かかる
  = 事実上、閉じるまで実用にならない

**繰り返すが、これは §9.8 が観測した 240 秒の説明ではない。** §9.8 の fixture は
純 ASCII であり(§2.4)、ASCII は折り返し ON でも 2M 文字で 33 ms にすぎない。
本節が扱うのは、非 ASCII の長大行という**別条件で起きる独立した問題**である。

なお折り返し OFF は全条件でミリ秒級(最悪 mixed 2M で 48.5 + 38.5 ms = 合計 87 ms)。
§2.1 で「OFF も主犯候補」とした推定は**外れ**だった。経路 ① は `MeasureRun` へ行全体を
一括投入するが、GDI 呼び出しは 1 回で済むため速い。

#### ★ 副産物: offscreen Form では経路 ③ を測れない

**最初の測定では全条件の paint が約 1.0 ms しか出なかった。** ASCII 2M / wrap 80 は
§2.2 の構造コストだけで 6.5 ms かかるはずで、この値はあり得ない。
Form の位置を `(-32000, -32000)` から `(100, 100)` へ移して測り直したところ、
同条件の paint が **1.0 ms → 33.2 ms** に変わった。

| 条件 | offscreen | 画面内 |
|---|---|---|
| ascii 2M / ON | 1.0 ms | 33.2 ms |
| cjk 2M / OFF | 0.9 ms | 34.9 ms |
| cjk 500K / ON | 1.2 ms | **41,047 ms** |

(この対比は初版=ウォームアップ導入前の測定同士。位置以外の条件は揃えてある。)

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

#### ★ この試行は元の観測条件を再現していない(最大の限界)

**§9.8 の 240 秒は「L5 実機 SR 検証(NVDA スピーチビューアー経由)」の副産物であり、
NVDA が動作中の観測である。本節の試行は SR 非接続で行った。**

差は小さくない。SR が接続していると §2.1 の**経路 ④** が加わる。
`UiaTextHostAdapter.cs:529-556` の `WordStart` / `WordEnd` は空白を 1 文字ずつ探すため、
空白ゼロの 500K 文字では行頭まで全走査する。§2.2 の F-5 実測では
`PrevWordStart`(同一構造)が **1 回 1,393.5 ms**、
`ExpandToEnclosingUnit(TextUnit.Word)` は `WordStart` と `WordEnd` を両方呼ぶので
**1 操作あたり約 2.8 秒**になる。さらに `UiaTextHostAdapter.cs:578` / `:708` は
**UI スレッドへ同期 `Invoke`** するため、SR 由来の負荷が UI スレッドを塞ぎ
`Process.Responding` を false にし得る。

**本節が測った最大値は 87 ms(§2.3 の折り返し OFF 最悪値)で、未測定の経路 ④ には
その 16 倍の実測値(1,393.5 ms)がある。** したがって本節の結論は
「**SR 非接続では再現しない**」に限定される。

#### 240 秒が出た原因は特定できていない

前回の測定スクリプト `open-and-measure.ps1` の `Wait-Idle` は、一度 `Responding=false` を
観測すると true に戻るまで break せず timeout(240 秒)まで回る構造になっている。
何らかの理由で false が継続したことは確かだが、その理由は本調査では特定できていない。

**§9.8 の副産物は「未再現(原条件で未試行)」として扱う。取り下げではない。**
再試行は NVDA 起動状態で行う必要がある(§4 N-5)。

#### 折り返し ON での実機測定は未実施

実装計画(`2026-08-02-large-line-triage.md:428`)は「折り返し ON / OFF の両方で測る」と
指定していたが、実機の既定設定が `WrapColumnEnabled=false` だったため **OFF のみ**を
測った。**ON は未測定である。** §9.8 自身の主因推定が「折り返し ON × 改行なし巨大 1 行」
だったことを踏まえると、これは反証すべき条件そのものだった。§4 N-5 の再試行に含める。

#### 実機の単語ナビ測定は無効だった(F-5 の実害は Core で確定済み)

Ctrl+End → Ctrl+Left × 10 を送って `Responding` をポーリングしたところ、合計 0.02 秒・
`busySeen=False` だった。しかし §2.2 の F-5 実測に照らすと、この fixture では
**1 回目の Ctrl+Left だけで約 1.4 秒**かかるはずである(2 回目以降はキャレットが 0 に
達しているので即返る)。観測値は約 70 倍ずれており、**キーがエディタへ届いていなかった
ことの積極的な証拠**と読める。PR #33 §9.8 の教訓「`SendKeys` の所要時間は処理完了までの
時間ではない」と同種の穴で、キャレット位置の確認を組み込んだ測定に作り直す必要がある
(§4 N-3)。

**なお F-5 の実害そのものは §2.2 で Core レベルの実測が取れており、「未確認」ではない。**

#### F-3 の実害採取は未実施

NVDA を用いる作業のため本セッションでは実施していない。

## 3. 申し送りの再評価(調査後)

`2026-08-02-large-line-triage-design.md` §2 の棚卸し表を、実測を踏まえて更新する。

| ID | 調査前の判断 | 調査後 | 根拠 |
|---|---|---|---|
| 副産物 | Yes(規模は調査後) | **未再現(原条件で未試行)** | SR 非接続でのみ試行しており、SR だけが踏む経路 ④ を落としている(§2.4)。**取り下げではない**。NVDA 起動状態での再試行が要る(N-5) |
| F-5 | Yes | **実害確定(Core レベル)** | 空白ゼロ 500K で `PrevWordStart` が 1,393.5 ms/回。UIA は `WordStart`+`WordEnd` を両方呼ぶため SR の単語読み 1 回で約 2.8 秒(§2.2)。SR 接続下の実挙動は未検証(N-3) |
| F-6 | 条件付き(まず実測) | **実測完了・対応は F-1 とセット** | 編集中文書の `GetChar` が最大 79,380 ns/回(§2.2)。単独では割に合わない判断は変わらず |
| F-1 | 手段 | **新問題には効かない** | 真因はレイアウト層(§2.3)。`CharCursor` は文字アクセス層なので無関係。F-5 / F-6 の手段としてのみ残る |
| F-2 | No(監視) | 変更なし | 実測も実害報告もない |
| F-3 | 採取後に判断 | **未採取・継続** | NVDA が要る |
| F-4 | Yes(小) | 変更なし | docs のみ。他作業のついでで回収 |
| **N-1** | — | **次のテーマとして対応** | 折り返し ON × 非 ASCII × 長大行(§2.3)。ユーザー判断済み |
| **N-2** | — | **申し送りに記録** | `GdiBench` の offscreen 問題(§2.3)。ユーザー判断済み |

## 4. 申し送り(follow-up)

- **N-1: 折り返し ON × 非 ASCII × 長大行で 1 フレーム 41 秒**(次のテーマとして起票する)
  - 真因: `LineLayout.Wrap`(`LineLayout.cs:50-52`)が 1 コードポイントごとに
    `ICharMetrics.MeasureRun` を呼び、`GdiCharMetrics`(`GdiCharMetrics.cs:43-46`)が
    非 ASCII で `TextRenderer.MeasureText`(GDI)へ落ちる
  - 悪化要因: `OnPaint` の `ViewportLayout.Build` に**フレームをまたぐキャッシュがない**
    (`EditorControl.Paint.cs:34`)ため、再描画のたびに再計算される
  - 対策の候補(いずれも設計が要る): ① 幅計測のバッチ化(GDI 呼び出しを 1 行 1 回にする)
    ② 視覚行キャッシュの導入 ③ `Wrap` を可視範囲で打ち切る(現状は行全体を Wrap してから
    可視分だけ使う)④ ASCII fast path の非 ASCII 版(等幅フォント前提の幅表)
  - 再現手順: `tests/yEdit.Editor.Smoke --largeline`(実測値は §2.3)
  - **起票時に条件へ追加すること**: 現在のベンチ fixture は BMP のみで、
    **サロゲートペア(astral)を含む行を測っていない**。`LineLayout.cs:50` は
    `TextBoundary.CodePointLengthAt`、`MonoCharMetrics.cs:26-29` も専用分岐で
    サロゲートを扱っており、絵文字だらけの 1 行 JSON は現実的な入力である
- **N-2: `GdiBench` が描画を測れていない疑い(L4 性能ゲートの信頼性)**
  - `tests/yEdit.Editor.Smoke/GdiBench.cs:50` は `Location = new Point(-32000, -32000)`。
    完全に画面外のウィンドウは可視領域が空になり、`Update()` が WM_PAINT を配送しない
  - 同条件の比較で paint コストが **1.0 ms → 33.2 ms** に変わった(§2.3)
  - 修正は 1 行(`Location` を画面内へ)だが、**直した途端に 16ms ゲートが FAIL する
    可能性がある**ため、前後値の再測定とゲート基準値の見直しをセットで行う
  - CLAUDE.md §5 の L4 が「測っていないのに合格」を出していた可能性がある
- **N-3: F-5 の SR 接続下での実挙動が未検証**(Core レベルの実害は §2.2 で確定済み)
  - 実機の `Responding` ポーリングは**キーがエディタへ届いたかを確認していない**ため
    無効だった(§2.4)。キャレット位置を UIA で読むなどして到達を確認する測定に作り直す
  - 併せて、UIA 経路(`ExpandToEnclosingUnit(Word)` = `WordStart`+`WordEnd`)を
    SR 接続下で測る。`UiaTextHostAdapter.cs:578` / `:708` の UI スレッド同期 `Invoke` が
    `Process.Responding` に与える影響もここで確認する
- **N-4: F-3(UIA 単語境界のずれ)の実害採取が未実施**
  - NVDA スピーチビューアーを UIA で読む手法(PR #33 §9.8)で採取する
- **N-5: §9.8 の 240 秒を原条件で再試行する(最優先の残課題)**
  - 本調査の試行は **SR 非接続・折り返し OFF のみ**で、元の観測条件
    (**NVDA 動作中**・折り返しは不明)を再現していない(§2.4)
  - 再試行時の必須条件: ① **NVDA を起動した状態**で行う ② **折り返し ON / OFF の両方**を
    測る ③ `Responding` だけでなく**キャレット位置・ウィンドウ状態・NVDA の発話**も
    併せて記録する ④ 経路 ④(UIA の単語境界)を直接叩いた値も取る
  - N-3 / N-4 と同じ実機セッションでまとめて実施できる
