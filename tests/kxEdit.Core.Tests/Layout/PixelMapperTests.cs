using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Layout;

public class PixelMapperTests
{
    private static MonoCharMetrics M => new(halfWidthPx: 1, lineHeightPx: 10);

    // ---- OffsetToPx ----

    [Fact]
    public void OffsetToPx_zero_returns_zero()
    {
        Assert.Equal(0, PixelMapper.OffsetToPx("abcあ", 0, M));
    }

    [Fact]
    public void OffsetToPx_ascii_prefix()
    {
        // "abc" までの幅 = 3
        Assert.Equal(3, PixelMapper.OffsetToPx("abcあ", 3, M));
    }

    [Fact]
    public void OffsetToPx_includes_cjk()
    {
        // "abcあ" = 3 + 2 = 5
        Assert.Equal(5, PixelMapper.OffsetToPx("abcあ", 4, M));
    }

    [Fact]
    public void OffsetToPx_snaps_low_surrogate_position_forward_to_pair_start()
    {
        // charOffset=1 は low サロゲート位置 → 前方スナップで 0 に寄せる → "" の幅 = 0
        Assert.Equal(0, PixelMapper.OffsetToPx("😀", 1, M));
    }

    [Fact]
    public void OffsetToPx_at_end_returns_full_width()
    {
        // charOffset=2 = "😀".Length → 全幅 = 2
        Assert.Equal(2, PixelMapper.OffsetToPx("😀", 2, M));
    }

    [Fact]
    public void OffsetToPx_empty_segment()
    {
        Assert.Equal(0, PixelMapper.OffsetToPx("", 0, M));
    }

    // ---- PxToOffset ----

    [Fact]
    public void PxToOffset_empty_segment_returns_zero()
    {
        Assert.Equal(0, PixelMapper.PxToOffset("", 5, M));
    }

    [Fact]
    public void PxToOffset_zero_returns_zero()
    {
        Assert.Equal(0, PixelMapper.PxToOffset("abcあ", 0, M));
    }

    [Fact]
    public void PxToOffset_negative_returns_zero()
    {
        Assert.Equal(0, PixelMapper.PxToOffset("abcあ", -10, M));
    }

    [Fact]
    public void PxToOffset_maxvalue_returns_length()
    {
        var text = "abcあ";
        Assert.Equal(text.Length, PixelMapper.PxToOffset(text, int.MaxValue, M));
    }

    [Fact]
    public void PxToOffset_exact_boundary_ascii()
    {
        // "abc" 幅 3、px=2 → "ab"(2px)まで入る → 次の 'c'(1px)を足すと 3>=2 で 'c' を含めた直後 = 3? いや、2+1=3>=2 だから含める → 3
        // うーん、丁度境界ケース: 累積 1(a)+1(b)=2、次の 'b' 追加時に 1+1=2 >= 2 → 'b' 含めた直後 = 2
        // トレース: i=0 c='a' cpW=1 acc=0 0+1=1 not >= 2 → acc=1 i=1
        //          i=1 c='b' cpW=1 acc=1 1+1=2 >= 2 → return 2
        Assert.Equal(2, PixelMapper.PxToOffset("abcあ", 2, M));
    }

    [Fact]
    public void PxToOffset_mid_cjk_returns_after_cjk()
    {
        // "abcあ": acc=0/1/2/3(a,b,c)、次 'あ'(2px) → 3+2=5 >= 4 → "あ" 含めた直後 = 4
        Assert.Equal(4, PixelMapper.PxToOffset("abcあ", 4, M));
    }

    [Fact]
    public void PxToOffset_before_first_cp_returns_after_first()
    {
        // "あい": px=1 → 累積 0+2=2 >= 1 → "あ" 含めた直後 = 1
        Assert.Equal(1, PixelMapper.PxToOffset("あい", 1, M));
    }

    [Fact]
    public void PxToOffset_mid_surrogate_returns_after_pair()
    {
        // "😀" 幅 2、px=1 → 累積 0+2=2 >= 1 → サロゲートペア含めた直後 = 2(サロゲート中間には落ちない)
        Assert.Equal(2, PixelMapper.PxToOffset("😀", 1, M));
    }

    [Fact]
    public void PxToOffset_full_width_returns_length()
    {
        // "abc" 全幅 3、px=3 → segment.Length = 3
        Assert.Equal(3, PixelMapper.PxToOffset("abc", 3, M));
    }
}
