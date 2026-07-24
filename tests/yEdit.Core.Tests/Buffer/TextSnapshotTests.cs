using System.Text;
using yEdit.Core.Buffers;

namespace yEdit.Core.Tests.Buffers;

public class TextSnapshotTests
{
    // 6行: CRLF / LF / 単独CR / CRLF / LF の5 break+最終行(絵文字終端)
    private const string Doc = "これは1行目\r\n2nd line\nempty next\r\r\n\n最終行😀";

    private static Piece P(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        return Piece.Of(new TextChunk(bytes, gridBytes: 8), 0, bytes.Length);
    }

    private static TextSnapshot Snap(params string[] parts) =>
        new(PieceTree.BuildBalanced(parts.Select(P).ToArray()));

    /// <summary>単一ピース版と CRLF跨ぎ複数ピース版の両レイアウト。</summary>
    private static IEnumerable<TextSnapshot> DocLayouts()
    {
        yield return Snap(Doc);
        yield return Snap("これは1行目\r", "\n2nd line\nempty next", "\r\r", "\n\n最終行😀");
    }

    private static List<int> NaiveBreakEnds(string s)
    {
        var ends = new List<int>();
        for (int i = 0; i < s.Length; i++)
            if (s[i] == '\n' || (s[i] == '\r' && (i + 1 == s.Length || s[i + 1] != '\n')))
                ends.Add(i);
        return ends;
    }

    [Fact]
    public void CharLength_and_LineCount()
    {
        foreach (var snap in DocLayouts())
        {
            Assert.Equal(Doc.Length, snap.CharLength);
            Assert.Equal(6, snap.LineCount);
        }
    }

    [Fact]
    public void GetLineStart_and_GetLineEnd_match_naive()
    {
        var ends = NaiveBreakEnds(Doc);
        foreach (var snap in DocLayouts())
        {
            for (int line = 0; line < snap.LineCount; line++)
            {
                int expectedStart = line == 0 ? 0 : ends[line - 1] + 1;
                Assert.Equal(expectedStart, snap.GetLineStart(line));

                bool isLast = line == snap.LineCount - 1;
                int expectedEndIncl = isLast ? Doc.Length : ends[line] + 1;
                int expectedEndNoBreak = isLast
                    ? Doc.Length
                    : ends[line]
                        - (
                            Doc[ends[line]] == '\n' && ends[line] > 0 && Doc[ends[line] - 1] == '\r'
                                ? 1
                                : 0
                        );
                Assert.Equal(expectedEndIncl, snap.GetLineEnd(line, includeBreak: true));
                Assert.Equal(expectedEndNoBreak, snap.GetLineEnd(line, includeBreak: false));
            }
        }
    }

    [Fact]
    public void GetLineIndexOfChar_matches_naive_at_every_position()
    {
        var ends = NaiveBreakEnds(Doc);
        foreach (var snap in DocLayouts())
            for (int pos = 0; pos <= Doc.Length; pos++)
                Assert.Equal(ends.Count(e => e < pos), snap.GetLineIndexOfChar(pos));
    }

    [Fact]
    public void GetLineIndexOfChar_at_char_length_is_last_line()
    {
        foreach (var snap in DocLayouts())
            Assert.Equal(snap.LineCount - 1, snap.GetLineIndexOfChar(snap.CharLength));
    }

    [Fact]
    public void Empty_document()
    {
        var snap = Snap();
        Assert.Equal(0, snap.CharLength);
        Assert.Equal(1, snap.LineCount);
        Assert.Equal(0, snap.GetLineStart(0));
        Assert.Equal(0, snap.GetLineEnd(0, includeBreak: true));
        Assert.Equal(0, snap.GetLineEnd(0, includeBreak: false));
        Assert.Equal(0, snap.GetLineIndexOfChar(0));
        Assert.Equal("", snap.GetText(0, 0));
    }

    [Fact]
    public void Trailing_newline_makes_empty_final_line()
    {
        var snap = Snap("abc\r\n");
        Assert.Equal(2, snap.LineCount);
        Assert.Equal(snap.CharLength, snap.GetLineStart(1));
        Assert.Equal(snap.CharLength, snap.GetLineEnd(1, includeBreak: false));
    }

