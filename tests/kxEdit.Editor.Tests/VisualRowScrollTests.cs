using System.Reflection;
using kxEdit.Core.Buffers;
using kxEdit.Core.Settings;

namespace kxEdit.Editor.Tests;

/// <summary>
/// 2026-08-22 監査 A-6: 可視域の起点を視覚行 (TopLine, TopSegment) にする(設計書 不変条件 I-2)。
/// 本ファイルは状態とスクロール判断の契約を固定する。
/// 折り返し OFF では TopSegment が常に 0 で全式が現行に退化すること(I-3)も併せて守る。
/// </summary>
public class VisualRowScrollTests
{
    /// <summary>
    /// 可視行数を明示したエディタを作る。折り返し ON では HScrollBar が常に隠れるため
    /// PaintHeightPx == ClientSize.Height になり、可視行数をテストから固定できる
    /// (EditorControlWrapCaretTests.MakeControl と同じ流儀)。
    /// </summary>
    /// <summary>
    /// private の視覚行ヘルパを叩く(seam の境界を直接固定するため)。ValueTuple の要素は
    /// public field Item1/Item2 として読める。
    /// </summary>
    private static T CallHelper<T>(EditorControl c, string name, params object[] args)
    {
        var m = typeof(EditorControl).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Instance
        )!;
        return (T)m.Invoke(c, args)!;
    }

    private static (int Line, int Seg) WalkForward(
        EditorControl c,
        TextSnapshot snap,
        int line,
        int seg,
        int n
    ) => CallHelper<(int, int)>(c, "WalkForwardVisualRows", snap, line, seg, n);

    /// <summary>論理行 line の視覚行数(打ち切りなし)。</summary>
    private static int SegCount(EditorControl c, TextSnapshot snap, int line) =>
        CallHelper<(int Count, bool Exact)>(
            c,
            "SegmentCountCapped",
            snap,
            line,
            int.MaxValue
        ).Count;

    private static (Form f, EditorControl c) MakeControl(string text, int wrap, int visibleRows)
    {
        var f = new HostForm();
        var c = new EditorControl { WrapColumns = wrap };
        f.Controls.Add(c);
        _ = f.Handle;
        c.ClientSize = new System.Drawing.Size(800, c.LineHeightPx * visibleRows);
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    [Fact]
    public void TopSegment_IsZero_ByDefault() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                Assert.Equal(0, c.TopSegment);
            }
        });

    [Fact]
    public void SetTopPosition_KeepsSegment_AndTopLineSetterResetsIt() =>
        Sta.Run(() =>
        {
            // 非既定位置(TopSegment=2)から検証を始める=「0 のままだった」と区別する。
            var (f, c) = MakeControl("abcdefghij\nklmnop", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 2);
                Assert.Equal(0, c.TopLine);
                Assert.Equal(2, c.TopSegment);

                // TopLine セッターは「その行の先頭視覚行から」の意味を保つ=TopSegment を 0 に戻す。
                // 同じ論理行を代入しても戻ること(早期 return に潰されないこと)。
                c.TopLine = 0;
                Assert.Equal(0, c.TopLine);
                Assert.Equal(0, c.TopSegment);
            }
        });

    [Fact]
    public void WrapColumnsSetter_ResetsTopSegment() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 2);
                Assert.Equal(2, c.TopSegment);
                c.WrapColumns = 4; // 折り返し幅が変わればセグメント分割そのものが変わる
                Assert.Equal(0, c.TopSegment);
            }
        });

    [Fact]
    public void ReplaceSource_ResetsTopSegment() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 2);
                c.ReplaceSource(TextBuffer.FromString("xyz"));
                Assert.Equal(0, c.TopSegment);
                Assert.Equal(0, c.TopLine);
            }
        });

    [Fact]
    public void SetTopPosition_ClampsLine_AndDropsSegmentWhenLineClamped() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                // 論理行 1 本しかないので line=5 は 0 にクランプされる。
                // 行がクランプされたときは segment の意味が失われるので 0 にする。
                c.SetTopPosition(5, 3);
                Assert.Equal(0, c.TopLine);
                Assert.Equal(0, c.TopSegment);
            }
        });

    /// <summary>
    /// 陳腐化したセグメント index(編集で段落が縮んでも _topSegment はリセットしない設計)を
    /// 渡されたとき、WalkForwardVisualRows は最終セグメントへ寄せて数える=1 本進めば
    /// 次の論理行の先頭に着く。寄せを外すと「行を跨ぐ 1 本」の消費が負になり、
    /// 陳腐化ぶんだけ下へ行き過ぎる(ViewportLayout.Build / CountVisualRowsForward と不整合)。
    /// </summary>
    [Fact]
    public void WalkForwardVisualRows_ClampsStaleSegment_LandsOnNextLineHead() =>
        Sta.Run(() =>
        {
            const string Text = "abcdefghij\nklmnop";
            var (f, c) = MakeControl(Text, wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                var snap = TextBuffer.FromString(Text).Current;
                int segs0 = SegCount(c, snap, 0);
                Assert.True(segs0 >= 2, "fixture 前提: 行 0 は複数の視覚行に折り返される");
                // 行 1 も複数本ないと、行き過ぎた着地が (1,0) と区別できず変異を殺せない。
                Assert.True(SegCount(c, snap, 1) >= 2, "fixture 前提: 行 1 も複数の視覚行を持つ");

                // 実セグメント数を超える起点は SetTopPosition で作れる(セグメントは行と違い
                // クランプされない=編集で段落が縮んだ後の _topSegment と同じ状態)。
                c.SetTopPosition(0, segs0 + 2);
                Assert.Equal(segs0 + 2, c.TopSegment);

                // 最終セグメントからの 1 本(基準)と、陳腐化した起点からの 1 本が一致すること。
                Assert.Equal((1, 0), WalkForward(c, snap, 0, segs0 - 1, 1));
                Assert.Equal((1, 0), WalkForward(c, snap, 0, segs0 + 2, 1));
            }
        });

    /// <summary>
    /// ApplyAppearance(フォント変更)は metrics が変わる=セグメント分割そのものが変わるので
    /// TopSegment を 0 に戻す。折り返し桁は据え置きにして、WrapColumns セッター経由の
    /// リセットと取り違えないようにする。
    /// </summary>
    [Fact]
    public void ApplyAppearance_ResetsTopSegment() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 2);
                Assert.Equal(2, c.TopSegment);

                c.ApplyAppearance(
                    new AppSettings
                    {
                        WrapColumnEnabled = true,
                        WrapColumn = 2,
                        FontSize = 20f,
                    }
                );

                Assert.Equal(2, c.WrapColumns); // 折り返し桁は変えていない
                Assert.Equal(0, c.TopSegment);
            }
        });
}
