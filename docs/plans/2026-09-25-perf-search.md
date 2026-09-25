# フェーズ 5: 検索(perf-search) 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development(または executing-plans)で、タスク単位に実装する。

**Goal:** 検索語の打鍵と F3 のたびに起きる全文化と全件列挙を減らし、全文化そのもののコストも下げる。

**設計書:** `docs/plans/2026-09-24-general-perf-improvements-design.md` §3・§10(以下「設計書」)
**調査記録:** `docs/plans/2026-09-24-general-perf-audit.md` P-5・P-13・P-14・M-5(以下「調査記録」)
**前フェーズの計画:** `docs/plans/2026-09-25-perf-append-grid.md`(実施記録は設計書 §9.5 に転記済み。本フェーズで転記するものはない)

**Architecture:**
- **P-13**: `TextSnapshot.GetText` を `string.Create` で 1 回のコピーにする。ピース全体は `Encoding.UTF8.GetChars` で宛先へ直接デコードし、端のピースは新しい `TextChunk.DecodeInto` で直接デコードする。書いた数が長さと一致しなければ例外を投げる。
- **P-5(a)**: 全文キャッシュを `MaterializedSearchStrategy` から切り出して public の `SnapshotTextCache` にする。`SearchController` が所有し、照合条件が変わって searcher を作り直しても同じキャッシュを渡す。
- **P-5(b)**: 検索語の打鍵(`TextChanged`)だけ、件数表示の更新を 200 ms の単発タイマーで間引く。タイマーは `IDebounceScheduler` で差し替えられるようにする。
- **P-14**: `MaterializedSearchStrategy` に、スナップショットごとの一致位置表(`MatchPositions`)を遅延構築する。構築するのは `Locate` と `FindPrev` だけで、この 2 つを二分探索で答える。`Count` は表が構築済みのときだけ表の件数を使う。

**Tech Stack:** C#(.NET 9)、xUnit、WinForms、kxEdit.Core.Bench、tools/perf-harness.ps1、PowerShell 7。

---

## 0. 前提と決定事項

### 0.1 計測の条件(設計書 §3.2・§5.5・§10.4)

Smoke `--perf` には検索のシナリオがないので、次の 2 本で測る(ユーザー判断 2026-09-25)。

- **Core.Bench `--search`**(新設・判定なし・Release・3 回): ja30k 相当(Smoke の ja10k と同じ行形式で 30,000 行・約 3MB)で、UI も GDI も通さずに次を測る。
  - B1: 全文化(`GetText(0, CharLength)`)。P-13 の対象。
  - B2: 検索語の打鍵 1 回(条件ごとに searcher を作り直して `Count`)。`SearchController.UpdateCount` の中身。
  - B3: F3 1 回(使い回した searcher で `FindNext` + `Locate`)。P-14 の対象。
  - B3': F3 の初回(searcher を作り直して `FindNext` + `Locate`)。P-14 で悪化しないことの確認。
  - B4: Shift+F3 1 回(`FindPrev` + `Locate`)。P-14 の対象。
  - Task 3 で B2s(全文キャッシュを共有した打鍵 1 回)を足す。P-5(a) の効果を B2 と分けて見るため。
- **perf-harness M-5**(publish・3 回): 体感値。間引き(P-5(b))の効果は Smoke でも Bench でも測れない(タイマーに回した処理を計上しない。設計書 §5.5)ので、ここで見る。**実行の直前にユーザーへ声をかける**(数分、キーボードとマウスを占有し、`%APPDATA%\kxEdit` を退避・復元する)。
- **NVDA は止めて測る**(ユーザー判断 2026-09-25。設計書 §5.5 の申し送り「フェーズ 5・6 は NVDA なしで変更前の値を採り直す」)。計測の直前に、ユーザーに NVDA の終了を依頼する。harness の CSV の `env` 行で NVDA なしを確かめる。変更後も同じ状態で測る。
- 画面のロック中・他のエージェントの重い処理と並走しているときは測らない。
- **注意**: M-5 は変更後、打鍵のたびの件数更新がなくなる(最後の打鍵から 200 ms 後に 1 回)。M-5 の周期(200 ms 間隔で打鍵)ではタイマーが満了しないことがある。値が下がるのは期待どおりで、「件数を数える費用が打鍵から外れた」ことを意味する。PR にはその旨を書く。

### 0.2 設計書からの精密化

- **P-13: `TextChunk.GetSubstring` を `DecodeInto` に置き換える**。設計書は「`GetSubstring` の端点について直接のテストを追加する」としている。`GetSubstring` の呼び出し元は `TextSnapshot.AppendRange` だけで、P-13 の後は使われなくなるので削除し、端点のテストは後継の `DecodeInto` に対して書く。
- **P-13: 実行時検査の例外型**は `InvalidOperationException`(前提=本文が妥当な UTF-8・ピース境界がコード点境界、が崩れた内部状態の異常)。宛先を超えて書こうとした場合は `Encoding.UTF8.GetChars` が `ArgumentException` を投げる。どちらも「黙って `\0` を含む文字列を返す」ことはない。
- **P-5(a): `SnapshotTextCache` の public な面は型と ctor だけ**。`TextOf` は internal(App からは所有して渡すだけで、中身を読まない)。
- **P-5(a): キャッシュの寿命**。`SearchController` は `_textCache` を遅延生成し、`DropSearcher` で searcher と一緒に捨てる。照合条件の変化(searcher の作り直し)では捨てない(これが P-5(a) の目的)。
- **P-5(b): 間引きの取り消し点**(設計書 §10.2(b) の精密化)
  - `UpdateCount` の先頭で必ず取り消す(即時の更新が保留中の更新を上書きする。チェックボックスの変化・Open・タブ切替がこれを通る)。
  - 検索の実行(`Find` / `ReplaceOne` / `ReplaceAll`)は、条件が空・文書なしの早期 return の**後**で取り消す。空条件の早期 return は発声もステータス更新もしないので、ここで取り消すと古い件数が残る。
  - ダイアログを閉じる(`Dismissed`)で取り消す。取り消さないと、満了時の `UpdateCount` が searcher と全文キャッシュを作り直し、ユーザーが検索を終えた後に文書を掴み直す。
  - タブ切替(`ActiveDocumentChanged`)で取り消す(表示中なら直後の `UpdateCount` で新しい文書を数える)。
  - **タブを閉じる(`DocumentClosed`)では取り消さない**(設計書からの逸脱)。取り消すと、表示中のダイアログの件数が最後の打鍵より前の値のまま残る。満了時の `UpdateCount` はアクティブ文書を数えるので、閉じた文書を掴み直すことはない。
- **P-5(b): コールバック**。`FindReplaceCallbacks` に `PatternChanged` を足す。ダイアログの検索語の `TextChanged` は `PatternChanged` を、チェックボックス 3 つは従来どおり `UpdateCount` を呼ぶ。
- **P-5(b): タイマーの所有**。本番の `WinFormsDebounceScheduler` は `IDisposable`(`System.Windows.Forms.Timer` を持つ)。`MainForm` が所有して `Dispose(bool)` で解放し、`SearchController` の ctor へ渡す。
- **P-14: 表の構築は `Regex.Matches` で列挙する**(`EnumerateMatches` を使わない)。旧実装の `Locate` / `FindPrev` と同じ列挙なので、集合と順序が構成上一致する。`EnumerateMatches` のほうが確保は少ないが、同値性の網を増やす必要が出るので採らない(YAGNI)。
- **P-14: 構築の失敗(上限超え・タイムアウト)もスナップショットごとに記憶する**。同じスナップショットでは再構築を試みず、従来の経路で答える。タイムアウトした最初の 1 回だけは、構築と従来経路の 2 回ぶん待つことになる(結果と例外は従来どおり)。
- **P-14: タイムアウトのテストのため、`TextSearcher` に internal ctor `(SearchOptions, TimeSpan matchTimeout)` を足す**。public ctor は 1 秒のまま。
- **P-14: 一致位置表は searcher ごと(照合条件ごと)に持つ**。全文キャッシュと違って共有しない(表は正規表現に依存する)。
- **P-14: 表が古いスナップショットを掴むことがある**。文書の編集後、次の `Locate` / `FindPrev` か `DropSearcher` までは、表が編集前のスナップショットを強参照する(全文キャッシュは新しいスナップショットへ移っていることがある)。寿命は従来の searcher の寿命の内側に収まるので受容する。

### 0.3 レビューとミューテーション検証

- **前倒しのコード品質レビュー**(設計書 §3.4): Task 3(`SnapshotTextCache`)と Task 4(`IDebounceScheduler`)。どちらも後続が依存する新しい seam。
- 前倒しの脆弱性レビュー: 該当なし(外部入力のパース・パス操作・プロセス起動・WebView・ネットワークに触れない。正規表現はユーザー入力だが、タイムアウトの扱いは従来どおりで、表の件数に上限を置く)。脆弱性の観点は最終レビューの脆弱性パスで見る(§Task 8)。
- **ミューテーション検証: 実施する(ユーザー承認 2026-09-25)**。設計書 §3.4 の「列挙の外だが、厳密な挙動の保証が要るもの」。対象は P-14 の二分探索の境界と表の採否判定に限る(Task 6)。P-13・P-5 には行わない。

### 0.4 L5

設計書 §10.4 のとおり**不要**。
- P-5 の件数表示は画面表示だけで、発声しない。
- P-14 の `Locate` の結果は「N 件中 M 件目」として発声されるが、発声する文字列が新旧で一致することを fake announcer のテスト(Task 5 の特性テスト)で確かめる。
- 件数表示の遅延は目視で確かめる(Task 9)。

### 0.5 意図的な挙動差

- 設計書 §3.5 のとおり: 検索語の打鍵では、件数表示が最後の打鍵から 200 ms 後に更新される。
- 本文・検索結果・発声の文字列は変わらない。

---

## Task 0: 本計画を commit する(docs のみ)

```powershell
git add docs/plans/2026-09-25-perf-search.md
git commit -m "docs(perf): フェーズ 5(検索)の実装計画"
```

---

## Task 1: Core.Bench `--search` を足して、変更前を測る(src は未変更)

**Files:**
- Modify: `tests/kxEdit.Core.Bench/Program.cs`(先頭のコメント・`using`・フラグ・`--largeline` ブロックの後に新ブロック)

**Step 1: 先頭のコメントにモードを足す**(`// 巨大 1 行調査 Task 2: --largeline 追加。…` の段落の後)

```csharp
// フェーズ 5(perf-search): --search 追加。3MB の日本語文書で全文化・検索語の打鍵・F3 を
//             UI 抜きで測る(判定なし・EXIT 0)。単独で早期 return する。
```

**Step 2: `using` とフラグ**

`using System.Globalization;` と `using kxEdit.Core.Search;` を足す(既存の `using` 群に、アルファベット順で)。

```csharp
bool searchMode = false;
```
を `bool largeLineMode = false;` の後に置き、引数ループに次を足す。

```csharp
    else if (args[i] == "--search")
    {
        searchMode = true;
    }
```

**Step 3: `--search` ブロックを書く**(`--largeline` ブロックの `return` の後、既定ベンチの前)

