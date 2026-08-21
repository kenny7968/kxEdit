using kxEdit.Core.Buffers;
using kxEdit.Core.Search;
using Xunit;

namespace kxEdit.Core.Tests.Search;

/// <summary>
/// P7 I-5 挙動凍結: <see cref="SnapshotSearcher"/> 閾値超経路(行単位 regex)における
/// アンカー(<c>^</c> / <c>$</c>)の「行内 anchor」挙動を凍結するテスト。
/// <para>
/// 閾値以下(<see cref="TextSearcher"/> 委譲)は文書全体を 1 入力として扱うため
/// <c>^</c>/<c>$</c> は文書の先頭/末尾に anchor されるが、閾値超は行単位で
/// <see cref="TextSearcher"/> を呼ぶため各行の先頭/末尾に anchor される。
/// この差異は「行単位マッチ」という契約上の必然=<see cref="SnapshotSearcher"/>
/// docstring に明記済みの「壊れる契約」の一部であり、本テストで凍結する。
/// </para>
/// </summary>
public class SnapshotSearcherRegexAnchorTests
{
    private static SnapshotSearcher MakeLarge(
        string pattern,
        bool matchCase = false,
        bool wholeWord = false,
        bool useRegex = false,
        int threshold = 4,
        int window = 8
    ) => new(new SearchOptions(pattern, matchCase, wholeWord, useRegex), threshold, window);

    private static TextSnapshot Snap(string text) => TextBuffer.FromString(text).Current;

    [Fact]
    public void FindNext_RegexCaretAnchor_MatchesLineStart_NotDocumentStart_AboveThreshold()
    {
        // "apple\nbanana\napple\n"
        //  0     5      6      12     13    18
        //  ^-apple-^   ^-banana-^   ^-apple-^
        // 閾値超経路(行単位 regex)では `^apple` が「行の先頭」に anchor されるため、
        // 行 0 の apple(index 0)と 行 2 の apple(index 13)の両方にヒットする。
        // 閾値以下(TextSearcher)なら `^` は文書先頭にしか anchor されず、行 2 の
        // apple にはヒットしない=閾値境界でアンカー挙動が変わる契約。
        var snap = Snap("apple\nbanana\napple\n");
        var above = MakeLarge("^apple", useRegex: true, matchCase: true, threshold: 4, window: 6);

        var m1 = above.FindNext(snap, 0);
        Assert.NotNull(m1);
        Assert.Equal(new MatchSpan(0, 5), m1);

        // 次のヒットは行 2 の先頭=index 13("apple\nbanana\n" = 13 文字)
        var m2 = above.FindNext(snap, m1!.Value.Start + m1.Value.Length);
        Assert.NotNull(m2);
        Assert.Equal(new MatchSpan(13, 5), m2);

        // 3 件目はない(行 3 は空行)
        var m3 = above.FindNext(snap, m2!.Value.Start + m2.Value.Length);
        Assert.Null(m3);

        // Count も 2 件(行 0・行 2)
        Assert.Equal(2, above.Count(snap));
    }

    [Fact]
    public void FindNext_RegexDollarAnchor_MatchesLineEnd_NotDocumentEnd_AboveThreshold()
    {
        // 閾値超経路では `apple$` が「行の末尾」に anchor されるため、
        // 行 0 の apple(index 0)と 行 2 の apple(index 13)の両方にヒットする。
        // 閾値以下(TextSearcher)なら `$` は文書末尾(=末尾改行の直前)にしか
        // anchor されず、行 2 の apple のみヒット=閾値境界でアンカー挙動が変わる契約。
        var snap = Snap("apple\nbanana\napple\n");
        var above = MakeLarge("apple$", useRegex: true, matchCase: true, threshold: 4, window: 6);

        var m1 = above.FindNext(snap, 0);
        Assert.NotNull(m1);
        Assert.Equal(new MatchSpan(0, 5), m1);

        // 次のヒットは行 2 の apple=index 13
        var m2 = above.FindNext(snap, m1!.Value.Start + m1.Value.Length);
        Assert.NotNull(m2);
        Assert.Equal(new MatchSpan(13, 5), m2);

        // 3 件目はない
        var m3 = above.FindNext(snap, m2!.Value.Start + m2.Value.Length);
        Assert.Null(m3);

        // Count も 2 件
        Assert.Equal(2, above.Count(snap));
    }
}
