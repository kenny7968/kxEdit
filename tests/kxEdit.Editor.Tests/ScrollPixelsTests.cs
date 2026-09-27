using System.Drawing;
using kxEdit.Editor.Tests.Fakes;

namespace kxEdit.Editor.Tests;

/// <summary>
/// フェーズ 9b(設計書 §14.2・計画 §0.2): スクロールのセッターは、画素を移して露出した帯と変わった行だけを無効化する。
/// 画素の移動は偽の画面(<see cref="ScreenSurface"/>)で受ける。
/// </summary>
public class ScrollPixelsTests
{
    private static string Body() =>
        string.Join(
            "\r\n",
            Enumerable
                .Range(0, 80)
                .Select(i => i == 3 ? new string('w', 300) : $"line {i:D2} あいう abc")
        );

    private static (Form F, EditorControl C, ScreenSurface S) MakeHosted()
    {
        var f = new Form { Size = new Size(400, 260) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(Body()));
        var s = new ScreenSurface(c.ClientSize.Width, c.ClientSize.Height);
        EditorControl.TestHook_SetPaintSurface(c, s);
        return (f, c, s);
    }

    private static void Paint(EditorControl c) =>
        EditorControl.TestHook_PaintToBitmap(c, record: true).Dispose();

    private static List<Rectangle> Rects(EditorControl c, Action act)
    {
        var rects = new List<Rectangle>();
        InvalidateEventHandler h = (_, e) => rects.Add(e.InvalidRect);
        c.Invalidated += h;
        try
        {
            act();
        }
        finally
        {
            c.Invalidated -= h;
        }
        return rects;
    }

    [Fact]
    public void One_line_down_scrolls_pixels_and_invalidates_only_the_exposed_band() =>
        Sta.Run(() =>
        {
            var (f, c, s) = MakeHosted();
            using (f)
            {
                c.TopLine = 5;
                Paint(c);
                int lh = c.Metrics.LineHeightPx;
                var rects = Rects(c, () => c.TopLine = 6);
                Assert.Equal(1, s.Scrolls);
                Assert.DoesNotContain(rects, r => r.Contains(c.ClientRectangle));
                // 露出した帯(1 行)と、途中で切れる新しい最下行の帯だけ = 高さは 2 行ぶん以下。
                var union = rects.Aggregate(Rectangle.Union);
                Assert.True(union.Height <= 2 * lh, $"無効化の和 {union} が 2 行ぶんを超える");
            }
        });

    [Fact]
    public void A_page_or_more_invalidates_everything_without_scrolling() =>
        Sta.Run(() =>
        {
            var (f, c, s) = MakeHosted();
            using (f)
            {
                c.TopLine = 5;
                Paint(c);
                var rects = Rects(c, () => c.TopLine = 40);
                Assert.Equal(0, s.Scrolls);
                Assert.Contains(rects, r => r.Contains(c.ClientRectangle));
            }
        });

    /// <summary>
    /// Fix round 2(S6b の退行): 移動量がちょうど可視行数(= PaintHeight / 行高、切り捨て)のとき、
    /// 「移動量(行) × 行高 &lt; PaintHeight」だった旧判定は最後の部分行の余白で誤って画素スクロールを
    /// 受理していた(設計書 §14.2「可視行数未満」に反する)。PageUp/PageDown 相当のこの移動量は全面にする。
    /// 非既定の TopLine(5)から始める(CLAUDE.md §4-B)。
    /// </summary>
    [Fact]
    public void Exactly_full_visible_rows_invalidates_everything_without_scrolling() =>
        Sta.Run(() =>
        {
            var (f, c, s) = MakeHosted();
            using (f)
            {
                c.TopLine = 5;
                Paint(c);
                int lh = c.Metrics.LineHeightPx;
                int fullRows = c.ClientSize.Height / lh;
                var rects = Rects(c, () => c.TopLine = 5 + fullRows);
                Assert.Equal(5 + fullRows, c.TopLine); // 前提: クランプされずに実際に動いた
                Assert.Equal(0, s.Scrolls);
                Assert.Contains(rects, r => r.Contains(c.ClientRectangle));
            }
        });

