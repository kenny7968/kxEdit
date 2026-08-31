# B1: `WordBoundary.maxScan` 契約表明の実態化 + 品質ゲートの Debug 構成復権 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** `WordBoundary` の `Debug.Assert(maxScan >= 1)`(実装の実態と食い違う表明)を削除して
Core.Tests を Debug 構成で緑にし、その上で `tools/pre-merge-check.ps1` と
`.github/workflows/ci.yml` に **Core / Editor / App 3 本の Debug 実行**を揃えて、
以後 B2〜B6 が足す `Debug.Assert` の網が最初からゲートに乗る状態にする。

**Architecture:** src のロジックは 1 行も変えない。変えるのは (1) `WordBoundary` の表明と
xmldoc、(2) 既存テストの陳腐化した `<remarks>`、(3) 2 つのゲート定義とその説明ドキュメント。
`Debug.Assert` は `[Conditional("DEBUG")]` なので **Release の実行時挙動は 1 命令も変わらない**。

**Tech Stack:** .NET 9 / C# / xunit / PowerShell 5.1 / GitHub Actions

**設計書:** `docs/plans/2026-08-08-wordboundary-maxscan-contract-design.md`
(§1〜§11 = 2026-08-08 スナップショット・**§12 = 2026-09-01 追記が正**)
**上位設計書:** `docs/plans/2026-08-31-v0.2-remaining-work-design.md` §4 の **B1**

**ブランチ:** `feature/wordboundary-maxscan-contract-v2`(main `67636bc` から分岐済み)

---

## 0. 前提の実測値(2026-09-01 / main `67636bc` で確認済み)

| 項目 | 値 |
|---|---|
| `dotnet test tests/kxEdit.Core.Tests -c Debug` | **失敗 4 / 合格 1336 / 計 1340** |
| 赤 4 件の内訳 | すべて `WordBoundaryTests.MaxScan_NonPositive_NeverRemovesScanLimit` の 4 InlineData |
| `dotnet test tests/kxEdit.Editor.Tests -c Debug --filter "Category!=LocalOnly"` | 失敗 0 / 合格 491(約 19 秒) |
| `src/kxEdit.Core/` の `Debug.Assert` | 8 サイト(`Buffer/TextSnapshot.cs` 4 / `Editing/WordBoundary.cs` 4) |
| `src/kxEdit.Editor/` の `Debug.Assert` | **0 件** |
| `src/kxEdit.App/` の `Debug.Assert` | 1 サイト(`DocumentState.cs:43`) |

**この計画で `WordBoundary.cs` の 4 サイトを削除するので、Core に残る Debug 網は
`TextSnapshot` の 4 サイトになる。**

## 1. やらないこと(スコープ外)

- `WordBoundary` のロジック変更。**1 行も変えない**。
- `WordBoundaryTests` の `Assert` / `InlineData` / fixture の変更。**1 文字も変えない**。
- `.github/workflows/release.yml` への Debug ステップ追加。先例 `7baa7f0` に倣い**足さない**
  (設計書 §12.4)。
- `PrevWordStart` 内の V-1 コメント(非正値クランプの根拠そのもの)の削除。**残す**。

## 2. L5(実機 SR 検証)の要否

**不要。** 設計書 §8 のとおり Release 挙動が不変で、`kxEdit.Accessibility` /
`EditorControl` の UIA 部 / App の Speech 系のいずれにも触れない
(上位設計書 §4.2 も「B1 = 不要」と判定済み)。

---

## Task 1: 設計書を commit する

**Files:**
- Modify(作成済み): `docs/plans/2026-08-08-wordboundary-maxscan-contract-design.md`

**Step 1: 追記が入っていることを確認**

```bash
grep -n "## 12. 2026-09-01 追記" docs/plans/2026-08-08-wordboundary-maxscan-contract-design.md
```

期待: `177:## 12. 2026-09-01 追記 — B1 着手時の精密化` 相当の 1 行がヒット。

**Step 2: commit**

```bash
git add docs/plans/2026-08-08-wordboundary-maxscan-contract-design.md
git commit -m "$(cat <<'EOF'
docs(plans): B1 設計書を持ち込み、2026-09-01 の精密化を追記

2026-08-08 に策定したまま未マージだった設計書(ローカルブランチ
feature/wordboundary-maxscan-contract)を main 系へ持ち込む。§1〜§11 は
策定時スナップショットのまま変更していない(CLAUDE.md §8)。

着手時に判明した差分を §12 として追記した:
- §9「ゲートは Release 一本のまま」は 2026-08-31 の傘設計書 §4.1 により撤回
- §7 の再発防止コメント文言は、ゲート拡張後は偽になるので差し替える
- Editor.Tests の Debug も足す(3 本揃える)= 2026-09-01 のユーザー判断
- release.yml には足さない(先例 7baa7f0)
- 旧パス yEdit.* は PR #39 で kxEdit.* に改名済み

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: `WordBoundary` から表明を削除し、規定挙動を xmldoc へ書く

**Files:**
- Modify: `src/kxEdit.Core/Editing/WordBoundary.cs`

**注意: すべて Edit ツールで行う。** Bash の heredoc / `sed -i` は日本語と BOM を壊す
(CLAUDE.md 環境ノート)。行番号は編集のたびにずれるので、**必ず文字列アンカーで置換**する。

**Step 1: クラス `<remarks>` の契約文を規定挙動へ差し替える(58 行目付近)**

置換前(1 行):

```
/// 消費される。契約は <c>maxScan &gt;= 1</c>(各 API 冒頭の <c>Debug.Assert</c> で検証)。
```

置換後(6 行):

```
/// 消費される。<b>推奨は <c>maxScan &gt;= 1</c></b>(新しい呼び出しで 0 以下を渡さないこと)。
/// ただし 0 以下は未定義動作ではなく<b>「予算を使い切った状態と同じ」</b>へ規定どおり縮退する
/// = DoS 対策の多重防御(2026-08-08 設計書 §4 / §5)。縮退時の返り値は
/// <c>NextWordStart</c> = <c>caret</c> / <c>WordEnd</c> = <c>pos</c> / <c>WordStart</c> = <c>pos</c> /
/// <c>PrevWordStart</c> = <c>caret</c> の <b>1 code point 左</b>(手順 2 の 1 歩だけは予算外なので
/// 「1 歩も走らない」と書くと <c>PrevWordStart</c> で嘘になる)。
```

**Step 2: `MaxScanContract` の定数ブロックを再発防止コメントへ差し替える(185-195 行目付近)**

置換前(11 行。`public const int DefaultMaxScan = 128;` の次の空行から
`/// <summary>次の単語の先頭に進む。...` の直前まで):

