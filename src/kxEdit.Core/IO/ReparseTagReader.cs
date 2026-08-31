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
/// 宣言するためのビット(<c>winnt.h</c> の <c>IsReparseTagNameSurrogate</c> と同一)。</para>
///
/// <para><b>ただし「surrogate ビット = パス解決が追従する」は 1:1 ではない</b>。
/// <c>IO_REPARSE_TAG_DFS</c>(<c>0x8000000A</c>)/ <c>DFSR</c>(<c>0x80000012</c>)/
/// <c>WCI</c>(<c>0x80000018</c>)/ <c>PROJFS</c>(<c>0x9000001C</c>)/
/// <c>NFS</c>(<c>0x80000014</c>)/ <c>CLOUD</c>(<c>0x9000001A</c>)は
/// **ビットが立たないのに名前を転送しうる**。それでもこの判定で塞がると言える根拠を、
/// <b>実測</b>と<b>未実測の想定</b>に分けて書く(混ぜると嘘の安全宣言になる)。</para>
///
/// <para><b>実測(2026-08-31・非管理者)</b>:
/// (1) NTFS 自身が名前を差し替えるのは <c>MOUNT_POINT</c> と <c>SYMLINK</c> のみで、
/// 両方ともビットを持つ。
/// (2) 非管理者が植えられないのは <c>SYMLINK</c> <b>だけ</b>(<c>mklink /D</c> が
/// <c>ERROR_PRIVILEGE_NOT_HELD</c> = 1314 で失敗)。<b><c>MOUNT_POINT</c> は有効な
/// ペイロードなら非管理者でも植わる</b> —— 昇格していないプロセス
/// (<c>WindowsPrincipal.IsInRole(Administrator)</c> = false)からの <c>mklink /J</c> が
/// <c>exit=0</c> で成功し、タグ <c>0xA0000003</c>(Microsoft / Name Surrogate / Mount Point)が
/// 読み戻せ、<c>LinkTarget</c> と <c>ResolveLinkTarget</c> が解決先を返し、配下のファイルも
/// 読めた(最終レビューで再実測・2026-08-31)。
/// <b>これは BK-M-1 という脅威の成立条件そのもの</b>であり、
/// <c>OriginalPathValidator</c> の「junction は無権限で作成可能」という記述および
/// 緑になっている junction テスト 2 本と整合する。
/// <c>FSCTL_SET_REPARSE_POINT</c> に<b>全 0 のペイロード</b>を渡した <c>MOUNT_POINT</c> /
/// <c>SYMLINK</c> / <c>GLOBAL_REPARSE</c> が <c>rc=4392</c>(<c>ERROR_INVALID_REPARSE_DATA</c>)/
/// <c>4393</c>(<c>ERROR_REPARSE_TAG_INVALID</c>)で失敗するのは<b>権限ではなくペイロード検証</b>に
/// よるもので、これを「非管理者は植えられない」と読んだのが誤りだった。
/// <b>結論は変わらない</b>: (1) のとおり MOUNT_POINT も SYMLINK も surrogate ビットを持つので、
/// 植えられるかどうかに関わらずこの判定で拒否される。(2) は元々「重ねて安全」の belt であって
/// 判定を支える根拠ではない。
/// (3) 一方で Microsoft の**非 surrogate** タグ 11 個(<c>DFS</c> / <c>DFSR</c> / <c>NFS</c> /
/// <c>WOF</c> / <c>WCI</c> / <c>WCI_1</c> / <c>APPEXECLINK</c> / <c>PROJFS</c> /
/// <c>PROJFS_TOMBSTONE</c> / <c>CLOUD</c> / <c>CLOUD_1</c>)は**非管理者でもすべて植えられる**
/// (<c>err=0</c>)。植えた対象配下に対する <c>OriginalPathValidator.Check</c> は <c>Ok</c> を返す。
/// (4) 担当フィルタが**無い**タグ(非 Microsoft タグを含む)の配下は、write も
/// <c>GetAttributes</c> も <c>EnumerateFiles</c> も <c>ERROR_CANT_ACCESS_FILE</c> で失敗する
/// = ゲートが開くだけで到達しない。
/// (5) <b>ただし (4) は <c>CLOUD</c> / <c>CLOUD_1</c> / <c>WCI</c> / <c>WCI_1</c> /
/// <c>PROJFS</c> の 5 つには適用できない。</b>素の Win11 に <c>cldflt</c> / <c>wcifs</c> /
/// <c>PrjFlt</c> が attach されているため、配下への書き込みが成功する。
/// (6) ただし無効ペイロード(全 0)では名前は移動せず、<c>GetFinalPathNameByHandle</c> は
/// 元の temp 配下を返す。<b>BlockedRoot 配下への実書き込みは再現できていない。</b></para>
///
/// <para><b>想定(未実測。ここが最終的な安全性を支えている)</b>: (5) のタグで実際に名前を
/// 横取りさせるには、WCI ならレイヤー結合、ProjFS なら仮想化ルートの登録、DFS なら
/// DFS 名前空間という<b>フィルタ側の前提</b>が先に要り、それらは管理者権限を必要とする、と
/// 想定している。<b>この一文はドキュメント上の理解であって実測ではない。</b>
/// したがって「非 surrogate タグは安全だと実測で確かめた」とは言えず、正確には
/// 「(6) の有効ペイロードを非管理者が用意できるかを確かめていない」。この想定を破るなら、
/// 有効ペイロードで BlockedRoot 配下へ実書き込みできることの再現が反例になる。</para>
///
/// <para><b>なぜ <see cref="FileSystemInfo.LinkTarget"/> を使わないか</b>(P/Invoke を避けられる案):
/// 策定時の実測(2026-08-31・net9.0)で、<c>LinkTarget</c> は
/// <b>非 Microsoft の name surrogate タグ(<c>0x20000123</c>)にも <c>null</c> を返した</b>。
/// つまり <c>LinkTarget != null</c> は name surrogate 判定と等価ではなく、これに置き換えると
/// サードパーティ製フィルタドライバの surrogate が現状より緩く通る。同じ実測で
/// junction は解決先を返し、未対応タグでは例外ではなく <c>null</c> が返ることも確認している。</para>
///
/// <para><b>hydrate を誘発しない(意図・<u>未実測</u>)</b>: <c>FILE_FLAG_OPEN_REPARSE_POINT</c> は
/// 「reparse point 自体を開き、解決先へ追従しない」ためのフラグなので、未ダウンロードの
/// クラウドファイルにダウンロードを起こさない**はず**(復元経路が通信を誘発しないための要件)。
/// ただし開発機の OneDrive に同期実体が無く、**実クラウドプレースホルダーでは確認できていない**
/// (設計書 §2.2 のとおり L5 送り)。この doc の他の主張は実測だが、ここだけは根拠が
/// documented behavior のみである点に注意。なお「呼び戻さない」ことを主目的とするフラグは
/// 本来 <c>FILE_FLAG_OPEN_NO_RECALL</c> だが、解決先へ追従しない以上そこまでは不要と判断した
/// (これも L5 で確認したい点)。<c>FILE_FLAG_BACKUP_SEMANTICS</c> はディレクトリを
/// 同じ呼び出しで扱うために必要。</para>
///
/// <para><b>呼出側の契約: <c>File.GetAttributes</c> より open が厳しい</b>
/// (実測 2026-08-31)。<c>File.GetAttributes</c> は <c>GetFileAttributesW</c> 経由で
/// 親ディレクトリの走査権限だけで答えるため、対象ファイル自身に Deny FullControl が付いていても
/// 成功する。一方 <c>CreateFileW</c> は同じファイルで <c>ERROR_ACCESS_DENIED</c> になり、
/// ここは <c>null</c> を返す(<c>FILE_READ_ATTRIBUTES</c> 要求でも 0 要求でも同じ)。
/// つまり属性ビット walk をタグ walk へ置き換えると「判定できない要素」が増える。
/// <c>null</c> は「reparse point ではない」を意味しない。</para>
///
/// <para><b>呼出側の前提</b>: 正規化済み(<c>Path.GetFullPath</c> 通過後)で、形が
/// ドライブ文字ルートか UNC のパスを渡すこと。<c>TryRead</c> は形の検査をしないので、
/// 相対パスはプロセスの CWD 基準で解決され、<c>\\.\pipe\...</c> を渡せば
/// **名前付きパイプへ実際に接続してしまう**(サーバー側がクライアントを偽装しうる)。
/// 埋め込み NUL だけは実装側で拒否する(下記 <see cref="TryRead"/> 参照)。
/// 現行の唯一の呼出側 <c>OriginalPathValidator</c> は device パスを先に Rejected にするため
/// いずれも到達しないが、これは呼出側の性質であってこのクラスの保証ではない。</para>
/// </summary>
internal static class ReparseTagReader
{
    /// <summary>この reparse point は別の名前付き実体を表す、というビット
    /// (<c>IO_REPARSE_TAG_NAME_SURROGATE_BIT</c>)。</summary>
    private const uint NameSurrogateBit = 0x20000000;

