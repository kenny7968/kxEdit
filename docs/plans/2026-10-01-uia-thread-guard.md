# UIA のスレッド境界(uia-thread-guard)実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** UIA の 7 経路が、Handle の破棄の窓(TOCTOU)でも RPC スレッドでエディタ内部に触れないようにする。あわせて、古いキーの行キャッシュの書き込み(項目 10)と、折り返し OFF のセグメントの保険(項目 11)を入れる。

**Architecture:** `UiaTextHostAdapter` に UI スレッドへの委譲の共通関数 `TryRunOnUi<T>`(同期・戻り値あり)と `TryPostToUi`(投函・戻り値なし)を置き、7 経路をすべてそこへ寄せる。`InvokeRequired` が false でも、今のスレッドが ctor で記録した UI スレッドでなければ縮退値を返す(投函系は捨てる)。

**Tech Stack:** C# / .NET WinForms / xUnit(Editor.Tests)

**Spec:** `docs/plans/2026-09-27-perf-followups-design.md` の §3(全フェーズ共通の規約)と §6(フェーズ 2)

## Global Constraints

- 0 warning(`-warnaserror`)。CSharpier 整形(pre-commit フック)を `--no-verify` で飛ばさない。
- コミットメッセージは `fix|test|docs(scope): 要約` + 日本語本文。署名でハングする場合は `git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -F <UTF-8 のメッセージファイル>`。
- 通常時(Handle があり、UI スレッドが応答する)の応答は変えない(設計書 §6.1)。
- 意図的な挙動差は設計書 §3.5 の 1 行だけ: 「Handle の破棄と UIA の問い合わせが競合したとき、RPC スレッドで計算せずに縮退値(空の範囲・失敗)を返す」。
- 変異検証は行わない(設計書 §3.3)。ただし陰性対照(修正を一時的に外してテストが落ちることの確認)は各タスクで行う。
- 前倒しのコード品質レビューを Task 1 で行う(新しい seam。設計書 §3.3)。
- L5 は必須(簡易): `tools/sr-regression.ps1` が EXIT 0 + NVDA で行移動・選択・タブを閉じる操作(設計書 §6.3)。

## Review Focus

1. **通常時の Invoke 経路が壊れていないこと**: Handle があり worker から呼ぶと、従来どおり UI スレッドで計算した値が返る(既存の `EditorControlCacheTests.LineSegs_MissFromWorkerThread_InvokesOnce`・`EditorControlBoundingRectsTests`・`EditorControlOffsetFromPointTests` が網。Task 1 の Step 6 で回す)。
2. **UI スレッドから直接呼んだとき**: `InvokeRequired=false` かつ UI スレッドなので、body がその場で走る(既存の UI スレッドからの全テストが網)。
3. **投函系の到達時の再判定**: BeginInvoke の到達時に Handle が消えていれば捨てる。SetFocus はこれまで再判定しなかったが、本変更で他の 2 つと揃う(到達時に Handle が無ければ `Focus()` は元々何もしないので、観測できる差はない)。
4. **縮退値が既定値と区別できること**: 各テストは、UI スレッドで同じ問い合わせを行う陽性対照で「計算が走れば縮退値と違う値になる」ことを確かめる(CLAUDE.md §4-B)。`GetBoundingRectangles` / `OffsetFromScreenPoint` は、本体が `_hwnd==0` で先に抜けるので値では区別できない(例外が出ないことだけを確かめる)。
5. **項目 10 の偽の緑**: 編集の処理がメッセージを汲むと、Invoke が編集より先に走ってテストが修正なしでも通る。陰性対照で必ず確かめる。

---

## ファイル構成

