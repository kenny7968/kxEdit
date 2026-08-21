using System.Text;
using kxEdit.Core.Search;
using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Search;

public class GrepServiceTests
{
    // 一時ディレクトリを 1 テストごとに作って後始末する補助。
    private sealed class TempDir : IDisposable
    {
        public string Root { get; }

        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "yedit_grep_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Write(string relative, byte[] bytes)
        {
            string full = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            return full;
        }

        public string WriteUtf8(string relative, string text) =>
            Write(relative, Encoding.UTF8.GetBytes(text));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            { /* 後始末失敗は無害 */
            }
        }
    }

    // 同期 IProgress（Progress<T> はスレッドプールへ非同期投函するためテストでは使わない）。
    private sealed class SyncProgress : IProgress<GrepProgress>
    {
        private readonly Action<GrepProgress> _on;

        public SyncProgress(Action<GrepProgress> on) => _on = on;

        public void Report(GrepProgress value) => _on(value);
    }

    private static GrepRequest Req(
        string folder,
        string pattern,
        string patterns = "*.*",
        bool recursive = true,
        bool matchCase = false,
        bool wholeWord = false,
        bool useRegex = false
    ) =>
        new(
            folder,
            patterns,
            recursive,
            new SearchOptions(pattern, matchCase, wholeWord, useRegex)
        );

    [Fact]
    public void Finds_matches_across_utf8_and_shift_jis()
    {
        using var t = new TempDir();
        t.WriteUtf8("a.txt", "これは TARGET です\n");
        t.Write("b.txt", EncodingCatalog.Get(932).GetBytes("これは TARGET です\n")); // Shift_JIS

        var outcome = GrepService.Search(Req(t.Root, "TARGET"));

        Assert.Equal(2, outcome.Hits.Count);
        Assert.Equal(2, outcome.FilesMatched);
        Assert.Equal(2, outcome.FilesScanned);
        Assert.False(outcome.Cancelled);
        Assert.Empty(outcome.Errors);
        Assert.All(
            outcome.Hits,
            h => Assert.Equal("TARGET", h.LineText.Substring(h.MatchStartInLine, h.MatchLength))
        );
    }

    [Fact]
    public void Absolute_offset_aligns_with_decoded_text_for_multibyte_and_surrogate()
    {
        using var t = new TempDir();
        // 多バイト（先頭3文字）＋サロゲート（𠀀=U+20000, 2 UTF-16 単位）＋複数行で AbsoluteOffset を検証。
        string content = "一行目\r\nあいう𠀀TARGET\r\n";
        string path = t.WriteUtf8("m.txt", content);

        var outcome = GrepService.Search(Req(t.Root, "TARGET"));
        var hit = Assert.Single(outcome.Hits);

        Assert.Equal(2, hit.LineNumber);
        // ロード後の本文（エディタが持つ UTF-16 スナップショットと同一）に対し、AbsoluteOffset が正確に一致。
        string text = TextFileService.Load(path).Text;
        Assert.Equal("TARGET", text.Substring(hit.AbsoluteOffset, hit.MatchLength));
        // 行内桁: あ,い,う,𠀀(2単位) = 5 単位 → Column=6, MatchStartInLine=5
        Assert.Equal(6, hit.Column);
        Assert.Equal(5, hit.MatchStartInLine);
    }

    [Fact]
    public void Line_number_correct_with_crlf()
    {
        using var t = new TempDir();
        t.WriteUtf8("c.txt", "a\r\nb\r\nTARGET\r\nc\r\n");
        var outcome = GrepService.Search(Req(t.Root, "TARGET"));
        var hit = Assert.Single(outcome.Hits);
        Assert.Equal(3, hit.LineNumber);
        Assert.Equal(1, hit.Column);
    }

    [Fact]
    public void Recursive_on_finds_subdir_off_skips()
    {
        using var t = new TempDir();
        t.WriteUtf8("top.txt", "TARGET top\n");
        t.WriteUtf8("sub/deep.txt", "TARGET deep\n");

        var on = GrepService.Search(Req(t.Root, "TARGET", recursive: true));
        Assert.Equal(2, on.Hits.Count);

        var off = GrepService.Search(Req(t.Root, "TARGET", recursive: false));
        Assert.Single(off.Hits);
        Assert.Equal("top.txt", Path.GetFileName(off.Hits[0].FilePath));
    }

    [Fact]
    public void Glob_filter_limits_files()
    {
        using var t = new TempDir();
        t.WriteUtf8("a.txt", "TARGET\n");
        t.WriteUtf8("b.cs", "TARGET\n");
        t.WriteUtf8("c.log", "TARGET\n");

        Assert.Single(GrepService.Search(Req(t.Root, "TARGET", patterns: "*.txt")).Hits);
        Assert.Equal(
            2,
            GrepService.Search(Req(t.Root, "TARGET", patterns: "*.txt;*.cs")).Hits.Count
        );
        Assert.Equal(3, GrepService.Search(Req(t.Root, "TARGET", patterns: "")).Hits.Count); // 空＝全件
        Assert.Equal(3, GrepService.Search(Req(t.Root, "TARGET", patterns: "*.*")).Hits.Count);
    }

    [Fact]
    public void Star_dot_star_and_star_match_extensionless_files()
    {
        using var t = new TempDir();
        t.WriteUtf8("Makefile", "TARGET\n"); // 拡張子（ドット）なし
        t.WriteUtf8("a.txt", "TARGET\n");

        // "*.*" と "*" はどちらも「すべてのファイル」を意味し、拡張子なしファイルも拾う。
        Assert.Equal(2, GrepService.Search(Req(t.Root, "TARGET", patterns: "*.*")).Hits.Count);
        Assert.Equal(2, GrepService.Search(Req(t.Root, "TARGET", patterns: "*")).Hits.Count);
        Assert.Equal(2, GrepService.Search(Req(t.Root, "TARGET", patterns: "")).Hits.Count);
        // 一方 "*.txt" は拡張子なしファイルを拾わない。
        var txt = GrepService.Search(Req(t.Root, "TARGET", patterns: "*.txt"));
        Assert.Single(txt.Hits);
        Assert.Equal("a.txt", Path.GetFileName(txt.Hits[0].FilePath));
    }

    [Fact]
    public void Binary_file_with_nul_is_skipped()
    {
        using var t = new TempDir();
        // ASCII の "TARGET" を含むが NUL を持つ＝バイナリとしてスキップされる。
        t.Write(
            "bin.dat",
            new byte[]
            {
                0x00,
                0x01,
                (byte)'T',
                (byte)'A',
                (byte)'R',
                (byte)'G',
                (byte)'E',
                (byte)'T',
                0x00,
            }
        );
        t.WriteUtf8("text.txt", "TARGET\n");

        var outcome = GrepService.Search(Req(t.Root, "TARGET"));
        var hit = Assert.Single(outcome.Hits); // text.txt のみ
        Assert.Equal("text.txt", Path.GetFileName(hit.FilePath));
    }

    [Fact]
    public void MatchCase_and_whole_word_are_honored()
    {
        using var t = new TempDir();
        t.WriteUtf8("a.txt", "target\nTARGET\nTARGETED\n");

        // 大小区別あり → "TARGET" 行のみ（"target" 行は不一致、"TARGETED" は部分一致で拾う）
        var cs = GrepService.Search(Req(t.Root, "TARGET", matchCase: true));
        Assert.Equal(2, cs.Hits.Count); // TARGET と TARGETED
        Assert.All(cs.Hits, h => Assert.NotEqual("target", h.LineText));

        // 単語単位 → "TARGETED" は除外（大小無視なので target/TARGET の 2 行）
        var ww = GrepService.Search(Req(t.Root, "TARGET", wholeWord: true));
        Assert.Equal(2, ww.Hits.Count);
        Assert.DoesNotContain(ww.Hits, h => h.LineText == "TARGETED");
    }

    [Fact]
    public void Regex_line_anchors_match_line_boundaries()
    {
        using var t = new TempDir();
        t.WriteUtf8("a.txt", "TARGET start\nend TARGET\n");

        var head = GrepService.Search(Req(t.Root, "^TARGET", useRegex: true));
        var h1 = Assert.Single(head.Hits);
        Assert.Equal(1, h1.LineNumber);

        var tail = GrepService.Search(Req(t.Root, "TARGET$", useRegex: true));
        var h2 = Assert.Single(tail.Hits);
        Assert.Equal(2, h2.LineNumber);
    }

    [Fact]
    public void Multiple_matches_in_line_yield_single_hit_at_first()
    {
        using var t = new TempDir();
        t.WriteUtf8("a.txt", "TARGET TARGET TARGET\n");
        var outcome = GrepService.Search(Req(t.Root, "TARGET"));
        var hit = Assert.Single(outcome.Hits); // 行頭の最初の 1 件のみ
        Assert.Equal(1, hit.Column);
    }

    [Fact]
    public void No_matches_returns_empty()
    {
        using var t = new TempDir();
        t.WriteUtf8("a.txt", "nothing here\n");
        var outcome = GrepService.Search(Req(t.Root, "TARGET"));
        Assert.Empty(outcome.Hits);
        Assert.Equal(0, outcome.FilesMatched);
        Assert.Equal(1, outcome.FilesScanned);
    }

    [Fact]
    public void Invalid_regex_returns_error_no_hits()
    {
        using var t = new TempDir();
        t.WriteUtf8("a.txt", "TARGET\n");
        var outcome = GrepService.Search(Req(t.Root, "[", useRegex: true));
        Assert.Empty(outcome.Hits);
        Assert.Single(outcome.Errors);
        Assert.False(outcome.Cancelled);
    }

    [Fact]
    public void Precancelled_token_returns_cancelled()
    {
        using var t = new TempDir();
        t.WriteUtf8("a.txt", "TARGET\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var outcome = GrepService.Search(
            Req(t.Root, "TARGET"),
            progress: null,
            cancellationToken: cts.Token
        );
        Assert.True(outcome.Cancelled);
        Assert.Empty(outcome.Hits);
    }

    [Fact]
    public void Cooperative_cancellation_returns_partial_results()
    {
        using var t = new TempDir();
        for (int i = 0; i < 130; i++)
            t.WriteUtf8($"f{i:D3}.txt", "TARGET\n"); // 名前昇順で安定

        using var cts = new CancellationTokenSource();
        // 進捗は 64 ファイル毎に通知。最初の通知（64 件走査時点）でキャンセル。
        var prog = new SyncProgress(p =>
        {
            if (p.FilesScanned >= 64)
                cts.Cancel();
        });

        var outcome = GrepService.Search(Req(t.Root, "TARGET"), prog, cts.Token);

        Assert.True(outcome.Cancelled);
        Assert.Equal(64, outcome.FilesScanned); // 65 件目に入る前に break
        Assert.Equal(64, outcome.Hits.Count); // 部分結果が保持される
    }

    [Fact]
    public void Missing_folder_records_error_not_throws()
    {
        string missing = Path.Combine(
            Path.GetTempPath(),
            "yedit_grep_missing_" + Guid.NewGuid().ToString("N")
        );
        var outcome = GrepService.Search(Req(missing, "TARGET"));
        Assert.Empty(outcome.Hits);
        Assert.NotEmpty(outcome.Errors); // 列挙時の DirectoryNotFound を集約
    }
}
