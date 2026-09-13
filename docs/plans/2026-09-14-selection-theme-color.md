# 本文選択表示のテーマ連動 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 黒地 3 テーマで本文の選択中テキストが「水色地に白/黄/緑」になって読めない不具合を、
選択色をテーマ表に持たせて `FrameBuilder` のテキスト op を選択境界で分割することで解消する。

**Architecture:** VS Code に追従した混成方式。選択色(背景・前景)を `AppearanceTheme` の
プリセット表に明示的に持たせ、`ViewportStyle` に `PaintColor? SelectionFore`(null = 指定しない)を
追加する。`FrameBuilder` は `SelectionFore` が非 null かつ行が選択と交差するときだけ本文
テキスト op を最大 3 分割する。標準テーマは `SelectionFore` が null なので **PaintOp 列が
現行と 1 op も変わらない**(描画完全不変)。

**Tech Stack:** C# / .NET 9 (net9.0-windows) / WinForms / xUnit / CSharpier(pre-commit)

**設計書:** [2026-09-14-selection-theme-color-design.md](./2026-09-14-selection-theme-color-design.md)
— 方針の根拠(VS Code / Notepad++ の調査)と代案の却下理由はそちらを見ること。

**ブランチ:** `feature/selection-theme-color`(設計書 commit `a6f8f6f` が先頭)

---

## 前提知識(この計画を実行する人向け)

- **`FrameBuilder` は描画を `PaintOp` の並びに落とす純関数層**。`EditorControl.OnPaint` は
  その並びを GDI 呼び出しに置換するだけなので、描画内容は xUnit で検証できる
  (`src/kxEdit.Core/Layout/FrameBuilder.cs` のクラスコメントに重なり順の仕様がある)。
- **`ICharMetrics.MeasureRun` は加算的ではない**。`GdiCharMetrics`
  (`src/kxEdit.Editor/GdiCharMetrics.cs:71`)は非 ASCII を含む run を GDI で一括計測するため、
  `MeasureRun("あい") != MeasureRun("あ") + MeasureRun("い")` になり得る。**分割後の X 座標と
  幅は必ず `PixelMapper.OffsetToPx`(行頭からの prefix 計測)の差分で出すこと**。部分文字列を
  `MeasureRun` すると選択矩形と文字がずれる。
- **`PixelMapper.OffsetToPx` はサロゲートペアの途中に落ちたオフセットを pair 先頭へ前方
  スナップする**(`src/kxEdit.Core/Layout/PixelMapper.cs:24`)。テキストを切り出す側も
  同じ `TextBoundary.SnapToCodePointStart` でスナップしないと、x と文字がずれるか
  サロゲートペアが割れる。
- **テストの色は識別可能な値にする**。`FrameBuilderTests.TestStyle()` は「実装が style から
  色を拾わず default を返していたら落ちる」ように全フィールドを別 RGB で埋めている。
- **CLAUDE.md §4-B のテスト設計則**: no-change テストは非既定状態から始める / partial-selection の
  fixture は prefix と suffix を除外できるものにする。
- **commit は署名でハングする既知の罠がある**。必ず次の形で打つこと(`--no-gpg-sign` /
  `--no-verify` は禁止):
  ```bash
  git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -F <UTF-8 のメッセージファイル>
  ```
  日本語メッセージは heredoc ではなく UTF-8 ファイルに書いて `-F` で渡す。
- **整形は pre-commit フック(CSharpier)が行う**。フックが直した場合は `git add` し直して
  commit をやり直す。

---

## Task 1: `AppearanceTheme` に選択色を追加する

**Files:**
- Modify: `src/kxEdit.Core/Settings/AppearanceTheme.cs`
- Test: `tests/kxEdit.Core.Tests/Settings/AppearanceThemeTests.cs`

### Step 1: 失敗するテストを書く

`tests/kxEdit.Core.Tests/Settings/AppearanceThemeTests.cs` の末尾(クラス内)に追加する。

```csharp
    // 選択色はテーマ表に明示的に持たせる(設計書 §5.1)。値そのものを固定して、
    // 「黒地テーマの選択が水色のまま」という退行を表の時点で捕まえる。
    [Theory]
    [InlineData("default", 0xADD8E6, null)]
    [InlineData("white-on-black", 0xFFFFFF, 0x000000)]
    [InlineData("yellow-on-black", 0xFFFF00, 0x000000)]
    [InlineData("green-on-black", 0x00FF00, 0x000000)]
    public void Selection_colors_are_defined_per_theme(
        string id,
        int expectedSelBack,
        int? expectedSelFore
    )
    {
        var t = AppearanceThemes.ById(id);

        Assert.Equal(expectedSelBack, t.SelectionBackRgb);
        Assert.Equal(expectedSelFore, t.SelectionForeRgb);
    }

    // ハイコントラスト(黒地)テーマだけが選択文字色を持つ、という方針そのものの網。
    // 標準テーマに文字色が入ると「標準テーマは描画不変」という不変条件(設計書 §5.2)が崩れる。
    [Fact]
    public void Only_non_default_themes_specify_selection_foreground()
    {
        foreach (var t in AppearanceThemes.All)
        {
            if (t.Id == "default")
                Assert.Null(t.SelectionForeRgb);
            else
                Assert.NotNull(t.SelectionForeRgb);
        }
    }
```

