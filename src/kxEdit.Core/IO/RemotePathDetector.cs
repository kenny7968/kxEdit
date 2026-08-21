namespace kxEdit.Core.IO;

/// <summary>
/// パスが「リモートアクセスを要する」かを判定する述語。UNC(先頭 <c>\\</c>)に加えて
/// マップドネットワークドライブ(<c>X:\</c> → <c>\\server\share</c>)も true として扱う。
/// HIGH-6 の <see cref="UncPathDetector"/>(純粋文字列述語)を内包し、DOS ドライブ文字の
/// 場合は <see cref="System.IO.DriveInfo.DriveType"/> が <see cref="System.IO.DriveType.Network"/>
/// なら true を返す。CSV-M-1 でリーチャビリティプローブ(FileController 側)の対象を
/// マップドドライブへ拡張するために追加。
/// </summary>
/// <remarks>
/// 命名を「Unc」ではなく「Remote」にする理由: <see cref="UncPathDetector.IsUnc"/> は
/// 「先頭 <c>\\</c> の純粋文字列判定」として意味的にも命名的にも妥当で、OS 依存の
/// DriveInfo 検査を混ぜると「Unc という名前なのに Network drive も見る=誤解」となる。
/// 新規クラスに分けることで Core 層の純粋述語を守り、OS 依存(DriveInfo)を隔離する。
///
/// silent fallback ポリシー: <see cref="System.IO.DriveInfo"/> は不正なドライブ文字や
/// パス解析失敗で <see cref="System.ArgumentException"/> を投げる可能性がある。呼出側
/// (FileController.LoadInto)は try/catch を持たない設計のため、ここで例外を握って
/// false(= 非リモート扱い=通常経路へフォールバック)を返す。TextFileService 側で
/// 適切な IOException を発火させる意図。
/// </remarks>
public static class RemotePathDetector
{
    public static bool IsRemote(string path)
    {
        // UNC は UncPathDetector と対称=先頭 `\\` の純粋判定が true を返す経路。
        // Network drive の追加チェック無しで直ちに true を返す(early true)。
        if (UncPathDetector.IsUnc(path))
            return true;

        // 相対パス / 空文字 は DriveInfo にかけずに false 返却(early return)。
        // Path.GetPathRoot は相対パスに対して空文字を、null に対して null を返す。
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return false;

        try
        {
            // DriveInfo は「マップされていないドライブ文字」も受け入れ、DriveType=NoRootDirectory
            // を返す契約=存在しないドライブ("Q:\\")は Network 以外に落ちて false になる。
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            // 不正パス文字 / 不正ドライブ形式は silent fallback(= 非リモート扱い)。
            return false;
        }
    }
}
