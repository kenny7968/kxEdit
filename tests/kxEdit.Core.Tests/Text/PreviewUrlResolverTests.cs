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
    // port / userinfo が付く protocol-relative。UNC として解釈されなくなるため規則 4
    // (Uri.TryCreate(Absolute)) を素通りする。ただしこれらを実際に止めているのは事後条件で
    // あって規則 3 ではない: 規則 3 (StartsWith("//")) は早期 return にすぎず、それだけを
    // 削っても全緑になる (= 前方一致側に固有の網は無い。ミューテーションで確認済み)。
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
    // userinfo だけが違う形。Uri.Host も Uri.Authority も userinfo を含まないので、
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

    [Theory]
    // preview 仮想ホスト宛の URL に残った %2f / %5c / 生 '\' は % 自身をエスケープして
    // 無害化する (区切り文字を含まない 1 つのファイル名への要求になる)。
    // System.Uri はこれらを復号しない (AbsoluteUri / AbsolutePath いずれもエスケープを大小
    // 込みで保つ・実測) ので、ここで潰さない限り WebView2 まで生のまま届く。
    [InlineData("https://kxedit.preview/..%2f..%2fx", "https://kxedit.preview/..%252f..%252fx")]
    // 大小保存 (%2F → %252F)。IgnoreCase の検出と、置換で元の大小を保つことの両方を固定する。
    [InlineData("https://kxedit.preview/..%2F..%2Fx", "https://kxedit.preview/..%252F..%252Fx")]
    [InlineData("https://kxedit.preview/a%5cb", "https://kxedit.preview/a%255cb")]
    [InlineData("https://kxedit.preview/a%5Cb", "https://kxedit.preview/a%255Cb")]
    // --- 脆弱性レビュー (2026-09-03) で実測された迂回形 ---
    // F-1: 非 ASCII ホスト。Uri.Host は Unicode を保つので Host 比較では外れるが、
    // Markdig の WriteEscapeUrl は IdnHost で ASCII 化して出力する = 実質 preview 宛。
    // 判定を Uri.Host に戻すとこの 2 本が落ちる。
    [InlineData("https://kxedit。preview/..%2f..%2fx", "https://kxedit。preview/..%252f..%252fx")]
    [InlineData("https://ｋxedit.preview/..%2f..%2fx", "https://ｋxedit.preview/..%252f..%252fx")]
    // F-2: percent-encode されたホスト。.NET は Uri.TryCreate(Absolute) に失敗するが
    // WHATWG (Chromium) は percent-decode → domain-to-ASCII で kxedit.preview に解決する。
    // parse 不能を「そのまま返す」へ戻すとこの 2 本が落ちる。
    [InlineData("https://%6bxedit.preview/..%2f..%2fx", "https://%6bxedit.preview/..%252f..%252fx")]
    [InlineData("https://kxedit%2epreview/..%2f..%2fx", "https://kxedit%2epreview/..%252f..%252fx")]
    // F-3: 末尾ドット。Uri.Host も Uri.IdnHost も末尾ドットを保持するので明示的に削る。
    // TrimEnd('.') を外すとこの 1 本が落ちる。
    [InlineData("https://kxedit.preview./..%2f..%2fx", "https://kxedit.preview./..%252f..%252fx")]
    // F-4: 生のバックスラッシュ。LinkRewriter は Markdig がエスケープする前の URL を渡すので
    // ここには生 '\' が届く。素通りさせると直後の WriteEscapeUrl が %5C を作る。
    // 正規表現から `|\\` を外すとこの 2 本が落ちる。
    [InlineData(
        "https://kxedit.preview/..\\..\\secret.txt",
        "https://kxedit.preview/..%255C..%255Csecret.txt"
    )]
    [InlineData("..\\..\\secret.txt", "..%255C..%255Csecret.txt")] // 相対形 (parse 不能側)
    // 対象外はそのまま返す (退化していないことの対照)
    [InlineData("https://kxedit.preview/my%20file.png", "https://kxedit.preview/my%20file.png")]
    [InlineData("https://example.com/a%2fb", "https://example.com/a%2fb")] // 外部 origin
    [InlineData("https://example.com/a%5cb", "https://example.com/a%5cb")] // 外部 origin
    [InlineData("https://example.com/a\\b", "https://example.com/a\\b")] // 外部 origin の生 '\'
    // 末尾ドットが付いた外部ホストも外部のまま (TrimEnd('.') が preview 側だけに効くこと)。
    [InlineData("https://example.com./a%2fb", "https://example.com./a%2fb")]
    // mailto: は「parse できてホストが preview でない」で除外される (default-deny の穴埋めが
    // 正当な scheme を巻き込んでいないことの対照)。
    [InlineData("mailto:a%2fb@example.com", "mailto:a%2fb@example.com")]
    [InlineData("#anchor", "#anchor")]
    // 裸のフラグメントは同一文書内スクロール。# ガードを外すとこの 1 本が落ちる
    // (default-deny により parse 不能側へ落ちて %252f 化されるため)。
    [InlineData("#a%2fb", "#a%2fb")]
    // 相対 URL は絶対 URL として parse できない。default-deny 側へ落ちるが、対象文字を
    // 含まないので replace は no-op になり結果は不変。
    [InlineData("pic.png", "pic.png")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void NeutralizeEncodedSeparators_Cases(string? input, string? expected) =>
        Assert.Equal(expected, PreviewUrlResolver.NeutralizeEncodedSeparators(input));
}
