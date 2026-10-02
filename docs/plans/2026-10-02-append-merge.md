# 追記ブロックのマージ(append-merge) 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 同じ追記ブロックの別の包み同士も `TextBuffer.Splice` の左マージで結合し、元の設計書フェーズ 4 で増えたピースと `--typing` の悪化を元に戻す。

**Architecture:** 先に、貼り付けで格子点が多バイト文字の途中に来る形の照合テスト(項目 27)を入れて安全網にする。次に `TextChunk.SharesBytesWith` を足し、左マージの判定を「同じ下地かつバイト連続」に広げ、結合したピースは新しい包み(`first.Chunk`)を採る(項目 16)。最後に `WordBoundary.cs` の実測値コメントを再計測して更新する(項目 20)。

**Tech Stack:** C# / .NET 9 / xUnit

**Spec:** `docs/plans/2026-09-27-perf-followups-design.md` §3・§9(フェーズ 5)。背景は `docs/plans/2026-09-24-general-perf-improvements-design.md` §9(元の設計書のフェーズ 4)と `docs/plans/2026-09-25-perf-append-grid.md` の申し送り。

## Global Constraints

- 順序: 27(テスト)を先に入れてから 16 を直す(傘 §3.2)。
- 変異検証を行う。`Splice` の左マージは CLAUDE.md §4-A の列挙を広げる扱い(2026-09-27 ユーザー承認)。変異のたびにビルドの成功を確かめる(`--no-build` やビルド失敗で古い DLL が走ると「生存」に見える)。
- 変異・陰性対照は **commit した後に**行い、後始末は `git checkout -- <file>` ではなく `git diff` で確認してから元に戻す(未 commit の変更を消さない)。
- 陰性対照で行を消さない(アナライザーでビルドが落ちて古い DLL で緑に見える)。条件を無効化する形で外す。
- テスト本体で `Thread.Sleep` を使わない(S2925)。
- 一時的なベンチコードは commit しない(scratchpad に置く)。
- L5 は不要(傘 §3.3)。
- 0 warning(`-warnaserror`)。pre-commit フックを飛ばさない。
- コミットメッセージは `test|perf|docs(scope): 日本語の要約`。末尾に `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`。

## Review Focus

1. **Undo の後の打鍵**: Undo で以前のルートに戻ってから打つと、追記位置は Undo したピースの先にあるのでバイトが連続しない → 結合しない。テキストは正しいこと(Task 2 Step 1 のテスト 3)。
2. **数値上は連続だが下地が違う**: ファイル由来のチャンク `[0,3)` の直後に、追記ブロックの `[3,4)` が来る形。下地の判定を落とすと別の配列のバイトを読む(Task 2 Step 1 のテスト 4)。
3. **ブロックの繰上げ**: 64KB を超えて打つと新しいブロックになり、その境界では結合しない(ブロックごとに 1 ピース。Task 2 Step 1 のテスト 1)。
4. **結合したピースが古い包みを採る**: 正しさは同じで格子が粗くなるだけなので、テキストの照合では検出できない。格子点の列で確かめる(Task 2 Step 1 のテスト 2)。
5. **古いスナップショット**: 結合後も、以前に取ったスナップショットが元の文字列と一致する(既存の `Old_snapshots_are_unchanged_after_later_writes_and_rewraps` が新しい結合経路を通る。Task 2 Step 4 で全件を流す)。

---

### Task 1: 貼り付けの格子点が多バイト文字の途中に来る形の照合テスト(項目 27)

**Files:**
- Modify: `tests/kxEdit.Core.Tests/Buffer/AppendBufferGridTests.cs`(「TextBuffer 経由」の節に 1 本足す)

**Interfaces:**
- Consumes: 既存の private ヘルパー `AssertMatchesSource(TextSnapshot, string)`・定数 `G`
- Produces: なし(安全網のテスト)

このテストは製品コードの変更前から PASS する(安全網)。テストに歯があることは、「格子点のうちスナップされた(4096 の倍数でない)点がある」ことの assert で担保する。

- [ ] **Step 1: テストを書く**

