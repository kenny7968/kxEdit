# フェーズ 3: 無変化時の再描画省略(perf-skip-invalidate) 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development(または executing-plans)で、タスク単位に実装する。

**Goal:** 描画内容が変わらないキャレット移動・選択操作で、全面再描画(ja10k で約 7 ms)をしない。

**設計書:** `docs/plans/2026-09-24-general-perf-improvements-design.md` §3・§8(以下「設計書」)
**調査記録:** `docs/plans/2026-09-24-general-perf-audit.md` §4 P-1(以下「調査記録」)
**前フェーズの計画:** `docs/plans/2026-09-24-perf-paint-cost.md`(`--paint-snapshot` とその申し送り)、`docs/plans/2026-09-25-perf-uia-rects.md`(申し送り)

**Architecture:**
- 描画が読む状態を 1 つの不変値 `FrameInputs` に集め、収集は `CaptureFrameInputs()` の 1 か所に限る。`OnPaint` はその値だけからフレームを組み立てて描き、描き終えた値を `_lastPaintedInputs` に残す。
- キャレット・選択の 4 経路(`EditorControl.Caret.cs` の `SetCaretCharOffset` / `SetSelectionCharRange` / `MoveCaretWithSelection` / `SetSelectionAnchored`)だけ、`Invalidate()` を `InvalidateIfFrameChanged()` に置き換える。今の入力が `_lastPaintedInputs` と等しければ何もしない。
- 本文・フォント・テーマを丸ごと差し替える経路では `_lastPaintedInputs` を捨てる。古いスナップショットやフォントを握り続けないためで、捨てた後の比較は必ず「変化あり」になる(安全側)。
- 検証は 2 本立て。単体テストのオラクル(ランダムな操作列で、省いた時点の「画面の絵」と「今の状態から描いた絵」を画素で比べる)と、Smoke の新しい `--paint-transition`(実画面で、描画を起こさずに撮った絵と、全面を描き直した絵を比べる)。

**Tech Stack:** C# / WinForms(.NET 9)、xUnit、Smoke(kxEdit.Editor.Smoke)、PowerShell 7。

---

## 0. 前提と決定事項

### 0.1 計測の条件(設計書 §3.2・§5.5 の申し送り)

- Smoke `--perf` の **S1・S2**(改善の対象。強調 OFF の既定設定)と、**S3・S7**(変わらないことの確認)で比べる。シナリオの集合は変更前後で `--scenario S1,S2,S3,S7` に固定する。
- 各 3 回、中央値の中央値で比べる。3 回の最小〜最大を揺れとして記録する。`paints_per_op` も記録する(S1・S2 は 1 → 0 になるはず)。
- **NVDA の有無を揃える。** 変更前の計測時の状態を実施記録に書き、変更後も同じ状態で測る。
- 画面のロック・スクリーンセーバー中は測らない(Smoke の自己チェックが EXIT 1 になったら値を捨てる)。
- **perf-harness の M-2 も測る。** 本フェーズは Invalidate(WM_PAINT の投函)を省く変更で、実アプリでは描画がメッセージループで起きる。Smoke は `Update()` で同期的に描かせるので省略の効果は写るが、IME/TSF など kxEdit の外の費用は写らない(設計書 §5.5)。harness は十数分キーボードとマウスを占有し、`%APPDATA%\kxEdit` を退避するので、**実行前にユーザーの了承を取る**。了承が得られなければ Smoke だけで判定し、PR にその旨を書く。

### 0.2 設計書からの精密化

- **`FrameInputs` の型**: 位置指定の record ではなく、`required init` プロパティの `sealed record` にする。
  - 位置指定だと 21 引数のコンストラクタになり、読みにくい(アナライザの引数の数の規則に掛かる可能性もある)。
  - 等値は手書きの `Equals` にする。`Font` は `Equals` を値比較に上書きしているので、既定の record の等値では「破棄済みのフォント」の値比較が走る。**参照で比べる型**(`TextSnapshot`・`GdiCharMetrics`・`Font` 3 つ)と**値で比べる型**(それ以外)を明示的に書き分ける。
  - `BackColor` は `ToArgb()` で比べる。`Color.White`(名前付き)と `FromArgb(255,255,255)` は `Equals` では異なるが、`g.Clear` の結果は同じ画素になる。
- **`FrameInputs` に入れるもの**(設計書 §8.1 の列挙を、実コードで確かめて確定した)

  | メンバー | 出所 | 読む箇所 |
  |---|---|---|
  | `Snapshot` | `_buffer.Current` | 行の組立・FrameBuilder・IME の位置 |
  | `TopLine` / `TopSegment` | `_topLine` / `_topSegment` | 行の組立・IME の位置 |
  | `ScrollX` | `_scrollX` | RenderFrame のシフト・IME の位置 |
  | `WrapColumns` | `_wrapColumns` | 行の組立(`MaxWrapWidthPx` 経由で IME の位置にも効く) |
  | `ClientSize` | `ClientSize` | `g.Clear` の範囲・RenderFrame の右端 |
  | `PaintWidth` / `PaintHeight` | `ClientSize - _vscroll.Width` / `PaintHeightPx`(`_hscroll.Visible` を含む) | FrameBuilder・行の組立 |
  | `ShowLineNumbers` / `LineNumberWidth` | `_showLineNumbers` / `MeasureLineNumberWidth(LineCount)` | FrameBuilder・IME の位置 |
  | `CurrentLineLogical` | `_highlightCurrentLine && !HasSelection ? キャレット行 : -1` | 現在行の背景と行番号の強調 |
  | `Selection` | 選択範囲(なければ null) | 選択の矩形と文字色 |
  | `CellHighlight` | `_cellHighlight` | セル強調 |
  | `ShowWhitespace` | `_showWhitespace` | 空白グリフ |
  | `Style` | `_style` | 全色 |
  | `Metrics` | `_metrics` | 幅と行高 |
  | `Font` / `UnderlineFont` / `TargetFont` | `_font` / `_underlineFontCache` / `_targetFontCache` | 本文・IME の通常節・IME の対象節 |
  | `BackColor` | `BackColor` | `g.Clear` |
  | `Ime` | `_imeCtrl.State`(record struct。配列は参照比較) | IME の未確定表示 |

  入れないもの(描画が読まない): `_hasFocus`・`_caretWidthPx`・`Overtype`・`ReadOnly`・`DesiredXpx`。IME の `CursorPos` は `Ime` の一部として比較に入る(描画は読まないが、比較に入っても再描画が増えるだけで安全側)。
- **IME の未確定表示だけは、`FrameInputs` ではなく生の状態を読む。** `ImeController.Draw` は `IImeOverlayHost` 経由で `ComputeCaretPoint(_ime.Start)` などを読む。これを `FrameInputs` 経由にするには `ImeController` の seam を作り直す必要があり、本フェーズの範囲を超える。
  - 描画は `CaptureFrameInputs()` と同じ同期処理の中で走るので、読む値は `FrameInputs` と一致する。
  - `Draw` が読む状態(`Ime`・`ScrollX`・`ComputeCaretPoint` の入力 = `Snapshot`・`TopLine`・`TopSegment`・`WrapColumns`・`Metrics`・`PaintHeight`・`ShowLineNumbers`・フォント 2 つ・`Style`)は、すべて `FrameInputs` に入っている。
  - 漏れはオラクル(Task 4)が IME の操作を含めて検出する。
- **可視行の組立は `FrameInputs` から行う。** 従来は `BuildVisibleRows(snap, heightPx)` を `OnPaint` と `GetVisibleCharRange` が共有していた(2026-08-22 A-6)。これを `BuildVisibleRows(FrameInputs)` にして、`GetVisibleCharRange` も `CaptureFrameInputs()` を経由させる。共有の約束は保たれ、`heightPx` も共有されるようになる。
- **設計書 §8.4 のオラクルは、フレームではなく画素で比べる。** 設計書は「記録した `Frame` と、生の状態から組み直した `Frame`」を比べる形だった。フレームの比較では、`RenderFrame` のシフトと IME の未確定表示(`Frame` の外で描く)の漏れを検出できない。そこで、「最後に描いた絵」と「今の状態から描いた絵」をビットマップに描いて画素で比べる。フレームの比較を包含する。
- **テストで描かせる手段**: 画面外の HostForm には WM_PAINT が来ない。`OnPaint` と同じ `PaintAndRecord(Graphics)` をビットマップに対して呼ぶテストフック `TestHook_PaintToBitmap(c, record)` を用意する。`record: true` は「WM_PAINT で描いた」、`record: false` は「記録せずに今の状態を描いた(正解)」にあたる。
- **`_lastPaintedInputs` を捨てる経路**: `SetSource`・`ReplaceSource`・`ConvertEols`(非 fast-path)・`UndoEolConversion`・`ApplyAppearance`。設計書は前 3 つを挙げていた。後の 2 つを足したのは、前者が本文の丸ごと差し戻し、後者がフォントと `GdiCharMetrics`(幅メモを抱える)の差し替えで、どちらも古い参照を次の描画まで握るため。いずれも元から無条件に `Invalidate()` しているので、`InvalidateAndForgetPaintedFrame()` にまとめて置き換える。
- **描画が例外で抜けたとき**: `PaintAndRecord` は描く前に `_lastPaintedInputs = null` にし、描き終えてから記録する。途中で投げたら記録は null のまま = 次の比較は必ず「変化あり」になる。
- **WM_PRINT(`DrawToBitmap` / `PrintWindow`)の経路でも記録してよい。** 根拠: 「全面の無効化が保留中でない限り、画面は `_lastPaintedInputs` から描いた絵である。かつ `_lastPaintedInputs` は今の入力と等しい」という不変条件は、描画が読む状態を変えたら必ず `Invalidate()` か `InvalidateIfFrameChanged()` を通すことで保たれる。WM_PRINT で今の入力を記録しても、保留中の無効化は消えず、保留がなければ今の入力 = 画面の入力である。部分的な WM_PAINT(窓の一部が露出したとき)も同じ理由で問題ない。
- **Smoke の遷移比較は `--paint-snapshot` と別のサブコマンド `--paint-transition` にする**(フェーズ 1 の申し送り 1〜3)。`--paint-snapshot` は「撮影の中で全面を描き直す」ことを前提にした道具で、基準画像との比較が目的なので、混ぜない。本文の組立と画素の読取りは `PaintSnapshot` のものを `internal` にして共有する。
- **`_lastFrame` のコメント**を「テスト観測用」に直す(フェーズ 2 の申し送り)。

### 0.3 レビューとミューテーション検証

- 前倒しの脆弱性レビュー: 該当なし(外部入力のパース・パス操作・プロセス起動・WebView・ネットワークに触れない)。
- **前倒しのコード品質レビュー: 2 本**(設計書 §3.4)
  - Task 2(`FrameInputs` の seam と描画経路の置換): フェーズ 9(部分再描画)がこの seam に乗る。
  - Task 5(`--paint-transition`): フェーズ 9 が再利用する検証道具。
- ミューテーション検証: **行わない**。設計書 §3.4 でフェーズ 3 は「禁止」(描画)に入っている。条件付き Invalidate の正しさは、オラクル(Task 4)と遷移比較(Task 5)で担保する。

### 0.4 L5

設計書 §8.5 のとおり**必須**(SR 経路 `EditorControl` のキャレット移動に触れる)。
- 目視: 強調 ON/OFF、選択の開始と解除、IME、スクロール、テーマ変更の直後。`--paint-transition` が自動で見る範囲を、実アプリでも確かめる。判定は PrintWindow ではなく**描画を起こさない撮り方**で行う(PrintWindow は撮影中に全面を描き直すので、古い絵が残る不具合が写らない)。
- NVDA: キャレット移動の読み上げ、ハイライト矩形。
- `tools/sr-regression.ps1`。
- 実機での確認はユーザーに依頼する(Task 8)。windows-mcp で確かめられる範囲は先に行う。

### 0.5 意図的な挙動差

- **なし**(設計書 §3.5 にもフェーズ 3 の行はない)。描かれる絵は変わらず、WM_PAINT の回数が減るだけである。
- 観測できる差として PR に書くもの: キャレット・選択の 4 経路で、フレームが変わらなければ `Control.Invalidated` イベントが発火しない。App 層に `Invalidated` の購読者はない(Task 3 の Step 1 で grep して確かめる)。

---

## Task 0: フェーズ 2 の実施記録を設計書へ追記する(docs のみ)

