using System.Reflection;

namespace kxEdit.Editor.Tests;

/// <summary>
/// Phase 3 Task 3c: <see cref="InputRouter"/> 経由での keymap dispatch を機械固定する pure テスト。
/// - keymap の網羅性は既存の <see cref="KeyboardNavigationTests"/> が担う(30+ tests)。
/// - 本テストは Router が「登録キーは Handled=true にする / 未登録キーは Handled=false のまま」の
///   契約を保つことに絞る。
/// </summary>
public class InputRouterTests
{
    private static (Form f, EditorControl c) MakeControl(string text = "")
    {
        var f = new Form();
        var c = new EditorControl();
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    // KeyEventArgs を OnKeyDown(protected) 経由で流す(既存 KeyboardNavigationTests と同流)。
    private static KeyEventArgs SendKey(EditorControl c, Keys keyData)
    {
        var e = new KeyEventArgs(keyData);
        var mi = typeof(EditorControl).GetMethod(
            "OnKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        mi!.Invoke(c, new object[] { e });
        return e;
    }

    [Fact]
    public void Route_UnmappedKey_LeavesHandledFalse() =>
        Sta.Run(() =>
        {
            // Keys.F13 は keymap に未登録 → Router は e.Handled を触らない
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                var e = SendKey(c, Keys.F13);
                Assert.False(e.Handled);
            }
        });

    [Fact]
    public void Route_MappedKey_SetsHandledTrue() =>
        Sta.Run(() =>
        {
            // Keys.Right は keymap に登録済み → ハンドラが true を返し Router が e.Handled=true にする
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(0);
                var e = SendKey(c, Keys.Right);
                Assert.True(e.Handled);
            }
        });

    [Fact]
    public void Route_CtrlA_SelectsAll() =>
        Sta.Run(() =>
        {
            // HandleA: Ctrl+A は SelectSelectionAnchored(0, CharLength) を呼ぶ
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                var e = SendKey(c, Keys.A | Keys.Control);
                Assert.True(e.Handled);
                Assert.Equal((0, 6), c.GetSelectionCharRange());
            }
        });

    [Fact]
    public void Route_BareA_NotHandled() =>
        Sta.Run(() =>
        {
            // HandleA: ctrl 無しの A は false 返す → Router は e.Handled を触らない
            // (OnKeyPress が 'a' を挿入する経路に委譲される)
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                var e = SendKey(c, Keys.A);
                Assert.False(e.Handled);
            }
        });

    [Fact]
    public void Route_DeleteWhileReadOnly_NotHandled() =>
        Sta.Run(() =>
        {
            // HandleDelete: ReadOnly=true では false 返す(元 switch の case Keys.Delete when !ReadOnly が
            // no-match で switch を抜ける挙動と等価)。本文も変わらない。
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.ReadOnly = true;
                c.SetCaretCharOffset(0);
                var e = SendKey(c, Keys.Delete);
                Assert.False(e.Handled);
                Assert.Equal("abc", c.CurrentBuffer.Current.GetText(0, 3));
            }
        });

    [Fact]
    public void Route_CtrlHomeAndEnd_JumpsToDocumentEnds() =>
        Sta.Run(() =>
        {
            // HandleHome/HandleEnd: Ctrl 付きで文書頭/末尾へジャンプ
            var (f, c) = MakeControl("abc\ndef");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(2);
                SendKey(c, Keys.End | Keys.Control);
                Assert.Equal(7, c.CaretCharOffset); // "abc\ndef".Length = 7
                SendKey(c, Keys.Home | Keys.Control);
                Assert.Equal(0, c.CaretCharOffset);
            }
        });
}