| ファイル | 変更 |
|---|---|
| `src/kxEdit.Editor/UiaTextHostAdapter.cs` | `_uiThreadId`・`IsUiBound`・`TryRunOnUi`・`TryPostToUi`・7 経路の付け替え・項目 10 のガード・テストフック 2 つ・コメント |
| `src/kxEdit.Editor/EditorControl.Uia.cs` | テストフックの転送 2 つ |
| `src/kxEdit.Editor/EditorControl.cs` | 項目 11(`SetTopPosition`) |
| `src/kxEdit.Editor/GdiCharMetrics.cs` | `TestHook_NonAsciiWidthCount`・スレッド安全性の remarks の更新 |
| `tests/kxEdit.Editor.Tests/UiaThreadGuardTests.cs` | 新規(7 経路) |
| `tests/kxEdit.Editor.Tests/EditorControlCacheTests.cs` | 項目 10 のテスト |
| `tests/kxEdit.Editor.Tests/VisualRowScrollTests.cs` | 項目 11 のテスト |
| `docs/plans/2026-09-27-perf-followups-design.md` | §6.4 実施記録の追記(Task 3) |

---

### Task 1: UI スレッドへの委譲の共通関数と 7 経路

**Files:**
- Modify: `src/kxEdit.Editor/UiaTextHostAdapter.cs`
- Modify: `src/kxEdit.Editor/EditorControl.Uia.cs`
- Modify: `src/kxEdit.Editor/GdiCharMetrics.cs`
- Create: `tests/kxEdit.Editor.Tests/UiaThreadGuardTests.cs`

**Interfaces:**
- Produces(Task 2 が使う):
  - `UiaTextHostAdapter.TryRunOnUi<T>(Func<T> body, T fallback)`(private)
  - `EditorControl.TestHook_UiaAssumeHandleCreated`(internal bool, get/set)
  - `GdiCharMetrics.TestHook_NonAsciiWidthCount`(internal int)

**UI スレッドの ID を記録する場所について(設計書 §6.1 の精密化)**: 設計書は「EditorControl の ctor で 1 回だけ記録し、以後は書き換えない」とする。`UiaTextHostAdapter` は `EditorControl` の ctor の中でだけ生成される(`EditorControl.cs:257`)ので、adapter の ctor で `readonly` フィールドに記録するのと同じ意味になる。`readonly` にすることで「以後は書き換えない」をコンパイラが保証する。

- [ ] **Step 1: テストフックを足す**

`src/kxEdit.Editor/GdiCharMetrics.cs` の `TestHook_RunCacheChars` の直後に足す:

```csharp
    // perf-followups フェーズ 2: UIA の経路が RPC スレッドで幅メモに書かないことを Editor.Tests から観測する。
    internal int TestHook_NonAsciiWidthCount => _nonAsciiWidths.Count;
```

`src/kxEdit.Editor/UiaTextHostAdapter.cs` の Test hook 節(`TestHook_ResetLastLineSegsCounters` の直前)に足す:

```csharp
    /// <summary>
    /// テスト専用: Handle のガード(<see cref="IsUiBound"/>)を通過したものとして扱う。
    /// IsHandleCreated と InvokeRequired の間で Handle が破棄される窓(TOCTOU)を、テストで再現するため
    /// (窓そのものは作れない)。製品コードからは設定しない。
    /// </summary>
    internal bool TestHook_AssumeHandleCreated { get; set; }
```

`src/kxEdit.Editor/EditorControl.Uia.cs` の `TestHook_ResetLastLineSegsCounters` の直後に足す:

```csharp
    /// <summary>
    /// テスト専用: UIA の経路で Handle のガードを通過したものとして扱う
    /// (perf-followups フェーズ 2。Editor.Tests UiaThreadGuardTests)。
    /// </summary>
    internal bool TestHook_UiaAssumeHandleCreated
    {
        get => _uia.TestHook_AssumeHandleCreated;
        set => _uia.TestHook_AssumeHandleCreated = value;
    }
```

- [ ] **Step 2: 失敗するテストを書く**

`tests/kxEdit.Editor.Tests/UiaThreadGuardTests.cs` を作る:

```csharp
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
                Assert.True(host.LineStartOf(Line1Offset) > Line1Start, "前提: 視覚行の先頭は論理行の先頭と違う");
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
```

- [ ] **Step 3: テストが落ちることを確かめる**

