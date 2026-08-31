# grep ジャンプの行ベース再解決 実装計画(A-18)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** grep の結果からジャンプするとき、ディスク基準の `AbsoluteOffset` ではなく
行番号+行内容を live バッファへ照合して着地点を決め、「別の行を正しい行として読み上げる」を止める。

**Architecture:** 純関数 `GrepJumpResolver`(Core)が `GrepHit` と `TextSnapshot` から
`GrepJumpTarget`(Exact / Nearby / Stale)を返す。`MainForm.OpenAndSelect` はシグネチャを
`OpenAndSelect(GrepHit)` に変え、resolver の結果で選択する。`AbsoluteOffset` は resolver に
渡るが読まれない(不使用をテストで固定する)。

**Tech Stack:** C# / .NET 9 / WinForms / xUnit。設計書は
[`2026-08-31-grep-jump-line-resolution-design.md`](./2026-08-31-grep-jump-line-resolution-design.md)。

**進め方:** CLAUDE.md §3 工程 4。各タスク = 実装 → 仕様レビュー。**Task 1 は後続が依存する新しい
抽象を導入するため、タスク時に前倒しのコード品質レビューを行う**(CLAUDE.md §3 の前倒し例外)。

---

## 前提知識(このリポジトリを知らない人向け)

| 事項 | 内容 |
|---|---|
| ブランチ | `feature/grep-jump-line-resolution`(main から作成済み・設計書 commit 済み) |
| ビルド | `dotnet build kxEdit.sln -c Release -warnaserror`(**0 warning 必須**) |
| テスト | `dotnet test tests/kxEdit.Core.Tests -c Release --no-build` 等(README §テスト) |
| 整形 | commit 時に Husky.Net の CSharpier が自動整形する。`--no-verify` で飛ばさない |
| コミット | `feat\|fix\|docs\|test\|refactor\|chore(scope): 要約` + 日本語本文 |
| 日本語 | コメント・コミット本文は日本語。識別子は英語 |

**PowerShell の落とし穴**: 複数行のコミットメッセージは here-string ではなく
**ファイルに書いて `git commit -F <path>`** で渡す(Bash ツール経由だと `@'...'@` が
リテラルとして混入する)。

**主要な既存 API**:

```csharp
// kxEdit.Core.Buffers.TextSnapshot
int LineCount { get; }                              // 空文字でも 1
int GetLineStart(int line);                         // 0-based・範囲外は例外
int GetLineEnd(int line, bool includeBreak);        // false=改行の手前
string GetText(int start, int length);
int GetLineIndexOfChar(int pos);
static TextBuffer TextBuffer.FromString(string s);  // テスト用の組み立て
TextSnapshot TextBuffer.Current { get; }

// kxEdit.Editor.EditorControl
void SelectCharRange(int start, int length);        // 範囲外は [0, CharLength] へクランプ
void BringCaretIntoView();
int CurrentLine { get; }                            // 0-based
(int Start, int End) GetSelectionCharRange();
int TopLine { get; set; }
void ReplaceCharRange(int start, int length, string replacement);
TextBuffer CurrentBuffer { get; }                   // non-null 保証
```

---

## Task 1: `GrepJumpResolver`(Core・純関数)

新しい抽象の導入。**このタスク完了時に前倒しコード品質レビューを行う**(CLAUDE.md §3)。

**Files:**
- Create: `src/kxEdit.Core/Search/GrepJumpResolver.cs`
- Create: `tests/kxEdit.Core.Tests/Search/GrepJumpResolverTests.cs`

### Step 1: テストファイルを作り、最初の 1 本を書く(失敗させる)

`tests/kxEdit.Core.Tests/Search/GrepJumpResolverTests.cs`:

```csharp
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
}
```

### Step 2: テストが**コンパイルエラーで**落ちることを確認

```
dotnet test tests/kxEdit.Core.Tests -c Release
```
Expected: FAIL — `GrepJumpResolver` / `GrepJumpKind` が存在しない旨のビルドエラー。

### Step 3: `GrepJumpResolver` を実装する

`src/kxEdit.Core/Search/GrepJumpResolver.cs`:

