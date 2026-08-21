using System.Text;
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Tests.Buffers;

public class TextChunkTests
{
    /// <summary>ASCII/CJK/絵文字/CR/LF/CRLF混在の約1KB文書。</summary>
    private static string BuildMixedDoc()
    {
        var sb = new StringBuilder();
        while (sb.Length < 1024)
            sb.Append("ABC abc 123\r\nあいう漢字カナ\r😀🈴えお\n\nx\rline\n");
        return sb.ToString();
    }

    /// <summary>サロゲートペアを割らない文字インデックス(=コード点境界)の一覧。</summary>
    private static List<int> CharBoundaries(string s)
    {
        var list = new List<int>();
        for (int i = 0; i <= s.Length; i++)
            if (i == s.Length || !char.IsLowSurrogate(s[i]))
                list.Add(i);
        return list;
    }

    private static int ByteOff(string s, int charIndex) =>
        Encoding.UTF8.GetByteCount(s.AsSpan(0, charIndex));

    private static void AssertStatsEqual(byte[] slice, PieceStats actual)
    {
        var (charLen, breaks, firstIsLf, lastIsCr) = Utf8Scan.Stats(slice);
        Assert.Equal(slice.Length, actual.ByteLen);
        Assert.Equal(charLen, actual.CharLen);
        Assert.Equal(breaks, actual.Breaks);
        Assert.Equal(firstIsLf, actual.FirstIsLf);
        Assert.Equal(lastIsCr, actual.LastIsCr);
    }

    [Fact]
    public void StatsOfRange_whole_chunk_matches_scan_with_tiny_grid()
    {
        string doc = BuildMixedDoc();
        byte[] bytes = Encoding.UTF8.GetBytes(doc);
        var chunk = new TextChunk(bytes, gridBytes: 8);
        AssertStatsEqual(bytes, chunk.StatsOfRange(0, bytes.Length));
    }

    [Fact]
    public void StatsOfRange_random_subranges_match_scan()
    {
        string doc = BuildMixedDoc();
        byte[] bytes = Encoding.UTF8.GetBytes(doc);
        var chunk = new TextChunk(bytes, gridBytes: 8);
        var bounds = CharBoundaries(doc);
        var rnd = new Random(20260705);
        for (int t = 0; t < 100; t++)
        {
            int i = bounds[rnd.Next(bounds.Count)],
                j = bounds[rnd.Next(bounds.Count)];
            if (i > j)
                (i, j) = (j, i);
            int a = ByteOff(doc, i),
                b = ByteOff(doc, j);
            AssertStatsEqual(bytes[a..b], chunk.StatsOfRange(a, b - a));
        }
    }

