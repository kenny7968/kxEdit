namespace kxEdit.Editor.Tests;

/// <summary>
/// P3 Task 2: アンカー概念導入(shift+左方向の選択保持)の契約テスト。
/// 現状の <c>_caret</c> + <c>_selStart</c>/<c>_selEnd</c> の 3 変数から
/// <c>_anchor</c> + <c>_caret</c> の 2 変数化へ移行し、以下 3 つの新規 API と
/// 既存 API の挙動不変を検証する。
/// - <see cref="EditorControl.SelectionAnchor"/>
/// - <see cref="EditorControl.MoveCaretWithSelection(int)"/>
/// - <see cref="EditorControl.SetSelectionAnchored(int, int)"/>
/// </summary>
public class AnchorSelectionTests
{
    private static (Form f, EditorControl c) MakeControl(string text)
    {
        var f = new HostForm();
        var c = new EditorControl();
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    [Fact]
    public void SetSelectionAnchored_KeepsCaretAtStart_WhenCaretBeforeAnchor() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetSelectionAnchored(anchor: 5, caret: 2);
                Assert.Equal(2, c.CaretCharOffset);
                Assert.Equal(5, c.SelectionAnchor);
                Assert.Equal((2, 5), c.GetSelectionCharRange());
            }
        });

    [Fact]
    public void MoveCaretWithSelection_KeepsAnchor() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetSelectionAnchored(anchor: 3, caret: 3); // アンカー = キャレット = 3(選択なし)
                c.MoveCaretWithSelection(1); // 左へ拡張
                Assert.Equal((1, 3), c.GetSelectionCharRange());
                Assert.Equal(1, c.CaretCharOffset);
                Assert.Equal(3, c.SelectionAnchor);
            }
        });

    [Fact]
    public void SetCaretCharOffset_ClearsSelection_AndAnchorFollows() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetSelectionAnchored(anchor: 5, caret: 2);
                c.SetCaretCharOffset(4);
                Assert.Equal(4, c.CaretCharOffset);
                Assert.Equal(4, c.SelectionAnchor);
                Assert.Equal((4, 4), c.GetSelectionCharRange()); // 選択なし
            }
        });

    [Fact]
    public void SetSelectionCharRange_MapsToAnchorAsMin_CaretAsMax() =>
        Sta.Run(() =>
        {
            // 既存 API 契約: (start, end) は Min→anchor, Max→caret にマップ(挙動不変)
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(5, 2); // 逆順で渡す
                Assert.Equal(2, c.SelectionAnchor); // Min = 2 が anchor
                Assert.Equal(5, c.CaretCharOffset); // Max = 5 が caret
                Assert.Equal((2, 5), c.GetSelectionCharRange());
            }
        });

    // ---- レビュー S-1: 新 API のサロゲート/クランプ契約テスト ----

    [Fact]
    public void SetSelectionAnchored_SnapsSurrogateLowToHigh() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc😀def"); // 😀 は high@3, low@4
            using (f)
            using (c)
            {
                c.SetSelectionAnchored(anchor: 4, caret: 0);
                Assert.Equal(3, c.SelectionAnchor); // low→high スナップ
                Assert.Equal(0, c.CaretCharOffset);
            }
        });

    [Fact]
    public void MoveCaretWithSelection_ClampsToCharLength() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetSelectionAnchored(anchor: 0, caret: 0);
                c.MoveCaretWithSelection(9999);
                Assert.Equal(3, c.CaretCharOffset); // CharLength にクランプ
                Assert.Equal(0, c.SelectionAnchor); // アンカー保持
            }
        });

    [Fact]
    public void SetSelectionAnchored_RightDirection_Works() =>
        Sta.Run(() =>
        {
            // 右方向(anchor < caret)の設定も正しく反映されることを明示検証
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetSelectionAnchored(anchor: 1, caret: 5);
                Assert.Equal(1, c.SelectionAnchor);
                Assert.Equal(5, c.CaretCharOffset);
                Assert.Equal((1, 5), c.GetSelectionCharRange());
            }
        });

    // ---- レビュー S-2: 選択折りたたみ契約 ----

    [Fact]
    public void MoveCaretWithSelection_CollapsesSelection_WhenCaretEqualsAnchor() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetSelectionAnchored(anchor: 3, caret: 5);
                c.MoveCaretWithSelection(3); // = _anchor
                Assert.Equal((3, 3), c.GetSelectionCharRange()); // 選択消滅
                Assert.Equal(3, c.CaretCharOffset);
                Assert.Equal(3, c.SelectionAnchor);
            }
        });
}
