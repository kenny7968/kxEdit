namespace kxEdit.App;

/// <summary>
/// パスへの到達可否を短時間で判定する DI シーム(HIGH-6)。
/// 本番は <see cref="FileReachabilityProbe"/> / テストは Fake を差し込む。
/// UNC ロード時の 60 秒 UI 凍結を 5 秒プローブで回避するために FileController が使う。
/// </summary>
public interface IReachabilityProbe
{
    /// <summary>到達確認済 = true / タイムアウトまたは到達不可 = false。</summary>
    bool ProbeWithTimeout(string path, TimeSpan timeout);
}
