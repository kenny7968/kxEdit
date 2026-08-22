using System.IO;

namespace kxEdit.App;

/// <summary>
/// <see cref="IReachabilityProbe"/> の本番実装。読み取り側の
/// <see cref="ProbeFileExistsWithTimeout"/> は <see cref="System.IO.File.Exists"/> を、
/// 書き込み側の <see cref="ProbeSaveTargetWithTimeout"/> は File.Exists + 親フォルダーの
/// <see cref="System.IO.Directory.Exists"/> を、それぞれ <see cref="Task.Run{TResult}(Func{TResult})"/> で
/// バックグラウンドスレッドに退避し、<see cref="Task.Wait(TimeSpan)"/> の短タイムアウトで
/// UI スレッドをブロックしない。どちらもタイムアウト時のフェイルセーフは
/// <see cref="WaitBounded{T}"/> に集約する(= 到達不能側に倒す)。
/// UNC 未到達時は 60 秒の SMB タイムアウトが走るスレッドが 1 本 leak するが、
/// まれなケースのため許容(設計書 PR-5 節)。
/// </summary>
public sealed class FileReachabilityProbe : IReachabilityProbe
{
    /// <summary>
    /// 境界付き待ちの判断。タイムアウトしたら <paramref name="onTimeout"/> を返す。
    /// 2 つの probe メソッドでフェイルセーフ規律を 1 箇所に集約するために切り出す
    /// (Task 1 コード品質レビュー I-1: タイムアウト値の変異が生存していた)。
    /// </summary>
    internal static T WaitBounded<T>(Task<T> task, TimeSpan timeout, T onTimeout) =>
        task.Wait(timeout) ? task.Result : onTimeout;

    /// <summary>
    /// 保存先プローブの骨格。<paramref name="work"/> をバックグラウンドへ退避し、
    /// 期限内に終わらなければ「到達不能」(false, false)へ倒す。
    /// <see cref="WaitBounded{T}"/> が generic でフェイルセーフ値を持てない以上、
    /// その値は呼出側に置くしかない。ここに置くのは <paramref name="work"/> を差し替えれば
    /// タイムアウト経路を**決定的に**テストできるからで、実 I/O 経由では
    /// フェイルセーフ値そのものが無被覆のまま残る(I-1 の実測: 変異が生存していた)。
    /// </summary>
    internal static SaveTargetProbeResult RunSaveTargetProbe(
        Func<SaveTargetProbeResult> work,
        TimeSpan timeout
    ) => WaitBounded(Task.Run(work), timeout, new SaveTargetProbeResult(false, false));

    /// <summary>
    /// 読み取り側プローブの骨格。<paramref name="work"/> をバックグラウンドへ退避し、
    /// 期限内に終わらなければ「存在を確認できなかった」= false へ倒す。
    /// 保存側の <see cref="RunSaveTargetProbe"/> と対称に切り出すのは、フェイルセーフ値を
    /// テストが届く場所へ置くため(再レビュー I-3): 素の
    /// <c>WaitBounded(task, timeout, false)</c> では定数が 1 トークンの引数でしかなく、
    /// true へ書き換えてもコンパイルが通り・ハングもせず・全緑になってしまう
    /// (= タイムアウトを「ファイルは在る」と読み、切断済み UNC で実 read へ進んで
    /// UI が 60 秒凍結する HIGH-6 の再導入)。
    /// </summary>
    internal static bool RunFileExistsProbe(Func<bool> work, TimeSpan timeout) =>
        WaitBounded(Task.Run(work), timeout, false);

    /// <inheritdoc />
    public bool ProbeFileExistsWithTimeout(string path, TimeSpan timeout) =>
        RunFileExistsProbe(
            () =>
            {
                try
                {
                    return File.Exists(path);
                }
                catch
                {
                    // File.Exists は通常例外を投げないが、UNC 未到達などで
                    // 稀に IOException 系が出る可能性を吸って false 扱いにする。
                    return false;
                }
            },
            timeout
        );

    /// <inheritdoc />
    public SaveTargetProbeResult ProbeSaveTargetWithTimeout(string path, TimeSpan timeout) =>
        RunSaveTargetProbe(
            () =>
            {
                try
                {
                    bool fileExists = File.Exists(path);
                    string? dir = Path.GetDirectoryName(path);
                    // dir が null = ルート自体(C:\ / \\server\share)= 親が無い。
                    // dir が空 = 相対パス(呼出側は正規化済み絶対パスを渡す契約)。
                    // どちらも書き込み先が確定しないので到達不能へ倒す。
                    bool dirExists = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
                    return new SaveTargetProbeResult(fileExists || dirExists, fileExists);
                }
                catch
                {
                    // File.Exists / Directory.Exists は通常投げないが、UNC 未到達などで稀に
                    // IOException 系が出る可能性を吸って「到達不能」に倒す
                    // (ProbeFileExistsWithTimeout と同方針)。
                    return new SaveTargetProbeResult(false, false);
                }
            },
            timeout
        );
}