`Old_snapshots_are_unchanged_after_later_writes_and_rewraps` の直前に追加する:

```csharp
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Pasted_blocks_with_grid_points_inside_multibyte_chars_match_source(int phase)
    {
        // 傘設計書の項目 27: 数 KB の塊を何度も貼り付け、名目の格子点(4096 の倍数)が
        // 3 バイト・4 バイトの文字の途中に来る形を作る。周期 7 バイト(あ😀)は 4096 を割り切らない
        // (4096 mod 7 = 1)ので、位相 phase(先頭の ASCII)を変えると格子点が文字の途中に来る。
        // 塊は 32KB 以下なので追記ブロックに入る。
        var unit = new StringBuilder();
        while (Encoding.UTF8.GetByteCount(unit.ToString()) < 3000)
            unit.Append("あ😀");
        unit.Append("\r\n");
        string block = unit.ToString(); // 約 3KB(格子幅より小さい)

        var b = TextBuffer.FromString("");
        var expected = new StringBuilder();
        string head = new('a', phase);
        b.Insert(0, head);
        expected.Append(head);
        for (int i = 0; i < 6; i++) // 約 18KB = 格子点 4 つをまたぐ
        {
            b.Insert(b.Current.CharLength, block);
            expected.Append(block);
            AssertMatchesSource(b.Current, expected.ToString());
        }

        // 歯の担保: 名目点が文字の途中だったため前方スナップされた格子点が、少なくとも 1 つある
        var lastChunk = PieceTree.Enumerate(b.Current.Root).Last().Chunk;
        Assert.Contains(
            lastChunk.GridByteOffsets.ToArray(),
            off => off != 0 && off % G != 0
        );
    }
```

- [ ] **Step 2: テストを走らせて PASS を確かめる**

Run: `dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~AppendBufferGridTests"`
Expected: 全件 PASS(新しい 4 ケースを含む)。

FAIL した場合は製品のバグの可能性があるので、Task 2 に進まず原因を調べる(superpowers:systematic-debugging)。
`Assert.Contains` だけが落ちる場合は fixture の位相が狙いどおりでないので、`block` の長さを調整する(製品のバグではない)。

- [ ] **Step 3: Commit**

```powershell
git add tests/kxEdit.Core.Tests/Buffer/AppendBufferGridTests.cs
git commit -m "test(buffer): 貼り付けで格子点が多バイト文字の途中に来る形を元の文字列と照合する"
```

---

### Task 2: 同じ下地の別の包み同士を左マージする(項目 16)

**Files:**
- Modify: `src/kxEdit.Core/Buffer/TextChunk.cs`(`SharesBytesWith` を足す。`:37-38` の注のコメントを更新)
- Modify: `src/kxEdit.Core/Buffer/TextBuffer.cs:237-258`(左マージの判定と採る包み)
- Modify: `src/kxEdit.Core/Buffer/AppendBuffer.cs:5-13`(クラスコメントにマージの一文)
- Test: `tests/kxEdit.Core.Tests/Buffer/AppendBufferGridTests.cs`(既存 1 本の期待値を変更・3 本追加)
- Test: `tests/kxEdit.Core.Tests/Buffer/TextChunkTests.cs`(1 本追加)

**Interfaces:**
- Produces: `internal bool TextChunk.SharesBytesWith(TextChunk other)` — 2 つのチャンクが同じ `ReadOnlyMemory<byte>`(同じ配列・同じ開始・同じ長さ)を包むなら true。

- [ ] **Step 1: 失敗するテストを書く**

(1) `AppendBufferGridTests` の既存テスト `Typing_adds_piece_only_after_grid_point_is_inside` を、次で**置き換える**(期待値が 2 → 1 に変わる。元の設計書 §3.5 のフェーズ 4 の挙動差の解消):

