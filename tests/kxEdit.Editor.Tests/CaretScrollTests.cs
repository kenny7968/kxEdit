using System.Linq;
using System.Runtime.InteropServices;
using kxEdit.Accessibility;

namespace kxEdit.Editor.Tests;

/// <summary>
/// P3 Task 7: キャレット追従スクロール(BringCaretIntoView + EnsureVisibleCharRange)の契約テスト。
/// - 垂直: caret 論理行が [TopLine, TopLine + visibleRows) 外なら TopLine 追従
/// - 水平: 折り返し OFF + HScroll 表示中で caret X が可視外なら ScrollX 追従
/// - EnsureVisibleCharRange: 範囲末尾を可視化しつつ caret/anchor は保存/復元
/// - SetSource 前は throw せず no-op
///
/// テスト値は MS ゴシック 12pt の実 LineHeight に依存するため相対比較で書く
/// (「TopLine が上端/下端に張り付く」「no-op」「呼び出し前と後の相対関係」)。
/// </summary>
public class CaretScrollTests
{
    // GetCaretPos は C-1 の回帰テストで使う(EditorControl 内部の NativeMethods は internal
    // かつ Task 12 まで InternalsVisibleTo を tests に付けない方針=Task 12 レビュー判断)なので
    // テスト側で個別に P/Invoke 宣言する。
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCaretPos(out System.Drawing.Point lpPoint);

    // ハンドル生成のため親フォームに載せてサイズを付ける。
    // GdiCharMetrics は SystemFonts 依存だが、LineHeightPx は行数計算のみに使うため
    // font 依存性は小さい(MS ゴシック 12pt で ~16-20px)。
    // 可視行数は明示的に高さから計算するため、テスト値は「TopLine 変化」の相対比較にする。
    private static (Form f, EditorControl c) MakeControl(string text, int width, int height)
    {
        var f = new Form { Size = new System.Drawing.Size(width, height) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    [Fact]
    public void BringCaretIntoView_ScrollsDown_WhenCaretBelowVisible() =>
        Sta.Run(() =>
        {
            // 10 行の文書、可視領域を 1 視覚行ぶんまで絞る(末尾行が必ず可視域外になる)。
            var text = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"line{i}"));
            // height は Form.Size なので ClientSize.Height は約 21 px=可視行数 1
            // (実測の詳細は MakeTallDocument の remarks 参照)。
            var (f, c) = MakeControl(text, width: 400, height: 60);
            using (f)
            using (c)
            {
                int lineHeight = c.LineHeightPx;
                int visibleRows = Math.Max(1, c.ClientSize.Height / lineHeight);

                // 末尾行(index 9)にキャレットを置いてから TopLine を先頭へ戻し、
                // BringCaretIntoView 単体がスクロールを起こすことを検証する。
                // 順序が逆(TopLine=0 → SetCaretCharOffset)だと、A-3 修正で setter 自身が
                // 追従スクロールするため assertion は BringCaretIntoView を呼ぶ前に既に
                // 満たされ、本テストの検証対象が BringCaretIntoView から SetCaretCharOffset へ
                // すり替わる(実測: 旧順序では c.BringCaretIntoView() の行を消しても緑のまま通る)。
                int lineStart = text.LastIndexOf('\n') + 1;
                c.SetCaretCharOffset(lineStart);
                c.TopLine = 0; // ★ caret を置いた「後」に可視域を先頭へ戻す
                c.BringCaretIntoView();

                // TopLine は少なくとも「末尾行が可視領域末尾に入る」位置に調整される
                Assert.True(
                    c.TopLine >= 9 - visibleRows + 1,
                    $"expected TopLine >= {9 - visibleRows + 1}, got {c.TopLine}"
                );
                Assert.True(c.TopLine <= 9, $"expected TopLine <= 9, got {c.TopLine}");
            }
        });

