namespace kxEdit.Core.Text;

/// <summary>
/// 同一ファイル判定用の正規化キー。Windows 前提で大文字小文字を無視し、
/// GetFullPath で相対パス・区切り文字差を吸収する。正規化できない場合は
/// 空文字を返し、「invalid はまとめて 1 件」に集約する（CSV-L-8）。
/// </summary>
public static class PathKey
{
    public static string For(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            // CSV-L-8 (v0.11): GetFullPath 例外時は攻撃者制御の生 path を返すのを避け、
            // 空文字（= dedup 用の invariant「invalid はまとめて 1 件」）に落とす。
            return string.Empty;
        }
        return full.ToLowerInvariant();
    }
}
