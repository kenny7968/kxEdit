using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using kxEdit.Core.Buffers;
using kxEdit.Editor;

namespace kxEdit.Editor.Smoke;

/// <summary>
/// 2026-08-22 A-6(視覚行スクロール)の性能確認。CJK 単一長大行を折り返し ON で載せ、
/// 「巨大段落の途中まで進んだ状態」のコストを 3 系統で測る
/// (実装計画 docs/plans/2026-08-22-wrap-vertical-navigation.md Task 7)。
/// <list type="number">
/// <item><b>1 フレーム描画</b>= 設計書 §5-2 が受容した O(topSegment)/フレーム。
/// <c>ViewportLayout.Build</c> が先頭 <c>topSegment</c> 本を読み飛ばすために払う。</item>
/// <item><b>↓ 1 打鍵</b>= 折り返し ON では <c>LineLayout.WrapThroughOffset</c> が
/// <b>1 打鍵あたり 2 回</b>増える(<c>BringCaretIntoView</c> が
/// <c>SetCaretCharOffset</c> 内と <c>InputRouter.ApplyNavMove</c> から<b>2 回</b>呼ばれる。
/// 本ブランチ以前からの構造)。起点が実際に動く打鍵では <c>SetTopPosition</c> →
/// <c>PositionCaret</c> → <c>ComputeCaretPoint</c> でさらに 1 回=<b>計最大 3 回</b>。</item>
/// <item><b>ホイール 1 ノッチ</b>= 下方向は <c>WalkForwardVisualRows</c> →
/// <c>SegmentCountCapped(snap, line, seg + n + 1)</c> を通るため <b>O(topSegment)</b>、
/// 上方向は同一論理行内なら <c>WalkBackVisualRows</c> の <c>seg &gt;= n</c> で即 return =
/// <b>O(1)</b>。この非対称を数値で出すのが目的。</item>
/// </list>
/// 判定ゲートは持たない(常に EXIT 0)。対になる基準値は PR #35 の 30.1 ms/フレーム
/// (CJK 500K・折り返し ON・先頭表示)。
/// </summary>
/// <remarks>
/// <para>
/// <b>測定条件</b>。(1) Form は<b>画面内</b>に置く。完全に画面外のウィンドウには
/// <c>Update()</c>(UpdateWindow)が WM_PAINT を配送せず、描画していない値を測ってしまう
/// (<see cref="GdiBench"/> / <see cref="LargeLineBench"/> と同じ理由)。
/// (2) <c>editor.Focus()</c> が必須。<c>PositionCaret</c> は <c>_hasFocus</c> で早期 return するため、
/// フォーカスが無いと ↓ 1 打鍵の<b>3 回目</b>の Wrap を測り落とす。
/// </para>
/// <para>
/// <b>本文の生成規則</b>は 'あ' から 40 種の巡回。<see cref="LargeLineBench"/> の <c>cjk</c> と
/// 同じ文字種数のため <c>GdiCharMetrics</c> の幅メモ化コストは実文書(2,000 種程度)より
/// 小さく出る。ここで見たいのは「セグメントを読み飛ばす本数に比例するか」なので、
/// 文字種数の効果は測定対象ではない。
/// </para>
/// <para>
/// <b>どの測定にも乗る定数コスト</b>: <c>ComputeCaretPoint</c> と <c>LineTextOf</c> は
/// 論理行の全文を 1 回 string 化する(500K char = 約 1MB)。深さに依存しないので
/// 「深さに対する増え方」の観察は成立するが、絶対値にはこの定数が含まれている。
/// </para>
/// </remarks>
internal static class WrapScrollBench
{
    /// <summary>折り返し桁数(半角換算)。</summary>
    private const int WrapCols = 80;

    /// <summary>単一論理行の長さ(char)。</summary>
    private const int LineChars = 500_000;

    /// <summary>測る深さ(視覚行 index)。100 ms/frame の判定は末尾の 5000 で行う。</summary>
    private static readonly int[] Depths = new[] { 0, 100, 1000, 5000 };

    // reason: protected な OnKeyDown / OnMouseWheel を叩くのが「1 打鍵 / 1 ノッチ」の唯一の入口。
    // kxEdit.Editor.Tests(KeyboardNavigationTests / VisualRowScrollTests 等)と同一の流儀で、
    // SonarAnalyzer は本物のテストプロジェクトでは S3011 を上げない(本 Smoke は test SDK を
    // 参照しないハーネスなので上がる)。外部入力は扱わない手動ベンチ限定の局所抑止。
#pragma warning disable S3011 // Make sure that this accessibility bypass is safe here
    private static readonly MethodInfo KeyDownMethod =
        typeof(EditorControl).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("EditorControl に protected OnKeyDown が無い");

