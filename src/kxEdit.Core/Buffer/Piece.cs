namespace kxEdit.Core.Buffers;

/// <summary>
/// 区分(ピース)ローカルの統計。改行セマンティクス:
/// Breaks = 区分内のLF数 + 「区分内でLFが直後に続かないCR」の数(末尾CRは単独扱いで数える)。
/// </summary>
internal readonly record struct PieceStats(
    long ByteLen,
    int CharLen,
    int Breaks,
    bool FirstIsLf,
    bool LastIsCr
)
{
    // CA1805: `= default` は record struct のゼロ初期化と同値のため省略。
    // `PieceStats.Empty` を明示的にゼロ値として公開する意図は変わらない。
    public static readonly PieceStats Empty;

    /// <summary>
    /// 改行モノイド結合。a の末尾CR(単独として計上済み)と b の先頭LF(単独として計上済み)が
    /// 合わさると CRLF=1 つなので 1 引く。結合律を満たす(部分木統計の事前計算が可能)。
    /// </summary>
    public static PieceStats Combine(in PieceStats a, in PieceStats b)
    {
        if (a.CharLen == 0)
            return b;
        if (b.CharLen == 0)
            return a;
        return new PieceStats(
            a.ByteLen + b.ByteLen,
            a.CharLen + b.CharLen,
            a.Breaks + b.Breaks - (a.LastIsCr && b.FirstIsLf ? 1 : 0),
            a.FirstIsLf,
            b.LastIsCr
        );
    }
}

/// <summary>チャンクの半開バイト範囲への参照+事前計算済み統計。不変。</summary>
internal readonly record struct Piece(TextChunk Chunk, int ByteStart, int ByteLen, PieceStats Stats)
{
    public int CharLen => Stats.CharLen;

    public static Piece Of(TextChunk chunk, int byteStart, int byteLen) =>
        new(chunk, byteStart, byteLen, chunk.StatsOfRange(byteStart, byteLen));
}
