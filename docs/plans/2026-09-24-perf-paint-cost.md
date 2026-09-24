# フェーズ 1: 描画の固定費削減(perf-paint-cost) 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development(または executing-plans)で、タスク単位に実装する。

**Goal:** 全面再描画 1 回あたりの固定費(GetWindowText・背景の三重塗り・バックバッファの毎回確保・非 ASCII run の再計測)を減らす。描画結果はピクセル単位で不変とする。

**設計書:** `docs/plans/2026-09-24-general-perf-improvements-design.md` §3・§6(以下「設計書」)
**調査記録:** `docs/plans/2026-09-24-general-perf-audit.md` §4 P-2・§9.2 M-2・§9.3 P-24(以下「調査記録」)
**フェーズ 0 の計画:** `docs/plans/2026-09-24-perf-bench.md`(計測道具の使い方と現状値)

**Architecture:**
- P-24 / P-17 は EditorControl の ctor の `SetStyle` に `CacheText` / `Opaque` を足すだけ。
- P-20 は先に測り、0.5 ms/描画以上の改善があるときだけ、スレッドごとの `BufferedGraphicsContext` で明示的に二重バッファする形へ置き換える。
- P-2 は `GdiCharMetrics` に、文字列キー・合計文字数で上限を決めたメモを足す。
- ピクセル不変は、新設する Smoke `--paint-snapshot` で変更前後の画像を比べて確かめる(フェーズ 3・9 でも使う)。

**Tech Stack:** C# / WinForms(.NET 9)、xUnit、Smoke(kxEdit.Editor.Smoke)、PowerShell 7。

---

## 0. 前提と決定事項

### 0.1 計測の条件(設計書 §3.2・§5.5 の申し送り)

- 数値は Smoke `--perf` の **S1・S3・S7** で比べる(設計書 §6.5)。シナリオの集合は変更前後で `--scenario S1,S3,S7` に固定する。
- 各 3 回、中央値の中央値で比べる。3 回の最小〜最大を揺れとして記録する。
- **NVDA の有無を揃える。** 変更前の計測時の状態を実施記録に書き、変更後も同じ状態で測る。
- 画面のロック・スクリーンセーバー中は測らない(Smoke の自己チェックが EXIT 1 になったら値を捨てる)。
- perf-harness(M-2)は**任意**。十数分キーボードとマウスを占有し、`%APPDATA%\kxEdit` を退避するので、実行前にユーザーの了承を取る。了承が得られなければ Smoke だけで判定する(設計書 §6.5 の完了条件は Smoke の値)。

### 0.2 ピクセル不変の確認手段(設計書 §6.5 の精密化)

設計書は「PrintWindow 画像を比べる」とだけ書いている。手段として Smoke に `--paint-snapshot` を新設する(Task 1)。
- 画面内の Form に EditorControl を置き、決まった状態の列(テーマ × 表示設定 × 選択・IME・水平スクロール)を 1 枚ずつ描かせ、`PrintWindow(PW_CLIENTONLY | PW_RENDERFULLCONTENT)` で PNG にする。
  - `PW_RENDERFULLCONTENT` を付けるのは、WM_PRINT 経路(`OnPrint`)ではなく、**WM_PAINT で実際に画面へ描いた結果**を取るため。本フェーズは WM_PAINT 経路(背景層・二重バッファ)を変えるので、WM_PRINT で撮っても検証にならない。
- `--compare <基準の出力フォルダー>` を付けると、同名の PNG を画素単位で比べ、差のある画素数を表示する。差が 1 画素でもあれば EXIT 1。
- システムキャレットの点滅が写り込まないよう、フォーカスは画面外のダミーボタンに置く(`_hasFocus` は描画の入力ではない = 設計書 §8.1)。
- 新しい共通の道具(フェーズ 3・9 が再利用する)なので、**コード品質の前倒しレビュー**を行う(CLAUDE.md §3 の 4)。

### 0.3 レビューとミューテーション検証

- 前倒しの脆弱性レビュー: 該当なし(外部入力・パス操作・プロセス起動に触れない。`--paint-snapshot` の出力先は引数で受けるが、開発者用の Smoke でファイルを書くだけ)。
- 前倒しのコード品質レビュー: Task 1(`--paint-snapshot`)と、採用した場合の Task 5(描画の二重バッファの置き換え)。
- ミューテーション検証: **実施しない**。描画・テーマは CLAUDE.md §4-A で禁止。`GdiCharMetrics` のメモは列挙(カーソル移動・選択範囲の算出・Undo・検索/字句解析)の外で、設計書 §3.4 も提案していない。

### 0.4 L5

設計書 §4 のとおり「目視と NVDA の簡易確認」(Task 8)。実機での確認はユーザーに依頼する。

---

## Task 1: Smoke `--paint-snapshot`(ピクセル比較の道具)

**Files:**
- Create: `tests/kxEdit.Editor.Smoke/PaintSnapshot.cs`
- Modify: `tests/kxEdit.Editor.Smoke/Program.cs`(`--perf` 分岐の直後に `--paint-snapshot` 分岐を追加)

**Step 1: 骨格を書く**

```csharp
// PaintSnapshot.cs(要点。コメント・doc はプロジェクトの流儀で日本語で書く)
internal static class PaintSnapshot
{
    private const uint PwClientOnly = 0x1;
    private const uint PwRenderFullContent = 0x2;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);

    public static int Run(string[] args)
    {
        // 引数: --paint-snapshot <outDir> [--compare <baseDir>]
        // 誤りは EXIT 2(PerfBench と同じ規約)
        ...
        ApplicationConfiguration.Initialize();
        using var form = new Form { Width = 640, Height = 420, StartPosition = FormStartPosition.Manual,
            Location = Screen.PrimaryScreen?.WorkingArea.Location ?? Point.Empty, ShowInTaskbar = false };
        // キャレットの点滅を写さないため、フォーカスは画面外のダミーに置く
        using var sink = new Button { Location = new Point(-200, -200), Size = new Size(10, 10) };
        using var editor = new EditorControl { Dock = DockStyle.Fill };
        form.Controls.Add(editor);
        form.Controls.Add(sink);
        form.Show();
        form.ActiveControl = sink;
        Application.DoEvents();
        // 作業領域に収まることを自己チェック(収まらなければ EXIT 1)
        ...
        foreach (var state in States())
        {
            state.Apply(editor);            // 外観 → 本文 → 選択/スクロール/IME
            editor.Invalidate(true);
            editor.Update();
            Application.DoEvents();
            Capture(form, Path.Combine(outDir, state.Name + ".png"));
            state.Reset?.Invoke(editor);    // IME の解除など
        }
        form.Close();
        return compareDir is null ? 0 : Compare(outDir, compareDir);
    }
}
```