フェーズ 2(PR #88)はマージ済みで、設計書 §7 の末尾への実施記録の追記が残っている(設計書 §3.1 の 6)。本ブランチの最初の commit にする。

**Files:**
- Modify: `docs/plans/2026-09-24-general-perf-improvements-design.md`(§7.3 の後、§8 の前に「### 7.4 実施記録(2026-09-25・PR #88)」を足す)

**Step 1:** §6.6 と同じ形(成果物・完了条件・本節からの精密化・意図的な挙動差・以後のフェーズへの申し送り)で書く。材料は `docs/plans/2026-09-25-perf-uia-rects.md` の実施記録。要点:
- 成果物: S-1(座標を問い合わせ時に求める。`TryGetClientOrigin` に集約)、P-9 (a)(b)(c)。
- 完了条件: S8 全文 3,975.92 → 0.58 ms、M-7 全文 2.8〜3.3 秒 → 2.6〜2.9 ms。S1〜S3 は揺れの範囲。ミューテーションのスポットチェック(M1〜M3・M6〜M8 は殺された。M4・M5・M9 は等価変異)。L5 は windows-mcp で可能な範囲を実施。視覚的ハイライトとマウス追従の実発声はユーザー判断で省略。
- 精密化: (c) の条件は `maxWidthPx <= 0`。脆弱性パスの I-1 で、原点の取得を Compute 側も `_hwnd` + `ClientToScreen` にした(§7.1 の「その場で `PointToScreen`」からの逸脱)。
- 以後への申し送り: フェーズ 3 の `_lastFrame` のコメント(本計画で回収)、折り返し OFF の古い `_topSegment` の食い違い(割り当てなし)、`IsHandleCreated` と `InvokeRequired` の間の競合(割り当てなし)。

**Step 2: Commit**

```powershell
git add docs/plans/2026-09-24-general-perf-improvements-design.md
git commit -m "docs(perf): 設計書 §7 にフェーズ 2 の実施記録を追記"
```

**Step 3: 本計画を commit する**

```powershell
git add docs/plans/2026-09-25-perf-skip-invalidate.md
git commit -m "docs(perf): フェーズ 3(無変化時の再描画省略)の実装計画"
```

---

## Task 1: 変更前の計測(src は未変更)

**Files:** なし(結果は本書の実施記録に書く。生の JSON / CSV は scratchpad に置き、リポジトリに入れない)

**Step 1: NVDA の状態を記録する**

```powershell
Get-Process nvda -ErrorAction SilentlyContinue | Select-Object Id, StartTime
```

**Step 2: Smoke `--perf` を 3 回**

```powershell
$s = "<scratchpad>\perf"
New-Item -ItemType Directory -Force $s | Out-Null
foreach ($i in 1..3) {
  dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --perf --scenario S1,S2,S3,S7 --json "$s\before-$i.json"
}
```
Expected: 各回 EXIT 0。EXIT 1(自己チェック失敗)の回は捨てて取り直す。

**Step 3: 中央値を集計する**(`paints_per_op` も出す)

```powershell
$rows = foreach ($f in Get-ChildItem "$s\before-*.json") {
  (Get-Content $f -Raw -Encoding utf8 | ConvertFrom-Json).results |
    Select-Object id, doc, param, median_ms, paints_per_op
}
$rows | Group-Object id, doc, param | ForEach-Object {
  $m = $_.Group.median_ms | Sort-Object
  [pscustomobject]@{ key = $_.Name; min = $m[0]; mid = $m[1]; max = $m[2]; paints = ($_.Group.paints_per_op | Select-Object -First 1) }
} | Format-Table -AutoSize
```

**Step 4(ユーザーの了承後): perf-harness の M-2 を 3 回**

```powershell
dotnet publish src/kxEdit.App -c Release -o "<scratchpad>\pub-before"
foreach ($i in 1..3) { pwsh -File tools/perf-harness.ps1 -PublishDir "<scratchpad>\pub-before" -Scenario M-2 }
```
CSV の `env` 行で NVDA の有無と自己チェック(`status`)を確かめる。見る行は「→←の交互」「↓↑の交互」「Shift+→」「基準:全面の再描画」「基準:Shiftの単押し」。

**Step 5:** 集計値(min / 中央 / max)と NVDA の状態を、本書末尾の実施記録に書いてコミットする。

```powershell
git add docs/plans/2026-09-25-perf-skip-invalidate.md
git commit -m "docs(perf): フェーズ 3 の変更前の計測値を記録"
```

---

## Task 2: `FrameInputs` の seam と描画経路の置換(挙動不変)

描画の入力を 1 か所で集め、`OnPaint` がその値だけから描く形にする。**この Task では Invalidate を省かない**(描かれる絵も回数も不変)。

**Files:**
- Create: `src/kxEdit.Editor/FrameInputs.cs`
- Modify: `src/kxEdit.Editor/EditorControl.Paint.cs`(`OnPaint` / `PaintBody` / `RenderFrame`、テストフック)
- Modify: `src/kxEdit.Editor/EditorControl.cs`(`_lastPaintedInputs` の宣言、`BuildVisibleRows`・`GetVisibleCharRange`、`_lastFrame` のコメント `:121-123`)
- Create: `tests/kxEdit.Editor.Tests/FrameInputsTests.cs`

**Step 1: 失敗するテストを書く**

`tests/kxEdit.Editor.Tests/FrameInputsTests.cs`:

```csharp
using System.Drawing;
using System.Reflection;
using kxEdit.Core.Buffers;
using kxEdit.Core.Editing;
using kxEdit.Core.Layout;

namespace kxEdit.Editor.Tests;

/// <summary>
/// 2026-09-25 性能改善フェーズ 3(設計書 §8.1・§8.4 の網羅性)。<see cref="FrameInputs"/> の
/// メンバーが列挙どおりであることと、各メンバーの違いが等値比較で「変化あり」になることを固定する。
/// メンバーを足したら <see cref="ExpectedMembers"/> と <see cref="Different"/> の両方を直すこと
/// (片方だけだと 1 本目か 2 本目が赤くなる)。
/// </summary>
public class FrameInputsTests
{
    private static readonly string[] ExpectedMembers =
    [
        "Snapshot",
        "TopLine",
        "TopSegment",
        "ScrollX",
        "WrapColumns",
        "ClientSize",
        "PaintWidth",
        "PaintHeight",
        "ShowLineNumbers",
        "LineNumberWidth",
        "CurrentLineLogical",
        "Selection",
        "CellHighlight",
        "ShowWhitespace",
        "Style",
        "Metrics",
        "Font",
        "UnderlineFont",
        "TargetFont",
        "BackColor",
        "Ime",
    ];

    public static TheoryData<string> Members() => [.. ExpectedMembers];

    /// <summary>テスト全体で使い回すフォントと幅メモ(GDI 資源なので作り直さない)。</summary>
    private static readonly Font s_font = new("ＭＳ ゴシック", 12f);
    private static readonly Font s_underline = new(s_font, FontStyle.Underline);
    private static readonly Font s_target = new(s_font, FontStyle.Underline | FontStyle.Bold);

    private static FrameInputs Base(GdiCharMetrics metrics) =>
        new()
        {
            Snapshot = TextBuffer.FromString("abc\r\ndef").Current,
            TopLine = 3,
            TopSegment = 1,
            ScrollX = 5,
            WrapColumns = 0,
            ClientSize = new Size(300, 200),
            PaintWidth = 283,
            PaintHeight = 200,
            ShowLineNumbers = true,
            LineNumberWidth = 28,
            CurrentLineLogical = 4,
            Selection = new SelectionRange(1, 3),
            CellHighlight = null,
            ShowWhitespace = false,
            Style = new ViewportStyle(
                new PaintColor(0x000000),
                new PaintColor(0xFFFFFF),
                new PaintColor(0xF0F0F0),
                new PaintColor(0xADD8E6),
                null,
                new PaintColor(0x777777),
                new PaintColor(0xD77800),
                new PaintColor(0xCCCCCC)
            ),
            Metrics = metrics,
            Font = s_font,
            UnderlineFont = s_underline,
            TargetFont = s_target,
            BackColor = Color.White,
            Ime = ImeCompositionState.Empty,
        };

    /// <summary>メンバーの値を「描画が変わりうる別の値」にする。参照で比べる型は、値が同じ別インスタンスにする。</summary>
    private static object? Different(object? value) =>
        value switch
        {
            int i => i + 1,
            bool b => !b,
            Size s => new Size(s.Width + 1, s.Height),
            Color c => Color.FromArgb(c.ToArgb() ^ 0x000001),
            TextSnapshot => TextBuffer.FromString("abc\r\ndef").Current, // 同じ本文・別の参照
            SelectionRange r => new SelectionRange(r.Start, r.End + 1),
            null => new SelectionRange(0, 1),
            ViewportStyle st => st with { Foreground = new PaintColor(st.Foreground.Rgb ^ 1) },
            GdiCharMetrics => new GdiCharMetrics(s_font), // 同じフォント・別の参照
            Font f => new Font(f, f.Style), // 同じ値・別の参照
            ImeCompositionState ime => ime with { Text = ime.Text + "あ" },
            _ => throw new InvalidOperationException($"未対応の型: {value.GetType()}"),
        };

    [Fact]
    public void Members_are_exactly_the_enumerated_inputs()
    {
        var names = typeof(FrameInputs)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(ExpectedMembers.Order(StringComparer.Ordinal), names);
    }

    [Fact]
    public void Same_values_and_same_references_are_equal() =>
        Sta.Run(() =>
        {
            var metrics = new GdiCharMetrics(s_font);
            var a = Base(metrics);
            var b = a with { }; // 全メンバーをそのまま写す
            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        });

    [Theory]
    [MemberData(nameof(Members))]
    public void A_difference_in_any_member_is_a_change(string member) =>
        Sta.Run(() =>
        {
            var metrics = new GdiCharMetrics(s_font);
            var a = Base(metrics);
            var prop = typeof(FrameInputs).GetProperty(member)!;
            var b = a with { };
            prop.SetValue(b, Different(prop.GetValue(a))); // init アクセサはリフレクションから呼べる
            Assert.NotEqual(a, b);
        });

    /// <summary>ViewportStyle は record の値比較(ApplyAppearance で同じテーマを作り直しても「変化なし」)。</summary>
    [Fact]
    public void Style_is_compared_by_value() =>
        Sta.Run(() =>
        {
            var a = Base(new GdiCharMetrics(s_font));
            var b = a with { Style = a.Style with { } };
            Assert.NotSame(a.Style, b.Style);
            Assert.Equal(a, b);
        });

    /// <summary>BackColor は画素の値で比べる(名前付きの White と、同じ ARGB の無名色は同じ絵になる)。</summary>
    [Fact]
    public void BackColor_is_compared_by_argb() =>
        Sta.Run(() =>
        {
            var a = Base(new GdiCharMetrics(s_font));
            var b = a with { BackColor = Color.FromArgb(255, 255, 255, 255) };
            Assert.NotEqual(Color.White, b.BackColor); // 前提: Color.Equals では異なる
            Assert.Equal(a, b);
        });

    /// <summary>未確定の配列は参照で比べる(打鍵ごとに新しい配列 = 「変化あり」= 安全側)。</summary>
    [Fact]
    public void Ime_arrays_are_compared_by_reference() =>
        Sta.Run(() =>
        {
            var a = Base(new GdiCharMetrics(s_font)) with
            {
                Ime = new ImeCompositionState(0, "かな", 2, [0, 0], [0, 2]),
            };
            var b = a with { Ime = a.Ime with { Attrs = [0, 0] } };
            Assert.NotEqual(a, b);
        });
}
```

`ImeCompositionState` のコンストラクタの引数順は `src/kxEdit.Core/Editing/ImeCompositionState.cs` で確かめて合わせる(`Start, Text, CursorPos, Attrs, Clauses` の想定)。

**Step 2: 失敗を確かめる**

```powershell
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~FrameInputsTests"
```
Expected: ビルドエラー(`FrameInputs` がない)。

**Step 3: `FrameInputs` を作る**

`src/kxEdit.Editor/FrameInputs.cs`:

```csharp
// FrameInputs.cs
// 2026-09-25 性能改善フェーズ 3(設計書 §8.1): 描画が読む状態の全部を 1 つの不変値に集めたもの。
// EditorControl.CaptureFrameInputs() だけが作り、OnPaint はこの値だけからフレームを組み立てて描く。
// 描画に新しい入力を足すときは、必ずここにメンバーを足し、Equals と FrameInputsTests も直すこと
// (足さずに描画から生の状態を読むと、キャレット移動で再描画を省いたときに古い絵が画面に残る)。
using System.Drawing;
using kxEdit.Core.Buffers;
using kxEdit.Core.Editing;
using kxEdit.Core.Layout;

namespace kxEdit.Editor;

/// <summary>
/// 1 回の描画の入力(設計書 §8.1)。等しい 2 つの値からは、同じ絵が描かれる。
/// </summary>
/// <remarks>
/// <para>
/// <b>比べ方</b>: <see cref="Snapshot"/>・<see cref="Metrics"/>・フォント 3 つは<b>参照</b>で比べる
/// (スナップショットは編集ごとに新しい参照になる。<see cref="Font"/> の <c>Equals</c> は値比較で、
/// 破棄済みのフォントに対しても走ってしまうので使わない)。<see cref="BackColor"/> は ARGB で比べる
/// (<c>Color.Equals</c> は名前の有無まで見るが、<c>g.Clear</c> の画素は同じ)。それ以外は値で比べる。
/// <see cref="Ime"/> は record struct の既定の等値で、配列(Attrs / Clauses)は参照比較になる
/// =打鍵ごとに「変化あり」(安全側)。
/// </para>
/// <para>
/// <b>IME の未確定表示だけは例外</b>: <see cref="ImeController.Draw"/> は host 経由で生の状態を読む。
/// 描画は <c>CaptureFrameInputs()</c> と同じ同期処理の中で走るので値は一致し、読む状態
/// (<see cref="Ime"/>・<see cref="ScrollX"/>・<c>ComputeCaretPoint</c> の入力・フォント・<see cref="Style"/>)は
/// すべてここに入っている(実装計画 docs/plans/2026-09-25-perf-skip-invalidate.md §0.2)。
/// </para>
/// </remarks>
internal sealed record FrameInputs
{
    public required TextSnapshot Snapshot { get; init; }
    public required int TopLine { get; init; }
    public required int TopSegment { get; init; }
    public required int ScrollX { get; init; }
    public required int WrapColumns { get; init; }

    /// <summary><c>g.Clear</c> の範囲と RenderFrame の右端(スクロールバーを引く前)。</summary>
    public required Size ClientSize { get; init; }

    public required int PaintWidth { get; init; }
    public required int PaintHeight { get; init; }
    public required bool ShowLineNumbers { get; init; }

    /// <summary>行番号の非表示時は 0。</summary>
    public required int LineNumberWidth { get; init; }

    /// <summary>現在行の強調が効く論理行。強調 OFF・選択中は -1。</summary>
    public required int CurrentLineLogical { get; init; }

    public required SelectionRange? Selection { get; init; }
    public required SelectionRange? CellHighlight { get; init; }
    public required bool ShowWhitespace { get; init; }
    public required ViewportStyle Style { get; init; }
    public required GdiCharMetrics Metrics { get; init; }
    public required Font Font { get; init; }
    public required Font UnderlineFont { get; init; }
    public required Font TargetFont { get; init; }
    public required Color BackColor { get; init; }
    public required ImeCompositionState Ime { get; init; }

    public bool Equals(FrameInputs? other) =>
        other is not null
        && ReferenceEquals(Snapshot, other.Snapshot)
        && TopLine == other.TopLine
        && TopSegment == other.TopSegment
        && ScrollX == other.ScrollX
        && WrapColumns == other.WrapColumns
        && ClientSize == other.ClientSize
        && PaintWidth == other.PaintWidth
        && PaintHeight == other.PaintHeight
        && ShowLineNumbers == other.ShowLineNumbers
        && LineNumberWidth == other.LineNumberWidth
        && CurrentLineLogical == other.CurrentLineLogical
        && Selection == other.Selection
        && CellHighlight == other.CellHighlight
        && ShowWhitespace == other.ShowWhitespace
        && Style.Equals(other.Style)
        && ReferenceEquals(Metrics, other.Metrics)
        && ReferenceEquals(Font, other.Font)
        && ReferenceEquals(UnderlineFont, other.UnderlineFont)
        && ReferenceEquals(TargetFont, other.TargetFont)
        && BackColor.ToArgb() == other.BackColor.ToArgb()
        && Ime.Equals(other.Ime);

    // 等しい値は同じハッシュになる(Equals が見るメンバーの部分集合から作る)。
    public override int GetHashCode() =>
        HashCode.Combine(
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Snapshot),
            TopLine,
            TopSegment,
            ScrollX,
            CurrentLineLogical,
            Selection
        );
}
```

アナライザ(Sonar の式の複雑さ等)に掛かったら、`docs/lint-format-setup.md` の抑止規約に従う(理由をコメントに書いて局所抑止)。`&&` を分割して読みにくくしない。

**Step 4: テストが通ることを確かめる**

```powershell
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~FrameInputsTests"
```
Expected: PASS(Theory 21 件を含む)。

**Step 5: 描画経路を `FrameInputs` に載せ替える**

`src/kxEdit.Editor/EditorControl.cs`:
- `_lastFrame` の宣言(`:121-123`)の近くに足す:

```csharp
    // 2026-09-25 性能改善フェーズ 3(設計書 §8.2): 最後に描き終えたフレームの入力。
    // キャレット・選択の 4 経路は、今の入力がこれと等しければ Invalidate を省く(InvalidateIfFrameChanged)。
    // null = 「画面の絵の入力が分からない」= 比較は必ず「変化あり」になる(描画前・描画の例外・
    // 本文/フォントの丸ごと差し替え = InvalidateAndForgetPaintedFrame)。UI スレッド専用。
    private FrameInputs? _lastPaintedInputs;
```

- `_lastFrame` のコメントを直す(フェーズ 2 の申し送り):

```csharp
    // _lastFrame は最後に描いた Frame(テスト観測用。TestHook_GetLastFrame が読む)。
    // フェーズ 2(S-1)以降、UIA の座標 API は問い合わせのたびに求めるので、これを読まない
    // (UiaTextHostAdapter_HasNoScreenCoordinateCache で固定)。描画を省いても UIA には影響しない。
```

- `BuildVisibleRows` と `GetVisibleCharRange`(`:403-440`)を置き換える:

```csharp
    /// <summary>
    /// 可視の視覚行を列挙する唯一の入口。描画(<c>PaintBody</c>)と <see cref="GetVisibleCharRange"/> が
    /// 同じ起点 (TopLine, TopSegment)・同じ折り返し設定・同じ可視高さを使うことを、言葉の約束ではなく
    /// 呼び出しの共有で保証する(「どこまで見えているか」の定義を二重化しない。2026-08-22 A-6)。
    /// </summary>
    /// <remarks>
    /// 2026-09-25 フェーズ 3: 入力を <see cref="FrameInputs"/> から取る形にした。両者とも
    /// <see cref="CaptureFrameInputs"/> を経由するので、従来は共有していなかった可視高さ
    /// (<see cref="PaintHeightPx"/>)も共有される。<c>UpdateHorizontalScrollbar</c> は<b>本ヘルパを使わない</b>=
    /// 折り返し OFF 専用で topSegment が 0 固定の別経路であり、起点の意味が違う。
    /// </remarks>
    private static IReadOnlyList<VisualRow> BuildVisibleRows(FrameInputs inputs) =>
        ViewportLayout.Build(
            inputs.Snapshot,
            inputs.TopLine,
            inputs.TopSegment,
            inputs.PaintHeight,
            inputs.WrapColumns,
            inputs.Metrics
        );

    // (GetVisibleCharRange の doc は「描画と同じ BuildVisibleRows と PaintHeightPx」を
    //  「描画と同じ CaptureFrameInputs と BuildVisibleRows」に直す)
    internal (int Start, int End) GetVisibleCharRange()
    {
        var inputs = CaptureFrameInputs();
        if (inputs is null)
            return (0, 0);
        var rows = BuildVisibleRows(inputs);
        if (rows.Count == 0)
            return (0, 0);
        var first = rows[0];
        var last = rows[rows.Count - 1];
        return (first.SegmentStartChar, last.SegmentStartChar + last.SegmentLength);
    }
```

`src/kxEdit.Editor/EditorControl.Paint.cs`:
- `OnPaint` の `PaintBody(e.Graphics)` / `PaintBody(buffer.Graphics)` を `PaintAndRecord(...)` に替える。
- `PaintBody` を次の 3 つに分ける(コメントの `g.Clear` の説明・`Control.ClientSize` の説明・`PaintHeightPx` の説明・選択中は現在行強調を抑止する説明・IME の順序の説明は、移った先に残す):

```csharp
    /// <summary>
    /// 今の状態を集めて描き、描き終えた入力を <see cref="_lastPaintedInputs"/> に記録する(OnPaint の本体)。
    /// 描く前に記録を捨てる=描画が例外で抜けたら記録は null のまま(次の比較は必ず「変化あり」)。
    /// WM_PRINT 経由(DrawToBitmap / PrintWindow)でも記録してよい根拠は実装計画 §0.2。
    /// </summary>
    private void PaintAndRecord(Graphics g)
    {
        var inputs = CaptureFrameInputs();
        _lastPaintedInputs = null;
        PaintBody(g, inputs);
        _lastPaintedInputs = inputs;
    }

    /// <summary>
    /// 描画が読む状態を集める唯一の場所(設計書 §8.1)。SetSource 前は null。
    /// 描画(<see cref="PaintBody"/>)は、ここで集めた値<b>だけ</b>を使う
    /// (例外は IME の未確定表示。<see cref="FrameInputs"/> の remarks を参照)。
    /// </summary>
    private FrameInputs? CaptureFrameInputs()
    {
        if (_buffer is null)
            return null;
        var snap = _buffer.Current;
        // 選択がある間は現在行強調を抑止する(選択矩形と重ねるとハイライトが二重になり
        // 視覚的に読みにくいため=EditorControl 層の責務)。
        bool hasSelection = _caretCtrl.HasSelection;
        SelectionRange? selection = null;
        if (hasSelection)
        {
            var (selS, selE) = GetSelectionCharRange();
            selection = new SelectionRange(selS, selE);
        }
        var clientSize = ClientSize;
        return new FrameInputs
        {
            Snapshot = snap,
            TopLine = _topLine,
            TopSegment = _topSegment,
            ScrollX = _scrollX,
            WrapColumns = _wrapColumns,
            ClientSize = clientSize,
            // Control.ClientSize は docked 子コントロールを引かないため、VScrollBar 幅を明示的に減算。
            PaintWidth = Math.Max(0, clientSize.Width - _vscroll.Width),
            // 可視高さの定義は PaintHeightPx に一本化する(UIA の GetVisibleCharRange / BringCaretIntoView /
            // ScrollCharRangeIntoView と「どこまで見えているか」の定義を食い違わせない)。
            PaintHeight = PaintHeightPx,
            ShowLineNumbers = _showLineNumbers,
            LineNumberWidth = _showLineNumbers ? MeasureLineNumberWidth(snap.LineCount) : 0,
            CurrentLineLogical =
                (_highlightCurrentLine && !hasSelection)
                    ? snap.GetLineIndexOfChar(_caretCtrl.Caret)
                    : -1,
            Selection = selection,
            CellHighlight = _cellHighlight,
            ShowWhitespace = _showWhitespace,
            Style = _style,
            Metrics = _metrics,
            Font = _font,
            UnderlineFont = _underlineFontCache,
            TargetFont = _targetFontCache,
            BackColor = BackColor,
            Ime = _imeCtrl.State,
        };
    }

    private void PaintBody(Graphics g, FrameInputs? inputs)
    {
        // (既存の g.Clear の説明コメントをここに残す)
        // SetSource 前(inputs が null)は BackColor で塗るだけ。
        g.Clear(inputs?.BackColor ?? BackColor);
        if (inputs is null)
            return;
        var frame = FrameBuilder.Build(
            inputs.Snapshot,
            BuildVisibleRows(inputs),
            inputs.PaintWidth,
            inputs.PaintHeight,
            inputs.LineNumberWidth,
            inputs.CurrentLineLogical,
            inputs.Selection,
            inputs.CellHighlight,
            inputs.ShowWhitespace,
            inputs.Style,
            inputs.Metrics
        );
        RenderFrame(g, frame, inputs.ScrollX, inputs.Font);

        // (既存の IME overlay の順序の説明コメントを残す)
        // ImeController.Draw は host 経由で生の状態を読む(FrameInputs の remarks)。
        if (inputs.Ime.IsActive)
            _imeCtrl.Draw(g);

        // テスト観測用(TestHook_GetLastFrame)。
        _lastFrame = frame;
    }
```

- `RenderFrame(Graphics g, Frame frame)` を `RenderFrame(Graphics g, Frame frame, int scrollX, Font font)` にし、本体の `_scrollX` → `scrollX`、`_font` → `font` に替える(doc の `_scrollX` の説明も合わせる)。
- テストフックを足す(`TestHook_ViewportStyle` の近く):

```csharp
    /// <summary>
    /// テスト専用: クライアント領域の大きさのビットマップに、OnPaint と同じ経路で描く。
    /// <paramref name="record"/> が true なら「WM_PAINT で描いた」扱いで <see cref="_lastPaintedInputs"/> を
    /// 記録する(<see cref="PaintAndRecord"/>)。false なら記録せずに今の状態を描く(オラクルの正解)。
    /// 画面外の HostForm には WM_PAINT が来ないので、描画とその記録はこれで同期的に起こす。
    /// </summary>
    internal static Bitmap TestHook_PaintToBitmap(EditorControl c, bool record)
    {
        var size = c.ClientSize;
        var bmp = new Bitmap(
            Math.Max(1, size.Width),
            Math.Max(1, size.Height),
            System.Drawing.Imaging.PixelFormat.Format32bppArgb
        );
        using var g = Graphics.FromImage(bmp);
        if (record)
            c.PaintAndRecord(g);
        else
            c.PaintBody(g, c.CaptureFrameInputs());
        return bmp;
    }

    /// <summary>テスト専用: 最後に描いたフレームの入力を持っているか。</summary>
    internal static bool TestHook_HasLastPaintedInputs(EditorControl c) =>
        c._lastPaintedInputs is not null;
```

**Step 6: 全テストとピクセル不変を確かめる**

```powershell
dotnet build kxEdit.sln -c Release
dotnet test tests/kxEdit.Editor.Tests
```
Expected: 0 warning・全 PASS。

描画処理のピクセル不変(変更前の main で基準を撮り、変更後と比べる。道具は同じ版):

```powershell
git stash -u   # 作業中の変更を退避(または main の worktree を使う)
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --paint-snapshot "<scratchpad>\snap-base"
git stash pop
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --paint-snapshot "<scratchpad>\snap-task2" --compare "<scratchpad>\snap-base"
```
Expected: 「16 枚すべて一致 EXIT 0」。

**Step 7: Commit**

```powershell
git add src/kxEdit.Editor/FrameInputs.cs src/kxEdit.Editor/EditorControl.cs src/kxEdit.Editor/EditorControl.Paint.cs tests/kxEdit.Editor.Tests/FrameInputsTests.cs
git commit -m "refactor(editor): 描画の入力を FrameInputs に集め、OnPaint はその値だけから描く"
```

**Step 8: 仕様レビュー → コード品質の前倒しレビュー**(別エージェント。§0.3)

観点: 描画が `FrameInputs` の外の状態を読んでいないか(`PaintBody` / `RenderFrame` / `FrameBuilder.Build` の引数 / `ImeController.Draw` が host から読むもの)、等値の書き分け(参照・値・ARGB)、`GetVisibleCharRange` の共有が保たれているか、フェーズ 9(部分再描画)がこの seam に乗れる形か。指摘は fixup commit で反映する。

---

## Task 3: 条件付き Invalidate と、記録を捨てる経路

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.Caret.cs:170/214/246/281`
- Modify: `src/kxEdit.Editor/EditorControl.Paint.cs`(`InvalidateIfFrameChanged` / `InvalidateAndForgetPaintedFrame`)
- Modify: `src/kxEdit.Editor/EditorControl.cs`(`SetSource:245`・`ReplaceSource:314`・`ConvertEols:659`・`UndoEolConversion:1794`・`ApplyAppearance:2816`)
- Create: `tests/kxEdit.Editor.Tests/EditorControlSkipInvalidateTests.cs`

**Step 1: 前提を確かめる(grep)**

```powershell
# App 層に Invalidated の購読者がないこと(§0.5)
rg -n "\.Invalidated\s*\+=" src
# キャレット・選択を _caretCtrl で直接動かして、Invalidate も AfterEdit も通らない経路がないこと
rg -n "_caretCtrl\.(SetTo|MoveTo|SetSelection)|ctx\.Caret\.(SetTo|MoveTo|SetSelection)" src/kxEdit.Editor
```
Expected: 1 本目は 0 件。2 本目は、各ヒットの直後に `AfterEdit()`・`Invalidate()`・4 経路のいずれか、または「元に戻す」(`EnsureVisibleCharRange` の finally)があること。なければ実施記録に書いてユーザーに相談する。

**Step 2: 失敗するテストを書く**

`tests/kxEdit.Editor.Tests/EditorControlSkipInvalidateTests.cs`:

```csharp
using System.Drawing;
using kxEdit.Core.Buffers;
using kxEdit.Core.Settings;
using kxEdit.Core.Text;

namespace kxEdit.Editor.Tests;

/// <summary>
/// 2026-09-25 性能改善フェーズ 3(設計書 §8.2・§8.4 の表)。キャレット・選択の 4 経路は、
/// フレームが変わらなければ Invalidate しない。数えるのは Control.Invalidated イベント
/// (先例 = EditorControlConvertEolsTests.UndoEolConversion_InvalidatesOnce)。
/// ホストは非フォーカス = PositionCaret が走らない。描画は TestHook_PaintToBitmap(record: true) で起こす
/// (画面外の窓には WM_PAINT が来ないため)。
/// 「0 回」のテストは、既定位置(0)ではない位置から始め、操作の前に「描画の記録がある」
/// 「キャレットが実際に動いた」を確かめる(CLAUDE.md §4-B = 前提と発火条件を一致させる)。
/// </summary>
public class EditorControlSkipInvalidateTests
{
    // 30 行・400×200 = 可視はおよそ 9〜10 行。行 25 は可視域の外。
    private static string Body() =>
        string.Join("\r\n", Enumerable.Range(0, 30).Select(i => $"line {i:D2} あいう abc"));

    private static (Form F, EditorControl C) MakeHosted()
    {
        var f = new Form { Size = new Size(400, 200) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(Body()));
        return (f, c);
    }

    private static int Line(EditorControl c, int line) => c.CurrentBuffer.Current.GetLineStart(line);

    private static int CountInvalidations(EditorControl c, Action act)
    {
        int n = 0;
        InvalidateEventHandler h = (_, _) => n++;
        c.Invalidated += h;
        try
        {
            act();
        }
        finally
        {
            c.Invalidated -= h;
        }
        return n;
    }

    private static void Paint(EditorControl c) =>
        EditorControl.TestHook_PaintToBitmap(c, record: true).Dispose();

    /// <summary>前提: 描画の記録がある状態から始める(記録がなければ比較は必ず「変化あり」)。</summary>
    private static void PaintAndAssumeRecorded(EditorControl c)
    {
        Paint(c);
        Assert.True(EditorControl.TestHook_HasLastPaintedInputs(c), "前提: 描画が記録されていない");
    }

    [Fact]
    public void CaretMove_WithoutHighlight_DoesNotInvalidate() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 2) + 3); // 非既定位置から
                PaintAndAssumeRecorded(c);
                int target = Line(c, 4) + 1;

                int n = CountInvalidations(c, () => c.SetCaretCharOffset(target));

                Assert.Equal(target, c.CaretCharOffset); // 前提: 早期 return していない
                Assert.Equal(0, c.TopLine); // 前提: スクロールしていない
                Assert.Equal(0, n);
            }
        });

    [Fact]
    public void CaretMove_BeforeAnyPaint_Invalidates() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                Assert.False(EditorControl.TestHook_HasLastPaintedInputs(c)); // 前提
                int n = CountInvalidations(c, () => c.SetCaretCharOffset(Line(c, 1) + 2));
                Assert.True(n >= 1);
            }
        });

    [Fact]
    public void CaretMove_WithHighlight_ToAnotherLine_Invalidates() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.HighlightCurrentLine = true;
                c.SetCaretCharOffset(Line(c, 2) + 3);
                PaintAndAssumeRecorded(c);
                int n = CountInvalidations(c, () => c.SetCaretCharOffset(Line(c, 3) + 3));
                Assert.True(n >= 1);
            }
        });

    [Fact]
    public void CaretMove_WithHighlight_WithinTheLine_DoesNotInvalidate() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.HighlightCurrentLine = true;
                c.SetCaretCharOffset(Line(c, 2) + 3);
                PaintAndAssumeRecorded(c);
                int target = Line(c, 2) + 5;
                int n = CountInvalidations(c, () => c.SetCaretCharOffset(target));
                Assert.Equal(target, c.CaretCharOffset);
                Assert.Equal(0, n);
            }
        });

    [Fact]
    public void SelectionChange_Invalidates_OnAllSelectingPaths() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 2) + 3);
                PaintAndAssumeRecorded(c);
                Assert.True(CountInvalidations(c, () => c.MoveCaretWithSelection(Line(c, 2) + 6)) >= 1);
                Paint(c);
                Assert.True(CountInvalidations(c, () => c.SetSelectionCharRange(Line(c, 1), Line(c, 3))) >= 1);
                Paint(c);
                Assert.True(CountInvalidations(c, () => c.SetSelectionAnchored(Line(c, 4), Line(c, 1))) >= 1);
            }
        });

    [Fact]
    public void SelectionClear_Invalidates() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetSelectionCharRange(Line(c, 1) + 1, Line(c, 2) + 4);
                PaintAndAssumeRecorded(c);
                int n = CountInvalidations(c, () => c.SetCaretCharOffset(Line(c, 2) + 4));
                Assert.Equal(c.SelectionAnchor, c.CaretCharOffset); // 前提: 選択が消えた
                Assert.True(n >= 1);
            }
        });

    /// <summary>範囲指定の 2 経路でも、空の選択(= 単なるキャレット移動)ならフレームは変わらない。</summary>
    [Fact]
    public void CollapsedSelection_OnRangePaths_DoesNotInvalidate() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 2) + 3);
                PaintAndAssumeRecorded(c);
                int p1 = Line(c, 3) + 2;
                Assert.Equal(0, CountInvalidations(c, () => c.SetSelectionCharRange(p1, p1)));
                Assert.Equal(p1, c.CaretCharOffset);
                int p2 = Line(c, 5) + 1;
                Assert.Equal(0, CountInvalidations(c, () => c.SetSelectionAnchored(p2, p2)));
                Assert.Equal(p2, c.CaretCharOffset);
            }
        });

    /// <summary>
    /// スクロールを伴う移動: スクロールのセッターが無条件に 1 回、InvalidateIfFrameChanged が TopLine の
    /// 違いを見てもう 1 回(設計書 §8.4。変更前も 2 回)。
    /// </summary>
    [Fact]
    public void CaretMove_WithScroll_InvalidatesAtMostTwice() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 2) + 3);
                PaintAndAssumeRecorded(c);
                int n = CountInvalidations(c, () => c.SetCaretCharOffset(Line(c, 25)));
                Assert.True(c.TopLine > 0, "前提: スクロールしていない");
                Assert.InRange(n, 1, 2);
            }
        });

    [Fact]
    public void CaretMove_DuringComposition_Invalidates() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(Line(c, 2) + 3);
                c.__TestApplyComposition("かな", 2, [0, 0], []);
                PaintAndAssumeRecorded(c);
                int n = CountInvalidations(c, () => c.SetCaretCharOffset(Line(c, 4)));
                Assert.False(c.__TestIsComposing()); // 前提: 移動で未確定が取り消された
                Assert.True(n >= 1);
            }
        });

    // ---- 記録を捨てる経路(設計書 §8.2 と実装計画 §0.2) ----

    public static TheoryData<string> ForgettingPaths() =>
        ["ReplaceSource", "ConvertEols", "UndoEolConversion", "ApplyAppearance"];

    [Theory]
    [MemberData(nameof(ForgettingPaths))]
    public void WholesaleReplacement_ForgetsThePaintedFrame(string path) =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                bool recorded = path == "UndoEolConversion" && c.ConvertEols(LineEnding.Lf);
                PaintAndAssumeRecorded(c);
                switch (path)
                {
                    case "ReplaceSource":
                        c.ReplaceSource(TextBuffer.FromString("x"));
                        break;
                    case "ConvertEols":
                        Assert.True(c.ConvertEols(LineEnding.Lf)); // 前提: 非 fast-path(本文は CRLF)
                        break;
                    case "UndoEolConversion":
                        Assert.True(recorded); // 前提
                        Assert.True(c.UndoEolConversion(recorded, 0, 0));
                        break;
                    case "ApplyAppearance":
                        c.ApplyAppearance(new AppSettings());
                        break;
                }
                Assert.False(EditorControl.TestHook_HasLastPaintedInputs(c));
            }
        });
}
```

`LineEnding` の名前空間と `AppSettings` の既定値で `ApplyAppearance` が通ることは、既存テスト(`EditorControlConvertEolsTests` / `EditorControlAppearanceTests` 等)の using に合わせて確かめる。

**Step 3: 失敗を確かめる**

```powershell
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~EditorControlSkipInvalidateTests"
```
Expected: 「0 回」の 3 本と、記録を捨てる Theory 4 件が FAIL。それ以外は PASS(変更前の挙動と一致する表の行)。

**Step 4: 実装する**

`EditorControl.Paint.cs` に足す:

```csharp
    /// <summary>
    /// 今の描画の入力が、最後に描いたフレームの入力と異なるときだけ Invalidate する(設計書 §8.2)。
    /// キャレット・選択の 4 経路(EditorControl.Caret.cs)専用。
    /// </summary>
    /// <remarks>
    /// 正しさの根拠: 画面に出ているのは <see cref="_lastPaintedInputs"/> から決定的に描いた絵である。
    /// 今の入力が同じなら、描き直しても同じ絵になる。未処理の無効領域がある場合でも、その描画は
    /// 今の入力で描かれる。スクロールのセッターは自前で無条件に Invalidate するので、この比較の外にある。
    /// 他の Invalidate(編集・IME・外観・CSV 強調・スクロール・リサイズ)は無条件のまま(変更範囲を最小にする)。
    /// </remarks>
    private void InvalidateIfFrameChanged()
    {
        var current = CaptureFrameInputs();
        if (current is not null && current.Equals(_lastPaintedInputs))
            return;
        Invalidate();
    }

    /// <summary>
    /// 本文・フォント・テーマを丸ごと差し替える経路の Invalidate。記録を捨てて、古いスナップショットや
    /// フォント・幅メモを次の描画まで握らない(描画されないタブで起きても解放される)。
    /// 捨てた後の比較は必ず「変化あり」になる(安全側)。
    /// </summary>
    private void InvalidateAndForgetPaintedFrame()
    {
        _lastPaintedInputs = null;
        Invalidate();
    }
