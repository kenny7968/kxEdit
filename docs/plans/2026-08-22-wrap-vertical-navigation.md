# 折り返し ON の垂直移動(A-5 / A-6)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 折り返し ON のとき ↑ が効かなくなる / ↓ が視覚行を飛ばす(A-5)と、キャレットが可視域外へ出ても
画面が追従しない(A-6)を根治する。

**Architecture:** 設計書 [`2026-08-22-wrap-vertical-navigation-design.md`](./2026-08-22-wrap-vertical-navigation-design.md)
の不変条件 I-1〜I-4 を実装する。A-5 は Core の着地クランプ 1 箇所。A-6 は EditorControl に
`_topSegment`(可視域最上段の視覚セグメント index)を導入し、可視判定・スクロール判断・座標算出・
ヒットテストを論理行から視覚行へ移す。折り返し OFF では `_topSegment ≡ 0` で全式が現行に退化する。

**Tech Stack:** .NET 9 / C# / WinForms / xUnit。整形は CSharpier(pre-commit フックが自動実行)。

---

## 全タスク共通のルール

**ビルドとテスト**(`kxEdit.sln` のあるリポジトリルートで実行):

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests   -c Release --no-build
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests    -c Release --no-build
```

- **赤を確認する段では `--no-build` を付けない**(古いバイナリで走ると「落ちるはずのテストが緑」
  「変異させたのに緑」を誤認する。過去ブランチで実際に踏んだ事故)。
- **`--filter` で 1 件に絞った結果からミューテーションの結論を出さない**。変異が本当に kill されたかは
  対象プロジェクト全件で確認する(絞ると別の網が拾っていることを見落とす)。
- **ミューテーション検証は必ずコミットしてから行う**。コミット前に `git checkout -- src/` で復元すると
  **未コミットの実装ごと消える**(Task 1 で実際に踏んだ。復元手順が実装を破壊し、差分が空になって発覚)。
  順序は「テスト緑 → コミット → 変異 → `git checkout -- src/` で復元 → 全緑を再確認」。
  変異で網の穴が見つかったら、テストを足して**別コミット**を積む(元コミットは書き換えない)。
- 0 warning を維持する(`-warnaserror` 稼働中)。
- コミットは `--no-verify` を使わない(CSharpier 整形+ローカルパス検出フックを通す)。
- コミットメッセージは日本語。末尾に `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`。

**タスクごとのレビュー**(CLAUDE.md §3-4):

| タスク | レビュー |
|--------|----------|
| Task 1, 2, 4, 5, 6, 7 | 仕様レビュー(実装・テストが本計画どおりか) |
| **Task 3** | 仕様レビュー + **コード品質レビュー**(後続 3 タスクが依存する新しい seam を導入するため=前倒し例外) |

**用語**: 「視覚行」= 折り返し後の 1 行 = `WrapSegment` 1 個。「論理行」= 改行で区切られた 1 行。

---

## Task 1: A-5 — 非最終セグメントへの着地をクランプする

**Files:**
- Modify: `src/kxEdit.Core/Layout/VisualSegments.cs`(先頭に `using kxEdit.Core.Text;` を追加)
- Modify: `src/kxEdit.Core/Editing/VerticalNavigation.cs:136-143`
- Test: `tests/kxEdit.Core.Tests/Layout/VisualSegmentsTests.cs`(追記)
- Test: `tests/kxEdit.Core.Tests/Editing/VerticalNavigationTests.cs`(追記)

### Step 1: 失敗するテストを書く(A-5 の再現)

`tests/kxEdit.Core.Tests/Editing/VerticalNavigationTests.cs` の末尾(クラス閉じ括弧の直前)に追記:

```csharp
    // ===== A-5(2026-08-22 監査): 視覚行の右端に着地するケース =====
    // 不変条件 I-1: 非最終セグメントへの着地は segEnd 未満でなければならない。
    // segEnd は描画・照会の双方で「次の視覚行の先頭」を意味するため、そこへ着地すると
    // ↓ は 1 行飛んで見え、↑ は同じ値へ着地し続けて動かなくなる。
    //
    // fixture: wrapColumns=4 / 半角 8px → maxWidthPx=32。
    //   行 0 "abcd"          → 視覚 [(0,4)]                     … 幅ぴったり 32px = 1 本
    //   行 1 "xxxxyyyyzzzz"  → 視覚 [(0,4),(4,4),(8,4)]         … 絶対 offset 5,9,13
    //   行 2 "end"           → 視覚 [(0,3)]                     … 絶対 offset 18
    private const string WrapFixture = "abcd\nxxxxyyyyzzzz\nend";

    [Fact]
    public void MoveDown_WithWrap_FromLineEnd_LandsInFirstVisualRow_NotSecond()
    {
        // 行 0 の行末(caret=4 / desiredPx=32=右端)から ↓。
        // 移動先は行 1 の視覚行 0 = "xxxx"(絶対 [5,9))。
        // 修正前は 9(= "yyyy" の行頭 = 視覚行 1 の先頭)に着地して 1 行飛んでいた。
        var s = Snap(WrapFixture);
        var (t, d) = VerticalNavigation.MoveDown(s, caret: 4, currentDesiredPx: -1, wrapColumns: 4, M);
        Assert.Equal(32, d);
        Assert.InRange(t, 5, 8); // 視覚行 0 の内側(9=次の視覚行の先頭 ではない)
        Assert.Equal(8, t); // 右端 = 最後のコードポイントの先頭
    }

    [Fact]
    public void MoveUp_WithWrap_FromRightEdge_ActuallyMovesEveryTime()
    {
        // A-5 の主症状。右端の desiredPx を保ったまま ↑ を 3 回押し、毎回 caret が動くこと。
        // 修正前は 1 回目以降ずっと同じ値に着地して「↑ が効かない」状態だった。
        var s = Snap(WrapFixture);
        // 行 1 の視覚行 2("zzzz")の右端に相当する位置=行末(17)から開始する。
        int caret = 17;
        int desired = -1;
        var visited = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            int before = caret;
            (caret, desired) = VerticalNavigation.MoveUp(s, caret, desired, wrapColumns: 4, M);
            Assert.True(caret < before, $"↑ {i + 1} 回目で caret が動いていない (before={before}, after={caret})");
            visited.Add(caret);
        }
        // 視覚行 1 の右端 → 視覚行 0 の右端 → 行 0 の行末
        Assert.Equal(new[] { 12, 8, 4 }, visited);
    }

    [Fact]
    public void MoveDown_WithWrap_RightEdge_TraversesEachVisualRowOnce()
    {
        // ↓ 側の対称テスト。右端を保ったまま 1 視覚行ずつ降りること(飛ばさないこと)。
        var s = Snap(WrapFixture);
        int caret = 4; // 行 0 の行末
        int desired = -1;
        var visited = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            (caret, desired) = VerticalNavigation.MoveDown(s, caret, desired, wrapColumns: 4, M);
            visited.Add(caret);
        }
        // 視覚行 0 の右端 → 視覚行 1 の右端 → 視覚行 2 は最終セグメント=行末(17)まで行ける
        Assert.Equal(new[] { 8, 12, 17 }, visited);
    }

    [Fact]
    public void MoveDown_WithWrap_LastSegment_StillLandsAtLogicalLineEnd()
    {
        // クランプの過剰適用防止。最終セグメントは segEnd(=行末)に着地してよい
        // (そこは「次の視覚行の先頭」ではないため)。
        var s = Snap(WrapFixture);
        // 行 2 "end" は 1 セグメント=最終。右端 desiredPx で ↓ すると行末(21)。
        var (t, _) = VerticalNavigation.MoveDown(s, caret: 13, currentDesiredPx: 32, wrapColumns: 4, M);
        Assert.Equal(21, t); // 18 + 3 = "end" の行末
    }

    [Fact]
    public void MoveDownThenUp_WithWrap_RightEdge_ReturnsToOriginalVisualRow()
    {
        // 往復。desiredPx を保持しているので元の視覚行へ戻る。
        var s = Snap(WrapFixture);
        var (down, d1) = VerticalNavigation.MoveDown(s, caret: 4, currentDesiredPx: -1, wrapColumns: 4, M);
        var (up, _) = VerticalNavigation.MoveUp(s, down, d1, wrapColumns: 4, M);
        Assert.Equal(4, up);
    }
```

`using System.Collections.Generic;` が必要なら追加する(ImplicitUsings 有効なら不要)。

### Step 2: 赤を確認する

```powershell
dotnet test tests/kxEdit.Core.Tests -c Release --filter "FullyQualifiedName~VerticalNavigationTests"
```

期待: 5 件中 4 件が FAIL。
- `MoveDown_WithWrap_FromLineEnd_...` → 先行する `Assert.InRange(t, 5, 8)` が Actual `9` で失敗
  (`Assert.Equal(8, t)` まで到達しない。原因は同一)
- `MoveUp_WithWrap_FromRightEdge_...` → 2 回目の `caret < before` で失敗
- `MoveDown_WithWrap_RightEdge_...` → **実測は `[9, 17, 21]`**(策定時の予測 `[9, 13, 17]` は誤り)。
  caret=9 は視覚行 1 の先頭と解釈されるため 2 回目の ↓ が視覚行 2 へ跳び(17)、3 回目で行 2 へ抜ける(21)。
  **A-5 の「1 行飛ばし」は 1 回では済まず、押すたびに視覚行を 1 本ずつ食い潰していく**
- `MoveDownThenUp_...` → 失敗
- `MoveDown_WithWrap_LastSegment_...` → **PASS**(既に正しい挙動=クランプの過剰適用を検出する網)

### Step 3: `ClampLandingOffset` を実装する

`src/kxEdit.Core/Layout/VisualSegments.cs` の 1 行目に `using kxEdit.Core.Text;` を追加し、
`FindContaining` の直後(クラス閉じ括弧の直前)に追記:

```csharp
    /// <summary>
    /// 視覚行への「キャレットの着地オフセット」を規約に合う範囲へクランプする(設計書 不変条件 I-1)。
    /// </summary>
    /// <param name="segment">着地先の視覚セグメントの本文(セグメント先頭を 0 とする span)。</param>
    /// <param name="localOffset">セグメント先頭からの着地オフセット([0, segment.Length])。</param>
    /// <param name="isFinalSegment">着地先が論理行の最終セグメントなら true。</param>
    /// <remarks>
    /// <para>
    /// <b>なぜ必要か</b>: <see cref="FindContaining"/> は <c>offsetInLine == segEnd</c> を
    /// 「<b>次の</b>視覚行の先頭」と判定する(最終セグメントのみ例外)。描画側
    /// (<c>EditorControl.ComputeCaretPoint</c>)も同じ規約なので、非最終セグメントの
    /// <c>segEnd</c> へキャレットを着地させると「歩いた視覚行」と「描画される視覚行」が
    /// 1 本ずれる。その結果 ↓ は 1 行飛んで見え、↑ は 1 つ戻した先で同じ <c>segEnd</c> に
    /// 再着地して動かなくなる(2026-08-22 監査 A-5)。
    /// </para>
    /// <para>
    /// クランプ先は最後のコードポイントの<b>先頭</b>=サロゲートペアを割らない。
    /// </para>
    /// <para>
    /// <b>マウス経路には適用しない</b>: ドラッグ選択の端点としては <c>segEnd</c> が正しく、
    /// クランプすると視覚行の最後の 1 文字が選択から漏れる(設計書 §3.2)。
    /// </para>
    /// </remarks>
    public static int ClampLandingOffset(
        ReadOnlySpan<char> segment,
        int localOffset,
        bool isFinalSegment
    )
    {
        if (isFinalSegment || localOffset < segment.Length)
            return localOffset;
        if (segment.Length == 0)
            return 0; // Wrap 契約上、空セグメントは [(0,0)] の 1 本=常に最終。到達しない防御
        return TextBoundary.SnapToCodePointStart(segment, segment.Length - 1);
    }
```

`src/kxEdit.Core/Editing/VerticalNavigation.cs:136-143` を次に置き換える:

```csharp
        var targetSegs = LineLayout.Wrap(targetLineText, maxWidthPx, metrics);
        // WalkVisualRows はセグメント数を歩きながら渡すので通常はここでのクランプは不要だが、
        // 折り返しなし経路(targetSegIdx=0 固定)と併せて防御的にクランプする。
        int usedSegIdx = Math.Min(targetSegIdx, targetSegs.Count - 1);
        var targetSeg = targetSegs[usedSegIdx];
        var targetSpan = targetLineText.AsSpan(targetSeg.OffsetInLine, targetSeg.Length);
        int localTarget = PixelMapper.PxToOffset(targetSpan, desiredPx, metrics);
        // 不変条件 I-1: 非最終セグメントの segEnd は「次の視覚行の先頭」なので着地させない。
        // ここを外すと ↓ が視覚行を飛ばし ↑ が固着する(2026-08-22 監査 A-5)。
        localTarget = VisualSegments.ClampLandingOffset(
            targetSpan,
            localTarget,
            isFinalSegment: usedSegIdx == targetSegs.Count - 1
        );
        int newCaret = targetLineStart + targetSeg.OffsetInLine + localTarget;
        return (newCaret, desiredPx);
