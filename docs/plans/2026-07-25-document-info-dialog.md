# 文書情報ダイアログ 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans / superpowers:subagent-driven-development to implement this plan task-by-task.

**Goal:** アクティブタブの文書メタ情報(ファイル名/形式/保存ディレクトリ/文字数/文字コード/改行コード/ファイルサイズ/作成日時/更新日時、CSV モード時のみ行×列)を一覧するモーダルダイアログを [ファイル] メニューに追加する。同時に Ctrl+Alt+P の位置照会から「文字数」「選択」を削除する。

**Architecture:** Core に純ロジック(`DocumentInfo` record・`CharacterCounter`・`DocumentInfoBuilder`・`DocumentInfoFormatter`・`FileMeta` struct)、App に薄い Dialog + Controller(`DocumentInfoDialog`・`DocumentInfoController`・`FileMetaProvider`)を追加。File I/O は App 側 `IFileMetaProvider` seam で Core と分離。`PositionFormatter.Format` のシグネチャを縮小(breaking)し `MainForm.AnnouncePosition` から `TextSnapshot.CountCrlfPairs` 呼び出しを撤去。

**Tech Stack:** .NET 9 / C# / WinForms(既存 yEdit と同じ)、xUnit(Core.Tests / App.Tests)

**設計書**: [`2026-07-25-document-info-dialog-design.md`](2026-07-25-document-info-dialog-design.md)

**§3 開発フロー適用**: 中規模変更。タスク毎に仕様レビュー(Task 完了時にコード + テストが仕様通りかを別エージェントで確認)、最終ブランチ 2 パスレビュー(品質 + 脆弱性)。前倒し脆弱性/品質レビューは不要。

---

## Task 1: Core - 型定義(DocumentInfo record + FormatKind enum + FileMeta 値型)

**Files:**
- Create: `src/yEdit.Core/DocumentInfo/DocumentInfo.cs`
- Create: `src/yEdit.Core/DocumentInfo/FormatKind.cs`
- Create: `src/yEdit.Core/DocumentInfo/FileMeta.cs`

**Step 1: `FormatKind.cs` を作成**

```csharp
namespace yEdit.Core.DocumentInfo;

/// <summary>文書情報ダイアログの「形式」項目のカテゴリ。拡張子から Builder が判定する。</summary>
public enum FormatKind
{
    Text,     // .txt
    Csv,      // .csv
    Markdown, // .md
    Other,    // その他の拡張子 or 拡張子なし
    Unsaved,  // Path=null(未保存)
}
```

**Step 2: `FileMeta.cs` を作成**

```csharp
namespace yEdit.Core.DocumentInfo;

/// <summary>ファイル属性(作成/更新/サイズ)の値型。App 側 FileMetaProvider が構築し Builder に注入する。
/// null は「未保存」または「取得失敗」を等しく意味する(Formatter は区別せず「-」表示)。</summary>
public readonly record struct FileMeta(
    DateTime CreationTime,
    DateTime LastWriteTime,
    long Length
);
```

**Step 3: `DocumentInfo.cs` を作成**

```csharp
using yEdit.Core.Text;

namespace yEdit.Core.DocumentInfo;

/// <summary>文書情報ダイアログに表示するイミュータブルなデータ。純関数 Builder が組み立て、
/// 純関数 Formatter が文字列に整形する(File I/O 不関与)。</summary>
public sealed record DocumentInfo(
    string DisplayName,          // "aaa"(拡張子除去済) or "無題 1"
    FormatKind Format,
    string? Extension,           // ".txt"(小文字化済) or null(未保存 or 拡張子なし)
    string? Directory,           // "d:\hogehoge" or null(未保存)
    int CharacterCount,          // Rune 数 - Rune.IsWhiteSpace 除外
    string EncodingLabel,        // "UTF-8 (BOM付き)" 等(整形済)
    LineEnding LineEnding,
    DateTime? CreationTime,      // null=未保存 or 属性取得失敗
    DateTime? LastWriteTime,     // 同上
    long? FileSizeBytes,         // 同上
    (int Rows, int Cols)? Csv    // null=CSVモードでない
);
```

**Step 4: ビルド確認**

Run: `dotnet build src/yEdit.Core/yEdit.Core.csproj -c Debug`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

**Step 5: Commit**

```bash
git add src/yEdit.Core/DocumentInfo/DocumentInfo.cs src/yEdit.Core/DocumentInfo/FormatKind.cs src/yEdit.Core/DocumentInfo/FileMeta.cs
git commit -m "feat(core): DocumentInfo 型定義(record + FormatKind + FileMeta)"
```

**Task 1 レビュー**: 型定義のみのため軽い仕様確認。record のプロパティ順が設計書 §2 と一致していること、nullable の意味付けがコメントに書かれていることを確認。

---

## Task 2: Core - CharacterCounter.CountVisible + 単体テスト

**Files:**
- Create: `src/yEdit.Core/DocumentInfo/CharacterCounter.cs`
- Create: `tests/yEdit.Core.Tests/DocumentInfo/CharacterCounterTests.cs`

**Step 1: 失敗するテストを書く**

`tests/yEdit.Core.Tests/DocumentInfo/CharacterCounterTests.cs` を新規作成:

