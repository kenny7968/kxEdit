using System.Reflection;
using yEdit.Accessibility;
using yEdit.Core.Editing;

namespace yEdit.Editor.Tests;

/// <summary>
/// 本ブランチの中心命題「<b>見える選択と聞くスパンが一致する</b>」を固定する。
/// </summary>
/// <remarks>
/// 単語スパンを出す本番経路は 3 つある — SR の読み上げ(<c>UiaTextHostAdapter</c>)・
/// Ctrl+←→(<c>InputRouter.HandleLeft/HandleRight</c>)・ダブルクリック単語選択
/// (<c>InputRouter.HandleMouseDoubleClick</c>)。3 経路は <b>同じ走査上限</b>
/// (<see cref="WordBoundary.DefaultMaxScan"/>)を使わなければならない。片方だけ広げると
/// 同じ位置で選択範囲と読み上げスパンが食い違い、F-3(SR のスパンがダブルクリック選択と
/// ずれる)の再導入になる。<c>maxScan</c> がパラメータなので技術的には 1 行で分けられてしまう。
///
/// Ctrl+←→ 側は <c>KeyboardNavigationTests</c> の <c>*_OnHugeSingleClassLine_StopsAtScanLimit</c>
/// が厳密比較で守っている。本ファイルが守るのは<b>ダブルクリック経路</b>と、
/// <b>ダブルクリック ⇔ SR の cap 一致</b>である。
///
/// マウス入力の挙動そのもの(どの座標がどの単語を選ぶか)は <c>MouseInputTests</c> の担当で、
/// あちらは Task 2 の挙動不変の一次証拠として無改修を維持する。ゆえにダブルクリックを叩く
/// ヘルパは<b>本ファイル側に複製</b>してある(同一のリフレクション手順)。
/// </remarks>
public class WordSpanConsistencyTests
{
    private static (Form f, EditorControl c) MakeControl(string text)
    {
        var f = new Form();
        var c = new EditorControl();
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    // MouseInputTests.SendMouseDoubleClick と同一手順。あちらを触らないための意図的な複製。
    private static void SendMouseDoubleClick(EditorControl c, int x, int y)
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnMouseDoubleClick",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        mi!.Invoke(c, new object[] { new MouseEventArgs(MouseButtons.Left, 2, x, y, 0) });
    }

    /// <summary>
    /// 2026-08-04 F-5: 空白ゼロの単一クラス長大行で、ダブルクリック単語選択にも走査上限が
    /// 効いており、かつ<b>その範囲が SR の読み上げスパンと 1 文字たがわず一致</b>する。
    /// </summary>
    /// <remarks>
    /// この 1 本で 2 つを同時に固定する:
    /// <list type="number">
    /// <item>ダブルクリック経路に cap が適用されている(<c>NoScanLimit</c> へ戻すと選択が
    ///   行全体になり、下の厳密比較が落ちる)</item>
    /// <item>ダブルクリック経路と SR 経路が<b>同じ</b> cap を使う(片方だけ別値にすると
    ///   <c>Assert.Equal(host.WordStart(target), s)</c> が落ちる)</item>
    /// </list>
    ///
    /// <c>target</c> は <c>HandleMouseDoubleClick</c> が使うのと同じ式
    /// (<c>OffsetFromClientPoint(x, y)</c>)で、ダブルクリック<b>前に</b>求める
    /// (クリック自身の計算もそのスクロール状態で行われるため)。<c>clientX</c> は
    /// クライアント幅にクランプされないので、大きな x を渡すことで
    /// 「左右どちらも cap に当たる」深さまで届く(font 依存を吸収するため run は十分長く取る)。
    ///
    /// 期待値を <c>target ± cap</c> の厳密式で書いているのは、font 差でクリック位置が
    /// 変わっても成立し、かつ<b>予算の off-by-one を素通ししない</b>ため。
    /// 単一クラス run の内側では <c>WordStart = target - (cap - 1)</c>(左だけ 1 狭い)・
    /// <c>WordEnd = target + cap</c>(<see cref="WordBoundary"/> の xmldoc の窓の表)。
    /// </remarks>
    [Fact]
    public void DoubleClickSelection_AndSrWordSpan_ShareTheSameScanLimit() =>
        Sta.Run(() =>
        {
            int cap = WordBoundary.DefaultMaxScan;
            // font 依存でクリック位置が動いても [cap, Length - cap] に確実に収まる長さ。
            var (f, c) = MakeControl(new string('a', cap * 80));
            using (f)
            using (c)
            {
                IUiaTextHost host = c;
                const int X = 20_000; // クライアント幅を超える x でも OffsetFromClientPoint は clamp しない
                const int Y = 2;
                int target = c.OffsetFromClientPoint(X, Y);

                SendMouseDoubleClick(c, X, Y);
                var (s, e) = c.GetSelectionCharRange();

                // (1) ダブルクリック経路に cap が効いている(= 行頭 / 行末まで舐めていない)
                Assert.Equal(target - (cap - 1), s);
                Assert.Equal(target + cap, e);

                // (2) SR の読み上げスパンと完全一致 = 同じ cap を使っている
                Assert.Equal(host.WordStart(target), s);
                Assert.Equal(host.WordEnd(target), e);
            }
        });
}
