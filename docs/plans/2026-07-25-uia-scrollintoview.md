# UIA `ScrollIntoView` 未実装解消 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** UIA `ITextRangeProvider.ScrollIntoView` の no-op を解消し、あわせて
`ITextProvider.GetVisibleRanges` が文書全体を「可視」と申告している不正確さを直す。

**Architecture:** `IUiaTextHost` にスクロール系 2 メンバを追加し、`TextRangeProviderV2` /
`TextProviderImplV2` は host への純委譲にする。判断は `EditorControl` に集約する。
RPC スレッドから UI スレッドへのマーシャリングは既存 2 経路の使い分けを踏襲する
(書き込み = `BeginInvoke` / UI スレッド専用状態の読み取り = 同期 `Invoke`)。

**Tech Stack:** .NET WinForms / xUnit / UIA (System.Windows.Automation.Provider) /
CSharpier (pre-commit) / Husky.Net

**設計書:** `docs/plans/2026-07-25-uia-scrollintoview-design.md`(commit `cdb8dae`)

---

## 前提知識(この計画を実行する人向け)

### プロジェクト規範(必読)

`CLAUDE.md` を読むこと。特に効いてくるのは次の 4 点。

1. **a11y 鉄則** — UIA プロバイダは RPC スレッドから呼ばれる。RPC スレッドから
   エディタ内部(UI スレッド専有の `_topLine` / `_metrics` / `ClientSize` 等)に触らない。
2. **SR の実発声は自動テストで検証できない。** 本計画のテストは「UIA 応答と
   `TopLine` の遷移」までしか固定しない。実際に読み上がるかは L5(実機 NVDA)でのみ分かる。
3. **0 warning を維持**(`-warnaserror` 稼働中)。
4. **pre-commit フックを `--no-verify` で飛ばさない**(CSharpier 整形が走る)。

### コマンド

```bash
# ビルド(0 warning でないと失敗する)
dotnet build yEdit.sln -c Release -warnaserror

# 層ごとのテスト
dotnet test tests/yEdit.Core.Tests   -c Release --no-build
dotnet test tests/yEdit.Editor.Tests -c Release --no-build

# 単一テストだけ走らせる
dotnet test tests/yEdit.Editor.Tests -c Release --filter "FullyQualifiedName~UiaScrollIntoViewTests"

# マージ前の品質ゲート(Task 4 で使う)
pwsh tools/pre-merge-check.ps1     # pwsh が無ければ powershell -File tools/pre-merge-check.ps1
```

### 命名の落とし穴(重要)

`EditorControl` は `IUiaTextHost` を **explicit interface implementation** で実装しており、
その実体は `UiaTextHostAdapter` へ委譲される。Adapter は逆に `_host`(= `EditorControl`)の
**public / internal メソッド**を呼び返す。

したがって **インターフェースのメンバ名と `EditorControl` 側の実処理メソッド名を同じにしてはいけない。**
同名にすると `_host.Xxx(...)` がどちらに解決されるか読み手に分からなくなる
(コンパイルは通るが、explicit 実装は具象型経由では見えないため public 側に解決される)。

既存コードはこの規則を守っている。本計画も踏襲する。

| インターフェース側 | `EditorControl` 側の実処理 |
|---|---|
| `IUiaTextHost.SetSelection` | `EditorControl.SetSelectionCharRange`(既存) |
| `IUiaTextHost.GetBoundingRectangles` | `EditorControl.ComputeCaretPointForUia`(既存) |
| **`IUiaTextHost.ScrollRangeIntoView`** | **`EditorControl.ScrollCharRangeIntoView`**(新規) |
| **`IUiaTextHost.GetVisibleCharRange`** | **`EditorControl.GetVisibleCharRangeForUia`**(新規) |

### `IUiaTextHost` の実装クラス(メンバを足すと全部直す必要がある)

| 場所 | 型 |
|---|---|
| `src/yEdit.Editor/EditorControl.Uia.cs` | `EditorControl`(explicit・`_uia` への薄い委譲) |
| `src/yEdit.Editor/UiaTextHostAdapter.cs` | `UiaTextHostAdapter`(本体) |
| `tests/yEdit.Core.Tests/Accessibility/IUiaTextHostContractStubTests.cs:50` 付近 | `StubHost` |
| `tests/yEdit.Core.Tests/Accessibility/TextControlProviderV2Tests.cs:52` 付近 | `StubHost` |
| `tests/yEdit.Core.Tests/Accessibility/TextProviderImplV2Tests.cs:57` 付近 | `Host` |
| `tests/yEdit.Core.Tests/Accessibility/TextRangeProviderV2Tests.cs:107` 付近 | `InMemoryHost` |
| `tests/yEdit.Core.Tests/Accessibility/TextRangeProviderV2Tests.cs:462` 付近 | `LargeSyntheticHost` |

行番号は各クラスの `public void SetFocus() { }` の位置。**その直後に追記する**のが一番安全。

### WinForms テストの書き方(このリポジトリの流儀)

`tests/yEdit.Editor.Tests/` のテストは STA スレッドで走らせる必要がある。
`Sta.Run(() => { ... })` で包む。`GlobalUsings.cs` により `System` / `System.Windows.Forms` /
`Xunit` / `yEdit.Core.Buffers` / `yEdit.Editor` は using 不要。

参考にすべき既存ファイル: `tests/yEdit.Editor.Tests/CaretScrollTests.cs`

---

## Task 1a: `ScrollIntoView` を host へ委譲する(配線)

このタスクではスクロールの中身は実装しない。**RPC → UI の配線だけ**を通し、
`EditorControl.ScrollCharRangeIntoView` は意図的に空のままにする。
中身は Task 1b で TDD で入れる。

