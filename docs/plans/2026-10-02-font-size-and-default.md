# フォント(font-size-and-default) 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** フォントダイアログで 20pt を選ぶと 20.3 pt と表示・保存される欠陥(項目 4)を、0.5pt 単位への丸めで直す。EditorControl の ctor の既定フォント名が半角「MS ゴシック」で解決していない件(項目 5)は、調査の結果に従って、既定値を `AppSettings` の定数 1 つに寄せる。

**Architecture:** 項目 4 は Core に純関数 `FontSizeRounding.ToHalfPoint` を置き、`DisplaySettingsTab` がダイアログで選んだときだけ通す(読み込んだ設定値は丸めない)。項目 5 は `AppSettings` に `DefaultFontName` / `DefaultFontSize` の定数を置き、`AppSettings` の初期値・`EditorControl` の ctor・`ApplyAppearance` の補完の 3 か所がそれを使う。

**Tech Stack:** C# / .NET 9 / WinForms / xUnit

**Spec:** `docs/plans/2026-09-27-perf-followups-design.md` §3・§11(フェーズ 7)。出典は `docs/plans/2026-09-24-general-perf-improvements-design.md` §15.3・§16.4。

## 0. 項目 5 の調査の結果と結論(計画の作成時に実施)

傘 §3.1 の「最初のタスクで調査する」を、計画の形を決めるために計画の作成時に前倒しした。

### 0.1 既定名を全角にしたときに落ちるテストの件数

- scratchpad のワークツリー(main `9340f70a` から detach)で、`EditorControl.cs:199` を `new Font("ＭＳ ゴシック", 12f)` に変えて Editor.Tests を流した。
- 結果: **失敗 0 / 合格 726 / スキップ 0 / 合計 726**(35 秒)。
- 変更が効いていることを、使い捨てのテストで確かめた。ctor の `_font.Name` / `FontFamily.Name` は、どちらも「ＭＳ ゴシック」に解決した(変更前は Microsoft Sans Serif)。
- 元の設計書 §16.2 の「ピクセル値を固定した箇所が 231」は、どれも相対比較か、`ApplyAppearance` / 明示のフォントで書かれていて、ctor のフォントには依存していなかった。

### 0.2 CI(windows-latest)に MS ゴシックがあるか

