namespace yEdit.Editor.Tests;

/// <summary>
/// 2026-07-24 CRLF atomic caret: <see cref="CaretController.SnapAndClamp"/> が
/// CRLF pair 中間位置(pos-1='\r'・pos='\n')を CR の前へスナップする挙動の検証。
/// サロゲート pair 中間の snap 挙動と対称で、キャレット/選択のすべての位置設定入り口を
/// 通る中央 seam=ここで一度スナップすれば mid-CRLF は不変条件として守られる。
/// </summary>
public class CaretControllerSnapAndClampTests
{
    private static TextSnapshot Snap(string s) => TextBuffer.FromString(s).Current;

    [Fact]
    public void SnapAndClamp_MidCrlf_SnapsToCr()
    {
        var s = Snap("a\r\nb");
        Assert.Equal(1, CaretController.SnapAndClamp(2, s));
    }

    [Fact]
    public void SnapAndClamp_AtCr_NotSnapped()
    {
        var s = Snap("a\r\nb");
        Assert.Equal(1, CaretController.SnapAndClamp(1, s));
    }

    [Fact]
    public void SnapAndClamp_AtLfPlus1_NotSnapped()
    {
        var s = Snap("a\r\nb");
        Assert.Equal(3, CaretController.SnapAndClamp(3, s));
    }

    [Fact]
    public void SnapAndClamp_LoneLf_NotSnapped()
    {
        var s = Snap("a\nb");
        Assert.Equal(1, CaretController.SnapAndClamp(1, s));
    }

    [Fact]
    public void SnapAndClamp_LoneCr_NotSnapped()
    {
        var s = Snap("a\rb");
        Assert.Equal(2, CaretController.SnapAndClamp(2, s));
    }
}
