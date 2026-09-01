namespace kxEdit.Core.Csv;

/// <summary>CSV フィールドの直列化（RFC 4180・区切りはカンマ固定）。F2 編集確定時に使う。
/// あわせてセル値の EOL 正規化規則（<see cref="NormalizeEols"/>）の持ち主でもある。</summary>
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

    /// <summary>
    /// セル値の改行を LF へ正規化する。<see cref="CsvParser"/> は引用符内の CR / LF を
    /// literal のまま <c>CsvField.Value</c> へ積むため、<c>EditorControl.ConvertEols</c> の
    /// 前後でセル値の見かけが変わる。F2 確定値(<c>CsvCellEditor.Commit</c>)と
    /// パース結果の値を比較する側は、必ずこの規則で揃えてから比較すること
    /// (2026-09-01 設計書 §4.3)。
    /// </summary>
    public static string NormalizeEols(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace("\r\n", "\n").Replace("\r", "\n");
    }
}