```

- `EditorControl.Caret.cs` の 4 か所(`:170` / `:214` / `:246` / `:281`)の `Invalidate();` を `InvalidateIfFrameChanged();` に替え、それぞれに 1 行コメント `// フェーズ 3: フレームが変わらなければ描き直さない(設計書 §8.2)` を付ける。`SetCaretCharOffset` の remarks の「順序は PositionCaret → BringCaretIntoView → Invalidate」も `InvalidateIfFrameChanged` に直す。
- `EditorControl.cs` の 5 か所(`SetSource` / `ReplaceSource` / `ConvertEols` の非 fast-path / `UndoEolConversion` / `ApplyAppearance`)の `Invalidate();` を `InvalidateAndForgetPaintedFrame();` に替える。

**Step 5: テストが通ることを確かめる**

```powershell
dotnet test tests/kxEdit.Editor.Tests
```
Expected: 全 PASS。既存テストで `Invalidated` の回数を見ているもの(`UndoEolConversion_InvalidatesOnce` など)も通ること。

**Step 6: Commit**

```powershell
git add src/kxEdit.Editor tests/kxEdit.Editor.Tests/EditorControlSkipInvalidateTests.cs
git commit -m "perf(editor): キャレット・選択の移動でフレームが変わらなければ再描画しない"
```

