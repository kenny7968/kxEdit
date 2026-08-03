# UIA 単語単位の境界ずれとコスト 調査 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** UIA の単語スパンが「空白のみ区切り」であることが生む①規則のずれ(F-3)と②空白ゼロ長大行での全走査(F-5)を、`src/` を 1 行も変えずに実測・可視化し、修正方針を確定できる材料を揃える。

**Architecture:** `tests/yEdit.Editor.Smoke` に `--wordunit` サブコマンドを 1 ファイルで追加する。実 `EditorControl` に `(IUiaTextHost)` でキャストして本番の UIA 実装を直接叩き、Core の `WordBoundary`(public API)と並べて表を出す。修正候補も `src/` ではなくベンチ内で構成し、before / after を同じ表に並べる。

**Tech Stack:** .NET 9 / WinForms / xUnit(本計画ではテストは追加しない)/ CSharpier / Husky.Net

**設計書:** `docs/plans/2026-08-03-uia-word-unit-triage-design.md`

---

## 前提知識(この計画を実行する人向け)

このリポジトリを知らない前提で、必要な事実だけ列挙する。

### 単語に関する実装が 3 つある

| # | 概念 | 実装 | 規則 |
|---|---|---|---|
| 1 | 次の単語の頭 | `src/yEdit.Core/Editing/WordBoundary.cs:60,97`(`NextWordStart` / `PrevWordStart`・**public**) | 文字クラス 8 分類 |
| 2 | 単語の左端 / 右端 | `src/yEdit.Editor/InputRouter.cs:539-568`(`PrevWordBoundary` / `NextWordBoundary`・**private**) | 文字クラス(1 を組み合わせて構成) |
| 3 | 単語の左端 / 右端 | `src/yEdit.Editor/UiaTextHostAdapter.cs:529-556`(`WordBoundary_WordStart` / `_WordEnd`・**private**) | **空白 / CR / LF のみ** |

2 はダブルクリック単語選択、3 は SR の読み上げスパン(`src/yEdit.Accessibility/TextRangeProviderV2.cs:58-64`)。
**同じ概念に 2 実装があり規則が違う**というのが調査対象である。

### 本番コードへの到達方法

`tests/yEdit.Editor.Tests/UiaTextHostAdapterClampTests.cs:19-22` が示すとおり、

```csharp
using var ctrl = new EditorControl();
ctrl.SetSource(TextBuffer.FromString("abc"));
var host = (IUiaTextHost)ctrl;
host.WordStart(0);   // → UiaTextHostAdapter の実装へ届く
```

で Handle 生成も Form も不要で本番実装を呼べる。`EditorControl.Uia.cs:86-92` が
`UiaTextHostAdapter` へ委譲している。**素朴実装を再実装してはならない**(現状の採取は必ず実物を叩く)。

### `TextSnapshot` の使い方

```csharp
var buf = TextBuffer.FromString(text);   // yEdit.Core.Buffers
ctrl.SetSource(buf);
var snap = buf.Current;                  // TextSnapshot
snap.CharLength;                         // UTF-16 code unit 数
snap.GetChar(i);                         // char
snap.GetText(start, length);             // string
```

### ビルド・品質の制約

- ソリューションは `-warnaserror` でビルドされる(`tools/pre-merge-check.ps1:32`)。**警告 0 が必須**。
- `yEdit.Editor.Smoke` は `yEdit.sln` に含まれる(`yEdit.sln:22`)ので、この新ファイルは品質ゲートのビルド対象になる。
- pre-commit フック(Husky.Net)が CSharpier で整形する。**`--no-verify` で飛ばさない**。
- `--wordunit` は調査用なので**判定ゲートを持たない**(常に EXIT 0)。既存 `--largeline` と同じ流儀。

### コミット規約

`feat|fix|docs|test|refactor|chore(scope): 要約` + 日本語本文。末尾に

```
Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

**注意:** Bash ツールで `git commit -m @'...'@` は使えない(PowerShell の here-string 構文)。
長いメッセージはファイルへ書いて `git commit -F <path>` を使う。

---

## Task 1: `--wordunit` 新設 = 現状採取

**Files:**
- Create: `tests/yEdit.Editor.Smoke/WordUnitBench.cs`
- Modify: `tests/yEdit.Editor.Smoke/Program.cs`(`--largeline` ブロックの直後に配線)

このタスクは設計書 §4.1(ずれの採取)と §4.2(コスト実測)を実装する。
テストではなくベンチなので TDD の赤→緑サイクルは適用しない。代わりに
**各ステップで実行して出力を目視確認する**ことを検証とする。

### Step 1: `WordUnitBench.cs` を作成する

`tests/yEdit.Editor.Smoke/WordUnitBench.cs` を新規作成する。

```csharp
using System.Diagnostics;
using System.Text;
using yEdit.Accessibility;
using yEdit.Core.Buffers;
using yEdit.Core.Editing;

