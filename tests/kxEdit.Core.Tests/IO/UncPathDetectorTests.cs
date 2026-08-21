using kxEdit.Core.IO;

namespace kxEdit.Core.Tests.IO;

public class UncPathDetectorTests
{
    [Theory]
    [InlineData(@"\\server\share\file.txt", true)]
    [InlineData(@"\\?\UNC\server\share\file.txt", true)]
    [InlineData(@"\\.\PhysicalDrive0", true)]
    [InlineData(@"C:\example\notes.txt", false)]
    [InlineData(@"C:\dev\project\readme.md", false)]
    [InlineData(@".\relative.txt", false)]
    [InlineData(@"relative.txt", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUnc(string? path, bool expected)
    {
        Assert.Equal(expected, UncPathDetector.IsUnc(path ?? ""));
    }
}
