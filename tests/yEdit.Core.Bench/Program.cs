using System.Diagnostics;
using System.Text;
using yEdit.Core.Buffers;
using yEdit.Core.Editing;
using yEdit.Core.Layout;

// P1 TextBuffer 性能ゲート(設計書DoD): --mb <サイズ> 既定1024
// 目標未達があれば EXIT 1
// P2 Task 14: --layout 追加。TextBuffer ベンチ実行後、レイアウト層の性能ゲートを走らせる。
// P3 Task 14: --typing 追加。1M 文字を 1 文字ずつ Insert する応答性ベンチ(目標 5µs/挿入 以下)。
//             他モードとは排他=--typing 単独で早期 return(TextBuffer 合成文書構築は走らせない)。
// 文字アクセス seam Task 2: --characcess 追加。GetChar の位置依存コストと単語ナビを測る。
//             --typing と同様に単独で早期 return する。

int mb = 1024;
bool layoutMode = false;
bool typingMode = false;
bool charAccessMode = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--mb" && i + 1 < args.Length && int.TryParse(args[i + 1], out int m))
    {
        mb = m;
        i++;
    }
    else if (args[i] == "--layout")
    {
        layoutMode = true;
    }
    else if (args[i] == "--typing")
    {
        typingMode = true;
    }
    else if (args[i] == "--characcess")
    {
        charAccessMode = true;
    }
}

// ---- P3 Task 14: --typing 応答性ベンチ ----
// 1M 文字を 1 文字ずつ Insert(splice 経路の連続タイピング=coalescing による断片化抑制の
// 実測値)。目標 5s 以内(=5µs/insert 以下)。他ベンチとは独立=単独で return する
// (合成文書構築を挟むと目的の「純粋な入力応答性」が測れない)。
if (typingMode)
{
    Console.WriteLine("--typing: 1M 文字を 1 文字ずつ挿入する応答性ベンチ");
    var typingBuilder = new TextBufferBuilder();
    var typingBuf = typingBuilder.Build();
    // 事前ウォームアップ(JIT + キャッシュ暖め・計測外・10k タイプ)
    for (int w = 0; w < 10_000; w++)
        typingBuf.Insert(typingBuf.Current.CharLength, "a");
    var typingSw = Stopwatch.StartNew();
    for (int i = 0; i < 1_000_000; i++)
        typingBuf.Insert(typingBuf.Current.CharLength, "a");
    typingSw.Stop();
    double perInsertUs = typingSw.Elapsed.TotalMicroseconds / 1_000_000.0;
    int piecesAfterTyping = typingBuf.Current.PieceCount;
    Console.WriteLine(
        $"typing 1M: {typingSw.Elapsed.TotalSeconds:F3}s ({perInsertUs:F2}µs/insert・ピース数 {piecesAfterTyping})"
    );
    Console.WriteLine("目標: 5s 以内 (=5µs/insert 以下)");
    bool typingPass = typingSw.Elapsed.TotalSeconds < 5.0;
    Console.WriteLine(typingPass ? "PASS (EXIT 0)" : "FAIL (EXIT 1)");
    return typingPass ? 0 : 1;
}

