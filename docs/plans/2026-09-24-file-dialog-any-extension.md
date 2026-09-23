# ファイルダイアログの拡張子制限撤廃 Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 開くダイアログの拡張子絞り込みを撤廃し、保存の参照ダイアログの初期「ファイルの種類」を元ファイルの拡張子に合わせる。

**Architecture:** 設計書 `docs/plans/2026-09-24-file-dialog-any-extension-design.md` に従う。
判定ロジックを `SaveAsDialog.FilterIndexFor(string?)`(internal static・純粋関数)に切り出して L3 で検証し、
Form 側は `FilterIndex` に代入するだけにする。開くダイアログは Filter 文字列の差し替えのみ。

**Tech Stack:** .NET 9 WinForms / xUnit

**規模:** §3 簡略化基準により 1 タスク・単一 commit。

---

### Task 1: FilterIndexFor の追加と両ダイアログの Filter 変更

**Files:**
- Modify: `src/kxEdit.App/SaveAsDialog.cs`(クラス doc コメント・`OnBrowseClicked`・新規 `FilterIndexFor`)
- Modify: `src/kxEdit.App/WinFormsFileDialogService.cs`(`PickOpenPath` の Filter・クラス doc コメント)
- Create: `tests/kxEdit.App.Tests/SaveAsDialogTests.cs`

**Step 1: 失敗するテストを書く**

`tests/kxEdit.App.Tests/SaveAsDialogTests.cs`:

```csharp
namespace kxEdit.App.Tests;

/// <summary>
/// <see cref="SaveAsDialog.FilterIndexFor(string?)"/> が参照ボタンの SaveFileDialog の
/// 初期「ファイルの種類」(1 始まり)を元パスの拡張子から正しく決めることを固定する。
/// Filter の並びは txt(1) / md(2) / csv(3) / すべて(4)。
/// 実 UI(Form/SaveFileDialog)には触れずヘルパの戻り値のみを検証する。
/// </summary>
public class SaveAsDialogTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FilterIndexFor_NoPath_ReturnsText(string? path)
    {
        Assert.Equal(1, SaveAsDialog.FilterIndexFor(path));
    }

    [Theory]
    [InlineData(@"C:\work\a.txt", 1)]
    [InlineData(@"C:\work\a.md", 2)]
    [InlineData(@"C:\work\a.csv", 3)]
    public void FilterIndexFor_KnownExtension_ReturnsMatchingFilter(string path, int expected)
    {
        Assert.Equal(expected, SaveAsDialog.FilterIndexFor(path));
    }

    [Theory]
    [InlineData(@"C:\work\A.TXT", 1)]
    [InlineData(@"C:\work\A.Md", 2)]
    [InlineData(@"C:\work\A.CSV", 3)]
    public void FilterIndexFor_KnownExtensionUpperCase_ReturnsMatchingFilter(
        string path,
        int expected
    )
    {
        Assert.Equal(expected, SaveAsDialog.FilterIndexFor(path));
    }

    [Theory]
    [InlineData(@"C:\work\a.log")]
    [InlineData(@"C:\work\a.json")]
    [InlineData(@"C:\work\a.txt.bak")]
    [InlineData("a.markdown")]
    public void FilterIndexFor_OtherExtension_ReturnsAllFiles(string path)
    {
        Assert.Equal(4, SaveAsDialog.FilterIndexFor(path));
    }

    [Theory]
    [InlineData(@"C:\work\Makefile")]
    [InlineData(@"C:\work.d\README")]
    public void FilterIndexFor_NoExtension_ReturnsAllFiles(string path)
    {
        // *.txt のままだと AddExtension で .txt が付与されるため「すべて」にする。
        // "C:\work.d\README" はディレクトリ側のドットを拡張子と誤認しないことの確認。
        Assert.Equal(4, SaveAsDialog.FilterIndexFor(path));
    }
}
```

**Step 2: テストが失敗することを確認**

