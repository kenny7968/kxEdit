namespace kxEdit.Core.Search;

/// <summary>ヒット範囲。オフセットは UTF-16 文字位置（editor の .NET string index と同一空間）。</summary>
public readonly record struct MatchSpan(int Start, int Length)
{
    /// <summary>ヒット範囲の終端（排他的）。Start + Length に等しい。</summary>
    public int End => Start + Length;
}
