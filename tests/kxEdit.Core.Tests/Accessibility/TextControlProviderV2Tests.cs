using System.Windows;
using System.Windows.Automation.Provider;
using kxEdit.Accessibility;
using Xunit;

namespace kxEdit.Core.Tests.Accessibility;

public class TextControlProviderV2Tests
{
    // UIA-L-3 (2026-07-20 v0.11): contract test 用の最小 IUiaTextHost。
    // GetRuntimeId は host に触らないので、他フィールドの値は不問。
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

        // Task 2: 本 stub は viewport を持たないため「文書全体が可視」で固定する。
        public (int Start, int End) GetVisibleRange() => (0, TextLength);
    }

    [Fact]
    public void GetRuntimeId_ReturnsAppendRuntimeIdContract()
    {
        // UIA-L-3 (2026-07-20 v0.11): hwnd 単位で一意化される invariant を機械固定。
        // GetRuntimeId は必ず [AutomationInteropProvider.AppendRuntimeId, 1] を返す。
        // AppendRuntimeId を UIA サーバが検出すると hwnd が append され、
        // 同一プロセス内の複数 provider インスタンスも自動的に区別される。
        var provider = new TextControlProviderV2(new StubHost());

        var runtimeId = provider.GetRuntimeId();

        Assert.Equal(new int[] { AutomationInteropProvider.AppendRuntimeId, 1 }, runtimeId);
    }
}
