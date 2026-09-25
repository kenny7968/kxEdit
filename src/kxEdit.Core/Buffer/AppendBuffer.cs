using System.Text;

namespace kxEdit.Core.Buffers;

/// <summary>
/// 編集挿入用の追記バッファ。64KB固定ブロック列で、公開済み範囲は以後不変
/// (ブロックが満杯になったら新規作成。配列再確保・上書き禁止=スナップショット安全)。
/// ブロックを包む TextChunk は「書込済みの長さ」を格子点の上限(gridLimit)にして作り、
/// 次の格子点が書込済み範囲の厳密に内側に入るたびに包み直す(2026-09-25 フェーズ 4・F-6)。
/// 格子点を未書込のゼロ領域に置かないことが本クラスの安全性の要である
/// (ゼロ領域で累積 (CharOff, BreaksTo) を焼き付けると、後から書いた文字の char↔byte 対応が
///  静かに壊れる。2026-07-31 の格子細分化で顕在化した)。古い包みは、書込済み範囲が不変なので
/// そのまま有効(古いスナップショット・Undo・RPC スレッドの読み)。
/// </summary>
internal sealed class AppendBuffer
{
    internal const int BlockBytes = 64 * 1024;
    internal const int LargeInsertBytes = 32 * 1024;

    private const int GridBytes = TextChunk.DefaultGridBytes;

    private byte[] _block = new byte[BlockBytes];
    private TextChunk _chunk;
    private int _pos;

    // 今の _chunk にまだ入っていない最初の名目格子点(GridBytes の倍数)
    private int _nextNominal = GridBytes;

    public AppendBuffer() => _chunk = new TextChunk(_block, gridLimit: 0);

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
            _chunk = new TextChunk(_block, gridLimit: 0);
            _pos = 0;
            _nextNominal = GridBytes;
        }
        pieces.Add(Write(bytes, off, bytes.Length - off));
        return pieces;
    }

    // 順序は「書込 → 包み直し → ピース」。逆にすると、書込で内側に入った格子点を
    // そのピース(32KB 以下の貼り付け)が参照できず、長い線形走査が残る。
    private Piece Write(byte[] src, int off, int len)
    {
        Array.Copy(src, off, _block, _pos, len);
        int start = _pos;
        _pos += len;
        RewrapIfGridPointWritten();
        return new Piece(_chunk, start, len, StatsOf(src, off, len));
    }

    /// <summary>
    /// 次の格子点(名目点を継続バイトの先へ前方スナップした位置)が書込済み範囲の厳密に内側に
    /// 入っていたら、同じブロックを gridLimit=_pos で包み直す。名目点ではなくスナップ後の位置で
    /// 判定するのは、_pos がちょうど名目点のときや名目点が多バイト文字の途中のときに、
    /// 格子点の増えない無駄な包み直し(ピースが 1 つ増えるだけ)をしないため。
    /// </summary>
    private void RewrapIfGridPointWritten()
    {
        if (_nextNominal >= _pos || SnapToCodePoint(_nextNominal) >= _pos)
            return;
        _chunk = new TextChunk(_block, gridLimit: _pos);
        // TextChunk の規則(スナップ後が gridLimit 未満の点だけを置く)に合わせて進める
        while (_nextNominal < _pos && SnapToCodePoint(_nextNominal) < _pos)
            _nextNominal += GridBytes;
    }

    /// <summary>p を継続バイトの先へ前方スナップする(書込済み範囲の外は読まない)。</summary>
    private int SnapToCodePoint(int p)
    {
        while (p < _pos && (_block[p] & 0xC0) == 0x80)
            p++;
        return p;
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
