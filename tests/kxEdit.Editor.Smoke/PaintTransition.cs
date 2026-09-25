using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using kxEdit.Core.Buffers;
using kxEdit.Core.Editing;
using kxEdit.Core.Settings;
using Body = kxEdit.Editor.Smoke.PaintSnapshot.Body;

namespace kxEdit.Editor.Smoke;

/// <summary>
/// 「Invalidate の省略や部分再描画で、画面に古い絵が残る」不具合を実画面で確かめる比較ツール
/// (実装計画 docs/plans/2026-09-25-perf-skip-invalidate.md Task 5。
/// フェーズ 1 の申し送り 1〜3 = docs/plans/2026-09-24-perf-paint-cost.md)。
/// <c>--paint-transition [--expect-skip] [--out &lt;dir&gt;]</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>手順</b>(遷移 1 つごと。<see cref="Transitions"/> の 1 要素)。
/// (1) <c>ApplyAppearance</c> → 本文を差し込む → 準備の操作 = 状態 A。状態を自己チェックする。
/// (2) 全面を同期で描き直し(描画が起きたことを自己チェック)、撮る = 絵 A。
/// (3) 遷移の操作をして <c>Application.DoEvents</c> でメッセージを流す(描き直しは保留中の無効領域の
/// 分だけ起きる)。その間の描画回数を記録する。流した後に保留中の無効領域が残っていないことを
/// 自己チェックする(残っていれば、差は省略ではなく配送漏れの意味になるため)。
/// (4) <b>描画を起こさずに</b>撮る = 絵 X(今、画面に出ている絵)。
/// (5) 全面を描き直して撮る = 絵 Y(今の状態の正解)。<b>Y の前の描き直しが実際に起きたことを
/// 自己チェックする</b>(起きなければ X も Y も同じ古い絵になり、偽の「一致」になる)。
/// もう一度描き直して撮った Y' が Y と一致することも確かめる(撮影の決定性)。
/// (6) X と Y を画素で比べる。差があれば「古い絵が残る」で失敗。
/// あわせて遷移の表を自己チェックする: <c>Paint</c> の遷移は Y ≠ A(絵が変わる)、<c>Skip</c> の遷移は
/// Y = A(絵が変わらない)であること。
/// </para>
/// <para>
/// <b>撮り方</b>: <c>GetDC(editor.Handle)</c> から <c>BitBlt(SRCCOPY)</c>。DWM の下では窓の描画面
/// (リダイレクト面)を読むので、他の窓(NVDA のスピーチビューアー等の最前面の窓)に覆われても
/// 読める。<b>撮影で WM_PAINT は起きない</b>(撮影の前後で描画回数が変わらないことを毎回自己チェック
/// する)。ここが <see cref="PaintSnapshot"/>(<c>PrintWindow</c>。撮影の中で全面を描き直すので、
/// 古い絵が残る不具合は写らない)との違い。エディタの子(スクロールバー)も同じ描画面にあるので写る。
/// </para>
/// <para>
/// <b>陽性対照</b>(<see cref="Expect.Stale"/>): 描画の入力を変えた直後に <c>ValidateRect</c> で
/// 無効領域を取り消す。X が A と同じで Y と異なることを要求する。満たさなければ、撮り方が画面の絵を
/// 読めていない(道具が壊れている)ので EXIT 1。
/// </para>
/// <para>
/// <b>描画回数の期待</b>(<see cref="Expect"/>): <c>Paint</c> の遷移で操作中の描画が 0 回なら失敗
/// (常に検査)。<c>Skip</c> の遷移で 1 回以上なら、<c>--expect-skip</c> のときだけ失敗
/// (Invalidate を省く前のコードは描き直すので、変更前の実行では付けない)。
/// </para>
/// <para>
/// <b>測定条件</b>は <see cref="PaintSnapshot"/> と同じ: 窓は画面内(作業領域に収まることを自己チェック。
/// 画面外の窓には WM_PAINT が来ない)・フォーカスは画面外のダミーボタン(システムキャレットを
/// 写し込まない)・描画中の例外は <c>Application.ThreadException</c> で捕まえて EXIT 1。
/// 画面がロックされていると描画が起きないので、自己チェックで EXIT 1 になる。
/// </para>
/// <para>
/// <b>EXIT</b>: 0 = 全遷移が一致し、描画回数も期待どおり(陽性対照は差あり)。1 = いずれかの遷移の失敗、
/// または自己チェックの失敗(後者は途中で止める)。2 = 引数の誤り。
/// <c>--out</c> があれば、失敗した遷移の X・Y を <c>&lt;名前&gt;-x.png</c> / <c>-y.png</c> で書く。
/// </para>
/// <para>
/// <b>遷移を足すとき</b>(フェーズ 9 など)は <see cref="Transitions"/> に 1 要素足すだけでよい。
/// 準備の後の状態(<see cref="Transition.Arranged"/>)は必ず書く(準備が効かずに「一致」になるのを防ぐ)。
/// </para>
/// </remarks>
internal static class PaintTransition
{
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwErase = 0x0004;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwUpdateNow = 0x0100;

