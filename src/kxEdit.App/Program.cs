using System.Diagnostics;
using kxEdit.Core.Settings;
using kxEdit.Core.Text;

namespace kxEdit.App;

static class Program
{
    [STAThread]
    static void Main()
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
        // M-V1(2026-08-29 最終レビュー 脆弱性パス): M-1 の Environment.Exit はフォームの
        // Dispose を走らせないため、プレビューを開いたままクラッシュすると
        // WebView2 のプロファイルが残る。起動時に回収する(自分だけのときに限る)。
        PreviewUserDataSweeper.SweepIfSoleInstance();
        ApplicationConfiguration.Initialize();

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
                    Trace.TraceError($"kxEdit unhandled non-Exception object: {e.ExceptionObject}");
                }
                catch (Exception traceEx)
                {
                    Trace.TraceError(
                        $"kxEdit non-Exception object dump failed: {traceEx.GetType()}"
                    );
                }
            }
        };

        Application.Run(form);
    }

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
