using System.Text;
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Tests.Buffers;

public class SnapshotIoTests
{
    private const string Doc = "ABC abc 123\r\nあいう漢字\r😀🈴えお\n\nx\rライン最終";

    private static TextBuffer EditedBuffer()
    {
        var b = TextBuffer.FromString(Doc);
        b.Insert(5, "挿入😀");
        b.Delete(0, 2);
        b.Insert(b.Current.CharLength, "\r\n末尾");
        return b;
    }

    [Fact]
    public void ReadToEnd_matches_GetText()
    {
        var b = EditedBuffer();
        using var reader = b.Current.CreateReader();
        Assert.Equal(b.Current.GetText(0, b.Current.CharLength), reader.ReadToEnd());
    }

    [Fact]
    public void Small_buffer_reads_produce_same_content()
    {
        var b = EditedBuffer();
        string expected = b.Current.GetText(0, b.Current.CharLength);
        using var reader = b.Current.CreateReader();
        var sb = new StringBuilder();
        var buf = new char[7];
        int n;
        while ((n = reader.Read(buf, 0, buf.Length)) > 0)
            sb.Append(buf, 0, n);
        Assert.Equal(expected, sb.ToString());
    }

    [Fact]
    public void Peek_and_single_char_read()
    {
        var snap = TextBuffer.FromString("aあ").Current;
        using var reader = snap.CreateReader();
        Assert.Equal('a', (char)reader.Peek());
        Assert.Equal('a', (char)reader.Read());
        Assert.Equal('あ', (char)reader.Peek());
        Assert.Equal('あ', (char)reader.Read());
        Assert.Equal(-1, reader.Peek());
        Assert.Equal(-1, reader.Read());
    }

    [Fact]
    public void Empty_document_reader_is_immediately_eof()
    {
        using var reader = TextBuffer.FromString("").Current.CreateReader();
        Assert.Equal(-1, reader.Read());
        Assert.Equal("", reader.ReadToEnd());
    }

    [Fact]
    public void WriteTo_roundtrips_builder_input_byte_identical()
    {
        // 妥当UTF-8 → Builder → WriteTo が元バイト列と完全一致(ゼロ変換の直接証明)
        byte[] original = Encoding.UTF8.GetBytes(Doc + Doc + Doc);
        var builder = new TextBufferBuilder();
        for (int off = 0; off < original.Length; off += 1000)
            builder.Add(original.AsSpan(off, Math.Min(1000, original.Length - off)));
        var buffer = builder.Build();

        using var ms = new MemoryStream();
        buffer.Current.WriteTo(ms);
        Assert.Equal(original, ms.ToArray());
    }

    [Fact]
    public void WriteTo_after_edits_matches_naive_utf8()
    {
        var b = TextBuffer.FromString(Doc);
        string model = Doc;
        b.Insert(5, "挿入😀");
        model = model[..5] + "挿入😀" + model[5..];
        b.Delete(2, 4);
        model = model[..2] + model[6..];
        b.Replace(0, 1, "Z");
        model = "Z" + model[1..];

        using var ms = new MemoryStream();
        b.Current.WriteTo(ms);
        Assert.Equal(Encoding.UTF8.GetBytes(model), ms.ToArray());
    }

    [Fact]
    public void Old_snapshot_reader_unaffected_by_later_edits()
    {
        var b = TextBuffer.FromString(Doc);
        var before = b.Current;
        b.Replace(0, b.Current.CharLength, "全部置換");
        using var reader = before.CreateReader();
        Assert.Equal(Doc, reader.ReadToEnd());
    }
}
