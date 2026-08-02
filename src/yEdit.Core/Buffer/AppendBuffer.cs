using System.Text;

namespace yEdit.Core.Buffers;

/// <summary>
/// 編集挿入用の追記バッファ。64KB固定ブロック列で、公開済み範囲は以後不変
/// (ブロックが満杯になったら新規作成。配列再確保・上書き禁止=スナップショット安全)。
/// ブロックを包む TextChunk は同一ブロックにつき1個を共有し、ピースは範囲だけ変える
/// (gridBytes に BlockBytes を明示指定して格子表を先頭 1 エントリだけに保つ。未書込領域が
///  構築時に走査されず、かつ書込後に累積値が古くならない=本クラスの安全性の要)。
/// </summary>
internal sealed class AppendBuffer
{
    internal const int BlockBytes = 64 * 1024;
    internal const int LargeInsertBytes = 32 * 1024;

    private byte[] _block = new byte[BlockBytes];
    private TextChunk _chunk;
    private int _pos;

    // gridBytes: BlockBytes は必須(既定値に頼らない)。本クラスは TextChunk で包んだ後も
    // 同じ _block へ書き込み続けるため、格子表が先頭エントリだけである必要がある。
    // 格子を細かくすると未書込のゼロ領域で累積 (CharOff, BreaksTo) がキャッシュされ、
    // 後から書いた文字の char↔byte 対応が静かに壊れる(2026-07-31 の格子細分化で顕在化)。
    public AppendBuffer() => _chunk = new TextChunk(_block, gridBytes: BlockBytes);

    /// <summary>text をUTF-8で追記し、参照ピース列(通常1〜2個)を返す。孤立サロゲートは既定でU+FFFD置換。</summary>
    public List<Piece> Append(string text)
    {
        var pieces = new List<Piece>(2);
        if (text.Length == 0)
            return pieces;
        byte[] bytes = Encoding.UTF8.GetBytes(text);

        if (bytes.Length > LargeInsertBytes)
        { // 大挿入は専用チャンク(ブロックの断片化防止)
            var chunk = new TextChunk(bytes);
            pieces.Add(MakePiece(chunk, bytes, 0, bytes.Length));
            return pieces;
        }

        int off = 0;
        int remaining = BlockBytes - _pos;
        if (bytes.Length > remaining)
        { // 現ブロックへコード点境界まで詰め、残りは新ブロックへ(ピース境界=コード点境界の維持)
            int cut = remaining;
            while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
                cut--;
            if (cut > 0)
            {
                pieces.Add(Write(bytes, 0, cut));
                off = cut;
            }
            _block = new byte[BlockBytes];
            _chunk = new TextChunk(_block, gridBytes: BlockBytes); // 理由は ctor のコメント参照
            _pos = 0;
        }
        pieces.Add(Write(bytes, off, bytes.Length - off));
        return pieces;
    }

    private Piece Write(byte[] src, int off, int len)
    {
        Array.Copy(src, off, _block, _pos, len);
        var piece = new Piece(_chunk, _pos, len, StatsOf(src, off, len));
        _pos += len;
        return piece;
    }

    private static Piece MakePiece(TextChunk chunk, byte[] src, int off, int len) =>
        new(chunk, off, len, StatsOf(src, off, len));

    /// <summary>ソースバイト直走査で統計を計算(チャンク側の累積走査を避ける)。</summary>
    private static PieceStats StatsOf(byte[] src, int off, int len)
    {
        var (charLen, breaks, firstIsLf, lastIsCr) = Utf8Scan.Stats(src.AsSpan(off, len));
        return new PieceStats(len, charLen, breaks, firstIsLf, lastIsCr);
    }
}
