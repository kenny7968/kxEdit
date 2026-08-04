# UIA 単語単位の境界ずれとコスト 修正 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 「単語の左端 / 右端」の定義を Core の文字クラス規則 1 本へ統合し(F-3)、単語走査に
キャレットを含む窓の上限を入れて空白ゼロ長大行の全走査を止める(F-5)。SR が読むスパンを
ダブルクリック単語選択と一致させる。

**Architecture:** `yEdit.Core.Editing.WordBoundary` に `WordStart` / `WordEnd` を新設し、既存の
`NextWordStart` / `PrevWordStart` を含む public API 4 本すべてに**必須引数 `maxScan`**(走査上限・
code point 数)を足す。`InputRouter` の private 2 メソッドと `UiaTextHostAdapter` の素朴実装を
削除してこの 1 本へ寄せる。`TextRangeProviderV2.ExpandToEnclosingUnit` は `WordEnd` の起点を
`_start` から `pos` へ変え、上限が効いた場合でもスパンがキャレットを含むようにする。

**Tech Stack:** .NET 9 / WinForms / xUnit / CSharpier / Husky.Net

**設計書:** `docs/plans/2026-08-03-uia-word-unit-design.md` §3(修正方針)
**調査の生データ:** 同 §2 / `tests/yEdit.Editor.Smoke --wordunit`

---

## この計画で確定させた事項(設計書 §3.3 の未確定 3 点)

2026-08-04 にユーザー判断で確定した。

| # | 論点 | 決定 |
|---|---|---|
| 1 | cap 値 | **実測してから決定**(Task 6)。実装は `maxScan` 引数で先に作り、定数値だけ最後に確定する |
| 2 | U+3000 を Whitespace クラスへ | **今回はスコープ外**。`ClassOf` は現状のまま(Other)。候補 A の副作用で §2.2 の逆向きずれは解消する |
| 3 | 移動側(Ctrl+←→ / UIA `Move`)への上限 | **入れる**。空白ゼロ長大行で UI スレッドが約 1.4 秒占有される問題も同時に潰す。キャレットが単語 run の途中で止まる**仕様変更**を伴う |

決定 3 により、本ブランチは設計書 §3.1 の 3 点に加えて**移動側の上限**を含む。
設計書は策定時スナップショット(CLAUDE.md §8)なので書き換えず、Task 0 で追記する。

---

## 前提知識(この計画を実行する人向け)

### 1. いま「単語」の実装が 3 つある

| # | 概念 | 実装 | 規則 | 利用者 |
|---|---|---|---|---|
| 1 | 次の単語の頭 | `src/yEdit.Core/Editing/WordBoundary.cs:60,97`(public) | 文字クラス 8 分類 | Ctrl+←→(`InputRouter.cs:160,171`)/ UIA `Move(Word)`(`TextRangeProviderV2.cs:303,312`) |
| 2 | 単語の左端 / 右端 | `src/yEdit.Editor/InputRouter.cs:539-568`(private) | 文字クラス(1 の組み合わせ) | ダブルクリック単語選択 |
| 3 | 単語の左端 / 右端 | `src/yEdit.Editor/UiaTextHostAdapter.cs:529-556`(private) | **空白 / CR / LF のみ** | SR の読み上げスパン(`TextRangeProviderV2.cs:58-64`) |

**2 と 3 が同じ概念の 2 実装で規則が違う**のが F-3 の正体。本計画は 2 を Core へ抽出し、3 を消す。

### 2. SR が読むスパンの決まり方

`TextRangeProviderV2.ExpandToEnclosingUnit(TextUnit.Word)`(`src/yEdit.Accessibility/TextRangeProviderV2.cs:58-64`):

```csharp
_start = host.WordStart(pos);
_end = host.WordEnd(_start);      // ← 起点が _start であることが後で効く
if (_end == _start)
    _end = host.NextChar(_start);
```

実機 NVDA がこの戻り値をそのまま読むことは設計書 §2.3 で確認済み。

### 3. 上限を「キャレットを含む窓」にしないと壊れる理由

単純に「`WordStart` は自分の引数から左へ cap 歩」「`WordEnd` は自分の引数から右へ cap 歩」とすると、
上の呼び順では `_start = pos - cap` → `_end = WordEnd(_start) ≤ _start + cap = pos` となり、
**スパンがキャレット位置の手前で終わる**(設計書 §2.5 の候補 B 欠陥 1)。

本計画は `_end = host.WordEnd(pos)` へ変えて解消する。こうすると
`WordStart(pos) ∈ [pos-cap, pos]` / `WordEnd(pos) ∈ [pos, pos+cap]` となり、窓が pos 中心になる。

**既存の quirk は維持される**: pos が空白の上にあるとき `WordEnd(pos) == pos` になりスパンは
キャレットを含まない(前の単語を読む)。これは上限とは無関係な**現行仕様**で、
`MouseInputTests.DoubleClick_OnWhitespace_SelectsPrevWordPlusWhitespaceRun` が固定している。
今回は変えない。

### 4. 本番コードへの到達方法(テスト)

```csharp
using var ctrl = new EditorControl();
ctrl.SetSource(TextBuffer.FromString("abc"));
var host = (IUiaTextHost)ctrl;
host.WordStart(0);   // → UiaTextHostAdapter の実装へ届く(Handle 生成も Form も不要)
```

典拠: `tests/yEdit.Editor.Tests/UiaTextHostAdapterClampTests.cs:19-22`。
Editor 層のテストは STA が要る(`Sta.Run(() => { ... })`。`MouseInputTests.cs` 参照)。

### 5. 触ると落ちる既存テスト(事前把握)

| テスト | 位置 | 本計画での扱い |
|---|---|---|
| `WordBoundaryTests`(13 件) | `tests/yEdit.Core.Tests/Editing/WordBoundaryTests.cs` | Task 1 で `NoScanLimit` を渡す機械的改修。**期待値は変えない**(挙動不変の網) |
| `MouseInputTests.DoubleClick_*`(2 件) | `tests/yEdit.Editor.Tests/MouseInputTests.cs:219,235` | Task 2 で**無改修のまま緑**であること = 挙動不変の証明 |
| `EditorControlUiaHostTests.Host_WordStart_UsesCoreWordBoundary` | `tests/yEdit.Editor.Tests/EditorControlUiaHostTests.cs:263` | `hello world` なので新旧一致。無改修で緑のはず |
| `TextRangeProviderV2Tests.ExpandToEnclosingUnit_Word_*` | `tests/yEdit.Core.Tests/Accessibility/TextRangeProviderV2Tests.cs:141` | stub host 経由。Task 4 の起点変更後も `hello world` pos=3 は `hello` で緑のはず |
| `tools/word-sim.ps1` | `tools/` | `prelude ABC abc 123 tail` は全 run が単一クラス = 期待値不変。Task 7 で実行確認 |

### 6. ビルド・品質の制約

- ソリューションは `-warnaserror`。**警告 0 が必須**。
- `tests/yEdit.Editor.Smoke/WordUnitBench.cs` も `yEdit.sln` に含まれる。Task 1 で
  `WordBoundary` のシグネチャを変えると**ここがコンパイルエラーになる**ので同時に直す。
- pre-commit フック(Husky.Net + CSharpier)を `--no-verify` で飛ばさない。
- コミットメッセージは `feat|fix|refactor|test|docs(scope): 要約` + 日本語本文 + 末尾に
  `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`。
  **Bash ツールで PowerShell here-string は使えない**。長文は一時ファイルへ書いて `git commit -F`。

### 7. レビュー体制(CLAUDE.md §3 / §4)

- 各タスク末に**別エージェント**の仕様レビュー。
- **Task 1 は前倒しコード品質レビュー**を追加する(後続 4 タスクが依存する新しい抽象を導入するため)。
- 最終は**コード品質パス / 脆弱性パスの 2 パス**を**別々のエージェント起動**で行う(Task 7)。
- 指摘は 3 択(① fixup commit ② PR description に記載して受容 ③ 理由付き却下)。元 commit は書き換えない。

---

## Task 0: ブランチ改名と既存文書への改訂追記

**Files:**
- Modify: `docs/plans/2026-08-03-uia-word-unit-triage-design.md`(冒頭へ追記)
- Modify: `docs/plans/2026-08-03-uia-word-unit.md`(冒頭へ追記)
- Modify: `docs/plans/2026-08-03-uia-word-unit-design.md`(§3.3 の決定を追記)
- Create: `docs/plans/2026-08-04-uia-word-unit-fix.md`(本書)

調査ブランチは「`src/` 変更ゼロ」を不変条件にしていた。本ブランチで実装まで行うと決めたので、
**その不変条件を撤回した事実を文書に残す**。CLAUDE.md §8 により本文は書き換えず、
日付つきの改訂ブロックを冒頭へ**追記**する。

### Step 1: ブランチが未 push であることを確認する

Run:
```
git rev-parse --abbrev-ref --symbolic-full-name '@{u}'
```
Expected: `fatal: no upstream configured`(= 未 push)。

**upstream があった場合は改名しない。** `git push origin :feature/uia-word-unit-triage` で
リモートを消してから改名するか、そのままの名前で続ける(その場合は Step 2 をスキップし、
PR description に「ブランチ名は調査時のまま」と書く)。

### Step 2: ブランチを改名する

Run:
```
git branch -m feature/uia-word-unit
git rev-parse --abbrev-ref HEAD
```
Expected: `feature/uia-word-unit`

### Step 3: 調査の設計書へ改訂ブロックを追記する

`docs/plans/2026-08-03-uia-word-unit-triage-design.md` のタイトル行(`# UIA 単語単位の…`)の直後、
`作成日:` 行の前に挿入する。

```markdown
> **2026-08-04 改訂(スコープ拡大)**: 本書 §3 の不変条件「`src/` 変更ゼロ」と §9 R2
> 「調査が実装に流れる」は、ユーザー判断により**撤回**された。同一ブランチで
> `docs/plans/2026-08-03-uia-word-unit-design.md` §3 の修正までを実施する。
> ブランチ名も `feature/uia-word-unit-triage` → `feature/uia-word-unit` へ改名した。
> 実装の計画は `docs/plans/2026-08-04-uia-word-unit-fix.md`。
> 本書の以降の記述は策定時スナップショット(CLAUDE.md §8)としてそのまま残す。
```

