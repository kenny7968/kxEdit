# 折り返し ON の垂直移動を直す(A-5 / A-6 / E-1)設計書

作成日: 2026-08-22 / ブランチ: `feature/wrap-vertical-nav` / 起点 main = `efd2127`

対象は [`docs/plans/2026-08-22-v0.2-release-bug-audit.md`](./2026-08-22-v0.2-release-bug-audit.md) の
**A-5**(優先度 1)・**A-6**(優先度 1)と、同書 §5 の未起票事項 **E-1**。監査書 §8-4 は
「A-5 の着地クランプだけ先行し、A-6 は設計書で扱う」としていたが、後述 §1.4 の理由により
**A-6 も本ブランチで根治する**方針をユーザー承認のうえ採る。

本書は策定時スナップショット(CLAUDE.md §8)。実装時の精密化と実施記録の追記のみ行う。

## 1. 問題

### 1.1 A-5 — 折り返し ON で ↑ が効かなくなる / ↓ が視覚行を 1 行飛ばす [DLL]

再現(`MonoCharMetrics` 8px・`wrapColumns=4`・`"abcd\nxxxxyyyyzzzz\nend"`):
行 0 の行末(caret=4)で ↓ を押すと caret=9 になる。9 は行 1 の `offsetInLine=4`
= 視覚行 `"xxxx"` を飛ばして `"yyyy"` の行頭。続けて ↑ を何回押しても caret=9 のまま動かない。

**機構**は 1 つで、症状が 2 つに見えているだけである。

`VerticalNavigation.MoveVerticalRelative`(`src/kxEdit.Core/Editing/VerticalNavigation.cs:139-142`)は
移動先セグメントの span に対して `PixelMapper.PxToOffset(desiredPx)` を呼び、戻り値をそのまま
着地オフセットにする。`PxToOffset` は `px >= 全幅` のとき `segment.Length` を返す
(`src/kxEdit.Core/Layout/PixelMapper.cs:44-45`)ので、desiredPx が移動先セグメントの幅以上のとき
着地は `segEnd`(= セグメント末尾)になる。

ところが `segEnd` は、**描画・照会の両方で「次の視覚行の先頭」と解釈される**。

| 経路 | 規約 | 典拠 |
|------|------|------|
| 照会(`VisualSegments.FindContaining`) | `offsetInLine < segEnd` を満たす最初のセグメント。`segEnd` ちょうどは次セグメント(最終行のみ例外) | `src/kxEdit.Core/Layout/VisualSegments.cs:30` |
| 描画(`EditorControl.ComputeCaretPoint`) | 同上(`ReachedLineEnd && 最終要素` のときだけ末尾ちょうどを許容) | `src/kxEdit.Editor/EditorControl.cs:1568-1585` |

したがって `segEnd` へ着地すると、

- **↓ が 1 行飛んで見える**: 歩いたのはセグメント 0 なのに、描画されるのはセグメント 1 の行頭。
- **↑ が効かない**: 次の ↑ は `FindSegIndex` が「今いるのはセグメント 1」と判定し、1 つ戻した
  セグメント 0 で再び同じ `segEnd` に着地する。caret 値が変わらないので画面も動かない。

発生条件は「desiredPx ≧ 移動先視覚行の幅」。全角段落は各視覚行が幅いっぱいになるため、
**段落末尾からの ↑ / 幅いっぱいの行の行末からの ↓ で日常的に踏む**。

既存 `VerticalNavigationTests` の wrap 系(5 件)はセグメント**中央**の桁だけを見ており、
右端ケースが未被覆であるためテストは全緑のままだった。

### 1.2 A-6 — 折り返し ON でキャレットが可視域外に出ても追従スクロールしない [コード]

`EditorControl.BringCaretIntoView`(`src/kxEdit.Editor/EditorControl.Caret.cs:368-382`)は
キャレットの**論理行**が `[TopLine, TopLine + visibleRows)` の外にあるかどうかだけを見る。
折り返し ON では 1 論理行が複数の視覚行を占めるため、この判定は成立しない。

監査書の再現(段落あたり 5 視覚行・30 行ウィンドウ)では、論理行 6 あたりで
`ComputeCaretPoint` が `Visible=false` を返しはじめ、システムキャレットは (-1000,-1000) へ退避する。
それでも `TopLine` は論理行 30 に達するまで 1 行も動かない。

