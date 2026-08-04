using System;
using System.Linq;
using System.Windows.Forms;
using Xunit;
using yEdit.Accessibility;
using yEdit.Core.Buffers;
using yEdit.Core.Editing;
using yEdit.Editor;

namespace yEdit.Editor.Tests;

public class EditorControlUiaHostTests
{
    [Fact]
    public void Host_GetTextRange_ReturnsSubstring()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("Hello, world!");
            ctrl.SetSource(buf);
            IUiaTextHost host = ctrl;
            Assert.Equal("Hello", host.GetTextRange(0, 5));
            Assert.Equal("world", host.GetTextRange(7, 5));
        });
    }

    [Fact]
    public void Host_TextLength_MatchesBuffer()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("abcdef");
            ctrl.SetSource(buf);
            Assert.Equal(6, ((IUiaTextHost)ctrl).TextLength);
        });
    }

    [Fact]
    public void Host_GetSelection_ReturnsCurrent()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("abcdefg");
            ctrl.SetSource(buf);
            ctrl.SetSelectionCharRange(2, 5);
            Assert.Equal((2, 5), ((IUiaTextHost)ctrl).GetSelection());
        });
    }

    [Fact]
    public void Host_LineStartOf_ReturnsLineStart()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("aaa\nbbb\nccc");
            ctrl.SetSource(buf);
            IUiaTextHost host = ctrl;
            Assert.Equal(0, host.LineStartOf(2));
            Assert.Equal(4, host.LineStartOf(5));
            Assert.Equal(8, host.LineStartOf(10));
        });
    }

    [Fact]
    public void Host_LineEndNoBreakOf_ExcludesLineBreak()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("aaa\nbbb");
            ctrl.SetSource(buf);
            IUiaTextHost host = ctrl;
            Assert.Equal(3, host.LineEndNoBreakOf(1)); // "aaa" の後・"\n" 前
            Assert.Equal(7, host.LineEndNoBreakOf(5)); // 末尾行
        });
    }

    // ===== P8-1c: 折り返し ON での視覚行境界(N-3 修正) =====

    [Fact]
    public void Host_LineStartOf_WithWrap_ReturnsVisualSegmentStart()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            // "abcdefghij" 10 文字を wrapColumns=4 で分割
            // GDI ASCII 幅は環境依存だが、ASCII は 8px 前後で MonoCharMetrics 相当。ここは実 GDI で計測。
            var buf = TextBuffer.FromString("abcdefghij");
            ctrl.SetSource(buf);
            ctrl.WrapColumns = 4;
            IUiaTextHost host = ctrl;
            int startOfMid = host.LineStartOf(6); // caret 6 の視覚 seg 先頭
            int startOfEnd = host.LineStartOf(9); // caret 9 の視覚 seg 先頭
            // 継続 seg=論理行先頭(0)ではなく視覚 seg の start
            Assert.True(
                startOfMid > 0,
                $"expected visual seg start > 0 for mid caret, got {startOfMid}"
            );
            Assert.True(
                startOfEnd > 0,
                $"expected visual seg start > 0 for end caret, got {startOfEnd}"
            );
        });
    }

    [Fact]
    public void Host_LineStartOf_WithWrap_FirstSegOfLine_ReturnsLogicalStart()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("abcdefghij");
            ctrl.SetSource(buf);
            ctrl.WrapColumns = 4;
            IUiaTextHost host = ctrl;
            // 第 1 視覚 seg 内(offset 0..3 想定)は論理行先頭 0
            Assert.Equal(0, host.LineStartOf(0));
            Assert.Equal(0, host.LineStartOf(1));
        });
    }

    [Fact]
    public void Host_LineEnd_WithWrap_ContinuationSeg_DoesNotCrossBreak()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("abcdefghij\nsecond");
            ctrl.SetSource(buf);
            ctrl.WrapColumns = 4;
            IUiaTextHost host = ctrl;
            // 第 1 論理行内の継続 seg の LineEnd は次視覚 seg 先頭=改行を跨がない
            int end = host.LineEnd(2); // 第 1 論理行の第 1 seg 内
            Assert.True(end <= 10, $"continuation LineEnd should not cross break (10), got {end}");
        });
    }

    [Fact]
    public void Host_LineEnd_WithWrap_LastSegOfLine_CrossesBreak()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("ab\ncd");
            ctrl.SetSource(buf);
            ctrl.WrapColumns = 4; // 2 文字は 1 視覚 seg に収まる=通常の論理行と同じ
            IUiaTextHost host = ctrl;
            // 論理行末最終 seg は改行を含めて次論理行先頭を返す(既存挙動維持)
            Assert.Equal(3, host.LineEnd(1)); // "ab" の後 = 3(改行含む)
            Assert.Equal(5, host.LineEnd(4)); // "cd" 末尾 = TextLength
        });
    }

    [Fact]
    public void Host_LineStartOf_WrapOff_UsesLogicalLine()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("aaa\nbbbbbbbbbb"); // wrap OFF なら bbb 側は 1 論理行
            ctrl.SetSource(buf);
            // WrapColumns = 0 が既定=wrap OFF
            IUiaTextHost host = ctrl;
            // 論理行先頭(0)を返す(P8-1c 前と同じ)
            Assert.Equal(4, host.LineStartOf(10));
            Assert.Equal(4, host.LineStartOf(13));
        });
    }

    // ===== P8 レビュー Important-1 対応: 日本語+RPC 越境の Invoke マーシャリング検証 =====

    [Fact]
    public void Host_LineStartOf_WithWrap_JapaneseContent_ComputesVisualSegmentStart()
    {
        // 日本語は非 ASCII=GdiCharMetrics.MeasureRun が TextRenderer(GDI)へ落ちる=UI スレッド必須。
        // Invoke マーシャリング済経路でも正しく視覚 seg 先頭が返ることを確認。
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            using var form = new Form { WindowState = FormWindowState.Minimized };
            form.Controls.Add(ctrl);
            form.Show();
            try
            {
                var buf = TextBuffer.FromString("あいうえおかきくけこさしすせそたちつてと");
                ctrl.SetSource(buf);
                ctrl.WrapColumns = 4; // 全角 4 col=約 64px=数 seg に分割
                IUiaTextHost host = ctrl;
                int startMid = host.LineStartOf(10);
                int startEnd = host.LineStartOf(18);
                // 継続 seg=論理行頭(0)ではなく視覚 seg 先頭
                Assert.True(
                    startMid > 0,
                    $"Japanese wrap mid: got {startMid}, expected visual seg start > 0"
                );
                Assert.True(
                    startEnd > 0,
                    $"Japanese wrap end: got {startEnd}, expected visual seg start > 0"
                );
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void Host_LineStartOf_WithWrap_CalledFromNonUiThread_MarshalsSafely()
    {
        // UIA RPC スレッド越境の再現: Task.Run から host.LineStartOf を呼ぶ。
        // Invoke マーシャリングが deadlock/例外なく結果を返すことを検証(Important-1 の根本ケース)。
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            using var form = new Form { WindowState = FormWindowState.Minimized };
            form.Controls.Add(ctrl);
            form.Show();
            try
            {
                var buf = TextBuffer.FromString("あいうえおかきくけこ");
                ctrl.SetSource(buf);
                ctrl.WrapColumns = 4;
                IUiaTextHost host = ctrl;

                int? result = null;
                Exception? ex = null;
                var task = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        result = host.LineStartOf(6);
                    }
                    catch (Exception e)
                    {
                        ex = e;
                    }
                });

                // UI スレッドで DoEvents ループを回して Invoke を進行させる
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!task.IsCompleted && sw.ElapsedMilliseconds < 3000)
                    Application.DoEvents();
                task.Wait(1000);

                Assert.True(
                    task.IsCompleted,
                    "cross-thread host call must complete without deadlock"
                );
                Assert.Null(ex);
                Assert.NotNull(result);
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void Host_WordStart_UsesCoreWordBoundary()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("hello world");
            ctrl.SetSource(buf);
            IUiaTextHost host = ctrl;
            // "hello world" の 3 は "hello" 内 → WordStart=0
            Assert.Equal(0, host.WordStart(3));
            // "hello world" の 9 は "world" 内 → WordStart=6
            Assert.Equal(6, host.WordStart(9));
        });
    }

    /// <summary>
    /// 2026-08-04 F-3: UIA の単語スパンが文字クラス規則(= ダブルクリック単語選択)と一致する。
    /// 修正前は「空白 / CR / LF のみが区切り」だったため、空白の無い日本語行では
    /// 行全体が 1 単語として SR に読まれていた(2026-08-03-uia-word-unit-design.md §2.3)。
    /// </summary>
    /// <remarks>
    /// 期待値は Core の <c>WordBoundaryTests.WordStart_WordEnd_MatchDoubleClickRule</c> と
    /// 同じ規則から採っている。重複ではない — あちらは規則そのものの正しさを、こちらは
    /// 「UIA ホストがその規則へ本当に配線されているか」を固定する。
    ///
    /// <b>「英文は不変」ではない</b>(Task 3 仕様レビュー Important-1)。空白の上・行頭インデント・
    /// EOF 直前では純 ASCII でもスパンが変わる。旧実装は空白位置でその空白 1 個を返していたが、
    /// 新規則は<b>左の単語の頭</b>へ戻る(ダブルクリック単語選択が元からそう動く)。下の
    /// <c>"    hello"</c> / <c>"ab    cd"</c> / <c>"don't stop"</c> がその形を固定する。
    /// 真の対照群(新旧一致)は単一空白の <c>"hello world"</c> pos=5 側。
    ///
    /// 非 BMP は<b>1 行だけ</b>にしてある。xUnit は <c>InlineData</c> に非 BMP を複数並べると
    /// test ID が衝突して黙ってスキップする(このリポジトリの既知の罠)。
    /// </remarks>
    [Theory]
    [InlineData("今日は晴れです。", 0, 0, 2)] // 修正前は [0,8) = 行全体
    [InlineData("今日は晴れです。", 2, 2, 3)]
    [InlineData("abc123def", 4, 3, 6)] // 修正前は [0,9) = 行全体
    [InlineData("foo(bar)=baz;", 0, 0, 3)] // 修正前は [0,13) = 行全体
    [InlineData("foo bar baz", 4, 4, 7)] // 単語の内側: 修正前後で不変
    [InlineData("    hello", 2, 0, 2)] // 行頭インデント: 英文でも変わる(旧は [2,3) = 空白 1 個)
    [InlineData("ab    cd", 4, 0, 4)] // 複数空白 run(旧は [4,5))
    [InlineData("hello world", 5, 0, 5)] // 単一空白 = 真の対照群(新旧一致)
    [InlineData("don't stop", 4, 4, 5)] // 英文 + 記号は意図的に変わる
    [InlineData("a\U0001F600b", 2, 1, 3)] // サロゲート中間 offset(UIA クライアントは任意 offset を渡せる)
    public void Host_WordSpan_UsesCharClassRule(string text, int pos, int start, int end)
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString(text);
            ctrl.SetSource(buf);
            IUiaTextHost host = ctrl;
            Assert.Equal(start, host.WordStart(pos));
            Assert.Equal(end, host.WordEnd(pos));
        });
    }

    /// <summary>
    /// 2026-08-04 F-5: 空白ゼロの単一クラス長大行で、UIA の単語走査が行全体を舐めない。
    /// 修正前は WordStart(500K) が必ず 0 を返し、1 回の読み上げに約 2.8 秒かかっていた
    /// (2026-08-03-uia-word-unit-design.md §2.4)。文字クラス規則(Task 3)は単一クラスの
    /// 長大連続には効かないので、上限が別途要る。
    /// </summary>
    [Fact]
    public void Host_WordSpan_OnHugeSingleClassLine_IsBoundedAndContainsCaret()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString(new string('a', 500_000)));
            IUiaTextHost host = ctrl;
            const int Pos = 250_000;

            int start = host.WordStart(Pos);
            int end = host.WordEnd(Pos);

            Assert.True(start > 0, $"start={start} = 行頭まで走っている(上限が効いていない)");
            Assert.True(end < 500_000, $"end={end} = 行末まで走っている(上限が効いていない)");
            // Task 4 で expand の窓をキャレット中心にしたので、切り詰めてもスパンは pos を含む。
            Assert.True(start <= Pos, $"start={start} が pos を超えている");
            Assert.True(Pos < end, $"end={end} が pos を含んでいない");
            // 窓の幅は code point 数で数える(maxScan の単位。char オフセット差ではない)。
            // 詳細は Host_WordSpan_UnderScanLimit_AlwaysContainsCaret の remarks を参照。
            int width = CodePointCount(host, start, end);
            Assert.True(
                width <= 2 * WordBoundary.DefaultMaxScan,
                $"窓が広すぎる: [{start}, {end}) = {width} code point (cap={WordBoundary.DefaultMaxScan})"
            );
        });
    }

    /// <summary>移動側(UIA Move / Ctrl+←→)にも同じ上限が効いていること。</summary>
    /// <remarks>
    /// 厳密比較にしてあるのは、<b>SR 経路だけ別の cap にする改変</b>を殺すため
    /// (不等号だと <c>cap / 4</c> のような縮小変異が素通りし、それは「同じ位置で見える選択と
    /// 聞くスパンが違う」= F-3 の再導入にあたる)。予算の off-by-one も同時に殺す。
    ///
    /// <c>PrevWordStart</c> の期待値に <c>- 1</c> の緩みは<b>要らない</b>。窓が
    /// <c>[caret - maxScan, caret]</c> ちょうどになるのは、手順 2 の最初の 1 歩
    /// (caret から左へ 1)を<b>予算に数えるから</b>である(数えない実装なら 1 広がる)。
    /// <c>WordBoundary</c> の xmldoc の窓の表と一致する。
    /// </remarks>
    [Fact]
    public void Host_WordNavigation_OnHugeSingleClassLine_IsBounded()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString(new string('a', 500_000)));
            IUiaTextHost host = ctrl;

            Assert.Equal(WordBoundary.DefaultMaxScan, host.NextWordStart(0));
            Assert.Equal(500_000 - WordBoundary.DefaultMaxScan, host.PrevWordStart(500_000));
        });
    }

    /// <summary>
    /// 2026-08-04 F-5 リスク R2 の網: 上限が実際に噛む長さの行で、<b>全 pos</b> について
    /// <c>WordStart(pos) &lt;= pos &lt;= WordEnd(pos)</c>(= 反転レンジなし・窓はキャレットを含む)。
    /// </summary>
    /// <remarks>
    /// <c>UiaWordUnitExpandTests.ExpandToEnclosingUnit_Word_NeverProducesReversedRange</c> は
    /// 同じ不変条件を Provider 経由で総当りするが、fixture が短く<b>上限に一度も当たらない</b>。
    /// こちらは cap を確実に越える run を混ぜて「切り詰めが不変条件を壊さない」ことを見る。
    /// 空白 run / 改行 / <b>非 BMP の長い run</b> を含めるのは、末尾空白の巻き戻し(<c>WordEnd</c>)と
    /// サロゲート歩進が切り詰めと同時に効く位置を通すため。
    ///
    /// <b>窓の幅は code point 数で数える</b>(<c>maxScan</c> の単位。正本 =
    /// <c>WordBoundary</c> クラスの <c>&lt;remarks&gt;</c>「窓についてよくある誤読」(1))。
    /// 実測(<b>cap 確定前の暫定値 256 で採取</b>= fixture が cap 比例なので現在の 128 では
    /// 位置も幅も半分になる): 絵文字 run の中央で <c>[1490, 2512)</c> = 1022 char =
    /// 511 code point = <c>2 * cap - 1</c>。ここで char 差の <c>&lt;= 4 * cap</c> へ
    /// 緩めると<b>予算そのものの倍化(ASCII で <c>4 * cap - 1</c>)を素通しする</b>ため、
    /// 単位を揃えて締める方を選んだ。
    /// </remarks>
    [Fact]
    public void Host_WordSpan_UnderScanLimit_AlwaysContainsCaret()
    {
        Sta.Run(() =>
        {
            int cap = WordBoundary.DefaultMaxScan;
            string text =
                new string('a', cap * 2)
                + new string(' ', cap + 5)
                + new string('あ', cap * 2)
                + "\r\n"
                + new string('1', cap + 3)
                + string.Concat(Enumerable.Repeat("\U0001F600", cap * 2))
                + new string('\t', cap + 1)
                + "end";
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString(text));
            IUiaTextHost host = ctrl;

            for (int pos = 0; pos <= host.TextLength; pos++)
            {
                int start = host.WordStart(pos);
                int end = host.WordEnd(pos);
                Assert.True(start <= pos, $"pos={pos}: start={start} が pos を超えた");
                Assert.True(end >= pos, $"pos={pos}: end={end} = 反転レンジ");
                int width = CodePointCount(host, start, end);
                Assert.True(
                    width <= 2 * cap,
                    $"pos={pos}: 窓が広すぎる [{start}, {end}) = {width} code point (cap={cap})"
                );
            }
        });
    }

    /// <summary>
    /// <c>[start, end)</c> の code point 数 = <c>maxScan</c> の単位。
    /// 端がサロゲートペアの中間に落ちた場合、その孤立サロゲートは 1 と数える(実際の
    /// code point 数以下になる=上限 assert としては安全側)。
    /// </summary>
    private static int CodePointCount(IUiaTextHost host, int start, int end)
    {
        string s = host.GetTextRange(start, end - start);
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                i++;
            n++;
        }
        return n;
    }

    [Fact]
    public void Host_ControlTypeId_IsDocument()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            IUiaTextHost host = ctrl;
            Assert.Equal(System.Windows.Automation.ControlType.Document.Id, host.ControlTypeId);
        });
    }

    [Fact]
    public void Host_AutomationId_Editor()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            IUiaTextHost host = ctrl;
            Assert.Equal("editor", host.AutomationId);
        });
    }

    [Fact]
    public void Host_Name_Honmon()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            IUiaTextHost host = ctrl;
            Assert.Equal("本文", host.Name);
        });
    }

    [Fact]
    public void Host_SetSelection_MarshalsToUIThread()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("abcdefg");
            ctrl.SetSource(buf);
            using var form = new Form();
            form.Controls.Add(ctrl);
            form.Show();
            try
            {
                IUiaTextHost host = ctrl;
                host.SetSelection(1, 4);
                Application.DoEvents(); // BeginInvoke を回す
                Assert.Equal((1, 4), host.GetSelection());
            }
            finally
            {
                form.Close();
            }
        });
    }
}