**Files:**
- Modify: `src/yEdit.Accessibility/IUiaTextHost.cs`
- Modify: `src/yEdit.Accessibility/TextRangeProviderV2.cs:285-291`
- Modify: `src/yEdit.Editor/UiaTextHostAdapter.cs`
- Modify: `src/yEdit.Editor/EditorControl.Uia.cs`
- Modify: `src/yEdit.Editor/EditorControl.Caret.cs`(末尾)
- Modify: `tests/yEdit.Core.Tests/Accessibility/*.cs`(stub 5 箇所)
- Test: `tests/yEdit.Core.Tests/Accessibility/TextRangeProviderV2Tests.cs`

### Step 1: `IUiaTextHost` にメンバを宣言する

`src/yEdit.Accessibility/IUiaTextHost.cs` の「座標」節の直後、「属性」節の**前**に
新しい節を挿入する(`OffsetFromScreenPoint` の宣言の後)。

```csharp
    // ---------- スクロール ----------

    /// <summary>
    /// [start, end) を可視域へスクロールする(実装は UI スレッドへマーシャリング)。
    /// <paramref name="alignToTop"/> が true なら範囲**先頭**を、false なら範囲**末尾**を
    /// 対象にする(UIA <c>ITextRangeProvider.ScrollIntoView</c> の意味論)。
    /// 選択・キャレットは変更しない(装飾スクロール)。
    /// 対象が既に可視なら何もしない=SR がテキストを歩くたびに画面が飛ぶのを防ぐ。
    /// </summary>
    void ScrollRangeIntoView(int start, int end, bool alignToTop);
```

また、クラス冒頭の `<summary>` にあるスレッド説明を更新する。

変更前:

```csharp
/// スレッド: RPC スレッドから呼ばれ得る。実装側は不変スナップショット参照 +
/// キャッシュ値で応答すること(<see cref="SetSelection"/> / <see cref="SetFocus"/> のみ UI マーシャリング)。
```

変更後:

```csharp
/// スレッド: RPC スレッドから呼ばれ得る。実装側は不変スナップショット参照 +
/// キャッシュ値で応答すること(<see cref="SetSelection"/> / <see cref="SetFocus"/> /
/// <see cref="ScrollRangeIntoView"/> のみ UI マーシャリング)。
```

### Step 2: テスト stub 5 箇所に実装を追加する

各 stub の `public void SetFocus() { }` の**直後**に追記する。

`TextRangeProviderV2Tests.cs` の `InMemoryHost` **だけ**は引数を記録する
(Step 5 の委譲検証で使う)。

```csharp
        // Task 1a: ScrollIntoView の委譲検証用に引数を記録する。
        public (int Start, int End, bool AlignToTop)? LastScroll { get; private set; }

        public void ScrollRangeIntoView(int start, int end, bool alignToTop) =>
            LastScroll = (start, end, alignToTop);
```

残り 4 箇所(`IUiaTextHostContractStubTests.StubHost` /
`TextControlProviderV2Tests.StubHost` / `TextProviderImplV2Tests.Host` /
`TextRangeProviderV2Tests.LargeSyntheticHost`)は空実装で良い。

```csharp
        public void ScrollRangeIntoView(int start, int end, bool alignToTop) { }
```

### Step 3: `EditorControl` に空の実処理メソッドを足す

`src/yEdit.Editor/EditorControl.Caret.cs` の `EnsureVisibleCharRange` の直後
(ファイル末尾の閉じ括弧の直前)に追加する。

**この時点では中身を書かない。** Task 1b で TDD で入れる。

```csharp
    /// <summary>
    /// UIA <c>ITextRangeProvider.ScrollIntoView</c> の実処理(UI スレッド専用)。
    /// <paramref name="alignToTop"/> が true なら範囲先頭を、false なら範囲末尾を可視域へ入れる。
    /// 選択・キャレットは変更しない。<c>SetSource</c> 前は no-op。
    /// 典拠: docs/plans/2026-07-25-uia-scrollintoview-design.md §5.1。
    /// </summary>
    public void ScrollCharRangeIntoView(int start, int end, bool alignToTop)
    {
        if (_buffer is null)
            return;
        // Task 1b で実装する(この時点では配線のみ)。
    }
```

> **注意**: 未使用引数の警告が出る場合は Task 1b まで一時的に
> `_ = start; _ = end; _ = alignToTop;` を置くのではなく、**Task 1a と 1b を続けて実施**して
> ビルド確認は Step 4 の 1 回で済ませてよい。0 warning を壊さないことを優先する。

### Step 4: `UiaTextHostAdapter` にマーシャリングを実装する

`src/yEdit.Editor/UiaTextHostAdapter.cs` の `void IUiaTextHost.SetFocus()` の**直前**
(= `AutomationId` の後)に追加する。既存 `SetSelection` と同形にする。

```csharp
    void IUiaTextHost.ScrollRangeIntoView(int start, int end, bool alignToTop)
    {
        // SetSelection と同形 (P5 Task 14 I-3): 破棄後 / Handle 未生成での BeginInvoke による
        // InvalidOperationException を防ぐ。
        if (_host.IsDisposed || !_host.IsHandleCreated)
            return;
        if (_host.InvokeRequired)
        {
            // 書き込み系は fire-and-forget (RPC スレッドを待たせない=deadlock 回避)。
            // WinForms の invoke キューは FIFO のため、SR が直後に呼ぶ同期 Invoke 系
            // (GetBoundingRectangles 等) はこのスクロールの後に走る。
            _host.BeginInvoke(
                new Action(() => ((IUiaTextHost)this).ScrollRangeIntoView(start, end, alignToTop))
            );
            return;
        }
        _host.ScrollCharRangeIntoView(start, end, alignToTop);
    }
```

### Step 5: `EditorControl.Uia.cs` に explicit 委譲を追加する

`void IUiaTextHost.SetFocus() => ((IUiaTextHost)_uia).SetFocus();` の**直前**に追加する。

