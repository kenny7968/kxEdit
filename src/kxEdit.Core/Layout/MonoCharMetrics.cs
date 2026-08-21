using kxEdit.Core.Text;

namespace kxEdit.Core.Layout;

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
        int i = 0;
        // 他の span 系 4 箇所と同じ「長さを受け取って進む」形に揃える(二重進行を作らない)。
        // サロゲートペアは _half * 2 を 1 回だけ加える = BMP 2 文字分ではない。
        while (i < text.Length)
        {
            int cpLen = TextBoundary.CodePointLengthAt(text, i);
            char c = text[i];
            px += (cpLen == 1 && (c < 0x80 || c == '\t')) ? _half : _half * 2; // ASCII/タブ=1・それ以外=2
            i += cpLen;
        }
        return px;
    }
}
