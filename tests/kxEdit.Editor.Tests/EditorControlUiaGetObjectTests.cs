using System;
using System.Windows.Forms;
using kxEdit.Core.Buffers;
using kxEdit.Editor;
using Xunit;

namespace kxEdit.Editor.Tests;

public class EditorControlUiaGetObjectTests
{
    [Fact]
    public void WndProc_ReturnsProviderForUiaRootObjectId()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hi"));
            using var form = HostForm.CreateVisible();
            form.Controls.Add(ctrl);
            try
            {
                // WM_GETOBJECT(UiaRootObjectId) を送る
                var msg = Message.Create(
                    ctrl.Handle,
                    0x003D,
                    System.IntPtr.Zero,
                    new System.IntPtr(-25)
                );
                // internal test hook 経由で WndProc を叩く
                EditorControl.TestHook_WndProc(ctrl, ref msg);
                Assert.NotEqual(System.IntPtr.Zero, msg.Result); // 非 0 = プロバイダを返した
                Assert.True(EditorControl.TestHook_LastGetObjectServed(ctrl));
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void WndProc_IgnoresOtherObjIds()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hi"));
            using var form = HostForm.CreateVisible();
            form.Controls.Add(ctrl);
            try
            {
                // WM_GETOBJECT(OBJID_CLIENT=-4)は base に流す=自前応答しない
                var msg = Message.Create(
                    ctrl.Handle,
                    0x003D,
                    System.IntPtr.Zero,
                    new System.IntPtr(-4)
                );
                EditorControl.TestHook_WndProc(ctrl, ref msg);
                // 自前応答経路に入らなかったことを内部フラグで確認
                Assert.False(EditorControl.TestHook_LastGetObjectServed(ctrl));
            }
            finally
            {
                form.Close();
            }
        });
    }
}
