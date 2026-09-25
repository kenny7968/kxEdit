namespace kxEdit.App.Tests;

/// <summary>
/// MD-M-4: MarkdownPreviewForm 用の per-form WebView2 UserDataFolder。
/// プロファイルロック競合回避 (複数プレビュー同時) と、フォーム破棄時の
/// 一時ディレクトリ削除 (残骸を残さない) を機械固定する。
///
/// 実 %LOCALAPPDATA% を触ることに注意。各テストは try/finally で
/// 生成した Path を必ず後始末する (Dispose の副作用に頼らない)。
///
/// 性能改善フェーズ 7(P-21(a)): 削除は背景で行う。Dispose の直後はフォルダーが残っていることがあるので、
/// 削除を確かめるテストは <c>DeletionTask</c> の完了を待つ。
///
/// L5 検証項目 (WebView2 依存で unit test 不可):
///   - 2 プレビュー同時起動でロック競合しない
///   - プレビュー閉じたあと %LOCALAPPDATA%\kxEdit\WebView2\preview-* が増え続けない
/// </summary>
public class PreviewUserDataFolderTests
{
    // xUnit1031(Fact 直下での Task.Wait/.Result 直呼び禁止)・S2925(Thread.Sleep 直呼び禁止)は
    // 「Fact 本体に直接書かれているか」だけを見るため、他ファイル(WinFormsDebounceSchedulerTests /
    // PendingActivationTests 等)の PumpUntil 系ヘルパーと同じ考え方で、private ヘルパー経由にして
    // 回避する。待つこと自体・値はテストの主目的なので変えない。
    private static bool WaitOrTimeout(Task task, TimeSpan timeout) => task.Wait(timeout);

    private static int ResultOf(Task<int> task) => task.Result;

    private static void SleepMs(int milliseconds) => Thread.Sleep(milliseconds);

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
    public void Ctor_PathUnderLocalAppDataKxeditWebView2Preview()
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
            Assert.True(WaitOrTimeout(sut.DeletionTask!, TimeSpan.FromSeconds(10))); // 削除は背景で行う(P-21(a))
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
            var first = sut.DeletionTask;
            // 2 回目でも throw せず、削除を二重に投げない。
            sut.Dispose();
            Assert.Same(first, sut.DeletionTask);
            Assert.True(WaitOrTimeout(sut.DeletionTask!, TimeSpan.FromSeconds(10))); // 削除は背景で行う(P-21(a))
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

