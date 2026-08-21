using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax.Inlines;

namespace kxEdit.Core.Text;

/// <summary>
/// Markdig パイプラインへ「リンク URL スキーム whitelist」を差し込む拡張。
/// CSP `default-src 'none'` を弱めた瞬間の live XSS を防ぐ二層目の防御として、
/// LinkInlineRenderer / AutolinkInlineRenderer を差し替え、
/// `href` の scheme を {http, https, mailto, /, #, 空, 相対} に限定する。
/// 許可外 (`javascript:` / `vbscript:` / `data:` / `file:` 等) は href 属性ごと drop し、
/// リンクタグと表示テキストのみ残す (audit doc MD-M-3)。
/// 画像 (`![]()`) の src は本タスク scope 外 (CSP `img-src` で別途遮断)。
/// </summary>
internal sealed class SafeLinkExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline) { }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is not HtmlRenderer html)
        {
            return;
        }
        // Replace は「該当型が見つかって差し替えた」= true / 「見つからず no-op」= false を返す。
        // security-critical な差し替えが silent に消えないよう、false は fail fast させる
        // (将来 Markdig の default renderer 構成が変わり別 assembly/extension に移った場合の検知)。
        if (!html.ObjectRenderers.Replace<LinkInlineRenderer>(new SafeLinkInlineRenderer()))
        {
            throw new InvalidOperationException(
                "Markdig LinkInlineRenderer not found - SafeLinkExtension setup failed. "
                    + "Verify Markdig version compatibility."
            );
        }
        // CommonMark autolink `<javascript:...>` は AutolinkInline 経路 (別 renderer) を通るため、
        // LinkInlineRenderer だけの差し替えでは防御に穴が残る。同 whitelist を適用する。
        if (!html.ObjectRenderers.Replace<AutolinkInlineRenderer>(new SafeAutolinkInlineRenderer()))
        {
            throw new InvalidOperationException(
                "Markdig AutolinkInlineRenderer not found - SafeLinkExtension setup failed. "
                    + "Verify Markdig version compatibility."
            );
        }
    }

    /// <summary>
    /// href として安全に使える URL かを判定する。null / 空 / fragment / root-relative /
    /// scheme 無し相対 は許可。scheme あり URL は http/https/mailto のみ許可。
    /// </summary>
    internal static bool IsAllowedLinkUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return true;
        }
        if (url[0] == '#' || url[0] == '/')
        {
            return true; // fragment / root-relative
        }
        int colon = url.IndexOf(':');
        if (colon < 0)
        {
            return true; // scheme 無し (path/to/file.md 等)
        }
        string scheme = url[..colon].ToLowerInvariant();
        return scheme is "http" or "https" or "mailto";
    }
}

/// <summary>
/// LinkInline (`[text](url)` および GFM bare-URL autolink) 用の安全化 renderer。
/// 許可外 scheme の場合は href を drop して `&lt;a&gt;...&lt;/a&gt;` の枠と表示テキストのみ残す。
/// 画像 (`link.IsImage == true`) は本 scope 外なので base に委譲。
/// </summary>
internal sealed class SafeLinkInlineRenderer : LinkInlineRenderer
{
    protected override void Write(HtmlRenderer renderer, LinkInline link)
    {
        if (link.IsImage)
        {
            base.Write(renderer, link);
            return;
        }

        // base 実装と同じ URL 決定順 (GetDynamicUrl が優先)。
        string? url = link.GetDynamicUrl != null ? link.GetDynamicUrl() ?? link.Url : link.Url;
        if (SafeLinkExtension.IsAllowedLinkUrl(url))
        {
            base.Write(renderer, link);
            return;
        }

        // 許可外 scheme: href を drop。CSS スタイルが残るよう <a> タグは維持する。
        // 属性配置は base LinkInlineRenderer の !IsImage 分岐と同順序で並べる。
        if (renderer.EnableHtmlForInline)
        {
            renderer.Write("<a");
            renderer.WriteAttributes(link);
            if (!string.IsNullOrEmpty(link.Title))
            {
                renderer.Write(" title=\"");
                renderer.WriteEscape(link.Title);
                renderer.Write('"');
            }
            if (!string.IsNullOrWhiteSpace(Rel))
            {
                renderer.Write(" rel=\"");
                renderer.Write(Rel);
                renderer.Write('"');
            }
            renderer.Write('>');
        }
        renderer.WriteChildren(link);
        if (renderer.EnableHtmlForInline)
        {
            renderer.Write("</a>");
        }
    }
}

/// <summary>
/// CommonMark autolink (`<https://x>` / `<user@host>`) 用の安全化 renderer。
/// Email autolink は Markdig が mailto: を強制付加するため常に安全なので base に委譲。
/// URI autolink は同一 scheme whitelist を適用する。
/// </summary>
internal sealed class SafeAutolinkInlineRenderer : AutolinkInlineRenderer
{
    protected override void Write(HtmlRenderer renderer, AutolinkInline obj)
    {
        if (obj.IsEmail)
        {
            base.Write(renderer, obj);
            return;
        }
        if (SafeLinkExtension.IsAllowedLinkUrl(obj.Url))
        {
            base.Write(renderer, obj);
            return;
        }

        // 許可外 scheme: href を drop。生 URL を表示テキストとして残す (base と同じ)。
        if (renderer.EnableHtmlForInline)
        {
            renderer.Write("<a");
            renderer.WriteAttributes(obj);
            renderer.Write('>');
        }
        renderer.WriteEscape(obj.Url);
        if (renderer.EnableHtmlForInline)
        {
            renderer.Write("</a>");
        }
    }
}
