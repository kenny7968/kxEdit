# 設定ダイアログの寸法決定 実装計画 (Issue #68)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 136×89 px に潰れてマウス操作できない設定ダイアログを、全タブページが収まる寸法で開くようにする。

**Architecture:** `TabControl` がページ内容から推奨サイズを算出しない(常に既定 200×100)という
WinForms の仕様のため、Form の `AutoSize` は使えない。全ページ本体の `GetPreferredSize` の最大値に
タブ枠とボタン列を足した値を `SettingsDialog` の `ClientSize` に代入する。併せて、正しい寸法を
与えた瞬間に表面化する 2 つ目の欠陥(Dock の追加順が逆で TabControl がボタン列を覆う)を是正する。
寸法計算は `SettingsTabLayoutHelper` の internal static メソッドに置き、フォント追従の網を張れるようにする。

**Tech Stack:** .NET 9 / WinForms / xUnit v2(STA ヘルパ `Sta.Run`)/ CSharpier / Husky.Net

**設計書:** [2026-09-04-settings-dialog-size-design.md](./2026-09-04-settings-dialog-size-design.md)

---

## 前提: この計画のコードは実測済み(ただし正解の保証ではない)

設計書 §2 の実測に加え、**この計画に載せた `ComputeDialogClientSize` の実装案そのもの**を
スクラッチのコンソールプローブで動かし、フォント倍率 1.0 / 1.25 / 1.5 / 2.0 のすべてで

- 全 5 ページが収まる(**最小余白 0×0 = 過不足なし**)
- TabControl とボタン列が重ならない
- OK / キャンセルがクライアント領域内に入る

ことを確認した。実装中に食い違ったら、**計画ではなく実物を信じて計画を直すこと**。

途中で否定された仮説(採用しないこと):

| 仮説 | 実測結果 |
|---|---|
| Issue 記載の `_tabControl.MinimumSize` を与える | **Form は 136×89 のまま**。Form の AutoSize は Dock=Fill の子の希望を見ない |
| 枠を測るため Dock 済みの `_tabControl.Size` に仮寸法を代入する | **レイアウトに即座に上書きされ**枠が `{3820, 3913}` になり、ダイアログが 1940×1100 に膨張 |
| 枠測定用 probe に `TabPage` を載せない | ヘッダ帯が現れず枠が `8×8`(正しくは `8×28` @96DPI)。**1 枚は必要**(1 枚と 5 枚は同値) |

---

## Task 1: 回帰テストを書いて「現状の src で赤」を確認する

**Files:**
- Create: `tests/kxEdit.App.Tests/SettingsDialogLayoutTests.cs`

**Step 1: テストファイルを作る**

```csharp
using kxEdit.Core.Settings;

namespace kxEdit.App.Tests;

/// <summary>
/// 設定ダイアログの寸法決定を固定する(Issue #68)。
/// TabControl はページの中身から推奨サイズを算出せず常に既定 200x100 を返すため、
/// Form の AutoSize に委ねると 136x89 に潰れてマウス操作できなくなっていた。
/// キーボード / UIA 経路は生きたままなので SR 中心の検証では見逃される
/// (CLAUDE.md §2「晴眼・弱視ユーザーも第一級」)。
/// ピクセル即値ではなく包含関係で見るので DPI・フォントに依らない
/// (既存 EditSettingsTabTests と同じ流儀)。
/// 実際の見え方・読み上げは L5 実機検証でしか確認できない(CLAUDE.md §2 a11y 鉄則)。
/// </summary>
public class SettingsDialogLayoutTests
{
    /// <summary>フォームを画面外に可視化する(レイアウト確定に Show が要る)。</summary>
    private static SettingsDialog ShowOffScreen()
    {
        var dlg = new SettingsDialog(new AppSettings())
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ShowInTaskbar = false,
        };
        dlg.Show();
        return dlg;
    }

    private static T Child<T>(Control root)
        where T : Control => root.Controls.OfType<T>().Single();

    [Fact]
    public void Every_tab_page_fits_its_content() =>
        Sta.Run(() =>
        {
            using var dlg = ShowOffScreen();
            var tabs = Child<TabControl>(dlg);
            Assert.NotEmpty(tabs.TabPages);

            foreach (TabPage page in tabs.TabPages)
            {
                // 未選択のページはレイアウトされない(実測: 選択するまで 112x22 のまま)。
                // 全ページを検査するには必ず選択してから測る。
                tabs.SelectedTab = page;
                dlg.PerformLayout();

                var body = page.Controls.OfType<Control>().Single();
                var want = body.GetPreferredSize(Size.Empty);
                Assert.True(
                    page.ClientSize.Width >= want.Width && page.ClientSize.Height >= want.Height,
                    $"タブ「{page.Text}」の表示領域 {page.ClientSize} が本体の希望サイズ {want} を収められない"
                );
            }
        });

    [Fact]
    public void Tab_control_and_button_row_do_not_overlap() =>
        Sta.Run(() =>
        {
            // Dock は子インデックスの大きい方から確定する。Dock=Bottom のボタン列を先に Add すると
            // Dock=Fill の TabControl がクライアント全面を取りボタン列を覆う。
            // (潰れていた間は表面化していなかった 2 つ目の欠陥。DocumentInfoDialog は正しい順序。)
            using var dlg = ShowOffScreen();
            var tabs = Child<TabControl>(dlg);
            var buttons = Child<FlowLayoutPanel>(dlg);
            var client = new Rectangle(Point.Empty, dlg.ClientSize);

            Assert.False(
                tabs.Bounds.IntersectsWith(buttons.Bounds),
                $"TabControl {tabs.Bounds} とボタン列 {buttons.Bounds} が重なっている"
            );
            Assert.True(client.Contains(tabs.Bounds), $"TabControl {tabs.Bounds} がクライアント領域 {client} からはみ出している");
            Assert.True(client.Contains(buttons.Bounds), $"ボタン列 {buttons.Bounds} がクライアント領域 {client} からはみ出している");

            // 潰れの直接の被害(OK が画面外・キャンセルが半分だけ)をそのまま固定する。
            foreach (Control b in buttons.Controls)
            {
                var abs = new Rectangle(b.Left + buttons.Left, b.Top + buttons.Top, b.Width, b.Height);
                Assert.True(client.Contains(abs), $"ボタン「{b.Text}」{abs} がクライアント領域 {client} の外にある");
            }
        });
}
```

