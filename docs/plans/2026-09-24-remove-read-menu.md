# 「読み上げ」メニュー廃止 Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** メニューバーの「読み上げ(&R)」を廃止し、「行へ移動」を「検索」メニュー末尾へ移し、「現在位置」(Ctrl+Alt+P)を機能ごと削除する。

**Architecture:** 設計書 `docs/plans/2026-09-24-remove-read-menu-design.md` に従う。
`MainForm.BuildMenu` の構成変更と `ProcessCmdKey` / `AnnouncePosition` の削除が本体。
利用箇所がなくなる `kxEdit.Core.Reading.PositionFormatter` とそのテストも削除する。

**Tech Stack:** .NET 9 WinForms / xUnit

**規模:** §3 簡略化基準により 1 タスク・単一 commit。

---

### Task 1: 読み上げメニュー廃止・行へ移動の移設・現在位置の削除

**Files:**
- Modify: `src/kxEdit.App/MainForm.cs`(`ProcessCmdKey`・`BuildMenu`・`AnnouncePosition` 削除・コメント 2 箇所)
- Modify: `src/kxEdit.Core/Documents/CharacterCounter.cs:17-19`(Ctrl+Alt+P への言及)
- Delete: `src/kxEdit.Core/Reading/PositionFormatter.cs`
- Delete: `tests/kxEdit.Core.Tests/Reading/PositionFormatterTests.cs`
- Modify: `tests/kxEdit.App.Tests/MainFormSmokeTests.cs`(`AnnouncePosition_ReadsLineTotalAndColumnOnly` を削除し、検索メニューのテストを追加)
- Modify: `tests/kxEdit.App.Tests/MainFormModeMenuTests.cs`(`OwnedByProcessCmdKey` から Ctrl+Alt+P を外し、未処理になったことのテストを追加)

**Step 1: 失敗するテストを書く**

`tests/kxEdit.App.Tests/MainFormSmokeTests.cs` — `AnnouncePosition_ReadsLineTotalAndColumnOnly` とその見出しコメント
(`// ===== AnnouncePosition: ...` から テスト末尾まで)を削除し、`File_menu_accelerators_are_unique` の直後に追加:

```csharp
    // ===== [検索] > 行へ移動(2026-09-24 「読み上げ」メニュー廃止) =====

    private static ToolStripMenuItem SearchMenuOf(MainForm form) =>
        form.MainMenuStrip!.Items.OfType<ToolStripMenuItem>()
            .Single(mi => mi.Text == "検索(&S)");

    [Fact]
    public void Read_menu_is_removed_from_menu_bar() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);

            var tops = form
                .MainMenuStrip!.Items.OfType<ToolStripMenuItem>()
                .Select(mi => mi.Text!)
                .ToList();

            Assert.Contains("検索(&S)", tops); // 陽性対照: 走査が空だと空虚に緑になる
            Assert.DoesNotContain(tops, t => t.StartsWith("読み上げ", StringComparison.Ordinal));
        });

    // 行へ移動は [検索] の最後・直前は区切り線(設計 §1)。Ctrl+G は ProcessCmdKey 所有なので
    // ShortcutKeys は None のまま表示だけを持つ(ShortcutKeys にすると二重発火/衝突する)。
    [Fact]
    public void Go_to_line_is_last_item_of_search_menu_after_separator() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);

            var items = SearchMenuOf(form).DropDownItems;
            var last = Assert.IsType<ToolStripMenuItem>(items[items.Count - 1]);

            Assert.Equal("行へ移動(&J)...", last.Text);
            Assert.IsType<ToolStripSeparator>(items[items.Count - 2]);
            Assert.Equal("Ctrl+G", last.ShortcutKeyDisplayString);
            Assert.Equal(Keys.None, last.ShortcutKeys);
        });

    // [検索] 内でアクセラレータが衝突しないこと。grep が &G を使っているため、
    // 行へ移動を &G のまま移すと Alt→S→G が巡回になる(設計 §1)。
    [Fact]
    public void Search_menu_accelerators_are_unique() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);

            var keys = SearchMenuOf(form)
                .DropDownItems.OfType<ToolStripMenuItem>()
                .Select(mi => AccelOf(mi.Text!))
                .Where(c => c is not null)
                .Select(c => c!.Value)
                .ToList();

            Assert.Contains('J', keys); // 行へ移動(&J) が居る
            Assert.Contains('G', keys); // フォルダ検索(grep)(&G) が居る
            Assert.Equal(keys.Count, keys.Distinct().Count()); // 重複なし
        });
```

`tests/kxEdit.App.Tests/MainFormModeMenuTests.cs` — `OwnedByProcessCmdKey` から
`Keys.Control | Keys.Alt | Keys.P,` の行を削除し、末尾(`Ctrl_shift_i_...` の後)に追加:

```csharp
    // 2026-09-24 「読み上げ」メニュー廃止: 現在位置(Ctrl+Alt+P)は機能ごと削除した。
    // ProcessCmdKey が食わない(false)= 位置読み上げの配線が残っていないこと。
    [Fact]
    public void Ctrl_alt_p_is_no_longer_handled() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(tmp);

            var m = typeof(MainForm).GetMethod("ProcessCmdKey", Priv);
            Assert.NotNull(m);
            object?[] args = { default(Message), Keys.Control | Keys.Alt | Keys.P };

            Assert.False((bool)m!.Invoke(form, args)!);
        });
```

**Step 2: テストが失敗することを確認**

Run: `dotnet build tests/kxEdit.App.Tests -c Release; dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~Read_menu_is_removed|FullyQualifiedName~Go_to_line_is_last|FullyQualifiedName~Search_menu_accelerators|FullyQualifiedName~Ctrl_alt_p_is_no_longer"`
Expected: 4 件 FAIL(読み上げが存在 / 行へ移動が検索に無い(Search は grep が末尾)/ J が無い / Ctrl+Alt+P が true)

**Step 3: 実装**

`src/kxEdit.App/MainForm.cs`:

1. `ProcessCmdKey` の次の 3 行を削除:
   ```csharp
               case Keys.Control | Keys.Alt | Keys.P:
                   AnnouncePosition();
                   return true;
   ```
2. `BuildMenu` の grep 追加の直後(読み上げブロックの代わり)に:
   ```csharp
           // 行へ移動(2026-09-24: 廃止した「読み上げ」メニューから移設。grep が &G を使うため &J)。
           // キーは ProcessCmdKey で処理し、ここは表示のみ（二重発火回避・F3 と同方式）。
           search.DropDownItems.Add(new ToolStripSeparator());
           search.DropDownItems.Add(
               new ToolStripMenuItem("行へ移動(&J)...", null, (_, _) => GoToLine())
               {
                   ShortcutKeyDisplayString = "Ctrl+G",
               }
           );
   ```
   「読み上げ（SR 照会）」ブロック(`var read = ...` から 2 つの `read.DropDownItems.Add(...)` まで)を削除し、
   `menu.Items.AddRange(file, edit, search, read, mode, options, help);` から `read` を外す。
3. モードメニューのコメント 2 箇所の `F3 / Shift+F3 / Ctrl+G / Ctrl+Alt+P と同じ` →
   `F3 / Shift+F3 / Ctrl+G と同じ`、`F3 / Ctrl+G / Ctrl+Alt+P と同方式` → `F3 / Ctrl+G と同方式`。
4. `// ==================== 読み上げ照会（SR 利便・M6） ====================` 見出しと
   `AnnouncePosition()`(doc コメント含む)を削除。`GoToLine` / `ToggleOvertype` は残すので、
   見出しは `// ==================== 行へ移動 / 挿入・上書き ====================` に改める。
5. `using kxEdit.Core.Reading;` があれば削除(未使用 using は -warnaserror で落ちる可能性)。

`src/kxEdit.Core/Documents/CharacterCounter.cs` 17〜19 行目を差し替え:

```csharp
/// 設計判断: 「人間に自然な文字数」= CRLF は空白として除外・サロゲート=1(Rune)を採る
/// (設計 2026-07-25 §4 参照。当時比較対象だった位置照会(Ctrl+Alt+P)の CRLF=1 論理文字基準は、
/// 位置照会ごと 2026-09-24 に廃止した)。
```

削除: `src/kxEdit.Core/Reading/PositionFormatter.cs`、`tests/kxEdit.Core.Tests/Reading/PositionFormatterTests.cs`
(空になる `Reading` ディレクトリも消す)。

**Step 4: テストが通ることを確認**

Run: `dotnet build kxEdit.sln -c Release; dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~MainFormSmokeTests|FullyQualifiedName~MainFormModeMenuTests"`
Expected: PASS(全件)・0 warning

**Step 5: Commit**

```bash
git add -A src tests
git commit -m "feat(menu): 「読み上げ」メニューを廃止し行へ移動を検索メニューへ移す"
```

---

## 検証(タスク後)

1. 別エージェントによる最終レビュー(コード品質+脆弱性の 1 回統合。§3 簡略化基準)。指摘は fixup commit。
2. `pwsh tools/pre-merge-check.ps1` → EXIT 0。
3. `tools/sr-regression.ps1` を手動実行(a11y 関連変更のマージ前)。
4. L5(実機 NVDA): 設計書「L5 要否」の 4 項目。
