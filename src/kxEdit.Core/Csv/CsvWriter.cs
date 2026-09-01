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
    /// 前後で同じセルの値の見かけが変わる。用途は 2 つ:
    /// (1) F2 確定値を直列化する前に揃える(<c>CsvCellEditor.Commit</c>)。確定値はどことも
    /// 比較されず、揃えた上で直列化されて本文へ書かれるだけである。
    /// (2) 開始時と確定時の <b>2 つのパース結果</b>を「同じセルか」で比べる前に、<b>両辺</b>を
    /// 揃える(2026-09-01 設計書 §4.2 / §4.3)。
    /// 改行の<b>連続は畳まない</b>(1 改行 → 1 LF)。(1) の用途では本文へ書く値そのものを
    /// 作るので、畳むとユーザーが入れたセル内の空行が黙って消える。
    /// </summary>
    public static string NormalizeEols(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace("\r\n", "\n").Replace("\r", "\n");
    }
}