```csharp
    /// <summary>
    /// <c>maxScan &gt;= 1</c> 契約違反の <c>Debug.Assert</c> メッセージ。
    /// </summary>
    /// <remarks>
    /// 3 引数版の <c>Debug.Assert</c> を使うのは、2 引数版の message が
    /// <c>[CallerArgumentExpression]</c> 付きで明示指定が S3236 になるため
    /// (<c>TextSnapshot.DecodeUtf16At</c> と同じ流儀)。
    /// </remarks>
    private const string MaxScanContract =
        "maxScan は 1 以上でなければならない(0 以下は未規定=正規化しない)";
```

置換後:

```csharp
    // Debug.Assert(maxScan >= 1, ...) をここへ戻さないこと。実装 4 本は非正値をクラス
    // <remarks> の規定どおり縮退させており、食い違っていたのは表明の文言の側だった
    // (V-1 修正で「非正値を明示的に正規化する」を選んだのに、メッセージが「0 以下は未規定」の
    // まま取り残された = 申し送り S-5 / 2026-08-08 設計書 §2)。
    // 戻すと WordBoundaryTests.MaxScan_NonPositive_NeverRemovesScanLimit が Debug 構成で
    // 4 件赤になる。2026-09-01 以降は tools/pre-merge-check.ps1 と ci.yml の
    // 「Core.Tests(Debug・Debug.Assert 有効)」ステップが同じ赤を再現するので**ゲートで落ちる**。
```

**Step 3: `NextWordStart` の param doc と assert(206-211 行目付近)**

置換前(2 行・次行の「上限なしは」まで含めて一意にする):

```
    /// 走査上限(契約 <c>&gt;= 1</c>・窓はクラス <c>&lt;remarks&gt;</c> の表)。
    /// 上限なしは <see cref="NoScanLimit"/>。
```

置換後:

```
    /// 走査上限(<b>推奨 <c>&gt;= 1</c></b>・窓はクラス <c>&lt;remarks&gt;</c> の表。
    /// 0 以下の縮退も同 <c>&lt;remarks&gt;</c>)。上限なしは <see cref="NoScanLimit"/>。
```

続けて、その直下のメソッド本体 1 行目を削除する:

```csharp
        Debug.Assert(maxScan >= 1, MaxScanContract, nameof(maxScan));
        if (caret >= snap.CharLength)
```

→

```csharp
        if (caret >= snap.CharLength)
```

**Step 4: `PrevWordStart` の param doc と assert(253-258 行目付近)**

置換前:

```
    /// 走査上限(契約 <c>&gt;= 1</c>・窓はクラス <c>&lt;remarks&gt;</c> の表)。手順 2 の
    /// <b>最初の 1 歩も予算に数える</b>。上限なしは <see cref="NoScanLimit"/>。
```

置換後:

```
    /// 走査上限(<b>推奨 <c>&gt;= 1</c></b>・窓はクラス <c>&lt;remarks&gt;</c> の表)。手順 2 の
    /// <b>最初の 1 歩も予算に数える</b>(ただし 0 以下に縮退したときの 1 歩は予算外=
    /// クラス <c>&lt;remarks&gt;</c>)。上限なしは <see cref="NoScanLimit"/>。
```

assert 削除:

```csharp
        Debug.Assert(maxScan >= 1, MaxScanContract, nameof(maxScan));
        if (caret <= 0)
```

→

```csharp
        if (caret <= 0)
```

**`int budget = maxScan > 0 ? maxScan - 1 : 0;` の直前にある V-1 コメント(6 行)は残す。**

**Step 5: `WordStart` の param doc と assert(338-343 行目付近)**

置換前:

```
    /// 走査上限(契約 <c>&gt;= 1</c>・窓はクラス <c>&lt;remarks&gt;</c> の表=<b>左だけ 1 狭い</b>)。
```

置換後:

```
    /// 走査上限(<b>推奨 <c>&gt;= 1</c></b>・窓はクラス <c>&lt;remarks&gt;</c> の表=<b>左だけ 1 狭い</b>。
    /// 0 以下の縮退も同 <c>&lt;remarks&gt;</c>)。
```

assert 削除:

```csharp
        Debug.Assert(maxScan >= 1, MaxScanContract, nameof(maxScan));
        if (pos <= 0)
```

→

```csharp
        if (pos <= 0)
```

**Step 6: `WordEnd` の param doc と assert(373-379 行目付近)**

置換前:

```
    /// 走査上限(契約 <c>&gt;= 1</c>・窓はクラス <c>&lt;remarks&gt;</c> の表)。末尾空白の巻き戻しは
```

置換後:

```
    /// 走査上限(<b>推奨 <c>&gt;= 1</c></b>・窓はクラス <c>&lt;remarks&gt;</c> の表。
    /// 0 以下の縮退も同 <c>&lt;remarks&gt;</c>)。末尾空白の巻き戻しは
```

assert 削除:

```csharp
        Debug.Assert(maxScan >= 1, MaxScanContract, nameof(maxScan));
        if (pos >= snap.CharLength)
```

→

```csharp
        if (pos >= snap.CharLength)
```

**Step 7: `using System.Diagnostics;`(1 行目)を削除する**

**Step 8: 残存確認**

```bash
grep -n "Debug\.Assert\|MaxScanContract\|System\.Diagnostics\|契約 <c>&gt;= 1" src/kxEdit.Core/Editing/WordBoundary.cs
```

期待: **ヒット 0 件**。`Debug` の語が残っていてよいのは Step 2 の再発防止コメントと
テスト側だけ。ヒットがあれば置換漏れなので直す。

**Step 9: ビルドで 0 警告を確認**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
```

期待: `Build succeeded` / 0 Warning(s)。
`using` 削除で `Debug` の未解決が出たら Step 2 のコメント内に `Debug.` の**コード**が
混じっていないか確認する(コメント内なら影響しない)。

**Step 10: Debug と Release の両方で Core.Tests が緑になることを確認**

```bash
dotnet test tests/kxEdit.Core.Tests -c Debug
dotnet test tests/kxEdit.Core.Tests -c Release
```

期待: **両方とも 失敗 0 / 合格 1340 / 計 1340**。
Debug 側は §0 の「失敗 4 / 合格 1336」から 4 件が緑へ転じるだけで、**合計数は変わらない**。
合計が 1340 以外ならテストを消してしまっている。

**Step 11: commit**

```bash
git add src/kxEdit.Core/Editing/WordBoundary.cs
git commit -m "$(cat <<'EOF'
fix(core): WordBoundary の maxScan 契約表明を実装の実態へ合わせる(S-5)

public API 4 本の Debug.Assert(maxScan >= 1) は「0 以下は未規定=正規化しない」と
表明していたが、PR #36 の脆弱性パス V-1 で PrevWordStart は
`int budget = maxScan > 0 ? maxScan - 1 : 0;` へ修正され、非正値を明示的に
正規化する側を選んでいる。他 3 本も while (budget > 0) と PrevWordStart 委譲で
非正値に安全へ縮退する。食い違っていたのは表明の文言の側だった。

WordBoundaryTests.MaxScan_NonPositive_NeverRemovesScanLimit は実装の実態
(非正値でも上限が消えない)を正しく固定しており、Debug 構成でだけ 4 件赤に
なっていた(申し送り S-5)。

Debug.Assert 4 本と MaxScanContract、using System.Diagnostics を削除し、
非正値の縮退を「予算を使い切った状態と同じ」という規定挙動として xmldoc に
書いた。PrevWordStart だけは手順 2 の 1 歩が予算外なので 1 code point 左へ
動く点も明記(「1 歩も走らない」と書くと嘘になる)。削除跡に assert を戻させない
コメントを置いた。

Debug.Assert は [Conditional("DEBUG")] なので Release バイナリに呼び出しが
生成されておらず、Release の実行時挙動は 1 命令も変わらない。ロジック未変更。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: 陳腐化したテスト `<remarks>` を実態へ書き換え、EOF 経路の網を足す

> **2026-09-01 改訂(Task 2 の仕様レビュー Important-1 / 再レビュー Important-3 を受けて)**
> 当初この Task は「assert / InlineData / fixture は一切変更しない」を条件にしていた。
> しかし Task 2 のレビューで **`WordStart` の EOF 経路(`pos >= CharLength`)× `maxScan <= 0`
> はリポジトリ全体で無網**であることが判明し(既存 Theory は `pos=4000` / `CharLength=5000` で
> `pos + 1` 経路しか通していない)、設計書 §12.7 が「網は Task 3 で足す」と記録した。
> **その約束と本 Task の禁止条項が矛盾していた**ため、`Assert.` 行の追加を
> **EOF ケース 1 本に限って許可**する。これを緩めないと、穴が空いたまま
> 「塞いだ」と記録が残る=このリポジトリが繰り返し戒めている嘘の安全宣言になる。
> **既存の assert / InlineData / fixture の変更・削除は引き続き禁止。**

**Files:**
- Modify: `tests/kxEdit.Core.Tests/Editing/WordBoundaryTests.cs`(305-306 行目付近 + 既存 Theory 本体)

この 2 行は「ビルド / CI / ローカルゲートはすべて Release 構成なので発火しない」と書いており、
**Debug 構成で 4 件赤だった事実そのものを見落としている**。残すと同じ勘違いを再生産する。

**Step 1: `<remarks>` 末尾 2 行を置換**

置換前:

```
    /// <c>maxScan &lt;= 0</c> は <c>Debug.Assert</c> の契約違反でもあるが、ビルド / CI /
    /// ローカルゲートはすべて Release 構成(<c>Debug.Assert</c> は消える)なので発火しない。
```

置換後:

```
    /// 2026-09-01 まで <c>WordBoundary</c> は <c>maxScan &gt;= 1</c> を <c>Debug.Assert</c> で
    /// 表明しており、本 Theory の 4 ケースは <b>Debug 構成で赤</b>だった。ビルド / CI /
    /// ローカルゲートが Release 一本で <c>Debug.Assert</c> ごと消えていたため誰も踏まなかった
    /// (申し送り S-5)。表明の側が V-1 修正前の文言のまま取り残されていたので削除し、
    /// 非正値の縮退は <c>WordBoundary</c> のクラス <c>&lt;remarks&gt;</c> の規定挙動になった。
    /// 以後この Theory は <b>Debug / Release の両構成で緑</b>であり、両方がゲートで走る。
```

