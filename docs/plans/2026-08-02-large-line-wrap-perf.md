# 巨大 1 行(折り返し ON × 非 ASCII)描画コスト解消 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 折り返し ON × 非 ASCII × 長大行で 1 フレーム約 40 秒かかる描画コストを実用域まで落とし、
併せてその効果を測るための性能ベンチ(`GdiBench`)が実際に描画を測るようにする。

**Architecture:** 独立した 2 つの無駄を別々に潰す。**A** = `GdiCharMetrics` に
コードポイント幅のメモ化を入れて GDI 呼び出しの定数倍を消す。**B** = `LineLayout` に
打ち切り可能な入口 `WrapPrefix` を足し、既存 `Wrap` をその薄いラッパへ書き換えることで
「打ち切り結果は完全結果の prefix」を**構造的に**保証する。適用先は毎フレーム走る
`ViewportLayout.Build` とキャレット計算の 2 箇所に限定する。

**Tech Stack:** C# / .NET 9 / WinForms / xUnit / CSharpier。設計書は
`docs/plans/2026-08-02-large-line-wrap-perf-design.md`。

---

## 事前に読むもの

- `docs/plans/2026-08-02-large-line-wrap-perf-design.md`(本計画の設計書)
- `docs/plans/2026-08-02-large-line-resilience-design.md` §2.1〜§2.3(実測の根拠)
- `CLAUDE.md` §3(開発フロー)/ §4(レビュー標準)/ §5(テスト 5 層)/ §6(品質ゲート)

## 共通コマンド

```powershell
# 全ビルド (警告=エラー)
dotnet build yEdit.sln -c Release -warnaserror

# 個別テスト
dotnet test tests/yEdit.Core.Tests   -c Release --no-build
dotnet test tests/yEdit.Editor.Tests -c Release --no-build
dotnet test tests/yEdit.App.Tests    -c Release --no-build

# 単一テストだけ走らせる
dotnet test tests/yEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~LineLayoutPrefixTests"
```

WinForms を触るテストは `Sta.Run(() => { ... })` で囲む(`tests/yEdit.Editor.Tests/Sta.cs`)。
xUnit v2 は `[Fact]` の STA 化を標準サポートしないため。

---

## Task 1: `GdiBench` を実際に描画させる(N-2)

**Files:**
- Modify: `tests/yEdit.Editor.Smoke/GdiBench.cs:8-13, 36, 46-51`

このタスクだけはテストを書かない。**測定器そのものの修正**であり、成果物は
「修正前後の実測値」という数値である。

### Step 1: 修正前の値を採取する

```powershell
dotnet build tests/yEdit.Editor.Smoke -c Release
dotnet run --project tests/yEdit.Editor.Smoke -c Release --no-build -- --bench
```

出力の `GDI 平均フレーム時間: X ms (max Y ms・1000 frames / 合計 Z s)` と
`判定: PASS/FAIL` を**そのまま記録する**。文書構築に時間がかかるため数分待つこと。

> ⚠ この値は「描画していない」値である。これから直すものの記録であって、正しい値ではない。

### Step 2: `Location` を画面内へ移す

`tests/yEdit.Editor.Smoke/GdiBench.cs:46-51` を次に置き換える。

```csharp
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
```

併せてクラスの XML doc(`:8-13`)の「offscreen Form + Show + Invalidate/Update を」を
「画面内 Form + Show + Invalidate/Update を」へ、`:36` の
「offscreen だが Show でハンドル生成 → Invalidate/Update が同期 paint」を
「Show でハンドル生成 → Invalidate/Update が同期 paint(画面内に置く理由は下記)」へ直す。

### Step 3: 修正後の値を採取する

```powershell
dotnet build tests/yEdit.Editor.Smoke -c Release
dotnet run --project tests/yEdit.Editor.Smoke -c Release --no-build -- --bench
```

Step 1 と同じ形式で記録する。

### Step 4: 16ms 基準の可否を判断する

- **PASS のままなら**: 基準値は変更しない。設計書 §3 に「修正後も PASS(実測 X ms)」を追記する。
- **FAIL に変わったら**: **しきい値を上げて通してはならない。** これは実在の性能事実であり、
  「測っていないのに合格」を「基準を緩めて合格」に置き換えるだけになる。
  実測値と条件を記録し、**ユーザーへ報告して判断を仰ぐ**。
  Task 2・3 の改善後に再測定すると PASS へ戻る可能性があるため、
  判断は Task 5 の最終実測まで保留してよい。

いずれの場合も、設計書 §3 に修正前後の実測値を追記する。

### Step 5: Commit

```powershell
git add tests/yEdit.Editor.Smoke/GdiBench.cs docs/plans/2026-08-02-large-line-wrap-perf-design.md
git commit -m "fix(bench): GdiBench を画面内に置いて実際に描画を測る"
```

コミット本文には修正前後の実測値を必ず含める。

---

## Task 2: `GdiCharMetrics` にコードポイント幅メモ化(変更 A)

**Files:**
- Modify: `src/yEdit.Editor/GdiCharMetrics.cs`
- Test: `tests/yEdit.Editor.Tests/GdiCharMetricsCacheTests.cs`(新規)

### Step 1: 失敗するテストを書く

`tests/yEdit.Editor.Tests/GdiCharMetricsCacheTests.cs` を新規作成する。

