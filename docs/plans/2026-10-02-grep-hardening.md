# grep の堅牢化(grep-hardening)実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** grep の結果一覧に出る外部由来の文字列(行の本文・ファイル名)を無害化し、正規表現モードのキャンセルを行単位で効かせ、ファイルのシンボリックリンクで 64MB 上限を迂回できないようにし、保持する結果に上限を設ける。

**Architecture:** 表示の無害化は `GrepResultsWindow.Format` を internal static に切り出し、`SanitizeForDisplay.OneLine` に通す前に span で窓を切る。Core の `GrepService` では、ファイルを `FileStream` で開いて辿った後の長さで上限を判定し、`CollectLineHits` の行ループで `CancellationToken` と結果の上限(件数・保持する行の総文字数)を確かめる。打ち切りは `GrepOutcome.Truncated` で App に伝え、題名と発声で知らせる。

**Tech Stack:** C# / .NET 9 / WinForms / xUnit 2.9(Core.Tests・App.Tests)

**Spec:** `docs/plans/2026-09-27-perf-followups-design.md` の §3(全フェーズ共通の規約)と §7(フェーズ 3)

## 0. 調査の結果と、設計書 §7 からの精密化

### 0.1 G-2(ファイルのシンボリックリンク)

- ファイルのシンボリックリンクの作成には管理者権限(または開発者モード)が要り、この環境では作れなかった(`New-Item -ItemType SymbolicLink` → 「この操作には管理者特権が必要です」。`sudo` は無効)。**ユーザーの判断で、実測せずに直す**(2026-10-02)。
- 根拠: .NET の `FileInfo.Length` は Win32 の `GetFileAttributesEx` で属性を取る。この API はリンクを辿らず、リンク自体の情報を返す。一方 `File.ReadAllBytes` は `CreateFile` で開くのでリンクを辿る。よって、リンクを経由すると上限の判定(長さ 0)と実際に読む中身(リンク先)が食い違いうる。
- 直し方は設計書 §7.2 の第一候補のとおり、**`FileStream` で開いてから `fs.Length` で上限を判定し、そのストリームから読む**。リンクを辿った後の長さで判定でき、長さを見てから読むまでに伸びたファイルも上限を超えて読まない(TOCTOU も閉じる)。リンクでない場合の結果は変わらないので、迂回できなかったとしても害はない。
- UNC のリンク先への SMB 接続を防ぐことは本フェーズの範囲外(設計書 §7.2。必要なら別の設計にする)。

### 0.2 項目 14(結果が保持するメモリ)

- 合成データ(1 行 1〜10MB の minified 風 JS を 40 本・計 約 210M 字)を `GrepService.Search` で検索し、結果を保持したまま `GC.GetTotalMemory(true)` を測った(scratchpad の使い捨てプローブ)。

| 条件 | ヒット | 保持されるマネージドメモリ |
|---|---|---|
| リテラル `return` | 40 | 419.6 MiB |
| 正規表現 `return` | 40 | 419.6 MiB |
| 一致なし | 0 | 0 MiB |

- 保持量は「ヒットした行の総文字数 × 2 バイト」と一致した。**ヒットはたった 40 件**なので、設計書 §7.2 の第一候補(件数の上限)だけでは防げない。
- 逆の極端として、短い行がすべてヒットする 64MB のファイル(`a\n` の繰り返し)では、約 3,200 万件 × 約 100 バイトで 3 GB 規模になる(推計)。こちらは件数の上限で防げる。
- **ユーザーの判断(2026-10-02)**: 件数と文字数の 2 つの上限を置く。
  - ヒット **100,000 件**、または保持する行の総文字数 **64 Mi 字(67,108,864 字 = 128 MiB)** に達したら、走査を打ち切る。
  - `LineText` は切り詰めない(照合キーの不変条件 `MatchStartInLine + MatchLength <= LineText.Length` を守る。設計書 §7.2)。
  - `GrepOutcome` に `Truncated` を足し、結果窓の題名と発声で「上限で打ち切り」を伝える。
- **打ち切りの意味(本計画で決める)**: `Truncated=true` は「**見つかったのに結果へ入れなかったヒットが 1 件以上ある**」ことを表す。
  - 件数: 一致を見つけた時点で、すでに 100,000 件あれば、その一致を入れずに打ち切る。ちょうど 100,000 件で終わる検索は `Truncated=false`。
  - 文字数: 一致を見つけた時点で、すでに保持している総文字数が上限以上なら、その一致を入れずに打ち切る。上限をまたぐ 1 行は入れるので、最悪は「上限 + 1 行(最大 64MB のファイルの 1 行 = 64 Mi 字)」= 約 256 MiB になる。
- 発声: 今はヒットがあると要約を発声せず、結果窓のフォーカスに読み上げを任せている(`GrepController.cs:151-156`)。打ち切りは、読み取りエラーと同じく**必ず発声する**(黙って不完全な一覧を見せない)。

### 0.3 項目 12・G-1(表示の無害化)の精密化

- 設計書 §7.1 は「先に span で数百字に切ってから Trim と `OneLine`」とする。本計画では、**先頭の空白を span のまま飛ばしてから**(コピーなし)窓を切る。窓を先に切ると、先頭に空白が 1,024 字以上ある行が空に見えるため。`ReadOnlySpan<char>.TrimStart()` はコピーを作らないので、巨大な行の全体コピーを避けるという設計書の目的は保たれる。
- 窓は 1,024 字。表示は従来どおり 200 字 + `…`(`OneLine(…, 201)` の出力は「200 字 + `…`」で、従来の切り方と同じ形)。
- 窓で切ったのに、無害化の結果が 200 字以内に収まった場合(書式文字が多い行など)も、末尾に `…` を付けて「続きがある」ことを示す。

### 0.4 意図的な挙動差(PR に記載する)