// ---- 2026-07-31 文字アクセス seam: --characcess ----
// GetChar のコスト特性(格子セル内の位置で変わる)と、実操作 Ctrl+← 相当の
// WordBoundary.PrevWordStart を測る。DoD = 1M 文字 ASCII で PrevWordStart < 0.05 ms。
// 他ベンチとは独立=単独で return する。
if (charAccessMode)
{
    Console.WriteLine("--characcess: 文字アクセス(GetChar / 単語ナビ)ベンチ");

    static string MakeWordDoc(int chars, bool cjk)
    {
        var sb = new StringBuilder(chars);
        var r = new Random(20260731);
        while (sb.Length < chars)
        {
            int wordLen = r.Next(2, 9);
            for (int i = 0; i < wordLen; i++)
                sb.Append(cjk ? (char)('あ' + r.Next(40)) : (char)('a' + r.Next(26)));
            sb.Append(r.Next(12) == 0 ? '\n' : ' ');
        }
        return sb.ToString(0, chars);
    }

    var caResults = new List<(string Name, string Value, string Target, bool? Pass)>();
    long caSink = 0;

    // --- GetChar 単発(位置依存を見る) ---
    var gcSnap = TextBuffer.FromString(MakeWordDoc(1_000_000, cjk: false)).Current;
    foreach (int probe in new[] { 0, gcSnap.CharLength / 2, gcSnap.CharLength - 10 })
    {
        for (int w = 0; w < 1000; w++)
            caSink += gcSnap.GetChar(probe); // ウォームアップ
        const int GcIters = 20_000;
        var gcSw = Stopwatch.StartNew();
        for (int i = 0; i < GcIters; i++)
            caSink += gcSnap.GetChar(probe);
        gcSw.Stop();
        double gcNs = gcSw.Elapsed.TotalNanoseconds / GcIters;
        caResults.Add(($"C1 GetChar(pos={probe})", $"{gcNs:N0} ns/回", "記録のみ", null));
    }

    // --- Ctrl+← 相当(DoD 判定はこれ) ---
    foreach (bool cjk in new[] { false, true })
    {
        foreach (int chars in new[] { 10_000, 200_000, 1_000_000 })
        {
            var wbSnap = TextBuffer.FromString(MakeWordDoc(chars, cjk)).Current;
            int mid = wbSnap.CharLength / 2;
            for (int w = 0; w < 50; w++)
                caSink += WordBoundary.PrevWordStart(wbSnap, mid + w); // ウォームアップ
            const int WbIters = 300;
            var wbSw = Stopwatch.StartNew();
            for (int i = 0; i < WbIters; i++)
                caSink += WordBoundary.PrevWordStart(wbSnap, mid + i);
            wbSw.Stop();
            double wbMs = wbSw.Elapsed.TotalMilliseconds / WbIters;
            bool isDod = !cjk && chars == 1_000_000;
            caResults.Add(
                (
                    $"C2 PrevWordStart({(cjk ? "CJK" : "ASCII")} {chars:N0})",
                    $"{wbMs:F3} ms/回",
                    isDod ? "<0.05ms (DoD)" : "記録のみ",
                    isDod ? wbMs < 0.05 : null
                )
            );
        }
    }

    Console.WriteLine();
    Console.WriteLine("| # シナリオ | 結果 | 目標 | 判定 |");
    Console.WriteLine("|---|---|---|---|");
    foreach (var r in caResults)
        Console.WriteLine(
            $"| {r.Name} | {r.Value} | {r.Target} | {(r.Pass is null ? "―" : r.Pass.Value ? "PASS" : "FAIL")} |"
        );
    Console.WriteLine($"(sink={caSink})");
    // 空振り PASS の防止: All は空集合で true を返すため、判定行(Pass 非 null)が 1 つも
    // 無いと「測っていないのに合格」になる。シナリオ配列から DoD 条件
    // (ASCII 1,000,000)を外すと黙って通ってしまうので、判定行の存在自体をゲートに含める。
    int caJudgedRows = caResults.Count(r => r.Pass is not null);
    if (caJudgedRows == 0)
        Console.WriteLine("DoD 判定行が 0 件(シナリオ設定に DoD 条件が含まれていない=ゲート無効)");
    bool caPass = caJudgedRows > 0 && caResults.All(r => r.Pass is not false);
    Console.WriteLine(caPass ? "DoD 達成 (EXIT 0)" : "DoD 未達 (EXIT 1)");
    return caPass ? 0 : 1;
}

long targetBytes = (long)mb * 1024 * 1024;
var rnd = new Random(20260705);
var results = new List<(string Name, string Value, string Target, bool? Pass)>();
long sink = 0; // 最適化防止

Console.WriteLine($"TextBuffer ベンチ開始 (--mb {mb})");

// ---- 1) 合成文書構築(日本語+ASCII+改行混合) ----
const string TemplateLine =
    "The quick brown fox jumps over 0123456789.\r\n日本語の行テキスト、あいうえお漢字カナ混在の内容です。\nもう一行😀絵文字と🈴記号付き\r\n";
byte[] template = Encoding.UTF8.GetBytes(TemplateLine);
byte[] block;
{
    var b = new byte[1 << 20];
    int w = 0;
    while (w + template.Length <= b.Length)
    {
        template.CopyTo(b, w);
        w += template.Length;
    }
    block = b[..w]; // テンプレ整数個(コード点/改行を割らない)
}

var swBuild = Stopwatch.StartNew();
var builder = new TextBufferBuilder();
long written = 0;
while (written < targetBytes)
{
    int len = (int)Math.Min(block.Length, targetBytes - written);
    builder.Add(block.AsSpan(0, len));
    written += len;
}
var buffer = builder.Build();
swBuild.Stop();

var snap = buffer.Current;
int charLen = snap.CharLength;
int lineCount = snap.LineCount;
int initialPieces = snap.PieceCount;
results.Add(
    (
        "1 構築",
        $"{swBuild.Elapsed.TotalSeconds:F1}s / {charLen:N0}文字 / {lineCount:N0}行 / {initialPieces}ピース",
        "記録のみ",
        null
    )
);