```csharp
using System.Text;
using Xunit;
using yEdit.Core.Buffers;
using yEdit.Core.DocumentInfo;

namespace yEdit.Core.Tests.DocumentInfo;

/// <summary>CharacterCounter の境界網羅テスト。CRLF/LF/CR、半角/タブ/全角スペース、
/// サロゲートペアの取り扱いを固定する。低頻度・低影響のため文字列一括アロケーションで良いが、
/// 実装は TextSnapshot.CreateReader() 経由で peak O(chunk) を維持する。</summary>
public class CharacterCounterTests
{
    private static TextSnapshot Snap(string s)
    {
        var buf = new TextBuffer();
        if (s.Length > 0) buf.Insert(0, s);
        return buf.Current;
    }

    [Fact]
    public void Empty_document_counts_zero() =>
        Assert.Equal(0, CharacterCounter.CountVisible(Snap("")));

    [Fact]
    public void All_crlf_counts_zero() =>
        Assert.Equal(0, CharacterCounter.CountVisible(Snap("\r\n\r\n\r\n")));

    [Fact]
    public void All_lf_counts_zero() =>
        Assert.Equal(0, CharacterCounter.CountVisible(Snap("\n\n\n")));

    [Fact]
    public void All_cr_counts_zero() =>
        Assert.Equal(0, CharacterCounter.CountVisible(Snap("\r\r\r")));

    [Fact]
    public void Half_width_spaces_only_count_zero() =>
        Assert.Equal(0, CharacterCounter.CountVisible(Snap("     ")));

    [Fact]
    public void Tabs_only_count_zero() =>
        Assert.Equal(0, CharacterCounter.CountVisible(Snap("\t\t\t")));

    [Fact]
    public void Full_width_spaces_only_count_zero() =>
        Assert.Equal(0, CharacterCounter.CountVisible(Snap("\u3000\u3000\u3000")));

    [Fact]
    public void Ascii_letters_counted() =>
        Assert.Equal(3, CharacterCounter.CountVisible(Snap("abc")));

    [Fact]
    public void Whitespace_mixed_in_letters_excluded() =>
        Assert.Equal(3, CharacterCounter.CountVisible(Snap("a b\tc")));

    [Fact]
    public void Line_break_between_content_excluded() =>
        // "行1\r\n行2" = 4 visible chars(改行 2 コードユニットを除外)
        Assert.Equal(4, CharacterCounter.CountVisible(Snap("行1\r\n行2")));

    [Fact]
    public void Surrogate_pair_counts_as_one()
    {
        // "𩸽"(U+29E3D、ホッケ)=UTF-16 で 2 コードユニット、Rune 1 個
        Assert.Equal(1, CharacterCounter.CountVisible(Snap("\uD867\uDE3D")));
    }

    [Fact]
    public void Multiple_surrogate_pairs_with_space()
    {
        // "𩸽 𩸽" = 2 Rune + 1 半角空白除外
        Assert.Equal(2, CharacterCounter.CountVisible(Snap("\uD867\uDE3D \uD867\uDE3D")));
    }

    [Fact]
    public void Unpaired_high_surrogate_skipped()
    {
        // "a" + 未対 high サロゲート単独 → "a" だけカウント
        Assert.Equal(1, CharacterCounter.CountVisible(Snap("a\uD867")));
    }

    [Fact]
    public void Cjk_and_ascii_mixed()
    {
        // "日本語 abc" = 3 CJK + 空白除外 + 3 ASCII = 6
        Assert.Equal(6, CharacterCounter.CountVisible(Snap("日本語 abc")));
    }
}
```

**Step 2: テストを実行して失敗を確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~CharacterCounterTests" -c Debug`
Expected: `Build FAILED` — `CharacterCounter` 未定義

**Step 3: 実装を書く**

`src/yEdit.Core/DocumentInfo/CharacterCounter.cs` を新規作成:

```csharp
using System.Buffers;
using System.Text;
using yEdit.Core.Buffers;

namespace yEdit.Core.DocumentInfo;

/// <summary>
/// 文書全体の「可視文字数」を数える純関数。
/// - サロゲートペア = 1 文字(Rune 単位)
/// - Rune.IsWhiteSpace(Unicode White_Space)に該当する文字を除外
///   = 半角スペース(U+0020) / タブ(U+0009) / CR(U+000D) / LF(U+000A) / 全角スペース(U+3000) / NBSP 等
/// - 不正な UTF-16 シーケンス(未対 high/low サロゲート等)はスキップ
///
/// 設計判断: 位置照会(Ctrl+Alt+P)側の CRLF=1 論理文字(サロゲート=2)とは異なる基準を採る。
/// 本メソッドは「人間に自然な文字数」= CRLF は空白として除外・サロゲート=1(Rune)を優先する。
/// 両者は異なる文脈の指標として意図的に棲み分ける(設計書 §4 参照)。
/// </summary>
public static class CharacterCounter
{
    public static int CountVisible(TextSnapshot snap)
    {
        ArgumentNullException.ThrowIfNull(snap);
        using var reader = snap.CreateReader();
        int count = 0;
        Span<char> buf = stackalloc char[2];
        int ch;
        while ((ch = reader.Read()) >= 0)
        {
            buf[0] = (char)ch;
            int len = 1;
            if (char.IsHighSurrogate(buf[0]))
            {
                int ch2 = reader.Read();
                if (ch2 < 0) break;           // 未対 high → EOF まで来た → 破棄
                buf[1] = (char)ch2;
                len = 2;
            }
            var status = Rune.DecodeFromUtf16(buf[..len], out Rune rune, out _);
            if (status == OperationStatus.Done && !Rune.IsWhiteSpace(rune))
                count++;
        }
        return count;
    }
}
```

**Step 4: テストが通ることを確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~CharacterCounterTests" -c Debug`
Expected: `Passed! - Failed: 0, Passed: 14, Skipped: 0`

**Step 5: Commit**

```bash
git add src/yEdit.Core/DocumentInfo/CharacterCounter.cs tests/yEdit.Core.Tests/DocumentInfo/CharacterCounterTests.cs
git commit -m "feat(core): CharacterCounter.CountVisible(Rune 単位+IsWhiteSpace 除外)"
```

**Task 2 レビュー**: (仕様レビュー) `Rune.IsWhiteSpace` の除外範囲が設計書 §4 と一致しているか、境界テスト(不正サロゲート・全角空白・CRLF)が漏れていないか。

---

## Task 3: Core - DocumentInfoBuilder + 単体テスト

**Files:**
- Create: `src/yEdit.Core/DocumentInfo/DocumentInfoBuilder.cs`
- Create: `tests/yEdit.Core.Tests/DocumentInfo/DocumentInfoBuilderTests.cs`

**Step 1: 失敗するテストを書く**

`tests/yEdit.Core.Tests/DocumentInfo/DocumentInfoBuilderTests.cs`:

```csharp
using System.Text;
using Xunit;
using yEdit.Core.Buffers;
using yEdit.Core.Csv;
using yEdit.Core.DocumentInfo;
using yEdit.Core.Text;

namespace yEdit.Core.Tests.DocumentInfo;

/// <summary>DocumentInfoBuilder の Format 判定・Encoding ラベル生成・null 伝播を固定する。
/// path == null と FileMeta == null は独立に扱う(未保存 vs 属性取得失敗 の意味を持つ)。</summary>
public class DocumentInfoBuilderTests
{
    /// <summary>DocumentInfoBuilder のシグネチャに合わせた最小の DocumentState 値オブジェクト。
    /// DocumentState を Core から直参照できないため、Builder は必要フィールドを個別引数で受ける。</summary>
    private static TextSnapshot EmptySnap() => new TextBuffer().Current;

    private static TextSnapshot Snap(string s)
    {
        var buf = new TextBuffer();
        if (s.Length > 0) buf.Insert(0, s);
        return buf.Current;
    }

    [Fact]
    public void Unsaved_produces_unsaved_format_and_nulls()
    {
        var info = DocumentInfoBuilder.Build(
            path: null,
            untitledNumber: 1,
            snapshot: EmptySnap(),
            encoding: Encoding.UTF8,
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
        Assert.Null(info.CreationTime);
        Assert.Null(info.LastWriteTime);
        Assert.Null(info.FileSizeBytes);
        Assert.Null(info.Csv);
    }

    [Fact]
    public void Txt_extension_lowercased_and_detected()
    {
        var info = DocumentInfoBuilder.Build(
            path: @"d:\hogehoge\aaa.TXT",
            untitledNumber: 0,
            snapshot: Snap("hello"),
            encoding: Encoding.UTF8,
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
        var info = DocumentInfoBuilder.Build(
            path: @"d:\data.csv", untitledNumber: 0, snapshot: EmptySnap(),
            encoding: Encoding.UTF8, hasBom: false, lineEnding: LineEnding.Lf,
            fileMeta: null, csv: null);
        Assert.Equal(FormatKind.Csv, info.Format);
        Assert.Equal(".csv", info.Extension);
    }

    [Fact]
    public void Md_extension()
    {
        var info = DocumentInfoBuilder.Build(
            path: @"d:\notes.MD", untitledNumber: 0, snapshot: EmptySnap(),
            encoding: Encoding.UTF8, hasBom: false, lineEnding: LineEnding.Crlf,
            fileMeta: null, csv: null);
        Assert.Equal(FormatKind.Markdown, info.Format);
        Assert.Equal(".md", info.Extension);
    }

    [Fact]
    public void Unknown_extension_becomes_other_with_ext()
    {
        var info = DocumentInfoBuilder.Build(
            path: @"d:\config.ini", untitledNumber: 0, snapshot: EmptySnap(),
            encoding: Encoding.UTF8, hasBom: false, lineEnding: LineEnding.Crlf,
            fileMeta: null, csv: null);
        Assert.Equal(FormatKind.Other, info.Format);
        Assert.Equal(".ini", info.Extension);
    }

    [Fact]
    public void No_extension_becomes_other_with_null_ext()
    {
        var info = DocumentInfoBuilder.Build(
            path: @"d:\repo\README", untitledNumber: 0, snapshot: EmptySnap(),
            encoding: Encoding.UTF8, hasBom: false, lineEnding: LineEnding.Crlf,
            fileMeta: null, csv: null);
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
        var info = DocumentInfoBuilder.Build(
            path: @"d:\a.txt", untitledNumber: 0, snapshot: EmptySnap(),
            encoding: Encoding.UTF8, hasBom: false, lineEnding: LineEnding.Crlf,
            fileMeta: new FileMeta(t1, t2, 2048), csv: null);
        Assert.Equal(t1, info.CreationTime);
        Assert.Equal(t2, info.LastWriteTime);
        Assert.Equal(2048L, info.FileSizeBytes);
    }

    [Fact]
    public void FileMeta_null_leaves_all_times_null()
    {
        var info = DocumentInfoBuilder.Build(
            path: @"d:\a.txt", untitledNumber: 0, snapshot: EmptySnap(),
            encoding: Encoding.UTF8, hasBom: false, lineEnding: LineEnding.Crlf,
            fileMeta: null, csv: null);
        Assert.Null(info.CreationTime);
        Assert.Null(info.LastWriteTime);
        Assert.Null(info.FileSizeBytes);
    }

    [Fact]
    public void Utf8_with_bom_labelled()
    {
        var info = DocumentInfoBuilder.Build(
            path: null, untitledNumber: 1, snapshot: EmptySnap(),
            encoding: Encoding.UTF8, hasBom: true, lineEnding: LineEnding.Crlf,
            fileMeta: null, csv: null);
        Assert.Equal("UTF-8 (BOM付き)", info.EncodingLabel);
    }

    [Fact]
    public void Shift_jis_labelled()
    {
        EncodingCatalog.EnsureRegistered();
        var info = DocumentInfoBuilder.Build(
            path: null, untitledNumber: 1, snapshot: EmptySnap(),
            encoding: Encoding.GetEncoding(932), hasBom: false, lineEnding: LineEnding.Crlf,
            fileMeta: null, csv: null);
        Assert.Equal("Shift_JIS", info.EncodingLabel);
    }

    [Fact]
    public void Csv_mode_fills_csv_field()
    {
        var doc = CsvParser.Parse(Snap("a,b,c\r\nd,e,f"));
        var info = DocumentInfoBuilder.Build(
            path: @"d:\x.csv", untitledNumber: 0, snapshot: Snap("a,b,c\r\nd,e,f"),
            encoding: Encoding.UTF8, hasBom: false, lineEnding: LineEnding.Crlf,
            fileMeta: null, csv: doc);
        Assert.NotNull(info.Csv);
        Assert.Equal((2, 3), info.Csv);
    }

    [Fact]
    public void Non_csv_mode_leaves_csv_field_null()
    {
        var info = DocumentInfoBuilder.Build(
            path: @"d:\x.csv", untitledNumber: 0, snapshot: Snap("a,b,c"),
            encoding: Encoding.UTF8, hasBom: false, lineEnding: LineEnding.Crlf,
            fileMeta: null, csv: null);
        Assert.Null(info.Csv);
    }
}
```

**Step 2: テストを実行して失敗を確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~DocumentInfoBuilderTests" -c Debug`
Expected: `Build FAILED` — `DocumentInfoBuilder` 未定義

**Step 3: 実装を書く**

`src/yEdit.Core/DocumentInfo/DocumentInfoBuilder.cs` を新規作成:

```csharp
using System.IO;
using System.Text;
using yEdit.Core.Buffers;
using yEdit.Core.Csv;
using yEdit.Core.Text;

namespace yEdit.Core.DocumentInfo;

/// <summary>
/// DocumentInfo を組み立てる純関数。DocumentState への直接依存を避け、必要フィールドを個別引数で受ける
/// (Core は App 層 DocumentState を参照できないため)。呼び出し側 (App の DocumentInfoController) が
/// state.Path / state.UntitledNumber / state.Encoding / state.HasBom / state.LineEnding を展開して渡す。
/// </summary>
public static class DocumentInfoBuilder
{
    /// <param name="path">保存済ファイルのフルパス。未保存なら null。</param>
    /// <param name="untitledNumber">未保存タブの連番("無題 N" 表示に使用)。path 非 null 時は無視。</param>
    /// <param name="snapshot">文字数カウント元。CurrentBuffer.Current を渡す。</param>
    /// <param name="encoding">現在のエンコーディング(state.Encoding)。</param>
    /// <param name="hasBom">BOM 有無(state.HasBom)。</param>
    /// <param name="lineEnding">改行種別(state.LineEnding)。</param>
    /// <param name="fileMeta">ファイル属性。未保存 or 取得失敗なら null。</param>
    /// <param name="csv">CSV モード時は ParseCsv 済ドキュメント、非 CSV モードなら null。</param>
    public static DocumentInfo Build(
        string? path,
        int untitledNumber,
        TextSnapshot snapshot,
        Encoding encoding,
        bool hasBom,
        LineEnding lineEnding,
        FileMeta? fileMeta,
        CsvDocument? csv)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(encoding);

        (FormatKind format, string? extension) = DecideFormat(path);
        string displayName =
            path is not null ? Path.GetFileNameWithoutExtension(path)
            : untitledNumber > 0 ? $"無題 {untitledNumber}"
            : "無題";
        string? directory = path is not null ? Path.GetDirectoryName(path) : null;
        int charCount = CharacterCounter.CountVisible(snapshot);
        string encodingLabel = ComposeEncodingLabel(encoding, hasBom);
        (int, int)? csvDim = csv is null ? null : (csv.Records.Count, csv.MaxColumns);

        return new DocumentInfo(
            DisplayName: displayName,
            Format: format,
            Extension: extension,
            Directory: directory,
            CharacterCount: charCount,
            EncodingLabel: encodingLabel,
            LineEnding: lineEnding,
            CreationTime: fileMeta?.CreationTime,
            LastWriteTime: fileMeta?.LastWriteTime,
            FileSizeBytes: fileMeta?.Length,
            Csv: csvDim
        );
    }

    private static (FormatKind, string?) DecideFormat(string? path)
    {
        if (path is null) return (FormatKind.Unsaved, null);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".txt" => (FormatKind.Text, ".txt"),
            ".csv" => (FormatKind.Csv,  ".csv"),
            ".md"  => (FormatKind.Markdown, ".md"),
            ""     => (FormatKind.Other, null),
            _      => (FormatKind.Other, ext),
        };
    }

    private static string ComposeEncodingLabel(Encoding encoding, bool hasBom)
    {
        string baseName = EncodingCatalog.DisplayName(encoding.CodePage);
        return hasBom ? $"{baseName} (BOM付き)" : baseName;
    }
}
```

**Note**: `CsvDocument.Records.Count` と `CsvDocument.MaxColumns` の API 名は実装確認が必要(存在しなければ既存 API に合わせる)。Task 3 実装時に `src/yEdit.Core/Csv/CsvDocument.cs` を確認して調整すること。

**Step 4: テストが通ることを確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~DocumentInfoBuilderTests" -c Debug`
Expected: `Passed! - Failed: 0, Passed: 12, Skipped: 0`

**Step 5: Commit**

```bash
git add src/yEdit.Core/DocumentInfo/DocumentInfoBuilder.cs tests/yEdit.Core.Tests/DocumentInfo/DocumentInfoBuilderTests.cs
git commit -m "feat(core): DocumentInfoBuilder(拡張子/Encoding/CSV 判定)"
```

**Task 3 レビュー**: `DecideFormat` の switch が設計書 §3 と一致、CsvDocument のプロパティ名が実 API と合っている、`Path.GetFileNameWithoutExtension` の挙動(`README` → `README`)がテストで固定されている。

---

## Task 4: Core - DocumentInfoFormatter + 単体テスト

**Files:**
- Create: `src/yEdit.Core/DocumentInfo/DocumentInfoFormatter.cs`
- Create: `tests/yEdit.Core.Tests/DocumentInfo/DocumentInfoFormatterTests.cs`

**Step 1: 失敗するテストを書く**

`tests/yEdit.Core.Tests/DocumentInfo/DocumentInfoFormatterTests.cs`:

```csharp
using Xunit;
using yEdit.Core.DocumentInfo;
using yEdit.Core.Text;

namespace yEdit.Core.Tests.DocumentInfo;

/// <summary>Formatter の出力文字列を固定する。改行は \r\n(WinForms TextBox 期待形式)。
/// 数値は三桁カンマ区切り(InvariantCulture)、日時は yyyy-MM-dd HH:mm:ss。</summary>
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
            CreationTime: new DateTime(2026, 7, 25, 10, 30, 15),
            LastWriteTime: new DateTime(2026, 7, 25, 12, 45, 0),
            FileSizeBytes: 2048,
            Csv: null
        );
        string expected =
            "ファイル名: aaa" + NL +
            "形式: テキスト(.txt)" + NL +
            "保存ディレクトリ: d:\\hogehoge" + NL +
            "文字数: 1,234" + NL +
            "文字コード: UTF-8 (BOM付き)" + NL +
            "改行コード: CRLF" + NL +
            "ファイルサイズ: 2,048 バイト" + NL +
            "作成日時: 2026-07-25 10:30:15" + NL +
            "更新日時: 2026-07-25 12:45:00";
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
            "ファイル名: 無題 1" + NL +
            "形式: -" + NL +
            "保存ディレクトリ: -" + NL +
            "文字数: 0" + NL +
            "文字コード: UTF-8" + NL +
            "改行コード: CRLF" + NL +
            "ファイルサイズ: -" + NL +
            "作成日時: -" + NL +
            "更新日時: -";
        Assert.Equal(expected, DocumentInfoFormatter.Format(info));
    }

    [Fact]
    public void No_extension_file_labeled_appropriately()
    {
        var info = new DocumentInfo(
            DisplayName: "README",
            Format: FormatKind.Other,
            Extension: null,
            Directory: @"d:\repo",
            CharacterCount: 100,
            EncodingLabel: "UTF-8",
            LineEnding: LineEnding.Lf,
            CreationTime: null,
            LastWriteTime: null,
            FileSizeBytes: null,
            Csv: null
        );
        string result = DocumentInfoFormatter.Format(info);
        Assert.Contains("形式: その他(拡張子なし)" + NL, result);
    }

    [Fact]
    public void Unknown_extension_labeled()
    {
        var info = new DocumentInfo(
            DisplayName: "config",
            Format: FormatKind.Other,
            Extension: ".ini",
            Directory: @"d:\etc",
            CharacterCount: 50,
            EncodingLabel: "UTF-8",
            LineEnding: LineEnding.Crlf,
            CreationTime: null,
            LastWriteTime: null,
            FileSizeBytes: null,
            Csv: null
        );
        string result = DocumentInfoFormatter.Format(info);
        Assert.Contains("形式: その他(.ini)" + NL, result);
    }

    [Fact]
    public void Csv_mode_appends_csv_line()
    {
        var info = new DocumentInfo(
            DisplayName: "data",
            Format: FormatKind.Csv,
            Extension: ".csv",
            Directory: @"d:\x",
            CharacterCount: 30,
            EncodingLabel: "UTF-8",
            LineEnding: LineEnding.Crlf,
            CreationTime: null,
            LastWriteTime: null,
            FileSizeBytes: null,
            Csv: (100, 5)
        );
        string result = DocumentInfoFormatter.Format(info);
        Assert.EndsWith("CSV: 100 行 × 5 列", result);
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
        var info = new DocumentInfo("x", FormatKind.Unsaved, null, null, 0, "UTF-8", le,
            null, null, null, null);
        Assert.Contains($"改行コード: {expected}" + NL, DocumentInfoFormatter.Format(info));
    }
}
```

