using yEdit.Core.Buffers;
using yEdit.Core.Text;
using yEdit.Editor.Smoke;

// P2 Task 14 の smoke 起動器。--bench を渡すと GDI 経由の実描画ベンチ(画面内 Form。
// 理由は GdiBench.Run)を走らせて EXIT 0/1 で判定する。それ以外の起動では EditorControl を Fill させた
// 単体 Form を出し、ファイルを読み込ませて eye check する用途。
//
// エンコーディングプロバイダ登録: MainForm の Shift_JIS / EUC-JP メニューが使えるように
// 起動最初に一度だけ呼ぶ(Core が内部で二重チェックしているが明示・yEdit.App と同じ扱い)。
EncodingCatalog.EnsureRegistered();

if (args.Length > 0 && args[0] == "--bench")
{
    return GdiBench.Run(args);
}

// 2026-08-02 巨大 1 行調査 Task 3: --largeline。空白・改行を一切含まない単一長大行を
// 実 EditorControl へ載せ、バッファ差し込みと初回描画を GDI 込みで測る
// (GDI 抜きの構造コストは yEdit.Core.Bench --largeline が対)。
if (args.Length > 0 && args[0] == "--largeline")
{
    return LargeLineBench.Run();
}

// 2026-08-03 UIA 単語単位調査 Task 1 で新設し、2026-08-04 の修正 Task 6 で作り直した
// --wordunit。調査時代は「UIA の単語スパン(空白のみ区切り)が Ctrl+←→ / ダブルクリック選択
// (文字クラス規則)とずれる」ことの採取が目的だったが、その素朴実装は修正 Task 3 で消えた。
// 現在は (1) SR の読み上げスパンとダブルクリック単語選択が一致することの確認、
// (2) 空白ゼロ長大行での「上限なし」対「本番 cap」のコスト差、(3) cap 掃引、
// (4) 現実テキストのクラス run 長 を採る(WordUnitBench のクラス doc が正本)。GDI は通らない。
if (args.Length > 0 && args[0] == "--wordunit")
{
    return WordUnitBench.Run();
}

// P4 Task 14: --ime サブコマンド。ATOK 実機検証(docs/plans/2026-07-06-p4-ime-checklist.md)
// 用に、未確定色/下線を目視しやすい長文サンプルをメモリ上で生成して開いた状態から起動する。
if (args.Length > 0 && args[0] == "--ime")
{
    ApplicationConfiguration.Initialize();
    var buf = TextBuffer.FromString(
        "IME 動作確認用サンプル(P4 Task 14)\n"
            + "文字入力→確定/変換/取消 の動作をこの EditorControl 上で試してください。\n\n"
            + string.Concat(Enumerable.Repeat("あいうえおかきくけこさしすせそたちつてと\n", 20))
    );
    Application.Run(new MainForm(buf, "(IME サンプル)"));
    return 0;
}

// P5 Task 13: --uia サブコマンド。SR(NVDA/ナレーター)と ATOK 実機検証
// (docs/plans/2026-07-06-p5-uia-checklist.md)用の起動モード。
// UIA プロバイダは EditorControl に常時配線済みのため、--uia の実態は
// タイトルバーへ [UIA] を付けて起動モードを判別できるようにする目印のみ。
// 追加引数(パス)があれば openat 起動する。
if (args.Length > 0 && args[0] == "--uia")
{
    ApplicationConfiguration.Initialize();
    var initialPath = args.Length > 1 ? args[1] : null;
    var form = new MainForm(initialPath) { MarkUiaTitle = true };
    Application.Run(form);
    return 0;
}

