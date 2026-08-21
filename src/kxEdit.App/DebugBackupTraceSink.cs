using System.Diagnostics;

namespace kxEdit.App;

/// <summary>
/// 既定の trace sink。System.Diagnostics.Trace.TraceWarning に流す。本番 UI/SR/保存挙動への
/// 副作用なし(Trace リスナが有効な場合のみ診断出力に流れる)。例外は依然握り潰される点も
/// Task 1b 前と同じ。
/// </summary>
public sealed class DebugBackupTraceSink : IBackupTraceSink
{
    public void Warn(string category, string detail, Exception? ex)
    {
        var msg = ex is null
            ? $"[backup:{category}] {detail}"
            : $"[backup:{category}] {detail} :: {ex.GetType().Name}: {ex.Message}";
        Trace.TraceWarning(msg);
    }
}