    /// <summary>可視行数 - 1 の移動は、これまでどおり画素スクロールする(境界の反対側)。</summary>
    [Fact]
    public void FullRows_minus_one_still_scrolls_pixels() =>
        Sta.Run(() =>
        {
            var (f, c, s) = MakeHosted();
            using (f)
            {
                c.TopLine = 5;
                Paint(c);
                int lh = c.Metrics.LineHeightPx;
                int fullRows = c.ClientSize.Height / lh;
                var rects = Rects(c, () => c.TopLine = 5 + fullRows - 1);
                Assert.Equal(5 + fullRows - 1, c.TopLine); // 前提: クランプされずに実際に動いた
                Assert.Equal(1, s.Scrolls);
                Assert.DoesNotContain(rects, r => r.Contains(c.ClientRectangle));
            }
        });

    [Fact]
    public void A_pending_update_prevents_scrolling() =>
        Sta.Run(() =>
        {
            var (f, c, s) = MakeHosted();
            using (f)
            {
                c.TopLine = 5;
                Paint(c);
                s.Pending = true;
                var rects = Rects(c, () => c.TopLine = 6);
                Assert.Equal(0, s.Scrolls);
                Assert.Contains(rects, r => r.Contains(c.ClientRectangle));
            }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_active_composition_prevents_scrolling(bool composing) =>
        Sta.Run(() =>
        {
            var (f, c, s) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(c.CurrentBuffer.Current.GetLineStart(8));
                if (composing)
                    c.__TestApplyComposition("か", 1, [0], []);
                c.TopLine = 5;
                Paint(c);
                // 前提: guard の発火条件(未確定の有無)が、スクロールの時点でも成り立っている(CLAUDE.md §4-B)。
                Assert.Equal(composing, c.__TestIsComposing());
                c.TopLine = 6;
                // 対照(未確定なし)は同じ配置で画素を移す = 0 回は未確定のせいである。
                Assert.Equal(composing ? 0 : 1, s.Scrolls);
            }
        });

    /// <summary>
    /// 横の画素の移動: 偽の画面に無効化した矩形を合成し、今の状態の絵と画素で一致すること(移動の向き・露出した帯)。
    /// 行番号(一緒にシフトされる)・現在行強調・選択を含む構成でも確かめる。
    /// 選択がある間は現在行強調が効かない(CaptureFrameInputs)ので、強調は選択なしの構成で見る。
    /// </summary>
    [Theory]
    [InlineData(40, 60, false, false, false)]
    [InlineData(60, 35, false, false, false)]
    [InlineData(40, 60, true, true, true)]
    [InlineData(60, 35, true, true, true)]
    [InlineData(40, 60, true, true, false)]
    [InlineData(60, 35, true, true, false)]
    public void Horizontal_scroll_moves_pixels_sideways(
        int from,
        int to,
        bool lineNumbers,
        bool highlight,
        bool selection
    ) =>
        Sta.Run(() =>
        {
            var (f, c, s) = MakeHosted();
            using (f)
            {
                // 未 Show の Form では _hscroll.Visible が常に false で ScrollX のセッターが no-op になる
                // (ClipPaintTests の同種コメント参照)。描画は Bitmap への TestHook_PaintToBitmap なので、
                // 表示しても比較には影響しない。
                f.Show();
                var snap = c.CurrentBuffer.Current;
                c.ShowLineNumbers = lineNumbers;
                c.HighlightCurrentLine = highlight;
                // キャレットは行頭近く(ScrollX = 0 で見えている = 追従の横スクロールは起きない)。
                if (selection)
                    c.SetSelectionAnchored(snap.GetLineStart(3) + 5, snap.GetLineStart(5) + 2);
                else
                    c.SetCaretCharOffset(snap.GetLineStart(2));
                c.ScrollX = from;
                Assert.Equal(from, c.ScrollX); // 前提: hscroll が表示されている(行 3 が長い)
                s.Pixels = SkipInvalidateOracleTests.Paint(c, record: true);
                var rects = Rects(c, () => c.ScrollX = to);
                Assert.Equal(to, c.ScrollX); // 前提
                Assert.Equal(1, s.Scrolls);
                Assert.DoesNotContain(rects, r => r.Contains(c.ClientRectangle));
                SkipInvalidateOracleTests.Composite(c, s.Pixels, rects);
                var truth = SkipInvalidateOracleTests.Paint(c, record: false);
                var diff = SkipInvalidateOracleTests.DiffBounds(c, s.Pixels, truth);
                Assert.True(diff.IsEmpty, $"古い絵が残る(差の外接矩形 {diff})");
            }
        });

    /// <summary>
    /// Task 4 レビュー由来(Review Focus 1 のスクロールを伴う版): EnsureVisibleCharRange は一時的にキャレットを動かし、
    /// 追従スクロールのセッターは画面の絵の入力を一時的な状態で記録する(画素を移す)。戻した後の画面が
    /// 今の状態の絵と一致すること(古い現在行が残らないこと)を、偽の画面に無効化した矩形を合成して確かめる。
    /// これはエンドツーエンドの性質テストであり、<c>EnsureVisibleCharRange</c> の finally 内にある
    /// 特定の <c>InvalidateChangedRows()</c> 呼び出し 1 つを単体で保証するものではない
    /// (その呼び出しが load-bearing かどうかは <c>EnsureVisibleCharRange</c> 側のコメントを参照)。
    /// </summary>
    [Fact]
    public void EnsureVisibleCharRange_that_scrolls_leaves_no_stale_row() =>
        Sta.Run(() =>
        {
            var (f, c, s) = MakeHosted();
            using (f)
            {
                c.HighlightCurrentLine = true;
                c.TopLine = 2;
                c.SetCaretCharOffset(c.CurrentBuffer.Current.GetLineStart(6) + 1); // 可視域の中
                s.Pixels = SkipInvalidateOracleTests.Paint(c, record: true);
                int topBefore = c.TopLine;
                int visible = c.ClientSize.Height / c.Metrics.LineHeightPx;
                // 可視域の少し下(数行ぶんのスクロールで入る)。
                int target = c.CurrentBuffer.Current.GetLineStart(topBefore + visible + 1);
                var rects = Rects(c, () => c.EnsureVisibleCharRange(target, 0));
                Assert.Equal(1, s.Scrolls); // 前提: 画素を移した
                Assert.InRange(c.TopLine - topBefore, 1, 4); // 前提: 小さなスクロール
                Assert.Equal(c.CurrentBuffer.Current.GetLineStart(6) + 1, c.CaretCharOffset); // 前提: キャレットは戻っている
                SkipInvalidateOracleTests.Composite(c, s.Pixels, rects);
                var truth = SkipInvalidateOracleTests.Paint(c, record: false);
                var diff = SkipInvalidateOracleTests.DiffBounds(c, s.Pixels, truth);
                Assert.True(diff.IsEmpty, $"古い絵が残る(差の外接矩形 {diff})");
            }
        });

    [Fact]
    public void Scrolling_without_a_recorded_frame_invalidates_everything() =>
        Sta.Run(() =>
        {
            var (f, c, s) = MakeHosted();
            using (f)
            {
                Assert.False(EditorControl.TestHook_HasLastPaintedInputs(c)); // 前提
                var rects = Rects(c, () => c.TopLine = 1);
                Assert.Equal(0, s.Scrolls);
                Assert.Contains(rects, r => r.Contains(c.ClientRectangle));
            }
        });
}
