namespace kxEdit.Core.Buffers;

/// <summary>
/// 永続平衡ピース木(join-based AVL・"Just Join for Parallel Ordered Sets" の手法)。
/// ノードは完全不変(経路コピーで編集)。Join(left, piece, right) だけで挿入/削除/分割を組み立てる。
/// スナップショットはルート参照を持つだけで O(1)。
/// </summary>
internal static class PieceTree
{
    internal sealed class Node
    {
        public readonly Node? Left,
            Right;
        public readonly Piece Piece;
        public readonly byte Height; // 葉=1
        public readonly PieceStats Sum; // Left+Piece+Right の結合統計

        public Node(Node? left, Piece piece, Node? right)
        {
            Left = left;
            Right = right;
            Piece = piece;
            Height = (byte)(1 + Math.Max(H(left), H(right)));
            Sum = PieceStats.Combine(PieceStats.Combine(SumOf(left), piece.Stats), SumOf(right));
        }
    }

    private static int H(Node? n) => n?.Height ?? 0;

    public static PieceStats SumOf(Node? n) => n?.Sum ?? PieceStats.Empty;

    private static Node RotL(Node t) =>
        new(new Node(t.Left, t.Piece, t.Right!.Left), t.Right.Piece, t.Right.Right);

    private static Node RotR(Node t) =>
        new(t.Left!.Left, t.Left.Piece, new Node(t.Left.Right, t.Piece, t.Right));

    /// <summary>高さ差が任意の2木を p を挟んで結合(AVL join)。</summary>
    public static Node Join(Node? l, Piece p, Node? r)
    {
        if (H(l) > H(r) + 1)
            return JoinOnLeft(l!, p, r);
        if (H(r) > H(l) + 1)
            return JoinOnRight(l, p, r!);
        return new Node(l, p, r);
    }

    private static Node JoinOnLeft(Node l, Piece p, Node? r)
    { // 左が高い: 左の右背骨を降りて r と釣り合う位置で接ぐ
        if (H(l.Right) <= H(r) + 1)
        {
            var t = new Node(l.Right, p, r);
            return t.Height <= l.Height + 1 && H(l.Left) + 2 > t.Height
                ? new Node(l.Left, l.Piece, t)
                : RotL(new Node(l.Left, l.Piece, RotR(t))); // 標準の再平衡
        }
        var joined = JoinOnLeft(l.Right!, p, r);
        var node = new Node(l.Left, l.Piece, joined);
        return joined.Height <= H(l.Left) + 1 ? node : RotL(node);
    }

    private static Node JoinOnRight(Node? l, Piece p, Node r)
    { // 右が高い: 右の左背骨を降りて l と釣り合う位置で接ぐ(JoinOnLeft の対称形)
        if (H(r.Left) <= H(l) + 1)
        {
            var t = new Node(l, p, r.Left);
            return t.Height <= r.Height + 1 && H(r.Right) + 2 > t.Height
                ? new Node(t, r.Piece, r.Right)
                : RotR(new Node(RotL(t), r.Piece, r.Right));
        }
        var joined = JoinOnRight(l, p, r.Left!);
        var node = new Node(joined, r.Piece, r.Right);
        return joined.Height <= H(r.Right) + 1 ? node : RotR(node);
    }

    /// <summary>文字オフセット pos で分割(pos はコード点境界にスナップ済み前提)。</summary>
    public static (Node? Left, Node? Right) Split(Node? t, int pos)
    {
        if (t is null)
            return (null, null);
        int leftChars = SumOf(t.Left).CharLen;
        if (pos < leftChars)
        {
            var (a, b) = Split(t.Left, pos);
            return (a, Join(b, t.Piece, t.Right));
        }
        pos -= leftChars;
        if (pos == 0 && t.Piece.CharLen > 0)
            return (t.Left, Join(null, t.Piece, t.Right)); // ピース左境界: 空ピースを作らない
        if (pos >= t.Piece.CharLen)
        {
            var (a, b) = Split(t.Right, pos - t.Piece.CharLen);
            return (Join(t.Left, t.Piece, a), b);
        }
        // ピース内部分割: 1走査で分割点と接頭辞統計を求め、残り半分はモノイド差分でO(1)導出
        var piece = t.Piece;
        var (byteMid, prefix) = piece.Chunk.SplitStats(piece.ByteStart, piece.ByteLen, pos);
        if (byteMid == piece.ByteStart) // 先頭コード点中間へのスナップ → ピース左境界扱い(空ピースを作らない)
            return (t.Left, Join(null, piece, t.Right));
        var p1 = new Piece(piece.Chunk, piece.ByteStart, byteMid - piece.ByteStart, prefix);
        var total = piece.Stats;
        bool p2FirstIsLf = piece.Chunk.Span[byteMid] == (byte)'\n';
        var p2 = new Piece(
            piece.Chunk,
            byteMid,
            piece.ByteStart + piece.ByteLen - byteMid,
            new PieceStats(
                total.ByteLen - prefix.ByteLen,
                total.CharLen - prefix.CharLen,
                total.Breaks - prefix.Breaks + (prefix.LastIsCr && p2FirstIsLf ? 1 : 0),
                p2FirstIsLf,
                total.LastIsCr
            )
        );
        return (Join(t.Left, p1, null), Join(null, p2, t.Right));
    }

