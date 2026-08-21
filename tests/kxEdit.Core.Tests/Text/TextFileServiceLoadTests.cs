using System.Text;
using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

public class TextFileServiceLoadTests
{
    private const string Jp = "一行目\r\n二行目\r\n";

    [Fact]
    public void Loads_utf8_no_bom_roundtrip()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(Jp));
            var doc = TextFileService.Load(path);
            Assert.Equal(Jp, doc.Text);
            Assert.Equal(65001, doc.Encoding.CodePage);
            Assert.Equal(LineEnding.Crlf, doc.LineEnding);
            Assert.False(doc.HadReplacementChar);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Loads_utf8_with_bom_strips_preamble()
    {
        string path = Path.GetTempFileName();
        try
        {
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            File.WriteAllBytes(path, bom.Concat(Encoding.UTF8.GetBytes(Jp)).ToArray());
            var doc = TextFileService.Load(path);
            // 先頭に U+FEFF（BOM 文字）が残らず、本文が正しく取れること。
            Assert.Equal(Jp, doc.Text);
            Assert.Equal(65001, doc.Encoding.CodePage);
            Assert.True(doc.HasBom);
            Assert.False(doc.HadReplacementChar);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Loads_shift_jis_with_explicit_codepage()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, EncodingCatalog.Get(932).GetBytes(Jp));
            var doc = TextFileService.Load(path, forcedCodePage: 932);
            Assert.Equal(Jp, doc.Text);
            Assert.Equal(932, doc.Encoding.CodePage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Wrong_encoding_flags_replacement_char()
    {
        string path = Path.GetTempFileName();
        try
        {
            // Shift_JIS バイトを UTF-8 として強制読み → 置換文字混入
            File.WriteAllBytes(path, EncodingCatalog.Get(932).GetBytes(Jp));
            var doc = TextFileService.Load(path, forcedCodePage: 65001);
            Assert.True(doc.HadReplacementChar);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
