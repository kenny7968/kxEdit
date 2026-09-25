using System.IO;

namespace kxEdit.App;

/// <summary>
/// MD-M-4: MarkdownPreviewForm 用の per-form WebView2 UserDataFolder。
/// %LOCALAPPDATA%\kxEdit\WebView2\preview-{guid}\ に一時ディレクトリを作り、
/// フォーム破棄時にディレクトリごと削除して残骸を残さない。
/// <para>
/// 副次効果として、複数プレビューを同時に開いた際の WebView2 プロファイル
/// ロック競合 (先発 WebView が握るファイルロックで後続の起動が失敗する) を解消する。
/// </para>
/// <para>
/// 性能改善フェーズ 7(P-21(a)): 削除は背景で行う(UI スレッドで再帰削除を待たない)。
/// WebView2 のブラウザプロセスは非同期に終了し、それまでプロファイルを掴んでいるので、
/// 短い間隔で数回リトライする。最後まで消せなければ Trace 警告を残して諦め、次回起動の
/// <see cref="PreviewUserDataSweeper"/> に任せる。アプリの終了で背景の削除が打ち切られた場合も同じ。
/// </para>
/// <para>
/// App 層内部にのみ露出するため <c>internal sealed</c>。テストは
/// <c>InternalsVisibleTo kxEdit.App.Tests</c> 経由でアクセスする。
/// </para>
/// </summary>
internal sealed class PreviewUserDataFolder : IDisposable
{
    /// <summary>削除のリトライの間隔(P-21(a))。合計 1.5 秒待っても消せなければ、次回起動の sweeper に任せる。</summary>
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
    ];

    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private int _disposed;

    /// <summary>WebView2 の <c>userDataFolder</c> に渡す絶対パス。</summary>
    public string Path { get; }

    /// <summary>背景の削除(<see cref="Dispose"/> の前は null)。テストが完了を待つための観測点。</summary>
    internal Task? DeletionTask { get; private set; }

    public PreviewUserDataFolder()
        : this(PreviewUserDataSweeper.DefaultRoot, DefaultRetryDelays) { }

    /// <summary>テスト用: 親フォルダーとリトライの間隔を差し替える。</summary>
    internal PreviewUserDataFolder(string parentDir, IReadOnlyList<TimeSpan> retryDelays)
    {
        _retryDelays = retryDelays;
        // Guid.NewGuid().ToString("N") = 32 桁小文字 hex (ハイフン無し)。
        // ファイルシステム安全かつ per-form 一意性を担保。
        Path = System.IO.Path.Combine(parentDir, "preview-" + Guid.NewGuid().ToString("N"));
        // idempotent: 既存でも throw しない。
        System.IO.Directory.CreateDirectory(Path);
    }

    /// <summary>
    /// V-2: 仮想ホストのマッピング先にする空フォルダー(<c>{Path}\empty-base</c>)を作って返す。
    /// <para>
    /// <b>契約: このフォルダーには何も置かない。</b> マッピング専用であり、ここに置いた
    /// ファイルは <c>https://kxedit.preview/</c> でプレビューから読める。
    /// </para>
    /// <para>
    /// WebView2 のプロファイル実体は <see cref="Path"/> 直下に作られるので、マッピング先を
    /// サブフォルダーにして<b>プロファイルを直接のルートにはしない</b>。ただし
    /// <c>..%2f</c> 等のエスケープ密輸でこのフォルダーの外(= 親のプロファイル)へ
    /// 出られるかは<b>未確認</b>(監査 V-3 / L5 項目 1 で確認する)。WebView2 が
    /// <c>%2f</c> をパス区切りとしてデコードするなら届きうるため、<b>「プロファイルは
    /// 露出しない」と断定できる実測はまだ無い</b>。
    /// 置き場所を変えても <c>..</c> の反復回数が変わるだけで traversal の成否は変わらないため、
    /// 対処は V-3 側(密輸そのものを塞ぐ)で行う。
    /// </para>
    /// <para>後始末は <see cref="Dispose"/> が親ごと消すので専用の経路を持たない。</para>
    /// </summary>
    public string EnsureEmptyBaseFolder()
    {
        string path = System.IO.Path.Combine(Path, "empty-base");
        System.IO.Directory.CreateDirectory(path); // idempotent: 既存でも throw しない
        return path;
    }

    public void Dispose()
    {
        // 2 回目以降は何もしない(削除を二重に投げない)。
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        // 削除するのは ctor で自分が作った Path だけ(不変・外部入力を含まない)。
        DeletionTask = DeleteWithRetryAsync(Path, _retryDelays);
    }

    /// <summary>
    /// <paramref name="path"/> を再帰削除する。<see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/> なら <paramref name="retryDelays"/> の間隔で再試行し、
    /// 使い切ったら Trace 警告を残して諦める(例外は外へ出さない)。
    /// <see cref="System.IO.Directory.Delete(string, bool)"/> はリパースポイント(ジャンクション・
    /// シンボリックリンク)の先を辿らず、リンク自体だけを消す(従来の同期削除と同じ API・同じ性質)。
    /// </summary>
    /// <returns>試行の回数(テスト用)。</returns>
    internal static Task<int> DeleteWithRetryAsync(
        string path,
        IReadOnlyList<TimeSpan> retryDelays
    ) =>
        Task.Run(async () =>
        {
            // for (;;attempt++) は S1994(停止条件が attempt を見ない)に当たるため while(true) +
            // 手動インクリメントにする。ループ末尾で加算する点は for の attempt++ と同じ位置(継続時のみ)。
            int attempt = 0;
            while (true)
            {
                try
                {
                    if (System.IO.Directory.Exists(path))
                        System.IO.Directory.Delete(path, recursive: true);
                    return attempt + 1;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= retryDelays.Count)
                    {
                        // 次回起動の sweeper(PreviewUserDataSweeper)が回収する。
                        System.Diagnostics.Trace.TraceWarning(
                            $"PreviewUserDataFolder 削除失敗: {ex.Message} ({path})"
                        );
                        return attempt + 1;
                    }
                    await Task.Delay(retryDelays[attempt]).ConfigureAwait(false);
                }
                attempt++;
            }
        });
}
