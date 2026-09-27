# フェーズ 9: 部分再描画とスクロール(perf-partial-paint) 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 打鍵・IME・選択・現在行強調・セル強調・スクロールで、画面のうち変わった行(とスクロールで露出した帯)だけを描き直す。

**Architecture:** 可視行ごとに「その行の絵を決める値」(`RowPaintKey`)を作り、前回描いた入力と今の入力で行ごとに比べて、違う行の帯だけを無効化する(9a)。描画は `e.ClipRectangle` に交差する行だけを組み立てる。スクロールのセッターでは、行の対応をずらして比べ、`ScrollWindowEx` で既存の画素を移したうえで露出した帯と変わった行だけを無効化する(9b)。画素の移動は `IPaintSurface` の seam を通し、テストでは「画面」ビットマップを動かす偽物に差し替える。

**Tech Stack:** C# / .NET 9 / WinForms / GDI(TextRenderer)/ Win32(`ScrollWindowEx`・`GetUpdateRect`)/ xUnit

**Spec:** `docs/plans/2026-09-24-general-perf-improvements-design.md` §3(全フェーズ共通の規約)・§14(フェーズ 9)。前提として §8.6 と、フェーズ 3 の計画 `docs/plans/2026-09-25-perf-skip-invalidate.md` の実施記録(フェーズ 9 への申し送り)を読むこと。

## Global Constraints

- 挙動不変が原則(CLAUDE.md §2)。意図的な逸脱は本書 §0.2 と PR description に書く。
- 0 warning(`-warnaserror`)。pre-commit フック(CSharpier + ローカルパス検出)を `--no-verify` で飛ばさない。**commit 後の状態で build / test する**(CSharpier が整形で構造を変えることがある)。
- commit は `git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -F <UTF-8 のメッセージファイル>`(Git Bash の ssh-keygen は署名で無言ハングする)。メッセージは `feat|fix|docs|test|refactor|chore(scope): 要約` + 日本語本文 + `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`。
- 描画は UI スレッドだけ。UIA(RPC スレッド)からエディタ内部に触らない(CLAUDE.md §2 の a11y 鉄則)。本フェーズは UIA の経路を変えない。
- ミューテーション検証は**行わない**(設計書 §3.4: フェーズ 9 は描画 = 禁止の対象)。
- 計測は NVDA 起動中で行う(ユーザー決定。NVDA なしでの採り直しは提案しない)。harness の利用はユーザー承認済み(2026-09-27)。
- ログを書くときは UTF-8 を明示する。警告を集計したいビルドだけ `-p:TreatWarningsAsErrors=false`。

## Review Focus

仕様が明示していないが、使う人に最も効きそうな入力・条件(各行に対応するテストを該当タスクに入れてある):

1. **スクロールの途中でキャレット・選択を一時的に動かして戻す経路**(`EnsureVisibleCharRange` の finally)— スクロールで「画面の絵の入力」を更新した後に、無効化なしで状態を戻すと、現在行強調や選択が古いまま残る。→ Task 4 Step 1(`EnsureVisibleCharRange_WithHighlight_LeavesNoStaleRow`)と Task 5 のオラクル。
2. **セル強調枠の下辺が次の行の先頭画素に掛かる**こと — 下の行だけを描き直すと枠が消える / 上の行だけ直すと枠が残る。→ Task 2(`RowsTouching` の表)・Task 3(クリップの画素テスト)・Task 4(帯の +1px)。
3. **可視域の外の編集・行数の変化・行番号の桁の変化・hscroll の出入り** — 行数だけ変わる編集(Enter)で下の行が全部ずれる、999→1000 行で行番号幅が変わる。→ Task 4 の表。
4. **IME 未確定中のスクロールと、未確定表示のはみ出し**(太字のディセンダ)— 未確定表示は行の帯の外を描く可能性がある。→ Task 4(IME の帯は 2 行ぶん)・Task 5(未確定中はスクロールで画素を移さない)・L5。
5. **兄弟・子のコントロールが重なった状態・保留中の無効領域がある状態での ScrollWindowEx**(CSV のセル編集 TextBox など)— 重なった画素を運ぶと他の窓の絵が混ざる。→ Task 5(`CanScroll` の条件)・L5。

---

## 0. 前提と決定事項

### 0.1 計測の条件(設計書 §3.2・§5.5)

- Smoke `--perf` を Release で 3 回ずつ。比べるシナリオは S1・S2(悪化なし)・S3(打鍵)・S4(IME)・S6(スクロール)・S7(全面、悪化なし)。
- harness(`tools/perf-harness.ps1`)は M-2(打鍵・PageDown)と M-4(スクロール)を 3 回ずつ。publish は `dotnet publish` の出力をそのまま使う(手でコピーしない)。
- 変更前後で、同じマシン・同じシナリオ・NVDA 起動中で揃える。Smoke の自己チェックが EXIT 1 なら値を使わない。
- 計測中は別のエージェントに Smoke を回させない(値が揺れる)。

### 0.2 設計書からの精密化(逸脱。PR に書く)

| 設計書 | 本計画 | 理由 |
|---|---|---|
| §14.1 の表(変化の種類ごとに無効化する行を決め、それ以外は全面) | **行ごとの記述子 `RowPaintKey` の比較**(ユーザー決定 2026-09-27)。行の外に効く入力(幅・フォント・テーマ・行番号幅・空白表示・折り返し・背景色)が変われば全面。それ以外は、記述子が違う行の帯だけ | 表の 4 種類を包含し、行数が変わる編集(Enter)や折り返し ON での段落の組み替えも、ずれた行だけが自然に無効化される。編集範囲を AfterEdit に渡す仕組みが要らない |
| §14.1「セル強調枠のために範囲を 1 行上へ広げる」 | 上の行を足すのは、**その行がセル強調と交差するときだけ**(`FrameBuilder.RowsTouching`)。帯の +1px も、セル強調を持つ行だけ | 打鍵ごとに 2 行ぶん描くのを避ける |
| §14.1「クリップ版の op 列が全体の op 列を行で絞ったものと一致する(Core の純関数テスト)」 | **画素の比較**(クリップして描いた絵の、クリップの内側が、全面の絵と一致する。Editor.Tests)と、行の選び方の Core テスト | 行をまたぐ描画(枠の下辺・IME のはみ出し・RenderFrame のシフト)まで含めて確かめるため。op の比較では行への帰属の判定が複雑になる |
| §14.2 ScrollWindowEx の適否「移動量が可視行数未満かつ未処理の無効領域がない」 | 同じ条件に加えて、①行の対応(新しい先頭行が古い可視行の何番目か)を可視行から求める、②縦と横が同時に変わるとき・IME 未確定のとき・兄弟/子のコントロールが重なるときは全面、③移した後も記述子で比べ、違う行も無効化する | 行の対応を記述子で検証するので、編集とスクロールが同じ操作で起きても(AfterEdit の追従)正しい |
| §14.2「途中で切れていた旧最下行も無効化する」 | 露出した帯を**自前で計算して必ず無効化**し、`ScrollWindowEx` の `prcUpdate` も無効化する | 旧最下行の下半分は露出した帯に入る(計算で確認)。`prcUpdate` は他の窓に隠れていた部分も含む |
| フェーズ 3 の申し送り「IME の原点と色を値として `FrameInputs` に取り込む」 | **原点だけ**を `FrameInputs.ImeOrigin` に取り込む。色とフォントはすでに `Style`・フォント 3 つとして `FrameInputs` にある | 無効化する帯を原点から求めるため |
| 設計書 §14.3 の L5「PrintWindow で判定」 | 描かずに撮った絵(`CopyFromScreen`)と、全面を描き直した絵を比べる(フェーズ 3 の L5 と同じ) | PrintWindow は撮影の中で全面を描き直すので、古い絵が残る不具合は写らない |

### 0.3 不変条件(コードのコメントにも書く)

- **不変条件 1**(フェーズ 3 から): 描画が読む状態は `FrameInputs` の中にある。`PaintBody` が static なのでコンパイラが守る。
- **不変条件 2**(フェーズ 3 から): 描画の入力を変える経路は、必ず自分で `Invalidate()` か `InvalidateChangedRows()` を呼ぶ(他の経路の無効化に便乗しない)。
- **不変条件 3**(本フェーズ): `_lastPaintedInputs` は「画面の絵が表す入力」である。画面は、保留中の無効領域を除いて、この入力から描いた絵と画素で一致する。
  - 描画(`PaintAndRecord`)の後: WM_PAINT の更新領域は保留中の無効領域の和で、その中は今の入力で描いた。外側は、`InvalidateChangedRows` が差の全画素を無効化してきたので、今の入力と同じ絵である。
  - スクロール(`ScrollWindowEx`)の後: 画素を移したうえで、露出した帯と記述子の違う行を無効化する。その外側は今の入力の絵と同じなので、`_lastPaintedInputs` を今の入力にする。
  - スクロールは、保留中の無効領域がないときだけ行う(あると、移した画素の一部が古い入力の絵になり、不変条件 3 が崩れる)。

### 0.4 レビュー

- **前倒しのコード品質レビュー**(CLAUDE.md §3 の 4・設計書 §3.4): Task 3(クリップ対応の描画と `FrameRowCache` の seam)と Task 5(`IPaintSurface` の seam)。
- **脆弱性レビュー**: 本フェーズはセキュリティ敏感面(外部入力のパース・パス操作・プロセス起動・WebView・ネットワーク)に触れないので、前倒しは不要。最終レビューの脆弱性パスで、P/Invoke(`ScrollWindowEx` の RECT・戻り値)と UI スレッド外からの呼び出しがないことを見る。
- 最終ブランチレビューは 2 パス(コード品質 / 脆弱性)を別エージェントで。並走させるときは、各エージェントに scratchpad の専用ディレクトリを割り当て、リポジトリでのファイル作成と `dotnet build` / `dotnet test` を禁止する。

### 0.5 L5

設計書 §14.3。目視が重い。Task 9 の手順で、描かずに撮った絵と全面を描き直した絵を比べる。実 IME の操作と NVDA のハイライト矩形は、ユーザーに実機確認を依頼する。

### 0.6 意図的な挙動差

なし(設計書 §3.5 にフェーズ 9 の行はない)。観測できる差は次のとおりで、PR に書く。
- `Control.Invalidated` の `InvalidRect` が、クライアント全体ではなく行の帯になる(App 層に購読者はない。フェーズ 3 で確認済み)。
- 可視域の外だけを変える編集では、`Invalidated` が発火しない。
- スクロールのセッターでは、画素を移したうえで露出した帯だけが描かれる。

---

## ファイル構成

| ファイル | 役割 | 変更 |
|---|---|---|
| `src/kxEdit.Core/Layout/FrameDiff.cs` | `RowPaintKey`(1 行の絵を決める値)・`FrameDiff.Describe` / `DirtyBands`(純関数) | 新規 |
| `src/kxEdit.Core/Layout/FrameBuilder.cs` | `RowsTouching`(クリップに交差する行)を追加。`TryComputeRowIntersection` を internal にして `FrameDiff` と共有 | 変更 |
| `src/kxEdit.Editor/FrameInputs.cs` | `ImeOrigin` を追加。`SameLayoutAs`(行の外に効く入力の比較)を切り出す | 変更 |
| `src/kxEdit.Editor/FrameRowCache.cs` | 描画の入力 → 可視行と記述子の、直近 2 件のキャッシュ | 新規 |
| `src/kxEdit.Editor/PaintSurface.cs` | `IPaintSurface`(画素の移動の seam)と `Win32PaintSurface` | 新規(Task 5) |
| `src/kxEdit.Editor/EditorControl.Paint.cs` | クリップ対応の `PaintBody` / `PaintAndRecord`、`InvalidateChangedRows` | 変更 |
| `src/kxEdit.Editor/EditorControl.cs` | フィールド・セッター・AfterEdit・セル強調の無効化の置き換え | 変更 |
| `src/kxEdit.Editor/EditorControl.Caret.cs` | 4 経路と `EnsureVisibleCharRange` | 変更 |
| `src/kxEdit.Editor/EditorControl.Ime.cs` / `ImeController.cs` / `IImeOverlayHost.cs` | `ComputeOrigin` / `Draw(g, origin)`、host の Invalidate | 変更 |
| `src/kxEdit.Editor/NativeMethods.cs` | `ScrollWindowEx` / `GetUpdateRect` | 変更(Task 5) |
| `tests/kxEdit.Core.Tests/Layout/FrameDiffTests.cs` / `RowsTouchingTests.cs` | 純関数のテスト | 新規 |
| `tests/kxEdit.Editor.Tests/ClipPaintTests.cs` | クリップして描いた絵の画素テスト | 新規 |
| `tests/kxEdit.Editor.Tests/EditorControlPartialInvalidateTests.cs` | `InvalidRect` の表 | 新規 |
| `tests/kxEdit.Editor.Tests/Fakes/ScreenSurface.cs` | オラクル用の偽の画面 | 新規(Task 5) |
| `tests/kxEdit.Editor.Tests/SkipInvalidateOracleTests.cs` | 矩形の合成・スクロール・陽性対照 | 変更 |
| `tests/kxEdit.Editor.Tests/FrameInputsTests.cs` | `ImeOrigin` と `SameLayoutAs` | 変更 |
| `tests/kxEdit.Editor.Smoke/PaintTransition.cs` / `tools/README.md` | `Expect.Partial` / `Expect.StaleScroll` と遷移の追加 | 変更(Task 6) |

---

## Task 1: 変更前の計測(src は未変更)

**Files:** なし(結果は本計画の「実施記録」に追記する。`d` は scratchpad の `perf-partial-paint` ディレクトリ)

- [ ] **Step 1: Release ビルドと Smoke の基準**

```powershell
$d = "<scratchpad>\perf-partial-paint"; New-Item -ItemType Directory -Force $d | Out-Null
dotnet build kxEdit.sln -c Release 2>&1 | Out-File -Encoding utf8 "$d\before-build.log"
1..3 | ForEach-Object { dotnet run --project tests/kxEdit.Editor.Smoke -c Release --no-build -- --perf --scenario S1,S2,S3,S4,S6,S7 --json "$d\before-perf-$_.json" }
```

Expected: 3 回とも EXIT 0。各シナリオの ja10k / en10k の中央値の min / 中央 / max を表にする。`paints_per_op` も記録する(S1・S2 は 0、他は 1)。

- [ ] **Step 2: `--paint-transition` の基準**

```powershell
dotnet run --project tests/kxEdit.Editor.Smoke -c Release --no-build -- --paint-transition --expect-skip
```

Expected: EXIT 0(全遷移が一致)。

- [ ] **Step 3: harness(M-2・M-4)**

```powershell
dotnet publish src/kxEdit.App -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:DebugType=embedded -o "$d\before\publish" 2>&1 | Out-File -Encoding utf8 "$d\before-publish.log"
1..3 | ForEach-Object { pwsh -File tools\perf-harness.ps1 -PublishDir "$d\before\publish" -Scenario M-2 -OutCsv "$d\before-m2-$_.csv" }
1..3 | ForEach-Object { pwsh -File tools\perf-harness.ps1 -PublishDir "$d\before\publish" -Scenario M-4 -OutCsv "$d\before-m4-$_.csv" }
```

Expected: 3 回とも `status=completed`。`env` 行で NVDA 起動中であることを確かめる。harness が中止条件(kxEdit 起動中・`backups` にファイルがある・前回の退避が残る)で止まったら、ユーザーに状況を伝えて判断を仰ぐ。

- [ ] **Step 4: 実施記録に追記して commit**

本計画の末尾「実施記録 / Task 1」に、環境(解像度・DPI・NVDA の pid)・3 表(Smoke・M-2・M-4)を書く。

```powershell
git add docs/plans/2026-09-27-perf-partial-paint.md
git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -F <msg>   # docs(perf): フェーズ 9 の計画と変更前の計測
```

---

## Task 2: 行の記述子と、クリップに交差する行(Core の純関数)

**Files:**
- Create: `src/kxEdit.Core/Layout/FrameDiff.cs`
- Modify: `src/kxEdit.Core/Layout/FrameBuilder.cs`(`TryComputeRowIntersection` を internal に・`RowsTouching` を追加)
- Test: `tests/kxEdit.Core.Tests/Layout/FrameDiffTests.cs`、`tests/kxEdit.Core.Tests/Layout/RowsTouchingTests.cs`

**Interfaces:**
- Produces:
  - `internal readonly record struct RowPaintKey(int LogicalLine, int SegmentIndex, string Text, bool IsCurrentLine, int SelectionStart, int SelectionEnd, int CellStart, int CellEnd)`(行内オフセット。交差なしは -1 / -1)
  - `internal static RowPaintKey[] FrameDiff.Describe(TextSnapshot snapshot, IReadOnlyList<VisualRow> rows, int currentLineLogical, SelectionRange? selection, SelectionRange? cellHighlight)`
  - `internal static List<(int Top, int Bottom)> FrameDiff.DirtyBands(RowPaintKey[] old, RowPaintKey[] now, int shift, int lineHeight, int heightPx)`
  - `internal static IReadOnlyList<VisualRow> FrameBuilder.RowsTouching(IReadOnlyList<VisualRow> rows, int clipTop, int clipBottom, int lineHeight, SelectionRange? cellHighlight)`
  - `internal static bool FrameBuilder.TryComputeRowIntersection(VisualRow row, SelectionRange range, out int startInRow, out int endInRow)`(private → internal。中身は変えない)

- [ ] **Step 1: 失敗するテストを書く(`RowsTouchingTests.cs`)**

