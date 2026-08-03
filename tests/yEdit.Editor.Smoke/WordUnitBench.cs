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

                // PrevWordStart は jamix ではクラス境界で数文字止まりになり ms が 0 付近へ落ちる。
                // F1 だと "0.0" と表示されたうえで比だけ 6 桁になり、表の中で矛盾して見える。
                // 桁を増やして「割った相手が極小だった」ことを表から読めるようにする。
                Console.WriteLine(
                    $"| {kind} | {len:N0} | {startMs:F1} | {endMs:F1} | **{totalMs:F1}** | {prevMs:F3} | {totalMs / prevMs:F2}x |"
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
