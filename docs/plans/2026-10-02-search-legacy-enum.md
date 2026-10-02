# 検索の旧経路(search-legacy-enum)実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 表の上限を超えたときの従来経路(`TextSearcher.FindPrev` / `Locate`)で、一致ごとに `Match` を確保しないようにする。あわせて、デバウンスが遅延を最後の予約から数え直すことのテストを足す。

**Architecture:** `TextSearcher` の 2 か所の `foreach (Match m in _regex.Matches(text))` を `foreach (var m in _regex.EnumerateMatches(text))` に置き換える(`Index` と `Length` しか使っていない)。`EnumerateMatches` が `Matches` と同じ (Index, Length) の列を同じ順序で返すことは、`CollectMatches` で既に確かめている(`MatchPositionsTests.CollectMatches_yields_same_sequence_as_Matches`)。項目 28 は、製品コードを変えずにテストだけを足す。

**Tech Stack:** .NET 9 / C# / xUnit / WinForms(`System.Windows.Forms.Timer`)

**Spec:** `docs/plans/2026-09-27-perf-followups-design.md` §3(全フェーズ共通の規約)と §8(フェーズ 4)。元の設計書の関連節は `docs/plans/2026-09-24-general-perf-improvements-design.md` §10.5。

## Global Constraints

- 挙動不変(CLAUDE.md §2)。`FindPrev` / `Locate` の返り値・例外(`RegexMatchTimeoutException` の伝播)は変えない。
- 0 warning(`-warnaserror`)。pre-commit フック(CSharpier)を `--no-verify` で飛ばさない。
- L5 は不要(設計書 §3.3 の表。SR 経路に触れない)。
- 変異検証はスポットで 1〜2 個(`FindPrev` の break の条件)。**変異のたびにビルドの成功を確かめる**(`--no-build` や失敗したビルドで古い DLL が走ると「生存」に見える。設計書 §3.3 の注意)。
- 陰性対照で行を消すとアナライザー(S4487 等)でビルドが落ちることがある。条件を無効化する形で外す。
- 一時的な変異・陰性対照のコードは commit しない(終わったら `git diff` が空であることを確かめる)。
- コミットメッセージは `feat|fix|docs|test|refactor|chore(scope): 要約`+日本語本文。末尾に `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`。
- git commit が SSH 署名で無言ハングする場合は `git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit ...` を使う。

## Review Focus

- **startat より前を返す病的パターン**(`(?:b(?!a)+?)*`): `Matches` と `EnumerateMatches` で列が一致すること、`FindPrev` の break 規則(減少する列で最初の `Index >= before` で止まる)が保たれること → Task 1 の等価性テストの条件に含める。
- **ゼロ幅の一致**(`a*`・`\b`・`$`・`(?m)^`): Length=0 の一致を `Locate` が数え、同じ (Index, Length) が複数あれば最後の序数を返すこと → Task 1 の等価性テストの条件に含める。
- **タイムアウトの伝播**: 遅延列挙なので、`FindPrev` は break より後ろを照合しない(破滅的な区間に届かなければ成功する)・`Locate` は全件を列挙して例外を伝播する → 既存の `MatchPositionsTests.Timeout_while_building_falls_back_to_old_path` が押さえる(Task 1 で緑のままであることを確かめる)。
- **大量一致での確保量**: 20MB の CSV で `,` を F3 する形 → Task 1 の確保量テスト。
- **デバウンスの再始動**: 遅延より長く汲まなかった後の再予約が、即座に発火しないこと → Task 2。

---

### Task 1: `FindPrev` / `Locate` を `EnumerateMatches` で列挙する

**Files:**
- Modify: `src/kxEdit.Core/Search/TextSearcher.cs:103-143`(2 つの foreach と xmldoc)、`:145-157`(`CollectMatches` の xmldoc の「`Locate` / `FindPrev` が使う `Matches`」の記述)
- Test: `tests/kxEdit.Core.Tests/Search/MatchPositionsTests.cs`

**Interfaces:**
- Consumes: なし
- Produces: なし(公開シグネチャは不変)

- [ ] **Step 1: 等価性テストを書く(旧実装=`Matches` を正解にする)**

今の `Strategy_matches_old_implementation_for_random_texts` は `TextSearcher.FindPrev` / `Locate` を正解にしている。置き換えるとその正解も `EnumerateMatches` になるので、`Matches` を直接使う正解を別に置く。`MatchPositionsTests` の「戦略」の節(`CollectMatches_yields_same_sequence_as_Matches` の後ろ)に足す。`ReferenceRegex`・`RefLocate`・`RefFindPrev`・`RandomText` は同じクラスの既存の private ヘルパー。

