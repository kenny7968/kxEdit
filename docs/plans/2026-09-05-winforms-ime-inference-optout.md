# WinForms の IME モード推測を無効化する 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 起動時に WinForms の `ImeMode` 推測機構を無効化し、タブを閉じる / ダイアログ開閉のたびに
スクリーンリーダーが「文字変換」「変換停止」と読み上げる問題を解消する。

**Architecture:** WinForms は `ImeContext.GetImeMode()` が読む `ImeModeConversion` の変換テーブルからしか
「今の IME モード」を推測しない。`Control.PropagatingImeMode` の setter は `NoControl` / `Disable` を
記録しない仕様なので、テーブルの推測セルを `NoControl` で塗ると `s_propagatingImeMode` は永久に
`Inherit` のままとなり、`WmImeKillFocus` と `UpdateImeContextMode` が完全な no-op になる。
起動最初の 1 呼び出しで完結し、既存コードの変更は `Program.Main` の 1 行だけ。

**Tech Stack:** .NET 9 (net9.0-windows) / WinForms / xUnit / System.Reflection

**設計書:** [`2026-09-05-winforms-ime-inference-optout-design.md`](./2026-09-05-winforms-ime-inference-optout-design.md)
—— 根本原因・実測・採らなかった案・受容事項はすべてそちら。本書は手順のみ。

**ブランチ:** `feature/winforms-ime-optout`(main から切る)

---

## 前提知識(この計画を実行する人が知らないこと)

- **CLAUDE.md §1**: コミットメッセージ本文・コメント・xmldoc は日本語。識別子は英語。
- **CLAUDE.md §6**: `-warnaserror` が稼働中。警告 1 個でビルドが落ちる。
- **`InternalsVisibleTo`**: `src/kxEdit.App/kxEdit.App.csproj:24` で `kxEdit.App.Tests` に公開済み。
  新規クラスは `internal` でよく、csproj の変更は**不要**。
- **`catch (Exception ex)` は許容**されている(`BackupCoordinator` / `CrashHandler` に多数の前例)。
  アナライザで弾かれない。
- **テストの前提汚染**: 本機能はプロセス全体の static 配列を書き換える。テストが「塗られた」ことを
  観測するには、**先に非 `NoControl` の番兵値を書き込んでから**呼ぶこと。そうしないと
  「別のテストが先に塗っていたので緑」という順序依存の偽陽性になる(CLAUDE.md §4-B の
  「no-change のテストは非既定状態から始める」と同じ理由)。

---

## Task 1: `ImeStartup` 本体とテスト

**Files:**
- Create: `src/kxEdit.App/ImeStartup.cs`
- Create: `tests/kxEdit.App.Tests/ImeStartupTests.cs`

### Step 1: 失敗するテストを書く

`tests/kxEdit.App.Tests/ImeStartupTests.cs` を新規作成:

```csharp
using System;
using System.Linq;
using System.Reflection;

namespace kxEdit.App.Tests;

/// <summary>
/// WinForms の IME モード推測を無効化する起動処理を固定する
/// (設計 2026-09-05 §3)。
///
/// <para><b>なぜこれが要るか</b>: WinForms は <c>ImeContext.GetImeMode()</c> が読む
/// <c>ImeModeConversion</c> の変換テーブルからしか「今の IME モード」を推測しない。
/// 推測セルが常に <c>NoControl</c> を返せば <c>Control.PropagatingImeMode</c> の setter が
/// 値を記録しなくなり、<c>WmImeKillFocus</c> が IME を開いて閉じる動作(= SR の
/// 「文字変換」「変換停止」)ごと止まる。</para>
///
/// <para><b>テーブルへの到達経路は本番と別にしてある</b>: 本番はフィールド<b>型</b>で発見するが、
/// ここではフィールド<b>名</b>で引く。同じ経路で引くと「本番が探せていない」ことをテストも
/// 同時に見失う(循環)。.NET 更新で名前が変わればこのテストが赤くなり、CI が気づく。</para>
///
/// <para><b>本テストはプロセス全体の static 状態を変える</b>(製品と同じ状態にする)。
/// 順序依存の偽陽性を避けるため、各テストは<b>非 NoControl の番兵値を書いてから</b>呼ぶ。</para>
///
/// 実際の発声が消えたかは自動テストでは確認できない(CLAUDE.md §2 a11y 鉄則)。L5 で見る。
/// </summary>
public class ImeStartupTests
{
    private const BindingFlags Any =
        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    private static Type ConversionType =>
        typeof(Control).Assembly.GetType("System.Windows.Forms.ImeModeConversion")
        ?? throw new InvalidOperationException("ImeModeConversion が見つからない");

    /// <summary>本番と別経路(フィールド名)で日本語テーブルを引く。</summary>
    private static ImeMode[] JapaneseTable =>
        (ImeMode[])(
            ConversionType.GetField("s_japaneseTable", Any)?.GetValue(null)
            ?? throw new InvalidOperationException("s_japaneseTable が見つからない")
        );

    /// <summary>推測セルを非 NoControl の番兵で埋め、先頭 2 セルを既定値に戻す。</summary>
    private static void Arm(ImeMode[] table)
    {
        table[0] = ImeMode.Inherit;
        table[1] = ImeMode.Disable;
        for (int i = 2; i < table.Length; i++)
            table[i] = ImeMode.Off;
    }

    [Fact]
    public void 推測セルだけが_NoControl_に塗られる()
    {
        var table = JapaneseTable;
        Arm(table);
        Assert.Equal(ImeMode.Off, table[^1]); // 番兵が効いていることの確認(前提の検査)

        var result = ImeStartup.SuppressWinFormsImeModeInference();

        Assert.True(result.Succeeded, result.FailureReason);
        // 先頭 2 セルは構造的な値なので不変。ループ開始位置を 0 に変える変異はここで死ぬ。
        Assert.Equal(ImeMode.Inherit, table[0]);
        Assert.Equal(ImeMode.Disable, table[1]);
        // 末尾まで塗れていること。範囲を縮める変異はここで死ぬ。
        Assert.All(table.Skip(2), m => Assert.Equal(ImeMode.NoControl, m));
    }

    [Fact]
    public void 日本語テーブルが対象に含まれ_UnsupportedTable_は含まれない()
    {
        var result = ImeStartup.SuppressWinFormsImeModeInference();

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Contains("s_japaneseTable", result.PatchedTables);
        Assert.DoesNotContain(
            result.PatchedTables,
            n => n.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void 二回呼んでも成功する()
    {
        Arm(JapaneseTable);

        var first = ImeStartup.SuppressWinFormsImeModeInference();
        var second = ImeStartup.SuppressWinFormsImeModeInference();

        Assert.True(first.Succeeded, first.FailureReason);
        Assert.True(second.Succeeded, second.FailureReason);
        Assert.Equal(first.PatchedTables, second.PatchedTables);
    }

    [Fact]
    public void 型を解決できなくても例外を投げず失敗を返す()
    {
        var result = ImeStartup.SuppressWinFormsImeModeInference(null);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.PatchedTables);
    }

    [Fact]
    public void 変換テーブルを持たない型を渡しても失敗を返すだけで落ちない()
    {
        var result = ImeStartup.SuppressWinFormsImeModeInference(typeof(string));

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
    }
}
```

### Step 2: テストが失敗することを確認する

```
dotnet build kxEdit.sln -c Debug
```

期待: `ImeStartup` が存在しないため **CS0103 / CS0246 でコンパイルエラー**。
(この時点ではテスト実行まで到達しない。それが「失敗」の確認である)

### Step 3: 実装を書く

`src/kxEdit.App/ImeStartup.cs` を新規作成:

```csharp
using System.Diagnostics;
using System.Reflection;

namespace kxEdit.App;

/// <summary>
/// <see cref="ImeStartup.SuppressWinFormsImeModeInference()"/> の結果。
/// 成否とその根拠をテストから観測できるようにするために返す(void にすると
/// 「1 つも塗れていない」変異が緑のまま生き残る)。
/// </summary>
internal readonly record struct ImeSuppressionResult(
    bool Succeeded,
    IReadOnlyList<string> PatchedTables,
    string? FailureReason
)
{
    internal static ImeSuppressionResult Failed(string reason) => new(false, [], reason);
}

/// <summary>
/// WinForms の <c>ImeMode</c> 推測機構を起動時に無効化する(設計 2026-09-05)。
///
/// <para><b>なぜ要るか</b>: WinForms は <c>Control.WmImeKillFocus</c> で
/// <c>ImeContext.SetImeStatus(PropagatingImeMode, ...)</c> を呼ぶ。<c>PropagatingImeMode</c> が
/// <c>ImeMode.Off</c>(= ユーザーが IME を切っている通常状態)のとき、その実装は
/// <b>「一度 IME を開いてから閉じる」</b>ため <c>ImmSetOpenStatus</c> が 2 回走り、
/// 日本語スクリーンリーダーが「文字変換」「変換停止」と読み上げる。
/// タブを閉じたときとモーダルダイアログを閉じたときに条件が成立する(設計 §2.2)。</para>
///
/// <para><b>なぜテーブルを塗るのか</b>: WinForms が「今の IME モード」を<b>推測する入口は
/// <c>ImeModeConversion</c> の変換テーブル 1 つだけ</b>で、<c>Control.PropagatingImeMode</c> の
/// setter は <c>NoControl</c> と <c>Disable</c> を<b>記録しない</b>。推測結果が常に
/// <c>NoControl</c> になれば <c>s_propagatingImeMode</c> は永久に <c>Inherit</c> のままとなり、
/// <c>WmImeKillFocus</c> も <c>UpdateImeContextMode</c> も no-op になる。
/// コントロール単位の opt-out は<b>すべて無効だった</b>(実測・設計 §4)——
/// 状態がプロセス全体の static で、<c>TabControl</c> / <c>TabPage</c> / <c>Form</c> や
/// ダイアログ側のコントロールが供給源になるため。</para>
///
/// <para><b>この機構は Microsoft 自身が「Windows 8 以降 <c>ImeMode</c> は無視される」と
/// 文書化した遺物</b>(VB6 の <c>IMEMode</c> プロパティの移植)。それでも実装は残っていて
/// グローバルな IME 状態を実際に叩き続けている。</para>
///
/// <para><b>明示的な <c>ImeMode</c> 設定は生きている</b>: <c>ImeMode.Disable</c> は
/// 変換テーブルを経由しないため、数値入力欄の IME 無効化は従来どおり効く(実測)。</para>
/// </summary>
internal static class ImeStartup
{
    /// <summary>
    /// 塗り始める index。0 = <c>ImeMode.Inherit</c> / 1 = <c>ImeMode.Disable</c> は
    /// 「IME 状態から推測した値」ではなく構造的な定数なので<b>触らない</b>。
    /// ここを 0 にすると <c>ImeContext.GetImeMode</c> が「無効」を返せなくなる。
    /// </summary>
    private const int FirstInferredCell = 2;

    /// <summary>
    /// 本番の入口。<c>Program.Main</c> の<b>最初</b>で呼ぶ(ウィンドウを 1 つも作る前)。
    /// 遅れて呼ぶと、起動時に既に武装済みの分が 1 回だけ鳴る(実測)。
    /// </summary>
    internal static ImeSuppressionResult SuppressWinFormsImeModeInference() =>
        SuppressWinFormsImeModeInference(
            typeof(Control).Assembly.GetType("System.Windows.Forms.ImeModeConversion")
        );

    /// <summary>
    /// 型解決を外から差し替えられる seam(テストが異常系を駆動するため)。
    /// <paramref name="conversionType"/> が null / 想定外でも<b>例外を投げず</b>失敗を返す
    /// ——起動を止めないことが最優先で、失敗しても現状動作(発声が出る)に落ちるだけ。
    /// </summary>
    internal static ImeSuppressionResult SuppressWinFormsImeModeInference(Type? conversionType)
    {
        try
        {
            if (conversionType is null)
                return ImeSuppressionResult.Failed("ImeModeConversion 型を解決できない");

            const BindingFlags any =
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

            // フィールド名ではなく型で発見する(内部名が変わっても壊れにくくする)。
            var unsupported =
                conversionType.GetProperty("UnsupportedTable", any)?.GetValue(null) as ImeMode[];

            var patched = new List<string>();
            foreach (var field in conversionType.GetFields(any))
            {
                if (field.FieldType != typeof(ImeMode[]))
                    continue;
                if (field.GetValue(null) is not ImeMode[] table)
                    continue;
                // 空の UnsupportedTable(長さ 0)はここで落ちる。参照比較は二重の保険。
                if (table.Length <= FirstInferredCell)
                    continue;
                if (unsupported is not null && ReferenceEquals(table, unsupported))
                    continue;

                for (int i = FirstInferredCell; i < table.Length; i++)
                    table[i] = ImeMode.NoControl;
                patched.Add(field.Name);
            }

            return patched.Count == 0
                ? ImeSuppressionResult.Failed("ImeMode 変換テーブルが 1 つも見つからない")
                : new ImeSuppressionResult(true, patched, null);
        }
        catch (Exception ex)
        {
            // 起動処理なのでここで投げ返さない。握り潰した事実は呼び出し側が Trace へ落とす。
            return ImeSuppressionResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
```