namespace yEdit.Editor.Smoke;

/// <summary>
/// 2026-08-03 UIA 単語単位調査(docs/plans/2026-08-03-uia-word-unit-triage-design.md §4)。
/// UIA が SR へ返す単語スパンは <c>UiaTextHostAdapter.WordBoundary_WordStart / _WordEnd</c> の
/// 「空白 / CR / LF のみを区切りとする」素朴実装で決まる。一方 Ctrl+←→(<c>WordBoundary</c>)と
/// ダブルクリック単語選択(<c>InputRouter.PrevWordBoundary / NextWordBoundary</c>)は
/// 文字クラス規則を使う。本ベンチは
/// <list type="number">
/// <item>その規則差が実際にどうずれるか(§4.1)</item>
/// <item>空白ゼロ長大行で素朴実装が行頭まで全走査するコスト(§4.2)</item>
/// </list>
/// を採取する。調査用のため判定ゲートは持たない(常に EXIT 0)。
/// </summary>
/// <remarks>
/// <b>現状の採取は必ず実物を叩く。</b> <c>(IUiaTextHost)EditorControl</c> 経由で
/// <c>UiaTextHostAdapter</c> の実装へ届くため、素朴実装をベンチ側へ写経しない
/// (写経すると「同じ壊れ方をする参照」になり、ずれを検出できなくなる)。
/// Handle 生成も Form も不要=GDI を通らない。
/// </remarks>
internal static class WordUnitBench
{
    public static int Run()
    {
        PrintDivergenceTable();
        PrintCostTable();

        Console.WriteLine();
        Console.WriteLine("(調査用ベンチのため判定ゲートなし) EXIT 0");
        return 0;
    }

    // ===== §4.1 ずれの採取 =====

    /// <summary>目視可能な短い 1 行 fixture。<c>en</c> は「全部ずれる」という誤結論を防ぐ対照群。</summary>
    private static readonly (string Name, string Text)[] Fixtures =
    [
        ("ja", "今日は晴れです。"),
        ("jakana", "メモ帳のテキストを編集"),
        ("en", "foo bar baz"),
        ("sym", "foo(bar)=baz;"),
        ("num", "abc123def"),
        ("wsp", "今日　は"), // 全角空白。ClassOf は U+3000 を Other 扱いにしている
        ("emoji", "あ\U0001F600い\U0001F600う"),
    ];

