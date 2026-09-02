using System.IO;
using System.Text;
using kxEdit.Core.Text;
using Microsoft.Web.WebView2.Core;

namespace kxEdit.App;

/// <summary>
/// MD-M-2: MarkdownPreviewForm 用の CSP HTTP header injector。
/// <see cref="CoreWebView2.WebResourceRequested"/> に登録して、preview 用 CSS 仮想パス
/// (<see cref="MarkdownRenderer.PreviewStylesheetPath"/>) へのリクエストを intercept し、
/// CSS 本体 + <c>Content-Security-Policy</c> ヘッダを含む response を返す。
/// (実 file は存在しない — <see cref="MarkdownRenderer.PreviewStylesheet"/> が single source of truth。)
/// <para>
/// preview 内の他リソース (画像/フォント等) は本 Injector を通さず、
/// <c>SetVirtualHostNameToFolderMapping</c> が直接応答する (filter を narrow に絞ったため
/// event 自体が発火しない)。それら sub-resource の CSP は HTML 内の
/// <c>&lt;meta http-equiv&gt;</c> が担保する (WebView2 API では passthrough 時の
/// response header 上書き手段が未提供)。
/// </para>
/// <para>
/// <b>防御レイヤの役割 (M-1 補正):</b>
/// <list type="bullet">
///   <item><c>&lt;meta http-equiv&gt;</c> CSP: HTML 文書本体 (<c>data:text/html</c> bootstrap
///     経由で HTTP header 注入不可) と passthrough sub-resource の CSP は meta が唯一の担保。</item>
///   <item>本 Injector の HTTP header CSP: <b>防御層として数えない</b> (V-6・2026-09-03)。
///     CSP はドキュメントとワーカーにのみ適用される仕様で、<b>CSS レスポンスに付けた
///     ヘッダは強制されない</b>。CSS の <c>@import</c> / <c>url(...)</c> を実際に縛って
///     いるのは<b>文書側</b>の <c>style-src</c> / <c>img-src</c> / <c>font-src</c>。
///     送出自体は <see cref="MarkdownRenderer.PreviewCspHeader"/> 定数を共有する
///     single source of truth として残す (害が無いため) が、旧記述の
///     「styles.css レスポンス自体を強化する defense-in-depth」「両者がブラウザ側で
///     intersect される」は<b>実在しない防御</b>だった。</item>
/// </list>
/// </para>
/// <para>
/// <b>NavigateToString bootstrap (仕様):</b> 初回 HTML の origin は
/// <c>data:text/html;...</c> URI であり、<see cref="CoreWebView2.WebResourceRequested"/> は
/// <c>data:</c> URI に対しては発火しない (WebView2 仕様)。従って初回 HTML 文書自身の
/// CSP は上記の通り <c>&lt;meta&gt;</c> 側が担う。
/// </para>
/// <para>
/// App 層内部にのみ露出するため <c>internal sealed</c>。テストは
/// <c>InternalsVisibleTo kxEdit.App.Tests</c> 経由でアクセスする。
/// </para>
/// </summary>
internal sealed class PreviewCspHeaderInjector
{
    // WebView2 の CreateWebResourceResponse は MIME sniff を持たないため
    // Content-Type を明示する。charset=utf-8 は PreviewStylesheet が ASCII 部分集合
    // であっても将来の多言語コメント混入時に備え固定。
    private const string StylesheetContentType = "text/css; charset=utf-8";

    // PreviewStylesheet は静的定数なので UTF-8 バイト列も 1 度だけ計算すれば足りる
    // (M-3: 毎リクエストで Encoding.UTF8.GetBytes を再実行する必要はない)。
    private static readonly byte[] StylesheetBytes = Encoding.UTF8.GetBytes(
        MarkdownRenderer.PreviewStylesheet
    );

    private readonly CoreWebView2Environment _env;

    public PreviewCspHeaderInjector(CoreWebView2Environment env)
    {
        _env = env ?? throw new ArgumentNullException(nameof(env));
    }

