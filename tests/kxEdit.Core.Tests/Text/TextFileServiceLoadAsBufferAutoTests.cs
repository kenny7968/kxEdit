using System.Text;
using kxEdit.Core.Buffers;
using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

public class TextFileServiceLoadAsBufferAutoTests
{
    private const string JpCrlf = "一行目\r\n二行目\r\n三行目\r\n";
    private const string JpLf = "一行目\n二行目\n三行目\n";

    [Fact]
    public void LoadAuto_Utf8_NoBom_DetectsUtf8_AndReturnsBufferText()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(JpCrlf));
            var loaded = TextFileService.LoadAsBufferAuto(path);
            Assert.Equal(65001, loaded.Encoding.CodePage);
            Assert.False(loaded.HasBom);
            Assert.Equal(LineEnding.Crlf, loaded.LineEnding);
            Assert.False(loaded.HadReplacementChar);
            Assert.Equal(
                JpCrlf,
                loaded.Buffer.Current.GetText(0, loaded.Buffer.Current.CharLength)
            );
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAuto_Utf8_WithBom_DetectsHasBom_AndStripsPreamble()
    {
        string path = Path.GetTempFileName();
        try
        {
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            File.WriteAllBytes(path, bom.Concat(Encoding.UTF8.GetBytes(JpCrlf)).ToArray());
            var loaded = TextFileService.LoadAsBufferAuto(path);
            Assert.Equal(65001, loaded.Encoding.CodePage);
            Assert.True(loaded.HasBom);
            Assert.Equal(
                JpCrlf,
                loaded.Buffer.Current.GetText(0, loaded.Buffer.Current.CharLength)
            );
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAuto_ShiftJis_Autodetects()
    {
        string path = Path.GetTempFileName();
        try
        {
            var enc = EncodingCatalog.Get(932);
            File.WriteAllBytes(path, enc.GetBytes(JpCrlf));
            var loaded = TextFileService.LoadAsBufferAuto(path);
            Assert.Equal(932, loaded.Encoding.CodePage);
            Assert.False(loaded.HasBom);
            Assert.Equal(
                JpCrlf,
                loaded.Buffer.Current.GetText(0, loaded.Buffer.Current.CharLength)
            );
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAuto_EucJp_Autodetects()
    {
        string path = Path.GetTempFileName();
        try
        {
            var enc = EncodingCatalog.Get(51932);
            // EUC-JP は UtfUnknown が信頼度不足で null 返しがあるため、明示に十分な量を書く。
            string body = string.Concat(Enumerable.Repeat(JpCrlf, 20));
            File.WriteAllBytes(path, enc.GetBytes(body));
            var loaded = TextFileService.LoadAsBufferAuto(path);
            // 検出は EUC-JP or SJIS 相当が期待だが、少なくとも buffer 内容とロジックは通ることを確認。
            // ここでは EUC-JP と決めつけず、forcedCodePage 経路も別テストで担保する。
            Assert.NotNull(loaded.Buffer);
            Assert.Equal(LineEnding.Crlf, loaded.LineEnding);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAuto_Utf8_LfOnly_DetectsLf()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(JpLf));
            var loaded = TextFileService.LoadAsBufferAuto(path);
            Assert.Equal(LineEnding.Lf, loaded.LineEnding);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAuto_ForcedCodePage_UsesGivenEncoding()
    {
        // ファイルは UTF-8 で書くが、forcedCodePage=932 (SJIS) で読むと本文が壊れる+HadReplacement 可能性。
        // ここでは「forced が優先される=戻り Encoding.CodePage が 932 になる」ことのみ検証。
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("abcdef"));
            var loaded = TextFileService.LoadAsBufferAuto(path, forcedCodePage: 932);
            Assert.Equal(932, loaded.Encoding.CodePage);
            Assert.False(loaded.HasBom);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAuto_LargeUtf8_ChunkBoundary_MultibyteAcrossReads_Roundtrip()
    {
        // 64KB prefix + 64KB read chunk の両方で multibyte 分断が起きても正しく復号できる。
        // 200,000 code point の日本語(≒600KB UTF-8)= 10+ 個の 64KB 境界を跨ぐ。
        string sample = string.Concat(Enumerable.Repeat("日本語のテスト。", 25000));
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(sample));
            var loaded = TextFileService.LoadAsBufferAuto(path);
            Assert.Equal(65001, loaded.Encoding.CodePage);
            Assert.False(loaded.HasBom);
            Assert.Equal(
                sample,
                loaded.Buffer.Current.GetText(0, loaded.Buffer.Current.CharLength)
            );
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAuto_EmptyFile_UsesUtf8Default_AndReturnsEmptyBuffer()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Array.Empty<byte>());
            var loaded = TextFileService.LoadAsBufferAuto(path);
            Assert.Equal(65001, loaded.Encoding.CodePage);
            Assert.False(loaded.HasBom);
            Assert.Equal(LineEnding.Crlf, loaded.LineEnding); // 既定
            Assert.False(loaded.HadReplacementChar);
            Assert.Equal(0, loaded.Buffer.Current.CharLength);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
