# モードメニュー項目へのショートカットキー追加 実装プラン

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** モードメニューの 2 項目に `Ctrl+Shift+M`（マークダウンプレビュー）/ `Ctrl+Shift+K`（CSVモード）を割り当て、通常編集中はモードへ入り、既に CSVモード中なら「現在CSVモードです」と発声するだけでトグルしないようにする。

**Architecture:** `ToolStripMenuItem.ShortcutKeys` に登録してキーとメニュークリックを同一ハンドラに寄せ、判定はハンドラ側（`CsvController.EnterMode()` と `MainForm.ShowMarkdownPreview()` の早期 return）に置く。発声文言は既存の `CsvAnnounceFormatter`（`kxEdit.Core`）へ定数で追加する。新規アルゴリズムは無く、進入処理は既存の `TryEnterMode(Document)` をそのまま使う。

**Tech Stack:** .NET 9 / WinForms / xUnit（STA ヘルパー `Sta.Run`）/ CSharpier（pre-commit で自動整形）

**設計書:** [2026-09-23-mode-shortcut-keys-design.md](./2026-09-23-mode-shortcut-keys-design.md) — 決定事項・挙動変更 2 件・却下した案の理由はすべてそちら。本書は手順のみ。

---

## 前提知識（この変更を触る人が知らないと詰まる点）

1. **同じ `ShortcutKeys` を 2 つの `ToolStripMenuItem` に登録しても WinForms は例外を出さない。**
   `ToolStripManager.ProcessShortcut` が先に見つけた 1 つだけを発火させ、もう片方は黙って死ぬ。
   だから「キー重複」は静的初期化でも実行時例外でも捕まらず、**テストで走査するしかない**。
   実際に当初案の `Ctrl+Shift+J` は既存の「折り返し整形（禁則処理）」と衝突していた。
2. **無効（`Enabled == false`）な `ToolStripMenuItem` は `ShortcutKeys` が発火しない。**
   `mdPreview.Enabled` は `mode.DropDownOpening` でしか更新されないので、状態に応じて落とすと
   「モード中にメニューを開く → `Esc` で抜ける → キーが死ぬ」が起きる。
   **`Enabled` を CSVモードで落としてはいけない**（設計書に理由あり）。
3. **`MainForm.ShowMarkdownPreview` は挙動テストから通せない。** `MarkdownPreviewForm.ShowDialog`
   （WebView2 実体）と `MessageBox.Show` を含むため、ガードが消えた退行ではテストが
   **落ちるのではなく固まる**。既存の `MainFormPreviewStructureTests` と同じく IL 走査
   （`IlCallees.Of`）で構造を固定する。
4. **文言のリテラル手書きは禁止**（`tests/README.md` 6 項）。assert は
   `CsvAnnounceFormatter.<定数>` 参照で書く。
5. **`--no-build` の罠**（セッションメモリー）: ビルドが失敗したまま `dotnet test --no-build`
   を打つと古い DLL が走って**偽の緑**になる。必ず build の exit code を確認してから test する。

---

## Task 1: 実装（TDD・単一 commit）

設計書の「CLAUDE.md §3 の簡略化基準に乗せ、実装 1 タスク・単一 commit」に従い、4 ファイルの
変更とテストを 1 タスクにまとめる。

**Files:**
- Modify: `src/kxEdit.Core/Csv/CsvAnnounceFormatter.cs`（`ModeOff` の直後・現在 60 行目付近）
- Modify: `src/kxEdit.App/CsvController.cs`（`ToggleMode()` の直後・現在 55 行目付近）
- Modify: `src/kxEdit.App/MainForm.cs`（モードメニュー構築 1187-1205 行付近 / `ShowMarkdownPreview` 1770-1783 行付近）
- Modify: `tests/kxEdit.App.Tests/CsvControllerTests.cs`（`ExitMode` セクションの直後・現在 293 行目付近）
- Modify: `tests/kxEdit.App.Tests/MainFormPreviewStructureTests.cs`（末尾にテスト 1 本追加）
- Create: `tests/kxEdit.App.Tests/MainFormModeMenuTests.cs`

