using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace kxEdit.Core.Tests.IO;

/// <summary>
/// テスト用に「任意タグの reparse point」を作る。クラウドプレースホルダー
/// (Microsoft タグだが name surrogate ではない)と同じ形を実ファイルシステム上に用意するための道具。
///
/// <para>非 Microsoft タグ(bit31 = 0)は <c>REPARSE_GUID_DATA_BUFFER</c> 形式で書け、
/// 管理者権限を要しない(策定時の実測)。ファイルシンボリックリンクの作成は要管理者 /
/// 開発者モードなので、この経路では使わない。</para>
///
/// <para>作成できない環境(非 NTFS / ポリシー制限 / CI)では <see cref="TryCreate"/> が false を返し、
/// 呼出側テストは既存の <c>Check_Rejects_PathThroughJunction</c> と同じく early return で
/// skip 相当にする。</para>
/// </summary>
internal static class ReparsePointFixture
{
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;

    /// <summary>name surrogate ではない非 Microsoft タグ(クラウドプレースホルダーの代用)。</summary>
    internal const uint NonSurrogateTag = 0x00000123;

    /// <summary>name surrogate ビットを持つ非 Microsoft タグ。</summary>
    internal const uint SurrogateTag = 0x20000123;

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        EntryPoint = "CreateFileW"
    )]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        int nInBufferSize,
        nint lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        nint lpOverlapped
    );

    /// <summary>
    /// 既存の**ファイルまたは空ディレクトリ**を指定タグの reparse point にする。成功したら true。
    ///
    /// <para>ディレクトリにも効く(<c>FILE_FLAG_BACKUP_SEMANTICS</c> 付きで開いているため)。
    /// ただし実測(2026-08-31)で**空でないディレクトリには設定できない**
    /// (<c>ERROR_DIR_NOT_EMPTY</c> = 145)。また非 surrogate タグを付けたディレクトリの
    /// 配下には新規エントリを作れない(<c>IOException</c>)。したがって
    /// 「reparse ディレクトリの配下に leaf がある」形は**この経路では作れない**。</para>
    ///
    /// <para><b>後半の適用範囲を明記する(最終レビュー M-6)</b>: 「配下に新規エントリを作れない」は
    /// <b>この fixture が植えるタグ(<see cref="NonSurrogateTag"/> /
    /// <see cref="SurrogateTag"/> = 担当フィルタドライバが存在しない非 Microsoft タグ)に限った
    /// 性質</b>であり、非 surrogate タグ一般の性質ではない。担当フィルタが無いタグの配下は
    /// I/O が <c>ERROR_CANT_ACCESS_FILE</c> で弾かれるためこうなるのであって、
    /// <c>CLOUD</c> / <c>WCI</c> / <c>PROJFS</c> は素の Win11 に cldflt / wcifs / PrjFlt が
    /// attach 済みなので<b>配下へ書ける</b>(実測)。<c>ReparseTagReader</c> のクラス doc (5) と
    /// 同じ切り分け。ここを「非 surrogate なら配下に届かない」と一般化して読まないこと。</para>
    /// </summary>
    internal static bool TryCreate(string path, uint tag)
    {
        try
        {
            using var handle = CreateFile(
                path,
                GENERIC_READ | GENERIC_WRITE,
                0,
                nint.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                nint.Zero
            );
            if (handle.IsInvalid)
            {
                ReportSkip($"open failed (win32={Marshal.GetLastWin32Error()}) for {path}");
                return false;
            }

            // REPARSE_GUID_DATA_BUFFER: ReparseTag(4) ReparseDataLength(2) Reserved(2)
            //                           ReparseGuid(16) DataBuffer(n)
            const int dataLength = 8;
            var buffer = new byte[8 + 16 + dataLength];
            BitConverter.GetBytes(tag).CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)dataLength).CopyTo(buffer, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(buffer, 6);
            Guid.NewGuid().ToByteArray().CopyTo(buffer, 8);

            if (
                !DeviceIoControl(
                    handle,
                    FSCTL_SET_REPARSE_POINT,
                    buffer,
                    buffer.Length,
                    nint.Zero,
                    0,
                    out _,
                    nint.Zero
                )
            )
            {
                // 145 = ERROR_DIR_NOT_EMPTY(非空ディレクトリ)は fixture の使い方の誤り。
                ReportSkip($"FSCTL_SET_REPARSE_POINT failed (win32={Marshal.GetLastWin32Error()})");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            ReportSkip($"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// skip の理由を標準出力へ残す。呼出側テストは環境依存で early return するが、
    /// **無音で緑になると「網があるのに実は一度も走っていない」状態に気付けない**ため、
    /// 少なくとも <c>dotnet test -v n</c> で理由が見えるようにする。
    /// (実装バグと環境要因を切り分けるのが目的。)
    /// </summary>
    internal static void ReportSkip(string reason) =>
        Console.WriteLine($"[ReparsePointFixture] SKIP: {reason}");

    /// <summary>使い捨ての一時ディレクトリを作って返す。</summary>
    internal static string CreateTempDir() =>
        Directory
            .CreateDirectory(
                Path.Combine(Path.GetTempPath(), "kxedit_reparse_" + Guid.NewGuid().ToString("N"))
            )
            .FullName;

    /// <summary>
    /// <paramref name="dir"/> を後始末する。<see cref="Directory.Delete(string, bool)"/> の
    /// 再帰版と違い、**reparse point は中へ入らず非再帰で剥がす**。
    ///
    /// <para>既存 <c>Check_Rejects_PathThroughJunction</c> の finally が持っている
    /// 「順序重要: junction を先に外す」という知識を fixture 側へ集約したもの。
    /// 素の再帰 Delete は reparse ディレクトリで失敗し得るうえ、junction の場合は
    /// **解決先の中身まで消しに行く**ので、テストの後始末には使えない。</para>
    ///
    /// <para>後始末なので失敗は握る(本来の assertion 失敗をマスクしないため)。</para>
    /// </summary>
    internal static void DeleteTree(string dir)
    {
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(sub); // 非再帰 = 参照だけ剥がす
                else
                    DeleteTree(sub);
            }
            foreach (var file in Directory.EnumerateFiles(dir))
                File.Delete(file);
            Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[ReparsePointFixture] DeleteTree({dir}) best-effort: {ex.Message}");
        }
    }
}