```csharp
using yEdit.Editor;

namespace yEdit.Editor.Tests;

/// <summary>
/// 2026-08-02 巨大 1 行対応(変更 A)。GdiCharMetrics のコードポイント幅メモ化が
/// 「同じ MeasureText の結果を返す」= 挙動不変であることを確認する契約テスト。
/// メモ化の効果(速度)は L4(Editor.Smoke --largeline)で測る。
/// 本テストが守るのはキャッシュキーの正しさ = サロゲートペアと単独サロゲートの
/// 取り違え・衝突が起きないことである。
/// </summary>
public class GdiCharMetricsCacheTests
{
    private const TextFormatFlags MeasureFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
    private static readonly Size MaxSize = new(int.MaxValue, int.MaxValue);

    /// <summary>キャッシュを経由しない参照実装(現行 GdiCharMetrics の非 ASCII 経路と同一)。</summary>
    private static int Reference(string s, Font font) =>
        TextRenderer.MeasureText(s, font, MaxSize, MeasureFlags).Width;

    [Theory]
    [InlineData("a")] // ASCII
    [InlineData("\t")] // タブ(ASCII 経路・スペース幅へ読み替え)
    [InlineData("あ")] // BMP CJK
    [InlineData("漢")] // BMP CJK
    [InlineData("😀")] // astral(サロゲートペア)
    [InlineData("\uD83D")] // 単独 high サロゲート(不正 UTF-16)
    [InlineData("\uDE00")] // 単独 low サロゲート(不正 UTF-16)
    public void MeasureRun_single_codepoint_matches_uncached_reference(string cp) =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);

            int first = m.MeasureRun(cp);
            int second = m.MeasureRun(cp); // キャッシュヒット経路

            Assert.Equal(second, first); // 何度呼んでも同じ
            if (cp[0] >= 128)
                Assert.Equal(Reference(cp, font), first);
        });

    [Fact]
    public void Surrogate_pair_and_lone_high_surrogate_do_not_share_a_cache_entry() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);

            // 先に単独 high サロゲートを測って、そのキーがペアを汚染しないことを見る。
            // (naive な「先頭 char をキーにする」実装はここで落ちる)
            int lone = m.MeasureRun("\uD83D");
            int pair = m.MeasureRun("😀");

            Assert.Equal(Reference("\uD83D", font), lone);
            Assert.Equal(Reference("😀", font), pair);
        });

    [Fact]
    public void Multi_codepoint_run_still_goes_through_MeasureText_as_a_whole() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);

            // 複数コードポイントの run は一括計測の既存挙動を維持する
            // (コードポイント幅の和と一致するとは限らないため、キャッシュを使ってはならない)。
            Assert.Equal(Reference("あいうえお", font), m.MeasureRun("あいうえお"));
            Assert.Equal(Reference("あa", font), m.MeasureRun("あa"));
        });
}
```

### Step 2: テストが失敗することを確認する

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Editor.Tests -c Release --no-build --filter "FullyQualifiedName~GdiCharMetricsCacheTests"
```

Expected: この時点では **PASS する**(まだキャッシュを入れていないため参照実装と同じ経路)。
これは意図どおりで、**このテストは「メモ化を入れても壊れないこと」を守る回帰テスト**である。
先に緑を確認しておくことで、Step 3 の後に赤くなったらメモ化のバグだと断定できる。

### Step 3: メモ化を実装する

`src/yEdit.Editor/GdiCharMetrics.cs` を次の内容に置き換える。

```csharp
using yEdit.Core.Layout;
using yEdit.Core.Text;

namespace yEdit.Editor;

/// <summary>
/// TextRenderer(GDI)ベースの <see cref="ICharMetrics"/> 実装(UI スレッド専用)。
/// ASCII(0..127)の 1 文字幅を構築時に前計算してキャッシュし、ホットパス(<see cref="MeasureRun"/> は
/// 1000 文字行なら 1000 回呼ばれる)ではキャッシュ加算で完結させる。カーニングは無視する。
/// TAB は半角スペース幅として扱う(タブ揃えの本実装は入力側 P3 に配置)。
/// 非 ASCII の 1 コードポイントは初回だけ GDI で測り、以後はメモ化した値を返す(下記)。
/// </summary>
/// <remarks>
/// <b>スレッド安全性(重要)</b>: 本クラスは <b>UI スレッド専用</b>である。
/// <see cref="_nonAsciiWidths"/> は非スレッドセーフな <see cref="Dictionary{TKey,TValue}"/> で、
/// 複数スレッドから同時に書かれると値がずれるのではなく<b>構造が壊れる</b>(無限ループ・例外)。
/// UIA RPC スレッドからは <c>UiaTextHostAdapter</c> が <see cref="Control.Invoke(Delegate)"/> で
/// UI スレッドへマーシャリングしてから呼ぶ(<c>UiaTextHostAdapter.cs</c> の
/// <c>TryFindVisualSegment</c> / <c>GetVisibleRange</c>)。この契約を崩さないこと。
/// </remarks>
public sealed class GdiCharMetrics : ICharMetrics
{
    private static readonly Size MaxSize = new(int.MaxValue, int.MaxValue);
    private const TextFormatFlags MeasureFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    private readonly Font _font;
    private readonly int[] _asciiWidths;

    // 2026-08-02 変更 A: 非 ASCII 1 コードポイントの幅メモ化。
    // LineLayout.Wrap は 1 コードポイントずつ MeasureRun を呼ぶため(LineLayout.cs)、
    // 長大行では同じ文字種を何度も GDI で測り直していた(CJK 500K 文字で約 40 秒 =
    // docs/plans/2026-08-02-large-line-resilience-design.md §2.3)。
    // 格納するのは MeasureText の結果そのもの = 返す値は不変。
    //
    // 無効化は不要: 本クラスはフォント単位で生成され、フォント変更時は
    // インスタンスごと差し替えられる(EditorControl の初期化と ApplyAppearance)。
    // = キャッシュの寿命がフォントの寿命と一致する。
    //
    // 初回コストは文書長ではなく「異なるコードポイント数」で決まる(日本語なら 2,000 種程度)。
    // エントリ数は文書に現れるコードポイント数で有界。
    private readonly Dictionary<int, int> _nonAsciiWidths = new();

    public GdiCharMetrics(Font font)
    {
        ArgumentNullException.ThrowIfNull(font);
        _font = font;
        LineHeightPx = TextRenderer.MeasureText("Mg", font, MaxSize, MeasureFlags).Height;
        _asciiWidths = new int[128];
        for (int c = 0; c < 128; c++)
        {
            _asciiWidths[c] = TextRenderer
                .MeasureText(((char)c).ToString(), font, MaxSize, MeasureFlags)
                .Width;
        }
        _asciiWidths['\t'] = _asciiWidths[' '];
    }

    public int LineHeightPx { get; }

    public int MeasureRun(ReadOnlySpan<char> text)
    {
        // ホットパス: 非 ASCII の単一コードポイント(= LineLayout.Wrap の呼び方)はメモ化で返す。
        // 複数コードポイントの run は一括 MeasureText の既存挙動を維持する
        // (run 全体の計測結果はコードポイント幅の和と一致するとは限らないため)。
        if (
            text.Length > 0
            && text[0] >= 128
            && TextBoundary.CodePointLengthAt(text, 0) == text.Length
        )
        {
            return CachedCodePointWidth(text);
        }

        int px = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= 128)
                return TextRenderer
                    .MeasureText(text.ToString(), _font, MaxSize, MeasureFlags)
                    .Width;
            px += _asciiWidths[c];
        }
        return px;
    }

    /// <summary>
    /// 非 ASCII 1 コードポイントの幅をメモ化して返す。
    /// キーは長さ 2 なら UTF-32 コードポイント(&gt;= 0x10000)、長さ 1 ならその char 値
    /// (&lt;= 0xFFFF)。両者は値域が重ならないため、単独サロゲートとサロゲートペアが
    /// 同じエントリを共有することはない。
    /// </summary>
    private int CachedCodePointWidth(ReadOnlySpan<char> cp)
    {
        int key = cp.Length == 2 ? char.ConvertToUtf32(cp[0], cp[1]) : cp[0];
        if (_nonAsciiWidths.TryGetValue(key, out int cached))
            return cached;

        int width = TextRenderer.MeasureText(cp.ToString(), _font, MaxSize, MeasureFlags).Width;
        _nonAsciiWidths[key] = width;
        return width;
    }
}
```

> **なぜ `cp.Length == 2` で `char.ConvertToUtf32` が安全か**: ここへ来るのは
> `TextBoundary.CodePointLengthAt(text, 0) == text.Length` が成り立つときだけで、
> 長さ 2 を返すのは正当なサロゲートペアの場合に限られる。単独サロゲートは
> `CodePointLengthAt` が 1 を返すため `cp[0]` 側へ落ちる。

### Step 4: テストが通ることを確認する

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Editor.Tests -c Release --no-build --filter "FullyQualifiedName~GdiCharMetricsCacheTests"
dotnet test tests/yEdit.Editor.Tests -c Release --no-build
dotnet test tests/yEdit.Core.Tests -c Release --no-build
```

