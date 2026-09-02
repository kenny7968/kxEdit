using System.Diagnostics;
using kxEdit.Core.Settings;
using kxEdit.Core.Text;

namespace kxEdit.App;

static class Program
{
    [STAThread]
    static void Main()
    {
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