**状態の列**(名前 = ファイル名。テーマ `default` と `white-on-black` の 2 通りずつ = 16 枚)

| 名前 | 内容 |
|---|---|
| `<theme>-plain` | 既定の表示設定・選択なし・TopLine 0 |
| `<theme>-selection` | 2 行目の途中〜4 行目の途中を選択(部分選択 = CLAUDE.md §4-B) |
| `<theme>-curline` | `HighlightCurrentLine = true`・キャレットは 3 行目 |
| `<theme>-linenum` | `ShowLineNumbers = true` |
| `<theme>-whitespace` | `ShowWhitespace = true`(本文に半角空白・全角空白・タブを含める) |
| `<theme>-hscroll` | 長い行を含む本文で `ScrollX = 120`(両方のスクロールバーが出て右下の角ができる) |
| `<theme>-ime` | キャレットを 2 行目の途中に置いて `__TestApplyComposition("にほんご", 4, [Input×4], [])`(撮影後に `__TestApplyResult("")` で解除) |
| `<theme>-scrolled` | `TopLine = 5` |

- 外観は `new AppSettings { Theme = ..., ShowLineNumbers = ..., HighlightCurrentLine = ..., ShowWhitespace = ... }` を `ApplyAppearance` に渡す(製品と同じ経路)。
- 本文は 60 行(縦スクロールバーを出す)。日本語・ASCII・混在・サロゲートペア(「𠮷」)・空行・200 字の長い行を含める。行の生成はコードに固定する(乱数を使わない)。
- 各状態で自己チェックする: 選択が張られた・`IsComposing` になった・`ScrollX` / `TopLine` が設定値になった。効いていなければ EXIT 1。

**撮影**

```csharp
private static void Capture(Form form, string path)
{
    var size = form.ClientSize;
    using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        nint hdc = g.GetHdc();
        try
        {
            if (!PrintWindow(form.Handle, hdc, PwClientOnly | PwRenderFullContent))
                throw new PaintSnapshotException($"PrintWindow が失敗した(Win32 {Marshal.GetLastWin32Error()})");
        }
        finally { g.ReleaseHdc(hdc); }
    }
    bmp.Save(path, ImageFormat.Png);
}
```

**比較**

- `baseDir` の `*.png` を列挙し、`outDir` の同名ファイルと比べる。片方にしかないファイルは差分として扱う。
- 大きさが違えば差分。同じなら `LockBits(Format32bppArgb)` で行ごとに `Span<int>` として比べ、差のある画素数を数える。
- 表示: `名前: 一致` / `名前: 差 N 画素(最初の座標 (x,y))`。末尾に合計。差があれば EXIT 1。

**Step 2: Program.cs に分岐を追加**

```csharp
// 2026-09-24 性能改善フェーズ 1: --paint-snapshot。描画を変えるフェーズの「ピクセル不変」を
// 変更前後の画像比較で確かめる(docs/plans/2026-09-24-perf-paint-cost.md §0.2)。
if (args.Length > 0 && args[0] == "--paint-snapshot")
{
    return PaintSnapshot.Run(args[1..]);
}
```
(`--perf` 分岐の書き方に合わせる。戻り値の扱いは既存分岐と同じにする。)

**Step 3: 同じコードで 2 回撮って一致することを確かめる**(決定性の確認)

```powershell
$s = "<scratchpad>\paint"
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --paint-snapshot "$s\a"
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --paint-snapshot "$s\b" --compare "$s\a"
```
Expected: 16 枚すべて「一致」・EXIT 0。一致しない場合は原因(キャレット・点滅・タイミング)を潰してから先へ進む。PNG を 2〜3 枚 Read で開き、状態が意図どおり写っているか(選択・IME・右下の角)を目で確かめる。

**Step 4: 逆に、差を検出できることを確かめる**(道具の偽陰性の確認)

一時的に `DefaultStyle()` か `BuildStyle` の 1 色を 1 だけ変えて `--compare "$s\a"` を走らせ、EXIT 1 になることを確認してから元に戻す(`git diff src/` が空であること)。

**Step 5: ビルドとコミット**

```powershell
dotnet build -c Release
git add tests/kxEdit.Editor.Smoke/PaintSnapshot.cs tests/kxEdit.Editor.Smoke/Program.cs
git commit -m "test(perf): Smoke に --paint-snapshot(描画のピクセル比較)を追加"
```

**Step 6: コード品質の前倒しレビュー**(別エージェント。§0.3)

観点: 決定性(撮影の同期・キャレット・DPI)、偽陰性(何も描かれていない画像同士が一致して通る経路がないか = 自己チェック)、フェーズ 3・9 で状態を足しやすい形か。指摘は fixup commit で反映する。

---

## Task 2: 変更前の計測と基準画像(src は未変更)

**Files:** なし(結果は本書の実施記録に書く。生の JSON / PNG は scratchpad に置き、リポジトリに入れない)

**Step 1: NVDA の状態を記録する**

```powershell
Get-Process nvda -ErrorAction SilentlyContinue | Select-Object Id, StartTime
```

**Step 2: Smoke `--perf` を 3 回**

```powershell
$s = "<scratchpad>\perf"
foreach ($i in 1..3) {
  dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --perf --scenario S1,S3,S7 --json "$s\before-$i.json"
}
```
Expected: 各回 EXIT 0。EXIT 1(自己チェック失敗)の回は捨てて取り直す。

**Step 3: 中央値を集計する**

```powershell
$rows = foreach ($f in Get-ChildItem "$s\before-*.json") {
  (Get-Content $f -Raw -Encoding utf8 | ConvertFrom-Json).results |
    Select-Object id, doc, param, median_ms, @{n='run';e={$f.BaseName}}
}
$rows | Group-Object id, doc, param | ForEach-Object {
  $m = $_.Group.median_ms | Sort-Object
  [pscustomobject]@{ key = $_.Name; min = $m[0]; mid = $m[1]; max = $m[2] }
} | Format-Table -AutoSize
```

