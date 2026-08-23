namespace kxEdit.Core.Text;

/// <summary>
/// 同一ファイル判定用の正規化キー。Windows 前提で大文字小文字を無視する。
/// 契約は 1 つだけ:<b>入力は正規化済みの絶対パス</b>で、
/// ここは<b>ファイルシステムに一切触れない</b>。
/// </summary>
/// <remarks>
/// <b>生入力版(<c>For</c>)は削除した</b>(Issue #48 / 最終ブランチレビュー Q-I-2・
/// public API の意図的な削除)。
/// <para>
/// 経緯: <c>Path.GetFullPath</c> は正規化後のパスに <c>~</c> が含まれると
/// <c>GetLongPathName</c> を呼ぶ。これは境界の無い実ファイルシステム / ネットワーク呼び出しで、
/// 不達の共有に対して約 21 秒 UI スレッドを止める(S-15・実測 2026-08-23)。
/// <c>For</c> はその <c>GetFullPath</c> を内包したまま、タブ数や履歴件数に比例して呼ばれる場所
/// (<c>DocumentManager.FindByPath</c> / <c>RecentFilesList.Add</c>)から使われていた=
/// S-15 の凶器そのもの。
/// </para>
/// <para>
/// 両方の呼出側を <see cref="ForNormalized"/> へ移した時点で <c>For</c> の実消費者はゼロに
/// なったが、<b>残しておくこと自体が罠だった</b>: より短く自然な名前が
/// <see cref="ForNormalized"/> の隣に並び、その doc が「相対パス・区切り文字差を吸収する」と
/// まさに欲しく見える能力を謳う。次にパス比較を書く人が最初に手を伸ばすのはそちらで、
/// 1 行で S-15 が戻る。消したことで、その再導入は実行時のリフレクション網ではなく
/// <b>コンパイルエラー</b>という強く安い保証で止まる。
/// </para>
/// <para>
/// 正規化が要るなら、ここではなく<b>境界付き seam</b>
/// (<c>IReachabilityProbe.NormalizePathWithTimeout</c>)を通す。1 操作につき多くとも 1 回。
/// </para>
/// <para>
/// <b>一緒に消えた副次契約</b>: <c>For</c> は <c>GetFullPath</c> の例外を空文字へ落として
/// 「正規化できない入力(埋め込み NUL 等)はまとめて 1 件」に集約していた(CSV-L-8)。
/// 唯一の消費者だった <c>RecentFilesList.Add</c> が <see cref="ForNormalized"/> へ移り、
/// 生の綴りのまま重複判定するようになったので、この契約には生きた消費者がいない
/// (件数は <c>RecentFilesList.MaxItems</c> で頭打ちなので増幅も起きない)。
/// </para>
/// </remarks>
public static class PathKey
{
    /// <summary>
    /// 正規化済み絶対パス用。小文字化するだけで、<b>ファイルシステムには一切触れない</b>。
    /// 呼出側が正規化済みパスを渡す契約(設計書 §3.1 の不変条件)。
    /// 区切り差(<c>/</c> と <c>\</c>)や <c>..</c> は吸収しない — 吸収させたくなったら
    /// それは呼出側が正規化を怠っているということなので、ここではなく呼出側を直す。
    /// </summary>
    public static string ForNormalized(string fullPath) =>
        string.IsNullOrEmpty(fullPath) ? string.Empty : fullPath.ToLowerInvariant();
}