```csharp
// ---- 2026-09-25 フェーズ 5(perf-search): --search ----
// 設計書 2026-09-24-general-perf-improvements-design.md §10。Smoke --perf には検索のシナリオが
// ないので、SearchController が UI スレッドで行う処理の中身を、UI も GDI も通さずに測る。
// 文書は Editor.Smoke --perf の ja10k と同じ行形式で 30,000 行(調査記録 §9.5 の ja30k 相当・約 3MB)。
// 判定はしない(EXIT 0)。変更前後は同じマシン・同じ NVDA の状態で 3 回ずつ走らせて比べる。
if (searchMode)
{
    Console.WriteLine("--search: 検索の全文化・件数・F3 のベンチ(ja30k 相当・判定なし)");
    var searchDocSb = new StringBuilder();
    for (int i = 1; i <= 30_000; i++)
        searchDocSb
            .Append(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:D5}: 吾輩は猫である。名前はまだ無い。kxEdit の性能計測 sample 行です。",
                    i
                )
            )
            .Append("\r\n");
    string searchDoc = searchDocSb.ToString();
    var searchSnap = TextBuffer.FromString(searchDoc).Current;
    Console.WriteLine(
        $"文書: {searchSnap.CharLength:N0} 字・{Encoding.UTF8.GetByteCount(searchDoc):N0} バイト・ピース {searchSnap.PieceCount}"
    );
    const string term = "名前はまだ無い";

    // 1 回ごとの所要時間を測り、中央値・最小・最大を出す(ウォームアップ 5 回は捨てる)。
    static void Report(string label, int n, Action op)
    {
        for (int w = 0; w < 5; w++)
            op();
        var ms = new double[n];
        for (int k = 0; k < n; k++)
        {
            long t0 = Stopwatch.GetTimestamp();
            op();
            ms[k] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
        Array.Sort(ms);
        Console.WriteLine(
            $"{label}: 中央値 {ms[n / 2]:F3} ms(最小 {ms[0]:F3}・最大 {ms[^1]:F3}・n={n})"
        );
    }

    // B1 全文化(P-13 の対象)
    Report("B1 全文化", 50, () => _ = searchSnap.GetText(0, searchSnap.CharLength));

    // B2 検索語の打鍵 1 回: 条件が変わるので searcher を作り直して数える(UpdateCount の中身)。
    // 検索語は harness M-5 と同じく「名」「名前」…「名前はまだ無い」を順に回す。
    int b2 = 0;
    Report(
        "B2 打鍵 1 回(searcher ごとに全文化)",
        70,
        () =>
        {
            string p = term[..(b2++ % term.Length + 1)];
            _ = new SnapshotSearcher(new SearchOptions(p)).Count(searchSnap);
        }
    );

    // B3 F3 1 回: 使い回した searcher で FindNext + Locate(SearchController.Find の中身)。
    var f3 = new SnapshotSearcher(new SearchOptions(term));
    int from = 0;
    Report(
        "B3 F3 1 回(FindNext + Locate)",
        200,
        () =>
        {
            var hit = f3.FindNext(searchSnap, from) ?? f3.FindNext(searchSnap, 0)!.Value;
            _ = f3.Locate(searchSnap, hit);
            from = hit.End;
        }
    );

    // B3' F3 の初回: searcher を作り直して FindNext + Locate(P-14 で初回が悪化しないことの確認)。
    Report(
        "B3' F3 初回(searcher を作り直す)",
        30,
        () =>
        {
            var s = new SnapshotSearcher(new SearchOptions(term));
            var hit = s.FindNext(searchSnap, 0)!.Value;
            _ = s.Locate(searchSnap, hit);
        }
    );

    // B4 Shift+F3 1 回: FindPrev + Locate。
    int before = searchSnap.CharLength;
    Report(
        "B4 Shift+F3 1 回(FindPrev + Locate)",
        200,
        () =>
        {
            var hit =
                f3.FindPrev(searchSnap, before)
                ?? f3.FindPrev(searchSnap, searchSnap.CharLength)!.Value;
            _ = f3.Locate(searchSnap, hit);
            before = hit.Start;
        }
    );
    return 0;
}
```

**Step 4: ビルドと 1 回の試走**

```powershell
dotnet build tests/kxEdit.Core.Bench -c Release
dotnet run --project tests/kxEdit.Core.Bench -c Release -- --search
```
Expected: 0 warning。文書は約 1,590,000 字・約 3,000,000 バイト。B1〜B4 の行が出て EXIT 0。

**Step 5: Commit**

```powershell
git add tests/kxEdit.Core.Bench/Program.cs
git commit -m "test(bench): 検索の全文化・件数・F3 を測る Core.Bench --search を追加"
```

**Step 6: NVDA を止めてもらう**(ユーザーに依頼し、止まったことを確かめる)

```powershell
Get-Process nvda -ErrorAction SilentlyContinue | Select-Object Id, StartTime
```
Expected: 何も出ない。

**Step 7: Core.Bench `--search` を 3 回**

```powershell
$s = "<scratchpad>\perf"
New-Item -ItemType Directory -Force $s | Out-Null
foreach ($i in 1..3) {
  dotnet run --project tests/kxEdit.Core.Bench -c Release -- --search | Out-File -Encoding utf8 "$s\before-search-$i.txt"
}
```
各行の中央値の、3 回の最小・中央・最大を集計する。

**Step 8(実行の直前にユーザーへ声をかけてから): perf-harness の M-5 を 3 回**

```powershell
dotnet publish src/kxEdit.App -c Release -o "<scratchpad>\pub-before"
foreach ($i in 1..3) { pwsh -File tools/perf-harness.ps1 -PublishDir "<scratchpad>\pub-before" -Scenario M-5 }
```
CSV の `env` 行で NVDA なしと `status=completed` を確かめる。M-5 は empty / ja10k / ja30k の 3 行。

**Step 9:** 集計値・NVDA の状態・src のコミット(main と同一であること)を、本書末尾の実施記録に書いて commit する。

```powershell
git add docs/plans/2026-09-25-perf-search.md
git commit -m "docs(perf): フェーズ 5 の変更前の計測値を記録"
```

---

## Task 2: P-13 `TextSnapshot.GetText` のコピーを 1 回にする(挙動不変)

**Files:**
- Modify: `src/kxEdit.Core/Buffer/TextChunk.cs`(`GetSubstring` を `DecodeInto` に置き換える)
- Modify: `src/kxEdit.Core/Buffer/TextSnapshot.cs`(`GetText`・`AppendRange` → `WriteRange`)
- Test: `tests/kxEdit.Core.Tests/Buffer/TextChunkTests.cs`・`tests/kxEdit.Core.Tests/Buffer/TextSnapshotTests.cs`

**Step 1: 失敗するテストを書く — `TextChunkTests` の末尾**

```csharp
    [Fact]
    public void DecodeInto_matches_substring_for_every_window_including_surrogate_middles()
    {
        // 範囲の外側に別の文字を置き、ピースがチャンクの途中から始まる形にする(byteStart > 0)。
        // 格子を細かくして(gridBytes: 4)、CharToByte の格子点からの走査も通す。
        const string content = "aあ😀\r\n😀b😀";
        byte[] prefix = Encoding.UTF8.GetBytes("xyz");
        byte[] body = Encoding.UTF8.GetBytes(content);
        byte[] bytes = [.. prefix, .. body, .. Encoding.UTF8.GetBytes("Q")];
        var chunk = new TextChunk(bytes, gridBytes: 4);
        var dest = new char[content.Length + 4];
        for (int from = 0; from <= content.Length; from++)
        {
            for (int to = from; to <= content.Length; to++)
            {
                Array.Fill(dest, '#');
                int n = chunk.DecodeInto(prefix.Length, body.Length, from, to, dest);
                Assert.Equal(to - from, n);
                Assert.Equal(content.Substring(from, to - from), new string(dest, 0, n));
                Assert.Equal('#', dest[n]); // 数えた数より先に書いていない
            }
        }
    }
```
(`TextChunkTests` に `using System.Text;` がなければ足す。)

**Step 2: 失敗するテストを書く — `TextSnapshotTests` の `GetText_random_windows_…` の後**

```csharp
    [Fact]
    public void GetText_every_window_matches_substring_across_surrogate_pieces()
    {
        // ピース境界の両側にサロゲートペアを置き、全窓(始端・終端がペアの中間を含む)を総当たりする。
        // 端のピース(DecodeInto)と全体のピース(直接デコード)の両方を、窓の位置で切り替えて通す。
        const string doc = "😀a😀😀b\r\n😀";
        foreach (var snap in new[] { Snap(doc), Snap("😀a", "😀", "😀b\r", "\n😀") })
        {
            Assert.Equal(doc.Length, snap.CharLength);
            for (int a = 0; a <= doc.Length; a++)
                for (int b = a; b <= doc.Length; b++)
                    Assert.Equal(doc.Substring(a, b - a), snap.GetText(a, b - a));
        }
    }

    [Fact]
    public void GetText_throws_instead_of_returning_padding_when_utf8_invariant_is_broken()
    {
        // 前提(Utf8Sanitizer 済み)が崩れた内部状態: 0xFF は 4 バイト先頭として 2 単位に数えられるが、
        // デコードすると U+FFFD の 1 単位になる。旧実装は長さの違う文字列を返し、
        // 検査のない string.Create は末尾が '\0' の文字列を黙って返す。どちらも起こさず例外にする。
        byte[] bytes = [0xFF, (byte)'a'];
        var snap = new TextSnapshot(
            PieceTree.BuildBalanced([Piece.Of(new TextChunk(bytes), 0, bytes.Length)])
        );
        Assert.Equal(3, snap.CharLength); // 前提: 0xFF を 2 単位 + 'a' と数える
        Assert.Throws<InvalidOperationException>(() => snap.GetText(0, snap.CharLength));
    }
```
注意: 3 本目は、`CharLength` が 3 になること(`TextChunk.ScanForward` は継続バイト以外を 1、`>= 0xF0` をさらに 1 と数えるので、0xFF は 2 単位)と、デコードが U+FFFD と `a` の 2 文字になることが前提。テストの冒頭で `Assert.Equal(3, snap.CharLength);` を表明する。

**Step 3: テストが失敗することを確かめる**

```powershell
dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~TextChunkTests|FullyQualifiedName~TextSnapshotTests"
```
Expected: `DecodeInto` がないのでビルドエラー。`DecodeInto` のテストをいったんコメントアウトすると、`GetText_throws_…` は FAIL(例外が出ない)で、`GetText_every_window_…` は PASS(旧実装でも正しい=網として先に張る)。

**Step 4: `TextChunk.GetSubstring` を `DecodeInto` に置き換える**(`TextChunk.cs:223-240`)

```csharp
    /// <summary>
    /// 範囲内の文字区間 [charFrom, charTo) を <paramref name="dest"/> の先頭へ直接デコードし、書いた数を返す
    /// (中間の string を作らない。<see cref="TextSnapshot.GetText"/> の全文化を 1 コピーにするため・
    /// 2026-09-25 フェーズ 5 P-13。旧 <c>GetSubstring</c> の後継)。
    /// 両端はサロゲート中間でもよい: 始端が中間なら low サロゲートだけ、終端が中間なら high サロゲートだけを書く
    /// (旧 <c>GetSubstring</c> の「コード点境界へ広げて切り出し → Substring」と同じ結果)。
    /// </summary>
    /// <remarks>前提は <see cref="GetString"/> と同じ(範囲の両端がコード点境界で、不正な UTF-8 を含まない)。</remarks>
    public int DecodeInto(int byteStart, int byteLen, int charFrom, int charTo, Span<char> dest)
    {
        if (charFrom >= charTo)
            return 0;
        var s = _bytes.Span;
        Span<char> pair = stackalloc char[2];
        int written = 0;
        int bF = CharToByte(byteStart, byteLen, charFrom, out int cF); // 中間なら低い方へ
        if (cF < charFrom)
        { // 始端が中間: そのコード点(必ず 4 バイト = 2 単位)の low サロゲートだけ
            Encoding.UTF8.GetChars(s.Slice(bF, 4), pair);
            dest[written++] = pair[1];
            bF += 4;
        }
        int bT = CharToByte(byteStart, byteLen, charTo, out int cT);
        written += Encoding.UTF8.GetChars(s.Slice(bF, bT - bF), dest[written..]);
        if (cT < charTo)
        { // 終端が中間: そのコード点の high サロゲートだけ
            Encoding.UTF8.GetChars(s.Slice(bT, 4), pair);
            dest[written++] = pair[0];
        }
        return written;
    }
```
始端と終端が同じコード点の中間になることはない(`charTo > charFrom` なので、始端が中間なら `charTo` はそのコード点の終わり以降)。始端が中間で `charTo = charFrom + 1` のとき、`bT` は `+= 4` 後の `bF` に一致し、中央のデコードは 0 文字になる。

