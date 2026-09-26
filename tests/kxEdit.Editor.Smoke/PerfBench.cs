using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using kxEdit.Accessibility;
using kxEdit.Core.Buffers;
using kxEdit.Core.Editing;
using kxEdit.Core.Settings;

namespace kxEdit.Editor.Smoke;

/// <summary>
/// 性能改善フェーズの共通計測(設計書 docs/plans/2026-09-24-general-perf-improvements-design.md §5.1・
/// 実装計画 docs/plans/2026-09-24-perf-bench.md)。「操作 → <c>editor.Update()</c>(同期 WM_PAINT)」を
/// 1 回として Stopwatch で測り、平均・中央値・p95 と 1 操作あたりの WM_PAINT 回数を出す。
/// </summary>
/// <remarks>
/// <para>
/// <b>判定はしない</b>(基準値の固定はデータがたまってから)。EXIT 1 になるのは
/// <b>自己チェックの失敗</b>=入力が効いておらず「何もしていない時間」を測っている場合だけ。
/// EXIT 2 は引数の誤り。
/// </para>
/// <para>
/// <b>測定条件</b>。(1) Form は<b>画面内</b>に置く。画面外の窓には <c>Update()</c> が WM_PAINT を
/// 配送しない(<see cref="GdiBench"/> と同じ理由)。作業領域に収まることと、S7 で 1 操作 = 1 WM_PAINT
/// であることを自己チェックする。(2) フォーカスが必須。<c>PositionCaret</c> は <c>_hasFocus</c> で
/// 早期 return する(<see cref="WrapScrollBench"/> と同じ理由)。(3) 外観は
/// <c>ApplyAppearance(new AppSettings())</c>=製品の既定(ＭＳ ゴシック 12pt・全角表記)。
/// perf-harness(tools/perf-harness.ps1)も空のプロフィール=同じ既定で測るので条件が揃う。
/// (4) 最初のシナリオの前に全体のウォームアップ、各シナリオの前に GC を行う
/// (JIT の段階昇格と前のシナリオのゴミを計測区間に混ぜない)。
/// </para>
/// <para>
/// <b>入力経路</b>。キーは <c>WM_KEYDOWN</c> / <c>WM_CHAR</c> を <c>__TestProcessMessage</c>(WndProc)に
/// 入れる=<c>OnKeyDown</c> → <c>InputRouter</c> / <c>OnKeyPress</c> → <c>InsertConfirmedText</c> の
/// 実経路を通る。IME と貼り付け片は既存のテストフック(<c>__TestApplyComposition</c> /
/// <c>__TestApplyResult</c>)を使う。
/// </para>
/// <para>
/// <b>測らないもの</b>(体感値は perf-harness で測る): IME/TSF の費用(調査記録 §9 の S-2)、
/// 実キー入力の対になる片側(文字キーの WM_KEYDOWN・BackSpace の WM_CHAR 0x08)、
/// PreProcessMessage / ProcessCmdKey、App 層のステータスバー更新、UIA イベントの配信(クライアント不在)、
/// S8 の RPC スレッドからの Invoke によるマーシャリング(RPC スレッドからの折り返し ON の行読みは
/// S9 がワーカースレッドで測る)、IME の Imm 呼び出し。
/// <b>計測中はメッセージを汲まないので、BeginInvoke やタイマーに回した処理も計上しない</b>
/// (現状の打鍵経路には無い)。処理を投函・間引きに回すフェーズ(例: フェーズ 3・5)の効果は、
/// 静穏待ちまで含めて CPU を測る perf-harness で判定すること。
/// </para>
/// <para>
/// <b>WM_PAINT の数え方</b>: <c>Paint</c> イベントで数えるので、<c>OnPaint</c> が末尾で
/// <c>base.OnPaint</c> を呼ぶことに依存する(EditorControl.Paint.cs)。OnPaint に早期 return を入れると
/// <c>paints_per_op</c> は過少になる(S7 は自己チェックで EXIT 1 になるので安全側)。
/// </para>
/// <para>
/// <b>比較の規則</b>: JIT やキャッシュの状態がシナリオの順序に依存するので、変更前後は同じ
/// <c>--scenario</c> の集合で比べる(tools/README.md §3)。
/// </para>
/// </remarks>
internal static class PerfBench
{
    private const int WmKeyDown = 0x0100;
    private const int WmChar = 0x0102;

