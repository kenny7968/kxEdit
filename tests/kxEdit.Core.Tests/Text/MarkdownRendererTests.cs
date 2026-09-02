using System.Text.RegularExpressions;
using kxEdit.Core.Text;

namespace kxEdit.Core.Tests.Text;

public class MarkdownRendererTests
{
    private const string Base = "https://kxedit.preview/";

    [Fact]
    public void Heading_becomes_h1() =>
        Assert.Contains("<h1", MarkdownRenderer.Render("# 見出し", Base));

    [Fact]
    public void Bold_becomes_strong() =>
        Assert.Contains("<strong>太字</strong>", MarkdownRenderer.Render("**太字**", Base));

    [Fact]
    public void Inline_code_becomes_code() =>
        Assert.Contains("<code>x</code>", MarkdownRenderer.Render("`x`", Base));

    [Fact]
    public void Fenced_code_becomes_pre() =>
        Assert.Contains("<pre><code", MarkdownRenderer.Render("```\ncode\n```", Base));

    [Fact]
    public void Pipe_table_becomes_table()
    {
        string md = "| A | B |\n|---|---|\n| 1 | 2 |";
        Assert.Contains("<table", MarkdownRenderer.Render(md, Base));
    }

    [Fact]
    public void Text_special_chars_are_escaped() =>
        Assert.Contains("1 &lt; 2 &amp; 3", MarkdownRenderer.Render("1 < 2 & 3", Base));

    // A-2 (2026-08-22・案 B): <base> は経路を問わず一切出力しない。<base> があると裸の
    // フラグメント URL (#section) まで base 基準で解決され、目次リンクと脚注の戻りリンクが
    // MD-H-1 の Block に巻き込まれて全滅する (設計書 §7.1)。相対 URL は描画前に絶対化する。
    // 「空文字なら省く / 非空なら出す」という案 A 時代の対比はもう存在しないので、
    // 両経路を 1 本の Theory で固定する。
    [Theory]
    [InlineData("")]
    [InlineData(Base)]
    public void Render_EmitsNoBaseTag(string baseHref) =>
        Assert.DoesNotContain("<base", MarkdownRenderer.Render("x", baseHref));

    [Fact]
    public void Document_declares_utf8_charset() =>
        Assert.Contains("charset=\"utf-8\"", MarkdownRenderer.Render("x", Base));

    [Fact]
    public void Null_markdown_does_not_throw() =>
        Assert.Contains("<html", MarkdownRenderer.Render(null, Base));

