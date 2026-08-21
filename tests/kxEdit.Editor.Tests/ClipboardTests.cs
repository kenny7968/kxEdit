using System.Reflection;
using kxEdit.Core.Text;

namespace kxEdit.Editor.Tests;

/// <summary>
/// P3 Task 11: クリップボード(Cut/Copy/Paste + SelectAll + Ctrl+X/C/V + レガシーキー)の契約テスト。
/// - Copy は本文不変(ReadOnly でも動く)/選択なしで no-op
/// - Cut は ReadOnly / 選択なしで no-op
/// - Paste は ReadOnly / 空クリップボードで no-op
/// - Ctrl+X / Ctrl+C / Ctrl+V の配線
/// - レガシーキー(Ctrl+Insert=Copy / Shift+Insert=Paste / Shift+Delete=Cut)の横取り
/// - Insert(修飾なし)=Overtype トグルが既存挙動維持
/// を STA スレッド上で検証する(<see cref="Clipboard"/> は STA 必須)。
/// </summary>
/// <remarks>
/// 各テスト冒頭で <c>Clipboard.SetText("SENTINEL")</c> 等の既知値を書き込み、
/// テスト間の順序依存を避ける(SENTINEL がそのまま残っていれば「Copy/Cut が触らなかった」判定になる)。
///
/// LocalOnly 分類: 実クリップボードはプロセス横断のグローバル資源で、CI ランナー上で他プロセス
/// (エージェントの clipboard ヘルパ・IME 等)と衝突する可能性が高い。
/// <see cref="SetClipboardTextAndWait"/> の 30×10ms リトライは短命 STA 上での WM_CLIPBOARDUPDATE
/// 反映遅延を吸収する目的で、CI 他プロセス競合の完全解決はできない(=フレーク源候補筆頭)。
/// pre-merge-check は Category フィルタなしで全数実行するためローカルでは常時走る。
/// ci.yml / release.yml 側は <c>--filter "Category!=LocalOnly"</c> で除外し、
/// 実機での退行検知はローカルゲート+実機 SR 検証に委ねる。
/// </remarks>
[Trait("Category", "LocalOnly")]
public class ClipboardTests
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

    /// <summary>
    /// <see cref="Clipboard.SetText(string, TextDataFormat)"/> の書き込み確定を待つヘルパ。
    /// Windows のシステムクリップボードは書き込み後の反映が非同期(WM_CLIPBOARDUPDATE 等の
    /// メッセージ伝播が絡む)で、Sta.Run で作られた短命 STA スレッド上では SetText 直後の
    /// Read で稀に空/古い内容が返る(WinForms テストの既知フレーク)。SetText 内部の 10 回 ×
    /// 100ms リトライは Set 側の話で Get 側の即時反映は保証されない。ここでは
    /// <see cref="Application.DoEvents"/> でメッセージキューを排出しつつ最大 30 回 × 10ms 待ち、
    /// 期待値が 3 回連続で読めた段階で「安定」と判定して return する(合計最大 300ms=実運用では
    /// 数十 ms で通る・単発読み一致だけを終了条件にすると「1 回目 OK・2 回目空」の瞬間で
    /// 抜けてしまい後続の Paste で読み損ねるため連続一致条件を採用)。
    /// </summary>
    private static void SetClipboardTextAndWait(
        string text,
        TextDataFormat format = TextDataFormat.UnicodeText
    )
    {
        Clipboard.SetText(text, format);
        // 3 回連続で期待値を読めるまで待つ(単発読みでは OS の delayed rendering / WM_CLIPBOARDUPDATE
        // 到達タイミングの隙間で「1 回目 OK・2 回目空」のような瞬間が残るため、連続一致を安定化条件にする)。
        int streak = 0;
        for (int i = 0; i < 30; i++)
        {
            Application.DoEvents(); // 短命 STA スレッドのメッセージキューを空にする
            if (Clipboard.ContainsText(format) && Clipboard.GetText(format) == text)
            {
                streak++;
                if (streak >= 3)
                    return;
            }
            else
            {
                streak = 0;
            }
            Thread.Sleep(10);
        }
        // 最終試行(失敗しても Assert 側で顕在化=詳細なエラーメッセージが出る)
    }

    /// <summary>
    /// クリップボードの UnicodeText を読み取る。<see cref="EditorControl.Copy"/> /
    /// <see cref="EditorControl.Cut"/> 直後に <see cref="Clipboard.GetText(TextDataFormat)"/> を叩くと、
    /// <see cref="Clipboard.SetText"/> 内部の完了通知(WM_CLIPBOARDUPDATE 等)を我々の Sta 側
    /// スレッドがまだ受け取っていないタイミングだと空文字列が返る。ここでは
    /// <see cref="Application.DoEvents"/> でメッセージキューを排出しつつ最大 30 回 × 10ms 再試行し、
    /// 空でない値が読めた時点で return する。全て空だった場合は最後に読んだ値(=空文字列)を返して
    /// 呼び出し側の Assert に判定を委ねる。
    /// </summary>
    private static string GetClipboardTextWithRetry(
        TextDataFormat format = TextDataFormat.UnicodeText
    )
    {
        string last = string.Empty;
        for (int i = 0; i < 30; i++)
        {
            Application.DoEvents();
            if (Clipboard.ContainsText(format))
            {
                last = Clipboard.GetText(format);
                if (!string.IsNullOrEmpty(last))
                    return last;
            }
            Thread.Sleep(10);
        }
        return last;
    }

    // ===== Copy =====

    [Fact]
    public void Copy_PutsSelectedText_OnClipboard() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("hello");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(1, 4);
                c.Copy();
                Assert.Equal("ell", GetClipboardTextWithRetry());
                Assert.Equal("hello", c.GetText()); // Copy は本文不変
            }
        });

    [Fact]
    public void Copy_NoSelection_NoOp() =>
        Sta.Run(() =>
        {
            SetClipboardTextAndWait("SENTINEL"); // sanity(反映確定を待つ)
            var (f, c) = MakeControl("hello");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(2);
                c.Copy();
                // Clipboard 内容が SENTINEL のまま(=Copy が触っていない)
                Assert.Equal("SENTINEL", Clipboard.GetText(TextDataFormat.UnicodeText));
            }
        });

    // ===== Cut =====

    [Fact]
    public void Cut_RemovesSelection_AndPutsOnClipboard() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("hello");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(1, 4);
                c.Cut();
                Assert.Equal("ell", GetClipboardTextWithRetry());
                Assert.Equal("ho", c.GetText());
                Assert.Equal(1, c.CaretCharOffset);
            }
        });

    [Fact]
    public void Cut_ReadOnly_NoOp() =>
        Sta.Run(() =>
        {
            SetClipboardTextAndWait("SENTINEL");
            var (f, c) = MakeControl("hello");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(1, 4);
                c.ReadOnly = true;
                c.Cut();
                Assert.Equal("SENTINEL", Clipboard.GetText(TextDataFormat.UnicodeText));
                Assert.Equal("hello", c.GetText());
            }
        });

    [Fact]
    public void Cut_NoSelection_NoOp() =>
        Sta.Run(() =>
        {
            // 選択なしでの Cut は本文・キャレット・クリップボードすべて不変
            // (Copy_NoSelection_NoOp と対で S-2 レビュー対応: 選択なし時の Cut が
            //  誤って全文/カレント行等を切り取る退行を検知するため)
            SetClipboardTextAndWait("SENTINEL");
            var (f, c) = MakeControl("hello");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(2);
                c.Cut();
                Assert.Equal("SENTINEL", Clipboard.GetText(TextDataFormat.UnicodeText));
                Assert.Equal("hello", c.GetText());
                Assert.Equal(2, c.CaretCharOffset);
            }
        });

    // ===== Paste =====

    [Fact]
    public void Paste_InsertsClipboardAtCaret() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("hello");
            using (f)
            using (c)
            {
                SetClipboardTextAndWait("XY");
                c.SetCaretCharOffset(2);
                c.Paste();
                Assert.Equal("heXYllo", c.GetText());
                Assert.Equal(4, c.CaretCharOffset);
            }
        });

    [Fact]
    public void Paste_ReplacesSelection() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("hello");
            using (f)
            using (c)
            {
                SetClipboardTextAndWait("XY");
                c.SetSelectionCharRange(1, 4);
                c.Paste();
                Assert.Equal("hXYo", c.GetText());
                Assert.Equal(3, c.CaretCharOffset);
            }
        });

    [Fact]
    public void Paste_ReadOnly_NoOp() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("hello");
            using (f)
            using (c)
            {
                SetClipboardTextAndWait("XY");
                c.ReadOnly = true;
                c.SetCaretCharOffset(2);
                c.Paste();
                Assert.Equal("hello", c.GetText());
            }
        });

    [Fact]
    public void Paste_EmptyClipboard_NoOp() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("hello");
            using (f)
            using (c)
            {
                Clipboard.Clear();
                c.SetCaretCharOffset(2);
                c.Paste();
                Assert.Equal("hello", c.GetText());
            }
        });

    // ===== SelectAll =====

    [Fact]
    public void SelectAll_SelectsWholeDocument() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("hello");
            using (f)
            using (c)
            {
                c.SelectAll();
                Assert.Equal((0, 5), c.GetSelectionCharRange());
            }
        });

    // ===== Ctrl+X/C/V =====

    [Fact]
    public void CtrlC_Copy() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("hello");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(0, 5);
                SendKey(c, Keys.C | Keys.Control);
                Assert.Equal("hello", GetClipboardTextWithRetry());
            }
        });

    [Fact]
    public void CtrlX_Cut() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("hello");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(0, 5);
                SendKey(c, Keys.X | Keys.Control);
                Assert.Equal("hello", GetClipboardTextWithRetry());
                Assert.Equal("", c.GetText());
            }
        });

    [Fact]
    public void CtrlV_Paste() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                SetClipboardTextAndWait("Z");
                c.SetCaretCharOffset(1);
                SendKey(c, Keys.V | Keys.Control);
                Assert.Equal("aZbc", c.GetText());
            }
        });

    // ===== レガシーキー =====

    [Fact]
    public void CtrlInsert_Copy() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("hello");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(0, 5);
                SendKey(c, Keys.Insert | Keys.Control);
                Assert.Equal("hello", GetClipboardTextWithRetry());
            }
        });

    [Fact]
    public void ShiftInsert_Paste() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                SetClipboardTextAndWait("Z");
                c.SetCaretCharOffset(1);
                SendKey(c, Keys.Insert | Keys.Shift);
                Assert.Equal("aZbc", c.GetText());
            }
        });

    [Fact]
    public void ShiftDelete_Cut() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("hello");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(0, 5);
                SendKey(c, Keys.Delete | Keys.Shift);
                Assert.Equal("hello", GetClipboardTextWithRetry());
                Assert.Equal("", c.GetText());
            }
        });

    // ===== Insert が Overtype をトグル(既存挙動維持)=====

    [Fact]
    public void Insert_TogglesOvertype_StillWorks_AfterAddingCopyPaste() =>
        Sta.Run(() =>
        {
            // Task 9 の Insert case が残っていること・Ctrl/Shift+Insert が横取りしていること
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                SendKey(c, Keys.Insert);
                Assert.True(c.Overtype);
            }
        });

    [Fact]
    public void CtrlInsert_DoesNotToggleOvertype() =>
        Sta.Run(() =>
        {
            // Ctrl+Insert は Copy が横取り=Overtype はトグルされない(Task 9 の Keys.Insert case は届かない)。
            // C# switch のフォールスルーは無いため理論的には不要だが、将来 case 順序が入れ替わった際の
            // 回帰検出のため保険で敷く(S-3 レビュー対応・OnKeyDown xmldoc の case 順序規約と対)。
            var (f, c) = MakeControl("hello");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(0, 5);
                bool before = c.Overtype;
                SendKey(c, Keys.Insert | Keys.Control);
                Assert.Equal(before, c.Overtype);
            }
        });
}
