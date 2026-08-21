// UiaTextHostAdapterCrlfTests.cs
// 2026-07-24 CRLF atomic caret Task 2: UIA NextChar/PrevChar が CRLF pair を
// 1 論理文字として扱う(atomic に 2 文字進退)ことと、SetSelection 経由の mid-CRLF が
// CaretController 中央 snap(Task 1)によって CR の前に丸められることを検証する。
//
// 孤立 CR / 孤立 LF は 1 文字ずつ進退する(既存 CR-only / LF-only 挙動と bit-perfect)。
using kxEdit.Accessibility;

namespace kxEdit.Editor.Tests;

public class UiaTextHostAdapterCrlfTests
{
    [Fact]
    public void UiaNextChar_SkipsCrlfPair() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\r\nb"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(3, host.NextChar(1)); // CR の前 → LF の後(pair skip)
        });

    [Fact]
    public void UiaPrevChar_SkipsCrlfPair() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\r\nb"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(1, host.PrevChar(3)); // LF の後 → CR の前
        });

    [Fact]
    public void UiaNextChar_LoneCr_MovesOneStep() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\rb"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(2, host.NextChar(1));
        });

    [Fact]
    public void UiaPrevChar_LoneLf_MovesOneStep() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(1, host.PrevChar(2));
        });

    /// <summary>
    /// Task 1 で <see cref="CaretController.SnapAndClamp"/> が mid-CRLF snap を実装済み。
    /// UIA SetSelection は <c>EditorControl.SetSelectionCharRange</c> へ委譲=SnapAndClamp を通る。
    /// 追加コード無しで pass する回帰保険。
    /// </summary>
    [Fact]
    public void UiaSetSelection_MidCrlf_SnapsToCr() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\r\nb"));
            var host = (IUiaTextHost)ctrl;
            host.SetSelection(2, 3); // start=mid-CRLF
            var (s, e) = host.GetSelection();
            Assert.Equal(1, s);
            Assert.Equal(3, e);
        });
}