```csharp
    [Fact]
    public void Typing_across_grid_points_and_blocks_keeps_one_piece_per_block()
    {
        // 格子点をまたいで包み直しても、同じブロックの別の包み同士は左マージで 1 ピースに戻る。
        // ブロックの繰上げ(別の配列)では結合しないので、2 ブロック強を打つと 3 ピースになる。
        var b = TextBuffer.FromString("");
        for (int i = 0; i < G + 1; i++)
            b.Insert(b.Current.CharLength, "a");
        Assert.Equal(1, b.Current.PieceCount); // フェーズ 4 の後は 2 だった

        int total = 2 * AppendBuffer.BlockBytes + 100;
        while (b.Current.CharLength < total)
            b.Insert(b.Current.CharLength, "a");
        Assert.Equal(3, b.Current.PieceCount);
        Assert.Equal(new string('a', total), b.Current.GetText(0, total));
    }
```

(2) 同じ節に追加する:

```csharp
    [Fact]
    public void Merged_piece_takes_the_newest_wrap()
    {
        // 結合したピースは新しい包み(格子点が多い)を採る。古い包みを採っても正しさは同じで
        // 格子が粗くなるだけなので、テキストの照合では検出できない(傘設計書 §9.2)。
        var b = TextBuffer.FromString("");
        for (int i = 0; i < 2 * G + 10; i++)
            b.Insert(b.Current.CharLength, "a");
        var piece = Assert.Single(PieceTree.Enumerate(b.Current.Root));
        AssertGrid(piece.Chunk, 0, G, 2 * G);
    }

    [Fact]
    public void Typing_after_undo_does_not_merge_non_contiguous_bytes()
    {
        // Undo で以前のルートに戻ると、追記位置は取り消したバイトの先にある。
        // 下地は同じでもバイトが連続しないので結合しない。
        var b = TextBuffer.FromString("");
        b.Insert(0, "abc");
        b.BreakUndoCoalescing();
        b.Insert(3, "def");
        Assert.NotNull(b.Undo());
        b.Insert(3, "X");
        Assert.Equal("abcX", b.Current.GetText(0, b.Current.CharLength));
        Assert.Equal(2, b.Current.PieceCount);
    }

    [Fact]
    public void Contiguous_offsets_in_different_chunks_are_not_merged()
    {
        // ファイル由来のチャンクのピース [0,3) の直後に、追記ブロックの [3,4) が来る。
        // オフセットは数値上連続だが下地が違うので、結合すると追記ブロックの [0,4) を読んでしまう。
        var b = TextBuffer.FromString("abc");
        b.Insert(0, "xyz"); // 追記ブロック [0,3)
        b.Insert(6, "Q"); // 追記ブロック [3,4)。左隣はファイル由来の [0,3)
        Assert.Equal("xyzabcQ", b.Current.GetText(0, b.Current.CharLength));
    }
```

(3) `TextChunkTests` の末尾に追加する:

```csharp
    [Fact]
    public void SharesBytesWith_is_true_only_for_wraps_of_the_same_memory()
    {
        var block = new byte[64];
        "abcdef"u8.CopyTo(block);
        var oldWrap = new TextChunk(block, gridBytes: 4, gridLimit: 0);
        var newWrap = new TextChunk(block, gridBytes: 4, gridLimit: 6);
        var sameContent = new TextChunk((byte[])block.Clone(), gridBytes: 4, gridLimit: 6);
        var slice = new TextChunk(block.AsMemory(0, 32), gridBytes: 4, gridLimit: 6);

        Assert.True(oldWrap.SharesBytesWith(newWrap));
        Assert.True(newWrap.SharesBytesWith(oldWrap));
        Assert.True(newWrap.SharesBytesWith(newWrap));
        Assert.False(newWrap.SharesBytesWith(sameContent)); // 中身が同じでも別の配列
        Assert.False(newWrap.SharesBytesWith(slice)); // 同じ配列でも範囲が違う
    }
```

- [ ] **Step 2: テストを走らせて失敗を確かめる**

Run: `dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~AppendBufferGridTests|FullyQualifiedName~TextChunkTests"`
Expected: ビルドエラー(`SharesBytesWith` が未定義)。いったん `SharesBytesWith` だけを Step 3 (a) のとおり足して再実行すると、`Typing_across_grid_points_and_blocks_keeps_one_piece_per_block`(1 ピースを期待して 2)と `Merged_piece_takes_the_newest_wrap`(`Assert.Single` が 3 で失敗)が FAIL し、残りは PASS する。
(`Typing_after_undo_…` と `Contiguous_offsets_…` は現行の `ReferenceEquals` でも PASS する回帰網。変異検証で歯を確かめる。)