- Microsoft の Windows 10 フォント一覧(<https://learn.microsoft.com/en-us/typography/fonts/windows_10_font_list>)では、MS Gothic(msgothic.ttc)は**基本のデスクトップフォントセット**に載っている。Japanese Supplemental Fonts(FOD)に入っているのは MS Mincho・Meiryo・BIZ UD などで、MS Gothic は入っていない。windows-latest は Desktop Experience の Windows Server なので、あると見込まれる。
- ただし「すべてのイメージに既定で入るとは限らない」という但し書きがあるので、**実地で確かめる**。Task 2 で足すテスト(ctor の `_font.Name` が既定名に解決すること)は、フォントが無い環境では Microsoft Sans Serif に解決して落ちる。ブランチを push したときの CI でこのテストが通れば、フォントがあると確定する。
- 仮に CI で落ちても、他のテストの結果は割れない。名前が解決しなければ今と同じ Microsoft Sans Serif で走るだけなので、CI の結果は今と同じになる(ローカルは 0.1 のとおり MS ゴシックで全件合格)。

### 0.3 結論

- **定数を 1 つに寄せる**(傘 §11.2 の第 1 の結論)。落ちる件数は 0 で、CI にもフォントがあると見込まれるため。
- CI の確認で落ちた場合の扱いは Task 2 Step 8 に書く(「理由を付けて閉じる」へ切り替える)。

### 0.4 傘 §11 からの精密化

- **大きさの既定値(12f)も同じ定数に寄せる**。3 か所(`AppSettings`・ctor・`ApplyAppearance` の補完)に同じ形で重複しているので、名前だけ寄せると片割れが残る。値は変わらない。
- **丸めの下限**: 丸めた結果が 0.5pt 未満になるときは 0.5pt にする。288 DPI では 1px = 0.25pt で、2 倍の中点 0.5 が ToEven で 0 に丸まる。0 は `SettingsStore` の補正で 12pt に化ける。96 DPI では起きない(1px = 0.75pt → 1pt)。
- **テストのための口**: `DisplaySettingsTab.PickFont` はモーダルのダイアログを出すので、ダイアログの後の反映を `internal void ApplyPickedFont(Font font)` に切り出してテストする。

### 0.5 意図的な挙動差(PR に記載する)

- 傘 §3.5 のフェーズ 7 の行のとおり(フォントの大きさを 0.5pt 単位に丸めて保存する。96 DPI で 11.5pt を選ぶと 11 になる)。
- ボタンの AccessibleName の読み上げも「20 pt」になる(SR 経路の外)。
- Editor.Tests と Smoke のうち、`ApplyAppearance` を呼ばずに `new EditorControl` を使うものは、Microsoft Sans Serif ではなく MS ゴシック 12pt で走る(製品と同じフォントになる)。製品の挙動は変わらない(製品は `MainForm.CreateEditor` の直後に `ApplyAppearance` を呼ぶ)。

## Global Constraints

- 丸めの規則: **値を 2 倍して `MidpointRounding.ToEven` で整数に丸め、2 で割る**(傘 §11.1)。`AwayFromZero` は採らない(20.25 → 20.5 になる)。
- **丸めるのはダイアログで選んだときだけ**。読み込み時(`LoadFrom`)には丸めない(傘 §11.1)。
- 丸めは Core の純関数にし、**中点のケース(20.25・11.25・9.75・12.75)と中点でないケース(10.5)を両方**テストする(傘 §11.1。CLAUDE.md §4-B)。
- 既定のフォント名は全角「ＭＳ ゴシック」(U+FF2D U+FF33)。
- 変異検証は行わない(傘 §3.3)。陰性対照は TDD の赤で兼ねる(各 Task の Step 2)。陰性対照を別に取る場合は **commit した後に**、行を消さず条件を無効化する形で行い、ビルドの成功を確かめる(失敗すると古い DLL で緑に見える)。後始末の `git checkout -- <file>` の前に `git diff` で変更が対照だけであることを確かめる。
- L5 は不要。目視だけ行う(傘 §3.3。PrintWindow による実解像度の PNG。Task 3)。
- 前倒しレビュー: なし(傘 §3.3 のフェーズ 7 に指定はない。セキュリティ敏感面にも触れない。新しい純関数は後続タスクが依存する seam ではない)。各 Task は別エージェントの仕様レビューを受ける。
- 0 warning(`-warnaserror`)。pre-commit フックを飛ばさない。
- コミットメッセージは `fix|refactor|test|docs(scope): 日本語の要約`。末尾に `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`。
- git commit が無言で止まったら、SSH 署名のハング(`-c gpg.ssh.program=` で Windows 版の ssh-keygen を指す)を疑う。

## Review Focus

1. **高 DPI で 0 に丸まる**: 288 DPI の 1px(0.25pt)を選ぶと、規則どおりなら 0 になり、保存すると 12pt に化ける。下限 0.5pt で止まること(Task 1 Step 1 の `ToHalfPoint_never_returns_below_half_point`)。
2. **96 DPI 以外の値**: 120 DPI では 20pt が 33px → 19.8pt で返る。float の 19.8 は 2 進で割り切れないが、20 に丸まること(Task 1 Step 1 の `19.8f` のケース)。
3. **保存済みの 20.25 を読み込んだだけで丸めない**: 設定ダイアログを開いて何も選ばずに OK すると、値が変わらないこと(Task 1 Step 1 の `Loaded_font_size_is_not_rounded`。非既定値 20.25 から始める)。
4. **フォント名の書き込み漏れ**: 丸めの導入でダイアログの後の反映を切り出すとき、名前の反映を落とさないこと。読み込んだ名前と違う名前を選んで確かめる(Task 1 Step 1 の `Picked_font_size_is_rounded_to_half_point`)。
5. **ctor のフォントが要求値として記録されない**: 定数に寄せた後も、最初の `ApplyAppearance` は ctor と同じ名前・大きさでも必ず作り直すこと(既存の `ApplyAppearance_FirstCall_AlwaysRecreatesEvenIfSameAsCtorFont` を定数に書き換えて残す。Task 2 Step 5)。

---

### Task 1: フォントの大きさを 0.5pt 単位に丸める(項目 4)

**Files:**
- Create: `src/kxEdit.Core/Settings/FontSizeRounding.cs`
- Create: `tests/kxEdit.Core.Tests/Settings/FontSizeRoundingTests.cs`
- Modify: `src/kxEdit.App/Settings/Tabs/DisplaySettingsTab.cs:150-164`(`PickFont` の反映を `ApplyPickedFont` に切り出し、丸めを通す)
- Create: `tests/kxEdit.App.Tests/DisplaySettingsTabTests.cs`

**Interfaces:**
- Produces:
  - `public static float kxEdit.Core.Settings.FontSizeRounding.ToHalfPoint(float points)`
  - `public const float kxEdit.Core.Settings.FontSizeRounding.MinPoints = 0.5f`
  - `internal void kxEdit.App.Settings.Tabs.DisplaySettingsTab.ApplyPickedFont(Font font)`

- [ ] **Step 1: 失敗するテストを書く**

`tests/kxEdit.Core.Tests/Settings/FontSizeRoundingTests.cs`:

```csharp
using kxEdit.Core.Settings;
using Xunit;

namespace kxEdit.Core.Tests.Settings;

/// <summary>
/// フォントダイアログが返した大きさを 0.5pt 単位に丸める規則(2 倍して ToEven で整数に丸め、2 で割る)。
/// 中点のケースと中点でないケースを両方入れる(中点でない値だけでは AwayFromZero や切り捨てと区別できない)。
/// </summary>
public class FontSizeRoundingTests
{
    [Theory]
    [InlineData(20.25f, 20f)] // 96 DPI の 27px。中点 → 偶数(整数 pt)。AwayFromZero なら 20.5
    [InlineData(11.25f, 11f)] // 15px。AwayFromZero なら 11.5
    [InlineData(9.75f, 10f)] // 13px。切り捨てなら 9.5
    [InlineData(12.75f, 13f)] // 17px
    [InlineData(10.5f, 10.5f)] // 14px ちょうど。中点でないので残る
    [InlineData(12f, 12f)]
    [InlineData(19.8f, 20f)] // 120 DPI の 33px。2 進で割り切れない値
    public void ToHalfPoint_rounds_to_half_point_with_midpoint_to_even(
        float input,
        float expected
    ) => Assert.Equal(expected, FontSizeRounding.ToHalfPoint(input));

    [Theory]
    [InlineData(0.25f)] // 288 DPI の 1px。2 倍の中点 0.5 が ToEven で 0 になる
    [InlineData(0.1f)]
    public void ToHalfPoint_never_returns_below_half_point(float input) =>
        Assert.Equal(FontSizeRounding.MinPoints, FontSizeRounding.ToHalfPoint(input));
}
```

`tests/kxEdit.App.Tests/DisplaySettingsTabTests.cs`:

```csharp
using kxEdit.App.Settings.Tabs;
using kxEdit.Core.Settings;

namespace kxEdit.App.Tests;

/// <summary>
/// [表示]タブのフォントの反映(フェーズ 7 の項目 4)。FontDialog は LOGFONT の整数ピクセル高から
/// Font を作り直すので、96 DPI で 20pt を選ぶと 20.25pt で返る。ダイアログで選んだときだけ
/// 0.5pt 単位に丸め、読み込んだ設定値は丸めない。
/// </summary>
public class DisplaySettingsTabTests
{
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c))
                yield return d;
        }
    }

    private static Button FontButton(Control page) =>
        Descendants(page)
            .OfType<Button>()
            .Single(b => b.Text.StartsWith("変更(&F)", StringComparison.Ordinal));

    [Fact]
    public void Picked_font_size_is_rounded_to_half_point() =>
        Sta.Run(() =>
        {
            using var tab = new DisplaySettingsTab();
            var page = tab.BuildPage();
            // 読み込んだ値と違う名前・大きさを選ぶ(名前の書き込み漏れも赤にする)。
            tab.LoadFrom(new AppSettings { FontName = "Arial", FontSize = 9f });

            using var picked = new Font("Consolas", 20.25f);
            tab.ApplyPickedFont(picked);

            var r = new AppSettings();
            tab.SaveTo(r);
            Assert.Equal("Consolas", r.FontName);
            Assert.Equal(20f, r.FontSize);
            Assert.Equal("フォント変更 現在 Consolas, 20 pt", FontButton(page).AccessibleName);
        });

    [Fact]
    public void Loaded_font_size_is_not_rounded() =>
        Sta.Run(() =>
        {
            using var tab = new DisplaySettingsTab();
            _ = tab.BuildPage();
            // 非既定の 20.25 から始める(既定の 12 のままでは、丸めても丸めなくても同じ)。
            tab.LoadFrom(new AppSettings { FontName = "Consolas", FontSize = 20.25f });

            var r = new AppSettings();
            tab.SaveTo(r);
            Assert.Equal(20.25f, r.FontSize);
        });
}
```

`Sta` は `tests/kxEdit.App.Tests/Sta.cs` の既存の補助。`Consolas`・`Arial` は Windows の基本フォントなので CI でも解決する(`Font.Name` は解決した名前を返すため、解決しないフォントは使わない)。

- [ ] **Step 2: テストが失敗することを確かめる**

Run: `dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~FontSizeRoundingTests"`
Expected: ビルド失敗(`FontSizeRounding` が無い)。

Run: `dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~DisplaySettingsTabTests"`
Expected: ビルド失敗(`ApplyPickedFont` が無い)。

- [ ] **Step 3: 純関数を書く**

`src/kxEdit.Core/Settings/FontSizeRounding.cs`:

```csharp
namespace kxEdit.Core.Settings;

/// <summary>
/// フォントダイアログが返した大きさ(pt)を 0.5pt 単位に丸める純関数。
/// FontDialog は LOGFONT の整数ピクセル高から Font を作り直すので、96 DPI では
/// 20pt → 26.67px → 27px → 20.25pt になり、「20.3 pt」と表示・保存されていた。
/// </summary>
public static class FontSizeRounding
{
    /// <summary>
    /// 丸めた結果の下限(pt)。288 DPI では 1px = 0.25pt で、2 倍の中点 0.5 が 0 に丸まる。
    /// 0 は設定の読み込みで既定の 12pt に補正されてしまうため、ここで止める。
    /// </summary>
    public const float MinPoints = 0.5f;

    /// <summary>
    /// 2 倍して <see cref="MidpointRounding.ToEven"/> で整数に丸め、2 で割る。
    /// 96 DPI の値は 0.75pt の倍数なので、奇数ピクセルでは必ず x.5 の中点になる。
    /// ToEven は中点を偶数(= 整数 pt)に寄せる(20.25 → 20、9.75 → 10)。中点でない値は残る(10.5 → 10.5)。
    /// </summary>
    public static float ToHalfPoint(float points)
    {
        double doubled = Math.Round((double)points * 2, MidpointRounding.ToEven);
        return Math.Max(MinPoints, (float)(doubled / 2));
    }
}
```

- [ ] **Step 4: タブの反映を切り出して丸めを通す**

`src/kxEdit.App/Settings/Tabs/DisplaySettingsTab.cs` の `PickFont` を次に置き換え、直後に `ApplyPickedFont` を足す。

```csharp
    private void PickFont()
    {
        // タブは Form ではないため、親ダイアログは FindForm() で取得する（フォーカスが親に戻る挙動を保つ）。
        using var dlg = new FontDialog
        {
            Font = SafeFont(),
            ShowEffects = false,
            FontMustExist = true,
        };
        if (dlg.ShowDialog(_fontButton.FindForm()) != DialogResult.OK)
            return;
        ApplyPickedFont(dlg.Font);
    }

    /// <summary>
    /// フォントダイアログで選んだフォントを反映する。大きさは 0.5pt 単位に丸める
    /// (<see cref="FontSizeRounding.ToHalfPoint"/>。FontDialog は整数ピクセル高から作り直すので、
    /// 96 DPI で 20pt が 20.25pt で返る)。読み込んだ設定値(<see cref="LoadFrom"/>)は丸めない。
    /// ダイアログを出さずにテストするため internal。
    /// </summary>
    internal void ApplyPickedFont(Font font)
    {
        _fontName = font.Name;
        _fontSize = FontSizeRounding.ToHalfPoint(font.Size);
        UpdateFontLabel();
    }
```

`using kxEdit.Core.Settings;` は既にある。

- [ ] **Step 5: テストが通ることを確かめる**

Run: `dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~FontSizeRoundingTests"`
Expected: PASS(9 ケース)。

Run: `dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~DisplaySettingsTabTests"`
Expected: PASS(2 件)。

- [ ] **Step 6: Commit**

```bash
git add src/kxEdit.Core/Settings/FontSizeRounding.cs tests/kxEdit.Core.Tests/Settings/FontSizeRoundingTests.cs src/kxEdit.App/Settings/Tabs/DisplaySettingsTab.cs tests/kxEdit.App.Tests/DisplaySettingsTabTests.cs
git commit -m "fix(settings): フォントダイアログで選んだ大きさを 0.5pt 単位に丸める

FontDialog は整数ピクセル高から Font を作り直すので、96 DPI で 20pt を
選ぶと 20.25pt が返り、20.3 pt と表示・保存されていた。2 倍して ToEven で
丸め、2 で割る。読み込んだ設定値は丸めない。

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 7: 丸めを外すと App のテストが落ちることを確かめる(陰性対照。commit の後に行う)**

`ApplyPickedFont` の `FontSizeRounding.ToHalfPoint(font.Size)` を `font.Size` に変えて App のテストを流す。
Expected: ビルドは成功し、`Picked_font_size_is_rounded_to_half_point` が `20.25` で FAIL。`git diff` で変更がこの 1 行だけであることを確かめてから `git checkout -- src/kxEdit.App/Settings/Tabs/DisplaySettingsTab.cs` で戻す。

---

### Task 2: 既定のフォントを定数 1 つに寄せる(項目 5)

**Files:**
- Modify: `src/kxEdit.Core/Settings/AppSettings.cs:9-10`(定数を足し、初期値に使う)
- Modify: `src/kxEdit.Editor/EditorControl.cs:199`(ctor)
- Modify: `src/kxEdit.Editor/EditorControl.cs:2811-2814`(`ApplyAppearance` の補完)
- Modify: `tests/kxEdit.Editor.Tests/ApplyAppearanceFontReuseTests.cs`(ctor のテストを足し、既定値の文字列を定数に寄せる)

**Interfaces:**
- Produces:
  - `public const string kxEdit.Core.Settings.AppSettings.DefaultFontName = "ＭＳ ゴシック"`
  - `public const float kxEdit.Core.Settings.AppSettings.DefaultFontSize = 12f`

- [ ] **Step 1: 失敗するテストを書く**

`tests/kxEdit.Editor.Tests/ApplyAppearanceFontReuseTests.cs` の `ApplyAppearance_SameFontTwice_ReusesFontAndMetrics` の前に足す。

```csharp
    /// <summary>
    /// フェーズ 7 の項目 5: ctor の描画フォントは製品の既定(<see cref="AppSettings.DefaultFontName"/>・
    /// <see cref="AppSettings.DefaultFontSize"/>)と同じで、名前が実際に解決している。
    /// <c>Font.Name</c> は GDI+ が解決した実フォント名で、名前が解決しないと Microsoft Sans Serif になる
    /// (半角「MS ゴシック」だった頃はそうだった)。CI(windows-latest)に MS ゴシックがあることの確認も兼ねる。
    /// </summary>
    [Fact]
    public void Ctor_DrawFont_IsProductDefaultAndResolves() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                var font = DrawFont(c);
                Assert.Equal(AppSettings.DefaultFontName, font.Name);
                Assert.Equal(AppSettings.DefaultFontSize, font.Size);
            }
        });