> `Size` / `Point` / `Rectangle` は暗黙 using(`ImplicitUsings=enable` + `UseWindowsForms`)で入る。
> `SettingsDialog` は `kxEdit.App.Settings` 名前空間なので `using kxEdit.App.Settings;` が要る場合は足す
> (`GlobalUsings.cs` に `kxEdit.App` はあるが `.Settings` は無い)。

**Step 2: 現状の src に対して走らせ、赤を確認する**

```
dotnet build kxEdit.sln -c Debug -warnaserror
dotnet test tests/kxEdit.App.Tests -c Debug --no-build --filter "FullyQualifiedName~SettingsDialogLayoutTests"
```

期待: **2 本とも FAIL**。`$LASTEXITCODE` が 0 でないことと、失敗したテスト名 2 件を目視する
(exit code だけでなく件数まで見る。ビルドが割れても exit 1 になるため区別が要る)。

想定される失敗メッセージ:
- `Every_tab_page_fits_its_content` → `タブ「基本」の表示領域 {Width=112, Height=22} が本体の希望サイズ {Width=382, Height=138} を収められない`
- `Tab_control_and_button_row_do_not_overlap` → `TabControl {X=0,Y=0,Width=120,Height=50} とボタン列 {X=0,Y=-34,Width=120,Height=84} が重なっている`

**Step 3: コミットしない**

赤のままコミットしない。Task 2 の実装と一緒に 1 commit にする(CLAUDE.md §3 簡略化の基準)。

---

## Task 2: 寸法計算ヘルパを足し、SettingsDialog に適用する

**Files:**
- Modify: `src/kxEdit.App/Settings/SettingsTabLayoutHelper.cs`(末尾にメソッド 2 つ追加)
- Modify: `src/kxEdit.App/Settings/SettingsDialog.cs`(ctor の AutoSize 削除・`BuildLayout` の末尾)

**Step 1: `SettingsTabLayoutHelper` に寸法計算を足す**

`NewRoot()` の後ろに追加する。

