using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using yEdit.Core.Backup;
using yEdit.Core.Text;

namespace yEdit.App;

/// <summary>
/// 自動バックアップとクラッシュ復元の統括。UI スレッドのタイマー(＋アクティブ文書変更)で
/// 文書を走査(Reconcile)し、変化のある未保存文書のスナップショットを背景直列ライターへ渡す。
/// スナップショット取得(SCI_* 由来)は UI スレッドで行い、ディスク I/O は背景で行う(§4.1 鉄則)。
/// クリーン終了では「当セッションが管理した文書」のバックアップのみ削除する(「あとで」先送りした
/// 孤児は残し次回再提案する)。判定の中核は Core の純粋関数 BackupPlanner で単体テスト可能。
/// Phase 2 Stage 5: 時計・背景書込・復元ダイアログを IBackupWriter/IRestorePrompt/TimeProvider で
/// 注入化し、BackupStore への直接参照を持たない(App.Tests から Reconcile を internal 直呼び)。
/// hot exit 統合(設計 2026-07-23 §3.1-§3.3): タブレイアウト(session-state.json)の定期退避と
/// silent 統合復元用 API(CollectForSilentRestore/AdoptRestored ほか)も担う。レイアウト退避は
/// _sessionRestoreEnabled のみに依存し、本文バックアップ(_enabled)とは独立(設計 §5.2)。
/// </summary>
public sealed class BackupCoordinator : IDisposable
{
    private sealed class DocBackup
    {
        public string Id = "";
        public long LastSig;
        public bool HasBackup;
        public bool ForceWrite; // 前回の背景書込が失敗 → 次 tick で強制再書込(陳腐化・欠落を防ぐ)
    }

    /// <summary>BK-M-3 (v0.11): バックアップに載せる本文の上限 (chars=UTF-16 code units)。
    /// 32M chars = 64 MB UTF-16 相当。日常編集の CSV / ログを大きく超える。上限超過時は
    /// <see cref="BackupRecord.Content"/>=null (path-only) にフォールバックし、
    /// <see cref="_trace"/>.Warn("backup-content-skipped", ...) で診断可能にする。
    /// テストから <see cref="Reconcile"/> の分岐を機械的に叩けるよう、ctor 経由で override 可能な
    /// seam (<see cref="_maxBackupChars"/>) を追加している(既定=この定数)。</summary>
    internal const int MaxBackupChars = 32 * 1024 * 1024;

    private readonly DocumentManager _docs;
    private readonly string _dir;

    /// <summary>BK-M-3: 実行時の size cap。既定は <see cref="MaxBackupChars"/>。ctor の
    /// optional 引数 <c>maxBackupCharsOverride</c> でテスト時に小さな値へ差し替え、実際に
    /// 32M chars 相当のバッファを alloc せずに fallback 分岐を検証する。</summary>
    private readonly int _maxBackupChars;

    /// <summary>BK-M-2: 自セッション用 subdirectory (<c>%APPDATA%\yEdit\backups\session-{Guid.N}\</c>)。
    /// ctor で生成しプロセス寿命で不変。SerialBackupWriter へ渡す書込先はこの subdir に閉じ、
    /// 復元列挙 (LoadAll) と 30 日 sweep (SweepOldSessions) は <see cref="_dir"/> (base dir) に対して行う。
    /// </summary>
    private readonly string _sessionDir;
    private readonly TimeProvider _clock;
    private readonly Func<string, IBackupWriter> _writerFactory;
    private readonly IRestorePrompt _restorePrompt;
    private readonly IBackupTraceSink _trace; // Task 1b: silent catch を診断可能に(既定=DebugBackupTraceSink)
    private bool _enabled; // UpdateSettings で実行時に切替可能
    private readonly System.Windows.Forms.Timer _timer = new();
    private IBackupWriter? _writer; // 無効時は生成しない(有効化時に factory 経由で遅延生成)
    private readonly Dictionary<Document, DocBackup> _map = new();

    /// <summary>ユーザーが明示的に破棄(未保存確認で「いいえ」)した文書(設計 §3.2 補遺・
    /// PR #22 M-1 後継)。Reconcile の再登録・BuildLayout の記録から除外し、hot exit の
    /// 復元対象に破棄意図を silent 復活させない。</summary>
    private readonly HashSet<Document> _discarded = new();
    private readonly ConcurrentQueue<string> _failed = new(); // 背景書込が失敗した Id(UI スレッドで回収)
    private bool _shutDown;

