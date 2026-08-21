using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

public class LineEndingDetectorTests
{
    [Theory]
    [InlineData("a\r\nb", LineEnding.Crlf)]
    [InlineData("a\nb", LineEnding.Lf)]
    [InlineData("a\rb", LineEnding.Cr)]
    public void Detects_dominant_line_ending(string text, LineEnding expected) =>
        Assert.Equal(expected, LineEndingDetector.Detect(text));

    [Fact]
    public void Mixed_returns_dominant() =>
        Assert.Equal(LineEnding.Lf, LineEndingDetector.Detect("a\nb\nc\r\nd"));

    [Fact]
    public void No_newline_returns_platform_default() =>
        Assert.Equal(LineEnding.Crlf, LineEndingDetector.Detect("abc"));
}