    /// <summary>データを読まずに属性とタグだけを問い合わせるための最小権限。
    /// 実測(2026-08-31)で <c>dwDesiredAccess = 0</c> と挙動は完全に一致した
    /// (通常ファイル / ディレクトリ / System32 / Deny ACE 付きファイル / 不在パスのすべてで同結果)ため、
    /// 意図が名前に出るこちらを採る。</summary>
    private const uint FILE_READ_ATTRIBUTES = 0x0080;

    private const uint FILE_SHARE_READ_WRITE_DELETE = 0x00000007;
    private const uint OPEN_EXISTING = 3;

    /// <summary>これ以上の長さのパスは <c>\\?\</c> 形でないと CreateFileW が開けない。</summary>
    private const int MaxPathLimit = 260;
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
    /// **読み取れなかった場合は <c>null</c>**(存在しない / アクセス不能 / API 失敗 /
    /// <paramref name="path"/> が空・<c>null</c>・埋め込み NUL を含む)。
    /// 呼出側は <c>null</c> を「安全と判明した」と読んではならない。
    ///
    /// <para>埋め込み NUL を含むパスは**開かずに <c>null</c> を返す**。CreateFileW は
    /// NUL 以降を切り捨てるため、素通しすると「渡したのとは別のパス」のタグを
    /// 返してしまうから(実測 2026-08-31)。</para>
    ///
    /// <para><c>null</c> の**理由**(不在なのか権限なのか)は捨てている。区別が要るなら
    /// <c>Marshal.GetLastWin32Error()</c> を拾う必要がある(実測で不在 = 2 /
    /// アクセス拒否 = 5 と別々のコードが立つ)。現状の呼出側は理由を使わないので
    /// 拾っていないが、**「不在」と「読めない」を同じ扱いにしてよいかは呼出側の判断**。</para>
    /// </summary>
    internal static uint? TryRead(string path)
    {
        // 埋め込み NUL は**必ず拒否する**。マーシャラは例外を投げず、CreateFileW が
        // NUL 以降を切り捨てて解釈するため、ガードが無いと
        // TryRead(@"C:\safe" + "\0\junction\x") が **C:\safe のタグ**を返す
        // (実測 2026-08-31: TryRead("C:\Windows\0path.txt") → 0x00000000)。
        // 「渡したパスとは別のパスについて安全だと答える」形になるので、
        // 呼出側の正規化に依存せずここで塞ぐ。
        //
        // IsNullOrEmpty を前に置くのは NUL ガードの**前提条件**であって飾りではない:
        // 直後の path.Contains は null に対して NullReferenceException を投げ、
        // それは TryReadCore の catch フィルタ(DllNotFound / EntryPointNotFound /
        // Argument)に**掛からず呼出側へ抜ける**。NUL ガードを足したこと自体が
        // null 経路を例外化したので、対で必要になった。空文字は CreateFileW でも
        // 同じ null になるが、判定を 1 か所に集めるため同じ枝で畳んでいる。
        if (string.IsNullOrEmpty(path) || path.Contains('\0'))
            return null;

        uint? tag = TryReadCore(path);
        if (tag is not null)
            return tag;

        // MAX_PATH 超のパスは CreateFileW が開けない(.NET の API と違い extended 形へ
        // 自動変換しない)。属性 walk では読めていた長パスがここで null になると、
        // OneDrive の深い階層で誤 Rejected を招く(A-15 と同じ症状)ので拡張形で再試行する。
        // 実測: 312 文字のパスで File.GetAttributes は成功・素の CreateFileW は失敗・
        //       \\?\ 前置で成功。
        string? extended = ToExtendedLengthPath(path);
        return extended is null ? null : TryReadCore(extended);
    }

