using kxEdit.Core.Search;
using Xunit;

namespace kxEdit.Core.Tests.Search;

/// <summary>
/// フェーズ 8(perf-grep): span 版 <see cref="TextSearcher.FindFirst"/> が、文字列を切り出して
/// <c>FindNext(line, 0)</c> した結果と同じであること。特に、大きな文字列の一部を span で渡しても
/// アンカー・後読み・先読み・\b が範囲の外を見ないこと。
/// </summary>
public class TextSearcherFindFirstTests
{
    private static readonly string[] Patterns =
    {
        "a",
        "ab",
        "^a",
        "a$",
        "^$",
        @"\Ab",
        @"a\z",
        @"\bab\b",
        "(?<=a)b",
        "a(?=b)",
        "(?<!a)b",
        "b(?!a)",
        "a*",
        @"\w+",
        "[あ-ん]+",
        "Ǆ",
        "(?i)k",
        @"\G.",
    };

    [Fact]
    public void FindFirst_on_slice_equals_FindNext_on_substring()
    {
        var rng = new Random(20260926);
        const string alphabet = "abAB あKKǅ𠀀_";
        foreach (string pattern in Patterns)
        {
            foreach (bool useRegex in new[] { true, false })
            {
                foreach (bool matchCase in new[] { true, false })
                {
                    foreach (bool wholeWord in new[] { true, false })
                    {
                        var s = new TextSearcher(
                            new SearchOptions(pattern, matchCase, wholeWord, useRegex)
                        );
                        if (!s.IsValid)
                            continue;
                        for (int n = 0; n < 60; n++)
                        {
                            var chars = new char[rng.Next(0, 16)];
                            for (int i = 0; i < chars.Length; i++)
                                chars[i] = alphabet[rng.Next(alphabet.Length)];
                            string whole = new(chars);
                            int start = rng.Next(0, whole.Length + 1);
                            int len = rng.Next(0, whole.Length - start + 1);
                            string line = whole.Substring(start, len);

                            Assert.Equal(
                                s.FindNext(line, 0),
                                s.FindFirst(whole.AsSpan(start, len))
                            );
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void FindFirst_returns_null_when_invalid()
    {
        var s = new TextSearcher(new SearchOptions("[", UseRegex: true));
        Assert.Null(s.FindFirst("abc".AsSpan()));
    }
}
