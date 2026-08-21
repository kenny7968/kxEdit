using System.Text;
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Tests.Buffers;

public class PieceTreeLineTests
{
    private static Piece P(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        return Piece.Of(new TextChunk(bytes, gridBytes: 8), 0, bytes.Length);
    }

    private static PieceTree.Node Build(params string[] parts) =>
        PieceTree.BuildBalanced(parts.Select(P).ToArray())!;

    /// <summary>ナイーブ: 全break終端文字(LFまたは単独CR)の文字位置を列挙。</summary>
    private static List<int> NaiveBreakEnds(string s)
    {
        var ends = new List<int>();
        for (int i = 0; i < s.Length; i++)
            if (s[i] == '\n' || (s[i] == '\r' && (i + 1 == s.Length || s[i + 1] != '\n')))
                ends.Add(i);
        return ends;
    }

    private static void AssertAllBreakEnds(string doc, PieceTree.Node root)
    {
        var naive = NaiveBreakEnds(doc);
        Assert.Equal(naive.Count, PieceTree.SumOf(root).Breaks);
        for (int k = 1; k <= naive.Count; k++)
            Assert.Equal(naive[k - 1], PieceTree.NthBreakEnd(root, k, followedByLf: false));
    }

    [Fact]
    public void NthBreakEnd_fixed_document_single_piece()
    {
        const string doc = "a\nb\r\nc\rd\r";
        var root = Build(doc);
        Assert.Equal(1, PieceTree.NthBreakEnd(root, 1, false));
        Assert.Equal(4, PieceTree.NthBreakEnd(root, 2, false));
        Assert.Equal(6, PieceTree.NthBreakEnd(root, 3, false));
        Assert.Equal(8, PieceTree.NthBreakEnd(root, 4, false));
    }

    [Fact]
    public void NthBreakEnd_crlf_straddling_piece_boundaries()
    {
        // "a\r\nb\r\n" を CRLF がピース境界を跨ぐ形で構築
        var root = Build("a\r", "\nb\r", "\n");
        Assert.Equal(2, PieceTree.SumOf(root).Breaks);
        Assert.Equal(2, PieceTree.NthBreakEnd(root, 1, false)); // CRLFのLF
        Assert.Equal(5, PieceTree.NthBreakEnd(root, 2, false)); // CRLFのLF

        // 固定文書をバラバラのピース割りにしても同じ結果
        AssertAllBreakEnds("a\nb\r\nc\rd\r", Build("a\n", "b\r", "\nc\r", "d\r"));
        AssertAllBreakEnds("\r\r\n\n\r", Build("\r", "\r", "\n\n", "\r"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NthBreakEnd_random_newline_heavy_matches_naive(int seed)
    {
        var rnd = new Random(seed);
        for (int trial = 0; trial < 5; trial++)
        {
            string doc = RandomNewlineHeavy(rnd, 500 + rnd.Next(1500));
            var root = BuildRandomPieces(doc, rnd, 5 + rnd.Next(46));
            AssertAllBreakEnds(doc, root);
        }
    }

    [Fact]
    public void PrefixStats_matches_naive_scan_of_prefix()
    {
        var rnd = new Random(20260705);
        string doc =
            "これは1行目\r\n2nd line\nempty\r\r\n\n最終行😀x\r" + RandomNewlineHeavy(rnd, 300);
        var root = BuildRandomPieces(doc, rnd, 12);
        var bounds = new List<int>();
        for (int i = 0; i <= doc.Length; i++)
            if (i == doc.Length || !char.IsLowSurrogate(doc[i]))
                bounds.Add(i);
        foreach (int pos in bounds)
        {
            byte[] prefix = Encoding.UTF8.GetBytes(doc[..pos]);
            var (charLen, breaks, firstIsLf, lastIsCr) = Utf8Scan.Stats(prefix);
            var actual = PieceTree.PrefixStats(root, pos);
            Assert.Equal(prefix.Length, actual.ByteLen);
            Assert.Equal(charLen, actual.CharLen);
            Assert.Equal(breaks, actual.Breaks);
            Assert.Equal(firstIsLf, actual.FirstIsLf);
            Assert.Equal(lastIsCr, actual.LastIsCr);
        }
    }

    [Fact]
    public void PrefixStats_of_empty_tree_and_zero_pos()
    {
        Assert.Equal(PieceStats.Empty, PieceTree.PrefixStats(null, 0));
        var root = Build("abc\r\ndef");
        Assert.Equal(PieceStats.Empty, PieceTree.PrefixStats(root, 0));
        Assert.Equal(PieceTree.SumOf(root), PieceTree.PrefixStats(root, 8));
    }

    private static string RandomNewlineHeavy(Random rnd, int approxLen)
    {
        var sb = new StringBuilder();
        while (sb.Length < approxLen)
        {
            int roll = rnd.Next(10);
            if (roll == 0)
                sb.Append('\r');
            else if (roll == 1)
                sb.Append('\n');
            else if (roll == 2)
                sb.Append("\r\n");
            else
                sb.Append((char)('a' + rnd.Next(26)));
        }
        return sb.ToString();
    }

    private static PieceTree.Node BuildRandomPieces(string s, Random rnd, int pieceCount)
    {
        var cuts = new SortedSet<int> { 0, s.Length };
        int guard = 0;
        while (cuts.Count < pieceCount + 1 && guard++ < 1000)
        {
            int c = rnd.Next(s.Length + 1);
            if (c > 0 && c < s.Length && char.IsLowSurrogate(s[c]))
                continue; // ペアは割らない
            cuts.Add(c);
        }
        var list = cuts.ToList();
        var pieces = new List<Piece>();
        for (int i = 0; i + 1 < list.Count; i++)
            if (list[i + 1] > list[i])
                pieces.Add(P(s[list[i]..list[i + 1]]));
        return PieceTree.BuildBalanced(pieces.ToArray())!;
    }
}