```

### Step 4: `ClampLandingOffset` 単体のテストを追加する

`tests/kxEdit.Core.Tests/Layout/VisualSegmentsTests.cs` のクラス末尾に追記:

```csharp
    // ===== ClampLandingOffset(設計書 I-1)=====

    [Fact]
    public void ClampLandingOffset_FinalSegment_KeepsSegEnd()
    {
        // 最終セグメントの segEnd は論理行の行末=正当なキャレット位置なので触らない。
        Assert.Equal(4, VisualSegments.ClampLandingOffset("abcd", 4, isFinalSegment: true));
    }

    [Fact]
    public void ClampLandingOffset_NonFinalSegment_ClampsToLastCodePointStart()
    {
        Assert.Equal(3, VisualSegments.ClampLandingOffset("abcd", 4, isFinalSegment: false));
    }

    [Fact]
    public void ClampLandingOffset_NonFinalSegment_Interior_Unchanged()
    {
        Assert.Equal(2, VisualSegments.ClampLandingOffset("abcd", 2, isFinalSegment: false));
    }

    [Fact]
    public void ClampLandingOffset_NonFinalSegment_SurrogateTail_DoesNotSplitPair()
    {
        // "a" + U+1F600(サロゲートペア)= 3 code unit。末尾から 1 引くと low サロゲート位置に
        // なるので、ペア先頭(=1)まで戻ること。
        Assert.Equal(1, VisualSegments.ClampLandingOffset("a😀", 3, isFinalSegment: false));
    }

    [Fact]
    public void ClampLandingOffset_EmptySegment_ReturnsZero()
    {
        Assert.Equal(0, VisualSegments.ClampLandingOffset("", 0, isFinalSegment: false));
    }
```

### Step 5: 緑を確認する

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests   -c Release --no-build
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests    -c Release --no-build
```

期待: 3 プロジェクト全緑・0 warning。**既存テストの変更は 0 件**であること
(A-5 の修正が既存の被覆範囲を壊していない証拠)。

### Step 6: ミューテーション検証(2 件)

> **実装時に判明した手順の誤り(2026-08-22)**: 本 Step は当初 Step 7(コミット)より前に置いていたが、
> 末尾の復元手順 `git checkout -- src/` は**未コミットの実装ごと消す**。実装中に実際にこれを踏んだ。
> **正しい順序は Step 7(コミット)→ Step 6(変異 → 復元 → 全緑再確認)**。共通ルールに反映済み。

`--no-build` を付けずに実行すること。

1. `ClampLandingOffset` の `isFinalSegment ||` を削って `if (localOffset < segment.Length)` にする
   → `ClampLandingOffset_FinalSegment_KeepsSegEnd` と
   `MoveDown_WithWrap_LastSegment_StillLandsAtLogicalLineEnd` が赤くなること。
2. `localOffset < segment.Length` を `localOffset <= segment.Length` にする
   → `MoveUp_WithWrap_FromRightEdge_ActuallyMovesEveryTime` が赤くなること。

いずれも確認後 `git checkout -- src/` で**必ず復元**し、復元後に全緑を再確認する
(変異を戻し忘れたままコミットした事故が過去にある)。

### Step 7: コミット

```powershell
git add src/kxEdit.Core/Layout/VisualSegments.cs src/kxEdit.Core/Editing/VerticalNavigation.cs tests/kxEdit.Core.Tests
git commit -F - <<'EOF'
fix(core): 折り返し ON の垂直移動で非最終視覚行の segEnd に着地しない(A-5)

VerticalNavigation の着地オフセットが、移動先が非最終セグメントのときに
segEnd(=次の視覚行の先頭)になり得た。segEnd は VisualSegments.FindContaining と
ComputeCaretPoint の双方で「次の視覚行」と解釈されるため、歩いた視覚行と
描画される視覚行が 1 本ずれ、↓ は 1 行飛び、↑ は同じ値に着地し続けて固着していた。

不変条件を VisualSegments.ClampLandingOffset として明示し、
MoveVerticalRelative(MoveUp/MoveDown/PageUp/PageDown の唯一の実装)で適用する。
PixelMapper.PxToOffset は純関数のまま据え置く(マウス経路と共有しており、
ドラッグ選択の端点としては segEnd が正しいため)。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

## Task 2: `ViewportLayout.Build` に topSegment を足す(挙動不変)

**Files:**
- Modify: `src/kxEdit.Core/Layout/ViewportLayout.cs`
- Modify(呼び出し側・`topSegment: 0` を渡すだけ): `src/kxEdit.Editor/EditorControl.Paint.cs:34`、
  `src/kxEdit.Editor/EditorControl.cs:390`(`GetVisibleCharRange`)、
  `src/kxEdit.Editor/EditorControl.cs:1072`(`UpdateHorizontalScrollbar`)
- Test: `tests/kxEdit.Core.Tests/Layout/ViewportLayoutTests.cs`、
  `tests/kxEdit.Core.Tests/Layout/ViewportLayoutPrefixTests.cs`、
  `tests/kxEdit.Core.Tests/Layout/FrameBuilderTests.cs`(いずれも引数追加の機械的修正)

**注意**: **省略可能引数(`int topSegment = 0`)にしない**。必須引数にすることで全呼び出し元に
「この経路の起点はどこか」を一度考えさせる(既定値で素通りさせない)。

### Step 1: 失敗するテストを書く

`tests/kxEdit.Core.Tests/Layout/ViewportLayoutTests.cs` のクラス末尾に追記:

```csharp
    [Fact]
    public void TopSegment_skips_leading_visual_rows_of_first_line()
    {
        // "aaaaaa"(6 文字)を wrapColumns=2(=maxWidthPx 2px)で折り返すと視覚 [(0,2),(2,2),(4,2)]。
        // topSegment=1 なら先頭 1 本を読み飛ばして 2 本目から積む(y は 0 から始まる)。
        var buf = TextBuffer.FromString("aaaaaa");
        var rows = ViewportLayout.Build(
            buf.Current,
            topLine: 0,
            topSegment: 1,
            heightPx: 20,
            wrapColumns: 2,
            M
        );

        Assert.Equal(2, rows.Count);
        Assert.Equal(new VisualRow(0, 1, 2, 2, 0), rows[0]);
        Assert.Equal(new VisualRow(0, 2, 4, 2, 10), rows[1]);
    }

    [Fact]
    public void TopSegment_beyond_segment_count_clamps_to_last_segment()
    {
        // 編集で段落が縮み topSegment が実際のセグメント数以上になった場合の防御。
        // 最終セグメントへクランプする(空リストを返して真っ白にしない)。
        var buf = TextBuffer.FromString("aaaaaa");
        var rows = ViewportLayout.Build(
            buf.Current,
            topLine: 0,
            topSegment: 99,
            heightPx: 20,
            wrapColumns: 2,
            M
        );

        Assert.Single(rows);
        Assert.Equal(new VisualRow(0, 2, 4, 2, 0), rows[0]);
    }

    [Fact]
    public void TopSegment_applies_only_to_the_first_logical_line()
    {
        // 2 行目以降は常に先頭視覚行から積む。
        var buf = TextBuffer.FromString("aaaa\nbbbb");
        var rows = ViewportLayout.Build(
            buf.Current,
            topLine: 0,
            topSegment: 1,
            heightPx: 40,
            wrapColumns: 2,
            M
        );

        Assert.Equal(3, rows.Count);
        Assert.Equal(new VisualRow(0, 1, 2, 2, 0), rows[0]);
        Assert.Equal(new VisualRow(1, 0, 5, 2, 10), rows[1]);
        Assert.Equal(new VisualRow(1, 1, 7, 2, 20), rows[2]);
    }
```

### Step 2: 赤を確認する

```powershell
dotnet test tests/kxEdit.Core.Tests -c Release --filter "FullyQualifiedName~ViewportLayoutTests"
```

期待: **コンパイルエラー**(`Build` に 6 引数版が無い)。これがこの段階の「赤」。

### Step 3: `ViewportLayout.Build` を実装する

`src/kxEdit.Core/Layout/ViewportLayout.cs` のシグネチャと doc を更新し、本体のループ先頭を変更する。

```csharp
    /// <summary>
    /// (topLine, topSegment) 以降を積み上げて heightPx を満たす分だけ VisualRow を返す。
    /// - wrapColumns&lt;=0: 折り返し OFF(1 論理行=1 視覚行)。<paramref name="topSegment"/> は常に 0 の想定
    /// - wrapColumns&gt;0: 半角 wrapColumns 文字分の px を max として折り返しを各行に適用
    ///   (各行は「まだ積める視覚行数」までで打ち切って Wrap する=巨大 1 行でも O(可視行数))
    /// - <paramref name="topSegment"/>: 先頭論理行のうち読み飛ばす視覚行数(設計書 不変条件 I-2)。
    ///   実際のセグメント数以上なら最終セグメントへクランプする(編集で段落が縮んだ場合の防御)
    /// - 空文書(LineCount=1・CharLength=0)は topLine=0 なら "1 個空の視覚行"(EOF キャレット用)を返す
    /// - topLine が LineCount 以上なら空リスト
    /// </summary>
    public static IReadOnlyList<VisualRow> Build(
        TextSnapshot snapshot,
        int topLine,
        int topSegment,
        int heightPx,
        int wrapColumns,
        ICharMetrics metrics
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(metrics);

        var result = new List<VisualRow>();
        if (topLine < 0 || topLine >= snapshot.LineCount || heightPx <= 0)
            return result;
        if (topSegment < 0)
            topSegment = 0;

        int maxWidthPx = wrapColumns > 0 ? wrapColumns * metrics.MeasureRun("0") : 0;
        int lineHeight = metrics.LineHeightPx;

        int y = 0;
        for (int line = topLine; line < snapshot.LineCount; line++)
        {
            if (y >= heightPx)
                return result;

            int lineStart = snapshot.GetLineStart(line);
            int lineEndNoBreak = snapshot.GetLineEnd(line, includeBreak: false);
            int lineLen = lineEndNoBreak - lineStart;
            string lineText = lineLen == 0 ? string.Empty : snapshot.GetText(lineStart, lineLen);

            // 先頭論理行だけ topSegment 本を読み飛ばす。読み飛ばす分も Wrap の要求本数に足す
            // (打ち切り結果は完全結果の prefix なので、skip 本目以降は完全 Wrap と一致する)。
            int skip = line == topLine ? topSegment : 0;
            int rowsNeeded =
                lineHeight > 0 ? (heightPx - y + lineHeight - 1) / lineHeight : int.MaxValue;
            // skip 加算のオーバーフロー回避(rowsNeeded は lineHeight<=0 で int.MaxValue になる)
            long needed = (long)Math.Max(1, rowsNeeded) + skip;
            var segments = LineLayout
                .WrapFirstSegments(
                    lineText,
                    maxWidthPx,
                    metrics,
                    needed > int.MaxValue ? int.MaxValue : (int)needed
                )
                .Segments;
            // topSegment が実セグメント数以上=編集で段落が縮んだ。最終セグメントへ寄せる。
            if (skip >= segments.Count)
                skip = segments.Count - 1;
            for (int si = skip; si < segments.Count; si++)
            {
                if (y >= heightPx)
                    return result;
                var seg = segments[si];
                result.Add(
                    new VisualRow(
                        LogicalLine: line,
                        SegmentIndex: si,
                        SegmentStartChar: lineStart + seg.OffsetInLine,
                        SegmentLength: seg.Length,
                        YPx: y
                    )
                );
                y += lineHeight;
            }
        }
        return result;
    }
```

既存のコメント(2026-08-02 変更 B の説明・`Math.Max(1, ...)` の生存理由)は保持したまま、
上記の変更点だけを差し込むこと。

### Step 4: 呼び出し元 3 箇所に `topSegment: 0` を渡す

- `src/kxEdit.Editor/EditorControl.Paint.cs:34`
- `src/kxEdit.Editor/EditorControl.cs:390`(`GetVisibleCharRange`)
- `src/kxEdit.Editor/EditorControl.cs:1072`(`UpdateHorizontalScrollbar`。折り返し OFF 専用経路なので
  Task 4 以降も 0 のまま)

```csharp
var rows = ViewportLayout.Build(snap, _topLine, topSegment: 0, paintHeight, _wrapColumns, _metrics);
```

既存テスト(`ViewportLayoutTests` 8 件・`ViewportLayoutPrefixTests`・`FrameBuilderTests`)にも
`topSegment: 0` を機械的に足す。**引数追加以外の変更を混ぜない**。

### Step 5: 緑を確認する

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests   -c Release --no-build
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests    -c Release --no-build
```