さらに既存の `All_themes_have_distinct_ids_and_rgb_in_range` の foreach 本体に範囲検査を足す:

```csharp
            Assert.InRange(t.SelectionBackRgb, 0x000000, 0xFFFFFF);
            if (t.SelectionForeRgb is int selFore)
                Assert.InRange(selFore, 0x000000, 0xFFFFFF);
```

### Step 2: 失敗することを確認する

```bash
dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~AppearanceThemeTests"
```
期待: **コンパイルエラー**(`SelectionBackRgb` / `SelectionForeRgb` が存在しない)。

### Step 3: 最小の実装を書く

`src/kxEdit.Core/Settings/AppearanceTheme.cs` を次に置き換える(コメント含む)。

```csharp
namespace kxEdit.Core.Settings;

/// <summary>
/// 配色テーマ（前景/背景/選択は 0xRRGGBB の RGB 値）。UI 非依存のため System.Drawing に依存しない。
/// </summary>
/// <param name="SelectionBackRgb">選択範囲の背景色。</param>
/// <param name="SelectionForeRgb">
/// 選択範囲の文字色。<c>null</c> は「指定しない」＝本文色のまま描くことを意味する
/// (VS Code の <c>editor.selectionForeground</c> と同じ意味論: ハイコントラストのときだけ指定する)。
/// </param>
public sealed record AppearanceTheme(
    string Id,
    string DisplayName,
    int ForeRgb,
    int BackRgb,
    int SelectionBackRgb,
    int? SelectionForeRgb
);

/// <summary>
/// 弱視・ハイコントラスト向けの配色テーマプリセット（カスタム RGB は対象外＝合意）。
/// </summary>
/// <remarks>
/// 選択色の方針(2026-09-14 設計書 §4): 標準テーマは選択文字色を<b>指定しない</b>(従来どおり
/// 薄水色の上に本文色)。黒地 3 テーマはハイコントラスト扱いで<b>反転</b>させる
/// (選択背景 = そのテーマの前景色 / 選択文字 = 黒)。
/// 選択文字色は結果として <c>BackRgb</c> と同値だが、純黒でない背景のテーマが将来入っても
/// 壊れないよう<b>独立したデータとして</b>書く。
/// </remarks>
public static class AppearanceThemes
{
    public static readonly IReadOnlyList<AppearanceTheme> All = new[]
    {
        new AppearanceTheme("default", "標準（白地に黒）", 0x000000, 0xFFFFFF, 0xADD8E6, null),
        new AppearanceTheme("white-on-black", "黒地に白", 0xFFFFFF, 0x000000, 0xFFFFFF, 0x000000),
        new AppearanceTheme("yellow-on-black", "黒地に黄", 0xFFFF00, 0x000000, 0xFFFF00, 0x000000),
        new AppearanceTheme("green-on-black", "黒地に緑", 0x00FF00, 0x000000, 0x00FF00, 0x000000),
    };

    /// <summary>Id からテーマを解決する。未知 Id は標準（先頭）へフォールバック。</summary>
    public static AppearanceTheme ById(string? id)
    {
        foreach (var t in All)
            if (t.Id == id)
                return t;
        return All[0];
    }
}
```

### Step 4: 通ることを確認する

```bash
dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~AppearanceThemeTests"
```
期待: **PASS**(全ケース)。

### Step 5: commit

```bash
git add src/kxEdit.Core/Settings/AppearanceTheme.cs tests/kxEdit.Core.Tests/Settings/AppearanceThemeTests.cs
git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -F <msgfile>
```
メッセージ:
```
feat(core): AppearanceTheme に選択色を持たせる

標準テーマは選択文字色を指定せず(null=本文色のまま)、黒地 3 テーマは
反転(選択背景=テーマ前景色 / 選択文字=黒)にする。設計書 §5.1。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

### Step 6: 仕様レビュー(別エージェント)

CLAUDE.md §3-4。テーマ表の値と方針(設計書 §4)が一致しているか、テストが表の値を
実際に固定しているかを見てもらう。指摘は fixup commit で反映する。

---

## Task 2: `ViewportStyle.SelectionFore` と `FrameBuilder` のテキスト分割

> **このタスクは後続が依存する新しい seam(`SelectionFore`)と分割ロジックを入れる。
> CLAUDE.md §3-4 の前倒しレビュー条件に該当するため、タスク完了時に
> 仕様レビューに加えて<b>コード品質レビュー</b>を実施する(Step 8)。**

**Files:**
- Modify: `src/kxEdit.Core/Layout/Frame.cs:48`(`ViewportStyle`)
- Modify: `src/kxEdit.Core/Layout/FrameBuilder.cs`(本文 op の発行と交差計算)
- Modify: `tests/kxEdit.Core.Bench/Program.cs:782`(ダミー style のコンパイル追従)
- Test: `tests/kxEdit.Core.Tests/Layout/FrameBuilderTests.cs`

### Step 1: 失敗するテストを書く

まず `FrameBuilderTests.TestStyle()` を選択文字色つきに拡張する。**既定は null のまま**にして
既存テストの意味(標準テーマ相当=分割しない)を保ち、分割を見たいテストだけ上書きする。

```csharp
    // 分割前提のテストだけが使う識別用の選択文字色(TestStyle の他の色と重ならない値)。
    private static readonly PaintColor SelFore = new(0xFF00FF);

    // テスト用スタイル: 全フィールドを識別可能な RGB で埋める。
    // (実装が style から色を拾わずに default を返しているとテストが落ちる)
    // selectionFore は既定 null = 標準テーマ相当(選択中も本文色のまま=分割しない)。
    private static ViewportStyle TestStyle(PaintColor? selectionFore = null) =>
        new(
            Foreground: new PaintColor(0x000000),
            Background: new PaintColor(0xFFFFFF),
            CurrentLineBack: new PaintColor(0x88FF88),
            SelectionBack: new PaintColor(0xADD8E6),
            SelectionFore: selectionFore,
            LineNumberFore: new PaintColor(0x777777),
            HighlightOutline: new PaintColor(0xFF8800),
            WhitespaceGlyph: new PaintColor(0xCCCCCC)
        );