**Step 2: EOF 経路の網を 1 本足す**

既存 Theory `MaxScan_NonPositive_NeverRemovesScanLimit` の**末尾に assert を 1 本追加**する
(fixture `'a' × 5000` をそのまま使うので `CharLength = 5000`)。

```csharp
        // EOF 経路(pos >= CharLength)は PrevWordStart(pos) 委譲=pos + 1 を渡さないため、
        // 内部経路と違って pos ではなく pos の 1 code point 左になる。
        // 2026-09-01 の Task 2 仕様レビュー Important-1 で、この経路が無網のまま
        // xmldoc に「WordStart = pos」と書かれていたことが判明した(設計書 §12.7)。
        Assert.Equal(4999, WordBoundary.WordStart(snap, 5000, maxScan));
```

**既存の 4 assert・`[InlineData]` 4 本・`var snap = S(new string('a', 5000));` は
1 文字も変更しないこと。** 追加はこの 1 行(+ コメント)だけ。

**Step 3: 追加した網が本当に網として働くことを確かめる**

期待値を書いただけでは「網がある」と言えない。**わざと落ちることを 1 度見てから**進める。

```bash
# 期待値を 5000(訂正前の xmldoc が主張していた値)に一時的に変えて実行
dotnet test tests/kxEdit.Core.Tests -c Debug --filter "FullyQualifiedName~MaxScan_NonPositive"
```

期待: **4 件とも赤**になり、`Assert.Equal() Failure: Expected: 5000 / Actual: 4999` が出る。
赤にならなければ、その assert は何も固定していない。**確認後、必ず 4999 へ戻す。**

**Step 4: 本体の変更が追加 1 本だけであることを確認**

```bash
git diff -- tests/kxEdit.Core.Tests/Editing/WordBoundaryTests.cs
```

期待: 差分は `///` で始まる行と、**Step 2 で追加した assert 1 行 + そのコメント**だけ。
既存の `Assert.` / `[InlineData` / `var snap =` の行が **`-` 側に現れたら戻す**
(既存 fixture と既存 assert は不変が本タスクの条件)。

**Step 5: フォーマット検証とテスト**

```bash
dotnet csharpier check .
dotnet test tests/kxEdit.Core.Tests -c Debug
dotnet test tests/kxEdit.Core.Tests -c Release
```

期待: csharpier が無出力で終了。テストは**両構成とも 失敗 0 / 合格 1340 / 合計 1340**。
**合計は 1340 のまま変わらない**(既存 Theory のメソッド内に assert を足したので
テスト件数は増えない)。1341 以上になっていたら新しい `[Fact]` / `[Theory]` を
足してしまっている。

**Step 6: commit**

コミットメッセージはヒアドキュメントが壊れることがあるので、
スクラッチパッドにファイルとして書いて `git commit -F <file>` を使うこと
(CLAUDE.md 環境ノート)。

```
test(core): MaxScan_NonPositive に EOF 経路の網を足し、remarks を実態へ書き換える

remarks の「ビルド / CI / ローカルゲートはすべて Release 構成なので発火しない」は、
この Theory が Debug 構成で 4 件赤だった事実そのものを見落としていた。そのまま
残すと同じ勘違いを再生産するので、S-5 の経緯と「以後は両構成で緑・両方が
ゲートで走る」へ書き換える。

あわせて WordStart の EOF 経路(pos >= CharLength)の assert を 1 本足す。
Task 2 の仕様レビュー Important-1 で、この経路は pos + 1 を渡さないため
maxScan <= 0 のとき pos ではなく pos の 1 code point 左を返すこと、そして
リポジトリ全体で無網だったことが判明した(既存 Theory は pos=4000 /
CharLength=5000 で内部経路しか通していない)。xmldoc だけ直して網を足さないと
同じ穴が残る(設計書 §12.7)。

期待値を 5000 に変えると 4 件とも赤になることを確認済み(網として働いている)。
既存の 4 assert・InlineData 4 本・fixture は 1 文字も変更していない。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

---

## Task 4: ローカルゲートに Core / Editor の Debug 実行を足す

**Files:**
- Modify: `tools/pre-merge-check.ps1`

**このファイルは BOM 付き UTF-8**(Windows PowerShell 5.1 日本語ロケール対策)。
**Edit ツールで編集する**こと。`>` / `Out-File` で書き直すと BOM とエンコードを壊す。

**Step 1: `.SYNOPSIS` を実態へ(5 行目)**

置換前:

```
  加えて App.Tests を Debug でも走らせる(Debug.Assert 由来の網を有効にするため)。
```

置換後:

```
  加えて 3 テストプロジェクトすべてを Debug でも走らせる(Debug.Assert 由来の網を
  有効にするため)。
