using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using kxEdit.Core.Backup;
using kxEdit.Core.Text;

namespace kxEdit.App;

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

    /// <summary>A-8: hot exit の確認スキップ前に最終 flush の完了を待つ上限。
    /// Windows のシャットダウン猶予に収まる長さとして 5 秒(設計 2026-08-24 §5.3)。
    /// この待ちは <see cref="Shutdown"/> の Join(15 秒)を**置き換えず直列に足す**ため、
    /// ワーカーが固まった場合の UI スレッド最悪ブロックは 15 秒 → 最大 20 秒に伸びる
    /// (設計 §10.1 で受容・申し送り S-A8-6)。正常時(ワーカーが詰まっておらず、S-A8-2 の
    /// 極端に遅いディスクでもない場合)はバリアが即返るため終了の体感は不変。</summary>
    internal static readonly TimeSpan FinalFlushWait = TimeSpan.FromSeconds(5);

    private readonly DocumentManager _docs;
    private readonly string _dir;

    /// <summary>BK-M-3: 実行時の size cap。既定は <see cref="MaxBackupChars"/>。ctor の
    /// optional 引数 <c>maxBackupCharsOverride</c> でテスト時に小さな値へ差し替え、実際に
    /// 32M chars 相当のバッファを alloc せずに fallback 分岐を検証する。</summary>
    private readonly int _maxBackupChars;

    /// <summary>BK-M-2: 自セッション用 subdirectory (<c>%APPDATA%\kxEdit\backups\session-{Guid.N}\</c>)。
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

    /// <summary>M-20(B5): 背景書込が成功したか(0/1)。失敗側と違い <b>id は使わない</b>
    /// (復旧の判定に要るのは「1 件でも書けたか」だけ)ので、<see cref="_layoutWriteFailed"/> と
    /// 同じ <see cref="Interlocked"/> フラグで受ける。
    /// <para>キューにしないのは設計判断である。設計書 §11 は「<c>_succeeded</c> を無制限に積む」
    /// (drain は <see cref="ReconcileContent"/> 冒頭 1 箇所だけなので、<c>_enabled == false</c> の
    /// レイアウトのみモードへ切り替えたまま長時間動かすと <see cref="ReconcileMapMaintenance"/> 側へ
    /// 分岐して drain が走らず伸び続ける)を実装時の判断事項として残していた。0/1 フラグなら
    /// <b>その問題が構造的に消える</b> —— 読まれなくても大きくならない。</para></summary>
    private int _writeSucceeded;

    /// <summary>M-20: バックアップ書込が健全か。<b>遷移したときだけ</b>
    /// <see cref="OnBackupHealthChanged"/> を撃つための状態。初期 true = 最初の失敗も報告される。</summary>
    private bool _backupHealthy = true;

    private bool _shutDown;

    /// <summary>起動時復元(MainForm.OnShown)が完了したか。完了までは保存点/クローズの
    /// 即時反映を止める(設計 2026-08-22 §3.3)。MainForm ctor の NewFile は SetSavePoint 経由で
    /// <see cref="OnBackupBecameUnneeded"/> へ到達するため、ゲートが無いと空無題 1 タブのレイアウトを
    /// 復元前に session-state.json へ書き込み、前回セッションを失う。既存の
    /// ActiveDocumentChanged 経路が同じ事故を起こしていないのは、ctor 時点で TabControl の
    /// ハンドルが未生成で WinForms の Selected が発火しないため(= 偶然に守られている)。</summary>
    private bool _startupRestoreDone;

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
        // hot exit 統合: レイアウト退避先。テストは TempDir 配下を注入、本番は既定 %APPDATA%\kxEdit。
        _sessionRestoreEnabled = restoreSessionEnabled;
        _layoutPath = sessionLayoutPath ?? kxEdit.Core.Session.SessionLayoutStore.DefaultPath;

        // 無効時でもハンドラは購読しておく(後から UpdateSettings で有効化できるように)。
        // Tick/ActiveDocumentChanged は Reconcile 冒頭のガードで素通りするため無効中は無害。
        _timer.Interval = Math.Clamp(intervalSeconds, 5, 3600) * 1000; // 上限クランプで int オーバーフロー防止
        _timer.Tick += (_, _) => Reconcile();
        _docs.ActiveDocumentChanged += (_, _) => Reconcile();
        // A-1 / M-31(設計 2026-08-22 §3.1): 「バックアップが不要になった」瞬間を即時反映する。
        // Timer と ActiveDocumentChanged だけでは、保存直後 / 破棄直後〜次 tick(既定 300 秒)の
        // クラッシュ窓で、古いバックアップが dirty 復元され Ctrl+S で新内容を上書きする。
        _docs.DocumentDirtyChanged += (_, doc) =>
            OnBackupBecameUnneeded(becameUnneeded: !doc.Editor.Modified);
        // クローズは内容の dirty / clean を問わず「この文書のバックアップは不要」。
        // M-31 が直すのは dirty タブを Ctrl+W の「いいえ」で破棄したケースなので、
        // ここを clean と呼ぶと読み手に嘘になる(コード品質レビュー M-2)。
        _docs.DocumentClosed += (_, _) => OnBackupBecameUnneeded(becameUnneeded: true);
        // レイアウトのみモード(設計 §5.2 OFF×ON)でも writer と timer は動かす。
        if (!_enabled && !_sessionRestoreEnabled)
            return;

        _writer = CreateWriter();
        _timer.Start();
    }

    /// <summary>M-20(B5): バックアップ書込の健全性が<b>遷移した</b>ときだけ呼ばれる
    /// (true=復旧 / false=失敗)。失敗が続く間は鳴らない。
    /// <para><b>UI スレッドから呼ばれる</b>ので、受け手はスレッド越えを吸収しなくてよい。
    /// 根拠は経路の数え上げではなく <see cref="BackupCoordinator"/> が <c>_map</c> を非スレッド
    /// セーフな Dictionary で持つ UI スレッド専有クラスであること —— 本フックを撃つのは
    /// <see cref="ReconcileContent"/> 冒頭の drain 1 箇所で、そこへ来るのは <c>Timer.Tick</c> /
    /// <c>ActiveDocumentChanged</c> / <see cref="UpdateSettings"/>(設定ダイアログ OK 直後)/
    /// <see cref="FinalFlushForRestore"/>(終了時)である。<b>tick 以外でも鳴る</b> ——
    /// とくに終了時の最終 flush で失敗すればそこでも 1 回鳴る。</para>
    /// <para>発声手段そのものは注入しない —— 上の専有クラスが <c>IAnnouncer</c> を知る必要は
    /// 無いので、何と言うかは配線側(<c>MainForm</c>)の担当にする。
    /// <see cref="IBackupWriter.OnWriteFailed"/> と同じ Action プロパティの idiom に揃えてある。</para>
    /// <para><b>本文バックアップ(<c>Write</c>)の成否だけを載せる。</b> レイアウト
    /// (<c>session-state.json</c>)の書込失敗は <see cref="_layoutWriteFailed"/> 側で扱い、ここには
    /// 来ない —— レイアウトが書けなくても次回起動の復元は <c>BackupStore.LoadAll</c> が本体を直接
    /// 読むので、失うのはタブ順とアクティブタブだけ(設計 §5.5 (a) で受容)。</para></summary>
    public Action<bool>? OnBackupHealthChanged { get; set; }

    /// <summary>writer を factory で生成し、失敗通知フックを配線する(遅延生成の意味論を保存)。
    /// BK-M-2: factory シグニチャは <c>Func&lt;string, IBackupWriter&gt;</c>=書込先の session dir を
    /// 明示的に渡す(base dir と混同するミスを compile-time で防ぐ seam)。</summary>
    private IBackupWriter CreateWriter()
    {
        var w = _writerFactory(_sessionDir);
        w.OnWriteFailed = OnBackgroundWriteFailed;
        // M-20: 成功は id を使わないのでフラグを立てるだけ(_layoutWriteFailed と同型)。
        w.OnWriteSucceeded = _ => Interlocked.Exchange(ref _writeSucceeded, 1);
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
    /// (E-2: 「すべて破棄」は提示した record を base dir 横断で実削除する。一覧に出していない
    /// バックアップ=表示後に他インスタンスが書いた分・自セッションが保護中の分は消さない。)
    /// confirm=false ではダイアログを出さず全件復元し、その件数を返す。
    /// confirm=true でも Restore 選択時は実復元件数を返す(設計 2026-07-24-restore-no-initial-untitled §1・
    /// 呼び出し側は件数&gt;0 で起動時の空無題タブを閉じる判断に使う)。DiscardAll/Later は 0。
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
                // 設計 2026-07-24-restore-no-initial-untitled §1: 実復元件数を返す。
                // 呼び出し側(MainForm.OnShown OFF 経路)がこの件数で _startupEmptyDoc の
                // TryClose を判断する(ON 経路 FileController.RestoreSession の
                // openedCount>0 && initialEmpty is not null と対称)。
                int restored = 0;
                foreach (var rec in outcome.Checked)
                {
                    try
                    {
                        var doc = restore(rec);
                        // Reconcile が先に新 Id で登録していても、ここで元 Id へ上書きして引き継ぐ。
                        // Task 4(設計 §3.4): adopt-move で消費済みファイルも自セッション dir へ
                        // 引き取る(チェックしなかった record は据え置き=次回再提案)。
                        AdoptRestored(doc, rec);
                        restored++;
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
                return restored;

            case RestoreAction.DiscardAll:
                // E-2: 自セッション dir ではなく base dir を横断し、提示した record を実削除する。
                // `?.` は引数式も短絡するため、writer 未生成時に集合を組み立てない。
                _writer?.DeleteAcrossSessions(_dir, DiscardTargets(ordered));
                return 0;

            case RestoreAction.Later:
            default:
                return 0; // 何もしない(次回再提案)
        }
    }

    /// <summary>E-2: 「すべて破棄」で実削除する Id を決める。提示した record から、
    /// **自セッションが現在保護中**の Id を除く。
    ///
    /// 除外の理由: 実ファイルだけ消えても <see cref="_map"/> は <c>HasBackup=true</c> のまま残るため、
    /// 次に内容が変わるまで再書込が走らず無保護窓ができる(A-1 / M-31 で潰したのと同型)。
    /// ダイアログ表示中に自分が書いた分は LoadAll 時点で存在せず元から対象外なので、
    /// ここで守るのは「LoadAll の直前に Reconcile が走って書かれた分」。
    ///
    /// 戻り値は背景スレッドへ渡すため、呼び出し時点で確定した独立リストにする。
    /// (素朴な foreach + if は S3267、**戻り値**を <c>IReadOnlyList&lt;string&gt;</c> にすると
    /// CA1859「戻り値の型を 'IReadOnlyList&lt;string&gt;' から 'List&lt;string&gt;' に変更します」で
    /// ビルドが止まる。LINQ と具象 List はアナライザ要求であって、意味は上記のとおり集合差そのもの。
    /// なお CA1859 は戻り値に対する指摘で、**引数**を <c>IReadOnlyList&lt;BackupRecord&gt;</c> へ
    /// 広げてもアナライザは黙る=引数が具象 List なのは呼び出し側の実型に合わせただけ。)</summary>
    private List<string> DiscardTargets(List<BackupRecord> offered)
    {
        var live = new HashSet<string>(
            _map.Values.Where(info => info.HasBackup).Select(info => info.Id),
            StringComparer.OrdinalIgnoreCase
        );
        return offered.Where(rec => !live.Contains(rec.Id)).Select(rec => rec.Id).ToList();
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
            // 防御として SanitizeForDisplay.OneLine(200) を通す(現状 %APPDATA%\kxEdit\backups は
            // 非攻撃者制御だが、防御の invariant を BackupCoordinator 全 trace で統一する)。
            _trace.Warn("sweep-temp", SanitizeForDisplay.OneLine(_dir, 200), ex);
        } // 残骸掃除失敗は無害・診断のため trace

        try
        {
            // BK-L-6: per-file の破損 catch / invalid-id / null-record を trace で可視化する。
            // file パスは JSON の内容(攻撃者制御可能)ではなくディレクトリ列挙で得た値だが、
            // %APPDATA%\kxEdit\backups 配下に置かれるファイル名は「.json」拡張子と Directory 名以外は
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
        kxEdit.Core.Session.SessionLayout? Layout,
        IReadOnlyList<BackupRecord> Backups
    ) CollectForSilentRestore()
    {
        var layout = kxEdit.Core.Session.SessionLayoutStore.Load(_layoutPath);
        var backups = LoadAllForRestore();
        return (layout, backups);
    }

    /// <summary>復元成功後にレイアウトを消費する(次回は今セッションの新レイアウトが正)。
    /// 消費後は次 Reconcile を強制書込に倒し、session-state.json 不在の窓を最小化する(M-4)。</summary>
    public void DeleteConsumedLayout()
    {
        kxEdit.Core.Session.SessionLayoutStore.Delete(_layoutPath);
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

    /// <summary>
    /// A-1 / M-31(設計 2026-08-22 §3.2): clean 化・クローズだけを即時反映する。
    /// </summary>
    /// <remarks>
    /// dirty 化(becameUnneeded=false)では何もしない。dirty 化で不要になるものは何も無い一方、
    /// <see cref="ReconcileLayout"/> はキャレット位置を署名に含むため、対称に配線すると
    /// 「保存後の 1 打鍵目」ごとに session-state.json の背景書込が増えるだけになる
    /// (レイアウトは次 tick と終了時の FinalFlush が確定させる)。
    /// なお <see cref="ReconcileMapMaintenance"/> は削除しかしないため、対称配線でも
    /// バックアップ本文が増えるわけではない(ここを取り違えないこと=
    /// テストの観測点もレイアウト書込に置いている)。
    /// 走らせるのが full <see cref="Reconcile"/> ではなく <see cref="ReconcileMapMaintenance"/>
    /// なのも同じ理由: <see cref="ReconcileContent"/> は他の dirty タブに対して SnapshotText
    /// (全文 string 化)を走らせるため、Ctrl+S ごとに呼ぶと巨大 dirty タブ同居時に保存の
    /// 応答時間が悪化する。必要なのは「clean 化 / 閉じた文書のバックアップ削除 + レイアウト更新」
    /// だけで、これは ReconcileMapMaintenance の意味論そのもの。
    /// ReconcileMapMaintenance は info.ForceWrite を落とさないが、
    /// <see cref="BackupPlanner.Decide"/> は modified=false のとき forceWrite を見ないため無害
    /// (次に dirty 化したとき 1 回余分に書くだけ = 安全側)。
    /// </remarks>
    private void OnBackupBecameUnneeded(bool becameUnneeded)
    {
        if (!becameUnneeded)
            return;
        if (_shutDown || !_startupRestoreDone || (!_enabled && !_sessionRestoreEnabled))
            return;
        ReconcileMapMaintenance();
        if (_sessionRestoreEnabled)
            ReconcileLayout(force: false);
    }

    /// <summary>起動時復元(MainForm.OnShown)が終わったことを通知する。これ以降のみ
    /// 保存点・クローズの即時反映が働く(設計 2026-08-22 §3.3)。呼び忘れると A-1 の修正が
    /// 丸ごと死ぬため、MainFormSmokeTests が実経路で固定する。</summary>
    public void MarkStartupRestoreComplete() => _startupRestoreDone = true;

    /// <summary>テスト観測用: ゲートが開いているか。MainForm が
    /// <see cref="MarkStartupRestoreComplete"/> を呼んだことを実経路から固定する seam
    /// (Coordinator 側テストは seam を直接叩くため配線漏れを検出できない)。</summary>
    internal bool StartupRestoreDoneForTest => _startupRestoreDone;

    /// <summary>本文バックアップの走査(旧 Reconcile 本体をそのまま抽出・挙動不変)。</summary>
    private void ReconcileContent()
    {
        // 背景書込が失敗した文書を強制再書込対象にする(楽観更新で欠落・陳腐化しないように)。
        bool anyFailed = false;
        while (_failed.TryDequeue(out var failedId))
        {
            anyFailed = true;
            foreach (var v in _map.Values)
                if (v.Id == failedId)
                    v.ForceWrite = true;
        }
        // M-20: 成功フラグは<b>健全なときも必ず読み捨てる</b>(Exchange の第 2 引数が 0 なのは
        // そのため)。残すと、後で失敗して unhealthy になった次の drain が、失敗より前に届いて
        // いた古い成功を今回の成功と読んで「復旧した」と報告する。
        bool anySucceeded = Interlocked.Exchange(ref _writeSucceeded, 0) == 1;
        ReportBackupHealth(anyFailed, anySucceeded);

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

    /// <summary>M-20(B5): 書込の健全性が遷移したときだけ報告する。
    /// <para><b>同一 pass に失敗と成功が両方あれば失敗が勝つ。</b> 複数文書のうち 1 つだけ
    /// 書けている状態を「復旧」と呼ばないため。</para>
    /// <para><b>成功の観測が必須である理由</b>: 「失敗が来ない」だけでは、書込が成功したのか
    /// <b>そもそも投入していない</b>のか(dirty でない・署名一致で <c>BackupAction.None</c>)を
    /// 区別できない。後者を復旧と読むと、一度も書けていないのに「再開しました」と言うことに
    /// なる(<see cref="IBackupWriter.OnWriteSucceeded"/> がある理由そのもの)。</para>
    /// <para><b>early return であって else-if ではない。</b> else-if で書くと、既に unhealthy の
    /// ときに第 1 分岐が <c>anyFailed &amp;&amp; false</c> で落ち、第 2 分岐
    /// (<c>anySucceeded &amp;&amp; !_backupHealthy</c>)が成立して<b>誤って復旧を報告する</b>。
    /// 文書 A の書込先が恒久的に塞がれ文書 B は正常、という構成で毎 drain が
    /// 「失敗 A + 成功 B」になり、tick ごとに失敗と復旧を交互に言い続ける。
    /// 上の「失敗が勝つ」が健全なときにしか効かない形になるということ。</para>
    /// <para><c>_enabled == false</c>(レイアウトのみモード)では <see cref="Reconcile"/> も
    /// <see cref="FinalFlushForRestore"/> も <see cref="ReconcileMapMaintenance"/> 側へ分岐して
    /// 本メソッドが走らない = 報告も起きない。<b>バックアップを書いていないのだから正しい</b>。
    /// ただしその間 <see cref="_failed"/> は溜まったままなので(drain がここ 1 箇所しかない)、
    /// 後で ON へ戻した最初の pass が<b>切替より前の失敗</b>を報告しうる。文言が過去形の断定に
    /// 留めてあるぶん嘘にはならないが、鳴る時点が実際の失敗より遅れることはある。</para></summary>
    private void ReportBackupHealth(bool anyFailed, bool anySucceeded)
    {
        if (anyFailed)
        {
            if (_backupHealthy)
            {
                _backupHealthy = false;
                OnBackupHealthChanged?.Invoke(false);
            }
            return; // 失敗がある pass では復旧の判定に入らない
        }
        if (anySucceeded && !_backupHealthy)
        {
            _backupHealthy = true;
            OnBackupHealthChanged?.Invoke(true);
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
    private kxEdit.Core.Session.SessionLayout BuildLayout()
    {
        var tabs = new List<kxEdit.Core.Session.SessionLayoutRecord>();
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
                new kxEdit.Core.Session.SessionLayoutRecord(
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
        return new kxEdit.Core.Session.SessionLayout(tabs, _clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>レイアウト署名(64bit)。全フィールドを '\x1'(フィールド)/'\x2'(レコード)区切りで
    /// 連結し ContentSignature に流す。SavedAtUtc は含めない(含めると毎 tick 異なり抑止が死ぬ)。</summary>
    private static long LayoutSig(kxEdit.Core.Session.SessionLayout layout)
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
    /// A-8(設計 2026-08-24 §5): <see cref="FinalFlushForRestore"/> で投入した本文書込が
    /// 全て成功したかを待ち合わせて答える。hot exit の確認スキップを決める**前**に呼ぶ事後条件検査。
    /// </summary>
    /// <remarks>
    /// <para>false の意味は「未保存本文が永続化されたと言い切れない」であって「失敗が確定した」ではない
    /// (timeout も false)。呼び出し側は従来の未保存確認へ倒すこと。</para>
    /// <para><b>前提条件: 必ず <see cref="FinalFlushForRestore"/> の直後に、対で呼ぶこと。</b>
    /// 先行 tick の失敗 Id を消すのは <see cref="ReconcileContent"/> 冒頭の drain だけなので、
    /// 対で呼ばないと今回の flush と無関係な古い失敗を読む。とくに BackupEnabled OFF
    /// (<c>_enabled == false</c>)では <see cref="Reconcile"/> が
    /// <see cref="ReconcileMapMaintenance"/> 側へ分岐して drain が二度と走らないため、
    /// 残留した失敗 Id を<b>恒久的に読み続けて false を返し続ける</b>
    /// (現在の呼び出し側は BackupEnabled を前提ゲートに置くので到達しない)。
    /// つまりこの API の正しさは、設計 §3 が掲げる事後条件検査そのものではなく
    /// 「flush と対で呼ぶ」呼び出し規約に支えられている=前提ゲートを増やす向きの変更をするときは
    /// ここを読み直すこと。</para>
    /// <para>UI スレッドから呼ぶこと(<see cref="BackupCoordinator"/> は <c>_map</c> を
    /// 非スレッドセーフな Dictionary で持つ UI スレッド専有クラスで、末尾バリア＝全保留ジョブの完了、
    /// という <see cref="IBackupWriter.WaitForPendingJobs"/> の前提も UI スレッド単一が根拠)。
    /// 最大 <see cref="FinalFlushWait"/> の間 UI スレッドをブロックする。</para>
    /// </remarks>
    public bool WaitForFinalFlush() => WaitForFinalFlush(FinalFlushWait);

    /// <summary>実装本体。既定値(<see cref="FinalFlushWait"/>)の適用点を上のラッパ 1 箇所に
    /// 閉じ込めるため timeout を明示引数で受ける。テストからは呼ばれない(timeout 経路の検証は
    /// <see cref="IBackupWriter.WaitForPendingJobs"/> を false で返す fake writer と、
    /// その fake が受け取った timeout の突き合わせで行う)ため private。</summary>
    private bool WaitForFinalFlush(TimeSpan timeout)
    {
        // _shutDown の判定を先に置くのは必須: Shutdown/Dispose は _shutDown=true の直後に
        // _writer を破棄するので「_writer is not null」では破棄済みを弾けない。破棄済みの
        // WaitForPendingJobs は「これ以上投入されない」の意味で true を返す(=保留ゼロの保証では
        // ない)ため、事後条件検査に使うと嘘になる(IBackupWriter の契約コメント参照)。
        if (_shutDown || _writer is null)
            return true; // 書くものが無い=失敗も無い
        if (!_writer.WaitForPendingJobs(timeout))
            return false; // 完了を確認できない=安全側で失敗扱い
        // 意図的に dequeue しない: ここで吸い出すと、終了がキャンセルされたときに
        // ReconcileContent 冒頭の ForceWrite 再試行が失敗を見失う(A-8 と同じ握り潰しの新設)。
        // 代償(安全側の偽陽性)は、前提条件どおり flush と対で呼ぶ限り 1 機構に絞られる:
        // 冒頭 drain より後・バリア完了より前に届いた「前 tick 由来の Write ジョブ」の失敗通知が、
        // 今回の flush の失敗として数えられるケース(その文書を今回の flush が書き直して成功して
        // いても false になる)。drain は同じ呼び出し連鎖の冒頭で走るので、それ以前の失敗 Id は
        // ここには残っていない=「失敗後に clean 化した文書」の残留は前提条件を破った場合の話。
        // いずれも実害は silent close をやめて既存の未保存確認へ倒すこと(申し送り S-A8-1)。
        return _failed.IsEmpty;
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
                kxEdit.Core.Session.SessionLayoutStore.Delete(_layoutPath);
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