```

次に新しいテストクラスを作る(既存の `FrameBuilderTests` が既に大きいので分ける)。

**Create: `tests/kxEdit.Core.Tests/Layout/FrameBuilderSelectionForeTests.cs`**

```csharp
// FrameBuilderSelectionForeTests.cs
// 2026-09-14 設計書: 黒地テーマで選択中テキストが読めない不具合の網。
// ViewportStyle.SelectionFore が非 null のとき、本文 DrawText を選択境界で分割することを固定する。
// 分割しない側(標準テーマ=SelectionFore が null)の不変性も同じ場所で対にして固定する。
using kxEdit.Core.Buffers;
using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Layout;

public class FrameBuilderSelectionForeTests
{
    private static MonoCharMetrics M => new(halfWidthPx: 1, lineHeightPx: 10);

    private static readonly PaintColor Fore = new(0x000000);
    private static readonly PaintColor SelFore = new(0xFF00FF);

    private static ViewportStyle Style(PaintColor? selectionFore) =>
        new(
            Foreground: Fore,
            Background: new PaintColor(0xFFFFFF),
            CurrentLineBack: new PaintColor(0x88FF88),
            SelectionBack: new PaintColor(0xADD8E6),
            SelectionFore: selectionFore,
            LineNumberFore: new PaintColor(0x777777),
            HighlightOutline: new PaintColor(0xFF8800),
            WhitespaceGlyph: new PaintColor(0xCCCCCC)
        );

    /// <summary>本文 DrawText だけを抽出する(行番号・空白グリフを除くため色と Text で絞る)。</summary>
    private static List<PaintOp> BodyText(Frame frame) =>
        frame
            .Ops.Where(op =>
                op.Kind == PaintOpKind.DrawText && (op.Fore == Fore || op.Fore == SelFore)
            )
            .ToList();

