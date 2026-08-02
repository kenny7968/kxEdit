using yEdit.Core.Text;

namespace yEdit.Core.Layout;

/// <summary>
/// 視覚行の 1 区間(論理行内オフセット + 長さ)。
/// </summary>
public readonly record struct WrapSegment(int OffsetInLine, int Length);

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
    )
    {
        // OFF: 単一セグメント
        if (maxWidthPx <= 0)
            return new[] { new WrapSegment(0, line.Length) };

        // 空行: 高さは持つが幅ゼロの 1 セグメント
        if (line.IsEmpty)
            return new[] { new WrapSegment(0, 0) };

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
            }

            // code-point を現セグメントに加える
            segWidth += cpWidth;
            i += cpLen;
        }

        // 末尾セグメント
        result.Add(new WrapSegment(segStart, line.Length - segStart));
        return result;
    }
}
