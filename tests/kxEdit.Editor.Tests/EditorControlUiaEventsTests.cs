using System;
using System.Windows.Forms;
using kxEdit.Core.Buffers;
using kxEdit.Editor;
using Xunit;

namespace kxEdit.Editor.Tests;

// P5 Task 14 (M-2): TestHook_ForceUiaListen は static bool のため、
// UIA イベント発火系テストは同じ collection に押し込んで xUnit の並列実行から除外する。
[Collection("UiaEventHook")]
public class EditorControlUiaEventsTests
{
    [Fact]
    public void Edit_RaisesTextChangedAndTextSelectionChanged()
    {
        Sta.Run(() =>
        {
            EditorControl.TestHook_ForceUiaListen = true;
            try
            {
                using var ctrl = new EditorControl();
                ctrl.SetSource(TextBuffer.FromString("abc"));
                using var form = new Form();
                form.Controls.Add(ctrl);
                form.Show();
                try
                {
                    // WM_GETOBJECT 経由でプロバイダを生成させる(=RaiseUia の early return を回避)
                    var msg = Message.Create(
                        ctrl.Handle,
                        0x003D,
                        System.IntPtr.Zero,
                        new System.IntPtr(-25)
                    );
                    EditorControl.TestHook_WndProc(ctrl, ref msg);

                    EditorControl.TestHook_ResetUiaEventCounts(ctrl);
                    ctrl.SetSelectionCharRange(3, 3);
                    ctrl.ReplaceCharRange(3, 0, "x"); // "abcx"
                    Application.DoEvents();
                    var (textChanged, selChanged, _) = EditorControl.TestHook_UiaEventCounts(ctrl);
                    Assert.True(textChanged >= 1, "TextChangedEvent が発火していない");
                    Assert.True(selChanged >= 1, "TextSelectionChangedEvent が発火していない");
                }
                finally
                {
                    form.Close();
                }
            }
            finally
            {
                EditorControl.TestHook_ForceUiaListen = false;
            }
        });
    }

    [Fact]
    public void MoveCaret_RaisesTextSelectionChanged()
    {
        Sta.Run(() =>
        {
            EditorControl.TestHook_ForceUiaListen = true;
            try
            {
                using var ctrl = new EditorControl();
                ctrl.SetSource(TextBuffer.FromString("hello"));
                using var form = new Form();
                form.Controls.Add(ctrl);
                form.Show();
                try
                {
                    var msg = Message.Create(
                        ctrl.Handle,
                        0x003D,
                        System.IntPtr.Zero,
                        new System.IntPtr(-25)
                    );
                    EditorControl.TestHook_WndProc(ctrl, ref msg);

                    EditorControl.TestHook_ResetUiaEventCounts(ctrl);
                    ctrl.SetCaretCharOffset(3);
                    Application.DoEvents();
                    var (_, selChanged, _) = EditorControl.TestHook_UiaEventCounts(ctrl);
                    Assert.True(
                        selChanged >= 1,
                        "MoveCaret で TextSelectionChangedEvent が発火していない"
                    );
                }
                finally
                {
                    form.Close();
                }
            }
            finally
            {
                EditorControl.TestHook_ForceUiaListen = false;
            }
        });
    }

    [Fact]
    public void RaiseUiaSelectionEvents_False_SuppressesSelectionEvents()
    {
        Sta.Run(() =>
        {
            EditorControl.TestHook_ForceUiaListen = true;
            try
            {
                using var ctrl = new EditorControl();
                ctrl.SetSource(TextBuffer.FromString("hello"));
                using var form = new Form();
                form.Controls.Add(ctrl);
                form.Show();
                try
                {
                    var msg = Message.Create(
                        ctrl.Handle,
                        0x003D,
                        System.IntPtr.Zero,
                        new System.IntPtr(-25)
                    );
                    EditorControl.TestHook_WndProc(ctrl, ref msg);

                    ctrl.RaiseUiaSelectionEvents = false;
                    EditorControl.TestHook_ResetUiaEventCounts(ctrl);
                    ctrl.SetCaretCharOffset(3);
                    Application.DoEvents();
                    var (_, selChanged, _) = EditorControl.TestHook_UiaEventCounts(ctrl);
                    Assert.Equal(0, selChanged);
                }
                finally
                {
                    form.Close();
                }
            }
            finally
            {
                EditorControl.TestHook_ForceUiaListen = false;
            }
        });
    }
}
