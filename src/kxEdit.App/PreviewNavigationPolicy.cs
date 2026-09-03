using kxEdit.Core.Text;

namespace kxEdit.App;

/// <summary>
/// MarkdownPreviewForm の WebView2 ナビゲーション対象 URI を「preview 内で許可」
/// 「既定ブラウザ/アプリで開く」「阻止」の 3 クラスに分類する純粋ロジック。
/// <para>
/// WebView2 の <c>NavigationStarting</c> / <c>NewWindowRequested</c> ハンドラから
/// 呼ばれる。Process.Start の副作用や WebView2 依存を持たないため単体テスト可能。
/// </para>
/// <para>
/// 攻撃面(audit doc MD-M-1 / MD-M-5、および設計 doc
/// <c>docs/plans/2026-07-22-preview-intra-nav-hardening-design.md</c> MD-H-1):
/// <list type="bullet">
///   <item>同梱 <c>https://kxedit.preview/*.html</c>/<c>.svg</c> への in-frame 遷移を Block
///     (attacker フォルダ由来の CSP 未適用ドキュメントが same-origin でスクリプト実行するのを塞ぐ・MD-H-1)。</item>
///   <item>preview 仮想ホストは <c>http</c> でも Block (<c>LaunchExternal</c> だと既定ブラウザが
///     <c>kxedit.preview</c> を実 DNS 解決してしまう・F-7)。ホスト判定は <c>Uri.IdnHost</c> の
///     末尾ドット除去で行い、<c>kxedit.preview.</c> / <c>kxedit。preview</c> も同じ扱いにする
///     (F-3。F-7 の時点では <c>Uri.Host</c> 直比較だったのでこれらが漏れていた)。</item>
///   <item>外部 http/https は in-frame ナビゲートさせず既定ブラウザへ逃がす
///     (プレビュー窓の title を保ったまま偽サイトが表示される phishing 防止)。</item>
///   <item><c>file://</c> UNC は Windows が SMB 経由で NTLM 認証を通してしまうため
///     全面 Block (NTLMv2 challenge/response のオフラインクラック用漏洩防止)。</item>
///   <item><c>javascript:</c>/<c>vbscript:</c>/<c>data:</c> 等の script scheme は
///     全面 Block (renderer 段の SafeLinkExtension を弱めた瞬間の live XSS 二層防御)。</item>
/// </list>
/// </para>
/// </summary>
public static class PreviewNavigationPolicy
{
    /// <summary>ナビゲーション分類。</summary>
    public enum Classification
    {
        /// <summary>preview 内で許可 (about:blank のみ)。MD-H-1 / F-7 以降 http(s)://kxedit.preview/* は Block。</summary>
        AllowIntra,

        /// <summary>
        /// 既定ブラウザ/アプリで開く安全 scheme (http/https で preview 仮想ホストを指さないもの, mailto)。
        /// <para>
        /// 「preview を指さない」の判定は <c>Uri.IdnHost</c> の末尾ドット除去で行う
        /// (実装は <see cref="MarkdownRenderer.TryIsPreviewHost"/>)。
        /// F-3 以前は <c>Uri.Host</c> の直比較だったため、<c>http://kxedit.preview./leak</c> が
        /// ここへ落ちていた。
        /// </para>
        /// <para>
        /// <b>実測と推定の切り分け (F-3・訂正)</b>: 実測しているのは
        /// 「<c>Classify</c> がこの形に <see cref="LaunchExternal"/> を返していた」ところまで。
        /// そこから先の「既定ブラウザが <c>kxedit.preview</c> を実 DNS 解決する」は
        /// <b>推測 (未実測)</b> —— DNS クエリが実際に出たことは測っていない。
        /// 旧記述はこの 2 つをまとめて「(実測)」と書いていた。
        /// </para>
        /// </summary>
        LaunchExternal,

        /// <summary>
        /// 阻止 (preview 仮想ホスト宛の http(s), file://, ftp://, data:, javascript:, vbscript:,
        /// parse 不能, その他 unknown)。preview 仮想ホスト宛には末尾ドット形
        /// (<c>kxedit.preview.</c>) と非 ASCII 表記 (<c>kxedit。preview</c>) も含む。
        /// ホストの IDNA 変換自体に失敗する形 (<c>https://xn--あ/x</c> 等) も
        /// 「判断がつかない」として Block へ倒す (A・<see cref="PointsAtPreviewHostOrUndecidable"/>)。
        /// </summary>
        Block,
    }

