namespace kxEdit.Core.Csv;

/// <summary>セル移動の方向。</summary>
public enum Direction
{
    Up,
    Down,
    Left,
    Right,
}

/// <summary>
/// CSV の1フィールド。Start/Length は元テキスト上の UTF-16 文字スパン（引用符込み）で、
/// ScintillaHost.SelectCharRange に直結する。Value は引用符を外し "" を " に復元した論理値（読み上げ用）。
/// </summary>
public sealed record CsvField(int Start, int Length, string Value);