- [ ] **Step 3: 実装する**

(a) `TextChunk.cs` の `GridByteOffsets` の直後に追加する:

```csharp
    /// <summary>
    /// <paramref name="other"/> と同じ下地(同じ配列の同じ範囲)を包んでいるか。
    /// <see cref="AppendBuffer"/> は 1 ブロックを書き進めるたびに包み直すので、同じブロックの
    /// 新旧の包みは別のオブジェクトになる。<c>TextBuffer.Splice</c> の左マージがそれらを
    /// 1 ピースに結合するために使う(新しい包みの格子は、書込済みの範囲の内側だけに置かれるので、
    /// 古い包みのピースの範囲にもそのまま有効)。
    /// </summary>
    internal bool SharesBytesWith(TextChunk other) => _bytes.Equals(other._bytes);
```

同じファイルの ctor 前の注(`:37-38`)を次にする:

```csharp
    // 注: AppendBuffer は共有ブロックを gridLimit=書込済みの長さで包み、書き進めるたびに包み直す
    //     (2026-09-25 フェーズ 4。理由は AppendBuffer のクラスコメント)。新旧の包みのピースは
    //     TextBuffer.Splice の左マージで結合する(SharesBytesWith。2026-10-02 append-merge)。
```

(b) `TextBuffer.cs` の左マージ(`:237-258`)を次にする:

```csharp
        // 4) 隣接マージ: 同じ下地かつバイト連続なら1ピース化(連続タイピングでピース数が伸びない)。
        //    AppendBuffer は格子点が増えるたびに同じブロックを包み直すので、包みの参照ではなく下地で
        //    判定し、新しい包み(first.Chunk)を採る。first は今回の Append の結果なので常に最新の包みで、
        //    その格子は書込済みの範囲の内側だけに置かれるから last の範囲にも有効。Undo の後は追記位置が
        //    取り消したバイトの先にあるので連続せず、結合は起きない(2026-10-02 append-merge)。
        PieceTree.Node? left = l;
        if (newPieces.Count > 0 && left is not null)
        {
            var (remaining, last) = PieceTree.SplitLast(left);
            var first = newPieces[0];
            if (
                last.Chunk.SharesBytesWith(first.Chunk)
                && last.ByteStart + last.ByteLen == first.ByteStart
            )
            {
                newPieces[0] = new Piece(
                    first.Chunk,
                    last.ByteStart,
                    last.ByteLen + first.ByteLen,
                    PieceStats.Combine(last.Stats, first.Stats)
                );
                left = remaining;
            }
            else
                left = PieceTree.Join(remaining, last, null);
        }
```

右側マージ(`:259-281`)は変えない(傘 §9.1 は左マージだけを対象にする。現設計で発火しない保険)。

(c) `AppendBuffer.cs` のクラスコメントの「古い包みは、…(古いスナップショット・Undo・RPC スレッドの読み)。」の直後に 1 行足す:

```csharp
/// 新旧の包みにまたがる連続したピースは、TextBuffer.Splice の左マージが新しい包みで 1 つに結合する
/// (TextChunk.SharesBytesWith。2026-10-02 append-merge)。
```

- [ ] **Step 4: テストを走らせて PASS を確かめる**

Run: `dotnet test tests/kxEdit.Core.Tests`
Expected: 全件 PASS。`FuzzTests`・`AppendBufferGridTests`・`TextBufferEditTests` を含む Core 全体。

- [ ] **Step 5: Commit**

```powershell
git add src/kxEdit.Core/Buffer/TextChunk.cs src/kxEdit.Core/Buffer/TextBuffer.cs src/kxEdit.Core/Buffer/AppendBuffer.cs tests/kxEdit.Core.Tests/Buffer/AppendBufferGridTests.cs tests/kxEdit.Core.Tests/Buffer/TextChunkTests.cs
git commit -m "perf(buffer): 同じ追記ブロックの別の包み同士を左マージで結合する"
```

