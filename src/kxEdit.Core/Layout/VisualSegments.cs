using kxEdit.Core.Text;

namespace kxEdit.Core.Layout;

/// <summary>視覚セグメント列(<see cref="LineLayout.Wrap"/> 出力)への共通照会を集約する。
/// UiaTextHostAdapter の TryFindVisualSegmentCore / VerticalNavigation.FindSegIndex /
/// NavigationCommands.MoveLineHome(wrap overload) から共有する。</summary>
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

    /// <summary>
    /// 視覚行への「キャレットの着地オフセット」を規約に合う範囲へクランプする(設計書 不変条件 I-1)。
    /// </summary>
    /// <param name="segment">着地先の視覚セグメントの本文(セグメント先頭を 0 とする span)。</param>
    /// <param name="localOffset">セグメント先頭からの着地オフセット([0, segment.Length])。</param>
    /// <param name="isFinalSegment">着地先が論理行の最終セグメントなら true。</param>
    /// <remarks>
    /// <para>
    /// <b>なぜ必要か</b>: <see cref="FindContaining"/> は <c>offsetInLine == segEnd</c> を
    /// 「<b>次の</b>視覚行の先頭」と判定する(最終セグメントのみ例外)。描画側
    /// (<c>EditorControl.ComputeCaretPoint</c>)も同じ規約なので、非最終セグメントの
    /// <c>segEnd</c> へキャレットを着地させると「歩いた視覚行」と「描画される視覚行」が
    /// 1 本ずれる。その結果 ↓ は 1 行飛んで見え、↑ は 1 つ戻した先で同じ <c>segEnd</c> に
    /// 再着地して動かなくなる(2026-08-22 監査 A-5)。
    /// </para>
    /// <para>
    /// クランプ先は最後のコードポイントの<b>先頭</b>=サロゲートペアを割らない。
    /// </para>
    /// <para>
    /// <b>マウス経路には適用しない</b>: ドラッグ選択の端点としては <c>segEnd</c> が正しく、
    /// クランプすると視覚行の最後の 1 文字が選択から漏れる(設計書 §3.2)。
    /// </para>
    /// </remarks>
    public static int ClampLandingOffset(
        ReadOnlySpan<char> segment,
        int localOffset,
        bool isFinalSegment
    )
    {
        if (isFinalSegment || localOffset < segment.Length)
            return localOffset;
        if (segment.Length == 0)
            return 0; // Wrap 契約上、空セグメントは [(0,0)] の 1 本=常に最終。到達しない防御
        return TextBoundary.SnapToCodePointStart(segment, segment.Length - 1);
    }
}
