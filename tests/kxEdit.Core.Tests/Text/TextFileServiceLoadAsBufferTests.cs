using System.Text;
using kxEdit.Core.Buffers;
using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

public class TextFileServiceLoadAsBufferTests
{
    private const string Jp = "一行目\r\n二行目\r\n";

    [Fact]
    public void LoadAsBuffer_Utf8_NoBom_Roundtrip()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(Jp));
            var (buf, hadReplacement) = TextFileService.LoadAsBuffer(
                path,
                new UTF8Encoding(false),
                hasBom: false
            );
            Assert.Equal(Jp, buf.Current.GetText(0, buf.Current.CharLength));
            Assert.False(hadReplacement);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAsBuffer_Utf8_WithBom_StripsPreamble()
    {
        string path = Path.GetTempFileName();
        try
        {
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            File.WriteAllBytes(path, bom.Concat(Encoding.UTF8.GetBytes(Jp)).ToArray());
            var (buf, hadReplacement) = TextFileService.LoadAsBuffer(
                path,
                new UTF8Encoding(false),
                hasBom: true
            );
            Assert.Equal(Jp, buf.Current.GetText(0, buf.Current.CharLength));
            Assert.False(hadReplacement);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAsBuffer_ShiftJis_Roundtrip()
    {
        string path = Path.GetTempFileName();
        try
        {
            var enc = EncodingCatalog.Get(932);
            File.WriteAllBytes(path, enc.GetBytes(Jp));
            var (buf, hadReplacement) = TextFileService.LoadAsBuffer(path, enc);
            Assert.Equal(Jp, buf.Current.GetText(0, buf.Current.CharLength));
            Assert.False(hadReplacement);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAsBuffer_EucJp_Roundtrip()
    {
        string path = Path.GetTempFileName();
        try
        {
            var enc = EncodingCatalog.Get(51932);
            File.WriteAllBytes(path, enc.GetBytes(Jp));
            var (buf, hadReplacement) = TextFileService.LoadAsBuffer(path, enc);
            Assert.Equal(Jp, buf.Current.GetText(0, buf.Current.CharLength));
            Assert.False(hadReplacement);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAsBuffer_Utf8_ChunkBoundary_MultibyteAcrossReads_Roundtrip()
    {
        // P6 Task 7 M-3: 64KB Stream.Read チャンクの境界で日本語(3-byte UTF-8)が
        // 分断されても TextBufferBuilder の _carry が吸収して正しく復号することを検証。
        // 200,000 code point の日本語文字列(≒600KB UTF-8) — 10+ 個の 64KB 境界を跨ぐ。
        string sample = string.Concat(Enumerable.Repeat("日本語のテスト。", 25000));
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(sample));
            var (buf, hadReplacement) = TextFileService.LoadAsBuffer(
                path,
                new UTF8Encoding(false),
                hasBom: false
            );
            Assert.Equal(sample, buf.Current.GetText(0, buf.Current.CharLength));
            Assert.False(hadReplacement);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAsBuffer_Sjis_LargeContent_ChunkedRead()
    {
        // P7 I-4: SJIS 経路を chunk read 化した際、TextBufferBuilder の 4MB チャンク境界を跨ぐ
        // 大容量ファイルでも本文が完全復元されることを固定する(挙動固定テスト=chunk 化前後で PASS)。
        EncodingCatalog.EnsureRegistered();
        // TextBufferBuilder の 4MB チャンク境界を跨ぐ SJIS ファイル
        string body = new string('あ', 3 * 1024 * 1024); // SJIS 6MB
        byte[] sjisBytes = Encoding.GetEncoding(932).GetBytes(body);
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(path, sjisBytes);
            var (buf, hadRepl) = TextFileService.LoadAsBuffer(
                path,
                EncodingCatalog.Get(932),
                hasBom: false
            );
            Assert.False(hadRepl);
            Assert.Equal(body.Length, buf.Current.CharLength);
            Assert.Equal(body.Substring(0, 100), buf.Current.GetText(0, 100));
            Assert.Equal(
                body.Substring(body.Length - 100, 100),
                buf.Current.GetText(body.Length - 100, 100)
            );
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void LoadAsBuffer_EucJp_ChunkedRead_PreservesContent()
    {
        // P7 I-4: EUC-JP 経路も chunk read 化後に本文が完全復元されることを固定する。
        EncodingCatalog.EnsureRegistered();
        string body = "EUC-JP 日本語テスト\nsecond line 日本語\n" + new string('か', 100_000);
        byte[] eucBytes = Encoding.GetEncoding(51932).GetBytes(body);
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(path, eucBytes);
            var (buf, hadRepl) = TextFileService.LoadAsBuffer(
                path,
                EncodingCatalog.Get(51932),
                hasBom: false
            );
            Assert.False(hadRepl);
            Assert.Equal(body.Length, buf.Current.CharLength);
            Assert.Equal(body, buf.Current.GetText(0, buf.Current.CharLength));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void LoadAsBuffer_Sjis_InvalidBytes_ReportsReplacement()
    {
        // P7 I-4: chunk read 経路でも DecoderReplacementFallback("�") が正しく通り、
        // 不正 SJIS バイトが U+FFFD で埋められて hadReplacement=true が返ることを固定する。
        EncodingCatalog.EnsureRegistered();
        // 不正な SJIS バイト(0x81 0x00 = 第2バイト範囲外)
        byte[] bytes = new byte[] { 0x81, 0x00, 0x82, 0xA0 };
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(path, bytes);
            var (_, hadRepl) = TextFileService.LoadAsBuffer(
                path,
                EncodingCatalog.Get(932),
                hasBom: false
            );
            Assert.True(hadRepl);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void LoadAsBuffer_ShiftJis_InvalidBytes_ReportHadReplacement()
    {
        // P6 Task 7 I-1: SJIS デコード失敗は U+FFFD へ落ちて hadReplacement=true を返す
        // (v1 DecodeBytes と契約一致=既定 DecoderFallback は '?'(U+003F)へ落として静かに読ませてしまう)。
        // 注: 単純に「UTF-8 バイトを SJIS 読み」ではダメで、CP932 の LeadByte 範囲(0x81-0x9F/0xE0-0xFC)
        //     に UTF-8 の 3-byte 先頭がほぼ嵌ってしまい、対応する SJIS 実文字にマップされてしまう。
        //     さらに未定義の LeadByte+TrailByte 対は Windows Best Fit で PUA(U+F8F3)へ落ちるため、
        //     DecoderFallback 経由を確実に踏むには LeadByte(0x81)に無効な TrailByte(0x00)を続けた
        //     構文レベルの不正バイト列を渡す。
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[] { 0x81, 0x00, 0x81, 0x00 });
            var (_, hadReplacement) = TextFileService.LoadAsBuffer(path, EncodingCatalog.Get(932));
            Assert.True(hadReplacement);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
