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

    /// <summary>
    /// 垂直スクロールバー(private フィールド)を取り出す。<see cref="Helper"/> と同じ理由で、
    /// リネームで静かに緑になる事故を防ぐためフィールド名つきで明示的に落とす。
    /// </summary>
    private static VScrollBar VScroll(EditorControl c)
    {
        var fi = typeof(EditorControl).GetField(
            "_vscroll",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        Assert.True(fi is not null, "EditorControl に private フィールド _vscroll が見つからない");
        var bar = fi!.GetValue(c) as VScrollBar;
        Assert.True(bar is not null, "_vscroll が VScrollBar として取り出せない");
        return bar!;
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
        CallHelper<int>(c, "SegmentCountCapped", snap, line, int.MaxValue);

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
    /// 陳腐化したセグメント index を渡されたとき、CountVisualRowsForward も最終セグメントへ
    /// 寄せて数える=WalkForwardVisualRows と同じ寄せ方になり、可視判定(距離)と着地(歩き)が
    /// 食い違わない。寄せを外すと負の距離を返し、可視判定が反転する。
    /// </summary>
    /// <remarks>
    /// 総当りオラクル(<see cref="CountVisualRowsForward_MatchesRowDistance_ForAllPairsAndCaps"/>)は
    /// 実在する視覚行しか起点にしないため陳腐化を作れない=この境界は本テストだけが守る。
    /// 実際、寄せを外す変異は総当りテストでは生存し、本テストでのみ kill される。
    /// </remarks>
    [Fact]
    public void CountVisualRowsForward_ClampsStaleSegment_MatchesLastSegmentDistance() =>
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

                // 行 0 の最終セグメントから行 1 の先頭までは 1 本(基準)。
                Assert.Equal(1, CountForward(c, snap, 0, segs0 - 1, 1, 0, 99));
                // 実セグメント数を超える起点でも同じ距離になること。
                Assert.Equal(1, CountForward(c, snap, 0, segs0 + 2, 1, 0, 99));
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

    // ===== TopSegment を尊重する経路(描画・座標・ヒットテスト・可視域報告)=====

    [Fact]
    public void PointFromCharOffset_ReturnsEmpty_ForRowsAboveTopSegment() =>
        Sta.Run(() =>
        {
            // "abcdefghij"(10 文字)を wrap=2 → 視覚行 5 本。TopSegment=2 なら 0,1 本目は不可視。
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 2);
                // 可視フラグそのもので弁別する。PointFromCharOffset は「不可視」を Point.Empty で
                // 表すが、可視域最上段の行頭は座標も (0, 0)=Point.Empty と等しくなるため、
                // 公開 API だけでは「不可視」と「可視だが原点」を区別できない。
                Assert.False(
                    c.ComputeCaretPoint(0).Visible,
                    "視覚行 0 は TopSegment より上=不可視"
                );
                Assert.False(
                    c.ComputeCaretPoint(2).Visible,
                    "視覚行 1 は TopSegment より上=不可視"
                );
                var (_, y, visible) = c.ComputeCaretPoint(4); // 視覚行 2 = 可視域の最上段
                Assert.True(visible, "視覚行 2 は可視域の最上段");
                Assert.Equal(0, y);

                // 公開 API 側にも伝播していること(不可視は Point.Empty)。
                Assert.Equal(System.Drawing.Point.Empty, c.PointFromCharOffset(0)); // 視覚行 0
                Assert.Equal(System.Drawing.Point.Empty, c.PointFromCharOffset(2)); // 視覚行 1
            }
        });

    /// <summary>
    /// 対象が TopLine より下の論理行にあるとき、積み上げから TopSegment 本ぶんを差し引くこと
    /// (先頭論理行は画面外に TopSegment 本を持つ)。<c>ViewportLayout.Build</c> の
    /// topSegment クランプと同じ寄せ方であることも同時に固定する。
    /// </summary>
    [Fact]
    public void ComputeCaretPoint_SubtractsSkippedRows_WhenTargetIsBelowTopLine() =>
        Sta.Run(() =>
        {
            // 論理行 0 = "abcdefghij"(wrap=2 → 視覚行 5 本)・論理行 1 = "klmnop"(3 本)。
            // TopSegment=2 なら論理行 0 は 3 本ぶんだけ見える=論理行 1 の先頭は視覚行 3。
            var (f, c) = MakeControl("abcdefghij\nklmnop", wrap: 2, visibleRows: 6);
            using (f)
            using (c)
            {
                int lh = c.LineHeightPx;
                c.SetTopPosition(0, 2);

                var (_, y, visible) = c.ComputeCaretPoint(11); // 論理行 1 の先頭(k)
                Assert.True(visible, "論理行 1 の先頭は可視域内");
                Assert.Equal(3 * lh, y);

                // 論理行 1 の 2 本目(m)は視覚行 4。
                Assert.Equal(4 * lh, c.ComputeCaretPoint(13).Y);
            }
        });

    [Fact]
    public void GetVisibleCharRange_StartsAtTopSegment() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 2);
                var (start, end) = c.GetVisibleCharRange();
                Assert.Equal(4, start); // 視覚行 2 の先頭
                Assert.Equal(10, end); // 視覚行 2..4 で文書末まで
            }
        });

    [Fact]
    public void OffsetFromClientPoint_TopRow_MapsToTopSegment() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 2);
                // クライアント最上段(y=0)の左端 = 視覚行 2 の先頭 = offset 4
                Assert.Equal(4, c.OffsetFromClientPoint(0, 0));
            }
        });

    // ===== 陳腐化した TopSegment のクランプ(3 者が同じ寄せ方をすること)=====
    //
    // SetTopPosition はセグメントをクランプしない(実セグメント数を知るには行全体の Wrap が要り
    // I-4 に反するため)。したがって「編集で段落が縮み _topSegment が実数以上になった」状態は
    // テストから直接作れる。この状態で ViewportLayout.Build / ComputeCaretPoint /
    // OffsetFromClientPoint の 3 者が<b>同じ最終セグメントへ寄せる</b>ことを以下 2 本で固定する
    // (Task 3 の WalkForwardVisualRows_ClampsStaleSegment_LandsOnNextLineHead と対称)。

    /// <summary>
    /// 陳腐化した TopSegment でのヒットテスト。<c>OffsetFromClientPoint</c> の
    /// <c>Math.Min(segIdx, segs.Count - 1)</c> を外すと <c>segs[99]</c> で
    /// ArgumentOutOfRangeException=クリック 1 回でクラッシュする(equivalent mutant ではない)。
    /// 寄せ先が <c>ViewportLayout.Build</c> と一致することも同じテストで確認する。
    /// </summary>
    [Fact]
    public void OffsetFromClientPoint_ClampsStaleTopSegment_ToLastSegment() =>
        Sta.Run(() =>
        {
            // "abcdefghij" は wrap=2 で 5 セグメント(最終セグメントは offset 8..9)。
            var (f, c) = MakeControl("abcdefghij", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 99); // 実セグメント数(5)を大きく超える陳腐化した値
                Assert.Equal(99, c.TopSegment); // SetTopPosition 自体は寄せない

                // Build の寄せ先=最終セグメント(offset 8)。
                Assert.Equal(8, c.GetVisibleCharRange().Start);
                // ヒットテストも同じ寄せ先を返す(投げない)。
                Assert.Equal(8, c.OffsetFromClientPoint(0, 0));
            }
        });

    /// <summary>
    /// 陳腐化した TopSegment での座標算出。<c>ComputeCaretPoint</c> の積み上げにある
    /// <c>Math.Min(skip, segs.Count - 1)</c> を外すと積み上げが負(5 - 99)になり、
    /// 巨大な負の Y を「可視」として返す(equivalent mutant ではない)。
    /// </summary>
    [Fact]
    public void ComputeCaretPoint_ClampsStaleTopSegment_ToLastSegment() =>
        Sta.Run(() =>
        {
            // 論理行 0 = 5 セグメント・論理行 1 = 3 セグメント。TopSegment=99 は論理行 0 の
            // 最終セグメントへ寄るので、論理行 1 の先頭は可視域の 2 本目=視覚行 1。
            var (f, c) = MakeControl("abcdefghij\nklmnop", wrap: 2, visibleRows: 6);
            using (f)
            using (c)
            {
                int lh = c.LineHeightPx;
                c.SetTopPosition(0, 99);

                // Build の寄せ先=論理行 0 の最終セグメント(offset 8)。
                Assert.Equal(8, c.GetVisibleCharRange().Start);

                var (_, y, visible) = c.ComputeCaretPoint(11); // 論理行 1 の先頭(k)
                Assert.True(visible, "論理行 1 の先頭は可視域内");
                Assert.Equal(1 * lh, y);
            }
        });

    /// <summary>
    /// 積み上げループの Wrap 予算に<b>読み飛ばす <c>skip</c> 本ぶんが乗っている</b>こと
    /// (<c>needed = maxUsefulRows - visualRowsBeforeThisLine + skip</c>)。先頭論理行は
    /// 画面外に <c>_topSegment</c> 本を持つので「可視分 + 読み飛ばし分」を要求しないと
    /// 打ち切りが浅すぎ、直後の <c>Math.Min(skip, segs.Count - 1)</c> が<b>打ち切り後の</b>
    /// 最終セグメントへ誤って寄せる。結果、積み上げが実際より縮み
    /// <b>可視域の外にある視覚行を「可視」として返す</b>(equivalent mutant ではない)。
    /// 実害は UIA <c>GetBoundingRectangles</c> / <c>PointFromCharOffset</c> /
    /// システムキャレット位置が誤った視覚行を指すこと=SR 経路。
    /// </summary>
    /// <remarks>
    /// 同型の <c>+ skip</c> は <c>CountVisualRowsForward</c> と <c>ViewportLayout.Build</c> にも
    /// あり、そちらには既に網がある。ここが 3 兄弟で唯一の穴だった(最終レビュー品質パス)。
    /// 穴になった理由は既存 fixture の <c>visibleRows</c> が先頭行のセグメント数以上で、
    /// <b>打ち切りが一度も噛まなかった</b>こと=可視行数 &lt; 先頭行の残りセグメント数、が
    /// この網の要である。
    /// </remarks>
    [Fact]
    public void ComputeCaretPoint_BudgetsSkippedRows_WhenLeadingLineIsTruncated() =>
        Sta.Run(() =>
        {
            // 論理行 0 = 20 文字(wrap=2 → 視覚行 10 本)・論理行 1 = "klmnop"。
            // 可視 3 行なので、先頭行の Wrap は必ず打ち切られる。
            var (f, c) = MakeControl(new string('a', 20) + "\nklmnop", wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                var snap = c.Buffer!.Current;
                Assert.Equal(10, SegCount(c, snap, 0)); // fixture 前提: 先頭行は 10 視覚行
                int lh = c.LineHeightPx;
                const int Line1Head = 21; // 20 文字 + "\n"

                // (a) TopSegment=5 → 論理行 1 の先頭は視覚行 10 - 5 = 5。可視 3 本の外=不可視。
                //     予算から skip を落とすと先頭行を 3 本しか Wrap せず、
                //     eff = Math.Min(5, 3 - 1) = 2 → 積み上げ 1 → Y = lh で「可視」と誤答する。
                c.SetTopPosition(0, 5);
                Assert.False(
                    c.ComputeCaretPoint(Line1Head).Visible,
                    "可視域の外(視覚行 5 / 可視 3 本)を可視と報告している"
                );

                // (b) TopSegment=8 → 同じ位置が視覚行 2 = 可視域の 3 本目。座標も固定して
                //     「常に不可視を返す」変異と弁別する(予算を落とすとここは Y = lh になる)。
                c.SetTopPosition(0, 8);
                var (_, y, visible) = c.ComputeCaretPoint(Line1Head);
                Assert.True(visible, "視覚行 2 は可視域内");
                Assert.Equal(2 * lh, y);
            }
        });

    // ===== A-6: 折り返し ON の追従スクロール =====

    /// <summary>各段落が複数視覚行になる文書。段落数 × 段落あたりの文字数で作る。</summary>
    private static string Paragraphs(int count, int charsPerParagraph) =>
        string.Join(
            "\n",
            Enumerable
                .Range(0, count)
                .Select(i => new string((char)('a' + (i % 26)), charsPerParagraph))
        );

    /// <summary>OnKeyDown(protected)を 1 回叩く。</summary>
    private static void KeyDown(EditorControl c, Keys keys)
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.True(mi is not null, "EditorControl に protected OnKeyDown が見つからない");
        try
        {
            mi!.Invoke(c, new object[] { new KeyEventArgs(keys) });
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw; // 到達しない
        }
    }

    /// <summary>
    /// キャレットが可視域に入っていること。<c>PointFromCharOffset</c> は不可視を
    /// <c>Point.Empty</c> で表すが <c>Point.Empty == new Point(0, 0)</c> なので、
    /// 可視域最上段の行頭(座標も (0,0))と区別できない。可視性は必ずフラグで見る
    /// (<see cref="PointFromCharOffset_ReturnsEmpty_ForRowsAboveTopSegment"/> と同じ理由)。
    /// </summary>
    private static void AssertCaretVisible(EditorControl c, string because) =>
        Assert.True(c.ComputeCaretPoint(c.CaretCharOffset).Visible, because);

    /// <summary>
    /// ↓ を押し続ける間の起点の位置を全打鍵で固定する。可視域を 1 本ずつ送る実装では、
    /// キャレットが初期可視域を出た後は<b>常に最下段</b>に居続ける(PR #45 が確立した
    /// 「ジャンプ先は下端」の連続版)。最終状態だけを見ると、文書末では起点が上限に
    /// 張り付いて<b>1 本ずれた実装でも同じ値に収束してしまう</b>ため、道中で押さえる。
    /// </summary>
    private static void AssertCaretOnBottomRow(
        EditorControl c,
        TextSnapshot snap,
        List<(int Line, int Seg)> rows,
        int visibleRows,
        int step
    )
    {
        int caretIdx = rows.IndexOf(LocateRow(c, snap, c.CaretCharOffset));
        int topIdx = rows.IndexOf((c.TopLine, c.TopSegment));
        Assert.True(caretIdx >= 0, $"{step} 回目: キャレットの視覚行が列挙に無い");
        Assert.True(topIdx >= 0, $"{step} 回目: 起点が実在の視覚行でない");
        Assert.Equal(Math.Max(0, caretIdx - (visibleRows - 1)), topIdx);
    }

    [Fact]
    public void KeyDown_Down_WithWrap_KeepsCaretVisible() =>
        Sta.Run(() =>
        {
            // 1 段落 = 5 視覚行(10 文字 / wrap=2)、可視 6 行。
            // 修正前は論理行が可視行数(6)に達するまで TopLine が動かず、
            // 2 段落目の途中でキャレットが可視域外へ出たまま戻らなかった。
            var (f, c) = MakeControl(Paragraphs(8, 10), wrap: 2, visibleRows: 6);
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(0);
                for (int i = 0; i < 20; i++)
                {
                    KeyDown(c, Keys.Down);
                    AssertCaretVisible(c, $"{i + 1} 回目の ↓ でキャレットが可視域外へ出た");
                }
                Assert.True(c.TopLine > 0 || c.TopSegment > 0, "画面が 1 度も追従していない");
            }
        });

    [Fact]
    public void KeyDown_Down_SingleHugeLogicalLine_ScrollsByVisualRows() =>
        Sta.Run(() =>
        {
            // 論理行 1 本だけの文書。修正前は TopLine が 0 から動かず(maxLine=0)、
            // 先頭 visibleRows 本より下へ到達する手段が無かった。
            var (f, c) = MakeControl(new string('a', 200), wrap: 2, visibleRows: 4);
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(0);
                for (int i = 0; i < 10; i++)
                    KeyDown(c, Keys.Down);

                Assert.Equal(0, c.TopLine); // 論理行は 1 本しかない
                Assert.True(
                    c.TopSegment > 0,
                    "TopSegment が進んでいない=視覚行スクロールしていない"
                );
                AssertCaretVisible(c, "巨大 1 行でキャレットが可視域外へ出た");
            }
        });

    [Fact]
    public void KeyDown_Up_WithWrap_ScrollsBackToTop() =>
        Sta.Run(() =>
        {
            // 下端まで降りてから ↑ で戻り、TopSegment が 0 まで戻ること。
            var (f, c) = MakeControl(new string('a', 200), wrap: 2, visibleRows: 4);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 20);
                c.SetCaretCharOffset(40); // 視覚行 20 の先頭
                Assert.Equal(20, c.TopSegment); // fixture 前提: 既に可視なので追従で動かない
                for (int i = 0; i < 25; i++)
                    KeyDown(c, Keys.Up);

                Assert.Equal(0, c.TopSegment);
                Assert.Equal(0, c.CaretCharOffset);
            }
        });

    [Fact]
    public void BringCaretIntoView_WithWrap_NoOp_WhenCaretAlreadyVisible() =>
        Sta.Run(() =>
        {
            // no-change テストは非既定位置から始める(既定 0 と区別する)。
            var (f, c) = MakeControl(new string('a', 200), wrap: 2, visibleRows: 4);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 10);
                c.SetCaretCharOffset(22); // 視覚行 11 = 可視域の 2 本目
                c.SetTopPosition(0, 10); // SetCaretCharOffset 自体の追従で動いた分を戻す
                c.BringCaretIntoView();
                Assert.Equal(10, c.TopSegment);
            }
        });

    [Fact]
    public void EnsureVisibleCharRange_WithWrap_PutsTargetAtBottom() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(new string('a', 200), wrap: 2, visibleRows: 4);
            using (f)
            using (c)
            {
                c.EnsureVisibleCharRange(100, 0); // 視覚行 50
                // 対象を下端に寄せる=起点は 50 - (4 - 1) = 47
                Assert.Equal(0, c.TopLine);
                Assert.Equal(47, c.TopSegment);
            }
        });

    /// <summary>
    /// 「起点より上か」を<b>辞書順比較で弁別する第 1 分岐が存在すること</b>を固定する。
    /// 距離だけで判断すると <c>CountVisualRowsForward</c> は前方距離しか返さない
    /// (同一論理行は <c>Math.Max(0, toSeg - fromSeg)</c>・手前の論理行は 0)ため、
    /// キャレットが起点より<b>上</b>にあっても距離 0 =「可視」と誤判定し画面が追従しない。
    /// 同一論理行内・論理行跨ぎの両方を 1 本で押さえる。
    /// <para>
    /// load-bearing なのは<b>分岐の存在</b>であって<b>2 分岐の記述順ではない</b>。
    /// 両条件は排他(第 1 分岐が真のとき距離は必ず 0 で、<c>visibleRows &gt;= 1</c> より
    /// 第 2 分岐は偽)なので、記述順を入れ替える変異は equivalent mutant で kill できない
    /// (実装側 remarks と同じ結論。当初「順序が load-bearing」と書いていたのは誤り)。
    /// </para>
    /// </summary>
    [Fact]
    public void BringCaretIntoView_ScrollsUp_WhenCaretIsAboveTop() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(OracleText, wrap: 2, visibleRows: 4);
            using (f)
            using (c)
            {
                var snap = TextBuffer.FromString(OracleText).Current;
                int lastLine = snap.LineCount - 1;
                Assert.True(SegCount(c, snap, lastLine) >= 7, "fixture 前提: 末尾行は視覚行が多い");
                int lastLineStart = snap.GetLineStart(lastLine);

                // (a) 同一論理行内で起点より上。距離は Math.Max(0, 2 - 6) = 0 になる。
                c.SetCaretCharOffset(lastLineStart + 4); // 末尾行の視覚行 2
                c.SetTopPosition(lastLine, 6);
                Assert.Equal((lastLine, 6), (c.TopLine, c.TopSegment));
                c.BringCaretIntoView();
                Assert.Equal((lastLine, 2), (c.TopLine, c.TopSegment));
                AssertCaretVisible(c, "同一論理行内で上へ追従していない");

                // (b) 起点より上の論理行。CountVisualRowsForward は toLine < fromLine で 0 を返す。
                c.SetCaretCharOffset(2); // 論理行 0 の視覚行 1
                c.SetTopPosition(lastLine, 4);
                Assert.Equal((lastLine, 4), (c.TopLine, c.TopSegment));
                c.BringCaretIntoView();
                Assert.Equal((0, 1), (c.TopLine, c.TopSegment));
                AssertCaretVisible(c, "論理行跨ぎで上へ追従していない");
            }
        });

    /// <summary>
    /// 陳腐化した <c>_topSegment</c>(編集で先頭段落が縮んでも _topSegment はリセットしない設計)
    /// からの<b>自己修復</b>を固定する。この状態では描画と可視判定が食い違う:
    /// <c>ViewportLayout.Build</c> は最終セグメントへクランプして描くが、
    /// <c>ComputeCaretPoint</c> の <c>segIdx &lt; _topSegment</c> はその最終セグメントも
    /// 不可視と報告する。編集経路(<c>AfterEdit</c>)が必ず呼ぶ <see cref="EditorControl.BringCaretIntoView"/> の
    /// <b>第 1 分岐(辞書順で「起点より上」)</b>が発火して起点をキャレットの実在視覚行へ
    /// 寄せ直すことで、1 フレーム内に整合が回復する。この第 1 分岐<b>そのものを落とす</b>と
    /// 距離 0 =「可視」と誤判定して修復しない(load-bearing なのは分岐の存在であって
    /// 2 分岐の記述順ではない=<see cref="BringCaretIntoView_ScrollsUp_WhenCaretIsAboveTop"/> の
    /// doc 参照)。
    /// <para>
    /// 自己修復が働くのは<b>キャレットが起点の論理行(またはそれより上)にある場合に限る</b>。
    /// キャレットが <c>_topLine</c> より後の論理行にあると第 1 分岐は発火せず、第 2 分岐も
    /// <c>CountVisualRowsForward</c> が陳腐化した起点セグメントを最終セグメントへ寄せて
    /// 数えるため「可視」と判定するので、陳腐化した <c>_topSegment</c> はそのまま残る
    /// (設計書 申し送り S-6)。本テストの fixture は前者の条件を踏んでいる。
    /// </para>
    /// </summary>
    [Fact]
    public void BringCaretIntoView_SelfHealsStaleTopSegment() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(OracleText, wrap: 2, visibleRows: 4);
            using (f)
            using (c)
            {
                var snap = TextBuffer.FromString(OracleText).Current;
                int lastLine = snap.LineCount - 1;
                int segs = SegCount(c, snap, lastLine);
                Assert.True(segs >= 3, "fixture 前提: 末尾行は複数の視覚行を持つ");
                int lastLineStart = snap.GetLineStart(lastLine);
                int caret = lastLineStart + 4; // 末尾行の視覚行 2

                c.SetCaretCharOffset(caret);
                c.SetTopPosition(lastLine, segs + 20); // 実セグメント数を超える陳腐化した起点
                Assert.Equal(segs + 20, c.TopSegment); // SetTopPosition 自体は寄せない

                // 陳腐化した状態=描画(Build)と可視判定(ComputeCaretPoint)が食い違う。
                int drawnRowStart = snap.GetLineStart(lastLine) + 2 * (segs - 1);
                Assert.Equal(
                    drawnRowStart,
                    c.GetVisibleCharRange().Start // Build は最終セグメントへクランプして描く
                );
                // 食い違いの<b>本体</b>: Build が最上段に描いているセグメント (segs-1) 自身を、
                // ComputeCaretPoint の segIdx < _topSegment が不可視と報告する。
                // (キャレット側のセグメント 2 は描画起点より実際に上なので、陳腐化が無くても
                //  不可視になる=それだけでは食い違いの証明にならない。)
                Assert.False(
                    c.ComputeCaretPoint(drawnRowStart).Visible,
                    "fixture 前提: 描画中の行そのものが不可視と報告される(=食い違いの本体)"
                );
                Assert.False(
                    c.ComputeCaretPoint(caret).Visible,
                    "fixture 前提: 陳腐化した起点ではキャレットも「不可視」になる"
                );

                c.BringCaretIntoView();

                Assert.Equal((lastLine, 2), (c.TopLine, c.TopSegment)); // 実在の視覚行へ寄った
                AssertCaretVisible(c, "陳腐化した TopSegment から自己修復していない");
            }
        });

    /// <summary>
    /// スクロール判断を全 (起点視覚行 × キャレット位置) で総当りし、視覚行を列挙したオラクルと
    /// 一致することを固定する。オラクルは <see cref="EnumerateRows"/> の平坦な index 差だけで
    /// 組むので、判定順序・距離計算・遡り歩きのどれを変異させても伝播しない。
    /// fixture は複数論理行 × 複数視覚行 × 空行 2 連 × 文書頭 / 文書末をすべて踏む。
    /// </summary>
    [Fact]
    public void BringCaretIntoView_MatchesRowOracle_ForAllStartsAndCarets() =>
        Sta.Run(() =>
        {
            const int VisibleRows = 4;
            var (f, c) = MakeControl(OracleText, wrap: 2, visibleRows: VisibleRows);
            using (f)
            using (c)
            {
                var snap = TextBuffer.FromString(OracleText).Current;
                var rows = EnumerateRows(c, snap);
                Assert.True(
                    rows.Count > VisibleRows + 2,
                    "fixture 前提: 可視行数より十分多い視覚行がある"
                );

                for (int offset = 0; offset <= snap.CharLength; offset++)
                {
                    c.SetCaretCharOffset(offset);
                    int caretIdx = rows.IndexOf(LocateRow(c, snap, offset));
                    Assert.True(caretIdx >= 0, $"offset {offset} の視覚行が列挙に無い");

                    for (int startIdx = 0; startIdx < rows.Count; startIdx++)
                    {
                        c.SetTopPosition(rows[startIdx].Line, rows[startIdx].Seg);
                        c.BringCaretIntoView();

                        int expectedIdx;
                        if (caretIdx < startIdx)
                            expectedIdx = caretIdx; // 上へはみ出し=キャレット行を最上段へ
                        else if (caretIdx - startIdx >= VisibleRows)
                            expectedIdx = Math.Max(0, caretIdx - (VisibleRows - 1)); // 下端へ寄せる
                        else
                            expectedIdx = startIdx; // 既に可視=動かさない

                        Assert.Equal(rows[expectedIdx], (c.TopLine, c.TopSegment));
                        Assert.True(
                            c.ComputeCaretPoint(offset).Visible,
                            $"offset {offset} / 起点 {rows[startIdx]} でキャレットが可視域外"
                        );
                    }
                }
            }
        });

    /// <summary>
    /// UIA <c>ScrollIntoView</c> 経路(<c>ScrollCharRangeIntoView</c>)も同じ判定を使うこと。
    /// alignToTop=true は対象の視覚行を最上段へ、false は最下段へ寄せる。
    /// 修正前は論理行だけで判定していたため、巨大 1 行の文書では TopLine が動かせず
    /// SR のレビューカーソルが画面外へ出たままになった。
    /// </summary>
    [Fact]
    public void ScrollCharRangeIntoView_WithWrap_UsesVisualRows() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(new string('a', 200), wrap: 2, visibleRows: 4);
            using (f)
            using (c)
            {
                c.ScrollCharRangeIntoView(100, 120, alignToTop: true); // 視覚行 50 を最上段へ
                Assert.Equal((0, 50), (c.TopLine, c.TopSegment));

                c.ScrollCharRangeIntoView(100, 120, alignToTop: false); // 視覚行 60 を最下段へ
                Assert.Equal((0, 60 - (4 - 1)), (c.TopLine, c.TopSegment));
            }
        });

    /// <summary>
    /// <c>ScrollCharRangeIntoView</c> の「既に可視なら垂直は動かさない」契約(SR が歩くたびに
    /// 画面が飛ばないための判断)が折り返し ON でも保たれること。非既定位置から始める。
    /// </summary>
    [Fact]
    public void ScrollCharRangeIntoView_WithWrap_KeepsTop_WhenTargetAlreadyVisible() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(new string('a', 200), wrap: 2, visibleRows: 4);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 10);
                // 可視域の<b>途中</b>(2 本目)を狙う。最下段(視覚行 13)を狙うと
                // 「下端へ寄せ直す」誤実装が偶然同じ起点を出してしまい変異を弁別できない。
                c.ScrollCharRangeIntoView(20, 22, alignToTop: false); // 視覚行 11
                Assert.Equal((0, 10), (c.TopLine, c.TopSegment));
            }
        });

    /// <summary>
    /// <c>ScrollCharRangeIntoView</c> の粗い否定(対象論理行が [TopLine, TopLine+visibleRows) の
    /// 外なら視覚行を計算せず不可視と断じる)が、論理行跨ぎでも正しい起点へ寄せること。
    /// 空行 2 連を含む fixture で、対象論理行が可視域より下・上の両方を踏む。
    /// </summary>
    [Fact]
    public void ScrollCharRangeIntoView_WithWrap_ScrollsAcrossLogicalLines() =>
        Sta.Run(() =>
        {
            const int VisibleRows = 4;
            var (f, c) = MakeControl(OracleText, wrap: 2, visibleRows: VisibleRows);
            using (f)
            using (c)
            {
                var snap = TextBuffer.FromString(OracleText).Current;
                var rows = EnumerateRows(c, snap);
                int lastLine = snap.LineCount - 1;
                int target = snap.GetLineStart(lastLine) + 6; // 末尾行の視覚行 3
                int targetIdx = rows.IndexOf((lastLine, 3));
                Assert.True(targetIdx >= VisibleRows, "fixture 前提: 対象は初期可視域より下");

                // 下方向: 対象を最下段へ寄せる。
                c.ScrollCharRangeIntoView(target, target, alignToTop: false);
                Assert.Equal(rows[targetIdx - (VisibleRows - 1)], (c.TopLine, c.TopSegment));

                // 上方向: 文書頭の視覚行 1 を最上段へ。
                c.ScrollCharRangeIntoView(2, 2, alignToTop: true);
                Assert.Equal((0, 1), (c.TopLine, c.TopSegment));
            }
        });

    /// <summary>
    /// 対象が起点と<b>同じ論理行</b>にあり、かつ TopSegment より上にあるケース。
    /// <c>ScrollCharRangeIntoView</c> の粗い否定は論理行でしか弾かないので、この位置は
    /// 辞書順の弁別(<c>row.Seg &gt;= _topSegment</c>)だけが「不可視」と判定できる。
    /// 距離だけで判定すると <c>CountVisualRowsForward</c> が 0 を返して「既に可視」となり、
    /// 早期リターンで画面が動かない=SR のレビューカーソルが画面外に取り残される。
    /// </summary>
    [Fact]
    public void ScrollCharRangeIntoView_WithWrap_ScrollsUp_WhenTargetIsAboveTopSegment() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(new string('a', 200), wrap: 2, visibleRows: 4);
            using (f)
            using (c)
            {
                c.SetTopPosition(0, 50);
                c.ScrollCharRangeIntoView(40, 40, alignToTop: true); // 視覚行 20 を最上段へ
                Assert.Equal((0, 20), (c.TopLine, c.TopSegment));

                c.SetTopPosition(0, 50);
                c.ScrollCharRangeIntoView(40, 40, alignToTop: false); // 視覚行 20 を最下段へ
                Assert.Equal((0, 20 - (4 - 1)), (c.TopLine, c.TopSegment));
            }
        });

    /// <summary>
    /// <c>ScrollCharRangeIntoView</c> を全 (起点視覚行 × 対象位置 × alignToTop) で総当りし、
    /// 視覚行を列挙したオラクルと一致することを固定する
    /// (<see cref="BringCaretIntoView_MatchesRowOracle_ForAllStartsAndCarets"/> の UIA 版)。
    /// 「既に可視なら垂直は動かさない」契約もこの網に含まれる。
    /// キャレット / アンカーを動かさないこと(装飾スクロール)も併せて確認する。
    /// </summary>
    [Fact]
    public void ScrollCharRangeIntoView_MatchesRowOracle_ForAllStartsAndTargets() =>
        Sta.Run(() =>
        {
            const int VisibleRows = 4;
            var (f, c) = MakeControl(OracleText, wrap: 2, visibleRows: VisibleRows);
            using (f)
            using (c)
            {
                var snap = TextBuffer.FromString(OracleText).Current;
                var rows = EnumerateRows(c, snap);
                Assert.True(
                    rows.Count > VisibleRows + 2,
                    "fixture 前提: 可視行数より十分多い視覚行がある"
                );

                for (int offset = 0; offset <= snap.CharLength; offset++)
                {
                    int targetIdx = rows.IndexOf(LocateRow(c, snap, offset));
                    Assert.True(targetIdx >= 0, $"offset {offset} の視覚行が列挙に無い");

                    for (int startIdx = 0; startIdx < rows.Count; startIdx++)
                    {
                        foreach (bool alignToTop in new[] { true, false })
                        {
                            c.SetTopPosition(rows[startIdx].Line, rows[startIdx].Seg);
                            c.ScrollCharRangeIntoView(offset, offset, alignToTop);

                            int expectedIdx;
                            if (targetIdx >= startIdx && targetIdx - startIdx < VisibleRows)
                                expectedIdx = startIdx; // 既に可視=動かさない
                            else if (alignToTop)
                                expectedIdx = targetIdx; // 対象を最上段へ
                            else
                                expectedIdx = Math.Max(0, targetIdx - (VisibleRows - 1)); // 最下段へ

                            Assert.Equal(rows[expectedIdx], (c.TopLine, c.TopSegment));
                            Assert.True(
                                c.ComputeCaretPoint(offset).Visible,
                                $"offset {offset} / 起点 {rows[startIdx]} / alignToTop={alignToTop} "
                                    + "で対象が可視域外"
                            );
                        }
                    }
                }

                // 装飾スクロールなのでキャレット / アンカーは動かない。
                Assert.Equal(0, c.CaretCharOffset);
                Assert.Equal(0, c.SelectionAnchor);
            }
        });

    /// <summary>
    /// A-6 の<b>中核症状</b>=「先頭 visibleRows 本より下が恒久的に到達不能」を固定する。
    /// 論理行 1 本の文書では TopLine が原理的に動かせないため、修正前は ↓ を何度押しても
    /// 起点が 1 度も動かず、4 回目でキャレットが可視域外へ出たまま戻らなかった。
    /// 最終視覚行へ届く回数まで押し切り、(a) 毎回可視であること (b) 最終視覚行に到達し
    /// 起点が最大位置(最終視覚行が最下段)まで進むこと の 2 つを assert する。
    /// </summary>
    [Fact]
    public void KeyDown_Down_SingleHugeLogicalLine_ReachesLastVisualRow() =>
        Sta.Run(() =>
        {
            const int VisibleRows = 4;
            const string Text = "aaaaaaaaaaaaaaaaaaaa";
            var (f, c) = MakeControl(
                string.Concat(Enumerable.Repeat(Text, 10)), // 200 文字 = 視覚行 100 本
                wrap: 2,
                visibleRows: VisibleRows
            );
            using (f)
            using (c)
            {
                var snap = c.Buffer!.Current;
                var rows = EnumerateRows(c, snap);
                Assert.Equal(1, snap.LineCount); // fixture 前提: 論理行は 1 本だけ
                Assert.True(rows.Count > VisibleRows * 4, "fixture 前提: 視覚行が十分多い");

                c.SetCaretCharOffset(0);
                for (int i = 0; i < rows.Count + VisibleRows * 10; i++)
                {
                    KeyDown(c, Keys.Down);
                    AssertCaretVisible(c, $"{i + 1} 回目の ↓ でキャレットが可視域外へ出た");
                    AssertCaretOnBottomRow(c, snap, rows, VisibleRows, i + 1);
                }

                // (b) 最終視覚行へ到達し、起点も最大位置まで進んでいる。
                Assert.Equal(rows[^1], LocateRow(c, snap, c.CaretCharOffset));
                Assert.Equal(rows[rows.Count - VisibleRows], (c.TopLine, c.TopSegment));
            }
        });

    /// <summary>
    /// 段落版(複数論理行 × 複数視覚行)の到達テスト。
    /// <see cref="KeyDown_Down_SingleHugeLogicalLine_ReachesLastVisualRow"/> と対称。
    /// </summary>
    [Fact]
    public void KeyDown_Down_WithWrap_ReachesLastVisualRow() =>
        Sta.Run(() =>
        {
            const int VisibleRows = 6;
            var (f, c) = MakeControl(Paragraphs(8, 10), wrap: 2, visibleRows: VisibleRows);
            using (f)
            using (c)
            {
                var snap = c.Buffer!.Current;
                var rows = EnumerateRows(c, snap);
                Assert.Equal(8, snap.LineCount); // fixture 前提: 論理行は複数本ある
                Assert.True(rows.Count > VisibleRows * 4, "fixture 前提: 視覚行が十分多い");

                c.SetCaretCharOffset(0);
                for (int i = 0; i < rows.Count + VisibleRows * 10; i++)
                {
                    KeyDown(c, Keys.Down);
                    AssertCaretVisible(c, $"{i + 1} 回目の ↓ でキャレットが可視域外へ出た");
                    AssertCaretOnBottomRow(c, snap, rows, VisibleRows, i + 1);
                }

                Assert.Equal(rows[^1], LocateRow(c, snap, c.CaretCharOffset));
                Assert.Equal(rows[rows.Count - VisibleRows], (c.TopLine, c.TopSegment));
            }
        });

    // ===== A-6: ホイールを視覚行送りにする(Task 6)=====

    /// <summary>
    /// <c>OnMouseWheel</c>(protected)を 1 ノッチぶん叩く。<paramref name="delta"/> は
    /// WM_MOUSEWHEEL と同じ符号規約で、負 = 下方向スクロール。
    /// </summary>
    private static void Wheel(EditorControl c, int delta)
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnMouseWheel",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.True(mi is not null, "EditorControl に protected OnMouseWheel が見つからない");
        try
        {
            mi!.Invoke(c, new object[] { new MouseEventArgs(MouseButtons.None, 0, 0, 0, delta) });
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw; // 到達しない
        }
    }

    /// <summary>
    /// 論理行 1 本の文書でホイールが視覚行を送ること。修正前は <c>TopLine</c> セッターの
    /// <c>ClampTopLine</c>(maxLine = 0)に潰されてホイールが<b>完全に効かなかった</b>。
    /// </summary>
    /// <remarks>
    /// 上方向は「TopSegment が減ったこと」では足りない: 上のループだけ <c>TopLine</c> 代入の
    /// ままにする変異でも、論理行が 1 本なので <c>clamped == _topLine &amp;&amp; _topSegment != 0</c>
    /// で早期リターンせず <c>_topSegment = 0</c> に落ちる=「減った」ように見えて生存する。
    /// 1 ノッチの視覚行数 <c>step</c> を実測し、往復の各段で<b>正確な位置</b>を固定する。
    /// </remarks>
    [Fact]
    public void MouseWheel_WithWrap_ScrollsByVisualRows() =>
        Sta.Run(() =>
        {
            const int VisibleRows = 4;
            // 2000 文字 / wrap=2 = 視覚行 1000 本。ノッチ量(MouseWheelScrollLines)が
            // どんな環境値でも 4 ノッチが文書末に当たらない余裕を取る。
            var (f, c) = MakeControl(new string('a', 2000), wrap: 2, visibleRows: VisibleRows);
            using (f)
            using (c)
            {
                var snap = c.Buffer!.Current;
                Assert.Equal(1, snap.LineCount); // fixture 前提: 論理行は 1 本だけ

                Wheel(c, -120);
                int step = c.TopSegment; // 1 ノッチの視覚行数(環境の MouseWheelScrollLines 依存)
                Assert.True(step > 0, "ホイール下方向で TopSegment が進んでいない");
                // 1 ノッチの「絶対量」を固定する。以降の assert は notch * step の線形性と
                // 往復の可逆性しか見ないため、送り量そのものがずれる変異(wheelLines のオフセット)
                // を殺せない(Task 6 レビュー Minor 1 で実際に生存した)。
                // ここは実装ロジックの複製ではなく、OnMouseWheel と同じ環境 API を参照している
                // (SystemInformation.MouseWheelScrollLines は「1 ページ」設定で -1 を返す仕様なので
                //  <= 0 は WinForms 標準の既定値 3 にフォールバックする)。
                int expectedStep =
                    SystemInformation.MouseWheelScrollLines <= 0
                        ? 3
                        : SystemInformation.MouseWheelScrollLines;
                Assert.Equal(expectedStep, step);
                Assert.Equal(0, c.TopLine); // 論理行は 1 本しかない
                Assert.True(
                    4 * step < EnumerateRows(c, snap).Count,
                    "fixture 前提: 4 ノッチでも文書末に当たらない"
                );

                for (int notch = 2; notch <= 4; notch++)
                {
                    Wheel(c, -120);
                    Assert.Equal(notch * step, c.TopSegment);
                }

                for (int notch = 3; notch >= 1; notch--)
                {
                    Wheel(c, 120);
                    Assert.Equal(notch * step, c.TopSegment);
                }
            }
        });

    /// <summary>
    /// 論理行を跨ぐホイール送り(単一論理行 fixture では覆えない積み上げ / 遡りを踏む)。
    /// 期待値は視覚行を全列挙したオラクルの index で表し、歩き側のロジックに依存させない。
    /// </summary>
    [Fact]
    public void MouseWheel_WithWrap_ScrollsAcrossLogicalLines() =>
        Sta.Run(() =>
        {
            const int VisibleRows = 4;
            // 1 段落 = 5 視覚行 × 8 段落 = 40 視覚行。
            var (f, c) = MakeControl(Paragraphs(8, 10), wrap: 2, visibleRows: VisibleRows);
            using (f)
            using (c)
            {
                var snap = c.Buffer!.Current;
                var rows = EnumerateRows(c, snap);
                Assert.Equal(8, snap.LineCount); // fixture 前提: 論理行は複数本ある

                Wheel(c, -120);
                int step = rows.IndexOf((c.TopLine, c.TopSegment));
                Assert.True(step > 0, "ホイール下方向で起点が進んでいない");
                Assert.True(4 * step < rows.Count, "fixture 前提: 4 ノッチでも文書末に当たらない");

                for (int notch = 2; notch <= 4; notch++)
                {
                    Wheel(c, -120);
                    Assert.Equal(rows[notch * step], (c.TopLine, c.TopSegment));
                }
                Assert.True(c.TopLine > 0, "fixture 前提: 4 ノッチで論理行を跨いでいる");

                for (int notch = 3; notch >= 1; notch--)
                {
                    Wheel(c, 120);
                    Assert.Equal(rows[notch * step], (c.TopLine, c.TopSegment));
                }
            }
        });

    /// <summary>
    /// 起点が<b>論理行を跨いで</b>動いたとき VScrollBar のサムが追従すること
    /// (<see cref="EditorControl.SetTopPosition"/> の <c>_vscroll.Value</c> 同期)。
    /// </summary>
    /// <remarks>
    /// 折り返し ON では <c>SetTopPosition</c> が主スクロール経路(A-6 の本体)であり、
    /// キーボード追従・ホイールが論理行を跨いだときの<b>唯一のサム同期点</b>である。
    /// <c>UpdateVerticalScrollbar</c> は編集 / リサイズ時にしか走らないため、純粋な
    /// ナビゲーション中は復旧しない。同期の 2 行を落とすと「↓ を押し続けても /
    /// ホイールを回してもサムが動かず、次にサムを掴むと画面が飛ぶ」
    /// (CLAUDE.md §2「晴眼・弱視ユーザーも第一級」に直接効く・equivalent mutant ではない)。
    /// <para>
    /// 段落<b>途中</b>(同一論理行内)ではサムが動かないのは意識的な近似(設計書 §4.4 / S-3)。
    /// ここで固定するのは「論理行が変わったら追従する」ことだけである。
    /// </para>
    /// </remarks>
    [Fact]
    public void MouseWheel_WithWrap_KeepsVScrollValueInSyncWithTopLine() =>
        Sta.Run(() =>
        {
            // 1 段落 = 5 視覚行 × 8 段落 = 40 視覚行(論理行を何度も跨ぐ)。
            var (f, c) = MakeControl(Paragraphs(8, 10), wrap: 2, visibleRows: 4);
            using (f)
            using (c)
            {
                var bar = VScroll(c);
                Assert.Equal(8, c.Buffer!.Current.LineCount); // fixture 前提: 論理行は複数本
                Assert.Equal(0, bar.Value);

                var seenLines = new HashSet<int>();
                for (int notch = 0; notch < 20; notch++)
                {
                    Wheel(c, -120);
                    seenLines.Add(c.TopLine);
                    Assert.Equal(c.TopLine, bar.Value);
                }
                Assert.True(c.TopLine > 0, "fixture 前提: 下方向で論理行を跨いでいる");
                Assert.True(
                    seenLines.Count >= 3,
                    $"fixture 前提: 論理行を複数回跨いでいる(観測 {seenLines.Count} 種)"
                );

                // 上方向も追従する(下がったまま張り付かない)。
                for (int notch = 0; notch < 20; notch++)
                {
                    Wheel(c, 120);
                    Assert.Equal(c.TopLine, bar.Value);
                }
                Assert.Equal(0, c.TopLine); // 文書頭まで戻り切っていること
            }
        });

    /// <summary>
    /// 文書頭 / 文書末でのクランプ。上端側は非既定位置(TopSegment=1)から回して
    /// 「最初から 0 だった」と区別する。
    /// </summary>
    [Fact]
    public void MouseWheel_WithWrap_ClampsAtDocumentEnds() =>
        Sta.Run(() =>
        {
            const int VisibleRows = 4;
            var (f, c) = MakeControl(new string('a', 200), wrap: 2, visibleRows: VisibleRows);
            using (f)
            using (c)
            {
                var snap = c.Buffer!.Current;
                var rows = EnumerateRows(c, snap);
                Assert.Equal(1, snap.LineCount);
                Assert.True(rows.Count > VisibleRows * 4, "fixture 前提: 視覚行が十分多い");

                // 下端: 視覚行数ぶん回しても最終視覚行より下へは行かない。
                for (int i = 0; i < rows.Count; i++)
                    Wheel(c, -120);
                Assert.Equal(rows[^1], (c.TopLine, c.TopSegment));

                // 上端: 非既定位置から回しても負に回り込まず (0, 0) で止まる。
                c.SetTopPosition(0, 1);
                Assert.Equal(1, c.TopSegment);
                for (int i = 0; i < 5; i++)
                    Wheel(c, 120);
                Assert.Equal((0, 0), (c.TopLine, c.TopSegment));
            }
        });

    /// <summary>
    /// 折り返し OFF は従来どおり論理行送り(I-3)。TopSegment は 1 度も立たない。
    /// </summary>
    [Fact]
    public void MouseWheel_WithoutWrap_StillMovesTopLine() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(Paragraphs(60, 4), wrap: 0, visibleRows: 4);
            using (f)
            using (c)
            {
                Wheel(c, -120);
                int step = c.TopLine; // 1 ノッチの論理行数
                Assert.True(step > 0, "ホイール下方向で TopLine が進んでいない");
                Assert.Equal(0, c.TopSegment);
                Assert.True(4 * step < 60, "fixture 前提: 4 ノッチでも文書末に当たらない");

                for (int notch = 2; notch <= 4; notch++)
                {
                    Wheel(c, -120);
                    Assert.Equal(notch * step, c.TopLine);
                    Assert.Equal(0, c.TopSegment);
                }

                for (int notch = 3; notch >= 1; notch--)
                {
                    Wheel(c, 120);
                    Assert.Equal(notch * step, c.TopLine);
                    Assert.Equal(0, c.TopSegment);
                }
            }
        });

    // ===== 最終レビュー脆弱性パス Low: int オーバーフローで負の index / 負の距離を返さない =====
    // seam は internal なので、production 経路(実在の起点しか渡らない)からは踏めないが、
    // 上限の破れが「負のセグメント index を Exhausted=false で返す」→ 呼び出し側の
    // OffsetFromClientPoint が List の負 index でクラッシュ、という形で表に出る。
    // 素の int 加算へ戻す変異でこの 2 件が赤くなる。

    [Fact]
    public void WalkForwardVisualRows_HugeDistance_DoesNotOverflowToNegativeSegment() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(Paragraphs(2, 10), wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                var snap = c.Buffer!.Current;

                // 起点は正常・距離だけ過大 → 文書末で打ち切り(Exhausted=true)
                var (line, seg, exhausted) = WalkForward(c, snap, 0, 1, int.MaxValue);
                Assert.True(exhausted, "文書末で打ち切られていない");
                Assert.True(seg >= 0, $"負のセグメント index が返った: {seg}");
                Assert.Equal(snap.LineCount - 1, line);

                // 起点が過大(陳腐化の極端形)。seg は最終セグメントへ寄せられるので
                // 1 本進むと次の論理行の先頭に着地する=歩き切れているので Exhausted は false。
                var (line2, seg2, exhausted2) = WalkForward(c, snap, 0, int.MaxValue, 1);
                Assert.True(seg2 >= 0, $"負のセグメント index が返った: {seg2}");
                Assert.Equal((1, 0, false), (line2, seg2, exhausted2));
            }
        });

    [Fact]
    public void CountVisualRowsForward_HugeTargetSegment_DoesNotOverflowToNegativeDistance() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(Paragraphs(2, 10), wrap: 2, visibleRows: 3);
            using (f)
            using (c)
            {
                var snap = c.Buffer!.Current;
                const int Cap = 4;
                int d = CountForward(c, snap, 0, 0, 1, int.MaxValue, Cap);
                // 負の距離は「起点より上=既に可視」と誤判定されるため、cap で頭打ちになること。
                Assert.Equal(Cap, d);
            }
        });
}
