namespace kxEdit.Core.Backup;

public enum PathValidation
{
    Ok,
    Rejected,
}

/// <summary>
/// BackupRecord.OriginalPath が復元先として安全か検証する。
/// Windows のシステム/プログラム系ルート配下は Rejected(攻撃者 JSON からの
/// 任意ファイル上書きを塞ぐ)。ユーザ配下は Ok。UNC は Ok(実運用サポート)。
///
/// BK-M-1: NTFS reparse point (directory junction / symbolic link) を検出して
/// バイパスを塞ぐ。junction は無権限で作成可能=見た目のパス
/// (%USERPROFILE%\innocent\hosts) が BlockedRoots に非該当でも parent が
/// C:\Windows\System32\drivers\etc\ を指せば hosts 上書きに至る。
/// 対策: (1) fast path = 対象パスとその全親を root まで遡り
/// FileAttributes.ReparsePoint bit を検査、(2) belt = File.ResolveLinkTarget
/// で解決先を再度 BlockedRoots に照合。ローカルドライブのみ対象で
/// UNC の Ok 契約は維持。
///
/// Task 4B: プレフィックス除去後の形を事後条件で検査する。残りが「ドライブ文字ルート」か
/// 「UNC」のどちらでもなければ Rejected(BlockedRoots の前方一致が意味を持つ形だけを通す)。
/// これにより \\?\GLOBALROOT\Device\HarddiskVolumeN\... 経由の BlockedRoots 回避を塞ぐ。
/// 副作用として \\?\Volume{GUID}\... (ドライブ文字未割当ボリューム)も Rejected になる
/// = hot exit 復元で無題タブに降格する(本文は失われない・受容)。
///
/// 現状の許容(次リリース以降で再検討):
/// - UNC 側の admin share (\\host\C$\Windows\... 等)経由の pivot は許容
///   (実運用の UNC を潰さない優先)。閉じる場合は BlockedRoots とは別の
///   UNC 用フィルタ(\\host\&lt;drive&gt;$\... を拒絶)で判定する。
/// - OneDrive Files On-Demand 等 cloud placeholder は IO_REPARSE_TAG_CLOUD 系
///   reparse tag を持つため BK-M-1 実装で無条件 Rejected になる可能性がある
///   (false-positive 受容)。tag 別判定(GetFileInformationByHandleEx /
///   FileAttributeTagInfo)による分離は将来検討。
/// </summary>
public static class OriginalPathValidator
{
    private static readonly string[] BlockedRoots = BuildBlockedRoots();

