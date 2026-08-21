namespace kxEdit.Core.Backup;

/// <summary>バックアップに対する次の操作。</summary>
public enum BackupAction
{
    None,
    Write,
    Delete,
}

/// <summary>
/// 文書 1 件のバックアップ要否を決める純粋な状態機械（UI/SR・スレッド非依存で単体テスト可能）。
/// これにより BackupCoordinator のデータ安全性の中核を SR 無しで検証できる。
/// </summary>
public static class BackupPlanner
{
    /// <summary>
    /// 次に行うべきバックアップ操作を返す。
    /// <para>modified: 現在未保存（dirty）か。currentSig: 現内容の署名。lastSig: 前回退避時の署名。</para>
    /// <para>hasBackup: ディスクに当文書のバックアップが存在するか。forceWrite: 前回書込失敗等で強制再書込か。</para>
    /// </summary>
    public static BackupAction Decide(
        bool modified,
        long currentSig,
        long lastSig,
        bool hasBackup,
        bool forceWrite
    )
    {
        if (modified)
            return (forceWrite || currentSig != lastSig) ? BackupAction.Write : BackupAction.None;
        // クリーン（保存済み等）→ 既存バックアップは不要（内容はディスクと一致）。
        return hasBackup ? BackupAction.Delete : BackupAction.None;
    }
}
