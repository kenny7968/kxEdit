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
                Assert.Equal(1, c.CaretCharOffset); // 削除位置=元の選択開始
                Assert.Equal((1, 1), c.GetSelectionCharRange()); // 選択は解除
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
                // HasText=false だが Text は非空にしておく: ContainsUnicodeText のガードだけが
                // 唯一の防壁になり、削除すると "heXYllo" になって赤化する。
                // Text も空(=Fake の既定)にすると string.IsNullOrEmpty が同じ false を返すため、
                // ガードを消しても緑のままになる(CLAUDE.md §4-B: 既定値と区別できる fixture)。
                cb.HasText = false;
                cb.Text = "XY";
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
            // 成功経路では ClipboardFailed が上がらないことの対照(本文は変わる)。
            // 非既定位置(キャレット 2)から始める(CLAUDE.md §4-B)。
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

    // ===== 捕捉範囲(設計 §4.1: ExternalException 限定)=====

    /// <summary>
    /// <c>catch (ExternalException)</c> を <c>catch (Exception)</c> へ広げる変異を kill する。
    /// 設計 §4.1 は「呼び出し側バグ(<c>ArgumentNullException</c> 等)を握り潰さない」ために
    /// 型を限定しているが、それを守る網が無いとコードレビュー以外で守られない。
    /// </summary>
    [Fact]
    public void Copy_NonExternalException_Propagates() =>
        Sta.Run(() =>
        {
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                cb.ThrowOnSet = true;
                cb.ThrowInstead = new InvalidOperationException("caller bug");
                int raised = 0;
                c.ClipboardFailed += (_, _) => raised++;
                c.SetSelectionCharRange(1, 4);
                Assert.Throws<InvalidOperationException>(() => c.Copy());
                Assert.Equal(0, raised); // 失敗イベントに化けさせない
            }
        });

    /// <summary><see cref="Copy_NonExternalException_Propagates"/> の Paste 版
    /// (catch は Copy と別に書かれているので片方だけ広げる変異が起こりうる)。</summary>
    [Fact]
    public void Paste_NonExternalException_Propagates() =>
        Sta.Run(() =>
        {
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                cb.ThrowOnContains = true;
                cb.ThrowInstead = new InvalidOperationException("caller bug");
                int raised = 0;
                c.ClipboardFailed += (_, _) => raised++;
                c.SetCaretCharOffset(2);
                Assert.Throws<InvalidOperationException>(() => c.Paste());
                Assert.Equal(0, raised);
            }
        });

    // ===== no-op 契約(既存 ClipboardTests は Category=LocalOnly で CI から除外される。
    //       戻り値という新契約を足した以上、CI で走る網をここに置く)=====

    /// <summary><see cref="EditorControl.Copy"/> は本文不変なので <c>ReadOnly</c> でも動く
    /// (Notepad と同挙動)。Cut/Paste と同じ ReadOnly ガードを足す変異を kill する。</summary>
    [Fact]
    public void Copy_ReadOnly_StillWritesToClipboard() =>
        Sta.Run(() =>
        {
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                c.ReadOnly = true;
                c.SetSelectionCharRange(1, 4);
                Assert.True(c.Copy());
                Assert.Equal("ell", cb.Text);
            }
        });

    /// <summary>選択なしの <see cref="EditorControl.Copy"/> はクリップボードに<b>触らない</b>
    /// (既存内容を保持する)。「選択なしで空文字を書く」変異を kill する。</summary>
    [Fact]
    public void Copy_NoSelection_DoesNotTouchClipboard() =>
        Sta.Run(() =>
        {
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                cb.Text = "SENTINEL";
                cb.HasText = true;
                c.SetCaretCharOffset(2); // 非既定位置・選択なし
                Assert.False(c.Copy());
                Assert.Equal(0, cb.SetCount);
                Assert.Equal("SENTINEL", cb.Text);
            }
        });

    /// <summary><c>ReadOnly</c> の <see cref="EditorControl.Paste"/> はクリップボードを
    /// 読みにいかない(busy 時の 10×100ms リトライを無駄に踏まない=ガードの順序契約)。</summary>
    [Fact]
    public void Paste_ReadOnly_DoesNotTouchClipboard() =>
        Sta.Run(() =>
        {
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                cb.HasText = true;
                cb.Text = "XY";
                c.ReadOnly = true;
                c.SetCaretCharOffset(2);
                Assert.False(c.Paste());
                Assert.Equal(0, cb.ContainsCount);
                Assert.Equal("hello", c.GetText());
            }
        });

    /// <summary><c>ReadOnly</c> の <see cref="EditorControl.Cut"/> は本文もクリップボードも
    /// 変えない(Copy が ReadOnly で動くため、ガードを Copy 側に寄せる変異が危険)。</summary>
    [Fact]
    public void Cut_ReadOnly_DoesNotTouchClipboardOrText() =>
        Sta.Run(() =>
        {
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                cb.Text = "SENTINEL";
                cb.HasText = true;
                c.ReadOnly = true;
                c.SetSelectionCharRange(1, 4);
                c.Cut();
                Assert.Equal(0, cb.SetCount);
                Assert.Equal("SENTINEL", cb.Text);
                Assert.Equal("hello", c.GetText());
            }
        });
}
