namespace yEdit.Core.Layout;

/// <summary>視覚セグメント列(<see cref="LineLayout.Wrap"/> 出力)への共通照会を集約する。
/// EditorControl の TryFindVisualSegmentCore / VerticalNavigation.FindSegIndex /
/// NavigationCommands.MoveHomeSmart(wrap overload) から共有する。</summary>
public static class VisualSegments
{
    /// <summary>offsetInLine を含む視覚セグメントの (index, segment) を返す。</summary>
    /// <remarks>
    /// <para>行末位置(=最終 segEnd)は最終セグメント扱い。
    /// 空 segs は非対応=<see cref="LineLayout.Wrap"/> は空入力でも [(0,0)] を返す契約なので
    /// 呼び出し側で空 segs を渡さないことを保証する。</para>
    /// <para><b><see cref="LineLayout.WrapThroughOffset"/> /
    /// <see cref="LineLayout.WrapFirstSegments"/> の打ち切り結果
    /// (<c>ReachedLineEnd == false</c>)を渡してはならない</b> —— 最後の要素が論理行の
    /// 最終セグメントではないため、上記「行末位置は最終セグメント扱い」の分岐が誤発火し、
    /// 行末位置の扱いが 1 セグメントぶんずれる。打ち切り結果は素の
    /// <c>IReadOnlyList&lt;WrapSegment&gt;</c> なのでコンパイルも通ってしまう
    /// (壊れるのは長大行 × 打ち切りのときだけ)。完全な <see cref="LineLayout.Wrap"/> 結果か、
    /// <c>ReachedLineEnd == true</c> の結果だけを渡すこと。</para>
    /// </remarks>
    public static (int Index, WrapSegment Segment) FindContaining(
        IReadOnlyList<WrapSegment> segs,
        int offsetInLine
    )
    {
        for (int i = 0; i < segs.Count; i++)
        {
            int segEnd = segs[i].OffsetInLine + segs[i].Length;
            if (offsetInLine < segEnd || i == segs.Count - 1)
                return (i, segs[i]);
        }
        throw new System.ArgumentException("segs must not be empty", nameof(segs));
    }
}
