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
}
