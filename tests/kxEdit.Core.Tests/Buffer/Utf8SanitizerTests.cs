using System.Text;
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Tests.Buffers;

public class Utf8SanitizerTests
{
    private static readonly byte[] Fffd = [0xEF, 0xBF, 0xBD]; // U+FFFD のUTF-8表現

    [Fact]
    public void Valid_ascii_cjk_emoji_passes_through_unreplaced()
    {
        byte[] input = Encoding.UTF8.GetBytes("abc あ亜 😀\r\n");
        var (clean, replaced) = Utf8Sanitizer.Sanitize(input);
        Assert.False(replaced);
        Assert.True(clean.Span.SequenceEqual(input));
    }

    [Fact]
    public void Empty_passes_through_unreplaced()
    {
        var (clean, replaced) = Utf8Sanitizer.Sanitize(ReadOnlyMemory<byte>.Empty);
        Assert.False(replaced);
        Assert.Equal(0, clean.Length);
    }

    [Fact]
    public void Lone_continuation_byte_is_replaced_with_fffd()
    {
        var (clean, replaced) = Utf8Sanitizer.Sanitize(new byte[] { 0x80 });
        Assert.True(replaced);
        Assert.True(clean.Span.SequenceEqual(Fffd));
    }

    [Fact]
    public void Truncated_3byte_sequence_is_replaced()
    {
        // "あ" = E3 81 82 の先頭2バイトで終端
        byte[] input = [(byte)'a', 0xE3, 0x81];
        var (clean, replaced) = Utf8Sanitizer.Sanitize(input);
        Assert.True(replaced);
        Assert.Equal((byte)'a', clean.Span[0]);
        Assert.True(clean.Span[1..].SequenceEqual(Fffd));
    }

    [Fact]
    public void Overlong_encoding_is_replaced()
    {
        // C0 AF = '/' の過長エンコーディング(.NETの検証が拒否する)
        byte[] input = [0xC0, 0xAF];
        var (clean, replaced) = Utf8Sanitizer.Sanitize(input);
        Assert.True(replaced);
        string s = Encoding.UTF8.GetString(clean.Span);
        Assert.DoesNotContain('/', s);
        Assert.Contains('�', s);
    }
}