期待: 全緑・0 warning。**Editor / App のテストは 1 行も変更していない**こと(挙動不変の証拠)。

### Step 6: コミット

```powershell
git add -A
git commit -m "refactor(core): ViewportLayout.Build に topSegment を足す(挙動不変・A-6 の下準備)

可視域の起点を視覚行にするため(設計書 I-2)、先頭論理行のうち読み飛ばす視覚行数を
引数に足した。既存呼び出し元は 3 箇所とも topSegment: 0 を渡すため挙動は不変。
省略可能引数にしないのは、将来の呼び出し元に起点を必ず意識させるため。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: EditorControl に視覚行の状態とヘルパを入れる(挙動不変)

> **このタスクは仕様レビューに加えてコード品質レビューを行う**(後続 3 タスクが依存する
> 新しい seam を導入するため。CLAUDE.md §3-4 の前倒し例外)。

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.cs`(フィールド・`TopLine` セッター・`SetTopPosition`・
  リセット規則・視覚行ヘルパ群)
- Test: `tests/kxEdit.Editor.Tests/VisualRowScrollTests.cs`(新規)

### Step 1: 失敗するテストを書く

`tests/kxEdit.Editor.Tests/VisualRowScrollTests.cs` を新規作成:

```csharp
using kxEdit.Core.Buffers;

namespace kxEdit.Editor.Tests;

/// <summary>
/// 2026-08-22 監査 A-6: 可視域の起点を視覚行 (TopLine, TopSegment) にする(設計書 不変条件 I-2)。
/// 本ファイルは状態とスクロール判断の契約を固定する。
/// 折り返し OFF では TopSegment が常に 0 で全式が現行に退化すること(I-3)も併せて守る。
/// </summary>
public class VisualRowScrollTests
{
    /// <summary>
    /// 可視行数を明示したエディタを作る。折り返し ON では HScrollBar が常に隠れるため
    /// PaintHeightPx == ClientSize.Height になり、可視行数をテストから固定できる
    /// (EditorControlWrapCaretTests.MakeControl と同じ流儀)。
    /// </summary>
    private static (Form f, EditorControl c) MakeControl(string text, int wrap, int visibleRows)
    {
        var f = new HostForm();
        var c = new EditorControl { WrapColumns = wrap };
        f.Controls.Add(c);
        _ = f.Handle;
        c.ClientSize = new System.Drawing.Size(800, c.LineHeightPx * visibleRows);
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    [Fact]
    public void TopSegment_IsZero_ByDefault() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                Assert.Equal(0, c.TopSegment);
            }
        });

    [Fact]
    public void SetTopPosition_KeepsSegment_AndTopLineSetterResetsIt() =>
        Sta.Run(() =>
        {
            // 非既定位置(TopSegment=2)から検証を始める=「0 のままだった」と区別する。
            var (f, c) = MakeControl("abcdefghij\nklmnop", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 2);
                Assert.Equal(0, c.TopLine);
                Assert.Equal(2, c.TopSegment);

                // TopLine セッターは「その行の先頭視覚行から」の意味を保つ=TopSegment を 0 に戻す。
                // 同じ論理行を代入しても戻ること(早期 return に潰されないこと)。
                c.TopLine = 0;
                Assert.Equal(0, c.TopLine);
                Assert.Equal(0, c.TopSegment);
            }
        });

    [Fact]
    public void WrapColumnsSetter_ResetsTopSegment() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 2);
                Assert.Equal(2, c.TopSegment);
                c.WrapColumns = 4; // 折り返し幅が変わればセグメント分割そのものが変わる
                Assert.Equal(0, c.TopSegment);
            }
        });

    [Fact]
    public void ReplaceSource_ResetsTopSegment() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 2);
                c.ReplaceSource(TextBuffer.FromString("xyz"));
                Assert.Equal(0, c.TopSegment);
                Assert.Equal(0, c.TopLine);
            }
        });

    [Fact]
    public void SetTopPosition_ClampsLine_AndDropsSegmentWhenLineClamped() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                // 論理行 1 本しかないので line=5 は 0 にクランプされる。
                // 行がクランプされたときは segment の意味が失われるので 0 にする。
                c.SetTopPosition(5, 3);
                Assert.Equal(0, c.TopLine);
                Assert.Equal(0, c.TopSegment);
            }
        });
}
```

`HostForm` は `tests/kxEdit.Editor.Tests/TestHost.cs`、`Sta` は同 `Sta.cs` の既存ヘルパ。

### Step 2: 赤を確認する

```powershell
dotnet test tests/kxEdit.Editor.Tests -c Release --filter "FullyQualifiedName~VisualRowScrollTests"
```

期待: **コンパイルエラー**(`TopSegment` / `SetTopPosition` が無い)。

### Step 3: 状態とリセット規則を実装する

`src/kxEdit.Editor/EditorControl.cs`:

1. `private int _topLine;`(`:42`)の直後にフィールドを追加。

```csharp
    // 2026-08-22 A-6: 可視域最上段が属する視覚セグメント index(設計書 不変条件 I-2)。
    // 折り返し OFF では常に 0=全式が導入前に退化する(I-3)。
    // 「セグメント index の意味が変わる契機」では 0 に戻す(SetSource / ReplaceSource /
    // TopLine セッター / WrapColumns セッター / ApplyAppearance / VScrollBar の防御クランプ)。
    // 編集ではリセットしない(巨大段落の途中を編集するたび段落先頭へ飛ぶのを避ける。
    // 実セグメント数を超えた場合は ViewportLayout.Build 側でクランプされる)。
    private int _topSegment;
```

2. `TopLine` プロパティの近くに公開(internal)アクセサを追加。

```csharp
    /// <summary>可視域最上段の視覚セグメント index(設計書 I-2)。折り返し OFF では常に 0。</summary>
    internal int TopSegment => _topSegment;
```

3. `TopLine` セッターを差し替える(早期 return が `_topSegment` を取り残さないようにする)。

```csharp
        set
        {
            int clamped = ClampTopLine(value);
            // 同じ論理行への代入でも「その行の先頭視覚行から」の意味を回復させるため、
            // _topSegment != 0 のときは早期 return しない(2026-08-22 A-6)。
            if (clamped == _topLine && _topSegment == 0)
                return;
            _topLine = clamped;
            _topSegment = 0;
            if (_vscroll.Value != clamped)
                _vscroll.Value = clamped;
            PositionCaret();
            Invalidate();
        }
```

4. `SetTopPosition` を追加(`TopLine` セッターの直後)。

```csharp
    /// <summary>
    /// 可視域の起点を<b>視覚行</b>単位で設定する(設計書 I-2)。<see cref="TopLine"/> セッターと違い
    /// <see cref="TopSegment"/> を保つ=巨大段落の途中を先頭に置ける。
    /// 論理行がクランプされた場合はセグメント index の意味が失われるので 0 に落とす。
    /// </summary>
    /// <remarks>
    /// VScrollBar は論理行基準のまま(Value = TopLine)である。段落の途中をスクロールしている間
    /// サムは動かず、論理行 1 本の文書ではバーが無効のままになる=意識的な近似
    /// (全文の視覚行数を数えると O(文書) になり PR #35 の退行になるため。設計書 §4.4 / 申し送り S-3)。
    /// </remarks>
    internal void SetTopPosition(int line, int segment)
    {
        int clampedLine = ClampTopLine(line);
        int clampedSeg = clampedLine == line ? Math.Max(0, segment) : 0;
        if (clampedLine == _topLine && clampedSeg == _topSegment)
            return;
        _topLine = clampedLine;
        _topSegment = clampedSeg;
        if (_vscroll.Value != clampedLine)
            _vscroll.Value = clampedLine;
        PositionCaret();
        Invalidate();
    }
```

5. リセット箇所に `_topSegment = 0;` を足す。

| 場所 | 変更 |
|------|------|
| `SetSource`(`:215` の `_topLine = 0;` の直後) | `_topSegment = 0;` |
| `ReplaceSource`(`:273` の `_topLine = 0;` の直後) | `_topSegment = 0;` |
| `WrapColumns` セッター(`_wrapColumns = clamped;` の直後) | `_topSegment = 0;` |
| `ApplyAppearance`(`_wrapColumns` 更新の直後) | `_topSegment = 0;` |
| `UpdateVerticalScrollbar` の防御クランプ | `if (_topLine > maxLine) { _topLine = maxLine; _topSegment = 0; }` |

### Step 4: 視覚行ヘルパを実装する

`src/kxEdit.Editor/EditorControl.cs` の `ComputeCaretPoint` の近くに追加する。

```csharp
    /// <summary>折り返し幅(px)。折り返し OFF は 0(=LineLayout.Wrap が単一セグメントを返す)。</summary>
    private int MaxWrapWidthPx => _wrapColumns > 0 ? _wrapColumns * _metrics.MeasureRun("0") : 0;

    /// <summary>論理行 1 本の本文(改行を含まない)。空行は空文字列。</summary>
    private static string LineTextOf(TextSnapshot snap, int line)
    {
        int ls = snap.GetLineStart(line);
        int le = snap.GetLineEnd(line, includeBreak: false);
        return le == ls ? string.Empty : snap.GetText(ls, le - ls);
    }

    /// <summary>
    /// 論理行内オフセットが属する視覚セグメントの index を返す(設計書 I-2 の単一定義)。
    /// 最終セグメントに限り「末尾ちょうど」も許容する(EOL キャレット位置)。
    /// </summary>
    /// <remarks>
    /// <paramref name="reachedLineEnd"/> が false(打ち切られた結果)のとき、最後の要素は
    /// 論理行の最終セグメントとは限らないため EOL 分岐を発火させてはならない。
    /// 詳細は <see cref="ComputeCaretPoint"/> 本体のコメント(「本当に load-bearing なのは
    /// LineLayout.WrapCore の &gt; 1 文字」)を参照。
    /// </remarks>
    private static int LocateSegmentIndex(
        IReadOnlyList<WrapSegment> segments,
        bool reachedLineEnd,
        int offsetInLine
    )
    {
        int segIdx = segments.Count - 1;
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            int segEnd = seg.OffsetInLine + seg.Length;
            if (
                offsetInLine < segEnd
                || (reachedLineEnd && i == segments.Count - 1 && offsetInLine == segEnd)
            )
            {
                segIdx = i;
                break;
            }
        }
        return segIdx;
    }

    /// <summary>
    /// char offset の視覚行位置 (論理行, セグメント index) を返す(設計書 I-2)。
    /// 折り返し OFF は Wrap を一切呼ばず (論理行, 0) を返す=I-3。
    /// </summary>
    private (int Line, int Seg) LocateVisualRow(TextSnapshot snap, int offset)
    {
        int line = snap.GetLineIndexOfChar(offset);
        if (_wrapColumns <= 0)
            return (line, 0);
        int lineStart = snap.GetLineStart(line);
        int offsetInLine = offset - lineStart;
        var wrapped = LineLayout.WrapThroughOffset(
            LineTextOf(snap, line),
            MaxWrapWidthPx,
            _metrics,
            offsetInLine
        );
        return (line, LocateSegmentIndex(wrapped.Segments, wrapped.ReachedLineEnd, offsetInLine));
    }

    /// <summary>
    /// 論理行 <paramref name="line"/> の視覚行数を最大 <paramref name="cap"/> 本まで数える
    /// (設計書 I-4: 打ち切れる歩きは必ず打ち切る)。
    /// Exact=false なら実際の本数は Count より多い。
    /// </summary>
    private (int Count, bool Exact) SegmentCountCapped(TextSnapshot snap, int line, int cap)
    {
        var r = LineLayout.WrapFirstSegments(
            LineTextOf(snap, line),
            MaxWrapWidthPx,
            _metrics,
            Math.Max(1, cap)
        );
        return (r.Segments.Count, r.ReachedLineEnd);
    }

    /// <summary>
    /// (fromLine, fromSeg) から (toLine, toSeg) までの視覚行距離を数える。
    /// <paramref name="cap"/> 本を超えたら <paramref name="cap"/> を返して打ち切る(I-4)。
    /// 「可視域 visibleRows 本に収まるか」の判定にだけ使うため、cap 超過の正確な値は要らない。
    /// </summary>
    private int CountVisualRowsForward(
        TextSnapshot snap,
        int fromLine,
        int fromSeg,
        int toLine,
        int toSeg,
        int cap
    )
    {
        if (toLine < fromLine)
            return 0;
        if (toLine == fromLine)
            return Math.Min(cap, Math.Max(0, toSeg - fromSeg));

        int rows = 0;
        for (int line = fromLine; line < toLine; line++)
        {
            if (rows >= cap)
                return cap;
            int skip = line == fromLine ? fromSeg : 0;
            long needed = (long)(cap - rows) + skip;
            var (count, _) = SegmentCountCapped(
                snap,
                line,
                needed > int.MaxValue ? int.MaxValue : (int)needed
            );
            int eff = Math.Min(skip, count - 1);
            rows += count - eff;
        }
        return Math.Min(cap, rows + toSeg);
    }

    /// <summary>視覚行を n 本ぶん前へ進めた位置を返す。文書末で打ち切る。</summary>
    private (int Line, int Seg) WalkForwardVisualRows(TextSnapshot snap, int line, int seg, int n)
    {
        while (n > 0)
        {
            // この行に「seg + n」本目があるかだけ判れば良いので打ち切って数える(I-4)。
            long cap = (long)seg + n + 1;
            var (count, _) = SegmentCountCapped(
                snap,
                line,
                cap > int.MaxValue ? int.MaxValue : (int)cap
            );
            if (seg + n < count)
                return (line, seg + n);
            n -= count - seg; // この行の残り本数 + 次行先頭へ移る 1 本
            if (line + 1 >= snap.LineCount)
                return (line, count - 1); // 文書末で打ち切り
            line++;
            seg = 0;
        }
        return (line, seg);
    }

    /// <summary>視覚行を n 本ぶん遡った位置を返す。文書頭で打ち切る。</summary>
    /// <remarks>
    /// 前の論理行へ入るときだけ<b>正確な</b>視覚行数が要る(最終セグメントから数えるため)ので、
    /// そこは打ち切れない完全 Wrap になる。巨大行を下から遡る場合の 1 回だけで、
    /// PR #35 の幅メモ化により CJK 500K 行で約 30 ms(設計書 §5)。
    /// </remarks>
    private (int Line, int Seg) WalkBackVisualRows(TextSnapshot snap, int line, int seg, int n)
    {
        while (n > 0)
        {
            if (seg >= n)
                return (line, seg - n);
            n -= seg; // (line, 0) までで seg 本
            if (line == 0)
                return (0, 0); // 文書頭で打ち切り
            line--;
            var segs = LineLayout.Wrap(LineTextOf(snap, line), MaxWrapWidthPx, _metrics);
            seg = segs.Count - 1; // 前行の最終視覚行へ移る = さらに 1 本
            n--;
        }
        return (line, seg);
    }
```