**Step 4: 基準画像を撮る**

```powershell
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --paint-snapshot "<scratchpad>\paint\before"
```

**Step 5(任意・ユーザーの了承後): perf-harness の M-2**

```powershell
dotnet publish src/kxEdit.App -c Release -o "<scratchpad>\pub-before"
pwsh -File tools/perf-harness.ps1 -PublishDir "<scratchpad>\pub-before" -Scenario M-2   # 3 回
```

**Step 6:** 集計値(min / 中央 / max)と NVDA の状態を、本書末尾の実施記録に書いてコミットする。

```powershell
git add docs/plans/2026-09-24-perf-paint-cost.md
git commit -m "docs(perf): フェーズ 1 の変更前の計測値を記録"
```

---

## Task 3: P-24 描画ごとの GetWindowText をやめる(`CacheText`)

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.cs`(ctor の `SetStyle`・WndProc の WM_GETTEXT 分岐にテスト用の計数)
- Modify: `src/kxEdit.Editor/EditorControl.Uia.cs`(TestHook の置き場。既存の `TestHook_*` と並べる)
- Create: `tests/kxEdit.Editor.Tests/EditorControlPaintCostTests.cs`

**Step 1: 事前確認(grep)**

```powershell
# base の Control.Text に書く箇所がないこと(設計書 §6.1「確認すること」)
rg -n "\(\(Control\)|as Control\)" src
```
Expected: EditorControl に対する該当なし。

**Step 2: テストフックを足す**

WndProc の WM_GETTEXT 分岐(`EditorControl.cs` の「P5 Task 7」)に計数を足す。

```csharp
if (m.Msg == NativeMethods.WM_GETTEXT || m.Msg == NativeMethods.WM_GETTEXTLENGTH)
{
    _testHook_getTextCount++;
    m.Result = IntPtr.Zero;
    return;
}
```

```csharp
// EditorControl.Uia.cs(既存の TestHook_* の並び)
// 2026-09-24 性能改善フェーズ 1(P-24): 自 HWND が受けた WM_GETTEXT / WM_GETTEXTLENGTH の回数。
// 描画のたびに WinForms が WindowText を読んでいないことを固定する。UI スレッド専用。
private int _testHook_getTextCount;
internal static int TestHook_GetTextCount(EditorControl c) => c._testHook_getTextCount;
internal static void TestHook_ResetGetTextCount(EditorControl c) => c._testHook_getTextCount = 0;
```
(フィールドは本体の `EditorControl.cs` で宣言する流儀なら、そちらに置く。)

**Step 3: 失敗するテストを書く**

```csharp
namespace kxEdit.Editor.Tests;

/// <summary>
/// 2026-09-24 性能改善フェーズ 1(設計書 §6)。描画の固定費を削った変更が、
/// 観測できる挙動(Text・アクセシブル名・描画の配送)を変えていないことを固定する。
/// </summary>
public class EditorControlPaintCostTests
{
    private static (Form F, EditorControl C) MakeHosted()
    {
        var f = new Form { Size = new Size(400, 200) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString("hello\r\nあいう\r\n"));
        return (f, c);
    }

    /// <summary>
    /// P-24: 描画 1 回ごとに WinForms の PaintWithErrorHandling が WindowText を読み、
    /// 自 HWND に WM_GETTEXTLENGTH / WM_GETTEXT を送っていた(結果は常に "")。CacheText で止める。
    /// 画面外の窓には WM_PAINT が来ないので、WM_PRINTCLIENT 経由(DrawToBitmap)で同じ
    /// PaintWithErrorHandling を通す。
    /// </summary>
    [Fact]
    public void Painting_does_not_query_its_own_window_text() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
            {
                int paints = 0;
                c.Paint += (_, _) => paints++;
                using var bmp = new Bitmap(c.Width, c.Height);
                EditorControl.TestHook_ResetGetTextCount(c);

                c.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));

                Assert.True(paints >= 1, "描画が起きていない(前提の崩れ)");
                Assert.Equal(0, EditorControl.TestHook_GetTextCount(c));
            }
        });

    /// <summary>base の Control.Text は "" のまま(CacheText 下では _text ?? "" を返す)。</summary>
    [Fact]
    public void Base_Control_Text_stays_empty() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
                Assert.Equal(string.Empty, ((Control)c).Text);
        });

    /// <summary>MSAA(OBJID_CLIENT)の名前の出所 = AccessibilityObject.Name も変わらないこと。</summary>
    [Fact]
    public void Msaa_name_stays_empty() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeHosted();
            using (f)
                Assert.True(string.IsNullOrEmpty(c.AccessibilityObject.Name));
        });
}
```

**Step 4: 赤を確認する**

```powershell
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~EditorControlPaintCostTests"
```
Expected: `Painting_does_not_query_its_own_window_text` だけが FAIL(計数 > 0。調査記録の経路なら 1 描画あたり 4)。他の 2 本は PASS(現状の性質の固定)。
- `Msaa_name_stays_empty` が現状で FAIL する場合は、現状の値を期待値にして書き直す(不変であることが目的)。
- 計数が 0 のまま(DrawToBitmap では PaintWithErrorHandling の WindowText 読みに届かない)場合は、描画を配送させる手段を変える: 画面内の窓(`StartPosition = Manual`・作業領域の左上)で `Invalidate()` + `Update()`。それでも届かなければ、計数を DrawToBitmap の外で起きたものと区別できていない可能性があるので、`Paint` の中で計数の差分を採る形にする。手段を変えたら実施記録に書く。

**Step 5: 実装**

```csharp
SetStyle(
    ControlStyles.AllPaintingInWmPaint
        | ControlStyles.OptimizedDoubleBuffer
        | ControlStyles.ResizeRedraw
        | ControlStyles.UserPaint
        | ControlStyles.Selectable
        // 2026-09-24 性能改善フェーズ 1(P-24): WinForms は描画のたびに WindowText を読み、
        // 自 HWND に WM_GETTEXTLENGTH / WM_GETTEXT を送る(描画 1 回で 4 往復)。本コントロールは
        // 本文非公開のため WM_GETTEXT に 0 を返し、Text も new で隠蔽しているので、結果は常に ""。
        // CacheText で読みを止める。base の Control.Text は _text ?? "" を返す(誰も設定しない = "")。
        | ControlStyles.CacheText,
    true
);
```

**Step 6: 緑を確認し、Editor 層を全件流す**

```powershell
dotnet test tests/kxEdit.Editor.Tests
```
Expected: 全件 PASS。

**Step 7: コミット**

```powershell
git add src/kxEdit.Editor/EditorControl.cs src/kxEdit.Editor/EditorControl.Uia.cs tests/kxEdit.Editor.Tests/EditorControlPaintCostTests.cs
git commit -m "perf(editor): 描画ごとの GetWindowText を CacheText で止める(P-24)"
```

---

## Task 4: P-17 背景の三重塗りを二重にする(`Opaque`)

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.cs`(ctor の `SetStyle`)
- Modify: `src/kxEdit.Editor/EditorControl.Paint.cs`(`OnPaint` 冒頭の `g.Clear` のコメント)

