using kxEdit.Core.Buffers;

namespace kxEdit.Core.Tests.Buffers;

public class TextBufferEditTests
{
    private static string FullText(TextBuffer b) => b.Current.GetText(0, b.Current.CharLength);

    [Theory]
    [InlineData("hello", 0, "X", "Xhello")] // 先頭
    [InlineData("hello", 2, "X", "heXllo")] // 中間
    [InlineData("hello", 5, "X", "helloX")] // 末尾
    [InlineData("", 0, "abc", "abc")] // 空文書へ
    [InlineData("hello", 2, "", "hello")] // 空文字insert=無変化
    [InlineData("あいう", 1, "😀", "あ😀いう")] // マルチバイト
    public void Insert_basic(string doc, int pos, string text, string expected)
    {
        var b = TextBuffer.FromString(doc);
        b.Insert(pos, text);
        Assert.Equal(expected, FullText(b));
    }

    [Theory]
    [InlineData("hello", 0, 2, "llo")]
    [InlineData("hello", 2, 2, "heo")]
    [InlineData("hello", 3, 2, "hel")]
    [InlineData("hello", 0, 5, "")] // 全文削除
    [InlineData("hello", 2, 0, "hello")] // 0長削除=無変化
    public void Delete_basic(string doc, int pos, int len, string expected)
    {
        var b = TextBuffer.FromString(doc);
        b.Delete(pos, len);
        Assert.Equal(expected, FullText(b));
    }

    [Theory]
    [InlineData("hello world", 0, 5, "goodbye", "goodbye world")]
    [InlineData("hello", 1, 3, "", "ho")]
    [InlineData("abc", 0, 3, "xyz", "xyz")]
    public void Replace_basic(string doc, int pos, int len, string text, string expected)
    {
        var b = TextBuffer.FromString(doc);
        b.Replace(pos, len, text);
        Assert.Equal(expected, FullText(b));
    }

    [Fact]
    public void Delete_lf_of_crlf_keeps_line_count()
    {
        var b = TextBuffer.FromString("a\r\nb");
        Assert.Equal(2, b.Current.LineCount);
        b.Delete(2, 1); // \n だけ削除
        Assert.Equal("a\rb", FullText(b));
        Assert.Equal(2, b.Current.LineCount); // 単独CRになっても1 break
    }

    [Fact]
    public void Delete_cr_of_crlf_keeps_line_count()
    {
        var b = TextBuffer.FromString("a\r\nb");
        b.Delete(1, 1); // \r だけ削除
        Assert.Equal("a\nb", FullText(b));
        Assert.Equal(2, b.Current.LineCount);
    }

    [Fact]
    public void Insert_lf_after_cr_fuses_into_single_crlf_break()
    {
        var b = TextBuffer.FromString("a\rb");
        Assert.Equal(2, b.Current.LineCount);
        b.Insert(2, "\n"); // \r 直後に \n
        Assert.Equal("a\r\nb", FullText(b));
        Assert.Equal(2, b.Current.LineCount); // CR+LF が1つのbreakに融合
    }

    [Fact]
    public void Insert_at_surrogate_middle_snaps_low()
    {
        var b = TextBuffer.FromString("a😀b");
        b.Insert(2, "x"); // pos=2 はペア中間 → pos=1 扱い
        Assert.Equal("ax😀b", FullText(b));
    }

    [Fact]
    public void Delete_spanning_surrogate_middles_snaps_both_ends()
    {
        var b = TextBuffer.FromString("😀😀");
        b.Delete(1, 2); // [1,3) は両端とも中間 → [0,2) = 最初のペア
        Assert.Equal("😀", FullText(b));
    }

    [Fact]
    public void Lone_surrogate_insert_becomes_replacement_char()
    {
        var b = TextBuffer.FromString("ab");
        b.Insert(1, "\uD83D"); // 孤立ハイサロゲート
        Assert.Equal("a�b", FullText(b));
    }

    [Fact]
    public void Snapshot_taken_before_edit_is_unaffected()
    {
        var b = TextBuffer.FromString("original text\r\nline2");
        var before = b.Current;
        string beforeText = before.GetText(0, before.CharLength);
        b.Replace(0, 8, "MODIFIED CONTENT");
        b.Insert(b.Current.CharLength, "\nline3");
        b.Delete(0, 3);
        // 旧スナップショットは旧内容のまま(永続性の直接検証)
        Assert.Equal(beforeText, before.GetText(0, before.CharLength));
        Assert.Equal("original text\r\nline2", beforeText);
        Assert.NotEqual(beforeText, FullText(b));
    }