---

### Step 1: 失敗するテストを書く（1/3）— `CsvController.EnterMode()`

`tests/kxEdit.App.Tests/CsvControllerTests.cs` の `// ===== ToggleMode(進入方向) =====`
（現在 295 行目）の**直前**に以下を挿入する。

```csharp
    // ===== EnterMode(Ctrl+Shift+K / モードメニュー用の進入専用 API・トグルしない) =====

    [Fact]
    public void EnterMode_FromNormal_EntersMode_AnnouncesModeOnWithCell() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);

            host.Csv.EnterMode();

            Assert.True(doc.State.CsvMode);
            Assert.True(doc.Editor.ReadOnly);
            Assert.False(doc.Editor.RaiseUiaSelectionEvents); // モード遷移中の生読み抑止
            Assert.Equal(
                CsvAnnounceFormatter.ModeOn + " " + CsvAnnounceFormatter.Cell("a1", 1, 1),
                host.Announcer.Said[^1]
            );
        });

    // 既にモード中なら「現在CSVモードです」だけを発声し、モードは落とさない(=トグルしない)。
    // CLAUDE.md §4-B: no-change のテストは既定値と区別するため非既定位置から始める
    // => EnterAt22 でセル (2,2)・モード ON・ReadOnly ON の非既定状態を作ってから検証する。
    [Fact]
    public void EnterMode_WhenAlreadyInMode_AnnouncesModeAlreadyOn_KeepsMode() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt22(host); // モード ON・(2,2)・通知履歴クリア済み
            Assert.True(doc.State.CsvMode); // 前提(guard の発火条件)を固定

            host.Csv.EnterMode();

            Assert.True(doc.State.CsvMode); // モードが落ちていない
            Assert.True(doc.Editor.ReadOnly); // 通常編集へ戻っていない
            Assert.False(doc.Editor.RaiseUiaSelectionEvents);
            Assert.Equal(2, doc.State.CsvRow); // セル位置も動かない
            Assert.Equal(2, doc.State.CsvCol);
            Assert.Single(host.Announcer.Said); // ModeOff / Cell 等を余計に言わない
            Assert.Equal(CsvAnnounceFormatter.ModeAlreadyOn, host.Announcer.Said[^1]);
        });

    [Fact]
    public void EnterMode_NoActiveDoc_IsNoOp() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // Docs.CreateNew を呼ばない(Active=null)
            host.Csv.EnterMode();

            Assert.Empty(host.Announcer.Said); // 通知も発火しない
        });

    // F2 編集中は進入も発声もしない(ToggleMode / ExitMode の既存ガードと揃える)。
    [Fact]
    public void EnterMode_WhileEditing_IsNoOp() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Csv.BeginEdit();
            Assert.True(host.Csv.IsEditing); // 前提(guard の発火条件)を固定
            host.Announcer.Said.Clear();

            host.Csv.EnterMode();

            Assert.True(doc.State.CsvMode);
            Assert.True(host.Csv.IsEditing); // 編集も巻き込んで落としていない
            Assert.Empty(host.Announcer.Said); // ModeAlreadyOn も言わない
        });

    // 解析不能な本文では進入せず ParseError のみ(TryEnterMode の既存挙動をそのまま通す)。
    [Fact]
    public void EnterMode_UnparseableCsv_AnnouncesParseError_DoesNotEnter() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc("a1,\"b1\na2,b2"); // 引用符未終端 => Ok=false

            host.Csv.EnterMode();

            Assert.False(doc.State.CsvMode);
            Assert.Equal(CsvAnnounceFormatter.ParseError, host.Announcer.Said[^1]);
        });
```

---

### Step 2: 失敗するテストを書く（2/3）— モードメニューの配線

`tests/kxEdit.App.Tests/MainFormModeMenuTests.cs` を**新規作成**する。

