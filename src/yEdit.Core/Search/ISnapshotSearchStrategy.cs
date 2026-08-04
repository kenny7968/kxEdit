using yEdit.Core.Buffers;

namespace yEdit.Core.Search;

/// <summary>
/// <see cref="SnapshotSearcher"/> の照合方式。文書サイズと照合条件から
/// <see cref="SnapshotSearcher"/> が 1 つ選び、以後の照合を丸ごと委譲する。
/// </summary>
/// <remarks>
/// <b>実装者への契約</b>:
/// <list type="bullet">
///   <item>照合条件は有効(<see cref="TextSearcher.IsValid"/>=true)であることが保証される。
///     無効時の短絡は <see cref="SnapshotSearcher"/> 側が持つため、実装で再度ガードしない。</item>
///   <item>オフセットは全て UTF-16 コード単位。</item>
/// </list>
/// </remarks>
internal interface ISnapshotSearchStrategy
{
    /// <summary>snap 全体のヒット件数。</summary>
    int Count(TextSnapshot snap);

    /// <summary>from 以降で最初のヒット(折り返しなし)。</summary>
    MatchSpan? FindNext(TextSnapshot snap, int from);

    /// <summary>開始位置が before より厳密に前にある最後のヒット(折り返しなし)。</summary>
    MatchSpan? FindPrev(TextSnapshot snap, int before);

    /// <summary>span が全ヒット中の何件目か(1 始まり, total)。ヒットでなければ null。</summary>
    (int Ordinal, int Total)? Locate(TextSnapshot snap, MatchSpan span);

    /// <summary>span が実際のヒットなら置換文字列を返す。違えば null。</summary>
    string? ReplacementAt(TextSnapshot snap, MatchSpan span, string replacement);

    /// <summary>[start, start+length) に完全に収まるヒットだけ置換した断片と件数。</summary>
    (string Fragment, int Count) ReplaceInRange(
        TextSnapshot snap,
        int start,
        int length,
        string replacement
    );
}
