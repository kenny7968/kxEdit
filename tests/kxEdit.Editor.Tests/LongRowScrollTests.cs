using kxEdit.Core.Buffers;

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

                Assert.True(c.ScrollX > 0, $"ScrollX が 0 のまま(横スクロールバーが出ていない)");
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
}