**Step 1: 実装**

`SetStyle` に `| ControlStyles.Opaque` を足し、理由をコメントに書く。

```csharp
        // 2026-09-24 性能改善フェーズ 1(P-17): 背景層(OnPaintBackground)を塗らない。
        // 全面は OnPaint 冒頭の g.Clear と FrameBuilder の工程 1 が塗るので、背景層は三重目だった。
        | ControlStyles.Opaque,
```

`OnPaint` の `g.Clear(BackColor);` に、残す理由のコメントを足す(設計書 §6.2): `_scrollX` のシフトで生じる右端の隙間と、スクロールバーの交差部(右下の角)を埋めている。Opaque で背景層を消したので、この行が唯一の全面の下塗りになった。

**Step 2: テストとピクセル比較**

```powershell
dotnet test tests/kxEdit.Editor.Tests
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --paint-snapshot "<scratchpad>\paint\t4" --compare "<scratchpad>\paint\before"
```
Expected: 全件 PASS。16 枚すべて一致。とくに `*-hscroll`(右下の角)を確認する。

**Step 3: コミット**

```powershell
git add src/kxEdit.Editor/EditorControl.cs src/kxEdit.Editor/EditorControl.Paint.cs
git commit -m "perf(editor): 背景層の塗りを Opaque で省く(P-17)"
```

リサイズ中のちらつきは目視でしか確かめられないので、Task 8 の L5 に回す。

---

## Task 5: P-20 バックバッファの毎回確保(先に測って採否を決める)

**Files(測定のみ・コミットしない):** `tests/kxEdit.Editor.Smoke/PerfBench.cs`(一時的な 1 行)

**Step 1: 測定**

Task 4 までの状態で、`PerfBench.Run` の `ApplicationConfiguration.Initialize();` の直後に一時的に次を足す。

```csharp
BufferedGraphicsManager.Current.MaximumBuffer = SystemInformation.VirtualScreen.Size; // P-20 の測定用・コミットしない
```

足した版と足さない版で `--scenario S7` を各 3 回走らせ、S7 の中央値を比べる(JSON は `p20-on-*.json` / `p20-off-*.json`)。終わったら元に戻し、`git diff tests/` が空であることを確かめる。

**Step 2: 採否**

- ja10k・en10k の両方で中央値の差が **0.5 ms/描画未満**なら不採用。実施記録に値と判断を書いて Task 6 へ進む。
- 0.5 ms 以上なら採用し、Step 3 以降を行う。値は実施記録に書く。

**Step 3(採用時): 実装**

- `ControlStyles.OptimizedDoubleBuffer` を外す(`AllPaintingInWmPaint` は残す。WM_ERASEBKGND を捨てる意味は変わらない)。
- `EditorControl.Paint.cs` にスレッドごとの共有コンテキストを置く。

```csharp
// 2026-09-24 性能改善フェーズ 1(P-20): WinForms の OptimizedDoubleBuffer は既定の
// MaximumBuffer(225×96)を超える面積で、描画のたびに一時コンテキストと DIB を作って捨てる。
// MaximumBuffer を画面サイズにした専用のコンテキストを使い回す。
// ・プロセス全体の BufferedGraphicsManager.Current は変えない(他コントロールへの影響が読めない)。
// ・[ThreadStatic] にするのは、BufferedGraphicsContext がスレッド安全でなく、テストは STA スレッドを
//   並行に立てるため。製品は UI スレッド 1 本なので、タブ数によらず 1 個になる。
[ThreadStatic]
private static BufferedGraphicsContext? t_paintBuffer;

private static BufferedGraphicsContext PaintBuffer =>
    t_paintBuffer ??= new BufferedGraphicsContext
    {
        MaximumBuffer = SystemInformation.VirtualScreen.Size,
    };
```

`OnPaint` は、本体の描画を `PaintBody(Graphics g)` に切り出し、次の形にする。

```csharp
protected override void OnPaint(PaintEventArgs e)
{
    using (var buffer = PaintBuffer.Allocate(e.Graphics, ClientRectangle))
    {
        PaintBody(buffer.Graphics);   // 旧 OnPaint の g.Clear 〜 RefreshClientToScreenOrigin
        buffer.Render(e.Graphics);
    }
    base.OnPaint(e);
}
```
- `base.OnPaint(e)`(Paint イベント)は従来どおり最後に呼ぶ。購読者は e.Graphics(画面)に描くことになるが、src に購読者はない(`rg "\.Paint \+=" src` で確認)。Smoke の `paints_per_op` の数え方も変わらない。
- `VirtualScreen` の変化(モニタの抜き差し)では、Allocate が MaximumBuffer を超えた面積を一時確保に回すだけなので、正しさは変わらない。

**Step 4(採用時): 確認**

```powershell
dotnet test tests/kxEdit.Editor.Tests
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --paint-snapshot "<scratchpad>\paint\t5" --compare "<scratchpad>\paint\before"
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --perf --scenario S7   # 効果が出ていること
```
Expected: 全件 PASS・16 枚一致・S7 が Step 1 の「足した版」と同程度に下がる。

**Step 5(採用時): コミットとコード品質の前倒しレビュー**

```powershell
git add src/kxEdit.Editor/EditorControl.cs src/kxEdit.Editor/EditorControl.Paint.cs
git commit -m "perf(editor): バックバッファを使い回す(P-20)"
```
描画経路の置き換えなので、別エージェントでコード品質レビューを行う(観点: `AllPaintingInWmPaint` と Opaque の組合せ、WM_PRINTCLIENT 経路、Dispose 漏れ、スレッド)。