    private static Frame Build(
        string text,
        SelectionRange? selection,
        PaintColor? selectionFore,
        int wrapCols = 0
    )
    {
        var buf = TextBuffer.FromString(text);
        var rows = ViewportLayout.Build(
            buf.Current,
            topLine: 0,
            topSegment: 0,
            heightPx: 1000,
            wrapColumns: wrapCols,
            M
        );
        return FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 200,
            clientHeight: 100,
            lineNumberMarginPx: 0,
            currentLineLogical: -1,
            selection: selection,
            cellHighlight: null,
            showWhitespace: false,
            Style(selectionFore),
            M
        );
    }

    // --- 分割する側 ---

    // fixture "abcdef" + 選択 [2,4) は prefix "ab" と suffix "ef" の両方を持つ
    // (全選択・行頭選択・行末選択のいずれとも区別できる=CLAUDE.md §4-B)。
    [Fact]
    public void Partial_selection_splits_body_text_into_three_runs()
    {
        var frame = Build("abcdef", new SelectionRange(2, 4), SelFore);

        var ops = BodyText(frame);

        Assert.Collection(
            ops,
            op => AssertRun(op, "ab", x: 0, w: 2, Fore),
            op => AssertRun(op, "cd", x: 2, w: 2, SelFore),
            op => AssertRun(op, "ef", x: 4, w: 2, Fore)
        );
    }

    // 行頭からの選択では prefix の空 op を出さない(空 op は無害だが、意図せず増えた
    // op が「分割数」を主張するテストを素通りさせるため、個数まで固定する)。
    [Fact]
    public void Selection_from_row_start_emits_two_runs()
    {
        var frame = Build("abcdef", new SelectionRange(0, 2), SelFore);

        Assert.Collection(
            BodyText(frame),
            op => AssertRun(op, "ab", x: 0, w: 2, SelFore),
            op => AssertRun(op, "cdef", x: 2, w: 4, Fore)
        );
    }

    [Fact]
    public void Selection_to_row_end_emits_two_runs()
    {
        var frame = Build("abcdef", new SelectionRange(4, 6), SelFore);

        Assert.Collection(
            BodyText(frame),
            op => AssertRun(op, "abcd", x: 0, w: 4, Fore),
            op => AssertRun(op, "ef", x: 4, w: 2, SelFore)
        );
    }

    // 行まるごとの選択は 1 op だが、色が SelectionFore であることが「分割しない」場合との差。
    [Fact]
    public void Whole_row_selection_emits_single_run_in_selection_color()
    {
        var frame = Build("abcdef", new SelectionRange(0, 6), SelFore);

        Assert.Collection(BodyText(frame), op => AssertRun(op, "abcdef", x: 0, w: 6, SelFore));
    }

    // 折り返しで複数の視覚行にまたがる選択は、視覚行ごとに分割される。
    // 視覚行 0 は "ab"(y=0)・視覚行 1 は "cd"(y=10)。
    [Fact]
    public void Selection_spanning_wrapped_rows_splits_each_row()
    {
        var frame = Build("abcd", new SelectionRange(1, 3), SelFore, wrapCols: 2);

        var ops = BodyText(frame);

        Assert.Collection(
            ops,
            op => AssertRun(op, "a", x: 0, w: 1, Fore, y: 0),
            op => AssertRun(op, "b", x: 1, w: 1, SelFore, y: 0),
            op => AssertRun(op, "c", x: 0, w: 1, SelFore, y: 10),
            op => AssertRun(op, "d", x: 1, w: 1, Fore, y: 10)
        );
    }

    // 論理行をまたぐ選択。改行文字ぶん char オフセットが飛ぶので、行内オフセットへの
    // 変換(SegmentStartChar の減算)を誤ると 2 行目の分割位置がずれる。
    [Fact]
    public void Selection_spanning_logical_lines_splits_each_row()
    {
        var frame = Build("ab\ncd", new SelectionRange(1, 4), SelFore);

        Assert.Collection(
            BodyText(frame),
            op => AssertRun(op, "a", x: 0, w: 1, Fore, y: 0),
            op => AssertRun(op, "b", x: 1, w: 1, SelFore, y: 0),
            op => AssertRun(op, "c", x: 0, w: 1, SelFore, y: 10),
            op => AssertRun(op, "d", x: 1, w: 1, Fore, y: 10)
        );
    }

    // 選択と交差しない行は分割しない(選択が存在する状態で、別の行が巻き込まれないこと)。
    [Fact]
    public void Row_outside_selection_is_not_split()
    {
        var frame = Build("ab\ncd", new SelectionRange(0, 2), SelFore);

        Assert.Collection(
            BodyText(frame),
            op => AssertRun(op, "ab", x: 0, w: 2, SelFore, y: 0),
            op => AssertRun(op, "cd", x: 0, w: 2, Fore, y: 10)
        );
    }

    // 空選択(Start == End)は分割しない。既存の選択矩形のガード(sel.Start < sel.End)と揃える。
    [Fact]
    public void Empty_selection_does_not_split()
    {
        var frame = Build("abcdef", new SelectionRange(3, 3), SelFore);

        Assert.Collection(BodyText(frame), op => AssertRun(op, "abcdef", x: 0, w: 6, Fore));
    }

    // サロゲートペアの途中に落ちた選択でも、文字が欠落も重複もしないこと。
    // OffsetToPx は pair 先頭へ前方スナップするので、文字の切り出しも同じ位置で
    // スナップしないと x と文字がずれるか pair が割れる。
    [Theory]
    [InlineData(1, 2)] // pair の途中で始まり途中で終わる
    [InlineData(0, 2)] // pair の途中で終わる
    [InlineData(2, 4)] // pair の途中で始まる
    public void Selection_inside_surrogate_pair_preserves_all_text(int start, int end)
    {
        // "a" + U+1F600 (サロゲートペア=2 code unit) + "b" = 4 code unit
        var frame = Build("a\uD83D\uDE00b", new SelectionRange(start, end), SelFore);

        string joined = string.Concat(BodyText(frame).Select(op => op.Text));
        Assert.Equal("a\uD83D\uDE00b", joined);
    }

    // --- 分割しない側(標準テーマ相当)---

    // no-change 網。既定状態(選択なし)と区別するため、選択が存在する状態から検証する
    // (CLAUDE.md §4-B)。SelectionFore が null なら本文 op は 1 本のまま=描画不変。
    [Fact]
    public void Null_selection_fore_keeps_single_body_run_even_when_selected()
    {
        var frame = Build("abcdef", new SelectionRange(2, 4), selectionFore: null);

        Assert.Collection(BodyText(frame), op => AssertRun(op, "abcdef", x: 0, w: 6, Fore));

        // アンカー: 選択自体は確かに有効(矩形は塗られている)。これが無いと選択が
        // 効いていない状態でも緑になり、このテストは何も主張しなくなる。
        Assert.Contains(
            frame.Ops,
            op => op.Kind == PaintOpKind.FillRect && op.Back == new PaintColor(0xADD8E6)
        );
    }

    private static void AssertRun(
        PaintOp op,
        string text,
        int x,
        int w,
        PaintColor fore,
        int y = 0
    )
    {
        Assert.Equal(PaintOpKind.DrawText, op.Kind);
        Assert.Equal(text, op.Text);
        Assert.Equal(x, op.X);
        Assert.Equal(w, op.Width);
        Assert.Equal(y, op.Y);
        Assert.Equal(fore, op.Fore);
    }
}
```

### Step 2: 失敗することを確認する

```bash
dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~FrameBuilderSelectionForeTests"
```
期待: **コンパイルエラー**(`ViewportStyle` に `SelectionFore` が無い)。

### Step 3: `ViewportStyle` に `SelectionFore` を足す

`src/kxEdit.Core/Layout/Frame.cs:44-56` のレコードを置き換える。

```csharp
/// <summary>
/// ビューポート描画のパレット(前景/背景/現在行/選択/行番号/ハイライト枠/空白グリフ)。
/// すべての色を明示的に指定する(既定=default(PaintColor) は使わない=RGB 0 と混同されないように)。
/// </summary>
/// <param name="SelectionFore">
/// 選択範囲の文字色。<c>null</c> は「指定しない」= 選択範囲も <see cref="Foreground"/> で描く
/// (VS Code の <c>editor.selectionForeground</c> と同じ意味論)。
/// 非 null のときだけ <see cref="FrameBuilder"/> が本文テキスト op を選択境界で分割する。
/// </param>
public sealed record ViewportStyle(
    PaintColor Foreground,
    PaintColor Background,
    PaintColor CurrentLineBack,
    PaintColor SelectionBack,
    PaintColor? SelectionFore,
    PaintColor LineNumberFore,
    PaintColor HighlightOutline,
    PaintColor WhitespaceGlyph
);
```

コンパイルを通すため、既存の構築箇所 3 つに `SelectionFore` を足す(いずれも名前付き引数なので
`SelectionBack` の直後に 1 行入れるだけ):

- `src/kxEdit.Editor/EditorControl.Paint.cs:142`(`DefaultStyle`)→ `SelectionFore: null,`
- `src/kxEdit.Editor/EditorControl.Paint.cs:167`(`BuildStyle`)→ `SelectionFore: null,`
  (**Task 3 で本実装に差し替える**。ここでは挙動不変のための仮置き)
- `tests/kxEdit.Core.Bench/Program.cs:787` → `SelectionFore: null,`
- `tests/kxEdit.Core.Tests/Layout/FrameBuilderTests.cs:18` → Step 1 の `TestStyle` 拡張で対応済み

### Step 4: `FrameBuilder` に分割を実装する

**(a) 交差計算を切り出す。** `TryComputeRowRangeRect`(`:281`)の冒頭の交差計算を private
ヘルパに抽出し、矩形側からも呼ぶ(選択矩形と文字の境界が同じ計算であることを構造で保証する)。

```csharp
    /// <summary>
    /// 視覚行と char 範囲の交差を求める。交差が空なら false。
    /// 選択矩形(工程 3)と本文の分割(工程 5)が必ず同じ境界を使うよう、計算はここ 1 箇所に置く。
    /// </summary>
    private static bool TryComputeRowIntersection(
        VisualRow row,
        SelectionRange range,
        out int interStart,
        out int interEnd
    )
    {
        int rowStart = row.SegmentStartChar;
        int rowEnd = rowStart + row.SegmentLength;
        interStart = Math.Max(range.Start, rowStart);
        interEnd = Math.Min(range.End, rowEnd);
        return interStart < interEnd;
    }
