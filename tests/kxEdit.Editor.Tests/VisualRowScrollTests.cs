using kxEdit.Core.Buffers;

namespace kxEdit.Editor.Tests;

/// <summary>
/// 2026-08-22 監査 A-6: 可視域の起点を視覚行 (TopLine, TopSegment) にする(設計書 不変条件 I-2)。
/// 本ファイルは状態とスクロール判断の契約を固定する。
/// 折り返し OFF では TopSegment が常に 0 で全式が現行に退化すること(I-3)も併せて守る。
/// </summary>
public class VisualRowScrollTests
{
    /// <summary>
    /// 可視行数を明示したエディタを作る。折り返し ON では HScrollBar が常に隠れるため
    /// PaintHeightPx == ClientSize.Height になり、可視行数をテストから固定できる
    /// (EditorControlWrapCaretTests.MakeControl と同じ流儀)。
    /// </summary>
    private static (Form f, EditorControl c) MakeControl(string text, int wrap, int visibleRows)
    {
        var f = new HostForm();
        var c = new EditorControl { WrapColumns = wrap };
        f.Controls.Add(c);
        _ = f.Handle;
        c.ClientSize = new System.Drawing.Size(800, c.LineHeightPx * visibleRows);
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    [Fact]
    public void TopSegment_IsZero_ByDefault() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                Assert.Equal(0, c.TopSegment);
            }
        });

    [Fact]
    public void SetTopPosition_KeepsSegment_AndTopLineSetterResetsIt() =>
        Sta.Run(() =>
        {
            // 非既定位置(TopSegment=2)から検証を始める=「0 のままだった」と区別する。
            var (f, c) = MakeControl("abcdefghij\nklmnop", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 2);
                Assert.Equal(0, c.TopLine);
                Assert.Equal(2, c.TopSegment);

                // TopLine セッターは「その行の先頭視覚行から」の意味を保つ=TopSegment を 0 に戻す。
                // 同じ論理行を代入しても戻ること(早期 return に潰されないこと)。
                c.TopLine = 0;
                Assert.Equal(0, c.TopLine);
                Assert.Equal(0, c.TopSegment);
            }
        });

    [Fact]
    public void WrapColumnsSetter_ResetsTopSegment() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 2);
                Assert.Equal(2, c.TopSegment);
                c.WrapColumns = 4; // 折り返し幅が変わればセグメント分割そのものが変わる
                Assert.Equal(0, c.TopSegment);
            }
        });

    [Fact]
    public void ReplaceSource_ResetsTopSegment() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 2);
                c.ReplaceSource(TextBuffer.FromString("xyz"));
                Assert.Equal(0, c.TopSegment);
                Assert.Equal(0, c.TopLine);
            }
        });

    [Fact]
    public void SetTopPosition_ClampsLine_AndDropsSegmentWhenLineClamped() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                // 論理行 1 本しかないので line=5 は 0 にクランプされる。
                // 行がクランプされたときは segment の意味が失われるので 0 にする。
                c.SetTopPosition(5, 3);
                Assert.Equal(0, c.TopLine);
                Assert.Equal(0, c.TopSegment);
            }
        });
}