```csharp
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Search;

/// <summary>grep ヒットを live バッファへ解決した結果の種別。</summary>
public enum GrepJumpKind
{
    /// <summary>grep 時の行番号にヒット行がそのままあった。</summary>
    Exact,

    /// <summary>行がずれていたが、近傍に同一内容の行が見つかった。</summary>
    Nearby,

    /// <summary>ヒット行が見つからない(内容が変わった)。行頭へ寄せるだけで選択しない。</summary>
    Stale,
}

/// <summary>grep ジャンプの着地点。<paramref name="Line"/> は 0-based。</summary>
public sealed record GrepJumpTarget(GrepJumpKind Kind, int Line, int Offset, int Length);

/// <summary>
/// grep ヒットを、その時点のバッファ内容へ照合して着地点を決める(A-18・設計書 §3.1)。
/// </summary>
/// <remarks>
/// <b>この関数は <see cref="GrepHit.AbsoluteOffset"/> を読まない。</b> AbsoluteOffset は
/// grep がディスク上のバイト列を復号した空間の値であり、
/// (1) 未保存編集のあるタブ (2) エディタ(先頭 64KB prefix)と grep(全バイト)の文字コード判定の
/// 割れ (3) grep 実行後のディスク側外部変更 —— のいずれでもバッファ空間とずれる。
/// A-18 はこれを選択位置に流用していたことによる「別の行を正しい行として読み上げる」不具合。
/// <b>AbsoluteOffset をこの経路へ戻さないこと</b>(<c>GrepJumpResolverTests</c> が固定している)。
/// </remarks>
public static class GrepJumpResolver
{
    /// <summary>
    /// 行がずれていたときに前後へ探しにいく行数。UI スレッド上の走査なので有界にする。
    /// 実測に基づく値ではない設計値(設計書 §6 申し送り)。
    /// </summary>
    internal const int NearbyLineWindow = 1000;

    /// <summary>
    /// <paramref name="hit"/> の行番号+行内容を <paramref name="snap"/> へ照合し、着地点を返す。
    /// 1) 行番号どおりの行が一致 → <see cref="GrepJumpKind.Exact"/>
    /// 2) 近い順に ±<see cref="NearbyLineWindow"/> 行を探して一致 → <see cref="GrepJumpKind.Nearby"/>
    /// 3) 見つからない / 行内容が空 → <see cref="GrepJumpKind.Stale"/>(選択せず行頭へ)
    /// </summary>
    public static GrepJumpTarget Resolve(GrepHit hit, TextSnapshot snap)
    {
        ArgumentNullException.ThrowIfNull(hit);
        ArgumentNullException.ThrowIfNull(snap);

        int lineCount = snap.LineCount; // 空文字でも 1
        int origin = Math.Clamp(hit.LineNumber - 1, 0, lineCount - 1);

        if (LineEquals(snap, origin, hit.LineText))
            return Land(GrepJumpKind.Exact, snap, origin, hit);

        // 行内容が空だと照合材料がゼロで、近傍の任意の空行に一致してしまう。無関係な空行へ
        // 黙って着地して正常であるかのように発声するより、Stale として明示するほうが誠実
        // (設計書 §3.1)。
        if (hit.LineText.Length > 0)
        {
            for (int d = 1; d <= NearbyLineWindow; d++)
            {
                int up = origin - d;
                int down = origin + d;
                // 同距離なら上を先に採る(タイブレークの規約)。
                if (up >= 0 && LineEquals(snap, up, hit.LineText))
                    return Land(GrepJumpKind.Nearby, snap, up, hit);
                if (down < lineCount && LineEquals(snap, down, hit.LineText))
                    return Land(GrepJumpKind.Nearby, snap, down, hit);
                if (up < 0 && down >= lineCount)
                    break; // 両端に到達=窓を使い切る前に探索終了
            }
        }

        return new GrepJumpTarget(GrepJumpKind.Stale, origin, snap.GetLineStart(origin), 0);
    }

    /// <summary>
    /// 一致した行の着地点を組み立てる。
    /// </summary>
    /// <remarks>
    /// <b>行内へのクランプは置かない。</b> <see cref="LineEquals"/> が
    /// 「行の長さ == <c>hit.LineText.Length</c>」を保証し、grep 側が
    /// 「<c>MatchStartInLine + MatchLength &lt;= LineText.Length</c>」を保証するので、
    /// 選択が行外へ食み出す経路が存在しない(=書いても到達不能な belt になる)。
    /// 範囲外の最終防衛は <c>EditorControl.SelectCharRange</c> の契約が担う。
    /// </remarks>
    private static GrepJumpTarget Land(
        GrepJumpKind kind,
        TextSnapshot snap,
        int line,
        GrepHit hit
    ) => new(kind, line, snap.GetLineStart(line) + hit.MatchStartInLine, hit.MatchLength);

    /// <summary>
    /// <paramref name="line"/> の行内容(改行を含まない)が <paramref name="text"/> と序数一致するか。
    /// 文字列を実体化する前に長さで篩う(近傍走査でピース木の走査を最大 2×窓回まわすため)。
    /// </summary>
    private static bool LineEquals(TextSnapshot snap, int line, string text)
    {
        int start = snap.GetLineStart(line);
        int length = snap.GetLineEnd(line, includeBreak: false) - start;
        if (length != text.Length)
            return false;
        return length == 0
            || string.Equals(snap.GetText(start, length), text, StringComparison.Ordinal);
    }
}
```