---

## Task 6: P-2 非 ASCII を含む run の幅をメモ化する

**Files:**
- Modify: `src/kxEdit.Editor/GdiCharMetrics.cs`
- Modify: `tests/kxEdit.Editor.Tests/GdiCharMetricsCacheTests.cs`

**Step 1: 失敗するテストを書く**(`GdiCharMetricsCacheTests` に追加)

```csharp
    /// <summary>
    /// 2026-09-24 性能改善フェーズ 1(P-2): 非 ASCII を含む複数コードポイントの run も
    /// MeasureText の結果そのものを返す(キャッシュ経由でも参照実装と一致)。
    /// 先頭が ASCII で途中から非 ASCII になる run(行番号付きの行など)も含める。
    /// </summary>
    [Theory]
    [InlineData("あいうえお")]
    [InlineData("あa")]
    [InlineData("00001: 吾輩は猫である。")]
    [InlineData("𠮷野家")]
    [InlineData("é")] // 結合文字
    public void MeasureRun_multi_codepoint_run_matches_reference_and_is_cached(string run) =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);

            int first = m.MeasureRun(run);
            int second = m.MeasureRun(run);

            Assert.Equal(Reference(run, font), first);
            Assert.Equal(first, second);
            Assert.Equal(1, m.TestHook_RunCacheCount);
            Assert.Equal(run.Length, m.TestHook_RunCacheChars);
        });

    /// <summary>span キー(部分 span)と string キーが同じエントリを引くこと。</summary>
    [Fact]
    public void Span_slice_and_string_share_an_entry() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);
            string line = "xxあいうえおyy";

            int bySpan = m.MeasureRun(line.AsSpan(2, 5)); // "あいうえお"(ToString を経ない slice)
            int byString = m.MeasureRun("あいうえお");

            Assert.Equal(Reference("あいうえお", font), bySpan);
            Assert.Equal(bySpan, byString);
            Assert.Equal(1, m.TestHook_RunCacheCount);
        });

    /// <summary>1 件の上限(4,096 文字)を超える run は格納しない。ちょうど上限は格納する。</summary>
    [Fact]
    public void Runs_longer_than_the_per_entry_limit_are_not_cached() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);
            string atLimit = new('あ', GdiCharMetrics.MaxCachedRunChars);
            string over = new('い', GdiCharMetrics.MaxCachedRunChars + 1);

            Assert.Equal(Reference(over, font), m.MeasureRun(over));
            Assert.Equal(0, m.TestHook_RunCacheCount);

            Assert.Equal(Reference(atLimit, font), m.MeasureRun(atLimit));
            Assert.Equal(1, m.TestHook_RunCacheCount);
        });

    /// <summary>
    /// 合計文字数の上限を超える格納で全消去し、その run だけが残る。
    /// 上限ちょうどまでは消去しない(境界を両側から見る)。
    /// </summary>
    [Fact]
    public void Exceeding_the_total_char_budget_clears_the_cache() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);
            int per = GdiCharMetrics.MaxCachedRunChars;
            int fit = GdiCharMetrics.RunCacheBudgetChars / per; // ちょうど予算を使い切る件数

            for (int i = 0; i < fit; i++)
                m.MeasureRun(UniqueRun(i, per));
            Assert.Equal(fit, m.TestHook_RunCacheCount);
            Assert.Equal(GdiCharMetrics.RunCacheBudgetChars, m.TestHook_RunCacheChars);

            m.MeasureRun("あい"); // 予算を 2 文字超える

            Assert.Equal(1, m.TestHook_RunCacheCount);
            Assert.Equal(2, m.TestHook_RunCacheChars);
        });

    /// <summary>i ごとに内容の異なる、長さ len の非 ASCII run。</summary>
    private static string UniqueRun(int i, int len)
    {
        var s = new string('あ', len).ToCharArray();
        s[0] = (char)('ア' + (i % 80));
        s[1] = (char)('亜' + (i / 80));
        return new string(s);
    }

    /// <summary>ASCII だけの run と単一コードポイントは、run のメモに入らない(経路不変)。</summary>
    [Fact]
    public void Ascii_runs_and_single_codepoints_do_not_use_the_run_cache() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);

            m.MeasureRun("hello world");
            m.MeasureRun("あ");
            m.MeasureRun("😀");

            Assert.Equal(0, m.TestHook_RunCacheCount);
        });
```

- `RunCacheBudgetChars` が `MaxCachedRunChars` の倍数であることを前提にしている(256K ÷ 4,096 = 64)。前提をテスト冒頭で `Assert.Equal(0, RunCacheBudgetChars % per)` として明示する。
- `UniqueRun` は 64 件を区別できれば足りる(`i / 80` は 0 のまま)。

**Step 2: 赤を確認する**

```powershell
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~GdiCharMetricsCacheTests"
```
Expected: コンパイルエラー(`TestHook_RunCacheCount` 等が未定義)。

**Step 3: 実装**

```csharp
    // 2026-09-24 性能改善フェーズ 1(P-2): 非 ASCII を含む複数コードポイントの run の幅メモ。
    // 描画(FrameBuilder の本文 run)・横スクロールバー(UpdateHorizontalScrollbar の全可視行)・
    // キャレット X(PixelMapper.OffsetToPx の prefix)が、同じ run を描画・打鍵ごとに測り直していた
    // (ja10k で 2.5 ms/打鍵 = 調査記録 §9.2)。格納するのは MeasureText の結果そのもの = 返す値は不変。
    //
    // 上限は件数ではなくキーの合計文字数で決める(件数だと最悪 4,096 字 × 件数に膨らみ、
    // それがタブ数倍になる)。溢れたら全消去する。1 件が MaxCachedRunChars を超える run は格納しない。
    // 寿命は _nonAsciiWidths と同じ = フォントの寿命。UI スレッド専用(クラス doc の契約)。
    internal const int MaxCachedRunChars = 4096;
    internal const int RunCacheBudgetChars = 256 * 1024;

    private readonly Dictionary<string, int> _runWidths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _runWidthsBySpan;
    private int _runCacheChars;

    internal int TestHook_RunCacheCount => _runWidths.Count;
    internal int TestHook_RunCacheChars => _runCacheChars;
```

ctor の末尾で `_runWidthsBySpan = _runWidths.GetAlternateLookup<ReadOnlySpan<char>>();`。

