using System.Linq;
using kxEdit.Core.Csv;
using Xunit;

namespace kxEdit.Core.Tests.Csv;

public class CsvParserTests
{
    [Fact]
    public void Empty_text_has_no_rows()
    {
        var d = CsvParser.Parse("");
        Assert.True(d.Ok);
        Assert.Empty(d.Rows);
    }

    [Fact]
    public void Single_cell_no_delimiter()
    {
        var d = CsvParser.Parse("hello");
        Assert.True(d.Ok);
        Assert.Single(d.Rows);
        Assert.Single(d.Rows[0]);
        Assert.Equal("hello", d.Rows[0][0].Value);
        Assert.Equal(0, d.Rows[0][0].Start);
        Assert.Equal(5, d.Rows[0][0].Length);
    }

    [Fact]
    public void Two_rows_two_cols_offsets_are_raw_spans()
    {
        var d = CsvParser.Parse("a,bb\nccc,d");
        Assert.True(d.Ok);
        Assert.Equal(2, d.Rows.Count);
        Assert.Equal(new[] { "a", "bb" }, new[] { d.Rows[0][0].Value, d.Rows[0][1].Value });
        Assert.Equal(new[] { "ccc", "d" }, new[] { d.Rows[1][0].Value, d.Rows[1][1].Value });
        Assert.Equal((0, 1), (d.Rows[0][0].Start, d.Rows[0][0].Length));
        Assert.Equal((2, 2), (d.Rows[0][1].Start, d.Rows[0][1].Length));
        Assert.Equal((5, 3), (d.Rows[1][0].Start, d.Rows[1][0].Length));
        Assert.Equal((9, 1), (d.Rows[1][1].Start, d.Rows[1][1].Length));
    }

    [Fact]
    public void Trailing_newline_does_not_make_empty_record()
    {
        var d = CsvParser.Parse("a,b\r\n");
        Assert.Single(d.Rows);
        Assert.Equal(new[] { "a", "b" }, new[] { d.Rows[0][0].Value, d.Rows[0][1].Value });
    }

    [Fact]
    public void Trailing_empty_field_on_content_line()
    {
        var d = CsvParser.Parse("a,");
        Assert.Single(d.Rows);
        Assert.Equal(2, d.Rows[0].Count);
        Assert.Equal("", d.Rows[0][1].Value);
        Assert.Equal((2, 0), (d.Rows[0][1].Start, d.Rows[0][1].Length));
    }

    [Fact]
    public void Quoted_field_with_comma_and_escaped_quote()
    {
        var d = CsvParser.Parse("a,\"b,\"\"c\"\"\",d");
        Assert.True(d.Ok);
        Assert.Single(d.Rows);
        Assert.Equal(3, d.Rows[0].Count);
        Assert.Equal("a", d.Rows[0][0].Value);
        Assert.Equal("b,\"c\"", d.Rows[0][1].Value);
        Assert.Equal("d", d.Rows[0][2].Value);
        Assert.Equal('"', "a,\"b,\"\"c\"\"\",d"[d.Rows[0][1].Start]);
        // 生スパンは外側の引用符を含む丸ごとのトークン "b,""c""" を覆う（9文字）。
        Assert.Equal(9, d.Rows[0][1].Length);
    }

    [Fact]
    public void Crlf_advances_offset_by_two()
    {
        var d = CsvParser.Parse("a\r\nbb");
        Assert.True(d.Ok);
        Assert.Equal(2, d.Rows.Count);
        Assert.Equal(3, d.Rows[1][0].Start);
    }

    [Fact]
    public void Quoted_field_with_embedded_newline_is_one_logical_row()
    {
        var d = CsvParser.Parse("\"line1\nline2\",x");
        Assert.True(d.Ok);
        Assert.Single(d.Rows);
        Assert.Equal(2, d.Rows[0].Count);
        Assert.Equal("line1\nline2", d.Rows[0][0].Value);
        Assert.Equal("x", d.Rows[0][1].Value);
    }

    [Fact]
    public void Unterminated_quote_is_not_ok()
    {
        var d = CsvParser.Parse("a,\"unterminated");
        Assert.False(d.Ok);
    }