```csharp
    void IUiaTextHost.ScrollRangeIntoView(int start, int end, bool alignToTop) =>
        ((IUiaTextHost)_uia).ScrollRangeIntoView(start, end, alignToTop);
```

### Step 6: ビルドが通ることを確認する

Run: `dotnet build yEdit.sln -c Release -warnaserror`

Expected: 成功・0 warning。
この時点で挙動は**まだ no-op**(`TextRangeProviderV2.ScrollIntoView` が未変更のため)。

### Step 7: L1 の失敗テストを書く

`tests/yEdit.Core.Tests/Accessibility/TextRangeProviderV2Tests.cs` を編集する。

まず `MakeProvider` を host も取り出せる形にリファクタする(既存 `MakeProvider` の呼び出し側は不変)。

変更前:

```csharp
    private static TextProviderImplV2 MakeProvider(string text)
    {
        var host = new InMemoryHost(text);
        var root = new TextControlProviderV2(host);
        return new TextProviderImplV2(host, root);
    }
```

変更後:

```csharp
    private static (InMemoryHost Host, TextProviderImplV2 Provider) MakeProviderWithHost(string text)
    {
        var host = new InMemoryHost(text);
        var root = new TextControlProviderV2(host);
        return (host, new TextProviderImplV2(host, root));
    }

    private static TextProviderImplV2 MakeProvider(string text) => MakeProviderWithHost(text).Provider;
```

次にテストをファイル末尾(クラスの閉じ括弧の直前)へ追加する。

```csharp
    [Fact]
    public void ScrollIntoView_DelegatesRangeAndAlignToTop_ToHost()
    {
        var (host, p) = MakeProviderWithHost("line0\nline1\nline2\nline3");
        var r = new TextRangeProviderV2(p, 6, 11); // "line1"

        r.ScrollIntoView(alignToTop: true);

        Assert.Equal((6, 11, true), host.LastScroll);
    }

    [Fact]
    public void ScrollIntoView_PassesAlignToTopFalse_Unchanged()
    {
        var (host, p) = MakeProviderWithHost("line0\nline1\nline2\nline3");
        var r = new TextRangeProviderV2(p, 6, 11);

        r.ScrollIntoView(alignToTop: false);

        Assert.Equal((6, 11, false), host.LastScroll);
    }

    [Fact]
    public void ScrollIntoView_DelegatesDegenerateRange_ToHost()
    {
        // 縮退範囲 (start == end) でも委譲される。SR のレビューカーソルは
        // 縮退範囲を歩くため、ここが落ちると実機で効かない。
        var (host, p) = MakeProviderWithHost("line0\nline1");
        var r = new TextRangeProviderV2(p, 3, 3);

        r.ScrollIntoView(alignToTop: true);

        Assert.Equal((3, 3, true), host.LastScroll);
    }
```

### Step 8: RED を確認する

Run:

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~ScrollIntoView_"
```

Expected: 3 件とも FAIL。`host.LastScroll` が `null` のまま
(`ScrollIntoView` がまだ no-op のため)。

### Step 9: `TextRangeProviderV2.ScrollIntoView` を委譲に変更する

`src/yEdit.Accessibility/TextRangeProviderV2.cs:285-291` を丸ごと置き換える。

変更前:

```csharp
    /// <summary>未実装(申し送り)。v1 挙動を踏襲してスクロールしない。UIA 対応 SR が
    /// レビューカーソルで画面外テキストを読むときの実害を実機で確認してから実装可否を判断する。
    /// 典拠: docs/plans/2026-07-25-sr-legacy-cleanup-design.md §7 案 C。</summary>
    public void ScrollIntoView(
        bool alignToTop
    ) { /* 未実装(申し送り): 上記 summary 参照 */
    }
```

変更後:

```csharp
    /// <summary>
    /// 範囲を可視域へスクロールする。<paramref name="alignToTop"/> が true なら範囲先頭を、
    /// false なら範囲末尾を対象にする。判断(可視判定・整列・クランプ)は host 側に集約し、
    /// ここは純委譲に留める。選択・キャレットは変更しない。
    /// 典拠: docs/plans/2026-07-25-uia-scrollintoview-design.md §5.1。
    /// </summary>
    public void ScrollIntoView(bool alignToTop) =>
        _owner.Host.ScrollRangeIntoView(_start, _end, alignToTop);
```

### Step 10: GREEN を確認する

Run:

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~ScrollIntoView_"
```

Expected: 3 件とも PASS。

### Step 11: commit

```bash
git add src/yEdit.Accessibility src/yEdit.Editor tests/yEdit.Core.Tests
git commit -m "feat(a11y): UIA ScrollIntoView を host へ委譲する配線を通す

TextRangeProviderV2.ScrollIntoView の no-op を解消する第 1 段。
IUiaTextHost に ScrollRangeIntoView を追加し、RPC スレッドから
UI スレッドへの BeginInvoke マーシャリングまでを通す。
EditorControl 側のスクロール本体は次 commit で実装する。

設計書: docs/plans/2026-07-25-uia-scrollintoview-design.md §5.1"
```

---

## Task 1b: スクロール本体(整列ロジック)

**Files:**
- Modify: `src/yEdit.Editor/EditorControl.Caret.cs`(Task 1a Step 3 で置いた空実装)
- Create: `tests/yEdit.Editor.Tests/UiaScrollIntoViewTests.cs`

### Step 1: 失敗するテストを書く

`tests/yEdit.Editor.Tests/UiaScrollIntoViewTests.cs` を新規作成する。

