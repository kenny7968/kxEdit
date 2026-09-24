using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using kxEdit.Core.Buffers;
using kxEdit.Core.Editing;
using kxEdit.Core.Settings;

namespace kxEdit.Editor.Smoke;

/// <summary>
/// 描画を変えるフェーズの「ピクセル不変」を確かめる撮影・比較ツール
/// (実装計画 docs/plans/2026-09-24-perf-paint-cost.md §0.2)。
/// <c>--paint-snapshot &lt;outDir&gt; [--compare &lt;baseDir&gt;]</c>。
/// 決まった状態の列(テーマ × 表示設定 × 選択・IME・水平スクロール)を 1 枚ずつ描かせて PNG にし、
/// <c>--compare</c> があれば基準フォルダーの同名 PNG と画素単位で比べる。
/// </summary>
/// <remarks>
/// <para>
/// <b>EXIT</b>: 0 = 撮影成功(比較ありなら全画素一致)。1 = 自己チェックの失敗
/// (状態が効いていない・描画が配送されない・撮影や入出力の失敗・描画中の例外・撮った絵が
/// 互いに区別できない)または比較で差あり。2 = 引数の誤り(出力先に既に PNG がある場合を含む)。
/// </para>
/// <para>
/// <b>撮影方法</b>: <c>PrintWindow(PW_CLIENTONLY | PW_RENDERFULLCONTENT)</c>。実測では、この呼び出しは
/// 撮影の時点で<b>現在の状態から WM_PAINT の経路(<c>OnPrint</c> ではない)で全面を描き直した絵</b>を
/// 撮る(PrintWindow → NativeWindow.Callback → Control.WmPaint → EditorControl.OnPaint。クリップは本文全面)。
/// したがって<b>描画処理そのもののピクセル不変は確かめられる</b>が、<b>Invalidate の省略や部分再描画で
/// 画面に古い絵が残る不具合は写らない</b>(そちらは別の手段で確かめること)。この前提
/// (撮影中に WM_PAINT が起きること)は撮影のたびに自己チェックする。撮影前の <c>RedrawWindow</c> は
/// 子(スクロールバー)を含めて描画を配送し終えておくためのもの。
/// </para>
/// <para>
/// <b>測定条件</b>。(1) Form は<b>画面内</b>に置く(画面外の窓には <c>UpdateWindow</c> が WM_PAINT を
/// 配送しない。<see cref="PerfBench"/> と同じ理由)。作業領域に収まることを自己チェックする。
/// (2) フォーカスは画面外(クライアント領域外)のダミーボタンに置く=システムキャレットの点滅を
/// 写し込まない。<c>_hasFocus</c> は描画の入力ではないので、フォーカスの有無で絵は変わらない。
/// (3) 状態ごとに <c>ApplyAppearance</c>(製品と同じ経路)→ 新しいバッファを差し込む
/// (<c>ReplaceSource</c> がキャレット・選択・スクロール・IME を初期化する)→ 状態固有の操作、の順で
/// 組み立てる=前の状態を持ち越さない。
/// (4) 描画中の例外は <c>Application.ThreadException</c> で捕まえて EXIT 1 にする
/// (既定の ThreadExceptionDialog で止まらない・例外で欠けた絵を「一致」にしない)。
/// </para>
/// <para>
/// <b>撮った絵の自己チェック</b>: 全画像の画素のハッシュを比べ、互いに同一の組があれば EXIT 1。
/// これで (a) 全画像が互いに異なる、(b) 各状態が同じテーマの plain と異なる、(c) 2 テーマの plain が
/// 異なる、をまとめて確かめる(状態が絵に効いていないのに「一致」になるのを防ぐ)。
/// 将来「同じであるべき組」を足すときは <see cref="ExpectedIdentical"/> に名前の組を登録する。
/// </para>
/// <para>
/// <b>運用</b>: 出力先に既に PNG があれば断る(EXIT 2・消さない)=古い絵と混ざらない。
/// <b>基準画像は同じ版の道具で撮り直す</b>(道具の状態や本文を変えると絵が変わる。
/// 変更前のコミットで基準を撮る → 変更後のコミットで <c>--compare</c>、の順で、道具自体は
/// 両方で同じにする)。出力先には撮影環境(DPI・クライアント寸法・フォントの平滑化・OS)を
/// <c>env.txt</c> に書き、<c>--compare</c> 時に基準と食い違えば「環境差」と表示する
/// (判定は画素だけで行う)。
/// </para>
/// <para>
/// <b>状態を足すとき</b>(フェーズ 3・9 など)は <see cref="StateDefs"/> に 1 要素足すだけでよい。
/// 名前はファイル名になるので、既存の名前を変えると基準画像との比較が「片方にしかない」差分になる。
/// </para>
/// </remarks>
internal static class PaintSnapshot
{
    private const uint PwClientOnly = 0x1;
    private const uint PwRenderFullContent = 0x2;