    private static string[] BuildBlockedRoots() =>
        new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        }
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => r.TrimEnd(Path.DirectorySeparatorChar))
            .ToArray();

    public static PathValidation Check(string path, out string normalized)
    {
        normalized = string.Empty;
        try
        {
            if (!Path.IsPathFullyQualified(path))
                return PathValidation.Rejected;
            normalized = Path.GetFullPath(path);

            // DOS device path プレフィックス(\\?\C:\..., \\.\C:\...)は .NET 9 の Path.GetFullPath 後も
            // 剥がされずに残るため、そのまま BlockedRoots (C:\Windows\... 等)との StartsWith 判定に流すと
            // 素通りしてしまう(実証: 攻撃者 JSON に `\\?\C:\Windows\System32\drivers\etc\hosts` を植えると
            // Ok が返る)。判定用にコピーを 1 本作り、そこから 4 文字プレフィックスを剥がして評価する。
            // \\?\UNC\server\share\... は「本物の UNC を長パス表現した安全な形式」なので、
            // \\server\share\... に戻したうえで既存の UNC 経路と同じ扱い(BlockedRoots に非該当=Ok)にする。
            string forCheck = normalized;
            if (forCheck.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                forCheck = @"\\" + forCheck[8..];
            else if (
                forCheck.StartsWith(@"\\?\", StringComparison.Ordinal)
                || forCheck.StartsWith(@"\\.\", StringComparison.Ordinal)
            )
                forCheck = forCheck[4..];

            // Task 4B: 事後条件 — プレフィックス除去後に残るのは「ドライブ文字ルート (X:\...)」か
            // 「UNC (\\server\share\...)」のどちらかでなければならない。
            // 4 文字剥がしだけでは \\?\GLOBALROOT\Device\HarddiskVolumeN\Windows\... が
            // GLOBALROOT\Device\... になり、BlockedRoots (C:\Windows\... 等)と決して前方一致しない=
            // Ok が返っていた(実証: 攻撃者 JSON に上記綴りを植えると hosts を Ok として復元先に採れる)。
            // ここを「拒否したい綴り (GLOBALROOT / Volume{GUID} / pipe / PhysicalDrive ...) の列挙」で
            // 書くと原理的に漏れる。**許可する形だけ**を書くので、新しい device 名前空間が
            // 増えても漏れない。BlockedRoots 判定はこの事後条件を満たしたパスにだけ適用される
            // = 「前方一致が意味を持つ形」であることが保証される。
            if (!IsDriveRooted(forCheck) && !IsUncRooted(forCheck))
                return PathValidation.Rejected;

            // BK-M-1: reparse point (junction/symlink) 検査は「ローカルドライブのみ」対象。
            // UNC (\\server\share\...) はサーバ側 NTFS でありクライアントから検査不能=
            // 既存の「UNC は BlockedRoots 非該当で Ok」契約を維持する。
            bool isUnc = forCheck.StartsWith(@"\\", StringComparison.Ordinal);
            if (!isUnc && RejectIfReparsePresent(forCheck) == PathValidation.Rejected)
                return PathValidation.Rejected;

            if (StartsWithAnyBlockedRoot(forCheck))
                return PathValidation.Rejected;
            return PathValidation.Ok;
        }
        catch
        {
            return PathValidation.Rejected;
        }
    }

    /// <summary>
    /// Task 4B: <c>X:\...</c> 形式か。区切りはバックスラッシュのみ許可する
    /// (<c>\\?\</c> 付きのパスは <see cref="Path.GetFullPath(string)"/> が「正規化済み」とみなして
    /// 素通しするため <c>\\?\C:/Windows/...</c> のようにスラッシュが残りうる。これを通すと
    /// <see cref="StartsWithAnyBlockedRoot"/> の前方一致 (<c>C:\Windows\</c>) が空振りする)。
    /// </summary>
    private static bool IsDriveRooted(string path) =>
        path.Length >= 3
        && char.IsAsciiLetter(path[0])
        && path[1] == ':'
        && path[2] == Path.DirectorySeparatorChar;

    /// <summary>
    /// Task 4B: <c>\\server\share...</c> 形式か。サーバー名と共有名の**両方**が非空であることを
    /// 要求する。ここを「<c>\\</c> で始まる」だけに緩めると、除去後に <c>\\</c> が残る綴りが
    /// 素通りする。
    /// </summary>
    private static bool IsUncRooted(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        int serverEnd = path.IndexOf(Path.DirectorySeparatorChar, 2);
        // -1 = 共有名の区切りが無い(\\server だけ) / 2 = サーバー名が空(\\\share\...)。
        // 後者は \\?\UNC\ の剥がしが \\ + 残り を作るだけなので degenerate な入力で生まれうる。
        if (serverEnd <= 2)
            return false;
        int shareStart = serverEnd + 1;
        return shareStart < path.Length && path[shareStart] != Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// BK-M-1: 対象パスとその全親ディレクトリを root まで遡り、reparse point
    /// (directory junction / symbolic link) が 1 つでも見つかれば Rejected を返す。
    /// 併せて <see cref="File.ResolveLinkTarget"/> でも解決先を BlockedRoots と再照合する
    /// (fast path が例外で見落とした場合の網)。
    ///
    /// 例外方針: I/O 例外 (FileNotFoundException / DirectoryNotFoundException /
    /// IOException / UnauthorizedAccessException) は握って continue する。leaf ファイルは
    /// バックアップの元ファイル削除後でも存在せず、親の権限不足で属性取得できない要素も
    /// 「バイパスに使えない=無害」扱いで進める。呼び出し側(<see cref="Check"/>)の
    /// 外側 catch で最終的な例外は Rejected へ丸められるが、想定内の I/O は
    /// ここでハンドリングして誤 Rejected を避ける。
    /// </summary>
    private static PathValidation RejectIfReparsePresent(string localPath)
    {
        // (1) fast path: 親を root まで遡って ReparsePoint bit を検査。
        string? cursor = localPath;
        while (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                var attrs = File.GetAttributes(cursor);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    return PathValidation.Rejected;
            }
            catch (Exception ex)
                when (ex
                        is FileNotFoundException
                            or DirectoryNotFoundException
                            or UnauthorizedAccessException
                            or IOException
                )
            {
                // その要素は検査できなかった=攻撃者もバイパスに使えないので進める
                // (leaf ファイル不在はバックアップ復元の正常経路)。
            }

            string? parent;
            try
            {
                parent = Path.GetDirectoryName(cursor);
            }
            catch
            {
                // 想定外のパス変形(将来の .NET / Windows 更新で新規例外が追加された
                // 場合)に対する fail-safe。現状の .NET 9 では Path.GetFullPath 通過後の
                // path に対して実質到達しないが、reparse 検出の可用性を優先し握って
                // walk を打ち切る(既に走査済みの祖先分だけで判定)。
                break;
            }
            if (
                string.IsNullOrEmpty(parent)
                || string.Equals(parent, cursor, StringComparison.Ordinal)
            )
                break;
            cursor = parent;
        }

        // (2) belt-and-suspenders: leaf が symlink/junction のとき解決先を BlockedRoots に再照合。
        //   ・fast path が既に catch していれば通常はここに到達しない
        //   ・File.ResolveLinkTarget は reparse でないパス / 存在しないパスに対して null を返す
        //     か例外を投げる=どちらも「非該当」扱いで通す
        try
        {
            var linkTarget = File.ResolveLinkTarget(localPath, returnFinalTarget: true);
            if (linkTarget != null && StartsWithAnyBlockedRoot(linkTarget.FullName))
                return PathValidation.Rejected;
        }
        catch (Exception ex)
            when (ex
                    is FileNotFoundException
                        or DirectoryNotFoundException
                        or UnauthorizedAccessException
                        or IOException
            )
        {
            // reparse でない / 存在しない / アクセス不能 = fast path 側の走査で十分。
        }

        return PathValidation.Ok;
    }

    /// <summary>
    /// BlockedRoots 判定の唯一の入口。将来 root マッチ規則を変える時は本ヘルパのみ触れば良い。
    /// </summary>
    private static bool StartsWithAnyBlockedRoot(string path)
    {
#pragma warning disable S3267 // foreach を LINQ Where に置換しない: plan Step 1.8 に従い可読性を優先する。
        foreach (var root in BlockedRoots)
        {
            if (
                path.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                return true;
        }
#pragma warning restore S3267
        return false;
    }
}
