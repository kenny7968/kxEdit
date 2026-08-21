using System.Diagnostics;
using System.Text;
using kxEdit.Core.Buffers;
using kxEdit.Editor;

namespace kxEdit.Editor.Smoke;

/// <summary>
/// P2 Task 14 の GDI 実測ベンチ。画面内 Form + Show + Invalidate/Update を
/// 1000 フレーム回して平均フレーム時間を測る。純レイアウトのベンチ
/// (Core.Bench --layout)と対になる「実描画 GDI 経路」の計測。
/// 目標: 平均 &lt; 16ms(60fps)。EXIT 0 で PASS・1 で FAIL。
/// </summary>
internal static class GdiBench
{
    /// <summary>実描画ベンチのエントリ。<c>--mb &lt;size&gt;</c> で文書サイズ(既定 256MB・環境依存で 1GB は厳しい)を指定。</summary>
    public static int Run(string[] args)
    {
        int mb = 256;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--mb" && int.TryParse(args[i + 1], out int m))
                mb = m;
        }
        long targetBytes = (long)mb * 1024L * 1024L;

        Console.WriteLine($"GDI smoke ベンチ開始 (--mb {mb})");
        var swBuild = Stopwatch.StartNew();
        var buffer = BuildBuffer(targetBytes);
        swBuild.Stop();
        var snap = buffer.Current;
        Console.WriteLine(
            $"構築 {swBuild.Elapsed.TotalSeconds:F1}s / {snap.CharLength:N0} 文字 / {snap.LineCount:N0} 行"
        );

        // WinForms アプリコンテキスト初期化(Show でハンドル生成 → Invalidate/Update が同期 paint。
        // 画面内に置く理由は下記)。
        ApplicationConfiguration.Initialize();
        using var form = new Form
        {
            Text = "kxEdit.Editor.Smoke --bench",
            Width = 900,
            Height = 700,
        };
        using var editor = new EditorControl { Dock = DockStyle.Fill };
        form.Controls.Add(editor);
        // ハンドル生成のため Show が必須(Show しないと Invalidate/Update が no-op)。
        // 画面内に置くことが測定条件である: 完全に画面外 (-32000,-32000) のウィンドウは
        // 可視領域が空になり、Update()(UpdateWindow)が WM_PAINT を配送しない
        // = 描画していない値で 16ms ゲートを通してしまう
        // (docs/plans/2026-08-02-large-line-resilience-design.md §2.3 で
        //  同条件の paint が 1.0 ms → 33.2 ms に変わることを確認済み)。
        // ShowInTaskbar=false でタスクバーには出さないが、ウィンドウ自体は見える。
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(100, 100);
        form.ShowInTaskbar = false;
        form.Show();
        editor.SetSource(buffer);
        Application.DoEvents();

        var rnd = new Random(20260705);
        const int Iterations = 1000;
        double totalMs = 0;
        double maxMs = 0;
        var swAll = Stopwatch.StartNew();
        for (int i = 0; i < Iterations; i++)
        {
            editor.TopLine = rnd.Next(0, snap.LineCount);
            var sw = Stopwatch.StartNew();
            editor.Invalidate();
            editor.Update(); // 同期 paint
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds;
            totalMs += ms;
            if (ms > maxMs)
                maxMs = ms;
        }
        swAll.Stop();

        double avgMs = totalMs / Iterations;
        Console.WriteLine(
            $"GDI 平均フレーム時間: {avgMs:F2} ms (max {maxMs:F2} ms・{Iterations} frames / 合計 {swAll.Elapsed.TotalSeconds:F1}s)"
        );
        Console.WriteLine($"目標: <16ms  判定: {(avgMs < 16 ? "PASS" : "FAIL")}");

        form.Close();
        return avgMs < 16 ? 0 : 1;
    }

    /// <summary>
    /// Core.Bench と同じテンプレ(ASCII+日本語+絵文字混在)で targetBytes まで積み上げた TextBuffer。
    /// テンプレはコード点/改行を割らない 1MB ブロックにパッキングしてから流し込むため、
    /// pieceCount はブロック数 + 1 程度に収まる(1GB でも 1000〜1050 個)。
    /// </summary>
    private static TextBuffer BuildBuffer(long targetBytes)
    {
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
            block = b[..w];
        }
        var builder = new TextBufferBuilder();
        long written = 0;
        while (written < targetBytes)
        {
            int len = (int)Math.Min(block.Length, targetBytes - written);
            builder.Add(block.AsSpan(0, len));
            written += len;
        }
        return builder.Build();
    }
}
