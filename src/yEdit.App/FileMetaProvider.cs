using System.IO;
using yEdit.Core.Documents;

namespace yEdit.App;

/// <summary><see cref="IFileMetaProvider"/> の本番実装(<see cref="FileInfo"/> の薄いラッパ)。</summary>
public sealed class FileMetaProvider : IFileMetaProvider
{
    /// <summary>既定インスタンス(状態を持たないため共有して構わない)。</summary>
    public static readonly FileMetaProvider Instance = new();

    public FileMeta? TryGet(string? path)
    {
        if (path is null)
            return null;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
                return null;
            return new FileMeta(fi.CreationTime, fi.LastWriteTime, fi.Length);
        }
        catch
        {
            // ネットワーク遮断・権限拒否・削除との race 等はすべて「属性なし」に落とす。
            // 文書情報の付随項目でしかなく致命度が低いため、MessageBox もログも出さずに
            // Formatter の「-」表示へ委ねる(設計 2026-07-25 §8)。
            return null;
        }
    }
}
