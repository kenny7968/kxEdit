using yEdit.Core.Text;

namespace yEdit.Core.Layout;

/// <summary>
/// 視覚行の 1 区間(論理行内オフセット + 長さ)。
/// </summary>
public readonly record struct WrapSegment(int OffsetInLine, int Length);

/// <summary>
/// <see cref="LineLayout.WrapPrefix"/> の結果。
/// <paramref name="Segments"/> は完全な <see cref="LineLayout.Wrap"/> 結果の prefix と
/// 厳密に一致する。<paramref name="ReachedLineEnd"/> が false なら打ち切られており、
/// 「最後の要素が論理行の最終セグメントである」とみなしてはならない
/// (EOL キャレット位置の判定がずれる)。
/// </summary>
/// <remarks>
/// <b>等価性の注意</b>: record struct だが <paramref name="Segments"/> は参照型のため、
/// == / Equals は内容比較にならない(リストの参照同一性で比較される)。
/// 内容を比べたい場合は <paramref name="Segments"/> を要素単位で比較すること。
/// </remarks>
public readonly record struct WrapResult(IReadOnlyList<WrapSegment> Segments, bool ReachedLineEnd);

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
        // 実装を WrapPrefix 1 本に保つことで、打ち切り結果が完全結果の prefix であることが
        // 構造的に保証される(2 実装間の同期に頼らない)。
        WrapPrefix(
            line,
            maxWidthPx,
            metrics,
            minSegments: int.MaxValue,
            minCoverOffset: -1
        ).Segments;

    /// <summary>
    /// <see cref="Wrap"/> と同じ規則で折り返しつつ、要求を満たした時点で走査を打ち切る。
    /// 打ち切り条件は次の<b>両方</b>が満たされたとき(セグメントを閉じた直後にのみ判定する)。
    /// <list type="bullet">
    /// <item>確定済みセグメント数が <paramref name="minSegments"/> 以上
    ///   (0 = 個数の要求なし)</item>
    /// <item>確定済みセグメントが <paramref name="minCoverOffset"/> を<b>超えて</b>カバー
    ///   (-1 = オフセットの要求なし)</item>
    /// </list>
    /// 行末まで到達したら要求に関わらず全セグメントを返し、
    /// <see cref="WrapResult.ReachedLineEnd"/> に true を入れる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 打ち切り結果が完全結果の prefix になるのは、<see cref="Wrap"/> が左から右への
    /// 貪欲な走査で、セグメント境界が<b>先行する内容だけ</b>で決まるためである。
    /// この性質は <c>LineLayoutPrefixTests</c> で検証している。
    /// </para>
    /// <para>
    /// <b>呼び出し方</b>:
    /// 「先頭 n 視覚行が欲しい」なら <c>minSegments: n, minCoverOffset: -1</c>。
    /// 「オフセット c を含むセグメントまで欲しい」なら <c>minSegments: 0, minCoverOffset: c</c>
    /// (c 自身を<b>含む</b>セグメントまで返る。+1 は不要)。
    /// 「行全体が欲しい」なら <see cref="Wrap"/> を使う
    /// —— 両方を「要求なし」(<c>0, -1</c>)にすると最初のセグメント境界で打ち切られる。
    /// </para>
    /// <para>
    /// 「要求なし」の値が 2 引数で異なる(<paramref name="minSegments"/> は 0・
    /// <paramref name="minCoverOffset"/> は -1)ため、範囲外
    /// (<paramref name="minSegments"/> が負・<paramref name="minCoverOffset"/> が -1 未満)は
    /// 静かに縮退させず <see cref="ArgumentOutOfRangeException"/> にする
    /// (<c>minSegments: -1</c> を「無制限」の意味で渡す誤用を実行時に露出させるため)。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minSegments"/> が負、または <paramref name="minCoverOffset"/> が -1 未満。
    /// </exception>
    public static WrapResult WrapPrefix(
        ReadOnlySpan<char> line,
        int maxWidthPx,
        ICharMetrics metrics,
        int minSegments,
        int minCoverOffset
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minSegments);
        ArgumentOutOfRangeException.ThrowIfLessThan(minCoverOffset, -1);

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
