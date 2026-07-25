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
        // 未保存時のラベルは DocumentState.DisplayName と同じ規則("無題 N" / 連番未確定なら "無題")。
        string displayName =
            path is not null ? Path.GetFileNameWithoutExtension(path)
            : untitledNumber > 0 ? $"無題 {untitledNumber}"
            : "無題";

        return new DocumentInfo(
            DisplayName: displayName,
            Format: format,
            Extension: extension,
            Directory: path is not null ? Path.GetDirectoryName(path) : null,
            CharacterCount: CharacterCounter.CountVisible(snapshot),
            EncodingLabel: ComposeEncodingLabel(encoding, hasBom),
            LineEnding: lineEnding,
            CreationTime: fileMeta?.CreationTime,
            LastWriteTime: fileMeta?.LastWriteTime,
            FileSizeBytes: fileMeta?.Length,
            Csv: MeasureCsv(csv)
        );
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

    /// <summary>CSV の (行数, 最大列数)。列数は行ごとの列数の最大値=不揃いでも最も広い行に合わせる。</summary>
    private static (int Rows, int Cols)? MeasureCsv(CsvDocument? csv)
    {
        if (csv is null)
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