    private const uint RdwInvalidate = 0x0001;
    private const uint RdwErase = 0x0004;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwUpdateNow = 0x0100;

    private const uint SpiGetFontSmoothing = 0x004A;
    private const uint SpiGetFontSmoothingType = 0x200A;
    private const uint SpiGetFontSmoothingContrast = 0x200C;

    private const string EnvFileName = "env.txt";

    private const string Usage = "使い方: --paint-snapshot <outDir> [--compare <baseDir>]";

    /// <summary>撮影するテーマ(AppearanceThemes の Id)。</summary>
    private static readonly string[] Themes = ["default", "white-on-black"];

    /// <summary>
    /// 1 状態の定義。<see cref="Suffix"/> はファイル名の後半(<c>&lt;theme&gt;-&lt;Suffix&gt;.png</c>)。
    /// </summary>
    private sealed record StateDef(string Suffix)
    {
        /// <summary>外観設定(ApplyAppearance に渡す AppSettings)の変更。</summary>
        public Action<AppSettings>? Configure { get; init; }

        /// <summary>本文差し込み後の操作。</summary>
        public Action<EditorControl, Body>? Arrange { get; init; }

        /// <summary>状態が効いたかの自己チェック(失敗なら理由を返す)。</summary>
        public Func<EditorControl, Body, string?>? Verify { get; init; }

        /// <summary>撮影後の後始末。</summary>
        public Action<EditorControl>? Reset { get; init; }

        /// <summary>撮影時に期待する TopLine(状態固有の操作が追従スクロール等で動かしていないこと)。</summary>
        public int TopLine { get; init; }

        /// <summary>撮影時に期待する ScrollX(同上)。</summary>
        public int ScrollX { get; init; }
    }

    /// <summary>本文と、状態が参照する位置。</summary>
    private sealed record Body(string Text, int[] LineStarts)
    {
        public int Line(int line) => LineStarts[line];
    }

    // 各状態の期待値(自己チェックと操作の両方で使う)。
    private const int SelStartLine = 1;
    private const int SelStartCol = 4;
    private const int SelEndLine = 3;
    private const int SelEndCol = 5;
    private const int CurLine = 2;
    private const int CurCol = 3;
    private const int ImeLine = 1;
    private const int ImeCol = 6;
    private const string ImeText = "にほんご";
    private const int HScrollPx = 120;
    private const int ScrolledTopLine = 5;

