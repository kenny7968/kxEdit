using System.Windows;
using Xunit;
using yEdit.Accessibility;

namespace yEdit.Core.Tests.Accessibility;

public class IUiaTextHostContractStubTests
{
    // v2 interface が「範囲ベース」「位置歩き」「座標 API」を持つことをスタブ実装で契約確認する
    private sealed class StubHost : IUiaTextHost
    {
        public string GetTextRange(int start, int length) => "";

        public int TextLength => 0;

        public (int Start, int End) GetSelection() => (0, 0);

        public void SetSelection(int start, int end) { }

        public int NextChar(int offset) => offset;

        public int PrevChar(int offset) => offset;

        public int LineStartOf(int offset) => 0;

        public int LineEndNoBreakOf(int offset) => 0;

        public int LineEnd(int offset) => 0;

        public int WordStart(int offset) => offset;

        public int WordEnd(int offset) => offset;

        public int NextWordStart(int offset) => offset;

        public int PrevWordStart(int offset) => offset;

        public Rect BoundingRectangle => System.Windows.Rect.Empty;

        public double[] GetBoundingRectangles(int start, int end) => System.Array.Empty<double>();

        public int OffsetFromScreenPoint(double x, double y) => 0;

        public nint Handle => System.IntPtr.Zero;
        public bool HasFocus => false;
        public int ControlTypeId => 0;
        public string Name => "";
        public string AutomationId => "";

        public void SetFocus() { }

        public void ScrollRangeIntoView(int start, int end, bool alignToTop) { }
    }

    [Fact]
    public void Stub_ImplementsAllMembers()
    {
        IUiaTextHost host = new StubHost();
        Assert.Equal("", host.GetTextRange(0, 0));
        Assert.Equal(0, host.TextLength);
        Assert.Equal((0, 0), host.GetSelection());
        host.SetSelection(0, 0);
        Assert.Equal(0, host.NextChar(0));
        Assert.Equal(0, host.WordStart(0));
        Assert.Equal(System.Array.Empty<double>(), host.GetBoundingRectangles(0, 0));
        Assert.Equal(0, host.OffsetFromScreenPoint(0, 0));
    }
}