    // ===== hot exit 統合(設計 2026-07-23 §3.1)=====
    private bool _sessionRestoreEnabled; // 「起動時に前回開いていたファイルを開く」設定(UpdateSettings で切替可能)
    private readonly string _layoutPath; // session-state.json のフルパス(テストは TempDir 配下へ差替)
    private long _lastLayoutSig; // 前回書込時のレイアウト署名(同一なら書かない=tick 抑止)
    private bool _layoutForceWrite; // 書込失敗・OFF→ON 切替 → 次 Reconcile で署名一致でも強制書込
    private int _layoutWriteFailed; // 背景書込失敗の通知置場(背景スレッドから Interlocked で設定)

    /// <summary>BK-M-2: 起動時 sweep の age 閾値。30 日以上更新のない session-* subdir を削除する。</summary>
    private static readonly TimeSpan SessionSweepMaxAge = TimeSpan.FromDays(30);

    /// <summary>テスト観測用: 現在の Timer.Interval(ms)。UpdateSettings/ctor の Clamp 結果を assert 化する seam。</summary>
    internal int TimerIntervalMs => _timer.Interval;

    /// <summary>テスト観測用: タイマー稼働中か。レイアウトのみモード(enabled=false かつ
    /// restoreSessionEnabled=true)でも起動する契約(設計 §5.2)を assert 化する seam。</summary>
    internal bool TimerEnabled => _timer.Enabled;

    public BackupCoordinator(
        DocumentManager docs,
        bool enabled,
        int intervalSeconds,
        TimeProvider clock,
        Func<string, IBackupWriter> writerFactory,
        IRestorePrompt restorePrompt,
        string? directory = null,
        IBackupTraceSink? traceSink = null,
        int? maxBackupCharsOverride = null,
        bool restoreSessionEnabled = false,
        string? sessionLayoutPath = null
    )
    {
        _docs = docs;
        _enabled = enabled;
        _clock = clock;
        _writerFactory = writerFactory;
        _restorePrompt = restorePrompt;
        _dir = directory ?? BackupStore.DefaultDirectory;
        // BK-M-3: 既定は MaxBackupChars。テストが小さな値を渡すと Reconcile の fallback 分岐を
        // 実際に 32M chars alloc せずに叩ける。負値は意図しない無効化を招くので 0 未満は defensive に
        // 既定へ戻す(既定 32M chars = 実運用上ほぼ超えない=本番挙動不変)。
        _maxBackupChars = maxBackupCharsOverride is int mo && mo >= 0 ? mo : MaxBackupChars;
        // BK-M-2: セッション別 subdir を ctor で生成 (プロセス寿命で不変)。
        // 別インスタンスと衝突しない一意名として Guid.N (32 桁 hex)。session- prefix で LoadAll /
        // SweepOldSessions が識別する契約(prefix を変えると sweep 対象から外れて孤児が溜まる)。
        _sessionDir = Path.Combine(_dir, "session-" + Guid.NewGuid().ToString("N"));
        // Task 1b: 既定は Trace.TraceWarning に流す DebugBackupTraceSink。MainForm は既定引数のまま呼ぶ
        // ため本番挙動は不変(silent catch → Trace 出力あり、例外は依然握り潰す)。
        _trace = traceSink ?? new DebugBackupTraceSink();
        // hot exit 統合: レイアウト退避先。テストは TempDir 配下を注入、本番は既定 %APPDATA%\yEdit。
        _sessionRestoreEnabled = restoreSessionEnabled;
        _layoutPath = sessionLayoutPath ?? yEdit.Core.Session.SessionLayoutStore.DefaultPath;

        // 無効時でもハンドラは購読しておく(後から UpdateSettings で有効化できるように)。
        // Tick/ActiveDocumentChanged は Reconcile 冒頭のガードで素通りするため無効中は無害。
        _timer.Interval = Math.Clamp(intervalSeconds, 5, 3600) * 1000; // 上限クランプで int オーバーフロー防止
        _timer.Tick += (_, _) => Reconcile();
        _docs.ActiveDocumentChanged += (_, _) => Reconcile();
        // レイアウトのみモード(設計 §5.2 OFF×ON)でも writer と timer は動かす。
        if (!_enabled && !_sessionRestoreEnabled)
            return;

        _writer = CreateWriter();
        _timer.Start();
    }