    private const string Ja10kLine =
        "{0:D5}: 吾輩は猫である。名前はまだ無い。kxEdit の性能計測 sample 行です。";
    private const string En10kLine =
        "{0:D5}: The quick brown fox jumps over the lazy dog; perf sample line.";
    private const string PasteLine =
        "{0:D3}: 吾輩は猫である。名前はまだ無い。どこで生れたかとんと見当がつかぬ。";

    /// <summary>S1〜S4 の事前状態で置くキャレット行(調査記録 §9.5「Ctrl+Home の後に ↓ を 10 回」)。</summary>
    private const int CaretLine = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // CSV の列名(mean_ms 等)と揃える。後続フェーズが前後比較でこの JSON を読むのでスキーマを固定する。
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static readonly string[] AllScenarios =
    [
        "S1",
        "S2",
        "S3",
        "S4",
        "S5",
        "S6",
        "S7",
        "S8",
        "S9",
    ];

    /// <summary>計測中に EditorControl の <c>Paint</c> が発火した回数(描画が本当に配送されたかの観測)。</summary>
    private static int s_paints;

    /// <summary>
    /// 1 シナリオ × 1 文書の集計結果。<c>--json</c> の <c>results</c> の 1 要素にもなる。
    /// <paramref name="Param"/> はシナリオ固有の条件(S5 の書込位置など)。<paramref name="PaintsPerOp"/> は
    /// 1 操作あたりの WM_PAINT 回数=再描画の省略(フェーズ 3)や部分再描画(フェーズ 9)の効果の証拠になる。
    /// </summary>
    internal sealed record Result(
        string Id,
        string Doc,
        string? Param,
        int N,
        double MeanMs,
        double MedianMs,
        double P95Ms,
        double MinMs,
        double MaxMs,
        double PaintsPerOp
    );

    /// <summary><c>--json</c> のメタ情報(前後比較で条件の違いを見分けるため)。</summary>
    internal sealed record Meta(
        string Timestamp,
        int N,
        int Warmup,
        string[] Scenarios,
        int DeviceDpi,
        string ClientSize,
        int LineHeightPx,
        string FontName,
        float FontSize,
        bool Optimized,
        string Runtime,
        string Os
    );

    /// <summary><c>--json</c> の最上位。</summary>
    internal sealed record Report(Meta Meta, List<Result> Results);

    /// <summary>1 回の計測値。</summary>
    private readonly record struct Sample(double Ms, int Paints);

    private sealed class Options
    {
        public int N { get; set; } = 200;
        public int Warmup { get; set; } = 20;
        public HashSet<string> Scenarios { get; } = new(AllScenarios, StringComparer.Ordinal);
        public string? JsonPath { get; set; }
    }

