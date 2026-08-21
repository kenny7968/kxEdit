using kxEdit.Core.Text;

namespace kxEdit.Editor.Tests;

/// <summary>
/// P3 Task 14: 残互換 API(<see cref="EditorControl.CurrentLine"/> /
/// <see cref="EditorControl.GetColumn"/> / <see cref="EditorControl.ReplaceCharRange"/>)の
/// 契約スモーク。P6 で ScintillaHost を置換した際の App 層互換のため、同名 API を
/// EditorControl 側に先出しする。設計書 §2-8。
/// </summary>
public class EditorControlContractTests
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

    // ===== CurrentLine =====

    [Fact]
    public void CurrentLine_ReturnsZero_BeforeSetSource() =>
        Sta.Run(() =>
        {
            using var f = new HostForm();
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            Assert.Equal(0, c.CurrentLine);
        });

    [Fact]
    public void CurrentLine_ReturnsCorrectLine() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc\ndef\nxyz");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(0);
                Assert.Equal(0, c.CurrentLine);
                c.SetCaretCharOffset(5); // "def" の 'e'
                Assert.Equal(1, c.CurrentLine);
                c.SetCaretCharOffset(9); // "xyz" の 'y'
                Assert.Equal(2, c.CurrentLine);
            }
        });

    // ===== GetColumn =====

    [Fact]
    public void GetColumn_ReturnsZero_ForLineStart() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc\ndef");
            using (f)
            using (c)
            {
                Assert.Equal(0, c.GetColumn(0)); // 行0 の頭
                Assert.Equal(0, c.GetColumn(4)); // 行1 の頭
            }
        });

    [Fact]
    public void GetColumn_ReturnsInLineOffset() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc\ndef");
            using (f)
            using (c)
            {
                Assert.Equal(1, c.GetColumn(1)); // 'b'
                Assert.Equal(2, c.GetColumn(6)); // 'f'(行1)
            }
        });

    [Fact]
    public void GetColumn_ClampsOutOfRange() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                Assert.Equal(3, c.GetColumn(9999)); // 末尾クランプ
                Assert.Equal(0, c.GetColumn(-1)); // 先頭クランプ
            }
        });

    // ===== ReplaceCharRange =====

    [Fact]
    public void ReplaceCharRange_ReplacesText() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.ReplaceCharRange(1, 3, "XY");
                Assert.Equal("aXYef", c.GetText());
                Assert.Equal(3, c.CaretCharOffset); // s + replacement.Length
            }
        });

    [Fact]
    public void ReplaceCharRange_InsertsWhenLengthZero() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.ReplaceCharRange(1, 0, "X");
                Assert.Equal("aXbc", c.GetText());
            }
        });

    [Fact]
    public void ReplaceCharRange_DeletesWhenReplacementEmpty() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.ReplaceCharRange(1, 3, "");
                Assert.Equal("aef", c.GetText());
            }
        });

    [Fact]
    public void ReplaceCharRange_ReadOnly_NoOp() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.ReadOnly = true;
                c.ReplaceCharRange(1, 1, "X");
                Assert.Equal("abc", c.GetText());
            }
        });

    [Fact]
    public void ReplaceCharRange_ClampsOutOfRange() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.ReplaceCharRange(0, 9999, "X"); // 全文置換
                Assert.Equal("X", c.GetText());
            }
        });

    [Fact]
    public void ReplaceCharRange_UpdatesUndoHistory() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.ReplaceCharRange(1, 1, "X");
                Assert.Equal("aXc", c.GetText());
                Assert.True(c.CanUndo);
                c.Undo();
                Assert.Equal("abc", c.GetText());
            }
        });
}
