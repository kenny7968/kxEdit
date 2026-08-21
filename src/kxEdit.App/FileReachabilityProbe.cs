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
}