```

`TryComputeRowRangeRect` の冒頭 `int rowStart = ...` から `}` までの交差計算を、これを呼ぶ形に
書き換える:

```csharp
        if (!TryComputeRowIntersection(row, range, out int interStart, out int interEnd))
        {
            x = 0;
            y = 0;
            w = 0;
            h = 0;
            return false;
        }

        int rowStart = row.SegmentStartChar;
        string text = snapshot.GetText(rowStart, row.SegmentLength);
        // ...(以降は現行のまま)
```

**(b) 本文 op(工程 5)を分割対応にする。** 現行の `// 5) 本文` ループを置き換える。

```csharp
        // 5) 本文
        // SelectionFore が指定されているテーマ(ハイコントラスト)では、選択境界でテキスト op を
        // 分割して選択範囲だけ別色で描く。未指定(null)または選択と交差しない行では
        // 従来どおり 1 op を出す = 標準テーマの PaintOp 列は一切変わらない(設計書 §5.2)。
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            string text =
                row.SegmentLength == 0
                    ? string.Empty
                    : snapshot.GetText(row.SegmentStartChar, row.SegmentLength);

            if (
                style.SelectionFore is PaintColor selFore
                && selection is SelectionRange sel2
                && sel2.Start < sel2.End
                && TryComputeRowIntersection(row, sel2, out int interStart, out int interEnd)
            )
            {
                EmitSplitBodyText(
                    text,
                    bodyX,
                    row.YPx,
                    lineHeight,
                    interStart - row.SegmentStartChar,
                    interEnd - row.SegmentStartChar,
                    style.Foreground,
                    selFore,
                    metrics,
                    ops
                );
                continue;
            }

            int width = text.Length == 0 ? 0 : metrics.MeasureRun(text);
            ops.Add(
                new PaintOp(
                    PaintOpKind.DrawText,
                    bodyX,
                    row.YPx,
                    width,
                    lineHeight,
                    Text: text,
                    Fore: style.Foreground
                )
            );
        }
```

