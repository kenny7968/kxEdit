# フェーズ 4: 追記ブロックの格子(perf-append-grid) 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development(または executing-plans)で、タスク単位に実装する。

**Goal:** 打鍵や貼り付けで入力したテキストへの文字アクセスが、追記ブロック(64KB)内の書込位置に比例して重くなる問題(F-6)を解消する。

**設計書:** `docs/plans/2026-09-24-general-perf-improvements-design.md` §3・§9(以下「設計書」)
**調査記録:** `docs/plans/2026-09-24-general-perf-audit.md` P-3・M-3(以下「調査記録」)
**前フェーズの計画:** `docs/plans/2026-09-25-perf-skip-invalidate.md`(実施記録を設計書 §8 へ転記する)

**Architecture:**
- `TextChunk` の ctor に `gridLimit` を足し、格子点を `< gridLimit` の位置にだけ置けるようにする。既定(`int.MaxValue`)は従来と同一。
- `AppendBuffer` は、次の格子点(名目 4KB の倍数を継続バイトの先へ前方スナップした位置)が書込済み範囲の**厳密に内側**に入ったら、同じ `_block` を `gridLimit = _pos` の新しい `TextChunk` で包み直す。順序は「書込 → 包み直し → 新しいチャンクでピースを作る」。
- 未書込のゼロ領域には格子点を置かないので、2026-07-31 に細かい格子を壊した原因(ゼロ領域の累積値の焼き付け)を踏まない。古い包みは、書込済み範囲が不変なのでそのまま有効(Undo・古いスナップショット・RPC スレッドの読み)。
- ピース木・`TextBuffer.Splice` の隣接マージには触れない。包み直しの直後の打鍵は新しいピースになる(1 ブロックで最大 16 ピース増)。

**Tech Stack:** C#(.NET 9)、xUnit、kxEdit.Core.Bench、kxEdit.Editor.Smoke、PowerShell 7。

---

## 0. 前提と決定事項

### 0.1 計測の条件(設計書 §3.2・§5.5・§9.4)

- **Smoke `--perf --scenario S3,S5,S7`**(Release・3 回)。S5 が改善の対象(のこぎり型が消えること)。S3・S7 は変わらないことの確認(ja10k / en10k は FromString のチャンクで、追記ブロックを通らない)。
  - S5 の位置 0(r0)は画面がほぼ空なので比較対象外(フェーズ 0 の記録と同じ)。
- **Core.Bench**(Release・各 3 回)
  - `--largeline` の F-6 節(打鍵で育てた 70,000 字の GetChar 3 点と PrevWordStart)。位置によらなくなること。
  - `--typing`(ゲート 5 µs/insert)。通ること。ピース数も記録する(包み直しで増える)。
  - `--mb 64`(既定ベンチの「7 連続タイピング断片化」ゲート Δ≤50 を含む)。通ること。
- **perf-harness M-3**(publish・3 回)。**ユーザーの了承済み(2026-09-25)**。実行の直前にもう一度声をかける(十数分キーボードとマウスを占有し、`%APPDATA%\kxEdit` を退避・復元する)。
- **NVDA の有無を揃える。** 変更前の状態を実施記録に書き、変更後も同じ状態で測る。
- 画面のロック中は測らない(Smoke の自己チェックが EXIT 1 なら値を捨てる)。他のエージェントの重い処理と並走させない。

### 0.2 設計書からの精密化

- **`gridLimit` の既定値**: `int.MaxValue` とし、ctor の中で `Math.Min(gridLimit, bytes.Length)` にする。設計書は「既存の呼び出しは `gridLimit = bytes.Length` と等価」としていた。既定引数に `bytes.Length` は書けないので、この形で等価にする。負の値は `ArgumentOutOfRangeException`。
- **前方スナップの範囲**: スナップの走査も `limit` までに限る(`while (p < limit && 継続バイト)`)。従来は `n` までだった。スナップ後に `p >= limit` なら置かないので、結果は同じ。ゼロ領域を読まないことを字面でも明らかにするための変更。
- **初期の包みと繰上げ後の包み**: `gridBytes: BlockBytes` をやめ、`gridLimit: 0` にする。どちらも格子表は先頭 1 エントリだけで、同じ結果になる。以後の格子幅は `TextChunk.DefaultGridBytes`(4KB)に揃う。
- **包み直しの判定は `AppendBuffer` 側で持つ**。`_nextNominal`(今の包みに入っていない最初の名目格子点)を保持し、書込のたびに `SnapToCodePoint(_nextNominal) < _pos` を見る。
  - `TextChunk` に格子の中身を問い合わせる API は足さない(判定のために格子表を公開しない)。
  - 包み直したら、`_nextNominal` を「スナップ後が `_pos` 以上になる最初の名目点」まで進める。`TextChunk` の ctor の規則(`p < limit` にだけ置く)と一致する。
- **包み直しの構築は全体を走査する**(前の包みの格子を引き継ぐ差分構築はしない)。1 ブロックで最大 16 回 × 平均 32KB = 約 512KB の走査で、打鍵 64KB あたりの費用として無視できる(`--typing` のゲートで確かめる)。YAGNI。
- **テスト用の観測点**: `TextChunk` に `internal ReadOnlySpan<int> GridByteOffsets => _gByte;` を足す(格子点の位置を直接確かめるため。Core.Tests は InternalsVisibleTo 済み)。製品コードからは使わない。
- **`WordBoundary.cs:172-176` の実測値コメント**: 「AppendBuffer 由来 piece で約 17 倍」は本フェーズで前提が変わる。書き換えず(当時の実測の記録なので)、「フェーズ 4 以後は格子が 4KB ごとに置かれるので、この差は縮む(未計測)」を 1 行足す。

### 0.3 レビューとミューテーション検証

- 前倒しの脆弱性レビュー: 該当なし(外部入力のパース・パス操作・プロセス起動・WebView・ネットワークに触れない)。
- 前倒しのコード品質レビュー: 該当なし(設計書 §3.4 の列挙にない。新しい seam を作らない)。
- **ミューテーション検証: 実施する(ユーザー承認 2026-09-25)**。設計書 §3.4 の「列挙の外だが、厳密な挙動の保証が要るもの」。対象は次の 2 か所に限る(Task 4)。
  - `TextChunk` の ctor の格子点の上限判定
  - `AppendBuffer` の包み直しの条件と順序

### 0.4 L5

設計書 §9.4 のとおり**必須**。新規文書に長文(64KB 以上)を打ち込んだ後、NVDA で行・単語・文字単位に読み、読み上げ位置がずれないこと。
- **進め方(ユーザー判断 2026-09-25)**: フェーズ 3 と同様に自動確認を先に行い、実機での確認の要否はその時点で相談する。
- `tools/sr-regression.ps1` も実行する。

### 0.5 意図的な挙動差

- 設計書 §3.5 のとおり: 追記ブロックでは、格子点が増えるたびにピースが 1 つ増える(最大で 4KB ごとに 1 つ。内部の診断値 `PieceCount` だけに現れる)。
- 本文・行・文字位置の結果は変わらない。

---

## Task 0: フェーズ 3 の実施記録を設計書へ追記する(docs のみ)

