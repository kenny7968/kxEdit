using System.Diagnostics;
using System.Text;
using kxEdit.Core.Buffers;
using kxEdit.Core.Editing;
using kxEdit.Core.Layout;
using kxEdit.Core.Text;

// P1 TextBuffer 性能ゲート(設計書DoD): --mb <サイズ> 既定1024
// 目標未達があれば EXIT 1
// P2 Task 14: --layout 追加。TextBuffer ベンチ実行後、レイアウト層の性能ゲートを走らせる。
// P3 Task 14: --typing 追加。1M 文字を 1 文字ずつ Insert する応答性ベンチ(目標 5µs/挿入 以下)。
//             他モードとは排他=--typing 単独で早期 return(TextBuffer 合成文書構築は走らせない)。
// 文字アクセス seam Task 2: --characcess 追加。GetChar の位置依存コストと単語ナビを測る。
//             --typing と同様に単独で早期 return する。
// 巨大 1 行調査 Task 2: --largeline 追加。空白・改行を一切含まない単一長大行に対する
//             ViewportLayout の構造コストを GDI 抜きで測る(GDI 込みは Editor.Smoke --largeline)。
//             単独で早期 return する。

int mb = 1024;
bool layoutMode = false;
bool typingMode = false;
bool charAccessMode = false;
bool largeLineMode = false;
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
    else if (args[i] == "--largeline")
    {
        largeLineMode = true;
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
// WordBoundary.PrevWordStart を測る。
// DoD = 1M 文字 ASCII で PrevWordStart の<中央値>が 0.05 ms 未満。
// 中央値なのは、コストが格子セル内オフセットにほぼ比例する=測定位置 1 点では
// 運で 5 倍以上変わるため(Task 4 レビューでの発見)。最悪値も併記するが判定には使わない
// (外れ値に振り回されるため)。他ベンチとは独立=単独で return する。
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
    int grid = TextChunk.DefaultGridBytes;
    Console.WriteLine($"格子幅(TextChunk 既定): {grid:N0} B");

    // CharToByte は「直近格子点から目標位置まで 1 バイトずつ前進」する。したがって
    // 文字アクセスのコストは文書サイズではなく「格子セル内オフセット」にほぼ比例する。
    // ASCII 文書は 1 バイト = 1 文字なので pos % grid がそのまま走査バイト数になる。

    double MeasureGetCharNs(TextSnapshot s, int pos)
    {
        for (int w = 0; w < 1000; w++)
            caSink += s.GetChar(pos); // ウォームアップ
        const int GcIters = 20_000;
        double best = double.MaxValue; // 3 ラウンドの最小値(GC / 周波数変動の外れ値を落とす)
        for (int round = 0; round < 3; round++)
        {
            var gcSw = Stopwatch.StartNew();
            for (int i = 0; i < GcIters; i++)
                caSink += s.GetChar(pos);
            gcSw.Stop();
            best = Math.Min(best, gcSw.Elapsed.TotalNanoseconds / GcIters);
        }
        return best;
    }

    // --- C1 GetChar 単発: セル内オフセット依存を明示する ---
    var gcSnap = TextBuffer.FromString(MakeWordDoc(1_000_000, cjk: false)).Current;
    int gcBase = 500_000 / grid * grid; // 格子点ちょうど
    caResults.Add(
        (
            "C1 GetChar(pos=0・ピース先頭 fast path)",
            $"{MeasureGetCharNs(gcSnap, 0):N0} ns/回",
            "記録のみ",
            null
        )
    );
    foreach (int off in new[] { 0, grid / 4, grid / 2, 3 * grid / 4, grid - 1 })
        caResults.Add(
            (
                $"C1 GetChar(格子点 {gcBase:N0} + {off:N0} B)",
                $"{MeasureGetCharNs(gcSnap, gcBase + off):N0} ns/回",
                "記録のみ",
                null
            )
        );

    // --- C2 Ctrl+← 相当(DoD 判定はこれ) ---
    // 単一の開始位置で測ると、その位置のセル内オフセットの運で結果が変わる
    // (4KB 格子ではセル先頭付近とセル末尾で 5 倍以上開く)。DoD の意図は
    // 「Ctrl+← が速い」であって「ある 1 点で速い」ではないので、開始位置を
    // 決定的な乱択で散らしてセル全域を踏ませ、中央値でゲートし最悪値も必ず出す。
    (double Median, double Worst) SweepPrevWordStart(TextSnapshot s)
    {
        const int Samples = 256; // 位置数(セル内オフセットが偏らないだけ取る)
        const int Repeats = 3; // 位置ごとに最小値=OS 由来の外れ値を落として位置の素コストを見る
        var rng = new Random(20260731); // 決定的(既存ベンチの流儀)
        int lo = Math.Min(1000, s.CharLength / 4);
        var positions = new int[Samples];
        for (int i = 0; i < Samples; i++)
            positions[i] = rng.Next(lo, s.CharLength);
        for (int w = 0; w < 200; w++)
            caSink += WordBoundary.PrevWordStart(
                s,
                positions[w % Samples],
                WordBoundary.NoScanLimit
            ); // ウォームアップ

        var ms = new double[Samples];
        for (int i = 0; i < Samples; i++)
        {
            double best = double.MaxValue;
            for (int r = 0; r < Repeats; r++)
            {
                var sw = Stopwatch.StartNew();
                caSink += WordBoundary.PrevWordStart(s, positions[i], WordBoundary.NoScanLimit);
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            ms[i] = best;
        }
        Array.Sort(ms);
        return (ms[Samples / 2], ms[^1]);
    }

    foreach (bool cjk in new[] { false, true })
    {
        foreach (int chars in new[] { 10_000, 200_000, 1_000_000 })
        {
            var wbSnap = TextBuffer.FromString(MakeWordDoc(chars, cjk)).Current;
            var (median, worst) = SweepPrevWordStart(wbSnap);
            bool isDod = !cjk && chars == 1_000_000;
            caResults.Add(
                (
                    $"C2 PrevWordStart({(cjk ? "CJK" : "ASCII")} {chars:N0})",
                    $"中央値 {median:F3} / 最悪 {worst:F3} ms/回",
                    isDod ? "中央値 <0.05ms (DoD)" : "記録のみ",
                    isDod ? median < 0.05 : null
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

// ---- 2026-08-02 巨大 1 行調査: --largeline ----
// 空白・改行を一切含まない単一長大行の構造コスト。MonoCharMetrics(固定幅)を使って
// GDI を経路から外すことで、非線形性が「構造由来」か「GDI 由来」かを切り分ける
// (GDI 込みは tests/kxEdit.Editor.Smoke --largeline が対)。
// 判定ゲートは持たない=調査用のため常に EXIT 0。他ベンチとは独立=単独で return する。
if (largeLineMode)
{
    Console.WriteLine("--largeline: 巨大 1 行の構造コスト(MonoCharMetrics・GDI 抜き)");

    // 空白も改行も含まない 1 行を作る。文字種を振るのは GdiCharMetrics.MeasureRun が
    // 「全 ASCII なら配列加算・非 ASCII を 1 文字でも含むと GDI へ落ちる」ためで、
    // 本ベンチ自体は MonoCharMetrics なので文字種で分岐しない=構造コストの基準線になる。
    // ただし MonoCharMetrics はサロゲートペアに専用分岐を持つため emoji は構造側でも意味がある。
    //
    // kind ごとの狙い(Editor.Smoke --largeline の MakeSingleLine と同一の生成規則・同一シード。
    //  2 つのベンチが対であることが設計の前提なので、片方だけ変えないこと):
    //   ascii   … a-z の 26 種。基準線
    //   cjk     … U+3042 から 40 種。異なるコードポイントが 40 種しかないため、
    //             幅メモ化(設計書 §4.1 変更 A)の初回コストが実文書より小さく出る点に注意
    //   mixed   … ascii と cjk を半々
    //   cjkwide … CJK 統合漢字 U+4E00〜U+55FF の 2,048 種。日本語の実文書に近い文字種数
    //   emoji   … U+1F600〜U+1F64F の 80 種=サロゲートペア。1 コードポイント = 2 char なので
    //             chars は char 数であってコードポイント数ではない(末尾がペアの中間で切れうるが、
    //             LineLayout は単独サロゲートを 1 code-unit として扱う契約なので測定は成立する)
    //
    // ascii / cjk / mixed の生成規則は変更しないこと(調査 §2.3 との前後比較が壊れる)。
    static string MakeSingleLine(int chars, string kind)
    {
        var sb = new StringBuilder(chars);
        var r = new Random(20260802); // 決定的(既存ベンチの流儀)
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
                case "cjkwide":
                    sb.Append((char)(0x4E00 + r.Next(2048))); // U+4E00〜U+55FF
                    break;
                case "emoji":
                    // 1 回で 2 char(サロゲートペア)積む。U+1F600〜U+1F64F。
                    sb.Append(char.ConvertFromUtf32(0x1F600 + r.Next(80)));
                    break;
                default: // "mixed"
                    sb.Append(r.Next(2) == 0 ? (char)('a' + r.Next(26)) : (char)('あ' + r.Next(40)));
                    break;
            }
        }
        return sb.ToString(0, chars);
    }

    var llMetrics = new MonoCharMetrics(halfWidthPx: 8, lineHeightPx: 20);
    int llHeightPx = 40 * llMetrics.LineHeightPx; // 可視 40 行相当

    long llSink = 0; // 最適化防止(既存ベンチの流儀)
    Console.WriteLine();
    Console.WriteLine("kind,chars,wrapColumns,buildMs,rows");
    foreach (string kind in new[] { "ascii", "cjk", "mixed", "cjkwide", "emoji" })
    {
        foreach (int len in new[] { 100_000, 500_000, 2_000_000 })
        {
            var llSnap = TextBuffer.FromString(MakeSingleLine(len, kind)).Current;
            foreach (int wrap in new[] { 0, 80 })
            {
                llSink += ViewportLayout
                    .Build(llSnap, 0, topSegment: 0, llHeightPx, wrap, llMetrics)
                    .Count; // ウォームアップ(JIT)
                var llSw = Stopwatch.StartNew();
                var llRows = ViewportLayout.Build(
                    llSnap,
                    0,
                    topSegment: 0,
                    llHeightPx,
                    wrap,
                    llMetrics
                );
                llSw.Stop();
                Console.WriteLine(
                    $"{kind},{len},{wrap},{llSw.Elapsed.TotalMilliseconds:F1},{llRows.Count}"
                );
            }
        }
    }

    // --- ファイル読み込み経路(TextFileService.LoadAsBufferAuto) ---
    // Smoke ベンチは TextBuffer.FromString で文書を作るため、実際の「ファイルを開く」の
    // 前半=読み込み・エンコーディング判定・行末検出を測っていない。長大 1 行でここが
    // 非線形なら描画とは別の主因になりうるので潰しておく。
    Console.WriteLine();
    Console.WriteLine("ファイル読み込み経路(LoadAsBufferAuto)");
    Console.WriteLine("kind,chars,fileBytes,loadMs");
    foreach (string loadKind in new[] { "ascii", "cjk" })
    {
        foreach (int len in new[] { 100_000, 500_000, 2_000_000 })
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"kxedit-largeline-{loadKind}-{len}.txt");
#pragma warning disable S6966 // reason: ベンチの top-level 非 async パス。計測対象は Load 側であり、fixture 書き出しの非同期化は測定に無関係
            File.WriteAllText(tmp, MakeSingleLine(len, loadKind), new UTF8Encoding(false));
#pragma warning restore S6966
            try
            {
                long fileBytes = new FileInfo(tmp).Length;
                var loadSw = Stopwatch.StartNew();
                var loaded = TextFileService.LoadAsBufferAuto(tmp);
                loadSw.Stop();
                llSink += loaded.Buffer.Current.CharLength;
                Console.WriteLine(
                    $"{loadKind},{len},{fileBytes},{loadSw.Elapsed.TotalMilliseconds:F1}"
                );
            }
            finally
            {
                File.Delete(tmp);
            }
        }
    }

    // --- F-5 の実測: 空白・改行を含まない長大トークンでの単語ナビ ---
    // 既存 --characcess は MakeWordDoc(空白・改行あり)を使うため、区切りが無い場合の
    // 最悪ケースを測っていない。上限なし(NoScanLimit)の WordBoundary.PrevWordStart は
    // 同一クラスの連続を 1 文字ずつ走査するので、ascii 一色だと行頭まで全走査する。
    // 同じ構造は UIA の単語スパンにもある: TextRangeProviderV2.ExpandToEnclosingUnit(
    // TextUnit.Word) が WordStart と WordEnd を両方呼ぶため、SR の単語単位読み 1 回あたりの
    // コストは概ねこの 2 倍になる。
    // 2026-08-04: 本番 3 経路(SR の読み上げ / Ctrl+←→ / ダブルクリック単語選択)は
    // WordBoundary.DefaultMaxScan を渡すようになったので、ここで測っているのは
    // 「上限を入れなかった場合」の反実仮想である(上限つきの実測と cap 掃引は
    // kxEdit.Editor.Smoke --wordunit 側。docs/plans/2026-08-04-uia-word-unit-fix.md Task 6)。
    Console.WriteLine();
    Console.WriteLine("F-5: 空白なし長大トークンの単語ナビ");
    Console.WriteLine("chars,prevWordStartMs");
    foreach (int len in new[] { 100_000, 500_000, 2_000_000 })
    {
        var tokSnap = TextBuffer.FromString(MakeSingleLine(len, "ascii")).Current;
        llSink += WordBoundary.PrevWordStart(tokSnap, tokSnap.CharLength, WordBoundary.NoScanLimit); // ウォームアップ
        double tokBest = double.MaxValue; // 3 回の最小値(既存 --characcess の流儀)
        for (int r = 0; r < 3; r++)
        {
            var tokSw = Stopwatch.StartNew();
            llSink += WordBoundary.PrevWordStart(
                tokSnap,
                tokSnap.CharLength,
                WordBoundary.NoScanLimit
            );
            tokSw.Stop();
            tokBest = Math.Min(tokBest, tokSw.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"{len},{tokBest:F1}");
    }

    // --- F-6 の空白埋め: AppendBuffer 現ブロック経路 ---
    // 既存 --characcess は TextBuffer.FromString(builder チャンク)だけを測っており、
    // 「実際にタイプして育てた文書」= AppendBuffer の現ブロックに一度も触れていない。
    // 同クラスの共有チャンクは格子表を先頭 1 エントリに固定しているため(設計書 §2.4)、
    // CharToByte がブロック先頭からの線形走査になる=最大 64KB 走査が残る領域。
    Console.WriteLine();
    Console.WriteLine("F-6: AppendBuffer 現ブロック経路(タイプして育てた文書)");
    var typedBuf = new TextBufferBuilder().Build();
    var typedRnd = new Random(20260802);
    // 64KB ブロックをまたぐ長さにする。単語区切りを入れて PrevWordStart が動くようにする。
    for (int i = 0; i < 70_000; i++)
    {
        typedBuf.Insert(
            typedBuf.Current.CharLength,
            typedRnd.Next(8) == 0 ? " " : ((char)('a' + typedRnd.Next(26))).ToString()
        );
    }
    var typedSnap = typedBuf.Current;
    Console.WriteLine(
        $"typed 文書: {typedSnap.CharLength:N0} 文字 / ピース数 {typedSnap.PieceCount}"
    );
    long f6Sink = 0;
    foreach (int frac in new[] { 1, 2, 4 })
    {
        int pos = typedSnap.CharLength * (frac - 1) / 4 + typedSnap.CharLength / 8;
        for (int w = 0; w < 1_000; w++)
            f6Sink += typedSnap.GetChar(pos); // ウォームアップ
        const int F6Iters = 20_000;
        var f6Sw = Stopwatch.StartNew();
        for (int i = 0; i < F6Iters; i++)
            f6Sink += typedSnap.GetChar(pos);
        f6Sw.Stop();
        Console.WriteLine(
            $"  GetChar(pos={pos:N0}): {f6Sw.Elapsed.TotalNanoseconds / F6Iters:N0} ns/回"
        );
    }
    for (int w = 0; w < 50; w++)
        f6Sink += WordBoundary.PrevWordStart(
            typedSnap,
            typedSnap.CharLength - 1 - w,
            WordBoundary.NoScanLimit
        ); // ウォームアップ
    var f6NavSw = Stopwatch.StartNew();
    for (int i = 0; i < 200; i++)
        f6Sink += WordBoundary.PrevWordStart(
            typedSnap,
            typedSnap.CharLength - 1 - i * 10,
            WordBoundary.NoScanLimit
        );
    f6NavSw.Stop();
    Console.WriteLine($"  PrevWordStart × 200: {f6NavSw.Elapsed.TotalMilliseconds / 200:F4} ms/回");

    Console.WriteLine($"(sink={llSink + f6Sink})");
    Console.WriteLine();
    Console.WriteLine("(調査用ベンチのため判定ゲートなし) EXIT 0");
    return 0;
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
        var _ = ViewportLayout.Build(
            layoutSnap,
            topLines[w % LayoutIters],
            topSegment: 0,
            heightPx,
            0,
            metrics
        );
        sink += _.Count;
    }
    sw.Restart();
    for (int i = 0; i < LayoutIters; i++)
    {
        var rows = ViewportLayout.Build(
            layoutSnap,
            topLines[i],
            topSegment: 0,
            heightPx,
            0,
            metrics
        );
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
        var _ = ViewportLayout.Build(
            layoutSnap,
            topLines[w % LayoutIters],
            topSegment: 0,
            heightPx,
            80,
            metrics
        );
        sink += _.Count;
    }
    sw.Restart();
    for (int i = 0; i < LayoutIters; i++)
    {
        var rows = ViewportLayout.Build(
            layoutSnap,
            topLines[i],
            topSegment: 0,
            heightPx,
            80,
            metrics
        );
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
            topSegment: 0,
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
        var rows = ViewportLayout.Build(
            layoutSnap,
            topLines[i],
            topSegment: 0,
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
    sw.Stop();
    double frameMs = TicksToMs(sw.ElapsedTicks) / LayoutIters;
    AddResult("L4 Frame(wrap OFF 全体)", $"{frameMs:F2} ms/回", "平均<16ms", frameMs < 16);

    // ---- L5) PixelMapper.OffsetToPx 相当計算 1000 回 ----
    // 代表 1 セグメント(可視 1 行目相当・平均行長)を対象に、末尾位置までの OffsetToPx を測る。
    // 空行ばかりに当たると偏るので topLine=0 の先頭視覚行を使う。
    var probeRows = ViewportLayout.Build(layoutSnap, 0, topSegment: 0, heightPx, 0, metrics);
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
