using kxEdit.Core.IO;

namespace kxEdit.Core.Tests.IO;

/// <summary>
/// A-15: reparse point を「名前を横取りするか」で分類する契約を固定する。
/// クラウドプレースホルダー(Microsoft タグだが name surrogate ではない)を
/// junction / symlink と区別できることが本体。
/// </summary>
public class ReparseTagReaderTests
{
    // ---- 純関数側: ビット判定 ----------------------------------------------------------

    [Theory]
    [InlineData(0xA0000003u, true)] // IO_REPARSE_TAG_MOUNT_POINT (junction)
    [InlineData(0xA000000Cu, true)] // IO_REPARSE_TAG_SYMLINK
    [InlineData(0x9000001Au, false)] // IO_REPARSE_TAG_CLOUD
    [InlineData(0x80000013u, false)] // IO_REPARSE_TAG_DEDUP
    [InlineData(0x80000017u, false)] // IO_REPARSE_TAG_WOF
    [InlineData(0x00000123u, false)] // 非 Microsoft・非 surrogate
    [InlineData(0x20000123u, true)] // 非 Microsoft・surrogate
    [InlineData(0u, false)] // reparse point でない = タグ無し
    public void IsNameSurrogate_ClassifiesByBit(uint tag, bool expected) =>
        Assert.Equal(expected, ReparseTagReader.IsNameSurrogate(tag));

    // ---- タグ取得側: 実ファイルシステム ------------------------------------------------

    [Fact]
    public void TryRead_ReturnsZero_ForPlainFile()
    {
        var dir = ReparsePointFixture.CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "plain.txt");
            File.WriteAllText(file, "x");
            // reparse point でないパスはタグを持たない = 0 を返す契約(Step 1.4 で実測確認)。
            Assert.Equal(0u, ReparseTagReader.TryRead(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryRead_ReturnsZero_ForPlainDirectory()
    {
        // FILE_FLAG_BACKUP_SEMANTICS 無しではディレクトリを開けない。walk が親を辿る
        // (Task 2)以上、ディレクトリで null に落ちないことが必須の前提。
        var dir = ReparsePointFixture.CreateTempDir();
        try
        {
            Assert.Equal(0u, ReparseTagReader.TryRead(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryRead_ReturnsNull_ForMissingPath()
    {
        var missing = Path.Combine(
            Path.GetTempPath(),
            "kxedit_nope_" + Guid.NewGuid().ToString("N")
        );
        Assert.Null(ReparseTagReader.TryRead(missing));
    }

    [Fact]
    public void TryRead_ReturnsNull_ForMalformedPath()
    {
        // CreateFileW に渡せない形(埋め込み NUL)= 判定不能。例外を素通しさせない。
        Assert.Null(ReparseTagReader.TryRead("C:\\bad\0path.txt"));
    }

    [Fact]
    public void TryRead_ReturnsTag_ForNonSurrogateReparsePoint()
    {
        // クラウドプレースホルダーの代用: reparse point だがタグは name surrogate ではない。
        // A-15 の本体 = ここを Rejected にしてはいけない、を Task 2 で使う。
        var dir = ReparsePointFixture.CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "cloudish.txt");
            File.WriteAllText(file, "");
            if (!ReparsePointFixture.TryCreate(file, ReparsePointFixture.NonSurrogateTag))
                return; // Skip: reparse point を作れない環境(非 NTFS / ポリシー / CI)

            // 属性ビットは立っている = 「属性だけでは足りない」ことをテスト側でも固定する。
            Assert.True((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(ReparsePointFixture.NonSurrogateTag, ReparseTagReader.TryRead(file));
            Assert.False(ReparseTagReader.IsNameSurrogate(ReparsePointFixture.NonSurrogateTag));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryRead_ReturnsMountPointTag_ForRealJunction()
    {
        // 上の Theory は「そのビットならこう分類する」というビット算術でしかない。
        // 実在の junction が本当に IO_REPARSE_TAG_MOUNT_POINT を持つことは別の事実であり、
        // ここが繋がって初めて「Task 2 で junction が引き続き Rejected になる」と言える。
        // ついでにディレクトリの reparse point で読めることも押さえる(fixture はファイルのみ)。
        //
        // junction は無権限で mklink /J で作れる(既存 Check_Rejects_PathThroughJunction と同じ前提)。
        // 作れない環境では early return で skip 相当。
        var guid = Guid.NewGuid().ToString("N");
        var target = Path.Combine(Path.GetTempPath(), $"kxedit_tagjunc_target_{guid}");
        var link = Path.Combine(Path.GetTempPath(), $"kxedit_tagjunc_link_{guid}");

        Directory.CreateDirectory(target);
        bool linkCreated = false;
        try
        {
            int exitCode;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(
                    "cmd",
                    $"/c mklink /J \"{link}\" \"{target}\""
                )
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi)!;
                if (!proc.WaitForExit(5000))
                {
                    proc.Kill();
                    return; // Skip: cmd がハング
                }
                exitCode = proc.ExitCode;
            }
            catch
            {
                return; // Skip: cmd を起動できない環境
            }
            if (exitCode != 0)
                return; // Skip: junction 作成不能 (非 NTFS / 権限不足)
            linkCreated = true;

            const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
            Assert.Equal(IO_REPARSE_TAG_MOUNT_POINT, ReparseTagReader.TryRead(link));
            Assert.True(ReparseTagReader.IsNameSurrogate(IO_REPARSE_TAG_MOUNT_POINT));

            // FILE_FLAG_OPEN_REPARSE_POINT が効いている証拠: junction 自身のタグが返り、
            // 解決先 (target = 通常ディレクトリ = タグ 0) のタグではない。
            Assert.Equal(0u, ReparseTagReader.TryRead(target));
        }
        finally
        {
            // 順序重要: junction を先に外す (Directory.Delete non-recursive は
            // reparse point だけ剥がし target contents は触らない)。
            if (linkCreated)
            {
                try
                {
                    Directory.Delete(link);
                }
                catch
                { /* best effort */
                }
            }
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch
            { /* best effort */
            }
        }
    }

    [Fact]
    public void TryRead_ReturnsTag_ForSurrogateReparsePoint()
    {
        // 対照群: 同じ経路で作った reparse point でも surrogate ビットが立つタグなら
        // そのまま読めること(TryRead が定数を返しているのではないことの弁別)。
        var dir = ReparsePointFixture.CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "surrogate.txt");
            File.WriteAllText(file, "");
            if (!ReparsePointFixture.TryCreate(file, ReparsePointFixture.SurrogateTag))
                return; // Skip: reparse point を作れない環境(非 NTFS / ポリシー / CI)

            Assert.Equal(ReparsePointFixture.SurrogateTag, ReparseTagReader.TryRead(file));
            Assert.True(ReparseTagReader.IsNameSurrogate(ReparsePointFixture.SurrogateTag));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
