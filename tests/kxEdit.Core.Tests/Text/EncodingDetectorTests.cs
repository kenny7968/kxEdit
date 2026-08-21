using System.Text;
using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

public class EncodingDetectorTests
{
    private const string Jp = "日本語のテスト。ABC 123 半角と　全角。";

    [Fact]
    public void Detects_utf8_bom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes(Jp))
            .ToArray();
        var r = EncodingDetector.Detect(bytes);
        Assert.Equal(65001, r.CodePage);
        Assert.True(r.HasBom);
    }

    [Fact]
    public void Detects_utf8_no_bom()
    {
        var r = EncodingDetector.Detect(Encoding.UTF8.GetBytes(Jp));
        Assert.Equal(65001, r.CodePage);
        Assert.False(r.HasBom);
    }

    [Fact]
    public void Detects_shift_jis()
    {
        var sjis = EncodingCatalog.Get(932);
        var r = EncodingDetector.Detect(sjis.GetBytes(Jp));
        Assert.Equal(932, r.CodePage);
    }

    [Fact]
    public void Detects_euc_jp()
    {
        var euc = EncodingCatalog.Get(51932);
        var r = EncodingDetector.Detect(euc.GetBytes(Jp));
        Assert.Equal(51932, r.CodePage);
    }

    [Fact]
    public void Utf16_le_bom_no_longer_detected_as_utf16()
    {
        // P6 Task 6: UTF-16 は非対応=BOM も検出しない。fallback 経路(UTF-8 strict → charset detect → SJIS)へ落ちる。
        var r = EncodingDetector.Detect(
            Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Jp)).ToArray()
        );
        Assert.Equal(932, r.CodePage); // fallback = SJIS(§P6 Task 6 レビュー M-3)
    }

    [Fact]
    public void Utf16_be_bom_no_longer_detected_as_utf16()
    {
        // P6 Task 6: UTF-16 BE も検出しない=fallback 経路(strict UTF-8 失敗→charset detect→SJIS)へ落ちる。
        var r = EncodingDetector.Detect(
            Encoding
                .BigEndianUnicode.GetPreamble()
                .Concat(Encoding.BigEndianUnicode.GetBytes(Jp))
                .ToArray()
        );
        Assert.Equal(932, r.CodePage); // fallback = SJIS(§P6 Task 6 レビュー M-3)
    }

    [Fact]
    public void Empty_defaults_to_utf8()
    {
        var r = EncodingDetector.Detect(Array.Empty<byte>());
        Assert.Equal(65001, r.CodePage);
    }

    [Fact]
    public void Utf8Bom_is_detected_before_utf_unknown_would_pick_ambiguous()
    {
        // CSV-L-6 (v0.11): 攻撃者制御バイト列で UtfUnknown を誤判定させても、BOM が
        // ある限り UTF-8 (65001, HasBom=true) を返すという invariant を固定する。
        // 前提: 同じ tail bytes (BOM なし) を単体で渡すと SJIS(932) として判定される
        // (Detects_shift_jis で確認済) — したがって BOM 前置で UTF-8 になる事実が
        // 「BOM check が UtfUnknown より前に走っている」ことの回帰保護になる。
        var sjis = EncodingCatalog.Get(932);
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(sjis.GetBytes(Jp))
            .ToArray();

        var r = EncodingDetector.Detect(bytes);

        Assert.Equal(65001, r.CodePage);
        Assert.True(r.HasBom);
    }

    [Fact]
    public void Utf8Bom_priority_survives_short_input()
    {
        // CSV-L-6 (v0.11): BOM 単体 (3 バイト) や BOM+1 バイトなど極端に短い入力でも
        // BOM 検出が最優先で機能する。BOM check を外して strict UTF-8 が先に走ると
        // (BOM 自体は valid UTF-8 = U+FEFF なので) HasBom=false が返り、この test が
        // 落ちる = 回帰保護。
        var bomOnly = new byte[] { 0xEF, 0xBB, 0xBF };
        var rBomOnly = EncodingDetector.Detect(bomOnly);
        Assert.Equal(65001, rBomOnly.CodePage);
        Assert.True(rBomOnly.HasBom);

        var bomPlusOne = new byte[] { 0xEF, 0xBB, 0xBF, 0x41 }; // BOM + 'A'
        var rBomPlusOne = EncodingDetector.Detect(bomPlusOne);
        Assert.Equal(65001, rBomPlusOne.CodePage);
        Assert.True(rBomPlusOne.HasBom);
    }
}
