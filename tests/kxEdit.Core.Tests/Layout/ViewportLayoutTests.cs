using kxEdit.Core.Buffers;
using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Layout;

public class ViewportLayoutTests
{
    private static MonoCharMetrics M => new(halfWidthPx: 1, lineHeightPx: 10);

    [Fact]
    public void Wrap_off_multiple_logical_lines_from_topLine_zero()
    {
        // "a\nb\nc"(3 論理行・各 1 文字)を折り返し OFF・height=25(=2.5 行分)で列挙。
        // 判定は「追加してから y 加算」なので y=20 の時点で 3 個目を積み(下端 5px はみ出す)、
        // 次の反復で y=30>=25 のため break。→ 3 個(3 行目は下端でクリップされる想定=OnPaint 側で処理)。
        var buf = TextBuffer.FromString("a\nb\nc");
        var rows = ViewportLayout.Build(
            buf.Current,
            topLine: 0,
            topSegment: 0,
            heightPx: 25,
            wrapColumns: 0,
            M
        );

        Assert.Equal(3, rows.Count);
        Assert.Equal(
            new VisualRow(
                LogicalLine: 0,
                SegmentIndex: 0,
                SegmentStartChar: 0,
                SegmentLength: 1,
                YPx: 0
            ),
            rows[0]
        );
        Assert.Equal(
            new VisualRow(
                LogicalLine: 1,
                SegmentIndex: 0,
                SegmentStartChar: 2,
                SegmentLength: 1,
                YPx: 10
            ),
            rows[1]
        );
        Assert.Equal(
            new VisualRow(
                LogicalLine: 2,
                SegmentIndex: 0,
                SegmentStartChar: 4,
                SegmentLength: 1,
                YPx: 20
            ),
            rows[2]
        );
    }

    [Fact]
    public void TopLine_beyond_line_count_yields_empty_list()
    {
        var buf = TextBuffer.FromString("a\nb\nc"); // LineCount=3
        var rows = ViewportLayout.Build(
            buf.Current,
            topLine: 3,
            topSegment: 0,
            heightPx: 100,
            wrapColumns: 0,
            M
        );
        Assert.Empty(rows);
    }

    [Fact]
    public void Empty_document_yields_single_empty_visual_row()
    {
        // 空文書=LineCount 1・CharLength 0。1 個の空視覚行(EOF キャレット用)を返す。
        var buf = TextBuffer.FromString("");
        var rows = ViewportLayout.Build(
            buf.Current,
            topLine: 0,
            topSegment: 0,
            heightPx: 100,
            wrapColumns: 0,
            M
        );

        Assert.Single(rows);
        Assert.Equal(
            new VisualRow(
                LogicalLine: 0,
                SegmentIndex: 0,
                SegmentStartChar: 0,
                SegmentLength: 0,
                YPx: 0
            ),
            rows[0]
        );
    }

    [Fact]
    public void Wrap_on_splits_single_logical_line_into_segments()
    {
        // wrapColumns=3・halfWidthPx=1 → maxWidthPx=3。"abcdef" → [(0,3),(3,3)]
        var buf = TextBuffer.FromString("abcdef");
        var rows = ViewportLayout.Build(
            buf.Current,
            topLine: 0,
            topSegment: 0,
            heightPx: 100,
            wrapColumns: 3,
            M
        );

        Assert.Equal(2, rows.Count);
        Assert.Equal(
            new VisualRow(
                LogicalLine: 0,
                SegmentIndex: 0,
                SegmentStartChar: 0,
                SegmentLength: 3,
                YPx: 0
            ),
            rows[0]
        );
        Assert.Equal(
            new VisualRow(
                LogicalLine: 0,
                SegmentIndex: 1,
                SegmentStartChar: 3,
                SegmentLength: 3,
                YPx: 10
            ),
            rows[1]
        );
    }

