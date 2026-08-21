namespace kxEdit.App;

/// <summary>
/// BackupCoordinator の silent catch を診断可能にする trace sink(Task 1b)。
/// 本番挙動は不変(既定 sink は Trace.TraceWarning のみ)。テストでは FakeBackupTraceSink で
/// 発火回数と category を assert する。
/// </summary>
public interface IBackupTraceSink
{
    /// <summary>非致命な失敗を通知する。category は "sweep-temp"/"sweep-old-sessions" (BK-M-2)/
    /// "load-all"/"restore-item"/"restore-item-later"/"backup-load-failed" (BK-L-6)/
    /// "backup-content-skipped" (BK-M-3) のいずれか。</summary>
    void Warn(string category, string detail, Exception? ex);
}
