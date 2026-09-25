using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using kxEdit.Core.Editing;
using kxEdit.Core.Settings;

namespace kxEdit.Editor.Tests;

/// <summary>
/// 2026-09-25 性能改善フェーズ 3 のオラクル(設計書 §8.4)。FrameInputs 同士の比較だけでは、
/// 「描画が読む状態が FrameInputs に入っていない」故障を検出できない(等しければ出力は自明に一致する)。
/// そこで「画面の絵」を模したビットマップを持ち、Invalidate が 1 回以上起きた操作の後だけ描き直す。
/// 各操作の後に、今の状態から描いた絵(正解)と画素で比べる。Invalidate を省いた時点で差があれば、
/// 実画面に古い絵が残る不具合である。フレームではなく画素で比べるのは、RenderFrame のシフトと
/// IME の未確定表示(Frame の外で描く)も含めるため(実装計画 §0.2)。
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

    private static int[] Pixels(Bitmap bmp)
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

    private static int[] Paint(EditorControl c, bool record)
    {
        using var bmp = EditorControl.TestHook_PaintToBitmap(c, record);
        return Pixels(bmp);
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
                int[] screen = Paint(c, record: true);
                int skipped = 0;
                for (int step = 0; step < 200; step++)
                {
                    var (name, act) = PickOp(rng, c);
                    log.Add(name);
                    int invalidated = 0;
                    InvalidateEventHandler h = (_, _) => invalidated++;
                    c.Invalidated += h;
                    try
                    {
                        act();
                    }
                    finally
                    {
                        c.Invalidated -= h;
                    }
                    if (invalidated > 0)
                        screen = Paint(c, record: true); // WM_PAINT が来た
                    else
                        skipped++;
                    int[] truth = Paint(c, record: false);
                    Assert.True(
                        screen.AsSpan().SequenceEqual(truth),
                        $"seed={seed} step={step}: 古い絵が残る。直前の操作: {string.Join(" → ", log.TakeLast(8))}"
                    );
                }
                // 前提: 省略の経路を実際に通っている(通らなければこのテストは何も確かめていない)。
                Assert.True(skipped >= 20, $"seed={seed}: 省略が {skipped} 回しか起きていない");
            }
        });

    /// <summary>
    /// 操作の抽選。キャレット移動(省略されうる)を厚めに、描画の入力を変える操作を一通り混ぜる。
    /// 描画の入力を足したら、それを変える操作もここに足すこと。
    /// </summary>
    private static (string Name, Action Act) PickOp(Random rng, EditorControl c)
    {
        var snap = c.CurrentBuffer.Current;
        int len = snap.CharLength;
        int Rand() => rng.Next(0, len + 1);
        int caret = c.CaretCharOffset;
        switch (rng.Next(0, 22))
        {
            case 0:
            case 1:
            case 2:
                return ("→", () => c.SetCaretCharOffset(Math.Min(len, caret + 1)));
            case 3:
            case 4:
                return ("←", () => c.SetCaretCharOffset(Math.Max(0, caret - 1)));
            case 5:
            case 6:
            {
                int line = snap.GetLineIndexOfChar(caret);
                int to = snap.GetLineStart(Math.Min(snap.LineCount - 1, line + 1));
                return ("↓", () => c.SetCaretCharOffset(to));
            }
            case 7:
                return ("任意の位置", () => c.SetCaretCharOffset(Rand()));
            case 8:
                return (
                    "Shift+移動",
                    () => c.MoveCaretWithSelection(Math.Min(len, caret + rng.Next(1, 8)))
                );
            case 9:
            {
                int a = Rand(),
                    b = Rand();
                return ("範囲選択", () => c.SetSelectionCharRange(a, b));
            }
            case 10:
            {
                int p = Rand();
                return ("空の選択(anchored)", () => c.SetSelectionAnchored(p, p));
            }
            case 11:
                return ("TopLine", () => c.TopLine = rng.Next(0, snap.LineCount));
            case 12:
                return ("ScrollX", () => c.ScrollX = rng.Next(0, 400));
            case 13:
                return ("現在行強調", () => c.HighlightCurrentLine = !c.HighlightCurrentLine);
            case 14:
                return ("行番号", () => c.ShowLineNumbers = !c.ShowLineNumbers);
            case 15:
                return ("空白表示", () => c.ShowWhitespace = !c.ShowWhitespace);
            case 16:
                return ("折り返し", () => c.WrapColumns = c.WrapColumns == 0 ? 24 : 0);
            case 17:
            {
                int s = Rand();
                return rng.Next(2) == 0
                    ? ("セル強調", () => c.HighlightCharRange(s, rng.Next(0, 6)))
                    : ("セル強調を消す", c.ClearHighlight);
            }
            case 18:
                return ("1 文字挿入", () => c.ReplaceCharRange(caret, 0, "x"));
            case 19:
                return ("Undo", c.Undo);
            case 20:
                return c.__TestIsComposing()
                    ? ("IME 確定", () => c.__TestApplyResult("漢字"))
                    : (
                        "IME 未確定",
                        () =>
                            c.__TestApplyComposition(
                                "かな",
                                1,
                                [ImeAttribute.Input, ImeAttribute.Input],
                                []
                            )
                    );
            default:
            {
                bool dark = rng.Next(2) == 0;
                bool hl = rng.Next(2) == 0;
                return (
                    "外観",
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
