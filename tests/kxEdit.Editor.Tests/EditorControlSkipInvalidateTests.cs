using System.Drawing;
using kxEdit.Core.Settings;
using kxEdit.Core.Text;

namespace kxEdit.Editor.Tests;

/// <summary>
/// 2026-09-25 性能改善フェーズ 3(設計書 §8.2・§8.4 の表)。キャレット・選択の 4 経路は、
/// フレームが変わらなければ Invalidate しない。数えるのは Control.Invalidated イベント
/// (先例 = EditorControlConvertEolsTests.UndoEolConversion_InvalidatesOnce)。
/// ホストは非フォーカス = PositionCaret が走らない。描画は TestHook_PaintToBitmap(record: true) で起こす
/// (画面外の窓には WM_PAINT が来ないため)。
/// 「0 回」のテストは、既定位置(0)ではない位置から始め、操作の前に「描画の記録がある」
/// 「キャレットが実際に動いた」を確かめる(CLAUDE.md §4-B = 前提と発火条件を一致させる)。
/// </summary>
public class EditorControlSkipInvalidateTests
{
    // 30 行・400×200 = 可視はおよそ 9〜10 行。行 25 は可視域の外。
    private static string Body() =>
        string.Join("\r\n", Enumerable.Range(0, 30).Select(i => $"line {i:D2} あいう abc"));

    private static (Form F, EditorControl C) MakeHosted()
    {
        var f = new Form { Size = new Size(400, 200) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(Body()));
        return (f, c);
    }

    private static int Line(EditorControl c, int line) =>
        c.CurrentBuffer.Current.GetLineStart(line);

    private static int CountInvalidations(EditorControl c, Action act)
    {
        int n = 0;
        InvalidateEventHandler h = (_, _) => n++;
        c.Invalidated += h;
        try
        {
            act();
        }
        finally
        {
            c.Invalidated -= h;
        }
        return n;
    }

    private static void Paint(EditorControl c) =>
        EditorControl.TestHook_PaintToBitmap(c, record: true).Dispose();

    /// <summary>前提: 描画の記録がある状態から始める(記録がなければ比較は必ず「変化あり」)。</summary>
    private static void PaintAndAssumeRecorded(EditorControl c)
    {
        Paint(c);
        Assert.True(EditorControl.TestHook_HasLastPaintedInputs(c), "前提: 描画が記録されていない");
    }

