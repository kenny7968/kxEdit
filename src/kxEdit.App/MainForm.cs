using kxEdit.App.Settings;
using kxEdit.App.Speech;
using kxEdit.Core.Backup;
using kxEdit.Core.Csv;
using kxEdit.Core.IO;
using kxEdit.Core.Reading;
using kxEdit.Core.Search;
using kxEdit.Core.Settings;
using kxEdit.Core.Text;
using kxEdit.Editor;

namespace kxEdit.App;

public sealed partial class MainForm : Form
{
    private readonly DocumentManager _docs;
    private readonly FileController _file; // コンストラクタで生成
    private readonly SearchController _search; // コンストラクタで生成
    private readonly GrepController _grep; // コンストラクタで生成
    private readonly BackupCoordinator _backup; // コンストラクタで生成
    private readonly CsvController _csv; // コンストラクタで生成
    private readonly KinsokuFormatController _kinsoku; // コンストラクタで生成(FormatWithKinsoku を委譲)
    private readonly DocumentInfoController _documentInfo; // コンストラクタで生成([ファイル]>文書情報)
    private bool _restoreOffered; // 起動時の復元提案を一度だけ行う

    /// <summary>M-18: CSV モードのタブを読み直している間だけ true。<see cref="AutoEnterCsvMode"/>(自動モード)を
    /// 読み直しの中では飛ばし、<see cref="CheckExternalChangeOnActive"/> が発声の後にモードへ戻す
    /// (発声順を手動モードと揃え、二重パースを避ける。最終コード品質レビュー Q-1)。</summary>
    private bool _reloadingCsv;
    private readonly ToolStripStatusLabel _posLabel = new("行 1, 桁 1");
    private readonly ToolStripStatusLabel _encLabel = new("UTF-8");
    private readonly ToolStripStatusLabel _eolLabel = new("CRLF");

    // SR への能動通知用ラベル（底部・最後の通知を視覚表示）。フォーカス不可なので編集を妨げない。
    private readonly Label _announceLabel = new()
    {
        Dock = DockStyle.Bottom,
        Height = 22,
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft,
        AccessibleName = "通知",
    };

    // CA1859: 実体は常に UiaAnnouncer(65 行下 ctor で直接生成)。
    // downstream (SearchController / GrepDialog / KinsokuFormatController 等) には
    // 依然 IAnnouncer として渡される(implicit conversion)ため公開契約は不変。
    private readonly UiaAnnouncer _announcer;
    private ToolStripMenuItem _recentMenu = null!; // BuildMenu で生成
    private readonly string _settingsPath;

    // PR4 C-6 (S3604): public/internal 全 ctor が chain 経由で internal MainForm(settings, _)
    // に到達=`_settings = settings;` が必ず走るため、field 初期化子は冗長。
    private AppSettings _settings;

    // Alt 等でメニューがアクティブな間は CSV の素キー横取りを止め、矢印/文字キーをメニュー操作へ通す。
    // メニューモードに入っても本文(EditorControl)はフォーカスを保持するため ContainsFocus では判別できず、
    // MenuStrip の Activate/Deactivate イベントで明示的に追跡する。
    private bool _menuActive;

    // テストが実 %APPDATA% を汚さないための seam。null=既定パス。
    // hot exit 統合後はレガシー移行(RestoreUnifiedSession)の Load/Delete と
    // OFF 終了時の orphan 掃除(OnFormClosing)だけが使う。
    private string? _lastSessionBuffersPathOverride;
    private string LastSessionBuffersPath =>
        _lastSessionBuffersPathOverride ?? kxEdit.Core.Session.LastSessionBuffersStore.DefaultPath;

    internal void SetLastSessionBuffersPathForTest(string path) =>
        _lastSessionBuffersPathOverride = path;

    // 起動時の空無題タブ(ctor で作った 1 個)を覚え、復元成功時に閉じるための seam。
    // FileController.RestoreSession の initialEmpty 引数に渡す=前回タブが 1 つでも復元
    // できた時のみ破棄される契約(Task 5 review M-2)。ctor 末尾で 1 度だけ代入し以後不変。
    private readonly Document? _startupEmptyDoc;

    // Task 7 + A-1 第 2 層テスト用: 起動時復元の Warn ダイアログ (MessageBox.Show=blocking) を
    // テストで抑止する seam。
    // 対象は 2 種 = FailedPaths の集約警告(ShowFailedRestoreDialog)と、A-1 第 2 層の陳腐化警告
    // (ShowStaleBackupWarning)。MessageBox が UI スレッドを塞ぐのを避けるため。
    // 実運用経路では常に false=ダイアログは出る。
    // Form 派生上の bool プロパティは WFO1000 を誘発するため、field + setter method で seam を作る
    // (SetLastSessionBuffersPathForTest と同じ方式)。
    private bool _suppressRestoreDialogsForTest;

    internal void SetSuppressRestoreDialogsForTest(bool value) =>
        _suppressRestoreDialogsForTest = value;

    // A-1 第 2 層テスト用: 陳腐化警告に到達した回数(抑止中でも数える)。ダイアログ自体は
    // MessageBox=blocking で観測できないため、到達を数だけ観測して配線を固定する。
    private int _staleBackupWarningCountForTest;
    internal int StaleBackupWarningCountForTest => _staleBackupWarningCountForTest;

    /// <summary>A-1 / M-31 テスト用: OnShown が即時反映ゲートを開いたか
    /// (<see cref="BackupCoordinator.StartupRestoreDoneForTest"/> の中継)。Coordinator 側の
    /// テストは seam を直接叩くため、MainForm の配線漏れをそちらでは検出できない。
    /// Coordinator 全体を露出せず観測点 1 個に絞る(レビュー M-3)。</summary>
    internal bool StartupRestoreGateOpenForTest => _backup.StartupRestoreDoneForTest;

    /// <summary>M-11(設計 2026-09-02 §5.4): 起動時に 1 回だけ出す設定の警告文言
    /// (<c>SettingsStartup.Prepare</c> が組み立て済み)。null=警告なし。</summary>
    private readonly string? _settingsWarning;

    /// <summary>B5(設計 2026-09-02 §6.3 = B4 申し送りの回収): 読み取れなかった設定ファイルを、
    /// <b>このセッションの最初の保存の直前に</b> <c>.bak</c> へ退避するか。
    /// <c>SettingsStartup.Prepare</c> が <c>Unreadable</c> の枝でだけ立てる。
    /// <b>readonly ではない</b> —— 表すのは「まだ試していない」ではなく<b>「原本がまだ在る」</b>で、
    /// ①退避が成功したとき ②保存が成功したとき(= 原本はもう無い)に落とす
    /// (<see cref="TrySaveSettings"/>)。<b>試行した時点では落とさない</b> ——
    /// 一過性ロックで退避も保存も落ちた回に落とすと、ロックが外れた次の保存が
    /// <c>.bak</c> を残さずに原本を消す(仕様レビュー I-1)。</summary>
    private bool _quarantineSettingsBeforeFirstSave;

    // M-11 テスト用: 設定警告に到達した回数(抑止中でも数える)。MessageBox は blocking で
    // 観測できないため、陳腐化警告と同じく到達数だけを見る。
    private int _settingsWarningCountForTest;
    internal int SettingsWarningCountForTest => _settingsWarningCountForTest;

    // M-22 テスト用: 保存失敗ダイアログの抑止と観測。MessageBox=blocking なので実表示そのものは
    // テストから叩けず、代わりに「到達した回数」と「渡された本文」を写し取る ——
    // 回数だけでは「常に同じ文字列を渡す」変異が生き残るため、本文まで観測面に載せる。
    // 実運用経路では常に false=ダイアログは出る(seam の作り方は _suppressRestoreDialogsForTest に倣う:
    // Form 派生上の bool プロパティは WFO1000 を誘発するので field + setter method)。
    private bool _suppressSettingsSaveFailedDialogForTest;

    internal void SetSuppressSettingsSaveFailedDialogForTest(bool value) =>
        _suppressSettingsSaveFailedDialogForTest = value;

    private int _settingsSaveFailedDialogCountForTest;
    internal int SettingsSaveFailedDialogCountForTest => _settingsSaveFailedDialogCountForTest;

    private string? _settingsSaveFailedDialogBodyForTest;
    internal string? SettingsSaveFailedDialogBodyForTest => _settingsSaveFailedDialogBodyForTest;

    /// <summary>M-22 テスト用: <see cref="ApplySettings"/> が <c>_backup.UpdateSettings</c> まで
    /// 通っているかの観測点(<see cref="BackupCoordinator.TimerIntervalMs"/> の中継)。
    /// 発声だけを見ていると、<b>外観適用と UpdateSettings をどちらも削っても全緑</b>になる
    /// (仕様レビュー M-3・実測)。Coordinator 全体を露出せず観測点 1 個に絞るのは
    /// <see cref="StartupRestoreGateOpenForTest"/> と同じ方針。</summary>
    internal int BackupTimerIntervalMsForTest => _backup.TimerIntervalMs;

    /// <summary>M-20 テスト用: <see cref="BackupCoordinator.OnBackupHealthChanged"/> に
    /// <b>実際に配線されたもの</b>を読んで撃つ。配線の 1 行が消えればフックは null になり、
    /// 発声が起きない=<see cref="LastAnnouncementForTest"/> の網が落ちる。
    /// <para>遷移の<b>判定</b>そのものは Coordinator 側のテストが固定する。実 <see cref="MainForm"/> で
    /// 本物の書込失敗を起こすには背景ライターと壊れた書込先と tick(既定 300 秒)が要り、
    /// L3 では再現が脆いため、ここは<b>配線と文言</b>だけを引き受ける。</para></summary>
    internal void InvokeBackupHealthChangedForTest(bool healthy) =>
        _backup.OnBackupHealthChanged?.Invoke(healthy);

    /// <summary>M-20 テスト用: 実 <see cref="BackupCoordinator"/> へ背景書込の失敗を注入する
    /// (実 writer の <c>OnWriteFailed</c> が入る受け口と同じ場所)。遷移は次の drain で実経路
    /// どおりに起きるので、<b>抑止・言い直しの網を実 <see cref="MainForm"/> の上で張れる</b>。
    /// <see cref="BackupCoordinator.InjectWriteFailureForTest"/> の中継。</summary>
    internal void InjectBackupWriteFailureForTest(string id) =>
        _backup.InjectWriteFailureForTest(id);

    /// <summary>M-20 テスト観測用: いま健全とみなしているか
    /// (<see cref="BackupCoordinator.BackupHealthy"/> の中継)。「言わなかった」ことの検証が
    /// vacuous にならないよう、<b>遷移が実際に起きたこと</b>を突き合わせるために要る。
    /// Coordinator 全体を露出せず観測点 1 個に絞るのは
    /// <see cref="BackupTimerIntervalMsForTest"/> と同じ方針。</summary>
    internal bool BackupHealthyForTest => _backup.BackupHealthy;

    // M-20: 健全性の発声が実際に出た回数(SayBackupHealth の到達記録)。
    // 抑止された分は数えない = 「終端では言わない」をフォーム破棄後にも検証できる唯一の面。
    // 最終レビュー I-2 以降は<b>本番の判定材料でもある</b>—— ApplySettings が
    // 「直下の drain がこの呼出の中で実際に鳴らしたか」をこの値の差で見る。
    private int _backupHealthSaidCount;

    /// <summary>M-20 テスト観測用: 健全性の発声が実際に出た回数
    /// (理由は <see cref="SayBackupHealth"/> の xmldoc)。</summary>
    internal int BackupHealthSaidCountForTest => _backupHealthSaidCount;

    /// <summary>最終レビュー I-2: 直近に <see cref="SayBackupHealth"/> が言った健全性。
    /// 言い直し(<see cref="ApplySettings"/> の末尾)が<b>直前に鳴らしたのと同じ事実</b>を
    /// 繰り返すために要る —— 状態(<c>_backup.BackupHealthy</c>)を読み直す形にすると、
    /// 報告と言い直しの間に状態が動いた場合に別の事実を言うことになる。</summary>
    private bool _lastBackupHealthSaid;

    /// <summary>最終レビュー M-7: 終端フラッシュ(<see cref="OnFormClosing"/> /
    /// <see cref="FlushBackupsForCrash"/>)の drain で<b>抑止した</b>健全性の報告
    /// (null=抑止していない)。終了をキャンセルしたときに言い直す判断を、
    /// 「今 unhealthy か」という状態ではなく<b>抑止が実際に起きたか</b>という事象で行うための記録。
    /// 値まで持つのは、抑止されるのが失敗の報告とは限らない(復旧も同じ経路で飲み込まれる)ため。</summary>
    private bool? _suppressedBackupHealth;

    /// <summary>M-11 テスト用: 設定警告に到達した<b>位置</b>の観測点(null=未到達)。
    /// 回数だけでは<b>順序を入れ替える変異が殺せない</b>——復元より前へ動かしても、
    /// 陳腐化警告より後ろへ動かしても、どちらの到達数も 1 のままだからである。そこで
    /// 到達した瞬間の周囲の状態を写し取る:
    /// <list type="bullet">
    /// <item><c>RestoreGateOpen</c> … 復元ブロックの <c>finally</c>
    /// (<see cref="BackupCoordinator.MarkStartupRestoreComplete"/>)を抜けた後か。
    /// false へ倒れる = モーダルを復元より前で出す = A-8 と同型の再入経路を開く。</item>
    /// <item><c>StaleWarningCount</c> … 陳腐化警告より前か。1 になる = 順序が入れ替わっている。</item>
    /// </list>
    /// 代入はテスト観測専用で、実挙動には影響しない。</summary>
    private (bool RestoreGateOpen, int StaleWarningCount)? _settingsWarningReachedAtForTest;