    [Fact]
    public void Crlf_line_excludes_break_characters_from_segment_length()
    {
        // "aa\r\nbb" は 2 論理行・各 2 文字(改行は含めない)。折り返し OFF。
        // SegmentStartChar は絶対 char offset。line1 は "\r\n" の後 = 4。
        var buf = TextBuffer.FromString("aa\r\nbb");
        var rows = ViewportLayout.Build(
            buf.Current,
            topLine: 0,
            topSegment: 0,
            heightPx: 100,
            wrapColumns: 0,
            M
        );

        Assert.Equal(2, rows.Count);
        Assert.Equal(
            new VisualRow(
                LogicalLine: 0,
                SegmentIndex: 0,
                SegmentStartChar: 0,
                SegmentLength: 2,
                YPx: 0
            ),
            rows[0]
        );
        Assert.Equal(
            new VisualRow(
                LogicalLine: 1,
                SegmentIndex: 0,
                SegmentStartChar: 4,
                SegmentLength: 2,
                YPx: 10
            ),
            rows[1]
        );
    }

    [Fact]
    public void Height_exactly_one_line_yields_only_one_row()
    {
        // heightPx=10 = LineHeightPx → 1 行積んだ次で y=10 になり heightPx 到達で打ち切り。
        var buf = TextBuffer.FromString("a\nb\nc");
        var rows = ViewportLayout.Build(
            buf.Current,
            topLine: 0,
            topSegment: 0,
            heightPx: 10,
            wrapColumns: 0,
            M
        );

        Assert.Single(rows);
        Assert.Equal(
            new VisualRow(
                LogicalLine: 0,
                SegmentIndex: 0,
                SegmentStartChar: 0,
                SegmentLength: 1,
                YPx: 0
            ),
            rows[0]
        );
    }

    [Fact]
    public void TopLine_starts_from_middle_line_with_YPx_zero_at_top()
    {
        // topLine=1 → その論理行の先頭視覚行が Y=0。SegmentStartChar は絶対 offset のまま。
        var buf = TextBuffer.FromString("a\nb\nc");
        var rows = ViewportLayout.Build(
            buf.Current,
            topLine: 1,
            topSegment: 0,
            heightPx: 100,
            wrapColumns: 0,
            M
        );

        Assert.Equal(2, rows.Count);
        Assert.Equal(
            new VisualRow(
                LogicalLine: 1,
                SegmentIndex: 0,
                SegmentStartChar: 2,
                SegmentLength: 1,
                YPx: 0
            ),
            rows[0]
        );
        Assert.Equal(
            new VisualRow(
                LogicalLine: 2,
                SegmentIndex: 0,
                SegmentStartChar: 4,
                SegmentLength: 1,
                YPx: 10
            ),
            rows[1]
        );
    }

    [Fact]
    public void Empty_line_between_content_takes_one_visual_row_of_height()
    {
        // 空行(改行だけ)は 1 視覚行分の高さを持ち、SegmentLength=0。
        var buf = TextBuffer.FromString("a\n\nb");
        var rows = ViewportLayout.Build(
            buf.Current,
            topLine: 0,
            topSegment: 0,
            heightPx: 100,
            wrapColumns: 0,
            M
        );

        Assert.Equal(3, rows.Count);
        Assert.Equal(
            new VisualRow(
                LogicalLine: 0,
                SegmentIndex: 0,
                SegmentStartChar: 0,
                SegmentLength: 1,
                YPx: 0
            ),
            rows[0]
        );
        Assert.Equal(
            new VisualRow(
                LogicalLine: 1,
                SegmentIndex: 0,
                SegmentStartChar: 2,
                SegmentLength: 0,
                YPx: 10
            ),
            rows[1]
        );
        Assert.Equal(
            new VisualRow(
                LogicalLine: 2,
                SegmentIndex: 0,
                SegmentStartChar: 3,
                SegmentLength: 1,
                YPx: 20
            ),
            rows[2]
        );
    }

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
}
