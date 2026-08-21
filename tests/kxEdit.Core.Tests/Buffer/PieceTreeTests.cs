using System.Text;
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Tests.Buffers;

public class PieceTreeTests
{
    // CRLF跨ぎピース("a\r"+"\nb")を必ず含む素材プール
    private static readonly string[] MixedPool =
    [
        "a\r",
        "\nb",
        "hello world",
        "あいう漢字",
        "😀🈴",
        "\r\n",
        "x\ry\n",
        "line1\nline2",
        "\r",
        "\n",
        "t",
    ];
    private static readonly string[] AsciiPool =
    [
        "abc",
        "d",
        "ef\n",
        "ghij\r\n",
        "k\r",
        "\nmn",
        "op q rs",
        "\n",
        "tuv",
    ];

    private static Piece P(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        return Piece.Of(new TextChunk(bytes, gridBytes: 8), 0, bytes.Length);
    }

    private static PieceTree.Node? Build(IEnumerable<string> parts) =>
        PieceTree.BuildBalanced(parts.Select(P).ToArray());

    private static string Text(PieceTree.Node? t)
    {
        var sb = new StringBuilder();
        foreach (var p in PieceTree.Enumerate(t))
            sb.Append(p.Chunk.GetString(p.ByteStart, p.ByteLen));
        return sb.ToString();
    }

    private static PieceStats SumOf(PieceTree.Node? n) => n?.Sum ?? PieceStats.Empty;

    private static List<int> Boundaries(string s)
    {
        var list = new List<int>();
        for (int i = 0; i <= s.Length; i++)
            if (i == s.Length || !char.IsLowSurrogate(s[i]))
                list.Add(i);
        return list;
    }

