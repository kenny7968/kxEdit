using System.IO;
using kxEdit.Core.IO;

namespace kxEdit.App;

/// <summary>
/// A-17: 「フォルダーが在るか」を<b>境界付きで</b>聞くときの共通入口
/// (導入時は grep 経路専用だった。現在の呼出点は下の「grep 専用ではなくなった」の段落を参照)。
/// <see cref="RemotePathDetector.IsRemote"/> が true(UNC / <c>DriveType=Network</c> のドライブ)の
/// ときだけ境界付きプローブへ回し、ローカルは <see cref="Directory.Exists"/> 直呼び
/// = <b>挙動不変・退避スレッドも作らない</b>。
///
/// <para><b>凍結が実際に起きる条件</b>(Task 5 レビューの実測。値は「約 60 秒」と書いていたが
/// 実測に置き換えた):
/// <list type="number">
/// <item><b>到達不能な UNC の直指定</b> — <c>Directory.Exists</c> が <b>21,002 ms</b> 返らない(実測)。
/// <b>この 21 秒は「445 への SYN が黙って落とされるホスト」に限った値</b>(最終レビューで再実測):
/// 経路の無い IP 直指定(RFC 5737 の <c>198.51.100.7</c>)で <b>21,001 ms</b>、
/// 一方で<b>名前解決そのものに失敗するホスト名</b>(<c>\\unreachable-host\share\nosuch</c> —
/// テストの fixture がこの形)は <b>1,234 ms</b> で false が返る。桁が違うので
/// 「不達ホスト = 21 秒」と読まないこと。凍結の主因は名前解決ではなく TCP の再送タイムアウト。
/// grep はその答えを<b>実行前のガード</b>として UI スレッドで聞くので、素直に呼ぶと
/// 「検索を始める前に 21 秒固まる」になる = A-17 の本体。</item>
/// <item><b><c>DriveType=Network</c> のままサーバーが不達</b> — 同じ凍結が起きるはずだが
/// <b>未実測</b>(実機の切断済みマッピングを再現する必要があるため L5 送り)。</item>
/// </list>
/// 一方 <b>切断済み(reconnect 待ち)のマップドドライブは対象外</b>: 実測で
/// <c>new DriveInfo(@"W:\").DriveType</c> は <c>Network</c> ではなく <c>NoRootDirectory</c> を返し、
/// <see cref="RemotePathDetector.IsRemote"/> が false=ローカル分岐へ落ちる。この状態の
/// <c>Directory.Exists</c> は <b>2 ms</b> で返るので実害はない。つまり「マップドネットワーク
/// ドライブもプローブ対象」が成り立つのは<b>マッピングが名前空間に生きている間だけ</b>。</para>
///
/// <para>切り出し当時の 2 呼出点(<c>GrepController.RunAsync</c> /
/// <c>GrepDialog.InitialBrowsePath</c>)で同じ判断を繰り返さないために切り出した
/// (現在の呼出点は下の「grep 専用ではなくなった」の段落を参照)。
/// タイムアウトの 5 秒は HIGH-6 / CSV-M-1 /
/// <see cref="FileTimestampProvider"/> / <see cref="FileMetaProvider"/> と同じ契約。</para>
///
/// <para><b>grep 専用ではなくなった</b>(2026-09-03・B6)。
/// <c>MarkdownPreviewForm.InitAsync</c> も本クラスを使う —— ただし
/// <b>UI スレッドから直接ではなく <c>Task.Run</c> 越しに await する</b>形で、
/// UI スレッドはブロックしない(grep は同期呼び出しのまま)。
/// かつての申し送り「プレビュー側を『ついでに』境界付きにしてはいけない
/// (到達不能を『フォルダーが無い』に畳むと未マップになり監査 §9 V-2 を踏む)」は、
/// <b>プレビュー側が空フォルダーへ倒すフェイルセーフを持ったことで解消した</b>
/// (<c>docs/plans/2026-09-03-preview-csp-virtual-host-design.md</c> §13.2)。
/// 警告の本旨は「境界付きにするな」ではなく「フェイルセーフとセットでなければするな」だった。</para>
///
/// <para><b>手前で正規化しない</b>(Task 4 の実測に基づく意図的な設計):
/// <see cref="Directory.Exists"/> は内部で <c>Path.GetFullPath</c> を通してから存在確認するので
/// 明示的な正規化は二度手間にしかならず(23 種の入力で不一致ゼロ。<b>ただしこの「不一致ゼロ」の
/// 数え方には誤りがある</b> —— 詳細と訂正は
/// <see cref="IReachabilityProbe.ProbeDirectoryExistsWithTimeout"/> の doc)、
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
