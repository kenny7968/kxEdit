using System.IO;
using System.Runtime.ExceptionServices;

namespace kxEdit.App;

/// <summary>
/// <see cref="IReachabilityProbe"/> の本番実装。読み取り側の
/// <see cref="ProbeFileExistsWithTimeout"/> は <see cref="System.IO.File.Exists"/> を、
/// 書き込み側の <see cref="ProbeSaveTargetWithTimeout"/> は File.Exists + 親フォルダーの
/// <see cref="System.IO.Directory.Exists"/> を、正規化の
/// <see cref="NormalizePathWithTimeout"/> は <see cref="System.IO.Path.GetFullPath(string)"/> を、
/// それぞれ <see cref="Task.Run{TResult}(Func{TResult})"/> で
/// バックグラウンドスレッドに退避し、<see cref="Task.Wait(TimeSpan)"/> の短タイムアウトで
/// UI スレッドをブロックしない。3 本ともタイムアウト時のフェイルセーフは
/// <see cref="WaitBounded{T}"/> に集約する(= 「確定しなかった」側に倒す。前 2 本は到達不能、
/// 正規化は <see cref="PathNormalizeStatus.TimedOut"/>)。
/// <see cref="System.IO.Path.GetFullPath(string)"/> は名前解決のみで実 I/O を行わない —
/// **ただし正規化後のパスに <c>~</c> が含まれる場合だけは例外**で <c>GetLongPathName</c> を
/// 呼ぶため、不達の共有に対して約 21 秒ブロックする(Issue #48 / S-15。だからここに退避する)。
/// <b>スレッド leak の受容(Issue #48 コード品質レビュー I-5)</b>: 不達先では退避したスレッドが
/// 本体の完了(SMB 60 秒 / <c>GetLongPathName</c> 約 21 秒)まで残る。「1 本」で済むのは単発操作の
/// ときだけで、復元経路のようにパスを次々に処理すると直列に積み上がる。1 件あたり
/// 「5 秒 UI ブロック + 寿命 21〜60 秒の leak 1 本」なので、同時 leak は 寿命 ÷ 間隔 ≒ 21/5 ≒
/// 5 本(SMB 60 秒なら ≒ 12 本)。<see cref="ProbeFileExistsWithTimeout"/> の leak も重なるため
/// 最悪で約 24 本。<c>ThreadPool</c> の最小ワーカー数は <c>Environment.ProcessorCount</c> と
/// 同数(実測した開発機では 16)なので、最悪ケースはこれを超える。枯渇はしない(上限は 32767)が、
/// 超えた分は投入が絞られるので待たされうる。
/// それでも受容する: <c>TaskCreationOptions.LongRunning</c> へ替えると、圧倒的多数を占める
/// ローカルの高速経路でも毎回 OS スレッドを起こす代償を払うことになる。
/// (設計書 PR-5 節の「1 本」という受容を、正規化を足した現状の算術で更新した。)
/// </summary>
public sealed class FileReachabilityProbe : IReachabilityProbe
{
    /// <summary>
    /// 境界付き待ちの判断。タイムアウトしたら <paramref name="onTimeout"/> を返す。
    /// 3 つの probe メソッドでフェイルセーフ規律を 1 箇所に集約するために切り出す
    /// (Task 1 コード品質レビュー I-1: タイムアウト値の変異が生存していた)。
    /// <b>fault は元の例外型のまま投げ直す</b>(Issue #48 コード品質レビュー I-6)。
    /// faulted task に対する実測(net9.0.8)は次のとおり:
    /// <c>Wait()</c> / <c>Wait(TimeSpan)</c> / <c>Result</c> は**いずれも**中身を
    /// <see cref="AggregateException"/> で包んで投げ、包まずに投げるのは
    /// <c>GetAwaiter().GetResult()</c> だけ。それでも
    /// <c>Wait(timeout) ? task.Result : onTimeout</c> の <c>Result</c> 側だけを
    /// <c>GetAwaiter().GetResult()</c> へ替えても**直らない** — 三項の条件である
    /// <c>Wait</c> が先に評価され、そこで包んで投げるので <c>Result</c> 側に制御が届かないから。
    /// (**「包むのは <c>Result</c> ではなく <c>Wait</c> のほう」と書いていたのは誤り**だった。
    /// 両方が包む。差し替えが効かない理由は<b>評価順</b>であって、どちらが包むかではない。)
    /// <see cref="ExceptionDispatchInfo"/> で中身を投げ直せば型も<b>スタックも</b>保たれる
    /// (素の <c>throw ex.InnerExceptions[0];</c> ではスタックが投げ直し地点にリセットされ、
    /// work 内のバグ地点が消える。実測で確認)。これにより
    /// <see cref="NormalizePathWithTimeout"/> のフィルタ外例外(=ロジックバグ)が、
    /// 移設前(<c>FileController</c> が直接 <c>GetFullPath</c> を呼んでいた頃)と
    /// 同じ姿で診断できる。
    /// <b>投げ直しは本メソッドの契約であって、呼出側の性質ではない</b>(再レビュー m-1):
    /// <see cref="ProbeFileExistsWithTimeout"/> / <see cref="ProbeSaveTargetWithTimeout"/> の
    /// work は素の <c>catch</c> で何も逃がさない=task が fault しないので、
    /// <b>この 2 つの呼出側については挙動不変</b>。ただし
    /// <see cref="RunSaveTargetProbe"/> / <see cref="RunFileExistsProbe"/> は internal で
    /// 任意の work を受けるため、throw しうる work を渡せばここが効く。
    /// </summary>
    internal static T WaitBounded<T>(Task<T> task, TimeSpan timeout, T onTimeout)
    {
        try
        {
            return task.Wait(timeout) ? task.Result : onTimeout;
        }
        // Count == 1 に絞るのは、複数例外を 1 本へ潰して情報を捨てないため
        // (Task.Run(work) 由来の fault は常に 1 本)。
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            throw; // 到達しない。Throw() が必ず投げるが、C# のフロー解析を満たすために要る。
        }
    }

    /// <summary>
    /// 保存先プローブの骨格。<paramref name="work"/> をバックグラウンドへ退避し、
    /// 期限内に終わらなければ「到達不能」(false, false)へ倒す。
    /// <see cref="WaitBounded{T}"/> が generic でフェイルセーフ値を持てない以上、
    /// その値は呼出側に置くしかない。ここに置くのは <paramref name="work"/> を差し替えれば
    /// タイムアウト経路を**決定的に**テストできるからで、実 I/O 経由では
    /// フェイルセーフ値そのものが無被覆のまま残る(I-1 の実測: 変異が生存していた)。
    /// </summary>
    internal static SaveTargetProbeResult RunSaveTargetProbe(
        Func<SaveTargetProbeResult> work,
        TimeSpan timeout
    ) => WaitBounded(Task.Run(work), timeout, new SaveTargetProbeResult(false, false));

    /// <summary>
    /// 読み取り側プローブの骨格。<paramref name="work"/> をバックグラウンドへ退避し、
    /// 期限内に終わらなければ「存在を確認できなかった」= false へ倒す。
    /// 保存側の <see cref="RunSaveTargetProbe"/> と対称に切り出すのは、フェイルセーフ値を
    /// テストが届く場所へ置くため(再レビュー I-3): 素の
    /// <c>WaitBounded(task, timeout, false)</c> では定数が 1 トークンの引数でしかなく、
    /// true へ書き換えてもコンパイルが通り・ハングもせず・全緑になってしまう
    /// (= タイムアウトを「ファイルは在る」と読み、切断済み UNC で実 read へ進んで
    /// UI が 60 秒凍結する HIGH-6 の再導入)。
    /// </summary>
    internal static bool RunFileExistsProbe(Func<bool> work, TimeSpan timeout) =>
        WaitBounded(Task.Run(work), timeout, false);

    /// <summary>
    /// 境界付き正規化の骨格。<paramref name="work"/> をバックグラウンドへ退避し、
    /// 期限内に終わらなければ「確定しなかった」= <see cref="PathNormalizeStatus.TimedOut"/> へ倒す。
    /// フェイルセーフ値をここに置く理由は <see cref="RunFileExistsProbe"/> と同じ:
    /// <c>WaitBounded(task, timeout, 定数)</c> と直書きすると定数が 1 トークンの引数でしかなく、
    /// <see cref="PathNormalizeStatus.Ok"/> へ書き換えてもコンパイルが通り・ハングもせず・
    /// 全緑になってしまう(= タイムアウトを「正規化できた」と読み、空文字のパスを
    /// 保存先として採用する)。
    /// </summary>
    internal static PathNormalizeResult RunNormalizeProbe(
        Func<PathNormalizeResult> work,
        TimeSpan timeout
    ) =>
        WaitBounded(
            Task.Run(work),
            timeout,
            new PathNormalizeResult(PathNormalizeStatus.TimedOut, string.Empty)
        );

    /// <inheritdoc />
    public bool ProbeFileExistsWithTimeout(string path, TimeSpan timeout) =>
        RunFileExistsProbe(
            () =>
            {
                try
                {
                    return File.Exists(path);
                }
                catch
                {
                    // File.Exists は通常例外を投げないが、UNC 未到達などで
                    // 稀に IOException 系が出る可能性を吸って false 扱いにする。
                    return false;
                }
            },
            timeout
        );

    /// <inheritdoc />
    public SaveTargetProbeResult ProbeSaveTargetWithTimeout(string path, TimeSpan timeout) =>
        RunSaveTargetProbe(
            () =>
            {
                try
                {
                    bool fileExists = File.Exists(path);
                    string? dir = Path.GetDirectoryName(path);
                    // dir が null = ルート自体(C:\ / \\server\share)= 親が無い。
                    // dir が空 = 相対パス(呼出側は正規化済み絶対パスを渡す契約)。
                    // どちらも書き込み先が確定しないので到達不能へ倒す。
                    bool dirExists = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
                    return new SaveTargetProbeResult(fileExists || dirExists, fileExists);
                }
                catch
                {
                    // File.Exists / Directory.Exists は通常投げないが、UNC 未到達などで稀に
                    // IOException 系が出る可能性を吸って「到達不能」に倒す
                    // (ProbeFileExistsWithTimeout と同方針)。
                    return new SaveTargetProbeResult(false, false);
                }
            },
            timeout
        );

    /// <inheritdoc />
    /// <remarks>
    /// <b>フィルタの実測(net9.0.8)</b> — 移設元 <c>FileController.TryNormalizeSavePath</c> の
    /// 記録を引き継ぐ。<see cref="Path.GetFullPath(string)"/> が投げるのは実質
    /// (a) NUL 文字混入 → <see cref="ArgumentException"/>、
    /// (b) 空 / 空白のみ → <see cref="ArgumentException"/>、
    /// (c) 総長超過 → 下の V-2 の表、の 3 つだけ。
    /// <c>&lt;</c> <c>|</c> <c>"</c> などの「無効文字」・予約デバイス名(CON / NUL)・
    /// 拡張ルート(<c>\\?\</c>)は<b>投げずに素通りする</b>ので、このフィルタは無効文字の門番ではない
    /// (実測: <c>GetFullPath("CON")</c> → <c>\\.\CON</c>、<c>GetFullPath(@"\\?\")</c> →
    /// <c>\\?\</c> がどちらも <see cref="PathNormalizeStatus.Ok"/> で返る)。
    /// つまり <c>Ok</c> は<b>「文字列として正規化できた」以上の意味を持たない</b>。
    /// 書き込み先が確定するか(デバイス名・ドライブルート・共有ルートを弾く)は呼出側の
    /// 「親フォルダーが取れるか」ガード(#47 の V-1)が別途見る契約で、
    /// <b>境界付きにしてもこのガードは正規化の後ろに残さなければならない</b>。
    /// <para>
    /// <b>V-2(#47 の脆弱性レビューで解消)の実測マップ</b> — 相対入力の文字数 → 例外型:
    /// <list type="bullet">
    /// <item>32765 / 32766 → 素の <see cref="IOException"/>(<c>is PathTooLongException</c> は false)</item>
    /// <item>32767 / 40000 → <see cref="PathTooLongException"/></item>
    /// </list>
    /// 前者は総長が 32767 の直下に収まる窓で、<c>GetFullPathNameW</c> が ERROR_INVALID_NAME を
    /// 返すために起きる。派生関係は一方向なので <c>PathTooLongException</c> だけを列挙すると
    /// この窓が抜けて未捕捉例外ダイアログになる。設計書 §4.3 の列挙は実測と食い違っていたため
    /// <see cref="IOException"/>(厳密な上位集合)へ広げてある。
    /// <b>この窓の上端は入力長だけで決まり CWD 長に依存しない</b>(総長 = CWD + 1 + 入力長 なので、
    /// 入力長 32766 ならどんな CWD でも 32767 を超える)。「CWD 長に依存する fixture になるので
    /// 自動テストにできない」と書いていたのは誤りで、網は
    /// <c>FileReachabilityProbeTests.NormalizePath_OverLongPath_ReturnsInvalid</c> の
    /// <c>[Theory]</c> にある。
    /// </para>
    /// </remarks>
    public PathNormalizeResult NormalizePathWithTimeout(string path, TimeSpan timeout) =>
        RunNormalizeProbe(
            () =>
            {
                try
                {
                    return new PathNormalizeResult(PathNormalizeStatus.Ok, Path.GetFullPath(path));
                }
                // フィルタは FileController.TryNormalizeSavePath から**そのまま移設**した。
                // PR #47 の V-2 対策(素の IOException の窓)を落とさないこと。
                // どの入力がどの例外型を投げるかの実測マップは本メソッドの remarks にある。
                //
                // **既存 2 本と違って素の catch を後ろに置かないのは意図的**(I-6)。
                // 素の catch を足すと、予期しない例外型=ロジックバグまで「パスが正しくありません」
                // へ黙って変換され、原因が永久に見えなくなる。移設元 TryNormalizeSavePath が
                // 絞り込みフィルタだったのはこの規律のためで、移設先でもそのまま保つ。
                // フィルタ外の例外は WaitBounded が元の型のまま投げ直す(AggregateException に
                // 包まない)ので、移設前と同じ姿で診断できる。
                catch (Exception ex)
                    when (ex
                            is ArgumentException
                                or NotSupportedException
                                or IOException
                                or System.Security.SecurityException
                    )
                {
                    return new PathNormalizeResult(PathNormalizeStatus.Invalid, string.Empty);
                }
            },
            timeout
        );
}