```csharp
using kxEdit.Core.Csv;
using kxEdit.Core.Settings;

namespace kxEdit.App.Tests;

/// <summary>
/// モードメニュー(モード(&amp;M))のショートカットキー配線(2026-09-23 設計書)。
/// マークダウンプレビュー = Ctrl+Shift+M / CSVモード = Ctrl+Shift+K。
/// <para>
/// <b>なぜメニュー全体の重複走査が要るか</b>: 当初案の Ctrl+Shift+J は既存の
/// 「折り返し整形(禁則処理)」が使用中だった。<b>同一 ShortcutKeys を 2 項目に登録しても
/// WinForms は例外を出さず、どちらか一方だけが発火して片方が黙って死ぬ</b>ため、
/// 重複は静的初期化でも実行時例外でも捕まらない。<c>CsvCommands.ByKey</c> が Add 形式の
/// 初期化子でキー重複を検出しているのと同じ網を、メニュー側にも張る。
/// </para>
/// </summary>
public class MainFormModeMenuTests
{
    /// <summary>MainForm を可視状態まで作る(TempDir 隔離は MainFormSmokeTests と同方式)。</summary>
    private static MainForm ShowMainForm(TempDir tmp)
    {
        var form = new MainForm(
            new AppSettings { BackupEnabled = false, CsvAutoModeOnOpen = false },
            System.IO.Path.Combine(tmp.Root, "settings.json"),
            backupDirectory: System.IO.Path.Combine(tmp.Root, "backups"),
            sessionLayoutPath: System.IO.Path.Combine(tmp.Root, "session-state.json")
        );
        form.SetLastSessionBuffersPathForTest(
            System.IO.Path.Combine(tmp.Root, "last-session-buffers.json")
        );
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Show();
        return form;
    }

    /// <summary>メニュー全体(入れ子を含む)の ToolStripMenuItem を列挙する。</summary>
    private static List<ToolStripMenuItem> AllMenuItems(MainForm form)
    {
        var acc = new List<ToolStripMenuItem>();
        void Walk(ToolStripItemCollection items)
        {
            foreach (var mi in items.OfType<ToolStripMenuItem>())
            {
                acc.Add(mi);
                Walk(mi.DropDownItems);
            }
        }
        Walk(form.MainMenuStrip!.Items);
        return acc;
    }

    private static ToolStripMenuItem ItemOf(MainForm form, string text) =>
        AllMenuItems(form).Single(mi => mi.Text == text);

    [Fact]
    public void Mode_menu_items_have_expected_shortcuts() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(tmp);

            Assert.Equal(
                Keys.Control | Keys.Shift | Keys.M,
                ItemOf(form, "マークダウンプレビュー(&P)").ShortcutKeys
            );
            Assert.Equal(
                Keys.Control | Keys.Shift | Keys.K,
                ItemOf(form, "CSVモード(&C)").ShortcutKeys
            );
        });

    // 陽性対照: 既存の Ctrl+Shift+J(折り返し整形)を奪っていないこと。
    // 当初案どおり J を割り当てると、この 2 本のどちらかが必ず赤くなる。
    [Fact]
    public void Kinsoku_format_keeps_ctrl_shift_j() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(tmp);

            Assert.Equal(
                Keys.Control | Keys.Shift | Keys.J,
                ItemOf(form, "折り返し整形（禁則処理）(&K)").ShortcutKeys
            );
        });

    [Fact]
    public void Menu_shortcut_keys_are_unique_across_whole_menu() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(tmp);

            var shortcuts = AllMenuItems(form)
                .Where(mi => mi.ShortcutKeys != Keys.None)
                .Select(mi => mi.ShortcutKeys)
                .ToList();

            // 陽性対照: 走査が空だと「重複なし」が空虚に緑になる。
            Assert.Contains(Keys.Control | Keys.Shift | Keys.M, shortcuts);
            Assert.Contains(Keys.Control | Keys.Shift | Keys.K, shortcuts);
            Assert.Equal(shortcuts.Count, shortcuts.Distinct().Count());
        });

    // 挙動変更(設計書): モードメニューの「CSVモード」再選択では OFF にならない。
    // 起動時の無題タブは本文が空 = CsvParser.Parse("") は Ok かつ Rows.Count==0 なので
    // TryEnterMode の「データ無し」分岐を通り ModeOn だけを発声する(前提として assert する)。
    [Fact]
    public void Csv_mode_menu_item_enters_but_does_not_toggle_off() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(tmp);
            var csvItem = ItemOf(form, "CSVモード(&C)");

            csvItem.PerformClick(); // 1 回目: 進入
            Assert.Equal(CsvAnnounceFormatter.ModeOn, form.LastAnnouncementForTest);

            csvItem.PerformClick(); // 2 回目: トグルしない

            // ToggleMode 配線への退行なら ModeOff になる。
            Assert.Equal(CsvAnnounceFormatter.ModeAlreadyOn, form.LastAnnouncementForTest);
        });
}
```