    /// <summary>writer を factory で生成し、失敗通知フックを配線する(遅延生成の意味論を保存)。
    /// BK-M-2: factory シグニチャは <c>Func&lt;string, IBackupWriter&gt;</c>=書込先の session dir を
    /// 明示的に渡す(base dir と混同するミスを compile-time で防ぐ seam)。</summary>
    private IBackupWriter CreateWriter()
    {
        var w = _writerFactory(_sessionDir);
        w.OnWriteFailed = OnBackgroundWriteFailed;
        // レイアウト書込失敗(背景スレッド)→ 次 Reconcile で強制再書込(設計 E13)。
        w.OnLayoutWriteFailed = () => Interlocked.Exchange(ref _layoutWriteFailed, 1);
        return w;
    }

    /// <summary>背景書込の失敗通知(Adapter から UI スレッド外で来る可能性あり=ConcurrentQueue で受ける)。</summary>
    private void OnBackgroundWriteFailed(string id) => _failed.Enqueue(id);

    /// <summary>
    /// 設定ダイアログ OK 時の即時反映。間隔は常に更新し、有効/無効の切替では
    /// タイマーとライターを追従させる。無効化では既存バックアップファイルを削除しない
    /// (次回起動時の孤児提案に任せる・安全側)。
    /// hot exit 統合: restoreSessionEnabled(レイアウト定期退避)も同経路で追従する。
    /// いずれかが有効なら timer/writer を動かし即 Reconcile(保護窓を作らない)。
    /// restoreSession の OFF→ON では署名が stale の可能性があるため強制書込を予約する。
    /// </summary>
    // restoreSessionEnabled は既定値を持たない(I-2): 既定 false を許すと将来の 2 引数呼び出しが
    // 復元設定を silent OFF にする footgun になるため、呼び出し側に常に明示させる。
    public void UpdateSettings(bool enabled, int intervalSeconds, bool restoreSessionEnabled)
    {
        if (_shutDown)
            return;
        _timer.Interval = Math.Clamp(intervalSeconds, 5, 3600) * 1000;

        bool wasRestore = _sessionRestoreEnabled;
        _enabled = enabled;
        _sessionRestoreEnabled = restoreSessionEnabled;
        if (restoreSessionEnabled && !wasRestore)
            _layoutForceWrite = true; // OFF 中に消えた/古びた session-state.json を即上書きする

        if (_enabled || _sessionRestoreEnabled)
        {
            _writer ??= CreateWriter();
            _timer.Start();
            Reconcile(); // 有効化した瞬間の未保存文書/レイアウトを即保護
        }
        else
        {
            _timer.Stop();
        }
    }

