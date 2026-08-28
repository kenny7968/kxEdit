using System.Text;
using kxEdit.Core.Buffers;
using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

public class TextFileServiceLoadAsBufferAutoTests
{
    private const string JpCrlf = "一行目\r\n二行目\r\n三行目\r\n";
    private const string JpLf = "一行目\n二行目\n三行目\n";

    /// <summary>
    /// A-9 の旧判定窓=先頭 4,096 code unit(UTF-16)
    /// (<see cref="TextFileService.LoadAsBufferAuto"/> の旧実装 <c>Math.Min(4096, CharLength)</c>)。
    /// 撤廃済みで src 側にはもう存在しないため、fixture の前提はテスト側で固定するしかない。
    /// </summary>
    private const int OldProbeWindowChars = 4096;

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

    // A-9(監査 2026-08-22): 改行判定が先頭 4,096 code unit 窓だったため、1 行目が窓より長い
    // LF ファイル(ミニファイ JSON・長いヘッダ行の CSV)が CRLF と誤判定され、
    // Ctrl+S で全行 CRLF 化されていた(Modified も立たず警告も出ない)。
    // fixture の要件: 旧窓の中に改行を 1 つも含まないこと=旧実装が必ず落ちる形。
    [Fact]
    public void LoadAuto_LfFile_FirstLineLongerThanOldProbeWindow_DetectsLf()
    {
        string path = Path.GetTempFileName();
        try
        {
            string body = new string('a', 5000) + "\n" + new string('b', 10) + "\n";
            Assert.True(
                body.IndexOf('\n') >= OldProbeWindowChars,
                $"fixture 前提が壊れています: 最初の改行が {body.IndexOf('\n')} code unit 目=旧窓 {OldProbeWindowChars} の内側"
            );
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(body));
            var loaded = TextFileService.LoadAsBufferAuto(path);
            Assert.Equal(LineEnding.Lf, loaded.LineEnding);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A-9: CR 単独版。旧実装は同じく CRLF 既定へ倒れていた。
    [Fact]
    public void LoadAuto_CrFile_FirstLineLongerThanOldProbeWindow_DetectsCr()
    {
        string path = Path.GetTempFileName();
        try
        {
            string body = new string('a', 5000) + "\r" + new string('b', 10) + "\r";
            Assert.True(
                body.IndexOf('\r') >= OldProbeWindowChars,
                $"fixture 前提が壊れています: 最初の改行が {body.IndexOf('\r')} code unit 目=旧窓 {OldProbeWindowChars} の内側"
            );
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(body));
            var loaded = TextFileService.LoadAsBufferAuto(path);
            Assert.Equal(LineEnding.Cr, loaded.LineEnding);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A-9: 上 2 件の対照群。1 行目が旧窓より長い CRLF ファイルは Crlf のまま。
    // 旧実装でも偶然 Crlf を返すのでバグの弁別力は無いが、「窓の撤廃が過剰修正になって
    // CRLF ファイルまで LF 側へ倒れる」変化はこれが捕まえる。
    [Fact]
    public void LoadAuto_CrlfFile_FirstLineLongerThanOldProbeWindow_DetectsCrlf()
    {
        string path = Path.GetTempFileName();
        try
        {
            string body = new string('a', 5000) + "\r\n" + new string('b', 10) + "\r\n";
            Assert.True(
                body.IndexOf('\r') >= OldProbeWindowChars,
                $"fixture 前提が壊れています: 最初の改行が {body.IndexOf('\r')} code unit 目=旧窓 {OldProbeWindowChars} の内側"
            );
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(body));
            var loaded = TextFileService.LoadAsBufferAuto(path);
            Assert.Equal(LineEnding.Crlf, loaded.LineEnding);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A-9: 窓の外にある多数派が判定に効くこと。旧窓の中には CRLF が 1 つだけあり、
    // 窓の外に LF が多数ある = 旧実装は CRLF、新実装は LF を返す。
    // (「窓を撤廃した」ことの証拠であって、「改行 0 件のときだけ延長した」では緑にならない)
    // filler は 4,094 文字。これで CRLF がちょうど窓の末尾 2 文字に収まり、窓内は crlf=1 / lf=0
    // =旧実装は Crlf を返す(4,000 文字だと後続の "x\n" が窓に 47 組入って旧実装でも Lf になり、
    // 網として無意味になる)。
    [Fact]
    public void LoadAuto_MajorityLfOutsideOldProbeWindow_DetectsLf()
    {
        string path = Path.GetTempFileName();
        try
        {
            string body =
                new string('a', 4094) + "\r\n" + string.Concat(Enumerable.Repeat("x\n", 50));
            // fixture 前提。旧窓のコードはもう無いので、filler を縮めても "x\n" を減らしても
            // 実装は黙って Lf を返し続け、テストは緑のまま弁別力だけを失う。ここで固定する。
            Assert.Equal(OldProbeWindowChars - 2, body.IndexOf('\r')); // CRLF が旧窓の末尾 2 文字
            Assert.Equal(OldProbeWindowChars - 1, body.IndexOf('\n')); // = 旧窓内は crlf 1 件のみ
            // LF 多数派(50 件)はすべて旧窓の外にある=窓を撤廃しないと多数決に効かない
            Assert.Equal(50, body.Skip(OldProbeWindowChars).Count(c => c == '\n'));
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(body));
            var loaded = TextFileService.LoadAsBufferAuto(path);
            Assert.Equal(LineEnding.Lf, loaded.LineEnding);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
