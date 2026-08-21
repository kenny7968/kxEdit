using kxEdit.Core.Buffers;
using kxEdit.Core.Documents;
using Xunit;

namespace kxEdit.Core.Tests.Documents;

/// <summary>CharacterCounter の境界網羅テスト。CRLF/LF/CR、半角/タブ/全角スペース/NBSP、
/// サロゲートペアの取り扱いを固定する。実装は TextSnapshot.CreateReader() 経由で
/// peak O(piece) を維持する(全文 string を実体化しない)。
/// 不可視文字は誤読・誤編集を避けるため \uXXXX エスケープで書く。</summary>
public class CharacterCounterTests
{
    private static TextSnapshot Snap(string s) => TextBuffer.FromString(s).Current;

    [Fact]
    public void Empty_document_counts_zero() =>
        Assert.Equal(0, CharacterCounter.CountVisible(Snap("")));

    [Fact]
    public void All_crlf_counts_zero() =>
        Assert.Equal(0, CharacterCounter.CountVisible(Snap("\r\n\r\n\r\n")));

    [Fact]
    public void All_lf_counts_zero() =>
        Assert.Equal(0, CharacterCounter.CountVisible(Snap("\n\n\n")));

    [Fact]
    public void All_cr_counts_zero() =>
        Assert.Equal(0, CharacterCounter.CountVisible(Snap("\r\r\r")));

    [Fact]
    public void Half_width_spaces_only_count_zero() =>
        Assert.Equal(0, CharacterCounter.CountVisible(Snap("   ")));

    [Fact]
    public void Tabs_only_count_zero() =>
        Assert.Equal(0, CharacterCounter.CountVisible(Snap("\t\t\t")));

    /// <summary>全角スペース(U+3000)も White_Space として除外する。</summary>
    [Fact]
    public void Full_width_spaces_only_count_zero() =>
        Assert.Equal(0, CharacterCounter.CountVisible(Snap(new string((char)0x3000, 3))));

    /// <summary>設計 §4「その他 Unicode White_Space(NBSP 等も一律除外)」の pin。
    /// NBSP(U+00A0)は char.IsWhiteSpace / Rune.IsWhiteSpace のどちらでも true=
    /// 「Unicode White_Space を一律除外する」判断そのものを固定する。</summary>
    [Fact]
    public void Non_breaking_spaces_only_count_zero() =>
        Assert.Equal(0, CharacterCounter.CountVisible(Snap(new string((char)0x00A0, 2))));

    [Fact]
    public void Ascii_letters_counted() =>
        Assert.Equal(3, CharacterCounter.CountVisible(Snap("abc")));

    [Fact]
    public void Whitespace_mixed_in_letters_excluded() =>
        Assert.Equal(3, CharacterCounter.CountVisible(Snap("a b\tc")));

    [Fact]
    public void Line_break_between_content_excluded() =>
        // "行1\r\n行2" = 4 visible chars(改行 2 コードユニットを除外)
        Assert.Equal(4, CharacterCounter.CountVisible(Snap("行1\r\n行2")));

    [Fact]
    public void Surrogate_pair_counts_as_one() =>
        // U+29E3D(ホッケ)=UTF-16 で 2 コードユニット、Rune 1 個
        Assert.Equal(1, CharacterCounter.CountVisible(Snap("𩸽")));

    [Fact]
    public void Multiple_surrogate_pairs_with_space() =>
        // U+29E3D 2 個 + 半角空白 1 個(除外)
        Assert.Equal(2, CharacterCounter.CountVisible(Snap("𩸽 𩸽")));

    /// <summary>孤立サロゲートはバッファ層(UTF-8 バックエンド)が U+FFFD へ正規化するため
    /// TextSnapshot には到達しない。U+FFFD は White_Space ではないので 1 文字として数える
    /// =「a」+「U+FFFD」で 2。CountVisible 側の不正シーケンス guard は
    /// TextSnapshot 経由では到達不能な防御であることを、この境界で明示的に固定する。</summary>
    [Fact]
    public void Unpaired_high_surrogate_is_normalized_to_replacement_char_by_buffer()
    {
        var snap = Snap("a\uD867");
        Assert.Equal("a" + (char)0xFFFD, snap.GetText(0, snap.CharLength)); // 前提: バッファ層の正規化
        Assert.Equal(2, CharacterCounter.CountVisible(snap));
    }

    [Fact]
    public void Cjk_and_ascii_mixed() =>
        // "日本語 abc" = 3 CJK + 空白除外 + 3 ASCII = 6
        Assert.Equal(6, CharacterCounter.CountVisible(Snap("日本語 abc")));
}
