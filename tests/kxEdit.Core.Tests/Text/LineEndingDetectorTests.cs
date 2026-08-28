using kxEdit.Core.Buffers;
using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

public class LineEndingDetectorTests
{
    [Theory]
    [InlineData("a\r\nb", LineEnding.Crlf)]
    [InlineData("a\nb", LineEnding.Lf)]
    [InlineData("a\rb", LineEnding.Cr)]
    public void Detects_dominant_line_ending(string text, LineEnding expected) =>
        Assert.Equal(expected, LineEndingDetector.Detect(text));

    [Fact]
    public void Mixed_returns_dominant() =>
        Assert.Equal(LineEnding.Lf, LineEndingDetector.Detect("a\nb\nc\r\nd"));

    [Fact]
    public void No_newline_returns_platform_default() =>
        Assert.Equal(LineEnding.Crlf, LineEndingDetector.Detect("abc"));

    // A-9: snapshot 版と string 版で多数決の意味論が一致すること(走査範囲だけが違う)。
    [Theory]
    [InlineData("a\r\nb")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\nb\nc\r\nd")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("a\r\nb\rc\nd\r\ne")]
    [InlineData("a\r")] // 末尾単独 CR(小さい文書での drain)
    // 多数決の 3 つの >= はいずれも同数のときに効く。同数 fixture が無いと比較演算子の
    // 変異(>= を > へ)が生存するので、3 つの境界を 1 件ずつ当てる(設計書 §7.4)。
    [InlineData("a\r\nb\nc")] // crlf == lf → Crlf
    [InlineData("a\r\nb\rc")] // crlf == cr → Crlf
    [InlineData("a\nb\rc")] // crlf < lf == cr → Lf
    public void Snapshot_overload_matches_string_overload(string text) =>
        Assert.Equal(
            LineEndingDetector.Detect(text),
            LineEndingDetector.Detect(TextBuffer.FromString(text).Current)
        );

    // A-9: CRLF が 4MB チャンク境界を跨いでも CR + LF に割れないこと(pendingCr の持ち越し)。
    // 割れると crlf=1 が cr=1 + lf=1 になり、多数決が Crlf → Lf へ反転する=弁別できる。
    // 改行はこの CRLF 1 つだけにすること。計画案のように末尾へ "tail" + CRLF を足すと、
    // 割れた場合でも crlf=1 / lf=1 / cr=1 で Crlf のままになり網として無意味になる。
    [Fact]
    public void Snapshot_overload_counts_crlf_spanning_chunk_boundary_as_one()
    {
        string body = new string('a', TextBufferBuilder.TargetChunkBytes - 1) + "\r\n";
        var snapshot = TextBuffer.FromString(body).Current;
        AssertTwoPiecesSplitBetween(snapshot, 0x0D, 0x0A); // CR で切れ LF で始まる=境界を跨いでいる
        Assert.Equal(LineEnding.Crlf, LineEndingDetector.Detect(snapshot));
    }

    // A-9: 文書末尾の単独 CR が drain されること(foreach 後の `if (pendingCr) cr++`)。
    // drain を落とすと crlf=lf=cr=0 になり既定の Crlf が返る=Cr と弁別できる。
    [Fact]
    public void Snapshot_overload_counts_trailing_lone_cr()
    {
        string body = new string('a', TextBufferBuilder.TargetChunkBytes) + "\r";
        var snapshot = TextBuffer.FromString(body).Current;
        AssertTwoPiecesSplitBetween(snapshot, 0x61, 0x0D); // 'a' で切れ CR だけの最終ピース
        Assert.Equal(LineEnding.Cr, LineEndingDetector.Detect(snapshot));
    }

    /// <summary>
    /// fixture の前提検証。スナップショットがちょうど 2 ピースで、1 つ目の末尾バイトが
    /// <paramref name="lastOfFirst"/>、2 つ目の先頭バイトが <paramref name="firstOfSecond"/>
    /// であることを確かめる。TextBufferBuilder のチャンク分割規則が変わって境界を跨がなくなった
    /// fixture が黙って通り続けるのを防ぐ。
    /// </summary>
    private static void AssertTwoPiecesSplitBetween(
        TextSnapshot snapshot,
        byte lastOfFirst,
        byte firstOfSecond
    )
    {
        var pieces = PieceTree.Enumerate(snapshot.Root).ToList();
        Assert.Equal(2, pieces.Count);
        Assert.Equal(
            lastOfFirst,
            pieces[0].Chunk.Span[pieces[0].ByteStart + pieces[0].ByteLen - 1]
        );
        Assert.Equal(firstOfSecond, pieces[1].Chunk.Span[pieces[1].ByteStart]);
    }
}
