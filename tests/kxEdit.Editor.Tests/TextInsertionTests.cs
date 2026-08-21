using System.Reflection;

namespace kxEdit.Editor.Tests;

/// <summary>
/// P3 Task 8: 文字挿入 + Overtype モードの契約テスト。
/// OnKeyPress は protected のため <see cref="KeyboardNavigationTests"/> と同じ
/// リフレクション経路で叩く(Task 8 の GetText 診断 API は internal 公開・
/// InternalsVisibleTo で参照)。
/// </summary>
public class TextInsertionTests
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

    // OnKeyPress を叩くリフレクション経路(既存の SendKey と同じ pattern)。
    private static void SendKeyPress(EditorControl c, char ch)
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnKeyPress",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        mi!.Invoke(c, new object[] { new KeyPressEventArgs(ch) });
    }

    // ===== 挿入モード =====

    [Fact]
    public void KeyPress_InsertsChar_AtCaret() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKeyPress(c, 'X');
                Assert.Equal("aXbc", c.GetText());
                Assert.Equal(2, c.CaretCharOffset);
            }
        });

    [Fact]
    public void KeyPress_ReplacesSelection() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(1, 4);
                SendKeyPress(c, 'X');
                Assert.Equal("aXef", c.GetText());
                Assert.Equal(2, c.CaretCharOffset);
                Assert.Equal(2, c.SelectionAnchor); // 選択解除
            }
        });

    [Fact]
    public void KeyPress_InsertsAtEnd() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                SendKeyPress(c, 'X');
                Assert.Equal("abcX", c.GetText());
                Assert.Equal(4, c.CaretCharOffset);
            }
        });

    [Fact]
    public void KeyPress_InsertsAtStart() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(0);
                SendKeyPress(c, 'X');
                Assert.Equal("Xabc", c.GetText());
                Assert.Equal(1, c.CaretCharOffset);
            }
        });

    [Fact]
    public void KeyPress_ControlChar_Ignored() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKeyPress(c, '\u0001'); // Ctrl+A の 0x01
                Assert.Equal("abc", c.GetText());
                Assert.Equal(1, c.CaretCharOffset);
            }
        });

    [Fact]
    public void KeyPress_BackspaceEnterTab_Ignored() =>
        Sta.Run(() =>
        {
            // Task 9 で OnKeyDown 経由で処理する予定=OnKeyPress では素通り
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(2);
                SendKeyPress(c, '\b'); // BackSpace 0x08
                SendKeyPress(c, '\r'); // Enter 0x0D
                SendKeyPress(c, '\t'); // Tab 0x09
                Assert.Equal("abc", c.GetText());
            }
        });

    // ===== Overtype モード =====

    [Fact]
    public void KeyPress_Overtype_Replaces1Char() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(2);
                c.Overtype = true;
                SendKeyPress(c, 'X');
                Assert.Equal("abXdef", c.GetText());
                Assert.Equal(3, c.CaretCharOffset);
            }
        });

    [Fact]
    public void KeyPress_Overtype_AtEol_InsertsWithoutReplace() =>
        Sta.Run(() =>
        {
            // 改行の直前(EOL 位置)では上書きモードでも改行を消さない
            var (f, c) = MakeControl("abc\ndef");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3); // \n の直前
                c.Overtype = true;
                SendKeyPress(c, 'X');
                Assert.Equal("abcX\ndef", c.GetText());
                Assert.Equal(4, c.CaretCharOffset);
            }
        });

    [Fact]
    public void KeyPress_Overtype_AtCrLf_InsertsWithoutReplace() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc\r\ndef");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3); // \r の直前
                c.Overtype = true;
                SendKeyPress(c, 'X');
                Assert.Equal("abcX\r\ndef", c.GetText());
            }
        });

    [Fact]
    public void KeyPress_Overtype_AtEnd_Inserts() =>
        Sta.Run(() =>
        {
            // Overtype 末尾では潰す対象がないので単純挿入
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                c.Overtype = true;
                SendKeyPress(c, 'X');
                Assert.Equal("abcX", c.GetText());
            }
        });

    [Fact]
    public void KeyPress_Overtype_SurrogatePair_ReplacesAsPair() =>
        Sta.Run(() =>
        {
            // "a😀b" の caret=1(😀 の直前)で Overtype 'X' → "aXb"(サロゲート 2 code units を潰す)
            var (f, c) = MakeControl("a😀b");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                c.Overtype = true;
                SendKeyPress(c, 'X');
                Assert.Equal("aXb", c.GetText());
                Assert.Equal(2, c.CaretCharOffset);
            }
        });

    // ===== ReadOnly =====

    [Fact]
    public void KeyPress_ReadOnly_NoInsertion() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.ReadOnly = true;
                c.SetCaretCharOffset(1);
                SendKeyPress(c, 'X');
                Assert.Equal("abc", c.GetText());
                Assert.Equal(1, c.CaretCharOffset);
            }
        });

    // ===== 選択削除+ReadOnly =====

    [Fact]
    public void KeyPress_ReadOnly_DoesNotDeleteSelection() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.ReadOnly = true;
                c.SetSelectionCharRange(1, 4);
                SendKeyPress(c, 'X');
                Assert.Equal("abcdef", c.GetText());
                Assert.Equal((1, 4), c.GetSelectionCharRange());
            }
        });

    // ===== Ctrl+Backspace の 0x7F (Task 8 レビュー I-1) =====

    [Fact]
    public void KeyPress_Ctrl_Backspace_0x7F_Ignored() =>
        Sta.Run(() =>
        {
            // Windows の Ctrl+Backspace は WM_CHAR で 0x7F(DEL)を送ってくる=無視すべき。
            // 現行の ch < 0x20 だけだと 0x7F を通してしまいバッファに DEL 制御文字が入るバグ。
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(2);
                SendKeyPress(c, '\u007F');
                Assert.Equal("abc", c.GetText());
                Assert.Equal(2, c.CaretCharOffset);
            }
        });

    // ===== Overtype + Selection の組み合わせ(Task 8 レビュー S-2) =====

    [Fact]
    public void KeyPress_OvertypeWithSelection_ReplacesSelection() =>
        Sta.Run(() =>
        {
            // 選択あり時は Overtype 分岐に入らず、選択部だけを置換する(現行実装の s != en 早期分岐)。
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.Overtype = true;
                c.SetSelectionCharRange(1, 4);
                SendKeyPress(c, 'X');
                Assert.Equal("aXef", c.GetText()); // 選択部のみ置換・5文字目 'e' は残る
                Assert.Equal(2, c.CaretCharOffset);
                Assert.Equal(2, c.SelectionAnchor); // 選択解除
            }
        });
}
