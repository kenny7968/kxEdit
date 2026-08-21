using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using kxEdit.Core.Buffers;
using kxEdit.Editor;
using Xunit;

namespace kxEdit.Editor.Tests;

public class EditorControlNativeSurfaceTests
{
    // CA1838 対応: StringBuilder パラメータではなく char[] を受ける宣言に変更。
    // WM_GETTEXT の lParam は「LPWSTR (char buffer)」の意味論なので char[] で表現するのが
    // 型的に正しい。テスト意図(WM_GETTEXT が空文字を返すことの検証)は不変。
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern System.IntPtr SendMessageGetText(
        System.IntPtr hWnd,
        uint msg,
        System.IntPtr wParam,
        [Out] char[] lParam
    );

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern System.IntPtr SendMessageInt(
        System.IntPtr hWnd,
        uint msg,
        System.IntPtr wParam,
        System.IntPtr lParam
    );

    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_GETTEXTLENGTH = 0x000E;

    [Fact]
    public void WM_GETTEXT_ReturnsEmpty()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("secret content"));
            using var form = HostForm.CreateVisible();
            form.Controls.Add(ctrl);
            try
            {
                var buf = new char[1024];
                var written = SendMessageGetText(
                    ctrl.Handle,
                    WM_GETTEXT,
                    new System.IntPtr(1024),
                    buf
                );
                // 書き込まれた文字数=0 が期待挙動(WM_GETTEXT は EditorControl で抑止済み)。
                // 念のため NUL 終端までの文字列としても空を確認。
                int len = Array.IndexOf(buf, '\0');
                if (len < 0)
                    len = buf.Length;
                Assert.Equal(System.IntPtr.Zero, written);
                Assert.Equal("", new string(buf, 0, len));
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void WM_GETTEXTLENGTH_ReturnsZero()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("some text"));
            using var form = HostForm.CreateVisible();
            form.Controls.Add(ctrl);
            try
            {
                var r = SendMessageInt(
                    ctrl.Handle,
                    WM_GETTEXTLENGTH,
                    System.IntPtr.Zero,
                    System.IntPtr.Zero
                );
                Assert.Equal(System.IntPtr.Zero, r);
            }
            finally
            {
                form.Close();
            }
        });
    }
}