Run: `dotnet build kxEdit.sln -c Release -warnaserror` の後 `dotnet test tests/kxEdit.Editor.Tests -c Release --no-build --filter "FullyQualifiedName~UiaThreadGuardTests"`

Expected: `LineStartOf_…`・`GetVisibleRange_…`・`SetSelection_…`・`ScrollRangeIntoView_…` の 4 件が FAIL、`HarmlessPaths_…` は PASS。

落ちるのは**陽性対照**の側(「前提: …」のメッセージ)。この時点ではフックがまだどこからも読まれないので、UI スレッドで呼んでも各経路の `IsHandleCreated` のガードで縮退値に抜ける。worker 側の assert は、修正の有無にかかわらず縮退値で通る。よってこのステップは「フックを配線して初めて陽性対照が成立する」ことの確認であって、修正が効くことの確認ではない。修正が効くことは Step 7 の陰性対照で確かめる。

- [ ] **Step 4: 共通関数を実装する**

`src/kxEdit.Editor/UiaTextHostAdapter.cs`:

(a) フィールド。`_trace` の宣言の直後に足す:

```csharp
    // perf-followups フェーズ 2: UI スレッドの ID。EditorControl の ctor(UI スレッドで生成される)の中で
    // 本インスタンスが 1 回だけ生成されるので、ここで記録する。readonly = 以後は書き換えない。
    // OnHandleCreated で覚えてはならない: RPC スレッドで Handle が作り直されたとき、ID が RPC スレッドの値で
    // 上書きされ、ガード(TryRunOnUi / TryPostToUi)が逆向きに効く(TryGetClientOrigin の remarks)。
    private readonly int _uiThreadId;
```

(b) ctor の末尾(`_trace = trace;` の後)に足す:

```csharp
        _uiThreadId = Environment.CurrentManagedThreadId;
```

(c) `// === IUiaTextHost 全メンバ実装` の見出しの直前に、共通関数を足す:

```csharp
    // === UI スレッドへの委譲(perf-followups フェーズ 2) ===

    /// <summary>
    /// UI スレッドが束縛されているか(Handle があり、Dispose されていない)。どのスレッドから読んでもよい。
    /// </summary>
    private bool IsUiBound =>
        !_host.IsDisposed && (_host.IsHandleCreated || TestHook_AssumeHandleCreated);

    /// <summary>
    /// <paramref name="body"/> を UI スレッドで実行して結果を返す。UI スレッドで実行できないときは
    /// body を走らせず、<paramref name="fallback"/>(縮退値)を返す。どのスレッドから呼んでもよい。
    /// </summary>
    /// <remarks>
    /// 判定の順序:
    /// <list type="number">
    /// <item>UI スレッドが束縛されていない(<see cref="IsUiBound"/>)→ fallback。</item>
    /// <item>InvokeRequired → 同期 Invoke。Invoke 中の破棄(ObjectDisposedException /
    /// InvalidOperationException)は fallback。</item>
    /// <item>それ以外は、今のスレッドを <see cref="_uiThreadId"/> と比べ、違えば fallback。</item>
    /// </list>
    /// 3 が要る理由: 1 と 2 の間で UI スレッドが Handle を破棄すると、InvokeRequired は false を返す
    /// (Handle 未生成 / 破棄後の仕様)。3 がないと body が RPC スレッドで走り、非スレッドセーフな
    /// 幅メモ(<see cref="GdiCharMetrics"/>)へ書き込む。UI スレッドと同時に書くと構造が壊れ、
    /// TryGetValue の無限ループで UI スレッドが戻らなくなる。
    /// </remarks>
    private T TryRunOnUi<T>(Func<T> body, T fallback)
    {
        if (!IsUiBound)
            return fallback;
        if (_host.InvokeRequired)
        {
            try
            {
                return _host.Invoke(body);
            }
            catch (ObjectDisposedException)
            {
                return fallback;
            }
            catch (InvalidOperationException)
            {
                return fallback;
            } // Handle 破棄との race
        }
        if (Environment.CurrentManagedThreadId != _uiThreadId)
            return fallback;
        return body();
    }

    /// <summary>
    /// <paramref name="body"/> を UI スレッドで実行する(書き込み系。戻り値は不要)。UI スレッド以外からは
    /// BeginInvoke で投函して RPC スレッドを待たせない(deadlock 回避)。実行できないときは捨てる。
    /// </summary>
    /// <remarks>
    /// 投函したものは、到達時にもう一度この判定を通す(到達までに破棄されていれば捨てる。
    /// 到達時は InvokeRequired が false で UI スレッドなので、再投函はしない)。
    /// WinForms の invoke キューは FIFO なので、SR が直後に呼ぶ同期 Invoke 系
    /// (GetBoundingRectangles 等)は、投函した書き込みの後に走る。
    /// 判定の順序と 3 つ目の判定が要る理由は <see cref="TryRunOnUi{T}"/> と同じ。
    /// </remarks>
    private void TryPostToUi(Action body)
    {
        if (!IsUiBound)
            return;
        if (_host.InvokeRequired)
        {
            try
            {
                _host.BeginInvoke(new Action(() => TryPostToUi(body)));
            }
            catch (ObjectDisposedException)
            {
                // 投函の直前に破棄された。書き込みは捨ててよい(対象の窓が無くなった)。
            }
            catch (InvalidOperationException)
            {
                // Handle 破棄との race。同上。
            }
            return;
        }
        if (Environment.CurrentManagedThreadId != _uiThreadId)
            return;
        body();
    }
```

