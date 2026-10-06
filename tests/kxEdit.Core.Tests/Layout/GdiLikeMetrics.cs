using kxEdit.Core.Layout;
using kxEdit.Core.Text;

namespace kxEdit.Core.Tests.Layout;

/// <summary>
/// GDI(<c>GdiCharMetrics</c>)の計測の性質を写した偽のメトリクス(設計書 2026-10-06 §4.1)。
/// 1 コードポイントの幅は ASCII とタブが 1、それ以外(サロゲートペアを含む)が 2。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>ASCII だけの run は 1 文字ずつの足し算(<c>GdiCharMetrics</c> の ASCII 経路と同じ)。</item>
/// <item>非 ASCII を含み 2 コードポイント以上の run は、足し算より 1 小さい(一括計測が加算的でないことの模擬)。</item>
/// <item>非 ASCII を含み <see cref="GdiRunLimit"/> 字を超える run は 0(GDI が上限を超えると幅 0 を返す。2026-10-06 実測)。</item>
/// </list>
/// <see cref="ICharMetrics.MeasureAdditive"/> は上書きしない(既定実装を使う)。
/// </remarks>
internal sealed class GdiLikeMetrics : ICharMetrics
{
    /// <summary>GDI が幅を返せる最大の文字数(Uniscribe の制約。F-1 設計書 §2.1)。</summary>
    internal const int GdiRunLimit = 43_679;

    private readonly MonoCharMetrics _mono = new(halfWidthPx: 1, lineHeightPx: 10);

    public int LineHeightPx => _mono.LineHeightPx;

    public int MeasureRun(ReadOnlySpan<char> text)
    {
        int sum = _mono.MeasureRun(text);
        bool hasNonAscii = false;
        foreach (char c in text)
        {
            if (c >= 128)
            {
                hasNonAscii = true;
                break;
            }
        }
        if (!hasNonAscii)
            return sum;
        if (text.Length > GdiRunLimit)
            return 0;
        bool singleCodePoint = TextBoundary.CodePointLengthAt(text, 0) == text.Length;
        return singleCodePoint ? sum : sum - 1;
    }
}
