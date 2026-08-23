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
/// Task 4B: BlockedRoots 照合の前段を 2 つ足す。どちらか片方だけでは塞がらない。
/// (1) 事後条件 = プレフィックス除去後の**形**が「ドライブ文字ルート」か「UNC」の
///     どちらかであることを要求する。\\?\GLOBALROOT\Device\HarddiskVolumeN\... /
///     \\.\PhysicalDrive0 / \\.\pipe\... はここで落ちる。
/// (2) 再正規化 = プレフィックスを剥がしてドライブ文字形式が残ったときだけ、もう一度
///     Path.GetFullPath に通す。\\?\ 付きは最初の GetFullPath が素通しするため、
///     C:\\Windows\... (区切り重複) や C:\PROGRA~3\... (8.3 短縮名) がそのまま残り、
///     どちらも**形は正しいドライブ文字ルート**なので (1) を通過して前方一致だけが空振りする
///     (実測: \\?\C:\\ProgramData\... への書き込みが実際に成立した)。
///
/// つまり事後条件は「形」しか保証せず、綴りの canonical 性は GetFullPath に依存する。
/// 安全性は「形の検査 + canonical 化 + 前方一致」の 3 点セットで初めて成立する。
///
/// 副作用として \\?\Volume{GUID}\... (ドライブ文字未割当ボリューム)も Rejected になる
/// = hot exit 復元では無題タブに降格する(本文は失われない・受容)。ただし
/// FileController の path-only extras 経路(未変更タブの復元)だけは Rejected でレコードごと
/// skip されるため、その綴りの clean タブは黙って復元されなくなる(本文は元ファイルに在る)。
///
/// 現状の許容(次リリース以降で再検討):
/// - subst / ネットワークドライブ割当は「ドライブ文字の許可リスト」では原理的に閉じない。
///   subst Y: C:\Windows した状態の Y:\System32\drivers\etc\hosts は Ok になる
///   (Task 4B 以前も同じ = 差分外)。%AppData% に書ける攻撃者は
///   HKCU\...\Explorer\DOS Devices にも書けるので脅威モデル内。根治は
///   「ハンドルを開いて最終パスを解決してから照合」だが本ブランチでは扱わない。
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
            bool prefixStripped = true;
            if (forCheck.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                forCheck = @"\\" + forCheck[8..];
            else if (
                forCheck.StartsWith(@"\\?\", StringComparison.Ordinal)
                || forCheck.StartsWith(@"\\.\", StringComparison.Ordinal)
            )
                forCheck = forCheck[4..];
            else
                prefixStripped = false;

            // Task 4B 再正規化: プレフィックスを剥がしてドライブ文字形式が残ったときだけ、
            // もう一度 GetFullPath に通す。上の :63 の GetFullPath は \\?\ 付きを
            // 「正規化済み」とみなして素通しするため、そのままでは非 canonical な綴りが残り、
            // StartsWithAnyBlockedRoot の前方一致が空振りする。実測で確認した実在バイパス:
            //   \\?\C:\\Windows\...        (区切りの重複。index 3 が \ vs W で不一致=素通り)
            //   \\?\C:\PROGRA~3\...        (8.3 短縮名。非 extended なら GetLongPathName で展開される)
            //   \\?\C:/Windows/...  \\?\C:\..\Windows\...  \\?\C:\Windows.\...
            // マーカーが消えた後なら GetFullPath が上のすべてを canonical 化する(実測 0 ms)。
            //
            // 順序が要: 先に IsDriveRooted で「ルート付き」を確かめてから通す。相対形
            // (GLOBALROOT\Device\... / pipe\foo / C:xyz)を GetFullPath に渡すと
            // **カレントディレクトリ基準で解決されて絶対パスに化ける**ため、事後条件を
            // 通過してしまう。
            //
            // コスト: 追加の GetFullPath はプレフィックス付き入力にしか走らない。UNC 枝は
            // IsDriveRooted が false になるので自動的に除外される = 不達共有 + `~` で
            // GetLongPathName が 21 秒かかる S-15 をこの経路から再導入しない。残るコスト増は
            // 「\\?\ / \\.\ 付き + ドライブ文字 + `~` を含む」入力で、そのドライブがネットワーク
            // 割当のときだけ(:63 の無境界 GetFullPath と同じ A-16 の受容範囲・設計書 §6)。
            if (prefixStripped && IsDriveRooted(forCheck))
                forCheck = Path.GetFullPath(forCheck);

            // Task 4B: 事後条件 — ここまで来た forCheck が「ドライブ文字ルート (X:\...)」か
            // 「UNC (\\server\share\...)」のどちらかでなければならない。
            // 4 文字剥がしだけでは \\?\GLOBALROOT\Device\HarddiskVolumeN\Windows\... が
            // GLOBALROOT\Device\... になり、BlockedRoots (C:\Windows\... 等)と決して前方一致しない=
            // Ok が返っていた(実証: 攻撃者 JSON に上記綴りを植えると hosts を Ok として復元先に採れる)。
            // ここを「拒否したい綴り (GLOBALROOT / Volume{GUID} / pipe / PhysicalDrive ...) の列挙」で
            // 書くと原理的に漏れるので、**許可する形だけ**を書く。
            //
            // ただし事後条件が保証するのは「形」だけで、綴りが canonical であることは
            // 上の再正規化(と :63 の GetFullPath)に依存する。両方が揃って初めて
            // StartsWithAnyBlockedRoot の前方一致が意味を持つ。片方だけでは塞がらない。
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
    /// Task 4B: <c>X:\...</c> 形式か = <b>ルート付きであること</b>の門番。
    /// canonical 性は一切保証しない(<c>C:\\Windows\...</c> も <c>C:\PROGRA~3\...</c> も
    /// <c>C:\..\Windows\...</c> も true を返す)。canonical 化は <see cref="Check"/> 側の
    /// 再正規化の仕事で、本メソッドはその<b>前提</b>(相対パスを
    /// <see cref="Path.GetFullPath(string)"/> に渡さない)を作るためにある。
    ///
    /// 3 文字目が区切りであることは load-bearing: ここを緩めるとドライブ相対形
    /// (<c>C:xyz</c>)が通り、再正規化がカレントディレクトリ基準で解決してしまう
    /// (実測: <c>GetFullPath(@"C:kxedit\a.txt")</c> → プロセスの CWD 配下)。
    /// ただし <b>load-bearing なのは「ドライブ相対を弾く」側だけ</b>で、
    /// 「<c>/</c> を弾く」側は再正規化と二重になっている:
    /// 区切りに <c>/</c> を足す変異は生存する(<c>C:/Windows/...</c> は再正規化が
    /// <c>C:\Windows\...</c> へ canonical 化して BlockedRoots が捕まえるため)。
    /// 形の検査そのものの証人は <c>Check_Rejects_ExtendedDriveRelativePath</c> の側。
    /// 1 文字目が英字であることも同様: 非 extended 入力なら
    /// <see cref="Path.IsPathFullyQualified(string)"/> が <c>1:\...</c> を入口で弾くが、
    /// <c>\\?\1:\...</c> は入口を通過するのでここが唯一の門番になる。
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
        //
        // このうち **load-bearing なのは == 2 の側だけ**。-1 の側は下流の共有名検査が同じ入力を
        // 落とす(\\server は shareStart == 0 になり path[0] が区切りなので false)。変異
        // 「<= 2 → == 2」が生存することで実証済み。意図を 1 箇所で読めるよう統合したまま残す。
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