### Step 4: 調査の実装計画へ改訂ブロックを追記する

`docs/plans/2026-08-03-uia-word-unit.md` の `> **For Claude:**` 行の直後に挿入する。

```markdown
> **2026-08-04 改訂(スコープ拡大)**: 本書 Task 5 の DoD 「`src/` の差分が 0 行」と
> リスク表 R2 は**撤回**された。同一ブランチで実装まで行う。
> 実装の計画は `docs/plans/2026-08-04-uia-word-unit-fix.md`。
```

### Step 5: 成果物設計書へ「§3.3 の決定」を追記する

`docs/plans/2026-08-03-uia-word-unit-design.md` の末尾に節を足す。**§3.3 本文は書き換えない**。

```markdown
## 7. §3.3 未確定点の決着(2026-08-04 追記)

実装ブランチ(`feature/uia-word-unit`)の着手時にユーザー判断で確定した。
実装の計画は `docs/plans/2026-08-04-uia-word-unit-fix.md`。

| §3.3 の論点 | 決定 |
|---|---|
| cap 値 | 実測してから決定する。実装は `maxScan` 引数で先に作り、定数値だけ最後に確定する |
| クラス単位の刻みが快適か | L5 で確認する(方針は変えない) |
| `ClassOf` の U+3000 | **今回はスコープ外**。候補 A の副作用で §2.2 の逆向きずれは解消するが、Whitespace 化は別テーマ |

**§3.1 に対する追加**: 移動側(`NextWordStart` / `PrevWordStart` = Ctrl+←→ と UIA `Move`)にも
同じ上限を入れる。§2.4 のコストは SR だけでなく **UI スレッドの Ctrl+← にも乗っている**ため
(ascii 500K で約 1.4 秒)。キャレットが単語 run の途中で止まる仕様変更を伴う。
```

### Step 6: commit

```
git add docs/plans/
git commit -F <message-file>
```

メッセージ:
```
docs(plans): UIA 単語単位の修正 実装計画(スコープを実装へ拡大)

調査ブランチの不変条件「src/ 変更ゼロ」をユーザー判断で撤回し、同一ブランチで
F-3 + F-5 の修正まで行う。既存 3 文書は策定時スナップショットとして本文を残し、
冒頭に改訂ブロックを追記した。ブランチ名も feature/uia-word-unit へ改名。

設計書 §3.3 の未確定 3 点も確定させた: cap は実測後に決定・U+3000 はスコープ外・
移動側にも上限を入れる(Ctrl+← の UI スレッド占有も潰すため)。
```

---

## Task 1: Core に `WordStart` / `WordEnd` と走査上限を新設する

**Files:**
- Modify: `src/yEdit.Core/Editing/WordBoundary.cs`
- Modify: `tests/yEdit.Core.Tests/Editing/WordBoundaryTests.cs`(既存 13 件へ `NoScanLimit` を渡す)
- Modify: `tests/yEdit.Editor.Smoke/WordUnitBench.cs`(コンパイルエラー解消)
- Modify: `src/yEdit.Editor/InputRouter.cs:160,171`(呼び出しの引数追加のみ・**挙動は変えない**)
- Modify: `src/yEdit.Editor/UiaTextHostAdapter.cs:514,523`(同上)

このタスクは**挙動不変**。`maxScan` には全呼び出しで `NoScanLimit` を渡し、上限の実適用は Task 5 で行う。
新しい抽象を導入するので**前倒しコード品質レビュー**の対象(CLAUDE.md §3)。

### Step 1: 失敗するテストを書く

`tests/yEdit.Core.Tests/Editing/WordBoundaryTests.cs` の末尾へ追加する。

```csharp
    // ===== 2026-08-04 F-3 修正: WordStart / WordEnd(ダブルクリック単語選択と同一規則) =====

    /// <summary>
    /// 期待値は <c>InputRouter.PrevWordBoundary</c> / <c>NextWordBoundary</c>(移設元)の
    /// 現行挙動そのもの。<b>この表は挙動不変の網</b>なので、実装に合わせて書き換えてはならない。
    /// 生成手順は実装計画 Task 2 Step 1(反射で移設元を全 pos 照合)。
    /// </summary>
    [Theory]
    // "hello world": Latin run 2 つ + 空白 1
    [InlineData("hello world", 0, 0, 5)]
    [InlineData("hello world", 3, 0, 5)]
    [InlineData("hello world", 5, 0, 5)] // 空白の上 = 前単語(キャレットを含まない・現行仕様)
    [InlineData("hello world", 6, 6, 11)]
    [InlineData("hello world", 11, 6, 11)] // EOF
    // "今日は晴れです。": クラス境界で刻む(現状の空白規則なら [0,8) = 行全体だった)
    [InlineData("今日は晴れです。", 0, 0, 2)]
    [InlineData("今日は晴れです。", 1, 0, 2)]
    [InlineData("今日は晴れです。", 2, 2, 3)]
    [InlineData("今日は晴れです。", 3, 3, 4)]
    [InlineData("今日は晴れです。", 7, 7, 8)] // 句点 = Other
    // "abc123def": Latin / Digit / Latin
    [InlineData("abc123def", 0, 0, 3)]
    [InlineData("abc123def", 4, 3, 6)]
    [InlineData("abc123def", 8, 6, 9)]
    // "今日　は": 全角空白は Other クラス = それ自体が 1 単語(§2.2 の逆向きずれが解消する位置)
    [InlineData("今日　は", 2, 2, 3)]
    public void WordStart_WordEnd_MatchDoubleClickRule(string text, int pos, int start, int end)
    {
        var snap = TextBuffer.FromString(text).Current;
        Assert.Equal(start, WordBoundary.WordStart(snap, pos, WordBoundary.NoScanLimit));
        Assert.Equal(end, WordBoundary.WordEnd(snap, pos, WordBoundary.NoScanLimit));
    }

    [Fact]
    public void WordStart_WordEnd_Empty_ReturnsZero()
    {
        var snap = TextBuffer.FromString("").Current;
        Assert.Equal(0, WordBoundary.WordStart(snap, 0, WordBoundary.NoScanLimit));
        Assert.Equal(0, WordBoundary.WordEnd(snap, 0, WordBoundary.NoScanLimit));
    }

    // ===== 走査上限 =====

    [Fact]
    public void WordStart_WithMaxScan_StopsWithinWindow()
    {
        // 単一クラスの長大 run。上限がなければ 0 まで走る。
        var snap = TextBuffer.FromString(new string('a', 5000)).Current;
        int start = WordBoundary.WordStart(snap, 4000, maxScan: 100);
        Assert.InRange(start, 3900, 4000);
        Assert.True(start > 0, "上限が効かず行頭まで走っている");
    }

    [Fact]
    public void WordEnd_WithMaxScan_StopsWithinWindow()
    {
        var snap = TextBuffer.FromString(new string('a', 5000)).Current;
        int end = WordBoundary.WordEnd(snap, 1000, maxScan: 100);
        Assert.InRange(end, 1000, 1100);
        Assert.True(end < snap.CharLength, "上限が効かず行末まで走っている");
    }

    /// <summary>
    /// <c>ExpandToEnclosingUnit</c> の呼び順(<c>WordStart(pos)</c> → <c>WordEnd(pos)</c>)で
    /// 上限が効いても<b>スパンがキャレットを含む</b>こと。候補 B の欠陥 1 が構造的に起きないことの網。
    /// </summary>
    [Fact]
    public void WordSpan_WithMaxScan_ContainsCaret()
    {
        var snap = TextBuffer.FromString(new string('a', 500_000)).Current;
        const int Pos = 250_000;
        int start = WordBoundary.WordStart(snap, Pos, maxScan: 100);
        int end = WordBoundary.WordEnd(snap, Pos, maxScan: 100);
        Assert.True(start <= Pos, $"start={start} が pos を超えている");
        Assert.True(end > Pos, $"end={end} が pos を含んでいない(候補 B の欠陥 1)");
    }

    [Fact]
    public void NextWordStart_WithMaxScan_StopsMidRun()
    {
        var snap = TextBuffer.FromString(new string('a', 5000)).Current;
        int next = WordBoundary.NextWordStart(snap, 0, maxScan: 100);
        Assert.Equal(100, next);
    }

    [Fact]
    public void PrevWordStart_WithMaxScan_StopsMidRun()
    {
        var snap = TextBuffer.FromString(new string('a', 5000)).Current;
        int prev = WordBoundary.PrevWordStart(snap, 5000, maxScan: 100);
        Assert.InRange(prev, 4900, 4901);
    }

    /// <summary>上限は 1 呼び出し全体の予算。空白 run をまたいでも合算で頭打ちになる。</summary>
    [Fact]
    public void NextWordStart_MaxScan_IsBudgetAcrossRuns()
    {
        // "aaaa" + 空白 100 + "bbbb": 上限 10 なら空白 run の途中で止まる
        var snap = TextBuffer.FromString("aaaa" + new string(' ', 100) + "bbbb").Current;
        int next = WordBoundary.NextWordStart(snap, 0, maxScan: 10);
        Assert.Equal(10, next);
    }
```

### Step 2: テストが失敗することを確認する

Run:
```
dotnet test tests/yEdit.Core.Tests -c Release --filter "FullyQualifiedName~WordBoundary"
```
Expected: **コンパイルエラー**(`WordStart` / `NoScanLimit` が存在しない)。これが赤。

### Step 3: `WordBoundary` を実装する

`src/yEdit.Core/Editing/WordBoundary.cs` を次のとおり変更する。

**(a) クラス冒頭に定数を足す**(`public static class WordBoundary {` の直後):

