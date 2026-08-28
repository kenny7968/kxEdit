using kxEdit.Core.Buffers;

namespace kxEdit.Core.Tests.Buffers;

/// <summary>
/// A-11(監査 2026-08-22): 保存時の EOL 一括変換が ReplaceSource で新規 TextBuffer に
/// 差し替わり、Undo/Redo 履歴を全消去していた。全文差し替えを 1 Undo 単位として
/// 記録する API の契約テスト。
/// </summary>
public class TextBufferReplaceAllTests
{
    private static TextSnapshot Rebuilt(string text) => TextBuffer.FromString(text).Current;

    private static string FullText(TextBuffer b) => b.Current.GetText(0, b.Current.CharLength);

    [Fact]
    public void ReplaceAllRecordingUndo_Undo_RestoresPreviousText()
    {
        var buf = TextBuffer.FromString("a\nb\nc");
        Assert.True(buf.ReplaceAllRecordingUndo(Rebuilt("a\r\nb\r\nc"))); // 記録した = true

        Assert.Equal("a\r\nb\r\nc", FullText(buf));
        Assert.True(buf.CanUndo);

        buf.Undo();
        Assert.Equal("a\nb\nc", FullText(buf));
    }

    [Fact]
    public void ReplaceAllRecordingUndo_Redo_ReappliesReplacement()
    {
        var buf = TextBuffer.FromString("a\nb");
        buf.ReplaceAllRecordingUndo(Rebuilt("a\r\nb"));
        buf.Undo();

        Assert.True(buf.CanRedo);
        buf.Redo();
        Assert.Equal("a\r\nb", FullText(buf));
    }

    // I-4: 通常の編集と同じく Redo スタックを破棄する(UndoHistory.Record の _redo.Clear())。
    // Task 4 の保存失敗ロールバックでユーザーの redo が失われる帰結は受容(設計書 §10.11)。
    [Fact]
    public void ReplaceAllRecordingUndo_DiscardsRedoStack()
    {
        var buf = TextBuffer.FromString("a\nb");
        buf.Insert(3, "Z");
        buf.Undo();
        Assert.True(buf.CanRedo);

        buf.ReplaceAllRecordingUndo(Rebuilt("a\r\nb"));
        Assert.False(buf.CanRedo);
    }

    // A-11 の本質的な回帰網: 差し替えの前に積んだ履歴が生き残ること。
    // 旧実装(ReplaceSource で新規 TextBuffer)ではここが 1 回目の Undo で頭打ちになっていた。
    [Fact]
    public void ReplaceAllRecordingUndo_PreservesEarlierHistory()
    {
        var buf = TextBuffer.FromString("a");
        buf.Insert(1, "\nX"); // 履歴 1
        buf.BreakUndoCoalescing();
        buf.Insert(3, "\nY"); // 履歴 2
        Assert.Equal("a\nX\nY", FullText(buf));

        buf.ReplaceAllRecordingUndo(Rebuilt("a\r\nX\r\nY")); // 履歴 3

        buf.Undo();
        Assert.Equal("a\nX\nY", FullText(buf));
        buf.Undo();
        Assert.Equal("a\nX", FullText(buf));
        buf.Undo();
        Assert.Equal("a", FullText(buf));
        Assert.False(buf.CanUndo);
    }

    // 保存点セマンティクス: _savedRoot を触らないので、差し替えで Modified が立ち、
    // Undo で保存点へ戻ると false へ復す(参照比較)。
    [Fact]
    public void ReplaceAllRecordingUndo_ModifiedTogglesWithSavePoint()
    {
        var buf = TextBuffer.FromString("a\nb");
        buf.MarkSaved();
        Assert.False(buf.Modified);

        buf.ReplaceAllRecordingUndo(Rebuilt("a\r\nb"));
        Assert.True(buf.Modified);

        buf.Undo();
        Assert.False(buf.Modified); // 保存点の root へ戻った
    }

    // EOL 変換が作る通常形(removed > 0 かつ inserted > 0)の characterization。
    // この形は UndoHistory.Record の融合判定が pureInsert / pureDelete のどちらにもならず、
    // 構造的に融合しない = 前置 BreakCoalescing / insertHasBreak のどちらを外しても結果は同じ
    // (2 つのガードを実際に撃墜するのは下の退化形 2 件の fact)。
    // ここで固定するのは「差し替えがタイプ 2 回の間で独立した 1 エントリになる」構造であり、
    // Undo 3 回でちょうど 3 段戻ることを見ている。
    [Fact]
    public void ReplaceAllRecordingUndo_IsSingleUndoUnit_BetweenTypedEdits()
    {
        var buf = TextBuffer.FromString("a\nb");
        buf.Insert(3, "X"); // 直前のタイプ(coalescing が開いた状態のまま差し替えへ入る)
        buf.ReplaceAllRecordingUndo(Rebuilt("a\r\nbX"));
        buf.Insert(5, "Z"); // 直後のタイプ

        buf.Undo();
        Assert.Equal("a\r\nbX", FullText(buf)); // 直後の入力だけが戻る
        buf.Undo();
        Assert.Equal("a\nbX", FullText(buf)); // 差し替えが戻る
        buf.Undo();
        Assert.Equal("a\nb", FullText(buf)); // 直前のタイプが戻る
        Assert.False(buf.CanUndo);
    }

