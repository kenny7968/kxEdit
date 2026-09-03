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
/// 削除失敗 (WebView2 プロセスが直後まで残るケース等) は Trace 警告のみで
/// silent。「次回起動時 sweep で拾う」 (Program.cs 側) は v0.12 以降候補で、
/// 本 Task では Dispose 経路のみ実装する。
/// </para>
/// <para>
/// App 層内部にのみ露出するため <c>internal sealed</c>。テストは
/// <c>InternalsVisibleTo kxEdit.App.Tests</c> 経由でアクセスする。
/// </para>
/// </summary>
internal sealed class PreviewUserDataFolder : IDisposable
{
    /// <summary>WebView2 の <c>userDataFolder</c> に渡す絶対パス。</summary>
    public string Path { get; }

    public PreviewUserDataFolder()
    {
        // Guid.NewGuid().ToString("N") = 32 桁小文字 hex (ハイフン無し)。
        // ファイルシステム安全かつ per-form 一意性を担保。
        Path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kxEdit",
            "WebView2",
            "preview-" + Guid.NewGuid().ToString("N")
        );
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
        try
        {
            if (System.IO.Directory.Exists(Path))
            {
                System.IO.Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 起動時 sweep は v0.12 以降候補 (WebView2 プロセス終了直後は Delete が
            // ロックにかかることがあるため fallback として意図的に silent)。
            System.Diagnostics.Trace.TraceWarning(
                $"PreviewUserDataFolder 削除失敗: {ex.Message} ({Path})"
            );
        }
    }
}