`WrapSegment` / `LineLayout` / `TextSnapshot` の using は既存の `EditorControl.cs` に揃っている
(`kxEdit.Core.Layout` / `kxEdit.Core.Buffers`)。

### Step 5: 緑を確認する

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests   -c Release --no-build
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests    -c Release --no-build
```

期待: 全緑・0 warning。この時点では新ヘルパを本番経路が誰も呼んでいないので**挙動は不変**
(既存 Editor / App テストは 1 行も変更していないこと)。

### Step 6: コミットし、コード品質レビューを依頼する

```powershell
git add -A
git commit -m "feat(editor): 可視域の起点を視覚行にする状態とヘルパを入れる(挙動不変・A-6)

_topSegment(可視域最上段の視覚セグメント index)と、視覚行を数える/歩くヘルパ群を追加した。
本番経路はまだ誰も呼ばないため挙動は不変。折り返し OFF では LocateVisualRow が Wrap を
一切呼ばず (論理行, 0) を返す=以降の全経路が現行式に退化する(設計書 I-3)。

歩きは I-4 に従い打ち切る。唯一打ち切れないのは WalkBackVisualRows が前の論理行へ入るとき
(最終セグメントから数えるため正確な本数が要る)で、これは設計書 §5 で受容した箇所。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

レビュー観点(別エージェントへ渡す):
- ヘルパ 5 本の境界(`cap` ちょうど・文書頭/末・空行・`seg` が実数を超える場合)
- `long` 経由のオーバーフロー回避が必要な箇所を落としていないか
- 折り返し OFF で追加コスト(Wrap 呼び出し)が発生していないか
- リセット規則の抜け(`_topLine = 0` を書いている箇所を全部拾えているか)

---

## Task 4: 座標・ヒットテスト・可視域報告を topSegment 対応にする(挙動不変)

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.cs`(`ComputeCaretPoint` / `GetVisibleCharRange`)
- Modify: `src/kxEdit.Editor/EditorControl.Paint.cs:34`
- Modify: `src/kxEdit.Editor/EditorControl.Input.cs`(`OffsetFromClientPoint`)
- Test: `tests/kxEdit.Editor.Tests/VisualRowScrollTests.cs`(追記)

### Step 1: 失敗するテストを書く

`VisualRowScrollTests` に追記:

```csharp
    // ===== TopSegment を尊重する経路(描画・座標・ヒットテスト・可視域報告)=====

    [Fact]
    public void PointFromCharOffset_ReturnsEmpty_ForRowsAboveTopSegment() =>
        Sta.Run(() =>
        {
            // "abcdefghij"(10 文字)を wrap=2 → 視覚行 5 本。TopSegment=2 なら 0,1 本目は不可視。
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 2);
                Assert.Equal(System.Drawing.Point.Empty, c.PointFromCharOffset(0)); // 視覚行 0
                Assert.Equal(System.Drawing.Point.Empty, c.PointFromCharOffset(2)); // 視覚行 1
                var p = c.PointFromCharOffset(4); // 視覚行 2 = 可視域の最上段
                Assert.NotEqual(System.Drawing.Point.Empty, p);
                Assert.Equal(0, p.Y);
            }
        });

    [Fact]
    public void GetVisibleCharRange_StartsAtTopSegment() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 2);
                var (start, end) = c.GetVisibleCharRange();
                Assert.Equal(4, start); // 視覚行 2 の先頭
                Assert.Equal(10, end); // 視覚行 2..4 で文書末まで
            }
        });

    [Fact]
    public void OffsetFromClientPoint_TopRow_MapsToTopSegment() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 2);
                // クライアント最上段(y=0)の左端 = 視覚行 2 の先頭 = offset 4
                Assert.Equal(4, c.OffsetFromClientPoint(0, 0));
            }
        });
```

`ShowLineNumbers` は既定 false(`_showLineNumbers` のフィールド既定)なので行番号マージンは 0 で、
X=0 が本文先頭になる。テストで明示的に true にしないこと。

### Step 2: 赤を確認する

```powershell
dotnet test tests/kxEdit.Editor.Tests -c Release --filter "FullyQualifiedName~VisualRowScrollTests"
```

期待: 追加 3 件が FAIL(`_topSegment` を誰も読んでいないため、`SetTopPosition(0,2)` しても
座標も可視域も視覚行 0 起点のまま)。

### Step 3: `ComputeCaretPoint` を実装する

`src/kxEdit.Editor/EditorControl.cs` の `ComputeCaretPoint` を次の 3 点で変更する。

1. **(Task 3 の品質レビューで前倒し実施済み)** セグメント選択ループの `LocateSegmentIndex`
   呼び出しへの置き換えは Task 3 の fixup で済ませた。理由: 逐語重複を 1 コミットぶん放置すると、
   `EditorControlWrapCaretTests` 13 件の網が seam 側に一切掛からない
   (対照実験: `ComputeCaretPoint` 側の `<` → `<=` 変異は 13 件赤・seam コピー側の同じ変異は 0 件)。
   本タスクでは可視判定の追加だけを行う。

```csharp
        // I-2: TopLine の途中セグメントから描いている場合、その上のセグメントは不可視。
        if (logicalLine == _topLine && segIdx < _topSegment)
            return (0, 0, false);
```

2. 積み上げループで先頭論理行の読み飛ばしを差し引く。

```csharp
        for (int line = _topLine; line < logicalLine; line++)
        {
            int skip = line == _topLine ? _topSegment : 0;
            long needed = (long)maxUsefulRows - visualRowsBeforeThisLine + skip;
            int rowsNeeded = needed > int.MaxValue ? int.MaxValue : (int)needed;
            var segs = LineLayout
                .WrapFirstSegments(
                    LineTextOf(snap, line),
                    maxWidthPx,
                    _metrics,
                    Math.Max(1, rowsNeeded)
                )
                .Segments;
            // ViewportLayout.Build と同じクランプ(topSegment が実数以上なら最終セグメント)
            int eff = Math.Min(skip, segs.Count - 1);
            visualRowsBeforeThisLine += segs.Count - eff;
            if (visualRowsBeforeThisLine * lineHeight >= paintHeight)
                return (0, 0, false);
        }
        int totalVisualRow =
            visualRowsBeforeThisLine + segIdx - (logicalLine == _topLine ? _topSegment : 0);
```

`Math.Max(1, ...)` を外さないこと(`PaintHeightPx == 0` で `WrapFirstSegments` の
`ThrowIfNegativeOrZero` が発火する生きた防御。既存コメント参照)。

3. `GetVisibleCharRange`(`:390`)と `EditorControl.Paint.cs:34` の `Build` 呼び出しで
   `topSegment: 0` → `_topSegment` に変える。`UpdateHorizontalScrollbar`(`:1072`)は
   折り返し OFF 専用なので **0 のまま**にする(コメントで理由を書く)。

### Step 4: `OffsetFromClientPoint` を実装する

**方針(Task 3 品質レビュー I-2 / I-3 の決定)**: 歩き出しを `_topSegment` にするだけでなく、
**視覚行の前進そのものを seam(`WalkForwardVisualRows`)に載せる**。理由:

- ヘッダコメントが seam の用途に「ヒットテスト」を挙げているのに実態が伴わず、規約が二重定義のまま残る。
- `EditorControl.Input.cs` の `SegmentCountAtLine` は `SegmentCountCapped` と同義だが
  **折り返し OFF ガードが無く**(OFF でも行全文を materialize)**打ち切りも無い**
  (`LineLayout.Wrap` で行全体)。巨大 1 行文書ではクリック 1 回が PR #35 の潰したコスト階級に触れる。
- 「最終視覚行より下のクリックは文書末尾へ」の分岐に必要な情報は、Task 3 fixup で
  `WalkForwardVisualRows` が返すようになった `Exhausted` で表現できる。

したがって `OffsetFromClientPoint` の視覚行前進ループを
`WalkForwardVisualRows(snap, _topLine, _topSegment, visualRowFromTop)` に置き換え、
`Exhausted == true` なら従来どおり `snap.CharLength` を返す。**`SegmentCountAtLine` は削除する**
(他に呼び出し元が無いことを確認してから)。

置き換え前後で `MouseInputTests` / `EditorControlOffsetFromPointTests` が
**1 行も変更せずに全緑**であることが等価性の証拠。

具体的には、現在の `while (rowsToAdvance > 0) { ... }` ループと `segCount` の管理を丸ごと
次に置き換える(`exhausted` の意味は変わらない)。

```csharp
        // (TopLine, TopSegment) の視覚行から visualRowFromTop 個進む。前進規約は seam に一本化する
        // (規約を二重定義しない・折り返し OFF ガードと打ち切りを継承する)。
        // 文書末に達した場合(Exhausted)は文書末尾へクランプする=X による位置決めは行わない。
        var (line, segIdx, exhausted) = WalkForwardVisualRows(
            snap,
            _topLine,
            _topSegment,
            visualRowFromTop
        );

        // 最終視覚行より下 → 文書末尾にクランプ
        if (exhausted)
            return snap.CharLength;
```

**この後の `int useSeg = Math.Min(segIdx, segs.Count - 1);` は残すこと。**
`visualRowFromTop == 0` のとき `WalkForwardVisualRows` は while ループに入らず
`_topSegment` をそのまま返すため、陳腐化した `_topSegment`(編集で段落が縮んだ)を
寄せる役目がここに残っている。

doc コメントの「`Y < 0` は `_topLine` の先頭視覚行にクランプ」を
「`(_topLine, _topSegment)` の視覚行にクランプ」へ更新する。

### Step 5: 緑を確認する

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests   -c Release --no-build
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests    -c Release --no-build
```

期待: 全緑・0 warning。**既存の Editor / App テストは 1 行も変更していない**こと
(`_topSegment` は本番経路ではまだ常に 0 なので挙動不変=設計書 I-3 の証拠その 1)。

### Step 6: コミット

```powershell
git add -A
git commit -m "feat(editor): 座標・ヒットテスト・可視域報告を TopSegment 起点にする(挙動不変・A-6)

ComputeCaretPoint / OffsetFromClientPoint / GetVisibleCharRange / OnPaint が
(TopLine, TopSegment) を起点に視覚行を数えるようにした。スクロール判断はまだ論理行のままなので
_topSegment は本番経路では常に 0=挙動不変(既存テスト無改変で全緑)。

セグメント選択の規約は LocateSegmentIndex に一本化した(ComputeCaretPoint と
LocateVisualRow で二重定義しない)。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: A-6 本体 — スクロール判断を視覚行にする

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.Caret.cs`(`BringCaretIntoView` / `ScrollCharRangeIntoView`)
- Test: `tests/kxEdit.Editor.Tests/VisualRowScrollTests.cs`(追記)