- [ ] **Step 6: 変異検証(commit の後に行う)**

各変異で: 変異を入れる → `dotnet build tests/kxEdit.Core.Tests` の**成功を確かめる** → `dotnet test tests/kxEdit.Core.Tests --no-build --filter "FullyQualifiedName~AppendBufferGridTests|FullyQualifiedName~TextChunkTests|FullyQualifiedName~Fuzz"` → 結果を記録 → `git diff` で変異だけであることを確かめてから `git checkout -- <file>`。

| # | 変異 | 殺すはずのテスト |
|---|---|---|
| M1 | 結合したピースに `last.Chunk` を採る | `Merged_piece_takes_the_newest_wrap` |
| M2 | 判定を `ReferenceEquals(last.Chunk, first.Chunk)` に戻す | `Typing_across_grid_points_and_blocks_keeps_one_piece_per_block` |
| M3 | 下地の判定を外す(`true && 連続`) | `Contiguous_offsets_in_different_chunks_are_not_merged` |
| M4 | 連続の判定を外す(`SharesBytesWith && true`) | `Typing_after_undo_does_not_merge_non_contiguous_bytes` |
| M5 | `SharesBytesWith` を「中身の等値」(`_bytes.Span.SequenceEqual(other._bytes.Span)`)にする | `SharesBytesWith_is_true_only_for_wraps_of_the_same_memory` |

生存した変異があれば、等価変異かどうかを判断して記録する。等価でなければテストを足す(別 commit)。

- [ ] **Step 7: 計測(`--typing`)**

変更前(main)と変更後で 3 回ずつ走らせ、中央値の µs/insert とピース数を記録する。NVDA 起動中で測る(傘 §3.4)。

```powershell
git stash list   # 空であることを確認
git switch main
dotnet run -c Release --project tests/kxEdit.Core.Bench -- --typing   # ×3
git switch feature/append-merge
dotnet run -c Release --project tests/kxEdit.Core.Bench -- --typing   # ×3
```

期待: 変更前 ≈0.71 µs・247 ピース → 変更後 ≈0.5 µs・16 ピース(実験値 0.51 µs・16)。

---

### Task 3: `WordBoundary.cs` の実測値コメントの再計測(項目 20)

**Files:**
- Modify: `src/kxEdit.Core/Editing/WordBoundary.cs:172-177`
- 一時: scratchpad の使い捨てコンソールプロジェクト(commit しない)

**条件**(2026-08-04 最終レビュー 脆弱性パス V-3 の条件): 空白のない単一クラスの長大行を、32KB 以下の塊の貼り付けで作る(追記ブロック由来のピース)。cap 128(`WordBoundary.DefaultMaxScan`)。expand 1 回 = `WordStart` + `WordEnd`。比較対象として、同じ文字列をファイル読み込み相当(`TextBuffer.FromString`)で作ったバッファでも測る。

- [ ] **Step 1: 使い捨てベンチを scratchpad に作る**

`<scratchpad>/wordbench/wordbench.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="<repo>\src\kxEdit.Core\kxEdit.Core.csproj" />
  </ItemGroup>
</Project>
```

(`TargetFramework` は `src/kxEdit.Core/kxEdit.Core.csproj` に合わせる。)

`<scratchpad>/wordbench/Program.cs`:

```csharp
using System.Diagnostics;
using kxEdit.Core.Buffers;
using kxEdit.Core.Editing;

const int Chars = 500_000;
const int PasteChars = 32 * 1024; // ASCII なので 32KB = LargeInsertBytes ちょうど(追記ブロックに入る)
string line = new('a', Chars);

var pasted = TextBuffer.FromString("");
for (int off = 0; off < Chars; off += PasteChars)
    pasted.Insert(pasted.Current.CharLength, line.Substring(off, Math.Min(PasteChars, Chars - off)));
var loaded = TextBuffer.FromString(line);

int cap = WordBoundary.DefaultMaxScan;
foreach (var (name, buf) in new[] { ("loaded", loaded), ("pasted", pasted) })
{
    var snap = buf.Current;
    // 位置: 中央付近のブロック 1 つ(64KB)を 64 字刻みで全位相
    int baseLo = 5 * 65536;
    var samples = new List<double>();
    for (int w = 0; w < 3; w++) // ウォームアップ
        WordBoundary.WordEnd(snap, WordBoundary.WordStart(snap, baseLo + 100, cap), cap);
    for (int p = baseLo; p < baseLo + 65536; p += 64)
    {
        var sw = Stopwatch.StartNew();
        int s = WordBoundary.WordStart(snap, p, cap);
        WordBoundary.WordEnd(snap, s, cap);
        sw.Stop();
        samples.Add(sw.Elapsed.TotalMilliseconds);
    }
    samples.Sort();
    Console.WriteLine(
        $"{name}: pieces={snap.PieceCount} median={samples[samples.Count / 2]:F3} ms  max={samples[^1]:F3} ms"
    );
}
```

(`PieceCount` は internal。参照できなければその列を削る。`WordStart`/`WordEnd` のシグネチャは `WordBoundary.cs:360,396` を見て合わせる。)

- [ ] **Step 2: 較正(旧実装で 22.5 ms 前後が再現するか)**

方法が当時の条件を再現していることを、元の設計書フェーズ 4 の前(PR #90 のマージの第 1 親 `14ca6925^1`)で確かめる:

```powershell
git worktree add <scratchpad>\old-core 14ca6925^1
# wordbench.csproj の ProjectReference を <scratchpad>\old-core\src\kxEdit.Core\kxEdit.Core.csproj に向けて実行 ×3
git worktree remove <scratchpad>\old-core
```

旧実装の `pasted` の max が 22.5 ms のオーダーで、`loaded` の約 17 倍になれば方法は妥当。大きく外れる場合は、塊の大きさ・位置の刻みを見直し、何を変えたかを記録する。

- [ ] **Step 3: 現行(本ブランチ)で 3 回走らせ、中央値を取る**

```powershell
dotnet run -c Release --project <scratchpad>\wordbench
```

- [ ] **Step 4: コメントを更新する**

`WordBoundary.cs:172-177` の段落を、実測値で次の形に書き換える(<> は実測値):

```csharp
    /// <b>上の ms は「ファイル読み込み直後のバッファ」の値である。</b> <c>AppendBuffer</c> 由来の
    /// piece(大きめの貼り付け直後など)では、2026-08-04 の時点で同じ expand 1 回が約 17 倍の
    /// 22.5 ms だった(追記ブロックに格子点が先頭の 1 つしかなく、<c>TextChunk.CharToByte</c> が
    /// 最大 64KB を線形走査した。最終レビュー 脆弱性パス V-3 の実測)。2026-09-25 フェーズ 4 で
    /// 追記ブロックにも 4KB ごとの格子点が入り、2026-10-02 の再計測では最悪 <X> ms・中央値 <Y> ms
    /// (ファイル読み込み直後は最悪 <Z> ms)になった。
```

(数値の解釈によって文面を調整してよい。「上限なしは 3,101 ms」「138 倍の改善」の文は 2026-08-04 の記録なので、残すなら日付を添える。)

- [ ] **Step 5: ビルドして commit**

Run: `dotnet build src/kxEdit.Core`
Expected: 0 warning・0 error。

```powershell
git add src/kxEdit.Core/Editing/WordBoundary.cs
git commit -m "docs(editing): 単語境界の上限の実測値コメントを再計測で更新する"
```

---

### 完了処理(タスクの外)

1. 最終レビュー: CLAUDE.md §3 の 5(コード品質パス=変異検証のスポットチェック込み / 脆弱性パス)。製品コードは 3 ファイル・十数行なので、簡略化の基準に沿って 2 パスを別エージェント 1 回に統合してよい。
2. `tools/pre-merge-check.ps1` が EXIT 0。
3. 傘設計書 §9 の末尾に実施記録(§9.4)を追記して同じ PR に含める。PR には `--typing` の前後・元の設計書 §3.5 のフェーズ 4 の挙動差(ピースの増加)が解消したこと・変異検証の結果を書く。