```csharp
using System.Linq;
using yEdit.Accessibility;

namespace yEdit.Editor.Tests;

/// <summary>
/// UIA ScrollIntoView(= EditorControl.ScrollCharRangeIntoView)の契約テスト。
///
/// 契約:
/// - 対象行(alignToTop なら範囲先頭・そうでなければ範囲末尾)が既に可視なら TopLine を動かさない
/// - 非可視なら alignToTop に従って TopLine を上端 / 下端に合わせる
/// - 選択・キャレットは変更しない(装飾スクロール)
/// - SetSource 前 / Handle 未生成 / 破棄後は no-op(throw しない)
///
/// LineHeightPx はフォント依存のため、可視行数はテスト側でも
/// ClientSize.Height / LineHeightPx で算出して相対比較する
/// (CaretScrollTests と同じ流儀)。長い行を置かないので hscroll は表示されず、
/// 実装側の paintHeight == ClientSize.Height になる。
/// </summary>
public class UiaScrollIntoViewTests
{
    private const int LineCount = 30;

    private static (Form f, EditorControl c) MakeControl(string text, int width, int height)
    {
        var f = new Form { Size = new System.Drawing.Size(width, height) };
        var c = new EditorControl { Dock = DockStyle.Fill };
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    private static string MakeText() =>
        string.Join("\n", Enumerable.Range(0, LineCount).Select(i => $"line{i}"));

    private static int LineStartOffset(string text, int line)
    {
        int pos = 0;
        for (int i = 0; i < line; i++)
            pos = text.IndexOf('\n', pos) + 1;
        return pos;
    }

    private static int VisibleRows(EditorControl c) =>
        Math.Max(1, c.ClientSize.Height / Math.Max(1, c.LineHeightPx));

    [Fact]
    public void ScrollCharRangeIntoView_KeepsTopLine_WhenTargetAlreadyVisible() =>
        Sta.Run(() =>
        {
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                // 非既定位置から開始する(既定 0 のままだと「動かなかった」と
                // 「そもそも 0 だった」を区別できない = CLAUDE.md §4 の教訓)。
                c.TopLine = 5;
                int target = 5 + VisibleRows(c) / 2; // 可視域の中ほど
                int off = LineStartOffset(text, target);

                c.ScrollCharRangeIntoView(off, off, alignToTop: true);
                Assert.Equal(5, c.TopLine);

                c.ScrollCharRangeIntoView(off, off, alignToTop: false);
                Assert.Equal(5, c.TopLine);
            }
        });

    [Fact]
    public void ScrollCharRangeIntoView_AlignsToTop_WhenTargetAboveViewport() =>
        Sta.Run(() =>
        {
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                c.TopLine = 20;
                int off = LineStartOffset(text, 3);

                c.ScrollCharRangeIntoView(off, off, alignToTop: true);

                Assert.Equal(3, c.TopLine);
            }
        });

    [Fact]
    public void ScrollCharRangeIntoView_AlignsToTop_WhenTargetBelowViewport() =>
        Sta.Run(() =>
        {
            // 案 1(EnsureVisibleCharRange への薄い委譲)との差分をここで固定する。
            // 案 1 だと対象行は「最下行」に来るため TopLine == 25 にはならない。
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                c.TopLine = 0;
                int off = LineStartOffset(text, 25);

                c.ScrollCharRangeIntoView(off, off, alignToTop: true);

                Assert.Equal(25, c.TopLine);
            }
        });

    [Fact]
    public void ScrollCharRangeIntoView_AlignsToBottom_WhenAlignToTopIsFalse() =>
        Sta.Run(() =>
        {
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                c.TopLine = 0;
                int expected = 25 - VisibleRows(c) + 1;
                int off = LineStartOffset(text, 25);

                c.ScrollCharRangeIntoView(off, off, alignToTop: false);

                Assert.Equal(expected, c.TopLine);
            }
        });

    [Fact]
    public void ScrollCharRangeIntoView_UsesRangeStartOrEnd_PerAlignToTop() =>
        Sta.Run(() =>
        {
            // start は可視域内・end は遥か下。alignToTop の値で対象端点が切り替わることを固定する
            // (両方 start を見る / 両方 end を見る実装をここで殺す)。
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                int start = LineStartOffset(text, 1);
                int end = LineStartOffset(text, 25);

                c.TopLine = 0;
                c.ScrollCharRangeIntoView(start, end, alignToTop: false);
                Assert.Equal(25 - VisibleRows(c) + 1, c.TopLine); // end を見た

                c.TopLine = 0;
                c.ScrollCharRangeIntoView(start, end, alignToTop: true);
                Assert.Equal(0, c.TopLine); // start(行 1)は既に可視 → 動かない
            }
        });

    [Fact]
    public void ScrollCharRangeIntoView_PreservesCaretAndAnchor() =>
        Sta.Run(() =>
        {
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                c.TopLine = 0;
                c.SetSelectionCharRange(2, 5);
                var (start0, end0) = c.GetSelectionCharRange();

                int off = LineStartOffset(text, 25);
                c.ScrollCharRangeIntoView(off, off, alignToTop: false);

                var (start1, end1) = c.GetSelectionCharRange();
                Assert.Equal(start0, start1);
                Assert.Equal(end0, end1);
                Assert.True(c.TopLine > 0, "スクロールしていないと本テストは無意味");
            }
        });

    [Fact]
    public void ScrollCharRangeIntoView_NoOp_BeforeSetSource() =>
        Sta.Run(() =>
        {
            using var f = new Form();
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;

            Assert.Null(Record.Exception(() => c.ScrollCharRangeIntoView(0, 10, alignToTop: true)));
            Assert.Equal(0, c.TopLine);
        });

    [Fact]
    public void ScrollRangeIntoView_NoThrow_WhenHandleNotCreated() =>
        Sta.Run(() =>
        {
            using var c = new EditorControl(); // Handle 未生成
            Assert.Null(
                Record.Exception(() => ((IUiaTextHost)c).ScrollRangeIntoView(0, 5, alignToTop: true))
            );
        });

    [Fact]
    public void ScrollRangeIntoView_NoThrow_AfterDispose() =>
        Sta.Run(() =>
        {
            var f = new Form { Size = new System.Drawing.Size(400, 120) };
            var c = new EditorControl { Dock = DockStyle.Fill };
            f.Controls.Add(c);
            _ = f.Handle;
            c.SetSource(TextBuffer.FromString(MakeText()));
            c.Dispose();
            f.Dispose();

            Assert.Null(
                Record.Exception(() => ((IUiaTextHost)c).ScrollRangeIntoView(0, 5, alignToTop: true))
            );
        });
}
```