**Step 7: 仕様レビュー**(別エージェント)

観点: 設計書 §8.2・§8.4 の表との一致、4 経路以外の Invalidate を変えていないこと、Step 1 の grep の結果、テストの前提 assert が guard の発火条件と一致していること。

---

## Task 4: オラクル(ランダムな操作列で、古い絵が残らないこと)

**Files:**
- Create: `tests/kxEdit.Editor.Tests/SkipInvalidateOracleTests.cs`

**Step 1: テストを書く**

```csharp
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using kxEdit.Core.Buffers;
using kxEdit.Core.Editing;
using kxEdit.Core.Settings;

namespace kxEdit.Editor.Tests;

/// <summary>
/// 2026-09-25 性能改善フェーズ 3 のオラクル(設計書 §8.4)。FrameInputs 同士の比較だけでは、
/// 「描画が読む状態が FrameInputs に入っていない」故障を検出できない(等しければ出力は自明に一致する)。
/// そこで「画面の絵」を模したビットマップを持ち、Invalidate が 1 回以上起きた操作の後だけ描き直す。
/// 各操作の後に、今の状態から描いた絵(正解)と画素で比べる。Invalidate を省いた時点で差があれば、
/// 実画面に古い絵が残る不具合である。フレームではなく画素で比べるのは、RenderFrame のシフトと
/// IME の未確定表示(Frame の外で描く)も含めるため(実装計画 §0.2)。
/// </summary>
public class SkipInvalidateOracleTests
{
    private static string Body()
    {
        var lines = new List<string>();
        for (int i = 0; i < 60; i++)
        {
            lines.Add(
                i % 9 == 0 ? ""
                : i == 7 ? string.Concat(Enumerable.Range(0, 20).Select(k => $"[{k:D2}]-long-"))
                : i % 2 == 0 ? $"{i:D2} 吾輩は猫である。\t名前はまだ無い。"
                : $"{i:D2} mixed 混在 𠮷 text"
            );
        }
        return string.Join("\r\n", lines);
    }

    private static (Form F, EditorControl C) MakeHosted()
    {
        var f = new Form { Size = new Size(420, 220) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(Body()));
        return (f, c);
    }

    private static int[] Pixels(Bitmap bmp)
    {
        var data = bmp.LockBits(
            new Rectangle(Point.Empty, bmp.Size),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb
        );
        try
        {
            var px = new int[bmp.Width * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + (y * data.Stride), px, y * bmp.Width, bmp.Width);
            return px;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static int[] Paint(EditorControl c, bool record)
    {
        using var bmp = EditorControl.TestHook_PaintToBitmap(c, record);
        return Pixels(bmp);
    }

    /// <summary>オラクルが「古い絵」を検出できること(画素比較が自明に一致しないことの陽性対照)。</summary>
    [Fact]
    public void Oracle_detects_a_stale_picture() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                c.SetCaretCharOffset(c.CurrentBuffer.Current.GetLineStart(2) + 3);
                int[] screen = Paint(c, record: true);
                c.ShowWhitespace = true; // Invalidate はされるが、ここでは描き直さない
                Assert.NotEqual(screen, Paint(c, record: false));
            }
        });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Skipped_invalidations_never_leave_a_stale_picture(int seed) =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                var rng = new Random(seed);
                var log = new List<string>();
                int[] screen = Paint(c, record: true);
                int skipped = 0;
                for (int step = 0; step < 200; step++)
                {
                    var (name, act) = PickOp(rng, c);
                    log.Add(name);
                    int invalidated = 0;
                    InvalidateEventHandler h = (_, _) => invalidated++;
                    c.Invalidated += h;
                    try
                    {
                        act();
                    }
                    finally
                    {
                        c.Invalidated -= h;
                    }
                    if (invalidated > 0)
                        screen = Paint(c, record: true); // WM_PAINT が来た
                    else
                        skipped++;
                    int[] truth = Paint(c, record: false);
                    Assert.True(
                        screen.AsSpan().SequenceEqual(truth),
                        $"seed={seed} step={step}: 古い絵が残る。直前の操作: {string.Join(" → ", log.TakeLast(8))}"
                    );
                }
                // 前提: 省略の経路を実際に通っている(通らなければこのテストは何も確かめていない)。
                Assert.True(skipped >= 20, $"seed={seed}: 省略が {skipped} 回しか起きていない");
            }
        });

    /// <summary>
    /// 操作の抽選。キャレット移動(省略されうる)を厚めに、描画の入力を変える操作を一通り混ぜる。
    /// 描画の入力を足したら、それを変える操作もここに足すこと。
    /// </summary>
    private static (string Name, Action Act) PickOp(Random rng, EditorControl c)
    {
        var snap = c.CurrentBuffer.Current;
        int len = snap.CharLength;
        int Rand() => rng.Next(0, len + 1);
        int caret = c.CaretCharOffset;
        switch (rng.Next(0, 22))
        {
            case 0:
            case 1:
            case 2:
                return ("→", () => c.SetCaretCharOffset(Math.Min(len, caret + 1)));
            case 3:
            case 4:
                return ("←", () => c.SetCaretCharOffset(Math.Max(0, caret - 1)));
            case 5:
            case 6:
            {
                int line = snap.GetLineIndexOfChar(caret);
                int to = snap.GetLineStart(Math.Min(snap.LineCount - 1, line + 1));
                return ("↓", () => c.SetCaretCharOffset(to));
            }
            case 7:
                return ("任意の位置", () => c.SetCaretCharOffset(Rand()));
            case 8:
                return ("Shift+移動", () => c.MoveCaretWithSelection(Math.Min(len, caret + rng.Next(1, 8))));
            case 9:
            {
                int a = Rand(), b = Rand();
                return ("範囲選択", () => c.SetSelectionCharRange(a, b));
            }
            case 10:
            {
                int p = Rand();
                return ("空の選択(anchored)", () => c.SetSelectionAnchored(p, p));
            }
            case 11:
                return ("TopLine", () => c.TopLine = rng.Next(0, snap.LineCount));
            case 12:
                return ("ScrollX", () => c.ScrollX = rng.Next(0, 400));
            case 13:
                return ("現在行強調", () => c.HighlightCurrentLine = !c.HighlightCurrentLine);
            case 14:
                return ("行番号", () => c.ShowLineNumbers = !c.ShowLineNumbers);
            case 15:
                return ("空白表示", () => c.ShowWhitespace = !c.ShowWhitespace);
            case 16:
                return ("折り返し", () => c.WrapColumns = c.WrapColumns == 0 ? 24 : 0);
            case 17:
            {
                int s = Rand();
                return rng.Next(2) == 0
                    ? ("セル強調", () => c.HighlightCharRange(s, rng.Next(0, 6)))
                    : ("セル強調を消す", c.ClearHighlight);
            }
            case 18:
                return ("1 文字挿入", () => c.ReplaceCharRange(caret, 0, "x"));
            case 19:
                return ("Undo", c.Undo);
            case 20:
                return c.__TestIsComposing()
                    ? ("IME 確定", () => c.__TestApplyResult("漢字"))
                    : ("IME 未確定", () => c.__TestApplyComposition("かな", 1, [ImeAttribute.Input, ImeAttribute.Input], []));
            default:
            {
                bool dark = rng.Next(2) == 0;
                bool hl = rng.Next(2) == 0;
                return (
                    "外観",
                    () =>
                        c.ApplyAppearance(
                            new AppSettings
                            {
                                Theme = dark ? "white-on-black" : "default",
                                HighlightCurrentLine = hl,
                            }
                        )
                );
            }
        }
    }
}
```

