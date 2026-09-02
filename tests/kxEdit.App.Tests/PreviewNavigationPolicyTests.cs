namespace kxEdit.App.Tests;

/// <summary>
/// audit doc `docs/plans/2026-07-19-security-hardening-medium-low.md` §MD-M-1 / §MD-M-5、
/// および設計 doc `docs/plans/2026-07-22-preview-intra-nav-hardening-design.md` §MD-H-1。
/// MarkdownPreviewForm の WebView2 ナビゲーション対象 URI 分類の期待挙動を機械固定する。
///
/// 分類ルール:
///   - null / 空 → Block
///   - about:blank (大小区別なし) → AllowIntra (NavigateToString の初回 origin)
///   - https://kxedit.preview/* (大小区別なし) → Block (MD-H-1: 同梱 .html/.svg の same-origin 実行防止)
///   - http/https 非 preview → LaunchExternal (既定ブラウザへ逃がす)
///   - mailto → LaunchExternal
///   - file / ftp / data / javascript / vbscript / その他 → Block
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
    /// search suffix 等に乗って「どの URL を踏ませたか」が外部へ出る)。本ブランチの
    /// 不変条件「この名前を実 DNS に出さない」の唯一の残存経路だった。
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