### Step 1: 失敗するテストを書く

`VisualRowScrollTests` に追記:

```csharp
    // ===== A-6: 折り返し ON の追従スクロール =====

    /// <summary>各段落が複数視覚行になる文書。段落数 × 段落あたりの文字数で作る。</summary>
    private static string Paragraphs(int count, int charsPerParagraph) =>
        string.Join(
            "\n",
            Enumerable.Range(0, count).Select(i => new string((char)('a' + (i % 26)), charsPerParagraph))
        );

    [Fact]
    public void KeyDown_Down_WithWrap_KeepsCaretVisible() =>
        Sta.Run(() =>
        {
            // 1 段落 = 5 視覚行(10 文字 / wrap=2)、可視 6 行。
            // 修正前は論理行が可視行数(6)に達するまで TopLine が動かず、
            // 2 段落目の途中でキャレットが可視域外へ出たまま戻らなかった。
            var (f, c) = MakeControl(Paragraphs(8, 10), wrap: 2, visibleRows: 6);
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(0);
                var mi = typeof(EditorControl).GetMethod(
                    "OnKeyDown",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                );
                for (int i = 0; i < 20; i++)
                {
                    mi!.Invoke(c, new object[] { new KeyEventArgs(Keys.Down) });
                    Assert.NotEqual(
                        System.Drawing.Point.Empty,
                        c.PointFromCharOffset(c.CaretCharOffset)
                    );
                }
                Assert.True(c.TopLine > 0 || c.TopSegment > 0, "画面が 1 度も追従していない");
            }
        });

    [Fact]
    public void KeyDown_Down_SingleHugeLogicalLine_ScrollsByVisualRows() =>
        Sta.Run(() =>
        {
            // 論理行 1 本だけの文書。修正前は TopLine が 0 から動かず(maxLine=0)、
            // 先頭 visibleRows 本より下へ到達する手段が無かった。
            var (f, c) = MakeControl(new string('a', 200), wrap: 2, visibleRows: 4);
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(0);
                var mi = typeof(EditorControl).GetMethod(
                    "OnKeyDown",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                );
                for (int i = 0; i < 10; i++)
                    mi!.Invoke(c, new object[] { new KeyEventArgs(Keys.Down) });

                Assert.Equal(0, c.TopLine); // 論理行は 1 本しかない
                Assert.True(c.TopSegment > 0, "TopSegment が進んでいない=視覚行スクロールしていない");
                Assert.NotEqual(
                    System.Drawing.Point.Empty,
                    c.PointFromCharOffset(c.CaretCharOffset)
                );
            }
        });

    [Fact]
    public void KeyDown_Up_WithWrap_ScrollsBackToTop() =>
        Sta.Run(() =>
        {
            // 下端まで降りてから ↑ で戻り、TopSegment が 0 まで戻ること。
            var (f, c) = MakeControl(new string('a', 200), wrap: 2, visibleRows: 4);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 20);
                c.SetCaretCharOffset(40); // 視覚行 20 の先頭
                var mi = typeof(EditorControl).GetMethod(
                    "OnKeyDown",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                );
                for (int i = 0; i < 25; i++)
                    mi!.Invoke(c, new object[] { new KeyEventArgs(Keys.Up) });

                Assert.Equal(0, c.TopSegment);
                Assert.Equal(0, c.CaretCharOffset);
            }
        });

    [Fact]
    public void BringCaretIntoView_WithWrap_NoOp_WhenCaretAlreadyVisible() =>
        Sta.Run(() =>
        {
            // no-change テストは非既定位置から始める(既定 0 と区別する)。
            var (f, c) = MakeControl(new string('a', 200), wrap: 2, visibleRows: 4);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 10);
                c.SetCaretCharOffset(22); // 視覚行 11 = 可視域の 2 本目
                c.SetTopPosition(0, 10); // SetCaretCharOffset 自体の追従で動いた分を戻す
                c.BringCaretIntoView();
                Assert.Equal(10, c.TopSegment);
            }
        });

    [Fact]
    public void EnsureVisibleCharRange_WithWrap_PutsTargetAtBottom() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(new string('a', 200), wrap: 2, visibleRows: 4);
            using (f)
            using (c)
            {
                c.EnsureVisibleCharRange(100, 0); // 視覚行 50
                // 対象を下端に寄せる=起点は 50 - (4 - 1) = 47
                Assert.Equal(0, c.TopLine);
                Assert.Equal(47, c.TopSegment);
            }
        });
```

`c.CaretCharOffset`(`EditorControl.Caret.cs:84`)と `c.PointFromCharOffset` は public、
`c.GetVisibleCharRange()` / `c.OffsetFromClientPoint` / `c.SetTopPosition` / `c.TopSegment` は
internal(`InternalsVisibleTo` で `kxEdit.Editor.Tests` から見える)。

### Step 2: 赤を確認する

```powershell
dotnet test tests/kxEdit.Editor.Tests -c Release --filter "FullyQualifiedName~VisualRowScrollTests"
```

期待: A-6 系 5 件が FAIL(`TopSegment` が 0 のまま / キャレットが `Point.Empty`)。

### Step 3: `BringCaretIntoView` の垂直分岐を実装する

`src/kxEdit.Editor/EditorControl.Caret.cs` の垂直部分を置き換える(水平部分は無変更)。

```csharp
        var snap = _buffer.Current;

        // I-1 対応: paintHeight ベースで可視行数を算出(ComputeCaretPoint の可視性判定と一致)。
        int visibleRows = VisibleRowCount;

        // 垂直: caret の視覚行が [(TopLine,TopSegment), +visibleRows 本) に入るように起点を調整。
        // 折り返し OFF では LocateVisualRow が (論理行, 0) を返し、CountVisualRowsForward は
        // 論理行差になるため、式は導入前(logicalLine < _topLine / >= _topLine + visibleRows)と
        // 同値に退化する(設計書 I-3)。
        var (caretLine, caretSeg) = LocateVisualRow(snap, _caretCtrl.Caret);
        if (caretLine < _topLine || (caretLine == _topLine && caretSeg < _topSegment))
        {
            // 上へはみ出している=キャレットの視覚行を最上段にする
            SetTopPosition(caretLine, caretSeg);
        }
        else if (
            CountVisualRowsForward(snap, _topLine, _topSegment, caretLine, caretSeg, visibleRows)
            >= visibleRows
        )
        {
            // 下へはみ出している=キャレットの視覚行が最下段に来る位置まで遡る
            var (newLine, newSeg) = WalkBackVisualRows(snap, caretLine, caretSeg, visibleRows - 1);
            SetTopPosition(newLine, newSeg);
        }
```

doc コメントの「垂直: キャレットの論理行が …」を視覚行ベースの説明に更新し、
折り返し ON では近似ではなくなった旨を書く(`VisibleRowCount` の remarks にある
「折り返し ON では視覚行数を論理行数と見なす近似」も併せて訂正する)。

### Step 4: `ScrollCharRangeIntoView` を実装する

```csharp
    internal void ScrollCharRangeIntoView(int start, int end, bool alignToTop)
    {
        if (_buffer is null)
            return;
        var snap = _buffer.Current;
        int target = SnapAndClamp(alignToTop ? start : end);
        int line = snap.GetLineIndexOfChar(target);

        int visibleRows = VisibleRowCount;
        bool alreadyVisible;
        (int Line, int Seg)? targetRow = null;
        if (_wrapColumns <= 0)
        {
            // 折り返し OFF: 導入前と同一式(I-3)。Wrap を一切呼ばない。
            alreadyVisible = line >= _topLine && line < _topLine + visibleRows;
        }
        else if (line < _topLine || line >= _topLine + visibleRows)
        {
            // 各論理行は 1 本以上の視覚行を占めるので、可視域が跨ぐ論理行は高々 visibleRows 本。
            // この粗い否定で弾ければ視覚行の計算(=対象行の Wrap)を省ける。
            alreadyVisible = false;
        }
        else
        {
            var row = LocateVisualRow(snap, target);
            targetRow = row;
            alreadyVisible =
                (row.Line > _topLine || (row.Line == _topLine && row.Seg >= _topSegment))
                && CountVisualRowsForward(
                    snap,
                    _topLine,
                    _topSegment,
                    row.Line,
                    row.Seg,
                    visibleRows
                ) < visibleRows;
        }

        // 垂直に動かす必要が無く、水平も動く余地が無いなら完全 no-op で抜ける。
        // (EnsureVisibleCharRange は finally で PositionCaret を呼び、その先の ComputeCaretPoint が
        //  対象論理行を再折り返しするため、無変化呼び出しでも折り返し ON の長行では重い。
        //  SR は歩くたびにここを呼ぶ。)
        if (alreadyVisible && (_wrapColumns > 0 || !_hscroll.Visible))
            return;

        // 既に可視なら垂直は動かさない(視界の揺れ防止)
        if (!alreadyVisible)
        {
            var (tl, ts) = targetRow ?? LocateVisualRow(snap, target);
            if (alignToTop)
                SetTopPosition(tl, ts);
            else
            {
                var (nl, ns) = WalkBackVisualRows(snap, tl, ts, visibleRows - 1);
                SetTopPosition(nl, ns);
            }
        }

        // 水平 + 保険。caret / anchor は EnsureVisibleCharRange が try/finally で復元する
        EnsureVisibleCharRange(target, 0);
    }
```

既存 remarks の「等価性の根拠」の 1 項目目(`logicalLine < _topLine` / `>= _topLine + visibleRows`
の両方とも不発)を視覚行版の表現へ更新する。また折り返し ON の「既に可視」判定に
対象行の Wrap 1 回ぶんのコストが乗ったこと(粗い否定で弾けなかった場合のみ)を追記する。

### Step 5: 緑を確認する

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests   -c Release --no-build
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests    -c Release --no-build
```

期待: 全緑・0 warning。**折り返し OFF の既存テスト
(`CaretScrollTests` / `UiaScrollIntoViewTests` / `UiaVisibleRangeTests` / `MouseInputTests` /
`EditorControlWrapCaretTests`)を 1 行も変更していない**こと=設計書 I-3 の証拠その 2。
もしこれらが赤くなったら、折り返し OFF の退化が壊れている(実装の誤り)。**テストを直さない**。

### Step 6: コミット

```powershell
git add -A
git commit -m "fix(editor): 折り返し ON でキャレットを視覚行で追従スクロールする(A-6)

BringCaretIntoView / ScrollCharRangeIntoView の可視判定を論理行から視覚行へ移した。
折り返し ON では 1 論理行が複数視覚行を占めるため、論理行での判定は成立しておらず、
キャレットが可視域外へ出ても画面が追従しなかった。論理行が 1 本しかない文書
(巨大 1 行)では TopLine が原理的に動かせず、先頭 visibleRows 本より下が
恒久的に到達不能だった。

折り返し OFF では LocateVisualRow が (論理行, 0) を返し CountVisualRowsForward が
論理行差になるため、判定式は導入前と同値に退化する。折り返し OFF の既存テストは
1 行も変更していない。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

### Step 7: ミューテーション検証(2 件)

**コミット後に行う**(共通ルール参照。コミット前に復元すると実装ごと消える)。

1. `caretSeg < _topSegment` を `caretSeg <= _topSegment` にする
   → `BringCaretIntoView_WithWrap_NoOp_WhenCaretAlreadyVisible` か
   `KeyDown_Up_WithWrap_ScrollsBackToTop` が赤くなること。
2. `WalkBackVisualRows(..., visibleRows - 1)` を `visibleRows` にする
   → `EnsureVisibleCharRange_WithWrap_PutsTargetAtBottom` が赤くなること。

確認後 `git checkout -- src/` で復元し(コミット済みなので実装は戻る)、全緑を再確認する。
変異が生存したら網の穴なので、テストを足して**別コミット**を積む。

#### 実施記録(2026-08-23)

上記 2 件に加え、判定 2 分岐と `ScrollCharRangeIntoView` の各要素へ計 11 件の変異を当てた
(Editor 全 412 件で実行=`--filter` で絞らない)。結果:

| 変異 | 結果 |
|------|------|
| 1. `caretSeg < _topSegment` → `<=`(計画 1 件目) | **equivalent mutant=kill 不能** |
| 2. `WalkBackVisualRows(..., visibleRows - 1)` → `visibleRows`(計画 2 件目・`BringCaretIntoView` 側) | kill(11 件・うち折り返し OFF の既存 7 件) |
| 第 1 分岐から辞書順の節を落とす | kill(4 件) |
| `caretLine < _topLine` → `<=` | kill(7 件) |
| `>= visibleRows` → `> visibleRows` | kill(14 件) |
| 2 分岐の記述順を入れ替える | **equivalent mutant=kill 不能** |
| `ScrollCharRangeIntoView` の辞書順の節を落とす | 当初**生存** → 網を追加して kill(2 件) |
| `ScrollCharRangeIntoView` の粗い否定を過剰発火(`line <= _topLine`) | 当初**生存** → 網を追加して kill(2 件) |
| `ScrollCharRangeIntoView` の下端寄せ → `visibleRows`(遡り過ぎ) | **equivalent mutant=kill 不能** |
| `ScrollCharRangeIntoView` の下端寄せ → `visibleRows - 2`(遡り不足) | kill(7 件) |
| `ScrollCharRangeIntoView` の `alignToTop` 反転 | kill(12 件) |

