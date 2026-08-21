using kxEdit.Core.Search;

namespace kxEdit.App;

/// <summary>
/// GrepResultsWindow 生成時に渡すコールバック束(Phase 2 Stage 7・上位文書 §2.2)。
/// 結果一覧のアクティベート(Enter/ダブルクリック)からジャンプ動作への 1 経路を delegate 化する。
/// Stage 8 Task C 以降は結果窓生成側(MainForm)が組み立てる=<see cref="GrepController"/> はジャンプ経路を知らない。
/// GrepCallbacks と対称。
/// </summary>
public sealed record GrepResultsCallbacks(Action<GrepHit> OnActivate);

/// <summary>
/// GrepResultsWindow の Controller 向け表面(Phase 2 設計書 §2.2)。
/// Populate は結果流し込み・ShowResults はモードレス表示。IsDisposed は再生成判定
/// (owner クローズ等での破棄検出)を従来コードのまま保存するために載せる。
/// </summary>
public interface IGrepResultsView
{
    bool IsDisposed { get; }
    void Populate(string pattern, string folder, GrepOutcome outcome);
    void ShowResults(IWin32Window owner);
}
