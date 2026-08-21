using kxEdit.Core.Search;

namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IGrepResultsView"/> のテスト用フェイク。Populate/ShowResults の呼び出しを記録し、
/// コールバック(<see cref="GrepResultsCallbacks.OnActivate"/>)を <see cref="FireActivate"/> で発火できる
/// (ジャンプ配線の対応固定用)。IsDisposed はテストが直接設定して再生成分岐を再現。
/// </summary>
public sealed class FakeGrepResultsView : IGrepResultsView
{
    private readonly GrepResultsCallbacks _cb;

    public FakeGrepResultsView(GrepResultsCallbacks callbacks)
    {
        _cb = callbacks;
    }

    public bool IsDisposed { get; set; }

    public List<(string Pattern, string Folder, GrepOutcome Outcome)> PopulateLog { get; } = new();
    public int ShowResultsCount;

    public void Populate(string pattern, string folder, GrepOutcome outcome) =>
        PopulateLog.Add((pattern, folder, outcome));

    public void ShowResults(IWin32Window owner) => ShowResultsCount++;

    /// <summary>結果一覧の「アクティベート」相当を発火(<see cref="GrepResultsCallbacks.OnActivate"/> 経由で
    /// 登録先=テストでは Host.Jumps.Add に届くかを検証)。</summary>
    public void FireActivate(GrepHit hit) => _cb.OnActivate(hit);
}
