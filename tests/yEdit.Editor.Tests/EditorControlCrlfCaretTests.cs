namespace yEdit.Editor.Tests;

/// <summary>
/// 2026-07-24 CRLF atomic caret: <see cref="CaretController.SnapAndClamp"/> の中央 seam 化
/// (mid-CRLF → CR の前スナップ)が EditorControl 側の位置設定入り口
/// (<c>SetCaretCharOffset</c> / <c>SetSelectionCharRange</c>)にも波及することを検証する回帰保険。
/// </summary>
public class EditorControlCrlfCaretTests
{
    [Fact]
    public void SetCaretCharOffset_MidCrlf_SnapsToCr() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\r\nb"));
            ctrl.SetCaretCharOffset(2);
            Assert.Equal(1, ctrl.CaretCharOffset);
        });

    [Fact]
    public void SetSelectionCharRange_MidCrlf_SnapsToCr() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\r\nb"));
            ctrl.SetSelectionCharRange(2, 3);
            var (s, e) = ctrl.GetSelectionCharRange();
            Assert.Equal(1, s);
            Assert.Equal(3, e);
        });
}
