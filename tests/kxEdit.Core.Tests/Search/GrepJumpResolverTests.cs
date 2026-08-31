using System.Text;
using kxEdit.Core.Buffers;
using kxEdit.Core.Search;
using Xunit;

namespace kxEdit.Core.Tests.Search;

/// <summary>
/// A-18: grep ジャンプの着地点解決。grep は<b>ディスク基準</b>の文字列上でヒットを算出し、
/// エディタは<b>バッファ</b>上の位置を選択するので、両者は未保存編集・文字コード判定窓の違い・
/// grep 後の外部変更でずれる。resolver は行番号+行内容で錨を打ち直す。
/// </summary>
public class GrepJumpResolverTests
{
    // AbsoluteOffset は既定でわざと「ありえない値」を入れる。resolver が読んでいれば
    // どのテストかで必ず破綻する(§3.1 不使用の網の第 1 層)。
    private static GrepHit Hit(
        int lineNumber,
        string lineText,
        int matchStart,
        int matchLength,
        int absoluteOffset = int.MaxValue
    ) =>
        new(
            FilePath: @"C:\fixture.txt",
            LineNumber: lineNumber,
            Column: matchStart + 1,
            LineText: lineText,
            MatchStartInLine: matchStart,
            MatchLength: matchLength,
            AbsoluteOffset: absoluteOffset
        );

    private static TextSnapshot Crlf(params string[] lines) =>
        TextBuffer.FromString(string.Join("\r\n", lines)).Current;

    [Fact]
    public void Resolve_LineMatchesAtGrepLineNumber_IsExactAndSelectsWithinThatLine()
    {
        // 非既定位置(先頭行でも末尾行でもない)から検証する(CLAUDE.md §4-B)。
        var snap = Crlf("alpha", "beta needle gamma", "delta");
        var hit = Hit(lineNumber: 2, lineText: "beta needle gamma", matchStart: 5, matchLength: 6);

        var t = GrepJumpResolver.Resolve(hit, snap);

        Assert.Equal(GrepJumpKind.Exact, t.Kind);
        Assert.Equal(1, t.Line);
        Assert.Equal(snap.GetLineStart(1) + 5, t.Offset);
        Assert.Equal(6, t.Length);
        // 選択が指しているのが本当に "needle" であることまで固定する(オフセットだけだと
        // 行頭計算の誤りと桁の誤りが打ち消し合って「たまたま緑」になりうる)。
        Assert.Equal("needle", snap.GetText(t.Offset, t.Length));
    }

    // ===== Nearby: 行がずれた =====

    [Fact]
    public void Resolve_LinesInsertedAbove_FindsHitBelowGrepLineNumber()
    {
        // grep 時は 3 行目だったが、その後 2 行が上に挿入され実際は 5 行目にある。
        var snap = Crlf("ins1", "ins2", "alpha", "beta", "needle here", "gamma");
        var hit = Hit(lineNumber: 3, lineText: "needle here", matchStart: 0, matchLength: 6);

        var t = GrepJumpResolver.Resolve(hit, snap);

        Assert.Equal(GrepJumpKind.Nearby, t.Kind);
        Assert.Equal(4, t.Line);
        Assert.Equal("needle", snap.GetText(t.Offset, t.Length));
    }

    [Fact]
    public void Resolve_LinesDeletedAbove_FindsHitAboveGrepLineNumber()
    {
        // grep 時は 5 行目だったが、その後 2 行が上から消えて実際は 3 行目にある。
        var snap = Crlf("alpha", "beta", "needle here", "gamma", "delta");
        var hit = Hit(lineNumber: 5, lineText: "needle here", matchStart: 0, matchLength: 6);

        var t = GrepJumpResolver.Resolve(hit, snap);

        Assert.Equal(GrepJumpKind.Nearby, t.Kind);
        Assert.Equal(2, t.Line);
        Assert.Equal("needle", snap.GetText(t.Offset, t.Length));
    }

    [Fact]
    public void Resolve_DuplicateLines_PicksNearestToGrepLineNumber()
    {
        // 同一内容の行が index 2 と index 6 にある。origin=index 5 からは 6 のほうが近い。
        // 「最初に見つかった行」を採る実装なら index 2 に着地して赤化する。
        var snap = Crlf("a", "b", "dup line", "c", "d", "e", "dup line", "f");
        var hit = Hit(lineNumber: 6, lineText: "dup line", matchStart: 4, matchLength: 4);

        var t = GrepJumpResolver.Resolve(hit, snap);

        Assert.Equal(GrepJumpKind.Nearby, t.Kind);
        Assert.Equal(6, t.Line);
        Assert.Equal("line", snap.GetText(t.Offset, t.Length));
    }

