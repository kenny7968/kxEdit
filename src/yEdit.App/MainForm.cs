using yEdit.App.Settings;
using yEdit.App.Speech;
using yEdit.Core.Backup;
using yEdit.Core.Csv;
using yEdit.Core.Reading;
using yEdit.Core.Settings;
using yEdit.Core.Text;
using yEdit.Editor;

namespace yEdit.App;

public sealed partial class MainForm : Form
{
    private readonly DocumentManager _docs;
    private readonly FileController _file; // コンストラクタで生成
    private readonly SearchController _search; // コンストラクタで生成
    private readonly GrepController _grep; // コンストラクタで生成
    private readonly BackupCoordinator _backup; // コンストラクタで生成
    private readonly CsvController _csv; // コンストラクタで生成
    private readonly KinsokuFormatController _kinsoku; // コンストラクタで生成(FormatWithKinsoku を委譲)
    private bool _restoreOffered; // 起動時の復元提案を一度だけ行う
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
        _lastSessionBuffersPathOverride ?? yEdit.Core.Session.LastSessionBuffersStore.DefaultPath;

    internal void SetLastSessionBuffersPathForTest(string path) =>
        _lastSessionBuffersPathOverride = path;

    // 起動時の空無題タブ(ctor で作った 1 個)を覚え、復元成功時に閉じるための seam。
    // FileController.RestoreSession の initialEmpty 引数に渡す=前回タブが 1 つでも復元
    // できた時のみ破棄される契約(Task 5 review M-2)。ctor 末尾で 1 度だけ代入し以後不変。
    private readonly Document? _startupEmptyDoc;

    // Task 7 テスト用: FailedPaths の Warn ダイアログ (MessageBox.Show=blocking) をテストで抑止する seam。
    // 4 番目の smoke テスト(missing file → FailedPaths に載る)で MessageBox が UI スレッドを
    // 塞ぐのを避けるため。実運用経路では常に false=ダイアログは出る。
    // Form 派生上の bool プロパティは WFO1000 を誘発するため、field + setter method で seam を作る
    // (SetLastSessionBuffersPathForTest と同じ方式)。
    private bool _suppressFailedRestoreDialogForTest;

    internal void SetSuppressFailedRestoreDialogForTest(bool value) =>
        _suppressFailedRestoreDialogForTest = value;

