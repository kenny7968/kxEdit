using kxEdit.Core.Buffers;
using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Layout;

/// <summary>
/// 長い行は、横の窓にかかる文字だけを本文・空白のグリフにする(設計書 2026-10-06 §3.4)。
/// 短い行の op 列は窓によらず変わらない。<see cref="GdiLikeMetrics"/>: ASCII 1px・それ以外 2px。
/// </summary>
public class FrameBuilderLongRowTests
{
    private static readonly ICharMetrics G = new GdiLikeMetrics();
    private static readonly PaintColor Fore = new(0x000000);
    private static readonly PaintColor SelFore = new(0xFF00FF);
    private static readonly PaintColor SelBack = new(0xADD8E6);
    private static readonly PaintColor Glyph = new(0xCCCCCC);

    private static ViewportStyle Style(PaintColor? selectionFore = null) =>
        new(
            Foreground: Fore,
            Background: new PaintColor(0xFFFFFF),
            CurrentLineBack: new PaintColor(0x88FF88),
            SelectionBack: SelBack,
            SelectionFore: selectionFore,
            LineNumberFore: new PaintColor(0x777777),
            HighlightOutline: new PaintColor(0xFF8800),
            WhitespaceGlyph: Glyph
        );

    private static Frame Build(
        string text,
        int? viewLeftPx = null,
        int viewWidthPx = 20,
        SelectionRange? selection = null,
        PaintColor? selectionFore = null,
        bool showWhitespace = false,
        int lineNumberMarginPx = 0
    )
    {
        var buf = TextBuffer.FromString(text);
        var rows = ViewportLayout.Build(
            buf.Current,
            topLine: 0,
            topSegment: 0,
            heightPx: 100,
            wrapColumns: 0,
            G
        );
        return viewLeftPx is int left
            ? FrameBuilder.Build(
                buf.Current,
                rows,
                clientWidth: 200,
                clientHeight: 100,
                lineNumberMarginPx: lineNumberMarginPx,
                currentLineLogical: -1,
                selection: selection,
                cellHighlight: null,
                showWhitespace: showWhitespace,
                Style(selectionFore),
                G,
                viewLeftPx: left,
                viewWidthPx: viewWidthPx
            )
            : FrameBuilder.Build(
                buf.Current,
                rows,
                clientWidth: 200,
                clientHeight: 100,
                lineNumberMarginPx: lineNumberMarginPx,
                currentLineLogical: -1,
                selection: selection,
                cellHighlight: null,
                showWhitespace: showWhitespace,
                Style(selectionFore),
                G
            );
    }

    /// <summary>本文の DrawText(行番号でも空白のグリフでもないもの)。</summary>
    private static List<PaintOp> Body(Frame f) =>
        f
            .Ops.Where(op =>
                op.Kind == PaintOpKind.DrawText
                && op.Fore != Glyph
                && op.Fore != new PaintColor(0x777777)
            )
            .ToList();

    [Fact]
    public void Short_row_ops_do_not_depend_on_the_window()
    {
        string row = string.Concat(Enumerable.Repeat("あ a", 100));
        var withoutWindow = Build(row, showWhitespace: true);
        var withWindow = Build(row, viewLeftPx: 37, viewWidthPx: 50, showWhitespace: true);
        Assert.Equal(withoutWindow.Ops, withWindow.Ops);
    }

    [Fact]
    public void Long_row_emits_only_the_characters_in_the_window()
    {
        string row = new('あ', 20_000);
        var body = Body(Build(row, viewLeftPx: 1001, viewWidthPx: 20));
        // 窓 [1001, 1021) → 文字 500([1000,1002))から、開始 X が 1021 未満の最後(文字 510)の次の 511 まで。
        var op = Assert.Single(body);
        Assert.Equal(new string('あ', 12), op.Text);
        Assert.Equal(1000, op.X);
        Assert.Equal(24, op.Width);
    }

