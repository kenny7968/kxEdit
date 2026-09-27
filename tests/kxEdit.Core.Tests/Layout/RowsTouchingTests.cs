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

    private static int[] Lines(IReadOnlyList<VisualRow> rows) =>
        [.. rows.Select(r => r.LogicalLine)];

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
