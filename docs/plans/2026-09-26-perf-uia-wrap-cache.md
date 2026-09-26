# フェーズ 10: 折り返し ON の UIA(perf-uia-wrap-cache) 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 折り返し ON のとき、UIA の行単位の問い合わせ(`LineStartOf` / `LineEnd` / `LineEndNoBreakOf`)がキャッシュにヒットしたら、UI スレッドへ同期 Invoke せずに RPC スレッドで即答する(P-10)。

**Architecture:**
- `UiaTextHostAdapter._lastLineSegs`(nullable の構造体タプル・UI スレッド専用)を、不変クラス `LineSegsCache(Snap, Line, Wrap, Metrics, Segs)` の **volatile 参照**に替える。`Segs` はキーだけで決まるので、キーが一致すればどのスレッドがいつ読んでも答えは正しい。
- `TryFindVisualSegment` の順序: 折り返し OFF → **Handle のガード** → 空行(スナップショットだけで判定・Invoke しない)→ **キャッシュ照合(ヒットなら即答)** → 従来どおり Invoke。ミス時の計算は従来どおり UI スレッドだけで行う(`GdiCharMetrics.MeasureRun` が UI スレッド専用のため)。
- `ApplyAppearance` の**先頭でも**キャッシュを破棄する(末尾の破棄は既存)。
- テストフックのヒット・ミスの回数は RPC スレッドから加算されうるので `Interlocked` にする。Invoke の回数を数えるフックを足す(変更前の計測にも使うので Task 1 で先に足す)。

**Tech Stack:** C#(.NET 9)、WinForms、UIA provider(`kxEdit.Accessibility`)、xUnit。

**Spec:** `docs/plans/2026-09-24-general-perf-improvements-design.md` §3・§15(以下「設計書」)
**調査記録:** `docs/plans/2026-09-24-general-perf-audit.md` P-10(以下「調査記録」)
**前フェーズの計画:** `docs/plans/2026-09-26-perf-grep.md`(実施記録は設計書 §13.4 に同梱済み。本フェーズで転記するものはない)

## Global Constraints

- 挙動不変が原則。UIA の各メンバーが返す値は変えない。設計書 §3.5 にフェーズ 10 の行はない(意図的な挙動差なし)。
- **a11y 鉄則**(CLAUDE.md §2): RPC スレッドから UI スレッド専有の状態に触らない。RPC スレッドで読んでよいのは、従来から読んでいるもの(`_host.WrapColumns`・`_host.IsHandleCreated`・`_host.InvokeRequired`・不変の `TextSnapshot`)と、本フェーズで足す `_host.Metrics` の**参照の比較だけ**(メソッドは呼ばない)。
- ミス時の `LineLayout.Wrap` / `MeasureRun` は UI スレッドでしか呼ばない。
- 0 warning(`-warnaserror`)。pre-commit フック(CSharpier・ローカルパス検出)を `--no-verify` で飛ばさない。
- コメント・コミットメッセージ本文は日本語。
- ミューテーション検証は行わない(下の 0.3)。Task 2 の最後に陰性対照(実装を戻してテストが落ちることの確認)を行う。
- 陰性対照でファイルを書き戻すときは `git checkout -- <file>` か `[IO.File]::WriteAllText(..., [Text.UTF8Encoding]::new($false))` を使う(`Set-Content -Encoding utf8BOM` は BOM を付けてしまう)。陰性対照のビルドは `-p:TreatWarningsAsErrors=false` を付ける。
- 実装者は **commit 後の状態で** build と test を確認する(pre-commit の CSharpier が整形で構造を変えて、アナライザの警告=エラーになることがある)。
- テストでスレッドを使うときは、UI スレッド(`Sta.Run` のスレッド)がメッセージを汲まない限り Invoke が進まないことに注意する。待つときは `Application.DoEvents()` のループ(上限時間つき)で汲む。

## Review Focus

- **teardown 中の問い合わせ**(SR がプロバイダを掴んだままタブを閉じる) — Handle 破棄後は、キャッシュが残っていても論理行へフォールバックすること(従来どおり)。Task 2 の `LineSegs_AfterHandleDestroyed_FallsBackToLogicalLine_EvenWithCachedSegs` で押さえる。
- **空行を含む文書の say all** — 空行でも Invoke せずに従来と同じ値(行頭・次の行の先頭・行頭)を返すこと。Task 2 の `LineSegs_EmptyLineFromWorkerThread_AnswersWithoutInvoke` で押さえる。
- **RPC スレッドの問い合わせの途中で UI スレッドが本文・折り返し桁・フォントを変える** — ヒットした答えが「どこかの時点の(snap, wrap, metrics)に対して正しい答え」であること。キーに Metrics を含め、`Segs` をキーだけの関数にすることで構造的に保証する(決定的なテストは作れない。レビューで論証を確認する。下の 0.2)。
- **キャッシュの有無で答えが変わらない** — 別スレッドから say all の順で掃引した答えが、キャッシュを毎回捨てて UI スレッドで求めた答えと全オフセットで一致すること(CRLF・空行・日本語・最終行を含む)。Task 2 の `LineSegs_SweepFromWorkerThread_MatchesUncachedUiThreadAnswers` で押さえる。
- **外観の変更**(フォント・折り返し桁の設定変更) — `ApplyAppearance` の後はミスになり、新しい条件で求め直すこと。Task 2 の `LastLineSegs_InvalidatesOnApplyAppearance` で押さえる。

---

## 0. 前提と決定事項

### 0.1 計測(設計書 §3.2)

- 既存の Smoke `--perf`(S1〜S8)と perf-harness(M-1〜M-7)には、**別スレッドからの UIA の行読み**を測るものがない(S8 は UI スレッドから直接呼ぶので Invoke を通らない。harness は UIA クライアントを持たない)。そこで **Smoke `--perf` に S9 を足す**(フェーズ 5・8 が Core.Bench に計測を足したのと同じ扱い。フェーズ 0 の計測基盤を拡張する)。
  - S9 は、折り返し ON(40 桁)の ja10k で、NVDA の say all に相当する行読みを**ワーカースレッドから**行う(UIA の RPC スレッドの代わり)。1 歩 = `TextRangeProviderV2.Move(Line, 1)` + 非退化レンジの `ExpandToEnclosingUnit(Line)` と同じ呼び出し列(`LineEnd` → `LineStartOf` → `LineEndNoBreakOf`)。
  - **S9a**: UI スレッドはメッセージを汲むだけ(アイドル)。**S9b**: 汲む合間に全面再描画を挟む(描画中の say all。調査記録の「描画処理の後ろに並ぶので数 ms〜数十 ms 待つ」を再現する)。
  - `param` 列に **1 歩あたりの Invoke 回数**を出す(Task 1 で足すテストフックで数える)。変更前は 3.00、変更後は論理行が変わる歩だけミスするので約 0.5 になる見込み(ja10k の 1 行は 72 桁 → 40 桁で 2 視覚行)。
- 変更前の計測(Task 1)は、Task 1 の commit(テストフックと S9 だけを足し、製品の挙動は変えない)で行う。変更後(Task 3)は Task 2 の後。
- `--scenario S9` だけを、変更前後で**各 3 回**走らせる。「3 run の中央値の中央値」と「3 run 通した min–max」で比べる(設計書 §3.2 の判断基準)。
- NVDA は起動したままでよい(ユーザー判断 2026-09-25)。他のエージェントの重い処理と並走させない。

