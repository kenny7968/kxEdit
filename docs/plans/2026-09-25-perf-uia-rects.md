# フェーズ 2: UIA の座標と矩形(perf-uia-rects) 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development(または executing-plans)で、タスク単位に実装する。

**Goal:** UIA のスクリーン座標が古くなる問題(S-1)を解消し、複数行範囲の `GetBoundingRectangles` の O(範囲の行数 × 可視行数)(P-9)をなくす。

**設計書:** `docs/plans/2026-09-24-general-perf-improvements-design.md` §3・§7(以下「設計書」)
**調査記録:** `docs/plans/2026-09-24-general-perf-audit.md` §4 P-9・§6 S-1(以下「調査記録」)
**フェーズ 0 の計画:** `docs/plans/2026-09-24-perf-bench.md`(計測道具の使い方と現状値)

**Architecture:**
- S-1: 座標のキャッシュ(`_clientToScreenX/Y`・`_bounds`)を捨てる。UI スレッドで走る 2 経路(`ComputeBoundingRectangles` / `ComputeOffsetFromScreenPoint`)はその場で `PointToScreen` を呼ぶ。RPC スレッドから読む `BoundingRectangle` は、キャッシュ済みの `_hwnd` に対して Win32 の `GetClientRect` + `ClientToScreen` をその場で呼ぶ。
- P-9 (a)(b): `ComputeBoundingRectangles` の行ループを、TopLine の先頭から始め、TopLine より下で不可視になったら打ち切る。
- P-9 (c): `ComputeCaretPoint` の折り返し OFF で、TopLine からの積み上げループを `logicalLine - _topLine` で置き換える。

**Tech Stack:** C# / WinForms(.NET 9)、UIA provider(kxEdit.Accessibility)、xUnit、Smoke(kxEdit.Editor.Smoke)、PowerShell 7。

---

## 0. 前提と決定事項

### 0.1 計測の条件(設計書 §3.2・§5.5 の申し送り)

- Smoke `--perf` の **S8**(改善の対象)と、**S1・S2・S3**((c) が毎打鍵の `PositionCaret` に効くので副次効果を見る)で比べる。シナリオの集合は変更前後で `--scenario S1,S2,S3,S8` に固定する。
- 各 3 回、中央値の中央値で比べる。3 回の最小〜最大を揺れとして記録する。
- **NVDA の有無を揃える。** 変更前の計測時の状態を実施記録に書き、変更後も同じ状態で測る。
- 画面のロック・スクリーンセーバー中は測らない(Smoke の自己チェックが EXIT 1 になったら値を捨てる)。
- perf-harness の **M-7** は設計書 §7.3 の完了条件に入っている。十数分キーボードとマウスを占有し、`%APPDATA%\kxEdit` を退避するので、**実行前にユーザーの了承を取る**。了承が得られなければ Smoke の S8 だけで判定し、PR にその旨を書く。

### 0.2 設計書からの精密化

- **(c) の条件は `maxWidthPx <= 0`**(設計書は `_wrapColumns <= 0`)。`LineLayout` の Wrap 系は `maxWidthPx <= 0` のとき必ず 1 セグメントを返す(`LineLayout.cs:140-141`)。等価性の根拠がこの分岐そのものなので、条件もこれに揃える。`_wrapColumns <= 0` なら `MaxWrapWidthPx == 0` なので、設計書の条件を含む。
- **(c) の旧実装の参照は、テスト内に複製せず、製品コードに残る積み上げループを使う。** 積み上げループは折り返し ON のために製品コードに残る。`ComputeCaretPoint` の本体を `ComputeCaretPointCore(offset, allowNoWrapShortcut)` にして、テスト用の入口 `TestHook_ComputeCaretPointByAccumulation` から短絡なしで呼ぶ。テストに複製すると、private の状態(`_topSegment`・`PaintHeightPx`・`LocateSegmentIndex` など)を全部なぞる必要があり、なぞり損ねがそのまま偽の緑になるため。
- **(a) の前提**(adapter の `_bufferSnapshot` と host の `_buffer.Current` が同一)は、計画時に確認した。本文を差し替える経路(`SetSource` / `ReplaceSource` / `ConvertEols` / `AfterEdit` / `UndoEolConversion` = `EditorControl.cs:249/316/645/1655/1790`)は、すべて同じ同期処理の中で `_uia.OnSnapshotChanged` を呼び、その間にメッセージを汲まない。`ComputeBoundingRectangles` は Invoke(= UI スレッドがメッセージを汲んだとき)にしか走らないので、2 つは常に一致している。
- **(c) の判定式は、末尾の `y >= paintHeight` 判定と重なる。** 折り返し OFF では `y = n * lineHeight` なので、短絡の `(long)n * lineHeight >= paintHeight` は末尾の判定と同じ結論になる。短絡で return するのは、それ以降の計算(行番号幅の計測)を省くためである。変異検証(Task 4)では、この重なりのために「`>=` を `>` にする」変異は等価変異になる。
- **`BoundingRectangle` の値は、2 回の Win32 呼び出しの間で揃わないことがある。** 従来は lock 越しに、1 回で書いた値を返していた。新しい実装では、UI スレッドがリサイズ中なら「新しい原点と古い大きさ」の組が返りうる。次の問い合わせで正しい値に戻る一過性のずれで、従来の「次の描画まで古いまま」より窓は狭い。PR に記載する。

### 0.3 レビューとミューテーション検証

- 前倒しの脆弱性レビュー: 該当なし(外部入力のパース・パス操作・プロセス起動・WebView・ネットワークに触れない)。
- 前倒しのコード品質レビュー: 該当なし(後続が依存する新しい seam を作らない。フェーズ 3 が依存するのは「OnPaint が座標更新の契機でなくなること」だけ)。
- ミューテーション検証: **Task 4 の (c) の判定式にスポットチェックを行う**(設計書 §3.4。キャレット位置の算出 = CLAUDE.md §4-A の列挙に入る)。(a)(b) は範囲の矩形の算出で、列挙の外なので行わない。
- 変異を当てるときは、毎回ビルドが成功したことを確かめてからテストを走らせる。`--no-build` は使わない(ビルドに失敗すると古い DLL が走り、変異が「生存」したように見える)。

### 0.4 L5

設計書 §7.3 のとおり**必須**(SR 経路 `UiaTextHostAdapter` に触れる)。実機での確認はユーザーに依頼する(Task 6)。

### 0.5 意図的な挙動差(設計書 §3.5。PR に載せる)