### Step 4: テストが通ることを確認

```
dotnet test tests/kxEdit.Core.Tests -c Release --filter FullyQualifiedName~GrepJumpResolverTests
```
Expected: PASS(1 件)

### Step 5: 残りの L1 テストを追加する(設計書 §5.1 の #2〜#9)

`GrepJumpResolverTests` に追記:

```csharp
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
```

`System.Linq` の using が要る場合は追加する(`ImplicitUsings` の設定次第。ビルドエラーが出たら足す)。

### Step 6: 混在 EOL の相互検証テストを追加する(設計書 §5.1 #7)

これは **grep 側と resolver 側が同じ行番号規約を使っていること**の相互検証であり、
手作りの `GrepHit` では確かめられない。**本物の `GrepService` を回して得たヒット**を使う。

```csharp
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
            Assert.Equal(3, outcome.Hits.Count); // 4 行中 3 行にヒット

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
            catch { /* 後始末失敗は無害 */
            }
        }
    }
```

**注意**: 期待値 `3` は fixture(5 行中 3 行が "needle" を含む)から導いた値だが、
**実際に走らせて確認してから固定する**。ずれた場合は fixture ではなく期待値のほうを疑い、
なぜずれたか(行勘定か・フィルタか・文字コード判定か)を先に突き止める
——「期待値を先に決めて fixture を合わせにいく」と、行勘定の食い違いという本題を
テストが素通りする。`GrepServiceTests` に同型の `Req(...)` ヘルパがあるので参考にしてよい。

### Step 7: 全 L1 テストを走らせる

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter FullyQualifiedName~GrepJumpResolverTests
```
Expected: 全 PASS・0 warning

### Step 8: コミット

```
git add src/kxEdit.Core/Search/GrepJumpResolver.cs tests/kxEdit.Core.Tests/Search/GrepJumpResolverTests.cs
git commit -F <メッセージファイル>
```

メッセージ:
```
feat(core): grep ヒットを行番号+行内容でバッファへ解決する GrepJumpResolver(A-18)

grep はディスク基準、エディタはバッファ基準という 2 空間の食い違いを、
AbsoluteOffset を使わず LineNumber / LineText / MatchStartInLine の照合で吸収する。
Exact / Nearby(±1000 行)/ Stale の 3 値を返す。LineText が空のときは照合材料が
無いため近傍走査せず Stale に倒す。
```

### Step 9: 前倒しコード品質レビュー(CLAUDE.md §3)

**別エージェント**を起動して `GrepJumpResolver` + そのテストをレビューさせる。観点:

- 近傍走査のタイブレーク・境界(`d <= NearbyLineWindow`・両端到達 break)が正しいか
- `Land` にクランプを置かない判断(到達不能 belt を作らない)が妥当か
- テストが「たまたま緑」でないか(特に #3 の重複行・#5/#6 の空ガード陽性対照)
- 後続 Task 2 が使う抽象として `GrepJumpTarget` の形が適切か

指摘は CLAUDE.md §4 の 3 択(① fixup / ② PR に記載して受容 / ③ 理由付き却下)で明示する。

---

## Task 2: `MainForm.OpenAndSelect` を resolver 経由にする

**Files:**
- Modify: `src/kxEdit.App/MainForm.cs`(`:195` 呼び出し・`:1061-1076` 本体・冒頭の using)
- Modify: `src/kxEdit.Core/Search/GrepTypes.cs`(偽の doc コメント訂正)
- Modify: `tests/kxEdit.App.Tests/MainFormSmokeTests.cs`

### Step 1: 既存 L3 テストを新シグネチャへ移植する(まだ赤で良い)

`MainFormSmokeTests.cs` にヘルパを足す:

```csharp
    /// <summary>grep が返すのと同じ形のヒットを組み立てる(A-18 のテスト用)。</summary>
    private static GrepHit GrepHitFor(
        string path,
        int lineNumber,
        string lineText,
        int matchStart,
        int matchLength,
        int absoluteOffset
    ) =>
        new(
            FilePath: path,
            LineNumber: lineNumber,
            Column: matchStart + 1,
            LineText: lineText,
            MatchStartInLine: matchStart,
            MatchLength: matchLength,
            AbsoluteOffset: absoluteOffset
        );
