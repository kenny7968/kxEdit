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
/// 対策: (1) fast path = 対象パスとその全親を root まで遡り、reparse point であれば
/// そのタグが name surrogate かを <see cref="kxEdit.Core.IO.ReparseTagReader"/> で判定、
/// (2) belt = File.ResolveLinkTarget で解決先を再度 BlockedRoots に照合。
/// ローカルドライブのみ対象で UNC の Ok 契約は維持。
///
/// A-15: (1) の判定は元は FileAttributes.ReparsePoint bit だけを見ていたが、
/// 同じビットはクラウドプレースホルダー (OneDrive Files On-Demand) / DEDUP / WOF にも立ち、
/// **クラウド配下の普通のファイルを無言で無題タブへ降格**させていた。塞ぎたいのは
/// 「名前を BlockedRoot へ横取りされること」なので、判定を name surrogate ビットに変更した
/// (junction / symlink / mount point はいずれも surrogate なので拒否は挙動不変)。
/// 拒否を緩める方向なので、開くのは「横取りしないと**積極的に判明した**」ときだけで、
/// タグを読めなかった場合は Rejected を維持する。
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
/// V-m-1(最終ブランチレビュー・脆弱性パスの実測): その事後条件には**穴がある**。
/// <see cref="IsUncRooted"/> の device 除外は \\?\ と \\.\ の**厳密 2 綴り**しか見ないので、
/// \\??\ / \\.?\ / \\?.\ / \\?\UNC\C:\ / \\?\UNC\GLOBALROOT\ は「\\ 始まり + サーバー名と
/// 共有名が非空」を満たして UNC と見なされ、Ok が返る。つまり
/// 「ここまで来た形はドライブ文字ルートか UNC のどちらか」という上の記述は、
/// **厳密には成立していない**。
/// 実害が無いのは下流の事情による: 実測でこれらの綴りは MUP(Multiple UNC Provider)へ
/// 回されて解決できず、実書き込みには至らない。したがって現状は「事後条件が破れているが
/// 到達できない」状態であり、事後条件そのものを証人にした議論はできない
/// (将来 UNC 側の扱いを変えるときは、ここが前提になっていないか必ず確認すること)。
///
/// (3) device プレフィックスの再出現も閉じる。「許可する形だけを書く」方式でも、
///     **許可した形に化ける綴り**は別途塞ぐ必要がある。実在した 2 経路:
///     - 再正規化が予約デバイス名を書き換えて \\.\NUL を作る(\\?\ 配下の NUL は
///       デバイスではなく普通のファイル名なので、通すと BlockedRoot 配下に実ファイルができる)
///     - プレフィックスを二重にした入力 (\\?\\\?\C:\Windows\...) は剥がしが 1 回なので
///       \\?\C:\Windows\... が残る
///     どちらも「\\ 始まり + 区切り + 非空」で UNC 判定を満たしてしまうため、
///     IsUncRooted の入口で \\?\ / \\.\ を明示的に除外している。
///
/// 副作用として \\?\Volume{GUID}\... (ドライブ文字未割当ボリューム)も Rejected になる
/// = hot exit 復元では無題タブに降格する(本文は失われない・受容)。ただし
/// FileController の path-only extras 経路(未変更タブの復元)だけは Rejected でレコードごと
/// skip されるため、その綴りの clean タブは黙って復元されなくなる(本文は元ファイルに在る)。
///
/// A-16: reparse 検査を skip する条件を「UNC」から「リモート全体」
/// (<see cref="kxEdit.Core.IO.RemotePathDetector.IsRemote"/> = UNC + マップドネットワーク
/// ドライブ)へ広げた。walk の契約は元から「ローカルドライブのみ対象」で、その根拠
/// (実体がサーバ側 NTFS でクライアントから検査不能)はマップドドライブ (Z:\) にも
/// そのまま当てはまる。つまり**契約の食い違いの是正**であって性能上の回避ではない。
/// 副次的に、不達共有で walk が同期 I/O を leaf から root まで直列に積む
/// (1 要素あたり SMB タイムアウト)経路が消える。
///
/// 現状の許容(次リリース以降で再検討):
/// - subst / ネットワークドライブ割当は「ドライブ文字の許可リスト」では原理的に閉じない。
///   subst Y: C:\Windows した状態の Y:\System32\drivers\etc\hosts は Ok になる
///   (Task 4B 以前も同じ = 差分外)。%AppData% に書ける攻撃者は
///   HKCU\...\Explorer\DOS Devices にも書けるので脅威モデル内。根治は
///   「ハンドルを開いて最終パスを解決してから照合」だが本ブランチでは扱わない。
///   A-16 で**ネットワーク割当の側だけ**が reparse 検査の対象からも外れた
///   (subst は DriveType=Fixed のままなので walk は続く。実測 2026-08-31)。
///   代償はマップドドライブ上の junction が Rejected にならなくなること。ただし
///   **これで新しく到達できる先は 1 つも増えない**: Z: の実体 \\server\share は UNC 綴りでも
///   書けて、そちらは元から未検査で Ok(上の V-m-3 = \\localhost\C$ 経由で BlockedRoot 配下へ
///   実際に書けた、がその実例)。境界が「ドライブ文字か否か」から「リモートか否か」へ
///   移るだけで、受容範囲の**形**も**広さ**も変わらない。
///   <b>将来 UNC 側を閉じるときは、同じフィルタをマップドドライブにも当てること</b>
///   (\\host\&lt;drive&gt;$ を UNC 綴りだけで弾いても、同じ共有をドライブ文字で綴った経路が残る)。
/// - UNC 側の admin share (\\host\C$\Windows\... 等)経由の pivot は許容
///   (実運用の UNC を潰さない優先)。閉じる場合は BlockedRoots とは別の
///   UNC 用フィルタ(\\host\&lt;drive&gt;$\... を拒絶)で判定する。
///   V-m-3(最終レビュー・実測): この受容範囲は上の例が示すより広い。**host はループバックで
///   よい** — \\?\UNC\localhost\C$\ProgramData\... が Ok を返し、実際に BlockedRoot 配下へ
///   書き込めることを実測した(\\127.0.0.1\C$\... も同様)。つまりネットワークも攻撃者の
///   インフラも要らず、%AppData% の backup JSON を書ければ 1 台の中で完結する
///   (admin share を開くので管理者権限は必要)。「リモート共有を使う攻撃だから遠い」と
///   読まないこと。
/// - reparse point だが**タグを読めない**要素(対象自身に Deny ACE が付いている等。
///   File.GetAttributes は親の走査権限だけで成功するが CreateFileW は ERROR_ACCESS_DENIED)は
///   Rejected のままになる。属性ビット時代と同じ結果なので A-15 の緩和で新しく生まれた
///   受容ではない(フェイルセーフ方向)。
/// - name surrogate ビットが立たないのに名前を転送しうるタグ (DFS / DFSR / WCI / PROJFS /
///   NFS / CLOUD) は通る。これらは**非管理者でも植えられ**、うち CLOUD / WCI / PROJFS は
///   素の Win11 に担当フィルタが attach されているため配下への書き込みまで成功する(実測)。
///   実書き込みが BlockedRoot へ届かないことの根拠は、**実測ではなく想定**である
///   (有効なペイロードの用意にフィルタ側の管理者権限前提が要る、という理解)。
///   何が実測で何が想定かの切り分けは <see cref="kxEdit.Core.IO.ReparseTagReader"/> の
///   クラス doc を参照。**「実測で安全と確かめた」と読まないこと。**
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
            //
            // V-m-2(最終レビュー・実測): この説明が当てはまるのは **UNC が厳密に大文字**の
            // 綴りだけ。剥がしの比較は Ordinal なので \\?\unc\... / \\?\Unc\... は
            // ここを通らず、下の \\?\ 枝で 4 文字だけ剥がされて "unc\server\share\..." になり、
            // 事後条件(ドライブ文字ルートでも UNC でもない)で Rejected になる。
            // Windows 側は小文字綴りも解決できる(実測)ので、これは**過剰拒否**。
            // フェイルセーフ方向なので安全上の問題は無く、症状は「その綴りの clean タブが
            // 黙って復元されない(本文は元ファイルに在る)」まで。OrdinalIgnoreCase へ緩める
            // 修正は、剥がし後の綴りが再び事後条件と BlockedRoots を通ることの確認込みで
            // 別途行う(本ブランチでは形を変えない)。
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
            //
            // 順序が load-bearing: 再正規化を**後ろ**へ動かすと、事後条件が検査した形と
            // BlockedRoots が照合する形が食い違う。GetFullPath は予約デバイス名を書き換える
            // (C:\ProgramData\...\NUL → \\.\NUL)ので、「ドライブ文字形式として合格した値」が
            // 照合時には device パスに化けている、という状態が作れてしまう(B-3)。
            // ここで**正規化後の値を検査する**ことでその窓を閉じている。
            if (!IsDriveRooted(forCheck) && !IsUncRooted(forCheck))
                return PathValidation.Rejected;

            // BK-M-1 / A-16: reparse point (junction/symlink) 検査は「ローカルドライブのみ」対象。
            // 元の skip 条件は UNC (\\server\share\...) だけで、根拠は「実体はサーバ側 NTFS で
            // クライアントから検査不能=既存の『UNC は BlockedRoots 非該当で Ok』契約を維持する」
            // だった。マップドネットワークドライブ (Z:\) も実体はサーバ側にあるので、同じ根拠が
            // そのまま当てはまる。ここを広げるのは性能上の回避ではなく**契約の食い違いの是正**で、
            // 副次的に不達共有での同期 I/O(GetAttributes を root まで直列 + A-15 のタグ読み
            // CreateFileW)を消す。
            //
            // 述語の差分は「ネットワーク割当のドライブ文字」だけ: RemotePathDetector は
            // UncPathDetector(先頭 \\ の純粋判定)を内包し、ドライブ文字なら
            // DriveInfo.DriveType == Network を見る。ここへ到達する forCheck は事後条件により
            // 「X:\... か \\server\share\...」のどちらかなので、UNC 側は元の StartsWith(@"\\") と
            // 完全に一致する(subst は DriveType=Fixed なので walk の対象のまま。実測 2026-08-31)。
            bool isRemote = kxEdit.Core.IO.RemotePathDetector.IsRemote(forCheck);
            if (!isRemote && RejectIfReparsePresent(forCheck) == PathValidation.Rejected)
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
    /// ただし「<c>/</c> を弾く」側は<b>セキュリティ的には再正規化と二重</b>になっている:
    /// BlockedRoot を指す綴り (<c>C:/Windows/...</c>) は、仮にここを緩めても再正規化が
    /// <c>C:\Windows\...</c> へ canonical 化して BlockedRoots が捕まえる(実測で確認)。
    /// 区切りに <c>/</c> を足す変異は<b>等価変異ではなく「挙動は変わるがセキュリティ的に
    /// 中立な変異」</b>で、変わるのは BlockedRoot **外**の綴りの可否だけ。その差分を
    /// <c>Check_Rejects_ExtendedDrivePathWithAltSeparators_OutsideBlockedRoots</c> で固定し、
    /// ドライブ相対を弾く側は <c>Check_Rejects_ExtendedDriveRelativePath</c> で固定している。
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
    ///
    /// B-3 / I-4: DOS device プレフィックス (<c>\\?\</c> / <c>\\.\</c>) を**明示的に除外**する。
    /// これが無いと <c>\\.\NUL</c> が「サーバー名 <c>.</c>・共有名 <c>NUL</c>」の UNC に化け、
    /// 事後条件・BlockedRoots・BK-M-1 の reparse 検査の 3 つすべてを迂回する。到達経路は 2 本:
    /// (1) <see cref="Check"/> の再正規化が予約デバイス名を書き換える
    ///     (<c>\\?\C:\ProgramData\...\NUL</c> → 剥がし → <c>C:\ProgramData\...\NUL</c> →
    ///      <c>GetFullPath</c> → <c>\\.\NUL</c>)。<c>\\?\</c> 配下では <c>NUL</c> はデバイスでなく
    ///      普通のファイル名なので、通すと BlockedRoot 配下に実ファイルが作られる(実証済み)。
    /// (2) プレフィックスを**二重**にした入力 (<c>\\?\\\?\C:\Windows\...</c>)。剥がしは 1 回
    ///     だけなので <c>\\?\C:\Windows\...</c> が残る。
    /// <para>
    /// <b>限界(V-m-1・最終ブランチレビューの実測)</b>: 除外するのは上の<b>厳密 2 綴り</b>だけ。
    /// <c>\\??\</c> / <c>\\.?\</c> / <c>\\?.\</c> / <c>\\?\UNC\C:\</c> /
    /// <c>\\?\UNC\GLOBALROOT\</c> はここを素通りし、「サーバー名と共有名が非空」を満たして
    /// <c>true</c> を返す = <see cref="Check"/> が <see cref="PathValidation.Ok"/> を返す。
    /// クラス doc が謳う事後条件(「形はドライブ文字ルートか UNC のどちらか」)はこの範囲で破れている。
    /// 実書込には至らない(実測: MUP へ回されて解決不能)ので実害は無いが、
    /// <b>事後条件を証人にして安全を主張しないこと</b>。
    /// </para>
    /// </summary>
    private static bool IsUncRooted(string path)
    {
        if (
            path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal)
        )
            return false;
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
    /// BK-M-1 / A-15: 対象パスとその全親ディレクトリを root まで遡り、**名前を横取りする種別の**
    /// reparse point (directory junction / symbolic link / mount point = name surrogate) が
    /// 1 つでも見つかれば Rejected を返す。横取りしない reparse point
    /// (クラウドプレースホルダー / DEDUP / WOF) は通す。
    /// 併せて <see cref="File.ResolveLinkTarget"/> でも解決先を BlockedRoots と再照合する
    /// (fast path が例外で見落とした場合の網)。
    ///
    /// 例外方針: I/O 例外 (FileNotFoundException / DirectoryNotFoundException /
    /// IOException / UnauthorizedAccessException) は握って continue する。leaf ファイルは
    /// バックアップの元ファイル削除後でも存在せず、親の権限不足で属性取得できない要素も
    /// 「バイパスに使えない=無害」扱いで進める。呼び出し側(<see cref="Check"/>)の
    /// 外側 catch で最終的な例外は Rejected へ丸められるが、想定内の I/O は
    /// ここでハンドリングして誤 Rejected を避ける。
    ///
    /// A-15 で足したタグ読みの失敗は**この方針の例外**で、continue ではなく Rejected へ倒す。
    /// 上の「握って進める」が成り立つのは「そもそも reparse point だと分からなかった要素」
    /// だからで、「reparse point だと分かっているが種別が読めない」要素はバイパスに使える。
    /// </summary>
    private static PathValidation RejectIfReparsePresent(string localPath)
    {
        // (1) fast path: 親を root まで遡って reparse point の**タグ**を検査。
        string? cursor = localPath;
        while (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                var attrs = File.GetAttributes(cursor);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    // A-15: 属性ビットは「reparse point である」しか言わない。塞ぎたいのは
                    // 名前を BlockedRoot へ横取りされることなので、判定は name surrogate かどうかで行う。
                    // クラウドプレースホルダー / DEDUP / WOF は横取りしないので通す。
                    //
                    // ガードが開くのは「reparse point だが横取りしないと**積極的に判明した**」
                    // ときだけ。タグを読めなかった (null) 場合は従来どおり Rejected へ倒す
                    // = 「読めなかった」と「安全だと分かった」を混ぜない。
                    //
                    // 属性ビットの検査を前に残すのは、タグ読み(CreateFileW)を
                    // **reparse point に対してだけ**走らせるため。walk は root まで全親を辿るので、
                    // 通常パスでハンドルを開く回数を増やさない。
                    //
                    // tag == 0(GetAttributes は reparse だと言ったのに TryRead は
                    // 「reparse ではない」と答えた= 2 つの観測が矛盾している状態)は
                    // **意図的に Ok へ倒す**。2 回の観測の間に対象が差し替わったことを意味するので、
                    // ここで Rejected にしても TOCTOU 窓が閉じるわけではない
                    // (walk 全体が元から Check → 実書き込みの間で TOCTOU であり、
                    // これはその窓が 1 つ内側にずれるだけ)。矛盾を検出しても使える情報が無い以上、
                    // 「reparse ではない」という新しい方の観測を採る。
                    uint? tag = kxEdit.Core.IO.ReparseTagReader.TryRead(cursor);
                    if (tag is null || kxEdit.Core.IO.ReparseTagReader.IsNameSurrogate(tag.Value))
                        return PathValidation.Rejected;
                }
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
        //   ・File.ResolveLinkTarget は reparse でないパス / 存在しないパスに対して null を返す
        //     か例外を投げる=どちらも「非該当」扱いで通す
        //
        // **この belt には網が 1 本も無く、実測でも到達させられなかった**(2026-08-31)。
        // 「潰しても全緑」なのは網の不足ではなく、fast path に構造的に先を越されるため:
        //   ・belt が非 null を返すのは実 SYMLINK / MOUNT_POINT の leaf だけ(実測。
        //     0x123 / 0x20000123 / WOF / WCI / CLOUD / PROJFS / DEDUP / APPEXECLINK は
        //     **すべて null**)。その 2 種はどちらも surrogate なので fast path が必ず先に Rejected。
        //   ・権限で fast path を盲にして belt だけ生かす構成も作れない。要求する権限は
        //     belt の方が**厳しい**(GetFileAttributesW は親の traverse だけ / ResolveLinkTarget は
        //     FindFirstFileEx + CreateFile)。実測: 親から ListDirectory+ReadAttributes を Deny すると
        //     fast path は読めたまま belt が UnauthorizedAccessException で落ち、対象自身に
        //     Deny FullControl を付けても fast path は tag=null で Rejected・belt は例外。
        //
        // したがって belt を「A-15 で fast path を緩めたことの保険」と読んではいけない。
        // **緩めて通るようになったタグ (CLOUD / WCI / PROJFS 等) に対して belt は null を返す**
        // = 保険として機能する余地がそもそも無い。撤去の是非は別途判断する(セキュリティ境界の
        // フェイルセーフを doc 修正のついでに消さない)。
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
