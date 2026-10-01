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
