# 長大行の描画コスト(long-line-paint-cost) 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 傘のフェーズ 9 を完了させる。17・19 を閉じ、18 を記録する。調査で分かった長い行の 2 つの問題を、設計書 `2026-10-06-long-row-geometry-design.md` に従って直す。(1) 非 ASCII の 43,679 字を超える行で座標が 0 になる。(2) 長大行で 1 打鍵ごとに全区切りを測って描いている。

**Architecture:** 16,384 字を超える視覚行(長い行)に限り、X をコードポイント幅の足し算(`ICharMetrics.MeasureAdditive`)で求める。切り替えは `PixelMapper` の中で行うので、キャレット・選択・マウス・UIA は呼び出し元を変えずに直る。`FrameBuilder.Build` は横の窓を受け取り、長い行では窓にかかる文字だけを本文と空白のグリフにする。短い行の座標と op 列は変えない。

**Tech Stack:** C# / .NET 9 / WinForms / GDI(TextRenderer) / xUnit

**Spec:** `docs/plans/2026-10-06-long-row-geometry-design.md`(以下「設計書」)。傘は `docs/plans/2026-09-27-perf-followups-design.md` §3・§13(フェーズ 9)。

## 0. 調査の結果(計画の作成時に実施)

傘 §3.1 の「最初のタスクで調査する」を、計画の形を決めるために計画の作成時に前倒しした(フェーズ 8 と同じ形)。

### 0.1 方法

- scratchpad のワークツリー(main `8a43d59c` から detach)で、Smoke `--perf` に S11(Task 1 と同じ形)を足した。計測用の Stopwatch を `TextSnapshot.GetText`・`FrameDiff.Describe`(本文の取り出し)・`FrameDiff.DirtyBands`・`PaintAndRecord`・`InvalidateChangedRows`・`UpdateHorizontalScrollbar` に入れた。製品コードは変えていない。
- 18 の内訳は、main のビルド(計測用の Stopwatch なし)の Smoke を dotnet-trace(`dotnet-sampled-thread-time`)で採った。`PerfBench.TimeOnce` の下の包含時間を、文書ごとの時間の窓(`PrepareAtCaretLine` の開始で区切る)で集計した。
- NVDA 起動中、Release ビルド。

### 0.2 項目 17・19(記述子の本文)

| 文書(S11) | 1 打鍵 | 描画 | 記述子の本文の取り出し | 比 |
|---|---|---|---|---|
| 英字 1M 字の 1 行 | 135 ms | 126 ms | 0.19 ms | 0.14% |
| 日本語 1M 字の 1 行 | 2.7 秒 | 2.7 秒 | 約 1 ms | 0.04% |

- `DirtyBands`(比較)は 0.002 ms。打鍵で行の長さが変わるので、`string.Equals` は長さの比較で終わる。
- 参考: ja10k の S3 では、`Describe` 全体が打鍵の 4.5%、en10k では 3%(dotnet-trace)。
- **結論**: 傘 §13.2 の基準(1 割)を大きく下回るので、**17 は閉じる**。19(古い件が本文を 1 世代長く握る)も閉じる。握るのは可視行の本文 1 部(1M 字の行で 2MB)である。一方、描画の経路は 1 打鍵ごとに行全体の文字列を 6〜7 部作っている(1 打鍵で 1,200〜1,400 万字ぶん)。

### 0.3 項目 18(S1・S3 の固定費の内訳)

`TimeOnce` の下の包含時間(ms・合計)。カッコは `TimeOnce` に対する比。

| | ja S1 | en S1 | ja S3 | en S3 |
|---|---|---|---|---|
| `TimeOnce` | 276 | 626 | 4,121 | 5,064 |
| `RaiseUia`(`RaiseAutomationEvent`) | 159(58%) | 459(73%) | 776(19%) | 1,396(28%) |
| `PositionCaret` | 109(39%) | 163(26%) | 323(8%) | 341(7%) |
| 描画(`OnPaint`) | — | — | 862(21%) | 907(18%) |
| `InvalidateChangedRows`(うち `Describe`) | — | — | 675(184) | 658(154) |
| `UpdateHorizontalScrollbar` | — | — | 648(16%) | 572(11%) |
| `ViewportLayout.Build` | — | — | 400(10%) | 395(8%) |

- S1 は ja・en とも 2,000 回 + ウォームアップ。S3 は挿入と BackSpace の合計 2,000 回 + ウォームアップ。
- **en の固定費の大部分は UIA イベントの発火**だった(NVDA が購読しているため)。ja と en の差も、主にここから来る。S3 では差 943 ms のうち 620 ms、S1 では差 350 ms のうち 300 ms。
- 傘 §13.1 のとおり、記録だけでコードは変えない。

### 0.4 新しく分かったこと(設計書 §1)

- 長大行の打鍵が重いのは描画の本体だった。`TextRenderer.DrawText` が約 6 割、`GdiCharMetrics.MeasureRun`(区切りごとの計測)が 5 割強を占める。
- `TextRenderer.MeasureText` は、43,679 字を超える文字列にエラーを出さずに幅 0 を返す。非 ASCII の長い行では、横スクロールバーが出ない。キャレット・クリック・選択矩形も壊れる(日本語 1M 字の行で、キャレットを行末に置いても `ScrollX` が 0 のままだった)。
- ユーザーの決定: F-1 の案 B を、座標の修正と合わせて本フェーズで行う。設計書は `2026-10-06-long-row-geometry-design.md`(commit `70f61003`)。

### 0.5 設計書からの精密化

- `FrameBuilder.Build` の窓の引数は、**省略可能**(`int viewLeftPx = 0, int viewWidthPx = int.MaxValue`)にする。既定値は「窓なし」で、長い行も全体を出す。既存のテスト(22 か所)と Bench の呼び出しを変えずに済む。製品の呼び出し元は `PaintBody` の 1 か所だけで、Task 4 の Editor のテストが窓が渡っていることを固定する。
- 長い行の本文は、`EmitBodyTextWithSelection` を使わず、専用の `EmitLongRowBody` で出す。run の X と幅を足し算で出すためである。既存の関数は、渡された run の中で `OffsetToPx` を呼ぶ。切り出した部分文字列は短い行の扱いになり、一括計測に戻ってしまう。「選択の px 幅が 0 なら分割しない」規則は、そのまま写す。
- `SliceForWindow` は、窓の左端で幅 0 のコードポイント(結合文字など)を開始にしない。直前の文字と一緒に、窓の外として扱う。
- 戻り値の型は `PixelMapper.RowSlice(int Start, int End, int StartPx)`(`readonly record struct`)にする。

### 0.6 意図的な挙動差(PR に記載する)

設計書 §3.6 のとおり。

## Global Constraints

- 0 warning(`-warnaserror` 稼働中)。CSharpier 整形は pre-commit が行う。`--no-verify` で飛ばさない。
- コメント・テスト名の説明・コミットメッセージの本文は日本語。識別子は英語。
- コミットメッセージの形式は `feat|fix|docs|test|refactor|chore(scope): 要約`。末尾に `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>` を付ける。
- `git commit` は `git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit ...` で行う(Git Bash の ssh-keygen だと署名で固まる)。
- 短い行(長さ `<= PixelMapper.LongRowThreshold`)の座標・op 列を変えない。
- `LongRowThreshold` は `FrameBuilder.MaxCharsPerTextOp` を参照する。値を写さない。
- `GdiCharMetrics` は UI スレッド専用(既存の契約)。新しい状態もこの契約に従う。
- テスト本体で `Thread.Sleep` を使わない(Sonar S2925)。
- 変異の後始末で `git checkout -- <file>` を使う前に、必ず commit しておく(未 commit の変更が消える)。

## Review Focus

- **窓の左端に結合文字があるとき**: 開始が結合文字の上に来ると、基底文字のない結合文字を描いてしまう。開始は基底文字の側に寄せる(Task 3 のテスト `SliceForWindow_does_not_start_on_a_zero_width_code_point`)。
- **窓が行末より右にあるとき**(長い行を右へスクロールした後に行が短くなった、など): 本文を出さず、例外も出さない(Task 3 の `SliceForWindow_is_empty_right_of_the_row`、Task 4 の `Long_row_right_of_the_window_emits_no_body_text`)。
- **ハイコントラストテーマで、選択が窓の外にはみ出すとき**: prefix・suffix は窓の中だけ、選択の文字色は選択と窓の交差だけに付く(Task 4 の `Long_row_selection_fore_splits_only_inside_the_window`)。
- **長い行のタブ**: 空白の可視化で、タブもスペースと同じく足し算の X に出る(Task 4 の空白のテストの fixture にタブを混ぜる)。
- **打鍵で行が閾値をまたぐとき**(16,384 → 16,385 字): キャレットと描画が同じ規則(行の長さ)で切り替わる。どちらも `PixelMapper` の判定を通る(Task 3 の閾値の境界のテスト)。

---

### Task 1: Smoke `--perf` に長大行の打鍵シナリオ S11 を足す

**Files:**
- Modify: `tests/kxEdit.Editor.Smoke/PerfBench.cs`
- Modify: `tools/README.md`(§3 の Smoke `--perf` の表)

**Interfaces:**
- Produces: `--perf --scenario S11`。CSV の行 `S11a,line1m-en,...`・`S11b,line1m-en,...`・`S11a,line1m-ja,...`・`S11b,line1m-ja,...`。

- [ ] **Step 1: `AllScenarios` に S11 を足す**

`PerfBench.cs` の `AllScenarios` の末尾:

```csharp
        "S10",
        "S11",
    ];
```

- [ ] **Step 2: `Run` でシナリオを呼ぶ**

`if (opt.Scenarios.Contains("S9")) ...` の直後に足す:

```csharp
            if (opt.Scenarios.Contains("S11"))
            {
                foreach (var (name, unit, bytesPerChar) in LongLineDocs)
                    results.AddRange(
                        MeasureLongLineTyping(
                            editor,
                            Fresh(BuildLongLine(unit, LongLineChars, bytesPerChar)),
                            name,
                            opt
                        )
                    );
            }
```

- [ ] **Step 3: シナリオの本体を足す**

`// ---- 計測の核 ----` の直前に足す:

```csharp
    /// <summary>S11 の長大行の長さ(UTF-16 の単位数)。</summary>
    private const int LongLineChars = 1_000_000;

    /// <summary>
    /// S11 の回数の上限。変更前の日本語 1M 字は 1 打鍵が約 2.7 秒かかるので、既定の n = 200 では
    /// 1 文書に 20 分を超える。<c>--n</c>・<c>--warmup</c> はこの上限で切る。
    /// </summary>
    private const int LongLineMaxN = 20;

    private const int LongLineMaxWarmup = 2;

    /// <summary>S11 の文書: (名前, 繰り返す単位, 1 文字あたりの UTF-8 バイト数)。改行も空白も含まない。</summary>
    private static readonly (string Name, string Unit, int BytesPerChar)[] LongLineDocs =
    [
        ("line1m-en", "abcdefghijklmnopqrstuvwxyz0123456789", 1),
        ("line1m-ja", "吾輩は猫である名前はまだ無いどこで生れたかとんと見当がつかぬ", 3),
    ];

    /// <summary>
    /// S11a(1 文字挿入)と S11b(BackSpace): 1M 字の 1 行(折り返し OFF)の行頭付近で、S3 と同じ交互の打鍵を行う
    /// (申し送り回収フェーズ 9・設計書 docs/plans/2026-09-27-perf-followups-design.md §13.1)。
    /// 打鍵のたびに行全体の本文が変わるので、行の記述子・描画・横スクロールバーの計算が行全体に及ぶ。
    /// 回数は <see cref="LongLineMaxN"/> / <see cref="LongLineMaxWarmup"/> で切る。
    /// </summary>
    private static List<Result> MeasureLongLineTyping(
        EditorControl editor,
        TextBuffer buffer,
        string doc,
        Options opt
    )
    {
        int n = Math.Min(opt.N, LongLineMaxN);
        int warmup = Math.Min(opt.Warmup, LongLineMaxWarmup);
        editor.SetOrReplaceSource(buffer);
        editor.TopLine = 0;
        editor.ScrollX = 0;
        editor.SetCaretCharOffset(5);
        editor.Update();
        Check(
            editor.WrapColumns == 0 && editor.TopLine == 0 && editor.ScrollX == 0,
            $"S11/{doc}: 折り返し OFF・行頭の表示で準備できない"
        );
        CheckFocus(editor);
        int len0 = editor.CurrentBuffer.Current.CharLength;
        TypeChar(editor, 'x');
        int len1 = editor.CurrentBuffer.Current.CharLength;
        KeyDown(editor, Keys.Back);
        Check(
            len1 == len0 + 1 && editor.CurrentBuffer.Current.CharLength == len0,
            $"S11/{doc}: WM_CHAR / BackSpace で本文長が変わらない({len0} → {len1})"
        );

        var insert = new List<Sample>(n);
        var back = new List<Sample>(n);
        CollectGarbage();
        for (int k = 0; k < warmup + n; k++)
        {
            var a = TimeOnce(editor, update: true, () => TypeChar(editor, 'x'));
            var b = TimeOnce(editor, update: true, () => KeyDown(editor, Keys.Back));
            if (k >= warmup)
            {
                insert.Add(a);
                back.Add(b);
            }
        }
        Check(
            editor.CurrentBuffer.Current.CharLength == len0,
            $"S11/{doc}: 計測後の本文長が元に戻らない"
        );
        Check(
            editor.TopLine == 0 && editor.ScrollX == 0,
            $"S11/{doc}: 計測中にスクロールした"
        );
        return [Summarize("S11a", doc, null, insert), Summarize("S11b", doc, null, back)];
    }

    /// <summary>
    /// <paramref name="unit"/> を繰り返して <paramref name="chars"/> 文字の 1 行を作る(改行なし)。
    /// UTF-8 のバイト数が仕様と違えば例外 = 仕様からずれた文書を黙って測らない。
    /// </summary>
    private static string BuildLongLine(string unit, int chars, int bytesPerChar)
    {
        var sb = new StringBuilder(chars);
        while (sb.Length < chars)
            sb.Append(unit, 0, Math.Min(unit.Length, chars - sb.Length));
        string s = sb.ToString();
        int bytes = Encoding.UTF8.GetByteCount(s);
        if (bytes != chars * bytesPerChar)
            throw new PerfSelfCheckException(
                $"生成した長大行が {bytes:N0} バイト(仕様は {chars * bytesPerChar:N0})"
            );
        return s;
    }
```

- [ ] **Step 4: tools/README.md の表に行を足す**

§3 の Smoke `--perf` の ID 表で、S10 の行の直後に足す:

```markdown
| S11a / S11b | 長大行(フェーズ 9): 1M 字の 1 行(`line1m-en` は英数字、`line1m-ja` は日本語。改行・空白なし。折り返し OFF)の行頭付近で、1 文字挿入と BackSpace を交互に行い、別々に集計。回数は `--n` / `--warmup` にかかわらず最大 20 / 2 回 |
```

あわせて、表の直前の箇条書き「外観は製品の既定(…)。文書はメモリ上で生成する(ja10k / en10k。…)。」の文書の列挙に「S11 は 1M 字の 1 行」を足す:

```markdown
- 外観は製品の既定(ＭＳ ゴシック 12pt・行番号なし・折り返しなし。S9 だけ折り返し 40 桁)。文書はメモリ上で生成する(ja10k / en10k。書式は調査記録 §9.5。S11 は 1M 字の 1 行)。
```

- [ ] **Step 5: ビルドして走らせる**

Run: `dotnet build tests/kxEdit.Editor.Smoke -c Release` → 0 warning・0 error。
Run: `tests/kxEdit.Editor.Smoke/bin/Release/net9.0-windows/kxEdit.Editor.Smoke.exe --perf --scenario S11 --n 3 --warmup 1`
Expected: S11a / S11b が line1m-en・line1m-ja の 4 行出て、`EXIT 0`。変更前なので、ja は 1 打鍵 1 秒以上かかる。

- [ ] **Step 6: Commit**

```bash
git add tests/kxEdit.Editor.Smoke/PerfBench.cs tools/README.md
git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -m "test(smoke): 長大行の打鍵シナリオ S11 を --perf に足す

申し送り回収フェーズ 9(傘 §13.1)。1M 字の 1 行(英字・日本語)の行頭付近で
1 文字挿入と BackSpace を交互に測る。シナリオは結論によらず残す。

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 7: 変更前の基準値を採る**

この commit(製品コードは main と同じ)で、NVDA 起動中に次を 3 回ずつ走らせる。JSON は scratchpad に置く(リポジトリに入れない)。

```bash
EXE=tests/kxEdit.Editor.Smoke/bin/Release/net9.0-windows/kxEdit.Editor.Smoke.exe
for r in 1 2 3; do $EXE --perf --scenario S11 --json "$SP/before-s11-$r.json"; done
for r in 1 2 3; do $EXE --perf --scenario S3,S7 --json "$SP/before-s3s7-$r.json"; done
```

(`$SP` は scratchpad のディレクトリ。)

---

### Task 2: 幅の足し算 `ICharMetrics.MeasureAdditive`(新しい seam)

**前倒しのコード品質レビューを行う**(後続の Task 3・4 が乗る seam のため。設計書 §5)。

**Files:**
- Modify: `src/kxEdit.Core/Layout/ICharMetrics.cs`
- Modify: `src/kxEdit.Editor/GdiCharMetrics.cs`
- Create: `tests/kxEdit.Core.Tests/Layout/GdiLikeMetrics.cs`
- Create: `tests/kxEdit.Core.Tests/Layout/CharMetricsAdditiveTests.cs`
- Create: `tests/kxEdit.Editor.Tests/GdiCharMetricsAdditiveTests.cs`

**Interfaces:**
- Produces: `int ICharMetrics.MeasureAdditive(ReadOnlySpan<char> text)`(既定実装つき)。`GdiCharMetrics.MeasureAdditive`(public・高速版)。テスト用の `GdiLikeMetrics`(Core.Tests・internal)と、その定数 `GdiLikeMetrics.GdiRunLimit = 43_679`。

- [ ] **Step 1: 偽のメトリクス `GdiLikeMetrics` を作る**

`tests/kxEdit.Core.Tests/Layout/GdiLikeMetrics.cs`:

```csharp
using kxEdit.Core.Layout;
using kxEdit.Core.Text;

namespace kxEdit.Core.Tests.Layout;

/// <summary>
/// GDI(<c>GdiCharMetrics</c>)の計測の性質を写した偽のメトリクス(設計書 2026-10-06 §4.1)。
/// 1 コードポイントの幅は ASCII とタブが 1、それ以外(サロゲートペアを含む)が 2。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>ASCII だけの run は 1 文字ずつの足し算(<c>GdiCharMetrics</c> の ASCII 経路と同じ)。</item>
/// <item>非 ASCII を含み 2 コードポイント以上の run は、足し算より 1 小さい(一括計測が加算的でないことの模擬)。</item>
/// <item>非 ASCII を含み <see cref="GdiRunLimit"/> 字を超える run は 0(GDI が上限を超えると幅 0 を返す。2026-10-06 実測)。</item>
/// </list>
/// <see cref="ICharMetrics.MeasureAdditive"/> は上書きしない(既定実装を使う)。
/// </remarks>
internal sealed class GdiLikeMetrics : ICharMetrics
{
    /// <summary>GDI が幅を返せる最大の文字数(Uniscribe の制約。F-1 設計書 §2.1)。</summary>
    internal const int GdiRunLimit = 43_679;

    private readonly MonoCharMetrics _mono = new(halfWidthPx: 1, lineHeightPx: 10);

    public int LineHeightPx => _mono.LineHeightPx;

    public int MeasureRun(ReadOnlySpan<char> text)
    {
        int sum = _mono.MeasureRun(text);
        bool hasNonAscii = false;
        foreach (char c in text)
        {
            if (c >= 128)
            {
                hasNonAscii = true;
                break;
            }
        }
        if (!hasNonAscii)
            return sum;
        if (text.Length > GdiRunLimit)
            return 0;
        bool singleCodePoint = TextBoundary.CodePointLengthAt(text, 0) == text.Length;
        return singleCodePoint ? sum : sum - 1;
    }
}
```

- [ ] **Step 2: Core の失敗するテストを書く**

`tests/kxEdit.Core.Tests/Layout/CharMetricsAdditiveTests.cs`:

```csharp
using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Layout;

