using kxEdit.Accessibility;
using kxEdit.Core.Buffers;
using kxEdit.Editor;

namespace kxEdit.Editor.Tests;

/// <summary>
/// P8 Minor-5: 論理行 segs 単一エントリキャッシュの契約テスト。
/// UIA の LineStartOf / LineEndNoBreakOf / LineEnd が同一 (snap, logicalLine, wrap) を
/// 連続照会するホットパスで、LineLayout.Wrap の再アロケーションが 1 回で済むことを
/// TestHook_LastLineSegsHit/MissCount で観測する。
/// </summary>
public class EditorControlCacheTests
{
    private static (Form f, EditorControl c) MakeControl(string text, int wrap)
    {
        var f = new HostForm();
        var c = new EditorControl { WrapColumns = wrap };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    [Fact]
    public void LastLineSegs_HitsAcrossThreeUiaLineCalls() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("Hello, world!", 20);
            using (f)
            using (c)
            {
                c.TestHook_ResetLastLineSegsCounters();
                var host = (IUiaTextHost)c;
                _ = host.LineStartOf(5);
                _ = host.LineEndNoBreakOf(5);
                _ = host.LineEnd(5);
                Assert.Equal(1, c.TestHook_LastLineSegsMissCount);
                Assert.Equal(2, c.TestHook_LastLineSegsHitCount);
            }
        });

    [Fact]
    public void LastLineSegs_InvalidatesOnEdit() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("Hello, world!", 20);
            using (f)
            using (c)
            {
                var host = (IUiaTextHost)c;
                _ = host.LineStartOf(5);
                _ = host.LineStartOf(5);
                c.TestHook_ResetLastLineSegsCounters();
                c.ReplaceCharRange(0, 0, "X");
                _ = host.LineStartOf(5);
                Assert.Equal(1, c.TestHook_LastLineSegsMissCount);
                Assert.Equal(0, c.TestHook_LastLineSegsHitCount);
            }
        });

    [Fact]
    public void LastLineSegs_InvalidatesOnWrapChange() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("Hello, world!", 20);
            using (f)
            using (c)
            {
                var host = (IUiaTextHost)c;
                _ = host.LineStartOf(5);
                _ = host.LineStartOf(5);
                c.TestHook_ResetLastLineSegsCounters();
                c.WrapColumns = 30;
                _ = host.LineStartOf(5);
                Assert.Equal(1, c.TestHook_LastLineSegsMissCount);
                Assert.Equal(0, c.TestHook_LastLineSegsHitCount);
            }
        });
}