```

**Step 2: 44-54 行目のコメント + `App.Tests(Debug...)` ステップを差し替える**

置換前(コメント 8 行 + ステップ 3 行):

```powershell
# Issue #48 最終レビュー Q-I-4: 上の 3 ステップはすべて Release で、Debug.Assert は
# [Conditional("DEBUG")] のため Release バイナリに残らない。DocumentState.Path の不変条件
# (「null か正規化済み絶対パス」= 設計書 §3.1)を守る網は Debug.Assert なので、Release だけの
# ゲートでは 1 行も走らない = 「網に見えるがゲート上は無効」という嘘の安全宣言になっていた
# (実証: IsPathFullyQualified → IsPathRooted の変異が Release 全緑で生存・Debug では赤)。
# --no-build を付けないのは、上の Release ビルドとは別構成のバイナリが要るため。ビルドは
# App.Tests の依存グラフだけで sln 全体は二重に建てない(実測 約 35 秒)。
# Core.Tests の Debug は**足さない**: 既知 S-5 で 4 件赤になり、ゲートが常時 NG になる。
Invoke-Step 'App.Tests(Debug・Debug.Assert 有効)' {
    dotnet test (Join-Path $repoRoot 'tests/kxEdit.App.Tests') -c Debug
}
```

置換後:

```powershell
# Issue #48 最終レビュー Q-I-4: 上の 3 ステップはすべて Release で、Debug.Assert は
# [Conditional("DEBUG")] のため Release バイナリに残らない。DocumentState.Path の不変条件
# (「null か正規化済み絶対パス」= 設計書 §3.1)を守る網は Debug.Assert なので、Release だけの
# ゲートでは 1 行も走らない = 「網に見えるがゲート上は無効」という嘘の安全宣言になっていた
# (実証: IsPathFullyQualified → IsPathRooted の変異が Release 全緑で生存・Debug では赤)。
# --no-build を付けないのは、上の Release ビルドとは別構成のバイナリが要るため。
#
# 2026-09-01(B1): Core / Editor も Debug で走らせて 3 本を揃えた。旧コメントの
# 「Core.Tests の Debug は足さない(既知 S-5 で 4 件赤)」は S-5 の解消により失効
# (docs/plans/2026-08-08-wordboundary-maxscan-contract-design.md)。
# kxEdit.Editor 自身に Debug.Assert は現状 0 件だが、プロジェクト単位で歯抜けにしておくと
# 「assert を足したのにゲートステップを足し忘れる」= Q-I-4 が踏んだのと同じ失敗モードが
# 再発する。3 本揃えれば「どの層に assert を入れてもゲートに乗る」が構造で保証される。
# Release 側と同じくフィルタなし(LocalOnly も全件)で走らせる。
Invoke-Step 'Core.Tests(Debug・Debug.Assert 有効)' {
    dotnet test (Join-Path $repoRoot 'tests/kxEdit.Core.Tests') -c Debug
}
Invoke-Step 'Editor.Tests(Debug・Debug.Assert 有効)' {
    dotnet test (Join-Path $repoRoot 'tests/kxEdit.Editor.Tests') -c Debug
}
Invoke-Step 'App.Tests(Debug・Debug.Assert 有効)' {
    dotnet test (Join-Path $repoRoot 'tests/kxEdit.App.Tests') -c Debug
}
```

**Step 3: BOM が保たれていることを確認**

```bash
head -c 3 tools/pre-merge-check.ps1 | od -An -tx1
```

期待: ` ef bb bf`。違っていたら BOM を壊しているので復旧する。

**Step 4: Editor.Tests をフィルタなし Debug で単独確認**

ローカルゲートは `Category=LocalOnly` を除外しない。Editor.Tests には LocalOnly 実 I/O
テストが存在するので、**フィルタなしでも緑であること**を先に単独確認する
(§0 の実測は `--filter "Category!=LocalOnly"` 付きだった)。

```bash
dotnet test tests/kxEdit.Editor.Tests -c Debug
```

期待: 失敗 0。赤が出たら **B1 の範囲外の既存問題**なので、直す前に原因を切り分けて報告する
(Release 構成の同じテストが緑かどうかを必ず確かめる)。

**Step 5: ゲート全体を実行し、所要時間を記録する**

```bash
powershell -File tools/pre-merge-check.ps1
```

期待: `OK: pre-merge チェック全通過` と **EXIT 0**。
所要時間(体感でよい)を控えておく。Task 7 で PR description に書く。

**Step 6: commit**

```bash
git add tools/pre-merge-check.ps1
git commit -m "$(cat <<'EOF'
chore(tools): ローカルゲートに Core / Editor の Debug 実行を足す(B1)

App.Tests だけ Debug で走らせていた歯抜けを解消し、3 テストプロジェクトを
揃える。Core.Tests の Debug を外していた理由(既知 S-5 で 4 件赤)は本ブランチの
WordBoundary 修正で解消した。

kxEdit.Editor 自身に Debug.Assert は現状 0 件だが、プロジェクト単位で歯抜けに
しておくと「assert を足したのにゲートステップを足し忘れる」= Issue #48 の
Q-I-4 が踏んだのと同じ失敗モードが再発する。3 本揃えることで「どの層に assert を
入れてもゲートに乗る」を構造で保証する。B2〜B6 が Core に足す網を最初から
ゲートに乗せるのが B1 を先頭に置いた理由(2026-08-31 傘設計書 §4.1)。

Release 側と同じくフィルタなしで走らせる(LocalOnly も全件)。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: CI に同名ステップを足す

**Files:**
- Modify: `.github/workflows/ci.yml`(60-70 行目付近)

CLAUDE.md §6:「CI とローカルゲートに同種のステップを追加するときは**ステップ名を一致**させる」。
**Task 4 で付けた名前と 1 文字も違えないこと。**

**Step 1: コメント + ステップを差し替える**

置換前:

```yaml
      # Issue #48 最終レビュー Q-I-4: ここまでの 3 ステップはすべて Release で、Debug.Assert は
      # [Conditional("DEBUG")] のため Release バイナリに残らない。DocumentState.Path の不変条件を
      # 守る網は Debug.Assert なので、Release だけでは 1 行も走らない(実証: IsPathFullyQualified →
      # IsPathRooted の変異が Release 全緑で生存・Debug では赤)。
      # --no-build を付けないのは別構成のバイナリが要るため。ビルドは App.Tests の依存グラフだけ。
      # Core.Tests の Debug は足さない(既知 S-5 で 4 件赤)。
      # ステップ名は tools/pre-merge-check.ps1 と**完全一致**させる(CLAUDE.md §6: ローカル / CI の
      # 失敗ログを同じキーワードで探せるようにする)。App.Tests に LocalOnly は現状 0 件だが、
      # 上の Release 版と同じ除外を付けて将来追加時の CI 落ちを防ぐ。
      - name: App.Tests(Debug・Debug.Assert 有効)
        run: dotnet test tests/kxEdit.App.Tests -c Debug --filter "Category!=LocalOnly"
```