`MeasureRun` の ASCII ループの `return TextRenderer.MeasureText(text.ToString(), …)` を `return CachedRunWidth(text);` に置き換える。

```csharp
    /// <summary>
    /// 非 ASCII を含む複数コードポイントの run の幅をメモ化して返す(フィールドのコメント参照)。
    /// ヒット時は文字列を割り当てない(span のまま引く)。
    /// </summary>
    private int CachedRunWidth(ReadOnlySpan<char> text)
    {
        if (text.Length <= MaxCachedRunChars && _runWidthsBySpan.TryGetValue(text, out int cached))
            return cached;

        string s = text.ToString();
        int width = TextRenderer.MeasureText(s, _font, MaxSize, MeasureFlags).Width;
        if (s.Length <= MaxCachedRunChars)
        {
            if (_runCacheChars + s.Length > RunCacheBudgetChars)
            {
                _runWidths.Clear();
                _runCacheChars = 0;
            }
            _runWidths[s] = width;
            _runCacheChars += s.Length;
        }
        return width;
    }
```

- 既存の `Multi_codepoint_run_still_goes_through_MeasureText_as_a_whole` のコメント「キャッシュを使ってはならない」は、「コードポイント幅の和で代用してはならない(run 全体の MeasureText の結果を使う。run 単位のメモは可)」に直す。テスト本体は変えない。
- クラス doc の「非 ASCII の 1 コードポイントは…」の段落に、run のメモの 1 文を足す。

**Step 4: 緑を確認する**

```powershell
dotnet test tests/kxEdit.Editor.Tests
dotnet test tests/kxEdit.Core.Tests
```
Expected: 全件 PASS。

**Step 5: 全消去の頻度を確かめる**(設計書 §6.4「メモリと置換頻度の見積り」)

一時的に `CachedRunWidth` の全消去の箇所で `Console.WriteLine("[P-2] clear")` を出し(コミットしない)、`--perf --scenario S1,S3 --n 200` で消去が何回起きるかを見る。
- S1・S3 で 0 回、または 1 回程度なら現形で確定。
- 頻繁(数十回以上)なら、設計書の代替案「prefix の計測はメモの対象にしない」を検討し、ユーザーに相談する。
結果を実施記録に書き、一時コードを戻す(`git diff src/` は Step 3 の変更だけ)。

**Step 6: ピクセル比較とコミット**

```powershell
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --paint-snapshot "<scratchpad>\paint\t6" --compare "<scratchpad>\paint\before"
git add src/kxEdit.Editor/GdiCharMetrics.cs tests/kxEdit.Editor.Tests/GdiCharMetricsCacheTests.cs
git commit -m "perf(editor): 非 ASCII を含む run の幅をメモ化する(P-2)"
```
Expected: 16 枚一致。

---

## Task 7: 変更後の計測

**Step 1:** Task 2 と同じ条件(NVDA の有無を揃える)で、`--perf --scenario S1,S3,S7` を 3 回(`after-*.json`)。Task 2 の Step 3 の集計で min / 中央 / max を出す。

**Step 2: 判定**(設計書 §3.2)

- S7・S1・S3a が、変更前の揺れ(3 回の min〜max)を超えて下がっていること。
- 揺れの範囲に収まる項目は、PR に書いてユーザーに採否を判断してもらう。

**Step 3(任意・ユーザーの了承後):** perf-harness の M-2 を変更後の publish で 3 回。

**Step 4:** 最終のピクセル比較(`--compare before`)が 16 枚一致すること。

**Step 5:** 値を実施記録に書いてコミットする。

```powershell
git add docs/plans/2026-09-24-perf-paint-cost.md
git commit -m "docs(perf): フェーズ 1 の変更後の計測値を記録"
```

---

## Task 8: L5(目視と NVDA の簡易確認)・最終レビュー・品質ゲート

**Step 1: L5(ユーザーに依頼。設計書 §6.5)**

変更後の publish を起動し、次を確認してもらう。目視は PrintWindow の実解像度画像で判定する(縮小スクリーンショットで判定しない)。
- 目視: テーマ(既定・黒地)、選択、現在行強調、行番号、空白表示、水平スクロール時の右端、右下の角、IME の未確定表示、**リサイズ中のちらつき**(Opaque の影響。`--paint-snapshot` では見られない)。
- NVDA: フォーカス時の名前「本文」、行の読み上げ、ハイライト矩形の位置。
- `tools/sr-regression.ps1` は、SR 経路(UIA)を変えていないので任意。WM_GETTEXT 分岐に計数を足しただけで応答は不変。

**Step 2: 最終ブランチレビュー(2 パス・別エージェント)**

- コード品質パス(ミューテーション検証のスポットチェックは §0.3 のとおり対象なし)
- 脆弱性パス
指摘は ① fixup / ② PR に記載して受容 / ③ 理由付き却下 のどれかで扱う。

**Step 3: 品質ゲート**

```powershell
pwsh -File tools/pre-merge-check.ps1
```
Expected: EXIT 0。

**Step 4: PR**(CLAUDE.md §7)

description に、変更前後の計測値(min / 中央 / max)・P-20 の採否と値・P-2 の全消去の頻度・ピクセル比較の結果・L5 の結果・レビュー経緯を書く。意図的な挙動差はない(設計書 §3.5 にフェーズ 1 の行はない)。

**Step 5(マージ後):** 設計書 §6 の末尾に「実施記録」を追記する(設計書 §3.1 の 6)。

---

## 実施記録

### Task 1: `--paint-snapshot`(dc594ba・fixup f14d237 / 4b74586)

**計画からの逸脱**
- 描画のさせ方: `editor.Update()` ではなく、`RedrawWindow(form, RDW_INVALIDATE|ERASE|ALLCHILDREN|UPDATENOW)` → `DoEvents` → `DwmFlush` を使う。`Control.Update` は子の HWND(スクロールバー)を同期で描かないため。
- 画素は、`Span<int>` の行比較ではなく、全画素を `int[]` にコピーして比べる。

