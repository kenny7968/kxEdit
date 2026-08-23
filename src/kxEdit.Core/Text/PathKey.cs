namespace kxEdit.Core.Text;

/// <summary>
/// 同一ファイル判定用の正規化キー。Windows 前提で大文字小文字を無視する。
/// 入力の契約で 2 つに分かれる:
/// <see cref="For"/> は生入力用で <c>GetFullPath</c> を通し、
/// <see cref="ForNormalized"/> は正規化済み絶対パス用で**ファイルシステムに触れない**。
/// 小文字化の規則そのものは <see cref="ForNormalized"/> が single source。
/// </summary>
/// <remarks>
/// Issue #48 (S-15): <c>Path.GetFullPath</c> は正規化後のパスに <c>~</c> が含まれると
/// <c>GetLongPathName</c> を呼ぶ。これは境界の無い実ファイルシステム / ネットワーク呼び出しで、
/// 不達の共有に対して約 21 秒 UI スレッドを止める(実測 2026-08-23)。
/// このため<b>タブ数や履歴件数に比例して <see cref="For"/> を呼ぶ経路を作ってはいけない</b>。
/// そういう経路(<c>DocumentManager.FindByPath</c> / <c>RecentFilesList.Add</c>)は
/// <see cref="ForNormalized"/> を使い、正規化は操作あたり 1 回・境界付きで済ませる。
/// </remarks>
public static class PathKey
{
    /// <summary>
    /// 生入力用。<c>GetFullPath</c> で相対パス・区切り文字差を吸収してからキー化する。
    /// 正規化できない場合は空文字を返し、「invalid はまとめて 1 件」に集約する（CSV-L-8）。
    /// <b>実 I/O を伴いうる</b>(remarks 参照)。UI スレッドから 1 操作につき 1 回を超えて
    /// 呼ばないこと。
    /// </summary>
    public static string For(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            // CSV-L-8 (v0.11): GetFullPath 例外時は攻撃者制御の生 path を返すのを避け、
            // 空文字（= dedup 用の invariant「invalid はまとめて 1 件」）に落とす。
            return string.Empty;
        }
        return ForNormalized(full);
    }

    /// <summary>
    /// 正規化済み絶対パス用。小文字化するだけで、<b>ファイルシステムには一切触れない</b>。
    /// 呼出側が正規化済みパスを渡す契約(設計書 §3.1 の不変条件)。
    /// 区切り差(<c>/</c> と <c>\</c>)や <c>..</c> は吸収しない — 吸収させたくなったら
    /// それは呼出側が正規化を怠っているということなので、ここではなく呼出側を直す。
    /// </summary>
    public static string ForNormalized(string fullPath) =>
        string.IsNullOrEmpty(fullPath) ? string.Empty : fullPath.ToLowerInvariant();
}
