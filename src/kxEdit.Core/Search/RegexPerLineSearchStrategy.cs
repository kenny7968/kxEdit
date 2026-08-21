using System.Text;
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Search;

/// <summary>
/// 閾値超の正規表現照合(行単位)。1 行ずつ切り出して
/// <see cref="TextSearcher"/> に適用し、行頭オフセットを足して文書座標へ戻す。
/// </summary>
/// <remarks>
/// <para>
/// <b>選択の前提</b>: この戦略は <c>CharLength &gt; 閾値</c> かつ <c>UseRegex == true</c> の
/// ときだけ選ばれる。パターンの解釈は委譲先の <see cref="TextSearcher"/> が構築時の
/// <see cref="SearchOptions"/> から決めるため、この戦略自身は
/// <see cref="SearchOptions"/> を持たない。
/// </para>
/// <para>
/// <b>壊れる契約</b>(<see cref="SnapshotSearcher"/> の選択規則表も参照):
/// </para>
/// <list type="bullet">
///   <item>改行を跨ぐパターンは絶対にヒットしない。</item>
///   <item>アンカー(<c>^</c> / <c>$</c> / <c>\A</c> / <c>\Z</c> / <c>\G</c>)は
///     「文書の先頭/末尾」ではなく「行の先頭/末尾」に束縛される。
///     <c>SnapshotSearcherRegexAnchorTests</c> がこの挙動を凍結している。</item>
/// </list>
/// </remarks>
internal sealed class RegexPerLineSearchStrategy : ISnapshotSearchStrategy
{
    private readonly TextSearcher _inner;

    internal RegexPerLineSearchStrategy(TextSearcher inner)
    {
        _inner = inner;
    }

    // ==============================
    // Regex 行単位(閾値超)
    // ==============================

    public int Count(TextSnapshot snap)
    {
        int total = 0;
        for (int line = 0; line < snap.LineCount; line++)
        {
            string lineText = ReadLine(snap, line);
            total += _inner.Count(lineText);
        }
        return total;
    }

    public MatchSpan? FindNext(TextSnapshot snap, int from)
    {
        int startLine = snap.GetLineIndexOfChar(from);
        // 起点行: from の行内 offset から検索
        {
            int ls = snap.GetLineStart(startLine);
            int le = snap.GetLineEnd(startLine, includeBreak: false);
            int lineLen = le - ls;
            int offset = Math.Max(0, from - ls);
            if (offset <= lineLen)
            {
                string lineText = snap.GetText(ls, lineLen);
                var h = _inner.FindNext(lineText, offset);
                if (h is { } m)
                    return new MatchSpan(ls + m.Start, m.Length);
            }
        }
        for (int line = startLine + 1; line < snap.LineCount; line++)
        {
            int ls = snap.GetLineStart(line);
            int le = snap.GetLineEnd(line, includeBreak: false);
            string lineText = snap.GetText(ls, le - ls);
            var h = _inner.FindNext(lineText, 0);
            if (h is { } m)
                return new MatchSpan(ls + m.Start, m.Length);
        }
        return null;
    }

    public MatchSpan? FindPrev(TextSnapshot snap, int before)
    {
        // 文書長超の before をここでクランプする(理由と反例は ISnapshotSearchStrategy の契約表)。
        // この 1 行が上限の唯一の防御である=ファサード SnapshotSearcher.FindPrev は
        // 上限をクランプしない。外すと直下の before - 1 が TextSnapshot.GetLineIndexOfChar の
        // 範囲外になり ArgumentOutOfRangeException で落ちる。網 =
        // FindPrev_BeforePastEnd_is_clamped_by_regex_strategy_above_threshold。
        // 以降 before は (0, CharLength] に収まるが、両端の出所は分かれている:
        //   下限 (before > 0) = ファサード SnapshotSearcher.FindPrev の `before > 0` 条件。
        //     — この行では保証しない。
        //   上限 (before <= CharLength) = 直下のクランプ。
        before = Math.Min(before, snap.CharLength);

        int startLine = snap.GetLineIndexOfChar(before - 1);
        {
            int ls = snap.GetLineStart(startLine);
            int le = snap.GetLineEnd(startLine, includeBreak: false);
            int lineLen = le - ls;
            int limit = Math.Min(lineLen, before - ls); // 行内 [0, limit) の最終ヒット
            if (limit > 0)
            {
                string lineText = snap.GetText(ls, lineLen);
                var h = _inner.FindPrev(lineText, limit);
                if (h is { } m)
                    return new MatchSpan(ls + m.Start, m.Length);
            }
        }
        for (int line = startLine - 1; line >= 0; line--)
        {
            int ls = snap.GetLineStart(line);
            int le = snap.GetLineEnd(line, includeBreak: false);
            int lineLen = le - ls;
            string lineText = snap.GetText(ls, lineLen);
            var h = _inner.FindPrev(lineText, lineLen + 1); // 行内全体を対象
            if (h is { } m)
                return new MatchSpan(ls + m.Start, m.Length);
        }
        return null;
    }