```csharp
    /// <summary>
    /// 走査上限なしを表す番兵。<b>新しい本番呼び出しでこれを使ってはならない</b> —
    /// 上限なしの走査は空白ゼロ長大行で行全体を舐める(2026-08-03-uia-word-unit-design.md §2.4)。
    /// テストと、上限を意図的に外すことに理由がある場所だけが使う。
    /// </summary>
    public const int NoScanLimit = int.MaxValue;

    /// <summary>
    /// 単語走査の既定上限(code point 数)。1 回の呼び出しがこの歩数を超えて走らない。
    /// SR の読み上げスパン(<c>UiaTextHostAdapter</c>)と Ctrl+←→(<c>InputRouter</c>)の両方が使う。
    /// </summary>
    /// <remarks>
    /// 値の根拠は docs/plans/2026-08-04-uia-word-unit-fix.md Task 6 の実測。
    /// 上限に当たると単語の途中で切れる = SR は run の一部だけを読み、キャレットも run の
    /// 途中で止まる。これは「500K 文字を 1 単語として読ませない」ための意図的な打ち切りである。
    /// </remarks>
    public const int DefaultMaxScan = 256; // Task 6 で確定させる暫定値
```

**(b) `NextWordStart` / `PrevWordStart` に `maxScan` を足す**(既存メソッドを置き換え):

```csharp
    /// <summary>次の単語の先頭に進む。EOF に達したら CharLength を返す。</summary>
    /// <param name="maxScan">
    /// 1 呼び出しで進める最大 code point 数。上限に当たった位置で打ち切る
    /// (= 単語の途中で止まる)。上限なしは <see cref="NoScanLimit"/>。
    /// </param>
    /// <remarks>
    /// 動作:
    /// 1. caret が CharLength なら CharLength を返す(EOF)
    /// 2. 現在位置の class が Whitespace/LineBreak → その連続をスキップして到達位置を返す
    /// 3. 現在位置の class が非空白 → 同 class の連続をスキップ → その先の空白/改行連続もスキップして到達位置を返す
    ///
    /// <paramref name="maxScan"/> は 2 / 3 のスキップを**通した合計予算**である。
    /// 単語 run で使い切れば空白 run へは 1 歩も入らない。
    /// </remarks>
    public static int NextWordStart(TextSnapshot snap, int caret, int maxScan)
    {
        if (caret >= snap.CharLength)
            return snap.CharLength;
        int budget = maxScan;
        int pos = caret;
        var start = ClassOf(snap, pos);
        if (start == CharClass.Whitespace || start == CharClass.LineBreak)
        {
            pos = SkipForwardWhile(
                snap,
                pos,
                cls => cls == CharClass.Whitespace || cls == CharClass.LineBreak,
                ref budget
            );
        }
        else
        {
            pos = SkipForwardWhile(snap, pos, cls => cls == start, ref budget);
            pos = SkipForwardWhile(
                snap,
                pos,
                cls => cls == CharClass.Whitespace || cls == CharClass.LineBreak,
                ref budget
            );
        }
        return pos;
    }

    /// <summary>前の単語の先頭に戻る。BOF に達したら 0 を返す。</summary>
    /// <param name="maxScan">
    /// 1 呼び出しで戻れる最大 code point 数。上限なしは <see cref="NoScanLimit"/>。
    /// 最初の 1 歩(caret から 1 code point 左へ)も予算に数える。
    /// </param>
    /// <remarks>
    /// 動作:
    /// 1. caret が 0 なら 0 を返す
    /// 2. 1 code-point 左へ移動(サロゲート考慮)
    /// 3. 空白/改行の後方連続をスキップ(pos が非空白 class に到達するまで左へ)
    /// 4. その class の後方連続をスキップ(左隣が同 class の間、左へ)
    /// 5. 到達位置を返す
    /// </remarks>
    public static int PrevWordStart(TextSnapshot snap, int caret, int maxScan)
    {
        if (caret <= 0)
            return 0;
        int budget = maxScan;
        int pos = TextBoundary.PrevCodePoint(snap, caret);
        budget--;
        // 左隣を空白/改行としてスキップ(後方=空白の直前まで)
        while (budget > 0 && pos > 0)
        {
            var cls = ClassOf(snap, pos);
            if (cls != CharClass.Whitespace && cls != CharClass.LineBreak)
                break;
            pos = TextBoundary.PrevCodePoint(snap, pos);
            budget--;
        }
        // 位置 pos の class を単語 class として、その連続をさらに左へ
        var wordCls = ClassOf(snap, pos);
        while (budget > 0 && pos > 0)
        {
            int prev = TextBoundary.PrevCodePoint(snap, pos);
            if (ClassOf(snap, prev) != wordCls)
                break;
            pos = prev;
            budget--;
        }
        return pos;
    }
```

**(c) `WordStart` / `WordEnd` を新設する**(`// ===== ヘルパ =====` の直前へ):

```csharp
    /// <summary>
    /// <paramref name="pos"/> を含む単語の左端。ダブルクリック単語選択と SR の読み上げスパンが共有する。
    /// </summary>
    /// <remarks>
    /// <b>規則は <see cref="PrevWordStart"/> の組み合わせで表現される</b>(新しい規則を発明しない)。
    /// <c>pos + 1</c> を渡すことで「pos 自身を含むクラス連続の左端」になる。
    /// 2026-08-04 に <c>InputRouter.PrevWordBoundary</c> から bit-perfect 移設した。
    ///
    /// pos が空白の上にあるときは左の空白 run を越えて**前の単語の頭**を返す(= スパンが
    /// キャレットを含まない)。これは移設元からの現行仕様で、
    /// <c>MouseInputTests.DoubleClick_OnWhitespace_SelectsPrevWordPlusWhitespaceRun</c> が固定している。
    /// </remarks>
    public static int WordStart(TextSnapshot snap, int pos, int maxScan)
    {
        if (pos <= 0)
            return 0;
        if (pos >= snap.CharLength)
            return PrevWordStart(snap, pos, maxScan);
        return PrevWordStart(snap, pos + 1, maxScan);
    }

    /// <summary>
    /// <paramref name="pos"/> の word run の終端。末尾の空白は含めない。
    /// </summary>
    /// <remarks>
    /// <see cref="NextWordStart"/> は「単語末尾 + 空白列をスキップして次単語の頭」を返すため、
    /// 返り値から左へ戻して空白/改行以外の最初の位置を求める。後方スキャンは
    /// <c>nextWordStart &gt; pos</c> でガードするので pos より左には決して戻らない。
    /// 2026-08-04 に <c>InputRouter.NextWordBoundary</c> から bit-perfect 移設した。
    /// </remarks>
    public static int WordEnd(TextSnapshot snap, int pos, int maxScan)
    {
        if (pos >= snap.CharLength)
            return snap.CharLength;
        int nextWordStart = NextWordStart(snap, pos, maxScan);
        while (nextWordStart > pos)
        {
            char c = snap.GetChar(nextWordStart - 1);
            if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                break;
            nextWordStart--;
        }
        return nextWordStart;
    }
```

**(d) `SkipForwardWhile` に予算を通す**:

```csharp
    /// <summary>pred が真の間、code-point 単位で右へ進む。予算 <paramref name="budget"/> を消費する。</summary>
    private static int SkipForwardWhile(
        TextSnapshot snap,
        int pos,
        Func<CharClass, bool> pred,
        ref int budget
    )
    {
        while (budget > 0 && pos < snap.CharLength && pred(ClassOf(snap, pos)))
        {
            pos = TextBoundary.NextCodePoint(snap, pos);
            budget--;
        }
        return pos;
    }
```

**(e) クラス xmldoc に上限の説明を足す**(`<remarks>` の末尾へ):

```csharp
/// 2026-08-04: public API 4 本すべてに走査上限 <c>maxScan</c> を必須引数として足した。
/// 空白ゼロの長大行で 1 回の走査が行全体(500K 文字で約 1.4〜2.8 秒)を舐めるのを止めるため
/// (docs/plans/2026-08-03-uia-word-unit-design.md §2.4)。省略可能引数にしていないのは、
/// 上限なしを**明示的に選ばせる**ため(既定が無制限だと新しい呼び出しが黙って無制限になる)。
```

### Step 4: 呼び出し元をコンパイルが通る最小限で直す(**挙動は変えない**)

`NoScanLimit` を渡すだけ。上限の実適用は Task 5。

- `src/yEdit.Editor/InputRouter.cs:160` → `WordBoundary.PrevWordStart(snap, ctx.Caret.Caret, WordBoundary.NoScanLimit)`
- `src/yEdit.Editor/InputRouter.cs:171` → `WordBoundary.NextWordStart(snap, ctx.Caret.Caret, WordBoundary.NoScanLimit)`
- `src/yEdit.Editor/InputRouter.cs:544,545,559` → 同様に `WordBoundary.NoScanLimit` を追加
- `src/yEdit.Editor/UiaTextHostAdapter.cs:514,523` → 同様
- `tests/yEdit.Core.Tests/Editing/WordBoundaryTests.cs` の既存 13 件 → 第 3 引数に `WordBoundary.NoScanLimit`
- `tests/yEdit.Editor.Smoke/WordUnitBench.cs` の `WordBoundary.*WordStart` 呼び出し → 同様

**期待値は 1 つも変えない。** ここで期待値を変える必要が出たら、それは上限実装のバグである。

### Step 5: テストが通ることを確認する

Run:
```
dotnet test tests/yEdit.Core.Tests -c Release --filter "FullyQualifiedName~WordBoundary"
```
Expected: 全件 PASS(新規 8 件 + 既存 13 件)。

Run:
```
dotnet build yEdit.sln -c Release
```
Expected: 警告 0・エラー 0。

### Step 6: 全層のテストを走らせて挙動不変を確認する

Run:
```
dotnet test yEdit.sln -c Release
```
Expected: 全件 PASS。**1 件でも赤なら Task 1 の「挙動不変」が破れている** — 上限の予算計算を疑う。

### Step 7: commit

```
feat(core): WordBoundary に WordStart / WordEnd と走査上限を新設する

「単語の左端 / 右端」の定義は現在 InputRouter(ダブルクリック・文字クラス規則)と
UiaTextHostAdapter(SR 読み上げ・空白のみ規則)に 2 実装あり規則が食い違っている。
統合先となる 1 本を Core へ用意する。規則は既存のダブルクリック側から bit-perfect
移設し、新しい規則は発明していない。

あわせて public API 4 本に走査上限 maxScan を必須引数で足した。省略可能にしないのは
上限なしを明示的に選ばせるため。本 commit では全呼び出しが NoScanLimit を渡すので
挙動は変わらない(上限の適用は後続タスク)。
```

