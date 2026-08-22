using System.IO;
using kxEdit.Core.IO;

namespace kxEdit.App;

/// <summary>
/// <see cref="IFileTimestampProvider"/> の本番実装。復元経路から呼ばれるため、
/// どんな入力でも例外を上位へ伝播させない(1 件の異常で全タブの復元を巻き添えにしない
/// = FileController.RestoreFromBackup のフォールバック方針と同じ)。
/// </summary>
/// <remarks>
/// 脆弱性レビュー H-1: リモートパス(UNC / マップドネットワークドライブ)は
/// <see cref="IReachabilityProbe"/> の 5 秒プローブを前置する。設計時は
/// 「<c>OriginalPathValidator.Check</c> が既に同期 I/O で触れた後のパスだけを見るので
/// 新しい凍結クラスは作らない」と考えていたが、<b>これは UNC で成立しない</b>:
/// 同 validator は <c>isUnc</c> のとき reparse 検査(唯一の I/O)をスキップするため、
/// UNC では本クラスの <see cref="File.Exists"/> が復元経路で最初の同期 I/O になる。
/// 切断済みリモートでは SMB タイムアウト(約 60 秒)まで UI スレッドが返らず、
/// 起動時にタブ数ぶん直列で発生する(HIGH-6 / CSV-M-1 / FileMetaProvider と同じ罠)。
/// </remarks>
public sealed class FileTimestampProvider : IFileTimestampProvider
{
    /// <summary>HIGH-6 / CSV-M-1 と同じ 5 秒契約(FileController.TryProbeReachability・
    /// FileMetaProvider と対称)。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly IReachabilityProbe _probe;

    /// <summary>到達不能と判明したリモートルート(<c>\\server\share</c> / <c>Z:\</c>)。
    /// 起動時復元は同じ共有上の文書を何件も含みうるため、これを憶えないと
    /// 「5 秒 × レコード数」が積み上がる(レビュー H-1 の増幅点)。
    /// 記録の効果は「その根の陳腐化判定をあきらめる = 従来どおり復元する」だけで、
    /// 安全側にしか倒れない。唯一の呼び出し元が起動時復元なので、プロセス寿命で保持して構わない。</summary>
    private readonly HashSet<string> _unreachableRoots = new(StringComparer.OrdinalIgnoreCase);

    public FileTimestampProvider(IReachabilityProbe? probe = null) =>
        _probe = probe ?? new FileReachabilityProbe();

    public DateTime? GetLastWriteTimeUtc(string path)
    {
        try
        {
            if (RemotePathDetector.IsRemote(path))
            {
                string root = RootKey(path);
                if (_unreachableRoots.Contains(root))
                    return null;
                if (!_probe.ProbeWithTimeout(path, ProbeTimeout))
                {
                    _unreachableRoots.Add(root);
                    return null;
                }
            }

            // 不在時の File.GetLastWriteTimeUtc は 1601-01-01 を返す(例外を投げない)。
            // そのまま返すと「非常に古いディスク」に見えて判定が黙って歪むため明示的に弾く。
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or NotSupportedException
                        or System.Security.SecurityException
            )
        {
            return null;
        }
    }

    /// <summary>到達不能の記録単位。<see cref="Path.GetPathRoot"/> は UNC なら
    /// <c>\\server\share</c>、マップドドライブなら <c>Z:\</c> を返す。取れなければ
    /// パス全体をキーにする(記録が効かないだけで判定の正しさは変わらない)。</summary>
    private static string RootKey(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? path : root;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
