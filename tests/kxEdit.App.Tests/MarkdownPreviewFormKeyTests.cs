using System.Reflection;
using kxEdit.App.Tests.Fakes;
using Microsoft.Web.WebView2.WinForms;

namespace kxEdit.App.Tests;

/// <summary>
/// フェーズ 8 項目 2: WebView2 にフォーカスがあると、Alt+C(「閉じる(&amp;C)」)は
/// フォームの ProcessCmdKey / ProcessDialogKey / ニーモニックのどれにも届かない。
/// WinForms の WebView2 は AcceleratorKeyPressed を自分の KeyDown に変換し、e.Handled を
/// WebView2 へ書き戻す(2026-10-02-preview-keys.md §0.3)。ここではその KeyDown を直接起こす。
/// <para>
/// フォームは Handle だけ作り、Show はしない。InitAsync は Shown で走るので WebView2 は
/// 初期化されない(WebView2 ランタイムに依存しない)。
/// </para>
/// </summary>
public class MarkdownPreviewFormKeyTests
{
    private static MarkdownPreviewForm NewForm()
    {
        var f = new MarkdownPreviewForm("<p>x</p>", null, "x.md", new FakeReachabilityProbe())
        {
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000),
        };
        _ = f.Handle; // Close が WM_CLOSE を送って FormClosing を起こせるようにする
        return f;
    }

    /// <summary>WebView2 の AcceleratorKeyPressed が行うのと同じく、_web の OnKeyDown を呼ぶ。</summary>
    private static KeyEventArgs RaiseWebKeyDown(MarkdownPreviewForm form, Keys keyData)
    {
        var field = typeof(MarkdownPreviewForm).GetField(
            "_web",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(field);
        var web = (WebView2)field!.GetValue(form)!;
        var onKeyDown = typeof(Control).GetMethod(
            "OnKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(onKeyDown);
        var e = new KeyEventArgs(keyData);
        onKeyDown!.Invoke(web, new object[] { e });
        return e;
    }

    [Fact]
    public void Alt_C_on_WebView2_closes_the_preview() =>
        Sta.Run(() =>
        {
            using var form = NewForm();
            int closing = 0;
            form.FormClosing += (_, _) => closing++;

            var e = RaiseWebKeyDown(form, Keys.Alt | Keys.C);

            Assert.True(e.Handled); // Review Focus 2: ブラウザへ渡さない
            Assert.Equal(1, closing);
        });

    // Review Focus 1: 本文のコピー(Ctrl+C)や、似たキーでは閉じない。
    [Theory]
    [InlineData(Keys.Control | Keys.C)]
    [InlineData(Keys.C)]
    [InlineData(Keys.Alt | Keys.Shift | Keys.C)]
    [InlineData(Keys.Alt | Keys.X)]
    public void Other_keys_are_left_to_WebView2(Keys keyData) =>
        Sta.Run(() =>
        {
            using var form = NewForm();
            int closing = 0;
            form.FormClosing += (_, _) => closing++;

            var e = RaiseWebKeyDown(form, keyData);

            Assert.False(e.Handled);
            Assert.Equal(0, closing);
        });
}