```

既存 2 本の呼び出しを差し替える:

- `OpenAndSelect_OpensSelectsAndSuppressesAutoCsv`
  内容 `"a,b,c\n1,2,3"` に対し
  `form.OpenAndSelect(path, offset: 2, length: 3);`
  → `form.OpenAndSelect(GrepHitFor(path, 1, "a,b,c", 2, 3, absoluteOffset: 2));`
  既存 assertion `Assert.Equal((2, 5), doc.Editor.GetSelectionCharRange());` はそのまま通る。

- `OpenAndSelect_ScrollsTargetIntoView`
  `form.OpenAndSelect(path, offset, length: 4);`
  → `form.OpenAndSelect(GrepHitFor(path, 200, "line199", 0, 4, absoluteOffset: offset));`
  既存 assertion(`CurrentLine == 199` / 桁 0 / `TopLine > 0` / 可視域内)はそのまま通る。

`using kxEdit.Core.Search;` が無ければ追加する。

### Step 2: ビルドが**シグネチャ不一致で**落ちることを確認

```
dotnet build kxEdit.sln -c Release -warnaserror
```
Expected: FAIL — `OpenAndSelect(string, int, int)` に一致する多重定義が無い旨。

### Step 3: `MainForm.OpenAndSelect` を書き換える

冒頭の using に追加:

```csharp
using kxEdit.Core.Search;
```

`MainForm.cs:1061-1076` を置き換える:

```csharp
    /// <summary>
    /// grep ジャンプ用: <paramref name="hit"/> のファイルを開き（既存タブがあれば再利用）、
    /// ヒット行を選択してエディタへフォーカスする。選択移動でエディタの UIA が一致行を SR に読ませる。
    /// </summary>
    /// <remarks>
    /// <b>A-18(2026-08-31)</b>: 以前は <c>hit.AbsoluteOffset</c> をそのまま
    /// <see cref="EditorControl.SelectCharRange"/> に渡し、doc で「同じ復号経路を通るため
    /// エディタのスナップショットと同一空間に揃う」と<b>無条件の不変条件として宣言していた</b>。
    /// 実際に揃うのは「タブを新規に開き、かつ復号結果が同一」のときだけで、未保存編集のある
    /// タブ・文字コード判定窓の割れ・grep 後の外部変更ではずれる。ずれた位置に着地したうえで
    /// 着地行を「N 行目」と発声するため、<b>SR ユーザーには検出できない嘘</b>になっていた。
    /// 現在は <see cref="GrepJumpResolver"/> が行番号+行内容を live バッファへ照合する。
    /// <b><c>AbsoluteOffset</c> をこの経路へ戻さないこと。</b>
    /// <para>
    /// 発声の行番号は <c>t.Line</c> ではなく<b>着地後の</b> <see cref="EditorControl.CurrentLine"/>
    /// から読み戻す。resolver の意図値を読むと <c>SelectCharRange</c> 側のクランプ/スナップの
    /// 不具合が発声に現れなくなる(発声文言は第 2 の観測面)。
    /// </para>
    /// </remarks>
    internal void OpenAndSelect(GrepHit hit)
    {
        var doc = _file.TryOpenOrActivate(hit.FilePath, suppressAutoCsv: true);
        if (doc is null)
            return;
        var t = GrepJumpResolver.Resolve(hit, doc.Editor.CurrentBuffer.Current);
        doc.Editor.SelectCharRange(t.Offset, t.Length);
        doc.FocusTarget.Focus();
        // ジャンプ先のファイル名と行を明示通知（選択移動の自動読みに加え、別ファイルへ飛んだ文脈を補う）。
        string where = $"{doc.State.DisplayName} {doc.Editor.CurrentLine + 1} 行目";
        _announcer.Say(
            t.Kind == GrepJumpKind.Stale ? $"{where} 内容が変わっています" : where
        );
    }
