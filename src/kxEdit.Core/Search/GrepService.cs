using System.Text.RegularExpressions;
using kxEdit.Core.Text;

namespace kxEdit.Core.Search;

/// <summary>
/// 複数ファイル横断 grep の純ロジック（SR・UI 非依存・スレッド非依存）。フォルダを列挙し、
/// ファイル毎に文字コードを自動判定して復号し、行ごとに <see cref="TextSearcher"/> で照合する。
/// 1 ヒット＝1 マッチ行（行頭の最初のマッチ）。オフセットは UTF-16 文字位置。
/// スレッド制御（Task.Run / CancellationToken / IProgress）は呼び出し側（App）が担う。
/// </summary>
public static class GrepService
{
    // バイナリ判定で先頭を覗くバイト数。
    private const int BinarySniffBytes = 8000;

    // 進捗通知の間隔（ファイル数）。
    private const int ProgressEvery = 64;

    // これを超えるファイルは読まずスキップ（OOM で grep 全体が落ちるのを防ぐ）。
    private const long MaxFileBytes = 64L * 1024 * 1024;

    // CA1861: BuildFilterRegex は複数回呼ばれるため、Split の separator と全一致 fallback を
    // 呼び出しごとに new せず static readonly で共有する。
    private static readonly char[] FilterGlobSeparators = new[] { ';', ',' };
    private static readonly string[] FilterAllPatterns = new[] { "*" };

    /// <summary>
    /// request に従ってフォルダを（再帰）走査し全ヒットを返す。読めないファイル/ディレクトリは
    /// 例外を投げず Errors に集約して走査を継続する。cancellationToken による協調キャンセルでは
    /// 途中までの部分結果＋Cancelled=true を返す。
    /// </summary>
    public static GrepOutcome Search(
        GrepRequest request,
        IProgress<GrepProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        var hits = new List<GrepHit>();
        var errors = new List<GrepError>();
        int filesScanned = 0,
            filesMatched = 0;
        bool cancelled = false;

        var searcher = new TextSearcher(request.Options);
        if (!searcher.IsValid)
        {
            errors.Add(new GrepError(request.Folder, searcher.Error ?? "検索条件が無効です。"));
            return new GrepOutcome(hits, 0, 0, errors, false);
        }

        Regex filter;
        try
        {
            filter = BuildFilterRegex(request.FilePatterns);
        }
        catch (ArgumentException ex)
        {
            errors.Add(
                new GrepError(request.FilePatterns, "ファイルフィルタが不正です: " + ex.Message)
            );
            return new GrepOutcome(hits, 0, 0, errors, false);
        }

        foreach (
            string path in EnumerateFiles(
                request.Folder,
                request.Recursive,
                errors,
                cancellationToken
            )
        )
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            if (!filter.IsMatch(Path.GetFileName(path)))
                continue;

            filesScanned++;
            int hitsBefore = hits.Count;
            try
            {
                long size = new FileInfo(path).Length;
                if (size > MaxFileBytes)
                {
                    errors.Add(
                        new GrepError(
                            path,
                            $"ファイルが大きすぎます（{size:N0} バイト）。スキップしました。"
                        )
                    );
                    continue;
                }

                byte[] bytes = File.ReadAllBytes(path);
                var det = EncodingDetector.Detect(bytes);
                // 対応エンコーディング(UTF-8/SJIS/EUC-JP)はいずれも正常な本文に NUL を含まないため、
                // 先頭 8000B に NUL があればバイナリとみなしてスキップする。
                if (ContainsNul(bytes))
                    continue;

                var loaded = TextFileService.DecodeBytes(bytes, det.CodePage);
                CollectLineHits(path, loaded.Text, searcher, hits);
            }
            catch (RegexMatchTimeoutException)
            {
                errors.Add(new GrepError(path, "検索式が複雑すぎます（タイムアウト）。"));
            }
            catch (Exception ex)
                when (ex
                        is IOException
                            or UnauthorizedAccessException
                            or System.Security.SecurityException
                            or NotSupportedException
                )
            {
                errors.Add(new GrepError(path, ex.Message));
            }
            finally
            {
                // タイムアウトで部分ヒットが残っても 1 件以上あればファイル一致として数える
                // （件数「M ファイル」の過少を防ぐ）。バイナリ/巨大スキップ時は hits 不変で加算されない。
                if (hits.Count > hitsBefore)
                    filesMatched++;
            }

            if (filesScanned % ProgressEvery == 0)
                progress?.Report(new GrepProgress(filesScanned, hits.Count, path));
        }