    // M-25(2026-09-01 設計書 §8.17-1): 末尾に未終端引用符のレコードを足すと Ok=false になるが、
    // **先行するレコードの行数・列数・値・スパンは 1 つも変わらない**。
    // `if (inQuotes) ok = false;` の直後の `if (ok && (pos > fieldStart || row.Count > 0))` が
    // 末尾の不完全レコードだけを rows へ混ぜないため。
    // この帰結は CsvController の F2 確定 guard が Ok を見なければならない唯一の根拠なので、
    // 論証ではなく実測として固定する(上の Unterminated_quote_is_not_ok は Ok しか見ていない)。
    [Fact]
    public void Unterminated_trailing_record_sets_not_ok_but_leaves_preceding_rows_intact()
    {
        var d = CsvParser.Parse("a1,a2\nb1,b2\n\"x");
        Assert.False(d.Ok);
        Assert.Equal(2, d.Rows.Count); // 末尾の不完全レコードは rows に混ざらない
        Assert.Equal(2, d.Rows[0].Count);
        Assert.Equal(2, d.Rows[1].Count);
        var f = d.GetField(0, 0);
        Assert.NotNull(f);
        Assert.Equal((0, 2, "a1"), (f!.Start, f.Length, f.Value));
    }

    [Fact]
    public void Parse_TextSnapshotOverload_ProducesSameResultAsString()
    {
        string csv = "a,b,c\n1,\"quoted, comma\",3\n\"multi\nline\",x,y\n";
        var expected = CsvParser.Parse(csv);

        var buffer = kxEdit.Core.Buffers.TextBuffer.FromString(csv);
        var actual = CsvParser.Parse(buffer.Current); // 新規オーバーロード

        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (int i = 0; i < expected.Rows.Count; i++)
        {
            Assert.Equal(expected.Rows[i].Count, actual.Rows[i].Count);
            for (int j = 0; j < expected.Rows[i].Count; j++)
                Assert.Equal(expected.Rows[i][j].Value, actual.Rows[i][j].Value);
        }
        Assert.Equal(expected.Ok, actual.Ok);
    }

    [Fact]
    public void Parse_ReturnsOkFalse_WhenSingleFieldExceedsLimit()
    {
        var text = "\"" + new string('a', 200); // 未クローズかつ長大
        var limits = new CsvParser.ParseLimits(
            MaxFieldChars: 100,
            MaxTotalCells: 10_000,
            MaxTotalRows: 100,
            MaxTotalChars: 1024
        );
        var csv = CsvParser.ParseForTest(text, limits);
        Assert.False(csv.Ok);
    }

    [Fact]
    public void Parse_ReturnsOkFalse_WhenTotalCellsExceedLimit()
    {
        var text = new string(',', 200);
        var limits = new CsvParser.ParseLimits(
            MaxFieldChars: 1024,
            MaxTotalCells: 100,
            MaxTotalRows: 100,
            MaxTotalChars: 1024
        );
        var csv = CsvParser.ParseForTest(text, limits);
        Assert.False(csv.Ok);
    }

    [Fact]
    public void Parse_ReturnsOkFalse_WhenTotalRowsExceedLimit()
    {
        var text = string.Concat(Enumerable.Repeat("a\n", 200));
        var limits = new CsvParser.ParseLimits(
            MaxFieldChars: 1024,
            MaxTotalCells: 10_000,
            MaxTotalRows: 100,
            MaxTotalChars: 1024
        );
        var csv = CsvParser.ParseForTest(text, limits);
        Assert.False(csv.Ok);
    }

    [Fact]
    public void Parse_ReturnsOkFalse_WhenTotalCharsExceedLimit()
    {
        // 各フィールド 50 chars + 51 chars = 総 101 chars。MaxTotalChars=100 で超過。
        // 単一フィールドは MaxFieldChars=1024 の内側なので、4 番目のガードのみ発火。
        var text = new string('a', 50) + "," + new string('b', 51);
        var limits = new CsvParser.ParseLimits(
            MaxFieldChars: 1024,
            MaxTotalCells: 10_000,
            MaxTotalRows: 100,
            MaxTotalChars: 100
        );
        var csv = CsvParser.ParseForTest(text, limits);
        Assert.False(csv.Ok);
    }

    [Fact]
    public void Parse_ReturnsOkTrue_AtExactFieldBoundary()
    {
        var text = new string('a', 100); // ちょうど上限
        var limits = new CsvParser.ParseLimits(
            MaxFieldChars: 100,
            MaxTotalCells: 10_000,
            MaxTotalRows: 100,
            MaxTotalChars: 1024
        );
        var csv = CsvParser.ParseForTest(text, limits);
        Assert.True(csv.Ok);
    }