**計画 1 件目が equivalent である理由**: `<` と `<=` が分かれるのは
`caretLine == _topLine && caretSeg == _topSegment` のときだけで、その場合
`SetTopPosition(_topLine, _topSegment)` は無変化ゆえ早期 return する(副作用ゼロ)。
`<` 側も距離 0 < visibleRows で第 2 分岐に入らない。よって両者は観測不能に等価。

**記述順が equivalent である理由**: 2 条件は排他である(第 1 分岐が真のとき
`CountVisualRowsForward` は必ず 0 を返し、`visibleRows >= 1` より第 2 分岐は偽)。
load-bearing なのは**辞書順で弁別する分岐が存在すること**であって記述順ではない
(落とす変異は 4 件が kill する)。`BringCaretIntoView` の remarks をこの実態に合わせて訂正した。

**`ScrollCharRangeIntoView` の下端寄せが遡り過ぎ方向で equivalent な理由**:
直後の `EnsureVisibleCharRange` → `BringCaretIntoView` 第 2 分岐が
`targetRow - (visibleRows - 1)` へ引き戻す。遡り不足の方向は引き戻されないので kill できる。

**当初生存した 2 件はいずれも fixture の狭さ**だった(対象を「起点と同じ論理行かつ
TopSegment より上」に置いたケースが 1 本も無い / 「既に可視」テストが対象を可視域の
最下段に置いていて下端寄せの誤実装と偶然一致する)。総当りオラクル
`ScrollCharRangeIntoView_MatchesRowOracle_ForAllStartsAndTargets` を追加して塞いだ。

#### 性能の増分(2026-08-23・レビュー実測で訂正)

当初の申し送りは「1 打鍵あたり `WrapThroughOffset` が 1 回増える」としていたが、
**これは過小申告だった**。`WrapThroughOffset` / `SegmentCountCapped` へ一時計装を仕込んだ
レビュー実測では **1 打鍵あたり 2 回**である。

| fixture | `BringCaretIntoView` 呼び出し | `LocateVisualRow` の Wrap |
|---|---|---|
| 巨大 1 行 wrap=2 | 40(2/打鍵) | **40(2/打鍵)** |
| 40 段落 wrap=2 | 40 | **40(2/打鍵)** |
| 巨大 1 行 wrap=0 | 20 | **0** |
| 40 段落 wrap=0 | 40 | **0** |

(20 打鍵・下端に張り付いた定常状態)

**正しい増分**:

> 折り返し ON では 1 打鍵あたり `WrapThroughOffset` が **2 回**増える
> (`BringCaretIntoView` が 1 打鍵で 2 回呼ばれるため)。起点が実際に動く打鍵では
> `PositionCaret` 経由でさらに 1 回、**計最大 3 回**。加えて論理行境界を跨ぐ遡りでは
> `WalkBackVisualRows` が前行の完全 Wrap を 1 回払う。
> **折り返し OFF では増分ゼロ**(実測 0 件)。

- **2 回になる理由**: 1 打鍵で `BringCaretIntoView` が 2 回呼ばれる
  (`EditorControl.Caret.cs` の `SetCaretCharOffset` 内と `InputRouter.cs` の `ApplyNavMove` 末尾)。
  **本 PR 以前からの構造で、本 PR は呼び出し元を変えていない**。
- **3 回目**は実アプリ(フォーカスあり)でのみ乗る。テストでは `PositionCaret` が
  `!_hasFocus` で即 return するため計上されない。折り返し ON の巨大 1 行では
  修正前は起点がまったく動かなかった(それが A-6)ので、**この 1 回は本 PR で新たに
  毎打鍵発生するようになった分**である。
- `WalkBackVisualRows` は前の論理行へ入るとき **cap 無しの完全 Wrap** を払う
  (`EditorControl.cs` の該当箇所)。巨大段落の直後の数行(最大 `visibleRows - 1` 打鍵ぶん)は
  1 打鍵ごとにこれを踏む。

**申し送り(本 PR のスコープ外)**: 「1 打鍵で `BringCaretIntoView` が 2 回呼ばれる」構造自体は
本 PR では直さない。Task 7 の性能実測の前提として、また将来の重複解消の起点として記録に残す。

---

## Task 6: ホイールを視覚行送りにする

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.Input.cs`(`OnMouseWheel`)
- Modify: `src/kxEdit.Editor/EditorControl.cs`(`ScrollByVisualRows` を追加)
- Test: `tests/kxEdit.Editor.Tests/VisualRowScrollTests.cs`(追記)

### Step 1: 失敗するテストを書く

```csharp
    [Fact]
    public void MouseWheel_WithWrap_ScrollsByVisualRows() =>
        Sta.Run(() =>
        {
            // 論理行 1 本 = 従来はホイールが完全に効かなかったケース。
            var (f, c) = MakeControl(new string('a', 200), wrap: 2, visibleRows: 4);
            using (f)
            using (c)
            {
                var mi = typeof(EditorControl).GetMethod(
                    "OnMouseWheel",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                );
                mi!.Invoke(c, new object[] { new MouseEventArgs(MouseButtons.None, 0, 0, 0, -120) });
                Assert.True(c.TopSegment > 0, "ホイール下方向で TopSegment が進んでいない");

                int after = c.TopSegment;
                mi!.Invoke(c, new object[] { new MouseEventArgs(MouseButtons.None, 0, 0, 0, 120) });
                Assert.True(c.TopSegment < after, "ホイール上方向で戻っていない");
            }
        });

    [Fact]
    public void MouseWheel_WithoutWrap_StillMovesTopLine() =>
        Sta.Run(() =>
        {
            // 折り返し OFF は従来どおり論理行送り(I-3)。
            var (f, c) = MakeControl(Paragraphs(30, 4), wrap: 0, visibleRows: 4);
            using (f)
            using (c)
            {
                var mi = typeof(EditorControl).GetMethod(
                    "OnMouseWheel",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                );
                mi!.Invoke(c, new object[] { new MouseEventArgs(MouseButtons.None, 0, 0, 0, -120) });
                Assert.True(c.TopLine > 0);
                Assert.Equal(0, c.TopSegment);
            }
        });
```

### Step 2: 赤を確認する

```powershell
dotnet test tests/kxEdit.Editor.Tests -c Release --filter "FullyQualifiedName~MouseWheel"
```

期待: `MouseWheel_WithWrap_ScrollsByVisualRows` が FAIL(TopSegment が 0 のまま)。
`MouseWheel_WithoutWrap_StillMovesTopLine` は PASS(現行挙動の固定=退行検出用)。

### Step 3: 実装する

`src/kxEdit.Editor/EditorControl.cs` に追加:

```csharp
    /// <summary>
    /// 可視域の起点を視覚行単位で相対移動する(ホイール用)。折り返し OFF は
    /// <see cref="TopLine"/> の相対移動に委譲する=導入前と同一(設計書 I-3)。
    /// </summary>
    private void ScrollByVisualRows(int deltaRows)
    {
        if (_buffer is null || deltaRows == 0)
            return;
        if (_wrapColumns <= 0)
        {
            TopLine = _topLine + deltaRows;
            return;
        }
        var snap = _buffer.Current;
        var (line, seg) =
            deltaRows < 0
                ? WalkBackVisualRows(snap, _topLine, _topSegment, -deltaRows)
                : WalkForwardVisualRows(snap, _topLine, _topSegment, deltaRows);
        SetTopPosition(line, seg);
    }
```

`src/kxEdit.Editor/EditorControl.Input.cs` の `OnMouseWheel`:

```csharp
        while (_wheelAccum >= 120)
        {
            ScrollByVisualRows(-wheelLines);
            _wheelAccum -= 120;
        }
        while (_wheelAccum <= -120)
        {
            ScrollByVisualRows(wheelLines);
            _wheelAccum += 120;
        }
```

doc コメントの「Delta>0=上方向スクロール=TopLine 減。TopLine setter がクランプする」を
「折り返し ON では視覚行送り(`ScrollByVisualRows`)・OFF では従来どおり `TopLine`」に更新する。

### Step 4: S1144 の局所抑止を外す

Task 3 で導入した視覚行ヘルパ群は、その時点で呼び出し元が無いため SonarAnalyzer **S1144**
(unused private member)が `-warnaserror` で 4 件のエラーになり、
`#pragma warning disable S1144` で局所抑止してある(Task 3 実施時に判明した計画の穴)。

**本タスクで 4 本すべてに呼び出し元が入る**ので、`src/kxEdit.Editor/EditorControl.cs` の
`#pragma warning disable S1144` / `restore` の対を**削除する**。

- `LocateVisualRow` → Task 5 の `BringCaretIntoView` / `ScrollCharRangeIntoView`
- `CountVisualRowsForward` → Task 5
- `WalkBackVisualRows` → Task 5 + Task 6
- `WalkForwardVisualRows` → Task 6 の `ScrollByVisualRows`

**不要な `#pragma warning disable` は C# では警告が出ない**=消し忘れても誰も気付かない。
削除後に `dotnet build kxEdit.sln -c Release -warnaserror` が 0 warning で通ることを確認する
(通れば「4 本すべてに呼び出し元が入った」ことの機械的な証明にもなる)。

### Step 5: 緑を確認する

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests   -c Release --no-build
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests    -c Release --no-build
```

### Step 6: コミット

```powershell
git add -A
git commit -m "fix(editor): 折り返し ON のホイールを視覚行送りにする(A-6)

論理行 1 本の文書ではホイールが TopLine セッターのクランプに潰されて完全に効かなかった。
折り返し ON では視覚行を歩く。OFF は TopLine の相対移動に委譲=導入前と同一。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: 性能確認・L5 チェックリスト・品質ゲート

**Files:**
- Create: `tests/kxEdit.Editor.Smoke/WrapScrollBench.cs`
- Modify: `tests/kxEdit.Editor.Smoke/Program.cs`(`--wrapscroll` サブコマンド追加)
- Create: `docs/plans/2026-08-22-wrap-vertical-navigation-l5-checklist.md`
- Modify: `docs/plans/2026-08-22-wrap-vertical-navigation.md`(本書に実施記録を追記)

### Step 1: 性能ベンチを追加する

設計書 §5-2 が受容した「巨大段落の途中へスクロールした状態の描画は O(topSegment)/フレーム」を
実測する。`tests/kxEdit.Editor.Smoke/WrapScrollBench.cs`:

```csharp
using System.Diagnostics;
using System.Text;
using kxEdit.Core.Buffers;
using kxEdit.Editor;

namespace kxEdit.Editor.Smoke;

/// <summary>
/// 2026-08-22 A-6(視覚行スクロール)の性能確認。CJK 単一長大行を折り返し ON で載せ、
/// TopSegment を段階的に進めながら 1 フレームの描画時間を測る。
/// 設計書 §5-2 が受容した O(topSegment)/フレームのコストが実用域に収まるかの判断材料。
/// 判定ゲートは持たない(常に EXIT 0)。対になる基準値は PR #35 の 30.1 ms/フレーム。
/// </summary>
internal static class WrapScrollBench
{
    public static int Run()
    {
        ApplicationConfiguration.Initialize();
        using var form = new Form
        {
            Text = "kxEdit.Editor.Smoke --wrapscroll",
            Width = 900,
            Height = 700,
            StartPosition = FormStartPosition.Manual,
            // 画面外ウィンドウには WM_PAINT が来ない=描画を測り落とす(GdiBench と同じ理由)。
            Location = new Point(100, 100),
            ShowInTaskbar = false,
        };
        using var editor = new EditorControl { Dock = DockStyle.Fill, WrapColumns = 80 };
        form.Controls.Add(editor);
        form.Show();
        editor.Focus();
        Application.DoEvents();

        var sb = new StringBuilder(500_000);
        for (int i = 0; i < 500_000; i++)
            sb.Append((char)('あ' + (i % 40)));
        editor.SetSource(TextBuffer.FromString(sb.ToString()));
        Application.DoEvents();

        Console.WriteLine($"ClientSize={editor.ClientSize} / LineHeightPx={editor.LineHeightPx}");
        // ウォームアップ(JIT + 幅メモ化の初回コストを計測から外す)
        editor.Invalidate();
        editor.Update();

        foreach (int seg in new[] { 0, 100, 1000, 5000 })
        {
            editor.SetTopPosition(0, seg);
            Application.DoEvents();
            var sw = Stopwatch.StartNew();
            const int frames = 20;
            for (int i = 0; i < frames; i++)
            {
                editor.Invalidate();
                editor.Update();
            }
            sw.Stop();
            Console.WriteLine(
                $"TopSegment={seg,5}: {sw.Elapsed.TotalMilliseconds / frames:F1} ms/frame"
            );
        }
        return 0;
    }
}
```