`ReplaceCharRange` がキャレット位置の扱い上 `caret` をサロゲートの中間に落とすことはない(`SetCaretCharOffset` がスナップ済み)。`ImeAttribute` の名前空間は `ImeController.cs` の using に合わせる。

**Step 2: 走らせる**

```powershell
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~SkipInvalidateOracleTests"
```
Expected: 全 PASS。`skipped` の下限(20)に届かない seed があれば、キャレット移動の比率を上げる(下限は下げない)。

**Step 3: オラクルが漏れを検出できることを確かめる(一時的な改変。commit しない)**

`CaptureFrameInputs` の `ShowWhitespace = _showWhitespace` を `ShowWhitespace = false` に替え、`PaintBody` の `inputs.ShowWhitespace` を `_showWhitespace` に替える(= 描画が FrameInputs の外を読む故障の模擬)。ビルドの成功を確かめてからオラクルを走らせ、FAIL することを確かめる。**結果を実施記録に書き、改変を戻す。**

同様に、IME の漏れの模擬として `FrameInputs.Equals` から `&& Ime.Equals(other.Ime)` を外し、FAIL することを確かめて戻す。

(これは変異検証ではなく、テスト道具が故障を検出できるかの確認である。§0.3 の「ミューテーション検証を行わない」とは別。)

**Step 4: Commit**

```powershell
git add tests/kxEdit.Editor.Tests/SkipInvalidateOracleTests.cs
git commit -m "test(editor): 再描画の省略で古い絵が残らないことをランダムな操作列で確かめる"
```

**Step 5: 仕様レビュー**(別エージェント)