- [ ] **Step 5: 7 経路を付け替える**

`SetSelection` を置き換える:

```csharp
    void IUiaTextHost.SetSelection(int start, int end) =>
        TryPostToUi(() => _host.SetSelectionCharRange(start, end));
```

`TryFindVisualSegment` の本体を置き換える(remarks の最後の 2 文「Handle のガードはキャッシュより前に置く…」は残す):

```csharp
    private WrapSegment? TryFindVisualSegment(TextSnapshot snap, int line, int offsetInLine)
    {
        int wrap = _host.WrapColumns;
        if (wrap <= 0)
            return null;
        if (!IsUiBound)
            return null; // UI スレッドが束縛されていない=論理行フォールバック
        // 空行は視覚セグメントを持たない(TryFindVisualSegmentCore も null を返す)。
        // 不変の snap だけで判定できるので、RPC スレッドでも Invoke せずに答える。
        if (snap.GetLineStart(line) == snap.GetLineEnd(line, includeBreak: false))
            return null;
        // フェーズ 10: キーが一致すれば RPC スレッドでも即答する(LineSegsCache の remarks)。
        // _host.Metrics は参照を読むだけ(比較にしか使わない)。
        if (TryGetCachedSegs(snap, line, wrap, _host.Metrics, out var cached))
            return VisualSegments.FindContaining(cached, offsetInLine).Segment;
        // フェーズ 10: UI スレッドへマーシャリングした回数(テストと Smoke S9 が観測する)。
        // Invoke の前に加算する(テストは「1 になった = Invoke に入った」を待つ)。
        if (_host.InvokeRequired)
            Interlocked.Increment(ref _testHook_lineSegsInvokeCount);
        return TryRunOnUi(
            () => TryFindVisualSegmentCore(snap, line, offsetInLine, wrap),
            (WrapSegment?)null
        );
    }
```

`GetBoundingRectangles` を置き換える(2026-08-02 のコメントは、要旨を残して短くする):

```csharp
    double[] IUiaTextHost.GetBoundingRectangles(int start, int end)
    {
        // 2026-08-02: Handle 破棄後に RPC スレッドで ComputeBoundingRectangles が走ると、
        // ComputeCaretPoint → GdiCharMetrics.MeasureRun が幅メモに書き込む(実測で確認済み)。
        // perf-followups フェーズ 2: IsHandleCreated と InvokeRequired の間で破棄される窓も
        // TryRunOnUi が塞ぐ。踏む窓は「SR がプロバイダを掴んだままタブ / アプリが teardown に入る」場面
        // (OnHandleDestroyed は _provider も _bufferSnapshot も落とさないため RPC は通る)。
        return TryRunOnUi(() => ComputeBoundingRectangles(start, end), Array.Empty<double>());
    }
```

