using System.Diagnostics;
using kxEdit.Core.Settings;
using kxEdit.Core.Text;

namespace kxEdit.App;

static class Program
{
    /// <summary>
    /// 引き渡しの接続確立の上限(設計 2026-09-14 §4)。
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 引き渡しの ACK 待ちの上限(設計 2026-09-14 §4)。
    /// </summary>
    /// <remarks>
    /// <b><c>PendingActivation.ActivateTimeout</c>(3 秒)と等号にしてはならない。</b>
    /// 等号だと「前面化は成功しているのに、2 つ目が待ちきれずエラーダイアログを出す」窓が開く
    /// (独立した 2 つのレビューが同じ指摘をしている)。期限の入れ子は
    /// <see cref="SingleInstanceServer"/> の remarks が唯一の正:
    /// <c>ActivateTimeout(3s) &lt; AckTimeout(4s) &lt; PerConnectionTimeout(5s) &lt; Dispose の待ち(7s)</c>。
    /// </remarks>
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(4);

    [STAThread]
    static void Main(string[] args)
    {
        // 設計 2026-09-05: WinForms の ImeMode 推測機構を無効化する。放置すると、タブを閉じた
        // ときとモーダルダイアログを閉じたときに WinForms が IME を「開いてから閉じる」ため、
        // 日本語 SR が毎回「文字変換」「変換停止」と読み上げる(kxEdit 自身は IME の
        // ON/OFF を変える API を一切呼んでいない —— ただし ImeMode.Disable を設定した
        // 2 ダイアログの入力欄は例外で、そこへ入ると IME は閉じる。設計 §6.2)。
        //
        // 位置の制約は「最初のウィンドウが作られる前」であること。実測で、フォーム表示後に
        // 当てると起動時に既に武装済みの分が 1 回だけ鳴った(設計 §5.2)。下の
        // Application.SetUnhandledExceptionMode(Application.Run より前・かつウィンドウ生成前)と
        // 同種の制約である。間に何かを挟むたび「それはウィンドウを作らないか」を確かめる
        // 必要が出るので、確実な最初の文に置く。
        // 前後関係は MainFormSmokeTests の
        // ProgramMain_suppresses_ime_mode_inference_before_creating_any_window が IL で固定する。
        //
        // 失敗しても起動は止めない(現状動作＝発声が出る、に落ちるだけ)。.NET 更新で内部構造が
        // 変わったことに後から気づけるよう Trace に残す。ImeStartupTests が CI で先に気づく。
        var imeSuppression = ImeStartup.SuppressWinFormsImeModeInference();
        if (imeSuppression.Succeeded)
        {
            Trace.TraceInformation(
                $"kxEdit ime: WinForms ImeMode inference disabled ({string.Join(", ", imeSuppression.PatchedTables)})"
            );
        }
        else
        {
            // 劣化経路なので Warning(設計 §5.3。本リポジトリでも失敗・劣化は TraceWarning で統一)。
            // 失敗枝でも PatchedTables を出す。ImeStartup は途中まで塗れていた分を残して
            // 失敗を返す(catch 経路)ので、ここで捨てると「1 つも無効化できていない」と
            // 「一部は無効化済み」がログ上で区別できなくなる。
            Trace.TraceWarning(
                $"kxEdit ime: WinForms ImeMode inference NOT disabled: {imeSuppression.FailureReason}"
                    + $" (patched so far: {string.Join(", ", imeSuppression.PatchedTables)})"
            );
        }

        // Shift_JIS/EUC-JP を使うため CodePagesEncodingProvider を登録（Core も内部登録するが明示）。
        EncodingCatalog.EnsureRegistered();
        // MD-L-2: 依存 (Markdig) のバージョンを Trace ログへ (post-mortem/依存更新時の追跡用)。
        // 既定リスナ未装着の環境では実質 no-op。ApplicationConfiguration.Initialize() より前で
        // 早い段階に出しておく (WinForms init 失敗時にも記録が残る)。
        var markdigVersion = typeof(Markdig.Markdown).Assembly.GetName().Version;
        Trace.TraceInformation($"kxEdit deps: Markdig={markdigVersion}");
        // 【T-1・Task 6 仕様レビュー 2026-09-15】Initialize は単一インスタンスのゲートより「前」。
        // ゲートの失敗経路(下の ShowStartupError)は MessageBox を出すが、kxEdit.App に
        // app.manifest は無く DPI 認識は Initialize() の SetHighDpiMode に依存している。
        // ゲートを先に置くと、高 DPI 環境で【このダイアログだけ】ビットマップ拡大(ぼやけ)に
        // なる = CLAUDE.md §2「弱視ユーザーも第一級」に触れる。
        // 移動して安全な理由: Initialize() は EnableVisualStyles / SetCompatibleTextRenderingDefault /
        // SetHighDpiMode を呼ぶだけで、共有状態に触れずウィンドウも作らない
        // (下の IME IL テストの xmldoc 自身が「Initialize はアンカーにしない —— ウィンドウを
        // 作らないので」と明記している)。
        ApplicationConfiguration.Initialize();

        // 単一インスタンス(設計 2026-09-14 §3)。ここは ImeStartup より後・
        // PreviewUserDataSweeper / SettingsStartup.Prepare より前でなければならない:
        //   - ImeStartup より後 = 失敗経路が MessageBox を出す(ウィンドウを作る)ため、
        //     「IME 推測の無効化は最初のウィンドウより前」の不変条件をまたがない。
        //   - Sweeper / Prepare より前 = どちらも共有状態を書き換える。特に Prepare は
        //     壊れた settings.json を退避するので、2 つ目に実行させてはならない。
        // この前後関係は ProgramMain_gates_single_instance_before_touching_shared_state が固定する。
        //
        // 【T-2・Task 6 仕様レビュー 2026-09-15】名前の組み立ては【ゲートの外】なので、
        // SID を取れないときの InvalidOperationException は Acquire の catch では拾えない。
        // ここで握って --new-instance と同じ経路(排他も待受もせず通常起動)へ落とす ——
        // 単一インスタンス化は諦めるがエディタは使える方へ倒す。設計全体が
        // 「待受の開始に失敗しても起動を止めない」で一貫しており、ここだけ起動不能にする
        // 理由がない。
        // Parse は try / if の【外】に置く。中へ入れると、SID 取得に失敗した劣化経路で
        // 「未知の引数を無視した」Trace が出なくなる —— 劣化経路こそ post-mortem の
        // 手掛かりが要る(レビュー指摘 M-2)。
        var options = CommandLineOptions.Parse(args);

        string? mutexName = null;
        string? pipeName = null;
        try
        {
            mutexName = SingleInstanceNames.CurrentMutexName();
            pipeName = SingleInstanceNames.CurrentPipeName();
        }
        catch (InvalidOperationException ex)
        {
            Trace.TraceWarning($"single-instance: names unavailable: {ex.Message}");
        }

        var pending = new PendingActivation();
        var outcome = SingleInstanceOutcome.FirstInstance;
        SingleInstanceGate? gate = null;
        if (mutexName is not null && pipeName is not null)
        {
            (outcome, gate) = SingleInstanceGate.Acquire(
                options,
                mutexName,
                pipeName,
                pending.Request,
                ConnectTimeout,
                AckTimeout
            );
        }

        // gate は Mutex を握っている。先に破棄すると排他が終了前に外れるので、
        // Application.Run まで囲む。1 つ目以外では null になるが、using は null を no-op として
        // 扱うので分岐は要らない(--new-instance / 上の T-2 経路も同じ)。
        using (gate)
        {
            switch (outcome)
            {
                case SingleInstanceOutcome.HandedOff:
                    // 既存ウィンドウが前面に出た。無言で終わる(設計 D1)。
                    // 【終了コードは 0】引き渡しは成功しているため。下の 2 つと区別が付く
                    // ことに意味がある —— 将来 kxEdit.exe foo.txt でファイルを開けるように
                    // なると(設計 §6)、スクリプトはこの終了コードで成否を判定できる。
                    return;
                case SingleInstanceOutcome.NoResponse:
                    ShowStartupError(
                        "kxEdit は既に起動していますが応答しません。\n"
                            + "タスク マネージャーで kxEdit を終了してから、もう一度実行してください。"
                    );
                    // 【設計 §3「ゲートの 3 状態」: 失敗は終了コード非 0】
                    // §4 の「失敗理由を 2 つに分ける」精密化はこの定めを撤回していない。
                    // Environment.Exit(1) は使わないこと —— using (gate) の Dispose を飛ばす。
                    // ExitCode なら Main から普通に抜けて finally が走る。
                    Environment.ExitCode = 1;
                    return;
                case SingleInstanceOutcome.ForeignPeer:
                    // 「別の場所」と断定しないこと(設計 §4 の訂正 2026-09-15)。
                    // この結果には 2 経路ある: ①実行ファイルパスの不一致(本当に別フォルダ)
                    // ②高 IL の kxEdit が先に起動していて Connect が拒否された(同じ場所・
                    // 同じビルドで権限だけ違う)。②で「別の場所」と言うと、ユーザーは
                    // 存在しないフォルダを探すことになる。取るべき行動は両方同じなので
                    // 場所にも権限にも触れない文言にする。
                    ShowStartupError(
                        "別の kxEdit が既に起動していますが、この kxEdit からは操作できません。\n"
                            + "先に起動している kxEdit を終了してから、もう一度実行してください。"
                    );
                    Environment.ExitCode = 1; // 上と同じ理由(設計 §3)
                    return;
                case SingleInstanceOutcome.FirstInstance:
                default:
                    break; // 通常起動へ進む
            }

            // M-V1(2026-08-29 最終レビュー 脆弱性パス): M-1 の Environment.Exit はフォームの
            // Dispose を走らせないため、プレビューを開いたままクラッシュすると
            // WebView2 のプロファイルが残る。起動時に回収する(自分だけのときに限る)。
            PreviewUserDataSweeper.SweepIfSoleInstance();

            // M-1(設計 2026-08-29 §5): WinForms 既定の未処理例外ダイアログに到達させない。
            // SetUnhandledExceptionMode は Application.Run より前・かつウィンドウ生成前に呼ぶ必要が
            // あるため MainForm の生成より前に置く。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            var form = CreateMainForm(SettingsStore.DefaultPath);
            var crash = new CrashHandler(new UiCrashSink(new MainFormCrashHost(form)));
            // Application.ThreadException の add は WinForms 内部で「代入」かつスレッド固有。
            // 2 箇所目の購読を足すとここが黙って消えるので、配線は 1 箇所に保つこと。
            Application.ThreadException += (_, e) => crash.Handle(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                // IsTerminating=false(現行 .NET では実質来ない)では既存の続行を邪魔しない。
                if (!e.IsTerminating)
                    return;
                // 退避を最優先する。ExceptionObject は Exception とは限らず、その ToString() は
                // 任意の実装=投げうる。ハンドラ内で例外が出ると CLR はそのままプロセスを落とすので、
                // Trace を先に置くと「退避せずに WER へ落ちる」= M-1 が塞ごうとした喪失に戻る。
                crash.Handle(e.ExceptionObject as Exception);
                // as で null に潰れると post-mortem から手掛かりが消えるため、生のオブジェクトも残す。
                // ここに到達するのは Exit が返らなかったときだけなので、実質は保険。
                if (e.ExceptionObject is not Exception)
                {
                    try
                    {
                        Trace.TraceError(
                            $"kxEdit unhandled non-Exception object: {e.ExceptionObject}"
                        );
                    }
                    catch (Exception traceEx)
                    {
                        Trace.TraceError(
                            $"kxEdit non-Exception object dump failed: {traceEx.GetType()}"
                        );
                    }
                }
            };

            // ウィンドウが出来たことを PendingActivation へ知らせる(設計 §4「起動中レース」で
            // 保留に積んだ要求の消化点)。MainForm 側は触らない —— ctor 引数を増やすと
            // CreateMainForm の呼び出し側すべてに波及する。
            // MainForm.OnShown は base.OnShown(=この購読)を先頭で呼ぶので、消化は復元処理より
            // 前に起きる。今日のペイロードは「前面化」だけなので順序に依存しない。
            form.Shown += (_, _) => pending.Attach(form);

            Application.Run(form);
        }
    }