### 0.2 設計書からの精密化・逸脱

- **LRU にしない(単一エントリのまま)**(設計書 §15.1「任意: 数件の LRU」の判断)。say all の 1 歩は「今の行で `LineEnd` → 次の行で `LineStartOf` / `LineEndNoBreakOf`」なので、ミスは**まだ一度も求めていない次の論理行**でしか起きない。LRU は既に求めた行へ戻る場合にしか効かず、say all の「次の行」のミスは減らせない。上下矢印も 1 回の読み上げの中では同じ行しか見ない。YAGNI として採らない。S9 の Invoke 回数(約 0.5 回/歩 = 論理行ごとに 1 回)が見込みどおりかで確かめる。
- **空行は RPC スレッドで即答する**(設計書に明記はないが挙動不変の精密化)。空行は `LineLayout.Wrap` を呼ぶ前に null を返す(従来の `TryFindVisualSegmentCore` の分岐)ので、キャッシュに載らず毎回 Invoke していた。判定は `TextSnapshot` だけで済む(スレッド安全)ので、Handle のガードの後・キャッシュ照合の前で null を返す。
- **Invoke の後にもう一度キャッシュを見る**。Invoke を待つ間に、別の問い合わせが同じ行を埋めていることがあるため(従来の `TryFindVisualSegmentCore` の先頭の照合と同じ位置)。
- **正しさの論証**(レビューで確認する): `Segs = LineLayout.Wrap(snap の line 行の本文, Wrap × metrics.MeasureRun("0"), metrics)` なので、`Segs` は (Snap, Line, Wrap, Metrics) の関数である。エントリは UI スレッドで、実際に使った `wrap` と `metrics` をキーにして作り、全フィールドを設定してから volatile 書き込みで公開する。RPC スレッドは volatile 読みを 1 回だけ行い、自分が読んだ (snap, line, wrap, metrics) とキーを照合する。一致すれば、返す答えは「RPC スレッドが読んだ条件に対する正しい答え」であり、条件の読みは従来から RPC スレッドで行っている(snap・wrap)。`metrics` は `ApplyAppearance` のたびに新しいインスタンスになるので、参照の比較でフォントの変化を区別できる。`ApplyAppearance` の先頭での破棄は防御の二重化(設計書 §15.1)。
- **Hit/Miss の数え方は従来と同じ意味を保つ**: ミス = UI スレッドで `LineLayout.Wrap` を求めた回数、ヒット = キャッシュの答えを使った回数(RPC スレッドでのヒットも数える)。空行はどちらにも数えない(従来どおり)。既存の `EditorControlCacheTests` の期待値(3 呼び出しで Miss 1・Hit 2)はそのまま通る。

### 0.3 レビューとミューテーション検証

- 前倒しの脆弱性レビュー: 該当なし(外部入力のパース・パス・プロセス・WebView・ネットワークに触れない)。
- 前倒しのコード品質レビュー: 該当なし(後続タスクが依存する seam を導入しない)。ただし Task 2 の仕様レビューでは、**スレッド安全性の論証(0.2)**を必ず確認項目に入れる。
- **ミューテーション検証: 行わない**。設計書 §3.4 はフェーズ 10 を列挙していない。CLAUDE.md §4-A の列挙(カーソル移動・選択範囲・Undo・検索置換のパース・Lexer)にも当たらない(SR への行境界の応答で、境界の算出ロジック自体は変えない)。等価性はテスト(全オフセットの突き合わせ)で確かめる。ユーザーのグローバル規約「原則実施しない」にも従う。

### 0.4 L5

- **必須**(設計書 §4・§15.2)。SR の経路(`UiaTextHostAdapter`)に触れる。
  - `tools/sr-regression.ps1`(UIA 応答の回帰)
  - NVDA 実機: 折り返し ON で、上下矢印と say all の読み上げが、変更前(main)と変更後で同じであること。
- 実施前にユーザーの了承を取る(キーボードとマウスを占有する)。

### 0.5 意図的な挙動差

- なし。

---

## Task 0: 本計画を commit する(docs のみ)

**Files:**
- Create: `docs/plans/2026-09-26-perf-uia-wrap-cache.md`(本書)

- [ ] **Step 1: commit**

```bash
git add docs/plans/2026-09-26-perf-uia-wrap-cache.md
git commit -m "docs(perf): フェーズ 10(perf-uia-wrap-cache)の実装計画"
```

---

## Task 1: Invoke の回数のテストフックと Smoke S9 を足し、変更前を計測する

製品の挙動は変えない(Invoke の直前でカウンタを 1 増やすだけ)。

**Files:**
- Modify: `src/kxEdit.Editor/UiaTextHostAdapter.cs`(`TryFindVisualSegment` の Invoke 分岐・末尾の Test hook 節)
- Modify: `src/kxEdit.Editor/EditorControl.Uia.cs`(テストフックの転送)
- Modify: `tests/kxEdit.Editor.Tests/EditorControlCacheTests.cs`(テスト 1 本)
- Modify: `tests/kxEdit.Editor.Smoke/PerfBench.cs`(S9)
- Modify: `tools/README.md`(Smoke `--perf` の表に S9 の行)

**Interfaces:**
- Produces: `internal long EditorControl.TestHook_LineSegsInvokeCount`(`TestHook_ResetLastLineSegsCounters()` で 0 に戻る)。Task 2 のテストと S9 が使う。
- Produces: `dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --perf --scenario S9`(Task 3 が使う)

- [ ] **Step 1: 失敗するテストを書く**

`tests/kxEdit.Editor.Tests/EditorControlCacheTests.cs` の `MakeControl` の直後にヘルパーを、クラスの末尾にテストを足す:

```csharp
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
```

```csharp
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
```

- [ ] **Step 2: テストが失敗する(コンパイルエラー)ことを確かめる**

Run: `dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~EditorControlCacheTests"`
Expected: FAIL(`TestHook_LineSegsInvokeCount` が未定義)

- [ ] **Step 3: テストフックを実装する**

`src/kxEdit.Editor/UiaTextHostAdapter.cs` の `TryFindVisualSegment` の `if (_host.InvokeRequired)` の中、`try` の直前に:

```csharp
            // フェーズ 10: UI スレッドへマーシャリングした回数(テストと Smoke S9 が観測する)。
            Interlocked.Increment(ref _testHook_lineSegsInvokeCount);
```

同じファイル末尾の `// === Test hook (Editor.Tests から観測) ===` 節を次に置き換える(Hit/Miss は Task 2 で Interlocked にするので、ここでは触らない):

```csharp
    // === Test hook (Editor.Tests から観測) ===

    // Editor.Tests から観測するためのヒットカウンタ(internal・テスト以外の呼び出しは想定しない)。
    internal long TestHook_LastLineSegsHitCount { get; private set; }
    internal long TestHook_LastLineSegsMissCount { get; private set; }

    // フェーズ 10: TryFindVisualSegment が UI スレッドへ同期 Invoke した回数。RPC スレッドから加算する。
    private long _testHook_lineSegsInvokeCount;

    internal long TestHook_LineSegsInvokeCount => Interlocked.Read(ref _testHook_lineSegsInvokeCount);

    internal void TestHook_ResetLastLineSegsCounters()
    {
        TestHook_LastLineSegsHitCount = 0;
        TestHook_LastLineSegsMissCount = 0;
        Interlocked.Exchange(ref _testHook_lineSegsInvokeCount, 0);
    }
```

