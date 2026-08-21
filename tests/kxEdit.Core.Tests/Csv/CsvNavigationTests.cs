using kxEdit.Core.Csv;
using Xunit;

namespace kxEdit.Core.Tests.Csv;

public class CsvNavigationTests
{
    private static CsvDocument Doc(string t) => CsvParser.Parse(t);

    [Fact]
    public void FindCell_caret_inside_field()
    {
        var d = Doc("a,bb\nccc,d"); // bb=[2,2)
        Assert.Equal((0, 1), d.FindCell(3)); // 'b' の上
    }

    [Fact]
    public void FindCell_caret_on_delimiter_belongs_to_left_field()
    {
        var d = Doc("a,bb"); // a=[0,1) , カンマ=1
        Assert.Equal((0, 0), d.FindCell(1));
    }

    [Fact]
    public void FindCell_caret_on_second_row()
    {
        var d = Doc("a,bb\nccc,d"); // ccc=[5,3)
        Assert.Equal((1, 0), d.FindCell(6));
    }

    [Fact]
    public void FindCell_empty_doc_returns_origin() => Assert.Equal((0, 0), Doc("").FindCell(0));

    [Fact]
    public void MoveCell_left_right_within_row()
    {
        var d = Doc("a,b,c");
        Assert.Equal((0, 0), d.MoveCell(0, 1, Direction.Left));
        Assert.Equal((0, 2), d.MoveCell(0, 1, Direction.Right));
        Assert.Null(d.MoveCell(0, 0, Direction.Left)); // 左端
        Assert.Null(d.MoveCell(0, 2, Direction.Right)); // 右端
    }

    [Fact]
    public void MoveCell_up_down_between_rows()
    {
        var d = Doc("a,b\nc,d\ne,f");
        Assert.Equal((0, 1), d.MoveCell(1, 1, Direction.Up));
        Assert.Equal((2, 1), d.MoveCell(1, 1, Direction.Down));
        Assert.Null(d.MoveCell(0, 0, Direction.Up)); // 先頭行
        Assert.Null(d.MoveCell(2, 0, Direction.Down)); // 最終行
    }

    [Fact]
    public void MoveCell_down_clamps_to_last_col_on_ragged_row()
    {
        var d = Doc("a,b,c\nx"); // 2行目は1列
        Assert.Equal((1, 0), d.MoveCell(0, 2, Direction.Down));
    }

    [Fact]
    public void GetField_and_Header()
    {
        var d = Doc("name,age\ntaro,20");
        Assert.Equal("20", d.GetField(1, 1)!.Value);
        Assert.Null(d.GetField(5, 0));
        Assert.Equal("age", d.Header(1)!.Value);
        Assert.Null(d.Header(9));
    }

    [Fact]
    public void FindCell_caret_past_end_returns_last_field() =>
        Assert.Equal((0, 1), Doc("a,bb").FindCell(100));

    [Fact]
    public void FindCell_negative_caret_returns_origin() =>
        Assert.Equal((0, 0), Doc("a,b").FindCell(-5));

    [Fact]
    public void FindCell_on_blank_line_returns_that_rows_empty_field()
    {
        var d = Doc("a,b\n\nc,d"); // 2行目は空行（空フィールド1つ・Start=4）
        var (r, _) = d.FindCell(4);
        Assert.Equal(1, r);
    }

    [Fact]
    public void MoveCell_up_clamps_to_last_col_on_ragged_row()
    {
        var d = Doc("x\na,b,c"); // 1行目1列・2行目3列
        Assert.Equal((0, 0), d.MoveCell(1, 2, Direction.Up));
    }

    [Fact]
    public void MoveCell_on_empty_doc_returns_null()
    {
        var d = Doc(""); // Rows.Count == 0
        Assert.Null(d.MoveCell(0, 0, Direction.Right));
        Assert.Null(d.MoveCell(0, 0, Direction.Down));
    }

    [Fact]
    public void GetField_negative_indices_return_null()
    {
        var d = Doc("a,b");
        Assert.Null(d.GetField(-1, 0));
        Assert.Null(d.GetField(0, -1));
    }
}
