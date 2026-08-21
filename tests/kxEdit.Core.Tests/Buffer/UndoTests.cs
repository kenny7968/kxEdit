using kxEdit.Core.Buffers;

namespace kxEdit.Core.Tests.Buffers;

public class UndoTests
{
    private static string FullText(TextBuffer b) => b.Current.GetText(0, b.Current.CharLength);

    [Fact]
    public void Single_insert_undo_redo_roundtrip_with_caret()
    {
        var b = TextBuffer.FromString("abcd");
        b.Insert(2, "XY");
        Assert.Equal("abXYcd", FullText(b));
        Assert.True(b.CanUndo);
        Assert.False(b.CanRedo);

        var undo = b.Undo();
        Assert.Equal("abcd", FullText(b));
        Assert.Equal(new UndoResult(2), undo); // Pos + RemovedLen(0)
        Assert.True(b.CanRedo);

        var redo = b.Redo();
        Assert.Equal("abXYcd", FullText(b));
        Assert.Equal(new UndoResult(4), redo); // Pos + InsertedLen(2)
    }

    [Fact]
    public void Single_delete_undo_redo_roundtrip_with_caret()
    {
        var b = TextBuffer.FromString("abcd");
        b.Delete(1, 2);
        Assert.Equal("ad", FullText(b));
        Assert.Equal(new UndoResult(3), b.Undo()); // Pos(1) + RemovedLen(2)
        Assert.Equal("abcd", FullText(b));
        Assert.Equal(new UndoResult(1), b.Redo()); // Pos(1) + InsertedLen(0)
        Assert.Equal("ad", FullText(b));
    }

    [Fact]
    public void Undo_redo_when_impossible_return_null()
    {
        var b = TextBuffer.FromString("a");
        Assert.Null(b.Undo());
        Assert.Null(b.Redo());
    }

    [Fact]
    public void Typing_five_chars_coalesces_into_one_undo()
    {
        var b = TextBuffer.FromString("");
        foreach (char c in "hello")
            b.Insert(b.Current.CharLength, c.ToString());
        Assert.Equal("hello", FullText(b));
        b.Undo();
        Assert.Equal("", FullText(b)); // Undo1回で全部消える
        Assert.False(b.CanUndo);
    }

    [Fact]
    public void BreakUndoCoalescing_splits_typing_run()
    {
        var b = TextBuffer.FromString("");
        b.Insert(0, "h");
        b.Insert(1, "e");
        b.BreakUndoCoalescing();
        b.Insert(2, "l");
        b.Insert(3, "l");
        b.Insert(4, "o");
        b.Undo();
        Assert.Equal("he", FullText(b)); // 2エントリ目だけ戻る
        b.Undo();
        Assert.Equal("", FullText(b));
        Assert.False(b.CanUndo);
    }

    [Fact]
    public void Newline_typing_does_not_coalesce()
    {
        var b = TextBuffer.FromString("");
        b.Insert(0, "a");
        b.Insert(1, "\n");
        b.Insert(2, "b");
        // "a" → "\n" → "b" = 3エントリ
        b.Undo();
        Assert.Equal("a\n", FullText(b));
        b.Undo();
        Assert.Equal("a", FullText(b));
        b.Undo();
        Assert.Equal("", FullText(b));
        Assert.False(b.CanUndo);
    }

    [Fact]
    public void Backspace_run_coalesces_in_reverse_direction()
    {
        var b = TextBuffer.FromString("hello");
        b.BreakUndoCoalescing();
        for (int i = 4; i >= 0; i--)
            b.Delete(i, 1); // Backspace連打
        Assert.Equal("", FullText(b));
        var undo = b.Undo();
        Assert.Equal("hello", FullText(b)); // 1回で全復元
        Assert.False(b.CanUndo);
        Assert.Equal(new UndoResult(5), undo); // Pos(0) + RemovedLen(5)
    }

    [Fact]
    public void Forward_delete_run_coalesces_at_same_position()
    {
        var b = TextBuffer.FromString("hello");
        for (int i = 0; i < 5; i++)
            b.Delete(0, 1); // Delete前方連打
        Assert.Equal("", FullText(b));
        b.Undo();
        Assert.Equal("hello", FullText(b));
        Assert.False(b.CanUndo);
    }

