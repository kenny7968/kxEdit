using kxEdit.Core.Csv;
using Xunit;

namespace kxEdit.Core.Tests.Csv;

public class CsvFileTests
{
    [Theory]
    [InlineData("data.csv", true)]
    [InlineData("data.CSV", true)]
    [InlineData(@"C:\a\b\table.Csv", true)]
    [InlineData("data.txt", false)]
    [InlineData("data", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsCsvPath_detects_csv_extension(string? path, bool expected) =>
        Assert.Equal(expected, CsvFile.IsCsvPath(path));
}
