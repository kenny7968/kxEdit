# IME overlay の文字色テーマ連動(Issue #72)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 黒地テーマで IME 未確定文字列の通常節が背景に溶けて見えない不具合(Issue #72)を、
変換対象節を退行させずに直す。

**Architecture:** overlay の色 seam `IImeOverlayHost` を通常節用(`OverlayForeColor` =
テーマ前景 `_style.Foreground`)と対象節用(`OverlayTargetForeColor` = 固定水色の選択背景に
対するコントラストを確保する黒)に分割し、`ImeController.Draw` の 3 経路で使い分ける。
設計は [2026-09-13-ime-overlay-theme-color-design.md](./2026-09-13-ime-overlay-theme-color-design.md)。

**Tech Stack:** C# / .NET 9 / WinForms(`kxEdit.Editor`)・xUnit(`kxEdit.Editor.Tests`)。
GDI 描画は `TextRenderer`(GDI+ の `Graphics.DrawString` ではない)。

**ブランチ:** `feature/ime-overlay-theme-color`(main から作成済み・設計書 commit 済み)

---

## 前提知識(この変更を触る人向け)

- `EditorControl` は `ControlStyles.UserPaint` の自前描画コントロール。本文は
  `ViewportStyle`(テーマから `BuildStyle` で算出)の色で描くが、IME overlay だけが
  WinForms の `Control.ForeColor`(ctor で黒固定)を使っていた。これが Issue #72 の根因。
- `IImeOverlayHost` は `internal` interface で、`EditorControl` が **explicit interface
  implementation** で実装する(public API に IME 内部を漏らさないため)。テストからは
  `InternalsVisibleTo` により `((IImeOverlayHost)control).OverlayForeColor` で読める。
- `ImeController.Draw` の色以外(節分割・負値防御・Attrs 長不整合防御・フォント・背景塗り・
  `curX` の進め方)は**一切変えない**。変更は色の選択だけ。
- WinForms のコントロールを生成するテストは STA が要る。`Sta.Run(() => { ... })` で包む
  (`tests/kxEdit.Editor.Tests/Sta.cs`)。`ImeController` 単体のテストは Control を作らないので不要。

---

## Task 1: overlay の色 seam を 2 分割してテーマに連動させる

**Files:**
- Modify: `src/kxEdit.Editor/IImeOverlayHost.cs:59-60`
- Modify: `src/kxEdit.Editor/EditorControl.Ime.cs:64`
- Modify: `src/kxEdit.Editor/ImeController.cs:210,251,262`
- Modify: `tests/kxEdit.Editor.Tests/Fakes/FakeImeOverlayHost.cs:25`
- Create: `tests/kxEdit.Editor.Tests/ImeOverlayColorTests.cs`

### Step 1: 失敗するテストを書く(seam のテーマ連動 + Draw の呼び分け)

`tests/kxEdit.Editor.Tests/ImeOverlayColorTests.cs` を新規作成する。