置換後:

```yaml
      # Issue #48 最終レビュー Q-I-4: ここまでの 3 ステップはすべて Release で、Debug.Assert は
      # [Conditional("DEBUG")] のため Release バイナリに残らない。DocumentState.Path の不変条件を
      # 守る網は Debug.Assert なので、Release だけでは 1 行も走らない(実証: IsPathFullyQualified →
      # IsPathRooted の変異が Release 全緑で生存・Debug では赤)。
      # --no-build を付けないのは別構成のバイナリが要るため。
      #
      # 2026-09-01(B1): Core / Editor も Debug で走らせて 3 本を揃えた。旧コメントの
      # 「Core.Tests の Debug は足さない(既知 S-5 で 4 件赤)」は S-5 の解消により失効。
      # kxEdit.Editor 自身に Debug.Assert は現状 0 件だが、歯抜けにしておくと「assert を
      # 足したのにゲートステップを足し忘れる」= Q-I-4 と同じ失敗モードが再発する。
      #
      # ステップ名は tools/pre-merge-check.ps1 と**完全一致**させる(CLAUDE.md §6: ローカル / CI の
      # 失敗ログを同じキーワードで探せるようにする)。LocalOnly 除外は上の Release 版と同じものを
      # 付ける(Core / App は現状該当 0 件だが将来追加時の CI 落ちを防ぐ)。
      - name: Core.Tests(Debug・Debug.Assert 有効)
        run: dotnet test tests/kxEdit.Core.Tests -c Debug --filter "Category!=LocalOnly"

      - name: Editor.Tests(Debug・Debug.Assert 有効)
        run: dotnet test tests/kxEdit.Editor.Tests -c Debug --filter "Category!=LocalOnly"

      - name: App.Tests(Debug・Debug.Assert 有効)
        run: dotnet test tests/kxEdit.App.Tests -c Debug --filter "Category!=LocalOnly"
```

**Step 2: ステップ名がローカルゲートと完全一致していることを機械的に確認**

```bash
grep -o "Core\.Tests(Debug・Debug\.Assert 有効)\|Editor\.Tests(Debug・Debug\.Assert 有効)\|App\.Tests(Debug・Debug\.Assert 有効)" tools/pre-merge-check.ps1 .github/workflows/ci.yml | sort | uniq -c
```

期待: 3 種類がそれぞれ **2 件ずつ**(ローカル 1 + CI 1)。
1 件しかない名前があれば綴りがずれている。

**Step 3: release.yml に手を入れていないことを確認**

```bash
git diff --name-only
```

期待: `.github/workflows/ci.yml` のみ。`release.yml` が出たら**戻す**(設計書 §12.4)。

**Step 4: commit**

```bash
git add .github/workflows/ci.yml
git commit -m "$(cat <<'EOF'
chore(ci): CI に Core / Editor の Debug 実行を足す(B1)

tools/pre-merge-check.ps1 と対称形にする。ステップ名は CLAUDE.md §6 に従い
ローカルゲートと完全一致させた(ローカル / CI の失敗ログを同じキーワードで
探せるようにするため)。LocalOnly 除外は上の Release 版と同じものを付ける。

release.yml には足さない(先例 7baa7f0 と同じ扱い。3 ファイル同期のコメントが
求めているのはテストプロジェクトの追加/削除であって構成の追加ではない)。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: ゲート説明ドキュメントを同期する

**Files:**
- Modify: `README.md`(112-115 行目付近)
- Modify: `tools/README.md`(9 行目 / 22-27 行目付近)

CLAUDE.md §8:「現在」を説明する文書だけが同期更新の対象。README / tools/README は該当する。

**Step 1: `README.md` の 2 箇所**

置換前:

```
Format check → Release ビルド (0 警告) → 3 テストプロジェクト全緑 → App.Tests を Debug で再実行、で PASS。

最後の Debug 実行は `Debug.Assert` (`[Conditional("DEBUG")]` で Release バイナリに残らない) を
使った網をゲートに載せるためのステップ。Core.Tests の Debug は既知の失敗があるため含めない。
```

置換後:

```
Format check → Release ビルド (0 警告) → 3 テストプロジェクト全緑 → 同じ 3 本を Debug で再実行、で PASS。

最後の Debug 実行は `Debug.Assert` (`[Conditional("DEBUG")]` で Release バイナリに残らない) を
使った網をゲートに載せるためのステップ。2026-09-01 に Core / Editor も揃えた
(Core を外していた理由の既知失敗 S-5 は解消済み。Editor 自身に `Debug.Assert` は無いが、
プロジェクト単位で歯抜けにすると「assert を足してゲートを足し忘れる」が再発するため)。
```

**Step 2: `tools/README.md` の一覧表(9 行目)**

置換前:

```
| `pre-merge-check.ps1` | main マージ前のローカルゲート(CSharpier check + Release 0 警告 + 全テスト緑 + App.Tests の Debug 実行) | **main マージ前 必須** |
```

置換後:

```
| `pre-merge-check.ps1` | main マージ前のローカルゲート(CSharpier check + Release 0 警告 + 全テスト緑 + 3 テストプロジェクトの Debug 実行) | **main マージ前 必須** |
```

**Step 3: `tools/README.md` の §1 手順(22-27 行目)**

置換前:

```
5. App.Tests を **Debug でもう一度**実行

