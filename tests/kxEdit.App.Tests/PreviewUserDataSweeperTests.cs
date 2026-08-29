using System.IO;
using Directory = System.IO.Directory;
using File2 = System.IO.File;
using Path = System.IO.Path;

namespace kxEdit.App.Tests;

/// <summary>
/// M-V1(2026-08-29 最終レビュー 脆弱性パス): M-1 の <c>Environment.Exit</c> は
/// フォームの <c>Dispose</c> を走らせないため、プレビューを開いたままクラッシュすると
/// <c>PreviewUserDataFolder</c> が残り、回収の当てが無いまま単調増加する。
/// 起動時 sweep がそれを拾うことを固定する。
/// </summary>
public class PreviewUserDataSweeperTests
{
    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("kxEditSweep_").FullName;

        public string Dir(string name)
        {
            string p = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(p);
            return p;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { /* 掃除失敗はテスト失敗にしない(ロック中のハンドルが残る可能性) */
            }
        }
    }

    [Fact]
    public void Sweep_DeletesPreviewDirectories_Recursively()
    {
        using var tmp = new TempRoot();
        string a = tmp.Dir("preview-aaaaaaaa");
        // 中身入り(WebView2 のプロファイルはネストしたファイル群)。
        // recursive:false への変異は、これが空でないことで殺される。
        Directory.CreateDirectory(Path.Combine(a, "Default", "Cache"));
        File2.WriteAllText(Path.Combine(a, "Default", "Cache", "data"), "x");
        string b = tmp.Dir("preview-bbbbbbbb");

        Assert.Equal(2, PreviewUserDataSweeper.Sweep(tmp.Path));
        Assert.False(Directory.Exists(a));
        Assert.False(Directory.Exists(b));
    }

    [Fact]
    public void Sweep_LeavesUnrelatedDirectories()
    {
        // `preview-*` 以外は誤爆させない。パターンを "*" に広げる変異を殺す。
        using var tmp = new TempRoot();
        string keep = tmp.Dir("EBWebView"); // WebView2 の共有プロファイル等
        string keep2 = tmp.Dir("previews"); // 紛らわしい名前(ハイフン無し)
        string sweep = tmp.Dir("preview-cccccccc");

        Assert.Equal(1, PreviewUserDataSweeper.Sweep(tmp.Path));
        Assert.True(Directory.Exists(keep));
        Assert.True(Directory.Exists(keep2));
        Assert.False(Directory.Exists(sweep));
    }

    [Fact]
    public void Sweep_MissingRoot_ReturnsZero()
    {
        // プレビューを一度も開いていない環境で起動を落とさない。
        using var tmp = new TempRoot();
        string missing = Path.Combine(tmp.Path, "does-not-exist");
        Assert.Equal(0, PreviewUserDataSweeper.Sweep(missing));
    }

    [Fact]
    public void Sweep_OneFailure_DoesNotAbortTheRest()
    {
        // 1 件がロックされていても残りは掃除する(1 件の失敗で全部諦めない)。
        using var tmp = new TempRoot();
        string locked = tmp.Dir("preview-locked00");
        string other = tmp.Dir("preview-other000");
        string lockedFile = Path.Combine(locked, "held");
        using (
            var fs = new FileStream(
                lockedFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None
            )
        )
        {
            fs.WriteByte(1);
            fs.Flush();

            Assert.Equal(1, PreviewUserDataSweeper.Sweep(tmp.Path));
            Assert.True(Directory.Exists(locked)); // 掴まれている側は残る
            Assert.False(Directory.Exists(other)); // 残りは消える
        }
    }

    [Fact]
    public void DefaultRoot_PointsAtPreviewParentUnderLocalAppData()
    {
        // PreviewUserDataFolder が実際に作る親と一致していること
        // (ここがずれると sweep は永遠に 0 件で、静かに何も守らない)。
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kxEdit",
            "WebView2"
        );
        Assert.Equal(expected, PreviewUserDataSweeper.DefaultRoot);

        using var folder = new PreviewUserDataFolder();
        Assert.Equal(PreviewUserDataSweeper.DefaultRoot, Path.GetDirectoryName(folder.Path));
        Assert.StartsWith("preview-", Path.GetFileName(folder.Path));
    }
}