    /// <summary>
    /// 起動時に孤児バックアップがあれば復元提案する。restore は復元先の新タブを作って Document を返す
    /// デリゲート(本文を載せ dirty のまま)。復元した文書には元 Id を引き継がせ、既存のバックアップ
    /// ファイルを継続使用する(孤児・無保護窓を作らない)。チェックしなかった項目は安全側で残し、
    /// 次回再提案する(明示的に消すのは「すべて破棄」のみ)。
    /// confirm=false ではダイアログを出さず全件復元し、その件数を返す(ダイアログ経路は 0 を返す)。
    /// </summary>
    public int OfferRestoreOnStartup(
        IWin32Window owner,
        Func<BackupRecord, Document> restore,
        bool confirm
    )
    {
        if (!_enabled)
            return 0;
        IReadOnlyList<BackupRecord> records = LoadAllForRestore();
        if (records.Count == 0)
            return 0;

        var ordered = records.OrderByDescending(r => r.TimestampUtc).ToList();

        // 確認 OFF: ダイアログを出さず全件復元(設計 2026-07-04)。呼び出し側が件数を能動通知する。
        if (!confirm)
        {
            int restored = 0;
            foreach (var rec in ordered)
            {
                try
                {
                    var doc = restore(rec);
                    // Task 4(設計 §3.4): _map 登録+ファイル本体の adopt-move。旧 session dir の
                    // 消費済みファイルを自セッション dir へ引き取り、clean 化 Delete が実ファイルに
                    // 届くようにする(BK-M-2 再提案バグ根治)。
                    AdoptRestored(doc, rec);
                    restored++;
                }
                catch (Exception ex)
                {
                    // 1 件の不正レコードで全復元を巻き添えにしない。失敗分はバックアップを残し再挑戦可能に。
                    // BK-L-5: rec.Id は攻撃者 JSON 由来の可能性(LoadAll 経路は validator で reject 済み
                    // だが防御は薄く重ねる)+ prompt outcome 経路は validator を通らないため、
                    // 全 trace で SanitizeForDisplay.OneLine(200) 統一で無害化する。
                    _trace.Warn("restore-item-later", SanitizeForDisplay.OneLine(rec.Id, 200), ex);
                }
            }
            return restored;
        }

        var outcome = _restorePrompt.Prompt(owner, ordered);
        switch (outcome.Action)
        {
            case RestoreAction.Restore:
                foreach (var rec in outcome.Checked)
                {
                    try
                    {
                        var doc = restore(rec);
                        // Reconcile が先に新 Id で登録していても、ここで元 Id へ上書きして引き継ぐ。
                        // Task 4(設計 §3.4): adopt-move で消費済みファイルも自セッション dir へ
                        // 引き取る(チェックしなかった record は据え置き=次回再提案)。
                        AdoptRestored(doc, rec);
                    }
                    catch (Exception ex)
                    {
                        // 1 件の不正レコードで全復元を巻き添えにしない。失敗分はバックアップを残し再挑戦可能に。
                        // BK-L-5: outcome.Checked 経路は BackupIdValidator を通らないため、
                        // 攻撃者が Prompt から悪意 Id を注入し得る。SanitizeForDisplay.OneLine(200) で
                        // 制御文字/BiDi/過剰長を無害化する(BackupCoordinator 全 trace で統一)。
                        _trace.Warn("restore-item", SanitizeForDisplay.OneLine(rec.Id, 200), ex);
                    }
                }
                // チェックしなかった項目は削除しない(SR 誤操作での消失を避け、次回再提案)。
                break;

            case RestoreAction.DiscardAll:
                _writer?.DeleteAll();
                break;

            case RestoreAction.Later:
                break; // 何もしない(次回再提案)
        }
        return 0;
    }

    /// <summary>起動時復元の入力収集(sweep+LoadAll+trace)。OfferRestoreOnStartup と
    /// CollectForSilentRestore の共通部として抽出(挙動不変)。失敗は trace のみで
    /// 空リストを返す(復元自体は続行可能・呼び出し側は 0 件と同扱い)。</summary>
    private IReadOnlyList<BackupRecord> LoadAllForRestore()
    {
        try
        {
            // BK-M-2: 30 日以上更新のない孤児 session-* subdir を掃除する(前回異常終了/古いインスタンス由来)。
            // 時計は TimeProvider seam 経由でテスト可能。失敗は無害(次回起動で再挑戦)。
            BackupStore.SweepOldSessions(_dir, _clock.GetUtcNow().UtcDateTime, SessionSweepMaxAge);
        }
        catch (Exception ex)
        {
            _trace.Warn("sweep-old-sessions", SanitizeForDisplay.OneLine(_dir, 200), ex);
        }
        try
        {
            // BK-M-2: 自セッション dir と base dir 直下(flat 後方互換)の両方で *.tmp 残骸を掃除。
            // session dir は初回書込前だと存在しないが、SweepTempFiles は Directory.Exists=false で
            // 無害 return する。base dir は v0.3.0-sec 由来の残置対策。
            BackupStore.SweepTempFiles(_sessionDir);
            BackupStore.SweepTempFiles(_dir);
        }
        catch (Exception ex)
        {
            // BK-L-5: 将来的に _dir がユーザ設定で可変化された場合の CRLF injection / BiDi 混入
            // 防御として SanitizeForDisplay.OneLine(200) を通す(現状 %APPDATA%\yEdit\backups は
            // 非攻撃者制御だが、防御の invariant を BackupCoordinator 全 trace で統一する)。
            _trace.Warn("sweep-temp", SanitizeForDisplay.OneLine(_dir, 200), ex);
        } // 残骸掃除失敗は無害・診断のため trace

        try
        {
            // BK-L-6: per-file の破損 catch / invalid-id / null-record を trace で可視化する。
            // file パスは JSON の内容(攻撃者制御可能)ではなくディレクトリ列挙で得た値だが、
            // %APPDATA%\yEdit\backups 配下に置かれるファイル名は「.json」拡張子と Directory 名以外は
            // 攻撃者制御下にあり得る(RLO 混入等)ため、SanitizeForDisplay.OneLine で 1 行化してから
            // trace に載せる。kind (例外型名 / "invalid-id" / "null-record") はコード側の enum 相当なので
            // detail 末尾へコロン結合する(Option A: 既存 3 引数 sink API を無変更で維持)。
            // maxLength=200 は BK-L-5 の統一値(設計 §PR-F (4))=BackupCoordinator 全 trace で揃える。
            return BackupStore.LoadAll(
                _dir,
                (file, kind) =>
                    _trace.Warn(
                        "backup-load-failed",
                        SanitizeForDisplay.OneLine(file, 200) + ":" + kind,
                        ex: null
                    )
            );
        }
        catch (Exception ex)
        {
            _trace.Warn("load-all", SanitizeForDisplay.OneLine(_dir, 200), ex);
            return Array.Empty<BackupRecord>();
        }
    }

