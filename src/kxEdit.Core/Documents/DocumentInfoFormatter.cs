using System.Globalization;
using System.Text;
using kxEdit.Core.Text;

namespace kxEdit.Core.Documents;

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

        sb.Append("ファイル名: ").Append(Safe(info.DisplayName)).Append(NL);
        sb.Append("形式: ").Append(FormatFormat(info.Format, info.Extension)).Append(NL);
        sb.Append("保存ディレクトリ: ").Append(Safe(info.Directory)).Append(NL);
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

    /// <summary>パス由来の攻撃者制御文字列を 1 行表示用に無害化する
    /// (BK-L-4 / CSV-L-5 で確立した横断不変条件=RestoreDialog と同じ扱い)。
    /// 値だけに適用し、ラベルと <see cref="NL"/> は Formatter が持つ信頼済み定数のままにすることで、
    /// 「1 行 = 1 項目」の行構造が値の中の CR/LF で壊されないようにする。
    /// 長さは切り詰めない: 本ダイアログは保存先パスの確認そのものが用途で、
    /// 到達性はダイアログ側の水平スクロールで担保する(一覧項目の RestoreDialog とは要件が違う)。
    /// 無害化の結果が空になる値(null・空文字列・制御文字だけの値)は他の欠損項目と同じ「-」に落とす。
    /// </summary>
    private static string Safe(string? value)
    {
        string s = SanitizeForDisplay.OneLine(value);
        return s.Length > 0 ? s : Missing;
    }

    private static string FormatFormat(FormatKind kind, string? ext) =>
        kind switch
        {
            FormatKind.Text => "テキスト(.txt)",
            FormatKind.Csv => "CSV(.csv)",
            FormatKind.Markdown => "マークダウン(.md)",
            FormatKind.Other => ext is null ? "その他(拡張子なし)" : $"その他({Safe(ext)})",
            FormatKind.Unsaved => Missing, // 未保存は形式を持たない
            _ => Missing,
        };

    private static string FormatSize(long? bytes) =>
        bytes is null ? Missing : $"{bytes.Value.ToString("N0", Culture)} バイト";

    private static string FormatDate(DateTime? dt) =>
        dt is null ? Missing : dt.Value.ToString("yyyy-MM-dd HH:mm:ss", Culture);
}