`OffsetFromScreenPoint` を置き換える:

```csharp
    int IUiaTextHost.OffsetFromScreenPoint(double x, double y)
    {
        // 理由は GetBoundingRectangles と同じ(TryRunOnUi)。
        return TryRunOnUi(() => ComputeOffsetFromScreenPoint(x, y), 0);
    }
```

`ScrollRangeIntoView` を置き換える:

```csharp
    void IUiaTextHost.ScrollRangeIntoView(int start, int end, bool alignToTop)
    {
        // 書き込み系は投函する(RPC スレッドを待たせない)。Handle が無いとき・UI スレッド以外で
        // InvokeRequired が false のときは捨てる(RPC スレッドが ClientSize / _hscroll.Visible /
        // PositionCaret に触れない。CLAUDE.md §2 の a11y 鉄則)。到達時の再判定は TryPostToUi。
        TryPostToUi(() => _host.ScrollCharRangeIntoView(start, end, alignToTop));
    }
```

`GetVisibleRange` を置き換える:

```csharp
    (int Start, int End) IUiaTextHost.GetVisibleRange()
    {
        // UI スレッド専用状態 (_topLine / _metrics / ClientSize) を要する読み取りのため同期 Invoke する
        // (書き込み系の ScrollRangeIntoView が投函なのは戻り値が不要だから)。
        // Handle が無ければ ClientSize が無意味なので (0, 0)(TryRunOnUi)。
        return TryRunOnUi(() => _host.GetVisibleCharRange(), (0, 0));
    }
```

`SetFocus` を置き換える:

```csharp
    void IUiaTextHost.SetFocus() => TryPostToUi(() => _host.Focus());
```

ファイル先頭の責務コメント(15〜18 行)の「書き込み系 … = UI スレッドへ BeginInvoke」「… = 同期 Invoke」の 2 行の後に 1 行足す:

```csharp
//       (どちらも TryRunOnUi / TryPostToUi 経由。UI スレッド以外で走れないときは縮退値・破棄。perf-followups フェーズ 2)
```

`GdiCharMetrics.cs` のクラス remarks の `<para><b>マーシャリングの前提</b>…</para>` を置き換える:

```csharp
/// <para>
/// <b>マーシャリングの前提</b>: 7 経路とも <c>UiaTextHostAdapter.TryRunOnUi</c> /
/// <c>TryPostToUi</c> を通す。そこでは Handle の有無を <c>InvokeRequired</c> の<b>手前</b>で見たうえで、
/// <c>InvokeRequired</c> が false でも今のスレッドが UI スレッドでなければ縮退値を返す。
/// <see cref="Control.InvokeRequired"/> は Handle 未生成 / 破棄後に <c>false</c> を返すため、
/// 2 つの判定の間で Handle が破棄されると、スレッドの照合がなければ RPC スレッドが本クラスへ到達する
/// (2026-08-02 に順序の誤りを 2 経路で発見して是正し、perf-followups フェーズ 2 で窓も塞いだ)。
/// </para>
```

- [ ] **Step 6: テストを通す**

Run: `dotnet build kxEdit.sln -c Release -warnaserror`
Run: `dotnet test tests/kxEdit.Editor.Tests -c Release --no-build`
Expected: 全件 PASS(新規 5 件を含む)。Review Focus 1・2 の網(`EditorControlCacheTests`・`EditorControlBoundingRectsTests`・`EditorControlOffsetFromPointTests`・`EditorControlUiaHostTests`・`UiaScreenCoordinateTests`)も含めて通ること。

- [ ] **Step 7: 陰性対照**

`TryRunOnUi` と `TryPostToUi` の `if (Environment.CurrentManagedThreadId != _uiThreadId) return …;` を**一時的に**両方とも消し、ビルドが成功したことを確かめてから(古い DLL で走らないように。設計書 §3.3 の注意)`UiaThreadGuardTests` を走らせる。