// ---- 8) メモリ(構築直後) ----
long managed = GC.GetTotalMemory(forceFullCollection: true);
long workingSet = Environment.WorkingSet;
results.Add(
    (
        "8 メモリ",
        $"managed {managed / 1048576.0:F0}MB / WorkingSet {workingSet / 1048576.0:F0}MB(文書 {mb}MB)",
        "記録のみ(文書+O(ピース))",
        null
    )
);

// ---- 3) Current 取得 1,000,000回(スナップショットO(1)実証) ----
for (int i = 0; i < 10_000; i++)
    sink += buffer.Current.CharLength; // ウォームアップ
const int CurrentIters = 1_000_000;
var sw = Stopwatch.StartNew();
for (int i = 0; i < CurrentIters; i++)
    sink += buffer.Current.CharLength;
sw.Stop();
double currentNs = TicksToNs(sw.ElapsedTicks) / CurrentIters;
AddResult("3 Current取得", $"{currentNs:F1} ns/回", "O(1)(<1µs)", currentNs < 1000);

// ---- 4) ランダム行 → GetLineStart 100,000回 ----
const int QueryIters = 100_000;
for (int i = 0; i < 1000; i++)
    sink += snap.GetLineStart(rnd.Next(lineCount));
sw.Restart();
for (int i = 0; i < QueryIters; i++)
    sink += snap.GetLineStart(rnd.Next(lineCount));
sw.Stop();
double lineStartUs = TicksToUs(sw.ElapsedTicks) / QueryIters;
AddResult("4 GetLineStart", $"{lineStartUs:F1} µs/回", "平均<100µs", lineStartUs < 100);

// ---- 5) ランダムpos → GetLineIndexOfChar 100,000回 ----
for (int i = 0; i < 1000; i++)
    sink += snap.GetLineIndexOfChar(rnd.Next(charLen + 1));
sw.Restart();
for (int i = 0; i < QueryIters; i++)
    sink += snap.GetLineIndexOfChar(rnd.Next(charLen + 1));
sw.Stop();
double lineIdxUs = TicksToUs(sw.ElapsedTicks) / QueryIters;
AddResult("5 GetLineIndexOfChar", $"{lineIdxUs:F1} µs/回", "平均<100µs", lineIdxUs < 100);

// ---- 6) ランダム窓 GetText(pos, 200) 100,000回 ----
for (int i = 0; i < 1000; i++)
    sink += snap.GetText(rnd.Next(charLen - 200), 200).Length;
sw.Restart();
for (int i = 0; i < QueryIters; i++)
    sink += snap.GetText(rnd.Next(charLen - 200), 200).Length;
sw.Stop();
double getTextUs = TicksToUs(sw.ElapsedTicks) / QueryIters;
AddResult("6 GetText(200)", $"{getTextUs:F1} µs/回", "平均<100µs", getTextUs < 100);

// ---- 2) ランダム位置 splice 10,000回(タイプ相当1〜3文字) ----
string[] typing = ["a", "あ", "xy", "漢字a", "e"];
for (int i = 0; i < 200; i++) // ウォームアップ
    buffer.Insert(rnd.Next(buffer.Current.CharLength + 1), typing[rnd.Next(typing.Length)]);
const int SpliceIters = 10_000;
var spliceTicks = new long[SpliceIters];
for (int i = 0; i < SpliceIters; i++)
{
    int pos = rnd.Next(buffer.Current.CharLength + 1);
    string s = typing[rnd.Next(typing.Length)];
    long t0 = Stopwatch.GetTimestamp();
    buffer.Insert(pos, s);
    spliceTicks[i] = Stopwatch.GetTimestamp() - t0;
}
Array.Sort(spliceTicks);
double spliceAvgMs = TicksToMs(spliceTicks.Sum()) / SpliceIters;
double spliceP99Ms = TicksToMs(spliceTicks[(int)(SpliceIters * 0.99)]);
AddResult(
    "2 splice 10,000回",
    $"平均 {spliceAvgMs * 1000:F1} µs / p99 {spliceP99Ms * 1000:F1} µs",
    "平均<1ms かつ p99<1ms",
    spliceAvgMs < 1.0 && spliceP99Ms < 1.0
);

// ---- 7) 連続タイピング10,000字後の PieceCount ----
buffer.BreakUndoCoalescing();
int piecesBefore = buffer.Current.PieceCount;
int caret = buffer.Current.CharLength / 2;
if (caret > 0 && char.IsLowSurrogate(buffer.Current.GetChar(caret)))
    caret--;