```csharp
using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Layout;

/// <summary>
/// フェーズ 9(設計書 §14.1・計画 §0.2): クリップの縦の範囲に交差する行だけを描く。
/// 行 i は [i·LH, (i+1)·LH) を占める。例外はセル強調枠の下辺で、行 i の枠は (i+1)·LH の 1 画素に引かれる
/// (FrameBuilder の工程 8)。そのため、クリップの中に下辺が来る行は、セル強調と交差するときだけ足す。
/// </summary>
public class RowsTouchingTests
{
    private const int Lh = 10;

    // 5 行・各行 4 文字(行 i の先頭 = i * 6。CRLF を挟む想定の絶対オフセット)。
    private static IReadOnlyList<VisualRow> Rows() =>
        [.. Enumerable.Range(0, 5).Select(i => new VisualRow(i, 0, i * 6, 4, i * Lh))];

    private static int[] Lines(IReadOnlyList<VisualRow> rows) => [.. rows.Select(r => r.LogicalLine)];

    [Theory]
    [InlineData(0, 50, new[] { 0, 1, 2, 3, 4 })] // 全体
    [InlineData(20, 30, new[] { 2 })] // ちょうど行 2 の帯(上の行 1 はセル強調なし = 足さない)
    [InlineData(25, 26, new[] { 2 })] // 行の途中の 1 画素
    [InlineData(19, 21, new[] { 1, 2 })] // 行 1 の最下画素と行 2 の先頭画素
    [InlineData(30, 30, new int[0])] // 空のクリップ
    [InlineData(60, 70, new int[0])] // 可視行の外
    public void Without_cell_highlight_rows_are_the_bands_that_intersect(
        int top,
        int bottom,
        int[] expected
    ) => Assert.Equal(expected, Lines(FrameBuilder.RowsTouching(Rows(), top, bottom, Lh, null)));

    [Fact]
    public void The_row_above_is_added_when_its_cell_highlight_edge_falls_in_the_clip()
    {
        var cell = new SelectionRange(1 * 6 + 1, 1 * 6 + 3); // 行 1 の中
        // クリップが行 2 の先頭画素(= 行 1 の枠の下辺)から始まる。
        Assert.Equal([1, 2], Lines(FrameBuilder.RowsTouching(Rows(), 20, 30, Lh, cell)));
    }

    [Fact]
    public void The_row_above_is_not_added_when_the_clip_starts_below_its_edge()
    {
        var cell = new SelectionRange(1 * 6 + 1, 1 * 6 + 3);
        Assert.Equal([2], Lines(FrameBuilder.RowsTouching(Rows(), 21, 30, Lh, cell)));
    }

    [Fact]
    public void An_empty_cell_highlight_adds_nothing()
    {
        var cell = new SelectionRange(1 * 6 + 1, 1 * 6 + 1);
        Assert.Equal([2], Lines(FrameBuilder.RowsTouching(Rows(), 20, 30, Lh, cell)));
    }

    [Fact]
    public void A_clip_covering_every_row_returns_the_input_list()
    {
        var rows = Rows();
        Assert.Same(rows, FrameBuilder.RowsTouching(rows, 0, 51, Lh, null));
    }
}
```

- [ ] **Step 2: 失敗するテストを書く(`FrameDiffTests.cs`)**

```csharp
using kxEdit.Core.Buffers;
using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Layout;

/// <summary>
/// フェーズ 9(計画 §0.2): 行ごとの記述子と、描き直しが要る縦の帯。
/// </summary>
public class FrameDiffTests
{
    private const int Lh = 10;
    private static MonoCharMetrics M => new(halfWidthPx: 1, lineHeightPx: Lh);

    private static TextSnapshot Snap(params string[] lines) =>
        TextBuffer.FromString(string.Join("\r\n", lines)).Current;

    private static IReadOnlyList<VisualRow> Rows(TextSnapshot s, int top = 0, int wrap = 0) =>
        ViewportLayout.Build(s, top, 0, 1000, wrap, M);

    private static RowPaintKey[] Keys(
        TextSnapshot s,
        int currentLine = -1,
        SelectionRange? sel = null,
        SelectionRange? cell = null,
        int top = 0
    ) => FrameDiff.Describe(s, Rows(s, top), currentLine, sel, cell);

    [Fact]
    public void Describe_takes_text_current_line_and_in_row_ranges()
    {
        var s = Snap("abcd", "efgh", "ijkl");
        // 選択は行 0 の 2 文字目から行 1 の 2 文字目まで。セル強調は行 2 の 1〜3。
        var keys = Keys(s, currentLine: 1, sel: new SelectionRange(2, 8), cell: new SelectionRange(13, 15));
        Assert.Equal(new RowPaintKey(0, 0, "abcd", false, 2, 4, -1, -1), keys[0]);
        Assert.Equal(new RowPaintKey(1, 0, "efgh", true, 0, 2, -1, -1), keys[1]);
        Assert.Equal(new RowPaintKey(2, 0, "ijkl", false, -1, -1, 1, 3), keys[2]);
    }

    [Fact]
    public void Describe_treats_an_empty_range_as_no_range()
    {
        var s = Snap("abcd");
        var keys = Keys(s, sel: new SelectionRange(2, 2), cell: new SelectionRange(1, 1));
        Assert.Equal(new RowPaintKey(0, 0, "abcd", false, -1, -1, -1, -1), keys[0]);
    }

    [Fact]
    public void Identical_keys_have_no_band()
    {
        var s = Snap("a", "b", "c");
        Assert.Empty(FrameDiff.DirtyBands(Keys(s), Keys(s), 0, Lh, 1000));
    }

    [Fact]
    public void A_changed_row_is_its_band()
    {
        var old = Keys(Snap("a", "b", "c"));
        var now = Keys(Snap("a", "bx", "c"));
        Assert.Equal([(10, 20)], FrameDiff.DirtyBands(old, now, 0, Lh, 1000));
    }

    [Fact]
    public void Adjacent_changed_rows_are_merged_and_separate_ones_are_not()
    {
        var old = Keys(Snap("a", "b", "c", "d", "e"));
        var now = Keys(Snap("A", "B", "c", "D", "e"));
        Assert.Equal([(0, 20), (30, 40)], FrameDiff.DirtyBands(old, now, 0, Lh, 1000));
    }

    [Fact]
    public void A_row_with_a_cell_highlight_extends_one_pixel_down()
    {
        var s = Snap("abcd", "efgh", "ijkl");
        var old = Keys(s);
        var now = Keys(s, cell: new SelectionRange(7, 8)); // 行 1
        Assert.Equal([(10, 21)], FrameDiff.DirtyBands(old, now, 0, Lh, 1000));
    }

    [Fact]
    public void Rows_that_exist_on_one_side_only_are_dirty()
    {
        var old = Keys(Snap("a", "b", "c", "d"));
        var now = Keys(Snap("a", "b"));
        Assert.Equal([(20, 40)], FrameDiff.DirtyBands(old, now, 0, Lh, 1000));
    }

    [Fact]
    public void Bands_are_clipped_to_the_height()
    {
        var old = Keys(Snap("a", "b", "c"));
        var now = Keys(Snap("a", "b", "C"));
        Assert.Equal([(20, 25)], FrameDiff.DirtyBands(old, now, 0, Lh, 25));
    }

    [Fact]
    public void Shift_compares_new_row_i_with_old_row_i_plus_shift()
    {
        var s = Snap("0", "1", "2", "3", "4", "5");
        var old = Keys(s, top: 0); // 行 0..5
        var now = Keys(s, top: 2); // 行 2..5(2 行ぶん上へ動いた)
        // 新しい 0..3 は古い 2..5 と同じ。新しい 4・5 は古い側に対応がない(露出)= 汚れ。
        Assert.Equal([(40, 60)], FrameDiff.DirtyBands(old, now, 2, Lh, 60));
    }

    [Fact]
    public void Negative_shift_makes_the_top_rows_dirty()
    {
        var s = Snap("0", "1", "2", "3", "4", "5");
        var old = Keys(s, top: 2);
        var now = Keys(s, top: 0); // 2 行ぶん下へ動いた
        Assert.Equal([(0, 20)], FrameDiff.DirtyBands(old, now, -2, Lh, 60));
    }

    [Fact]
    public void Shifting_a_cell_highlight_edge_into_the_top_pixel_dirties_it()
    {
        var s = Snap("abcd", "efgh", "ijkl", "mnop");
        // 古い行 0 にセル強調。1 行上へ動かすと、その枠の下辺(y=10)が新しい y=0 に来る。
        var old = Keys(s, cell: new SelectionRange(1, 3), top: 0);
        var now = Keys(s, cell: new SelectionRange(1, 3), top: 1);
        var bands = FrameDiff.DirtyBands(old, now, 1, Lh, 40);
        Assert.Equal((0, 1), bands[0]);
    }

    [Fact]
    public void The_top_pixel_is_clean_when_the_row_above_has_no_cell_highlight()
    {
        var s = Snap("abcd", "efgh", "ijkl", "mnop");
        var bands = FrameDiff.DirtyBands(Keys(s, top: 0), Keys(s, top: 1), 1, Lh, 40);
        Assert.DoesNotContain(bands, b => b.Top == 0);
    }
}
```

- [ ] **Step 3: テストが失敗することを確かめる**

Run: `dotnet build tests/kxEdit.Core.Tests -c Release -p:TreatWarningsAsErrors=false`
Expected: `RowsTouching` / `FrameDiff` / `RowPaintKey` が未定義でビルドエラー。

- [ ] **Step 4: 実装(`FrameBuilder.cs`)**

`TryComputeRowIntersection` の `private` を `internal` にする(doc に「`FrameDiff.Describe` と共有する」を 1 行足す)。`Build` の直前に次を足す。

```csharp
    /// <summary>
    /// クリップの縦の範囲 [<paramref name="clipTop"/>, <paramref name="clipBottom"/>) を描くのに要る行
    /// (フェーズ 9・設計書 §14.1)。行 r は [r.YPx, r.YPx + lineHeight) を塗るので、それが交差する行を返す。
    /// 例外はセル強調枠の下辺(工程 8)で、行 r の枠は r.YPx + lineHeight の 1 画素(= 次の行の先頭画素)に引かれる。
    /// その画素がクリップに入り、行 r がセル強調と交差するときだけ、行 r も足す。
    /// </summary>
    /// <remarks>
    /// すべての行が要るときは <paramref name="rows"/> をそのまま返す(全面の描画で配列を作り直さない)。
    /// 行の Y は <see cref="ViewportLayout.Build"/> が 0 から lineHeight ずつ積んだもの(昇順)である前提。
    /// </remarks>
    internal static IReadOnlyList<VisualRow> RowsTouching(
        IReadOnlyList<VisualRow> rows,
        int clipTop,
        int clipBottom,
        int lineHeight,
        SelectionRange? cellHighlight
    )
    {
        if (rows.Count == 0 || clipBottom <= clipTop)
            return [];
        if (clipTop <= 0 && clipBottom > rows[^1].YPx + lineHeight)
            return rows;
        var result = new List<VisualRow>();
        foreach (var row in rows)
        {
            bool body = row.YPx < clipBottom && row.YPx + lineHeight > clipTop;
            int edgeY = row.YPx + lineHeight;
            bool edge =
                !body
                && edgeY >= clipTop
                && edgeY < clipBottom
                && cellHighlight is SelectionRange hl
                && hl.Start < hl.End
                && TryComputeRowIntersection(row, hl, out _, out _);
            if (body || edge)
                result.Add(row);
        }
        return result;
    }
```

- [ ] **Step 5: 実装(`FrameDiff.cs`)**

```csharp
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Layout;

/// <summary>
/// 1 本の視覚行の絵を決める値(フェーズ 9・計画 §0.2)。行の外に効く入力(幅・フォント・テーマ・行番号幅・
/// 空白表示・折り返し・背景色・水平スクロール)は含めない。それらが同じ 2 つの描画で記述子が等しい行は、
/// 同じ絵になる(<see cref="FrameBuilder.Build"/> の各工程が行ごとに読む値の全部)。
/// </summary>
/// <param name="LogicalLine">行番号の文字列と、現在行の判定に使う。</param>
/// <param name="SegmentIndex">行番号を描くか(0 のときだけ)。</param>
/// <param name="Text">その視覚行の本文(改行なし)。</param>
/// <param name="IsCurrentLine">現在行の強調(工程 2)と、行番号の色(工程 7)。</param>
/// <param name="SelectionStart">選択の行内オフセット(工程 3・5)。交差なしは -1。</param>
/// <param name="SelectionEnd">同上。</param>
/// <param name="CellStart">セル強調の行内オフセット(工程 4・8)。交差なしは -1。</param>
/// <param name="CellEnd">同上。</param>
internal readonly record struct RowPaintKey(
    int LogicalLine,
    int SegmentIndex,
    string Text,
    bool IsCurrentLine,
    int SelectionStart,
    int SelectionEnd,
    int CellStart,
    int CellEnd
)
{
    public bool HasCell => CellStart >= 0;
}

/// <summary>
/// 前回描いた絵と今の絵で、描き直しが要る縦の帯を求める(フェーズ 9・計画 §0.2)。
/// </summary>
internal static class FrameDiff
{
    /// <summary>可視行ごとの記述子。<paramref name="rows"/> は <see cref="ViewportLayout.Build"/> の結果。</summary>
    public static RowPaintKey[] Describe(
        TextSnapshot snapshot,
        IReadOnlyList<VisualRow> rows,
        int currentLineLogical,
        SelectionRange? selection,
        SelectionRange? cellHighlight
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rows);
        var keys = new RowPaintKey[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            string text =
                row.SegmentLength == 0
                    ? string.Empty
                    : snapshot.GetText(row.SegmentStartChar, row.SegmentLength);
            var (selS, selE) = InRow(row, selection);
            var (cellS, cellE) = InRow(row, cellHighlight);
            keys[i] = new RowPaintKey(
                row.LogicalLine,
                row.SegmentIndex,
                text,
                currentLineLogical >= 0 && row.LogicalLine == currentLineLogical,
                selS,
                selE,
                cellS,
                cellE
            );
        }
        return keys;
    }

    /// <summary>
    /// 描き直しが要る縦の帯 [Top, Bottom)(px・昇順・重なりなし・[0, <paramref name="heightPx"/>) で切る)。
    /// 新しい行 i の位置には、古い行 i + <paramref name="shift"/> の画素がある
    /// (<paramref name="shift"/> &gt; 0 = 内容が上へ動いた。スクロールしていなければ 0)。
    /// </summary>
    /// <remarks>
    /// <para>行 i の帯は [i·LH, (i+1)·LH)。新旧のどちらかでセル強調を持つ行は、枠の下辺のために 1 画素下へ延ばす。</para>
    /// <para>
    /// 新しい先頭行の先頭画素(y = 0)には、古い行 shift − 1 の枠の下辺が運ばれてくることがある。
    /// その行がセル強調を持つときだけ [0, 1) を汚れにする(i = −1 の番)。
    /// </para>
    /// <para>両側とも行がない位置は、どちらも背景なので汚れにしない。</para>
    /// </remarks>
    public static List<(int Top, int Bottom)> DirtyBands(
        RowPaintKey[] old,
        RowPaintKey[] now,
        int shift,
        int lineHeight,
        int heightPx
    )
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(now);
        var bands = new List<(int Top, int Bottom)>();
        if (At(old, shift - 1) is { HasCell: true })
            Add(bands, 0, 1, heightPx);

        int end = Math.Max(now.Length, old.Length - shift);
        int runStart = -1;
        bool runHasCell = false;
        for (int i = 0; i <= end; i++)
        {
            RowPaintKey? a = i < end ? At(old, i + shift) : null;
            RowPaintKey? b = i < end ? At(now, i) : null;
            bool dirty = i < end && !Nullable.Equals(a, b);
            if (dirty)
            {
                if (runStart < 0)
                    runStart = i;
                runHasCell = (a?.HasCell ?? false) || (b?.HasCell ?? false);
                continue;
            }
            if (runStart >= 0)
            {
                Add(bands, runStart * lineHeight, i * lineHeight + (runHasCell ? 1 : 0), heightPx);
                runStart = -1;
                runHasCell = false;
            }
        }
        return bands;
    }

    private static RowPaintKey? At(RowPaintKey[] keys, int i) =>
        i >= 0 && i < keys.Length ? keys[i] : null;

    private static void Add(List<(int Top, int Bottom)> bands, int top, int bottom, int heightPx)
    {
        top = Math.Max(0, top);
        bottom = Math.Min(heightPx, bottom);
        if (top >= bottom)
            return;
        // 直前の帯と重なる・接するなら 1 つにする(i = −1 の [0,1) と行 0 の帯など)。
        if (bands.Count > 0 && bands[^1].Bottom >= top)
            bands[^1] = (bands[^1].Top, Math.Max(bands[^1].Bottom, bottom));
        else
            bands.Add((top, bottom));
    }

    private static (int Start, int End) InRow(VisualRow row, SelectionRange? range) =>
        range is SelectionRange r
        && r.Start < r.End
        && FrameBuilder.TryComputeRowIntersection(row, r, out int s, out int e)
            ? (s, e)
            : (-1, -1);
}
```

注意: `runHasCell` は「帯の最後の行」がセル強調を持つかで決める(下辺が要るのは最後の行の下だけ)。上のコードは dirty の行ごとに上書きするので、run を閉じた時点で最後の行の値になっている。

- [ ] **Step 6: テストが通ることを確かめる**

Run: `dotnet build kxEdit.sln -c Release; dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~FrameDiffTests|FullyQualifiedName~RowsTouchingTests|FullyQualifiedName~FrameBuilder"`
Expected: すべて PASS(既存の FrameBuilder のテストも)。

