using System.Text;
using kxEdit.Core.Buffers;
using kxEdit.Core.Text;

namespace kxEdit.Core.Tests.Buffers;

/// <summary>文書上限(§0-1: 既定 512 MB。2026-07-20 v0.11 引下げ)の強制。テストは上限を注入して閾値近傍を検証する。</summary>
public class SizeLimitTests
{
    [Fact]
    public void Builder_add_beyond_limit_throws()
    {
        var builder = new TextBufferBuilder { MaxTotalBytes = 10 };
        builder.Add("12345"u8);
        Assert.Throws<DocumentTooLargeException>(() => builder.Add("6789ab"u8)); // 5+6=11 > 10
    }

    [Fact]
    public void Builder_at_exact_limit_succeeds()
    {
        var builder = new TextBufferBuilder { MaxTotalBytes = 10 };
        builder.Add("1234567890"u8);
        var buffer = builder.Build();
        Assert.Equal(10, buffer.Current.CharLength);
    }

    [Fact]
    public void Builder_limit_counts_sanitized_expansion()
    {
        // 不正バイト1個 → U+FFFD(3バイト)に膨張して上限超過
        var builder = new TextBufferBuilder { MaxTotalBytes = 4 };
        Assert.Throws<DocumentTooLargeException>(() => builder.Add([(byte)'a', (byte)'b', 0x80]));
    }

    [Fact]
    public void Insert_beyond_limit_throws_and_leaves_buffer_intact()
    {
        var buffer = TextBuffer.FromString("abcdef"); // 6バイト
        buffer.MaxTotalBytes = 10;
        Assert.Throws<InvalidOperationException>(() => buffer.Insert(3, "12345")); // 6+5=11 > 10
        Assert.Equal("abcdef", buffer.Current.GetText(0, 6));
        Assert.False(buffer.CanUndo);
        Assert.False(buffer.Modified);
    }

    [Fact]
    public void Replace_that_shrinks_below_limit_succeeds()
    {
        var buffer = TextBuffer.FromString("あいう"); // 9バイト
        buffer.MaxTotalBytes = 10;
        buffer.Replace(0, 3, "xyz45"); // 9−9+5=5 ≤ 10
        Assert.Equal("xyz45", buffer.Current.GetText(0, 5));
    }

    [Fact]
    public void Insert_at_exact_limit_succeeds()
    {
        var buffer = TextBuffer.FromString("abcde"); // 5バイト
        buffer.MaxTotalBytes = 10;
        buffer.Insert(5, "あ12"); // 5+5=10
        Assert.Equal("abcdeあ12", buffer.Current.GetText(0, 8));
    }

    [Fact]
    public void TextBuffer_MaxTotalBytes_default_is_512MB()
    {
        // PR-E Task 7 (CSV-L-10): int.MaxValue (~2GB) は攻撃 file を通してしまう。
        // 512 MB (UTF-16 で 256M chars) に引下げ、TextBufferBuilder 側と統一。
        var buffer = TextBuffer.FromString("");
        Assert.Equal(512L * 1024 * 1024, buffer.MaxTotalBytes);
    }
}
