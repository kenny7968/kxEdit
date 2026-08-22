using kxEdit.Core.Layout;
using Xunit;

namespace kxEdit.Core.Tests.Layout;

public class VisualSegmentsTests
{
    [Fact]
    public void SingleSeg_Interior_ReturnsIndex0()
    {
        var segs = new[] { new WrapSegment(0, 10) };
        var (idx, seg) = VisualSegments.FindContaining(segs, 5);
        Assert.Equal(0, idx);
        Assert.Equal(new WrapSegment(0, 10), seg);
    }

    [Fact]
    public void SingleSeg_AtEnd_ReturnsLastSeg()
    {
        var segs = new[] { new WrapSegment(0, 10) };
        var (idx, seg) = VisualSegments.FindContaining(segs, 10);
        Assert.Equal(0, idx);
        Assert.Equal(new WrapSegment(0, 10), seg);
    }

    [Fact]
    public void TwoSegs_LastCharOfFirst_ReturnsFirst()
    {
        var segs = new[] { new WrapSegment(0, 5), new WrapSegment(5, 5) };
        var (idx, seg) = VisualSegments.FindContaining(segs, 4);
        Assert.Equal(0, idx);
        Assert.Equal(new WrapSegment(0, 5), seg);
    }

    [Fact]
    public void TwoSegs_BoundaryOffset_ReturnsSecond()
    {
        var segs = new[] { new WrapSegment(0, 5), new WrapSegment(5, 5) };
        var (idx, seg) = VisualSegments.FindContaining(segs, 5);
        Assert.Equal(1, idx);
        Assert.Equal(new WrapSegment(5, 5), seg);
    }

    [Fact]
    public void TwoSegs_InteriorOfSecond_ReturnsSecond()
    {
        var segs = new[] { new WrapSegment(0, 5), new WrapSegment(5, 5) };
        var (idx, seg) = VisualSegments.FindContaining(segs, 8);
        Assert.Equal(1, idx);
        Assert.Equal(new WrapSegment(5, 5), seg);
    }

    [Fact]
    public void TwoSegs_AtLineEnd_ReturnsLast()
    {
        var segs = new[] { new WrapSegment(0, 5), new WrapSegment(5, 5) };
        var (idx, seg) = VisualSegments.FindContaining(segs, 10);
        Assert.Equal(1, idx);
        Assert.Equal(new WrapSegment(5, 5), seg);
    }

    [Fact]
    public void EmptySegs_Throws()
    {
        var segs = System.Array.Empty<WrapSegment>();
        Assert.Throws<System.ArgumentException>(() => VisualSegments.FindContaining(segs, 0));
    }

    // ===== ClampLandingOffset(設計書 I-1)=====

    [Fact]
    public void ClampLandingOffset_FinalSegment_KeepsSegEnd()
    {
        // 最終セグメントの segEnd は論理行の行末=正当なキャレット位置なので触らない。
        Assert.Equal(4, VisualSegments.ClampLandingOffset("abcd", 4, isFinalSegment: true));
    }

    [Fact]
    public void ClampLandingOffset_NonFinalSegment_ClampsToLastCodePointStart()
    {
        Assert.Equal(3, VisualSegments.ClampLandingOffset("abcd", 4, isFinalSegment: false));
    }

    [Fact]
    public void ClampLandingOffset_NonFinalSegment_Interior_Unchanged()
    {
        Assert.Equal(2, VisualSegments.ClampLandingOffset("abcd", 2, isFinalSegment: false));
    }

    [Fact]
    public void ClampLandingOffset_NonFinalSegment_SurrogateTail_DoesNotSplitPair()
    {
        // "a" + U+1F600(サロゲートペア)= 3 code unit。末尾から 1 引くと low サロゲート位置に
        // なるので、ペア先頭(=1)まで戻ること。
        Assert.Equal(1, VisualSegments.ClampLandingOffset("a😀", 3, isFinalSegment: false));
    }

    [Fact]
    public void ClampLandingOffset_EmptySegment_ReturnsZero()
    {
        Assert.Equal(0, VisualSegments.ClampLandingOffset("", 0, isFinalSegment: false));
    }
}