Expected: `LineStartOf_…`・`GetVisibleRange_…`・`SetSelection_…`・`ScrollRangeIntoView_…` の 4 件が FAIL。`HarmlessPaths_…` は PASS のまま(Review Focus 4 のとおり値では区別できない)。

4 件のうち PASS のままのものがあれば、その陽性対照が成立していない。fixture を直す(例: `GetVisibleRange` の陽性対照が `(0, 0)` を返すなら ClientSize を確かめる)。消した 2 行を戻し、`git diff` で戻したことを確かめる。

- [ ] **Step 8: Commit**

```bash
git add src/kxEdit.Editor/UiaTextHostAdapter.cs src/kxEdit.Editor/EditorControl.Uia.cs src/kxEdit.Editor/GdiCharMetrics.cs tests/kxEdit.Editor.Tests/UiaThreadGuardTests.cs
git commit -F <msg>   # fix(uia): Handle 破棄の窓で UIA の経路が RPC スレッドで計算しないようにする
```

本文の例: 「UiaTextHostAdapter に UI スレッドへの委譲の共通関数 TryRunOnUi / TryPostToUi を置き、7 経路を寄せた。InvokeRequired が false でも UI スレッド以外なら縮退値を返す(投函系は捨てる)。perf-followups 設計書 §6・項目 8。」

- [ ] **Step 9: 前倒しのコード品質レビュー**(CLAUDE.md §3 の 4。新しい seam)

仕様レビューに加えて、別エージェントでコード品質レビューを行う。観点: 共通関数の判定順序・例外の扱い・投函の再判定・テストフックが製品経路に漏れていないこと・テストの陽性対照。

---

### Task 2: 古いキーの行キャッシュ(項目 10)と折り返し OFF のセグメント(項目 11)

**Files:**
- Modify: `src/kxEdit.Editor/UiaTextHostAdapter.cs`(`TryFindVisualSegmentCore`・Test hook 節)
- Modify: `src/kxEdit.Editor/EditorControl.Uia.cs`
- Modify: `src/kxEdit.Editor/EditorControl.cs:947-962`(`SetTopPosition`)
- Test: `tests/kxEdit.Editor.Tests/EditorControlCacheTests.cs`
- Test: `tests/kxEdit.Editor.Tests/VisualRowScrollTests.cs`

**Interfaces:**
- Consumes: Task 1 の `TryRunOnUi`(TryFindVisualSegment が使う。本タスクは中身に触れない)
- Produces: `EditorControl.TestHook_HasLastLineSegs`(internal bool)

- [ ] **Step 1: テストフックを足す**

`UiaTextHostAdapter.cs` の Test hook 節に足す:

```csharp
    /// <summary>行キャッシュが空でないか(perf-followups フェーズ 2・項目 10。Editor.Tests が観測する)。</summary>
    internal bool TestHook_HasLastLineSegs => _lastLineSegs is not null;
```

`EditorControl.Uia.cs` の `TestHook_LineSegsInvokeCount` の直後に足す:

```csharp
    /// <summary>行キャッシュが空でないか(perf-followups フェーズ 2・項目 10)。</summary>
    internal bool TestHook_HasLastLineSegs => _uia.TestHook_HasLastLineSegs;
```

- [ ] **Step 2: 項目 10 の失敗するテストを書く**

`EditorControlCacheTests.cs` の末尾(クラスの閉じ括弧の前)に足す:

```csharp
    /// <summary>
    /// perf-followups フェーズ 2・項目 10: worker が UI スレッドへの Invoke を待っている間に本文が
    /// 差し替わったら(OnSnapshotChanged がキャッシュを破棄した後に)、古いキーの結果を書かない。
    /// 書くと、破棄した直後に古い TextSnapshot を握り直す(答えはキー照合で正しいが、GC を阻む)。
    /// </summary>
    [Fact]
    public void LineSegs_EditWhileWorkerWaitsForInvoke_DoesNotCacheStaleKey() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", 4);
            using (f)
            using (c)
            {
                var host = (IUiaTextHost)c;
                c.TestHook_ResetLastLineSegsCounters();
                Assert.False(c.TestHook_HasLastLineSegs); // 前提: まだ誰も問い合わせていない

                var worker = System.Threading.Tasks.Task.Run(() => host.LineEnd(6));
                // ポンプせずに待つ。カウンタは Invoke の前に加算されるので、1 になれば worker は Invoke に
                // 入った(か入る直前)。UI スレッドが汲まない限り、Invoke の本体は走らない。
                Assert.True(
                    System.Threading.SpinWait.SpinUntil(
                        () => c.TestHook_LineSegsInvokeCount == 1,
                        5000
                    ),
                    "前提: worker が Invoke に入った"
                );
                c.ReplaceCharRange(0, 0, "X"); // 本文の差し替え(OnSnapshotChanged)
                // worker が戻るまで汲み続ける(カウンタは Invoke を呼ぶ前に加算されるので、DoEvents 1 回では
                // Invoke がまだ届いていないことがある)。
                PumpUntil(worker);
                Assert.True(worker.IsCompleted, "worker が終わらない");
                Assert.Equal(1, c.TestHook_LastLineSegsMissCount); // 前提: 本体が古い snap で計算した
                Assert.False(c.TestHook_HasLastLineSegs, "古いキーの行キャッシュが書かれた");
            }
        });
```

- [ ] **Step 3: 項目 11 の失敗するテストを書く**

`VisualRowScrollTests.cs` の `SetTopPosition_ClampsNegativeSegmentToZero` の直後に足す:

```csharp
    /// <summary>
    /// perf-followups フェーズ 2・項目 11(保険): 折り返し OFF ではセグメントを 0 に丸める。
    /// 到達可能な経路はない(設計書 §15 の 11)が、_topSegment が古いまま残る形を塞ぐ。
    /// fixture は行がクランプされない位置にする(クランプされるとセグメントも 0 に落ち、修正がなくても
    /// 通ってしまう。CLAUDE.md §4-B)。
    /// </summary>
    [Fact]
    public void SetTopPosition_WrapOff_DropsSegmentToZero() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("a\nb\nc\nd\ne", wrap: 0, visibleRows: 3);
            using (f)
            using (c)
            {
                c.SetTopPosition(2, 3);
                Assert.Equal(2, c.TopLine); // 前提: 行はクランプされていない
                Assert.Equal(0, c.TopSegment);
            }
        });
```

- [ ] **Step 4: 2 件が落ちることを確かめる**

Run: `dotnet build kxEdit.sln -c Release -warnaserror` の後 `dotnet test tests/kxEdit.Editor.Tests -c Release --no-build --filter "FullyQualifiedName~LineSegs_EditWhileWorkerWaitsForInvoke_DoesNotCacheStaleKey|FullyQualifiedName~SetTopPosition_WrapOff_DropsSegmentToZero"`

Expected: 2 件とも FAIL(「古いキーの行キャッシュが書かれた」/ `TopSegment` が 3)。

項目 10 のテストが PASS した場合は偽の緑(Review Focus 5)。`ReplaceCharRange` がメッセージを汲んで Invoke が編集より先に走っている。そのときは fixture の作り方を見直し、修正なしで落ちる形にしてから進む。

- [ ] **Step 5: 実装する**

`TryFindVisualSegmentCore` の書き込み(`_lastLineSegs = new LineSegsCache(…);` の 2 行のコメントと代入)を置き換える:

```csharp
            // 全フィールドを設定したインスタンスを volatile 書き込みで公開する(RPC スレッドが読む)。
            // キーは実際に使った wrap と metrics(Segs がキーだけの関数であることを保つ)。
            // perf-followups フェーズ 2・項目 10: Invoke を待つ間に本文か折り返し桁が変わっていたら
            // (OnSnapshotChanged / InvalidateLastLineSegs が先に走っていたら)書かない。書くと破棄の直後に
            // 古い TextSnapshot を握り直し、大容量ファイルの差し替え後の GC を阻む(答えはキー照合で正しい)。
            if (ReferenceEquals(snap, _bufferSnapshot) && wrap == _host.WrapColumns)
                _lastLineSegs = new LineSegsCache(snap, line, wrap, metrics, segs);
```

