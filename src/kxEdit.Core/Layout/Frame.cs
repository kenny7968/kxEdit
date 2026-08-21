namespace kxEdit.Core.Layout;

/// <summary>描画オペレータの種別。EditorControl.OnPaint はこれを見て GDI 呼び出しに置換する。</summary>
public enum PaintOpKind
{
    /// <summary>矩形塗り(背景・選択・現在行など)。</summary>
    FillRect,

    /// <summary>文字列描画(本文・行番号・空白グリフなど)。</summary>
    DrawText,

    /// <summary>直線描画。(X,Y) から (X+Width, Y+Height) まで。</summary>
    DrawLine,
}

/// <summary>
/// 描画色(RGB + アルファ)。<see cref="Rgb"/> は 0xRRGGBB。
/// <see cref="Alpha"/> == 0 は「無効/未使用」を意味する運用色として使う(例: CurrentLineBack)。
/// </summary>
public readonly record struct PaintColor(int Rgb, byte Alpha = 255);

/// <summary>
/// 単一の描画オペレータ(不変・値型)。
/// <see cref="PaintOpKind.FillRect"/> / <see cref="PaintOpKind.DrawText"/> は矩形 (X, Y, Width, Height)。
/// <see cref="PaintOpKind.DrawLine"/> は (X, Y) → (X+Width, Y+Height) の直線。
/// </summary>
public readonly record struct PaintOp(
    PaintOpKind Kind,
    int X,
    int Y,
    int Width,
    int Height,
    string? Text = null,
    PaintColor Fore = default,
    PaintColor Back = default
);

/// <summary>
/// 1 フレーム分の描画オペレータ列とクライアント寸法。
/// 順序が z-order(先に塗ったものが後で上書きされる)。
/// </summary>
public sealed record Frame(IReadOnlyList<PaintOp> Ops, int ClientWidth, int ClientHeight);

/// <summary>
/// ビューポート描画のパレット(前景/背景/現在行/選択/行番号/ハイライト枠/空白グリフ)。
/// すべての色を明示的に指定する(既定=default(PaintColor) は使わない=RGB 0 と混同されないように)。
/// </summary>
public sealed record ViewportStyle(
    PaintColor Foreground,
    PaintColor Background,
    PaintColor CurrentLineBack,
    PaintColor SelectionBack,
    PaintColor LineNumberFore,
    PaintColor HighlightOutline,
    PaintColor WhitespaceGlyph
);

/// <summary>
/// 選択/セルハイライトの char 範囲。End は排他。<c>Start &lt;= End</c> を invariant として構築時に検証する
/// (上流バグの silent no-op 化を防ぐ)。
/// </summary>
public readonly record struct SelectionRange
{
    public SelectionRange(int start, int end)
    {
        if (start > end)
            throw new ArgumentException($"Start ({start}) must be <= End ({end}).", nameof(start));
        Start = start;
        End = end;
    }

    public int Start { get; }
    public int End { get; }
}