(`Interlocked` は `System.Threading`。ImplicitUsings で解決しなければ `using System.Threading;` を足す。)

`src/kxEdit.Editor/EditorControl.Uia.cs` の `TestHook_LastLineSegsMissCount` の直後に:

```csharp
    /// <summary>
    /// 折り返し ON の行問い合わせが UI スレッドへ同期 Invoke した回数
    /// (フェーズ 10。Editor.Tests EditorControlCacheTests と Smoke S9)。
    /// </summary>
    internal long TestHook_LineSegsInvokeCount => _uia.TestHook_LineSegsInvokeCount;
```

`TestHook_ResetLastLineSegsCounters` の summary を「segs キャッシュのヒット/ミス/Invoke のカウンタをリセット。」にする。

- [ ] **Step 4: テストが通ることを確かめる**

Run: `dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~EditorControlCacheTests|FullyQualifiedName~UiaTextHostAdapterTests|FullyQualifiedName~EditorControlUiaHostTests"`
Expected: PASS(全件)

- [ ] **Step 5: Smoke に S9 を足す**

`tests/kxEdit.Editor.Smoke/PerfBench.cs`:

1. `AllScenarios` の `"S8",` の後に `"S9",` を足す。
2. `Run` の `if (opt.Scenarios.Contains("S5")) results.AddRange(MeasureAppendBlock(editor));` の**後ろ**(最後に走らせる=既存シナリオの JIT 状態を変えない)に:

```csharp
            if (opt.Scenarios.Contains("S9"))
                results.AddRange(
                    MeasureUiaWrapLines(editor, Fresh(docs[0].Text), docs[0].Name, opt)
                );
```

3. `MeasureUiaRects` の後ろに 2 メソッドを足す:

```csharp
    /// <summary>
    /// S9(P-10・フェーズ 10): 折り返し ON(40 桁)で、NVDA の say all 相当の行読みを<b>ワーカースレッドから</b>
    /// 行う(UIA の RPC スレッドの代わり)。1 歩 = <c>TextRangeProviderV2.Move(Line, 1)</c> +
    /// 非退化レンジの <c>ExpandToEnclosingUnit(Line)</c> と同じ呼び出し列
    /// (<c>LineEnd</c> → <c>LineStartOf</c> → <c>LineEndNoBreakOf</c>)。
    /// S9a は UI スレッドがメッセージを汲むだけ(アイドル)、S9b は汲む合間に全面再描画を挟む
    /// (描画中の say all)。<c>param</c> 列は 1 歩あたりの UI スレッドへの Invoke 回数。
    /// </summary>
    private static List<Result> MeasureUiaWrapLines(
        EditorControl editor,
        TextBuffer buffer,
        string doc,
        Options opt
    )
    {
        const int Wrap = 40;
        editor.SetOrReplaceSource(buffer);
        editor.SetCaretCharOffset(0);
        editor.TopLine = 0;
        int oldWrap = editor.WrapColumns;
        editor.WrapColumns = Wrap;
        editor.Update();
        var host = (IUiaTextHost)editor;
        try
        {
            var snap = buffer.Current;
            Check(
                host.LineEnd(0) < snap.GetLineEnd(0, includeBreak: false),
                $"S9/{doc}: 行 0 が折り返されていない(折り返し ON の経路を測れていない)"
            );
            return
            [
                MeasureWorkerLineReads(editor, host, "S9a", doc, opt, busyUi: false),
                MeasureWorkerLineReads(editor, host, "S9b", doc, opt, busyUi: true),
            ];
        }
        finally
        {
            editor.WrapColumns = oldWrap;
        }
    }

    /// <summary>
    /// S9 の 1 本。文書先頭から <c>Warmup + N</c> 歩、ワーカースレッドで行を読み進め、1 歩ずつ測る。
    /// その間 UI スレッドは <c>Application.DoEvents</c> で Invoke を汲む(<paramref name="busyUi"/> なら
    /// 汲む前に毎回 <c>Invalidate</c> + <c>Update</c> で全面を描く)。
    /// </summary>
    private static Result MeasureWorkerLineReads(
        EditorControl editor,
        IUiaTextHost host,
        string id,
        string doc,
        Options opt,
        bool busyUi
    )
    {
        var samples = new List<Sample>(opt.N);
        long invokes0 = 0;
        long invokes1 = 0;
        CollectGarbage();
        var worker = Task.Run(() =>
        {
            int pos = 0;
            for (int k = 0; k < opt.Warmup + opt.N; k++)
            {
                if (k == opt.Warmup)
                    invokes0 = editor.TestHook_LineSegsInvokeCount;
                int p0 = Volatile.Read(ref s_paints);
                long t0 = Stopwatch.GetTimestamp();
                int next = host.LineEnd(pos);
                int start = host.LineStartOf(next);
                int end = host.LineEndNoBreakOf(next);
                double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (next <= pos || start != next || end <= start)
                    throw new PerfSelfCheckException(
                        $"{id}/{doc}: 行読みが進まない(pos {pos} → {next}・[{start}, {end}))"
                    );
                if (k >= opt.Warmup)
                    samples.Add(new Sample(ms, Volatile.Read(ref s_paints) - p0));
                pos = next;
            }
            invokes1 = editor.TestHook_LineSegsInvokeCount;
        });
        while (!worker.IsCompleted)
        {
            if (busyUi)
            {
                editor.Invalidate();
                editor.Update();
            }
            Application.DoEvents();
        }
        worker.GetAwaiter().GetResult(); // ワーカーの例外(自己チェックの失敗を含む)をそのまま投げ直す
        double invokesPerStep = (double)(invokes1 - invokes0) / opt.N;
        return Summarize(
            id,
            doc,
            invokesPerStep.ToString("F2", CultureInfo.InvariantCulture),
            samples
        );
    }
```

4. クラスの `<remarks>` の「測らないもの」にある「S8 の RPC スレッドからの Invoke によるマーシャリング」の後ろに「(RPC スレッドからの折り返し ON の行読みは S9 がワーカースレッドで測る)」を足す。

`Task`・`Volatile` が解決しなければ `using System.Threading;` / `using System.Threading.Tasks;` を足す。

- [ ] **Step 6: tools/README.md に S9 を足す**

`| S8-1 / 40 / 1000 / all | ... |` の行の後に:

```markdown
| S9a / S9b | P-10: 折り返し ON(40 桁)の ja10k で、say all 相当の行読み(`LineEnd` → `LineStartOf` → `LineEndNoBreakOf` を 1 歩)を**ワーカースレッドから**。S9a は UI スレッドがメッセージを汲むだけ、S9b は汲む合間に全面再描画を挟む。`param` 列は 1 歩あたりの UI スレッドへの Invoke 回数 |
```