**監査書が書いていない、より重い帰結**: `_topLine` が論理行である以上、
**論理行が 1 本しかない文書(巨大 1 行)は折り返し ON で垂直スクロールが原理的に不能**になる。

- `BringCaretIntoView`: `logicalLine` は常に 0・`_topLine` も 0 なので判定が両方とも不発。
- `UpdateVerticalScrollbar`(`EditorControl.cs:1036-1047`): `maxLine = LineCount - 1 = 0` より
  `_vscroll.Enabled = false`。**スクロールバーもホイールも効かない**
  (ホイールは `TopLine` セッター経由で `ClampTopLine` に潰される)。

つまり先頭 `visibleRows` 本より下は**恒久的に到達不能**である。これは PR #35
(巨大 1 行 折り返し描画コスト解消)が最適化した当のファイル種別そのものであり、
CLAUDE.md §2「晴眼・弱視ユーザーも第一級」に照らして受容できない。

### 1.3 E-1 — 折り返し ON で ↓ を押すと NVDA が「ブランク」と発話(↑ は正常)

PR #35 の L5(2026-08-03)で発見・未起票。再現性 100%・main でも同一挙動。
監査書 §5 は「A-5 と同根の可能性」としているが、**本設計では A-5 由来を否定的に見る**。

- A-5 の着地オフセットは常に実在のセグメント内の位置であり、UIA の `LineStartOf` /
  `LineEnd`(`UiaTextHostAdapter.cs:360-395`)はそこから**空でない**視覚行範囲を返す。
  「ブランク」を説明できない。
- 一方 **↓ だけが症状を出し ↑ は正常**という非対称は、「↓ はキャレットを可視域の下へ
  押し出すが、↑ は押し出さない」= **A-6 の症状と一致**する。E-1 の再現ファイル
  (CJK 500K)は §1.2 の「巨大 1 行」に該当し、↓ を数回押しただけでキャレットが
  可視域外へ出たまま二度と戻らない。

よって E-1 は**本ブランチでは修正対象ではなく検証対象**とする。A-6 修正後の L5 で再現しなければ
A-6 由来と確定してクローズ、再現すれば UIA 側の独立した欠陥として別途起票する。

### 1.4 A-5 と A-6 を 1 ブランチに束ねる理由

1. どちらも「折り返し ON の垂直移動」という 1 つのユーザー体験の裏表で、
   片方だけ直しても「↓ で行が飛ばなくなったが画面は付いてこない」という中途半端な状態になる。
2. 検証コストの支配項は **L5(折り返し ON × NVDA の実機セッション)**であり、
   A-5 だけ直して L5 に臨むと §1.3 の見立てどおり E-1 が残り、L5 をもう 1 回焼く。
3. 折り返し OFF は後述 I-3 により構造的に不変。退行面はオプトインのモードに限定される。

## 2. 不変条件

