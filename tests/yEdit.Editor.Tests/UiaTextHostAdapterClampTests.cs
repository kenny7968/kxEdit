// UiaTextHostAdapterClampTests.cs
// 2026-07-31 文字アクセス seam Task 5: NextChar/PrevChar を Core の TextBoundary へ
// 委譲するにあたり、Adapter 側に残る前処理(範囲外 clamp / snapshot null)の等価性を固定する。
//
// 委譲によって「歩進規則が Core と同じ」ことは構造的に保証されるが、clamp は Adapter に
// 残るため、ここが唯一の網になる。特に TextBoundary の範囲外契約は非対称
// (Next* は pos < 0 で throw・Prev* は pos > CharLength で throw)なため、
// clamp を削ると UIA クライアントからの範囲外オフセットで a11y 経路が例外になる。
using yEdit.Accessibility;

namespace yEdit.Editor.Tests;

public class UiaTextHostAdapterClampTests
{
    [Fact]
    public void NextChar_NegativeOffset_ClampsToZeroThenSteps() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(1, host.NextChar(-100)); // 0 へ clamp してから 1 歩
        });

    [Fact]
    public void NextChar_BeyondEnd_ClampsToCharLength() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(3, host.NextChar(999));
        });

    [Fact]
    public void PrevChar_NegativeOffset_ClampsToZero() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(0, host.PrevChar(-100));
        });

    [Fact]
    public void PrevChar_BeyondEnd_ClampsThenSteps() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(2, host.PrevChar(999)); // 3 へ clamp してから 1 歩戻る
        });

    [Fact]
    public void NextPrevChar_BeforeSetSource_ReturnZero() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl(); // SetSource 前 = snapshot null
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(0, host.NextChar(5));
            Assert.Equal(0, host.PrevChar(5));
        });

    [Fact]
    public void PrevChar_ClampedFromBeyondEnd_SkipsCrlfPair() =>
        Sta.Run(() =>
        {
            // clamp と CRLF atomic が両方効く経路(委譲後も維持されること)
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\r\n")); // CharLength=3
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(1, host.PrevChar(999)); // 3 へ clamp → CRLF を 1 歩で戻る
        });
}
