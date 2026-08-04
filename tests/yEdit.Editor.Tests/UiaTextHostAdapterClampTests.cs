// UiaTextHostAdapterClampTests.cs
// 2026-07-31 文字アクセス seam Task 5: NextChar/PrevChar を Core の TextBoundary へ
// 委譲するにあたり、Adapter 側に残る前処理(範囲外 clamp / snapshot null)の等価性を固定する。
//
// 委譲によって「歩進規則が Core と同じ」ことは構造的に保証されるが、clamp は Adapter に
// 残るため、ここが唯一の網になる。特に TextBoundary の範囲外契約は非対称
// (Next* は pos < 0 で throw・Prev* は pos > CharLength で throw)なため、
// clamp を削ると UIA クライアントからの範囲外オフセットで a11y 経路が例外になる。
//
// 2026-08-04 最終レビュー Important-2: 同じ非対称は WordBoundary へ委譲する単語系 4 メンバ
// (WordStart / WordEnd / NextWordStart / PrevWordStart)にもあり、そちらは網が無かった。
// WordMembers_OutOfRangeOffset_ClampInsteadOfThrowing がその穴を塞ぐ。
using yEdit.Accessibility;

namespace yEdit.Editor.Tests;

public class UiaTextHostAdapterClampTests
{
    [Fact]
    public void NextChar_NegativeOffset_ClampsToZeroThenSteps() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(1, host.NextChar(-100)); // 0 へ clamp してから 1 歩
        });

    [Fact]
    public void NextChar_BeyondEnd_ClampsToCharLength() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(3, host.NextChar(999));
        });

    [Fact]
    public void PrevChar_NegativeOffset_ClampsToZero() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(0, host.PrevChar(-100));
        });

    [Fact]
    public void PrevChar_BeyondEnd_ClampsThenSteps() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(2, host.PrevChar(999)); // 3 へ clamp してから 1 歩戻る
        });

    [Fact]
    public void NextPrevChar_BeforeSetSource_ReturnZero() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl(); // SetSource 前 = snapshot null
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(0, host.NextChar(5));
            Assert.Equal(0, host.PrevChar(5));
        });

    /// <summary>
    /// 単語系 4 メンバ(<c>WordStart</c> / <c>WordEnd</c> / <c>NextWordStart</c> /
    /// <c>PrevWordStart</c>)も範囲外オフセットを clamp してから Core へ渡すこと。
    /// </summary>
    /// <remarks>
    /// 2026-08-04 最終レビュー Important-2 の網。<c>Math.Clamp</c> を消しても Editor 365 件が
    /// 全緑=<b>この clamp に網が 1 本も無かった</b>。飾りではないことは実測済みで、
    /// <c>WordBoundary</c> の範囲外契約は <c>NextChar</c> / <c>PrevChar</c> 側と同じく非対称:
    /// <list type="bullet">
    /// <item><c>WordStart(snap, len + 1, cap)</c> → <c>PrevCodePoint</c> 経由で
    /// <c>ArgumentOutOfRangeException</c>。<c>WordStart(snap, -5, cap)</c> は
    /// <c>pos &lt;= 0</c> の早期 return なので<b>投げない</b>。</item>
    /// <item><c>WordEnd(snap, len + 1, cap)</c> は <c>pos &gt;= CharLength</c> の早期 return で
    /// <b>投げない</b>が、<c>WordEnd(snap, -1, cap)</c> は <c>ClassOf</c> → <c>GetChar(-1)</c> で投げる。</item>
    /// </list>
    /// = <b>上限側だけ / 下限側だけを見て「クランプ不要」と判断できない</b>
    /// (<c>WordBoundary.WordStart</c> の xmldoc が警告している罠そのもの)。
    /// UIA クライアントは任意 offset を渡せるので、これは RPC スレッドへ例外が漏れる経路である。
    ///
    /// ミューテーション検証(2026-08-04): 4 メンバの <c>Math.Clamp</c> を除去すると
    /// 本テストが <c>ArgumentOutOfRangeException</c> で赤くなる(<c>WordStart</c> は上限側の
    /// <c>len + 1</c> が、<c>WordEnd</c> は下限側の <c>-1</c> が殺す)。
    /// </remarks>
    [Fact]
    public void WordMembers_OutOfRangeOffset_ClampInsteadOfThrowing() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("ab cd")); // CharLength = 5
            var host = (IUiaTextHost)ctrl;

            Assert.Equal(3, host.WordStart(6)); // 5 へ clamp → "cd" の頭
            Assert.Equal(0, host.WordStart(-1)); // 0 へ clamp
            Assert.Equal(5, host.WordEnd(6)); // 5 へ clamp → EOF
            Assert.Equal(2, host.WordEnd(-1)); // 0 へ clamp → "ab" の末尾(末尾空白は含まない)
            Assert.Equal(3, host.NextWordStart(-1)); // 0 へ clamp → 次単語の頭
            Assert.Equal(3, host.PrevWordStart(6)); // 5 へ clamp → "cd" の頭
        });

    [Fact]
    public void PrevChar_ClampedFromBeyondEnd_SkipsCrlfPair() =>
        Sta.Run(() =>
        {
            // clamp と CRLF atomic が両方効く経路(委譲後も維持されること)
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\r\n")); // CharLength=3
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(1, host.PrevChar(999)); // 3 へ clamp → CRLF を 1 歩で戻る
        });
}
