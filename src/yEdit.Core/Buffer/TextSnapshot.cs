using System.Diagnostics;
using System.Text;

namespace yEdit.Core.Buffers;

/// <summary>
/// 不変のテキストスナップショット(永続ピース木のルート参照を包むファサード)。
/// 位置・長さはすべてUTF-16コード単位。取得後の編集の影響を受けない。
/// </summary>
public sealed class TextSnapshot
{
    /// <summary><see cref="DecodeUtf16At"/> の Debug 多層防御: ピース先頭がコードポイント途中。</summary>
    private const string LeadByteBroken =
        "UTF-8 先頭バイトでない=ピース範囲がコードポイントの途中から始まっている"
        + "(GetChar は範囲内を読んだまま静かに誤った char を返す)。";

    /// <summary><see cref="DecodeUtf16At"/> の Debug 多層防御: コードポイントが途中で切れている。</summary>
    private const string ContinuationByteBroken =
        "UTF-8 継続バイトが無い=ピース範囲がコードポイントを分断している"
        + "(GetChar は範囲外/誤ったバイトを読む)。";

    internal static readonly TextSnapshot Empty = new(null);

    private readonly PieceTree.Node? _root;

    internal TextSnapshot(PieceTree.Node? root) => _root = root;

    internal PieceTree.Node? Root => _root;

    public int CharLength => PieceTree.SumOf(_root).CharLen;

    /// <summary>行数 = breaks + 1(空文書=1行)。</summary>
    public int LineCount => PieceTree.SumOf(_root).Breaks + 1;

    /// <summary>診断用(テスト・ベンチ)。</summary>
    internal int PieceCount => PieceTree.Enumerate(_root).Count();