**Step 5: `TextSnapshot.GetText` を `string.Create` にする**(`TextSnapshot.cs:38-54` と `:259-282`)

`GetText` の `StringBuilder` の 3 行を次に置き換える。

```csharp
        // 1 コピー(2026-09-25 フェーズ 5 P-13): 旧実装はピースごとの string → StringBuilder → ToString の
        // 3 コピーだった。書いた数が length と一致しなければ例外にする。Debug.Assert では Release で
        // 検査が消え、前提(不正な UTF-8 がない・ピース境界がコード点境界)が崩れたときに
        // 末尾が '\0' の文字列を黙って返してしまう(旧実装は長さが違うだけだった)。
        return string.Create(
            length,
            (Root: _root, Start: start),
            static (dest, st) =>
            {
                int written = WriteRange(st.Root, st.Start, st.Start + dest.Length, dest);
                if (written != dest.Length)
                    throw new InvalidOperationException(
                        $"GetText のデコード結果が {written} 文字(要求は {dest.Length} 文字)。"
                            + "本文が不正な UTF-8 を含むか、ピース境界がコード点の途中にある。"
                    );
            }
        );
```

`AppendRange` を次の `WriteRange` に置き換える。

```csharp
    /// <summary>
    /// ノードの文字区間 [from, to) を dest の先頭から書き、書いた数を返す。
    /// ピース全域は宛先へ直接デコード、端は <see cref="TextChunk.DecodeInto"/>。
    /// </summary>
    private static int WriteRange(PieceTree.Node? t, int from, int to, Span<char> dest)
    {
        if (t is null || from >= to)
            return 0;
        int written = 0;
        int leftChars = PieceTree.SumOf(t.Left).CharLen;
        if (from < leftChars)
            written += WriteRange(t.Left, from, Math.Min(to, leftChars), dest);
        int ps = leftChars,
            pe = leftChars + t.Piece.CharLen;
        if (to > ps && from < pe && t.Piece.CharLen > 0)
        {
            int f = Math.Max(from, ps) - ps,
                e = Math.Min(to, pe) - ps;
            var p = t.Piece;
            written +=
                f == 0 && e == p.CharLen
                    ? Encoding.UTF8.GetChars(
                        p.Chunk.Span.Slice(p.ByteStart, p.ByteLen),
                        dest[written..]
                    )
                    : p.Chunk.DecodeInto(p.ByteStart, p.ByteLen, f, e, dest[written..]);
        }
        if (to > pe)
            written += WriteRange(t.Right, Math.Max(from, pe) - pe, to - pe, dest[written..]);
        return written;
    }
```
`using System.Text;` は `Encoding` のために残る。`StringBuilder` を他で使っていなければ警告は出ない(`using` は名前空間単位)。

**Step 6: テストを通す**

```powershell
dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~TextChunkTests|FullyQualifiedName~TextSnapshotTests|FullyQualifiedName~FuzzTests|FullyQualifiedName~SnapshotIoTests|FullyQualifiedName~TextSnapshotGetCharEquivalenceTests"
dotnet test tests/kxEdit.Core.Tests
```
Expected: すべて PASS(設計書 §10.1 が挙げる `GetText_random_windows_…`・`FuzzTests.DeepVerify`・`SnapshotIoTests` を含む)。

**Step 7: Commit**

```powershell
git add src/kxEdit.Core/Buffer/TextChunk.cs src/kxEdit.Core/Buffer/TextSnapshot.cs tests/kxEdit.Core.Tests/Buffer/TextChunkTests.cs tests/kxEdit.Core.Tests/Buffer/TextSnapshotTests.cs
git commit -m "perf(core): TextSnapshot.GetText のコピーを 1 回にする(P-13)"
```

---

## Task 3: P-5(a) 全文キャッシュを `SnapshotTextCache` に切り出して共有する(挙動不変)

**前倒しのコード品質レビューの対象**(新しい public の seam)。

**Files:**
- Create: `src/kxEdit.Core/Search/SnapshotTextCache.cs`
- Modify: `src/kxEdit.Core/Search/MaterializedSearchStrategy.cs`・`src/kxEdit.Core/Search/SnapshotSearcher.cs`・`src/kxEdit.App/SearchController.cs`・`tests/kxEdit.Core.Bench/Program.cs`
- Test: `tests/kxEdit.Core.Tests/Search/MaterializedSearchStrategyTests.cs`・`tests/kxEdit.App.Tests/SearchControllerTests.cs`

**Step 1: 失敗するテストを書く — Core(`MaterializedSearchStrategyTests` の末尾)**

```csharp
    [Fact]
    public void Shared_cache_materializes_once_across_strategies()
    {
        // P-5(a): 照合条件が変わって戦略(searcher)を作り直しても、同じキャッシュを渡せば
        // 同じスナップショットの全文化は 1 回で済む。
        var snap = TextBuffer.FromString("ab abc").Current;
        var cache = new SnapshotTextCache();
        var first = new MaterializedSearchStrategy(
            new TextSearcher(new SearchOptions("ab", MatchCase: true)),
            cache
        );
        var second = new MaterializedSearchStrategy(
            new TextSearcher(new SearchOptions("abc", MatchCase: true)),
            cache
        );

        Assert.Equal(2, first.Count(snap));
        Assert.Equal(1, second.Count(snap));
        Assert.Equal(1, cache.MaterializeCountForTest);
    }

    [Fact]
    public void Shared_cache_rematerializes_after_edit()
    {
        var buffer = TextBuffer.FromString("ab");
        var cache = new SnapshotTextCache();
        var s = new MaterializedSearchStrategy(
            new TextSearcher(new SearchOptions("ab", MatchCase: true)),
            cache
        );
        Assert.Equal(1, s.Count(buffer.Current));

        buffer.Insert(2, " ab");
        var other = new MaterializedSearchStrategy(
            new TextSearcher(new SearchOptions("b", MatchCase: true)),
            cache
        );

        Assert.Equal(2, other.Count(buffer.Current)); // 古い本文を返さない
        Assert.Equal(2, cache.MaterializeCountForTest);
    }
```

`SnapshotSearcher` 経由の網(`tests/kxEdit.Core.Tests/Search/SnapshotSearcherTests.cs` の末尾):

```csharp
    [Fact]
    public void SnapshotSearcher_with_shared_cache_materializes_once()
    {
        var snap = TextBuffer.FromString("ab abc").Current;
        var cache = new SnapshotTextCache();
        Assert.Equal(2, new SnapshotSearcher(new SearchOptions("ab"), cache).Count(snap));
        Assert.Equal(1, new SnapshotSearcher(new SearchOptions("abc"), cache).Count(snap));
        Assert.Equal(1, cache.MaterializeCountForTest);
    }
```

**Step 2: 失敗するテストを書く — App(`SearchControllerTests` の「searcher の保持と破棄」節の末尾)**

```csharp
    [Fact]
    public void TextCache_IsShared_WhenMatchConditionChanges() =>
        Sta.Run(() =>
        {
            // P-5(a): 検索語の打鍵で searcher は作り直すが、全文キャッシュは使い回す。
            using var host = new Host();
            host.NewDoc("abc abd");
            host.View.Pattern = "ab";
            host.Search.OpenFind();
            var searcher = host.Search.SearcherForTest;
            var cache = host.Search.TextCacheForTest;
            Assert.NotNull(cache);

            host.View.Pattern = "abc";
            host.Search.UpdateCount();

            Assert.NotSame(searcher, host.Search.SearcherForTest);
            Assert.Same(cache, host.Search.TextCacheForTest);
            Assert.Equal(1, cache!.MaterializeCountForTest);
            Assert.Equal("1 件", host.View.Status);
        });
```

既存の破棄トリガのテストに、全文キャッシュの 1 行ずつを足す(searcher と同じ寿命であることを固定する)。

| テスト | 足す assert |
|---|---|
| `Searcher_IsDropped_WhenPatternBecomesEmpty` | `Assert.Null(host.Search.TextCacheForTest);` |
| `Searcher_IsDropped_OnActiveDocumentChanged` | 最初に `var firstCache = host.Search.TextCacheForTest;`、最後に `Assert.NotSame(firstCache, host.Search.TextCacheForTest);` |
| `Searcher_IsDropped_OnDocumentClosed` | `Assert.Null(host.Search.TextCacheForTest);` |
| `Searcher_IsDropped_OnDismissed_AndRebuiltOnNextSearch` | 1 回目の `RaiseDismissed` の後に `Assert.Null(host.Search.TextCacheForTest);` |
| `Searcher_IsDropped_WhenViewIsRecreated` | 最初に `var firstCache = …`、最後に `Assert.NotSame(firstCache, host.Search.TextCacheForTest);` |
| `Searcher_SurvivesG2Hide_AcrossRepeatedFindNext` | 最初に `var cache = …`、最後に `Assert.Same(cache, host.Search.TextCacheForTest);` |

**Step 3: 失敗を確かめる**

```powershell
dotnet build tests/kxEdit.Core.Tests; dotnet build tests/kxEdit.App.Tests
```
Expected: `SnapshotTextCache`・`TextCacheForTest` がないのでビルドエラー。

**Step 4: `SnapshotTextCache` を作る**(`src/kxEdit.Core/Search/SnapshotTextCache.cs`)

```csharp
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Search;

/// <summary>
/// スナップショット 1 本ぶんの全文 string のキャッシュ(1 枠)。
/// 照合条件が変わって <see cref="SnapshotSearcher"/> を作り直しても、同じ文書なら全文化をやり直さないために、
/// searcher の外(<c>SearchController</c>)が所有して注入する(2026-09-25 フェーズ 5 P-5(a))。
/// </summary>
/// <remarks>
/// <para>
/// <b>参照同一性で判定する</b>。<see cref="TextSnapshot"/> は不変(構築時のルート参照を包むだけ)で、
/// <see cref="TextBuffer.Current"/> は編集・Undo・Redo のときだけ差し替わるフィールド返しなので、
/// 参照同一性が「文書が変わっていない」の正当な signal になる。
/// 参照同一性を同種の signal に使う idiom は <see cref="TextBuffer.Modified"/> が既に採用している
/// (あちらが比べるのはスナップショットではなくピース木のルート参照)。
/// </para>
/// <para>
/// 誤りは<b>安全な側にしか倒れない</b>: 内容が同じでもインスタンスが別なら
/// (Undo で同じルートへ戻った直後など)材質化をやり直すだけで、古い本文を返すことはない。
/// 逆向き=「同じインスタンスなのに内容が違う」は <see cref="TextSnapshot"/> が不変である限り起こらない。
/// 保持するのは常に最大 1 本で、スナップショットが変われば古い文字列は参照が切れる。
/// </para>
/// <para>
/// <b>public なのは型と ctor だけ</b>。kxEdit.App が所有して渡すためで(Core の InternalsVisibleTo に
/// kxEdit.App は含まれない)、中身を読む <see cref="TextOf"/> は internal。
/// </para>
/// <para>
/// <b>スレッドセーフではない</b>。所有者と、これを注入したすべての searcher は同じスレッドから使うこと。
/// <b>寿命は所有者の責任</b>: 最後に材質化した <see cref="TextSnapshot"/> と全文を強参照で持つ
/// (= 背後のピース木・バイト配列ごとピン留めする)。文書の切替・クローズ・検索の終了で参照を捨てること
/// (<c>SearchController.DropSearcher</c> がその実装)。
/// </para>
/// </remarks>
public sealed class SnapshotTextCache
{
    private TextSnapshot? _snapshot;
    private string _text = string.Empty;

    /// <summary>
    /// テスト観測用: 実際に材質化した回数。キャッシュが効いていることを assert 化する seam。
    /// <b>消さないこと</b>: <c>Cache_holds_at_most_one_snapshot</c> が「保持は最大 1 本」を
    /// 検証する唯一の手段であり、結果値からは辞書実装(多スロット)と区別できない。
    /// </summary>
    internal int MaterializeCountForTest { get; private set; }

    /// <summary>snap の全文。直前と同じスナップショットなら前回の結果を返す。</summary>
    internal string TextOf(TextSnapshot snap)
    {
        if (ReferenceEquals(_snapshot, snap))
            return _text;
        // 代入順は text が先・snapshot が後(入れ替えないこと)。逆順だと GetText が
        // 例外を投げたときに _snapshot だけ新しくなり、次回の参照同一性ヒットで
        // 古い本文を新しいスナップショットのものとして返す stale の窓が開く。
        _text = snap.GetText(0, snap.CharLength);
        _snapshot = snap;
        MaterializeCountForTest++;
        return _text;
    }
}
```

