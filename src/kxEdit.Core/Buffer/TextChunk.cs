using System.Text;

namespace kxEdit.Core.Buffers;

/// <summary>
/// 不変UTF-8バイトチャンク+格子(既定4KB)の累積統計表。
/// ピース分割・行検索・文字⇔バイト変換の走査を格子1マスに局所化する(O(log n + 格子幅))。
///
/// 格子表: エントリ (ByteOff, CharOff, BreaksTo)。ByteOff は格子点をコード点境界へ前方スナップした位置。
/// CharOff/BreaksTo はチャンク先頭からの累積。
/// BreaksTo(x) の規約: [0,x) 内の LF数+「LFが直後に続かないCR」数、ただし x-1 の CR は(次を見ずに)単独扱いで数える。
/// 範囲統計の導出式: Breaks[a,b) = BreaksTo(b) − BreaksTo(a) + (a>0 かつ bytes[a-1]=CR かつ bytes[a]=LF ? 1 : 0)
///   (検算: "\r\n" の a=1,b=2 → 1−1+1 = 1 = 区分"\n"単体のBreaks)
/// </summary>
internal sealed class TextChunk
{
    private readonly ReadOnlyMemory<byte> _bytes;

    // 格子表(ByteOff/CharOff とも昇順)。先頭エントリは必ず (0, 0, 0)
    private readonly int[] _gByte;
    private readonly int[] _gChar;
    private readonly int[] _gBreaks;

    /// <summary>
    /// 既定の格子幅。<c>CharToByte</c> の線形走査量の上限そのものなので、文字アクセスの
    /// コストを直接決める(ベンチ <c>--characcess</c> がセル内オフセットの表示に使う)。
    /// </summary>
    internal const int DefaultGridBytes = 4 * 1024;

