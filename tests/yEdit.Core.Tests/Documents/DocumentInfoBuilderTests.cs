using System.Text;
using Xunit;
using yEdit.Core.Buffers;
using yEdit.Core.Csv;
using yEdit.Core.Documents;
using yEdit.Core.Text;

namespace yEdit.Core.Tests.Documents;

/// <summary>DocumentInfoBuilder の Format 判定・Encoding ラベル生成・null 伝播を固定する。
/// path == null と fileMeta == null は独立に扱う(未保存 vs 属性取得失敗 の意味を持つ)。
/// Builder は App 層 DocumentState を参照できないため、必要フィールドを個別引数で受ける。</summary>
public class DocumentInfoBuilderTests
{
    private static TextSnapshot Snap(string s) => TextBuffer.FromString(s).Current;

    private static TextSnapshot EmptySnap() => Snap("");

    [Fact]
    public void Unsaved_produces_unsaved_format_and_nulls()
    {
        var info = DocumentInfoBuilder.Build(
            path: null,
            untitledNumber: 1,
            snapshot: EmptySnap(),
            encoding: EncodingCatalog.Get(65001),
            hasBom: false,
            lineEnding: LineEnding.Crlf,
            fileMeta: null,
            csv: null
        );
        Assert.Equal("無題 1", info.DisplayName);
        Assert.Equal(FormatKind.Unsaved, info.Format);
        Assert.Null(info.Extension);
        Assert.Null(info.Directory);
        Assert.Equal(0, info.CharacterCount);
        Assert.Equal("UTF-8", info.EncodingLabel);
        Assert.Equal(LineEnding.Crlf, info.LineEnding);
        Assert.Null(info.CreationTime);
        Assert.Null(info.LastWriteTime);
        Assert.Null(info.FileSizeBytes);
        Assert.Null(info.Csv);
    }

    /// <summary>UntitledNumber=0(連番未確定)は DocumentState.DisplayName と同じく「無題」。</summary>
    [Fact]
    public void Unsaved_without_untitled_number_falls_back_to_plain_label()
    {
        var info = Build(path: null, untitledNumber: 0);
        Assert.Equal("無題", info.DisplayName);
    }

    [Fact]
    public void Txt_extension_lowercased_and_detected()
    {
        var info = DocumentInfoBuilder.Build(
            path: @"d:\hogehoge\aaa.TXT",
            untitledNumber: 0,
            snapshot: Snap("hello"),
            encoding: EncodingCatalog.Get(65001),
            hasBom: false,
            lineEnding: LineEnding.Crlf,
            fileMeta: null,
            csv: null
        );
        Assert.Equal("aaa", info.DisplayName);
        Assert.Equal(FormatKind.Text, info.Format);
        Assert.Equal(".txt", info.Extension);
        Assert.Equal(@"d:\hogehoge", info.Directory);
        Assert.Equal(5, info.CharacterCount);
    }

    [Fact]
    public void Csv_extension()
    {
        var info = Build(@"d:\data.csv");
        Assert.Equal(FormatKind.Csv, info.Format);
        Assert.Equal(".csv", info.Extension);
    }

    [Fact]
    public void Md_extension_lowercased()
    {
        var info = Build(@"d:\notes.MD");
        Assert.Equal(FormatKind.Markdown, info.Format);
        Assert.Equal(".md", info.Extension);
    }

    [Fact]
    public void Unknown_extension_becomes_other_with_ext()
    {
        var info = Build(@"d:\config.ini");
        Assert.Equal(FormatKind.Other, info.Format);
        Assert.Equal(".ini", info.Extension);
    }

    [Fact]
    public void No_extension_becomes_other_with_null_ext()
    {
        var info = Build(@"d:\repo\README");
        Assert.Equal("README", info.DisplayName);
        Assert.Equal(FormatKind.Other, info.Format);
        Assert.Null(info.Extension);
        Assert.Equal(@"d:\repo", info.Directory);
    }