    /// <summary>
    /// <see cref="CoreWebView2"/> に URL filter と <see cref="CoreWebView2.WebResourceRequested"/>
    /// ハンドラを登録する。
    /// <para>
    /// 呼び出し順:
    /// <c>EnsureCoreWebView2Async(env)</c> → <c>Attach(core)</c> → <c>NavigateToString(html)</c>。
    /// filter/handler を装着してから NavigateToString しないと初回サブリソース要求
    /// (styles.css) で virtual CSS response を返す機会を失う。
    /// </para>
    /// <para>
    /// ハンドラ解除は WebView2 の <c>Dispose</c> と一緒に自動で行われる (Form 側の
    /// <c>Dispose(bool)</c> で <c>WebView2</c> コントロールが破棄されると event 購読も切れる)。
    /// </para>
    /// </summary>
    public void Attach(CoreWebView2 core)
    {
        // filter は styles.css の URL 完全一致まで narrow (M-2 補正)。
        // 画像/フォント等 passthrough リソースには event 自体を発火させない
        // (WebView2 API の URL wildcard は "https://host/path" 完全一致でマッチする)。
        // これにより Injector 内部の passthrough 分岐は「future-proof の defensive layer」
        // としてのみ残る (通常運用では到達しない)。
        core.AddWebResourceRequestedFilter(
            "https://"
                + MarkdownRenderer.PreviewVirtualHost
                + MarkdownRenderer.PreviewStylesheetPath,
            CoreWebView2WebResourceContext.Stylesheet
        );
        core.WebResourceRequested += OnWebResourceRequested;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!IsPreviewStylesheetRequest(e.Request.Uri))
        {
            // passthrough: virtual host mapping (画像等) に任せる。Response 未設定なので
            // WebView2 が既定パスで解決する。CSP は meta http-equiv fallback で担保。
            return;
        }

        // MemoryStream は WebView2 側が Read するので writable=false + 可視 buffer 不要。
        // 共有バッファ (StylesheetBytes) を各リクエストで新しい MemoryStream に巻き直す
        // (Stream は per-request で消費される)。バイト列自体の再割当ては不要。
        var stream = new MemoryStream(StylesheetBytes, writable: false);
        e.Response = _env.CreateWebResourceResponse(stream, 200, "OK", BuildResponseHeaders());
    }

    /// <summary>
    /// URL が preview 用 CSS 仮想パスへのリクエストか判定する pure helper。
    /// <list type="bullet">
    ///   <item>null / 空 / malformed URI → false (安全側: 誤 Response を装着しない)</item>
    ///   <item>scheme が <c>https</c> 以外 → false (preview 仮想ホストは https のみで張っている)</item>
    ///   <item>host が preview 仮想ホスト以外 → false</item>
    ///   <item>path 完全一致 (case-insensitive) → true</item>
    ///   <item>query / fragment は無視 (<see cref="Uri.AbsolutePath"/> が既に落とすため)</item>
    /// </list>
    /// </summary>
    internal static bool IsPreviewStylesheetRequest(string? requestUrl)
    {
        if (string.IsNullOrEmpty(requestUrl))
        {
            return false;
        }
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }
        if (!string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (
            !string.Equals(
                parsed.Host,
                MarkdownRenderer.PreviewVirtualHost,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return false;
        }
        return string.Equals(
            parsed.AbsolutePath,
            MarkdownRenderer.PreviewStylesheetPath,
            StringComparison.OrdinalIgnoreCase
        );
    }

    /// <summary>
    /// preview CSS response の HTTP headers を CRLF 区切り 1 本文字列で組み立てる。
    /// <see cref="CoreWebView2Environment.CreateWebResourceResponse"/> の <c>headers</c> 引数は
    /// この形式 (LF 単独では header が壊れる)。
    /// <para>
    /// 2 本のみ: <c>Content-Type</c> + <c>Content-Security-Policy</c>。CSP は
    /// <see cref="MarkdownRenderer.PreviewCspHeader"/> を参照 (meta 側と同一の
    /// single source of truth)。V-6: この CSP ヘッダは CSS レスポンスに付くので
    /// <b>ブラウザに強制されない</b> — 定数共有の一貫性のために出しているだけで、
    /// 防御層として数えないこと (詳細はクラスの doc)。
    /// </para>
    /// </summary>
    internal static string BuildResponseHeaders()
    {
        return "Content-Type: "
            + StylesheetContentType
            + "\r\n"
            + "Content-Security-Policy: "
            + MarkdownRenderer.PreviewCspHeader;
    }
}
