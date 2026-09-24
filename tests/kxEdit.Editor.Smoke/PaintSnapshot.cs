using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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
/// (状態が効いていない・描画が配送されない・撮影失敗)または比較で差あり。2 = 引数の誤り。
/// </para>
/// <para>
/// <b>撮影方法</b>: <c>PrintWindow(PW_CLIENTONLY | PW_RENDERFULLCONTENT)</c>。
/// <c>PW_RENDERFULLCONTENT</c> を付けるのは、WM_PRINT 経路(<c>OnPrint</c>)で描き直させるのではなく
/// <b>WM_PAINT で実際に画面へ描いた結果</b>(DWM のリダイレクト面)を取るため。描画の固定費削減は
/// WM_PAINT 経路(背景層・二重バッファ)を変えるので、WM_PRINT で撮っても検証にならない。
/// </para>
/// <para>
/// <b>測定条件</b>。(1) Form は<b>画面内</b>に置く(画面外の窓には <c>UpdateWindow</c> が WM_PAINT を
/// 配送しない。<see cref="PerfBench"/> と同じ理由)。作業領域に収まることを自己チェックする。
/// (2) フォーカスは画面外(クライアント領域外)のダミーボタンに置く=システムキャレットの点滅を
/// 写し込まない。<c>_hasFocus</c> は描画の入力ではないので、フォーカスの有無で絵は変わらない。
/// (3) 状態ごとに <c>ApplyAppearance</c>(製品と同じ経路)→ 新しいバッファを差し込む
/// (<c>ReplaceSource</c> がキャレット・選択・スクロール・IME を初期化する)→ 状態固有の操作、の順で
/// 組み立てる=前の状態を持ち越さない。
/// </para>
/// <para>
/// <b>状態を足すとき</b>(フェーズ 3・9 など)は <see cref="StateDefs"/> に 1 行足すだけでよい。
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

    private const string Usage = "使い方: --paint-snapshot <outDir> [--compare <baseDir>]";

    /// <summary>撮影するテーマ(AppearanceThemes の Id)。</summary>
    private static readonly string[] Themes = ["default", "white-on-black"];

    /// <summary>
    /// 1 状態の定義。<paramref name="Suffix"/> はファイル名の後半(<c>&lt;theme&gt;-&lt;Suffix&gt;.png</c>)。
    /// <paramref name="Configure"/> は外観設定(ApplyAppearance に渡す AppSettings)の変更、
    /// <paramref name="Arrange"/> は本文差し込み後の操作、<paramref name="Verify"/> は状態が効いたかの
    /// 自己チェック(失敗なら理由を返す)、<paramref name="Reset"/> は撮影後の後始末。
    /// <see cref="TopLine"/> / <see cref="ScrollX"/> は撮影時に期待する位置(状態固有の操作が
    /// 追従スクロール等で位置を動かしていないことの自己チェック)。
    /// </summary>
    private sealed record StateDef(
        string Suffix,
        Action<AppSettings>? Configure,
        Action<EditorControl, Body>? Arrange,
        Func<EditorControl, Body, string?>? Verify,
        Action<EditorControl>? Reset
    )
    {
        public int TopLine { get; init; }
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
        new("plain", null, null, null, null),
        new(
            "selection",
            null,
            (e, b) =>
                e.SetSelectionCharRange(
                    b.Line(SelStartLine) + SelStartCol,
                    b.Line(SelEndLine) + SelEndCol
                ),
            (e, b) =>
                e.GetSelectionCharRange()
                == (b.Line(SelStartLine) + SelStartCol, b.Line(SelEndLine) + SelEndCol)
                    ? null
                    : $"選択が張られない({e.GetSelectionCharRange()})",
            null
        ),
        new(
            "curline",
            s => s.HighlightCurrentLine = true,
            (e, b) => e.SetCaretCharOffset(b.Line(CurLine) + CurCol),
            (e, b) =>
                e.HighlightCurrentLine && e.CaretCharOffset == b.Line(CurLine) + CurCol
                    ? null
                    : $"現在行の強調/キャレット位置が効かない(caret={e.CaretCharOffset})",
            null
        ),
        new(
            "linenum",
            s => s.ShowLineNumbers = true,
            null,
            (e, _) => e.ShowLineNumbers ? null : "ShowLineNumbers が効かない",
            null
        ),
        new(
            "whitespace",
            s => s.ShowWhitespace = true,
            null,
            (e, _) => e.ShowWhitespace ? null : "ShowWhitespace が効かない",
            null
        ),
        new(
            "hscroll",
            null,
            (e, _) => e.ScrollX = HScrollPx,
            (e, _) =>
                e.ScrollX == HScrollPx
                    ? null
                    : $"ScrollX が {e.ScrollX}(期待 {HScrollPx}。横スクロールバーが出ていない)",
            null
        )
        {
            ScrollX = HScrollPx,
        },
        new(
            "ime",
            null,
            (e, b) =>
            {
                e.SetCaretCharOffset(b.Line(ImeLine) + ImeCol);
                e.__TestApplyComposition(
                    ImeText,
                    ImeText.Length,
                    [.. Enumerable.Repeat(ImeAttribute.Input, ImeText.Length)],
                    []
                );
            },
            (e, _) =>
                e.__TestIsComposing() && e.__TestImeText() == ImeText
                    ? null
                    : "__TestApplyComposition で未確定状態にならない",
            e =>
            {
                e.__TestApplyResult(""); // 未確定を解除(本文は変えない)
                if (e.__TestIsComposing())
                    throw new PaintSnapshotException("ime: 未確定を解除できない");
            }
        ),
        new(
            "scrolled",
            null,
            (e, _) => e.TopLine = ScrolledTopLine,
            (e, _) =>
                e.TopLine == ScrolledTopLine
                    ? null
                    : $"TopLine が {e.TopLine}(期待 {ScrolledTopLine})",
            null
        )
        {
            TopLine = ScrolledTopLine,
        },
    ];

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(nint hwnd, nint rcUpdate, nint hrgnUpdate, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    /// <summary>描画が本当に配送されたかの観測(状態ごとに増えることを自己チェックする)。</summary>
    private static int s_paints;

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

        Directory.CreateDirectory(outDir);

        ApplicationConfiguration.Initialize();
        using var form = new Form
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
        using var sink = new Button { Location = new Point(-200, -200), Size = new Size(10, 10) };
        using var editor = new EditorControl { Dock = DockStyle.Fill };
        form.Controls.Add(editor);
        form.Controls.Add(sink);
        form.Show(); // ハンドル生成(Show しないと描画が配送されない)
        form.ActiveControl = sink;
        editor.Paint += (_, _) => s_paints++;
        Application.DoEvents();

        try
        {
            var body = BuildBody();
            Check(
                Screen.FromControl(form).WorkingArea.Contains(form.Bounds),
                $"窓 {form.Bounds} が画面の作業領域に収まらない(WM_PAINT が来ない)"
            );
            Console.WriteLine(
                $"DeviceDpi={editor.DeviceDpi} / ClientSize={form.ClientSize} / 状態数={Themes.Length * StateDefs.Length}"
            );
            foreach (string theme in Themes)
            {
                foreach (var def in StateDefs)
                {
                    string name = $"{theme}-{def.Suffix}";
                    Shoot(form, editor, body, theme, def, Path.Combine(outDir, name + ".png"));
                    Console.WriteLine($"撮影: {name}.png");
                }
            }
        }
        catch (PaintSnapshotException e)
        {
            Console.Error.WriteLine($"[自己チェック失敗] {e.Message}");
            form.Close();
            return 1;
        }

        form.Close();
        Console.WriteLine($"出力: {outDir}");
        return compareDir is null ? 0 : Compare(outDir, compareDir);
    }

    /// <summary>1 状態を組み立てて描かせ、自己チェックの後に撮影する。</summary>
    private static void Shoot(
        Form form,
        EditorControl editor,
        Body body,
        string theme,
        StateDef def,
        string path
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
        _ = DwmFlush(); // 失敗しても致命ではない(撮影は DWM のリダイレクト面から読む)
        Check(s_paints > p0, $"{name}: WM_PAINT が配送されない(画面がロック中?)");

        Capture(form, path);
        def.Reset?.Invoke(editor);
    }

    private static void Capture(Form form, string path)
    {
        var size = form.ClientSize;
        using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            nint hdc = g.GetHdc();
            try
            {
                if (!PrintWindow(form.Handle, hdc, PwClientOnly | PwRenderFullContent))
                    throw new PaintSnapshotException(
                        $"PrintWindow が失敗した(Win32 {Marshal.GetLastWin32Error()})"
                    );
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        bmp.Save(path, ImageFormat.Png);
    }

    // ---- 比較 ----

    /// <summary>
    /// 2 つのフォルダーの *.png を同名同士で画素比較する。片方にしかないファイル・大きさ違いは差分扱い。
    /// 差が 1 画素でもあれば 1、全一致なら 0。
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

    private static (string OutDir, string? CompareDir) ParseArgs(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("出力フォルダーの指定がない");
        string outDir = Path.GetFullPath(args[0]);
        string? compareDir = null;
        for (int i = 1; i < args.Length; i++)
        {
            string next = i + 1 < args.Length ? args[i + 1] : "";
            switch (args[i])
            {
                case "--compare" when next.Length > 0:
                    compareDir = Path.GetFullPath(next);
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
