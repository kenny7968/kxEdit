namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IReachabilityProbe"/> のテスト用フェイク。4 メンバーとも、呼び出し回数と
/// 呼出側が渡したタイムアウト値(5 秒契約)を pin するための観測点を持つ。
/// <list type="bullet">
/// <item><c>ProbeFileExistsWithTimeout</c> — 既定 <see cref="Result"/>=true
/// (ローカル / 正常 UNC は通過)。HIGH-6 の UNC プローブ経路の pin。</item>
/// <item><c>ProbeDirectoryExistsWithTimeout</c> — 既定 <see cref="DirectoryResult"/>=true
/// (到達できるフォルダー = grep が本体へ進む形)。A-17 のフォルダープローブ経路の pin。</item>
/// <item><c>ProbeSaveTargetWithTimeout</c> — 既定は「到達可能・未存在」= 新規保存が通る形(A-4)。</item>
/// <item><c>NormalizePathWithTimeout</c> — 既定は<b>実装への委譲</b>(素通しではない)。
/// 理由は <see cref="NormalizeResult"/> のコメント(Issue #48)。</item>
/// </list>
/// </summary>
public sealed class FakeReachabilityProbe : IReachabilityProbe
{
    /// <summary>
    /// <c>NormalizePathWithTimeout</c> の既定応答を作る実装。状態を持たないので使い回す
    /// (呼び出しごとに new しない)。
    /// </summary>
    private static readonly FileReachabilityProbe RealProbe = new();

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
    /// <c>ProbeDirectoryExistsWithTimeout</c> の応答。既定は true(到達できるフォルダー)。
    /// <see cref="Result"/> とは**独立**に設定できる必要がある(ファイルは在るがフォルダーは不達、
    /// の形を作れるように)。
    /// </summary>
    public bool DirectoryResult { get; set; } = true;

    public int DirectoryCallCount { get; private set; }

    /// <summary>直近の <c>ProbeDirectoryExistsWithTimeout</c> 呼出で渡された path。</summary>
    public string? DirectoryLastPath { get; private set; }

    /// <summary>直近の <c>ProbeDirectoryExistsWithTimeout</c> 呼出で渡された timeout(5s 契約の pin)。</summary>
    public TimeSpan DirectoryLastTimeout { get; private set; }

    public bool ProbeDirectoryExistsWithTimeout(string path, TimeSpan timeout)
    {
        DirectoryCallCount++;
        DirectoryLastPath = path;
        DirectoryLastTimeout = timeout;
        return DirectoryResult;
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

    /// <summary>
    /// <c>NormalizePathWithTimeout</c> の応答。null のときは実装 <see cref="FileReachabilityProbe"/>
    /// へ委譲し、非 null ならその固定値を返す。
    /// </summary>
    public PathNormalizeResult? NormalizeResult { get; set; }

    public int NormalizeCallCount { get; private set; }

    /// <summary>直近の <c>NormalizePathWithTimeout</c> 呼出で渡された path。</summary>
    public string? NormalizeLastPath { get; private set; }

    /// <summary>直近の <c>NormalizePathWithTimeout</c> 呼出で渡された timeout(5s 契約の pin)。</summary>
    public TimeSpan NormalizeLastTimeout { get; private set; }

    public PathNormalizeResult NormalizePathWithTimeout(string path, TimeSpan timeout)
    {
        NormalizeCallCount++;
        NormalizeLastPath = path;
        NormalizeLastTimeout = timeout;
        // 既定は「実 GetFullPath と同じ答え」を返す。Fake が素通し(path をそのまま返す)だと
        // 相対パス入力のテストが「正規化されたつもり」で通ってしまい、A-19 の網が
        // vacuous になる(PR #47 の教訓: Fake を注入するテストは本番実装の性質を証人にできない)。
        return NormalizeResult ?? RealProbe.NormalizePathWithTimeout(path, timeout);
    }
}