Run: `dotnet build tests/kxEdit.App.Tests -c Release`
Expected: FAIL(CS0117: 'SaveAsDialog' に 'FilterIndexFor' の定義がない)

**Step 3: 実装**

`src/kxEdit.App/SaveAsDialog.cs` — クラス doc コメント 7 行目を差し替え:

```csharp
/// 参照ボタン内部で SaveFileDialog を呼びパスを取得する(初期の種類は元パスの拡張子で選ぶ)。
```

`OnBrowseClicked` の Filter 直後に FilterIndex を追加し、Filter 文字列を定数化する:

```csharp
    // FilterIndexFor の戻り値(1 始まり)はこの並びに対応する。並びを変えるときは両方を直す。
    private const string SaveFilter =
        "テキスト ファイル (*.txt)|*.txt|マークダウン ファイル (*.md)|*.md|CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*";

    private void OnBrowseClicked()
    {
        using var dlg = new SaveFileDialog
        {
            // (既存の OverwritePrompt コメントはそのまま)
            OverwritePrompt = false,
            Filter = SaveFilter,
            FilterIndex = FilterIndexFor(_path.Text),
        };
        if (!string.IsNullOrEmpty(_path.Text))
            dlg.FileName = System.IO.Path.GetFileName(_path.Text);
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _path.Text = dlg.FileName;
    }

    /// <summary>
    /// 参照ダイアログの初期「ファイルの種類」(<see cref="SaveFilter"/> の 1 始まり index)を
    /// 元パスの拡張子(大文字小文字無視)から決める。
    /// パス未指定=1(テキスト。従来どおり)/ .txt=1 / .md=2 / .csv=3 / それ以外・拡張子なし=4(すべて)。
    /// 拡張子なしを「すべて」にするのは、*.txt のままだと AddExtension で .txt が付与されるため。
    /// テスト都合で <c>internal</c>(<c>InternalsVisibleTo kxEdit.App.Tests</c>)。
    /// </summary>
    internal static int FilterIndexFor(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return 1;
        return System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".txt" => 1,
            ".md" => 2,
            ".csv" => 3,
            _ => 4,
        };
    }
```

`src/kxEdit.App/WinFormsFileDialogService.cs` — `PickOpenPath`:

```csharp
        using var dlg = new OpenFileDialog
        {
            // 拡張子で絞り込まない(どの拡張子も最初から選べる)。
            Filter = "すべてのファイル (*.*)|*.*",
        };
```

クラス doc コメント 7〜8 行目の「従来と同一の引数・フィルタで表示し、結果だけを返す薄い Adapter(ロジックなし=挙動不変)」を
「表示し、結果だけを返す薄い Adapter(ロジックなし)」に改める(フィルタが従来と同一ではなくなるため)。

**Step 4: テストが通ることを確認**

Run: `dotnet build tests/kxEdit.App.Tests -c Release && dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~SaveAsDialogTests"`
Expected: PASS(全件)・0 warning

**Step 5: Commit**

```bash
git add src/kxEdit.App/SaveAsDialog.cs src/kxEdit.App/WinFormsFileDialogService.cs tests/kxEdit.App.Tests/SaveAsDialogTests.cs
git commit -m "feat(dialog): 開くダイアログの拡張子絞り込みを撤廃し保存の初期種類を拡張子で選ぶ"
```

---

## 検証(タスク後)

1. 別エージェントによる最終レビュー(コード品質+脆弱性の 1 回統合。§3 簡略化基準)。指摘は fixup commit。
2. `pwsh tools/pre-merge-check.ps1` → EXIT 0。
3. ユーザー手動確認(L5 は不要・目視のみ):
   - 開く: 種類が「すべてのファイル (*.*)」のみで、.log 等も最初から見える。
   - 保存(参照): 未保存=テキスト / a.md=マークダウン / a.csv=CSV / a.log=すべて+ファイル名 `a.log` / Makefile=すべて+`Makefile`。
     それぞれ保存したファイル名に余計な拡張子が付かない。
