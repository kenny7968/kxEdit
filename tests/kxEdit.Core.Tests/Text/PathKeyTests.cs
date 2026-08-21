using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

public class PathKeyTests
{
    [Fact]
    public void Same_path_different_case_yields_same_key() =>
        Assert.Equal(PathKey.For(@"C:\Temp\Memo.txt"), PathKey.For(@"c:\temp\memo.TXT"));

    [Fact]
    public void Forward_and_back_slashes_normalize_equal() =>
        Assert.Equal(PathKey.For(@"C:\Temp\a\b.txt"), PathKey.For("C:/Temp/a/b.txt"));

    [Fact]
    public void Relative_segments_collapse() =>
        Assert.Equal(PathKey.For(@"C:\Temp\b.txt"), PathKey.For(@"C:\Temp\x\..\b.txt"));

    [Fact]
    public void Empty_returns_empty() => Assert.Equal(string.Empty, PathKey.For(""));

    // CSV-L-8 (v0.11): 正規化不能パス（例: 埋め込み NUL 文字）は攻撃者が
    // 生パスを dedup キーに紛れ込ませるベクタなので、空文字に落として
    // 「invalid はまとめて 1 件」に集約する。
    [Fact]
    public void Invalid_path_returns_empty() => Assert.Equal(string.Empty, PathKey.For("a\0b"));
}
