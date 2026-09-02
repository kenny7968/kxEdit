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
        // TODO(Task 8): status を受けて Corrupt の退避と Corrupt / Unreadable の通知へ配線する
        // (設計 2026-09-02 §5.4)。ここが `out _` である間は、破損しても無言で既定値へ戻る。
        var settings = SettingsStore.Load(SettingsStore.DefaultPath, out _);
        // M-V1(2026-08-29 最終レビュー 脆弱性パス): M-1 の Environment.Exit はフォームの
        // Dispose を走らせないため、プレビューを開いたままクラッシュすると
        // WebView2 のプロファイルが残る。起動時に回収する(自分だけのときに限る)。
        PreviewUserDataSweeper.SweepIfSoleInstance();
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
}