    [Fact]
    public void BringCaretIntoView_ScrollsUp_WhenCaretAboveVisible() =>
        Sta.Run(() =>
        {
            var text = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"line{i}"));
            var (f, c) = MakeControl(text, width: 400, height: 60);
            using (f)
            using (c)
            {
                // 先頭行にキャレットを置いてから可視領域を下方向へずらす
                // (順序が逆だと setter 自身の追従スクロールで assertion が先に満たされ、
                //  検証対象が BringCaretIntoView からすり替わる=上の ScrollsDown 版の注記参照)。
                c.SetCaretCharOffset(0);
                c.TopLine = 5; // ★ caret を置いた「後」に可視域をずらす
                c.BringCaretIntoView();

                Assert.Equal(0, c.TopLine); // 上端に張り付く
            }
        });

    [Fact]
    public void BringCaretIntoView_NoOp_WhenCaretAlreadyVisible() =>
        Sta.Run(() =>
        {
            var text = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"line{i}"));
            var (f, c) = MakeControl(text, width: 400, height: 200); // 全行入る想定
            using (f)
            using (c)
            {
                c.TopLine = 0;
                int initial = c.TopLine;
                c.SetCaretCharOffset(text.IndexOf("line2", System.StringComparison.Ordinal)); // 3行目
                c.BringCaretIntoView();
                Assert.Equal(initial, c.TopLine);
            }
        });

    [Fact]
    public void BringCaretIntoView_NoOp_BeforeSetSource() =>
        Sta.Run(() =>
        {
            // SetSource 前の呼び出しは throw せず何もしない
            using var f = new Form();
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            c.BringCaretIntoView(); // 例外が投げられなければ OK
            Assert.Equal(0, c.TopLine);
        });

    [Fact]
    public void EnsureVisibleCharRange_PreservesCaretAndAnchor() =>
        Sta.Run(() =>
        {
            var text = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"line{i}"));
            var (f, c) = MakeControl(text, width: 400, height: 60);
            using (f)
            using (c)
            {
                c.TopLine = 0;
                c.SetSelectionCharRange(2, 5);
                var (start0, end0) = c.GetSelectionCharRange();

                int lineStart = text.LastIndexOf('\n') + 1;
                c.EnsureVisibleCharRange(lineStart, 4); // 末尾行を可視化

                // 選択とキャレットは変わらない
                var (start1, end1) = c.GetSelectionCharRange();
                Assert.Equal(start0, start1);
                Assert.Equal(end0, end1);
                // でも TopLine は末尾方向に動いている
                Assert.True(c.TopLine > 0);
            }
        });

    [Fact]
    public void EnsureVisibleCharRange_NoOp_BeforeSetSource() =>
        Sta.Run(() =>
        {
            using var f = new Form();
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            // SetSource 前でも throw せず no-op であること
            Assert.Null(Record.Exception(() => c.EnsureVisibleCharRange(0, 10)));
        });

    [Fact]
    public void EnsureVisibleCharRange_RestoresSystemCaretPosition() =>
        Sta.Run(() =>
        {
            // Task 7 レビュー C-1 の回帰テスト: EnsureVisibleCharRange 後に OS 側システムキャレット
            // 座標が savedCaret 位置と一致することを検証する。
            //
            // バグ想定(修正前): TopLine/ScrollX setter が内部で PositionCaret を呼び、その時点の
            // _caret = end で SetCaretPos を発火 → field 復元後も blinking caret 位置は end のまま。
            // 修正(finally で PositionCaret 再呼び出し)により savedCaret 位置に戻る。
            //
            // NOTE: GetCaretPos は Focus を持つスレッド上でのみ有効。Sta.Run 上で Handle 生成
            // → Focus → SetSource の順に組み立てる。
            var text = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"line{i}"));
            using var f = new Form { Size = new System.Drawing.Size(400, 60) };
            var c = new EditorControl { Dock = DockStyle.Fill };
            f.Controls.Add(c);
            f.Show(); // Focus を得るには可視化が必要
            c.Focus();
            c.SetSource(TextBuffer.FromString(text));

            try
            {
                c.TopLine = 0;
                c.SetCaretCharOffset(2); // savedCaret = 2(行 0 col=2 想定)

                // 末尾行を可視化 → TopLine は動くはず・caret 位置は 2 のまま
                int lineStart = text.LastIndexOf('\n') + 1;
                c.EnsureVisibleCharRange(lineStart, 4);

                // field は復元される
                Assert.Equal(2, c.CaretCharOffset);
                Assert.Equal(2, c.SelectionAnchor);

                // 期待 caret 位置は EnsureVisibleCharRange 後のスクロール状態で再計算
                // (savedCaret=2 が可視領域から外れる=Point.Empty)なら OS キャレットは
                // 隠し座標 (-1000, -1000) にあるはず。ここでは savedCaret が上端にあり
                // 末尾行を可視化して行 0 も可視外になるので Point.Empty 経路を通る。
                bool ok = GetCaretPos(out var actual);
                Assert.True(ok, "GetCaretPos failed");

                var expectedAfter = c.PointFromCharOffset(2);
                if (expectedAfter == System.Drawing.Point.Empty)
                {
                    // savedCaret が可視外に押し出された → 隠し座標
                    Assert.Equal(-1000, actual.X);
                    Assert.Equal(-1000, actual.Y);
                }
                else
                {
                    Assert.Equal(expectedAfter.X, actual.X);
                    Assert.Equal(expectedAfter.Y, actual.Y);
                }
            }
            finally
            {
                c.Dispose();
                f.Close();
            }
        });

    [Fact]
    public void BringCaretIntoView_ScrollsDown_WhenCaretHiddenByHScrollBar() =>
        Sta.Run(() =>
        {
            // Task 7 レビュー I-1 の回帰テスト: hscroll 表示中(折り返し OFF・長い行がある)に
            // キャレットが最下論理行に来たとき、垂直判定が paintHeight ベースでないと
            // TopLine が「hscroll 領域まで可視カウント」で足りない値で止まる。
            //
            // Bug/Fix の TopLine を確実に食い違わせるために ClientSize.Height を LineHeightPx の
            // 倍数(3*LH)に強制する。この配置なら:
            //   visibleRows_bug = 3*LH / LH = 3
            //   visibleRows_fix = (3*LH - hscroll.H) / LH ≤ 2   (hscroll.H > 0 のため必ず 1 段小さい)
            // 論理行 9 (0-based) の末尾行キャレット:
            //   TopLine_bug = 9 - 3 + 1 = 7
            //   TopLine_fix = 9 - visibleRows_fix + 1 ≥ 8
            // 検証は「TopLine が fix 側の期待値以上」で行う。Bug 状態では TopLine=7 で
            // 8 未満なので必ず fail。Fix 状態では 8 以上になり pass。
            //
            // 長文行は line 0 に置く: UpdateHorizontalScrollbar は _topLine から probeHeight 分の
            // 視覚行のみを走査して最長幅を推定するため、長文行が TopLine=0 時の viewport 内に
            // ないと hscroll が表示されず、fix 側の paintHeight 減算が効かない(=バグを再現できない)。
            var text = new string('x', 200) + "\nl1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9";
            using var f = new Form { Size = new System.Drawing.Size(200, 200) };
            var c = new EditorControl { Dock = DockStyle.Fill };
            f.Controls.Add(c);
            f.Show(); // Show 経由でレイアウトを確定させる(unshown だと ClientSize の子伝播が不完全)
            c.SetSource(TextBuffer.FromString(text));

            try
            {
                // ClientSize を LineHeightPx の 3 倍に強制。Dock=Fill 経由で c.ClientSize.Height も同値に。
                // 100 px 幅は 200 char の長文行より狭い → OnResize 内で UpdateHorizontalScrollbar が
                // hscroll を表示状態にする。
                int lh = c.LineHeightPx;
                f.ClientSize = new System.Drawing.Size(100, 3 * lh);
                f.PerformLayout();

                c.WrapColumns = 0; // 折り返し OFF(念のため明示・既定 0)

                // 論理行 9(末尾)にキャレットを置いてから TopLine を先頭へ戻す
                // (順序が逆だと setter 自身の追従スクロールで assertion が先に満たされ、
                //  検証対象が BringCaretIntoView からすり替わる=ScrollsDown 版の注記参照)。
                int line9Start = text.LastIndexOf('\n') + 1;
                c.SetCaretCharOffset(line9Start);
                c.TopLine = 0; // ★ caret を置いた「後」に可視域を先頭へ戻す
                c.BringCaretIntoView();

                // Bug/Fix の TopLine 期待値を計算
                int hscrollH = SystemInformation.HorizontalScrollBarHeight;
                int paintH = c.ClientSize.Height - hscrollH;
                int visibleRowsFix = Math.Max(1, paintH / Math.Max(1, lh));
                int expectedTopLineFix = 9 - visibleRowsFix + 1;

                Assert.True(
                    c.TopLine >= expectedTopLineFix,
                    $"expected TopLine >= {expectedTopLineFix} (fix formula), got {c.TopLine} "
                        + $"(LH={lh}, ClientH={c.ClientSize.Height}, hscroll.H={hscrollH}, paintH={paintH}, visibleRows_fix={visibleRowsFix})"
                );
            }
            finally
            {
                c.Dispose();
                f.Close();
            }
        });

    [Fact]
    public void KeyDown_Down_ScrollsWhenReachingBottom() =>
        Sta.Run(() =>
        {
            // Task 6 の OnKeyDown で BringCaretIntoView が呼ばれるので、
            // 連続 Down で TopLine が追従することを検証
            var text = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"l{i}"));
            var (f, c) = MakeControl(text, width: 200, height: 60);
            using (f)
            using (c)
            {
                c.TopLine = 0;
                c.SetCaretCharOffset(0);

                // Down を 9 回押してキャレットを末尾行へ
                var mi = typeof(EditorControl).GetMethod(
                    "OnKeyDown",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                );
                for (int i = 0; i < 9; i++)
                    mi!.Invoke(c, new object[] { new KeyEventArgs(Keys.Down) });

                // TopLine は 0 より進んでいる(末尾行が可視領域に入る位置まで)
                Assert.True(c.TopLine > 0, $"expected TopLine to advance, still 0");
            }
        });

    [Fact]
    public void KeyDown_CtrlA_DoesNotScroll() =>
        Sta.Run(() =>
        {
            // Ctrl+A の非スクロール契約(Task 6 レビュー I-1)を**キーボード経路で**固定する。
            // API 層(SelectAll_DoesNotScroll / SetSelectionAnchored_DoesNotScroll)だけでは
            // InputRouter.HandleA に BringCaretIntoView() を足す変異を kill できない
            // (最終ブランチレビュー品質パス Minor 5 で実際に生存した)。
            // 設計書 §2 が非対称の唯一の根拠に挙げているのがこの契約なので、
            // shift+移動(KeyDown_ShiftDown_ScrollsWhenReachingBottom)と対称にキー経路まで張る。
            var (f, c, text) = MakeTallDocument();
            using (f)
            using (c)
            {
                // 非既定位置から開始: caret を先頭に置いた後、可視域を 3 行目へずらす。
                c.SetCaretCharOffset(0);
                c.TopLine = 3;

                var mi = typeof(EditorControl).GetMethod(
                    "OnKeyDown",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                );
                mi!.Invoke(c, new object[] { new KeyEventArgs(Keys.A | Keys.Control) });

                // 全選択されたこと(=Ctrl+A の経路を実際に通ったこと)を確定させる。
                // これが無いと、キーが握られず何も起きなくても TopLine の assert は通る。
                Assert.Equal((0, text.Length), c.GetSelectionCharRange());
                // キャレットは末尾(可視域外)へ動くが画面は動かない契約。
                Assert.Equal(3, c.TopLine);
            }
        });

    [Fact]
    public void KeyDown_ShiftDown_ScrollsWhenReachingBottom() =>
        Sta.Run(() =>
        {
            // A-3(Task 2)後、InputRouter.ApplyNavMove 末尾の BringCaretIntoView() が
            // load-bearing なのは **shift 分岐だけ** になる:
            //   - 無修飾分岐は SetCaretCharOffset が自ら追従スクロールするようになった
            //   - shift 分岐は MoveCaretWithSelection(非追従=Ctrl+A が画面を飛ばさない契約)を通る
            // したがって既存の無修飾 Down のテスト(KeyDown_Down_ScrollsWhenReachingBottom)だけでは
            // ApplyNavMove の BringCaretIntoView() 行を消しても全緑になる(網の穴)。
            // この網はその行を kill するために張っている。
            var text = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"l{i}"));
            var (f, c) = MakeControl(text, width: 200, height: 60);
            using (f)
            using (c)
            {
                c.TopLine = 0;
                c.SetCaretCharOffset(0);

                var mi = typeof(EditorControl).GetMethod(
                    "OnKeyDown",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                );
                for (int i = 0; i < 9; i++)
                    mi!.Invoke(c, new object[] { new KeyEventArgs(Keys.Down | Keys.Shift) });

                // shift 分岐(MoveCaretWithSelection)を通ったことを選択の伸長で確定させる。
                // これが無いと、万一 shift が効かず無修飾扱いになっても TopLine の assert は通ってしまう。
                var (selStart, selEnd) = c.GetSelectionCharRange();
                Assert.Equal(0, selStart);
                Assert.True(selEnd > 0, $"expected selection to extend, got end={selEnd}");

                Assert.True(c.TopLine > 0, "expected TopLine to advance, still 0");
            }
        });

    // ===== A-3: 絶対位置指定 setter の追従スクロール(2026-08-22 設計書 §2)=====
    //
    // 契約: キャレット/選択の「絶対位置を外から指定する」API はキャレットを可視域に入れる。
    //       アンカー相対で動かす API は呼び出し側がスクロールを判断する。
    // 検索ジャンプ / Ctrl+G / grep ジャンプ / UIA Select() はすべてこの 2 メソッドを通る。

    /// <summary>
    /// 30 行の文書と 1 視覚行ぶんの可視域を作る(末尾行が必ず初期ビューポート外になる)。
    /// </summary>
    /// <remarks>
    /// <see cref="MakeControl"/> に渡す height は <c>Form.Size</c> なので、タイトルバーと枠を
    /// 引いた <c>ClientSize.Height</c> は 60 ではなく約 21 px = <c>LineHeightPx</c>(20)とほぼ同じ。
    /// 実測: ClientSize 384×21 / LineHeightPx 20 / visibleRows 1(2026-08-22 Task 2 レビュー)。
    /// 可視行数が 1 でも各テストの閾値式は成立する(むしろ「TopLine が対象行ちょうどに張り付く」
    /// まで要求する強い網になる)。height をいじると閾値の意味が変わるので、変えるなら
    /// 各テストの assertion を測り直すこと。
    /// </remarks>
    private static (Form f, EditorControl c, string text) MakeTallDocument()
    {
        var text = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line{i}"));
        var (f, c) = MakeControl(text, width: 400, height: 60);
        return (f, c, text);
    }

    [Fact]
    public void SetCaretCharOffset_ScrollsCaretIntoView() =>
        Sta.Run(() =>
        {
            var (f, c, text) = MakeTallDocument();
            using (f)
            using (c)
            {
                c.TopLine = 0;
                int visibleRows = Math.Max(1, c.ClientSize.Height / c.LineHeightPx);
                // fixture 前提: 末尾行(index 29)が初期ビューポートの外にあること。
                // これが崩れると以降の assertion が空振りする。
                Assert.True(visibleRows < 29, $"fixture 前提崩れ: visibleRows={visibleRows}");

                int lineStart = text.LastIndexOf('\n') + 1; // 論理行 29 の先頭
                c.SetCaretCharOffset(lineStart); // ★ BringCaretIntoView は呼ばない

                Assert.True(
                    c.TopLine >= 29 - visibleRows + 1,
                    $"expected TopLine >= {29 - visibleRows + 1}, got {c.TopLine}"
                );
            }
        });

    [Fact]
    public void SetSelectionCharRange_ScrollsRangeEndIntoView() =>
        Sta.Run(() =>
        {
            var (f, c, text) = MakeTallDocument();
            using (f)
            using (c)
            {
                c.TopLine = 0;
                int visibleRows = Math.Max(1, c.ClientSize.Height / c.LineHeightPx);
                Assert.True(visibleRows < 29, $"fixture 前提崩れ: visibleRows={visibleRows}");

                // 範囲は行 0 から行 29 までまたがせる。start と end を別の論理行に置かないと
                // 「範囲末尾を可視化する」契約(Caret = Max(start, end) にマップ)を検証できない
                // ——両端が同じ行にある fixture では、実装を「範囲先頭を可視化」へ差し替えても
                // 緑のまま通る(Task 2 レビューの変異 (c) が実際に生存した)。
                int lineStart = text.LastIndexOf('\n') + 1;
                c.SetSelectionCharRange(0, lineStart + 4); // ★ 検索ヒット選択と同じ経路

                // 先頭可視化なら TopLine=0 のまま・末尾可視化なら行 29 へ張り付く=両者を判別できる。
                Assert.True(
                    c.TopLine >= 29 - visibleRows + 1,
                    $"expected TopLine >= {29 - visibleRows + 1}, got {c.TopLine}"
                );
            }
        });

    // ----- 非対象 API が「スクロールしない」ことの固定 -----
    // no-change テストは非既定位置から始める(CLAUDE.md §4)= TopLine を 0 以外に置く。

    [Fact]
    public void SetSelectionAnchored_DoesNotScroll() =>
        Sta.Run(() =>
        {
            var (f, c, text) = MakeTallDocument();
            using (f)
            using (c)
            {
                // 非既定位置から開始: caret を先頭に置いた後、可視域を 3 行目へずらす。
                c.SetCaretCharOffset(0);
                c.TopLine = 3;

                // Ctrl+A 相当。キャレットは末尾(可視域外)へ動くが画面は動かない契約。
                c.SetSelectionAnchored(0, text.Length);

                Assert.Equal(3, c.TopLine);
            }
        });

    [Fact]
    public void SelectAll_DoesNotScroll() =>
        Sta.Run(() =>
        {
            // Ctrl+A のユーザー可視契約(Task 6 レビュー I-1 の判断)を直接固定する。
            var (f, c, _) = MakeTallDocument();
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(0);
                c.TopLine = 3;

                c.SelectAll();

                Assert.Equal(3, c.TopLine);
            }
        });

    [Fact]
    public void MoveCaretWithSelection_DoesNotScroll() =>
        Sta.Run(() =>
        {
            // shift+移動の共通経路。追従は呼び出し側(InputRouter)の責務=setter は動かさない。
            var (f, c, text) = MakeTallDocument();
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(0);
                c.TopLine = 3;

                c.MoveCaretWithSelection(text.Length);

                Assert.Equal(3, c.TopLine);
            }
        });

    // M-32(2026-08-22): UIA Select() がキャレットを可視域へスクロールしない。
    // Adapter の IUiaTextHost.SetSelection は BeginInvoke で UI スレッドへ渡した後
    // EditorControl.SetSelectionCharRange を呼ぶため、A-3 の修正で同時に解消する。
    // 本テストは UI スレッド上で呼ぶので InvokeRequired=false=直接経路を通る。
    [Fact]
    public void UiaSetSelection_ScrollsSelectionIntoView() =>
        Sta.Run(() =>
        {
            var (f, c, text) = MakeTallDocument();
            using (f)
            using (c)
            {
                c.TopLine = 0;
                int visibleRows = Math.Max(1, c.ClientSize.Height / c.LineHeightPx);
                Assert.True(visibleRows < 29, $"fixture 前提崩れ: visibleRows={visibleRows}");

                // 範囲は行 0 から行 29 までまたがせる(Task 2 レビュー M-1 と同じ理由=
                // start と end を別の論理行に置かないと「範囲末尾を可視化する」契約を検証できない)。
                int lineStart = text.LastIndexOf('\n') + 1;
                IUiaTextHost host = c;
                host.SetSelection(0, lineStart + 4);

                Assert.True(
                    c.TopLine >= 29 - visibleRows + 1,
                    $"expected TopLine >= {29 - visibleRows + 1}, got {c.TopLine}"
                );
            }
        });
}
