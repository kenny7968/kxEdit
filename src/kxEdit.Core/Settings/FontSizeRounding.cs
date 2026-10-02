namespace kxEdit.Core.Settings;

/// <summary>
/// フォントダイアログが返した大きさ(pt)を 0.5pt 単位に丸める純関数。
/// FontDialog は LOGFONT の整数ピクセル高から Font を作り直すので、96 DPI では
/// 20pt → 26.67px → 27px → 20.25pt になり、「20.3 pt」と表示・保存されていた。
/// </summary>
public static class FontSizeRounding
{
    /// <summary>
    /// 丸めた結果の下限(pt)。288 DPI では 1px = 0.25pt で、2 倍の中点 0.5 が 0 に丸まる。
    /// 0 は設定の読み込みで既定の 12pt に補正されてしまうため、ここで止める。
    /// </summary>
    public const float MinPoints = 0.5f;

    /// <summary>
    /// 2 倍して <see cref="MidpointRounding.ToEven"/> で整数に丸め、2 で割る。
    /// 96 DPI の値は 0.75pt の倍数なので、奇数ピクセルでは必ず x.5 の中点になる。
    /// ToEven は中点を偶数(= 整数 pt)に寄せる(20.25 → 20、9.75 → 10)。中点でない値は残る(10.5 → 10.5)。
    /// 有限の値を前提にする(FontDialog の <c>Font.Size</c> は常に有限の正の値)。NaN と +∞ はそのまま返る。
    /// </summary>
    public static float ToHalfPoint(float points)
    {
        double doubled = Math.Round((double)points * 2, MidpointRounding.ToEven);
        return Math.Max(MinPoints, (float)(doubled / 2));
    }
}
