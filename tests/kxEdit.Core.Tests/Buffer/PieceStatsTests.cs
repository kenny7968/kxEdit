using System.Text;
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Tests.Buffers;

public class PieceStatsTests
{
    private static PieceStats StatsOf(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        var (charLen, breaks, firstIsLf, lastIsCr) = Utf8Scan.Stats(bytes);
        return new PieceStats(bytes.Length, charLen, breaks, firstIsLf, lastIsCr);
    }

    // §1 検算表: 連結時の改行モノイド
    [Theory]
    [InlineData("a\r", "\nb", 1)] // 1+1−1: CR+LF が融合して CRLF=1
    [InlineData("\r", "\r\n", 2)] // \r\r\n → CR + CRLF = 2
    [InlineData("\n", "\n", 2)] // \n\n → 2
    [InlineData("x", "\r", 1)] // 単純結合
    public void Combine_matches_full_scan(string a, string b, int expectedBreaks)
    {
        var combined = PieceStats.Combine(StatsOf(a), StatsOf(b));
        Assert.Equal(expectedBreaks, combined.Breaks);
        Assert.Equal(StatsOf(a + b), combined);
    }

    [Fact]
    public void Combine_with_empty_is_identity()
    {
        var s = StatsOf("a\r\nb\r");
        Assert.Equal(s, PieceStats.Combine(s, PieceStats.Empty));
        Assert.Equal(s, PieceStats.Combine(PieceStats.Empty, s));
        Assert.Equal(PieceStats.Empty, PieceStats.Combine(PieceStats.Empty, PieceStats.Empty));
    }

    [Fact]
    public void Combine_is_associative_over_random_splits()
    {
        const string s = "ab\r\ncd\r\r\n\n\ref😀\nぁ\r";
        var rnd = new Random(42);
        var bounds = new List<int>();
        for (int i = 0; i <= s.Length; i++)
            if (i == s.Length || !char.IsLowSurrogate(s[i]))
                bounds.Add(i);
        for (int t = 0; t < 200; t++)
        {
            int i = bounds[rnd.Next(bounds.Count)],
                j = bounds[rnd.Next(bounds.Count)];
            if (i > j)
                (i, j) = (j, i);
            var a = StatsOf(s[..i]);
            var b = StatsOf(s[i..j]);
            var c = StatsOf(s[j..]);
            var left = PieceStats.Combine(PieceStats.Combine(a, b), c);
            var right = PieceStats.Combine(a, PieceStats.Combine(b, c));
            Assert.Equal(left, right);
            Assert.Equal(StatsOf(s), left); // 常に全体統計と一致
        }
    }

    [Fact]
    public void Piece_of_computes_stats_from_chunk()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("a\r\nbあ😀\r");
        var chunk = new TextChunk(bytes, gridBytes: 4);
        var piece = Piece.Of(chunk, 0, bytes.Length);
        Assert.Equal(chunk.StatsOfRange(0, bytes.Length), piece.Stats);
        Assert.Equal(piece.Stats.CharLen, piece.CharLen);
        // 部分ピース
        var sub = Piece.Of(chunk, 1, 2); // "\r\n"
        Assert.Equal(1, sub.Stats.Breaks);
        Assert.Equal(2, sub.CharLen);
    }
}