- [ ] **Step 7: commit(commit 後に Core.Tests 全体を通す)**

```powershell
git add src/kxEdit.Core/Layout/FrameDiff.cs src/kxEdit.Core/Layout/FrameBuilder.cs tests/kxEdit.Core.Tests/Layout/FrameDiffTests.cs tests/kxEdit.Core.Tests/Layout/RowsTouchingTests.cs
git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -F <msg>   # feat(layout): 行の記述子と、クリップに交差する行
dotnet build kxEdit.sln -c Release; dotnet test tests/kxEdit.Core.Tests -c Release --no-build
```

---

## Task 3: クリップ対応の描画(挙動不変・無効化はまだ全面のまま)

**Files:**
- Create: `src/kxEdit.Editor/FrameRowCache.cs`
- Modify: `src/kxEdit.Editor/FrameInputs.cs`、`src/kxEdit.Editor/EditorControl.Paint.cs`、`src/kxEdit.Editor/EditorControl.cs`(`BuildVisibleRows` を internal に・`_rowCache` フィールド)、`src/kxEdit.Editor/ImeController.cs`、`src/kxEdit.Editor/IImeOverlayHost.cs`(コメント)
- Test: `tests/kxEdit.Editor.Tests/ClipPaintTests.cs`(新規)、`tests/kxEdit.Editor.Tests/FrameInputsTests.cs`

**Interfaces:**
- Consumes: Task 2 の `FrameBuilder.RowsTouching`、`FrameDiff.Describe`、`RowPaintKey`
- Produces:
  - `FrameInputs.ImeOrigin`(`Point?`。未確定表示の原点 = `(ComputeCaretPoint(ime.Start).X − ScrollX, Y)`。表示しないときは null)
  - `bool FrameInputs.SameLayoutAs(FrameInputs other)`(行の中身とスクロール位置以外の比較)
  - `Point? ImeController.ComputeOrigin()`、`void ImeController.Draw(Graphics g, Point origin)`(`Draw(Graphics g)` は残す)
  - `sealed class FrameRowCache { IReadOnlyList<VisualRow> Rows(FrameInputs); RowPaintKey[] Keys(FrameInputs); void Clear(); }`
  - `EditorControl.TestHook_PaintToBitmap(EditorControl c, bool record, Rectangle? clip = null)`
  - `private void PaintAndRecord(Graphics g, Rectangle clip)`

- [ ] **Step 1: 失敗するテストを書く(`FrameInputsTests.cs`)**

`ExpectedMembers` に `"ImeOrigin"` を足す。`Base` に `ImeOrigin = new Point(10, 20),` を足す。`Different` に `Point p => new Point(p.X + 1, p.Y),` を足す(`Size` の行の次)。次の 2 つを足す。

```csharp
    /// <summary>
    /// 行の中身とスクロール位置(差分の無効化とスクロールが扱う)。これ以外のメンバーが変わったら全面を描き直す。
    /// </summary>
    private static readonly string[] RowContentOrScroll =
    [
        "Snapshot",
        "TopLine",
        "TopSegment",
        "ScrollX",
        "CurrentLineLogical",
        "Selection",
        "CellHighlight",
        "Ime",
        "ImeOrigin",
    ];

    [Theory]
    [MemberData(nameof(Members))]
    public void SameLayoutAs_ignores_exactly_row_content_and_scroll(string member) =>
        Sta.Run(() =>
        {
            var a = Base(new GdiCharMetrics(s_font));
            var prop = typeof(FrameInputs).GetProperty(member)!;
            var b = a with { };
            var different = Different(prop.GetValue(a));
            try
            {
                prop.SetValue(b, different);
                Assert.Equal(RowContentOrScroll.Contains(member), b.SameLayoutAs(a));
            }
            finally
            {
                (different as IDisposable)?.Dispose();
            }
        });
```

- [ ] **Step 2: 失敗するテストを書く(`ClipPaintTests.cs`)**

```csharp
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using kxEdit.Core.Editing;
using kxEdit.Core.Settings;

namespace kxEdit.Editor.Tests;

/// <summary>
/// フェーズ 9(設計書 §14.1・計画 §0.2): クリップして描いた絵は、クリップの内側で全面の絵と画素まで一致する。
/// WM_PAINT の DC は更新領域でクリップされているので、内側さえ正しければ画面は正しい。
/// 行をまたぐ描画(セル強調枠の下辺・IME の未確定表示・RenderFrame のシフト)も含めて確かめる。
/// </summary>
public class ClipPaintTests
{
    private static string Body() =>
        string.Join(
            "\r\n",
            Enumerable.Range(0, 40).Select(i =>
                i == 5
                    ? string.Concat(Enumerable.Range(0, 20).Select(k => $"[{k:D2}]-long-"))
                    : $"{i:D2} 吾輩は猫である。\t名前は まだ無い。 mixed 𠮷"
            )
        );

    private static (Form F, EditorControl C) MakeHosted()
    {
        var f = new Form { Size = new Size(420, 260) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(Body()));
        return (f, c);
    }

    private static int[] Pixels(Bitmap bmp)
    {
        var data = bmp.LockBits(
            new Rectangle(Point.Empty, bmp.Size),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb
        );
        try
        {
            var px = new int[bmp.Width * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + (y * data.Stride), px, y * bmp.Width, bmp.Width);
            return px;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static int Line(EditorControl c, int line) => c.CurrentBuffer.Current.GetLineStart(line);

    /// <summary>状態の組み合わせ(描画の各工程が 1 つ以上効くように選ぶ)。</summary>
    private static void Arrange(EditorControl c, int state)
    {
        switch (state)
        {
            case 0: // 既定
                c.SetCaretCharOffset(Line(c, 2) + 3);
                break;
            case 1: // 現在行強調 + 行番号 + 空白表示
                c.ApplyAppearance(new AppSettings { HighlightCurrentLine = true, ShowLineNumbers = true, ShowWhitespace = true });
                c.SetCaretCharOffset(Line(c, 3) + 1);
                break;
            case 2: // 複数行の選択(黒地テーマ = 選択文字色で本文 op を分割)
                c.ApplyAppearance(new AppSettings { Theme = "white-on-black" });
                c.SetSelectionCharRange(Line(c, 1) + 4, Line(c, 4) + 2);
                break;
            case 3: // セル強調(行 3)
                c.HighlightCharRange(Line(c, 3) + 2, 5);
                break;
            case 4: // IME 未確定(行 2)
                c.SetCaretCharOffset(Line(c, 2) + 4);
                c.__TestApplyComposition("にほんご", 2, [ImeAttribute.Input, ImeAttribute.TargetConverted, ImeAttribute.TargetConverted, ImeAttribute.Input], [0, 1, 3, 4]);
                break;
            case 5: // 折り返し ON + 選択
                c.WrapColumns = 24;
                c.SetSelectionCharRange(Line(c, 5) + 10, Line(c, 5) + 70);
                break;
            case 6: // 水平スクロール + 行番号
                c.ShowLineNumbers = true;
                c.ScrollX = 60;
                break;
        }
    }

    public static TheoryData<int> States() => [0, 1, 2, 3, 4, 5, 6];

    [Theory]
    [MemberData(nameof(States))]
    public void Clipped_paint_matches_full_paint_inside_the_clip(int state) =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                Arrange(c, state);
                if (state == 6)
                    Assert.True(c.ScrollX > 0, "前提: 水平スクロールが効いている");
                using var full = EditorControl.TestHook_PaintToBitmap(c, record: false);
                int[] truth = Pixels(full);
                int w = full.Width, h = full.Height;
                int lh = c.Metrics.LineHeightPx;
                var clips = new List<Rectangle>();
                for (int i = 0; i * lh < h; i++)
                    clips.Add(new Rectangle(0, i * lh, w, lh)); // 行にそろった帯
                clips.Add(new Rectangle(0, 4 * lh, w, 1)); // 行 3 の枠の下辺だけ(状態 3)
                clips.Add(new Rectangle(0, 3 * lh + lh - 1, w, 2)); // 行の境目の 2 画素
                var rng = new Random(state);
                for (int k = 0; k < 20; k++)
                {
                    int x = rng.Next(0, w - 1), y = rng.Next(0, h - 1);
                    clips.Add(new Rectangle(x, y, rng.Next(1, w - x + 1), rng.Next(1, h - y + 1)));
                }
                foreach (var clip in clips)
                {
                    using var part = EditorControl.TestHook_PaintToBitmap(c, record: true, clip);
                    int[] px = Pixels(part);
                    for (int y = clip.Top; y < Math.Min(h, clip.Bottom); y++)
                    for (int x = clip.Left; x < Math.Min(w, clip.Right); x++)
                        Assert.True(
                            px[y * w + x] == truth[y * w + x],
                            $"state={state} clip={clip}: ({x},{y}) が全面の絵と違う"
                        );
                }
            }
        });

    /// <summary>陽性対照: クリップの外は描かれない(クリップが実際に効いている)。</summary>
    [Fact]
    public void Pixels_outside_the_clip_are_not_painted() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                int lh = c.Metrics.LineHeightPx;
                using var part = EditorControl.TestHook_PaintToBitmap(c, record: true, new Rectangle(0, lh, 50, lh));
                Assert.Equal(0, part.GetPixel(200, 5 * lh).ToArgb()); // 透明のまま(Format32bppArgb の初期値)
            }
        });
}
```

- [ ] **Step 3: テストが失敗することを確かめる**

Run: `dotnet build tests/kxEdit.Editor.Tests -c Release -p:TreatWarningsAsErrors=false`
Expected: `ImeOrigin` / `SameLayoutAs` / `TestHook_PaintToBitmap` の clip 引数が未定義でビルドエラー。

- [ ] **Step 4: 実装(`ImeController.cs`)**

`Draw(Graphics g)` を次の 3 つに分ける(本体の描画コードは `curX` と `y` の出どころ以外そのまま)。

```csharp
    /// <summary>
    /// 未確定表示の原点(client 座標・水平スクロール適用後)。未確定がない・バッファがない・可視外なら null。
    /// 2026-09-27 フェーズ 9: <see cref="FrameInputs.ImeOrigin"/> として描画の入力に取り込む
    /// (無効化する帯を原点から求めるため。設計書 §14.1)。
    /// </summary>
    public Point? ComputeOrigin()
    {
        if (!_host.HasBuffer || _ime.Text.Length == 0)
            return null;
        var (x, y, visible) = _host.ComputeCaretPoint(_ime.Start);
        return visible ? new Point(x - _host.ScrollX, y) : null;
    }

    /// <summary>今の状態から原点を求めて描く(テスト・旧来の呼び出し用)。</summary>
    public void Draw(Graphics g)
    {
        if (ComputeOrigin() is Point origin)
            Draw(g, origin);
    }

    /// <summary>
    /// 未確定文字列 overlay 描画。旧 <c>EditorControl.DrawImeOverlay</c> bit-perfect 移設。(既存の summary をここへ移す)
    /// </summary>
    public void Draw(Graphics g, Point origin)
    {
        if (_ime.Text.Length == 0)
            return;
        int curX = origin.X;
        int y = origin.Y;
        // ↓ 以下、既存の「Clauses が空 or 節境界が 2 未満なら 1 節扱い」から末尾まで変更しない
```

既存の remarks(「Draw が host から読む値は、すべて FrameInputs に入っていること」)は `Draw(Graphics g, Point origin)` に残し、「原点は `FrameInputs.ImeOrigin` から受け取る」を 1 行足す。`IImeOverlayHost.cs` のコメント(32〜33 行)にも「原点は `FrameInputs.ImeOrigin`」を足す。

- [ ] **Step 5: 実装(`FrameInputs.cs`)**

`Ime` の次にメンバーを足す。

```csharp
    /// <summary>
    /// 未確定表示の原点(<see cref="ImeController.ComputeOrigin"/>)。表示しないときは null。
    /// フェーズ 9: 無効化する帯を原点から求める。
    /// </summary>
    public required Point? ImeOrigin { get; init; }
```

`Equals` を 2 つに分ける。

```csharp
    /// <summary>
    /// 行の外に効く入力(画面全体の描き方)が等しいか。等しくなければ、全面を描き直す(フェーズ 9・計画 §0.2)。
    /// 行の中身(<see cref="Snapshot"/>・<see cref="CurrentLineLogical"/>・<see cref="Selection"/>・
    /// <see cref="CellHighlight"/>・<see cref="Ime"/>・<see cref="ImeOrigin"/>)とスクロール位置
    /// (<see cref="TopLine"/>・<see cref="TopSegment"/>・<see cref="ScrollX"/>)は比べない。
    /// <see cref="Snapshot"/> が変わっても、行番号幅と hscroll の表示は <see cref="LineNumberWidth"/>・
    /// <see cref="PaintHeight"/> としてここで比べる。
    /// </summary>
    public bool SameLayoutAs(FrameInputs other) =>
        WrapColumns == other.WrapColumns
        && ClientSize == other.ClientSize
        && PaintWidth == other.PaintWidth
        && PaintHeight == other.PaintHeight
        && ShowLineNumbers == other.ShowLineNumbers
        && LineNumberWidth == other.LineNumberWidth
        && ShowWhitespace == other.ShowWhitespace
        && Style.Equals(other.Style)
        && ReferenceEquals(Metrics, other.Metrics)
        && ReferenceEquals(Font, other.Font)
        && ReferenceEquals(UnderlineFont, other.UnderlineFont)
        && ReferenceEquals(TargetFont, other.TargetFont)
        && BackColor.ToArgb() == other.BackColor.ToArgb();

    public bool Equals(FrameInputs? other) =>
        other is not null
        && SameLayoutAs(other)
        && ReferenceEquals(Snapshot, other.Snapshot)
        && TopLine == other.TopLine
        && TopSegment == other.TopSegment
        && ScrollX == other.ScrollX
        && CurrentLineLogical == other.CurrentLineLogical
        && Selection == other.Selection
        && CellHighlight == other.CellHighlight
        && Ime.Equals(other.Ime)
        && ImeOrigin == other.ImeOrigin;
```

remarks の「IME の未確定表示だけは例外」を「未確定表示の原点は `ImeOrigin` で受け取る。`ImeController.Draw` が host から読むのはフォントと色で、どちらも `Style`・フォント 3 つとしてここにある」に直す。

- [ ] **Step 6: 実装(`FrameRowCache.cs`)**

```csharp
// FrameRowCache.cs
// 2026-09-27 性能改善フェーズ 9(計画 §0.2): 描画の入力 → 可視行と行の記述子のキャッシュ。
// 差分の無効化(前回描いた入力と今の入力)と、直後の描画(今の入力)が同じ可視行を 2 度作らないためのもの。
using kxEdit.Core.Layout;

namespace kxEdit.Editor;

/// <summary>
/// 直近 2 つの描画の入力について、可視行(<see cref="EditorControl.BuildVisibleRows"/>)と記述子を持つ。
/// 入力は <see cref="FrameInputs.Equals(FrameInputs?)"/> で引く。UI スレッド専用。
/// </summary>
/// <remarks>
/// 2 件なのは「画面の絵の入力」と「今の入力」の 2 つが同時に要るため。入力はスナップショットを参照するので、
/// 本文やフォントを丸ごと差し替える経路では <see cref="Clear"/> で捨てる(古い本文を握らない)。
/// </remarks>
internal sealed class FrameRowCache
{
    private sealed class Entry(FrameInputs inputs, IReadOnlyList<VisualRow> rows)
    {
        public FrameInputs Inputs { get; } = inputs;
        public IReadOnlyList<VisualRow> Rows { get; } = rows;
        public RowPaintKey[]? Keys { get; set; }
    }

    private Entry? _recent;
    private Entry? _older;

    public IReadOnlyList<VisualRow> Rows(FrameInputs inputs) => Get(inputs).Rows;

    public RowPaintKey[] Keys(FrameInputs inputs)
    {
        var e = Get(inputs);
        return e.Keys ??= FrameDiff.Describe(
            inputs.Snapshot,
            e.Rows,
            inputs.CurrentLineLogical,
            inputs.Selection,
            inputs.CellHighlight
        );
    }

    public void Clear()
    {
        _recent = null;
        _older = null;
    }

    private Entry Get(FrameInputs inputs)
    {
        if (_recent is not null && _recent.Inputs.Equals(inputs))
            return _recent;
        if (_older is not null && _older.Inputs.Equals(inputs))
        {
            (_recent, _older) = (_older, _recent);
            return _recent;
        }
        var e = new Entry(inputs, EditorControl.BuildVisibleRows(inputs));
        _older = _recent;
        _recent = e;
        return e;
    }
}
```

- [ ] **Step 7: 実装(`EditorControl.cs` と `EditorControl.Paint.cs`)**

`EditorControl.cs`:
- `BuildVisibleRows` を `private static` → `internal static` にする(doc に「`FrameRowCache` もここを通す」を 1 行足す)。
- `_lastPaintedInputs` の宣言の次に足す:

```csharp
    // 2026-09-27 性能改善フェーズ 9: 描画の入力 → 可視行と記述子(直近 2 件)。
    // InvalidateAndForgetPaintedFrame で _lastPaintedInputs と一緒に捨てる(古い本文を握らない)。
    private readonly FrameRowCache _rowCache = new();
```

`EditorControl.Paint.cs`:
- `OnPaint` の `PaintAndRecord(e.Graphics)` / `PaintAndRecord(buffer.Graphics)` を `PaintAndRecord(e.Graphics, clip)` / `PaintAndRecord(buffer.Graphics, clip)` にする。
- `PaintAndRecord`:

