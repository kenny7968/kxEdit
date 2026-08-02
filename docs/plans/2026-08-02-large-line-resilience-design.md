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

(Task 3 で記述)

### 2.4 実機での裏取りと F-3 実害採取(Task 4)

(Task 4 で記述)