    public (int Ordinal, int Total)? Locate(TextSnapshot snap, MatchSpan span)
    {
        int total = 0,
            ordinal = 0;
        bool found = false;
        for (int line = 0; line < snap.LineCount; line++)
        {
            int ls = snap.GetLineStart(line);
            int le = snap.GetLineEnd(line, includeBreak: false);
            string lineText = snap.GetText(ls, le - ls);
            int off = 0;
            while (true)
            {
                var h = _inner.FindNext(lineText, off);
                if (h is not { } m)
                    break;
                total++;
                if (ls + m.Start == span.Start && m.Length == span.Length)
                {
                    ordinal = total;
                    found = true;
                }
                // ゼロ幅ヒットの前進ガード。regex 行単位経路では `a*` 等が実際に Length=0 の
                // ヒットを返すため Math.Max(1, ...) は生きたガードであり、m.Length へ落とすと
                // off が進まず無限ループする。網 =
                // Locate_RegexZeroWidthHits_terminates_and_counts_above_threshold
                // ([Fact(Timeout = 10000)] 付き。実測でこの変異は 10.3 秒でタイムアウト FAIL する)。
                // リテラル戦略の同型の式は plen >= 1 が保証されるため実質デッドで、非対称である。
                // 「3 経路が同じ形だから」で束ねないこと。
                off = m.Start + Math.Max(1, m.Length);
                if (off > lineText.Length)
                    break;
            }
        }
        return found ? (ordinal, total) : null;
    }

    public string? ReplacementAt(TextSnapshot snap, MatchSpan span, string replacement)
    {
        if (span.Start < 0 || span.Start + span.Length > snap.CharLength)
            return null;
        int line = snap.GetLineIndexOfChar(span.Start);
        int ls = snap.GetLineStart(line);
        int le = snap.GetLineEnd(line, includeBreak: false);
        // 行を跨ぐ span は行単位契約により対象外
        if (span.Start + span.Length > le)
            return null;
        string lineText = snap.GetText(ls, le - ls);
        var lineSpan = new MatchSpan(span.Start - ls, span.Length);
        return _inner.ReplacementAt(lineText, lineSpan, replacement);
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
        int startLine = snap.GetLineIndexOfChar(start);
        int endLine = end == 0 ? 0 : snap.GetLineIndexOfChar(end - 1);

        for (int line = startLine; line <= endLine; line++)
        {
            int ls = snap.GetLineStart(line);
            int le = snap.GetLineEnd(line, includeBreak: false);
            int lineLen = le - ls;

            // 契約: Fragment は [start, end) の中身のみ(範囲外文字を混入させない)。
            // 行の範囲内 substring [rangeInLineStart, rangeInLineEnd) を _inner に投げると、
            // 内部 ReplaceInRange が「行内 substring の中身+範囲内ヒットの置換」だけを返してくれる。
            int rangeInLineStart = Math.Max(0, start - ls);
            int rangeInLineEnd = Math.Min(lineLen, end - ls);

            string lineText = snap.GetText(ls, lineLen);
            var (frag, cnt) = _inner.ReplaceInRange(
                lineText,
                rangeInLineStart,
                rangeInLineEnd - rangeInLineStart,
                replacement
            );
            sb.Append(frag);
            count += cnt;

            // 行末の改行文字(あれば)を復元(最終行=改行なし)。range が break の途中で
            // 終わるケース(選択終端が CRLF の間など)は end で切り詰める(壊れる契約許容だが安全側)。
            int breakLen = snap.GetLineEnd(line, includeBreak: true) - le;
            int emit = Math.Min(breakLen, Math.Max(0, end - le));
            if (emit > 0)
                sb.Append(snap.GetText(le, emit));
        }
        return (sb.ToString(), count);
    }

    private static string ReadLine(TextSnapshot snap, int line)
    {
        int ls = snap.GetLineStart(line);
        int le = snap.GetLineEnd(line, includeBreak: false);
        return snap.GetText(ls, le - ls);
    }
}
