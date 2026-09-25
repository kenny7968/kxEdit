namespace kxEdit.App;

/// <summary>
/// <see cref="IDebounceScheduler"/> の本番実装。WinForms のタイマーなので、満了は UI スレッドの
/// メッセージループで起きる(action は UI スレッドで走る)。所有者が Dispose すること。
/// Dispose 後の <see cref="Schedule"/> / <see cref="Cancel"/> は何もしない(アプリ終了時の
/// 解放順序に依存しないため。WinForms の Timer は Dispose 後でも Start すると動き出す)。
/// Dispose は複数回呼んでよい。
/// </summary>
public sealed class WinFormsDebounceScheduler : IDebounceScheduler, IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private Action? _pending;
    private bool _disposed;

    public WinFormsDebounceScheduler(int delayMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(delayMs);
        _timer = new System.Windows.Forms.Timer { Interval = delayMs };
        _timer.Tick += OnTick;
    }

    public void Schedule(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_disposed)
            return;
        _timer.Stop(); // 再始動=遅延は最後の予約から数える
        _pending = action;
        _timer.Start();
    }

    public void Cancel()
    {
        if (_disposed)
            return;
        _timer.Stop();
        _pending = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // 単発: 先に止めて保留を外してから実行する(action の中で Schedule し直してもよい)。
        _timer.Stop();
        var action = _pending;
        _pending = null;
        action?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Cancel();
        _disposed = true;
        _timer.Dispose();
    }
}