### Step 8: 仕様レビュー + **前倒しコード品質レビュー**(別エージェント・2 起動)

CLAUDE.md §3 の前倒し例外「後続タスクが依存する新しい抽象を導入する」に該当する。

**仕様レビューの観点:**
- `WordStart` / `WordEnd` が `InputRouter.PrevWordBoundary` / `NextWordBoundary`(移設元)と
  bit-perfect か。ガード条件・境界の分岐を 1 行ずつ突き合わせる
- `maxScan` が「1 呼び出し全体の予算」になっているか(run をまたいで合算されるか)
- `NoScanLimit` を渡したとき、変更前の実装と完全に等価か

**コード品質レビューの観点:**
- 必須引数化の是非(呼び出し側の可読性 vs 誤用防止)
- `budget--` の位置(`PrevWordStart` の最初の 1 歩を数えるのは妥当か)
- 上限に当たったことを呼び出し側が知る必要はないか(現状は返り値だけでは区別できない)

指摘を fixup commit で反映してから Task 2 へ進む。

---

## Task 2: ダブルクリック単語選択を Core へ委譲する(挙動不変)

**Files:**
- Modify: `src/yEdit.Editor/InputRouter.cs:520-568`

設計書 §3.1 の 2「3 箇所が 1 本を共有する形にする」の前半。**挙動不変**。

### Step 1: 移設元の全 pos 出力を採って Task 1 の期待値表を検証する

Task 1 の `WordStart_WordEnd_MatchDoubleClickRule` の期待値は、移設元の現行挙動から
機械的に得たものでなければならない。`--wordunit` の `SelfCheckCandidateA` が
**まさにその照合(候補 A 構成 vs `InputRouter` private を反射で全 pos)**をしている。

Run:
```
dotnet run --project tests/yEdit.Editor.Smoke -c Release -- --wordunit | Out-File -Encoding utf8 (Join-Path $env:TEMP 'wordunit-before-task2.md')
```
Expected: 出力に `SelfCheck(候補 A)` の OK 行がある。

`$env:TEMP\wordunit-before-task2.md` の §4.1 表と Task 1 の InlineData を突き合わせ、
**1 行でも食い違ったら InlineData の方を直す**(実装ではなく期待値が正)。

### Step 2: `InputRouter` を委譲へ書き換える

`src/yEdit.Editor/InputRouter.cs` の `HandleMouseDoubleClick` 本体(520-521 行)を変える。

```csharp
        int start = WordBoundary.WordStart(snap, target, WordBoundary.NoScanLimit);
        int end = WordBoundary.WordEnd(snap, target, WordBoundary.NoScanLimit);
```

そして `// ===== word boundary helpers (for DoubleClick) =====` 節(528-568 行)を
**まるごと削除する**。移設先の xmldoc(Task 1 Step 3-c)に説明は引き継いである。

### Step 3: 挙動不変を確認する

Run:
```
dotnet test tests/yEdit.Editor.Tests -c Release --filter "FullyQualifiedName~MouseInput"
```
Expected: 全件 PASS。**`DoubleClick_SelectsWord` と
`DoubleClick_OnWhitespace_SelectsPrevWordPlusWhitespaceRun` を 1 文字も編集していないこと**が
挙動不変の証明になる。編集が必要になったら移設が bit-perfect でない。

Run:
```
dotnet test yEdit.sln -c Release
```
Expected: 全件 PASS。

### Step 4: commit

```
refactor(editor): ダブルクリック単語選択を Core WordBoundary へ委譲する

InputRouter の private ヘルパ 2 本(PrevWordBoundary / NextWordBoundary)を削除し、
Task 1 で Core へ移設した WordStart / WordEnd を呼ぶ。規則は移設時に bit-perfect で
写しているため挙動不変。MouseInputTests のダブルクリック 2 件を無改修のまま緑に
保てることが根拠。
```

### Step 5: 仕様レビュー(別エージェント)

観点: 削除した private 2 本と新しい呼び出しが等価か / `HandleMouseDoubleClick` 以外に
呼び出し元が残っていないか(`rg -n "PrevWordBoundary|NextWordBoundary" src/ tests/`)。

---

## Task 3: SR の読み上げスパンを Core 規則へ差し替える(**挙動変更**)

**Files:**
- Modify: `src/yEdit.Editor/UiaTextHostAdapter.cs:490-506, 526-556`
- Modify: `src/yEdit.Accessibility/IUiaTextHost.cs:57-61`(doc の嘘を直す)
- Modify: `tests/yEdit.Editor.Tests/EditorControlUiaHostTests.cs`(新スパンの検証を追加)

**ここが F-3 の本丸。** SR が読むスパンが「行全体」から「クラス単位」へ変わる。

### Step 1: 失敗するテストを書く

`tests/yEdit.Editor.Tests/EditorControlUiaHostTests.cs` の
`Host_WordStart_UsesCoreWordBoundary` の直後へ追加する。

```csharp
    /// <summary>
    /// 2026-08-04 F-3: UIA の単語スパンが文字クラス規則(= ダブルクリック単語選択)と一致する。
    /// 修正前は「空白 / CR / LF のみが区切り」だったため、空白の無い日本語行では
    /// 行全体が 1 単語として SR に読まれていた(2026-08-03-uia-word-unit-design.md §2.3)。
    /// </summary>
    [Theory]
    [InlineData("今日は晴れです。", 0, 0, 2)] // 修正前は [0,8) = 行全体
    [InlineData("今日は晴れです。", 2, 2, 3)]
    [InlineData("abc123def", 4, 3, 6)] // 修正前は [0,9) = 行全体
    [InlineData("foo(bar)=baz;", 0, 0, 3)] // 修正前は [0,13) = 行全体
    [InlineData("foo bar baz", 4, 4, 7)] // 対照群: 英文は修正前後で不変
    public void Host_WordSpan_UsesCharClassRule(string text, int pos, int start, int end) =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString(text);
            ctrl.SetSource(buf);
            IUiaTextHost host = ctrl;
            Assert.Equal(start, host.WordStart(pos));
            Assert.Equal(end, host.WordEnd(pos));
        });
```

> 期待値は Task 1 の Core テストと同じ表から採る。**両方に同じ表を置くのは冗長ではない** —
> Core は規則の正しさを、こちらは「UIA ホストがその規則へ本当に配線されているか」を固定する。

### Step 2: テストが失敗することを確認する

Run:
```
dotnet test tests/yEdit.Editor.Tests -c Release --filter "FullyQualifiedName~Host_WordSpan"
```
Expected: FAIL。`今日は晴れです。` pos=0 で `end` が `8`(行全体)になる。
**この失敗こそが F-3 の実害** — 期待値どおりに落ちることを目視確認する。

### Step 3: `UiaTextHostAdapter` を差し替える

`src/yEdit.Editor/UiaTextHostAdapter.cs:490-506` を次に変える。

```csharp
    int IUiaTextHost.WordStart(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null)
            return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        return yEdit.Core.Editing.WordBoundary.WordStart(
            snap,
            o,
            yEdit.Core.Editing.WordBoundary.NoScanLimit
        );
    }

    int IUiaTextHost.WordEnd(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null)
            return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        return yEdit.Core.Editing.WordBoundary.WordEnd(
            snap,
            o,
            yEdit.Core.Editing.WordBoundary.NoScanLimit
        );
    }
```

そして 526-556 行の**コメント 3 行と `WordBoundary_WordStart` / `WordBoundary_WordEnd` を削除する**。

> `NoScanLimit` のままなのは、本タスクの挙動変更を「規則の差し替え」だけに閉じるため。
> 上限は Task 5 で入れる。**1 タスクで 1 つの挙動変更**にしておくと L5 で問題が出たときに
> どちらが原因か切り分けられる。

### Step 4: `IUiaTextHost` の doc を直す

`src/yEdit.Accessibility/IUiaTextHost.cs:57-61`。設計書 §4.1 の「4 つのうち 2 つだけが嘘」を解消する。

```csharp
    /// <summary>
    /// offset を含む単語の左端(Core <c>WordBoundary.WordStart</c> 委譲=文字クラス規則・走査上限つき)。
    /// ダブルクリック単語選択と同じスパンを返す。
    /// </summary>
    int WordStart(int offset);

    /// <summary>
    /// offset を含む単語の右端(Core <c>WordBoundary.WordEnd</c> 委譲=文字クラス規則・走査上限つき)。
    /// 末尾の空白は含まない。
    /// </summary>
    int WordEnd(int offset);
```

### Step 5: テストが通ることを確認する

Run:
```
dotnet test yEdit.sln -c Release
```
Expected: 全件 PASS。

**落ちたら止まって読む。** 落ちうるのは:
- `TextRangeProviderV2Tests`(stub host なので本来無関係。落ちたら stub を触っていないか確認)
- `EditorControlUiaHostTests.Host_WordStart_UsesCoreWordBoundary`(`hello world` は新旧一致のはず)

### Step 6: commit

```
fix(a11y): UIA の単語スパンを文字クラス規則へ揃える(F-3)

SR が読む単語スパンは「空白 / CR / LF のみが区切り」の素朴実装で決まっていたため、
空白の無い日本語行では行全体が 1 単語として読まれていた。実機 NVDA が
ExpandToEnclosingUnit(Word) の戻り値をそのまま読むことは確認済み
(2026-08-03-uia-word-unit-design.md §2.3)。

Task 1 で Core へ移設した WordStart / WordEnd(= ダブルクリック単語選択と同じ規則)へ
差し替える。目で見える選択と耳で聞くスパンが一致するようになる。英文(空白区切り・
単一クラス run)の挙動は変わらない。

IUiaTextHost の xmldoc が「Core WordBoundary 委譲」と書いていながら素朴実装だった件
(設計書 §4.1)も、この差し替えで doc が事実になる。
```