    // 退化形 1: 空文書 → 1 文字(removed == 0 = 純挿入形)。この形だけは UndoHistory.Record の
    // 融合判定が pureInsert で通るため、直後のタイプが差し替えエントリへ融合しうる。
    // insertHasBreak: true がそれを止めている(通常形では pureInsert にならないので効かない)。
    [Fact]
    public void ReplaceAllRecordingUndo_PureInsertShape_DoesNotAbsorbFollowingTyping()
    {
        var buf = TextBuffer.FromString("");
        buf.ReplaceAllRecordingUndo(Rebuilt("a"));
        buf.Insert(1, "Z"); // 融合すると差し替えエントリの InsertedLen が伸びる

        buf.Undo();
        Assert.Equal("a", FullText(buf)); // 直後の入力だけが戻る
        buf.Undo();
        Assert.Equal("", FullText(buf)); // 差し替えが戻る
        Assert.False(buf.CanUndo);
    }

    // 退化形 2: 全文 → 空(inserted == 0 = 純削除形)。この形は pureDelete として融合判定を
    // 通り、しかも pureDelete 側は insertHasBreak を見ないため、直前の 1 文字削除へ
    // 逆方向融合(Backspace 継続扱い)しうる。Record 前の BreakCoalescing がそれを止めている。
    [Fact]
    public void ReplaceAllRecordingUndo_PureDeleteShape_DoesNotMergeIntoPrecedingDelete()
    {
        var buf = TextBuffer.FromString("abc");
        buf.Delete(2, 1); // 1 文字削除 = coalescing が開いたまま残る
        Assert.Equal("ab", FullText(buf));

        buf.ReplaceAllRecordingUndo(Rebuilt(""));
        Assert.Equal("", FullText(buf));

        buf.Undo();
        Assert.Equal("ab", FullText(buf)); // 融合していれば "abc" まで戻ってしまう
        buf.Undo();
        Assert.Equal("abc", FullText(buf));
        Assert.False(buf.CanUndo);
    }

    // ミューテーション検証で pos / removed / inserted の変異が生存したため追加
    // (これらは Undo() / Redo() の戻り値 CaretPos にしか効かない)。
    // Undo の推奨キャレット位置は Pos + RemovedLen(削除が復元された末尾)、
    // Redo は Pos + InsertedLen。全文差し替えではどちらも「文書末尾」になる。
    [Fact]
    public void ReplaceAllRecordingUndo_UndoRedoCaretPos_IsEndOfDocument()
    {
        var buf = TextBuffer.FromString("a\nb"); // CharLength 3
        buf.ReplaceAllRecordingUndo(Rebuilt("a\r\nb")); // CharLength 4

        var undo = buf.Undo();
        Assert.NotNull(undo);
        Assert.Equal(3, undo!.Value.CaretPos); // Pos(0) + RemovedLen(3)

        var redo = buf.Redo();
        Assert.NotNull(redo);
        Assert.Equal(4, redo!.Value.CaretPos); // Pos(0) + InsertedLen(4)
    }

    [Fact]
    public void ReplaceAllRecordingUndo_Null_Throws()
    {
        var buf = TextBuffer.FromString("a");
        Assert.Throws<ArgumentNullException>(() => buf.ReplaceAllRecordingUndo(null!));
    }

    // 無変化(同一 root)では履歴を汚さない=Splice の `return` と同じ契約。
    // 非既定状態(履歴を 1 つ積んだ後)から検証する(CLAUDE.md §4-B)。
    [Fact]
    public void ReplaceAllRecordingUndo_SameRoot_DoesNotRecord()
    {
        var buf = TextBuffer.FromString("a\nb");
        buf.Insert(3, "Z");

        // 同一 root のスナップショット(自分自身の Current)を渡す
        Assert.False(buf.ReplaceAllRecordingUndo(buf.Current)); // 記録しなかった = false

        buf.Undo(); // 記録されていれば 1 回目の Undo が no-op 相当になり "a\nbZ" が残る
        Assert.Equal("a\nb", FullText(buf));
        Assert.False(buf.CanUndo);
    }

    // 無変化パスは coalescing も汚さない(前置 BreakCoalescing を早期 return の「後」に置いた根拠)。
    // 早期 return の前へ移すと _open が倒れ、直前のタイプの続きが別エントリになる。
    [Fact]
    public void ReplaceAllRecordingUndo_SameRoot_DoesNotBreakCoalescing()
    {
        var buf = TextBuffer.FromString("");
        buf.Insert(0, "a"); // coalescing が開いた状態
        Assert.False(buf.ReplaceAllRecordingUndo(buf.Current));
        buf.Insert(1, "b"); // 直前のタイプへ融合し続けるはず

        buf.Undo();
        Assert.Equal("", FullText(buf)); // 融合が生きていれば 1 エントリで両方戻る
    }

    // 早期 return の判定基準が「root 同一」であることを、インスタンス同一と弁別できる形で固定する。
    // 空文書同士は別バッファ・別スナップショットだが BuildBalanced がどちらも null root を返す。
    // 判定を ReferenceEquals(_current, rebuilt) に変えると、ここで removed=0 / inserted=0 の
    // 死んだ Undo 段が積まれる(..._SameRoot_DoesNotRecord は同一インスタンスなので弁別できない)。
    [Fact]
    public void ReplaceAllRecordingUndo_DistinctSnapshotWithSameRoot_DoesNotRecord()
    {
        var buf = TextBuffer.FromString("");
        var other = TextBuffer.FromString("").Current;
        Assert.NotSame(buf.Current, other); // 前提: スナップショットは別インスタンス

        Assert.False(buf.ReplaceAllRecordingUndo(other));
        Assert.False(buf.CanUndo);
    }
}
