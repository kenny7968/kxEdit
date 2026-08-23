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

    // ---- Task 4B: プレフィックス除去後の「形」を事後条件で検査する ----
    //
    // 4 文字プレフィックスを剥がすだけでは
    // \\?\GLOBALROOT\Device\HarddiskVolumeN\Windows\System32\drivers\etc\hosts が
    // GLOBALROOT\Device\... になり、BlockedRoots (C:\Windows\... 等)と決して前方一致しない=
    // Ok が返っていた(= BlockedRoots という既存のセキュリティ制御を丸ごと無効化する)。
    //
    // 「拒否したい綴りの列挙」は原理的に漏れるので、除去後が
    // 「ドライブ文字ルート (X:\...)」か「UNC (\\server\share\...)」の**どちらかであること**を
    // 要求する。以下はその許可形以外が落ちることの pin。

    [Fact]
    public void Check_Rejects_GlobalRootDevicePathToBlockedRoot()
    {
        // ボリューム番号は固定値でよい: 検査は文字列の形だけを見るので、この番号が
        // 実在するか / どのドライブに解決するかは結果に影響しない。C: に解決する番号を
        // 探しに行くとテストが環境依存になる。
        var status = OriginalPathValidator.Check(
            @"\\?\GLOBALROOT\Device\HarddiskVolume1\Windows\System32\drivers\etc\hosts",
            out _
        );
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_GlobalRootDevicePath_EvenOutsideBlockedRoots()
    {
        // 形で弾くので配下は問わない。BlockedRoots への前方一致に頼っていないことの pin
        // (= 新しい device 名前空間が増えても漏れない)。
        var status = OriginalPathValidator.Check(
            @"\\?\GLOBALROOT\Device\HarddiskVolume1\Temp\a.txt",
            out _
        );
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_PhysicalDriveDevicePath()
    {
        var status = OriginalPathValidator.Check(@"\\.\PhysicalDrive0", out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_NamedPipeDevicePath()
    {
        var status = OriginalPathValidator.Check(@"\\.\pipe\foo", out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_VolumeGuidPath()
    {
        // 意図的な挙動変更(受容): ドライブ文字未割当ボリューム上のファイルは
        // hot exit 復元で無題タブに降格する(本文は残る)。許可リストに Volume{GUID} を
        // 足すと GLOBALROOT との弁別が形式的に難しくなり、事後条件方式の利点が薄れる。
        var status = OriginalPathValidator.Check(
            @"\\?\Volume{00000000-0000-0000-0000-000000000000}\a.txt",
            out _
        );
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_UncWithoutShareName()
    {
        // UNC は「サーバー名と共有名の両方がある」ことを要求する。ここを緩めると
        // \\ で始まりさえすれば通る = 除去後に \\ が残る綴りの素通りを許す。
        Assert.Equal(PathValidation.Rejected, OriginalPathValidator.Check(@"\\server", out _));
        Assert.Equal(PathValidation.Rejected, OriginalPathValidator.Check(@"\\server\", out _));
    }

    [Fact]
    public void Check_Rejects_UncWithoutServerName()
    {
        // \\?\UNC\ の剥がしは「\\ + 残り」を作るだけなので、残りが degenerate だと
        // \\\share\... という「サーバー名が空の UNC もどき」が生まれる。
        // \\?\ 付きは Path.GetFullPath が素通しする(=前段の正規化では落ちない)ので、
        // ここに到達することの証人になる。
        var status = OriginalPathValidator.Check(@"\\?\UNC\\share\a.txt", out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_ExtendedDrivePathWithAltSeparators()
    {
        // \\?\ 付きのパスは Path.GetFullPath が「正規化済み」とみなして素通しするため、
        // スラッシュがバックスラッシュに変換されない。C:/Windows/... は BlockedRoots
        // (C:\Windows\)と前方一致しない。
        // この fixture は事後条件(3 文字目が区切りでない=形が不正)で落ちるが、
        // 仮にそこを緩めても再正規化が C:\Windows\... へ canonical 化して BlockedRoots が
        // 捕まえる = 二重の網。形の検査そのものを固定しているのは
        // Check_Rejects_ExtendedDriveRelativePath の側(そちらは再正規化では救えない)。
        var status = OriginalPathValidator.Check(
            @"\\?\C:/Windows/System32/drivers/etc/hosts",
            out _
        );
        Assert.Equal(PathValidation.Rejected, status);
    }

    // ---- Task 4B fixup: 事後条件を通り抜ける「非 canonical なドライブ文字形式」 ----
    //
    // 事後条件は**形**しか見ない。\\?\ 付きは最初の GetFullPath が素通しするので、
    // 形は正しいドライブ文字ルートのまま綴りだけが非 canonical という入力が作れ、
    // StartsWithAnyBlockedRoot の前方一致だけが空振りする。実証済みの実在バイパス 2 系統。

    [Fact]
    public void Check_Rejects_ExtendedPathWithDoubledSeparatorToBlockedRoot()
    {
        // B-1: ドライブルート直後にセパレータを 1 個足すと index 3 が \ vs 本来の 1 文字目で
        // 不一致になり前方一致が空振りする。Windows は \\?\C:\\... を実際に解決するので
        // (実測: ProgramData 配下の victim.txt が上書きできた)実害がある。
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrEmpty(pd) || pd.Length < 3 || pd[1] != ':')
            return; // 環境依存 skip
        var plain = Path.Combine(pd, "kxEdit", "poison.json");
        // 前提の自己検証: canonical 形は BlockedRoots で Rejected になる。
        Assert.Equal(PathValidation.Rejected, OriginalPathValidator.Check(plain, out _));

        var doubled = @"\\?\" + plain.Insert(2, @"\"); // \\?\C:\\ProgramData\...
        Assert.Equal(PathValidation.Rejected, OriginalPathValidator.Check(doubled, out _));
    }

    [Fact]
    public void Check_Rejects_ExtendedPathWithShortNameToBlockedRoot()
    {
        // B-2: 8.3 短縮名。非 extended なら GetFullPath が GetLongPathName で展開するので
        // 正しく Rejected になるが、\\?\ 付きは素通しなので短縮名が残る。
        // 8.3 別名は環境依存(volume の 8.3 生成が無効なら存在しない)なので、
        // **非 extended 形の判定を oracle にして**「この綴りが BlockedRoot を指す」ことを
        // 確かめた候補だけを検証する(別名を決め打ちしない)。
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(win) || win.Length < 3 || win[1] != ':')
            return; // 環境依存 skip
        var root = win[..3];
        var candidates = new[] { "PROGRA~1", "PROGRA~2", "PROGRA~3" }
            .Select(alias => root + alias + @"\kxEdit\poison.json")
            .Where(p => OriginalPathValidator.Check(p, out _) == PathValidation.Rejected)
            .ToList();
        // candidates が空の環境(8.3 生成が無効など)では検証対象が無い=何も主張しない。
        foreach (var plain in candidates)
        {
            var status = OriginalPathValidator.Check(@"\\?\" + plain, out _);
            Assert.Equal(PathValidation.Rejected, status);
        }
    }

    [Fact]
    public void Check_Rejects_ExtendedDriveRelativePath()
    {
        // 再正規化の前提を固定する。\\?\C:xyz は入口の IsPathFullyQualified を**通過**し
        // (実測: 非 extended の C:xyz は False だが \\?\ 付きは True)、剥がすと
        // ドライブ相対形が残る。これを GetFullPath に渡すとカレントディレクトリ基準で
        // 解決されて絶対パスに化けるため、事後条件の「3 文字目が区切り」が門番になる。
        var status = OriginalPathValidator.Check(@"\\?\C:kxedit_no_such\a.txt", out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_ExtendedPathWithNonLetterDrive()
    {
        // ドライブ文字が英字であることの網。非 extended の 1:\... は
        // Path.IsPathFullyQualified が入口で弾く(実測 False)ので、\\?\ 付きだけが
        // ここに到達する = 事後条件が唯一の門番。
        var status = OriginalPathValidator.Check(@"\\?\1:\Temp\a.txt", out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_Rejects_ReservedDeviceNameNul()
    {
        // 意図的な挙動変更(安全側)。Path.GetFullPath はルート付きパスでも NUL だけを
        // \\.\NUL へ書き換える(実測: CON / COM1 / PRN / AUX / NUL.txt は書き換えない)。
        // \\.\NUL への保存は内容を黙って捨てるサイレントなデータ喪失なので、
        // 無題タブへの降格が正しい失敗。
        var path = Path.Combine(Path.GetTempPath(), "NUL");
        var status = OriginalPathValidator.Check(path, out _);
        Assert.Equal(PathValidation.Rejected, status);
    }

    [Fact]
    public void Check_ReturnsOk_ForExtendedDriveLetterPath()
    {
        // 回帰: \\?\ + ドライブ文字は従来どおり Ok。
        // この網は同時に「C:\Windows\... の Rejected 理由が BlockedRoots のままである」ことの
        // 証人でもある = 事後条件はドライブ文字形式を落とさないので、
        // Check_Rejects_System32Path 等を赤にしているのは BlockedRoots 側でしかありえない。
        var path = @"\\?\C:\kxedit_no_such_dir_" + Guid.NewGuid().ToString("N") + @"\a.txt";
        var status = OriginalPathValidator.Check(path, out _);
        Assert.Equal(PathValidation.Ok, status);
    }

    [Fact]
    public void Check_ReturnsOk_ForDosDeviceDriveLetterPath()
    {
        // 回帰: \\.\ + ドライブ文字も従来どおり Ok。
        var path = @"\\.\C:\kxedit_no_such_dir_" + Guid.NewGuid().ToString("N") + @"\a.txt";
        var status = OriginalPathValidator.Check(path, out _);
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
