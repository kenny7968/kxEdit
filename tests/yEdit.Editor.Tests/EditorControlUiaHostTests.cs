using System;
using System.Windows.Forms;
using Xunit;
using yEdit.Accessibility;
using yEdit.Core.Buffers;
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