**Step 5: `MaterializedSearchStrategy` をキャッシュに委ねる**

- クラス remarks の最初の 2 段落(材質化した文字列はスナップショット単位で保持…/誤りは安全な側にしか倒れない…)を、「材質化した文字列は注入された <see cref="SnapshotTextCache"/> が保持する(判定と安全性の議論はそちらの remarks)。同じキャッシュを複数の戦略(照合条件)で共有してよい」の 1 段落に置き換える。
- フィールドと ctor・`TextOf`・`MaterializeCountForTest` を次に置き換える。

```csharp
    private readonly TextSearcher _inner;
    private readonly SnapshotTextCache _texts;

    /// <summary>
    /// テスト観測用: 注入されたキャッシュの材質化回数(キャッシュを共有していれば、共有先の分も数える)。
    /// 既存のテストはこの戦略だけがキャッシュを使う形で観測している。
    /// </summary>
    internal int MaterializeCountForTest => _texts.MaterializeCountForTest;

    /// <summary>専用のキャッシュで構築する(テスト用)。</summary>
    internal MaterializedSearchStrategy(TextSearcher inner)
        : this(inner, new SnapshotTextCache()) { }

    internal MaterializedSearchStrategy(TextSearcher inner, SnapshotTextCache texts)
    {
        _inner = inner;
        _texts = texts;
    }

    /// <summary>snap の全文(キャッシュ経由)。</summary>
    private string TextOf(TextSnapshot snap) => _texts.TextOf(snap);
```

**Step 6: `SnapshotSearcher` の ctor にキャッシュを通す**

```csharp
    /// <summary>照合条件から SnapshotSearcher を構築する(専用の全文キャッシュを持つ)。IsValid/Error は内側 <see cref="TextSearcher"/> と同一。</summary>
    public SnapshotSearcher(SearchOptions options)
        : this(options, new SnapshotTextCache(), DefaultThresholdChars, DefaultWindowSize) { }

    /// <summary>
    /// 照合条件と、共有する全文キャッシュから構築する。照合条件が変わって searcher を作り直しても、
    /// 同じ文書なら全文化をやり直さない(2026-09-25 フェーズ 5 P-5(a))。
    /// キャッシュを共有する searcher 同士は同じスレッドから使うこと。
    /// </summary>
    public SnapshotSearcher(SearchOptions options, SnapshotTextCache textCache)
        : this(options, textCache, DefaultThresholdChars, DefaultWindowSize) { }

    /// <summary>
    /// 閾値・窓サイズを指定して SnapshotSearcher を構築する(テスト注入用)。
    /// 本番コードは既定コンストラクタを使う。閾値・窓サイズは正数でなければならない。
    /// </summary>
    public SnapshotSearcher(SearchOptions options, int thresholdChars, int windowSize)
        : this(options, new SnapshotTextCache(), thresholdChars, windowSize) { }

    private SnapshotSearcher(
        SearchOptions options,
        SnapshotTextCache textCache,
        int thresholdChars,
        int windowSize
    )
    {
        ArgumentNullException.ThrowIfNull(textCache);
        ArgumentOutOfRangeException.ThrowIfNegative(thresholdChars);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        // (以下は従来の本体。材質化戦略だけ textCache を渡す)
        ...
        _materialized = new MaterializedSearchStrategy(_inner, textCache);
        ...
    }
```
クラス doc の「スレッドセーフではない」段落に、「全文キャッシュを注入した場合は、それを共有するすべての searcher と所有者が同じスレッドから使うこと」を 1 文足す。「長寿命に保持するなら参照の寿命は呼び出し側の責任」の段落は、「材質化戦略のキャッシュ」を「全文キャッシュ(注入した場合は所有者が持つ)」に直す。

**Step 7: `SearchController` がキャッシュを所有する**

- `_searcher` の上のコメント(49-54 行)を次に直す。

```csharp
    // 照合条件が変わるまで searcher を使い回す。作り直すと内部の Regex が再コンパイルされる
    // (インスタンス生成の Regex は .NET の静的キャッシュに乗らない)。
    // 全文キャッシュ(_textCache)は searcher の外に持ち、条件が変わって作り直しても渡し直す
    // (打鍵ごとの UpdateCount で全文化をやり直さない。2026-09-25 フェーズ 5 P-5(a))。
    // 保持する側の責任: キャッシュは TextSnapshot → ピース木 → バイト配列を強参照するため、
    // 破棄トリガ(文書切替・文書クローズ・ユーザーの検索終了・検索語が空)を漏らすと
    // 閉じたタブの文書がまるごと生き残る。DropSearcher を呼ぶ経路を減らさないこと。
    private SearchOptions? _searcherOptions;
    private SnapshotSearcher? _searcher;
    private SnapshotTextCache? _textCache;
```
- `ResolveSearcher` の作り直し:

```csharp
        if (_searcher is null || _searcherOptions != opts)
        {
            _textCache ??= new SnapshotTextCache();
            _searcher = new SnapshotSearcher(opts, _textCache);
            _searcherOptions = opts;
        }
```
- `DropSearcher` に `_textCache = null;` を足し、summary を「保持中の searcher と全文キャッシュを捨てる」に直す。
- `SearcherForTest` の後に:

```csharp
    /// <summary>テスト観測用: 現在保持中の全文キャッシュ(未解決なら null)。
    /// 照合条件の変化で使い回されること・破棄トリガで捨てられることを、参照同一性で固定する。</summary>
    internal SnapshotTextCache? TextCacheForTest => _textCache;
```

**Step 8: Core.Bench に B2s を足す**(`B2` の直後)

```csharp
    // B2s 検索語の打鍵 1 回(全文キャッシュを共有): SearchController と同じ形(P-5(a) 以後)。
    // B2 との差が P-5(a) の効果。
    var sharedTexts = new SnapshotTextCache();
    int b2s = 0;
    Report(
        "B2s 打鍵 1 回(全文キャッシュを共有)",
        70,
        () =>
        {
            string p = term[..(b2s++ % term.Length + 1)];
            _ = new SnapshotSearcher(new SearchOptions(p), sharedTexts).Count(searchSnap);
        }
    );
```

**Step 9: テストを通す**

```powershell
dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~Search"
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~SearchController"
dotnet build tests/kxEdit.Core.Bench -c Release
```
Expected: すべて PASS・0 warning。既存の `MaterializedSearchStrategyTests` の 6 件は変更なしで PASS(1 引数 ctor が専用キャッシュを作るため)。

**Step 10: Commit**

```powershell
git add src/kxEdit.Core/Search/SnapshotTextCache.cs src/kxEdit.Core/Search/MaterializedSearchStrategy.cs src/kxEdit.Core/Search/SnapshotSearcher.cs src/kxEdit.App/SearchController.cs tests/kxEdit.Core.Tests/Search tests/kxEdit.App.Tests/SearchControllerTests.cs tests/kxEdit.Core.Bench/Program.cs
git commit -m "perf(search): 全文キャッシュを SnapshotTextCache に切り出して条件変更をまたいで共有する(P-5(a))"
```

**Step 11: 前倒しのコード品質レビュー**(別エージェント)。観点: public の面の最小性、所有と寿命の責任の書き分け、既存の破棄トリガとの一致、`MaterializeCountForTest` の意味の変化(共有時)。指摘は fixup commit で反映し、実施記録に 3 択で書く。

---

## Task 4: P-5(b) 検索語の打鍵で件数表示を間引く(意図的な挙動変更)

**前倒しのコード品質レビューの対象**(差し替え可能な間引きスケジューラ)。

**Files:**
- Create: `src/kxEdit.App/Abstractions/IDebounceScheduler.cs`・`src/kxEdit.App/WinFormsDebounceScheduler.cs`・`tests/kxEdit.App.Tests/Fakes/ManualDebounceScheduler.cs`・`tests/kxEdit.App.Tests/WinFormsDebounceSchedulerTests.cs`
- Modify: `src/kxEdit.App/Abstractions/IFindReplaceView.cs`(`FindReplaceCallbacks`)・`src/kxEdit.App/FindReplaceDialog.cs`・`src/kxEdit.App/SearchController.cs`・`src/kxEdit.App/MainForm.cs`
- Test: `tests/kxEdit.App.Tests/SearchControllerTests.cs`・`tests/kxEdit.App.Tests/FindReplaceDialogTests.cs`

**Step 1: 抽象と偽物を書く**

`src/kxEdit.App/Abstractions/IDebounceScheduler.cs`:

```csharp
namespace kxEdit.App;

/// <summary>
/// 単発の遅延実行(入力の間引き)。<see cref="Schedule"/> は保留中の実行を取り消してから予約し直す
/// =最後の予約から所定の遅延の後に 1 回だけ実行する。
/// 検索語の打鍵で件数表示の更新を間引くために使う(2026-09-25 フェーズ 5 P-5(b))。
/// テストでは手動で発火させる実装に差し替える。
/// UI スレッドから使い、action も UI スレッドで実行されること。
/// </summary>
public interface IDebounceScheduler
{
    /// <summary>保留中の実行を取り消し、<paramref name="action"/> を所定の遅延の後に 1 回実行するよう予約する。</summary>
    void Schedule(Action action);

    /// <summary>保留中の実行を取り消す。無ければ何もしない(冪等)。</summary>
    void Cancel();
}
```

`tests/kxEdit.App.Tests/Fakes/ManualDebounceScheduler.cs`:

```csharp
namespace kxEdit.App.Tests.Fakes;

/// <summary><see cref="IDebounceScheduler"/> のテスト用フェイク。時間は進めず、<see cref="Fire"/> で満了させる。</summary>
public sealed class ManualDebounceScheduler : IDebounceScheduler
{
    private Action? _pending;

    public int ScheduleCount { get; private set; }

    public bool IsPending => _pending is not null;

    public void Schedule(Action action)
    {
        ScheduleCount++;
        _pending = action;
    }

    public void Cancel() => _pending = null;

    /// <summary>保留中の実行を満了させる(無ければ例外=テストの前提違い)。</summary>
    public void Fire()
    {
        var action = _pending ?? throw new InvalidOperationException("保留中の実行がありません");
        _pending = null;
        action();
    }
}
```

**Step 2: 失敗するテストを書く — `SearchControllerTests`**

`Host` に `public ManualDebounceScheduler CountDebounce { get; } = new();` を足し、ctor 呼び出しを `new SearchController(docs, form, Announcer, cb => { … }, CountDebounce)` にする。新しい節を足す。

