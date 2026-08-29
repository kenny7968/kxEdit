using System.Reflection;

namespace kxEdit.Editor.Tests;

/// <summary>
/// A-20(監査 2026-08-22 / 設計 2026-08-29 §6): WM_CHAR で高・低に分割到着する
/// サロゲートペアを 1 コードポイントとして結合する契約テスト。
/// 発現源は KEYEVENTF_UNICODE で WM_CHAR を 2 通送るツール(絵文字パネルは IME 経路で無事=
/// 設計書 §2.2)。実機再現は PostMessageW(WM_CHAR, 0xD83D) → (WM_CHAR, 0xDE02)。
/// </summary>
public class SurrogatePairInputTests
{
    private const char Hi = '\uD83D'; // U+1F602 😂 の高位
    private const char Lo = '\uDE02'; // 同 低位
    private const string Emoji = "😂";

    private static (Form f, EditorControl c) MakeControl(string text)
    {
        var f = new HostForm();
        var c = new EditorControl();
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    private static void SendKeyPress(EditorControl c, char ch)
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnKeyPress",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        mi!.Invoke(c, new object[] { new KeyPressEventArgs(ch) });
    }

    private static void SendKeyDown(EditorControl c, Keys keyData)
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        mi!.Invoke(c, new object[] { new KeyEventArgs(keyData) });
    }

    private static void SendLostFocus(EditorControl c)
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnLostFocus",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        mi!.Invoke(c, new object[] { EventArgs.Empty });
    }

    // ===== 結合 =====

    [Fact]
    public void HighThenLow_InsertsOneCodePoint() =>
        Sta.Run(() =>
        {
            // prefix/suffix を置いて「ペアだけが入った」ことを両端で固定する(CLAUDE.md §4-B)。
            var (f, c) = MakeControl("ab");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKeyPress(c, Hi);
                SendKeyPress(c, Lo);
                Assert.Equal("a" + Emoji + "b", c.GetText());
                Assert.Equal(3, c.CaretCharOffset); // 1 + 2 UTF-16 単位
            }
        });

    [Fact]
    public void HighAlone_InsertsNothing_UntilLowArrives() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("ab");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKeyPress(c, Hi);
                // まだ何も入らない(U+FFFD も入らない)
                Assert.Equal("ab", c.GetText());
                Assert.Equal(1, c.CaretCharOffset);
            }
        });

    // ===== 破棄 =====

    [Fact]
    public void HighThenBmp_DropsHigh_InsertsBmpOnly() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("ab");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKeyPress(c, Hi);
                SendKeyPress(c, 'X');
                Assert.Equal("aXb", c.GetText()); // U+FFFD が残らないこと
            }
        });

    [Fact]
    public void LowAlone_InsertsNothing() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("ab");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKeyPress(c, Lo);
                Assert.Equal("ab", c.GetText());
            }
        });

    [Fact]
    public void HighThenHighThenLow_DropsFirstHigh() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("ab");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKeyPress(c, Hi);
                SendKeyPress(c, Hi);
                SendKeyPress(c, Lo);
                Assert.Equal("a" + Emoji + "b", c.GetText()); // ペアは 1 つだけ
            }
        });

    [Fact]
    public void HighThenKeyDown_DropsPending() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("ab");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKeyPress(c, Hi);
                SendKeyDown(c, Keys.Right); // キー入力が挟まった
                SendKeyPress(c, Lo);
                Assert.Equal("ab", c.GetText()); // 何も入らない
            }
        });

    [Fact]
    public void HighThenLostFocus_DropsPending() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("ab");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKeyPress(c, Hi);
                SendLostFocus(c); // フォーカスが移った
                SendKeyPress(c, Lo);
                Assert.Equal("ab", c.GetText()); // 何も入らない
            }
        });

    // ===== 上書きモード =====

    [Fact]
    public void Overtype_PairReplacesExactlyOneCodePoint() =>
        Sta.Run(() =>
        {
            // 監査書の「上書きモードでは既存 2 文字を潰す」の回帰。
            // prefix "a" / suffix "Yb" を置き、潰れるのが X 1 文字だけであることを両端で固定する。
            var (f, c) = MakeControl("aXYb");
            using (f)
            using (c)
            {
                c.Overtype = true;
                c.SetCaretCharOffset(1);
                SendKeyPress(c, Hi);
                SendKeyPress(c, Lo);
                Assert.Equal("a" + Emoji + "Yb", c.GetText());
            }
        });
}