/// <summary>
/// <see cref="ICharMetrics.MeasureAdditive"/> の既定実装(設計書 2026-10-06 §3.2)。
/// 1 コードポイントずつ <see cref="ICharMetrics.MeasureRun"/> で測った和になる。
/// </summary>
public class CharMetricsAdditiveTests
{
    private static readonly ICharMetrics G = new GdiLikeMetrics();

    [Fact]
    public void Default_sums_code_point_widths_instead_of_measuring_the_run_at_once()
    {
        // "あいう" は一括だと 2*3-1 = 5(GdiLikeMetrics の模擬カーニング)。足し算は 6。
        Assert.Equal(5, G.MeasureRun("あいう"));
        Assert.Equal(6, G.MeasureAdditive("あいう"));
    }

    [Fact]
    public void Default_counts_a_surrogate_pair_once()
    {
        // a(1) + 😀(2) + b(1) = 4。ペアを 2 つの単独サロゲートとして測ると 2+2 になる。
        Assert.Equal(4, G.MeasureAdditive("a😀b"));
    }

    [Fact]
    public void Default_does_not_fall_to_zero_beyond_the_gdi_limit()
    {
        string s = new('あ', GdiLikeMetrics.GdiRunLimit + 1);
        Assert.Equal(0, G.MeasureRun(s)); // 前提: 一括計測は 0 になる
        Assert.Equal(2 * s.Length, G.MeasureAdditive(s));
    }

    [Fact]
    public void Default_of_empty_is_zero()
    {
        Assert.Equal(0, G.MeasureAdditive(ReadOnlySpan<char>.Empty));
    }
}
```

- [ ] **Step 3: 失敗を確かめる**

Run: `dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~CharMetricsAdditiveTests"`
Expected: ビルドエラー(`ICharMetrics` に `MeasureAdditive` がない)。

- [ ] **Step 4: `ICharMetrics` に既定実装つきのメンバーを足す**

`src/kxEdit.Core/Layout/ICharMetrics.cs` 全体:

```csharp
using kxEdit.Core.Text;

namespace kxEdit.Core.Layout;

/// <summary>
/// 文字幅と行高の計測。純レイアウトはこれ越しに測る(実 GDI は kxEdit.Editor 側)。
/// 呼び出し側はサロゲートペアを分割しない(ペアは1回の呼び出しに含める)。
/// </summary>
public interface ICharMetrics
{
    int LineHeightPx { get; }

    /// <summary>text の描画幅(px)。サロゲートペア/CJK/ASCII 混在可。</summary>
    int MeasureRun(ReadOnlySpan<char> text);

    /// <summary>
    /// <paramref name="text"/> の幅を、コードポイントごとの <see cref="MeasureRun"/> の和で返す
    /// (長い行の座標用。設計書 docs/plans/2026-10-06-long-row-geometry-design.md §3.2)。
    /// </summary>
    /// <remarks>
    /// 非 ASCII を含む run の一括計測(<see cref="MeasureRun"/>)とは一致しないことがある(カーニング・合成)。
    /// 一括計測は 43,679 字を超えると 0 を返す(GDI の制約)が、こちらは長さによらず和を返す。
    /// 既定実装は 1 コードポイントずつ <see cref="MeasureRun"/> を呼ぶ。速い実装は同じ値を返すこと。
    /// </remarks>
    int MeasureAdditive(ReadOnlySpan<char> text)
    {
        int px = 0;
        int i = 0;
        while (i < text.Length)
        {
            int cpLen = TextBoundary.CodePointLengthAt(text, i);
            px += MeasureRun(text.Slice(i, cpLen));
            i += cpLen;
        }
        return px;
    }
}
```

- [ ] **Step 5: Core のテストが通ることを確かめる**

Run: `dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~CharMetricsAdditiveTests"`
Expected: 4 件 PASS。

- [ ] **Step 6: Editor の失敗するテストを書く**

`tests/kxEdit.Editor.Tests/GdiCharMetricsAdditiveTests.cs`:

```csharp
using kxEdit.Core.Layout;
using kxEdit.Editor;

namespace kxEdit.Editor.Tests;

/// <summary>
/// <see cref="GdiCharMetrics.MeasureAdditive"/>(設計書 2026-10-06 §3.2): BMP の幅を配列で引く高速版が、
/// 1 コードポイントずつの <see cref="GdiCharMetrics.MeasureRun"/> の和と同じ値を返すこと。
/// </summary>
public class GdiCharMetricsAdditiveTests
{
    private static int SumOfCodePoints(GdiCharMetrics m, string s)
    {
        int px = 0;
        int i = 0;
        while (i < s.Length)
        {
            int len = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            px += m.MeasureRun(s.AsSpan(i, len));
            i += len;
        }
        return px;
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("a\tb")]
    [InlineData("あいう漢字")]
    [InlineData("aあ😀b")]
    [InlineData("ｱｲｳ・ー")]
    public void Matches_the_sum_of_single_code_point_measures(string s) =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);

            int expected = SumOfCodePoints(m, s);
            Assert.Equal(expected, m.MeasureAdditive(s));
            Assert.Equal(expected, m.MeasureAdditive(s)); // 2 回目は配列のヒット
        });

    /// <summary>単独サロゲート(InlineData に置くと xUnit の ID が衝突するので Fact に分ける)。</summary>
    [Fact]
    public void Matches_the_sum_for_lone_surrogates() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);

            foreach (string s in new[] { "\uD83Dx", "x\uDE00", "\uDE00\uD83D" })
                Assert.Equal(SumOfCodePoints(m, s), m.MeasureAdditive(s));
        });

    [Fact]
    public void Does_not_fall_to_zero_beyond_the_gdi_limit() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);
            string s = new('吾', 50_000);

            Assert.Equal(0, m.MeasureRun(s)); // 前提: GDI の一括計測は 43,679 字を超えると 0
            Assert.Equal(50_000 * m.MeasureRun("吾"), m.MeasureAdditive(s));
        });

    [Fact]
    public void Interface_call_reaches_the_fast_override() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            ICharMetrics m = new GdiCharMetrics(font);
            // 既定実装でも値は同じなので、ここでは interface 経由で呼べて同じ値になることだけを見る。
            Assert.Equal(3 * m.MeasureRun("あ"), m.MeasureAdditive("あああ"));
        });
}
```

- [ ] **Step 7: 失敗を確かめる**

Run: `dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~GdiCharMetricsAdditiveTests"`
Expected: ビルドエラー(`GdiCharMetrics` に public の `MeasureAdditive` がない。interface の既定実装は具象型から呼べない)。

- [ ] **Step 8: `GdiCharMetrics` に高速版を足す**

`src/kxEdit.Editor/GdiCharMetrics.cs` の `_runWidthsBySpan` 宣言の直後にフィールドを足す:

```csharp
    // 2026-10-06 長い行の座標(設計書 docs/plans/2026-10-06-long-row-geometry-design.md §3.2):
    // MeasureAdditive が BMP の非 ASCII を Dictionary ではなく配列で引くための表。値は CachedCodePointWidth の結果
    // そのもの(未計測は -1)。1M 字の行を歩くとき、Dictionary では 10〜20 ms かかるため。
    // 初めて要るときに作る(65,536 要素 = 256KB)。寿命は _nonAsciiWidths と同じ = フォントの寿命。UI スレッド専用。
    private int[]? _bmpWidths;
```

`MeasureRun` の直後にメソッドを足す:

```csharp
    /// <summary>
    /// <see cref="ICharMetrics.MeasureAdditive"/> の高速版。返す値は 1 コードポイントずつの <see cref="MeasureRun"/>
    /// の和と同じ(ASCII は <see cref="_asciiWidths"/>、BMP の非 ASCII は <see cref="_bmpWidths"/>、
    /// サロゲートペアは <see cref="CachedCodePointWidth"/>)。単独サロゲートは長さ 1 のコードポイントとして
    /// BMP の表で引く(<see cref="CachedCodePointWidth"/> の長さ 1 のキーと同じ)。
    /// </summary>
    public int MeasureAdditive(ReadOnlySpan<char> text)
    {
        int px = 0;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c < 128)
            {
                px += _asciiWidths[c];
                i++;
                continue;
            }
            int cpLen = TextBoundary.CodePointLengthAt(text, i);
            if (cpLen == 1)
            {
                var table = _bmpWidths ??= CreateBmpWidthTable();
                int w = table[c];
                if (w < 0)
                {
                    w = CachedCodePointWidth(text.Slice(i, 1));
                    table[c] = w;
                }
                px += w;
            }
            else
            {
                px += CachedCodePointWidth(text.Slice(i, cpLen));
            }
            i += cpLen;
        }
        return px;
    }

    private static int[] CreateBmpWidthTable()
    {
        var table = new int[char.MaxValue + 1];
        Array.Fill(table, -1);
        return table;
    }
```

- [ ] **Step 9: テストが通ることを確かめる**

Run: `dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~GdiCharMetrics"`
Expected: 新しい 8 件(Theory 5 + Fact 3)と既存の `GdiCharMetricsCacheTests` がすべて PASS。
Run: `dotnet build kxEdit.sln -c Debug` → 0 warning。

- [ ] **Step 10: Commit**

