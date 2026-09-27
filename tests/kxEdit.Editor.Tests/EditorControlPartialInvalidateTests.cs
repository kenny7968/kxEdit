using System.Drawing;

namespace kxEdit.Editor.Tests;

/// <summary>
/// フェーズ 9a(設計書 §14.1・計画 §0.2): 無効化するのは、記述子が違う行の帯だけ。
/// 数えるのは Control.Invalidated の InvalidRect。描画は TestHook_PaintToBitmap(record: true) で起こす。
/// 各テストは非既定の位置から始め、操作の前に「描画の記録がある」を確かめる(CLAUDE.md §4-B)。
/// </summary>
public class EditorControlPartialInvalidateTests
{
    // 30 行・400×260。行 25 は可視域の外。
    private static string Body() =>
        string.Join("\r\n", Enumerable.Range(0, 30).Select(i => $"line {i:D2} あいう abc"));

    private static (Form F, EditorControl C) MakeHosted(string? body = null)
    {
        var f = new Form { Size = new Size(400, 260) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(body ?? Body()));
        Assert.True(
            c.IsHandleCreated,
            "前提: ハンドルがある(Invalidate(Rectangle) が Invalidated を発火する条件)"
        );
        return (f, c);
    }

    private static int Line(EditorControl c, int line) =>
        c.CurrentBuffer.Current.GetLineStart(line);

    private static void PaintAndAssumeRecorded(EditorControl c)
    {
        EditorControl.TestHook_PaintToBitmap(c, record: true).Dispose();
        Assert.True(EditorControl.TestHook_HasLastPaintedInputs(c), "前提: 描画が記録されていない");
    }

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

    /// <summary>
    /// 無効化した行の集合(帯の中身を LH で割る。+1px の下辺は次の行に数えない)。
    /// クライアントの下端で切れた帯の下辺は +1px ではない(下端の部分的な行の 1 画素でありうる)ので、そのまま数える。
    /// </summary>
    private static SortedSet<int> RowsOf(EditorControl c, List<Rectangle> rects)
    {
        int lh = c.Metrics.LineHeightPx;
        int clientBottom = c.ClientSize.Height;
        var set = new SortedSet<int>();
        foreach (var r in rects)
        {
            bool edgePixel = r.Bottom % lh == 1 && r.Bottom < clientBottom;
            for (int row = r.Top / lh; row * lh < r.Bottom - (edgePixel ? 1 : 0); row++)
            {
                set.Add(row);
            }
        }
        return set;
    }

    private static bool IsFull(EditorControl c, List<Rectangle> rects) =>
        rects.Any(r => r.Contains(c.ClientRectangle));

