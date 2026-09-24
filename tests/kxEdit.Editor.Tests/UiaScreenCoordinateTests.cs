using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using kxEdit.Accessibility;
using kxEdit.Core.Buffers;
using kxEdit.Editor;
using Xunit;

namespace kxEdit.Editor.Tests;

/// <summary>
/// フェーズ 2(S-1・設計書 §7.1): UIA の座標系 API が、描画を経ずにメインウィンドウを動かした
/// 直後も正しいスクリーン座標を返すこと。HostForm は画面外(-32000)にあり WM_PAINT が届かない
/// ので、「OnPaint 末尾で座標を更新する」旧実装の陳腐化をそのまま観測できる。
/// </summary>
public class UiaScreenCoordinateTests
{
    private static (HostForm Form, EditorControl Ctrl) MakeHosted(string text)
    {
        var form = HostForm.CreateVisible();
        var ctrl = new EditorControl { Dock = DockStyle.Fill };
        form.Controls.Add(ctrl);
        ctrl.SetSource(TextBuffer.FromString(text));
        form.ClientSize = new Size(300, 200);
        form.PerformLayout();
        return (form, ctrl);
    }

    /// <summary>フォームを画面外のまま動かす(子の LocationChanged は発火しない)。</summary>
    private static void MoveForm(Form form) =>
        form.Location = new Point(form.Location.X + 500, form.Location.Y + 300);

    [Fact]
    public void GetBoundingRectangles_AfterFormMove_FollowsNewOrigin()
    {
        Sta.Run(() =>
        {
            var (form, ctrl) = MakeHosted("hello\nworld");
            try
            {
                IUiaTextHost host = ctrl;
                var before = host.GetBoundingRectangles(0, 5);
                Assert.NotEmpty(before); // fixture 前提: 行 0 は可視
                var originBefore = ctrl.PointToScreen(Point.Empty);

                MoveForm(form);
                var origin = ctrl.PointToScreen(Point.Empty);
                Assert.NotEqual(originBefore, origin); // fixture 前提: 実際に動いた

                var after = host.GetBoundingRectangles(0, 5);
                Assert.Equal(before[0] + (origin.X - originBefore.X), after[0]);
                Assert.Equal(before[1] + (origin.Y - originBefore.Y), after[1]);
                Assert.Equal(before[2], after[2]);
                Assert.Equal(before[3], after[3]);

                // 絶対値でも確かめる: 移動後の矩形 = 新しい原点 + 描画原点座標 - ScrollX。
                // PointFromCharOffset は ScrollX 反映済みで、不可視と (0,0) が Point.Empty で区別できない
                // ため、adapter と同じ入口の ComputeCaretPointForUia を使う。
                var (cx, cy, visible) = ctrl.ComputeCaretPointForUia(0);
                Assert.True(visible); // fixture 前提
                Assert.Equal(origin.X + cx - ctrl.ScrollX, after[0]);
                Assert.Equal(origin.Y + cy, after[1]);
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }

    [Fact]
    public void OffsetFromScreenPoint_AfterFormMove_UsesNewOrigin()
    {
        Sta.Run(() =>
        {
            var (form, ctrl) = MakeHosted("hello\nworld");
            try
            {
                MoveForm(form);
                var origin = ctrl.PointToScreen(Point.Empty);
                int lh = ctrl.Metrics.LineHeightPx;
                IUiaTextHost host = ctrl;
                // 行 1("world")の左端・縦中央。旧実装は移動前の原点で client 座標に直すので、
                // 300px 下=文書の外を指して文書末尾(11)に丸める。
                int result = host.OffsetFromScreenPoint(origin.X + 1, origin.Y + lh + lh / 2);
                Assert.InRange(result, 6, 7);
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }

    [Fact]
    public void BoundingRectangle_AfterFormMove_MatchesClientRectOnScreen()
    {
        Sta.Run(() =>
        {
            var (form, ctrl) = MakeHosted("hello");
            try
            {
                MoveForm(form);
                var r = ctrl.RectangleToScreen(ctrl.ClientRectangle);
                IUiaTextHost host = ctrl;
                Assert.Equal(
                    new System.Windows.Rect(r.Left, r.Top, r.Width, r.Height),
                    host.BoundingRectangle
                );
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }

    // RPC スレッド相当(UI スレッド以外)から読んでも、UI スレッドで読んだ値と一致すること
    // (BoundingRectangle は Invoke しない=a11y 鉄則。Win32 の HWND API だけで答える)。
    [Fact]
    public void BoundingRectangle_FromWorkerThread_MatchesUiThreadValue()
    {
        Sta.Run(() =>
        {
            var (form, ctrl) = MakeHosted("hello");
            try
            {
                MoveForm(form);
                IUiaTextHost host = ctrl;
                var onUi = host.BoundingRectangle;
                System.Windows.Rect onWorker = default;
                var t = new Thread(() => onWorker = host.BoundingRectangle);
                t.Start();
                Assert.True(
                    t.Join(5000),
                    "ワーカースレッドが戻らない(Invoke して UI 待ちになっている)"
                );
                Assert.NotEqual(default, onUi); // fixture 前提
                Assert.Equal(onUi, onWorker);
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }

    // 設計書 §3.5: Handle の破棄後は、最後にキャッシュした値ではなく空矩形を返す。
    [Fact]
    public void BoundingRectangle_AfterDispose_ReturnsDefault()
    {
        Sta.Run(() =>
        {
            var (form, ctrl) = MakeHosted("hello");
            try
            {
                IUiaTextHost host = ctrl;
                Assert.NotEqual(default, host.BoundingRectangle); // fixture 前提
                ctrl.Dispose();
                Assert.Equal(default(System.Windows.Rect), host.BoundingRectangle);
            }
            finally
            {
                form.Close();
            }
        });
    }
}