Expected: すべて PASS。0 warning。

### Step 5: 効果を実測する

```powershell
dotnet build tests/yEdit.Editor.Smoke -c Release
dotnet run --project tests/yEdit.Editor.Smoke -c Release --no-build -- --largeline
```

`cjk,500000,80` 行の `setSourceMs` / `paintMsPerFrame` を記録する。改善前は
約 39,820 + 39,837 ms(設計調査 §2.3)。

> ⚠ **現行 fixture では効果が過大に出る。** `LargeLineBench.MakeSingleLine` の `cjk` は
> `(char)('あ' + r.Next(40))` = **異なる文字が 40 種しかない**ため、メモ化の初回コストが
> 実文書より桁違いに小さい。この時点の数値は「上限に近い理想値」として扱い、
> 現実的な文字種数での測定は Task 5 で行う。この注意はコミット本文にも書くこと。

### Step 6: Commit

```powershell
git add src/yEdit.Editor/GdiCharMetrics.cs tests/yEdit.Editor.Tests/GdiCharMetricsCacheTests.cs
git commit -m "perf(editor): GdiCharMetrics にコードポイント幅メモ化を入れる"
```

---

## Task 3: `LineLayout.WrapPrefix` を追加(変更 B-1)

**Files:**
- Modify: `src/yEdit.Core/Layout/LineLayout.cs`
- Test: `tests/yEdit.Core.Tests/Layout/LineLayoutPrefixTests.cs`(新規)

> **CLAUDE.md §3 の前倒し例外に該当する。** 後続の Task 4 が依存する新しい抽象を導入するため、
> このタスク完了時に**コード品質レビュー**を別エージェントで実施すること。

### Step 1: 失敗するテストを書く

`tests/yEdit.Core.Tests/Layout/LineLayoutPrefixTests.cs` を新規作成する。

```csharp
using yEdit.Core.Layout;

namespace yEdit.Core.Tests.Layout;

/// <summary>
/// 2026-08-02 巨大 1 行対応(変更 B)。LineLayout.WrapPrefix の契約テスト。
/// 守る性質は 2 つ。
/// (1) 打ち切り結果は完全な Wrap 結果の prefix と厳密に一致する
/// (2) ReachedLineEnd は「打ち切っていない」と同値である
/// Wrap は左から右への貪欲な走査でセグメント境界が先行内容だけで決まるため
/// (1) が成り立つ。これが ViewportLayout / ComputeCaretPoint の挙動不変の根拠になる。
/// </summary>
public class LineLayoutPrefixTests
{
    private static MonoCharMetrics M => new(halfWidthPx: 1, lineHeightPx: 10);

    /// <summary>MeasureRun の呼び出し回数を数える decorator。打ち切りが効いていることの直接証拠。</summary>
    private sealed class CountingMetrics(ICharMetrics inner) : ICharMetrics
    {
        private readonly ICharMetrics _inner = inner;

        public int Calls { get; private set; }

        public int LineHeightPx => _inner.LineHeightPx;

        public int MeasureRun(ReadOnlySpan<char> text)
        {
            Calls++;
            return _inner.MeasureRun(text);
        }
    }

    private static string LongAsciiLine(int chars) => new('a', chars);

    [Theory]
    // (minSegments, minCoverOffset)
    [InlineData(1, -1)]
    [InlineData(3, -1)]
    [InlineData(100, -1)]
    [InlineData(0, 0)]
    [InlineData(0, 7)]
    [InlineData(0, 99)]
    [InlineData(2, 25)]
    public void WrapPrefix_result_is_a_strict_prefix_of_full_Wrap(
        int minSegments,
        int minCoverOffset
    )
    {
        var line = LongAsciiLine(100);
        var full = LineLayout.Wrap(line, 10, M);
        var pre = LineLayout.WrapPrefix(line, 10, M, minSegments, minCoverOffset);

        Assert.True(pre.Segments.Count <= full.Count);
        for (int i = 0; i < pre.Segments.Count; i++)
            Assert.Equal(full[i], pre.Segments[i]);

        // 打ち切っていない <=> ReachedLineEnd。末尾セグメントはループ後に足されるため、
        // 早期打ち切り時は必ず full より短くなる。
        Assert.Equal(pre.Segments.Count == full.Count, pre.ReachedLineEnd);
    }

    [Fact]
    public void WrapPrefix_stops_after_minSegments_segments()
    {
        var line = LongAsciiLine(1000);
        var pre = LineLayout.WrapPrefix(line, 10, M, minSegments: 4, minCoverOffset: -1);

        Assert.Equal(4, pre.Segments.Count);
        Assert.False(pre.ReachedLineEnd);
    }

    [Fact]
    public void WrapPrefix_covers_the_requested_offset()
    {
        var line = LongAsciiLine(1000);
        var pre = LineLayout.WrapPrefix(line, 10, M, minSegments: 0, minCoverOffset: 35);

        int covered = 0;
        foreach (var s in pre.Segments)
            covered += s.Length;
        Assert.True(covered > 35, $"covered={covered} は 35 を超えていなければならない");
        Assert.False(pre.ReachedLineEnd);
    }

    [Fact]
    public void WrapPrefix_reaches_line_end_when_the_line_is_shorter_than_requested()
    {
        var line = LongAsciiLine(25); // 幅 10 → 3 セグメント
        var pre = LineLayout.WrapPrefix(line, 10, M, minSegments: 100, minCoverOffset: -1);

        Assert.True(pre.ReachedLineEnd);
        Assert.Equal(LineLayout.Wrap(line, 10, M).Count, pre.Segments.Count);
    }

    [Fact]
    public void WrapPrefix_at_end_of_line_cannot_truncate()
    {
        // 行末キャレット相当。行末まで走らないと minCoverOffset を超えられない
        // = B は効かない(設計書 §2 の非対称性。そこは変更 A が受け持つ)。
        var line = LongAsciiLine(100);
        var pre = LineLayout.WrapPrefix(line, 10, M, minSegments: 0, minCoverOffset: 100);

        Assert.True(pre.ReachedLineEnd);
        Assert.Equal(LineLayout.Wrap(line, 10, M).Count, pre.Segments.Count);
    }

    [Fact]
    public void WrapPrefix_measures_only_what_it_needs()
    {
        // 打ち切りが効いていることの直接証拠。10 万文字の行から 4 セグメントだけ要求する。
        var line = LongAsciiLine(100_000);

        var counting = new CountingMetrics(M);
        _ = LineLayout.WrapPrefix(line, 10, counting, minSegments: 4, minCoverOffset: -1);
        int truncated = counting.Calls;

        var countingFull = new CountingMetrics(M);
        _ = LineLayout.Wrap(line, 10, countingFull);
        int fullCalls = countingFull.Calls;

        Assert.Equal(100_000, fullCalls);
        Assert.True(truncated < 100, $"truncated={truncated} は 100 未満でなければならない");
    }

    [Fact]
    public void Wrap_off_and_empty_line_report_reaching_the_line_end()
    {
        var off = LineLayout.WrapPrefix("abcde", 0, M, minSegments: 0, minCoverOffset: -1);
        Assert.True(off.ReachedLineEnd);
        Assert.Single(off.Segments);

        var empty = LineLayout.WrapPrefix("", 10, M, minSegments: 0, minCoverOffset: -1);
        Assert.True(empty.ReachedLineEnd);
        Assert.Single(empty.Segments);
        Assert.Equal((0, 0), (empty.Segments[0].OffsetInLine, empty.Segments[0].Length));
    }
}
```

