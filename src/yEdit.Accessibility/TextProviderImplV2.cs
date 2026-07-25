using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;

namespace yEdit.Accessibility;

/// <summary>UIA TextPattern 本体(v2・ITextProvider)。範囲の生成と現在選択の提供を担う。</summary>
internal sealed class TextProviderImplV2 : ITextProvider
{
    public IUiaTextHost Host { get; }
    public IRawElementProviderSimple RootProvider { get; }

    public TextProviderImplV2(IUiaTextHost host, IRawElementProviderSimple root)
    {
        Host = host;
        RootProvider = root;
    }

    public ITextRangeProvider[] GetSelection()
    {
        var (s, e) = Host.GetSelection();
        return new ITextRangeProvider[] { new TextRangeProviderV2(this, s, e) };
    }

    /// <summary>
    /// 現在ビューポートに見えている範囲を 1 本返す(プレーンテキストエディタなので連続 1 範囲)。
    /// 旧実装は常に文書全体を返していたが、それは <c>GetBoundingRectangles</c> が画面外で
    /// 空配列を返す挙動と矛盾していた。典拠:
    /// docs/plans/2026-07-25-uia-scrollintoview-design.md §5.2。
    /// </summary>
    public ITextRangeProvider[] GetVisibleRanges()
    {
        var (s, e) = Host.GetVisibleRange();
        return new ITextRangeProvider[] { new TextRangeProviderV2(this, s, e) };
    }

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) =>
        new TextRangeProviderV2(this, 0, 0);

    /// <summary>スクリーン座標直下の縮退範囲(host.OffsetFromScreenPoint 委譲・本実装)。</summary>
    public ITextRangeProvider RangeFromPoint(Point screenLocation)
    {
        int pos = Host.OffsetFromScreenPoint(screenLocation.X, screenLocation.Y);
        return new TextRangeProviderV2(this, pos, pos);
    }

    public ITextRangeProvider DocumentRange => new TextRangeProviderV2(this, 0, Host.TextLength);

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;
}