    /// <summary>silent 統合復元の入力収集(設計 §3.3)。レイアウト Load → sweep+LoadAll。
    /// バックアップ無効でも動く(レイアウトのみ復元モード=設計 §5.2)。</summary>
    public (
        yEdit.Core.Session.SessionLayout? Layout,
        IReadOnlyList<BackupRecord> Backups
    ) CollectForSilentRestore()
    {
        var layout = yEdit.Core.Session.SessionLayoutStore.Load(_layoutPath);
        var backups = LoadAllForRestore();
        return (layout, backups);
    }

    /// <summary>復元成功後にレイアウトを消費する(次回は今セッションの新レイアウトが正)。
    /// 消費後は次 Reconcile を強制書込に倒し、session-state.json 不在の窓を最小化する(M-4)。</summary>
    public void DeleteConsumedLayout()
    {
        yEdit.Core.Session.SessionLayoutStore.Delete(_layoutPath);
        _layoutForceWrite = true;
    }

    /// <summary>復元した文書を元 Id で管理下へ引き取る(設計 §3.4 adopt-move)。
    /// _map 登録により以後の clean 化 Delete・クリーン終了削除が正しく効き、ファイル本体は
    /// 自セッション dir へ移動して「同一ファイル継続使用」を回復する。移動失敗は trace のみ。</summary>
    public void AdoptRestored(Document doc, BackupRecord rec)
    {
        _map[doc] = new DocBackup
        {
            Id = rec.Id,
            LastSig = ContentSignature.Of(doc.Editor.SnapshotText),
            HasBackup = true,
        };
        try
        {
            if (!BackupStore.TryMoveToSessionDir(_dir, rec.Id, _sessionDir))
                _trace.Warn("adopt-move-missed", SanitizeForDisplay.OneLine(rec.Id, 200), ex: null);
        }
        catch (Exception ex)
        {
            _trace.Warn("adopt-move", SanitizeForDisplay.OneLine(rec.Id, 200), ex);
        }
    }

    /// <summary>ユーザーが明示的に破棄(未保存確認で「いいえ」)した文書を最終 flush の対象外にする。
    /// バックアップを即削除し、以後の ReconcileContent の再登録と BuildLayout のレイアウト記録から
    /// 除外する(明示破棄の意図を hot exit の復元対象に silent 復活させない=PR #22 M-1 の後継)。
    /// 破棄意図が確定した後(確認ループ完走後)にのみ呼ぶこと(途中キャンセルで close が中止された
    /// 場合にマークが残留すると、以後その文書が保護対象から外れ hot exit で silent 消失するため)。</summary>
    public void MarkDiscarded(Document doc)
    {
        if (_map.TryGetValue(doc, out var info))
        {
            if (info.HasBackup)
                _writer?.Delete(info.Id);
            _map.Remove(doc);
        }
        _discarded.Add(doc);
    }