**(c) 分割の実体をクラス末尾(`EmitWhitespaceGlyphs` の後)に足す。**

```csharp
    /// <summary>
    /// 1 視覚行の本文を選択境界で最大 3 つの DrawText に分割して発行する
    /// (prefix=Foreground / 選択内=selectionFore / suffix=Foreground)。空の区間は発行しない。
    /// </summary>
    /// <remarks>
    /// X と幅は必ず <see cref="PixelMapper.OffsetToPx"/>(行頭からの prefix 計測)の差分で出す。
    /// <c>ICharMetrics.MeasureRun</c> は非 ASCII を含む run を一括計測するため加算的ではなく、
    /// 部分文字列を個別に測ると選択矩形(工程 3)と文字がずれる。
    /// オフセットは <see cref="TextBoundary.SnapToCodePointStart"/> で前方スナップする。
    /// OffsetToPx も同じスナップを行うため、ここで揃えないと x と文字がずれ、
    /// サロゲートペアが割れた文字列を描くことになる。
    /// </remarks>
    private static void EmitSplitBodyText(
        string text,
        int bodyX,
        int yPx,
        int lineHeight,
        int selStartInRow,
        int selEndInRow,
        PaintColor fore,
        PaintColor selectionFore,
        ICharMetrics metrics,
        List<PaintOp> ops
    )
    {
        var span = text.AsSpan();
        int selStart = TextBoundary.SnapToCodePointStart(span, Math.Clamp(selStartInRow, 0, text.Length));
        int selEnd = TextBoundary.SnapToCodePointStart(span, Math.Clamp(selEndInRow, 0, text.Length));

        int pxSelStart = PixelMapper.OffsetToPx(span, selStart, metrics);
        int pxSelEnd = PixelMapper.OffsetToPx(span, selEnd, metrics);
        int pxEnd = PixelMapper.OffsetToPx(span, text.Length, metrics);

        Emit(0, selStart, 0, pxSelStart, fore);
        Emit(selStart, selEnd, pxSelStart, pxSelEnd, selectionFore);
        Emit(selEnd, text.Length, pxSelEnd, pxEnd, fore);

        void Emit(int from, int to, int pxFrom, int pxTo, PaintColor color)
        {
            if (from >= to)
                return;
            ops.Add(
                new PaintOp(
                    PaintOpKind.DrawText,
                    bodyX + pxFrom,
                    yPx,
                    pxTo - pxFrom,
                    lineHeight,
                    Text: text[from..to],
                    Fore: color
                )
            );
        }
    }
```

クラスコメント(`:11-22` の重なり順)の工程 5 の行も更新する:

```
///   5) 本文 DrawText(行番号ぶんオフセット済み・<see cref="ViewportStyle.SelectionFore"/> 指定時は
///      選択境界で最大 3 分割)
```

### Step 5: 通ることを確認する

```bash
dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~FrameBuilder"
```
期待: **新規 12 ケースを含めて全 PASS**。既存 `FrameBuilderTests` も無改変で通ること
(通らなければ「標準テーマ相当では描画不変」という前提が崩れているので原因を追う)。

### Step 6: Core 全体の回帰を見る

```bash
dotnet test tests/kxEdit.Core.Tests
```
期待: **全 PASS**。

### Step 7: commit

```
feat(core): 選択範囲の文字色を ViewportStyle に足して本文 op を分割する

SelectionFore (null 許容) が指定されたテーマでのみ、FrameBuilder が本文
DrawText を選択境界で最大 3 分割する。null(標準テーマ相当)では PaintOp 列が
従来と一致する。X と幅は OffsetToPx の差分で出し、選択矩形と境界を共有する。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

### Step 8: レビュー(別エージェント × 2・並走可)

CLAUDE.md §3-4 の前倒しレビュー条件(後続が依存する新しい seam の導入)に該当する。

1. **仕様レビュー** — 実装とテストが設計書 §5.2 のとおりか。
2. **コード品質レビュー** — `SelectionFore` の null 意味論・交差計算の一本化・
   `EmitSplitBodyText` の境界処理(スナップ / clamp / 空区間)を見てもらう。

並走させる場合は各エージェントに scratchpad の専用ディレクトリを割り当て、
**リポジトリ内でのビルドを禁止**する(ワークツリーを共有しているため)。

---

## Task 3: Editor 層の配線(テーマ → `ViewportStyle` → IME overlay)

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.Paint.cs:137-172`(`DefaultStyle` / `BuildStyle`)
- Modify: `src/kxEdit.Editor/EditorControl.Ime.cs:69-72`(`OverlayTargetForeColor`)
- Test: `tests/kxEdit.Editor.Tests/ImeOverlayColorTests.cs`(既存テストの意味を更新)
- Test: `tests/kxEdit.Editor.Tests/EditorControlSelectionColorTests.cs`(新規)

### Step 1: 失敗するテストを書く