設計書 §3.5 のフェーズ 3 の 2 行に、本計画で次の 2 行を足す。
- grep の結果一覧で、行の本文とファイル名を無害化して表示する(C0/C1 の制御文字は空白に、BiDi・書式文字は除去、連続する空白は 1 つに畳む)。【§3.5】
- grep をキャンセルしたとき、途中のファイルで見つかっていたヒットが結果に残る(タイムアウト時の既存の扱いと揃う)。【§3.5】
- **ヒットが 100,000 件、または保持する行の総文字数が 64 Mi 字に達したら走査を打ち切り、結果窓の題名と発声で知らせる。**【本計画で追加】
- **ファイルの大きさの上限(64MB)を、開いた後の長さ(シンボリックリンクを辿った後の長さ)で判定する。判定後に伸びたファイルは、判定した長さまでしか読まない。**【本計画で追加】

## Global Constraints

- 0 warning(`-warnaserror`)。CSharpier 整形(pre-commit フック)を `--no-verify` で飛ばさない。
- コミットメッセージは `fix|test|docs(grep): 要約` + 日本語本文。署名でハングする場合は `git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -F <UTF-8 のメッセージファイル>`。
- 末尾に `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>` を付ける。
- `GrepHit.LineText` は変えない(照合キー。設計書 §7.1)。
- 上限: ヒット 100,000 件・保持する行の総文字数 67,108,864 字(64 Mi 字)。
- 変異検証は行わない(設計書 §3.3)。陰性対照(修正を一時的に外してテストが落ちることの確認)は各タスクで行う。外すときは**行を消さず、条件を無効化する形**にする(行を消すとアナライザーでビルドが落ち、古い DLL のテストが緑に見える。設計書 §6.4 の実例)。陰性対照のたびに、ビルドが成功したことを確かめる。
- 前倒しの脆弱性レビューを Task 1(外部由来の文字列の表示)と Task 2(シンボリックリンク・ファイルの読み込み)で行う(設計書 §3.3)。
- L5 は必須(軽い): NVDA で結果一覧の読み上げ(NUL 入りの行・長い行)と、打ち切りの発声を確かめる(設計書 §7.4)。

## Review Focus

1. **普通の行の表示が変わらないこと**: 制御文字も連続する空白もない 200 字以内の行、200 字を超える ASCII の行は、従来と同じ文字列になる(Task 1 の `Format_PlainLine_IsUnchanged`・`Format_LongPlainLine_CutsAt200WithEllipsis`)。
2. **巨大な行で全体をコピーしないこと**: 1,000 万字の行でも、表示は 201 字以下で返る(Task 1 の `Format_HugeLine_IsBounded`。コピーの有無は値では測れないので、コードレビューで `LineText.Trim()` / `ToString()` が全体にかからないことを見る)。
3. **ちょうど上限で終わる検索を「打ち切り」と言わないこと**: 一致がちょうど上限件数のとき `Truncated=false`(Task 4 の `Exactly_max_hits_is_not_truncated`)。
4. **キャンセルが行の途中のファイルで効くこと**: 1 ファイルの中で、すべての行を照合し終える前に止まる(Task 3)。既存のファイル間のキャンセル(`Cooperative_cancellation_returns_partial_results`)が壊れていないこと。
5. **ヒットがあって打ち切られたときに黙らないこと**: 打ち切りは、ヒットがあってエラーがなくても発声される(Task 4 の `RunAsync_Truncated_AnnouncesSummary`)。

---

## ファイル構成

| ファイル | 変更 |
|---|---|
| `src/kxEdit.App/GrepResultsWindow.cs` | `Format` / `RelativePath` を internal static に切り出し、無害化。題名に打ち切りを出す |
| `src/kxEdit.App/GrepController.cs` | `Summary` に打ち切りを足し、打ち切りは必ず発声する |
| `src/kxEdit.Core/Search/GrepService.cs` | `ReadAllBytesBounded`・行ごとのキャンセル・上限 |
| `src/kxEdit.Core/Search/GrepTypes.cs` | `GrepOutcome.Truncated`・`GrepLimits` |
| `tests/kxEdit.App.Tests/GrepResultsFormatTests.cs` | 新規(表示の無害化・題名) |
| `tests/kxEdit.App.Tests/GrepControllerTests.cs` | 打ち切りの発声 |
| `tests/kxEdit.App.Tests/Fakes/FakeGrepSearchFn.cs` | `OutcomeWith` に `truncated` |
| `tests/kxEdit.Core.Tests/Search/GrepServiceTests.cs` | 読み込みの上限・キャンセル・結果の上限 |
| `docs/plans/2026-09-27-perf-followups-design.md` | §7.5 実施記録の追記(Task 5) |

---

### Task 1: 結果一覧の表示の無害化(項目 12・G-1)

**Files:**
- Modify: `src/kxEdit.App/GrepResultsWindow.cs:44,67-91`
- Create: `tests/kxEdit.App.Tests/GrepResultsFormatTests.cs`

**Interfaces:**
- Produces: `internal static string GrepResultsWindow.Format(GrepHit hit, string baseFolder)`

- [ ] **Step 1: 失敗するテストを書く**

`tests/kxEdit.App.Tests/GrepResultsFormatTests.cs`:

```csharp
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
        Assert.EndsWith(": ab cd ef", shown);
        Assert.DoesNotContain('\0', shown);
    }

    [Fact]
    public void Format_Rlo_InLineAndFileName_IsRemoved()
    {
        // G-1: U+202E(RLO)で拡張子を偽装したファイル名と、本文中の RLO を除去する。
        string shown = GrepResultsWindow.Format(
            Hit("x\u202Eyz", file: @"C:\work\invoice\u202Etxt.exe"),
            Base
        );
        Assert.Equal(@"invoicetxt.exe (行 3): xyz", shown);
    }

    [Fact]
    public void Format_Tab_And_RunsOfSpaces_AreCollapsed()
    {
        Assert.EndsWith(": a b c", GrepResultsWindow.Format(Hit("a\t\tb   c"), Base));
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
        Assert.EndsWith(": TARGET", shown);
    }

    [Fact]
    public void Format_WindowCutButShortAfterSanitize_AddsEllipsis()
    {
        // 窓の中が書式文字(ZWSP)ばかりで、無害化すると 200 字に満たなくても、
        // 窓で切った以上は続きがあるので "…" を付ける。
        string line = "ab" + new string('\u200B', 3_000) + "cd";
        Assert.EndsWith(": ab…", GrepResultsWindow.Format(Hit(line), Base));
    }

    [Fact]
    public void Format_DoesNotSplitSurrogatePairAtCut()
    {
        // 200 字目の直前で高サロゲートが切れないこと(U+20BB7 𠮷 は 2 code unit)。
        string line = new string('a', 199) + "\U00020BB7" + "tail";
        string shown = GrepResultsWindow.Format(Hit(line), Base);
        Assert.EndsWith(": " + new string('a', 199) + "…", shown);
    }

    [Fact]
    public void Format_PathOutsideBase_FallsBackToRelativeOrFull()
    {
        // GetRelativePath は別ドライブなら絶対パスを返す。その場合も無害化される。
        string shown = GrepResultsWindow.Format(Hit("x", file: "D:\\o\u202Eut.txt"), Base);
        Assert.StartsWith(@"D:\out.txt (行 3): ", shown);
    }
}
```

