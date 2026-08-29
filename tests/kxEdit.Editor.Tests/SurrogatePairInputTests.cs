using System.Reflection;
using kxEdit.Editor.Tests.Fakes;

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
    public void HighThenNonKeyEdit_DropsPending() =>
        Sta.Run(() =>
        {
            // メニュー経由の貼り付け(OnKeyDown を伴わない編集)。**列挙側の契機では捕まらず、
            // AfterEdit の事後条件だけが保留を落とす経路**=設計 §6.2 が求めた
            // 「列挙ではなく事後条件で守る」の当のケース。
            // AfterEdit の DropPendingHighSurrogate() を消すと "aXY😂b" になって赤化する。
            var (f, c) = MakeControl("ab");
            var cb = new FakeClipboard { HasText = true, Text = "XY" };
            c.SetClipboardForTest(cb);
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKeyPress(c, Hi);
                Assert.True(c.Paste()); // AfterEdit を通す(OnKeyDown は伴わない)
                SendKeyPress(c, Lo);
                Assert.Equal("aXYb", c.GetText()); // 絵文字が湧かない
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

    // ===== 実メッセージ経路(WndProc)=====
    // 上の群は OnKeyPress をリフレクションで直接叩くので、WndProc → OnKeyDown/OnKeyPress の
    // 配管を通らない。A-20 の現実の発現源(KEYEVENTF_UNICODE の SendInput)は
    // WM_CHAR の前に WM_KEYDOWN VK_PACKET を挟むため、その配管ごと検証しないと
    // 「直したはずの経路でだけ動かない」を取りこぼす(設計 §8.2 が __TestProcessMessage を
    // 指定していたのはこのため)。

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_CHAR = 0x0102;
    private const int VK_PACKET = 0xE7;

    private static void SendMessage(EditorControl c, int msg, int wparam)
    {
        var m = Message.Create(c.Handle, msg, (IntPtr)wparam, (IntPtr)1);
        c.__TestProcessMessage(ref m);
    }

    [Fact]
    public void RawWmChar_HighThenLow_InsertsOneCodePoint() =>
        Sta.Run(() =>
        {
            // PostMessageW(WM_CHAR, 0xD83D) → (WM_CHAR, 0xDE02) 相当(設計 §2.2 の実機再現)。
            var (f, c) = MakeControl("ab");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendMessage(c, WM_CHAR, Hi);
                SendMessage(c, WM_CHAR, Lo);
                Assert.Equal("a" + Emoji + "b", c.GetText());
            }
        });

    [Fact]
    public void VkPacket_HighThenLow_InsertsOneCodePoint() =>
        Sta.Run(() =>
        {
            // KEYEVENTF_UNICODE の SendInput が実際に送る並び。A-20 の現実の発現源はこれで、
            // OnKeyDown で無条件に保留を破棄すると**この経路でだけ**ペアが結合せず、
            // 絵文字が丸ごと消える(U+FFFD ですらなくなる)。
            var (f, c) = MakeControl("ab");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendMessage(c, WM_KEYDOWN, VK_PACKET);
                SendMessage(c, WM_CHAR, Hi);
                SendMessage(c, WM_KEYUP, VK_PACKET);
                SendMessage(c, WM_KEYDOWN, VK_PACKET);
                SendMessage(c, WM_CHAR, Lo);
                SendMessage(c, WM_KEYUP, VK_PACKET);
                Assert.Equal("a" + Emoji + "b", c.GetText());
            }
        });

    [Fact]
    public void VkPacket_HighThenRealKey_DropsPending() =>
        Sta.Run(() =>
        {
            // VK_PACKET を素通しにしたことで「本物のキーでも破棄されなくなる」変異を弁別する。
            // 矢印キーが挟まったら保留は捨てる(設計 §6.2 の契約は生きている)。
            var (f, c) = MakeControl("ab");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendMessage(c, WM_KEYDOWN, VK_PACKET);
                SendMessage(c, WM_CHAR, Hi);
                SendMessage(c, WM_KEYDOWN, (int)Keys.Right); // 本物のキー入力
                SendMessage(c, WM_KEYDOWN, VK_PACKET);
                SendMessage(c, WM_CHAR, Lo);
                Assert.Equal("ab", c.GetText()); // 何も入らない
            }
        });

    // ===== 上書きモード =====

    [Fact]
    public void Overtype_PairReplacesExactlyOneCodePoint() =>
        Sta.Run(() =>
        {
            // 監査書の「上書きモードでは既存 2 文字を潰す」に対する**唯一の回帰網**。
            // 分割ペアを実際に生むのは VK_PACKET 経路なので、リフレクション直叩きではなく
            // 実 WndProc 経路で踏む(§0 の取りこぼしと同じ形にしない)。
            // prefix "a" / suffix "Yb" を置き、潰れるのが X 1 文字だけであることを両端で固定する。
            var (f, c) = MakeControl("aXYb");
            using (f)
            using (c)
            {
                c.Overtype = true;
                c.SetCaretCharOffset(1);
                SendMessage(c, WM_KEYDOWN, VK_PACKET);
                SendMessage(c, WM_CHAR, Hi);
                SendMessage(c, WM_KEYUP, VK_PACKET);
                SendMessage(c, WM_KEYDOWN, VK_PACKET);
                SendMessage(c, WM_CHAR, Lo);
                SendMessage(c, WM_KEYUP, VK_PACKET);
                Assert.Equal("a" + Emoji + "Yb", c.GetText());
            }
        });
}
