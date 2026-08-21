using kxEdit.Core.Search;

namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="GrepController"/> の検索デリゲート(§0 精密化 3)のテスト用フェイク。
/// <see cref="Pending"/> に <see cref="TaskCompletionSource{TResult}"/> を積むと、呼び出し順に
/// dequeue して保留 Task を返す(追い越し/BeginClose の決定的タイミング再現に使う)。
/// Pending が空のときは <see cref="DefaultOutcome"/> を <see cref="Task.FromResult"/> で即時返す。
/// <see cref="Invocations"/> は Progress も保持する(Stage 8 Task D-2:
/// Controller 内で作られた IProgress&lt;GrepProgress&gt; インスタンスをテスト側で
/// 明示発火し追い越し guard の 3 条件を独立検証するため)。
/// </summary>
public sealed class FakeGrepSearchFn
{
    public Queue<TaskCompletionSource<GrepOutcome>> Pending { get; } = new();
    public GrepOutcome DefaultOutcome { get; set; } = EmptyOutcome();
    public List<(
        GrepRequest Request,
        IProgress<GrepProgress>? Progress,
        CancellationToken Token
    )> Invocations { get; } = new();

    public Task<GrepOutcome> Invoke(
        GrepRequest req,
        IProgress<GrepProgress>? prog,
        CancellationToken ct
    )
    {
        Invocations.Add((req, prog, ct));
        return Pending.Count > 0 ? Pending.Dequeue().Task : Task.FromResult(DefaultOutcome);
    }

    public static GrepOutcome EmptyOutcome() =>
        new(Array.Empty<GrepHit>(), 0, 0, Array.Empty<GrepError>(), false);

    public static GrepOutcome OutcomeWith(int hits, int errors = 0, bool cancelled = false)
    {
        var hs = new GrepHit[hits];
        for (int i = 0; i < hits; i++)
            hs[i] = new GrepHit("C:/fake/x.txt", i + 1, 1, "line", 0, 1, i);
        var es = new GrepError[errors];
        for (int i = 0; i < errors; i++)
            es[i] = new GrepError("C:/fake/y.txt", "err");
        return new GrepOutcome(hs, hits, hits > 0 ? 1 : 0, es, cancelled);
    }
}