    /// <summary>UI スレッドで文書を走査し、必要なバックアップ書込/削除ジョブ(+有効時は
    /// レイアウト書込ジョブ)を投入する。App.Tests から直接叩けるよう internal(Timer は本番のみ)。</summary>
    internal void Reconcile()
    {
        if (_shutDown || (!_enabled && !_sessionRestoreEnabled))
            return;
        if (_enabled)
            ReconcileContent();
        else
            ReconcileMapMaintenance(); // layout-only モードでも _map をディスク実在の鏡に保つ(I-1)
        if (_sessionRestoreEnabled)
            ReconcileLayout(force: false);
    }

    /// <summary>本文バックアップの走査(旧 Reconcile 本体をそのまま抽出・挙動不変)。</summary>
    private void ReconcileContent()
    {
        // 背景書込が失敗した文書を強制再書込対象にする(楽観更新で欠落・陳腐化しないように)。
        while (_failed.TryDequeue(out var failedId))
            foreach (var v in _map.Values)
                if (v.Id == failedId)
                    v.ForceWrite = true;

        // 閉じた文書(map にあるが現存しない)→ バックアップ削除。
        var current = new HashSet<Document>(_docs.Documents);
        foreach (var doc in _map.Keys.ToList())
        {
            if (current.Contains(doc))
                continue;
            var gone = _map[doc];
            if (gone.HasBackup)
                _writer?.Delete(gone.Id);
            _map.Remove(doc);
        }

        foreach (var doc in _docs.Documents)
        {
            // 意図的変更(挙動不変リファクタではない): MarkDiscarded 済み文書は RegisterNew による
            // 再登録・再書込をしない。ここを外すと OnFormClosing の FinalFlush が破棄済み dirty を
            // 再退避し、次回起動で破棄意図が silent 復活する(PR #22 M-1 後継)。
            if (_discarded.Contains(doc))
                continue;
            if (!_map.TryGetValue(doc, out var info))
            {
                RegisterNew(doc);
                continue;
            }

            bool modified = doc.Editor.Modified;
            string content = modified ? doc.Editor.SnapshotText : ""; // クリーン時はスナップショット不要
            long sig = modified ? ContentSignature.Of(content) : info.LastSig;

            switch (
                BackupPlanner.Decide(modified, sig, info.LastSig, info.HasBackup, info.ForceWrite)
            )
            {
                case BackupAction.Write:
                    EnqueueWrite(info, doc, content);
                    info.LastSig = sig;
                    info.HasBackup = true;
                    info.ForceWrite = false;
                    break;
                case BackupAction.Delete:
                    _writer?.Delete(info.Id);
                    info.HasBackup = false;
                    info.LastSig = sig;
                    info.ForceWrite = false;
                    break;
                case BackupAction.None:
                    break;
            }
        }
    }

    /// <summary>_enabled=false(layout-only モード)でも _map をディスク実在の鏡に保つ:
    /// 閉じタブのバックアップ削除+clean 化した文書のバックアップ削除のみ行う(新規書込はしない)。
    /// これを怠ると BuildLayout が stale BackupId を書き、次回起動の silent 復元が
    /// 保存済みファイルへ古い内容を dirty 復元するデータ損失経路になる(Task 3 品質レビュー I-1)。</summary>
    private void ReconcileMapMaintenance()
    {
        if (_map.Count == 0)
            return;
        var current = new HashSet<Document>(_docs.Documents);
        foreach (var doc in _map.Keys.ToList())
        {
            // 意図的変更: MarkDiscarded 済み文書は対象外(通常は MarkDiscarded が _map から除去済みで
            // 到達しない=将来 _map への再登録経路が増えた場合の防御的整合。ReconcileContent と対)。
            if (_discarded.Contains(doc))
                continue;
            var info = _map[doc];
            if (!current.Contains(doc))
            {
                if (info.HasBackup)
                    _writer?.Delete(info.Id);
                _map.Remove(doc);
                continue;
            }
            if (info.HasBackup && !doc.Editor.Modified)
            {
                _writer?.Delete(info.Id);
                info.HasBackup = false;
            }
        }
    }

