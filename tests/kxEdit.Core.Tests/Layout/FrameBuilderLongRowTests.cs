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
    public void Long_row_selection_crossing_the_left_edge_starts_at_the_window()
    {
        string row = new('あ', 20_000);
        var f = Build(
            row,
            viewLeftPx: 1001,
            viewWidthPx: 20,
            selection: new SelectionRange(495, 505),
            selectionFore: SelFore
        );
        var body = Body(f);
        // 窓は [500,512)。選択の前半 [495,500) は窓の外 = 選択色の run は窓の左端(文字 500)から。
        // 窓より左の prefix は op を出さない。
        Assert.Equal(2, body.Count);
        Assert.Equal(
            (new string('あ', 5), 1000, 10, SelFore),
            (body[0].Text, body[0].X, body[0].Width, body[0].Fore)
        );
        Assert.Equal(
            (new string('あ', 7), 1010, 14, Fore),
            (body[1].Text, body[1].X, body[1].Width, body[1].Fore)
        );
        // 選択矩形(工程 3)は選択の全体 [495,505) を足し算の座標で(一括計測なら 989 になる)。
        var rect = Assert.Single(
            f.Ops,
            op => op.Kind == PaintOpKind.FillRect && op.Back == SelBack
        );
        Assert.Equal((990, 20), (rect.X, rect.Width));
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

    // GDI(TextRenderer)はタブを幅 0 で測り・描く一方、足し算の座標はタブを空白幅で数える。長い行の本文を
    // 1 run = 1 DrawText で描くと、タブのたびに文字が座標より左へずれる(2026-10-07 L5 で検出)。
    // そこで長い行の本文はタブで区切り、タブを含まない区間ごとに足し算の X で出す。

    /// <summary>"あa\tい" の繰り返し(1 単位 = 2+1+1+2 = 6px・4 文字)。20,000 字の長い行。</summary>
    private static readonly string TabRow = string.Concat(Enumerable.Repeat("あa\tい", 5_000));

    /// <summary>[from, to) をタブで区切った区間ごとの期待 op(Text・X・幅・色)。X と幅は足し算の座標。</summary>
    private static IEnumerable<(string, int, int, PaintColor)> ExpectedTabSplit(
        string row,
        int from,
        int to,
        int bodyX,
        PaintColor color
    )
    {
        int i = from;
        while (i < to)
        {
            if (row[i] == '\t')
            {
                i++;
                continue;
            }
            int end = i;
            while (end < to && row[end] != '\t')
                end++;
            int x = PixelMapper.OffsetToPx(row, i, G);
            yield return (row[i..end], bodyX + x, PixelMapper.OffsetToPx(row, end, G) - x, color);
            i = end;
        }
    }

    private static List<(string, int, int, PaintColor)> Actual(List<PaintOp> body) =>
        body.Select(op => (op.Text!, op.X, op.Width, op.Fore)).ToList();

    [Fact]
    public void Long_row_body_is_split_at_tabs_and_each_segment_is_at_additive_x()
    {
        Assert.True(PixelMapper.IsLongRow(TabRow));
        // 行番号ぶん 30px。行頭基準の窓は [1001, 1031)。
        var body = Body(Build(TabRow, viewLeftPx: 1031, viewWidthPx: 30, lineNumberMarginPx: 30));

        var slice = PixelMapper.SliceForWindow(TabRow, 1001, 1031, G);
        Assert.True(slice.Start > 0);
        Assert.Contains('\t', TabRow[slice.Start..slice.End]);
        Assert.DoesNotContain(body, op => op.Text!.Contains('\t'));
        Assert.Equal(
            TabRow[slice.Start..slice.End].Replace("\t", ""),
            string.Concat(body.Select(op => op.Text))
        );
        Assert.Equal(
            ExpectedTabSplit(TabRow, slice.Start, slice.End, 30, Fore).ToList(),
            Actual(body)
        );
    }

    [Fact]
    public void Long_row_selection_fore_runs_are_split_at_tabs_and_keep_their_colors()
    {
        // 窓 [1001, 1031) は文字 667(い・[1000,1002))から。選択 [669, 673) = "a\tいあ" はタブをまたぐ。
        var slice = PixelMapper.SliceForWindow(TabRow, 1001, 1031, G);
        Assert.Equal(667, slice.Start);
        Assert.Equal('\t', TabRow[670]);
        var body = Body(
            Build(
                TabRow,
                viewLeftPx: 1001,
                viewWidthPx: 30,
                selection: new SelectionRange(669, 673),
                selectionFore: SelFore
            )
        );

        var expected = ExpectedTabSplit(TabRow, slice.Start, 669, 0, Fore)
            .Concat(ExpectedTabSplit(TabRow, 669, 673, 0, SelFore))
            .Concat(ExpectedTabSplit(TabRow, 673, slice.End, 0, Fore))
            .ToList();
        Assert.Equal(expected, Actual(body));
        // 選択部はタブで 2 つ("a" と "いあ")に分かれ、どちらも選択色。
        Assert.Equal(
            ["a", "いあ"],
            body.Where(op => op.Fore == SelFore).Select(op => op.Text!).ToArray()
        );
    }
}