観点: オラクルが「描画を省いた時点」を正しく模擬しているか(Invalidate が 1 回でもあれば描き直す = 実画面の WM_PAINT)、陽性対照の意味、Step 3 の結果、操作の網羅(FrameInputs の各メンバーを変える操作が少なくとも 1 つあるか。`ClientSize` は除く = 無条件の ResizeRedraw)。

---

## Task 5: Smoke `--paint-transition`(実画面で、描画を起こさずに撮って比べる)

フェーズ 1 の申し送り 1〜3。フェーズ 9 も再利用する。

**Files:**
- Create: `tests/kxEdit.Editor.Smoke/PaintTransition.cs`
- Modify: `tests/kxEdit.Editor.Smoke/PaintSnapshot.cs`(`Body`・`BuildBody`・`ReadPixels` を `internal` に。`Body` の record も `internal`)
- Modify: `tests/kxEdit.Editor.Smoke/Program.cs`(`--paint-transition` の振り分け)
- Modify: `tools/README.md` §3(使い方を 1 節)

**仕様**

`--paint-transition [--expect-skip]`
- 窓・フォーカスの受け皿・例外の捕まえ方は `PaintSnapshot` と同じ(画面内に置く・フォーカスは画面外のダミーボタン・`ThreadException` を記録して EXIT 1)。
- 遷移 1 つごとに:
  1. `ApplyAppearance`(遷移の設定)→ `SetOrReplaceSource(本文)` → 準備の操作(状態 A)。
  2. 全面を同期で描き直す(`RedrawWindow(form, INVALIDATE|ERASE|ALLCHILDREN|UPDATENOW)` → `DoEvents` → `DwmFlush`)。
  3. 描画回数を控え、**遷移の操作**を行い、`Application.DoEvents()` でメッセージを流す(描き直しは保留中の無効領域の分だけ起きる)→ `DwmFlush`。操作中の描画回数を記録する。
  4. **描画を起こさずに撮る**(`GetDC(editor.Handle)` + `BitBlt` で、エディタのクライアント領域の画面上の絵を読む)。撮影の前後で描画回数が変わらないことを自己チェックする。→ 絵 X。
  5. 2. と同じく全面を描き直し、4. と同じ方法で撮る → 絵 Y(正解)。
  6. X と Y を画素で比べる。差があれば「古い絵が残る」で失敗。
- **陽性対照**(`control-stale`): 操作 = `ShowWhitespace = true` の直後に `ValidateRect(editor.Handle, null)` で無効領域を取り消す。X と Y に差が**出なければ**、撮り方が画面の絵を読めていない(=道具が壊れている)ので EXIT 1。
- **描画回数の期待**: 遷移ごとに `Paint`(描き直しが要る)/ `Skip`(フェーズ 3 以後は描き直さない)/ `Any` を持つ。
  - `Paint` の遷移で操作中の描画回数が 0 なら失敗(常に検査)。
  - `Skip` の遷移で描画回数が 1 以上なら、`--expect-skip` のときだけ失敗(変更前のコードは描き直すので、変更前の実行では付けない)。
- 出力: 遷移ごとに `名前: 描画 N 回・一致/差 K 画素`。EXIT 0 = 全遷移が一致(と期待どおりの描画回数)、1 = 失敗、2 = 引数の誤り。PNG は失敗した遷移だけ `--out <dir>` があれば書く(省略時は書かない)。

**遷移の表**(本文は `PaintSnapshot.BuildBody()` の 60 行。位置は `Body.Line(n) + col`)

| 名前 | 設定 | 準備(状態 A) | 操作 | 期待 |
|---|---|---|---|---|
| `caret-right` | 既定 | caret = (2,3) | caret = (2,4) | Skip |
| `caret-down` | 既定 | caret = (2,3) | caret = (3,3) | Skip |
| `linenum-caret-down` | 行番号 ON | caret = (2,3) | caret = (3,3) | Skip |
| `curline-same-line` | 強調 ON | caret = (2,3) | caret = (2,6) | Skip |
| `curline-next-line` | 強調 ON | caret = (2,3) | caret = (3,3) | Paint |
| `select-extend` | 既定 | caret = (2,3) | MoveCaretWithSelection((2,8)) | Paint |
| `select-clear` | 既定 | 選択 (1,4)〜(3,5) | caret = (3,5) | Paint |
| `collapse-anchored` | 既定 | caret = (2,3) | SetSelectionAnchored((4,1),(4,1)) | Skip |
| `scroll-by-caret` | 既定 | caret = (2,3) | caret = (40,0) | Paint |
| `hscroll-by-caret` | 既定 | caret = (7,0) | caret = 行 7 の末尾 | Paint |
| `ime-cancel-by-move` | 既定 | caret = (1,6)・未確定「にほんご」 | caret = (3,0) | Paint |
| `theme-then-move` | 黒地 | caret = (2,3) | caret = (3,3) | Skip |
| `theme-change` | 既定 | caret = (2,3) | ApplyAppearance(黒地) | Paint |
| `control-stale` | 既定 | caret = (2,3) | ShowWhitespace = true → ValidateRect | 差が出ること |

準備の後に状態の自己チェック(キャレット位置・選択・TopLine・ScrollX・未確定)を行う。`scroll-by-caret` は操作後に TopLine > 0、`hscroll-by-caret` は ScrollX > 0 を確かめる(スクロールが起きない窓の大きさだと遷移の意味がない)。

**撮り方が画面の絵を読めない場合**(陽性対照が差を出さない・X が全面黒など): `GetDC(IntPtr.Zero)`(画面の DC)+ エディタのスクリーン座標で読む形に切り替え、Form を `TopMost` にする。NVDA のスピーチビューアー(最前面)と重なると読めないので、重なりを自己チェックする。どちらにしたかを実施記録に書く。

**Step 1: 実装する**(`PaintSnapshot.cs` の構成に倣う。P/Invoke は `GetDC` / `ReleaseDC` / `BitBlt`(SRCCOPY)/ `ValidateRect` / `RedrawWindow` / `DwmFlush`)

**Step 2: 変更前のコード(Task 2 の commit の時点 = Invalidate を省かない)で走らせる**

```powershell
git stash -u; git checkout <Task 2 の commit>   # または worktree
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --paint-transition
```
Expected: EXIT 0。全遷移が「一致」、`Skip` の遷移も描画 1 回以上(まだ省かないので)。陽性対照が差を出す。

**Step 3: 変更後(HEAD)で走らせる**

```powershell
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --paint-transition --expect-skip
```
Expected: EXIT 0。`Skip` の遷移は描画 0 回・一致。`Paint` の遷移は描画 1 回以上・一致。

**Step 4: tools/README.md に 1 節足す**(`--paint-snapshot` との違い = 撮影で描き直すか否か・`--expect-skip` の使い分け・陽性対照)

**Step 5: Commit**

```powershell
git add tests/kxEdit.Editor.Smoke tools/README.md
git commit -m "test(smoke): --paint-transition を追加し、描画を起こさずに撮った絵と描き直した絵を比べる"
```

**Step 6: 仕様レビュー → コード品質の前倒しレビュー**(別エージェント)

観点: 撮り方が本当に描画を起こしていないか(自己チェック)、陽性対照の強さ、偽陰性の経路(X も Y も同じ古い絵になる経路がないか = Y の前の全面描き直しが確実に起きることの自己チェック)、フェーズ 9(部分再描画)で遷移を足しやすい形か。

---

## Task 6: 変更後の計測

**Files:** なし(実施記録に書く)

**Step 1:** Task 1 と同じ条件(NVDA の有無を揃える)で Smoke `--perf --scenario S1,S2,S3,S7` を 3 回。`after-<n>.json` に出す。

**Step 2:** 集計(Task 1 の Step 3 と同じ)。

**Step 3(ユーザーの了承後):** perf-harness M-2 を 3 回(publish は `<scratchpad>\pub-after`)。

**判定**(設計書 §3.2・§8.5)
- S1・S2(ja10k / en10k): 変更前の揺れ(min〜max)を超えて下がること。期待値は全面再描画の分(フェーズ 1 後の S7 ≈ 6.9 ms)。`paints_per_op` が 0 になること。
- S3・S7: 変更前の揺れの範囲に収まること(打鍵は無条件の Invalidate のまま)。
- harness M-2 の「→←の交互」「↓↑の交互」が下がること。「Shift+→」(選択が変わる)と基準の行は変わらないこと。
- 改善が揺れの範囲に収まる場合は、PR に記載したうえで採否をユーザーに判断してもらう。

**Step 4: Commit**

```powershell
git add docs/plans/2026-09-25-perf-skip-invalidate.md
git commit -m "docs(perf): フェーズ 3 の変更後の計測値と、各タスクの実施記録を記録"
```

---

## Task 7: 最終ブランチレビュー(2 パス)と品質ゲート

**Step 1: 最終ブランチレビュー(別エージェントを 2 本)**
- コード品質パス: seam(`FrameInputs`・`CaptureFrameInputs`・`PaintAndRecord`)、4 経路以外が変わっていないこと、オラクルと `--paint-transition` の偽陰性。ミューテーション検証のスポットチェックは**行わない**(§0.3。描画は禁止の対象)と明記して依頼する。
- 脆弱性パス: 触れる面は描画とテストフックだけ。UIA の RPC スレッドから `CaptureFrameInputs` を呼ぶ経路がないこと(`GetVisibleCharRange` は Invoke 後の UI スレッドで走ること)を確認する。

指摘は ① fixup / ② PR に記載して受容 / ③ 理由付き却下 のどれかで扱う。

**Step 2: sr-regression**

```powershell
pwsh -File tools/sr-regression.ps1
```

**Step 3: 品質ゲート**

```powershell
pwsh -File tools/pre-merge-check.ps1
```
Expected: EXIT 0。

---

## Task 8: L5(実機 SR 検証)

**Step 1(windows-mcp で可能な範囲):** publish を作り、実アプリで次を行う。
- 既定設定でキャレットを ←→↑↓・PageDown で動かし、NVDA の発声(スピーチビューアーの画面を実解像度で切り出して読む)が正常なこと。
- 目視は**描画を起こさない撮り方**(画面の DC から BitBlt。PrintWindow を使わない)で、強調 ON/OFF の切替直後の移動、選択の開始と解除、IME の未確定中の移動、スクロール、テーマ変更の直後を撮り、全面を描き直した絵と比べる。
- 検証後、`%APPDATA%\kxEdit\settings.json` を退避したコピーから戻す(ハッシュの一致を確認)。

**Step 2(ユーザーに依頼):** NVDA の実機で、キャレット移動の読み上げとハイライト矩形(視覚的ハイライトを ON にして)を確認してもらう。

**Step 3:** 結果を実施記録に書いて commit する。

---

## Task 9: PR

description(日本語)に次を書く: 目的、変更前後の計測値(min / 中央 / max・`paints_per_op`)、意図的な挙動差(なし。`Invalidated` の発火が減ることは §0.5)、設計書からの精密化(§0.2)、オラクルの漏れ検出の確認結果(Task 4 Step 3)、`--paint-transition` の結果、L5 の結果、レビュー経緯、申し送り。

マージ後、設計書 §8 の末尾に「実施記録」を追記する(設計書 §3.1 の 6。PR に同梱してもよい)。

---

## 実施記録

### Task 0: 設計書 §7 の実施記録(e5df4ae)

フェーズ 2 の実施記録を設計書 §7.4 として追記した。

### Task 1: 変更前の計測(2026-09-25・src は main `0305997` と同一)

**環境**: フェーズ 0〜2 と同じ VM(1024×767・96 DPI)。**NVDA 起動中**(pid 6372)。

**Smoke `--perf --scenario S1,S2,S3,S7`**(Release・3 回とも EXIT 0。3 回の中央値の min / 中央 / max・ms/操作。`paints_per_op` はすべて 1)

| ID | ja10k | en10k |
|---|---|---|
| S1 →← | 7.36 / 7.45 / 7.58 | 6.98 / 7.12 / 7.65 |
| S2 ↓↑ | 7.51 / 7.56 / 7.96 | 7.00 / 7.15 / 7.81 |
| S3a 挿入 | 7.97 / 8.07 / 8.20 | 7.64 / 7.79 / 7.86 |
| S3b BackSpace | 8.00 / 8.03 / 8.22 | 7.62 / 7.65 / 7.77 |
| S7 全面再描画 | 7.21 / 7.30 / 7.43 | 6.42 / 6.52 / 6.62 |

**perf-harness M-2**(publish d29f5cf・3 回・CPU ms/回。3 回とも `status=completed`・NVDA 起動中)

