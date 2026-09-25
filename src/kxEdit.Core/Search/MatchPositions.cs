namespace kxEdit.Core.Search;

/// <summary>
/// 1 つの本文に対する全ヒットの位置表(列挙順)。F3 のたびの全件列挙をやめ、
/// <see cref="TextSearcher.Locate"/> と <see cref="TextSearcher.FindPrev"/> と同じ答えを表から返す
/// (2026-09-25 フェーズ 5 P-14)。
/// </summary>
/// <remarks>
/// <para>
/// <b>二分探索してよいのは、開始位置が狭義単調増加のときだけ</b>。同じ開始位置が重なると、
/// 旧 <c>Locate</c> の「最後に一致したもの」を二分探索では選べない。.NET の Regex は
/// <c>Match(text, startat)</c> で startat より前のマッチを返すことがある(<see cref="TextSearcher.ReplaceInRange"/>
/// の doc)。列挙で同じことが起きた場合に備え、単調でなければ旧実装と同じ順序・同じ break 規則で
/// 線形に走査する。
/// </para>
/// <para>網 = <c>MatchPositionsTests</c>(旧実装のループとの照合)。</para>
/// </remarks>
internal sealed class MatchPositions
{
    private readonly int[] _starts;
    private readonly int[] _lengths;
    private readonly bool _strictlyIncreasing;

    internal MatchPositions(int[] starts, int[] lengths)
    {
        if (starts.Length != lengths.Length)
            throw new ArgumentException("開始位置と長さの数が違います。", nameof(lengths));
        _starts = starts;
        _lengths = lengths;
        _strictlyIncreasing = IsStrictlyIncreasing(starts);
    }

    /// <summary>ヒットの件数。</summary>
    public int Count => _starts.Length;

    internal bool IsStrictlyIncreasingForTest => _strictlyIncreasing;

    /// <summary><see cref="TextSearcher.Locate"/> と同じ(span が何件目か。同じヒットが複数なら最後)。</summary>
    public (int Ordinal, int Total)? Locate(MatchSpan span)
    {
        if (_strictlyIncreasing)
        {
            int i = Array.BinarySearch(_starts, span.Start);
            return i >= 0 && _lengths[i] == span.Length ? (i + 1, _starts.Length) : null;
        }
        int ordinal = 0;
        for (int k = 0; k < _starts.Length; k++)
        {
            if (_starts[k] == span.Start && _lengths[k] == span.Length)
                ordinal = k + 1;
        }
        return ordinal > 0 ? (ordinal, _starts.Length) : null;
    }

    /// <summary><see cref="TextSearcher.FindPrev"/> と同じ(開始位置が before より厳密に前にある、列挙順で最後のヒット)。</summary>
    public MatchSpan? FindPrev(int before)
    {
        // k = 「開始位置が before 以上の最初の添字」(旧実装が break する位置)
        int k;
        if (_strictlyIncreasing)
        {
            int i = Array.BinarySearch(_starts, before);
            k = i >= 0 ? i : ~i;
        }
        else
        {
            k = 0;
            while (k < _starts.Length && _starts[k] < before)
                k++;
        }
        return k > 0 ? new MatchSpan(_starts[k - 1], _lengths[k - 1]) : null;
    }

    private static bool IsStrictlyIncreasing(int[] starts)
    {
        for (int i = 1; i < starts.Length; i++)
        {
            if (starts[i] <= starts[i - 1])
                return false;
        }
        return true;
    }
}