```csharp
// ImeOverlayColorTests.cs
// Issue #72: IME 未確定 overlay の文字色がテーマに連動しない(Control.ForeColor 固定)不具合の網。
// 2 段で固定する:
//   (1) seam の値      : ApplyAppearance 後の OverlayForeColor がテーマ前景・OverlayTargetForeColor が黒
//   (2) Draw の呼び分け: 通常節 / 1 節扱いは OverlayForeColor、対象節は OverlayTargetForeColor で描く
// (2) は実 GDI で Bitmap に描いて画素を見る。ClearType のサブピクセル描画で厳密な色一致は
// 揺れるため、期待色に「十分近い」画素の有無で判定する(色距離のしきい値方式)。
using System.Drawing;
using kxEdit.Core.Buffers;
using kxEdit.Core.Editing;
using kxEdit.Core.Settings;
using kxEdit.Editor.Tests.Fakes;

namespace kxEdit.Editor.Tests;

public class ImeOverlayColorTests
{
    // === (1) seam の値がテーマに連動する ===

    [Theory]
    [InlineData("default", 0x000000)]
    [InlineData("white-on-black", 0xFFFFFF)]
    [InlineData("yellow-on-black", 0xFFFF00)]
    [InlineData("green-on-black", 0x00FF00)]
    public void OverlayForeColor_FollowsThemeForeground(string themeId, int expectedRgb) =>
        Sta.Run(() =>
        {
            using var f = new Form { Visible = false };
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            c.SetSource(TextBuffer.FromString("abc"));

            c.ApplyAppearance(new AppSettings { Theme = themeId });

            var expected = Color.FromArgb(
                255,
                (expectedRgb >> 16) & 0xFF,
                (expectedRgb >> 8) & 0xFF,
                expectedRgb & 0xFF
            );
            Assert.Equal(expected, ((IImeOverlayHost)c).OverlayForeColor);
        });

    // 対象節は固定水色 (0xADD8E6) の選択背景の上に描くため、テーマ色に連動させると
    // 黒地テーマで低コントラストになる(設計書 §3)。全テーマで黒のままであることを固定する。
    [Theory]
    [InlineData("default")]
    [InlineData("white-on-black")]
    [InlineData("yellow-on-black")]
    [InlineData("green-on-black")]
    public void OverlayTargetForeColor_StaysBlackForAllThemes(string themeId) =>
        Sta.Run(() =>
        {
            using var f = new Form { Visible = false };
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            c.SetSource(TextBuffer.FromString("abc"));

            c.ApplyAppearance(new AppSettings { Theme = themeId });

            Assert.Equal(Color.Black, ((IImeOverlayHost)c).OverlayTargetForeColor);
        });

    // === (2) Draw がどちらの色を使うか ===

    // 通常節(Attrs が Input)は OverlayForeColor で描く=黒地テーマで背景に溶けない。
    [Fact]
    public void Draw_NormalClause_UsesOverlayForeColor()
    {
        using var bmp = DrawOverlay(
            attrs: [ImeAttribute.Input, ImeAttribute.Input],
            clauses: [0, 2]
        );

        Assert.True(HasPixelNear(bmp, ForeMarker), "通常節が OverlayForeColor で描かれていない");
    }

    // 節境界が 2 未満のときの 1 節扱い経路も通常節と同じ色。
    [Fact]
    public void Draw_SingleClauseFallback_UsesOverlayForeColor()
    {
        using var bmp = DrawOverlay(attrs: [ImeAttribute.Input, ImeAttribute.Input], clauses: []);

        Assert.True(HasPixelNear(bmp, ForeMarker), "1 節扱い経路が OverlayForeColor で描かれていない");
    }

    // 変換対象節は OverlayTargetForeColor で描く(選択背景に対するコントラスト確保)。
    [Fact]
    public void Draw_TargetClause_UsesOverlayTargetForeColor()
    {
        using var bmp = DrawOverlay(
            attrs: [ImeAttribute.TargetConverted, ImeAttribute.TargetConverted],
            clauses: [0, 2]
        );

        Assert.True(
            HasPixelNear(bmp, TargetMarker),
            "対象節が OverlayTargetForeColor で描かれていない"
        );
        Assert.False(HasPixelNear(bmp, ForeMarker), "対象節に通常節の色が使われている");
    }

    // === ヘルパ ===

    // 2 色を実運用と違う識別しやすい値にして「どちらの seam が使われたか」を画素で判別する。
    private static readonly Color ForeMarker = Color.FromArgb(255, 0, 255, 0); // 通常節
    private static readonly Color TargetMarker = Color.FromArgb(255, 255, 0, 255); // 対象節

    /// <summary>
    /// FakeImeOverlayHost + 実 GDI で overlay を Bitmap に描く。背景は黒(黒地テーマ相当)。
    /// </summary>
    private static Bitmap DrawOverlay(byte[] attrs, int[] clauses)
    {
        var host = new FakeImeOverlayHost
        {
            HasBuffer = true,
            LineHeightPx = 28,
            OverlayForeColor = ForeMarker,
            OverlayTargetForeColor = TargetMarker,
            SelectionBackColor = Color.LightBlue,
        };
        // 細い線がアンチエイリアスで期待色から離れるのを避けるため大きめのフォントで描く。
        using var font = new Font("ＭＳ ゴシック", 24f);
        using var underline = new Font(font, font.Style | FontStyle.Underline);
        using var target = new Font(font, font.Style | FontStyle.Underline | FontStyle.Bold);
        host.Font = font;
        host.UnderlineFont = underline;
        host.TargetFont = target;

        var caret = new CaretController();
        var ctrl = new ImeController(() => new FakeImeContext(), caret, host, _ => { });
        ctrl.__TestApplyComposition("あい", 2, attrs, clauses);

        var bmp = new Bitmap(400, 60);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            ctrl.Draw(g);
        }
        return bmp;
    }

    /// <summary>期待色に十分近い画素が 1 つでもあるか(ClearType の滲みを許容する)。</summary>
    private static bool HasPixelNear(Bitmap bmp, Color expected)
    {
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var p = bmp.GetPixel(x, y);
            int dr = p.R - expected.R,
                dg = p.G - expected.G,
                db = p.B - expected.B;
            if (dr * dr + dg * dg + db * db <= 30 * 30)
                return true;
        }
        return false;
    }
}
```