```

`MainForm.cs:195` の呼び出しを差し替える:

```csharp
                resultsFactory: () =>
                    new GrepResultsWindow(new GrepResultsCallbacks(OpenAndSelect)),
```

> `BringCaretIntoView()` は **Task 3** で足す。ここでは入れない(§3.3 は別症状であり、
> 先に「belt 無しでは追従しない」ことをテストで示してから足す)。

### Step 4: `GrepTypes.cs` の偽の doc コメントを訂正する

`src/kxEdit.Core/Search/GrepTypes.cs` の `GrepHit`:

```csharp
/// <summary>
/// grep の 1 ヒット（＝1 マッチ行）。1 行に複数マッチがあっても行頭の最初のマッチを 1 件として持つ。
/// オフセットはいずれも UTF-16 文字位置。
/// </summary>
/// <remarks>
/// <b>A-18(2026-08-31)</b>: 旧 doc は「エディタの string index・SelectCharRange と同一空間」と
/// 書いていたが、これは<b>偽の不変条件</b>だった。<see cref="AbsoluteOffset"/> は
/// <b>ディスク上のバイト列を復号した空間</b>の値で、未保存編集のあるタブのバッファとは一致しない
/// (エディタと grep で文字コード判定の窓も違う)。ジャンプ先の解決には
/// <c>GrepJumpResolver.Resolve</c> を使い、<see cref="AbsoluteOffset"/> を選択位置へ流用しないこと。
/// </remarks>
public sealed record GrepHit(
    string FilePath, // 絶対パス
    int LineNumber, // 1 始まり
    int Column, // 1 始まり（行内 UTF-16 桁・最初のマッチ）
    string LineText, // 行内容（EOL 除外・表示用 / A-18 の照合キー）
    int MatchStartInLine, // 行内 UTF-16 オフセット（0 始まり）
    int MatchLength, // マッチ長（UTF-16）
    int AbsoluteOffset
); // ファイル先頭からの UTF-16 オフセット（ディスク基準・ジャンプには使わない=A-18）
```

### Step 5: ビルドと既存テストの緑を確認

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter FullyQualifiedName~MainFormSmokeTests
```
Expected: 全 PASS・0 warning

### Step 6: A-18 本体の L3 テストを追加する(設計書 §5.2 #1・#2)

```csharp
    // ===== A-18: 未保存編集のあるタブへのジャンプ =====

    /// <summary>
    /// grep のヒットはディスク基準。既に開いているタブに未保存の編集があると、
    /// AbsoluteOffset をそのまま使えば別の行に着地して「その行」を発声してしまう。
    /// 行番号+行内容で解決していれば、編集後の正しい行に着地し発声も一致する。
    /// </summary>
    [Fact]
    public void OpenAndSelect_DirtyTab_ResolvesByLineContentNotDiskOffset() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string path = tmp.File("doc.txt");
            var lines = new[] { "alpha", "beta", "gamma needle delta", "epsilon" };
            File2.WriteAllText(path, string.Join("\r\n", lines));
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);

            // ディスク基準のヒット(grep が返すのと同じ値)。
            int diskOffset = "alpha\r\nbeta\r\n".Length + 6; // "gamma " の直後
            var hit = GrepHitFor(path, 3, "gamma needle delta", 6, 6, absoluteOffset: diskOffset);

            // タブを開いてから、ヒット行より前に 3 行挿入して未保存にする。
            var opened = form.FileForTest.TryOpenOrActivate(path);
            Assert.NotNull(opened);
            opened!.Editor.ReplaceCharRange(0, 0, "ins1\r\nins2\r\nins3\r\n");
            Assert.True(opened.Editor.Modified);

            form.OpenAndSelect(hit);

            var doc = form.FileForTest.TryOpenOrActivate(path);
            Assert.NotNull(doc);
            var snap = doc!.Editor.CurrentBuffer.Current;

            // 陽性対照: 旧実装(AbsoluteOffset 直渡し)なら別の行に着地する fixture であること。
            // これが無いと「たまたま同じ行」でも緑になる。
            Assert.NotEqual(5, snap.GetLineIndexOfChar(diskOffset));

            // 正しい着地: 挿入 3 行ぶん下がった index 5(=6 行目)。
            Assert.Equal(5, doc.Editor.CurrentLine);
            var sel = doc.Editor.GetSelectionCharRange();
            Assert.Equal("needle", snap.GetText(sel.Start, sel.End - sel.Start));
            // 発声も着地行と一致する(A-18 の症状は「誤った行を発声」なので発声側も固定する)。
            Assert.Contains("6 行目", form.LastAnnouncementForTest);
            Assert.DoesNotContain("内容が変わっています", form.LastAnnouncementForTest);
        });

    /// <summary>
    /// ヒット行の内容が変わっていれば、黙って別の行へ飛ばず「内容が変わっています」と伝え、
    /// 選択もしない(キャレットを置くだけ)。
    /// </summary>
    [Fact]
    public void OpenAndSelect_StaleHit_AnnouncesContentChangedAndSelectsNothing() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string path = tmp.File("doc.txt");
            var lines = new[] { "alpha", "beta", "gamma needle delta", "epsilon" };
            File2.WriteAllText(path, string.Join("\r\n", lines));
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);

            var opened = form.FileForTest.TryOpenOrActivate(path);
            Assert.NotNull(opened);
            // ヒット行(index 2)を丸ごと別内容に差し替える=近傍にも一致行が無くなる。
            var snap0 = opened!.Editor.CurrentBuffer.Current;
            int start = snap0.GetLineStart(2);
            int len = snap0.GetLineEnd(2, includeBreak: false) - start;
            opened.Editor.ReplaceCharRange(start, len, "totally different");

            form.OpenAndSelect(
                GrepHitFor(path, 3, "gamma needle delta", 6, 6, absoluteOffset: start + 6)
            );

            var doc = form.FileForTest.TryOpenOrActivate(path);
            Assert.NotNull(doc);
            Assert.Contains("内容が変わっています", form.LastAnnouncementForTest);
            var sel = doc!.Editor.GetSelectionCharRange();
            Assert.Equal(sel.Start, sel.End); // 選択しない
            Assert.Equal(2, doc.Editor.CurrentLine); // 行頭へ寄せる
        });
```