`_style` を直接読むテストアクセサは無いが、`IImeOverlayHost` の seam から
`SelectionBackColor`(= `_style.SelectionBack`)と `OverlayTargetForeColor`
(= `_style.SelectionFore ?? _style.Foreground`)が読めるので、新しい seam を足さずに検証する。

**Create: `tests/kxEdit.Editor.Tests/EditorControlSelectionColorTests.cs`**

```csharp
// EditorControlSelectionColorTests.cs
// 2026-09-14 設計書: 選択色がテーマ表から ViewportStyle に載ることを固定する。
// _style を直接読むアクセサは無いので、IImeOverlayHost の seam 経由で読む
// (SelectionBackColor = _style.SelectionBack / OverlayTargetForeColor = SelectionFore ?? Foreground)。
using System.Drawing;
using kxEdit.Core.Buffers;
using kxEdit.Core.Settings;

namespace kxEdit.Editor.Tests;

public class EditorControlSelectionColorTests
{
    [Theory]
    [InlineData("default", 0xADD8E6)]
    [InlineData("white-on-black", 0xFFFFFF)]
    [InlineData("yellow-on-black", 0xFFFF00)]
    [InlineData("green-on-black", 0x00FF00)]
    public void SelectionBack_follows_theme(string themeId, int expectedRgb) =>
        Sta.Run(() =>
        {
            using var f = new Form { Visible = false };
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            c.SetSource(TextBuffer.FromString("abc"));

            c.ApplyAppearance(new AppSettings { Theme = themeId });

            // 色の比較を ToArgb() に統一する理由は ImeOverlayColorTests のコメント参照。
            Assert.Equal(
                Color.FromArgb(255, (expectedRgb >> 16) & 0xFF, (expectedRgb >> 8) & 0xFF, expectedRgb & 0xFF).ToArgb(),
                ((IImeOverlayHost)c).SelectionBackColor.ToArgb()
            );
        });

    // ApplyAppearance 前(ctor 直後の DefaultStyle)は標準テーマと同じ選択背景であること。
    // DefaultStyle はテーマ表と別に固定値を持つので、表を変えたときのドリフトをここで捕まえる。
    [Fact]
    public void SelectionBack_before_ApplyAppearance_matches_default_theme() =>
        Sta.Run(() =>
        {
            using var f = new Form { Visible = false };
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;

            int expected = AppearanceThemes.ById("default").SelectionBackRgb;

            Assert.Equal(
                Color.FromArgb(255, (expected >> 16) & 0xFF, (expected >> 8) & 0xFF, expected & 0xFF).ToArgb(),
                ((IImeOverlayHost)c).SelectionBackColor.ToArgb()
            );
        });
}
```

`ImeOverlayColorTests.OverlayTargetForeColor_StaysBlackForAllThemes` は**そのまま通る**
(4 テーマとも黒のまま=挙動不変)。ただし前提を説明するコメント(`:48-49` の
「固定水色 (0xADD8E6) の選択背景の上に描くため」)が実態とずれるので、次に差し替える:

```csharp
    // 対象節は選択背景 (_style.SelectionBack) の上に描くため、文字色は選択中テキストと
    // 同じ色 (_style.SelectionFore ?? _style.Foreground) を使う。2026-09-14 時点のテーマ表では
    // 4 テーマとも結果が黒になり、#74 の固定黒から挙動は変わらない(設計書 §5.3)。
```

### Step 2: 失敗することを確認する

```bash
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~EditorControlSelectionColorTests"
```
期待: 黒地 3 テーマの `SelectionBack_follows_theme` が **FAIL**
(実際の値は `0xADD8E6` のまま=Task 2 Step 3 の仮置き `SelectionFore: null` と固定水色が残っている)。

### Step 3: 実装する

`src/kxEdit.Editor/EditorControl.Paint.cs` の `BuildStyle`:

```csharp
            SelectionBack: new PaintColor(theme.SelectionBackRgb),
            SelectionFore: theme.SelectionForeRgb is int selFore ? new PaintColor(selFore) : null,
```

`BuildStyle` の doc コメント末尾「選択背景と枠色は現行 App 層と同じ固定値(...)」の一文を
実態に合わせて差し替える:

```
/// 選択色(背景・文字色)は <see cref="AppearanceTheme"/> の表から取る(2026-09-14 設計書 §5.1)。
/// 枠色は現行 App 層と同じ固定値。
```

`DefaultStyle`(`:137`)は ApplyAppearance 前の暫定値なので**固定値のまま**にし、標準テーマと
同値であることをコメントで明示する(値のドリフトは Step 1 のテストが捕まえる):

```csharp
            // 標準テーマ(AppearanceThemes の "default" 行)と同値。ApplyAppearance 前の暫定値。
            SelectionBack: new PaintColor(0xADD8E6),
            SelectionFore: null,
```

`src/kxEdit.Editor/EditorControl.Ime.cs:69-72` を置き換える:

```csharp
    // 対象節は選択背景 (_style.SelectionBack) の上に描くので、選択中テキストの文字色に揃える。
    // SelectionFore 未指定 (標準テーマ) では本文色にフォールバックする。2026-09-14 時点の
    // テーマ表では 4 テーマとも結果が黒で、#74 の固定黒から挙動は変わらない(設計書 §5.3)。
    Color IImeOverlayHost.OverlayTargetForeColor =>
        ToColor(_style.SelectionFore ?? _style.Foreground);
```

