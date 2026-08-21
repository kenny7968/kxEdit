namespace kxEdit.Core.IO;

/// <summary>
/// パスが UNC 形式または DOS デバイスパス(\\ で始まる)かの純粋述語。
/// \\server\share, \\?\UNC\server\share, \\.\PhysicalDrive0 いずれも true。
/// マップドネットワークドライブ経由の判定は含まない(将来の MEDIUM リリースで扱う)。
/// </summary>
public static class UncPathDetector
{
    public static bool IsUnc(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        return path.StartsWith(@"\\", StringComparison.Ordinal);
    }
}
