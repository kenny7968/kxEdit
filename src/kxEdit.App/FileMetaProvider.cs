using System.IO;
using kxEdit.Core.Documents;
using kxEdit.Core.IO;

namespace kxEdit.App;

/// <summary><see cref="IFileMetaProvider"/> の本番実装(<see cref="FileInfo"/> の薄いラッパ)。</summary>
public sealed class FileMetaProvider : IFileMetaProvider
{
    /// <summary>HIGH-6 / CSV-M-1 と同じ 5 秒契約(FileController.TryProbeFileExists と対称)。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>既定インスタンス(状態を持たないため共有して構わない)。</summary>
    public static readonly FileMetaProvider Instance = new();

    private readonly IReachabilityProbe _probe;

    public FileMetaProvider(IReachabilityProbe? probe = null) =>
        _probe = probe ?? new FileReachabilityProbe();

    public FileMeta? TryGet(string? path)
    {
        if (path is null)
            return null;
        try
        {
            // HIGH-6 + CSV-M-1 と同じポリシー: UNC / マップドネットワークドライブは 5 秒プローブで
            // 到達不能なら即あきらめる。FileInfo.Exists は GetFileAttributesExW を発火するため、
            // 切断済みリモートパスでは SMB タイムアウト(約 60 秒)まで UI スレッドが返らない。
            // FileController は Load/Save で同じ前置を行っており、本経路だけが素通りしていた。
            // 失敗の落とし先は他の取得失敗と同じ null(=Formatter が「-」表示)なので、
            // FileController のようなエラーダイアログは出さない(致命度が低い付随情報のため)。
            if (
                RemotePathDetector.IsRemote(path)
                && !_probe.ProbeFileExistsWithTimeout(path, ProbeTimeout)
            )
                return null;

            var fi = new FileInfo(path);
            if (!fi.Exists)
                return null;
            return new FileMeta(fi.CreationTime, fi.LastWriteTime, fi.Length);
        }
        catch
        {
            // 権限拒否・不正パス・削除との race 等はすべて「属性なし」に落とす。
            // 文書情報の付随項目でしかなく致命度が低いため、MessageBox もログも出さずに
            // Formatter の「-」表示へ委ねる(設計 2026-07-25 §8)。
            return null;
        }
    }
}