- [ ] **Step 2: 失敗を確かめる**

Run: `dotnet build kxEdit.sln -c Release -warnaserror`
Expected: ビルドエラー(`GrepResultsWindow.Format` が private instance で 2 引数ではない)。

- [ ] **Step 3: 実装する**

`src/kxEdit.App/GrepResultsWindow.cs` の先頭の using に `using kxEdit.Core.Text;` を足す。`Populate` の 44 行目を次にする:

```csharp
            _list.Items.Add(new Row(hit, Format(hit, _baseFolder)));
```

`Format` と `RelativePath`(67-91 行)を次に置き換える:

```csharp
    // 一覧に出す行の本文の最大文字数(超えたら "…" を付ける)。
    private const int MaxLineDisplay = 200;

    // 無害化に渡す前に、行の本文を切り出す窓の文字数。巨大な行(最大 64MB)の全体を
    // Trim / OneLine に渡してコピー・全走査させないため(perf-followups フェーズ 3・項目 14 の関連)。
    private const int RawLineWindow = 1024;

    /// <summary>
    /// 一覧の 1 行を作る。行の本文とファイル名は外部ファイル由来なので
    /// <see cref="SanitizeForDisplay.OneLine"/> で無害化する(perf-followups フェーズ 3・項目 12 / G-1:
    /// 8000 バイトより後ろの NUL、U+202E による拡張子の偽装)。
    /// <see cref="GrepHit.LineText"/> 自体はジャンプの照合キー(A-18)なので変えない。
    /// </summary>
    internal static string Format(GrepHit hit, string baseFolder)
    {
        string rel = SanitizeForDisplay.OneLine(RelativePath(baseFolder, hit.FilePath));

        // 先頭の空白は span のまま飛ばし(コピーしない)、そのうえで窓を切る。
        ReadOnlySpan<char> raw = hit.LineText.AsSpan().TrimStart();
        bool cutByWindow = raw.Length > RawLineWindow;
        if (cutByWindow)
        {
            int cut = RawLineWindow;
            if (char.IsHighSurrogate(raw[cut - 1]))
                cut--; // サロゲートペアを割らない
            raw = raw[..cut];
        }

        // OneLine は maxLength を超えると「maxLength - 1 字 + "…"」にする。従来の
        // 「200 字 + "…"」と同じ形にするため 201 を渡す。
        string line = SanitizeForDisplay.OneLine(raw.ToString(), MaxLineDisplay + 1).TrimStart();
        if (cutByWindow && !line.EndsWith('…'))
            line += "…";
        return $"{rel} (行 {hit.LineNumber}): {line}";
    }

    private static string RelativePath(string baseFolder, string full)
    {
        try
        {
            return Path.GetRelativePath(baseFolder, full);
        }
        catch
        {
            return full;
        }
    }
```

注: `OneLine` は末尾の空白を落とすので、従来の `Trim()` の末尾側はこれで保たれる。先頭側は `TrimStart()`(span)と、書式文字を除いた結果に空白が先頭へ来る場合のための最後の `TrimStart()` で保つ。

- [ ] **Step 4: 通ることを確かめる**

Run: `dotnet build kxEdit.sln -c Release -warnaserror`
Run: `dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~GrepResultsFormatTests"`
Expected: 10 件 PASS。

- [ ] **Step 5: 陰性対照**

`Format` の 2 か所の `SanitizeForDisplay.OneLine(` を、それぞれ一時的に恒等の形へ変える(行は消さない):
- `rel` の行: `string rel = RelativePath(baseFolder, hit.FilePath);`
- `line` の行: `string line = raw.ToString().TrimStart();`

Run: ビルド(成功を確かめる)→ 同じフィルターでテスト。
Expected: `Format_Nul_And_C1_BecomeSpace`・`Format_Rlo_InLineAndFileName_IsRemoved`・`Format_Tab_And_RunsOfSpaces_AreCollapsed`・`Format_PathOutsideBase_FallsBackToRelativeOrFull`・`Format_LongPlainLine_CutsAt200WithEllipsis` などが FAIL。確かめたら元に戻し、再ビルドして全件 PASS に戻ることを確かめる。

- [ ] **Step 6: App.Tests 全体**

Run: `dotnet test tests/kxEdit.App.Tests -c Release --no-build`
Expected: 全件 PASS。

- [ ] **Step 7: Commit**

```bash
git add src/kxEdit.App/GrepResultsWindow.cs tests/kxEdit.App.Tests/GrepResultsFormatTests.cs
git commit -m "fix(grep): 結果一覧の行の本文とファイル名を無害化して表示する"
```

本文: 項目 12(8000 バイトより後ろの NUL)と G-1(U+202E による拡張子の偽装)。巨大な行は窓で切ってから無害化する。`LineText` は照合キーなので変えない。

- [ ] **Step 8: 前倒しの脆弱性レビュー**(別エージェント。外部由来の文字列の表示)

---

### Task 2: 開いた後の長さで上限を判定する(G-2)

**Files:**
- Modify: `src/kxEdit.Core/Search/GrepService.cs:95-109`
- Test: `tests/kxEdit.Core.Tests/Search/GrepServiceTests.cs`

**Interfaces:**
- Produces: `internal static (byte[]? Bytes, long Length) GrepService.ReadAllBytesBounded(string path, long maxBytes)`

- [ ] **Step 1: 失敗するテストを書く**

`GrepServiceTests` の `Missing_folder_records_error_not_throws` の後に足す:

```csharp
    // ---- perf-followups フェーズ 3(G-2): 開いた後の長さで上限を判定する ----

    [Fact]
    public void ReadAllBytesBounded_returns_bytes_within_limit()
    {
        using var t = new TempDir();
        string p = t.WriteUtf8("a.txt", "0123456789");
        var (bytes, length) = GrepService.ReadAllBytesBounded(p, maxBytes: 10);
        Assert.Equal(10, length);
        Assert.Equal(Encoding.UTF8.GetBytes("0123456789"), bytes);
    }

    [Fact]
    public void ReadAllBytesBounded_returns_null_over_limit()
    {
        using var t = new TempDir();
        string p = t.WriteUtf8("a.txt", "0123456789");
        var (bytes, length) = GrepService.ReadAllBytesBounded(p, maxBytes: 9);
        Assert.Null(bytes);
        Assert.Equal(10, length);
    }

    [Fact]
    public void Oversized_file_is_skipped_with_error()
    {
        using var t = new TempDir();
        string big = Path.Combine(t.Root, "big.txt");
        using (var fs = File.Create(big))
            fs.SetLength(64L * 1024 * 1024 + 1); // 上限 + 1 バイト(NTFS は実際には書かない)
        t.WriteUtf8("small.txt", "TARGET\n");

        var outcome = GrepService.Search(Req(t.Root, "TARGET"));

        Assert.Single(outcome.Hits);
        var err = Assert.Single(outcome.Errors);
        Assert.Equal(big, err.Path);
        Assert.Contains("大きすぎます", err.Message);
    }

    [Fact]
    public void Symlink_to_oversized_file_is_judged_by_target_length()
    {
        // FileInfo.Length はリンク自体の長さ(0)を返し、ReadAllBytes はリンクを辿る。
        // 開いた後の長さで判定すれば、リンク先の大きさで上限が効く。
        using var t = new TempDir();
        string target = Path.Combine(t.Root, "target.bin");
        using (var fs = File.Create(target))
            fs.SetLength(64L * 1024 * 1024 + 1);
        string sub = Path.Combine(t.Root, "sub");
        Directory.CreateDirectory(sub);
        string link = Path.Combine(sub, "link.txt");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // Skip: シンボリックリンクを作れない環境(管理者権限・開発者モードなし)
        }

        var outcome = GrepService.Search(Req(sub, "TARGET"));

        var err = Assert.Single(outcome.Errors);
        Assert.Equal(link, err.Path);
        Assert.Contains("大きすぎます", err.Message);
    }
```

注: シンボリックリンクのテストは、リンクを作れない環境(この開発機・CI)では何も確かめずに抜ける(`OriginalPathValidatorTests` の reparse point のテストと同じ形)。G-2 は実測していない(§0.1)ので、判定の仕組みは `ReadAllBytesBounded` の 2 件と `Oversized_file_is_skipped_with_error` で確かめる。

- [ ] **Step 2: 失敗を確かめる**

Run: `dotnet build kxEdit.sln -c Release -warnaserror`
Expected: ビルドエラー(`ReadAllBytesBounded` がない)。

- [ ] **Step 3: 実装する**

`GrepService.Search` の try の先頭(`long size = new FileInfo(path).Length;` から `byte[] bytes = File.ReadAllBytes(path);` まで)を次に置き換える:

```csharp
                // G-2: 開いた後の長さで上限を判定する(FileInfo.Length はシンボリックリンク自体の
                // 長さを返すので、リンク経由だと上限を迂回して読めてしまう)。
                var (read, size) = ReadAllBytesBounded(path, MaxFileBytes);
                if (read is null)
                {
                    errors.Add(
                        new GrepError(
                            path,
                            $"ファイルが大きすぎます（{size:N0} バイト）。スキップしました。"
                        )
                    );
                    continue;
                }

                byte[] bytes = read;
```

`ContainsNul` の前に足す:

```csharp
    /// <summary>
    /// path を開き、開いた後の長さ(シンボリックリンクを辿った後の長さ)が maxBytes 以下なら全体を読む。
    /// 超えていれば Bytes=null を返し、読まない。判定してから読むまでにファイルが伸びても、
    /// 判定した長さまでしか読まない(TOCTOU を閉じる)。縮んだら読めたぶんだけを返す。
    /// 共有モードとバッファなしは <see cref="File.ReadAllBytes(string)"/> と同じ。
    /// </summary>
    internal static (byte[]? Bytes, long Length) ReadAllBytesBounded(string path, long maxBytes)
    {
        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.SequentialScan
        );
        long length = fs.Length;
        if (length > maxBytes)
            return (null, length);

        var bytes = new byte[length];
        int total = 0;
        while (total < bytes.Length)
        {
            int n = fs.Read(bytes, total, bytes.Length - total);
            if (n == 0)
                break;
            total += n;
        }
        if (total < bytes.Length)
            Array.Resize(ref bytes, total);
        return (bytes, length);
    }
```

- [ ] **Step 4: 通ることを確かめる**

Run: `dotnet build kxEdit.sln -c Release -warnaserror`
Run: `dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~GrepServiceTests"`
Expected: 全件 PASS。

- [ ] **Step 5: 陰性対照**

`ReadAllBytesBounded` の `if (length > maxBytes)` を一時的に `if (length > maxBytes && false)` にする(S2583 等で警告が出る場合は `-p:TreatWarningsAsErrors=false` を付けてビルドし、ビルドの成功を確かめる)。
Expected: `ReadAllBytesBounded_returns_null_over_limit` と `Oversized_file_is_skipped_with_error` が FAIL。戻して全件 PASS を確かめる。

- [ ] **Step 6: Core.Tests 全体**

Run: `dotnet test tests/kxEdit.Core.Tests -c Release --no-build`
Expected: 全件 PASS。

- [ ] **Step 7: Commit**

```bash
git add src/kxEdit.Core/Search/GrepService.cs tests/kxEdit.Core.Tests/Search/GrepServiceTests.cs
git commit -m "fix(grep): ファイルの大きさの上限を開いた後の長さで判定する"
```

