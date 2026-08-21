// WinImeContextSmokeTests.cs
// Phase 3 (Task 3a): WinImeContext は本番 P/Invoke を叩くため実 IME が必要だが、
// hwnd == 0 (IME 無効相当) のときの no-op / null 返却契約と Dispose 冪等性は smoke で確認できる。
namespace kxEdit.Editor.Tests;

public class WinImeContextSmokeTests
{
    [Fact]
    public void WinImeContext_ZeroHwnd_GetCompositionString_ReturnsNull()
    {
        using var ctx = new WinImeContext(IntPtr.Zero);
        // himc == 0 で全 Get* は null / 0 / [] を返す (旧 EditorControl.ReadIme* の
        // hIMC ガードと等価挙動)
        Assert.Null(ctx.GetCompositionString(NativeMethods.GCS_COMPSTR));
        Assert.Null(ctx.GetCompositionBytes(NativeMethods.GCS_COMPATTR));
        Assert.Equal(0, ctx.GetCompositionInt(NativeMethods.GCS_CURSORPOS));
    }

    [Fact]
    public void WinImeContext_Dispose_IsIdempotent()
    {
        var ctx = new WinImeContext(IntPtr.Zero);
        ctx.Dispose();
        // 2 回目 Dispose でも例外なし (旧 finally 内 ImmReleaseContext と対称)
        Assert.Null(Record.Exception(() => ctx.Dispose()));
    }
}
