using kxEdit.Accessibility;
using kxEdit.Core.Buffers;
using kxEdit.Editor;

namespace kxEdit.Editor.Tests;

/// <summary>
/// P8 Minor-5: 論理行 segs 単一エントリキャッシュの契約テスト。
/// UIA の LineStartOf / LineEndNoBreakOf / LineEnd が同一 (snap, logicalLine, wrap) を
/// 連続照会するホットパスで、LineLayout.Wrap の再アロケーションが 1 回で済むことを
/// TestHook_LastLineSegsHit/MissCount で観測する。
/// </summary>
public class EditorControlCacheTests
{
    private static (Form f, EditorControl c) MakeControl(string text, int wrap)
    {
        var f = new HostForm();
        var c = new EditorControl { WrapColumns = wrap };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    /// <summary>
    /// UI スレッドでメッセージを汲みながら <paramref name="t"/> の完了を待つ(上限つき)。
    /// ワーカーからの Invoke は、UI スレッドが汲まない限り進まない。
    /// </summary>
    private static void PumpUntil(System.Threading.Tasks.Task t, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!t.IsCompleted && sw.ElapsedMilliseconds < timeoutMs)
            Application.DoEvents();
    }

    [Fact]
    public void LastLineSegs_HitsAcrossThreeUiaLineCalls() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("Hello, world!", 20);
            using (f)
            using (c)
            {
                c.TestHook_ResetLastLineSegsCounters();
                var host = (IUiaTextHost)c;
                _ = host.LineStartOf(5);
                _ = host.LineEndNoBreakOf(5);
                _ = host.LineEnd(5);
                Assert.Equal(1, c.TestHook_LastLineSegsMissCount);
                Assert.Equal(2, c.TestHook_LastLineSegsHitCount);
            }
        });

    [Fact]
    public void LastLineSegs_InvalidatesOnEdit() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("Hello, world!", 20);
            using (f)
            using (c)
            {
                var host = (IUiaTextHost)c;
                _ = host.LineStartOf(5);
                _ = host.LineStartOf(5);
                c.TestHook_ResetLastLineSegsCounters();
                c.ReplaceCharRange(0, 0, "X");
                _ = host.LineStartOf(5);
                Assert.Equal(1, c.TestHook_LastLineSegsMissCount);
                Assert.Equal(0, c.TestHook_LastLineSegsHitCount);
            }
        });

    [Fact]
    public void LastLineSegs_InvalidatesOnWrapChange() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("Hello, world!", 20);
            using (f)
            using (c)
            {
                var host = (IUiaTextHost)c;
                _ = host.LineStartOf(5);
                _ = host.LineStartOf(5);
                c.TestHook_ResetLastLineSegsCounters();
                c.WrapColumns = 30;
                _ = host.LineStartOf(5);
                Assert.Equal(1, c.TestHook_LastLineSegsMissCount);
                Assert.Equal(0, c.TestHook_LastLineSegsHitCount);
            }
        });

    [Fact]
    public void LineSegs_MissFromWorkerThread_InvokesOnce() =>
        Sta.Run(() =>
        {
            // wrap=4 で "abcdefghij" は [0,4)[4,8)[8,10) に折り返す。offset 6 は 2 つ目の視覚行。
            var (f, c) = MakeControl("abcdefghij", 4);
            using (f)
            using (c)
            {
                var host = (IUiaTextHost)c;
                c.TestHook_ResetLastLineSegsCounters();
                var worker = System.Threading.Tasks.Task.Run(() => host.LineStartOf(6));
                PumpUntil(worker);
                Assert.True(worker.IsCompleted, "ワーカーからの問い合わせが終わらない");
                Assert.Equal(4, worker.Result);
                Assert.Equal(1, c.TestHook_LineSegsInvokeCount);
                Assert.Equal(1, c.TestHook_LastLineSegsMissCount);
            }
        });

    [Fact]
    public void LineSegs_HitFromWorkerThread_AnswersWithoutInvoke() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", 4);
            using (f)
            using (c)
            {
                var host = (IUiaTextHost)c;
                // UI スレッドで行 0 を埋める。セグメント境界はフォントに依存する(既定コンストラクタの
                // フォントは比例フォールバックフォントへ解決し得るため、固定値ではなく UI スレッドでの
                // 実測を期待値として使う。コントローラ裁定 2026-09-26)。
                var expected = (host.LineStartOf(6), host.LineEnd(6), host.LineEndNoBreakOf(6));
                // 前提: offset 6 は折り返しの 2 つ目以降(先頭ではない)かつ、行末に達しない継続の視覚行。
                Assert.True(
                    expected.Item1 > 0 && expected.Item1 <= 6 && expected.Item2 < 10,
                    "前提: offset 6 は折り返しの 2 つ目以降かつ継続の視覚行"
                );
                c.TestHook_ResetLastLineSegsCounters();

                var worker = System.Threading.Tasks.Task.Run(() =>
                    (host.LineStartOf(6), host.LineEnd(6), host.LineEndNoBreakOf(6))
                );
                // まず汲まずに待つ。Invoke していれば UI スレッドが応えないので時間内に終わらない
                // (STA の待機が一部のメッセージを汲むことがあるので、決め手は下の Invoke 回数)。
                bool doneWithoutPump = worker.Wait(2000);
                PumpUntil(worker); // 失敗時にワーカーを解放してから assert する
                Assert.True(doneWithoutPump, "キャッシュにヒットしたのに UI スレッドを待った");
                Assert.Equal(expected, worker.Result);
                Assert.Equal(0, c.TestHook_LineSegsInvokeCount);
                Assert.Equal(3, c.TestHook_LastLineSegsHitCount);
                Assert.Equal(0, c.TestHook_LastLineSegsMissCount);
            }
        });

    [Fact]
    public void LineSegs_EmptyLineFromWorkerThread_AnswersWithoutInvoke() =>
        Sta.Run(() =>
        {
            // 行 1 は空行("ab\r\n" の後の位置 4)。行 2 は位置 6 から。
            var (f, c) = MakeControl("ab\r\n\r\ncd", 4);
            using (f)
            using (c)
            {
                var host = (IUiaTextHost)c;
                c.TestHook_ResetLastLineSegsCounters();
                var worker = System.Threading.Tasks.Task.Run(() =>
                    (host.LineStartOf(4), host.LineEnd(4), host.LineEndNoBreakOf(4))
                );
                bool doneWithoutPump = worker.Wait(2000);
                PumpUntil(worker);
                Assert.True(doneWithoutPump, "空行の問い合わせで UI スレッドを待った");
                Assert.Equal((4, 6, 4), worker.Result);
                Assert.Equal(0, c.TestHook_LineSegsInvokeCount);
                // 空行はヒットにもミスにも数えない(従来どおり)。
                Assert.Equal(0, c.TestHook_LastLineSegsHitCount);
                Assert.Equal(0, c.TestHook_LastLineSegsMissCount);
            }
        });

    [Fact]
    public void LineSegs_AfterHandleDestroyed_FallsBackToLogicalLine_EvenWithCachedSegs() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", 4);
            using (f)
            using (c)
            {
                var host = (IUiaTextHost)c;
                // キャッシュを埋める。セグメント境界はフォントに依存するため、視覚行の先頭が
                // 論理行の先頭(0)と異なることだけを構造的に確認する(コントローラ裁定 2026-09-26)。
                int visualStart = host.LineStartOf(6);
                Assert.True(visualStart > 0, "前提: 視覚行の先頭は論理行の先頭と異なる");
                f.Dispose(); // 子の EditorControl も破棄され、Handle が無くなる
                Assert.False(c.IsHandleCreated);
                // Handle のガードはキャッシュより前: 論理行の先頭 0 に落ちる(視覚行の先頭ではない)。
                Assert.Equal(0, host.LineStartOf(6));
            }
        });

    [Fact]
    public void LastLineSegs_InvalidatesOnApplyAppearance() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", 4);
            using (f)
            using (c)
            {
                var host = (IUiaTextHost)c;
                _ = host.LineStartOf(6);
                _ = host.LineStartOf(6);
                c.TestHook_ResetLastLineSegsCounters();
                // 折り返し桁は同じ 4 のまま(wrap の変化による破棄と区別する)。
                c.ApplyAppearance(
                    new kxEdit.Core.Settings.AppSettings
                    {
                        WrapColumnEnabled = true,
                        WrapColumn = 4,
                    }
                );
                Assert.Equal(4, c.WrapColumns); // 前提
                // ApplyAppearance はフォントも差し替えるため、セグメント境界の固定値は検証しない
                // (ヒット/ミスのカウンタが本テストの主張。コントローラ裁定 2026-09-26)。
                _ = host.LineStartOf(6);
                Assert.Equal(1, c.TestHook_LastLineSegsMissCount);
                Assert.Equal(0, c.TestHook_LastLineSegsHitCount);
            }
        });

    [Fact]
    public void LineSegs_SweepFromWorkerThread_MatchesUncachedUiThreadAnswers() =>
        Sta.Run(() =>
        {
            // 折り返し・空行・日本語(全角 2 桁)・CRLF・改行なしの最終行を含む。
            const string Text =
                "abcdefghij\r\n\r\nあいうえおかきくけこ\r\nxy\r\nlast-line-no-break";
            var (f, c) = MakeControl(Text, 4);
            using (f)
            using (c)
            {
                var host = (IUiaTextHost)c;
                int len = host.TextLength;
                // 正解: UI スレッドで、毎回キャッシュを捨ててから求める(折り返し桁の変更で破棄)。
                var expected = new (int, int, int)[len + 1];
                for (int o = 0; o <= len; o++)
                {
                    c.WrapColumns = 0;
                    c.WrapColumns = 4;
                    expected[o] = (host.LineStartOf(o), host.LineEnd(o), host.LineEndNoBreakOf(o));
                }
                // 実際: ワーカースレッドから、先頭から順に(say all と同じくヒットとミスが混ざる)。
                var worker = System.Threading.Tasks.Task.Run(() =>
                {
                    var actual = new (int, int, int)[len + 1];
                    for (int o = 0; o <= len; o++)
                        actual[o] = (
                            host.LineStartOf(o),
                            host.LineEnd(o),
                            host.LineEndNoBreakOf(o)
                        );
                    return actual;
                });
                PumpUntil(worker, timeoutMs: 10_000);
                Assert.True(worker.IsCompleted, "ワーカーの掃引が終わらない");
                Assert.Equal(expected, worker.Result);
                Assert.True(c.TestHook_LastLineSegsHitCount > 0); // 前提: ヒットの経路を通った
            }
        });
}
