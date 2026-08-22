namespace kxEdit.App;

/// <summary>
/// 保存先を 1 回の境界付き I/O で調べた結果(A-4 / A-7)。
/// <c>Reachable</c> が false のとき <c>FileExists</c> は情報を持たない
/// (未存在とタイムアウトを区別できない)。必ず <c>Reachable</c> を先に見て短絡すること。
/// タイムアウト時は (false, false)。
/// </summary>
/// <param name="Reachable">書き込み先が確定できる(ファイルが在る、または親フォルダーが在る)。</param>
/// <param name="FileExists">
/// 上書きになる。<paramref name="Reachable"/> が false のときは無意味。
/// </param>
public readonly record struct SaveTargetProbeResult(bool Reachable, bool FileExists);

/// <summary>
/// パスへの到達可否を短時間で判定する DI シーム(HIGH-6)。
/// 本番は <see cref="FileReachabilityProbe"/> / テストは Fake を差し込む。
/// UNC ロード時の 60 秒 UI 凍結を 5 秒プローブで回避するために FileController が使う。
/// どちらのメソッドも、呼出側が**正規化済みの絶対パス**を渡す契約
/// (相対パスは親フォルダーが空文字になるため到達不能へ倒れる)。
/// </summary>
public interface IReachabilityProbe
{
    /// <summary>
    /// 既存ファイルの存在を境界付きで確認する。存在を確認できた = true /
    /// タイムアウト・到達不可・未存在 = false。**読み取り側専用**:
    /// 「未存在」と「到達不能」を区別しないので保存先の判定には使えない(= A-4 の機構)。
    /// </summary>
    bool ProbeFileExistsWithTimeout(string path, TimeSpan timeout);

    /// <summary>
    /// 保存先の到達性と既存有無を 1 回の境界付き I/O で得る(A-4 / A-7)。
    /// <see cref="ProbeFileExistsWithTimeout"/> は File.Exists 意味論なので、存在しない新規パスを
    /// 到達不能と誤判定する(= A-4 の機構)。**書き込み側はこちらを使う**。
    /// 2 つの述語を 1 タスクにまとめてあるのは、遠隔共有での待ちを 5 秒 1 回に収めるため。
    /// </summary>
    SaveTargetProbeResult ProbeSaveTargetWithTimeout(string path, TimeSpan timeout);
}
