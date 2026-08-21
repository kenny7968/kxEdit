using System.Diagnostics;
using System.Text;
using kxEdit.Core.Buffers;
using kxEdit.Editor;

namespace kxEdit.Editor.Smoke;

/// <summary>
/// 2026-08-02 巨大 1 行調査(docs/plans/2026-08-02-large-line-triage-design.md §4.2)。
/// 空白・改行を一切含まない単一長大行を実 <see cref="EditorControl"/> へ載せ、
/// バッファ差し込みと初回描画の実時間を GDI 込みで測る。
/// GDI 抜きの構造コストは <c>kxEdit.Core.Bench --largeline</c> が対で、
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
            Text = "kxEdit.Editor.Smoke --largeline",
            Width = 900,
            Height = 700,
            StartPosition = FormStartPosition.Manual,
            // 完全に画面外 (-32000,-32000) のウィンドウは可視領域が空になり
            // Update()(UpdateWindow)が WM_PAINT を配送しない=経路 ③ を測り落とす。
            // 画面内に置くこと自体が測定条件である(GdiBench も同じ理由で画面内)。
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

        foreach (string kind in new[] { "ascii", "cjk", "mixed", "cjkwide", "emoji" })
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
    /// <remarks>
    /// <paramref name="kind"/> ごとの狙い。
    /// <list type="table">
    /// <item>
    ///   <term>ascii</term>
    ///   <description>a-z の 26 種。<c>GdiCharMetrics</c> の ASCII 配列加算だけを通る基準線。</description>
    /// </item>
    /// <item>
    ///   <term>cjk</term>
    ///   <description>U+3042 から 40 種。<b>異なるコードポイントが 40 種しかない</b>ため、
    ///   幅メモ化(設計書 §4.1 変更 A)の初回コストが実文書より小さく出る点に注意
    ///   (日本語の実文書は 2,000 種程度=<c>cjkwide</c> が実文書相当)。</description>
    /// </item>
    /// <item>
    ///   <term>mixed</term>
    ///   <description>ascii と cjk を半々に混ぜる。非 ASCII を 1 文字でも含むと
    ///   複数文字 run が GDI へ落ちる経路の確認用。</description>
    /// </item>
    /// <item>
    ///   <term>cjkwide</term>
    ///   <description>CJK 統合漢字 U+4E00〜U+55FF の 2,048 種。日本語の実文書に近い文字種数で
    ///   幅メモ化の初回コストを測る。<c>cjk</c> との差が「文字種数の効果」になる。</description>
    /// </item>
    /// <item>
    ///   <term>emoji</term>
    ///   <description>U+1F600〜U+1F64F の 80 種=<b>サロゲートペア</b>。<c>LineLayout</c> の
    ///   <c>TextBoundary.CodePointLengthAt</c> と <c>GdiCharMetrics</c> の UTF-32 キー経路を踏ませる。</description>
    /// </item>
    /// </list>
    /// <b>ascii / cjk / mixed の生成規則は変更しないこと。</b>
    /// 調査(2026-08-02-large-line-resilience-design.md §2.3)および Task 2 / Task 4 の
    /// 測定値との前後比較が成立しなくなる。
    /// <para>
    /// <b>emoji の注意</b>: 1 コードポイント = 2 char のため <paramref name="chars"/> は char 数であって
    /// コードポイント数ではない。末尾の <c>ToString(0, chars)</c> がサロゲートペアの中間で切ることが
    /// あるが、<c>LineLayout</c> は単独サロゲートを 1 code-unit として扱う契約
    /// (<c>LineLayoutTests.Lone_high_surrogate_is_treated_as_single_code_unit</c>)なので測定は成立する。
    /// </para>
    /// </remarks>
    private static string MakeSingleLine(int chars, string kind)
    {
        var sb = new StringBuilder(chars);
        var r = new Random(20260802);
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
}
