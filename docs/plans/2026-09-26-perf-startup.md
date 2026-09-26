# フェーズ 11: 起動・生成(perf-startup) 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 配布物を ReadyToRun にし(P-7)、設定変更の適用でフォントが変わらなければフォント 3 個と `GdiCharMetrics`(幅メモ)を作り直さない(P-16 前半)。

**Architecture:**
- P-16: `EditorControl` に「前回 `ApplyAppearance` で適用したフォントの要求値」(既定値を補完した後の名前とサイズ)を `FontRequest?` で持つ。今回の要求値と等しければ、フォントの差し替えと `GdiCharMetrics` の再構築だけを飛ばす。テーマ・表示設定・スクロールバー・キャレット・UIA キャッシュの破棄・`_topSegment` のリセットなど、それ以外の処理は従来どおり毎回行う。
- P-7: `release.yml` の publish に `-p:PublishReadyToRun=true` を足す。ci.yml と pre-merge は publish しないので、同じ publish をローカルで行って crossgen の警告(`-warnaserror`)と WebView2Loader.dll の同梱を確かめ、README の「リリース」に記す。
- 計測: 設定変更の適用時間を測る場が既存の Smoke `--perf` にも harness にもないので、Smoke `--perf` に **S10**(同じ設定での `ApplyAppearance` + 全面再描画)を足す。起動は harness の M-1。

**Tech Stack:** C#(.NET 9)、WinForms、GDI(`TextRenderer`)、xUnit、GitHub Actions、PowerShell 7。

**Spec:** `docs/plans/2026-09-24-general-perf-improvements-design.md` §3・§16(以下「設計書」)
**調査記録:** `docs/plans/2026-09-24-general-perf-audit.md` P-7・P-16・M-1(§9.2・§9.5。以下「調査記録」)
**前フェーズの計画:** `docs/plans/2026-09-26-perf-uia-wrap-cache.md`(実施記録は設計書 §15.3 に同梱済み。本フェーズで転記するものはない)

## Global Constraints

- 挙動不変が原則。意図的な挙動差は設計書 §3.5 のフェーズ 11 の行(「配布物が ReadyToRun になり、publish フォルダーが約 2.3MB 大きくなる(zip での増分は未計測)」)だけ。zip での増分は本フェーズで測って PR に書く。
- 比較の相手は**前回適用した要求値**。`_font.Name` とは比べない(英語 UI では「MS Gothic」が返り、存在しない名前ではフォールバック名が返るので、毎回作り直しになる。設計書 §16.2)。
- ctor の既定フォント(半角「MS ゴシック」で比例フォントへ落ちる既知の件)は**触らない**(設計書 §16.2 対象外。多数のテストのピクセル値に影響しうる)。
- フォントと幅メモのタブ間共有はしない(設計書 §18 の保留)。
- 0 warning(`-warnaserror`)。pre-commit フック(CSharpier・ローカルパス検出)を `--no-verify` で飛ばさない。
- コメント・コミットメッセージ本文は日本語。
- 実装者は **commit 後の状態で** build と test を確認する(pre-commit の CSharpier が整形で構造を変えて、アナライザの警告=エラーになることがある)。
- 陰性対照でファイルを書き戻すときは `git checkout -- <file>` か `[IO.File]::WriteAllText(..., [Text.UTF8Encoding]::new($false))`。陰性対照のビルドは `-p:TreatWarningsAsErrors=false`。
- publish の出力は手でコピーしない(WebView2Loader.dll が欠ける。`dotnet publish` の出力をそのまま使う)。

## Review Focus

- **同じ設定での再適用の前に、別のフォントを一度挟んだ場合**(A → B → A) — 2 回目の A は「前回=B」と比べるので作り直すこと。初回の A と比べて誤って使い回さないこと。Task 2 の `ApplyAppearance_FontChangedAndBack_Recreates` で押さえる。
- **ctor の直後の初回の適用** — 設定のフォントが ctor のフォント名と同じ文字列(半角「MS ゴシック」12pt)でも、初回は必ず作り直すこと(ctor のフォントは要求値として記録されていない)。Task 2 の `ApplyAppearance_FirstCall_AlwaysRecreatesEvenIfSameAsCtorFont` で押さえる。
- **フォントが同じでテーマだけ変える** — フォントは使い回し、テーマ(背景色など)は反映されること。Task 2 の `ApplyAppearance_SameFontDifferentTheme_ReusesFontButAppliesTheme` で押さえる。
- **サイズだけ変える**(名前は同じ) — 作り直して行の高さが変わること。Task 2 の `ApplyAppearance_SizeOnlyChanged_Recreates` で押さえる。
- **既定値の補完**(FontName が空・FontSize が 0 以下) — 補完後の値(「ＭＳ ゴシック」12pt)で比べるので、明示の既定値との間で使い回すこと(生成されるフォントは同一)。Task 2 の `ApplyAppearance_EmptyNameAndZeroSize_EqualToExplicitDefaults_Reuses` で押さえる。

---

## 0. 前提と決定事項

### 0.1 計測(設計書 §3.2・§16.3)

- **設定変更の適用時間**: Smoke `--perf` に **S10** を足す(Task 1)。1 操作 = 「同じ `AppSettings` で `ApplyAppearance` → `editor.Update()`(同期 WM_PAINT)」。ja10k / en10k の両方。
  - 変更前はフォント 3 個と `GdiCharMetrics`(ASCII 128 字 + 行高 = `MeasureText` 129 回)を作り直し、幅メモ(非 ASCII のコードポイント幅・run 幅)が空になるので、直後の描画が可視域の run を測り直す。変更後はどちらも起きない。ja10k で差が大きく出る見込み。
  - `MainForm.ApplySettings` は全タブに `ApplyAppearance` を呼ぶので、実アプリではタブ数倍になる(S10 は 1 タブぶん)。
  - `--scenario S10` だけを、変更前後で**各 3 回**走らせる。「3 run の中央値の中央値」と「3 run 通した min–max」で比べる。