> **⚠ 上の commit message の「英文の挙動は変わらない」は誤りである**(Task 3 の仕様レビューで発覚)。
> 変わるのは非空白 run のスパンだけではない。**キャレットが空白の上にあるときのスパンも変わる**:
> 修正前は「キャレット直下の空白 1 個」、修正後は「左の空白 run を越えた先の前の単語」になる。
> `"ab    cd"` の pos=4 では旧 `[4,5)`(空白)→ 新 `[0,2)`(`ab`)= **キャレットの 2 桁左が読まれる**。
> 行頭インデントは純 ASCII の英文であり、ソースコード編集で最も踏みやすい位置である。
>
> これはダブルクリック単語選択が元から持っている挙動(空白位置では前の単語を選ぶ)へ揃えた
> **当然の帰結でバグではない**が、設計書 §3.2 の「変わるのは日本語・記号混じり・数字混じりの行だけ」
> という記述は不正確である。**PR description と L5 項目 10 で扱う。**

### Step 7: 仕様レビュー(別エージェント)

観点:
- 設計書 §3.1 の 1 / 2 を満たしたか(規則を発明していない・3 箇所が 1 本を共有)
- `UiaTextHostAdapter` に単語走査の私有実装が残っていないか
- **a11y 鉄則**: 追加した経路が RPC スレッドから UI スレッド専有状態へ触っていないか
  (`_bufferSnapshot` を読んで静的ヘルパを呼ぶだけ = 設計書 §2.4 の構造を保っているか)

---

## Task 4: expand の窓をキャレット中心にする

**Files:**
- Modify: `src/yEdit.Accessibility/TextRangeProviderV2.cs:58-64`
- Modify: `tests/yEdit.Core.Tests/Accessibility/TextRangeProviderV2Tests.cs`

Task 5 で上限を入れる前に、**上限が効いてもスパンがキャレットを含む**形へ直しておく。
順序が逆だと、上限を入れた瞬間にキャレット除外スパンが SR へ出る。

### Step 1: 失敗するテストを書く

`tests/yEdit.Core.Tests/Accessibility/TextRangeProviderV2Tests.cs` の
`ExpandToEnclosingUnit_Word_ExpandsToWordSpan` の直後へ追加する。

```csharp
    /// <summary>
    /// 2026-08-04: expand の窓はキャレット中心でなければならない。
    /// <c>WordEnd</c> の起点を <c>_start</c> にすると、走査上限が効いたとき
    /// <c>_end = _start + cap = pos</c> となりスパンがキャレットの手前で終わる
    /// (2026-08-03-uia-word-unit-design.md §2.5 の候補 B 欠陥 1)。
    /// </summary>
    /// <remarks>
    /// stub host は上限を持たないため、この網は「<c>WordEnd</c> に何を渡しているか」を
    /// 起点依存の期待値で固定する形にする。<c>WordEnd</c> の引数が <c>_start</c> だと
    /// 4 を返し、<c>pos</c> だと 7 を返す fixture を使う。
    /// </remarks>
    [Fact]
    public void ExpandToEnclosingUnit_Word_UsesCaretAsWordEndOrigin()
    {
        // "ab  cdef": pos=5 のとき WordStart(5)=4。stub の WordEnd は空白区切りなので
        // WordEnd(4)=8 / WordEnd(5)=8 で差が出ない → 空白を挟んで差が出る fixture にする。
        var p = MakeProvider("ab cd ef");
        var r = new TextRangeProviderV2(p, 2, 2); // pos=2 = 空白の上
        r.ExpandToEnclosingUnit(TextUnit.Word);
        // WordStart(2)=0 / WordEnd(2)=2 → "ab"(キャレット位置 2 で終わる)
        // 起点が _start(=0) だと WordEnd(0)=2 で同じ。差が出ないので下の Theory で担保する。
        Assert.Equal("ab", r.GetText(int.MaxValue));
    }

    [Theory]
    [InlineData("hello world", 3, "hello")]
    [InlineData("hello world", 8, "world")]
    [InlineData("hello    world", 7, "hello   ")] // 空白 run の途中 = 起点差が出る位置
    public void ExpandToEnclosingUnit_Word_SpanIsAnchoredAtCaret(
        string text,
        int pos,
        string expected
    )
    {
        var p = MakeProvider(text);
        var r = new TextRangeProviderV2(p, pos, pos);
        r.ExpandToEnclosingUnit(TextUnit.Word);
        Assert.Equal(expected, r.GetText(int.MaxValue));
    }
```

> `"hello    world"` pos=7 は、起点が `_start`(=0)なら `WordEnd(0)` = 5 で `"hello"`、
> 起点が `pos` なら `WordEnd(7)` = 7 で `"hello  "` になる**判別点**である。
> stub host(`TextRangeProviderV2Tests.cs:61-75`)の実装を読んで期待値を確定させること。
> **期待値を推測で書かない** — Step 2 で赤を見て、実際の差分を確認してから確定する。

### Step 2: テストが失敗することを確認する

Run:
```
dotnet test tests/yEdit.Core.Tests -c Release --filter "FullyQualifiedName~SpanIsAnchoredAtCaret"
```
Expected: `hello    world` の行だけ FAIL。**失敗メッセージの実際値をもって期待値を確定する**
(stub の空白スキップ規則を机上で追うより確実)。

### Step 3: 起点を変える

`src/yEdit.Accessibility/TextRangeProviderV2.cs:58-64`:

```csharp
            case TextUnit.Word:
            case TextUnit.Format:
                // 2026-08-04: WordEnd の起点は _start ではなく pos。
                // 走査上限が入ると WordStart(pos) は最大 cap 歩しか戻らないため、
                // _end = WordEnd(_start) だと _start + cap = pos で終わり、スパンが
                // キャレット位置を含まなくなる(設計書 §2.5 の候補 B 欠陥 1)。
                // pos 起点なら窓が [pos-cap, pos+cap] とキャレット中心になり、
                // 欠陥 1 は構造的に起きない。
                _start = host.WordStart(pos);
                _end = host.WordEnd(pos);
                if (_end == _start)
                    _end = host.NextChar(_start);
                break;
```

### Step 3.5: 新規則で `ExpandToEnclosingUnit(Word)` を叩く end-to-end テストを足す

**Task 3 の仕様レビューで見つかったカバレッジ穴の回収。** `TextRangeProviderV2Tests` の
`InMemoryHost`(`:61-75`)は空白区切りの独自実装なので、**Provider 側のテストは新規則を一切通らない**。
つまり本 Step までの時点で「SR が実際に受け取る文字列」を固定しているテストが**どこにも無い**。
起点を `_start` → `pos` へ変える本タスクで、この網が無いまま進むのは危険である。

`yEdit.Editor.Tests` は `yEdit.Accessibility` の `InternalsVisibleTo` 対象
(`yEdit.Accessibility.csproj:14`)なので、`(IUiaTextHost)EditorControl` を `TextProviderImplV2` へ
噛ませた end-to-end テストが書ける。**本物のホスト × 本物の Provider** で
`ExpandToEnclosingUnit(TextUnit.Word)` → `GetText()` を検証する。

最低限おさえる位置(Task 3 レビューの実測から):

| text | pos | 得たい文字列 | 意図 |
|---|---|---|---|
| `今日は晴れです。` | 0 | `今日` | F-3 解消の本体(修正前は行全体) |
| `foo bar baz` | 4 | `bar` | 英文の対照群 |
| `ab    cd` | 4 | — | **空白位置**。起点変更の影響が出る位置なので、実測値を見てから期待値を確定する |
| `    hello` | 0 | — | 行頭インデント。空スパン → `NextChar` フォールバックの経路 |

**期待値を推測で書かない。** 先に実行して実測を見てから確定すること。

### Step 4: テストが通ることを確認する

Run:
```
dotnet test yEdit.sln -c Release
```
Expected: 全件 PASS。

### Step 5: commit

```
fix(a11y): expand(Word) の窓をキャレット中心にする

WordEnd の起点を _start から pos へ変える。走査上限を入れると
WordStart(pos) は最大 cap 歩しか戻らないため、_start 起点のままでは
_end = _start + cap = pos となりスパンがキャレット位置の手前で終わる。
pos 起点なら窓が [pos-cap, pos+cap] になり、この欠陥が構造的に起きない。

上限を入れる前段としての変更で、上限なしの現時点では空白 run 内での
スパン端がわずかに変わるだけ(テストで固定)。
```

### Step 6: 仕様レビュー(別エージェント)

観点: `_end >= _start` が常に成り立つか / 縮退分岐(`_end == _start`)が壊れていないか /
`Move` / `MoveEndpointByUnit` 側と整合するか。

---

## Task 5: 走査上限を本番経路へ適用する(**挙動変更**)

**Files:**
- Modify: `src/yEdit.Editor/UiaTextHostAdapter.cs`(4 メソッド)
- Modify: `src/yEdit.Editor/InputRouter.cs:160,171,520,521`
- Modify: `tests/yEdit.Editor.Tests/EditorControlUiaHostTests.cs`(上限の網)

設計書 §3.1 の 3 + 本計画の決定 3。`NoScanLimit` を `DefaultMaxScan` へ置き換える。

### Step 1: 失敗するテストを書く

`tests/yEdit.Editor.Tests/EditorControlUiaHostTests.cs` へ追加する。

```csharp
    /// <summary>
    /// 2026-08-04 F-5: 空白ゼロの単一クラス長大行で、UIA の単語走査が行全体を舐めない。
    /// 修正前は WordStart(500K) が必ず 0 を返し、1 回の読み上げに約 1.9〜2.8 秒かかっていた
    /// (2026-08-03-uia-word-unit-design.md §2.4)。
    /// </summary>
    [Fact]
    public void Host_WordSpan_OnHugeSingleClassLine_IsBoundedAndContainsCaret() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString(new string('a', 500_000));
            ctrl.SetSource(buf);
            IUiaTextHost host = ctrl;

            const int Pos = 250_000;
            int start = host.WordStart(Pos);
            int end = host.WordEnd(Pos);

            Assert.True(start > 0, $"行頭まで走っている(start={start})");
            Assert.True(end < 500_000, $"行末まで走っている(end={end})");
            Assert.True(start <= Pos && Pos < end, $"スパン [{start},{end}) がキャレット {Pos} を含まない");
            Assert.True(
                end - start <= 2 * WordBoundary.DefaultMaxScan,
                $"窓が cap の 2 倍を超えている({end - start})"
            );
        });

    /// <summary>移動側(UIA Move / Ctrl+←→)にも同じ上限が効いていること。</summary>
    [Fact]
    public void Host_WordNavigation_OnHugeSingleClassLine_IsBounded() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString(new string('a', 500_000));
            ctrl.SetSource(buf);
            IUiaTextHost host = ctrl;

            Assert.True(host.NextWordStart(0) <= WordBoundary.DefaultMaxScan);
            Assert.True(host.PrevWordStart(500_000) >= 500_000 - WordBoundary.DefaultMaxScan - 1);
        });
```

