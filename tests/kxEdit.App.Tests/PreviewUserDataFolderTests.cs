namespace kxEdit.App.Tests;

/// <summary>
/// MD-M-4: MarkdownPreviewForm 用の per-form WebView2 UserDataFolder。
/// プロファイルロック競合回避 (複数プレビュー同時) と、フォーム破棄時の
/// 一時ディレクトリ削除 (残骸を残さない) を機械固定する。
///
/// 実 %LOCALAPPDATA% を触ることに注意。各テストは try/finally で
/// 生成した Path を必ず後始末する (Dispose の副作用に頼らない)。
///
/// L5 検証項目 (WebView2 依存で unit test 不可):
///   - 2 プレビュー同時起動でロック競合しない
///   - プレビュー閉じたあと %LOCALAPPDATA%\kxEdit\WebView2\preview-* が増え続けない
/// </summary>
public class PreviewUserDataFolderTests
{
    [Fact]
    public void Ctor_CreatesDirectory()
    {
        var sut = new PreviewUserDataFolder();
        try
        {
            Assert.True(System.IO.Directory.Exists(sut.Path));
        }
        finally
        {
            SafeCleanup(sut);
        }
    }

    [Fact]
    public void Ctor_PathUnderLocalAppDataYeditWebView2Preview()
    {
        var sut = new PreviewUserDataFolder();
        try
        {
            string expectedRoot = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "kxEdit",
                "WebView2"
            );
            Assert.StartsWith(expectedRoot, sut.Path, StringComparison.OrdinalIgnoreCase);
            string leaf = System.IO.Path.GetFileName(sut.Path);
            Assert.StartsWith("preview-", leaf, StringComparison.Ordinal);
        }
        finally
        {
            SafeCleanup(sut);
        }
    }

    [Fact]
    public void Dispose_RemovesDirectory()
    {
        var sut = new PreviewUserDataFolder();
        string path = sut.Path;
        try
        {
            Assert.True(System.IO.Directory.Exists(path));
            sut.Dispose();
            Assert.False(System.IO.Directory.Exists(path));
        }
        finally
        {
            // Dispose 済みだが念のため残骸ガード
            if (System.IO.Directory.Exists(path))
                System.IO.Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void Dispose_Idempotent()
    {
        var sut = new PreviewUserDataFolder();
        string path = sut.Path;
        try
        {
            sut.Dispose();
            // 2 回目でも throw しない (Directory.Exists ガードで silent)。
            sut.Dispose();
            Assert.False(System.IO.Directory.Exists(path));
        }
        finally
        {
            if (System.IO.Directory.Exists(path))
                System.IO.Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void Ctor_PathIsUnique_AcrossInstances()
    {
        var a = new PreviewUserDataFolder();
        var b = new PreviewUserDataFolder();
        try
        {
            Assert.NotEqual(a.Path, b.Path);
        }
        finally
        {
            SafeCleanup(a);
            SafeCleanup(b);
        }
    }

    [Fact]
    public void Ctor_PathLeaf_Matches_PreviewGuidNContract()
    {
        // Guid.N (32 桁小文字 hex・ハイフン無) の naming 契約を機械固定する。
        // Guid.NewGuid().ToString() (ハイフン付) や ToString("D") への誤変更を検知。
        var sut = new PreviewUserDataFolder();
        try
        {
            string leaf = System.IO.Path.GetFileName(sut.Path);
            Assert.Matches("^preview-[0-9a-f]{32}$", leaf);
        }
        finally
        {
            SafeCleanup(sut);
        }
    }

    private static void SafeCleanup(PreviewUserDataFolder sut)
    {
        try
        {
            sut.Dispose();
        }
        catch
        {
            // テスト後始末のみ。Dispose が warn 経路に落ちても無視。
        }
        if (System.IO.Directory.Exists(sut.Path))
        {
            try
            {
                System.IO.Directory.Delete(sut.Path, recursive: true);
            }
            catch
            {
                // ここで throw すると本来のテスト assertion 失敗が隠れるため飲む。
            }
        }
    }
}
