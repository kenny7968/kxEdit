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
        Assert.Equal(snap.GetLineStart(1) + 5, t.BufferOffset);
        Assert.Equal(6, t.Length);
        // 選択が指しているのが本当に "needle" であることまで固定する(オフセットだけだと
        // 行頭計算の誤りと桁の誤りが打ち消し合って「たまたま緑」になりうる)。
        Assert.Equal("needle", snap.GetText(t.BufferOffset, t.Length));
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
        Assert.Equal("needle", snap.GetText(t.BufferOffset, t.Length));
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
        Assert.Equal("needle", snap.GetText(t.BufferOffset, t.Length));
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
        Assert.Equal("line", snap.GetText(t.BufferOffset, t.Length));
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
        Assert.Equal(snap.GetLineStart(3), t.BufferOffset);
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
        Assert.Equal("needle", snap.GetText(t.BufferOffset, t.Length));
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
        // 行数は定数から導く。ベタ書きだと NearbyLineWindow を上げたとき origin が最終行へ
        // クランプされ、緑のまま「距離ちょうど窓」を検証しなくなる(静かな無意味化)。
        var snap = WindowFixture(
            needleIndex: 0,
            totalLines: GrepJumpResolver.NearbyLineWindow + 400
        );
        var hit = Hit(
            lineNumber: GrepJumpResolver.NearbyLineWindow + 1, // origin index = NearbyLineWindow
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
        // 「窓が実際に効いている」ことを弁別できる。行数・期待 origin とも定数から導く。
        var snap = WindowFixture(
            needleIndex: 0,
            totalLines: GrepJumpResolver.NearbyLineWindow + 400
        );
        int origin = GrepJumpResolver.NearbyLineWindow + 1;
        var hit = Hit(
            lineNumber: origin + 1,
            lineText: "needle here",
            matchStart: 0,
            matchLength: 6
        );

        var t = GrepJumpResolver.Resolve(hit, snap);

        Assert.Equal(GrepJumpKind.Stale, t.Kind);
        Assert.Equal(origin, t.Line);
        Assert.Equal(snap.GetLineStart(origin), t.BufferOffset);
        Assert.Equal(0, t.Length);
    }

    // ===== 行内容が空: 近傍走査「だけ」を止める(Exact は止めない・陽性対照つき) =====

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

    [Fact]
    public void Resolve_EmptyLineText_OnABlankOriginLine_IsExactNotStale()
    {
        // 空ガードが止めるのは近傍走査だけで、Exact 判定は止めない。ガードを Exact 判定より
        // 前へ移すとこのケースが Stale に落ち、「変わっていない行」に対して
        // 「内容が変わっています」と嘘を発声することになる(A-18 と同種)。
        // 空 LineText のヒットは実在する: 正規表現 grep の ^ や a* 等のゼロ幅パターンは
        // Length=0 の MatchSpan を返し(TextSearcher.FindNext)、CollectLineHits は
        // 空行にも FindNext を掛けるため LineText="" のヒットが作られる。
        var snap = Crlf("a", "b", "", "d");
        var hit = Hit(lineNumber: 3, lineText: "", matchStart: 0, matchLength: 0);

        var t = GrepJumpResolver.Resolve(hit, snap);

        Assert.Equal(GrepJumpKind.Exact, t.Kind);
        Assert.Equal(2, t.Line);
        Assert.Equal(snap.GetLineStart(2), t.BufferOffset);
        Assert.Equal(0, t.Length);
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
        Assert.Equal(snap.GetLineStart(1), t.BufferOffset);
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
        Assert.Equal(0, t.BufferOffset);
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
        Assert.Equal(snap.GetLineStart(1), t.BufferOffset);
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
        Assert.Equal(snap.GetLineStart(1) + 5, results[0].BufferOffset);
    }

    // ===== grep 側との相互検証(混在 EOL) =====

    [Theory]
    [InlineData("")] // 末尾改行なし
    [InlineData("\r\n")] // 末尾 CRLF(最も普通のファイル形状)
    [InlineData("\n")] // 末尾 LF
    [InlineData("\r")] // 末尾 単独 CR
    public void Resolve_MixedEols_AgreesWithGrepServiceAndReproducesAbsoluteOffset(
        string trailingEol
    )
    {
        // CRLF / LF / 単独 CR を混ぜる。grep(GrepService.CollectLineHits)と
        // エディタ(TextChunk の Breaks="LF + LF が続かない CR")の行勘定が一致していないと、
        // Kind が Exact にならないか BufferOffset がずれて赤化する。
        // 末尾改行の有無は両者が構造的に食い違う唯一の場所なので、両方を通す:
        // grep は末尾改行で空の最終行を作らない / エディタは breaks + 1 なので幽霊の空最終行を持つ。
        string text = "alpha needle\r\nbeta\nneedle two\rgamma needle three\r\ndelta" + trailingEol;

        string root = Path.Combine(
            Path.GetTempPath(),
            "kxedit_jump_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "f.txt"), Encoding.UTF8.GetBytes(text));

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
            var snap = TextBuffer.FromString(text).Current;
            foreach (var hit in outcome.Hits)
            {
                // Land が行内クランプを置かない根拠(remarks)＝grep 側の不変条件。
                // GrepService が将来 LineText を加工(タブ展開・長大行の切り詰め・トリム)すると
                // resolver は静かに行外オフセットを吐き、SelectCharRange がクランプするので
                // 例外も赤も出ずに別の場所へ着地して「N 行目」と発声する(A-18 と同じ形)。
                Assert.True(hit.MatchStartInLine + hit.MatchLength <= hit.LineText.Length);
                var t = GrepJumpResolver.Resolve(hit, snap);
                Assert.Equal(GrepJumpKind.Exact, t.Kind);
                Assert.Equal(hit.AbsoluteOffset, t.BufferOffset);
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

    /// <summary>
    /// 行末ゼロ幅ヒット(正規表現 <c>$</c>)。<c>Land</c> の remarks が「行内クランプを置かない」
    /// 根拠にしている grep 側の不変条件 <c>MatchStartInLine + MatchLength &lt;= LineText.Length</c> の
    /// <b>等号の端</b>で、その判断が最も張り詰める点。
    /// 既存の <see cref="Resolve_ZeroWidthMatch_PlacesCaretWithoutSelecting"/> は
    /// <c>matchStart: 0</c>(<c>^</c> 相当)だけで、この端を 1 件も通していなかった。
    /// </summary>
    /// <remarks>
    /// 手作りの <c>GrepHit</c> ではなく<b>本物の <see cref="GrepService"/> を通す</b>のは、
    /// 「等号の端が実際に到達可能であること」(=仮想の心配ではないこと)と
    /// 「resolver がそれを正しく扱うこと」を 1 本で固定するため。手作りだと前者が証明できない。
    /// 実測(2026-08-31・<c>UseRegex: true</c> の <c>$</c>): 3 行すべてで
    /// <c>MatchStartInLine == LineText.Length</c> / <c>MatchLength == 0</c> のヒットが作られ、
    /// <c>AbsoluteOffset</c> は行末(改行の手前)を指した。
    /// </remarks>
    [Fact]
    public void Resolve_ZeroWidthMatchAtEndOfLine_LandsOnLineEndWithoutSelecting()
    {
        // 全行とも非空にする: 行末と行頭が必ず別位置になり、「行末に着地した」ことを
        // 「行頭に着地した」と弁別できる。空行を混ぜると下の NotEqual が静かに無意味化する。
        string text = "alpha\r\nbeta\r\ngamma";

        string root = Path.Combine(
            Path.GetTempPath(),
            "kxedit_jumpeol_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "f.txt"), Encoding.UTF8.GetBytes(text));

            var outcome = GrepService.Search(
                new GrepRequest(
                    Folder: root,
                    FilePatterns: "*.txt",
                    Recursive: false,
                    Options: new SearchOptions(
                        "$",
                        MatchCase: false,
                        WholeWord: false,
                        UseRegex: true
                    )
                )
            );

            Assert.Empty(outcome.Errors);
            Assert.Equal(3, outcome.Hits.Count); // 3 行すべてに行末ゼロ幅ヒットが立つ

            var snap = TextBuffer.FromString(text).Current;
            foreach (var hit in outcome.Hits)
            {
                // 陽性対照: このヒットが本当に「等号の端」であること。ここが崩れると
                // 以下の assertion は別のケースを検証していることになる(静かな無意味化)。
                Assert.Equal(hit.LineText.Length, hit.MatchStartInLine);
                Assert.Equal(0, hit.MatchLength);

                int line = hit.LineNumber - 1;
                var t = GrepJumpResolver.Resolve(hit, snap);

                Assert.Equal(GrepJumpKind.Exact, t.Kind);
                Assert.Equal(line, t.Line);
                // 行末(改行の手前)。リテラル値ではなく式で書くことで「行末」という意図が読める。
                Assert.Equal(snap.GetLineEnd(line, includeBreak: false), t.BufferOffset);
                Assert.Equal(0, t.Length); // ゼロ幅=選択せずキャレットを置くだけ
                // 行頭ではないこと。Land が MatchStartInLine を落とすとここで弁別できる。
                Assert.NotEqual(snap.GetLineStart(line), t.BufferOffset);
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