### Step 2: テストが失敗することを確認する

Run:
```
dotnet test tests/yEdit.Editor.Tests -c Release --filter "OnHugeSingleClassLine"
```
Expected: FAIL(`start=0` / `end=500000`)。**数秒かかること自体が F-5 の症状**なので、
体感時間もメモしておく(Task 6 の前後比較の材料)。

### Step 3: 上限を適用する

`NoScanLimit` を `DefaultMaxScan` へ置き換える。対象は本番経路のみ。

| ファイル | 箇所 | 意味 |
|---|---|---|
| `UiaTextHostAdapter.cs` | `WordStart` / `WordEnd` / `NextWordStart` / `PrevWordStart` の 4 メソッド | SR 経路 |
| `InputRouter.cs:160,171` | Ctrl+← / Ctrl+→ | UI スレッドの移動 |
| `InputRouter.cs:520,521` | ダブルクリック単語選択 | UI スレッドの選択 |

> **3 経路は同じ cap を使うこと。** ダブルクリックだけ別値にしたくなるかもしれないが
> (下記のとおり唯一データが欠ける経路なので)、**それは F-3 の再導入**である。本ブランチの
> 目的は「見える選択と聞くスパンを一致させる」ことなので、選択と読み上げで cap が違えば
> 同じ位置で違うスパンが出る。`maxScan` がパラメータなので技術的には 1 行で分けられるが、
> **分けてはならない**。
>
> **ただしダブルクリックは 3 経路で唯一「データが欠ける」経路である。** Ctrl+← は
> キャレットが run の途中で止まるだけ、SR は run の一部が読まれるだけだが、
> ダブルクリックは切り詰められた選択がそのまま Ctrl+C でコピーされる。
> CLAUDE.md §2「晴眼・弱視ユーザーも第一級」に照らして L5 の確認項目に入れる(Task 7 Step 4 の 9)。

`InputRouter.cs:160` の周辺に理由コメントを足す:

```csharp
        // 2026-08-04: 走査上限つき。空白ゼロの単一クラス長大行(ascii 500K)では
        // 上限なしの PrevWordStart が UI スレッドを約 1.4 秒占有する
        // (docs/plans/2026-08-03-uia-word-unit-design.md §2.4)。上限に当たると
        // キャレットは単語 run の途中で止まる = 意図的な仕様変更。
```

### Step 4: テストが通ることを確認する

Run:
```
dotnet test yEdit.sln -c Release
```
Expected: 全件 PASS。

**`WordBoundaryTests` の既存 13 件が落ちないこと**を特に確認する(短い fixture なので
上限に当たらないはず。当たっているなら `DefaultMaxScan` の暫定値が小さすぎる)。

### Step 5: commit

```
fix(editor,a11y): 単語走査に上限を入れて長大行の全走査を止める(F-5)

空白ゼロの単一クラス長大行では、単語走査が行頭 / 行末まで舐めていた。SR の読み上げ
1 回で約 1.9〜2.8 秒(RPC スレッド=発話が返らない)、Ctrl+← で約 1.4 秒(UI スレッド=
アプリが固まる)。文字クラス規則(F-3 修正)は単一クラスの連続には効かないため、
上限が別途要る。

上限に当たると単語の途中で切れる。SR は run の一部だけを読み、キャレットも run の
途中で止まる。500K 文字を 1 単語として扱わないための意図的な打ち切りである。
expand の窓は Task 4 でキャレット中心にしてあるので、切り詰めてもスパンは必ず
キャレットを含む。

cap の値は暫定。Task 6 の実測で確定させる。
```

### Step 6: 仕様レビュー(別エージェント)

観点:
- `NoScanLimit` が本番経路に残っていないか(`rg -n "NoScanLimit" src/`)
- 上限適用で**スパンがキャレットを含まなくなる**位置が作れないか(反例探索)
- Ctrl+←→ の仕様変更が xmldoc / コメントに書かれているか

---

## Task 6: cap の実測と確定

**Files:**
- Modify: `tests/yEdit.Editor.Smoke/WordUnitBench.cs`
- Modify: `src/yEdit.Core/Editing/WordBoundary.cs`(`DefaultMaxScan` の値のみ)

設計書 §3.3 の第 1 項。**値を決めるのはユーザー**。Claude は材料を出す。

> **⚠ Task 6 到達まで `--wordunit` の §4.1 表を「F-3 が消えた」の証拠に使わないこと。**
> Task 2 で `InputRouter` の private 2 本を削除した時点で、ベンチの `SelfCheckCandidateA`
> (`GetMethod("PrevWordBoundary", NonPublic|Static)` の文字列反射)が null を引いて `NG` を返すように
> なった。ベンチは NG を明示出力して `false` を返す作りなので沈黙して嘘はつかないが、
> **§4.1 表の「ダブルクリック選択」列を担保する網はそこで死んでいる**。以後この列は
> 「本物」ではなく「Core と逐語同一のコピー」を表示している = **同じ壊れ方をする参照**であり、
> ずれを検出できない。Step 1 の差し替えで解消する。
>
> **Task 3 以降は `SelfCheckCandidateB` も必ず NG になる。** 候補 B は「現状実装(空白のみ規則)の
> 写経」なので、本物が文字クラス規則へ変わった時点で照合が合わなくなるのは**正しい振る舞い**である。
> つまり Task 3〜5 の間、`--wordunit` は **SelfCheck が A / B 両方 NG のまま回る**。
> ゲートを持たない(常に EXIT 0)ので害はないが、**次の担当者が「自分が壊した」と誤認しないよう**
> ここに記録しておく。Step 1 で両方とも削除される。

### Step 1: ベンチを実装後の本物基準へ作り直す

`--wordunit` は調査時の構成(現状実装 vs 候補 A 写経 vs 候補 B 写経)のままなので、
実装後は意味が変わる。次のとおり組み替える。

- **削除**: `CappedWordStart` / `CappedWordEnd`(候補 B の写経)と `SelfCheckCandidateB`
  — 本物が上限を持つようになったので写経は不要
- **削除**: `DoubleClickWordStart` / `DoubleClickWordEnd`(候補 A の写経)と `SelfCheckCandidateA`
  — 本物が同じ 1 本になったので照合対象が消えた
- **維持**: §4.1 のずれ表。ただし「SR 読みスパン」と「ダブルクリック選択」が**全 fixture で一致する**
  ことの確認表に役割が変わる(= F-3 が消えたことの可視化)
- **追加**: cap 掃引表

```csharp
    /// <summary>
    /// cap 掃引。<c>WordBoundary.DefaultMaxScan</c> の値を決めるための材料を出す。
    /// 壁時計は cap に単純比例しない(<c>TextSnapshot.GetChar</c> のコストが
    /// <c>TextChunk</c> の格子内線形走査に比例するため。設計書 §2.5 の候補 B 欠陥 2)ので、
    /// 掃引して実測する以外に決め方がない。
    /// </summary>
    private static void PrintCapSweep()
    {
        Console.WriteLine();
        Console.WriteLine("## cap 掃引(空白ゼロ 500K・expand 1 回 = WordStart + WordEnd)");
        Console.WriteLine();
        Console.WriteLine("| kind | cap | expand ms | Ctrl+← 1 回 ms | スパン長 |");
        Console.WriteLine("|---|---|---|---|---|");

        foreach (string kind in new[] { "ascii", "cjk", "jamix" })
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString(MakeSingleLine(500_000, kind));
            ctrl.SetSource(buf);
            var snap = buf.Current;
            int pos = 250_000;

            foreach (int cap in new[] { 32, 64, 128, 256, 512, 1024, 4096 })
            {
                // ウォームアップ(計測外)
                _ = WordBoundary.WordStart(snap, pos, cap);
                _ = WordBoundary.PrevWordStart(snap, pos, cap);

                int s = WordBoundary.WordStart(snap, pos, cap);
                int e = WordBoundary.WordEnd(snap, pos, cap);

                double expandMs =
                    BestOf3(() => _ = WordBoundary.WordStart(snap, pos, cap))
                    + BestOf3(() => _ = WordBoundary.WordEnd(snap, pos, cap));
                double navMs = BestOf3(() => _ = WordBoundary.PrevWordStart(snap, pos, cap));

                Console.WriteLine(
                    $"| {kind} | {cap} | {expandMs:F3} | {navMs:F3} | {e - s} |"
                );
            }
        }
    }
```

`Run()` から呼ぶ。**判定ゲートは持たない**(常に EXIT 0)。

### Step 2: 「切り詰めが起きる頻度」の材料も出す

速度だけでは決められない。**現実のテキストで cap に当たるか**を出す。

```csharp
    /// <summary>
    /// 各 fixture の最長クラス run。cap がこれを下回ると「普通の文章で単語が切れる」。
    /// 速度側の下限(Step 1)と合わせて cap の下限を決めるための材料。
    /// </summary>
    private static void PrintRunLengthHistogram()
    {
        Console.WriteLine();
        Console.WriteLine("## 現実のテキストにおける最長クラス run");
        Console.WriteLine();
        Console.WriteLine("| fixture | 最長 run | 平均 run |");
        Console.WriteLine("|---|---|---|");

        foreach (var (name, text) in Fixtures)
        {
            var snap = TextBuffer.FromString(text).Current;
            int max = 0,
                total = 0,
                count = 0,
                pos = 0;
            while (pos < snap.CharLength)
            {
                int next = WordBoundary.NextWordStart(snap, pos, WordBoundary.NoScanLimit);
                if (next <= pos)
                    break;
                max = Math.Max(max, next - pos);
                total += next - pos;
                count++;
                pos = next;
            }
            Console.WriteLine(
                $"| {name} | {max} | {(count == 0 ? 0 : total / count)} |"
            );
        }
    }
```

