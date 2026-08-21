using kxEdit.Core.Csv;
using Xunit;

namespace kxEdit.Core.Tests.Csv;

public class CsvAnnounceFormatterTests
{
    // ---------- Cell: clean input (regression) ----------

    [Fact]
    public void Cell_reads_content_then_position() =>
        Assert.Equal("田中 2行2列", CsvAnnounceFormatter.Cell("田中", row: 2, col: 2));

    [Fact]
    public void Cell_empty_value_says_blank() =>
        Assert.Equal("ブランク 2行2列", CsvAnnounceFormatter.Cell("", 2, 2));

    [Fact]
    public void Cell_CleanShortValue_UnchangedFormat() =>
        // UIA-M-1 regression pin: 純 ASCII 短文はそのまま "{value} {row}行{col}列"
        Assert.Equal("hello 3行4列", CsvAnnounceFormatter.Cell("hello", row: 3, col: 4));

    [Fact]
    public void Cell_Empty_UnchangedFormat() =>
        // UIA-M-1 regression pin: 空セルは "ブランク {row}行{col}列"
        Assert.Equal("ブランク 1行1列", CsvAnnounceFormatter.Cell("", row: 1, col: 1));

    // ---------- Cell: full-length preservation (2026-07-21 改定) ----------

    [Fact]
    public void Cell_PreservesFullLength_ForLongValues()
    {
        // UIA-M-1 (改定): SR ユーザが長いセル (住所・備考欄) を全文読むのは正当な操作。
        // 500 文字のクリーン文字列は切詰めなしでそのまま発話される。
        var big = new string('あ', 500);
        var actual = CsvAnnounceFormatter.Cell(big, row: 10, col: 5);
        Assert.Equal(big + " 10行5列", actual);
    }

    // ---------- Cell: control character marker ----------

    [Fact]
    public void Cell_ReplacesControlCharsWithMarker_ForRlo()
    {
        // UIA-M-1: RLO (U+202E) を含むセルはマーカー付きで発話される。
        // "evil-‮txt.exe" → OneLine で "evil-txt.exe"、その先頭 60 文字。
        var actual = CsvAnnounceFormatter.Cell("evil-‮txt.exe", row: 2, col: 2);
        Assert.Equal("制御文字を含みます: evil-txt.exe 2行2列", actual);
    }

    [Fact]
    public void Cell_ReplacesControlCharsWithMarker_ForCr()
    {
        // UIA-M-1: CR (U+000D) を含むセルもマーカー付き。OneLine で単一空白へ畳まれる。
        var actual = CsvAnnounceFormatter.Cell("a\rb", row: 2, col: 2);
        Assert.Equal("制御文字を含みます: a b 2行2列", actual);
    }

    [Fact]
    public void Cell_MarkerTruncatesHeadTo60Chars_WhenControlCharPresent()
    {
        // UIA-M-1: マーカー形式では先頭 60 文字だけを載せる (完全な dedupe より攻撃気付き優先)。
        // 制御文字を含むため OneLine(value, 60) で頭 60 文字 (末尾は "…")。
        var big = new string('x', 100) + "\rtail";
        var actual = CsvAnnounceFormatter.Cell(big, row: 1, col: 1);
        var expectedHead = new string('x', 59) + "…"; // 60 文字 (59 + "…")
        Assert.Equal($"制御文字を含みます: {expectedHead} 1行1列", actual);
    }

    // ---------- Header: clean input (regression) ----------

    [Fact]
    public void Header_returns_value_or_blank()
    {
        Assert.Equal("氏名", CsvAnnounceFormatter.Header("氏名"));
        Assert.Equal("ブランク", CsvAnnounceFormatter.Header(""));
    }

    [Fact]
    public void Header_CleanValue_Unchanged() =>
        // UIA-M-1 regression pin
        Assert.Equal("名前", CsvAnnounceFormatter.Header("名前"));

    // ---------- Header: control character marker ----------

    [Fact]
    public void Header_ReplacesControlCharsWithMarker()
    {
        // UIA-M-1: ヘッダも同じルール。制御文字を含めばマーカー、なければ pass-through。
        var actual = CsvAnnounceFormatter.Header("bad-‮hdr");
        Assert.Equal("制御文字を含みます: bad-hdr", actual);
    }

    [Fact]
    public void Header_PreservesFullLength_ForLongValues()
    {
        // UIA-M-1 (改定): ヘッダも切詰めなし。長い列名を全文読む。
        var big = new string('a', 300);
        Assert.Equal(big, CsvAnnounceFormatter.Header(big));
    }
}
