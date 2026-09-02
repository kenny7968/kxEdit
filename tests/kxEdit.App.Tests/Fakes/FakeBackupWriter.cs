using kxEdit.Core.Backup;

namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IBackupWriter"/> のテスト用フェイク。in-memory Dictionary に格納するため
/// 実 I/O(BackupStore.Write 等)は起きない=テストが Coordinator の呼び出し配線・状態機械を
/// 純粋に観測できる。
///
/// 書込失敗の再現には 2 通りあり、<b>作れる状態が違う</b>(M-20 で成功通知が入って以降):
/// - <see cref="FailNextWrite"/> = 次の 1 件の <see cref="Write"/> を失敗させる。
///   <b>失敗だけが来る</b>=実物と同じ状態。
/// - <see cref="OnWriteFailed"/> をテスト側で直接 Invoke = <see cref="Write"/> を通した後に撃つ形に
///   なるため、その Id については<b>成功通知も既に鳴っている</b>=「1 回の Write に成功と失敗の
///   両方」という<b>実物では起こらない</b>状態になる
///   (<see cref="IBackupWriter.OnWriteSucceeded"/> の契約参照)。
///
/// どちらも正当な用途がある。実物どおりの失敗を再現したいなら前者、
/// 実物では起こらない状態に消費者が正しく振る舞うかを試したいなら後者(退行テスト)。
/// 既存テストは後者のまま(失敗通知だけを購読していた頃の資産)。
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

    /// <summary>M-20: true なら次の <see cref="Write"/> 1 件を「失敗」させる:
    /// <see cref="Writes"/> / <see cref="Store"/> に記録せず <see cref="OnWriteFailed"/> だけを
    /// 同期発火し、false へ戻す(1 回限りの失敗注入・<see cref="FailNextLayoutWrite"/> と同型)。
    ///
    /// 使い方 —— 失敗させたい pass の直前に立てる:
    /// <code>
    /// host.Writer.FailNextWrite = true;
    /// host.Coordinator.Reconcile(); // この pass で最初に走る Write 1 件だけが失敗する
    /// </code>
    ///
    /// <b><see cref="OnWriteFailed"/> の直接 Invoke と使い分けること</b>(class xmldoc の 2 通り):
    /// - <b>本フラグ</b> = 実物と同じ「失敗だけが来る」状態。実物では 1 回の Write が両方鳴ることは
    ///   ないので(<see cref="IBackupWriter.OnWriteSucceeded"/> の契約)、実物どおりの失敗を
    ///   再現したいときはこちら。
    /// - <b>直接 Invoke</b> = <see cref="Write"/> を通した後に撃つので成功と失敗が両方鳴る。
    ///   <b>実物に存在しないこの状態を意図的に作りたいとき</b>(消費者がそれでも正しく振る舞うかの
    ///   退行テスト)はこちらが要る。</summary>
    public bool FailNextWrite;

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

    /// <summary>M-20: 成功通知。<b>実物との非対称に注意</b> —— Fake は <see cref="Write"/> の中で
    /// 同期発火する(=投入したその場・呼び出しスレッド)。<c>SerialBackupWriter</c> は背景スレッドが
    /// 後で撃つため<b>いつ届くかは不定</b>で、同じ Reconcile の最中に届くこともあれば次 tick 以降に
    /// なることもある。Fake はその最速端に固定した形。
    /// したがって<b>「投入したその場で成功が観測されること」を前提にしたテストは実物では成立しない</b>。
    /// 到着が遅れても壊れない形(受け取りをキューへ積んで後で読む等)で書くこと。</summary>
    public Action<string>? OnWriteSucceeded { get; set; }

    public Action? OnLayoutWriteFailed { get; set; }

    public void Write(BackupRecord record)
    {
        // M-20: 1 回限りの失敗注入(WriteLayout の FailNextLayoutWrite と同型)。
        // 記録もせず成功通知も撃たない=実物の失敗ジョブと同じく「失敗だけが来る」。
        if (FailNextWrite)
        {
            FailNextWrite = false;
            OnWriteFailed?.Invoke(record.Id);
            return;
        }
        Writes.Add(record);
        Store[record.Id] = record;
        // M-20: 実物と同じく成功を通知する(発火タイミングの非対称は OnWriteSucceeded の xmldoc 参照)。
        OnWriteSucceeded?.Invoke(record.Id);
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
