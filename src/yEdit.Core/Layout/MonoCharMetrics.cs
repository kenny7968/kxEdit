using yEdit.Core.Text;

namespace yEdit.Core.Layout;

/// <summary>ASCII=1・BMP CJK=2・サロゲートペア=2 の固定幅(テスト用)。</summary>
public sealed class MonoCharMetrics : ICharMetrics
{
    private readonly int _half;

    public MonoCharMetrics(int halfWidthPx = 8, int lineHeightPx = 16)
    {
        _half = halfWidthPx;
        LineHeightPx = lineHeightPx;
    }

    public int LineHeightPx { get; }

    public int MeasureRun(ReadOnlySpan<char> text)
    {
        int px = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (TextBoundary.CodePointLengthAt(text, i) == 2)
            {
                px += _half * 2;
                i++;
                continue;
            }
            char c = text[i];
            px += (c < 0x80 || c == '\t') ? _half : _half * 2; // ASCII/タブ=1・それ以外=2
        }
        return px;
    }
}
