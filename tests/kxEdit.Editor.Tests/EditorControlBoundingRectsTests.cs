using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using kxEdit.Accessibility;
using kxEdit.Core.Buffers;
using kxEdit.Editor;
using Xunit;

namespace kxEdit.Editor.Tests;

public class EditorControlBoundingRectsTests
{
    [Fact]
    public void GetBoundingRectangles_EmptyRange_ReturnsEmptyArray()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello"));
            using var form = HostForm.CreateVisible();
            form.Controls.Add(ctrl);
            try
            {
                IUiaTextHost host = ctrl;
                Assert.Empty(host.GetBoundingRectangles(3, 3)); // 縮退範囲=空配列
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void GetBoundingRectangles_SingleLineRange_ReturnsOneRect()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello world"));
            ctrl.Size = new System.Drawing.Size(400, 100);
            using var form = HostForm.CreateVisible();
            form.Controls.Add(ctrl);
            try
            {
                // 描画を 1 回発生させて _lastFrame を確定
                ctrl.Invalidate();
                ctrl.Update();
                Application.DoEvents();
                IUiaTextHost host = ctrl;
                var rects = host.GetBoundingRectangles(0, 5); // "hello"
                Assert.Equal(4, rects.Length); // 1 行 = 4 要素
                Assert.True(rects[2] > 0); // 幅 > 0
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void GetBoundingRectangles_MultiLineRange_ReturnsMultipleRects()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("aaa\nbbb\nccc"));
            ctrl.Size = new System.Drawing.Size(200, 100);
            using var form = HostForm.CreateVisible();
            form.Controls.Add(ctrl);
            try
            {
                ctrl.Invalidate();
                ctrl.Update();
                Application.DoEvents();
                IUiaTextHost host = ctrl;
                var rects = host.GetBoundingRectangles(0, 11); // 全体
                Assert.Equal(3 * 4, rects.Length); // 3 行 × 4 要素
            }
            finally
            {
                form.Close();
            }
        });
    }

    // A-12(2026-08-22): GetBoundingRectangles が _scrollX を引かず、折り返し OFF で
    // 右へスクロールした状態では NVDA のフォーカスハイライト矩形が実描画より右にずれる。
    // 描画(Paint.cs)・PointFromCharOffset・逆変換 OffsetFromClientPoint は引いており、
    // ここだけが往復非対称だった。
    [Fact]
    public void GetBoundingRectangles_SubtractsScrollX()
    {
        Sta.Run(() =>
        {
            // 長文行 1 本 + 短い行数本。幅を絞って hscroll を表示状態にする。
            // 長文行は line 0 に置く(UpdateHorizontalScrollbar は TopLine から probeHeight 分の
            // 視覚行しか走査しないため、可視域に無いと hscroll が出ない)。
            var text = new string('x', 400) + "\nl1\nl2\nl3";
            using var form = HostForm.CreateVisible();
            var ctrl = new EditorControl { Dock = DockStyle.Fill };
            form.Controls.Add(ctrl);
            ctrl.SetSource(TextBuffer.FromString(text));
            try
            {
                form.ClientSize = new System.Drawing.Size(120, 100);
                form.PerformLayout();
                ctrl.WrapColumns = 0; // 折り返し OFF
                ctrl.TopLine = 0;
                ctrl.Invalidate();
                Application.DoEvents(); // 描画を 1 回起こしてレイアウトを確定

                IUiaTextHost host = ctrl;
                var before = host.GetBoundingRectangles(0, 4);
                Assert.NotEmpty(before); // fixture 前提: 行 0 は可視

                ctrl.ScrollX = 50;
                // fixture 前提: hscroll が表示されていないと ScrollX setter は no-op。
                Assert.True(
                    ctrl.ScrollX > 0,
                    "fixture 前提崩れ: hscroll 非表示で ScrollX を置けない"
                );

                var after = host.GetBoundingRectangles(0, 4);
                Assert.NotEmpty(after);

                // X は ScrollX 分だけ左へ寄る。幅は差分なので不変。
                Assert.Equal(before[0] - ctrl.ScrollX, after[0]);
                Assert.Equal(before[2], after[2]);
                // Y と高さは水平スクロールと無関係=不変。X だけ見ていると
                // 「軸を取り違えて Y から引く」変異(rects.Add(csy + y1 - sx))が素通りする
                // (最終ブランチレビュー品質パス Minor 4 で実際に生存した)。
                Assert.Equal(before[1], after[1]);
                Assert.Equal(before[3], after[3]);
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }

    /// <summary>
    /// フェーズ 2(P-9 (a)(b))の突き合わせ用: 変更前の ComputeBoundingRectangles と同じ走査。
    /// 範囲の全論理行について ComputeCaretPointForUia を呼び、可視なものだけ矩形にする。
    /// safety(10 万反復)は設計書 §3.5 の意図的な挙動差なので入れない。
    /// </summary>
    private static double[] ReferenceRects(
        EditorControl ctrl,
        TextSnapshot snap,
        int start,
        int end
    )
    {
        int s = Math.Clamp(start, 0, snap.CharLength);
        int en = Math.Clamp(end, 0, snap.CharLength);
        var list = new List<double>();
        if (s >= en)
            return list.ToArray();
        var origin = ctrl.PointToScreen(System.Drawing.Point.Empty);
        int sx = ctrl.ScrollX;
        int lh = ctrl.Metrics.LineHeightPx;
        int pos = s;
        while (pos < en)
        {
            int line = snap.GetLineIndexOfChar(pos);
            int rangeEnd = Math.Min(en, snap.GetLineEnd(line, includeBreak: false));
            var (x1, y1, visible) = ctrl.ComputeCaretPointForUia(pos);
            var (x2, _, _) = ctrl.ComputeCaretPointForUia(rangeEnd);
            if (visible)
            {
                list.Add(origin.X + x1 - sx);
                list.Add(origin.Y + y1);
                list.Add(Math.Max(1, x2 - x1));
                list.Add(lh);
            }
            int next = line + 1 < snap.LineCount ? snap.GetLineStart(line + 1) : snap.CharLength;
            if (next <= pos)
                break;
            pos = next;
        }
        return list.ToArray();
    }

    /// <summary>
    /// 200 行。空行(i % 17 == 5)・CRLF(i % 3 == 0)・非 ASCII を混ぜ、最終行は改行なし。
    /// </summary>
    private static string MixedDoc()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 200; i++)
        {
            if (i % 17 != 5)
                sb.Append(CultureInfo.InvariantCulture, $"line{i:D3} あいう");
            if (i < 199)
                sb.Append(i % 3 == 0 ? "\r\n" : "\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// (line, col) を文字オフセットにする。col は行頭からの UTF-16 位置で、行の長さ+1 を渡すと
    /// CRLF の中間(CR と LF の間)を指せる。
    /// </summary>
    private static int Off(TextSnapshot snap, int line, int col) =>
        Math.Min(snap.GetLineStart(line) + col, snap.CharLength);

    // 各ケース: 範囲 [(sl,sc), (el,ec)) と TopLine / 窓の高さ。expectNonEmpty は
    // 「参照も新実装も空」で一致する空振りを防ぐための fixture 前提。
    [Theory]
    // (199, 99) は Off のクランプで CharLength(文書末尾)になる。改行なしの最終行
    // (199 行目 = 11 文字)を最後まで含み、最終行の nextLineStart = CharLength の分岐と
    // 文書末尾でのループ終了を踏む。
    [InlineData(50, 200, 0, 0, 199, 99, true)] //   全文(可視域の上・中・下にまたがる)
    [InlineData(50, 200, 0, 0, 30, 3, false)] //    可視域より上だけ
    [InlineData(50, 200, 120, 0, 199, 99, false)] // 可視域より下から文書末尾まで
    [InlineData(50, 200, 20, 4, 55, 2, true)] //    上の行の途中から可視域の途中まで
    [InlineData(50, 200, 53, 4, 150, 0, true)] //   可視域の途中から下まで
    [InlineData(51, 200, 48, 12, 60, 0, true)] //   可視域の上の 48 行目(CRLF)の CR と LF の間から
    [InlineData(0, 200, 0, 0, 199, 99, true)] //    全文・TopLine=0・下端超過
    [InlineData(190, 200, 100, 0, 199, 99, true)] // 改行なしの最終行を文書末尾まで含む
    [InlineData(50, 0, 0, 0, 199, 99, false)] //    全文・PaintHeightPx = 0(すべて不可視)
    public void GetBoundingRectangles_MatchesFullScan_WrapOff(
        int topLine,
        int clientHeight,
        int sl,
        int sc,
        int el,
        int ec,
        bool expectNonEmpty
    )
    {
        Sta.Run(() =>
        {
            var buf = TextBuffer.FromString(MixedDoc());
            using var form = HostForm.CreateVisible();
            var ctrl = new EditorControl();
            form.ClientSize = new System.Drawing.Size(400, 400);
            form.Controls.Add(ctrl);
            ctrl.SetSource(buf);
            try
            {
                ctrl.Size = new System.Drawing.Size(300, clientHeight);
                ctrl.WrapColumns = 0;
                ctrl.TopLine = topLine;
                Assert.Equal(topLine, ctrl.TopLine); // fixture 前提: クランプされていない
                var snap = buf.Current;
                // fixture 前提: 48 行目は CRLF で、col=12 が CR と LF の間を指す
                Assert.Equal(
                    12,
                    snap.GetLineEnd(48, includeBreak: false) - snap.GetLineStart(48) + 1
                );
                int s = Off(snap, sl, sc);
                int e = Off(snap, el, ec);
                // fixture 前提: (199, 99) は文書末尾(最終行は改行なし)まで取る
                if (el == 199 && ec == 99)
                {
                    Assert.Equal(snap.CharLength, e);
                    Assert.Equal(snap.CharLength, snap.GetLineEnd(199, includeBreak: false));
                }
                IUiaTextHost host = ctrl;
                var expected = ReferenceRects(ctrl, snap, s, e);
                Assert.Equal(expectNonEmpty, expected.Length > 0); // fixture 前提
                Assert.Equal(expected, host.GetBoundingRectangles(s, e));
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }

    // 折り返し ON。TopLine の途中セグメントから描いている(_topSegment > 0)ときは、
    // TopLine の上のセグメントが不可視になる。(b) の打ち切りは line > TopLine に限るので、
    // TopLine の隠れたセグメントから始まる範囲でも、後続行の矩形が出なければならない。
    // 下の 2 ケースは初回反復で s が TopLine より下の行の途中(下のセグメント)にある。
    // 可視なら後続行も走査し、可視域より下なら 1 回目で打ち切る((b) の line > TopLine)。
    // 後者が本当に可視域の下にあることは expectNonEmpty=false(参照が空)で fixture 前提として確かめる。
    [Theory]
    [InlineData(3, 2, 3, 0, true)] //    TopLine の隠れたセグメント(先頭)から
    [InlineData(3, 2, 3, 25, true)] //   TopLine の可視セグメントの途中から
    [InlineData(3, 0, 3, 0, true)] //    _topSegment = 0
    [InlineData(3, 2, 4, 35, true)] //   TopLine+1 行の下のセグメント(4 本目)の途中から
    [InlineData(3, 2, 23, 35, false)] // 可視域より下(TopLine+20 行)の下のセグメントの途中から
    public void GetBoundingRectangles_MatchesFullScan_WrapOn(
        int topLine,
        int topSegment,
        int startLine,
        int startCol,
        bool expectNonEmpty
    )
    {
        Sta.Run(() =>
        {
            // 各行 100 字 = 折り返し 10 桁で 10 セグメント
            var text = string.Join(
                "\n",
                Enumerable.Range(0, 30).Select(i => new string((char)('a' + i % 26), 100))
            );
            var buf = TextBuffer.FromString(text);
            using var form = HostForm.CreateVisible();
            var ctrl = new EditorControl();
            form.ClientSize = new System.Drawing.Size(400, 400);
            form.Controls.Add(ctrl);
            ctrl.SetSource(buf);
            try
            {
                ctrl.Size = new System.Drawing.Size(300, 300);
                ctrl.WrapColumns = 10;
                ctrl.SetTopPosition(topLine, topSegment);
                Assert.Equal(topLine, ctrl.TopLine); // fixture 前提
                Assert.Equal(topSegment, ctrl.TopSegment); // fixture 前提
                var snap = buf.Current;
                int s = snap.GetLineStart(startLine) + startCol;
                IUiaTextHost host = ctrl;
                var expected = ReferenceRects(ctrl, snap, s, snap.CharLength);
                Assert.Equal(expectNonEmpty, expected.Length > 0); // fixture 前提
                Assert.Equal(expected, host.GetBoundingRectangles(s, snap.CharLength));
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }

    // 設計書 §3.5: 範囲先頭から 10 万行より先に可視域がある場合、従来は safety で打ち切られて
    // 空配列だった。(a) で TopLine の先頭から走査するので矩形を返す。
    [Fact]
    public void GetBoundingRectangles_VisibleAreaBeyond100kLines_ReturnsRects()
    {
        Sta.Run(() =>
        {
            var buf = TextBuffer.FromString(string.Concat(Enumerable.Repeat("a\n", 150_000)));
            using var form = HostForm.CreateVisible();
            var ctrl = new EditorControl();
            form.ClientSize = new System.Drawing.Size(400, 400);
            form.Controls.Add(ctrl);
            ctrl.SetSource(buf);
            try
            {
                ctrl.Size = new System.Drawing.Size(300, 200);
                ctrl.TopLine = 120_000;
                Assert.Equal(120_000, ctrl.TopLine); // fixture 前提
                var snap = buf.Current;
                int e = snap.GetLineStart(120_050);
                IUiaTextHost host = ctrl;
                var actual = host.GetBoundingRectangles(0, e);
                Assert.NotEmpty(actual);
                Assert.Equal(ReferenceRects(ctrl, snap, 0, e), actual);
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }
}