> `LineLayout` は `internal` だが、`yEdit.Core.Tests` からは既存の
> `LineLayoutTests.cs` が直接叩けているため `InternalsVisibleTo` は設定済み。追加作業は不要。

### Step 2: テストが失敗することを確認する

```powershell
dotnet build yEdit.sln -c Release -warnaserror
```

Expected: **ビルドエラー** — `LineLayout.WrapPrefix` と `WrapResult` が存在しない。

### Step 3: `WrapPrefix` を実装し、`Wrap` をその薄いラッパにする

`src/yEdit.Core/Layout/LineLayout.cs` の `WrapSegment` 宣言の直後に `WrapResult` を足し、
`Wrap` を書き換える。

```csharp
/// <summary>
/// <see cref="LineLayout.WrapPrefix"/> の結果。
/// <paramref name="Segments"/> は完全な <see cref="LineLayout.Wrap"/> 結果の prefix と
/// 厳密に一致する。<paramref name="ReachedLineEnd"/> が false なら打ち切られており、
/// 「最後の要素が論理行の最終セグメントである」とみなしてはならない
/// (EOL キャレット位置の判定がずれる)。
/// </summary>
public readonly record struct WrapResult(
    IReadOnlyList<WrapSegment> Segments,
    bool ReachedLineEnd
);
```

`Wrap` は本体を持たず `WrapPrefix` へ委譲する形にする(**実装を 1 つに保つことで
「prefix である」ことが構造的に保証される**)。

```csharp
    public static IReadOnlyList<WrapSegment> Wrap(
        ReadOnlySpan<char> line,
        int maxWidthPx,
        ICharMetrics metrics
    ) =>
        // minSegments = int.MaxValue = 「どれだけ積んでも足りない」= 打ち切りが起きない。
        // 実装を WrapPrefix 1 本に保つことで、打ち切り結果が完全結果の prefix であることが
        // 構造的に保証される(2 実装間の同期に頼らない)。
        WrapPrefix(line, maxWidthPx, metrics, minSegments: int.MaxValue, minCoverOffset: -1)
            .Segments;

    /// <summary>
    /// <see cref="Wrap"/> と同じ規則で折り返しつつ、要求を満たした時点で走査を打ち切る。
    /// 打ち切り条件は次の<b>両方</b>が満たされたとき(セグメントを閉じた直後にのみ判定する)。
    /// <list type="bullet">
    /// <item>確定済みセグメント数が <paramref name="minSegments"/> 以上
    ///   (0 = 個数の要求なし)</item>
    /// <item>確定済みセグメントが <paramref name="minCoverOffset"/> を<b>超えて</b>カバー
    ///   (-1 = オフセットの要求なし)</item>
    /// </list>
    /// 行末まで到達したら要求に関わらず全セグメントを返し、
    /// <see cref="WrapResult.ReachedLineEnd"/> に true を入れる。
    /// </summary>
    /// <remarks>
    /// 打ち切り結果が完全結果の prefix になるのは、<see cref="Wrap"/> が左から右への
    /// 貪欲な走査で、セグメント境界が<b>先行する内容だけ</b>で決まるためである。
    /// この性質は <c>LineLayoutPrefixTests</c> で検証している。
    /// </remarks>
    public static WrapResult WrapPrefix(
        ReadOnlySpan<char> line,
        int maxWidthPx,
        ICharMetrics metrics,
        int minSegments,
        int minCoverOffset
    )
    {
        // OFF: 単一セグメント
        if (maxWidthPx <= 0)
            return new WrapResult(new[] { new WrapSegment(0, line.Length) }, true);

        // 空行: 高さは持つが幅ゼロの 1 セグメント
        if (line.IsEmpty)
            return new WrapResult(new[] { new WrapSegment(0, 0) }, true);

        var result = new List<WrapSegment>();
        int segStart = 0;
        int segWidth = 0;
        int i = 0;

        while (i < line.Length)
        {
            // 次の code-point を切り出す(サロゲートペアは 2 code-unit 分)
            int cpLen = TextBoundary.CodePointLengthAt(line, i);

            int cpWidth = metrics.MeasureRun(line.Slice(i, cpLen));

            // 累積+今回の幅が max を超えるならセグメントを閉じて新セグメント開始。
            // ただし現セグメントが空(segWidth==0)なら閉じない=強制前進(空セグメント禁止)。
            if (segWidth > 0 && segWidth + cpWidth > maxWidthPx)
            {
                result.Add(new WrapSegment(segStart, i - segStart));
                segStart = i;
                segWidth = 0;

                // 打ち切り判定はセグメントを閉じた直後だけ。
                // このとき segStart = 確定済みセグメントがカバーし終えた char 数。
                if (result.Count >= minSegments && segStart > minCoverOffset)
                    return new WrapResult(result, false);
            }

            // code-point を現セグメントに加える
            segWidth += cpWidth;
            i += cpLen;
        }

        // 末尾セグメント
        result.Add(new WrapSegment(segStart, line.Length - segStart));
        return new WrapResult(result, true);
    }
```