`Program.cs` の `--largeline` 分岐の直後に追加:

```csharp
// 2026-08-22 A-6(視覚行スクロール)Task 7: --wrapscroll。巨大段落の途中へスクロールした
// 状態の 1 フレーム時間を測る(設計書 §5-2 が受容した O(topSegment) コストの実測)。
if (args.Length > 0 && args[0] == "--wrapscroll")
{
    return WrapScrollBench.Run();
}
```

実行:

```powershell
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --wrapscroll
```

判断: `TopSegment=5000` が 100 ms/frame を大きく超えるなら、設計書 §5-2 の
「1 エントリメモ(`(snapshot, line, wrap, topSegment) → 行内 char offset`)」を実装する。
超えないなら実装せず、申し送りに残す。**どちらを選んだかを本書の実施記録に必ず書く**。

### Step 2: L5 チェックリストを作る

`docs/plans/2026-08-22-wrap-vertical-navigation-l5-checklist.md` を作成する
(`2026-08-22-backup-savepoint-sync-l5-checklist.md` の書式に合わせる)。項目:

1. 折り返し ON・通常の日本語文書で ↓↑ が 1 視覚行ずつ動き、NVDA が各視覚行を読む
   (行を飛ばさない・↑ が固着しない)。
2. **E-1 の再検証**: CJK 500K・折り返し ON で ↓ 連打。NVDA が「ブランク」と言わないこと。
   → 言わなければ E-1 は A-6 由来と確定=クローズ。言うなら UIA 側の独立欠陥として起票する
   (設計書 §1.3 / 申し送り S-5)。
3. 巨大 1 行(単一論理行)で ↓ を押し続けて文書末尾まで到達できること・ホイールで
   スクロールできること。
