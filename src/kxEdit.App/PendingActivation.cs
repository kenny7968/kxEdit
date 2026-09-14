// PendingActivation.cs
// 設計 2026-09-14 §4「起動中レース」: 待受は MainForm 構築より前に始まるため、
// ウィンドウがまだ無い時点で前面化要求が来うる。取りこぼさずに保留する。
using System.Diagnostics;

namespace kxEdit.App;

/// <summary>
/// パイプスレッドから来る前面化要求を UI スレッドへ渡す受け皿。
/// </summary>
/// <remarks>
/// 今日は「どのみち起動して前面に出る」ので保留の消化は実質 no-op だが、
/// <b>将来ファイル引数が乗ると取りこぼしが実害になる</b>(設計 §6)。配線を先に作っておく。
/// </remarks>
internal sealed class PendingActivation
{
    /// <summary>
    /// UI スレッドがハングしている場合に待ち続けないための上限(設計 D4)。
    /// </summary>
    /// <remarks>
    /// <b><c>Program.AckTimeout</c> と等号にしてはならない</b>。等号だと「前面化は成功して
    /// いるのに 2 つ目が待ちきれずエラーダイアログを出す」窓が開く(独立した 2 つのレビューが
    /// 指摘)。期限の入れ子の全体像は <see cref="SingleInstanceServer"/> の remarks が正:
    /// <c>ActivateTimeout(3s) &lt; AckTimeout(4s) &lt; PerConnectionTimeout(5s) &lt; Dispose の待ち(7s)</c>。
    /// </remarks>
    private static readonly TimeSpan ActivateTimeout = TimeSpan.FromSeconds(3);

    private readonly Action<Form> _activate;
    private readonly object _sync = new();
    private Form? _form;
    private bool _deferred;

    /// <param name="activate">
    /// 前面化の実処理。既定は <see cref="WindowActivator.Activate"/>。
    /// <b>差し替え可能にしてあるのはテストのため</b> —— <see cref="WindowActivator"/> は実ウィンドウと
    /// フォアグラウンド状態を要求する(設計 §5 で L5 担当と決めた型)ので、注入できないと
    /// <see cref="Attach"/> の分岐が丸ごと自動テストの網から落ちる。
    /// </param>
    internal PendingActivation(Action<Form>? activate = null) =>
        _activate = activate ?? WindowActivator.Activate;

    /// <summary>
    /// <b>パイプスレッドから呼ばれる。</b>UI スレッドへマーシャルして前面化する。
    /// 戻り値は「<b>要求を引き受けたか(ACK を返してよいか)</b>」——<c>false</c> なら
    /// 2 つ目は「応答しません」エラーになる(設計 D4)。
    /// <para>
    /// 「前面化できたか」ではない点に注意。ウィンドウ未生成のときは保留に積んで
    /// <c>true</c> を返す —— どのみちこれから前面に出るので、引き渡しは成立している。
    /// </para>
    /// </summary>
    /// <param name="ct">
    /// この接続の期限(<see cref="SingleInstanceServer"/> の <c>PerConnectionTimeout</c>)。
    /// <b>マーシャル待ちは必ずこれで打ち切ること。</b>無視してブロックすると、UI が一度
    /// 詰まっただけで<b>待受ループごと止まり</b>、以後その kxEdit は引き渡し不能になる
    /// (Task 4 の C-1 と同じ壊れ方。<c>PendingActivationTests</c> の
    /// <c>Request_WhenConnectionDeadlineAlreadyExpired_DoesNotAccept</c> が固定する)。
    /// </param>
    internal bool Request(CancellationToken ct)
    {
        Form? form;
        lock (_sync)
        {
            form = _form;
            if (form is null)
            {
                // まだウィンドウが無い = 自分が今まさに起動中。成立扱いにする。
                _deferred = true;
                return true;
            }
        }
        try
        {
            // Invoke ではなく BeginInvoke + 期限付き待機。UI スレッドが固まっていても
            // パイプの待受スレッドを道連れにしない。
            // マーシャル先で前面化が走る = UI スレッドで実行されることがここで担保される
            // (Task 5 の申し送り: Control.Handle を別スレッドから初回に触ると、そのスレッドで
            // ハンドルが生成されメッセージポンプの所属を誤る)。
            //
            // 【投げっぱなしにしてはならない】完了を待たずに true を返すと、2 つ目は ACK を
            // 受けて即終了し、譲渡したフォアグラウンド権ごと消える(設計 §4 ★1)。
            // すると A の SetForegroundWindow が拒否され、タスクバーが点滅するだけになる。
            var marshalled = form.BeginInvoke(() => ActivateSafely(form));
            int signaled = WaitHandle.WaitAny(
                [marshalled.AsyncWaitHandle, ct.WaitHandle],
                ActivateTimeout
            );
            return signaled == 0; // 1 = 接続の期限切れ / WaitTimeout = 活性化の期限切れ
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // BeginInvoke 自体の失敗(ハンドル未生成・破棄済み)。引き受けられないので ACK しない。
            Trace.TraceWarning($"single-instance: activation failed: {ex.Message}");
            return false;
        }
    }

    /// <summary><b>UI スレッドから呼ぶ。</b>ウィンドウが出来たことを知らせ、保留を消化する。</summary>
    internal void Attach(Form form)
    {
        bool deferred;
        lock (_sync)
        {
            _form = form;
            deferred = _deferred;
            _deferred = false;
        }
        if (deferred)
            ActivateSafely(form);
    }

    /// <summary>
    /// 前面化を実行し、<b>例外を外へ出さない</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 【意図的に全例外を飲む】<see cref="WindowActivator.Activate"/> は <c>form.Handle</c> で
    /// <see cref="ObjectDisposedException"/> を投げうる(実測)——終了中レース
    /// (<c>BeginInvoke</c> 成功後・デリゲート実行前に <c>MainForm</c> が破棄される)で踏む。
    /// 包まないと実測で次が起きる:
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="Request"/> 経路: WinForms が <c>BeginInvoke</c> のデリゲート内例外を
    /// <see cref="Application.ThreadException"/> へ回送 → <c>Program</c> の配線が
    /// <c>CrashHandler</c> → <c>Environment.Exit(1)</c>。一方 <c>WaitAny</c> は 0(完了)を
    /// 返すので 2 つ目は ACK を受けて静かに終了する = <b>「2 つ目を起動したら 1 つ目が消えた」</b>。</item>
    /// <item><see cref="Attach"/> 経路: 直接呼び出しなので <c>MainForm.OnShown</c> を巻き込んで
    /// <b>起動を止める</b>(設計の「前面化の失敗で起動を止めない」に直接抵触)。</item>
    /// </list>
    /// <para>
    /// <b>対処を <see cref="WindowActivator"/> 側のガードにしないこと</b> —— あちらは自動テストの
    /// 網の外で、検証不能な分岐を増やさない方針(設計 §5)。ここは網の内側にある。
    /// </para>
    /// </remarks>
    private void ActivateSafely(Form form)
    {
        try
        {
            _activate(form);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                $"single-instance: activation threw: {ex.GetType().Name}: {ex.Message}"
            );
        }
    }
}