    private const int SrcCopy = 0x00CC0020;

    private const string Usage = "使い方: --paint-transition [--expect-skip] [--out <dir>]";

    private const string DarkTheme = "white-on-black";

    /// <summary>操作中の描画回数の期待。</summary>
    internal enum Expect
    {
        /// <summary>描き直しが要る(0 回なら失敗)。</summary>
        Paint,

        /// <summary>フェーズ 3 以後は描き直さない(1 回以上なら <c>--expect-skip</c> のときだけ失敗)。</summary>
        Skip,

        /// <summary>回数を問わない。</summary>
        Any,

        /// <summary>陽性対照: 描画 0 回で、X = A かつ X ≠ Y であること。</summary>
        Stale,
    }

    /// <summary>1 つの遷移。</summary>
    internal sealed record Transition(string Name, Expect Expect)
    {
        /// <summary>外観設定(ApplyAppearance に渡す AppSettings)の変更。</summary>
        public Action<AppSettings>? Configure { get; init; }

        /// <summary>本文差し込み後の準備(状態 A を作る)。</summary>
        public required Action<EditorControl, Body> Arrange { get; init; }

        /// <summary>
        /// 準備の後の (アンカー, キャレット)。自己チェックに使う。TopLine と ScrollX は 0・未確定は
        /// <see cref="Composing"/> のとおりであることも確かめる。
        /// </summary>
        public required Func<Body, (int Anchor, int Caret)> Arranged { get; init; }

        /// <summary>準備の後に未確定文字列があるか。</summary>
        public bool Composing { get; init; }

        /// <summary>遷移の操作。</summary>
        public required Action<EditorControl, Body> Act { get; init; }

        /// <summary>操作の後の自己チェック(失敗なら理由を返す)。遷移の意味が成り立つことを確かめる。</summary>
        public Func<EditorControl, Body, string?>? VerifyAfter { get; init; }
    }

    // 位置(行, 桁)。行は PaintSnapshot.BuildBody の 0 始まりの行番号。
    private const int CurLine = 2;
    private const int CurCol = 3;
    private const int ImeLine = 1;
    private const int ImeCol = 6;
    private const string ImeText = "にほんご";
    private const int LongLine = 7;
    private const int FarLine = 40;

    private static int At(Body b, int line, int col) => b.Line(line) + col;

    private static (int, int) Caret(Body b, int line, int col) =>
        (At(b, line, col), At(b, line, col));

    private static string? CaretAt(EditorControl e, int expected) =>
        e.CaretCharOffset == expected && e.SelectionAnchor == expected
            ? null
            : $"操作の後の位置が想定外(anchor={e.SelectionAnchor}・caret={e.CaretCharOffset}・期待 {expected})";

    private static void MoveTo(EditorControl e, Body b, int line, int col) =>
        e.SetCaretCharOffset(At(b, line, col));

    /// <summary>既定の準備: キャレットを (2,3) に置く。</summary>
    private static Transition CaretMove(
        string name,
        Expect expect,
        int toLine,
        int toCol,
        Action<AppSettings>? configure = null
    ) =>
        new(name, expect)
        {
            Configure = configure,
            Arrange = (e, b) => MoveTo(e, b, CurLine, CurCol),
            Arranged = b => Caret(b, CurLine, CurCol),
            Act = (e, b) => MoveTo(e, b, toLine, toCol),
            VerifyAfter = (e, b) => CaretAt(e, At(b, toLine, toCol)),
        };

