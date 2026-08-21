using kxEdit.Core.Text;

namespace kxEdit.App.Tests;

/// <summary>
/// MD-M-2: PreviewCspHeaderInjector の pure helper (URL 判定 + response headers 組立)
/// を機械固定する。
///
/// 分離テスト戦略:
///   - CoreWebView2 / CoreWebView2Environment は WebView2 runtime 依存で unit test 不可。
///   - 判定ロジック (IsPreviewStylesheetRequest) と header 組立 (BuildResponseHeaders) は
///     pure static helper に抽出済 → ここで単独テスト。
///   - 実 event 発火 + Response 装着経路は L5 実機検証 (DevTools で response headers 確認)。
///
/// 契約 (IsPreviewStylesheetRequest):
///   - null / 空 / malformed → false
///   - scheme が https 以外 → false (http:// はプレビュー仮想ホスト非対応)
///   - host が preview 仮想ホスト以外 → false (他ホストを Injector が差し替えない)
///   - path 完全一致 (case-insensitive) → true
///   - query / fragment は無視
/// </summary>
public class PreviewCspHeaderInjectorTests
{
    // ---------------------------------------------------------------------
    // IsPreviewStylesheetRequest — URL 判定 pure helper
    // ---------------------------------------------------------------------

    [Fact]
    public void IsPreviewStylesheetRequest_ExactUrl_ReturnsTrue() =>
        Assert.True(
            PreviewCspHeaderInjector.IsPreviewStylesheetRequest(
                "https://yedit.preview/_yedit/styles.css"
            )
        );

    [Fact]
    public void IsPreviewStylesheetRequest_UpperCaseHostAndPath_ReturnsTrue()
    {
        // URL は case-insensitive (scheme/host は URI 仕様上大小区別なし、path は defensively 揃える)。
        // preview はローカル仮想ホストのみを対象とするため OS 依存の case sensitivity は
        // 気にしない (Windows FS 由来のパス比較と一致させる)。
        Assert.True(
            PreviewCspHeaderInjector.IsPreviewStylesheetRequest(
                "HTTPS://YEDIT.PREVIEW/_YEDIT/STYLES.CSS"
            )
        );
    }

    [Fact]
    public void IsPreviewStylesheetRequest_WithQueryString_ReturnsTrue()
    {
        // キャッシュバスト等で ?v=1 が付いても path は変わらないため一致扱い。
        Assert.True(
            PreviewCspHeaderInjector.IsPreviewStylesheetRequest(
                "https://yedit.preview/_yedit/styles.css?v=1"
            )
        );
    }

    [Fact]
    public void IsPreviewStylesheetRequest_WithFragment_ReturnsTrue()
    {
        // CSS URL に fragment が付くことは無いが Uri.AbsolutePath は fragment を落とすため true。
        // 挙動を機械固定して回帰保護。
        Assert.True(
            PreviewCspHeaderInjector.IsPreviewStylesheetRequest(
                "https://yedit.preview/_yedit/styles.css#top"
            )
        );
    }

    [Fact]
    public void IsPreviewStylesheetRequest_OtherPathOnPreviewHost_ReturnsFalse()
    {
        // preview 内の他リソース (画像等) は passthrough する契約なので false を返す必要がある。
        Assert.False(
            PreviewCspHeaderInjector.IsPreviewStylesheetRequest("https://yedit.preview/photo.png")
        );
    }

    [Fact]
    public void IsPreviewStylesheetRequest_PreviewRoot_ReturnsFalse() =>
        Assert.False(PreviewCspHeaderInjector.IsPreviewStylesheetRequest("https://yedit.preview/"));

    [Fact]
    public void IsPreviewStylesheetRequest_HttpScheme_ReturnsFalse()
    {
        // preview 仮想ホストは https のみで張っている。http:// のリクエストは
        // Injector が偽装 CSS 応答を返さない (他ミドルウェアが処理する余地を残す)。
        Assert.False(
            PreviewCspHeaderInjector.IsPreviewStylesheetRequest(
                "http://yedit.preview/_yedit/styles.css"
            )
        );
    }