| 操作 | ja10k(1 / 2 / 3 回目) | en10k(1 / 2 / 3 回目) |
|---|---|---|
| →←の交互 | 17.97 / 17.19 / 17.03 | 17.19 / 17.03 / 17.03 |
| ↓↑の交互 | 15.94 / 16.25 / 16.41 | 13.91 / 15.47 / 13.44 |
| Shift+→ | 16.80 / 17.58 / 16.41 | 17.58 / 20.70 / 15.62 |
| 文字入力x | 19.27 / 20.57 / 17.45 | 15.89 / 16.41 / 16.41 |
| BackSpace | 14.32 / 17.71 / 16.15 | 13.54 / 15.10 / 14.06 |
| PageDown/PageUpの交互 | 20.94 / 21.56 / 16.88 | 19.06 / 18.44 / 19.69 |
| 基準:全面の再描画 | 7.34 / 6.56 / 7.66 | 7.03 / 6.88 / 6.25 |
| 基準:文書先頭での← | 6.09 / 8.75 / 5.16 | 6.41 / 5.16 / 6.88 |
| 基準:Shiftの単押し | 1.56 / 1.88 / 1.88 | 1.88 / 2.34 / 3.12 |

`backups` には空のフォルダーが 1 つあった(ファイルはない)ので、harness の中止条件には当たらなかった。

### Task 2: FrameInputs の seam(3d6c722・fixup 47e8238)

- テスト 617 件 PASS・Release 0 warning。`--paint-snapshot` は変更前(11707de)の基準と 16 枚すべて一致(fixup 後も同じ)。
- **計画からの逸脱**
  - `RenderFrame` を static にした(生の状態を読み戻すとコンパイルで止まる)。
  - `SelectionRange` は `System.Windows.Forms.SelectionRange` と衝突するので、既存の Paint.cs と同じく別名の using にした。`System.Drawing` は暗黙の using にあるので外し、doc の cref は `System.Drawing.Font` と書いた(`FrameInputs.Font` プロパティと紛れるため)。
- **仕様レビュー**: ✅。逸脱 4 件はいずれも妥当と判断された。Minor 2 件(`ClientSize` の doc が不正確・テストの using が計画と違う)は、前者をコード品質レビューの M-1 で直し、後者は実害なし(global using にある)で受容した。
- **前倒しのコード品質レビュー**: 条件付き承認 → fixup 47e8238 の再レビューで承認。
  - I-1(`PaintBody` がインスタンスメソッドのままで、生の状態を 1 語で読み戻せる): ① `PaintBody` を static にし、生の状態への出口を引数(`emptyBackColor`・`ime`)に限った。`_lastFrame` の更新は `PaintAndRecord` に移した(記録しない正解描画は `_lastFrame` を書き換えない)。
  - I-2(IME の例外の約束が `ImeController` 側に書かれていない): ① `ImeController.Draw` と `IImeOverlayHost` に相互参照を書いた。より良い形(IME の原点と色を値として `FrameInputs` に取り込み、`Draw` が host を読まない形)は、フェーズ 9 への申し送りにする(部分再描画で IME の領域を無効化矩形に含めるときに必須になる)。
  - M-1(`ClientSize` の doc): ① 「描画は直接読まない。ResizeRedraw があるので比較上は冗長だが、安全側で残す」に直した。**§0.2 の表の「読む箇所」の記述(`g.Clear` の範囲・RenderFrame の右端)は不正確だった**(RenderFrame の右端は `PaintWidth` から来る)。
  - M-2(`PaintAndRecord` が記録を捨てる前に Capture していた): ① 順序を入れ替えた。
  - M-3(`RenderFrame` の summary に古いフィールド名): ① 直した。
  - M-4(record の `==` は null 同士を true にする): ① remarks に「比較は Equals を使い、null は常に変化あり」と書いた。
  - M-5(21 項の `&&`): ③ 現状維持。漏れは網羅性テストが捕まえる。フェーズ 9 で差分関数に作り直すときに書き直す。
  - M-6(テストの Font のリーク): ① finally で破棄する。`GdiCharMetrics` は IDisposable ではない。
- **フェーズ 9 への申し送り**(レビューから)
  - 部分クリップの描画で `PaintAndRecord` が「全面の入力」を記録しても正しいのは、「無効化した領域が、入力の差で変わる全画素を覆う」ときに限る。
  - IME の原点の値化(I-2)。
  - ScrollWindowEx の適否は `old with { TopLine = n.TopLine, TopSegment = n.TopSegment, ScrollX = n.ScrollX }.Equals(n)` の形で判定できる。

### Task 3: 条件付き Invalidate(066d572)

- **Step 1 の grep**
  - `Invalidated +=` は src に 0 件で、App 層に購読者はない。
  - `_caretCtrl` / `ctx.Caret` への書き込みは 24 件あり、すべての後に次のどれかが続く: AfterEdit、丸ごと差し替え、4 経路、`EnsureVisibleCharRange` の finally での復元。
  - 仕様レビューが独立に検証した範囲: FrameInputs の各メンバーの元になるフィールドへの書き込み全件、IME の `_ime =` の 6 か所、UIA が呼ぶ host の API。いずれも Invalidate を通らない経路はなかった。
  - 4 経路の中で、比較より後に描画の入力を変えるコードもない。`BringCaretIntoView` はセッター経由で、比較より前に走る。`UpdateUI` の購読者はステータスバーを読むだけ。
- **Step 3**: 実装前は 13 件中 7 件が FAIL した(「0 回」の 3 本と、記録を捨てる Theory の 4 件)。6 件は PASS。計画の予想どおり。
- **Step 5**: Editor.Tests 630・App.Tests 1011・Core.Tests 1544 件がすべて PASS。Release は 0 warning。
- **仕様レビュー**: ✅。Minor 1 件(記録を捨てる Theory に `SetSource` がない)は ③ 却下。`SetSource` は `_buffer` が null のときに 1 度しか呼べず、その時点の記録は必ず null である(null のバッファで描いた入力は null)。2 回目以降の `Text` セッターは `ReplaceSource` を通り、Theory に入っている。

### Task 4: オラクル(2970540・fixup 538ab8e)

- オラクル 9 件(陽性対照 1 件と 8 seed × 200 step)が PASS。1 seed は約 0.6〜0.9 秒。省略の回数(4 経路の操作で、キャレットかアンカーが変わり、Invalidate が 0 回だったもの)は、seed ごとに 27〜41 回。
- **Step 3(オラクルが漏れを検出できるかの確認)の結果と、計画の前提の訂正**
  - **計画の 2 つの改変は FAIL しなかった。これは計画の前提の誤りである。**
    - 1 つ目は「ShowWhitespace を FrameInputs の外で読む」改変(`PaintBody` が static になったので、引数を 1 つ足して生の値を渡す形で模擬した)。2 つ目は「Equals から Ime を外す」改変。
    - どちらの状態も、変わるたびに必ず無条件に Invalidate される。ShowWhitespace はセッター、IME は `ImeController` の全経路が Invalidate し、4 経路も冒頭で未確定を取り消す。そのため、漏れても古い絵は残らず、不具合にならない。
    - §0.2 の「漏れはオラクルが IME の操作を含めて検出する」は過大な記述だった。正確には、**オラクルが検出するのは、省略されうる 4 経路で変わる状態(キャレット・アンカー)と、そこから派生する入力(`CurrentLineLogical`・`Selection`)の漏れと誤り**である。スクロール系も、セッターが無条件に Invalidate するので対象外。
  - **代わりに次の 2 つを当て、どちらも 8 seed すべてで FAIL した**(fixup 後に当て直しても同じ)。どちらの改変も戻した。
    - `CurrentLineLogical` を FrameInputs の外で読む改変。
    - Equals から `Selection` を外す改変。
  - **他の故障の型を守る層**
    - 描画が FrameInputs の外を読む故障: `PaintBody` が static なので、コンパイラが防ぐ。
    - Equals の漏れ: `FrameInputsTests` の網羅性テストが捕まえる。
- **仕様レビュー**: ✅ → fixup 538ab8e の再レビューも ✅。
  - m-1(省略の数え方に早期 return の no-op が混ざり、guard が緩い): ① 4 経路の操作で、キャレットかアンカーが実際に変わり、Invalidate が 0 回のときだけ数える形にした。
  - m-2(後方の選択・非対称の選択・Shift でアンカーに戻る遷移を踏まない): ① Shift+移動を前後両方向にし、anchored の半分を後方の選択にした。
  - m-3: ② フェーズ 9 への申し送り(下記)。
- **フェーズ 9 への申し送り**(レビューから)
  - **オラクルは、Invalidate が 1 回でもあれば全面を描き直す。** 部分無効化(`Invalidate(Rectangle)`)を入れる前に、`InvalidateEventArgs.InvalidRect` の範囲だけを「画面」のビットマップへ合成する形に広げること。今のままでは、無効化した矩形の不足を検出できない。
  - 条件付き Invalidate を 4 経路の外へ広げるときは、その経路の操作がオラクルの `PickOp` に入っていることを確かめる。
  - 状態を変えても Invalidate せず、4 経路の Invalidate に便乗しているコードがあると、今後は古い絵になる。現時点では grep で該当なし。

### Task 5: Smoke `--paint-transition`(10a74b7・fixup 12e38d1)