### Step 4: テストが通ることを確認する

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter FullyQualifiedName~ImeStartupTests
```

期待: ビルド **0 warning / 0 error**、テスト **5 passed / 0 failed**。

> **落ちたら**: 「合格件数」まで必ず読むこと。アナライザ error でビルドが割れても
> テストランナーの exit code だけ見ていると気づけない(セッションメモリー
> `mutation-harness-exit-code-trap` の実測)。

### Step 5: コミット

```
git add src/kxEdit.App/ImeStartup.cs tests/kxEdit.App.Tests/ImeStartupTests.cs
git commit -m "feat(app): WinForms の IME モード推測を無効化する起動処理"
```

---

## Task 2: `Program.Main` へ配線する

**Files:**
- Modify: `src/kxEdit.App/Program.cs`(`Main` の先頭)

### Step 1: 呼び出しを足す

`Main` の**最初の文**として挿入する(`EncodingCatalog.EnsureRegistered()` より前)。

```csharp
    [STAThread]
    static void Main()
    {
        // 設計 2026-09-05: WinForms の ImeMode 推測機構を無効化する。放置すると、タブを閉じた
        // ときとモーダルダイアログを閉じたときに WinForms が IME を「開いてから閉じる」ため、
        // 日本語 SR が毎回「文字変換」「変換停止」と読み上げる(kxEdit 自身は IME の
        // ON/OFF を変える API を一切呼んでいない)。
        //
        // 位置の制約: ウィンドウを 1 つも作る前でなければならない。実測で、フォーム表示後に
        // 当てると起動時に既に武装済みの分が 1 回だけ鳴った。Application.SetUnhandledExceptionMode
        // が「Application.Run より前・かつウィンドウ生成前」なのと同種の理由で、ここが唯一の正しい位置。
        //
        // 失敗しても起動は止めない(現状動作＝発声が出る、に落ちるだけ)。.NET 更新で内部構造が
        // 変わったことに後から気づけるよう Trace に残す。ImeStartupTests が CI で先に気づく。
        var imeSuppression = ImeStartup.SuppressWinFormsImeModeInference();
        Trace.TraceInformation(
            imeSuppression.Succeeded
                ? $"kxEdit ime: WinForms ImeMode inference disabled ({string.Join(", ", imeSuppression.PatchedTables)})"
                : $"kxEdit ime: WinForms ImeMode inference NOT disabled: {imeSuppression.FailureReason}"
        );

        // Shift_JIS/EUC-JP を使うため CodePagesEncodingProvider を登録（Core も内部登録するが明示）。
        EncodingCatalog.EnsureRegistered();
```

`using System.Diagnostics;` は `Program.cs` に既にある(変更不要)。

### Step 2: ビルドと全テスト

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests   -c Release --no-build
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests    -c Release --no-build
```

期待: **0 warning**、3 プロジェクトとも **0 failed**。

> `Main` の配線そのものは自動テストで観測できない(`Main` は `[STAThread]` +
> `Application.Run` で叩けない。既存の `SetUnhandledExceptionMode` /
> `EncodingCatalog.EnsureRegistered` と同じ穴)。**受容済み**——回収点は L5 と Trace ログ。

### Step 3: コミット

```
git add src/kxEdit.App/Program.cs
git commit -m "feat(app): 起動時に WinForms の IME モード推測を無効化する"
```

---

## Task 3: L5 チェックリスト

**Files:**
- Create: `docs/plans/2026-09-05-winforms-ime-inference-optout-l5-checklist.md`

既存の `2026-09-04-settings-dialog-size-l5-checklist.md` と同じ体裁で、最低限これを含める。
**NVDA スピーチビューアーで逐語記録**する(セッションメモリー `save-encoding-loss-warning` で
確立した手法)。

| # | 手順 | 期待 |
|---|---|---|
| 1 | 半角/全角で IME を ON→OFF した直後、タブを 2 枚開いて `Ctrl+W` | 「文字変換」「変換停止」が**出ない** |
| 2 | 同じ前提で設計ダイアログを**開く** | 出ない(設計 §2.2 の未確定点。**出るかどうかを記録**する) |
| 3 | 設定ダイアログを**閉じる** | 出ない |
| 4 | 半角/全角キーを押す | 「日本語変換」/「変換停止」は**従来どおり鳴る**(潰していないこと) |
| 5 | 日本語を変換入力する | 未確定文字列・候補・確定が従来どおり読まれる |
| 6 | 日本語入力中に `Ctrl+G`(行へ移動)→ Esc → 編集領域 | **残存の確認**: 発声が出て、戻ると IME が切れている(設計 §6.2 の受容事項。**そのとおりなら PASS**) |
| 7 | CSV モードで F2 セル編集 → 確定 | 追加の発声が出ない |

### コミット

```
git add docs/plans/2026-09-05-winforms-ime-inference-optout-l5-checklist.md
git commit -m "docs(plans): IME 推測無効化の L5 チェックリスト"
```

---

## Task 4: 最終ブランチレビュー(CLAUDE.md §3-5)

規模が小さいので **2 パスを 1 回に統合してよい**(§3「簡略化の基準」)。
ただし**別エージェントによるレビューは省略しない**。観点:

- リフレクションの失敗経路がすべて「起動を止めない」に落ちているか(脆弱性/堅牢性パス)
- `FirstInferredCell = 2` の根拠がコードから読めるか。0 や 1 にした変異をテストが殺せるか
- テストが**順序依存の偽陽性**になっていないか(番兵を書く前に呼んでいないか)
- 設計書 §6 の受容事項が、コード側のコメントと食い違っていないか

指摘は CLAUDE.md §4 の 3 択(fixup / PR description に記載して受容 / 理由付き却下)で処理し、
**元 commit を書き換えず fixup commit で積む**。

---

## Task 5: 品質ゲートと PR(CLAUDE.md §6-7)

```
powershell -File tools\pre-merge-check.ps1
```

期待: **EXIT 0**。

その後 push → PR 作成。PR description(日本語)に必ず書くこと:

- 目的と根本原因(設計書へのリンク)
- **kxEdit 側に非は無く、.NET/WinForms の遺物機構を回避する変更である**こと
- 受容事項: 設計書 §6.1(明示的な能動 `ImeMode` の読み値退化)・§6.2(`ImeMode.Disable` の
  2 ダイアログを残す判断と、日本語入力中に戻ると IME が切れる件)
- 申し送り: §6.2 の撤去案(`ImeMode.Disable` → `NoControl` + 入力の全角→半角正規化)
- 申し送り: 設計書 §8(dotnet/winforms への feature request)
- **L5 の実施状況**(未実施ならその旨を明記。マージ可否はユーザー判断)