    [Fact]
    public void IsPreviewStylesheetRequest_OtherHost_ReturnsFalse()
    {
        // 他ホストの同名 path (evil.com が /_yedit/styles.css を持っていても) は差し替えない。
        Assert.False(
            PreviewCspHeaderInjector.IsPreviewStylesheetRequest(
                "https://evil.com/_yedit/styles.css"
            )
        );
    }

    [Fact]
    public void IsPreviewStylesheetRequest_Null_ReturnsFalse() =>
        Assert.False(PreviewCspHeaderInjector.IsPreviewStylesheetRequest(null));

    [Fact]
    public void IsPreviewStylesheetRequest_Empty_ReturnsFalse() =>
        Assert.False(PreviewCspHeaderInjector.IsPreviewStylesheetRequest(string.Empty));

    [Fact]
    public void IsPreviewStylesheetRequest_Malformed_ReturnsFalse()
    {
        // WebView2 が通常発火する URI は絶対 URI だが、不正な入力に対しても false で
        // 安全側 (誤 Response を装着しない)。
        Assert.False(PreviewCspHeaderInjector.IsPreviewStylesheetRequest("not a url"));
    }

    // M-4 補正: 完全一致契約を near-miss suffix で機械固定。将来 StartsWith 等への
    // 実装差し替え (contract 弛緩) を silent に許さないための tripwire。

    [Fact]
    public void IsPreviewStylesheetRequest_SuffixExtension_ReturnsFalse() =>
        Assert.False(
            PreviewCspHeaderInjector.IsPreviewStylesheetRequest(
                "https://yedit.preview/_yedit/styles.css.txt"
            )
        );

    [Fact]
    public void IsPreviewStylesheetRequest_SuffixCharacter_ReturnsFalse() =>
        Assert.False(
            PreviewCspHeaderInjector.IsPreviewStylesheetRequest(
                "https://yedit.preview/_yedit/styles.cssx"
            )
        );

    [Fact]
    public void IsPreviewStylesheetRequest_PathPrefixMismatch_ReturnsFalse() =>
        Assert.False(
            PreviewCspHeaderInjector.IsPreviewStylesheetRequest(
                "https://yedit.preview/_yedit/styles.css/extra"
            )
        );

    // ---------------------------------------------------------------------
    // BuildResponseHeaders — CRLF 区切り HTTP header 文字列
    // WebView2.CreateWebResourceResponse は headers 引数を CRLF 区切りで受け取る。
    // ---------------------------------------------------------------------

    [Fact]
    public void BuildResponseHeaders_ContainsContentType()
    {
        // CSS を返すため text/css + charset=utf-8 を明示 (WebView2 の MIME sniff に依存させない)。
        string headers = PreviewCspHeaderInjector.BuildResponseHeaders();
        Assert.Contains("Content-Type: text/css; charset=utf-8", headers);
    }

    [Fact]
    public void BuildResponseHeaders_ContainsCspFromMarkdownRenderer()
    {
        // meta と HTTP header の CSP が同一定数を参照する single source of truth 契約。
        // ここが破れたら二経路で防御差が生まれる = レビュー強制。
        string headers = PreviewCspHeaderInjector.BuildResponseHeaders();
        Assert.Contains("Content-Security-Policy: " + MarkdownRenderer.PreviewCspHeader, headers);
    }

    [Fact]
    public void BuildResponseHeaders_UsesCrlfSeparator()
    {
        // WebView2 の headers 引数は CRLF 区切り (LF 単独では不正で header が壊れる)。
        // 少なくとも 1 つの CRLF (Content-Type と CSP の間) を機械固定。
        string headers = PreviewCspHeaderInjector.BuildResponseHeaders();
        Assert.Contains("\r\n", headers);
    }

    [Fact]
    public void BuildResponseHeaders_HasExactlyTwoHeaders()
    {
        // Content-Type + Content-Security-Policy の 2 本のみ。
        // 別の header が silent に混入するのを検知するため件数を固定。
        string headers = PreviewCspHeaderInjector.BuildResponseHeaders();
        string[] lines = headers.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
    }
}