    /// <summary>レイアウトの走査(設計 §3.1)。署名が前回書込時と同じなら書かない(tick 抑止)。
    /// force=true(FinalFlushForRestore)は署名判定を飛ばして確定書込する。背景書込の失敗通知は
    /// Interlocked で回収し、次回を強制書込へ倒す(本文の ForceWrite と同方針=設計 E13)。</summary>
    private void ReconcileLayout(bool force)
    {
        if (Interlocked.Exchange(ref _layoutWriteFailed, 0) == 1)
            _layoutForceWrite = true;
        var layout = BuildLayout();
        long sig = LayoutSig(layout);
        if (!force && !_layoutForceWrite && sig == _lastLayoutSig)
            return;
        _layoutForceWrite = false;
        _lastLayoutSig = sig;
        _writer?.WriteLayout(_layoutPath, layout);
    }

    /// <summary>現在のタブ列から SessionLayout を構築する(UI スレッド=SCI_* 由来の値もここで取る)。
    /// dirty 文書(_map で HasBackup)は BackupId で本文バックアップを参照し、レイアウト側に
    /// 本文・エンコーディングを重複して持たない(設計 §2.1)。</summary>
    private yEdit.Core.Session.SessionLayout BuildLayout()
    {
        var tabs = new List<yEdit.Core.Session.SessionLayoutRecord>();
        var active = _docs.Active;
        foreach (var doc in _docs.Documents)
        {
            // 意図的変更: MarkDiscarded 済みタブはレイアウトに書かない(タブごと復元対象外)。
            // ここを外すと ON×BackupOFF の fall-through で No'd 無題タブが空枠として復活する。
            if (_discarded.Contains(doc))
                continue;
            string? backupId =
                _map.TryGetValue(doc, out var info) && info.HasBackup ? info.Id : null;
            tabs.Add(
                new yEdit.Core.Session.SessionLayoutRecord(
                    Path: doc.State.Path,
                    UntitledNumber: doc.State.Path is null ? doc.State.UntitledNumber : 0,
                    BackupId: backupId,
                    IsActive: ReferenceEquals(doc, active),
                    CaretLine: doc.Editor.CurrentLine,
                    CaretColumn: doc.Editor.GetColumn(doc.Editor.CurrentPosition),
                    LineEnding: (int)doc.State.LineEnding
                )
            );
        }
        return new yEdit.Core.Session.SessionLayout(tabs, _clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>レイアウト署名(64bit)。全フィールドを '\x1'(フィールド)/'\x2'(レコード)区切りで
    /// 連結し ContentSignature に流す。SavedAtUtc は含めない(含めると毎 tick 異なり抑止が死ぬ)。</summary>
    private static long LayoutSig(yEdit.Core.Session.SessionLayout layout)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in layout.Tabs)
            sb.Append(t.Path)
                .Append('\x1')
                .Append(t.UntitledNumber)
                .Append('\x1')
                .Append(t.BackupId)
                .Append('\x1')
                .Append(t.IsActive ? 1 : 0)
                .Append('\x1')
                .Append(t.CaretLine)
                .Append('\x1')
                .Append(t.CaretColumn)
                .Append('\x1')
                .Append(t.LineEnding)
                .Append('\x2');
        return ContentSignature.Of(sb.ToString());
    }

    /// <summary>未登録文書を登録する。登録時点で既に dirty なら即退避し保護窓を作らない(起動時無題タブ対策)。</summary>
    private void RegisterNew(Document doc)
    {
        string content = doc.Editor.SnapshotText;
        var info = new DocBackup
        {
            Id = Guid.NewGuid().ToString("N"),
            LastSig = ContentSignature.Of(content),
            HasBackup = false,
        };
        _map[doc] = info;
        if (doc.Editor.Modified)
        {
            EnqueueWrite(info, doc, content);
            info.HasBackup = true;
        }
    }