**Step 2: テストを実行して失敗を確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~DocumentInfoFormatterTests" -c Debug`
Expected: `Build FAILED`

**Step 3: 実装を書く**

`src/yEdit.Core/DocumentInfo/DocumentInfoFormatter.cs` を新規作成:

```csharp
using System.Globalization;
using System.Text;
using yEdit.Core.Text;

namespace yEdit.Core.DocumentInfo;

/// <summary>DocumentInfo を複数行文字列に整形する純関数。
/// 改行は \r\n(WinForms TextBox 期待形式)。数値は三桁カンマ区切り(InvariantCulture)、
/// 日時は yyyy-MM-dd HH:mm:ss(ローカル時刻・InvariantCulture)。</summary>
public static class DocumentInfoFormatter
{
    private const string NL = "\r\n";
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Format(DocumentInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var sb = new StringBuilder();

        sb.Append("ファイル名: ").Append(info.DisplayName).Append(NL);
        sb.Append("形式: ").Append(FormatFormat(info.Format, info.Extension)).Append(NL);
        sb.Append("保存ディレクトリ: ").Append(info.Directory ?? "-").Append(NL);
        sb.Append("文字数: ").Append(info.CharacterCount.ToString("N0", Culture)).Append(NL);
        sb.Append("文字コード: ").Append(info.EncodingLabel).Append(NL);
        sb.Append("改行コード: ").Append(info.LineEnding.ToDisplayString()).Append(NL);
        sb.Append("ファイルサイズ: ").Append(FormatSize(info.FileSizeBytes)).Append(NL);
        sb.Append("作成日時: ").Append(FormatDate(info.CreationTime)).Append(NL);
        sb.Append("更新日時: ").Append(FormatDate(info.LastWriteTime));

        if (info.Csv is { } csv)
            sb.Append(NL).Append("CSV: ").Append(csv.Rows).Append(" 行 × ")
              .Append(csv.Cols).Append(" 列");

        return sb.ToString();
    }

    private static string FormatFormat(FormatKind kind, string? ext) => kind switch
    {
        FormatKind.Text     => "テキスト(.txt)",
        FormatKind.Csv      => "CSV(.csv)",
        FormatKind.Markdown => "マークダウン(.md)",
        FormatKind.Other    => ext is null ? "その他(拡張子なし)" : $"その他({ext})",
        FormatKind.Unsaved  => "-",
        _                   => "-",
    };

    private static string FormatSize(long? bytes) =>
        bytes is null ? "-" : $"{bytes.Value.ToString("N0", Culture)} バイト";

    private static string FormatDate(DateTime? dt) =>
        dt is null ? "-" : dt.Value.ToString("yyyy-MM-dd HH:mm:ss", Culture);
}
```

**Step 4: テストが通ることを確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~DocumentInfoFormatterTests" -c Debug`
Expected: `Passed! - Failed: 0, Passed: 9, Skipped: 0`

**Step 5: Commit**

```bash
git add src/yEdit.Core/DocumentInfo/DocumentInfoFormatter.cs tests/yEdit.Core.Tests/DocumentInfo/DocumentInfoFormatterTests.cs
git commit -m "feat(core): DocumentInfoFormatter(出力文字列固定)"
```

**Task 4 レビュー**: 出力文字列が設計書 §5 サンプルと逐字一致、null → "-" の分岐が全対象項目で正しい、`InvariantCulture` を使い環境依存を排している。

---

## Task 5: App - IFileMetaProvider + FileMetaProvider

**Files:**
- Create: `src/yEdit.App/FileMetaProvider.cs`

**Step 1: 実装を書く**(単体テストなし・実 File I/O の薄いラッパで Task 7 で seam 経由テスト)

`src/yEdit.App/FileMetaProvider.cs` を新規作成:

```csharp
using yEdit.Core.DocumentInfo;

namespace yEdit.App;

/// <summary>path から FileMeta を取り出す seam。実 File I/O をテストから分離するために用意する
/// (App.Tests では stub 差し替え)。ネットワーク遮断・権限拒否・ファイル削除中の race 等の例外は
/// 握って null を返す(致命度低・Formatter が "-" に落とす)。</summary>
public interface IFileMetaProvider
{
    FileMeta? TryGet(string? path);
}

public sealed class FileMetaProvider : IFileMetaProvider
{
    public static readonly FileMetaProvider Instance = new();

    public FileMeta? TryGet(string? path)
    {
        if (path is null) return null;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            return new FileMeta(fi.CreationTime, fi.LastWriteTime, fi.Length);
        }
        catch
        {
            return null;
        }
    }
}
```

**Step 2: ビルド確認**

Run: `dotnet build src/yEdit.App/yEdit.App.csproj -c Debug`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

**Step 3: Commit**

```bash
git add src/yEdit.App/FileMetaProvider.cs
git commit -m "feat(app): FileMetaProvider(File I/O 分離 seam)"
```

**Task 5 レビュー**: catch-all は本設計で意図的(致命度低・Formatter が "-" に落とす)。ログ出力の要否は「不要」と設計書 §8 で判断済み。

---

## Task 6: App - DocumentInfoDialog

**Files:**
- Create: `src/yEdit.App/DocumentInfoDialog.cs`

**Step 1: 実装を書く**(GoToLineDialog パターン踏襲)

`src/yEdit.App/DocumentInfoDialog.cs` を新規作成:

```csharp
namespace yEdit.App;

/// <summary>文書情報を表示するモーダルダイアログ。
/// 単一の Multiline/ReadOnly TextBox に全項目を \r\n 区切りで表示し、
/// SR は TextBox フォーカス時に内容全体を通し読み、以降キャレット移動で行/文字単位に読める。
/// GoToLineDialog パターン踏襲。</summary>
public sealed class DocumentInfoDialog : Form
{
    private readonly TextBox _textBox;

    public DocumentInfoDialog(string text)
    {
        Text = "文書情報";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(480, 280);

        _textBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Text = text,
            TabStop = true,
        };

        var closeBtn = new Button
        {
            Text = "閉じる(&C)",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
        };

        var buttonPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Padding = new Padding(6),
        };
        buttonPanel.Controls.Add(closeBtn);

        Controls.Add(_textBox);         // Dock=Fill を Controls に先に追加すると下側 panel と衝突するため
        Controls.Add(buttonPanel);      // panel を後追加で正しい順序
        // WinForms は逆順 z-order のため Fill は先追加、Bottom は後追加が正解。

        AcceptButton = closeBtn;   // Enter で閉じる
        CancelButton = closeBtn;   // Esc で閉じる
    }
}
```