    [Fact]
    public void CaretMove_WithoutHighlight_DoesNotInvalidate() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 2) + 3); // 非既定位置から
                PaintAndAssumeRecorded(c);
                int target = Line(c, 4) + 1;

                int n = CountInvalidations(c, () => c.SetCaretCharOffset(target));

                Assert.Equal(target, c.CaretCharOffset); // 前提: 早期 return していない
                Assert.Equal(0, c.TopLine); // 前提: スクロールしていない
                Assert.Equal(0, n);
            }
        });

    [Fact]
    public void CaretMove_BeforeAnyPaint_Invalidates() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                Assert.False(EditorControl.TestHook_HasLastPaintedInputs(c)); // 前提
                int n = CountInvalidations(c, () => c.SetCaretCharOffset(Line(c, 1) + 2));
                Assert.True(n >= 1);
            }
        });

    [Fact]
    public void CaretMove_WithHighlight_ToAnotherLine_Invalidates() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.HighlightCurrentLine = true;
                c.SetCaretCharOffset(Line(c, 2) + 3);
                PaintAndAssumeRecorded(c);
                int n = CountInvalidations(c, () => c.SetCaretCharOffset(Line(c, 3) + 3));
                Assert.True(n >= 1);
            }
        });

    [Fact]
    public void CaretMove_WithHighlight_WithinTheLine_DoesNotInvalidate() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.HighlightCurrentLine = true;
                c.SetCaretCharOffset(Line(c, 2) + 3);
                PaintAndAssumeRecorded(c);
                int target = Line(c, 2) + 5;
                int n = CountInvalidations(c, () => c.SetCaretCharOffset(target));
                Assert.Equal(target, c.CaretCharOffset);
                Assert.Equal(0, n);
            }
        });

    [Fact]
    public void SelectionChange_Invalidates_OnAllSelectingPaths() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 2) + 3);
                PaintAndAssumeRecorded(c);
                Assert.True(
                    CountInvalidations(c, () => c.MoveCaretWithSelection(Line(c, 2) + 6)) >= 1
                );
                PaintAndAssumeRecorded(c);
                Assert.True(
                    CountInvalidations(c, () => c.SetSelectionCharRange(Line(c, 1), Line(c, 3)))
                        >= 1
                );
                PaintAndAssumeRecorded(c);
                Assert.True(
                    CountInvalidations(c, () => c.SetSelectionAnchored(Line(c, 4), Line(c, 1))) >= 1
                );
            }
        });

    [Fact]
    public void SelectionClear_Invalidates() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetSelectionCharRange(Line(c, 1) + 1, Line(c, 2) + 4);
                PaintAndAssumeRecorded(c);
                int n = CountInvalidations(c, () => c.SetCaretCharOffset(Line(c, 2) + 4));
                Assert.Equal(c.SelectionAnchor, c.CaretCharOffset); // 前提: 選択が消えた
                Assert.True(n >= 1);
            }
        });

    /// <summary>範囲指定の 2 経路でも、空の選択(= 単なるキャレット移動)ならフレームは変わらない。</summary>
    [Fact]
    public void CollapsedSelection_OnRangePaths_DoesNotInvalidate() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 2) + 3);
                PaintAndAssumeRecorded(c);
                int p1 = Line(c, 3) + 2;
                Assert.Equal(0, CountInvalidations(c, () => c.SetSelectionCharRange(p1, p1)));
                Assert.Equal(p1, c.CaretCharOffset);
                int p2 = Line(c, 5) + 1;
                Assert.Equal(0, CountInvalidations(c, () => c.SetSelectionAnchored(p2, p2)));
                Assert.Equal(p2, c.CaretCharOffset);
            }
        });

    /// <summary>
    /// スクロールを伴う移動: スクロールのセッターが無条件に 1 回、InvalidateIfFrameChanged が TopLine の
    /// 違いを見てもう 1 回(設計書 §8.4。変更前も 2 回)。
    /// </summary>
    [Fact]
    public void CaretMove_WithScroll_InvalidatesAtMostTwice() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 2) + 3);
                PaintAndAssumeRecorded(c);
                int n = CountInvalidations(c, () => c.SetCaretCharOffset(Line(c, 25)));
                Assert.True(c.TopLine > 0, "前提: スクロールしていない");
                Assert.InRange(n, 1, 2);
            }
        });

    [Fact]
    public void CaretMove_DuringComposition_Invalidates() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 2) + 3);
                c.__TestApplyComposition("かな", 2, [0, 0], []);
                PaintAndAssumeRecorded(c);
                int n = CountInvalidations(c, () => c.SetCaretCharOffset(Line(c, 4)));
                Assert.False(c.__TestIsComposing()); // 前提: 移動で未確定が取り消された
                Assert.True(n >= 1);
            }
        });

    // ---- 記録を捨てる経路(設計書 §8.2 と実装計画 §0.2) ----

    public static TheoryData<string> ForgettingPaths() =>
        ["ReplaceSource", "ConvertEols", "UndoEolConversion", "ApplyAppearance"];

    [Theory]
    [MemberData(nameof(ForgettingPaths))]
    public void WholesaleReplacement_ForgetsThePaintedFrame(string path) =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                bool recorded = path == "UndoEolConversion" && c.ConvertEols(LineEnding.Lf);
                PaintAndAssumeRecorded(c);
                switch (path)
                {
                    case "ReplaceSource":
                        c.ReplaceSource(TextBuffer.FromString("x"));
                        break;
                    case "ConvertEols":
                        Assert.True(c.ConvertEols(LineEnding.Lf)); // 前提: 非 fast-path(本文は CRLF)
                        break;
                    case "UndoEolConversion":
                        Assert.True(recorded); // 前提
                        Assert.True(c.UndoEolConversion(recorded, 0, 0));
                        break;
                    case "ApplyAppearance":
                        c.ApplyAppearance(new AppSettings());
                        break;
                }
                Assert.False(EditorControl.TestHook_HasLastPaintedInputs(c));
            }
        });
}