**注意**: `form.LastAnnouncementForTest` の実プロパティ名・`TempDir.File` / `File2` /
`ShowMainForm` / `NewSettings` の使い方は `MainFormSmokeTests.cs` の既存テストに合わせる。
`opened.Editor.Modified` が期待どおり立つかを最初に確認し、立たないなら
`ReplaceCharRange` ではなく実際の編集経路を使う。

### Step 7: テストを走らせる

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter FullyQualifiedName~MainFormSmokeTests
```
Expected: 全 PASS

### Step 8: コミット

```
fix(app): grep ジャンプをディスク基準オフセットから行ベース解決へ切り替える(A-18)

OpenAndSelect のシグネチャを OpenAndSelect(GrepHit) に変え、裸のオフセットを
渡せる入口を無くす。着地点は GrepJumpResolver が行番号+行内容で決め、Stale は
「内容が変わっています」と伝えて選択しない。

GrepHit / OpenAndSelect の doc が「エディタのスナップショットと同一空間に揃う」と
無条件に宣言していたのは偽の不変条件だったので訂正する(A-18 の直接の原因)。
```

### Step 9: 仕様レビュー(別エージェント)

実装とテストが設計書 §3.2 / §5.2 どおりかを確認させる。

---

## Task 3: 同一ヒットへの再ジャンプでスクロールを追従させる(設計書 §3.3)

A-18 とは別症状の**意図的なスコープ追加**。A-3 で `GoToLine` に入れた belt の取りこぼし。

**Files:**
- Modify: `src/kxEdit.App/MainForm.cs`(`OpenAndSelect`)
- Modify: `tests/kxEdit.App.Tests/MainFormSmokeTests.cs`

### Step 1: 失敗するテストを書く

```csharp
    /// <summary>
    /// 設計書 §3.3(A-3 同型): SetSelectionCharRange は Anchor/Caret 無変化で早期 return する
    /// (EditorControl.Caret.cs:202)ため、同じヒットへ再ジャンプするとスクロールが追従しない。
    /// ホイールでのスクロール退避を TopLine の代入で再現する(キャレットは動かない)。
    /// </summary>
    [Fact]
    public void OpenAndSelect_SameHitTwice_ScrollsBackIntoView() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string path = tmp.File("many-lines.txt");
            var lines = Enumerable.Range(0, 200).Select(i => $"line{i}").ToArray();
            File2.WriteAllText(path, string.Join("\r\n", lines));
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);

            var hit = GrepHitFor(
                path,
                lineNumber: 200,
                lineText: "line199",
                matchStart: 0,
                matchLength: 4,
                absoluteOffset: string.Join("\r\n", lines.Take(199)).Length + 2
            );

            form.OpenAndSelect(hit);
            var doc = form.FileForTest.TryOpenOrActivate(path);
            Assert.NotNull(doc);
            Assert.True(doc!.Editor.TopLine > 0, "1 回目のジャンプで既にスクロールしている前提");

            // ホイールでのスクロール退避を再現: TopLine だけ動きキャレットは不動。
            doc.Editor.TopLine = 0;
            var selBefore = doc.Editor.GetSelectionCharRange();

            form.OpenAndSelect(hit); // 同じヒットへ再ジャンプ

            // 選択は無変化(=SetSelectionCharRange は早期 return する経路に入っている)。
            Assert.Equal(selBefore, doc.Editor.GetSelectionCharRange());
            // それでもスクロールは追従する。
            int visibleRows = Math.Max(
                1,
                doc.Editor.ClientSize.Height / Math.Max(1, doc.Editor.LineHeightPx)
            );
            Assert.True(
                doc.Editor.TopLine > 0,
                $"expected TopLine > 0 after re-jump, got {doc.Editor.TopLine}"
            );
            Assert.InRange(
                doc.Editor.CurrentLine,
                doc.Editor.TopLine,
                doc.Editor.TopLine + visibleRows - 1
            );
        });