    /// <summary>書込ジョブを投入する。失敗時は Adapter が OnWriteFailed 経由で Id を _failed へ積み、
    /// 次 Reconcile で強制再書込する。
    /// BK-M-3: content.Length が上限 (<see cref="_maxBackupChars"/>) を超える場合は Content=null の
    /// path-only record にフォールバックし、_trace に "backup-content-skipped" を出す。ContentSignature の
    /// 判定は Reconcile 側で実 content から計算済みなので、ここで null に落としても sig(false-negative)
    /// は起きない(次 tick で内容が上限以下に戻れば通常経路で Write が再走る)。</summary>
    private void EnqueueWrite(DocBackup info, Document doc, string content)
    {
        string? persistContent = content;
        if (content.Length > _maxBackupChars)
        {
            // pathKey: doc.State.Path が null (untitled) の場合はプレースホルダ。
            // SanitizeForDisplay.OneLine(200) で BiDi/改行/過剰長を無害化 (BackupCoordinator 全 trace で統一)。
            var pathKey = SanitizeForDisplay.OneLine(
                doc.State.Path ?? $"<untitled-{doc.State.UntitledNumber}>",
                200
            );
            // BK-M-3 I-1: sizeChars を detail に折り込む(閾値ぎりぎり vs 遥かに超えの区別が付き
            // 閾値チューニング診断が可能になる)。追加する " (Nchars)" は自コード生成 = sanitize 不要。
            _trace.Warn("backup-content-skipped", pathKey + $" ({content.Length}chars)", ex: null);
            persistContent = null;
        }
        var rec = BuildRecord(info.Id, doc, persistContent);
        _writer?.Write(rec);
    }

    private BackupRecord BuildRecord(string id, Document doc, string? content) =>
        new(
            Id: id,
            OriginalPath: doc.State.Path,
            UntitledNumber: doc.State.UntitledNumber,
            CodePage: doc.State.Encoding.CodePage,
            HasBom: doc.State.HasBom,
            LineEndingId: (int)doc.State.LineEnding,
            Content: content,
            TimestampUtc: _clock.GetUtcNow().UtcDateTime
        );

    /// <summary>hot exit 終了時の最終 flush(設計 §3.2)。dirty 本文の未退避分を退避し、
    /// レイアウトを署名判定なしで確定書込する。docs が生きている OnFormClosing 中に呼ぶこと。</summary>
    public void FinalFlushForRestore()
    {
        if (_shutDown)
            return;
        if (_enabled)
            ReconcileContent();
        else
            ReconcileMapMaintenance(); // 最終書込でも stale BackupId を残さない(I-1)
        if (_sessionRestoreEnabled)
            ReconcileLayout(force: true);
    }

    /// <summary>
    /// クリーン終了: タイマー停止 → 当セッション管理分のバックアップ削除を投入 → 背景書込をドレイン。
    /// 「あとで」先送りした孤児は _map に無いので残り、次回起動で再提案される。
    /// 未保存確認をすべて通過した後に呼ぶこと。
    /// hot exit(設計 §3.2): keepForRestore=true は削除を全てスキップし、バックアップと
    /// session-state.json を次回起動の統合復元用に残す(タイマー停止+ドレインのみ)。
    /// </summary>
    public void Shutdown(bool keepForRestore = false)
    {
        // ガードは _shutDown のみ: セッション途中で無効化されても、有効だった間に書いた
        // バックアップ(_map の HasBackup)をクリーン終了で削除する。一度も有効になって
        // いなければ _map は空・_writer は null で各行は無害に素通りする。
        if (_shutDown)
            return;
        _shutDown = true;
        _timer.Stop();
        if (!keepForRestore)
        {
            foreach (var info in _map.Values)
                if (info.HasBackup)
                    _writer?.Delete(info.Id);
            // stale レイアウトを残さない(後日 ON に切替えた際の亡霊復元を防ぐ)。
            // writer 未生成(両機能 OFF)でも直接消す=過去 ON セッションの残骸掃除。
            if (_writer is not null)
                _writer.DeleteLayout(_layoutPath);
            else
                yEdit.Core.Session.SessionLayoutStore.Delete(_layoutPath);
        }
        _writer?.Dispose(); // 保留ジョブ(削除/レイアウト含む)をドレイン
        _timer.Dispose();
    }

    public void Dispose()
    {
        // Shutdown 済みなら timer/writer は解放済み。未経由(異常系)なら timer/writer を片付ける
        // (孤児バックアップは残し、次回起動で復元提案できるようにする)。冪等。
        if (_shutDown)
            return;
        _shutDown = true;
        _timer.Stop();
        _timer.Dispose();
        _writer?.Dispose();
    }
}
