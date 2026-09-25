namespace kxEdit.App;

/// <summary>
/// 単発の遅延実行(入力の間引き)。<see cref="Schedule"/> は保留中の実行を取り消してから予約し直す
/// =最後の予約から所定の遅延の後に 1 回だけ実行する。
/// 検索語の打鍵で件数表示の更新を間引くために使う(2026-09-25 フェーズ 5 P-5(b))。
/// テストでは手動で発火させる実装に差し替える。
/// UI スレッドから使い、action も UI スレッドで実行されること。
/// </summary>
public interface IDebounceScheduler
{
    /// <summary>保留中の実行を取り消し、<paramref name="action"/> を所定の遅延の後に 1 回実行するよう予約する。</summary>
    void Schedule(Action action);

    /// <summary>保留中の実行を取り消す。無ければ何もしない(冪等)。</summary>
    void Cancel();
}