```

### Step 2: テストが赤で落ちることを確認

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter FullyQualifiedName~OpenAndSelect_SameHitTwice
```
Expected: FAIL — `expected TopLine > 0 after re-jump, got 0`

**このステップを飛ばさないこと。** 赤を確認せずに belt を足すと、実は到達不能な belt を
「効いている」と誤認する(過去ブランチで実際に起きている)。

### Step 3: `OpenAndSelect` に belt を足す

`doc.FocusTarget.Focus();` の**直前**に:

```csharp
        doc.Editor.SelectCharRange(t.Offset, t.Length);
        // 設計書 §3.3(A-3 同型): SetSelectionCharRange は Anchor/Caret 無変化で早期 return し
        // BringCaretIntoView へ到達しない。ジャンプは「移動先を必ず見せる」操作なので、
        // 同じヒットへ再ジャンプしたとき(ホイールでスクロール退避 → 同じ行を再選択)にも
        // 追従するよう、ジャンプ導線の側で明示的に呼ぶ。
        doc.Editor.BringCaretIntoView();
        doc.FocusTarget.Focus();
```

### Step 4: テストが通ることを確認

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter FullyQualifiedName~MainFormSmokeTests
```
Expected: 全 PASS

### Step 5: コミット

```
fix(app): 同じ grep ヒットへ再ジャンプしたときスクロールを追従させる(A-3 同型)

SetSelectionCharRange は Anchor/Caret 無変化で早期 return するため、ホイールで
スクロール退避したあと同じヒットへ再ジャンプしても画面が戻らなかった。A-3 で
GoToLine に入れた belt の取りこぼし。設計書 §3.3 の意図的なスコープ追加。
```

---

## Task 4: ミューテーション検証(設計書 §5.3)

`GrepJumpResolver` は CLAUDE.md §4-A の**有効領域**(「テキスト選択範囲の算出」)。
GUI レイアウト・キーバインド・I/O には**行わない**。

**手順**: `src/kxEdit.Core/Search/GrepJumpResolver.cs` に 1 つずつ変異を入れ、
`dotnet build kxEdit.sln -c Release -warnaserror` の**あとに**テストを走らせ、
**赤になること**を確認してから元に戻す。

| # | 変異 | kill を期待するテスト |
|---|---|---|
| 1 | `hit.LineNumber - 1` → `hit.LineNumber` | `Resolve_LineMatchesAtGrepLineNumber_...` ほか多数 |
| 2 | `d <= NearbyLineWindow` → `d < NearbyLineWindow` | `Resolve_ExactlyAtNearbyWindowEdge_IsStillFound` |
| 3 | 近傍走査の up / down の探索順を入れ替える | `Resolve_DuplicateLinesEquidistant_PrefersTheOneAbove` |
| 4 | `if (hit.LineText.Length > 0)` ガードを削除 | `Resolve_EmptyLineText_DoesNotScanNearby_...` |
| 5 | `Stale` の `Length` を `0` → `hit.MatchLength` | `Resolve_JustBeyondNearbyWindow_IsStale` / L3 の Stale テスト |
| 6 | `LineEquals` の長さ篩い `length != text.Length` → `false`(常に文字列比較) | **どのテストも落ちないのが正しい**(篩いは最適化であって意味論ではない) |
| 7 | `Land` の `+ hit.MatchStartInLine` を削除 | `Resolve_LineMatchesAtGrepLineNumber_...`(`GetText` で内容まで見ている) |

**罠(過去に 3 回踏んでいる)**: ビルドエラーの検出に `grep "error CS"` を使わないこと。
Sonar アナライザの `error S###` を見落として**古い DLL に対してテストを走らせ**、
変異が生存したと誤認する。`grep -E " error [A-Z]+[0-9]+"` を使う。
また、対の関数を片方へ退化させる変異は S4144 で必ずビルドが落ちるので変異として無効。

