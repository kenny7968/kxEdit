using System.Linq;
using System.Reflection;

namespace kxEdit.Editor.Tests;

/// <summary>
/// P3 Task 12: マウス入力配線(Down/Move/Up/DoubleClick/Wheel 精度改善)の契約テスト。
/// - MouseDown: 座標→char offset にキャレット移動(Shift 併用時はアンカー保持=選択拡張)
/// - MouseMove(Left 押下中): ドラッグで選択拡張
/// - MouseUp: ドラッグ終了(状態フラグ落とし)
/// - MouseDoubleClick: 単語選択(WordBoundary 使用)
/// - MouseWheel: SystemInformation.MouseWheelScrollLines + 120 単位蓄積
/// - 座標→char offset のクランプ: 行末超過 / 最終行以降
///
/// テスト値は MS ゴシック 12pt の実 LineHeight / 文字幅に依存するため相対比較で書く
/// (行番号/クランプは決定的なので絶対値でも可)。
/// </summary>
public class MouseInputTests
{
    private static (Form f, EditorControl c) MakeControl(
        string text,
        int width = 400,
        int height = 200
    )
    {
        var f = new Form { Size = new System.Drawing.Size(width, height) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle; // WinForms ハンドル強制生成でネイティブキャレット/描画経路を有効化
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    private static void SendMouseDown(
        EditorControl c,
        int x,
        int y,
        MouseButtons btn = MouseButtons.Left
    )
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnMouseDown",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        mi!.Invoke(c, new object[] { new MouseEventArgs(btn, 1, x, y, 0) });
    }

    private static void SendMouseMove(
        EditorControl c,
        int x,
        int y,
        MouseButtons btn = MouseButtons.Left
    )
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnMouseMove",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        mi!.Invoke(c, new object[] { new MouseEventArgs(btn, 0, x, y, 0) });
    }