**注意(テスト設計)**

- `Draw_TargetClause_UsesOverlayTargetForeColor` の 2 つ目の assertion(通常節の色が**無い**こと)が
  「両節が同じ色で描かれる」実装ミスを落とす網。1 つ目だけでは 2 色が同値でも通ってしまう。
- `HasPixelNear` のしきい値 30(ユークリッド色距離)は、ClearType の滲みを許容しつつ
  背景の黒(距離 255 相当)や選択背景の水色とは明確に分離できる幅。
- **`Color` の等値比較の落とし穴**: `System.Drawing.Color` の `Equals` は ARGB だけでなく
  known color かどうかも見るため、`Color.Black.Equals(Color.FromArgb(255, 0, 0, 0))` は
  **false** になる。上のテストは「`ToColor` 由来(known color でない)同士」と
  「`Color.Black` 同士」で比較しているので成立するが、実装側の返し方を変えるときは
  ここが壊れる。ARGB だけを比べたいときは `Assert.Equal(expected.ToArgb(), actual.ToArgb())`
  に倒すこと。
- CLAUDE.md §4-A によりミューテーション検証は**実施しない**(テーマ配色の適用処理は禁止領域)。

### Step 2: ビルドして red を確認する

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
```

期待: **失敗**。`OverlayForeColor` / `OverlayTargetForeColor` が存在しないため
`CS1061`(`IImeOverlayHost` に定義が無い)と `FakeImeOverlayHost` の
`CS0117` 系エラーが出る。ここでエラーが**出ない**場合はテストが seam を見ていないので書き直す。

### Step 3: seam を 2 分割する

`src/kxEdit.Editor/IImeOverlayHost.cs` の以下を置き換える。

```csharp
    /// <summary>overlay 文字色 (通常は <c>Control.ForeColor</c>)。</summary>
    Color ForeColor { get; }
```

↓

```csharp
    /// <summary>
    /// 通常節 (下線のみ) / 1 節扱い経路の文字色。本文と同じテーマ前景 (<c>_style.Foreground</c>)。
    /// Issue #72: ここが <c>Control.ForeColor</c> (ctor で黒固定) だったため、黒地テーマで
    /// 通常節が黒地に黒になって見えなかった。
    /// </summary>
    Color OverlayForeColor { get; }

    /// <summary>
    /// 変換対象節の文字色。<see cref="SelectionBackColor"/> の上に描くので、テーマ前景ではなく
    /// その背景に対してコントラストが取れる色を返す (設計書 §3 の退行回避)。
    /// </summary>
    Color OverlayTargetForeColor { get; }
```

### Step 4: `EditorControl` 側の実装をテーマ連動にする

`src/kxEdit.Editor/EditorControl.Ime.cs:64` の以下を置き換える。

```csharp
    Color IImeOverlayHost.ForeColor => ForeColor;
```

↓

```csharp
    // Issue #72: 旧実装は Control.ForeColor (ctor で黒固定・ApplyAppearance も更新しない) を返しており、
    // 黒地テーマで通常節が背景に溶けていた。本文と同じ _style.Foreground に揃える (意図的挙動変更)。
    Color IImeOverlayHost.OverlayForeColor => ToColor(_style.Foreground);

    // 対象節は _style.SelectionBack (テーマ非連動の固定水色 0xADD8E6) の上に描くため、テーマ前景に
    // 連動させると黒地テーマで水色地に白/黄/緑となり読めなくなる。全テーマで黒を維持する (挙動不変)。
    // SelectionBack をテーマ連動にするとき (設計書 §7 の申し送り) はこの色も合わせて見直すこと。
    Color IImeOverlayHost.OverlayTargetForeColor => Color.Black;
