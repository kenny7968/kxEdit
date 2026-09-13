// FrameBuilderSelectionForeTests.cs
// 2026-09-14 設計書: 黒地テーマで選択中テキストが読めない不具合の網。
// ViewportStyle.SelectionFore が非 null のとき、本文 DrawText を選択境界で分割することを固定する。
// 分割しない側(標準テーマ=SelectionFore が null)の不変性も同じ場所で対にして固定する。
using kxEdit.Core.Buffers;
using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Layout;

public class FrameBuilderSelectionForeTests
{
    private static MonoCharMetrics M => new(halfWidthPx: 1, lineHeightPx: 10);

    private static readonly PaintColor Fore = new(0x000000);
    private static readonly PaintColor SelFore = new(0xFF00FF);

    private static ViewportStyle Style(PaintColor? selectionFore) =>
        new(
            Foreground: Fore,
            Background: new PaintColor(0xFFFFFF),
            CurrentLineBack: new PaintColor(0x88FF88),
            SelectionBack: new PaintColor(0xADD8E6),
            SelectionFore: selectionFore,
            LineNumberFore: new PaintColor(0x777777),
            HighlightOutline: new PaintColor(0xFF8800),
            WhitespaceGlyph: new PaintColor(0xCCCCCC)
        );

    /// <summary>本文 DrawText だけを抽出する(行番号・空白グリフを除くため色と Text で絞る)。</summary>
    private static List<PaintOp> BodyText(Frame frame) =>
        frame
            .Ops.Where(op =>
                op.Kind == PaintOpKind.DrawText && (op.Fore == Fore || op.Fore == SelFore)
            )
            .ToList();

