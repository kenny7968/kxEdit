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

    /// <summary>既存ファイルを指定タグの reparse point にする。成功したら true。</summary>
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
                return false;

            // REPARSE_GUID_DATA_BUFFER: ReparseTag(4) ReparseDataLength(2) Reserved(2)
            //                           ReparseGuid(16) DataBuffer(n)
            const int dataLength = 8;
            var buffer = new byte[8 + 16 + dataLength];
            BitConverter.GetBytes(tag).CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)dataLength).CopyTo(buffer, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(buffer, 6);
            Guid.NewGuid().ToByteArray().CopyTo(buffer, 8);

            return DeviceIoControl(
                handle,
                FSCTL_SET_REPARSE_POINT,
                buffer,
                buffer.Length,
                nint.Zero,
                0,
                out _,
                nint.Zero
            );
        }
        catch
        {
            return false;
        }
    }

    /// <summary>使い捨ての一時ディレクトリを作って返す。</summary>
    internal static string CreateTempDir() =>
        Directory
            .CreateDirectory(
                Path.Combine(Path.GetTempPath(), "kxedit_reparse_" + Guid.NewGuid().ToString("N"))
            )
            .FullName;
}
