using System.Windows.Automation.Text;
using yEdit.Accessibility;

namespace yEdit.Editor.Tests;

/// <summary>
/// UIA の <c>ExpandToEnclosingUnit(TextUnit.Word)</c> を、本物のホスト
/// (<c>(IUiaTextHost)EditorControl</c>)× 本物の Provider で end-to-end に固定する。
/// </summary>
/// <remarks>
/// <b>なぜ Core.Tests 側では足りないか</b>: <c>TextRangeProviderV2Tests.InMemoryHost</c> は
/// 空白区切りの独自 stub で、Core の文字クラス規則を一切通らない。Task 3 で SR の読み上げ
/// スパンをクラス規則へ差し替えたあとも、「<b>SR が実際に受け取る文字列</b>」を固定した網は
/// どこにも無かった(Task 3 仕様レビューのカバレッジ穴)。ここがその網である。
///
/// <c>EditorControlUiaHostTests.Host_WordSpan_UsesCharClassRule</c> との重複ではない。
/// あちらは host の <c>WordStart</c> / <c>WordEnd</c> が返す<b>オフセット</b>を見るが、
/// こちらは Provider がそれを<b>どう合成して文字列にするか</b>を見る。Task 4 が変えたのは
/// 後者だけなので、あちらは全件不変のままここだけが動く。
///
/// 期待値は実測から採っている(2026-08-04・Task 4 実施時)。
/// </remarks>
public class UiaWordUnitExpandTests
{
    /// <param name="text">本文。</param>
    /// <param name="pos">キャレット位置(縮退範囲の起点)。</param>
    /// <param name="expected">expand 後に <c>GetText</c> が返す文字列。</param>
    [Theory]
    // F-3 解消の本体。修正前(Task 3 前)は空白が無いので行全体が 1 単語だった。
    [InlineData("今日は晴れです。", 0, "今日")]
    // 英文の対照群。単語の内側なので Task 4 の起点変更でも動かない。
    [InlineData("foo bar baz", 4, "bar")]
    // 空白 run の上 = 起点変更の影響が最も出る位置。
    // Task 4 前は "ab"(WordEnd(_start=0) が末尾空白を巻き戻して 2 で止まる)。
    // Task 4 後は WordEnd(pos=4) = 4 なので、左の単語 + キャレットまでの空白を含む。
    [InlineData("ab    cd", 4, "ab  ")]
    // 行頭インデント。WordStart(0) == WordEnd(0) == 0 で縮退し、NextChar フォールバックが
    // 空白 1 文字を返す経路。pos == _start なので起点変更では動かない。
    [InlineData("    hello", 0, " ")]
    // ---- 以下 3 行は Task 4 仕様レビュー Important-2 で追加(変化する族を 1 行ずつ代表させる)。
    // 行頭インデントの run 2 文字目以降。左に単語が無いので _start は 0 のまま伸びる。
    // host レベルの WordStart/WordEnd を見る EditorControlUiaHostTests の
    // ("    hello", 2) → [0,2) と対になる(あちらは offset・こちらは文字列)。
    [InlineData("    hello", 2, "  ")]
    // EOF(末尾が空白 run)。Task 4 前は "ab" で末尾空白が落ちていた。
    [InlineData("ab   ", 5, "ab   ")]
    // ★ 改行を含むスパン = Task 4 で生じた新種。Task 4 前のスパンは改行を跨げなかった。
    // ダブルクリック単語選択とは一致するので設計方針とは整合するが、SR 可視の挙動変更である。
    [InlineData("a\r\nb  \n  c", 7, "b  \n")]
    public void ExpandToEnclosingUnit_Word_ReturnsCharClassSpan(
        string text,
        int pos,
        string expected
    )
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString(text));
            IUiaTextHost host = ctrl;
            var root = new TextControlProviderV2(host);
            var provider = new TextProviderImplV2(host, root);
            var range = new TextRangeProviderV2(provider, pos, pos);

            range.ExpandToEnclosingUnit(TextUnit.Word);

            Assert.Equal(expected, range.GetText(int.MaxValue));
        });
    }

    /// <summary>
    /// 全 pos で <c>_start &lt;= pos &lt;= _end</c>(=窓がキャレットに固定されている)ことを、
    /// Task 5 で cap が入る前の本物のホストで押さえる。反転レンジは UIA 側で未定義動作になるが
    /// <c>GetText</c> では空文字列に潰れて見えないため、オフセットを直接見る。
    /// </summary>
    /// <remarks>
    /// 「キャレット中心」なのは走査の窓であってスパンの包含ではない
    /// (正本 = <c>yEdit.Core.Editing.WordBoundary</c> クラスの <c>&lt;remarks&gt;</c>
    /// 「窓についてよくある誤読」(2))。ゆえに下の assert は <c>pos &lt;= end</c> であって
    /// <c>pos &lt; end</c> ではない(<c>&lt;</c> にすると空白 run で落ちる)。
    ///
    /// 起点を <c>pos</c> にした以上、この不変条件の担保は host 側の
    /// <c>WordStart(pos) &lt;= pos &lt;= WordEnd(pos)</c> に移った
    /// (<c>WordBoundary.WordEnd</c> の xmldoc が根拠。Core 側は
    /// <c>WordEnd_OnWhitespaceRun_NeverReturnsBeforePos</c> が固定している)。
    /// ここでは Provider を通した形で、空白 run / 行頭 / EOF / 改行を含む全 pos を総当りする。
    /// Task 5 で cap を入れると窓が <c>[pos-(cap-1), pos+cap]</c> へ狭まるが、
    /// 本テストはその後も同じ形で成立していなければならない(= リスク R2 の網)。
    /// </remarks>
    [Theory]
    [InlineData("ab    cd")]
    [InlineData("    hello")]
    [InlineData("今日は晴れです。")]
    [InlineData("foo(bar)=baz;")]
    [InlineData("a\r\nb  \n  c")]
    [InlineData("")]
    public void ExpandToEnclosingUnit_Word_NeverProducesReversedRange(string text)
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString(text));
            IUiaTextHost host = ctrl;
            var root = new TextControlProviderV2(host);
            var provider = new TextProviderImplV2(host, root);
            var zero = new TextRangeProviderV2(provider, 0, 0);

            for (int pos = 0; pos <= host.TextLength; pos++)
            {
                var range = new TextRangeProviderV2(provider, pos, pos);
                range.ExpandToEnclosingUnit(TextUnit.Word);

                int start = range.CompareEndpoints(
                    TextPatternRangeEndpoint.Start,
                    zero,
                    TextPatternRangeEndpoint.Start
                );
                int end = range.CompareEndpoints(
                    TextPatternRangeEndpoint.End,
                    zero,
                    TextPatternRangeEndpoint.Start
                );
                Assert.True(end >= start, $"reversed range at pos={pos}: [{start}, {end})");
                Assert.True(
                    start <= pos && pos <= end,
                    $"window not anchored at caret pos={pos}: [{start}, {end})"
                );
            }
        });
    }
}