```

- [ ] **Step 2: テストが失敗することを確かめる**

Run: `dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~Ctor_DrawFont_IsProductDefaultAndResolves"`
Expected: ビルド失敗(`AppSettings.DefaultFontName` が無い)。

- [ ] **Step 3: 定数を足す**

`src/kxEdit.Core/Settings/AppSettings.cs` の `FontName` / `FontSize` の 2 行を次に置き換える。

```csharp
    /// <summary>
    /// 既定のフォント名(全角「ＭＳ」)。<c>EditorControl</c> の ctor と <c>ApplyAppearance</c> の補完も
    /// これを使う(半角「MS ゴシック」は解決せず、Microsoft Sans Serif に落ちる)。
    /// </summary>
    public const string DefaultFontName = "ＭＳ ゴシック";

    /// <summary>既定のフォントの大きさ(pt)。<see cref="DefaultFontName"/> と同じく 3 か所で共有する。</summary>
    public const float DefaultFontSize = 12f;

    public string FontName { get; set; } = DefaultFontName;
    public float FontSize { get; set; } = DefaultFontSize;
```

- [ ] **Step 4: ctor と `ApplyAppearance` を定数に寄せる**

`src/kxEdit.Editor/EditorControl.cs:199`:

```csharp
        // 製品の既定と同じフォントで始める(フェーズ 7 の項目 5。以前は半角「MS ゴシック」で解決せず、
        // Microsoft Sans Serif に落ちていた)。製品では直後の ApplyAppearance が設定値に置き換える。
        // ctor のフォントは要求値(_appliedFont)として記録しないので、最初の ApplyAppearance は必ず作り直す。
        _font = new Font(AppSettings.DefaultFontName, AppSettings.DefaultFontSize);
```