    private static uint? TryReadCore(string path)
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
            // 判定不能はすべて null へ倒す。
            // 実測(2026-08-31)では**この経路に到達しない**: 埋め込み NUL は上で弾いており、
            // kernel32 は Windows に必ずある。非 Windows 実行と将来のマーシャラ変更に
            // 対する保険として残している(無被覆であることを明記)。
            return null;
        }
    }

    /// <summary>
    /// MAX_PATH 超のパスを <c>\\?\</c> 形へ書き換える。適用対象外なら <c>null</c>。
    ///
    /// <para>短いパスには**適用しない**。<c>\\?\</c> 形は正規化を無効化するため
    /// (<c>..</c> や <c>/</c> が解決されなくなる)、素の呼び出しが成功し得る限りは
    /// 素のまま使う。ここは「素で開けなかった長パス」だけの救済経路。</para>
    /// </summary>
    private static string? ToExtendedLengthPath(string path)
    {
        if (path.Length < MaxPathLimit)
            return null;
        if (
            path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal)
        )
            return null; // 既に extended / device 形
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path[2..];
        // ドライブ文字ルートのみ。相対パスは extended 形にできない。
        if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\')
            return @"\\?\" + path;
        return null;
    }

    /// <summary>タグが name surrogate(名前を横取りする種別)か。</summary>
    internal static bool IsNameSurrogate(uint tag) => (tag & NameSurrogateBit) != 0;
}