```bash
git add src/kxEdit.Core/Layout/ICharMetrics.cs src/kxEdit.Editor/GdiCharMetrics.cs tests/kxEdit.Core.Tests/Layout/GdiLikeMetrics.cs tests/kxEdit.Core.Tests/Layout/CharMetricsAdditiveTests.cs tests/kxEdit.Editor.Tests/GdiCharMetricsAdditiveTests.cs
git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -m "feat(layout): コードポイント幅の足し算 ICharMetrics.MeasureAdditive を足す

長い行の座標用(設計書 2026-10-06 §3.2)。GDI の一括計測は 43,679 字を超えると
幅 0 を返すので、長い行は 1 コードポイントずつの幅の和で測る。GdiCharMetrics は
BMP の幅を配列で引く高速版で上書きする(値は既存の 1 コードポイントの計測と同じ)。

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: `PixelMapper` の長い行の経路と横スクロールバー

**前倒しのコード品質レビューを行う**(Task 4 が乗る seam のため)。変異検証(スポット 2 個)もこのタスクで行う。

**Files:**
- Modify: `src/kxEdit.Core/Layout/PixelMapper.cs`
- Modify: `src/kxEdit.Editor/EditorControl.cs:1612-1613`(`UpdateHorizontalScrollbar` の行幅)
- Create: `tests/kxEdit.Core.Tests/Layout/PixelMapperLongRowTests.cs`
- Create: `tests/kxEdit.Editor.Tests/LongRowScrollTests.cs`

**Interfaces:**
- Consumes: `ICharMetrics.MeasureAdditive`(Task 2)、`GdiLikeMetrics`(Task 2)。
- Produces(Task 4 が使う):
  - `internal const int PixelMapper.LongRowThreshold`(= `FrameBuilder.MaxCharsPerTextOp`)
  - `internal static bool PixelMapper.IsLongRow(ReadOnlySpan<char> segment)`
  - `internal static int PixelMapper.RowWidthPx(ReadOnlySpan<char> segment, ICharMetrics metrics)`
  - `internal readonly record struct PixelMapper.RowSlice(int Start, int End, int StartPx)`
  - `internal static RowSlice PixelMapper.SliceForWindow(ReadOnlySpan<char> segment, int leftPx, int rightPx, ICharMetrics metrics)`

- [ ] **Step 1: Core の失敗するテストを書く**

`tests/kxEdit.Core.Tests/Layout/PixelMapperLongRowTests.cs`:

```csharp
using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Layout;

/// <summary>
/// 長い行(<see cref="PixelMapper.LongRowThreshold"/> を超える視覚行)の座標(設計書 2026-10-06 §3.3)。
/// <see cref="GdiLikeMetrics"/> は、非 ASCII の一括計測が足し算より 1 小さく、43,679 字を超えると 0 になる。
/// 短い行は一括計測(今まで)、長い行は足し算になることを、この差で見分ける。
/// </summary>
public class PixelMapperLongRowTests
{
    private static readonly ICharMetrics G = new GdiLikeMetrics();
    private const int T = PixelMapper.LongRowThreshold;

    [Fact]
    public void Threshold_is_the_text_op_limit()
    {
        Assert.Equal(FrameBuilder.MaxCharsPerTextOp, PixelMapper.LongRowThreshold);
    }

    // ---- 閾値の境界(短い行は今までどおり一括計測) ----

    [Fact]
    public void Row_at_exactly_the_threshold_keeps_the_one_shot_measure()
    {
        string row = new('あ', T);
        Assert.False(PixelMapper.IsLongRow(row));
        Assert.Equal(2 * T - 1, PixelMapper.OffsetToPx(row, T, G));
        Assert.Equal(19, PixelMapper.OffsetToPx(row, 10, G)); // prefix 10 字の一括計測 = 20-1
        Assert.Equal(2 * T - 1, PixelMapper.RowWidthPx(row, G));
    }

    [Fact]
    public void Row_over_the_threshold_uses_the_additive_measure()
    {
        string row = new('あ', T + 1);
        Assert.True(PixelMapper.IsLongRow(row));
        Assert.Equal(2 * (T + 1), PixelMapper.OffsetToPx(row, T + 1, G));
        // 判定は行の長さで決まる(prefix が短くても足し算)。
        Assert.Equal(20, PixelMapper.OffsetToPx(row, 10, G));
        Assert.Equal(2 * (T + 1), PixelMapper.RowWidthPx(row, G));
    }

    // ---- GDI の上限を超える行(今は 0 になる = 設計書 §1.2 の不具合) ----

    [Fact]
    public void OffsetToPx_beyond_the_gdi_limit_is_not_zero()
    {
        string row = new('あ', 50_000);
        Assert.Equal(90_000, PixelMapper.OffsetToPx(row, 45_000, G));
        Assert.Equal(100_000, PixelMapper.OffsetToPx(row, 50_000, G));
        Assert.Equal(100_000, PixelMapper.RowWidthPx(row, G));
    }

    [Fact]
    public void OffsetToPx_in_a_long_row_snaps_a_low_surrogate_forward()
    {
        string row = string.Concat(Enumerable.Repeat("😀", 10_000)); // 20,000 単位
        Assert.True(PixelMapper.IsLongRow(row));
        Assert.Equal(PixelMapper.OffsetToPx(row, 4, G), PixelMapper.OffsetToPx(row, 5, G));
        Assert.Equal(4, PixelMapper.OffsetToPx(row, 4, G));
    }

    [Theory]
    [InlineData(90_000, 45_000)] // ちょうど境界 = その直前のコードポイントを含めた直後
    [InlineData(89_999, 45_000)] // コードポイントの途中 = そのコードポイントの直後
    [InlineData(89_998, 44_999)]
    [InlineData(1, 1)]
    [InlineData(100_000, 50_000)]
    [InlineData(200_000, 50_000)] // 行末より右 = 行末
    public void PxToOffset_in_a_long_row_inverts_OffsetToPx(int px, int expected)
    {
        string row = new('あ', 50_000);
        Assert.Equal(expected, PixelMapper.PxToOffset(row, px, G));
    }

    // ---- SliceForWindow ----

    [Fact]
    public void SliceForWindow_from_the_row_start()
    {
        string row = new('あ', 20_000); // 1 文字 2px
        var s = PixelMapper.SliceForWindow(row, 0, 10, G);
        // 開始 X が 10 未満の文字は 0..4。最初に 10 以上になる 5 の次まで含める。
        Assert.Equal(new PixelMapper.RowSlice(0, 6, 0), s);
    }

    [Fact]
    public void SliceForWindow_starts_at_the_code_point_containing_the_left_edge()
    {
        string row = new('あ', 20_000);
        var s = PixelMapper.SliceForWindow(row, 11, 21, G);
        // 11 は文字 5([10,12))の中。開始 X が 21 未満の最後は文字 10(X=20)。その次の 11 まで含める。
        Assert.Equal(new PixelMapper.RowSlice(5, 12, 10), s);
        Assert.Equal(PixelMapper.OffsetToPx(row, s.Start, G), s.StartPx);
    }

    [Fact]
    public void SliceForWindow_with_a_negative_left_edge_starts_at_zero()
    {
        string row = new('あ', 20_000);
        Assert.Equal(new PixelMapper.RowSlice(0, 3, 0), PixelMapper.SliceForWindow(row, -50, 3, G));
    }

    [Fact]
    public void SliceForWindow_is_empty_right_of_the_row()
    {
        string row = new('あ', 20_000);
        var s = PixelMapper.SliceForWindow(row, 40_000, 40_010, G);
        Assert.Equal(new PixelMapper.RowSlice(20_000, 20_000, 40_000), s);
    }

    [Fact]
    public void SliceForWindow_does_not_split_a_surrogate_pair()
    {
        string row = string.Concat(Enumerable.Repeat("😀", 10_000)); // 1 ペア 2px
        var s = PixelMapper.SliceForWindow(row, 3, 5, G);
        // 3 はペア 1(単位 2..3・[2,4))の中。開始 X が 5 未満の最後はペア 2(X=4)。その次のペア 3 まで含める。
        Assert.Equal(new PixelMapper.RowSlice(2, 8, 2), s);
    }

    [Fact]
    public void SliceForWindow_does_not_start_on_a_zero_width_code_point()
    {
        // 幅 0 のコードポイントを持つ偽のメトリクスで、基底文字(1px) + 結合文字(0px)を並べる。
        var m = new ZeroWidthMarkMetrics();
        string row = string.Concat(Enumerable.Repeat("a\u0301", 10_000)); // 20,000 単位
        var s = PixelMapper.SliceForWindow(row, 3, 5, m);
        // 左端 3 は基底文字 3(単位 6)の中。結合文字(単位 7)から始めない。
        Assert.Equal(6, s.Start);
        Assert.Equal(3, s.StartPx);
        // 左端がちょうど基底文字 3 の終わり(X=4)= 結合文字 3 の位置でも、結合文字からは始めない。
        var t = PixelMapper.SliceForWindow(row, 4, 6, m);
        Assert.Equal(8, t.Start); // 基底文字 4
        Assert.Equal(4, t.StartPx);
    }

    /// <summary>U+0301 だけ幅 0、それ以外は 1px。</summary>
    private sealed class ZeroWidthMarkMetrics : ICharMetrics
    {
        public int LineHeightPx => 10;

        public int MeasureRun(ReadOnlySpan<char> text)
        {
            int px = 0;
            foreach (char c in text)
                px += c == '\u0301' ? 0 : 1;
            return px;
        }
    }
}
```

- [ ] **Step 2: 失敗を確かめる**

Run: `dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~PixelMapperLongRowTests"`
Expected: ビルドエラー(`LongRowThreshold`・`IsLongRow`・`RowWidthPx`・`RowSlice`・`SliceForWindow` がない)。

- [ ] **Step 3: `PixelMapper` を実装する**

`src/kxEdit.Core/Layout/PixelMapper.cs` 全体:

```csharp
using kxEdit.Core.Text;

namespace kxEdit.Core.Layout;

/// <summary>
/// 折り返し済みセグメント内での char↔pixel マッピング(設計書 §2-3)。
/// セグメント先頭を x=0 とする純関数。P2 のキャレット位置決め・P3 のマウス衝突判定で使う。
/// </summary>
/// <remarks>
/// <b>長い行</b>(長さが <see cref="LongRowThreshold"/> を超えるセグメント)は、X をコードポイント幅の足し算
/// (<see cref="ICharMetrics.MeasureAdditive"/>)で求める(docs/plans/2026-10-06-long-row-geometry-design.md §3.3)。
/// GDI の一括計測は 43,679 字を超えると幅 0 を返し、また長い行を毎回一括で測ると行の長さに比例する GDI 計測が
/// 残るためである。短い行は従来どおり一括計測(座標は変えない)。判定はセグメント全体の長さで行う
/// (prefix の長さではない)ので、同じ行のキャレット・選択・描画は必ず同じ規則で測られる。
/// </remarks>
internal static class PixelMapper
{
    /// <summary>
    /// これを超える長さのセグメントを「長い行」とする。F-1 の区切り(<see cref="FrameBuilder.MaxCharsPerTextOp"/>)と
    /// 同じ値 = 通常の行では絶対に切り替わらない。
    /// </summary>
    internal const int LongRowThreshold = FrameBuilder.MaxCharsPerTextOp;

    /// <summary>窓にかかる文字範囲 [<paramref name="Start"/>, <paramref name="End"/>) と、Start の X(<see cref="SliceForWindow"/>)。</summary>
    internal readonly record struct RowSlice(int Start, int End, int StartPx);

    internal static bool IsLongRow(ReadOnlySpan<char> segment) =>
        segment.Length > LongRowThreshold;

    /// <summary>
    /// segment 内の charOffset(0..segment.Length)を pixel(0..)にマップ。
    /// - charOffset&lt;=0 → 0 / charOffset&gt;=segment.Length → 全幅
    /// - low サロゲート位置に落ちた場合は前方スナップ(pair 先頭 = charOffset-1 に寄せる)
    /// - 長い行は足し算(クラスの remarks)
    /// </summary>
    /// <remarks>
    /// <b>このスナップ方針に外部が依存している</b>:
    /// <c>FrameBuilder.EmitBodyTextWithSelection</c> は選択境界で本文テキストを切り出す際に
    /// <see cref="TextBoundary.SnapToCodePointStart"/> を<b>自前で</b>掛け、その結果が本メソッドの
    /// 内部スナップと一致することを前提に x と文字を合わせている。
    /// スナップ方針を変える(後方スナップにする・論理文字単位にする等)ときは、
    /// <b>必ずあちらも一緒に直すこと</b>。放置すると選択矩形と文字が黙ってずれる。
    /// </remarks>
    public static int OffsetToPx(ReadOnlySpan<char> segment, int charOffset, ICharMetrics metrics)
    {
        if (charOffset <= 0)
            return 0;
        if (charOffset > segment.Length)
            charOffset = segment.Length;

        // low サロゲート位置なら pair 先頭へ前方スナップ
        charOffset = TextBoundary.SnapToCodePointStart(segment, charOffset);

        var prefix = segment[..charOffset];
        return IsLongRow(segment) ? metrics.MeasureAdditive(prefix) : metrics.MeasureRun(prefix);
    }

    /// <summary>
    /// x(px)に最も近い code-point 境界のオフセットを返す。
    /// - x&lt;=0 → 0 / x&gt;=全幅 → segment.Length
    /// - code-point に px が食い込む場合はその code-point の直後を返す
    ///   (=「入れば含める」・選択拡張の直観に合わせる)
    /// - サロゲートペアの中間には落ちない(常に pair の直後)
    /// </summary>
    /// <remarks>
    /// 長い行では、先頭の全幅の一括計測を行わない(43,679 字を超えると 0 になり、どの px でも行末を返してしまう)。
    /// 1 コードポイントずつ歩く処理だけで求め、歩き切ったら行末を返す(全幅以上の px と同じ結果)。
    /// </remarks>
    public static int PxToOffset(ReadOnlySpan<char> segment, int px, ICharMetrics metrics)
    {
        if (segment.IsEmpty)
            return 0;
        if (px <= 0)
            return 0;

        if (!IsLongRow(segment))
        {
            int total = metrics.MeasureRun(segment);
            if (px >= total)
                return segment.Length;
        }

        int i = 0;
        int accumulated = 0;
        while (i < segment.Length)
        {
            // 次の code-point を切り出す(サロゲートペアは 2 code-unit 分)
            int cpLen = TextBoundary.CodePointLengthAt(segment, i);

            int cpWidth = metrics.MeasureRun(segment.Slice(i, cpLen));

            // 累積 + この code-point の幅が px 以上なら、この code-point を含めた直後を返す
            if (accumulated + cpWidth >= px)
                return i + cpLen;

            accumulated += cpWidth;
            i += cpLen;
        }

        // 短い行では早期リターンで捕捉されるはずだが安全網。長い行では「全幅以上の px」がここに来る。
        return segment.Length;
    }

    /// <summary>
    /// セグメントの全幅(横スクロールバーの幅の計算用)。<see cref="OffsetToPx"/> で行末を測ったのと同じ値。
    /// 短い行は一括計測、長い行は足し算。
    /// </summary>
    internal static int RowWidthPx(ReadOnlySpan<char> segment, ICharMetrics metrics) =>
        IsLongRow(segment) ? metrics.MeasureAdditive(segment) : metrics.MeasureRun(segment);

    /// <summary>
    /// 窓 [<paramref name="leftPx"/>, <paramref name="rightPx"/>)(セグメント先頭が x = 0)にかかる文字範囲を返す
    /// (長い行の描画用。設計書 2026-10-06 §3.3)。X は足し算で測る。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Start: X の範囲 [x, x + w) が leftPx を含むコードポイントの先頭。leftPx &lt;= 0 なら 0。
    /// 幅 0 のコードポイント(結合文字など)からは始めない(基底文字のない結合文字を描かないため)。</item>
    /// <item>End: X の開始が rightPx 以上になる最初のコードポイントの、次のコードポイントの先頭
    /// (右端からはみ出すグリフのための 1 つの余裕)。行末を超えない。</item>
    /// <item>窓が行末より右なら Start = End = 行末(空)。</item>
    /// <item>StartPx は <c>OffsetToPx(segment, Start)</c>(長い行)と同じ値。境界はコードポイントの境界。</item>
    /// <item>費用は O(End)。窓より右は歩かない。</item>
    /// </list>
    /// </remarks>
    internal static RowSlice SliceForWindow(
        ReadOnlySpan<char> segment,
        int leftPx,
        int rightPx,
        ICharMetrics metrics
    )
    {
        int i = 0;
        int x = 0;
        if (leftPx > 0)
        {
            while (i < segment.Length)
            {
                int cpLen = TextBoundary.CodePointLengthAt(segment, i);
                int w = metrics.MeasureAdditive(segment.Slice(i, cpLen));
                if (x + w > leftPx)
                    break;
                x += w;
                i += cpLen;
            }
        }
        int start = i;
        int startPx = x;
        while (i < segment.Length && x < rightPx)
        {
            int cpLen = TextBoundary.CodePointLengthAt(segment, i);
            x += metrics.MeasureAdditive(segment.Slice(i, cpLen));
            i += cpLen;
        }
        if (i < segment.Length && i > start)
            i += TextBoundary.CodePointLengthAt(segment, i);
        return new RowSlice(start, i, startPx);
    }
}
```

注: 2 つ目のループの後の `i > start` は、窓が空(`rightPx <= leftPx` など)で 1 文字も歩かなかったときに、余裕の 1 文字だけを足さないための条件である。`SliceForWindow_with_a_negative_left_edge_starts_at_zero`(右端 3)では、文字 0・1(X = 0, 2)を歩いて、X = 4 ≥ 3 で止まり、余裕の 1 文字を足して End = 3 になる。

- [ ] **Step 4: Core のテストが通ることを確かめる**

Run: `dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~PixelMapper"`
Expected: 新しいテストと既存の `PixelMapperTests` がすべて PASS。
Run: `dotnet test tests/kxEdit.Core.Tests`(Core 全体)→ 全件 PASS。

- [ ] **Step 5: Editor の失敗するテストを書く**

`tests/kxEdit.Editor.Tests/LongRowScrollTests.cs`:

```csharp
using kxEdit.Core.Buffers;

namespace kxEdit.Editor.Tests;

/// <summary>
/// 非 ASCII の 43,679 字を超える行で、横スクロールとキャレットの X が働くこと(設計書 2026-10-06 §1.2・§4.2)。
/// 修正前は GDI の一括計測が幅 0 を返し、横スクロールバーが出ず、キャレットを行末に置いても ScrollX が 0 のままだった。
/// </summary>
public class LongRowScrollTests
{
    private const int Chars = 50_000;

    private static (Form f, EditorControl c) MakeControl()
    {
        var f = new Form { Size = new Size(400, 200) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(new string('吾', Chars)));
        return (f, c);
    }

    [Fact]
    public void Caret_at_the_end_of_a_long_japanese_row_scrolls_into_view() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                Assert.Equal(0, c.WrapColumns);
                c.SetCaretCharOffset(Chars);

                Assert.True(c.ScrollX > 0, $"ScrollX が 0 のまま(横スクロールバーが出ていない)");
                var (x, _, visible) = c.ComputeCaretPoint(Chars);
                Assert.True(visible);
                // 追従は「可視領域末尾から 1 半角幅内側」に置く(BringCaretIntoView)。描画幅は縦スクロールバーの分だけ
                // クライアント幅より狭いが、その幅は DPI に依存するので、ここではクライアント幅の内側であることを見る。
                Assert.InRange(x - c.ScrollX, 0, c.ClientSize.Width - 1);
            }
        });

    [Fact]
    public void Caret_x_in_a_long_japanese_row_is_the_sum_of_character_widths() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                int one = c.ComputeCaretPoint(1).X - c.ComputeCaretPoint(0).X;
                Assert.True(one > 0);
                int x0 = c.ComputeCaretPoint(0).X;
                Assert.Equal(x0 + 45_000 * one, c.ComputeCaretPoint(45_000).X);
            }
        });
}
```

- [ ] **Step 6: 失敗を確かめる**

Run: `dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~LongRowScrollTests"`
Expected: `Caret_at_the_end_...` が FAIL(`ScrollX が 0 のまま`)。`Caret_x_...` は Step 3 の `PixelMapper` で既に PASS しうる(キャレットの X は `PixelMapper` を通る)。横スクロールバーは `UpdateHorizontalScrollbar` を直すまで出ない。

- [ ] **Step 7: `UpdateHorizontalScrollbar` を直す**

`src/kxEdit.Editor/EditorControl.cs` の `UpdateHorizontalScrollbar` のループ:

```csharp
            string lineText = snap.GetText(row.SegmentStartChar, row.SegmentLength);
            // 2026-10-06 長い行(設計書 docs/plans/2026-10-06-long-row-geometry-design.md §3.5): 長い行は足し算で測る。
            // 一括計測は非 ASCII を含み 43,679 字を超えると幅 0 を返し、横スクロールバーが出なくなる。
            int width = PixelMapper.RowWidthPx(lineText.AsSpan(), _metrics);
```