    /// <summary>
    /// 単一インスタンス判定の失敗をユーザーへ伝える(設計 2026-09-14 D4)。
    /// </summary>
    /// <remarks>
    /// フォールバック起動はしない —— 引き渡し失敗は既存インスタンスのハング前後に集中するので、
    /// そこで 2 つ目を起動すると「ハングした A + 新規 B」がバックアップを奪い合う、
    /// 本設計が塞ごうとしている最悪の組み合わせを作る(設計 D4 の理由)。
    /// </remarks>
    private static void ShowStartupError(string message) =>
        MessageBox.Show(message, "kxEdit", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>
    /// 設定を確定して <see cref="MainForm"/> を作る(M-11・設計 2026-09-02 §5.4)。
    /// 設定は起動で 1 回だけ読む(起動時確定方針)。壊れていれば退避し、起動後に 1 回出す
    /// 警告文言を受け取って <see cref="MainForm"/> へ渡す。判定・退避・文言の組み立ては
    /// <see cref="SettingsStartup.Prepare"/> の担当。<b>ここで通知はしない</b>——この時点では
    /// 通知手段が無い(フォームがまだ無い)ので、回収点は <c>MainForm.OnShown</c> である。
    /// <para>
    /// <b><see cref="Main"/> から切り出してあるのは、ここを自動テストから叩くため。</b>
    /// <c>Main</c> は <c>[STAThread]</c> + <c>Application.Run</c> で実行できず、その IL を読んでも
    /// <b>「<c>Prepare</c> の戻り値が本当に <c>MainForm</c> へ渡っているか」は観測できない</b>
    /// (呼出集合は同じまま、警告を捨てて <c>null</c> を渡す変異が生存する。実測・§10.18)。
    /// 実行して観測できる形にしないと、配線が黙って切れても緑のままになる。
    /// </para>
    /// <para>
    /// <paramref name="backupDirectory"/> / <paramref name="sessionLayoutPath"/> は
    /// <b>テストが実 <c>%APPDATA%</c> を触らないための隔離用</b>(null=既定パス=製品の経路)。
    /// ここを開けていないと、破損 <c>settings.json</c> で作った既定設定は
    /// <c>BackupEnabled=true</c> なので、テストが実バックアップを走査・削除しうる。
    /// </para>
    /// </summary>
    internal static MainForm CreateMainForm(
        string settingsPath,
        string? backupDirectory = null,
        string? sessionLayoutPath = null
    )
    {
        var (settings, settingsWarning, quarantineBeforeFirstSave) = SettingsStartup.Prepare(
            settingsPath
        );
        return new MainForm(
            settings,
            settingsPath,
            backupDirectory,
            sessionLayoutPath,
            settingsWarning,
            quarantineBeforeFirstSave
        );
    }
}