`src/kxEdit.Editor/EditorControl.cs:2811-2814`:

```csharp
        var request = new FontRequest(
            string.IsNullOrEmpty(settings.FontName) ? AppSettings.DefaultFontName : settings.FontName,
            settings.FontSize > 0 ? settings.FontSize : AppSettings.DefaultFontSize
        );
```

`using kxEdit.Core.Settings;` は既にある。

- [ ] **Step 5: 既存テストの既定値の文字列を定数に寄せる**

`ApplyAppearanceFontReuseTests.cs` の `ApplyAppearance_FirstCall_AlwaysRecreatesEvenIfSameAsCtorFont` の 2 行を次に置き換える。

```csharp
                // ctor と同じ名前・大きさ。ctor のフォントは要求値として記録されていない。
                c.ApplyAppearance(
                    new AppSettings
                    {
                        FontName = AppSettings.DefaultFontName,
                        FontSize = AppSettings.DefaultFontSize,
                    }
                );
```

`ApplyAppearance_EmptyNameAndZeroSize_EqualToExplicitDefaults_Reuses` の最初の `ApplyAppearance` も同じ形にし、コメントを次にする。

```csharp
                c.ApplyAppearance(
                    new AppSettings
                    {
                        FontName = AppSettings.DefaultFontName,
                        FontSize = AppSettings.DefaultFontSize,
                    }
                );
                var metrics = c.Metrics;

                // 補完後は既定の名前・大きさ=同じフォントが作られるので使い回す。
```

