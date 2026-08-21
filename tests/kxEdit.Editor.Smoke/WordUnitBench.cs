using System.Diagnostics;
using System.Text;
using kxEdit.Accessibility;
using kxEdit.Core.Buffers;
using kxEdit.Core.Editing;
using kxEdit.Core.Text;

namespace kxEdit.Editor.Smoke;

/// <summary>
/// UIA 単語単位(F-3 境界ずれ / F-5 コスト)の実測ベンチ。
/// 2026-08-04 の修正(docs/plans/2026-08-04-uia-word-unit-fix.md)後の版で、採るのは 4 つ。
/// <list type="number">
/// <item>SR の読み上げスパンとダブルクリック単語選択が<b>一致すること</b>の確認(= F-3 が消えたことの可視化)</item>
/// <item>空白ゼロ長大行での「上限なし」と「本番 cap」のコスト差(= F-5 の効き)</item>
/// <item><c>WordBoundary.DefaultMaxScan</c> を決めるための cap 掃引(速度側の材料)</item>
/// <item>現実のテキストにおけるクラス run 長(単語らしさ側の材料=どこから切り詰めが起きるか)</item>
/// </list>
/// 材料採取のためのベンチなので判定ゲートは持たない(常に EXIT 0)。
/// </summary>
/// <remarks>
/// <b>採取は必ず実物を叩く。</b> 単語規則も走査上限も <c>kxEdit.Core.Editing.WordBoundary</c> の
/// 1 本に集約されたので、ベンチ側へ規則を写経しない(写経すると「同じ壊れ方をする参照」になり、
/// ずれを検出できなくなる)。SR 側は <c>(IUiaTextHost)EditorControl</c> 経由で
/// <c>UiaTextHostAdapter</c> の実装へ届く=Handle 生成も Form も不要で GDI を通らない。
///
/// 2026-08-04 の Task 6 で、調査時代の構成(現状実装 vs 候補 A 写経 vs 候補 B 写経 +
/// 写経が本物と一致するかの SelfCheck)を全廃した。候補 A の照合先
/// (<c>InputRouter.PrevWordBoundary / NextWordBoundary</c>)は Task 2 で、候補 B の照合先
/// (<c>UiaTextHostAdapter</c> の空白のみ規則)は Task 3 で消えたため、
/// どちらの SelfCheck も照合相手を失っていた。
/// </remarks>
internal static class WordUnitBench
{
    public static int Run()
    {
        Console.WriteLine("# --wordunit(UIA 単語単位・2026-08-04 修正後の実測)");
        Console.WriteLine();
        Console.WriteLine(
            $"`WordBoundary.DefaultMaxScan` = **{WordBoundary.DefaultMaxScan}**"
                + "(SR の読み上げ / Ctrl+←→ / ダブルクリック単語選択の 3 経路が渡す cap)"
        );

        PrintAgreementTable();
        PrintCostTable();
        PrintCapSweep();
        PrintRunLengthTables();

        Console.WriteLine();
        Console.WriteLine("(材料採取のためのベンチ=判定ゲートなし) EXIT 0");
        return 0;
    }

    // ===== 1. SR 読みスパン = ダブルクリック選択 =====

    /// <summary>目視可能な短い 1 行 fixture。<c>en</c> は「全部変わる」という誤結論を防ぐ対照群。</summary>
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

