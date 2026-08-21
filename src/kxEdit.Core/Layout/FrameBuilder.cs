using System.Globalization;
using kxEdit.Core.Buffers;
using kxEdit.Core.Text;

namespace kxEdit.Core.Layout;

/// <summary>
/// 描画結果を <see cref="PaintOp"/> の並びで表現する層(設計書 §2-4)。
/// EditorControl.OnPaint はこの結果を GDI 呼び出しに置換するだけ=xUnit で描画内容を検証できる。
/// </summary>
/// <remarks>
/// 重なり順(<see cref="Frame.Ops"/> の並び):
///   1) 背景全域(<see cref="ViewportStyle.Background"/>)
///   2) 現在行強調(<see cref="ViewportStyle.CurrentLineBack"/>)
///   3) 選択矩形(<see cref="ViewportStyle.SelectionBack"/>・視覚行ごとに分割)
///   4) セルハイライト半透明背景(<see cref="ViewportStyle.HighlightOutline"/> + Alpha=<see cref="HighlightBackAlpha"/>)
///   5) 本文 DrawText(行番号ぶんオフセット済み)
///   6) 空白可視化グリフ(showWhitespace=true・本文と別 op で重ね塗り)
///   7) 行番号(SegmentIndex=0 の視覚行のみ・右寄せ・現在行のみ <see cref="ViewportStyle.Foreground"/>・
///      他は <see cref="ViewportStyle.LineNumberFore"/>)
///   8) セルハイライト枠 DrawLine ×4(<see cref="ViewportStyle.HighlightOutline"/>)
/// </remarks>
internal static class FrameBuilder
{
    /// <summary>行番号の右余白(px)。</summary>
    private const int LineNumberPadding = 4;

    /// <summary>セルハイライト背景のアルファ(半透明)。</summary>
    private const byte HighlightBackAlpha = 60;

    /// <summary>スペースの可視化グリフ(中点)。</summary>
    private const string SpaceGlyph = "·";

    /// <summary>タブの可視化グリフ(右向き矢印)。</summary>
    private const string TabGlyph = "→";

