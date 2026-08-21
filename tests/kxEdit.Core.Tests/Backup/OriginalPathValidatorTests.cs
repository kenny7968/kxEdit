using kxEdit.Core.Backup;
using Xunit;

namespace kxEdit.Core.Tests.Backup;

/// <summary>
/// HIGH-2: BackupRecord.OriginalPath が復元先として安全か検証する契約を固定する。
/// 攻撃者 JSON が Windows / System32 / ProgramFiles 系のシステムパスへ復元させ、
/// ユーザ操作(Ctrl+S)で任意ファイル上書きに繋がる導線を遮断する(Untitled フォールバック)。
/// </summary>
public class OriginalPathValidatorTests
{
    [Fact]
    public void Check_ReturnsOk_ForNormalUserPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "notes.txt");
        var status = OriginalPathValidator.Check(path, out var normalized);
        Assert.Equal(PathValidation.Ok, status);
        Assert.Equal(Path.GetFullPath(path), normalized);
    }

    [Fact]
    public void Check_Rejects_RelativePath()
    {
        var status = OriginalPathValidator.Check(@"..\evil.txt", out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_System32Path()
    {
        var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var path = Path.Combine(sys32, "drivers", "etc", "hosts");
        var status = OriginalPathValidator.Check(path, out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_WindowsRootPath()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var path = Path.Combine(win, "win.ini");
        var status = OriginalPathValidator.Check(path, out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_ProgramFilesPath()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrEmpty(pf))
            return; // 環境依存 skip
        var path = Path.Combine(pf, "some", "app.exe");
        var status = OriginalPathValidator.Check(path, out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_ProgramDataPath()
    {
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var path = Path.Combine(pd, "kxEdit", "backups", "poison.json");
        var status = OriginalPathValidator.Check(path, out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_ReturnsOk_ForUncPath()
    {
        var status = OriginalPathValidator.Check(@"\\server\share\legit.txt", out _);
        Assert.Equal(PathValidation.Ok, status);
    }

    [Fact]
    public void Check_Rejects_InvalidPathChars()
    {
        var status = OriginalPathValidator.Check("C:\\x\0y.txt", out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    // ---- I-2: DOS device path プレフィックス経由のバイパス回帰ガード ----

    [Fact]
    public void Check_Rejects_ExtendedPathToSystem32()
    {
        // \\?\C:\Windows\System32\... は .NET 9 の Path.GetFullPath でも \\?\ が残り、
        // 素の StartsWith("C:\\Windows\\") 判定を素通りする。stripping で塞ぐ。
        var status = OriginalPathValidator.Check(
            @"\\?\C:\Windows\System32\drivers\etc\hosts",
            out _
        );
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_DosDevicePathToWindowsRoot()
    {
        // \\.\ プレフィックス版も同様に BlockedRoots を素通りするため塞ぐ。
        var status = OriginalPathValidator.Check(@"\\.\C:\Windows\win.ini", out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_ReturnsOk_ForExtendedUncPath()
    {
        // \\?\UNC\server\share\file.txt は「本物の UNC を長パスで表現」した安全な形式。
        // 判定は先頭 \\?\UNC\ を剥がし \\server\share\ に戻して評価する=Ok に落ちる。
        var status = OriginalPathValidator.Check(@"\\?\UNC\server\share\legit.txt", out _);
        Assert.Equal(PathValidation.Ok, status);
    }

    // ---- BK-M-1: NTFS reparse point (junction / symlink) 経由バイパスの回帰ガード ----

    [Fact]
    public void Check_ReturnsOk_ForNonexistentUserPath()
    {
        // BK-M-1: バックアップは元ファイル削除後でも復元可能=存在しないパス自体は
        // Rejected の理由にならない。reparse 検査ループが「leaf 不在」を握って通す契約を固定。
        var path = Path.Combine(
            Path.GetTempPath(),
            "kxedit_nonexistent_" + Guid.NewGuid().ToString("N") + ".txt"
        );
        var status = OriginalPathValidator.Check(path, out var normalized);
        Assert.Equal(PathValidation.Ok, status);
        Assert.Equal(Path.GetFullPath(path), normalized);
    }

    [Fact]
    public void Check_ReturnsOk_ForDeepNonexistentPath()
    {
        // reparse 検査で親ディレクトリを root まで遡る際、不在パスに対して I/O 例外を握って
        // continue する契約を固定(NRE / InvalidOperationException を投げないこと)。
        var path =
            @"C:\kxedit_no_such_dir_"
            + Guid.NewGuid().ToString("N")
            + @"\a\b\c\d\e\f\g\h\i\j\file.txt";
        var status = OriginalPathValidator.Check(path, out _);
        Assert.Equal(PathValidation.Ok, status);
    }

    [Fact]
    public void Check_Rejects_PathThroughJunction()
    {
        // BK-M-1 メイン回帰ガード: 親ディレクトリが directory junction のとき、
        // 見た目のパス (%TEMP%\<link>\innocent.txt) が BlockedRoots に非該当でも
        // Rejected を返すこと。
        //
        // junction は無権限 (elevated 不要) で mklink /J で作成できる。ただし CI や
        // 非 NTFS ボリューム / cmd 不可環境では作成失敗するので、その場合は既存の
        // 環境依存スキップと同じく early return して pass 扱いにする
        // (テストは 1 件 skip 相当だが green のまま通る)。
        var guid = Guid.NewGuid().ToString("N");
        var target = Path.Combine(Path.GetTempPath(), $"kxedit_junc_target_{guid}");
        var link = Path.Combine(Path.GetTempPath(), $"kxedit_junc_link_{guid}");

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

            var pathViaJunction = Path.Combine(link, "innocent.txt");
            var status = OriginalPathValidator.Check(pathViaJunction, out _);
            Assert.Equal(PathValidation.Rejected, status);
        }
        finally
        {
            // 順序重要: junction を先に外す (Directory.Delete non-recursive は
            // reparse point だけ剥がし target contents は触らない)。target を先に
            // 消してから junction を消すと空 target への junction が残るだけで安全だが、
            // 明示的に junction → target の順で片付ける。
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
}
