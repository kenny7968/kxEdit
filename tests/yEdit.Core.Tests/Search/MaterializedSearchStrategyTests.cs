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

    [Fact]
    public void Cache_holds_at_most_one_snapshot()
    {
        // 「保持は常に最大 1 本」を固定する網。A → B → A と叩くと、単一スロット実装では
        // B の材質化で A のエントリが押し出されるので、A に戻った 3 回目で再材質化が起きる=3。
        // Dictionary<TextSnapshot, string> のような多スロット実装だと A が残り 3 回目が
        // キャッシュヒットになるので 2 になる。つまりこの assert が 3 であることだけが
        // 「複数本を抱え込んでいない(= 古い本文が居座らない)」を区別する。
        var s = Make("ab");
        var a = TextBuffer.FromString("ab").Current;
        var b = TextBuffer.FromString("ab ab").Current;

        Assert.Equal(1, s.Count(a));
        Assert.Equal(2, s.Count(b));
        Assert.Equal(1, s.Count(a)); // 押し出された A を読み直す(結果自体は不変)

        Assert.Equal(3, s.MaterializeCountForTest);
    }
}
