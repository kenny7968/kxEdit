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
}
