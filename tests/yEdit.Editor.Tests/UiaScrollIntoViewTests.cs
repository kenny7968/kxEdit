using System.Linq;
using yEdit.Accessibility;

namespace yEdit.Editor.Tests;

/// <summary>
/// UIA ScrollIntoView(= EditorControl.ScrollCharRangeIntoView)の契約テスト。
///
/// 契約:
/// - 対象行(alignToTop なら範囲先頭・そうでなければ範囲末尾)が既に可視なら TopLine を動かさない
/// - 非可視なら alignToTop に従って TopLine を上端 / 下端に合わせる
/// - 選択・キャレットは変更しない(装飾スクロール)
/// - SetSource 前 / Handle 未生成 / 破棄後は no-op(throw しない)
///
/// LineHeightPx はフォント依存のため、可視行数はテスト側でも
/// ClientSize.Height / LineHeightPx で算出して相対比較する
/// (CaretScrollTests と同じ流儀)。長い行を置かないので hscroll は表示されず、
/// 実装側の paintHeight == ClientSize.Height になる。
/// </summary>
public class UiaScrollIntoViewTests
{
    private const int LineCount = 30;

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
    public void ScrollCharRangeIntoView_KeepsTopLine_WhenTargetAlreadyVisible() =>
        Sta.Run(() =>
        {
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                // 非既定位置から開始する(既定 0 のままだと「動かなかった」と
                // 「そもそも 0 だった」を区別できない = CLAUDE.md §4 の教訓)。
                c.TopLine = 5;
                int target = 5 + VisibleRows(c) / 2; // 可視域の中ほど
                int off = LineStartOffset(text, target);

                c.ScrollCharRangeIntoView(off, off, alignToTop: true);
                Assert.Equal(5, c.TopLine);

                c.ScrollCharRangeIntoView(off, off, alignToTop: false);
                Assert.Equal(5, c.TopLine);
            }
        });

    [Fact]
    public void ScrollCharRangeIntoView_AlignsToTop_WhenTargetAboveViewport() =>
        Sta.Run(() =>
        {
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                c.TopLine = 20;
                int off = LineStartOffset(text, 3);

                c.ScrollCharRangeIntoView(off, off, alignToTop: true);

                Assert.Equal(3, c.TopLine);
            }
        });

    [Fact]
    public void ScrollCharRangeIntoView_AlignsToTop_WhenTargetBelowViewport() =>
        Sta.Run(() =>
        {
            // 案 1(EnsureVisibleCharRange への薄い委譲)との差分をここで固定する。
            // 案 1 だと対象行は「最下行」に来るため TopLine == 25 にはならない。
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                c.TopLine = 0;
                int off = LineStartOffset(text, 25);

                c.ScrollCharRangeIntoView(off, off, alignToTop: true);

                Assert.Equal(25, c.TopLine);
            }
        });

    [Fact]
    public void ScrollCharRangeIntoView_AlignsToBottom_WhenAlignToTopIsFalse() =>
        Sta.Run(() =>
        {
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                c.TopLine = 0;
                int expected = 25 - VisibleRows(c) + 1;
                int off = LineStartOffset(text, 25);

                c.ScrollCharRangeIntoView(off, off, alignToTop: false);

                Assert.Equal(expected, c.TopLine);
            }
        });

    [Fact]
    public void ScrollCharRangeIntoView_UsesRangeStartOrEnd_PerAlignToTop() =>
        Sta.Run(() =>
        {
            // start は可視域内・end は遥か下。alignToTop の値で対象端点が切り替わることを固定する
            // (両方 start を見る / 両方 end を見る実装をここで殺す)。
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                int start = LineStartOffset(text, 1);
                int end = LineStartOffset(text, 25);

                c.TopLine = 0;
                c.ScrollCharRangeIntoView(start, end, alignToTop: false);
                Assert.Equal(25 - VisibleRows(c) + 1, c.TopLine); // end を見た

                c.TopLine = 0;
                c.ScrollCharRangeIntoView(start, end, alignToTop: true);
                Assert.Equal(0, c.TopLine); // start(行 1)は既に可視 → 動かない
            }
        });

    [Fact]
    public void ScrollCharRangeIntoView_PreservesCaretAndAnchor() =>
        Sta.Run(() =>
        {
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                c.TopLine = 0;
                c.SetSelectionCharRange(2, 5);
                var (start0, end0) = c.GetSelectionCharRange();

                int off = LineStartOffset(text, 25);
                c.ScrollCharRangeIntoView(off, off, alignToTop: false);

                var (start1, end1) = c.GetSelectionCharRange();
                Assert.Equal(start0, start1);
                Assert.Equal(end0, end1);
                Assert.True(c.TopLine > 0, "スクロールしていないと本テストは無意味");
            }
        });

    [Fact]
    public void ScrollCharRangeIntoView_AlignsToTop_WhenTargetIsFirstRowBelowViewport() =>
        Sta.Run(() =>
        {
            // 上端境界(Nit-2): line == TopLine + visibleRows = 可視域の「直下 1 行目」。
            // guard の `>=` を `>` に緩めると発火せず、後段の EnsureVisibleCharRange が
            // 下端整列にしてしまう(上端整列にならない)=観測可能な差をここで固定する。
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                c.TopLine = 5; // 非既定位置から開始する
                int target = 5 + VisibleRows(c); // 可視域 [5, 5+VisibleRows) の直下 1 行目
                int off = LineStartOffset(text, target);

                c.ScrollCharRangeIntoView(off, off, alignToTop: true);

                Assert.Equal(target, c.TopLine);
            }
        });

    [Fact]
    public void ScrollCharRangeIntoView_KeepsTopLine_WhenTargetIsTopRow_AndAlignToBottom() =>
        Sta.Run(() =>
        {
            // 下端境界(Nit-3): line == TopLine(可視域の先頭行そのもの)かつ alignToTop=false。
            // guard の `<` を `<=` に緩めると、既に可視な行をわざわざ下端へ送る
            // = 案 3(常に整列)と同じ「視界の揺れ」が起きる。本設計が明示的に避けている挙動。
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                c.TopLine = 5; // 非既定位置。既定 0 だと下端整列しても 0 にクランプされ差が出ない
                int off = LineStartOffset(text, 5); // ちょうど TopLine の行

                c.ScrollCharRangeIntoView(off, off, alignToTop: false);

                Assert.Equal(5, c.TopLine);
            }
        });

    [Fact]
    public void ScrollCharRangeIntoView_AlignsToBottom_WhenTargetAboveViewport() =>
        Sta.Run(() =>
        {
            // Nit-4 関連: alignToTop=false かつ対象が viewport より「上」。
            // TopLine 代入を落とすと BringCaretIntoView が line < TopLine を見て
            // 上端整列(TopLine == line)にしてしまう=下端整列であることをここで固定する。
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                c.TopLine = 20;
                int expected = 10 - VisibleRows(c) + 1;
                // 期待値が 0 に潰れると ClampTopLine の 0 クランプと区別できず無意味になる。
                Assert.True(expected > 0, $"fixture broken: expected={expected} (must be > 0)");

                int off = LineStartOffset(text, 10);
                c.ScrollCharRangeIntoView(off, off, alignToTop: false);

                Assert.Equal(expected, c.TopLine);
            }
        });

    [Fact]
    public void ScrollCharRangeIntoView_ScrollsHorizontally_WhenTargetOffScreenRight() =>
        Sta.Run(() =>
        {
            // Nit-1: 水平方向の脚(EnsureVisibleCharRange への委譲)の被覆。
            // 他のテストは短い行のみで hscroll が出ないため、この行を消しても全緑だった。
            //
            // フィクスチャは CaretScrollTests.BringCaretIntoView_ScrollsDown_WhenCaretHiddenByHScrollBar
            // に倣う: 長文行を line 0 に置く(UpdateHorizontalScrollbar は _topLine から
            // 走査するため、長文行が TopLine=0 の viewport 内にないと hscroll が表示されない)。
            // f.Show() + f.ClientSize 強制はレイアウト確定のため(unshown だと子への伝播が不完全)。
            //
            // 対象は line 0 の右端付近 = 垂直方向は既に可視。それでも水平は追従する、が契約。
            var text = new string('x', 200) + "\nl1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9";
            using var f = new Form { Size = new System.Drawing.Size(200, 200) };
            var c = new EditorControl { Dock = DockStyle.Fill };
            f.Controls.Add(c);
            f.Show();
            c.SetSource(TextBuffer.FromString(text));

            try
            {
                int lh = c.LineHeightPx;
                f.ClientSize = new System.Drawing.Size(100, 3 * lh); // 200 char の長文行より狭い
                f.PerformLayout();

                c.WrapColumns = 0; // 折り返し OFF(水平スクロールの前提)
                c.TopLine = 0;

                Assert.True(c.ScrollX == 0, "fixture broken: ScrollX は 0 から始まるはず");

                int off = 195; // line 0 の右端付近(可視域の右外)
                c.ScrollCharRangeIntoView(off, off, alignToTop: true);

                Assert.Equal(0, c.TopLine); // 垂直は動かない(line 0 は既に可視)
                Assert.True(
                    c.ScrollX > 0,
                    $"水平スクロールが追従していない (ScrollX={c.ScrollX}, "
                        + $"ClientSize={c.ClientSize}, LineHeightPx={lh})"
                );
            }
            finally
            {
                c.Dispose();
                f.Close();
            }
        });

    [Fact]
    public void ScrollCharRangeIntoView_NoOp_BeforeSetSource() =>
        Sta.Run(() =>
        {
            using var f = new Form();
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;

            Assert.Null(Record.Exception(() => c.ScrollCharRangeIntoView(0, 10, alignToTop: true)));
            Assert.Equal(0, c.TopLine);
        });

    [Fact]
    public void ScrollRangeIntoView_NoThrow_WhenHandleNotCreated() =>
        Sta.Run(() =>
        {
            using var c = new EditorControl(); // Handle 未生成
            Assert.Null(
                Record.Exception(() =>
                    ((IUiaTextHost)c).ScrollRangeIntoView(0, 5, alignToTop: true)
                )
            );
        });

    [Fact]
    public void ScrollRangeIntoView_NoThrow_AfterDispose() =>
        Sta.Run(() =>
        {
            var f = new Form { Size = new System.Drawing.Size(400, 120) };
            var c = new EditorControl { Dock = DockStyle.Fill };
            f.Controls.Add(c);
            _ = f.Handle;
            c.SetSource(TextBuffer.FromString(MakeText()));
            c.Dispose();
            f.Dispose();

            Assert.Null(
                Record.Exception(() =>
                    ((IUiaTextHost)c).ScrollRangeIntoView(0, 5, alignToTop: true)
                )
            );
        });
}