**Step 2: ビルド確認**

Run: `dotnet build src/yEdit.App/yEdit.App.csproj -c Debug`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

**Step 3: Commit**

```bash
git add src/yEdit.App/DocumentInfoDialog.cs
git commit -m "feat(app): DocumentInfoDialog(ReadOnly TextBox + 閉じるボタン)"
```

**Task 6 レビュー**: `Controls.Add` 順序が正しい(Dock=Fill を先、Dock=Bottom を後)、Enter/Esc 両方で閉じる(AcceptButton=CancelButton=closeBtn)、閉じるボタンにアクセラレータ(&C)が付いている。

---

## Task 7: App - DocumentInfoController + 単体テスト

**Files:**
- Create: `src/yEdit.App/DocumentInfoController.cs`
- Create: `tests/yEdit.App.Tests/DocumentInfoControllerTests.cs`

**Step 1: 失敗するテストを書く**

`tests/yEdit.App.Tests/DocumentInfoControllerTests.cs`:

```csharp
using System.Text;
using Xunit;
using yEdit.App;
using yEdit.Core.DocumentInfo;
using yEdit.Core.Text;

namespace yEdit.App.Tests;

/// <summary>DocumentInfoController の起動導線の smoke。Dialog は Show せず、
/// Builder/Formatter に流れるデータ(state → FileMetaProvider → Build → Format 文字列)を検証する。
/// Show 直前で string を作る public API を Controller に切り出してテスト対象にする。</summary>
public class DocumentInfoControllerTests
{
    private sealed class StubFileMeta : IFileMetaProvider
    {
        public FileMeta? Result;
        public string? LastPath;
        public FileMeta? TryGet(string? path) { LastPath = path; return Result; }
    }

    [Fact]
    public void Build_returns_null_when_no_active_document()
    {
        var docs = new DocumentManager(_ => TestHelpers.CreateEditorForTests());
        var ctrl = new DocumentInfoController(docs, new StubFileMeta());
        Assert.Null(ctrl.BuildText());          // Active が null → null 戻り
    }

    [Fact]
    public void Build_wires_stub_meta_and_produces_formatted_text()
    {
        var docs = new DocumentManager(_ => TestHelpers.CreateEditorForTests());
        docs.OpenNew();                          // 未保存の空タブ 1 つ
        var stub = new StubFileMeta();           // path=null なので TryGet は呼ばれない(未保存)
        var ctrl = new DocumentInfoController(docs, stub);

        string? text = ctrl.BuildText();

        Assert.NotNull(text);
        Assert.Contains("ファイル名: 無題", text);
        Assert.Contains("形式: -", text);
        Assert.Contains("保存ディレクトリ: -", text);
        Assert.Contains("文字数: 0", text);
        Assert.Contains("作成日時: -", text);
    }

    [Fact]
    public void Build_uses_stub_meta_when_path_present()
    {
        var docs = new DocumentManager(_ => TestHelpers.CreateEditorForTests());
        var doc = docs.OpenNew();
        doc.State.Path = @"d:\test\a.txt";
        var stub = new StubFileMeta
        {
            Result = new FileMeta(
                new DateTime(2026, 1, 2, 3, 4, 5),
                new DateTime(2026, 6, 7, 8, 9, 10),
                12345)
        };
        var ctrl = new DocumentInfoController(docs, stub);

        string? text = ctrl.BuildText();

        Assert.Equal(@"d:\test\a.txt", stub.LastPath);
        Assert.NotNull(text);
        Assert.Contains("ファイル名: a", text);
        Assert.Contains("形式: テキスト(.txt)", text);
        Assert.Contains("保存ディレクトリ: d:\\test", text);
        Assert.Contains("ファイルサイズ: 12,345 バイト", text);
        Assert.Contains("作成日時: 2026-01-02 03:04:05", text);
        Assert.Contains("更新日時: 2026-06-07 08:09:10", text);
    }
}
```

**Note**: `TestHelpers.CreateEditorForTests()` と `DocumentManager.OpenNew()` の API 名は実コードに合わせて調整。既存 `DocumentManagerTests.cs` の書き方を参考にする(Task 7 実装時に確認)。

**Step 2: テストを実行して失敗を確認**

Run: `dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj --filter "FullyQualifiedName~DocumentInfoControllerTests" -c Debug`
Expected: `Build FAILED` — `DocumentInfoController` 未定義

**Step 3: 実装を書く**

`src/yEdit.App/DocumentInfoController.cs` を新規作成:

```csharp
using yEdit.Core.DocumentInfo;

namespace yEdit.App;

/// <summary>[ファイル] > 文書情報 の起動導線。DocumentManager から Active を取り、
/// Builder/Formatter に流して文字列を作り、Dialog を Show する。
/// テスト観点: Show の呼び出しは副作用(WinForms モーダル)なので、文字列生成(BuildText)を
/// public にして smoke テスト対象にする(Dialog そのものは Task 6 で目視/手動確認)。</summary>
public sealed class DocumentInfoController
{
    private readonly DocumentManager _docs;
    private readonly IFileMetaProvider _meta;

    public DocumentInfoController(DocumentManager docs, IFileMetaProvider? meta = null)
    {
        _docs = docs;
        _meta = meta ?? FileMetaProvider.Instance;
    }

    /// <summary>Active タブから DocumentInfo を組み立て、Formatter で文字列にして返す。
    /// Active が null(タブなし)なら null。Dialog を Show せず文字列だけ得るテスト用/内部用エントリ。</summary>
    public string? BuildText()
    {
        var doc = _docs.Active;
        if (doc is null) return null;
        var info = DocumentInfoBuilder.Build(
            path: doc.State.Path,
            untitledNumber: doc.State.UntitledNumber,
            snapshot: doc.Editor.CurrentBuffer.Current,
            encoding: doc.State.Encoding,
            hasBom: doc.State.HasBom,
            lineEnding: doc.State.LineEnding,
            fileMeta: _meta.TryGet(doc.State.Path),
            csv: doc.State.CsvMode ? doc.ParseCsv() : null
        );
        return DocumentInfoFormatter.Format(info);
    }

    /// <summary>ダイアログを表示する。Active が null なら何もしない。
    /// 閉じたら編集領域にフォーカスを戻す(操作継続性)。</summary>
    public void Show(IWin32Window owner)
    {
        string? text = BuildText();
        if (text is null) return;
        using var dlg = new DocumentInfoDialog(text);
        dlg.ShowDialog(owner);
        _docs.Active?.FocusTarget.Focus();
    }
}
```

