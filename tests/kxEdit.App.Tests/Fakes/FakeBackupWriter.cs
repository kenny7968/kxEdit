using kxEdit.Core.Backup;

namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IBackupWriter"/> のテスト用フェイク。in-memory Dictionary に格納するため
/// 実 I/O(BackupStore.Write 等)は起きない=テストが Coordinator の呼び出し配線・状態機械を
/// 純粋に観測できる。書込失敗の再現は <see cref="OnWriteFailed"/> をテスト側で直接 Invoke する。
/// </summary>
public sealed class FakeBackupWriter : IBackupWriter
{
    /// <summary>現在ディスクにあるとみなす記録(Id → 最新 record)。</summary>
    public Dictionary<string, BackupRecord> Store { get; } = new();

    /// <summary>Write 呼び出し履歴(順序保持・sig 追跡に使う)。</summary>
    public List<BackupRecord> Writes { get; } = new();

    /// <summary>Delete された Id の履歴。</summary>
    public List<string> Deletes { get; } = new();

    /// <summary>E-2: DeleteAcrossSessions の呼び出し回数(旧 DeleteAllCount)。</summary>
    public int DiscardCalls;

    /// <summary>E-2: DeleteAcrossSessions に渡された base dir(最後の 1 回)。
    /// 自セッション dir を渡す退行(=E-2 そのもの)を検出する証人。</summary>
    public string? LastDiscardBaseDir;

    /// <summary>E-2: DeleteAcrossSessions に渡された Id 群(最後の 1 回)。件数だけの assert では
    /// 「どの Id を渡したか」の変異が生き残るため中身を保持する。</summary>
    public List<string> LastDiscardIds { get; } = new();

    /// <summary>Dispose 呼び出し回数(冪等性検証に使う)。</summary>
    public int DisposeCount;

    /// <summary>hot exit 統合(Task 3): WriteLayout されたレイアウトの履歴(順序保持)。</summary>
    public List<kxEdit.Core.Session.SessionLayout> LayoutWrites { get; } = new();

    /// <summary>WriteLayout に渡された path の履歴(LayoutWrites と同順)。</summary>
    public List<string> LayoutWritePaths { get; } = new();

    /// <summary>DeleteLayout 呼び出し回数。</summary>
    public int LayoutDeletes;

    /// <summary>true なら次の WriteLayout を「失敗」させる: 書込を記録せず
    /// OnLayoutWriteFailed を同期発火し、false へ戻す(1 回限りの失敗注入)。</summary>
    public bool FailNextLayoutWrite;

    /// <summary>A-8: Fake は同期実行なので保留ジョブは常に無い。
    /// これを立てると「完了を確認できない」= timeout 相当を再現する。</summary>
    public bool WaitReturnsFalse;

    /// <summary>WaitForPendingJobs の呼び出し回数(配線の kill に使う)。</summary>
    public int WaitCalls;

    /// <summary>WaitForPendingJobs に最後に渡された timeout。呼び出し側が意図した定数
    /// (BackupCoordinator.FinalFlushWait)を渡していることを固定する(捨てると
    /// TimeSpan.Zero へ縮める変異が生き残る)。未呼び出しなら null。</summary>
    public TimeSpan? LastWaitTimeout;

    public Action<string>? OnWriteFailed { get; set; }

    public Action? OnLayoutWriteFailed { get; set; }

    public void Write(BackupRecord record)
    {
        Writes.Add(record);
        Store[record.Id] = record;
    }

    public void Delete(string id)
    {
        Deletes.Add(id);
        Store.Remove(id);
    }

    public void DeleteAcrossSessions(string baseDir, IReadOnlyList<string> ids)
    {
        DiscardCalls++;
        LastDiscardBaseDir = baseDir;
        LastDiscardIds.Clear();
        LastDiscardIds.AddRange(ids);
        // 旧 DeleteAll は Store.Clear() だったが、実装は「渡された Id だけ」を消す。
        foreach (string id in ids)
            Store.Remove(id);
    }

    public void WriteLayout(string path, kxEdit.Core.Session.SessionLayout layout)
    {
        if (FailNextLayoutWrite)
        {
            FailNextLayoutWrite = false;
            OnLayoutWriteFailed?.Invoke();
            return;
        }
        LayoutWrites.Add(layout);
        LayoutWritePaths.Add(path);
    }

    public void DeleteLayout(string path) => LayoutDeletes++;

    /// <summary>A-8: 保留ジョブの待ち合わせ。Fake は同期実行なので既定では常に完了扱い
    /// (<see cref="WaitReturnsFalse"/> で timeout 相当を注入できる)。</summary>
    public bool WaitForPendingJobs(TimeSpan timeout)
    {
        WaitCalls++;
        LastWaitTimeout = timeout;
        return !WaitReturnsFalse;
    }

    public void Dispose() => DisposeCount++;
}
