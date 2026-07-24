using System.Collections.Concurrent;
using System.Threading;
using yEdit.Core.Backup;

namespace yEdit.App;

/// <summary>
/// バックアップの背景直列ライター。UI スレッドが投入したジョブ(Core への書込/削除)を、
/// 単一の背景スレッドで投入順に実行する。各ジョブの失敗は致命でないため握り潰す(無音)が、
/// 書込(Write)の失敗のみは OnWriteFailed に record.Id を渡して UI スレッド側に通知し、
/// 次 Reconcile で強制再書込を促す(Stage 5 で IBackupWriter を実装)。
/// Dispose で投入を締め切り、保留ジョブをドレインしてから戻る。
/// BK-M-2: <c>_dir</c> は base backup directory ではなく **自セッション用 subdirectory** を保持する
/// (<c>%APPDATA%\yEdit\backups\session-{Guid.N}\</c>)。<see cref="DeleteAll"/> は
/// <see cref="BackupStore.DeleteSessionDir(string)"/> 経由で自セッション dir のみを掃除する
/// ため、他インスタンスのライブは無傷。base dir 側の LoadAll / SweepOldSessions は
/// BackupCoordinator が別途担当する。
/// </summary>
public sealed class SerialBackupWriter : IBackupWriter
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private readonly string _dir;
    private bool _disposed;

    /// <inheritdoc/>
    public Action<string>? OnWriteFailed { get; set; }

    /// <inheritdoc/>
    public Action? OnLayoutWriteFailed { get; set; }

    /// <summary>BK-M-2: <paramref name="sessionDirectory"/> は自セッション専用の subdirectory
    /// (<c>%APPDATA%\yEdit\backups\session-{Guid.N}\</c>)。base dir を渡すと flat 配置に戻り
    /// 別インスタンス影響を再導入するため、呼び出し側 (BackupCoordinator) 責務で必ず session dir
    /// を渡す契約。</summary>
    public SerialBackupWriter(string sessionDirectory)
    {
        _dir = sessionDirectory;
        _worker = new Thread(Run) { IsBackground = true, Name = "yEdit backup writer" };
        _worker.Start();
    }

    public void Write(BackupRecord record) =>
        Enqueue(() =>
        {
            try
            {
                BackupStore.Write(_dir, record);
            }
            catch
            {
                OnWriteFailed?.Invoke(record.Id);
            }
        });

    public void Delete(string id) =>
        Enqueue(() =>
        {
            try
            {
                BackupStore.Delete(_dir, id);
            }
            catch
            { /* 削除失敗は致命でない・無音 */
            }
        });

    public void DeleteAll() =>
        Enqueue(() =>
        {
            try
            {
                // BK-M-2: session dir のみを掃除する(他インスタンスの session-* / flat 配置は無傷)。
                BackupStore.DeleteSessionDir(_dir);
            }
            catch
            { /* 一括削除失敗は致命でない・無音 */
            }
        });

    public void WriteLayout(string path, yEdit.Core.Session.SessionLayout layout) =>
        Enqueue(() =>
        {
            try
            {
                yEdit.Core.Session.SessionLayoutStore.Save(path, layout);
            }
            catch
            {
                // 失敗は Write と同型で UI スレッド側へ通知 → 次 Reconcile で強制再書込(設計 E13)。
                OnLayoutWriteFailed?.Invoke();
            }
        });

    public void DeleteLayout(string path) =>
        Enqueue(() =>
        {
            try
            {
                yEdit.Core.Session.SessionLayoutStore.Delete(path);
            }
            catch
            { /* 削除失敗は致命でない・無音 */
            }
        });

    /// <summary>ジョブを投入する(締め切り後・破棄後は無視)。実装詳細。呼び出しは UI スレッド前提。</summary>
    // _disposed は volatile 不要: 書き込み(Dispose)も読み取り(Enqueue)も UI スレッドのみ。
    private void Enqueue(Action job)
    {
        // Dispose 開始後は無視(_disposed=true → CompleteAdding → Join → _queue.Dispose の順で進むため
        // この一読で破棄済み・締切済みの両方をカバー)。従来は _queue.IsAddingCompleted を try 外で読んで
        // いたが、_queue.Dispose 後は getter 自体が ObjectDisposedException を投げるため
        // 呼び出し元に伝播していた(xmldoc「破棄後は無視」の意図との乖離)。_disposed で先に遮断する。
        if (_disposed)
            return;
        // 競合で AddingCompleted 済み／破棄済み(ObjectDisposedException は InvalidOperationException 派生
        // のため 1 つの catch で両方拾える)。UI スレッド前提のため race window はごく狭いが防御的に残す。
        try
        {
            _queue.Add(job);
        }
        catch (InvalidOperationException)
        { /* AddingCompleted 済み or 破棄済み。UI スレッド前提の狭 race・無視 */
        }
    }

    private void Run()
    {
        // 列挙自体(MoveNext)も保護する。Dispose 競合で ObjectDisposedException が出ても
        // 背景スレッドを巻き添えに落とさない(未捕捉例外はプロセス終了に直結するため)。
        try
        {
            foreach (var job in _queue.GetConsumingEnumerable())
            {
                try
                {
                    job();
                }
                catch
                { /* バックアップ失敗は致命でない・無音 */
                }
            }
        }
        catch
        { /* Dispose 競合等。ワーカーを静かに終える */
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queue.CompleteAdding();
        // 保留ジョブのドレインを十分待つ(クリーン終了でバックアップ/削除を取りこぼさない)。
        bool finished = false;
        try
        {
            finished = _worker.Join(TimeSpan.FromSeconds(15));
        }
        catch
        { /* 参加待ち失敗は無視 */
        }
        // ワーカーがまだ走行中に Dispose すると MoveNext が ObjectDisposedException を投げるため、
        // 完全終了を確認できたときだけ破棄する。未終了なら放置(プロセス終了時で実害なし)。
        if (finished)
        {
            try
            {
                _queue.Dispose();
            }
            catch
            { /* 二重 Dispose 競合等は無視(プロセス終了で回収され実害なし) */
            }
        }
    }
}