    private static readonly StateDef[] StateDefs =
    [
        new("plain"),
        new("selection")
        {
            Arrange = (e, b) =>
                e.SetSelectionCharRange(
                    b.Line(SelStartLine) + SelStartCol,
                    b.Line(SelEndLine) + SelEndCol
                ),
            Verify = (e, b) =>
                e.GetSelectionCharRange()
                == (b.Line(SelStartLine) + SelStartCol, b.Line(SelEndLine) + SelEndCol)
                    ? null
                    : $"選択が張られない({e.GetSelectionCharRange()})",
        },
        new("curline")
        {
            Configure = s => s.HighlightCurrentLine = true,
            Arrange = (e, b) => e.SetCaretCharOffset(b.Line(CurLine) + CurCol),
            Verify = (e, b) =>
                e.HighlightCurrentLine && e.CaretCharOffset == b.Line(CurLine) + CurCol
                    ? null
                    : $"現在行の強調/キャレット位置が効かない(caret={e.CaretCharOffset})",
        },
        new("linenum")
        {
            Configure = s => s.ShowLineNumbers = true,
            Verify = (e, _) => e.ShowLineNumbers ? null : "ShowLineNumbers が効かない",
        },
        new("whitespace")
        {
            Configure = s => s.ShowWhitespace = true,
            Verify = (e, _) => e.ShowWhitespace ? null : "ShowWhitespace が効かない",
        },
        new("hscroll")
        {
            Arrange = (e, _) => e.ScrollX = HScrollPx,
            Verify = (e, _) =>
                e.ScrollX == HScrollPx
                    ? null
                    : $"ScrollX が {e.ScrollX}(期待 {HScrollPx}。横スクロールバーが出ていない)",
            ScrollX = HScrollPx,
        },
        new("ime")
        {
            Arrange = (e, b) =>
            {
                e.SetCaretCharOffset(b.Line(ImeLine) + ImeCol);
                e.__TestApplyComposition(
                    ImeText,
                    ImeText.Length,
                    [.. Enumerable.Repeat(ImeAttribute.Input, ImeText.Length)],
                    []
                );
            },
            Verify = (e, b) =>
                e.__TestIsComposing()
                && e.__TestImeText() == ImeText
                && e.CaretCharOffset == b.Line(ImeLine) + ImeCol
                    ? null
                    : $"__TestApplyComposition で未確定状態にならない/キャレット位置が違う(caret={e.CaretCharOffset})",
            Reset = e =>
            {
                e.__TestApplyResult(""); // 未確定を解除(本文は変えない)
                if (e.__TestIsComposing())
                    throw new PaintSnapshotException("ime: 未確定を解除できない");
            },
        },
        new("scrolled")
        {
            Arrange = (e, _) => e.TopLine = ScrolledTopLine,
            Verify = (e, _) =>
                e.TopLine == ScrolledTopLine
                    ? null
                    : $"TopLine が {e.TopLine}(期待 {ScrolledTopLine})",
            TopLine = ScrolledTopLine,
        },
    ];

    /// <summary>
    /// 画素が同一であってよい画像名(拡張子なし)の組。撮った絵の自己チェック(全画像が互いに異なる)から
    /// 除外する。現在は空。「同じであるべき組」の状態を足すときに登録する(順不同)。
    /// </summary>
    private static readonly (string A, string B)[] ExpectedIdentical = [];

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(nint hwnd, nint rcUpdate, nint hrgnUpdate, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint param,
        out uint value,
        uint winIni
    );

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    /// <summary>描画が本当に配送されたかの観測(状態ごと・撮影ごとに増えることを自己チェックする)。</summary>
    private static int s_paints;

    /// <summary>メッセージ処理中(描画中など)に起きた最初の例外。撮影ごとに null であることを確かめる。</summary>
    private static Exception? s_error;

