namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IReachabilityProbe"/> のテスト用フェイク。既定は Result=true(ローカル/正常 UNC は通過)。
/// HIGH-6 の UNC プローブ経路(<c>ProbeFileExistsWithTimeout</c>)呼び出し回数と、
/// 呼出時に FileController が渡したタイムアウト値(5 秒契約)の pin に使う。
/// </summary>
public sealed class FakeReachabilityProbe : IReachabilityProbe
{
    public bool Result { get; set; } = true;
    public int CallCount { get; private set; }

    /// <summary>直近の <c>ProbeFileExistsWithTimeout</c> 呼出で渡された timeout。
    /// FileController が 5s → 5min のような mutation を起こしていないか固定するための観測点。</summary>
    public TimeSpan LastTimeout { get; private set; }

    public bool ProbeFileExistsWithTimeout(string path, TimeSpan timeout)
    {
        CallCount++;
        LastTimeout = timeout;
        return Result;
    }

    /// <summary>
    /// <c>ProbeSaveTargetWithTimeout</c> の応答。既定は「到達可能・未存在」= 新規保存が通る形。
    /// 旧 <see cref="Result"/>(bool)とは**独立**に設定できる必要がある: 同値に縛ると
    /// A-4 の本質(到達可能かつ非存在)を表現できない。
    /// </summary>
    public SaveTargetProbeResult SaveTargetResult { get; set; } =
        new(Reachable: true, FileExists: false);

    public int SaveTargetCallCount { get; private set; }

    /// <summary>直近の <c>ProbeSaveTargetWithTimeout</c> 呼出で渡された timeout(5s 契約の pin)。</summary>
    public TimeSpan SaveTargetLastTimeout { get; private set; }

    public SaveTargetProbeResult ProbeSaveTargetWithTimeout(string path, TimeSpan timeout)
    {
        SaveTargetCallCount++;
        SaveTargetLastTimeout = timeout;
        return SaveTargetResult;
    }
}