    [Fact]
    public void GetText_random_windows_match_substring_including_surrogate_middles()
    {
        var rnd = new Random(20260705);
        foreach (var snap in DocLayouts())
        {
            for (int t = 0; t < 50; t++)
            {
                int a = rnd.Next(Doc.Length + 1),
                    b = rnd.Next(Doc.Length + 1);
                if (a > b)
                    (a, b) = (b, a);
                Assert.Equal(Doc.Substring(a, b - a), snap.GetText(a, b - a));
            }
            // 絵文字(末尾のサロゲートペア)の中間開始・中間終了を明示的に
            int hi = Doc.Length - 2; // 😀 の high surrogate
            Assert.Equal(Doc.Substring(hi + 1, 1), snap.GetText(hi + 1, 1)); // 中間開始
            Assert.Equal(Doc.Substring(hi - 1, 2), snap.GetText(hi - 1, 2)); // 中間終了
            Assert.Equal(Doc.Substring(hi + 1), snap.GetText(hi + 1, 1)); // low のみ
        }
    }

    [Fact]
    public void GetChar_matches_string_indexer()
    {
        foreach (var snap in DocLayouts())
            for (int pos = 0; pos < Doc.Length; pos++)
                Assert.Equal(Doc[pos], snap.GetChar(pos));
    }

    [Fact]
    public void Out_of_range_arguments_throw()
    {
        var snap = Snap(Doc);
        Assert.Throws<ArgumentOutOfRangeException>(() => snap.GetText(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snap.GetText(0, Doc.Length + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snap.GetText(Doc.Length, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snap.GetChar(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snap.GetChar(Doc.Length));
        Assert.Throws<ArgumentOutOfRangeException>(() => snap.GetLineStart(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snap.GetLineStart(6));
        Assert.Throws<ArgumentOutOfRangeException>(() => snap.GetLineEnd(6, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => snap.GetLineIndexOfChar(Doc.Length + 1));
    }

    [Fact]
    public void PieceCount_reflects_layout()
    {
        Assert.Equal(1, Snap(Doc).PieceCount);
        Assert.Equal(
            4,
            Snap("これは1行目\r", "\n2nd line\nempty next", "\r\r", "\n\n最終行😀").PieceCount
        );
    }

    [Fact]
    public void CountCrlfPairs_EmptyRange_ReturnsZero()
    {
        var s = Snap("a\r\nb");
        Assert.Equal(0, s.CountCrlfPairs(0, 0));
        Assert.Equal(0, s.CountCrlfPairs(2, 2));
        Assert.Equal(0, s.CountCrlfPairs(s.CharLength, s.CharLength));
    }

    [Fact]
    public void CountCrlfPairs_FullRange_CountsCrlfPairs()
    {
        var s = Snap("a\r\nb\r\nc");
        Assert.Equal(2, s.CountCrlfPairs(0, s.CharLength));
    }

    [Fact]
    public void CountCrlfPairs_LoneCrLfMixed_CountsOnlyPairs()
    {
        var s = Snap("a\rb\nc\r\nd\rd\ne"); // CRLF 1 個 + 孤立 CR 2 個 + 孤立 LF 2 個
        Assert.Equal(1, s.CountCrlfPairs(0, s.CharLength));
    }

    [Fact]
    public void CountCrlfPairs_PartialRange_Correct()
    {
        var s = Snap("a\r\nb\r\nc");
        Assert.Equal(1, s.CountCrlfPairs(0, 3));
        Assert.Equal(0, s.CountCrlfPairs(0, 2));
        Assert.Equal(1, s.CountCrlfPairs(3, 7));
    }

    [Fact]
    public void CountCrlfPairs_PieceBoundaryStraddled_CountsCrlfAcrossPieces()
    {
        // 意図的に "a\r" | "\nb" の 2 ピース構成(Snap ヘルパで複数ピース版を組む)
        var snap = Snap("a\r", "\nb");
        Assert.Equal(1, snap.CountCrlfPairs(0, snap.CharLength));
    }
}
