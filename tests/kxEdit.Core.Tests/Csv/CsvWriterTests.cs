using kxEdit.Core.Csv;
using Xunit;

namespace kxEdit.Core.Tests.Csv;

public class CsvWriterTests
{
    [Fact]
    public void Plain_value_is_unchanged() => Assert.Equal("abc", CsvWriter.EscapeField("abc"));

    [Fact]
    public void Empty_value_is_unchanged() => Assert.Equal("", CsvWriter.EscapeField(""));

    [Fact]
    public void Comma_is_quoted() => Assert.Equal("\"a,b\"", CsvWriter.EscapeField("a,b"));

    [Fact]
    public void Quote_is_doubled_and_wrapped() =>
        Assert.Equal("\"he \"\"q\"\"\"", CsvWriter.EscapeField("he \"q\""));

    [Fact]
    public void Lf_is_quoted() => Assert.Equal("\"a\nb\"", CsvWriter.EscapeField("a\nb"));

    [Fact]
    public void Cr_is_quoted() => Assert.Equal("\"a\rb\"", CsvWriter.EscapeField("a\rb"));

    [Fact]
    public void Leading_space_is_not_quoted() => Assert.Equal(" a ", CsvWriter.EscapeField(" a "));

    [Theory]
    [InlineData("abc")]
    [InlineData("a,b,c")]
    [InlineData("he said \"hi\"")]
    [InlineData("line1\nline2")]
    [InlineData("comma, and \"quote\"")]
    public void Roundtrip_escape_then_parse_preserves_value(string value)
    {
        // 1行1セルの CSV として直列化→パースし、論理値が戻ることを確認。
        string csvText = CsvWriter.EscapeField(value);
        var doc = CsvParser.Parse(csvText);
        Assert.True(doc.Ok);
        Assert.Equal(value, doc.GetField(0, 0)!.Value);
    }

    [Fact]
    public void Empty_value_parses_to_no_rows() =>
        Assert.Empty(CsvParser.Parse(CsvWriter.EscapeField("")).Rows);
}
