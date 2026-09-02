using System.Threading;
using System.Windows.Forms.Automation;
using kxEdit.Accessibility;

namespace kxEdit.App.Speech;

/// <summary>
/// UIA 通知(RaiseAutomationNotification)で読ませる Announcer。
/// 「視覚表示(label.Text)は無条件・発声は非空メッセージだけ」の契約を担保する。
/// 空メッセージは視覚クリアのみ(発声なし)=SR に空通知を撃たない。
/// UIA 非対応環境では <see cref="Raise"/> の catch で握りつぶし、視覚のみに縮退する。
/// UIA-M-4 (v0.11): 50 ms throttle + trailing timer。連打の Raise を絞り、SR キューの詰まりを防ぐ。
/// ただし dedupe (直前と同一なら skip) は意図的に行わない=同一セルの読み直しは SR の基本操作で
/// 沈黙させてはならない。trailing で最後の 1 件を必ず Raise し「今どこにいるか」を SR に伝え続ける。
/// M-8: 「最後に Raise されるのは常に最後に Say されたメッセージ」が throttle 契約の不変条件。
/// このため保留中の trailing は、後続の Say(empty) と窓外 Say(即 Raise) の双方で取り消す。
/// </summary>
internal class UiaAnnouncer : IAnnouncer
{
    /// <summary>UIA-M-4: 連続 Raise の throttle 窓 (50 ms)。この窓内の 2 度目以降の Say は
    /// Raise を skip し、trailing timer に最終メッセージを bufferする。窓経過後に trailing が
    /// 発火して最後の 1 件だけを Raise する契約。既存 SR キューがフラッシュされる猶予として
    /// 50 ms は NVDA の実機観測から選んだ小さめの値 (design §PR-G (3) UIA-M-4)。</summary>
    internal static readonly TimeSpan ThrottleWindow = TimeSpan.FromMilliseconds(50);

    protected readonly Label _label;
    private readonly TimeProvider _clock;

    /// <summary>UIA-L-2: RaiseAutomationNotification 失敗の観測用 trace sink。既定 <c>null</c> で silent 継続
    /// (視覚のみに縮退)=本番挙動は不変。テストでは Fake を注入して「例外が Trace に落ちる」ことを assert する。</summary>
    private readonly IUiaTraceSink? _trace;

    /// <summary>Say/TrailingCallback が触る mutable state (_lastSaidUtc / _pendingMessage / _trailingTimer)
    /// の排他。Say は UI スレッド、TrailingCallback は本番では ThreadPool、テストでは Advance の呼出元
    /// スレッドから走るため、共有 field の可視性を lock で保証する。</summary>
    private readonly object _sync = new();
    private DateTime _lastSaidUtc = DateTime.MinValue;
    private string? _pendingMessage;
    private ITimer? _trailingTimer;

    /// <summary>backward-compat: 既存 <c>new UiaAnnouncer(label)</c> 経路は <paramref name="clock"/>=null
    /// で <see cref="TimeProvider.System"/> にフォールバック・<paramref name="trace"/>=null で silent 継続
    /// =本番挙動は不変。テストは <see cref="FakeTimeProvider"/> で throttle を deterministically 駆動し、
    /// Fake <see cref="IUiaTraceSink"/> で UIA 失敗の観測を検証する。</summary>
    public UiaAnnouncer(Label label, TimeProvider? clock = null, IUiaTraceSink? trace = null)
    {
        _label = label;
        _clock = clock ?? TimeProvider.System;
        _trace = trace;
    }