- **起動(M-1)**: `tools/perf-harness.ps1 -Scenario M-1` を、変更前(main 相当・通常の publish)と変更後(本ブランチ・ReadyToRun の publish)で**各 3 回**。指標は「窓の表示まで」「入力受付まで」「0.8 秒時点の CPU」「ワーキングセット」。調査記録 §9.2 の値(入力受付 213 → 200 ms)と比べる。
  - harness は利用者の `%APPDATA%\kxEdit` を退避・復元する。**実行前にユーザーの了承を取る**。
  - 起動には P-16 は効かない(起動時の最初の `ApplyAppearance` は必ず作り直すため)。M-1 の差は P-7 のもの。
- **配布物の大きさ**: 変更前後の publish フォルダーと zip(`release.yml` と同じ手順で説明書・変更履歴を同梱して `Compress-Archive`)のバイト数。
- NVDA は起動したままでよい(ユーザー判断 2026-09-25)。他のエージェントの重い処理と並走させない。

### 0.2 設計書からの精密化・逸脱

- **比べる値は既定値を補完した後の要求値**(設計書 §16.2 の「要求したフォント名とサイズ」の精密化)。`FontName` が空なら「ＭＳ ゴシック」、`FontSize` が 0 以下なら 12pt に補完してから比べる。補完後が同じなら `new Font(...)` の引数が同じ=生成されるフォントは同一なので、使い回しても挙動は変わらない。名前は序数比較(大文字小文字の違いも「別の要求」として作り直す=安全側)。
- **スタイルは比べない**。設計書 §16.2 は「フォント名・サイズ・スタイル」と書くが、`AppSettings` にスタイルの項目はなく、`ApplyAppearance` は常に Regular で作る。比べる値が要らない。
- **フォント以外の処理は毎回行う**: `_topSegment = 0`(既存テスト `ApplyAppearance_ResetsTopSegment` が固定)、スクロールバーの再計算、フォーカス時のキャレットの作り直し、`InvalidateAndForgetPaintedFrame`、`NotifyCompositionFont`、UIA の行キャッシュの破棄(先頭・末尾)。フォントが同じなら冗長なものもあるが、挙動不変を優先して残す(コストはフォントの作り直しと幅メモの喪失に比べて小さい)。
- **S10 を新設する**(設計書 §16.3「設定変更の適用時間」を測る場がないため。フェーズ 10 が S9 を足したのと同じ扱い)。

### 0.3 レビューとミューテーション検証

- 前倒しの脆弱性レビュー: 該当なし(外部入力のパース・パス・プロセス起動・WebView・ネットワークに触れない。`release.yml` の変更は publish のフラグ 1 つで、入力の扱いを変えない)。
- 前倒しのコード品質レビュー: 該当なし(後続タスクが依存する seam を導入しない)。
- **ミューテーション検証: 行わない**。設計書 §3.4 はフェーズ 11 のフォント・起動を「禁止」側(GUI・描画・テーマ)に挙げている。Task 2 の最後に陰性対照(使い回しの分岐を外すとテストが落ちること)を行う。

### 0.4 L5

- 設計書 §4 の L5 列は「起動確認」。SR の経路(`kxEdit.Accessibility`・`EditorControl` の UIA 部・App の Speech 系)は変えない(`ApplyAppearance` 内の UIA キャッシュ破棄はそのまま残す)ので、NVDA の発声検証は行わない。
- 起動確認(Task 4): 配布 zip を展開して起動し、文書を開いて Markdown プレビューを表示する。設定ダイアログでフォントサイズを変えて OK → 本文の大きさが変わること、変えずに OK → 表示が崩れないことを、実解像度の画面取得で確かめる。
- 実施前にユーザーの了承を取る(キーボードとマウスを占有する)。

### 0.5 意図的な挙動差

- 設計書 §3.5 のフェーズ 11 の行のみ(配布物が ReadyToRun になり大きくなる)。増分の実測値を PR に書く。

---

## Task 0: 本計画を commit する(docs のみ)

**Files:**
- Create: `docs/plans/2026-09-26-perf-startup.md`(本書)

- [ ] **Step 1: commit**

```bash
git add docs/plans/2026-09-26-perf-startup.md
git commit -m "docs(perf): フェーズ 11(perf-startup)の実装計画"
```

---

## Task 1: Smoke S10 を足し、変更前を計測する

製品コードは変えない。

**Files:**
- Modify: `tests/kxEdit.Editor.Smoke/PerfBench.cs`(`AllScenarios`・文書ごとのループ・S10 の関数)
- Modify: `tools/README.md`(Smoke `--perf` の表に S10 の行)

**Interfaces:**
- Produces: `dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --perf --scenario S10`(Task 4 が使う)

- [ ] **Step 1: S10 を足す**

`AllScenarios` の末尾(`"S9",` の後)に `"S10",` を足す。

文書ごとのループの S7 の直後に足す:

```csharp
                if (opt.Scenarios.Contains("S10"))
                    results.Add(MeasureApplySameSettings(editor, Fresh(text), name, opt));
```

`MeasureFullRepaint` の直後に関数を足す:

```csharp
    /// <summary>
    /// S10: P-16。設定ダイアログで OK を押したときの 1 タブぶん=<b>フォントを含めて同じ</b>設定で
    /// <c>ApplyAppearance</c> → <c>Update()</c>。変更前はフォントと <c>GdiCharMetrics</c> を作り直し、
    /// 幅メモが空になるので直後の描画が可視域の run を測り直す。
    /// </summary>
    private static Result MeasureApplySameSettings(
        EditorControl editor,
        TextBuffer buffer,
        string doc,
        Options opt
    )
    {
        editor.SetOrReplaceSource(buffer);
        editor.SetCaretCharOffset(0);
        editor.TopLine = 0;
        editor.Update();
        // 計測前の外観と同じ(Run の ApplyAppearance(new AppSettings()))=状態を変えない。
        var settings = new AppSettings();
        return Measure(
            editor,
            "S10",
            doc,
            opt.N,
            opt.Warmup,
            update: true,
            () => editor.ApplyAppearance(settings),
            requireOnePaint: true
        );
    }
```