for (int i = 0; i < 10_000; i++)
{
    buffer.Insert(caret, "a");
    caret++;
}
int piecesAfter = buffer.Current.PieceCount;
int pieceDelta = piecesAfter - piecesBefore;
AddResult(
    "7 連続タイピング断片化",
    $"before {piecesBefore} → after {piecesAfter}(Δ{pieceDelta})",
    "Δ≤50(断片化しない)",
    pieceDelta <= 50
);

// ---- P2 Task 14: レイアウトベンチ(--layout 指定時のみ) ----
// 純レイアウトの決定的ベンチ。MonoCharMetrics(半角=1px・全角=2px・行高=10px)を使い、
// フォント/OS 依存を排して 1000 回の合計を測る。EditorControl は経由しない(GDI ベンチは smoke 側)。
//
// **snapshot は splice/typing 前の `snap`(構築直後・ピース数=構築時のまま)を使う**。
// TextBuffer は immutable snapshot なので `snap` はここに来ても構築直後の状態を保持している
// (Current=最新なら splice 後の 2 万ピースの重い木を歩くことになり、実運用の初期ロード直後
// フレームコストの見積もりから外れる=Task 14 DoD の趣旨と合わない)。
if (layoutMode)
{
    // 構築直後のヒープ量を記録(9 メモリで delta を出すため)
    long memBeforeLayout = GC.GetTotalMemory(forceFullCollection: true);

    var layoutSnap = snap; // 構築直後スナップショット(splice 前)
    var metrics = new MonoCharMetrics(halfWidthPx: 1, lineHeightPx: 10);
    const int LayoutIters = 1000;
    const int VisibleRowsTarget = 50;
    int heightPx = VisibleRowsTarget * metrics.LineHeightPx; // = 500
    int lineCountForRnd = layoutSnap.LineCount;
    var layoutStyle = BuildLayoutBenchStyle();

    // TopLine の乱数列(全シナリオで同じ列を使うため事前生成=決定的比較のため)
    int[] topLines = new int[LayoutIters];
    for (int i = 0; i < LayoutIters; i++)
        topLines[i] = rnd.Next(0, Math.Max(1, lineCountForRnd));

    // ---- L1) 折り返し OFF: ViewportLayout.Build 1000 回 ----
    // ウォームアップ(JIT + キャッシュ暖め・計測外)
    for (int w = 0; w < 32; w++)
    {
        var _ = ViewportLayout.Build(layoutSnap, topLines[w % LayoutIters], heightPx, 0, metrics);
        sink += _.Count;
    }
    sw.Restart();
    for (int i = 0; i < LayoutIters; i++)
    {
        var rows = ViewportLayout.Build(layoutSnap, topLines[i], heightPx, 0, metrics);
        sink += rows.Count;
    }
    sw.Stop();
    double buildOffMs = TicksToMs(sw.ElapsedTicks) / LayoutIters;
    AddResult(
        "L2 ViewportLayout(wrap OFF)",
        $"{buildOffMs:F2} ms/回",
        "平均<16ms",
        buildOffMs < 16
    );

    // ---- L2) 折り返し ON(WrapColumns=80): ViewportLayout.Build 1000 回 ----
    for (int w = 0; w < 32; w++)
    {
        var _ = ViewportLayout.Build(layoutSnap, topLines[w % LayoutIters], heightPx, 80, metrics);
        sink += _.Count;
    }
    sw.Restart();
    for (int i = 0; i < LayoutIters; i++)
    {
        var rows = ViewportLayout.Build(layoutSnap, topLines[i], heightPx, 80, metrics);
        sink += rows.Count;
    }
    sw.Stop();
    double buildOnMs = TicksToMs(sw.ElapsedTicks) / LayoutIters;
    AddResult(
        "L3 ViewportLayout(wrap ON 80)",
        $"{buildOnMs:F2} ms/回",
        "平均<16ms",
        buildOnMs < 16
    );

    // ---- L3) ViewportLayout → FrameBuilder 1 フレーム全体 1000 回(wrap OFF) ----
    // 実描画の代表シナリオ(装飾なし・現在行なし)を測る。装飾ありは可視性次第で
    // 分岐しないので、装飾なしフレームの時間 <= 装飾ありフレームの時間 とはならないが、
    // 主要ホットパス(GetText × 可視視覚行数)を測る目的に合致する。
    for (int w = 0; w < 32; w++)
    {
        var rows = ViewportLayout.Build(
            layoutSnap,
            topLines[w % LayoutIters],
            heightPx,
            0,
            metrics
        );
        var frame = FrameBuilder.Build(
            layoutSnap,
            rows,
            clientWidth: 800,
            clientHeight: heightPx,
            lineNumberMarginPx: 0,
            currentLineLogical: -1,
            selection: null,
            cellHighlight: null,
            showWhitespace: false,
            style: layoutStyle,
            metrics: metrics
        );
        sink += frame.Ops.Count;
    }
    sw.Restart();
    for (int i = 0; i < LayoutIters; i++)
    {
        var rows = ViewportLayout.Build(layoutSnap, topLines[i], heightPx, 0, metrics);
        var frame = FrameBuilder.Build(
            layoutSnap,
            rows,
            clientWidth: 800,
            clientHeight: heightPx,
            lineNumberMarginPx: 0,
            currentLineLogical: -1,
            selection: null,
            cellHighlight: null,
            showWhitespace: false,
            style: layoutStyle,
            metrics: metrics
        );
        sink += frame.Ops.Count;
    }
    sw.Stop();
    double frameMs = TicksToMs(sw.ElapsedTicks) / LayoutIters;
    AddResult("L4 Frame(wrap OFF 全体)", $"{frameMs:F2} ms/回", "平均<16ms", frameMs < 16);

    // ---- L5) PixelMapper.OffsetToPx 相当計算 1000 回 ----
    // 代表 1 セグメント(可視 1 行目相当・平均行長)を対象に、末尾位置までの OffsetToPx を測る。
    // 空行ばかりに当たると偏るので topLine=0 の先頭視覚行を使う。
    var probeRows = ViewportLayout.Build(layoutSnap, 0, heightPx, 0, metrics);
    string probeText =
        probeRows.Count > 0 && probeRows[0].SegmentLength > 0
            ? layoutSnap.GetText(probeRows[0].SegmentStartChar, probeRows[0].SegmentLength)
            : "The quick brown fox jumps over 0123456789.";
    for (int w = 0; w < 100; w++)
        sink += PixelMapper.OffsetToPx(probeText.AsSpan(), probeText.Length, metrics);
    sw.Restart();
    for (int i = 0; i < LayoutIters; i++)
        sink += PixelMapper.OffsetToPx(probeText.AsSpan(), probeText.Length, metrics);
    sw.Stop();
    double pxMs = TicksToMs(sw.ElapsedTicks) / LayoutIters;
    AddResult("L5 PixelMapper.OffsetToPx", $"{pxMs * 1000:F3} µs/回", "平均<1ms", pxMs < 1.0);

    // ---- L6) メモリ増分(構築後→レイアウト後) ----
    long memAfterLayout = GC.GetTotalMemory(forceFullCollection: true);
    long deltaMB = (memAfterLayout - memBeforeLayout) / 1048576;
    results.Add(("L6 メモリ増分(layout)", $"Δ managed {deltaMB} MB", "記録のみ", null));
}

