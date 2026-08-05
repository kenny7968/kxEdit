using Xunit;
using yEdit.Core.Buffers;
using yEdit.Core.Search;

namespace yEdit.Core.Tests.Search;

/// <summary>
/// <see cref="MaterializedSearchStrategy"/> の材質化キャッシュ。
/// これは<b>新しい不変条件</b>であり、リファクタ前の src では成立しない
/// (キャッシュ自体が存在しないため)。よって「変更前で緑だったから挙動不変」の
/// 証明材料には数えない(設計書 §7.2)。
/// </summary>
public class MaterializedSearchStrategyTests
{
    private static MaterializedSearchStrategy Make(string pattern) =>
        new(new TextSearcher(new SearchOptions(pattern, MatchCase: true)));

    [Fact]
    public void SameSnapshot_reuses_materialized_text()
    {
        var snap = TextBuffer.FromString("ab ab").Current;
        var s = Make("ab");

        Assert.Equal(2, s.Count(snap));
        Assert.Equal(2, s.Count(snap)); // 2 回目はキャッシュから
        Assert.Equal(1, s.MaterializeCountForTest);
    }

    [Fact]
    public void DifferentSnapshot_rematerializes()
    {
        var s = Make("ab");
        var first = TextBuffer.FromString("ab").Current;
        var second = TextBuffer.FromString("ab ab").Current;

        Assert.Equal(1, s.Count(first));
        Assert.Equal(2, s.Count(second));
        Assert.Equal(2, s.MaterializeCountForTest);
    }

    [Fact]
    public void EditedBuffer_yields_new_snapshot_and_fresh_results()
    {
        // 本命の回帰: 編集後に同じ戦略インスタンスで検索すると新しい本文が見える
        // (参照同一性でのキャッシュ無効化が効いていることの証明)。
        var buffer = TextBuffer.FromString("ab");
        var s = Make("ab");
        Assert.Equal(1, s.Count(buffer.Current));

        buffer.Insert(2, " ab");

        Assert.Equal(2, s.Count(buffer.Current));
        Assert.Equal(2, s.MaterializeCountForTest);
    }
}