`requireOnePaint` の自己チェックで EXIT 1 になった場合(スクロールバーの再計算が余分な WM_PAINT を起こすなど)は、`requireOnePaint: false` にせず、原因を調べて報告する(描画を測れていない値を使わないため)。

- [ ] **Step 2: tools/README.md の表に行を足す**

`| S9a / S9b | ...` の行の後に:

```markdown
| S10 | P-16: 同じ設定(フォントも同じ)で `ApplyAppearance` + `Update`。設定ダイアログの OK の 1 タブぶん |
```

- [ ] **Step 3: commit して build と S10 の動作を確かめる**

```bash
git add tests/kxEdit.Editor.Smoke/PerfBench.cs tools/README.md
git commit -m "test(perf): Smoke --perf に S10(同じ設定の ApplyAppearance)を追加"
```

```powershell
dotnet build -c Release
dotnet run --project tests/kxEdit.Editor.Smoke -c Release -- --perf --scenario S10 --n 20 --warmup 5
```

Expected: EXIT 0。S10 の行が ja10k / en10k の 2 行出て、`paints_per_op` が 1.00。

- [ ] **Step 4: 変更前の S10 を 3 回測る**

```powershell
$d = "<scratchpad>\perf-startup"; New-Item -ItemType Directory -Force $d | Out-Null
1..3 | ForEach-Object { dotnet run --project tests/kxEdit.Editor.Smoke -c Release --no-build -- --perf --scenario S10 --json "$d\before-s10-$_.json" }
```

Expected: 3 回とも EXIT 0。median を記録する。

- [ ] **Step 5: 変更前の publish と配布物の大きさ**

```powershell
dotnet publish src/kxEdit.App -c Release -r win-x64 --self-contained false -p:DebugType=embedded -o "$d\before\publish"
Copy-Item -Recurse 説明書 "$d\before\publish\説明書"; Copy-Item 変更履歴.txt "$d\before\publish\変更履歴.txt"
Compress-Archive -Path "$d\before\publish\*" -DestinationPath "$d\before\kxEdit.zip"
(Get-ChildItem -Recurse -File "$d\before\publish" | Measure-Object Length -Sum).Sum; (Get-Item "$d\before\kxEdit.zip").Length
```

- [ ] **Step 6: 変更前の M-1 を 3 回測る(ユーザーの了承を取ってから)**

```powershell
1..3 | ForEach-Object { pwsh -File tools\perf-harness.ps1 -PublishDir "$d\before\publish" -Scenario M-1 -OutCsv "$d\before-m1-$_.csv" }
```

Expected: 各 CSV の `env` 行の `status` が `completed`。

- [ ] **Step 7: 計測値を本書末尾の実施記録 (1) に書き、commit**

```bash
git add docs/plans/2026-09-26-perf-startup.md
git commit -m "docs(perf): フェーズ 11 の変更前の計測値"
```

---

## Task 2: P-16 同じフォントなら作り直さない

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.cs`(フィールドと `ApplyAppearance` のフォント差し替え部分・2768〜2800 行付近)
- Create: `tests/kxEdit.Editor.Tests/ApplyAppearanceFontReuseTests.cs`

**Interfaces:**
- Consumes: `internal ICharMetrics EditorControl.Metrics`(既存)、`EditorControl.LineHeightPx`(既存)
- Produces: なし(内部の最適化)

- [ ] **Step 1: 失敗するテストを書く**

`tests/kxEdit.Editor.Tests/ApplyAppearanceFontReuseTests.cs`:

```csharp
using System.Reflection;
using kxEdit.Core.Buffers;
using kxEdit.Core.Settings;

namespace kxEdit.Editor.Tests;

