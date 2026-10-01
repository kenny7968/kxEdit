using kxEdit.Core.Search;
using Xunit;

namespace kxEdit.App.Tests;

/// <summary>
/// grep の結果一覧の 1 行の整形(perf-followups フェーズ 3・項目 12・G-1)。
/// 行の本文とファイル名は外部ファイル由来なので、SR と画面に載せる前に無害化する。
/// </summary>
public class GrepResultsFormatTests
{
    private const string Base = @"C:\work";

    private static GrepHit Hit(string lineText, string file = @"C:\work\src\a.txt", int line = 3) =>
        new(file, line, 1, lineText, 0, Math.Min(1, lineText.Length), 0);

    [Fact]
    public void Format_PlainLine_IsUnchanged()
    {
        Assert.Equal(
            @"src\a.txt (行 3): var x = 1;",
            GrepResultsWindow.Format(Hit("    var x = 1;   "), Base)
        );
    }

    [Fact]
    public void Format_LongPlainLine_CutsAt200WithEllipsis()
    {
        string line = new string('a', 199) + "bcdefg";
        string shown = GrepResultsWindow.Format(Hit(line), Base);
        Assert.Equal(@"src\a.txt (行 3): " + new string('a', 199) + "b…", shown);
    }

    [Fact]
    public void Format_Exactly201Chars_CutsAt200WithEllipsis()
    {
        // 従来(Trim の後で Length > 200 なら 200 字 + "…")と同じ境界であること。
        string shown = GrepResultsWindow.Format(Hit(new string('a', 201)), Base);
        Assert.EndsWith(": " + new string('a', 200) + "…", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_TrailingIdeographicSpace_IsTrimmed()
    {
        // 従来の Trim() は行末の全角空白(U+3000)・NBSP も落としていた。
        string shown = GrepResultsWindow.Format(Hit("設定値　 "), Base);
        Assert.EndsWith(": 設定値", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_WindowCutAndExactly201AfterSanitize_IsBounded()
    {
        // 窓で切り、無害化した結果がちょうど 201 字でも、本文は 200 字 + "…" に収まる。
        string line = new string('a', 201) + new string('​', 900) + "zzz";
        string shown = GrepResultsWindow.Format(Hit(line), Base);
        Assert.EndsWith(": " + new string('a', 200) + "…", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_LongPath_KeepsTailWithinLimit()
    {
        // 最終レビュー(脆弱性)I-1: 深い階層で相対パスが数万字になっても、表示は末尾 260 字に収める
        // (ヒットごとに数万字の表示文字列を作って GB 級のメモリと UI スレッドの時間を使わない)。
        // ファイル名と拡張子が見えるよう、先頭を "…" にして末尾を残す。
        string deep = string.Concat(Enumerable.Repeat(new string('d', 200) + "\\", 150));
        string shown = GrepResultsWindow.Format(
            Hit("x", file: Base + "\\" + deep + "name.txt"),
            Base
        );
        string rel = shown[..shown.IndexOf(" (行 3): ", StringComparison.Ordinal)];
        Assert.Equal(260, rel.Length);
        Assert.StartsWith("…", rel, StringComparison.Ordinal);
        Assert.EndsWith("\\name.txt", rel, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_LongPathWithFormatChars_IsSanitizedAndBounded()
    {
        // 長いパスの末尾に RLO があっても除去され、長さの上限も保たれる。
        string deep = string.Concat(Enumerable.Repeat(new string('d', 200) + "\\", 150));
        string shown = GrepResultsWindow.Format(
            Hit("x", file: Base + "\\" + deep + "invoice‮txt.exe"),
            Base
        );
        string rel = shown[..shown.IndexOf(" (行 3): ", StringComparison.Ordinal)];
        Assert.True(rel.Length <= 260, $"len={rel.Length}");
        Assert.EndsWith("\\invoicetxt.exe", rel, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_PathOf260Chars_IsNotCut()
    {
        // 前提(境界): ちょうど 260 字の相対パスは省略しない。
        string relPath = new string('p', 252) + "\\a.txt"; // 252 + 1 + 5 = 258 → 余りを足して 260 にする
        relPath = "pp" + relPath;
        Assert.Equal(260, relPath.Length);
        string shown = GrepResultsWindow.Format(Hit("x", file: Base + "\\" + relPath), Base);
        Assert.StartsWith(relPath + " (行 3): ", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_Nul_And_C1_BecomeSpace()
    {
        // NUL(項目 12: 8000 バイトより後ろの NUL)と C1 の NEL(U+0085)は空白 1 つに畳む。
        string shown = GrepResultsWindow.Format(Hit("ab\0\0cd\u0085ef"), Base);
        Assert.EndsWith(": ab cd ef", shown, StringComparison.Ordinal);
        Assert.DoesNotContain('\0', shown);
    }

    [Fact]
    public void Format_Rlo_InLineAndFileName_IsRemoved()
    {
        // G-1: U+202E(RLO)で拡張子を偽装したファイル名と、本文中の RLO を除去する。
        string shown = GrepResultsWindow.Format(
            Hit("x\u202Eyz", file: "C:\\work\\invoice\u202Etxt.exe"),
            Base
        );
        Assert.Equal(@"invoicetxt.exe (行 3): xyz", shown);
    }

    [Fact]
    public void Format_Tab_And_RunsOfSpaces_AreCollapsed()
    {
        Assert.EndsWith(
            ": a b c",
            GrepResultsWindow.Format(Hit("a\t\tb   c"), Base),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Format_HugeLine_IsBounded()
    {
        // 巨大な行(1,000 万字)でも、表示は 200 字 + "…" に収まる。
        string shown = GrepResultsWindow.Format(Hit(new string('z', 10_000_000)), Base);
        string body = shown[(shown.IndexOf("): ", StringComparison.Ordinal) + 3)..];
        Assert.Equal(new string('z', 200) + "…", body);
    }

    [Fact]
    public void Format_LeadingWhitespaceLongerThanWindow_StillShowsText()
    {
        // 窓(1,024 字)より長い先頭の空白は、窓を切る前に飛ばす。
        string shown = GrepResultsWindow.Format(Hit(new string(' ', 5_000) + "TARGET"), Base);
        Assert.EndsWith(": TARGET", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_WindowCutButShortAfterSanitize_AddsEllipsis()
    {
        // 窓の中が書式文字(ZWSP)ばかりで、無害化すると 200 字に満たなくても、
        // 窓で切った以上は続きがあるので "…" を付ける。
        string line = "ab" + new string('\u200B', 3_000) + "cd";
        Assert.EndsWith(
            ": ab…",
            GrepResultsWindow.Format(Hit(line), Base),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Format_DoesNotSplitSurrogatePairAtCut()
    {
        // 200 字目の直前で高サロゲートが切れないこと(U+20BB7 𠮷 は 2 code unit)。
        string line = new string('a', 199) + "\U00020BB7" + "tail";
        string shown = GrepResultsWindow.Format(Hit(line), Base);
        Assert.EndsWith(": " + new string('a', 199) + "…", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_PathOutsideBase_FallsBackToRelativeOrFull()
    {
        // GetRelativePath は別ドライブなら絶対パスを返す。その場合も無害化される。
        string shown = GrepResultsWindow.Format(Hit("x", file: "D:\\o\u202Eut.txt"), Base);
        Assert.StartsWith(@"D:\out.txt (行 3): ", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void Populate_Truncated_ShowsInTitle() =>
        Sta.Run(() =>
        {
            using var w = new GrepResultsWindow(new GrepResultsCallbacks(_ => { }));
            var hits = new[] { Hit("TARGET") };
            w.Populate("TARGET", Base, new GrepOutcome(hits, 1, 1, [], false, Truncated: true));
            Assert.Contains("（上限で打ち切り）", w.Text);

            w.Populate("TARGET", Base, new GrepOutcome(hits, 1, 1, [], false));
            Assert.DoesNotContain("打ち切り", w.Text); // 前提: 打ち切りでなければ出ない
        });
}
