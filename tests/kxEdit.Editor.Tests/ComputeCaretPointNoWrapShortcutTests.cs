using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using kxEdit.Core.Buffers;
using kxEdit.Editor;
using Xunit;

namespace kxEdit.Editor.Tests;

/// <summary>
/// フェーズ 2(P-9 (c)・設計書 §7.2): 折り返し OFF の ComputeCaretPoint の短絡が、
/// TopLine からの積み上げループ(=変更前の実装)と全オフセットで一致すること。
/// 境界: 最終可視行・1 行はみ出し・窓の高さが行高の端数・古い _topSegment・PaintHeightPx = 0。
/// </summary>
public class ComputeCaretPointNoWrapShortcutTests
{
    // rows: 窓の高さを行高の何行分にするか。frac: 端数(-1 は「行高 - 1」を意味する)。
    [Theory]
    [InlineData(10, 0, 0, 0)] //    高さがちょうど 10 行(最終可視行と 1 行はみ出しの境界)
    [InlineData(10, 1, 0, 0)] //    10 行 + 1px(11 行目が 1px だけ見える)
    [InlineData(10, -1, 0, 0)] //   10 行 + (行高 - 1)px
    [InlineData(10, 0, 37, 0)] //   TopLine > 0
    [InlineData(10, 1, 37, 3)] //   古い _topSegment(折り返し OFF でも SetTopPosition で残せる)
    [InlineData(0, 0, 37, 0)] //    PaintHeightPx = 0
    public void Shortcut_MatchesAccumulation_ForEveryLine(
        int rows,
        int frac,
        int topLine,
        int topSegment
    )
    {
        Sta.Run(() =>
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 120; i++)
                sb.Append(
                        i % 7 == 3
                            ? ""
                            : string.Create(CultureInfo.InvariantCulture, $"行{i:D3} abc")
                    )
                    .Append(i % 2 == 0 ? "\r\n" : "\n");
            var buf = TextBuffer.FromString(sb.ToString());
            using var form = HostForm.CreateVisible();
            var ctrl = new EditorControl();
            form.ClientSize = new Size(600, 800);
            form.Controls.Add(ctrl);
            ctrl.SetSource(buf);
            try
            {
                ctrl.WrapColumns = 0;
                int lh = ctrl.Metrics.LineHeightPx;
                int extra = frac < 0 ? lh - 1 : frac;
                ctrl.Size = new Size(300, rows * lh + extra);
                Assert.Equal(rows * lh + extra, ctrl.ClientSize.Height); // fixture 前提(枠なし)
                ctrl.SetTopPosition(topLine, topSegment);
                Assert.Equal(topLine, ctrl.TopLine); // fixture 前提
                Assert.Equal(topSegment, ctrl.TopSegment); // fixture 前提

                var snap = buf.Current;
                int visible = 0,
                    hiddenBelow = 0;
                for (int line = 0; line < snap.LineCount; line++)
                {
                    int start = snap.GetLineStart(line);
                    int end = snap.GetLineEnd(line, includeBreak: false);
                    foreach (int off in new[] { start, (start + end) / 2, end })
                    {
                        var expected = ctrl.TestHook_ComputeCaretPointByAccumulation(off);
                        var actual = ctrl.ComputeCaretPoint(off);
                        Assert.Equal(expected, actual);
                        if (actual.Visible)
                            visible++;
                        else if (line > topLine)
                            hiddenBelow++;
                    }
                }
                // fixture 前提: 可視と「下にはみ出して不可視」の両方を踏んでいる(PaintHeightPx = 0 を除く)
                if (rows > 0)
                    Assert.True(visible > 0, "可視の行がない");
                Assert.True(hiddenBelow > 0, "下にはみ出した行がない");
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }
}