1. `GetBoundingRectangles` の範囲先頭から 10 万行より先に可視域がある場合、従来は空配列だったが、矩形を返すようになる。
2. `BoundingRectangle` と座標系の API が、メインウィンドウを動かした直後も正しい値を返すようになる(不具合修正。監査 M-10 の回収)。
3. Handle の破棄後の `BoundingRectangle` は、最後にキャッシュした値ではなく空矩形(`default(Rect)`)を返す。

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
  dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --perf --scenario S1,S2,S3,S8 --json "$s\before-$i.json"
}
```
Expected: 各回 EXIT 0。EXIT 1(自己チェック失敗)の回は捨てて取り直す。S8 の全文は 1 回あたり約 4 秒かかる。

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

**Step 4(ユーザーの了承後): perf-harness の M-7 を 3 回**

```powershell
dotnet publish src/kxEdit.App -c Release -o "<scratchpad>\pub-before"
foreach ($i in 1..3) { pwsh -File tools/perf-harness.ps1 -PublishDir "<scratchpad>\pub-before" -Scenario M-7 }
```
CSV の `env` 行で NVDA の有無と自己チェック(`status`)を確かめる。

**Step 5:** 集計値(min / 中央 / max)と NVDA の状態を、本書末尾の実施記録に書いてコミットする。

```powershell
git add docs/plans/2026-09-25-perf-uia-rects.md
git commit -m "docs(perf): フェーズ 2 の変更前の計測値を記録"
```

---

## Task 2: S-1 座標を問い合わせ時に求める

**Files:**
- Create: `tests/kxEdit.Editor.Tests/UiaScreenCoordinateTests.cs`
- Modify: `src/kxEdit.Editor/NativeMethods.cs`(`GetClientRect` / `ClientToScreen` を追加)
- Modify: `src/kxEdit.Editor/UiaTextHostAdapter.cs`(キャッシュ 4 field・`OnBoundsChanged`・`RefreshClientToScreenOrigin` を削除。3 経路をその場計算へ)
- Modify: `src/kxEdit.Editor/EditorControl.cs`(`OnSizeChanged` / `OnLocationChanged` の override を削除。`:81-84` / `:115-119` のコメント)
- Modify: `src/kxEdit.Editor/EditorControl.Paint.cs:176-181`(`RefreshClientToScreenOrigin` の呼び出しを削除)
- Modify: `src/kxEdit.Accessibility/IUiaTextHost.cs:96`(`BoundingRectangle` の doc)
- Modify: `tests/kxEdit.Editor.Tests/UiaTextHostAdapterContractTests.cs`(field の一覧)

**Step 1: 失敗するテストを書く**

`tests/kxEdit.Editor.Tests/UiaScreenCoordinateTests.cs`:

```csharp
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using kxEdit.Accessibility;
using kxEdit.Core.Buffers;
using kxEdit.Editor;
using Xunit;

namespace kxEdit.Editor.Tests;

/// <summary>
/// フェーズ 2(S-1・設計書 §7.1): UIA の座標系 API が、描画を経ずにメインウィンドウを動かした
/// 直後も正しいスクリーン座標を返すこと。HostForm は画面外(-32000)にあり WM_PAINT が届かない
/// ので、「OnPaint 末尾で座標を更新する」旧実装の陳腐化をそのまま観測できる。
/// </summary>
public class UiaScreenCoordinateTests
{
    private static (HostForm Form, EditorControl Ctrl) MakeHosted(string text)
    {
        var form = HostForm.CreateVisible();
        var ctrl = new EditorControl { Dock = DockStyle.Fill };
        form.Controls.Add(ctrl);
        ctrl.SetSource(TextBuffer.FromString(text));
        form.ClientSize = new Size(300, 200);
        form.PerformLayout();
        return (form, ctrl);
    }

    /// <summary>フォームを画面外のまま動かす(子の LocationChanged は発火しない)。</summary>
    private static void MoveForm(Form form) =>
        form.Location = new Point(form.Location.X + 500, form.Location.Y + 300);

