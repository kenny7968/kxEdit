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
///     <c>kxedit.preview</c> を実 DNS 解決してしまう・F-7)。</item>
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

        /// <summary>既定ブラウザ/アプリで開く安全 scheme (http/https 非 preview, mailto)。</summary>
        LaunchExternal,

        /// <summary>
        /// 阻止 (http(s)://kxedit.preview/*, file://, ftp://, data:, javascript:, vbscript:, その他 unknown)。
        /// </summary>
        Block,
    }

    /// <summary>
    /// preview 仮想ホスト。この host への top-level ナビゲーションは Block する (MD-H-1)。
    /// MarkdownRenderer 側の VirtualHost マッピング (App の SetVirtualHostNameToFolderMapping)
    /// と単一の source of truth を共有し、host 名の判定基準がずれないようにする。
    /// </summary>
    private static readonly string PreviewHost = MarkdownRenderer.PreviewVirtualHost;

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
            // preview 本体は NavigateToString (data: URI) 経由、CSS/画像は WebResourceRequested
            // のサブリソース経由で届くため、正当な top-level ナビゲーションがこのホストを対象にする
            // ことは無い。ここを AllowIntra にすると、攻撃者が .md と同梱した CSP 未適用の
            // .html/.svg への相対リンク click が in-frame 遷移し、same-origin でスクリプト実行
            // (兄弟ファイル fetch + 外部 exfiltration) されてしまう。よって Block する。
            //
            // http / https のどちらでも preview 仮想ホストは全面 Block。
            // かつてこのコメントは「kxedit.preview は実ホストではないので LaunchExternal しても
            // 無意味」と書いていたが、無意味ではない —— 既定ブラウザが実 DNS 解決を行い、
            // 企業 DNS の search suffix 等で「どの URL を踏ませたか」が外部へ漏れる
            // (監査 §9 V-2 と同じ経路。2026-09-03・F-7)。
            "http"
            or "https"
                when string.Equals(parsed.Host, PreviewHost, StringComparison.OrdinalIgnoreCase) =>
                Classification.Block,
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