    /// <summary>
    /// フレームを構築する。<paramref name="rows"/> は <see cref="ViewportLayout.Build"/> の結果を渡す想定。
    /// </summary>
    /// <param name="snapshot">描画対象のテキストスナップショット。</param>
    /// <param name="rows">可視視覚行(y 昇順)。</param>
    /// <param name="clientWidth">クライアント幅(px)。</param>
    /// <param name="clientHeight">クライアント高さ(px)。</param>
    /// <param name="lineNumberMarginPx">行番号マージン幅(px)。0 なら非表示・本文 X=0。</param>
    /// <param name="currentLineLogical">現在行(論理行番号)。-1 なら現在行強調なし。</param>
    /// <param name="selection">選択範囲(排他・Start&lt;=End)。null なら選択なし。</param>
    /// <param name="cellHighlight">セルハイライト範囲。null なら非表示。</param>
    /// <param name="showWhitespace">空白可視化を有効にするか。</param>
    /// <param name="style">配色。</param>
    /// <param name="metrics">文字メトリクス。</param>
    public static Frame Build(
        TextSnapshot snapshot,
        IReadOnlyList<VisualRow> rows,
        int clientWidth,
        int clientHeight,
        int lineNumberMarginPx,
        int currentLineLogical,
        SelectionRange? selection,
        SelectionRange? cellHighlight,
        bool showWhitespace,
        ViewportStyle style,
        ICharMetrics metrics
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(metrics);

        // 50 行 × 4 op 相当を初期容量に(YAGNI: 実測は Task 14 のベンチで)。
        var ops = new List<PaintOp>(rows.Count * 4 + 4);
        int lineHeight = metrics.LineHeightPx;
        int bodyX = lineNumberMarginPx;

        // 1) 背景全域
        ops.Add(
            new PaintOp(
                PaintOpKind.FillRect,
                0,
                0,
                clientWidth,
                clientHeight,
                Back: style.Background
            )
        );

        // 2) 現在行強調(該当する視覚行=折り返し ON なら複数あり得る)
        if (currentLineLogical >= 0)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.LogicalLine == currentLineLogical)
                    ops.Add(
                        new PaintOp(
                            PaintOpKind.FillRect,
                            0,
                            row.YPx,
                            clientWidth,
                            lineHeight,
                            Back: style.CurrentLineBack
                        )
                    );
            }
        }

        // 3) 選択(視覚行ごとに矩形分割)
        if (selection is SelectionRange sel && sel.Start < sel.End)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (
                    TryComputeRowRangeRect(
                        snapshot,
                        rows[i],
                        sel,
                        bodyX,
                        lineHeight,
                        metrics,
                        out var x,
                        out var y,
                        out var w,
                        out var h
                    )
                )
                {
                    ops.Add(
                        new PaintOp(PaintOpKind.FillRect, x, y, w, h, Back: style.SelectionBack)
                    );
                }
            }
        }

        // 4) セルハイライト背景(半透明)
        if (cellHighlight is SelectionRange hl && hl.Start < hl.End)
        {
            var hlBack = new PaintColor(style.HighlightOutline.Rgb, HighlightBackAlpha);
            for (int i = 0; i < rows.Count; i++)
            {
                if (
                    TryComputeRowRangeRect(
                        snapshot,
                        rows[i],
                        hl,
                        bodyX,
                        lineHeight,
                        metrics,
                        out var x,
                        out var y,
                        out var w,
                        out var h
                    )
                )
                {
                    ops.Add(new PaintOp(PaintOpKind.FillRect, x, y, w, h, Back: hlBack));
                }
            }
        }

        // 5) 本文
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            string text =
                row.SegmentLength == 0
                    ? string.Empty
                    : snapshot.GetText(row.SegmentStartChar, row.SegmentLength);
            int width = text.Length == 0 ? 0 : metrics.MeasureRun(text);
            ops.Add(
                new PaintOp(
                    PaintOpKind.DrawText,
                    bodyX,
                    row.YPx,
                    width,
                    lineHeight,
                    Text: text,
                    Fore: style.Foreground
                )
            );
        }

        // 6) 空白可視化(スペース/タブごとに個別 DrawText=検証しやすい)
        if (showWhitespace)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.SegmentLength == 0)
                    continue;
                string text = snapshot.GetText(row.SegmentStartChar, row.SegmentLength);
                EmitWhitespaceGlyphs(
                    text,
                    bodyX,
                    row.YPx,
                    lineHeight,
                    style.WhitespaceGlyph,
                    metrics,
                    ops
                );
            }
        }

        // 7) 行番号(SegmentIndex==0 の視覚行のみ・右寄せ)
        // 現在行の行番号は Foreground 色で強調、それ以外は LineNumberFore。
        if (lineNumberMarginPx > 0)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.SegmentIndex != 0)
                    continue;
                string numText = (row.LogicalLine + 1).ToString(CultureInfo.InvariantCulture);
                int textWidth = metrics.MeasureRun(numText);
                int x = lineNumberMarginPx - textWidth - LineNumberPadding;
                if (x < 0)
                    x = 0; // マージン幅が狭すぎる場合の安全網(左端貼り付き)
                PaintColor lnFore =
                    (currentLineLogical >= 0 && row.LogicalLine == currentLineLogical)
                        ? style.Foreground
                        : style.LineNumberFore;
                ops.Add(
                    new PaintOp(
                        PaintOpKind.DrawText,
                        x,
                        row.YPx,
                        textWidth,
                        lineHeight,
                        Text: numText,
                        Fore: lnFore
                    )
                );
            }
        }

        // 8) セルハイライト枠 DrawLine ×4(視覚行ごと)
        if (cellHighlight is SelectionRange hl2 && hl2.Start < hl2.End)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (
                    !TryComputeRowRangeRect(
                        snapshot,
                        rows[i],
                        hl2,
                        bodyX,
                        lineHeight,
                        metrics,
                        out var x,
                        out var y,
                        out var w,
                        out var h
                    )
                )
                    continue;
                // 上辺: (x,y) → (x+w, y)
                ops.Add(
                    new PaintOp(PaintOpKind.DrawLine, x, y, w, 0, Fore: style.HighlightOutline)
                );
                // 下辺: (x, y+h) → (x+w, y+h)
                ops.Add(
                    new PaintOp(PaintOpKind.DrawLine, x, y + h, w, 0, Fore: style.HighlightOutline)
                );
                // 左辺: (x,y) → (x, y+h)
                ops.Add(
                    new PaintOp(PaintOpKind.DrawLine, x, y, 0, h, Fore: style.HighlightOutline)
                );
                // 右辺: (x+w, y) → (x+w, y+h)
                ops.Add(
                    new PaintOp(PaintOpKind.DrawLine, x + w, y, 0, h, Fore: style.HighlightOutline)
                );
            }
        }

        return new Frame(ops, clientWidth, clientHeight);
    }

    /// <summary>
    /// 視覚行と char 範囲 <paramref name="range"/> の交差をピクセル矩形に変換する。
    /// 交差が空なら false(呼び出し側はスキップ)。
    /// </summary>
    private static bool TryComputeRowRangeRect(
        TextSnapshot snapshot,
        VisualRow row,
        SelectionRange range,
        int bodyX,
        int lineHeight,
        ICharMetrics metrics,
        out int x,
        out int y,
        out int w,
        out int h
    )
    {
        int rowStart = row.SegmentStartChar;
        int rowEnd = rowStart + row.SegmentLength;
        int interStart = Math.Max(range.Start, rowStart);
        int interEnd = Math.Min(range.End, rowEnd);
        if (interStart >= interEnd)
        {
            x = 0;
            y = 0;
            w = 0;
            h = 0;
            return false;
        }

        string text = snapshot.GetText(rowStart, row.SegmentLength);
        var span = text.AsSpan();
        int xStart = PixelMapper.OffsetToPx(span, interStart - rowStart, metrics);
        int xEnd = PixelMapper.OffsetToPx(span, interEnd - rowStart, metrics);
        x = bodyX + xStart;
        y = row.YPx;
        w = xEnd - xStart;
        h = lineHeight;
        return true;
    }

    /// <summary>
    /// 1 視覚行分の空白(スペース/タブ)にグリフ DrawText を発行する。
    /// サロゲートペアは 2 コード単位=1 code-point 扱いで前進。
    /// </summary>
    private static void EmitWhitespaceGlyphs(
        string text,
        int bodyX,
        int yPx,
        int lineHeight,
        PaintColor glyphFore,
        ICharMetrics metrics,
        List<PaintOp> ops
    )
    {
        var span = text.AsSpan();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == ' ' || c == '\t')
            {
                string glyph = c == ' ' ? SpaceGlyph : TabGlyph;
                int px = PixelMapper.OffsetToPx(span, i, metrics);
                int glyphWidth = metrics.MeasureRun(glyph);
                ops.Add(
                    new PaintOp(
                        PaintOpKind.DrawText,
                        bodyX + px,
                        yPx,
                        glyphWidth,
                        lineHeight,
                        Text: glyph,
                        Fore: glyphFore
                    )
                );
            }

            // サロゲートペアなら 2 進める(空白判定を安全にスキップ)
            i += TextBoundary.CodePointLengthAt(span, i);
        }
    }
}
