namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IReachabilityProbe"/> のテスト用フェイク。既定は Result=true(ローカル/正常 UNC は通過)。
/// HIGH-6 の UNC プローブ経路(<c>ProbeWithTimeout</c>)呼び出し回数と、
/// 呼出時に FileController が渡したタイムアウト値(5 秒契約)の pin に使う。
/// </summary>
public sealed class FakeReachabilityProbe : IReachabilityProbe
{
    public bool Result { get; set; } = true;
    public int CallCount { get; private set; }

    /// <summary>直近の <c>ProbeWithTimeout</c> 呼出で渡された timeout。
    /// FileController が 5s → 5min のような mutation を起こしていないか固定するための観測点。</summary>
    public TimeSpan LastTimeout { get; private set; }

    public bool ProbeWithTimeout(string path, TimeSpan timeout)
    {
        CallCount++;
        LastTimeout = timeout;
        return Result;
    }
}
