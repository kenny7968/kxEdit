using System.Linq;
using yEdit.Core.Buffers;
using yEdit.Core.Layout;
using yEdit.Editor;

namespace yEdit.Editor.Tests;

/// <summary>
/// 2026-08-02 巨大 1 行対応(変更 B-2)。ComputeCaretPoint が Wrap を打ち切るように
/// なったことで、EOL キャレット(最終セグメント末尾ちょうど)の判定が壊れていないことを守る。
/// 打ち切ると「返された最後のセグメント」が論理行の最終セグメントとは限らなくなるため、
/// ReachedLineEnd を見ずに実装すると行末キャレットが 1 行下(または左端)へずれる。
///
/// もう 1 つの守備範囲が「積み上げループ」の可視判定。TopLine ～ キャレット行の各論理行を
/// 「まだ意味のある視覚行数」までで打ち切って数えるため、その上限を 1 でも小さく取ると
/// 視覚行数を過小に見積もり、本来不可視の位置を可視と誤判定する。
/// </summary>
public class EditorControlWrapCaretTests
{
    // 文字種の識別子。InlineData には ASCII 文字列だけを置く
    // (xUnit v2 は InlineData の string を直列化して test case ID を作るため、
    //  非 BMP を直接並べると ID が衝突して片方が黙ってスキップされる)。
    private const string Ascii = "ascii";
    private const string Cjk = "cjk";
    private const string Emoji = "emoji";
    private const string Mixed = "mixed";