| ID | 内容 |
|----|------|
| **I-1** | 垂直移動の着地オフセットは、**非最終セグメント R に対して `segEnd(R)` 未満**でなければならない。`segEnd` は描画・照会の双方で「次の視覚行の先頭」を意味するため、そこへ着地すると歩いた視覚行と表示される視覚行が 1 本ずれる(§1.1) |
| **I-2** | 可視域の起点は論理行ではなく**視覚行** `(TopLine, TopSegment)` である。可視判定・スクロール判断・座標算出・ヒットテストはすべてこの起点から**視覚行数**で数える |
| **I-3** | 折り返し OFF(`WrapColumns <= 0`)では `TopSegment ≡ 0` であり、すべての式が変更前と同一に退化する。**折り返し OFF の既存テストが 1 行も変わらず全緑であること**を、この不変条件の証拠とする |
| **I-4** | 視覚行の歩き(前方・後方とも)は必要本数で打ち切る。文書全体・論理行全体を無条件に Wrap する経路を新設しない(PR #35 の O(可視行数) を維持) |

## 3. A-5 の設計

### 3.1 修正

`VisualSegments` に I-1 を体現する純関数を 1 本追加する。置き場所を `VisualSegments` にするのは、
I-1 の根拠が同クラスの `FindContaining` の境界規約そのものだからである(規約とその防護を同居させる)。

```csharp
/// <summary>
/// 視覚行 R への「キャレットの着地オフセット」を規約に合う範囲へクランプする(不変条件 I-1)。
/// 非最終セグメントでは segEnd = 次の視覚行の先頭を意味するため、そこへ着地してはならない。
/// クランプ先は最後のコードポイントの先頭(サロゲートペアを割らない)。
/// </summary>
public static int ClampLandingOffset(
    ReadOnlySpan<char> segment,
    int localOffset,
    bool isFinalSegment
)
{
    if (isFinalSegment || localOffset < segment.Length)
        return localOffset;
    if (segment.Length == 0)
        return 0; // 空セグメントは Wrap 契約上 [(0,0)] の 1 本=常に最終。到達しない防御
    return TextBoundary.SnapToCodePointStart(segment, segment.Length - 1);
}
```

呼び出しは `VerticalNavigation.MoveVerticalRelative` の 1 箇所(`:139-142`)。

```csharp
int usedSegIdx = Math.Min(targetSegIdx, targetSegs.Count - 1);
var targetSeg = targetSegs[usedSegIdx];
var targetSpan = targetLineText.AsSpan(targetSeg.OffsetInLine, targetSeg.Length);
int localTarget = PixelMapper.PxToOffset(targetSpan, desiredPx, metrics);
localTarget = VisualSegments.ClampLandingOffset(
    targetSpan,
    localTarget,
    isFinalSegment: usedSegIdx == targetSegs.Count - 1
);
int newCaret = targetLineStart + targetSeg.OffsetInLine + localTarget;
```

`MoveDown` / `MoveUp` / `PageDown` / `PageUp` はすべて `MoveVerticalRelative` に集約されているため、
4 経路が同時に直る。

### 3.2 意識的に**しない**こと

- **`PixelMapper.PxToOffset` は変更しない**。純関数の意味論(「px が全幅以上なら全長」)は
  マウスのヒットテスト(`EditorControl.Input.cs:260`)と共有しており、そちらでは
  `segEnd` に着地するのが正しい(下記)。
- **マウス経路には I-1 を適用しない**。ドラッグ選択で視覚行の右端まで引いたとき、
  着地を `segEnd - 1` にすると**その行の最後の 1 文字が選択から漏れる**。選択の端点としては
  `segEnd` が正しい(その位置は「行 R の末尾」でもあるため、ハイライトは行 R 全体を覆う)。
  クリックによるキャレット配置だけが `segEnd` を避けるべきだが、
  同一関数がドラッグ選択にも使われるため、区別は本ブランチのスコープ外とする(§8 申し送り)。

### 3.3 受容するトレードオフ

desiredPx が右端にあるとき、非最終視覚行ではキャレットが**最後の 1 コードポイントぶん内側**に立つ。
これはキャレットの行帰属(affinity — 同一オフセットを「行 R の末尾」と「行 R+1 の先頭」の
どちらとして扱うかの状態)を持たない設計での必然であり、affinity の導入は描画・選択・UIA・
編集の全経路に状態を 1 つ増やす大改造になるため採らない(§7 却下案 B)。

Shift+↓ の選択も 1 コードポイントぶん短くなるが、「↑ が効かない・↓ が行を飛ばす」よりは軽い。

## 4. A-6 の設計

### 4.1 状態

`EditorControl` に `private int _topSegment` を追加する(既定 0)。
意味は「`_topLine` の視覚セグメント列のうち、可視域の最上段に描く要素の index」。

**リセット規則**(セグメント index の意味が変わる契機ではすべて 0 に戻す):

| 契機 | 理由 |
|------|------|
| `TopLine` セッター(公開 API・VScrollBar ドラッグ・既存の全呼び出し元) | 論理行を指定する API の意味を「その行の先頭視覚行から」に保つ |
| `WrapColumns` セッター | 折り返し幅が変わればセグメント分割そのものが変わる |
| `ApplyAppearance`(フォント/metrics 変更) | 同上 |
| `SetSource` | 文書が別物になる |
| `UpdateVerticalScrollbar` の防御クランプ(`_topLine > maxLine`) | 行が消えた後の index は無意味 |

編集(`AfterEdit`)ではリセットしない。編集で `_topSegment` が実際のセグメント数を超えた場合は
`ViewportLayout.Build` 側でクランプする(§4.3)。ここでリセットすると、巨大段落の途中を
編集するたびに表示が段落先頭へ飛ぶ。

### 4.2 スクロール判断(`BringCaretIntoView` / `ScrollCharRangeIntoView`)

```
1. (caretLine, caretSeg) = キャレットの視覚行位置
2. (caretLine, caretSeg) < (TopLine, TopSegment)          … 辞書順比較
      → 起点 = (caretLine, caretSeg)                        [上へスクロール]
3. 起点から前方へ visibleRows 本以内に (caretLine, caretSeg) が入る
      → 何もしない                                          [既に可視]
4. それ以外
      → 起点 = WalkBackVisualRows(caretLine, caretSeg, visibleRows - 1)
                                                            [キャレットを最下行に置く]
```

4 は既存の `TopLine = logicalLine - visibleRows + 1`(= 対象を下端に寄せる)の視覚行版であり、
PR #45 が確立した「ジャンプ先は下端」という UX とそのテストを維持する。

`ScrollCharRangeIntoView`(UIA `ScrollIntoView`)も同じ判定を使う。既存の
「**既に可視なら垂直方向は動かさない**」契約(SR が歩くたびに画面が飛ばないための判断・
`EditorControl.Caret.cs:519` 付近)は 3 でそのまま保たれる。

### 4.3 各経路の変更点

| 経路 | 変更 |
|------|------|
| `ViewportLayout.Build` | 引数に `topSegment` を追加。先頭行だけ `WrapFirstSegments(topSegment + rowsNeeded)` を要求して先頭 `topSegment` 本を捨てる。`topSegment` がセグメント数以上なら最終セグメントへクランプ(編集で段落が縮んだ場合の防御) |
| `EditorControl.ComputeCaretPoint` | 不可視条件に `(logicalLine == _topLine && segIdx < _topSegment)` を追加。積み上げループの先頭行の寄与を `segs.Count - _topSegment` にし、打ち切り上限 `rowsNeeded` にも `_topSegment` を足す |
| `EditorControl.OffsetFromClientPoint` | 視覚行の歩き出しを `segIdx = _topSegment` にする |
| `BringCaretIntoView` / `ScrollCharRangeIntoView` | §4.2 |
| `OnMouseWheel`(`Input.cs:98,103`) | `TopLine ± wheelLines` を「視覚行 ± wheelLines 歩き」に変更 |
| `GetVisibleCharRange` | `ViewportLayout.Build` 経由のため自動追従(引数の受け渡しのみ) |
| `UpdateHorizontalScrollbar` | `wrapColumns: 0` 固定の呼び出し=`topSegment: 0` を渡すだけ |

キャレットの視覚行位置を求める処理は `ComputeCaretPoint` に既にあるため、
**`(segIndex, segment)` を返す private static ヘルパへ抽出**して両者で共有する
(「どのセグメントに属するか」の規約を二重化しない。CLAUDE.md §4 の
「可視判定の単一定義」と同じ趣旨)。この抽出は後続タスクが依存する新しい seam なので、
CLAUDE.md §3-4 の前倒し例外に該当=**コード品質レビューを当該タスクで実施する**。

### 4.4 垂直スクロールバーは論理行基準のまま(意識的な割り切り)

`_vscroll` の `Maximum` / `Value` は `LineCount` と `_topLine` を使い続ける。
文書全体の視覚行数を数えるには全論理行を Wrap する必要があり、O(文書) = PR #35 の退行そのものになる
(500K 文字 1 行のファイルで編集のたびに全文 Wrap)。

帰結として残る制約:

- 段落の途中をスクロールしている間、サムは動かない(位置表示が粗い近似になる)。
- **論理行が 1 本しかない文書ではスクロールバーが `Enabled=false` のまま**で、
  到達手段はキーボードとホイールに限られる。§1.2 の「恒久的に到達不能」は解消するが、
  スクロールバーによる到達は回復しない。→ §8 申し送り。

## 5. 性能

I-4 に従い、視覚行の歩きは必要本数で打ち切る。

- **前方**(起点 → キャレット): `visibleRows` 本で打ち切る。§4.2 の 3 は「visibleRows 本以内に
  入るか」の真偽しか要らないため、遠方ジャンプ(Ctrl+End・検索ジャンプ)でも O(可視行数) で止まる。
- **後方**(キャレット → 新起点): 高々 `visibleRows` 本ぶんしか歩かない。

打ち切れない箇所が 2 つあり、どちらも意図的に受容する。

1. **巨大行を下から遡って入るとき**、その行の視覚行数が必要=完全 Wrap が 1 回要る
   (「最終セグメントから n 本戻る」には総数が要るため)。PR #35 の幅メモ化により
   CJK 500K 行で約 30 ms(`docs/plans/2026-08-02-large-line-resilience-design.md` の実測)。
   なお現状の `VerticalNavigation` は**キー 1 打ごとに現在行と移動先行を完全 Wrap している**ので、
   新しいコスト階級ではない。
2. **巨大段落の途中へスクロールした状態の描画**は、先頭 `topSegment` 本の走査が要る
   = O(topSegment)/フレーム。`(snapshot, line, wrapColumns, topSegment) → 行内 char offset` の
   1 エントリメモ(`UiaTextHostAdapter._lastLineSegs` と同型)で、同じ位置の再描画を
   O(可視行数) に戻す。実装するかは実装計画で確定する
   (Wrap は左から右への貪欲 1 パスで、セグメント境界は先行内容だけで決まる=
   既知の境界から再開した結果は完全 Wrap の suffix と厳密に一致する。
   `LineLayout.WrapCore` の remarks が prefix について述べているのと同じ根拠)。

L4 の `GdiBench` で「500K CJK・折り返し ON・段落途中へスクロールした状態」の 1 フレーム時間を
測り、PR #35 の水準(30.1 ms)から悪化していないことを確認する。

## 6. テスト計画

**L1 kxEdit.Core.Tests**

- `ClampLandingOffset`: 非最終/最終・境界ちょうど/内側・サロゲートペア末尾・空セグメント。
- `VerticalNavigation` の右端ケース(A-5 の回帰):
  - 監査書の再現そのもの(`"abcd\nxxxxyyyyzzzz\nend"`・`wrapColumns=4`)で
    ↓ が `"xxxx"` の行に着地すること・続く ↑ ×3 が**毎回動く**こと。
  - ↓↑ の往復で元の視覚行に戻ること(desiredPx 保持の確認)。
  - 全角段落(各視覚行が幅ぴったり)での ↑ 連打。
  - 最終セグメントへの着地は従来どおり `segEnd`(=行末)であること(クランプの過剰適用防止)。
- `ViewportLayout.Build` の `topSegment`: 先頭行の先頭 n 本を捨てる・`topSegment` が
  セグメント数以上ならクランプ・`topSegment=0` で既存結果と一致。

**L2 kxEdit.Editor.Tests**

- A-6 の再現(段落 5 視覚行 × 小ウィンドウ)で ↓ 連打中キャレットが可視のままであること。
- **巨大 1 行(単一論理行)の到達性**: ↓ 連打で `TopSegment` が進み、
  `GetVisibleCharRange` が文書後半を返すこと。
- ホイールが視覚行単位で進むこと。
- `EnsureVisibleCharRange`(A-3 の経路)が折り返し ON でも下端に寄せること。
- **折り返し OFF の既存テストを 1 行も変更しない**(I-3 の証拠)。
  `CaretScrollTests` / `UiaScrollIntoViewTests` / `UiaVisibleRangeTests` /
  `EditorControlWrapCaretTests` / `MouseInputTests` が無改変で全緑であることを PR に明記する。

**ミューテーション検証**(CLAUDE.md §4・最終品質パスのスポットチェック)

- `ClampLandingOffset` の `isFinalSegment` 分岐を反転 → 右端テストが赤くなること。
- `localOffset < segment.Length` を `<=` に緩める → 同上。
- §4.2 の辞書順比較(2)と可視判定(3)の境界を 1 ずらす → 巨大 1 行テストが赤くなること。

**L5 実機 SR 検証**(必須。SR 経路 = UIA `ScrollIntoView` / 可視域報告に触れるため)

1. 折り返し ON・通常の日本語文書で ↓↑ が視覚行単位に動き、NVDA が各視覚行を読むこと。
2. **E-1 の再検証**(CJK 500K・折り返し ON で ↓ 連打)。ブランクが出なくなれば A-6 由来と確定。
3. 巨大 1 行で ↓ を押し続けて文書末尾まで到達できること。
4. 検索ジャンプ・Ctrl+G が折り返し ON でも追従すること(PR #45 の回帰確認)。

## 7. 却下した案

**A. A-5 のみ修正(監査書 §8-4 の当初案)** — §1.4 の 3 点により却下。特に L5 を 2 回焼く。

**B. キャレット affinity の導入** — `segEnd` を「行 R の末尾」として扱う状態を持てば
A-5 は情報を失わずに直り、選択の 1 文字問題も出ない。しかし描画・選択・UIA・編集・
Undo 復元の全経路に状態が 1 つ増え、「affinity をいつクリアするか」の規約が全経路に波及する。
v0.2 の直前に入れる変更ではない。§8 申し送り。

**C. `FindContaining` の境界規約を「前の行の末尾を優先」に反転** — ↑ は直るが、
描画側も同時に反転しない限り「歩いた行と表示行が 1 本ずれる」構図は変わらない。
両方反転すると今度は視覚行の**先頭**にキャレットを置けなくなる。規約の反転では解けない。

**D. スクロールバーを視覚行基準にする** — 全文の視覚行数が要り O(文書)。§4.4。

## 8. 申し送り

| ID | 内容 |
|----|------|
| S-1 | マウスのヒットテストは `segEnd` に着地するため、視覚行の右端をクリックすると次の行の先頭にキャレットが立つ。キャレット配置とドラッグ選択で着地規約を分ける必要があり、affinity(S-2)と併せて扱うのが筋 |
| S-2 | キャレット affinity の導入(§7 案 B)。A-5 の「右端で 1 コードポイント内側」と S-1 を同時に解消する唯一の筋 |
| S-3 | 垂直スクロールバーの視覚行対応(§4.4)。全論理行の視覚行数キャッシュが前提=別テーマ |
| S-4 | `VerticalNavigation` がキー 1 打ごとに現在行・移動先行を完全 Wrap している(巨大 1 行で毎回 ~30 ms)。`WrapThroughOffset` / `WrapFirstSegments` で打ち切れる可能性がある |
| S-5 | E-1 が A-6 修正後も再現する場合は UIA 側の独立欠陥として起票する(§1.3) |

### 実装中に決まった追記(2026-08-23・Task 7)

策定時のスナップショット(§1〜§7)は変更していない。以下は実装で判明し、
実装計画の実施記録から回収した申し送りである。

| ID | 内容 |
|----|------|
| S-6 | **`OnPaint` での `_topSegment` 自己修復案は却下した**。`ViewportLayout.Build` が返す `rows[0].SegmentIndex` を `_topSegment` へ書き戻せば陳腐化(編集で先頭段落が縮んでも `_topSegment` をリセットしない設計=§4.1)が無料で消える、という案が Task 3 の品質レビューで出た。却下の理由は 2 つ。(a) **描画中に状態を書くのは再入・順序の観点でリスク**(`Invalidate` を誘発する経路との相互作用、`OnPaint` が UI スレッド以外から来ないという前提への依存が増える)。(b) **残る実害が確認できない**=陳腐化は `AfterEdit` → `BringCaretIntoView` の第 1 分岐(辞書順比較)が編集直後に必ず修復するため、1 フレームを跨いで残らない。**将来 `BringCaretIntoView` の呼び出し構造を変えるとき(S-8)は再検討の価値がある**=第 1 分岐が自己修復を担っている事実が前提になっているため |
| S-7 | **`PointFromCharOffset` の `Point.Empty` は「不可視」と「可視域最上段の行頭」を弁別できない**。`Point.Empty == new Point(0, 0)` であり、可視域最上段の行頭は座標も `(0, 0)` になる。public API の設計上の瑕疵。テストは `ComputeCaretPoint` の `Visible` フラグで弁別している(Task 4 で計画のテスト期待値が原理的に矛盾していたのはこれが原因)。戻り値を `Point?` にするのが筋だが public API の破壊的変更=別テーマ |
| S-8 | **`BringCaretIntoView` が 1 打鍵で 2 回呼ばれる**(`EditorControl.Caret.cs` の `SetCaretCharOffset` 内の追従と、`InputRouter.cs` の `ApplyNavMove` 末尾の明示呼び出し)。**本ブランチ以前からの構造**で本ブランチは呼び出し元を変えていないが、折り返し ON では 1 打鍵あたりの `LineLayout.WrapThroughOffset` を **2 倍**にしている(実測=実装計画 Task 5 の実施記録)。重複解消は別テーマ。S-4 と併せて扱うのが筋 |
| S-9 | **ホイール 1 ノッチの送り量(絶対量)の網は本ブランチで張ったが、既存 `MouseInputTests`(折り返し OFF 経路)は今も「TopLine が増えた/減った」しか見ていない**。`OnMouseWheel` の `wheelLines` に +1 する変異は折り返し OFF 側では今も生存する。本ブランチの変更対象外のため触っていない |
