using System.Diagnostics;
using System.Reflection;
using kxEdit.Accessibility;

namespace kxEdit.Editor.Tests;

/// <summary>
/// perf-followups フェーズ 2(uia-thread-guard): IsHandleCreated と InvokeRequired の間で Handle が
/// 破棄される窓(TOCTOU)でも、UIA の 7 経路が RPC スレッドでエディタ内部に触れないこと。
/// </summary>
/// <remarks>
/// 窓そのものは再現できないので、<c>TestHook_UiaAssumeHandleCreated</c> で Handle のガードを通過させ、
/// Handle 破棄後・未 Dispose・親なし(= worker から見て InvokeRequired が false)の状態で worker から呼ぶ。
/// 窓の中で走ったときと同じ入力になる(<c>UiaScreenCoordinateTests.ComputePaths_AfterHandleDestroyed_DoNotRecreateHandle</c>
/// と同じ考え方)。各テストは、同じ問い合わせを UI スレッドで行う陽性対照で、計算が走れば縮退値と
/// 違う値になることを確かめる(CLAUDE.md §4-B)。
/// </remarks>
public class UiaThreadGuardTests
{
    // 行 0・1 は日本語: 非 ASCII の 1 文字幅は初回だけ GDI で測って幅メモに入る(GdiCharMetrics)。
    // worker で計算が走れば幅メモが増える。行 1 の先頭は 11。
    private const string Text =
        "あいうえおかきくけこ\nさしすせそ\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9";
    private const int Line1Start = 11;
    private const int Line1Offset = Line1Start + 4; // 行 1 の、先頭ではない視覚行に属する位置