`Wrap` の既存 XML doc(空入力の契約など)はそのまま残す。

### Step 4: テストが通ることを確認する

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build
dotnet test tests/yEdit.Editor.Tests -c Release --no-build
dotnet test tests/yEdit.App.Tests -c Release --no-build
```

Expected: 全 PASS。**既存の `LineLayoutTests` 7 件が緑のままであること**が
「`Wrap` の挙動不変」の証拠になる。

### Step 5: Commit

```powershell
git add src/yEdit.Core/Layout/LineLayout.cs tests/yEdit.Core.Tests/Layout/LineLayoutPrefixTests.cs
git commit -m "feat(core): LineLayout に打ち切り可能な WrapPrefix を追加"
```

### Step 6: コード品質レビュー(前倒し・CLAUDE.md §3)

別エージェントでコード品質レビューを実施する。レビュー観点を明示して依頼すること。

- `Wrap` を `WrapPrefix` へ委譲した結果、既存 8 呼び出し元の挙動が本当に変わっていないか
- `minSegments` / `minCoverOffset` のセンチネル(0 / -1)が API として誤用されにくいか
- 打ち切り判定の位置(セグメントを閉じた直後のみ)が Task 4 の 2 つの用途を過不足なく満たすか
- `WrapResult` を struct にしたことによる不都合がないか

---

## Task 4: `ViewportLayout` / `ComputeCaretPoint` へ適用(変更 B-2)

**Files:**
- Modify: `src/yEdit.Core/Layout/ViewportLayout.cs:47-72`
- Modify: `src/yEdit.Editor/EditorControl.cs:1528-1573`
- Test: `tests/yEdit.Core.Tests/Layout/ViewportLayoutPrefixTests.cs`(新規)
- Test: `tests/yEdit.Editor.Tests/EditorControlWrapCaretTests.cs`(新規)

### Step 1: 失敗するテストを書く(Core 側)

`tests/yEdit.Core.Tests/Layout/ViewportLayoutPrefixTests.cs` を新規作成する。

```csharp
using yEdit.Core.Buffers;
using yEdit.Core.Layout;

namespace yEdit.Core.Tests.Layout;

/// <summary>
/// 2026-08-02 巨大 1 行対応(変更 B-2)。ViewportLayout.Build が
/// 「可視分だけ Wrap する」ようになったことを、MeasureRun の呼び出し回数で直接検証する。
/// 併せて返す VisualRow が変更前と同一であること(挙動不変)も確認する。
/// </summary>
public class ViewportLayoutPrefixTests
{
    private static MonoCharMetrics M => new(halfWidthPx: 1, lineHeightPx: 10);

    private sealed class CountingMetrics(ICharMetrics inner) : ICharMetrics
    {
        private readonly ICharMetrics _inner = inner;

        public int Calls { get; private set; }

        public int LineHeightPx => _inner.LineHeightPx;

        public int MeasureRun(ReadOnlySpan<char> text)
        {
            Calls++;
            return _inner.MeasureRun(text);
        }
    }

    [Fact]
    public void Build_over_a_huge_single_line_measures_only_the_visible_part()
    {
        // 空白・改行を含まない 10 万文字の 1 行 = 調査で問題になった形状。
        var snap = TextBuffer.FromString(new string('a', 100_000)).Current;
        var counting = new CountingMetrics(M);

        // wrapColumns=10 / 可視高さ = 10px * 40 行
        var rows = ViewportLayout.Build(snap, 0, heightPx: 400, wrapColumns: 10, counting);

        Assert.Equal(40, rows.Count);
        // 可視 40 行 × 10 文字 = 400 文字ぶん + maxWidthPx 算出の 1 回 + 余裕。
        // 10 万文字を舐めていたら 100,000 を超えるので、ここで確実に落ちる。
        Assert.True(counting.Calls < 1_000, $"Calls={counting.Calls} は 1,000 未満のはず");
    }

    [Fact]
    public void Build_returns_the_same_rows_as_a_full_Wrap_would()
    {
        var snap = TextBuffer.FromString(new string('a', 100_000)).Current;
        var rows = ViewportLayout.Build(snap, 0, heightPx: 400, wrapColumns: 10, M);

        // 参照: 論理行全体を Wrap した結果の先頭 40 セグメントと一致しなければならない。
        var full = LineLayout.Wrap(new string('a', 100_000), 10 * M.MeasureRun("0"), M);
        Assert.Equal(40, rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(full[i].OffsetInLine, rows[i].SegmentStartChar);
            Assert.Equal(full[i].Length, rows[i].SegmentLength);
            Assert.Equal(i, rows[i].SegmentIndex);
            Assert.Equal(i * M.LineHeightPx, rows[i].YPx);
        }
    }

    [Fact]
    public void Build_across_multiple_logical_lines_is_unchanged()
    {
        // 複数論理行に跨るとき、打ち切りが次の論理行の扱いを壊さないことの確認。
        var snap = TextBuffer.FromString("aaaaaaaaaaaaaaa\nbbbbb\nccccccccccccccc").Current;
        var rows = ViewportLayout.Build(snap, 0, heightPx: 1000, wrapColumns: 10, M);

        // 行 0: 15 文字 → 2 seg / 行 1: 5 文字 → 1 seg / 行 2: 15 文字 → 2 seg
        Assert.Equal(5, rows.Count);
        Assert.Equal((0, 0), (rows[0].LogicalLine, rows[0].SegmentIndex));
        Assert.Equal((0, 1), (rows[1].LogicalLine, rows[1].SegmentIndex));
        Assert.Equal((1, 0), (rows[2].LogicalLine, rows[2].SegmentIndex));
        Assert.Equal((2, 0), (rows[3].LogicalLine, rows[3].SegmentIndex));
        Assert.Equal((2, 1), (rows[4].LogicalLine, rows[4].SegmentIndex));
    }
}
```

### Step 2: テストが失敗することを確認する

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~ViewportLayoutPrefixTests"
```

Expected: `Build_over_a_huge_single_line_measures_only_the_visible_part` が **FAIL**
(`Calls=100001` 程度)。他 2 件は PASS(現状の挙動を固定するテストのため)。

### Step 3: `ViewportLayout.Build` に打ち切りを適用する

`src/yEdit.Core/Layout/ViewportLayout.cs:47-72` のループ本体を置き換える。