    // 2026-07-31: 既定 64KB → 4KB。格子幅はそのまま CharToByte の線形走査量になり、
    // GetChar / 単語ナビのコストを支配する(64KB 時は 1 文字読むのに最大 64KB 走査していた)。
    // 代償(4MB チャンク 1 個あたりの実測値):
    //   格子表 896 B → 12,416 B(+11,520 B = チャンクの 0.27%。512MB 文書で +1.4 MB)
    //   構築   8.5 ms → 8.6 ms(+2%。走査バイト数は幅によらず 4M で不変で、増えるのは
    //                          格子点あたりの List.Add 3 回 ×(1023−63)回だけ=走査に対し
    //                          無視できる。交互サンプリング n=80 で測ること)
    // 注: AppendBuffer の共有ブロックは gridBytes: BlockBytes を明示して除外している(理由は同所)。
    public TextChunk(ReadOnlyMemory<byte> bytes, int gridBytes = DefaultGridBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gridBytes, 1);
        _bytes = bytes;
        var span = bytes.Span;
        int n = span.Length;
        int capacity = n / gridBytes + 2;
        var gb = new List<int>(capacity) { 0 };
        var gc = new List<int>(capacity) { 0 };
        var gk = new List<int>(capacity) { 0 };
        for (long nominal = gridBytes; nominal < n; nominal += gridBytes)
        {
            int p = (int)nominal;
            while (p < n && (span[p] & 0xC0) == 0x80)
                p++; // コード点境界へ前方スナップ
            if (p >= n || p == gb[^1])
                continue;
            var (ch, f) = ScanForward(span, gb[^1], gc[^1], gk[^1], p);
            gb.Add(p);
            gc.Add(ch);
            gk.Add(f);
        }
        _gByte = [.. gb];
        _gChar = [.. gc];
        _gBreaks = [.. gk];
    }

    public ReadOnlySpan<byte> Span => _bytes.Span;
    public int ByteLength => _bytes.Length;

    /// <summary>[byteStart, byteStart+byteLen) の区分ローカル統計。両端はコード点境界前提。</summary>
    public PieceStats StatsOfRange(int byteStart, int byteLen)
    {
        if (byteLen == 0)
            return PieceStats.Empty;
        var s = _bytes.Span;
        int a = byteStart,
            b = byteStart + byteLen;
        var (chA, fA) = CumAt(a);
        var (chB, fB) = CumAt(b);
        int breaks = fB - fA + (a > 0 && s[a - 1] == (byte)'\r' && s[a] == (byte)'\n' ? 1 : 0);
        return new PieceStats(
            byteLen,
            chB - chA,
            breaks,
            s[a] == (byte)'\n',
            s[b - 1] == (byte)'\r'
        );
    }

    /// <summary>範囲先頭から charDelta 文字(UTF-16単位)進んだバイトオフセット(範囲内保証・サロゲート中間は呼び出し側でスナップ済み)。</summary>
    public int CharToByte(int byteStart, int byteLen, int charDelta) =>
        CharToByte(byteStart, byteLen, charDelta, out _);

    /// <summary>同上。actualCharDelta には実際に到達した文字オフセット(中間指定なら charDelta-1)を返す。</summary>
    public int CharToByte(int byteStart, int byteLen, int charDelta, out int actualCharDelta)
    {
        var s = _bytes.Span;
        var (chA, _) = CumAt(byteStart);
        int target = chA + charDelta;
        // CharOff ≤ target の最遠格子点から前進走査(byteStart より手前なら byteStart から)
        int j = GridIndexForChar(target);
        int pos,
            cum;
        if (_gByte[j] >= byteStart)
        {
            pos = _gByte[j];
            cum = _gChar[j];
        }
        else
        {
            pos = byteStart;
            cum = chA;
        }
        while (cum < target)
        {
            byte b = s[pos];
            int step =
                b < 0x80 ? 1
                : b < 0xE0 ? 2
                : b < 0xF0 ? 3
                : 4;
            int units = step == 4 ? 2 : 1;
            if (cum + units > target)
                break; // サロゲート中間=低い方へスナップ
            cum += units;
            pos += step;
        }
        actualCharDelta = cum - chA;
        return pos;
    }

    /// <summary>
    /// 範囲先頭から charDelta 文字目の分割バイト位置と接頭辞 [byteStart, ByteMid) の統計を
    /// 1回の格子走査で返す(ピース分割・接頭辞統計の高速経路)。charDelta がサロゲート中間なら低い方へスナップ。
    /// </summary>
    public (int ByteMid, PieceStats Prefix) SplitStats(int byteStart, int byteLen, int charDelta)
    {
        var s = _bytes.Span;
        var (chA, fA) = CumAt(byteStart);
        int target = chA + charDelta;

        int j = GridIndexForChar(target);
        int pos,
            cum,
            f;
        if (_gByte[j] >= byteStart)
        {
            pos = _gByte[j];
            cum = _gChar[j];
            f = _gBreaks[j];
        }
        else
        {
            pos = byteStart;
            cum = chA;
            f = fA;
        }
        int jump = pos;

        // 実lookaheadでbreakを数えながら target 文字まで前進(f規約への補正は走査後)
        while (cum < target)
        {
            byte b = s[pos];
            int step =
                b < 0x80 ? 1
                : b < 0xE0 ? 2
                : b < 0xF0 ? 3
                : 4;
            int units = step == 4 ? 2 : 1;
            if (cum + units > target)
                break; // サロゲート中間 → 低い方へスナップ
            if (b == (byte)'\n')
                f++;
            else if (b == (byte)'\r' && (pos + 1 >= s.Length || s[pos + 1] != (byte)'\n'))
                f++;
            cum += units;
            pos += step;
        }
        if (pos > jump)
        {
            // f(x)規約: x-1 の CR は単独扱い(実lookaheadでは CRLF として未計上なら +1)
            if (s[pos - 1] == (byte)'\r' && pos < s.Length && s[pos] == (byte)'\n')
                f++;
            // jump点の BreaksTo は jump-1 の CR を単独計上済み。実際は CRLF なら LF 側と二重になるため −1
            if (jump > 0 && s[jump - 1] == (byte)'\r' && s[jump] == (byte)'\n')
                f--;
        }

        if (pos == byteStart)
            return (pos, PieceStats.Empty);
        int breaks =
            f
            - fA
            + (
                byteStart > 0 && s[byteStart - 1] == (byte)'\r' && s[byteStart] == (byte)'\n'
                    ? 1
                    : 0
            );
        return (
            pos,
            new PieceStats(
                pos - byteStart,
                cum - chA,
                breaks,
                s[byteStart] == (byte)'\n',
                s[pos - 1] == (byte)'\r'
            )
        );
    }

    /// <summary>
    /// 範囲内の文字区間 [charFrom, charTo) を string 化。両端がサロゲート中間でもよい
    /// (コード点境界へ広げて切り出し→部分stringスライス)。
    /// </summary>
    public string GetSubstring(int byteStart, int byteLen, int charFrom, int charTo)
    {
        if (charFrom >= charTo)
            return string.Empty;
        int bF = CharToByte(byteStart, byteLen, charFrom, out int cF); // 中間なら低い方へ
        int bT = CharToByte(byteStart, byteLen, charTo, out int cT);
        if (cT < charTo)
        { // 終端が中間: そのコード点(必ず4バイト=2単位)を丸ごと含める
            bT += 4;
            cT += 2;
        }
        string s = GetString(bF, bT - bF);
        return charFrom == cF && charTo == cT ? s : s.Substring(charFrom - cF, charTo - charFrom);
    }

    /// <summary>
    /// 範囲内 k 番目(1始まり)の break 終端文字(LF または単独CR)の、範囲先頭からの文字オフセット。
    /// ピースローカル semantics: 範囲末尾の CR は単独扱い(=終端)、範囲内の CRLF は LF が終端。
    /// </summary>
    public int NthBreakEndChar(int byteStart, int byteLen, int k)
    {
        var s = _bytes.Span;
        int a = byteStart,
            b = byteStart + byteLen;
        var (chA, fA) = CumAt(a);
        int corrA = a > 0 && s[a - 1] == (byte)'\r' && s[a] == (byte)'\n' ? 1 : 0;

        // 「その格子点までの範囲内終端数 < k」の最遠格子点まで表で飛び、残りを線形走査
        int scanPos = a,
            scanChar = chA,
            cnt = 0;
        for (int j = GridIndexFor(a) + 1; j < _gByte.Length && _gByte[j] < b; j++)
        {
            int x = _gByte[j];
            if (x <= a)
                continue;
            // [a,x) 内の終端数: x-1 の CR が実際は CRLF(次がLF)なら終端は x 側なので 1 引く
            int ends =
                _gBreaks[j] - fA + corrA - (s[x - 1] == (byte)'\r' && s[x] == (byte)'\n' ? 1 : 0);
            if (ends >= k)
                break;
            scanPos = x;
            scanChar = _gChar[j];
            cnt = ends;
        }

        int chRel = scanChar - chA;
        for (int i = scanPos; i < b; i++)
        {
            byte c = s[i];
            bool isEnd =
                c == (byte)'\n' || (c == (byte)'\r' && (i + 1 >= b || s[i + 1] != (byte)'\n'));
            if (isEnd && ++cnt == k)
                return chRel;
            if ((c & 0xC0) != 0x80)
                chRel++;
            if (c >= 0xF0)
                chRel++;
        }
        throw new ArgumentOutOfRangeException(nameof(k), k, "範囲内のbreak終端数を超えています。");
    }

    /// <summary>範囲を string へデコード(両端はコード点境界前提)。</summary>
    public string GetString(int byteStart, int byteLen) =>
        Encoding.UTF8.GetString(_bytes.Span.Slice(byteStart, byteLen));

    /// <summary>コード点境界 x における累積 (CharOff, BreaksTo)。最寄り格子点から線形走査。</summary>
    private (int CharOff, int BreaksTo) CumAt(int x)
    {
        int j = GridIndexFor(x);
        return ScanForward(_bytes.Span, _gByte[j], _gChar[j], _gBreaks[j], x);
    }

    /// <summary>ByteOff ≤ x の最大格子インデックス。</summary>
    private int GridIndexFor(int x)
    {
        int lo = 0,
            hi = _gByte.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_gByte[mid] <= x)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    /// <summary>CharOff ≤ targetChar の最大格子インデックス。</summary>
    private int GridIndexForChar(int targetChar)
    {
        int lo = 0,
            hi = _gChar.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_gChar[mid] <= targetChar)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    /// <summary>
    /// 累積値既知のコード点境界 from から境界 to までを線形走査し (CharOff, BreaksTo) を返す。
    /// BreaksTo の規約合わせ: f(from) は from-1 の CR を単独扱いで計上済みなので、実際は CRLF
    /// (from の直後が LF)だった場合は局所走査で加わる LF の +1 と相殺するため 1 引く。
    /// </summary>
    private static (int CharOff, int BreaksTo) ScanForward(
        ReadOnlySpan<byte> s,
        int from,
        int fromChar,
        int fromBreaks,
        int to
    )
    {
        if (to == from)
            return (fromChar, fromBreaks);
        int ch = fromChar,
            br = fromBreaks;
        if (from > 0 && s[from - 1] == (byte)'\r' && s[from] == (byte)'\n')
            br--;
        for (int i = from; i < to; i++)
        {
            byte b = s[i];
            if ((b & 0xC0) != 0x80)
                ch++;
            if (b >= 0xF0)
                ch++;
            if (b == (byte)'\n')
                br++;
            else if (b == (byte)'\r' && (i + 1 >= to || s[i + 1] != (byte)'\n'))
                br++;
        }
        return (ch, br);
    }
}