    /// <summary>
    /// URI のホストが preview 仮想ホストを指すか、<b>または判断がつかない</b>か。
    /// ホスト一致判定そのものは <see cref="MarkdownRenderer.TryIsPreviewHost"/>
    /// (Core 内 1 箇所・D) が持ち、ここが決めるのは判断不能時にどちらへ倒すかだけ。
    /// <para>
    /// <b>倒す向き: 判断がつかない = <see cref="Classification.Block"/></b>。
    /// <c>Uri.TryCreate</c> の失敗を Block へ倒している「malformed = safe by default」と同じ
    /// 向きで、外部起動 (実 DNS 解決) へ落ちる形を作らないためのもの。
    /// <see cref="Uri.IdnHost"/> は <c>TryCreate</c> が成功した URI に対してすら
    /// <see cref="UriFormatException"/> を投げる (<c>https://xn--あ/x</c> 等・実測) ので、
    /// この経路は実在する (A・本ブランチが作った退行)。
    /// </para>
    /// <para>
    /// <b>Core 側の <c>PreviewUrlResolver</c> とは倒す向きの意味が逆</b>である点に注意。
    /// あちらは「判断がつかない = 無害化する」(default-deny)。共通化したのはホスト一致判定で
    /// あって、判断不能時の扱いではない。
    /// </para>
    /// <para>
    /// 一致判定を <see cref="Uri.Host"/> 直比較でやると <c>http://kxedit.preview./leak</c> が
    /// 「preview ではない」と判定されて <see cref="Classification.LaunchExternal"/> に落ちる
    /// (F-3・実測)。<see cref="MarkdownRenderer.TryIsPreviewHost"/> は末尾ドットを削り、
    /// 非 ASCII ホスト (<c>kxedit。preview</c> 等) も IDNA 正規化して同じ土俵に載せる。
    /// </para>
    /// </summary>
    private static bool PointsAtPreviewHostOrUndecidable(Uri uri) =>
        !MarkdownRenderer.TryIsPreviewHost(uri, out bool isPreviewHost) || isPreviewHost;

    /// <summary>
    /// WebView2 の navigation 対象 URI を 3 クラスに分類する。
    /// </summary>
    /// <param name="uri">WebView2 の <c>NavigationStartingEventArgs.Uri</c> 相当の絶対 URI 文字列。</param>
    /// <returns>分類結果。詳細は <see cref="Classification"/> 参照。</returns>
    public static Classification Classify(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return Classification.Block;
        }

        // about:blank は NavigateToString の初回 origin として WebView2 が渡してくるため
        // 明示的に許可する。Uri.TryCreate は about:blank を Scheme="about" として parse
        // するので後段の switch より前に string 比較で片付ける (path が blank 以外の
        // about:* を巻き込まない)。
        if (string.Equals(uri, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return Classification.AllowIntra;
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed))
        {
            return Classification.Block; // malformed = safe by default
        }

        string scheme = parsed.Scheme.ToLowerInvariant();
        return scheme switch
        {
            // preview 仮想ホストは全面 Block (MD-H-1)。
            // preview 本体は NavigateToString (data: URI) 経由、CSS は WebResourceRequested の
            // サブリソース経由で届く。画像はこの経路を通らない —— PreviewCspHeaderInjector の
            // filter は CSS の仮想パスへ narrow に絞ってあり、画像は
            // SetVirtualHostNameToFolderMapping が直接応答する。いずれにせよ正当な
            // top-level ナビゲーションがこのホストを対象にすることは無い。
            // ここを AllowIntra にすると、攻撃者が .md と同梱した CSP 未適用の
            // .html/.svg への相対リンク click が in-frame 遷移し、same-origin でスクリプト実行
            // (兄弟ファイル fetch + 外部 exfiltration) されてしまう。よって Block する。
            //
            // http / https のどちらでも preview 仮想ホストは全面 Block。
            // かつてこのコメントは「kxedit.preview は実ホストではないので LaunchExternal しても
            // 無意味」と書いていたが、無意味ではない —— 既定ブラウザが実 DNS 解決を行い、
            // 企業 DNS の search suffix 等で「どの URL を踏ませたか」が外部へ漏れる
            // (監査 §9 V-2 と同じ経路。2026-09-03・F-7)。
            "http" or "https" when PointsAtPreviewHostOrUndecidable(parsed) => Classification.Block,
            "http" or "https" => Classification.LaunchExternal,
            "mailto" => Classification.LaunchExternal,
            // file:// UNC は Windows が SMB で NTLM 認証を通してしまうため全面 Block (MD-M-5)。
            // ftp / data / javascript / vbscript / その他未知 scheme も既定 Block (safe by default)。
            _ => Classification.Block,
        };
    }

    /// <summary>
    /// WebView2 の <c>CoreWebView2.NavigateToString(html)</c> 起点の
    /// NavigationStarting.Uri かどうかを検出する。
    /// <para>
    /// WebView2 は NavigateToString の内部実装で HTML を
    /// <c>data:text/html;charset=utf-16;base64,...</c> の data URI へエンコードして
    /// ナビゲートする (Microsoft docs の "origin will be about:blank" は origin の
    /// 話であって NavigationStarting.Uri ではない、というのが本ヘルパの由来)。
    /// この初回だけ通さないと preview 本体の HTML が描画されず about:blank のまま残る。
    /// </para>
    /// <para>
    /// 通過制御 (one-shot flag) はフォーム側の責務で、本ヘルパはピュアな検出関数。
    /// bootstrap 済 (通常時) の <c>data:</c> はフォーム側でこの分岐を通さず
    /// <see cref="Classify"/> に落ちるため引き続き Block される (MD-M-3 二層防御)。
    /// </para>
    /// </summary>
    public static bool IsNavigateToStringBootstrapUri(string? uri) =>
        uri != null && uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase);
}