    // Task 13 テスト用: OnFormClosing が silent path(§8.2 fast-path)を通ったかを観測する。
    // null = OnFormClosing 未実行 / true = silent (ConfirmDiscardIfDirty loop skip) / false = fall-through。
    private bool? _lastCloseTookSilentPathForTest;
    internal bool? LastCloseTookSilentPathForTest => _lastCloseTookSilentPathForTest;

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
    /// </summary>
    internal MainForm(
        AppSettings settings,
        string settingsPath,
        string? backupDirectory = null,
        string? sessionLayoutPath = null
    )
    {
        _settingsPath = settingsPath;
        _settings = settings; // Program.Main が読込済み

        Text = "yEdit";
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
        };
        _docs.KeyBasedSwitch += (_, doc) => _announcer.Say(doc.TabLabel);
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
            prompt: new MessageBoxUserPrompt(),
            fileDialogs: new WinFormsFileDialogService(),
            reachabilityProbe: new FileReachabilityProbe()
        );
        _search = new SearchController(_docs, this, _announcer, cb => new FindReplaceDialog(cb));
        _grep = new GrepController(
            docs: _docs,
            owner: this,
            // Batch D Task 12: GrepDialog は new UiaAnnouncer(_status) の直生成を廃止し
            // 共有 _announcer(SearchController と同型経路)を注入する。
            viewFactory: cb => new GrepDialog(cb, _announcer),
            resultsFactory: () =>
                new GrepResultsWindow(
                    new GrepResultsCallbacks(hit =>
                        OpenAndSelect(hit.FilePath, hit.AbsoluteOffset, hit.MatchLength)
                    )
                )
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
        _csv = new CsvController(
            docs: _docs,
            announcer: _announcer,
            cellPicker: new WinFormsCellPicker()
        );
        _kinsoku = new KinsokuFormatController(_docs, _announcer);
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

        if (_settings.RestoreOpenFilesOnStartup)
        {
            // hot exit 統合復元(設計 §3.3): クラッシュ/正常終了を区別せず silent 復元。
            RestoreUnifiedSession();
            return;
        }

        // OFF: 従来どおり異常終了バックアップの復元提案のみ。
        int restored = _backup.OfferRestoreOnStartup(
            this,
            _file.RestoreFromBackup,
            _settings.ConfirmRestoreOnStartup
        );
        if (restored > 0)
            _announcer.Say($"バックアップを {restored} 件復元しました");
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
                var buffers = yEdit.Core.Session.LastSessionBuffersStore.Load(
                    LastSessionBuffersPath
                );
                var (converted, synthetic) = yEdit.Core.Session.LegacySessionConverter.Convert(
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
            yEdit.Core.Session.LastSessionBuffersStore.Delete(LastSessionBuffersPath);
            if (failed.Count > 0 && !_suppressFailedRestoreDialogForTest)
                ShowFailedRestoreDialog(failed);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "yEdit: unified-restore failed: {0}",
                yEdit.Core.Text.SanitizeForDisplay.OneLine(ex.Message, 200)
            );
        }
    }

    /// <summary>
    /// 復元できなかったパス群を 1 個の Warn ダイアログにまとめて表示する。最大 10 件表示、
    /// それ以上は「他 N 件」で省略。パスは <see cref="yEdit.Core.Text.SanitizeForDisplay.OneLine"/>
    /// で BiDi/制御文字を無害化してから表示する(RLO 等の欺瞞対策=MD-H-1 と同じ思想)。
    /// </summary>
    private void ShowFailedRestoreDialog(IReadOnlyList<string> failed)
    {
        const int Cap = 10;
        var shown = failed
            .Take(Cap)
            .Select(p => yEdit.Core.Text.SanitizeForDisplay.OneLine(p, 200));
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
            if (_docs.TabHost.ContainsFocus)
                return;
            _docs.Active?.FocusTarget.Focus();
        });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
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
        _lastCloseTookSilentPathForTest = silentPath;

        if (!silentPath)
        {
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
        if (_settings.RestoreOpenFilesOnStartup)
            _backup.FinalFlushForRestore();

        _settings.LastSession = null; // 統合後は旧形式を書かない
        if (!_settings.RestoreOpenFilesOnStartup)
        {
            // レガシー残骸の掃除(設計 2026-07-23 統合 §8): OFF 恒久ユーザーは移行パス
            // (RestoreUnifiedSession)を一度も通らないため、ここで消さないと本文入りの
            // last-session-buffers.json が orphan として永久に残る。ON 側の掃除は
            // 起動時の移行パスが担う(復元消費とセットで削除)。
            yEdit.Core.Session.LastSessionBuffersStore.Delete(LastSessionBuffersPath);
        }
        SaveSettingsSafe();
        base.OnFormClosing(e);
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
                    "yEdit v0.1.1",
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
            ? "yEdit"
            : $"{(doc.Editor.Modified ? "* " : "")}{doc.State.DisplayName} - yEdit";
    }

    private static string EncodingDisplayName(System.Text.Encoding enc, bool bom)
    {
        // 表示名は Core（EncodingCatalog）に集約。BOM 表記のみ App 側で付与する（UTF-8 のみ）。
        string name = EncodingCatalog.DisplayName(enc.CodePage);
        return enc.CodePage == 65001 && bom ? name + " (BOM)" : name;
    }

    // ==================== 最近のファイル / 設定（M7） ====================

    /// <summary>設定を永続化する（保存失敗は致命でないため握る）。</summary>
    private void SaveSettingsSafe()
    {
        try
        {
            SettingsStore.Save(_settingsPath, _settings);
        }
        catch
        { /* 設定保存失敗は致命でない */
        }
    }

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

    /// <summary>設定ダイアログを開き、OK なら全タブへ外観適用＋バックアップ設定の即時反映＋永続化する。
    /// 項目→コントロールの対応はダイアログに閉じ、ここは Result を差し替えるだけにする。</summary>
    private void OpenSettings()
    {
        using var dlg = new SettingsDialog(_settings);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        _settings = dlg.Result; // Result は取得のたびに組み立てるため一度だけ読む
        foreach (var doc in _docs.Documents)
            EditorAppearance.Apply(doc.Editor, _settings);
        _backup.UpdateSettings(
            _settings.BackupEnabled,
            _settings.BackupIntervalSeconds,
            _settings.RestoreOpenFilesOnStartup
        );
        SaveSettingsSafe();
        _announcer.Say("設定を適用しました");
    }

    /// <summary>
    /// スモークテストの導線=Active 経由の TryOpenOrActivate/Save を Test から叩くため
    /// (MainForm 内では _file を直接使い、テスト側は FileForTest を通す)。
    /// </summary>
    internal FileController FileForTest => _file;

    /// <summary>
    /// grep ジャンプ用: path を開き（既存タブがあれば再利用）、文字オフセット範囲を選択して
    /// エディタへフォーカスする。選択移動でエディタの UIA が一致行を SR に読ませる。
    /// offset は grep が算出した UTF-16 文字位置で、同じ復号経路（TextFileService）を通るため
    /// エディタのスナップショットと同一空間に揃う。
    /// </summary>
    internal void OpenAndSelect(string path, int offset, int length)
    {
        var doc = _file.TryOpenOrActivate(path, suppressAutoCsv: true);
        if (doc is null)
            return;
        doc.Editor.SelectCharRange(offset, length);
        doc.FocusTarget.Focus();
        // ジャンプ先のファイル名と行を明示通知（選択移動の自動読みに加え、別ファイルへ飛んだ文脈を補う）。
        _announcer.Say($"{doc.State.DisplayName} {doc.Editor.CurrentLine + 1} 行目");
    }

    // ==================== 読み上げ照会（SR 利便・M6） ====================

    /// <summary>現在位置（行/総行/桁/文字数/選択数）を読み上げる。</summary>
    private void AnnouncePosition()
    {
        var ed = _docs.Active?.Editor;
        if (ed is null)
            return;
        int line = ed.CurrentLine + 1;
        int totalLines = ed.LineCount;
        int column = ed.GetColumn(ed.CurrentPosition) + 1;
        var (s, e) = ed.GetSelectionCharRange();
        _announcer.Say(
            PositionFormatter.Format(
                line,
                totalLines,
                column,
                ed.SnapshotText.Length,
                e - s,
                ed.Overtype
            )
        );
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
    /// MarkdownPreviewForm.cs:135 と同様に MessageBox.Show を直接使う。
    /// </remarks>
    private void ShowMarkdownPreview()
    {
        var doc = _docs.Active;
        if (doc is null)
            return;

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
            MessageBox.Show(
                this,
                $"プレビューを表示できません。マークダウン本文が大きすぎます。\n\n詳細: {ex.Message}",
                "プレビューを表示できません",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
            _docs.Active?.FocusTarget.Focus(); // 成功パスと対称: 戻り後は編集領域へフォーカス
            return;
        }

        using var f = new MarkdownPreviewForm(html, dir, doc.State.DisplayName);
        f.ShowDialog(this);
        _docs.Active?.FocusTarget.Focus(); // 戻り後は編集領域へフォーカス
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
