using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

public class LineEndingExtensionsTests
{
    [Theory]
    [InlineData(LineEnding.Crlf, "\r\n")]
    [InlineData(LineEnding.Lf, "\n")]
    [InlineData(LineEnding.Cr, "\r")]
    public void ToEolString_returns_actual_newline(LineEnding eol, string expected) =>
        Assert.Equal(expected, eol.ToEolString());

    [Theory]
    [InlineData(LineEnding.Crlf, "CRLF")]
    [InlineData(LineEnding.Lf, "LF")]
    [InlineData(LineEnding.Cr, "CR")]
    public void ToDisplayString_returns_short_name(LineEnding eol, string expected) =>
        Assert.Equal(expected, eol.ToDisplayString());
}
