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
        var keys = Keys(
            s,
            currentLine: 1,
            sel: new SelectionRange(2, 8),
            cell: new SelectionRange(13, 15)
        );
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
        // 新しい 0..3 は古い 2..5 と同じ。新しい 4・5 は old 側も now 側も行がない
        // (どちらもドキュメント末尾の外)= DirtyBands の規約により両側とも背景なので汚れない
        // (スクロールで露出した帯を invalidate するのは ScrollWindowEx 側の責務で、
        // DirtyBands は「行の中身が変わったか」だけを見る)。
        Assert.Empty(FrameDiff.DirtyBands(old, now, 2, Lh, 60));
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