`ApplyAppearance_SameFontTwice_ReusesFontAndMetrics` のコメント「既定(ＭＳ ゴシック 12pt)ではない状態から始める」は値の説明なので残す。`GdiCharMetricsCacheTests` などがテスト用に明示している半角「MS ゴシック」は、ctor と関係しないので変えない(傘 §11.2 の範囲外)。

- [ ] **Step 6: テストが通ることを確かめる**

Run: `dotnet test tests/kxEdit.Editor.Tests`
Expected: 合格 727 / 失敗 0(0.1 の 726 件 + 本 Task の 1 件)。

Run: `dotnet test tests/kxEdit.Core.Tests`
Expected: 全件合格。

- [ ] **Step 7: Commit**

```bash
git add src/kxEdit.Core/Settings/AppSettings.cs src/kxEdit.Editor/EditorControl.cs tests/kxEdit.Editor.Tests/ApplyAppearanceFontReuseTests.cs
git commit -m "fix(editor): ctor の既定フォントを設定の既定値の定数に寄せる

ctor は半角「MS ゴシック」で名前が解決せず、Microsoft Sans Serif で
走っていた。製品は直後の ApplyAppearance で置き換えるので影響しない。
既定の名前と大きさを AppSettings の定数 1 つにまとめ、ctor・
ApplyAppearance の補完・設定の初期値で共有する。

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 8: CI で MS ゴシックが解決することを確かめる(0.2)**

ブランチを push する(CLAUDE.md §7 の push を前倒しする。ci.yml は全ブランチの push で走る)。

```bash
git push -u origin feature/font-size-and-default
gh run watch --exit-status $(gh run list --branch feature/font-size-and-default --limit 1 --json databaseId --jq '.[0].databaseId')
```

(`gh run list` は push の直後だと前の run を返すことがある。`headSha` が `git rev-parse HEAD` と一致することを確かめる。)

Expected: 「Editor.Tests(LocalOnly 除外)」を含めて CI が成功する。`Ctor_DrawFont_IsProductDefaultAndResolves` が通れば、CI に MS ゴシックがあると確定する。

**CI でこのテストだけが `Microsoft Sans Serif` で落ちた場合**(フォントが無い)は、結論を「理由を付けて閉じる」(傘 §11.2 の第 2 の結論)に切り替える。
1. Step 7 の commit を `git revert` で戻す(履歴は書き換えない)。
2. ctor の行に次のコメントを足して commit する(`fix(editor)` ではなく `docs(editor)`)。

```csharp
        // 半角「MS ゴシック」は解決せず Microsoft Sans Serif になる。製品では直後の ApplyAppearance が
        // 設定値(既定は全角「ＭＳ ゴシック」)に置き換えるので影響しない。全角に寄せないのは、
        // CI(windows-latest)に MS ゴシックが無く、ローカルと CI でテストのフォントが割れるため
        // (フェーズ 7 の項目 5。docs/plans/2026-10-02-font-size-and-default.md §0)。
        // Editor.Tests は比例フォントでも安定して動くように、相対比較か明示のフォントで書かれている。
