using kxEdit.App;
using Xunit;

namespace kxEdit.App.Tests;

/// <summary>
/// バージョン情報ダイアログの表示文字列の契約。
/// <para>
/// バージョン番号そのものは assert しない。`0.2.0` を pin すると
/// バージョンを上げるたびにテストが落ちるため、形だけを固定する。
/// </para>
/// </summary>
public class AppVersionTests
{
    [Fact]
    public void Compose_AppendsVersionAfterName()
    {
        Assert.Equal("kxEdit v0.2.0", AppVersion.Compose("0.2.0+deadbeef"));
    }

    [Fact]
    public void Compose_WithoutShaSuffix_Works()
    {
        Assert.Equal("kxEdit v1.2.3", AppVersion.Compose("1.2.3"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+onlysha")]
    public void Compose_WhenVersionUnavailable_ShowsNameOnly(string? informationalVersion)
    {
        // バージョンが取れないときに "kxEdit v" と末尾が切れた文字列を出さない。
        Assert.Equal("kxEdit", AppVersion.Compose(informationalVersion));
    }

    [Fact]
    public void DisplayText_ReadsRealAssemblyAttribute()
    {
        string text = AppVersion.DisplayText;

        // 実アセンブリから読めていること = 属性が剥がれていない、かつ SHA が
        // 落ちていることを、バージョン番号を pin せずに確かめる。
        Assert.StartsWith("kxEdit v", text, StringComparison.Ordinal);

        string version = text["kxEdit v".Length..];
        Assert.NotEmpty(version);
        Assert.DoesNotContain('+', version);
    }
}
