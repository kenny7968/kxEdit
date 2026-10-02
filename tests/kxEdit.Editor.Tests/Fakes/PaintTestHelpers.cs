using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace kxEdit.Editor.Tests.Fakes;

/// <summary>
/// 描画テストの共通の補助(2026-09-27 申し送り回収 フェーズ 6・項目 21)。
/// 画素の取り出し(<see cref="Pixels"/>・<see cref="PaintPixels"/>)、描画の記録(<see cref="PaintRecorded"/>・
/// <see cref="PaintAndAssumeRecorded"/>)、無効化の矩形の採取(<see cref="Rects"/>)、
/// WM_PAINT の模倣(<see cref="Composite"/>)と画面に見える範囲の比較(<see cref="DiffBounds"/>)。
/// 描画は <c>EditorControl.TestHook_PaintToBitmap</c> で起こす(画面外の窓には WM_PAINT が来ないため)。
/// </summary>
internal static class PaintTestHelpers
{
    internal static int[] Pixels(Bitmap bmp)
    {
        var data = bmp.LockBits(
            new Rectangle(Point.Empty, bmp.Size),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb
        );
        try
        {
            var px = new int[bmp.Width * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + (y * data.Stride), px, y * bmp.Width, bmp.Width);
            return px;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>今の状態を描いて画素を返す。<paramref name="record"/> が true なら描画を記録する(画面の絵の入力になる)。</summary>
    internal static int[] PaintPixels(EditorControl c, bool record)
    {
        using var bmp = EditorControl.TestHook_PaintToBitmap(c, record);
        return Pixels(bmp);
    }

    /// <summary>描画を記録するためだけに描く(絵は捨てる)。</summary>
    internal static void PaintRecorded(EditorControl c) =>
        EditorControl.TestHook_PaintToBitmap(c, record: true).Dispose();

    /// <summary>前提: 描画の記録がある状態から始める(記録がなければ比較は必ず「変化あり」)。</summary>
    internal static void PaintAndAssumeRecorded(EditorControl c)
    {
        PaintRecorded(c);
        Assert.True(EditorControl.TestHook_HasLastPaintedInputs(c), "前提: 描画が記録されていない");
    }

    /// <summary><paramref name="act"/> の間に起きた無効化の矩形(Control.Invalidated の InvalidRect)を順に返す。</summary>
    internal static List<Rectangle> Rects(EditorControl c, Action act)
    {
        var rects = new List<Rectangle>();
        InvalidateEventHandler h = (_, e) => rects.Add(e.InvalidRect);
        c.Invalidated += h;
        try
        {
            act();
        }
        finally
        {
            c.Invalidated -= h;
        }
        return rects;
    }

    /// <summary>
    /// 画面に見える範囲 = クライアントのうちスクロールバー(子ウィンドウ)に隠れない部分。
    /// VScrollBar は製品で常に表示される(Visible を落とす経路がない)ので、画面外の Form で Visible が false でも除く。
    /// HScrollBar は実際に表示されているときだけ除く。
    /// フェーズ 9b: 画素の移動(ScrollWindowEx)はこの範囲だけを動かすので、隠れた部分の画素は比べない
    /// (実画面では子ウィンドウが覆っていて見えない)。
    /// </summary>
    private static Rectangle OnScreen(EditorControl c)
    {
        var r = c.ClientRectangle;
        foreach (Control child in c.Controls)
        {
            if (child is VScrollBar)
                r.Width = Math.Min(r.Width, child.Left);
            else if (child is HScrollBar && child.Visible)
                r.Height = Math.Min(r.Height, child.Top);
        }
        return r;
    }

    /// <summary>画面に見える範囲(<see cref="OnScreen"/>)の画素が等しいか。</summary>
    internal static bool SameOnScreen(EditorControl c, int[] a, int[] b) =>
        DiffBounds(c, a, b).IsEmpty;

    /// <summary>画面に見える範囲で画素が違う部分の外接矩形(等しければ空)。失敗の診断にも使う。</summary>
    internal static Rectangle DiffBounds(EditorControl c, int[] a, int[] b)
    {
        var view = OnScreen(c);
        int width = c.ClientSize.Width;
        int l = int.MaxValue,
            t = int.MaxValue,
            r = -1,
            btm = -1;
        for (int y = view.Top; y < view.Bottom; y++)
        {
            for (int x = view.Left; x < view.Right; x++)
            {
                int i = y * width + x;
                if (a[i] == b[i])
                    continue;
                l = Math.Min(l, x);
                t = Math.Min(t, y);
                r = Math.Max(r, x);
                btm = Math.Max(btm, y);
            }
        }
        return r < 0 ? Rectangle.Empty : Rectangle.FromLTRB(l, t, r + 1, btm + 1);
    }

    /// <summary>
    /// WM_PAINT を模す: 無効化した矩形の外接矩形をクリップにして描き(実際の e.ClipRectangle と同じ)、
    /// 矩形の和の中の画素だけを画面へ写す(実際の DC は更新領域でクリップされている)。
    /// </summary>
    internal static void Composite(EditorControl c, int[] screen, List<Rectangle> rects)
    {
        var client = c.ClientRectangle;
        var bounds = Rectangle.Empty;
        foreach (var r in rects)
            bounds = bounds.IsEmpty ? r : Rectangle.Union(bounds, r);
        bounds.Intersect(client);
        if (bounds.IsEmpty)
            return;
        using var bmp = EditorControl.TestHook_PaintToBitmap(c, record: true, bounds);
        int[] px = Pixels(bmp);
        int w = bmp.Width;
        foreach (var r0 in rects)
        {
            var r = Rectangle.Intersect(r0, client);
            for (int y = r.Top; y < r.Bottom; y++)
            {
                Array.Copy(px, y * w + r.Left, screen, y * w + r.Left, r.Width);
            }
        }
    }
}