---

### Step 3: 失敗するテストを書く（3/3）— プレビューの CSVモードガード

`tests/kxEdit.App.Tests/MainFormPreviewStructureTests.cs` の末尾（クラスの閉じ括弧の直前）に
以下を追加する。

```csharp
    /// <summary>
    /// 2026-09-23 設計書: CSVモード中はプレビューを開かない(キー・メニューとも)。
    /// <para>
    /// <b>なぜ挙動テストで代替できないか</b>: ガードが効いていれば即座に return するが、
    /// <b>ガードが消えた退行では <c>ShowDialog</c>(WebView2 実体)やその初期化失敗の
    /// <c>MessageBox</c> に入り、テストは「落ちる」のではなく「固まる」</b>。
    /// CI を無限に止める網は網にならないので、構造(IL)で固定する。
    /// </para>
    /// <para>
    /// 位置も同時に固定する: CSVモード判定は <c>ExceedsMaxChars</c> / <c>SnapshotText</c> より
    /// <b>前</b>。後ろへ移ると 4M 文字超の CSV で「大きすぎます」ダイアログが先に出てしまい、
    /// 「CSVモード中は無反応」という要件が崩れる。
    /// </para>
    /// </summary>
    [Fact]
    public void ShowMarkdownPreview_BailsOutInCsvMode_BeforeAnyWork()
    {
        var callees = IlCallees.Of(ShowMarkdownPreview());

        int csvMode = callees.FindIndex(m =>
            m.DeclaringType == typeof(DocumentState) && m.Name == "get_CsvMode"
        );
        int exceeds = callees.FindIndex(m =>
            m.DeclaringType == typeof(MarkdownRenderer)
            && m.Name == nameof(MarkdownRenderer.ExceedsMaxChars)
        );
        int snapshot = callees.FindIndex(m => m.Name == "get_SnapshotText");

        // 陽性対照: 3 つとも実在すること。FindIndex は見つからないと -1 を返すので、
        // 比較だけだと片方が消えた状態が「-1 < n」で空虚に緑になる。
        Assert.True(csvMode >= 0, "CSVモードの判定が見つからない");
        Assert.True(exceeds >= 0, "ExceedsMaxChars の呼出が見つからない");
        Assert.True(snapshot >= 0, "SnapshotText の取得が見つからない");

        Assert.True(csvMode < exceeds, "CSVモード判定は上限判定より前で行うこと");
        Assert.True(csvMode < snapshot, "CSVモード判定は SnapshotText より前で行うこと");
    }
```

---

### Step 4: テストが失敗することを確認する

```
dotnet build kxEdit.sln -c Release -warnaserror
```

Expected: **ビルドが失敗する**。`CsvAnnounceFormatter.ModeAlreadyOn` と
`CsvController.EnterMode()` が未定義（CS0117 / CS1061）。`MainFormModeMenuTests` の
`Mode_menu_items_have_expected_shortcuts` などはビルドは通る。

この段階では「コンパイルエラーで落ちる」が期待どおり。**`--no-build` で test を打たないこと**
（古い DLL が走って偽の緑になる）。

---

### Step 5: 実装（1/4）— 発声文言の定数

`src/kxEdit.Core/Csv/CsvAnnounceFormatter.cs` の `ModeOff` 定義（現在 59-60 行）の直後に挿入。