4. 折り返し ON で検索ジャンプ・Ctrl+G が追従すること(PR #45 の回帰確認)。
5. 折り返し ON の PageDown / PageUp が視覚行単位で動き、画面が追従すること。
6. 折り返し OFF で ↓↑ / ホイール / 検索ジャンプの挙動が従来どおりであること(I-3 の実機確認)。

### Step 3: 品質ゲート

```powershell
powershell -File tools\pre-merge-check.ps1
```

EXIT 0 を確認する(Format check → Release ビルド 0 警告 → 3 テストプロジェクト全緑)。

### Step 4: 実施記録を追記してコミット

本書の末尾に「## 実施記録」節を作り、次を書く:

- 各タスクのコミットハッシュ
- Task 7 Step 1 のベンチ実測値と、メモを実装したか否かの判断
- 計画から逸脱した点(あれば理由つき)
- 既存テストを変更した箇所(あれば理由つき。**無いことが I-3 の証拠**)

```powershell
git add -A
git commit -m "test(smoke)/docs: 視覚行スクロールの性能ベンチと L5 チェックリスト(A-6)

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## 最終ブランチレビュー(CLAUDE.md §3-5)

タスク完了後、**独立した 2 エージェント**で焦点別 2 パスを実施する(1 起動に混載しない)。

**コード品質パス**の重点:
- 設計書 I-3(折り返し OFF の退化)が本当に成り立っているか。`_wrapColumns <= 0` の分岐を
  1 つずつ潰して、折り返し OFF の既存テストが赤くなるか確かめる
  (**赤くならない分岐は網が無い**=I-3 が実証されていない)。
- 視覚行を歩く 4 本(`CountVisualRowsForward` / `WalkForwardVisualRows` / `WalkBackVisualRows` /
  `SegmentCountCapped`)の境界。特に「行を跨ぐ 1 本」の数え落とし/二重数え。
- ミューテーション検証のスポットチェック(Task 1 / Task 5 で実施したもの + 追加 2〜3 件)。
  変異は**必ず復元**し、復元後の全緑を確認する。

**脆弱性パス**の重点:
- 新しい算術(`seg + n` / `cap - rows` / `topSegment + rowsNeeded`)のオーバーフロー。
  `int.MaxValue` 付近のオフセット・巨大 `visibleRows` を実際に流し込んで確かめる。
- `_topSegment` が実セグメント数を超えた状態(編集で段落が縮む)での全経路
  (描画・座標・ヒットテスト・可視域報告・スクロール)。例外が抜けないこと。
- `SetTopPosition` の `_vscroll.Value` 代入が範囲外にならないこと(バッファ縮小との競合)。

## PR

- タイトル: `fix: 折り返し ON の垂直移動を直す(A-5 / A-6)`
- description(日本語)に含める:
  - 監査書 A-5 / A-6 との対応、設計書へのリンク
  - **A-6 の帰結が監査書の記述より重かった**こと(論理行 1 本の文書で到達不能)
  - E-1 の見立てと L5 での判定方法
  - 折り返し OFF の既存テストが無改変で全緑=I-3 の証拠
  - 受容したトレードオフ(右端で 1 コードポイント内側・スクロールバーは論理行基準のまま)
  - 申し送り S-1〜S-5(設計書 §8)
  - **L5 は未実施**であること(実施したらチェックリストの結果を追記する)

---

## 実施記録(2026-08-23・Task 7)

本節は CLAUDE.md §8 が認める「実装時の精密化・実施記録の追記」である。
Task 1〜6 の各節(策定時スナップショット)は書き換えていない。

### 1. コミット

| Task | commit | 内容 |
|------|--------|------|
| — | `b77d1dd` | 設計書 |
| — | `4cfe577` | 実装計画 |
| 1 | `70e8daf` | fix(core): 非最終視覚行の segEnd に着地しない(A-5) |
| 1 | `b81f232` | docs: ミューテーション検証の順序を訂正し A-5 の赤の実測を反映 |
| 2 | `65b1ba9` | refactor(core): `ViewportLayout.Build` に topSegment を足す(挙動不変) |
| 2 | `74c04cf` | fixup(core): topSegment 負値ガードに網を張る(レビュー Minor 2 件) |
| 3 | `e76de4f` | feat(editor): 視覚行の状態とヘルパ(挙動不変) |
| 3 | `52903d9` | docs: Task 6 に S1144 局所抑止の除去ステップを追加 |
| 3 | `b0fef66` | fixup(editor): 陳腐化クランプと折り返し OFF の追加コストを塞ぐ(レビュー) |
| 3 | `34d9ad7` | docs: Task 4 で `OffsetFromClientPoint` を seam に載せる方針へ変更(品質レビュー) |
| 3 | `78fba6d` | fixup(editor): seam に総当りオラクルの網・規約重複の解消(品質レビュー) |
| 3 | `0f8ffce` | test(editor): 陳腐化 seg の距離計算に網を張る(品質レビュー) |
| 4 | `9b16ed4` | docs: Task 4 Step 4 のコード例を seam 方針に合わせる |
| 4 | `85ecb2e` | feat(editor): 座標・ヒットテスト・可視域報告を TopSegment 起点に(挙動不変) |
| 4 | `07be9b7` | fixup(editor): S1144 抑止理由の陳腐化を直す |
| 4 | `f856135` | fixup(editor): 陳腐化クランプに網・可視行の起点を機構で共有(レビュー Minor) |
| 5 | `76792f4` | fix(editor): 折り返し ON でキャレットを視覚行で追従スクロール(A-6) |
| 5 | `8ea2bfe` | test(editor): 生存変異 3 件を塞ぎ「順序が load-bearing」の記述を実態へ直す |
| 5 | `91ccf2b` | docs: Task 5 のミューテーション検証 実施記録 |
| 5 | `4fa7a0d` | fixup(editor): 性能申し送りの過小申告を訂正・A-6 の到達症状に網(レビュー) |
| 5 | `d662776` | fixup(test): ↓ の道中で起点が最下段に居続けることを固定(レビュー) |
| 6 | `c4c6827` | fix(editor): 折り返し ON のホイールを視覚行送りに(A-6)+ S1144 抑止の除去 |
| 6 | `510cee9` | fixup(test): ホイール 1 ノッチの絶対量に網を張る(レビュー Minor 1) |
| 7 | `b6a5c06` | test(smoke)/docs: 性能ベンチ・L5 チェックリスト・実施記録 |

### 2. 性能ベンチの実測値と §5-2 のメモの判断

```powershell
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --wrapscroll
```

**測定条件**: CJK 500,000 文字の**単一論理行**(改行なし)・折り返し ON(`WrapColumns=80`)・
`ClientSize={884, 661}` / `LineHeightPx=20`(可視 33 視覚行)・
`MouseWheelScrollLines=3`(1 ノッチ = 3 視覚行)・**画面内 Form**・**`editor.Focus()` あり**。
実測で **1 視覚行 = 60 文字**(全角が半角 2 桁ぶんとは限らない)= 文書全体で約 **8,333 視覚行**。
数値は連続 2 回の実行でほぼ一致した(下表は 2 回目)。

**(1) 1 フレーム描画**(設計書 §5-2 が受容した O(topSegment)/フレーム)

| topSegment | ms/frame |
|---:|---:|
| 0 | 33.1 |
| 100 | 31.9 |
| 1000 | 32.7 |
| **5000** | **36.4** |

**(2) ↓ 1 打鍵**(20 打鍵の平均。`topBefore == topAfter` の行は起点が動かない打鍵 = Wrap 2 回、
違う行は起点が動く打鍵 = Wrap 3 回。`nominalSeg` は狙いの深さで、実際の深さは `topBefore`)

| nominalSeg | topBefore | topAfter | ms/key |
|---:|---:|---:|---:|
| 0 | 0 | 0 | 25.4 |
| 100 | 69 | 89 | 27.6 |
| 1000 | 981 | 1001 | 30.9 |
| **5000** | **5035** | **5055** | **45.6** |

**(3) ホイール 1 ノッチ**(= 3 視覚行。10 ノッチの平均。毎回同じ深さへ戻してから 1 ノッチを測る)

| topSegment | 下方向 ms/notch | 上方向 ms/notch |
|---:|---:|---:|
| 0 | 4.2 | 0.0 |
| 100 | 4.2 | 1.9 |
| 1000 | 4.8 | 2.1 |
| **5000** | **8.5** | **2.1** |

**判断: 設計書 §5-2 の 1 エントリメモ(`(snapshot, line, wrap, topSegment) → 行内 char offset`)は
実装しない。申し送りに残す。**

理由:

1. **判定ゲートを大きく下回る**。`TopSegment=5000` の描画は **36.4 ms/frame** で、
   計画の判定基準 100 ms/frame の 3 分の 1 強。PR #35 の基準値 30.1 ms/frame からの
   悪化も約 6 ms に留まる。
2. **O(topSegment) の増分が支配項ではない**。`topSegment` 0 → 5000 の増分は
   描画で **+3.3 ms**(≒ 11 ns/char)。フレームの大半(約 32 ms)は深さに依存しない定数
   (論理行全文の string 化 + 可視 33 行の GDI 描画)で、メモを入れても消えるのは
   3.3 ms のうちの一部でしかない。
3. **同じ結論がホイールでも成り立つ**。下方向 1 ノッチの深さ依存増分は
   0 → 5000 で **+4.3 ms**(4.2 → 8.5 ms)。上方向は **2.1 ms で平坦**
   = `WalkBackVisualRows` の `seg >= n` 即 return が O(1) であることの実証。
   非対称は数値に出ているが、絶対値がホイール 1 ノッチとして体感できる水準ではない。
4. **メモは正しさのリスクを増やす**。キー(snapshot / line / wrapColumns / topSegment)の
   どれか 1 つでも無効化を落とすと、**編集後に誤った位置から描く**という
   「静かに壊れる」種類の欠陥になる。3.3 ms のために v0.2 直前に入れる変更ではない。

**この判断の限界(申し送り)**: 本ベンチが測ったのは 500K 単一論理行・約 8,333 視覚行で、
`topSegment=5000` は文書の 60% 相当である。**より深い位置・より長い行では線形に伸びる**。
体感の閾値(100 ms/frame)に届くのは、上の傾き(3.3 ms / 5000 セグメント)から外挿すると
**約 10 万視覚行**(= 600 万文字の単一段落)の深さになる。到達しうる文書が出てきたら
§5-2 のメモを再検討する。

**支配項は別にある(こちらの方が体感に効く)**: **↓ 1 打鍵が深さ 5000 で 45.6 ms**
(深さ 0 でも 25.4 ms)。この 25 ms の下駄は A-6 由来ではなく、
`VerticalNavigation` がキー 1 打ごとに現在行・移動先行を完全 Wrap している既存コスト
(申し送り S-4)である。深さによる増分 +20 ms は
「`BringCaretIntoView` が 1 打鍵で 2 回呼ばれる」構造(申し送り S-8)が
`WrapThroughOffset` を 2 倍にしているぶんが効いている。
**S-4 と S-8 を潰す方が §5-2 のメモより費用対効果が高い**。

### 3. 計画から逸脱した点

計画は策定時スナップショットなので節そのものは直していない。実装で判明した
**計画側の誤り**を以下に記録する。

**Task 1** — 計画の「赤の予測」が誤りだった。修正前の実測は `[9, 13, 17]` ではなく
**`[9, 17, 21]`**。A-5 の飛ばしは 1 回で済まず、**押すたびに視覚行を 1 本ずつ食い潰す**
(飛び幅が広がっていく)。症状の理解そのものが計画より重かった。
`b81f232` で計画本文の該当箇所に反映済み。

**Task 2** — (a) `tests/kxEdit.Core.Bench/Program.cs` が計画の Files 節から**漏れていた**。
`topSegment` を必須引数にしたため 9 箇所がコンパイル不能になった。
(b) `topSegment` の**負値ガードに網が無かった**(レビュー指摘・`74c04cf` の fixup で追加)。

**Task 3** — (a) **計画のコードのままではビルドが通らない**。呼び出し元を持たない private
ヘルパ 4 本に SonarAnalyzer **S1144** が出て `-warnaserror` でエラーになる。
`#pragma warning disable S1144` の局所抑止を入れ、Task 6 で呼び出し元が入った時点で
除去するステップを計画へ追記した(`52903d9`)。
(b) `WalkForwardVisualRows` が**陳腐化した seg で行き過ぎる**欠陥(`b0fef66` の fixup で修正)。
(c) **折り返し OFF でも `LineTextOf` が論理行全文を string 化していた**
(= I-3「OFF では増分ゼロ」が破れていた。同 fixup で塞いだ)。
(d) 品質レビューの指摘で Task 4 の方針を変更(`OffsetFromClientPoint` も seam に載せる・`34d9ad7`)。

**Task 4** — (a) **計画のテスト期待値が原理的に矛盾していた**。`Point.Empty == new Point(0, 0)`
なので `PointFromCharOffset` では「可視域最上段の行頭」と「不可視」を弁別できない。
`ComputeCaretPoint` の `Visible` フラグで判定するようテストを組み替えた(設計書 S-7 に記録)。
(b) 計画の 3 テストが**すべて単一論理行 fixture** で、**論理行を跨ぐ積み上げに網が無かった**。

**Task 5** — (a) コントローラの指示「判定の**記述順**が load-bearing」は**誤り**だった。
2 条件は排他なので記述順は観測不能(equivalent mutant)。load-bearing なのは
**辞書順で弁別する第 1 分岐が存在すること**である(落とす変異は 4 件が kill する)。
実装の remarks をこの実態へ訂正した(`8ea2bfe`)。
(b) **性能の申し送りが過小申告だった**(1 打鍵 +1 回 → 実測 **+2 回**、実機ではさらに +1 で最大 3 回)。
`4fa7a0d` で訂正。

**Task 6** — (a) **計画のコードはコンパイルできない**。
`deltaRows < 0 ? WalkBackVisualRows(...) : WalkForwardVisualRows(...)` は
両辺が 2 要素タプルと 3 要素タプルで共通型を持たない(**CS0173**)。`if/else` に展開した。
(b) 計画のテストは**上ループの変異を殺せなかった**(最終状態が偶然一致するため)。
`510cee9` で 1 ノッチの絶対量に網を張った。

**横断する教訓** — 計画に書いたテストは**期待値は正しいのに fixture が狭くて狙った境界に
当たらず変異が生存する**という事故が **6 回連続**で起きた。パターンは 3 つに集約できる。

1. **単一論理行だけの fixture**(論理行を跨ぐ積み上げ・数え落とし・二重数えに当たらない)。
2. **実在する視覚行しか起点にできないオラクル**(陳腐化 seg・起点より上の位置に当たらない)。
3. **最終状態しか見ないループ**(道中で 1 本ずれていても、上限や文書末に張り付いて
   最終値が一致してしまう)。

**計画にテストを書く時点で「この fixture でその変異は本当に観測できるか」を
1 件ずつ当てて確かめる**のが唯一の対策だった。期待値の正しさは網の強さを保証しない。

### 4. 既存テストの変更(不変条件 I-3 の証拠)

**設計書 §6 が I-3 の証拠として名指しした 5 本は、1 バイトも変更していない。**

| ファイル | 状態 |
|---|---|
| `tests/kxEdit.Editor.Tests/CaretScrollTests.cs` | **無改変** |
| `tests/kxEdit.Editor.Tests/UiaScrollIntoViewTests.cs` | **無改変** |
| `tests/kxEdit.Editor.Tests/UiaVisibleRangeTests.cs` | **無改変** |
| `tests/kxEdit.Editor.Tests/EditorControlWrapCaretTests.cs` | **無改変** |
| `tests/kxEdit.Editor.Tests/MouseInputTests.cs` | **無改変** |

`git diff --numstat efd2127..HEAD -- tests/kxEdit.Editor.Tests` は
`VisualRowScrollTests.cs`(**新規** 1374 行・削除 0)のみを返す。

**ただし「既存テストの変更ゼロ」ではない**。Core.Tests / Core.Bench 側に
**呼び出し側の機械的な追随**が 25 行ある。

| ファイル | +/− | 内容 |
|---|---|---|
| `tests/kxEdit.Core.Bench/Program.cs` | 53/8 | `ViewportLayout.Build(...)` 8 箇所に `topSegment: 0` を足す(Task 2 で必須引数化) |
| `tests/kxEdit.Core.Tests/Layout/ViewportLayoutTests.cs` | 143/8 | 同上 8 箇所 + topSegment のテスト追加 |
| `tests/kxEdit.Core.Tests/Layout/ViewportLayoutPrefixTests.cs` | 35/7 | 同上 7 箇所 |
| `tests/kxEdit.Core.Tests/Layout/FrameBuilderTests.cs` | 17/2 | 同上 2 箇所 |
| `tests/kxEdit.Core.Tests/Layout/VisualSegmentsTests.cs` | 35/0 | 追加のみ |
| `tests/kxEdit.Core.Tests/Editing/VerticalNavigationTests.cs` | 104/0 | 追加のみ |

**削除された 25 行はすべて `ViewportLayout.Build(...)` の呼び出し 1 行**であり、
**assertion・期待値・fixture を書き換えた箇所は 1 件も無い**
(`git diff efd2127..HEAD -- tests/ | grep '^-' | grep -v '^---'` で機械的に確認できる)。
折り返し OFF の挙動を固定している既存の期待値は、Core / Editor とも一切触れていない。

### 5. Task 6 のミューテーション記録の数値訂正(**訂正の訂正**)

Task 6 実施時に「`ScrollByVisualRows` の折り返し OFF 委譲ブロック
(`TopLine = _topLine + deltaRows`)の符号を反転する変異は **4 件 red**」と報告し、
その後「レビュアーの実測では **3 件 red** が正しい」と訂正した。
**この訂正の方が誤りだった。当初の 4 件が正しい。**

最終ブランチレビュー(コード品質パス)の再検算と、その後の本セッションでの再実測
(`--filter` なし・Editor 全 420 件を Release で 1 回実行)で確定した内訳:

| # | テスト |
|---|--------|
| 1 | `MouseInputTests.MouseWheel_ScrollsDown_WithSystemInformationLines` |
| 2 | `MouseInputTests.MouseWheel_ScrollsUp_WithSystemInformationLines` |
| 3 | `MouseInputTests.MouseWheel_AccumulatesSmallDeltas` |
| 4 | `VisualRowScrollTests.MouseWheel_WithoutWrap_StillMovesTopLine` |

実測は `失敗: 4、合格: 416、合計: 420`。4 本目は Task 6 の実装 commit `c4c6827` で
`VisualRowScrollTests.cs` に追加されており(`git show c4c6827:...` で存在を確認)、
**Task 6 時点でも存在していた**。結論(= 折り返し OFF 経路にも網が掛かっている)は
一貫して変わらない。

**なぜ誤ったか**: 「3 件」は `MouseInputTests` だけを数えた値である。折り返し OFF の
委譲は「既存テストが守っている」という文脈で語られていたため、**同じ変異で赤くなる
新規テスト(`VisualRowScrollTests.MouseWheel_WithoutWrap_StillMovesTopLine`)が
数え落とされた**。網の所在をファイル単位の先入観で切り分けたことが原因で、
本ブランチが繰り返し踏んだ「`--filter` を絞るとミューテーションの結論を誤る」
の同型である。**赤の件数は必ず `--filter` なしの 1 回の実行結果から読む**。

### 6. 品質ゲート

`powershell -File tools\pre-merge-check.ps1` → **EXIT 0**
(Local tool restore → CSharpier check → Release ビルド 0 警告 → Core / Editor / App 全緑)。

### 7. 最終ブランチレビュー 2 パスの反映(2026-08-23)

CLAUDE.md §3-5 に従い、コード品質パスと脆弱性パスを**別エージェント**で独立に実施した。
指摘はすべて元 commit を書き換えず fixup commit で積んだ(§4)。

| commit | パス | 内容 |
|--------|------|------|
| `35e0464` | 脆弱性 (Low 2 件) | 視覚行の歩き / 距離の int オーバーフローを long で塞ぐ |
| `8872a33` | 脆弱性 (Medium) | SR 経路の打ち切り不能を申し送り S-10 に記録し L5 ⑨ を追加 |
| `b35a565` | 品質 (Important 2 件) | 生存変異 2 件に網を張る(下記 ① ②・src 変更なし) |
| `1c9b78e` | 品質 (Minor 4 + 記録 1) | `MaxWrapWidthPx` seam の重複解消・`SegmentCountCapped` の死んだ API 面(`Exact`)除去・`OffsetFromClientPoint` の着地行 Wrap を I-4 化・`CountVisualRowsForward` のループ内打ち切りが値ベースで守れない旨の明記・remarks 2 件の陳腐化/過小申告の訂正 |
| (本 commit) | 品質 (Minor 4 件) | 設計書 S-6 の事実誤り訂正・実施記録 §5 の訂正の訂正・テスト doc の「順序が load-bearing」除去・I-3 の証拠範囲の限定(設計書 S-11) |

**コード品質パスが実証した生存変異 2 件**(どちらも Core / Editor 全緑で生き残っていた):

① `ComputeCaretPoint` の積み上げループの Wrap 予算から `+ skip` を落とす変異。
既存 fixture はどれも「可視行数 ≧ 先頭論理行のセグメント数」で**打ち切りが一度も噛まず**、
過小な予算が観測できなかった。可視 3 行・先頭行 10 視覚行・`TopSegment=5` で噛ませると、
予算不足が直後の `Math.Min(skip, segs.Count - 1)` の誤クランプを誘発し、
**可視域の外にある視覚行を「可視」として返す**(UIA `GetBoundingRectangles` /
`PointFromCharOffset` / システムキャレット位置が誤った行を指す = SR 経路)。
同型の `+ skip` は `CountVisualRowsForward` と `ViewportLayout.Build` には網があり、
**3 兄弟のうちここだけが穴**だった。実装計画が §3 で抽象化した「単一の fixture が
狙った境界に当たらない」パターンそのものが、最終レビューでもう 1 件見つかった形である。

② `SetTopPosition` の `_vscroll.Value` 同期 2 行を削除する変異。
**リポジトリ全体で `_vscroll` を読むテストが 1 件も無かった**。折り返し ON では
`SetTopPosition` が主スクロール経路であり、`UpdateVerticalScrollbar` は編集 / リサイズ時に
しか走らないため、純粋なナビゲーション中はサムが本文に追従しなくなる
(症状=「↓ を押し続けても / ホイールを回してもサムが動かず、次にサムを掴むと画面が飛ぶ」)。

**等価変換と確定したもの**(kill 不能・網を張らないと決めた):
折り返し OFF の 4 短絡(`LocateVisualRow` / `SegmentCountCapped` / `ScrollByVisualRows` /
`ScrollCharRangeIntoView`)、`BringCaretIntoView` の 2 分岐の**記述順**、
`CountVisualRowsForward` のループ内 `if (rows >= cap) return cap;`(値としては等価で、
守っているのは反復数=I-4)。最後のものはコードコメントに「値ベースのテストでは
原理的に守れない」と明記した(嘘の安全宣言を作らない)。

### 8. 残作業

- **L5 実機 SR 検証は未実施**。`docs/plans/2026-08-22-wrap-vertical-navigation-l5-checklist.md`
  の 9 項目(計画の 6 項目 + ⑦ 段落途中の描画 + ⑧ スクロールバーのサム + ⑨ SR の体感)を
  ユーザーが実施する。
  **⑦ は自動テストでは原理的に確認できない**(オフスクリーン Form に WM_PAINT が来ない)。
