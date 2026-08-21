using System;
using System.Windows.Forms;
using kxEdit.Core.Buffers;
using kxEdit.Editor;
using Xunit;

namespace kxEdit.Editor.Tests;

// P5 Task 14 (M-2): TestHook_ForceUiaListen は static bool のため、
// UIA イベント発火系テストは同じ collection に押し込んで xUnit の並列実行から除外する。
[Collection("UiaEventHook")]
public class EditorControlUiaFocusEventTests
{
    [Fact]
    public void OnGotFocus_RaisesFocusChangedAndTextSelectionChanged()
    {
        Sta.Run(() =>
        {
            EditorControl.TestHook_ForceUiaListen = true;
            try
            {
                using var ctrl = new EditorControl();
                ctrl.SetSource(TextBuffer.FromString("hi"));
                using var form = new Form();
                form.Controls.Add(ctrl);
                using var other = new TextBox();
                form.Controls.Add(other);
                form.Show();

                // WM_GETOBJECT 経由でプロバイダを生成させる(=RaiseUia の early return を回避)
                var msg = Message.Create(
                    ctrl.Handle,
                    0x003D,
                    System.IntPtr.Zero,
                    new System.IntPtr(-25)
                );
                EditorControl.TestHook_WndProc(ctrl, ref msg);

                other.Focus();
                Application.DoEvents();

                EditorControl.TestHook_ResetUiaEventCounts(ctrl);
                ctrl.Focus();
                Application.DoEvents();

                var (_, selChanged, focusChanged) = EditorControl.TestHook_UiaEventCounts(ctrl);
                Assert.True(focusChanged >= 1, "AutomationFocusChangedEvent が発火していない");
                Assert.True(
                    selChanged >= 1,
                    "OnGotFocus 明示発火 TextSelectionChangedEvent が発火していない"
                );
                form.Close();
            }
            finally
            {
                EditorControl.TestHook_ForceUiaListen = false;
            }
        });
    }
}
