using kxEdit.Core.Text;

namespace kxEdit.Core.Layout;

/// <summary>
/// 視覚行の 1 区間(論理行内オフセット + 長さ)。
/// </summary>
public readonly record struct WrapSegment(int OffsetInLine, int Length);

/// <summary>
/// <see cref="LineLayout.WrapFirstSegments"/> / <see cref="LineLayout.WrapThroughOffset"/> の結果。
/// <paramref name="Segments"/> は完全な <see cref="LineLayout.Wrap"/> 結果の prefix と
/// 厳密に一致する。<paramref name="ReachedLineEnd"/> が false なら打ち切られており、
/// 「最後の要素が論理行の最終セグメントである」とみなしてはならない
/// (EOL キャレット位置の判定がずれる。<see cref="VisualSegments.FindContaining"/> の
/// remarks も参照)。
/// </summary>
/// <remarks>
/// <b>等価性の注意</b>: record struct だが <paramref name="Segments"/> は参照型のため、
/// == / Equals は内容比較にならない(リストの参照同一性で比較される)。
/// 内容を比べたい場合は <paramref name="Segments"/> を要素単位で比較すること。
/// </remarks>
internal readonly record struct WrapResult(
    IReadOnlyList<WrapSegment> Segments,
    bool ReachedLineEnd
);

/// <summary>
/// 論理行 1 本を最大幅で分割する純関数(設計書 §2-3 の char-based 折り返し)。
/// 呼び出し側は改行文字を含めない(GetLineEnd(includeBreak:false) 済みの入力を渡す)。
/// </summary>
internal static class LineLayout
{
    /// <summary>
    /// line を maxWidthPx で char 単位に折り返し、視覚行の開始オフセットと長さを返す。
    /// - maxWidthPx&lt;=0 は「折り返し無し」= [ (0, line.Length) ] を返す
    /// - サロゲートペアの中間で分割しない
    /// - 折り返し境界にタブや半角/全角の混在があっても、1 文字入るなら必ず入れる(空セグメント禁止)
    /// - 空文字列は [ (0, 0) ] を返す(空行も 1 視覚行分の高さを持つ)
    /// </summary>
    /// <remarks>
    /// <b>空入力の契約</b>: <paramref name="line"/> が空のとき、必ず 1 個の空セグメント
    /// <c>[(0, 0)]</c> を返す。ViewportLayout はこの契約に依存して空行/空文書の視覚行を
    /// 確保するため、この挙動を変更してはならない(変更する場合は ViewportLayout 側も直す)。
    /// </remarks>
    public static IReadOnlyList<WrapSegment> Wrap(
        ReadOnlySpan<char> line,
        int maxWidthPx,
        ICharMetrics metrics
    ) =>
        // minSegments = int.MaxValue = 「どれだけ積んでも足りない」= 打ち切りが起きない。
        // 実装を WrapCore 1 本に保つことで、打ち切り結果が完全結果の prefix であることが
        // 構造的に保証される(2 実装間の同期に頼らない)。
        WrapCore(line, maxWidthPx, metrics, minSegments: int.MaxValue, minCoverOffset: -1).Segments;