5 が要る理由(Issue #48 最終レビュー Q-I-4): `Debug.Assert` は `[Conditional("DEBUG")]` なので
Release バイナリに残らない。`DocumentState.Path` の不変条件のように `Debug.Assert` で守っている網は、
Release だけのゲートでは 1 行も走らない=「網に見えるがゲート上は無効」になる。
**Core.Tests の Debug は含めない**(既知 S-5 で 4 件赤になり、ゲートが常時 NG になるため)。
```

置換後:

```
5. 同じ 3 テストプロジェクトを **Debug でもう一度**実行

5 が要る理由(Issue #48 最終レビュー Q-I-4): `Debug.Assert` は `[Conditional("DEBUG")]` なので
Release バイナリに残らない。`DocumentState.Path` の不変条件のように `Debug.Assert` で守っている網は、
Release だけのゲートでは 1 行も走らない=「網に見えるがゲート上は無効」になる。

2026-09-01 (B1) に **3 本すべて**へ揃えた。Core を外していた理由(既知 S-5 で 4 件赤)は
`WordBoundary` の `Debug.Assert` 削除で解消。`kxEdit.Editor` 自身に `Debug.Assert` は現状 0 件だが、
プロジェクト単位で歯抜けにすると「assert を足したのにゲートステップを足し忘れる」= Q-I-4 が
踏んだ失敗モードが再発するため揃えている。
```

**Step 4: 陳腐化した記述が残っていないことを確認**

```bash
grep -rn "Core.Tests の Debug は\|App.Tests を Debug で再実行\|App.Tests の Debug 実行" README.md tools/README.md tools/pre-merge-check.ps1 .github/workflows/ci.yml docs/lint-format-setup.md tests/README.md 2>/dev/null
```

期待: **ヒット 0 件**。1 件でも残れば「Core は Debug で走らない」という嘘が文書に残る。

**Step 5: commit**

```bash
git add README.md tools/README.md
git commit -m "$(cat <<'EOF'
docs: ゲート説明を 3 本 Debug 実行へ同期する(B1)

README.md と tools/README.md の「App.Tests だけ Debug」「Core.Tests の Debug は
含めない」という記述は、本ブランチのゲート変更で偽になる。実態へ揃える。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: ゲート強化の効果を実測する(嘘の安全宣言を作らない)

**Files:** src / テストの変更なし。**測定と記録だけ。**

**背景:** この PR は「ゲートを強化した」と主張する。だが `src/kxEdit.Editor/` の
`Debug.Assert` は 0 件、Core に残るのは `TextSnapshot` の 4 サイトだけで、しかも
`TextSnapshot.cs` の `<remarks>` 自身が「前提そのものは
`TextSnapshotGetCharEquivalenceTests.AssertEveryPieceIsWholeCodePoints` が全ピースについて
固定している」と書いている。**つまり「今日から新しく捕まるものが増えた」は成り立たない
可能性が高い。** 確かめずに書けば嘘の安全宣言になる。1 回の変異で決着させる。

**CLAUDE.md §4-A の適用:** 対象は `TextChunk.SplitStats`(バッファ内部の境界スナップ)で
禁止領域(GUI / キーバインド / テーマ / File I/O / プラグインロード)に当たらない。
**変異は 1 個だけ。** これ以上広げない。

**Step 1: 作業ツリーが clean であることを確認**

```bash
git status --short
```

期待: 出力なし。変異を戻し忘れて commit する事故を防ぐため、**必ず先に確認する**。

**Step 2: `TextChunk.SplitStats` の code point 歩幅を 1 箇所だけ壊す**

`src/kxEdit.Core/Buffer/TextChunk.cs` の `SplitStats` 内、`while (cum < target)` ループの
歩幅テーブルで `: b < 0xF0 ? 3` を `: b < 0xF0 ? 2` に変える(3 バイト文字の分割位置が
コードポイント途中に落ちる)。**この 1 文字だけ。**

**Step 3: Release で Core.Tests を走らせる**

```bash
dotnet test tests/kxEdit.Core.Tests -c Release
```

**Step 4: Debug で Core.Tests を走らせる**

```bash
dotnet test tests/kxEdit.Core.Tests -c Debug
```

**Step 5: 変異を戻し、戻ったことを確認する**

```bash
git checkout -- src/kxEdit.Core/Buffer/TextChunk.cs
git status --short
```

期待: 出力なし。**ここを飛ばすと変異が PR に混入する。**

**Step 6: 結果を解釈して記録する**

判定はあらかじめ決めておく。**どちらの結果でも「発見」であり、失敗ではない。**

| 観測 | 意味 | PR description に書くこと |
|---|---|---|
| Release 赤 / Debug 赤 | `TextSnapshot` の 4 assert は今日時点で **Release 網に対する追加の捕捉力を持たない**(純粋な多重防御) | 「今日から新しく捕まる網が増えたわけではない。B1 の価値は **B2〜B6 が足す網が最初からゲートに乗ること**と、歯抜けの再発防止という構造の側にある」 |
| Release 緑 / Debug 赤 | Debug ステップに**固有の捕捉力がある** | 「Core.Tests の Debug 追加でこの変異が捕まるようになった」と実測付きで書ける |
| Release 緑 / Debug 緑 | 変異が弱すぎて何も測れていない | 歩幅ではなく `GridIndexForChar` の返り値など別の 1 箇所へ変えて 1 回だけ再試行する |

Debug のビルドで `error` が出た場合は**古い DLL を叩いていないか**を必ず疑う。
検出には `grep -E " error [A-Z]+[0-9]+"` を使う(`grep "error CS"` は Sonar の
`error S###` を見落とす)。

**Step 7: 実測結果を設計書へ追記する**

`docs/plans/2026-08-08-wordboundary-maxscan-contract-design.md` の §12 末尾に
`### 12.7 ゲート強化の効果 — 実測` を足し、Step 3 / Step 4 の**生の結果**
(失敗数 / 合格数 / どのテストが落ちたか)と Step 6 の解釈を書く。
**「想定」と「実測」を物理的に分けて書く**こと。

**Step 8: commit**

```bash
git add docs/plans/2026-08-08-wordboundary-maxscan-contract-design.md
git commit -m "$(cat <<'EOF'
docs(plans): ゲート強化の効果を実測して §12.7 に記録(B1)

「Debug をゲートに足した = 今日から新しく捕まる」は確かめずに書けば嘘の安全
宣言になる。TextChunk.SplitStats の歩幅を 1 箇所だけ変異させ、Release / Debug の
両構成で Core.Tests を走らせて実測した。変異は戻してある。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: 最終レビュー・品質ゲート・PR

**Step 1: ブランチ全体の差分を確認する**

```bash
git diff main...HEAD --stat
```

期待するファイル(7 個):

```
.github/workflows/ci.yml
README.md
docs/plans/2026-08-08-wordboundary-maxscan-contract-design.md
docs/plans/2026-09-01-wordboundary-maxscan-contract.md
src/kxEdit.Core/Editing/WordBoundary.cs
tests/kxEdit.Core.Tests/Editing/WordBoundaryTests.cs
tools/README.md
tools/pre-merge-check.ps1
```

`src/kxEdit.Core/Buffer/TextChunk.cs` が出たら **Task 7 の変異が残っている**。戻す。

**Step 2: 挙動不変の証明を機械的に取る**

```bash
git diff main...HEAD -- src/kxEdit.Core/Editing/WordBoundary.cs | grep "^[+-]" | grep -v "^[+-][+-]" | grep -v "^[+-]\s*///" | grep -v "^[+-]\s*//"
```

期待: 出力は **`Debug.Assert(...)` 4 行の削除と `using System.Diagnostics;` 1 行の削除だけ**。
それ以外のコード行が出たらロジックを触っている。

**Step 3: 最終レビュー(別エージェント・1 パス統合)**

CLAUDE.md §3-5 は 2 パスを求めるが、本ブランチは「簡略化の基準」(単一ファイルの小変更 +
ゲート定義とドキュメント)に該当するので**コード品質パスと脆弱性パスを 1 回に統合**する。
**別エージェントの起動は省略しない。**

レビュー観点として渡すこと:

1. `WordBoundary` の xmldoc が §4 の表(4 API の縮退時の返り値)と**実装で一致**しているか。
   とくに `PrevWordStart` の「1 code point 左」。
2. ゲートのステップ名がローカル / CI で**完全一致**しているか。
3. README / tools/README に「Core.Tests の Debug は含めない」系の**陳腐化記述が残っていないか**。
4. Task 7 の記録が「実測」と「解釈」を分けて書けているか。**根拠が偽の主張が無いか。**
5. `TextChunk.cs` の変異が残っていないか。

指摘は CLAUDE.md §4 の 3 択(① fixup / ② PR に記載して受容 / ③ 理由付き却下)で明示する。
**修正は元 commit を書き換えず fixup commit で積む。**

**Step 4: 品質ゲート**

```bash
powershell -File tools/pre-merge-check.ps1
```

期待: `OK: pre-merge チェック全通過` と **EXIT 0**。
ドキュメントのみの変更ではないので**省略不可**。

**Step 5: push と PR**

```bash
git push -u origin feature/wordboundary-maxscan-contract-v2
```

PR description(日本語)に必ず書くこと:

- **目的**: B1 = v0.2 残 6 ブランチの先頭。S-5 の解消と、B2〜B6 が足す網を最初からゲートに
  乗せるための土台。
- **挙動不変の根拠**: `Debug.Assert` は `[Conditional("DEBUG")]` で Release バイナリに
  呼び出しが生成されない。Step 2 の diff 検査の結果を貼る。
- **Task 7 の実測結果**と、そこから言えること / **言えないこと**。
- **申し送り**:
  - 旧ローカルブランチ `feature/wordboundary-maxscan-contract`(318 commits 前)は
    設計書を取り込んだので**削除してよい**。
  - `release.yml` には Debug ステップを足していない(設計書 §12.4)。
  - 2026-08-08 設計書 §11 が挙げた残 PR #37 申し送り(S-1〜S-4 / S-6)は未回収のまま。
  - 上位設計書 §5 の「XML doc の `cref` がビルドで未検証(main に既存 CS1574 が 10 件)」は
    「B1(ゲート強化)の次の回で扱うのが自然」とされていた項目。**本ブランチでは扱っていない**。
- **L5**: 不要(設計書 §8 / 上位設計書 §4.2)。

---

## 9. 完了条件

- [ ] `dotnet test tests/kxEdit.Core.Tests -c Debug` が 失敗 0 / 合格 1340
- [ ] `dotnet test tests/kxEdit.Core.Tests -c Release` が 失敗 0 / 合格 1340
- [ ] `tools/pre-merge-check.ps1` が EXIT 0(Debug ステップ 3 本を含む)
- [ ] ローカルゲートと CI の Debug ステップ名が 3 種類 × 2 箇所で一致
- [ ] README / tools/README に「Core.Tests の Debug は含めない」系の記述が 0 件
- [ ] `WordBoundary.cs` の非コメント差分が assert 4 行 + using 1 行の削除のみ
- [ ] Task 7 の変異が作業ツリーに残っていない
- [ ] 別エージェントによる最終レビュー実施済み・指摘の 3 択を明示
- [ ] PR 作成済み