    [Fact]
    public void StatsOfRange_crlf_straddling_boundaries()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("x\r\ny");
        var chunk = new TextChunk(bytes, gridBytes: 8);
        Assert.Equal(1, chunk.StatsOfRange(2, 2).Breaks); // "\ny"
        Assert.Equal(1, chunk.StatsOfRange(0, 2).Breaks); // "x\r"
        Assert.Equal(1, chunk.StatsOfRange(0, 4).Breaks); // 全体 CRLF=1
    }

    [Fact]
    public void CharToByte_matches_encoding_byte_count()
    {
        string doc = "a😀bあ\r\n亜😀x\rありがとう🈴z";
        byte[] bytes = Encoding.UTF8.GetBytes(doc);
        var chunk = new TextChunk(bytes, gridBytes: 8);
        foreach (int k in CharBoundaries(doc))
            Assert.Equal(ByteOff(doc, k), chunk.CharToByte(0, bytes.Length, k));
    }

    [Fact]
    public void CharToByte_on_subrange_uses_relative_char_delta()
    {
        string doc = BuildMixedDoc();
        byte[] bytes = Encoding.UTF8.GetBytes(doc);
        var chunk = new TextChunk(bytes, gridBytes: 8);
        var bounds = CharBoundaries(doc);
        var rnd = new Random(20260705);
        for (int t = 0; t < 50; t++)
        {
            int i = bounds[rnd.Next(bounds.Count)],
                j = bounds[rnd.Next(bounds.Count)];
            if (i > j)
                (i, j) = (j, i);
            int a = ByteOff(doc, i),
                b = ByteOff(doc, j);
            // 範囲 [a,b) の先頭から (j-i) UTF-16単位進むと範囲末尾
            Assert.Equal(b, chunk.CharToByte(a, b - a, j - i));
        }
    }

    [Fact]
    public void NthBreakEndChar_fixed_document()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("a\nb\r\nc\rd");
        var chunk = new TextChunk(bytes, gridBytes: 8);
        int len = bytes.Length;
        Assert.Equal(1, chunk.NthBreakEndChar(0, len, 1)); // \n
        Assert.Equal(4, chunk.NthBreakEndChar(0, len, 2)); // CRLFのLF
        Assert.Equal(6, chunk.NthBreakEndChar(0, len, 3)); // 単独\r
    }

    [Fact]
    public void NthBreakEndChar_trailing_cr_of_range_is_an_end()
    {
        // 範囲がCRLFの間で切れる場合: 範囲内ではCRが単独扱い=終端
        byte[] bytes = Encoding.UTF8.GetBytes("a\r\nb");
        var chunk = new TextChunk(bytes, gridBytes: 8);
        Assert.Equal(1, chunk.NthBreakEndChar(0, 2, 1)); // "a\r" → \r が終端
        Assert.Equal(0, chunk.NthBreakEndChar(2, 2, 1)); // "\nb" → \n が終端
    }

    [Fact]
    public void NthBreakEndChar_counts_utf16_offsets_with_multibyte()
    {
        string doc = "あ😀\nい\r";
        byte[] bytes = Encoding.UTF8.GetBytes(doc);
        var chunk = new TextChunk(bytes, gridBytes: 4);
        Assert.Equal(3, chunk.NthBreakEndChar(0, bytes.Length, 1)); // あ(1)+😀(2)の直後
        Assert.Equal(5, chunk.NthBreakEndChar(0, bytes.Length, 2)); // 末尾\r
    }

    [Fact]
    public void Grid_snap_across_3byte_chars_does_not_break_conversion()
    {
        string doc = "あいうえお"; // 3バイト×5(格子幅3の格子点がコード点境界と衝突/中間の両方を踏む)
        byte[] bytes = Encoding.UTF8.GetBytes(doc);
        var chunk = new TextChunk(bytes, gridBytes: 3);
        for (int k = 0; k <= 5; k++)
            Assert.Equal(3 * k, chunk.CharToByte(0, bytes.Length, k));
        var chunk2 = new TextChunk(bytes, gridBytes: 2); // 格子点が必ずコード点中間を踏む
        for (int k = 0; k <= 5; k++)
            Assert.Equal(3 * k, chunk2.CharToByte(0, bytes.Length, k));
    }

    [Fact]
    public void SplitStats_matches_naive_prefix_scan()
    {
        string doc = BuildMixedDoc();
        byte[] bytes = Encoding.UTF8.GetBytes(doc);
        var chunk = new TextChunk(bytes, gridBytes: 8);
        var bounds = CharBoundaries(doc);
        var rnd = new Random(20260705);
        for (int t = 0; t < 200; t++)
        {
            int i = bounds[rnd.Next(bounds.Count)],
                j = bounds[rnd.Next(bounds.Count)];
            if (i > j)
                (i, j) = (j, i);
            int a = ByteOff(doc, i),
                b = ByteOff(doc, j);
            // 範囲内のランダムな境界 k まで
            int k = i;
            foreach (int cand in bounds)
                if (cand >= i && cand <= j && rnd.Next(4) == 0)
                {
                    k = cand;
                    break;
                }
            var (byteMid, prefix) = chunk.SplitStats(a, b - a, k - i);
            Assert.Equal(ByteOff(doc, k), byteMid);
            AssertStatsEqual(bytes[a..byteMid], prefix);
        }
    }

    [Fact]
    public void SplitStats_snaps_low_at_surrogate_middle()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("a😀b");
        var chunk = new TextChunk(bytes, gridBytes: 4);
        var (byteMid, prefix) = chunk.SplitStats(0, bytes.Length, 2); // ペア中間
        Assert.Equal(1, byteMid); // "a" の直後へスナップ
        Assert.Equal(1, prefix.CharLen);
    }

    [Fact]
    public void SplitStats_crlf_edge_conventions()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("x\r\ny\r");
        var chunk = new TextChunk(bytes, gridBytes: 2);
        // 接頭辞 "x\r" (CRLFの途中で切る) → 末尾CRは単独扱いで1 break
        var (mid1, p1) = chunk.SplitStats(0, bytes.Length, 2);
        Assert.Equal(2, mid1);
        Assert.Equal(1, p1.Breaks);
        Assert.True(p1.LastIsCr);
        // 範囲 [2,5)="\ny\r" の接頭辞 "\ny" → 1 break
        var (mid2, p2) = chunk.SplitStats(2, 3, 2);
        Assert.Equal(4, mid2);
        Assert.Equal(1, p2.Breaks);
        Assert.True(p2.FirstIsLf);
    }

    [Fact]
    public void GetString_roundtrips()
    {
        string doc = BuildMixedDoc();
        byte[] bytes = Encoding.UTF8.GetBytes(doc);
        var chunk = new TextChunk(bytes, gridBytes: 16);
        Assert.Equal(doc, chunk.GetString(0, bytes.Length));
        int a = ByteOff(doc, 5),
            b = ByteOff(doc, 20);
        Assert.Equal(doc[5..20], chunk.GetString(a, b - a));
    }
}