```csharp
    private void PaintAndRecord(Graphics g, Rectangle clip)
    {
        _lastPaintedInputs = null;
        var inputs = CaptureFrameInputs();
        var rows = inputs is null ? null : _rowCache.Rows(inputs);
        var frame = PaintBody(g, inputs, rows, clip, BackColor, _imeCtrl);
        // テスト観測用(TestHook_GetLastFrame)。SetSource 前(frame が null)は従来どおり更新しない。
        // フェーズ 9: クリップに交差する行だけのフレームになる(本番コードで読む箇所はない)。
        if (frame is not null)
            _lastFrame = frame;
        _lastPaintedInputs = inputs;
    }
```

  summary に「クリップに交差する行だけを描く。全面の入力を記録してよい根拠は計画 docs/plans/2026-09-27-perf-partial-paint.md §0.3(不変条件 3)」を足す。
- `InvalidateAndForgetPaintedFrame` に `_rowCache.Clear();` を足す。
- `CaptureFrameInputs` の `Ime = _imeCtrl.State,` の次に `ImeOrigin = _imeCtrl.ComputeOrigin(),` を足す。
- `PaintBody`:

```csharp
    private static Frame? PaintBody(
        Graphics g,
        FrameInputs? inputs,
        IReadOnlyList<VisualRow>? rows,
        Rectangle clip,
        Color emptyBackColor,
        ImeController ime
    )
    {
        // (既存の g.Clear とコメントはそのまま)
        g.Clear(inputs?.BackColor ?? emptyBackColor);
        if (inputs is null || rows is null)
            return null;

        // フェーズ 9: クリップに交差する行だけを組み立てて描く(設計書 §14.1)。g のクリップは呼び出し側が掛けている
        // (WM_PAINT は BeginPaint の DC、バッファは SetClip)。行の外の描画(g.Clear・背景全域 op・IME)は
        // g のクリップで絞られる。
        var drawn = FrameBuilder.RowsTouching(
            rows,
            clip.Top,
            clip.Bottom,
            inputs.Metrics.LineHeightPx,
            inputs.CellHighlight
        );
        var frame = FrameBuilder.Build(
            inputs.Snapshot,
            drawn,
            inputs.PaintWidth,
            inputs.PaintHeight,
            inputs.LineNumberWidth,
            inputs.CurrentLineLogical,
            inputs.Selection,
            inputs.CellHighlight,
            inputs.ShowWhitespace,
            inputs.Style,
            inputs.Metrics
        );
        RenderFrame(g, frame, inputs.ScrollX, inputs.Font);

        // (既存の IME のコメント。末尾の 1 行を「フェーズ 9: 原点は FrameInputs.ImeOrigin から受け取る」に直す)
        if (inputs.ImeOrigin is Point origin)
            ime.Draw(g, origin);

        return frame;
    }
```

  remarks の「`ime` が唯一の例外で、host 経由で生の状態を読む」を「`ime` はフォントと色を host から読む(値は `FrameInputs` の `Style`・フォントと同じ)」に直す。`rows` は `inputs` から `FrameRowCache` が作ったもの(描画と差分で共有する)と書く。
- `TestHook_PaintToBitmap`:

```csharp
    internal static Bitmap TestHook_PaintToBitmap(EditorControl c, bool record, Rectangle? clip = null)
    {
        var size = c.ClientSize;
        var bmp = new Bitmap(
            Math.Max(1, size.Width),
            Math.Max(1, size.Height),
            System.Drawing.Imaging.PixelFormat.Format32bppArgb
        );
        using var g = Graphics.FromImage(bmp);
        var area = clip ?? new Rectangle(Point.Empty, size);
        if (clip is not null)
            g.SetClip(area);
        if (record)
            c.PaintAndRecord(g, area);
        else
        {
            // 正解の絵: キャッシュを通さず、今の状態から可視行を作り直して全面を描く(オラクルの独立性)。
            var inputs = c.CaptureFrameInputs();
            var rows = inputs is null ? null : BuildVisibleRows(inputs);
            PaintBody(g, inputs, rows, area, c.BackColor, c._imeCtrl);
        }
        return bmp;
    }
```

  doc に「`clip` を渡すと、その矩形でクリップして描く(WM_PAINT の部分再描画を模す)」を足す。

- [ ] **Step 8: テストが通ることを確かめる**

Run: `dotnet build kxEdit.sln -c Release; dotnet test tests/kxEdit.Editor.Tests -c Release --no-build`
Expected: 全件 PASS(`ClipPaintTests`・`FrameInputsTests` を含む。既存の `SkipInvalidateOracleTests` と `ImeOverlayColorTests` も PASS)。

- [ ] **Step 9: 描画の画素が変わっていないことを確かめる(`--paint-snapshot`)**

```powershell
git stash; dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --paint-snapshot "$d\snap-base"; git stash pop
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --paint-snapshot "$d\snap-task3" --compare "$d\snap-base"
```

(`git stash` の代わりに、Task 2 の commit を `git worktree add` で別ディレクトリに出して基準を撮ってもよい。)
Expected: 全枚一致。PrintWindow は全面を描くので、クリップの行の選び方がどうであれ一致するはず(全面のときは `RowsTouching` が入力をそのまま返す)。

- [ ] **Step 10: commit(commit 後に Editor.Tests と App.Tests を通す)**

```powershell
git add src/kxEdit.Editor tests/kxEdit.Editor.Tests
git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -F <msg>   # feat(editor): クリップに交差する行だけを描く(フェーズ 9a)
dotnet build kxEdit.sln -c Release; dotnet test tests/kxEdit.Editor.Tests -c Release --no-build; dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

- [ ] **Step 11: 仕様レビューと、前倒しのコード品質レビュー**

別エージェント 2 本(並走させるなら memory の 3 行の規則を冒頭に入れる)。コード品質レビューの観点:
- `PaintBody` の static 性(不変条件 1)が保たれているか。`rows` 引数が「`inputs` から作ったもの」以外を受け取る余地がないか。
- `FrameRowCache` が古いスナップショットを握り続ける経路がないか(`InvalidateAndForgetPaintedFrame` の 5 経路)。
- `ImeController.Draw(g)` と `Draw(g, origin)` の 2 本立ての是非。
- `ClipPaintTests` の網(状態の選び方・陽性対照)で、`RowsTouching` の上の行の規則を外したら落ちるか(レビュアーが scratchpad で当てて確かめる)。

指摘は ① fixup commit / ② PR に記載して受容 / ③ 理由付き却下 のどれかで扱い、実施記録に書く。

---

## Task 4: 差分の無効化(9a)

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.Paint.cs`(`InvalidateIfFrameChanged` → `InvalidateChangedRows`)、`src/kxEdit.Editor/EditorControl.Caret.cs`(4 経路・`EnsureVisibleCharRange`)、`src/kxEdit.Editor/EditorControl.cs`(`AfterEdit`・`HighlightCharRange`・`ClearHighlight`・`_lastPaintedInputs` のコメント)、`src/kxEdit.Editor/EditorControl.Ime.cs`(`IImeOverlayHost.Invalidate`)、`src/kxEdit.Editor/FrameInputs.cs`(冒頭コメント)
- Test: `tests/kxEdit.Editor.Tests/EditorControlPartialInvalidateTests.cs`(新規)、`tests/kxEdit.Editor.Tests/SkipInvalidateOracleTests.cs`

**Interfaces:**
- Consumes: Task 2 の `FrameDiff.DirtyBands`、Task 3 の `FrameRowCache.Keys`・`FrameInputs.SameLayoutAs`・`ImeOrigin`・`TestHook_PaintToBitmap(c, record, clip)`
- Produces:
  - `private void InvalidateChangedRows()`(Task 5 で `InvalidateChangedRows(bool allowScroll)` に広げる)
  - `private void InvalidateRectIfAny(Rectangle r)`(空・クライアント外の矩形は捨てる。`Invalidate(Rectangle.Empty)` が全面に化けるのを防ぐ)

- [ ] **Step 1: 失敗するテストを書く(`EditorControlPartialInvalidateTests.cs`)**

```csharp
using System.Drawing;

namespace kxEdit.Editor.Tests;

/// <summary>
/// フェーズ 9a(設計書 §14.1・計画 §0.2): 無効化するのは、記述子が違う行の帯だけ。
/// 数えるのは Control.Invalidated の InvalidRect。描画は TestHook_PaintToBitmap(record: true) で起こす。
/// 各テストは非既定の位置から始め、操作の前に「描画の記録がある」を確かめる(CLAUDE.md §4-B)。
/// </summary>
public class EditorControlPartialInvalidateTests
{
    // 30 行・400×260。行 25 は可視域の外。
    private static string Body() =>
        string.Join("\r\n", Enumerable.Range(0, 30).Select(i => $"line {i:D2} あいう abc"));

    private static (Form F, EditorControl C) MakeHosted(string? body = null)
    {
        var f = new Form { Size = new Size(400, 260) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(body ?? Body()));
        Assert.True(c.IsHandleCreated, "前提: ハンドルがある(Invalidate(Rectangle) が Invalidated を発火する条件)");
        return (f, c);
    }

    private static int Line(EditorControl c, int line) => c.CurrentBuffer.Current.GetLineStart(line);

    private static void PaintAndAssumeRecorded(EditorControl c)
    {
        EditorControl.TestHook_PaintToBitmap(c, record: true).Dispose();
        Assert.True(EditorControl.TestHook_HasLastPaintedInputs(c), "前提: 描画が記録されていない");
    }

    private static List<Rectangle> Rects(EditorControl c, Action act)
    {
        var rects = new List<Rectangle>();
        InvalidateEventHandler h = (_, e) => rects.Add(e.InvalidRect);
        c.Invalidated += h;
        try
        {
            act();
        }
        finally
        {
            c.Invalidated -= h;
        }
        return rects;
    }

    /// <summary>無効化した行の集合(帯の中身を LH で割る。+1px の下辺は次の行に数えない)。</summary>
    private static SortedSet<int> RowsOf(EditorControl c, List<Rectangle> rects)
    {
        int lh = c.Metrics.LineHeightPx;
        var set = new SortedSet<int>();
        foreach (var r in rects)
            for (int row = r.Top / lh; row * lh < r.Bottom - (r.Bottom % lh == 1 ? 1 : 0); row++)
                set.Add(row);
        return set;
    }

    private static bool IsFull(EditorControl c, List<Rectangle> rects) =>
        rects.Any(r => r.Contains(c.ClientRectangle));

    [Fact]
    public void Typing_invalidates_only_the_row() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 3) + 4);
                PaintAndAssumeRecorded(c);
                var rects = Rects(c, () => c.ReplaceCharRange(c.CaretCharOffset, 0, "x"));
                Assert.False(IsFull(c, rects));
                Assert.Equal([3], RowsOf(c, rects));
            }
        });

    [Fact]
    public void Enter_invalidates_the_row_and_everything_below_but_nothing_above() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 3) + 4);
                PaintAndAssumeRecorded(c);
                var rects = Rects(c, () => c.ReplaceCharRange(c.CaretCharOffset, 0, "\r\n"));
                Assert.False(IsFull(c, rects));
                var rows = RowsOf(c, rects);
                Assert.Equal(3, rows.Min);
                int lastVisible = (c.ClientSize.Height - 1) / c.Metrics.LineHeightPx;
                Assert.Equal(lastVisible, rows.Max);
            }
        });

    [Fact]
    public void Moving_the_current_line_invalidates_the_old_and_new_rows() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.HighlightCurrentLine = true;
                c.SetCaretCharOffset(Line(c, 2) + 3);
                PaintAndAssumeRecorded(c);
                var rects = Rects(c, () => c.SetCaretCharOffset(Line(c, 5) + 1));
                Assert.Equal([2, 5], RowsOf(c, rects));
            }
        });

    [Fact]
    public void Extending_a_selection_within_a_row_invalidates_only_that_row() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 2) + 3);
                PaintAndAssumeRecorded(c);
                var rects = Rects(c, () => c.MoveCaretWithSelection(Line(c, 2) + 8));
                Assert.Equal([2], RowsOf(c, rects));
            }
        });

    [Fact]
    public void Clearing_a_multi_row_selection_invalidates_those_rows() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetSelectionCharRange(Line(c, 1) + 4, Line(c, 3) + 5);
                PaintAndAssumeRecorded(c);
                var rects = Rects(c, () => c.SetCaretCharOffset(Line(c, 3) + 5));
                Assert.Equal([1, 2, 3], RowsOf(c, rects));
            }
        });

    [Fact]
    public void An_ime_update_invalidates_its_row_and_the_next() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 4) + 2);
                c.__TestApplyComposition("か", 1, [0], []);
                PaintAndAssumeRecorded(c);
                var rects = Rects(c, () => c.__TestApplyComposition("かな", 2, [0, 0], []));
                Assert.Equal([4, 5], RowsOf(c, rects));
            }
        });

    [Fact]
    public void A_cell_highlight_invalidates_its_row_plus_the_edge_pixel() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 1) + 1);
                PaintAndAssumeRecorded(c);
                int lh = c.Metrics.LineHeightPx;
                var rects = Rects(c, () => c.HighlightCharRange(Line(c, 4) + 2, 3));
                Assert.Equal(4 * lh, rects.Min(r => r.Top));
                Assert.Equal(5 * lh + 1, rects.Max(r => r.Bottom));
            }
        });

    [Fact]
    public void An_edit_outside_the_viewport_invalidates_nothing() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 2) + 1);
                PaintAndAssumeRecorded(c);
                int before = c.TopLine;
                // 行 25(可視外)の中身だけを変える。キャレットは動かさない(ReplaceCharRange はキャレットを置換の後ろへ動かすので、
                // 可視外へ追従スクロールが起きる = この API は使わない)。
                var rects = Rects(c, () => c.TestHook_ReplaceWithoutCaret(Line(c, 25) + 2, 1, "Z"));
                Assert.Equal(before, c.TopLine); // 前提: スクロールしていない
                Assert.Equal("Z", c.CurrentBuffer.Current.GetText(Line(c, 25) + 2, 1)); // 前提: 本文が変わった
                Assert.Empty(rects);
            }
        });

    [Fact]
    public void A_change_of_line_number_width_invalidates_everything() =>
        Sta.Run(() =>
        {
            // 999 行 → 1000 行で行番号の桁が 3 → 4 になる。
            var (f, c) = MakeHosted(string.Join("\r\n", Enumerable.Range(0, 999).Select(i => $"l{i}")));
            using (f)
            {
                c.ShowLineNumbers = true;
                c.SetCaretCharOffset(Line(c, 2));
                PaintAndAssumeRecorded(c);
                var rects = Rects(c, () => c.ReplaceCharRange(c.CaretCharOffset, 0, "\r\n"));
                Assert.True(IsFull(c, rects));
            }
        });

    [Fact]
    public void Retyping_in_a_wrapped_paragraph_does_not_invalidate_rows_above() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.WrapColumns = 10;
                c.SetCaretCharOffset(Line(c, 3) + 2);
                PaintAndAssumeRecorded(c);
                var rects = Rects(c, () => c.ReplaceCharRange(c.CaretCharOffset, 0, "xxxxxx"));
                Assert.False(IsFull(c, rects));
                int firstRowOfLine3 = c.TestHook_VisualRowIndexOf(Line(c, 3)); // 行 3 の先頭の視覚行の番号
                Assert.Equal(firstRowOfLine3, RowsOf(c, rects).Min);
            }
        });

    [Fact]
    public void Without_a_recorded_frame_everything_is_invalidated() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                Assert.False(EditorControl.TestHook_HasLastPaintedInputs(c)); // 前提
                var rects = Rects(c, () => c.ReplaceCharRange(0, 0, "x"));
                Assert.True(IsFull(c, rects));
            }
        });

    /// <summary>Review Focus 1: 一時的にキャレットを動かして戻す経路が、古い現在行を残さない。</summary>
    [Fact]
    public void EnsureVisibleCharRange_WithHighlight_LeavesNoStaleRow() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.HighlightCurrentLine = true;
                c.SetCaretCharOffset(Line(c, 2) + 1);
                PaintAndAssumeRecorded(c);
                c.EnsureVisibleCharRange(Line(c, 4), 1); // 可視域の中 = スクロールしない
                Assert.Equal(Line(c, 2) + 1, c.CaretCharOffset); // 前提: キャレットは戻っている
                Assert.Equal(0, c.TopLine); // 前提: スクロールしていない
                // 一時的な移動は setter を通らない(スクロールしない)ので、無効化は起きなくてよい。
                // 戻した後の入力が記録と等しいこと = 古い行が残らないこと。
                // スクロールを伴う場合は Task 5 のオラクル(偽の画面)で確かめる。
                Assert.True(EditorControl.TestHook_LastPaintedEqualsCurrent(c));
            }
        });
}
```

このテストが使うテストフックを `EditorControl.Paint.cs` に足す(Step 3 で実装):
- `internal void TestHook_ReplaceWithoutCaret(int start, int length, string text)` — `_buffer.Replace` → `AfterEdit()` だけを行い、キャレット・アンカーは置換前の位置のまま(置換がキャレットより後ろなので位置はずれない)。
- `internal int TestHook_VisualRowIndexOf(int offset)` — `_rowCache.Rows(CaptureFrameInputs()!)` の中で `SegmentStartChar <= offset < SegmentStartChar + SegmentLength`(または行末)の最初の index。
- `internal static bool TestHook_LastPaintedEqualsCurrent(EditorControl c)` — `c._lastPaintedInputs is { } p && p.Equals(c.CaptureFrameInputs())`。

- [ ] **Step 2: テストが失敗することを確かめる**

