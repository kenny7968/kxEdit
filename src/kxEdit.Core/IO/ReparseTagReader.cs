using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace kxEdit.Core.IO;

/// <summary>
/// A-15: reparse point の**タグ**を読み、「名前を別の場所へ横取りするか」
/// (= Windows が name surrogate と呼ぶ種別か)を判定する。
///
/// <para><b>なぜ属性ビットでは足りないか</b>: <see cref="FileAttributes.ReparsePoint"/> は
/// junction / symbolic link だけでなく、OneDrive Files On-Demand のクラウドプレースホルダー・
/// 重複除去(DEDUP)・WOF 圧縮・AppExecLink にも立つ。これらは名前を横取りしないので、
/// 属性ビットだけで拒否すると**クラウド配下の普通のファイルを拒否**してしまう(= A-15)。</para>
///
/// <para><b>なぜタグの列挙ではなくビット判定か</b>: 「拒否したいタグの列挙」は原理的に漏れる
/// (<c>OriginalPathValidator</c> が事後条件の議論で確立した規律と同型)。name surrogate ビット
/// (<c>0x20000000</c>)は Windows 自身が「この reparse point は別の名前付き実体を表す」と
/// 宣言するためのビットで、パス解決が追従する種別と 1:1 で対応する。</para>
///
/// <para><b>なぜ <see cref="FileSystemInfo.LinkTarget"/> を使わないか</b>(P/Invoke を避けられる案):
/// 策定時の実測(2026-08-31・net9.0)で、<c>LinkTarget</c> は
/// <b>非 Microsoft の name surrogate タグ(<c>0x20000123</c>)にも <c>null</c> を返した</b>。
/// つまり <c>LinkTarget != null</c> は name surrogate 判定と等価ではなく、これに置き換えると
/// サードパーティ製フィルタドライバの surrogate が現状より緩く通る。同じ実測で
/// junction は解決先を返し、未対応タグでは例外ではなく <c>null</c> が返ることも確認している。</para>
///
/// <para><b>hydrate を誘発しない</b>: <c>FILE_FLAG_OPEN_REPARSE_POINT</c> を付けて開くので、
/// 未ダウンロードのクラウドファイルに対してダウンロードを起こさない(復元経路が
/// 通信を誘発しないための要件)。<c>FILE_FLAG_BACKUP_SEMANTICS</c> はディレクトリを
/// 同じ呼び出しで扱うために必要。</para>
///
/// <para><b>呼出側(Task 2)への申し送り: <c>File.GetAttributes</c> より open が厳しい</b>
/// (実装時の実測 2026-08-31)。<c>File.GetAttributes</c> は <c>GetFileAttributesW</c> 経由で
/// 親ディレクトリの走査権限だけで答えるため、対象ファイル自身に Deny FullControl が付いていても
/// 成功する。一方 <c>CreateFileW</c> は同じファイルで <c>ERROR_ACCESS_DENIED</c> になり、
/// ここは <c>null</c> を返す(<c>FILE_READ_ATTRIBUTES</c> 要求でも 0 要求でも同じ)。
/// つまり属性ビット walk をタグ walk へ置き換えると「判定できない要素」が増える。
/// <c>null</c> を「reparse point ではない」と読むと、そこが穴になる。</para>
/// </summary>
internal static class ReparseTagReader
{
    /// <summary>この reparse point は別の名前付き実体を表す、というビット
    /// (<c>IO_REPARSE_TAG_NAME_SURROGATE_BIT</c>)。</summary>
    internal const uint NameSurrogateBit = 0x20000000;

    /// <summary>データを読まずに属性とタグだけを問い合わせるための最小権限。
    /// 実測(2026-08-31)で <c>dwDesiredAccess = 0</c> と挙動は完全に一致した
    /// (通常ファイル / ディレクトリ / System32 / Deny ACE 付きファイル / 不在パスのすべてで同結果)ため、
    /// 意図が名前に出るこちらを採る。</summary>
    private const uint FILE_READ_ATTRIBUTES = 0x0080;

    private const uint FILE_SHARE_READ_WRITE_DELETE = 0x00000007;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    /// <summary><c>FileAttributeTagInfo</c>(<c>FILE_INFO_BY_HANDLE_CLASS</c> の 9)。</summary>
    private const int FileAttributeTagInfoClass = 9;

    /// <summary><c>FILE_ATTRIBUTE_TAG_INFO</c>。DWORD 2 本のみでパディングの罠が無い
    /// (<c>WIN32_FIND_DATAW</c> 経由でタグを取る案は、先頭 DWORD の後に FILETIME が来るため
    /// 既定 Pack でフィールドがずれる。策定時に実際に踏んだので採らない)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

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
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int FileInformationClass,
        out FileAttributeTagInfo lpFileInformation,
        int dwBufferSize
    );

    /// <summary>
    /// <paramref name="path"/> の reparse タグを返す。reparse point でなければ <c>0</c>、
    /// **読み取れなかった場合は <c>null</c>**(存在しない / アクセス不能 / API 失敗)。
    /// 呼出側は <c>null</c> を「安全と判明した」と読んではならない。
    /// </summary>
    internal static uint? TryRead(string path)
    {
        try
        {
            using var handle = CreateFile(
                path,
                FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ_WRITE_DELETE,
                nint.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                nint.Zero
            );
            if (handle.IsInvalid)
                return null;
            return GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfoClass,
                out var info,
                Marshal.SizeOf<FileAttributeTagInfo>()
            )
                ? info.ReparseTag
                : null;
        }
        catch (Exception ex)
            when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            // ネイティブが無い / パスが CreateFileW に渡せない形 = 判定不能。
            return null;
        }
    }

    /// <summary>タグが name surrogate(名前を横取りする種別)か。</summary>
    internal static bool IsNameSurrogate(uint tag) => (tag & NameSurrogateBit) != 0;
}
