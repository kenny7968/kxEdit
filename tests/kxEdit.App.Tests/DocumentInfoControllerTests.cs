using kxEdit.Core.Documents;

namespace kxEdit.App.Tests;

/// <summary>
/// DocumentInfoController の起動導線のスモーク。ダイアログは Show せず、
/// state → IFileMetaProvider → Builder → Formatter と流れて出来上がる文字列を検証する
/// (Show はモーダルで自動テストできないため、文字列生成を BuildText に切り出して対象にする)。
/// Builder/Formatter 自体の網羅は Core.Tests が担保済み=ここでは「配線が生きているか」だけを固定する。
/// </summary>
public class DocumentInfoControllerTests
{
    /// <summary>実 File I/O を通さずに FileMeta を注入する stub(取得の有無と引数を観測する)。</summary>
    private sealed class StubFileMeta : IFileMetaProvider
    {
        public FileMeta? Result;
        public string? LastPath;
        public int Calls;

        public FileMeta? TryGet(string? path)
        {
            Calls++;
            LastPath = path;
            return Result;
        }
    }

    [Fact]
    public void BuildText_returns_null_when_no_active_document() =>
        Sta.Run(() =>
        {
            var (form, docs) = HostForm.CreateWithDocs();
            using (form)
            {
                var ctrl = new DocumentInfoController(docs, new StubFileMeta());
                Assert.Null(docs.Active); // 前提: タブ 0 枚
                Assert.Null(ctrl.BuildText());
            }
        });

    [Fact]
    public void BuildText_unsaved_document_reports_placeholders() =>
        Sta.Run(() =>
        {
            var (form, docs) = HostForm.CreateWithDocs();
            using (form)
            {
                _ = docs.CreateNew(); // 未保存の空タブ
                var stub = new StubFileMeta();
                var ctrl = new DocumentInfoController(docs, stub);

                string? text = ctrl.BuildText();

                Assert.NotNull(text);
                Assert.Contains("ファイル名: 無題", text);
                Assert.Contains("形式: -", text);
                Assert.Contains("保存ディレクトリ: -", text);
                Assert.Contains("文字数: 0", text);
                Assert.Contains("ファイルサイズ: -", text);
                Assert.Contains("作成日時: -", text);
                // 未保存でも provider は呼ばれ、null path を渡されて null を返す(呼び分けを持たない契約)。
                Assert.Equal(1, stub.Calls);
                Assert.Null(stub.LastPath);
            }
        });

