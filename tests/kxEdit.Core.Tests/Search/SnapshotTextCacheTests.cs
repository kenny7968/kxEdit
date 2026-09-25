using kxEdit.Core.Buffers;
using kxEdit.Core.Search;
using Xunit;

namespace kxEdit.Core.Tests.Search;

/// <summary>
/// <see cref="SnapshotTextCache"/> の単体テスト(所有者であるキャッシュを直接叩く)。
/// 2026-09-25 フェーズ 5 P-5(a)。
/// </summary>
public class SnapshotTextCacheTests
{
    [Fact]
    public void Reuses_text_for_same_snapshot()
    {
        var snap = TextBuffer.FromString("ab ab").Current;
        var cache = new SnapshotTextCache();

        _ = cache.TextOf(snap);
        _ = cache.TextOf(snap);

        Assert.Equal(1, cache.MaterializeCountForTest);
    }

    [Fact]
    public void Returns_same_text_as_GetText()
    {
        var snap = TextBuffer.FromString("ab\r\ncd あ😀").Current;
        var cache = new SnapshotTextCache();

        Assert.Equal(snap.GetText(0, snap.CharLength), cache.TextOf(snap));
    }

    [Fact]
    public void Holds_at_most_one_snapshot()
    {
        // A → B → A: 1 枠なので B で A を追い出し、A に戻ると読み直す(辞書実装なら 2 回で済む)。
        var a = TextBuffer.FromString("ab").Current;
        var b = TextBuffer.FromString("ab ab").Current;
        var cache = new SnapshotTextCache();

        Assert.Equal("ab", cache.TextOf(a));
        Assert.Equal("ab ab", cache.TextOf(b));
        Assert.Equal("ab", cache.TextOf(a));

        Assert.Equal(3, cache.MaterializeCountForTest);
    }
}
