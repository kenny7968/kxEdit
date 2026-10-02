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
    public void Typing_across_grid_points_and_blocks_keeps_one_piece_per_block()
    {
        // 格子点をまたいで包み直しても、同じブロックの別の包み同士は左マージで 1 ピースに戻る。
        // ブロックの繰上げ(別の配列)では結合しないので、2 ブロック強を打つと 3 ピースになる。
        var b = TextBuffer.FromString("");
        for (int i = 0; i < G + 1; i++)
            b.Insert(b.Current.CharLength, "a");
        Assert.Equal(1, b.Current.PieceCount); // フェーズ 4 の後は 2 だった

        int total = 2 * AppendBuffer.BlockBytes + 100;
        while (b.Current.CharLength < total)
            b.Insert(b.Current.CharLength, "a");
        Assert.Equal(3, b.Current.PieceCount);
        Assert.Equal(new string('a', total), b.Current.GetText(0, total));
    }

    [Fact]
    public void Merged_piece_takes_the_newest_wrap()
    {
        // 結合したピースは新しい包み(格子点が多い)を採る。古い包みを採っても正しさは同じで
        // 格子が粗くなるだけなので、テキストの照合では検出できない(傘設計書 §9.2)。
        var b = TextBuffer.FromString("");
        for (int i = 0; i < 2 * G + 10; i++)
            b.Insert(b.Current.CharLength, "a");
        var piece = Assert.Single(PieceTree.Enumerate(b.Current.Root));
        AssertGrid(piece.Chunk, 0, G, 2 * G);
    }

    [Fact]
    public void Paste_straddling_block_end_merges_head_into_old_block_piece()
    {
        // ブロック末尾をまたぐ貼り付けは [旧ブロックの末尾, 新ブロック] の 2 ピースになる。
        // 左マージは旧ブロックの末尾(newPieces[0])とだけ結合し、その包みを採る。
        // 新ブロック側(newPieces[^1])の包みを採ると、旧ブロックのオフセットで新ブロックを読んで壊れる。
        var b = TextBuffer.FromString("");
        int fill = AppendBuffer.BlockBytes - 6;
        b.Insert(0, new string('a', AppendBuffer.LargeInsertBytes));
        b.Insert(b.Current.CharLength, new string('a', fill - AppendBuffer.LargeInsertBytes));
        b.Insert(b.Current.CharLength, "0123456789ABCDEFGHIJ");
        Assert.Equal(
            new string('a', fill) + "0123456789ABCDEFGHIJ",
            b.Current.GetText(0, b.Current.CharLength)
        );
        Assert.Equal(2, b.Current.PieceCount);
    }

    [Fact]
    public void Typing_after_undo_does_not_merge_non_contiguous_bytes()
    {
        // Undo で以前のルートに戻ると、追記位置は取り消したバイトの先にある。
        // 下地は同じでもバイトが連続しないので結合しない。
        var b = TextBuffer.FromString("");
        b.Insert(0, "abc");
        b.BreakUndoCoalescing();
        b.Insert(3, "def");
        Assert.NotNull(b.Undo());
        b.Insert(3, "X");
        Assert.Equal("abcX", b.Current.GetText(0, b.Current.CharLength));
        Assert.Equal(2, b.Current.PieceCount);
    }

    [Fact]
    public void Contiguous_offsets_in_different_chunks_are_not_merged()
    {
        // ファイル由来のチャンクのピース [0,3) の直後に、追記ブロックの [3,4) が来る。
        // オフセットは数値上連続だが下地が違うので、結合すると追記ブロックの [0,4) を読んでしまう。
        var b = TextBuffer.FromString("abc");
        b.Insert(0, "xyz"); // 追記ブロック [0,3)
        b.Insert(6, "Q"); // 追記ブロック [3,4)。左隣はファイル由来の [0,3)
        Assert.Equal("xyzabcQ", b.Current.GetText(0, b.Current.CharLength));
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
        // 多バイト文字・CR/LF 混在の打鍵で、元の文字列と一致すること(一般的な回帰網)。
        // 周期 16 バイトは 4096 を割り切るので名目点はいつも模様の先頭に当たる。名目点が文字の
        // 途中に来る形は Nominal_inside_… と Snapped_point_equal_…、TextChunk の単体テスト・ファズが担う
        var sb = new StringBuilder();
        while (Encoding.UTF8.GetByteCount(sb.ToString()) < 5 * G)
            sb.Append("éあ😀a\r\nx\ry\n");
        string text = sb.ToString();
        var b = TypeByCodePoint(text);
        AssertMatchesSource(b.Current, text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Pasted_blocks_with_grid_points_inside_multibyte_chars_match_source(int phase)
    {
        // 傘設計書の項目 27: 数 KB の塊を何度も貼り付け、名目の格子点(4096 の倍数)が
        // 3 バイト・4 バイトの文字の途中に来る形を作る。周期 7 バイト(あ😀)は 4096 を割り切らない
        // (4096 mod 7 = 1)ので、位相 phase(先頭の ASCII)を変えると格子点が文字の途中に来る。
        // 塊は 32KB 以下なので追記ブロックに入る。
        var unit = new StringBuilder();
        while (Encoding.UTF8.GetByteCount(unit.ToString()) < 3000)
            unit.Append("あ😀");
        unit.Append("\r\n");
        string block = unit.ToString(); // 約 3KB(格子幅より小さい)

        var b = TextBuffer.FromString("");
        var expected = new StringBuilder();
        string head = new('a', phase);
        b.Insert(0, head);
        expected.Append(head);
        for (int i = 0; i < 6; i++) // 約 18KB = 格子点 4 つをまたぐ
        {
            b.Insert(b.Current.CharLength, block);
            expected.Append(block);
            AssertMatchesSource(b.Current, expected.ToString());
        }

        // 歯の担保: 名目点が文字の途中だったため前方スナップされた格子点が、少なくとも 1 つある
        var lastChunk = PieceTree.Enumerate(b.Current.Root).Last().Chunk;
        Assert.Contains(lastChunk.GridByteOffsets.ToArray(), off => off != 0 && off % G != 0);
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