Run: `dotnet build kxEdit.sln -c Release -p:TreatWarningsAsErrors=false` の後 `dotnet test tests/kxEdit.Editor.Tests -c Release --no-build --filter "FullyQualifiedName~PartialInvalidate"`
Expected: テストフックが未定義でビルドエラー → フックを先に足すと、「行だけ」を期待するテストが全面の無効化で FAIL する(`Without_a_recorded_frame` と `A_change_of_line_number_width` と `EnsureVisible…` は PASS しうる)。FAIL / PASS の内訳を実施記録に書く。

- [ ] **Step 3: 実装(`EditorControl.Paint.cs`)**

`InvalidateIfFrameChanged` を次に置き換える(名前を変える。remarks の「正しさの根拠」は不変条件 3 の形に書き直す)。

```csharp
    /// <summary>
    /// 今の描画の入力と、画面の絵の入力(<see cref="_lastPaintedInputs"/>)の差から、描き直しが要る行の帯だけを
    /// 無効化する(設計書 §14.1・計画 §0.2)。等しければ何もしない(フェーズ 3 の省略)。
    /// 行の外に効く入力(<see cref="FrameInputs.SameLayoutAs"/>)やスクロール位置が違えば全面。
    /// </summary>
    /// <remarks>
    /// 正しさの根拠は計画 §0.3 の不変条件 3。無効化する帯は、前回の絵と今の絵で画素が違いうる範囲をすべて覆う
    /// (記述子が行の絵を決め尽くす = <see cref="RowPaintKey"/> の doc。IME は原点の行と次の行)。
    /// 描画の入力を変える経路は、必ずこれか <see cref="Control.Invalidate()"/> を自分で呼ぶ(不変条件 2)。
    /// </remarks>
    private void InvalidateChangedRows()
    {
        var now = CaptureFrameInputs();
        var old = _lastPaintedInputs;
        if (now is null || old is null)
        {
            Invalidate();
            return;
        }
        if (now.Equals(old))
            return;
        bool scrolled =
            now.TopLine != old.TopLine
            || now.TopSegment != old.TopSegment
            || now.ScrollX != old.ScrollX;
        if (scrolled || !now.SameLayoutAs(old))
        {
            Invalidate();
            return;
        }
        int lh = now.Metrics.LineHeightPx;
        InvalidateBands(
            FrameDiff.DirtyBands(_rowCache.Keys(old), _rowCache.Keys(now), 0, lh, now.PaintHeight)
        );
        InvalidateImeRows(old, now);
    }

    private void InvalidateBands(List<(int Top, int Bottom)> bands)
    {
        int width = ClientSize.Width;
        foreach (var (top, bottom) in bands)
            InvalidateRectIfAny(new Rectangle(0, top, width, bottom - top));
    }

    /// <summary>
    /// 未確定表示の行を無効化する。未確定表示は行の帯でクリップせずに描くので、原点の行と次の行の 2 行ぶんにする
    /// (太字のディセンダが帯を越える場合の余裕。越えないことは L5 で確かめる。設計書 §14.1 の例外 2)。
    /// </summary>
    private void InvalidateImeRows(FrameInputs old, FrameInputs now)
    {
        if (old.Ime.Equals(now.Ime) && old.ImeOrigin == now.ImeOrigin)
            return;
        int lh = now.Metrics.LineHeightPx;
        int width = ClientSize.Width;
        if (old.ImeOrigin is Point o)
            InvalidateRectIfAny(new Rectangle(0, o.Y, width, 2 * lh));
        if (now.ImeOrigin is Point n)
            InvalidateRectIfAny(new Rectangle(0, n.Y, width, 2 * lh));
    }

    /// <summary>
    /// クライアント領域と交差する部分だけを無効化する。空なら何もしない
    /// (<c>Invalidate(Rectangle.Empty)</c> は全面の無効化に化ける。設計書 §14.1)。
    /// </summary>
    private void InvalidateRectIfAny(Rectangle r)
    {
        r.Intersect(ClientRectangle);
        if (r.Width > 0 && r.Height > 0)
            Invalidate(r);
    }
```

Step 1 のテストフック 3 つを `TestHook_HasLastPaintedInputs` の近くに足す。

```csharp
    /// <summary>テスト専用: キャレットとアンカーを動かさずに本文を置き換え、編集の後処理(AfterEdit)を通す。</summary>
    internal void TestHook_ReplaceWithoutCaret(int start, int length, string text)
    {
        _buffer!.Replace(start, length, text);
        AfterEdit();
    }

    /// <summary>テスト専用: 今の可視行のうち、<paramref name="offset"/> を含む最初の視覚行の番号(ない場合は -1)。</summary>
    internal int TestHook_VisualRowIndexOf(int offset)
    {
        var rows = _rowCache.Rows(CaptureFrameInputs()!);
        for (int i = 0; i < rows.Count; i++)
            if (offset >= rows[i].SegmentStartChar && offset <= rows[i].SegmentStartChar + rows[i].SegmentLength)
                return i;
        return -1;
    }

    /// <summary>テスト専用: 画面の絵の入力(記録)と今の入力が等しいか。</summary>
    internal static bool TestHook_LastPaintedEqualsCurrent(EditorControl c) =>
        c._lastPaintedInputs is { } p && p.Equals(c.CaptureFrameInputs());
```

- [ ] **Step 4: 実装(呼び出しの置き換え)**

- `EditorControl.Caret.cs` の 4 経路: `InvalidateIfFrameChanged(); // フェーズ 3: …` → `InvalidateChangedRows(); // フェーズ 9: 変わった行だけ(設計書 §14.1)`。`SetCaretCharOffset` の remarks にある `InvalidateIfFrameChanged` も直す。
- `EnsureVisibleCharRange` の finally の `PositionCaret();` の次に次を足し、既存の「末尾 Invalidate は削除: …」のコメントを置き換える。

```csharp
            // フェーズ 9: 一時的に動かしたキャレット(現在行強調・選択)を戻したので、描画の入力が変わりうる。
            // スクロールのセッターが画面の絵の入力を一時的な状態で記録している場合もある(9b)ので、
            // 戻した状態との差を必ず無効化する(不変条件 2。Review Focus 1)。
            InvalidateChangedRows();
```

- `EditorControl.cs`:
  - `AfterEdit` の `Invalidate();` → `InvalidateChangedRows();`。remarks の「再描画(Invalidate)」を「再描画(InvalidateChangedRows = 変わった行だけ)」に直す。
  - `HighlightCharRange` と `ClearHighlight` の `Invalidate();` → `InvalidateChangedRows();`
  - `_lastPaintedInputs` のコメントを不変条件 2・3 の形に書き直す(「キャレット・選択の 4 経路だけは InvalidateIfFrameChanged()」を「差の行だけを無効化する経路(4 経路・AfterEdit・IME・セル強調)は InvalidateChangedRows()」に)。
- `EditorControl.Ime.cs`: `void IImeOverlayHost.Invalidate() => Invalidate();` → `void IImeOverlayHost.Invalidate() => InvalidateChangedRows();`(コメント 1 行: 「フェーズ 9: 未確定の行だけ」)。
- `FrameInputs.cs` 冒頭コメントの `InvalidateIfFrameChanged` の言及を直す。
- grep で残りがないことを確かめる: `rg "InvalidateIfFrameChanged" src tests` → 0 件。

- [ ] **Step 5: 状態を書き換えて無効化しない経路がないことを確かめる(フェーズ 3 Task 3 Step 1 と同じ観点)**

```powershell
rg -n "_caretCtrl\.(SetTo|SetSelection|MoveTo)|_cellHighlight =|_topLine =|_topSegment =|_scrollX =" src/kxEdit.Editor
```

各行について、その後に `AfterEdit` / `InvalidateChangedRows` / `Invalidate` / `InvalidateAndForgetPaintedFrame` のどれかが(同じメソッドの中で、状態の変更の**後に**)続くことを確かめ、表にして実施記録に書く。`UpdateVerticalScrollbar` / `UpdateHorizontalScrollbar` の中のクランプ(`_topLine` / `_scrollX` の直接代入)は、呼び出し元の後続の無効化に依存している(AfterEdit・リサイズ)。その呼び出し元を列挙して確かめる。

- [ ] **Step 6: オラクルを矩形の合成に広げる(`SkipInvalidateOracleTests.cs`)**

フェーズ 3 の申し送り(「Invalidate が 1 回でもあれば全面を描き直す」形では、無効化した矩形の不足を検出できない)。

1. `Skipped_invalidations_never_leave_a_stale_picture` のループを次の形にする。

```csharp
                    var rects = new List<Rectangle>();
                    InvalidateEventHandler h = (_, e) => rects.Add(e.InvalidRect);
                    c.Invalidated += h;
                    try
                    {
                        act();
                    }
                    finally
                    {
                        c.Invalidated -= h;
                    }
                    if (rects.Count > 0)
                    {
                        Composite(c, screen, rects); // WM_PAINT: 無効化した矩形の中だけが描き直される
                        if (!rects.Any(r => r.Contains(c.ClientRectangle)))
                            partial++;
                    }
                    else if (skippable && (c.CaretCharOffset != caretBefore || c.SelectionAnchor != anchorBefore))
                        skipped++;
```

   ループの後に `Assert.True(partial >= 20, $"seed={seed}: 部分的な無効化が {partial} 回しか起きていない");` を足す(前提: 部分の経路を実際に通っている)。

2. `Composite` を足す。

```csharp
    /// <summary>
    /// WM_PAINT を模す: 無効化した矩形の外接矩形をクリップにして描き(実際の e.ClipRectangle と同じ)、
    /// 矩形の和の中の画素だけを画面へ写す(実際の DC は更新領域でクリップされている)。
    /// </summary>
    private static void Composite(EditorControl c, int[] screen, List<Rectangle> rects)
    {
        var client = c.ClientRectangle;
        var bounds = Rectangle.Empty;
        foreach (var r in rects)
            bounds = bounds.IsEmpty ? r : Rectangle.Union(bounds, r);
        bounds.Intersect(client);
        if (bounds.IsEmpty)
            return;
        using var bmp = EditorControl.TestHook_PaintToBitmap(c, record: true, bounds);
        int[] px = Pixels(bmp);
        int w = bmp.Width;
        foreach (var r0 in rects)
        {
            var r = Rectangle.Intersect(r0, client);
            for (int y = r.Top; y < r.Bottom; y++)
                Array.Copy(px, y * w + r.Left, screen, y * w + r.Left, r.Width);
        }
    }
```

3. `PickOp` に操作を足す(`rng.Next(0, 22)` を `rng.Next(0, 26)` にし、`default` の前に)。

```csharp
            case 22:
                return ("改行挿入", false, () => c.ReplaceCharRange(caret, 0, "\r\n"));
            case 23:
            {
                int s = Rand();
                int n = rng.Next(1, 12);
                return ("範囲削除", false, () => c.ReplaceCharRange(s, Math.Min(n, len - Math.Min(s, len)), ""));
            }
            case 24:
                return c.__TestIsComposing()
                    ? ("IME 更新", false, () => c.__TestApplyComposition("かなかな", 2, [0, 0, 0, 0], []))
                    : ("セル強調を隣の行へ", false, () => c.HighlightCharRange(snap.GetLineStart(Math.Min(snap.LineCount - 1, snap.GetLineIndexOfChar(caret) + 1)), 3));
            case 25:
                return ("1 文字削除", false, () => c.ReplaceCharRange(Math.Max(0, caret - 1), caret > 0 ? 1 : 0, ""));
```

   クラスの doc コメントの「検出範囲」を書き直す: 「Invalidate を省きうる経路(4 経路)と、行の帯だけを無効化する経路(4 経路・編集・IME・セル強調)で、無効化の不足があれば検出する」。

4. 陽性対照を足す(オラクルが矩形の不足を検出できること)。

```csharp
    /// <summary>陽性対照: 無効化した矩形の一部を合成しなければ、古い絵として検出される。</summary>
    [Fact]
    public void Oracle_detects_a_missing_rectangle() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.HighlightCurrentLine = true;
                c.SetCaretCharOffset(c.CurrentBuffer.Current.GetLineStart(2) + 3);
                int[] screen = Paint(c, record: true);
                var rects = new List<Rectangle>();
                InvalidateEventHandler h = (_, e) => rects.Add(e.InvalidRect);
                c.Invalidated += h;
                c.SetCaretCharOffset(c.CurrentBuffer.Current.GetLineStart(5) + 1);
                c.Invalidated -= h;
                Assert.True(rects.Count >= 2, "前提: 旧行と新行の 2 つの帯が無効化される");
                Composite(c, screen, [rects[^1]]); // 旧行の帯を捨てる
                Assert.False(screen.AsSpan().SequenceEqual(Paint(c, record: false)));
            }
        });
```

- [ ] **Step 7: テストが通ることを確かめる**

Run: `dotnet build kxEdit.sln -c Release; dotnet test tests/kxEdit.Editor.Tests -c Release --no-build`
Expected: 全件 PASS。既存のテストで、Invalidated の回数や InvalidRect を全面前提で数えているものが FAIL した場合は、そのテストの意図(「無効化が起きること」か「全面であること」か)を読んで判断する。前者なら期待を「1 回以上・行の帯を含む」に直し、後者なら本変更の意図的な差として実施記録に書いてから直す。**テストを直した件数と理由を実施記録に列挙する**。

- [ ] **Step 8: オラクルが不足を検出できることを確かめる(一時的な改変。commit しない)**

次の 2 つを 1 つずつ当て、`SkipInvalidateOracleTests` と `EditorControlPartialInvalidateTests` が FAIL することを確かめて戻す(`git checkout -- <file>`)。ビルドは `-p:TreatWarningsAsErrors=false`。
1. `InvalidateImeRows` の `2 * lh` を `lh` にし、さらに `old.ImeOrigin` の行を無効化しない。
2. `FrameDiff.Describe` で `IsCurrentLine` を常に false にする。

結果(どのテストが何 seed で落ちたか)を実施記録に書く。落ちなければ網の穴なので、原因を調べて網を足す。

- [ ] **Step 9: commit(commit 後に全テスト)**

```powershell
git add src/kxEdit.Editor tests/kxEdit.Editor.Tests
git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -F <msg>   # feat(editor): 変わった行だけを無効化する(フェーズ 9a)
dotnet build kxEdit.sln -c Release; dotnet test kxEdit.sln -c Release --no-build
```

- [ ] **Step 10: 仕様レビュー**(別エージェント。Step 5 の表とオラクルの改変結果も渡す)

---

## Task 5: ScrollWindowEx(9b)

**Files:**
- Create: `src/kxEdit.Editor/PaintSurface.cs`、`tests/kxEdit.Editor.Tests/Fakes/ScreenSurface.cs`
- Modify: `src/kxEdit.Editor/NativeMethods.cs`、`src/kxEdit.Editor/EditorControl.Paint.cs`、`src/kxEdit.Editor/EditorControl.cs`(3 つのセッター・`_paintSurface` フィールド)、`tests/kxEdit.Editor.Tests/SkipInvalidateOracleTests.cs`
- Test: `tests/kxEdit.Editor.Tests/ScrollPixelsTests.cs`(新規)

**Interfaces:**
- Consumes: Task 4 の `InvalidateChangedRows`・`InvalidateBands`・`InvalidateRectIfAny`
- Produces:
  - `internal interface IPaintSurface { bool CanScroll(EditorControl editor); bool TryScroll(EditorControl editor, int dx, int dy, Rectangle area, out Rectangle uncovered); }`
  - `internal sealed class Win32PaintSurface : IPaintSurface`(`Instance`)
  - `internal static void EditorControl.TestHook_SetPaintSurface(EditorControl c, IPaintSurface surface)`
  - `private void InvalidateChangedRows(bool allowScroll)`(引数なし版は `allowScroll: false`)

- [ ] **Step 1: 偽の画面を書く(`Fakes/ScreenSurface.cs`)**

```csharp
using System.Drawing;

namespace kxEdit.Editor.Tests.Fakes;

/// <summary>
/// オラクル用の「画面」(フェーズ 9b)。<see cref="IPaintSurface.TryScroll"/> で画素を実際に動かし、
/// 露出した帯は古い画素のまま残す(実画面の ScrollWindowEx と同じ = 描き直さなければ古い絵が残る)。
/// <see cref="Pending"/> は「この操作で無効化が起きた」(= 保留中の無効領域がある)を表し、テストが立てる。
/// </summary>
internal sealed class ScreenSurface(int width, int height) : IPaintSurface
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int[] Pixels { get; set; } = new int[width * height];
    public bool Pending { get; set; }
    public int Scrolls { get; private set; }

    public bool CanScroll(EditorControl editor) => !Pending;

    public bool TryScroll(EditorControl editor, int dx, int dy, Rectangle area, out Rectangle uncovered)
    {
        var copy = (int[])Pixels.Clone();
        for (int y = area.Top; y < area.Bottom; y++)
        for (int x = area.Left; x < area.Right; x++)
        {
            int sx = x - dx, sy = y - dy;
            if (area.Contains(sx, sy))
                Pixels[y * Width + x] = copy[sy * Width + sx];
        }
        uncovered =
            dy < 0 ? Rectangle.FromLTRB(area.Left, area.Bottom + dy, area.Right, area.Bottom)
            : dy > 0 ? Rectangle.FromLTRB(area.Left, area.Top, area.Right, area.Top + dy)
            : dx < 0 ? Rectangle.FromLTRB(area.Right + dx, area.Top, area.Right, area.Bottom)
            : Rectangle.FromLTRB(area.Left, area.Top, area.Left + dx, area.Bottom);
        Scrolls++;
        return true;
    }
}
```

- [ ] **Step 2: 失敗するテストを書く(`ScrollPixelsTests.cs`)**

