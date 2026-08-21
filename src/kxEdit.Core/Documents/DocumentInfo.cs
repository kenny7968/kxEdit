using kxEdit.Core.Text;

namespace kxEdit.Core.Documents;

/// <summary>
/// 文書情報ダイアログに表示するイミュータブルなデータ。純関数 <see cref="DocumentInfoBuilder"/> が
/// 組み立て、純関数 <see cref="DocumentInfoFormatter"/> が文字列に整形する(File I/O 不関与)。
/// </summary>
/// <param name="DisplayName">拡張子を除いたファイル名("aaa")、未保存なら "無題 1"。</param>
/// <param name="Format">拡張子から判定した形式カテゴリ。</param>
/// <param name="Extension">小文字化済みの拡張子(".txt")。未保存 or 拡張子なしは null。</param>
/// <param name="Directory">保存ディレクトリ(@"d:\hogehoge")。未保存は null。</param>
/// <param name="CharacterCount">可視文字数(Rune 数 - Rune.IsWhiteSpace 該当分)。</param>
/// <param name="EncodingLabel">整形済みの文字コード表示("UTF-8 (BOM付き)" 等)。</param>
/// <param name="LineEnding">改行種別。</param>
/// <param name="CreationTime">作成日時。null=未保存 or 属性取得失敗。</param>
/// <param name="LastWriteTime">更新日時。null=未保存 or 属性取得失敗。</param>
/// <param name="FileSizeBytes">ファイルサイズ(バイト)。null=未保存 or 属性取得失敗。</param>
/// <param name="Csv">CSV モード時の (行数, 最大列数)。null=CSV モードでない。</param>
public sealed record DocumentInfo(
    string DisplayName,
    FormatKind Format,
    string? Extension,
    string? Directory,
    int CharacterCount,
    string EncodingLabel,
    LineEnding LineEnding,
    DateTime? CreationTime,
    DateTime? LastWriteTime,
    long? FileSizeBytes,
    (int Rows, int Cols)? Csv
);