- **撮り方**: `GetDC(editor.Handle)` + `BitBlt(SRCCOPY)` で窓の描画面を読む方式を採った。画面の DC と `TopMost` への切り替えは要らなかった。
  - 陽性対照(`control-stale`)で 319 画素の差が出た。操作後の画面 X が状態 A と一致し、正解 Y とは異なることも自己チェックする。
  - 全遷移で、全面を描き直した 2 枚(Y と Y')が一致した(決定性)。
- **結果**
  - 変更前のコード(47e8238)に最終版の道具を当てた(仕様レビューが実施)。
    - `--expect-skip` なし: EXIT 0。14 遷移すべて一致し、Skip の 6 遷移も描画 1 回。
    - `--expect-skip` あり: Skip の 6 遷移が「描画の省略を期待したが描いた」で EXIT 1。
  - HEAD(`--expect-skip` あり): EXIT 0。Skip の 6 遷移(caret-right・caret-down・linenum-caret-down・curline-same-line・collapse-anchored・theme-then-move)は描画 0 回で一致。Paint の 7 遷移は描画 1 回で一致。
  - **偽陰性の確認**(仕様レビューが実施): 変更前の worktree で 4 経路の `Invalidate()` を消すと、curline-next-line(差 17,718 画素)・select-extend(666)・select-clear(10,607)が「古い絵が残る」で失敗し、EXIT 1 になった。
- **計画からの逸脱**
  - `collapse-anchored` の位置を (5,1) にした。行 4 は空行で、(4,1) は CR と LF の間にあり (4,0) にスナップされるため。
  - 自己チェックを足した。
    - 操作の後に `GetUpdateRect` で、保留中の無効領域がないこと。
    - 陽性対照で X = A であること。
    - Y = Y' であること。
    - 遷移の表との整合(Paint の遷移は Y ≠ A、Skip の遷移は Y = A)。
  - `PaintSnapshot` の `CloseQuietly` も internal にして共有した。
- **仕様レビュー**: ✅(上の 2 つの確認を含む)。
- **前倒しのコード品質レビュー**: 承認。fixup 12e38d1 の再レビューでも承認。
  - Important-1(準備の後のスクロール位置が 0 に固定されていて、フェーズ 9 のスクロールから始まる遷移を書けない): ① `Transition.ArrangedScroll` を足した(既定 (0,0))。TopLine=5 から始める一時的な遷移で、正常時と失敗時の両方を確かめた。
  - Minor-2(陽性対照が表の最後にある): ① 先頭に移し、失敗したらそこで止める。
  - Minor-3(「撮影で WM_PAINT が起きない」の回数チェックは構造上失敗しない): ① コードは残し、doc と README で「撮り方の前提は陽性対照で確かめる。回数のチェックは撮り方を変えたときの回帰を防ぐもの」と書き分けた。
  - Minor-5(一部の例外が EXIT 1 にならない): ① catch に `ArgumentException` と `InvalidOperationException` を足した。
  - Minor-6(README の出力例): ① 実際の出力に合わせた。
  - Minor-1・4・7・8: ② フェーズ 9 への申し送り(下記)。
- **フェーズ 9 への申し送り**(レビューから)
  - **Paint の遷移は「描画が 1 回以上」しか見ていない。** 部分再描画になっているか(性能の目的)を確かめるには、次を足す。
    - `Paint` の `e.ClipRectangle` の和を記録する。
    - 期待に `Expect.Partial(Rectangle maxClip)` のようなものを足す。
    - スクロール領域向けの陽性対照(TopLine を変えてから `ValidateRect`)を足す。
    - 表示の不具合(古い絵)は、今の X と Y の比較で検出できる。
  - 保留中の無効領域はエディタ本体でしか見ていない。スクロールバー(子)に触るなら、子にも `GetUpdateRect` をかける。
  - PaintSnapshot との重複(窓の組み立て・`Check`・`RedrawWindow` の P/Invoke)は、3 つ目の利用者が出たら共通の型へ切り出す。例外型 `PaintSnapshotException` も、あわせて改名する。
  - 窓を重ねた状態(スピーチビューアーなど)で撮れることは前提としているが、確かめる対照がない。L5 などで一度、重ねた状態で実行して記録を残す。

### Task 6: 変更後の計測(2026-09-25・src は 12e38d1 と同一)

**環境**: Task 1 と同じ。NVDA 起動中(pid 6372)。

**Smoke `--perf --scenario S1,S2,S3,S7`**(3 回とも EXIT 0。min / 中央 / max・ms/操作。括弧内は変更前の中央値。`paints_per_op` は S1・S2 が 0、S3・S7 が 1)

| ID | ja10k | en10k |
|---|---|---|
| S1 →← | 0.08 / 0.08 / 0.08(7.45) | 0.42 / 0.43 / 0.45(7.12) |
| S2 ↓↑ | 0.09 / 0.10 / 0.10(7.56) | 0.45 / 0.47 / 0.48(7.15) |
| S3a 挿入 | 7.11 / 7.46 / 7.69(8.07) | 6.99 / 7.13 / 7.16(7.79) |
| S3b BackSpace | 7.10 / 7.55 / 7.63(8.03) | 7.01 / 7.15 / 7.15(7.65) |
| S7 全面再描画 | 6.86 / 6.88 / 6.92(7.30) | 6.06 / 6.10 / 6.20(6.52) |

**perf-harness M-2**(publish 760db06・3 回・CPU ms/回。3 回とも `status=completed`・NVDA 起動中。括弧内は変更前の 3 回の範囲)

| 操作 | ja10k(1 / 2 / 3 回目) | en10k(1 / 2 / 3 回目) |
|---|---|---|
| →←の交互 | 11.88 / 10.31 / 9.22(17.03〜17.97) | 12.34 / 11.41 / 12.97(17.03〜17.19) |
| ↓↑の交互 | 10.62 / 8.91 / 10.31(15.94〜16.41) | 9.22 / 9.84 / 10.78(13.44〜15.47) |
| Shift+→ | 15.62 / 16.02 / 15.23(16.41〜17.58) | 17.19 / 14.84 / 16.02(15.62〜20.70) |
| 文字入力x | 17.97 / 19.53 / 16.67(17.45〜20.57) | 13.80 / 17.19 / 15.36(15.89〜16.41) |
| BackSpace | 15.62 / 14.84 / 16.15(14.32〜17.71) | 15.10 / 14.32 / 16.67(13.54〜15.10) |
| PageDown/PageUpの交互 | 16.56 / 17.50 / 16.88(16.88〜21.56) | 20.94 / 17.81 / 21.25(18.44〜19.69) |
| 基準:全面の再描画 | 8.12 / 7.03 / 6.41(6.56〜7.66) | 6.25 / 6.41 / 5.78(6.25〜7.03) |
| 基準:文書先頭での← | 8.44 / 16.56 / 6.09(5.16〜8.75) | 6.88 / 5.16 / 11.56(5.16〜6.88) |
| 基準:Shiftの単押し | 1.56 / 1.56 / 1.25(1.56〜1.88) | 1.56 / 2.66 / 2.34(1.88〜3.12) |

**判定**(設計書 §3.2・§8.5)
- **S1・S2**: ja10k・en10k とも、変更前の揺れを大きく超えて下がり、`paints_per_op` は 0 になった。期待値(全面再描画の分 ≈ −7 ms)どおり。en10k が ja10k より 0.35 ms ほど高い理由は調べていない。S1・S2 の残りは、キャレット移動そのもの(`PositionCaret`・`BringCaretIntoView`・`CaptureFrameInputs`)の費用である。
- **S3・S7**: 描画の経路は変えていない(打鍵は無条件の Invalidate のまま)。それでも変更前の中央値より 0.4〜0.7 ms 低く、S3・S7 のすべて(ja10k・en10k)で、変更後の最大値が変更前の最小値を下回った。原因は切り分けていない。計測の日内の揺れの可能性があるので、改善とは主張しない。悪化はない。
- **harness M-2**: 「→←の交互」と「↓↑の交互」は、変更前の揺れを超えて、中央値で 4〜7 ms 下がった。
  - ja10k の →← は、中央値で 17.19 → 10.31 ms。
  - 下げ幅が Smoke の −7 ms より小さいのは、IME/TSF など kxEdit の外の費用(「基準:文書先頭での←」で 5〜8 ms)が残るため。en10k は下げ幅が小さい(→← 17.03 → 12.34、↓↑ 13.91 → 9.84)。
  - 省略の対象外の行(Shift+→・文字入力x・BackSpace・PageDown・基準の行)は、3 回ずつでは揺れが大きい。変更前の範囲を少し出る値が、上下どちらの向きにもある。
    - 下に出た例: ja10k の Shift+→ が 3 回とも変更前の最小値を下回った。en10k の文字入力x の 13.80 も変更前の最小値を下回った。
    - 上に出た例: en10k の PageDown の 20.94・21.25 と BackSpace の 16.67 が、変更前の最大値を上回った。
    - 基準:全面の再描画(ja10k)の 8.12・6.41 も、変更前の範囲の外にある。
    - 向きが一定しないので、揺れとみる。
  - 「基準:文書先頭での←」に 16.56・11.56 の外れ値が 1 回ずつある。この操作はキャレットが動かず、kxEdit の中では早期 return するので、本変更の影響を受けない。kxEdit の外の揺れとみる。

### Task 7: 最終ブランチレビュー(2 パス)の指摘と扱い(fixup 803febc)

**コード品質パス**(Important 1 件と Minor 5 件を直せばマージ可。Core 1544・Editor 639・App 1011 件が PASS、Release は 0 warning)
- **I-1**(2 つ目の不変条件「描画の入力を変える経路は必ず自分で Invalidate する」が、コードのどこにも書かれていない): ① 803febc で次の 3 か所に書いた。
  - `_lastPaintedInputs` のフィールドコメント。他の経路の Invalidate に便乗しないこと。Invalidate せずに状態を変えると、その後に WM_PRINT や部分的な描画で記録が更新された場合、または 4 経路の中で比較の後に状態を変えた場合に、古い絵が残ること。以前のように次のキャレット移動で必ず直るとは限らないこと(文言は再レビューの M-6 で精密化した)。
  - 再レビューの M-6(上の文言が、古い絵が残る仕組みを実際より広く書いていた)・M-7(S3b の列挙漏れ): ① 直した。
  - `InvalidateIfFrameChanged` の remarks。
  - `FrameInputs.cs` の冒頭。
  - 1 つ目の不変条件(描画が読む状態は FrameInputs の中にある)は、`PaintBody` が static なのでコンパイラが守る。2 つ目は、今回の変更までは言葉による守りもなかった。
- **M-1**(実施記録 Task 6 の harness の記述が表の数値と合わない): ① 事実に沿って書き直した(本 commit)。
- **M-2**(OnPaint の古いコメント)・**M-3**(オラクルのクラス doc が検出範囲の訂正を反映していない)・**M-5**(「CaptureFrameInputs() だけが作る」はテストを除けば正しい): ① 803febc。
- **M-4**(`SelectionChange_Invalidates_OnAllSelectingPaths` の 2 本目と 3 本目の前で「記録がある」を確かめていないので、何も確かめずに緑になりうる。CLAUDE.md §4-B): ① 803febc で `PaintAndAssumeRecorded` にした。

**脆弱性パス**(Critical・Important なし)
- 確かめられたこと
  - `CaptureFrameInputs` の呼び出しは 4 か所で、すべて UI スレッド。`GetVisibleCharRange` は UIA から Invoke 経由で呼ばれる。
  - `_lastPaintedInputs` にスレッドの競合はない。
  - 破棄済みの Font は参照で比べるだけ。
  - 計画 §0.2 の「WM_PRINT や部分的な WM_PAINT で記録してよい根拠」は正しい。
  - 観点に挙げた経路にも問題はない: DPI(SystemAware)、テーマ、ハイコントラスト、フォーカス、最小化からの復元。
  - Smoke の DC の解放、Smoke が配布物に含まれないこと、テストフックが internal であること。
- Minor-1(正しさが規約だけで守られている): コード品質の I-1 と同じ。コメントで ① とした。
- Minor-2(ClearType の切り替えなど、FrameInputs が追跡しない OS の設定の変化は、以前はキャレットを動かせば描き直されたが、今後は編集・スクロール・リサイズまで古い描き方が残りうる): ② 受容。見た目だけの問題で、誤った選択範囲やキャレット行を示すことはない。PR の「観測できる差」に書く。
- Minor-3(`GetVisibleCharRange` の処理が少し増えた): ② 受容。UI スレッド上のわずかな費用。

**品質ゲート**(babdb49 の後の 5c96cac 時点)
- `tools/sr-regression.ps1`: 全通過(EXIT 0)。
- `tools/pre-merge-check.ps1`: EXIT 0(Editor 639・App 1011 件ほか)。

### Task 8: L5(2026-09-25・windows-mcp と自前のスクリプトによる自動確認・publish babdb49)

**環境**: NVDA 起動中(pid 6372・High 整合性。スピーチビューアーは WM_GETTEXT で読めないので、画面から切り出して読んだ)。kxEdit はスピーチビューアーと重ならない画面右側(585,0)-(1024,710) に置いた。

**方法**(スクリプトは scratchpad に置き捨て)
- `settings.json` を退避したうえで設定を変え(既定 / 現在行強調 ON + 行番号 / 黒地テーマ)、300 行のファイルを Ctrl+O で開いた。
- キーは SendInput で送った。操作のたびに、次の 2 枚を撮って比べた。
  - X: エディタのクライアント領域を、描画を起こさずに撮った絵。画面の画素を `CopyFromScreen` で読む。
  - Y: `RedrawWindow` で全面を描き直させてから撮った絵。
- システムキャレットの点滅の分は、`GetGUIThreadInfo` のキャレット矩形(±3px)を除いて比べた。
- **陽性対照**: 操作の前の絵と Y の差(= その操作で絵が変わったか)も数えた。撮り方が変化を捉えられることを確かめるため。

**結果**(X と Y の差はすべて 0 画素 = 古い絵は残らない)

| 設定 | 操作 | X と Y の差 | 操作による絵の変化 |
|---|---|---|---|
| 既定 | → → ↓ ↓ ↑ ←(強調 OFF の移動) | すべて 0 | すべて 0(描き直しを省いてよい場面) |
| 既定 | Shift+↓・選択の解除・PageDown・↓(スクロール) | すべて 0 | 2,467〜4,739 画素 |
| 現在行強調 ON + 行番号 | 同じ行の → → ← | すべて 0 | すべて 0 |
| 現在行強調 ON + 行番号 | 行が変わる ↓ ↓ ↑・Shift+→・選択の解除 | すべて 0 | 5,689〜11,610 画素 |
| 黒地テーマ | → ↓ Shift+→ 選択の解除 ↑ | すべて 0 | —(陽性対照を足す前の実行) |

- 1 文字の Shift+→ で「絵の変化」が 0 なのは、選択した 1 文字がキャレット矩形の除外範囲に収まったためである。2 文字目以降の選択や行をまたぐ選択では、変化を捉えている。
- **NVDA の発声**(既定の設定。スピーチビューアーを切り出して確認)は、操作どおりだった。
  - → で「ゼロ」(文字)。
  - ↓↑ で行の読み上げ(「L002 あいうえお line 2 テキスト」など)。
  - Shift+→ で「0 選択」など。選択の解除で「選択なし」。
  - PageDown の後の ↓↑ で「L040…」「L039…」。
- 検証後、`%APPDATA%\kxEdit\settings.json` を退避したコピーから戻した(ハッシュの一致を確認)。

**自動では確かめていないこと(ユーザーの実機確認に回す)**
- IME の未確定中のキャレット移動(実 IME の操作)。`--paint-transition` の `ime-cancel-by-move` と、オラクルの IME の操作では確かめた。
- NVDA の視覚的ハイライトの表示位置。利用者の設定で OFF(フェーズ 2 の L5 と同じ)。フェーズ 2 以降、UIA の座標は描画に依存しない。