        if (cancellationToken.IsCancellationRequested)
            cancelled = true;
        progress?.Report(new GrepProgress(filesScanned, hits.Count, null));
        return new GrepOutcome(hits, filesScanned, filesMatched, errors, cancelled);
    }

    /// <summary>
    /// text を行（\r\n / \n / \r 区切り）に分け、各行の先頭マッチを 1 ヒットとして hits へ加える。
    /// 行頭の絶対 UTF-16 オフセットを厳密に積算し、AbsoluteOffset＝行頭＋行内マッチ位置とする。
    /// 末尾の改行は空の最終行を作らない（標準 grep の行勘定）。
    /// </summary>
    private static void CollectLineHits(
        string path,
        string text,
        TextSearcher searcher,
        List<GrepHit> hits
    )
    {
        int pos = 0,
            lineNumber = 0,
            n = text.Length;
        while (pos < n)
        {
            lineNumber++;
            int eol = pos;
            while (eol < n && text[eol] != '\r' && text[eol] != '\n')
                eol++;

            // 行内容 [pos, eol)。FindNext は部分文字列に対して照合するので ^/$ が行境界に効く。
            string line = text.Substring(pos, eol - pos);
            var m = searcher.FindNext(line, 0);
            if (m is { } hit)
            {
                hits.Add(
                    new GrepHit(
                        FilePath: path,
                        LineNumber: lineNumber,
                        Column: hit.Start + 1,
                        LineText: line,
                        MatchStartInLine: hit.Start,
                        MatchLength: hit.Length,
                        AbsoluteOffset: pos + hit.Start
                    )
                );
            }

            if (eol >= n)
                break; // 末尾行（後続 EOL 無し）
            pos = (text[eol] == '\r' && eol + 1 < n && text[eol + 1] == '\n') ? eol + 2 : eol + 1;
        }
    }

    /// <summary>先頭 <see cref="BinarySniffBytes"/> バイトに NUL を含むか。</summary>
    private static bool ContainsNul(byte[] bytes)
    {
        int n = Math.Min(bytes.Length, BinarySniffBytes);
        for (int i = 0; i < n; i++)
            if (bytes[i] == 0)
                return true;
        return false;
    }

    /// <summary>
    /// ";"/"," 区切りの glob 群を 1 本の正規表現へ翻訳する（* → .*、? → .、他はエスケープ、
    /// ^…$ 固定、Windows のため IgnoreCase）。空・空白のみなら全ファイル一致（"*"）。
    /// </summary>
    internal static Regex BuildFilterRegex(string patterns)
    {
        string[] globs = (patterns ?? "").Split(
            FilterGlobSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        if (globs.Length == 0)
            globs = FilterAllPatterns;

        var parts = globs.Select(g =>
            // "*" と "*.*" は「すべてのファイル」を表す慣用（Windows シェル準拠）。素直に翻訳すると
            // "*.*" は `^.*\..*$` となりドットを持たない名前（Makefile/LICENSE 等）を取りこぼすため、
            // この 2 つは全一致へ正規化する。
            (g == "*" || g == "*.*")
                ? "^.*$"
                : "^" + Regex.Escape(g).Replace("\\*", ".*").Replace("\\?", ".") + "$"
        );
        return new Regex(
            string.Join("|", parts),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );
    }

    /// <summary>
    /// folder 配下のファイルを深さ優先で列挙する。各ディレクトリの列挙失敗（アクセス不可等）は
    /// 例外を投げず errors に積んで走査を継続する（EnumerateFiles(AllDirectories) は最初の
    /// 不可ディレクトリで全体が止まるため使わない）。ファイル/ディレクトリ名でソートし順序を安定化。
    /// </summary>
    private static IEnumerable<string> EnumerateFiles(
        string folder,
        bool recursive,
        List<GrepError> errors,
        CancellationToken ct
    )
    {
        var stack = new Stack<string>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested)
                yield break;
            string dir = stack.Pop();

            string[]? files = null;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex)
                when (ex
                        is UnauthorizedAccessException
                            or IOException
                            or System.Security.SecurityException
                            or DirectoryNotFoundException
                )
            {
                errors.Add(new GrepError(dir, ex.Message));
            }
            if (files is not null)
            {
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                    yield return f;
            }

            if (!recursive)
                continue;

            string[]? subs = null;
            try
            {
                subs = Directory.GetDirectories(dir);
            }
            catch (Exception ex)
                when (ex
                        is UnauthorizedAccessException
                            or IOException
                            or System.Security.SecurityException
                            or DirectoryNotFoundException
                )
            {
                errors.Add(new GrepError(dir, ex.Message));
            }
            if (subs is not null)
            {
                // スタックは LIFO なので逆順に積んで名前昇順で処理する。
                Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
                for (int i = subs.Length - 1; i >= 0; i--)
                {
                    // ジャンクション/シンボリックリンク（reparse point）は辿らない。
                    // 循環参照で無限ループ・重複走査に陥るのを防ぐ。
                    if (IsReparsePoint(subs[i]))
                        continue;
                    stack.Push(subs[i]);
                }
            }
        }
    }

    /// <summary>ディレクトリが reparse point（ジャンクション/シンボリックリンク）か。取得不能時は false。</summary>
    private static bool IsReparsePoint(string dir)
    {
        try
        {
            return (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        } // 判定不能なら通常ディレクトリとして扱い、列挙時の失敗は errors に積む
    }
}