    [Fact]
    public void Resolve_DuplicateLinesEquidistant_PrefersTheOneAbove()
    {
        // index 3 と index 7 は origin=index 5 から等距離。タイブレーク規約=上を採る。
        var snap = Crlf("a", "b", "c", "dup line", "d", "e", "f", "dup line", "g");
        var hit = Hit(lineNumber: 6, lineText: "dup line", matchStart: 0, matchLength: 3);

        var t = GrepJumpResolver.Resolve(hit, snap);

        Assert.Equal(GrepJumpKind.Nearby, t.Kind);
        Assert.Equal(3, t.Line);
        Assert.Equal(snap.GetLineStart(3), t.Offset);
    }

    [Fact]
    public void Resolve_LineDiffersOnlyByCase_IsNotTreatedAsTheSameLine()
    {
        // origin(index 2)は大文字小文字だけが違う別の行。序数比較なら一致せず、
        // 近傍にある真の行(index 4)へ着地する。ここを IgnoreCase にすると
        // 「見た目が似た別の行」に錨を打って、その行を自信を持って読み上げる
        // ——A-18 と同じ嘘になる。
        var snap = Crlf("a", "b", "NEEDLE HERE", "d", "needle here", "f");
        var hit = Hit(lineNumber: 3, lineText: "needle here", matchStart: 0, matchLength: 6);

        var t = GrepJumpResolver.Resolve(hit, snap);

        Assert.Equal(GrepJumpKind.Nearby, t.Kind);
        Assert.Equal(4, t.Line);
        Assert.Equal("needle", snap.GetText(t.Offset, t.Length));
    }

    // ===== 近傍窓の境界(陰性/陽性の対) =====

    private static TextSnapshot WindowFixture(int needleIndex, int totalLines)
    {
        var lines = new string[totalLines];
        for (int i = 0; i < totalLines; i++)
            lines[i] = $"filler {i}";
        lines[needleIndex] = "needle here";
        return TextBuffer.FromString(string.Join("\r\n", lines)).Current;
    }

    [Fact]
    public void Resolve_ExactlyAtNearbyWindowEdge_IsStillFound()
    {
        // 距離ちょうど NearbyLineWindow。`d <= NearbyLineWindow` を `<` にする変異を kill する。
        var snap = WindowFixture(needleIndex: 0, totalLines: 1400);
        var hit = Hit(
            lineNumber: GrepJumpResolver.NearbyLineWindow + 1, // origin index = 1000
            lineText: "needle here",
            matchStart: 0,
            matchLength: 6
        );

        var t = GrepJumpResolver.Resolve(hit, snap);

        Assert.Equal(GrepJumpKind.Nearby, t.Kind);
        Assert.Equal(0, t.Line);
    }

    [Fact]
    public void Resolve_JustBeyondNearbyWindow_IsStale()
    {
        // 距離 NearbyLineWindow + 1。上の陽性対照と 1 行しか違わないので、
        // 「窓が実際に効いている」ことを弁別できる。
        var snap = WindowFixture(needleIndex: 0, totalLines: 1400);
        var hit = Hit(
            lineNumber: GrepJumpResolver.NearbyLineWindow + 2, // origin index = 1001
            lineText: "needle here",
            matchStart: 0,
            matchLength: 6
        );

        var t = GrepJumpResolver.Resolve(hit, snap);

        Assert.Equal(GrepJumpKind.Stale, t.Kind);
        Assert.Equal(1001, t.Line);
        Assert.Equal(snap.GetLineStart(1001), t.Offset);
        Assert.Equal(0, t.Length);
    }

    // ===== 行内容が空: 近傍走査しない(陽性対照つき) =====

    [Fact]
    public void Resolve_EmptyLineText_DoesNotScanNearby_EvenWhenBlankLinesAreAdjacent()
    {
        // index 3 と index 5 が空行・origin(index 4)は非空。近傍走査すれば必ず空行に当たる
        // fixture なので、Stale になったのは「空ガードのせい」だと弁別できる。
        var snap = Crlf("a", "b", "c", "", "middle", "", "f");
        var hit = Hit(lineNumber: 5, lineText: "", matchStart: 0, matchLength: 0);

        var t = GrepJumpResolver.Resolve(hit, snap);

        Assert.Equal(GrepJumpKind.Stale, t.Kind);
        Assert.Equal(4, t.Line);
        Assert.Equal(0, t.Length);
    }

    [Fact]
    public void Resolve_NonEmptyLineText_DoesScanNearby_OnTheSameFixture()
    {
        // 陽性対照: 同じ fixture で LineText が非空なら近傍走査は働く。
        // これが無いと上のテストは「そもそも近傍走査が壊れている」でも緑になる。
        var snap = Crlf("a", "b", "c", "", "middle", "", "f");
        var hit = Hit(lineNumber: 7, lineText: "middle", matchStart: 0, matchLength: 6);

        var t = GrepJumpResolver.Resolve(hit, snap);

        Assert.Equal(GrepJumpKind.Nearby, t.Kind);
        Assert.Equal(4, t.Line);
    }

