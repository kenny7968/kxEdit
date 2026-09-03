using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

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
    /// 無害化の対象になる「パス区切りになりうる形」。
    /// <list type="bullet">
    ///   <item><c>%2f</c> = '/' / <c>%5c</c> = '\' の percent-escape (16 進部は大小両方)。</item>
    ///   <item><b>生のバックスラッシュ</b>。<see cref="NeutralizeEncodedSeparators"/> は
    ///     Markdig の <c>LinkRewriter</c> から呼ばれる = <c>WriteEscapeUrl</c> が
    ///     エスケープする<b>前</b>の URL を受け取るので、ここには生の <c>\</c> が届く。
    ///     素通りさせると直後の <c>WriteEscapeUrl</c> がそれを <c>%5C</c> へ変換し、
    ///     出力 HTML には区切りエスケープが載る (F-4・実測)。</item>
    /// </list>
    /// 置換は <c>%</c> 自身をエスケープする形。16 進部の大小はマッチ全文
    /// (<c>Match.Value</c>) から持ち越して保存するので、キャプチャグループは使わない。
    /// </summary>
    private static readonly Regex SeparatorForms = new(
        @"%2[fF]|%5[cC]|\\",
        RegexOptions.CultureInvariant
    );

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
            // 変異させて kill を確認済み)。UserInfo は必須: Uri.Host も Uri.Authority も
            // userinfo を含まないため、"\/user@kxedit.preview/x" は
            // ホスト検査だけではすり抜ける (実測で確認済み)。
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

    /// <summary>
    /// URL が「<b>明確に外部</b>と分かる」ホストを指すかどうか。ホスト判定そのものは
    /// <see cref="MarkdownRenderer.TryIsPreviewHost"/> (Core 内 1 箇所・D) が持ち、
    /// ここが決めるのは<b>判断がつかないときにどちらへ倒すか</b>だけ。
    /// <para>
    /// <b>倒す向き: 判断がつかない = 外部ではない = 無害化する</b> (default-deny)。
    /// <c>NeutralizeEncodedSeparators</c> の doc にあるとおり、parse 不能な形
    /// (<c>https://%6bxedit.preview/…</c>) は WHATWG (Chromium) 側では
    /// <c>kxedit.preview</c> に解決されるので「外部だから触らない」に倒してはならない。
    /// IDNA 変換に失敗する形 (<c>https://xn--あ/…</c> / ホストに U+200B・U+FFFD) も同じ扱いで、
    /// 無害化しても実害は「区切りを含まない 1 ファイル名への要求になる」だけ。
    /// </para>
    /// <para>
    /// <b>App 層の <c>PreviewNavigationPolicy</c> とは倒す向きの意味が逆</b>である点に注意。
    /// あちらは「判断がつかない = Block」(safe by default)。共通化したのはホスト一致判定で
    /// あって、判断不能時の扱いではない。
    /// </para>
    /// </summary>
    private static bool IsClearlyExternalHost(Uri uri) =>
        MarkdownRenderer.TryIsPreviewHost(uri, out bool isPreviewHost) && !isPreviewHost;

    /// <summary>
    /// V-3 (監査 §9): preview 仮想ホスト宛の URL に残った区切り文字の密輸形
    /// (<c>%2f</c> / <c>%5c</c> / 生の <c>\</c>) を無害化する。<c>%</c> 自身をエスケープして
    /// <c>%252f</c> にするので、要求は「区切り文字を含まない 1 つのファイル名」になる。
    /// <para>
    /// <b>置き場所が要点。</b> <see cref="TryResolve"/> は絶対 URL に触らない
    /// (scheme 付きは早期 return する) ため、そちらの事後条件に置くと
    /// <c>![x](https://kxedit.preview/..%2f..%2fsecret.txt)</c> が素通りする (設計書 §14.1 の実測)。
    /// 本メソッドは <see cref="PreviewRelativeUrlExtension"/> が Markdig の
    /// <c>HtmlRenderer.LinkRewriter</c> として登録する。適用点は <c>WriteEscapeUrl</c> の中
    /// = <b>出力 HTML に URL を書く直前</b>で、AST 段 (<c>DocumentProcessed</c>) より後段になる。
    /// </para>
    /// <para>
    /// <b>覆う経路 (実測)</b>: 画像 <c>![](…)</c> / インラインリンク <c>[a](…)</c> /
    /// 角括弧宛先 <c>[a](&lt;…&gt;)</c> / CommonMark autolink <c>&lt;https://…&gt;</c> /
    /// GFM の裸 URL autolink / 参照リンク定義 / 表セル内リンク。<br/>
    /// <b>覆わない経路 (実測)</b>: 脚注リンク (<c>#fn:1</c> / <c>#fnref:1</c>)。
    /// <c>HtmlFootnoteLinkRenderer</c> が href を直書きし <c>WriteEscapeUrl</c> を通らないため
    /// <c>LinkRewriter</c> が発火しない。ただしこの href は Markdig が採番する固定形式で
    /// ユーザ入力が入らないので攻撃面にならない。<br/>
    /// <b>MediaLinks は経路自体を除去した (C・2026-09-03)</b>。
    /// <c>HtmlMediaLinkRenderer</c> も <c>WriteEscapeUrl</c> を通らないので、
    /// <c>&lt;video&gt;&lt;source src="…%2f…"&gt;</c> の形で区切りエスケープが出力へ残る
    /// (SafeLinkExtension を外した同一構成で実測)。実 pipeline でそれが起きていなかったのは
    /// <c>SafeLinkExtension.Setup</c> の <c>ObjectRenderers.Replace&lt;LinkInlineRenderer&gt;()</c> が
    /// MediaLinks の <c>TryWriters</c> ごと renderer を差し替えていたからで、
    /// <b>関門が効いていること自体が拡張の登録順への偶然の依存</b>だった。
    /// そのため <c>MarkdownRenderer.BuildPipeline</c> で <c>MediaLinkExtension</c> を除去した
    /// (出力差はゼロ・実測)。
    /// </para>
    /// <para>
    /// <b>判定は default-deny</b>。「parse できてホストが preview と一致したときだけ対象」に
    /// すると、.NET が parse に失敗する形 (<c>https://%6bxedit.preview/…</c> /
    /// <c>https://kxedit%2epreview/…</c>) が素通りする一方、WHATWG (Chromium) のホスト解析は
    /// 「percent-decode → domain-to-ASCII」なので同じ URL が <c>kxedit.preview</c> に解決される
    /// (F-2・Node/Ada で実測)。よって<b>明確に外部と分かるものだけ除外</b>し、
    /// 判断がつかないもの (parse 不能・相対・<b>IDNA 変換に失敗する形</b>) は無害化側へ倒す。
    /// <c>mailto:</c> 等は「parse できてホストが preview でない」に該当して除外される (実測)。
    /// 倒す向きの根拠は <see cref="IsClearlyExternalHost"/> の doc を参照。
    /// </para>
    /// <para>
    /// <c>System.Uri</c> はこれらのエスケープを復号しない (<c>AbsoluteUri</c> /
    /// <c>AbsolutePath</c> のいずれも大小込みで保持する・実測)。よって潰さない限り
    /// WebView2 まで生のまま届く。<b>推測 (未実測)</b>: WebView2 側が復号した場合は
    /// マッピング先フォルダーの外を指しうる / 無害化後は存在しないファイル名になり
    /// 404 で終わる —— 復号の成否も 404 の実挙動も L5 で確認する。
    /// </para>
    /// <para>
    /// 置換は URL 全体に掛けるので query / fragment の <c>%2f</c> も巻き込む。
    /// <b>推測 (未実測)</b>: 仮想ホストのファイル解決には query / fragment が使われないので
    /// 実害は無いはず (L5 で確認する)。
    /// URL を空にする案は採らない: <c>&lt;img src=""&gt;</c> の解決は data: 文書に対して曖昧で、
    /// ブラウザ依存の要求が飛びうるため。
    /// </para>
    /// </summary>
    /// <remarks>
    /// 戻り値の非 null 契約 (<see cref="NotNullIfNotNullAttribute"/>) は飾りではない。
    /// <c>LinkRewriter</c> は <c>Func&lt;string, string&gt;</c> なので、これが無いと
    /// メソッドグループ代入が CS8621 で警告になる (0 warning ゲートに引っかかる)。
    /// null を返すと <c>&lt;img src=""&gt;</c> が出力される (実測) ので、返してはならない。
    /// </remarks>
    [return: NotNullIfNotNull(nameof(url))]
    internal static string? NeutralizeEncodedSeparators(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url;
        }
        // 裸のフラグメントは同一文書内スクロール。preview 宛のファイル要求にならないので触らない
        // (TryResolve の # ガードと同じ理由)。これが無いと #a%2fb が無害化側へ落ちる。
        if (url[0] == '#')
        {
            return url;
        }
        // 明確に外部と分かるものだけ除外する。parse 不能・相対・IDNA 変換不能は
        // 下の無害化へ落ちる (default-deny)。
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) && IsClearlyExternalHost(parsed))
        {
            return url;
        }
        // 大小は保存する (%2F → %252F)。%20 など他の escape は触らない。
        // 生の '\' は Markdig が %5C を作る前に潰す (F-4)。
        return SeparatorForms.Replace(url, m => m.Value[0] == '%' ? "%25" + m.Value[1..] : "%255C");
    }
}