(`int width = _metrics.MeasureRun(lineText.AsSpan());` を置き換える。`EditorControl.cs` は `kxEdit.Core.Layout` を using 済みか確かめ、なければ足す。)

- [ ] **Step 8: テストが通ることを確かめる**

Run: `dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~LongRowScrollTests|FullyQualifiedName~CaretScrollTests|FullyQualifiedName~MouseInputTests|FullyQualifiedName~EditorControlOffsetFromPointTests"`
Expected: すべて PASS。
Run: `dotnet build kxEdit.sln -c Debug` → 0 warning。

- [ ] **Step 9: Commit**

```bash
git add src/kxEdit.Core/Layout/PixelMapper.cs src/kxEdit.Editor/EditorControl.cs tests/kxEdit.Core.Tests/Layout/PixelMapperLongRowTests.cs tests/kxEdit.Editor.Tests/LongRowScrollTests.cs
git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -m "fix(layout): 長い行の座標を文字幅の足し算で求め、横スクロールを効かせる

16,384 字を超える視覚行は、PixelMapper が X をコードポイント幅の足し算で求める
(設計書 2026-10-06 §3.3)。GDI の一括計測は非 ASCII を含み 43,679 字を超えると
幅 0 を返し、横スクロールバーが出ない・キャレットの X が 0 になる・クリックが行末に
飛ぶ不具合があった。短い行の座標は変えない。

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 10: 変異検証(スポット 2 個)**

commit 済みであることを確かめてから行う。1 個ずつ、変異 → ビルドの成功を確かめる → テスト → 元に戻す。

1. `IsLongRow` の `>` を `>=` にする。
   - Run: `dotnet build tests/kxEdit.Core.Tests` が成功すること(失敗したら変異は無効。古い DLL で緑に見える)。
   - Run: `dotnet test tests/kxEdit.Core.Tests --no-build --filter "FullyQualifiedName~PixelMapperLongRowTests"`
   - Expected: `Row_at_exactly_the_threshold_keeps_the_one_shot_measure` が FAIL(殺される)。
2. `PxToOffset` の `accumulated + cpWidth >= px` を `>` にする。
   - 同じ手順。Expected: `PxToOffset_in_a_long_row_inverts_OffsetToPx(90000, 45000)` が FAIL。
3. 元に戻す: `git checkout -- src/kxEdit.Core/Layout/PixelMapper.cs`。`git status` が clean であることを確かめる。

結果(殺された・生き残った)を、Task 5 の実施記録に書く。

---

### Task 4: 長い行は窓にかかる文字だけを描く(`FrameBuilder` と `PaintBody`)

**Files:**
- Modify: `src/kxEdit.Core/Layout/FrameBuilder.cs`(`Build` の引数・工程 5 と 6・新しい private メソッド 2 つ)
- Modify: `src/kxEdit.Editor/EditorControl.Paint.cs:236-248`(`PaintBody` の `Build` 呼び出し)
- Create: `tests/kxEdit.Core.Tests/Layout/FrameBuilderLongRowTests.cs`
- Modify: `tests/kxEdit.Editor.Tests/LongRowScrollTests.cs`(テストを 1 つ足す)

**Interfaces:**
- Consumes: `PixelMapper.IsLongRow`・`SliceForWindow`・`RowSlice`・`OffsetToPx`(Task 3)、`ICharMetrics.MeasureAdditive`(Task 2)。
- Produces: `FrameBuilder.Build(..., ICharMetrics metrics, int viewLeftPx = 0, int viewWidthPx = int.MaxValue)`。

- [ ] **Step 1: Core の失敗するテストを書く**

`tests/kxEdit.Core.Tests/Layout/FrameBuilderLongRowTests.cs`:

```csharp
using kxEdit.Core.Buffers;
using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Layout;

/// <summary>
/// 長い行は、横の窓にかかる文字だけを本文・空白のグリフにする(設計書 2026-10-06 §3.4)。
/// 短い行の op 列は窓によらず変わらない。<see cref="GdiLikeMetrics"/>: ASCII 1px・それ以外 2px。
/// </summary>
public class FrameBuilderLongRowTests
{
    private static readonly ICharMetrics G = new GdiLikeMetrics();
    private static readonly PaintColor Fore = new(0x000000);
    private static readonly PaintColor SelFore = new(0xFF00FF);
    private static readonly PaintColor SelBack = new(0xADD8E6);
    private static readonly PaintColor Glyph = new(0xCCCCCC);

    private static ViewportStyle Style(PaintColor? selectionFore = null) =>
        new(
            Foreground: Fore,
            Background: new PaintColor(0xFFFFFF),
            CurrentLineBack: new PaintColor(0x88FF88),
            SelectionBack: SelBack,
            SelectionFore: selectionFore,
            LineNumberFore: new PaintColor(0x777777),
            HighlightOutline: new PaintColor(0xFF8800),
            WhitespaceGlyph: Glyph
        );

    private static Frame Build(
        string text,
        int? viewLeftPx = null,
        int viewWidthPx = 20,
        SelectionRange? selection = null,
        PaintColor? selectionFore = null,
        bool showWhitespace = false,
        int lineNumberMarginPx = 0
    )
    {
        var buf = TextBuffer.FromString(text);
        var rows = ViewportLayout.Build(
            buf.Current,
            topLine: 0,
            topSegment: 0,
            heightPx: 100,
            wrapColumns: 0,
            G
        );
        return viewLeftPx is int left
            ? FrameBuilder.Build(
                buf.Current,
                rows,
                clientWidth: 200,
                clientHeight: 100,
                lineNumberMarginPx: lineNumberMarginPx,
                currentLineLogical: -1,
                selection: selection,
                cellHighlight: null,
                showWhitespace: showWhitespace,
                Style(selectionFore),
                G,
                viewLeftPx: left,
                viewWidthPx: viewWidthPx
            )
            : FrameBuilder.Build(
                buf.Current,
                rows,
                clientWidth: 200,
                clientHeight: 100,
                lineNumberMarginPx: lineNumberMarginPx,
                currentLineLogical: -1,
                selection: selection,
                cellHighlight: null,
                showWhitespace: showWhitespace,
                Style(selectionFore),
                G
            );
    }

    /// <summary>本文の DrawText(行番号でも空白のグリフでもないもの)。</summary>
    private static List<PaintOp> Body(Frame f) =>
        f.Ops.Where(op => op.Kind == PaintOpKind.DrawText && op.Fore != Glyph && op.Fore != new PaintColor(0x777777)).ToList();

    [Fact]
    public void Short_row_ops_do_not_depend_on_the_window()
    {
        string row = string.Concat(Enumerable.Repeat("あ a", 100));
        var withoutWindow = Build(row, showWhitespace: true);
        var withWindow = Build(row, viewLeftPx: 37, viewWidthPx: 50, showWhitespace: true);
        Assert.Equal(withoutWindow.Ops, withWindow.Ops);
    }

    [Fact]
    public void Long_row_emits_only_the_characters_in_the_window()
    {
        string row = new('あ', 20_000);
        var body = Body(Build(row, viewLeftPx: 1001, viewWidthPx: 20));
        // 窓 [1001, 1021) → 文字 500([1000,1002))から、開始 X が 1021 未満の最後(文字 510)の次の 511 まで。
        var op = Assert.Single(body);
        Assert.Equal(new string('あ', 12), op.Text);
        Assert.Equal(1000, op.X);
        Assert.Equal(24, op.Width);
    }

    [Fact]
    public void Long_row_window_is_relative_to_the_body_after_the_line_number_margin()
    {
        string row = new('あ', 20_000);
        var body = Body(Build(row, viewLeftPx: 1001, viewWidthPx: 20, lineNumberMarginPx: 30));
        // 行頭基準の窓は [971, 991) → 文字 485([970,972))から。X = 30 + 970。
        Assert.Equal(1000, body[0].X);
        Assert.StartsWith("あ", body[0].Text);
    }

    [Fact]
    public void Long_row_without_a_window_emits_the_whole_row()
    {
        string row = new('あ', 20_000);
        var body = Body(Build(row));
        Assert.Equal(row, string.Concat(body.Select(op => op.Text)));
        Assert.Equal(0, body[0].X);
    }

    [Fact]
    public void Long_row_right_of_the_window_emits_no_body_text()
    {
        string row = new('あ', 20_000);
        Assert.Empty(Body(Build(row, viewLeftPx: 50_000, viewWidthPx: 20)));
    }

    [Fact]
    public void Long_row_selection_fore_splits_only_inside_the_window()
    {
        string row = new('あ', 20_000);
        var f = Build(
            row,
            viewLeftPx: 1001,
            viewWidthPx: 20,
            selection: new SelectionRange(505, 508),
            selectionFore: SelFore
        );
        var body = Body(f);
        // prefix [500,505)・選択 [505,508)・suffix [508,512)。prefix と suffix は窓の中だけ。
        Assert.Equal(3, body.Count);
        Assert.Equal((new string('あ', 5), 1000, 10, Fore), (body[0].Text, body[0].X, body[0].Width, body[0].Fore));
        Assert.Equal((new string('あ', 3), 1010, 6, SelFore), (body[1].Text, body[1].X, body[1].Width, body[1].Fore));
        Assert.Equal((new string('あ', 4), 1016, 8, Fore), (body[2].Text, body[2].X, body[2].Width, body[2].Fore));
        // 選択矩形(工程 3)も足し算の座標。
        var rect = Assert.Single(f.Ops, op => op.Kind == PaintOpKind.FillRect && op.Back == SelBack);
        Assert.Equal((1010, 6), (rect.X, rect.Width));
    }

    [Fact]
    public void Long_row_selection_outside_the_window_is_drawn_as_unselected()
    {
        string row = new('あ', 20_000);
        var body = Body(
            Build(
                row,
                viewLeftPx: 1001,
                viewWidthPx: 20,
                selection: new SelectionRange(100, 200),
                selectionFore: SelFore
            )
        );
        var op = Assert.Single(body);
        Assert.Equal(Fore, op.Fore);
        Assert.Equal(new string('あ', 12), op.Text);
    }

