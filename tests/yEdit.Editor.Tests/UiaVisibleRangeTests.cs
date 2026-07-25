using System.Linq;
using yEdit.Accessibility;

namespace yEdit.Editor.Tests;

/// <summary>
/// UIA GetVisibleRanges(= EditorControl.GetVisibleCharRange / IUiaTextHost.GetVisibleRange)の
/// 契約テスト。
///
/// 契約:
/// - 現在ビューポートに見えている本文の範囲 [Start, End) を返す(描画と同じ ViewportLayout.Build)
/// - TopLine に追従する。文書全体ではない(旧実装は常に (0, TextLength) だった)
/// - 空文書 / SetSource 前 / Handle 未生成は (0, 0)
/// - RPC スレッドから呼ばれたら同期 Invoke で UI スレッドへマーシャリングする
///
/// UiaScrollIntoViewTests とはヘルパ(MakeControl / MakeText / LineStartOffset)が重複するが、
/// 本ファイルは単独 revert できることを優先して意図的に複製している
/// (GetVisibleRanges の可視域化は NVDA の通し読み範囲を縮めるリスクがあり、L5 で NG なら
///  この 1 commit だけを revert する = revert 可能性 &gt; DRY)。
/// </summary>
public class UiaVisibleRangeTests
{
    private const int LineCount = 30;

    /// <summary>Handle だけ生成し <c>Show()</c> はしない既定フィクスチャ(短い行のみ=hscroll は出ない)。</summary>
    private static (Form f, EditorControl c) MakeControl(string text, int width, int height)
    {
        var f = new Form { Size = new System.Drawing.Size(width, height) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    private static string MakeText() =>
        string.Join("\n", Enumerable.Range(0, LineCount).Select(i => $"line{i}"));

    private static int LineStartOffset(string text, int line)
    {
        int pos = 0;
        for (int i = 0; i < line; i++)
            pos = text.IndexOf('\n', pos) + 1;
        return pos;
    }

    private static int VisibleRows(EditorControl c) =>
        Math.Max(1, c.ClientSize.Height / Math.Max(1, c.LineHeightPx));

    [Fact]
    public void GetVisibleCharRange_FollowsTopLine() =>
        Sta.Run(() =>
        {
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                c.TopLine = 0;
                var (start0, end0) = c.GetVisibleCharRange();
                Assert.Equal(0, start0);

                c.TopLine = 10;
                var (start1, end1) = c.GetVisibleCharRange();

                Assert.Equal(LineStartOffset(text, 10), start1);
                Assert.True(start1 > start0, $"Start が追従していない ({start0} -> {start1})");
                Assert.True(end1 > end0, $"End が追従していない ({end0} -> {end1})");

                // End は可視最終行の「行末」(改行の手前)。行頭(=直前が改行)を指す実装は
                // 最終行の SegmentLength を足しそこねており、可視最終行が丸ごと欠ける。
                // 行数に依存しない不変条件なので、フォント依存の可視行数に左右されない。
                Assert.True(
                    end0 > 0 && text[end0 - 1] != '\n',
                    $"End が行頭を指している (end={end0})"
                );
                Assert.True(text[end1 - 1] != '\n', $"End が行頭を指している (end={end1})");

                // 可視域は「viewport 全体」であって 1 行ではない = Task 2 の存在意義そのもの。
                // last を rows[0] にする実装(=先頭行だけ返す)をここで殺す。壊れると
                // 「NVDA に見えている範囲が 1 行だけ」という形で出て、設計書 §5.3 の
                // L5 リスク判定(通し読みが縮んだ原因)を誤らせる。
                // VisibleRows は floor・実装は ceil なので実装の方が広い = 不等号で比較する
                // (厳密等値にすると端数高さで脆くなる)。
                int lastFloorLine = 10 + VisibleRows(c) - 1;
                Assert.True(
                    end1 >= LineStartOffset(text, lastFloorLine),
                    $"可視域が最終可視行に届いていない (end={end1}, lastFloorLine={lastFloorLine})"
                );
            }
        });

    [Fact]
    public void GetVisibleCharRange_IsNotWholeDocument_WhenScrolled() =>
        Sta.Run(() =>
        {
            // 旧実装 (0, TextLength) の退行を殺す。Start > 0 かつ End < TextLength の
            // 両方が要る(片方だけだと「先頭だけ切った」実装を見逃す)。
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                // フィクスチャ前提: 可視行数が残り行数より少ない=末尾に届かないこと。
                Assert.True(
                    10 + VisibleRows(c) < LineCount,
                    $"fixture broken: VisibleRows={VisibleRows(c)} (10 + それ < {LineCount} が必要)"
                );

                c.TopLine = 10;
                var (start, end) = c.GetVisibleCharRange();

                Assert.True(start > 0, $"Start が 0 のまま (start={start})");
                Assert.True(
                    end < text.Length,
                    $"End が文書末尾のまま (end={end}, TextLength={text.Length})"
                );
                Assert.True(start < end, $"範囲が空 (start={start}, end={end})");
            }
        });