    /// <summary>
    /// SR が読むスパンとダブルクリックが選ぶスパンを並べる。
    /// <b>両方とも本物</b>を叩く=前者は <c>(IUiaTextHost)EditorControl</c>、
    /// 後者は <c>InputRouter.HandleMouseDoubleClick</c> と同じ <c>WordBoundary</c> 呼び出し。
    /// </summary>
    private static void PrintAgreementTable()
    {
        Console.WriteLine();
        Console.WriteLine("## 1. SR 読みスパン = ダブルクリック選択(F-3 解消の可視化)");
        Console.WriteLine();
        Console.WriteLine(
            "SR 側は `TextRangeProviderV2.ExpandToEnclosingUnit(TextUnit.Word)` と同じ順で "
                + "`(IUiaTextHost)EditorControl` を叩く(`WordStart(pos)` → `WordEnd(pos)` → "
                + "空スパンなら `NextChar` へ縮退)。ダブルクリック側は `InputRouter` と同じ "
                + "`WordBoundary.WordStart / WordEnd(snap, pos, DefaultMaxScan)`。"
                + "**この表が調査時代に見せていた「ずれ」が消えていること**が確認事項である。"
        );

        int diffRows = 0;
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
                var sr = SrSpan(host, pos);
                var dc = DoubleClickSpan(snap, pos);
                int next = WordBoundary.NextWordStart(snap, pos, WordBoundary.DefaultMaxScan);
                bool same = sr == dc;
                if (!same)
                    diffRows++;
                Console.WriteLine(
                    $"| {pos} | {next} | {Span(snap, sr)} | {Span(snap, dc)} | {(same ? "=" : "**≠**")} |"
                );
                if (next <= pos)
                    break; // EOF で進まなくなったら終了(無限ループ防止)
                pos = next;
            }

