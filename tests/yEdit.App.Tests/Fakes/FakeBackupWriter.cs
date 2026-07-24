using yEdit.Core.Backup;

namespace yEdit.App.Tests.Fakes;

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

    /// <summary>DeleteAll 呼び出し回数。</summary>
    public int DeleteAllCount;

    /// <summary>Dispose 呼び出し回数(冪等性検証に使う)。</summary>
    public int DisposeCount;

    /// <summary>hot exit 統合(Task 3): WriteLayout されたレイアウトの履歴(順序保持)。</summary>
    public List<yEdit.Core.Session.SessionLayout> LayoutWrites { get; } = new();

    /// <summary>WriteLayout に渡された path の履歴(LayoutWrites と同順)。</summary>
    public List<string> LayoutWritePaths { get; } = new();

    /// <summary>DeleteLayout 呼び出し回数。</summary>
    public int LayoutDeletes;

    /// <summary>true なら次の WriteLayout を「失敗」させる: 書込を記録せず
    /// OnLayoutWriteFailed を同期発火し、false へ戻す(1 回限りの失敗注入)。</summary>
    public bool FailNextLayoutWrite;

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

    public void DeleteAll()
    {
        DeleteAllCount++;
        Store.Clear();
    }

    public void WriteLayout(string path, yEdit.Core.Session.SessionLayout layout)
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

    public void Dispose() => DisposeCount++;
}
