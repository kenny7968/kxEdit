using kxEdit.Core.Buffers;
using kxEdit.Core.Layout;
using kxEdit.Core.Settings;
using kxEdit.Editor.Tests.Fakes;

namespace kxEdit.Editor.Tests;

/// <summary>
/// 非 ASCII の 43,679 字を超える行で、横スクロールとキャレットの X が働くこと(設計書 2026-10-06 §1.2・§4.2)。
/// 修正前は GDI の一括計測が幅 0 を返し、横スクロールバーが出ず、キャレットを行末に置いても ScrollX が 0 のままだった。
/// </summary>
public class LongRowScrollTests
{
    private const int Chars = 50_000;

    // 横スクロールバーの Visible は祖先(Form)の表示状態に連動し、未 Show の Form では常に false になる
    // (= BringCaretIntoView の横の追従が no-op になり、修正の有無にかかわらず ScrollX が 0 のまま)。
    // そのため画面外・非アクティブで可視化した HostForm に載せる(ClipPaintTests の同種コメント参照)。
    private static (Form f, EditorControl c) MakeControl()
    {
        var f = HostForm.CreateVisible();
        f.Size = new Size(400, 200);
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        f.PerformLayout();
        c.SetSource(TextBuffer.FromString(new string('吾', Chars)));
        return (f, c);
    }

    [Fact]
    public void Caret_at_the_end_of_a_long_japanese_row_scrolls_into_view() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                Assert.Equal(0, c.WrapColumns);
                c.SetCaretCharOffset(Chars);

                Assert.True(c.ScrollX > 0, "ScrollX が 0 のまま(横スクロールバーが出ていない)");
                var (x, _, visible) = c.ComputeCaretPoint(Chars);
                Assert.True(visible);
                // 追従は「可視領域末尾から 1 半角幅内側」に置く(BringCaretIntoView)。描画幅は縦スクロールバーの分だけ
                // クライアント幅より狭いが、その幅は DPI に依存するので、ここではクライアント幅の内側であることを見る。
                Assert.InRange(x - c.ScrollX, 0, c.ClientSize.Width - 1);
            }
        });

    [Fact]
    public void Caret_x_in_a_long_japanese_row_is_the_sum_of_character_widths() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                int one = c.ComputeCaretPoint(1).X - c.ComputeCaretPoint(0).X;
                Assert.True(one > 0);
                int x0 = c.ComputeCaretPoint(0).X;
                Assert.Equal(x0 + 45_000 * one, c.ComputeCaretPoint(45_000).X);
            }
        });

    [Fact]
    public void Paint_of_a_long_row_draws_only_the_characters_in_the_window() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(45_000);
                Assert.True(c.ScrollX > 0);
                PaintTestHelpers.PaintRecorded(c);

                var frame = EditorControl.TestHook_GetLastFrame(c);
                Assert.NotNull(frame);
                // 本文の op だけ(行番号の op が混ざっても拾わないよう、本文の文字だけでできたものに絞る)。
                var body = frame!
                    .Ops.Where(op =>
                        op.Kind == PaintOpKind.DrawText
                        && op.Text is { Length: > 0 } t
                        && t.All(ch => ch == '吾')
                    )
                    .ToList();
                int one = c.ComputeCaretPoint(1).X - c.ComputeCaretPoint(0).X;
                int chars = body.Sum(op => op.Text!.Length);
                // 窓に入る文字数 + 余裕(左端の 1 文字・右端の 1 文字)を超えない。行全体(5 万字)ではない。
                Assert.InRange(chars, 1, (c.ClientSize.Width / one) + 3);
                // 最初の op は窓の左端以前から始まり、1 文字ぶんより左には出ない。
                Assert.InRange(body[0].X - c.ScrollX, -one, 0);
            }
        });

    // 最終レビュー I-1: 足し算の幅が int を超える行(折り返し OFF)。修正前は幅が負へ回り込み、横スクロールバーが
    // 出なかった(帯によっては Maximum だけが負になり、ScrollBar.Value の設定で ArgumentOutOfRangeException)。
    // 大きなフォントで 1 文字の幅を広げ、和が int.MaxValue をわずかに超える字数の行を作る(数 MB で済む)。
    [Fact]
    public void Row_wider_than_int_keeps_the_horizontal_scrollbar_usable() =>
        Sta.Run(() =>
        {
            var f = HostForm.CreateVisible();
            f.Size = new Size(400, 200);
            var c = new EditorControl { Dock = DockStyle.Fill };
            using (f)
            using (c)
            {
                f.Controls.Add(c);
                f.PerformLayout();
                c.ApplyAppearance(new AppSettings { FontSize = 1000f, ShowLineNumbers = true });
                int one = c.Metrics.MeasureRun("吾");
                Assert.True(one >= 500, $"フォントが大きくならない(1 文字 {one}px)");
                int chars = (int.MaxValue / one) + 2; // 和は int.MaxValue を 1〜2 文字ぶん超える
                c.SetSource(TextBuffer.FromString(new string('吾', chars)));
                Assert.Equal(0, c.WrapColumns);

                c.ScrollX = int.MaxValue; // 右端へ(クランプされる)
                Assert.True(
                    c.ScrollX > int.MaxValue / 2,
                    $"横スクロールバーが働かない(ScrollX = {c.ScrollX})"
                );

                // 行末・行頭へキャレットを動かして追従させても例外を出さない(スクロールバーの更新を含む)。
                // 行末のキャレットの X(行番号の幅 + 行の幅)は int を超えるので、ここでは位置を問わない。
                c.SetCaretCharOffset(chars);
                c.SetCaretCharOffset(0);
                Assert.InRange(c.ScrollX, 0, int.MaxValue - 1);
            }
        });
}