**§4.1 の短い fixture だけでは足りない。** リポジトリ内の実ファイル(例:
`docs/plans/2026-08-03-uia-word-unit-design.md` 自身・`src/yEdit.Editor/EditorControl.cs`)を
読み込んで同じ統計を出す行を足す(コードとドキュメントで最長 run が違うため両方見る)。

### Step 3: 測定して結果を保存する

Run (PowerShell):
```
dotnet run --project tests/yEdit.Editor.Smoke -c Release -- --wordunit | Out-File -Encoding utf8 (Join-Path $env:TEMP 'wordunit-after.md')
```

確認すること:
1. §4.1 の表が**全 fixture で「SR 読みスパン = ダブルクリック選択」**になっている(F-3 解消の可視化)
2. cap 掃引表が単調でない箇所があっても驚かない(格子位相の影響。設計書 §2.5 の欠陥 2)
3. 最長 run 表で、コード / ドキュメントの実測値が cap 候補とどう並ぶか

### Step 3.5: 較正で踏んではいけない罠(Task 5 レビューで判明)

- **`maxScan` は code point 数であって char 数ではない。** 走査**回数**は cap で頭打ちだが、
  **スパン幅は非 BMP で最大 4×cap char** まで伸びる(実測: 絵文字 run で cap=256 のとき幅 1022)。
  「SR が 1 回に読む最大文字数 = 2×cap」と誤読しないこと。
- **cap 打ち切りは空白 run の途中でも起きる。** `WordStart` が空白の上に着地する状態は
  上限なしの走査では決して生じない。L5 で「空白の連続を読んだとき」の体感を見る(項目 12)。
- **cap を上げると全 pos 総当りテストが二次で効く。** `Host_WordSpan_UnderScanLimit_AlwaysContainsCaret` は
  fixture 長 ≈ `7 × cap` を全 pos 走るため、cap 4096 では約 30 秒になる見込み。
  大きい cap を採る場合は fixture 長に上限を設けるか pos をサンプリングする。

### Step 4: ユーザーへ提示して cap を確定する

次の 3 点を並べて提示し、**ユーザーの承認を得る**。

- 速度: cap ごとの expand / Ctrl+← の実測(目安 = SR の 1 回の読みが 1 ms 未満)
- 単語らしさ: 現実のテキストで切り詰めが起きない最小値
- 推奨値とその理由

**承認なしに値を確定しない。**

### Step 5: `DefaultMaxScan` を確定して commit

`src/yEdit.Core/Editing/WordBoundary.cs` の `DefaultMaxScan` を確定値へ変え、
xmldoc の `<remarks>` に**実測の根拠**(kind / cap / ms / 最長 run)を 2〜3 行で書く。

```
fix(core): 単語走査の上限を実測値で確定する

cap 掃引(空白ゼロ 500K × ascii/cjk/jamix)と、現実のテキストにおける最長クラス run
の実測から決めた。速度側の要求(SR の読み 1 回が十分速い)と、単語らしさの要求
(普通の文章で切り詰めが起きない)の両方を満たす最小値を採る。
壁時計は cap に単純比例しない(TextChunk 格子内走査の位相)ため、掃引で決めた。
```

### Step 6: 仕様レビュー(別エージェント)

観点: ベンチの写経削除で「本物を測っているか」が保たれているか / 根拠が xmldoc から追えるか。

---

## Task 7: 最終ブランチレビュー → 品質ゲート → L5 → PR

### Step 1: 最終ブランチレビュー(**2 パス・別々のエージェント起動**)

CLAUDE.md §3 工程 5。`src/` を 4 ファイル変更しており簡略化基準に該当しないので**統合しない**。

**パス A: コード品質**
- Core / Editor / Accessibility の責務分担は妥当か(規則は Core・clamp は Adapter)
- `maxScan` 必須引数化が誤用を実際に防げているか
- 削除した private 実装の知識(xmldoc)が移設先に残っているか
- **ミューテーション検証(スポットチェック)**: 次の 3 箇所を一時的に変異させ、
  対象テストが**赤になること**を確認してから復元する
  1. `SkipForwardWhile` の `budget > 0` を `true` に → `NextWordStart_WithMaxScan_StopsMidRun` が赤
  2. `TextRangeProviderV2` の `host.WordEnd(pos)` を `host.WordEnd(_start)` へ戻す →
     `ExpandToEnclosingUnit_Word_SpanIsAnchoredAtCaret` が赤
  3. `WordStart` の `PrevWordStart(snap, pos + 1, ...)` を `pos` へ →
     `WordStart_WordEnd_MatchDoubleClickRule` が赤
  - **`--no-build` を付けない**(変異前バイナリを測る事故。[[uia-scrollintoview]] の教訓)

**パス B: 脆弱性**
- 上限が DoS 面を悪化させていないか(むしろ改善のはずだが、`WordEnd` の巻き戻しループが
  上限外で走らないか)
- サロゲート / 孤立サロゲートで無限ループや例外が起きないか
- RPC スレッドと UI スレッドの分離が保たれているか(a11y 鉄則)

指摘は 3 択で明示し、fixup commit で積む。

**あわせて回収する doc 整合**(レビュー前に済ませてよい):

- `tests/yEdit.Editor.Tests/MouseInputTests.cs:238,263` のコメントが、Task 2 で削除された
  `InputRouter.PrevWordBoundary` / `NextWordBoundary` を参照している。**Task 2〜6 の間は
  触ってはならない**(このファイルが無改修であることが挙動不変の一次証拠のため、コメントでも
  触ると証拠が濁る)。ブランチ末尾のここで `WordBoundary.WordStart` / `WordEnd` へ読み替える。
- `tests/yEdit.Core.Bench/Program.cs:339` のコメントが、Task 3 で削除された
  `UiaTextHostAdapter.WordBoundary_WordStart` / `_WordEnd` を参照している(「同じ構造が UIA 側にもある」)。
  Task 3 以降は事実でないので、ここで読み替えるか削除する。
- `tests/yEdit.Editor.Smoke/WordUnitBench.cs:13,160` も同じシンボルを指している。
  こちらは Task 6 の作り直しで消える見込みだが、**残っていたらここで回収する**。

### Step 2: 品質ゲート

Run (PowerShell):
```
powershell -ExecutionPolicy Bypass -File tools/pre-merge-check.ps1
```
Expected: **EXIT 0**。この環境に `pwsh` は無い。

### Step 3: SR 回帰スクリプト

Run (PowerShell):
```
powershell -ExecutionPolicy Bypass -File tools/sr-regression.ps1
powershell -ExecutionPolicy Bypass -File tools/word-sim.ps1
```
Expected: 全 Check が OK。`word-sim.ps1` は `prelude ABC abc 123 tail` を使うので、
全 run が単一クラス = **期待値は変わらないはず**。落ちたら規則の写し間違いを疑う。

**これは UIA 応答の検証まで。実発声は検出できないので L5 の代替にならない**(CLAUDE.md §5)。

### Step 4: L5 手順書を書いてユーザーへ渡す

`docs/plans/2026-08-04-uia-word-unit-l5-checklist.md` を作る。設計書 §3.4 により **L5 必須**。

必ず含める項目:

| # | 確認内容 | 判定 |
|---|---|---|
| 1 | `今日は晴れです。` で Ctrl+→ を繰り返す | 発話が `今日` / `は` / `晴` / `れです` / `。` と刻まれる(修正前は毎回行全体) |
| 2 | 同じ行の `今` をダブルクリック → 選択を目視 → 同じ位置で単語を読ませる | **見える選択と聞くスパンが一致する**(修正前は 2 文字 vs 8 文字) |
| 3 | `foo bar baz` で Ctrl+→ | `bar` / `baz`(**英文の体験が変わっていないこと**) |
| 4 | `メモ帳のテキストを編集` で Ctrl+→ | カタカナ / 漢字 / ひらがなで刻まれる |
| 5 | **クラス単位の刻みが自然か**(設計書 §3.3 の第 2 項) | ユーザーの主観判断。不自然なら規則を再検討する |
| 6 | 空白ゼロ 500K 行(ascii)で Ctrl+← / Ctrl+→ | **発話までの実時間**を測る。修正前は約 1.9〜2.8 秒 / 修正後は即座 |
| 7 | 同上で上限に当たったときの読まれ方 | run の途中で切れた発話が許容できるか |
| 8 | 全角空白を含む行(`今日　は`)で全角空白の上へ移動 | 全角空白そのものが読まれる(修正前は前の単語 `今日` を読んでいた) |
| 9 | 空白ゼロ 500K 行(ascii)でダブルクリック → Ctrl+C → 別タブへ貼り付け | **cap で切り詰められた内容がコピーされる**。3 経路で唯一データが欠ける経路なので、切り詰めが許容できるか / cap 値が妥当かを晴眼の視点で判断する |
| 10 | **インデントされたコード行**(例 `    hello`・`ab    cd`)で、空白の上へ ← → で移動して読ませる | **英文でも変わる位置**(Task 3 レビューで発覚)。修正前は「キャレット直下の空白 1 個」、修正後は「前の単語」を読む。`ab    cd` の pos=4 では**キャレットの 2 桁左の `ab` が読まれる**。ダブルクリック単語選択と同じ規則なので設計としては一貫しているが、**SR 利用者にとって自然かは実機でしか判断できない** |
| 12 | **長い空白の連続**(cap を超える長さ)をダブルクリック / SR で読む | **cap 打ち切りは空白 run の途中でも起きる**(Task 5 レビューで判明)。上限なしの走査では決して生じない状態なので、体感を見ておく |
| 11 | **行末の空白の上**(例 `abc  ` の末尾側)や、**空行を挟んだ位置**へ移動して読ませる | **改行を含むスパンが返りうる**(Task 4 レビューで発覚)。旧実装のスパンは改行を跨げなかったが、新実装は跨ぐ(`"a\r\nb  \n  c"` の pos=7 で `"b  \n"` が返る)。NVDA がこれをどう読むか(改行を読み上げるか・行をまたいで読むか・沈黙するか)は**実機でしか分からない**。PR #35 の L5 で見つかった E-1(折り返し ON の ↓ で「ブランク」)と紛らわしいので、**修正前後の両方で同じ操作を試して差分を見る**こと |