```csharp
using System.Drawing;
using kxEdit.Editor.Tests.Fakes;

namespace kxEdit.Editor.Tests;

/// <summary>
/// フェーズ 9b(設計書 §14.2・計画 §0.2): スクロールのセッターは、画素を移して露出した帯と変わった行だけを無効化する。
/// 画素の移動は偽の画面(<see cref="ScreenSurface"/>)で受ける。
/// </summary>
public class ScrollPixelsTests
{
    private static string Body() =>
        string.Join("\r\n", Enumerable.Range(0, 80).Select(i => i == 3 ? new string('w', 300) : $"line {i:D2} あいう abc"));

    private static (Form F, EditorControl C, ScreenSurface S) MakeHosted()
    {
        var f = new Form { Size = new Size(400, 260) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(Body()));
        var s = new ScreenSurface(c.ClientSize.Width, c.ClientSize.Height);
        EditorControl.TestHook_SetPaintSurface(c, s);
        return (f, c, s);
    }

    private static void Paint(EditorControl c) =>
        EditorControl.TestHook_PaintToBitmap(c, record: true).Dispose();

    private static List<Rectangle> Rects(EditorControl c, Action act)
    {
        var rects = new List<Rectangle>();
        InvalidateEventHandler h = (_, e) => rects.Add(e.InvalidRect);
        c.Invalidated += h;
        try
        {
            act();
        }
        finally
        {
            c.Invalidated -= h;
        }
        return rects;
    }

    [Fact]
    public void One_line_down_scrolls_pixels_and_invalidates_only_the_exposed_band() =>
        Sta.Run(() =>
        {
            var (f, c, s) = MakeHosted();
            using (f)
            {
                c.TopLine = 5;
                Paint(c);
                int lh = c.Metrics.LineHeightPx;
                var rects = Rects(c, () => c.TopLine = 6);
                Assert.Equal(1, s.Scrolls);
                Assert.DoesNotContain(rects, r => r.Contains(c.ClientRectangle));
                // 露出した帯(1 行)と、途中で切れる新しい最下行の帯だけ = 高さは 2 行ぶん以下。
                var union = rects.Aggregate(Rectangle.Union);
                Assert.True(union.Height <= 2 * lh, $"無効化の和 {union} が 2 行ぶんを超える");
            }
        });

    [Fact]
    public void A_page_or_more_invalidates_everything_without_scrolling() =>
        Sta.Run(() =>
        {
            var (f, c, s) = MakeHosted();
            using (f)
            {
                c.TopLine = 5;
                Paint(c);
                var rects = Rects(c, () => c.TopLine = 40);
                Assert.Equal(0, s.Scrolls);
                Assert.Contains(rects, r => r.Contains(c.ClientRectangle));
            }
        });

    [Fact]
    public void A_pending_update_prevents_scrolling() =>
        Sta.Run(() =>
        {
            var (f, c, s) = MakeHosted();
            using (f)
            {
                c.TopLine = 5;
                Paint(c);
                s.Pending = true;
                var rects = Rects(c, () => c.TopLine = 6);
                Assert.Equal(0, s.Scrolls);
                Assert.Contains(rects, r => r.Contains(c.ClientRectangle));
            }
        });

    [Fact]
    public void An_active_composition_prevents_scrolling() =>
        Sta.Run(() =>
        {
            var (f, c, s) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(c.CurrentBuffer.Current.GetLineStart(8));
                c.__TestApplyComposition("か", 1, [0], []);
                c.TopLine = 5;
                Paint(c);
                c.TopLine = 6;
                Assert.Equal(0, s.Scrolls);
            }
        });

    [Fact]
    public void Horizontal_scroll_moves_pixels_sideways() =>
        Sta.Run(() =>
        {
            var (f, c, s) = MakeHosted();
            using (f)
            {
                c.ScrollX = 40;
                Assert.Equal(40, c.ScrollX); // 前提: hscroll が表示されている(行 3 が長い)
                Paint(c);
                var rects = Rects(c, () => c.ScrollX = 60);
                Assert.Equal(1, s.Scrolls);
                Assert.DoesNotContain(rects, r => r.Contains(c.ClientRectangle));
            }
        });

    [Fact]
    public void Scrolling_without_a_recorded_frame_invalidates_everything() =>
        Sta.Run(() =>
        {
            var (f, c, s) = MakeHosted();
            using (f)
            {
                Assert.False(EditorControl.TestHook_HasLastPaintedInputs(c)); // 前提
                var rects = Rects(c, () => c.TopLine = 1);
                Assert.Equal(0, s.Scrolls);
                Assert.Contains(rects, r => r.Contains(c.ClientRectangle));
            }
        });
}
```

- [ ] **Step 3: テストが失敗することを確かめる**

Run: `dotnet build tests/kxEdit.Editor.Tests -c Release -p:TreatWarningsAsErrors=false`
Expected: `IPaintSurface` / `TestHook_SetPaintSurface` が未定義でビルドエラー。

- [ ] **Step 4: 実装(`NativeMethods.cs`)**

既存の `DllImport` の並びに足す(RECT は既存のものを使う)。

```csharp
    // 2026-09-27 フェーズ 9b: スクロールで既存の画素を移す(設計書 §14.2)。UI スレッド専用。
    // 戻り値 0 = ERROR。prcUpdate は、移した結果として描き直しが要る領域(露出した帯と、他の窓に隠れていた部分)の外接矩形。
    [DllImport("user32.dll")]
    public static extern int ScrollWindowEx(
        nint hWnd,
        int dx,
        int dy,
        ref RECT prcScroll,
        ref RECT prcClip,
        nint hrgnUpdate,
        out RECT prcUpdate,
        uint flags
    );

    // 保留中の無効領域があるか(lpRect = 0 で有無だけを見る)。
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetUpdateRect(
        nint hWnd,
        nint lpRect,
        [MarshalAs(UnmanagedType.Bool)] bool bErase
    );
```

- [ ] **Step 5: 実装(`PaintSurface.cs`)**

```csharp
// PaintSurface.cs
// 2026-09-27 性能改善フェーズ 9b(設計書 §14.2・計画 §0.2): スクロールで既存の画素を移す seam。
// 製品は Win32PaintSurface(ScrollWindowEx)。テストは画面のビットマップを動かす偽物に差し替える
// (画面外の HostForm には WM_PAINT が来ず、保留中の無効領域も消えないため)。
namespace kxEdit.Editor;

/// <summary>画面の画素を移す先。UI スレッド専用。</summary>
internal interface IPaintSurface
{
    /// <summary>
    /// 画素を移してよいか。移した画素が「画面の絵の入力」(<c>_lastPaintedInputs</c>)から描いた絵であることが前提
    /// (計画 §0.3 の不変条件 3)なので、保留中の無効領域があるときは false にする。
    /// </summary>
    bool CanScroll(EditorControl editor);

    /// <summary>
    /// <paramref name="area"/> の画素を (<paramref name="dx"/>, <paramref name="dy"/>) だけ移す。失敗したら false。
    /// <paramref name="uncovered"/> は、移した結果として描き直しが要る領域の外接矩形(空のこともある)。
    /// </summary>
    bool TryScroll(EditorControl editor, int dx, int dy, Rectangle area, out Rectangle uncovered);
}

/// <summary><see cref="IPaintSurface"/> の製品実装(ScrollWindowEx)。</summary>
internal sealed class Win32PaintSurface : IPaintSurface
{
    public static readonly Win32PaintSurface Instance = new();

    private Win32PaintSurface() { }

    /// <remarks>
    /// false にする条件: ハンドルがない・非表示(画面外の窓・最小化前の組み立て中など)・保留中の無効領域がある・
    /// 兄弟のコントロールがエディタに重なる(CSV のセル編集 TextBox など。重なった部分の画素を運ぶと、
    /// 他のコントロールの絵がエディタに混ざるおそれがある)・スクロールバー以外の子がある。
    /// いずれも全面の再描画に落とすだけなので、条件は保守的でよい。
    /// </remarks>
    public bool CanScroll(EditorControl editor)
    {
        if (!editor.IsHandleCreated || !editor.Visible)
            return false;
        if (NativeMethods.GetUpdateRect(editor.Handle, 0, false))
            return false;
        if (editor.Parent is { } parent)
        {
            foreach (Control sibling in parent.Controls)
            {
                if (
                    !ReferenceEquals(sibling, editor)
                    && sibling.Visible
                    && sibling.Bounds.IntersectsWith(editor.Bounds)
                )
                    return false;
            }
        }
        foreach (Control child in editor.Controls)
        {
            if (child is not ScrollBar && child.Visible)
                return false;
        }
        return true;
    }

    public bool TryScroll(EditorControl editor, int dx, int dy, Rectangle area, out Rectangle uncovered)
    {
        var rc = new NativeMethods.RECT
        {
            left = area.Left,
            top = area.Top,
            right = area.Right,
            bottom = area.Bottom,
        };
        var clip = rc;
        // flags 0: 子ウィンドウ(スクロールバー)は動かさず、無効化もしない(呼び出し側が uncovered を無効化する)。
        int result = NativeMethods.ScrollWindowEx(editor.Handle, dx, dy, ref rc, ref clip, 0, out var upd, 0);
        uncovered = Rectangle.FromLTRB(upd.left, upd.top, upd.right, upd.bottom);
        return result != 0;
    }
}
```

- [ ] **Step 6: 実装(`InvalidateChangedRows(bool allowScroll)`)**

`EditorControl.cs` にフィールドを足す(`_rowCache` の次):

```csharp
    // 2026-09-27 性能改善フェーズ 9b: スクロールで画素を移す先(テストは TestHook_SetPaintSurface で差し替える)。
    private IPaintSurface _paintSurface = Win32PaintSurface.Instance;
```

`EditorControl.Paint.cs` の `InvalidateChangedRows()` を次に置き換える。

```csharp
    /// <summary>(Task 4 の summary)スクロールでは画素を移さない = スクロール位置が違えば全面。</summary>
    private void InvalidateChangedRows() => InvalidateChangedRows(allowScroll: false);

    /// <summary>
    /// (Task 4 の summary に続けて)<paramref name="allowScroll"/> が true(スクロールのセッター)なら、
    /// スクロール位置の差を <see cref="IPaintSurface.TryScroll"/> で画素の移動に変え、露出した帯と、
    /// 行の対応をずらして比べたときに記述子が違う行だけを無効化する(設計書 §14.2)。
    /// </summary>
    /// <remarks>
    /// 画素を移した後は <see cref="_lastPaintedInputs"/> を今の入力にする(計画 §0.3 の不変条件 3。
    /// 画面は、これから無効化する範囲を除いて、今の入力から描いた絵と同じになった)。
    /// 全面に落とす条件: 記録がない・行の外に効く入力が違う・縦と横が同時に変わった・IME の未確定がある・
    /// 移動量が可視域以上・行の対応が見つからない・<see cref="IPaintSurface.CanScroll"/> が false・移動に失敗した。
    /// 全面のまま残すもの(設計書 §14.2): クランプ(UpdateVerticalScrollbar など)・ReplaceSource・ApplyAppearance・
    /// WrapColumns は、この経路を通らないか allowScroll が false の経路で呼ばれる。
    /// </remarks>
    private void InvalidateChangedRows(bool allowScroll)
    {
        var now = CaptureFrameInputs();
        var old = _lastPaintedInputs;
        if (now is null || old is null)
        {
            Invalidate();
            return;
        }
        if (now.Equals(old))
            return;
        if (!now.SameLayoutAs(old))
        {
            Invalidate();
            return;
        }
        int lh = now.Metrics.LineHeightPx;
        bool scrolled =
            now.TopLine != old.TopLine
            || now.TopSegment != old.TopSegment
            || now.ScrollX != old.ScrollX;
        if (!scrolled)
        {
            InvalidateBands(
                FrameDiff.DirtyBands(_rowCache.Keys(old), _rowCache.Keys(now), 0, lh, now.PaintHeight)
            );
            InvalidateImeRows(old, now);
            return;
        }
        if (
            !allowScroll
            || old.Ime.IsActive
            || now.Ime.IsActive
            || !TryPlanScroll(old, now, out int shift, out int dx)
            || !_paintSurface.CanScroll(this)
        )
        {
            Invalidate();
            return;
        }
        var bands = FrameDiff.DirtyBands(
            _rowCache.Keys(old),
            _rowCache.Keys(now),
            shift,
            lh,
            now.PaintHeight
        );
        var area = new Rectangle(0, 0, now.PaintWidth, now.PaintHeight);
        int dy = -shift * lh;
        if (!_paintSurface.TryScroll(this, dx, dy, area, out var uncovered))
        {
            Invalidate();
            return;
        }
        _lastPaintedInputs = now;
        // ScrollWindowEx はシステムキャレットも画素と一緒に動かしうるので、置き直す(設計書 §14.2 の実機確認 1)。
        PositionCaret();
        InvalidateRectIfAny(uncovered);
        InvalidateRectIfAny(ExposedStrip(area, dx, dy));
        InvalidateBands(bands);
    }

    /// <summary>
    /// 行の対応(新しい行 i = 古い行 i + <paramref name="shift"/>)と横の移動量を決める。縦と横が同時に変わるとき・
    /// 移動量が可視域以上のとき・行の対応が見つからないときは false(全面)。
    /// 行の対応は可視行の (論理行, 視覚行) で探す。対応が誤っていても記述子の比較が違う行を無効化するので、
    /// 正しさには効かない(効率だけ)。
    /// </summary>
    private bool TryPlanScroll(FrameInputs old, FrameInputs now, out int shift, out int dx)
    {
        shift = 0;
        dx = 0;
        bool vertical = now.TopLine != old.TopLine || now.TopSegment != old.TopSegment;
        bool horizontal = now.ScrollX != old.ScrollX;
        if (vertical && horizontal)
            return false;
        if (horizontal)
        {
            dx = old.ScrollX - now.ScrollX;
            return Math.Abs(dx) < now.PaintWidth;
        }
        var oldRows = _rowCache.Rows(old);
        var nowRows = _rowCache.Rows(now);
        if (oldRows.Count == 0 || nowRows.Count == 0)
            return false;
        int k = IndexOfRow(oldRows, nowRows[0]);
        if (k > 0)
            shift = k;
        else
        {
            int j = IndexOfRow(nowRows, oldRows[0]);
            if (j <= 0)
                return false;
            shift = -j;
        }
        return Math.Abs(shift) * now.Metrics.LineHeightPx < now.PaintHeight;
    }

    private static int IndexOfRow(IReadOnlyList<VisualRow> rows, VisualRow target)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].LogicalLine == target.LogicalLine && rows[i].SegmentIndex == target.SegmentIndex)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 画素を移して露出した帯。<see cref="IPaintSurface.TryScroll"/> の uncovered とは別に必ず無効化する
    /// (途中で切れていた旧最下行の下半分もこの帯に入る。設計書 §14.2)。
    /// </summary>
    private static Rectangle ExposedStrip(Rectangle area, int dx, int dy) =>
        dy < 0 ? Rectangle.FromLTRB(area.Left, area.Bottom + dy, area.Right, area.Bottom)
        : dy > 0 ? Rectangle.FromLTRB(area.Left, area.Top, area.Right, area.Top + dy)
        : dx < 0 ? Rectangle.FromLTRB(area.Right + dx, area.Top, area.Right, area.Bottom)
        : dx > 0 ? Rectangle.FromLTRB(area.Left, area.Top, area.Left + dx, area.Bottom)
        : Rectangle.Empty;

    /// <summary>テスト専用: 画素の移動先を差し替える(偽の画面)。</summary>
    internal static void TestHook_SetPaintSurface(EditorControl c, IPaintSurface surface) =>
        c._paintSurface = surface;
```

`EditorControl.cs` の `TopLine` / `SetTopPosition` / `ScrollX` のセッターの `Invalidate();` を `InvalidateChangedRows(allowScroll: true);` にし、それぞれに 1 行コメント「フェーズ 9b: 画素を移して露出した帯だけ(設計書 §14.2)」を付ける。`TopLine` の doc の「Invalidate」も直す。

- [ ] **Step 7: オラクルにスクロールを入れる(`SkipInvalidateOracleTests.cs`)**

1. `MakeHosted` の後に偽の画面を差し込み、画面の配列をそれと共有する。`Skipped_invalidations_never_leave_a_stale_picture` の先頭を次にする。

```csharp
                var surface = new ScreenSurface(c.ClientSize.Width, c.ClientSize.Height);
                EditorControl.TestHook_SetPaintSurface(c, surface);
                surface.Pixels = Paint(c, record: true);
                int[] screen = surface.Pixels;
```

   ループの中で、`act()` の前に `surface.Pending = false;`、ハンドラを `(_, e) => { rects.Add(e.InvalidRect); surface.Pending = true; }` にする。`surface.TryScroll` は `Pixels` の中身を書き換えるので、`screen` と同じ配列を指し続けるよう `Pixels` を差し替えないこと(`TryScroll` は `copy` から `Pixels` へ書き戻す実装になっている)。ループの後に `Assert.True(surface.Scrolls >= 5, $"seed={seed}: 画素の移動が {surface.Scrolls} 回しか起きていない");` を足す。

2. `PickOp` に小さなスクロールを足す(`rng.Next(0, 26)` → `rng.Next(0, 30)`)。

```csharp
            case 26:
            case 27:
            {
                int d = rng.Next(1, 4) * (rng.Next(2) == 0 ? -1 : 1);
                return ("小スクロール(縦)", false, () => c.TopLine = Math.Clamp(c.TopLine + d, 0, snap.LineCount - 1));
            }
            case 28:
            {
                int d = rng.Next(1, 30) * (rng.Next(2) == 0 ? -1 : 1);
                return ("小スクロール(横)", false, () => c.ScrollX = Math.Max(0, c.ScrollX + d));
            }
            case 29:
                return ("視覚行スクロール", false, () => c.SetTopPosition(c.TopLine, rng.Next(0, 3)));
```

