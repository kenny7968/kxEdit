using kxEdit.Core.Csv;
using Xunit;

namespace kxEdit.Core.Tests.Csv;

public class CsvGridNavigationTests
{
    private static CsvDocument Doc(string t) => CsvParser.Parse(t);

    [Fact]
    public void RowStart_and_RowEnd()
    {
        var d = Doc("a,b,c\nd,e,f");
        Assert.Equal((1, 0), d.RowStart(1));
        Assert.Equal((1, 2), d.RowEnd(1));
    }

    [Fact]
    public void ColumnTop_and_ColumnBottom_uniform()
    {
        var d = Doc("a,b\nc,d\ne,f");
        Assert.Equal((0, 1), d.ColumnTop(1));
        Assert.Equal((2, 1), d.ColumnBottom(1));
    }

    [Fact]
    public void ColumnBottom_skips_rows_missing_that_column()
    {
        var d = Doc("a,b,c\nd,e,f\ng"); // 3行目は1列のみ
        Assert.Equal((1, 2), d.ColumnBottom(2)); // 列2を持つ最後の行は2行目
    }

    [Fact]
    public void ColumnTop_skips_leading_rows_missing_that_column()
    {
        var d = Doc("g\na,b,c\nd,e,f"); // 1行目は1列のみ
        Assert.Equal((1, 2), d.ColumnTop(2)); // 列2を持つ最初の行は2行目
    }

    [Fact]
    public void TopLeft_and_BottomRight()
    {
        var d = Doc("a,b,c\nd,e");
        Assert.Equal((0, 0), d.TopLeft());
        Assert.Equal((1, 1), d.BottomRight()); // 最終行の最終列
    }

    [Fact]
    public void GoTo_valid_and_out_of_range()
    {
        var d = Doc("a,b\nc,d");
        Assert.Equal((1, 1), d.GoTo(1, 1));
        Assert.Null(d.GoTo(5, 0));
        Assert.Null(d.GoTo(0, 9));
        Assert.Null(d.GoTo(-1, 0));
    }

    [Fact]
    public void Helpers_on_empty_doc_return_null()
    {
        var d = Doc("");
        Assert.Null(d.RowStart(0));
        Assert.Null(d.RowEnd(0));
        Assert.Null(d.ColumnTop(0));
        Assert.Null(d.ColumnBottom(0));
        Assert.Null(d.TopLeft());
        Assert.Null(d.BottomRight());
        Assert.Null(d.GoTo(0, 0));
    }
}
