using kxEdit.Core.Text;

namespace kxEdit.Core.Buffers;

/// <summary>
/// ストリーム読込用ビルダー。チャンク境界のコード点分断は繰越しバッファ(最大3バイト)で吸収する。
/// 各Addごとに Sanitize→TextChunk 化(目標サイズ4MB。大きいAddは4MB単位に分割)。再利用不可。
/// </summary>
public sealed class TextBufferBuilder
{
    internal const int TargetChunkBytes = 4 * 1024 * 1024;

    private readonly List<Piece> _pieces = [];
    private byte[] _carry = [];
    private long _totalBytes;
    private bool _built;

    /// <summary>文書上限(§0-1: 既定 512 MB (2026-07-20 v0.11 引下げ))。テスト注入用。</summary>
    internal long MaxTotalBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>不正UTF-8をU+FFFDに置換したか。</summary>
    public bool HadReplacement { get; private set; }

    public void Add(ReadOnlySpan<byte> utf8Bytes)
    {
        if (_built)
            throw new InvalidOperationException("Build後のAddはできません。");
        if (utf8Bytes.IsEmpty && _carry.Length == 0)
            return;

        // 繰越し+今回分を結合(TextChunkが参照を保持するため必ず自前の配列にコピー)
        byte[] combined = new byte[_carry.Length + utf8Bytes.Length];
        _carry.CopyTo(combined, 0);
        utf8Bytes.CopyTo(combined.AsSpan(_carry.Length));

        int tail = IncompleteTailLength(combined);
        _carry = tail > 0 ? combined[^tail..] : [];

        int bodyLen = combined.Length - tail;
        for (int off = 0; off < bodyLen; )
        {
            int len = Math.Min(TargetChunkBytes, bodyLen - off);
            if (off + len < bodyLen)
            { // 4MB分割点をコード点境界へ後退スナップ(最大3バイト)
                int cut = off + len;
                for (int back = 0; back < 3 && cut > off && (combined[cut] & 0xC0) == 0x80; back++)
                    cut--;
                len = cut - off;
                if (len == 0)
                    len = Math.Min(TargetChunkBytes, bodyLen - off); // 全部継続バイト=不正列: そのまま切る
            }
            AddChunk(combined.AsMemory(off, len));
            off += len;
        }
    }

    /// <summary>木を一括構築(O(n))。繰越しに不完全列が残っていれば U+FFFD 化して吐く。</summary>
    public TextBuffer Build()
    {
        if (_built)
            throw new InvalidOperationException("Buildは一度だけ呼べます。");
        _built = true;
        if (_carry.Length > 0)
        {
            AddChunk(_carry); // 不完全列=不正UTF-8として Sanitize が置換する
            _carry = [];
        }
        return new TextBuffer(
            PieceTree.BuildBalanced(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pieces)
            )
        );
    }

    private void AddChunk(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;
        var (clean, replaced) = Utf8Sanitizer.Sanitize(bytes);
        HadReplacement |= replaced;
        // 上限は実格納バイトで判定(U+FFFD置換による膨張も含む)
        _totalBytes += clean.Length;
        if (_totalBytes > MaxTotalBytes)
            throw new DocumentTooLargeException(
                _totalBytes,
                "文書サイズ上限(512 MB)を超えました。"
            );
        var chunk = new TextChunk(clean);
        _pieces.Add(Piece.Of(chunk, 0, chunk.ByteLength));
    }

    /// <summary>末尾の不完全UTF-8シーケンス長(0〜3)。将来のバイトで完成しうる場合のみ非0。</summary>
    private static int IncompleteTailLength(ReadOnlySpan<byte> b)
    {
        int n = b.Length;
        for (int back = 1; back <= 3 && back <= n; back++)
        {
            byte c = b[n - back];
            if ((c & 0xC0) == 0x80)
                continue; // 継続バイト→さらに遡る
            int expected =
                c < 0x80 ? 1
                : c < 0xE0 ? 2
                : c < 0xF0 ? 3
                : 4;
            return expected > back ? back : 0;
        }
        return 0; // 3バイト遡っても先頭バイトが無い=不正列(Sanitizeに任せる)
    }
}