    private static void SendMouseUp(
        EditorControl c,
        int x,
        int y,
        MouseButtons btn = MouseButtons.Left
    )
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnMouseUp",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        mi!.Invoke(c, new object[] { new MouseEventArgs(btn, 1, x, y, 0) });
    }

    private static void SendMouseDoubleClick(EditorControl c, int x, int y)
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnMouseDoubleClick",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        mi!.Invoke(c, new object[] { new MouseEventArgs(MouseButtons.Left, 2, x, y, 0) });
    }

    private static void SendMouseWheel(EditorControl c, int delta)
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnMouseWheel",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        mi!.Invoke(c, new object[] { new MouseEventArgs(MouseButtons.None, 0, 0, 0, delta) });
    }

    // ===== MouseDown: 行への配置(font 依存回避=行番号のみ絶対値検証) =====

    [Fact]
    public void MouseDown_MovesCaret_ToClickedRow() =>
        Sta.Run(() =>
        {
            // Y = LineHeight + 2 → 論理行 1(0-based)
            var text = "hello\nworld\nlong line here";
            var (f, c) = MakeControl(text);
            using (f)
            using (c)
            {
                int lineHeight = c.LineHeightPx;
                SendMouseDown(c, x: 100, y: lineHeight + 2);
                var snap = TextBuffer.FromString(text).Current;
                int caretLine = snap.GetLineIndexOfChar(c.CaretCharOffset);
                Assert.Equal(1, caretLine);
            }
        });

    [Fact]
    public void MouseDown_MovesCaret_ToDocumentStart_WhenClickAtOrigin() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("hello\nworld");
            using (f)
            using (c)
            {
                // 初期キャレットは 0 だが、明示的に別位置へ移してから MouseDown(0,0) で戻ることを検証
                c.SetCaretCharOffset(5);
                SendMouseDown(c, 0, 0);
                Assert.Equal(0, c.CaretCharOffset);
            }
        });

    [Fact]
    public void MouseDown_LeftButton_ClearsSelection() =>
        Sta.Run(() =>
        {
            // 事前に選択があった状態で単発 MouseDown(shift 無し)は選択を解除する
            var (f, c) = MakeControl("hello world");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(2, 7);
                SendMouseDown(c, 0, 0);
                var (s, e) = c.GetSelectionCharRange();
                Assert.Equal(0, s);
                Assert.Equal(0, e); // 選択解除
            }
        });

    [Fact]
    public void MouseDown_RightButton_DoesNothing() =>
        Sta.Run(() =>
        {
            // 右クリックはキャレット移動 / 選択操作をしない(将来のコンテキストメニュー予約)
            var (f, c) = MakeControl("hello world");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                SendMouseDown(c, x: 100, y: 2, btn: MouseButtons.Right);
                Assert.Equal(3, c.CaretCharOffset);
            }
        });

    // ===== ドラッグ選択 =====

    [Fact]
    public void MouseDrag_SelectsRange() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("hello world");
            using (f)
            using (c)
            {
                SendMouseDown(c, 0, 0);
                SendMouseMove(c, x: 200, y: 0);
                SendMouseUp(c, x: 200, y: 0);
                var (s, en) = c.GetSelectionCharRange();
                Assert.True(en > s, $"expected selection range, got ({s},{en})");
                Assert.Equal(0, s); // アンカーは MouseDown 位置(=0)に固定
            }
        });

    [Fact]
    public void MouseMove_WithoutMouseDown_DoesNothing() =>
        Sta.Run(() =>
        {
            // MouseDown を経ずに直接 MouseMove を投げても選択やキャレット移動は起きない
            // (MouseDragging=false の間は no-op)
            var (f, c) = MakeControl("hello world");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                SendMouseMove(c, x: 200, y: 0);
                Assert.Equal(3, c.CaretCharOffset);
                var (s, e) = c.GetSelectionCharRange();
                Assert.Equal(s, e); // 選択なし
            }
        });

    [Fact]
    public void MouseUp_StopsDragging() =>
        Sta.Run(() =>
        {
            // Down → Up 後は MouseMove がキャレットを動かさないことを検証
            var (f, c) = MakeControl("hello world");
            using (f)
            using (c)
            {
                SendMouseDown(c, 0, 0);
                int afterDown = c.CaretCharOffset;
                SendMouseUp(c, x: 100, y: 0);
                SendMouseMove(c, x: 300, y: 0);
                // Up 後の Move はキャレットを動かさない
                Assert.Equal(afterDown, c.CaretCharOffset);
            }
        });

    // ===== ダブルクリック単語選択 =====

    [Fact]
    public void DoubleClick_SelectsWord() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("hello world");
            using (f)
            using (c)
            {
                // Y=2=行0, X=20 は "hello" 内の何処か(font 依存だがどこでも "hello" を選ぶはず)
                SendMouseDoubleClick(c, x: 20, y: 2);
                var (s, en) = c.GetSelectionCharRange();
                Assert.Equal(0, s);
                Assert.Equal(5, en);
            }
        });

    [Fact]
    public void DoubleClick_OnWhitespace_SelectsPrevWordPlusWhitespaceRun() =>
        Sta.Run(() =>
        {
            // 現状仕様(Task 12 レビュー I-1 で明文化・WordBoundary.WordEnd の xmldoc 参照):
            // 空白位置ダブルクリックは「前単語頭 + target 位置までの空白 run の一部」を選択する
            // 非対称仕様(Notepad 近似・VS Code の空白 run 単独選択への変更要否は
            // Task 14 smoke / P7 実機検証で判断)。
            //
            // font 依存性を吸くため PointFromCharOffset で pixel 位置を割り出し、
            // 空白 run の中間位置を DoubleClick する。
            // 注意: PxToOffset は「px が code-point 内に入る → その直後の offset」を返す
            // (「入れば含める」の直観)。1-space の "hello world" で space 中央を叩くと
            // target=6('w' の頭)=world 選択に転ぶため、テストは 4-space run
            // "hello    world" で行う=positions 5..8 が空白・6/7 中間を叩けば
            // 確実に target が空白 run 内(=6 or 7)になり、意図する仕様を検証できる。
            var (f, c) = MakeControl("hello    world"); // 4 spaces
            using (f)
            using (c)
            {
                var pt6 = c.PointFromCharOffset(6);
                var pt7 = c.PointFromCharOffset(7);
                Assert.NotEqual(System.Drawing.Point.Empty, pt6);
                Assert.NotEqual(System.Drawing.Point.Empty, pt7);
                int midX = (pt6.X + pt7.X) / 2;

                SendMouseDoubleClick(c, x: midX, y: pt6.Y + 2);
                var (s, en) = c.GetSelectionCharRange();
                // 期待: 選択開始は前単語頭(0)、終了は空白 run の内側(5 超え・world=9 未満)。
                // 具体: midX の PxToOffset → 7 → WordBoundary.WordStart(7)=0 / WordEnd(7)=7 →
                //       selection [0, 7) = "hello  "(2 spaces 含む)。
                // 2026-08-04: 規則の実装は InputRouter の private 2 本から Core の
                // WordBoundary.WordStart / WordEnd へ bit-perfect 移設した(本ブランチ Task 1〜2)。
                // このテストが無改修のまま緑であることが、その移設の挙動不変の一次証拠である。
                Assert.Equal(0, s);
                Assert.InRange(en, 6, 8); // world 頭(=9)未満・"hello"末(=5)より右
            }
        });

    // ===== Wheel 精度改善 =====

    [Fact]
    public void MouseWheel_ScrollsUp_WithSystemInformationLines() =>
        Sta.Run(() =>
        {
            var text = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line{i}"));
            var (f, c) = MakeControl(text, height: 60);
            using (f)
            using (c)
            {
                c.TopLine = 10;
                int before = c.TopLine;
                // 1 tick = 120 delta。SystemInformation.MouseWheelScrollLines 行送り(既定 3)
                SendMouseWheel(c, delta: 120); // 上方向 → TopLine 減
                Assert.True(c.TopLine < before, $"expected TopLine decrease, got {c.TopLine}");
            }
        });

    [Fact]
    public void MouseWheel_ScrollsDown_WithSystemInformationLines() =>
        Sta.Run(() =>
        {
            var text = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line{i}"));
            var (f, c) = MakeControl(text, height: 60);
            using (f)
            using (c)
            {
                c.TopLine = 5;
                int before = c.TopLine;
                SendMouseWheel(c, delta: -120); // 下方向 → TopLine 増
                Assert.True(c.TopLine > before, $"expected TopLine increase, got {c.TopLine}");
            }
        });

    [Fact]
    public void MouseWheel_AccumulatesSmallDeltas() =>
        Sta.Run(() =>
        {
            // 40 x 3 = 120 で 1 tick 相当(蓄積で発火)
            var text = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line{i}"));
            var (f, c) = MakeControl(text, height: 60);
            using (f)
            using (c)
            {
                c.TopLine = 10;
                int before = c.TopLine;
                SendMouseWheel(c, 40);
                SendMouseWheel(c, 40);
                Assert.Equal(before, c.TopLine); // まだ 120 に満たない=変化なし
                SendMouseWheel(c, 40);
                Assert.True(
                    c.TopLine < before,
                    $"expected accumulation to trigger, got {c.TopLine}"
                );
            }
        });

    // ===== 座標→char offset のクランプ検証 =====

    [Fact]
    public void MouseDown_BeyondLineEnd_ClampsToLineEnd() =>
        Sta.Run(() =>
        {
            // 行末を大きく超えた X 位置をクリック → 行末位置にクランプ
            var (f, c) = MakeControl("hi", width: 800);
            using (f)
            using (c)
            {
                SendMouseDown(c, x: 700, y: 2);
                Assert.Equal(2, c.CaretCharOffset); // "hi" の末尾
            }
        });

    [Fact]
    public void MouseDown_BelowLastLine_ClampsToDocumentEnd() =>
        Sta.Run(() =>
        {
            // 最終視覚行より下をクリック → 文書末尾にクランプ
            var (f, c) = MakeControl("abc\ndef");
            using (f)
            using (c)
            {
                SendMouseDown(c, x: 0, y: 9999);
                Assert.Equal(7, c.CaretCharOffset); // "abc\ndef" 末尾
            }
        });
}
