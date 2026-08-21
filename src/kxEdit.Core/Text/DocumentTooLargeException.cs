namespace kxEdit.Core.Text;

/// <summary>
/// 文書のロードが上限バイト(既定 int.MaxValue)を超えた際に TextBufferBuilder が投げる例外。
/// FileController の catch 節が握って error prompt を出せるよう、想定内エラーとして
/// InvalidOperationException から独立させる(NullReference 等のロジックバグの伝播原則を維持)。
/// </summary>
public sealed class DocumentTooLargeException : Exception
{
    /// <summary>上限超過を検出した時点の累積バイト数(標準 ctor 経由生成時は 0)。</summary>
    public long AttemptedBytes { get; }

    public DocumentTooLargeException(long attemptedBytes, string message)
        : base(message)
    {
        AttemptedBytes = attemptedBytes;
    }

    // 以下は RCS1194 (Implement exception constructors) 準拠のための標準 ctor。
    // 本文書では TextBufferBuilder からしか投げないため呼び出し実績はないが、
    // 将来 catch→再 throw 等で汎用パターンが必要になった場合の互換性を保つ。
    public DocumentTooLargeException() { }

    public DocumentTooLargeException(string message)
        : base(message) { }

    public DocumentTooLargeException(string message, Exception innerException)
        : base(message, innerException) { }
}