本文: G-2。`FileInfo.Length` はシンボリックリンク自体の長さを返す。開いたストリームの長さで判定し、そのストリームから判定した長さまで読む。実測はしていない(管理者権限がなくリンクを作れない。ユーザー判断)。

- [ ] **Step 8: 前倒しの脆弱性レビュー**(別エージェント。シンボリックリンク・ファイルの読み込み)

---

### Task 3: 行ごとにキャンセルを確かめる(項目 13)

**Files:**
- Modify: `src/kxEdit.Core/Search/GrepService.cs`(`CollectLineHits` とその呼び出し)
- Test: `tests/kxEdit.Core.Tests/Search/GrepServiceTests.cs`

**Interfaces:**
- Consumes: なし
- Produces: `CollectLineHits(string path, string text, TextSearcher searcher, List<GrepHit> hits, CancellationToken ct)`(private。Task 4 が引数を足す)

- [ ] **Step 1: 失敗するテストを書く**

プリフィルタのテストの後に足す:

```csharp
    // ---- perf-followups フェーズ 3(項目 13): 行ごとにキャンセルを確かめる ----

    // 注: プリフィルタの差し替え口はリテラル検索でしか呼ばれない。項目 13 は正規表現モードの
    // 問題(行ごとに 1 秒のタイムアウトで「行数 × 約 1 秒」キャンセルできない)だが、両モードが
    // 共有する CollectLineHits の行ループを通すことで確かめる(時間に依存しない)。
    [Fact]
    public void Cancellation_inside_a_file_stops_line_matching()
    {
        using var t = new TempDir();
        var sb = new StringBuilder();
        for (int i = 0; i < 1000; i++)
            sb.Append("TARGET\n");
        t.WriteUtf8("many.txt", sb.ToString());

        using var cts = new CancellationTokenSource();
        var outcome = GrepService.Search(
            Req(t.Root, "TARGET"),
            progress: null,
            (searcher, text) =>
            {
                cts.Cancel(); // このファイルの行照合に入る直前でキャンセルする
                return true;
            },
            cts.Token
        );

        Assert.True(outcome.Cancelled);
        Assert.Equal(1, outcome.FilesScanned); // 前提: ファイルの中まで入った
        Assert.True(outcome.Hits.Count < 1000, $"hits={outcome.Hits.Count}");
    }

    [Fact]
    public void Hits_found_before_cancellation_are_kept()
    {
        using var t = new TempDir();
        t.WriteUtf8("a.txt", "TARGET\nTARGET\n");
        t.WriteUtf8("b.txt", "TARGET\nTARGET\n");

        using var cts = new CancellationTokenSource();
        int calls = 0;
        var outcome = GrepService.Search(
            Req(t.Root, "TARGET"),
            progress: null,
            (searcher, text) =>
            {
                if (++calls == 2)
                    cts.Cancel(); // 2 つ目のファイル(b.txt)の行照合の直前
                return true;
            },
            cts.Token
        );

        Assert.True(outcome.Cancelled);
        Assert.Equal(2, outcome.Hits.Count); // a.txt の 2 件は残る
        Assert.All(outcome.Hits, h => Assert.EndsWith("a.txt", h.FilePath));
        Assert.Equal(1, outcome.FilesMatched);
    }
```

- [ ] **Step 2: 失敗を確かめる**

Run: `dotnet build kxEdit.sln -c Release -warnaserror` の後 `dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~Cancellation_inside_a_file|FullyQualifiedName~Hits_found_before_cancellation"`
Expected: `Cancellation_inside_a_file_stops_line_matching` が FAIL(hits=1000)。`Hits_found_before_cancellation_are_kept` は FAIL(b.txt の 2 件も入って 4 件)。

- [ ] **Step 3: 実装する**

`CollectLineHits` に引数 `CancellationToken ct` を足し、`while (pos < n)` の本体の先頭に足す:

```csharp
            // 項目 13: 行ごとにキャンセルを確かめる(volatile 読み 1 回。行ごとの照合に比べて無視できる)。
            // キャンセル不能な時間の上限は、約 1 秒(1 行ぶんの Regex タイムアウト)+ リテラルの
            // プリフィルタの約 1 秒になる。見つかっていたヒットは残す(呼び出し側が Cancelled を立てる)。
            if (ct.IsCancellationRequested)
                return;
```

呼び出し側(`CollectLineHits(path, text, searcher, hits);`)を `CollectLineHits(path, text, searcher, hits, cancellationToken);` にする。`<summary>` の末尾に「ct がキャンセルされたら、その行の照合の前で戻る」を足す。

- [ ] **Step 4: 通ることを確かめる**

Run: ビルド → `dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~GrepServiceTests"`
Expected: 全件 PASS(既存の `Cooperative_cancellation_returns_partial_results`・`Precancelled_token_returns_cancelled` を含む)。

- [ ] **Step 5: 陰性対照**

`if (ct.IsCancellationRequested)` を一時的に `if (ct.IsCancellationRequested && false)` にする(警告が出る場合は `-p:TreatWarningsAsErrors=false`。ビルドの成功を確かめる)。
Expected: 新しい 2 件が FAIL。戻して PASS を確かめる。

- [ ] **Step 6: Commit**

```bash
git add src/kxEdit.Core/Search/GrepService.cs tests/kxEdit.Core.Tests/Search/GrepServiceTests.cs
git commit -m "fix(grep): 1 ファイルの中でも行ごとにキャンセルを確かめる"
```

本文: 項目 13。正規表現モードでは、1 ファイル内のキャンセル不能時間が「行数 × 約 1 秒」になりえた。

---

### Task 4: 結果の上限と打ち切りの通知(項目 14)

**Files:**
- Modify: `src/kxEdit.Core/Search/GrepTypes.cs`(`GrepOutcome`・`GrepLimits`)
- Modify: `src/kxEdit.Core/Search/GrepService.cs`
- Modify: `src/kxEdit.App/GrepResultsWindow.cs`(題名)
- Modify: `src/kxEdit.App/GrepController.cs:151-156,188-195`
- Modify: `tests/kxEdit.App.Tests/Fakes/FakeGrepSearchFn.cs`
- Test: `tests/kxEdit.Core.Tests/Search/GrepServiceTests.cs`・`tests/kxEdit.App.Tests/GrepControllerTests.cs`・`tests/kxEdit.App.Tests/GrepResultsFormatTests.cs`