同じ節の「外観は製品の既定(ＭＳ ゴシック 12pt・行番号なし・折り返しなし)」を「外観は製品の既定(ＭＳ ゴシック 12pt・行番号なし・折り返しなし。S9 だけ折り返し 40 桁)」にする。

- [ ] **Step 7: commit し、commit 後の状態で build と test**

```bash
git add src/kxEdit.Editor/UiaTextHostAdapter.cs src/kxEdit.Editor/EditorControl.Uia.cs tests/kxEdit.Editor.Tests/EditorControlCacheTests.cs tests/kxEdit.Editor.Smoke/PerfBench.cs tools/README.md
git commit -m "test(perf): 折り返し ON の行問い合わせの Invoke 回数フックと Smoke S9 を足す"
```

Run: `dotnet build kxEdit.sln -c Release` → 0 warning / 0 error
Run: `dotnet test tests/kxEdit.Editor.Tests -c Release --filter "FullyQualifiedName~EditorControlCacheTests"` → PASS

- [ ] **Step 8: 変更前を計測する(3 回)**

他の重い処理を止め、画面をロックしない状態で:

```powershell
$out = "<scratchpad>\perf-uia-wrap-cache"
New-Item -ItemType Directory -Force $out | Out-Null
1..3 | ForEach-Object { dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --perf --scenario S9 --json "$out\before-$_.json" }
```

Expected: 各回 EXIT 0。S9a / S9b の `param` が `3.00`(毎回 Invoke している)。EXIT 1(自己チェックの失敗)なら値を使わず原因を調べる。

- [ ] **Step 9: 計測値を本計画の「実施記録」に記録する(commit は Task 3 でまとめる)**

本書末尾の「実施記録」に、S9a / S9b それぞれの 3 run の median・min・max と `param` を表で書く(Task 3 まで未 commit のまま持ち越してよい。後続タスクの commit に混ざらないよう、Task 2 の `git add` はファイルを明示する)。

---

## Task 2: キャッシュを不変クラスの volatile 参照にし、ヒット時は Invoke しない

**Files:**
- Modify: `src/kxEdit.Editor/UiaTextHostAdapter.cs`
- Modify: `src/kxEdit.Editor/EditorControl.cs`(`ApplyAppearance` の先頭)
- Test: `tests/kxEdit.Editor.Tests/EditorControlCacheTests.cs`

**Interfaces:**
- Consumes: `EditorControl.TestHook_LineSegsInvokeCount`(Task 1)、`EditorControl.TestHook_LastLineSegsHitCount` / `MissCount` / `TestHook_ResetLastLineSegsCounters()`(既存)
- Produces: なし(内部の変更のみ)

- [ ] **Step 1: 失敗するテストを書く**

`tests/kxEdit.Editor.Tests/EditorControlCacheTests.cs` の末尾に足す:

```csharp
    [Fact]
    public void LineSegs_HitFromWorkerThread_AnswersWithoutInvoke() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdefghij", 4);
            using (f)
            using (c)
            {
                var host = (IUiaTextHost)c;
                // UI スレッドで行 0 を埋める。offset 6 は 2 つ目の視覚行 [4,8)(既定の位置と区別する)。
                Assert.Equal(4, host.LineStartOf(6));
                Assert.Equal(8, host.LineEnd(6));
                Assert.Equal(8, host.LineEndNoBreakOf(6));
                c.TestHook_ResetLastLineSegsCounters();

                var worker = System.Threading.Tasks.Task.Run(() =>
                    (host.LineStartOf(6), host.LineEnd(6), host.LineEndNoBreakOf(6))
                );
                // まず汲まずに待つ。Invoke していれば UI スレッドが応えないので時間内に終わらない
                // (STA の待機が一部のメッセージを汲むことがあるので、決め手は下の Invoke 回数)。
                bool doneWithoutPump = worker.Wait(2000);
                PumpUntil(worker); // 失敗時にワーカーを解放してから assert する
                Assert.True(doneWithoutPump, "キャッシュにヒットしたのに UI スレッドを待った");
                Assert.Equal((4, 8, 8), worker.Result);
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
                Assert.Equal(4, host.LineStartOf(6)); // キャッシュを埋める(視覚行の先頭 4)
                f.Dispose(); // 子の EditorControl も破棄され、Handle が無くなる
                Assert.False(c.IsHandleCreated);
                // Handle のガードはキャッシュより前: 論理行の先頭 0 に落ちる(視覚行の 4 ではない)。
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
                    new kxEdit.Core.Settings.AppSettings { WrapColumnEnabled = true, WrapColumn = 4 }
                );
                Assert.Equal(4, c.WrapColumns); // 前提
                Assert.Equal(4, host.LineStartOf(6));
                Assert.Equal(1, c.TestHook_LastLineSegsMissCount);
                Assert.Equal(0, c.TestHook_LastLineSegsHitCount);
            }
        });

    [Fact]
    public void LineSegs_SweepFromWorkerThread_MatchesUncachedUiThreadAnswers() =>
        Sta.Run(() =>
        {
            // 折り返し・空行・日本語(全角 2 桁)・CRLF・改行なしの最終行を含む。
            const string Text = "abcdefghij\r\n\r\nあいうえおかきくけこ\r\nxy\r\nlast-line-no-break";
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
                        actual[o] = (host.LineStartOf(o), host.LineEnd(o), host.LineEndNoBreakOf(o));
                    return actual;
                });
                PumpUntil(worker, timeoutMs: 10_000);
                Assert.True(worker.IsCompleted, "ワーカーの掃引が終わらない");
                Assert.Equal(expected, worker.Result);
                Assert.True(c.TestHook_LastLineSegsHitCount > 0); // 前提: ヒットの経路を通った
            }
        });
```

(`AppSettings` の名前空間が `kxEdit.Core.Settings` でなければ、`PerfBench.cs` の `using` に合わせる。)

- [ ] **Step 2: テストが失敗することを確かめる**

Run: `dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~EditorControlCacheTests"`
Expected: `LineSegs_HitFromWorkerThread_AnswersWithoutInvoke` と `LineSegs_EmptyLineFromWorkerThread_AnswersWithoutInvoke` が FAIL(Invoke 回数が 0 でない)。残りの 3 本は現行コードでも PASS してよい(Handle のガードの順序・ApplyAppearance 末尾の破棄・値の等価性は従来から成り立つ。変更で壊さないための網)。

- [ ] **Step 3: 実装する**

`src/kxEdit.Editor/UiaTextHostAdapter.cs`:

(a) ファイル先頭のコメントの

```csharp
//       UI スレッド専用状態を要する読み取り (GetBoundingRectangles / OffsetFromScreenPoint /
//         GetVisibleRange / 折り返し ON の TryFindVisualSegment) = 同期 Invoke
```

を

```csharp
//       UI スレッド専用状態を要する読み取り (GetBoundingRectangles / OffsetFromScreenPoint /
//         GetVisibleRange / 折り返し ON の TryFindVisualSegment のキャッシュミス) = 同期 Invoke
//       折り返し ON の TryFindVisualSegment のキャッシュヒット・空行 = RPC スレッドで即答 (フェーズ 10)
```

にする。

(b) `_lastLineSegs` のコメントと宣言(`// P8 Minor-5: SR の Line 単位連続読み…` から `private (TextSnapshot Snap, …)? _lastLineSegs;` まで)を次に置き換える:

```csharp
    // P8 Minor-5: SR の Line 単位連続読み(LineStartOf/LineEndNoBreakOf/LineEnd)で
    // 同一 (snap, logicalLine, wrap) が繰り返されるため単一エントリキャッシュ。
    // フェーズ 10(P-10・2026-09-26): 不変の LineSegsCache の volatile 参照にし、RPC スレッドも読む。
    // キーが一致すれば RPC スレッドで即答し、UI スレッドへ Invoke しない。書くのは UI スレッドだけ
    // (TryFindVisualSegmentCore のミス時)。
    // 無効化ポイント: OnSnapshotChanged / InvalidateLastLineSegs (WrapColumns setter /
    // ApplyAppearance の先頭と末尾から)。
    // 設計原則: _bufferSnapshot を更新するすべての経路で _lastLineSegs も破棄する
    // (correctness はキー照合で守られるが、旧 TextSnapshot の Root PieceTree が強参照で
    //  pin されて大容量ファイル差替後の GC を阻害するため)。
    private volatile LineSegsCache? _lastLineSegs;

    /// <summary>
    /// フェーズ 10: 論理行 1 本ぶんの視覚セグメント列と、それを求めた条件(キー)の不変の組。
    /// </summary>
    /// <remarks>
    /// <see cref="Segs"/> は <c>LineLayout.Wrap(Snap の Line 行の本文, Wrap × Metrics.MeasureRun("0"), Metrics)</c>
    /// なので、キー (Snap, Line, Wrap, Metrics) だけで決まる。よってキーが一致すれば、どのスレッドが
    /// いつ読んでも答えは「そのキーの条件に対して正しい」。Metrics は ApplyAppearance のたびに
    /// 新しいインスタンスになるので参照で比べる(RPC スレッドはメソッドを呼ばない)。
    /// </remarks>
    private sealed class LineSegsCache
    {
        public LineSegsCache(
            TextSnapshot snap,
            int line,
            int wrap,
            ICharMetrics metrics,
            IReadOnlyList<WrapSegment> segs
        )
        {
            Snap = snap;
            Line = line;
            Wrap = wrap;
            Metrics = metrics;
            Segs = segs;
        }

        public TextSnapshot Snap { get; }
        public int Line { get; }
        public int Wrap { get; }
        public ICharMetrics Metrics { get; }
        public IReadOnlyList<WrapSegment> Segs { get; }

        public bool Matches(TextSnapshot snap, int line, int wrap, ICharMetrics metrics) =>
            ReferenceEquals(Snap, snap)
            && Line == line
            && Wrap == wrap
            && ReferenceEquals(Metrics, metrics);
    }
```

(c) `TryFindVisualSegment` の `<remarks>` の末尾(`…Handle 未生成時(SetSource 前)は null=論理行フォールバック。` の後)に 1 段落足す:

```csharp
    /// フェーズ 10(P-10): キャッシュにヒットするとき(と空行のとき)は Invoke せずに即答する。
    /// Handle のガードはキャッシュより前に置く(teardown 後は、キャッシュが残っていても論理行へ
    /// フォールバックする従来の挙動を保つ)。
```

(d) `TryFindVisualSegment` と `TryFindVisualSegmentCore` の本体を次に置き換える(`TryFindVisualSegment` の Invoke 分岐の Task 1 のカウンタは残す):

```csharp
    private WrapSegment? TryFindVisualSegment(TextSnapshot snap, int line, int offsetInLine)
    {
        int wrap = _host.WrapColumns;
        if (wrap <= 0)
            return null;
        if (!_host.IsHandleCreated)
            return null; // UI スレッドが束縛されていない=論理行フォールバック
        // 空行は視覚セグメントを持たない(TryFindVisualSegmentCore も null を返す)。
        // 不変の snap だけで判定できるので、RPC スレッドでも Invoke せずに答える。
        if (snap.GetLineStart(line) == snap.GetLineEnd(line, includeBreak: false))
            return null;
        // フェーズ 10: キーが一致すれば RPC スレッドでも即答する(LineSegsCache の remarks)。
        // _host.Metrics は参照を読むだけ(比較にしか使わない)。
        if (TryGetCachedSegs(snap, line, wrap, _host.Metrics, out var cached))
            return VisualSegments.FindContaining(cached, offsetInLine).Segment;
        if (_host.InvokeRequired)
        {
            // フェーズ 10: UI スレッドへマーシャリングした回数(テストと Smoke S9 が観測する)。
            Interlocked.Increment(ref _testHook_lineSegsInvokeCount);
            try
            {
                return _host.Invoke(
                    new Func<WrapSegment?>(() =>
                        TryFindVisualSegmentCore(snap, line, offsetInLine, wrap)
                    )
                );
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            } // Handle 破棄との race
        }
        return TryFindVisualSegmentCore(snap, line, offsetInLine, wrap);
    }

    /// <summary>UI スレッド上での視覚セグメント検索本体(<see cref="TryFindVisualSegment"/> から Invoke マーシャリング後)。</summary>
    private WrapSegment? TryFindVisualSegmentCore(
        TextSnapshot snap,
        int line,
        int offsetInLine,
        int wrap
    )
    {
        var metrics = _host.Metrics;
        // Invoke を待つ間に、別の問い合わせが同じ行を埋めていることがあるので、もう一度見る。
        if (!TryGetCachedSegs(snap, line, wrap, metrics, out var segs))
        {
            int logicalStart = snap.GetLineStart(line);
            int logicalEnd = snap.GetLineEnd(line, includeBreak: false);
            if (logicalStart == logicalEnd)
                return null;
            string lineText = snap.GetText(logicalStart, logicalEnd - logicalStart);
            int maxWidthPx = wrap * metrics.MeasureRun("0".AsSpan());
            segs = LineLayout.Wrap(lineText.AsSpan(), maxWidthPx, metrics);
            // 全フィールドを設定したインスタンスを volatile 書き込みで公開する(RPC スレッドが読む)。
            // キーは実際に使った wrap と metrics(Segs がキーだけの関数であることを保つ)。
            _lastLineSegs = new LineSegsCache(snap, line, wrap, metrics, segs);
            Interlocked.Increment(ref _testHook_lastLineSegsMissCount);
        }

        return VisualSegments.FindContaining(segs, offsetInLine).Segment;
    }

    /// <summary>
    /// キャッシュのキーが (<paramref name="snap"/>, <paramref name="line"/>, <paramref name="wrap"/>,
    /// <paramref name="metrics"/>) と一致すれば、その視覚セグメント列を返す。どのスレッドから呼んでもよい。
    /// </summary>
    private bool TryGetCachedSegs(
        TextSnapshot snap,
        int line,
        int wrap,
        ICharMetrics metrics,
        out IReadOnlyList<WrapSegment> segs
    )
    {
        var c = _lastLineSegs; // volatile 読みは 1 回だけ(照合と Segs の取り出しを同じ組で行う)
        if (c is not null && c.Matches(snap, line, wrap, metrics))
        {
            segs = c.Segs;
            Interlocked.Increment(ref _testHook_lastLineSegsHitCount);
            return true;
        }
        segs = Array.Empty<WrapSegment>();
        return false;
    }
```

