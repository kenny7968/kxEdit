namespace kxEdit.App;

/// <summary>
/// 保存先を 1 回の境界付き I/O で調べた結果(A-4 / A-7)。
/// <paramref name="Reachable"/> = 書き込み先が確定できる(ファイルが在る、または親フォルダーが在る)。
/// <paramref name="FileExists"/> = 上書きになる。タイムアウト時は (false, false)。
/// </summary>
public readonly record struct SaveTargetProbe(bool Reachable, bool FileExists);

/// <summary>
/// パスへの到達可否を短時間で判定する DI シーム(HIGH-6)。
/// 本番は <see cref="FileReachabilityProbe"/> / テストは Fake を差し込む。
/// UNC ロード時の 60 秒 UI 凍結を 5 秒プローブで回避するために FileController が使う。
/// </summary>
public interface IReachabilityProbe
{
    /// <summary>到達確認済 = true / タイムアウトまたは到達不可 = false。**読み取り側専用**。</summary>
    bool ProbeWithTimeout(string path, TimeSpan timeout);

    /// <summary>
    /// 保存先の到達性と既存有無を 1 回の境界付き I/O で得る(A-4 / A-7)。
    /// <see cref="ProbeWithTimeout"/> は File.Exists 意味論なので、存在しない新規パスを
    /// 到達不能と誤判定する(= A-4 の機構)。**書き込み側はこちらを使う**。
    /// 2 つの述語を 1 タスクにまとめてあるのは、遠隔共有での待ちを 5 秒 1 回に収めるため。
    /// </summary>
    SaveTargetProbe ProbeSaveTargetWithTimeout(string path, TimeSpan timeout);
}
