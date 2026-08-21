namespace kxEdit.Core.Settings;

/// <summary>配色テーマ（前景/背景は 0xRRGGBB の RGB 値）。UI 非依存のため System.Drawing に依存しない。</summary>
public sealed record AppearanceTheme(string Id, string DisplayName, int ForeRgb, int BackRgb);

/// <summary>
/// 弱視・ハイコントラスト向けの配色テーマプリセット（カスタム RGB は対象外＝合意）。
/// </summary>
public static class AppearanceThemes
{
    public static readonly IReadOnlyList<AppearanceTheme> All = new[]
    {
        new AppearanceTheme("default", "標準（白地に黒）", 0x000000, 0xFFFFFF),
        new AppearanceTheme("white-on-black", "黒地に白", 0xFFFFFF, 0x000000),
        new AppearanceTheme("yellow-on-black", "黒地に黄", 0xFFFF00, 0x000000),
        new AppearanceTheme("green-on-black", "黒地に緑", 0x00FF00, 0x000000),
    };

    /// <summary>Id からテーマを解決する。未知 Id は標準（先頭）へフォールバック。</summary>
    public static AppearanceTheme ById(string? id)
    {
        foreach (var t in All)
            if (t.Id == id)
                return t;
        return All[0];
    }
}