```csharp
    // ===== P-5(b)(2026-09-25): 検索語の打鍵で件数表示を間引く =====
    // 打鍵(PatternChanged)は予約だけ・満了で UpdateCount。即時の更新と検索の実行は保留中の更新を取り消す。

    [Fact]
    public void PatternChanged_DefersCount_UntilDebounceFires() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc abc abc");
            host.Search.OpenFind(); // 空の検索語=ステータスはクリア
            int statusCalls = host.View.StatusLog.Count;

            host.View.Pattern = "abc";
            host.Callbacks!.PatternChanged();

            Assert.Equal(statusCalls, host.View.StatusLog.Count); // 打鍵の時点では数えない
            Assert.True(host.CountDebounce.IsPending);

            host.CountDebounce.Fire();

            Assert.Equal("3 件", host.View.Status);
            Assert.Empty(host.Announcer.Said); // 件数は発声しない(従来どおり)
        });

    [Fact]
    public void PatternChanged_Repeated_CountsOnceWithLatestPattern() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("ab abc abc");
            host.Search.OpenFind();
            int statusCalls = host.View.StatusLog.Count;

            host.View.Pattern = "ab";
            host.Callbacks!.PatternChanged();
            host.View.Pattern = "abc";
            host.Callbacks!.PatternChanged();
            host.CountDebounce.Fire();

            Assert.Equal(statusCalls + 1, host.View.StatusLog.Count); // 1 回だけ数える
            Assert.Equal("2 件", host.View.Status); // 最後の検索語で数える
        });

    [Fact]
    public void UpdateCount_CancelsPendingDebounce() =>
        Sta.Run(() =>
        {
            // チェックボックスの変化は即時(UpdateCount)。保留中の打鍵の更新は取り消す
            // (即時の更新が現在の条件で数えている=満了しても同じ結果を上書きするだけ)。
            using var host = new Host();
            host.NewDoc("ABC abc");
            host.Search.OpenFind();
            host.View.Pattern = "abc";
            host.Callbacks!.PatternChanged();

            host.View.MatchCase = true;
            host.Callbacks!.UpdateCount();

            Assert.Equal("1 件", host.View.Status);
            Assert.False(host.CountDebounce.IsPending);
        });

    [Theory]
    [InlineData("FindNext")]
    [InlineData("FindPrev")]
    [InlineData("ReplaceOne")]
    [InlineData("ReplaceAll")]
    public void SearchExecution_CancelsPendingDebounce(string op) =>
        Sta.Run(() =>
        {
            // 取り消さないと、満了した UpdateCount が「N 件中 M 件目」「N 件置換しました」の
            // ステータスを「N 件」で上書きする。
            using var host = new Host();
            var doc = host.NewDoc("abc abc");
            host.Search.OpenReplace();
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            doc.Editor.SelectCharRange(doc.Editor.Text.Length, 0); // FindPrev が末尾から探せるように
            host.Callbacks!.PatternChanged();

            switch (op)
            {
                case "FindNext":
                    host.Search.FindNext();
                    break;
                case "FindPrev":
                    host.Search.FindPrev();
                    break;
                case "ReplaceOne":
                    host.Search.ReplaceOne();
                    break;
                default:
                    host.Search.ReplaceAll();
                    break;
            }

            Assert.False(host.CountDebounce.IsPending);
        });

    [Fact]
    public void FindNext_WithEmptyPattern_KeepsPendingDebounce() =>
        Sta.Run(() =>
        {
            // 空の検索語の Find は発声もステータス更新もせずに戻る。ここで取り消すと、
            // 検索語を消した打鍵の更新(ステータスのクリア)が失われ、古い件数が残る。
            using var host = new Host();
            host.NewDoc("abc");
            host.View.Pattern = "abc";
            host.Search.OpenFind();
            Assert.Equal("1 件", host.View.Status);

            host.View.Pattern = "";
            host.Callbacks!.PatternChanged();
            Assert.False(host.Search.FindNext());
            Assert.True(host.CountDebounce.IsPending);

            host.CountDebounce.Fire();
            Assert.Equal("", host.View.Status);
        });

    [Fact]
    public void Dismissed_CancelsPendingDebounce_AndDoesNotResurrectSearcher() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc");
            host.Search.OpenFind();
            host.View.Pattern = "abc";
            host.Callbacks!.PatternChanged();

            host.View.RaiseDismissed();

            Assert.False(host.CountDebounce.IsPending);
            Assert.Null(host.Search.SearcherForTest); // 満了で searcher とキャッシュを作り直さない
            Assert.Null(host.Search.TextCacheForTest);
        });

    [Fact]
    public void ActiveDocumentChanged_CancelsPendingDebounce() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc");
            host.Search.OpenFind();
            host.View.Pattern = "abc";
            host.Callbacks!.PatternChanged();

            _ = host.NewDoc("abc abc"); // 表示中なので切替の直後に新しい文書で数える

            Assert.False(host.CountDebounce.IsPending);
            Assert.Equal("2 件", host.View.Status);
        });

    [Fact]
    public void DocumentClosed_KeepsPendingDebounce() =>
        Sta.Run(() =>
        {
            // 設計書 §10.2(b) からの逸脱(計画 §0.2): タブを閉じても取り消さない。
            // 取り消すと、最後の打鍵の件数が表示されないまま古い件数が残る。
            using var host = new Host();
            var doc1 = host.NewDoc("abc");
            _ = host.NewDoc("abc abc"); // アクティブ
            host.Search.OpenFind();
            host.View.Pattern = "abc";
            host.Callbacks!.PatternChanged();

            Assert.True(host.Docs.TryClose(doc1, _ => true)); // 非アクティブタブを閉じる

            Assert.True(host.CountDebounce.IsPending);
            host.CountDebounce.Fire();
            Assert.Equal("2 件", host.View.Status); // アクティブ文書を数える
        });
```

`Callbacks_AreWiredToMatchingControllerMethods` の「Action 3 本の判別 1」の前に足す:

```csharp
            // PatternChanged は予約だけ(発声も本文変更もステータス更新もしない)
            int statusBefore = host.View.StatusLog.Count;
            cb.PatternChanged();
            Assert.True(host.CountDebounce.IsPending);
            Assert.Equal(statusBefore, host.View.StatusLog.Count);
            host.CountDebounce.Cancel(); // 以降の判別へ持ち越さない
```

**Step 3: 失敗するテストを書く — `FindReplaceDialogTests`**

`HitCallbacks()` に `PatternChanged: () => { },` を足す(`UpdateCount` の前)。新しいテストを足す。

```csharp
    [Fact]
    public void PatternTextChanged_RaisesPatternChanged_AndCheckboxesRaiseUpdateCount() =>
        Sta.Run(() =>
        {
            // P-5(b): 検索語の打鍵だけ間引く。チェックボックスは従来どおり即時に数える。
            int pattern = 0,
                count = 0;
            using var dlg = new FindReplaceDialog(
                new FindReplaceCallbacks(
                    FindNext: () => false,
                    FindPrev: () => false,
                    ReplaceOne: () => { },
                    ReplaceAll: () => { },
                    PatternChanged: () => pattern++,
                    UpdateCount: () => count++,
                    InSelectionToggled: _ => { }
                )
            );

            ((TextBox)typeof(FindReplaceDialog).GetField("_pattern", Priv)!.GetValue(dlg)!).Text =
                "a";
            Assert.Equal((1, 0), (pattern, count));

            foreach (var name in new[] { "_matchCase", "_wholeWord", "_useRegex" })
                ((CheckBox)typeof(FindReplaceDialog).GetField(name, Priv)!.GetValue(dlg)!).Checked =
                    true;
            Assert.Equal((1, 3), (pattern, count));
        });
```
(`Sta.Run` の有無は同じファイルの既存テストに合わせる。)

**Step 4: 失敗を確かめる**

```powershell
dotnet build tests/kxEdit.App.Tests
```
Expected: `PatternChanged`・5 引数の ctor がないのでビルドエラー。

**Step 5: 本番の実装**

`src/kxEdit.App/WinFormsDebounceScheduler.cs`:

```csharp
namespace kxEdit.App;

/// <summary>
/// <see cref="IDebounceScheduler"/> の本番実装。WinForms のタイマーなので、満了は UI スレッドの
/// メッセージループで起きる(action は UI スレッドで走る)。所有者が Dispose すること。
/// </summary>
public sealed class WinFormsDebounceScheduler : IDebounceScheduler, IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private Action? _pending;

    public WinFormsDebounceScheduler(int delayMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(delayMs);
        _timer = new System.Windows.Forms.Timer { Interval = delayMs };
        _timer.Tick += OnTick;
    }

    public void Schedule(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _timer.Stop(); // 再始動=遅延は最後の予約から数える
        _pending = action;
        _timer.Start();
    }

    public void Cancel()
    {
        _timer.Stop();
        _pending = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // 単発: 先に止めて保留を外してから実行する(action の中で Schedule し直してもよい)。
        _timer.Stop();
        var action = _pending;
        _pending = null;
        action?.Invoke();
    }

    public void Dispose()
    {
        Cancel();
        _timer.Dispose();
    }
}
```

`FindReplaceCallbacks`(`IFindReplaceView.cs`)に `Action PatternChanged,` を `UpdateCount` の前へ足し、summary に「PatternChanged は検索語の打鍵(件数更新を間引く)、UpdateCount はチェックボックスの変化(即時)」を 1 文足す。

`FindReplaceDialog.cs:61`:

```csharp
        _pattern.TextChanged += (_, _) => _cb.PatternChanged(); // 件数更新は Controller 側で間引く(P-5(b))
```

`SearchController.cs`:

```csharp
    /// <summary>検索語の打鍵から件数表示の更新までの遅延(設計書 §10.2(b)・§3.5)。</summary>
    public const int CountDebounceMs = 200;

    private readonly IDebounceScheduler _countDebounce;
```
ctor に第 5 引数 `IDebounceScheduler countDebounce` を足して `_countDebounce = countDebounce;`。ctor のハンドラを次にする。

```csharp
        _docs.ActiveDocumentChanged += (_, _) =>
        {
            _lastHit = null; // 別文書の歩進状態を持ち越さない
            _selectionScope = null; // 別文書へ切替時は捕捉済みスコープも無効化
            _countDebounce.Cancel(); // 表示中なら直後の UpdateCount が新しい文書で数える
            DropSearcher(); // 別文書の材質化キャッシュを持ち越さない(破棄トリガ ii-a)
            if (_view?.Visible == true)
                UpdateCount(); // 表示中なら新アクティブで件数を更新
        };
        // 破棄トリガ ii-b: タブクローズ。ActiveDocumentChanged は選択タブ削除で発火が保証されず、
        // 非アクティブタブのクローズでは切替自体が起きないため、こちらが唯一の通知源。
        // 保留中の件数更新は取り消さない: 取り消すと最後の打鍵の件数が出ないまま古い件数が残る。
        // 満了時の UpdateCount はアクティブ文書を数えるので、閉じた文書を掴み直さない。
        _docs.DocumentClosed += (_, _) => DropSearcher();
```
`Open` のコールバック束に `PatternChanged: OnPatternChanged,` を足し、`Dismissed` の購読を次にする。

```csharp
            _view.Dismissed += (_, _) =>
            {
                // 保留中の件数更新を取り消す。満了すると searcher と全文キャッシュを作り直し、
                // 検索を終えた後も文書を掴み続ける。
                _countDebounce.Cancel();
                DropSearcher();
            };
```
メソッドを足す(`UpdateCount` の前)。

```csharp
    /// <summary>検索語の打鍵。件数表示の更新を <see cref="CountDebounceMs"/> 後へ間引く(最後の打鍵から数える)。
    /// 件数は発声しないので、SR の挙動は変わらない(設計書 §10.2(b))。</summary>
    public void OnPatternChanged() => _countDebounce.Schedule(UpdateCount);
```
`UpdateCount` の先頭(`var d = _view;` の前)に:

