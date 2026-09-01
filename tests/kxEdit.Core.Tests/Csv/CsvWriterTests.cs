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

    // ===== NormalizeEols(F2 確定値と CsvParser の Value を同じ土俵に乗せる) =====
    // CsvParser は引用符内の CR / LF を literal のまま Value へ積む(CsvParser.cs:117-124)ため、
    // ConvertEols 後の Value は変換前と素の比較で一致しない。正規化はその差を吸収する。

    // kill 対象(以下 2 本が担う): 置換順序の入替(\r→\n を先に適用)・
    // Replace("\r\n","\n") の削除・過剰置換(CRLF→LF 2 個)・丸ごと no-op。
    // いずれも CRLF が LF 2 個へ化けるため落ちる=順序と非過剰性はこの 2 本が守る。
    [Fact]
    public void Crlf_is_normalized_to_lf() =>
        Assert.Equal("a\nb", CsvWriter.NormalizeEols("a\r\nb"));

    // kill 対象: Replace("\r","\n") の削除・丸ごと no-op(単独 CR が残る)。
    [Fact]
    public void Lone_cr_is_normalized_to_lf() =>
        Assert.Equal("a\nb", CsvWriter.NormalizeEols("a\rb"));

    // 混在は上記 4 変異すべてを単独で殺す(CRLF と単独 CR を 1 本で踏む)。
    [Fact]
    public void Mixed_breaks_are_normalized_to_lf() =>
        Assert.Equal("a\nb\nc\nd", CsvWriter.NormalizeEols("a\r\nb\rc\nd"));

    // このファイルで末尾に改行を持つ唯一の fixture。入力に CR が無いので CR 関連の変異は
    // 1 つも殺さない(実測)。単独で殺すのは末尾の改行を落とす変異(TrimEnd 相当)のみ。
    [Fact]
    public void Lf_only_value_is_not_changed_by_normalize() =>
        Assert.Equal("a\nb\n", CsvWriter.NormalizeEols("a\nb\n"));

    // 改行を含まない値は素通し(空文字列を含む)。
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("a,b\"c")]
    public void Value_without_breaks_is_not_changed_by_normalize(string s) =>
        Assert.Equal(s, CsvWriter.NormalizeEols(s));

    // kill 対象: ArgumentNullException.ThrowIfNull の削除(null は Replace で
    // NullReferenceException になり、契約どおりの ArgumentNullException にならない)。
    [Fact]
    public void NormalizeEols_throws_on_null_value() =>
        Assert.Throws<ArgumentNullException>(() => CsvWriter.NormalizeEols(null!));
}
