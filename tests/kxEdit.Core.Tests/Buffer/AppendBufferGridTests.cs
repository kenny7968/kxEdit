using System.Text;
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Tests.Buffers;

/// <summary>
/// 2026-09-25 フェーズ 4(F-6): AppendBuffer は次の格子点が書込済み範囲の厳密に内側に入ったら、
/// 同じブロックを gridLimit=書込済みの長さで包み直す。
/// 正解は常に元の文字列に取る(GetChar と GetText は同じ格子を通るので、相互比較では
/// 格子の破損を検出できない。設計書 §9.3・seam 設計書 §9.4)。
/// </summary>
public class AppendBufferGridTests
{
    private const int G = TextChunk.DefaultGridBytes;

    // ---- AppendBuffer 単体: 包み直しの条件と順序 ----

    [Fact]
    public void No_rewrap_when_pos_reaches_nominal_exactly()
    {
        var ab = new AppendBuffer();
        var first = ab.Append(new string('a', G - 1));
        var second = ab.Append("a"); // _pos = 4096 ちょうど: 4096 には置けない(< gridLimit)
        Assert.Same(first[0].Chunk, second[0].Chunk);
        AssertGrid(second[0].Chunk, 0);

        var third = ab.Append("b"); // _pos = 4097: 4096 が内側に入った
        Assert.NotSame(second[0].Chunk, third[0].Chunk);
        AssertGrid(third[0].Chunk, 0, G);
    }

    [Fact]
    public void Nominal_inside_multibyte_char_waits_until_snapped_point_is_inside()
    {
        var ab = new AppendBuffer();
        var first = ab.Append(new string('a', G - 1));
        var second = ab.Append("あ"); // 4095..4097。名目 4096 は途中 → スナップ後 4098 = _pos
        Assert.Same(first[0].Chunk, second[0].Chunk);

        var third = ab.Append("b"); // _pos = 4099: スナップ後の 4098 が内側に入った
        Assert.NotSame(second[0].Chunk, third[0].Chunk);
        AssertGrid(third[0].Chunk, 0, G + 2);
    }

    [Fact]
    public void Piece_of_a_large_write_refers_to_the_rewrapped_chunk()
    {
        // 順序「書込 → 包み直し → ピース」: 32KB 以下の貼り付けが古い格子を参照しないこと
        var ab = new AppendBuffer();
        var pieces = ab.Append(new string('a', 5 * G + 10));
        Assert.Single(pieces);
        AssertGrid(pieces[0].Chunk, 0, G, 2 * G, 3 * G, 4 * G, 5 * G);
    }

    [Fact]
    public void New_block_starts_without_grid_points_and_rewraps_again()
    {
        var ab = new AppendBuffer();
        ab.Append(new string('a', AppendBuffer.LargeInsertBytes));
        ab.Append(new string('a', AppendBuffer.LargeInsertBytes)); // ブロックちょうど満杯
        var next = ab.Append("b"); // 新ブロックの先頭
        Assert.Equal(0, next[0].ByteStart);
        AssertGrid(next[0].Chunk, 0);

        var more = ab.Append(new string('c', G)); // 新ブロックで _pos = 4097
        AssertGrid(more[0].Chunk, 0, G);
    }

    // 以下 2 本は no-change / 待機の検証を _nextNominal の既定値(G)ではない状態から始める
    // (CLAUDE.md §4-B。既定値からだけだと while の進め方の誤りが生き残る)。

    [Fact]
    public void No_extra_rewrap_after_a_write_crossing_several_grid_points()
    {
        // 1 回の書込で 2 点(G・2G)をまたいだ後、次の書込で余計に包み直さない
        // (while が 1 点しか進めないと、2G が「まだ入っていない」扱いになり包み直してしまう)
        var ab = new AppendBuffer();
        var first = ab.Append(new string('a', 2 * G + 10));
        AssertGrid(first[0].Chunk, 0, G, 2 * G);
        var second = ab.Append("x");
        Assert.Same(first[0].Chunk, second[0].Chunk);
    }

    [Fact]
    public void Snapped_point_equal_to_pos_after_multi_point_write_waits_for_next_write()
    {
        // 1 回の書込で G をまたぎ、末尾の「あ」(2G-1..2G+1)が 2G をまたぐ。スナップ後の 2G+2 は
        // _pos と等しいので置かずに待ち、次の書込で包み直す(while の条件を <= にすると 2G を飛ばして漏れる)
        var ab = new AppendBuffer();
        var first = ab.Append(new string('a', 2 * G - 1) + "あ"); // _pos = 2G+2
        AssertGrid(first[0].Chunk, 0, G);
        var second = ab.Append("b");
        Assert.NotSame(first[0].Chunk, second[0].Chunk);
        AssertGrid(second[0].Chunk, 0, G, 2 * G + 2);
    }

    // ---- TextBuffer 経由: 元の文字列との一致 ----