```csharp
        // 即時の更新は保留中の打鍵の更新を置き換える(チェックボックス・Open・タブ切替がここを通る)。
        _countDebounce.Cancel();
```
`Find` の `if (ed is null || opts is null) return false;` の直後、`ReplaceOne` と `ReplaceAll` の `if (ed is null || opts is null || d is null) return;` の直後に:

```csharp
        // 保留中の件数更新を取り消す(満了すると、この後の通知のステータスを「N 件」で上書きする)。
        // 空条件の早期 return より後に置く: そちらは何も表示しないので、取り消すと古い件数が残る。
        _countDebounce.Cancel();
```

`MainForm.cs`:
- フィールド(`_search` の直後): `private readonly WinFormsDebounceScheduler _searchCountDebounce = new(SearchController.CountDebounceMs); // 検索語の打鍵の件数更新を間引く(P-5(b))`
- 324 行: `_search = new SearchController(_docs, this, _announcer, cb => new FindReplaceDialog(cb), _searchCountDebounce);`
- `Dispose(bool)` の `if (disposing)` の中に `_searchCountDebounce.Dispose();`

**Step 6: `WinFormsDebounceScheduler` のテスト**(`tests/kxEdit.App.Tests/WinFormsDebounceSchedulerTests.cs`)

```csharp
using System.Diagnostics;

namespace kxEdit.App.Tests;

/// <summary>本番の間引き: 最後の予約だけが 1 回走ること・取り消せること(時間の精度は検証しない)。</summary>
public class WinFormsDebounceSchedulerTests
{
    /// <summary>条件が満たされるか上限時間が過ぎるまでメッセージを汲む。</summary>
    private static void PumpUntil(Func<bool> done, int maxMs)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < maxMs)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void Schedule_Twice_RunsOnlyLatestOnce() =>
        Sta.Run(() =>
        {
            using var s = new WinFormsDebounceScheduler(30);
            var ran = new List<string>();
            s.Schedule(() => ran.Add("first"));
            s.Schedule(() => ran.Add("second"));

            PumpUntil(() => ran.Count > 0, 3000);
            PumpUntil(() => false, 150); // 余計な 2 回目が来ないこと

            Assert.Equal(new[] { "second" }, ran);
        });

    [Fact]
    public void Cancel_PreventsRun() =>
        Sta.Run(() =>
        {
            using var s = new WinFormsDebounceScheduler(30);
            bool ran = false;
            s.Schedule(() => ran = true);
            s.Cancel();

            PumpUntil(() => ran, 300);

            Assert.False(ran);
        });
}
```

**Step 7: テストを通す**

```powershell
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~SearchController|FullyQualifiedName~FindReplaceDialog|FullyQualifiedName~WinFormsDebounceScheduler"
dotnet test tests/kxEdit.App.Tests
```
Expected: すべて PASS。既存の SearchController のテストは、`UpdateCount` / `OpenFind` を直接呼ぶので間引きの影響を受けない。

**Step 8: Commit**

```powershell
git add src/kxEdit.App tests/kxEdit.App.Tests
git commit -m "perf(search): 検索語の打鍵では件数表示の更新を 200 ms 間引く(P-5(b))"
```
本文に「意図的な挙動変更(設計書 §3.5)。タブを閉じる経路では取り消さない(計画 §0.2・設計書からの逸脱)」を書く。

**Step 9: 前倒しのコード品質レビュー**(別エージェント)。観点: 抽象の最小性、取り消し点の網羅と根拠、タイマーの所有と解放、テストが guard の発火条件と一致していること(CLAUDE.md §4-B)。

---

## Task 5: P-14 一致位置のキャッシュ(挙動不変)

**Files:**
- Create: `src/kxEdit.Core/Search/MatchPositions.cs`・`tests/kxEdit.Core.Tests/Search/MatchPositionsTests.cs`
- Modify: `src/kxEdit.Core/Search/TextSearcher.cs`(internal ctor・`CollectMatches`)・`src/kxEdit.Core/Search/MaterializedSearchStrategy.cs`
- Test: `tests/kxEdit.Core.Tests/Search/MaterializedSearchStrategyTests.cs`・`tests/kxEdit.App.Tests/SearchControllerTests.cs`

**Step 1: 発声の特性テストを先に書き、旧実装で通す**(`SearchControllerTests` の末尾)

```csharp
    // ===== P-14(2026-09-25): 「N 件中 M 件目」の発声が一致位置表の導入で変わらないこと =====
    // 旧実装(全件列挙)で PASS することを確かめてから P-14 を入れる=特性テスト。

    [Fact]
    public void Announcements_ForF3ShiftF3AndReplace_AreStable() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("ab ab ab ab");
            host.View.Pattern = "ab";
            host.View.Replacement = "X";
            host.Search.OpenReplace();

            for (int i = 0; i < 5; i++)
                host.Search.FindNext();
            host.Search.FindPrev();
            host.Search.FindPrev();
            host.Search.ReplaceOne(); // 選択中の (3,2) を置換 → 次は新しいスナップショットで数える
            host.Search.FindPrev(); // 置換後のスナップショットで Shift+F3

            Assert.Equal(
                new[]
                {
                    "4 件中 1 件目",
                    "4 件中 2 件目",
                    "4 件中 3 件目",
                    "4 件中 4 件目",
                    "これ以上見つかりません",
                    "4 件中 3 件目",
                    "4 件中 2 件目",
                    "置換しました。3 件中 2 件目",
                    "3 件中 1 件目",
                },
                host.Announcer.Said
            );
        });

    [Fact]
    public void Announcements_ForZeroWidthRegex_AreStable() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("ab cd");
            host.View.Pattern = @"\b";
            host.View.UseRegex = true;
            host.Search.OpenFind();

            for (int i = 0; i < 5; i++)
                host.Search.FindNext();

            Assert.Equal(
                new[]
                {
                    "4 件中 1 件目",
                    "4 件中 2 件目",
                    "4 件中 3 件目",
                    "4 件中 4 件目",
                    "これ以上見つかりません",
                },
                host.Announcer.Said
            );
        });
```
期待値は旧実装の挙動から手で導いたもの。**旧実装で FAIL したら、期待値のほうを旧実装の実際の発声に合わせる**(この 2 件は「変わらないこと」を固定する網であり、期待値の正しさを主張するものではない)。

```powershell
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~Announcements_For"
```
Expected: PASS(旧実装)。ここで commit する。

```powershell
git add tests/kxEdit.App.Tests/SearchControllerTests.cs
git commit -m "test(search): F3・Shift+F3・置換の発声の特性テスト(P-14 の前)"
```

**Step 2: 失敗するテストを書く — `MatchPositionsTests`**(新規)