**#6 が「落ちない」のが正**である点に注意する。ここで赤になるなら、篩いが意味論に
影響しているということなので実装を見直す。

結果(生存した変異があればその理由と対応)を設計書 §10 相当の実施記録として追記する。

---

## Task 5: L5 チェックリスト作成 + 品質ゲート

**Files:**
- Create: `docs/plans/2026-08-31-grep-jump-line-resolution-l5-checklist.md`
- Modify: `docs/plans/2026-08-31-grep-jump-line-resolution-design.md`(実施記録の追記)

### Step 1: L5 チェックリストを書く

既存の `2026-08-31-network-cloud-path-freeze-l5-checklist.md` の書式に合わせる。項目:

| # | 手順 | 期待 |
|---|---|---|
| 1 | ファイル A を開く → 先頭に数行挿入(未保存)→ grep でファイル A 内の語を検索 → 結果からジャンプ | NVDA が**編集後の正しい行**を読む。読み上げ行番号と画面のキャレット行が一致 |
| 2 | 同上でヒット行の内容を書き換えてからジャンプ | 「内容が変わっています」が読まれる。選択は起きない |
| 3 | 200 行超のファイルで末尾付近のヒットへジャンプ → ホイールで先頭へスクロール → 同じヒットを再アクティベート | キャレット行が画面内に戻る(弱視観点・目視) |
| 4 | 別ファイル(未オープン)のヒットへジャンプ | 従来どおりファイル名+行が読まれる(退行なし) |

**L5 の実施は保存操作を挟まないこと**(過去に保存で検証して結果を汚した事例あり)。

### Step 2: 品質ゲート

```
pwsh -File tools/pre-merge-check.ps1
```
Expected: **EXIT 0**

ログをファイルに落とすときは `Out-File -Encoding utf8` を明示する(Windows PowerShell 5.1 の
既定 UTF-16 LE は検索ツールが読めない)。

### Step 3: 最終ブランチレビュー(2 パス・CLAUDE.md §3 工程 5)

**別エージェントを 2 回、独立に起動する**(1 起動に混載しない):

1. **コード品質パス** — ミューテーション検証のスポットチェック込み
2. **脆弱性パス** — grep 結果は外部入力(ファイル内容由来の `LineText`)なので、
   長大な行・サロゲート・制御文字を含む `LineText` で resolver が破綻しないかを見る

指摘は **fixup commit** で積む(元 commit を書き換えない)。

### Step 4: コミット & PR

設計書に実施記録(ミューテーション結果・L5 実施状況)を追記してコミットしたうえで、

```
git push -u origin feature/grep-jump-line-resolution
gh pr create --base main
```

PR description(日本語)に含めるもの:
- 目的(A-18)と、3 つの食い違い源をまとめて扱った理由
- §3.3 の**意図的なスコープ追加**(A-3 同型の belt)
- **スコープ外**の明示(文字コード判定窓の統一=M-16 は未着手・grep の入口バッファ化は別テーマ)
- レビュー経緯(前倒し品質レビュー 1 回 + 最終 2 パス)と指摘の 3 択対応
- **L5 未実施ならその旨**(CLAUDE.md §5「L5 が最終ゲート」)

---

## 完了条件

- [ ] Task 1〜3 の実装とテストが緑・`-warnaserror` で 0 warning
- [ ] Task 4 のミューテーション検証で #1〜#5・#7 が kill、#6 が生存(＝正)
- [ ] `tools/pre-merge-check.ps1` が EXIT 0
- [ ] 最終ブランチレビュー 2 パス実施・指摘は 3 択で処理
- [ ] L5 チェックリスト作成済み(実施はユーザー依頼)
- [ ] PR 作成