    [Fact]
    public void Typing_up_to_nominal_does_not_add_piece()
    {
        var b = TextBuffer.FromString("");
        for (int i = 0; i < G; i++)
            b.Insert(b.Current.CharLength, "a");
        Assert.Equal(1, b.Current.PieceCount);
        b.Insert(b.Current.CharLength, "a");
        Assert.Equal(2, b.Current.PieceCount); // §3.5 の意図的な挙動差
    }

    [Theory]
    [InlineData(G - 1)] // CR が 4095、LF が 4096(格子点)に来る: CRLF が格子点をまたぐ
    [InlineData(G)] // CR が 4096(格子点)で、書込の最後のバイトになる。LF は次の打鍵
    public void Crlf_straddling_grid_point_typed_one_by_one(int crAt)
    {
        string text = new string('a', crAt) + "\r\n" + new string('b', 100) + "\r\nend";
        var b = TextBuffer.FromString("");
        for (int i = 0; i < text.Length; i++)
        {
            b.Insert(b.Current.CharLength, text[i].ToString());
            if (i >= crAt - 1 && i <= crAt + 2)
                AssertMatchesSource(b.Current, text[..(i + 1)]); // CR だけ書いた状態も含む
        }
        AssertMatchesSource(b.Current, text);
    }

    [Fact]
    public void Multibyte_text_typed_by_code_point_matches_source()
    {
        // 名目 4KB 点が 2・3・4 バイト文字の途中に当たる形を網羅する(周期 13 バイトは 4096 と互いに素)
        var sb = new StringBuilder();
        while (Encoding.UTF8.GetByteCount(sb.ToString()) < 5 * G)
            sb.Append("éあ😀a\r\nx\ry\n");
        string text = sb.ToString();
        var b = TypeByCodePoint(text);
        AssertMatchesSource(b.Current, text);
    }

    [Fact]
    public void Old_snapshots_are_unchanged_after_later_writes_and_rewraps()
    {
        var b = TextBuffer.FromString("");
        var saved = new List<(TextSnapshot Snap, string Text)>();
        var sb = new StringBuilder();
        for (int i = 0; i < 3000; i++)
        {
            string s =
                (
                    i % 7 == 0 ? "\r\n"
                    : i % 5 == 0 ? "あ"
                    : "ab"
                ) + (i % 11 == 0 ? "😀" : "");
            b.Insert(b.Current.CharLength, s);
            sb.Append(s);
            if (i % 250 == 0)
                saved.Add((b.Current, sb.ToString()));
        }
        foreach (var (snap, expected) in saved)
            AssertMatchesSource(snap, expected);
    }

    [Fact]
    public void Undo_redo_across_rewraps_restore_exact_text()
    {
        var b = TextBuffer.FromString("");
        var history = new List<string> { "" };
        for (int i = 0; i < 40; i++)
        {
            string chunk = string.Concat(Enumerable.Repeat($"行{i}あ😀\r\n", 12)); // 約 300 バイト
            b.Insert(b.Current.CharLength, chunk);
            b.BreakUndoCoalescing();
            history.Add(history[^1] + chunk);
        }
        for (int i = history.Count - 1; i > 0; i--)
        {
            Assert.NotNull(b.Undo());
            AssertMatchesSource(b.Current, history[i - 1]);
        }
        for (int i = 1; i < history.Count; i++)
        {
            Assert.NotNull(b.Redo());
            AssertMatchesSource(b.Current, history[i]);
        }
    }

    // ---- helpers ----

    private static void AssertGrid(TextChunk chunk, params int[] expected) =>
        Assert.Equal(expected, chunk.GridByteOffsets.ToArray());

    private static TextBuffer TypeByCodePoint(string text)
    {
        var b = TextBuffer.FromString("");
        for (int i = 0; i < text.Length; )
        {
            int n = char.IsHighSurrogate(text[i]) ? 2 : 1;
            b.Insert(b.Current.CharLength, text.Substring(i, n));
            i += n;
        }
        return b;
    }

    /// <summary>全位置の GetChar・全行の行頭・全位置の行番号を、元の文字列と照合する。</summary>
    private static void AssertMatchesSource(TextSnapshot snap, string src)
    {
        Assert.Equal(src.Length, snap.CharLength);
        Assert.Equal(src, snap.GetText(0, snap.CharLength));
        for (int pos = 0; pos < src.Length; pos++)
            Assert.Equal(src[pos], snap.GetChar(pos));
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < src.Length; i++)
            if (src[i] == '\n' || (src[i] == '\r' && (i + 1 == src.Length || src[i + 1] != '\n')))
                lineStarts.Add(i + 1);
        Assert.Equal(lineStarts.Count, snap.LineCount);
        for (int line = 0; line < lineStarts.Count; line++)
            Assert.Equal(lineStarts[line], snap.GetLineStart(line));
        int li = 0;
        for (int pos = 0; pos <= src.Length; pos++)
        {
            while (li + 1 < lineStarts.Count && lineStarts[li + 1] <= pos)
                li++;
            Assert.Equal(li, snap.GetLineIndexOfChar(pos));
        }
    }
}
