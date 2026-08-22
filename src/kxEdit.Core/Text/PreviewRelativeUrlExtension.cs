using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace kxEdit.Core.Text;

/// <summary>
/// 本文中の相対 URL を <see cref="PreviewUrlResolver"/> で絶対化する Markdig 拡張。
/// <c>LinkInline</c> はリンクと画像の両方を表すので 1 箇所で足りる。
/// 書き換えは <c>DocumentProcessed</c> (描画前) で行うため、描画時に効く
/// <see cref="SafeLinkExtension"/> の scheme whitelist より前段になる。
/// scheme 付き URL は <see cref="PreviewUrlResolver"/> が触らないので whitelist の判定は不変。
/// </summary>
internal sealed class PreviewRelativeUrlExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        // -= は Setup が同一 builder に対して複数回呼ばれた場合の冪等化。現状その経路は無い
        // (builder は BuildPipeline のローカル 1 個で Build() は 1 回だけ) ので到達不能であり、
        // 変異させてもテストは赤くならない。二重登録の実例があるという意味ではない。
        pipeline.DocumentProcessed -= OnDocumentProcessed;
        pipeline.DocumentProcessed += OnDocumentProcessed;
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }

    private static void OnDocumentProcessed(MarkdownDocument document)
    {
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (PreviewUrlResolver.TryResolve(link.Url, out string? absolute))
            {
                link.Url = absolute;
            }
        }
    }
}