    [Fact]
    public void EnsureEmptyBaseFolder_CreatesEmptyDirectoryUnderPath()
    {
        // V-2: baseDir が使えないときのマッピング先。空であることが契約 (ここに何か置くと
        // プレビューへ露出する)。
        //
        // NotEqual / GetFileName の 2 行は「return path; を return Path; へ退化させる」変異を
        // 殺すためにある。テストでは WebView2 を作らないので Path 直下も空のままであり、
        // Exists / Empty / StartsWith は 3 本とも緑を保ってしまう。だが実行時の Path 直下には
        // WebView2 プロファイル (Cookies / Local State 等) が出来るので、そこを
        // https://kxedit.preview/ のルートにするのは実害のある退行。
        var sut = new PreviewUserDataFolder();
        try
        {
            string empty = sut.EnsureEmptyBaseFolder();
            Assert.True(System.IO.Directory.Exists(empty));
            Assert.Empty(System.IO.Directory.GetFileSystemEntries(empty));
            Assert.StartsWith(sut.Path, empty, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(sut.Path, empty, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("empty-base", System.IO.Path.GetFileName(empty));
        }
        finally
        {
            SafeCleanup(sut);
        }
    }

    [Fact]
    public void EnsureEmptyBaseFolder_Idempotent()
    {
        // 2 回目でも throw しない (InitAsync が再入しても登録先が変わらない網)。
        var sut = new PreviewUserDataFolder();
        try
        {
            string first = sut.EnsureEmptyBaseFolder();
            string second = sut.EnsureEmptyBaseFolder();
            Assert.Equal(first, second);
            Assert.True(System.IO.Directory.Exists(second));
        }
        finally
        {
            SafeCleanup(sut);
        }
    }

    [Fact]
    public void Dispose_RemovesEmptyBaseFolder()
    {
        // 後始末の経路を増やさない設計 (親を消せば一緒に消える) の網。
        var sut = new PreviewUserDataFolder();
        string empty = sut.EnsureEmptyBaseFolder();
        try
        {
            sut.Dispose();
            Assert.True(WaitOrTimeout(sut.DeletionTask!, TimeSpan.FromSeconds(10))); // 削除は背景で行う(P-21(a))
            Assert.False(System.IO.Directory.Exists(empty));
        }
        finally
        {
            if (System.IO.Directory.Exists(sut.Path))
                System.IO.Directory.Delete(sut.Path, recursive: true);
        }
    }

    // ===== 性能改善フェーズ 7(P-21(a)): 削除は背景で行い、短い間隔でリトライする =====

    [Fact]
    public void Dispose_WhileLocked_ReturnsWithoutWaiting_AndDeletesAfterUnlock()
    {
        // WebView2 のブラウザプロセスがプロファイルを掴んだまま閉じた形。Dispose は削除を待たずに戻り、
        // ロックが外れた後のリトライで消える。
        using var tmp = new TempDir();
        var sut = new PreviewUserDataFolder(
            tmp.Root,
            Enumerable.Repeat(TimeSpan.FromMilliseconds(50), 100).ToArray()
        );
        string locked = System.IO.Path.Combine(sut.Path, "held");
        var fs = new System.IO.FileStream(
            locked,
            System.IO.FileMode.CreateNew,
            System.IO.FileAccess.Write,
            System.IO.FileShare.None
        );
        try
        {
            sut.Dispose();

            Assert.NotNull(sut.DeletionTask);
            Assert.False(sut.DeletionTask!.IsCompleted); // 掴まれている間は終わらない=Dispose は待っていない
            Assert.True(System.IO.Directory.Exists(sut.Path));
        }
        finally
        {
            fs.Dispose();
        }

        Assert.True(WaitOrTimeout(sut.DeletionTask!, TimeSpan.FromSeconds(10)));
        Assert.False(System.IO.Directory.Exists(sut.Path));
    }

    [Fact]
    public void DeleteWithRetry_RetriesAfterFailure()
    {
        using var tmp = new TempDir();
        string dir = System
            .IO.Directory.CreateDirectory(System.IO.Path.Combine(tmp.Root, "preview-x"))
            .FullName;
        var fs = new System.IO.FileStream(
            System.IO.Path.Combine(dir, "held"),
            System.IO.FileMode.CreateNew,
            System.IO.FileAccess.Write,
            System.IO.FileShare.None
        );
        Task<int> task;
        try
        {
            task = PreviewUserDataFolder.DeleteWithRetryAsync(
                dir,
                Enumerable.Repeat(TimeSpan.FromMilliseconds(50), 100).ToArray()
            );
            SleepMs(300); // 掴まれている間に少なくとも 1 回失敗させる
        }
        finally
        {
            fs.Dispose();
        }

        Assert.True(WaitOrTimeout(task, TimeSpan.FromSeconds(10)));
        Assert.True(ResultOf(task) >= 2, $"attempts={ResultOf(task)}"); // 失敗の後にリトライして消した
        Assert.False(System.IO.Directory.Exists(dir));
    }

    [Fact]
    public void DeleteWithRetry_GivesUpAfterRetries_WithoutThrowing()
    {
        // 最後まで消せなければ諦める(残骸は次回起動の sweeper が回収する)。例外は外へ出さない。
        using var tmp = new TempDir();
        string dir = System
            .IO.Directory.CreateDirectory(System.IO.Path.Combine(tmp.Root, "preview-y"))
            .FullName;
        using var fs = new System.IO.FileStream(
            System.IO.Path.Combine(dir, "held"),
            System.IO.FileMode.CreateNew,
            System.IO.FileAccess.Write,
            System.IO.FileShare.None
        );

        var task = PreviewUserDataFolder.DeleteWithRetryAsync(
            dir,
            new[] { TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10) }
        );

        Assert.True(WaitOrTimeout(task, TimeSpan.FromSeconds(10)));
        Assert.Equal(3, ResultOf(task)); // 初回 + リトライ 2 回
        Assert.True(System.IO.Directory.Exists(dir));
    }

    [Fact]
    public void DeleteWithRetry_MissingDirectory_IsNoOp()
    {
        using var tmp = new TempDir();
        var task = PreviewUserDataFolder.DeleteWithRetryAsync(
            System.IO.Path.Combine(tmp.Root, "preview-missing"),
            new[] { TimeSpan.FromMilliseconds(10) }
        );

        Assert.True(WaitOrTimeout(task, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, ResultOf(task));
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
