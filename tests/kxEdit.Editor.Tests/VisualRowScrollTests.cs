using System.Reflection;
using System.Runtime.ExceptionServices;
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
    /// 視覚行ヘルパ(private)を名前で取り出す。リネームで静かに壊れる(=常に緑になる)のが
    /// 反射テスト定番の事故なので、見つからないことをメソッド名つきで明示的に落とす。
    /// </summary>
    private static MethodInfo Helper(string name)
    {
        var m = typeof(EditorControl).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        Assert.True(m is not null, $"EditorControl に private メソッド {name} が見つからない");
        return m!;
    }

    /// <summary>
    /// private ヘルパを呼ぶ。ValueTuple の戻り値はそのままキャストできる。
    /// 内部で投げられた例外は <see cref="TargetInvocationException"/> に包まれて
    /// assert の失敗理由を隠すため、スタックを保ったまま元の例外を再スローする。
    /// </summary>
    private static T CallHelper<T>(EditorControl c, string name, params object[] args)
    {
        try
        {
            return (T)Helper(name).Invoke(c, args)!;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw; // 到達しない(上で必ず再スローされる)
        }
    }

    private static (int Line, int Seg, bool Exhausted) WalkForward(
        EditorControl c,
        TextSnapshot snap,
        int line,
        int seg,
        int n
    ) => CallHelper<(int, int, bool)>(c, "WalkForwardVisualRows", snap, line, seg, n);

    private static (int Line, int Seg) WalkBack(
        EditorControl c,
        TextSnapshot snap,
        int line,
        int seg,
        int n
    ) => CallHelper<(int, int)>(c, "WalkBackVisualRows", snap, line, seg, n);

    private static int CountForward(
        EditorControl c,
        TextSnapshot snap,
        int fromLine,
        int fromSeg,
        int toLine,
        int toSeg,
        int cap
    ) => CallHelper<int>(c, "CountVisualRowsForward", snap, fromLine, fromSeg, toLine, toSeg, cap);

    private static (int Line, int Seg) LocateRow(EditorControl c, TextSnapshot snap, int offset) =>
        CallHelper<(int, int)>(c, "LocateVisualRow", snap, offset);

    /// <summary>論理行 line の視覚行数(打ち切りなし)。</summary>
    private static int SegCount(EditorControl c, TextSnapshot snap, int line) =>
        CallHelper<(int Count, bool Exact)>(
            c,
            "SegmentCountCapped",
            snap,
            line,
            int.MaxValue
        ).Count;

    /// <summary>
    /// fixture の視覚行を (論理行, セグメント index) の昇順に全列挙したオラクル。
    /// 歩き/数えのロジックには一切依存せず「各論理行が何本の視覚行を持つか」だけから組むので、
    /// 歩き側の変異はこのリストに伝播しない。
    /// </summary>
    /// <remarks>
    /// 本数の取得だけは <c>SegmentCountCapped</c>(=<c>LineLayout</c> の薄いラッパ)に依存する。
    /// kxEdit.Core の internal はテストアセンブリから見えないため、折り返し規則そのものを
    /// 独立に持つことはできない(その規則は Core の LineLayout 系テストが守っている)。
    /// つまり本オラクルが守るのは<b>視覚行の歩き方・数え方</b>であって折り返し規則ではない。
    /// </remarks>
    private static List<(int Line, int Seg)> EnumerateRows(EditorControl c, TextSnapshot snap)
    {
        var rows = new List<(int Line, int Seg)>();
        for (int line = 0; line < snap.LineCount; line++)
        {
            int segs = SegCount(c, snap, line);
            for (int seg = 0; seg < segs; seg++)
                rows.Add((line, seg));
        }
        return rows;
    }

    /// <summary>
    /// 総当りオラクル用の fixture。複数セグメントの行・空行 2 連・長い末尾行を含む
    /// (文書頭/文書末/空行/段落跨ぎのすべてを 1 本の網に入れる)。
    /// </summary>
    private const string OracleText = "abcdefghij\n\n\nklmnopqrstuvwxyz";

    /// <summary>
    /// 可視行数を明示したエディタを作る。折り返し ON では HScrollBar が常に隠れるため
    /// PaintHeightPx == ClientSize.Height になり、可視行数をテストから固定できる
    /// (EditorControlWrapCaretTests.MakeControl と同じ流儀)。
    /// </summary>
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
    /// 負のセグメントを弾くガード(M-1)。負値が _topSegment に入ると WalkBackVisualRows の
    /// <c>n -= seg</c> で n が増え、上方向の歩きが文書頭まで暴走する。
    /// 非既定位置(2)から始めて「0 のままだった」と区別する。
    /// </summary>
    [Fact]
    public void SetTopPosition_ClampsNegativeSegmentToZero() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 2);
                Assert.Equal(2, c.TopSegment);

                c.SetTopPosition(0, -5);
                Assert.Equal(0, c.TopLine);
                Assert.Equal(0, c.TopSegment);
            }
        });

    /// <summary>
    /// 編集で論理行が消え _topLine が新しい maxLine を超えたときの防御クランプ(O)。
    /// 行が消えた後のセグメント index は無意味なので 0 に戻す。
    /// AfterEdit は UpdateVerticalScrollbar → BringCaretIntoView の順で走り、この fixture では
    /// キャレットが可視のため BringCaretIntoView は TopLine を触らない=クランプ単独の検証になる。
    /// </summary>
    [Fact]
    public void UpdateVerticalScrollbar_DefensiveClamp_ResetsTopSegment() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("aaaa\nbbbb\ncccc\ndddd\neeee\nffff", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(4, 1);
                Assert.Equal(4, c.TopLine);
                Assert.Equal(1, c.TopSegment);

                // 論理行 6 本 → 1 本へ縮める。_topLine(4) > maxLine(0) で防御クランプが発火する。
                c.ReplaceCharRange(0, c.TextLength, "ab");

                Assert.Equal(0, c.TopLine);
                Assert.Equal(0, c.TopSegment);
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
                Assert.Equal((1, 0, false), WalkForward(c, snap, 0, segs0 - 1, 1));
                Assert.Equal((1, 0, false), WalkForward(c, snap, 0, segs0 + 2, 1));
            }
        });

    /// <summary>
    /// 前方の歩きを全起点 × 全距離で総当りし、視覚行を列挙したオラクルと一致することを固定する。
    /// 文書末を越える要求では最終視覚行で止まり Exhausted=true になること(=要求 n 本を
    /// 歩き切れなかったことを呼び出し側が区別できること)も併せて固定する。
    /// </summary>
    [Fact]
    public void WalkForwardVisualRows_MatchesEnumeratedRows_ForAllStartsAndDistances() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(OracleText, wrap: 2, visibleRows: 30);
            using (f)
            using (c)
            {
                var snap = TextBuffer.FromString(OracleText).Current;
                var rows = EnumerateRows(c, snap);
                Assert.True(rows.Count >= 8, "fixture 前提: 視覚行が十分な本数ある");
                int last = rows.Count - 1;

                for (int i = 0; i <= last; i++)
                {
                    for (int n = 0; n <= rows.Count + 1; n++)
                    {
                        var expected = rows[Math.Min(i + n, last)];
                        bool exhausted = i + n > last;
                        Assert.Equal(
                            (expected.Line, expected.Seg, exhausted),
                            WalkForward(c, snap, rows[i].Line, rows[i].Seg, n)
                        );
                    }
                }
            }
        });

    /// <summary>
    /// 後方の歩きを全起点 × 全距離で総当りし、オラクルと一致することを固定する。
    /// 文書頭を越える要求は先頭視覚行で止まる。
    /// </summary>
    [Fact]
    public void WalkBackVisualRows_MatchesEnumeratedRows_ForAllStartsAndDistances() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(OracleText, wrap: 2, visibleRows: 30);
            using (f)
            using (c)
            {
                var snap = TextBuffer.FromString(OracleText).Current;
                var rows = EnumerateRows(c, snap);
                int last = rows.Count - 1;

                for (int i = 0; i <= last; i++)
                {
                    for (int n = 0; n <= rows.Count + 1; n++)
                    {
                        var expected = rows[Math.Max(i - n, 0)];
                        Assert.Equal(expected, WalkBack(c, snap, rows[i].Line, rows[i].Seg, n));
                    }
                }
            }
        });

    /// <summary>
    /// 視覚行距離を全 (起点, 終点) ペア × cap 3 種(ちょうど / 未満 / 超過)で総当りし、
    /// オラクル上の index 差と一致することを固定する。終点が起点より手前なら 0。
    /// </summary>
    [Fact]
    public void CountVisualRowsForward_MatchesRowDistance_ForAllPairsAndCaps() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(OracleText, wrap: 2, visibleRows: 30);
            using (f)
            using (c)
            {
                var snap = TextBuffer.FromString(OracleText).Current;
                var rows = EnumerateRows(c, snap);

                for (int i = 0; i < rows.Count; i++)
                {
                    for (int j = 0; j < rows.Count; j++)
                    {
                        int distance = j - i;
                        int[] caps =
                        {
                            Math.Max(1, distance), // cap ちょうど
                            Math.Max(1, distance - 1), // cap 未満
                            rows.Count + 5, // cap 超過
                        };
                        foreach (int cap in caps)
                        {
                            int expected = distance <= 0 ? 0 : Math.Min(cap, distance);
                            Assert.Equal(
                                expected,
                                CountForward(
                                    c,
                                    snap,
                                    rows[i].Line,
                                    rows[i].Seg,
                                    rows[j].Line,
                                    rows[j].Seg,
                                    cap
                                )
                            );
                        }
                    }
                }
            }
        });

    /// <summary>
    /// LocateVisualRow が返す視覚行位置が、描画経路(ComputeCaretPoint の Y 積み上げ)の
    /// 行番号と一致することを全オフセットで固定する。両者は視覚行数の数え方が独立している
    /// (片方はセグメント index を返し、もう片方は行数を積み上げて px にする)。
    /// </summary>
    [Fact]
    public void LocateVisualRow_AgreesWithComputeCaretPointRow_ForAllOffsets() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(OracleText, wrap: 2, visibleRows: 30);
            using (f)
            using (c)
            {
                var snap = TextBuffer.FromString(OracleText).Current;
                var rows = EnumerateRows(c, snap);
                int lineHeight = c.LineHeightPx;

                for (int offset = 0; offset <= snap.CharLength; offset++)
                {
                    var (_, y, visible) = c.ComputeCaretPoint(offset);
                    Assert.True(visible, $"fixture 前提: offset {offset} は可視域に入る");
                    Assert.Equal(rows[y / lineHeight], LocateRow(c, snap, offset));
                }
            }
        });

    /// <summary>
    /// 折り返し OFF では TopSegment が常に 0 で、視覚行の歩き/数えが論理行の算術に退化する
    /// (設計書 I-3)。ON 側の分割規則に一切依存しないことを固定する。
    /// </summary>
    [Fact]
    public void VisualRowHelpers_DegenerateToLogicalLines_WhenWrapOff() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(OracleText, wrap: 0, visibleRows: 30);
            using (f)
            using (c)
            {
                var snap = TextBuffer.FromString(OracleText).Current;
                Assert.Equal(0, c.TopSegment);

                for (int line = 0; line < snap.LineCount; line++)
                {
                    Assert.Equal(1, SegCount(c, snap, line));
                    Assert.Equal((line, 0), LocateRow(c, snap, snap.GetLineStart(line)));
                }

                // 論理行 4 本 = 視覚行 4 本。歩きも数えも論理行の算術になる。
                Assert.Equal((2, 0, false), WalkForward(c, snap, 0, 0, 2));
                Assert.Equal((3, 0, true), WalkForward(c, snap, 0, 0, 99));
                Assert.Equal((1, 0), WalkBack(c, snap, 3, 0, 2));
                Assert.Equal((0, 0), WalkBack(c, snap, 3, 0, 99));
                Assert.Equal(3, CountForward(c, snap, 0, 0, 3, 0, 99));
            }
        });
}