    public static int Run(string[] args)
    {
        string outDir;
        string? compareDir;
        try
        {
            (outDir, compareDir) = ParseArgs(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            int shot = Shoot(outDir);
            if (shot != 0)
                return shot;
            Console.WriteLine($"出力: {outDir}");
            return compareDir is null ? 0 : Compare(outDir, compareDir);
        }
        catch (Exception e)
            when (e
                    is IOException
                        or UnauthorizedAccessException
                        or ExternalException
                        // 壊れた/PNG でないファイルを Bitmap で読むと ArgumentException になる
                        or ArgumentException
            )
        {
            Console.Error.WriteLine($"[失敗] {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    /// <summary>全状態を撮影して <paramref name="outDir"/> に書く。自己チェック失敗は 1。</summary>
    private static int Shoot(string outDir)
    {
        Directory.CreateDirectory(outDir);

        ApplicationConfiguration.Initialize();
        // 描画中の例外で ThreadExceptionDialog(モーダル)を出して止まらないよう、捕まえて記録する。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => s_error ??= e.Exception;

        var form = new Form
        {
            Text = "kxEdit.Editor.Smoke --paint-snapshot",
            Width = 640,
            Height = 420,
            StartPosition = FormStartPosition.Manual,
            // 画面外の窓には WM_PAINT が来ない=撮るものが無い(PerfBench と同じ理由)。
            Location = Screen.PrimaryScreen?.WorkingArea.Location ?? Point.Empty,
            ShowInTaskbar = false,
        };
        // フォーカスの受け皿。クライアント領域の外に置く=自身も写らず、エディタのキャレットも点滅しない。
        // editor と sink は form の子なので、form の破棄で一緒に破棄される(finally の CloseQuietly)。
        var sink = new Button { Location = new Point(-200, -200), Size = new Size(10, 10) };
        var editor = new EditorControl { Dock = DockStyle.Fill };
        form.Controls.Add(editor);
        form.Controls.Add(sink);
        form.Show(); // ハンドル生成(Show しないと描画が配送されない)
        form.ActiveControl = sink;
        editor.Paint += (_, _) => s_paints++;
        Application.DoEvents();

        try
        {
            CheckNoError("起動");
            var body = BuildBody();
            Check(
                Screen.FromControl(form).WorkingArea.Contains(form.Bounds),
                $"窓 {form.Bounds} が画面の作業領域に収まらない(WM_PAINT が来ない)"
            );
            string env = DescribeEnvironment(editor, form);
            File.WriteAllText(Path.Combine(outDir, EnvFileName), env, new UTF8Encoding(false));
            Console.Write(env);
            Console.WriteLine($"状態数={Themes.Length * StateDefs.Length}");

            var hashes = new List<(string Name, string Hash)>();
            foreach (string theme in Themes)
            {
                foreach (var def in StateDefs)
                {
                    string name = $"{theme}-{def.Suffix}";
                    string hash = ShootOne(form, editor, body, theme, def, outDir);
                    hashes.Add((name, hash));
                    Console.WriteLine($"撮影: {name}.png");
                }
            }
            CheckDistinct(hashes);
        }
        catch (PaintSnapshotException e)
        {
            Console.Error.WriteLine($"[自己チェック失敗] {e.Message}");
            return 1;
        }
        finally
        {
            CloseQuietly(form);
        }
        return 0;
    }

    /// <summary>
    /// 窓を閉じて破棄する。破棄の競合による <see cref="InvalidOperationException"/> だけは吸収する
    /// (撮影と自己チェックの結果はこの時点で確定しているため)。
    /// </summary>
    /// <remarks>
    /// 以前は <c>form.Close()</c> の後に <c>using</c> でもう一度 Dispose しており、1/7 の頻度で
    /// 「CreateHandle() の実行中は Dispose() を呼び出せません」で落ちた(GdiBench にも同じ前例がある =
    /// 2026-08-02-large-line-wrap-perf-design.md §9.7)。有力な仮説は、NVDA などの UIA クライアントが
    /// WinForms 標準の FormAccessibleObject.Name(→ WindowText → Handle getter)を RPC スレッドから読み、
    /// 破棄中の窓を別スレッドで作り直すところに UI スレッドの Dispose が重なる、というもの
    /// (docs/plans/2026-09-24-perf-paint-cost.md の実施記録)。
    /// </remarks>
    private static void CloseQuietly(Form form)
    {
        try
        {
            form.Close(); // 非モーダルの Form は WM_CLOSE の中で子まで Dispose される
            if (!form.IsDisposed)
                form.Dispose();
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine($"[警告] 窓の破棄で競合(結果には影響しない): {e.Message}");
        }
    }

    /// <summary>1 状態を組み立てて描かせ、自己チェックの後に撮影する。画素のハッシュを返す。</summary>
    private static string ShootOne(
        Form form,
        EditorControl editor,
        Body body,
        string theme,
        StateDef def,
        string outDir
    )
    {
        string name = $"{theme}-{def.Suffix}";
        var settings = new AppSettings { Theme = theme };
        def.Configure?.Invoke(settings);
        editor.ApplyAppearance(settings);
        editor.SetOrReplaceSource(TextBuffer.FromString(body.Text));
        def.Arrange?.Invoke(editor, body);

        string? failure = def.Verify?.Invoke(editor, body);
        Check(failure is null, $"{name}: {failure}");
        // 状態固有の操作が他の位置を動かしていないこと(例: 選択の追従スクロール)。
        Check(
            editor.TopLine == def.TopLine && editor.ScrollX == def.ScrollX,
            $"{name}: スクロール位置が想定外(TopLine={editor.TopLine}・ScrollX={editor.ScrollX})"
        );
        Check(!editor.Focused, $"{name}: EditorControl がフォーカスを持った(キャレットが写り込む)");

        int p0 = s_paints;
        // エディタ本体と子(スクロールバー)を同期で描き直させる。Control.Update は子を描かない。
        RedrawWindow(form.Handle, 0, 0, RdwInvalidate | RdwErase | RdwAllChildren | RdwUpdateNow);
        Application.DoEvents();
        _ = DwmFlush(); // 失敗しても致命ではない
        // 例外を先に見る(OnPaint が base.OnPaint より前で投げると Paint が数えられず、
        // 「配送されない」という誤った理由になるため)。
        CheckNoError(name);
        Check(s_paints > p0, $"{name}: WM_PAINT が配送されない(画面がロック中?)");

        string hash = Capture(form, name, Path.Combine(outDir, name + ".png"));
        def.Reset?.Invoke(editor);
        CheckNoError(name);
        return hash;
    }

    /// <summary>
    /// クライアント領域を撮って PNG に書き、画素(32bpp ARGB)の SHA-256 を返す。
    /// 撮影中に WM_PAINT が起きること(クラス doc の前提)を自己チェックする。
    /// </summary>
    private static string Capture(Form form, string name, string path)
    {
        var size = form.ClientSize;
        using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        int p0 = s_paints;
        using (var g = Graphics.FromImage(bmp))
        {
            nint hdc = g.GetHdc();
            try
            {
                if (!PrintWindow(form.Handle, hdc, PwClientOnly | PwRenderFullContent))
                    throw new PaintSnapshotException(
                        $"{name}: PrintWindow が失敗した(Win32 {Marshal.GetLastWin32Error()})"
                    );
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        CheckNoError(name); // 描画回数より先に見る(理由は ShootOne と同じ)
        Check(
            s_paints > p0,
            $"{name}: 撮影中に WM_PAINT が起きない(撮影方法の前提が崩れた。クラス doc 参照)"
        );
        bmp.Save(path, ImageFormat.Png);
        int[] pixels = ReadPixels(bmp);
        return Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(pixels.AsSpan())));
    }

    /// <summary>
    /// 撮った絵が互いに異なることを確かめる(<see cref="ExpectedIdentical"/> の組は除く)。
    /// 全組を見るので「各状態 ≠ 同じテーマの plain」「2 テーマの plain が異なる」も含む。
    /// </summary>
    private static void CheckDistinct(List<(string Name, string Hash)> hashes)
    {
        var same = new List<string>();
        for (int i = 0; i < hashes.Count; i++)
        {
            for (int j = i + 1; j < hashes.Count; j++)
            {
                if (hashes[i].Hash != hashes[j].Hash)
                    continue;
                string a = hashes[i].Name;
                string b = hashes[j].Name;
                bool allowed = ExpectedIdentical.Any(p =>
                    (p.A == a && p.B == b) || (p.A == b && p.B == a)
                );
                if (!allowed)
                    same.Add($"{a} = {b}");
            }
        }
        Check(
            same.Count == 0,
            $"画素が同一の画像がある(状態が絵に効いていない): {string.Join(" / ", same)}"
        );
    }

    private static void CheckNoError(string name)
    {
        if (s_error is not null)
            throw new PaintSnapshotException($"{name}: メッセージ処理中に例外: {s_error}");
    }

    // ---- 環境 ----

    /// <summary>絵を左右する環境の記述(<c>env.txt</c> の内容。1 行 1 項目の <c>key=value</c>)。</summary>
    private static string DescribeEnvironment(EditorControl editor, Form form)
    {
        var sb = new StringBuilder();
        sb.Append("DeviceDpi=").Append(editor.DeviceDpi).Append('\n');
        sb.Append("ClientSize=")
            .Append(form.ClientSize.Width)
            .Append('x')
            .Append(form.ClientSize.Height)
            .Append('\n');
        sb.Append("FontSmoothing=").Append(Spi(SpiGetFontSmoothing)).Append('\n');
        sb.Append("FontSmoothingType=").Append(Spi(SpiGetFontSmoothingType)).Append('\n');
        sb.Append("FontSmoothingContrast=").Append(Spi(SpiGetFontSmoothingContrast)).Append('\n');
        sb.Append("OS=").Append(Environment.OSVersion.VersionString).Append('\n');
        return sb.ToString();
    }

    private static string Spi(uint action) =>
        SystemParametersInfo(action, 0, out uint v, 0)
            ? v.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : $"(取得失敗 {Marshal.GetLastWin32Error()})";

    /// <summary>基準と今回の env.txt を比べ、食い違いを表示する(判定には使わない)。</summary>
    private static void CompareEnvironment(string outDir, string baseDir)
    {
        string basePath = Path.Combine(baseDir, EnvFileName);
        string outPath = Path.Combine(outDir, EnvFileName);
        if (!File.Exists(basePath) || !File.Exists(outPath))
        {
            Console.WriteLine(
                $"[警告] {EnvFileName} が{(File.Exists(basePath) ? "今回の出力" : "基準")}に無い(撮影環境を照合できない)"
            );
            return;
        }
        var a = ReadEnv(basePath);
        var b = ReadEnv(outPath);
        var diffs = a
            .Keys.Union(b.Keys)
            .Order(StringComparer.Ordinal)
            .Where(k => a.GetValueOrDefault(k) != b.GetValueOrDefault(k))
            .Select(k =>
                $"{k}: 基準 {a.GetValueOrDefault(k) ?? "(無し)"} ⇔ 今回 {b.GetValueOrDefault(k) ?? "(無し)"}"
            )
            .ToList();
        if (diffs.Count == 0)
        {
            Console.WriteLine("撮影環境: 基準と同じ");
            return;
        }
        Console.WriteLine("[環境差] 基準と撮影環境が違う(画素の差は環境由来の可能性がある):");
        foreach (string d in diffs)
            Console.WriteLine($"  {d}");
    }

    private static Dictionary<string, string> ReadEnv(string path) =>
        File.ReadAllLines(path)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.Ordinal);

    // ---- 比較 ----

    /// <summary>
    /// 2 つのフォルダーの *.png を同名同士で画素比較する。片方にしかないファイル・大きさ違いは差分扱い。
    /// 差が 1 画素でもあれば 1、全一致なら 0。env.txt は比較対象外(食い違いは表示のみ)。
    /// </summary>
    private static int Compare(string outDir, string baseDir)
    {
        var names = Directory
            .EnumerateFiles(baseDir, "*.png")
            .Concat(Directory.EnumerateFiles(outDir, "*.png"))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Console.WriteLine();
        Console.WriteLine($"比較: {outDir} ⇔ 基準 {baseDir}");
        CompareEnvironment(outDir, baseDir);
        int differing = 0;
        foreach (string file in names)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string a = Path.Combine(baseDir, file);
            string b = Path.Combine(outDir, file);
            string line;
            if (!File.Exists(a))
                line = "差(基準に無い)";
            else if (!File.Exists(b))
                line = "差(今回の出力に無い)";
            else
                line = CompareImages(a, b);
            if (line != "一致")
                differing++;
            Console.WriteLine($"{name}: {line}");
        }
        Console.WriteLine(
            differing == 0
                ? $"合計: {names.Count} 枚すべて一致 EXIT 0"
                : $"合計: {names.Count} 枚中 {differing} 枚に差 EXIT 1"
        );
        return differing == 0 ? 0 : 1;
    }

    private static string CompareImages(string pathA, string pathB)
    {
        using var a = new Bitmap(pathA);
        using var b = new Bitmap(pathB);
        if (a.Size != b.Size)
            return $"差(大きさ違い {a.Width}x{a.Height} ⇔ {b.Width}x{b.Height})";
        int w = a.Width;
        int[] pxA = ReadPixels(a);
        int[] pxB = ReadPixels(b);
        long diff = 0;
        Point? first = null;
        for (int i = 0; i < pxA.Length; i++)
        {
            if (pxA[i] == pxB[i])
                continue;
            diff++;
            first ??= new Point(i % w, i / w);
        }
        return diff == 0
            ? "一致"
            : $"差 {diff} 画素(最初の座標 ({first!.Value.X},{first.Value.Y}))";
    }

    /// <summary>全画素を 32bpp ARGB の int 配列(行優先・stride の詰め物なし)で読む。</summary>
    private static int[] ReadPixels(Bitmap bmp)
    {
        int width = bmp.Width;
        int height = bmp.Height;
        var data = bmp.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb
        );
        try
        {
            var pixels = new int[width * height];
            for (int y = 0; y < height; y++)
                Marshal.Copy(data.Scan0 + (y * data.Stride), pixels, y * width, width);
            return pixels;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    // ---- 本文と引数 ----

    /// <summary>
    /// 60 行の固定本文(乱数なし)。縦スクロールバーが出る行数・日本語・ASCII・混在・
    /// サロゲートペア・空行・半角/全角空白とタブ・200 字の長い行(横スクロールバーが出る)を含む。
    /// </summary>
    private static Body BuildBody()
    {
        var lines = new List<string>
        {
            "kxEdit 描画スナップショット用の本文(ピクセル比較)",
            "The quick brown fox jumps over the lazy dog.",
            "日本語と ASCII の混在: abc あいう 123 漢字カナ",
            "サロゲートペア: 𠮷野家の𠮷・𩸽(ほっけ)",
            "",
            "半角 空白 と 全角\u3000空白\u3000と タブ\tの行\t末尾",
            "\tタブで始まる行",
            string.Concat(Enumerable.Range(0, 20).Select(i => $"[{i:D2}]-long-")),
        };
        for (int i = lines.Count; i < 60; i++)
        {
            lines.Add(
                i % 7 == 0 ? ""
                : (i % 2 == 0) ? $"{i + 1:D2} 行目: 吾輩は猫である。名前はまだ無い。"
                : $"{i + 1:D2} line: mixed 混在 text {i * 3}"
            );
        }
        Check(lines.Count == 60, "本文が 60 行でない");
        Check(lines[7].Length == 200, $"長い行が 200 字でない({lines[7].Length})");

        var sb = new StringBuilder();
        var starts = new int[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            starts[i] = sb.Length;
            sb.Append(lines[i]);
            if (i + 1 < lines.Count)
                sb.Append("\r\n");
        }
        return new Body(sb.ToString(), starts);
    }

    /// <summary>末尾の区切り文字を落とした完全パス(同一フォルダーの判定を末尾の <c>\</c> ですり抜けさせない)。</summary>
    private static string NormalizeDir(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static (string OutDir, string? CompareDir) ParseArgs(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("出力フォルダーの指定がない");
        string outDir = NormalizeDir(args[0]);
        string? compareDir = null;
        for (int i = 1; i < args.Length; i++)
        {
            string next = i + 1 < args.Length ? args[i + 1] : "";
            switch (args[i])
            {
                case "--compare" when next.Length > 0:
                    compareDir = NormalizeDir(next);
                    i++;
                    break;
                default:
                    throw new ArgumentException(
                        $"--paint-snapshot の引数を解釈できない: {args[i]}"
                    );
            }
        }
        if (compareDir is not null)
        {
            if (!Directory.Exists(compareDir))
                throw new ArgumentException($"基準フォルダーが無い: {compareDir}");
            if (string.Equals(compareDir, outDir, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("出力フォルダーと基準フォルダーが同じ");
        }
        // 古い絵と混ざらないよう、既に PNG がある出力先は断る(消さない)。
        if (Directory.Exists(outDir) && Directory.EnumerateFiles(outDir, "*.png").Any())
            throw new ArgumentException(
                $"出力フォルダーに既に PNG がある(新しいフォルダーを指定する): {outDir}"
            );
        return (outDir, compareDir);
    }

    private static void Check(bool ok, string message)
    {
        if (!ok)
            throw new PaintSnapshotException(message);
    }
}

/// <summary><see cref="PaintSnapshot"/> の自己チェックの失敗。EXIT 1 で終わる。</summary>
public sealed class PaintSnapshotException : Exception
{
    public PaintSnapshotException() { }

    public PaintSnapshotException(string message)
        : base(message) { }

    public PaintSnapshotException(string message, Exception innerException)
        : base(message, innerException) { }
}