```csharp
    /// <summary>
    /// 従来経路(<see cref="TextSearcher.FindPrev"/> / <see cref="TextSearcher.Locate"/>)が、
    /// <c>Matches</c> で列挙した旧実装と同じ答えを返す(フェーズ 4: 列挙を <c>EnumerateMatches</c> に
    /// 差し替えた)。正解は <c>Matches</c> の列に旧実装のループ(<see cref="RefFindPrev"/> /
    /// <see cref="RefLocate"/>)を当てたもの。startat より前のマッチを返す病的パターン
    /// (FindPrev の break 規則が効く)とゼロ幅パターンを含む。
    /// </summary>
    [Fact]
    public void Legacy_paths_match_Matches_reference()
    {
        SearchOptions[] conditions =
        [
            new("(?:b(?!a)+?)*", UseRegex: true),
            new("a*", UseRegex: true),
            new("b*", UseRegex: true),
            new(@"\b", UseRegex: true),
            new("(?=a)", UseRegex: true),
            new("a|ab", MatchCase: true, UseRegex: true),
            new("[ab]+?", UseRegex: true),
            new("$", UseRegex: true),
            new("(?m)^", UseRegex: true),
            new(@"\r?\n", UseRegex: true),
            new(".", UseRegex: true),
        ];
        string[] fixed_ = ["abbbb", "bab", "", "babbab", "BBaB", "aaa"];
        var rnd = new Random(20261002);
        foreach (var o in conditions)
        {
            var searcher = new TextSearcher(o);
            var texts = fixed_.Concat(Enumerable.Range(0, 80).Select(_ => RandomText(rnd)));
            foreach (var text in texts)
            {
                var ms = ReferenceRegex(o).Matches(text);
                int[] starts = ms.Select(m => m.Index).ToArray();
                int[] lengths = ms.Select(m => m.Length).ToArray();
                for (int before = -1; before <= text.Length + 2; before++)
                {
                    Assert.Equal(
                        RefFindPrev(starts, lengths, before),
                        searcher.FindPrev(text, before)
                    );
                }
                for (int st = -1; st <= text.Length + 1; st++)
                {
                    for (int l = 0; l <= 3; l++)
                    {
                        Assert.Equal(
                            RefLocate(starts, lengths, new(st, l)),
                            searcher.Locate(text, new(st, l))
                        );
                    }
                }
            }
        }
    }
```

注意: 変数名 `fixed_` は `fixed` が予約語のため。CSharpier・アナライザーが別名を求めたら `fixedTexts` にしてよい。

- [ ] **Step 2: 確保量テストを書く**

同じクラスの末尾に足す。暖機で、一致件数によらない確保(Regex の runner など)を先に済ませる。旧実装は一致ごとに `Match`(と内部の配列)を確保し、`MatchCollection` が保持するので、10 万件で MB 単位になる。

```csharp
    /// <summary>
    /// 従来経路が一致ごとに確保しない(フェーズ 4)。20MB の CSV で <c>,</c> を F3 すると表の上限を超え、
    /// 以後の F3 は毎回この経路を通る。<c>Matches</c> は一致ごとに <see cref="Match"/> を確保して保持する
    /// (10 万件で MB 単位)。上限は一致件数に比例しない定数にする。
    /// </summary>
    [Fact]
    public void Legacy_paths_do_not_allocate_per_match()
    {
        const int hits = 100_000;
        string text = string.Join(",", Enumerable.Repeat("a", hits + 1)); // "a,a,...,a" に "," が 10 万個
        var searcher = new TextSearcher(new SearchOptions(",", MatchCase: true));
        var last = new MatchSpan(text.Length - 2, 1);

        // 暖機: 一致件数によらない確保(runner など)を先に済ませる
        Assert.Equal((hits, hits), searcher.Locate(text, last));
        Assert.Equal(last, searcher.FindPrev(text, text.Length));

        long start = GC.GetAllocatedBytesForCurrentThread();
        var located = searcher.Locate(text, last);
        var prev = searcher.FindPrev(text, text.Length);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Equal((hits, hits), located);
        Assert.Equal(last, prev);
        Assert.True(allocated < 64 * 1024, $"確保量 {allocated} バイト(上限 64KiB)");
    }
```

- [ ] **Step 3: テストを走らせて、確保量テストだけが落ちることを確かめる**

Run: `dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~MatchPositionsTests"`
Expected: `Legacy_paths_match_Matches_reference` は PASS(今の実装は `Matches` なので正解と一致する)。`Legacy_paths_do_not_allocate_per_match` は FAIL(確保量が MB 単位)。FAIL のメッセージの確保量を控えておく(PR に載せる)。

- [ ] **Step 4: 列挙を差し替える**

`src/kxEdit.Core/Search/TextSearcher.cs` の `FindPrev`:

```csharp
    /// <summary>
    /// 開始位置（Index）が before より厳密に前にある最後のヒットを返す（折り返しなし）。
    /// 開始が before より前で終端が before を越える“またぎ”ヒットも返り得る。
    /// 列挙は <c>EnumerateMatches</c>(一致ごとに <see cref="Match"/> を確保しない。フェーズ 4)。
    /// <c>Matches</c> と同じ (Index, Length) の列を同じ順序で返し、遅延列挙なので break より後ろは照合しない。
    /// 等価性の網 = <c>MatchPositionsTests.Legacy_paths_match_Matches_reference</c>。
    /// 複雑な正規表現では RegexMatchTimeoutException が送出され得る（1秒）。
    /// </summary>
    public MatchSpan? FindPrev(string text, int before)
    {
        if (_regex is null)
            return null;
        MatchSpan? last = null;
        foreach (var m in _regex.EnumerateMatches(text))
        {
            if (m.Index >= before)
                break;
            last = new MatchSpan(m.Index, m.Length);
        }
        return last;
    }

    /// <summary>
    /// span を全ヒット中の何件目か（1始まり, total）。span がヒットでなければ null。
    /// 列挙は <c>EnumerateMatches</c>(<see cref="FindPrev"/> と同じ。フェーズ 4)。
    /// 複雑な正規表現では RegexMatchTimeoutException が送出され得る（1秒）。
    /// </summary>
    public (int Ordinal, int Total)? Locate(string text, MatchSpan span)
    {
        if (_regex is null)
            return null;
        int ordinal = 0,
            total = 0;
        bool found = false;
        foreach (var m in _regex.EnumerateMatches(text))
        {
            total++;
            if (m.Index == span.Start && m.Length == span.Length)
            {
                ordinal = total;
                found = true;
            }
        }
        return found ? (ordinal, total) : null;
    }
```

`CollectMatches` の xmldoc の次の 2 行を直す。

変更前:
```
    /// 上限 1,000,000 件では約 220MB のピークになる(実測)。<c>EnumerateMatches</c> は
    /// <see cref="Locate"/> / <see cref="FindPrev"/> が使う <c>Matches</c> と同じ (Index, Length) の列を
    /// 同じ順序で返す=同じ集合・同じ順序。この等価性の網は
```
変更後:
```
    /// 上限 1,000,000 件では約 220MB のピークになる(実測)。<c>EnumerateMatches</c> は
    /// <c>Matches</c> と同じ (Index, Length) の列を同じ順序で返す=同じ集合・同じ順序
    /// (<see cref="Locate"/> / <see cref="FindPrev"/> もフェーズ 4 で同じ列挙にした)。この等価性の網は
```

`MatchPositionsTests` のクラス xmldoc(9-10 行)の「正解は旧実装=`TextSearcher` の `Locate` / `FindPrev` / `Count`(全件列挙)」に、次の 1 文を足す: 「`TextSearcher` の `Locate` / `FindPrev` も `EnumerateMatches` で列挙するので、`Matches` を正解にした照合は `Legacy_paths_match_Matches_reference` が受け持つ。」

- [ ] **Step 5: テストを走らせる**

Run: `dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~Search"`
Expected: すべて PASS。特に `Legacy_paths_*` の 2 件、`Timeout_while_building_falls_back_to_old_path`、`RegexPerLineSearchStrategy` 系(`_inner.FindPrev` / `Locate` を行ごとに呼ぶ)。

もし確保量テストが PASS しない場合(暖機で済まない確保が残る場合)は、実測値と内訳を報告して止まる(上限を勝手に上げない)。

- [ ] **Step 6: 変異検証(スポット 2 個)**

変異ごとに: 書き換える → `dotnet build tests/kxEdit.Core.Tests` が**成功したこと**を確かめる → `dotnet test tests/kxEdit.Core.Tests --no-build --filter "FullyQualifiedName~MatchPositionsTests"` → `git checkout -- src/kxEdit.Core/Search/TextSearcher.cs` で戻す。

| # | 変異(`FindPrev`) | 期待 |
|---|---|---|
| M1 | `if (m.Index >= before)` → `if (m.Index > before)` | `Legacy_paths_match_Matches_reference` が FAIL(殺される) |
| M2 | `break;` → `continue;` | 同上。病的パターン(減少する列)で、break の後ろの `Index < before` の一致を拾う |

どちらかが生存した場合は、殺せる fixture(病的パターンの固定文字列など)を `fixed_` に足して再検証する。足しても殺せない場合は、等価な変異かどうかを判断して報告する。

最後に `git diff --stat` で、変異が残っていないこと(Step 4 の変更だけ)を確かめる。

- [ ] **Step 7: Commit**

