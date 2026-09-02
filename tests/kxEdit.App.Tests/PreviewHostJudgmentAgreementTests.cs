using kxEdit.Core.Text;

namespace kxEdit.App.Tests;

/// <summary>
/// D (最終レビュー): preview 仮想ホストかどうかの判断が、Core (無害化) と App (Block) で
/// <b>一致し続ける</b>ことを機械固定する。
/// <para>
/// <b>なぜ要るか</b>: 判定は元々 2 アセンブリに逐語コピーされており、本ブランチ自身が
/// 「片方だけ直す」事故を実際に踏んでいる —— F-7 の commit は
/// <c>PreviewNavigationPolicy.Classify</c> を <c>Uri.Host</c> 直比較のまま出し、
/// 末尾ドット (<c>kxedit.preview.</c>) の穴が F-3 で後から判明した。
/// 現在は <see cref="MarkdownRenderer.TryIsPreviewHost"/> 1 箇所に集約してあるが、
/// 「集約されていること」自体は網ではない (どちらかがまた自前判定を持てば静かに戻る)。
/// </para>
/// <para>
/// <b>何を assert しているか</b>: 同じ URL に対して
/// <c>Classify == Block</c> と <c>NeutralizeEncodedSeparators が書き換える</c> が同値であること。
/// <b>両者は「同じ向きに倒す」わけではない</b>点に注意 —— Core は「判断がつかない = 無害化する」
/// (default-deny)、App は「判断がつかない = Block」(safe by default) で、
/// <b>意味は違うが結論が一致する</b>ように出来ている。共通化したのはホスト一致判定だけで、
/// 判断不能時の扱いは各呼び出し側が持つ (それぞれの private ヘルパーの doc 参照)。
/// </para>
/// <para>
/// 表の URL には <c>%2f</c> を必ず含める。含めないと無害化は no-op になり
/// 「書き換えたか」で判定できない (退化した網になる)。
/// </para>
/// </summary>
public class PreviewHostJudgmentAgreementTests
{
    [Theory]
    // --- preview 宛と判断されるべき形 ---
    [InlineData("kxedit.preview", true)]
    [InlineData("KXEDIT.PREVIEW", true)]
    [InlineData("kxedit.preview.", true)] // F-3: 末尾ドット
    [InlineData("kxedit。preview", true)] // F-1: U+3002 全角句点
    [InlineData("ｋxedit.preview", true)] // F-1: U+FF4B 全角 k
    [InlineData("%6bxedit.preview", true)] // F-2: .NET は parse 不能 / WHATWG は解決する
    [InlineData("kxedit%2epreview", true)] // F-2
    [InlineData("xn--あ", true)] // A: IdnHost が投げる = 判断不能
    // 次の 1 本のホストは目に見えない文字そのもの。codepoint を併記する。
    [InlineData("\U0000200B.example", true)] // A: U+200B / 判断不能
    // --- 明確に外部と分かる形 (非退化の対照) ---
    [InlineData("example.com", false)]
    [InlineData("example.com.", false)] // 末尾ドット除去は preview 側だけに効く
    [InlineData("kxedit.preview.evil.com", false)]
    public void ClassifyAndNeutralize_AgreeOnHost(string host, bool expectedPreviewBound)
    {
        string url = $"https://{host}/..%2f..%2fx";

        bool blocked =
            PreviewNavigationPolicy.Classify(url) == PreviewNavigationPolicy.Classification.Block;
        bool neutralized = !string.Equals(
            PreviewUrlResolver.NeutralizeEncodedSeparators(url),
            url,
            StringComparison.Ordinal
        );

        Assert.Equal(expectedPreviewBound, blocked);
        Assert.Equal(expectedPreviewBound, neutralized);
    }
}