```csharp
    /// <summary>既に CSVモード中に CSVモードのショートカット（Ctrl+Shift+K）を押した/
    /// モードメニューを再選択したときの読み上げ。モードはトグルせず「今どのモードにいるか」
    /// だけを伝える（2026-09-23 設計書。終了は Esc に一本化）。</summary>
    public const string ModeAlreadyOn = "現在CSVモードです";
```

---

### Step 6: 実装（2/4）— `CsvController.EnterMode()`

`src/kxEdit.App/CsvController.cs` の `ToggleMode()` の閉じ括弧（現在 55 行）の直後に挿入。

```csharp
    /// <summary>
    /// CSVモードへ入る（Ctrl+Shift+K / モードメニュー）。既にモード中なら
    /// <see cref="CsvAnnounceFormatter.ModeAlreadyOn"/> を発声するだけで<b>トグルしない</b>
    /// （終了は Esc = <see cref="ExitMode()"/> に一本化。2026-09-23 設計書）。
    /// アクティブ文書なし・F2 編集中は何もしない（冪等・発声もしない）。
    /// <para>
    /// <c>ToggleMode()</c> を流用せず進入専用の公開 API を分けてあるのは、
    /// <see cref="ExitMode()"/> と同じ理由——「このキーは入る専用」という意図を
    /// コードに残し、呼出側の取り違えをコンパイル時に見えるようにするため。
    /// </para>
    /// </summary>
    public void EnterMode()
    {
        var doc = _docs.Active;
        if (doc is null || _editor.IsEditing)
            return;
        if (doc.State.CsvMode)
        {
            _announcer.Say(CsvAnnounceFormatter.ModeAlreadyOn);
            return;
        }
        TryEnterMode(doc); // 解析不可なら TryEnterMode が ParseError を通知して通常モードのまま
    }
```

---

### Step 7: 実装（3/4）— モードメニューの配線

`src/kxEdit.App/MainForm.cs` のモードメニュー構築（現在 1187-1205 行）を**丸ごと**以下に置き換える。

```csharp
        // モード（マークダウンプレビュー / CSVモード）。CSV 操作系はメニューに出さず
        // キー専用（CsvCommands・キー一覧は将来のヘルプに記載する）。
        // 2026-09-23 設計書: ショートカットは ShortcutKeys に登録する＝キーとメニュークリックが
        // 同一ハンドラになり、2 つの動線で挙動がズレない。Ctrl+Shift+J は既存の
        // 「折り返し整形（禁則処理）」が使用中のため、プレビューは Ctrl+Shift+M（Markdown の M）。
        var mode = new ToolStripMenuItem("モード(&M)");
        var mdPreview = new ToolStripMenuItem(
            "マークダウンプレビュー(&P)",
            null,
            (_, _) => ShowMarkdownPreview()
        )
        {
            ShortcutKeys = Keys.Control | Keys.Shift | Keys.M,
        };
        mode.DropDownItems.Add(mdPreview);
        mode.DropDownItems.Add(new ToolStripSeparator());
        // 進入専用（EnterMode）。再選択でモードを解除しない＝終了は Esc に一本化した
        // （2026-09-23 設計書の意図的な挙動変更 1 件目）。Checked は状態の提示として残す。
        var csvEnter = new ToolStripMenuItem("CSVモード(&C)", null, (_, _) => _csv.EnterMode())
        {
            ShortcutKeys = Keys.Control | Keys.Shift | Keys.K,
        };
        mode.DropDownItems.Add(csvEnter);
        // 開く度に活性状態を更新（プレビューはアクティブタブがあれば拡張子を問わず有効、
        // CSVモードは現在のモードを Checked で表示）。
        // CSVモード中にプレビュー項目を Enabled=false にはしない: 無効な ToolStripMenuItem は
        // ShortcutKeys が発火せず、Enabled の更新は DropDownOpening でしか走らないため
        // 「モード中にメニューを開く → Esc で抜ける → Ctrl+Shift+M が黙って死ぬ」が起きる。
        // CSVモード判定は ShowMarkdownPreview 側のガードで行う（2026-09-23 設計書）。
        mode.DropDownOpening += (_, _) =>
        {
            mdPreview.Enabled = _docs.Active is not null;
            csvEnter.Checked = _docs.Active?.State.CsvMode == true;
        };
```

