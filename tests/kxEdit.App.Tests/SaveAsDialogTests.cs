namespace kxEdit.App.Tests;

/// <summary>
/// <see cref="SaveAsDialog.FilterIndexFor(string?)"/> が参照ボタンの SaveFileDialog の
/// 初期「ファイルの種類」(1 始まり)を元パスの拡張子から正しく決めることを固定する。
/// Filter の並びは txt(1) / md(2) / csv(3) / すべて(4)。
/// 実 UI(Form/SaveFileDialog)には触れずヘルパの戻り値のみを検証する。
/// </summary>
public class SaveAsDialogTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\work\")]
    public void FilterIndexFor_NoFileName_ReturnsText(string? path)
    {
        Assert.Equal(1, SaveAsDialog.FilterIndexFor(path));
    }

    /// <summary>
    /// 戻り値の index が <see cref="SaveAsDialog.SaveFilter"/> の該当区画を指すことを固定する
    /// (Filter の並べ替えで index とずれたら落ちる)。区画 n の pattern は Split('|')[2n-1]。
    /// </summary>
    [Theory]
    [InlineData(@"C:\work\a.txt", "*.txt")]
    [InlineData(@"C:\work\a.md", "*.md")]
    [InlineData(@"C:\work\a.csv", "*.csv")]
    [InlineData(@"C:\work\a.log", "*.*")]
    public void FilterIndexFor_PointsToMatchingSaveFilterPattern(string path, string pattern)
    {
        var parts = SaveAsDialog.SaveFilter.Split('|');
        Assert.Equal(pattern, parts[(SaveAsDialog.FilterIndexFor(path) * 2) - 1]);
    }

    [Theory]
    [InlineData(@"C:\work\a.txt", 1)]
    [InlineData(@"C:\work\a.md", 2)]
    [InlineData(@"C:\work\a.csv", 3)]
    public void FilterIndexFor_KnownExtension_ReturnsMatchingFilter(string path, int expected)
    {
        Assert.Equal(expected, SaveAsDialog.FilterIndexFor(path));
    }

    [Theory]
    [InlineData(@"C:\work\A.TXT", 1)]
    [InlineData(@"C:\work\A.Md", 2)]
    [InlineData(@"C:\work\A.CSV", 3)]
    public void FilterIndexFor_KnownExtensionUpperCase_ReturnsMatchingFilter(
        string path,
        int expected
    )
    {
        Assert.Equal(expected, SaveAsDialog.FilterIndexFor(path));
    }

    [Theory]
    [InlineData(@"C:\work\a.log")]
    [InlineData(@"C:\work\a.json")]
    [InlineData(@"C:\work\a.txt.bak")]
    [InlineData("a.markdown")]
    public void FilterIndexFor_OtherExtension_ReturnsAllFiles(string path)
    {
        Assert.Equal(4, SaveAsDialog.FilterIndexFor(path));
    }

    [Theory]
    [InlineData(@"C:\work\Makefile")]
    [InlineData(@"C:\work.d\README")]
    public void FilterIndexFor_NoExtension_ReturnsAllFiles(string path)
    {
        // *.txt のままだと AddExtension で .txt が付与されるため「すべて」にする。
        // "C:\work.d\README" はディレクトリ側のドットを拡張子と誤認しないことの確認。
        Assert.Equal(4, SaveAsDialog.FilterIndexFor(path));
    }
}