    /// <summary>ピースを挟まない結合(削除で使用)。左木の最右ピースを抜いて Join。</summary>
    public static Node? Join2(Node? l, Node? r)
    {
        if (l is null)
            return r;
        if (r is null)
            return l;
        var (rest, last) = SplitLast(l);
        return Join(rest, last, r);
    }

    /// <summary>最右ピースの取り出し(隣接マージ用)。</summary>
    public static (Node? Remaining, Piece Last) SplitLast(Node l)
    {
        if (l.Right is null)
            return (l.Left, l.Piece);
        var (rest, last) = SplitLast(l.Right);
        return (Join(l.Left, l.Piece, rest), last);
    }

    /// <summary>最左ピースの取り出し(隣接マージ用)。</summary>
    public static (Piece First, Node? Remaining) SplitFirst(Node r)
    {
        if (r.Left is null)
            return (r.Piece, r.Right);
        var (first, rest) = SplitFirst(r.Left);
        return (first, Join(rest, r.Piece, r.Right));
    }

    /// <summary>ピース列から平衡木を一括構築(中央分割・O(n))。ビルダー用。</summary>
    public static Node? BuildBalanced(ReadOnlySpan<Piece> pieces)
    {
        if (pieces.IsEmpty)
            return null;
        int mid = pieces.Length / 2;
        return new Node(
            BuildBalanced(pieces[..mid]),
            pieces[mid],
            BuildBalanced(pieces[(mid + 1)..])
        );
    }

    /// <summary>k番目(1始まり)のbreak終端文字(LFまたは単独CR)の文字オフセット。ルート呼び出しは followedByLf=false。</summary>
    public static int NthBreakEnd(Node t, int k, bool followedByLf)
    {
        // 部分木 S の直後文字が LF のとき、S 末尾の CR は CRLF の一部なので終端は S 内に無い
        static int EndsIn(PieceStats s, bool nextIsLf) =>
            s.Breaks - (s.LastIsCr && nextIsLf ? 1 : 0);

        bool afterLeftIsLf =
            t.Piece.CharLen > 0 ? t.Piece.Stats.FirstIsLf
            : t.Right is not null ? t.Right.Sum.FirstIsLf
            : followedByLf;
        int endsInLeft = t.Left is null ? 0 : EndsIn(t.Left.Sum, afterLeftIsLf);
        if (k <= endsInLeft)
            return NthBreakEnd(t.Left!, k, afterLeftIsLf);
        k -= endsInLeft;

        int pieceStart = t.Left?.Sum.CharLen ?? 0;
        bool afterPieceIsLf = t.Right is not null ? t.Right.Sum.FirstIsLf : followedByLf;
        int endsInPiece = EndsIn(t.Piece.Stats, afterPieceIsLf);
        if (k <= endsInPiece)
            return pieceStart
                + t.Piece.Chunk.NthBreakEndChar(t.Piece.ByteStart, t.Piece.ByteLen, k);
        k -= endsInPiece;

        return pieceStart + t.Piece.CharLen + NthBreakEnd(t.Right!, k, followedByLf);
    }

    /// <summary>接頭辞 [0, pos) の結合統計(O(log n)+格子1マス走査)。</summary>
    public static PieceStats PrefixStats(Node? t, int pos)
    {
        var acc = PieceStats.Empty;
        while (t is not null)
        {
            int leftChars = SumOf(t.Left).CharLen;
            if (pos < leftChars)
            {
                t = t.Left;
                continue;
            }
            acc = PieceStats.Combine(acc, SumOf(t.Left));
            pos -= leftChars;
            if (pos < t.Piece.CharLen)
            {
                if (pos == 0)
                    return acc;
                var (_, prefix) = t.Piece.Chunk.SplitStats(t.Piece.ByteStart, t.Piece.ByteLen, pos);
                return PieceStats.Combine(acc, prefix);
            }
            acc = PieceStats.Combine(acc, t.Piece.Stats);
            pos -= t.Piece.CharLen;
            t = t.Right;
        }
        return acc;
    }

    /// <summary>in-order列挙(WriteTo/Reader用)。明示スタックで深い木でも再帰なし。</summary>
    public static IEnumerable<Piece> Enumerate(Node? t)
    {
        var stack = new Stack<Node>();
        while (t is not null || stack.Count > 0)
        {
            while (t is not null)
            {
                stack.Push(t);
                t = t.Left;
            }
            var n = stack.Pop();
            yield return n.Piece;
            t = n.Right;
        }
    }
}