**Interfaces:**
- Consumes: Task 3 の `CollectLineHits(..., CancellationToken ct)`
- Produces:
  - `GrepOutcome(..., bool Cancelled, bool Truncated = false)`(public。末尾に既定値付きで足すので既存の生成箇所は変えなくてよい)
  - `internal readonly record struct GrepLimits(int MaxHits, long MaxRetainedLineChars)` と `GrepLimits.Default`
  - `internal static GrepOutcome GrepService.Search(GrepRequest, IProgress<GrepProgress>?, Func<TextSearcher, string, bool>, GrepLimits, CancellationToken)`

- [ ] **Step 1: 失敗するテストを書く(Core)**

Task 3 のテストの後に足す:

```csharp
    // ---- perf-followups フェーズ 3(項目 14): 結果の上限 ----

    private static GrepOutcome SearchLimited(GrepRequest req, int maxHits, long maxChars) =>
        GrepService.Search(
            req,
            progress: null,
            GrepService.DefaultLiteralPrefilter,
            new GrepLimits(maxHits, maxChars),
            CancellationToken.None
        );

    [Fact]
    public void Default_limits_are_pinned()
    {
        Assert.Equal(100_000, GrepLimits.Default.MaxHits);
        Assert.Equal(64L * 1024 * 1024, GrepLimits.Default.MaxRetainedLineChars);
    }

    [Fact]
    public void Max_hits_truncates_and_stops_scanning()
    {
        using var t = new TempDir();
        t.WriteUtf8("a.txt", "TARGET\nTARGET\n");
        t.WriteUtf8("b.txt", "TARGET\nTARGET\n");
        t.WriteUtf8("c.txt", "TARGET\n");

        var outcome = SearchLimited(Req(t.Root, "TARGET"), maxHits: 3, maxChars: long.MaxValue);

        Assert.True(outcome.Truncated);
        Assert.False(outcome.Cancelled);
        Assert.Equal(3, outcome.Hits.Count);
        Assert.Equal(2, outcome.FilesMatched); // a と b(b は途中まで)
        Assert.Equal(2, outcome.FilesScanned); // c は走査しない
    }

    [Fact]
    public void Exactly_max_hits_is_not_truncated()
    {
        using var t = new TempDir();
        t.WriteUtf8("a.txt", "TARGET\nTARGET\nnone\n");
        t.WriteUtf8("b.txt", "TARGET\n");

        var outcome = SearchLimited(Req(t.Root, "TARGET"), maxHits: 3, maxChars: long.MaxValue);

        Assert.False(outcome.Truncated); // 入れなかったヒットはない
        Assert.Equal(3, outcome.Hits.Count);
        Assert.Equal(2, outcome.FilesScanned);
    }

    [Fact]
    public void Max_retained_chars_truncates_after_the_line_that_crosses_it()
    {
        using var t = new TempDir();
        // 1 行 10 字。上限 15 字: 1 行目(10)は入る。2 行目(計 20)も、入れる時点の保持量は
        // 10 < 15 なので入る。3 行目は保持量 20 >= 15 なので入れずに打ち切る。
        t.WriteUtf8("a.txt", "TARGETxxxx\nTARGETyyyy\nTARGETzzzz\n");

        var outcome = SearchLimited(Req(t.Root, "TARGET"), maxHits: int.MaxValue, maxChars: 15);

        Assert.True(outcome.Truncated);
        Assert.Equal(new[] { "TARGETxxxx", "TARGETyyyy" }, outcome.Hits.Select(h => h.LineText));
    }

    [Fact]
    public void Retained_chars_are_counted_across_files()
    {
        using var t = new TempDir();
        t.WriteUtf8("a.txt", "TARGETxxxx\n"); // 10 字
        t.WriteUtf8("b.txt", "TARGETyyyy\n"); // 入れる時点で 10 >= 10 → 打ち切り

        var outcome = SearchLimited(Req(t.Root, "TARGET"), maxHits: int.MaxValue, maxChars: 10);

        Assert.True(outcome.Truncated);
        var hit = Assert.Single(outcome.Hits);
        Assert.EndsWith("a.txt", hit.FilePath);
        Assert.Equal(1, outcome.FilesMatched);
    }

    [Fact]
    public void Public_search_applies_default_max_hits()
    {
        using var t = new TempDir();
        var sb = new StringBuilder();
        for (int i = 0; i < 100_001; i++)
            sb.Append("a\n");
        t.WriteUtf8("a.txt", sb.ToString());

        var outcome = GrepService.Search(Req(t.Root, "a"));

        Assert.True(outcome.Truncated);
        Assert.Equal(100_000, outcome.Hits.Count);
    }
```

- [ ] **Step 2: 失敗するテストを書く(App)**

`tests/kxEdit.App.Tests/Fakes/FakeGrepSearchFn.cs` の `OutcomeWith` に引数を足す:

```csharp
    public static GrepOutcome OutcomeWith(
        int hits,
        int errors = 0,
        bool cancelled = false,
        bool truncated = false
    )
```

戻り値を `return new GrepOutcome(hs, hits, hits > 0 ? 1 : 0, es, cancelled, truncated);` にする。

`GrepControllerTests` の `RunAsync_Cancelled_AnnouncesInterrupted` の後に足す:

```csharp
    [Fact]
    public void RunAsync_Truncated_AnnouncesSummary() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            host.SearchFn.DefaultOutcome = FakeGrepSearchFn.OutcomeWith(hits: 3, truncated: true);

            host.Grep.RunAsync().GetAwaiter().GetResult();

            Assert.Equal(1, host.Results.ShowResultsCount);
            // ヒットがあってエラーがなくても、打ち切りは必ず発声する(黙って不完全な一覧を見せない)。
            Assert.Contains(
                host.View.Notifications,
                s => s.Contains("3 行 / 1 ファイル") && s.Contains("上限に達したため打ち切り")
            );
        });
```

`GrepResultsFormatTests` の末尾に足す(題名):

```csharp
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
```

- [ ] **Step 3: 失敗を確かめる**

Run: `dotnet build kxEdit.sln -c Release -warnaserror`
Expected: ビルドエラー(`GrepLimits`・`Truncated` がない)。