    private static readonly MethodInfo WheelMethod =
        typeof(EditorControl).GetMethod(
            "OnMouseWheel",
            BindingFlags.Instance | BindingFlags.NonPublic
        ) ?? throw new InvalidOperationException("EditorControl に protected OnMouseWheel が無い");
#pragma warning restore S3011

    public static int Run()
    {
        ApplicationConfiguration.Initialize();
        using var form = new Form
        {
            Text = "kxEdit.Editor.Smoke --wrapscroll",
            Width = 900,
            Height = 700,
            StartPosition = FormStartPosition.Manual,
            // 画面外ウィンドウには WM_PAINT が来ない=描画を測り落とす(GdiBench と同じ理由)。
            Location = new Point(100, 100),
            ShowInTaskbar = false,
        };
        using var editor = new EditorControl { Dock = DockStyle.Fill, WrapColumns = WrapCols };
        form.Controls.Add(editor);
        form.Show(); // ハンドル生成(Show しないと Invalidate/Update が no-op)
        editor.Focus(); // PositionCaret を通す(remarks 参照)
        Application.DoEvents();

        var sb = new StringBuilder(LineChars);
        for (int i = 0; i < LineChars; i++)
            sb.Append((char)('あ' + (i % 40)));
        editor.SetSource(TextBuffer.FromString(sb.ToString()));
        Application.DoEvents();

        int wheelLines = SystemInformation.MouseWheelScrollLines;
        if (wheelLines <= 0)
            wheelLines = 3; // OnMouseWheel と同じフォールバック(「1 ページ」設定は -1)
        Console.WriteLine(
            $"editor.Focused={editor.Focused}(false なら ↓ 1 打鍵の 3 回目を測れていない)"
        );
        Console.WriteLine(
            $"editor.ClientSize={editor.ClientSize} / LineHeightPx={editor.LineHeightPx}"
                + $"(高さ 0 なら描画を測れていない)"
        );
        Console.WriteLine(
            $"MouseWheelScrollLines={SystemInformation.MouseWheelScrollLines} → 1 ノッチ={wheelLines} 視覚行"
        );

        // ウォームアップ(JIT + 幅メモ化 + 初回 GetText の確保を計測から外す)。
        editor.SetTopPosition(0, Depths[^1]);
        editor.Invalidate();
        editor.Update();
        editor.SetTopPosition(0, 0);
        editor.SetCaretCharOffset(0);
        Application.DoEvents();

        // 駆動の自己チェック兼キャリブレーション。リフレクション経由の呼び出しが実際に効いて
        // いないと「何もしていない時間」を 1 打鍵 / 1 ノッチとして報告してしまう。
        // ついでに 1 視覚行あたりの文字数を実測で決める。全角 1 文字が半角 2 桁ぶんとは限らない
        // (実測ではフォント依存で 1 視覚行 = 60 文字だった)ため、定数で置くと深さの目盛りが狂う。
        int caretBefore = editor.CaretCharOffset;
        KeyDown(editor, Keys.Down);
        int charsPerRow = Math.Max(1, editor.CaretCharOffset - caretBefore);
        Console.WriteLine(
            $"自己チェック ↓ 1 打鍵: CaretCharOffset {caretBefore} → {editor.CaretCharOffset}"
                + "(変化しないなら OnKeyDown 経路を測れていない)"
        );
        editor.SetTopPosition(0, Depths[1]);
        int topBeforeWheel = editor.TopSegment;
        Wheel(editor, -120);
        Console.WriteLine(
            $"自己チェック ホイール 1 ノッチ: TopSegment {topBeforeWheel} → {editor.TopSegment}"
                + "(変化しないなら OnMouseWheel 経路を測れていない)"
        );
        Console.WriteLine(
            $"LineChars={LineChars:N0} / WrapColumns={WrapCols} / 実測 1 視覚行={charsPerRow} 文字"
                + $" → 視覚行数={LineChars / charsPerRow:N0}"
        );
        editor.SetCaretCharOffset(0);
        editor.SetTopPosition(0, 0);
        Application.DoEvents();

        MeasurePaint(editor);
        MeasureKeystroke(editor, charsPerRow);
        MeasureWheel(editor, wheelLines);

        form.Close();
        Console.WriteLine();
        Console.WriteLine("(性能観測用ベンチのため判定ゲートなし) EXIT 0");
        return 0;
    }

