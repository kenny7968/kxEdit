namespace kxEdit.Core.Csv;

/// <summary>CSV フィールドの直列化（RFC 4180・区切りはカンマ固定）。F2 編集確定時に使う。</summary>
public static class CsvWriter
{
    /// <summary>論理値を CSV フィールド文字列へ直列化する。カンマ・二重引用符・CR・LF を含む場合のみ
    /// 二重引用符で囲み、内部の " を "" にエスケープする。それ以外は素通し。</summary>
    public static string EscapeField(string value)
    {
        bool needsQuote =
            value.Contains(',', System.StringComparison.Ordinal)
            || value.Contains('"', System.StringComparison.Ordinal)
            || value.Contains('\r', System.StringComparison.Ordinal)
            || value.Contains('\n', System.StringComparison.Ordinal);
        return needsQuote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
