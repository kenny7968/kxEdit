using kxEdit.Core.Editing;
using Xunit;

namespace kxEdit.Core.Tests.Editing;

public class ImeCompositionStateTests
{
    [Fact]
    public void Empty_HasNoActiveComposition()
    {
        Assert.False(ImeCompositionState.Empty.IsActive);
        Assert.Equal("", ImeCompositionState.Empty.Text);
    }

    [Fact]
    public void WithText_MarksActive()
    {
        var s = new ImeCompositionState(
            Start: 5,
            Text: "あい",
            CursorPos: 2,
            Attrs: [0, 0],
            Clauses: [0, 2]
        );
        Assert.True(s.IsActive);
        Assert.Equal(5, s.Start);
        Assert.Equal(2, s.CursorPos);
    }

    // Attrs パース: バイト列をそのまま byte[] へコピーする素朴ケース(GCS_COMPATTR が返すのは
    // 未確定文字列 UTF-16 code unit 数分の 1 バイト属性配列)。
    [Fact]
    public void ParseAttrs_ReturnsCopyOfSourceBytes()
    {
        var src = new byte[] { 0x00, 0x00, 0x01, 0x01, 0x00 };
        var parsed = ImeCompositionState.ParseAttrs(src);
        Assert.Equal(src, parsed);
    }

    // Clauses パース: DWORD(int32) little-endian の配列。
    // GCS_COMPCLAUSE が返すのは「節開始位置の連続 + 末尾の全長」形式。
    [Fact]
    public void ParseClauses_DecodesInt32LittleEndian()
    {
        // 3 clauses: [0, 2, 4] → 0x00,0x00,0x00,0x00, 0x02,0x00,0x00,0x00, 0x04,0x00,0x00,0x00
        var bytes = new byte[]
        {
            0x00,
            0x00,
            0x00,
            0x00,
            0x02,
            0x00,
            0x00,
            0x00,
            0x04,
            0x00,
            0x00,
            0x00,
        };
        var parsed = ImeCompositionState.ParseClauses(bytes);
        Assert.Equal(new[] { 0, 2, 4 }, parsed);
    }

    [Fact]
    public void ParseAttrs_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(ImeCompositionState.ParseAttrs([]));
        Assert.Empty(ImeCompositionState.ParseClauses([]));
    }

    // サロゲート境界: CursorPos が low surrogate 位置を指したら high 側にスナップ。
    // "あ😀い" = 'あ' (1) + '😀' surrogate pair (2) + 'い' (1) = 4 UTF-16 code units
    // UTF-16 layout: text[0]='あ', text[1]=high surrogate, text[2]=low surrogate, text[3]='い'
    [Fact]
    public void SnapCursorPos_MovesFromLowSurrogateToHigh()
    {
        string text = "あ😀い"; // Length = 4
        // CursorPos = 2 (text[2]=low surrogate, text[1]=high surrogate) → 1 にスナップ
        // CursorPos = 3 (text[3]='い' = BMP) → 保持
        Assert.Equal(1, ImeCompositionState.SnapCursorPos(text, 2)); // low → high 側 (=1)
        Assert.Equal(3, ImeCompositionState.SnapCursorPos(text, 3)); // 'い' — 保持
        Assert.Equal(0, ImeCompositionState.SnapCursorPos(text, 0));
        Assert.Equal(4, ImeCompositionState.SnapCursorPos(text, 4)); // 末尾
        Assert.Equal(1, ImeCompositionState.SnapCursorPos(text, 1)); // text[1]=high — 保持
    }

    // 境界値: cursor が [0, Length] を外れたらクランプする。
    // 2026-07-31: SnapCursorPos を TextBoundary.SnapToCodePointStart へ委譲する前に、
    // 既存挙動(Math.Clamp)を境界まで固定して等価性を検証可能にする。
    [Fact]
    public void SnapCursorPos_OutOfRangeCursor_IsClamped()
    {
        string text = "あ😀い"; // Length = 4
        Assert.Equal(0, ImeCompositionState.SnapCursorPos(text, -1));
        Assert.Equal(0, ImeCompositionState.SnapCursorPos(text, int.MinValue));
        Assert.Equal(4, ImeCompositionState.SnapCursorPos(text, 5));
        Assert.Equal(4, ImeCompositionState.SnapCursorPos(text, int.MaxValue));
    }

    // 空文字列は Length=0 なので、どの cursor でも 0 に落ちる(縮退ケース)。
    [Fact]
    public void SnapCursorPos_EmptyText_ReturnsZero()
    {
        Assert.Equal(0, ImeCompositionState.SnapCursorPos("", 0));
        Assert.Equal(0, ImeCompositionState.SnapCursorPos("", 3));
        Assert.Equal(0, ImeCompositionState.SnapCursorPos("", -2));
    }

    // null は明示ガードで ArgumentNullException にする。TextBoundary への委譲で AsSpan() の
    // 暗黙変換に流すと null が空 span 化して静かに 0 を返すため、そちらへ倒さないことを固定する。
    [Fact]
    public void SnapCursorPos_NullText_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ImeCompositionState.SnapCursorPos(null!, 1));
    }

    // Clauses のバイト長不整合(4 の倍数でない): 切り捨てで tolerate する
    [Fact]
    public void ParseClauses_IgnoresTrailingPartialDword()
    {
        var bytes = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x02, 0x00 }; // 6 bytes = 1 full + 2 半端
        Assert.Equal(new[] { 0 }, ImeCompositionState.ParseClauses(bytes));
    }
}