    /// <summary>① 起点を段階的に深くしながら 1 フレームの描画時間を測る(設計書 §5-2)。</summary>
    private static void MeasurePaint(EditorControl editor)
    {
        const int frames = 20;
        Console.WriteLine();
        Console.WriteLine("== (1) 1 フレーム描画 ==");
        Console.WriteLine("topSegment,msPerFrame");
        foreach (int seg in Depths)
        {
            editor.SetTopPosition(0, seg);
            Application.DoEvents();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < frames; i++)
            {
                editor.Invalidate();
                editor.Update(); // 同期 paint
            }
            sw.Stop();
            Console.WriteLine($"{seg},{sw.Elapsed.TotalMilliseconds / frames:F1}");
        }
    }

    /// <summary>
    /// ② ↓ 1 打鍵あたりの実時間。キャレットを段階的に深い位置へ置いてから連打する。
    /// <c>topBefore</c> と <c>topAfter</c> が同じ行は<b>起点が動かない打鍵</b>
    /// (=Wrap 2 回)、違う行は<b>起点が動く打鍵</b>(=Wrap 3 回)を測っている。
    /// </summary>
    private static void MeasureKeystroke(EditorControl editor, int charsPerRow)
    {
        const int keys = 20;
        Console.WriteLine();
        Console.WriteLine("== (2) ↓ 1 打鍵 ==");
        Console.WriteLine(
            "(nominalSeg は狙いの深さ。1 視覚行の文字数は行内で厳密には一定でないため"
                + "深いほど端数がずれる=実際の深さは topBefore を読むこと)"
        );
        Console.WriteLine("nominalSeg,topBefore,topAfter,msPerKey");
        foreach (int seg in Depths)
        {
            // 深さごとに同じ初期状態から始める。ここを省くと前条件の起点が残り、
            // 最初の 1 打鍵だけが「起点を大きく引き戻す打鍵」になって平均が濁る
            // (SetCaretCharOffset はキャレットが既にその位置なら早期 return するので、
            //  起点は SetTopPosition で明示的に戻す)。
            editor.SetCaretCharOffset(0);
            editor.SetTopPosition(0, 0);
            editor.SetCaretCharOffset(seg * charsPerRow);
            Application.DoEvents();
            int topBefore = editor.TopSegment;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < keys; i++)
                KeyDown(editor, Keys.Down);
            sw.Stop();
            Console.WriteLine(
                $"{seg},{topBefore},{editor.TopSegment},{sw.Elapsed.TotalMilliseconds / keys:F1}"
            );
        }
    }

    /// <summary>
    /// ③ ホイール 1 ノッチ。下方向(<c>Delta &lt; 0</c>)は <c>WalkForwardVisualRows</c> の
    /// O(topSegment)、上方向は <c>WalkBackVisualRows</c> の O(1)。両方が同じ
    /// <c>SetTopPosition</c> → <c>PositionCaret</c> の定数を払うので、差が歩きのコストになる。
    /// </summary>
    private static void MeasureWheel(EditorControl editor, int wheelLines)
    {
        const int notches = 10;
        Console.WriteLine();
        Console.WriteLine($"== (3) ホイール 1 ノッチ(={wheelLines} 視覚行)==");
        Console.WriteLine("topSegment,downMsPerNotch,upMsPerNotch");
        editor.SetCaretCharOffset(0); // キャレット位置由来の差を消す(定数は両方向に等しく乗る)
        foreach (int seg in Depths)
        {
            double down = MeasureNotch(editor, seg, -120, notches);
            double up = MeasureNotch(editor, seg, +120, notches);
            Console.WriteLine($"{seg},{down:F1},{up:F1}");
        }
    }

    /// <summary>起点を <paramref name="seg"/> へ戻してから 1 ノッチぶんだけ測る、を繰り返す。</summary>
    private static double MeasureNotch(EditorControl editor, int seg, int delta, int notches)
    {
        double total = 0;
        for (int i = 0; i < notches; i++)
        {
            editor.SetTopPosition(0, seg); // 計測外(毎回同じ深さから 1 ノッチを測るため)
            Application.DoEvents();
            var sw = Stopwatch.StartNew();
            Wheel(editor, delta);
            sw.Stop();
            total += sw.Elapsed.TotalMilliseconds;
        }
        return total / notches;
    }

    /// <summary>protected な <c>OnKeyDown</c> を 1 回叩く(Editor.Tests と同じ流儀)。</summary>
    private static void KeyDown(EditorControl editor, Keys keys) =>
        InvokeProtected(KeyDownMethod, editor, new KeyEventArgs(keys));

    /// <summary>protected な <c>OnMouseWheel</c> を 1 回叩く。120 単位 = 1 ノッチ。</summary>
    private static void Wheel(EditorControl editor, int delta) =>
        InvokeProtected(WheelMethod, editor, new MouseEventArgs(MouseButtons.None, 0, 0, 0, delta));

    /// <summary>
    /// リフレクション呼び出しの <see cref="TargetInvocationException"/> を剥がして
    /// 元の例外をそのまま投げ直す(スタックを潰さない)。
    /// </summary>
    private static void InvokeProtected(MethodInfo mi, EditorControl editor, object arg)
    {
        try
        {
            mi.Invoke(editor, new[] { arg });
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw; // 到達しない
        }
    }
}