    // ===== クランプ / ゼロ幅 =====

    [Fact]
    public void Resolve_LineNumberBeyondBuffer_ClampsToLastLineAndIsStale()
    {
        var snap = Crlf("alpha", "beta");
        var hit = Hit(lineNumber: 999, lineText: "vanished", matchStart: 0, matchLength: 3);

        var t = GrepJumpResolver.Resolve(hit, snap);

        Assert.Equal(GrepJumpKind.Stale, t.Kind);
        Assert.Equal(1, t.Line); // 最終行へクランプ
        Assert.Equal(snap.GetLineStart(1), t.Offset);
        Assert.Equal(0, t.Length);
    }

    [Fact]
    public void Resolve_EmptyBuffer_IsStaleAtOriginWithoutThrowing()
    {
        // 全選択して削除した直後のタブへ grep 結果からジャンプする状況。LineCount=1 /
        // CharLength=0 で、行 API を 1 つでも取り違えると例外=UI スレッドで落ちる。
        var snap = TextBuffer.FromString("").Current;
        var hit = Hit(lineNumber: 3, lineText: "vanished", matchStart: 0, matchLength: 8);

        var t = GrepJumpResolver.Resolve(hit, snap);

        Assert.Equal(GrepJumpKind.Stale, t.Kind);
        Assert.Equal(0, t.Line);
        Assert.Equal(0, t.Offset);
        Assert.Equal(0, t.Length);
    }

    [Fact]
    public void Resolve_ZeroWidthMatch_PlacesCaretWithoutSelecting()
    {
        // 正規表現 `^` 等はゼロ幅ヒットになる(MatchLength = 0)。
        var snap = Crlf("alpha", "beta", "gamma");
        var hit = Hit(lineNumber: 2, lineText: "beta", matchStart: 0, matchLength: 0);

        var t = GrepJumpResolver.Resolve(hit, snap);

        Assert.Equal(GrepJumpKind.Exact, t.Kind);
        Assert.Equal(snap.GetLineStart(1), t.Offset);
        Assert.Equal(0, t.Length);
    }

    // ===== AbsoluteOffset 不使用の網 =====

    [Fact]
    public void Resolve_IgnoresAbsoluteOffsetEntirely()
    {
        // A-18 の再発防止。AbsoluteOffset を選択位置に流用する実装に戻ると、
        // 3 つの値で結果が割れて赤化する。
        var snap = Crlf("alpha", "beta needle gamma", "delta");

        var results = new[] { 0, -12345, int.MaxValue }
            .Select(off =>
                GrepJumpResolver.Resolve(
                    Hit(2, "beta needle gamma", 5, 6, absoluteOffset: off),
                    snap
                )
            )
            .ToArray();

        Assert.All(results, r => Assert.Equal(results[0], r));
        Assert.Equal(snap.GetLineStart(1) + 5, results[0].Offset);
    }

    // ===== grep 側との相互検証(混在 EOL) =====

    [Fact]
    public void Resolve_MixedEols_AgreesWithGrepServiceAndReproducesAbsoluteOffset()
    {
        // CRLF / LF / 単独 CR を混ぜる。grep(GrepService.CollectLineHits)と
        // エディタ(TextChunk の Breaks="LF + LF が続かない CR")の行勘定が一致していないと、
        // Kind が Exact にならないか Offset がずれて赤化する。
        const string Text = "alpha needle\r\nbeta\nneedle two\rgamma needle three\r\ndelta";

        string root = Path.Combine(
            Path.GetTempPath(),
            "kxedit_jump_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "f.txt"), Encoding.UTF8.GetBytes(Text));

            var outcome = GrepService.Search(
                new GrepRequest(
                    Folder: root,
                    FilePatterns: "*.txt",
                    Recursive: false,
                    // SearchOptions(Pattern, MatchCase=false, WholeWord=false, UseRegex=false)
                    Options: new SearchOptions("needle")
                )
            );

            Assert.Empty(outcome.Errors);
            Assert.Equal(3, outcome.Hits.Count); // 5 行中 3 行にヒット

            // ディスクとバッファの内容が同一なら、resolver は AbsoluteOffset を
            // 「読まずに」再現できるはず。これが両者の空間が揃っていることの証明。
            var snap = TextBuffer.FromString(Text).Current;
            foreach (var hit in outcome.Hits)
            {
                var t = GrepJumpResolver.Resolve(hit, snap);
                Assert.Equal(GrepJumpKind.Exact, t.Kind);
                Assert.Equal(hit.AbsoluteOffset, t.Offset);
                Assert.Equal(hit.LineNumber - 1, t.Line);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            { /* 後始末失敗は無害 */
            }
        }
    }
}
