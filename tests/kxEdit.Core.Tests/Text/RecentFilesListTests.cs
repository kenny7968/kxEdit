using System.Linq;
using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

public class RecentFilesListTests
{
    [Fact]
    public void New_path_goes_to_front()
    {
        var r = RecentFilesList.Add(new[] { @"C:\a.txt", @"C:\b.txt" }, @"C:\c.txt", 10);
        Assert.Equal(new[] { @"C:\c.txt", @"C:\a.txt", @"C:\b.txt" }, r);
    }

    [Fact]
    public void Existing_path_moves_to_front_without_duplicate()
    {
        var r = RecentFilesList.Add(
            new[] { @"C:\a.txt", @"C:\b.txt", @"C:\c.txt" },
            @"C:\b.txt",
            10
        );
        Assert.Equal(new[] { @"C:\b.txt", @"C:\a.txt", @"C:\c.txt" }, r);
    }

    [Fact]
    public void Caps_at_max()
    {
        var r = RecentFilesList.Add(
            new[] { @"C:\a.txt", @"C:\b.txt", @"C:\c.txt" },
            @"C:\d.txt",
            2
        );
        Assert.Equal(new[] { @"C:\d.txt", @"C:\a.txt" }, r);
    }

    [Fact]
    public void Cap_one_returns_only_new()
    {
        var r = RecentFilesList.Add(new[] { @"C:\a.txt", @"C:\b.txt" }, @"C:\c.txt", 1);
        Assert.Equal(new[] { @"C:\c.txt" }, r); // max==1 で超過しない
    }

    [Fact]
    public void Dedup_is_pathkey_normalized_case_and_separators()
    {
        // 同一ファイルの大小・区切り違いは 1 件に集約される（PathKey 正規化）。
        var r = RecentFilesList.Add(new[] { @"C:\Dir\A.TXT" }, @"c:/dir/a.txt", 10);
        Assert.Single(r);
        Assert.Equal(@"c:/dir/a.txt", r[0]); // 新規入力が先頭
    }

    [Fact]
    public void Max_zero_or_negative_returns_empty()
    {
        Assert.Empty(RecentFilesList.Add(new[] { @"C:\a.txt" }, @"C:\b.txt", 0));
        Assert.Empty(RecentFilesList.Add(new[] { @"C:\a.txt" }, @"C:\b.txt", -1));
    }

    [Fact]
    public void Empty_current_yields_single() =>
        Assert.Equal(
            new[] { @"C:\a.txt" },
            RecentFilesList.Add(System.Array.Empty<string>(), @"C:\a.txt", 10)
        );

    // CSV-L-4: settings.json 側で 10 万件の RecentFiles を投入された場合、Deserialize は O(N) を避けられない
    // (System.Text.Json 側の仕様)が、Deserialize 直後に本ヘルパを通せば後段(Add / メニュー再構築 / 各所の走査)
    // を O(MaxItems) に固定できる。null 耐性も持たせ SettingsStore.Normalize と二重防御にする。
    [Fact]
    public void Truncate_caps_to_max_items()
    {
        var source = Enumerable.Range(0, 100_000).Select(i => $@"C:\a{i}.txt");
        var r = RecentFilesList.Truncate(source);
        Assert.Equal(RecentFilesList.MaxItems, r.Count);
        Assert.Equal(@"C:\a0.txt", r[0]);
        Assert.Equal($@"C:\a{RecentFilesList.MaxItems - 1}.txt", r[^1]);
    }

    [Fact]
    public void Truncate_short_list_is_unchanged()
    {
        var source = new[] { @"C:\a.txt", @"C:\b.txt", @"C:\c.txt", @"C:\d.txt", @"C:\e.txt" };
        var r = RecentFilesList.Truncate(source);
        Assert.Equal(source, r);
    }

    [Fact]
    public void Truncate_null_returns_empty_list()
    {
        var r = RecentFilesList.Truncate(null!);
        Assert.NotNull(r);
        Assert.Empty(r);
    }

    // 定数値の pin: FileController から参照される single-source-of-truth を回帰保護する。
    [Fact]
    public void MaxItems_is_10() => Assert.Equal(10, RecentFilesList.MaxItems);
}
