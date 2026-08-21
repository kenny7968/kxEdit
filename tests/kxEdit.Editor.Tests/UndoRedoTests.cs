using System.Reflection;
using kxEdit.Core.Text;

namespace kxEdit.Editor.Tests;

/// <summary>
/// P3 Task 10: Undo/Redo 配線 + Modified/SavePoint/EmptyUndoBuffer の契約テスト。
/// - TextBuffer の Undo/Redo API が EditorControl 経由で正しく呼ばれるか
/// - Ctrl+Z / Ctrl+Y が OnKeyDown で配線されているか
/// - SetSavePoint / EmptyUndoBuffer の副作用(Modified/CanUndo)
/// - ReadOnly 時の no-op
/// を STA スレッド上で検証する(WinForms 契約)。
/// </summary>
public class UndoRedoTests
{
    private static (Form f, EditorControl c) MakeControl(string text)
    {
        var f = new Form();
        var c = new EditorControl();
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    private static void SendKey(EditorControl c, Keys keyData)
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        mi!.Invoke(c, new object[] { new KeyEventArgs(keyData) });
    }

    private static void SendKeyPress(EditorControl c, char ch)
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnKeyPress",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        mi!.Invoke(c, new object[] { new KeyPressEventArgs(ch) });
    }

    // ===== 基本 =====

    [Fact]
    public void Undo_RestoresText_AndCaret() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                SendKeyPress(c, 'X');
                Assert.Equal("abcX", c.GetText());
                Assert.True(c.CanUndo);
                Assert.True(c.Modified);

                c.Undo();
                Assert.Equal("abc", c.GetText());
                Assert.Equal(3, c.CaretCharOffset);
                Assert.False(c.Modified);
            }
        });

    [Fact]
    public void Redo_ReappliesEdit() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                SendKeyPress(c, 'X');
                c.Undo();
                Assert.False(c.Modified);
                Assert.True(c.CanRedo);

                c.Redo();
                Assert.Equal("abcX", c.GetText());
                Assert.Equal(4, c.CaretCharOffset);
                Assert.True(c.Modified);
            }
        });

    [Fact]
    public void Undo_NoOp_WhenNoHistory() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                Assert.False(c.CanUndo);
                c.Undo(); // 例外なし
                Assert.Equal("abc", c.GetText());
            }
        });

    [Fact]
    public void Redo_NoOp_WhenNoHistory() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                Assert.False(c.CanRedo);
                c.Redo();
                Assert.Equal("abc", c.GetText());
            }
        });

    // ===== Ctrl+Z / Ctrl+Y =====

    [Fact]
    public void CtrlZ_Undo() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                SendKeyPress(c, 'X');
                SendKey(c, Keys.Z | Keys.Control);
                Assert.Equal("abc", c.GetText());
            }
        });

    [Fact]
    public void CtrlY_Redo() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                SendKeyPress(c, 'X');
                c.Undo();
                SendKey(c, Keys.Y | Keys.Control);
                Assert.Equal("abcX", c.GetText());
            }
        });

    // ===== SavePoint / Modified =====

    [Fact]
    public void SetSavePoint_ResetsModified() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                SendKeyPress(c, 'X');
                Assert.True(c.Modified);
                c.SetSavePoint();
                Assert.False(c.Modified);
            }
        });

    [Fact]
    public void Modified_ReturnsFalse_BeforeSetSource() =>
        Sta.Run(() =>
        {
            using var f = new Form();
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            Assert.False(c.Modified);
            Assert.False(c.CanUndo);
            Assert.False(c.CanRedo);
        });

    // ===== EmptyUndoBuffer =====

    [Fact]
    public void EmptyUndoBuffer_ClearsHistory() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                SendKeyPress(c, 'X');
                Assert.True(c.CanUndo);
                c.EmptyUndoBuffer();
                Assert.False(c.CanUndo);
                Assert.False(c.CanRedo);
            }
        });

    // ===== ReadOnly =====

    [Fact]
    public void CtrlZ_ReadOnly_NoOp() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                SendKeyPress(c, 'X');
                c.ReadOnly = true;
                SendKey(c, Keys.Z | Keys.Control);
                Assert.Equal("abcX", c.GetText()); // Undo されない
            }
        });

    [Fact]
    public void Undo_Method_NoOp_WhenReadOnly() =>
        Sta.Run(() =>
        {
            // I-1 対応の回帰保護: メソッド直接呼び出しでも ReadOnly なら Undo が no-op になる
            // (App 層メニュー shortcut は OnKeyDown を経由しないため・CSV グリッドモード互換)
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                SendKeyPress(c, 'X');
                Assert.Equal("abcX", c.GetText());
                c.ReadOnly = true;
                c.Undo(); // メソッド直接呼び出しでも no-op
                Assert.Equal("abcX", c.GetText());
            }
        });

    [Fact]
    public void Redo_Method_NoOp_WhenReadOnly() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                SendKeyPress(c, 'X');
                c.Undo();
                Assert.True(c.CanRedo);
                c.ReadOnly = true;
                c.Redo(); // メソッド直接呼び出しでも no-op
                Assert.Equal("abc", c.GetText());
            }
        });

    // ===== Redo スタック消費 =====

    [Fact]
    public void Redo_ClearsCanRedo_AfterConsuming() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                SendKeyPress(c, 'X');
                c.Undo();
                Assert.True(c.CanRedo);
                c.Redo();
                Assert.False(c.CanRedo); // Redo スタックが消費された
                Assert.True(c.CanUndo); // Undo スタックには積まれた
            }
        });

    // ===== BackSpace + Undo =====

    [Fact]
    public void Undo_RestoresBackspaceDeletion() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(2);
                SendKey(c, Keys.Back); // "ac", caret=1
                Assert.Equal("ac", c.GetText());
                c.Undo();
                Assert.Equal("abc", c.GetText());
            }
        });

    // ===== 選択削除 + Undo =====

    [Fact]
    public void Undo_RestoresSelectionDeletion() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(1, 4);
                SendKey(c, Keys.Delete);
                Assert.Equal("aef", c.GetText());
                c.Undo();
                Assert.Equal("abcdef", c.GetText());
            }
        });
}