    [Fact]
    public void Snapshot_containing_shared_append_block_is_unaffected_by_further_typing()
    {
        // 同一64KBブロックを共有するピースを含むスナップショットが、
        // その後の同ブロックへの追記で変化しないこと(公開済み範囲不変の直接検証)
        var b = TextBuffer.FromString("base:");
        for (int i = 0; i < 10; i++)
            b.Insert(b.Current.CharLength, "タイプ" + i);
        var mid = b.Current;
        string midText = mid.GetText(0, mid.CharLength);
        for (int i = 0; i < 200; i++)
            b.Insert(b.Current.CharLength, "追記" + i + "\r\n");
        Assert.Equal(midText, mid.GetText(0, mid.CharLength));
        Assert.NotEqual(midText, b.Current.GetText(0, b.Current.CharLength));
    }

    [Fact]
    public void Continuous_typing_does_not_fragment_pieces()
    {
        var b = TextBuffer.FromString("seed");
        int initial = b.Current.PieceCount;
        for (int i = 0; i < 1000; i++)
            b.Insert(b.Current.CharLength, "a");
        Assert.Equal("seed" + new string('a', 1000), FullText(b));
        Assert.True(
            b.Current.PieceCount <= initial + 2,
            $"PieceCount {b.Current.PieceCount} > initial {initial} + 2"
        );
    }

    [Fact]
    public void Large_insert_100kb_uses_dedicated_chunk()
    {
        string big = new string('あ', 50_000) + "\r\n" + new string('x', 30_000);
        var b = TextBuffer.FromString("[]");
        b.Insert(1, big);
        Assert.Equal("[" + big + "]", FullText(b));
    }

    [Fact]
    public void Typing_across_append_block_boundary_preserves_content()
    {
        // 64KBブロックを跨ぐ量を細切れ挿入(かな3バイト×30文字×1000回≒90KB)
        var b = TextBuffer.FromString("");
        var expected = new System.Text.StringBuilder();
        for (int i = 0; i < 1000; i++)
        {
            string s = "かきくけこさしすせそ" + (i % 10) + "\r\n";
            b.Insert(b.Current.CharLength, s);
            expected.Append(s);
        }
        Assert.Equal(expected.ToString(), FullText(b));
        Assert.Equal(1001, b.Current.LineCount);
    }

    [Fact]
    public void Mixed_random_edits_match_naive_string_model()
    {
        var rnd = new Random(20260705);
        var b = TextBuffer.FromString("start\r\n中身😀\n");
        string model = "start\r\n中身😀\n";
        string[] pool = ["x", "あ", "😀", "\r\n", "\n", "\r", "word ", "行"];
        for (int i = 0; i < 300; i++)
        {
            int op = rnd.Next(3);
            if (op == 0)
            {
                int pos = rnd.Next(model.Length + 1);
                if (pos > 0 && pos < model.Length && char.IsLowSurrogate(model[pos]))
                    pos--;
                string text = pool[rnd.Next(pool.Length)];
                b.Insert(pos, text);
                model = model[..pos] + text + model[pos..];
            }
            else if (op == 1 && model.Length > 0)
            {
                int pos = rnd.Next(model.Length);
                int len = Math.Min(rnd.Next(1, 6), model.Length - pos);
                if (pos > 0 && char.IsLowSurrogate(model[pos]))
                    pos--;
                int end = pos + len;
                if (end < model.Length && char.IsLowSurrogate(model[end]))
                    end--;
                if (end < pos)
                    end = pos;
                b.Delete(pos, len);
                model = model[..pos] + model[end..];
            }
            else if (model.Length > 1)
            {
                int pos = rnd.Next(model.Length - 1);
                if (pos > 0 && char.IsLowSurrogate(model[pos]))
                    pos--;
                int end = pos + 1;
                if (end < model.Length && char.IsLowSurrogate(model[end]))
                    end--;
                if (end < pos)
                    end = pos;
                b.Replace(pos, 1, "R");
                model = model[..pos] + "R" + model[end..];
            }
            Assert.Equal(model.Length, b.Current.CharLength);
        }
        Assert.Equal(model, FullText(b));
    }

    [Fact]
    public void Out_of_range_edits_throw()
    {
        var b = TextBuffer.FromString("abc");
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Insert(-1, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Insert(4, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Delete(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Delete(2, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Replace(3, 1, "x"));
    }
}