    [Fact]
    public void Insert_delete_alternation_does_not_coalesce()
    {
        var b = TextBuffer.FromString("");
        b.Insert(0, "a"); // entry1
        b.Delete(0, 1); // entry2(挿入と削除は融合しない)
        b.Insert(0, "b"); // entry3
        b.Undo();
        Assert.Equal("", FullText(b));
        b.Undo();
        Assert.Equal("a", FullText(b));
        b.Undo();
        Assert.Equal("", FullText(b));
        Assert.False(b.CanUndo);
    }

    [Fact]
    public void Modified_tracks_save_point_through_undo()
    {
        var b = TextBuffer.FromString("doc");
        Assert.False(b.Modified); // 読込直後は未変更
        b.Insert(3, "!");
        Assert.True(b.Modified);
        b.MarkSaved();
        Assert.False(b.Modified);
        b.Insert(4, "?");
        Assert.True(b.Modified);
        b.Undo(); // 保存点まで戻る → Modified=false(参照一致)
        Assert.False(b.Modified);
        b.Redo();
        Assert.True(b.Modified);
    }

    [Fact]
    public void MarkUnsaved_pins_modified_until_next_save_point()
    {
        var b = TextBuffer.FromString("doc");
        Assert.False(b.Modified); // fresh バッファは生成時に保存点を持つ
        b.MarkUnsaved();
        Assert.True(b.Modified); // 保存点破棄=編集なしでも dirty(バックアップ復元)
        b.MarkSaved();
        Assert.False(b.Modified); // 保存で解除=以後は通常の参照比較へ戻る
        b.Insert(3, "!");
        Assert.True(b.Modified);
        b.Undo();
        Assert.False(b.Modified); // 解除後は Undo で保存点へ戻れる(フラグが残留しない)
    }

    [Fact]
    public void MarkUnsaved_pins_modified_even_for_empty_buffer()
    {
        // 空文書はルートも保存時ルートも null=参照比較では絶対に dirty にできない経路。
        // フラグでない代替実装(_savedRoot 差し替え等)をここで弾く。
        var b = TextBuffer.FromString("");
        Assert.False(b.Modified);
        b.MarkUnsaved();
        Assert.True(b.Modified);
    }

    [Fact]
    public void MarkUnsaved_keeps_modified_when_undo_returns_to_original_root()
    {
        var b = TextBuffer.FromString("doc");
        b.MarkUnsaved();
        b.Insert(3, "!");
        b.Undo(); // 生成時ルートへ戻っても保存点は存在しない
        Assert.True(b.Modified); // =復元内容は依然どこにも保存されていない
    }

    [Fact]
    public void New_edit_after_undo_discards_redo()
    {
        var b = TextBuffer.FromString("");
        b.Insert(0, "abc");
        b.Undo();
        Assert.True(b.CanRedo);
        b.Insert(0, "x");
        Assert.False(b.CanRedo);
        Assert.Equal("x", FullText(b));
    }

    [Fact]
    public void Edit_after_undo_does_not_coalesce_with_older_entry()
    {
        var b = TextBuffer.FromString("");
        b.Insert(0, "a");
        b.BreakUndoCoalescing();
        b.Insert(1, "b");
        b.Undo(); // "b" を取り消し
        b.Insert(1, "c"); // 古い "a" エントリへ融合してはいけない
        Assert.Equal("ac", FullText(b));
        b.Undo();
        Assert.Equal("a", FullText(b));
        b.Undo();
        Assert.Equal("", FullText(b));
    }

    [Fact]
    public void ClearUndo_discards_stacks_but_keeps_save_point()
    {
        var b = TextBuffer.FromString("doc");
        b.Insert(3, "!");
        b.MarkSaved();
        b.Insert(4, "?");
        Assert.True(b.CanUndo);
        Assert.True(b.Modified);
        b.ClearUndo();
        Assert.False(b.CanUndo);
        Assert.False(b.CanRedo);
        Assert.True(b.Modified); // 判定は不変(保存点維持)
        Assert.Null(b.Undo());
    }

    [Fact]
    public void Replace_is_single_undo_unit()
    {
        var b = TextBuffer.FromString("hello world");
        b.Replace(0, 5, "goodbye");
        Assert.Equal("goodbye world", FullText(b));
        var undo = b.Undo();
        Assert.Equal("hello world", FullText(b));
        Assert.Equal(new UndoResult(5), undo); // Pos(0) + RemovedLen(5)
        var redo = b.Redo();
        Assert.Equal("goodbye world", FullText(b));
        Assert.Equal(new UndoResult(7), redo); // Pos(0) + InsertedLen(7)
    }
}