```csharp
        int y = 0;
        for (int line = topLine; line < snapshot.LineCount; line++)
        {
            int lineStart = snapshot.GetLineStart(line);
            int lineEndNoBreak = snapshot.GetLineEnd(line, includeBreak: false);
            int lineLen = lineEndNoBreak - lineStart;
            string lineText = lineLen == 0 ? string.Empty : snapshot.GetText(lineStart, lineLen);

            // 2026-08-02 変更 B: この論理行から実際に使える視覚行数だけ Wrap する。
            // 以前は論理行全体を Wrap してから可視分だけ使っていたため、巨大 1 行では
            // 1 フレームあたり行全体を舐めていた(docs/plans/2026-08-02-large-line-resilience-design.md §2.3)。
            // 打ち切り結果は完全結果の prefix なので、返す VisualRow は変わらない。
            int rowsNeeded =
                lineHeight > 0 ? (heightPx - y + lineHeight - 1) / lineHeight : int.MaxValue;
            var segments = LineLayout
                .WrapPrefix(
                    lineText,
                    maxWidthPx,
                    metrics,
                    minSegments: Math.Max(1, rowsNeeded),
                    minCoverOffset: -1
                )
                .Segments;

            for (int si = 0; si < segments.Count; si++)
            {
                if (y >= heightPx)
                    return result;
                var seg = segments[si];
                result.Add(
                    new VisualRow(
                        LogicalLine: line,
                        SegmentIndex: si,
                        SegmentStartChar: lineStart + seg.OffsetInLine,
                        SegmentLength: seg.Length,
                        YPx: y
                    )
                );
                y += lineHeight;
            }
        }
        return result;
```

> **なぜ挙動が変わらないか**: 内側ループは `y >= heightPx` になったら即 return するため、
> 1 論理行から使うのは高々 `ceil((heightPx - y) / lineHeight)` 行。それより多く
> Wrap しても捨てるだけである。打ち切って足りなくなった場合でも `y >= heightPx` に
> 達しているため、次の論理行の先頭で return する(`Wrap` は必ず 1 個以上返す契約)。

### Step 4: Core 側のテストが通ることを確認する

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build
```

Expected: 全 PASS。

### Step 5: 失敗するテストを書く(Editor 側 = EOL キャレットの罠)

`tests/yEdit.Editor.Tests/EditorControlWrapCaretTests.cs` を新規作成する。

```csharp
using yEdit.Core.Buffers;
using yEdit.Editor;

namespace yEdit.Editor.Tests;

/// <summary>
/// 2026-08-02 巨大 1 行対応(変更 B-2)。ComputeCaretPoint が Wrap を打ち切るように
/// なったことで、EOL キャレット(最終セグメント末尾ちょうど)の判定が壊れていないことを守る。
/// 打ち切ると「返された最後のセグメント」が論理行の最終セグメントとは限らなくなるため、
/// ReachedLineEnd を見ずに実装すると行末キャレットが 1 行下(または左端)へずれる。
/// </summary>
public class EditorControlWrapCaretTests
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

    [Fact]
    public void Caret_at_end_of_a_wrapped_line_stays_on_the_last_visual_row() =>
        Sta.Run(() =>
        {
            // 折り返し 10 桁・35 文字 → 4 視覚行。末尾キャレット(offset=35)は
            // 4 行目(最終セグメント)の末尾に来なければならない。
            var (f, c) = MakeControl(new string('a', 35), 10);
            using (f)
            using (c)
            {
                var pEnd = c.PointFromCharOffset(35);
                var pLastRowStart = c.PointFromCharOffset(30);

                // 同じ視覚行にいる = Y が一致する
                Assert.Equal(pLastRowStart.Y, pEnd.Y);
                // 行頭より右にいる
                Assert.True(pEnd.X > pLastRowStart.X, $"pEnd.X={pEnd.X} > {pLastRowStart.X}");
            }
        });

    [Fact]
    public void Caret_at_a_segment_boundary_is_on_the_next_visual_row() =>
        Sta.Run(() =>
        {
            // セグメント境界ちょうど(offset=10)は「次の視覚行の先頭」であって
            // 「前の視覚行の末尾」ではない(最終セグメント以外は末尾ちょうどを許容しない)。
            var (f, c) = MakeControl(new string('a', 35), 10);
            using (f)
            using (c)
            {
                var pRow0 = c.PointFromCharOffset(0);
                var pBoundary = c.PointFromCharOffset(10);

                Assert.True(
                    pBoundary.Y > pRow0.Y,
                    $"pBoundary.Y={pBoundary.Y} は pRow0.Y={pRow0.Y} より下のはず"
                );
                Assert.Equal(pRow0.X, pBoundary.X); // 行頭
            }
        });

    [Fact]
    public void Caret_positions_are_unchanged_across_a_long_wrapped_line() =>
        Sta.Run(() =>
        {
            // 打ち切りが効く長さで、各視覚行の先頭が等間隔に下がることを確認する。
            var (f, c) = MakeControl(new string('a', 1000), 10);
            using (f)
            using (c)
            {
                var p0 = c.PointFromCharOffset(0);
                var p1 = c.PointFromCharOffset(10);
                var p2 = c.PointFromCharOffset(20);

                Assert.Equal(p0.X, p1.X);
                Assert.Equal(p0.X, p2.X);
                Assert.Equal(p1.Y - p0.Y, p2.Y - p1.Y); // 等間隔 = 行高
                Assert.True(p1.Y > p0.Y);
            }
        });
}
```

> `HostForm` は `tests/yEdit.Editor.Tests/TestHost.cs` にある既存ヘルパ
> (`EditorControlCacheTests` が同じ形で使っている)。

### Step 6: テストが通ること(=現状維持)を確認する

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Editor.Tests -c Release --no-build --filter "FullyQualifiedName~EditorControlWrapCaretTests"
```

Expected: **全 PASS**。まだ `ComputeCaretPoint` を変えていないため、
これは「変更前の正しい挙動」を固定するテストである。緑を確認してから Step 7 へ進む。

### Step 7: `ComputeCaretPoint` に打ち切りを適用する

`src/yEdit.Editor/EditorControl.cs:1528-1550` を次に置き換える。

