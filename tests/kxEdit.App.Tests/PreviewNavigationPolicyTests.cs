namespace kxEdit.App.Tests;

/// <summary>
/// audit doc `docs/plans/2026-07-19-security-hardening-medium-low.md` §MD-M-1 / §MD-M-5、
/// および設計 doc `docs/plans/2026-07-22-preview-intra-nav-hardening-design.md` §MD-H-1。
/// MarkdownPreviewForm の WebView2 ナビゲーション対象 URI 分類の期待挙動を機械固定する。
///
/// 分類ルール:
///   - null / 空 → Block
///   - about:blank (大小区別なし) → AllowIntra (NavigateToString の初回 origin)
///   - preview 仮想ホスト宛の http / https → Block
///     * https は MD-H-1 (同梱 .html/.svg の same-origin 実行防止)
///     * http は F-7 (LaunchExternal だと既定ブラウザが実 DNS 解決してしまう)
///     * ホスト判定は Uri.IdnHost の末尾ドット除去。よって kxedit.preview. や
///       kxedit。preview (U+3002) も同じく Block (F-3)
///   - http/https で preview 仮想ホストを指さないもの → LaunchExternal (既定ブラウザへ逃がす)
///   - mailto → LaunchExternal
///   - file / ftp / data / javascript / vbscript / parse 不能 / その他 → Block
///     * file:// は特に MD-M-5 の NTLM 漏出対策の主眼
///     * javascript:/vbscript:/data: は MD-M-3 (renderer 段) の二層目
///
/// MarkdownPreviewForm 本体の event handler 配線は WebView2 runtime 依存で unit test 不可。
/// PR description に L5 manual smoke test 項目を書き残すことで代替する。
/// </summary>
public class PreviewNavigationPolicyTests
{
    [Fact]
    public void Classify_Null_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify(null)
        );

    [Fact]
    public void Classify_Empty_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("")
        );

    [Fact]
    public void Classify_AboutBlank_ReturnsAllowIntra() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.AllowIntra,
            PreviewNavigationPolicy.Classify("about:blank")
        );

    [Fact]
    public void Classify_AboutBlank_UpperCase_ReturnsAllowIntra() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.AllowIntra,
            PreviewNavigationPolicy.Classify("ABOUT:BLANK")
        );

    [Fact]
    public void Classify_HttpsPreviewHost_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("https://kxedit.preview/foo/bar.md")
        );

    /// <summary>大文字で書かれた URL でも Block になることを機械固定する。
    /// <para>
    /// 注意 (F-1): このテストではホスト段の <c>OrdinalIgnoreCase</c> は検証できない。
    /// <see cref="Uri.Host"/> は DNS ホストを常に小文字へ正規化するため、実装側の比較を
    /// <c>Ordinal</c> へ変えても結果が偶然一致し、このテストは緑のまま残る
    /// (ミューテーションが survive する)。<c>Uri.Scheme</c> も同様に常に小文字。
    /// ホスト段の <c>OrdinalIgnoreCase</c> はあくまで defensive layer であり、
    /// 本テストが実際に固定しているのは「大文字で書かれた URL でも Block」という入口の契約。
    /// </para></summary>
    [Fact]
    public void Classify_HttpsPreviewHost_UpperCaseUrl_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("HTTPS://KXEDIT.PREVIEW/x")
        );

    /// <summary>MD-H-1: 同梱 .html への in-frame 遷移 (相対リンク click) は Block。
    /// CSP 未適用の attacker HTML が same-origin でスクリプト実行するのを塞ぐ。</summary>
    [Fact]
    public void Classify_HttpsPreviewHost_HtmlFile_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("https://kxedit.preview/setup.html")
        );

    /// <summary>MD-H-1: 同梱 .svg への in-frame 遷移も Block (svg 内 script の same-origin 実行防止)。</summary>
    [Fact]
    public void Classify_HttpsPreviewHost_SvgFile_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("https://kxedit.preview/evil.svg")
        );

    [Fact]
    public void Classify_HttpsNonPreviewHost_ReturnsLaunchExternal() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.LaunchExternal,
            PreviewNavigationPolicy.Classify("https://example.com/")
        );

    [Fact]
    public void Classify_HttpNonPreviewHost_ReturnsLaunchExternal() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.LaunchExternal,
            PreviewNavigationPolicy.Classify("http://example.com/")
        );

    /// <summary>
    /// F-7: http の preview 仮想ホストも全面 Block。
    /// <para>
    /// 以前はここを <c>LaunchExternal</c> で固定していたが、それだと既定ブラウザが
    /// <c>kxedit.preview</c> を<b>実 DNS 解決</b>する (監査 V-2 と同じ漏れ方: 企業 DNS の
    /// search suffix 等に乗って「どの URL を踏ませたか」が外部へ出る)。
    /// </para>
    /// <para>
    /// <b>訂正 (2026-09-03)</b>: F-7 の commit はこれを「不変条件『この名前を実 DNS に
    /// 出さない』の<b>唯一の</b>残存経路」と書いたが、それは<b>偽</b>だった。ホスト判定が
    /// <c>Uri.Host</c> の直比較だったため、末尾ドット形 <c>http://kxedit.preview./leak</c> が
    /// <c>LaunchExternal</c> のまま残っていた (F-3・実測)。現在は
    /// <c>Uri.IdnHost</c> + 末尾ドット除去で判定しており、その網が
    /// <see cref="Classify_HttpPreviewHost_TrailingDot_ReturnsBlock"/> と
    /// <see cref="Classify_HttpsPreviewHost_TrailingDot_ReturnsBlock"/>。
    /// </para>
    /// <para>
    /// http は preview として in-frame 許可もしない (strict): 仮想ホストマッピングは
    /// https のみで張っているので、AllowIntra 化されないことも同時に固定している。
    /// </para>
    /// </summary>
    [Fact]
    public void Classify_HttpPreviewHost_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("http://kxedit.preview/")
        );

    /// <summary>
    /// F-3: 末尾ドット付きの preview 仮想ホスト (http) も Block。
    /// <para>
    /// DNS の絶対名表記なので既定ブラウザは <c>kxedit.preview</c> として実解決する。
    /// <c>Uri.Host</c> / <c>Uri.IdnHost</c> はどちらも末尾ドットを保持する (実測) ため、
    /// 実装側の <c>TrimEnd('.')</c> を外すとこのテストが落ちる。
    /// </para>
    /// </summary>
    [Fact]
    public void Classify_HttpPreviewHost_TrailingDot_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("http://kxedit.preview./leak")
        );

    /// <summary>F-3: 末尾ドット付きの preview 仮想ホスト (https) も Block (MD-H-1 と同じ理由)。</summary>
    [Fact]
    public void Classify_HttpsPreviewHost_TrailingDot_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("https://kxedit.preview./x.html")
        );

    /// <summary>
    /// F-3 の副産物: 非 ASCII 表記の preview 仮想ホストも Block。
    /// <para>
    /// <c>Uri.Host</c> は Unicode を保つので Host 直比較では外れるが、<c>Uri.IdnHost</c> は
    /// IDNA 正規化して <c>kxedit.preview</c> を返す (実測)。実装を <c>Uri.Host</c> へ戻すと
    /// このテストが落ちる。
    /// </para>
    /// <para>
    /// WebView2 が <c>NavigationStarting</c> にこの形を渡してくるかは<b>未実測 (推測)</b>。
    /// 正規化済みの URI を渡すと思われるが、確認していないので安全側に倒してある。
    /// </para>
    /// </summary>
    [Fact]
    public void Classify_HttpsPreviewHost_NonAsciiHost_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("https://kxedit。preview/x.html")
        );

    /// <summary>
    /// A (最終レビュー): ホストの IDNA 変換に<b>失敗する</b>形は Block へ倒す。
    /// <para>
    /// <c>Uri.TryCreate(..., Absolute)</c> は成功するのに <see cref="Uri.IdnHost"/> だけが
    /// <see cref="UriFormatException"/> を投げる形が実在する (実測)。F-1 / F-3 でホスト判定を
    /// <c>IdnHost</c> へ変えた時点では例外が <see cref="PreviewNavigationPolicy.Classify"/> から
    /// 抜けており、WebView2 の <c>NavigationStarting</c> ハンドラ経由で未捕捉になっていた。
    /// 倒す向きは「malformed = safe by default」と同じ Block
    /// (Core の <c>PreviewUrlResolver</c> は逆に「無害化する」側へ倒す)。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("https://xn--あ/x")] // 不正な punycode ラベル
    [InlineData("http://xn--あ/x")]
    // 次の 2 本のホストラベルは目に見えない / 化けて見える文字そのもの。codepoint を併記する。
    [InlineData("https://\U0000200B.example/x")] // U+200B ZERO WIDTH SPACE
    [InlineData("https://\U0000FFFD.example/x")] // U+FFFD REPLACEMENT CHARACTER
    public void Classify_IdnaUnconvertibleHost_ReturnsBlock(string uri) =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify(uri)
        );

    /// <summary>
    /// A の非退化対照: 正常な外部ホストは従来どおり LaunchExternal。
    /// 例外安全化が「全部 Block」へ退化していないことを固定する。
    /// </summary>
    [Fact]
    public void Classify_NormalExternalHost_ReturnsLaunchExternal() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.LaunchExternal,
            PreviewNavigationPolicy.Classify("https://example.com/x")
        );

    /// <summary>
    /// 非退化の対照: 末尾ドット付きの<b>外部</b>ホストは従来どおり LaunchExternal。
    /// <c>TrimEnd('.')</c> が preview 判定を広げすぎて外部 URL まで Block していないことを固定する。
    /// </summary>
    [Fact]
    public void Classify_HttpsNonPreviewHost_TrailingDot_ReturnsLaunchExternal() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.LaunchExternal,
            PreviewNavigationPolicy.Classify("https://example.com./x")
        );

    [Fact]
    public void Classify_MailtoUrl_ReturnsLaunchExternal() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.LaunchExternal,
            PreviewNavigationPolicy.Classify("mailto:a@b.example")
        );

    /// <summary>MD-M-5 の主眼: file:// UNC の NTLM ハッシュ漏出を navigation 段で確実に block。</summary>
    [Fact]
    public void Classify_FileUnc_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("file://server/share/x")
        );

    /// <summary>ローカル file:// も preview モーダル内のローカルファイル表示を防ぐため block。</summary>
    [Fact]
    public void Classify_FileLocal_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("file:///C:/secret.txt")
        );

    [Fact]
    public void Classify_JavascriptScheme_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("javascript:alert(1)")
        );

    [Fact]
    public void Classify_VbscriptScheme_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("vbscript:msgbox(1)")
        );

    [Fact]
    public void Classify_DataScheme_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("data:text/html,<script>alert(1)</script>")
        );

    [Fact]
    public void Classify_FtpScheme_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("ftp://server/x")
        );

    /// <summary>allow list 方式なので未知 scheme は既定 Block (safe by default)。</summary>
    [Fact]
    public void Classify_UnknownScheme_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("foo:bar")
        );

    /// <summary>Uri.TryCreate が失敗する malformed 入力は Block (safe by default)。</summary>
    [Fact]
    public void Classify_MalformedUri_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("not a url")
        );

    // -----------------------------------------------------------------------
    // IsNavigateToStringBootstrapUri: WebView2 の NavigateToString(html) は
    // 内部的に HTML を data:text/html;charset=utf-16;base64,... の data URI に
    // エンコードして NavigationStarting を発火させる。この初回だけ通すための
    // 検出ヘルパ。通常の Classify() では data: を Block しつづける (MD-M-3 二層防御)。
    // -----------------------------------------------------------------------

    [Fact]
    public void IsBootstrap_DataTextHtml_ReturnsTrue() =>
        Assert.True(
            PreviewNavigationPolicy.IsNavigateToStringBootstrapUri(
                "data:text/html;charset=utf-16;base64,PGh0bWw+PC9odG1sPg=="
            )
        );

    [Fact]
    public void IsBootstrap_DataTextHtml_UpperCase_ReturnsTrue() =>
        Assert.True(
            PreviewNavigationPolicy.IsNavigateToStringBootstrapUri(
                "DATA:TEXT/HTML;charset=utf-8,<html></html>"
            )
        );

    [Fact]
    public void IsBootstrap_Null_ReturnsFalse() =>
        Assert.False(PreviewNavigationPolicy.IsNavigateToStringBootstrapUri(null));

    [Fact]
    public void IsBootstrap_Empty_ReturnsFalse() =>
        Assert.False(PreviewNavigationPolicy.IsNavigateToStringBootstrapUri(""));

    [Fact]
    public void IsBootstrap_AboutBlank_ReturnsFalse() =>
        Assert.False(PreviewNavigationPolicy.IsNavigateToStringBootstrapUri("about:blank"));

    [Fact]
    public void IsBootstrap_HttpsPreview_ReturnsFalse() =>
        Assert.False(
            PreviewNavigationPolicy.IsNavigateToStringBootstrapUri("https://kxedit.preview/x")
        );

    /// <summary>data:image/... 等の非 text/html data URI は bootstrap とみなさない
    /// (最上位ナビゲートには通常出ないが、防御的に text/html に限定)。</summary>
    [Fact]
    public void IsBootstrap_DataImageSvg_ReturnsFalse() =>
        Assert.False(
            PreviewNavigationPolicy.IsNavigateToStringBootstrapUri(
                "data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg'/>"
            )
        );

    /// <summary>bootstrap 通過後の data:text/html は依然として Classify で Block される
    /// こと (MD-M-3 二層防御が生きる) を機械固定。IsNavigateToStringBootstrapUri は
    /// 単なる検出 helper で、通過の可否はフォーム側の one-shot flag が決める。</summary>
    [Fact]
    public void Classify_DataTextHtml_ReturnsBlock() =>
        Assert.Equal(
            PreviewNavigationPolicy.Classification.Block,
            PreviewNavigationPolicy.Classify("data:text/html;charset=utf-16;base64,PGh0bWw+")
        );
}
