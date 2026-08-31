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
    // bit30 = winnt.h の R(Reserved)ビット。directory ビットは bit28(0x10000000)なので
    // 混同しないこと。これが無いとマスクを 0x60000000 へ広げる誤りが上の全ケースを
    // 生き残る(隣接ビットとの弁別)。
    [InlineData(0x40000000u, false)]
    [InlineData(0x10000000u, false)] // bit28 = directory。surrogate ではない。
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
            ReparsePointFixture.DeleteTree(dir);
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
            ReparsePointFixture.DeleteTree(dir);
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
    public void TryRead_ReturnsNull_ForPathContainingEmbeddedNul()
    {
        // 脆弱性レビュー V-1 の回帰ガード。ガードが無いと CreateFileW は NUL 以降を
        // 切り捨て、**渡したのとは別のパス**のタグを返す(実測: 下の 2 例はどちらも
        // ガード削除時に 0x00000000 = 実在する前半パスのタグが返る)。
        // 「安全だ」と答えた対象が入力と違う = confused deputy の原始形なので必ず null。
        Assert.Null(ReparseTagReader.TryRead("C:\\Windows\0path.txt"));

        var dir = ReparsePointFixture.CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "real.txt");
            File.WriteAllText(file, "x");
            // 実在ファイル + NUL + junk。切り捨てられれば 0 が返ってしまう。
            Assert.Null(ReparseTagReader.TryRead(file + "\0zzz"));
        }
        finally
        {
            ReparsePointFixture.DeleteTree(dir);
        }
    }

    [Fact]
    public void TryRead_ReturnsNull_ForNullOrEmptyPath()
    {
        // null ガードは NUL ガードの前提条件。これを外すと path.Contains('\0') が
        // NullReferenceException を投げ、TryReadCore の catch フィルタに掛からずに
        // 呼出側へ抜ける(= 判定不能が例外になる)。空文字は CreateFileW 任せでも
        // 同じ null だが、同じ枝に畳んでいるので併せて固定する。
        Assert.Null(ReparseTagReader.TryRead(null!));
        Assert.Null(ReparseTagReader.TryRead(string.Empty));
    }

    [Fact]
    public void TryRead_ReadsTag_ForPathLongerThanMaxPath()
    {
        // 脆弱性レビュー V-3 の回帰ガード。CreateFileW は .NET の API と違い extended 形へ
        // 自動変換しないため、素のままでは MAX_PATH 超で null になる。属性 walk では
        // 読めていた長パスがここで null になると、OneDrive の深い階層で誤 Rejected を招く
        // (A-15 と同じ症状)。
        //
        // **弁別力の前提**: このテストは「読めること」を assert する形なので、
        // \\?\ フォールバックを消しても *長パスが素で開ける環境* では緑のままになる。
        // 弁別できるのは次の 2 条件が成り立つ機械に限られる:
        //   (1) レジストリ LongPathsEnabled = 0(既定)
        //   (2) テストホストに longPathAware マニフェストが無い
        // 策定機はどちらも満たしており、修正前に実測で null が返ることを確認済み。
        // CI や他機では前提が変わり得るので、緑を「フォールバックが効いた証拠」と
        // 読む前にこの 2 条件を確認すること。
        var dir = ReparsePointFixture.CreateTempDir();
        try
        {
            var deep = dir;
            while (deep.Length < 300)
            {
                deep = Path.Combine(deep, "abcdefghijklmnopqrstuvwxyz01234567890123456789");
                Directory.CreateDirectory(deep);
            }
            var leaf = Path.Combine(deep, "leaf.txt");
            File.WriteAllText(leaf, "y");
            Assert.True(leaf.Length > 260, $"fixture が短すぎる: {leaf.Length}");

            // 対照: 属性 API は長パスでも読める。タグ側だけ読めないのは seam の欠陥。
            Assert.Equal(FileAttributes.Archive, File.GetAttributes(leaf) & FileAttributes.Archive);
            Assert.Equal(0u, ReparseTagReader.TryRead(leaf));
        }
        finally
        {
            ReparsePointFixture.DeleteTree(dir);
        }
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
            ReparsePointFixture.DeleteTree(dir);
        }
    }

    [Fact]
    public void TryRead_ReturnsTag_ForNonSurrogateReparseDirectory()
    {
        // A-15 の本命の形。OneDrive のプレースホルダーは**フォルダ単位**でも立ち、
        // Task 2 の walk が root まで辿るのは親=ディレクトリなので、
        // 「ディレクトリ + 非 surrogate タグ」を読めることがこの部品の要件。
        // (ファイルの reparse point だけでは Task 2 が踏む形を押さえたことにならない。)
        var dir = ReparsePointFixture.CreateTempDir();
        try
        {
            var target = Path.Combine(dir, "placeholderish");
            Directory.CreateDirectory(target);
            // 実測: 空ディレクトリにしか設定できない(非空は ERROR_DIR_NOT_EMPTY = 145)。
            if (!ReparsePointFixture.TryCreate(target, ReparsePointFixture.NonSurrogateTag))
                return; // Skip: reparse point を作れない環境(非 NTFS / ポリシー / CI)

            Assert.True((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(ReparsePointFixture.NonSurrogateTag, ReparseTagReader.TryRead(target));
            Assert.False(ReparseTagReader.IsNameSurrogate(ReparsePointFixture.NonSurrogateTag));
        }
        finally
        {
            ReparsePointFixture.DeleteTree(dir);
        }
    }

    [Fact]
    public void TryRead_ReturnsMountPointTag_ForRealJunction()
    {
        // 上の Theory は「そのビットならこう分類する」というビット算術でしかない。
        // 実在の junction が本当に IO_REPARSE_TAG_MOUNT_POINT を持つことは別の事実であり、
        // ここが繋がって初めて「Task 2 で junction が引き続き Rejected になる」と言える。
        // こちらは「実在の Microsoft タグ = surrogate」側の対照群。非 surrogate な
        // ディレクトリは TryRead_ReturnsTag_ForNonSurrogateReparseDirectory が押さえている。
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
                    ReparsePointFixture.ReportSkip("mklink /J がタイムアウト(cmd ハング)");
                    return;
                }
                exitCode = proc.ExitCode;
            }
            catch
            {
                ReparsePointFixture.ReportSkip("cmd を起動できない環境");
                return;
            }
            if (exitCode != 0)
            {
                ReparsePointFixture.ReportSkip($"mklink /J failed (exit={exitCode})");
                return;
            }
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
            ReparsePointFixture.DeleteTree(dir);
        }
    }
}
