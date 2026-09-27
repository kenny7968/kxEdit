using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using kxEdit.Core.Editing;
using kxEdit.Core.Settings;
using kxEdit.Editor.Tests.Fakes;

namespace kxEdit.Editor.Tests;

/// <summary>
/// 2026-09-25 性能改善フェーズ 3 のオラクル(設計書 §8.4)。FrameInputs 同士の比較だけでは、
/// 「描画が読む状態が FrameInputs に入っていない」故障を検出できない(等しければ出力は自明に一致する)。
/// そこで「画面の絵」を模したビットマップを持ち、無効化が起きた操作の後だけ、無効化した矩形の中を描き直す
/// (フェーズ 9: WM_PAINT の更新領域を模して矩形を合成する。<c>Composite</c>)。
/// 各操作の後に、今の状態から描いた絵(正解)と画素で比べる。無効化を省いた・足りなかった時点で差があれば、
/// 実画面に古い絵が残る不具合である。フレームではなく画素で比べるのは、RenderFrame のシフトと
/// IME の未確定表示(Frame の外で描く)も含めるため(実装計画 §0.2)。
/// 検出範囲: Invalidate を省きうる経路(4 経路)と、行の帯だけを無効化する経路(4 経路・編集・IME・セル強調)で、
/// 無効化の不足があれば検出する。フェーズ 9b: スクロールのセッターは画素を移すので、偽の画面(<see cref="Fakes.ScreenSurface"/>)で
/// 画素を実際に動かし、露出した帯と差の行の無効化の不足も検出する(画面外の Form では hscroll が出ず横のスクロールは no-op なので、確かめるのは縦だけ。横は ScrollPixelsTests)。
/// 無条件に全面を Invalidate するセッターの裏にある状態(ShowWhitespace など)は、漏れても古い絵にならないので対象外。
/// 描画が生の状態を読む故障は PaintBody が static であることで、比較の漏れは FrameInputsTests で防ぐ。
/// </summary>
public class SkipInvalidateOracleTests
{
    private static string Body()
    {
        var lines = new List<string>();
        for (int i = 0; i < 60; i++)
        {
            lines.Add(
                i % 9 == 0 ? ""
                : i == 7 ? string.Concat(Enumerable.Range(0, 20).Select(k => $"[{k:D2}]-long-"))
                : i % 2 == 0 ? $"{i:D2} 吾輩は猫である。\t名前はまだ無い。"
                : $"{i:D2} mixed 混在 𠮷 text"
            );
        }
        return string.Join("\r\n", lines);
    }

    private static (Form F, EditorControl C) MakeHosted()
    {
        var f = new Form { Size = new Size(420, 220) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(Body()));
        return (f, c);
    }

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

