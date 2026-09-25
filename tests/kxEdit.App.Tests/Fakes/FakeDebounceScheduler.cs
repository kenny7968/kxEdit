namespace kxEdit.App.Tests.Fakes;

/// <summary><see cref="IDebounceScheduler"/> のテスト用フェイク。時間は進めず、<see cref="Fire"/> で満了させる。</summary>
public sealed class FakeDebounceScheduler : IDebounceScheduler
{
    private Action? _pending;

    public int ScheduleCount { get; private set; }

    public bool IsPending => _pending is not null;

    public void Schedule(Action action)
    {
        ScheduleCount++;
        _pending = action;
    }

    public void Cancel() => _pending = null;

    /// <summary>保留中の実行を満了させる(無ければ例外=テストの前提違い)。</summary>
    public void Fire()
    {
        var action = _pending ?? throw new InvalidOperationException("保留中の実行がありません");
        _pending = null;
        action();
    }
}
