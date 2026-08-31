using System.IO;
using kxEdit.Core.IO;

namespace kxEdit.App;

/// <summary>
/// A-17: 「フォルダーが在るか」を UI スレッドから聞くときの、<b>grep 経路の</b>唯一の入口。
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
/// <para>2 呼出点(<c>GrepController.RunAsync</c> / <c>GrepDialog.InitialBrowsePath</c>)で
/// 同じ判断を繰り返さないために切り出す。タイムアウトの 5 秒は HIGH-6 / CSV-M-1 /
/// <see cref="FileTimestampProvider"/> / <see cref="FileMetaProvider"/> と同じ契約。</para>
///
/// <para><b>「唯一の入口」は grep 経路に限った話で、App 全体ではない</b>(最終レビュー I-2)。
/// <c>MarkdownPreviewForm.InitAsync</c> が <c>Directory.Exists(_baseDir)</c> を
/// <b>UI スレッドで無境界に</b>呼んでいる(<c>Shown += async … await InitAsync()</c> の継続は
/// WinForms の SynchronizationContext で UI スレッドへ戻る)。<c>_baseDir</c> は
/// <c>MainForm</c> が <c>Path.GetDirectoryName(doc.State.Path)</c> で作るので、
/// <b>共有上の .md を開いた後にその共有が不達になるとプレビュー表示で同じ 21 秒が起きる</b> ——
/// つまり<b>同じバグクラスの未修正箇所</b>である。A-17 の定義は grep の 2 か所なので
/// <b>本ブランチのスコープ外</b>とし、プレビュー系の別ブランチ(監査 §9 の V-2〜V-6)で回収する。</para>
///
/// <para><b>プレビュー側を「ついでに」境界付きにしてはいけない</b>(申し送りの前提):
/// ここを false へ倒すと <c>SetVirtualHostNameToFolderMapping</c> を張らなくなり、
/// <c>.preview</c> 仮想ホストの<b>実 DNS 解決</b>が起きる V-2 の経路を踏ませうる。
/// 到達不能を「フォルダーが無い」に畳む本クラスの意味論は、そのフェイルセーフとは
/// 向きが逆なので、<b>プレビュー側の設計判断とセットで扱う</b>。</para>
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