    internal (bool RestoreGateOpen, int StaleWarningCount)? SettingsWarningReachedAtForTest =>
        _settingsWarningReachedAtForTest;

    // A-8 / H-1(設計 2026-08-24 §10.7): OnFormClosing 実行中フラグ(再入ガード)。
    // 最終 flush の完了待ちが UI スレッドをブロックしている最中に SENT メッセージ経由で
    // クローズが再入しうるため。機構と対処の根拠は OnFormClosing 冒頭のコメント参照。
    private bool _closeInProgress;

    // Task 13 テスト用: OnFormClosing が silent path(§8.2 fast-path)を通ったかを観測する。
    // null = OnFormClosing 未実行 / true = silent (ConfirmDiscardIfDirty loop skip) / false = fall-through。
    // A-8: <see cref="LastCloseFinalFlushOkForTest"/> と組で読む(下の 3 状態表を参照)。
    private bool? _lastCloseTookSilentPathForTest;
    internal bool? LastCloseTookSilentPathForTest => _lastCloseTookSilentPathForTest;

    /// <summary>A-8: 直近のクローズで、hot exit の事後条件検査(最終 flush の成否)が
    /// どう出たか。null=OnFormClosing 未実行、または検査に到達しなかった
    /// (前提ゲートで既に silent close ではない)。
    /// oversized による fall-through と「バックアップ書込失敗」による fall-through を
    /// テストが弁別するための seam。
    /// <see cref="LastCloseTookSilentPathForTest"/> と組で
    /// <c>(silent, flushOk)</c> の 3 状態を弁別する:
    /// <c>(true, true)</c>=hot exit の確認なしクローズ /
    /// <c>(false, false)</c>=A-8 の書込失敗フォールバック /
    /// <c>(false, null)</c>=前提ゲート(OFF・BackupOFF・oversized)での fall-through。
    /// <c>(true, null)</c> と <c>(false, true)</c> は構造上あり得ない。</summary>
    private bool? _lastCloseFinalFlushOkForTest;

    internal bool? LastCloseFinalFlushOkForTest => _lastCloseFinalFlushOkForTest;

    // Task 13 テスト用: fall-through 経路の ConfirmDiscardIfDirty 呼出を差し替える。
    // null = 通常経路 (実 _file.ConfirmDiscardIfDirty=MessageBox 発火) / 非 null = 呼出をこの delegate に置き換え。
    // テストでは MessageBox がブロックしないよう常に override を渡すこと。返り値=保存/破棄成功=true / キャンセル=false。
    private Func<Document, bool>? _confirmDiscardOverrideForTest;

    internal void SetConfirmDiscardOverrideForTest(Func<Document, bool>? overrideFunc) =>
        _confirmDiscardOverrideForTest = overrideFunc;

    public MainForm(AppSettings settings)
        : this(settings, SettingsStore.DefaultPath) { }

    /// <summary>
    /// テストで実設定ファイルを汚さないため internal 経由で settingsPath を注入可能に
    /// (既存の public コンストラクタ経路は不変=Program.Main は DefaultPath へチェーン)。
    /// hot exit 統合(設計 2026-07-23 統合 §3.1-§3.3): backupDirectory / sessionLayoutPath も
    /// 同様にテスト隔離用(null=既定 %APPDATA% パス)。
    /// <para>
    /// <paramref name="settingsWarning"/> = 起動時に 1 回だけ出す設定の警告(M-11・設計
    /// 2026-09-02 §5.4)。null=警告なし。<paramref name="quarantineSettingsBeforeFirstSave"/> =
    /// 最初の保存の直前に読み取れなかった原本を退避するか(B5・設計 §6.3)。どちらも
    /// <c>Program.CreateMainForm</c> が <c>SettingsStartup.Prepare</c> の戻り値をそのまま渡す。
    /// <paramref name="prompt"/> = 確認ダイアログの seam(テスト用。null = <c>MessageBoxUserPrompt</c>)。
    /// M-18 の MainForm 側配線をモーダル無しで検証するため。
    /// <b>public ctor には足していない</b>
    /// —— 足すと 2 引数の位置指定呼出が <c>(settings, settingsPath)</c> ではなくそちらへ
    /// 黙って束縛される(省略した任意引数が少ない方が優先される)。
    /// </para>
    /// </summary>
    internal MainForm(
        AppSettings settings,
        string settingsPath,
        string? backupDirectory = null,
        string? sessionLayoutPath = null,
        string? settingsWarning = null,
        bool quarantineSettingsBeforeFirstSave = false,
        IUserPrompt? prompt = null
    )
    {
        _settingsPath = settingsPath;
        _settings = settings; // Program.Main が読込済み
        _settingsWarning = settingsWarning;
        _quarantineSettingsBeforeFirstSave = quarantineSettingsBeforeFirstSave;

        Text = "kxEdit";
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        StartPosition = FormStartPosition.CenterScreen;

        _docs = new DocumentManager(CreateEditor);
        // Announcer は KeyBasedSwitch のラムダで参照されるため、event 購読より前に確定させる
        // (readonly 化に伴い null! 初期化を廃止 → definite assignment を先に済ませる)。
        _announcer = new UiaAnnouncer(_announceLabel);
        _docs.ActiveDocumentChanged += (_, _) =>
        {
            UpdateTitle();
            UpdateStatus();
            // M-18(設計 2026-09-03 §3.4): TabControl の選択変更ハンドラの中でモーダルを出さない
            // (WinForms の再入)。BeginInvoke 先で「まだそのタブがアクティブ」「フォームがアクティブ」を
            // 再確認する。ctor 中(ハンドル未生成)の発火は BeginInvoke できないので見送る
            // (起動直後の無題タブは Path=null で判定対象にならない)。
            var doc = _docs.Active;
            if (doc is null || !IsHandleCreated)
                return;
            BeginInvoke(() =>
            {
                if (IsDisposed || ActiveForm != this || !ReferenceEquals(_docs.Active, doc))
                    return;
                CheckExternalChangeOnActive();
            });
        };
        _docs.KeyBasedSwitch += (_, doc) => _announcer.Say(doc.TabLabel);
        // A-13(設計 2026-08-29 §4.3): クリップボードが他プロセスに保持されていると
        // Copy/Cut/Paste が失敗する。発生源(EditorControl)で捕捉済み=ここでは通知だけ行う。
        // 失敗しても本文は無傷(Cut も消さない)なので、伝えるべきは「今の操作が効かなかった」こと。
        _docs.ClipboardFailed += (_, kind) => _announcer.Say(ClipboardFailureMessage(kind));
        _docs.ActiveDirtyChanged += (_, _) => UpdateTitle();
        _docs.ActiveCaretChanged += (_, _) => UpdateStatus();
        // 設定は OpenSettings で参照が差し替わるため Func で都度解決させる。
        // Stage 8 A.3: delegate 4 個(saveSettings/recentChanged/metaChanged/openedFresh)が同型 Action で
        // 入れ替わっても検出不能なため、名前付き引数化で自己ドキュメント化(Stage 4 の教訓)。
        _file = new FileController(
            docs: _docs,
            owner: this,
            settings: () => _settings,
            saveSettings: SaveSettingsSafe,
            recentChanged: RebuildRecentMenu,
            metaChanged: () =>
            {
                UpdateTitle();
                UpdateStatus();
            },
            openedFresh: AutoEnterCsvMode,
            prompt: prompt ?? new MessageBoxUserPrompt(),
            fileDialogs: new WinFormsFileDialogService(),
            reachabilityProbe: new FileReachabilityProbe(),
            fileTimestamps: new FileTimestampProvider()
        );
        _search = new SearchController(_docs, this, _announcer, cb => new FindReplaceDialog(cb));
        _grep = new GrepController(
            docs: _docs,
            owner: this,
            // Batch D Task 12: GrepDialog は new UiaAnnouncer(_status) の直生成を廃止し
            // 共有 _announcer(SearchController と同型経路)を注入する。
            viewFactory: cb => new GrepDialog(cb, _announcer),
            resultsFactory: () => new GrepResultsWindow(new GrepResultsCallbacks(OpenAndSelect))
        );
        _backup = new BackupCoordinator(
            _docs,
            _settings.BackupEnabled,
            _settings.BackupIntervalSeconds,
            TimeProvider.System,
            // BK-M-2: session dir は BackupCoordinator ctor 内で生成し factory に渡す
            // (Func<string, IBackupWriter> シグニチャ)。ここで DefaultDirectory を直埋めしない=
            // base dir と混同するミスを compile-time で防ぐ。
            sessionDir => new SerialBackupWriter(sessionDir),
            new WinFormsRestorePrompt(),
            directory: backupDirectory,
            restoreSessionEnabled: settings.RestoreOpenFilesOnStartup,
            sessionLayoutPath: sessionLayoutPath
        );
        // M-20(B5): バックアップ書込の健全性が遷移したときだけ知らせる。修正前は書込が失敗しても
        // ユーザーへ出る面が一つも無く、既定 tick 300 秒のまま「守られている」と信じて編集が続いた。
        // 一過性の失敗では「失敗」「復旧」の 2 回鳴りうるが、その間バックアップは実際に効いて
        // いなかったので、黙る側ではなく言う側へ倒す(設計 §5.5 (b) で受容)。
        //
        // 文言の根拠は BackupHealthMessage の xmldoc(言い直しの経路が 2 つあるので 1 箇所に閉じる)。
        _backup.OnBackupHealthChanged = healthy =>
        {
            // 仕様レビュー I-2: 終端フラッシュ(通常終了の OnFormClosing / クラッシュ時の
            // FlushBackupsForCrash)の drain から来た報告は言わない。理由は 3 つ:
            // (a) 助言「ファイルを保存してください」がその場で原理的に実行不能である
            //     (クラッシュ経路は直後に Environment.Exit(1) が続く)。
            // (b) 通常終了は A-8 の WaitForFinalFlush が既にユーザーへ届ける仕組みを持つ ——
            //     退避を確認できなければ silent close をやめて未保存確認へ倒す。そちらの方が
            //     正確(実際に書けたかを見ている)で行動可能。しかも終端 flush では、drain が
            //     セットした ForceWrite による再書込が**同じ pass の中で**走るため、
            //     「復元できない可能性がある」と言った直後に実際には退避できている、という
            //     偽の発声になりうる(復旧の報告は次 pass が無いので永遠に来ない)。
            // (c) クラッシュ経路では UiCrashSink.CrashMessage(「退避したので次回起動時に
            //     復元できます」)が権威ある案内であり、それと矛盾する発声を重ねない。
            // _closeInProgress は OnFormClosing / FlushBackupsForCrash のどちらでも
            // FinalFlushForRestore の**呼出前に**立つ(実コードで確認)。前者は finally で必ず
            // 戻すので、終了がキャンセルされればフックは通常運用へ復帰する。
            if (_closeInProgress)
            {
                // 最終レビュー M-7: 飲み込んだ事実を残す。終了がキャンセルされたときの言い直しは、
                // 「今 unhealthy か」ではなく<b>ここで抑止が起きたか</b>を条件にする。
                _suppressedBackupHealth = healthy;
                return;
            }
            SayBackupHealth(healthy);
        };
        _csv = new CsvController(
            docs: _docs,
            announcer: _announcer,
            cellPicker: new WinFormsCellPicker()
        );
        _kinsoku = new KinsokuFormatController(_docs, _announcer);
        _documentInfo = new DocumentInfoController(_docs);
        _docs.BeforeActiveChange = () => _csv.AbortEdit(); // タブ切替直前に F2 編集を中断（焦点の引き戻し防止）
        // P6 で編集エンジンが自作 EditorControl (v2 UIA 単一経路) に統一されたため、
        // CSVモード中に Editor がフォーカスを得た瞬間にシンクへ強制退避していた仕組みは撤去。
        // 誤読み抑止は CsvController.TryEnterMode の RaiseUiaSelectionEvents=false が担う。
        // _docs.EditorGotFocus 自体は §0-8 の撤退安全性のため残す(購読ゼロで実質死・P7 で撤去)。

        var menu = BuildMenu();
        var status = BuildStatusBar();

        Controls.Add(_docs.TabHost);
        Controls.Add(status);
        Controls.Add(_announceLabel); // 最下部（status の下）
        Controls.Add(menu);
        MainMenuStrip = menu;

        _file.NewFile(); // 起動時の無題タブ1つ（Q1=B：常に新規タブ）
        // 前回タブ復元が成功したとき、ctor で作った空無題タブを閉じるための参照
        // (FileController.RestoreSession の initialEmpty 引数=Task 5 review M-2)。
        _startupEmptyDoc = _docs.Active;
    }

    /// <summary>タブ毎の EditorControl を生成する。受動読みは EditorControl 単一経路（UIA v2）に一本化済み。</summary>
    private EditorControl CreateEditor()
    {
        var e = new EditorControl { Dock = DockStyle.Fill };
        EditorAppearance.Apply(e, _settings); // フォント＋配色テーマ＋表示設定を EditorControl.ApplyAppearance へ委譲
        return e;
    }

