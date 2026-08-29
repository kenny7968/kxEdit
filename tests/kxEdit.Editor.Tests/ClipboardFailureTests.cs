using kxEdit.Editor.Tests.Fakes;

namespace kxEdit.Editor.Tests;

/// <summary>
/// A-13(監査 2026-08-22 / 設計 2026-08-29 §4): クリップボードが他プロセスに保持されている
/// 間の <c>ExternalException</c> を発生源で捕捉する契約テスト。
/// 実機再現: 別プロセスで <c>OpenClipboard(NULL)</c> を 12 秒保持したまま Ctrl+C(設計書 §2.1)。
/// 修正前は WinForms 既定の未処理例外ダイアログが出て、その「終了」が保存確認のキャンセルを
/// 無視して落ち、hot exit バックアップも書かれないことがあった=未保存データの喪失経路。
/// </summary>
/// <remarks>
/// <see cref="FakeClipboard"/> を使うので実クリップボードを触らない
/// = <c>ClipboardTests</c> と違い <c>Category=LocalOnly</c> ではない(CI でも走る)。
/// </remarks>
public class ClipboardFailureTests
{
    private static (Form f, EditorControl c, FakeClipboard cb) MakeControl(string text)
    {
        var f = new HostForm();
        var c = new EditorControl();
        var cb = new FakeClipboard();
        c.SetClipboardForTest(cb);
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c, cb);
    }

    // ===== Copy =====

    [Fact]
    public void Copy_ClipboardBusy_ReturnsFalse_AndRaisesEventOnce() =>
        Sta.Run(() =>
        {
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                cb.ThrowOnSet = true;
                var kinds = new List<ClipboardFailureKind>();
                c.ClipboardFailed += (_, k) => kinds.Add(k);

                c.SetSelectionCharRange(1, 4);
                Assert.False(c.Copy());
                Assert.Equal("hello", c.GetText()); // 本文不変
                Assert.Equal(new[] { ClipboardFailureKind.Write }, kinds);
            }
        });

    [Fact]
    public void Copy_Success_DoesNotRaiseEvent() =>
        Sta.Run(() =>
        {
            // no-change のテスト。非既定位置(1..4)から始める(CLAUDE.md §4-B)。
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                int raised = 0;
                c.ClipboardFailed += (_, _) => raised++;
                c.SetSelectionCharRange(1, 4);
                Assert.True(c.Copy());
                Assert.Equal("ell", cb.Text);
                Assert.Equal(0, raised);
            }
        });

    // ===== Cut(いちばん重要な回帰)=====

    [Fact]
    public void Cut_ClipboardBusy_DoesNotDeleteText() =>
        Sta.Run(() =>
        {
            // 既存契約(EditorControl.Cut の remarks):
            // 「クリップボードに書けなかったら本文を消さない」。
            // Copy の中で例外を握り潰すとここが壊れ、A-13 より重いデータ喪失に化ける。
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                cb.ThrowOnSet = true;
                c.SetSelectionCharRange(1, 4);
                c.Cut();
                Assert.Equal("hello", c.GetText()); // 本文が残っていること
                Assert.Equal((1, 4), c.GetSelectionCharRange()); // 選択も残ること
            }
        });

    [Fact]
    public void Cut_ClipboardBusy_RaisesWriteOnce() =>
        Sta.Run(() =>
        {
            // 通知は Copy が 1 回だけ上げる(Cut は二重通知しない)。
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                cb.ThrowOnSet = true;
                var kinds = new List<ClipboardFailureKind>();
                c.ClipboardFailed += (_, k) => kinds.Add(k);
                c.SetSelectionCharRange(1, 4);
                c.Cut();
                Assert.Equal(new[] { ClipboardFailureKind.Write }, kinds);
            }
        });

    [Fact]
    public void Cut_Success_StillDeletesText() =>
        Sta.Run(() =>
        {
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(1, 4);
                c.Cut();
                Assert.Equal("ho", c.GetText());
                Assert.Equal("ell", cb.Text);
            }
        });

    // ===== Paste =====

    [Fact]
    public void Paste_ContainsThrows_ReturnsFalse_AndRaisesRead() =>
        Sta.Run(() =>
        {
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                cb.ThrowOnContains = true;
                var kinds = new List<ClipboardFailureKind>();
                c.ClipboardFailed += (_, k) => kinds.Add(k);
                c.SetCaretCharOffset(2);
                Assert.False(c.Paste());
                Assert.Equal("hello", c.GetText());
                Assert.Equal(new[] { ClipboardFailureKind.Read }, kinds);
            }
        });

    [Fact]
    public void Paste_GetThrows_ReturnsFalse_AndRaisesRead() =>
        Sta.Run(() =>
        {
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                cb.HasText = true; // Contains は通り Get で落ちる
                cb.ThrowOnGet = true;
                var kinds = new List<ClipboardFailureKind>();
                c.ClipboardFailed += (_, k) => kinds.Add(k);
                c.SetCaretCharOffset(2);
                Assert.False(c.Paste());
                Assert.Equal("hello", c.GetText());
                Assert.Equal(new[] { ClipboardFailureKind.Read }, kinds);
            }
        });

    [Fact]
    public void Paste_EmptyClipboard_ReturnsFalse_WithoutRaising() =>
        Sta.Run(() =>
        {
            // 「空で no-op」は失敗ではない = イベントを上げない。
            // 戻り値 false は「挿入しなかった」の意味で、失敗の判定には使えない
            // (= EditorControl.Paste の returns/remarks に明記した契約)。
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                cb.HasText = false;
                int raised = 0;
                c.ClipboardFailed += (_, _) => raised++;
                c.SetCaretCharOffset(2);
                Assert.False(c.Paste());
                Assert.Equal("hello", c.GetText());
                Assert.Equal(0, raised);
            }
        });

    [Fact]
    public void Paste_Success_ReturnsTrue_WithoutRaising() =>
        Sta.Run(() =>
        {
            // no-change のテスト。非既定位置(キャレット 2)から始める(CLAUDE.md §4-B)。
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                cb.HasText = true;
                cb.Text = "XY";
                int raised = 0;
                c.ClipboardFailed += (_, _) => raised++;
                c.SetCaretCharOffset(2);
                Assert.True(c.Paste());
                Assert.Equal("heXYllo", c.GetText());
                Assert.Equal(0, raised);
            }
        });
}
