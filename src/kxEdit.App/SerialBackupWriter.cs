using System.Collections.Concurrent;
using System.Threading;
using kxEdit.Core.Backup;

namespace kxEdit.App;

/// <summary>
/// バックアップの背景直列ライター。UI スレッドが投入したジョブ(Core への書込/削除)を、
/// 単一の背景スレッドで投入順に実行する。各ジョブの失敗は致命でないため握り潰す(無音)が、
/// 書込(Write)だけは結果を通知する: 失敗は OnWriteFailed に record.Id を渡して
/// 次 Reconcile での強制再書込を促し(Stage 5 で IBackupWriter を実装)、成功は
/// M-20(B5)で足した OnWriteSucceeded に record.Id を渡す。どちらも背景スレッドから同期発火する
/// (通知フックの契約は IBackupWriter 側の xmldoc が正)。
/// Dispose で投入を締め切り、保留ジョブをドレインしてから戻る。
/// BK-M-2: <c>_dir</c> は base backup directory ではなく **自セッション用 subdirectory** を保持する
/// (<c>%APPDATA%\kxEdit\backups\session-{Guid.N}\</c>)。<see cref="Write"/> / <see cref="Delete"/> は
/// この dir に束縛される。「すべて破棄」だけは例外で、<see cref="DeleteAcrossSessions"/> が
/// 引数の base dir を横断する(E-2)。base dir 側の LoadAll / SweepOldSessions は
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
    public Action<string>? OnWriteSucceeded { get; set; }

    /// <inheritdoc/>
    public Action? OnLayoutWriteFailed { get; set; }

    /// <summary>BK-M-2: <paramref name="sessionDirectory"/> は自セッション専用の subdirectory
    /// (<c>%APPDATA%\kxEdit\backups\session-{Guid.N}\</c>)。base dir を渡すと flat 配置に戻り
    /// 別インスタンス影響を再導入するため、呼び出し側 (BackupCoordinator) 責務で必ず session dir
    /// を渡す契約。</summary>
    public SerialBackupWriter(string sessionDirectory)
    {
        _dir = sessionDirectory;
        _worker = new Thread(Run) { IsBackground = true, Name = "kxEdit backup writer" };
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
                return;
            }
            // M-20: 成功通知は try の**外**で撃つ。try の中(BackupStore.Write の直後)に置くと、
            // フック自身が投げた場合に上の catch が拾って OnWriteFailed を鳴らす=書けているのに
            // 「書込が失敗した」と報告する経路を新設してしまう(B5 が潰そうとしている虚偽通知そのもの)。
            // catch 側の early return は、この位置と対で「成功と失敗のどちらか一方だけ」を保つ。
            // フックが投げても Run のジョブ単位 catch が握るのでワーカーは死なず後続ジョブも走るが、
            // 握るだけで再通知はしない=フックは投げない契約(IBackupWriter 側の xmldoc)。
            OnWriteSucceeded?.Invoke(record.Id);
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

    public void DeleteAcrossSessions(string baseDir, IReadOnlyList<string> ids)
    {
        // 設計 2026-08-24 §3.2: 背景スレッドが後で読むため、投入時に複写して切り離す。
        // 呼び出し側の善意(毎回新しい List を渡す)に依存すると、使い回しの List を渡して
        // 直後に書き換える 2 人目の呼び出し側が現れた瞬間、別の集合を消すことになる
        // (=一覧に出していないライブを消す)。複写は投入 1 回きりで件数も小さい。
        string[] snapshot = ids.ToArray();
        Enqueue(() =>
        {
            try
            {
                // E-2: 自セッション dir(_dir)ではなく引数の base dir を横断する。
                // BK-M-2 の DeleteSessionDir では、一覧に出した孤児が一件も消えなかった。
                BackupStore.DeleteByIds(baseDir, snapshot);
            }
            catch
            { /* 一括削除失敗は致命でない・無音 */
            }
        });
    }

    public void WriteLayout(string path, kxEdit.Core.Session.SessionLayout layout) =>
        Enqueue(() =>
        {
            try
            {
                kxEdit.Core.Session.SessionLayoutStore.Save(path, layout);
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
                kxEdit.Core.Session.SessionLayoutStore.Delete(path);
            }
            catch
            { /* 削除失敗は致命でない・無音 */
            }
        });

    /// <summary>ジョブを投入する(締め切り後・破棄後は無視)。投入できたら true。実装詳細。
    /// 呼び出しは UI スレッド前提。
    /// Write/Delete/DeleteAcrossSessions/WriteLayout/DeleteLayout の 5 箇所が戻り値を捨てるのは意図的
    /// (=投入失敗は無音、という既存挙動の保存)。戻り値を見るのは
    /// <see cref="WaitForPendingJobs"/> だけ。</summary>
    // _disposed は volatile 不要: 書き込み(Dispose)も読み取り(Enqueue)も UI スレッドのみ。
    private bool Enqueue(Action job)
    {
        // Dispose 開始後は無視(_disposed=true → CompleteAdding → Join → _queue.Dispose の順で進むため
        // この一読で破棄済み・締切済みの両方をカバー)。従来は _queue.IsAddingCompleted を try 外で読んで
        // いたが、_queue.Dispose 後は getter 自体が ObjectDisposedException を投げるため
        // 呼び出し元に伝播していた(xmldoc「破棄後は無視」の意図との乖離)。_disposed で先に遮断する。
        if (_disposed)
            return false;
        // 競合で AddingCompleted 済み／破棄済み(ObjectDisposedException は InvalidOperationException 派生
        // のため 1 つの catch で両方拾える)。UI スレッド前提のため race window はごく狭いが防御的に残す。
        try
        {
            _queue.Add(job);
            return true;
        }
        catch (InvalidOperationException)
        { /* AddingCompleted 済み or 破棄済み。UI スレッド前提の狭 race・無視 */
            return false;
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

    /// <inheritdoc/>
    public bool WaitForPendingJobs(TimeSpan timeout)
    {
        // キュー末尾にバリアジョブを積み、それが走り終わるのを待つ。直列ワーカーなので
        // バリアが走った時点で先行ジョブは全て実行済み=結果通知(OnWriteFailed / OnWriteSucceeded)も
        // 発火済み。
        //
        // 不変条件: 投入者(producer)は UI スレッドただ 1 つ。「末尾バリア」が「全保留ジョブの完了」を
        // 意味するのは、待っている間に誰も新しいジョブを積めないからである(現状 Reconcile を回す
        // BackupCoordinator._timer は System.Windows.Forms.Timer=Tick も UI スレッド)。将来 producer が
        // 増えると、true を返した直後に新しい write が保留になり、呼び出し側(hot exit の事後条件検査)が
        // 静かに嘘になる。producer を増やすならバリアの意味論から設計し直すこと。
        //
        // 同期プリミティブに TaskCompletionSource を使う理由: ManualResetEventSlim + using だと
        // timeout 後にバリアが破棄済みインスタンスへ Set を打ち、ワーカー側 catch に例外を
        // 吸わせる設計になる。TrySetResult は timeout 後に呼ばれても無害。
        // RunContinuationsAsynchronously は現状 no-op(Task.Wait の待ちはユーザー継続
        // (await / ContinueWith)ではなく InvokeMayRunArbitraryCode=false の内部完了アクションとして
        // 登録されるため、FinishContinuations がフラグに関係なくインライン実行する)。
        // 将来 await する消費者が現れたとき継続がワーカースレッドで走るのを防ぐ防御として残す。
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 締切済み/破棄済み=**今後の**投入は無い(既に保留のジョブがあれば Dispose の Join が
        // ドレインを担う)。待てないので待たない=この true は「保留ゼロ」の保証ではない
        // (極性の注意は IBackupWriter 側の xmldoc を参照)。
        if (!Enqueue(() => barrier.TrySetResult()))
            return true;
        // 中核契約: ここで Dispose はしない(終了がキャンセルされてもライターは生き続ける)。
        return barrier.Task.Wait(timeout);
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