    /// <summary>
    /// <see cref="Wrap"/> と同じ規則で折り返しつつ、先頭 <paramref name="segmentCount"/> 個の
    /// 視覚行が確定した時点で走査を打ち切る。行末まで到達した場合は要求に関わらず
    /// 全セグメントを返し、<see cref="WrapResult.ReachedLineEnd"/> に true を入れる。
    /// </summary>
    /// <remarks>
    /// 返るセグメント列が完全な <see cref="Wrap"/> 結果の prefix と厳密に一致することは
    /// <c>LineLayoutPrefixTests</c> で検証している(根拠は <see cref="WrapCore"/> の remarks)。
    /// 行全体が欲しいときは <see cref="Wrap"/> を使う。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="segmentCount"/> が 0 以下。<see cref="Wrap"/> は必ず 1 個以上返す契約なので
    /// 0 を渡すのは呼び出し側のバグ(静かに 1 として扱わず落とす)。
    /// </exception>
    public static WrapResult WrapFirstSegments(
        ReadOnlySpan<char> line,
        int maxWidthPx,
        ICharMetrics metrics,
        int segmentCount
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentCount);
        return WrapCore(line, maxWidthPx, metrics, segmentCount, minCoverOffset: -1);
    }

    /// <summary>
    /// <see cref="Wrap"/> と同じ規則で折り返しつつ、<paramref name="offsetInLine"/> を
    /// <b>含む</b>セグメントが確定した時点で走査を打ち切る
    /// (<paramref name="offsetInLine"/> は論理行内の code unit オフセット。+1 して渡す必要はない)。
    /// 行末まで到達した場合は全セグメントを返し、
    /// <see cref="WrapResult.ReachedLineEnd"/> に true を入れる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 行末オフセット(= line.Length)を渡すと必ず行末まで走る。これは仕様であり、
    /// 行末キャレットに対してこの打ち切りは効かない(設計書 §2 の非対称性)。
    /// </para>
    /// <para>
    /// <paramref name="offsetInLine"/> がサロゲートペアの内側を指していても、セグメント境界は
    /// コードポイント境界にしか置かれないため、返る prefix がペアを割ることはない。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offsetInLine"/> が負。</exception>
    public static WrapResult WrapThroughOffset(
        ReadOnlySpan<char> line,
        int maxWidthPx,
        ICharMetrics metrics,
        int offsetInLine
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offsetInLine);
        return WrapCore(line, maxWidthPx, metrics, minSegments: 1, minCoverOffset: offsetInLine);
    }

    /// <summary>
    /// <see cref="Wrap"/> / <see cref="WrapFirstSegments"/> / <see cref="WrapThroughOffset"/> の
    /// 唯一の実装。確定済みセグメント数が <paramref name="minSegments"/> 以上<b>かつ</b>
    /// 確定済みセグメントが <paramref name="minCoverOffset"/> を<b>超えて</b>カバーした時点で
    /// 打ち切る(判定はセグメントを閉じた直後にのみ行う)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 打ち切り結果が完全結果の prefix になるのは、走査が左から右への貪欲な 1 パスで、
    /// セグメント境界が<b>先行する内容だけ</b>で決まるためである。
    /// </para>
    /// <para>
    /// <b>private の事前条件</b>: <paramref name="minSegments"/> は 1 以上・
    /// <paramref name="minCoverOffset"/> は -1 以上。判定が <c>result.Add</c> の直後にしかない
    /// 以上、評価時点で <c>result.Count &gt;= 1</c> が常に成り立つため、
    /// <paramref name="minSegments"/> の 0 と 1 は<b>全入力で同一挙動</b>である
    /// (「0 = 個数の要求なし」という別の意味は存在しない)。公開 API を 2 本に分けたのは、
    /// この紛らわしいセンチネルを構造的に消すため。同様に <paramref name="minCoverOffset"/> が
    /// -1 のときは <c>segStart &gt; -1</c> が常に真=オフセットの要求なしとして働く。
    /// </para>
    /// </remarks>
    private static WrapResult WrapCore(
        ReadOnlySpan<char> line,
        int maxWidthPx,
        ICharMetrics metrics,
        int minSegments,
        int minCoverOffset
    )
    {
        // OFF: 単一セグメント
        if (maxWidthPx <= 0)
            return new WrapResult(new[] { new WrapSegment(0, line.Length) }, true);

        // 空行: 高さは持つが幅ゼロの 1 セグメント
        if (line.IsEmpty)
            return new WrapResult(new[] { new WrapSegment(0, 0) }, true);

        var result = new List<WrapSegment>();
        int segStart = 0;
        int segWidth = 0;
        int i = 0;

        while (i < line.Length)
        {
            // 次の code-point を切り出す(サロゲートペアは 2 code-unit 分)
            int cpLen = TextBoundary.CodePointLengthAt(line, i);

            int cpWidth = metrics.MeasureRun(line.Slice(i, cpLen));

            // 累積+今回の幅が max を超えるならセグメントを閉じて新セグメント開始。
            // ただし現セグメントが空(segWidth==0)なら閉じない=強制前進(空セグメント禁止)。
            if (segWidth > 0 && segWidth + cpWidth > maxWidthPx)
            {
                result.Add(new WrapSegment(segStart, i - segStart));
                segStart = i;
                segWidth = 0;

                // 打ち切り判定はセグメントを閉じた直後だけ。
                // このとき segStart = 確定済みセグメントがカバーし終えた char 数。
                if (result.Count >= minSegments && segStart > minCoverOffset)
                    return new WrapResult(result, false);
            }

            // code-point を現セグメントに加える
            segWidth += cpWidth;
            i += cpLen;
        }

        // 末尾セグメント
        result.Add(new WrapSegment(segStart, line.Length - segStart));
        return new WrapResult(result, true);
    }
}