### Step 2: RED を確認する

Run:

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Editor.Tests -c Release --no-build --filter "FullyQualifiedName~UiaScrollIntoViewTests"
```

Expected: 整列系 4 件(`AlignsToTop_WhenTargetAboveViewport` /
`AlignsToTop_WhenTargetBelowViewport` / `AlignsToBottom_WhenAlignToTopIsFalse` /
`UsesRangeStartOrEnd_PerAlignToTop` の後半)と `PreservesCaretAndAnchor` が FAIL
(`TopLine` が動かない)。no-op 系・no-throw 系は PASS。

**RED の内訳をメモしておくこと。** GREEN 後に「本当に実装が効いたのか」を判断する材料になる。

### Step 3: 本体を実装する

`src/yEdit.Editor/EditorControl.Caret.cs` の `ScrollCharRangeIntoView` を実装する。

```csharp
    /// <summary>
    /// UIA <c>ITextRangeProvider.ScrollIntoView</c> の実処理(UI スレッド専用)。
    /// <paramref name="alignToTop"/> が true なら範囲先頭を、false なら範囲末尾を可視域へ入れる。
    /// 選択・キャレットは変更しない。<c>SetSource</c> 前は no-op。
    /// 典拠: docs/plans/2026-07-25-uia-scrollintoview-design.md §5.1。
    /// </summary>
    /// <remarks>
    /// **既に可視なら垂直方向は動かさない。** UIA 仕様の文言だけを読むと「常に整列させる」
    /// 実装もあり得るが、SR がテキストを歩くたびに画面が飛び、晴眼・弱視ユーザーに実害が出る
    /// (CLAUDE.md §2「晴眼・弱視ユーザーも第一級」)。
    ///
    /// <paramref name="start"/> / <paramref name="end"/> は UIA 範囲の端点だが、
    /// 呼び出し元 (<c>TextRangeProviderV2</c>) が範囲を作った後にバッファが縮むと stale に
    /// なり得るため、ここで <see cref="SnapAndClamp"/> を通す(二重防御)。
    ///
    /// <c>visibleRows</c> を論理行数として扱うのは折り返し ON では近似だが、
    /// <see cref="BringCaretIntoView"/> と同じ流儀を意図的に踏襲している。
    /// ここだけ別計算にすると 2 つの可視判定が食い違う。
    ///
    /// 水平方向と「対象行が可視域に入りきらない」端数の処理は
    /// <see cref="EnsureVisibleCharRange"/> に委ねる(caret / anchor の保存・復元も同メソッドが行う)。
    /// </remarks>
    public void ScrollCharRangeIntoView(int start, int end, bool alignToTop)
    {
        if (_buffer is null)
            return;
        var snap = _buffer.Current;
        int target = SnapAndClamp(alignToTop ? start : end);
        int line = snap.GetLineIndexOfChar(target);

        int paintHeight = Math.Max(0, ClientSize.Height - (_hscroll.Visible ? _hscroll.Height : 0));
        int visibleRows = Math.Max(1, paintHeight / Math.Max(1, _metrics.LineHeightPx));

        // 既に可視なら垂直は動かさない(視界の揺れ防止)
        if (line < _topLine || line >= _topLine + visibleRows)
            TopLine = alignToTop ? line : line - visibleRows + 1;

        // 水平 + 保険。caret / anchor は EnsureVisibleCharRange が try/finally で復元する
        EnsureVisibleCharRange(target, 0);
    }
```

### Step 4: GREEN を確認する

Run:

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Editor.Tests -c Release --no-build --filter "FullyQualifiedName~UiaScrollIntoViewTests"
```

Expected: 9 件すべて PASS。

### Step 5: 層全体の回帰を確認する

Run:

```bash
dotnet test tests/yEdit.Core.Tests   -c Release --no-build
dotnet test tests/yEdit.Editor.Tests -c Release --no-build
```

Expected: 全緑。特に `CaretScrollTests` が全緑であること
(`EnsureVisibleCharRange` / `TopLine` setter を再利用しているため)。

### Step 6: commit

```bash
git add src/yEdit.Editor tests/yEdit.Editor.Tests
git commit -m "feat(a11y): ScrollIntoView のスクロール本体を実装する

対象行(alignToTop なら範囲先頭・そうでなければ範囲末尾)が既に可視なら
垂直方向は動かさず、非可視のときだけ alignToTop に従って TopLine を
上端 / 下端へ合わせる。水平方向と caret/anchor の保存復元は既存の
EnsureVisibleCharRange に委ねる。

「既に可視なら動かさない」は意図的な仕様。常に整列させると SR がテキストを
歩くたびに画面が飛び、晴眼・弱視ユーザーに実害が出る(CLAUDE.md §2)。

設計書: docs/plans/2026-07-25-uia-scrollintoview-design.md §5.1"
```

### Step 7: レビューを受ける(前倒しコード品質レビュー)

Task 1a + 1b は RPC → UI の**新しい書き込み seam** を導入し、Task 2 もこの形を踏襲する。
CLAUDE.md §3 の前倒し例外(後続タスクが依存する新しい抽象・seam の導入)に該当する。

**別エージェント**に次の観点でコード品質レビューを依頼する:

- `_host.ScrollCharRangeIntoView` の名前分離が意図どおり機能しているか(無限再帰の余地がないか)
- `BeginInvoke` の fire-and-forget が既存 `SetSelection` と本当に同形か
- `EnsureVisibleCharRange(target, 0)` の再利用が `TopLine` を二重に動かさないか
- テストが実装の写経になっていないか(実装を変異させたら赤くなるか)