    // MD-L-4: baseHref は空文字か PreviewBaseHref 定数以外を受け付けない (単一 caller の防御ガード)。
    [Fact]
    public void Render_Throws_ArgumentException_OnUnknownBaseHref()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            MarkdownRenderer.Render("x", "https://evil.com/")
        );
        Assert.Equal("baseHref", ex.ParamName);
    }

    [Fact]
    public void Render_Accepts_EmptyBaseHref()
    {
        var html = MarkdownRenderer.Render("x", "");
        Assert.Contains("<html", html);
        Assert.DoesNotContain("<base", html);
    }

    [Fact]
    public void Render_Accepts_PreviewBaseHref()
    {
        // MD-L-4 の allow-list が PreviewBaseHref を通すこと (例外を投げず文書を返す)。
        // <base> の有無は Render_EmitsNoBaseTag が受け持つ。
        var html = MarkdownRenderer.Render("x", MarkdownRenderer.PreviewBaseHref);
        Assert.Contains("<html", html);
    }

    [Fact]
    public void PreviewBaseHref_ContainsOnly_HtmlAttrSafeChars()
    {
        // A-2 (案 B) で <base> の出力を廃止したので、baseHref が HTML へ interpolate される
        // 経路はもう無い。それでも PreviewBaseHref は PreviewUrlResolver が
        // new Uri(PreviewBase, url) の解決基準として使う URI であり、URL-safe 外の文字が
        // 混ざると解決結果が壊れる (絶対化された URL は最終的に href/src 属性へ載る)。
        // ここで機械固定して回帰を防ぐ。
        Assert.DoesNotContain('"', MarkdownRenderer.PreviewBaseHref);
        Assert.DoesNotContain('<', MarkdownRenderer.PreviewBaseHref);
        Assert.DoesNotContain('>', MarkdownRenderer.PreviewBaseHref);
        Assert.DoesNotContain('&', MarkdownRenderer.PreviewBaseHref);
    }

    [Fact]
    public void Document_includes_csp_blocking_scripts()
    {
        string html = MarkdownRenderer.Render("x", Base);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("default-src 'none'", html);
    }

    // DisableHtml() の網。A-2 (案 B) でパイプラインが preview 用 / 素の用の 2 本に分かれたため、
    // 空 baseHref だけを回すと本番で実際に使われる側 (MainForm は常に PreviewBaseHref を渡す)
    // を検証しないことになる。両経路を回して構成差で穴が開かないことを固定する。
    [Theory]
    [InlineData("")]
    [InlineData(Base)]
    public void Render_EscapesRawScriptTag(string baseHref)
    {
        var html = MarkdownRenderer.Render("<script>alert(1)</script>", baseHref);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Theory]
    [InlineData("")]
    [InlineData(Base)]
    public void Render_EscapesRawIframeTag(string baseHref)
    {
        var html = MarkdownRenderer.Render("<iframe src=\"evil\"></iframe>", baseHref);
        Assert.DoesNotContain("<iframe", html);
    }

    [Theory]
    [InlineData("")]
    [InlineData(Base)]
    public void Render_EscapesInlineEventHandler(string baseHref)
    {
        var html = MarkdownRenderer.Render("<a href=\"x\" onclick=\"evil()\">y</a>", baseHref);
        // <a> tag itself is escaped, so onclick can never reach the DOM as an attribute
        Assert.Contains("&lt;a href=", html);
        Assert.DoesNotContain("<a href=\"x\"", html);
    }

    [Fact]
    public void Render_PreservesMarkdownGeneratedTable()
    {
        var md = "| a | b |\n|---|---|\n| 1 | 2 |";
        var html = MarkdownRenderer.Render(md, "");
        Assert.Contains("<table", html);
        Assert.Matches(@"<td[^>]*>\s*1\s*</td>", html);
    }

    [Fact]
    public void Render_PreservesCodeBlock()
    {
        var md = "```csharp\nvar x = 1;\n```";
        var html = MarkdownRenderer.Render(md, "");
        Assert.Contains("<code", html);
        Assert.Contains("var x = 1;", html);
    }

    // ---------------------------------------------------------------------
    // MD-M-3: リンク URL スキーム whitelist (二層目の防御)
    //
    // CSP `default-src 'none'` により javascript: URI の実行は現状阻止できているが、
    // MD-M-2 で CSP を弱める瞬間に live XSS 化するため、renderer 段でも href の
    // scheme を http/https/mailto/相対/fragment に限定し、それ以外は href 属性を
    // まるごと drop する。表示テキスト (<a>...</a>) は残す。
    // ---------------------------------------------------------------------

    [Fact]
    public void Render_JavascriptScheme_DropsHrefAttribute()
    {
        var html = MarkdownRenderer.Render("[click](javascript:alert(1))", Base);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.Contains("<a", html); // opening <a> は残す (Write の open タグ削除変異を kill)
        Assert.Contains(">click</a>", html);
    }

    [Fact]
    public void Render_VbscriptScheme_DropsHrefAttribute()
    {
        var html = MarkdownRenderer.Render("[x](vbscript:foo)", Base);
        Assert.DoesNotContain("href=\"vbscript:", html);
        Assert.Contains("<a", html);
        Assert.Contains(">x</a>", html);
    }

    [Fact]
    public void Render_DataScheme_DropsHrefAttribute()
    {
        var html = MarkdownRenderer.Render("[x](data:text/html,<script>)", Base);
        Assert.DoesNotContain("href=\"data:", html);
        Assert.Contains("<a", html);
        Assert.Contains(">x</a>", html);
    }

    [Fact]
    public void Render_FileScheme_DropsHrefAttribute()
    {
        // MD-M-5 補完: file:// URL は本タスクでも遮断。
        var html = MarkdownRenderer.Render("[x](file://server/share)", Base);
        Assert.DoesNotContain("href=\"file:", html);
        Assert.Contains("<a", html);
        Assert.Contains(">x</a>", html);
    }

    [Fact]
    public void Render_HttpUrl_KeepsHref()
    {
        var html = MarkdownRenderer.Render("[x](http://example.com/)", Base);
        Assert.Contains("href=\"http://example.com/\"", html);
    }

    [Fact]
    public void Render_HttpsUrl_KeepsHref()
    {
        var html = MarkdownRenderer.Render("[x](https://example.com/)", Base);
        Assert.Contains("href=\"https://example.com/\"", html);
    }

    [Fact]
    public void Render_MailtoUrl_KeepsHref()
    {
        var html = MarkdownRenderer.Render("[x](mailto:a@b)", Base);
        Assert.Contains("href=\"mailto:a@b\"", html);
    }

    [Fact]
    public void Render_RelativeLink_IsResolvedButNotDropped()
    {
        // whitelist は相対 URL を drop しない。ただし preview 経路では A-2 (案 B) の絶対化が
        // 先に効くため href は preview 仮想ホスト基準になる。よって「相対のまま保つ」ことは
        // もう検証していない (その網は Render_EmptyBaseHref_DoesNotRewriteRelativeUrls)。
        var html = MarkdownRenderer.Render("[x](path/to.md)", Base);
        Assert.Contains("href=\"https://kxedit.preview/path/to.md\"", html);
    }

    [Fact]
    public void Render_RootRelativeLink_IsResolvedButNotDropped()
    {
        var html = MarkdownRenderer.Render("[x](/root/path)", Base);
        Assert.Contains("href=\"https://kxedit.preview/root/path\"", html);
    }

    [Fact]
    public void Render_FragmentOnly_KeepsHref()
    {
        var html = MarkdownRenderer.Render("[x](#section)", Base);
        Assert.Contains("href=\"#section\"", html);
    }

    [Fact]
    public void Render_CaseInsensitiveScheme_JavascriptUppercase_DropsHref()
    {
        var html = MarkdownRenderer.Render("[x](JAVASCRIPT:foo)", Base);
        Assert.DoesNotContain("href=\"JAVASCRIPT:", html);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.Contains("<a", html);
        Assert.Contains(">x</a>", html);
    }

    [Fact]
    public void Render_ImageSrc_NotFiltered_ThisTaskScopedOnly()
    {
        // image の src filter は本タスク scope 外 (CSP img-src で別途遮断済み)。
        // 通常の http(s) 画像は素通しされることを機械固定して scope 境界を明示する。
        var html = MarkdownRenderer.Render("![alt](https://kxedit.preview/img.png)", Base);
        Assert.Contains("<img", html);
        Assert.Contains("src=\"https://kxedit.preview/img.png\"", html);
    }

    [Fact]
    public void Render_AngleBracketAutolinkJavascript_DropsHref()
    {
        // CommonMark autolink `<javascript:alert(1)>` は AutolinkInline 経路を通り、
        // LinkInlineRenderer とは別の AutolinkInlineRenderer で処理される。
        // 同じ scheme whitelist を適用して防御の穴を塞ぐ。
        var html = MarkdownRenderer.Render("<javascript:alert(1)>", Base);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.Contains("<a", html); // opening <a> は残す (Write の open タグ削除変異を kill)
        Assert.Contains("</a>", html);
    }

    // ---------------------------------------------------------------------
    // MD-L-3: レンダー入力サイズ上限 (既定 4,000,000 文字 = 8 MB UTF-16 相当)。
    //
    // ネスト深度 / テーブルサイズの pre-scan は入れない (設計書: 入力サイズ
    // 4 MB で実質封じられる・保守負担を優先)。入口一箇所の cap のみで DoS を
    // 抑える。境界値 (ちょうど 4M 文字は許容 / +1 で throw) と const 値を
    // 機械固定する。
    // ---------------------------------------------------------------------

    [Fact]
    public void MaxMarkdownChars_IsFourMillion()
    {
        // const を書き換える PR は必ずこのテストを更新する = レビュー強制。
        Assert.Equal(4_000_000, MarkdownRenderer.MaxMarkdownChars);
    }

    [Fact]
    public void Render_Throws_DocumentTooLarge_WhenExceedingCap()
    {
        var md = new string('a', MarkdownRenderer.MaxMarkdownChars + 1);
        Assert.Throws<DocumentTooLargeException>(() => MarkdownRenderer.Render(md, ""));
    }

    [Fact]
    public void Render_Accepts_MaxSizedInput()
    {
        // 境界: ちょうど上限は素通し。cap を off-by-one で厳しくする回帰を防ぐ。
        var md = new string('a', MarkdownRenderer.MaxMarkdownChars);
        var html = MarkdownRenderer.Render(md, "");
        Assert.Contains("<html", html);
    }

    [Fact]
    public void Render_DocumentTooLargeException_ReportsAttemptedBytes()
    {
        // AttemptedBytes は UTF-16 バイト換算 (Length * 2)。TextBufferBuilder の
        // 「実格納バイト数」とは意味が違うが、DocumentTooLargeException の契約に
        // 揃える (テストが期待値を機械固定する)。
        var md = new string('a', MarkdownRenderer.MaxMarkdownChars + 1);
        var ex = Assert.Throws<DocumentTooLargeException>(() => MarkdownRenderer.Render(md, ""));
        Assert.Equal((long)(MarkdownRenderer.MaxMarkdownChars + 1) * 2L, ex.AttemptedBytes);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(MarkdownRenderer.MaxMarkdownChars - 1, false)]
    [InlineData(MarkdownRenderer.MaxMarkdownChars, false)] // 境界ちょうどは通す
    [InlineData(MarkdownRenderer.MaxMarkdownChars + 1, true)]
    public void ExceedsMaxChars_UsesSameBoundaryAsRenderCap(int charCount, bool expected)
    {
        // M-23: caller が全文 string 化の前に判定するための述語。Render 内の cap と
        // 不等号がずれると「事前判定は通ったのに Render が投げる」二重基準になる。
        Assert.Equal(expected, MarkdownRenderer.ExceedsMaxChars(charCount));
    }

    [Fact]
    public void Render_TooLargeMessage_ComesFromTooLargeDetail()
    {
        // M-23: 事前判定した caller のダイアログと Render の例外で文面を一致させる。
        var md = new string('a', MarkdownRenderer.MaxMarkdownChars + 1);
        var ex = Assert.Throws<DocumentTooLargeException>(() => MarkdownRenderer.Render(md, ""));
        Assert.Equal(
            MarkdownRenderer.TooLargeDetail(MarkdownRenderer.MaxMarkdownChars + 1),
            ex.Message
        );
    }

    // ---------------------------------------------------------------------
    // MD-M-2 + MD-L-1: CSP を HTTP ヘッダで配信 + img-src data: 削除 + CSS 外部化。
    //
    // 変更点:
    //   - meta CSP と HTTP header 用の PreviewCspHeader 定数を single source of truth 化
    //   - img-src から data: を削除 (MD-L-1)
    //   - base-uri/form-action/frame-ancestors/object-src/worker-src/manifest-src/connect-src
    //     を追加 (全て 'none')
    //   - A-2 (2026-08-22・案 B): base-uri は 'none' のまま。文書に <base> を出力せず、
    //     相対 URL は PreviewRelativeUrlExtension が描画前に絶対化する (設計書 §7)
    //   - style-src から 'unsafe-inline' を削除し 'self' https://kxedit.preview のみに
    //     (V-4・2026-09-03: 'self' も削除した。data: 文書の origin は opaque で
    //      'self' は何にもマッチせず、防御として機能していなかったため)
    //   - <style>{Css}</style> を <link rel="stylesheet"> へ外部化 (href は A-2 案 B で
    //     絶対 URL https://kxedit.preview/_kxedit/styles.css へ変更・<base> 非依存にするため。
    //     実 file は PreviewCspHeaderInjector が virtual response で供給)
    // ---------------------------------------------------------------------

    [Fact]
    public void Meta_ImgSrc_Excludes_Data_Scheme()
    {
        // MD-L-1: img-src ディレクティブは存在するが data: は付かない。
        // M-6 補正: 単純な "img-src https://kxedit.preview data:" の substring 判定は
        // "img-src https://kxedit.preview 'self' data:" のような insertion mutation を
        // 検出できないため、img-src ディレクティブ全体を切り出して data: が入らない
        // ことを regex で機械固定する。
        string html = MarkdownRenderer.Render("x", Base);
        Assert.Contains("img-src https://kxedit.preview", html);
        Assert.Matches(@"img-src\s+[^;]*?;", html);
        Assert.DoesNotMatch(@"img-src\s+[^;]*\bdata:", html);
    }

    [Fact]
    public void Meta_Contains_BaseUri_None()
    {
        // A-2 (2026-08-22・案 B): 文書に <base> を出力しないので base-uri は最も強い 'none' を
        // 維持できる。directive 全体を切り出して 'none' ちょうどであることを機械固定する
        // (Meta_ImgSrc_Excludes_Data_Scheme と同じ insertion mutation 耐性)。
        string html = MarkdownRenderer.Render("x", Base);
        var m = Regex.Match(html, @"base-uri\s+([^;]*);");
        Assert.True(m.Success, "base-uri directive が見つからない");
        Assert.Equal("'none'", m.Groups[1].Value.Trim());
    }

    [Fact]
    public void Meta_Contains_FormAction_None() =>
        Assert.Contains("form-action 'none'", MarkdownRenderer.Render("x", Base));

    [Fact]
    public void Meta_Contains_FrameAncestors_None() =>
        Assert.Contains("frame-ancestors 'none'", MarkdownRenderer.Render("x", Base));

    [Fact]
    public void Meta_Contains_ObjectSrc_None() =>
        Assert.Contains("object-src 'none'", MarkdownRenderer.Render("x", Base));

    [Fact]
    public void Meta_Contains_WorkerSrc_None() =>
        Assert.Contains("worker-src 'none'", MarkdownRenderer.Render("x", Base));

    [Fact]
    public void Meta_Contains_ManifestSrc_None() =>
        Assert.Contains("manifest-src 'none'", MarkdownRenderer.Render("x", Base));

    [Fact]
    public void Meta_Contains_ConnectSrc_None() =>
        Assert.Contains("connect-src 'none'", MarkdownRenderer.Render("x", Base));

    [Fact]
    public void Meta_StyleSrc_ExcludesUnsafeInline()
    {
        // MD-M-2: 外部化により 'unsafe-inline' は不要になる。誰かが緩和で戻すことを検知。
        string html = MarkdownRenderer.Render("x", Base);
        Assert.DoesNotContain("'unsafe-inline'", html);
    }

    [Fact]
    public void Document_StylesheetLink_IsAbsolutePreviewUrl()
    {
        // MD-M-2: inline <style> を撤去し <link> へ外部化。パスは /_kxedit/styles.css 固定
        // (先頭アンダースコアで .md フォルダ内のユーザファイル衝突をほぼゼロに)。
        // A-2 (案 B): href は絶対 URL。<base> を出力しなくなったので、相対のままでは
        // data: origin (NavigateToString) から解決できず CSS が永久に効かない。
        // 空 baseHref 経路でも同じ絶対 URL であること (Minor-4 の解消) まで固定する。
        Assert.Equal(
            "https://kxedit.preview/_kxedit/styles.css",
            MarkdownRenderer.PreviewStylesheetUrl
        );
        string expected =
            "<link rel=\"stylesheet\" href=\"https://kxedit.preview/_kxedit/styles.css\">";
        Assert.Contains(expected, MarkdownRenderer.Render("x", Base));
        Assert.Contains(expected, MarkdownRenderer.Render("x", ""));
    }

    [Fact]
    public void Document_NoInlineStyleTag()
    {
        // MD-M-2: CSS 外部化により <style> タグは HTML 内に含まれない。
        string html = MarkdownRenderer.Render("x", Base);
        Assert.DoesNotContain("<style>", html);
        Assert.DoesNotContain("<style ", html);
    }

    [Fact]
    public void PreviewCspHeader_ContainsAllDirectives()
    {
        // HTTP header 側と meta 側で同一の CSP 文字列を使う single source of truth。
        // 各 directive の存在 + 不要な緩和が入っていないことを機械固定。
        string csp = MarkdownRenderer.PreviewCspHeader;
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("base-uri 'none'", csp);
        Assert.Contains("form-action 'none'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("worker-src 'none'", csp);
        Assert.Contains("manifest-src 'none'", csp);
        Assert.Contains("connect-src 'none'", csp);
        Assert.Contains("img-src https://kxedit.preview", csp);
        Assert.Contains("media-src https://kxedit.preview", csp);
        // V-4: data: 文書の origin は opaque なので 'self' は何にもマッチしない。
        // <link> を実際に通しているのは https://kxedit.preview の方なので 'self' は置かない。
        Assert.Contains("style-src https://kxedit.preview", csp);
        Assert.DoesNotContain("'self'", csp);
        Assert.Contains("font-src https://kxedit.preview data:", csp);
        Assert.DoesNotContain("'unsafe-inline'", csp);
        Assert.DoesNotContain("img-src https://kxedit.preview data:", csp);
    }

    [Fact]
    public void StyleSrc_Covers_StylesheetLinkOrigin_WithoutSelf()
    {
        // V-4: 'self' を外しても <link rel="stylesheet"> が通ることを、href の origin と
        // style-src ディレクティブの突き合わせで固定する。data: 文書の origin は opaque なので
        // 'self' は何にもマッチせず、<link> を実際に通しているのは https://kxedit.preview の側。
        // directive 全体を切り出すので "style-src https://kxedit.preview 'self'" のような
        // insertion mutation も落ちる。
        var m = Regex.Match(MarkdownRenderer.PreviewCspHeader, @"style-src\s+([^;]*)(;|$)");
        Assert.True(m.Success, "style-src directive が見つからない");
        string directive = m.Groups[1].Value.Trim();
        Assert.Equal("https://" + MarkdownRenderer.PreviewVirtualHost, directive);
        // 実際に読み込む CSS の URL がこの source の配下にある = 防御は落ちていない。
        Assert.StartsWith(directive + "/", MarkdownRenderer.PreviewStylesheetUrl);
    }

    [Fact]
    public void PreviewStylesheetPath_IsUnderKxeditNamespace()
    {
        // /_kxedit/styles.css は Injector の URL 判定と HTML link href の両方で参照される
        // single source of truth。名前空間 (先頭 _) を機械固定して衝突リスク回帰を防ぐ。
        Assert.Equal("/_kxedit/styles.css", MarkdownRenderer.PreviewStylesheetPath);
    }

    [Fact]
    public void PreviewStylesheet_ContainsCoreRules()
    {
        // CSS 外部化前後の見た目一致を担保するため代表 rule の存在を機械固定する。
        // css 側の書き換え時にこのテストが落ちる = 目視確認のトリガ。
        string css = MarkdownRenderer.PreviewStylesheet;
        Assert.Contains("body", css);
        Assert.Contains("font-family", css);
    }

    [Fact]
    public void Meta_And_HttpHeader_Use_SameCspString()
    {
        // meta http-equiv 側と HTTP header 側で CSP 文字列が食い違うと防御差が生まれる。
        // 同一定数を参照している契約を機械固定する。
        string html = MarkdownRenderer.Render("x", Base);
        Assert.Contains(MarkdownRenderer.PreviewCspHeader, html);
    }

    // ---------------------------------------------------------------------
    // A-21 (v0.2 リリース前バグ監査): UseAdvancedExtensions 同梱の GenericAttributes を
    // 除去し、`{...}` 属性記法が HTML 属性として出力されないことを機械固定する。
    // 実行を止めていたのは CSP (script-src なし) だけで、SafeLinkExtension (二層目の
    // scheme whitelist) に対して GenericAttributes は 2 つの別経路で作用していた:
    //   追記 — 安全な href を持つリンクに 2 つ目の href を足す。
    //          `[y](x){href="javascript:..."}` → <a href="x" href="javascript:...">
    //          HTML の先勝ち規則で実挙動は守られていたが、属性が出ること自体が
    //          パーサ差で容易に逆転しうる不安定な均衡。
    //   復活 — SafeLink が drop した href を単一の href として蘇らせる。
    //          `[y](javascript:...){href="javascript:..."}` → <a href="javascript:...">
    //          先勝ち規則が効かない本物のバイパスで、CSP を弱めた瞬間に live XSS。
    // ---------------------------------------------------------------------

    [Fact]
    public void Render_GenericAttributes_DoesNotEmit_OnClickOnLink()
    {
        string html = MarkdownRenderer.Render("[y](x){onclick=\"evil()\"}", Base);
        Assert.DoesNotMatch(@"<a[^>]*onclick", html);
    }

    [Fact]
    public void Render_GenericAttributes_DoesNotEmit_OnErrorOnImage()
    {
        string html = MarkdownRenderer.Render("![a](x){onerror=alert(1)}", Base);
        Assert.DoesNotMatch(@"<img[^>]*onerror", html);
    }

    [Fact]
    public void Render_GenericAttributes_CannotAppend_JavascriptHref()
    {
        // 安全な href を持つリンクに 2 つ目の href を追記できないこと。
        // 変更前は <a href="x" href="javascript:alert(1)"> となっていた。HTML の先勝ち規則で
        // 実挙動は守られていたが、属性が出力されること自体が不安定な均衡なので塞ぐ。
        // (drop された href の復活は Render_GenericAttributes_CannotRestore_DroppedJavascriptHref。)
        string html = MarkdownRenderer.Render("[y](x){href=\"javascript:alert(1)\"}", Base);
        Assert.DoesNotMatch(@"<a[^>]*javascript:", html);
    }

    [Fact]
    public void Render_GenericAttributes_CannotRestore_DroppedJavascriptHref()
    {
        // 本物のバイパス経路: SafeLinkExtension が javascript: scheme の href を drop した後、
        // GenericAttributes が {href="javascript:..."} で単一の href として復活させていた。
        // (`[y](x){href=...}` と違い href の重複にならないため、HTML の先勝ち規則では守られない。)
        string html = MarkdownRenderer.Render(
            "[y](javascript:alert(1)){href=\"javascript:alert(1)\"}",
            Base
        );
        Assert.DoesNotMatch(@"<a[^>]*javascript:", html);
    }

    [Fact]
    public void Render_GenericAttributes_Syntax_BecomesLiteralText()
    {
        // 拡張除去に伴う挙動変化を仕様として固定する ({#id} は本文にそのまま出る)。
        // 見出しの id は UseAutoIdentifiers が引き続き生成するが、{#id} で指定していた
        // カスタム id は自動生成値へ変わる (`# Title {#custom}` は id="custom" →
        // id="title-custom")。よって既存 .md 内の [link](#custom) は切れる。
        // この fixture が変更前後とも id="custom" で一致するのは、slug 生成が非 ASCII
        // (見出し) と {#} を捨てた結果の偶然にすぎない。ASCII 見出しへ書き換えると id は
        // 変わるので、本テストを「id が不変である」根拠には使わないこと。
        string html = MarkdownRenderer.Render("# 見出し {#custom}", Base);
        Assert.Contains("{#custom}", html);
    }

    [Fact]
    public void Render_AbbreviationLabel_DoesNotEmit_RawHtml()
    {
        // FINDING 1: Markdig の HtmlAbbreviationRenderer はラベルを WriteEscape せず出力するため、
        // DisableHtml() が全面バイパスされていた (title 側は正しくエスケープされる)。
        string md = "*[<script>fetch(1)</script>]: x\n\n<script>fetch(1)</script>\n";
        string html = MarkdownRenderer.Render(md, Base);
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public void Render_AbbreviationLabel_DoesNotEmit_MetaRefresh()
    {
        // 最も実害のある注入。CSP に該当 directive が無いため、プレビューを開くだけで
        // MarkdownPreviewForm の LaunchExternal 経路が発火し既定ブラウザが開く。
        string md =
            "*[<meta http-equiv=refresh content=0;url=https://evil.example/pwn>]: x\n\n"
            + "<meta http-equiv=refresh content=0;url=https://evil.example/pwn>\n";
        string html = MarkdownRenderer.Render(md, Base);
        Assert.DoesNotContain("<meta http-equiv=refresh", html);
    }

    [Fact]
    public void Render_AbbreviationDefinition_BecomesLiteralText()
    {
        // 拡張除去 (FINDING 1) に伴う挙動変化を仕様として固定する。
        // Render_GenericAttributes_Syntax_BecomesLiteralText (A-21) と対称。
        // 定義行はそのまま本文に出て、本文中の略語も <abbr> へ展開されない。
        string md = "*[HTML]: HyperText Markup Language\n\nHTML は仕様である。\n";
        string html = MarkdownRenderer.Render(md, Base);
        Assert.Contains("*[HTML]: HyperText Markup Language", html);
        Assert.DoesNotContain("<abbr", html);
    }

    // ---------------------------------------------------------------------
    // A-2 (2026-08-22・案 B): <base> を出力せず、相対 URL を描画前 (DocumentProcessed) に
    // preview 仮想ホスト基準へ絶対化する。
    //
    // <base> を復活させる案 A は、裸のフラグメント URL (#section) まで base 基準で解決させて
    // しまい、文書自身の URL が data:text/html;... のままなので同一文書内スクロールではなく
    // クロス文書遷移になる。PreviewNavigationPolicy.Classify は https + preview ホストを
    // MD-H-1 で Block するため、目次リンクと脚注の戻りリンクが全て無反応になる (FINDING 3)。
    //
    // FINDING 3 の回帰防止網は次の 2 本 (ミューテーションで kill 確認済み):
    //   - Render_FragmentLink_IsNotRewritten ... resolver が # 始まりを書き換えないこと
    //     (PreviewUrlResolver の # ガードを消すと赤)
    //   - Render_EmitsNoBaseTag ... 文書に <base> を出力しないこと
    //     (<base> を再注入すると赤)
    // Render_FootnoteLinks_AreNotRewritten はこの網には入らない (同テストのコメント参照)。
    // ---------------------------------------------------------------------

    [Fact]
    public void Render_RelativeImage_IsResolvedToPreviewHost()
    {
        string html = MarkdownRenderer.Render("![](pic.png)", Base);
        Assert.Contains("src=\"https://kxedit.preview/pic.png\"", html);
    }

    [Fact]
    public void Render_RelativeLink_IsResolvedToPreviewHost()
    {
        string html = MarkdownRenderer.Render("[y](other.md)", Base);
        Assert.Contains("href=\"https://kxedit.preview/other.md\"", html);
    }

    [Fact]
    public void Render_FragmentLink_IsNotRewritten()
    {
        // FINDING 3 の回帰防止 (最重要)。目次リンクは同一文書内スクロールのまま保つ。
        string html = MarkdownRenderer.Render("# 見出し\n\n[目次](#midashi)\n", Base);
        Assert.Contains("href=\"#midashi\"", html);
        Assert.DoesNotContain("href=\"https://kxedit.preview/#", html);
    }

    [Fact]
    public void Render_FootnoteLinks_AreNotRewritten()
    {
        // FINDING 3 の網ではない。脚注リンクは LinkInline ではなく FootnoteLink で、href は
        // HtmlFootnoteLinkRenderer が直書きするため PreviewRelativeUrlExtension が届かない。
        // よって resolver の # ガードを消しても <base> を再注入しても赤くならない (実測済み)。
        // 本テストの位置づけは脚注リンクの出力形式 (#fn:1 / #fnref:1) の仕様固定で、
        // RemoveAll の述語に FootnoteExtension を足す変異を唯一 kill する網でもある。
        string html = MarkdownRenderer.Render("text[^1]\n\n[^1]: note\n", Base);
        Assert.Contains("href=\"#fn:1\"", html);
        Assert.Contains("href=\"#fnref:1\"", html);
        Assert.DoesNotContain("href=\"https://kxedit.preview/#", html);
    }

    [Fact]
    public void Render_AbsoluteLink_IsNotRewritten()
    {
        string html = MarkdownRenderer.Render("[y](https://example.com/)", Base);
        Assert.Contains("href=\"https://example.com/\"", html);
        Assert.DoesNotContain("kxedit.preview/https", html);
    }

    [Fact]
    public void Render_JavascriptScheme_StillDropsHref_UnderPreviewPipeline()
    {
        // 相対 URL 書き換えが SafeLinkExtension の scheme whitelist を壊していないことの証拠。
        // PreviewUrlResolver は scheme 付き URL を触らないので判定結果は不変。
        string html = MarkdownRenderer.Render("[y](javascript:alert(1))", Base);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.DoesNotContain("kxedit.preview/javascript:", html);
        Assert.Contains("<a", html);
        Assert.Contains(">y</a>", html);
    }

    [Fact]
    public void Render_EmptyBaseHref_DoesNotRewriteRelativeUrls()
    {
        // 空 baseHref の経路は解決基準を持たないので書き換えない (パイプライン 2 本の境界)。
        // 案 B 以降、SafeLinkExtension の whitelist が「scheme 無し相対 URL」を drop しない
        // ことを固定する唯一の網でもある (preview 経路では絶対化が先に効いて href が https に
        // なるため、Render_RelativeLink_IsResolvedButNotDropped 系は相対のケースを
        // 検証できなくなった)。
        string html = MarkdownRenderer.Render("![](pic.png)\n\n[y](other.md)\n", "");
        Assert.Contains("src=\"pic.png\"", html);
        Assert.Contains("href=\"other.md\"", html);
        Assert.DoesNotContain("https://kxedit.preview/pic.png", html);
        Assert.DoesNotContain("https://kxedit.preview/other.md", html);
    }

    [Theory]
    // V-3 (監査 §9): 区切り文字を密輸する形。相対・絶対の両方を潰す。
    // 絶対形は TryResolve が触らない経路なので、ガードを resolver の絶対化側に置くと素通りする
    // (設計書 §14.1 の実測)。ここは Render の出力で固定する。
    [InlineData("![x](..%2f..%2fsecret.txt)")]
    [InlineData("![x](https://kxedit.preview/..%2f..%2fsecret.txt)")]
    [InlineData("![x](https://kxedit.preview/..%2F..%2FEBWebView/Default/Preferences)")]
    [InlineData("[a](https://kxedit.preview/..%5c..%5cx)")]
    [InlineData("[a](..%5C..%5Cx)")]
    // --- 脆弱性レビュー (2026-09-03) で実測された迂回形 ---
    // F-1: 非 ASCII ホスト。Uri.Host は Unicode を保つので Host 比較では一致しないが、
    // Markdig の WriteEscapeUrl は IdnHost で ASCII 化して出力するため、最終的な HTML は
    // 本物の preview ホスト宛になる。ホスト判定を IdnHost にしないとここだけ素通りする。
    [InlineData("![x](https://kxedit。preview/..%2f..%2fsecret.txt)")] // U+3002 全角句点
    [InlineData("![x](https://ｋxedit.preview/..%2f..%2fsecret.txt)")] // U+FF4B 全角 k
    // F-2: percent-encode されたホスト。.NET は Uri.TryCreate(Absolute) 自体に失敗するが、
    // WHATWG (Chromium) のホスト解析は percent-decode → domain-to-ASCII なので
    // kxedit.preview に解決される。parse 不能を「そのまま返す」に倒すと素通りする。
    [InlineData("![x](https://%6bxedit.preview/..%2f..%2fsecret.txt)")]
    [InlineData("![x](https://kxedit%2epreview/..%2f..%2fsecret.txt)")]
    // F-3: 末尾ドット。Uri.Host も Uri.IdnHost も末尾ドットを保持するので明示的に削る必要がある。
    [InlineData("![x](https://kxedit.preview./..%2f..%2fsecret.txt)")]
    // F-4: 生のバックスラッシュ。Uri.AbsolutePath 上では '\' が '/' へ正規化されて
    // dot-segment が畳まれるため AST 段では「区切りは無い」と見えるのに、そのあと
    // WriteEscapeUrl が %5C を作って出力する。ガードを LinkRewriter 段へ置き、かつ
    // 生の '\' を対象に含めて初めて塞がる。
    //
    // 注意 (二重エスケープ): ここは C# 文字列と CommonMark の両方でエスケープが効く。
    // C# の "\\\\" は markdown ソース上の `\\` = CommonMark のバックスラッシュエスケープで
    // 1 個の生 '\' になる。C# の "\\" は markdown 上の `\` で、後続が '.' (ASCII 句読点) だと
    // CommonMark が食べてしまい URL は `....\secret.txt` になる。どちらも生 '\' が
    // LinkRewriter に届くので両方を網に入れる (実測)。
    [InlineData("![x](https://kxedit.preview/..\\\\..\\\\secret.txt)")] // URL は ..\..\secret.txt
    [InlineData("![x](https://kxedit.preview/..\\..\\secret.txt)")] // URL は ....\secret.txt
    // F-5: AutolinkInline は LinkInline ではないので Descendants<LinkInline>() に掛からない。
    // LinkRewriter は autolink 経路でも発火する (実測)。
    [InlineData("<https://kxedit.preview/..%2f..%2fa>")] // CommonMark autolink
    [InlineData("[a](<https://kxedit.preview/..%2f..%2fa>)")] // 角括弧宛先 (これは LinkInline)
    public void Preview_EncodedSeparators_NeverReachOutput(string markdown)
    {
        string html = MarkdownRenderer.Render(markdown, Base);
        string[] urls = BodyUrlAttributes(html);
        // 網が空振りしていないこと (URL 属性が 1 つも無ければ以下の Assert.All は空虚)。
        Assert.NotEmpty(urls);
        Assert.All(
            urls,
            url =>
            {
                Assert.DoesNotContain("%2f", url, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("%5c", url, StringComparison.OrdinalIgnoreCase);
                // 生の '\' も残さない (残ると WebView2 側が区切りとして解釈しうる)。
                Assert.DoesNotContain("\\", url, StringComparison.Ordinal);
                // 無害化の形も固定する (URL を空にはしない = <img src=""> の解決はブラウザ依存。
                // LinkRewriter が null を返すと実際に src="" になる・実測)。
                Assert.Contains("%25", url, StringComparison.Ordinal);
            }
        );
        // 検証対象は URL 属性のみ。autolink はリンクテキストにも生 URL を出すため
        // (<a href="…%252f…">https://kxedit.preview/..%2f..%2fa</a>)、HTML 全文に対する
        // DoesNotContain("%2f") は成立しない。テキストは要求を発生させないので対象外
        // ——「どこを見て安全と言っているか」を取り違えないためにここへ明記する。
    }

    /// <summary>
    /// F-9: V-3 の無害化が preview 経路<b>専用</b>であることの対照。
    /// <para>
    /// <c>LinkRewriter</c> を設定するのは <c>PreviewRelativeUrlExtension</c> で、
    /// その拡張は preview パイプラインにしか入らない。よって空 baseHref 経路では
    /// 区切りエスケープはそのまま出力される。これは穴ではなく境界:
    /// 空 baseHref 経路の出力を WebView2 へ渡す caller が存在せず
    /// (Render の唯一の caller = MainForm.ShowMarkdownPreview は常に PreviewBaseHref を渡す)、
    /// 仮想ホストマッピングによるフォルダー解決自体が起きないため。
    /// 「ここでは無害化されない」ことを明示的に固定して、将来 caller が増えたときに
    /// この前提が黙って崩れないようにする。
    /// </para>
    /// </summary>
    [Fact]
    public void EmptyBaseHref_EncodedSeparators_AreNotNeutralized()
    {
        string html = MarkdownRenderer.Render(
            "![x](https://kxedit.preview/..%2f..%2fsecret.txt)",
            ""
        );
        Assert.Contains(
            "src=\"https://kxedit.preview/..%2f..%2fsecret.txt\"",
            html,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("%252f", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>&lt;body&gt;</c> 内の <c>href</c> / <c>src</c> 属性値をすべて取り出す。
    /// head の stylesheet <c>&lt;link&gt;</c> は本文由来ではないので範囲外に置く。
    /// </summary>
    private static string[] BodyUrlAttributes(string html)
    {
        int start = html.IndexOf("<body>", StringComparison.Ordinal);
        int end = html.IndexOf("</body>", StringComparison.Ordinal);
        Assert.InRange(start, 0, int.MaxValue);
        Assert.InRange(end, start, int.MaxValue);
        string body = html[(start + "<body>".Length)..end];
        return Regex
            .Matches(body, "(?:href|src)=\"([^\"]*)\"", RegexOptions.CultureInvariant)
            .Select(m => m.Groups[1].Value)
            .ToArray();
    }

    [Theory]
    // 非退化の対照: 他の percent-escape と通常の相対パスは従来どおり絶対化される。
    [InlineData("![x](my%20file.png)", "https://kxedit.preview/my%20file.png")]
    [InlineData("![x](sub/dir/pic.png)", "https://kxedit.preview/sub/dir/pic.png")]
    // 外部 URL は我々のマッピングではないので触らない。
    [InlineData("[a](https://example.com/a%2fb)", "https://example.com/a%2fb")]
    public void Preview_OtherUrls_AreUntouched(string markdown, string expectedUrl)
    {
        Assert.Contains(
            expectedUrl,
            MarkdownRenderer.Render(markdown, Base),
            StringComparison.Ordinal
        );
    }
}
