using System.IO;

namespace kxEdit.App;

/// <summary>
/// <see cref="IReachabilityProbe"/> の本番実装。<see cref="System.IO.File.Exists"/> を
/// <see cref="Task.Run(Func{bool})"/> でバックグラウンドスレッドに退避し、
/// <see cref="Task.Wait(TimeSpan)"/> の短タイムアウトで UI スレッドをブロックしない。
/// UNC 未到達時は 60 秒の SMB タイムアウトが走るスレッドが 1 本 leak するが、
/// まれなケースのため許容(設計書 PR-5 節)。
/// </summary>
public sealed class FileReachabilityProbe : IReachabilityProbe
{
    public bool ProbeWithTimeout(string path, TimeSpan timeout)
    {
        var task = Task.Run(() =>
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
        });
        return task.Wait(timeout) && task.Result;
    }

    /// <inheritdoc />
    public SaveTargetProbe ProbeSaveTargetWithTimeout(string path, TimeSpan timeout)
    {
        var task = Task.Run(() =>
        {
            try
            {
                bool fileExists = File.Exists(path);
                string? dir = System.IO.Path.GetDirectoryName(path);
                // dir が null/空 = ルート自体("C:\")を指す入力。ファイルとしては保存できないので
                // 到達不能側に落とす(親フォルダーが無い=書き込み先が確定しない)。
                bool dirExists = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
                return new SaveTargetProbe(fileExists || dirExists, fileExists);
            }
            catch
            {
                // File.Exists / Directory.Exists は通常投げないが、UNC 未到達などで稀に
                // IOException 系が出る可能性を吸って「到達不能」に倒す(ProbeWithTimeout と同方針)。
                return new SaveTargetProbe(false, false);
            }
        });
        return task.Wait(timeout) ? task.Result : new SaveTargetProbe(false, false);
    }
}