指摘は 3 択で明示する: ① fixup commit で修正 / ② PR description に記載して受容 / ③ 理由付き却下。

---

## Task 2: `GetVisibleRanges` を実可視域にする

**このタスクは単独で revert できるように、独立した commit にする**(設計書 §5.3)。
NVDA が `GetVisibleRanges` を通し読みの範囲決定に使っていた場合、読み範囲が縮む恐れがあり、
それは L5 でしか分からないため。

**Files:**
- Modify: `src/yEdit.Accessibility/IUiaTextHost.cs`
- Modify: `src/yEdit.Accessibility/TextProviderImplV2.cs:26-27`
- Modify: `src/yEdit.Editor/UiaTextHostAdapter.cs`
- Modify: `src/yEdit.Editor/EditorControl.Uia.cs`
- Modify: `src/yEdit.Editor/EditorControl.cs`(`HasFocusCached` の直後)
- Modify: `tests/yEdit.Core.Tests/Accessibility/*.cs`(stub 5 箇所)
- Test: `tests/yEdit.Core.Tests/Accessibility/TextProviderImplV2Tests.cs`
- Test: `tests/yEdit.Editor.Tests/UiaScrollIntoViewTests.cs`(追記)

### Step 1: `IUiaTextHost` にメンバを宣言する

Task 1a Step 1 で作った「スクロール」節を「スクロール / 可視範囲」に改名し、
`ScrollRangeIntoView` の後に追加する。

```csharp
    /// <summary>
    /// 現在ビューポートに見えている本文の範囲 [Start, End)。
    /// 末尾行の改行は含めない(<see cref="LineEndNoBreakOf"/> と同じ流儀)。
    /// 水平スクロールで横に隠れている部分は「可視」に含める(行単位で報告する)。
    /// 実装は UI スレッド専用状態を要するため同期マーシャリングする。
    /// バッファ未設定 / Handle 未生成では (0, 0)。
    /// </summary>
    (int Start, int End) GetVisibleCharRange();
```

### Step 2: テスト stub 5 箇所に実装を追加する

`TextProviderImplV2Tests.Host` **だけ**は差し替え可能にする(Step 5 の検証で使う)。

```csharp
        // Task 2: GetVisibleRanges の委譲検証用。既定は「全体可視」ではない値にしておく。
        public (int Start, int End) VisibleRange { get; set; } = (0, 100);

        public (int Start, int End) GetVisibleCharRange() => VisibleRange;
```

残り 4 箇所は固定値で良い。

```csharp
        public (int Start, int End) GetVisibleCharRange() => (0, TextLength);
```

> `InMemoryHost` は `TextLength` プロパティを持つのでそのまま書ける。
> `LargeSyntheticHost` も `TextLength` を持つ。`IUiaTextHostContractStubTests.StubHost` /
> `TextControlProviderV2Tests.StubHost` は各自の `TextLength` に合わせること。

### Step 3: `EditorControl` に実処理メソッドを足す

`src/yEdit.Editor/EditorControl.cs` の `internal bool HasFocusCached => _hasFocus;` の直後に追加する
(UIA 用 accessor が並んでいる区画)。

```csharp
    /// <summary>
    /// UIA <c>ITextProvider.GetVisibleRanges</c> の実処理(UI スレッド専用)。
    /// 現在ビューポートに見えている本文の範囲 [Start, End) を返す。
    /// </summary>
    /// <remarks>
    /// 描画 (<c>EditorControl.Paint.cs</c>) と**同じ** <see cref="ViewportLayout.Build"/> を使う。
    /// 「見えている行」の定義を二重化しないことが本メソッドの要点。
    /// 折り返し ON では視覚行境界になる。末尾行の改行は含めない。
    /// バッファ未設定・可視行ゼロでは (0, 0)。
    /// 典拠: docs/plans/2026-07-25-uia-scrollintoview-design.md §5.2。
    /// </remarks>
    internal (int Start, int End) GetVisibleCharRangeForUia()
    {
        if (_buffer is null)
            return (0, 0);
        var snap = _buffer.Current;
        int paintHeight = Math.Max(0, ClientSize.Height - (_hscroll.Visible ? _hscroll.Height : 0));
        var rows = ViewportLayout.Build(snap, _topLine, paintHeight, _wrapColumns, _metrics);
        if (rows.Count == 0)
            return (0, 0);
        var first = rows[0];
        var last = rows[rows.Count - 1];
        return (first.SegmentStartChar, last.SegmentStartChar + last.SegmentLength);
    }
```

> `ViewportLayout` は `yEdit.Core.Layout` の `internal` 型で、`yEdit.Core.csproj` の
> `InternalsVisibleTo yEdit.Editor` により参照できる。`EditorControl.cs` に
> `using yEdit.Core.Layout;` が無ければ追加する(`EditorControl.Paint.cs` を参照)。

### Step 4: Adapter と explicit 委譲を追加する

`src/yEdit.Editor/UiaTextHostAdapter.cs`、`IUiaTextHost.ScrollRangeIntoView` の直後に追加する。

**書き込み系と違い同期 `Invoke` を使う**(戻り値が要るため)。
`TryFindVisualSegment` と同形の race ガードを付ける。

```csharp
    (int Start, int End) IUiaTextHost.GetVisibleCharRange()
    {
        // UI スレッド専用状態 (_topLine / _metrics / ClientSize) を要する読み取りのため、
        // GetBoundingRectangles / TryFindVisualSegment と同形で同期 Invoke する。
        if (!_host.IsHandleCreated)
            return (0, 0); // UI スレッドが束縛されていない
        if (_host.InvokeRequired)
        {
            try
            {
                return _host.Invoke(new Func<(int, int)>(() => _host.GetVisibleCharRangeForUia()));
            }
            catch (ObjectDisposedException)
            {
                return (0, 0);
            }
            catch (InvalidOperationException)
            {
                return (0, 0);
            } // Handle 破棄との race
        }
        return _host.GetVisibleCharRangeForUia();
    }
```

