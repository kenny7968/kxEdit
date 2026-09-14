namespace kxEdit.App.Tests;

/// <summary>
/// 設計 2026-09-14 §3「引数の扱い」: 今回は <c>--new-instance</c> だけを解釈し、
/// 未知の引数は<b>無視する</b>(将来ファイルパスを受けるときに後方非互換にしないため)。
/// </summary>
public class CommandLineOptionsTests
{
    [Fact]
    public void Parse_WithoutArguments_DoesNotRequestNewInstance()
    {
        Assert.False(CommandLineOptions.Parse([]).NewInstance);
    }

    [Fact]
    public void Parse_RecognizesNewInstanceSwitch()
    {
        Assert.True(CommandLineOptions.Parse(["--new-instance"]).NewInstance);
    }

    [Fact]
    public void Parse_MatchesSwitchCaseInsensitively()
    {
        // Windows の CLI 慣習。大文字で打ったユーザーが黙って単一インスタンス化されない。
        Assert.True(CommandLineOptions.Parse(["--New-Instance"]).NewInstance);
    }

    [Theory]
    [InlineData("--new-instances")] // 後方に余分
    [InlineData("-new-instance")] // ハイフン 1 本
    [InlineData("--new")] // 前方一致
    [InlineData("xx--new-instance")] // 部分一致
    public void Parse_DoesNotMatchNearMissSwitches(string arg)
    {
        // StartsWith / Contains / 前方一致への変異を殺す。ここを緩めると、
        // 将来ファイルパスに "--new" を含むものが来たときに排他が黙って外れる。
        Assert.False(CommandLineOptions.Parse([arg]).NewInstance);
    }

    [Fact]
    public void Parse_IgnoresUnknownArguments()
    {
        // 将来のファイルパス引数はここへ来る。例外にしない(設計 §3)。
        var options = CommandLineOptions.Parse([@"C:\work\memo.txt", "--verbose"]);
        Assert.False(options.NewInstance);
    }

    [Fact]
    public void Parse_FindsSwitchAmongUnknownArguments()
    {
        // 「先頭だけ見る」変異を殺す。
        var options = CommandLineOptions.Parse([@"C:\work\memo.txt", "--new-instance"]);
        Assert.True(options.NewInstance);
    }

    [Fact]
    public void Parse_KeepsNewInstanceWhenUnknownArgumentFollows()
    {
        // 非既定状態(スイッチ検出済み)から始める。後続の未知引数がフラグを落とさないこと。
        // 将来 `kxEdit.exe --new-instance C:\work\memo.txt` が実際の起動形になる(設計 §6)。
        var options = CommandLineOptions.Parse(["--new-instance", @"C:\work\memo.txt"]);
        Assert.True(options.NewInstance);
    }
}