```csharp
using System.Text.RegularExpressions;
using kxEdit.Core.Buffers;
using kxEdit.Core.Search;
using Xunit;

namespace kxEdit.Core.Tests.Search;

/// <summary>
/// 一致位置表(P-14)。正解は旧実装=<see cref="TextSearcher"/> の <c>Locate</c> / <c>FindPrev</c> /
/// <c>Count</c>(全件列挙)で、表を使う <see cref="MaterializedSearchStrategy"/> がそれと一致することを見る。
/// </summary>
public class MatchPositionsTests
{
    // ---- 表そのもの(合成した開始位置で、二分探索と線形走査の両方を旧実装の規則と比べる) ----

    /// <summary>旧 <c>TextSearcher.Locate</c> のループを、列挙の代わりに配列へ適用したもの。</summary>
    private static (int, int)? RefLocate(int[] starts, int[] lengths, MatchSpan span)
    {
        int ordinal = 0,
            total = 0;
        bool found = false;
        for (int k = 0; k < starts.Length; k++)
        {
            total++;
            if (starts[k] == span.Start && lengths[k] == span.Length)
            {
                ordinal = total;
                found = true;
            }
        }
        return found ? (ordinal, total) : null;
    }

    /// <summary>旧 <c>TextSearcher.FindPrev</c> のループ(同じ break 規則)。</summary>
    private static MatchSpan? RefFindPrev(int[] starts, int[] lengths, int before)
    {
        MatchSpan? last = null;
        for (int k = 0; k < starts.Length; k++)
        {
            if (starts[k] >= before)
                break;
            last = new MatchSpan(starts[k], lengths[k]);
        }
        return last;
    }

    private static void AssertMatchesReference(int[] starts, int[] lengths)
    {
        var p = new MatchPositions(starts, lengths);
        Assert.Equal(starts.Length, p.Count);
        int max = starts.Length == 0 ? 0 : starts.Max();
        for (int s = -1; s <= max + 2; s++)
            for (int l = 0; l <= 3; l++)
                Assert.Equal(RefLocate(starts, lengths, new(s, l)), p.Locate(new(s, l)));
        for (int before = -1; before <= max + 3; before++)
            Assert.Equal(RefFindPrev(starts, lengths, before), p.FindPrev(before));
    }

    [Fact]
    public void Empty_table_matches_reference() => AssertMatchesReference([], []);

    [Fact]
    public void Strictly_increasing_tables_match_reference()
    {
        var rnd = new Random(20260925);
        for (int t = 0; t < 300; t++)
        {
            int n = rnd.Next(0, 12);
            var starts = new int[n];
            var lengths = new int[n];
            int pos = rnd.Next(0, 3);
            for (int k = 0; k < n; k++)
            {
                starts[k] = pos;
                lengths[k] = rnd.Next(0, 3);
                pos += 1 + rnd.Next(0, 3); // 狭義単調増加
            }
            Assert.True(new MatchPositions(starts, lengths).IsStrictlyIncreasingForTest);
            AssertMatchesReference(starts, lengths);
        }
    }

    [Theory]
    [InlineData(new[] { 0, 2, 2, 5 }, new[] { 1, 0, 0, 1 })] // 同じ (Index, Length) が 2 回=Locate は最後
    [InlineData(new[] { 0, 2, 2, 5 }, new[] { 1, 0, 1, 1 })] // 同じ Index で長さ違い
    [InlineData(new[] { 0, 3, 1, 5 }, new[] { 1, 2, 2, 0 })] // 減少(startat より前のマッチ)=FindPrev は break 規則
    [InlineData(new[] { 4, 1 }, new[] { 0, 0 })]
    public void Non_monotone_tables_fall_back_to_linear_rules(int[] starts, int[] lengths)
    {
        Assert.False(new MatchPositions(starts, lengths).IsStrictlyIncreasingForTest);
        AssertMatchesReference(starts, lengths);
    }

    // ---- 戦略(実際の正規表現で、旧実装と比べる) ----

    private static readonly SearchOptions[] Conditions =
    [
        new("a", MatchCase: true),
        new("aa", MatchCase: true),
        new("ab"),
        new("ab", WholeWord: true),
        new("😀", MatchCase: true),
        new("a*", UseRegex: true),
        new("b*", UseRegex: true),
        new(@"\b", UseRegex: true),
        new("(?=a)", UseRegex: true),
        new("a|ab", UseRegex: true),
        new("[ab]+?", UseRegex: true),
        new("$", UseRegex: true),
        new("(?m)^", UseRegex: true),
        new(@"\r?\n", UseRegex: true),
        new(".", UseRegex: true),
        new("(?:b(?!a)+?)*", UseRegex: true), // Match(text, startat) が startat より前を返す例(TextSearcher の doc)
    ];

    private static string RandomText(Random rnd)
    {
        string[] parts = ["a", "b", "A", " ", "\r", "\n", "😀"];
        var sb = new System.Text.StringBuilder();
        int n = rnd.Next(0, 10);
        for (int i = 0; i < n; i++)
            sb.Append(parts[rnd.Next(parts.Length)]);
        return sb.ToString();
    }

    [Fact]
    public void Strategy_matches_old_implementation_for_random_texts()
    {
        var rnd = new Random(925);
        foreach (var opts in Conditions)
        {
            for (int t = 0; t < 60; t++)
            {
                string text = RandomText(rnd);
                var snap = TextBuffer.FromString(text).Current;
                var reference = new TextSearcher(opts);
                var s = new MaterializedSearchStrategy(new TextSearcher(opts));

                Assert.Equal(reference.Count(text), s.Count(snap)); // 未構築(Regex.Count)
                for (int before = 1; before <= text.Length + 2; before++)
                    Assert.Equal(reference.FindPrev(text, before), s.FindPrev(snap, before));
                for (int st = -1; st <= text.Length + 1; st++)
                    for (int l = 0; l <= 3; l++)
                        Assert.Equal(
                            reference.Locate(text, new(st, l)),
                            s.Locate(snap, new(st, l))
                        );
                Assert.Equal(reference.Count(text), s.Count(snap)); // 構築済み(表の件数)
            }
        }
    }

    [Fact]
    public void Overlapping_candidates_follow_matches_not_match_at()
    {
        // "aaa" を "aa" で探すと Matches は (0,2) だけ。(1,2) は Match(text, 1) なら返るがヒットではない。
        var snap = TextBuffer.FromString("aaa").Current;
        var s = new MaterializedSearchStrategy(
            new TextSearcher(new SearchOptions("aa", MatchCase: true))
        );
        Assert.Equal((1, 1), s.Locate(snap, new(0, 2)));
        Assert.Null(s.Locate(snap, new(1, 2)));
        Assert.Equal(new MatchSpan(0, 2), s.FindPrev(snap, 3));
    }

    [Fact]
    public void Count_does_not_build_table_but_uses_it_once_built()
    {
        var buffer = TextBuffer.FromString("ab ab ab");
        var s = new MaterializedSearchStrategy(
            new TextSearcher(new SearchOptions("ab", MatchCase: true))
        );

        Assert.Equal(3, s.Count(buffer.Current));
        Assert.Equal(0, s.BuildCountForTest); // Count は構築を始めない(M-5 を悪化させない)

        Assert.Equal((2, 3), s.Locate(buffer.Current, new(3, 2)));
        Assert.Equal(1, s.BuildCountForTest);
        Assert.True(s.HasPositionsForTest);
        Assert.Equal(new MatchSpan(3, 2), s.FindPrev(buffer.Current, 6));
        Assert.Equal(3, s.Count(buffer.Current));
        Assert.Equal(1, s.BuildCountForTest); // 同じスナップショットでは作り直さない

        buffer.Insert(8, " ab");
        Assert.Equal(4, s.Count(buffer.Current)); // 古い表の件数を返さない
        Assert.Equal(1, s.BuildCountForTest); // Count は新しいスナップショットでも構築しない
        Assert.Equal((4, 4), s.Locate(buffer.Current, new(9, 2)));
        Assert.Equal(2, s.BuildCountForTest);
    }

    [Theory]
    [InlineData(2, false)] // 3 件 > 上限 2 → 表を作らない
    [InlineData(3, true)] // 3 件 = 上限 3 → 作る
    public void Table_is_not_built_beyond_limit(int limit, bool built)
    {
        var snap = TextBuffer.FromString("a a a").Current;
        var s = new MaterializedSearchStrategy(
            new TextSearcher(new SearchOptions("a", MatchCase: true)),
            new SnapshotTextCache(),
            maxCachedMatches: limit
        );

        Assert.Equal((2, 3), s.Locate(snap, new(2, 1))); // どちらでも答えは同じ
        Assert.Equal(new MatchSpan(2, 1), s.FindPrev(snap, 4));
        Assert.Equal(built, s.HasPositionsForTest);
        Assert.Equal(1, s.BuildCountForTest); // 失敗も記憶する=作り直しを試みない
    }

    [Fact]
    public void Timeout_while_building_falls_back_to_old_path()
    {
        // "xyx" の後ろに破滅的なバックトラックを起こす区間を置く。旧実装の FindPrev(2) は
        // 3 件目の x(Index 2 >= 2)で break するので、その区間を照合しない=成功する。
        // 表の構築は全件を列挙するので、その区間でタイムアウトする。
        var opts = new SearchOptions("x|y|(a|aa)+$", UseRegex: true);
        string text = "xyx" + new string('a', 40) + "b";
        var timeout = TimeSpan.FromMilliseconds(50);
        // 前提: 全件の列挙がタイムアウトすること(しなければテストの前提違い=パターンを選び直す)
        Assert.Throws<RegexMatchTimeoutException>(() =>
            new TextSearcher(opts, timeout).Count(text)
        );
        Assert.Equal(new MatchSpan(1, 1), new TextSearcher(opts, timeout).FindPrev(text, 2));

        var snap = TextBuffer.FromString(text).Current;
        var s = new MaterializedSearchStrategy(new TextSearcher(opts, timeout));

        Assert.Equal(new MatchSpan(1, 1), s.FindPrev(snap, 2)); // 従来の経路で答える
        Assert.False(s.HasPositionsForTest);
        Assert.Throws<RegexMatchTimeoutException>(() => s.Locate(snap, new(0, 1))); // 伝播も従来どおり
        Assert.Equal(1, s.BuildCountForTest); // タイムアウトも記憶する
    }
}
```
注意: `Timeout_…` の前提 assert が落ちる(.NET の最適化で破滅的にならない)場合は、`(a|aa)+$` を `(a|a)+$`・`(a+)+$` 等に替えて前提が成り立つものを選ぶ。前提を満たさないまま assert を消さないこと。

**Step 3: 失敗を確かめる**

```powershell
dotnet build tests/kxEdit.Core.Tests
```
Expected: `MatchPositions`・`BuildCountForTest` 等がないのでビルドエラー。

**Step 4: `MatchPositions` を作る**(`src/kxEdit.Core/Search/MatchPositions.cs`)

```csharp
namespace kxEdit.Core.Search;

/// <summary>
/// 1 つの本文に対する全ヒットの位置表(列挙順)。F3 のたびの全件列挙をやめ、
/// <see cref="TextSearcher.Locate"/> と <see cref="TextSearcher.FindPrev"/> と同じ答えを表から返す
/// (2026-09-25 フェーズ 5 P-14)。
/// </summary>
/// <remarks>
/// <para>
/// <b>二分探索してよいのは、開始位置が狭義単調増加のときだけ</b>。同じ開始位置が重なると、
/// 旧 <c>Locate</c> の「最後に一致したもの」を二分探索では選べない。.NET の Regex は
/// <c>Match(text, startat)</c> で startat より前のマッチを返すことがある(<see cref="TextSearcher.ReplaceInRange"/>
/// の doc)。列挙で同じことが起きた場合に備え、単調でなければ旧実装と同じ順序・同じ break 規則で
/// 線形に走査する。
/// </para>
/// <para>網 = <c>MatchPositionsTests</c>(旧実装のループとの照合)。</para>
/// </remarks>
internal sealed class MatchPositions
{
    private readonly int[] _starts;
    private readonly int[] _lengths;
    private readonly bool _strictlyIncreasing;

    internal MatchPositions(int[] starts, int[] lengths)
    {
        if (starts.Length != lengths.Length)
            throw new ArgumentException("開始位置と長さの数が違います。", nameof(lengths));
        _starts = starts;
        _lengths = lengths;
        _strictlyIncreasing = IsStrictlyIncreasing(starts);
    }

    /// <summary>ヒットの件数。</summary>
    public int Count => _starts.Length;

    internal bool IsStrictlyIncreasingForTest => _strictlyIncreasing;

    /// <summary><see cref="TextSearcher.Locate"/> と同じ(span が何件目か。同じヒットが複数なら最後)。</summary>
    public (int Ordinal, int Total)? Locate(MatchSpan span)
    {
        if (_strictlyIncreasing)
        {
            int i = Array.BinarySearch(_starts, span.Start);
            return i >= 0 && _lengths[i] == span.Length ? (i + 1, _starts.Length) : null;
        }
        int ordinal = 0;
        for (int k = 0; k < _starts.Length; k++)
            if (_starts[k] == span.Start && _lengths[k] == span.Length)
                ordinal = k + 1;
        return ordinal > 0 ? (ordinal, _starts.Length) : null;
    }

    /// <summary><see cref="TextSearcher.FindPrev"/> と同じ(開始位置が before より厳密に前にある、列挙順で最後のヒット)。</summary>
    public MatchSpan? FindPrev(int before)
    {
        // k = 「開始位置が before 以上の最初の添字」(旧実装が break する位置)
        int k;
        if (_strictlyIncreasing)
        {
            int i = Array.BinarySearch(_starts, before);
            k = i >= 0 ? i : ~i;
        }
        else
        {
            k = 0;
            while (k < _starts.Length && _starts[k] < before)
                k++;
        }
        return k > 0 ? new MatchSpan(_starts[k - 1], _lengths[k - 1]) : null;
    }

    private static bool IsStrictlyIncreasing(int[] starts)
    {
        for (int i = 1; i < starts.Length; i++)
            if (starts[i] <= starts[i - 1])
                return false;
        return true;
    }
}
```

**Step 5: `TextSearcher` に internal ctor と `CollectMatches` を足す**

```csharp
    /// <summary>照合条件から照合エンジンを構築する。不正でも例外は投げず IsValid/Error で返す。</summary>
    public TextSearcher(SearchOptions options)
        : this(options, TimeSpan.FromSeconds(1)) { }

    /// <summary>照合のタイムアウトを指定して構築する(テスト用。本番は 1 秒の public ctor を使う)。</summary>
    internal TextSearcher(SearchOptions options, TimeSpan matchTimeout)
    {
        // (従来の本体。new Regex(body, opts, TimeSpan.FromSeconds(1)) を matchTimeout にする)
    }
```

```csharp
    /// <summary>
    /// text の全ヒットを列挙順に表へ集める(P-14)。件数が <paramref name="limit"/> を超えたら null
    /// (表を作らない)。列挙は <see cref="Locate"/> / <see cref="FindPrev"/> と同じ <c>Matches</c> で行う
    /// =同じ集合・同じ順序。無効なら null。
    /// 複雑な正規表現では RegexMatchTimeoutException が送出され得る(捕捉しない)。
    /// </summary>
    internal MatchPositions? CollectMatches(string text, int limit)
    {
        if (_regex is null)
            return null;
        var starts = new List<int>();
        var lengths = new List<int>();
        foreach (Match m in _regex.Matches(text))
        {
            if (starts.Count == limit)
                return null;
            starts.Add(m.Index);
            lengths.Add(m.Length);
        }
        return new MatchPositions(starts.ToArray(), lengths.ToArray());
    }
```

**Step 6: `MaterializedSearchStrategy` に表を持たせる**

```csharp
    /// <summary>一致位置表を作る件数の上限(設計書 §10.3)。超えたら表を作らず従来の経路で答える(配列の肥大化を防ぐ)。</summary>
    internal const int DefaultMaxCachedMatches = 1_000_000;

    private readonly int _maxCachedMatches;

    // 一致位置表(P-14)。_positionsSnapshot は「表を試みたスナップショット」で、_positions が null なら
    // 未構築ではなく「作れなかった」(上限超え・タイムアウト)。同じスナップショットでは作り直さない。
    // 全文キャッシュと違って searcher(照合条件)ごとに持つ(表は正規表現に依存する)。
    private TextSnapshot? _positionsSnapshot;
    private MatchPositions? _positions;

    /// <summary>テスト観測用: 表の構築を試みた回数(失敗も数える)。</summary>
    internal int BuildCountForTest { get; private set; }

    /// <summary>テスト観測用: 直近に試みたスナップショットの表があるか。</summary>
    internal bool HasPositionsForTest => _positions is not null;
```
ctor を次にする(既存の 2 つに `maxCachedMatches` を通す)。

