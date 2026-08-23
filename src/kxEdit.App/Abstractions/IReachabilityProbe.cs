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
/// 境界付き正規化の結果状態(Issue #48 / 設計書 §4)。
/// <b>ゼロ値をフェイルセーフ側に置いてある</b>: 初期化漏れや <c>default</c> が
/// 「正規化できた」に転ばないようにするため、<see cref="TimedOut"/> を 0 にする。
/// </summary>
public enum PathNormalizeStatus
{
    /// <summary>期限内に終わらなかった。パスは確定していない。</summary>
    TimedOut = 0,

    /// <summary>入力が正規化できない(NUL 混入・総長超過など)。</summary>
    Invalid = 1,

    /// <summary>正規化できた。</summary>
    Ok = 2,
}

/// <summary>
/// 境界付き正規化の結果(Issue #48 / 設計書 §4)。
/// <c>Status</c> が <see cref="PathNormalizeStatus.Ok"/> のときだけ <c>Full</c> が意味を持つ。
/// それ以外は空文字。呼出側は必ず <c>Status</c> で分岐すること。
/// <b>兄弟の <see cref="SaveTargetProbeResult"/> と同じく、<c>default</c> でも全メンバーが
/// フェイルセーフ値になる</b>: <c>Status</c> はゼロ値が <see cref="PathNormalizeStatus.TimedOut"/>、
/// <c>Full</c> は backing field が null のとき空文字を返す。positional record
/// (<c>record struct X(Status, string Full)</c>)では <c>default</c> の <c>Full</c> が
/// <b>null</b> になり、「ゼロ値をフェイルセーフ側に置く」原則から <c>Full</c> だけが外れる
/// ため、手書きのプロパティにしてある(消費側は <c>SanitizeForDisplay.OneLine(...)</c> と
/// <c>State.Path = ...</c> なので、null が漏れると NRE か「Path が null の無題タブ」になる)。
/// 等値比較も<b>公開プロパティ基準</b>にしてあるので、
/// <c>default == new PathNormalizeResult(TimedOut, string.Empty)</c> は true になる。
/// </summary>
public readonly record struct PathNormalizeResult
{
    private readonly string? _full;

    /// <summary>結果を組み立てる。</summary>
    /// <param name="status">結果状態。</param>
    /// <param name="full">正規化済み絶対パス(Ok のときのみ)。</param>
    public PathNormalizeResult(PathNormalizeStatus status, string full) =>
        (Status, _full) = (status, full);

    /// <summary>結果状態。ゼロ値は <see cref="PathNormalizeStatus.TimedOut"/>。</summary>
    public PathNormalizeStatus Status { get; }

    /// <summary>
    /// 正規化済み絶対パス(<see cref="PathNormalizeStatus.Ok"/> のときのみ意味を持つ)。
    /// <c>default</c> でも null にならない。
    /// </summary>
    public string Full => _full ?? string.Empty;

    // record struct の自動生成 Equals は公開プロパティではなく backing field(_full)を見るため、
    // 「同じフェイルセーフ値」である default と new(TimedOut, string.Empty) が等値にならない
    // (null vs "")。これは positional 版でも同じ挙動で、手書き化の代償ではない(実測)。
    // ワナを doc の但し書きで回避するのではなく、公開プロパティ基準の等値を手で書いて型ごと消す。
    public bool Equals(PathNormalizeResult other) => Status == other.Status && Full == other.Full;

    public override int GetHashCode() => HashCode.Combine(Status, Full);
}

/// <summary>
/// パスへの到達可否を短時間で判定し(HIGH-6)、パスの正規化そのものにも境界を張る
/// (Issue #48 / S-15)DI シーム。
/// 本番は <see cref="FileReachabilityProbe"/> / テストは Fake を差し込む。
/// UNC ロード時の 60 秒 UI 凍結を 5 秒プローブで回避するために FileController が使う。
/// <b>プローブ 2 本</b>(<see cref="ProbeFileExistsWithTimeout"/> /
/// <see cref="ProbeSaveTargetWithTimeout"/>)は、呼出側が**正規化済みの絶対パス**を渡す契約。
/// <see cref="NormalizePathWithTimeout"/> だけはこの契約の**外**にある(その正規化を作るのが
/// 仕事なので、生パスを受け取る)。
/// <b>合成規則</b>: <see cref="NormalizePathWithTimeout"/> の出力(<c>Full</c>)を
/// プローブ 2 本の入力に渡す。これが 3 本の関係のすべてであり、
/// 「1 操作あたり正規化<b>多くとも</b> 1 本」(設計書 §3 の不変条件)はこの向きにしか成立しない。
/// 「ちょうど 1 本」ではない: Ctrl+S は <c>State.Path</c> が正規化済みという不変条件により
/// <b>0 本</b>で済む(<c>SaveDocument_ExistingPath_DoesNotNormalizeAtAll</c> が 0 を pin する)。
/// 理由は**両者共通で 1 つだけ**: 相対パスを渡すと内部の <c>File.Exists</c> /
/// <c>Directory.Exists</c> がプロセスの CWD 基準で解決するので、例外も「到達不能」も返さずに
/// **黙って別のファイルを指す**(= A-19 が正規化を要求する理由)。
/// 実測(2026-08-23。CWD に <c>memo.txt</c> と <c>sub\memo.txt</c> を置いた状態):
/// <c>ProbeSaveTargetWithTimeout("memo.txt")</c> → (Reachable: true, FileExists: true)、
/// <c>("nope.txt")</c> → (false, false)、<c>("sub\nope.txt")</c> → (true, false)。
/// <b>「保存側だけは親フォルダーが空文字になって到達不能へ倒れるので失敗が見える」と書いていたのは誤り。</b>
/// 空文字になるのはディレクトリー成分を持たない相対入力に限られ(<c>sub\...</c> では
/// <c>GetDirectoryName</c> が <c>"sub"</c> を返すので機構自体が発火しない)、しかもそのときは
/// CWD に同名ファイルが無い場合=読み取り側が返す false と区別がつかない。**非対称は無い。**
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

    /// <summary>
    /// パスを境界付きで正規化する(Issue #48 / S-15)。
    /// <c>Path.GetFullPath</c> は正規化後のパスに <c>~</c> が含まれると
    /// <c>GetLongPathName</c> を呼び、不達の共有に対して約 21 秒 UI を止める。
    /// <b>UI スレッドから正規化するときは必ずこれを通す。</b>
    /// この 1 本だけは<b>正規化前の生パスを渡してよい</b>(他の 2 本と契約が違う。
    /// 正規化そのものが仕事なので)。
    /// </summary>
    PathNormalizeResult NormalizePathWithTimeout(string path, TimeSpan timeout);
}