**測るのは `Process.Responding` ではなく発話までの実時間**(設計書 §2.4 の申し送り)。
前後比較にするため、修正前(`main`)でも同じ 6 番を測ってもらう。

採取手法の注意(設計書 §6):
- NVDA スピーチビューアーの `RICHEDIT50W` は `ValuePattern` が 4096 文字で切れる。
  **Win32 `WM_GETTEXT` で全文を取る**
- windows-mcp は Insert 修飾を送れない

### Step 5: push → PR 作成

```
git push -u origin feature/uia-word-unit
gh pr create --title "UIA 単語単位: 境界ずれ(F-3)とコスト(F-5)の調査と修正" --body-file <path>
```

PR description(日本語)に必ず書くこと:
- 調査(N-3 / N-4 の決着)と修正が**同一ブランチ**であること、および不変条件を途中で撤回した経緯
- 挙動変更 3 点: SR の読み上げ単位 / expand の窓の起点 / Ctrl+←→ が単語 run の途中で止まる
- cap の確定値と根拠
- スコープ外(U+3000 の Whitespace 化 / E-1 / E-2 / 上書きモードの到達性)
- レビュー経緯(前倒し品質レビュー 1・仕様レビュー 5・最終 2 パス)と L5 の結果
- 末尾に `🤖 Generated with [Claude Code](https://claude.com/claude-code)`

### Step 6: 申し送りを設計書へ追記する

`docs/plans/2026-08-03-uia-word-unit-design.md` §7(Task 0 で作った節)へ、実施後に判明した
申し送りを追記する。少なくとも次を回収対象として残す。

- `UiaTextHostAdapter._lastLineSegs` の完全リスト前提(SR 経路が巨大 1 行で `Wrap` する)
- U+3000 の Whitespace 化(本ブランチではスコープ外)
- L5 未検証の繰り越し 2 件(レビューカーソル / 点字)
- E-1 / E-2 / 上書きモード到達性が**未起票**であること

---

## リスクと対処

| ID | リスク | 対処 |
|---|---|---|
| R1 | 移設が bit-perfect でなくダブルクリックの挙動が変わる | Task 2 で `MouseInputTests` の 2 件を**無改修のまま緑**にすることを条件にする。編集が要る時点で移設ミス |
| R2 | 上限がスパンからキャレットを外す | Task 4 を Task 5 より**前**に置く。`WordSpan_WithMaxScan_ContainsCaret`(Core)と `Host_WordSpan_OnHugeSingleClassLine_IsBoundedAndContainsCaret`(Editor)の 2 層で固定 |
| R3 | cap が小さすぎて普通の文章で単語が切れる | Task 6 Step 2 で**現実のテキストの最長 run** を実測してから決める。速度だけで決めない |
| R4 | クラス単位の刻みが SR 利用者に不自然 | L5 項目 5 で主観判断を仰ぐ。不自然なら規則を再検討(設計書 §3.3 の第 2 項は未確認のまま残っている) |
| R5 | Ctrl+←→ が run の途中で止まる仕様変更が受け入れられない | L5 項目 6 / 7 で確認。NG なら Task 5 の移動側だけ revert できるよう、SR 経路と移動側の上限適用を**同一 commit 内でも別ファイル**に分けておく |
| R6 | 既存テストが赤くなり「期待値を直す」誘惑が生じる | Task 1 / 2 は挙動不変。**赤が出たら実装を疑う**。期待値を変えてよいのは Task 3 / 4 / 5 の挙動変更分だけで、その場合も**先に赤を見てから**変える |
| R7 | 測定値がマシン負荷で 2 倍振れる(PR #34 の教訓) | 絶対値ではなく倍率と性質で主張する。cap 決定は 1 回の実測で決め打たず、掃引の形(単調性・桁)を見る |
| R8 | `--wordunit` の写経削除で調査時の数値と比較できなくなる | 調査時の出力は PR #34 / 本ブランチの commit と `$env:TEMP` の保存ファイルに残る。設計書 §2 の表が一次資料 |

---

## 完了条件(DoD)

- [ ] `WordBoundary` に `WordStart` / `WordEnd` / `maxScan` が入り、`src/` から
      単語走査の私有実装が消えている(`rg -n "WordBoundary_WordStart|PrevWordBoundary" src/` が空)
- [ ] `rg -n "NoScanLimit" src/ -g '!**/WordBoundary.cs'` が空(本番経路は全て `DefaultMaxScan`。
      定義ファイル自身の `const` 宣言と xmldoc の `<see cref>` は残る)
- [ ] `IUiaTextHost.WordStart` / `WordEnd` の xmldoc が事実と一致している
- [ ] `MouseInputTests` のダブルクリック 2 件が**無改修**で緑
- [ ] 空白ゼロ 500K 行で `WordStart` / `WordEnd` / `NextWordStart` / `PrevWordStart` が
      上限内で返り、スパンがキャレットを含む
- [ ] `DefaultMaxScan` の値がユーザー承認済みで、根拠が xmldoc から追える
- [ ] `dotnet test yEdit.sln -c Release` 全件 PASS・警告 0
- [ ] `tools/pre-merge-check.ps1` が **EXIT 0**
- [ ] `tools/sr-regression.ps1` と `tools/word-sim.ps1` が全 OK
- [ ] 別エージェントレビュー: 前倒し品質 1 + 仕様 5 + 最終 2 パス が完了し、指摘が 3 択で処理済み
- [ ] ミューテーション検証 3 箇所が kill されることを確認済み
- [ ] **L5 実機検証(NVDA)完了**。特に項目 2(見える選択と聞くスパンの一致)と
      項目 6(発話までの実時間の前後比較)
- [ ] PR description に挙動変更 3 点・cap の根拠・スコープ外・申し送りが書かれている

---

## 実施記録(2026-08-04・実装完了時の追記)

本節は策定時スナップショット(CLAUDE.md §8)への**実施記録の追記**である。上の本文は書き換えていない。

### 本文と食い違っている点

- **Task 4 Step 1 のテスト名** `ExpandToEnclosingUnit_Word_SpanIsAnchoredAtCaret` は、
  最終レビュー Minor-2 の指摘により
  `ExpandToEnclosingUnit_Word_ComposesWordStartWordEndWithNextCharFallback` 相当へ**改名した**。
  理由: stub host では anchored-at-caret と anchored-at-start を**原理的に区別できない**
  (ミューテーションで実証済み)ため、旧名は観測できない性質を主張していた。
  本文中の `--filter "FullyQualifiedName~SpanIsAnchoredAtCaret"` は**実行しても 0 件**になる。
- **Task 4 Step 3 のコメント例**は cap=256 時の実測値で書かれていたが、出荷値は 128。
  最終レビュー Minor-4 により本番コードのコメントからは数値を落とした(下記に転記する)。

### 候補 B の欠陥 1 の実像(cap=256 時・最終レビューで採取)

設計書 §2.5 は欠陥 1 を「キャレット位置の手前で終わる」と説明していたが、**run の種類で実像が違う**。

| run の種類 | fixture | 旧起点 `WordEnd(_start)` の結果 |
|---|---|---|
| 単一クラス | `'a'×5000`・pos=2500 | `[2245, 2501)` = **キャレット文字を含む**(欠陥は牙をむかない) |
| 空白 | `'a'×2000 + ' '×2000 + 'b'×2000`・pos=3000 | `[2745, 2746)` = **キャレットの 255 文字左に 1 文字だけ** |

**欠陥 1 が実際に問題になるのは空白 run である。** 設計書 §2.5 の例(単一クラス run)では
上限を入れてもキャレットは含まれたままなので、あの例だけを見ると修正の必要性が伝わらない。

### 最終ブランチレビュー 2 パスの結果

- **コード品質パス**: ✅ 承認。指定 3 + 追加 3 のミューテーションを実施し、
  **`DefaultMaxScan` の値(128→129)** と **Adapter の `Math.Clamp` 除去**の 2 つが生存 → 網を追加
- **脆弱性パス**: ✅ 承認。Critical / High ゼロ。`maxScan == int.MinValue` で上限が消える
  (`budget--` の unchecked underflow)を実証 → 1 行で修正

### 申し送り(将来回収)

- **cap の較正値はファイル読み込み直後のバッファのもの。** `AppendBuffer` 由来 piece
  (大きめの貼り付け直後)では `TextChunk.CharToByte` の線形走査が効いて **約 17 倍(22.5 ms)**になる。
  上限なしなら 3,101 ms なので本ブランチによる悪化ではない(138 倍改善)が、
  Ctrl+←→ の autorepeat では 30 Hz 予算 33 ms の 68% を使う。
  根治は `TextSnapshot.GetChar` 側(既知の申し送り)。
- **`NoScanLimit` を `internal` 化する**(本番から「上限なし」を表現できなくする)。
  `yEdit.Editor.Tests` / `yEdit.Editor.Smoke` への `InternalsVisibleTo` 追加が要る。
- **`ClassOf` の U+3000 を Whitespace 扱いにするか**(設計書 §3.3・本ブランチではスコープ外)。
- **`ClassOf` を改変すると Ctrl+←→ の境界が変わるのにテストが赤くならない**(2026-07-31 由来の既存申し送り)。
  `maxScan` 導入で走査経路が増えた分、回収価値は上がっている。
- **`DefaultMaxScan` の名前**。最終品質レビューが `SharedWordScanLimit` 系を提案した
  (`Default` は「経路ごとに変えてよい」を連想させるが、要求は逆で 3 経路が必ず同じ値)。
  改名 churn に見合わないと判断し、定数の xmldoc に不変条件を明記する形で受容した。