    /// <summary>開く系経路（開く/最近/開き直し）で新規ロードした直後の .csv 自動 CSV モード進入（設定 ON のときのみ）。</summary>
    private void AutoEnterCsvMode(Document doc)
    {
        if (_reloadingCsv)
            return; // M-18: 読み直し中は MainForm が発声の後にモードへ戻す(発声順を手動モードと揃え、二重パースを避ける)
        if (!_settings.CsvAutoModeOnOpen)
            return;
        if (
            !string.Equals(
                System.IO.Path.GetExtension(doc.State.Path),
                ".csv",
                StringComparison.OrdinalIgnoreCase
            )
        )
            return;
        _csv.TryEnterMode(doc); // 解析不可なら TryEnterMode が通知して通常モードのまま
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _docs.Active?.FocusTarget.Focus();
        UpdateTitle();
        UpdateStatus();

        if (_restoreOffered)
            return;
        _restoreOffered = true;

        try
        {
            if (_settings.RestoreOpenFilesOnStartup)
                // hot exit 統合復元(設計 §3.3): クラッシュ/正常終了を区別せず silent 復元。
                RestoreUnifiedSession();
            else
                OfferBackupRestoreOnStartup();
        }
        finally
        {
            // A-1 / M-31(設計 2026-08-22 §3.3): ここから先だけ、保存点・クローズの即時反映が働く。
            // 復元より前に有効化すると、ctor の NewFile → SetSavePoint が空無題 1 タブのレイアウトを
            // session-state.json へ書き、前回セッションを失う。
            // finally なのはレビュー L-3: OFF 経路(OfferBackupRestoreOnStartup)は ON 経路と違い
            // 全体 try/catch を持たないため、ここで例外が抜けるとゲートが二度と開かず、
            // そのプロセスの間ずっと A-1 の修正が黙って死ぬ(300 秒のクラッシュ窓が復活する)。
            _backup.MarkStartupRestoreComplete();
        }

        // M-11(設計 2026-09-02 §5.4): 設定が壊れていた / 読めなかったことを起動時に 1 回だけ伝える。
        // 文言の組み立て(退避先か原本か・無害化・語順)は SettingsStartup.Prepare が済ませており、
        // ここは出すだけ。位置には理由が 2 つある。
        //  (1) 復元より「後」: MessageBox はメッセージをポンプするので、復元ブロックの finally
        //      (MarkStartupRestoreComplete)より前でモーダルを出すと、ゲートが開く前に再入経路を
        //      開くことになる(A-8 と同型)。既存の陳腐化警告が復元の後に居るのも同じ理由。
        //  (2) 陳腐化警告より「前」: 設定が既定値へ戻っている事実は、復元が ON / OFF どちらで
        //      動いたかの説明にもなる(RestoreOpenFilesOnStartup / ConfirmRestoreOnStartup が
        //      既定へ戻っているため)。理由を先に渡してから結果の警告を読ませる。
        // 1 回だけなのは上の _restoreOffered による early return による(Shown はフォーム 1 個に
        // つき 1 回しか上がらないため、そもそも 2 周しない。MainFormSmokeTests で実測)。
        if (_settingsWarning is not null)
        {
            _settingsWarningCountForTest++;
            // 到達「位置」の観測点(テスト専用・実挙動には影響しない)。到達数だけでは順序を
            // 入れ替える変異が殺せないため、到達した瞬間の周囲の状態を写し取る。
            _settingsWarningReachedAtForTest = (
                _backup.StartupRestoreDoneForTest,
                _staleBackupWarningCountForTest
            );
            if (!_suppressRestoreDialogsForTest)
                ShowSettingsStartupWarning(_settingsWarning);
        }

        // A-1 第 2 層(設計 2026-08-22 §4.2): 復元したタブのうちディスク側が新しかったものを
        // 1 個の警告にまとめて通知する。ON / OFF どちらの復元経路も FileController を通るため
        // 回収点は 1 つでよい。
        var stale = _file.TakeStaleRestoredPaths();
        if (stale.Count > 0)
        {
            _staleBackupWarningCountForTest++;
            if (!_suppressRestoreDialogsForTest)
                ShowStaleBackupWarning(stale);
        }
    }

    /// <summary>OFF 経路(RestoreOpenFilesOnStartup=false)の従来どおりの復元提案。
    /// OnShown から切り出しただけで挙動は不変(早期 return を無くし、復元後の共通処理=
    /// ゲート開放と陳腐化警告を ON / OFF 双方で通すため)。</summary>
    private void OfferBackupRestoreOnStartup()
    {
        // 設計 2026-07-24-restore-no-initial-untitled §1: 復元件数>0 なら ON 経路
        // (FileController.RestoreSession の openedCount>0 で initialEmpty を TryClose)と対称に
        // 起動時の空無題タブ (_startupEmptyDoc) を閉じる。Announcer は従来どおり silent 自動復元
        // (!ConfirmRestoreOnStartup) のときのみ発話する(確認 ON はダイアログで件数を既知)。
        int restored = _backup.OfferRestoreOnStartup(
            this,
            _file.RestoreFromBackup,
            _settings.ConfirmRestoreOnStartup
        );
        if (restored > 0)
        {
            if (_startupEmptyDoc is not null)
                _docs.TryClose(_startupEmptyDoc, _ => true); // ON 経路と同じ「空無題は無条件破棄」
            if (!_settings.ConfirmRestoreOnStartup)
                _announcer.Say($"バックアップを {restored} 件復元しました");
        }
    }