```csharp
        int lineStart = snap.GetLineStart(logicalLine);
        int lineEnd = snap.GetLineEnd(logicalLine, includeBreak: false);
        int lineLen = lineEnd - lineStart;
        string lineText = lineLen == 0 ? string.Empty : snap.GetText(lineStart, lineLen);
        int maxWidthPx = _wrapColumns > 0 ? _wrapColumns * _metrics.MeasureRun("0") : 0;
        int caretInLine = offset - lineStart;

        // 2026-08-02 変更 B: キャレットを含むセグメントが確定した時点で Wrap を打ち切る。
        // 行末キャレットのときは打ち切れない(行末まで走らないと含むセグメントが決まらない)が、
        // その場合のコストは変更 A(GdiCharMetrics のメモ化)が受け持つ。
        var wrapped = LineLayout.WrapPrefix(
            lineText,
            maxWidthPx,
            _metrics,
            minSegments: 0,
            minCoverOffset: caretInLine
        );
        var segments = wrapped.Segments;

        // 対象がどの視覚セグメントに属するかを決める。
        // - 通常は「seg.OffsetInLine + seg.Length で終わる直前」まで
        // - 最終セグメントに限り「末尾ちょうど」も許容(EOL キャレット位置)
        // 打ち切られている(ReachedLineEnd=false)ときは、最後の要素は論理行の
        // 最終セグメントではないため「末尾ちょうど」を許容してはならない。
        int segIdx = segments.Count - 1;
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            int segEnd = seg.OffsetInLine + seg.Length;
            if (
                caretInLine < segEnd
                || (wrapped.ReachedLineEnd && i == segments.Count - 1 && caretInLine == segEnd)
            )
            {
                segIdx = i;
                break;
            }
        }
```

次に `:1559-1572` の積み上げループを置き換える。

```csharp
        // TopLine の先頭視覚行を Y=0 として、対象視覚行までの積み上げ視覚行数を算出。
        // paintHeight を超えたら以降の Wrap は無駄なので早期退避(Task 10 I-1)。
        // 2026-08-02 変更 B: 各行についても「まだ意味のある視覚行数」までで Wrap を打ち切る。
        // 打ち切って上限に達した場合、その行だけで paintHeight を超えるため判定結果は同じ。
        int visualRowsBeforeThisLine = 0;
        int maxUsefulRows = lineHeight > 0 ? (paintHeight / lineHeight) + 1 : int.MaxValue;
        for (int line = _topLine; line < logicalLine; line++)
        {
            int lStart = snap.GetLineStart(line);
            int lEnd = snap.GetLineEnd(line, includeBreak: false);
            int lLen = lEnd - lStart;
            string lText = lLen == 0 ? string.Empty : snap.GetText(lStart, lLen);
            var segs = LineLayout
                .WrapPrefix(
                    lText,
                    maxWidthPx,
                    _metrics,
                    minSegments: Math.Max(1, maxUsefulRows - visualRowsBeforeThisLine),
                    minCoverOffset: -1
                )
                .Segments;
            visualRowsBeforeThisLine += segs.Count;
            if (visualRowsBeforeThisLine * lineHeight >= paintHeight)
                return (0, 0, false);
        }
```

> `lineHeight` はこの時点で既に `_metrics.LineHeightPx` から取得済み(`:1556`)。
> `maxUsefulRows` の定義 `paintHeight / lineHeight + 1` により
> `maxUsefulRows * lineHeight > paintHeight` が常に成り立つため、
> 打ち切って上限に達したケースは必ず早期退避の分岐に入る = 打ち切らない場合と同じ結果になる。

`:1507-1512` の `<remarks>` にある「TopLine ～ 対象行までの各論理行に対して `LineLayout.Wrap`
を呼び直す」という記述も、打ち切りを使う旨に更新する。

### Step 8: すべてのテストが通ることを確認する

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests   -c Release --no-build
dotnet test tests/yEdit.Editor.Tests -c Release --no-build
dotnet test tests/yEdit.App.Tests    -c Release --no-build
```

Expected: 全 PASS・0 warning。とくに `EditorControlWrapCaretTests` 3 件と
既存の `CaretScrollTests` / `KeyboardNavigationTests` / `UiaScrollIntoViewTests` が
緑であることを確認する(キャレット位置は広範囲に影響するため)。

### Step 9: Commit

```powershell
git add src/yEdit.Core/Layout/ViewportLayout.cs src/yEdit.Editor/EditorControl.cs `
        tests/yEdit.Core.Tests/Layout/ViewportLayoutPrefixTests.cs `
        tests/yEdit.Editor.Tests/EditorControlWrapCaretTests.cs
git commit -m "perf(editor): 描画とキャレット計算で Wrap を可視分に打ち切る"
```

---

## Task 5: ベンチ fixture を現実的にして最終実測

**Files:**
- Modify: `tests/yEdit.Editor.Smoke/LargeLineBench.cs:69-78, 123-142`
- Modify: `tests/yEdit.Core.Bench`(`--largeline` の fixture 生成・同一規則を保つ)

### Step 1: fixture の問題を理解する

現行の `MakeSingleLine` は次のとおりで、**`cjk` の異なる文字が 40 種しかない**。

```csharp
"cjk" => (char)('あ' + r.Next(40)),
```

変更 A(メモ化)の初回コストは「異なるコードポイント数」で決まるため、
この fixture では効果が過大に出る。加えて調査 §4 N-1 が起票条件として
**サロゲートペアを含む行が測られていない**ことを指摘している。

### Step 2: 既存 fixture を残したまま kind を追加する

**既存の `ascii` / `cjk` / `mixed` は変えない**(§2.3 の測定値との前後比較が
できなくなるため)。新しい kind を 2 つ足す。

`tests/yEdit.Editor.Smoke/LargeLineBench.cs` の `MakeSingleLine` を次に置き換える。

```csharp
    /// <summary>
    /// 空白も改行も含まない 1 行を作る(Core.Bench --largeline と同一の生成規則・同一シード)。
    /// </summary>
    /// <remarks>
    /// kind の意味:
    /// <list type="bullet">
    /// <item><c>ascii</c> / <c>cjk</c> / <c>mixed</c> — 2026-08-02 調査時と同一の生成規則。
    ///   前後比較のため変更しないこと。<c>cjk</c> は異なる文字が 40 種しかない点に注意
    ///   (幅メモ化の初回コストが実文書より小さく出る)</item>
    /// <item><c>cjkwide</c> — 常用漢字域から広く採る = 異なる文字が約 2,000 種。
    ///   日本語の実文書に近い文字種数で幅メモ化の初回コストを測るために追加した</item>
    /// <item><c>emoji</c> — サロゲートペア(astral)を含む。LineLayout の
    ///   CodePointLengthAt 経路と幅キャッシュの UTF-32 キー経路を踏む</item>
    /// </list>
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
                    // CJK 統合漢字 U+4E00〜U+55FF から 2,048 種
                    sb.Append((char)(0x4E00 + r.Next(2048)));
                    break;
                case "emoji":
                    // U+1F600〜U+1F64F(顔文字ブロック)= サロゲートペア 80 種
                    sb.Append(char.ConvertFromUtf32(0x1F600 + r.Next(80)));
                    break;
                default: // mixed
                    sb.Append(
                        r.Next(2) == 0 ? (char)('a' + r.Next(26)) : (char)('あ' + r.Next(40))
                    );
                    break;
            }
        }
        return sb.ToString(0, chars);
    }
