using System.Diagnostics;
using System.Text;
using yEdit.Core.Buffers;
using yEdit.Editor;

namespace yEdit.Editor.Smoke;

/// <summary>
/// 2026-08-02 巨大 1 行調査(docs/plans/2026-08-02-large-line-triage-design.md §4.2)。
/// 空白・改行を一切含まない単一長大行を実 <see cref="EditorControl"/> へ載せ、
/// バッファ差し込みと初回描画の実時間を GDI 込みで測る。
/// GDI 抜きの構造コストは <c>yEdit.Core.Bench --largeline</c> が対で、
/// 両者の差分が <see cref="GdiCharMetrics"/> の GDI 呼び出しコストになる。
/// 調査用のため判定ゲートは持たない(常に EXIT 0)。
/// </summary>
/// <remarks>
/// 測定対象は設計書 §2.1 で確定した 3 経路。
/// <list type="number">
/// <item><c>UpdateHorizontalScrollbar</c>(折り返し OFF のみ)= 行全体を string 化して
/// <c>MeasureRun</c> へ一括投入</item>
/// <item><c>ComputeCaretPoint</c>(フォーカス時のみ)= 行全体を <c>GetText</c> + <c>Wrap</c></item>
/// <item><c>OnPaint</c> → <c>ViewportLayout.Build</c> = フレームごと(キャッシュなし)</item>
/// </list>
/// ①② は <c>SetOrReplaceSource</c> 内で走るため setSource 側に、③ は
/// <c>Invalidate</c> + <c>Update</c>(同期 paint)側に計上される。
/// </remarks>
internal static class LargeLineBench
{
    /// <summary>1 条件がこの秒数を超えたら、その文字種 / 折り返し設定の以降の行長をスキップする。</summary>
    private const double SkipThresholdSec = 30.0;

    public static int Run()
    {
        ApplicationConfiguration.Initialize();
        using var form = new Form
        {
            Text = "yEdit.Editor.Smoke --largeline",
            Width = 900,
            Height = 700,
            StartPosition = FormStartPosition.Manual,
            // GdiBench は (-32000,-32000) の完全オフスクリーンに置くが、その位置では
            // ウィンドウの可視領域が空になり Update()(UpdateWindow)が WM_PAINT を
            // 配送しない=経路 ③ を測り落とす。画面内に置くこと自体が測定条件である。
            Location = new Point(100, 100),
            ShowInTaskbar = false,
        };
        using var editor = new EditorControl { Dock = DockStyle.Fill };
        form.Controls.Add(editor);
        form.Show(); // ハンドル生成(Show しないと Invalidate/Update が no-op)
        // 設計書 §2.1 の申し送り: フォーカスが無いと ComputeCaretPoint(経路 ②)が
        // スキップされ、主犯の 1 つを測り落とす。
        editor.Focus();
        Application.DoEvents();

        Console.WriteLine($"editor.Focused={editor.Focused}(false なら経路 ② を測れていない)");
        Console.WriteLine(
            $"editor.ClientSize={editor.ClientSize}(高さ 0 なら経路 ③ を測れていない)"
        );
        // JIT ウォームアップ(捨て打ち・計測外)。これが無いと最初の 1 条件だけ初回 JIT を
        // 含んで見かけ上遅くなり、100K が 500K より遅いという逆転が表に出る。
        editor.SetOrReplaceSource(TextBuffer.FromString(MakeSingleLine(100_000, "ascii")));
        editor.Invalidate();
        editor.Update();
        Application.DoEvents();

        Console.WriteLine();
        Console.WriteLine("kind,chars,wrapColumns,setSourceMs,paintMsPerFrame,paintReps");

        foreach (string kind in new[] { "ascii", "cjk", "mixed" })
        {
            foreach (int wrap in new[] { 0, 80 })
            {
                foreach (int len in new[] { 100_000, 500_000, 2_000_000 }) // 短い順=閾値超えで残りを捨てる
                {
                    // 前条件の巨大バッファが WrapColumns セッター経由で再レイアウトされるのを
                    // 避けるため、毎回小さいバッファへ戻してから条件を組む(計測外)。
                    editor.SetOrReplaceSource(TextBuffer.FromString("reset"));
                    editor.WrapColumns = wrap;
                    Application.DoEvents();

                    var buf = TextBuffer.FromString(MakeSingleLine(len, kind));

                    var swSet = Stopwatch.StartNew();
                    editor.SetOrReplaceSource(buf);
                    swSet.Stop();

                    // paint を複数回まわして 1 フレームあたりのコストを出す。1 回だけだと
                    // 「無効領域が無くて Update が no-op だった」場合と区別できない。
                    // setSource が既に重い条件では反復すると暴走するため 1 回に落とす。
                    int paintReps = swSet.Elapsed.TotalSeconds > 1.0 ? 1 : 5;
                    var swPaint = Stopwatch.StartNew();
                    for (int p = 0; p < paintReps; p++)
                    {
                        editor.Invalidate();
                        editor.Update(); // 同期 paint
                    }
                    swPaint.Stop();

                    double paintMsPerFrame = swPaint.Elapsed.TotalMilliseconds / paintReps;
                    double totalSec = (swSet.Elapsed + swPaint.Elapsed).TotalSeconds;
                    Console.WriteLine(
                        $"{kind},{len},{wrap},{swSet.Elapsed.TotalMilliseconds:F1},"
                            + $"{paintMsPerFrame:F1},{paintReps}"
                    );

                    if (totalSec > SkipThresholdSec)
                    {
                        Console.WriteLine(
                            $"  → {totalSec:F1}s > {SkipThresholdSec}s のため {kind}/wrap={wrap} の以降の行長をスキップ"
                        );
                        break;
                    }
                }
            }
        }

        form.Close();
        Console.WriteLine();
        Console.WriteLine("(調査用ベンチのため判定ゲートなし) EXIT 0");
        return 0;
    }

    /// <summary>
    /// 空白も改行も含まない 1 行を作る(Core.Bench --largeline と同一の生成規則・同一シード)。
    /// </summary>
    private static string MakeSingleLine(int chars, string kind)
    {
        var sb = new StringBuilder(chars);
        var r = new Random(20260802);
        while (sb.Length < chars)
        {
            sb.Append(
                kind switch
                {
                    "ascii" => (char)('a' + r.Next(26)),
                    "cjk" => (char)('あ' + r.Next(40)),
                    _ => r.Next(2) == 0 ? (char)('a' + r.Next(26)) : (char)('あ' + r.Next(40)),
                }
            );
        }
        return sb.ToString(0, chars);
    }
}