    /// <summary>
    /// 可視高さを明示したエディタを作る。折り返し ON では HScrollBar が常に隠れるため
    /// <c>PaintHeightPx == ClientSize.Height</c> になる=可視行数をテストから固定できる。
    /// </summary>
    private static (Form f, EditorControl c) MakeControl(
        string text,
        int wrap,
        int visibleRows,
        int extraPx = 0
    )
    {
        var f = new HostForm();
        var c = new EditorControl { WrapColumns = wrap };
        f.Controls.Add(c);
        _ = f.Handle;
        c.ClientSize = new System.Drawing.Size(800, c.LineHeightPx * visibleRows + extraPx);
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    /// <summary>
    /// 指定した文字種で <paramref name="codePoints"/> コードポイント分の 1 行を作る。
    /// ASCII 固定幅だけだとセグメント境界が必ず同じ倍数に落ちて cpLen も常に 1 になり、
    /// 「打ち切り位置がコードポイント境界に乗る」性質が試されない(Task 3 の教訓)。
    /// </summary>
    private static string LineOf(string kind, int codePoints)
    {
        if (kind == Ascii)
            return new string('a', codePoints);

        (string unit, int unitCodePoints) = kind switch
        {
            Cjk => ("あ", 1),
            Emoji => ("\U0001F600", 1),
            Mixed => ("a\U0001F600あb", 4),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知の文字種"),
        };

        var sb = new System.Text.StringBuilder();
        for (int n = 0; n < codePoints; n += unitCodePoints)
            sb.Append(unit);
        return sb.ToString();
    }

    /// <summary>
    /// LineLayout とは独立に書いた折り返しの参照実装(yEdit.Core の internal は
    /// Editor.Tests から見えないため。期待値の出どころを本番実装に頼らない)。
    /// 返すのは (セグメント開始オフセット, 長さ) の列。
    /// </summary>
    private static List<(int Start, int Length)> ReferenceWrap(
        string line,
        int maxWidthPx,
        ICharMetrics metrics
    )
    {
        var segs = new List<(int Start, int Length)>();
        if (maxWidthPx <= 0 || line.Length == 0)
        {
            segs.Add((0, line.Length));
            return segs;
        }

        int segStart = 0;
        int segWidth = 0;
        int i = 0;
        while (i < line.Length)
        {
            int cpLen =
                char.IsHighSurrogate(line[i])
                && i + 1 < line.Length
                && char.IsLowSurrogate(line[i + 1])
                    ? 2
                    : 1;
            int cpWidth = metrics.MeasureRun(line.AsSpan(i, cpLen));
            if (segWidth > 0 && segWidth + cpWidth > maxWidthPx)
            {
                segs.Add((segStart, i - segStart));
                segStart = i;
                segWidth = 0;
            }
            segWidth += cpWidth;
            i += cpLen;
        }
        segs.Add((segStart, line.Length - segStart));
        return segs;
    }

    /// <summary>offsetInLine を含むセグメントの index(行末は最終セグメント扱い)。</summary>
    private static int RowOf(List<(int Start, int Length)> segs, int offsetInLine)
    {
        for (int i = 0; i < segs.Count; i++)
        {
            if (offsetInLine < segs[i].Start + segs[i].Length || i == segs.Count - 1)
                return i;
        }
        throw new InvalidOperationException("segs が空");
    }

    private static List<(int Start, int Length)> WrapOf(EditorControl c, string line, int wrap) =>
        ReferenceWrap(line, wrap * c.Metrics.MeasureRun("0"), c.Metrics);

    [Fact]
    public void Caret_at_end_of_a_wrapped_line_stays_on_the_last_visual_row() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(new string('a', 35), 10, visibleRows: 20);
            using (f)
            using (c)
            {
                var segs = WrapOf(c, new string('a', 35), 10);
                int lastRowStart = segs[^1].Start;

                var pEnd = c.PointFromCharOffset(35);
                var pLastRowStart = c.PointFromCharOffset(lastRowStart);

                Assert.Equal(pLastRowStart.Y, pEnd.Y);
                Assert.True(pEnd.X > pLastRowStart.X, $"pEnd.X={pEnd.X} > {pLastRowStart.X}");
            }
        });

    [Fact]
    public void Caret_at_a_segment_boundary_is_on_the_next_visual_row() =>
        Sta.Run(() =>
        {
            // セグメント境界ちょうどは「次の視覚行の先頭」であって「前の視覚行の末尾」ではない
            // (最終セグメント以外は末尾ちょうどを許容しない)。
            var (f, c) = MakeControl(new string('a', 35), 10, visibleRows: 20);
            using (f)
            using (c)
            {
                var segs = WrapOf(c, new string('a', 35), 10);
                int boundary = segs[1].Start;

                var pRow0 = c.PointFromCharOffset(0);
                var pBoundary = c.PointFromCharOffset(boundary);

                Assert.True(
                    pBoundary.Y > pRow0.Y,
                    $"pBoundary.Y={pBoundary.Y} > pRow0.Y={pRow0.Y}"
                );
                Assert.Equal(pRow0.X, pBoundary.X); // 行頭
            }
        });

    [Fact]
    public void Caret_positions_are_unchanged_across_a_long_wrapped_line() =>
        Sta.Run(() =>
        {
            string line = new string('a', 1000);
            var (f, c) = MakeControl(line, 10, visibleRows: 20);
            using (f)
            using (c)
            {
                var segs = WrapOf(c, line, 10);

                var p0 = c.PointFromCharOffset(segs[0].Start);
                var p1 = c.PointFromCharOffset(segs[1].Start);
                var p2 = c.PointFromCharOffset(segs[2].Start);

                Assert.Equal(p0.X, p1.X);
                Assert.Equal(p0.X, p2.X);
                Assert.Equal(p1.Y - p0.Y, p2.Y - p1.Y); // 等間隔 = 行高
                Assert.True(p1.Y > p0.Y);
            }
        });

    /// <summary>
    /// 打ち切りが「どのセグメントを選ぶか」を変えていないことを、行内の全コードポイント境界
    /// (行末含む)について参照実装と突き合わせて確認する。非 ASCII / サロゲートを通すのが要点。
    /// </summary>
    [Theory]
    [InlineData(Ascii)]
    [InlineData(Cjk)]
    [InlineData(Emoji)]
    [InlineData(Mixed)]
    public void Caret_row_and_x_match_a_reference_wrap_for_every_code_point(string kind) =>
        Sta.Run(() =>
        {
            string line = LineOf(kind, 60);
            var (f, c) = MakeControl(line, 10, visibleRows: 200);
            using (f)
            using (c)
            {
                var segs = WrapOf(c, line, 10);
                int lh = c.LineHeightPx;
                // 行番号マージン幅は offset 0(セグメント先頭)の X がそのまま示す。
                int lnWidth = c.ComputeCaretPoint(0).X;

                for (int offset = 0; offset <= line.Length; offset++)
                {
                    // コードポイント境界だけを見る(low サロゲート位置は前方スナップ対象で
                    // ここでの期待値計算が別の話になるため)。
                    if (offset < line.Length && char.IsLowSurrogate(line[offset]))
                        continue;

                    int row = RowOf(segs, offset);
                    var seg = segs[row];
                    var (x, y, visible) = c.ComputeCaretPoint(offset);

                    Assert.True(visible, $"kind={kind} offset={offset} が不可視");
                    Assert.Equal(row * lh, y);
                    Assert.Equal(
                        lnWidth + c.Metrics.MeasureRun(line.AsSpan(seg.Start, offset - seg.Start)),
                        x
                    );
                }
            }
        });

    /// <summary>
    /// 可視判定の境界。可視領域の最終行ちょうどにキャレットがあるときは可視、
    /// その 1 行下は不可視。積み上げループの上限をここより小さく取ると崩れる位置。
    /// </summary>
    [Fact]
    public void Caret_on_the_last_visible_row_is_visible_and_the_next_row_is_not() =>
        Sta.Run(() =>
        {
            // 各 1 視覚行の短い論理行を 10 本。可視は 3 行ちょうど。
            var text = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"L{i}"));
            var (f, c) = MakeControl(text, 10, visibleRows: 3);
            using (f)
            using (c)
            {
                int lh = c.LineHeightPx;
                int[] lineStarts = LineStartsOf(text);

                var second = c.ComputeCaretPoint(lineStarts[1]);
                Assert.True(second.Visible);
                Assert.Equal(lh, second.Y);

                var last = c.ComputeCaretPoint(lineStarts[2]);
                Assert.True(last.Visible, "可視領域の最終行ちょうどは可視でなければならない");
                Assert.Equal(2 * lh, last.Y);

                var beyond = c.ComputeCaretPoint(lineStarts[3]);
                Assert.False(beyond.Visible);
            }
        });

    /// <summary>
    /// キャレット行より前の論理行だけでビューポートが埋まるケース。
    /// 積み上げ数の上限を 1 でも小さく取ると、視覚行数を過小評価して
    /// 「不可視のはずの位置」を可視として返してしまう。
    /// extraPx=1(可視高さが行高の倍数でない)は ceil と floor が分かれる形状で、
    /// 上限を floor(paintHeight / lineHeight) に緩めるとここが落ちる。
    /// </summary>
    [Theory]
    [InlineData(0, 3)] // 可視高さ = 行高 × 3 ちょうど
    [InlineData(1, 4)] // 行高 × 3 + 1px → 4 行目が下端で切れつつ可視
    public void Caret_after_a_line_that_overflows_the_viewport_is_invisible(
        int extraPx,
        int visualRows
    ) =>
        Sta.Run(() =>
        {
            string huge = new string('a', 1000);
            var (f, c) = MakeControl(huge + "\nb", 10, visibleRows: 3, extraPx: extraPx);
            using (f)
            using (c)
            {
                int lh = c.LineHeightPx;
                var segs = WrapOf(c, huge, 10);
                Assert.True(
                    segs.Count > visualRows,
                    "行 0 はビューポートを超えていなければ意味がない"
                );

                // 可視領域の最終行(= visualRows - 1)までは可視、その次は不可視。
                var lastVisible = c.ComputeCaretPoint(segs[visualRows - 1].Start);
                Assert.True(lastVisible.Visible, "可視領域の最終行ちょうどは可視のはず");
                Assert.Equal((visualRows - 1) * lh, lastVisible.Y);

                var firstHidden = c.ComputeCaretPoint(segs[visualRows].Start);
                Assert.False(firstHidden.Visible);

                // 行 1 の先頭は、行 0 だけでビューポートが埋まっているので不可視。
                var nextLine = c.ComputeCaretPoint(huge.Length + 1);
                Assert.False(nextLine.Visible, "巨大行の後続論理行は不可視でなければならない");
            }
        });

    /// <summary>
    /// PaintHeightPx が 0(最小化・レイアウト確定前・hscroll より低いペイン)でも、
    /// 積み上げループの打ち切り本数が WrapFirstSegments の事前条件(1 以上)を割らないこと。
    /// Math.Max(1, ...) を外すと ArgumentOutOfRangeException が PositionCaret / UIA 経路へ抜ける
    /// (打ち切り導入で新設された例外面)。
    /// </summary>
    [Fact]
    public void Caret_below_top_line_with_zero_paint_height_is_invisible_and_does_not_throw() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("aaaa\nbbbb\ncccc\ndddd", 10, visibleRows: 0);
            using (f)
            using (c)
            {
                var p = c.ComputeCaretPoint(10); // 行 2 の先頭
                Assert.False(p.Visible);
            }
        });

    /// <summary>
    /// 巨大 1 行の行末キャレット。WrapThroughOffset は行末オフセットでは打ち切れない
    /// (行末まで走らないと「含むセグメント」が決まらない)=ReachedLineEnd は true。
    /// 打ち切りを入れても行末の扱いが変わらないことを固定する。
    /// </summary>
    [Fact]
    public void Caret_at_end_of_a_huge_line_is_placed_on_its_own_segment() =>
        Sta.Run(() =>
        {
            string huge = new string('a', 2000);
            var (f, c) = MakeControl(huge, 10, visibleRows: 400);
            using (f)
            using (c)
            {
                var segs = WrapOf(c, huge, 10);
                int lh = c.LineHeightPx;

                var pEnd = c.ComputeCaretPoint(huge.Length);
                Assert.True(pEnd.Visible);
                Assert.Equal((segs.Count - 1) * lh, pEnd.Y);

                var pLastStart = c.ComputeCaretPoint(segs[^1].Start);
                Assert.Equal(pLastStart.Y, pEnd.Y);
                Assert.True(pEnd.X > pLastStart.X, $"pEnd.X={pEnd.X} > {pLastStart.X}");
            }
        });

    /// <summary>
    /// 非 ASCII + サロゲートの折り返し行で、行末キャレットと最終セグメント先頭が
    /// 同じ視覚行に乗ること。ASCII 固定幅だと最終セグメント長が縮退しやすいため別に置く。
    /// </summary>
    [Theory]
    [InlineData(Cjk)]
    [InlineData(Emoji)]
    [InlineData(Mixed)]
    public void Caret_at_end_of_a_non_ascii_wrapped_line_stays_on_the_last_visual_row(
        string kind
    ) =>
        Sta.Run(() =>
        {
            // 60 コードポイント。折り返し 10 桁では最終セグメントが半端な長さになる。
            string line = LineOf(kind, 62);
            var (f, c) = MakeControl(line, 10, visibleRows: 200);
            using (f)
            using (c)
            {
                var segs = WrapOf(c, line, 10);
                int lh = c.LineHeightPx;

                var pEnd = c.ComputeCaretPoint(line.Length);
                Assert.True(pEnd.Visible);
                Assert.Equal((segs.Count - 1) * lh, pEnd.Y);

                var pLastStart = c.ComputeCaretPoint(segs[^1].Start);
                Assert.Equal(pLastStart.Y, pEnd.Y);
                Assert.True(
                    pEnd.X > pLastStart.X,
                    $"kind={kind}: pEnd.X={pEnd.X} > {pLastStart.X}"
                );
            }
        });

    private static int[] LineStartsOf(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }
        return starts.ToArray();
    }
}
