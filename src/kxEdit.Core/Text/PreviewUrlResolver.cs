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
    /// URL のホストが preview 仮想ホストを指すかどうかを、ブラウザ側の解釈に寄せて判定する。
    /// <para>
    /// <see cref="Uri.Host"/> ではなく <see cref="Uri.IdnHost"/> を見るのが要点。
    /// <c>Host</c> は Unicode をそのまま保つので <c>https://kxedit。preview/</c> (U+3002) や
    /// <c>https://ｋxedit.preview/</c> (全角 k) が一致せず素通りするが、Markdig の
    /// <c>WriteEscapeUrl</c> は出力時に <c>IdnHost</c> で ASCII 化するため、
    /// 最終的な HTML は本物の preview ホスト宛になる (F-1・実測)。
    /// <c>IdnHost</c> は IDNA 正規化後の値 = 出力に載る値なので、こちらで比べる。
    /// </para>
    /// <para>
    /// 末尾ドットを落とすのは <c>https://kxedit.preview./x</c> 形のため (F-3・実測)。
    /// <c>Host</c> / <c>IdnHost</c> のいずれも末尾ドットを保持するので、明示的に削る。
    /// ドット 2 個以上 (<c>kxedit.preview../x</c>) は <c>Uri.TryCreate</c> 自体が失敗するので
    /// 呼び出し側の default-deny に落ちる (実測)。
    /// </para>
    /// </summary>
    private static bool PointsAtPreviewHost(Uri uri) =>
        string.Equals(
            uri.IdnHost.TrimEnd('.'),
            PreviewBase.IdnHost,
            StringComparison.OrdinalIgnoreCase
        );

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
    /// ユーザ入力が入らないので攻撃面にならない。
    /// </para>
    /// <para>
    /// <b>判定は default-deny</b>。「parse できてホストが preview と一致したときだけ対象」に
    /// すると、.NET が parse に失敗する形 (<c>https://%6bxedit.preview/…</c> /
    /// <c>https://kxedit%2epreview/…</c>) が素通りする一方、WHATWG (Chromium) のホスト解析は
    /// 「percent-decode → domain-to-ASCII」なので同じ URL が <c>kxedit.preview</c> に解決される
    /// (F-2・Node/Ada で実測)。よって<b>明確に外部と分かるものだけ除外</b>し、
    /// 判断がつかないもの (parse 不能・相対) は無害化側へ倒す。
    /// <c>mailto:</c> 等は「parse できてホストが preview でない」に該当して除外される (実測)。
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
        // 明確に外部と分かるものだけ除外する。parse 不能・相対は下の無害化へ落ちる (default-deny)。
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) && !PointsAtPreviewHost(parsed))
        {
            return url;
        }
        // 大小は保存する (%2F → %252F)。%20 など他の escape は触らない。
        // 生の '\' は Markdig が %5C を作る前に潰す (F-4)。
        return SeparatorForms.Replace(url, m => m.Value[0] == '%' ? "%25" + m.Value[1..] : "%255C");
    }
}
