using System.Text;
using System.Text.RegularExpressions;
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
            Root = Path.Combine(Path.GetTempPath(), "kxedit_grep_" + Guid.NewGuid().ToString("N"));
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
        // pin しているのは「ディスク上のバイト列を全バイト復号した空間で AbsoluteOffset が一致すること」。
        // A-18: ここが「エディタのスナップショットと同一空間」だと読まれないこと。TextFileService.Load は
        // 全バイトを DecodeBytes するが、production のエディタは FileController → LoadAsBufferAuto の
        // 先頭 64KB prefix 判定を通る(設計書 §1.1 経路 2)。未保存編集・判定の割れ・grep 後の外部変更が
        // あればバッファ空間とは一致しない=ジャンプ位置には GrepJumpResolver を使う。
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
    public void Nul_beyond_sniff_window_is_scanned_as_text()
    {
        using var t = new TempDir();
        // NUL 判定の窓は先頭 8000 バイト。窓の外の NUL だけならテキストとして照合する(従来どおり)。
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("TARGET\n"));
        while (bytes.Count < 8000)
            bytes.Add((byte)'x');
        bytes.Add(0x00);
        t.Write("late-nul.txt", bytes.ToArray());

        var outcome = GrepService.Search(Req(t.Root, "TARGET"));
        var hit = Assert.Single(outcome.Hits);
        Assert.Equal(1, hit.LineNumber);
    }

    [Fact]
    public void Nul_at_last_byte_of_sniff_window_is_binary()
    {
        using var t = new TempDir();
        // 窓の最後のバイト(添字 7999)の NUL はバイナリ扱い(境界の固定)。
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("TARGET\n"));
        while (bytes.Count < 7999)
            bytes.Add((byte)'x');
        bytes.Add(0x00);
        t.Write("edge-nul.txt", bytes.ToArray());

        var outcome = GrepService.Search(Req(t.Root, "TARGET"));
        Assert.Empty(outcome.Hits);
        // 前提: バイナリとしてスキップ(continue)したことを固定する。例外が Errors に落ちて
        // Hits が空になった場合(catch 節が誤って発火した場合)と区別する。
        Assert.Empty(outcome.Errors);
        Assert.Equal(1, outcome.FilesScanned);
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
    public void Regex_line_anchors_match_middle_lines()
    {
        using var t = new TempDir();
        // 途中の行(2・3 行目)でも ^ / $ が行頭・行末に効く。CRLF・LF・CR の混在も含める。
        t.WriteUtf8("a.txt", "head\r\nTARGET mid\nmid TARGET\rtail\n");

        var head = Assert.Single(GrepService.Search(Req(t.Root, "^TARGET", useRegex: true)).Hits);
        Assert.Equal(2, head.LineNumber);
        Assert.Equal("TARGET mid", head.LineText);

        var tail = Assert.Single(GrepService.Search(Req(t.Root, "TARGET$", useRegex: true)).Hits);
        Assert.Equal(3, tail.LineNumber);
        Assert.Equal("mid TARGET", tail.LineText);
        Assert.Equal(4, tail.MatchStartInLine);

        // 空行に一致する ^$ は、途中の空行を拾う(ゼロ幅・LineText は空)。
        t.WriteUtf8("b.txt", "x\n\ny\n");
        var empty = GrepService.Search(Req(t.Root, "^$", useRegex: true, patterns: "b.txt"));
        var e = Assert.Single(empty.Hits);
        Assert.Equal(2, e.LineNumber);
        Assert.Equal("", e.LineText);
        Assert.Equal(0, e.MatchLength);
    }

    [Fact]
    public void Lookaround_does_not_see_across_line_boundary()
    {
        using var t = new TempDir();
        // 前の行の末尾 a・次の行の先頭 b。行単位の照合では、後読み・先読みは行の外を見ない。
        t.WriteUtf8("a.txt", "xa\nbx\n");

        Assert.Empty(GrepService.Search(Req(t.Root, "(?<=a)b", useRegex: true)).Hits);
        Assert.Empty(GrepService.Search(Req(t.Root, "a(?=b)", useRegex: true)).Hits);
        Assert.Empty(GrepService.Search(Req(t.Root, @"(?<=\n)b", useRegex: true)).Hits);
        // \A と \z は行の先頭・末尾になる(全文の先頭・末尾ではない)。
        var a = Assert.Single(GrepService.Search(Req(t.Root, @"\Ab", useRegex: true)).Hits);
        Assert.Equal(2, a.LineNumber);
        var z = Assert.Single(GrepService.Search(Req(t.Root, @"a\z", useRegex: true)).Hits);
        Assert.Equal(1, z.LineNumber);
    }

    [Fact]
    public void Whole_word_at_line_edges_is_honored()
    {
        using var t = new TempDir();
        // 単語単位の \b は行頭・行末でも境界になる。前の行の末尾が単語文字でも影響しない。
        t.WriteUtf8("a.txt", "xx\nTARGET\nxTARGET\nTARGETx\n");
        var hits = GrepService.Search(Req(t.Root, "TARGET", wholeWord: true)).Hits;
        var h = Assert.Single(hits);
        Assert.Equal(2, h.LineNumber);
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
            "kxedit_grep_missing_" + Guid.NewGuid().ToString("N")
        );
        var outcome = GrepService.Search(Req(missing, "TARGET"));
        Assert.Empty(outcome.Hits);
        Assert.NotEmpty(outcome.Errors); // 列挙時の DirectoryNotFound を集約
    }

    // ---- フェーズ 8(perf-grep): リテラル検索の全文プリフィルタ ----

    private static GrepOutcome SearchWith(
        GrepRequest req,
        Func<TextSearcher, string, bool> prefilter
    ) => GrepService.Search(req, progress: null, prefilter, CancellationToken.None);

    // プリフィルタを常に通す(=プリフィルタなし)ときと、既定のプリフィルタのときの結果を比べる。
    private static void AssertSameAsWithoutPrefilter(GrepRequest req)
    {
        var with = GrepService.Search(req);
        var without = SearchWith(req, (_, _) => true);
        Assert.Equal(without.Hits, with.Hits);
        Assert.Equal(without.FilesScanned, with.FilesScanned);
        Assert.Equal(without.FilesMatched, with.FilesMatched);
        Assert.Equal(without.Errors, with.Errors);
    }

    private static TempDir PrefilterCorpus()
    {
        var t = new TempDir();
        t.WriteUtf8("a.txt", "xx\nTARGET\nxTARGET\nTARGETx\n"); // 単語単位が行頭・行末で効く
        t.WriteUtf8("b.txt", "nothing\r\nhere\r\n"); // 一致しない
        t.WriteUtf8("c.txt", "target\rTaRgEt\n"); // 大小無視・CR 区切り
        t.WriteUtf8("d.txt", "ｔａｒｇｅｔ\nＴＡＲＧＥＴ\n"); // 全角(大小無視で互いに一致)
        t.WriteUtf8("e.txt", "Kelvin K and k\nǅ ǆ Ǆ\n"); // ケルビン記号・タイトルケース
        t.WriteUtf8("f.txt", "TAR\nGET\n"); // 行をまたぐと一致する(行単位では不一致)
        t.WriteUtf8("g.txt", "");
        t.Write("h.txt", EncodingCatalog.Get(932).GetBytes("これは TARGET です\n"));
        return t;
    }

    [Theory]
    [InlineData("TARGET", false, false)]
    [InlineData("TARGET", true, false)]
    [InlineData("TARGET", false, true)]
    [InlineData("TARGET", true, true)]
    [InlineData("ｔａｒｇｅｔ", false, false)]
    [InlineData("k", false, false)]
    [InlineData("k", false, true)]
    [InlineData("ǆ", false, false)]
    [InlineData("TAR\nGET", false, false)]
    [InlineData("nothing", false, true)]
    public void Literal_results_are_same_with_and_without_prefilter(
        string pattern,
        bool matchCase,
        bool wholeWord
    )
    {
        using var t = PrefilterCorpus();
        AssertSameAsWithoutPrefilter(
            Req(t.Root, pattern, matchCase: matchCase, wholeWord: wholeWord)
        );
    }

    [Theory]
    [InlineData("^TARGET")]
    [InlineData("TARGET$")]
    [InlineData("(?<=x)TARGET")]
    [InlineData("TAR\\nGET")]
    [InlineData("^$")]
    public void Regex_results_are_same_with_and_without_prefilter(string pattern)
    {
        using var t = PrefilterCorpus();
        AssertSameAsWithoutPrefilter(Req(t.Root, pattern, useRegex: true));
    }

    // 同等性テスト(Literal_results_are_same_with_and_without_prefilter 等)は、
    // DefaultLiteralPrefilter が常に true を返す no-op に退化していても通ってしまう
    // (「プリフィルタ有り」と「プリフィルタなし」が同じ経路になるため)。
    // 実物の DefaultLiteralPrefilter の返り値そのものを固定して、no-op 退化を検出する。
    [Fact]
    public void DefaultLiteralPrefilter_returns_actual_match_result()
    {
        var plain = new TextSearcher(new SearchOptions("TARGET"));
        Assert.False(GrepService.DefaultLiteralPrefilter(plain, "nothing\r\nhere\r\n"));
        Assert.True(GrepService.DefaultLiteralPrefilter(plain, "xx\nxTARGETx\n"));

        var wholeWord = new TextSearcher(new SearchOptions("TARGET", WholeWord: true));
        Assert.False(GrepService.DefaultLiteralPrefilter(wholeWord, "xTARGETx\n"));
        Assert.True(GrepService.DefaultLiteralPrefilter(wholeWord, "xx\nTARGET\n"));

        // 大小無視・全角(既定で MatchCase=false)。
        var fullWidth = new TextSearcher(new SearchOptions("ｔａｒｇｅｔ"));
        Assert.True(GrepService.DefaultLiteralPrefilter(fullWidth, "ＴＡＲＧＥＴ"));
    }

    [Fact]
    public void Prefilter_is_not_consulted_in_regex_mode()
    {
        using var t = PrefilterCorpus();
        int calls = 0;
        var outcome = SearchWith(
            Req(t.Root, "TARGET", useRegex: true),
            (_, _) =>
            {
                calls++;
                return false;
            }
        );
        Assert.Equal(0, calls);
        Assert.NotEmpty(outcome.Hits); // プリフィルタが false でも正規表現モードは照合する
    }

    [Fact]
    public void Prefilter_false_skips_line_matching_in_literal_mode()
    {
        using var t = PrefilterCorpus();
        int calls = 0;
        var outcome = SearchWith(
            Req(t.Root, "TARGET"),
            (_, _) =>
            {
                calls++;
                return false;
            }
        );
        // 差し替え口が使われていること(テキストのファイル 8 件すべてで呼ばれる)の確認。
        Assert.Equal(8, calls);
        Assert.Empty(outcome.Hits);
        Assert.Equal(8, outcome.FilesScanned);
        Assert.Empty(outcome.Errors);
    }

    [Fact]
    public void Prefilter_timeout_falls_back_to_line_matching_without_error()
    {
        using var t = PrefilterCorpus();
        var req = Req(t.Root, "TARGET");
        var fallback = SearchWith(req, (_, _) => throw new RegexMatchTimeoutException());
        var without = SearchWith(req, (_, _) => true);

        // 前提: catch の "return false" 変異(タイムアウトを「通す」でなく「弾く」に変える)は
        // ヒットが空のままでも Equal(空, 空) が通ってしまい検出できない。ヒットが実在することを先に固定する。
        Assert.NotEmpty(without.Hits);
        Assert.Equal(without.Hits, fallback.Hits);
        Assert.Equal(without.FilesMatched, fallback.FilesMatched);
        Assert.Empty(fallback.Errors); // プリフィルタのタイムアウトはエラーとして記録しない
    }

    [Fact]
    public void Prefilter_skip_keeps_progress_notifications()
    {
        using var t = new TempDir();
        for (int i = 0; i < 130; i++)
            t.WriteUtf8($"f{i:D3}.txt", "nothing\n"); // すべてプリフィルタで省かれる

        // 前提: 130 ファイルが「本当にプリフィルタで省かれた」ことを固定する(呼び出し回数と
        // false の回数)。判定そのものは既定の DefaultLiteralPrefilter に委ねるので、
        // プリフィルタの実装を変えても no-op 化しない限りここは通る。
        int calls = 0,
            falseCalls = 0;
        var reports = new List<GrepProgress>();
        var outcome = GrepService.Search(
            Req(t.Root, "TARGET"),
            new SyncProgress(reports.Add),
            (searcher, text) =>
            {
                calls++;
                bool result = GrepService.DefaultLiteralPrefilter(searcher, text);
                if (!result)
                    falseCalls++;
                return result;
            },
            CancellationToken.None
        );

        Assert.Equal(130, outcome.FilesScanned);
        Assert.Equal(130, calls);
        Assert.Equal(130, falseCalls);
        // 64・128 ファイル目の途中通知と、最後の通知(CurrentFile=null)の 3 回。
        Assert.Equal(new[] { 64, 128, 130 }, reports.Select(r => r.FilesScanned));
    }
}