3. 陽性対照を足す。

```csharp
    /// <summary>陽性対照: 画素を移した後に露出した帯を描かなければ、古い絵として検出される。</summary>
    [Fact]
    public void Oracle_detects_an_unpainted_exposed_band() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                var surface = new ScreenSurface(c.ClientSize.Width, c.ClientSize.Height);
                EditorControl.TestHook_SetPaintSurface(c, surface);
                c.TopLine = 5;
                surface.Pixels = Paint(c, record: true);
                c.TopLine = 6;
                Assert.Equal(1, surface.Scrolls); // 前提: 画素を移した
                Assert.False(surface.Pixels.AsSpan().SequenceEqual(Paint(c, record: false)));
            }
        });
```

- [ ] **Step 8: テストが通ることを確かめる**

Run: `dotnet build kxEdit.sln -c Release; dotnet test tests/kxEdit.Editor.Tests -c Release --no-build`
Expected: 全件 PASS。

- [ ] **Step 9: オラクルがスクロールの不足を検出できることを確かめる(一時的な改変。commit しない)**

次を 1 つずつ当てて、オラクルか `ScrollPixelsTests` が FAIL することを確かめて戻す。
1. `InvalidateChangedRows(bool)` の `InvalidateRectIfAny(ExposedStrip(...))` と `InvalidateRectIfAny(uncovered)` を消す。
2. `FrameDiff.DirtyBands` の先頭(`At(old, shift - 1) is { HasCell: true }`)を消す。
3. `EnsureVisibleCharRange` の finally に Task 4 で足した `InvalidateChangedRows();` を消す(Review Focus 1)。

3 が落ちない場合は、PickOp に「現在行強調 ON で `EnsureVisibleCharRange` を可視域の外へ」の操作を足して落ちることを確かめる(`c.EnsureVisibleCharRange(Rand(), 0)`)。結果を実施記録に書く。

- [ ] **Step 10: commit(commit 後に全テスト)**

```powershell
git add src/kxEdit.Editor tests/kxEdit.Editor.Tests
git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -F <msg>   # feat(editor): スクロールで画素を移し、露出した帯だけを描く(フェーズ 9b)
dotnet build kxEdit.sln -c Release; dotnet test kxEdit.sln -c Release --no-build
```

- [ ] **Step 11: 仕様レビューと、前倒しのコード品質レビュー**(`IPaintSurface` の seam)。観点:
- 不変条件 3 が崩れる経路(スクロールの後に、無効化せずに状態を変える経路)がないか。Task 4 Step 5 の表を、スクロールの後という観点で見直す。
- `CanScroll` の条件が足りているか(兄弟・子・非表示・保留中の無効領域)。
- `ScrollWindowEx` とシステムキャレット(`PositionCaret` の置き直し)。
- 偽の画面(`ScreenSurface`)が実画面の意味論(露出した帯は古い画素のまま・保留中の無効領域)を正しく模しているか。

---

## Task 6: Smoke `--paint-transition` に部分再描画とスクロールを足す

**Files:**
- Modify: `tests/kxEdit.Editor.Smoke/PaintTransition.cs`、`tools/README.md`(`--paint-transition` の節)

**Interfaces:**
- Consumes: Task 4・5 の製品コード(実画面で確かめる)
- Produces: `Expect.Partial`(描画 1 回以上・クリップの和がクライアント全体より小さい・X = Y)、`Expect.StaleScroll`(陽性対照: 画素を移した直後に無効領域を取り消すと、X ≠ A かつ X ≠ Y)

- [ ] **Step 1: クリップの和を記録する**

`s_paints` の次に `private static Rectangle s_clip;` を足す。`RunAll` の購読を次にする。

```csharp
        editor.Paint += (_, e) =>
        {
            s_paints++;
            s_clip = s_clip.IsEmpty ? e.ClipRectangle : Rectangle.Union(s_clip, e.ClipRectangle);
        };
```

`RunOne` の `int p0 = s_paints;`(Act の直前)の次に `s_clip = Rectangle.Empty;` を足し、`int paints = s_paints - p0;` の次に `var clip = s_clip;` を足す。

- [ ] **Step 2: 期待の種類を足す**

`Expect` に足す。

```csharp
        /// <summary>
        /// フェーズ 9: 部分再描画(描画 1 回以上・クリップの和の面積がクライアント全体より小さい・X = Y)。
        /// </summary>
        Partial,

        /// <summary>
        /// フェーズ 9b の陽性対照: 画素を移した直後に無効領域を取り消す。描画 0 回で、X ≠ A(画素が動いた)かつ
        /// X ≠ Y(露出した帯が古い)であること。
        /// </summary>
        StaleScroll,
```

`failure` の switch に足す(`Expect.Stale` の行の次)。

```csharp
            Expect.StaleScroll when paints != 0 => "陽性対照で描画が起きた(ValidateRect が効かない)",
            Expect.StaleScroll when Diff(x, a).Count == 0 =>
                "陽性対照で画素が動いていない(ScrollWindowEx の経路を通っていない)",
            Expect.StaleScroll when diff == 0 => "陽性対照で差が出ない(露出した帯の古さを撮れていない)",
            Expect.StaleScroll => null,
```

`_ when diff != 0 => "古い絵が残る",` の次に足す。

```csharp
            Expect.Partial when paints == 0 => "描き直しが要るのに描画 0 回",
            Expect.Partial
                when (long)clip.Width * clip.Height
                    >= (long)editor.ClientSize.Width * editor.ClientSize.Height =>
                $"部分再描画を期待したが全面を描いた(クリップの和 {clip})",
```

保留中の無効領域の自己チェック(`t.Expect != Expect.Stale` の条件)を `t.Expect is not (Expect.Stale or Expect.StaleScroll)` にし、エディタの子(スクロールバー)にも `GetUpdateRect` をかける(フェーズ 3 の申し送り)。

```csharp
            foreach (Control child in editor.Controls)
                Check(
                    !child.IsHandleCreated || !GetUpdateRect(child.Handle, 0, false),
                    $"{name}: 子({child.GetType().Name})に保留中の無効領域が残る"
                );
```

遷移の表の自己チェックに `Partial` を足す: `t.Expect is not (Expect.Paint or Expect.Partial) || changed != 0`。陽性対照の失敗で止める条件を `t.Expect is Expect.Stale or Expect.StaleScroll` にする。`label` に `Expect.StaleScroll => "陽性対照(スクロール)"` を足す。

- [ ] **Step 3: 遷移を足す・期待を部分に変える**

`Transitions` を次のように変える(既存の要素は、`Expect` の変更を除いてそのまま)。
- `control-stale` の次に:

```csharp
        new("control-stale-scroll", Expect.StaleScroll)
        {
            Arrange = (e, _) => e.TopLine = 5,
            Arranged = _ => (0, 0),
            ArrangedScroll = (5, 0),
            Act = (e, _) =>
            {
                e.TopLine = 6;
                ValidateRect(e.Handle, 0); // 露出した帯を描かせない
            },
            VerifyAfter = (e, _) => e.TopLine == 6 ? null : "TopLine が 6 にならない",
        },
```

- `curline-next-line`・`select-extend`・`select-clear`・`ime-cancel-by-move` の `Expect.Paint` を `Expect.Partial` にする。
- 末尾(`theme-change` の前)に:

```csharp
        new("type-char", Expect.Partial)
        {
            Arrange = (e, b) => MoveTo(e, b, CurLine, CurCol),
            Arranged = b => Caret(b, CurLine, CurCol),
            Act = (e, b) => e.ReplaceCharRange(At(b, CurLine, CurCol), 0, "x"),
            VerifyAfter = (e, b) => CaretAt(e, At(b, CurLine, CurCol) + 1),
        },
        new("type-enter", Expect.Partial)
        {
            Arrange = (e, b) => MoveTo(e, b, CurLine, CurCol),
            Arranged = b => Caret(b, CurLine, CurCol),
            Act = (e, b) => e.ReplaceCharRange(At(b, CurLine, CurCol), 0, "\r\n"),
            VerifyAfter = (e, b) => CaretAt(e, At(b, CurLine, CurCol) + 2),
        },
        new("wrap-type", Expect.Partial)
        {
            Configure = s =>
            {
                s.WrapColumnEnabled = true;
                s.WrapColumn = 20;
            },
            Arrange = (e, b) => MoveTo(e, b, CurLine, CurCol),
            Arranged = b => Caret(b, CurLine, CurCol),
            Act = (e, b) => e.ReplaceCharRange(At(b, CurLine, CurCol), 0, "xxxxxxxx"),
            VerifyAfter = (e, b) => CaretAt(e, At(b, CurLine, CurCol) + 8),
        },
        new("ime-update", Expect.Partial)
        {
            Arrange = (e, b) =>
            {
                MoveTo(e, b, ImeLine, ImeCol);
                e.__TestApplyComposition(ImeText, ImeText.Length, [.. Enumerable.Repeat(ImeAttribute.Input, ImeText.Length)], []);
            },
            Arranged = b => Caret(b, ImeLine, ImeCol),
            Composing = true,
            Act = (e, _) =>
                e.__TestApplyComposition(ImeText + "を", ImeText.Length + 1, [.. Enumerable.Repeat(ImeAttribute.Input, ImeText.Length + 1)], []),
            VerifyAfter = (e, _) => e.__TestImeText() == ImeText + "を" ? null : "未確定が更新されない",
        },
        new("cell-highlight-move", Expect.Partial)
        {
            Arrange = (e, b) =>
            {
                MoveTo(e, b, CurLine, CurCol);
                e.HighlightCharRange(At(b, 3, 2), 4);
            },
            Arranged = b => Caret(b, CurLine, CurCol),
            Act = (e, b) => e.HighlightCharRange(At(b, 4, 2), 4),
            VerifyAfter = (e, b) => CaretAt(e, At(b, CurLine, CurCol)),
        },
        new("scroll-down-1", Expect.Partial)
        {
            Arrange = (e, _) => e.TopLine = 5,
            Arranged = _ => (0, 0),
            ArrangedScroll = (5, 0),
            Act = (e, _) => e.TopLine = 6,
            VerifyAfter = (e, _) => e.TopLine == 6 ? null : "TopLine が 6 にならない",
        },
        new("scroll-up-1", Expect.Partial)
        {
            Arrange = (e, _) => e.TopLine = 6,
            Arranged = _ => (0, 0),
            ArrangedScroll = (6, 0),
            Act = (e, _) => e.TopLine = 5,
            VerifyAfter = (e, _) => e.TopLine == 5 ? null : "TopLine が 5 にならない",
        },
        new("scroll-down-1-curline", Expect.Partial)
        {
            Configure = s => s.HighlightCurrentLine = true,
            Arrange = (e, b) =>
            {
                MoveTo(e, b, 8, 0);
                e.TopLine = 5;
            },
            Arranged = b => Caret(b, 8, 0),
            ArrangedScroll = (5, 0),
            Act = (e, _) => e.TopLine = 6,
            VerifyAfter = (e, b) => CaretAt(e, At(b, 8, 0)),
        },
        new("hscroll-small", Expect.Partial)
        {
            Arrange = (e, b) =>
            {
                MoveTo(e, b, LongLine, 0);
                e.ScrollX = 40;
            },
            Arranged = b => Caret(b, LongLine, 0),
            ArrangedScroll = (0, 40),
            Act = (e, _) => e.ScrollX = 70,
            VerifyAfter = (e, _) => e.ScrollX == 70 ? null : $"ScrollX が 70 にならない({e.ScrollX})",
        },
        new("page-down", Expect.Paint)
        {
            Arrange = (e, _) => e.TopLine = 5,
            Arranged = _ => (0, 0),
            ArrangedScroll = (5, 0),
            Act = (e, _) => e.TopLine = 45,
            VerifyAfter = (e, _) => e.TopLine == 45 ? null : "TopLine が 45 にならない",
        },
```

`PaintSnapshot.BuildBody()` の行数・行 7 の長さが上の前提(行 45 まである・行 7 で ScrollX = 70 が効く)を満たすことを確かめる。満たさなければ値を調整し、実施記録に書く。`ImeAttribute` の using(`kxEdit.Core.Editing`)はすでにある。

- [ ] **Step 4: 実行する**

```powershell
dotnet build kxEdit.sln -c Release
dotnet run --project tests/kxEdit.Editor.Smoke -c Release --no-build -- --paint-transition --expect-skip --out "$d\transition"
```

Expected: EXIT 0。`control-stale-scroll` が陽性対照として通り、`Partial` の遷移のクリップの和が全体より小さい。**NVDA のスピーチビューアーなど他の窓を重ねた状態でも 1 回実行し**、結果を実施記録に残す(フェーズ 3 の申し送り: 重ねた状態の対照)。

- [ ] **Step 5: `tools/README.md` を直す**

`--paint-transition` の節に 3 行足す: `Partial`(部分再描画であることまで見る)、`StaleScroll`(スクロール領域の陽性対照 = 画素が動き、露出した帯が古いことを撮れる)、子のスクロールバーの保留中の無効領域も確かめること。

- [ ] **Step 6: commit と仕様レビュー**

```powershell
git add tests/kxEdit.Editor.Smoke/PaintTransition.cs tools/README.md
git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -F <msg>   # test(smoke): --paint-transition に部分再描画とスクロールの遷移を足す
dotnet build kxEdit.sln -c Release
```

---

## Task 7: 変更後の計測

- [ ] **Step 1: Smoke**(Task 1 Step 1 と同じコマンドで `after-perf-$_.json`)
- [ ] **Step 2: harness**(Task 1 Step 3 と同じコマンドで `after\publish`・`after-m2-$_.csv`・`after-m4-$_.csv`)
- [ ] **Step 3: 判定**(設計書 §3.2・§14.3)
  - S3(打鍵)・S4(IME)・S6a(1 行スクロール)が、変更前の揺れ(3 回の最小〜最大)を超えて下がっていること。
  - S6b(1 ページ)・S7(全面)・S1・S2 は悪化していないこと(全面の経路は、差の計算の分だけ重くなりうる。増えていれば値を書く)。
  - harness の M-2(文字入力・PageDown)・M-4 は、揺れを超えた改善があれば主張し、揺れの中なら主張しない。
  - 改善が揺れの範囲に収まる項目は、PR に書いてユーザーに採否を判断してもらう。
  - `ViewportLayout.Build` を行で絞るか(設計書 §14.1)は、S3 の残りの内訳を dotnet-trace で見て判断する。絞らない場合は理由(数値)を実施記録に書く。
- [ ] **Step 4: 実施記録に追記して commit**(docs のみ)

---

## Task 8: 最終ブランチレビュー(2 パス)と品質ゲート

- [ ] **Step 1: 別エージェント 2 本**(memory の 3 行の規則を冒頭に入れる)
  - **コード品質パス**: 不変条件 1〜3 の守り方(コメントとテスト)、`FrameRowCache` の寿命、`InvalidateChangedRows` の分岐の網羅、テストの前提(CLAUDE.md §4-B)。ミューテーション検証は行わない(§0.4)。代わりに Task 4 Step 8・Task 5 Step 9 の改変結果を渡し、網の穴を探させる。
  - **脆弱性パス**: P/Invoke(RECT の受け渡し・戻り値の扱い)、UI スレッド外からの呼び出しがないこと(UIA の Invoke 経路で `InvalidateChangedRows` や `ScrollWindowEx` が呼ばれないか)、ハンドル破棄後の呼び出し、キャッシュが握る本文の寿命(描画されないタブ)。
- [ ] **Step 2: 指摘を ①②③ で扱い、fixup commit で直す**(元の commit は書き換えない)。実施記録に書く。
- [ ] **Step 3: 品質ゲート**

```powershell
pwsh -File tools\sr-regression.ps1
pwsh -File tools\pre-merge-check.ps1
```

Expected: どちらも EXIT 0。

---

## Task 9: L5(実機 SR 検証と目視)

**方法**(フェーズ 3 の L5 と同じ。スクリプトは scratchpad に置き捨て。memory の「L5 を自動で回すときの 4 つの罠」を先に読む)
- `%APPDATA%\kxEdit\settings.json` を退避してから設定を変え、終わったらハッシュが一致することを確かめて戻す。
- Task 7 の publish を起動し(Win+R など実操作で起動する)、300 行のファイルを Ctrl+O で開く。kxEdit はスピーチビューアーと重ならない位置に置く。
- 操作のたびに 2 枚撮って比べる: X = 描画を起こさずに `CopyFromScreen` で撮った絵、Y = `RedrawWindow` で全面を描き直させてから撮った絵。システムキャレットの矩形(±3px)は除く。操作の前の絵と Y の差(操作で絵が変わったか)も数える(陽性対照)。
- 実解像度の PNG で判定する(縮小スクリーンショットで判定しない)。

**組み合わせ**(設計書 §14.3。全 32 通りは多いので、描画の要素を最も多く出す組と既定の組にする。これで足りない場合はユーザーと相談する)

| 設定 | テーマ | 折り返し | 行番号・現在行強調・空白表示 |
|---|---|---|---|
| A | 標準 | OFF | すべて OFF(既定) |
| B | 標準 | OFF | すべて ON |
| C | 標準 | ON(80) | すべて ON |
| D | 黒地 | OFF | すべて ON |
| E | 黒地 | ON(80) | すべて ON |

