using System.Globalization;
using System.Text;
using yEdit.Core.Text;

namespace yEdit.Core.Documents;

/// <summary><see cref="DocumentInfo"/> を複数行文字列に整形する純関数。
/// 改行は \r\n(WinForms TextBox 期待形式)。数値は三桁カンマ区切り、
/// 日時は yyyy-MM-dd HH:mm:ss(ローカル時刻)。いずれも InvariantCulture で
/// 環境ロケール依存(区切り文字・和暦等)を排する。
/// null な項目は値部分だけを「-」に置換し、項目ラベルは残す。</summary>
public static class DocumentInfoFormatter
{
    private const string NL = "\r\n";
    private const string Missing = "-";
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Format(DocumentInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var sb = new StringBuilder();

        sb.Append("ファイル名: ").Append(info.DisplayName).Append(NL);
        sb.Append("形式: ").Append(FormatFormat(info.Format, info.Extension)).Append(NL);
        sb.Append("保存ディレクトリ: ").Append(info.Directory ?? Missing).Append(NL);
        sb.Append("文字数: ").Append(info.CharacterCount.ToString("N0", Culture)).Append(NL);
        sb.Append("文字コード: ").Append(info.EncodingLabel).Append(NL);
        sb.Append("改行コード: ").Append(info.LineEnding.ToDisplayString()).Append(NL);
        sb.Append("ファイルサイズ: ").Append(FormatSize(info.FileSizeBytes)).Append(NL);
        sb.Append("作成日時: ").Append(FormatDate(info.CreationTime)).Append(NL);
        sb.Append("更新日時: ").Append(FormatDate(info.LastWriteTime));

        if (info.Csv is { } csv)
        {
            sb.Append(NL)
                .Append("CSV: ")
                .Append(csv.Rows.ToString("N0", Culture))
                .Append(" 行 × ")
                .Append(csv.Cols.ToString("N0", Culture))
                .Append(" 列");
        }

        return sb.ToString();
    }

    private static string FormatFormat(FormatKind kind, string? ext) =>
        kind switch
        {
            FormatKind.Text => "テキスト(.txt)",
            FormatKind.Csv => "CSV(.csv)",
            FormatKind.Markdown => "マークダウン(.md)",
            FormatKind.Other => ext is null ? "その他(拡張子なし)" : $"その他({ext})",
            _ => Missing, // FormatKind.Unsaved(未保存は形式を持たない)
        };

    private static string FormatSize(long? bytes) =>
        bytes is null ? Missing : $"{bytes.Value.ToString("N0", Culture)} バイト";

    private static string FormatDate(DateTime? dt) =>
        dt is null ? Missing : dt.Value.ToString("yyyy-MM-dd HH:mm:ss", Culture);
}
