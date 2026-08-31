using System.IO;
using kxEdit.Core.IO;

namespace kxEdit.App;

/// <summary>
/// A-17: 「フォルダーが在るか」を UI スレッドから聞くときの唯一の入口。
/// <see cref="RemotePathDetector.IsRemote"/> が true(UNC / <c>DriveType=Network</c> のドライブ)の
/// ときだけ境界付きプローブへ回し、ローカルは <see cref="Directory.Exists"/> 直呼び
/// = <b>挙動不変・退避スレッドも作らない</b>。
///
/// <para><b>凍結が実際に起きる条件</b>(Task 5 レビューの実測。値は「約 60 秒」と書いていたが
/// 実測に置き換えた):
/// <list type="number">
/// <item><b>到達不能な UNC の直指定</b> — <c>Directory.Exists(@"\\&lt;不達ホスト&gt;\share\nosuch")</c> は
/// <b>21,002 ms</b> 返らない(実測)。grep はその答えを<b>実行前のガード</b>として UI スレッドで
/// 聞くので、素直に呼ぶと「検索を始める前に 21 秒固まる」になる = A-17 の本体。</item>
/// <item><b><c>DriveType=Network</c> のままサーバーが不達</b> — 同じ凍結が起きるはずだが
/// <b>未実測</b>(実機の切断済みマッピングを再現する必要があるため L5 送り)。</item>
/// </list>
/// 一方 <b>切断済み(reconnect 待ち)のマップドドライブは対象外</b>: 実測で
/// <c>new DriveInfo(@"W:\").DriveType</c> は <c>Network</c> ではなく <c>NoRootDirectory</c> を返し、
/// <see cref="RemotePathDetector.IsRemote"/> が false=ローカル分岐へ落ちる。この状態の
/// <c>Directory.Exists</c> は <b>2 ms</b> で返るので実害はない。つまり「マップドネットワーク
/// ドライブもプローブ対象」が成り立つのは<b>マッピングが名前空間に生きている間だけ</b>。</para>
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