**操作**(各設定で): 打鍵 5 文字・Enter・BackSpace、選択のドラッグ(SendInput のマウス)、ホイール 1 ノッチ ×3 と逆方向 ×3、スクロールバーのドラッグ、PageDown / PageUp、水平スクロール(折り返し OFF の長い行で End)、CSV ファイルでのセル強調の移動と F2(セル編集 TextBox が重なった状態でホイール)。

**判定**: X と Y の差がすべて 0 画素。差があれば PNG を保存して原因を調べる(systematic-debugging)。

**NVDA**: キャレット移動・打鍵・スクロール後の読み上げが操作どおりであること(スピーチビューアーから切り出して読む)。

**ユーザーに実機確認を依頼するもの**(自動では確かめられない)
- 実 IME での変換中の打鍵・スクロール・変換確定(未確定表示の太字のディセンダが帯を越えて残らないこと)。
- NVDA の視覚的ハイライトの表示位置(利用者の設定で OFF の場合は、その旨を記録)。
- 結果を実施記録に書く。ユーザーの判断で省く場合は、その旨と日付を書く。

---

## Task 10: PR

- [ ] **Step 1: 実施記録を整え、設計書 §14 の末尾に「§14.4 実施記録」を追記する**(成果物・完了条件の結果・本節からの精密化(本計画 §0.2 の表)・観測できる差(§0.6)・申し送り)。PR 番号は PR を作った後に記入する commit を積む(フェーズ 11 と同じ)。
- [ ] **Step 2: push と PR 作成**(日本語。目的・変更前後の計測値・意図的な挙動差(なし)と観測できる差・L5 の結果・レビュー経緯・申し送り。末尾に `🤖 Generated with [Claude Code](https://claude.com/claude-code)`)
- [ ] **Step 3: メモリーの `perf-audit-2026-09-24-findings.md` を更新する**(フェーズ 9 の状態・次はフェーズ 12)

---

## 実施記録

### Task 1: 変更前の計測(2026-09-27・src は main `1394ed2` と同一)

**環境**: 画面 1024×767・96 DPI。**NVDA 起動中**(pid 6184)。Smoke の窓のクライアントは 884×661(LineHeightPx 16・可視 ≈41 行)。

**Smoke `--perf --scenario S1,S2,S3,S4,S6,S7`**(Release・3 回とも EXIT 0。3 回の中央値の min / 中央 / max・ms/操作)

| ID | ja10k | en10k | paints_per_op |
|---|---|---|---|
| S1 →← | 0.08 / 0.08 / 0.09 | 0.50 / 0.50 / 0.55 | 0 |
| S2 ↓↑ | 0.10 / 0.10 / 0.10 | 0.54 / 0.54 / 0.58 | 0 |
| S3a 挿入 | 7.12 / 7.22 / 7.22 | 6.81 / 6.84 / 6.90 | 1 |
| S3b BackSpace | 7.10 / 7.20 / 7.24 | 6.78 / 6.79 / 6.92 | 1 |
| S4 IME | 6.90 / 6.94 / 6.97 | 5.84 / 6.35 / 6.37 | 1 |
| S6a 1 行スクロール | 6.66 / 6.70 / 6.74 | 5.85 / 6.20 / 6.30 | 1 |
| S6b 1 ページ | 6.67 / 6.70 / 6.73 | 6.22 / 6.33 / 6.34 | 1 |
| S7 全面再描画 | 6.50 / 6.52 / 6.54 | 5.37 / 5.80 / 5.90 | 1 |

**`--paint-transition --expect-skip`**: EXIT 0(14 遷移すべて期待どおり)。

**perf-harness M-2・M-4**(publish は ReadyToRun・3 回とも `status=completed`・NVDA 起動中。CPU ms/回・1 / 2 / 3 回目)

| 操作 | ja10k | en10k |
|---|---|---|
| →←の交互 | 11.88 / 10.47 / 8.44 | 11.41 / 11.56 / 10.62 |
| ↓↑の交互 | 10.00 / 9.84 / 7.97 | 10.00 / 8.59 / 9.06 |
| Shift+→ | 16.02 / 19.14 / 16.41 | 14.45 / 15.62 / 16.80 |
| 文字入力x | 16.67 / 17.45 / 15.89 | 13.54 / 14.06 / 15.36 |
| BackSpace | 15.10 / 13.80 / 15.62 | 14.58 / 12.50 / 14.06 |
| PageDown/PageUpの交互 | 17.50 / 20.94 / 20.62 | 18.12 / 18.75 / 17.81 |
| 基準:全面の再描画 | 6.25 / 5.62 / 5.94 | 5.16 / 6.09 / 7.19 |
| 基準:文書先頭での← | 6.88 / 6.72 / 7.19 | 5.94 / 5.16 / 7.03 |
| 基準:Shiftの単押し | 2.66 / 2.19 / 3.12 | 2.19 / 0.78 / 2.19 |
| M-4 ホイール下 | 14.06 / 11.98 / 13.54 | — |
| M-4 ホイール上 | 11.46 / 9.64 / 11.72 | — |

`backups` にファイルはなく、harness の中止条件には当たらなかった。

### Task 2〜6(実装・レビュー)

| Task | commit | レビュー |
|---|---|---|
| 2 行の記述子と RowsTouching | 44ce4d0 | 承認。計画のテスト期待 1 件(`Shift_compares_new_row_i_with_old_row_i_plus_shift`)が誤りで、実装者が文書どおりの意味(新旧とも行がない位置は背景同士 = 汚れにしない)に直した。露出した帯は Task 5 の `ExposedStrip` が別に無効化する |
| 3 クリップ対応の描画 | 482f257 | 承認(前倒しのコード品質レビューを兼ねる)。`--paint-snapshot` は 16 枚すべて一致。水平スクロールのテストは、画面外の Form では hscroll が出ないので `Show()` した |
| 4 差分の無効化 | c997892 | 承認。既存テストの修正は 0 件。オラクルへの故障注入 2 種はどちらも 8 seed すべてで FAIL。Task 3 の申し送り(コメント 2 件・`FrameRowCacheTests`)を同梱 |
| 5 ScrollWindowEx | 285ff20・fixup a2a8100・fixup 0476b55 | 1 回目は Needs fixes(横の画素移動に画素レベルのテストがない = dx の符号を反転しても全テストが通った)→ a2a8100 で合成比較のテストにして解消。**Task 7 の計測で S6b(1 ページ)の退行を見つけた**: 可視行数ちょうど(41 行・656 < 661 px)の移動が画素移動の経路に入り、全行の記述子を作ってほぼ全面を描いていた。設計書 §14.2 どおり「可視行数未満」に直した(0476b55)。故障注入: 露出帯の無効化を消すとオラクル 8/8 seed が FAIL、枠の上端画素の規則を消すと 2 seed が FAIL。`EnsureVisibleCharRange` の finally の無効化は、消しても画素の違いが出ない冗長な防御と判明(戻した状態は、ずらした記録と記述子が一致するため)。不変条件 2 のために残す |
| 6 `--paint-transition` | 50f1989 | 承認。25 遷移すべて期待どおり。他の最前面の窓を重ねた状態でも同じ結果(フェーズ 3 の申し送りの対照) |

### Task 7: 変更後の計測(2026-09-27・src は 0476b55 と同一)

**環境**: Task 1 と同じ。NVDA 起動中(pid 6184)。

**Smoke `--perf`**(3 回とも EXIT 0。min / 中央 / max。括弧内は変更前の中央値)

| ID | ja10k | en10k | paints_per_op |
|---|---|---|---|
| S1 →← | 0.08 / 0.08 / 0.08(0.08) | 0.55 / 0.57 / 0.58(0.50) | 0 |
| S2 ↓↑ | 0.10 / 0.10 / 0.10(0.10) | 0.58 / 0.59 / 0.62(0.54) | 0 |
| S3a 挿入 | 1.46 / 1.47 / 1.52(7.22) | 2.56 / 2.57 / 2.61(6.84) | 1 |
| S3b BackSpace | 1.45 / 1.47 / 1.51(7.20) | 2.57 / 2.58 / 2.59(6.79) | 1 |
| S4 IME | 1.40 / 1.64 / 1.67(6.94) | 1.70 / 1.84 / 1.87(6.35) | 1 |
| S6a 1 行スクロール | 2.13 / 2.35 / 2.37(6.70) | 2.71 / 2.75 / 2.75(6.20) | 1 |
| S6b 1 ページ | 6.55 / 6.57 / 6.61(6.70) | 6.09 / 6.09 / 6.10(6.33) | 1 |
| S7 全面再描画 | 6.33 / 6.34 / 6.43(6.52) | 5.65 / 5.70 / 5.78(5.80) | 1 |

0476b55 の前(50f1989)の S6b は ja 7.37 / 7.57 / 7.62・en 6.58 / 7.05 / 7.14 だった(上の退行)。

**perf-harness M-2・M-4**(publish 0476b55・3 回とも `status=completed`。括弧内は変更前の範囲)

| 操作 | ja10k | en10k |
|---|---|---|
| →←の交互 | 10.47 / 11.41 / 12.34(8.44〜11.88) | 10.00 / 10.31 / 11.56(10.62〜11.56) |
| ↓↑の交互 | 10.31 / 8.28 / 10.94(7.97〜10.00) | 9.06 / 7.81 / 9.53(8.59〜10.00) |
| Shift+→ | 14.45 / 10.16 / 13.67(16.02〜19.14) | 13.67 / 13.67 / 11.33(14.45〜16.80) |
| 文字入力x | 14.06 / 13.28 / 12.76(15.89〜17.45) | 8.07 / 10.16 / 9.90(13.54〜15.36) |
| BackSpace | 7.55 / 11.98 / 12.24(13.80〜15.62) | 12.50 / 11.46 / 10.94(12.50〜14.58) |
| PageDown/PageUpの交互 | 17.50 / 14.69 / 16.88(17.50〜20.94) | 15.62 / 14.06 / 12.50(17.81〜18.75) |
| 基準:全面の再描画 | 5.16 / 6.72 / 6.09(5.62〜6.25) | 5.16 / 5.47 / 6.09(5.16〜7.19) |
| 基準:文書先頭での← | 9.22 / 5.78 / 7.19(6.72〜7.19) | 7.66 / 7.19 / 6.41(5.16〜7.03) |
| 基準:Shiftの単押し | 2.03 / 2.34 / 2.81(2.19〜3.12) | 1.25 / 1.72 / 1.41(0.78〜2.19) |
| M-4 ホイール下 | 10.42 / 11.46 / 10.42(11.98〜14.06) | — |
| M-4 ホイール上 | 7.55 / 8.59 / 7.29(9.64〜11.72) | — |

**判定**(設計書 §3.2・§14.3)
- **S3・S4・S6a**: ja10k・en10k とも、変更前の揺れを大きく超えて下がった(打鍵 ja 7.22 → 1.47 ms、IME 6.94 → 1.64 ms、1 行スクロール 6.70 → 2.35 ms)。完了条件を満たす。
- **S6b・S7**: 悪化なし(変更後の値は変更前の中央値以下)。
- **S1・S2 の en10k**: 中央値で +0.05〜0.07 ms。変更前の最大値をわずかに超える。`CaptureFrameInputs` は変わっていない(未確定がないときの `ComputeOrigin` は即 return)ので、日内の揺れとみるが、改善とも悪化とも主張しない。ja10k は変化なし。
- **harness**: 揺れを超えて下がったのは、文字入力x(ja・en)・Shift+→(ja・en)・M-4 のホイール(上下とも)・PageDown/PageUp(en)。BackSpace は 3 回中 2〜3 回が変更前の最小値を下回る。→← と ↓↑ は描画を省く対象(フェーズ 3)で、本フェーズの影響外。揺れの範囲内。
- **`ViewportLayout.Build` を行で絞るか**(設計書 §14.1): 絞らない。S3 の残り(ja 1.47 ms)には、キャプチャ・可視行の構築・記述子・1 行の描画が含まれ、全面再描画(6.3 ms)に対して 8 割減を達成しているため。内訳の採取は申し送りにする。

### Task 8: 最終ブランチレビュー(2 パス)と品質ゲート(fixup 4cca497)

**コード品質パス**(Ready to merge: Yes・Critical / Important なし)
- 不変条件 2・3 を破る経路なし(状態の代入を全件 grep で確認)。帯の計算・`RowsTouching`・オラクルの強さも確認された。
- Minor 1〜3・6 と、Task 5 の申し送り(`TryPlanScroll` の summary)・脆弱性パスの Minor-V2 を ① 4cca497 で直した。
  - 古いテストのコメント(全面になるのは `CanScroll` が false だから)。
  - `EnsureVisibleCharRange` の finally の無効化は「今は冗長な防御(不変条件 2 のため)」と書き直した(等価であることをレビュアーも故障注入で確認)。
  - `ImeController.Draw(g, origin)` が `_ime` を読むことを remarks に足した。
  - `RowsTouching` の全行の近道を、最終行が途中で切れる通常の全面描画でも効くようにした(結果は同一。テスト 1 件追加)。
  - `TryPlanScroll` の `fullRows` の割り算を `Math.Max(1, now.Metrics.LineHeightPx)` で防御した。
- Minor 4(テスト用ヘルパーの散らばり)・5(`EditorControl.Paint.cs` の肥大)は ② 申し送り。
- 先送りした Minor はすべて「マージ前に直す必要なし」と仕分けられた。

**脆弱性パス**(Ready to merge: Yes・Critical / Important なし)
- 確かめられたこと: P/Invoke の宣言と戻り値(ERROR = 0 だけを失敗)、flags 0 で再入なし、RPC スレッドから到達しないこと(UIA は `Invoke` / `BeginInvoke` 経由)、ハンドルの寿命、解放済みフォントを使わないこと、矩形の算術、テストフックが internal であること。
- Minor-V1(`RowPaintKey.Text` が可視行の本文を複製して最大 2 件保持する。長大行で増幅): ② 申し送り(F-1 = 長大行と同じ領域で扱う)。
- Minor-V2(0 除算の防御): ① 4cca497。

**品質ゲート**(4cca497 時点)
- `tools/pre-merge-check.ps1`: EXIT 0。
- `tools/sr-regression.ps1`: 全通過(EXIT 0)。

### Task 9: L5(2026-09-27・自動確認・publish 26685e6)

**環境**: NVDA 起動中(スピーチビューアーは (78,78)-(578,578))。kxEdit はスピーチビューアーと重ならない (580,0) に 444×719 で置いた(最初の試運転では x=550 に置いてスピーチビューアーと 28px 重なり、撮った絵がスピーチビューアーを写していたので、やり直した。操作のたびに撮影点の最前面の窓のクラスを記録し、kxEdit であることを確かめた)。途中で Windows の全画面の通知(「PC のセットアップを完了しましょう」)が前面を取ったので、ユーザーに閉じてもらった。

**方法**(スクリプトは scratchpad に置き捨て)
- `%APPDATA%\kxEdit` を丸ごと退避してから設定を書き換え、最後に戻してハッシュが一致することを確かめた。
- 操作は SendInput(キー・マウスのドラッグ・ホイール・スクロールバーのドラッグと矢印のクリック)。
- 操作のたびに 2 枚撮って比べた。X = 描画を起こさずに `CopyFromScreen` で撮った絵、Y = `RedrawWindow`(全面・子を含む・即時)の後に撮った絵。システムキャレットの矩形(±3px)は除いた。
- 陽性対照として、直前の Y と今の Y の差(操作で絵が変わった画素数)も数えた。

**結果**: 6 設定・118 操作のすべてで、X と Y の差は **0 画素**(古い絵は残らない)。

| 設定 | 内容 | 操作 | 操作による絵の変化 |
|---|---|---|---|
| A | 標準・折り返し OFF・行番号 / 現在行強調 / 空白表示 OFF | 24 | 16〜40,180 画素 |
| B | 標準・折り返し OFF・3 つとも ON | 24 | 1,309〜42,183 |
| C | 標準・折り返し ON(80)・3 つとも ON | 19 | 1,309〜38,472 |
| D | 黒地・折り返し OFF・3 つとも ON | 24 | 1,309〜44,018 |
| E | 黒地・折り返し ON(80)・3 つとも ON | 19 | 1,309〜43,521 |
| F | CSV モード(セル強調の移動・F2 でセル編集中のホイール・Esc・ホイール) | 8 | 1,539〜11,531 |

- 操作: ↓×3・5 文字の打鍵・Enter・BackSpace・Shift+↓×2・選択の解除・マウスのドラッグ選択・クリック・ホイール下×3 / 上×3・縦スクロールバーのドラッグ・PageDown / PageUp・下端での ↓(1 行スクロール)・↑×45。折り返し OFF では、長い行で End / Home と、水平スクロールバーの矢印のクリック(右×2・左。小さな水平スクロール = 画素を移す経路)も行った。
- A の PageDown だけは絵の変化が 0 だった。直前の操作で、キャレットの移動先がもともと可視域の中にあり、スクロールが起きなかったため(強調 OFF のキャレット移動は描き直さない = フェーズ 3)。
- CSV のセル編集中(兄弟の TextBox が重なる)のホイールでも差は 0。`CanScroll` が false になり全面に落ちる経路。

**自動では確かめていないこと(ユーザーの実機確認に回す)**
- 実 IME での変換中の打鍵・スクロール・確定(未確定表示の太字のディセンダが、無効化する 2 行の帯を越えて残らないか。設計書 §14.1 の例外 2)。自動の確認は `__TestApplyComposition` を使うオラクルと `--paint-transition` の `ime-update` だけ。
- NVDA の発声とハイライト矩形。本フェーズは UIA の経路を変えていない(`sr-regression.ps1` は全通過)。スピーチビューアーは昇格した NVDA の窓で、WM_GETTEXT で読めないので、自動では採っていない。