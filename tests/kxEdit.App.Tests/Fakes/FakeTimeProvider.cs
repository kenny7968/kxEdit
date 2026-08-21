namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// テスト用の <see cref="TimeProvider"/>。GetUtcNow を固定値で返し、
/// <see cref="Advance(TimeSpan)"/> で明示的に進められる。NuGet 依存を増やさない自作 6 行。
/// タイマー(CreateTimer)は本 Stage では使わない(Reconcile を internal 直呼びするため)。
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
