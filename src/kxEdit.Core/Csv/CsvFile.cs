namespace kxEdit.Core.Csv;

/// <summary>CSV ファイルの判定。メニュー活性化・オープン時検出・テストで共用する。</summary>
public static class CsvFile
{
    /// <summary>パスの拡張子が .csv（大文字小文字無視）なら true。null・拡張子なし・他拡張子は false。</summary>
    public static bool IsCsvPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        return string.Equals(
            System.IO.Path.GetExtension(path),
            ".csv",
            StringComparison.OrdinalIgnoreCase
        );
    }
}
