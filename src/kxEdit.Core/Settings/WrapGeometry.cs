namespace kxEdit.Core.Settings;

/// <summary>
/// 指定桁の表示折り返しに使う純算術（Scintilla 非依存・テスト対象）。
/// 桁は半角換算（全角=2桁）。桁幅は半角1文字のピクセル幅を単位とする。
/// </summary>
public static class WrapGeometry
{
    /// <summary>桁数 × 半角1文字幅(px) → 目標テキスト領域幅(px)。</summary>
    public static int TargetWidthPx(int columns, int halfWidthPx) => columns * halfWidthPx;

    /// <summary>テキスト領域幅と目標幅から右マージン(px)。広い分だけ空白に充てる（負にしない）。</summary>
    public static int RightMargin(int textAreaPx, int targetPx) =>
        Math.Max(0, textAreaPx - targetPx);

    /// <summary>折り返し桁数を許容範囲（10〜1000）へクランプ。破損設定・範囲外対策。</summary>
    public static int ClampColumns(int columns) => Math.Clamp(columns, 10, 1000);
}