フェーズ 3(PR #89)はマージ済みで、設計書 §8 の末尾への実施記録の追記が残っている(設計書 §3.1 の 6)。

**Files:**
- Modify: `docs/plans/2026-09-24-general-perf-improvements-design.md`(§8.5 の後、§9 の前に「### 8.6 実施記録(2026-09-25・PR #89)」を足す)

**Step 1:** §6.6・§7.4 と同じ形(成果物・完了条件・本節からの精密化・意図的な挙動差・以後のフェーズへの申し送り)で書く。材料は `docs/plans/2026-09-25-perf-skip-invalidate.md` の実施記録。要点:
- 成果物: `FrameInputs` の seam(`CaptureFrameInputs()`・static な `PaintBody`)、4 経路の `InvalidateIfFrameChanged()`、記録を捨てる 5 経路、オラクル、Smoke `--paint-transition`。
- 完了条件: S1 ja10k 7.45 → 0.08 ms・S2 ja10k 7.56 → 0.10 ms(`paints_per_op` 1 → 0)。harness M-2 の →← ja10k 17.19 → 10.31 ms(中央値)。L5 は自動確認(X と Y の差がすべて 0 画素、NVDA の発声は操作どおり)。実機確認(実 IME・視覚的ハイライト)はユーザーの判断で省いた。
- 精密化: `FrameInputs` は `required init` の sealed record・手書きの Equals。記録を捨てる経路に `UndoEolConversion`・`ApplyAppearance` を足した。オラクルはフレームでなく画素で比べる。オラクルが検出するのは 4 経路で変わる状態の漏れに限る(計画の前提の訂正)。
- 意図的な挙動差: なし。観測できる差として、4 経路でフレームが変わらなければ `Invalidated` が発火しない。ClearType など FrameInputs が追跡しない OS の設定の変化は、キャレット移動では描き直されなくなった(脆弱性パス Minor-2・受容)。
- 申し送り(フェーズ 9): オラクルの無効化矩形単位の合成、`--paint-transition` のクリップ矩形の記録と `Expect.Partial`、IME の原点の値化、ScrollWindowEx の適否判定の形、PaintSnapshot との重複の切り出し、窓を重ねた状態での撮影の対照。

**Step 2: Commit**

```powershell
git add docs/plans/2026-09-24-general-perf-improvements-design.md
git commit -m "docs(perf): 設計書 §8 にフェーズ 3 の実施記録を追記"
```

**Step 3: 本計画を commit する**

```powershell
git add docs/plans/2026-09-25-perf-append-grid.md
git commit -m "docs(perf): フェーズ 4(追記ブロックの格子)の実装計画"
```

---

## Task 1: 変更前の計測(src は未変更)

**Files:** なし(結果は本書の実施記録に書く。生の JSON / CSV / ログは scratchpad に置き、リポジトリに入れない)

**Step 1: NVDA の状態を記録する**

```powershell
Get-Process nvda -ErrorAction SilentlyContinue | Select-Object Id, StartTime
```

**Step 2: Smoke `--perf` を 3 回**

```powershell
$s = "<scratchpad>\perf"
New-Item -ItemType Directory -Force $s | Out-Null
foreach ($i in 1..3) {
  dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --perf --scenario S3,S5,S7 --json "$s\before-$i.json"
}
```
Expected: 各回 EXIT 0。EXIT 1 の回は捨てて取り直す。

**Step 3: 集計する**(S5 は `param` = 書込位置ごと)

```powershell
$rows = foreach ($f in Get-ChildItem "$s\before-*.json") {
  (Get-Content $f -Raw -Encoding utf8 | ConvertFrom-Json).results |
    Select-Object id, doc, param, median_ms, paints_per_op
}
$rows | Group-Object id, doc, param | ForEach-Object {
  $m = $_.Group.median_ms | Sort-Object
  [pscustomobject]@{ key = $_.Name; min = $m[0]; mid = $m[1]; max = $m[2] }
} | Format-Table -AutoSize
```

**Step 4: Core.Bench を 3 回ずつ**

```powershell
foreach ($i in 1..3) {
  dotnet run --project tests/kxEdit.Core.Bench -c Release -- --largeline | Out-File -Encoding utf8 "$s\before-largeline-$i.txt"
  dotnet run --project tests/kxEdit.Core.Bench -c Release -- --typing    | Out-File -Encoding utf8 "$s\before-typing-$i.txt"
  dotnet run --project tests/kxEdit.Core.Bench -c Release -- --mb 64     | Out-File -Encoding utf8 "$s\before-mb64-$i.txt"
}
```
見る行: `--largeline` の「F-6: AppendBuffer 現ブロック経路」節(ピース数・GetChar 3 点・PrevWordStart)、`--typing` の µs/insert とピース数、`--mb 64` の「7 連続タイピング断片化」と「2 splice」。

**Step 5(実行の直前にユーザーへ声をかけてから): perf-harness の M-3 を 3 回**

```powershell
dotnet publish src/kxEdit.App -c Release -o "<scratchpad>\pub-before"
foreach ($i in 1..3) { pwsh -File tools/perf-harness.ps1 -PublishDir "<scratchpad>\pub-before" -Scenario M-3 }
```
CSV の `env` 行で NVDA の有無と `status=completed` を確かめる。

**Step 6:** 集計値と NVDA の状態を、本書末尾の実施記録に書いてコミットする。

```powershell
git add docs/plans/2026-09-25-perf-append-grid.md
git commit -m "docs(perf): フェーズ 4 の変更前の計測値を記録"
```

---

## Task 2: `TextChunk` に `gridLimit` を足す(挙動不変)

**Files:**
- Modify: `src/kxEdit.Core/Buffer/TextChunk.cs`(ctor・コメント・テスト用の `GridByteOffsets`)
- Test: `tests/kxEdit.Core.Tests/Buffer/TextChunkTests.cs`

**Step 1: 失敗するテストを書く**(`TextChunkTests` の末尾に追加)

```csharp
    [Fact]
    public void GridLimit_default_places_same_grid_as_before()
    {
        // 既定(int.MaxValue)は「上限 = バイト長」と等価。格子点はバイト長ちょうどには置かない
        byte[] bytes = Encoding.UTF8.GetBytes(new string('a', 20));
        var chunk = new TextChunk(bytes, gridBytes: 4);
        AssertGrid(chunk, 0, 4, 8, 12, 16);
    }

    [Theory]
    [InlineData(0, new[] { 0 })]
    [InlineData(4, new[] { 0 })] // 上限ちょうど(厳密に未満の規則)
    [InlineData(5, new[] { 0, 4 })]
    [InlineData(12, new[] { 0, 4, 8 })]
    [InlineData(13, new[] { 0, 4, 8, 12 })]
    public void GridLimit_places_grid_points_strictly_below_limit(int limit, int[] expected)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(new string('a', 20));
        var chunk = new TextChunk(bytes, gridBytes: 4, gridLimit: limit);
        AssertGrid(chunk, expected);
    }

    [Fact]
    public void GridLimit_snapped_point_at_or_beyond_limit_is_not_placed()
    {
        // "aaa" + "あ"(3..5) + "b": 名目 4 は「あ」の途中 → 前方スナップで 6
        byte[] bytes = Encoding.UTF8.GetBytes("aaaあbcdefgh");
        AssertGrid(new TextChunk(bytes, gridBytes: 4, gridLimit: 6), 0);
        AssertGrid(new TextChunk(bytes, gridBytes: 4, gridLimit: 7), 0, 6);
    }

    [Fact]
    public void GridLimit_does_not_bake_zero_region_into_grid()
    {
        // 書込済み 10 バイト + 未書込のゼロ領域。上限を書込済みの長さにすれば、ゼロ領域に格子点を
        // 置かない。後からゼロ領域へ書いた文字の char↔byte 対応が壊れないこと(AppendBuffer の前提)
        byte[] block = new byte[64];
        byte[] head = Encoding.UTF8.GetBytes("あいう\nx"); // 9+1+1 = 11 バイト・5 文字
        head.CopyTo(block, 0);
        var chunk = new TextChunk(block, gridBytes: 4, gridLimit: head.Length);
        byte[] tail = Encoding.UTF8.GetBytes("えお\r\nz"); // 後から書く(11..20)
        tail.CopyTo(block, head.Length);
        string all = "あいう\nx" + "えお\r\nz";
        int totalBytes = head.Length + tail.Length;
        AssertStatsEqual(block[..totalBytes], chunk.StatsOfRange(0, totalBytes));
        for (int c = 0; c <= all.Length; c++)
            Assert.Equal(ByteOff(all, c), chunk.CharToByte(0, totalBytes, c));
    }

    [Fact]
    public void GridLimit_negative_throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextChunk(new byte[8], gridLimit: -1));

    private static void AssertGrid(TextChunk chunk, params int[] expected) =>
        Assert.Equal(expected, chunk.GridByteOffsets.ToArray());
```

**Step 2: 失敗を確かめる**

```powershell
dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~TextChunkTests"
```
Expected: コンパイルエラー(`gridLimit` と `GridByteOffsets` がない)。

**Step 3: 実装する**(`TextChunk.cs` の ctor を置き換える)

```csharp
    // 2026-07-31: 既定 64KB → 4KB。…(既存のコメントはそのまま)
    // 注: AppendBuffer は共有ブロックを gridLimit=書込済みの長さで包み、書き進めるたびに包み直す
    //     (2026-09-25 フェーズ 4。理由は AppendBuffer のクラスコメント)。
    /// <param name="gridLimit">
    /// 格子点を置く位置の上限。この位置<b>未満</b>にだけ置く(既定は <paramref name="bytes"/> の長さ)。
    /// <see cref="AppendBuffer"/> は書込済みの長さを渡す。未書込のゼロ領域から累積値を焼き付けないため。
    /// </param>
    public TextChunk(
        ReadOnlyMemory<byte> bytes,
        int gridBytes = DefaultGridBytes,
        int gridLimit = int.MaxValue
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gridBytes, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(gridLimit);
        _bytes = bytes;
        var span = bytes.Span;
        int limit = Math.Min(gridLimit, span.Length);
        int capacity = limit / gridBytes + 2;
        var gb = new List<int>(capacity) { 0 };
        var gc = new List<int>(capacity) { 0 };
        var gk = new List<int>(capacity) { 0 };
        for (long nominal = gridBytes; nominal < limit; nominal += gridBytes)
        {
            int p = (int)nominal;
            while (p < limit && (span[p] & 0xC0) == 0x80)
                p++; // コード点境界へ前方スナップ(上限の先は読まない)
            if (p >= limit || p == gb[^1])
                continue;
            var (ch, f) = ScanForward(span, gb[^1], gc[^1], gk[^1], p);
            gb.Add(p);
            gc.Add(ch);
            gk.Add(f);
        }
        _gByte = [.. gb];
        _gChar = [.. gc];
        _gBreaks = [.. gk];
    }

    /// <summary>テスト観測用: 格子点のバイト位置(昇順・先頭は 0)。製品コードからは使わない。</summary>
    internal ReadOnlySpan<int> GridByteOffsets => _gByte;
```

既存の 37 行目のコメント「注: AppendBuffer の共有ブロックは gridBytes: BlockBytes を明示して除外している(理由は同所)。」は、上の 2 行の注に置き換える。

**Step 4: テストを通す**

```powershell
dotnet test tests/kxEdit.Core.Tests
```
Expected: 全件 PASS(既存の `gridBytes:` 指定の呼び出しはすべて既定の上限 = バイト長で、従来と同一)。

**Step 5: Commit**

```powershell
git add src/kxEdit.Core/Buffer/TextChunk.cs tests/kxEdit.Core.Tests/Buffer/TextChunkTests.cs
git commit -m "feat(core): TextChunk に格子点の上限 gridLimit を足す"
```

---

## Task 3: `AppendBuffer` の包み直し

**Files:**
- Modify: `src/kxEdit.Core/Buffer/AppendBuffer.cs`
- Create: `tests/kxEdit.Core.Tests/Buffer/AppendBufferGridTests.cs`
- Modify: `tests/kxEdit.Core.Tests/Buffer/FuzzTests.cs`(追記に偏ったケース)
- Modify(コメントのみ): `src/kxEdit.Core/Buffer/TextSnapshot.cs:95-97`、`tests/kxEdit.Core.Tests/Buffer/TextSnapshotGetCharEquivalenceTests.cs:92-95,159-160,169-172,211-214`、`src/kxEdit.Core/Editing/WordBoundary.cs:176` の後、`tests/kxEdit.Core.Bench/Program.cs:377-381`

**Step 1: 失敗するテストを書く**(`AppendBufferGridTests.cs` を新規作成)

```csharp
using System.Text;
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Tests.Buffers;

/// <summary>
/// 2026-09-25 フェーズ 4(F-6): AppendBuffer は次の格子点が書込済み範囲の厳密に内側に入ったら、
/// 同じブロックを gridLimit=書込済みの長さで包み直す。
/// 正解は常に元の文字列に取る(GetChar と GetText は同じ格子を通るので、相互比較では
/// 格子の破損を検出できない。設計書 §9.3・seam 設計書 §9.4)。
/// </summary>
public class AppendBufferGridTests
{
    private const int G = TextChunk.DefaultGridBytes;

    // ---- AppendBuffer 単体: 包み直しの条件と順序 ----

    [Fact]
    public void No_rewrap_when_pos_reaches_nominal_exactly()
    {
        var ab = new AppendBuffer();
        var first = ab.Append(new string('a', G - 1));
        var second = ab.Append("a"); // _pos = 4096 ちょうど: 4096 には置けない(< gridLimit)
        Assert.Same(first[0].Chunk, second[0].Chunk);
        AssertGrid(second[0].Chunk, 0);

        var third = ab.Append("b"); // _pos = 4097: 4096 が内側に入った
        Assert.NotSame(second[0].Chunk, third[0].Chunk);
        AssertGrid(third[0].Chunk, 0, G);
    }

    [Fact]
    public void Nominal_inside_multibyte_char_waits_until_snapped_point_is_inside()
    {
        var ab = new AppendBuffer();
        var first = ab.Append(new string('a', G - 1));
        var second = ab.Append("あ"); // 4095..4097。名目 4096 は途中 → スナップ後 4098 = _pos
        Assert.Same(first[0].Chunk, second[0].Chunk);

        var third = ab.Append("b"); // _pos = 4099: スナップ後の 4098 が内側に入った
        Assert.NotSame(second[0].Chunk, third[0].Chunk);
        AssertGrid(third[0].Chunk, 0, G + 2);
    }

    [Fact]
    public void Piece_of_a_large_write_refers_to_the_rewrapped_chunk()
    {
        // 順序「書込 → 包み直し → ピース」: 32KB 以下の貼り付けが古い格子を参照しないこと
        var ab = new AppendBuffer();
        var pieces = ab.Append(new string('a', 5 * G + 10));
        Assert.Single(pieces);
        AssertGrid(pieces[0].Chunk, 0, G, 2 * G, 3 * G, 4 * G, 5 * G);
    }

    [Fact]
    public void New_block_starts_without_grid_points_and_rewraps_again()
    {
        var ab = new AppendBuffer();
        ab.Append(new string('a', AppendBuffer.LargeInsertBytes));
        ab.Append(new string('a', AppendBuffer.LargeInsertBytes)); // ブロックちょうど満杯
        var next = ab.Append("b"); // 新ブロックの先頭
        Assert.Equal(0, next[0].ByteStart);
        AssertGrid(next[0].Chunk, 0);

        var more = ab.Append(new string('c', G)); // 新ブロックで _pos = 4097
        AssertGrid(more[0].Chunk, 0, G);
    }

    // ---- TextBuffer 経由: 元の文字列との一致 ----

    [Fact]
    public void Typing_up_to_nominal_does_not_add_piece()
    {
        var b = TextBuffer.FromString("");
        for (int i = 0; i < G; i++)
            b.Insert(b.Current.CharLength, "a");
        Assert.Equal(1, b.Current.PieceCount);
        b.Insert(b.Current.CharLength, "a");
        Assert.Equal(2, b.Current.PieceCount); // §3.5 の意図的な挙動差
    }

    [Theory]
    [InlineData(G - 1)] // CR が 4095、LF が 4096(格子点)に来る: CRLF が格子点をまたぐ
    [InlineData(G)] // CR が 4096(格子点)で、書込の最後のバイトになる。LF は次の打鍵
    public void Crlf_straddling_grid_point_typed_one_by_one(int crAt)
    {
        string text = new string('a', crAt) + "\r\n" + new string('b', 100) + "\r\nend";
        var b = TextBuffer.FromString("");
        for (int i = 0; i < text.Length; i++)
        {
            b.Insert(b.Current.CharLength, text[i].ToString());
            if (i >= crAt - 1 && i <= crAt + 2)
                AssertMatchesSource(b.Current, text[..(i + 1)]); // CR だけ書いた状態も含む
        }
        AssertMatchesSource(b.Current, text);
    }

    [Fact]
    public void Multibyte_text_typed_by_code_point_matches_source()
    {
        // 名目 4KB 点が 2・3・4 バイト文字の途中に当たる形を網羅する(周期 13 バイトは 4096 と互いに素)
        var sb = new StringBuilder();
        while (Encoding.UTF8.GetByteCount(sb.ToString()) < 5 * G)
            sb.Append("éあ😀a\r\nx\ry\n");
        string text = sb.ToString();
        var b = TypeByCodePoint(text);
        AssertMatchesSource(b.Current, text);
    }

    [Fact]
    public void Old_snapshots_are_unchanged_after_later_writes_and_rewraps()
    {
        var b = TextBuffer.FromString("");
        var saved = new List<(TextSnapshot Snap, string Text)>();
        var sb = new StringBuilder();
        for (int i = 0; i < 3000; i++)
        {
            string s = (i % 7 == 0 ? "\r\n" : i % 5 == 0 ? "あ" : "ab") + (i % 11 == 0 ? "😀" : "");
            b.Insert(b.Current.CharLength, s);
            sb.Append(s);
            if (i % 250 == 0)
                saved.Add((b.Current, sb.ToString()));
        }
        foreach (var (snap, expected) in saved)
            AssertMatchesSource(snap, expected);
    }

    [Fact]
    public void Undo_redo_across_rewraps_restore_exact_text()
    {
        var b = TextBuffer.FromString("");
        var history = new List<string> { "" };
        for (int i = 0; i < 40; i++)
        {
            string chunk = string.Concat(Enumerable.Repeat($"行{i}あ😀\r\n", 12)); // 約 300 バイト
            b.Insert(b.Current.CharLength, chunk);
            b.BreakUndoCoalescing();
            history.Add(history[^1] + chunk);
        }
        for (int i = history.Count - 1; i > 0; i--)
        {
            Assert.NotNull(b.Undo());
            AssertMatchesSource(b.Current, history[i - 1]);
        }
        for (int i = 1; i < history.Count; i++)
        {
            Assert.NotNull(b.Redo());
            AssertMatchesSource(b.Current, history[i]);
        }
    }

    // ---- helpers ----

    private static void AssertGrid(TextChunk chunk, params int[] expected) =>
        Assert.Equal(expected, chunk.GridByteOffsets.ToArray());

    private static TextBuffer TypeByCodePoint(string text)
    {
        var b = TextBuffer.FromString("");
        for (int i = 0; i < text.Length; )
        {
            int n = char.IsHighSurrogate(text[i]) ? 2 : 1;
            b.Insert(b.Current.CharLength, text.Substring(i, n));
            i += n;
        }
        return b;
    }

    /// <summary>全位置の GetChar・全行の行頭・全位置の行番号を、元の文字列と照合する。</summary>
    private static void AssertMatchesSource(TextSnapshot snap, string src)
    {
        Assert.Equal(src.Length, snap.CharLength);
        Assert.Equal(src, snap.GetText(0, snap.CharLength));
        for (int pos = 0; pos < src.Length; pos++)
            Assert.Equal(src[pos], snap.GetChar(pos));
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < src.Length; i++)
            if (src[i] == '\n' || (src[i] == '\r' && (i + 1 == src.Length || src[i + 1] != '\n')))
                lineStarts.Add(i + 1);
        Assert.Equal(lineStarts.Count, snap.LineCount);
        for (int line = 0; line < lineStarts.Count; line++)
            Assert.Equal(lineStarts[line], snap.GetLineStart(line));
        int li = 0;
        for (int pos = 0; pos <= src.Length; pos++)
        {
            while (li + 1 < lineStarts.Count && lineStarts[li + 1] <= pos)
                li++;
            Assert.Equal(li, snap.GetLineIndexOfChar(pos));
        }
    }
}
```

`FuzzTests` に、追記に偏ったケースを足す(同じクラスの `ModelSplice` / `DeepVerify` / `Material` を使う)。

```csharp
    /// <summary>
    /// 2026-09-25 フェーズ 4: 末尾付近への挿入に偏らせ、追記ブロックの格子点(4KB)と
    /// ブロック(64KB)の境界を何度もまたがせる。包み直しをまたいだ Undo / Redo も踏む。
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Append_heavy_edits_match_naive_model(int seed)
    {
        var rnd = new Random(seed);
        var buffer = TextBuffer.FromString("");
        string text = "";
        var undoStack = new List<string>();
        var redoStack = new List<string>();
        for (int op = 1; op <= 1500; op++)
        {
            int roll = rnd.Next(100);
            if (roll < 70)
            { // 末尾から 0〜40 文字手前への挿入。1 割は 1〜3KB の塊(貼り付け相当)
                int pos = Math.Max(0, text.Length - rnd.Next(41));
                string ins = rnd.Next(10) == 0 ? BigMaterial(rnd) : Material(rnd);
                buffer.Insert(pos, ins);
                text = ModelSplice(text, pos, 0, ins, undoStack, redoStack);
            }
            else if (roll < 82)
            {
                int pos = Math.Max(0, text.Length - rnd.Next(61));
                int len = Math.Min(rnd.Next(1, 31), text.Length - pos);
                buffer.Delete(pos, len);
                text = ModelSplice(text, pos, len, "", undoStack, redoStack);
            }
            else if (roll < 94)
            {
                if (undoStack.Count > 0)
                {
                    Assert.NotNull(buffer.Undo());
                    redoStack.Add(text);
                    text = undoStack[^1];
                    undoStack.RemoveAt(undoStack.Count - 1);
                }
            }
            else if (redoStack.Count > 0)
            {
                Assert.NotNull(buffer.Redo());
                undoStack.Add(text);
                text = redoStack[^1];
                redoStack.RemoveAt(redoStack.Count - 1);
            }
            buffer.BreakUndoCoalescing();
            Assert.Equal(text.Length, buffer.Current.CharLength);
            if (op % 25 == 0)
            {
                DeepVerify(text, buffer.Current, rnd);
                // 末尾 64 文字を GetChar でも照合する(GetText と同じ格子を通らない正解との比較)
                for (int p = Math.Max(0, text.Length - 64); p < text.Length; p++)
                    Assert.Equal(text[p], buffer.Current.GetChar(p));
            }
        }
        // 規模の自己チェック: 格子点とブロックを実際にまたいだこと
        Assert.True(Encoding.UTF8.GetByteCount(text) > 2 * TextChunk.DefaultGridBytes, "追記量が足りない");
    }

    private static string BigMaterial(Random rnd)
    {
        int target = rnd.Next(1000, 3001);
        var sb = new StringBuilder();
        while (sb.Length < target)
            sb.Append(Pool[rnd.Next(Pool.Length)]);
        return sb.ToString();
    }
```

注: 最終テキスト長の自己チェックは「格子点をまたいだ」ことの下限。ブロック(64KB)をまたいだかは総追記バイト数で決まり、Undo で本文が縮んでも追記バッファは戻らない。Step 2 で総追記バイト数を 1 度だけ出力して(`Console.WriteLine` を一時的に足して外す)、5 seed とも 64KB を超えていることを実施記録に書く。超えなければ op 数を増やす。

**Step 2: 失敗を確かめる**

```powershell
dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~AppendBufferGridTests|FullyQualifiedName~FuzzTests"
```
Expected: 包み直しを期待する 4 件(`No_rewrap_…` の後半・`Nominal_inside_…` の後半・`Piece_of_a_large_write…`・`New_block_…` の後半)と `Typing_up_to_nominal…` の後半が FAIL。元の文字列との一致を見るテスト(CRLF・多バイト・古いスナップショット・Undo/Redo・ファズ)は PASS(現行は正しいが遅いだけ)。この予想と違えば、実施記録に書いてから進む。

**Step 3: 実装する**(`AppendBuffer.cs`)

クラスコメントを差し替える:

```csharp
/// <summary>
/// 編集挿入用の追記バッファ。64KB固定ブロック列で、公開済み範囲は以後不変
/// (ブロックが満杯になったら新規作成。配列再確保・上書き禁止=スナップショット安全)。
/// ブロックを包む TextChunk は「書込済みの長さ」を格子点の上限(gridLimit)にして作り、
/// 次の格子点が書込済み範囲の厳密に内側に入るたびに包み直す(2026-09-25 フェーズ 4・F-6)。
/// 格子点を未書込のゼロ領域に置かないことが本クラスの安全性の要である
/// (ゼロ領域で累積 (CharOff, BreaksTo) を焼き付けると、後から書いた文字の char↔byte 対応が
///  静かに壊れる。2026-07-31 の格子細分化で顕在化した)。古い包みは、書込済み範囲が不変なので
/// そのまま有効(古いスナップショット・Undo・RPC スレッドの読み)。
/// </summary>
```

フィールドと ctor・`Append` の繰上げ・`Write` を次にする(21-25 行目のコメントは上のクラスコメントに統合して消す):

```csharp
    private const int GridBytes = TextChunk.DefaultGridBytes;

    private byte[] _block = new byte[BlockBytes];
    private TextChunk _chunk;
    private int _pos;

    // 今の _chunk にまだ入っていない最初の名目格子点(GridBytes の倍数)
    private int _nextNominal = GridBytes;

    public AppendBuffer() => _chunk = new TextChunk(_block, gridLimit: 0);
```

`Append` の繰上げ部分:

```csharp
            _block = new byte[BlockBytes];
            _chunk = new TextChunk(_block, gridLimit: 0);
            _pos = 0;
            _nextNominal = GridBytes;
```

`Write`:

```csharp
    // 順序は「書込 → 包み直し → ピース」。逆にすると、書込で内側に入った格子点を
    // そのピース(32KB 以下の貼り付け)が参照できず、長い線形走査が残る。
    private Piece Write(byte[] src, int off, int len)
    {
        Array.Copy(src, off, _block, _pos, len);
        int start = _pos;
        _pos += len;
        RewrapIfGridPointWritten();
        return new Piece(_chunk, start, len, StatsOf(src, off, len));
    }

    /// <summary>
    /// 次の格子点(名目点を継続バイトの先へ前方スナップした位置)が書込済み範囲の厳密に内側に
    /// 入っていたら、同じブロックを gridLimit=_pos で包み直す。名目点ではなくスナップ後の位置で
    /// 判定するのは、_pos がちょうど名目点のときや名目点が多バイト文字の途中のときに、
    /// 格子点の増えない無駄な包み直し(ピースが 1 つ増えるだけ)をしないため。
    /// </summary>
    private void RewrapIfGridPointWritten()
    {
        if (_nextNominal >= _pos || SnapToCodePoint(_nextNominal) >= _pos)
            return;
        _chunk = new TextChunk(_block, gridLimit: _pos);
        // TextChunk の規則(スナップ後が gridLimit 未満の点だけを置く)に合わせて進める
        while (_nextNominal < _pos && SnapToCodePoint(_nextNominal) < _pos)
            _nextNominal += GridBytes;
    }

    /// <summary>p を継続バイトの先へ前方スナップする(書込済み範囲の外は読まない)。</summary>
    private int SnapToCodePoint(int p)
    {
        while (p < _pos && (_block[p] & 0xC0) == 0x80)
            p++;
        return p;
    }
```

**Step 4: コメントを更新する**(格子 1 エントリ前提の記述)
- `TextSnapshot.cs:95-97`: 「AppendBuffer のチャンクは格子幅=ブロック長で…最大 64 KB 走査するため」→「CharToByte 内の CumAt(byteStart) は最寄りの格子点から走査する(AppendBuffer のチャンクも 2026-09-25 以後は 4KB ごとに格子点を持つが、最大 4KB の走査は残る)。この回避は 1 文字ピースが多数ある文書で効く」。
- `TextSnapshotGetCharEquivalenceTests.cs`
  - 92-95: 「AppendBuffer の格子表が空である前提の網は」→「AppendBuffer の格子がゼロ領域を焼き付けない前提の網は」。
  - 159-160: 「共有ブロックを gridBytes: BlockBytes で包んでいること(= 格子表が先頭 1 エントリだけ)を固定する」→「共有ブロックの格子点を書込済みの範囲にだけ置いていること(ゼロ領域を焼き付けないこと)を固定する。2026-09-25 以後は包み直しで格子点を持つので、格子を実際に通って照合する」。
  - 169-172: 「明示指定は ctor と繰上げの 2 箇所に必要になる」→「gridLimit の指定は ctor・繰上げ・包み直しの 3 箇所にある」。
  - 211-214: 「正しい実装(格子 1 エントリ)だと 1 回の GetChar が O(pos) 走査になり」→「照合の費用を抑えるため(末尾 1 点で全格子幅を捕まえられるので、全位置は要らない)」。
- `WordBoundary.cs:176` の後に 1 行: 「(フェーズ 4 = 2026-09-25 以後、AppendBuffer のチャンクも 4KB ごとに格子点を持つので、この差は縮む。未計測)」。
- `tests/kxEdit.Core.Bench/Program.cs:377-381`: 「同クラスの共有チャンクは格子表を先頭 1 エントリに固定しているため…最大 64KB 走査が残る領域」→「2026-09-25 フェーズ 4 までは共有チャンクの格子表が先頭 1 エントリだけで、最大 64KB を走査していた。フェーズ 4 の前後比較に使う」。

**Step 5: テストを通す**

```powershell
dotnet test tests/kxEdit.Core.Tests
dotnet build -c Release
```
Expected: 全件 PASS。`TextBufferEditTests.Continuous_typing_does_not_fragment_pieces`(1,004 バイト = 格子点をまたがない)も PASS のまま。Release 0 warning。

**Step 6: Editor / App のテストも通す**

```powershell
dotnet test tests/kxEdit.Editor.Tests
dotnet test tests/kxEdit.App.Tests
```

**Step 7: Commit**

```powershell
git add src/kxEdit.Core tests/kxEdit.Core.Tests tests/kxEdit.Core.Bench
git commit -m "perf(core): 追記ブロックを格子点ごとに包み直して F-6 を解消する"
```

**Step 8: 仕様レビュー**(別エージェント)。観点: 設計書 §9.1・§9.2 との一致、順序(書込 → 包み直し → ピース)、ゼロ領域とフロンティアに格子点を置かないこと、テストの正解が元の文字列であること、CLAUDE.md §4-B(no-change のテストが非既定の状態から始まっているか)。

---

## Task 4: ミューテーション検証(スポットチェック)

**Files:** なし(結果は実施記録に書く。変異は必ず戻す)

**注意(メモリー: 古いバイナリで偽の生存)**: `--no-build` を使わない。変異ごとに `dotnet build tests/kxEdit.Core.Tests` の成功を確かめてからテストを走らせる。ビルドに失敗した変異は「不成立」として記録する。

**対象と期待**

| # | 場所 | 変異 | 期待 |
|---|---|---|---|
| M1 | `TextChunk` ctor | `int limit = Math.Min(gridLimit, span.Length)` → `span.Length`(上限を無視) | 殺される(ゼロ領域の焼き付け: `GridLimit_…`・探針ブロック・ファズ) |
| M2 | 同 | `p >= limit` → `p > limit` | 殺される(`GridLimit_places_…` の上限ちょうど) |
| M3 | 同 | スナップの `p < limit` → `p < span.Length` | 等価の見込み(上限の先はゼロなので止まる)。生存したら等価と記録 |
| M4 | 同 | `nominal < limit` → `nominal <= limit` | 等価の見込み(名目点 = 上限は M2 の判定で置かれない) |
| M5 | `RewrapIfGridPointWritten` | `_nextNominal >= _pos` → `>` | 殺される(`No_rewrap_when_pos_reaches_nominal_exactly`) |
| M6 | 同 | `SnapToCodePoint(_nextNominal) >= _pos` → `_nextNominal >= _pos`(スナップなし) | 殺される(`Nominal_inside_multibyte…`) |
| M7 | 同 | `gridLimit: _pos` → `gridLimit: BlockBytes` | 殺される(ゼロ領域の焼き付け) |
| M8 | `Write` | 順序を「ピース → 包み直し」に戻す | 殺される(`Piece_of_a_large_write…`) |
| M9 | 同(ループ) | `while` の `SnapToCodePoint(_nextNominal) < _pos` → `_nextNominal < _pos` | 生存しうる(格子点が 1 つ抜けて遅くなるだけで、結果は正しい)。生存したら、性能のみの変異として記録し、テストを足すかを判断する |
| M10 | `Append` 繰上げ | `_nextNominal = GridBytes` を消す | 殺される(`New_block_…` の後半) |

**Step 1〜:** 1 変異ずつ当てて、`dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~AppendBufferGridTests|FullyQualifiedName~TextChunkTests|FullyQualifiedName~TextSnapshotGetCharEquivalenceTests|FullyQualifiedName~FuzzTests"` を走らせ、殺したテスト名を記録して戻す。最後に `git diff --exit-code src/` で変異が残っていないことを確かめる。

**Step Final:** 実施記録に表を書いて commit(docs のみ)。予想外の生存があればテストを足して fixup commit にする。

```powershell
git add docs/plans/2026-09-25-perf-append-grid.md
git commit -m "docs(perf): フェーズ 4 のミューテーション検証の結果を記録"
```

---

## Task 5: 変更後の計測

Task 1 と同じ手順・同じ NVDA の状態で行う(publish は `<scratchpad>\pub-after`)。harness は実行の直前にユーザーへ声をかける。

**判定**(設計書 §3.2・§9.4)
- S5: r1〜r9 の位置によらず、ほぼ一定になること(変更前は 12.9 → 26.0 ms の増加とブロック越えでの落ち込み)。
- Core.Bench `--largeline` F-6: GetChar 3 点が位置によらないこと。
- `--typing`: 5 µs/insert 未満(PASS)。ピース数の増加を記録する。
- `--mb 64`: 全ゲート PASS。
- S3・S7: 変更前の揺れの範囲(追記ブロックを通らないので変わらないはず)。
- harness M-3: のこぎり型が消えること。

実施記録に書いて commit(docs のみ)。

---

## Task 6: 最終ブランチレビュー(2 パス)と品質ゲート

CLAUDE.md §3 の 5・§6 に従う。

- **コード品質パス**(ミューテーション検証のスポットチェック込み)と**脆弱性パス**を、別々のエージェントで起動する。並走させるときは、scratchpad の専用ディレクトリを割り当て、リポジトリでのビルドを禁止する(メモリー: レビューエージェントの並走はワークツリーを共有する)。
- 脆弱性パスの観点: RPC スレッドが古いスナップショットを読んでいる間に UI スレッドが同じブロックへ書き込む競合(格子点がフロンティアに置かれないこと、クエリ時の CR/LF の先読みが書込中のバイトを読んでも結果が変わらないこと)、`gridLimit` の境界、ブロック満杯時の繰上げ。
- 指摘は fixup commit で反映し、3 択(① 修正 / ② 受容 / ③ 却下)で実施記録に書く。
- `tools/pre-merge-check.ps1` → EXIT 0。
- `tools/sr-regression.ps1` → EXIT 0。

---

## Task 7: L5(実機 SR 検証)

**方法**(フェーズ 3 と同様、自動確認を先に行う。スクリプトは scratchpad に置き捨て)
- publish を起動し、新規タブに 64KB 以上を入れる。貼り付け(32KB 以下の塊)と 1 文字ずつの打鍵を混ぜ、格子点とブロック境界を何度もまたがせる。日本語・絵文字・CRLF を含める。
- UIA の TextPattern で、キャレットを格子点の前後(4KB ごと)とブロック境界(64KB)の前後に置き、行・単語・文字の `ExpandToEnclosingUnit` の文字列が、送った元の文字列の該当箇所と一致することを確かめる。
- NVDA の発声(スピーチビューアーを画面から切り出して読む)で、↓↑(行)・Ctrl+→←(単語)・→←(文字)の読み上げが元の文字列と一致することを、格子点とブロック境界の前後で確かめる。
- `%APPDATA%\kxEdit` を退避・復元する(ハッシュの一致を確認する)。
- 自動確認の結果を示し、**実機での確認の要否をユーザーに相談する**。

実施記録に書いて commit(docs のみ)。

---

## Task 8: PR

- `git push -u origin feature/perf-append-grid`
- PR description(日本語): 目的・変更点・変更前後の計測値・意図的な挙動差(§0.5)・ミューテーション検証の結果・レビュー経緯・L5 の結果・申し送り。
- マージ後に、設計書 §9 の末尾へ実施記録を追記する(次のフェーズのブランチに同梱する)。

---

## 実施記録

### Task 0: 設計書 §8 の実施記録(4241100)

フェーズ 3 の実施記録を設計書 §8.6 として追記した。

### Task 1: 変更前の計測(2026-09-25・src は main `b046ffc` と同一)

**環境**: フェーズ 0〜3 と同じ VM(1024×767・96 DPI)。**NVDA 起動中**(pid 6372)。

**Smoke `--perf --scenario S3,S5,S7`**(Release・3 回とも EXIT 0。3 回の中央値の min / 中央 / max・ms/操作)

| ID | ja10k | en10k |
|---|---|---|
| S3a 挿入 | 7.41 / 7.67 / 7.76 | 6.88 / 7.14 / 7.18 |
| S3b BackSpace | 7.39 / 7.65 / 7.69 | 6.91 / 7.18 / 7.19 |
| S7 全面再描画 | 7.11 / 7.11 / 7.62 | 6.02 / 6.21 / 6.33 |

| S5 書込位置(バイト) | 5 | 10,650 | 21,295 | 31,940 | 42,585 | 53,230 | 63,875 | 74,520 | 85,165 | 95,810 |
|---|---|---|---|---|---|---|---|---|---|---|
| 中央 | 2.76 | 8.71 | 10.99 | 12.80 | 14.32 | 16.02 | **17.82** | **8.89** | 10.68 | 12.56 |
| min〜max | 2.71〜2.83 | 8.47〜8.85 | 10.59〜11.31 | 12.41〜13.63 | 13.77〜15.19 | 15.66〜17.77 | 17.71〜20.93 | 8.42〜9.02 | 10.19〜11.04 | 11.91〜13.92 |

**Core.Bench**(Release・3 回とも EXIT 0)
- `--largeline` の F-6 節(typed 文書 70,000 字・ピース数 2): GetChar(pos=8,750)2,758〜2,905 ns、(26,250)8,187〜8,645 ns、(61,250)19,055〜19,998 ns。PrevWordStart 0.0230〜0.0235 ms/回。
- `--typing`: 0.51〜0.52 µs/insert(PASS)・ピース数 16。
- `--mb 64`: 全ゲート PASS。「7 連続タイピング断片化」Δ2、「2 splice」平均 10.6〜12.4 µs / p99 17.3〜23.9 µs。

**perf-harness M-3**(publish 46838dc・3 回・CPU ms/打鍵。3 回とも `status=completed`・NVDA 起動中)

| 書込位置≈ | 0 | 10,640 | 21,280 | 31,920 | 42,560 | 53,200 | 63,840 | 74,480 | 85,120 | 95,760 |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 回目 | 19.92 | 19.53 | 17.58 | 15.23 | 20.31 | 19.53 | 17.97 | 13.67 | 15.62 | 17.97 |
| 2 回目 | 21.48 | 18.36 | 13.67 | 17.97 | 20.31 | 21.48 | 16.80 | 14.84 | 15.23 | 14.84 |
| 3 回目 | 19.92 | 19.53 | 16.41 | 18.36 | 17.97 | 21.48 | 19.14 | 13.67 | 15.23 | 17.58 |

- harness は 1 回ごとの揺れが大きく、ブロック内の単調増加は Smoke ほどはっきりしない。ブロックを越えた直後(74KB)の落ち込みは 3 回とも見える(53KB の 19.5〜21.5 → 13.7〜14.8)。
- `backups` にファイルはなかった(harness の中止条件に当たらない)。

### Task 2: TextChunk の gridLimit(d4e9adc)

- Step 2 は予想どおりコンパイルエラー(`gridLimit` と `GridByteOffsets` がない)。Core.Tests 1553 件 PASS・Release 0 warning。
- テストのコメントの誤記(「書込済み 10 バイト」→ 実際は 11)を直した。ほかは計画どおり。
- **仕様レビュー**: ✅(既定値で格子表が旧実装と同一になることを論証。ゼロ領域のテストが gridLimit を無視する実装で FAIL することを手で追って確認)。
  - Minor-1(スナップの上限 `p < limit` はどのテストでも観測できない): ② 受容。上限の先で止めても止めなくても、スナップ後が上限以上なら置かないので格子表は同じ。Task 4 の M3 で等価変異として扱う。
  - Minor-2(負の値のテストが `ParamName` を見ていない): ③ 却下。`gridBytes` は既定で有効なので、例外の出所は `gridLimit` に限られる。

### Task 3: AppendBuffer の包み直し(80f0441)

- **Step 2**: 予想どおり 20 件中 5 件が FAIL(包み直しを期待する後半のアサーションだけ: `Typing_up_to_nominal…`(のちに `Typing_adds_piece_only_after_grid_point_is_inside` へ改名)の PieceCount・`New_block_…` の後半・`Nominal_inside_…`・`No_rewrap_…` の NotSame・`Piece_of_a_large_write…`)。元の文字列との一致を見るテストとファズは PASS。
- **ファズの総追記バイト数**(5 seed): 427,735 / 402,631 / 314,267 / 361,279 / 314,271。すべて 64KB を大きく超え、op 数は 1500 のまま。
- Core.Tests 1568・Editor.Tests 639・App.Tests 1011 件 PASS。Release 0 warning。
- **仕様レビュー**: ❌(Important 2・Minor 3)。実装は計画のコードと同一で、正しさの問題はなし(`_nextNominal` の進め方と ctor の配置規則の整合、古い包みの不変、格子値が書込済みバイトだけから決まることを論証)。
  - Important-1(「1 回だけ進める」と、while のスナップ条件の `<=` の 2 変異が全件を通過して生存した。no-change の検証が初期状態からしか始まっていない = CLAUDE.md §4-B): ① fixup でテスト 2 本を足す。
  - Important-2(探針テストはゼロ領域の網ではなくなった。最初の挿入ですぐ包み直すので、初期・繰上げの包みはどのピースも参照しない。全経路を `gridLimit: BlockBytes` にする変異でも 14 件すべて通る): ① fixup でコメントを実態に合わせる。**設計書 §9.3 の「既存の `TextSnapshotGetCharEquivalenceTests`(ゼロ領域の網を含む)は全件通すこと」の前提は崩れた。** ゼロ領域の網は `AppendBufferGridTests`(AssertGrid 群と `Old_snapshots_…`)が担う。
  - Minor-1(実施記録がない): ① 本節。
  - Minor-2(ファズの自己チェックが「最終本文 > 8KB」だけで、ブロックをまたいだことを保証しない): ① fixup で総追記バイト数を数えて assert する。
  - **fixup 7381283 の再レビュー**: 新テスト 2 本が 2 変異をそれぞれ殺すことを、実装者とレビューの双方がコピーで確認した。appendedBytes の数え方も妥当。Minor 1 件(`AppendProbeBlock` の「1 ブロックあたり 3 ピース」は誤りで、実測は 2 ピース): ① 本記録と同じ commit で直した。Core.Tests 1570 件 PASS。
  - Minor-3(設計書 §9.2 の「格子点の直後に LF が後から書かれても補正が効く」は、厳密に内側に置く規則のもとでは起きない): ② 精密化として記録する。格子点 x を置く時点で s[x] は必ず書込済みで、後から LF が来るのはフロンティア(格子点がない)だけである。クエリ時の補正が読む s[x-1]・s[x] は書き換わらない。安全性の結論は変わらない。

### Task 4: ミューテーション検証(HEAD 3c61df1・ユーザー承認 2026-09-25)

13 変異を 1 つずつ当てた。13 件ともビルド成功を確かめてから `--no-build` なしでテストを走らせた。**殺された 10 件・生存 3 件(すべて等価)**。正しさに影響して生存したものはない。最後に `git diff --exit-code src/` が 0、Core.Tests 1570 件 PASS。

| # | 変異 | 結果 | 殺したテスト(抜粋) |
|---|---|---|---|
| M1 | `limit = span.Length`(上限を無視) | 殺された(14 件) | `GridLimit_…` 群・`Old_snapshots_…`・`Piece_of_a_large_write…` ほか |
| M2 | `p >= limit` → `p > limit` | 殺された(2) | `GridLimit_snapped_point_…`・`Snapped_point_equal_to_pos_…` |
| M3 | スナップの `p < limit` → `p < span.Length` | **生存(等価)** | — |
| M4 | `nominal < limit` → `<=` | **生存(等価)** | — |
| M5 | 包み直しの先頭 `_nextNominal >= _pos` → `>` | **生存(等価)** | — |
| M6 | 包み直しの先頭のスナップ判定を削除 | 殺された(1) | `Nominal_inside_multibyte_char_…` |
| M7 | `gridLimit: _pos` → `BlockBytes` | 殺された(6) | `New_block_…`・`No_rewrap_…` ほか |
| M8 | 順序を「ピース → 包み直し」に戻す | 殺された(7) | `Typing_up_to_nominal…`・`No_rewrap_…` ほか |
| M9 | while のスナップ判定を削除 | 殺された(1) | `Snapped_point_equal_to_pos_…` |
| M10 | 繰上げの `_nextNominal = GridBytes` を削除 | 殺された(1) | `New_block_…` |
| M11 | while → if(1 回だけ進める) | 殺された(1) | `No_extra_rewrap_…` |
| M12 | while の `< _pos` → `<= _pos` | 殺された(1) | `Snapped_point_equal_to_pos_…` |
| M13 | `== 0x80` → `== 0xC0`(継続バイトの判定) | 殺された(2) | `Snapped_point_equal_…`・`Nominal_inside_…` |

**生存の論証**
- M3: 差が出るのはスナップ中に p が上限に達したときだけで、直後の `p >= limit` で置かないので格子表は同一。差は「上限の先を数バイト読みうる」ことだけで出力に現れない(AppendBuffer では上限の先はゼロなので、実際には上限で止まる)。
- M4: 追加で回るのは `nominal == limit` の 1 回だけで、`p >= limit` で置かない。
- M5(計画の見込み「殺される」は誤り): `SnapToCodePoint(p)` は `p >= _pos` なら p をそのまま返すので、先頭の `_nextNominal >= _pos` は全体として冗長な早期脱出(スナップの走査を省くだけ)。while 側の `_nextNominal < _pos &&` も同じ。判定の本体であるスナップ項は M6 で守られている。
- M9(計画の見込み「生存しうる」に反して殺された): 飛ばした点も次の包み直しで ctor が置くので正しさは壊れず、性能だけの差。`Snapped_point_equal_to_pos_…` が包み直しの回数を観測しているので殺された。

### Task 5: 変更後の計測(2026-09-25・src は 3c61df1 と同一)

**環境**: Task 1 と同じ。NVDA 起動中(pid 6372)。

**Smoke `--perf --scenario S3,S5,S7`**(3 回とも EXIT 0。min / 中央 / max・ms/操作。括弧内は変更前の中央値)

| ID | ja10k | en10k |
|---|---|---|
| S3a 挿入 | 6.92 / 7.26 / 7.34(7.67) | 6.76 / 6.80 / 6.85(7.14) |
| S3b BackSpace | 6.92 / 7.24 / 7.32(7.65) | 6.78 / 6.82 / 6.92(7.18) |
| S7 全面再描画 | 6.73 / 6.75 / 6.79(7.11) | 5.92 / 5.93 / 5.98(6.21) |

| S5 書込位置(バイト) | 5 | 10,650 | 21,295 | 31,940 | 42,585 | 53,230 | 63,875 | 74,520 | 85,165 | 95,810 |
|---|---|---|---|---|---|---|---|---|---|---|
| 中央 | 2.70 | 7.36 | 7.95 | 7.65 | 8.30 | 7.89 | **7.50** | **7.47** | 7.69 | 8.47 |
| min〜max | 2.69〜2.72 | 7.29〜7.42 | 7.89〜8.07 | 7.54〜7.70 | 8.21〜8.32 | 7.79〜8.09 | 7.49〜7.55 | 7.44〜7.55 | 7.65〜7.81 | 8.42〜8.47 |
| 変更前の中央 | 2.76 | 8.71 | 10.99 | 12.80 | 14.32 | 16.02 | 17.82 | 8.89 | 10.68 | 12.56 |

**Core.Bench**(3 回とも EXIT 0)
- `--largeline` の F-6 節(typed 文書 70,000 字・**ピース数 2 → 18**): GetChar(pos=8,750)197〜200 ns(変更前 2,758〜2,905)、(26,250)543〜554 ns(8,187〜8,645)、(61,250)1,254〜1,269 ns(19,055〜19,998)。PrevWordStart 0.0180〜0.0182 ms(0.0230〜0.0235)。
- `--typing`: 0.71〜0.72 µs/insert(変更前 0.51〜0.52)・**ピース数 16 → 247**。ゲート 5 µs は PASS。
- `--mb 64`: 全ゲート PASS。「7 連続タイピング断片化」Δ2 → Δ4、「2 splice」平均 9.6〜10.9 µs / p99 13.2〜22.9 µs(変更前 10.6〜12.4 / 17.3〜23.9)。

**perf-harness M-3**(publish 8eeca2d・3 回・CPU ms/打鍵。3 回とも `status=completed`・NVDA 起動中。min / 中央 / max)

| 書込位置≈ | 0 | 10,640 | 21,280 | 31,920 | 42,560 | 53,200 | 63,840 | 74,480 | 85,120 | 95,760 |
|---|---|---|---|---|---|---|---|---|---|---|
| 変更後 | 18.75 / 19.92 / 22.27 | 13.28 / 16.80 / 16.80 | 11.33 / 13.67 / 14.84 | 9.38 / 14.06 / 14.45 | 12.89 / 15.23 / 15.62 | 13.67 / 15.62 / 17.19 | 12.11 / 16.80 / 17.19 | 10.55 / 11.72 / 12.50 | 12.89 / 14.06 / 14.84 | 14.06 / 14.45 / 14.84 |
| 変更前 | 19.92 / 19.92 / 21.48 | 18.36 / 19.53 / 19.53 | 13.67 / 16.41 / 17.58 | 15.23 / 17.97 / 18.36 | 17.97 / 20.31 / 20.31 | 19.53 / 21.48 / 21.48 | 16.80 / 17.97 / 19.14 | 13.67 / 13.67 / 14.84 | 15.23 / 15.23 / 15.62 | 14.84 / 17.58 / 17.97 |

**判定**(設計書 §3.2・§9.4)
- **S5**: のこぎり型が消えた。位置 10KB〜96KB で 7.36〜8.47 ms とほぼ一定(変更前は 8.71 → 17.82 → 8.89 ms)。ブロック後半ほど効き、63,875 では 17.82 → 7.50 ms。全位置(r0 を除く)で、変更後の最大値が変更前の最小値を下回った。
- **F-6 の GetChar**: 約 15 分の 1 になった。3 点の値はまだ位置とともに増えるが、これはブロック内の位置ではなく**格子セル内のオフセット**(8,750 − 8,192 = 558、26,250 − 24,576 = 1,674、61,250 − 57,344 = 3,906 バイト)にほぼ比例している。FromString で読み込んだ文書と同じ性質で、上限は格子幅 4KB。設計書 §9.4 の「位置によらなくなる」は、ブロック内の位置によらない、の意味で満たした。
- **`--typing`**: 0.52 → 0.71 µs/insert(+37%)。ゲート(5 µs)には余裕がある。原因は**ほぼすべてピースの増加**(木が深くなり、Split/Join の費用が増える)で、包み直しの全走査の費用は無視できる(打鍵 1 回あたり平均約 8 バイト)。最終レビューのコード品質パス(I-2)が実験で切り分けた: 包み直しの全走査を残したまま、左マージを「同じブロックの別の包み同士なら新しい包みで結合する」に一時的に変えると 0.51〜0.56 µs/insert・ピース数 16 に戻った。したがって §0.2 の「差分構築はしない(YAGNI)」は強く支持される(差分構築で削れる費用がない)。改善するなら、差分構築ではなくマージの拡張が効く(申し送り)。
- **S3・S7**: 追記ブロックを通らないので変わらないはずの行。変更前の中央値より 0.3〜0.4 ms 低く、en10k の S3b を除く 5 行で、変更後の最大値が変更前の最小値を下回った。フェーズ 3 の Task 6 と同じく、日内の揺れの可能性があるので改善とは主張しない。悪化はない。
- **harness M-3**: 位置 10KB 以上の中央値の平均は 17.79 → 14.71 ms。ただし 1 回ごとの揺れが大きく、一部の位置では変更前後の範囲が重なる(変更後の最大値が変更前の最小値を下回ったのは、10,640・31,920・42,560・53,200・74,480・85,120 の 6 点。21,280・63,840・95,760 の 3 点は重なる)。kxEdit の外の費用(IME/TSF・NVDA)が乗るため。§3.2 の判断基準は Smoke(再現性の数値)で満たしており、harness は改善の向きが一致することの確認とする。

### Task 6: 最終ブランチレビュー(2 パス)の指摘と扱い

**脆弱性パス**(Critical・Important なし)
- 確かめられたこと
  - RPC スレッドの読み × UI スレッドの書込: 新旧どちらの包みも、格子値は `[0, gridLimit)`(構築時点の `_pos` 以下 = 書込済みで不変)のバイトだけから決まる。読み手の補正が読む s[x-1]・s[x] も書込済み。`SplitStats` の先読みは呼び出し条件(`pos < CharLen`)からピースの内側に収まる。`StatsOfRange`・`CumAt`・`CharToByte`・`GetSubstring`・`DecodeUtf16At` もピースの外を読まない。
  - 可視性: 新しい TextChunk と格子配列は ctor で作り終えてから Piece → TextSnapshot に載り、`UiaTextHostAdapter._bufferSnapshot`(volatile)の release で公開される。
  - `_nextNominal` が誤っても影響は包み直しの時期(性能)だけで、正しさは依存しない(多重防御)。
  - 境界・算術(gridLimit の負値・0・超過、`(int)nominal`、ブロック満杯、`LargeInsertBytes` ちょうど、空文字列)、不正な UTF-8(範囲外読み・無限ループなし)、資源(包み直しは 1 ブロック最大 15〜16 回で入力に依存しない。DoS 的に悪化する入力はない)。
  - **並行ストレステスト**(リポジトリ外で一時的に作成): UI スレッドが最大 300 万回挿入し、読み手 3 スレッドが最新と古いスナップショット(最大 200 個)を問い合わせて元の文字列と照合。Release で約 790 万回照合し、不一致・例外 0。
- Minor-1(包み直しのたびにブロックを先頭から走査する): ② 受容。回数は入力によらず上限があり、費用は無視できる(コード品質の I-2 で実測)。PR に書く。
- Minor-2(スレッド安全の根拠がコメントにない): ① fixup で AppendBuffer のクラスコメントに書いた。

**コード品質パス**(条件付きでマージ可。Critical なし)
- 確かめられたこと: 0 warning・1570 件 PASS。古い「格子 1 エントリ前提」の記述の残りを全体で grep し、残りは時点を明記した過去の記述だけ。
- **ミューテーション検証のスポットチェック**: 記録の M1・M8・M10・M12 を再現し、結果は記録と一致。M5 の等価の論証も妥当。記録にない変異を 4 つ追加し、すべて殺された(繰上げで `_chunk` を作り直さない・`gridLimit: _pos - 1`・繰上げの包みを `BlockBytes` に・最初の包みを既定引数に)。繰上げの包みを `BlockBytes` にする変異は `New_block_…` の AssertGrid 1 件でしか殺されないが、ゼロ領域の網は AssertGrid 群が担うという Task 3 の整理どおりなので受容。
- I-1(`Multibyte_text_typed_by_code_point…` のコメント「周期 13 バイト」は誤りで、実際は 16 バイト。名目点はいつも模様の先頭に当たり、文字の途中に来る形は一度も起きていない。1 コード点ずつの打鍵では、文字の途中の格子点は構造的に飛び先に使われない): ① fixup でコメントを実態(一般的な回帰網)に合わせた。貼り付けの塊で格子点が文字の途中に来る、元の文字列を正解にしたテストは申し送り。
- I-2(`--typing` の +37% の原因の書き方): ① Task 5 の判定を直した(本 commit)。
- M-1(包み直しの ctor 呼び出しが格子幅を既定値に頼っている): ① fixup で `gridBytes: GridBytes` を明示した。
- M-2(`Typing_up_to_nominal_does_not_add_piece` の名前が後半の検証と合わない): ① fixup で改名した。
- M-3(PR に書くべき挙動差と申し送り): ① 下の「意図的な挙動差の精密化」と「申し送り」に書き、PR に載せる。
- M-4(設計書の「最大 16 ピース」は正確には 15): ② 受容。上限の表現としては誤りではない。

**意図的な挙動差の精密化**(設計書 §3.5 のフェーズ 4 の行)
- 「内部の診断値 `PieceCount` だけに現れる」は厳密には不足で、打鍵のスループットにも現れる(`--typing` 0.52 → 0.71 µs/insert、ゲート 5 µs に対して余裕は大きい)。Bench の「7 連続タイピング断片化」も Δ2 → Δ4(ゲート ≤50)。

**申し送り**(将来のタスク。どのフェーズにも割り当てていない)
1. **同じブロックの別の包み同士の隣接マージ**: `TextBuffer.Splice` の左マージで、下地の配列が同じでバイトが連続していれば、新しい包みを採って結合する。新しい包みの格子は、古い包みのピースの範囲にもそのまま有効(格子点は書込済みの範囲の内側だけ)。これでピースの増加と `--typing` の悪化がほぼ解消する(コード品質 I-2 の実験: 0.51 µs・16 ピース)。本フェーズは設計で「Splice の中核に触れない」と決めたので見送った。同じ下地かどうかは `TextChunk` に判定 API を足す形が望ましい。
2. `WordBoundary.cs` の「約 17 倍・22.5 ms」の再計測(今は「未計測」と注記しただけ)。
3. 貼り付けの塊で格子点が多バイト文字の途中に来る形を、元の文字列を正解にして確かめるテスト(今の網はファズと TextChunk の単体テスト)。