    [Fact]
    public void GetBoundingRectangles_AfterFormMove_FollowsNewOrigin()
    {
        Sta.Run(() =>
        {
            var (form, ctrl) = MakeHosted("hello\nworld");
            try
            {
                IUiaTextHost host = ctrl;
                var before = host.GetBoundingRectangles(0, 5);
                Assert.NotEmpty(before); // fixture 前提: 行 0 は可視
                var originBefore = ctrl.PointToScreen(Point.Empty);

                MoveForm(form);
                var origin = ctrl.PointToScreen(Point.Empty);
                Assert.NotEqual(originBefore, origin); // fixture 前提: 実際に動いた

                var after = host.GetBoundingRectangles(0, 5);
                Assert.Equal(before[0] + (origin.X - originBefore.X), after[0]);
                Assert.Equal(before[1] + (origin.Y - originBefore.Y), after[1]);
                Assert.Equal(before[2], after[2]);
                Assert.Equal(before[3], after[3]);
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }

    [Fact]
    public void OffsetFromScreenPoint_AfterFormMove_UsesNewOrigin()
    {
        Sta.Run(() =>
        {
            var (form, ctrl) = MakeHosted("hello\nworld");
            try
            {
                MoveForm(form);
                var origin = ctrl.PointToScreen(Point.Empty);
                int lh = ctrl.Metrics.LineHeightPx;
                IUiaTextHost host = ctrl;
                // 行 1("world")の左端・縦中央。旧実装は移動前の原点で client 座標に直すので、
                // 300px 下=文書の外を指して文書末尾(11)に丸める。
                int result = host.OffsetFromScreenPoint(origin.X + 1, origin.Y + lh + lh / 2);
                Assert.InRange(result, 6, 7);
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }

    [Fact]
    public void BoundingRectangle_AfterFormMove_MatchesClientRectOnScreen()
    {
        Sta.Run(() =>
        {
            var (form, ctrl) = MakeHosted("hello");
            try
            {
                MoveForm(form);
                var r = ctrl.RectangleToScreen(ctrl.ClientRectangle);
                IUiaTextHost host = ctrl;
                Assert.Equal(
                    new System.Windows.Rect(r.Left, r.Top, r.Width, r.Height),
                    host.BoundingRectangle
                );
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }

    // RPC スレッド相当(UI スレッド以外)から読んでも、UI スレッドで読んだ値と一致すること
    // (BoundingRectangle は Invoke しない=a11y 鉄則。Win32 の HWND API だけで答える)。
    [Fact]
    public void BoundingRectangle_FromWorkerThread_MatchesUiThreadValue()
    {
        Sta.Run(() =>
        {
            var (form, ctrl) = MakeHosted("hello");
            try
            {
                MoveForm(form);
                IUiaTextHost host = ctrl;
                var onUi = host.BoundingRectangle;
                System.Windows.Rect onWorker = default;
                var t = new Thread(() => onWorker = host.BoundingRectangle);
                t.Start();
                Assert.True(t.Join(5000), "ワーカースレッドが戻らない(Invoke して UI 待ちになっている)");
                Assert.NotEqual(default, onUi); // fixture 前提
                Assert.Equal(onUi, onWorker);
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }

    // 設計書 §3.5: Handle の破棄後は、最後にキャッシュした値ではなく空矩形を返す。
    [Fact]
    public void BoundingRectangle_AfterDispose_ReturnsDefault()
    {
        Sta.Run(() =>
        {
            var (form, ctrl) = MakeHosted("hello");
            try
            {
                IUiaTextHost host = ctrl;
                Assert.NotEqual(default, host.BoundingRectangle); // fixture 前提
                ctrl.Dispose();
                Assert.Equal(default(System.Windows.Rect), host.BoundingRectangle);
            }
            finally
            {
                form.Close();
            }
        });
    }
}
```

**Step 2: 失敗を確かめる**

```powershell
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~UiaScreenCoordinateTests"
```
Expected: `*_AfterFormMove_*` の 3 件と `AfterDispose` が FAIL(古い原点・古い矩形を返す)。`FromWorkerThread` は PASS してよい(キャッシュ値も一致するため。変更後の回帰の網)。
`AfterFormMove` が PASS した場合は、fixture が陳腐化を起こせていない(画面内に出て WM_PAINT が届いた等)。先へ進まず原因を調べる。

**Step 3: NativeMethods に Win32 API を足す**

`src/kxEdit.Editor/NativeMethods.cs` の `GetCaretPos` の直後に追加する(`POINT` / `RECT` は同ファイルに既存)。

```csharp
    // フェーズ 2(S-1・2026-09-25): UIA の BoundingRectangle を RPC スレッドでその場で求める。
    // どちらも HWND を受けるだけのスレッド安全な Win32 API で、エディタ内部の状態
    // (UI スレッド専有)に触れない=a11y 鉄則に反しない。
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);
```

**Step 4: adapter をその場計算にする**

`src/kxEdit.Editor/UiaTextHostAdapter.cs`:

1. field `_boundsSync` / `_bounds` / `_clientToScreenX` / `_clientToScreenY` と、その上のコメント(`:58` の `_bounds` の行、`:73-76`)を削除する。
2. `OnBoundsChanged`(`:134-150`)と `RefreshClientToScreenOrigin`(`:152-166`)を削除する。
3. `OnHandleCreated` を次にする。

```csharp
    /// <summary>
    /// Handle 生成通知 (EditorControl.OnHandleCreated から)。_hwnd をキャッシュする。
    /// </summary>
    /// <remarks>
    /// フェーズ 2(S-1): 以前はここで初期 bounds も計算していた。座標は問い合わせのたびに
    /// 求めるようになったので(<c>BoundingRectangle</c> / <c>ComputeBoundingRectangles</c> /
    /// <c>ComputeOffsetFromScreenPoint</c>)、ここで持つのは hwnd だけ。
    /// </remarks>
    public void OnHandleCreated()
    {
        _hwnd = _host.Handle; // P5 Task 14 (I-2): RPC スレッドが安全に読める hwnd キャッシュ
    }
```

4. `BoundingRectangle` を次にする。

```csharp
    // フェーズ 2(S-1・2026-09-25): 以前は OnPaint 末尾と OnBoundsChanged で更新したキャッシュを
    // 返していた。LocationChanged は親から見た位置の変化なので、メインウィンドウを動かしても
    // 発火せず、次の描画まで古い位置を返していた(監査 M-10)。RPC スレッドから呼ばれるので
    // Invoke せず、キャッシュ済みの _hwnd に対するスレッド安全な Win32 API でその場で求める。
    // _hwnd == 0(Handle 未生成 / 破棄後)や Win32 の失敗は、従来の初期値と同じ default を返す
    // (破棄後に最後の値を返し続けていたのは、設計書 §3.5 の意図的な挙動差として変える)。
    // 2 回の呼び出しの間にリサイズが挟まると「新しい原点と古い大きさ」が返りうるが、
    // 次の問い合わせで戻る一過性のずれで、従来の「次の描画まで古い」より窓が狭い。
    System.Windows.Rect IUiaTextHost.BoundingRectangle
    {
        get
        {
            nint hwnd = _hwnd;
            if (hwnd == 0)
                return default;
            if (!NativeMethods.GetClientRect(hwnd, out var rc))
                return default;
            var origin = new NativeMethods.POINT();
            if (!NativeMethods.ClientToScreen(hwnd, ref origin))
                return default;
            return new System.Windows.Rect(
                origin.x,
                origin.y,
                rc.right - rc.left,
                rc.bottom - rc.top
            );
        }
    }
```

5. `ComputeBoundingRectangles` の `int csx = _clientToScreenX, csy = _clientToScreenY;` を次にする。

```csharp
        // フェーズ 2(S-1): client 原点のスクリーン座標は問い合わせのたびに求める
        // (本メソッドは UI スレッド上でのみ走る=下の _host.ScrollX と同じ理由で PointToScreen を呼べる)。
        var origin = _host.PointToScreen(System.Drawing.Point.Empty);
        int csx = origin.X,
            csy = origin.Y;
```

6. `ComputeOffsetFromScreenPoint` の変換を次にする(コメントの「client 原点は _clientToScreenX/Y」も直す)。

```csharp
        // スクリーン→クライアント変換。原点は問い合わせのたびに求める(フェーズ 2 S-1・UI スレッド上)。
        // 範囲外は OffsetFromClientPoint 側で「Y<0=先頭視覚行の X」「exhausted=文書末尾」に丸める。
        var origin = _host.PointToScreen(System.Drawing.Point.Empty);
        int clientX = (int)(x - origin.X);
        int clientY = (int)(y - origin.Y);
```

7. ファイル冒頭の責務コメント(`:7-15`)の field 一覧と通知経路から、削除した 4 field・`OnBoundsChanged` を外す。

**Step 5: EditorControl 側の呼び出しを消す**

- `src/kxEdit.Editor/EditorControl.cs:2789-2801` の `OnSizeChanged` / `OnLocationChanged` の override を削除する。削除前に、`base.OnXxx(e)` と `_uia.OnBoundsChanged()` 以外の処理がないことを確かめる。
- `src/kxEdit.Editor/EditorControl.Paint.cs:178-181`(`_uia.RefreshClientToScreenOrigin();` とその直前のコメント 3 行)を削除する。`_lastFrame = frame;` とそのコメントは残す。
- `EditorControl.cs:81-84` / `:115-119` の「Uia 系 12 field」のコメントを、実際の field 一覧(8 個)に直す。`OnBoundsChanged` への言及も外す。

```powershell
# 取り残しがないこと(0 件を期待)
git grep -n -e OnBoundsChanged -e RefreshClientToScreenOrigin -e _clientToScreen -e "_bounds\b" -e _boundsSync -- src
```

**Step 6: 契約テストと IUiaTextHost の doc**

- `tests/kxEdit.Editor.Tests/UiaTextHostAdapterContractTests.cs`: `UiaFields` から `_boundsSync` / `_bounds` / `_clientToScreenX` / `_clientToScreenY` を外す。メソッド名 `UiaTextHostAdapter_Owns12UiaFields` を `UiaTextHostAdapter_OwnsUiaFields` にし、`// (1)` `// (2)` のコメントの「12」を外す。冒頭のコメントに「フェーズ 2(S-1)で座標キャッシュ 4 field を削除した」と 1 行足す。
- さらに、削除した 4 field が adapter に**存在しない**ことも固定する(キャッシュの再導入で陳腐化が戻るのを防ぐ):

```csharp
    // フェーズ 2(S-1): 座標はその場で求める。キャッシュを戻すと、描画なしのウィンドウ移動で
    // 古い座標を返す不具合(監査 M-10)が戻る(UiaScreenCoordinateTests が挙動側の網)。
    [Fact]
    public void UiaTextHostAdapter_HasNoScreenCoordinateCache()
    {
        var adapterType = typeof(EditorControl).Assembly.GetType("kxEdit.Editor.UiaTextHostAdapter");
        Assert.NotNull(adapterType);
        foreach (var name in new[] { "_boundsSync", "_bounds", "_clientToScreenX", "_clientToScreenY" })
            Assert.Null(adapterType!.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic));
    }
```

- `src/kxEdit.Accessibility/IUiaTextHost.cs:96` の doc を次にする。

```csharp
    /// <summary>コントロールのクライアント領域のスクリーン座標矩形。問い合わせのたびに求める(RPC スレッド安全・マーシャリングしない)。Handle がなければ既定値。</summary>
```

**Step 7: テストを通す**

```powershell
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~UiaScreenCoordinateTests|FullyQualifiedName~UiaTextHostAdapterContractTests|FullyQualifiedName~EditorControlBoundingRectsTests|FullyQualifiedName~EditorControlOffsetFromPointTests"
dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~Accessibility"
```
Expected: 全件 PASS。

**Step 8: コミット**

```powershell
git add src/kxEdit.Editor src/kxEdit.Accessibility/IUiaTextHost.cs tests/kxEdit.Editor.Tests/UiaScreenCoordinateTests.cs tests/kxEdit.Editor.Tests/UiaTextHostAdapterContractTests.cs
git commit -m "fix(uia): スクリーン座標を問い合わせのたびに求め、ウィンドウ移動後の陳腐化をなくす"
```
本文に、S-1 / 監査 M-10 の回収と、破棄後に空矩形を返す挙動差(設計書 §3.5)を書く。

**仕様レビュー**(タスク完了時): a11y 鉄則(RPC スレッドから UI スレッド専有の状態に触れない)を、`BoundingRectangle` の新実装について確かめる。

---

## Task 3: P-9 (a)(b) 範囲の行ループを可視域に限定する

**Files:**
- Modify: `src/kxEdit.Editor/UiaTextHostAdapter.cs`(`ComputeBoundingRectangles`)
- Modify: `tests/kxEdit.Editor.Tests/EditorControlBoundingRectsTests.cs`

**Step 1: 旧走査の参照実装と、突き合わせのテストを書く**

`EditorControlBoundingRectsTests` に追加する。参照実装は**変更前の走査そのまま**(TopLine の上を飛ばさない・下で打ち切らない)で、`safety` の上限だけを外す(上限は §3.5 の挙動差そのものなので、参照には入れない)。

```csharp
    /// <summary>
    /// フェーズ 2(P-9 (a)(b))の突き合わせ用: 変更前の ComputeBoundingRectangles と同じ走査。
    /// 範囲の全論理行について ComputeCaretPointForUia を呼び、可視なものだけ矩形にする。
    /// safety(10 万反復)は設計書 §3.5 の意図的な挙動差なので入れない。
    /// </summary>
    private static double[] ReferenceRects(EditorControl ctrl, TextSnapshot snap, int start, int end)
    {
        int s = Math.Clamp(start, 0, snap.CharLength);
        int en = Math.Clamp(end, 0, snap.CharLength);
        var list = new List<double>();
        if (s >= en)
            return list.ToArray();
        var origin = ctrl.PointToScreen(System.Drawing.Point.Empty);
        int sx = ctrl.ScrollX;
        int lh = ctrl.Metrics.LineHeightPx;
        int pos = s;
        while (pos < en)
        {
            int line = snap.GetLineIndexOfChar(pos);
            int rangeEnd = Math.Min(en, snap.GetLineEnd(line, includeBreak: false));
            var (x1, y1, visible) = ctrl.ComputeCaretPointForUia(pos);
            var (x2, _, _) = ctrl.ComputeCaretPointForUia(rangeEnd);
            if (visible)
            {
                list.Add(origin.X + x1 - sx);
                list.Add(origin.Y + y1);
                list.Add(Math.Max(1, x2 - x1));
                list.Add(lh);
            }
            int next = line + 1 < snap.LineCount ? snap.GetLineStart(line + 1) : snap.CharLength;
            if (next <= pos)
                break;
            pos = next;
        }
        return list.ToArray();
    }

    /// <summary>
    /// 200 行。空行(i % 17 == 5)・CRLF(i % 3 == 0)・非 ASCII を混ぜ、最終行は改行なし。
    /// </summary>
    private static string MixedDoc()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 200; i++)
        {
            if (i % 17 != 5)
                sb.Append($"line{i:D3} あいう");
            if (i < 199)
                sb.Append(i % 3 == 0 ? "\r\n" : "\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// (line, col) を文字オフセットにする。col は行頭からの UTF-16 位置で、行の長さ+1 を渡すと
    /// CRLF の中間(CR と LF の間)を指せる。
    /// </summary>
    private static int Off(TextSnapshot snap, int line, int col) =>
        Math.Min(snap.GetLineStart(line) + col, snap.CharLength);

    // 各ケース: 範囲 [(sl,sc), (el,ec)) と TopLine / 窓の高さ。expectNonEmpty は
    // 「参照も新実装も空」で一致する空振りを防ぐための fixture 前提。
    [Theory]
    [InlineData(50, 200, 0, 0, 199, 5, true)] //   全文(可視域の上・中・下にまたがる)
    [InlineData(50, 200, 0, 0, 30, 3, false)] //   可視域より上だけ
    [InlineData(50, 200, 120, 0, 199, 5, false)] // 可視域より下だけ
    [InlineData(50, 200, 20, 4, 55, 2, true)] //   上の行の途中から可視域の途中まで
    [InlineData(50, 200, 53, 4, 150, 0, true)] //  可視域の途中から下まで
    [InlineData(51, 200, 48, 12, 60, 0, true)] //  可視域の上の 48 行目(CRLF)の CR と LF の間から
    [InlineData(0, 200, 0, 0, 199, 5, true)] //    TopLine=0・下端超過
    [InlineData(190, 200, 100, 0, 199, 5, true)] // 改行なしの最終行を含む
    [InlineData(50, 0, 0, 0, 199, 5, false)] //    PaintHeightPx = 0(すべて不可視)
    public void GetBoundingRectangles_MatchesFullScan_WrapOff(
        int topLine,
        int clientHeight,
        int sl,
        int sc,
        int el,
        int ec,
        bool expectNonEmpty
    )
    {
        Sta.Run(() =>
        {
            var buf = TextBuffer.FromString(MixedDoc());
            using var form = HostForm.CreateVisible();
            var ctrl = new EditorControl();
            form.ClientSize = new System.Drawing.Size(400, 400);
            form.Controls.Add(ctrl);
            ctrl.SetSource(buf);
            try
            {
                ctrl.Size = new System.Drawing.Size(300, clientHeight);
                ctrl.WrapColumns = 0;
                ctrl.TopLine = topLine;
                Assert.Equal(topLine, ctrl.TopLine); // fixture 前提: クランプされていない
                var snap = buf.Current;
                int s = Off(snap, sl, sc);
                int e = Off(snap, el, ec);
                IUiaTextHost host = ctrl;
                var expected = ReferenceRects(ctrl, snap, s, e);
                Assert.Equal(expectNonEmpty, expected.Length > 0); // fixture 前提
                Assert.Equal(expected, host.GetBoundingRectangles(s, e));
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }
```

- CRLF の中間のケース(48 行目): `48 % 3 == 0` なので CRLF。`"line048 あいう"` は 11 文字なので col=12 が CR と LF の間。TopLine=51 にして、48 行目が可視域の上にあることを使う(TopLine-1 ではなく 3 行上になるが、「CRLF の中間から飛ばす」の網として十分)。実装時に `sc` を `snap.GetLineEnd(48, includeBreak: false) - snap.GetLineStart(48) + 1` と一致するか確かめ、ずれていたら InlineData を直す。
- 空行は 5・22・39・56・… 行目にあり、TopLine=50 の可視域(56 行目)に入る。

折り返し ON(`_topSegment > 0`)の突き合わせ:

```csharp
    // 折り返し ON。TopLine の途中セグメントから描いている(_topSegment > 0)ときは、
    // TopLine の上のセグメントが不可視になる。(b) の打ち切りは line > TopLine に限るので、
    // TopLine の隠れたセグメントから始まる範囲でも、後続行の矩形が出なければならない。
    [Theory]
    [InlineData(3, 2, 0)] //  TopLine の隠れたセグメント(先頭)から
    [InlineData(3, 2, 25)] // TopLine の可視セグメントの途中から
    [InlineData(3, 0, 0)] //  _topSegment = 0
    public void GetBoundingRectangles_MatchesFullScan_WrapOn(int topLine, int topSegment, int startCol)
    {
        Sta.Run(() =>
        {
            // 各行 100 字 = 折り返し 10 桁で 10 セグメント
            var text = string.Join("\n", Enumerable.Range(0, 30).Select(i => new string((char)('a' + i % 26), 100)));
            var buf = TextBuffer.FromString(text);
            using var form = HostForm.CreateVisible();
            var ctrl = new EditorControl();
            form.ClientSize = new System.Drawing.Size(400, 400);
            form.Controls.Add(ctrl);
            ctrl.SetSource(buf);
            try
            {
                ctrl.Size = new System.Drawing.Size(300, 300);
                ctrl.WrapColumns = 10;
                ctrl.SetTopPosition(topLine, topSegment);
                Assert.Equal(topSegment, ctrl.TopSegment); // fixture 前提
                var snap = buf.Current;
                int s = snap.GetLineStart(topLine) + startCol;
                IUiaTextHost host = ctrl;
                var expected = ReferenceRects(ctrl, snap, s, snap.CharLength);
                Assert.NotEmpty(expected); // fixture 前提
                Assert.Equal(expected, host.GetBoundingRectangles(s, snap.CharLength));
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }
```

意図的な挙動差(§3.5 の 1)のテスト:

```csharp
    // 設計書 §3.5: 範囲先頭から 10 万行より先に可視域がある場合、従来は safety で打ち切られて
    // 空配列だった。(a) で TopLine の先頭から走査するので矩形を返す。
    [Fact]
    public void GetBoundingRectangles_VisibleAreaBeyond100kLines_ReturnsRects()
    {
        Sta.Run(() =>
        {
            var buf = TextBuffer.FromString(string.Concat(Enumerable.Repeat("a\n", 150_000)));
            using var form = HostForm.CreateVisible();
            var ctrl = new EditorControl();
            form.ClientSize = new System.Drawing.Size(400, 400);
            form.Controls.Add(ctrl);
            ctrl.SetSource(buf);
            try
            {
                ctrl.Size = new System.Drawing.Size(300, 200);
                ctrl.TopLine = 120_000;
                Assert.Equal(120_000, ctrl.TopLine); // fixture 前提
                var snap = buf.Current;
                int e = snap.GetLineStart(120_050);
                IUiaTextHost host = ctrl;
                var actual = host.GetBoundingRectangles(0, e);
                Assert.NotEmpty(actual);
                Assert.Equal(ReferenceRects(ctrl, snap, 0, e), actual);
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }
```

(`using System.Linq;` / `using System.Collections.Generic;` / `using kxEdit.Core.Text;`(`TextSnapshot` の名前空間。実装時に確かめる)を足す。)

**Step 2: 失敗を確かめる**

```powershell
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~EditorControlBoundingRectsTests"
```
Expected: `VisibleAreaBeyond100kLines` だけ FAIL(空配列)。突き合わせの Theory は変更前でも PASS する(等価性の網なので、変更後に PASS し続けることが要点)。

**Step 3: 実装**

`ComputeBoundingRectangles` のループの前後を次にする(矩形を積む部分は変えない)。

```csharp
        int sx = _host.ScrollX;
        int lineHeight = _host.Metrics.LineHeightPx;
        int topLine = _host.TopLine;
        var rects = new List<double>(16);

        int pos = s;
        // フェーズ 2(P-9 (a)): TopLine より上の行は、ComputeCaretPoint が必ず即座に不可視を返し、
        // 副作用もない。よって範囲の先頭が上にはみ出していれば TopLine の先頭まで一気に飛ばす
        // (厳密に等価)。CRLF の中間は前の行に属する規約でも、飛び先は TopLine の先頭で同じ。
        // 前提: _bufferSnapshot と host の _buffer.Current が同一であること。本文を差し替える経路は
        // すべて同じ同期処理の中で OnSnapshotChanged を呼び、本メソッドは Invoke 経由
        // (= UI スレッドがメッセージを汲んだとき)にしか走らないので成り立つ。
        if (topLine < snap.LineCount)
        {
            int topStart = snap.GetLineStart(topLine);
            if (pos < topStart)
                pos = topStart;
        }
        int safety = 0;
        while (pos < en && safety++ < 100_000)
        {
            int line = snap.GetLineIndexOfChar(pos);
            int lineEndNoBreak = snap.GetLineEnd(line, includeBreak: false);
            int rangeEnd = Math.Min(en, lineEndNoBreak);

            var (x1, y1, visible) = _host.ComputeCaretPointForUia(pos);
            if (visible)
            {
                var (x2, _, _) = _host.ComputeCaretPointForUia(rangeEnd);
                // 幅 w は差分なので _scrollX の影響を受けない (両端から同量を引くため)。
                double w = Math.Max(1, x2 - x1);
                rects.Add(csx + x1 - sx);
                rects.Add(csy + y1);
                rects.Add(w);
                rects.Add(lineHeight);
            }
            else if (line > topLine)
            {
                // フェーズ 2(P-9 (b)): TopLine より下の行で不可視になったら、後続の行は積み上げの
                // 視覚行数が単調に増えるので必ず不可視。打ち切る。
                // line == topLine は _topSegment による上方向のはみ出し(隠れたセグメント)で
                // 不可視になりうるので、打ち切ってはならない(後続行は可視でありうる)。
                break;
            }
            ...(nextLineStart の計算は変えない)
        }
```

- `x2` の計算を `if (visible)` の中へ移した。`ComputeCaretPointForUia` は幅メモへの書き込み以外に副作用がなく、`x2` は可視のときにしか使わないので、返す配列は変わらない。
- `safety` は残す(可視域の中だけを走査するので、実際に 10 万に届くのは可視域の行数が 10 万を超える場合だけ)。

**Step 4: テストを通す**

```powershell
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~EditorControlBoundingRectsTests|FullyQualifiedName~UiaScreenCoordinateTests"
```
Expected: 全件 PASS。

**Step 5: コミット**

```powershell
git add src/kxEdit.Editor/UiaTextHostAdapter.cs tests/kxEdit.Editor.Tests/EditorControlBoundingRectsTests.cs
git commit -m "perf(uia): GetBoundingRectangles の行ループを可視域に限定する"
```
本文に、10 万行の safety による空配列が矩形を返すようになる挙動差(設計書 §3.5)を書く。

---

## Task 4: P-9 (c) 折り返し OFF の `ComputeCaretPoint` を短絡する

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.cs:2504-2629`(`ComputeCaretPoint`)
- Create: `tests/kxEdit.Editor.Tests/ComputeCaretPointNoWrapShortcutTests.cs`

**Step 1: 本体を Core に切り出し、テスト用の入口を作る(挙動は変えない)**

```csharp
    internal (int X, int Y, bool Visible) ComputeCaretPoint(int offset) =>
        ComputeCaretPointCore(offset, allowNoWrapShortcut: true);

    /// <summary>
    /// テスト専用: 折り返し OFF の短絡(フェーズ 2 P-9 (c))を使わず、TopLine からの積み上げループで
    /// 求める。短絡と積み上げの同値性を突き合わせるための参照(積み上げループは折り返し ON のために
    /// 製品コードに残るので、テストに旧実装を複製するより確実)。
    /// </summary>
    internal (int X, int Y, bool Visible) TestHook_ComputeCaretPointByAccumulation(int offset) =>
        ComputeCaretPointCore(offset, allowNoWrapShortcut: false);

    private (int X, int Y, bool Visible) ComputeCaretPointCore(int offset, bool allowNoWrapShortcut)
    {
        // (既存の本体をそのまま移す)
    }
```

既存の doc コメント(`:2504-2524`)は `ComputeCaretPoint` に残す。

**Step 2: 突き合わせのテストを書く**

`tests/kxEdit.Editor.Tests/ComputeCaretPointNoWrapShortcutTests.cs`:

```csharp
using System.Drawing;
using System.Windows.Forms;
using kxEdit.Core.Buffers;
using kxEdit.Editor;
using Xunit;

namespace kxEdit.Editor.Tests;

/// <summary>
/// フェーズ 2(P-9 (c)・設計書 §7.2): 折り返し OFF の ComputeCaretPoint の短絡が、
/// TopLine からの積み上げループ(=変更前の実装)と全オフセットで一致すること。
/// 境界: 最終可視行・1 行はみ出し・窓の高さが行高の端数・古い _topSegment・PaintHeightPx = 0。
/// </summary>
public class ComputeCaretPointNoWrapShortcutTests
{
    // rows: 窓の高さを行高の何行分にするか。frac: 端数(-1 は「行高 - 1」を意味する)。
    [Theory]
    [InlineData(10, 0, 0, 0)] //    高さがちょうど 10 行(最終可視行と 1 行はみ出しの境界)
    [InlineData(10, 1, 0, 0)] //    10 行 + 1px(11 行目が 1px だけ見える)
    [InlineData(10, -1, 0, 0)] //   10 行 + (行高 - 1)px
    [InlineData(10, 0, 37, 0)] //   TopLine > 0
    [InlineData(10, 1, 37, 3)] //   古い _topSegment(折り返し OFF でも SetTopPosition で残せる)
    [InlineData(0, 0, 37, 0)] //    PaintHeightPx = 0
    public void Shortcut_MatchesAccumulation_ForEveryLine(int rows, int frac, int topLine, int topSegment)
    {
        Sta.Run(() =>
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 120; i++)
                sb.Append(i % 7 == 3 ? "" : $"行{i:D3} abc").Append(i % 2 == 0 ? "\r\n" : "\n");
            var buf = TextBuffer.FromString(sb.ToString());
            using var form = HostForm.CreateVisible();
            var ctrl = new EditorControl();
            form.ClientSize = new Size(600, 800);
            form.Controls.Add(ctrl);
            ctrl.SetSource(buf);
            try
            {
                ctrl.WrapColumns = 0;
                int lh = ctrl.Metrics.LineHeightPx;
                int extra = frac < 0 ? lh - 1 : frac;
                ctrl.Size = new Size(300, rows * lh + extra);
                ctrl.SetTopPosition(topLine, topSegment);
                Assert.Equal(topLine, ctrl.TopLine); // fixture 前提
                Assert.Equal(topSegment, ctrl.TopSegment); // fixture 前提

                var snap = buf.Current;
                int visible = 0,
                    hiddenBelow = 0;
                for (int line = 0; line < snap.LineCount; line++)
                {
                    int start = snap.GetLineStart(line);
                    int end = snap.GetLineEnd(line, includeBreak: false);
                    foreach (int off in new[] { start, (start + end) / 2, end })
                    {
                        var expected = ctrl.TestHook_ComputeCaretPointByAccumulation(off);
                        var actual = ctrl.ComputeCaretPoint(off);
                        Assert.Equal(expected, actual);
                        if (actual.Visible)
                            visible++;
                        else if (line > topLine)
                            hiddenBelow++;
                    }
                }
                // fixture 前提: 可視と「下にはみ出して不可視」の両方を踏んでいる(PaintHeightPx = 0 を除く)
                if (rows > 0)
                    Assert.True(visible > 0, "可視の行がない");
                Assert.True(hiddenBelow > 0, "下にはみ出した行がない");
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }
}
```

- `ctrl.Size` の高さがそのまま `ClientSize.Height` になるか(枠がないか)を実装時に確かめる。行は短いので hscroll は出ない(`PaintHeightPx == ClientSize.Height`)。
- `SetTopPosition` は折り返し OFF でもセグメント index を上限でクランプしない前提(`EditorControl.cs:900-`)。クランプされるなら `topSegment=3` のケースは fixture 前提で落ちるので、そのときはケースを外して計画の実施記録に書く。

**Step 3: テストを走らせる(短絡なしで両者は同じ経路なので PASS する)**

```powershell
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~ComputeCaretPointNoWrapShortcutTests"
```
Expected: PASS(fixture 前提が成り立つことの確認)。

**Step 4: 短絡を入れる**

`ComputeCaretPointCore` の、`int paintHeight = PaintHeightPx;` の直後(= `logicalLine == _topLine && segIdx < _topSegment` の判定より後)を次にする。既存のループは `else` 側へ入れ、中身は変えない。

```csharp
        int visualRowsBeforeThisLine = 0;
        if (allowNoWrapShortcut && maxWidthPx <= 0 && lineHeight > 0)
        {
            // フェーズ 2(P-9 (c)): 折り返しなし(maxWidthPx <= 0)では LineLayout の Wrap 系が
            // 必ず 1 セグメントを返す(LineLayout.WrapCore 冒頭)。よって下のループの
            // eff = Math.Min(skip, 0) = 0 で、k 行目までの積み上げは常に k になり、ループは
            // 「k * lineHeight >= paintHeight となる k (1..n) があるか」を判定するだけ。
            // k について単調なので n だけ見ればよい(厳密に等価)。n = 0 はループが回らない場合で、
            // 0 >= paintHeight は paintHeight = 0 のときだけ真。そのとき従来も末尾の
            // y (= 0) >= paintHeight で不可視を返していた。
            // 毎打鍵の PositionCaret / BringCaretIntoView でも走る経路なので、可視域の各行の
            // LineTextOf(文字列化)も消える。
            // long で掛ける: n は文書の行数まで大きくなりうる(int だと溢れて可視と誤判定する)。
            // この判定は _topSegment による上方向のはみ出し判定(上)より後に置く。折り返し OFF でも
            // SetTopPosition で古い _topSegment が残りうるので、その扱いを従来どおりに保つため。
            int n = logicalLine - _topLine;
            if ((long)n * lineHeight >= paintHeight)
                return (0, 0, false);
            visualRowsBeforeThisLine = n;
        }
        else
        {
            int maxUsefulRows = ...(既存のループをそのまま)
        }
```

- `visualRowsBeforeThisLine` の宣言をループの前(`if` の外)へ出す。`maxUsefulRows` は `else` の中へ入れる(ループでしか使わない)。
- `lineHeight <= 0` は従来のループに戻す(設計書 §7.2)。

**Step 5: テストを通す**

```powershell
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~ComputeCaretPointNoWrapShortcutTests|FullyQualifiedName~EditorControlBoundingRectsTests|FullyQualifiedName~Caret|FullyQualifiedName~Scroll"
```
Expected: 全件 PASS。

**Step 6: ミューテーション検証(スポットチェック)**

1 つずつ当て、ビルドが成功したことを確かめてから(`--no-build` を使わない)、Step 5 の `ComputeCaretPointNoWrapShortcutTests` を走らせ、FAIL(= 変異が殺された)を確かめる。終わったら `git checkout -- src/kxEdit.Editor/EditorControl.cs` で戻し、`git diff` が空であることを確かめる。

| # | 変異 | 期待 |
|---|---|---|
| M1 | `int n = logicalLine - _topLine;` → `logicalLine - _topLine + 1` | 殺される(y が 1 行ずれる) |
| M2 | `visualRowsBeforeThisLine = n;` → `= n - 1` | 殺される |
| M3 | `visualRowsBeforeThisLine = n;` → `= 0` | 殺される |
| M4 | `(long)n * lineHeight >= paintHeight` → `>` | **等価変異**(末尾の `y >= paintHeight` が同じ結論を出す。§0.2)。生存を記録する |
| M5 | 短絡の条件 `maxWidthPx <= 0` → `maxWidthPx < 0` | **等価変異**(短絡が効かずループに落ちるだけ)。生存を記録する |
| M6 | 短絡をはみ出し判定(`segIdx < _topSegment`)の前へ移し、`logicalLine == _topLine` でも短絡で可視を返す形にする | `topSegment=3` のケースで殺される |

結果(殺された / 生存と理由)を本書の実施記録に書く。

**Step 7: コミット**

```powershell
git add src/kxEdit.Editor/EditorControl.cs tests/kxEdit.Editor.Tests/ComputeCaretPointNoWrapShortcutTests.cs
git commit -m "perf(editor): 折り返し OFF の ComputeCaretPoint を積み上げなしで即答する"
```

---

## Task 5: 変更後の計測

**Step 1:** Task 1 と同じ条件(NVDA の有無を揃える)で、`--perf --scenario S1,S2,S3,S8` を 3 回(`after-*.json`)。Task 1 の Step 3 の集計で min / 中央 / max を出す。

**Step 2: 判定**(設計書 §3.2・§7.3)

- **S8**: 1 万行(全文)の所要時間が、1 行の数倍以内になっていること。
- S1・S2・S3 は副次効果。変更前の揺れを超えて悪化していないこと(改善していれば記録する)。
- 揺れの範囲に収まる項目は、PR に書いてユーザーに採否を判断してもらう。

**Step 3(ユーザーの了承後):** perf-harness の M-7 を変更後の publish(`<scratchpad>\pub-after`)で 3 回。1 万行(全文)が 1 行と同程度(十数 ms)になっていること。

**Step 4:** 値を実施記録に書いてコミットする。

```powershell
git add docs/plans/2026-09-25-perf-uia-rects.md
git commit -m "docs(perf): フェーズ 2 の変更後の計測値を記録"
```

---

## Task 6: L5・最終レビュー・品質ゲート

**Step 1: `tools/sr-regression.ps1`**

```powershell
pwsh -File tools/sr-regression.ps1
```
Expected: 全項目 PASS。UIA 応答の検証までで、実発声の代わりにはならない(CLAUDE.md §5)。

**Step 2: L5(ユーザーに依頼。設計書 §7.3)**

変更後の publish を起動し、NVDA で次を確認してもらう。
- **フォーカスハイライト(視覚的ハイライト)が、メインウィンドウを動かした後も正しい位置を指すこと**(S-1 の主目的)。移動の直後、キー操作や描画を挟まずに確かめる。
- 全選択(Ctrl+A)時のハイライト矩形。TopLine > 0 にスクロールした状態でも確かめる。
- `RangeFromPoint`(マウス追従の読み上げ)。メインウィンドウを動かした後にも確かめる。
- キャレット移動の読み上げ((c) は `PositionCaret` の経路に効く)。
- 見た目の確認が要る場合は、PrintWindow の実解像度画像で判定する(縮小スクリーンショットで判定しない)。

**Step 3: 最終ブランチレビュー(2 パス・別エージェント)**

- コード品質パス(ミューテーション検証のスポットチェックを含む。対象は Task 4 の (c))
- 脆弱性パス(RPC スレッドからの Win32 呼び出しと、UI スレッド専有状態への接触がないこと)
指摘は ① fixup / ② PR に記載して受容 / ③ 理由付き却下 のどれかで扱う。

**Step 4: 品質ゲート**

```powershell
pwsh -File tools/pre-merge-check.ps1
```
Expected: EXIT 0。

**Step 5: PR**(CLAUDE.md §7)

description に、変更前後の計測値(min / 中央 / max)・意図的な挙動差(§0.5 の 3 つ)・`BoundingRectangle` の一過性のずれ(§0.2)・ミューテーション検証の結果・L5 の結果・レビュー経緯を書く。

**Step 6(マージ後):** 設計書 §7 の末尾に「実施記録」を追記する(設計書 §3.1 の 6)。PR に同梱してもよい(フェーズ 1 と同じ)。

---

## 実施記録

### Task 1: 変更前の計測(2026-09-25・src は main `457d252` と同一)

**環境**: フェーズ 0・1 と同じ VM(1024×767・96 DPI)。**NVDA 起動中**(pid 6372)。

**Smoke `--perf --scenario S1,S2,S3,S8`**(Release・3 回の中央値の min / 中央 / max・ms/操作)

| ID | ja10k | en10k |
|---|---|---|
| S1 →← | 6.94 / 7.10 / 7.17 | 6.44 / 6.58 / 6.77 |
| S2 ↓↑ | 6.98 / 7.29 / 7.54 | 6.51 / 6.79 / 6.82 |
| S3a 挿入 | 7.36 / 7.59 / 7.93 | 7.05 / 7.25 / 7.36 |
| S3b BackSpace | 7.31 / 7.83 / 7.86 | 7.05 / 7.24 / 7.36 |

| S8(ja10k・UI スレッドから直接) | min / 中央 / max(ms) |
|---|---|
| 1 行 | 0.00 / 0.00 / 0.00 |
| 40 行 | 4.61 / 4.79 / 5.17 |
| 1,000 行 | 344.73 / 348.57 / 380.99 |
| 全文 | 3,797.61 / 3,975.92 / 4,221.84 |

**perf-harness M-7**(publish・3 回・COM 越し・ms。可視矩形は 36)

| 範囲 | 1 回目 | 2 回目 | 3 回目 |
|---|---|---|---|
| 1 行 | 0.26 | 0.25 | 0.31 |
| 40 行 | 4.23 | 4.95 | 4.89 |
| 1,000 行 | 245.72 | 294.10 | 274.43 |
| 全文 | 2,815.91 | 3,291.31 | 3,124.72 |

3 回とも `status=completed`。

### Task 2: S-1(ef8546c・fixup 2d8bce1)

- **計画からの逸脱**: 既存テスト `EditorControlOffsetFromPointTests.OffsetFromScreenPoint_OutOfBounds_Clamped` の入力を直した。HostForm は (-32000,-32000) にあるので、絶対座標 (-9999,-9999) は client の右下(= 文書末尾)を指す。旧実装は Handle 生成時(親付け前)の古い原点 (8,31) で変換していたので、偶然 0 になって通っていた(監査 M-10 の陳腐化そのものに依存していた)。入力を実際の原点からの相対 `(origin - 9999)` にして、「左上より外は先頭 0」の意図を保った。
- **仕様レビュー**: ✅。Minor 3 件。
  - Minor-2(`IUiaTextHost` と adapter 冒頭のスレッド分類の doc が「キャッシュ値で応答」のまま): ① fixup 2d8bce1。
  - Minor-3(移動後の矩形を差分でしか見ていない): ① fixup 2d8bce1 で絶対値の assert を足した。`PointFromCharOffset` は ScrollX 反映済みで、不可視と (0,0) を区別できないので、`ComputeCaretPointForUia` を使う。
  - Minor-1(`IsHandleCreated` の確認直後に Handle が破棄されると、`InvokeRequired` が false になり、Compute が RPC スレッドで走る。今回その経路に `PointToScreen` が加わった): ② 受容。競合自体は以前からあり、`ComputeCaretPoint` の幅メモへの書き込みのほうが重い。既存の競合を直すときに、Compute 側も `_hwnd` + `ClientToScreen` に揃える案とあわせて扱う(申し送り)。

### Task 3: P-9 (a)(b)(85374c5・fixup e923493)

- 変更前は `VisibleAreaBeyond100kLines` だけ FAIL、突き合わせの Theory は変更前後とも PASS(計画どおり)。
- **計画からの逸脱**: `TextSnapshot` の名前空間は `kxEdit.Core.Buffers`。CA1305 のため `StringBuilder.Append(CultureInfo.InvariantCulture, $"…")`。CRLF の中間(48 行目の col 12)と、折り返し ON の TopLine の非クランプを fixture 前提として assert した。
- **仕様レビュー**: ✅(等価性の反例なし。不可視になる 5 経路すべてで、打ち切った後の行が必ず不可視であることを確認)。Minor 2 件はどちらも ① fixup e923493。
  - 「全文」のケースの終端が最終行の途中で、最終行の `nextLineStart = CharLength` の分岐を踏んでいなかった → 終端を文書末尾にした。
  - 「初回反復で範囲の先頭が TopLine より下の行の途中(下のセグメント)にある」ケースがなかった → 可視・不可視の 2 ケースを足した。

### Task 4: P-9 (c)(4d6103c・fixup 3bf6c37)

- **計画からの逸脱**: Step 3(短絡を入れる前の確認)は、`allowNoWrapShortcut` が未使用だと Sonar S1172 でビルドできないので、一時的に `_ = allowNoWrapShortcut;` を置いて確かめた(commit には含まない)。`ctrl.Size` の高さ = `ClientSize.Height` を fixture 前提として足した。`SetTopPosition` は折り返し OFF でもセグメントをクランプしないので、`topSegment=3` のケースを残した。
- **ミューテーション検証**(実際の変更を commit した後に当て、毎回ビルドの成功を確かめてから実行した)

| # | 変異 | 結果 |
|---|---|---|
| M1 | `n = logicalLine - _topLine + 1` | 殺された(6 ケース中 5 で FAIL) |
| M2 | `visualRowsBeforeThisLine = n - 1` | 殺された(同上) |
| M3 | `visualRowsBeforeThisLine = 0` | 殺された(同上) |
| M4 | `>=` → `>` | 生存・等価変異(末尾の `y >= paintHeight` が同じ結論を出す) |
| M5 | `maxWidthPx <= 0` → `< 0` | 生存・等価変異(短絡が効かずにループへ落ちるだけで、出力は同じ) |
| M6 | 短絡をはみ出し判定より前に置いた形 | 殺された(`topSegment=3` のケース) |

  M1〜M3 で PASS したのは PaintHeightPx = 0 のケースだけ(すべて不可視で、座標を比べないため)。
- **仕様レビュー**: ✅(等価性の反例なし。`_wrapColumns > 0` でも `MeasureRun("0") == 0` なら `MaxWrapWidthPx == 0` になり、`maxWidthPx <= 0` を条件にした §0.2 の判断が正しいことも確認された)。
  - Minor-2(境界を踏んでいることを件数で固定していない): ① fixup 3bf6c37 で、可視の論理行数(10 / 11 / 0)を fixture 前提として固定した。
  - Minor-1(短絡が効かなくなっても、出力が変わらないので自動テストでは検出できない = M5 の生存): ② 受容。PR に記載する。性能の退行は計測(S8・S1〜S3)で見る。製品コードにテスト用のカウンタを足すほどの価値はないと判断した。

### Task 5: 変更後の計測(2026-09-25・3bf6c37)

**環境**: Task 1 と同じ。NVDA 起動中(pid 6372)。

**Smoke `--perf --scenario S1,S2,S3,S8`**(min / 中央 / max・ms/操作。括弧内は変更前の中央値)

| ID | ja10k | en10k |
|---|---|---|
| S1 →← | 6.91 / 7.04 / 7.06(7.10) | 6.52 / 6.63 / 6.82(6.58) |
| S2 ↓↑ | 7.14 / 7.14 / 7.30(7.29) | 6.60 / 6.71 / 6.80(6.79) |
| S3a 挿入 | 7.56 / 7.60 / 7.78(7.59) | 7.28 / 7.30 / 7.31(7.25) |
| S3b BackSpace | 7.47 / 7.57 / 7.75(7.83) | 7.24 / 7.24 / 7.30(7.24) |

| S8(ja10k・UI スレッドから直接) | min / 中央 / max(ms) | 変更前の中央値 |
|---|---|---|
| 1 行 | 0.00 / 0.00 / 0.00 | 0.00 |
| 40 行 | 0.45 / 0.53 / 0.53 | 4.79 |
| 1,000 行 | 0.50 / 0.55 / 0.57 | 348.57 |
| 全文 | 0.53 / 0.58 / 0.62 | 3,975.92 |

**perf-harness M-7**(publish・3 回・COM 越し・ms)

| 範囲 | 1 回目 | 2 回目 | 3 回目 | 変更前(3 回) |
|---|---|---|---|---|
| 1 行 | 0.28 | 0.29 | 0.28 | 0.25〜0.31 |
| 40 行 | 2.83 | 2.88 | 2.96 | 4.23〜4.95 |
| 1,000 行 | 2.78 | 2.91 | 2.86 | 245.72〜294.10 |
| 全文 | 2.64 | 2.86 | 2.83 | 2,815.91〜3,291.31 |

3 回とも `status=completed`。

**判定**(設計書 §3.2・§7.3)
- S8: 全文 0.58 ms は、可視域 40 行ぶん(0.53 ms)と同程度で、範囲の行数によらなくなった。1 行は 0.01 ms 未満に丸められるので「1 行の数倍以内」は比で言えないが、1 行と全文の差は 0.6 ms 未満。完了条件を満たす。
- M-7: 全文が 2.6〜2.9 ms で、1 行(0.3 ms)と同じ桁の十数 ms 以下になった。完了条件を満たす。
- S1〜S3: ja10k・en10k とも、変更前の揺れ(min〜max)の範囲に収まった。悪化も、揺れを超える改善もない。(c) が省くのは可視域の各行の `LineTextOf` で、全面再描画(約 7 ms)に比べて小さいため。
