using System.Drawing;

namespace kxEdit.Editor.Tests;

/// <summary>
/// 2026-09-24 性能改善フェーズ 1(設計書 §6)。描画の固定費を削った変更が、
/// 観測できる挙動(Text・アクセシブル名・描画の配送)を変えていないことを固定する。
/// </summary>
public class EditorControlPaintCostTests
{
    private static (Form F, EditorControl C) MakeHosted()
    {
        var f = new Form { Size = new Size(400, 200) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString("hello\r\nあいう\r\n"));
        return (f, c);
    }

    /// <summary>
    /// P-24: 描画 1 回ごとに WinForms の PaintWithErrorHandling が WindowText を読み、
    /// 自 HWND に WM_GETTEXTLENGTH / WM_GETTEXT を送っていた(結果は常に "")。CacheText で止める。
    /// 画面外の窓には WM_PAINT が来ないので、WM_PRINTCLIENT 経由(DrawToBitmap)で同じ
    /// PaintWithErrorHandling を通す。
    /// </summary>
    [Fact]
    public void Painting_does_not_query_its_own_window_text() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                int paints = 0;
                c.Paint += (_, _) => paints++;
                using var bmp = new Bitmap(c.Width, c.Height);
                EditorControl.TestHook_ResetGetTextCount(c);
                c.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
                Assert.True(paints >= 1, "描画が起きていない(前提の崩れ)");
                Assert.Equal(0, EditorControl.TestHook_GetTextCount(c));
            }
        });

    /// <summary>base の Control.Text は "" のまま(CacheText 下では _text ?? "" を返す)。</summary>
    [Fact]
    public void Base_Control_Text_stays_empty() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                Assert.Equal(string.Empty, ((Control)c).Text);
            }
        });

    /// <summary>MSAA(OBJID_CLIENT)の名前の出所 = AccessibilityObject.Name も変わらないこと。</summary>
    [Fact]
    public void Msaa_name_stays_empty() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                Assert.True(string.IsNullOrEmpty(c.AccessibilityObject.Name));
            }
        });
}