    [Fact]
    public void Parse_ReturnsOkTrue_AtExactCellsBoundary()
    {
        var text = new string(',', 100); // 100 コンマ = totalCells++ 100 回でちょうど上限
        var limits = new CsvParser.ParseLimits(
            MaxFieldChars: 1024,
            MaxTotalCells: 100,
            MaxTotalRows: 100,
            MaxTotalChars: 1024
        );
        var csv = CsvParser.ParseForTest(text, limits);
        Assert.True(csv.Ok);
    }

    [Fact]
    public void Parse_ReturnsOkTrue_AtExactRowsBoundary()
    {
        var text = string.Concat(Enumerable.Repeat("a\n", 100)); // 100 行でちょうど上限
        var limits = new CsvParser.ParseLimits(
            MaxFieldChars: 1024,
            MaxTotalCells: 10_000,
            MaxTotalRows: 100,
            MaxTotalChars: 1024
        );
        var csv = CsvParser.ParseForTest(text, limits);
        Assert.True(csv.Ok);
        Assert.Equal(100, csv.Rows.Count);
    }

    [Fact]
    public void Parse_ReturnsOkTrue_AtExactTotalCharsBoundary()
    {
        // 各フィールド 50 chars ずつ = 総 100 chars でちょうど上限。
        var text = new string('a', 50) + "," + new string('b', 50);
        var limits = new CsvParser.ParseLimits(
            MaxFieldChars: 1024,
            MaxTotalCells: 10_000,
            MaxTotalRows: 100,
            MaxTotalChars: 100
        );
        var csv = CsvParser.ParseForTest(text, limits);
        Assert.True(csv.Ok);
    }

    [Fact]
    public void Parse_ReturnsOkFalse_WhenTotalCharsExceedAtCommaBoundary()
    {
        // mid-loop の EndField 直後ガード (comma 分岐 L134-135 / CR|LF 分岐 L149-150)
        // が発火する経路を機械固定する。上の Parse_ReturnsOkFalse_WhenTotalCharsExceedLimit
        // は tail EndField 経路のみを踏むため、mid-loop の `if (!ok) break;` が消えても
        // 素通りしてしまう(mutation 耐性ゼロ)。
        //
        // 入力: 60 chars + "," + 60 chars + ",X\n"
        //   - 1st comma (pos=60) の EndField: totalChars=60 で OK 継続
        //   - 2nd comma (pos=121) の EndField: totalChars=120 > 100 で ok=false
        //     → mid-loop comma 分岐で break → 以降の "X\n" は消費されず EndRow も不発
        //
        // 両 mid-loop break が同時に消えた場合、"X\n" が消費されて CR/LF 分岐で
        // EndRow が発火 = rows に 1 行混入する。Assert.Empty がその変異を kill する。
        var text = new string('a', 60) + "," + new string('b', 60) + ",X\n";
        var limits = new CsvParser.ParseLimits(
            MaxFieldChars: 1024,
            MaxTotalCells: 10_000,
            MaxTotalRows: 100,
            MaxTotalChars: 100
        );
        var csv = CsvParser.ParseForTest(text, limits);
        Assert.False(csv.Ok);
        Assert.Empty(csv.Rows); // mid-loop break が消えると余分な行が混入する
    }

    [Fact]
    public void Parse_TextSnapshotOverload_HandlesQuotedFieldAcrossPieceBoundary()
    {
        // SnapshotReader は piece 境界を Read/Peek で透過的に跨ぐ(Ensure() 経由)。
        // TextBufferBuilder に複数 Add で強制的に piece を分割し、quoted field / \r\n / "" が
        // 境界を跨いでも状態機械が崩れないことを機械固定する。
        var b = new kxEdit.Core.Buffers.TextBufferBuilder();
        // piece 1: "head,\"" (開き引用符でフィールド開始)
        b.Add(System.Text.Encoding.UTF8.GetBytes("head,\""));
        // piece 2: "with , comma\r\nand newline," (quoted 内に comma と CRLF)
        b.Add(System.Text.Encoding.UTF8.GetBytes("with , comma\r\nand newline\","));
        // piece 3: "tail\n" (次の field と行終端)
        b.Add(System.Text.Encoding.UTF8.GetBytes("tail\n"));
        var buffer = b.Build();

        var parsed = CsvParser.Parse(buffer.Current);
        Assert.Single(parsed.Rows);
        Assert.Equal(3, parsed.Rows[0].Count);
        Assert.Equal("head", parsed.Rows[0][0].Value);
        Assert.Equal("with , comma\r\nand newline", parsed.Rows[0][1].Value);
        Assert.Equal("tail", parsed.Rows[0][2].Value);
        Assert.True(parsed.Ok);
    }
}
