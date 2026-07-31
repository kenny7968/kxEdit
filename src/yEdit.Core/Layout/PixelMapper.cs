using yEdit.Core.Text;

namespace yEdit.Core.Layout;

/// <summary>
/// 折り返し済みセグメント内での char↔pixel マッピング(設計書 §2-3)。
/// セグメント先頭を x=0 とする純関数。P2 のキャレット位置決め・P3 のマウス衝突判定で使う。
/// </summary>
internal static class PixelMapper
{
    /// <summary>
    /// segment 内の charOffset(0..segment.Length)を pixel(0..)にマップ。
    /// - charOffset&lt;=0 → 0 / charOffset&gt;=segment.Length → 全幅
    /// - low サロゲート位置に落ちた場合は前方スナップ(pair 先頭 = charOffset-1 に寄せる)
    /// </summary>
    public static int OffsetToPx(ReadOnlySpan<char> segment, int charOffset, ICharMetrics metrics)
    {
        if (charOffset <= 0)
            return 0;
        if (charOffset > segment.Length)
            charOffset = segment.Length;

        // low サロゲート位置なら pair 先頭へ前方スナップ
        charOffset = TextBoundary.SnapToCodePointStart(segment, charOffset);

        return metrics.MeasureRun(segment[..charOffset]);
    }

    /// <summary>
    /// x(px)に最も近い code-point 境界のオフセットを返す。
    /// - x&lt;=0 → 0 / x&gt;=全幅 → segment.Length
    /// - code-point に px が食い込む場合はその code-point の直後を返す
    ///   (=「入れば含める」・選択拡張の直観に合わせる)
    /// - サロゲートペアの中間には落ちない(常に pair の直後)
    /// </summary>
    public static int PxToOffset(ReadOnlySpan<char> segment, int px, ICharMetrics metrics)
    {
        if (segment.IsEmpty)
            return 0;
        if (px <= 0)
            return 0;

        int total = metrics.MeasureRun(segment);
        if (px >= total)
            return segment.Length;

        int i = 0;
        int accumulated = 0;
        while (i < segment.Length)
        {
            // 次の code-point を切り出す(サロゲートペアは 2 code-unit 分)
            int cpLen = TextBoundary.CodePointLengthAt(segment, i);

            int cpWidth = metrics.MeasureRun(segment.Slice(i, cpLen));

            // 累積 + この code-point の幅が px 以上なら、この code-point を含めた直後を返す
            if (accumulated + cpWidth >= px)
                return i + cpLen;

            accumulated += cpWidth;
            i += cpLen;
        }

        // 早期リターンで捕捉されるはずだが安全網
        return segment.Length;
    }
}