    /// <summary>[start, start+length) の文字列。両端はサロゲート中間でもよい(UIA/描画は任意窓を切るため)。</summary>
    public string GetText(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if ((long)start + length > CharLength)
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                "範囲が文書末尾を超えています。"
            );
        if (length == 0)
            return string.Empty;
        var sb = new StringBuilder(length);
        AppendRange(_root, start, start + length, sb);
        return sb.ToString();
    }

    /// <summary>
    /// pos の code unit を返す。ピース木を降りて <see cref="TextChunk.CharToByte"/> を
    /// <b>1 回だけ</b>呼び、そのバイト位置から UTF-8 を直接 UTF-16 化する
    /// (2026-07-31: 旧実装 <c>GetText(pos, 1)[0]</c> は 1 文字ごとに StringBuilder と
    /// string を作り、char↔byte マッピングを 2 回行っていた=1 呼び出し 128 B / 最大 64 KB 走査)。
    /// </summary>
    /// <remarks>
    /// <b>依存する前提</b>(いずれも既存の <c>Encoding.UTF8.GetString</c> 経路が既に依存している):
    /// <list type="bullet">
    /// <item>ピース境界はコードポイント境界(<see cref="TextBufferBuilder"/> /
    /// <see cref="AppendBuffer"/> / <see cref="PieceTree.Split"/> がいずれも境界へスナップする)
    /// = コードポイントがピースを跨がない。</item>
    /// <item>バッファは <see cref="Utf8Sanitizer"/> 済みで不正 UTF-8 を含まない
    /// = 継続バイトの存在を検査せずに読める。</item>
    /// </list>
    /// <paramref name="pos"/> がサロゲート中間(low サロゲート位置)のとき、
    /// <c>CharToByte</c> は低い方へスナップして <c>actual == pos - 1</c> を返す。
    /// これを low サロゲート要求として使い分ける(旧実装の <c>GetSubstring</c> が
    /// 「終端が中間ならコードポイントを丸ごと含めてから Substring」で得ていた結果と等価)。
    /// </remarks>
    public char GetChar(int pos)
    {
        if (pos < 0 || pos >= CharLength)
            throw new ArgumentOutOfRangeException(nameof(pos));
        var t = _root;
        while (true)
        {
            int leftChars = PieceTree.SumOf(t!.Left).CharLen;
            if (pos < leftChars)
            {
                t = t.Left;
                continue;
            }
            pos -= leftChars;
            if (pos < t.Piece.CharLen)
            {
                var p = t.Piece;
                // ピース境界はコードポイント境界なので、ピース内オフセット 0 は必ず
                // コードポイント先頭 = CharToByte を呼ばずに読める(IsLfAt の FirstIsLf 早道と同じ)。
                // AppendBuffer のチャンクは格子幅=ブロック長で格子表が先頭 1 エントリしかなく、
                // CharToByte 内の CumAt(byteStart) が最大 64 KB 走査するため、この回避は
                // 1 文字ピースが多数ある文書(散在する 1 文字挿入の繰り返し)で効く。
                if (pos == 0)
                    return DecodeUtf16At(p.Chunk.Span, p.ByteStart, wantLowSurrogate: false);
                int b = p.Chunk.CharToByte(p.ByteStart, p.ByteLen, pos, out int actual);
                return DecodeUtf16At(p.Chunk.Span, b, wantLowSurrogate: actual != pos);
            }
            pos -= t.Piece.CharLen;
            t = t.Right;
        }
    }

    /// <summary>
    /// <paramref name="byteOffset"/> の UTF-8 コードポイントを UTF-16 化し、
    /// <paramref name="wantLowSurrogate"/>=false なら 1 単位目(BMP 文字または high サロゲート)、
    /// true なら 2 単位目(low サロゲート)を返す。
    /// wantLowSurrogate=true は 4 バイト列でのみ起こる(<c>CharToByte</c> が 2 単位進む唯一の形)。
    /// </summary>
    /// <remarks>
    /// 先頭バイトの妥当性も継続バイトの存在も検査せずに読む(<see cref="GetChar"/> の前提節を参照)。
    /// 前提が破れると例外なしに誤った char を返す「静かな破壊」になるため、Debug 構成でのみ
    /// 多層防御として両方を表明する(Release では <see cref="Debug"/> ごと消える)。
    /// 2 種類あるのは破れ方が 2 通りだから: ピースが<b>途中から始まる</b>(先頭バイト)と
    /// ピースが<b>途中で切れる</b>(継続バイト)で、前者は範囲内を読むため例外にならない。
    /// 前提そのものは <c>TextSnapshotGetCharEquivalenceTests</c> の
    /// <c>AssertEveryPieceIsWholeCodePoints</c> が全ピースについて固定している。
    /// </remarks>
    private static char DecodeUtf16At(ReadOnlySpan<byte> s, int byteOffset, bool wantLowSurrogate)
    {
        byte b0 = s[byteOffset];
        // 先頭バイトが妥当な UTF-8 lead か(0x00-0x7F / 0xC2-0xF4)。継続バイト 0x80-0xBF なら
        // ピースがコードポイントの途中から始まっている=範囲内を読んだまま静かに誤値を返す形。
        // 3 引数版を使うのは、2 引数版の message が [CallerArgumentExpression] 付きで
        // 明示指定が S3236 になるため(detailMessage にどのバイトかを載せる)。
        Debug.Assert(b0 < 0x80 || (b0 >= 0xC2 && b0 <= 0xF4), LeadByteBroken, "byteOffset");
        if (b0 < 0x80)
            return (char)b0;
        Debug.Assert((s[byteOffset + 1] & 0xC0) == 0x80, ContinuationByteBroken, "byteOffset + 1");
        if (b0 < 0xE0)
            return (char)(((b0 & 0x1F) << 6) | (s[byteOffset + 1] & 0x3F));
        Debug.Assert((s[byteOffset + 2] & 0xC0) == 0x80, ContinuationByteBroken, "byteOffset + 2");
        if (b0 < 0xF0)
            return (char)(
                ((b0 & 0x0F) << 12) | ((s[byteOffset + 1] & 0x3F) << 6) | (s[byteOffset + 2] & 0x3F)
            );
        Debug.Assert((s[byteOffset + 3] & 0xC0) == 0x80, ContinuationByteBroken, "byteOffset + 3");
        int cp =
            ((b0 & 0x07) << 18)
            | ((s[byteOffset + 1] & 0x3F) << 12)
            | ((s[byteOffset + 2] & 0x3F) << 6)
            | (s[byteOffset + 3] & 0x3F);
        int v = cp - 0x10000;
        return wantLowSurrogate ? (char)(0xDC00 + (v & 0x3FF)) : (char)(0xD800 + (v >> 10));
    }

    /// <summary>[start, endExclusive) に含まれる CRLF pair の数(=論理文字数計算用)。
    /// ピース跨ぎ("...\r" | "\n...")も 1 pair としてカウントする。低頻度(位置照会ホットキー押下時のみ)
    /// なため O(N) 走査を許容する。</summary>
    public int CountCrlfPairs(int start, int endExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(endExclusive);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, CharLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(endExclusive, CharLength);
        if (start >= endExclusive)
            return 0;
        string t = GetText(start, endExclusive - start);
        int count = 0;
        for (int i = 0; i + 1 < t.Length; i++)
        {
            if (t[i] == '\r' && t[i + 1] == '\n')
                count++;
        }
        return count;
    }

    public int GetLineStart(int line)
    {
        if (line < 0 || line >= LineCount)
            throw new ArgumentOutOfRangeException(nameof(line));
        return line == 0 ? 0 : PieceTree.NthBreakEnd(_root!, line, followedByLf: false) + 1;
    }

    /// <summary>行末。includeBreak=false なら行のbreak開始位置(CRLF行なら\rの位置)。最終行は CharLength。</summary>
    public int GetLineEnd(int line, bool includeBreak)
    {
        if (line < 0 || line >= LineCount)
            throw new ArgumentOutOfRangeException(nameof(line));
        if (line == LineCount - 1)
            return CharLength;
        int breakEnd = PieceTree.NthBreakEnd(_root!, line + 1, followedByLf: false);
        if (includeBreak)
            return breakEnd + 1;
        return breakEnd
            - (breakEnd > 0 && GetChar(breakEnd) == '\n' && GetChar(breakEnd - 1) == '\r' ? 1 : 0);
    }

    /// <summary>pos が属する行番号。pos == CharLength は最終行(キャレットがEOF位置に立つため)。</summary>
    public int GetLineIndexOfChar(int pos)
    {
        if (pos < 0 || pos > CharLength)
            throw new ArgumentOutOfRangeException(nameof(pos));
        var prefix = PieceTree.PrefixStats(_root, pos);
        int breaks = prefix.Breaks;
        // CRLFの途中(pos-1がCR かつ posがLF)= breakはまだ完了していない。
        // 接頭辞末尾がCRのときだけ pos のLF判定を行う(通常経路は追加走査なし)
        if (prefix.LastIsCr && pos < CharLength && IsLfAt(pos))
            breaks--;
        return breaks;
    }

    /// <summary>pos の文字がLFか(バイト1点照会・stringデコードなし)。pos &lt; CharLength 前提。</summary>
    private bool IsLfAt(int pos)
    {
        var t = _root;
        while (true)
        {
            int leftChars = PieceTree.SumOf(t!.Left).CharLen;
            if (pos < leftChars)
            {
                t = t.Left;
                continue;
            }
            pos -= leftChars;
            if (pos < t.Piece.CharLen)
            {
                if (pos == 0)
                    return t.Piece.Stats.FirstIsLf;
                int b = t.Piece.Chunk.CharToByte(t.Piece.ByteStart, t.Piece.ByteLen, pos);
                return t.Piece.Chunk.Span[b] == (byte)'\n';
            }
            pos -= t.Piece.CharLen;
            t = t.Right;
        }
    }

    /// <summary>全文を供給する TextReader(全文string非実体化・Markdig/regex行適用向け)。</summary>
    public TextReader CreateReader() => new SnapshotReader(_root);

    /// <summary>UTF-8チャンクをストリームへ直書き(変換ゼロ・全文string化しない)。</summary>
    public void WriteTo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        foreach (var p in PieceTree.Enumerate(_root))
            stream.Write(p.Chunk.Span.Slice(p.ByteStart, p.ByteLen));
    }

    /// <summary>ノードの文字区間 [from, to) を sb へ追記。ピース全域は直接デコード、端はスナップ切り出し。</summary>
    private static void AppendRange(PieceTree.Node? t, int from, int to, StringBuilder sb)
    {
        if (t is null || from >= to)
            return;
        int leftChars = PieceTree.SumOf(t.Left).CharLen;
        if (from < leftChars)
            AppendRange(t.Left, from, Math.Min(to, leftChars), sb);
        int ps = leftChars,
            pe = leftChars + t.Piece.CharLen;
        if (to > ps && from < pe && t.Piece.CharLen > 0)
        {
            int f = Math.Max(from, ps) - ps,
                e = Math.Min(to, pe) - ps;
            var p = t.Piece;
            sb.Append(
                f == 0 && e == p.CharLen
                    ? p.Chunk.GetString(p.ByteStart, p.ByteLen)
                    : p.Chunk.GetSubstring(p.ByteStart, p.ByteLen, f, e)
            );
        }
        if (to > pe)
            AppendRange(t.Right, Math.Max(from, pe) - pe, to - pe, sb);
    }
}
