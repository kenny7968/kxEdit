namespace kxEdit.Core.Buffers;

/// <summary>
/// オペレーションログ方式のUndo履歴。エントリは編集前後のルート参照+位置情報のみ
/// (永続木なのでルート保持=全内容保持。コピー不要)。
/// coalescing: タイプ/Backspace/Delete前方の連続小編集を直前エントリへ融合する。
/// </summary>
internal sealed class UndoHistory
{
    internal readonly record struct Entry(
        PieceTree.Node? RootBefore,
        PieceTree.Node? RootAfter,
        int Pos,
        int RemovedLen,
        int InsertedLen
    );

    private readonly List<Entry> _undo = [];
    private readonly List<Entry> _redo = [];
    private bool _open; // 直前エントリへの融合を許すか

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>キャレット移動・保存時にApp側が呼ぶ強制分割。</summary>
    public void BreakCoalescing() => _open = false;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _open = false;
    }

    public void Record(
        PieceTree.Node? before,
        PieceTree.Node? after,
        int pos,
        int removedLen,
        int insertedLen,
        bool insertHasBreak
    )
    {
        _redo.Clear(); // 新規編集で Redo は破棄

        bool pureInsert = removedLen == 0 && insertedLen > 0;
        bool pureDelete = insertedLen == 0 && removedLen > 0;
        // 融合可能な小編集: タイプ(≤2文字・改行なし)/ 削除(≤2文字)
        bool coalescable =
            (pureInsert && insertedLen <= 2 && !insertHasBreak) || (pureDelete && removedLen <= 2);

        if (_open && coalescable && _undo.Count > 0)
        {
            var prev = _undo[^1];
            if (
                pureInsert
                && prev.RemovedLen == 0
                && prev.InsertedLen > 0
                && pos == prev.Pos + prev.InsertedLen
            )
            { // タイプ継続(RootBefore は据え置き)
                _undo[^1] = prev with
                {
                    RootAfter = after,
                    InsertedLen = prev.InsertedLen + insertedLen,
                };
                return;
            }
            if (pureDelete && prev.InsertedLen == 0 && prev.RemovedLen > 0)
            {
                if (pos + removedLen == prev.Pos)
                { // Backspace(逆方向)
                    _undo[^1] = prev with
                    {
                        RootAfter = after,
                        Pos = pos,
                        RemovedLen = prev.RemovedLen + removedLen,
                    };
                    return;
                }
                if (pos == prev.Pos)
                { // Delete前方
                    _undo[^1] = prev with
                    {
                        RootAfter = after,
                        RemovedLen = prev.RemovedLen + removedLen,
                    };
                    return;
                }
            }
        }

        _undo.Add(new Entry(before, after, pos, removedLen, insertedLen));
        _open = coalescable;
    }

    public Entry? PopUndo()
    {
        if (_undo.Count == 0)
            return null;
        var e = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(e);
        _open = false; // Undo後の新規編集は古いエントリへ融合しない
        return e;
    }

    public Entry? PopRedo()
    {
        if (_redo.Count == 0)
            return null;
        var e = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(e);
        _open = false;
        return e;
    }
}