    [Fact]
    public void Typing_invalidates_only_the_row() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 3) + 4);
                PaintAndAssumeRecorded(c);
                var rects = Rects(c, () => c.ReplaceCharRange(c.CaretCharOffset, 0, "x"));
                Assert.False(IsFull(c, rects));
                Assert.Equal([3], RowsOf(c, rects));
            }
        });

    [Fact]
    public void Enter_invalidates_the_row_and_everything_below_but_nothing_above() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 3) + 4);
                PaintAndAssumeRecorded(c);
                var rects = Rects(c, () => c.ReplaceCharRange(c.CaretCharOffset, 0, "\r\n"));
                Assert.False(IsFull(c, rects));
                var rows = RowsOf(c, rects);
                Assert.Equal(3, rows.Min);
                int lastVisible = (c.ClientSize.Height - 1) / c.Metrics.LineHeightPx;
                Assert.Equal(lastVisible, rows.Max);
            }
        });

    [Fact]
    public void Moving_the_current_line_invalidates_the_old_and_new_rows() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.HighlightCurrentLine = true;
                c.SetCaretCharOffset(Line(c, 2) + 3);
                PaintAndAssumeRecorded(c);
                var rects = Rects(c, () => c.SetCaretCharOffset(Line(c, 5) + 1));
                Assert.Equal([2, 5], RowsOf(c, rects));
            }
        });

    [Fact]
    public void Extending_a_selection_within_a_row_invalidates_only_that_row() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 2) + 3);
                PaintAndAssumeRecorded(c);
                var rects = Rects(c, () => c.MoveCaretWithSelection(Line(c, 2) + 8));
                Assert.Equal([2], RowsOf(c, rects));
            }
        });

    [Fact]
    public void Clearing_a_multi_row_selection_invalidates_those_rows() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetSelectionCharRange(Line(c, 1) + 4, Line(c, 3) + 5);
                PaintAndAssumeRecorded(c);
                var rects = Rects(c, () => c.SetCaretCharOffset(Line(c, 3) + 5));
                Assert.Equal([1, 2, 3], RowsOf(c, rects));
            }
        });

    [Fact]
    public void An_ime_update_invalidates_its_row_and_the_next() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 4) + 2);
                c.__TestApplyComposition("か", 1, [0], []);
                PaintAndAssumeRecorded(c);
                var rects = Rects(c, () => c.__TestApplyComposition("かな", 2, [0, 0], []));
                Assert.Equal([4, 5], RowsOf(c, rects));
            }
        });

    [Fact]
    public void A_cell_highlight_invalidates_its_row_plus_the_edge_pixel() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 1) + 1);
                PaintAndAssumeRecorded(c);
                int lh = c.Metrics.LineHeightPx;
                var rects = Rects(c, () => c.HighlightCharRange(Line(c, 4) + 2, 3));
                Assert.Equal(4 * lh, rects.Min(r => r.Top));
                Assert.Equal(5 * lh + 1, rects.Max(r => r.Bottom));
            }
        });

    [Fact]
    public void An_edit_outside_the_viewport_invalidates_nothing() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 2) + 1);
                PaintAndAssumeRecorded(c);
                int before = c.TopLine;
                // 行 25(可視外)の中身だけを変える。キャレットは動かさない(ReplaceCharRange はキャレットを置換の後ろへ動かすので、
                // 可視外へ追従スクロールが起きる = この API は使わない)。
                var rects = Rects(c, () => c.TestHook_ReplaceWithoutCaret(Line(c, 25) + 2, 1, "Z"));
                Assert.Equal(before, c.TopLine); // 前提: スクロールしていない
                Assert.Equal("Z", c.CurrentBuffer.Current.GetText(Line(c, 25) + 2, 1)); // 前提: 本文が変わった
                Assert.Empty(rects);
            }
        });

    [Fact]
    public void A_change_of_line_number_width_invalidates_everything() =>
        Sta.Run(() =>
        {
            // 999 行 → 1000 行で行番号の桁が 3 → 4 になる。
            var (f, c) = MakeHosted(
                string.Join("\r\n", Enumerable.Range(0, 999).Select(i => $"l{i}"))
            );
            using (f)
            {
                c.ShowLineNumbers = true;
                c.SetCaretCharOffset(Line(c, 2));
                PaintAndAssumeRecorded(c);
                var rects = Rects(c, () => c.ReplaceCharRange(c.CaretCharOffset, 0, "\r\n"));
                Assert.True(IsFull(c, rects));
            }
        });

    [Fact]
    public void Retyping_in_a_wrapped_paragraph_does_not_invalidate_rows_above() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.WrapColumns = 10;
                c.SetCaretCharOffset(Line(c, 3) + 2);
                PaintAndAssumeRecorded(c);
                var rects = Rects(c, () => c.ReplaceCharRange(c.CaretCharOffset, 0, "xxxxxx"));
                Assert.False(IsFull(c, rects));
                int firstRowOfLine3 = c.TestHook_VisualRowIndexOf(Line(c, 3)); // 行 3 の先頭の視覚行の番号
                Assert.Equal(firstRowOfLine3, RowsOf(c, rects).Min);
            }
        });

    [Fact]
    public void Without_a_recorded_frame_everything_is_invalidated() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                Assert.False(EditorControl.TestHook_HasLastPaintedInputs(c)); // 前提
                var rects = Rects(c, () => c.ReplaceCharRange(0, 0, "x"));
                Assert.True(IsFull(c, rects));
            }
        });

    /// <summary>Review Focus 1: 一時的にキャレットを動かして戻す経路が、古い現在行を残さない。</summary>
    [Fact]
    public void EnsureVisibleCharRange_WithHighlight_LeavesNoStaleRow() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.HighlightCurrentLine = true;
                c.SetCaretCharOffset(Line(c, 2) + 1);
                PaintAndAssumeRecorded(c);
                c.EnsureVisibleCharRange(Line(c, 4), 1); // 可視域の中 = スクロールしない
                Assert.Equal(Line(c, 2) + 1, c.CaretCharOffset); // 前提: キャレットは戻っている
                Assert.Equal(0, c.TopLine); // 前提: スクロールしていない
                // 一時的な移動は setter を通らない(スクロールしない)ので、無効化は起きなくてよい。
                // 戻した後の入力が記録と等しいこと = 古い行が残らないこと。
                // スクロールを伴う場合は Task 5 のオラクル(偽の画面)で確かめる。
                Assert.True(EditorControl.TestHook_LastPaintedEqualsCurrent(c));
            }
        });
}