- [ ] **Step 4: 実装する(Core の型)**

`src/kxEdit.Core/Search/GrepTypes.cs` の `GrepOutcome` を次に置き換える:

```csharp
/// <summary>
/// grep の結果一式。Cancelled=true は協調キャンセルで途中打ち切り（Hits は途中までの部分結果）。
/// FilesMatched は 1 件以上ヒットしたファイル数。
/// Truncated=true は、結果の上限(<see cref="GrepLimits"/>)に達したため、見つかったのに Hits へ
/// 入れなかったヒットが 1 件以上あり、そこで走査を打ち切ったことを表す
/// (perf-followups フェーズ 3・項目 14)。ちょうど上限で終わった検索は false。
/// </summary>
public sealed record GrepOutcome(
    IReadOnlyList<GrepHit> Hits,
    int FilesScanned,
    int FilesMatched,
    IReadOnlyList<GrepError> Errors,
    bool Cancelled,
    bool Truncated = false
);

/// <summary>
/// grep が保持する結果の上限(perf-followups フェーズ 3・項目 14)。一致を見つけた時点で、
/// すでに <see cref="MaxHits"/> 件あるか、保持している <see cref="GrepHit.LineText"/> の総文字数が
/// <see cref="MaxRetainedLineChars"/> 以上なら、その一致を入れずに走査を打ち切る。
/// 上限をまたぐ 1 行は入れるので、総文字数は最悪「上限 + 1 行」になる。
/// LineText は照合キー(A-18)なので切り詰めない。
/// </summary>
internal readonly record struct GrepLimits(int MaxHits, long MaxRetainedLineChars)
{
    /// <summary>
    /// 100,000 件・64 Mi 字(128 MiB)。1 行 1〜10MB の minified 風 JS 40 本で 40 件・420 MiB を
    /// 保持した計測(実装計画 2026-10-02-grep-hardening.md §0.2)から決めた。
    /// </summary>
    public static readonly GrepLimits Default = new(100_000, 64L * 1024 * 1024);
}
```

- [ ] **Step 5: 実装する(GrepService)**

既存の internal 4 引数 `Search` を、上限付きの 5 引数へ委譲する形にする:

```csharp
    /// <summary>
    /// <see cref="Search(GrepRequest, IProgress{GrepProgress}?, CancellationToken)"/> の本体。
    /// literalPrefilter はテストでプリフィルタを差し替えるための口(本番は <see cref="DefaultLiteralPrefilter"/>)。
    /// </summary>
    internal static GrepOutcome Search(
        GrepRequest request,
        IProgress<GrepProgress>? progress,
        Func<TextSearcher, string, bool> literalPrefilter,
        CancellationToken cancellationToken
    ) => Search(request, progress, literalPrefilter, GrepLimits.Default, cancellationToken);

    /// <summary>上限(<paramref name="limits"/>)を差し替えられる本体。テストで小さな上限を渡す。</summary>
    internal static GrepOutcome Search(
        GrepRequest request,
        IProgress<GrepProgress>? progress,
        Func<TextSearcher, string, bool> literalPrefilter,
        GrepLimits limits,
        CancellationToken cancellationToken
    )
    {
```

本体の変更:
- 変数の宣言に `bool truncated = false;` と `long retainedChars = 0;` を足す。
- 呼び出しを次にする:

```csharp
                if (
                    request.Options.UseRegex
                    || PassesLiteralPrefilter(literalPrefilter, searcher, text)
                )
                    truncated = CollectLineHits(
                        path,
                        text,
                        searcher,
                        hits,
                        ref retainedChars,
                        limits,
                        cancellationToken
                    );
```

- `finally` の後、進捗通知の前に足す:

```csharp
            // 項目 14: 上限に達したら、残りのファイルは走査しない。
            if (truncated)
                break;
```

- 末尾の `return` を `return new GrepOutcome(hits, filesScanned, filesMatched, errors, cancelled, truncated);` にする。

`CollectLineHits` を次の形にする(戻り値 = 打ち切ったか):

```csharp
    /// <summary>
    /// text を行（\r\n / \n / \r 区切り）に分け、各行の先頭マッチを 1 ヒットとして hits へ加える。
    /// 行頭の絶対 UTF-16 オフセットを厳密に積算し、AbsoluteOffset＝行頭＋行内マッチ位置とする。
    /// 末尾の改行は空の最終行を作らない（標準 grep の行勘定）。
    /// ct がキャンセルされたら、その行の照合の前で戻る(項目 13)。
    /// 一致を見つけた時点で上限(<paramref name="limits"/>)に達していれば、その一致を入れずに
    /// true を返す(項目 14)。retainedChars は hits が保持する LineText の総文字数(ファイルをまたいで積む)。
    /// </summary>
    private static bool CollectLineHits(
        string path,
        string text,
        TextSearcher searcher,
        List<GrepHit> hits,
        ref long retainedChars,
        GrepLimits limits,
        CancellationToken ct
    )
    {
        int pos = 0,
            lineNumber = 0,
            n = text.Length;
        while (pos < n)
        {
            // (Task 3 のキャンセル確認。return を return false にする)
            if (ct.IsCancellationRequested)
                return false;

            lineNumber++;
            int eol = pos;
            while (eol < n && text[eol] != '\r' && text[eol] != '\n')
                eol++;

            // 行内容 [pos, eol)。span で照合するので ^/$・先読み・後読みは行の外を見ない
            // (Substring して照合するのと同じ意味。P-8: 一致しない行の文字列を作らない)。
            var m = searcher.FindFirst(text.AsSpan(pos, eol - pos));
            if (m is { } hit)
            {
                if (hits.Count >= limits.MaxHits || retainedChars >= limits.MaxRetainedLineChars)
                    return true;
                hits.Add(
                    new GrepHit(
                        FilePath: path,
                        LineNumber: lineNumber,
                        Column: hit.Start + 1,
                        LineText: text.Substring(pos, eol - pos),
                        MatchStartInLine: hit.Start,
                        MatchLength: hit.Length,
                        AbsoluteOffset: pos + hit.Start
                    )
                );
                retainedChars += eol - pos;
            }

            if (eol >= n)
                break; // 末尾行（後続 EOL 無し）
            pos = (text[eol] == '\r' && eol + 1 < n && text[eol + 1] == '\n') ? eol + 2 : eol + 1;
        }
        return false;
    }
```