    internal static int[] Paint(EditorControl c, bool record)
    {
        using var bmp = EditorControl.TestHook_PaintToBitmap(c, record);
        return Pixels(bmp);
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

    /// <summary>オラクルが「古い絵」を検出できること(画素比較が自明に一致しないことの陽性対照)。</summary>
    [Fact]
    public void Oracle_detects_a_stale_picture() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(c.CurrentBuffer.Current.GetLineStart(2) + 3);
                int[] screen = Paint(c, record: true);
                c.ShowWhitespace = true; // Invalidate はされるが、ここでは描き直さない
                Assert.NotEqual(screen, Paint(c, record: false));
            }
        });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Skipped_invalidations_never_leave_a_stale_picture(int seed) =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                var rng = new Random(seed);
                var log = new List<string>();
                // フェーズ 9b: 画素の移動は偽の画面で受け、画面の配列をそれと共有する
                // (TryScroll は Pixels の中身を書き換える = screen と同じ配列を指し続ける)。
                var surface = new ScreenSurface(c.ClientSize.Width, c.ClientSize.Height);
                EditorControl.TestHook_SetPaintSurface(c, surface);
                surface.Pixels = Paint(c, record: true);
                int[] screen = surface.Pixels;
                int skipped = 0;
                int partial = 0;
                // フェーズ 9b: スクロールの操作(26〜30)を足して抽選が 26 → 31 通りになったので、
                // キャレット・選択の操作の回数を保つため 200 → 300 手にする(下の前提の閾値は変えない)。
                for (int step = 0; step < 300; step++)
                {
                    var (name, skippable, act) = PickOp(rng, c);
                    log.Add(name);
                    int caretBefore = c.CaretCharOffset;
                    int anchorBefore = c.SelectionAnchor;
                    var rects = new List<Rectangle>();
                    // 前の操作の無効化は下の Composite で描き直した = 保留中の無効領域はない。
                    surface.Pending = false;
                    InvalidateEventHandler h = (_, e) =>
                    {
                        rects.Add(e.InvalidRect);
                        surface.Pending = true;
                    };
                    c.Invalidated += h;
                    try
                    {
                        act();
                    }
                    finally
                    {
                        c.Invalidated -= h;
                    }
                    if (rects.Count > 0)
                    {
                        Composite(c, screen, rects); // WM_PAINT: 無効化した矩形の中だけが描き直される
                        if (!rects.Any(r => r.Contains(c.ClientRectangle)))
                            partial++;
                    }
                    else if (
                        skippable
                        && (c.CaretCharOffset != caretBefore || c.SelectionAnchor != anchorBefore)
                    )
                        skipped++; // 省略の経路を通った(4 経路で、位置が動いたのに描き直していない)
                    int[] truth = Paint(c, record: false);
                    var diff = DiffBounds(c, screen, truth);
                    if (!diff.IsEmpty)
                    {
                        Assert.Fail(
                            $"seed={seed} step={step}: 古い絵が残る(差の外接矩形 {diff})。"
                                + $"直前の操作: {string.Join(" → ", log.TakeLast(8))}"
                        );
                    }
                }
                // 前提: 省略の経路を実際に通っている(通らなければこのテストは何も確かめていない)。
                // 数えるのは、キャレット・選択の 4 経路の操作で、キャレットかアンカーが実際に動き、
                // かつ Invalidate が 0 回だったときだけ(早期 return の no-op は数えない)。
                Assert.True(skipped >= 20, $"seed={seed}: 省略が {skipped} 回しか起きていない");
                // 前提: 部分的な無効化の経路を実際に通っている。
                Assert.True(
                    partial >= 20,
                    $"seed={seed}: 部分的な無効化が {partial} 回しか起きていない"
                );
                // 前提: 画素の移動(フェーズ 9b)の経路を実際に通っている。
                Assert.True(
                    surface.Scrolls >= 5,
                    $"seed={seed}: 画素の移動が {surface.Scrolls} 回しか起きていない"
                );
            }
        });

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

    /// <summary>陽性対照: 無効化した矩形の一部を合成しなければ、古い絵として検出される。</summary>
    [Fact]
    public void Oracle_detects_a_missing_rectangle() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.HighlightCurrentLine = true;
                c.SetCaretCharOffset(c.CurrentBuffer.Current.GetLineStart(2) + 3);
                int[] screen = Paint(c, record: true);
                var rects = new List<Rectangle>();
                InvalidateEventHandler h = (_, e) => rects.Add(e.InvalidRect);
                c.Invalidated += h;
                c.SetCaretCharOffset(c.CurrentBuffer.Current.GetLineStart(5) + 1);
                c.Invalidated -= h;
                Assert.True(rects.Count >= 2, "前提: 旧行と新行の 2 つの帯が無効化される");
                Composite(c, screen, [rects[^1]]); // 旧行の帯を捨てる
                Assert.False(screen.AsSpan().SequenceEqual(Paint(c, record: false)));
            }
        });

    /// <summary>陽性対照: 画素を移した後に露出した帯を描かなければ、古い絵として検出される。</summary>
    [Fact]
    public void Oracle_detects_an_unpainted_exposed_band() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                var surface = new ScreenSurface(c.ClientSize.Width, c.ClientSize.Height);
                EditorControl.TestHook_SetPaintSurface(c, surface);
                c.TopLine = 5;
                surface.Pixels = Paint(c, record: true);
                c.TopLine = 6;
                Assert.Equal(1, surface.Scrolls); // 前提: 画素を移した
                Assert.False(SameOnScreen(c, surface.Pixels, Paint(c, record: false)));
            }
        });

    /// <summary>
    /// 操作の抽選。キャレット移動(省略されうる)を厚めに、描画の入力を変える操作を一通り混ぜる。
    /// 描画の入力を足したら、それを変える操作もここに足すこと。
    /// <c>Skippable</c> は、Invalidate を省きうるキャレット・選択の 4 経路
    /// (SetCaretCharOffset / SetSelectionCharRange / MoveCaretWithSelection / SetSelectionAnchored)の操作なら true。
    /// </summary>
    private static (string Name, bool Skippable, Action Act) PickOp(Random rng, EditorControl c)
    {
        var snap = c.CurrentBuffer.Current;
        int len = snap.CharLength;
        int Rand() => rng.Next(0, len + 1);
        int caret = c.CaretCharOffset;
        switch (rng.Next(0, 31))
        {
            case 0:
            case 1:
            case 2:
                return ("→", true, () => c.SetCaretCharOffset(Math.Min(len, caret + 1)));
            case 3:
            case 4:
                return ("←", true, () => c.SetCaretCharOffset(Math.Max(0, caret - 1)));
            case 5:
            case 6:
            {
                int line = snap.GetLineIndexOfChar(caret);
                int to = snap.GetLineStart(Math.Min(snap.LineCount - 1, line + 1));
                return ("↓", true, () => c.SetCaretCharOffset(to));
            }
            case 7:
                return ("任意の位置", true, () => c.SetCaretCharOffset(Rand()));
            case 8:
            {
                // 前後両方向(後方の選択と、アンカーまで戻って空の選択になる遷移も踏む)。差 0 は避ける。
                int d = rng.Next(1, 8) * (rng.Next(2) == 0 ? -1 : 1);
                int to = Math.Clamp(caret + d, 0, len);
                return ("Shift+移動", true, () => c.MoveCaretWithSelection(to));
            }
            case 9:
            {
                int a = Rand(),
                    b = Rand();
                return ("範囲選択", true, () => c.SetSelectionCharRange(a, b));
            }
            case 10:
            {
                int a = Rand(),
                    b = Rand();
                if (rng.Next(2) == 0)
                    return ("空の選択(anchored)", true, () => c.SetSelectionAnchored(a, a));
                // 空でない非対称の選択(アンカーが後ろ = キャレットが選択先頭)。
                int lo = Math.Min(a, b);
                int hi = Math.Max(a, b) == lo ? Math.Min(len, lo + 1) : Math.Max(a, b);
                return ("後方の選択(anchored)", true, () => c.SetSelectionAnchored(hi, lo));
            }
            case 11:
                return ("TopLine", false, () => c.TopLine = rng.Next(0, snap.LineCount));
            case 12:
                return ("ScrollX", false, () => c.ScrollX = rng.Next(0, 400));
            case 13:
                return (
                    "現在行強調",
                    false,
                    () => c.HighlightCurrentLine = !c.HighlightCurrentLine
                );
            case 14:
                return ("行番号", false, () => c.ShowLineNumbers = !c.ShowLineNumbers);
            case 15:
                return ("空白表示", false, () => c.ShowWhitespace = !c.ShowWhitespace);
            case 16:
                return ("折り返し", false, () => c.WrapColumns = c.WrapColumns == 0 ? 24 : 0);
            case 17:
            {
                int s = Rand();
                return rng.Next(2) == 0
                    ? ("セル強調", false, () => c.HighlightCharRange(s, rng.Next(0, 6)))
                    : ("セル強調を消す", false, c.ClearHighlight);
            }
            case 18:
                return ("1 文字挿入", false, () => c.ReplaceCharRange(caret, 0, "x"));
            case 19:
                return ("Undo", false, c.Undo);
            case 20:
                return c.__TestIsComposing()
                    ? ("IME 確定", false, () => c.__TestApplyResult("漢字"))
                    : (
                        "IME 未確定",
                        false,
                        () =>
                            c.__TestApplyComposition(
                                "かな",
                                1,
                                [ImeAttribute.Input, ImeAttribute.Input],
                                []
                            )
                    );
            case 22:
                return ("改行挿入", false, () => c.ReplaceCharRange(caret, 0, "\r\n"));
            case 23:
            {
                int s = Rand();
                int n = rng.Next(1, 12);
                return (
                    "範囲削除",
                    false,
                    () => c.ReplaceCharRange(s, Math.Min(n, len - Math.Min(s, len)), "")
                );
            }
            case 24:
                return c.__TestIsComposing()
                    ? (
                        "IME 更新",
                        false,
                        () => c.__TestApplyComposition("かなかな", 2, [0, 0, 0, 0], [])
                    )
                    : (
                        "セル強調を隣の行へ",
                        false,
                        () =>
                            c.HighlightCharRange(
                                snap.GetLineStart(
                                    Math.Min(snap.LineCount - 1, snap.GetLineIndexOfChar(caret) + 1)
                                ),
                                3
                            )
                    );
            case 25:
                return (
                    "1 文字削除",
                    false,
                    () => c.ReplaceCharRange(Math.Max(0, caret - 1), caret > 0 ? 1 : 0, "")
                );
            case 26:
            case 27:
            {
                int d = rng.Next(1, 4) * (rng.Next(2) == 0 ? -1 : 1);
                return (
                    "小スクロール(縦)",
                    false,
                    () => c.TopLine = Math.Clamp(c.TopLine + d, 0, snap.LineCount - 1)
                );
            }
            case 28:
            {
                int d = rng.Next(1, 30) * (rng.Next(2) == 0 ? -1 : 1);
                return ("小スクロール(横)", false, () => c.ScrollX = Math.Max(0, c.ScrollX + d));
            }
            case 29:
                return (
                    "視覚行スクロール",
                    false,
                    () => c.SetTopPosition(c.TopLine, rng.Next(0, 3))
                );
            case 30:
            {
                // 一時的にキャレットを動かして追従スクロールし、戻す経路(Review Focus 1)。
                // 半分は可視域のすぐ外(数行の画素の移動になる)、半分は任意の位置。
                int visible = c.ClientSize.Height / Math.Max(1, c.Metrics.LineHeightPx);
                int near =
                    rng.Next(2) == 0
                        ? Math.Min(snap.LineCount - 1, c.TopLine + visible + rng.Next(0, 3))
                        : Math.Max(0, c.TopLine - rng.Next(1, 4));
                int target = rng.Next(2) == 0 ? snap.GetLineStart(near) : Rand();
                return ("EnsureVisible", false, () => c.EnsureVisibleCharRange(target, 0));
            }
            default:
            {
                bool dark = rng.Next(2) == 0;
                bool hl = rng.Next(2) == 0;
                return (
                    "外観",
                    false,
                    () =>
                        c.ApplyAppearance(
                            new AppSettings
                            {
                                Theme = dark ? "white-on-black" : "default",
                                HighlightCurrentLine = hl,
                            }
                        )
                );
            }
        }
    }
}
