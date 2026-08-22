namespace kxEdit.Core.Text;

/// <summary>
/// プレビュー本文中の URL を、preview 仮想ホスト基準の絶対 URL へ解決する純粋ロジック。
/// <para>
/// A-2 / 設計書 §7: プレビュー文書は <c>NavigateToString</c> 経由で origin が
/// <c>data:text/html;...</c> になるため、相対 URL の解決基準を持たない。
/// <c>&lt;base href&gt;</c> を置く案は、裸のフラグメント URL (<c>#section</c>) まで base 基準で
/// 解決してしまい、目次リンクと脚注の戻りリンクが MD-H-1 の Block に巻き込まれて全滅するため
/// 採らない。代わりに描画前の AST 段でここが絶対化する。
/// </para>
/// </summary>
internal static class PreviewUrlResolver
{
    private static readonly Uri PreviewBase = new(MarkdownRenderer.PreviewBaseHref);

    /// <summary>
    /// 相対 URL なら preview 仮想ホスト基準の絶対 URL を返す。書き換え不要なら false。
    /// 判定順は設計書 §7.2 の表のとおり。
    /// </summary>
    internal static bool TryResolve(string? url, out string? absolute)
    {
        absolute = null;
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }
        // FINDING 3: 裸のフラグメントは同一文書内スクロールなので絶対に触らない。
        if (url[0] == '#')
        {
            return false;
        }
        // protocol-relative は new Uri(base, "//host/p") が別ホストへ飛ぶので触らない。
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }
        // scheme 付きは SafeLinkExtension の whitelist が扱う (javascript: 等)。
        if (Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return false;
        }
        try
        {
            absolute = new Uri(PreviewBase, url).ToString();
            return true;
        }
        catch (UriFormatException)
        {
            return false; // 解決不能は安全側に倒して書き換えない
        }
    }
}