    private static Frame Build(
        string text,
        SelectionRange? selection,
        PaintColor? selectionFore,
        int wrapCols = 0
    )
    {
        var buf = TextBuffer.FromString(text);
        var rows = ViewportLayout.Build(
            buf.Current,
            topLine: 0,
            topSegment: 0,
            heightPx: 1000,
            wrapColumns: wrapCols,
            M
        );
        return FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 200,
            clientHeight: 100,
            lineNumberMarginPx: 0,
            currentLineLogical: -1,
            selection: selection,
            cellHighlight: null,
            showWhitespace: false,
            Style(selectionFore),
            M
        );
    }

    // --- 分割する側 ---

    // fixture "abcdef" + 選択 [2,4) は prefix "ab" と suffix "ef" の両方を持つ
    // (全選択・行頭選択・行末選択のいずれとも区別できる=CLAUDE.md §4-B)。
    [Fact]
    public void Partial_selection_splits_body_text_into_three_runs()
    {
        var frame = Build("abcdef", new SelectionRange(2, 4), SelFore);

        var ops = BodyText(frame);

        Assert.Collection(
            ops,
            op => AssertRun(op, "ab", x: 0, w: 2, Fore),
            op => AssertRun(op, "cd", x: 2, w: 2, SelFore),
            op => AssertRun(op, "ef", x: 4, w: 2, Fore)
        );
    }

    // 行頭からの選択では prefix の空 op を出さない(空 op は無害だが、意図せず増えた
    // op が「分割数」を主張するテストを素通りさせるため、個数まで固定する)。
    [Fact]
    public void Selection_from_row_start_emits_two_runs()
    {
        var frame = Build("abcdef", new SelectionRange(0, 2), SelFore);

        Assert.Collection(
            BodyText(frame),
            op => AssertRun(op, "ab", x: 0, w: 2, SelFore),
            op => AssertRun(op, "cdef", x: 2, w: 4, Fore)
        );
    }

    [Fact]
    public void Selection_to_row_end_emits_two_runs()
    {
        var frame = Build("abcdef", new SelectionRange(4, 6), SelFore);

        Assert.Collection(
            BodyText(frame),
            op => AssertRun(op, "abcd", x: 0, w: 4, Fore),
            op => AssertRun(op, "ef", x: 4, w: 2, SelFore)
        );
    }

    // 行まるごとの選択は 1 op だが、色が SelectionFore であることが「分割しない」場合との差。
    [Fact]
    public void Whole_row_selection_emits_single_run_in_selection_color()
    {
        var frame = Build("abcdef", new SelectionRange(0, 6), SelFore);

        Assert.Collection(BodyText(frame), op => AssertRun(op, "abcdef", x: 0, w: 6, SelFore));
    }

    // 折り返しで複数の視覚行にまたがる選択は、視覚行ごとに分割される。
    // 視覚行 0 は "ab"(y=0)・視覚行 1 は "cd"(y=10)。
    [Fact]
    public void Selection_spanning_wrapped_rows_splits_each_row()
    {
        var frame = Build("abcd", new SelectionRange(1, 3), SelFore, wrapCols: 2);

        var ops = BodyText(frame);

        Assert.Collection(
            ops,
            op => AssertRun(op, "a", x: 0, w: 1, Fore, y: 0),
            op => AssertRun(op, "b", x: 1, w: 1, SelFore, y: 0),
            op => AssertRun(op, "c", x: 0, w: 1, SelFore, y: 10),
            op => AssertRun(op, "d", x: 1, w: 1, Fore, y: 10)
        );
    }

    // 論理行をまたぐ選択。改行文字ぶん char オフセットが飛ぶので、行内オフセットへの
    // 変換(SegmentStartChar の減算)を誤ると 2 行目の分割位置がずれる。
    [Fact]
    public void Selection_spanning_logical_lines_splits_each_row()
    {
        var frame = Build("ab\ncd", new SelectionRange(1, 4), SelFore);

        Assert.Collection(
            BodyText(frame),
            op => AssertRun(op, "a", x: 0, w: 1, Fore, y: 0),
            op => AssertRun(op, "b", x: 1, w: 1, SelFore, y: 0),
            op => AssertRun(op, "c", x: 0, w: 1, SelFore, y: 10),
            op => AssertRun(op, "d", x: 1, w: 1, Fore, y: 10)
        );
    }

    // 選択と交差しない行は分割しない(選択が存在する状態で、別の行が巻き込まれないこと)。
    [Fact]
    public void Row_outside_selection_is_not_split()
    {
        var frame = Build("ab\ncd", new SelectionRange(0, 2), SelFore);

        Assert.Collection(
            BodyText(frame),
            op => AssertRun(op, "ab", x: 0, w: 2, SelFore, y: 0),
            op => AssertRun(op, "cd", x: 0, w: 2, Fore, y: 10)
        );
    }

    // 空選択(Start == End)は分割しない。既存の選択矩形のガード(sel.Start < sel.End)と揃える。
    [Fact]
    public void Empty_selection_does_not_split()
    {
        var frame = Build("abcdef", new SelectionRange(3, 3), SelFore);

        Assert.Collection(BodyText(frame), op => AssertRun(op, "abcdef", x: 0, w: 6, Fore));
    }

    // サロゲートペアの途中に落ちた選択でも、文字が欠落も重複もしないこと。
    // OffsetToPx は pair 先頭へ前方スナップするので、文字の切り出しも同じ位置で
    // スナップしないと x と文字がずれるか pair が割れる。
    [Theory]
    [InlineData(1, 2)] // pair の途中で始まり途中で終わる
    [InlineData(0, 2)] // pair の途中で終わる
    [InlineData(2, 4)] // pair の途中で始まる
    public void Selection_inside_surrogate_pair_preserves_all_text(int start, int end)
    {
        // "a" + U+1F600 (サロゲートペア=2 code unit) + "b" = 4 code unit
        var frame = Build("a😀b", new SelectionRange(start, end), SelFore);

        string joined = string.Concat(BodyText(frame).Select(op => op.Text));
        Assert.Equal("a😀b", joined);
    }

    // --- 分割しない側(標準テーマ相当)---

    // no-change 網。既定状態(選択なし)と区別するため、選択が存在する状態から検証する
    // (CLAUDE.md §4-B)。SelectionFore が null なら本文 op は 1 本のまま=描画不変。
    [Fact]
    public void Null_selection_fore_keeps_single_body_run_even_when_selected()
    {
        var frame = Build("abcdef", new SelectionRange(2, 4), selectionFore: null);

        Assert.Collection(BodyText(frame), op => AssertRun(op, "abcdef", x: 0, w: 6, Fore));

        // アンカー: 選択自体は確かに有効(矩形は塗られている)。これが無いと選択が
        // 効いていない状態でも緑になり、このテストは何も主張しなくなる。
        Assert.Contains(
            frame.Ops,
            op => op.Kind == PaintOpKind.FillRect && op.Back == new PaintColor(0xADD8E6)
        );
    }

    private static void AssertRun(PaintOp op, string text, int x, int w, PaintColor fore, int y = 0)
    {
        Assert.Equal(PaintOpKind.DrawText, op.Kind);
        Assert.Equal(text, op.Text);
        Assert.Equal(x, op.X);
        Assert.Equal(w, op.Width);
        Assert.Equal(y, op.Y);
        Assert.Equal(fore, op.Fore);
    }
}