```csharp
    /// <summary>
    /// 設定ダイアログのクライアント寸法を、全タブページの希望サイズから算出する。
    /// <see cref="TabControl"/> はページの中身から推奨サイズを算出せず常に既定の 200x100 を
    /// 返すため、Form の AutoSize に委ねると 136x89 に潰れる(Issue #68・実測)。
    /// Form.AutoSize は Dock=Fill の子の希望サイズを見ないので、TabControl 側に
    /// MinimumSize を与えても直らない(実測で否定済み)。
    /// 即値は一切使わず枠も実測するため、フォント・DPI に自動追従する。
    /// </summary>
    public static Size ComputeDialogClientSize(TabControl tabs, Control buttons)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(buttons);

        var body = Size.Empty;
        foreach (TabPage page in tabs.TabPages)
        {
            foreach (Control c in page.Controls)
            {
                var want = c.GetPreferredSize(Size.Empty);
                body = new Size(
                    Math.Max(body.Width, want.Width),
                    Math.Max(body.Height, want.Height)
                );
            }
        }

        var frame = MeasureTabFrame(tabs.Font);
        var buttonRow = buttons.GetPreferredSize(Size.Empty);
        return new Size(
            Math.Max(body.Width + frame.Width, buttonRow.Width),
            body.Height + frame.Height + buttonRow.Height
        );
    }

    /// <summary>
    /// タブ枠(ヘッダ帯＋境界)の実測。親に接続していない probe で測るのは、Dock 済みの実物へ
    /// Size を代入してもレイアウトが即座に上書きしてしまい測れないため(実測)。
    /// 枠はフォントだけで決まり、ページ枚数にもキャプション文字列にも依存しない
    /// (実測: 1 枚と 5 枚・文字列違いで同値)。ただし 0 枚だとヘッダ帯が現れず測れないので 1 枚載せる。
    /// Multiline=false(既定)なのでヘッダは常に 1 段であり、幅による段数変動は起きない。
    /// </summary>
    private static Size MeasureTabFrame(Font font)
    {
        const int Probe = 1000; // 枠より十分大きければ測定値は変わらない
        using var probe = new TabControl { Font = font, Size = new Size(Probe, Probe) };
        using var page = new TabPage("A"); // probe.Dispose でも解放されるが二重解放は安全
        probe.TabPages.Add(page);
        var display = probe.DisplayRectangle;
        return new Size(Probe - display.Width, Probe - display.Height);
    }
```

**Step 2: `SettingsDialog` ctor から AutoSize を外す**

```csharp
        ShowInTaskbar = false;
        AutoSize = true;                              // ← この 2 行を削除する
        AutoSizeMode = AutoSizeMode.GrowAndShrink;    // ←
```

**Step 3: `BuildLayout` の Dock 追加順を是正し、ClientSize を代入する**

末尾の 4 行を差し替える。

```csharp
        // Dock は「子インデックスが大きい方」から確定する。したがって Dock=Fill を先に Add し、
        // Dock=Bottom を後に Add する(逆順にすると Fill の TabControl がクライアント全面を
        // 取ってボタン列を覆う)。DocumentInfoDialog も同じ順序。
        Controls.Add(_tabControl);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        // Controls.Add の後に測る(親に接続して初めて Form の Font を継承するため。
        // 高 DPI では Form.Font が拡大されており、接続前に測ると小さすぎる値になる)。
        ClientSize = SettingsTabLayoutHelper.ComputeDialogClientSize(_tabControl, buttons);
```

**Step 4: ビルドしてテストを走らせる**

```
dotnet build kxEdit.sln -c Debug -warnaserror
dotnet test tests/kxEdit.App.Tests -c Debug --no-build --filter "FullyQualifiedName~SettingsDialogLayoutTests"
```

期待: **2 本とも PASS**、`$LASTEXITCODE` = 0、`合格: 2` を目視。

アナライザで弾かれたら(CLAUDE.md 環境ノート・過去に頻発):
- `CA2000`(スコープ喪失前に Dispose)→ Step 1 のとおり `using var page` を明示済み。なお出るなら
  `probe.TabPages.Add(new TabPage("A"))` へ戻し、抑止ではなく構造で解くこと。
- `CA1062` / null チェック → `ArgumentNullException.ThrowIfNull` を既に置いている。
- **抑止コメントで黙らせない**(docs/lint-format-setup.md の抑止規約)。

**Step 5: App.Tests 全体を走らせて巻き添え退行がないことを確認する**

```
dotnet test tests/kxEdit.App.Tests -c Debug --no-build
```

期待: 全件 PASS。特に `MainFormSmokeTests` の設定ダイアログ経路と `SettingsStartupTests` を見る。

**Step 6: 整形してコミット**

```
dotnet csharpier format .
git add src/kxEdit.App/Settings/SettingsTabLayoutHelper.cs src/kxEdit.App/Settings/SettingsDialog.cs tests/kxEdit.App.Tests/SettingsDialogLayoutTests.cs
git commit
```

メッセージ:

```
fix(app): 設定ダイアログが 136x89 に潰れる不具合を修正 (#68)

TabControl はページの中身から推奨サイズを算出せず常に既定 200x100 を返すため、
Form の AutoSize では潰れていた。全ページ本体の希望サイズの最大 + タブ枠 + ボタン列
から ClientSize を算出して与える。枠も即値ではなく実測するのでフォント・DPI に追従する。

Issue 記載の修正案(TabControl.MinimumSize を与える)は実測で否定した。Form の AutoSize は
Dock=Fill の子の希望サイズを見ないため、それでは 136x89 のまま変わらない。

併せて Dock の追加順を是正した。子インデックスが大きい方から確定するため、Bottom の
ボタン列を先に Add していた従来の順序では、正しい寸法を与えた瞬間に Fill の TabControl が
ボタン列を覆う(潰れていた間は表面化していなかった)。
```

---

## Task 3: フォント追従の網を足す

即値でハードコードした実装が通ってしまわないようにする。`SettingsDialog.ClientSize` は ctor で
一度確定するため、ダイアログ経由ではフォント追従を検証できない。ヘルパ単体で見る。

**Files:**
- Modify: `tests/kxEdit.App.Tests/SettingsDialogLayoutTests.cs`

**Step 1: テストを足す**

```csharp
    /// <summary>
    /// 寸法がフォントに追従することを固定する。ダイアログの ClientSize は ctor で確定するため、
    /// フォントを差し替えて測り直す網はヘルパ経由でしか張れない。
    /// 「両方の倍率で内容を包含する」だけでは即値実装(十分大きい定数)が生き残るので、
    /// 「倍率を上げたら寸法も増える」ことまで見る。
    /// </summary>
    [Fact]
    public void Client_size_follows_the_font_rather_than_hard_coded_pixels() =>
        Sta.Run(() =>
        {
            var normal = MeasureWithFontScale(1.0f);
            var large = MeasureWithFontScale(1.5f);

            Assert.True(large.Width > normal.Width, $"フォントを 1.5 倍にしても幅が増えない ({normal.Width} → {large.Width})");
            Assert.True(large.Height > normal.Height, $"フォントを 1.5 倍にしても高さが増えない ({normal.Height} → {large.Height})");
        });

    /// <summary>指定倍率のフォントでヘルパを呼び、戻り値が内容を包含することを確かめて返す。</summary>
    private static Size MeasureWithFontScale(float scale)
    {
        using var font = new Font(Control.DefaultFont.FontFamily, Control.DefaultFont.Size * scale);
        using var tabs = new TabControl { Font = font };
        using var buttons = new FlowLayoutPanel { AutoSize = true, Font = font };

        var page = new TabPage("ページ");
        var body = new Label { AutoSize = true, Text = "設定項目のラベル", Font = font };
        page.Controls.Add(body);
        tabs.TabPages.Add(page);
        buttons.Controls.Add(new Button { Text = "OK", AutoSize = true, Font = font });

        var size = SettingsTabLayoutHelper.ComputeDialogClientSize(tabs, buttons);
        var wantBody = body.GetPreferredSize(Size.Empty);
        var wantButtons = buttons.GetPreferredSize(Size.Empty);

        Assert.True(size.Width >= wantBody.Width, $"幅 {size.Width} が本体の希望幅 {wantBody.Width} に足りない");
        Assert.True(
            size.Height >= wantBody.Height + wantButtons.Height,
            $"高さ {size.Height} が本体 {wantBody.Height} + ボタン列 {wantButtons.Height} に足りない"
        );
        return size;
    }
```

> `SettingsTabLayoutHelper` は `internal`。`kxEdit.App.csproj` に `InternalsVisibleTo` が既にあるので参照できる。
> 名前空間 `kxEdit.App.Settings` の using が要る。

**Step 2: 走らせて緑を確認**

```
dotnet build kxEdit.sln -c Debug -warnaserror
dotnet test tests/kxEdit.App.Tests -c Debug --no-build --filter "FullyQualifiedName~SettingsDialogLayoutTests"
```

期待: **3 本 PASS**、`$LASTEXITCODE` = 0。

**Step 3: 整形してコミット**

```
dotnet csharpier format .
git add tests/kxEdit.App.Tests/SettingsDialogLayoutTests.cs
git commit -m "test(app): 設定ダイアログの寸法がフォントに追従することを固定 (#68)"
```

> ミューテーション検証は行わない。CLAUDE.md §4-A で GUI レイアウトは禁止領域。
> テストの有効性は「Task 1 で現状の src に対して実際に赤になった」ことで担保する。

---

## Task 4: L5 チェックリストを書く

Dock の追加順を変えると `Controls` の z-order が変わる。本ダイアログは `TabIndex` を明示していないため、
**Tab キーの巡回順と UIA ツリーの子順序が変わりうる**(従来「ボタン列 → TabControl」→ 変更後
「TabControl → ボタン列」)。CLAUDE.md §5「判定に迷ったら必要に倒す」に従い L5 を必須とする。