```csharp
    internal MaterializedSearchStrategy(TextSearcher inner)
        : this(inner, new SnapshotTextCache()) { }

    internal MaterializedSearchStrategy(
        TextSearcher inner,
        SnapshotTextCache texts,
        int maxCachedMatches = DefaultMaxCachedMatches
    )
    {
        _inner = inner;
        _texts = texts;
        _maxCachedMatches = maxCachedMatches;
    }
```
表の取得と、4 メソッドの置き換え:

```csharp
    /// <summary>
    /// snap の一致位置表。初めてのスナップショットなら構築を試みる。作れなければ null
    /// (呼び出し側は従来の経路=全件列挙で答える)。
    /// </summary>
    private MatchPositions? PositionsOf(TextSnapshot snap, string text)
    {
        if (ReferenceEquals(_positionsSnapshot, snap))
            return _positions;
        BuildCountForTest++;
        MatchPositions? built;
        try
        {
            built = _inner.CollectMatches(text, _maxCachedMatches);
        }
        catch (RegexMatchTimeoutException)
        {
            // 表を確定させずに従来の経路へ戻す。従来の経路が同じくタイムアウトすれば、
            // 例外はそちらから従来どおり伝播する(旧 FindPrev は早く break して成功することもある)。
            built = null;
        }
        // 代入順は表が先・スナップショットが後(TextOf と同じ理由。入れ替えないこと)。
        _positions = built;
        _positionsSnapshot = snap;
        return built;
    }

    /// <summary>
    /// 表が構築済みなら表の件数、未構築なら従来どおり <c>Regex.Count</c>。
    /// <b>ここから構築を始めない</b>: 検索語の打鍵では searcher が作り直されるので表は再利用されず、
    /// 構築(Matches の全列挙と配列の確保)は Match を作らない Regex.Count より重い(設計書 §10.3)。
    /// </summary>
    public int Count(TextSnapshot snap)
    {
        string text = TextOf(snap);
        return ReferenceEquals(_positionsSnapshot, snap) && _positions is { } p
            ? p.Count
            : _inner.Count(text);
    }
```
`FindPrev`(doc の「before をクランプしない」はそのまま残す。表も同じく生の before で答える):

```csharp
    public MatchSpan? FindPrev(TextSnapshot snap, int before)
    {
        string text = TextOf(snap);
        return PositionsOf(snap, text) is { } p
            ? p.FindPrev(before)
            : _inner.FindPrev(text, before);
    }

    public (int Ordinal, int Total)? Locate(TextSnapshot snap, MatchSpan span)
    {
        string text = TextOf(snap);
        return PositionsOf(snap, text) is { } p ? p.Locate(span) : _inner.Locate(text, span);
    }
```
`FindNext` / `ReplacementAt` / `ReplaceInRange` は変えない。`FindNext` の上に「表で置き換えない: `Regex.Match(text, from)` は Matches の集合と一致しない(`"aaa"` を `"aa"` で探すと Matches は (0,2) だけだが、`Match(text, 1)` は (1,2) を返す)」を 1 段落足す。クラス remarks に P-14 の段落(表を持つこと・構築の契機・失敗の記憶・古いスナップショットを次の照合まで掴むこと)を足す。`using System.Text.RegularExpressions;` を足す。

**Step 7: テストを通す**

```powershell
dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~Search"
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~SearchController"
dotnet test tests/kxEdit.Core.Tests; dotnet test tests/kxEdit.App.Tests
```
Expected: すべて PASS。`Announcements_For…` の 2 件が変更後も PASS であること。

**Step 8: Commit**

```powershell
git add src/kxEdit.Core/Search tests/kxEdit.Core.Tests/Search
git commit -m "perf(search): Locate と FindPrev を一致位置の表で答える(P-14)"
```

---

## Task 6: ミューテーション検証(スポットチェック・ユーザー承認 2026-09-25)

**Files:** なし(結果は実施記録に書く。変異は必ず戻す)

**注意(メモリー: 古いバイナリで偽の生存)**: `--no-build` を使わない。変異ごとに `dotnet build tests/kxEdit.Core.Tests` の成功を確かめてからテストを走らせる。ビルドに失敗した変異は「不成立」として記録する。

| # | 場所 | 変異 | 期待 |
|---|---|---|---|
| M1 | `MatchPositions.Locate` | `i >= 0` → `i > 0` | 殺される(先頭のヒットの Locate) |
| M2 | 同 | `&& _lengths[i] == span.Length` を消す | 殺される(同じ開始位置で長さ違いの span) |
| M3 | `MatchPositions.FindPrev` | `k = i >= 0 ? i : ~i` → `i >= 0 ? i + 1 : ~i` | 殺される(before ちょうどに開始位置がある場合) |
| M4 | 同 | `~i` → `~i - 1` | 殺される |
| M5 | 同(線形) | `_starts[k] < before` → `<=` | 殺される(非単調の表) |
| M6 | `IsStrictlyIncreasing` | `<=` → `<` | 殺される(同じ開始位置が重なる表で Locate が最後を返さない) |
| M7 | `CollectMatches` | `starts.Count == limit` → `starts.Count > limit` | 殺される(`Table_is_not_built_beyond_limit(2, false)`) |
| M8 | `MaterializedSearchStrategy.Count` | `ReferenceEquals(_positionsSnapshot, snap) &&` を消す | 殺される(編集後の Count) |
| M9 | 同 | `Count` で `PositionsOf` を呼んで表を使う | 殺される(`BuildCountForTest == 0`) |
| M10 | `PositionsOf` | `catch (RegexMatchTimeoutException)` を消す | 殺される(`Timeout_…`) |

**Step 1〜:** 1 変異ずつ当て、`dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~MatchPositionsTests|FullyQualifiedName~MaterializedSearchStrategyTests"` を走らせ、殺したテスト名を記録して戻す。最後に `git diff --exit-code src/` で変異が残っていないことを確かめる。

**Step Final:** 実施記録に表を書いて commit(docs のみ)。予想外の生存があればテストを足して fixup commit にする。

```powershell
git add docs/plans/2026-09-25-perf-search.md
git commit -m "docs(perf): フェーズ 5 のミューテーション検証の結果を記録"
```

---

## Task 7: 変更後の計測

Task 1 と同じ手順・同じ NVDA の状態(なし)で行う(publish は `<scratchpad>\pub-after`)。NVDA の終了と harness の実行は、直前にユーザーへ声をかける。

**判定**(設計書 §3.2・§10.4)
- B1(全文化): 変更前の揺れ(3 回の最小〜最大)を超えて下がること。
- B2 → B2s: B2s が全文化の分だけ下がること(B2 は P-13 の分だけ下がる)。
- B3・B4: 全件列挙がなくなり、大きく下がること。B3' は変更前から大きく悪化しないこと(構築の配列確保の分は増えうる)。
- harness M-5: 下がること(打鍵ごとの件数更新が外れる。§0.1 の注意)。

実施記録に書いて commit(docs のみ)。

---

## Task 8: 最終ブランチレビュー(2 パス)と品質ゲート

CLAUDE.md §3 の 5・§6 に従う。

- **コード品質パス**(ミューテーション検証のスポットチェック込み)と**脆弱性パス**を、別々のエージェントで起動する。並走させるときは、scratchpad の専用ディレクトリを割り当て、リポジトリでのビルドを禁止する(メモリー: レビューエージェントの並走はワークツリーを共有する)。
- 脆弱性パスの観点: 正規表現のタイムアウトの扱いが従来と同じであること(構築の失敗で例外が握りつぶされないこと)、一致位置表の上限(メモリーの上限)、全文キャッシュと表の寿命(閉じた文書を掴み続けないこと)、`GetText` の実行時検査、間引きの満了がダイアログを閉じた後に文書を掴み直さないこと。
- 指摘は fixup commit で反映し、3 択(① 修正 / ② 受容 / ③ 却下)で実施記録に書く。
- `tools/pre-merge-check.ps1` → EXIT 0。

---

## Task 9: 件数表示の遅延の目視(設計書 §10.4)

**方法**(windows-mcp。スクリプトは scratchpad に置き捨て)
- `<scratchpad>\pub-after` を起動し、ja10k 相当の文書を開く(`%APPDATA%\kxEdit` を退避・復元する)。
- Ctrl+F で検索語を 1 文字ずつ打つ。打鍵の直後はステータスが前の値のままで、最後の打鍵から約 200 ms 後に「N 件」になることを、ダイアログを PrintWindow で実解像度に撮って確かめる(メモリー: 縮小スクリーンショットで判定しない)。
- 「大文字と小文字を区別」の切替では、ステータスが即時に変わることを確かめる。
- 打鍵の直後(200 ms 以内)に Enter を押し、ステータスが「N 件中 1 件目」のまま「N 件」で上書きされないことを確かめる。

実施記録に書いて commit(docs のみ)。

---

## Task 10: PR

- `git push -u origin feature/perf-search`
- PR description(日本語): 目的・変更点(P-13・P-5(a)(b)・P-14)・変更前後の計測値・意図的な挙動差(§0.5)・設計書からの逸脱(§0.2 のタブを閉じる経路)・ミューテーション検証の結果・レビュー経緯・L5 不要の理由と目視の結果・申し送り。
- 設計書 §10 の末尾へ「### 10.5 実施記録」を追記し、本 PR に同梱する(フェーズ 4 と同じ扱い。同梱しない場合はマージ後に次のフェーズのブランチで追記する)。

---

## 実施記録

### Task 0: 本計画(4ba11f6)

### Task 1: 変更前の計測(2026-09-25・src は main `14ca692` と同一・bench は 63685a5)

- **ベンチ**: Core.Bench `--search` を 63685a5 で追加した。仕様レビューの指摘はなし。
  - Expected との差: 文書は約 1,590,000 字の見込みだったが、実際は **1,470,000 字**(1 行 49 字 × 30,000 行)。計画の見積もりの誤りで、コードの問題ではない。バイト数は 2,970,000 で見込みどおり。
- **§0.1 からの変更(ユーザー判断 2026-09-25)**: **NVDA 起動中に測る**。いったん NVDA を止めて Core.Bench を採ったが、ユーザーの指示で NVDA ありに切り替え、Core.Bench も採り直した。変更後も NVDA 起動中に測る。設計書 §5.5 の「フェーズ 5 は NVDA なしで採り直す」は行わない。
- **Core.Bench `--search`**(NVDA 起動中・各行の中央値の 3 回の最小 / 中央 / 最大・ms)

| 行 | 最小 | 中央 | 最大 |
|---|---|---|---|
| B1 全文化 | 2.030 | 2.030 | 2.046 |
| B2 打鍵 1 回(searcher ごとに全文化) | 3.187 | 3.223 | 3.327 |
| B3 F3 1 回(FindNext + Locate) | 1.800 | 1.803 | 1.808 |
| B3' F3 初回(searcher を作り直す) | 11.255 | 11.871 | 12.415 |
| B4 Shift+F3 1 回(FindPrev + Locate) | 3.616 | 3.628 | 3.739 |

  参考として、NVDA なしの 3 回の中央値は B1 1.998 / B2 3.189 / B3 1.774 / B3' 11.902 / B4 3.557 ms。NVDA の有無による差は揺れの範囲だった(UI を通さないため)。
- **harness M-5**(publish `pub-before`・NVDA 起動中・CPU ms/実打鍵・3 回の最小 / 中央 / 最大)

| 文書 | 最小 | 中央 | 最大 |
|---|---|---|---|
| empty | 9.21 | 10.32 | 11.16 |
| ja10k | 12.00 | 12.00 | 14.23 |
| ja30k | 12.83 | 13.95 | 14.79 |

  CSV は `%LOCALAPPDATA%\kxEdit-perf-harness\results-20260925-211913.csv`・`-212010.csv`・`-212106.csv`(`env` 行: NVDA 起動中・`status=completed`)。
