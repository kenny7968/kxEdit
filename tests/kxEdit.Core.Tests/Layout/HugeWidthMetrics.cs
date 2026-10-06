using kxEdit.Core.Layout;
using kxEdit.Core.Text;

namespace kxEdit.Core.Tests.Layout;

/// <summary>
/// 1 コードポイントの幅が <see cref="CodePointPx"/>(2^17 px)の偽のメトリクス(最終レビュー I-1)。
/// 16,384 コードポイントで幅の和が 2^31 に届き、int で足すと負へ回り込む。
/// <see cref="MeasureRun"/> は 1 コードポイントずつ呼ばれる前提(複数なら long で足して int.MaxValue で頭打ち)。
/// <see cref="ICharMetrics.MeasureAdditive"/> は上書きしない(既定実装を使う)。
/// </summary>
internal sealed class HugeWidthMetrics : ICharMetrics
{
    internal const int CodePointPx = 1 << 17;

    public int LineHeightPx => 10;

    public int MeasureRun(ReadOnlySpan<char> text)
    {
        long px = 0;
        int i = 0;
        while (i < text.Length)
        {
            px += CodePointPx;
            i += TextBoundary.CodePointLengthAt(text, i);
        }
        return (int)Math.Min(px, int.MaxValue);
    }
}