            Console.WriteLine();
            Console.WriteLine(AllPosSummary(host, snap));
        }

        Console.WriteLine();
        Console.WriteLine(
            diffRows == 0
                ? "**表の全行で一致した(`≠` は 0 行)= F-3 は解消している。**"
                : $"**`≠` が {diffRows} 行ある = F-3 が残っている疑い。**"
        );
        Console.WriteLine();
        Console.WriteLine(
            "注: 表の pos は `NextWordStart`(Ctrl+→)で刻んだ位置なので、行内の全位置を見ていない。"
                + "各 fixture の直後の行が**全 pos 総当り**の結果である。"
        );
    }

    /// <summary>
    /// SR が実際に受け取るスパン。<c>TextRangeProviderV2.ExpandToEnclosingUnit</c>(:77-80)と同じ順・
    /// 同じ縮退分岐で叩く。
    /// </summary>
    private static (int Start, int End) SrSpan(IUiaTextHost host, int pos)
    {
        int start = host.WordStart(pos);
        int end = host.WordEnd(pos);
        if (end == start)
            end = host.NextChar(start); // 空スパン → 1 文字へ膨らませる分岐
        return (start, end);
    }

    /// <summary>
    /// ダブルクリック単語選択が選ぶスパン。<c>InputRouter.HandleMouseDoubleClick</c>(:529-530)と
    /// 同じ引数・同じ cap で叩く。
    /// </summary>
    private static (int Start, int End) DoubleClickSpan(TextSnapshot snap, int pos) =>
        (
            WordBoundary.WordStart(snap, pos, WordBoundary.DefaultMaxScan),
            WordBoundary.WordEnd(snap, pos, WordBoundary.DefaultMaxScan)
        );

    /// <summary>
    /// 行内の<b>全 pos</b> で UIA 側と Core 側の規則出力を突き合わせる。
    /// </summary>
    /// <remarks>
    /// 比べるのは縮退分岐を通す<b>前</b>の生の <c>WordStart</c> / <c>WordEnd</c> である。
    /// 空スパン(<c>WordStart(pos) == WordEnd(pos)</c>)になる位置では Provider が
    /// <c>NextChar</c> で 1 文字へ膨らませるため、そこだけは SR が受け取る文字列が
    /// ダブルクリック選択(空選択)と一致しない。これは<b>規則のずれではなく Provider の
    /// フォールバック</b>なので、件数だけを別に出す。
    /// </remarks>
    private static string AllPosSummary(IUiaTextHost host, TextSnapshot snap)
    {
        int mismatch = 0;
        int degenerate = 0;
        for (int p = 0; p <= snap.CharLength; p++)
        {
            var uia = (host.WordStart(p), host.WordEnd(p));
            if (uia.Item1 == uia.Item2)
                degenerate++;
            if (uia != DoubleClickSpan(snap, p))
                mismatch++;
        }
        string head =
            mismatch == 0
                ? $"全 pos 照合({snap.CharLength + 1} 位置): **一致**"
                : $"全 pos 照合({snap.CharLength + 1} 位置): **不一致 {mismatch} 箇所**";
        return degenerate == 0
            ? head
            : head
                + $" / うち空スパン位置 {degenerate} 箇所(Provider が `NextChar` で 1 文字へ縮退)";
    }

    private static string Span(TextSnapshot snap, (int Start, int End) s) =>
        $"[{s.Start},{s.End}) `{snap.GetText(s.Start, s.End - s.Start)}`";

    // ===== 2. 上限なし vs 本番 cap =====

    /// <summary>
    /// 1 条件の<b>実経過</b>がこの秒数を超えたら、その kind の以降の行長をスキップする
    /// (--largeline と同じ流儀)。
    /// </summary>
    /// <remarks>
    /// --largeline の 30 秒より大きいのは、1 条件の実経過が「ウォームアップ 1 回 + 計測 3 回 ×
    /// (start + end + prev)」= 表に出る合計 ms の約 6 倍あるため。30 秒のままだと最長条件
    /// (ascii 2M = 実測約 68 秒)で必ず発動し、しかもそれは各 kind の最後の行長なので
    /// 「以降をスキップ」が空振りのまま出力に残る。全条件を採り切ることを優先する。
    /// </remarks>
    private const double SkipThresholdSec = 90.0;

    private static readonly int[] LineLengths = [100_000, 500_000, 2_000_000];

    private static readonly string[] Kinds = ["ascii", "cjk", "jamix"];

    /// <summary>
    /// 空白・改行ゼロの単一長大行で、走査上限なしと本番 cap のコストを並べる。
    /// </summary>
    /// <remarks>
    /// 「上限なし」列は <c>NoScanLimit</c> を明示的に渡した<b>反実仮想</b>である
    /// (本番にこの経路はもう無い)。ascii / cjk は単一クラスの長大連続なので、
    /// 修正前の素朴実装(空白のみ区切り)と走査距離が同じ=設計書 §2.4 の表と直接比較できる。
    /// <b>jamix は比較できない</b>: §2.4 の jamix は空白のみ規則で全走査していたが、
    /// この列は文字クラス規則なので上限なしでも数文字で止まる(= 候補 A の効果がここに出る)。
    /// </remarks>
    private static void PrintCostTable()
    {
        Console.WriteLine();
        Console.WriteLine("## 2. 上限なし vs 本番 cap(空白・改行ゼロの単一長大行)");
        Console.WriteLine();
        Console.WriteLine(
            "expand = `WordStart(行末)` + `WordEnd(先頭)`(= `ExpandToEnclosingUnit(Word)` の片道 2 回分)。"
                + "Ctrl+← = `PrevWordStart(行末)`。本番列は SR 経路そのもの"
                + "(`(IUiaTextHost)EditorControl` = `DefaultMaxScan` を内部で渡す)を叩いている。"
        );
        Console.WriteLine();
        Console.WriteLine(
            $"| kind | chars | 上限なし expand ms | 上限なし Ctrl+← ms | 本番 expand ms(cap={WordBoundary.DefaultMaxScan}) | 本番 Ctrl+← ms | expand 倍率 |"
        );
        Console.WriteLine("|---|---|---|---|---|---|---|");

        long sink = 0;

        foreach (string kind in Kinds)
        {
            foreach (int len in LineLengths)
            {
                using var ctrl = new EditorControl();
                var buf = TextBuffer.FromString(MakeSingleLine(len, kind));
                ctrl.SetSource(buf);
                var snap = buf.Current;
                var host = (IUiaTextHost)ctrl;
                int end = snap.CharLength;

                // 打ち切り判定は「1 条件の実経過」で行う(LargeLineBench.cs:100 の totalSec と同基準)。
                // 表に出る合計 ms は best-of-3 の最小値どうしの和なので、実経過はその数倍ある。
                var swCond = Stopwatch.StartNew();

                // ウォームアップ(JIT・計測外)
                sink += WordBoundary.WordStart(snap, end, WordBoundary.NoScanLimit);
                sink += WordBoundary.WordEnd(snap, 0, WordBoundary.NoScanLimit);
                sink += WordBoundary.PrevWordStart(snap, end, WordBoundary.NoScanLimit);
                sink += host.WordStart(end) + host.WordEnd(0);
                sink += WordBoundary.PrevWordStart(snap, end, WordBoundary.DefaultMaxScan);

                double freeMs =
                    BestOf3(() =>
                        sink += WordBoundary.WordStart(snap, end, WordBoundary.NoScanLimit)
                    )
                    + BestOf3(() =>
                        sink += WordBoundary.WordEnd(snap, 0, WordBoundary.NoScanLimit)
                    );
                double freeNavMs = BestOf3(() =>
                    sink += WordBoundary.PrevWordStart(snap, end, WordBoundary.NoScanLimit)
                );
                double capMs =
                    BestOf3(() => sink += host.WordStart(end))
                    + BestOf3(() => sink += host.WordEnd(0));
                double capNavMs = BestOf3(() =>
                    sink += WordBoundary.PrevWordStart(snap, end, WordBoundary.DefaultMaxScan)
                );

                swCond.Stop(); // Console 出力は計測外(LargeLineBench も測定部だけを見ている)
                double condSec = swCond.Elapsed.TotalSeconds;

                // 本番側は 0.00x ms へ落ちるため F1 だと「0.0」と表示されたうえで倍率だけ
                // 6 桁になり、表の中で矛盾して見える。桁を増やして読めるようにする。
                Console.WriteLine(
                    $"| {kind} | {len:N0} | {freeMs:F1} | {freeNavMs:F1} | {capMs:F4} | {capNavMs:F4} | {freeMs / capMs:N0}x |"
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
            "注: **jamix の「上限なし」列は設計書 §2.4 の jamix と別物である。** §2.4 は"
                + "「空白のみ区切り」の素朴実装で全走査していたが、この列は文字クラス規則なので"
                + "上限なしでもクラス境界(1〜4 文字)で止まる。ascii / cjk は単一クラスの長大連続なので"
                + "両者が同じ距離を歩き、§2.4 と直接比較できる。"
        );
        Console.WriteLine();
        Console.WriteLine($"(sink={sink})");
    }

    // ===== 3. cap 掃引 =====

    private static readonly int[] CapCandidates = [32, 64, 128, 256, 512, 1024, 4096];

    /// <summary>位相サンプルの点数。<c>TextChunk</c> の格子 1 セル(4KB)を跨いで覆う数にする。</summary>
    private const int PhaseSamples = 16;

    /// <summary>
    /// 位相サンプルの刻み。格子幅(ascii なら 4096 文字・3 バイト文字なら 1365 文字)と
    /// 互いに素になるよう素数にして、同じ位相ばかり踏まないようにする。
    /// </summary>
    private const int PhaseStride = 257;

    /// <summary>
    /// cap 掃引。<c>WordBoundary.DefaultMaxScan</c> の値を決めるための<b>速度側</b>の材料。
    /// </summary>
    /// <remarks>
    /// <b>壁時計は cap に単純比例しない。</b> <c>TextSnapshot.GetChar</c> のコストが
    /// <c>TextChunk</c> の格子(<c>DefaultGridBytes</c> 既定 4KB)内の線形走査に比例するため、
    /// 実コストは行長と<b>位相</b>(窓が格子セルのどこに載るか)で数倍振れる
    /// (設計書 §2.5 の候補 B 欠陥 2)。単調でない結果が出てもそれ自体は異常ではない。
    ///
    /// <b>だから 1 位置だけ測ると値が位相の当たり外れになる。</b> 窓がセルの<b>末尾側</b>に
    /// 載ると 1 回の <c>GetChar</c> がセル先頭から最大 4KB 走るため、同じ cap でも 10 倍以上
    /// 高くなる。表には「行中央 1 点」と「<see cref="PhaseSamples"/> 点での最悪値」を並べ、
    /// cap の判断が位相の当たり外れに乗らないようにする。
    /// </remarks>
    private static void PrintCapSweep()
    {
        Console.WriteLine();
        Console.WriteLine("## 3. cap 掃引(空白ゼロ 500K)");
        Console.WriteLine();
        Console.WriteLine(
            "expand = `WordStart(pos, cap)` + `WordEnd(pos, cap)` = **SR の読み上げ 1 回**。"
                + "Ctrl+← = `PrevWordStart(pos, cap)` = **UI スレッドの Ctrl+← 1 回**。"
                + "スパン幅は SR が実際に読む長さで、**cap は code point 数**なので"
                + "非 BMP では char 幅がこれより伸びる。"
        );
        Console.WriteLine();
        Console.WriteLine(
            $"「中央」は pos = 行中央の 1 点。「最悪位相」は pos を {PhaseStride} 文字刻みで "
                + $"{PhaseSamples} 点ずらしたときの最大値"
                + "(`TextChunk` の格子 4KB のどこに窓が載るかで数倍振れるため。**判断はこちらを見る**)。"
        );
        Console.WriteLine();
        Console.WriteLine(
            "| kind | cap | expand ms 中央 | **expand ms 最悪位相** | Ctrl+← ms 中央 | "
                + "**Ctrl+← ms 最悪位相** | スパン幅 char | スパン幅 cp | cap 到達 |"
        );
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|");

        long sink = 0;

        foreach (string kind in Kinds)
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString(MakeSingleLine(500_000, kind));
            ctrl.SetSource(buf);
            var snap = buf.Current;
            int pos = snap.CharLength / 2;

            foreach (int cap in CapCandidates)
            {
                // ウォームアップ(JIT・計測外)
                sink += WordBoundary.WordStart(snap, pos, cap);
                sink += WordBoundary.WordEnd(snap, pos, cap);
                sink += WordBoundary.PrevWordStart(snap, pos, cap);

                int s = WordBoundary.WordStart(snap, pos, cap);
                int e = WordBoundary.WordEnd(snap, pos, cap);

                double expandMs =
                    BestOf3(() => sink += WordBoundary.WordStart(snap, pos, cap))
                    + BestOf3(() => sink += WordBoundary.WordEnd(snap, pos, cap));
                double navMs = BestOf3(() => sink += WordBoundary.PrevWordStart(snap, pos, cap));

                double worstExpandMs = 0;
                double worstNavMs = 0;
                for (int i = 0; i < PhaseSamples; i++)
                {
                    int p = pos + (i * PhaseStride);
                    worstExpandMs = Math.Max(
                        worstExpandMs,
                        BestOf3(() => sink += WordBoundary.WordStart(snap, p, cap))
                            + BestOf3(() => sink += WordBoundary.WordEnd(snap, p, cap))
                    );
                    worstNavMs = Math.Max(
                        worstNavMs,
                        BestOf3(() => sink += WordBoundary.PrevWordStart(snap, p, cap))
                    );
                }

                int spanCp = CountCodePoints(snap, s, e);
                // WordStart の窓は左だけ 1 狭い(pos + 1 を PrevWordStart へ渡すため)ので、
                // cap を使い切った状態のスパン幅は 2*cap - 1 code point になる。
                bool hitCap = spanCp >= (2 * cap) - 1;
                Console.WriteLine(
                    $"| {kind} | {cap} | {expandMs:F4} | **{worstExpandMs:F4}** | {navMs:F4} | "
                        + $"**{worstNavMs:F4}** | {e - s:N0} | {spanCp:N0} | {(hitCap ? "**到達**" : "-")} |"
                );
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            "注: **中央列と最悪位相列の差が「位相の当たり外れ」そのもの**である。1 点だけ測って"
                + "cap を決めると、その pos がたまたま格子セルの先頭側だっただけ、ということが起こる。"
        );
        Console.WriteLine(
            "注: ascii / cjk は単一クラスの 500K 連続なので cap が必ず効く(= 到達)。"
                + "jamix はクラス境界が 1〜4 文字ごとに来るため cap に触れず、"
                + "**cap を上げても下げてもスパンもコストも変わらない**。"
        );
        Console.WriteLine();
        Console.WriteLine($"(sink={sink})");
    }

    // ===== 4. 現実のテキストにおけるクラス run 長 =====

    /// <summary>
    /// 実ファイル。リポジトリルート(<c>kxEdit.sln</c> のある場所)からの相対パスで解決する
    /// (ベンチの実行ディレクトリは <c>bin/Release/net9.0-windows</c> 配下)。
    /// </summary>
    private static readonly (string Label, string RelPath)[] RealFiles =
    [
        ("日本語散文(設計書)", "docs/plans/2026-08-03-uia-word-unit-design.md"),
        ("日本語散文(ユーザー向け説明書)", "説明書/kxEdit説明書.md"),
        ("C# コード", "src/kxEdit.Editor/EditorControl.cs"),
        ("英語主体(YAML)", ".github/workflows/ci.yml"),
        ("英語主体(csproj)", "src/kxEdit.App/kxEdit.App.csproj"),
        ("日英混在(README)", "README.md"),
    ];

    /// <summary>1 テキストのクラス run 統計(長さの単位はすべて code point)。</summary>
    private sealed record RunStats(string Label, int CodePoints, int[] WordRuns, int MaxWsRun);

    /// <summary>
    /// クラス run 長の分布。<b>cap がこれを下回ると普通の文章で単語が切れる</b>ので、
    /// 速度側(§3)と合わせて cap の下限を決めるための材料になる。
    /// </summary>
    /// <remarks>
    /// 1 回の <c>WordStart</c> / <c>WordEnd</c> が走る距離は、pos を含む<b>クラス run の長さ</b>で
    /// 決まる(<c>WordEnd</c> が飛び越える末尾の空白は巻き戻しで打ち消されるので予算に効かない)。
    /// したがって run 長 L の run は <c>cap &gt;= L</c> なら切り詰められない。
    /// </remarks>
    private static void PrintRunLengthTables()
    {
        Console.WriteLine();
        Console.WriteLine("## 4. 現実のテキストにおけるクラス run 長(単位 = code point)");
        Console.WriteLine();
        Console.WriteLine(
            "「単語 run」= 同一文字クラス(Latin / Digit / Hiragana / Katakana / Han / Other)の連続。"
                + "「空白 run」= Whitespace / LineBreak の連続(改行 + 行頭インデントは 1 本に繋がる)。"
                + "**cap >= run 長なら、その run では切り詰めが起きない。**"
        );

        var stats = new List<RunStats>();
        foreach (var (name, text) in Fixtures)
            stats.Add(Analyze($"fixture `{name}`", TextBuffer.FromString(text).Current));
        stats.AddRange(CollectRealFileStats());

        Console.WriteLine();
        Console.WriteLine(
            "| テキスト | code point | 単語 run 数 | 平均 | p50 | p90 | p99 | **最長** | 空白 run 最長 |"
        );
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var st in stats)
        {
            var sorted = st.WordRuns.Order().ToArray();
            double avg = sorted.Length == 0 ? 0 : (double)sorted.Sum() / sorted.Length;
            Console.WriteLine(
                $"| {st.Label} | {st.CodePoints:N0} | {sorted.Length:N0} | {avg:F1} | "
                    + $"{Percentile(sorted, 0.50)} | {Percentile(sorted, 0.90)} | {Percentile(sorted, 0.99)} | "
                    + $"**{(sorted.Length == 0 ? 0 : sorted[^1])}** | {st.MaxWsRun} |"
            );
        }

        Console.WriteLine();
        Console.WriteLine("### cap 候補ごとに切り詰められる単語 run の数");
        Console.WriteLine();
        Console.WriteLine(
            "各セルは「run 長 > cap の単語 run 数」= その cap を採ると**単語の途中で切れる箇所の数**。"
        );
        Console.WriteLine();
        Console.WriteLine(
            "| テキスト | 単語 run 数 | "
                + string.Join(" | ", CapCandidates.Select(c => $"cap={c}"))
                + " |"
        );
        Console.WriteLine("|---|---|" + string.Concat(CapCandidates.Select(_ => "---|")));
        foreach (var st in stats)
        {
            var cells = CapCandidates.Select(c => $"{st.WordRuns.Count(len => len > c):N0}");
            Console.WriteLine(
                $"| {st.Label} | {st.WordRuns.Length:N0} | " + string.Join(" | ", cells) + " |"
            );
        }
    }

    /// <summary>実ファイルを読んで統計を採る。見つからないファイルはスキップして落とさない。</summary>
    private static List<RunStats> CollectRealFileStats()
    {
        var list = new List<RunStats>();
        string? root = FindRepoRoot();
        if (root is null)
        {
            Console.WriteLine();
            Console.WriteLine(
                "（リポジトリルート(`kxEdit.sln` のあるディレクトリ)が見つからないため実ファイルの統計をスキップ）"
            );
            return list;
        }

        Console.WriteLine();
        Console.WriteLine($"（実ファイルのルート: `{root}`）");
        foreach (var (label, rel) in RealFiles)
        {
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            string? text = TryReadAllText(full);
            if (text is null)
            {
                Console.WriteLine($"（見つからなかった / 読めなかったのでスキップ: `{rel}`）");
                continue;
            }
            list.Add(Analyze($"{label} `{rel}`", TextBuffer.FromString(text).Current));
        }
        return list;
    }

    /// <summary>実行ディレクトリから上へ辿って <c>kxEdit.sln</c> のあるディレクトリを探す。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "kxEdit.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>読めなければ null。<b>例外でベンチを落とさない</b>。</summary>
    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// テキストをクラス run へ分解する。<b>規則は本物(<see cref="WordBoundary"/>)から得る</b>=
    /// <c>ClassOf</c> は internal なのでベンチから直接は呼べないし、写経もしない。
    /// </summary>
    /// <remarks>
    /// <c>WordEnd(pos, NoScanLimit)</c> は pos を含むクラス run の終端(末尾空白を含まない)を返すので、
    /// 非空白位置ではそのまま run 境界になる。空白位置では <c>WordEnd(pos) == pos</c> なので、
    /// そこだけ <c>NextWordStart</c> で空白 run を飛ばす。
    /// </remarks>
    private static RunStats Analyze(string label, TextSnapshot snap)
    {
        var wordRuns = new List<int>();
        int maxWs = 0;
        int totalCp = 0;
        int pos = 0;
        while (pos < snap.CharLength)
        {
            int end = WordBoundary.WordEnd(snap, pos, WordBoundary.NoScanLimit);
            bool isWord = end > pos;
            if (!isWord)
            {
                end = WordBoundary.NextWordStart(snap, pos, WordBoundary.NoScanLimit);
                if (end <= pos)
                    break; // 進まなくなったら終了(無限ループ防止)
            }
            int cp = CountCodePoints(snap, pos, end);
            totalCp += cp;
            if (isWord)
                wordRuns.Add(cp);
            else
                maxWs = Math.Max(maxWs, cp);
            pos = end;
        }
        return new RunStats(label, totalCp, [.. wordRuns], maxWs);
    }

    /// <summary>[start, end) に含まれる code point 数。char 数とは非 BMP でずれる。</summary>
    private static int CountCodePoints(TextSnapshot snap, int start, int end)
    {
        int n = 0;
        for (int p = start; p < end; p = TextBoundary.NextCodePoint(snap, p))
            n++;
        return n;
    }

    /// <summary>昇順ソート済み配列の分位点(nearest-rank)。空なら 0。</summary>
    private static int Percentile(int[] sorted, double q) =>
        sorted.Length == 0
            ? 0
            : sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];

    // ===== 共通ヘルパ =====

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
    ///   文字クラス規則へ揃えても走査は縮まない=cap が要る条件。
    ///   生成規則は <c>LargeLineBench.MakeSingleLine</c> と同一・同一シードで前後比較が成立する。</description>
    /// </item>
    /// <item>
    ///   <term>cjk</term>
    ///   <description>U+3042 から 40 種。<b>全て Hiragana クラス</b>なので ascii と同じく単一クラス。
    ///   同上(生成規則・シードとも <c>LargeLineBench.MakeSingleLine</c> と同一)。</description>
    /// </item>
    /// <item>
    ///   <term>jamix</term>
    ///   <description>漢字 / ひらがな / カタカナが 1〜4 文字ごとに交替する現実的な日本語。
    ///   <b>文字クラス規則の効果はこの kind でしか見えない</b> —
    ///   ascii / cjk は単一クラスなのでクラス規則を当てても全走査のままになる。</description>
    /// </item>
    /// </list>
    /// </remarks>
    private static string MakeSingleLine(int chars, string kind)
    {
        var sb = new StringBuilder(chars);
        // LargeLineBench.MakeSingleLine(20260802)/ Core.Bench の MakeSingleLine(20260802)と同一シード。
        // ascii / cjk の fixture が literally 同一になり、既存実測との前後比較が成立する。
        // Core.Bench/Program.cs:233-234 の「2 つのベンチが対であることが設計の前提なので、
        // 片方だけ変えないこと」に従う。jamix は本テーマで追加した新 kind なので取り決めの対象外。
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
