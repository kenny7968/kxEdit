using kxEdit.Core.Text;

namespace kxEdit.Core.Layout;

/// <summary>
/// 文字幅と行高の計測。純レイアウトはこれ越しに測る(実 GDI は kxEdit.Editor 側)。
/// 呼び出し側はサロゲートペアを分割しない(ペアは1回の呼び出しに含める)。
/// </summary>
public interface ICharMetrics
{
    int LineHeightPx { get; }

    /// <summary>text の描画幅(px)。サロゲートペア/CJK/ASCII 混在可。</summary>
    int MeasureRun(ReadOnlySpan<char> text);

    /// <summary>
    /// <paramref name="text"/> の幅を、コードポイントごとの <see cref="MeasureRun"/> の和で返す
    /// (長い行の座標用。設計書 docs/plans/2026-10-06-long-row-geometry-design.md §3.2)。
    /// </summary>
    /// <remarks>
    /// 非 ASCII を含む run の一括計測(<see cref="MeasureRun"/>)とは一致しないことがある(カーニング・合成)。
    /// 一括計測は 43,679 字を超えると 0 を返す(GDI の制約)が、こちらは長さによらず和を返す。
    /// 既定実装は 1 コードポイントずつ <see cref="MeasureRun"/> を呼ぶ。速い実装は同じ値を返すこと。
    /// </remarks>
    int MeasureAdditive(ReadOnlySpan<char> text)
    {
        int px = 0;
        int i = 0;
        while (i < text.Length)
        {
            int cpLen = TextBoundary.CodePointLengthAt(text, i);
            px += MeasureRun(text.Slice(i, cpLen));
            i += cpLen;
        }
        return px;
    }
}
