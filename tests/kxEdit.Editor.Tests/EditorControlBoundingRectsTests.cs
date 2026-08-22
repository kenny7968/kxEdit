using System.Windows.Forms;
using kxEdit.Accessibility;
using kxEdit.Core.Buffers;
using kxEdit.Editor;
using Xunit;

namespace kxEdit.Editor.Tests;

public class EditorControlBoundingRectsTests
{
    [Fact]
    public void GetBoundingRectangles_EmptyRange_ReturnsEmptyArray()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello"));
            using var form = HostForm.CreateVisible();
            form.Controls.Add(ctrl);
            try
            {
                IUiaTextHost host = ctrl;
                Assert.Empty(host.GetBoundingRectangles(3, 3)); // 縮退範囲=空配列
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void GetBoundingRectangles_SingleLineRange_ReturnsOneRect()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello world"));
            ctrl.Size = new System.Drawing.Size(400, 100);
            using var form = HostForm.CreateVisible();
            form.Controls.Add(ctrl);
            try
            {
                // 描画を 1 回発生させて _lastFrame を確定
                ctrl.Invalidate();
                ctrl.Update();
                Application.DoEvents();
                IUiaTextHost host = ctrl;
                var rects = host.GetBoundingRectangles(0, 5); // "hello"
                Assert.Equal(4, rects.Length); // 1 行 = 4 要素
                Assert.True(rects[2] > 0); // 幅 > 0
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void GetBoundingRectangles_MultiLineRange_ReturnsMultipleRects()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("aaa\nbbb\nccc"));
            ctrl.Size = new System.Drawing.Size(200, 100);
            using var form = HostForm.CreateVisible();
            form.Controls.Add(ctrl);
            try
            {
                ctrl.Invalidate();
                ctrl.Update();
                Application.DoEvents();
                IUiaTextHost host = ctrl;
                var rects = host.GetBoundingRectangles(0, 11); // 全体
                Assert.Equal(3 * 4, rects.Length); // 3 行 × 4 要素
            }
            finally
            {
                form.Close();
            }
        });
    }

    // A-12(2026-08-22): GetBoundingRectangles が _scrollX を引かず、折り返し OFF で
    // 右へスクロールした状態では NVDA のフォーカスハイライト矩形が実描画より右にずれる。
    // 描画(Paint.cs)・PointFromCharOffset・逆変換 OffsetFromClientPoint は引いており、
    // ここだけが往復非対称だった。
    [Fact]
    public void GetBoundingRectangles_SubtractsScrollX()
    {
        Sta.Run(() =>
        {
            // 長文行 1 本 + 短い行数本。幅を絞って hscroll を表示状態にする。
            // 長文行は line 0 に置く(UpdateHorizontalScrollbar は TopLine から probeHeight 分の
            // 視覚行しか走査しないため、可視域に無いと hscroll が出ない)。
            var text = new string('x', 400) + "\nl1\nl2\nl3";
            using var form = HostForm.CreateVisible();
            var ctrl = new EditorControl { Dock = DockStyle.Fill };
            form.Controls.Add(ctrl);
            ctrl.SetSource(TextBuffer.FromString(text));
            try
            {
                form.ClientSize = new System.Drawing.Size(120, 100);
                form.PerformLayout();
                ctrl.WrapColumns = 0; // 折り返し OFF
                ctrl.TopLine = 0;
                ctrl.Invalidate();
                Application.DoEvents(); // 描画を 1 回起こしてレイアウトを確定

                IUiaTextHost host = ctrl;
                var before = host.GetBoundingRectangles(0, 4);
                Assert.NotEmpty(before); // fixture 前提: 行 0 は可視

                ctrl.ScrollX = 50;
                // fixture 前提: hscroll が表示されていないと ScrollX setter は no-op。
                Assert.True(
                    ctrl.ScrollX > 0,
                    "fixture 前提崩れ: hscroll 非表示で ScrollX を置けない"
                );

                var after = host.GetBoundingRectangles(0, 4);
                Assert.NotEmpty(after);

                // X は ScrollX 分だけ左へ寄る。幅は差分なので不変。
                Assert.Equal(before[0] - ctrl.ScrollX, after[0]);
                Assert.Equal(before[2], after[2]);
                // Y と高さは水平スクロールと無関係=不変。X だけ見ていると
                // 「軸を取り違えて Y から引く」変異(rects.Add(csy + y1 - sx))が素通りする
                // (最終ブランチレビュー品質パス Minor 4 で実際に生存した)。
                Assert.Equal(before[1], after[1]);
                Assert.Equal(before[3], after[3]);
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }
}
