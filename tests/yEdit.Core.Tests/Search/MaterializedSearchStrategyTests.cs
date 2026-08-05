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

    // 以下 2 件は「6 API が 1 つのスロットを共有する」を固定する網。上の 4 件はすべて Count 経由でしか
    // MaterializeCountForTest を観測していないため、Count 以外の 5 API については
    // 「キャッシュを使っているか」が一切固定されていなかった。
    //
    // 本番の実利益はまさにそこ: SearchController.Find は 1 回の検索で同じ snapshot に
    // FindNext + Locate を呼び、ReplaceOne は ReplacementAt / FindNext / Locate を最大 5 回呼ぶ。
    // 共有が壊れても結果値は正しいまま利益だけが消えるので、結果を見る網では検出できない。
    //
    // 2 件に分けてあるのは、片方だけでは殺せない変異があるため(分割の根拠は各テストのコメント)。

    [Fact]
    public void All_operations_share_one_materialization_per_snapshot()
    {
        // 「API ごとに別スロットを持つ」変異を殺す網。1 スナップショットへ 6 API を全部叩いて
        // 合計が 1 であることを見る(API ごとにキャッシュを分けた実装なら 6 になる)。
        // これは合計しか見ないので、後続テストが受け持つ「迂回」は殺せない(下記)。
        var snap = TextBuffer.FromString("ab ab").Current;
        var s = Make("ab");

        var hit = s.FindNext(snap, 0)!.Value;
        s.FindPrev(snap, snap.CharLength);
        s.Count(snap);
        s.Locate(snap, hit);
        s.ReplacementAt(snap, hit, "X");
        s.ReplaceInRange(snap, 0, snap.CharLength, "X");

        Assert.Equal(1, s.MaterializeCountForTest); // 6 API が 1 スロットを共有する
    }

    [Fact]
    public void Every_operation_goes_through_the_shared_cache()
    {
        // 「どれか 1 API だけが TextOf を迂回する」変異を殺す網。例えば FindNext を
        //   _inner.FindNext(snap.GetText(0, snap.CharLength), from)   // TextOf を迂回
        // と変異させても、上の 5 件は全部緑のまま生存する(実測済み)。迂回は材質化回数を
        // 増やさないので、合計を見る assert では残り 5 API の 1 回で 1 になってしまうため。
        // 迂回を観測できる唯一の位置は「新品の戦略に対する最初の 1 呼び出し」=そこで
        // 材質化回数が 0 のままなら、その API はキャッシュ経路に乗っていない。
        // よって API ごとに戦略を作り直して 1 呼び出しずつ観測する。
        var snap = TextBuffer.FromString("ab ab").Current;
        var hit = Make("ab").FindNext(snap, 0)!.Value;

        AssertMaterializesOnce(nameof(MaterializedSearchStrategy.Count), s => s.Count(snap));
        AssertMaterializesOnce(
            nameof(MaterializedSearchStrategy.FindNext),
            s => s.FindNext(snap, 0)
        );
        AssertMaterializesOnce(
            nameof(MaterializedSearchStrategy.FindPrev),
            s => s.FindPrev(snap, snap.CharLength)
        );
        AssertMaterializesOnce(nameof(MaterializedSearchStrategy.Locate), s => s.Locate(snap, hit));
        AssertMaterializesOnce(
            nameof(MaterializedSearchStrategy.ReplacementAt),
            s => s.ReplacementAt(snap, hit, "X")
        );
        AssertMaterializesOnce(
            nameof(MaterializedSearchStrategy.ReplaceInRange),
            s => s.ReplaceInRange(snap, 0, snap.CharLength, "X")
        );

        static void AssertMaterializesOnce(string api, Action<MaterializedSearchStrategy> call)
        {
            var fresh = Make("ab");
            call(fresh);
            Assert.True(
                fresh.MaterializeCountForTest == 1,
                $"{api} がキャッシュ経路(TextOf)を通っていない: 材質化回数={fresh.MaterializeCountForTest}"
            );
        }
    }
}