    [Fact]
    public void Long_row_window_is_relative_to_the_body_after_the_line_number_margin()
    {
        string row = new('あ', 20_000);
        var body = Body(Build(row, viewLeftPx: 1001, viewWidthPx: 20, lineNumberMarginPx: 30));
        // 行頭基準の窓は [971, 991) → 文字 485([970,972))から。X = 30 + 970。
        Assert.Equal(1000, body[0].X);
        Assert.StartsWith("あ", body[0].Text);
    }

    [Fact]
    public void Long_row_without_a_window_emits_the_whole_row()
    {
        string row = new('あ', 20_000);
        var body = Body(Build(row));
        Assert.Equal(row, string.Concat(body.Select(op => op.Text)));
        Assert.Equal(0, body[0].X);
    }

    [Fact]
    public void Long_row_right_of_the_window_emits_no_body_text()
    {
        string row = new('あ', 20_000);
        Assert.Empty(Body(Build(row, viewLeftPx: 50_000, viewWidthPx: 20)));
    }

    [Fact]
    public void Long_row_selection_fore_splits_only_inside_the_window()
    {
        string row = new('あ', 20_000);
        var f = Build(
            row,
            viewLeftPx: 1001,
            viewWidthPx: 20,
            selection: new SelectionRange(505, 508),
            selectionFore: SelFore
        );
        var body = Body(f);
        // prefix [500,505)・選択 [505,508)・suffix [508,512)。prefix と suffix は窓の中だけ。
        Assert.Equal(3, body.Count);
        Assert.Equal(
            (new string('あ', 5), 1000, 10, Fore),
            (body[0].Text, body[0].X, body[0].Width, body[0].Fore)
        );
        Assert.Equal(
            (new string('あ', 3), 1010, 6, SelFore),
            (body[1].Text, body[1].X, body[1].Width, body[1].Fore)
        );
        Assert.Equal(
            (new string('あ', 4), 1016, 8, Fore),
            (body[2].Text, body[2].X, body[2].Width, body[2].Fore)
        );
        // 選択矩形(工程 3)も足し算の座標。
        var rect = Assert.Single(
            f.Ops,
            op => op.Kind == PaintOpKind.FillRect && op.Back == SelBack
        );
        Assert.Equal((1010, 6), (rect.X, rect.Width));
    }

    [Fact]
    public void Long_row_selection_outside_the_window_is_drawn_as_unselected()
    {
        string row = new('あ', 20_000);
        var body = Body(
            Build(
                row,
                viewLeftPx: 1001,
                viewWidthPx: 20,
                selection: new SelectionRange(100, 200),
                selectionFore: SelFore
            )
        );
        var op = Assert.Single(body);
        Assert.Equal(Fore, op.Fore);
        Assert.Equal(new string('あ', 12), op.Text);
    }

    [Fact]
    public void Long_row_whitespace_glyphs_only_inside_the_window_at_additive_x()
    {
        // "あ \t" の繰り返し(1 単位 = あ 2px + 空白 1px + タブ 1px = 4px・3 文字)。20,001 字。
        string row = string.Concat(Enumerable.Repeat("あ \t", 6_667));
        Assert.True(PixelMapper.IsLongRow(row));
        var f = Build(row, viewLeftPx: 1001, viewWidthPx: 20, showWhitespace: true);
        var glyphs = f
            .Ops.Where(op => op.Kind == PaintOpKind.DrawText && op.Fore == Glyph)
            .ToList();

        var slice = PixelMapper.SliceForWindow(row, 1001, 1021, G);
        var expectedX = Enumerable
            .Range(slice.Start, slice.End - slice.Start)
            .Where(i => row[i] == ' ' || row[i] == '\t')
            .Select(i => PixelMapper.OffsetToPx(row, i, G))
            .ToList();
        Assert.NotEmpty(expectedX);
        Assert.Equal(expectedX, glyphs.Select(op => op.X).ToList());
        Assert.Contains(glyphs, op => op.Text == "→"); // タブも出る
    }
}
