using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

/// <summary>
/// <see cref="VersionText.FromInformationalVersion"/> の契約。
/// .NET 8 以降の <c>AssemblyInformationalVersion</c> は commit SHA が付くため、
/// 表示前に落とす必要がある。
/// </summary>
public class VersionTextTests
{
    [Fact]
    public void CommitShaSuffix_IsStripped()
    {
        Assert.Equal(
            "0.2.0",
            VersionText.FromInformationalVersion("0.2.0+03ffae3ca8ec50b1acf916f30f5002042d8ec604")
        );
    }

    [Fact]
    public void PlainVersion_IsReturnedAsIs()
    {
        Assert.Equal("0.2.0", VersionText.FromInformationalVersion("0.2.0"));
    }

    [Fact]
    public void PrereleaseIdentifier_IsPreserved()
    {
        // プレリリース識別子は '+' より前にあるので残る。
        Assert.Equal("0.3.0-rc.1", VersionText.FromInformationalVersion("0.3.0-rc.1+deadbeefcafe"));
    }

    [Fact]
    public void OnlyFirstPlus_Splits()
    {
        Assert.Equal("1.0.0", VersionText.FromInformationalVersion("1.0.0+a+b"));
    }

    [Fact]
    public void LeadingPlus_YieldsEmpty()
    {
        Assert.Equal(string.Empty, VersionText.FromInformationalVersion("+abc123"));
    }

    [Fact]
    public void SurroundingWhitespace_IsTrimmed()
    {
        Assert.Equal("0.2.0", VersionText.FromInformationalVersion("  0.2.0+sha  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void MissingOrBlank_YieldsEmpty(string? input)
    {
        Assert.Equal(string.Empty, VersionText.FromInformationalVersion(input));
    }
}
