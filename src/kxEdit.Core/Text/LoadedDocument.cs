using System.Text;

namespace kxEdit.Core.Text;

/// <summary>読み込み結果。本文と確定した文字コード・改行・付随情報。</summary>
public sealed class LoadedDocument
{
    public required string Text { get; init; }
    public required Encoding Encoding { get; init; }
    public required bool HasBom { get; init; }
    public required LineEnding LineEnding { get; init; }

    /// <summary>デコード時に U+FFFD（置換文字）が出たか（文字コード取り違えの示唆）。</summary>
    public required bool HadReplacementChar { get; init; }
}
