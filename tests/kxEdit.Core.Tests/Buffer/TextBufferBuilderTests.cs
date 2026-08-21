using System.Text;
using kxEdit.Core.Buffers;
using kxEdit.Core.Text;

namespace kxEdit.Core.Tests.Buffers;

public class TextBufferBuilderTests
{
    private static string FullText(TextBuffer buffer) =>
        buffer.Current.GetText(0, buffer.Current.CharLength);

    [Fact]
    public void FromString_empty_gives_empty_document()
    {
        var buffer = TextBuffer.FromString("");
        Assert.Equal(0, buffer.Current.CharLength);
        Assert.Equal(1, buffer.Current.LineCount);
    }

    [Fact]
    public void FromString_roundtrips_mixed_content()
    {
        const string doc = "ABC\r\nあいう😀\n\rx";
        var buffer = TextBuffer.FromString(doc);
        Assert.Equal(doc, FullText(buffer));
        Assert.Equal(4, buffer.Current.LineCount);
    }

    [Fact]
    public void Cjk_3byte_split_1_plus_2_across_adds()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("あ");
        var builder = new TextBufferBuilder();
        builder.Add(bytes.AsSpan(0, 1));
        builder.Add(bytes.AsSpan(1, 2));
        var buffer = builder.Build();
        Assert.Equal("あ", FullText(buffer));
        Assert.False(builder.HadReplacement);
    }

    [Fact]
    public void Emoji_4byte_split_2_plus_2_across_adds()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("😀");
        var builder = new TextBufferBuilder();
        builder.Add(bytes.AsSpan(0, 2));
        builder.Add(bytes.AsSpan(2, 2));
        var buffer = builder.Build();
        Assert.Equal("😀", FullText(buffer));
        Assert.False(builder.HadReplacement);
    }

    [Fact]
    public void Valid_2mb_document_added_in_64kb_slices()
    {
        var sb = new StringBuilder();
        while (sb.Length < 1_300_000)
            sb.Append("line of ascii text 0123456789\r\nあいうえお漢字カナ😀🈴まみむめも\n");
        string doc = sb.ToString();
        byte[] bytes = Encoding.UTF8.GetBytes(doc); // 2MB超
        Assert.True(bytes.Length > 2_000_000);

        var builder = new TextBufferBuilder();
        for (int off = 0; off < bytes.Length; off += 64 * 1024)
            builder.Add(bytes.AsSpan(off, Math.Min(64 * 1024, bytes.Length - off)));
        var buffer = builder.Build();

        Assert.False(builder.HadReplacement);
        Assert.Equal(doc.Length, buffer.Current.CharLength);
        Assert.Equal(doc, FullText(buffer));
        Assert.True(buffer.Current.PieceCount >= 1);
    }

    [Fact]
    public void Incomplete_tail_becomes_replacement_char()
    {
        var builder = new TextBufferBuilder();
        builder.Add("ab"u8);
        builder.Add([0xE3, 0x81]); // 「あ」の先頭2バイトで終端
        var buffer = builder.Build();
        Assert.True(builder.HadReplacement);
        Assert.Equal("ab�", FullText(buffer));
    }

    [Fact]
    public void Invalid_bytes_mid_stream_are_replaced()
    {
        var builder = new TextBufferBuilder();
        builder.Add([(byte)'a', 0x80, (byte)'b']); // 孤立継続バイト
        var buffer = builder.Build();
        Assert.True(builder.HadReplacement);
        Assert.Equal("a�b", FullText(buffer));
    }

    [Fact]
    public void Add_after_build_throws()
    {
        var builder = new TextBufferBuilder();
        builder.Add("abc"u8);
        builder.Build();
        Assert.Throws<InvalidOperationException>(() => builder.Add("x"u8));
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void MaxTotalBytes_default_is_512MB()
    {
        // PR-E Task 7 (CSV-L-10): int.MaxValue (~2GB) は攻撃 file を通してしまう。
        // 512 MB (UTF-16 で 256M chars) に引下げ、TextBuffer 側と統一。
        var builder = new TextBufferBuilder();
        Assert.Equal(512L * 1024 * 1024, builder.MaxTotalBytes);
    }

    [Fact]
    public void Add_Throws_DocumentTooLarge_WhenExceedingCap()
    {
        var builder = new TextBufferBuilder { MaxTotalBytes = 1024 }; // 既存 internal init を活用
        var payload = new byte[2048];
        Array.Fill(payload, (byte)'a');

        var ex = Assert.Throws<DocumentTooLargeException>(() => builder.Add(payload));
        Assert.True(ex.AttemptedBytes > 1024);
    }

    [Fact]
    public void Add_Succeeds_AtExactCap()
    {
        var builder = new TextBufferBuilder { MaxTotalBytes = 1024 };
        var payload = new byte[1024];
        Array.Fill(payload, (byte)'a');

        builder.Add(payload); // 例外なし
        Assert.Equal(1024, builder.Build().Current.CharLength);
    }

    [Fact]
    public void DocumentTooLargeException_IsCaughtBy_BroadExceptionCatch()
    {
        // catch (Exception) 経路でも受けられることを担保するリグレッションガード
        var builder = new TextBufferBuilder { MaxTotalBytes = 8 };
        var payload = new byte[16];
        Array.Fill(payload, (byte)'a');

        try
        {
            builder.Add(payload);
            Assert.Fail("expected throw");
        }
        catch (Exception ex)
        {
            Assert.IsType<DocumentTooLargeException>(ex);
        }
    }
}
