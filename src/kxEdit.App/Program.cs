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
        // 設定は起動で1回だけ読む（起動時確定方針）。
        var settings = SettingsStore.Load(SettingsStore.DefaultPath);
        ApplicationConfiguration.Initialize();

        // M-1(設計 2026-08-29 §5): WinForms 既定の未処理例外ダイアログに到達させない。
        // SetUnhandledExceptionMode は Application.Run より前・かつウィンドウ生成前に呼ぶ必要が
        // あるため MainForm の生成より前に置く。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        var form = new MainForm(settings);
        var crash = new CrashHandler(new UiCrashSink(new MainFormCrashHost(form)));
        // Application.ThreadException の add は WinForms 内部で「代入」かつスレッド固有。
        // 2 箇所目の購読を足すとここが黙って消えるので、配線は 1 箇所に保つこと。
        Application.ThreadException += (_, e) => crash.Handle(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // IsTerminating=false(現行 .NET では実質来ない)では既存の続行を邪魔しない。
            if (!e.IsTerminating)
                return;
            // ExceptionObject は Exception とは限らない。as で null に潰れると post-mortem から
            // 手掛かりが完全に消えるため、生のオブジェクトはここで Trace へ落としておく。
            if (e.ExceptionObject is not Exception)
                Trace.TraceError($"kxEdit unhandled non-Exception object: {e.ExceptionObject}");
            crash.Handle(e.ExceptionObject as Exception);
        };

        Application.Run(form);
    }
}