    public static int Run(string[] args)
    {
        Options opt;
        try
        {
            opt = ParseArgs(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(
                $"使い方: --perf [--n <回数>] [--warmup <回数>] [--scenario {string.Join(',', AllScenarios)}] [--json <path>]"
            );
            return 2;
        }

        bool optimized = !(
            typeof(EditorControl)
                .Assembly.GetCustomAttribute<DebuggableAttribute>()
                ?.IsJITOptimizerDisabled
            ?? false
        );
        if (!optimized)
        {
            Console.Error.WriteLine(
                "[警告] kxEdit.Editor が最適化なしのビルド(Debug)。計測は -c Release で行うこと。"
            );
        }

        var results = new List<Result>();
        ApplicationConfiguration.Initialize();
        using var form = new Form
        {
            Text = "kxEdit.Editor.Smoke --perf",
            Width = 900,
            Height = 700,
            StartPosition = FormStartPosition.Manual,
            // 画面外の窓には WM_PAINT が来ない=描画を測り落とす(GdiBench と同じ理由)。
            // 小さい画面(調査環境は 1024×767)でも収まるよう作業領域の左上に置く。
            Location = Screen.PrimaryScreen?.WorkingArea.Location ?? Point.Empty,
            ShowInTaskbar = false,
        };
        using var editor = new EditorControl { Dock = DockStyle.Fill };
        form.Controls.Add(editor);
        form.Show(); // ハンドル生成(Show しないと Invalidate/Update が no-op)
        editor.ApplyAppearance(new AppSettings());
        editor.Paint += (_, _) => s_paints++;
        editor.Focus();
        Application.DoEvents();

        Console.WriteLine(
            $"DeviceDpi={editor.DeviceDpi} / ClientSize={editor.ClientSize}"
                + $" / LineHeightPx={editor.LineHeightPx} / 可視行≈{VisibleRows(editor)}"
                + $" / n={opt.N} / warmup={opt.Warmup}"
        );

        try
        {
            Check(
                Screen.FromControl(form).WorkingArea.Contains(form.Bounds),
                $"窓 {form.Bounds} が画面の作業領域に収まらない(WM_PAINT を測り落とす)"
            );
            CheckFocus(editor);
            WarmUpGlobally(editor);

            var docs = new (string Name, string Text)[]
            {
                ("ja10k", BuildDoc(Ja10kLine, 10_000, 990_000)),
                ("en10k", BuildDoc(En10kLine, 10_000, 710_000)),
            };
            // シナリオごとに新しいバッファを作る=前のシナリオの編集・Undo 履歴・ピース構成を持ち越さない。
            foreach (var (name, text) in docs)
            {
                if (opt.Scenarios.Contains("S1"))
                    results.Add(MeasureArrows(editor, Fresh(text), name, opt, vertical: false));
                if (opt.Scenarios.Contains("S2"))
                    results.Add(MeasureArrows(editor, Fresh(text), name, opt, vertical: true));
                if (opt.Scenarios.Contains("S3"))
                    results.AddRange(MeasureTyping(editor, Fresh(text), name, opt));
                if (opt.Scenarios.Contains("S4"))
                    results.Add(MeasureComposition(editor, Fresh(text), name, opt));
                if (opt.Scenarios.Contains("S6"))
                    results.AddRange(MeasureScroll(editor, Fresh(text), name, opt));
                if (opt.Scenarios.Contains("S7"))
                    results.Add(MeasureFullRepaint(editor, Fresh(text), name, opt));
            }
            if (opt.Scenarios.Contains("S8"))
                results.AddRange(MeasureUiaRects(editor, Fresh(docs[0].Text), docs[0].Name, opt));
            if (opt.Scenarios.Contains("S5"))
                results.AddRange(MeasureAppendBlock(editor));
            if (opt.Scenarios.Contains("S9"))
                results.AddRange(
                    MeasureUiaWrapLines(editor, Fresh(docs[0].Text), docs[0].Name, opt)
                );
        }
        catch (PerfSelfCheckException e)
        {
            Console.Error.WriteLine($"[自己チェック失敗] {e.Message}");
            form.Close();
            return 1;
        }

        Print(results);
        if (opt.JsonPath is not null)
        {
            var meta = new Meta(
                DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                opt.N,
                opt.Warmup,
                [.. AllScenarios.Where(opt.Scenarios.Contains)],
                editor.DeviceDpi,
                $"{editor.ClientSize.Width}x{editor.ClientSize.Height}",
                editor.LineHeightPx,
                // 描画に使うフォント(Control.Font ではない)。既定フォント名の解決が変わったときに気づけるよう記録する。
                ((IImeOverlayHost)editor)
                    .Font
                    .Name,
                ((IImeOverlayHost)editor).Font.SizeInPoints,
                optimized,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription
            );
            File.WriteAllText(
                opt.JsonPath,
                JsonSerializer.Serialize(new Report(meta, results), JsonOptions),
                new UTF8Encoding(false)
            );
            Console.WriteLine($"JSON: {opt.JsonPath}");
        }

        form.Close();
        Console.WriteLine("(性能観測用ベンチのため判定ゲートなし) EXIT 0");
        return 0;
    }

    // ---- シナリオ ----

    /// <summary>S1(→/← の交互)・S2(↓/↑ の交互)。選択なし・スクロールなし。</summary>
    private static Result MeasureArrows(
        EditorControl editor,
        TextBuffer buffer,
        string doc,
        Options opt,
        bool vertical
    )
    {
        string id = vertical ? "S2" : "S1";
        Keys forward = vertical ? Keys.Down : Keys.Right;
        Keys back = vertical ? Keys.Up : Keys.Left;
        PrepareAtCaretLine(editor, buffer);

        int before = editor.CaretCharOffset;
        KeyDown(editor, forward);
        int moved = editor.CaretCharOffset - before;
        KeyDown(editor, back);
        int expected = vertical
            ? buffer.Current.GetLineStart(CaretLine + 1) - buffer.Current.GetLineStart(CaretLine)
            : 1;
        Check(
            moved == expected && editor.CaretCharOffset == before && editor.TopLine == 0,
            $"{id}/{doc}: キーでキャレットが動かない(移動量 {moved}・期待 {expected})"
        );

        int i = 0;
        return Measure(
            editor,
            id,
            doc,
            opt.N,
            opt.Warmup,
            update: true,
            () => KeyDown(editor, (i++ % 2 == 0) ? forward : back)
        );
    }

    /// <summary>S3a(1 文字挿入)と S3b(BackSpace)を交互に行い、別々に集計する(本文を伸ばし続けない)。</summary>
    private static List<Result> MeasureTyping(
        EditorControl editor,
        TextBuffer buffer,
        string doc,
        Options opt
    )
    {
        PrepareAtCaretLine(editor, buffer);
        int len0 = editor.CurrentBuffer.Current.CharLength;
        TypeChar(editor, 'x');
        int len1 = editor.CurrentBuffer.Current.CharLength;
        KeyDown(editor, Keys.Back);
        Check(
            len1 == len0 + 1 && editor.CurrentBuffer.Current.CharLength == len0,
            $"S3/{doc}: WM_CHAR / BackSpace で本文長が変わらない({len0} → {len1})"
        );

        var insert = new List<Sample>(opt.N);
        var back = new List<Sample>(opt.N);
        CollectGarbage();
        for (int k = 0; k < opt.Warmup + opt.N; k++)
        {
            var a = TimeOnce(editor, update: true, () => TypeChar(editor, 'x'));
            var b = TimeOnce(editor, update: true, () => KeyDown(editor, Keys.Back));
            if (k >= opt.Warmup)
            {
                insert.Add(a);
                back.Add(b);
            }
        }
        Check(
            editor.CurrentBuffer.Current.CharLength == len0,
            $"S3/{doc}: 計測後の本文長が元に戻らない"
        );
        return [Summarize("S3a", doc, null, insert), Summarize("S3b", doc, null, back)];
    }

    /// <summary>S4: IME の未確定を「あ」「あい」で交互に更新する。</summary>
    private static Result MeasureComposition(
        EditorControl editor,
        TextBuffer buffer,
        string doc,
        Options opt
    )
    {
        PrepareAtCaretLine(editor, buffer);
        string[] texts = ["あ", "あい"];
        byte[][] attrs =
        [
            [ImeAttribute.Input],
            [ImeAttribute.Input, ImeAttribute.Input],
        ];
        editor.__TestApplyComposition(texts[0], 1, attrs[0], []);
        Check(
            editor.__TestIsComposing() && editor.__TestImeText() == texts[0],
            $"S4/{doc}: __TestApplyComposition で未確定状態にならない"
        );

        int i = 0;
        var r = Measure(
            editor,
            "S4",
            doc,
            opt.N,
            opt.Warmup,
            update: true,
            () =>
            {
                int k = i++ % 2 == 0 ? 1 : 0;
                editor.__TestApplyComposition(texts[k], texts[k].Length, attrs[k], []);
            }
        );
        editor.__TestApplyResult(""); // 未確定を解除(本文は変えない)
        Check(!editor.__TestIsComposing(), $"S4/{doc}: 未確定を解除できない");
        return r;
    }

    /// <summary>
    /// S6a(1 行スクロール)・S6b(1 ページスクロール)。<c>TopLine</c> setter を ± で交互。
    /// 「ページ」は <c>ClientSize.Height / LineHeightPx</c> で数える。製品の <c>VisibleRowCount</c>
    /// (<c>PaintHeightPx</c> 基準・private)とは横スクロールバーの表示時にずれうるが、全面再描画の間は
    /// 費用に差が出ない(フェーズ 9 で ScrollWindowEx を入れるときに見直す)。
    /// </summary>
    private static List<Result> MeasureScroll(
        EditorControl editor,
        TextBuffer buffer,
        string doc,
        Options opt
    )
    {
        const int Base = 100;
        editor.SetOrReplaceSource(buffer);
        editor.SetCaretCharOffset(0);
        int page = Math.Max(1, VisibleRows(editor));
        var results = new List<Result>();
        foreach (var (id, step) in new[] { ("S6a", 1), ("S6b", page) })
        {
            editor.TopLine = Base;
            editor.Update();
            editor.TopLine = Base + step;
            Check(editor.TopLine == Base + step, $"{id}/{doc}: TopLine が変わらない");
            editor.TopLine = Base;
            editor.Update();
            int i = 0;
            results.Add(
                Measure(
                    editor,
                    id,
                    doc,
                    opt.N,
                    opt.Warmup,
                    update: true,
                    () => editor.TopLine = (i++ % 2 == 0) ? Base + step : Base
                )
            );
        }
        editor.TopLine = 0;
        return results;
    }

    /// <summary>S7: 全面再描画だけ(外から RedrawWindow したのと同じ量)。1 操作 = 1 WM_PAINT を検査する。</summary>
    private static Result MeasureFullRepaint(
        EditorControl editor,
        TextBuffer buffer,
        string doc,
        Options opt
    )
    {
        editor.SetOrReplaceSource(buffer);
        editor.SetCaretCharOffset(0);
        editor.TopLine = 0;
        editor.Update();
        return Measure(
            editor,
            "S7",
            doc,
            opt.N,
            opt.Warmup,
            update: true,
            editor.Invalidate,
            requireOnePaint: true
        );
    }

    /// <summary>
    /// S8: UIA の <c>GetBoundingRectangles</c> を UI スレッドから直接呼ぶ(COM と Invoke を通らない)。
    /// 範囲は文書先頭から 1 行・40 行・1,000 行・全文。行末の CRLF は範囲に含めない(ちょうど n 行)。
    /// 描画は起きないので <c>Update()</c> しない。
    /// </summary>
    private static List<Result> MeasureUiaRects(
        EditorControl editor,
        TextBuffer buffer,
        string doc,
        Options opt
    )
    {
        editor.SetOrReplaceSource(buffer);
        editor.SetCaretCharOffset(0);
        editor.TopLine = 0;
        editor.Update();
        var snap = buffer.Current;
        var host = (IUiaTextHost)editor;
        // 可視域の矩形数(下端の部分行を含む)。可視行数は DPI で変わるので実測して期待値にする。
        int visible = host.GetBoundingRectangles(0, snap.CharLength).Length / 4;
        Check(visible >= 1, "S8: 全文の範囲で矩形が返らない(可視域を測れていない)");
        // 全文 1 回で 3.5 秒級(調査記録 M-7)なので、行数の多い範囲は回数を絞る。
        var cases = new (string Id, int Lines, int N)[]
        {
            ("S8-1", 1, opt.N),
            ("S8-40", 40, opt.N),
            ("S8-1000", 1000, 10),
            ("S8-all", snap.LineCount, 3),
        };
        var results = new List<Result>();
        foreach (var (id, lines, n) in cases)
        {
            int end = lines >= snap.LineCount ? snap.CharLength : snap.GetLineStart(lines) - 2;
            int rects = host.GetBoundingRectangles(0, end).Length / 4;
            int expected = Math.Min(lines, visible); // 範囲の行数ぶん。ただし可視域で頭打ち
            Check(rects == expected, $"{id}: 矩形数 {rects}(期待 {expected})");
            results.Add(
                Measure(
                    editor,
                    id,
                    doc,
                    n,
                    warmup: 1,
                    update: false,
                    () => host.GetBoundingRectangles(0, end)
                )
            );
        }
        return results;
    }

    /// <summary>
    /// S9(P-10・フェーズ 10): 折り返し ON(40 桁)で、NVDA の say all 相当の行読みを<b>ワーカースレッドから</b>
    /// 行う(UIA の RPC スレッドの代わり)。1 歩 = <c>TextRangeProviderV2.Move(Line, 1)</c> +
    /// 非退化レンジの <c>ExpandToEnclosingUnit(Line)</c> と同じ呼び出し列
    /// (<c>LineEnd</c> → <c>LineStartOf</c> → <c>LineEndNoBreakOf</c>)。
    /// S9a は UI スレッドがメッセージを汲むだけ(アイドル)、S9b は汲む合間に全面再描画を挟む
    /// (描画中の say all)。<c>param</c> 列は 1 歩あたりの UI スレッドへの Invoke 回数。
    /// </summary>
    private static List<Result> MeasureUiaWrapLines(
        EditorControl editor,
        TextBuffer buffer,
        string doc,
        Options opt
    )
    {
        const int Wrap = 40;
        editor.SetOrReplaceSource(buffer);
        editor.SetCaretCharOffset(0);
        editor.TopLine = 0;
        int oldWrap = editor.WrapColumns;
        editor.WrapColumns = Wrap;
        editor.Update();
        var host = (IUiaTextHost)editor;
        try
        {
            var snap = buffer.Current;
            Check(
                host.LineEnd(0) < snap.GetLineEnd(0, includeBreak: false),
                $"S9/{doc}: 行 0 が折り返されていない(折り返し ON の経路を測れていない)"
            );
            return
            [
                MeasureWorkerLineReads(editor, host, "S9a", doc, opt, busyUi: false),
                MeasureWorkerLineReads(editor, host, "S9b", doc, opt, busyUi: true),
            ];
        }
        finally
        {
            editor.WrapColumns = oldWrap;
        }
    }

    /// <summary>
    /// S9 の 1 本。文書先頭から <c>Warmup + N</c> 歩、ワーカースレッドで行を読み進め、1 歩ずつ測る。
    /// その間 UI スレッドは <c>Application.DoEvents</c> で Invoke を汲む(<paramref name="busyUi"/> なら
    /// 汲む前に毎回 <c>Invalidate</c> + <c>Update</c> で全面を描く)。
    /// </summary>
    private static Result MeasureWorkerLineReads(
        EditorControl editor,
        IUiaTextHost host,
        string id,
        string doc,
        Options opt,
        bool busyUi
    )
    {
        var samples = new List<Sample>(opt.N);
        long invokes0 = 0;
        long invokes1 = 0;
        CollectGarbage();
        var worker = Task.Run(() =>
        {
            int pos = 0;
            for (int k = 0; k < opt.Warmup + opt.N; k++)
            {
                if (k == opt.Warmup)
                    invokes0 = editor.TestHook_LineSegsInvokeCount;
                int p0 = Volatile.Read(ref s_paints);
                long t0 = Stopwatch.GetTimestamp();
                int next = host.LineEnd(pos);
                int start = host.LineStartOf(next);
                int end = host.LineEndNoBreakOf(next);
                double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (next <= pos || start != next || end <= start)
                    throw new PerfSelfCheckException(
                        $"{id}/{doc}: 行読みが進まない(pos {pos} → {next}・[{start}, {end}))"
                    );
                if (k >= opt.Warmup)
                    samples.Add(new Sample(ms, Volatile.Read(ref s_paints) - p0));
                pos = next;
            }
            invokes1 = editor.TestHook_LineSegsInvokeCount;
        });
        while (!worker.IsCompleted)
        {
            if (busyUi)
            {
                editor.Invalidate();
                editor.Update();
            }
            Application.DoEvents();
        }
        worker.GetAwaiter().GetResult(); // ワーカーの例外(自己チェックの失敗を含む)をそのまま投げ直す
        double invokesPerStep = (double)(invokes1 - invokes0) / opt.N;
        return Summarize(
            id,
            doc,
            invokesPerStep.ToString("F2", CultureInfo.InvariantCulture),
            samples
        );
    }

    /// <summary>
    /// S5(F-6): 空の新規文書に貼り付け片(約 10.6KB)を 1 回入れるたびに、末尾で <c>x</c> を 40 回打って
    /// 1 打鍵を測る。ブロック(64KB)内の書込位置に比例して重くなる「のこぎり型」を見る(調査記録 M-3)。
    /// Id は <c>S5-r&lt;回&gt;</c>、Param は計測直前の UTF-8 バイト数(=AppendBuffer への書込位置)。
    /// </summary>
    private static List<Result> MeasureAppendBlock(EditorControl editor)
    {
        const int Rounds = 10;
        const int Keystrokes = 40;
        const int Warmup = 5;
        string paste = BuildDoc(PasteLine, 100, 10_600);
        editor.SetOrReplaceSource(TextBuffer.FromString(""));
        editor.Update();
        var results = new List<Result>();
        for (int round = 0; round < Rounds; round++)
        {
            for (int w = 0; w < Warmup; w++)
                TimeOnce(editor, update: true, () => TypeChar(editor, 'x'));
            int bytes = Utf8Length(editor);
            int len0 = editor.CurrentBuffer.Current.CharLength;
            var samples = new List<Sample>(Keystrokes);
            CollectGarbage();
            for (int k = 0; k < Keystrokes; k++)
                samples.Add(TimeOnce(editor, update: true, () => TypeChar(editor, 'x')));
            Check(
                editor.CurrentBuffer.Current.CharLength == len0 + Keystrokes,
                $"S5: 打鍵で本文が伸びない(位置 {bytes})"
            );
            results.Add(
                Summarize(
                    $"S5-r{round}",
                    "append",
                    bytes.ToString(CultureInfo.InvariantCulture),
                    samples
                )
            );

            int before = editor.CurrentBuffer.Current.CharLength;
            editor.__TestApplyResult(paste);
            editor.Update();
            Check(
                editor.CurrentBuffer.Current.CharLength == before + paste.Length,
                "S5: 貼り付け片が挿入されない"
            );
        }
        return results;
    }

    // ---- 計測の核 ----

    /// <summary>
    /// ウォームアップ <paramref name="warmup"/> 回の後、<paramref name="op"/>(+ <paramref name="update"/>
    /// なら <c>editor.Update()</c>)を <paramref name="n"/> 回、1 回ずつ測る。
    /// <paramref name="requireOnePaint"/> なら毎回 WM_PAINT がちょうど 1 回であることを検査する。
    /// </summary>
    private static Result Measure(
        EditorControl editor,
        string id,
        string doc,
        int n,
        int warmup,
        bool update,
        Action op,
        bool requireOnePaint = false
    )
    {
        for (int k = 0; k < warmup; k++)
            TimeOnce(editor, update, op);
        var samples = new List<Sample>(n);
        CollectGarbage();
        for (int k = 0; k < n; k++)
        {
            var s = TimeOnce(editor, update, op);
            if (requireOnePaint)
                Check(
                    s.Paints == 1,
                    $"{id}/{doc}: 1 操作の WM_PAINT が {s.Paints} 回(描画を測れていない)"
                );
            samples.Add(s);
        }
        return Summarize(id, doc, null, samples);
    }

    private static Sample TimeOnce(EditorControl editor, bool update, Action op)
    {
        int p0 = s_paints;
        long t0 = Stopwatch.GetTimestamp();
        op();
        if (update)
            editor.Update(); // 同期 WM_PAINT
        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return new Sample(ms, s_paints - p0);
    }

    private static Result Summarize(string id, string doc, string? param, List<Sample> samples)
    {
        var sorted = samples.Select(s => s.Ms).OrderBy(x => x).ToArray();
        int n = sorted.Length;
        double median = n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2.0;
        double p95 = sorted[Math.Max(0, (int)Math.Ceiling(n * 0.95) - 1)];
        double paints = samples.Average(s => s.Paints);
        return new Result(
            id,
            doc,
            param,
            n,
            sorted.Average(),
            median,
            p95,
            sorted[0],
            sorted[^1],
            paints
        );
    }

    /// <summary>前のシナリオが出したゴミの GC を計測区間に混ぜない。</summary>
    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// 全体のウォームアップ。最初に測るシナリオだけが JIT の段階昇格(tiered compilation)を計測区間内で
    /// 払わないよう、キー・打鍵・描画の経路を計測せずに約 1.5 秒回す。<c>--scenario</c> で一部だけを
    /// 走らせたときも JIT の状態を揃える。
    /// </summary>
    private static void WarmUpGlobally(EditorControl editor)
    {
        PrepareAtCaretLine(editor, Fresh(BuildDoc(Ja10kLine, 10_000, 990_000)));
        var sw = Stopwatch.StartNew();
        bool right = true;
        while (sw.ElapsedMilliseconds < 1500)
        {
            KeyDown(editor, right ? Keys.Right : Keys.Left);
            right = !right;
            TypeChar(editor, 'x');
            KeyDown(editor, Keys.Back);
            editor.Invalidate();
            editor.Update();
        }
    }

    // ---- 入力と準備 ----

    /// <summary>文書を差し替え、行 <see cref="CaretLine"/> の先頭 + 5 にキャレットを置く(TopLine=0・選択なし)。</summary>
    private static void PrepareAtCaretLine(EditorControl editor, TextBuffer buffer)
    {
        editor.SetOrReplaceSource(buffer);
        editor.TopLine = 0;
        editor.SetCaretCharOffset(buffer.Current.GetLineStart(CaretLine) + 5);
        editor.Update();
        Check(editor.TopLine == 0, "キャレット行の準備でスクロールが起きた(窓が小さすぎる)");
        CheckFocus(editor);
    }

    /// <summary>フォーカスが無いと <c>PositionCaret</c> が早期 return し、キャレット配置の費用を測り落とす。</summary>
    private static void CheckFocus(EditorControl editor) =>
        Check(
            editor.Focused && editor.HasFocusCached,
            "EditorControl にフォーカスが無い(キャレット配置の費用を測り落とす)"
        );

    private static TextBuffer Fresh(string text) => TextBuffer.FromString(text);

    private static void KeyDown(EditorControl editor, Keys key)
    {
        var m = Message.Create(editor.Handle, WmKeyDown, (IntPtr)(int)key, (IntPtr)1);
        editor.__TestProcessMessage(ref m);
    }

    private static void TypeChar(EditorControl editor, char ch)
    {
        var m = Message.Create(editor.Handle, WmChar, (IntPtr)ch, (IntPtr)1);
        editor.__TestProcessMessage(ref m);
    }

    private static int VisibleRows(EditorControl editor) =>
        editor.LineHeightPx > 0 ? editor.ClientSize.Height / editor.LineHeightPx : 0;

    private static int Utf8Length(EditorControl editor)
    {
        var snap = editor.CurrentBuffer.Current;
        return Encoding.UTF8.GetByteCount(snap.GetText(0, snap.CharLength));
    }

    /// <summary>
    /// 調査記録 §9.5 の書式で <paramref name="lines"/> 行(CRLF 区切り・行番号 1 始まり)を作る。
    /// UTF-8 のバイト数が仕様と違えば例外=仕様からずれた文書を黙って測らない。
    /// </summary>
    private static string BuildDoc(string format, int lines, int expectedUtf8Bytes)
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= lines; i++)
            sb.Append(string.Format(CultureInfo.InvariantCulture, format, i)).Append("\r\n");
        string s = sb.ToString();
        int bytes = Encoding.UTF8.GetByteCount(s);
        if (bytes != expectedUtf8Bytes)
            throw new PerfSelfCheckException(
                $"生成した文書が {bytes:N0} バイト(仕様は {expectedUtf8Bytes:N0})"
            );
        return s;
    }

    private static void Check(bool ok, string message)
    {
        if (!ok)
            throw new PerfSelfCheckException(message);
    }

    // ---- 引数と出力 ----

    private static Options ParseArgs(string[] args)
    {
        var opt = new Options();
        for (int i = 1; i < args.Length; i++)
        {
            string next = i + 1 < args.Length ? args[i + 1] : "";
            switch (args[i])
            {
                case "--n" when int.TryParse(next, out int n) && n > 0:
                    opt.N = n;
                    i++;
                    break;
                case "--warmup" when int.TryParse(next, out int w) && w >= 0:
                    opt.Warmup = w;
                    i++;
                    break;
                case "--scenario" when next.Length > 0:
                    opt.Scenarios.Clear();
                    foreach (var s in next.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string id = s.Trim().ToUpperInvariant();
                        if (!AllScenarios.Contains(id))
                            throw new ArgumentException($"未知のシナリオ: {s}");
                        opt.Scenarios.Add(id);
                    }
                    if (opt.Scenarios.Count == 0)
                        throw new ArgumentException("--scenario が空");
                    i++;
                    break;
                case "--json" when next.Length > 0:
                    opt.JsonPath = next;
                    i++;
                    break;
                default:
                    throw new ArgumentException($"--perf の引数を解釈できない: {args[i]}");
            }
        }
        return opt;
    }

    private static void Print(List<Result> results)
    {
        Console.WriteLine();
        Console.WriteLine("id,doc,param,n,mean_ms,median_ms,p95_ms,min_ms,max_ms,paints_per_op");
        foreach (var r in results)
        {
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{r.Id},{r.Doc},{r.Param},{r.N},{r.MeanMs:F3},{r.MedianMs:F3},{r.P95Ms:F3},{r.MinMs:F3},{r.MaxMs:F3},{r.PaintsPerOp:F2}"
                )
            );
        }
    }
}

/// <summary><see cref="PerfBench"/> の自己チェックの失敗。値を出さずに EXIT 1 で終わる。</summary>
public sealed class PerfSelfCheckException : Exception
{
    public PerfSelfCheckException() { }

    public PerfSelfCheckException(string message)
        : base(message) { }

    public PerfSelfCheckException(string message, Exception innerException)
        : base(message, innerException) { }
}