// ---- 結果表 ----
Console.WriteLine();
Console.WriteLine("| # シナリオ | 結果 | 目標 | 判定 |");
Console.WriteLine("|---|---|---|---|");
foreach (var r in results.OrderBy(r => r.Name))
    Console.WriteLine(
        $"| {r.Name} | {r.Value} | {r.Target} | {(r.Pass is null ? "―" : r.Pass.Value ? "PASS" : "FAIL")} |"
    );
Console.WriteLine($"(sink={sink})");

bool allPass = results.All(r => r.Pass is not false);
Console.WriteLine(allPass ? "全シナリオ目標達成 (EXIT 0)" : "目標未達あり (EXIT 1)");
return allPass ? 0 : 1;

void AddResult(string name, string value, string target, bool pass) =>
    results.Add((name, value, target, pass));

static double TicksToNs(long ticks) => ticks * 1_000_000_000.0 / Stopwatch.Frequency;
static double TicksToUs(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;
static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

// レイアウトベンチ用のダミー ViewportStyle(色は結果に影響しない=OpKind 数だけが計測対象)
static ViewportStyle BuildLayoutBenchStyle() =>
    new(
        Foreground: new PaintColor(0x000000),
        Background: new PaintColor(0xFFFFFF),
        CurrentLineBack: new PaintColor(0xF0F0F0),
        SelectionBack: new PaintColor(0xADD8E6),
        LineNumberFore: new PaintColor(0x777777),
        HighlightOutline: new PaintColor(0xD77800),
        WhitespaceGlyph: new PaintColor(0xCCCCCC)
    );