    [Fact]
    public void BuildText_uses_state_path_and_injected_meta() =>
        Sta.Run(() =>
        {
            var (form, docs) = HostForm.CreateWithDocs();
            using (form)
            {
                var doc = docs.CreateNew();
                doc.State.Path = @"d:\test\a.txt";
                doc.Editor.Text = "hello";
                var stub = new StubFileMeta
                {
                    Result = new FileMeta(
                        new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local),
                        new DateTime(2026, 6, 7, 8, 9, 10, DateTimeKind.Local),
                        12345
                    ),
                };
                var ctrl = new DocumentInfoController(docs, stub);

                string? text = ctrl.BuildText();

                Assert.Equal(@"d:\test\a.txt", stub.LastPath); // state.Path が provider へ渡る
                Assert.NotNull(text);
                Assert.Contains("ファイル名: a", text);
                Assert.Contains("形式: テキスト(.txt)", text);
                Assert.Contains(@"保存ディレクトリ: d:\test", text);
                Assert.Contains("文字数: 5", text); // スナップショットが Builder へ渡る
                Assert.Contains("ファイルサイズ: 12,345 バイト", text);
                Assert.Contains("作成日時: 2026-01-02 03:04:05", text);
                Assert.Contains("更新日時: 2026-06-07 08:09:10", text);
            }
        });

    /// <summary>state.UntitledNumber が Builder まで届く配線の kill(既定 0 ではない連番から検証する)。</summary>
    [Fact]
    public void BuildText_propagates_untitled_number() =>
        Sta.Run(() =>
        {
            var (form, docs) = HostForm.CreateWithDocs();
            using (form)
            {
                var doc = docs.CreateNew();
                doc.State.UntitledNumber = 3;

                string? text = new DocumentInfoController(docs, new StubFileMeta()).BuildText();

                Assert.NotNull(text);
                Assert.Contains("ファイル名: 無題 3", text);
            }
        });

    /// <summary>state.Encoding / HasBom / LineEnding が Builder まで届く配線の kill
    /// (既定の UTF-8 / BOM なし / CRLF ではない値から検証を始める)。</summary>
    [Fact]
    public void BuildText_propagates_encoding_bom_and_line_ending() =>
        Sta.Run(() =>
        {
            var (form, docs) = HostForm.CreateWithDocs();
            using (form)
            {
                var doc = docs.CreateNew();
                doc.State.Encoding = kxEdit.Core.Text.EncodingCatalog.Get(932);
                doc.State.HasBom = true;
                doc.State.LineEnding = kxEdit.Core.Text.LineEnding.Lf;

                string? text = new DocumentInfoController(docs, new StubFileMeta()).BuildText();

                Assert.NotNull(text);
                Assert.Contains("文字コード: Shift_JIS (BOM付き)", text);
                Assert.Contains("改行コード: LF", text);
            }
        });

    /// <summary>CSV モード中は拡張子に関わらず CSV 行が付く(手動でモードに入れた .txt 等も対象)。</summary>
    [Fact]
    public void BuildText_csv_mode_appends_dimensions_regardless_of_extension() =>
        Sta.Run(() =>
        {
            var (form, docs) = HostForm.CreateWithDocs();
            using (form)
            {
                var doc = docs.CreateNew();
                doc.State.Path = @"d:\x\data.txt"; // .csv ではない=モード側の条件だけで通す
                doc.Editor.Text = "a,b,c\r\nd,e,f";
                doc.State.CsvMode = true;

                string? text = new DocumentInfoController(docs, new StubFileMeta()).BuildText();

                Assert.NotNull(text);
                Assert.EndsWith("CSV: 2 行 × 3 列", text);
            }
        });

    /// <summary>拡張子 .csv なら通常モード(CsvMode=false)でも CSV 行を出す。
    /// 大文字 .CSV でも判定されること(OrdinalIgnoreCase → Ordinal の変異の kill)を含めて固定する。
    /// MainForm.AutoEnterCsvMode の自動進入判定と同じ規則。</summary>
    [Theory]
    [InlineData(@"d:\x\data.csv")]
    [InlineData(@"d:\x\DATA.CSV")]
    public void BuildText_csv_extension_appends_dimensions_even_in_normal_mode(string path) =>
        Sta.Run(() =>
        {
            var (form, docs) = HostForm.CreateWithDocs();
            using (form)
            {
                var doc = docs.CreateNew();
                doc.State.Path = path;
                doc.Editor.Text = "a,b,c\r\nd,e,f";
                doc.State.CsvMode = false; // 通常モード

                string? text = new DocumentInfoController(docs, new StubFileMeta()).BuildText();

                Assert.NotNull(text);
                Assert.EndsWith("CSV: 2 行 × 3 列", text);
            }
        });

    [Fact]
    public void BuildText_non_csv_mode_omits_dimensions() =>
        Sta.Run(() =>
        {
            var (form, docs) = HostForm.CreateWithDocs();
            using (form)
            {
                var doc = docs.CreateNew();
                doc.State.Path = @"d:\x\notes.txt"; // 拡張子も .csv ではない
                doc.Editor.Text = "a,b,c\r\nd,e,f"; // 内容は CSV だがモードは OFF
                doc.State.CsvMode = false;

                string? text = new DocumentInfoController(docs, new StubFileMeta()).BuildText();

                Assert.NotNull(text);
                Assert.DoesNotContain("CSV: ", text);
            }
        });

    /// <summary>provider 未注入なら本番実装(FileMetaProvider.Instance)にフォールバックする。
    /// 実在しないパスでも例外にならず「-」に落ちることまでを固定する。</summary>
    [Fact]
    public void BuildText_without_injected_provider_falls_back_to_real_provider() =>
        Sta.Run(() =>
        {
            var (form, docs) = HostForm.CreateWithDocs();
            using (form)
            {
                var doc = docs.CreateNew();
                doc.State.Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "kxEdit-no-such-file-" + Guid.NewGuid().ToString("N") + ".txt"
                );

                string? text = new DocumentInfoController(docs).BuildText();

                Assert.NotNull(text);
                Assert.Contains("ファイルサイズ: -", text);
                Assert.Contains("作成日時: -", text);
            }
        });
}