Task 3 で入れたキャンセル確認のコメントはそのまま残す(`return;` → `return false;` だけ変える)。

注(`RegexMatchTimeoutException`): `CollectLineHits` の途中でタイムアウトすると、その時点の `truncated` は false のまま(代入の前に例外が出る)。既存どおり、そのファイルはエラーに積んで次へ進む。

- [ ] **Step 6: 実装する(App)**

`GrepResultsWindow.Populate` の題名の接尾辞に足す(`outcome.Cancelled` の行の直後):

```csharp
        if (outcome.Truncated)
            suffix += "（上限で打ち切り）";
```

`GrepController` の発声の条件(151-156 行)を次にする:

```csharp
            // ヒットがあれば結果窓のフォーカスが SR を駆動するので二重読みを避ける。ただし
            // 読み取りエラーがある時は走査が不完全な旨を必ず音声化する（誤った「見つかりません」防止）。
            // 上限による打ち切りも同じく必ず音声化する(黙って不完全な一覧を見せない。perf-followups フェーズ 3)。
            if (outcome.Hits.Count == 0 || outcome.Errors.Count > 0 || outcome.Truncated)
                d.RaiseNotification(Summary(outcome));
            else
                d.SetStatus(Summary(outcome));
```

`Summary` を次にする:

```csharp
    private static string Summary(GrepOutcome o)
    {
        string errs = o.Errors.Count > 0 ? $"・読み取り不可 {o.Errors.Count} 件" : "";
        if (o.Hits.Count == 0)
            return (o.Cancelled ? "中断しました（0 件）" : "見つかりません") + errs;
        string head = o.Cancelled ? "中断: " : "";
        string trunc = o.Truncated ? "・上限に達したため打ち切り" : "";
        return $"{head}{o.Hits.Count} 行 / {o.FilesMatched} ファイル{trunc}{errs}";
    }
```

- [ ] **Step 7: 通ることを確かめる**

Run: `dotnet build kxEdit.sln -c Release -warnaserror`
Run: `dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~GrepServiceTests"`
Run: `dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~Grep"`
Expected: 全件 PASS。

- [ ] **Step 8: 陰性対照**

1. `CollectLineHits` の上限の条件を一時的に `if ((hits.Count >= limits.MaxHits || retainedChars >= limits.MaxRetainedLineChars) && false)` にする(警告が出る場合は `-p:TreatWarningsAsErrors=false`。ビルドの成功を確かめる)。
   Expected: `Max_hits_truncates_and_stops_scanning`・`Max_retained_chars_truncates_after_the_line_that_crosses_it`・`Retained_chars_are_counted_across_files`・`Public_search_applies_default_max_hits` が FAIL。戻す。
2. `GrepController` の条件から `|| outcome.Truncated` を一時的に `|| (outcome.Truncated && false)` にする。
   Expected: `RunAsync_Truncated_AnnouncesSummary` が FAIL。戻す。
3. 戻した後に再ビルドし、全件 PASS を確かめる。

- [ ] **Step 9: 全テスト**

Run: `dotnet test tests/kxEdit.Core.Tests -c Release --no-build` と `dotnet test tests/kxEdit.App.Tests -c Release --no-build`
Expected: 全件 PASS。

- [ ] **Step 10: Commit**

```bash
git add src/kxEdit.Core/Search/GrepTypes.cs src/kxEdit.Core/Search/GrepService.cs src/kxEdit.App/GrepResultsWindow.cs src/kxEdit.App/GrepController.cs tests/
git commit -m "fix(grep): 結果の件数と保持する行の文字数に上限を設け、打ち切りを知らせる"
```

本文: 項目 14。巨大な行に当たるファイル 40 本で 40 件・420 MiB を保持した(計測は実装計画 §0.2)。件数だけの上限では防げないので、保持する行の総文字数にも上限を置く(ユーザー判断)。打ち切りは題名と発声で知らせる。

---

### Task 5: 最終レビュー・品質ゲート・L5・実施記録

- [ ] **Step 1: 最終ブランチレビュー(2 パス)**

CLAUDE.md §3 の 5。コード品質パスと脆弱性パスを、別々のエージェントで行う(ミューテーション検証は行わない。設計書 §3.3)。指摘は fixup commit で反映する。並走させる場合は、レビューエージェントにリポジトリでのビルドをさせない(scratchpad の専用ディレクトリを割り当てる)。

- [ ] **Step 2: 品質ゲート**

Run: `pwsh -File tools/pre-merge-check.ps1`
Expected: EXIT 0

- [ ] **Step 3: L5(ユーザーに依頼)**

`tools/sr-regression.ps1` を実行して EXIT 0 を確かめる。そのうえで、NVDA で次を確かめてもらう(fixture は scratchpad に用意する):
1. 8000 バイトより後ろに NUL を含む行がヒットしたとき、結果一覧の項目が NUL で途切れずに読まれる。
2. 200 字を超える行が「…」で終わり、読み上げが長すぎない。
3. U+202E を含むファイル名が、偽装された順序で読まれない。
4. 100,001 行がすべて一致するファイルを検索すると、「上限に達したため打ち切り」が発声され、題名に「（上限で打ち切り）」が出る。

- [ ] **Step 4: 実施記録**

`docs/plans/2026-09-27-perf-followups-design.md` の §7.4 の後に `### 7.5 実施記録(2026-10-02・PR #<番号>)` を足す。§6.4 と同じ見出し(成果物 / 完了条件 / 本節からの精密化 / 申し送り)で、本計画 §0 の調査結果と判断、テスト数、陰性対照、レビュー、L5 の結果を書く。

```bash
git add docs/plans/2026-09-27-perf-followups-design.md
git commit -m "docs(perf): フェーズ 3(grep の堅牢化)の実施記録"
```

- [ ] **Step 5: PR**

PR description(日本語)に、目的・§0.4 の意図的な挙動差・§0.2 の計測値・G-2 を実測していないこと・L5 の結果・レビューの経緯を書く。