`src/yEdit.Editor/EditorControl.Uia.cs`、`IUiaTextHost.ScrollRangeIntoView` の委譲の直後:

```csharp
    (int Start, int End) IUiaTextHost.GetVisibleCharRange() =>
        ((IUiaTextHost)_uia).GetVisibleCharRange();
```

### Step 5: L1 の失敗テストを書く

`tests/yEdit.Core.Tests/Accessibility/TextProviderImplV2Tests.cs` のクラス末尾に追加する。

```csharp
    [Fact]
    public void GetVisibleRanges_UsesHostVisibleCharRange()
    {
        // 旧実装は常に (0, TextLength) = (0, 100) を返していた。
        // 非既定の可視域を与えて、旧実装と区別できる形で固定する。
        var h = new Host { VisibleRange = (10, 25) };
        var root = new TextControlProviderV2(h);
        var pi = new TextProviderImplV2(h, root);

        var ranges = pi.GetVisibleRanges();

        Assert.Single(ranges);
        var r = (TextRangeProviderV2)ranges[0];
        var probe = new TextRangeProviderV2(pi, 10, 25);
        Assert.Equal(
            0,
            r.CompareEndpoints(
                System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,
                probe,
                System.Windows.Automation.Text.TextPatternRangeEndpoint.Start
            )
        );
        Assert.Equal(
            0,
            r.CompareEndpoints(
                System.Windows.Automation.Text.TextPatternRangeEndpoint.End,
                probe,
                System.Windows.Automation.Text.TextPatternRangeEndpoint.End
            )
        );
    }
```

### Step 6: RED を確認する

Run:

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~GetVisibleRanges_"
```

Expected: FAIL。旧実装が `(0, 100)` を返すため End 側の比較で落ちる。

### Step 7: `TextProviderImplV2.GetVisibleRanges` を委譲に変更する

`src/yEdit.Accessibility/TextProviderImplV2.cs:26-27` を置き換える。

変更前:

```csharp
    public ITextRangeProvider[] GetVisibleRanges() =>
        new ITextRangeProvider[] { new TextRangeProviderV2(this, 0, Host.TextLength) };
```

変更後:

```csharp
    /// <summary>
    /// 現在ビューポートに見えている範囲を 1 本返す(プレーンテキストエディタなので連続 1 範囲)。
    /// 旧実装は常に文書全体を返していたが、それは <c>GetBoundingRectangles</c> が画面外で
    /// 空配列を返す挙動と矛盾していた。典拠:
    /// docs/plans/2026-07-25-uia-scrollintoview-design.md §5.2。
    /// </summary>
    public ITextRangeProvider[] GetVisibleRanges()
    {
        var (s, e) = Host.GetVisibleCharRange();
        return new ITextRangeProvider[] { new TextRangeProviderV2(this, s, e) };
    }
```

### Step 8: GREEN を確認する

Run:

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~GetVisibleRanges_"
```

Expected: PASS。

### Step 9: L2 のテストを追加する

`tests/yEdit.Editor.Tests/UiaScrollIntoViewTests.cs` のクラス末尾に追加する。

```csharp
    [Fact]
    public void GetVisibleCharRangeForUia_FollowsTopLine() =>
        Sta.Run(() =>
        {
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                c.TopLine = 0;
                var (s0, e0) = c.GetVisibleCharRangeForUia();

                c.TopLine = 10;
                var (s1, e1) = c.GetVisibleCharRangeForUia();

                Assert.Equal(LineStartOffset(text, 10), s1);
                Assert.True(s1 > s0, $"expected {s1} > {s0}");
                Assert.True(e1 > e0, $"expected {e1} > {e0}");
            }
        });

    [Fact]
    public void GetVisibleCharRangeForUia_DoesNotReportWholeDocument_WhenScrolled() =>
        Sta.Run(() =>
        {
            // 旧 GetVisibleRanges は常に (0, TextLength) を返していた。その退行を殺す。
            var text = MakeText();
            var (f, c) = MakeControl(text, width: 400, height: 120);
            using (f)
            using (c)
            {
                c.TopLine = 10;
                var (s, e) = c.GetVisibleCharRangeForUia();

                Assert.True(s > 0, "可視域が文書先頭から始まっている");
                Assert.True(e < text.Length, $"可視域が文書末尾まで伸びている ({e} vs {text.Length})");
            }
        });

    [Fact]
    public void GetVisibleCharRangeForUia_EmptyDocument_ReturnsZeroZero() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("", width: 400, height: 120);
            using (f)
            using (c)
            {
                Assert.Equal((0, 0), c.GetVisibleCharRangeForUia());
            }
        });

    [Fact]
    public void GetVisibleCharRangeForUia_NoOp_BeforeSetSource() =>
        Sta.Run(() =>
        {
            using var f = new Form();
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;

            Assert.Equal((0, 0), c.GetVisibleCharRangeForUia());
        });

    [Fact]
    public void GetVisibleCharRange_ReturnsZeroZero_WhenHandleNotCreated() =>
        Sta.Run(() =>
        {
            using var c = new EditorControl(); // Handle 未生成
            Assert.Equal((0, 0), ((IUiaTextHost)c).GetVisibleCharRange());
        });
```

### Step 10: GREEN と層全体の回帰を確認する

Run:

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests   -c Release --no-build
dotnet test tests/yEdit.Editor.Tests -c Release --no-build
dotnet test tests/yEdit.App.Tests    -c Release --no-build
```

Expected: 全緑・0 warning。

### Step 11: commit

```bash
git add src/yEdit.Accessibility src/yEdit.Editor tests
git commit -m "feat(a11y): GetVisibleRanges を実際の可視域にする