```

`:69` の kind ループへ新 kind を足す。

```csharp
        foreach (string kind in new[] { "ascii", "cjk", "mixed", "cjkwide", "emoji" })
```

> `emoji` は 1 コードポイント = 2 char のため、`chars` は char 数であって
> コードポイント数ではない。`sb.ToString(0, chars)` がサロゲートペアの中間で切る
> 可能性があるが、`LineLayout` は単独サロゲートを 1 code-unit として扱う契約
> (`LineLayoutTests.Lone_high_surrogate_is_treated_as_single_code_unit`)なので
> 測定は成立する。**この点はコミット本文に明記すること。**

`tests/yEdit.Core.Bench` の `--largeline` にも同一の生成規則を適用する
(2 つのベンチは「同一の生成規則・同一シード」であることが対の前提)。

### Step 3: 最終実測

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet build tests/yEdit.Editor.Smoke -c Release
dotnet run --project tests/yEdit.Editor.Smoke -c Release --no-build -- --largeline
dotnet run --project tests/yEdit.Core.Bench -c Release --no-build -- --largeline
dotnet run --project tests/yEdit.Editor.Smoke -c Release --no-build -- --bench
```

記録するもの:

| 条件 | 改善前(調査 §2.3) | 改善後 |
|---|---|---|
| `cjk` 500K / wrap 80 | 39,820 + 39,837 ms | ? |
| `mixed` 500K / wrap 80 | 19,847 + 20,064 ms | ? |
| `cjkwide` 500K / wrap 80 | (未測定) | ? |
| `emoji` 500K / wrap 80 | (未測定) | ? |
| `GdiBench` 平均フレーム | Task 1 で採取 | ? |

### Step 4: 設計書へ実測を追記して Commit

`docs/plans/2026-08-02-large-line-wrap-perf-design.md` に §9「実施記録」を新設し、
上表と Task 1 の前後値、16ms 基準の最終判断を書く。

```powershell
git add tests/yEdit.Editor.Smoke/LargeLineBench.cs tests/yEdit.Core.Bench `
        docs/plans/2026-08-02-large-line-wrap-perf-design.md
git commit -m "bench: fixture に現実的な文字種数とサロゲートを足して最終実測を記録"
```

---

## Task 6: 最終ブランチレビュー → 品質ゲート → PR

### Step 1: 最終ブランチレビュー(2 パス・CLAUDE.md §3-5)

**パスごとに独立した別エージェントを起動する**(1 起動に混載しない)。

**コード品質パス** — 観点:
- `Wrap` → `WrapPrefix` 委譲で既存 8 呼び出し元の挙動が本当に不変か
- `ComputeCaretPoint` の `ReachedLineEnd` 判定に漏れがないか(EOL / セグメント境界 / 空行)
- `maxUsefulRows` / `rowsNeeded` の境界計算(`paintHeight` が 0・`lineHeight` が 0)
- **ミューテーション検証のスポットチェック**(CLAUDE.md §4):
  - `LineLayout.WrapPrefix` の `segStart > minCoverOffset` を `>=` に変異 →
    `LineLayoutPrefixTests.WrapPrefix_covers_the_requested_offset` が赤くなること
  - `ComputeCaretPoint` の `wrapped.ReachedLineEnd &&` を削除 →
    `EditorControlWrapCaretTests.Caret_at_end_of_a_wrapped_line_stays_on_the_last_visual_row`
    が赤くなること
  - `ViewportLayout` の `Math.Max(1, rowsNeeded)` を `rowsNeeded` に変異 →
    `ViewportLayoutPrefixTests` のいずれかが赤くなること
  - 変異後は必ず `--no-build` を外して再ビルドする(変異バイナリを誤認しないため)
  - 確認後は必ず復元する

**脆弱性パス** — 観点:
- `char.ConvertToUtf32` が不正な入力で例外を投げる経路が残っていないか
- `Dictionary` キャッシュが非 UI スレッドから触られる経路が本当に無いか
  (`UiaTextHostAdapter` の `Invoke` 漏れ)
- キャッシュのエントリ数が入力で無制限に膨らむ経路がないか

指摘は CLAUDE.md §4 の 3 択(fixup commit / PR description に記載して受容 / 理由付き却下)で
明示する。修正は元 commit を書き換えず **別 fixup commit** で積む。

### Step 2: 品質ゲート

```powershell
pwsh tools/pre-merge-check.ps1
```

Expected: **EXIT 0**、0 warning。

### Step 3: L5 実機 SR 検証(ユーザー)

**必要と判定済み**(設計書 §6)。`ComputeCaretPoint` は UIA の
`ComputeBoundingRectangles` / `ComputeOffsetFromScreenPoint` から呼ばれるため。

保留中の **N-3 / N-4 / N-5 実機セッションに相乗り**させる。確認項目:

1. 折り返し ON で日本語長文を開き、キャレット移動が SR で正しく読まれるか
2. 折り返し ON × 長大 1 行(日本語)を開き、実用的な速度になっているか
3. 行末・視覚行境界でのキャレット位置が視覚的にずれていないか
4. (相乗り)N-5: §9.8 の 240 秒を **NVDA 起動状態**・折り返し ON/OFF 両方で再試行
5. (相乗り)N-3: F-5 の SR 接続下での実挙動
6. (相乗り)N-4: F-3 の実害採取

### Step 4: PR 作成

```powershell
git push -u origin feature/large-line-wrap-perf
gh pr create --title "perf: 巨大 1 行(折り返し ON × 非 ASCII)の描画コストを解消" --body "..."
```

PR description は日本語で、**目的・レビュー経緯・申し送り**を記載する(CLAUDE.md §7)。
設計書 §8 の申し送りを転記する。

---

## 申し送り(この計画の範囲外)

設計書 §8 のとおり。要約:

- `GdiBench` を自動ゲート(`bench.yml` / `pre-merge-check.ps1`)へ接続するか
- 変更 B の適用先拡大(`VerticalNavigation` / `NavigationCommands` / `Input` の 6 箇所)
- `UiaTextHostAdapter._lastLineSegs` は完全リスト前提のため SR 経路は行全体を Wrap し続ける
- N-3 / N-4 / N-5(実機 NVDA セッション)
- F-1 / F-2 / F-3 / F-4 / F-5 / F-6