```

3. 実施記録(Task 3 Step 4)に、CI の失敗のログを根拠として書く。

---

### Task 3: 目視・最終レビュー・品質ゲート・実施記録

**Files:**
- Modify: `docs/plans/2026-09-27-perf-followups-design.md`(§11 の末尾に「11.3 実施記録」を足す)

- [ ] **Step 1: 目視(PrintWindow の実解像度 PNG で、変更前後の描画を比べる)**

傘 §11.1 の「行高の float 計算で 1px 変わりうる」を確かめる。ビルドしたアプリ(`src/kxEdit.App/bin/Debug/net9.0-windows/kxEdit.exe`)を使う。

1. `%APPDATA%\kxEdit\settings.json` を退避する(`Copy-Item` で scratchpad へ)。
2. `FontSize` を `20.25` にして起動し、同じ文書(英数字・日本語・複数行。scratchpad に UTF-8 で作る)を Ctrl+O で開く。ウィンドウを `PrintWindow` で実解像度の PNG にする。
3. 閉じて、`FontSize` を `20` にして同じ手順で PNG にする。
4. 2 枚の画素を比べる(差分の画素数と、差分の外接矩形)。差分が無ければ PASS。差分があれば、関心領域を NearestNeighbor で 4〜8 倍にした PNG を Read で見て、行高のずれかどうかを記録し、ユーザーに判定を渡す。
5. メニューから設定ダイアログを開き、[表示]タブで、フォントの変更から 20pt を選び、表示が「…, 20 pt」になることを PNG で確かめる。
6. `settings.json` を退避したものに戻す。

ヘルパーは scratchpad に `l5cap.ps1` として作る(`GetWindowRect` + `PrintWindow`、`Bitmap` の画素比較)。モーダルが出ていると入力が吸われるので、おかしいときは全画面のスクリーンショットでモーダルの有無を見る。

- [ ] **Step 2: 最終レビュー(2 パス)**

別々のエージェントで、ブランチ全体(`git diff main...HEAD`)を次の 2 パスでレビューする(CLAUDE.md §3 の 5)。
- **コード品質パス**: 傘 §11 との一致、§0.4 の精密化、テストが空振りしないこと(中点のケース・非既定値から始めること)、コメントの正確さ。変異検証のスポットチェックは行わない(傘 §3.3)。
- **脆弱性パス**: 設定値の扱い(丸めの下限・0 や負の値・保存済みの値の扱い)。

指摘は CLAUDE.md §4 の 3 択(fixup / PR に記載して受容 / 理由付き却下)で扱う。fixup は `git commit --fixup=<sha>` で積む。

- [ ] **Step 3: 品質ゲート**

Run: `pwsh -NoProfile -File tools/pre-merge-check.ps1 2>&1 | Out-File -Encoding utf8 <scratchpad>/pre-merge.log; $LASTEXITCODE`
Expected: `0`。

- [ ] **Step 4: 実施記録を書く**

`docs/plans/2026-09-27-perf-followups-design.md` の §11.2 の後(§12 の前)に「### 11.3 実施記録(2026-10-02)」を足す。他のフェーズの実施記録と同じ構成(成果物・完了条件・本節からの精密化・申し送り)で、次を書く。
- 項目 5 の調査の結果(§0.1・§0.2)と結論、CI の確認の結果
- 項目 4 の丸め、テストの件数、陰性対照の結果
- 目視の結果
- 本計画 §0.4 の精密化と §0.5 の挙動差
- レビューの経緯

- [ ] **Step 5: Commit**

```bash
git add docs/plans/2026-09-27-perf-followups-design.md
git commit -m "docs(perf): フェーズ 7(フォント)の実施記録

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 6: PR を作る**

superpowers:finishing-a-development-branch に従う。PR description(日本語)に、目的・§0.5 の挙動差・目視の結果・L5 不要の理由・レビューの経緯・申し送りを書く。