```

`EditorControl.cs:150` の `ForeColor = Color.Black;` は**触らない**(設計書 §4.2)。

### Step 5: `ImeController.Draw` で使い分ける

`src/kxEdit.Editor/ImeController.cs` の 3 箇所の `_host.ForeColor` を置き換える。

- `:210`(`Clauses.Length < 2` の 1 節扱い)→ `_host.OverlayForeColor`
- `:251`(`isTarget` 側の `TextRenderer.DrawText`)→ `_host.OverlayTargetForeColor`
- `:262`(通常節側の `TextRenderer.DrawText`)→ `_host.OverlayForeColor`

あわせて `Draw` の xmldoc(`:186` 付近)の「それ以外は Underline のみで通常前景色」を
「target 節は `OverlayTargetForeColor`・それ以外は `OverlayForeColor`(本文と同じテーマ前景)」に直す。

### Step 6: fake を追随させる

`tests/kxEdit.Editor.Tests/Fakes/FakeImeOverlayHost.cs:25` の
`public Color ForeColor { get; set; } = Color.Black;` を次の 2 行に置き換える。

```csharp
    public Color OverlayForeColor { get; set; } = Color.Black;
    public Color OverlayTargetForeColor { get; set; } = Color.Black;
```

ヘッダコメントの「Draw に必要な Font/Color も個別に差し替え可能 (Draw テストは Graphics 依存の
ため本 fake では扱わない)」は実態と合わなくなるので、「`ImeOverlayColorTests` は Bitmap に
描いて色を検証する」旨に更新する。

### Step 7: green を確認する

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
```

期待: ビルド **0 warning**・テスト **失敗 0**。新規 8 ケース(Theory 4+4 と Fact 3 のうち
Theory は 4 ケースずつ)がすべて緑。既存の `ImeControllerTests` / `EditorControlImeTests` は
無改変で緑のまま(ここが落ちるなら挙動不変の前提が崩れているので原因を追う)。

### Step 8: commit

```powershell
git add src/kxEdit.Editor/IImeOverlayHost.cs src/kxEdit.Editor/EditorControl.Ime.cs src/kxEdit.Editor/ImeController.cs tests/kxEdit.Editor.Tests/Fakes/FakeImeOverlayHost.cs tests/kxEdit.Editor.Tests/ImeOverlayColorTests.cs
```

commit メッセージ(UTF-8 のファイルに書いて `-F` で渡す。SSH 署名は Windows 版 ssh-keygen を指す):

