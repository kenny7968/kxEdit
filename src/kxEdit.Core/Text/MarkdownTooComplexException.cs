namespace kxEdit.Core.Text;

/// <summary>
/// B (最終レビュー): Markdig が入力の構造を扱いきれずに投げた例外を、
/// <see cref="MarkdownRenderer.Render"/> の caller が扱える型へ翻訳したもの。
/// <para>
/// <b>実測された発火条件</b>: Markdig の <c>MaximumNestingDepth</c>(既定 128)超過。
/// <c>"&gt; " × 200</c>(<b>400 バイト</b>)や <c>"[" × 20000</c> で
/// <c>System.ArgumentException("Markdown elements in the input are too deeply nested …")</c>
/// が出る。<see cref="MarkdownRenderer.MaxMarkdownChars"/>(4,000,000 文字)の
/// <b>はるか下</b>なので、入口の文字数 cap では止まらない。
/// (<c>"- " × 200</c> は thematic break として解釈され<b>投げない</b> —— 実測。)
/// </para>
/// <para>
/// <b>なぜ独立した型か</b>: <see cref="MarkdownRenderer.Render"/> は baseHref の
/// allow-list 違反 (MD-L-4) でも <see cref="ArgumentException"/> を投げる。あちらは
/// <b>呼び出し側の実装バグ</b>なので握り潰してはならない。caller が
/// <c>ArgumentException</c> を無差別に捕まえる形にしないため、Markdig 由来のものだけを
/// この型へ翻訳する (翻訳は <c>Markdown.ToHtml</c> の呼び出しだけを囲む try で行うので、
/// allow-list ガードは構造的にこの型へ入らない)。
/// </para>
/// <para>
/// <b>推測 (未実測)</b>: <c>Markdown.ToHtml</c> が深度上限<b>以外</b>の理由で
/// <c>ArgumentException</c> を投げる経路があるかは確認していない。もし在れば
/// それもこの型になる。<c>ArgumentNullException</c> だけは除外してあり
/// (実装バグの伝播原則)、そのまま抜ける。
/// </para>
/// </summary>
public sealed class MarkdownTooComplexException : Exception
{
    public MarkdownTooComplexException() { }

    public MarkdownTooComplexException(string message)
        : base(message) { }

    public MarkdownTooComplexException(string message, Exception innerException)
        : base(message, innerException) { }
}