**§0.2 の前提の誤り(コード品質の前倒しレビュー I-2 で実測)**
- `PrintWindow(PW_RENDERFULLCONTENT)` は、撮影の中で自分から全面の WM_PAINT を起こす。スタックは `PrintWindow → NativeWindow.Callback → Control.WmPaint → … → EditorControl.OnPaint`。
- したがって撮っているのは「撮影の時点の状態から、WM_PAINT の経路(`OnPrint` ではない)で全面を描き直した絵」で、§0.2 に書いた「画面へ描いた結果(DWM のリダイレクト面)」ではない。
- フェーズ 1 の目的(描画処理そのもののピクセル不変)は、これで確かめられる。
- 一方、Invalidate の省略や部分再描画のせいで画面に古い絵が残る不具合は写らない。

**レビューで足したもの**
- 自己チェック
  - 撮影中に描画が起きること
  - 16 枚の画素が互いに異なること
  - 描画中の例外で EXIT 1 にする(`ThreadException`。ダイアログで止まらない)
- 引数の扱い
  - 出力と基準が同じフォルダーなら EXIT 2(末尾の区切り文字も正規化してから比べる)
  - 出力先に PNG が既にあれば EXIT 2(消さない)
- 撮影環境を `env.txt` に書き、比較のときに照合する

**却下した指摘**
- `ParseArgs` の中の I/O 例外が未処理のまま終わる: 終了コードは 0 以外なので実害がない。
- env.txt の OS の行に月例更新の番号(UBR)が入らない: 比較は同じ機体・同じ日に行うので、区別する必要がない。
- マウスのホバー: この機体では差が出なかった。

**フェーズ 3・9 への申し送り**
本道具は画面に古い絵が残る不具合を原理的に検出できない。それを検出するには、次のものを足す必要がある。
1. **描画を起こさない撮り方**
   - 例: `GetDC` + `BitBlt` で、画面上の絵をそのまま読む。
   - 撮影の前後で描画回数が変わらないことを自己チェックする。
2. **遷移する状態の比較**
   - 状態 A を全面描画した後に操作(キャレット移動・1 行スクロールなど)をして、メッセージを流すだけにする。
   - その絵を 1. の方法で撮り、同じ最終状態を全面描画した絵と、同じ実行の中で比べる。基準画像は要らない。
3. **フェーズ 3 の検証**: フレームが変わらない操作の後に、描画回数が 0 であることを確かめる。

### Task 2: 変更前の計測(2026-09-25・src は main `9713c2e` と同一)

- 条件
  - NVDA 起動中(フェーズ 0 の現状値と同じ)
  - Release ビルド
  - `--perf --scenario S1,S3,S7`、既定の n=200 / warmup=20
  - 3 回とも EXIT 0
- 基準画像: `--paint-snapshot` の 16 枚(scratchpad。リポジトリには入れない)

各回の中央値(ms)の、3 回の最小・中央・最大:

| シナリオ | 文書 | 最小 | 中央 | 最大 |
|---|---|---|---|---|
| S1(→/←) | ja10k | 8.84 | 9.00 | 9.10 |
| S1 | en10k | 7.25 | 7.30 | 7.34 |
| S3a(1 文字挿入) | ja10k | 10.80 | 10.87 | 10.92 |
| S3a | en10k | 7.61 | 7.83 | 7.89 |
| S3b(BackSpace) | ja10k | 10.83 | 10.83 | 10.90 |
| S3b | en10k | 7.68 | 7.71 | 7.83 |
| S7(全面再描画) | ja10k | 8.83 | 8.88 | 9.01 |
| S7 | en10k | 6.79 | 6.80 | 6.91 |

フェーズ 0 の現状値(S7 ja10k 9.05 ms)と同程度である。

### `--paint-snapshot` の破棄時の例外(Task 3 の実行中に 1/7 回・fixup 548d311)

**症状**
- 16 枚を撮り終えた後、`form.Close()` の後の `using` でもう一度 Dispose したところで落ちた。
- 例外は「CreateHandle() の実行中は Dispose() を呼び出せません」(`InvalidOperationException`)。

**調査結果**(別エージェントによる調査)
- 30 回実行しても再現しなかった(0/30)。
- 同じ例外が `GdiBench` でも出たことがある(`2026-08-02-large-line-wrap-perf-design.md` §9.7)。本道具だけの問題ではない。
- 有力な仮説
  - NVDA などの UIA クライアントが、WinForms 標準の `FormAccessibleObject.Name` を読む。この読み取りは `WindowText` → `Handle` getter を通る。
  - そのため RPC スレッドの上で、破棄中の窓が作り直される。そこに UI スレッドの Dispose が重なると、この例外になる。
  - RPC スレッドで `Form.CreateHandle` が走るスタックは実測で捕えた。ただし、その実行では例外にはならなかった。
- kxEdit 自前の UIA 経路(`UiaTextHostAdapter`)は主因ではない。`_hwnd` のキャッシュと `IsHandleCreated` のガードで、`Handle` getter を通らない。

**対処**: 二重の Dispose をやめ、後片付けで出るこの例外だけを警告にとどめる(`CloseQuietly`)。例外が出る時点で、撮影と比較の結果はもう決まっている。

**申し送り**
- **製品**: 同じ競合は、NVDA を常用する製品のフォーム・ダイアログ・タブを閉じる処理(`DocumentManager.TryClose`)でも、理論上は起きうる。実害の報告はなく、原因も未確定なので、本フェーズでは扱わない。
- **`GdiBench`**: 同じ形の後片付け(Close の後に using で Dispose)なので、同じ対処の候補になる。範囲外なので今回は変えない。

### Task 5: P-20 の測定と採否(2026-09-25)

**条件**
- Task 4 まで入れた状態で測った。NVDA は起動中。
- `--perf --scenario S7` を各 3 回。
- `PerfBench.Run` に `BufferedGraphicsManager.Current.MaximumBuffer = SystemInformation.VirtualScreen.Size` を一時的に入れた版と、入れない版を比べた。
  - この 1 行はコミットしていない。
  - 入れた版では、実行中に MaximumBuffer = 1024×767 になっていることを表示させて確かめた。
- 1 回目の測定は捨てた。破棄時の例外を調べるエージェントが、同じ時刻に Smoke を繰り返し起動していて、変更なしの版でも 6.5〜8.3 ms と大きく揺れたため。調査が終わってから測り直した値を下に載せる。

S7 の各回の中央値(ms)。3 回の最小〜最大:

| 文書 | 変更なし | MaximumBuffer を画面サイズ | 差(中央) |
|---|---|---|---|
| ja10k | 8.54〜8.58(中央 8.55) | 7.69〜7.79(中央 7.79) | −0.76 |
| en10k | 6.49〜6.61(中央 6.51) | 5.69〜5.69(中央 5.69) | −0.82 |