    [Fact]
    public void Long_row_whitespace_glyphs_only_inside_the_window_at_additive_x()
    {
        // "あ \t" の繰り返し(1 単位 = あ 2px + 空白 1px + タブ 1px = 4px・3 文字)。20,001 字。
        string row = string.Concat(Enumerable.Repeat("あ \t", 6_667));
        Assert.True(PixelMapper.IsLongRow(row));
        var f = Build(row, viewLeftPx: 1001, viewWidthPx: 20, showWhitespace: true);
        var glyphs = f.Ops.Where(op => op.Kind == PaintOpKind.DrawText && op.Fore == Glyph).ToList();

        var slice = PixelMapper.SliceForWindow(row, 1001, 1021, G);
        var expectedX = Enumerable
            .Range(slice.Start, slice.End - slice.Start)
            .Where(i => row[i] == ' ' || row[i] == '\t')
            .Select(i => PixelMapper.OffsetToPx(row, i, G))
            .ToList();
        Assert.NotEmpty(expectedX);
        Assert.Equal(expectedX, glyphs.Select(op => op.X).ToList());
        Assert.Contains(glyphs, op => op.Text == "→"); // タブも出る
    }
}
```

- [ ] **Step 2: 失敗を確かめる**

Run: `dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~FrameBuilderLongRowTests"`
Expected: ビルドエラー(`viewLeftPx` / `viewWidthPx` という名前の引数がない)。

- [ ] **Step 3: `Build` に窓の引数を足す**

`src/kxEdit.Core/Layout/FrameBuilder.cs` の `Build` の doc の `<param name="metrics">` の後に足す:

```csharp
    /// <param name="viewLeftPx">
    /// 横の窓の左端(Frame の X 座標 = 水平スクロール量)。長い行(<see cref="PixelMapper.IsLongRow"/>)だけが使い、
    /// 窓にかかる文字だけを本文・空白のグリフにする(設計書 2026-10-06 §3.4)。短い行の op 列は窓によらない。
    /// </param>
    /// <param name="viewWidthPx">横の窓の幅(描画幅)。既定値は窓なし(長い行も全体を出す)。</param>
```

シグネチャの末尾:

```csharp
        ViewportStyle style,
        ICharMetrics metrics,
        int viewLeftPx = 0,
        int viewWidthPx = int.MaxValue
    )
```

`int bodyX = lineNumberMarginPx;` の直後に足す:

```csharp
        // 長い行の窓(行頭基準)。右端は int を溢れないよう long で計算して丸める(既定の窓なしは int.MaxValue)。
        int windowLeft = viewLeftPx - bodyX;
        int windowRight = (int)Math.Clamp(
            (long)viewLeftPx + Math.Max(0, viewWidthPx) - bodyX,
            int.MinValue,
            int.MaxValue
        );
```

- [ ] **Step 4: 工程 5(本文)で長い行を分ける**

工程 5 のループの先頭(`var row = rows[i];` の直後、`string text = ...` の前)に足す:

```csharp
            if (row.SegmentLength > PixelMapper.LongRowThreshold)
            {
                EmitLongRowBody(
                    snapshot.GetText(row.SegmentStartChar, row.SegmentLength),
                    row,
                    bodyX,
                    lineHeight,
                    windowLeft,
                    windowRight,
                    split,
                    style.Foreground,
                    metrics,
                    ops
                );
                continue;
            }
```

- [ ] **Step 5: 工程 6(空白)で長い行を分ける**

工程 6 のループの `string text = snapshot.GetText(...)` の直後を、次に置き換える:

```csharp
                string text = snapshot.GetText(row.SegmentStartChar, row.SegmentLength);
                if (PixelMapper.IsLongRow(text))
                {
                    EmitLongRowWhitespaceGlyphs(
                        text,
                        bodyX,
                        row.YPx,
                        lineHeight,
                        windowLeft,
                        windowRight,
                        style.WhitespaceGlyph,
                        metrics,
                        ops
                    );
                    continue;
                }
                EmitWhitespaceGlyphs(
```

(既存の `EmitWhitespaceGlyphs(...)` の呼び出しはそのまま続く。)

- [ ] **Step 6: 2 つの private メソッドを足す**

`EmitWhitespaceGlyphs` の直後に足す:

```csharp
    /// <summary>
    /// 長い行の本文(設計書 2026-10-06 §3.4)。窓にかかる文字 [Start, End)(<see cref="PixelMapper.SliceForWindow"/>)だけを、
    /// 足し算の X で出す。<paramref name="split"/>(選択の文字色)があれば、選択と窓の交差で最大 3 run に分ける。
    /// </summary>
    /// <remarks>
    /// <see cref="EmitBodyTextWithSelection"/> を使わないのは、あちらが run の中で <see cref="PixelMapper.OffsetToPx"/>
    /// を呼ぶため。切り出した部分文字列は短い行の扱いになり、一括計測に戻ってしまう(選択矩形の足し算の X と
    /// ずれる)。「選択の px 幅が 0 なら分割しない」規則(あちらの doc)は、ここでも同じに守る。
    /// run の幅は足し算。run が <see cref="MaxCharsPerTextOp"/> を超える場合(窓なしの既定など)は
    /// <see cref="EmitBodyRun"/> の分割に任せる。
    /// </remarks>
    private static void EmitLongRowBody(
        string text,
        VisualRow row,
        int bodyX,
        int lineHeight,
        int windowLeft,
        int windowRight,
        (SelectionRange Range, PaintColor Fore)? split,
        PaintColor fore,
        ICharMetrics metrics,
        List<PaintOp> ops
    )
    {
        var span = text.AsSpan();
        var slice = PixelMapper.SliceForWindow(span, windowLeft, windowRight, metrics);
        if (slice.Start >= slice.End)
            return;

        if (
            split is { } sp
            && TryComputeRowIntersection(row, sp.Range, out int startInRow, out int endInRow)
        )
        {
            int selFrom = Math.Clamp(
                TextBoundary.SnapToCodePointStart(span, startInRow),
                slice.Start,
                slice.End
            );
            int selTo = Math.Clamp(
                TextBoundary.SnapToCodePointStart(span, endInRow),
                slice.Start,
                slice.End
            );
            int pxSelFrom = slice.StartPx + metrics.MeasureAdditive(span[slice.Start..selFrom]);
            int pxSelTo = pxSelFrom + metrics.MeasureAdditive(span[selFrom..selTo]);
            if (pxSelFrom != pxSelTo)
            {
                Run(slice.Start, selFrom, slice.StartPx, fore);
                Run(selFrom, selTo, pxSelFrom, sp.Fore);
                Run(selTo, slice.End, pxSelTo, fore);
                return;
            }
        }
        Run(slice.Start, slice.End, slice.StartPx, fore);

        void Run(int from, int to, int px, PaintColor color)
        {
            if (from >= to)
                return;
            // span(ref ローカル)はローカル関数から参照できないので text から作り直す。
            var run = text.AsSpan(from, to - from);
            EmitBodyRun(
                run,
                bodyX + px,
                row.YPx,
                lineHeight,
                color,
                metrics,
                ops,
                widthOverride: metrics.MeasureAdditive(run)
            );
        }
    }

    /// <summary>
    /// 長い行の空白の可視化(設計書 2026-10-06 §3.4)。窓にかかる文字だけを見て、X は窓の先頭から足し算で累積する
    /// (空白ごとに行頭から測り直すと、行の長さの 2 乗のコストになる)。
    /// </summary>
    private static void EmitLongRowWhitespaceGlyphs(
        string text,
        int bodyX,
        int yPx,
        int lineHeight,
        int windowLeft,
        int windowRight,
        PaintColor glyphFore,
        ICharMetrics metrics,
        List<PaintOp> ops
    )
    {
        var span = text.AsSpan();
        var slice = PixelMapper.SliceForWindow(span, windowLeft, windowRight, metrics);
        int x = slice.StartPx;
        int i = slice.Start;
        while (i < slice.End)
        {
            int cpLen = TextBoundary.CodePointLengthAt(span, i);
            char c = span[i];
            if (c == ' ' || c == '\t')
            {
                string glyph = c == ' ' ? SpaceGlyph : TabGlyph;
                ops.Add(
                    new PaintOp(
                        PaintOpKind.DrawText,
                        bodyX + x,
                        yPx,
                        metrics.MeasureRun(glyph),
                        lineHeight,
                        Text: glyph,
                        Fore: glyphFore
                    )
                );
            }
            x += metrics.MeasureAdditive(span.Slice(i, cpLen));
            i += cpLen;
        }
    }
```

- [ ] **Step 7: Core のテストが通ることを確かめる**

Run: `dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~FrameBuilder"`
Expected: 新しい 8 件と既存の `FrameBuilderTests`・`FrameBuilderSelectionForeTests` がすべて PASS。既存の F-1 のテスト(上限 +1 字の英字の行の分割)は、窓なしの既定で全体を出すので変わらない。ASCII の行は足し算と一括計測が同じ値になる。

注: F-1 の既存テストのうち、非 ASCII を含み上限を超える行で、op の X・幅を一括計測の値で固定しているものがあれば、長い行の足し算の値に合わせて期待値を直す。これは設計書 §3.6 の意図的な挙動差にあたるので、テストのコメントにその旨を書く。直したテストは Task 5 の実施記録に列挙する。

- [ ] **Step 8: `PaintBody` から窓を渡す**

`src/kxEdit.Editor/EditorControl.Paint.cs` の `PaintBody` の `FrameBuilder.Build(...)` の末尾の引数:

```csharp
            inputs.Style,
            inputs.Metrics,
            // 2026-10-06 長い行(設計書 docs/plans/2026-10-06-long-row-geometry-design.md §3.5): 長い行は窓にかかる文字だけを描く。
            // 窓は ScrollX と PaintWidth で決まり、どちらも描画の入力(SameLayoutAs と スクロールの判定)に入っている。
            viewLeftPx: inputs.ScrollX,
            viewWidthPx: inputs.PaintWidth
        );
```

- [ ] **Step 9: Editor のテストを足す**

