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
/// <para>
/// 中核の不変条件: <b>書き換え先は必ず preview 仮想ホスト origin である</b>。
/// 前置ガードの前方一致では担保できないため、解決結果に事後条件を課して保証する。
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
        // protocol-relative (//host/p) の早期 return。これは前方一致にすぎず保証にはならない
        // (先頭のバックスラッシュや空白/タブが付くと素通りする)。真の保証は下の事後条件が与える。
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
            var resolved = new Uri(PreviewBase, url);
            // 事後条件: 絶対化は origin を変えてはならない。前方一致のガード (規則 3) だけでは
            // 先頭のバックスラッシュ・空白・タブが付いた protocol-relative が素通りする形が
            // 実在する (Uri は先頭空白を捨て、バックスラッシュを / へ正規化してから authority を
            // 解釈する)。列挙は原理的に漏れるので、解決結果側で preview origin を検査する。
            // Host / Port / UserInfo の 3 条件はそれぞれ単独で網が張ってある (条件ごとに
            // 変異させて kill を確認済み)。UserInfo は必須: Host も
            // GetLeftPart(UriPartial.Authority) も userinfo を含まないため、
            // "\/user@kxedit.preview/x" はホスト検査だけではすり抜ける。
            // Scheme 検査だけは現状到達不能 (変異させても全緑)。相対解決で scheme が変わるには
            // url 自身が scheme を持つ必要があり、それは上の Uri.TryCreate(Absolute) が
            // 先に捕まえるため。System.Uri の解釈が将来変わったときの保険として残す。
            if (
                !string.Equals(resolved.Scheme, PreviewBase.Scheme, StringComparison.Ordinal)
                || !string.Equals(
                    resolved.Host,
                    PreviewBase.Host,
                    StringComparison.OrdinalIgnoreCase
                )
                || resolved.Port != PreviewBase.Port
                || !string.IsNullOrEmpty(resolved.UserInfo)
            )
            {
                return false;
            }
            // AbsoluteUri は percent-escape を保った正規形。ToString() は表示用に復号するため
            // out 値に生の < や " が載り、安全性が下流 (Markdig の WriteEscapeUrl) 依存になる。
            absolute = resolved.AbsoluteUri;
            return true;
        }
        catch (UriFormatException)
        {
            return false; // 解決不能は安全側に倒して書き換えない
        }
    }
}