旧実装は常に文書全体を「可視」と申告しており、画面外で空配列を返す
GetBoundingRectangles と矛盾していた。描画と同じ ViewportLayout.Build を
使って実際のビューポート範囲を返す。

NVDA が GetVisibleRanges を通し読みの範囲決定に使っていた場合に読み範囲が
縮むリスクがあるため、この変更だけを revert できるよう独立 commit にする。
L5 チェックリスト②(NVDA の通し読みが文書全体を読み切るか)で検証する。

設計書: docs/plans/2026-07-25-uia-scrollintoview-design.md §5.2 / §5.3"
```

### Step 12: 仕様レビューを受ける

**別エージェント**に、実装とテストが設計書 §5.2 の仕様どおりかを確認してもらう。

---

## Task 3: 最終ブランチレビュー(2 パス)

CLAUDE.md §3 工程 5。**パスごとに独立した別エージェントを起動する**
(1 起動に混載するとレビューが浅くなるため)。

### Step 1: コード品質パス

別エージェントに依頼する。ミューテーション検証のスポットチェックを含める。

| 変異 | 赤になるべきテスト |
|---|---|
| `if (line < _topLine \|\| line >= _topLine + visibleRows)` を常時 true に | `ScrollCharRangeIntoView_KeepsTopLine_WhenTargetAlreadyVisible` |
| `alignToTop ? line : line - visibleRows + 1` の三項を反転 | `AlignsToTop_WhenTargetBelowViewport` / `AlignsToBottom_WhenAlignToTopIsFalse` |
| `SnapAndClamp(alignToTop ? start : end)` の三項を反転 | `UsesRangeStartOrEnd_PerAlignToTop` |
| Adapter の `IsDisposed \|\| !IsHandleCreated` ガードを除去 | `ScrollRangeIntoView_NoThrow_AfterDispose` |

変異 → 対象テストが赤 → **復元**まで確認すること。

### Step 2: 脆弱性パス

別エージェントに依頼する。焦点は外部入力のパースやパス操作ではなく、
**RPC 境界を跨ぐ新しい書き込み経路**に置く。

- 悪意ある / 暴走した UIA クライアントが `ScrollIntoView` を高頻度で呼んだとき、
  `BeginInvoke` のキューが膨張して UI スレッドが飽和しないか
- `GetVisibleCharRange` の同期 `Invoke` が RPC スレッドを長時間ブロックし得ないか
  (`ViewportLayout.Build` は可視行数に比例 = O(数十行))
- `_start` / `_end` が stale なときのクランプ漏れ

**注記**: `BeginInvoke` の fire-and-forget という性質は既存の `SetSelection` / `SetFocus`
にも同じくあり、本作業で新規に生じるものではない。この点はレビュー結果に明記する。

### Step 3: 指摘への対応

指摘は 3 択で明示する: ① fixup commit で修正 / ② PR description に記載して受容 / ③ 理由付き却下。
**元 commit を書き換えず、別 fixup commit で積む**(CLAUDE.md §4)。

---

## Task 4: 品質ゲート → L5 → PR

### Step 1: ローカル品質ゲート

Run: `pwsh tools/pre-merge-check.ps1`(pwsh が無ければ `powershell -File tools/pre-merge-check.ps1`)

Expected: **EXIT 0**。

### Step 2: `tools/sr-regression.ps1` の実行可否を判断する

本作業は UIA 応答を**変更する**ため、回帰目的で意味を持つ(設計書 §6.4)。
ただし前ブランチ(PR #28 §8.4)の記録どおり、`pwsh` 未インストール環境では
`word-sim.ps1` が既知のエンコーディング問題で落ちる。

実行できない場合は**設計書 §8 に理由を記録**し、L5 で代替することを明記する。

### Step 3: L5 実機 SR 検証をユーザーに依頼する

**ここで一旦止まり、ユーザーに実機検証を依頼する。** 自動化しない(CLAUDE.md §5)。

| # | 手順 | 期待 |
|---|---|---|
| ① | NVDA で大きなファイルを開き、検索ジャンプ後にレビューカーソルを画面外へ動かす | 画面が追従する |
| ② | **NVDA の通し読み(NVDA+↓)** | **文書全体を読み切る**(Task 2 の退行チェック) |
| ③ | ナレーターのスキャンモード(Caps+↓)で末尾まで移動 | 追従する |
| ④ | 通常のキー入力で編集 | 不意のスクロールが起きない |
| ⑤ | CSV モード | 異常が出ない |
| ⑥ | (余力があれば)Windows 拡大鏡「テキストカーソルに従う」 | 追従する |

**② が NG だった場合**: Task 2 の commit だけを revert する(そのために独立 commit にしてある)。
Task 1a / 1b は据え置いて PR を出し、`GetVisibleRanges` は申し送りに戻す。

### Step 4: 設計書に実施記録を追記する

`docs/plans/2026-07-25-uia-scrollintoview-design.md` の §8 に、
実装時の精密化・レビュー指摘の反映・L5 結果・§6.4 からの逸脱を追記する
(CLAUDE.md §8: 日付付き文書は策定時スナップショット。**実施記録の追記のみ可**)。

### Step 5: PR を作成する

```bash
git push -u origin feature/uia-scrollintoview
gh pr create --base main --title "feat(a11y): UIA ScrollIntoView 未実装解消(PR #28 申し送り 案 C)"
```

PR description は**日本語**で、目的・レビュー経緯・申し送りを記載する(CLAUDE.md §7)。
L5 の結果を必ず含めること。

---

## 完了条件

1. L1 / L2 の新規テストが全緑。
2. `tools/pre-merge-check.ps1` が EXIT 0(0 warning 維持)。
3. L5 チェックリストが全項目 OK(特に ②)。
4. 別エージェントによる最終 2 パスレビュー完了・指摘の 3 択対応が済んでいる。
