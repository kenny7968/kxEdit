using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace kxEdit.Core.Text;

/// <summary>
/// preview 経路の URL 書き換えを担う Markdig 拡張。役割は 2 つあり、<b>適用段が違う</b>。
/// <list type="number">
///   <item><b>相対 URL の絶対化</b> (A-2) —— AST 段 (<c>DocumentProcessed</c>) で
///     <c>LinkInline</c> を走査する。<c>LinkInline</c> はリンクと画像の両方を表し、
///     CommonMark autolink は定義上 scheme 必須 (= 相対になりえない) ので、
///     絶対化にはこの 1 箇所で足りる。描画時に効く <see cref="SafeLinkExtension"/> の
///     scheme whitelist より前段になるが、scheme 付き URL は
///     <see cref="PreviewUrlResolver.TryResolve"/> が触らないので whitelist の判定は不変。</item>
///   <item><b>区切り文字の密輸の無害化</b> (V-3) —— 描画段
///     (<c>HtmlRenderer.LinkRewriter</c>) で行う。理由は
///     <see cref="Setup(MarkdownPipeline, IMarkdownRenderer)"/> のコメント参照。</item>
/// </list>
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

    /// <summary>
    /// V-3: 区切り文字の密輸 (<c>%2f</c> / <c>%5c</c> / 生の <c>\</c>) を潰すガードを
    /// Markdig の <c>LinkRewriter</c> として登録する。
    /// <para>
    /// <b>AST 段 (<c>DocumentProcessed</c>) ではなくここに置く理由。</b>
    /// 主張と実測をきっちり分けて書く (ここを曖昧にすると「実在しない防御を謳う」ことになる):
    /// <list type="number">
    ///   <item><b>これだけが実測で必須</b> —— <c>Descendants&lt;LinkInline&gt;()</c> は
    ///     <c>AutolinkInline</c> (<c>&lt;https://…&gt;</c>) を拾わないので、AST 段のガードでは
    ///     autolink が素通りする (F-5)。<c>LinkRewriter</c> は autolink 経路でも発火する
    ///     (実測)。ガードを AST 段へ戻す変異を掛けると、落ちるのは
    ///     <c>Preview_EncodedSeparators_NeverReachOutput</c> の autolink ケース<b>1 本だけ</b>
    ///     だった (実測)。</item>
    ///   <item><b>設計上の整合 (現時点では穴ではない)</b> —— 主張は「出力 HTML に区切り
    ///     エスケープを載せない」であり、<c>LinkRewriter</c> は <c>WriteEscapeUrl</c> の中
    ///     = 出力を書く直前で適用されるので、主張と適用点が一致する。
    ///     F-4 (生の <c>\</c> を <c>WriteEscapeUrl</c> が <c>%5C</c> にする) を実際に塞いだのは
    ///     この移設ではなく <see cref="PreviewUrlResolver"/> 側の 2 つの修正
    ///     (<c>AbsolutePath</c> 前置チェックの撤去と、生 <c>\</c> の対象化) である ——
    ///     上記の変異で F-4 のケースは落ちなかった。<br/>
    ///     旧実装が F-4 を取り逃がしていた原因自体は AST 段固有ではあった:
    ///     <c>Uri.AbsolutePath</c> が <c>\</c> を <c>/</c> へ正規化して dot-segment を畳むため
    ///     「区切りは無い」と見えていた。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 上書き衝突は無い: Markdig の既定値は null で、本リポジトリで
    /// <c>LinkRewriter</c> を設定するのはここだけ (grep 済み・既定値 null は実測)。
    /// <c>Setup</c> は描画のたびに新しい <c>HtmlRenderer</c> に対して呼ばれるので、
    /// 代入が renderer 間で共有されることもない。
    /// </para>
    /// </summary>
    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer html)
        {
            html.LinkRewriter = PreviewUrlResolver.NeutralizeEncodedSeparators;
        }
    }

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
