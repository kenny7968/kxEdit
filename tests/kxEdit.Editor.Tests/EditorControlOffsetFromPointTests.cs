using System.Windows.Forms;
using kxEdit.Accessibility;
using kxEdit.Core.Buffers;
using kxEdit.Editor;
using Xunit;

namespace kxEdit.Editor.Tests;

public class EditorControlOffsetFromPointTests
{
    [Fact]
    public void OffsetFromScreenPoint_TopLeft_ReturnsZero()
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
                ctrl.Invalidate();
                ctrl.Update();
                Application.DoEvents();
                var screen = ctrl.PointToScreen(new System.Drawing.Point(2, 2));
                IUiaTextHost host = ctrl;
                // PxToOffset は「入れば含める」規則(px が 1 文字の内側なら次の境界を返す)。
                // client (2,2) は概ね 1 文字目の内側なので 0 か 1 を許容する。
                int result = host.OffsetFromScreenPoint(screen.X, screen.Y);
                Assert.InRange(result, 0, 1);
            }
            finally
            {
                form.Close();
            }
        });
    }

    // LocalOnly 化候補: HostForm 方式(off-screen -32000,-32000)では OnPaint が抑止され
    // ComputeCaretPoint が visible=false を返す=PointFromCharOffset が Point.Empty に落ちる。
    // 座標系の往復を検証する本テストは on-screen での実描画が必須のため、
    // 従来どおり new Form() + form.Show() で on-screen アクティブ化する(CI 非対話では要注意)。
    [Fact]
    public void OffsetFromScreenPoint_MidLine_ReturnsMidChar()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello world"));
            ctrl.Size = new System.Drawing.Size(400, 100);
            using var form = new Form();
            form.Controls.Add(ctrl);
            form.Show();
            try
            {
                ctrl.Invalidate();
                ctrl.Update();
                Application.DoEvents();
                // "hello" の 3 番目の文字位置を client 座標で取得(EditorControl の既存 API)
                var mid = ctrl.PointFromCharOffset(3);
                // 少し右にずらして「その桁を含める」側の HitTest 挙動を狙う
                var screen = ctrl.PointToScreen(new System.Drawing.Point(mid.X + 2, mid.Y + 2));
                IUiaTextHost host = ctrl;
                int result = host.OffsetFromScreenPoint(screen.X, screen.Y);
                Assert.InRange(result, 2, 4); // 3 前後にヒット
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void OffsetFromScreenPoint_OutOfBounds_Clamped()
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
                // 範囲外の (-9999, -9999) → 0 (または clamp した先頭)
                Assert.Equal(0, host.OffsetFromScreenPoint(-9999, -9999));
            }
            finally
            {
                form.Close();
            }
        });
    }
}
