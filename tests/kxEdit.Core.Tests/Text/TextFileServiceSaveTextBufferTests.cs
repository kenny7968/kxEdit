using System.Text;
using kxEdit.Core.Buffers;
using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

public class TextFileServiceSaveTextBufferTests
{
    private const string Jp = "一行目\r\n二行目\r\n";

    [Fact]
    public void SaveBuffer_Utf8_NoBom_Roundtrip()
    {
        string path = Path.GetTempFileName();
        try
        {
            var buf = TextBuffer.FromString(Jp);
            TextFileService.Save(path, buf, new UTF8Encoding(false), hasBom: false);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(Encoding.UTF8.GetBytes(Jp), bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveBuffer_Utf8_WithBom_WritesPreamble()
    {
        string path = Path.GetTempFileName();
        try
        {
            var buf = TextBuffer.FromString(Jp);
            TextFileService.Save(path, buf, new UTF8Encoding(false), hasBom: true);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal(0xBB, bytes[1]);
            Assert.Equal(0xBF, bytes[2]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveBuffer_ShiftJis_Roundtrip()
    {
        string path = Path.GetTempFileName();
        try
        {
            var buf = TextBuffer.FromString(Jp);
            var enc = EncodingCatalog.Get(932);
            TextFileService.Save(path, buf, enc, hasBom: false);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(enc.GetBytes(Jp), bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveBuffer_EucJp_Roundtrip()
    {
        string path = Path.GetTempFileName();
        try
        {
            var buf = TextBuffer.FromString(Jp);
            var enc = EncodingCatalog.Get(51932);
            TextFileService.Save(path, buf, enc, hasBom: false);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(enc.GetBytes(Jp), bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveTextBuffer_Utf8_LargeContent_WritesExactBytes()
    {
        // 5MB(TextBufferBuilder のチャンク境界 4MB を跨ぐ)
        var body = new string('あ', 5 * 1024 * 1024 / 3); // UTF-8 で 5MB 近辺
        var buffer = TextBuffer.FromString(body);
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            TextFileService.Save(path, buffer, Encoding.UTF8, hasBom: false);
            byte[] actual = File.ReadAllBytes(path);
            byte[] expected = Encoding.UTF8.GetBytes(body);
            Assert.Equal(expected.Length, actual.Length);
            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void SaveTextBuffer_Utf8_WithBom_EmitsPreamble()
    {
        var buffer = TextBuffer.FromString("hello");
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            TextFileService.Save(path, buffer, Encoding.UTF8, hasBom: true);
            byte[] actual = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, actual.Take(3).ToArray());
            Assert.Equal("hello", Encoding.UTF8.GetString(actual, 3, actual.Length - 3));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void SaveTextBuffer_Sjis_LargeContent_WritesExactBytes()
    {
        EncodingCatalog.EnsureRegistered();
        var body = new string('あ', 100_000); // SJIS で 200KB
        var buffer = TextBuffer.FromString(body);
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            TextFileService.Save(path, buffer, EncodingCatalog.Get(932), hasBom: false);
            byte[] actual = File.ReadAllBytes(path);
            byte[] expected = Encoding.GetEncoding(932).GetBytes(body);
            Assert.Equal(expected.Length, actual.Length);
            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void SaveTextBuffer_EucJp_WritesExactBytes()
    {
        EncodingCatalog.EnsureRegistered();
        string body = "日本語テキスト EUC-JP\nsecond line\n";
        var buffer = TextBuffer.FromString(body);
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            TextFileService.Save(path, buffer, EncodingCatalog.Get(51932), hasBom: false);
            byte[] actual = File.ReadAllBytes(path);
            byte[] expected = Encoding.GetEncoding(51932).GetBytes(body);
            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void SaveTextBuffer_EmptyBuffer_WritesZeroBytes()
    {
        var buffer = TextBuffer.FromString("");
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            TextFileService.Save(path, buffer, Encoding.UTF8, hasBom: false);
            Assert.Equal(0, new FileInfo(path).Length);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void SaveTextBuffer_EmptyBuffer_WithBom_WritesOnlyPreamble()
    {
        var buffer = TextBuffer.FromString("");
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            TextFileService.Save(path, buffer, Encoding.UTF8, hasBom: true);
            byte[] actual = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, actual);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void SaveTextBuffer_Utf8_SurrogatePairSpansCharBufferBoundary_EmitsCorrectBytes()
    {
        // CharBufLen(8192)境界にサロゲート対の中央を強制配置=Encoder.Convert(flush:false)の
        // 状態持ち越しが正しく機能することの回帰テスト。
        // 前半 8191 個の 'a' + U+1F600(高サロゲート D83D+低サロゲート DE00)= 8193 char
        // ちょうど 8191+1=8192 で最初の Read(char[])に高サロゲートまで、次の Read で低サロゲートが来る。
        // 注: UTF-8 経路(codepage 65001)は snap.WriteTo(stream) の byte 直書きで Encoder を経由しない
        //     ため、この境界問題は原理的に発生しない=期待挙動の回帰テスト(トリビアルに PASS する)。
        string body = new string('a', 8191) + "😀" + new string('b', 100);
        var buffer = TextBuffer.FromString(body);
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            TextFileService.Save(path, buffer, Encoding.UTF8, hasBom: false);
            byte[] actual = File.ReadAllBytes(path);
            byte[] expected = Encoding.UTF8.GetBytes(body);
            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void SaveTextBuffer_Sjis_SurrogatePairSpansCharBufferBoundary_FallsBackToSingleReplacement()
    {
        // SJIS/EUC-JP 経路(Encoder.Convert(flush:false)ループ)のサロゲート境界回帰テスト。
        // U+1F600(😀・SMP)は SJIS 定義域外=EncoderFallback.ReplacementFallback で '?' 1 個に置換される。
        // ★ Encoder が状態を持ち越さないと、8192 char バッファ境界で分断されたサロゲート対の
        //    高サロゲート・低サロゲートがそれぞれ「無効」として '?' '?' の 2 個に置換される。
        // ★ 状態を持ち越して pair 完成後に単一コードポイントとして fallback すると '?' 1 個のみ。
        // 8191 個の 'a' + '😀' で高サロゲートが CharBufLen=8192 の最後、低サロゲートが次 Read の先頭に来る配置。
        EncodingCatalog.EnsureRegistered();
        string body = new string('a', 8191) + "😀" + new string('b', 100);
        var buffer = TextBuffer.FromString(body);
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            TextFileService.Save(path, buffer, EncodingCatalog.Get(932), hasBom: false);
            byte[] actual = File.ReadAllBytes(path);
            // 期待値: 現行 .NET Encoding.GetEncoding(932, EncoderFallback.ReplacementFallback, ...) の
            // string 経路と同一挙動になるはず(Encoder 状態が正しく持ち越されている場合)。
            byte[] expected = Encoding
                .GetEncoding(
                    932,
                    EncoderFallback.ReplacementFallback,
                    DecoderFallback.ReplacementFallback
                )
                .GetBytes(body);
            Assert.Equal(expected, actual);
            // 追加保証: '?' の個数が期待値と一致(サロゲート対は 1 個の '?' に融合されるべき)
            int expectedQMark = expected.Count(b => b == (byte)'?');
            int actualQMark = actual.Count(b => b == (byte)'?');
            Assert.Equal(expectedQMark, actualQMark);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void SaveTextBuffer_ShareViolation_FallsBackToInPlaceOverwrite()
    {
        // 共有違反 catch 経路(TextBuffer 版 Save → string 版 Save 委譲)が動作することの直接検証。
        // ターゲットを FileShare.None で握って File.Replace を失敗させる=SharingViolation IOException が
        // TextBuffer 版 Save の catch (when IsShareOrLockViolation) に届くはず。
        // fallback 先の string 版 Save も同 FileShare.None のため WriteAllBytes で失敗=最終的に IOException
        // が伝播する(原本は変更されない=原子性契約温存)。
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "old");
            var buffer = TextBuffer.FromString("new-content");
            using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Throws<IOException>(() =>
                    TextFileService.Save(path, buffer, Encoding.UTF8, hasBom: false)
                );
            }
            // hold 解放後、原本が変更されていない(fallback も含めて完全に失敗した=原本喪失回避)
            Assert.Equal("old", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
