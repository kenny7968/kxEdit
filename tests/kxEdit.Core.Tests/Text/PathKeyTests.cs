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

    // ===== ForNormalized(Issue #48 §3.2)=====
    // 正規化済み絶対パス専用の契約。ToLowerInvariant のみで、ファイルシステムに触れない。
    // For との弁別が本体: For は GetFullPath を通すので不達ネットワーク共有で
    // UI を約 21 秒止めうる(S-15)。ForNormalized はそれを構造的に持たない。

    [Fact]
    public void ForNormalized_lowercases_only() =>
        Assert.Equal(@"c:\temp\memo.txt", PathKey.ForNormalized(@"C:\Temp\Memo.TXT"));

    [Fact]
    public void ForNormalized_same_path_different_case_yields_same_key() =>
        Assert.Equal(
            PathKey.ForNormalized(@"C:\Temp\Memo.txt"),
            PathKey.ForNormalized(@"c:\temp\memo.TXT")
        );

    [Fact]
    public void ForNormalized_does_not_normalize_separators()
    {
        // For との**弁別**。ForNormalized は正規化しないので区切り差は別キーになる。
        // 呼出側が正規化済みパスを渡す契約(Issue #48 §3.1)を、ここで明文化して固定する。
        // このテストが無いと「ForNormalized の中で GetFullPath も呼ぶ」書き損じが
        // 全緑で通り、S-15 が丸ごと戻る。
        Assert.NotEqual(
            PathKey.ForNormalized(@"C:\Temp\a.txt"),
            PathKey.ForNormalized("C:/Temp/a.txt")
        );
        Assert.Equal(PathKey.For(@"C:\Temp\a.txt"), PathKey.For("C:/Temp/a.txt")); // 対照群: For は吸収する
    }

    [Fact]
    public void ForNormalized_does_not_collapse_relative_segments() =>
        // 同上の弁別(2 軸目)。`x\..` を畳まないことが GetFullPath 非経由の証拠になる。
        Assert.NotEqual(
            PathKey.ForNormalized(@"C:\Temp\b.txt"),
            PathKey.ForNormalized(@"C:\Temp\x\..\b.txt")
        );

    [Fact]
    public void ForNormalized_empty_returns_empty() =>
        Assert.Equal(string.Empty, PathKey.ForNormalized(""));

    [Fact]
    public void ForNormalized_does_not_touch_filesystem_for_invalid_input() =>
        // For は NUL 混入を空文字へ落とす(CSV-L-8)。ForNormalized は正規化しないので
        // 落とす対象が無く、そのまま小文字化して返す = 契約が違うことを固定する。
        Assert.Equal("a\0b", PathKey.ForNormalized("a\0b"));
}
