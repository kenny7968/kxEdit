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
}
