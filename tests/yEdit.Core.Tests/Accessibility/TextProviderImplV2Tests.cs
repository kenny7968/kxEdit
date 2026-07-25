using System.Windows.Automation;
using System.Windows.Automation.Provider;
using Xunit;
using yEdit.Accessibility;

namespace yEdit.Core.Tests.Accessibility;

public class TextProviderImplV2Tests
{
    private sealed class Host : IUiaTextHost
    {
        public int _selS,
            _selE;

        public string GetTextRange(int s, int l) => "";

        public int TextLength => 100;

        public (int Start, int End) GetSelection() => (_selS, _selE);

        public void SetSelection(int s, int e)
        {
            _selS = s;
            _selE = e;
        }

        public int NextChar(int o) => o + 1;

        public int PrevChar(int o) => o - 1;

        public int LineStartOf(int o) => 0;

        public int LineEndNoBreakOf(int o) => 100;

        public int LineEnd(int o) => 100;

        public int WordStart(int o) => 0;

        public int WordEnd(int o) => 100;

        public int NextWordStart(int o) => o;

        public int PrevWordStart(int o) => o;

        public System.Windows.Rect BoundingRectangle => new System.Windows.Rect(0, 0, 200, 100);

        public double[] GetBoundingRectangles(int s, int e) => System.Array.Empty<double>();

        public int OffsetFromScreenPoint(double x, double y) => 42; // 定数=呼ばれたことがわかる

        public nint Handle => System.IntPtr.Zero;
        public bool HasFocus => true;
        public int ControlTypeId => ControlType.Document.Id;
        public string Name => "本文";
        public string AutomationId => "editor";

        public void SetFocus() { }

        public void ScrollRangeIntoView(int start, int end, bool alignToTop) { }

        // Task 2: GetVisibleRanges の委譲検証用。既定値は「文書全体ではない」値にしておく
        // (TextLength == 100 なので (0, 100) は文字どおり全体可視=旧実装と同値になり、
        //  将来「既定のまま呼ぶテスト」が書かれたときに旧挙動と区別できなくなる)。
        // 委譲テストは引き続き非既定値を明示的に設定する。
        public (int Start, int End) VisibleRange { get; set; } = (3, 42);

        public (int Start, int End) GetVisibleRange() => VisibleRange;
    }

    [Fact]
    public void GetSelection_ReturnsHostSelectionAsSingleRange()
    {
        var h = new Host { _selS = 10, _selE = 20 };
        var root = new TextControlProviderV2(h);
        var pi = new TextProviderImplV2(h, root);
        var sel = pi.GetSelection();
        Assert.Single(sel);
        Assert.Equal("", ((TextRangeProviderV2)sel[0]).GetText(0));
    }

    [Fact]
    public void RangeFromPoint_UsesHostOffsetFromScreenPoint()
    {
        var h = new Host();
        var root = new TextControlProviderV2(h);
        var pi = new TextProviderImplV2(h, root);
        var r = pi.RangeFromPoint(new System.Windows.Point(50, 30)) as TextRangeProviderV2;
        Assert.NotNull(r);
        // OffsetFromScreenPoint が 42 を返す → [42, 42) の縮退範囲
        int startCmp = r!.CompareEndpoints(
            System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,
            r,
            System.Windows.Automation.Text.TextPatternRangeEndpoint.End
        );
        Assert.Equal(0, startCmp);
    }

    [Fact]
    public void DocumentRange_ReturnsFullText()
    {
        var h = new Host();
        var root = new TextControlProviderV2(h);
        var pi = new TextProviderImplV2(h, root);
        var r = (TextRangeProviderV2)pi.DocumentRange;
        Assert.Equal(
            0,
            r.CompareEndpoints(
                System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,
                new TextRangeProviderV2(pi, 0, 0),
                System.Windows.Automation.Text.TextPatternRangeEndpoint.Start
            )
        );
    }

    [Fact]
    public void TextControlProviderV2_ReportsDocumentControlType()
    {
        var h = new Host();
        var root = new TextControlProviderV2(h);
        Assert.Equal(
            ControlType.Document.Id,
            root.GetPropertyValue(AutomationElementIdentifiers.ControlTypeProperty.Id)
        );
        Assert.Equal("本文", root.GetPropertyValue(AutomationElementIdentifiers.NameProperty.Id));
        Assert.Equal(
            "editor",
            root.GetPropertyValue(AutomationElementIdentifiers.AutomationIdProperty.Id)
        );
    }

    [Fact]
    public void GetVisibleRanges_UsesHostVisibleRange()
    {
        // 旧実装は常に (0, TextLength) = (0, 100) を返していた。
        // 非既定の可視域を与えて、旧実装と区別できる形で固定する。
        var h = new Host { VisibleRange = (10, 25) };
        var root = new TextControlProviderV2(h);
        var pi = new TextProviderImplV2(h, root);

        var ranges = pi.GetVisibleRanges();

        Assert.Single(ranges);
        var r = (TextRangeProviderV2)ranges[0];
        var probe = new TextRangeProviderV2(pi, 10, 25);
        Assert.Equal(
            0,
            r.CompareEndpoints(
                System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,
                probe,
                System.Windows.Automation.Text.TextPatternRangeEndpoint.Start
            )
        );
        Assert.Equal(
            0,
            r.CompareEndpoints(
                System.Windows.Automation.Text.TextPatternRangeEndpoint.End,
                probe,
                System.Windows.Automation.Text.TextPatternRangeEndpoint.End
            )
        );
    }
}
