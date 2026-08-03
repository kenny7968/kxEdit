using System.Diagnostics;
using System.Reflection;
using System.Text;
using yEdit.Accessibility;
using yEdit.Core.Buffers;
using yEdit.Core.Editing;
using yEdit.Core.Text;

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
/// <item>修正候補 A / B を当てたときのコスト(§4.3)</item>
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
        // 判定ゲートを持たない方針(常に EXIT 0)なので返り値は捨てる。
        // NG 行が出力に残ることが警告になる。
        _ = SelfCheck();
        PrintDivergenceTable();
        PrintCostTable();
        PrintCandidateCostTable();

        Console.WriteLine();
        Console.WriteLine("(調査用ベンチのため判定ゲートなし) EXIT 0");
        return 0;
    }

    // ===== SelfCheck: 候補の写経が本物と一致するか =====
    // 候補 A も候補 B も src の private 実装の写経である(候補 A = InputRouter の
    // private static ヘルパ・候補 B = UiaTextHostAdapter の private static 素朴実装)。
    // 写経がズレていると「候補の効果」ではなく「写経のバグ」を測ることになるため、
    // 毎回本物と突き合わせる。<b>反射でメンバーが見つからないときに黙って PASS しないこと。</b>
    // 「照合したつもりで何も照合していない」は本リポジトリで過去に踏んだ失敗パターンである。

    /// <summary>候補 A / 候補 B の写経を本物と照合する。片方でも NG なら false。</summary>
    private static bool SelfCheck()
    {
        Console.WriteLine("## SelfCheck(候補の写経 vs 本物)");
        Console.WriteLine();
        bool a = SelfCheckCandidateA();
        bool b = SelfCheckCandidateB();
        Console.WriteLine();
        return a && b;
    }

    /// <summary>
    /// 候補 A(<see cref="DoubleClickWordStart"/> / <see cref="DoubleClickWordEnd"/>)が
    /// <c>InputRouter.PrevWordBoundary</c> / <c>NextWordBoundary</c> と完全一致することを確認する。
    /// </summary>
    /// <remarks>
    /// <c>InputRouter</c> は <c>internal sealed</c>・両ヘルパは <c>private static</c> なので反射で叩く
    /// (<c>yEdit.Editor.Smoke</c> は InternalsVisibleTo 対象 = yEdit.Editor.csproj:19)。
    /// 型名 / メソッド名が変わったら **NG として報告する**(見つからないことを PASS にしない)。
    /// </remarks>
    private static bool SelfCheckCandidateA()
    {
        var type = typeof(EditorControl).Assembly.GetType("yEdit.Editor.InputRouter");
#pragma warning disable S3011 // reason: 照合対象が private static ヘルパのため反射以外に本物を叩く手段がない。調査ベンチ限定・読み取りのみ(App 層のテスト群も同じ手法を使っている)
        var prev = type?.GetMethod(
            "PrevWordBoundary",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        var next = type?.GetMethod(
            "NextWordBoundary",
            BindingFlags.NonPublic | BindingFlags.Static
        );
#pragma warning restore S3011

        if (type is null || prev is null || next is null)
        {
            Console.WriteLine(
                "SelfCheck 候補A: **NG(照合できていない)** — 反射で本物が見つからない: "
                    + $"type={(type is null ? "**null**" : type.FullName)} / "
                    + $"PrevWordBoundary={(prev is null ? "**null**" : "found")} / "
                    + $"NextWordBoundary={(next is null ? "**null**" : "found")}。"
                    + "InputRouter の型名かヘルパ名が変わった可能性がある。"
                    + "**候補 A の数値は本物と一致する保証がない = 無効。**"
            );
            return false;
        }

        bool ok = true;
        foreach (var (name, text) in Fixtures)
        {
            var snap = TextBuffer.FromString(text).Current;
            for (int pos = 0; pos <= snap.CharLength; pos++)
            {
                int realStart = InvokeBoundary(prev, snap, pos);
                int realEnd = InvokeBoundary(next, snap, pos);
                int mineStart = DoubleClickWordStart(snap, pos);
                int mineEnd = DoubleClickWordEnd(snap, pos);
                if (realStart != mineStart || realEnd != mineEnd)
                {
                    Console.WriteLine(
                        $"SelfCheck 候補A **NG**: {name} pos={pos} "
                            + $"start {realStart}/{mineStart} end {realEnd}/{mineEnd}"
                    );
                    ok = false;
                }
            }
        }

        Console.WriteLine(
            ok
                ? "SelfCheck 候補A: OK(InputRouter.PrevWordBoundary / NextWordBoundary と一致)"
                : "SelfCheck 候補A: **NG**"
        );
        return ok;
    }

    /// <summary>反射で得た <c>(TextSnapshot, int) -&gt; int</c> を呼ぶ。</summary>
    private static int InvokeBoundary(MethodInfo m, TextSnapshot snap, int pos) =>
        m.Invoke(null, [snap, pos]) as int?
        ?? throw new InvalidOperationException($"{m.Name} が int を返さなかった");

    /// <summary>
    /// cap 無制限の <see cref="CappedWordStart"/> / <see cref="CappedWordEnd"/> が
    /// 本番実装(<c>UiaTextHostAdapter.WordBoundary_WordStart / _WordEnd</c>)と
    /// 完全一致することを確認する。
    /// </summary>
    private static bool SelfCheckCandidateB()
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
                        $"SelfCheck 候補B **NG**: {name} pos={pos} "
                            + $"start {realStart}/{capStart} end {realEnd}/{capEnd}"
                    );
                    ok = false;
                }
            }
        }

        Console.WriteLine(
            ok
                ? "SelfCheck 候補B: OK(cap 無制限で UiaTextHostAdapter の素朴実装と一致)"
                : "SelfCheck 候補B: **NG**"
        );
        return ok;
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
        // 全角空白。ClassOf は U+3000 を Other 扱いにしている。
        // リテラルへ生の U+3000 を置くと S2479(制御文字はエスケープで書く)になる。
        ("wsp", "今日\u3000は"),
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
            Console.WriteLine(
                "| pos | Ctrl+→ 移動先 | SR 読みスパン | ダブルクリック選択 | 一致 |"
            );
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

    // ===== §4.3 候補 B: 走査上限キャップ =====
    // 素朴実装(UiaTextHostAdapter.cs:529-556)に歩数上限を足しただけの候補。
    // 「写経 + 改変」なので、cap 無制限で本物と一致することを SelfCheckCandidateB で毎回確認する。

    /// <summary>キャップ付き <c>WordStart</c>。cap 歩を超えたらそこで打ち切る。</summary>
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

    /// <summary>キャップ付き <c>WordEnd</c>。cap 歩を超えたらそこで打ち切る。</summary>
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

    // ===== §4.2 コスト実測 =====

    /// <summary>
    /// 1 条件の<b>実経過</b>がこの秒数を超えたら、その kind の以降の行長をスキップする
    /// (--largeline と同じ流儀)。
    /// </summary>
    /// <remarks>
    /// --largeline の 30 秒より大きいのは、1 条件の実経過が「ウォームアップ 1 回 + 計測 3 回 ×
    /// (start + end + prev)」= 表に出る 合計 ms の約 6 倍あるため。30 秒のままだと最長条件
    /// (ascii 2M = 実測約 68 秒)で必ず発動し、しかもそれは各 kind の最後の行長なので
    /// 「以降をスキップ」が空振りのまま出力に残る。全条件を採り切ることを優先する。
    /// </remarks>
    private const double SkipThresholdSec = 90.0;

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

                // 打ち切り判定は「1 条件の実経過」で行う(LargeLineBench.cs:100 の totalSec と同基準)。
                // 表に出る 合計 ms は best-of-3 の最小値どうしの和なので、実経過はその約 6 倍
                // (ウォームアップ 1 回 + 計測 3 回 ×(start + end + prev))ある。
                // 合計 ms を閾値と比べると実経過の 1/6 で判定してしまい、ガードが効かない。
                var swCond = Stopwatch.StartNew();

                sink += host.WordStart(snap.CharLength); // ウォームアップ(JIT・計測外)
                sink += host.WordEnd(0);
                sink += WordBoundary.PrevWordStart(snap, snap.CharLength);

                double startMs = BestOf3(() => sink += host.WordStart(snap.CharLength));
                double endMs = BestOf3(() => sink += host.WordEnd(0));
                double prevMs = BestOf3(() =>
                    sink += WordBoundary.PrevWordStart(snap, snap.CharLength)
                );

                swCond.Stop(); // Console 出力は計測外(LargeLineBench も測定部だけを見ている)
                double condSec = swCond.Elapsed.TotalSeconds;
                double totalMs = startMs + endMs;

                // PrevWordStart は jamix ではクラス境界で数文字止まりになり ms が 0 付近へ落ちる。
                // F1 だと "0.0" と表示されたうえで比だけ 6 桁になり、表の中で矛盾して見える。
                // 桁を増やして「割った相手が極小だった」ことを表から読めるようにする。
                Console.WriteLine(
                    $"| {kind} | {len:N0} | {startMs:F1} | {endMs:F1} | **{totalMs:F1}** | {prevMs:F3} | {totalMs / prevMs:F2}x |"
                );

                if (condSec > SkipThresholdSec)
                {
                    Console.WriteLine(
                        $"（実経過 {condSec:F1}s > {SkipThresholdSec}s のため {kind} の以降の行長をスキップ）"
                    );
                    break;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            "注: `PrevWordStart`(移動側)は文字クラス規則なので jamix では最初のクラス境界で"
                + "数文字止まりになる。ゆえに jamix の `合計/Prev` は比として意味を持たない"
                + "(片道走査どうしの比になっていない)。ascii / cjk は単一クラスの連続で"
                + "両者が同じ距離を歩くため、そこでの約 2x が `ExpandToEnclosingUnit(Word)` の"
                + "片道 2 回分を表す。"
        );
        Console.WriteLine();
        Console.WriteLine($"(sink={sink})");
    }

    // ===== §4.3 候補比較 =====

    /// <summary>候補 B の走査上限。1,000 文字で単語を打ち切る。</summary>
    private const int Cap = 1000;

    /// <summary>
    /// 現状 / 候補 A / 候補 B の <c>ExpandToEnclosingUnit(Word)</c> 相当コストを同じ表に並べる。
    /// </summary>
    /// <remarks>
    /// <b>3 者とも同じ形で測る</b>: <c>start = WordStart(pos)</c> → <c>end = WordEnd(start)</c>
    /// (<c>TextRangeProviderV2.ExpandToEnclosingUnit</c> の呼び順)。
    /// <c>WordEnd</c> に <c>pos</c> をそのまま渡すと、<c>pos = CharLength</c> のとき
    /// 候補 A / B とも先頭ガードで即 return し<b>何も測らない</b>ので注意
    /// (実装計画 Task 2 Step 3 のコード片はこの誤りを含んでいた)。
    /// </remarks>
    private static void PrintCandidateCostTable()
    {
        Console.WriteLine();
        Console.WriteLine($"## §4.3 候補比較(cap={Cap:N0}・空白ゼロの単一長大行)");
        Console.WriteLine();
        Console.WriteLine(
            "測定は 3 者とも `start = WordStart(行末)` → `end = WordEnd(start)` の形"
                + "(`WordEnd` に行末をそのまま渡すと候補 A / B は先頭ガードで即 return し何も測らない)。"
        );
        Console.WriteLine();
        Console.WriteLine(
            $"| kind | chars | 現状 合計 ms | 候補A(クラス規則) ms | 候補B(cap={Cap}) ms | 現状/候補A | 現状/候補B |"
        );
        Console.WriteLine("|---|---|---|---|---|---|---|");

        long sink = 0;
        var spanRows = new List<string>();

        foreach (string kind in new[] { "ascii", "cjk", "jamix" })
        {
            foreach (int len in new[] { 100_000, 500_000, 2_000_000 })
            {
                using var ctrl = new EditorControl();
                var buf = TextBuffer.FromString(MakeSingleLine(len, kind));
                ctrl.SetSource(buf);
                var snap = buf.Current;
                var host = (IUiaTextHost)ctrl;
                int pos = snap.CharLength;

                var swCond = Stopwatch.StartNew();

                // ウォームアップ(JIT・計測外)。ここで得た start を end の起点に使う。
                int curStart = host.WordStart(pos);
                int aStart = DoubleClickWordStart(snap, pos);
                int bStart = CappedWordStart(snap, pos, Cap);
                int curEnd = host.WordEnd(curStart);
                int aEnd = DoubleClickWordEnd(snap, aStart);
                int bEnd = CappedWordEnd(snap, bStart, Cap);
                sink += curStart + aStart + bStart + curEnd + aEnd + bEnd;

                double curMs =
                    BestOf3(() => sink += host.WordStart(pos))
                    + BestOf3(() => sink += host.WordEnd(curStart));
                double aMs =
                    BestOf3(() => sink += DoubleClickWordStart(snap, pos))
                    + BestOf3(() => sink += DoubleClickWordEnd(snap, aStart));
                double bMs =
                    BestOf3(() => sink += CappedWordStart(snap, pos, Cap))
                    + BestOf3(() => sink += CappedWordEnd(snap, bStart, Cap));

                swCond.Stop(); // Console 出力は計測外
                double condSec = swCond.Elapsed.TotalSeconds;

                // 候補 A / B の ms は F3。jamix の候補 A は 0.00x ms へ落ちるため F1 だと
                // 「0.0」と表示されたうえで倍率だけ 6 桁になり、表の中で矛盾して見える
                // (§4.2 の PrevWordStart 列と同じ理由)。
                Console.WriteLine(
                    $"| {kind} | {len:N0} | {curMs:F1} | {aMs:F3} | {bMs:F3} | "
                        + $"{curMs / aMs:F1}x | {curMs / bMs:F0}x |"
                );

                // スパンの実態(コストではなく「SR が何を 1 単語として読むことになるか」)。
                // 行末からの展開に加え、行の中央からの展開も採る = 候補 B の非対称性を見るため。
                int mid = snap.CharLength / 2;
                int bMidStart = CappedWordStart(snap, mid, Cap);
                int bMidEnd = CappedWordEnd(snap, bMidStart, Cap);
                spanRows.Add(
                    $"| {kind} | {len:N0} | {curEnd - curStart:N0} | {aEnd - aStart:N0} | "
                        + $"{bEnd - bStart:N0} | [{bMidStart:N0},{bMidEnd:N0}) = {bMidEnd - bMidStart:N0} | "
                        + $"{(len + Cap - 1) / Cap:N0} |"
                );

                if (condSec > SkipThresholdSec)
                {
                    Console.WriteLine(
                        $"（実経過 {condSec:F1}s > {SkipThresholdSec}s のため {kind} の以降の行長をスキップ）"
                    );
                    break;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            "注: `現状/候補B` は候補 B が cap で定数時間になるため倍率が桁違いに大きくなる。"
                + "**候補 A は ascii / cjk(単一クラスの長大連続)では 1x 前後にしかならない** — "
                + "これが「候補 A だけでは足りない」ことの根拠である。逆に jamix では"
                + "クラス境界で数文字止まりになるため候補 A が効く。"
        );

        Console.WriteLine();
        Console.WriteLine("### 候補 B がスパンをどう切り詰めるか");
        Console.WriteLine();
        Console.WriteLine(
            "| kind | chars | 現状 span | 候補A span | 候補B span(行末から) | 候補B span(中央 pos から) | 候補B の分割数 |"
        );
        Console.WriteLine("|---|---|---|---|---|---|---|");
        foreach (string row in spanRows)
            Console.WriteLine(row);
        Console.WriteLine();
        Console.WriteLine(
            $"注: 分割数 = ceil(chars / {Cap:N0}) = 行頭から順に読ませたときに SR が受け取る「単語」の個数。"
                + "中央 pos の列は候補 B の副作用を示す = `WordStart(pos)` が `pos - cap` を返し "
                + "`WordEnd` がそこから cap 歩進むため、スパンが **pos の手前で終わり caret 位置の文字を含まない**。"
                + "現状実装は行全体を返すため必ず含んでいた。Task 4 の判断材料。"
        );
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
    ///   同上(生成規則・シードとも <c>LargeLineBench.MakeSingleLine</c> と同一)。</description>
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
        // LargeLineBench.MakeSingleLine(20260802)/ Core.Bench の MakeSingleLine(20260802)と同一シード。
        // ascii / cjk の fixture が literally 同一になり、既存実測との前後比較が成立する。
        // Core.Bench/Program.cs:233-234 の「2 つのベンチが対であることが設計の前提なので、
        // 片方だけ変えないこと」に従う。jamix は本調査で追加した新 kind なので取り決めの対象外。
        var r = new Random(20260802);
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
