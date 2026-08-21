using System.Text;
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Tests.Buffers;

public class Utf8ScanTests
{
    private static (int CharLen, int Breaks, bool FirstIsLf, bool LastIsCr) S(string s) =>
        Utf8Scan.Stats(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Empty_is_all_zero() => Assert.Equal((0, 0, false, false), S(""));

    [Fact]
    public void Ascii_counts_utf16_units() => Assert.Equal(3, S("abc").CharLen);

    [Fact]
    public void Cjk_3byte_counts_one_unit() => Assert.Equal(2, S("あ亜").CharLen);

    [Fact]
    public void Emoji_4byte_counts_two_units() => Assert.Equal(2, S("😀").CharLen);

    [Fact]
    public void Lf_cr_crlf_each_count_one_break()
    {
        Assert.Equal(1, S("a\nb").Breaks);
        Assert.Equal(1, S("a\rb").Breaks);
        Assert.Equal(1, S("a\r\nb").Breaks);
    }

    [Fact]
    public void Trailing_cr_counts_as_lone_break() => Assert.Equal(1, S("ab\r").Breaks);

    [Fact]
    public void Cr_cr_lf_is_two_breaks() => Assert.Equal(2, S("\r\r\n").Breaks);

    [Fact]
    public void First_lf_last_cr_flags()
    {
        var t = S("\nabc\r");
        Assert.True(t.FirstIsLf);
        Assert.True(t.LastIsCr);
    }

    [Fact]
    public void Mixed_document_matches_naive()
    {
        const string s = "ABC abc 123\r\nあいう\r😀えお\n\nx\r";
        var t = S(s);
        Assert.Equal(s.Length, t.CharLen); // UTF-16単位=string.Length
        Assert.Equal(5, t.Breaks); // \r\n, \r, \n, \n, 末尾\r
    }
}
