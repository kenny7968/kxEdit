using kxEdit.Core.Text;

namespace kxEdit.Core.Tests.Text;

/// <summary>
/// A-2 / 設計書 §7.2: プレビュー本文の相対 URL を preview 仮想ホスト基準へ絶対化する
/// 純粋ロジックの判定規則を機械固定する。
/// <para>
/// 最重要は規則 2 (<c>#</c> 始まりは書き換えない)。ここを緩めると目次リンクと脚注の
/// 戻りリンクが <c>https://kxedit.preview/#...</c> へ解決され、MD-H-1 の Block に
/// 巻き込まれてページ内ナビゲーションが全滅する (FINDING 3)。
/// </para>
/// </summary>
public class PreviewUrlResolverTests
{
    [Theory]
    [InlineData("pic.png", "https://kxedit.preview/pic.png")]
    [InlineData("sub/other.md", "https://kxedit.preview/sub/other.md")]
    [InlineData("/root.png", "https://kxedit.preview/root.png")]
    [InlineData("./pic.png", "https://kxedit.preview/pic.png")]
    // Uri の解決は ../ を仮想ホストのルートで打ち切る (親フォルダへは出られない)。
    // 元々 preview 仮想ホストのマッピング外は WebView2 が配信しないので実害の変化は無いが、
    // 「絶対化がパスを外へ広げない」ことは security 上の不変条件なので機械固定する。
    [InlineData("../../secret.txt", "https://kxedit.preview/secret.txt")]
    // percent-escape を保った正規形 (AbsoluteUri) で返すこと。ToString() は表示用に復号する
    // ので out 値に生の < や " が載り、安全性が下流の Markdig の再エスケープ依存になる。
    [InlineData("%3Cscript%3E.png", "https://kxedit.preview/%3Cscript%3E.png")]
    [InlineData("a%22b.png", "https://kxedit.preview/a%22b.png")]
    public void Relative_IsResolved(string input, string expected)
    {
        Assert.True(PreviewUrlResolver.TryResolve(input, out string? actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#midashi")] // FINDING 3: 同一文書内アンカーを守る
    [InlineData("#fn:1")] // 脚注の戻りリンク
    [InlineData("//evil.example/p")] // protocol-relative は別ホストへ飛ぶので触らない
    // 規則 3 (// 始まり) の網。上の "//evil.example/p" は Windows の Uri が UNC パスと解釈して
    // file://evil.example/p になるため規則 4 が偶然拾ってしまい、規則 3 を消しても赤くならない。
    // port / userinfo が付くと UNC として解釈されなくなり規則 4 を素通りするので、
    // 規則 3 だけが止められる入力としてこの 2 本を並べる (ミューテーションで確認済み)。
    [InlineData("//evil.example:8080/p")]
    [InlineData("//user@evil.example/p")]
    // 事後条件 (解決結果が preview origin であること) の網。規則 3 は前方一致にすぎず、
    // 先頭にバックスラッシュ / 空白 / タブが付くと素通りする。Uri は先頭空白を捨て、
    // バックスラッシュを / へ正規化してから authority を解釈するため、これらは規則 4 も
    // 抜けて別ホストへ解決されていた (最終レビューで両パスが独立に発見)。
    [InlineData("\\/evil.example:8080/p")]
    [InlineData("\\/user@evil.example/p")]
    [InlineData("\\/evil.example#x")]
    [InlineData("\\/evil.example?q")]
    [InlineData(" //evil.example?x=1")]
    [InlineData("\t//evil.example#f")]
    // userinfo だけが違う形。Host / GetLeftPart(Authority) は userinfo を含まないので、
    // 事後条件から UserInfo 検査を外すとこの 1 本だけがすり抜ける。
    [InlineData("\\/user@kxedit.preview/x")]
    // port だけが違う形。scheme と host は preview と一致するので、事後条件から Port 検査を
    // 外すとこの 3 本だけがすり抜ける (別 origin なので CSP の allow-list にも載らない)。
    [InlineData("\\/kxedit.preview:8443/p")]
    [InlineData("\\/kxedit.preview:80/p")]
    [InlineData(" //kxedit.preview:8443/p")]
    [InlineData("https://example.com/")]
    [InlineData("http://example.com/")]
    [InlineData("mailto:a@b.c")]
    [InlineData("javascript:alert(1)")] // scheme 付きは SafeLinkExtension の管轄
    [InlineData("data:text/html,x")]
    public void NotRewritten(string? input)
    {
        Assert.False(PreviewUrlResolver.TryResolve(input, out string? actual));
        Assert.Null(actual);
    }
}