**Files:**
- Create: `docs/plans/2026-09-04-settings-dialog-size-l5-checklist.md`

**Step 1: チェックリストを書く**

既存の L5 チェックリスト(`docs/plans/2026-09-04-home-key-behavior-l5-checklist.md` 等)の体裁に合わせる。
最低限、次の項目を含める(各項目に 手順 / 期待 / 結果欄 を置く)。

1. `オプション → 設定` を開くと、**ダイアログ全体が見え、OK / キャンセルがマウスで押せる**(本 Issue の直接の症状)。
2. 開いた直後のフォーカスが先頭タブ「基本」にあり、そう読まれる。
3. Tab キーで タブ → ページ内コントロール → OK → キャンセル の順に回る(Dock 順変更の影響)。
4. Shift+Tab で逆順に回る。
5. 左右矢印でタブを切り替えるとカテゴリ名が読まれ、切替後のページ内容が読まれる。
6. Enter が OK、Esc がキャンセルとして働く。
7. 5 タブすべてで、項目が見切れず全部表示される(特に「表示」タブ=最も幅が要る)。
8. Windows の表示スケール 150% で 1・7 を再確認する(高 DPI 追従)。

**Step 2: コミット**

```
git add docs/plans/2026-09-04-settings-dialog-size-l5-checklist.md
git commit -m "docs(plans): 設定ダイアログ寸法修正の L5 チェックリスト (#68)"
```

---

## Task 5: 最終レビュー(別エージェント・2 パス統合 1 回)

**Step 1: レビューを依頼する**

CLAUDE.md §3 の「簡略化の基準」に該当する規模なので、コード品質パスと脆弱性パスを 1 回に統合してよい。
ただし**別エージェントによるレビューは省略しない**(§4)。

レビュー対象: `main...feature/settings-dialog-size` の差分全体。特に見てほしい点を明示する:

- `ComputeDialogClientSize` が全ページを走査できているか(`page.Controls` が複数/0 のときの振る舞い)。
- `MeasureTabFrame` の probe が実物と食い違う条件(`Multiline=true` にされたら / `Alignment` が
  `Left`/`Right` にされたら / `Appearance` が変わったら)。**現在の実装では起きないが、将来の変更で
  静かに壊れないか**を見てもらう。
- Dock 追加順の変更が、`ActiveControl = _tabControl` の初期フォーカスと Tab 順に与える影響。
- テストが「現状の src で赤になる」ことを実際に確認済みか(Task 1 Step 2 の記録)。

**Step 2: 指摘を 3 択で処理する**

① fixup commit で修正 / ② PR description に記載して受容 / ③ 理由付き却下。
**元 commit は書き換えず fixup commit で積む**(§4)。指摘は鵜呑みにせず技術的に検証する。

---

## Task 6: 品質ゲートと PR

**Step 1: 品質ゲート**

```
powershell -File tools\pre-merge-check.ps1
```

期待: **EXIT 0**。途中で落ちたら、落ちたステップ名と失敗テスト名まで確認してから直す
(ビルドが割れても exit 1 になるため、テストが 1 本も走っていない可能性を必ず疑う)。

**Step 2: push して PR を作る**

```
git push -u origin feature/settings-dialog-size
gh pr create --base main --title "fix(app): 設定ダイアログが 136x89 に潰れる不具合を修正 (#68)"
```

PR description(日本語)に必ず書くこと:

- 目的: Issue #68 の解消。マウスで操作できない設定ダイアログを直す。
- **Issue 記載の修正案を実測で否定したこと**と、その根拠(Form の AutoSize は Dock=Fill の子の
  希望サイズを見ない)。
- 同居していた 2 つ目の欠陥(Dock 追加順)を同ブランチで是正したこと。
- 実測表(フォント 1.0 / 1.25 / 1.5 / 2.0 倍で全ページ収まり・余白 0)。
- レビュー経緯と指摘の処理(3 択のどれにしたか)。
- **申し送り: L5 未実施**(Task 4 のチェックリスト・ユーザーに実機検証を依頼)。
- **申し送り: マージ後に PR #70(Home キーの動作切替)のマウス目視確認が可能になる**。同 PR は
  本不具合のため機械検証で代替していたので回収すること。
- `Closes #68`

**Step 3: マージはユーザーが行う**

こちらではマージしない。

---

## 対象外

- **Issue #69**(`tools/check-no-local-paths.ps1` が 1 行ファイルを走査できない)は別件。含めない。
- 画面に収まらない場合のクランプ / `AutoScroll` は入れない(フォント 2.0 倍相当でも 700×456 で
  実害が観測されないため・YAGNI)。
