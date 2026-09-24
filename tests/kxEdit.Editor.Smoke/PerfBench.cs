using System.Diagnostics;
using System.Globalization;
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
/// 1 回として Stopwatch で測り、平均・中央値・p95 を出す。
/// </summary>
/// <remarks>
/// <para>
/// <b>判定はしない</b>(基準値の固定はデータがたまってから)。EXIT 1 になるのは
/// <b>自己チェックの失敗</b>=入力が効いておらず「何もしていない時間」を測っている場合だけ。
/// </para>
/// <para>
/// <b>測定条件</b>。(1) Form は<b>画面内</b>に置く。画面外の窓には <c>Update()</c> が WM_PAINT を
/// 配送しない(<see cref="GdiBench"/> と同じ理由)。(2) <c>editor.Focus()</c> が必須。
/// <c>PositionCaret</c> は <c>_hasFocus</c> で早期 return する(<see cref="WrapScrollBench"/> と同じ理由)。
/// (3) 外観は <c>ApplyAppearance(new AppSettings())</c>=製品の既定(ＭＳ ゴシック 12pt・全角表記)。
/// perf-harness(tools/perf-harness.ps1)も空のプロフィール=同じ既定で測るので条件が揃う。
/// </para>
/// <para>
/// <b>入力経路</b>。キーは <c>WM_KEYDOWN</c> / <c>WM_CHAR</c> を <c>__TestProcessMessage</c>(WndProc)に
/// 入れる=<c>OnKeyDown</c> → <c>InputRouter</c> / <c>OnKeyPress</c> → <c>InsertConfirmedText</c> の
/// 実経路を通る。IME と貼り付け片は既存のテストフック(<c>__TestApplyComposition</c> /
/// <c>__TestApplyResult</c>)を使う。SendInput と違い IME/TSF の費用(調査記録 §9 の S-2)は乗らない。
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

    /// <summary>S1〜S4・S6 の事前状態で置くキャレット行(調査記録 §9.5「Ctrl+Home の後に ↓ を 10 回」)。</summary>
    private const int CaretLine = 10;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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
    ];

    /// <summary>1 シナリオ × 1 文書の集計結果。<c>--json</c> の 1 要素にもなる。</summary>
    internal sealed record Result(
        string Id,
        string Doc,
        int N,
        double MeanMs,
        double MedianMs,
        double P95Ms,
        double MinMs,
        double MaxMs
    );

    private sealed class Options
    {
        public int N { get; set; } = 200;
        public int Warmup { get; set; } = 20;
        public HashSet<string> Scenarios { get; } = new(AllScenarios, StringComparer.Ordinal);
        public string? JsonPath { get; set; }
    }

    public static int Run(string[] args)
    {
        var opt = ParseArgs(args);
        var results = new List<Result>();

        ApplicationConfiguration.Initialize();
        using var form = new Form
        {
            Text = "kxEdit.Editor.Smoke --perf",
            Width = 900,
            Height = 700,
            StartPosition = FormStartPosition.Manual,
            // 画面外の窓には WM_PAINT が来ない=描画を測り落とす(GdiBench と同じ理由)。
            Location = new Point(100, 100),
            ShowInTaskbar = false,
        };
        using var editor = new EditorControl { Dock = DockStyle.Fill };
        form.Controls.Add(editor);
        form.Show(); // ハンドル生成(Show しないと Invalidate/Update が no-op)
        editor.ApplyAppearance(new AppSettings());
        editor.Focus();
        Application.DoEvents();

        Console.WriteLine(
            $"editor.Focused={editor.Focused}(false ならキャレット配置の費用を測り落とす)"
        );
        Console.WriteLine(
            $"editor.ClientSize={editor.ClientSize} / LineHeightPx={editor.LineHeightPx}"
                + $" / 可視行≈{VisibleRows(editor)} / n={opt.N} / warmup={opt.Warmup}"
        );

        try
        {
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
            File.WriteAllText(
                opt.JsonPath,
                JsonSerializer.Serialize(results, JsonOptions),
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

        var insert = new List<double>(opt.N);
        var back = new List<double>(opt.N);
        for (int k = 0; k < opt.Warmup + opt.N; k++)
        {
            double a = TimeOnce(editor, update: true, () => TypeChar(editor, 'x'));
            double b = TimeOnce(editor, update: true, () => KeyDown(editor, Keys.Back));
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
        return [Summarize("S3a", doc, insert), Summarize("S3b", doc, back)];
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

    /// <summary>S6a(1 行スクロール)・S6b(1 ページスクロール)。<c>TopLine</c> setter を ± で交互。</summary>
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

    /// <summary>S7: 全面再描画だけ(外から RedrawWindow したのと同じ量)。</summary>
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
        return Measure(editor, "S7", doc, opt.N, opt.Warmup, update: true, editor.Invalidate);
    }

    /// <summary>
    /// S8: UIA の <c>GetBoundingRectangles</c> を UI スレッドから直接呼ぶ(COM と Invoke を通らない)。
    /// 範囲は文書先頭から 1 行・40 行・1,000 行・全文。描画は起きないので <c>Update()</c> しない。
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
        // 全文 1 回で 3.5 秒級(調査記録 M-7)なので、行数の多い範囲は回数を絞る。
        var cases = new (string Id, int Lines, int N)[]
        {
            ("S8-1", 1, opt.N),
            ("S8-40", 40, opt.N),
            ("S8-1000", 1000, 10),
            ("S8-all", snap.LineCount, 3),
        };
        var results = new List<Result>();
        int visibleRects = -1;
        foreach (var (id, lines, n) in cases)
        {
            int end = lines >= snap.LineCount ? snap.CharLength : snap.GetLineStart(lines) - 2; // 行末の CRLF を含めない=ちょうど lines 行
            int rects = host.GetBoundingRectangles(0, end).Length / 4;
            // 1 行・40 行は範囲の行数ぶん(窓 900×700 で可視域は 40 行を超える)。1,000 行と全文は
            // 可視域で頭打ちになり、互いに同数(下端の部分行を含む)。
            if (lines <= 40)
                Check(rects == lines, $"{id}: 矩形数 {rects}(期待 {lines})");
            else if (visibleRects < 0)
                visibleRects = rects;
            else
                Check(
                    rects == visibleRects,
                    $"{id}: 矩形数 {rects}(1,000 行の範囲では {visibleRects})"
                );
            Check(rects >= 1, $"{id}: 矩形が返らない(可視域を測れていない)");
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
    /// S5(F-6): 空の新規文書に貼り付け片(約 10.6KB)を 1 回入れるたびに、末尾で <c>x</c> を 40 回打って
    /// 1 打鍵を測る。ブロック(64KB)内の書込位置に比例して重くなる「のこぎり型」を見る(調査記録 M-3)。
    /// Id は <c>S5@&lt;計測直前の UTF-8 バイト数&gt;</c>。
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
                TypeChar(editor, 'x');
            int bytes = Utf8Length(editor);
            int len0 = editor.CurrentBuffer.Current.CharLength;
            var samples = new List<double>(Keystrokes);
            for (int k = 0; k < Keystrokes; k++)
                samples.Add(TimeOnce(editor, update: true, () => TypeChar(editor, 'x')));
            Check(
                editor.CurrentBuffer.Current.CharLength == len0 + Keystrokes,
                $"S5: 打鍵で本文が伸びない(位置 {bytes})"
            );
            results.Add(Summarize($"S5@{bytes}", "append", samples));

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
    /// </summary>
    private static Result Measure(
        EditorControl editor,
        string id,
        string doc,
        int n,
        int warmup,
        bool update,
        Action op
    )
    {
        for (int k = 0; k < warmup; k++)
            TimeOnce(editor, update, op);
        var samples = new List<double>(n);
        for (int k = 0; k < n; k++)
            samples.Add(TimeOnce(editor, update, op));
        return Summarize(id, doc, samples);
    }

    private static double TimeOnce(EditorControl editor, bool update, Action op)
    {
        long t0 = Stopwatch.GetTimestamp();
        op();
        if (update)
            editor.Update(); // 同期 WM_PAINT
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    private static Result Summarize(string id, string doc, List<double> samples)
    {
        var sorted = samples.OrderBy(x => x).ToArray();
        int n = sorted.Length;
        double median = n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2.0;
        double p95 = sorted[Math.Max(0, (int)Math.Ceiling(n * 0.95) - 1)];
        return new Result(id, doc, n, sorted.Average(), median, p95, sorted[0], sorted[^1]);
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
    }

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
                        opt.Scenarios.Add(s.Trim().ToUpperInvariant());
                    i++;
                    break;
                case "--json" when next.Length > 0:
                    opt.JsonPath = next;
                    i++;
                    break;
                default:
                    throw new ArgumentException(
                        $"--perf の引数を解釈できない: {args[i]}(--n / --warmup / --scenario / --json)"
                    );
            }
        }
        return opt;
    }

    private static void Print(List<Result> results)
    {
        Console.WriteLine();
        Console.WriteLine("id,doc,n,mean_ms,median_ms,p95_ms,min_ms,max_ms");
        foreach (var r in results)
        {
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{r.Id},{r.Doc},{r.N},{r.MeanMs:F3},{r.MedianMs:F3},{r.P95Ms:F3},{r.MinMs:F3},{r.MaxMs:F3}"
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