### Step 4: 通ることを確認する

```bash
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~SelectionColor"
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~ImeOverlayColorTests"
```
期待: 両方とも **PASS**(`OverlayTargetForeColor_StaysBlackForAllThemes` が 4 ケースとも緑=挙動不変)。

### Step 5: Editor / App の回帰を見る

```bash
dotnet test tests/kxEdit.Editor.Tests
dotnet test tests/kxEdit.App.Tests
```
期待: **全 PASS**。

### Step 6: commit

```
feat(editor): 選択色をテーマ表から ViewportStyle に載せる

BuildStyle の固定水色をテーマ表からの供給に置き換え、IME 変換対象節の文字色を
_style.SelectionFore へ繋ぎ換える(4 テーマとも結果は黒=挙動不変)。
黒地 3 テーマの本文選択が「テーマ前景色地に黒」になる(意図的挙動変更)。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

### Step 7: 仕様レビュー(別エージェント)

配線が設計書 §5.3 のとおりか、`DefaultStyle` を固定値のまま残した判断が妥当かを見てもらう。

---

## Task 4: L5 実機検証チェックリスト

**Files:**
- Create: `docs/plans/2026-09-14-selection-theme-color-l5-checklist.md`

PR #74 の L5 チェックリスト(`docs/plans/2026-09-13-ime-overlay-theme-color-l5-checklist.md`)を
雛形にする。**偽 PASS / 偽 FAIL の芽を潰す**こと(#74 の fixup `249494d` で受けた指摘):
手順に「どのテーマを選ぶか」「何を見れば PASS か」を曖昧さなく書き、判定不能の場合の
扱いも書く。

確認項目:

1. 黒地 3 テーマそれぞれで、本文を選択したテキストが読めること(選択の途中・行頭・行末・
   複数行にまたがる選択)。
2. 同じテーマで IME 変換中の**通常節・対象節**が読めること(#74 の退行が無いこと)。
3. **黄・緑の選択背景が眩しすぎないか**。眩しければ黒地 3 テーマの選択背景を白固定に
   切り替える(設計書 §4.2・`AppearanceThemes` の 2 行の書き換えで済む)。
4. 標準テーマの選択表示が従来どおり(水色地に黒)であること。
5. 日本語(全角)を含む行で、選択矩形と文字の位置がずれていないこと
   (設計書 §8 の ±1px 申し送りの目視確認)。

チェックリストを commit する(`docs(plans): ...`)。

---

## Task 5: 最終レビュー・品質ゲート・PR

### Step 1: 最終ブランチレビュー(2 パス・別エージェント)

CLAUDE.md §3-5。**1 起動に混載しない**。

1. **コード品質パス**(ミューテーション検証のスポットチェック込み)
2. **脆弱性パス**

指摘は **fixup commit** で反映する(元 commit は書き換えない)。指摘への対応は
①修正 / ②PR に記載して受容 / ③理由付き却下 の 3 択で明示する。

並走させる場合は scratchpad の専用ディレクトリを割り当て、リポジトリ内でのビルドを禁止する。

### Step 2: 品質ゲート

```powershell
pwsh tools/pre-merge-check.ps1
```
期待: **EXIT 0** / 0 warning。

`sr-regression.ps1` は**不要**(SR 経路に触れない・設計書 §7)。

### Step 3: push と PR

```bash
git push -u origin feature/selection-theme-color
gh pr create --base main
```

PR description(日本語)に必ず書くこと:

- **目的**: #74 の申し送り(黒地テーマで本文の選択中テキストが読めない)の回収。Issue は
  起票していない(ユーザー判断 2026-09-14)。
- **方針の根拠**: VS Code / Notepad++ の調査(設計書 §3)と混成方式の採用理由。
- **意図的挙動変更**: 黒地 3 テーマの本文選択が「水色地に本文色」→「テーマ前景色地に黒」。
  標準テーマは描画不変。
- **レビュー経緯**: 各タスクの仕様レビュー・Task 2 の前倒しコード品質レビュー・最終 2 パス。
- **申し送り**: WPF 移行ブランチへの反映 / 空白グリフとセルハイライトは非連動 /
  非 ASCII 混在行の ±1px / L5 で黄・緑の選択背景の眩しさを判定し白固定案に切り替える余地。
- **L5 が未実施であること**(マージ前にユーザーへ実機検証を依頼する)。

### Step 4: L5 実機検証(ユーザー依頼)

Task 4 のチェックリストでユーザーに実機確認してもらう。項目 3 の判定次第で
`AppearanceThemes` の 2 行を fixup commit で書き換える。

---

## 完了条件

- [ ] Task 1〜4 が commit 済み
- [ ] 最終レビュー 2 パスの指摘が 3 択で処理済み
- [ ] `tools/pre-merge-check.ps1` が EXIT 0 / 0 warning
- [ ] PR 作成済み(description に意図的挙動変更と申し送りを記載)
- [ ] L5 チェックリストでユーザーの実機検証が完了(**マージの最終ゲート**)