    [Fact]
    public void FileMeta_propagates_when_provided()
    {
        var t1 = new DateTime(2026, 7, 25, 10, 30, 15, DateTimeKind.Local);
        var t2 = new DateTime(2026, 7, 25, 12, 45, 0, DateTimeKind.Local);
        var info = Build(@"d:\a.txt", fileMeta: new FileMeta(t1, t2, 2048));
        Assert.Equal(t1, info.CreationTime);
        Assert.Equal(t2, info.LastWriteTime);
        Assert.Equal(2048L, info.FileSizeBytes);
    }

    /// <summary>保存済み(path 非 null)でも fileMeta=null なら日時/サイズは null のまま
    /// =「未保存」と「属性取得失敗」を独立に扱う契約の pin。</summary>
    [Fact]
    public void FileMeta_null_leaves_all_times_null_even_when_saved()
    {
        var info = Build(@"d:\a.txt");
        Assert.Equal(@"d:\", info.Directory); // 前提: path 非 null(=保存済み)
        Assert.Null(info.CreationTime);
        Assert.Null(info.LastWriteTime);
        Assert.Null(info.FileSizeBytes);
    }

    [Fact]
    public void Utf8_with_bom_labelled()
    {
        var info = Build(path: null, hasBom: true);
        Assert.Equal("UTF-8 (BOM付き)", info.EncodingLabel);
    }

    [Fact]
    public void Shift_jis_labelled()
    {
        var info = Build(path: null, encoding: EncodingCatalog.Get(932));
        Assert.Equal("Shift_JIS", info.EncodingLabel);
    }

    [Fact]
    public void Euc_jp_with_bom_flag_still_appends_bom_suffix()
    {
        var info = Build(path: null, encoding: EncodingCatalog.Get(51932), hasBom: true);
        Assert.Equal("EUC-JP (BOM付き)", info.EncodingLabel);
    }

    [Fact]
    public void Csv_mode_fills_csv_field()
    {
        var snap = Snap("a,b,c\r\nd,e,f");
        var info = Build(@"d:\x.csv", snapshot: snap, csv: CsvParser.Parse(snap));
        Assert.Equal((2, 3), info.Csv);
    }

    /// <summary>列数は行ごとの最大値(不揃い CSV でも最も広い行に合わせる)。</summary>
    [Fact]
    public void Csv_columns_is_max_across_ragged_rows()
    {
        var snap = Snap("a,b,c\r\nd,e");
        var info = Build(@"d:\x.csv", snapshot: snap, csv: CsvParser.Parse(snap));
        Assert.Equal((2, 3), info.Csv);
    }

    /// <summary>行ゼロの CsvDocument でも (0, 0) を返す(Max の空シーケンス例外を出さない)。</summary>
    [Fact]
    public void Csv_with_no_rows_reports_zero_dimensions()
    {
        var empty = new CsvDocument(Array.Empty<IReadOnlyList<CsvField>>(), ok: true);
        var info = Build(@"d:\x.csv", csv: empty);
        Assert.Equal((0, 0), info.Csv);
    }

    [Fact]
    public void Non_csv_mode_leaves_csv_field_null()
    {
        var info = Build(@"d:\x.csv", snapshot: Snap("a,b,c"));
        Assert.Null(info.Csv);
    }

    /// <summary>既定値をまとめた薄いラッパ(各テストは検証したい引数だけを上書きする)。</summary>
    private static DocumentInfo Build(
        string? path,
        int untitledNumber = 0,
        TextSnapshot? snapshot = null,
        Encoding? encoding = null,
        bool hasBom = false,
        LineEnding lineEnding = LineEnding.Crlf,
        FileMeta? fileMeta = null,
        CsvDocument? csv = null
    ) =>
        DocumentInfoBuilder.Build(
            path,
            untitledNumber,
            snapshot ?? EmptySnap(),
            encoding ?? EncodingCatalog.Get(65001),
            hasBom,
            lineEnding,
            fileMeta,
            csv
        );
}