    public void Say(string message)
    {
        _label.Text = message ?? ""; // 視覚フィードバックは無条件(晴眼/弱視も第一級)。空はクリア
        if (string.IsNullOrEmpty(message))
        {
            // 空=視覚クリアのみ(発声なし)。同時に pending trailing もキャンセルする=
            // T=0 で Say("見つかりました") → T=25ms で Say("2 件目") が throttled/buffered された後
            // T=30ms で Say("") が来た場合、pending を残すと T=75ms の trailing 発火で SR が古い
            // "2 件目" を読み上げ、視覚(空)と乖離する。SR/視覚 parity のため trailing もここで潰す。
            lock (_sync)
            {
                _pendingMessage = null;
                _trailingTimer?.Dispose();
                _trailingTimer = null;
            }
            return;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        lock (_sync)
        {
            if (now - _lastSaidUtc < ThrottleWindow)
            {
                // 窓内: Raise を skip し、pending に最終メッセージを保持する (last-write-wins)。
                // 既に trailing timer が armed 済みなら再スケジュールしない=連投で trailing が
                // 永遠に後ろへ滑る現象を避ける (最初に skip した Say の時刻+50 ms で必ず発火)。
                _pendingMessage = message;
                _trailingTimer ??= _clock.CreateTimer(
                    TrailingCallback,
                    null,
                    ThrottleWindow,
                    Timeout.InfiniteTimeSpan
                );
                return;
            }
            // 窓外: 即 Raise。lock 内で timestamp を更新し、Raise 自体は lock 外で行う
            // (RaiseAutomationNotification の I/O を lock で長時間握らないため)。
            //
            // M-8: armed 済みの trailing も同時に潰す。窓外の即時 Raise は「今このメッセージが
            // 最新である」という宣言であり、それより古い pending を後から鳴らす理由が無い。
            // 潰さないと T=0 Say("a") / T=20 Say("b")(pending) / T=60 Say("c")(窓外・即 Raise)
            // の後、T=70 の trailing が "b" を Raise して「1 つ前が最後に読まれる」。
            // 空文字列分岐(上の IsNullOrEmpty ガード)が同じ危険を名指しして既に潰しており、
            // ここはその対称化。
            _pendingMessage = null;
            _trailingTimer?.Dispose();
            _trailingTimer = null;
            _lastSaidUtc = now;
        }
        Raise(message);
    }

    /// <summary>trailing timer の発火ハンドラ。skip 時に buffer した最終メッセージを Raise する。
    /// 本番では ThreadPool スレッドで走るため、実際の Raise は <see cref="InvokeOnUiThread"/> 経由で
    /// UI スレッドに marshal する (RaiseAutomationNotification は UI スレッドで叩く前提)。</summary>
    private void TrailingCallback(object? state)
    {
        string? msg;
        lock (_sync)
        {
            msg = _pendingMessage;
            _pendingMessage = null;
            _trailingTimer?.Dispose();
            _trailingTimer = null;
            // trailing 発火自体が「直近の Raise」と等価。以降の Say は本発火時刻を起点に throttle 判定。
            _lastSaidUtc = _clock.GetUtcNow().UtcDateTime;
        }
        if (msg is null)
            return; // Say(empty) が timer arm と callback 発火の間で pending をキャンセルした場合ここに来る=silent no-op (SR/視覚 parity)。
        InvokeOnUiThread(() => Raise(msg));
    }

    /// <summary>trailing callback から Raise を UI スレッドで走らせる seam。
    /// 本番: <see cref="Label.BeginInvoke(Delegate)"/> で marshal (ハンドル未生成/廃棄済みなら no-op)。
    /// テスト: override で同期呼び出しに置き換え、メッセージポンプなしで観測可能にする。</summary>
    protected virtual void InvokeOnUiThread(Action action)
    {
        if (_label.IsDisposed || !_label.IsHandleCreated)
            return; // ハンドル未生成/廃棄済み=UI スレッドに marshal 不能。SR は視覚のみに縮退(安全側)
        try
        {
            _label.BeginInvoke(action);
        }
        // ObjectDisposedException は InvalidOperationException を継承するため、具体側を先に受ける。
        catch (ObjectDisposedException)
        { /* Label が Dispose された直後の race: 視覚のみに縮退 */
        }
        catch (InvalidOperationException)
        { /* ハンドル破棄との race: 無害に握り潰す (Raise 自体も try/catch で保護済み) */
        }
    }

    /// <summary>確定済み(非空)メッセージを Label の UIA プロバイダから通知として上げる。
    /// 空ガードと視覚表示は <see cref="Say"/> が済ませているため、ここでは message が非空であることが前提。
    /// テストではオーバーライドして発声呼び出しを観測する(発声手段の seam)。</summary>
    /// <remarks>
    /// UIA-L-2: 実際の <see cref="RaiseCore"/> 呼び出しは try/catch で保護される。
    /// - <see cref="ObjectDisposedException"/> / <see cref="InvalidOperationException"/> は
    ///   Label 破棄後 race / UIA 非対応環境の想定内失敗として個別 catch し、silent 継続 (視覚のみ縮退)。
    /// - それ以外の例外は base <c>Exception</c> でまとめて捕捉し、<see cref="_trace"/> があれば
    ///   "raise-automation-notification" カテゴリで通知した上で silent 継続する
    ///   (SR キューの詰まりでも本体の Say を落とさない=元 <c>catch { }</c> の握りつぶし契約を pin)。
    /// </remarks>
    protected virtual void Raise(string message)
    {
        try
        {
            RaiseCore(message);
        }
        // ObjectDisposedException は InvalidOperationException を継承するため、具体側を先に受ける。
        catch (ObjectDisposedException)
        { /* Label が Dispose された直後の race: 視覚のみに縮退 */
        }
        catch (InvalidOperationException)
        { /* UIA 非対応環境では視覚表示のみ */
        }
        catch (Exception ex)
        {
            // UIA-L-2: 想定外例外は trace に落として観測可能にしつつ silent 継続。
            // I-1 fixup: trace sink 自身 (Debug/Trace listener I/O・formatting 等) が投げても
            //            本体 (edit 経路) には影響させない=元 `catch { }` の握りつぶし契約を維持する。
            try
            {
                _trace?.Warn("raise-automation-notification", "label UIA notification failed", ex);
            }
            catch
            { /* trace sink 自身が投げても本体には影響させない (UIA-L-2 I-1 fixup) */
            }
        }
    }

    /// <summary>UIA-L-2: <see cref="Raise"/> の try 内で実行する「実際の RaiseAutomationNotification 呼出」の seam。
    /// テストではこれを override して「Raise の catch/trace 経路」を deterministically に駆動する
    /// (<see cref="Raise"/> 自体を override すると catch も差し替わってしまうため 1 段挟む)。</summary>
    protected virtual void RaiseCore(string message)
    {
        _label.AccessibilityObject.RaiseAutomationNotification(
            AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.MostRecent,
            message
        );
    }
}
