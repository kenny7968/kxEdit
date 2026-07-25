using Xunit;
using yEdit.Core.Documents;
using yEdit.Core.Text;

namespace yEdit.Core.Tests.Documents;

/// <summary>Formatter の出力文字列を固定する。改行は \r\n(WinForms TextBox 期待形式)。
/// 数値は三桁カンマ区切り(InvariantCulture)、日時は yyyy-MM-dd HH:mm:ss。
/// 設計 2026-07-25 §5 のサンプルと逐字一致させる。</summary>
public class DocumentInfoFormatterTests
{
    private const string NL = "\r\n";

    [Fact]
    public void Full_saved_document()
    {
        var info = new DocumentInfo(
            DisplayName: "aaa",
            Format: FormatKind.Text,
            Extension: ".txt",
            Directory: @"d:\hogehoge",
            CharacterCount: 1234,
            EncodingLabel: "UTF-8 (BOM付き)",
            LineEnding: LineEnding.Crlf,
            CreationTime: new DateTime(2026, 7, 25, 10, 30, 15, DateTimeKind.Local),
            LastWriteTime: new DateTime(2026, 7, 25, 12, 45, 0, DateTimeKind.Local),
            FileSizeBytes: 2048,
            Csv: null
        );
        string expected =
            "ファイル名: aaa"
            + NL
            + "形式: テキスト(.txt)"
            + NL
            + @"保存ディレクトリ: d:\hogehoge"
            + NL
            + "文字数: 1,234"
            + NL
            + "文字コード: UTF-8 (BOM付き)"
            + NL
            + "改行コード: CRLF"
            + NL
            + "ファイルサイズ: 2,048 バイト"
            + NL
            + "作成日時: 2026-07-25 10:30:15"
            + NL
            + "更新日時: 2026-07-25 12:45:00";
        Assert.Equal(expected, DocumentInfoFormatter.Format(info));
    }

    [Fact]
    public void Unsaved_document_shows_hyphens()
    {
        var info = new DocumentInfo(
            DisplayName: "無題 1",
            Format: FormatKind.Unsaved,
            Extension: null,
            Directory: null,
            CharacterCount: 0,
            EncodingLabel: "UTF-8",
            LineEnding: LineEnding.Crlf,
            CreationTime: null,
            LastWriteTime: null,
            FileSizeBytes: null,
            Csv: null
        );
        string expected =
            "ファイル名: 無題 1"
            + NL
            + "形式: -"
            + NL
            + "保存ディレクトリ: -"
            + NL
            + "文字数: 0"
            + NL
            + "文字コード: UTF-8"
            + NL
            + "改行コード: CRLF"
            + NL
            + "ファイルサイズ: -"
            + NL
            + "作成日時: -"
            + NL
            + "更新日時: -";
        Assert.Equal(expected, DocumentInfoFormatter.Format(info));
    }

    [Fact]
    public void No_extension_file_labeled_appropriately()
    {
        var info = Info(format: FormatKind.Other, extension: null, directory: @"d:\repo");
        Assert.Contains("形式: その他(拡張子なし)" + NL, DocumentInfoFormatter.Format(info));
    }

    [Fact]
    public void Unknown_extension_labeled()
    {
        var info = Info(format: FormatKind.Other, extension: ".ini", directory: @"d:\etc");
        Assert.Contains("形式: その他(.ini)" + NL, DocumentInfoFormatter.Format(info));
    }

    [Fact]
    public void Csv_format_labeled()
    {
        var info = Info(format: FormatKind.Csv, extension: ".csv");
        Assert.Contains("形式: CSV(.csv)" + NL, DocumentInfoFormatter.Format(info));
    }

    [Fact]
    public void Markdown_format_labeled()
    {
        var info = Info(format: FormatKind.Markdown, extension: ".md");
        Assert.Contains("形式: マークダウン(.md)" + NL, DocumentInfoFormatter.Format(info));
    }

    [Fact]
    public void Csv_mode_appends_csv_line()
    {
        var info = Info(format: FormatKind.Csv, extension: ".csv", csv: (100, 5));
        Assert.EndsWith("CSV: 100 行 × 5 列", DocumentInfoFormatter.Format(info));
    }

    /// <summary>非 CSV モードでは CSV 行を出さない(末尾は更新日時)。</summary>
    [Fact]
    public void Non_csv_mode_omits_csv_line()
    {
        string result = DocumentInfoFormatter.Format(Info());
        Assert.DoesNotContain("CSV: ", result);
        Assert.EndsWith("更新日時: -", result);
    }

    [Fact]
    public void Large_numbers_use_thousand_separator()
    {
        var info = new DocumentInfo(
            DisplayName: "big",
            Format: FormatKind.Text,
            Extension: ".txt",
            Directory: @"d:\",
            CharacterCount: 1234567,
            EncodingLabel: "UTF-8",
            LineEnding: LineEnding.Crlf,
            CreationTime: null,
            LastWriteTime: null,
            FileSizeBytes: 9876543210L,
            Csv: null
        );
        string result = DocumentInfoFormatter.Format(info);
        Assert.Contains("文字数: 1,234,567" + NL, result);
        Assert.Contains("ファイルサイズ: 9,876,543,210 バイト" + NL, result);
    }

    [Theory]
    [InlineData(LineEnding.Crlf, "CRLF")]
    [InlineData(LineEnding.Lf, "LF")]
    [InlineData(LineEnding.Cr, "CR")]
    public void Line_ending_display_names(LineEnding le, string expected)
    {
        string result = DocumentInfoFormatter.Format(Info(lineEnding: le));
        Assert.Contains("改行コード: " + expected + NL, result);
    }

    /// <summary>ファイルサイズ 0 バイトは "-"(取得失敗)と区別して "0 バイト" と出す。</summary>
    [Fact]
    public void Zero_byte_size_is_not_confused_with_missing()
    {
        string result = DocumentInfoFormatter.Format(Info(fileSizeBytes: 0));
        Assert.Contains("ファイルサイズ: 0 バイト" + NL, result);
    }

    /// <summary>既定値をまとめた薄いラッパ(各テストは検証したい項目だけを上書きする)。</summary>
    private static DocumentInfo Info(
        FormatKind format = FormatKind.Unsaved,
        string? extension = null,
        string? directory = null,
        LineEnding lineEnding = LineEnding.Crlf,
        long? fileSizeBytes = null,
        (int Rows, int Cols)? csv = null
    ) =>
        new(
            DisplayName: "x",
            Format: format,
            Extension: extension,
            Directory: directory,
            CharacterCount: 0,
            EncodingLabel: "UTF-8",
            LineEnding: lineEnding,
            CreationTime: null,
            LastWriteTime: null,
            FileSizeBytes: fileSizeBytes,
            Csv: csv
        );
}
