namespace kxEdit.Core.Settings;

/// <summary>
/// 配色テーマ（前景/背景/選択は 0xRRGGBB の RGB 値）。UI 非依存のため System.Drawing に依存しない。
/// </summary>
/// <param name="SelectionBackRgb">選択範囲の背景色。</param>
/// <param name="SelectionForeRgb">
/// 選択範囲の文字色。<c>null</c> は「指定しない」＝本文色のまま描くことを意味する
/// (VS Code の <c>editor.selectionForeground</c> と同じ意味論: ハイコントラストのときだけ指定する)。
/// </param>
public sealed record AppearanceTheme(
    string Id,
    string DisplayName,
    int ForeRgb,
    int BackRgb,
    int SelectionBackRgb,
    int? SelectionForeRgb
);

/// <summary>
/// 弱視・ハイコントラスト向けの配色テーマプリセット（カスタム RGB は対象外＝合意）。
/// </summary>
/// <remarks>
/// 選択色の方針(2026-09-14 設計書 §4): 標準テーマは選択文字色を<b>指定しない</b>(従来どおり
/// 薄水色の上に本文色)。黒地 3 テーマはハイコントラスト扱いで<b>反転</b>させる
/// (選択背景 = そのテーマの前景色 / 選択文字 = 黒)。
/// 選択文字色は結果として <c>BackRgb</c> と同値だが、純黒でない背景のテーマが将来入っても
/// 壊れないよう<b>独立したデータとして</b>書く。
/// </remarks>
public static class AppearanceThemes
{
    public static readonly IReadOnlyList<AppearanceTheme> All = new[]
    {
        new AppearanceTheme("default", "標準（白地に黒）", 0x000000, 0xFFFFFF, 0xADD8E6, null),
        new AppearanceTheme("white-on-black", "黒地に白", 0xFFFFFF, 0x000000, 0xFFFFFF, 0x000000),
        new AppearanceTheme("yellow-on-black", "黒地に黄", 0xFFFF00, 0x000000, 0xFFFF00, 0x000000),
        new AppearanceTheme("green-on-black", "黒地に緑", 0x00FF00, 0x000000, 0x00FF00, 0x000000),
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