---

### Step 8: 実装（4/4）— `ShowMarkdownPreview` の CSVモードガード

`src/kxEdit.App/MainForm.cs` の `ShowMarkdownPreview()` 冒頭（現在 1772-1774 行の
`var doc = ...; if (doc is null) return;`）の**直後**に挿入する。`ExceedsMaxChars` の
判定より**前**でなければならない（テストが IL 順で固定している）。

```csharp
        // 2026-09-23 設計書（意図的な挙動変更 2 件目）: CSVモード中はプレビューを開かない。
        // CSV として読んでいる本文をマークダウンとして描く意味が薄く、「モード中は今のモードに
        // 留まる」という規則に揃える。発声もしない（要件どおり無反応）。キー・メニューの
        // どちらもこのハンドラを通るので、判定はここ 1 箇所で足りる。
        if (doc.State.CsvMode)
            return;
```

---

### Step 9: ビルドとテストを通す

```
dotnet build kxEdit.sln -c Release -warnaserror
```
Expected: **exit code 0・警告 0**。ここが 0 でない限り次へ進まない
（`--no-build` の test は古い DLL を走らせて偽の緑を出す）。

```
dotnet test tests/kxEdit.Core.Tests   -c Release --no-build
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests    -c Release --no-build
```
Expected: 3 プロジェクトすべて **Failed: 0**。

新規分だけを先に回すなら:
```
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~MainFormModeMenuTests|FullyQualifiedName~EnterMode|FullyQualifiedName~BailsOutInCsvMode"
```

---

### Step 10: ガードが本当に効いていることを手で確かめる（変異の手動スポット確認）

ミューテーションテストは CLAUDE.md §4-A でこの領域（キーバインドのイベントマッピング）は
**禁止**なので、ツールは回さない。代わりに以下 2 点だけ手で入れ替えて**赤くなること**を確認し、
確認後は必ず元に戻す（セッションメモリー: 変異を当てたのに緑なら、まず古い DLL を疑うこと。
必ず `dotnet build` の exit code 0 を確認してから test する）。

1. `csvEnter` のハンドラを `_csv.EnterMode()` → `_csv.ToggleMode()` に戻す。
   → `Csv_mode_menu_item_enters_but_does_not_toggle_off` が赤（`ModeOff` になる）。
2. `mdPreview` の `ShortcutKeys` を `Keys.Control | Keys.Shift | Keys.J` にする。
   → `Mode_menu_items_have_expected_shortcuts` と
     `Menu_shortcut_keys_are_unique_across_whole_menu` が赤。

---

### Step 11: commit

```
git add src/kxEdit.Core/Csv/CsvAnnounceFormatter.cs src/kxEdit.App/CsvController.cs src/kxEdit.App/MainForm.cs tests/kxEdit.App.Tests/CsvControllerTests.cs tests/kxEdit.App.Tests/MainFormPreviewStructureTests.cs tests/kxEdit.App.Tests/MainFormModeMenuTests.cs
```

コミットメッセージは**日本語の本文を UTF-8 のファイルに書いて `-F` で渡す**
（ヒアドキュメントは文字化けする。セッションメモリー）。SSH 署名は Windows 版 `ssh-keygen` を指す:

```
git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -F <msgfile>
```

メッセージ案:

```
feat(mode): モードメニューにショートカットキーを追加する

マークダウンプレビュー = Ctrl+Shift+M / CSVモード = Ctrl+Shift+K。
既に CSVモード中なら「現在CSVモードです」と発声するだけでトグルしない
(終了は Esc に一本化)。当初案の Ctrl+Shift+J は既存の「折り返し整形
(禁則処理)」と衝突していたため M に変更した。

挙動変更 2 件(設計書に記載):
- モードメニュー「CSVモード」の再選択では OFF にならない
- CSVモード中はマークダウンプレビューを開かない

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

`--no-verify` は禁止（CLAUDE.md §6）。pre-commit の CSharpier が整形を入れたら、
整形後の内容で `git add` して commit し直す。

---

## Task 2: 最終ブランチレビュー（別エージェント・2 パス統合）

CLAUDE.md §3 の簡略化基準により**コード品質パスと脆弱性パスを 1 回に統合**してよい規模だが、
**別エージェントによるレビューは省略しない**（§4）。

**Steps:**

1. レビュー用エージェントを起動し、`main...feature/mode-shortcut-keys` の差分をレビューさせる。
   渡す前提情報:
   - 設計書 `docs/plans/2026-09-23-mode-shortcut-keys-design.md`
   - 「`Enabled` を状態で落とさない」判断の理由（無効項目は `ShortcutKeys` が発火しない）
   - 意図的な挙動変更 2 件
   - ミューテーション検証は §4-A の禁止領域なので指摘対象外
2. 特に見てほしい観点:
   - `Ctrl+Shift+M` / `Ctrl+Shift+K` が**他の経路**（`EditorControl` / `InputRouter` /
     `CsvCommands.ByKey` / WebView2 のアクセラレータ）と衝突していないか。
   - `EnterMode()` のガード順（`doc is null` → `IsEditing` → `CsvMode`）が
     `ToggleMode()` / `ExitMode()` と整合しているか。
   - `ShowMarkdownPreview` のガード位置が IL テストの意図と合っているか。
   - `Csv_mode_menu_item_enters_but_does_not_toggle_off` の前提
     （空の無題タブが CSV として `Ok`）が壊れやすくないか。
3. 指摘は CLAUDE.md §4 の 3 択で明示する:
   ① fixup commit で修正 / ② PR description に記載して受容 / ③ 理由付き却下。
   **元 commit は書き換えず、別 fixup commit で積む**。

---

## Task 3: 品質ゲート → PR

**Steps:**

1. 品質ゲートを回す（CLAUDE.md §6。本件はコード変更を含むので省略不可）:
   ```
   powershell -File tools\pre-merge-check.ps1
   ```
   Expected: **EXIT 0**。
2. push して PR を作る（`gh pr create`）。description は日本語で、
   目的・レビュー経緯・**挙動変更 2 件**・申し送りを書く。末尾に:
   ```
   🤖 Generated with [Claude Code](https://claude.com/claude-code)
   ```
3. **L5（実機 SR 検証）をユーザーへ依頼する。** 設計書「テスト戦略 > L5」の項目をそのまま
   チェックリストとして渡す。自動化しない（CLAUDE.md §5）。要点:
   - 通常編集中 `Ctrl+Shift+K` → CSVモードに入り読み上げ
   - CSVモード中 `Ctrl+Shift+K` → 「現在CSVモードです」・モード維持（続けてセル移動できる）
   - CSVモード中 `Ctrl+Shift+M` → 無反応・無発声（続けてセル移動できる）
   - 通常編集中 `Ctrl+Shift+M` → プレビューが開き `Esc` で戻る
   - `Ctrl+Shift+J`（折り返し整形）が従来どおり動く
   - F2 セル編集中に両キー → 編集維持・余計な発声なし
   - モードメニュー再選択で CSVモードが OFF にならない（挙動変更の確認）
4. 説明書の差分案を提示する（**書き換えない**。CLAUDE.md §8）。設計書「申し送り」の案を使う。
   未反映は本件 2 件＋PR #79 の `Esc`＋PR #78 の `Ctrl+矢印` で**計 4 件**ある旨も伝える。

---

## 完了条件

- [ ] `dotnet build kxEdit.sln -c Release -warnaserror` が exit 0・警告 0
- [ ] L1 / L2 / L3 すべて Failed: 0
- [ ] Step 10 の手動スポット確認 2 件で赤を観測し、元に戻した
- [ ] 別エージェントレビュー実施済み・指摘は 3 択で処理済み
- [ ] `tools/pre-merge-check.ps1` が EXIT 0
- [ ] PR 作成済み（挙動変更 2 件を description に明記）
- [ ] L5 チェックリストをユーザーへ提示済み
- [ ] 説明書の差分案を提示済み（リポジトリは書き換えていない）