```
fix(editor): IME 未確定 overlay の文字色をテーマに連動させる

overlay の文字色 seam が Control.ForeColor(ctor で黒固定・ApplyAppearance が
更新しない)を返していたため、黒地テーマで未確定文字列の通常節が黒地に黒で
見えなかった(Issue #72)。

seam を通常節用 OverlayForeColor(_style.Foreground=本文と同色)と対象節用
OverlayTargetForeColor(黒を維持)に分割する。対象節は固定水色の選択背景の上に
描くため、テーマ前景に連動させると黒地テーマで逆に読めなくなる。

通常節の文字色がテーマ色になるのは意図的な挙動変更。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

### Step 9: 別エージェントによるレビュー(CLAUDE.md §3-4 / §4)

小変更のため**仕様レビューとコード品質レビューを 1 本に統合**する(§3 の簡略化基準)。
外部入力のパース・パス操作・プロセス起動・WebView・ネットワークのいずれにも触れないため
脆弱性レビューは不要。レビュー観点:

1. `Draw` の 3 経路の色の対応が設計書 §4.3 の表どおりか(1 節扱い=通常色、対象節のみ別色)。
2. 色以外(節分割・負値防御・Attrs 長不整合防御・`curX` の進め方・フォント選択・背景塗り)が
   挙動不変か。
3. テストが「両節が同色」の実装ミスを実際に落とすか(`Assert.False(HasPixelNear(bmp, ForeMarker))`
   を外すと通ってしまわないか)。
4. `HasPixelNear` のしきい値が環境の ClearType 設定に依存して偽陰性を生まないか。

指摘は CLAUDE.md §4 の 3 択(fixup commit / PR description に記載して受容 / 理由付き却下)で
処理し、修正は**別 fixup commit** で積む。

---

## Task 2: L5(実機目視)チェックリストを起こす

**Files:**
- Create: `docs/plans/2026-09-13-ime-overlay-theme-color-l5-checklist.md`

SR の読み上げには影響しない変更だが**見た目の変更**なので、実機での目視確認が最終ゲートになる。
自動テストは「seam の値」と「Draw がどちらの色を使うか」までしか言えず、実際の可読性は
目視でしか判定できない。

チェックリストに入れる項目(既存の `*-l5-checklist.md` の書式に合わせる):

| # | テーマ | 操作 | 期待 |
|---|---|---|---|
| 1 | 標準(白地に黒) | 日本語を入力し変換前(未確定)の状態にする | 通常節が黒字・下線で読める(変更前と同じ) |
| 2 | 標準(白地に黒) | 変換して対象節を作る(スペース/矢印で対象節を移動) | 対象節が水色地に黒の太字下線で読める(変更前と同じ) |
| 3 | 黒地に白 | 1 と同じ | 通常節が**白字**で読める(修正前は不可視) |
| 4 | 黒地に白 | 2 と同じ | 対象節が水色地に黒で読める(退行していない) |
| 5 | 黒地に黄 | 1 / 2 と同じ | 通常節が**黄字**・対象節は水色地に黒 |
| 6 | 黒地に緑 | 1 / 2 と同じ | 通常節が**緑字**・対象節は水色地に黒 |
| 7 | 任意 | 設定ダイアログでテーマを切り替えた直後に未確定文字列を出す | 切替が即座に反映される(`ApplyAppearance` 経路) |

commit: `docs(plans): IME overlay 文字色テーマ連動の L5 チェックリスト`

---

## Task 3: 品質ゲート → PR

### Step 1: 品質ゲート

```powershell
pwsh -File tools/pre-merge-check.ps1
```

期待: **EXIT 0**(0 warning・テスト失敗 0)。`tools/sr-regression.ps1` は SR 経路に
触れないため不要(設計書 §6)。

### Step 2: 実機目視をユーザーに依頼

Task 2 のチェックリストを提示し、黒地 3 テーマでの見え方を確認してもらう。
**ここが最終ゲート**。不可視・低コントラストが残っていれば設計に戻る。

### Step 3: push して PR を作成

```powershell
git push -u origin feature/ime-overlay-theme-color
```

PR description(日本語)に必ず書く:

- 目的: Issue #72 の修正(`Fixes #72`)。
- **意図的挙動変更**: 黒地テーマで未確定の通常節がテーマ前景色で描かれるようになる
  (従来は黒固定)。対象節は全テーマで挙動不変。
- Issue 記載の「1 行修正」を採らなかった理由(設計書 §3 の対象節の退行)。
- レビュー経緯(Task 1 Step 9 の指摘と処理)。
- 申し送り: 本文の選択表示も同じ根(`SelectionBack` がテーマ非連動)で黒地テーマでは読めない。
  別 Issue に切って v0.2 の優先度判断にかける。

### Step 4: 申し送りの Issue を立てる

タイトル案: 「黒地テーマで選択中のテキストが読めない(SelectionBack がテーマ非連動の固定水色)」

本文に入れる内容:

- 症状: 黒地 3 テーマでテキストを選択すると「水色地に白/黄/緑」になり読めない。
- 原因: `BuildStyle`(`src/kxEdit.Editor/EditorControl.Paint.cs:167`)の `SelectionBack` が
  テーマ非連動の固定値 `0xADD8E6`。`FrameBuilder`
  (`src/kxEdit.Core/Layout/FrameBuilder.cs:128,177`)は選択矩形を塗ったうえで、
  テキストは選択の内外を問わず常に `style.Foreground` で描く。
- 修正の方向: `ViewportStyle` に `SelectionFore` を追加し、`FrameBuilder` のテキスト op を
  選択境界で分割する。あわせて `OverlayTargetForeColor` の固定黒(本 PR)も見直す。
- 影響: 弱視ユーザー向けハイコントラストテーマ(CLAUDE.md §2)。SR 読み上げには影響しない。

### Step 5: マージ

PR をマージし、Issue #72 が閉じたことを確認する。