`tests/kxEdit.Editor.Tests/LongRowScrollTests.cs` の末尾(クラスの中)に足す。ファイル先頭に `using kxEdit.Core.Layout;` と `using kxEdit.Editor.Tests.Fakes;` を足す:

```csharp
    [Fact]
    public void Paint_of_a_long_row_draws_only_the_characters_in_the_window() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(45_000);
                Assert.True(c.ScrollX > 0);
                PaintTestHelpers.PaintRecorded(c);

                var frame = EditorControl.TestHook_GetLastFrame(c);
                Assert.NotNull(frame);
                // 本文の op だけ(行番号の op が混ざっても拾わないよう、本文の文字だけでできたものに絞る)。
                var body = frame!
                    .Ops.Where(op =>
                        op.Kind == PaintOpKind.DrawText
                        && op.Text is { Length: > 0 } t
                        && t.All(ch => ch == '吾')
                    )
                    .ToList();
                int one = c.ComputeCaretPoint(1).X - c.ComputeCaretPoint(0).X;
                int chars = body.Sum(op => op.Text!.Length);
                // 窓に入る文字数 + 余裕(左端の 1 文字・右端の 1 文字)を超えない。行全体(5 万字)ではない。
                Assert.InRange(chars, 1, (c.ClientSize.Width / one) + 3);
                // 最初の op は窓の左端以前から始まり、1 文字ぶんより左には出ない。
                Assert.InRange(body[0].X - c.ScrollX, -one, 0);
            }
        });
```

- [ ] **Step 10: テストが通ることを確かめる**

Run: `dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~LongRowScrollTests"`
Expected: 3 件 PASS。
Run: `dotnet test tests/kxEdit.Editor.Tests`(Editor 全体)→ 全件 PASS。
Run: `dotnet build kxEdit.sln -c Debug` → 0 warning。

- [ ] **Step 11: Commit**

```bash
git add src/kxEdit.Core/Layout/FrameBuilder.cs src/kxEdit.Editor/EditorControl.Paint.cs tests/kxEdit.Core.Tests/Layout/FrameBuilderLongRowTests.cs tests/kxEdit.Editor.Tests/LongRowScrollTests.cs
git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -m "perf(paint): 長い行は横の窓にかかる文字だけを描く

F-1 の案 B(設計書 2026-10-06 §3.4)。長い行(16,384 字超)は、1 打鍵ごとに
全区切りを測って描いていた(1M 字で英字 135 ms・日本語 2.7 秒)。FrameBuilder.Build に
横の窓を渡し、本文と空白のグリフを窓にかかる文字だけにする。短い行の op 列は変えない。

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

(Step 7 の注で既存テストを直した場合は、そのファイルも add する。)

---

### Task 5: 計測・L5・実施記録

**Files:**
- Modify: `docs/plans/2026-09-27-perf-followups-design.md`(§13 の末尾に §13.3 実施記録を追記)
- Modify: `docs/plans/2026-10-06-long-row-geometry-design.md`(末尾に §7 実施記録を追記)
- Modify: 本書(末尾に実施記録を追記)

- [ ] **Step 1: 変更後の計測**

Release でビルドし、Task 1 Step 7 と同じ条件(NVDA 起動中・同じシナリオの集合)で 3 回ずつ走らせる。

```bash
dotnet build tests/kxEdit.Editor.Smoke -c Release
EXE=tests/kxEdit.Editor.Smoke/bin/Release/net9.0-windows/kxEdit.Editor.Smoke.exe
for r in 1 2 3; do $EXE --perf --scenario S11 --json "$SP/after-s11-$r.json"; done
for r in 1 2 3; do $EXE --perf --scenario S3,S7 --json "$SP/after-s3s7-$r.json"; done
```

判定(設計書 §4.3):
- S11a / S11b(en・ja): 変更後の中央値の中央値が、変更前の 3 回の最小値を下回ること。
- S3a / S3b / S7(ja10k・en10k): 変更後の中央値が、変更前の 3 回の最小〜最大の範囲を大きく超えて悪化していないこと。

下がらない場合は、dotnet-trace(tools/README §3)で内訳を採り、原因を調べる。

- [ ] **Step 2: 画面の古い絵の確認**

Run: `tests/kxEdit.Editor.Smoke/bin/Release/net9.0-windows/kxEdit.Editor.Smoke.exe --paint-transition --expect-skip`
Expected: EXIT 0。

- [ ] **Step 3: 品質ゲート**

Run(pwsh): `pwsh -File tools/pre-merge-check.ps1`
Expected: EXIT 0。

- [ ] **Step 4: L5(設計書 §4.4)**

1. `tools/sr-regression.ps1` を実行して EXIT 0。
2. 実アプリ(Release の publish)で、長大行のファイルを開く。日本語 5 万字の 1 行、英字 5 万字の 1 行、空白とタブの混ざる日本語 5 万字の 1 行を使う。PrintWindow で窓を実解像度の PNG にして、目で判定する(縮小スクリーンショットで判定しない)。
   - 行頭・中ほど(Ctrl+F で中ほどへ飛ぶ)・行末(End)で、本文が描かれていること。キャレットが文字の境目にあること。
   - 中ほどで Shift+→ を数回押して選択し、選択矩形と文字の色が合っていること。ハイコントラスト(黒地)テーマでも確かめる。
   - 空白の表示を ON にして、グリフが空白・タブの位置に出ること。
   - 横スクロールバーが出て、ドラッグで行末まで動くこと。
   - 中ほどで文字をクリックし、クリックした位置にキャレットが来ること。
3. NVDA で、長大行の中ほどで ←/→ を押し、文字の読み上げが変わらないこと(スピーチビューアー)。行の途中で、NVDA のハイライト矩形がキャレットの位置に出ること(目視)。

結果を本書の実施記録に書く。

- [ ] **Step 5: 実施記録を書く**

1. 傘 `2026-09-27-perf-followups-design.md` の §13 の末尾に `### 13.3 実施記録(2026-10-06)` を追記する。内容:
   - 調査の結論(本書 §0.2・§0.3 の要約): 17 と 19 は閉じる(理由と数値)、18 の内訳。
   - 調査で分かったこと(§0.4)と、ユーザーの決定(F-1 の案 B を座標の修正と合わせて本フェーズで行う)。新しい設計書へのリンク。
   - 成果物・計測値(変更前後の中央値)・L5 の結果・レビューの経緯・変異検証の結果。
   - 意図的な挙動差(設計書 §3.6)を、傘の §3.5 の表に足すべき行として記録する。
   - 申し送り(設計書 §6 と、レビューで受容したもの)。
2. 設計書の末尾に `## 7. 実施記録(2026-10-06)` を追記する(計測値・L5・本書からの精密化)。
3. 本書の末尾に実施記録を追記する(Task ごとの結果、Task 4 Step 7 で直したテストの一覧)。

- [ ] **Step 6: Commit**

```bash
git add docs/plans/2026-09-27-perf-followups-design.md docs/plans/2026-10-06-long-row-geometry-design.md docs/plans/2026-10-06-long-line-paint-cost.md
git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -m "docs(perf): フェーズ 9(長大行の描画コスト)の実施記録

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

その後、CLAUDE.md §3 の 5(最終ブランチレビュー 2 パス)・§6・§7 に進む。

## 実施記録(2026-10-07)

- **Task 1**(`eb5fe204`): S11 を足した。変更前の基準値(NVDA 起動中・各 3 回の中央値)は次のとおり。
  - S11a: en 126.2 / 126.8 / 127.5、ja 2,819 / 2,847 / 3,057 ms
  - S11b: en 127.4 / 126.0 / 129.2、ja 2,822 / 2,831 / 3,044 ms
  - S3a: ja 1.417 / 1.469 / 1.429、en 2.296 / 2.348 / 2.311 ms
  - S3b: ja 1.420 / 1.464 / 1.437、en 2.319 / 2.343 / 2.322 ms
  - S7: ja 6.404 / 6.328 / 6.174、en 5.440 / 5.609 / 5.545 ms
- **Task 2**(`d6024ed1`): `MeasureAdditive`。CA1859 がテストの 2 か所で出た。interface の既定実装は具象型からは呼べないので、1 行の `#pragma ... // reason:` で抑止した。
- **Task 3**(`de250e43`): `PixelMapper` の長い行の経路と横スクロールバー。計画からの逸脱が 2 つあり、どちらもコントローラーが承認した。
  - Editor のテストの fixture を `HostForm.CreateVisible()` にした。表示しない Form では横スクロールバーが出ず、修正後も赤のままになるため。
  - 既存の `FrameBuilderSelectionForeTests.Run_at_exactly_the_limit_keeps_the_prefix_difference_width` の期待値を、足し算の値(X = 10*prefixLen・幅 = 10*limit)に直した。
  - 変異 2 個は殺された。殺したテストは次のとおり。
    - `IsLongRow` の `>` → `>=`: `Row_at_exactly_the_threshold_keeps_the_one_shot_measure`
    - `PxToOffset` の `>=` → `>`: `PxToOffset_in_a_long_row_inverts_OffsetToPx`(90000 と 89998)
- **Task 4**(`cfa9db18`、修正 `dc3b8145`): 窓の描画。既存のテストの期待値の変更はない。L5 で、タブを含む長い行の不整合を見つけた(GDI はタブを幅 0 で描くが、足し算はスペース幅で数える)。修正ラウンド 1 で、本文をタブで区切る形に直した。テストを 2 件足し、スコープを絞った再レビューで解消済みと判定された。
- **Task 5**(計測・L5・記録): 変更後の値(各 3 回の中央値)は次のとおり。
  - S11a: en 2.432 / 2.660 / 2.452、ja 9.444 / 8.564 / 8.635 ms
  - S11b: en 3.420 / 2.614 / 2.334、ja 9.117 / 8.546 / 8.363 ms
  - S3a: ja 1.476 / 1.532 / 1.436、en 2.456 / 2.406 / 1.914 ms
  - S3b: ja 1.456 / 1.510 / 1.455、en 2.420 / 2.431 / 1.932 ms
  - S7: ja 6.147 / 6.449 / 6.289、en 5.526 / 5.516 / 5.626 ms
  - L5 の結果は傘 §13.3 のとおり。S11 の変更後の値は、タブの修正の前に採った。S11 の文書にタブはないので、修正の影響はない。