**判断**: 両方の文書で、採用の基準 0.5 ms/描画を超えたので**採用する**。

参考: 変更なしの版の S7(ja10k 8.55 ms)は、Task 2 の 8.88 ms より 0.33 ms 低い。Task 3・4(P-24・P-17)の効果と見られるが、効果の判定は Task 7 で同じ条件で測ってから行う。

**実装(b91183c・fixup b784f00 / c4af042)**

計画からの逸脱と精密化。いずれも、逆コンパイルした旧 `Control.WmPaint`(WinForms 9.0.20)の挙動に揃えるためのもの。
- **バッファ側のクリップ**: バッファ側の Graphics に、更新領域のクリップ(`SetClip(e.ClipRectangle)`)を掛ける。
- **確保に失敗したときの退避**: `Allocate` が失敗したら、バッファなしで直接描く(仕様レビューで見つかった計画の見落とし)。
  - 旧 WmPaint にあった退避である。これが無いと、失敗の後ずっと赤い × になる。
  - 退避する例外の判定は、`System.ExceptionExtensions.IsCriticalException` から OutOfMemory を除いたもの。出荷版の IL で照合した。
- **空の更新領域**: 更新領域が空なら何もせずに return する(Paint イベントも出さない)。旧 WmPaint がそうしていたため。

**受容した挙動差**(PR に書く): バックバッファの DIB を、プロセスの寿命のあいだ持ち続ける。
- 大きさは、これまでに描いた最大の幅 × 最大の高さ × 4 byte。1920×1080 で約 8MB、4K で約 33MB。
- 旧経路は、描画のたびに作って捨てていた。

**実装後の S7**: ja10k 7.92 ms、en10k 5.84 ms(1 回)。採否を決めたときの計測(`MaximumBuffer` を広げた版)と同程度だった。

### Task 6: P-2(ada2f15・fixup cd546ed)

- 全消去の回数: `--perf --scenario S1,S3 --n 200` の実行中は 0 回だった。prefix の計測もメモの対象のままにする(設計書 §6.4 の代替案は採らない)。

### Task 7: 変更後の計測(2026-09-25・ada2f15)

**条件**: Task 2 と同じ(NVDA 起動中、Release、`--perf --scenario S1,S3,S7`、3 回とも EXIT 0)。

**ピクセル比較**: `--paint-snapshot` の変更前後(Task 2 の基準と ada2f15)で、16 枚すべて一致した。

各回の中央値(ms)。3 回の最小〜最大:

| シナリオ | 文書 | 変更前 | 変更後 | 差(中央) |
|---|---|---|---|---|
| S1(→/←) | ja10k | 8.84〜9.10(9.00) | 6.93〜7.11(6.98) | −2.02(−22%) |
| S1 | en10k | 7.25〜7.34(7.30) | 6.55〜6.65(6.63) | −0.67(−9%) |
| S3a(1 文字挿入) | ja10k | 10.80〜10.92(10.87) | 7.47〜7.62(7.60) | −3.27(−30%) |
| S3a | en10k | 7.61〜7.89(7.83) | 7.01〜7.20(7.10) | −0.73(−9%) |
| S3b(BackSpace) | ja10k | 10.83〜10.90(10.83) | 7.44〜7.56(7.53) | −3.30(−30%) |
| S3b | en10k | 7.68〜7.83(7.71) | 7.08〜7.19(7.08) | −0.63(−8%) |
| S7(全面再描画) | ja10k | 8.83〜9.01(8.88) | 6.84〜6.96(6.86) | −2.02(−23%) |
| S7 | en10k | 6.79〜6.91(6.80) | 6.02〜6.06(6.03) | −0.77(−11%) |

**判定**(設計書 §3.2): 全項目で、変更後の最大値が変更前の最小値を下回った。揺れを超えた改善である。

**読み**
- ja10k の改善が大きいのは P-2 による。非 ASCII の run の再計測が、描画と横スクロールバーの両方から消えた。
- en10k の改善は、主に P-20・P-24・P-17 の固定費削減による。

### Task 8: 最終ブランチレビュー(2 パス)の指摘と扱い

コード品質パス・脆弱性パスとも**承認**(Critical / Important / High / Medium はなし)。

| 指摘 | 扱い |
|---|---|
| 品質 M-1: バックバッファの DIB の色深度が、最初の描画のときの形式のまま固定される | ② 受容。16bpp の RDP のあとで 32bpp の画面に戻る、というまれな条件でしか差が出ない。BitBlt が変換するので、表示が崩れることはない |
| 品質 M-2: 上限ちょうどの run のヒットと、文字数の二重計上を検出するテストがない | ① d4befa3。テストに 2 回目の計測を足し、格納を `TryAdd` にした |
| 品質 M-3: `IsCriticalForPaintFallback` にテストがない | ① 03c7a7c。`internal` にして、9 型の表形式テストで固定した |
| 脆弱性 Low-1: 全消去しても `Dictionary` の容量が残る(最悪で 1 タブあたり数 MB) | ① d4befa3。`Clear()` の後に `TrimExcess()` を呼ぶ |
| 脆弱性 Low-2: DIB がプロセスの寿命のあいだ残る | ② 受容(Task 5 の「受容した挙動差」と同じ件) |

**変異のスポットチェック**(品質 M-2 の検索条件 `<=` → `<`)
- `TryAdd` にする前は、追加したテストで検出できた(文字数が期待 4096 に対して 8192)。
- `TryAdd` にした後は、この変異では文字数がずれない。結果は「毎回測り直す」性能の劣化だけで、返す値は変わらないので、テストでは検出できない。値は不変なので受容する。

### L5(2026-09-25)

- **NVDA の簡易確認**: ユーザーが実機で確認し、OK だった。確認した項目は、フォーカス時の名前「本文」、行の読み上げ、ハイライト矩形の位置。
- **目視**: ユーザーの判断で省いた。
  - 全面を描いた絵は `--paint-snapshot` の 16 枚が一致しているので、確かめられている。
  - 次の 2 つは未確認のまま残る。
    - リサイズ中のちらつき(Opaque と自前の二重バッファの影響)
    - 部分的な再描画で画面に古い絵が残る不具合(`--paint-snapshot` では写らない)
