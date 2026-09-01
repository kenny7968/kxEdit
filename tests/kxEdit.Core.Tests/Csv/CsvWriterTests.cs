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
    //
    // 以下の「kill 対象」は変異を実際に注入して落ちたテストを数えた実測。変異ごとに落ちる
    // 本数が違うので「いずれも N 本」と束ねない。参照する変異は次の 6 種:
    //   (a) 置換順序の入替(\r→\n を先に)  (b) Replace("\r\n","\n") 削除
    //   (c) Replace("\r","\n") 削除        (d) 過剰置換(CRLF→LF 2 個)
    //   (e) 丸ごと no-op                   (f) 改行の連続を 1 個へ畳む

    // kill 対象: (a) (b) (d) (e)。この fixture 上では CRLF が LF 1 個へ畳まれない変異が落ちる。
    // (c) は CRLF を素通しするので落ちず、(f) も単発 CRLF は LF 1 個へ畳むので落ちない。
    [Fact]
    public void Crlf_is_normalized_to_lf() =>
        Assert.Equal("a\nb", CsvWriter.NormalizeEols("a\r\nb"));

    // kill 対象: (c) (e)。単独 CR が LF にならない変異だけがここで落ちる。
    [Fact]
    public void Lone_cr_is_normalized_to_lf() =>
        Assert.Equal("a\nb", CsvWriter.NormalizeEols("a\rb"));

    // kill 対象: (a) (b) (c) (d) (e) の 5 種を単独で殺す(CRLF と単独 CR を 1 本で踏むため)。
    // ただし (f) は殺せない —— 改行が隣接しないので畳み込みが観測できない。
    [Fact]
    public void Mixed_breaks_are_normalized_to_lf() =>
        Assert.Equal("a\nb\nc\nd", CsvWriter.NormalizeEols("a\r\nb\rc\nd"));

    // kill 対象: (a)〜(f) の 6 種すべて。隣接した改行を持つ唯一の fixture で、(f) を殺せるのは
    // ここだけ —— 単発改行だけの fixture は「改行ごとに 1 個」と「連続を 1 個へ畳む」を
    // 区別できない(CLAUDE.md §4-B の partial-selection と同型の穴)。
    // NormalizeEols は比較専用ではなく CsvCellEditor.Commit が本文へ書く値そのものを作るので、
    // 畳み込みはユーザーが入れたセル内の空行を黙って消す実害になる。
    [Theory]
    [InlineData("a\r\n\r\rb", "a\n\n\nb")] // CRLF に単独 CR が 2 つ隣接
    [InlineData("a\n\rb", "a\n\nb")] // LF の直後の CR(CRLF ではない=改行 2 個)
    [InlineData("\r\n\r\n", "\n\n")] // 改行だけ・末尾も改行
    public void Adjacent_breaks_are_normalized_one_for_one(string s, string expected) =>
        Assert.Equal(expected, CsvWriter.NormalizeEols(s));

    // kill 対象: 末尾の改行を落とす変異(TrimEnd('\n') 相当)。入力に CR が無いので
    // (a)〜(f) は 1 つも殺さない(実測)。TrimEnd は上の "\r\n\r\n" も落ちるので 2 本で押さえる形。
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
