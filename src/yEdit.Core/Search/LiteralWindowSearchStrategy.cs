using System.Text;
using yEdit.Core.Buffers;

namespace yEdit.Core.Search;

/// <summary>
/// 閾値超のリテラル照合(窓照合)。全文を材質化せず、
/// <c>windowSize</c> のウィンドウ + パターン長 -1 の overlap で走査する。
/// </summary>
/// <remarks>
/// <b>壊れる契約</b>(<see cref="SnapshotSearcher"/> の要約表も参照):
/// <see cref="SearchOptions.WholeWord"/> はエンジン内蔵の Unicode <c>\b</c> ではなく
/// ASCII 単純判定(<see cref="IsWordChar"/>)= 全角英数境界で
/// 材質化経路と差が出うる。
/// </remarks>
internal sealed class LiteralWindowSearchStrategy : ISnapshotSearchStrategy
{
    private readonly SearchOptions _opts;
    private readonly int _windowSize;

    internal LiteralWindowSearchStrategy(SearchOptions opts, int windowSize)
    {
        _opts = opts;
        _windowSize = windowSize;
    }

    // ==============================
    // リテラル窓照合(閾値超)
    // ==============================

    private StringComparison GetLiteralComparison()
    {
        // TextSearcher は RegexOptions.CultureInvariant + IgnoreCase = char 単位の ToUpperInvariant 折り畳み
        // (合字折り畳みなし)。これは StringComparison.OrdinalIgnoreCase と等価。InvariantCulture 系は
        // 合字折り畳み(ß↔ss 等)が発生して既存挙動と食い違うため使わない。
        return _opts.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }

    public int Count(TextSnapshot snap)
    {
        int total = 0;
        int pos = 0;
        while (true)
        {
            var hit = FindNext(snap, pos);
            if (hit is not { } h)
                break;
            total++;
            pos = h.Start + Math.Max(1, h.Length);
            if (pos > snap.CharLength)
                break;
        }
        return total;
    }

    public MatchSpan? FindNext(TextSnapshot snap, int from)
    {
        int total = snap.CharLength;
        string pattern = _opts.Pattern;
        int plen = pattern.Length;
        if (plen == 0 || from >= total)
            return null;

        var cmp = GetLiteralComparison();
        int windowSize = Math.Max(_windowSize, plen * 2);
        int overlap = Math.Max(0, plen - 1);
        int pos = from;

        while (pos < total)
        {
            int chunkLen = Math.Min(windowSize, total - pos);
            // ウィンドウ末尾のヒットが窓境界を跨ぐケースは overlap で確実に取りこぼさない
            string chunk = snap.GetText(pos, chunkLen);
            int idx = chunk.IndexOf(pattern, cmp);
            while (idx >= 0)
            {
                int absStart = pos + idx;
                if (!_opts.WholeWord || IsWordBoundaryMatch(snap, absStart, plen))
                    return new MatchSpan(absStart, plen);
                // 次の候補=idx+1 から続けて同ウィンドウ内を探索
                int nextStart = idx + 1;
                if (nextStart > chunk.Length - plen)
                    break;
                idx = chunk.IndexOf(pattern, nextStart, cmp);
            }
            if (chunkLen < windowSize)
                break; // 最終窓
            pos += windowSize - overlap;
        }
        return null;
    }

    public MatchSpan? FindPrev(TextSnapshot snap, int before)
    {
        // 文書長超の before はここでクランプする。閾値以下(材質化)経路は生の before を
        // TextSearcher へ渡す=ゼロ幅ヒットの有無で結果が変わるため、クランプはファサードへ
        // 集約できない。クランプを必要とする戦略が自分で持つ。
        before = Math.Min(before, snap.CharLength);

        string pattern = _opts.Pattern;
        int plen = pattern.Length;
        if (plen == 0)
            return null;

        var cmp = GetLiteralComparison();
        int windowSize = Math.Max(_windowSize, plen * 2);
        int overlap = Math.Max(0, plen - 1);
        // ヒット開始が before-1 まで、ヒット終端は before+overlap まで拡張して読み込む必要がある
        int end = Math.Min(before + overlap, snap.CharLength);

        while (end > 0)
        {
            int chunkStart = Math.Max(0, end - windowSize);
            int chunkLen = end - chunkStart;
            string chunk = snap.GetText(chunkStart, chunkLen);
            int idx = chunk.LastIndexOf(pattern, cmp);
            while (idx >= 0)
            {
                int absStart = chunkStart + idx;
                if (
                    absStart < before
                    && (!_opts.WholeWord || IsWordBoundaryMatch(snap, absStart, plen))
                )
                    return new MatchSpan(absStart, plen);
                if (idx == 0)
                    break;
                idx = chunk.LastIndexOf(pattern, idx - 1, cmp);
            }
            if (chunkStart == 0)
                break;
            end = chunkStart + overlap;
        }
        return null;
    }

    public (int Ordinal, int Total)? Locate(TextSnapshot snap, MatchSpan span)
    {
        int total = 0,
            ordinal = 0;
        bool found = false;
        int pos = 0;
        while (true)
        {
            var hit = FindNext(snap, pos);
            if (hit is not { } h)
                break;
            total++;
            if (h.Start == span.Start && h.Length == span.Length)
            {
                ordinal = total;
                found = true;
            }
            pos = h.Start + Math.Max(1, h.Length);
            if (pos > snap.CharLength)
                break;
        }
        return found ? (ordinal, total) : null;
    }

    public string? ReplacementAt(TextSnapshot snap, MatchSpan span, string replacement)
    {
        if (span.Start < 0 || span.Start + span.Length > snap.CharLength)
            return null;
        if (span.Length != _opts.Pattern.Length)
            return null;
        string actual = snap.GetText(span.Start, span.Length);
        if (!actual.Equals(_opts.Pattern, GetLiteralComparison()))
            return null;
        if (_opts.WholeWord && !IsWordBoundaryMatch(snap, span.Start, span.Length))
            return null;
        return replacement; // リテラル: $ 展開なし
    }

    public (string Fragment, int Count) ReplaceInRange(
        TextSnapshot snap,
        int start,
        int length,
        string replacement
    )
    {
        int end = start + length;

        var sb = new StringBuilder();
        int count = 0;
        int pos = start;
        while (pos < end)
        {
            var hit = FindNext(snap, pos);
            if (hit is not { } h)
                break;
            if (h.Start + h.Length > end)
                break; // 範囲またぎ・範囲外は除外
            if (h.Start > pos)
                sb.Append(snap.GetText(pos, h.Start - pos));
            sb.Append(replacement);
            pos = h.Start + Math.Max(1, h.Length);
            count++;
        }
        if (pos < end)
            sb.Append(snap.GetText(pos, end - pos));
        return (sb.ToString(), count);
    }

    // ==============================
    // WholeWord 判定(閾値超・素朴 ASCII \w)
    // ==============================

    private static bool IsWordChar(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';

    /// <summary>\b と等価の zero-width 判定=前後の "word char" 属性が異なる境界。</summary>
    private static bool IsBoundary(TextSnapshot snap, int pos)
    {
        bool beforeIsWord = pos > 0 && IsWordChar(snap.GetChar(pos - 1));
        bool atIsWord = pos < snap.CharLength && IsWordChar(snap.GetChar(pos));
        return beforeIsWord != atIsWord;
    }

    private static bool IsWordBoundaryMatch(TextSnapshot snap, int start, int len) =>
        IsBoundary(snap, start) && IsBoundary(snap, start + len);
}
