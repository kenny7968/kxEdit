using System.Drawing;
using kxEdit.Core.Editing;
using kxEdit.Core.Settings;
using kxEdit.Editor.Tests.Fakes;

namespace kxEdit.Editor.Tests;

/// <summary>
/// フェーズ 9(設計書 §14.1・計画 §0.2): クリップして描いた絵は、クリップの内側で全面の絵と画素まで一致する。
/// WM_PAINT の DC は更新領域でクリップされているので、内側さえ正しければ画面は正しい。
/// 行をまたぐ描画(セル強調枠の下辺・IME の未確定表示・RenderFrame のシフト)も含めて確かめる。
/// </summary>
public class ClipPaintTests
{
    private static string Body() =>
        string.Join(
            "\r\n",
            Enumerable
                .Range(0, 40)
                .Select(i =>
                    i == 5
                        ? string.Concat(Enumerable.Range(0, 20).Select(k => $"[{k:D2}]-long-"))
                        : $"{i:D2} 吾輩は猫である。\t名前は まだ無い。 mixed 𠮷"
                )
        );

    private static (Form F, EditorControl C) MakeHosted()
    {
        var f = new Form { Size = new Size(420, 260) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(Body()));
        return (f, c);
    }

    private static int Line(EditorControl c, int line) =>
        c.CurrentBuffer.Current.GetLineStart(line);

    /// <summary>状態の組み合わせ(描画の各工程が 1 つ以上効くように選ぶ)。</summary>
    private static void Arrange(EditorControl c, int state)
    {
        switch (state)
        {
            case 0: // 既定
                c.SetCaretCharOffset(Line(c, 2) + 3);
                break;
            case 1: // 現在行強調 + 行番号 + 空白表示
                c.ApplyAppearance(
                    new AppSettings
                    {
                        HighlightCurrentLine = true,
                        ShowLineNumbers = true,
                        ShowWhitespace = true,
                    }
                );
                c.SetCaretCharOffset(Line(c, 3) + 1);
                break;
            case 2: // 複数行の選択(黒地テーマ = 選択文字色で本文 op を分割)
                c.ApplyAppearance(new AppSettings { Theme = "white-on-black" });
                c.SetSelectionCharRange(Line(c, 1) + 4, Line(c, 4) + 2);
                break;
            case 3: // セル強調(行 3)
                c.HighlightCharRange(Line(c, 3) + 2, 5);
                break;
            case 4: // IME 未確定(行 2)
                c.SetCaretCharOffset(Line(c, 2) + 4);
                c.__TestApplyComposition(
                    "にほんご",
                    2,
                    [
                        ImeAttribute.Input,
                        ImeAttribute.TargetConverted,
                        ImeAttribute.TargetConverted,
                        ImeAttribute.Input,
                    ],
                    [0, 1, 3, 4]
                );
                break;
            case 5: // 折り返し ON + 選択
                c.WrapColumns = 24;
                c.SetSelectionCharRange(Line(c, 5) + 10, Line(c, 5) + 70);
                break;
            case 6: // 水平スクロール + 行番号
                c.ShowLineNumbers = true;
                // ScrollX の setter は _hscroll.Visible が false だと no-op になり、その Visible は
                // 祖先(Form)の表示状態にも連動する。未 Show の Form では常に false になるため、
                // この状態でだけ Form を表示して水平スクロールバーを実際に効かせる
                // (UiaScrollIntoViewTests.cs の同種コメント参照。描画は Bitmap への
                // TestHook_PaintToBitmap 経由なので、Form を画面に出しても比較結果には影響しない)。
                c.FindForm()!.Show();
                c.ScrollX = 60;
                break;
        }
    }

    public static TheoryData<int> States() => [0, 1, 2, 3, 4, 5, 6];

    [Theory]
    [MemberData(nameof(States))]
    public void Clipped_paint_matches_full_paint_inside_the_clip(int state) =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                Arrange(c, state);
                if (state == 6)
                    Assert.True(c.ScrollX > 0, "前提: 水平スクロールが効いている");
                using var full = EditorControl.TestHook_PaintToBitmap(c, record: false);
                int[] truth = PaintTestHelpers.Pixels(full);
                int w = full.Width,
                    h = full.Height;
                int lh = c.Metrics.LineHeightPx;
                var clips = new List<Rectangle>();
                for (int i = 0; i * lh < h; i++)
                    clips.Add(new Rectangle(0, i * lh, w, lh)); // 行にそろった帯
                clips.Add(new Rectangle(0, 4 * lh, w, 1)); // 行 3 の枠の下辺だけ(状態 3)
                clips.Add(new Rectangle(0, 3 * lh + lh - 1, w, 2)); // 行の境目の 2 画素
                var rng = new Random(state);
                for (int k = 0; k < 20; k++)
                {
                    int x = rng.Next(0, w - 1),
                        y = rng.Next(0, h - 1);
                    clips.Add(new Rectangle(x, y, rng.Next(1, w - x + 1), rng.Next(1, h - y + 1)));
                }
                foreach (var clip in clips)
                {
                    using var part = EditorControl.TestHook_PaintToBitmap(c, record: true, clip);
                    int[] px = PaintTestHelpers.Pixels(part);
                    for (int y = clip.Top; y < Math.Min(h, clip.Bottom); y++)
                    {
                        for (int x = clip.Left; x < Math.Min(w, clip.Right); x++)
                        {
                            Assert.True(
                                px[y * w + x] == truth[y * w + x],
                                $"state={state} clip={clip}: ({x},{y}) が全面の絵と違う"
                            );
                        }
                    }
                }
            }
        });

    /// <summary>陽性対照: クリップの外は描かれない(クリップが実際に効いている)。</summary>
    [Fact]
    public void Pixels_outside_the_clip_are_not_painted() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                int lh = c.Metrics.LineHeightPx;
                using var part = EditorControl.TestHook_PaintToBitmap(
                    c,
                    record: true,
                    new Rectangle(0, lh, 50, lh)
                );
                Assert.Equal(0, part.GetPixel(200, 5 * lh).ToArgb()); // 透明のまま(Format32bppArgb の初期値)
            }
        });
}
