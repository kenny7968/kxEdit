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

    [Fact]
    public void Base_href_is_injected() =>
        Assert.Contains(
            "<base href=\"https://kxedit.preview/\">",
            MarkdownRenderer.Render("x", Base)
        );

    [Fact]
    public void Document_declares_utf8_charset() =>
        Assert.Contains("charset=\"utf-8\"", MarkdownRenderer.Render("x", Base));

    [Fact]
    public void Null_markdown_does_not_throw() =>
        Assert.Contains("<html", MarkdownRenderer.Render(null, Base));

    [Fact]
    public void Empty_base_href_omits_base_tag() =>
        Assert.DoesNotContain("<base", MarkdownRenderer.Render("x", ""));

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
        var html = MarkdownRenderer.Render("x", MarkdownRenderer.PreviewBaseHref);
        Assert.Contains("<html", html);
        Assert.Contains("<base href=\"https://kxedit.preview/\">", html);
    }

    [Fact]
    public void PreviewBaseHref_ContainsOnly_HtmlAttrSafeChars()
    {
        // MD-L-4 の allow-list は "baseHref は PreviewBaseHref か空文字のみ" を保証するので
        // 直接 interpolate しても安全。ただし PreviewBaseHref 自体が将来 URL-safe 外の
        // 文字を持つとその前提が崩れる。ここで機械固定して回帰を防ぐ。
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

    [Fact]
    public void Render_EscapesRawScriptTag()
    {
        var html = MarkdownRenderer.Render("<script>alert(1)</script>", "");
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Render_EscapesRawIframeTag()
    {
        var html = MarkdownRenderer.Render("<iframe src=\"evil\"></iframe>", "");
        Assert.DoesNotContain("<iframe", html);
    }

    [Fact]
    public void Render_EscapesInlineEventHandler()
    {
        var html = MarkdownRenderer.Render("<a href=\"x\" onclick=\"evil()\">y</a>", "");
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
    public void Render_RelativeLink_KeepsHref()
    {
        var html = MarkdownRenderer.Render("[x](path/to.md)", Base);
        Assert.Contains("href=\"path/to.md\"", html);
    }

    [Fact]
    public void Render_RootRelativeLink_KeepsHref()
    {
        var html = MarkdownRenderer.Render("[x](/root/path)", Base);
        Assert.Contains("href=\"/root/path\"", html);
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

    // ---------------------------------------------------------------------
    // MD-M-2 + MD-L-1: CSP を HTTP ヘッダで配信 + img-src data: 削除 + CSS 外部化。
    //
    // 変更点:
    //   - meta CSP と HTTP header 用の PreviewCspHeader 定数を single source of truth 化
    //   - img-src から data: を削除 (MD-L-1)
    //   - base-uri/form-action/frame-ancestors/object-src/worker-src/manifest-src/connect-src
    //     を追加 (全て 'none')
    //   - style-src から 'unsafe-inline' を削除し 'self' https://kxedit.preview のみに
    //   - <style>{Css}</style> を <link rel="stylesheet" href="/_kxedit/styles.css"> へ外部化
    //     (実 file は PreviewCspHeaderInjector が virtual response で供給)
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
    public void Meta_Contains_BaseUri_None() =>
        Assert.Contains("base-uri 'none'", MarkdownRenderer.Render("x", Base));

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
    public void Document_LinksToStylesheet_ViaAbsolutePath()
    {
        // MD-M-2: inline <style> を撤去し <link> へ外部化。href は
        // /_kxedit/styles.css 固定 (先頭アンダースコアで .md フォルダ内のユーザ
        // ファイル衝突をほぼゼロに)。
        string html = MarkdownRenderer.Render("x", Base);
        Assert.Contains("<link rel=\"stylesheet\" href=\"/_kxedit/styles.css\">", html);
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
        Assert.Contains("style-src 'self' https://kxedit.preview", csp);
        Assert.Contains("font-src https://kxedit.preview data:", csp);
        Assert.DoesNotContain("'unsafe-inline'", csp);
        Assert.DoesNotContain("img-src https://kxedit.preview data:", csp);
    }

    [Fact]
    public void PreviewStylesheetPath_IsUnderYeditNamespace()
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
}