(e) 末尾の Test hook 節の Hit/Miss を Interlocked にする(Task 1 の Invoke のカウンタと同じ形):

```csharp
    // === Test hook (Editor.Tests から観測) ===

    // Editor.Tests から観測するためのヒット/ミスのカウンタ(internal・テスト以外の呼び出しは想定しない)。
    // フェーズ 10: ヒットは RPC スレッドからも加算するので Interlocked にする。
    private long _testHook_lastLineSegsHitCount;
    private long _testHook_lastLineSegsMissCount;

    internal long TestHook_LastLineSegsHitCount =>
        Interlocked.Read(ref _testHook_lastLineSegsHitCount);
    internal long TestHook_LastLineSegsMissCount =>
        Interlocked.Read(ref _testHook_lastLineSegsMissCount);

    // フェーズ 10: TryFindVisualSegment が UI スレッドへ同期 Invoke した回数。RPC スレッドから加算する。
    private long _testHook_lineSegsInvokeCount;

    internal long TestHook_LineSegsInvokeCount => Interlocked.Read(ref _testHook_lineSegsInvokeCount);

    internal void TestHook_ResetLastLineSegsCounters()
    {
        Interlocked.Exchange(ref _testHook_lastLineSegsHitCount, 0);
        Interlocked.Exchange(ref _testHook_lastLineSegsMissCount, 0);
        Interlocked.Exchange(ref _testHook_lineSegsInvokeCount, 0);
    }
```

`ICharMetrics` は `kxEdit.Core.Layout`(既に `using` 済み)。

`src/kxEdit.Editor/EditorControl.cs` の `ApplyAppearance` の `ArgumentNullException.ThrowIfNull(settings);` の直後に:

```csharp
        // フェーズ 10(P-10): RPC スレッドが _lastLineSegs を読むようになったので、先頭でも破棄する
        // (途中の状態でヒットさせず、ここより前の答えとして線形化する。キーの Metrics 照合との二重化)。
        _uia.InvalidateLastLineSegs();
```

- [ ] **Step 4: テストが通ることを確かめる**

Run: `dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~EditorControlCacheTests|FullyQualifiedName~UiaTextHostAdapter|FullyQualifiedName~EditorControlUiaHostTests|FullyQualifiedName~Uia"`
Expected: PASS(全件)

- [ ] **Step 5: commit し、commit 後の状態で build と全テスト**

```bash
git add src/kxEdit.Editor/UiaTextHostAdapter.cs src/kxEdit.Editor/EditorControl.cs tests/kxEdit.Editor.Tests/EditorControlCacheTests.cs
git commit -m "perf(uia): 折り返し ON の行問い合わせがキャッシュにヒットしたら Invoke しない(P-10)"
```

Run: `dotnet build kxEdit.sln -c Release` → 0 warning / 0 error
Run: `dotnet test kxEdit.sln -c Release` → 全件 PASS

- [ ] **Step 6: 陰性対照(3 つ)**

それぞれ実装を一時的に戻し、`-p:TreatWarningsAsErrors=false` でビルドして、対象のテストが落ちることを確かめる。確かめたら `git checkout -- <file>` で戻す。

1. `TryFindVisualSegment` のキャッシュ照合の `if (TryGetCachedSegs(...)) return ...;` を消す → `LineSegs_HitFromWorkerThread_AnswersWithoutInvoke` が FAIL。
2. 空行の `if (snap.GetLineStart(line) == ...) return null;` を消す → `LineSegs_EmptyLineFromWorkerThread_AnswersWithoutInvoke` が FAIL。
3. Handle のガード `if (!_host.IsHandleCreated) return null;` をキャッシュ照合の後ろへ移す → `LineSegs_AfterHandleDestroyed_FallsBackToLogicalLine_EvenWithCachedSegs` が FAIL。

結果(落ちたテスト名)を本計画の実施記録に書く。

---

## Task 3: 変更後の計測・sr-regression・実施記録

**Files:**
- Modify: `docs/plans/2026-09-26-perf-uia-wrap-cache.md`(実施記録)

- [ ] **Step 1: 変更後を計測する(3 回)**

Task 1 Step 8 と同じ条件で:

```powershell
1..3 | ForEach-Object { dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --perf --scenario S9 --json "$out\after-$_.json" }
```

Expected: 各回 EXIT 0。`param` が約 0.5(論理行が変わる歩だけ Invoke)。S9a / S9b の median が変更前の min–max を下回る(設計書 §3.2)。下回らない場合は、実施記録に書いてユーザーに採否を判断してもらう。

- [ ] **Step 2: sr-regression**

Run: `pwsh -File tools/sr-regression.ps1`
Expected: EXIT 0(手順と前提は tools/README.md の該当節に従う)。

- [ ] **Step 3: 実施記録を書いて commit**

本書末尾の「実施記録」に、変更前後の表(S9a / S9b の 3 run の median・min・max・`param`)、判断基準の結果、陰性対照の結果、sr-regression の結果を書く。

```bash
git add docs/plans/2026-09-26-perf-uia-wrap-cache.md
git commit -m "docs(perf): フェーズ 10 の計測と実施記録"
```

---

## Task 4: L5(NVDA 実機)

実施前にユーザーの了承を取る(キーボードとマウスを占有する)。

- [ ] **Step 1: 変更前(main)と変更後の publish を用意する**

```powershell
git worktree add <scratchpad>\l5-before main
dotnet publish <scratchpad>\l5-before\src\kxEdit.App -c Release -o <scratchpad>\pub-before
dotnet publish src\kxEdit.App -c Release -o <scratchpad>\pub-after
```

(publish の出力をそのまま使う。`dotnet build` の出力を手でコピーしない。)

- [ ] **Step 2: 両方で同じ操作を行い、NVDA の読み上げを比べる**

- 文書: 1 行が 2〜3 視覚行に折り返される日本語の行と、空行・英語の行を混ぜた 20 行程度(scratchpad に UTF-8 で作る)。
- 設定: 折り返し ON(40 桁)。
- 操作: 先頭から ↓ を 10 回、↑ を 10 回。先頭へ戻って say all(NVDA+↓)を数行ぶん。
- 読み上げはスピーチビューアーから WM_GETTEXT で取る(手順と罠は L5 自動化のメモリー・tools/README に従う)。
- 判定: 変更前と変更後の読み上げの列が一致すること。

- [ ] **Step 3: 実施記録に結果を追記して commit**

```bash
git add docs/plans/2026-09-26-perf-uia-wrap-cache.md
git commit -m "docs(perf): フェーズ 10 の L5 の結果"
```

---

## Task 5: 最終レビュー・品質ゲート・PR

- [ ] **Step 1: 最終ブランチレビュー(2 パス・別エージェント)**
  - コード品質パス(スレッド安全性の論証 0.2 の確認を含む)
  - 脆弱性パス
  - 指摘は fixup commit で反映する(CLAUDE.md §4)。