// P7 I-3 Task 4: --gen-1gb <path>=1GB UTF-8 ASCII ダミーファイル生成(手動 bench 用)。
// 1MB block を 1024 回書き出す=1,073,741,824 bytes ちょうど。64 char ごとに '\n' を混ぜて
// 単一巨大行を回避=Load 側の LineEndingDetector / TextBufferBuilder 行分割経路を踏む。
// 注意: 検出 EOL=Lf のため `ConvertEols(Lf)` は fast-path 判定で rebuild 未実行。
// rebuild 経路(LF→CRLF 等)の 1GB bench が要る場合は別サブコマンドで CRLF 統一する必要あり=P7 申し送り。
if (args.Length >= 2 && args[0] == "--gen-1gb")
{
    string outPath = args[1];
    byte[] block = new byte[1024 * 1024]; // 1MB
    for (int i = 0; i < block.Length; i++)
        block[i] = (byte)('a' + (i % 26));
    for (int i = 63; i < block.Length; i += 64)
        block[i] = (byte)'\n';
    using var fs = File.Create(outPath);
#pragma warning disable S6966 // reason: smoke 手動実行の top-level 非 async パス、sync IO 保持(GrepController 残 4 件は Task C-8 で修正)
    for (int i = 0; i < 1024; i++)
        fs.Write(block, 0, block.Length);
    fs.Flush();
#pragma warning restore S6966
    Console.WriteLine($"generated {outPath} = {new FileInfo(outPath).Length:N0} bytes");
    return 0;
}

// P7 I-3 Task 4: --bench-save <path>=Load→ConvertEols→Save のメモリ peak/時間実測。
// I-3 で「1GB 級 Save で 5GB peak」を chunk 化して O(text) 化した効能を数値で確認する用途。
// 自動テストは 1GB を扱わない=手動 smoke で 1 回計測して設計書に控えるだけ。
if (args.Length >= 2 && args[0] == "--bench-save")
{
    string inPath = args[1];
    string outPath = Path.Combine(
        Path.GetTempPath(),
        "yedit-bench-save-" + Path.GetRandomFileName() + ".txt"
    );
    try
    {
        // ベースライン(GC 強制回収後の peak)
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long peak0 = GC.GetTotalMemory(forceFullCollection: false);

        // Load(Stream I/O・UTF-8 は TextBufferBuilder チャンク経路)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var loaded = TextFileService.LoadAsBufferAuto(inPath);
        long loadMs = sw.ElapsedMilliseconds;
        long peakLoad = GC.GetTotalMemory(forceFullCollection: false);

        // EditorControl.SetSource は Handle 作成(CreateCaret/Invalidate)を発火し得る。
        // WinForms Control のハンドル作成は STA 前提=直接 MTA スレッドで呼ぶと落ちる可能性がある。
        // 安全側で STA スレッドを立てて Set/ConvertEols/Save をその中で回す。
        long peakConvert = 0,
            peakSave = 0,
            convertMs = 0,
            saveMs = 0;
        Exception? threadEx = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var ctrl = new yEdit.Editor.EditorControl();
                ctrl.SetSource(loaded.Buffer);
                ctrl.EolMode = loaded.LineEnding;

                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                ctrl.ConvertEols(loaded.LineEnding);
                convertMs = sw2.ElapsedMilliseconds;
                peakConvert = GC.GetTotalMemory(forceFullCollection: false);

                sw2.Restart();
                TextFileService.Save(outPath, ctrl.CurrentBuffer, loaded.Encoding, loaded.HasBom);
                saveMs = sw2.ElapsedMilliseconds;
                peakSave = GC.GetTotalMemory(forceFullCollection: false);
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (threadEx is not null)
            throw threadEx;

        long inSize = new FileInfo(inPath).Length;
        long outSize = new FileInfo(outPath).Length;
        Console.WriteLine($"in={inSize:N0} B  out={outSize:N0} B  match={(inSize == outSize)}");
        Console.WriteLine($"peak0     ={peak0:N0} B");
        Console.WriteLine(
            $"peakLoad  ={peakLoad:N0} B  (delta={(peakLoad - peak0):N0})  loadMs={loadMs}"
        );
        Console.WriteLine(
            $"peakConvert={peakConvert:N0} B  (delta={(peakConvert - peakLoad):N0})  convertMs={convertMs}"
        );
        Console.WriteLine(
            $"peakSave  ={peakSave:N0} B  (delta={(peakSave - peakConvert):N0})  saveMs={saveMs}"
        );
    }
    finally
    {
        if (File.Exists(outPath))
            File.Delete(outPath);
    }
    return 0;
}

ApplicationConfiguration.Initialize();
Application.Run(new MainForm(args.FirstOrDefault()));
return 0;
