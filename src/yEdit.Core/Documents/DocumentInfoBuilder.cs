using System.Text;
using yEdit.Core.Buffers;
using yEdit.Core.Csv;
using yEdit.Core.Text;

namespace yEdit.Core.Documents;

/// <summary>
/// <see cref="DocumentInfo"/> を組み立てる純関数。App 層 DocumentState への直接依存を避け、
/// 必要フィールドを個別引数で受ける(Core は App を参照できないため)。呼び出し側
/// (App の DocumentInfoController)が state.Path / UntitledNumber / Encoding / HasBom /
/// LineEnding を展開して渡す。File I/O には触れず、ファイル属性は
/// <see cref="FileMeta"/> として注入される。
/// </summary>
public static class DocumentInfoBuilder
{
    /// <param name="path">保存済みファイルのフルパス。未保存なら null。</param>
    /// <param name="untitledNumber">無題タブの連番("無題 N" 表示に使う)。path 非 null 時は無視。</param>
    /// <param name="snapshot">文字数カウント元(Editor.CurrentBuffer.Current)。</param>
    /// <param name="encoding">現在のエンコーディング(state.Encoding)。</param>
    /// <param name="hasBom">BOM 有無(state.HasBom)。</param>
    /// <param name="lineEnding">改行種別(state.LineEnding)。</param>
    /// <param name="fileMeta">ファイル属性。未保存 or 取得失敗なら null。</param>
    /// <param name="csv">CSV モード時はパース済みドキュメント、非 CSV モードなら null。</param>
    public static DocumentInfo Build(
        string? path,
        int untitledNumber,
        TextSnapshot snapshot,
        Encoding encoding,
        bool hasBom,
        LineEnding lineEnding,
        FileMeta? fileMeta,
        CsvDocument? csv
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(encoding);

        (FormatKind format, string? extension) = DecideFormat(path);

        return new DocumentInfo(
            DisplayName: DecideDisplayName(path, untitledNumber),
            Format: format,
            Extension: extension,
            Directory: DecideDirectory(path),
            CharacterCount: CharacterCounter.CountVisible(snapshot),
            EncodingLabel: ComposeEncodingLabel(encoding, hasBom),
            LineEnding: lineEnding,
            CreationTime: fileMeta?.CreationTime,
            LastWriteTime: fileMeta?.LastWriteTime,
            FileSizeBytes: fileMeta?.Length,
            Csv: MeasureCsv(csv)
        );
    }

    /// <summary>表示名。保存済みは拡張子を除いたファイル名、未保存は DocumentState.DisplayName と
    /// 同じ規則("無題 N" / 連番未確定なら "無題")。</summary>
    private static string DecideDisplayName(string? path, int untitledNumber)
    {
        if (path is null)
            return untitledNumber > 0 ? $"無題 {untitledNumber}" : "無題";
        // dotfile(".gitignore" 等)は先頭ドットが拡張子区切りと解釈され
        // GetFileNameWithoutExtension が空文字列を返すため、ファイル名全体へフォールバックする
        // (空欄表示になると SR は「ファイル名」の後に何も読まない)。
        string stem = Path.GetFileNameWithoutExtension(path);
        return stem.Length > 0 ? stem : Path.GetFileName(path);
    }

    /// <summary>保存ディレクトリ。未保存 or ルート情報を持たない相対パスは null
    /// (Formatter が「-」に落とす。空文字列のまま渡すと値なしの空欄描画になる)。</summary>
    private static string? DecideDirectory(string? path)
    {
        if (path is null)
            return null;
        string? dir = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(dir) ? null : dir;
    }

    private static (FormatKind, string?) DecideFormat(string? path)
    {
        if (path is null)
            return (FormatKind.Unsaved, null);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".txt" => (FormatKind.Text, ".txt"),
            ".csv" => (FormatKind.Csv, ".csv"),
            ".md" => (FormatKind.Markdown, ".md"),
            "" => (FormatKind.Other, null),
            _ => (FormatKind.Other, ext),
        };
    }

    /// <summary>CSV の (行数, 最大列数)。列数は行ごとの列数の最大値=不揃いでも最も広い行に合わせる。
    /// パース失敗(Ok=false)なら null=CSV 行を出さない。CsvParser は引用符未終端や上限超過でも
    /// 例外ではなく打ち切った部分結果を Ok=false で返すため、そのまま数えると実データと異なる
    /// 寸法を正しい情報として見せてしまう(CSV モード中は F2 セル編集の確定で Ok=false へ
    /// 落ちてもモードは継続する=CsvController の onCommit は読み上げのみ)。</summary>
    private static (int Rows, int Cols)? MeasureCsv(CsvDocument? csv)
    {
        if (csv is null || !csv.Ok)
            return null;
        int cols = 0;
        foreach (var row in csv.Rows)
            cols = Math.Max(cols, row.Count);
        return (csv.Rows.Count, cols);
    }

    private static string ComposeEncodingLabel(Encoding encoding, bool hasBom)
    {
        string baseName = EncodingCatalog.DisplayName(encoding.CodePage);
        return hasBom ? $"{baseName} (BOM付き)" : baseName;
    }
}