    private static void PrintDivergenceTable()
    {
        Console.WriteLine("## §4.1 ずれの採取");

        foreach (var (name, text) in Fixtures)
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString(text);
            ctrl.SetSource(buf);
            var snap = buf.Current;
            var host = (IUiaTextHost)ctrl;

            Console.WriteLine();
            Console.WriteLine($"### {name}: `{text}` ({snap.CharLength} code units)");
            Console.WriteLine();
            Console.WriteLine("| pos | Ctrl+→ 移動先 | SR 読みスパン | ダブルクリック選択 | 一致 |");
            Console.WriteLine("|---|---|---|---|---|");

            int pos = 0;
            while (pos < snap.CharLength)
            {
                // SR が読むスパン: TextRangeProviderV2.ExpandToEnclosingUnit(TextUnit.Word) と同じ順で叩く
                int srStart = host.WordStart(pos);
                int srEnd = host.WordEnd(srStart);
                if (srEnd == srStart)
                    srEnd = host.NextChar(srStart); // TextRangeProviderV2.cs:62-63 の縮退分岐

                int dcStart = DoubleClickWordStart(snap, pos);
                int dcEnd = DoubleClickWordEnd(snap, pos);

                int next = WordBoundary.NextWordStart(snap, pos);
                bool same = srStart == dcStart && srEnd == dcEnd;

                Console.WriteLine(
                    $"| {pos} | {next} | {Span(snap, srStart, srEnd)} | {Span(snap, dcStart, dcEnd)} | {(same ? "=" : "**≠**")} |"
                );

                if (next <= pos)
                    break; // EOF で進まなくなったら終了(無限ループ防止)
                pos = next;
            }
        }
    }

    private static string Span(TextSnapshot snap, int start, int end) =>
        $"[{start},{end}) `{snap.GetText(start, end - start)}`";

    // ===== ダブルクリック単語選択の規則(InputRouter.cs:539-568 と同一構成) =====
    // InputRouter の実装は private のため、public な WordBoundary から同じ構成で組み直している。
    // 2 実装同期のリスクがあるため、実際の修正では Core へ抽出して 1 本化すること(設計書 §4.3)。

    /// <summary>target を含む単語の左端(<c>InputRouter.PrevWordBoundary</c> と同一)。</summary>
    private static int DoubleClickWordStart(TextSnapshot snap, int target)
    {
        if (target <= 0)
            return 0;
        if (target >= snap.CharLength)
            return WordBoundary.PrevWordStart(snap, target);
        return WordBoundary.PrevWordStart(snap, target + 1);
    }

    /// <summary>target の word run の終端(<c>InputRouter.NextWordBoundary</c> と同一)。</summary>
    private static int DoubleClickWordEnd(TextSnapshot snap, int target)
    {
        if (target >= snap.CharLength)
            return snap.CharLength;
        int nextWordStart = WordBoundary.NextWordStart(snap, target);
        while (nextWordStart > target)
        {
            char c = snap.GetChar(nextWordStart - 1);
            if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                break;
            nextWordStart--;
        }
        return nextWordStart;
    }

    // ===== §4.2 コスト実測 =====

    /// <summary>1 条件がこの秒数を超えたら、その kind の以降の行長をスキップする(--largeline と同じ流儀)。</summary>
    private const double SkipThresholdSec = 30.0;

    private static void PrintCostTable()
    {
        Console.WriteLine();
        Console.WriteLine("## §4.2 コスト実測(空白・改行ゼロの単一長大行)");
        Console.WriteLine();
        Console.WriteLine(
            "| kind | chars | WordStart(末尾から) ms | WordEnd(先頭から) ms | 合計 ms | PrevWordStart ms | 合計/Prev |"
        );
        Console.WriteLine("|---|---|---|---|---|---|---|");

        long sink = 0;

        foreach (string kind in new[] { "ascii", "cjk", "jamix" })
        {
            foreach (int len in new[] { 100_000, 500_000, 2_000_000 })
            {
                using var ctrl = new EditorControl();
                var buf = TextBuffer.FromString(MakeSingleLine(len, kind));
                ctrl.SetSource(buf);
                var snap = buf.Current;
                var host = (IUiaTextHost)ctrl;

                sink += host.WordStart(snap.CharLength); // ウォームアップ(JIT・計測外)
                sink += host.WordEnd(0);
                sink += WordBoundary.PrevWordStart(snap, snap.CharLength);

                double startMs = BestOf3(() => sink += host.WordStart(snap.CharLength));
                double endMs = BestOf3(() => sink += host.WordEnd(0));
                double prevMs = BestOf3(() =>
                    sink += WordBoundary.PrevWordStart(snap, snap.CharLength)
                );
                double totalMs = startMs + endMs;

                Console.WriteLine(
                    $"| {kind} | {len:N0} | {startMs:F1} | {endMs:F1} | **{totalMs:F1}** | {prevMs:F1} | {totalMs / prevMs:F2}x |"
                );

                if (totalMs / 1000.0 > SkipThresholdSec)
                {
                    Console.WriteLine(
                        $"（{totalMs / 1000.0:F1}s > {SkipThresholdSec}s のため {kind} の以降の行長をスキップ）"
                    );
                    break;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"(sink={sink})");
    }

    /// <summary>3 回まわして最小値を返す(--largeline / --characcess の流儀)。</summary>
    private static double BestOf3(Action action)
    {
        double best = double.MaxValue;
        for (int r = 0; r < 3; r++)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    /// <summary>
    /// 空白も改行も含まない 1 行を作る。
    /// </summary>
    /// <remarks>
    /// <list type="table">
    /// <item>
    ///   <term>ascii</term>
    ///   <description>a-z。<b>全て Latin クラス = 単一クラスの長大連続</b>。
    ///   文字クラス規則へ揃えても走査は縮まない対照群(= 候補 B が要る条件)。
    ///   生成規則は <c>LargeLineBench.MakeSingleLine</c> と同一・同一シードで前後比較が成立する。</description>
    /// </item>
    /// <item>
    ///   <term>cjk</term>
    ///   <description>U+3042 から 40 種。<b>全て Hiragana クラス</b>なので ascii と同じく単一クラス。
    ///   同上。</description>
    /// </item>
    /// <item>
    ///   <term>jamix</term>
    ///   <description><b>本調査で追加</b>。漢字 / ひらがな / カタカナが 1〜4 文字ごとに交替する
    ///   現実的な日本語。<b>候補 A(文字クラス規則)の効果はこの kind でしか見えない</b> —
    ///   ascii / cjk は単一クラスなので候補 A を当てても全走査のままになる。</description>
    /// </item>
    /// </list>
    /// </remarks>
    private static string MakeSingleLine(int chars, string kind)
    {
        var sb = new StringBuilder(chars);
        var r = new Random(20260803);
        while (sb.Length < chars)
        {
            switch (kind)
            {
                case "ascii":
                    sb.Append((char)('a' + r.Next(26)));
                    break;
                case "cjk":
                    sb.Append((char)('あ' + r.Next(40)));
                    break;
                default: // "jamix"
                    AppendJapaneseRun(sb, r);
                    break;
            }
        }
        return sb.ToString(0, chars);
    }

    /// <summary>漢字 / ひらがな / カタカナのいずれかを 1〜4 文字続けて積む(クラス境界を作る)。</summary>
    private static void AppendJapaneseRun(StringBuilder sb, Random r)
    {
        int runLen = 1 + r.Next(4);
        int cls = r.Next(3);
        for (int i = 0; i < runLen; i++)
        {
            sb.Append(
                cls switch
                {
                    0 => (char)(0x4E00 + r.Next(2048)), // 漢字 = Han
                    1 => (char)(0x3042 + r.Next(40)), // ひらがな = Hiragana
                    _ => (char)(0x30A2 + r.Next(40)), // カタカナ = Katakana
                }
            );
        }
    }
}
```

**Step 2: `Program.cs` へ配線する**

`tests/yEdit.Editor.Smoke/Program.cs` の `--largeline` ブロック(21-24 行目)の直後に挿入する。

```csharp
// 2026-08-03 UIA 単語単位調査 Task 1: --wordunit。UIA が SR へ返す単語スパンは
// 「空白のみ区切り」の素朴実装で決まるため、Ctrl+←→ / ダブルクリック選択(文字クラス規則)と
// ずれる。その規則差と、空白ゼロ長大行での全走査コストを採取する。GDI は通らない。
if (args.Length > 0 && args[0] == "--wordunit")
{
    return WordUnitBench.Run();
}
```

**Step 3: ビルドして警告 0 を確認する**

Run:
```
dotnet build tests/yEdit.Editor.Smoke/yEdit.Editor.Smoke.csproj -c Release -warnaserror
```
Expected: `ビルドに成功しました` / 警告 0・エラー 0。

失敗しやすい点:
- 未使用 using が警告になる場合は削る(`yEdit.Editor` は namespace が `yEdit.Editor.Smoke` なので using 不要)。
- CSharpier の整形差分。`dotnet csharpier check tests/yEdit.Editor.Smoke` で確認し、
  `dotnet csharpier format tests/yEdit.Editor.Smoke` で直す。

**Step 4: 実行して出力を目視確認する**

Run:
```
dotnet run --project tests/yEdit.Editor.Smoke -c Release -- --wordunit
```

確認すること(**ここが本タスクの検証**):
1. `en`(`foo bar baz`)の行が**すべて `=`** になっている。空白区切りなら 2 実装は一致するはずで、
   ここがずれていたら仮説かベンチのどちらかが間違っている。
2. `ja` / `jakana` の行が **`≠`** で、SR 読みスパンが行全体になっている。
3. `emoji` で例外が出ない(サロゲート中間位置を踏んでも `PrevCodePoint` が吸収する)。
4. コスト表で `合計/Prev` が概ね **2x** 前後になる(`WordStart` + `WordEnd` の片道 2 回分)。
5. 2M で異常に長い場合はスキップ行が出る。

出力は次のタスクと設計書で使うので、ファイルへ保存しておく。**UTF-8 を明示すること**
(Windows PowerShell 5.1 の既定 UTF-16 LE は検索ツールが読めない)。

Run (PowerShell):
```
dotnet run --project tests/yEdit.Editor.Smoke -c Release -- --wordunit | Out-File -Encoding utf8 (Join-Path $env:TEMP 'wordunit-task1.md')
```

**Step 5: commit**

```
git add tests/yEdit.Editor.Smoke/WordUnitBench.cs tests/yEdit.Editor.Smoke/Program.cs
git commit -F <message-file>
```

メッセージ:
```
test(bench): --wordunit で UIA 単語スパンのずれと走査コストを採取する

UIA の単語スパン(空白のみ区切り)と、Ctrl+←→ / ダブルクリック単語選択
(文字クラス規則)の差を並べて表にする。現状の採取は (IUiaTextHost)EditorControl
経由で本番実装を直接叩き、素朴実装をベンチへ写経しない。

コスト側は空白ゼロ長大行で WordStart + WordEnd を測る。kind に jamix を
追加した。ascii / cjk は単一クラスの連続なので、後続タスクで文字クラス規則を
当てても走査が縮まない=候補の効果を測れないため。
```

**Step 6: 仕様レビュー**

CLAUDE.md §3 工程 4 に従い、**別エージェント**で仕様レビューを行う。観点:
- 設計書 §4.1 / §4.2 の指定どおりか(fixture 7 種・kind 3 種・測定項目 4 つ)
- 現状の採取が本番実装を叩いているか(写経していないか)
- `en` 対照群が機能しているか

指摘は fixup commit で反映してから Task 2 へ進む。

---

## Task 2: 候補評価列の追加

**Files:**
- Modify: `tests/yEdit.Editor.Smoke/WordUnitBench.cs`

設計書 §4.3。候補 A(文字クラス規則へ揃える)と候補 B(走査上限キャップ)を
ベンチ内で構成し、before / after を同じ表に並べる。

**候補 A はベンチ内に既にある** — `DoubleClickWordStart` / `DoubleClickWordEnd` が
そのまま候補 A である(「ダブルクリック選択が使っている規則へ揃える」が候補 A の定義)。
Task 1 の §4.1 表は既に before / after を並べていることになるので、Task 2 で足すのは
**コスト側の候補列**と**候補 B**である。

**Step 1: 候補 B(キャップ付き素朴実装)を追加する**

`WordUnitBench.cs` に追加する。`using yEdit.Core.Text;` を追加すること。

```csharp
    // ===== §4.3 候補 B: 走査上限キャップ =====
    // 素朴実装(UiaTextHostAdapter.cs:529-556)に歩数上限を足しただけの候補。
    // これは現状実装の「写経 + 改変」なので、cap を無制限にしたとき本物と一致することを
    // SelfCheck で毎回確認する(写経のズレを検出する唯一の網)。

    private static int CappedWordStart(TextSnapshot snap, int pos, int cap)
    {
        if (pos <= 0)
            return 0;
        int p = pos;
        for (int steps = 0; p > 0 && steps < cap; steps++)
        {
            int prev = TextBoundary.PrevCodePoint(snap, p);
            char pc = snap.GetChar(prev);
            if (char.IsWhiteSpace(pc) || pc == '\r' || pc == '\n')
                break;
            p = prev;
        }
        return p;
    }

    private static int CappedWordEnd(TextSnapshot snap, int pos, int cap)
    {
        int p = pos;
        for (int steps = 0; p < snap.CharLength && steps < cap; steps++)
        {
            char c = snap.GetChar(p);
            if (char.IsWhiteSpace(c) || c == '\r' || c == '\n')
                break;
            p = TextBoundary.NextCodePoint(snap, p);
        }
        return p;
    }
```

**Step 2: 自己検査を追加する**

`Run()` の先頭で呼ぶ。cap 無制限の候補 B が本物と一致しなければ、以降の数値は信用できない。

```csharp
    /// <summary>
    /// cap 無制限の <see cref="CappedWordStart"/> / <see cref="CappedWordEnd"/> が
    /// 本番実装と完全一致することを確認する。候補 B は現状実装の写経なので、
    /// ここがズレていると「キャップの効果」ではなく「写経のバグ」を測ってしまう。
    /// </summary>
    private static bool SelfCheck()
    {
        bool ok = true;
        foreach (var (name, text) in Fixtures)
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString(text);
            ctrl.SetSource(buf);
            var snap = buf.Current;
            var host = (IUiaTextHost)ctrl;

            for (int pos = 0; pos <= snap.CharLength; pos++)
            {
                int realStart = host.WordStart(pos);
                int capStart = CappedWordStart(snap, pos, int.MaxValue);
                int realEnd = host.WordEnd(pos);
                int capEnd = CappedWordEnd(snap, pos, int.MaxValue);
                if (realStart != capStart || realEnd != capEnd)
                {
                    Console.WriteLine(
                        $"SelfCheck NG: {name} pos={pos} start {realStart}/{capStart} end {realEnd}/{capEnd}"
                    );
                    ok = false;
                }
            }
        }
        Console.WriteLine($"SelfCheck: {(ok ? "OK(候補 B の写経は本物と一致)" : "**NG**")}");
        return ok;
    }
```

`Run()` を次に変える。

```csharp
    public static int Run()
    {
        SelfCheck();
        PrintDivergenceTable();
        PrintCostTable();

        Console.WriteLine();
        Console.WriteLine("(調査用ベンチのため判定ゲートなし) EXIT 0");
        return 0;
    }
```

**判定ゲートは持たない方針なので `SelfCheck` が NG でも EXIT 0 のままにする**
(NG の行が出力に残ることが警告になる)。返り値を捨てていることが警告になる場合は
`_ = SelfCheck();` とする。

**Step 3: コスト表に候補列を足す**

`PrintCostTable` のヘッダと行を差し替える。候補 A は
`DoubleClickWordStart` / `DoubleClickWordEnd`、候補 B は cap = 1,000 で測る。

ヘッダ:
```csharp
        Console.WriteLine(
            "| kind | chars | 現状 合計 ms | 候補A(クラス規則) ms | 候補B(cap=1000) ms | 現状/候補A | 現状/候補B |"
        );
        Console.WriteLine("|---|---|---|---|---|---|---|");
```

行(ウォームアップと計測を候補ぶんも回す):
```csharp
                const int Cap = 1000;

                sink += host.WordStart(snap.CharLength); // ウォームアップ(計測外)
                sink += host.WordEnd(0);
                sink += DoubleClickWordStart(snap, snap.CharLength);
                sink += CappedWordStart(snap, snap.CharLength, Cap);

                double curMs =
                    BestOf3(() => sink += host.WordStart(snap.CharLength))
                    + BestOf3(() => sink += host.WordEnd(0));
                double aMs =
                    BestOf3(() => sink += DoubleClickWordStart(snap, snap.CharLength))
                    + BestOf3(() => sink += DoubleClickWordEnd(snap, snap.CharLength));
                double bMs =
                    BestOf3(() => sink += CappedWordStart(snap, snap.CharLength, Cap))
                    + BestOf3(() => sink += CappedWordEnd(snap, 0, Cap));

                Console.WriteLine(
                    $"| {kind} | {len:N0} | {curMs:F1} | {aMs:F1} | {bMs:F1} | {curMs / aMs:F1}x | {curMs / bMs:F1}x |"
                );
```

§4.2 の「現状の内訳」表(`WordStart` / `WordEnd` / `PrevWordStart`)は残す。
**内訳表と候補比較表の 2 つを出す**。前者が N-3 の答え、後者が修正方針の材料である。

**Step 4: ビルドして警告 0 を確認する**

Run:
```
dotnet build tests/yEdit.Editor.Smoke/yEdit.Editor.Smoke.csproj -c Release -warnaserror
```
Expected: 警告 0・エラー 0。

**Step 5: 実行して出力を確認する**

Run:
```
dotnet run --project tests/yEdit.Editor.Smoke -c Release -- --wordunit | Out-File -Encoding utf8 (Join-Path $env:TEMP 'wordunit-task2.md')
```

確認すること(**本タスクの検証**):
1. **`SelfCheck: OK`** が出ている。NG なら候補 B の写経がズレている=以降の数値は無効。
2. `jamix` で **現状/候補A が大きい**(クラス境界で止まるので候補 A が速い)。
3. `ascii` / `cjk` で **現状/候補A が 1x 前後**(単一クラスなので候補 A は効かない)。
   ここが「候補 A だけでは足りない」ことの根拠になる。
4. `ascii` / `cjk` で **現状/候補B が大きい**(キャップが効く)。
5. §4.1 の表が Task 1 から変わっていない(候補 A は既に載っているため)。

**Step 6: commit**

メッセージ:
```
test(bench): --wordunit に修正候補の before/after 列を足す

候補 A(ダブルクリック選択が使っている文字クラス規則へ揃える)と
候補 B(走査上限キャップ)を並べる。候補 B は現状実装の写経のため、
cap 無制限で本物と一致することを SelfCheck で毎回確認する。

候補 A は単一クラスの長大連続(ascii / cjk)には効かず、候補 B は
スパンを切り詰める副作用がある。両者は排他ではない。
```

**Step 7: 仕様レビュー**

**別エージェント**で仕様レビュー。観点:
- 設計書 §4.3 の指定どおりか(候補 A / B の両方・before/after が同じ表)
- `SelfCheck` が候補 B の写経ズレを実際に検出できる形か
- 候補 A が「ダブルクリック規則と同一構成」であることが読み取れるか

---

## Task 3: 実機セッション(ユーザー)

**Files:** なし(採取のみ)

設計書 §5。**ユーザーが実施する。** Claude は手順書を用意し、結果を受け取る。

**Step 1: 手順書を用意する**

Task 1〜2 の出力(`§4.1 ずれの採取` の表)を印刷可能な形でユーザーへ渡す。
実機では次を確認する。

1. **NVDA が UIA Word 単位を使っているか(最重要)**
   - `ja` fixture(`今日は晴れです。`)を yEdit で開く
   - Ctrl+→ を押して NVDA スピーチビューアーの発話を採取する
   - **行全体を読む** → UIA Word 単位を使っている(F-3 の実害あり)
   - **`今日` などクラス単位で切れる** → NVDA がクライアント側で分割している(設計書 §6 の後者へ)
2. **ダブルクリック選択との食い違い**
   - `今日` の上でダブルクリック → 選択範囲を目視
   - 同じ位置で NVDA に単語を読ませる → スパンを比較
   - **SR 無しでも見える食い違い**なので、晴眼ユーザーの視点でも記録する
3. **L5 未検証 3 項目の消化**(相乗り)
   - レビューカーソル / 上書きモード / 音・点字

**採取手法**: NVDA スピーチビューアーを UIA で読む(PR #33 §9.8)。
プロセス名 `nvda.exe` で特定して `RICHEDIT50W` を探す。**日本語ウィンドウ名でマッチさせない**
(Windows PowerShell 5.1 の BOM なし UTF-8 誤デコードで壊れる)。

**注意**: yEdit は起動引数を受け付けない。Ctrl+O でパスを打つ。

**Step 2: 結果を受け取り、設計書 §6 のどちらの分岐かを確定する**

---

## Task 4: 成果物設計書の執筆

**Files:**
- Create: `docs/plans/2026-08-03-uia-word-unit-design.md`

Task 1〜3 の結果を「調査結果 + 修正方針」としてまとめる。次テーマの一次資料になる。

**Step 1: 構成に沿って書く**

- §1 背景(N-3 / N-4 / N-5 の最終状態を 1 か所へ。**N-5 の追跡をここで畳む**)
- §2 調査結果
  - §2.1 規則のずれ(Task 1 の §4.1 表を貼る)
  - §2.2 コスト(Task 1〜2 の表を貼る)
  - §2.3 実機での NVDA の挙動(Task 3)
- §3 修正方針(設計書 §6 の分岐に従って結論を書く)
  - 候補 A / B の採否と根拠
  - Core への `WordStart` / `WordEnd` 抽出(3 箇所が 1 本を共有する形)
  - 挙動変更の範囲と L5 の要否
- §4 申し送り

**Step 2: 事実確認を機械的に行う**

- 引用する commit hash は `git cat-file -t <hash>` で解決確認してから貼る
  (2026-07-22 の GitHub PR フロー移行より前の hash は現行履歴で解決できない)
- 数値は Task 1〜2 の保存済み出力から転記する。記憶から書かない
- **測定値はマシン負荷で 2 倍振れる**(PR #34 の教訓)。絶対値は参考値と明記し、
  主張は「性質」(何倍か・何に比例するか)に置く

**Step 3: 発見した副次事実も記録する**

調査中に見つけた次の 2 点は成果物に含める。

- `src/yEdit.Accessibility/IUiaTextHost.cs:57-61` の doc コメントが `WordStart` / `WordEnd` を
  **「Core WordBoundary 委譲」と書いているが事実ではない**(素朴実装)。
  `NextWordStart` / `PrevWordStart` は実際に委譲しているため、4 つのうち 2 つだけが嘘になっている
- 単語スパンの実装が 2 つある件は棚卸し **F-4**(「文字」の数え方が 3 通り)の姉妹問題

**Step 4: commit**

```
docs(plans): UIA 単語単位の調査結果と修正方針
```

**Step 5: 仕様レビュー**

**別エージェント**で、設計書の技術的主張が採取データから導けるかを検証する。

---

## Task 5: 最終ブランチレビュー → 品質ゲート → PR

**Step 1: 最終ブランチレビュー(統合 1 回)**

CLAUDE.md §3 の簡略化基準により、コード品質パスと脆弱性パスを**1 回に統合**する
(`src/` 変更ゼロ・新規 1 ファイル + docs のため)。**別エージェント**で実施する。

レビュー観点:
- ベンチが本番実装を叩いているか(現状の採取を写経していないか)
- `SelfCheck` が候補 B の写経ズレを検出できるか
- 成果物設計書の主張が採取データから導けるか
- 引用している file:line が現行コードと一致するか

ミューテーション検証はスポットチェックで行う: `SelfCheck` の比較を
`realStart != capStart` → `false` に変異させ、**SelfCheck が NG を報告しなくなる**ことを
確認してから復元する(この網が本当に効いているかの確認)。

指摘は 3 択で明示する: ① fixup commit で修正 / ② PR description に記載して受容 / ③ 理由付き却下。
**元 commit は書き換えず別 fixup commit で積む。**

**Step 2: 品質ゲート**

Run (PowerShell):
```
powershell -ExecutionPolicy Bypass -File tools/pre-merge-check.ps1
```
Expected: **EXIT 0**。

**注意**: この環境に `pwsh` は無い。`powershell` で実行する。

ドキュメントのみの差分ではない(ベンチを足している)ので**省略しない**。

**Step 3: push → PR 作成**

```
git push -u origin feature/uia-word-unit-triage
gh pr create --title "調査: UIA 単語単位の境界ずれとコスト(N-3 / N-4)" --body-file <path>
```

PR description(日本語)に含めるもの:
- 目的(N-3 / N-4 の回収・N-5 の追跡を畳む)
- 発見(単語スパンの 2 実装・`IUiaTextHost` の doc が事実と違う)
- 採取結果の要点
- 実機セッションの結果
- 修正方針の結論
- レビュー経緯・申し送り
- 末尾に:
```
🤖 Generated with [Claude Code](https://claude.com/claude-code)
```

**Step 4: 申し送りの回収**

PR description または成果物設計書に次を明記する。

- **E-1**(折り返し ON で ↓ を押すと NVDA が「ブランク」)と **E-2**(「すべて破棄」が
  孤児バックアップを消さない)は PR #35 の L5 で発見された既存問題で、**まだ起票されていない**
- **N-5 は PR #35 の L5 で決着済み**。`2026-08-02-large-line-resilience-design.md` §4 の
  「最優先の残課題」という記述は策定時スナップショットであり現状ではない

---

## リスクと対処

| ID | リスク | 対処 |
|---|---|---|
| R1 | NVDA が UIA Word 単位を使っていない | 設計書 §6 で分岐を事前確定済み。どちらでも結論が書ける |
| R2 | 調査が実装に流れる | **`src/` 変更ゼロ**が本ブランチの不変条件。修正候補が有望でも実装しない |
| R3 | 候補 B の写経が本物とズレる | `SelfCheck`(Task 2 Step 2)で毎回検出する |
| R4 | Editor.Smoke の Main が STA でない | 既存 `--largeline` が Form を `Show()` して動いているので STA である。万一 `EditorControl` の生成で例外が出たら、`Run()` の中身を `[STAThread]` を持つ専用スレッドで実行する |
| R5 | 2M の測定が長すぎる | `SkipThresholdSec` で打ち切る(`--largeline` と同じ) |
| R6 | 測定値がマシン負荷で振れる | 絶対値ではなく倍率で主張する。設計書に明記する |

---

## 完了条件(DoD)

- [ ] `dotnet run --project tests/yEdit.Editor.Smoke -c Release -- --wordunit` が EXIT 0 で表を出す
- [ ] `SelfCheck: OK` が出ている
- [ ] §4.1 表で `en` が全て `=`・`ja` / `jakana` が `≠`
- [ ] コスト表に「現状 / 候補 A / 候補 B」の 3 列が並ぶ
- [ ] 実機セッションで NVDA の発話が採取され、設計書 §6 のどちらの分岐かが確定している
- [ ] `docs/plans/2026-08-03-uia-word-unit-design.md` に修正方針の結論が書かれている
- [ ] 別エージェントによる仕様レビュー(Task 1・2・4)と最終レビュー(Task 5)が完了している
- [ ] `tools/pre-merge-check.ps1` が **EXIT 0**
- [ ] `src/` の差分が **0 行**(`git diff main --stat -- src/` が空)
