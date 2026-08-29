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
        var crash = new CrashHandler(new MainFormCrashSink(form));
        Application.ThreadException += (_, e) => crash.Handle(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // IsTerminating=false(現行 .NET では実質来ない)では既存の続行を邪魔しない。
            if (e.IsTerminating)
                crash.Handle(e.ExceptionObject as Exception);
        };

        Application.Run(form);
    }

    /// <summary>
    /// M-1 の本番 <see cref="ICrashSink"/>。順序と再入は <see cref="CrashHandler"/> 側で
    /// 検証済み(<c>CrashHandlerTests</c>)なので、ここは各手順の「本物」だけを持つ。
    /// </summary>
    private sealed class MainFormCrashSink(MainForm form) : ICrashSink
    {
        /// <summary>UI スレッドへの marshal を諦めるまでの待ち時間。
        /// UI スレッドが死んでいる/ブロックされていると戻ってこないため(設計 §5.3)。</summary>
        private static readonly TimeSpan MarshalWait = TimeSpan.FromSeconds(5);

        public bool FlushBackups()
        {
            // Application.ThreadException は UI スレッドで発火するので通常はここを素通りする。
            // AppDomain.UnhandledException は任意のスレッドで発火し、BackupCoordinator は
            // UI スレッド専有なので marshal が要る(設計 §5.3)。
            if (form.IsDisposed || !form.IsHandleCreated)
                return false; // marshal 先が無い=退避できたと言い切れない
            if (!form.InvokeRequired)
                return form.FlushBackupsForCrash();

            bool result = false;
            using var done = new ManualResetEventSlim(false);
            try
            {
                form.BeginInvoke(() =>
                {
                    try
                    {
                        result = form.FlushBackupsForCrash();
                    }
                    finally
                    {
                        done.Set();
                    }
                });
            }
            catch (InvalidOperationException)
            {
                return false; // ハンドル破棄と競合した
            }
            // タイムアウトで諦める側に倒す(設計 §5.3)。この経路は「今は誰も投げていない」保険で、
            // ここで無期限に待つと通知も終了もできずプロセスが固まる=既定挙動より悪くなる。
            return done.Wait(MarshalWait) && result;
        }

        public void Notify(bool flushed, Exception? ex)
        {
            // 例外の中身は post-mortem 用に Trace へ落とすだけで、MessageBox には出さない。
            // 例外メッセージには開いていたファイルのパス等が混じりうるため、
            // 画面共有・スクリーンショット経由で漏れる面を増やさない。
            Trace.TraceError($"kxEdit unhandled exception (flushed={flushed}): {ex}");
            MessageBox.Show(
                flushed
                    ? "予期しないエラーが発生したため kxEdit を終了します。\n"
                        + "編集中の内容は退避したので、次回起動時に復元できます。"
                    : "予期しないエラーが発生したため kxEdit を終了します。\n"
                        + "編集中の内容を退避できなかった可能性があります。",
                "kxEdit",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }

        /// <remarks>
        /// <see cref="Application.Exit()"/> は使わない: この経路の <c>FormClosing</c> は
        /// 結果が読めない(設計 §2.1 = 既定ダイアログの「終了」では保存確認のキャンセルが
        /// 無視された)。退避は済んでいるので終了確認をもう一度出す意味もない。
        /// </remarks>
        public void Exit() => Environment.Exit(1);
    }
}