    private static (Form f, EditorControl c) MakeAfterHandleDestroyed(int wrap)
    {
        var f = new HostForm();
        var c = new EditorControl { WrapColumns = wrap };
        f.Controls.Add(c);
        _ = f.Handle;
        c.ClientSize = new System.Drawing.Size(400, c.LineHeightPx * 3);
        c.SetSource(TextBuffer.FromString(Text));
        // 親から外す(親が Handle を持っていると、InvokeRequired は親の Handle で判定して true になる)。
        f.Controls.Remove(c);
        typeof(Control)
            .GetMethod("DestroyHandle", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(c, null);
        Assert.False(c.IsHandleCreated); // fixture 前提
        Assert.False(c.IsDisposed);
        Assert.Null(c.Parent);
        c.TestHook_UiaAssumeHandleCreated = true;
        return (f, c);
    }

    /// <summary>
    /// worker で <paramref name="query"/> を実行し、そのときの InvokeRequired と結果を返す。
    /// 万一 Invoke に入った場合に備えて、UI スレッドで汲みながら待つ(上限つき)。
    /// </summary>
    private static (bool InvokeRequired, T Result) OnWorker<T>(EditorControl c, Func<T> query)
    {
        var t = System.Threading.Tasks.Task.Run(() => (c.InvokeRequired, query()));
        var sw = Stopwatch.StartNew();
        while (!t.IsCompleted && sw.ElapsedMilliseconds < 5000)
            Application.DoEvents();
        Assert.True(t.IsCompleted, "worker が終わらない");
        return t.Result;
    }

    private static int MemoCount(EditorControl c) =>
        ((GdiCharMetrics)c.Metrics).TestHook_NonAsciiWidthCount;

    [Fact]
    public void LineStartOf_FromWorkerAfterHandleDestroyed_FallsBackWithoutMeasuring() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeAfterHandleDestroyed(wrap: 2);
            using (f)
            using (c)
            {
                var host = (IUiaTextHost)c;
                int memo0 = MemoCount(c);

                var (invokeRequired, start) = OnWorker(c, () => host.LineStartOf(Line1Offset));
                Assert.False(invokeRequired); // fixture 前提: 窓の中と同じく InvokeRequired=false
                Assert.Equal(Line1Start, start); // 縮退値 = 論理行の先頭
                Assert.Equal(memo0, MemoCount(c));

                // 陽性対照: UI スレッドでは計算が走り、視覚行の先頭を返し、幅メモが増える。
                Assert.True(
                    host.LineStartOf(Line1Offset) > Line1Start,
                    "前提: 視覚行の先頭は論理行の先頭と違う"
                );
                Assert.True(MemoCount(c) > memo0, "前提: 計算が走れば幅メモが増える");
            }
        });

    [Fact]
    public void GetVisibleRange_FromWorkerAfterHandleDestroyed_ReturnsEmpty() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeAfterHandleDestroyed(wrap: 2);
            using (f)
            using (c)
            {
                var host = (IUiaTextHost)c;
                int memo0 = MemoCount(c);

                var (invokeRequired, range) = OnWorker(c, () => host.GetVisibleRange());
                Assert.False(invokeRequired);
                Assert.Equal((0, 0), range);
                Assert.Equal(memo0, MemoCount(c));

                // 陽性対照: UI スレッドでは (0, 0) 以外になる。
                Assert.NotEqual((0, 0), host.GetVisibleRange());
            }
        });

    [Fact]
    public void SetSelection_FromWorkerAfterHandleDestroyed_IsDropped() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeAfterHandleDestroyed(wrap: 2);
            using (f)
            using (c)
            {
                var host = (IUiaTextHost)c;
                host.SetSelection(3, 5); // 非既定位置から始める(UI スレッドなのでその場で走る)
                Assert.Equal((3, 5), host.GetSelection()); // 前提

                var (invokeRequired, _) = OnWorker(
                    c,
                    () =>
                    {
                        host.SetSelection(0, 1);
                        return 0;
                    }
                );
                Assert.False(invokeRequired);
                Application.DoEvents(); // 万一投函されていたら、ここで走らせて検出する
                Assert.Equal((3, 5), host.GetSelection());

                // 陽性対照: UI スレッドでは選択が変わる。
                host.SetSelection(0, 1);
                Assert.Equal((0, 1), host.GetSelection());
            }
        });

    [Fact]
    public void ScrollRangeIntoView_FromWorkerAfterHandleDestroyed_IsDropped() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeAfterHandleDestroyed(wrap: 2);
            using (f)
            using (c)
            {
                var host = (IUiaTextHost)c;
                int last = host.TextLength;
                Assert.Equal(0, c.TopLine); // 前提

                var (invokeRequired, _) = OnWorker(
                    c,
                    () =>
                    {
                        host.ScrollRangeIntoView(last - 1, last, alignToTop: true);
                        return 0;
                    }
                );
                Assert.False(invokeRequired);
                Application.DoEvents();
                Assert.Equal(0, c.TopLine);

                // 陽性対照: UI スレッドでは末尾行が見えるようにスクロールする。
                host.ScrollRangeIntoView(last - 1, last, alignToTop: true);
                Assert.True(c.TopLine > 0, "前提: 計算が走ればスクロールする");
            }
        });

    // GetBoundingRectangles / OffsetFromScreenPoint / SetFocus は、本体が _hwnd==0(破棄後)で
    // 先に抜けるか、Handle が無いと何もしないので、縮退値と計算結果を値では区別できない
    // (設計書 §6 の表の「無害化済み」・「実害なし」)。ここでは worker から呼んで例外が出ず、
    // 縮退値が返ることだけを確かめる。
    [Fact]
    public void HarmlessPaths_FromWorkerAfterHandleDestroyed_ReturnFallbackWithoutThrowing() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeAfterHandleDestroyed(wrap: 2);
            using (f)
            using (c)
            {
                var host = (IUiaTextHost)c;
                int memo0 = MemoCount(c);

                var (invokeRequired, (rects, offset)) = OnWorker(
                    c,
                    () =>
                    {
                        var r = host.GetBoundingRectangles(0, 5);
                        int o = host.OffsetFromScreenPoint(10, 10);
                        host.SetFocus();
                        return (r, o);
                    }
                );
                Application.DoEvents();
                Assert.False(invokeRequired);
                Assert.Empty(rects);
                Assert.Equal(0, offset);
                Assert.Equal(memo0, MemoCount(c));
                Assert.False(c.IsHandleCreated, "worker の呼び出しが Handle を作り直した");
            }
        });
}