    /// <summary>
    /// 遷移の表(実装計画 Task 5)。計画の <c>collapse-anchored</c> の (4,1) は空行の CR と LF の間で、
    /// (4,0) にスナップされるので、同じ意味(別の行の選択なしの位置へ)の (5,1) にした。
    /// </summary>
    internal static readonly Transition[] Transitions =
    [
        CaretMove("caret-right", Expect.Skip, CurLine, CurCol + 1),
        CaretMove("caret-down", Expect.Skip, CurLine + 1, CurCol),
        CaretMove(
            "linenum-caret-down",
            Expect.Skip,
            CurLine + 1,
            CurCol,
            s => s.ShowLineNumbers = true
        ),
        CaretMove("curline-same-line", Expect.Skip, CurLine, 6, s => s.HighlightCurrentLine = true),
        CaretMove(
            "curline-next-line",
            Expect.Paint,
            CurLine + 1,
            CurCol,
            s => s.HighlightCurrentLine = true
        ),
        new("select-extend", Expect.Paint)
        {
            Arrange = (e, b) => MoveTo(e, b, CurLine, CurCol),
            Arranged = b => Caret(b, CurLine, CurCol),
            Act = (e, b) => e.MoveCaretWithSelection(At(b, CurLine, 8)),
            VerifyAfter = (e, b) =>
                e.GetSelectionCharRange() == (At(b, CurLine, CurCol), At(b, CurLine, 8))
                    ? null
                    : $"選択が張られない({e.GetSelectionCharRange()})",
        },
        new("select-clear", Expect.Paint)
        {
            Arrange = (e, b) => e.SetSelectionCharRange(At(b, 1, 4), At(b, 3, 5)),
            Arranged = b => (At(b, 1, 4), At(b, 3, 5)),
            Act = (e, b) => MoveTo(e, b, 3, 5),
            VerifyAfter = (e, b) => CaretAt(e, At(b, 3, 5)),
        },
        new("collapse-anchored", Expect.Skip)
        {
            Arrange = (e, b) => MoveTo(e, b, CurLine, CurCol),
            Arranged = b => Caret(b, CurLine, CurCol),
            Act = (e, b) => e.SetSelectionAnchored(At(b, 5, 1), At(b, 5, 1)),
            VerifyAfter = (e, b) => CaretAt(e, At(b, 5, 1)),
        },
        CaretMove("scroll-by-caret", Expect.Paint, FarLine, 0) with
        {
            VerifyAfter = (e, b) =>
                CaretAt(e, At(b, FarLine, 0))
                ?? (e.TopLine > 0 ? null : "TopLine が 0 のまま(スクロールが起きない窓の大きさ)"),
        },
        new("hscroll-by-caret", Expect.Paint)
        {
            Arrange = (e, b) => MoveTo(e, b, LongLine, 0),
            Arranged = b => Caret(b, LongLine, 0),
            Act = (e, b) => e.SetCaretCharOffset(b.Line(LongLine + 1) - 2), // 行 7 の末尾(CRLF の前)
            VerifyAfter = (e, b) =>
                CaretAt(e, b.Line(LongLine + 1) - 2)
                ?? (e.ScrollX > 0 ? null : "ScrollX が 0 のまま(水平スクロールが起きない)"),
        },
        new("ime-cancel-by-move", Expect.Paint)
        {
            Arrange = (e, b) =>
            {
                MoveTo(e, b, ImeLine, ImeCol);
                e.__TestApplyComposition(
                    ImeText,
                    ImeText.Length,
                    [.. Enumerable.Repeat(ImeAttribute.Input, ImeText.Length)],
                    []
                );
            },
            Arranged = b => Caret(b, ImeLine, ImeCol),
            Composing = true,
            Act = (e, b) => MoveTo(e, b, 3, 0),
            VerifyAfter = (e, b) =>
                e.__TestIsComposing() ? "未確定が取り消されない" : CaretAt(e, At(b, 3, 0)),
        },
        CaretMove("theme-then-move", Expect.Skip, CurLine + 1, CurCol, s => s.Theme = DarkTheme),
        new("theme-change", Expect.Paint)
        {
            Arrange = (e, b) => MoveTo(e, b, CurLine, CurCol),
            Arranged = b => Caret(b, CurLine, CurCol),
            Act = (e, _) => e.ApplyAppearance(new AppSettings { Theme = DarkTheme }),
            VerifyAfter = (e, b) => CaretAt(e, At(b, CurLine, CurCol)),
        },
        new("control-stale", Expect.Stale)
        {
            Arrange = (e, b) => MoveTo(e, b, CurLine, CurCol),
            Arranged = b => Caret(b, CurLine, CurCol),
            Act = (e, _) =>
            {
                e.ShowWhitespace = true;
                // 保留中の無効領域を取り消す = 描き直さない。画面は A のまま残るはず。
                ValidateRect(e.Handle, 0);
            },
            VerifyAfter = (e, _) => e.ShowWhitespace ? null : "ShowWhitespace が効かない",
        },
    ];

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        nint hdcDest,
        int x,
        int y,
        int width,
        int height,
        nint hdcSrc,
        int xSrc,
        int ySrc,
        int rop
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ValidateRect(nint hwnd, nint rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUpdateRect(
        nint hwnd,
        nint rect,
        [MarshalAs(UnmanagedType.Bool)] bool erase
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(nint hwnd, nint rcUpdate, nint hrgnUpdate, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    /// <summary>エディタの描画回数(Paint イベント)。</summary>
    private static int s_paints;

    /// <summary>メッセージ処理中(描画中など)に起きた最初の例外。</summary>
    private static Exception? s_error;

    /// <summary>撮った絵(画素は 32bpp ARGB・行優先)。</summary>
    private sealed record Shot(int Width, int Height, int[] Pixels);

    public static int Run(string[] args)
    {
        bool expectSkip = false;
        string? outDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--expect-skip":
                    expectSkip = true;
                    break;
                case "--out"
                    when i + 1 < args.Length
                        && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    outDir = Path.GetFullPath(args[++i]);
                    break;
                default:
                    Console.Error.WriteLine($"--paint-transition の引数を解釈できない: {args[i]}");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        try
        {
            return RunAll(expectSkip, outDir);
        }
        catch (Exception e)
            when (e is IOException or UnauthorizedAccessException or ExternalException)
        {
            Console.Error.WriteLine($"[失敗] {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static int RunAll(bool expectSkip, string? outDir)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => s_error ??= e.Exception;

        var form = new Form
        {
            Text = "kxEdit.Editor.Smoke --paint-transition",
            Width = 640,
            Height = 420,
            StartPosition = FormStartPosition.Manual,
            // 画面外の窓には WM_PAINT が来ない(PaintSnapshot と同じ理由)。
            Location = Screen.PrimaryScreen?.WorkingArea.Location ?? Point.Empty,
            ShowInTaskbar = false,
        };
        // フォーカスの受け皿(クライアント領域の外)。editor と sink は form の破棄で一緒に破棄される。
        var sink = new Button { Location = new Point(-200, -200), Size = new Size(10, 10) };
        var editor = new EditorControl { Dock = DockStyle.Fill };
        form.Controls.Add(editor);
        form.Controls.Add(sink);
        form.Show();
        form.ActiveControl = sink;
        editor.Paint += (_, _) => s_paints++;
        Application.DoEvents();

        int failed = 0;
        try
        {
            CheckNoError("起動");
            var body = PaintSnapshot.BuildBody();
            Check(
                Screen.FromControl(form).WorkingArea.Contains(form.Bounds),
                $"窓 {form.Bounds} が画面の作業領域に収まらない(WM_PAINT が来ない)"
            );
            Console.WriteLine(
                $"DeviceDpi={editor.DeviceDpi} ClientSize={editor.ClientSize.Width}x{editor.ClientSize.Height} "
                    + $"expect-skip={(expectSkip ? "on" : "off")} 遷移数={Transitions.Length}"
            );
            foreach (var t in Transitions)
            {
                if (!RunOne(form, editor, body, t, expectSkip, outDir))
                    failed++;
            }
        }
        catch (PaintSnapshotException e)
        {
            Console.Error.WriteLine($"[自己チェック失敗] {e.Message}");
            return 1;
        }
        finally
        {
            PaintSnapshot.CloseQuietly(form);
        }
        Console.WriteLine(
            failed == 0
                ? $"合計: {Transitions.Length} 遷移すべて期待どおり EXIT 0"
                : $"合計: {Transitions.Length} 遷移中 {failed} 件が失敗 EXIT 1"
        );
        return failed == 0 ? 0 : 1;
    }

    /// <summary>1 遷移を走らせて結果を 1 行出す。期待どおりなら true。自己チェックの失敗は例外。</summary>
    private static bool RunOne(
        Form form,
        EditorControl editor,
        Body body,
        Transition t,
        bool expectSkip,
        string? outDir
    )
    {
        string name = t.Name;
        var settings = new AppSettings();
        t.Configure?.Invoke(settings);
        editor.ApplyAppearance(settings);
        editor.SetOrReplaceSource(TextBuffer.FromString(body.Text));
        t.Arrange(editor, body);
        CheckNoError(name);

        var (anchor, caret) = t.Arranged(body);
        Check(
            editor.SelectionAnchor == anchor && editor.CaretCharOffset == caret,
            $"{name}: 準備の後の位置が想定外(anchor={editor.SelectionAnchor}・caret={editor.CaretCharOffset}・期待 {anchor}/{caret})"
        );
        Check(
            editor.TopLine == 0 && editor.ScrollX == 0,
            $"{name}: 準備の後のスクロール位置が想定外(TopLine={editor.TopLine}・ScrollX={editor.ScrollX})"
        );
        Check(
            editor.__TestIsComposing() == t.Composing,
            $"{name}: 準備の後の未確定の有無が想定外({editor.__TestIsComposing()})"
        );
        Check(!editor.Focused, $"{name}: EditorControl がフォーカスを持った(キャレットが写り込む)");

        RedrawFull(form, $"{name}(状態 A)");
        var a = Capture(editor, $"{name}(A)");

        int p0 = s_paints;
        t.Act(editor, body);
        Application.DoEvents();
        _ = DwmFlush(); // 失敗しても致命ではない
        CheckNoError(name);
        int paints = s_paints - p0;
        if (t.Expect != Expect.Stale)
        {
            Check(
                !GetUpdateRect(editor.Handle, 0, false),
                $"{name}: メッセージを流した後も保留中の無効領域が残る(描画が配送されていない)"
            );
        }
        string? after = t.VerifyAfter?.Invoke(editor, body);
        Check(after is null, $"{name}: {after}");

        var x = Capture(editor, $"{name}(X)");
        RedrawFull(form, $"{name}(正解 Y)");
        var y = Capture(editor, $"{name}(Y)");
        RedrawFull(form, $"{name}(正解 Y')");
        var y2 = Capture(editor, $"{name}(Y')");
        var (detDiff, _) = Diff(y, y2);
        Check(
            detDiff == 0,
            $"{name}: 全面を描き直した 2 枚が {detDiff} 画素違う(撮影が決定的でない)"
        );

        // 遷移の表の自己チェック: Paint は絵が変わる遷移、Skip は絵が変わらない遷移でなければ意味がない。
        long changed = Diff(a, y).Count;
        Check(
            t.Expect != Expect.Paint || changed != 0,
            $"{name}: Paint の遷移なのに、正解の絵が状態 A と同じ(遷移の定義が誤り)"
        );
        Check(
            t.Expect != Expect.Skip || changed == 0,
            $"{name}: Skip の遷移なのに、正解の絵が状態 A と {changed} 画素違う(遷移の定義が誤り)"
        );

        var (diff, first) = Diff(x, y);
        string result = diff == 0 ? "一致" : $"差 {diff} 画素(最初の座標 ({first.X},{first.Y}))";
        string? failure = t.Expect switch
        {
            Expect.Stale when paints != 0 => "陽性対照で描画が起きた(ValidateRect が効かない)",
            Expect.Stale when diff == 0 => "陽性対照で差が出ない(撮り方が画面の絵を読めていない)",
            Expect.Stale when Diff(x, a).Count != 0 =>
                "陽性対照の X が状態 A の絵と違う(撮り方が画面の絵を読めていない)",
            Expect.Stale => null,
            _ when diff != 0 => "古い絵が残る",
            Expect.Paint when paints == 0 => "描き直しが要るのに描画 0 回",
            Expect.Skip when expectSkip && paints != 0 => "描画の省略を期待したが描いた",
            _ => null,
        };
        string label = t.Expect switch
        {
            Expect.Stale => "陽性対照",
            _ => t.Expect.ToString(),
        };
        Console.WriteLine(
            $"{name}: 描画 {paints} 回・{result} [{label}]"
                + (failure is null ? "" : $" → 失敗: {failure}")
        );

        if (failure is not null && outDir is not null)
        {
            Directory.CreateDirectory(outDir);
            SavePng(x, Path.Combine(outDir, $"{name}-x.png"));
            SavePng(y, Path.Combine(outDir, $"{name}-y.png"));
        }
        return failure is null;
    }

    /// <summary>
    /// フォームと子(エディタ・スクロールバー)を同期で全面描き直す。エディタの描画が実際に起きたことを
    /// 自己チェックする(起きなければ、この後に撮る絵は「正解」ではない)。
    /// </summary>
    private static void RedrawFull(Form form, string what)
    {
        int p0 = s_paints;
        RedrawWindow(form.Handle, 0, 0, RdwInvalidate | RdwErase | RdwAllChildren | RdwUpdateNow);
        Application.DoEvents();
        _ = DwmFlush();
        CheckNoError(what); // 描画回数より先に見る(PaintSnapshot.ShootOne と同じ理由)
        Check(s_paints > p0, $"{what}: 全面の描き直しで WM_PAINT が配送されない(画面がロック中?)");
    }

    /// <summary>
    /// エディタのクライアント領域の今の絵を、描画を起こさずに撮る(クラス doc の「撮り方」)。
    /// 撮影の前後で描画回数が変わらないことを自己チェックする。
    /// </summary>
    private static Shot Capture(EditorControl editor, string what)
    {
        var size = editor.ClientSize;
        // 32bppRgb に BitBlt し、読むときに ARGB へ変換する(アルファは常に 0xFF になる)。
        using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
        int p0 = s_paints;
        nint src = GetDC(editor.Handle);
        Check(src != 0, $"{what}: GetDC が失敗した");
        try
        {
            using var g = Graphics.FromImage(bmp);
            nint dst = g.GetHdc();
            try
            {
                if (!BitBlt(dst, 0, 0, size.Width, size.Height, src, 0, 0, SrcCopy))
                    throw new PaintSnapshotException(
                        $"{what}: BitBlt が失敗した(Win32 {Marshal.GetLastWin32Error()})"
                    );
            }
            finally
            {
                g.ReleaseHdc(dst);
            }
        }
        finally
        {
            _ = ReleaseDC(editor.Handle, src);
        }
        CheckNoError(what);
        Check(
            s_paints == p0,
            $"{what}: 撮影で WM_PAINT が起きた(描画を起こさない撮り方の前提が崩れた)"
        );
        return new Shot(size.Width, size.Height, PaintSnapshot.ReadPixels(bmp));
    }

    /// <summary>異なる画素の数と、最初の座標。大きさが違えば全画素を差とする。</summary>
    private static (long Count, Point First) Diff(Shot a, Shot b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            return (Math.Max(a.Pixels.Length, b.Pixels.Length), Point.Empty);
        long count = 0;
        Point? first = null;
        for (int i = 0; i < a.Pixels.Length; i++)
        {
            if (a.Pixels[i] == b.Pixels[i])
                continue;
            count++;
            first ??= new Point(i % a.Width, i / a.Width);
        }
        return (count, first ?? Point.Empty);
    }

    private static void SavePng(Shot shot, string path)
    {
        using var bmp = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(
            new Rectangle(0, 0, shot.Width, shot.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb
        );
        try
        {
            for (int row = 0; row < shot.Height; row++)
                Marshal.Copy(
                    shot.Pixels,
                    row * shot.Width,
                    data.Scan0 + (row * data.Stride),
                    shot.Width
                );
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        bmp.Save(path, ImageFormat.Png);
    }

    private static void CheckNoError(string what)
    {
        if (s_error is not null)
            throw new PaintSnapshotException($"{what}: メッセージ処理中に例外: {s_error}");
    }

    private static void Check(bool ok, string message)
    {
        if (!ok)
            throw new PaintSnapshotException(message);
    }
}
