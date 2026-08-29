// UiCrashSink.cs
// M-1(設計 2026-08-29 §5): CrashHandler の本番 ICrashSink 実装。
// Program.cs の private nested から切り出したのは、ここに CrashHandler では検証できない
// ロジック(UI スレッドへの marshal とタイムアウト・文面の選択)が残るため
// (Task 4 レビュー Major-2)。MainForm への直依存は ICrashUiHost 越しに薄く保つ。
using System.Diagnostics;

namespace kxEdit.App;

/// <summary>
/// <see cref="UiCrashSink"/> が触る UI 側の面。<see cref="MainForm"/> を直接持たないのは、
/// marshal とタイムアウトの経路をテストから決定的に駆動するため。
/// </summary>
/// <remarks>実装は<b>ロジックを持たない</b>(薄いアダプタに徹する)。</remarks>
internal interface ICrashUiHost
{
    /// <summary>UI スレッドへ marshal できるか(ハンドル生成済みかつ未破棄)。</summary>
    bool CanMarshal { get; }

    /// <summary>現在のスレッドが UI スレッド以外か。</summary>
    bool InvokeRequired { get; }

    /// <summary>UI スレッドへ非同期に投げる(<c>Control.BeginInvoke</c>)。</summary>
    void Post(Action action);

    /// <summary>本文を退避する。<b>UI スレッドから呼ばれること</b>。</summary>
    bool FlushBackups();

    /// <summary>ユーザーへメッセージを出す。</summary>
    void ShowMessage(string text);
}

/// <summary>
/// M-1 の本番 <see cref="ICrashSink"/>。順序と再入は <see cref="CrashHandler"/> 側が担うので、
/// ここは「UI スレッドで退避する」「文面を選ぶ」「終了する」の 3 つだけを持つ。
/// </summary>
internal sealed class UiCrashSink : ICrashSink
{
    /// <summary>UI スレッドへの marshal を諦めるまでの待ち時間(設計 §5.3)。</summary>
    internal static readonly TimeSpan DefaultMarshalWait = TimeSpan.FromSeconds(5);

    private readonly ICrashUiHost _host;
    private readonly TimeSpan _marshalWait;

    internal UiCrashSink(ICrashUiHost host, TimeSpan? marshalWait = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _marshalWait = marshalWait ?? DefaultMarshalWait;
    }

    /// <summary>
    /// 退避する文面を選ぶ。<b>true / false の取り違えは、このブランチが潰そうとしている
    /// 「嘘の安全宣言」そのもの</b>になるため、切り出してテストで固定する。
    /// </summary>
    internal static string CrashMessage(bool flushed) =>
        flushed
            ? "予期しないエラーが発生したため kxEdit を終了します。\n"
                + "編集中の内容は退避したので、次回起動時に復元できます。"
            : "予期しないエラーが発生したため kxEdit を終了します。\n"
                + "編集中の内容を退避できなかった可能性があります。";

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="Application.ThreadException"/> は UI スレッドで発火するので通常は
    /// <see cref="ICrashUiHost.InvokeRequired"/> が false で素通りする。
    /// <c>AppDomain.UnhandledException</c> は任意のスレッドで発火し、
    /// <see cref="BackupCoordinator"/> は UI スレッド専有なので marshal が要る(設計 §5.3)。
    /// <para>
    /// 同期プリミティブに <see cref="TaskCompletionSource{TResult}"/> を使う理由は
    /// <see cref="SerialBackupWriter.WaitForPendingJobs"/> と同じ:
    /// <c>ManualResetEventSlim</c> + <c>using</c> だと、タイムアウトで抜けた後に
    /// UI スレッドが破棄済みインスタンスへ <c>Set</c> を打つ設計になる
    /// (現行 .NET では無害だが実装詳細に寄りかかる形)。<c>TrySetResult</c> は
    /// タイムアウト後に呼ばれても無害で、結果の受け渡しも兼ねるので
    /// captured local の race 面ごと消える。
    /// </para>
    /// </remarks>
    public bool FlushBackups()
    {
        if (!_host.CanMarshal)
            return false; // marshal 先が無い=退避できたと言い切れない
        if (!_host.InvokeRequired)
            return _host.FlushBackups();

        var done = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        try
        {
            _host.Post(() =>
            {
                try
                {
                    done.TrySetResult(_host.FlushBackups());
                }
                catch (Exception ex)
                {
                    // UI スレッド上で新しい未処理例外にしない(再入ガードに握り潰されて無音で消える)。
                    Trace.TraceError($"kxEdit crash flush (marshalled) failed: {ex}");
                    done.TrySetResult(false);
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return false; // ハンドル破棄と競合した
        }
        // タイムアウトで諦める側に倒す(設計 §5.3)。この経路は「今は誰も投げていない」保険で、
        // ここで無期限に待つと通知も終了もできずプロセスが固まる=既定挙動より悪くなる。
        return done.Task.Wait(_marshalWait) && done.Task.Result;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 例外の中身は post-mortem 用に <see cref="Trace"/> へ落とすだけで、
    /// <c>MessageBox</c> には出さない。例外メッセージには開いていたファイルのパス等が
    /// 混じりうるため、画面共有・スクリーンショット経由で漏れる面を増やさない。
    /// <para>
    /// <b>marshal しない判断</b>(設計 §10 に記録): この時点で退避は済んでおり、
    /// 直後に <see cref="Exit"/> する。UI スレッドが死んでいる/ブロックされている場合に
    /// marshal すると「通知も出ずに固まる」= WinForms 既定より悪くなるため、
    /// 呼ばれたスレッドでそのまま出す。代償は、背景スレッド発火時に
    /// オーナー無しのダイアログがメインウィンドウの背後に出る可能性
    /// (<c>AppDomain</c> 経路は現状「誰も投げていない保険」なので実害は小さい)。
    /// </para>
    /// </remarks>
    public void Notify(bool flushed, Exception? ex)
    {
        Trace.TraceError($"kxEdit unhandled exception (flushed={flushed}): {ex}");
        _host.ShowMessage(CrashMessage(flushed));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="Application.Exit()"/> は使わない: この経路の <c>FormClosing</c> は
    /// 結果が読めない(設計 §2.1 = 既定ダイアログの「終了」では保存確認のキャンセルが
    /// 無視された)。退避は済んでいるので終了確認をもう一度出す意味もない。
    /// <b>申し送り</b>: <c>Environment.Exit</c> はフォームの <c>Dispose</c> を走らせないため、
    /// プレビューを開いた状態でクラッシュすると <c>PreviewUserDataFolder</c> が残る
    /// (起動時 sweep は未実装=設計 §11)。
    /// </remarks>
    public void Exit() => Environment.Exit(1);
}

/// <summary>
/// <see cref="ICrashUiHost"/> の本番実装。<see cref="MainForm"/> を薄く包むだけで
/// 判断を持たない(判断は <see cref="UiCrashSink"/> 側=テスト可能な場所に置く)。
/// </summary>
internal sealed class MainFormCrashHost(MainForm form) : ICrashUiHost
{
    public bool CanMarshal => !form.IsDisposed && form.IsHandleCreated;

    public bool InvokeRequired => form.InvokeRequired;

    public void Post(Action action) => form.BeginInvoke(action);

    public bool FlushBackups() => form.FlushBackupsForCrash();

    public void ShowMessage(string text) =>
        MessageBox.Show(text, "kxEdit", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