```bash
git add src/kxEdit.Core/Search/TextSearcher.cs tests/kxEdit.Core.Tests/Search/MatchPositionsTests.cs
git commit -m "perf(search): 検索の旧経路で一致ごとに Match を確保しない

表の上限を超えたとき(またはタイムアウトしたとき)の従来経路 FindPrev / Locate を
Matches から EnumerateMatches に置き換えた。返す (Index, Length) の列と順序は同じ。

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: デバウンスが遅延を最後の予約から数え直すことのテスト(項目 28)

**Files:**
- Test: `tests/kxEdit.App.Tests/WinFormsDebounceSchedulerTests.cs`

**Interfaces:**
- Consumes: `WinFormsDebounceScheduler(int delayMs)`・`Schedule(Action)`、テスト内の `PumpUntil`、既存の `Sta.Run`
- Produces: なし

製品コードは変えない(`Schedule` の `_timer.Stop()` が既にこの挙動を実装している)。

- [ ] **Step 1: テストを書く**

`Cancel_PreventsRun` の後ろに足す。

```csharp
    [Fact]
    public void Schedule_Again_RestartsDelayFromLatestCall() =>
        Sta.Run(() =>
        {
            // A を予約し、汲まずに遅延より長く待つ(WM_TIMER は汲まない限り配送されない)。
            // そこで B を予約すると、遅延は B から数え直す=汲み始めてすぐには発火しない。
            // Schedule の Stop() が無いと、A の時点で満了したタイマーがそのまま発火する(約 0 ms)。
            // 片側の不等式だけを見る: 遅いマシンでは発火が遅れるだけなので偽の失敗になりにくい。
            const int delayMs = 200;
            using var s = new WinFormsDebounceScheduler(delayMs);
            var ran = new List<string>();
            s.Schedule(() => ran.Add("A"));
            Thread.Sleep(delayMs * 2);

            s.Schedule(() => ran.Add("B"));
            var sw = Stopwatch.StartNew();
            PumpUntil(() => ran.Count > 0, 5000);
            long elapsedMs = sw.ElapsedMilliseconds;

            Assert.Equal(new[] { "B" }, ran);
            Assert.True(
                elapsedMs >= delayMs / 2,
                $"再予約から {elapsedMs} ms で発火した(遅延 {delayMs} ms の半分未満)"
            );
        });
```

- [ ] **Step 2: テストを走らせる**

Run: `dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~WinFormsDebounceSchedulerTests"`
Expected: 4 件 PASS。

- [ ] **Step 3: 陰性対照**

`src/kxEdit.App/WinFormsDebounceScheduler.cs:28` の `_timer.Stop();` を `if (_pending is null) _timer.Stop();` に変える(行を消さずに条件で無効化する。A の予約後は `_pending` が非 null なので Stop されない)。`dotnet build tests/kxEdit.App.Tests` の**成功を確かめてから** `dotnet test tests/kxEdit.App.Tests --no-build --filter "FullyQualifiedName~Schedule_Again_RestartsDelayFromLatestCall"` を走らせる。

Expected: FAIL(発火までが遅延の半分未満)。メッセージの経過時間を控える(PR に載せる)。

`git checkout -- src/kxEdit.App/WinFormsDebounceScheduler.cs` で戻し、`git diff --stat` にテストファイルだけが残っていることを確かめる。

PASS してしまった場合(陰性対照で区別できない場合)は、経過時間を報告して止まる。

- [ ] **Step 4: Commit**

```bash
git add tests/kxEdit.App.Tests/WinFormsDebounceSchedulerTests.cs
git commit -m "test(app): デバウンスが遅延を最後の予約から数え直すことを確かめる

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: 最終レビュー・品質ゲート・実施記録

製品コードは 1 ファイル・数行の置き換えなので、CLAUDE.md §3 の簡略化の基準に沿って、最終レビューの 2 パスを別エージェント 1 回に統合する(別エージェントのレビューと品質ゲートは省略しない)。

- [ ] **Step 1: 最終レビュー**(別エージェント。コード品質+脆弱性。変異検証の結果のスポットチェックを含む)。指摘は fixup commit で反映する(CLAUDE.md §4)。
- [ ] **Step 2: 品質ゲート**: `pwsh tools/pre-merge-check.ps1` が EXIT 0。
- [ ] **Step 3: 実施記録**: 設計書 `docs/plans/2026-09-27-perf-followups-design.md` の §8 の末尾に「### 8.3 実施記録(2026-10-02・PR #<番号>)」を追記する。§7.5 と同じ構成(成果物 / 完了条件(テスト・陰性対照・変異検証・品質ゲート・レビュー)/ 本節からの精密化 / 申し送り)。確保量は Task 1 Step 3 の変更前の実測値と変更後の値を書く。commit は `docs(perf): フェーズ 4(検索の旧経路)の実施記録`。
- [ ] **Step 4: PR**: push して PR を作る(日本語。目的・挙動差なし・確保量の実測・変異検証の結果・レビュー経緯・L5 不要の理由)。