**Step 4: テストが通ることを確認**

Run: `dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj --filter "FullyQualifiedName~DocumentInfoControllerTests" -c Debug`
Expected: `Passed! - Failed: 0, Passed: 3, Skipped: 0`

**Step 5: Commit**

```bash
git add src/yEdit.App/DocumentInfoController.cs tests/yEdit.App.Tests/DocumentInfoControllerTests.cs
git commit -m "feat(app): DocumentInfoController(起動導線 + BuildText smoke)"
```

**Task 7 レビュー**: `BuildText` と `Show` の責務分離、Active=null ガードが両経路に効く、閉じた後に FocusTarget.Focus() が呼ばれる(既存 controllers と同型)。

---

## Task 8: App - MainForm メニュー配線 + MainFormSmokeTests

**Files:**
- Modify: `src/yEdit.App/MainForm.cs`
  - 新規 field `_documentInfo` 追加(既存 `_kinsoku` に隣接)
  - ctor で `_documentInfo = new DocumentInfoController(_docs);`
  - `BuildFileMenu` で「文書情報(&I)」を [タブを閉じる] の直上に追加
- Modify: `tests/yEdit.App.Tests/MainFormSmokeTests.cs`
  - メニュー項目の存在と位置(タブを閉じる の直上)を検証するテストを追加

**Step 1: 失敗するテストを書く**

`tests/yEdit.App.Tests/MainFormSmokeTests.cs` の末尾に追加(既存 class に追記):

```csharp
[Fact]
public void File_menu_contains_document_info_directly_above_close_tab()
{
    RunOnUiThread(() =>
    {
        using var form = new MainForm(NewSettings(), Path.Combine(TempDir(), "settings.json"));
        var file = form.MainMenuStrip!.Items.OfType<ToolStripMenuItem>()
            .First(mi => mi.Text!.StartsWith("ファイル"));
        var items = file.DropDownItems.OfType<ToolStripMenuItem>().ToList();
        int docInfoIdx = items.FindIndex(mi => mi.Text == "文書情報(&I)");
        int closeTabIdx = items.FindIndex(mi => mi.Text == "タブを閉じる(&W)");
        Assert.True(docInfoIdx >= 0, "文書情報 メニューが見つからない");
        Assert.True(closeTabIdx >= 0, "タブを閉じる メニューが見つからない");
        Assert.Equal(closeTabIdx - 1, docInfoIdx);   // 直上
    });
}
```

**Note**: `RunOnUiThread` / `NewSettings` / `TempDir` は既存 `MainFormSmokeTests` のヘルパを流用。差異があれば実装時に既存パターンに合わせる。

**Step 2: テストを実行して失敗を確認**

Run: `dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj --filter "FullyQualifiedName~File_menu_contains_document_info" -c Debug`
Expected: `Failed` — 「文書情報 メニューが見つからない」

**Step 3: MainForm を実装**

`src/yEdit.App/MainForm.cs` の変更(3 箇所):

3-1. field 追加(line 20 直後):

```csharp
private readonly DocumentInfoController _documentInfo; // コンストラクタで生成
```

3-2. ctor に追加(line 178 の `_kinsoku = ...` の直後):

```csharp
_documentInfo = new DocumentInfoController(_docs);
```

3-3. `BuildFileMenu` の変更(line 578 の `file.DropDownItems.Add(new ToolStripSeparator());` の直後、line 579 の「タブを閉じる」より前に挿入):

```csharp
AddMenuItem(file, "文書情報(&I)", (_, _) => _documentInfo.Show(this));
```

**Step 4: テストが通ることを確認**

Run: `dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj --filter "FullyQualifiedName~File_menu_contains_document_info" -c Debug`
Expected: `Passed`

**Step 5: 全 App テストの緑を確認**

Run: `dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj -c Debug`
Expected: `Failed: 0`(既存全テスト + Task 7 + Task 8 追加分すべて緑)

**Step 6: Commit**

```bash
git add src/yEdit.App/MainForm.cs tests/yEdit.App.Tests/MainFormSmokeTests.cs
git commit -m "feat(app): [ファイル]>文書情報(&I) メニュー配線 + smoke"
```

**Task 8 レビュー**: メニュー項目の位置(タブを閉じる の直上)、アクセラレータ &I(ファイルメニュー内他項目と衝突しない)、ctor での controller 生成順序が既存 controllers と同型。

---

## Task 9: Core+App - PositionFormatter 引数縮小 + AnnouncePosition 縮小

**Files:**
- Modify: `src/yEdit.Core/Reading/PositionFormatter.cs`(引数 `totalChars`/`selectionLength` 削除)
- Modify: `tests/yEdit.Core.Tests/Reading/PositionFormatterTests.cs`(追従)
- Modify: `src/yEdit.App/MainForm.cs`(`AnnouncePosition` 縮小)

**Step 1: PositionFormatter シグネチャ縮小(既存テストを新シグネチャに書き換え)**

`tests/yEdit.Core.Tests/Reading/PositionFormatterTests.cs` を上書き:

```csharp
using Xunit;
using yEdit.Core.Reading;

namespace yEdit.Core.Tests.Reading;

/// <summary>PositionFormatter の出力を固定する。
/// 2026-07-25 変更: 文書情報ダイアログ導入に伴い、位置照会からは「文字数 M」「選択 K 文字」を削除。
/// 文字数の詳細は文書情報ダイアログへ集約(設計 2026-07-25 §Task 9)。</summary>
public class PositionFormatterTests
{
    [Fact]
    public void Formats_basic_position() =>
        Assert.Equal(
            "行 12 / 全 340、桁 5",
            PositionFormatter.Format(line: 12, totalLines: 340, column: 5)
        );

    [Fact]
    public void Appends_overtype_when_set() =>
        Assert.Equal(
            "行 1 / 全 1、桁 1、上書き",
            PositionFormatter.Format(1, 1, 1, overtype: true)
        );
}
```

**Step 2: テストを実行して失敗を確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~PositionFormatterTests" -c Debug`
Expected: `Build FAILED` — 引数不一致

**Step 3: PositionFormatter を実装(シグネチャ縮小)**

`src/yEdit.Core/Reading/PositionFormatter.cs` を上書き:

```csharp
namespace yEdit.Core.Reading;

/// <summary>現在位置(行/桁)の読み上げ文字列を組み立てる純ロジック(UI 非依存・テスト可能)。
/// 2026-07-25: 文書情報ダイアログ導入に伴い、文字数(totalChars)と選択(selectionLength)引数を削除。
/// 文字数の詳細は [ファイル]>文書情報 ダイアログへ集約する。</summary>
public static class PositionFormatter
{
    /// <summary>
    /// 「行 L / 全 N、桁 C」を組み立てる。overtype 時は「、上書き」を付ける。
    /// line/column は 1 始まり。
    /// </summary>
    public static string Format(int line, int totalLines, int column, bool overtype = false)
    {
        string s = $"行 {line} / 全 {totalLines}、桁 {column}";
        if (overtype) s += "、上書き";
        return s;
    }
}
```

**Step 4: MainForm.AnnouncePosition を縮小**

`src/yEdit.App/MainForm.cs` の `AnnouncePosition` メソッド(line 849〜876)を以下に置換:

```csharp
/// <summary>現在位置(行/総行/桁)を読み上げる。
/// 2026-07-25: 文字数と選択は本メソッドから削除し、詳細は [ファイル]>文書情報 ダイアログへ集約。</summary>
private void AnnouncePosition()
{
    var ed = _docs.Active?.Editor;
    if (ed is null) return;
    int line = ed.CurrentLine + 1;
    int totalLines = ed.LineCount;
    int column = ed.GetColumn(ed.CurrentPosition) + 1;
    _announcer.Say(PositionFormatter.Format(line, totalLines, column, ed.Overtype));
}
```

`snap.CountCrlfPairs` の呼び出しは削除される。`TextSnapshot.CountCrlfPairs` API 自体は残す(他呼び出しの可能性・将来利用のため撤去は本 Task のスコープ外)。

**Step 5: テストが通ることを確認**

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~PositionFormatterTests" -c Debug`
Expected: `Passed! - Failed: 0, Passed: 2`

**Step 6: App 側の関連テスト(AnnouncePosition の観測がある場合)確認**

Run: `dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj -c Debug`
Expected: `Failed: 0`

**Note**: 既存 App テストで `AnnouncePosition` の発話結果を検証している場所があれば追従する。Grep で `文字数` `選択.*文字` を検索して該当テストを新仕様に更新する:

```bash
# Task 9 実装時に確認:
# Grep パターン: `文字数 \d+` or `選択 \d+ 文字` を含む App/Editor テスト
```

**Step 7: Commit**

```bash
git add src/yEdit.Core/Reading/PositionFormatter.cs tests/yEdit.Core.Tests/Reading/PositionFormatterTests.cs src/yEdit.App/MainForm.cs
git commit -m "refactor(core+app): PositionFormatter 縮小(文字数/選択削除)+ AnnouncePosition 追従"
```

**Task 9 レビュー**: Ctrl+Alt+P 出力から「文字数」「選択」が完全に消える、`TextSnapshot.CountCrlfPairs` の他呼び出し場所がないことを Grep で確認(削除経路の副次影響なし)、既存 App/Editor テストが誤って新出力を検証していないか確認。

---

## Task 10: 品質ゲート + 最終ブランチ 2 パスレビュー

**Step 1: 品質ゲート実行**

Run: `pwsh tools/pre-merge-check.ps1`
Expected: `EXIT 0`(ローカル build + テスト + CSharpier + no-local-paths が全緑)

**Step 2: 最終ブランチ 2 パスレビュー**

superpowers:requesting-code-review スキル or Agent(subagent_type=superpowers:code-reviewer)を使い、**2 パス**を **別エージェント**として起動:

- **パス 1: コード品質パス** — 責務分離・命名・DRY・テスト設計・ミューテーション検証スポットチェック(`CharacterCounter` の境界テスト・`DocumentInfoBuilder` の Format 判定を対象)
- **パス 2: 脆弱性パス** — File I/O 経路(FileMetaProvider の try-catch・path 由来入力・null 伝播)、UI からの信頼境界

各パスの指摘は superpowers:receiving-code-review スキルで扱い、3 択(① fixup commit / ② PR description 記載受容 / ③ 却下)を明示。

**Step 3: L5 実機 SR ドライブ(軽度・推奨)**

NVDA 実行下で以下を目視/耳確認:
1. [ファイル] メニューを開く → 「文書情報(&I)」が読み上げられ、[タブを閉じる] の直上に位置する
2. 選択 → ダイアログが開き、TextBox に初期フォーカスが入り、内容全体が SR で通し読みされる
3. Esc / Enter で閉じ、フォーカスが編集領域に戻る
4. Ctrl+Alt+P を押す → 「行 X / 全 Y、桁 Z」のみ発話される(「文字数」「選択」が発話されない)
5. 上書きモード時 Ctrl+Alt+P → 末尾に「、上書き」が付く

**Step 4: PR 作成**

superpowers:finishing-a-development-branch スキルで GitHub PR を作成。description は日本語で:
- 目的(§3 開発フロー適用の変更)
- 変更の要約(9 タスクの帰結)
- レビュー経緯(Task 毎レビュー結果・最終 2 パスレビューの指摘対応)
- L5 検証結果(実施日時・NVDA バージョン)
- 申し送り(あれば)

---

## タスク依存関係と実行順

```
Task 1 (型定義)
  ↓
Task 2 (CharacterCounter) ─┐
                             ↓
Task 3 (Builder) ────────────┤
                             ↓
Task 4 (Formatter) ──────────┤
                             ↓
Task 5 (FileMetaProvider) ─┐ │
                            ↓ ↓
Task 6 (Dialog) ────────────┤
                            ↓
Task 7 (Controller) ────────┤
                            ↓
Task 8 (MainForm メニュー) ─┤
                            ↓
Task 9 (PositionFormatter 縮小) ← 独立(Task 1〜8 と並行可)
                            ↓
Task 10 (ゲート + レビュー + PR)
```

Task 2/3/4 は Task 1 完了後、Core 内で連鎖。Task 5/6 は Core と独立。Task 7 は 4/5/6 完了後。Task 8 は 7 後。Task 9 は Task 1〜8 と並行可能だが、レビューの見通しをよくするため **Task 8 完了後にまとめて 1 commit** が推奨。

## 参照

- 設計書: [`2026-07-25-document-info-dialog-design.md`](2026-07-25-document-info-dialog-design.md)
- CLAUDE.md: `<repo>/CLAUDE.md` §3(開発フロー)、§5(テスト戦略)、§6(品質ゲート)
- 参考実装パターン: `src/yEdit.App/KinsokuFormatController.cs`(Controller)、`src/yEdit.App/GoToLineDialog.cs`(Dialog)、`src/yEdit.Core/Reading/PositionFormatter.cs`(Formatter)
- 関連メモリ: [[crlf-atomic-caret]](CRLF=1 論理文字方針・本件は F-3 サロゲート=1 と棲み分け)