- [ ] **Step 2: 設計書 §15 の末尾に「15.3 実施記録(2026-09-26・PR #<番号>)」を追記する**(フェーズ 8 と同じく PR に同梱する)。
- [ ] **Step 3: `pwsh -File tools/pre-merge-check.ps1` → EXIT 0**
- [ ] **Step 4: push → PR 作成**(日本語。変更前後の計測値・意図的な挙動差なし・L5 の結果・レビューの経緯)

---

## 実施記録

### (1) 計測(S9・変更前後 3 run ずつ)

コマンド(1 run。変更前は Task 1、変更後は Task 3 で実行):

```powershell
1..3 | ForEach-Object { dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --perf --scenario S9 --json "<out>\{before,after}-$_.json" }
```

6 run(変更前 3・変更後 3)とも **EXIT 0**(自己チェック失敗なし)。計測環境は変更前後で同一:
DeviceDpi=96 / ClientSize=884x661 / LineHeightPx=16 / n=200 / warmup=20 / Release / .NET 9.0.20 /
Windows 10.0.26200。

#### S9a(折り返し ON・UI スレッドはアイドルで汲むだけ)

| 状態 | run | median_ms | min_ms | max_ms | param(Invoke/歩) |
|---|---|---|---|---|---|
| 変更前 | 1 | 0.339 | 0.209 | 4.263 | 3.01 |
| 変更前 | 2 | 0.359 | 0.108 | 4.447 | 3.01 |
| 変更前 | 3 | 0.349 | 0.121 | 4.697 | 3.01 |
| 変更後 | 1 | 0.086 | 0.003 | 3.280 | 0.50 |
| 変更後 | 2 | 0.097 | 0.003 | 3.185 | 0.50 |
| 変更後 | 3 | 0.071 | 0.003 | 4.174 | 0.50 |

- 変更前: median の中央値 = 0.349ms、3 run 通した min–max = [0.108, 4.697]ms。
- 変更後: median の中央値 = 0.086ms、3 run 通した min–max = [0.003, 4.174]ms。
- 判断(設計書 §3.2): 変更後の median の中央値(0.086ms)が変更前の min–max の下限(0.108ms)を
  下回る → **改善を達成**。

#### S9b(折り返し ON・UI スレッドは汲む合間に全面再描画)

| 状態 | run | median_ms | min_ms | max_ms | param(Invoke/歩) |
|---|---|---|---|---|---|
| 変更前 | 1 | 19.035 | 12.402 | 27.010 | 3.00 |
| 変更前 | 2 | 19.072 | 6.098 | 20.861 | 3.00 |
| 変更前 | 3 | 18.969 | 6.746 | 25.368 | 3.00 |
| 変更後 | 1 | 3.065 | 0.003 | 13.842 | 0.51 |
| 変更後 | 2 | 0.229 | 0.003 | 16.233 | 0.51 |
| 変更後 | 3 | 0.505 | 0.003 | 8.216 | 0.51 |

- 変更前: median の中央値 = 19.035ms、3 run 通した min–max = [6.098, 27.010]ms。
- 変更後: median の中央値 = 0.505ms、3 run 通した min–max = [0.003, 16.233]ms。
- 判断(設計書 §3.2): 変更後の median の中央値(0.505ms)が変更前の min–max の下限(6.098ms)を
  大きく下回る → **改善を達成**。

`param`(1 歩あたりの UI スレッドへの Invoke 回数)は S9a/S9b とも、変更前は約 3.00
(1 歩 = `LineEnd`/`LineStartOf`/`LineEndNoBreakOf` の 3 呼び出しが毎回 Invoke)、変更後は約 0.50
(ja10k は 1 論理行 72 桁が折り返し 40 桁で 2 視覚行になるため、論理行が変わる歩だけ Invoke する
設計どおりの見込み値)。期待どおりの結果だった。

参考(深追いしない範囲での確認): 変更前 S9a の `param` が 3.00 ちょうどではなく 3.01(200 歩に対し
602 回 Invoke 相当)である点(Task 1 からの申し送り)について、`LineStartOf`/`LineEnd`/
`LineEndNoBreakOf`(`LineEndNoBreakOf` は内部で `LineEnd` を呼ぶ)の呼び出し経路を確認したところ、
1 歩あたり `TryFindVisualSegment` を通る箇所は 3 回で説明がつくが、+2 回(600→602)の出所は
コードレビューだけでは特定できなかった。600 に対し 2 回(0.3%)の小さな誤差であり、変更後の
約 0.50 との差(6 倍以上)には影響しないため、本タスクの判断には影響しない。

### (2) 陰性対照(Task 2 Step 6・3 件)

| # | 変異内容 | 落ちたテスト |
|---|---|---|
| 1 | `TryFindVisualSegment` のキャッシュ照合 `if (TryGetCachedSegs(...)) return ...;` を削除 | `LineSegs_HitFromWorkerThread_AnswersWithoutInvoke` |
| 2 | 空行判定 `if (snap.GetLineStart(line) == snap.GetLineEnd(line, includeBreak: false)) return null;` を削除 | `LineSegs_EmptyLineFromWorkerThread_AnswersWithoutInvoke` |
| 3 | Handle ガード `if (!_host.IsHandleCreated) return null;` をキャッシュ照合の後ろへ移動 | `LineSegs_AfterHandleDestroyed_FallsBackToLogicalLine_EvenWithCachedSegs` |

各変異とも対象テストのみが FAIL し、他 8 本(`EditorControlCacheTests` 内)は PASS のままだった
(build は `-p:TreatWarningsAsErrors=false` で 0 警告/0 エラー)。3 件とも
`git checkout -- src/kxEdit.Editor/UiaTextHostAdapter.cs` で復元済み(詳細は Task 2 実施報告)。

### (3) sr-regression

`pwsh -File tools/sr-regression.ps1` → **EXIT 0**(`verify-uia-editor.ps1` 5 件・`word-sim.ps1` 6 件、
全 11 件 PASS)。判定は UIA 応答の疎通までで、実発声は検出できない(L5 は Task 4 で別途実施)。

### (4) 計画からの逸脱(コントローラ裁定)

- (a) **Task 1 の変更前計測値の記録場所**: Task 1 Step 9 は本書「実施記録」への記録を指示していたが、
  後続タスク(Task 2)の commit に未 commit の計画書編集が混ざるのを避けるため、変更前計測値は
  いったん Task 1 実施報告書に記録し、Task 3(本タスク)でまとめて本書へ転記する扱いとした
  (Task 1 実施報告の申し送りに明記済み)。
- (b) **Task 2 のテストフィクスチャの期待値**: 本計画(Task 2 Step 1)は `wrap=4` の
  `"abcdefghij"` が `[0,4)[4,8)[8,10)` に折り返す前提で固定値(`LineStartOf(6)==4` /
  `LineEnd(6)==8` / `LineEndNoBreakOf(6)==8` 等)を検証するテストを指定していたが、
  `EditorControl` の既定コンストラクタのフォント名(半角「MS ゴシック」)が実在フォントに
  解決されず比例フォント(Microsoft Sans Serif)へフォールバックする既知の未起票課題により、
  本環境の実際の折り返しは `[0,4)[4,9)[9,10)` になっていた。ctor のフォント修正は本フェーズの
  スコープ外と裁定し、代わりに該当テストを UI スレッドで実測した値を `expected` とする・
  境界値そのものは検証せず構造的前提(範囲・大小関係)のみを検証する形に修正した
  (詳細は Task 2 実施報告)。製品コードの折り返しロジック自体は変更しておらず、
  挙動不変の原則には反しない。

### (5) L5(NVDA 実機・自動)

- **実施日**: 2026-09-26。**実施方法**: 自動(操作の送出とスピーチビューアーへの WM_GETTEXT で発声を採取)。
- **比較対象**: 変更前 = `main`(8d0a6639)の publish、変更後 = 本ブランチ(6dfff0fa)の publish
  (どちらも `dotnet publish -c Release` の出力をそのまま起動)。
- **環境**: NVDA 2026.2jp・スピーチビューアー表示。発声はスピーチビューアーの RICHEDIT50W への
  WM_GETTEXT で取得。
- **条件**: 折り返し ON(40 桁)。試験文書は、2〜3 視覚行に折り返される日本語行・空行・英語行を
  混ぜた 20 行程度のもの(scratchpad に作成・非 commit)。
- **操作**: 文書先頭から ↓ を 10 回、↑ を 10 回、先頭へ戻って say all。変更前後それぞれに対して
  同一の操作列を 2 回実施。
- **判定**: **変更前後の発声列が完全一致**(↓×10・↑×10・say all のいずれも、2 回の実施を通して
  差分ゼロ)。
- **事前から存在する挙動(本フェーズの回帰ではない)**: say all が、変更前・変更後のどちらでも
  論理行 2 行ぶんを読んだところで止まり、文書末まで続かない。原因が NVDA 側の say all の継続条件か、
  kxEdit の UIA テキスト範囲(`Move`/`ExpandToEnclosingUnit`)の応答かは切り分けられていない。
  この事前挙動により、「say all を Ctrl で中断する」操作は自然終了後との比較に近くなり、中断そのものの
  比較としては意味のある結果にならなかった。→ 以後への申し送り候補(調査は別途)。

### (6) S9 の中央値の読み方(最終レビュー指摘)

S9(S9a/S9b)の `param`(1 歩あたりの Invoke 回数)は、変更後は約 0.50 である。これは
「n=200 歩のうち約 100 歩はキャッシュヒット(µs オーダーで答える歩)、残り約 100 歩はミス
(UI スレッドへの Invoke を伴う歩)」という 2 峰性の分布を平均した値であり、個々の歩の典型的な
レイテンシを代表する値ではない。この分布では中央値(200 サンプルのうち小さい方から 100 番目)が
ちょうどヒット/ミスの境界に位置するため、median 単独では「ヒットの実力」も「ミスの実コスト」も
代表しない(最終レビューのコード品質パス指摘)。

判断基準(計画 §0.1・設計書 §3.2、「median の中央値」「3 run 通した min–max」)そのものは、
変更前後で分布の**位置**が大きく異なる(変更後の min–max が変更前の min–max を明確に下回る)ことを
示すには妥当であり、「改善を達成」という結論は変わらない。ただし中央値だけでは分布の形(2 峰性)が
見えないため、参考情報として mean(平均)と p95 を、変更前後・S9a/S9b それぞれ 3 run ぶん
(scratchpad に保存した before-{1,2,3}.json / after-{1,2,3}.json の `mean_ms` / `p95_ms` フィールド)
以下に示す。

#### S9a(折り返し ON・UI スレッドはアイドルで汲むだけ)

| 状態 | run | mean_ms | p95_ms |
|---|---|---|---|
| 変更前 | 1 | 0.7495 | 2.6214 |
| 変更前 | 2 | 0.7150 | 2.4981 |
| 変更前 | 3 | 0.6965 | 2.3469 |
| 変更前 | 3 run の median | 0.7150 | 2.4981 |
| 変更後 | 1 | 0.4469 | 1.5621 |
| 変更後 | 2 | 0.3892 | 1.3893 |
| 変更後 | 3 | 0.4105 | 1.5819 |
| 変更後 | 3 run の median | 0.4105 | 1.5621 |

#### S9b(折り返し ON・UI スレッドは汲む合間に全面再描画)

| 状態 | run | mean_ms | p95_ms |
|---|---|---|---|
| 変更前 | 1 | 18.9939 | 20.4486 |
| 変更前 | 2 | 18.8711 | 20.2764 |
| 変更前 | 3 | 18.8891 | 20.5928 |
| 変更前 | 3 run の median | 18.8891 | 20.4486 |
| 変更後 | 1 | 3.2545 | 7.5266 |
| 変更後 | 2 | 3.2318 | 7.6540 |
| 変更後 | 3 | 3.2040 | 7.2650 |
| 変更後 | 3 run の median | 3.2318 | 7.5266 |

mean は S9a/S9b とも変更前後で明確に下がっている(改善の方向は median と同じ)。p95 も同様に
下がっており、「稀に遅い歩(再描画待ちなど)」を含めた分布の上側でも改善していることが分かる。
結論(改善を達成)は変わらない。

### (7) 最終レビュー

ブランチ全体レビューを、コード品質パスと脆弱性パスの 2 パス(別エージェント)で実施した。
両パスとも**マージ可**、Critical/Important の指摘はなし。

Minor の指摘は本 fix wave で反映した:
- 掃引テスト `LineSegs_SweepFromWorkerThread_MatchesUncachedUiThreadAnswers` の前提 assert が
  ワーカー開始前にカウンタをリセットしていないため実質的に空振りしていた点(expected ループ後に
  リセットし、ヒット件数 > 0 に加えて Invoke=Miss の厳密な assert を追加)。
- Task 1 由来のテスト `LineSegs_MissFromWorkerThread_InvokesOnce` のコメントと期待値が、実際には
  フォント依存で崩れている固定のセグメント境界を前提にしていた点(コメントをフォント依存の注記に
  直し、期待値を UI スレッドでの実測に置き換えた)。
- `worker.Wait(2000)` を使う 2 本(ヒット/空行)の待ち時間(スレッドプール起動の揺らぎを吸収する
  ため 5000 に延長。判定の決め手は Invoke 回数である旨をコメントに明記)。
- S9 の `param`(約 0.50)の中央値としての読み方の注記(本節 (6))。

以下は当面は対応不要(却下・受容)として扱う:
- **脆弱性パスの M-1(既存のレース。受容・以後への申し送りへ転記)**: `OnSnapshotChanged` の直後に、
  その前から進行中だった Invoke が古いスナップショットをキーにしたキャッシュエントリを書き込みうる。
  ただし `LineSegsCache.Matches` の `ReferenceEquals(Snap, snap)` により、新しいスナップショットに
  対する以後の問い合わせがそのエントリにヒットすることはない(キーが一致せず単に無駄になるだけ)。
  実害は「1 エントリぶんの無駄な書き込み」に限られ、正しさへの影響はない。任意対応として、キャッシュへ
  書き込む直前に現在のスナップショットとの `ReferenceEquals` ガードを足す案がある(本フェーズでは
  見送り)。
- **`ApplyAppearance` 先頭でのキャッシュ破棄が単独ではテストされていない**: 末尾の破棄・キーへの
  Metrics 照合との**防御の二重化**であり、単独の効果だけを切り分けて検証するテストは意義が薄いと
  判断した(却下)。
- **空行チェックが UI スレッド直接呼出の経路にも重複している**: 結果は変わらず重複コストも
  無視できるため、実害なし(却下)。