    [Fact]
    public void GetVisibleCharRange_UsesVisualRowBoundary_WhenWrapOn() =>
        Sta.Run(() =>
        {
            // 折り返し ON では視覚行境界になる。1 論理行しか無いので、論理行単位の実装なら
            // End == text.Length になる=ここで区別できる。
            var text = new string('x', 200);
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                c.WrapColumns = 10; // 200 文字を多数の視覚行に割る
                c.TopLine = 0;

                var (start, end) = c.GetVisibleCharRange();

                Assert.Equal(0, start);
                Assert.True(end > 0, "可視行がゼロになっている(フィクスチャ破損)");
                Assert.True(
                    end < text.Length,
                    $"視覚行境界ではなく論理行末を返している (end={end}, TextLength={text.Length})"
                );
            }
        });

    [Fact]
    public void GetVisibleCharRange_ReturnsZero_ForEmptyDocument() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("", width: 400, height: 120);
            using (f)
            using (c)
            {
                Assert.Equal((0, 0), c.GetVisibleCharRange());
            }
        });

    [Fact]
    public void GetVisibleCharRange_ReturnsZero_WhenPaintHeightIsZero() =>
        Sta.Run(() =>
        {
            // rows.Count == 0 ガードの被覆。高さ 0 なら PaintHeightPx == 0 →
            // ViewportLayout.Build は heightPx <= 0 で空リストを返す。ガードが無いと
            // rows[0] が ArgumentOutOfRangeException を投げる。
            //
            // この経路は Adapter の同期 Invoke の中で走る。Adapter が catch しているのは
            // ObjectDisposedException / InvalidOperationException だけなので、
            // ArgumentOutOfRangeException は RPC スレッド経由で UIA の COM 境界へ抜ける。
            using var c = new EditorControl { Size = new System.Drawing.Size(400, 0) };
            c.SetSource(TextBuffer.FromString("a\nb\nc"));
            Assert.Equal((0, 0), c.GetVisibleCharRange());
        });

    [Fact]
    public void GetVisibleCharRange_ReturnsZero_BeforeSetSource() =>
        Sta.Run(() =>
        {
            using var f = new Form { Size = new System.Drawing.Size(400, 120) };
            using var c = new EditorControl { Dock = DockStyle.Fill };
            f.Controls.Add(c);
            _ = f.Handle;

            Assert.Equal((0, 0), c.GetVisibleCharRange());
        });

    [Fact]
    public void GetVisibleRange_ReturnsZero_WhenHandleNotCreated() =>
        Sta.Run(() =>
        {
            // Adapter の `!IsHandleCreated` ガードの被覆。Control.InvokeRequired は Handle 未生成時に
            // false を返すため、ガードが無いと RPC スレッドがそのまま UI スレッド専有状態
            // (_topLine / _metrics / ClientSize) に触れる = CLAUDE.md §2 の a11y 鉄則違反。
            //
            // フィクスチャ注記: SetSource 自体が Handle を生成する(実測)。「Handle 未生成 かつ
            // バッファあり」は SetSource 後に Handle だけ落として作る。バッファが無いと
            // GetVisibleCharRange が _buffer null で (0,0) を返し、ガードを外しても観測差が
            // 出ない=変異を殺せない。DestroyHandle は protected なので reflection で呼ぶ
            // (UiaScrollIntoViewTests / CaretScrollTests と同じ流儀)。
            var text = MakeText();
            using var c = new EditorControl { Size = new System.Drawing.Size(400, 120) };
            c.SetSource(TextBuffer.FromString(text));
            c.TopLine = 7; // 非既定位置=Start が 0 にならない状態を作る

            IUiaTextHost host = c;
            Assert.NotEqual((0, 0), host.GetVisibleRange()); // fixture sanity: 破棄前は実範囲を返す

            typeof(Control)
                .GetMethod(
                    "DestroyHandle",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                )!
                .Invoke(c, null);

            Assert.False(c.IsHandleCreated, "fixture broken: Handle が生成されたまま");
            Assert.Equal(7, c.TopLine); // Handle 破棄で TopLine が動いていないこと

            Assert.Equal((0, 0), host.GetVisibleRange());
        });

    [Fact]
    public void GetVisibleRange_CalledFromNonUiThread_MarshalsSafely() =>
        Sta.Run(() =>
        {
            // Task 1a/1b レビューの教訓: UI スレッド上のテストだけでは InvokeRequired == false の
            // ため Invoke マーシャリング分岐に一度も入らない。同期 Invoke 側も同じ穴が空くので
            // 実際に別スレッドから呼ぶ(型は
            // EditorControlUiaHostTests.Host_LineStartOf_WithWrap_CalledFromNonUiThread_MarshalsSafely に倣う)。
            var text = MakeText();
            using var f = new Form { Size = new System.Drawing.Size(400, 120) };
            var c = new EditorControl { Dock = DockStyle.Fill };
            f.Controls.Add(c);
            f.Show(); // Handle 生成 + レイアウト確定(InvokeRequired を true にするため必須)
            try
            {
                c.SetSource(TextBuffer.FromString(text));
                c.TopLine = 10; // 非既定位置(既定 0 だと (0, *) と区別しにくい)
                IUiaTextHost host = c;

                var expected = c.GetVisibleCharRange(); // UI スレッド上の値
                Assert.True(expected.Start > 0, $"fixture broken: Start={expected.Start}");

                (int Start, int End)? actual = null;
                Exception? ex = null;
                var task = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        actual = host.GetVisibleRange();
                    }
                    catch (Exception e)
                    {
                        ex = e;
                    }
                });

                // UI スレッドで DoEvents ループを回して同期 Invoke を進行させる
                // (回さないと Invoke が完了せず、RPC スレッド側が待ち続ける)。
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!task.IsCompleted && sw.ElapsedMilliseconds < 3000)
                    Application.DoEvents();
                task.Wait(1000);

                Assert.True(
                    task.IsCompleted,
                    "cross-thread host call must complete without deadlock"
                );
                Assert.Null(ex);
                Assert.Equal(expected, actual);
            }
            finally
            {
                c.Dispose();
                f.Close();
            }
        });
}