    private static void AssertStatsMatch(string expected, PieceStats actual)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(expected);
        var (charLen, breaks, firstIsLf, lastIsCr) = Utf8Scan.Stats(bytes);
        Assert.Equal(bytes.Length, actual.ByteLen);
        Assert.Equal(charLen, actual.CharLen);
        Assert.Equal(breaks, actual.Breaks);
        Assert.Equal(firstIsLf, actual.FirstIsLf);
        Assert.Equal(lastIsCr, actual.LastIsCr);
    }

    /// <summary>AVL不変条件(平衡・高さ・統計集約)を全ノードで検証し高さを返す。</summary>
    private static int CheckInvariants(PieceTree.Node? n)
    {
        if (n is null)
            return 0;
        int hl = CheckInvariants(n.Left),
            hr = CheckInvariants(n.Right);
        Assert.True(Math.Abs(hl - hr) <= 1, $"unbalanced: |{hl}-{hr}| > 1");
        Assert.Equal(1 + Math.Max(hl, hr), n.Height);
        var sum = PieceStats.Combine(
            PieceStats.Combine(SumOf(n.Left), n.Piece.Stats),
            SumOf(n.Right)
        );
        Assert.Equal(sum, n.Sum);
        return n.Height;
    }

    [Fact]
    public void BuildBalanced_enumerate_roundtrip_preserves_pieces()
    {
        var pieces = new[] { P("a\r"), P("\nb"), P("あ😀"), P("x\ry\n") };
        var root = PieceTree.BuildBalanced(pieces);
        Assert.Equal(pieces, PieceTree.Enumerate(root).ToArray());
        CheckInvariants(root);
        Assert.Equal("a\r\nbあ😀x\ry\n", Text(root));
    }

    [Fact]
    public void Sum_handles_crlf_straddling_piece_boundary()
    {
        Assert.Equal(1, SumOf(Build(["a\r", "\nb"])).Breaks);
        Assert.Equal(2, SumOf(Build(["\r", "\r\n"])).Breaks);
        Assert.Equal(2, SumOf(Build(["\n", "\n"])).Breaks);
        Assert.Equal(1, SumOf(Build(["x", "\r", "\n"])).Breaks);
    }

    [Fact]
    public void Random_ascii_split_charlen_is_exact()
    {
        var rnd = new Random(20260705);
        for (int trial = 0; trial < 30; trial++)
        {
            var parts = Enumerable
                .Range(0, rnd.Next(1, 201))
                .Select(_ => AsciiPool[rnd.Next(AsciiPool.Length)])
                .ToList();
            string all = string.Concat(parts);
            var root = Build(parts);
            for (int q = 0; q < 10; q++)
            {
                int k = rnd.Next(all.Length + 1);
                var (l, r) = PieceTree.Split(root, k);
                Assert.Equal(k, SumOf(l).CharLen);
                Assert.Equal(all.Length - k, SumOf(r).CharLen);
                CheckInvariants(l);
                CheckInvariants(r);
            }
        }
    }

    [Fact]
    public void Random_split_join2_roundtrip_preserves_text_and_stats()
    {
        var rnd = new Random(42);
        for (int trial = 0; trial < 30; trial++)
        {
            var parts = Enumerable
                .Range(0, rnd.Next(1, 201))
                .Select(_ => MixedPool[rnd.Next(MixedPool.Length)])
                .ToList();
            string all = string.Concat(parts);
            var root = Build(parts);
            // Sum = 全ピース統計の逐次Combine と一致
            var seq = parts.Aggregate(
                PieceStats.Empty,
                (acc, x) => PieceStats.Combine(acc, P(x).Stats)
            );
            Assert.Equal(seq, SumOf(root));
            AssertStatsMatch(all, SumOf(root));
            var bounds = Boundaries(all);
            for (int q = 0; q < 10; q++)
            {
                int k = bounds[rnd.Next(bounds.Count)];
                var (l, r) = PieceTree.Split(root, k);
                Assert.Equal(all[..k], Text(l));
                Assert.Equal(all[k..], Text(r));
                var joined = PieceTree.Join2(l, r);
                Assert.Equal(all, Text(joined));
                Assert.Equal(SumOf(root), SumOf(joined));
                CheckInvariants(joined);
            }
        }
    }

    [Fact]
    public void Height_stays_within_avl_bound_after_repeated_split_join()
    {
        var rnd = new Random(7);
        var parts = Enumerable
            .Range(0, 200)
            .Select(_ => AsciiPool[rnd.Next(AsciiPool.Length)])
            .ToList();
        var current = Build(parts)!;
        string all = string.Concat(parts);
        for (int i = 0; i < 100; i++)
        {
            int k = rnd.Next(SumOf(current).CharLen + 1);
            var (l, r) = PieceTree.Split(current, k);
            current = PieceTree.Join2(l, r)!;
        }
        Assert.Equal(all, Text(current));
        int count = PieceTree.Enumerate(current).Count();
        Assert.True(
            current.Height <= 1.45 * Math.Log2(count + 2) + 2,
            $"height {current.Height} exceeds AVL bound for {count} pieces"
        );
        CheckInvariants(current);
    }

    [Fact]
    public void SplitFirst_and_SplitLast_extract_edge_pieces()
    {
        var pieces = new[] { P("aa"), P("bb"), P("cc"), P("dd"), P("ee") };
        var root = PieceTree.BuildBalanced(pieces)!;
        var (rest1, last) = PieceTree.SplitLast(root);
        Assert.Equal(pieces[4], last);
        Assert.Equal("aabbccdd", Text(rest1));
        var (first, rest2) = PieceTree.SplitFirst(root);
        Assert.Equal(pieces[0], first);
        Assert.Equal("bbccddee", Text(rest2));
        CheckInvariants(rest1);
        CheckInvariants(rest2);
    }

    [Fact]
    public void Piece_internal_split_with_multibyte_matches_naive()
    {
        const string s = "aあ😀b\r\nかき😀くけこ";
        var root = PieceTree.BuildBalanced(new[] { P(s) });
        foreach (int k in Boundaries(s))
        {
            var (l, r) = PieceTree.Split(root, k);
            Assert.Equal(s[..k], Text(l));
            Assert.Equal(s[k..], Text(r));
            AssertStatsMatch(s[..k], SumOf(l));
            AssertStatsMatch(s[k..], SumOf(r));
        }
    }

    [Fact]
    public void Join_with_very_unbalanced_heights_rebalances()
    {
        // 高い木×低い木の Join(JoinOnLeft/JoinOnRight 両経路)
        var big = Build(Enumerable.Range(0, 150).Select(i => "x" + i))!;
        var small = Build(["yy"])!;
        var j1 = PieceTree.Join(big, P("MID"), small);
        CheckInvariants(j1);
        Assert.Equal(Text(big) + "MID" + "yy", Text(j1));
        var j2 = PieceTree.Join(small, P("MID"), big);
        CheckInvariants(j2);
        Assert.Equal("yy" + "MID" + Text(big), Text(j2));
    }
}
