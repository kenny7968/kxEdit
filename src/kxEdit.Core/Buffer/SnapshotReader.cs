namespace kxEdit.Core.Buffers;

/// <summary>
/// スナップショット全文を供給する TextReader(Markdig/regex行適用向け)。
/// ピース単位でUTF-8デコードしながら供給する(ピースはコード点境界保証なので
/// デコーダ状態の持ち越し不要)。全文の string 実体化はしない。
/// </summary>
internal sealed class SnapshotReader : TextReader
{
    private readonly IEnumerator<Piece> _pieces;
    private string _current = "";
    private int _pos;
    private bool _done;

    public SnapshotReader(PieceTree.Node? root) =>
        _pieces = PieceTree.Enumerate(root).GetEnumerator();

    /// <summary>現在ピースに未読文字が残るよう次ピースへ進める。EOFなら false。</summary>
    private bool Ensure()
    {
        while (_pos >= _current.Length)
        {
            if (_done || !_pieces.MoveNext())
            {
                _done = true;
                return false;
            }
            var p = _pieces.Current;
            _current = p.Chunk.GetString(p.ByteStart, p.ByteLen);
            _pos = 0;
        }
        return true;
    }

    public override int Peek() => Ensure() ? _current[_pos] : -1;

    public override int Read() => Ensure() ? _current[_pos++] : -1;

    public override int Read(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - index < count)
            throw new ArgumentException("バッファが不足しています。", nameof(count));

        int total = 0;
        while (count > 0 && Ensure())
        {
            int n = Math.Min(count, _current.Length - _pos);
            _current.CopyTo(_pos, buffer, index, n);
            _pos += n;
            index += n;
            count -= n;
            total += n;
        }
        return total;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _pieces.Dispose();
        base.Dispose(disposing);
    }
}