/// <summary>
/// 性能改善フェーズ 11(P-16 前半): <c>ApplyAppearance</c> は、前回適用したフォントの要求値
/// (既定値の補完後の名前とサイズ)と同じならフォントと <c>GdiCharMetrics</c>(幅メモ)を作り直さない。
/// 使い回しの判定は <see cref="EditorControl.Metrics"/> と描画フォント(private <c>_font</c>)の参照で見る。
/// </summary>
public class ApplyAppearanceFontReuseTests
{
    private static (Form f, EditorControl c) MakeControl()
    {
        var f = new Form();
        var c = new EditorControl();
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString("abc 日本語"));
        return (f, c);
    }

    /// <summary>描画フォント(private フィールド)。リネームで静かに緑になる事故を防ぐため名前つきで落とす。</summary>
    private static Font DrawFont(EditorControl c)
    {
        var fi = typeof(EditorControl).GetField(
            "_font",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        Assert.True(fi is not null, "EditorControl に private フィールド _font が見つからない");
        var font = fi!.GetValue(c) as Font;
        Assert.True(font is not null, "_font が Font として取り出せない");
        return font!;
    }

    [Fact]
    public void ApplyAppearance_SameFontTwice_ReusesFontAndMetrics() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                // 既定(ＭＳ ゴシック 12pt)ではない状態から始める(既定値と区別する)。
                var s = new AppSettings { FontName = "Consolas", FontSize = 15f };
                c.ApplyAppearance(s);
                var metrics = c.Metrics;
                var font = DrawFont(c);

                c.ApplyAppearance(new AppSettings { FontName = "Consolas", FontSize = 15f });

                Assert.Same(metrics, c.Metrics);
                Assert.Same(font, DrawFont(c));
            }
        });

    [Fact]
    public void ApplyAppearance_FirstCall_AlwaysRecreatesEvenIfSameAsCtorFont() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                var ctorMetrics = c.Metrics;
                var ctorFont = DrawFont(c);

                // ctor と同じ文字列(半角「MS ゴシック」12pt)。ctor のフォントは要求値として記録されていない。
                c.ApplyAppearance(new AppSettings { FontName = "MS ゴシック", FontSize = 12f });

                Assert.NotSame(ctorMetrics, c.Metrics);
                Assert.NotSame(ctorFont, DrawFont(c));
            }
        });

    [Fact]
    public void ApplyAppearance_FontChangedAndBack_Recreates() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                c.ApplyAppearance(new AppSettings { FontName = "Consolas", FontSize = 15f });
                var first = c.Metrics;
                c.ApplyAppearance(new AppSettings { FontName = "Arial", FontSize = 15f });
                var second = c.Metrics;
                c.ApplyAppearance(new AppSettings { FontName = "Consolas", FontSize = 15f });

                Assert.NotSame(first, second);
                Assert.NotSame(second, c.Metrics); // 前回(Arial)と比べる
                Assert.NotSame(first, c.Metrics); // 最初の Consolas を使い回さない(破棄済み)
            }
        });

    [Fact]
    public void ApplyAppearance_SizeOnlyChanged_Recreates() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                c.ApplyAppearance(new AppSettings { FontName = "Consolas", FontSize = 12f });
                var metrics = c.Metrics;
                int height = c.LineHeightPx;

                c.ApplyAppearance(new AppSettings { FontName = "Consolas", FontSize = 24f });

                Assert.NotSame(metrics, c.Metrics);
                Assert.True(
                    c.LineHeightPx > height,
                    $"行の高さが大きくならない({height} → {c.LineHeightPx})"
                );
            }
        });

    [Fact]
    public void ApplyAppearance_SameFontDifferentTheme_ReusesFontButAppliesTheme() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                c.ApplyAppearance(
                    new AppSettings
                    {
                        FontName = "Consolas",
                        FontSize = 15f,
                        Theme = "default",
                    }
                );
                var metrics = c.Metrics;
                Assert.Equal(Color.White.ToArgb(), c.BackColor.ToArgb()); // 前提

                c.ApplyAppearance(
                    new AppSettings
                    {
                        FontName = "Consolas",
                        FontSize = 15f,
                        Theme = "white-on-black",
                    }
                );

                Assert.Same(metrics, c.Metrics);
                Assert.Equal(Color.Black.ToArgb(), c.BackColor.ToArgb()); // テーマは反映される
            }
        });

    [Fact]
    public void ApplyAppearance_EmptyNameAndZeroSize_EqualToExplicitDefaults_Reuses() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                c.ApplyAppearance(new AppSettings { FontName = "ＭＳ ゴシック", FontSize = 12f });
                var metrics = c.Metrics;

                // 補完後は「ＭＳ ゴシック」12pt=同じフォントが作られるので使い回す。
                c.ApplyAppearance(new AppSettings { FontName = "", FontSize = 0f });

                Assert.Same(metrics, c.Metrics);
            }
        });
}
```

テーマ ID `default`(背景 0xFFFFFF)・`white-on-black`(背景 0x000000)は `src/kxEdit.Core/Settings/AppearanceTheme.cs` の `AppearanceThemes.All` にある。

- [ ] **Step 2: テストが落ちることを確かめる**

```powershell
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~ApplyAppearanceFontReuseTests"
```

Expected: `SameFontTwice_ReusesFontAndMetrics`・`SameFontDifferentTheme_...`・`EmptyNameAndZeroSize_...` の 3 本が `Assert.Same` で FAIL。残り 3 本は PASS(現行は毎回作り直すので)。

- [ ] **Step 3: 実装する**

`EditorControl.cs` の `_metrics` フィールドの直後に足す:

```csharp
    // 性能改善フェーズ 11(P-16 前半): 前回 ApplyAppearance で適用したフォントの要求値(既定値の補完後)。
    // 今回と同じならフォント 3 個と _metrics(幅メモ)を作り直さない=設定変更のたびに全タブの幅メモを捨てない。
    // null=まだ適用していない(ctor のフォントは要求値として記録しない=初回は必ず作り直す)。
    // _font.Name とは比べない(英語 UI では "MS Gothic"、存在しない名前ではフォールバック名が返り、毎回作り直しになる)。
    private FontRequest? _appliedFont;

    /// <summary><see cref="ApplyAppearance"/> が作るフォントの要求値(スタイルは常に Regular なので持たない)。</summary>
    private readonly record struct FontRequest(string Name, float Size);
