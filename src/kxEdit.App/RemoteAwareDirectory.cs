using System.IO;
using kxEdit.Core.IO;

namespace kxEdit.App;

/// <summary>
/// A-17: 「フォルダーが在るか」を UI スレッドから聞くときの唯一の入口。
/// リモート(UNC / マップドネットワークドライブ)のときだけ境界付きプローブへ回し、
/// ローカルは <see cref="Directory.Exists"/> 直呼び = <b>挙動不変・退避スレッドも作らない</b>。
///
/// <para>切断済みの共有に対する <see cref="Directory.Exists"/> は SMB タイムアウト(約 60 秒)まで
/// 返らない。grep はその答えを<b>実行前のガード</b>として UI スレッドで聞くので、
/// 素直に呼ぶと「検索を始める前に 60 秒固まる」になる(= A-17)。</para>
///
/// <para>2 呼出点(<c>GrepController.RunAsync</c> / <c>GrepDialog.InitialBrowsePath</c>)で
/// 同じ判断を繰り返さないために切り出す。タイムアウトの 5 秒は HIGH-6 / CSV-M-1 /
/// <see cref="FileTimestampProvider"/> / <see cref="FileMetaProvider"/> と同じ契約。</para>
///
/// <para><b>手前で正規化しない</b>(Task 4 の実測に基づく意図的な設計):
/// <see cref="Directory.Exists"/> は内部で <c>Path.GetFullPath</c> を通してから存在確認するので
/// 明示的な正規化は二度手間にしかならず(23 種の入力で不一致ゼロを実測)、
/// <see cref="IReachabilityProbe.NormalizePathWithTimeout"/> を前置すると<b>境界付き I/O が
/// 1 操作 2 回</b>(UI ブロック最悪 5 秒 → 10 秒・leak スレッド 2 本)になって
/// 凍結を減らすという目的に反する。詳細は
/// <see cref="IReachabilityProbe.ProbeDirectoryExistsWithTimeout"/> の doc。</para>
/// </summary>
internal static class RemoteAwareDirectory
{
    /// <summary>HIGH-6 / CSV-M-1 と同じ 5 秒契約。</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// <paramref name="path"/> のフォルダーが在るか。リモートのときだけ
    /// <paramref name="probe"/> の境界付きプローブへ回す(期限内に確定しなければ false)。
    /// </summary>
    internal static bool Exists(IReachabilityProbe probe, string path) =>
        RemotePathDetector.IsRemote(path)
            ? probe.ProbeDirectoryExistsWithTimeout(path, ProbeTimeout)
            : Directory.Exists(path);
}
