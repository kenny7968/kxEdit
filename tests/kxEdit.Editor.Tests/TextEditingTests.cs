using System.Reflection;
using kxEdit.Core.Text;

namespace kxEdit.Editor.Tests;

/// <summary>
/// P3 Task 9: 削除/Enter/Tab/Insert + EolMode の契約テスト。
/// OnKeyDown 経由で BackSpace/Delete/Enter/Tab/Insert を叩き、
/// バッファ変化・キャレット位置・EolMode 反映・サロゲート境界の 1 文字扱いを検証する。
/// </summary>
public class TextEditingTests
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

    private static void SendKey(EditorControl c, Keys keyData)
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        mi!.Invoke(c, new object[] { new KeyEventArgs(keyData) });
    }

    // ===== BackSpace =====
    [Fact]
    public void Backspace_DeletesPrevChar() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(2);
                SendKey(c, Keys.Back);
                Assert.Equal("ac", c.GetText());
                Assert.Equal(1, c.CaretCharOffset);
            }
        });

    [Fact]
    public void Backspace_AtStart_NoOp() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(0);
                SendKey(c, Keys.Back);
                Assert.Equal("abc", c.GetText());
            }
        });

    [Fact]
    public void Backspace_DeletesSurrogatePair() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("a😀b");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                SendKey(c, Keys.Back);
                Assert.Equal("ab", c.GetText());
                Assert.Equal(1, c.CaretCharOffset);
            }
        });

    [Fact]
    public void Backspace_DeletesSelection() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(1, 4);
                SendKey(c, Keys.Back);
                Assert.Equal("aef", c.GetText());
                Assert.Equal(1, c.CaretCharOffset);
            }
        });

    [Fact]
    public void Backspace_ReadOnly_NoOp() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.ReadOnly = true;
                c.SetCaretCharOffset(2);
                SendKey(c, Keys.Back);
                Assert.Equal("abc", c.GetText());
            }
        });

    // ===== Delete =====
    [Fact]
    public void Delete_DeletesNextChar() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKey(c, Keys.Delete);
                Assert.Equal("ac", c.GetText());
                Assert.Equal(1, c.CaretCharOffset);
            }
        });

    [Fact]
    public void Delete_AtEnd_NoOp() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                SendKey(c, Keys.Delete);
                Assert.Equal("abc", c.GetText());
            }
        });

    [Fact]
    public void Delete_DeletesSurrogatePair() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("a😀b");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKey(c, Keys.Delete);
                Assert.Equal("ab", c.GetText());
                Assert.Equal(1, c.CaretCharOffset);
            }
        });

    [Fact]
    public void Delete_DeletesSelection() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(1, 4);
                SendKey(c, Keys.Delete);
                Assert.Equal("aef", c.GetText());
                Assert.Equal(1, c.CaretCharOffset);
            }
        });

    // ===== Enter (EolMode) =====
    [Fact]
    public void Enter_InsertsCrlf_ByDefault() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                SendKey(c, Keys.Enter);
                Assert.Equal("abc\r\n", c.GetText());
                Assert.Equal(5, c.CaretCharOffset);
            }
        });

    [Fact]
    public void Enter_InsertsLf_WhenEolLf() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.EolMode = LineEnding.Lf;
                c.SetCaretCharOffset(3);
                SendKey(c, Keys.Enter);
                Assert.Equal("abc\n", c.GetText());
                Assert.Equal(4, c.CaretCharOffset);
            }
        });

    [Fact]
    public void Enter_InsertsCr_WhenEolCr() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.EolMode = LineEnding.Cr;
                c.SetCaretCharOffset(3);
                SendKey(c, Keys.Enter);
                Assert.Equal("abc\r", c.GetText());
                Assert.Equal(4, c.CaretCharOffset);
            }
        });

    [Fact]
    public void Enter_ReplacesSelection() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(1, 4);
                SendKey(c, Keys.Enter);
                Assert.Equal("a\r\nef", c.GetText());
            }
        });

    // ===== Tab =====
    [Fact]
    public void Tab_InsertsTabChar() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKey(c, Keys.Tab);
                Assert.Equal("a\tbc", c.GetText());
                Assert.Equal(2, c.CaretCharOffset);
            }
        });

    [Fact]
    public void Tab_ReplacesSelection() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(1, 4);
                SendKey(c, Keys.Tab);
                Assert.Equal("a\tef", c.GetText());
            }
        });

    // ===== Insert (Overtype toggle) =====
    [Fact]
    public void Insert_TogglesOvertype() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                Assert.False(c.Overtype);
                SendKey(c, Keys.Insert);
                Assert.True(c.Overtype);
                SendKey(c, Keys.Insert);
                Assert.False(c.Overtype);
            }
        });

    [Fact]
    public void CtrlInsert_DoesNotToggleOvertype() =>
        Sta.Run(() =>
        {
            // Ctrl+Insert は Task 11 のクリップボード用に予約=Task 9 では Overtype をトグルしない
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                SendKey(c, Keys.Insert | Keys.Control);
                Assert.False(c.Overtype);
            }
        });

    [Fact]
    public void ShiftInsert_DoesNotToggleOvertype() =>
        Sta.Run(() =>
        {
            // Shift+Insert は Task 11 の Paste 用に予約=Task 9 では Overtype をトグルしない
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                SendKey(c, Keys.Insert | Keys.Shift);
                Assert.False(c.Overtype);
            }
        });

    // ===== ReadOnly 網羅(S-2)=====

    [Fact]
    public void Delete_ReadOnly_NoOp() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.ReadOnly = true;
                c.SetCaretCharOffset(1);
                SendKey(c, Keys.Delete);
                Assert.Equal("abc", c.GetText());
            }
        });

    [Fact]
    public void Enter_ReadOnly_NoOp() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.ReadOnly = true;
                c.SetCaretCharOffset(3);
                SendKey(c, Keys.Enter);
                Assert.Equal("abc", c.GetText());
            }
        });

    [Fact]
    public void Tab_ReadOnly_NoOp() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.ReadOnly = true;
                c.SetCaretCharOffset(1);
                SendKey(c, Keys.Tab);
                Assert.Equal("abc", c.GetText());
            }
        });

    // ===== IsInputKey Tab =====
    [Fact]
    public void IsInputKey_IncludesTab() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                var mi = typeof(Control).GetMethod(
                    "IsInputKey",
                    BindingFlags.Instance | BindingFlags.NonPublic
                );
                bool tabIsInput = (bool)mi!.Invoke(c, new object[] { Keys.Tab })!;
                Assert.True(tabIsInput);
            }
        });
}