    /// <summary>
    /// hot exit 統合復元(設計 §3.3/§8)。レイアウト+バックアップを silent 復元し、
    /// レガシー(PR #22)形式が残っていれば一回限り読み替える。失敗パスは集約 Warn 1 個。
    /// 想定外例外は Trace に落として通常起動へフォールバックする(E8)。
    /// </summary>
    private void RestoreUnifiedSession()
    {
        try
        {
            var (layout, backups) = _backup.CollectForSilentRestore();
            IReadOnlyList<BackupRecord> allBackups = backups;
            Action<Document, BackupRecord>? adopt = _backup.AdoptRestored;
            if (layout is null && _settings.LastSession is { Tabs.Count: > 0 } legacy)
            {
                // レガシー移行(設計 §8): 旧形式を統合復元の入力へ一回限り変換。
                var buffers = kxEdit.Core.Session.LastSessionBuffersStore.Load(
                    LastSessionBuffersPath
                );
                var (converted, synthetic) = kxEdit.Core.Session.LegacySessionConverter.Convert(
                    legacy,
                    buffers,
                    DateTime.UtcNow
                );
                layout = converted;
                if (synthetic.Count > 0)
                {
                    var merged = new List<BackupRecord>(backups.Count + synthetic.Count);
                    merged.AddRange(backups);
                    merged.AddRange(synthetic);
                    allBackups = merged;

                    // 計画 Task 6 コードからの意図的逸脱(Task 5 契約/設計 §8/§10 精密化 2 準拠):
                    // 合成レコードは in-memory のみ=ディスクに実体が無い。AdoptRestored で
                    // LastSig=現在値+HasBackup=true 登録すると BackupPlanner が None を返し続け、
                    // 本文バックアップが一度も書かれないまま次回起動の E9'/E4' demote で移行内容を
                    // silent 喪失する。合成 Id は adopt から除外し、通常の RegisterNew / FinalFlush
                    // 経路の新規書込で保護する。実バックアップ由来の extras は同一呼び出し内でも
                    // adopt-move を維持する(BK-M-2 再提案バグ修正の保存)。
                    var syntheticIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var s in synthetic)
                        syntheticIds.Add(s.Id);
                    adopt = (doc, rec) =>
                    {
                        if (!syntheticIds.Contains(rec.Id))
                            _backup.AdoptRestored(doc, rec);
                    };
                }
            }
            var failed = _file.RestoreSession(layout, allBackups, _startupEmptyDoc, adopt);
            _backup.DeleteConsumedLayout();
            _settings.LastSession = null; // レガシー残骸の掃除(次回 Save で消える)
            kxEdit.Core.Session.LastSessionBuffersStore.Delete(LastSessionBuffersPath);
            if (failed.Count > 0 && !_suppressRestoreDialogsForTest)
                ShowFailedRestoreDialog(failed);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "kxEdit: unified-restore failed: {0}",
                kxEdit.Core.Text.SanitizeForDisplay.OneLine(ex.Message, 200)
            );
        }
    }

    /// <summary>
    /// 復元できなかったパス群を 1 個の Warn ダイアログにまとめて表示する。最大 10 件表示、
    /// それ以上は「他 N 件」で省略。パスは <see cref="kxEdit.Core.Text.SanitizeForDisplay.OneLine"/>
    /// で BiDi/制御文字を無害化してから表示する(RLO 等の欺瞞対策=MD-H-1 と同じ思想)。
    /// </summary>
    private void ShowFailedRestoreDialog(IReadOnlyList<string> failed)
    {
        const int Cap = 10;
        var shown = failed
            .Take(Cap)
            .Select(p => kxEdit.Core.Text.SanitizeForDisplay.OneLine(p, 200));
        var body = "以下のファイルを開けませんでした:\n\n  " + string.Join("\n  ", shown);
        if (failed.Count > Cap)
            body += $"\n  ... 他 {failed.Count - Cap} 件";
        body += "\n\nこれらは復元対象からはずしました。";
        MessageBox.Show(
            this,
            body,
            "一部のファイルを開けませんでした",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning
        );
    }

    /// <summary>
    /// A-1 第 2 層(設計 2026-08-22 §4.2): バックアップ取得後にディスク側が更新されていた
    /// ファイルを 1 個の警告にまとめて通知する。
    /// </summary>
    /// <remarks>
    /// バックアップを捨てて「ディスク版を優先」はしない。ディスクが新しい理由が kxEdit 自身の
    /// 保存(A-1)か他アプリの更新かを区別できず、捨てる実装は新しい無言喪失経路になるため。
    /// 表示規約(最大 10 件・<see cref="SanitizeForDisplay.OneLine"/>)は
    /// <see cref="ShowFailedRestoreDialog"/> と揃える。
    /// </remarks>
    /// <summary>M-11(設計 2026-09-02 §5.4): 起動時の設定警告。<b>文言は組み立て済みで渡ってくる</b>
    /// (<c>SettingsStartup.Prepare</c>)——パスの無害化も「切り詰めない」判断もそちらの担当なので、
    /// ここで加工しない。<b>本文をログ・クリップボード・例外へ流さないこと</b>(Task 8 の申し送り 2:
    /// 長さに上限が無い上、%APPDATA% 配下のパスを含む)。</summary>
    private void ShowSettingsStartupWarning(string body) =>
        MessageBox.Show(
            this,
            body,
            "設定を読み込めませんでした",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning
        );

    /// <summary>M-22(設計 2026-09-02 §4.3): 設定保存の差替失敗を伝える。
    /// <b>文言は組み立て済みで渡ってくる</b>(<see cref="SettingsSaveOutcome"/>)——
    /// パスの無害化も「tmp は切り詰めない」判断もそちらの担当なので、ここで加工しない。
    /// <b>本文をログ・クリップボード・例外へ流さないこと</b>(B4 Task 8 の申し送り 2 と同じ制約:
    /// 長さに上限が無い上、%APPDATA% 配下のパスを含む)。
    /// <para>
    /// 呼ばれるのは設定ダイアログを閉じた直後だけなので、ここで MessageBox を出しても編集は
    /// 止まらない。<b>到達の記録は抑止中でも行う</b>——MessageBox は blocking で実表示を
    /// 観測できないため、ここが「ダイアログが実際に発火したか」の唯一の観測面になる
    /// (記録を止めると <see cref="ApplySettings"/> の発火分岐が無網に戻る。実測で確認)。
    /// </para></summary>
    private void ShowSettingsSaveFailedDialog(string body)
    {
        _settingsSaveFailedDialogCountForTest++;
        _settingsSaveFailedDialogBodyForTest = body;
        if (_suppressSettingsSaveFailedDialogForTest)
            return;
        MessageBox.Show(
            this,
            body,
            "設定を保存できませんでした",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning
        );
    }

    private void ShowStaleBackupWarning(IReadOnlyList<string> paths)
    {
        const int Cap = 10;
        var shown = paths
            .Take(Cap)
            .Select(p => kxEdit.Core.Text.SanitizeForDisplay.OneLine(p, 200));
        var body =
            "次のファイルは、バックアップを取った後にディスク側が更新されています:\n\n  "
            + string.Join("\n  ", shown);
        if (paths.Count > Cap)
            body += $"\n  ... 他 {paths.Count - Cap} 件";
        body +=
            "\n\n復元したタブを上書き保存すると、ディスク上の新しい内容が失われます。"
            + "\n内容を確認してから保存してください。";
        MessageBox.Show(
            this,
            body,
            "復元した内容が古い可能性があります",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning
        );
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // 他ウィンドウから戻ったとき、SR 常駐環境ではフォーカスがメニューバー等タブ配下以外に
        // 残ったまま復元されないことがあり（実機事象）、WinForms の ActiveControl 復元では
        // 回復しない。タブ配下（エディタ／CSVシンク／F2ボックス／タブ列）に正当な保持者が
        // 居なければ編集領域へ戻して即編集可能にする。
        // BeginInvoke: 活性化時の WinForms 側フォーカス復元が済んだ後に判定するため。
        // ActiveForm 判定: 判定時点で既に別窓（自前のモーダル含む）へ移っていたら奪わない。
        // _menuActive 判定: 非アクティブ状態からメニュークリックで活性化した直後に
        // メニューを閉じてしまわないため。
        BeginInvoke(() =>
        {
            if (IsDisposed || ActiveForm != this || _menuActive)
                return;
            if (!_docs.TabHost.ContainsFocus)
                _docs.Active?.FocusTarget.Focus();
            // M-18(設計 2026-09-03 §3.4): フォーカスを戻した後に外部変更を見る。確認ダイアログが閉じると
            // OnActivated が再び来るが、Reloaded / Kept なら観測値が更新済みで NoChange で終わる。
            // ReloadFailed は意図的に再び聞く(CheckExternalChangeOnActive の doc)。
            // 上の ActiveForm != this ガードは検知の前に置くこと: 別のモーダル(保存直前の上書き確認・
            // A-10 の符号化確認)の message loop で BeginInvoke 済みの検知が走り、保存の途中に
            // 読み直しの確認が重なるのを防ぐ(FileController の再入ガードは「検知の中の検知」しか防がない)。
            CheckExternalChangeOnActive();
        });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // ===== 再入ガード(A-8 / H-1・設計 2026-08-24 §10.7) =====
        // STA スレッド上の**管理された**ブロッキング待機(下の WaitForFinalFlush →
        // WaitHandle.WaitOne)は CoWaitForMultipleHandles を経由するため、待っている間も
        // **SENT メッセージを配送する**。posted な WM_TIMER しか来ないという前提は誤りで、
        // WM_CLOSE(タスクマネージャーの「タスクの終了」)や WM_QUERYENDSESSION
        // (ログオフ/シャットダウンで CSRSS が送る)は SendMessage=配送されて
        // OnFormClosing を再入する。
        // 入れ子の close をそのまま走らせると、外側が待機で止まっている足元で
        // OnFormClosed → BackupCoordinator.Shutdown → Form.Dispose まで完走してしまい、
        // 外側が待機から戻ったときには
        //   - 外側の e.Cancel=true が無視される(Form は既に破棄済み)
        //   - 外側の確認ループの答えが全て捨てられる(No の MarkDiscarded は破棄済み writer へ
        //     落ち、末尾の FinalFlushForRestore は _shutDown で no-op)
        // となり、A-8 が直したはずの喪失が「ユーザーの回答ごと」再発する。
        // したがって入れ子側は**即座に取り消して**外側の close に決着させる。
        // 代償: WM_QUERYENDSESSION を veto すると Windows がシャットダウン阻止 UI を出しうるが、
        // 外側の待機は最長 BackupCoordinator.FinalFlushWait(5 秒)で終わり、その後は通常どおり
        // 閉じる=阻止は一時的。入れ子を勝たせて上記の喪失を招くより軽い。
        // grep の中断・前提ゲート・最終 flush・確認ループ・設定保存の**どれにも触れない**こと
        // (外側が実行中で、二重実行はそのまま二重の副作用になる)。
        if (_closeInProgress)
        {
            e.Cancel = true;
            base.OnFormClosing(e);
            return;
        }
        _closeInProgress = true;
        try
        {
            // 終了開始: 実行中の grep を中止し、終了確認中に結果窓が湧くのを抑止する。
            _grep.BeginClose();

            // hot exit(設計 §3.2/§10): ON かつ内容の定期退避が生きている(BackupEnabled)かつ
            // 全 dirty がバックアップ可能(≤32M chars)なら、未保存確認なしで閉じる。
            // BackupEnabled=false は「内容を永続化しない」ユーザー意思の尊重、32M 超は path-only
            // バックアップ(内容なし)による無断喪失の防止=いずれも従来の確認経路へ fall-through。
            bool silentPath =
                _settings.RestoreOpenFilesOnStartup
                && _settings.BackupEnabled
                && !HasOversizedDirtyDoc();
            _lastCloseFinalFlushOkForTest = null;
            // A-8: 末尾 flush(:末尾の RestoreOpenFilesOnStartup ブロック)を飛ばしてよいかは、
            // silentPath の単調性(true → false へしか遷移しない、という prose の約束)ではなく
            // 「flush を実際に走らせ、かつその後に文書の状態を変えていない」という事実に置く。
            // silentPath に依存させると、将来 silentPath=true を代入する経路が入ったり下の flush が
            // 別条件でくくられたときに「flush していないのに飛ばす」= silent close なのに本文も
            // レイアウトも一切書かれない(A-8 と同じ喪失クラス)へ倒れる。
            bool flushUpToDate = false;
            if (silentPath)
            {
                // A-8(設計 2026-08-24 §3): 前提ゲートだけでは「退避できる条件が揃っている」しか
                // 言えない。確認をスキップしてよいのは**実際に退避できたとき**だけなので、
                // ここで最終 flush を投入し完了を待って事後条件を検査する。
                // 投入した本文書込が 1 件でも失敗している / 完了を確認できない場合は、
                // hot exit の交換条件が成立していない=従来の未保存確認へ倒す。
                _backup.FinalFlushForRestore();
                flushUpToDate = true;
                bool flushOk = _backup.WaitForFinalFlush();
                _lastCloseFinalFlushOkForTest = flushOk;
                if (!flushOk)
                    silentPath = false;
            }
            _lastCloseTookSilentPathForTest = silentPath;

            if (!silentPath)
            {
                // 確認ループは保存(Modified=false 化)と破棄(MarkDiscarded)で文書の状態を変えるため、
                // 上で走らせた flush があってもレイアウト/本文は陳腐化する=末尾で必ず flush し直す。
                // A-8 のフォールバック(flushUpToDate=true で進入)でここを落とすと、実体の無い
                // BackupId を指したままの session-state.json が残る。
                flushUpToDate = false;

                // 従来経路: 全 dirty タブに Yes/No/Cancel 確認(all-or-nothing fall-through)。
                // どれかでキャンセルなら終了中止。
                var discarded = new List<Document>();
                foreach (var doc in _docs.Documents.ToArray())
                {
                    if (!doc.Editor.Modified)
                        continue;
                    _docs.Activate(doc); // どのファイルの確認かを SR/視覚で示す
                    bool keepClosing = _confirmDiscardOverrideForTest is not null
                        ? _confirmDiscardOverrideForTest(doc)
                        : _file.ConfirmDiscardIfDirty(doc);
                    if (!keepClosing)
                    {
                        e.Cancel = true;
                        _grep.CancelClose(); // 終了を取りやめたので grep を通常運用へ戻す
                        base.OnFormClosing(e);
                        return;
                    }
                    // keepClosing=true+Modified 維持=No(破棄)の明示選択(Yes は SaveDocument で
                    // Modified=false 化される)。破棄意図を hot exit の復元対象へ silent 復活させない
                    // (PR #22 M-1 後継)。確定は確認ループ完走後: 途中キャンセルでマークが残留すると、
                    // 以後その文書がバックアップ/レイアウト対象から永久に外れて silent 消失するため。
                    if (doc.Editor.Modified)
                        discarded.Add(doc);
                }
                foreach (var doc in discarded)
                    _backup.MarkDiscarded(doc); // OFF 経路でも冪等に無害(Shutdown が全削除する)
            }

            // ウィンドウサイズを設定に保存（最大化中は RestoreBounds を使う・M1 同様）。
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _settings.WindowWidth = b.Width;
            _settings.WindowHeight = b.Height;

            // ON: docs が生きているうちに最終 flush(本文+レイアウト)。OFF の stale layout 掃除は
            // OnFormClosed の Shutdown(keepForRestore:false) が担う。
            // A-8: 飛ばしてよいのは flushUpToDate=true のとき、すなわち「上の事後条件検査で flush 済み」
            // かつ「その後に文書の状態を変えていない」ときだけ(確認ループを通ったら false に戻る)。
            // 二重に走らせない理由は速度: ReconcileContent は dirty 文書ごとに SnapshotText
            // (全文 string 化)を走らせるため、巨大 dirty タブ同居時の終了が目に見えて遅くなる。
            if (_settings.RestoreOpenFilesOnStartup && !flushUpToDate)
                _backup.FinalFlushForRestore();

            _settings.LastSession = null; // 統合後は旧形式を書かない
            if (!_settings.RestoreOpenFilesOnStartup)
            {
                // レガシー残骸の掃除(設計 2026-07-23 統合 §8): OFF 恒久ユーザーは移行パス
                // (RestoreUnifiedSession)を一度も通らないため、ここで消さないと本文入りの
                // last-session-buffers.json が orphan として永久に残る。ON 側の掃除は
                // 起動時の移行パスが担う(復元消費とセットで削除)。
                kxEdit.Core.Session.LastSessionBuffersStore.Delete(LastSessionBuffersPath);
            }
            SaveSettingsSafe();
            base.OnFormClosing(e);
        }
        finally
        {
            // キャンセル経路(確認ループの早期 return / e.Cancel=true)でも必ず戻す。
            // 戻し漏れると、一度キャンセルした窓は以後永久に閉じられなくなる。
            _closeInProgress = false;
            // I-2 の続き: 終了を取りやめて編集へ戻るなら、上の終端フラッシュで抑止した健全性の
            // 報告を言い直す。抑止したまま戻ると、その drain で起きた遷移が誰にも伝わらないまま
            // 居座り、以後の tick も「遷移していない」ので無言になる
            // (= M-20 が潰した「無言の失敗」の再発)。閉じる側では言わない —— 助言が実行不能な
            // のは上の (a) のとおりで、終端の抑止理由がそのまま効いている。
            //
            // 条件は<b>抑止が実際に起きたか</b>(最終レビュー M-7)。「今 unhealthy か」で書くと、
            // 既に unhealthy な状態で終了 → キャンセルするたびに、何も飲み込んでいないのに
            // 1 回ずつ重ねて鳴る(終端の drain は遷移が無ければ報告しない)。
            if (e.Cancel && _suppressedBackupHealth is bool suppressed)
            {
                _suppressedBackupHealth = null; // 次のキャンセルで二重に鳴らさない
                SayBackupHealth(suppressed);
            }
        }
    }

    /// <summary>設計 §10: BK-M-3 の 32M cap を超える dirty 文書があるか(path-only バックアップは
    /// 内容を持たないため silent close 不可=確認経路へ fall-through する判定)。O(docs) の
    /// TextLength 参照のみで全文コピーはしない。</summary>
    private bool HasOversizedDirtyDoc() =>
        _docs.Documents.Any(doc => IsOversizedDirty(doc.Editor.Modified, doc.Editor.TextLength));

    /// <summary>判定の中核を純関数として切り出した seam: テストが 32M chars の実バッファを
    /// alloc せずに閾値境界(<see cref="BackupCoordinator.MaxBackupChars"/> 前後)を検証できる。</summary>
    internal static bool IsOversizedDirty(bool modified, int textLength) =>
        modified && textLength > BackupCoordinator.MaxBackupChars;

    internal bool HasOversizedDirtyDocForTest() => HasOversizedDirtyDoc();

    /// <summary>
    /// A-13: <see cref="DocumentManager.ClipboardFailed"/> に対する発声文言。
    /// 原因(他プロセスの保持)は同じだが、ユーザーがやろうとした操作が分かるよう
    /// 読み書きで文言を分ける(SR で聞き分けられる短文にする)。
    /// </summary>
    internal static string ClipboardFailureMessage(ClipboardFailureKind kind) =>
        kind == ClipboardFailureKind.Write
            ? "クリップボードにコピーできません。他のアプリが使用中の可能性があります"
            : "クリップボードから貼り付けられません。他のアプリが使用中の可能性があります";

    /// <summary>M-20: 健全性の 1 行を発声する。呼び出しは 3 つ(遷移そのもの / 設定適用の末尾で
    /// 言い直す経路 / 終了キャンセル後に言い直す経路)あり、<b>ここへ集めて</b>文言と到達の記録を
    /// 一対にする。
    /// <para><b>到達の記録が要る理由</b>: 発声先の <c>_announceLabel</c> は Form を閉じると
    /// ハンドルごと消え、<see cref="LastAnnouncementForTest"/> は<b>常に空文字列を返すようになる</b>
    /// (WinForms の <c>Control</c> は <c>CacheText</c> でない限りテキストをネイティブ窓側に置くため)。
    /// つまり「閉じ切る側では言わない」を閉じた後に検証する術が他に無く、記録を止めると
    /// <c>e.Cancel</c> の条件を落とす変異が生き残る(実測)。
    /// <see cref="ShowSettingsSaveFailedDialog"/> が MessageBox の発火を数えているのと同じ形。</para>
    /// <para>最終レビュー I-2 以降、この記録は<b>本番の判定にも使う</b>——
    /// <see cref="ApplySettings"/> が「直下の drain がこの呼出の中で実際に鳴らしたか」を
    /// <see cref="_backupHealthSaidCount"/> の差で見る。テスト観測専用ではない。</para></summary>
    private void SayBackupHealth(bool healthy)
    {
        _backupHealthSaidCount++;
        _lastBackupHealthSaid = healthy;
        _announcer.Say(BackupHealthMessage(healthy));
    }

    /// <summary>M-20(B5): バックアップ書込の健全性を伝える 1 行。
    /// <para><b>文言をここ 1 箇所に閉じる。</b> 言う場所は 3 つある(遷移そのもの / 設定適用の
    /// 末尾で言い直す経路 / 終了キャンセル後に言い直す経路)ので、配線ラムダの三項に埋めたままだと
    /// <b>同じ事実を別の強さで書く</b>温床になる。</para>
    /// <para>失敗側は 3 つに割ってある —— <b>起きた事実</b>(書込が失敗した)は断定し、
    /// <b>帰結</b>(復元できるか)は possibility に留め、<b>行動</b>(手で保存する)を添える。
    /// 帰結を断定できないのは、直前までの成功で取れた古いバックアップが残っていることがあり、
    /// かつ報告が届く時点でユーザーが既に保存を済ませていることもあるため
    /// (<c>SetSavePoint</c> は <c>ReconcileMapMaintenance</c> しか通らず drain しないので、
    /// 失敗の報告は次の tick までずれる)。M-22 の <see cref="SettingsSaveOutcome"/> と同じ規律。
    /// 「編集中の内容」ではなく<b>「未保存の内容」</b>と書くのはその 2 つ目のため —— 前者は
    /// 報告の瞬間に編集中の文書があることを前提にしてしまい、ずれて届いた場合に偽になる。</para></summary>
    private static string BackupHealthMessage(bool healthy) =>
        healthy
            ? "バックアップの保存を再開しました"
            : "バックアップを保存できませんでした。"
                + "未保存の内容は復元できない可能性があるため、ファイルを保存してください";

    /// <summary>
    /// M-1(設計 2026-08-29 §5): 未処理例外からの退避。
    /// </summary>
    /// <returns>true = 「次回起動で復元できる」と言い切れるときだけ。</returns>
    /// <remarks>
    /// <see cref="BackupCoordinator.FinalFlushForRestore"/> と
    /// <see cref="BackupCoordinator.WaitForFinalFlush"/> は<b>必ず対でこの順に</b>呼ぶ
    /// (<c>WaitForFinalFlush</c> の remarks を参照)。
    /// <b>UI スレッドから呼ぶこと</b>(<see cref="BackupCoordinator"/> は <c>_map</c> を
    /// 非スレッドセーフな Dictionary で持つ UI スレッド専有クラス)。
    /// <para>
    /// <b>前提ゲート</b>は hot exit の silent path と<b>意図的に違う</b>:
    /// <c>BackupEnabled</c> と「32M 超 dirty なし」だけを見て、
    /// <c>RestoreOpenFilesOnStartup</c> は<b>見ない</b>。OFF でも
    /// <see cref="OfferBackupRestoreOnStartup"/> が次回起動でバックアップからの復元を提案するので、
    /// OFF を理由に「退避できなかった可能性があります」と言うと、実際には復元できる構成に
    /// 嘘の悲観を出すことになる(Task 4 レビュー Major-3)。
    /// <c>BackupEnabled</c> は落とせない: OFF では <c>FinalFlushForRestore</c> が本文を書かないのに
    /// <c>WaitForFinalFlush</c> が「書くものが無い=失敗も無い」の true を返すため、
    /// <b>何も退避していないのに「復元できます」と表示する</b>嘘の安全宣言になる。
    /// 「32M 超 dirty なし」も落とせない: path-only バックアップからは本文が戻らない。
    /// </para>
    /// <para>
    /// ゲートは <c>&amp;&amp;</c> の短絡ではなく<b>構造ガード</b>で書く。
    /// <c>WaitForFinalFlush</c> は <c>BackupEnabled</c> OFF のとき残留失敗 Id を恒久的に
    /// 読み続ける契約(同 remarks)なので、<b>ゲートを通らない呼び出しを作らない</b>のが本質であり、
    /// 「見た目が等価な」リファクタ(先に結果を変数へ受ける)で静かに壊れる形にしない。
    /// </para>
    /// </remarks>
    internal bool FlushBackupsForCrash()
    {
        // A-8 / H-1(OnFormClosing の再入ガード)と同じ機構をここでも塞ぐ:
        // WaitForFinalFlush の管理された待機は SENT メッセージを配送するため、待っている間に
        // WM_CLOSE / WM_QUERYENDSESSION が届くと OnFormClosing が足元で完走し、
        // BackupCoordinator.Shutdown → Dispose まで進んでしまう。必ず Environment.Exit するので
        // 立てたら戻さない。
        _closeInProgress = true;

        bool canRestore = _settings.BackupEnabled && !HasOversizedDirtyDoc();
        _backup.FinalFlushForRestore();
        if (!canRestore)
            return false; // BackupEnabled OFF では WaitForFinalFlush を呼ばない(呼び出し規約)
        return _backup.WaitForFinalFlush();
    }

    /// <summary>テスト専用: 最後に <c>IAnnouncer.Say</c> した文言
    /// (<c>UiaAnnouncer</c> は視覚表示を無条件で行うため通知ラベルの文言と一致する)。</summary>
    internal string LastAnnouncementForTest => _announceLabel.Text;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // 閉じが確定した後にバックアップを停止する（OnFormClosing 後に取消される余地を残さない）。
        // hot exit(設計 §3.2): ON はバックアップと session-state.json を次回起動の統合復元用に残し、
        // OFF は従来どおり当セッション管理分を削除して孤児(=前回異常終了の印)を残さない。
        _backup.Shutdown(keepForRestore: _settings.RestoreOpenFilesOnStartup);
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        // 異常系（OnFormClosed 未経由）でも Timer/背景スレッドを確実に解放する。Shutdown 済みなら冪等で無害。
        // Sub 3.4-B(CA1001): _docs(DocumentManager) と _csv(CsvController) が IDisposable 化されたが、
        // _docs は TabHost 経由で本 Form.Controls ツリーに接続済みのため base.Dispose(disposing) で
        // _tabs → TabPages → EditorControl まで解放される(=DocumentManager.Dispose を明示呼び出しても
        // 冪等で無害だが、既存の解放経路を尊重して二重呼び出しを増やさない)。
        // _csv は Form の Controls ツリーに載らないため明示 Dispose する(CsvCellEditor 内 TextBox の
        // リーク防止=編集中に強制終了する異常系のセーフティ)。
        // _docs?.Dispose() は現状 no-op(内部 field は全て Control で base.Dispose が回収する)だが、
        // 将来 DocumentManager が non-Control disposable を保持した際の silent leak 防止で明示呼び出し。
        // Dispose は冪等契約のため二重呼び出しでも無害。
        if (disposing)
        {
            _backup?.Dispose();
            _csv?.Dispose();
            _docs?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ==================== キー操作（タブ切替・クローズ） ====================

    // Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+1..9 は子の EditorControl に食われないよう
    // フォームの ProcessCmdKey で横取りする。Ctrl+W はメニューのショートカットで処理。
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // CSVモードのアクティブタブのみ、素のキーをグリッドナビ用に横取りする。
        // F2 編集オーバーレイ表示中（_csv.IsEditing）は素通しし、TextBox に通常編集させる。
        // P7 で CsvFocusSink を撤去し FocusTarget=Editor 固定になったため、
        // 横取り条件は Editor へのフォーカス保持のみで判定する。タブ列（Ctrl+Tab でフォーカスが移る）
        // に居るときは矢印/Home/End 等をタブ操作へ通す。メニューがアクティブ（Alt 等）な間は
        // 横取りせず、矢印/文字キーをメニュー操作へ通す。
        var activeDoc = _docs.Active;
        if (
            activeDoc?.State.CsvMode == true
            && !_csv.IsEditing
            && !_menuActive
            && activeDoc.Editor.ContainsFocus
            && CsvCommands.ByKey.TryGetValue(keyData, out var csvCmd)
        )
        {
            csvCmd(_csv);
            return true;
        }

        switch (keyData)
        {
            case Keys.Control | Keys.Tab:
                _docs.SelectNext(+1);
                return true;
            case Keys.Control | Keys.Shift | Keys.Tab:
                _docs.SelectNext(-1);
                return true;
            case Keys.F3:
                _search.FindNext();
                return true;
            case Keys.Shift | Keys.F3:
                _search.FindPrev();
                return true;
            case Keys.Control | Keys.Alt | Keys.P:
                AnnouncePosition();
                return true;
            case Keys.Control | Keys.G:
                GoToLine();
                return true;
            case Keys.Insert:
                ToggleOvertype();
                return true;
        }
        if ((keyData & (Keys.Control | Keys.Alt | Keys.Shift)) == Keys.Control)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k >= Keys.D1 && k <= Keys.D9)
            {
                if (k == Keys.D9)
                    _docs.SelectAt(_docs.Count - 1); // 9 = 最後のタブ
                else
                    _docs.SelectAt(k - Keys.D1);
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ==================== メニュー / ステータス ====================

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();
        // メニューモード中は CSV の素キー横取りを止める（ProcessCmdKey の _menuActive ガード参照）。
        menu.MenuActivate += (_, _) => _menuActive = true;
        menu.MenuDeactivate += (_, _) => _menuActive = false;

        var file = new ToolStripMenuItem("ファイル(&F)");
        AddMenuItem(file, "新規(&N)", (_, _) => _file.NewFile(), Keys.Control | Keys.N);
        AddMenuItem(
            file,
            "開く(&O)...",
            (_, _) => _file.OpenFileWithDialog(),
            Keys.Control | Keys.O
        );
        AddMenuItem(
            file,
            "文字コードを指定して開き直す(&R)...",
            (_, _) => _file.ReopenWithEncoding()
        );
        _recentMenu = new ToolStripMenuItem("最近のファイル(&Y)");
        file.DropDownItems.Add(_recentMenu);
        file.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(file, "上書き保存(&S)", (_, _) => _file.Save(), Keys.Control | Keys.S);
        AddMenuItem(
            file,
            "名前を付けて保存(&A)...",
            (_, _) => _file.SaveAs(),
            Keys.Control | Keys.Shift | Keys.S
        );
        file.DropDownItems.Add(new ToolStripSeparator());
        // ショートカットは割り当てない(Alt→F→I で到達・設計 2026-07-25 §0)。
        AddMenuItem(file, "文書情報(&I)", (_, _) => _documentInfo.Show(this));
        AddMenuItem(file, "タブを閉じる(&W)", (_, _) => CloseActiveTab(), Keys.Control | Keys.W);
        AddMenuItem(file, "終了(&X)", (_, _) => Close());
        RebuildRecentMenu();

        var edit = new ToolStripMenuItem("編集(&E)");
        AddMenuItem(
            edit,
            "元に戻す(&U)",
            (_, _) => _docs.Active?.Editor.Undo(),
            Keys.Control | Keys.Z
        );
        AddMenuItem(
            edit,
            "やり直し(&R)",
            (_, _) => _docs.Active?.Editor.Redo(),
            Keys.Control | Keys.Y
        );
        edit.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(
            edit,
            "切り取り(&T)",
            (_, _) => _docs.Active?.Editor.Cut(),
            Keys.Control | Keys.X
        );
        AddMenuItem(
            edit,
            "コピー(&C)",
            (_, _) => _docs.Active?.Editor.Copy(),
            Keys.Control | Keys.C
        );
        AddMenuItem(
            edit,
            "貼り付け(&P)",
            (_, _) => _docs.Active?.Editor.Paste(),
            Keys.Control | Keys.V
        );
        AddMenuItem(
            edit,
            "すべて選択(&A)",
            (_, _) => _docs.Active?.Editor.SelectAll(),
            Keys.Control | Keys.A
        );
        edit.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(
            edit,
            "折り返し整形（禁則処理）(&K)",
            (_, _) => FormatWithKinsoku(),
            Keys.Control | Keys.Shift | Keys.J
        );

        // 検索系（旧「編集」メニューから分離。挙動・ショートカットは不変）。
        var search = new ToolStripMenuItem("検索(&S)");
        AddMenuItem(search, "検索(&F)...", (_, _) => _search.OpenFind(), Keys.Control | Keys.F);
        AddMenuItem(search, "置換(&H)...", (_, _) => _search.OpenReplace(), Keys.Control | Keys.H);
        // F3/Shift+F3 は ProcessCmdKey で処理するため、メニューは表示専用（ShortcutKeys 未登録）にして二重発火を避ける。
        var findNext = new ToolStripMenuItem("次を検索(&N)", null, (_, _) => _search.FindNext())
        {
            ShortcutKeyDisplayString = "F3",
        };
        var findPrev = new ToolStripMenuItem("前を検索(&B)", null, (_, _) => _search.FindPrev())
        {
            ShortcutKeyDisplayString = "Shift+F3",
        };
        search.DropDownItems.Add(findNext);
        search.DropDownItems.Add(findPrev);
        search.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(
            search,
            "フォルダ検索(grep)(&G)...",
            (_, _) => _grep.Open(),
            Keys.Control | Keys.Shift | Keys.F
        );

        // 読み上げ（SR 照会）。キーは ProcessCmdKey で処理し、ここは表示のみ（二重発火回避・M3 同方式）。
        var read = new ToolStripMenuItem("読み上げ(&R)");
        read.DropDownItems.Add(
            new ToolStripMenuItem("現在位置(&P)", null, (_, _) => AnnouncePosition())
            {
                ShortcutKeyDisplayString = "Ctrl+Alt+P",
            }
        );
        read.DropDownItems.Add(
            new ToolStripMenuItem("行へ移動(&G)...", null, (_, _) => GoToLine())
            {
                ShortcutKeyDisplayString = "Ctrl+G",
            }
        );

        // モード（マークダウンプレビュー / CSVモード）。CSV 操作系はメニューに出さず
        // キー専用（CsvCommands・キー一覧は将来のヘルプに記載する）。
        var mode = new ToolStripMenuItem("モード(&M)");
        var mdPreview = new ToolStripMenuItem(
            "マークダウンプレビュー(&P)",
            null,
            (_, _) => ShowMarkdownPreview()
        );
        mode.DropDownItems.Add(mdPreview);
        mode.DropDownItems.Add(new ToolStripSeparator());
        var csvToggle = new ToolStripMenuItem("CSVモード(&C)", null, (_, _) => _csv.ToggleMode());
        mode.DropDownItems.Add(csvToggle);
        // 開く度に活性状態を更新（プレビューはアクティブタブがあれば拡張子を問わず有効、
        // CSVトグルは現在のモードを Checked で表示）。
        mode.DropDownOpening += (_, _) =>
        {
            mdPreview.Enabled = _docs.Active is not null;
            csvToggle.Checked = _docs.Active?.State.CsvMode == true;
        };

        var options = new ToolStripMenuItem("オプション(&O)");
        AddMenuItem(options, "設定(&P)...", (_, _) => OpenSettings());

        var help = new ToolStripMenuItem("ヘルプ(&H)");
        help.DropDownItems.Add(
            "バージョン情報(&A)",
            null,
            (_, _) =>
                MessageBox.Show(
                    AppVersion.DisplayText,
                    "バージョン情報",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                )
        );

        menu.Items.AddRange(file, edit, search, read, mode, options, help);
        return menu;
    }

    /// <summary>ドロップダウンに ToolStripMenuItem を追加し、任意でショートカットキーを設定する。</summary>
    private static void AddMenuItem(
        ToolStripMenuItem parent,
        string text,
        EventHandler onClick,
        Keys shortcut = Keys.None
    )
    {
        var item = new ToolStripMenuItem(text, null, onClick);
        if (shortcut != Keys.None)
            item.ShortcutKeys = shortcut;
        parent.DropDownItems.Add(item);
    }

    private StatusStrip BuildStatusBar()
    {
        var strip = new StatusStrip();
        _posLabel.Spring = true;
        _posLabel.TextAlign = ContentAlignment.MiddleLeft;
        strip.Items.AddRange(_posLabel, _encLabel, _eolLabel);
        return strip;
    }

    private void UpdateStatus()
    {
        var doc = _docs.Active;
        if (doc is null)
            return;
        int line = doc.Editor.CurrentLine + 1;
        int col = doc.Editor.GetColumn(doc.Editor.CurrentPosition) + 1;
        _posLabel.Text = $"行 {line}, 桁 {col}";
        _encLabel.Text = EncodingDisplayName(doc.State.Encoding, doc.State.HasBom);
        _eolLabel.Text = doc.State.LineEnding.ToDisplayString();
    }

    private void UpdateTitle()
    {
        var doc = _docs.Active;
        Text = doc is null
            ? "kxEdit"
            : $"{(doc.Editor.Modified ? "* " : "")}{doc.State.DisplayName} - kxEdit";
    }

    private static string EncodingDisplayName(System.Text.Encoding enc, bool bom)
    {
        // 表示名は Core（EncodingCatalog）に集約。BOM 表記のみ App 側で付与する（UTF-8 のみ）。
        string name = EncodingCatalog.DisplayName(enc.CodePage);
        return enc.CodePage == 65001 && bom ? name + " (BOM)" : name;
    }

    // ==================== 最近のファイル / 設定（M7） ====================

    /// <summary>設定を永続化し、失敗した例外を返す(成功なら null)。
    /// M-22(B5・設計 2026-09-02 §4.2): 握り潰しをここで止め、<b>伝えるかどうかは呼び出し側に
    /// 決めさせる</b>。3 つの呼出のうち発声を伴うのは <see cref="ApplySettings"/> だけで、
    /// 残る 2 つ(<c>OnFormClosing</c> / <see cref="FileController"/> の最近ファイル更新)は
    /// 設計 §8 の判断により現行どおり握る(= <see cref="SaveSettingsSafe"/> を通る)。
    /// <para>
    /// B5(設計 §6.3 = B4 申し送りの回収): 読み取れなかった設定を上書きする直前の退避も
    /// <b>ここ</b>で行う。3 つの呼出すべてに効かせるためで、<see cref="ApplySettings"/> だけに
    /// 置くと<b>設定ダイアログを一度も開かないユーザーには効かない</b> ——
    /// 上書きは <c>OnFormClosing</c>(終了のたび)と <c>FileController.RegisterRecent</c>
    /// (ファイルを開く・保存するたび)からも来る。
    /// </para></summary>
    private Exception? TrySaveSettings()
    {
        // 起動時の警告は「先に次のファイルをコピーしてください」と案内するが、ユーザーが対処する
        // 前に上書きが走る = 案内した当のファイルが消える。ここで先回りして退避する。
        // 退避の失敗で保存を止めない(B4 §5.5)。止めると「設定を適用しました」が虚偽になり、
        // M-22 で潰した欠陥をここで新設することになる。
        //
        // フラグが表すのは「まだ試していない」ではなく<b>「原本がまだ在る」</b>である
        // (仕様レビュー I-1)。試行の前に落とすと、一過性ロックの場面で belt が丸ごと消える ——
        // ロック中の保存で退避も保存も落ち、ロックが外れた次の保存が .bak を残さずに原本を消す。
        // 落とすのは①退避が成功したとき ②保存が成功したとき(= 原本はもう無い)の 2 つで、
        // どちらも起きなければ armed のまま次の保存へ持ち越す。
        //
        // 残余(多重起動): 2 つ目のインスタンスも Unreadable で起動していると、そちらで最初に
        // 通る保存が<先着が書き直した settings.json>を .bak へ落とし、先着が退避した本物の原本を
        // 上書きしうる。.bad と同じ「最新のコピーだけを残す」を採った結果で、窓は<b>両方が同じ
        // 起動時に読めなかった</b>ときだけ。逆(overwrite: false)にすると、過去の .bak が 1 つでも
        // 残っている限り退避が二度と効かなくなる。データ面だけでなく<b>文言の面でも</b>残余が
        // ある —— その .bak は「以前の設定」と案内された名前を持ちながら、中身は<b>以前の設定
        // ではない</b>(kxEdit 自身が書いた既定寄りの設定)。
        //
        // 文言の残余は<b>単一インスタンスでも到達する</b>(最終レビュー・脆弱性パス Minor 3):
        // 今セッションの退避が落ちたまま保存だけ通ると、過去のセッションが残した .bak
        // (中身が kxEdit 自身の書いた設定であることもある)がそのまま残り、ユーザーはそれを
        // 「今回退避された以前の設定」と読んで復元しうる。多重起動固有の窓ではない。
        if (_quarantineSettingsBeforeFirstSave)
            _quarantineSettingsBeforeFirstSave = !SettingsStore.TryQuarantineUnreadable(
                _settingsPath,
                out _
            );

        try
        {
            SettingsStore.Save(_settingsPath, _settings);
            // 保存が通った = 読み取れなかった原本はこの書込で失われた。以後の退避は
            // <b>kxEdit 自身が書いた設定</b>を .bak へ落とすだけになるので、ここで確実に落とす。
            _quarantineSettingsBeforeFirstSave = false;
            return null;
        }
        catch (Exception ex)
        {
            // catch-all のまま残す理由は SettingsStore.Load / TryQuarantineCorrupt と同じ ——
            // 握ってよい例外の前置列挙は原理的に漏れる(監査 §9 V-7)。ここは握らずに<b>返す</b>ので、
            // 「伝えるか黙るか」の判断はコード上に呼出側として残る。
            return ex;
        }
    }

    /// <summary>設定を永続化する(保存失敗は致命でないため握る)。
    /// <see cref="FileController"/> へ <c>Action</c> として渡るため戻り値を持てない経路
    /// (最近使ったファイル一覧の更新)と、終了時の保存が使う。失敗を<b>伝える</b>のは
    /// <see cref="ApplySettings"/> だけ(設計 2026-09-02 §8)。</summary>
    private void SaveSettingsSafe() => _ = TrySaveSettings();

    private void RebuildRecentMenu()
    {
        // 旧項目を解放（差し替え毎のリーク防止）。Clear 後に Dispose してコレクション変更との競合を避ける。
        var olds = new ToolStripItem[_recentMenu.DropDownItems.Count];
        _recentMenu.DropDownItems.CopyTo(olds, 0);
        _recentMenu.DropDownItems.Clear();
        foreach (var o in olds)
            o.Dispose();

        if (_settings.RecentFiles.Count == 0)
        {
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem("(なし)") { Enabled = false });
            return;
        }
        int n = 0;
        foreach (string path in _settings.RecentFiles)
        {
            string p = path; // クロージャ捕捉
            n++;
            string body = (
                $"{System.IO.Path.GetFileName(p)}  〔{System.IO.Path.GetDirectoryName(p)}〕"
            ).Replace("&", "&&");
            // 1..9 は &1..&9、10 件目は &0 をアクセスキーに（不揃いを避ける）。
            string text =
                n <= 9 ? $"&{n} {body}"
                : n == 10 ? $"&0 {body}"
                : body;
            _recentMenu.DropDownItems.Add(
                new ToolStripMenuItem(text, null, (_, _) => _file.TryOpenOrActivate(p))
            );
        }
    }

    /// <summary>設定ダイアログを開き、OK なら <see cref="ApplySettings"/> へ渡す。
    /// 項目→コントロールの対応はダイアログに閉じ、ここは Result を渡すだけにする。
    /// <para>
    /// <b>ダイアログを開くこと以外はここに置かない</b>(M-22)—— <c>ShowDialog</c> はモーダルで
    /// 自動テストから叩けないため、判断をここへ残すと配線が黙って切れても緑のままになる
    /// (<see cref="Program.CreateMainForm"/> を <c>Main</c> から切り出したのと同じ理由)。
    /// </para></summary>
    private void OpenSettings()
    {
        using var dlg = new SettingsDialog(_settings);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        ApplySettings(dlg.Result); // Result は取得のたびに組み立てるため一度だけ読む
    }

    /// <summary>設定ダイアログ OK 後の反映本体。全タブへ外観適用＋バックアップ設定の即時反映＋
    /// 永続化を行い、<b>永続化の成否を見て</b>発声を選ぶ(M-22・設計 2026-09-02 §4)。
    /// 発声の時点で外観適用と <c>UpdateSettings</c> は済んでいる = 走っているアプリには効いている
    /// ので、失敗時も「適用しました」は残し、落ちた永続化の方を足す。
    /// <para>
    /// <b>発声チャネルには 1 行しか載らない</b>(<c>UiaAnnouncer</c> は 50 ms 窓 +
    /// <c>AutomationNotificationProcessing.MostRecent</c> なので、続けて <c>Say</c> すると SR が
    /// 1 つ目を読み終える前に 2 つ目が置き換える)。ところが直上の <c>UpdateSettings</c> は
    /// <c>Reconcile</c> → drain を走らせ、その中でバックアップ健全性が報告されうる
    /// (仕様レビュー I-1)。<b>1 本のチャネルに 2 つの事実は載らない</b>ので、
    /// ここでは<b>チャネルを分ける</b>(最終レビュー I-2):
    /// <list type="bullet">
    /// <item><b>発声</b>は健全性の報告に譲る。より緊急で(未保存の内容が復元できないかもしれない)、
    /// より行動可能(ファイルを保存する)なため。</item>
    /// <item><b>設定保存の失敗</b>はダイアログで届ける。<b>通常失敗にはダイアログを出さない</b>
    /// という現行の判断(<see cref="SettingsSaveOutcome"/> の xmldoc)に対する、ここだけの
    /// 原則的な例外である —— 例外の根拠は「案内すべきパスがある」ことではなく
    /// <b>発声チャネルが埋まっていること</b>。</item>
    /// </list>
    /// 併記(1 つの発声にまとめる)を採らなかったのは長さ —— 既存の発声は 44〜53 字で、
    /// つなぐと約 100 字になる。<c>_announceLabel</c> は高さ 22px・<c>AutoSize=false</c> の
    /// 1 行ラベルなので、視覚側(晴眼・弱視も第一級)では末尾が黙って切れる。
    /// </para>
    /// <para>
    /// 犠牲にするのは<b>成功の定型文だけ</b>(「設定を適用しました」)。失敗の事実は
    /// どの分岐でもいずれかのチャネルで必ず届く。
    /// </para></summary>
    private void ApplySettings(AppSettings result)
    {
        _settings = result;
        foreach (var doc in _docs.Documents)
            EditorAppearance.Apply(doc.Editor, _settings);
        // 健全性の報告が「この呼出の中で」鳴ったかを<b>事象として</b>捉える(最終レビュー I-2)。
        // 「今 unhealthy か」という状態で見ると、3 tick 前に鳴らして誰も上書きしていない警告まで
        // 設定 OK のたびに言い直す。drain を走らせうるのは直下の UpdateSettings だけ
        // (EditorAppearance.Apply も TrySaveSettings も Coordinator を触らない)。
        int healthSaidBefore = _backupHealthSaidCount;
        _backup.UpdateSettings(
            _settings.BackupEnabled,
            _settings.BackupIntervalSeconds,
            _settings.RestoreOpenFilesOnStartup
        );
        bool healthJustAnnounced = _backupHealthSaidCount != healthSaidBefore;

        var saveError = TrySaveSettings();
        var (speech, dialogBody) = SettingsSaveOutcome(saveError);
        if (healthJustAnnounced)
        {
            // 発声は健全性の報告に譲る(上書きしない)。失敗していたらダイアログへ回す。
            if (saveError is not null)
                dialogBody ??= SettingsSaveFailedWithoutSpeechBody;
        }
        else
        {
            _announcer.Say(speech);
        }

        if (dialogBody is not null)
        {
            ShowSettingsSaveFailedDialog(dialogBody);
            // モーダルは直前の通知に割り込む(SR は先にダイアログを読む)。閉じた後に言い直して、
            // 譲ったはずの発声がダイアログの陰で失われないようにする。
            if (healthJustAnnounced)
                SayBackupHealth(_lastBackupHealthSaid);
        }
    }

    /// <summary>テスト専用: 設定ダイアログを閉じた<b>後</b>の経路だけを叩く
    /// (<c>SettingsDialog.ShowDialog</c> はモーダルで自動テストから開けない)。</summary>
    internal void ApplySettingsForTest(AppSettings result) => ApplySettings(result);

    /// <summary>最終レビュー I-2: 発声チャネルを健全性の報告へ譲ったときに、設定保存の失敗を
    /// 届けるダイアログ本文(通常失敗=<see cref="SettingsSaveOutcome"/> が本文を持たない枝で使う)。
    /// <para>
    /// <b>発声の 1 行と同じ事実を、同じ強さで書く。</b>「今の kxEdit には効いている」と
    /// 「次回まで残るかは<b>可能性</b>」の 2 点だけで、やり直しの手順は足さない ——
    /// 通常失敗の原因は外(読み取り専用・ディスクフル・他プロセスのロック)にあるので、
    /// もう一度 OK すれば直る、と読める案内は<b>断定できないことを断定する</b>ことになる
    /// (原本が失われた枝の <c>LostOriginal</c> がやり直しを案内できるのは、その枝では次の保存が
    /// <c>File.Move</c> になるぶん成功しやすいという根拠があるため)。
    /// </para>
    /// <para>
    /// 冒頭でキャプション(<see cref="ShowSettingsSaveFailedDialog"/> の
    /// 「設定を保存できませんでした」)を逐語で繰り返さない —— SR はキャプション → 本文の順に
    /// 読むので、同じ一文を 2 回聞くことになる(最終レビュー M-1)。
    /// </para></summary>
    private const string SettingsSaveFailedWithoutSpeechBody =
        "設定は今の kxEdit には適用されていますが、設定ファイルに書き込めませんでした。"
        + "この設定は次回起動時に残らない可能性があります。";

    /// <summary>M-22: 設定保存の結果から「発声する 1 行」と「出すならダイアログ本文」を決める。
    /// <para>
    /// <b>失敗時も「適用しました」を残す。</b> 呼出時点で外観適用と <c>UpdateSettings</c> は
    /// 済んでおり、走っているアプリには設定が効いている。「適用できませんでした」は
    /// <b>逆向きの嘘</b>になる。欠けているのは「この設定が次回まで残るか」の方。
    /// </para>
    /// <para>
    /// <b>次回起動時の状態を断定しない</b>(仕様レビュー I-2)。ここから到達しうる結末は 3 通りで、
    /// どれも起こりうる:
    /// <list type="bullet">
    /// <item><b>新しい設定が残る</b> —— 同一セッションの他の保存経路
    /// (<c>FileController.RegisterRecent</c> の最近ファイル更新・<c>OnFormClosing</c>)は
    /// <see cref="ApplySettings"/> が差し替えた<b>新しい</b> <see cref="AppSettings"/> を書くので、
    /// 失敗が一過性なら、あるいはユーザーが案内を聞いて読み取り専用属性を外したら、
    /// ファイルを 1 つ開くか終了するだけで新設定が永続化される。
    /// <b>案内を聞いたユーザーが最も自然に取る行動が、断定をそのまま偽にする。</b></item>
    /// <item><b>元の設定に戻る</b> —— 通常の失敗(原本は無傷)でこのまま何も保存されなかった場合。</item>
    /// <item><b>既定値で始まる</b> —— <see cref="AtomicReplaceFailedException"/> の分岐。原本が
    /// 失われているので、次回の <see cref="SettingsStore.Load"/> は <c>File.Exists</c> が false =
    /// <c>Missing</c> となり、戻るのは「元の設定」ではなく<b>既定値</b>である(実コードで確認)。</item>
    /// </list>
    /// したがって発声は possibility に留め、ダイアログ側は<b>原本が失われた分岐でしか出ない</b>という
    /// 事実を使って既定値の側だけを述べる。
    /// </para>
    /// <para>
    /// <b>本メソッドが</b>ダイアログ本文を返すのは <see cref="AtomicReplaceFailedException"/> の
    /// ときだけ。通常の失敗(ディスクフル・ACL)は tmp が残らず案内すべきパスが無いので、
    /// 発声だけで完結する。tmp が残る場合は <c>%APPDATA%\kxEdit\</c> <b>直下に恒久残留し、
    /// 中身は最近使ったファイルの一覧(パス)を含む</b>(B4 の実測。
    /// <see cref="SettingsStore.Save"/> の xmldoc)ため、場所と後始末を届ける価値がある。
    /// 1 行のステータスラベルに長いパスは載らないので二段にする。
    /// </para>
    /// <para>
    /// <b>ただし「通常失敗にはダイアログを出さない」には呼出側に 1 つだけ例外がある</b> ——
    /// <see cref="ApplySettings"/> は発声チャネルをバックアップ健全性の報告へ譲る場合に限り、
    /// 通常失敗でも <see cref="SettingsSaveFailedWithoutSpeechBody"/> をダイアログへ回す
    /// (最終レビュー I-2)。判断を呼出側に置くのは、<b>チャネルが埋まっているかを知っているのは
    /// 呼出側だけ</b>だからで、ここは失敗の中身だけを見る純関数のまま保つ。
    /// </para>
    /// <para>
    /// 実在確認は <c>File.Exists</c> 一本。復旧リネームが「tmp まで失われていた」で落ちた場合も
    /// 同じ例外型になるため、例外の型で分けると原理的に漏れる(監査 §9 V-7)。
    /// </para></summary>
    private static (string Speech, string? DialogBody) SettingsSaveOutcome(Exception? error)
    {
        if (error is null)
            return ("設定を適用しました", null);

        // 発声は 1 行のステータスラベル。3 通りの結末(上の xmldoc)のどれとも矛盾しないよう、
        // 「保存できなかった」という<b>起きた事実</b>だけを断定し、次回起動時の状態は possibility に
        // 留める。ここを断定にすると、このブランチが潰そうとしている虚偽発声を自分で新設する。
        const string Speech =
            "設定を適用しましたが、保存できませんでした。この設定は次回起動時に残らない可能性があります";

        if (error is not AtomicReplaceFailedException replaceFailed)
            return (Speech, null);

        // ここから先は<b>原本が失われた分岐だけ</b>(AtomicFile が
        // AtomicReplaceFailedException を投げる条件そのもの)。通常失敗と違って原本が無いので、
        // 「元の設定に戻る」は書けない —— 何も保存されないまま次回起動すると既定値で始まる。
        // 逆に断定もしない: 後続の保存が成功すれば新設定が残る(むしろ destExists=false =
        // File.Move になるぶん成功しやすい)。両方を含む条件形にする。
        const string LostOriginal =
            "設定は今の kxEdit には適用されていますが、保存先が無くなったため、"
            + "このまま保存されなければ次回起動時は既定の設定で始まります。"
            + "設定をもう一度開いて OK すると、保存をやり直せます。";

        // 原本パスは丸めてよい(80)——ユーザーが設定ファイルの場所を知らなくても、退避先の
        // フォルダーは tmp パス側に完全な形で載る。逆に tmp パスは kxEdit がその場で作った
        // 乱数入りで他所から知る手段が無いので<b>切り詰めない</b>(FileController が確立した非対称)。
        // 無害化(OneLine)はどちらも外さない —— ダイアログ偽装を防ぐ既存のセキュリティ制御。
        //
        // 語順も SR を前提に決める(FileController の M-12 が確立した教訓): SR は線形に読むので、
        // 「何が起きたか」と「次に何をすればよいか」(= LostOriginal 末尾のやり直し手順)を
        // 長い退避先パスより<b>前</b>に置く。
        //
        // 冒頭でキャプション(ShowSettingsSaveFailedDialog の「設定を保存できませんでした」)を
        // 逐語で繰り返さない —— SR はキャプション → 本文の順に読むので同じ一文を 2 回聞く
        // (最終レビュー M-1)。本文が FileController の M-12(キャプションは汎用の「エラー」)に
        // 倣って書かれた名残で、専用キャプションを後から付けたときに重複が残っていた。
        string target = SanitizeForDisplay.OneLine(replaceFailed.TargetPath, 80);
        string body = System.IO.File.Exists(replaceFailed.PreservedTempPath)
            ? $"保存先 '{target}' が失われました。"
                + LostOriginal
                + "\n\n書き込んだ内容は次の場所に残してあります。不要になったら削除してください:\n  "
                + SanitizeForDisplay.OneLine(replaceFailed.PreservedTempPath)
            : $"保存先 '{target}' が失われ、書き込んだ内容も残せませんでした。" + LostOriginal;
        return (Speech, body);
    }

    /// <summary>テスト専用: <see cref="SettingsSaveOutcome"/> の判断だけを直接叩く。
    /// <see cref="AtomicReplaceFailedException"/> は「差替に失敗し<b>かつ原本が失われ</b>かつ
    /// 復旧リネームも失敗」でしか出ない(<see cref="AtomicFile"/> のクラス xmldoc)ため、
    /// <b>素の I/O では作れない</b>——例外を組み立ててここへ渡すのが、<c>File.Exists</c> の
    /// 2 分岐(退避先が実在する / 失われている)を並べて固定できる唯一の形である。
    /// <b>ここだけでは発火まで届かない</b>ので、配線の側は
    /// <c>AtomicFile.OverrideReplaceStepForTest</c> で差替段を偽装する網が別に要る
    /// (<c>MainFormSmokeTests.Applying_settings_shows_the_preserved_temp_dialog_...</c>)。</summary>
    internal static (string Speech, string? DialogBody) SettingsSaveOutcomeForTest(
        Exception? error
    ) => SettingsSaveOutcome(error);

    /// <summary>
    /// スモークテストの導線=Active 経由の TryOpenOrActivate/Save を Test から叩くため
    /// (MainForm 内では _file を直接使い、テスト側は FileForTest を通す)。
    /// </summary>
    internal FileController FileForTest => _file;

    /// <summary>テスト専用: CSV モードの手動切替(M-18 の読み直し後のモード復帰を検証する)。</summary>
    internal CsvController CsvForTest => _csv;

    /// <summary>
    /// M-18(設計 2026-09-03 §3.4 / §3.7): ウィンドウ復帰・タブ切替時の外部変更検知。
    /// 判定と確認は <see cref="FileController.CheckExternalChange"/>。ここは配線と、読み直した後の
    /// 発声・CSV モードの復帰(<c>LoadInto</c> が CsvMode を false に落とすため)だけを担う。
    /// F2 編集中は起こらない: ウィンドウを離れた時点で OnLostFocus → CancelEdit、タブ切替は
    /// BeforeActiveChange が AbortEdit する。
    /// 発声を TryEnterMode より先に出すのは、パース不能で入れなかったときの通知(TryEnterMode が出す)を
    /// 最後に残すため(1 行の発声チャネルは最後の 1 件が残る。B5 の教訓)。
    /// CSV モード中はキャレットがセルに追従しないため、セル位置は (row, col) で戻す
    /// (<see cref="CsvController.TryGoToCell"/>。設計 §3.7 の「キャレットから導出するので近い位置に戻る」は
    /// 偽だった: キャレット由来の TryEnterMode は先頭セルへ入る)。自動モード(<c>_openedFresh</c> =
    /// <see cref="AutoEnterCsvMode"/>)は読み直しの中では <see cref="_reloadingCsv"/> で飛ばす: 自動で入り
    /// 直させると発声が CSVモード オン … → 読み直しました の順になって手動モードと食い違い、本文も 2 度
    /// パースする(最終コード品質レビュー Q-1)。手動・自動のどちらでも発声は 読み直しました → CSVモード オン … →
    /// セル、の順で同期に連続し、UiaAnnouncer の 50 ms 窓により SR に届くのは 1 件目と最後
    /// (セルが無くなっていれば TryGoToCell は黙って false = 先頭セルの発声が残る)。
    /// <see cref="ExternalChangeOutcome.ReloadFailed"/> は両方の観測値が不変なので、エラーダイアログを
    /// 閉じた直後の OnActivated で同じ確認が再び出る(一過性ロックの再試行。「いいえ」で止まる)。
    /// </summary>
    private ExternalChangeOutcome CheckExternalChangeOnActive()
    {
        var doc = _docs.Active;
        if (doc is null)
            return ExternalChangeOutcome.Skipped;
        bool wasCsv = doc.State.CsvMode;
        int row = doc.State.CsvRow; // 読み直しで (0, 0) に戻るので先に捕捉する
        int col = doc.State.CsvCol;
        // 再入(確認ダイアログのモーダル中に届いた切替由来の検知。FileController 側は Skipped を返す)で
        // 外側の値を壊さないよう、上書きではなく退避して戻す。
        bool wasReloadingCsv = _reloadingCsv;
        _reloadingCsv = wasCsv;
        ExternalChangeOutcome outcome;
        try
        {
            outcome = _file.CheckExternalChange(doc);
        }
        finally
        {
            _reloadingCsv = wasReloadingCsv;
        }
        if (outcome != ExternalChangeOutcome.Reloaded)
            return outcome;
        _announcer.Say("読み直しました");
        if (!wasCsv)
            return outcome;
        // LoadInto が CsvMode を false に落とし、自動モードは飛ばしてあるので、ここで入り直す。
        // パース不能なら入らず TryEnterMode が発声する(最後に残る)。
        if (_csv.TryEnterMode(doc))
            _csv.TryGoToCell(doc, row, col);
        return outcome;
    }

    /// <summary>テスト専用: <see cref="CheckExternalChangeOnActive"/> を活性化イベント無しで叩く。</summary>
    internal ExternalChangeOutcome CheckExternalChangeOnActiveForTest() =>
        CheckExternalChangeOnActive();

    /// <summary>
    /// grep ジャンプ用: <paramref name="hit"/> のファイルを開き（既存タブがあれば再利用）、
    /// ヒット行を選択してエディタへフォーカスする(<see cref="GrepJumpKind.Stale"/> は選択せず
    /// 行頭へ寄せる)。
    /// <para>
    /// SR への通知経路は<b>一様ではない</b>ので、どれか 1 つを無条件の前提にしないこと。
    /// <see cref="EditorControl.SetSelectionCharRange"/> は無変化(=同じヒットへの再ジャンプ)だと
    /// 早期 return するため<b>setter からの</b> <c>RaiseSelectionChanged</c> は飛ばない。ただし
    /// 通常の導線ではフォーカスがエディタ<b>外</b>(<c>GrepResultsWindow</c> の一覧)から戻るので、
    /// 直後の <c>FocusTarget.Focus()</c> が <c>EditorControl.OnGotFocus</c> を起こし、そこで
    /// <c>RaiseFocusChanged</c> と <c>RaiseSelectionChanged</c> が<b>別途</b>発火する。
    /// 一方 CSV モードのタブでは <c>RaiseUiaSelectionEvents=false</c>(<c>CsvController</c>)なので
    /// <b>選択変化の</b> UIA 経路が無い。<b>フォーカス変化のほうは CSV モードでも飛ぶ</b>——
    /// <c>EditorControl.OnGotFocus</c> の <c>RaiseFocusChanged</c> は<b>無条件</b>で、このフラグが
    /// 抑えるのは <c>RaiseSelectionChanged</c> だけなので、ここも無条件の前提にはしない。
    /// (<c>suppressAutoCsv: true</c> は新規オープン時の自動遷移を抑えるだけで、
    /// <b>既に CSV モードのタブへ飛ぶ経路は塞いでいない</b>。)
    /// <b>いずれの場合も末尾の <c>_announcer.Say</c> は常に走る</b>=SR は着地行を必ず聞ける。
    /// 無変化のときに途切れるのは<b>視覚的な追従</b>だけで、それは本メソッドが明示的に呼ぶ
    /// <see cref="EditorControl.BringCaretIntoView"/>(設計書 §3.3・A-3 同型)が補う。
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>A-18(2026-08-31)</b>: 以前は <c>hit.AbsoluteOffset</c> をそのまま
    /// <see cref="EditorControl.SelectCharRange"/> に渡し、doc で「同じ復号経路を通るため
    /// エディタのスナップショットと同一空間に揃う」と<b>無条件の不変条件として宣言していた</b>。
    /// 実際には<b>揃う保証がない</b>(未保存編集のあるタブ・文字コード判定窓の割れ・grep 後の
    /// 外部変更でずれる。逆に、開いたまま未編集のタブや、ヒットより後ろだけを編集した場合は
    /// たまたま揃う)。ずれた位置に着地したうえで着地行を「N 行目」と発声するため、
    /// <b>SR ユーザーには検出できない嘘</b>になっていた。
    /// 現在は <see cref="GrepJumpResolver"/> が行番号+行内容を live バッファへ照合する。
    /// <b><c>AbsoluteOffset</c> をこの経路へ戻さないこと。</b>
    /// <para>
    /// 発声の行番号は <c>t.BufferLine</c> ではなく<b>着地後の</b> <see cref="EditorControl.CurrentLine"/>
    /// から読み戻す。resolver の意図値を読むと <c>SelectCharRange</c> 側のクランプ/スナップの
    /// 不具合が発声に現れなくなる(発声文言は第 2 の観測面)。
    /// </para>
    /// <para>
    /// <c>SearchController.SelectHit</c> が <c>ed.CurrentBuffer.Current</c> を<b>読み直さない</b>のと
    /// ここが<b>読み直す</b>のは、矛盾ではなく同じ原則(ヒットは、それを解決した空間と対で扱う)の
    /// 裏表。検索のヒットは手元の snap 上で見つけたのでその snap と対にする。grep のヒットは
    /// 出所がディスクなので、対にすべき空間は<b>ここで読む live バッファ</b>のほうになる。
    /// </para>
    /// </remarks>
    internal void OpenAndSelect(GrepHit hit)
    {
        var doc = _file.TryOpenOrActivate(hit.FilePath, suppressAutoCsv: true);
        if (doc is null)
            return;
        var t = GrepJumpResolver.Resolve(hit, doc.Editor.CurrentBuffer.Current);
        doc.Editor.SelectCharRange(t.BufferOffset, t.Length);
        // 設計書 §3.3(A-3 同型): SetSelectionCharRange は Anchor/Caret 無変化で早期 return し
        // BringCaretIntoView へ到達しない。ジャンプは「移動先を必ず見せる」操作なので、
        // 同じヒットへ再ジャンプしたとき(ホイールでスクロール退避 → 同じ行を再選択)にも
        // 追従するよう、ジャンプ導線の側で明示的に呼ぶ。
        doc.Editor.BringCaretIntoView();
        doc.FocusTarget.Focus();
        // ジャンプ先のファイル名と行を明示通知（選択移動の自動読みに加え、別ファイルへ飛んだ文脈を補う）。
        string where = $"{doc.State.DisplayName} {doc.Editor.CurrentLine + 1} 行目";
        _announcer.Say(t.Kind == GrepJumpKind.Stale ? $"{where} 内容が変わっています" : where);
    }

    // ==================== 読み上げ照会（SR 利便・M6） ====================

    /// <summary>現在位置（行/総行/桁）を読み上げる。
    /// 2026-07-25: 文字数と選択数は本メソッドから削除し、詳細は [ファイル]&gt;文書情報 へ集約した
    /// （位置照会=編集位置の指標・文書情報=文書全体の内容量の指標という棲み分け。設計 2026-07-25 §0）。</summary>
    private void AnnouncePosition()
    {
        var ed = _docs.Active?.Editor;
        if (ed is null)
            return;
        int line = ed.CurrentLine + 1;
        int totalLines = ed.LineCount;
        int column = ed.GetColumn(ed.CurrentPosition) + 1;
        _announcer.Say(PositionFormatter.Format(line, totalLines, column, ed.Overtype));
    }

    /// <summary>行番号を入力して移動する。</summary>
    private void GoToLine()
    {
        // CSVモード中は行ジャンプをセル指定に読み替える（Ctrl+G のキーボード経路と統一）。
        if (_docs.Active?.State.CsvMode == true)
        {
            _csv.GoToCell();
            return;
        }
        var ed = _docs.Active?.Editor;
        if (ed is null)
            return;
        int max = ed.LineCount;
        using var dlg = new GoToLineDialog(ed.CurrentLine + 1, max);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        int target = Math.Clamp(dlg.LineNumber, 1, max);
        ed.GoToLine(target - 1);
        ed.Focus();
        _announcer.Say($"行 {target}");
    }

    /// <summary>挿入/上書きモードをトグルし読み上げる（Insert キー）。</summary>
    private void ToggleOvertype()
    {
        var ed = _docs.Active?.Editor;
        if (ed is null)
            return;
        ed.Overtype = !ed.Overtype;
        _announcer.Say(ed.Overtype ? "上書きモード" : "挿入モード");
    }

    /// <summary>アクティブタブの編集中内容を WebView2 プレビューで表示する（拡張子は問わない）。</summary>
    /// <remarks>
    /// MD-L-3 L5 検証: 4M 文字超の .md を開いて Preview 起動 → エラーダイアログが出て
    /// プレビュー窓は開かないこと。MainForm には IUserPrompt が注入されていないため、
    /// <c>MarkdownPreviewForm.InitAsync</c> の catch と同様に MessageBox.Show を直接使う
    /// (行番号ではなくメソッド名で指す —— 旧記述の "MarkdownPreviewForm.cs:135" は
    /// 既に陳腐化していた)。
    /// M-23: cap 判定は TextLength で行い SnapshotText を呼ばない。
    /// B: Markdig のネスト深度上限超過 (MarkdownTooComplexException) も同様に提示する。
    /// </remarks>
    private void ShowMarkdownPreview()
    {
        var doc = _docs.Active;
        if (doc is null)
            return;

        // M-23: cap 超過は SnapshotText を呼ぶ前に弾く。全文 string 化してから Render 内で
        // 判定すると、1G 文字級の文書では string 化そのものが OutOfMemoryException になり
        // 未捕捉で落ちる。TextLength は材料化せずに文字数を返す。
        if (MarkdownRenderer.ExceedsMaxChars(doc.Editor.TextLength))
        {
            ShowPreviewTooLarge(MarkdownRenderer.TooLargeDetail(doc.Editor.TextLength));
            return;
        }

        string markdown = doc.Editor.SnapshotText; // 編集中バッファ（未保存も反映）
        string? dir = System.IO.Path.GetDirectoryName(doc.State.Path);
        string html;
        try
        {
            html = MarkdownRenderer.Render(markdown, MarkdownRenderer.PreviewBaseHref);
        }
        catch (DocumentTooLargeException ex)
        {
            // MD-L-3: 入力サイズ cap 超過時はユーザに提示してプレビュー窓は開かない。
            // M-23: 事前判定 (上の ExceedsMaxChars) と同じ提示を通るよう共通化した。
            ShowPreviewTooLarge(ex.Message);
            return;
        }
        catch (MarkdownTooComplexException ex)
        {
            // B: Markdig のネスト深度上限 (既定 128) 超過。"> " × 200 = 400 バイトで発火する
            // ので 4M 文字 cap のはるか下で起きる。翻訳前は未捕捉例外として
            // Application.ThreadException → CrashHandler → アプリ終了になっていた。
            // baseHref allow-list 違反 (MD-L-4) の ArgumentException はここへ来ない
            // (Render 側で型が分かれている) ので、実装バグは引き続き伝播する。
            ShowPreviewNotRenderable(ex.Message);
            return;
        }

        using var f = new MarkdownPreviewForm(
            html,
            dir,
            doc.State.DisplayName,
            new FileReachabilityProbe()
        );
        f.ShowDialog(this);
        _docs.Active?.FocusTarget.Focus(); // 戻り後は編集領域へフォーカス
    }

    /// <summary>
    /// M-23: プレビューが上限超過で開けないことをユーザへ提示する（提示後は編集領域へフォーカス）。
    /// <para>
    /// 呼び出し元は 2 つ: ① <see cref="ShowMarkdownPreview"/> の事前判定
    /// (<c>MarkdownRenderer.ExceedsMaxChars</c>) ② <c>MarkdownRenderer.Render</c> が投げる
    /// <see cref="DocumentTooLargeException"/> の catch。<b>両者で文面・タイトル・アイコン・
    /// フォーカス復帰を同一にする</b>ためにここへ切り出してある
    /// (detail はどちらも <c>MarkdownRenderer.TooLargeDetail</c> 由来)。
    /// </para>
    /// </summary>
    private void ShowPreviewTooLarge(string detail) =>
        ShowPreviewUnavailable("マークダウン本文が大きすぎます。", detail);

    /// <summary>
    /// B: プレビューが<b>構造の複雑さ</b>で開けないことをユーザへ提示する。
    /// 「大きすぎます」とは別文言にする —— 400 バイトの .md でも起きるので、
    /// サイズの話にすると原因を取り違えさせる (実際 <c>"&gt; " × 200</c> で発火する)。
    /// </summary>
    private void ShowPreviewNotRenderable(string detail) =>
        ShowPreviewUnavailable("マークダウンの構造が深すぎて表示できません。", detail);

    /// <summary>
    /// プレビューを開けなかったことの提示 (文面・タイトル・アイコン・フォーカス復帰を
    /// 全経路で同一にするための 1 箇所)。
    /// </summary>
    private void ShowPreviewUnavailable(string reason, string detail)
    {
        MessageBox.Show(
            this,
            $"プレビューを表示できません。{reason}\n\n詳細: {detail}",
            "プレビューを表示できません",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning
        );
        _docs.Active?.FocusTarget.Focus(); // 成功パスと対称: 戻り後は編集領域へフォーカス
    }

    /// <summary>選択範囲（無ければ全文）を WrapColumn 桁で禁則整形する（Stage 8 で <see cref="KinsokuFormatController"/> へ委譲）。
    /// AppSettings は OpenSettings で参照が差し替わるため Run 引数(呼び出し時解決)で渡す。</summary>
    private void FormatWithKinsoku() => _kinsoku.Run(_settings);

    /// <summary>アクティブタブを閉じる。変更確認→クローズ。最後の1つを閉じたらアプリ終了（Q1=B）。</summary>
    private void CloseActiveTab()
    {
        _csv.AbortEdit(); // F2 編集中ならタブ破棄前にオーバーレイを除去（IsEditing 固着防止）
        var doc = _docs.Active;
        if (doc is null)
            return;
        if (!_docs.TryClose(doc, _file.ConfirmDiscardIfDirty))
            return;
        if (_docs.Count == 0)
        {
            Close();
            return;
        }
        // 選択タブ削除時の TabControl.Selected 発火は WinForms の仕様上保証されないため、
        // クローズ後の新アクティブへフォーカス・タイトル・ステータスを明示更新する
        // （Selected 発火に依存しない唯一の更新源）。
        _docs.Active?.FocusTarget.Focus();
        UpdateTitle();
        UpdateStatus();
    }
}