```

`ApplyAppearance` のフォント差し替え部分(`var newFont = new Font(` から `_metrics = newMetrics;` まで)を次に置き換える:

```csharp
        // フォント差し替え + GdiCharMetrics 再構築(古い Font は明示的に Dispose して GDI HFONT リーク回避)。
        // 例外安全: newFont / newMetrics を両方作り切ってから旧 Font を Dispose する。
        // GdiCharMetrics のコンストラクタが throw した場合は newFont も破棄して呼び出し元へ propagate
        // (旧 _font / _metrics は生きたまま=次回 OnPaint も従前の高さで安全に描画できる。
        // _appliedFont も更新しない=次回は作り直しを試みる)。
        // フェーズ 11(P-16 前半): 要求値が前回と同じなら丸ごと飛ばす(同じ引数の new Font は同じフォント)。
        var request = new FontRequest(
            string.IsNullOrEmpty(settings.FontName) ? "ＭＳ ゴシック" : settings.FontName,
            settings.FontSize > 0 ? settings.FontSize : 12f
        );
        if (_appliedFont != request)
        {
            var newFont = new Font(request.Name, request.Size);
            GdiCharMetrics newMetrics;
            try
            {
                newMetrics = new GdiCharMetrics(newFont);
            }
            catch
            {
                newFont.Dispose();
                throw;
            }
            _font.Dispose();
            _underlineFontCache.Dispose();
            _targetFontCache.Dispose(); // Task 10
            _font = newFont;
            _underlineFontCache = new Font(_font, _font.Style | FontStyle.Underline);
            _targetFontCache = new Font(_font, _font.Style | FontStyle.Underline | FontStyle.Bold); // Task 10
            _metrics = newMetrics;
            _appliedFont = request;
        }
```

`ApplyAppearance` の doc コメントの「フォント:」の項に 1 行足す:

```csharp
    /// - フォント: 既存 Font を Dispose して新 Font に差し替え、<see cref="GdiCharMetrics"/> も再構築する
    ///   (LineHeightPx が変わるため後段の VScroll/HScroll 再計算とキャレット再配置が必須)。
    ///   前回適用した要求値(名前・サイズ。既定値の補完後)と同じなら差し替えない(性能改善フェーズ 11・P-16)。
```

- [ ] **Step 4: テストが通ることを確かめる**

```powershell
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~ApplyAppearanceFontReuseTests"
```

Expected: 6 本すべて PASS。

- [ ] **Step 5: commit し、commit 後の状態で全体を確かめる**

```bash
git add src/kxEdit.Editor/EditorControl.cs tests/kxEdit.Editor.Tests/ApplyAppearanceFontReuseTests.cs
git commit -m "perf(editor): 同じフォントなら ApplyAppearance でフォントと幅メモを作り直さない(P-16)"
```

```powershell
dotnet build -c Release
dotnet test tests/kxEdit.Editor.Tests -c Release
dotnet test tests/kxEdit.App.Tests -c Release
```

Expected: 0 warning、全件 PASS(既存の `ApplyAppearance_ResetsTopSegment`・`LastLineSegs_InvalidatesOnApplyAppearance`・`EditorControlSkipInvalidateTests` の `ApplyAppearance` ケースを含む)。

- [ ] **Step 6: 陰性対照**

`if (_appliedFont != request)` の `if` の行だけを消してブロックを常に実行する形(=変更前と同じく毎回作り直す)に書き換え、`-p:TreatWarningsAsErrors=false` でビルドしてテストを走らせる。

Expected: Step 2 と同じ 3 本だけが FAIL。確かめたら `git checkout -- src/kxEdit.Editor/EditorControl.cs` で戻し、`git status` がクリーンであることを確かめる。結果を実施記録 (2) に書く(Task 4 の commit に含める)。

---

## Task 3: P-7 配布物を ReadyToRun にする

**Files:**
- Modify: `.github/workflows/release.yml`(publish のステップ)
- Modify: `README.md`(「リリース」節)
- Modify: `tools/README.md`(perf-harness の publish の例に注記)

**Interfaces:**
- Produces: ReadyToRun の publish コマンド(Task 4 が使う):
  `dotnet publish src/kxEdit.App -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:DebugType=embedded -o <dir>`

- [ ] **Step 1: release.yml に足す**

```yaml
        run: >
          dotnet publish src/kxEdit.App -c Release -r win-x64 --self-contained false
          -p:PublishReadyToRun=true
          "-p:Version=$env:VERSION"
          -p:DebugType=embedded
          -o publish
```

- [ ] **Step 2: ローカルで同じ publish をして警告と同梱物を確かめる**

```powershell
$d = "<scratchpad>\perf-startup"
dotnet publish src/kxEdit.App -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:DebugType=embedded -o "$d\after\publish" 2>&1 | Tee-Object "$d\after-publish.log"
$LASTEXITCODE
Select-String -Path "$d\after-publish.log" -Pattern 'warning' 
Test-Path "$d\after\publish\WebView2Loader.dll"
```

Expected: EXIT 0、`warning` の行なし、WebView2Loader.dll が True。
自前の DLL が ReadyToRun になったことを確かめる:

```powershell
Add-Type -AssemblyName System.Reflection.Metadata
foreach ($n in 'kxEdit.dll','kxEdit.Editor.dll','kxEdit.Core.dll','kxEdit.Accessibility.dll') {
  $p = Join-Path "$d\after\publish" $n
  if (Test-Path $p) {
    $fs = [IO.File]::OpenRead($p); $pe = [Reflection.PortableExecutable.PEReader]::new($fs)
    "{0}: R2R={1}" -f $n, ($null -ne $pe.PEHeaders.CorHeader.ManagedNativeHeaderDirectory -and $pe.PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size -gt 0)
    $pe.Dispose(); $fs.Dispose()
  }
}
```

Expected: 列挙した DLL(存在するもの)がすべて `R2R=True`。実在の DLL 名は publish フォルダーで確かめて合わせる。

- [ ] **Step 3: README の「リリース」節に追記**

「配布物のアセンブリバージョンは…」の段落の後に:

```markdown
配布物は ReadyToRun(`-p:PublishReadyToRun=true`)で publish する(起動時の JIT を減らす。性能改善フェーズ 11)。
ci.yml と `tools/pre-merge-check.ps1` は publish しないので、publish の手順を変えたときは
ローカルで release.yml と同じ publish を実行し、警告が出ないこと(`-warnaserror`)と
`WebView2Loader.dll` が出力に含まれることを確かめる。配布物は publish の出力から作り、手でコピーしない。
```

- [ ] **Step 4: tools/README.md の perf-harness の節に注記**

publish の例の直後の箇条書きの先頭に:

```markdown
- 配布物と同じ条件で測るときは、publish に `-p:PublishReadyToRun=true -p:DebugType=embedded` を足す(release.yml と同じ。性能改善フェーズ 11 以降)。
```

- [ ] **Step 5: commit**

```bash
git add .github/workflows/release.yml README.md tools/README.md
git commit -m "perf(release): 配布物を ReadyToRun で publish する(P-7)"
```

---

## Task 4: 変更後の計測・起動確認・実施記録

**Files:**
- Modify: `docs/plans/2026-09-26-perf-startup.md`(本書末尾の実施記録)

- [ ] **Step 1: 変更後の S10 を 3 回測る**

```powershell
dotnet build -c Release
1..3 | ForEach-Object { dotnet run --project tests/kxEdit.Editor.Smoke -c Release --no-build -- --perf --scenario S10 --json "$d\after-s10-$_.json" }
```

Expected: 3 回とも EXIT 0。ja10k の median の中央値が、変更前の 3 run の min–max の下限を下回ること(設計書 §3.2)。

- [ ] **Step 2: 配布物の大きさ**

Task 3 Step 2 の publish を使う(作り直さない):

```powershell
Copy-Item -Recurse 説明書 "$d\after\publish\説明書"; Copy-Item 変更履歴.txt "$d\after\publish\変更履歴.txt"
Compress-Archive -Path "$d\after\publish\*" -DestinationPath "$d\after\kxEdit.zip"
(Get-ChildItem -Recurse -File "$d\after\publish" | Measure-Object Length -Sum).Sum; (Get-Item "$d\after\kxEdit.zip").Length
```

- [ ] **Step 3: 変更後の M-1 を 3 回測る(ユーザーの了承を取ってから)**

```powershell
1..3 | ForEach-Object { pwsh -File tools\perf-harness.ps1 -PublishDir "$d\after\publish" -Scenario M-1 -OutCsv "$d\after-m1-$_.csv" }
```

- [ ] **Step 4: 起動確認(0.4・ユーザーの了承を取ってから)**

1. `$d\after\kxEdit.zip` を別フォルダーへ展開し、`kxEdit.exe` を起動する。
2. Markdown の文書を開き、プレビューを表示する(WebView2Loader.dll の欠落があるとここで失敗する)。
3. 設定ダイアログでフォントサイズを変えて OK → 本文が大きくなる。もう一度、何も変えずに OK → 表示が崩れない・キャレットが行の高さと合っている。
4. 各段階を実解像度の画面取得(PrintWindow)で判定する。

- [ ] **Step 5: 実施記録を書いて commit**

本書末尾の「実施記録」に (1) 変更前、(2) 陰性対照、(3) 変更後と比較、(4) 起動確認 を書く。

```bash
git add docs/plans/2026-09-26-perf-startup.md
git commit -m "docs(perf): フェーズ 11 の変更後の計測値と起動確認"
```

---

## Task 5: 最終レビュー・品質ゲート・PR(CLAUDE.md §3 の 5・6、§7)

- [ ] **Step 1: 最終ブランチレビュー(2 パス・別エージェント)**: コード品質パスと脆弱性パス(`release.yml` の変更を含む)。指摘は fixup commit で反映し、3 択(修正 / 受容 / 却下)で実施記録に残す。
- [ ] **Step 2: 設計書 §16 の末尾に実施記録を追記**(PR 番号は PR 作成後に記入する commit を積む。フェーズ 10 と同じ扱い)。
- [ ] **Step 3: `tools/pre-merge-check.ps1` で EXIT 0**
- [ ] **Step 4: push → PR 作成**(日本語。変更前後の計測値・意図的な挙動差(配布物の増分の実測値)・起動確認の結果・レビュー経緯)

---

## 実施記録

### (1) 変更前の計測(Task 1)

計測対象コミット: `a8d9ff26`(Smoke `--perf` に S10 を追加した commit。製品コードはこのフェーズの
変更前のまま=`EditorControl.ApplyAppearance` はまだフォントを使い回さない)。

#### Smoke --perf S10

```powershell
1..3 | ForEach-Object { dotnet run --project tests/kxEdit.Editor.Smoke -c Release --no-build -- --perf --scenario S10 --json "<scratchpad>\perf-startup\before-s10-$_.json" }
```

3 回とも **EXIT 0**、`paints_per_op` はいずれも 1.00(自己チェック OK=描画を測れている)。
計測環境: DeviceDpi=96 / ClientSize=884x661 / LineHeightPx=16 / n=200 / warmup=20 / Release /
.NET 9.0.20 / Windows 10.0.26200(3 回とも同一)。

| doc | run | median_ms | min_ms | max_ms |
|---|---|---|---|---|
| ja10k | 1 | 10.223 | 9.374 | 12.638 |
| ja10k | 2 | 10.435 | 9.377 | 12.380 |
| ja10k | 3 | 10.336 | 9.358 | 12.654 |
| en10k | 1 | 8.230 | 7.502 | 10.533 |
| en10k | 2 | 8.397 | 7.723 | 10.542 |
| en10k | 3 | 8.403 | 7.720 | 10.513 |

- ja10k: median の中央値 = **10.336ms**、3 run 通した min–max = [9.358, 12.654]ms。
- en10k: median の中央値 = **8.397ms**、3 run 通した min–max = [7.502, 10.542]ms。

生データ: `<scratchpad>\perf-startup\before-s10-{1,2,3}.json`(commit しない)。

#### 配布物の大きさ(P-7 前=ReadyToRun 無し)

```powershell
dotnet publish src/kxEdit.App -c Release -r win-x64 --self-contained false -p:DebugType=embedded -o "<scratchpad>\perf-startup\before\publish"
Copy-Item -Recurse 説明書 "<scratchpad>\perf-startup\before\publish\説明書"; Copy-Item 変更履歴.txt "<scratchpad>\perf-startup\before\publish\変更履歴.txt"
Compress-Archive -Path "<scratchpad>\perf-startup\before\publish\*" -DestinationPath "<scratchpad>\perf-startup\before\kxEdit.zip"
```

- publish フォルダー: **3,385,683 バイト**
- zip(説明書・変更履歴を同梱): **1,143,191 バイト**

生成物: `<scratchpad>\perf-startup\before\publish`、`<scratchpad>\perf-startup\before\kxEdit.zip`(commit しない)。

#### perf-harness M-1(3 回・ユーザー承認済み)

各 CSV の `env` 行: `status=completed`(3/3)。`NVDA起動中=1.00`(NVDA を起動したまま計測。
ユーザー判断 2026-09-25 に基づく条件・設計書 §16.3)。

| 指標 | run1 | run2 | run3 | 3 run の median |
|---|---|---|---|---|
| 窓の表示まで (ms) | 177.02 | 173.84 | 174.32 | 174.32 |
| 入力受付まで (ms) | 193.00 | 196.19 | 196.25 | 196.19 |
| 0.8秒時点のCPU (ms) | 312.50 | 312.50 | 281.25 | 312.50 |
| ワーキングセット (MB) | 61.23 | 61.30 | 61.28 | 61.28 |

調査記録 §9.2 の値(入力受付 213 → 200 ms・NVDA なし)とは NVDA 有無の条件が異なるため単純比較しない。
変更前後の比較(Task 4)は同一条件(NVDA 起動中)で行う。

生データ: `<scratchpad>\perf-startup\before-m1-{1,2,3}.csv`(commit しない)。

### (2) 陰性対照(Task 2)

P-16(`fd2ccd86`)のテストが「フォントを使い回すこと」を実際に検出しているかを、実装を壊して確かめた。

- 壊し方: `EditorControl.ApplyAppearance` の `if (_appliedFont != request)` の 1 行だけを消し、
  フォントと幅メモを作り直すブロックが毎回走るようにした。
- 結果: 次の 3 件が **FAIL**(RED 時と同じ 3 件)、P-16 で追加した残り 3 件は PASS。
  - `ApplyAppearance_SameFontTwice_ReusesFontAndMetrics`
  - `ApplyAppearance_SameFontDifferentTheme_ReusesFontButAppliesTheme`
  - `ApplyAppearance_EmptyNameAndZeroSize_EqualToExplicitDefaults_Reuses`
- 確認後 `git checkout` で元に戻した(commit には残していない)。

### (3) 変更後の計測と比較(Task 3・4)

計測対象: Smoke S10 はブランチ HEAD `537feb12`(P-16 を含む。`537feb12` 自体は release.yml と
README のみの変更で製品コードは `fd2ccd86` と同じ)の Release ビルド。M-1 と配布物は Task 3 の
ReadyToRun publish(`<scratchpad>\perf-startup\after\publish`。CSV の `env` 行の版は
`0.2.0+fd2ccd86`)=P-7 と P-16 の両方を含むビルド(ただし起動には P-16 は効かない。下記「読み方と留保」)。

#### Smoke --perf S10

```powershell
dotnet build -c Release
1..3 | ForEach-Object { dotnet run --project tests/kxEdit.Editor.Smoke -c Release --no-build -- --perf --scenario S10 --json "<scratchpad>\perf-startup\after-s10-$_.json" }
```

3 回とも **EXIT 0**(各回の `$LASTEXITCODE` で確認)、`paints_per_op` はいずれも 1.00。計測環境は変更前と同一
(DeviceDpi=96 / ClientSize=884x661 / LineHeightPx=16 / n=200 / warmup=20 / Release / .NET 9.0.20 /
Windows 10.0.26200)。

| doc | run | median_ms | min_ms | max_ms |
|---|---|---|---|---|
| ja10k | 1 | 7.539 | 6.873 | 11.802 |
| ja10k | 2 | 7.338 | 6.806 | 9.970 |
| ja10k | 3 | 7.592 | 6.841 | 9.376 |
| en10k | 1 | 6.814 | 6.123 | 8.660 |
| en10k | 2 | 6.936 | 6.179 | 8.960 |
| en10k | 3 | 6.929 | 6.399 | 9.120 |

- ja10k: median の中央値 = **7.539ms**、3 run 通した min–max = [6.806, 11.802]ms。
- en10k: median の中央値 = **6.929ms**、3 run 通した min–max = [6.123, 9.120]ms。

生データ: `<scratchpad>\perf-startup\after-s10-{1,2,3}.json`(commit しない)。

#### 配布物の大きさ(P-7 後=ReadyToRun)

Task 3 の publish(`dotnet publish src/kxEdit.App -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:DebugType=embedded -o "<scratchpad>\perf-startup\after\publish"`)をそのまま使った(作り直していない)。

- Task 3 の確認結果: publish は `-v:n -tl:off` で crossgen2 が走ったこと・「0 個の警告」・**EXIT 0** を確認。
  `WebView2Loader.dll` は同梱されている。自前の `kxEdit.dll` / `kxEdit.Editor.dll` / `kxEdit.Core.dll` /
  `kxEdit.Accessibility.dll` はすべて R2R(`ManagedNativeHeaderDirectory` あり)。依存の `Markdig.dll` /
  `UtfUnknown.dll` も R2R になっていた。

```powershell
Copy-Item -Recurse 説明書 "<scratchpad>\perf-startup\after\publish\説明書"; Copy-Item 変更履歴.txt "<scratchpad>\perf-startup\after\publish\変更履歴.txt"
Compress-Archive -Path "<scratchpad>\perf-startup\after\publish\*" -DestinationPath "<scratchpad>\perf-startup\after\kxEdit.zip"
```

- publish フォルダー: **5,864,123 バイト**(説明書・変更履歴を含む。含めない値は 5,845,136 バイト)
- zip(説明書・変更履歴を同梱): **2,238,156 バイト**

注: 変更前の 3,385,683 バイトは説明書・変更履歴を**含めた**値(`before\publish` に同梱済みの状態で
集計)。比較は両方とも同梱後の値で行う(Task 3 の報告にある「5,845,136 との差 +2,459,453」は
同梱前後が混ざった比較のため採らない)。

#### perf-harness M-1(3 回・ユーザー承認済み)

```powershell
1..3 | ForEach-Object { pwsh -File tools\perf-harness.ps1 -PublishDir "<scratchpad>\perf-startup\after\publish" -Scenario M-1 -OutCsv "<scratchpad>\perf-startup\after-m1-$_.csv" }
```

3 回とも EXIT 0、各 CSV の `env` 行: `status=completed`(3/3)、`NVDA起動中=1.00`(変更前と同じ条件)、
`screen=1024x767`。計測前に kxEdit が起動していないことを確認した。プロファイルはハーネスが
退避・復元した(「プロフィールを復元しました(照合済み)」)。

| 指標 | run1 | run2 | run3 | 3 run の median |
|---|---|---|---|---|
| 窓の表示まで (ms) | 156.90 | 153.39 | 150.31 | 153.39 |
| 入力受付まで (ms) | 168.49 | 170.47 | 163.52 | 168.49 |
| 0.8秒時点のCPU (ms) | 218.75 | 234.38 | 218.75 | 218.75 |
| ワーキングセット (MB) | 58.09 | 58.05 | 58.06 | 58.06 |

生データ: `<scratchpad>\perf-startup\after-m1-{1,2,3}.csv`(commit しない)。

#### 変更前後の比較

判定は設計書 §3.2 のとおり、変更後の中央値が変更前の 3 run の min–max の下限を下回ったときだけ
「改善」とする。

| 項目 | 変更前(中央値 / 3 run の範囲) | 変更後(中央値 / 3 run の範囲) | 差 | 判定 |
|---|---|---|---|---|
| S10 ja10k median (ms) | 10.336 / [9.358, 12.654] | 7.539 / [6.806, 11.802] | −2.797(−27.1%) | 改善(下限 9.358 を下回る) |
| S10 en10k median (ms) | 8.397 / [7.502, 10.542] | 6.929 / [6.123, 9.120] | −1.468(−17.5%) | 改善(下限 7.502 を下回る) |
| M-1 窓の表示まで (ms) | 174.32 / [173.84, 177.02] | 153.39 / [150.31, 156.90] | −20.93(−12.0%) | 改善(下限 173.84 を下回る) |
| M-1 入力受付まで (ms) | 196.19 / [193.00, 196.25] | 168.49 / [163.52, 170.47] | −27.70(−14.1%) | 改善(下限 193.00 を下回る) |
| M-1 0.8秒時点のCPU (ms) | 312.50 / [281.25, 312.50] | 218.75 / [218.75, 234.38] | −93.75 | 改善(下限 281.25 を下回る) |
| M-1 ワーキングセット (MB) | 61.28 / [61.23, 61.30] | 58.06 / [58.05, 58.09] | −3.22 | 改善(下限 61.23 を下回る) |
| publish フォルダー(バイト・同梱物込み) | 3,385,683 | 5,864,123 | +2,478,440(+73.2%) | 意図的な増加(P-7) |
| zip(バイト) | 1,143,191 | 2,238,156 | **+1,094,965(+95.8%)** | 意図的な増加(P-7) |

読み方と留保:

- S10 は Smoke(ReadyToRun と無関係な通常の Release ビルド)で測っているので、差は P-16
  (同じフォントなら作り直さない)によるもの。
- M-1 の差は P-7(ReadyToRun)によるもの。計測条件 `session_restore=0` では起動時のタブは 1 つで、
  `ApplyAppearance` は `MainForm.CreateEditor` から 1 回だけ呼ばれ、必ず初回=作り直しになるため
  P-16 は効かない(§0.1)。調査記録の −13ms は NVDA なしの条件で、本フェーズ(NVDA 起動中)とは
  条件が異なるので大小を比べない。変更後の値の内訳(どの処理が何 ms 縮んだか)は切り分けていない。
  0.8 秒時点の CPU は OS の時間刻み(15.625ms)単位の値なので粒度が粗い。
- 変更前と変更後は同日の別時刻に同じ機械・同じ条件(NVDA 起動中)で測った。
- 配布物は zip で約 1.09MB(ほぼ 2 倍)増える。ReadyToRun がネイティブコードを IL に併載するための
  既知のトレードオフで、設計どおりの意図的な挙動差。

### (4) 起動確認(Task 4・ユーザー承認済み)

`<scratchpad>\perf-startup\after\kxEdit.zip` を新しいフォルダー `<scratchpad>\perf-startup\after\unzipped`
へ展開し、その `kxEdit.exe` を起動した(NVDA 起動中)。判定はすべて kxEdit のウィンドウを実解像度で
取得した PNG(PrintWindow。キャレットの確認のみ画面からの等倍取得を 4 倍に拡大)で行った。
画面取得は `<scratchpad>\perf-startup\startup-check\` に保存(commit しない)。

| 段階 | 操作 | 結果 | 画像 |
|---|---|---|---|
| 1 | 展開した `kxEdit.exe` を起動 | 「無題 1 - kxEdit」の窓が表示された | `01-launch.png` |
| 2 | Ctrl+O で見出し・箇条書き・リンクを含む Markdown(`startup-check.md`)を開く | 本文に内容が表示された(UTF-8 / LF) | `02-opened.png` |
| 3 | モード → マークダウンプレビュー(Ctrl+Shift+U) | 「プレビュー: startup-check.md - kxEdit」の窓に見出し 2 つ・箇条書き 3 項目・リンクが HTML として描画された(WebView2 が動作=`WebView2Loader.dll` の欠落なし)。閉じる(C) で閉じた | `03-preview.png`、`04-after-preview.png` |
| 4 | オプション → 設定 →[表示]→ 変更(F) でサイズを 12 → 20 にして OK、設定も OK | 設定のフォント表示が「ＭＳ ゴシック, 20.3 pt」になり、本文が大きく表示された。ステータスバーに「設定を適用しました」 | `08-fontdlg-20.png`、`09-settings-20.png`、`10-body-20pt.png` |
| 5 | もう一度設定を開き、何も変えずに OK | 本文の表示は崩れず段階 4 と同じ。キャレット(行 3・桁 18)は文字の上端から下端までの高さで描かれ、20pt の行の高さと合っていた | `11-settings-again.png`、`12-body-2nd-ok-1.png`、`14-caret-zoom-1.png` |
| 6 | 設定でサイズを 12 に戻して OK(後片付け) | 本文が元の大きさに戻り、`settings.json` の `FontSize` が 12 に戻った | `15-restore-fontdlg.png`、`16-restore-settings.png`、`17-body-restored-12pt.png` |

- 後片付け: kxEdit は保存せずに閉じた(文書は未変更)。ファイルを開いたことで `settings.json` の
  `RecentFiles` に試験文書が追加されたため、起動前に退避した `settings.json` で戻した
  (SHA-256 が起動前と一致することを確認)。
- 観察: フォントダイアログで 20 を選ぶと設定のフォント表示は「20.3 pt」になる。これはフォントダイアログが
  返す値によるもので、本フェーズの変更(`ApplyAppearance` の使い回し判定・ReadyToRun)とは関係しないと
  見ている(変更前のビルドでの再現は確認していない)。
- 自動操作の注意(記録): NVDA のスピーチビューアー(最前面・半透明)が kxEdit の窓の左側に重なるため、
  キャレットの確認は重ならない位置(行 3・桁 18)で行った。