(`Interlocked.Increment(ref _testHook_lastLineSegsMissCount);` はガードの外に残す。計算したことに変わりはない。)

`EditorControl.cs` の `SetTopPosition` の `clampedSeg` の行を置き換える(上の 3 行のコメントは残す):

```csharp
        // 折り返し OFF ではセグメントは常に 0(perf-followups フェーズ 2・項目 11 の保険。到達する経路は
        // ないが、_topSegment が古いまま残る形を塞ぐ。設計書 2026-09-27-perf-followups-design.md §15)。
        int clampedSeg = clampedLine == line && _wrapColumns > 0 ? Math.Max(0, segment) : 0;
```

- [ ] **Step 6: テストを通す**

Run: `dotnet build kxEdit.sln -c Release -warnaserror`
Run: `dotnet test tests/kxEdit.Editor.Tests -c Release --no-build`
Expected: 全件 PASS(`VisualRowScrollTests` と `EditorControlCacheTests` の既存テストを含む)。

- [ ] **Step 7: Commit**

```bash
git add src/kxEdit.Editor/UiaTextHostAdapter.cs src/kxEdit.Editor/EditorControl.Uia.cs src/kxEdit.Editor/EditorControl.cs tests/kxEdit.Editor.Tests/EditorControlCacheTests.cs tests/kxEdit.Editor.Tests/VisualRowScrollTests.cs
git commit -F <msg>   # fix(uia): Invoke 待ちの間に本文が変わったら古いキーの行キャッシュを書かない
```

本文の例: 「項目 10: TryFindVisualSegmentCore の書き込みに snap と wrap のガードを足した。項目 11(保険): 折り返し OFF の SetTopPosition でセグメントを 0 に丸める。perf-followups 設計書 §6.1。」

---

### Task 3: 品質ゲート・L5・実施記録

**Files:**
- Modify: `docs/plans/2026-09-27-perf-followups-design.md`(§6 の末尾に §6.4 実施記録を追記。CLAUDE.md §8 で許される追記)

- [ ] **Step 1: 最終ブランチレビュー(2 パス)**

CLAUDE.md §3 の 5。コード品質パス(ミューテーション検証のスポットチェックは本フェーズでは行わない。設計書 §3.3)と脆弱性パスを、別々のエージェントで行う。並走させる場合は、各エージェントに scratchpad の専用ディレクトリを割り当て、リポジトリでのビルド・テストを禁止する。指摘は fixup commit で反映する。

- [ ] **Step 2: 品質ゲート**

Run: `pwsh -File tools/pre-merge-check.ps1`
Expected: EXIT 0

- [ ] **Step 3: L5(簡易)**

- `pwsh -File tools/sr-regression.ps1` が EXIT 0。
- ユーザーに NVDA で次を確かめてもらう: 行移動(折り返し ON / OFF)・選択(Shift+矢印)・タブを閉じる操作(読み上げが途切れない・例外ダイアログが出ない)。

- [ ] **Step 4: 実施記録を追記する**

設計書 §6.3 の後に「### 6.4 実施記録」を足す。書く内容: 実施日・ブランチ・commit の一覧、設計からの精密化(UI スレッドの ID を adapter の ctor の readonly に記録したこと・SetFocus も到達時に再判定するようになったこと・Invoke と BeginInvoke の例外を縮退値に落とすようになったこと)、陰性対照の結果(Task 1 Step 7・Task 2 Step 4)、L5 の結果。

```bash
git add docs/plans/2026-09-27-perf-followups-design.md
git commit -F <msg>   # docs(perf): フェーズ 2(UIA のスレッド境界)の実施記録
```

- [ ] **Step 5: PR**

push して PR を作る。description(日本語)には、目的・意図的な挙動差(設計書 §3.5 のフェーズ 2 の行)・レビュー経緯・L5 の結果・申し送りを書く。
