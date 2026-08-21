using System.Reflection;
using kxEdit.Core.Editing;

namespace kxEdit.Editor.Tests;

/// <summary>
/// P3 Task 6: キーバインド配線(移動系)の契約テスト。
/// Task 3〜5 で作った純ロジック(NavigationCommands / VerticalNavigation / WordBoundary)を
/// EditorControl の OnKeyDown で叩き、キャレット/選択が期待どおり更新されることを検証する。
/// 編集系(BackSpace/Delete/Enter/文字挿入/Cut/Copy/Paste/Undo/Redo)は Task 8〜11 の担当。
/// </summary>
/// <remarks>
/// OnKeyDown は protected のため、リフレクションで直接叩く(Task 12 の
/// InternalsVisibleTo 判断まで EditorControl 表面を汚さない)。
/// </remarks>
public class KeyboardNavigationTests
{
    private static (Form f, EditorControl c) MakeControl(string text, bool focus = false)
    {
        var f = new Form();
        var c = new EditorControl();
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        if (focus)
            c.Focus();
        return (f, c);
    }

    // OnKeyDown を直接呼ぶヘルパ(protected 対象のためリフレクション経由)。
    // Task 12 の InternalsVisibleTo 判断まで EditorControl 表面を汚さない。
    private static void SendKey(EditorControl c, Keys keyData)
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        mi!.Invoke(c, new object[] { new KeyEventArgs(keyData) });
    }

    [Fact]
    public void RightArrow_MovesCaretByOneChar() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(0);
                SendKey(c, Keys.Right);
                Assert.Equal(1, c.CaretCharOffset);
            }
        });

    [Fact]
    public void LeftArrow_MovesCaretByOneChar() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(2);
                SendKey(c, Keys.Left);
                Assert.Equal(1, c.CaretCharOffset);
            }
        });

    [Fact]
    public void ShiftRightArrow_ExtendsSelection() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKey(c, Keys.Right | Keys.Shift);
                Assert.Equal((1, 2), c.GetSelectionCharRange());
                Assert.Equal(2, c.CaretCharOffset);
                Assert.Equal(1, c.SelectionAnchor);
            }
        });

    [Fact]
    public void ShiftLeftArrow_ExtendsSelection_TowardStart() =>
        Sta.Run(() =>
        {
            // shift+左方向の選択保持(アンカー概念の恩恵)
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(4);
                SendKey(c, Keys.Left | Keys.Shift);
                Assert.Equal(3, c.CaretCharOffset);
                Assert.Equal(4, c.SelectionAnchor);
                Assert.Equal((3, 4), c.GetSelectionCharRange());
            }
        });

    [Fact]
    public void CtrlRight_MovesToNextWord() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("hello world");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(0);
                SendKey(c, Keys.Right | Keys.Control);
                Assert.Equal(6, c.CaretCharOffset); // "hello " の後 → 'w'
            }
        });

    [Fact]
    public void CtrlLeft_MovesToPrevWord() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("hello world");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(11);
                SendKey(c, Keys.Left | Keys.Control);
                Assert.Equal(6, c.CaretCharOffset); // "world" の頭
            }
        });

    /// <summary>
    /// 2026-08-04 F-5(仕様変更): 空白ゼロの単一クラス長大行では、Ctrl+→ が行末まで走らず
    /// <c>WordBoundary.DefaultMaxScan</c> 歩でキャレットが単語 run の途中に止まる。
    /// </summary>
    /// <remarks>
    /// 修正前は 1 回の Ctrl+← / Ctrl+→ が UI スレッドを約 1.4 秒占有していた
    /// (docs/plans/2026-08-03-uia-word-unit-design.md §2.4)。文字クラス規則(F-3 修正)は
    /// 単一クラスの長大連続には効かないため上限が別途要る。上限に当たると単語の途中で
    /// 止まるのは<b>意図的</b>。cap は SR の読み上げスパン(<c>UiaTextHostAdapter</c>)・
    /// ダブルクリック単語選択(<c>InputRouter.HandleMouseDoubleClick</c>)と共有する。
    /// </remarks>
    [Fact]
    public void CtrlRight_OnHugeSingleClassLine_StopsAtScanLimit() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(new string('a', 500_000));
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(0);
                SendKey(c, Keys.Right | Keys.Control);
                Assert.Equal(WordBoundary.DefaultMaxScan, c.CaretCharOffset);
            }
        });

    /// <summary>
    /// <see cref="CtrlRight_OnHugeSingleClassLine_StopsAtScanLimit"/> の対称形(Ctrl+←)。
    /// 窓は <c>[caret - maxScan, caret]</c>(<c>WordBoundary</c> の xmldoc の表)。
    /// </summary>
    [Fact]
    public void CtrlLeft_OnHugeSingleClassLine_StopsAtScanLimit() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl(new string('a', 500_000));
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(500_000);
                SendKey(c, Keys.Left | Keys.Control);
                Assert.Equal(500_000 - WordBoundary.DefaultMaxScan, c.CaretCharOffset);
            }
        });

    [Fact]
    public void Home_MovesToSmartLineStart() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("  hello");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(4);
                SendKey(c, Keys.Home);
                Assert.Equal(2, c.CaretCharOffset); // SmartHome=先頭空白の後
                SendKey(c, Keys.Home);
                Assert.Equal(0, c.CaretCharOffset); // トグルで行頭へ
            }
        });

    [Fact]
    public void End_MovesToLineEnd() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc\r\ndef");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(0);
                SendKey(c, Keys.End);
                Assert.Equal(3, c.CaretCharOffset); // 行0 の末尾(\r の前)
            }
        });

    [Fact]
    public void CtrlHome_MovesToDocumentStart() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc\ndef");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(5);
                SendKey(c, Keys.Home | Keys.Control);
                Assert.Equal(0, c.CaretCharOffset);
            }
        });

    [Fact]
    public void CtrlEnd_MovesToDocumentEnd() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc\ndef");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(0);
                SendKey(c, Keys.End | Keys.Control);
                Assert.Equal(7, c.CaretCharOffset);
            }
        });

    [Fact]
    public void Down_MovesToNextLineSameColumn() =>
        Sta.Run(() =>
        {
            // 同一文字で両行を構成することでシステムフォントのグリフ幅差に依存しない
            // (MS ゴシックへのフォント代替時 ASCII a-f と x-w で幅が微妙に異なるケースがある)。
            var (f, c) = MakeControl("abcdef\nabcdef");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                SendKey(c, Keys.Down);
                Assert.Equal(10, c.CaretCharOffset); // 行1 の col=3
            }
        });

    [Fact]
    public void Up_MovesToPrevLineSameColumn() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef\nabcdef");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(10);
                SendKey(c, Keys.Up);
                Assert.Equal(3, c.CaretCharOffset);
            }
        });

    [Fact]
    public void CtrlA_SelectsAll() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("hello");
            using (f)
            using (c)
            {
                SendKey(c, Keys.A | Keys.Control);
                Assert.Equal((0, 5), c.GetSelectionCharRange());
            }
        });

    [Fact]
    public void CtrlA_ResetsDesiredX() =>
        Sta.Run(() =>
        {
            // レビュー S-1: Ctrl+A は文書頭〜末尾のジャンプ相当なので、水平移動と同様に
            // _desiredXpx をリセットする必要がある。リセットされていないと、Ctrl+A 直後に
            // SetCaretCharOffset(手動配置) → Up/Down したときに「Ctrl+A 前の古い列」を目指す
            // (ここでは caret=15=col=1 のはずが、古い px_of_col5 が流れ 12 に着地するバグ)。
            //
            // レイアウト: "abcdef\nabcdef\nabcdef" は CharLength=20。
            //   行0="abcdef"[0-5], \n@6・行1="abcdef"[7-12], \n@13・行2="abcdef"[14-19]
            // 手計算:
            //   1. SetCaretCharOffset(5) → caret=5(行0 col=5)
            //   2. Down → 行1 col=5 = 12。_desiredXpx = px_of_col5
            //   3. Ctrl+A → 選択(0,20)。修正後: _desiredXpx = -1 にリセット
            //   4. SetCaretCharOffset(15) → caret=15(行2 col=1)。_desiredXpx は不変
            //   5. Up → _desiredXpx=-1 で新規計算=px_of_col1・行1 col=1 = 7+1 = 8
            // バグ想定(リセット無し): 手順 5 で古い px_of_col5 が流れ、target = 7+5 = 12
            var (f, c) = MakeControl("abcdef\nabcdef\nabcdef");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(5); // 行0 col=5
                SendKey(c, Keys.Down); // 行1 col=5 位置=12。_desiredXpx = px_of_col5
                Assert.Equal(12, c.CaretCharOffset);
                SendKey(c, Keys.A | Keys.Control); // 全選択。_desiredXpx リセット期待
                c.SetCaretCharOffset(15); // 行2 col=1 位置(選択解除+手動配置)
                SendKey(c, Keys.Up); // 行1 col=1 位置=8(新規計算=px_of_col1)
                Assert.Equal(8, c.CaretCharOffset); // バグなら 12 になる
            }
        });

    [Fact]
    public void LeftArrow_ResetsDesiredX() =>
        Sta.Run(() =>
        {
            // 上下移動 → Left でリセットされて desiredX が再計算されることを間接検証。
            // 実装上 _desiredXpx は private なので、Left → Down で「新規計算」経路に入り
            // かつ現在 caret 位置の X が使われることを間接検証する形になる。
            // 全行同一文字で構成しシステムフォントのグリフ幅差に依存させない。
            var (f, c) = MakeControl("abcdef\nab\nabcdefghij");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(5);
                SendKey(c, Keys.Down); // 行1 は長さ 2 → クランプで col=2 = 9
                Assert.Equal(9, c.CaretCharOffset);
                SendKey(c, Keys.Left); // 8 に。resetDesired=true で desiredX=-1
                Assert.Equal(8, c.CaretCharOffset);
                SendKey(c, Keys.Down); // desiredX 新規計算=col=1 の px → 行2 col=1 → 11
                Assert.Equal(11, c.CaretCharOffset);
            }
        });
}
