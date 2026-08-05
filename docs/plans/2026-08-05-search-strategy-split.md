# 検索照合の戦略分離 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** `SnapshotSearcher` の 6 箇所に散った三重分岐を戦略 3 実装 + セレクタ 1 箇所へ集約し、材質化済み全文をスナップショット単位で再利用できるようにする(挙動不変)。

**Architecture:** `ISnapshotSearchStrategy`(internal)に 6 操作を定義し、`Materialized` / `LiteralWindow` / `RegexPerLine` の 3 実装へ分離する。`SnapshotSearcher` は戦略選択と委譲だけのファサードになる。材質化済み文字列は `Materialized` 戦略の状態として保持し、`TextSnapshot` の参照同一性で無効化する。App 側では `SearchController` が照合条件が変わるまで searcher を使い回し、ユーザーが検索を終えたら破棄する。

**Tech Stack:** C# / .NET (WinForms) / xUnit / CSharpier / Husky.Net

**設計書:** `docs/plans/2026-08-05-search-strategy-split-design.md`(精密化 1・2 を含む)

**ブランチ:** `feature/search-strategy-split`

---

## 実行前に読むこと

### この計画の 3 つの掟

1. **既存テスト 4 本を一行も変えない。** `SnapshotSearcherTests` / `SnapshotSearcherRegexAnchorTests` / `TextSearcherTests` / `SearchControllerTests`。これらが無変更で緑であり続けることが挙動不変の証明である。変えたくなったら、それは実装が間違っているサイン。
2. **12 個の private メソッドは「書き写す」のではなく「移動する」。** 計画に全文を再掲していないのは手抜きではない。350 行を計画へ転記すると転記ミスという新しい事故が増えるだけで、リスクが下がらないため。エディタのカット&ペーストで移し、`private` → `public`(戦略クラス内)と受け手の変更だけを行う。
3. **Task 1 は赤にならない。** Task 1 で足すのは新機能のテストではなく**現行挙動の特性化テスト**である。変更前の src に対して**緑になるのが正しい**。ここで赤が出たら閾値まわりの理解が間違っているので、実装へ進まず設計へ戻ること。

### よく使うコマンド

```powershell
# 全ビルド (警告=エラー)
dotnet build yEdit.sln -c Release -warnaserror

# 検索まわりだけ流す
dotnet test tests/yEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~Search"
dotnet test tests/yEdit.App.Tests  -c Release --no-build --filter "FullyQualifiedName~SearchController"

# 最終ゲート
powershell -File tools\pre-merge-check.ps1
```

`--no-build` を使うときは直前に必ず `dotnet build` を通すこと。**変異を入れたまま `--no-build` で走らせると、変異前のバイナリを叩いて「kill できた/できない」を誤認する**(既知の踏み抜き)。

---

## Task 1: 現行挙動の特性化テストを足す(変更前 src で緑)

設計 §7.4 精密化 2 のとおり、`IsLarge` の `>` を `>=` に変異させても既存テストは全て生き延びる。閾値境界と、閾値以下の端点入力に網が無いため。**リファクタで触る前に網を張る。**

**Files:**
- Modify: `tests/yEdit.Core.Tests/Search/SnapshotSearcherTests.cs`(末尾にテストを追加。既存テストは触らない)

**Step 1: 境界テストと端点テストを追加**

`SnapshotSearcherTests` クラスの末尾(`FindNext_LiteralAcrossWindowBoundary_still_hits_above_threshold` の後)に追記する。

```csharp
    // ==============================
    // 閾値境界 (CharLength == thresholdChars ちょうど)
    // ==============================

    [Fact]
    public void AtExactThreshold_uses_below_path_not_above()
    {
        // 契約: IsLarge は `CharLength > thresholdChars`。ちょうど一致は「閾値以下」= 材質化経路。
        // 経路差が観測できる形として改行跨ぎ regex を使う(材質化経路だけがヒットする)。
        // このテストが無いと `>` → `>=` の変異が既存テストを全て生き延びる
        // (空文書だけが境界に当たるが、空文書は両経路とも同じ値を返すため差が出ない)。
        var snap = Snap("ab\ncd"); // CharLength == 5
        Assert.Equal(5, snap.CharLength);

        var s = MakeLarge(@"b\nc", useRegex: true, matchCase: true, threshold: 5, window: 6);

        // 閾値以下経路 = 文書全体をひとつの入力として regex 適用 → 改行を跨いでヒットする
        Assert.Equal(new MatchSpan(1, 3), s.FindNext(snap, 0));
        Assert.Equal(1, s.Count(snap));
    }

    [Fact]
    public void OneCharAboveThreshold_uses_above_path()
    {
        // 境界の反対側。閾値 +1 文字で行単位経路へ切り替わり、改行跨ぎは取れなくなる。
        // AtExactThreshold_uses_below_path_not_above と対で境界を挟む。
        var snap = Snap("ab\ncd"); // CharLength == 5
        var s = MakeLarge(@"b\nc", useRegex: true, matchCase: true, threshold: 4, window: 6);

        Assert.Null(s.FindNext(snap, 0));
        Assert.Equal(0, s.Count(snap));
    }

    // ==============================
    // 閾値以下の端点入力 (既存は閾値超しか固定していない)
    // ==============================

    [Fact]
    public void FindNext_ClampsNegativeFrom_below_threshold()
    {
        var snap = Snap("ab ab ab");
        var s = Make("ab", matchCase: true);
        Assert.Equal(new MatchSpan(0, 2), s.FindNext(snap, -5));
    }

    [Fact]
    public void FindNext_PastEnd_returns_null_below_threshold()
    {
        var snap = Snap("ab");
        var s = Make("ab", matchCase: true);
        Assert.Null(s.FindNext(snap, snap.CharLength + 1));
    }

    [Fact]
    public void FindPrev_AtOrBeforeZero_returns_null_below_threshold()
    {
        var snap = Snap("ab ab");
        var s = Make("ab", matchCase: true);
        Assert.Null(s.FindPrev(snap, 0));
        Assert.Null(s.FindPrev(snap, -3));
    }

    [Fact]
    public void ReplaceInRange_ClampsOutOfRangeArgs_below_threshold()
    {
        var snap = Snap("ab_ab");
        var s = Make("ab", matchCase: true);
        // start が負・length が文書長を超える → 文書全体へクランプされる
        var (frag, count) = s.ReplaceInRange(snap, -10, 999, "X");
        Assert.Equal("X_X", frag);
        Assert.Equal(2, count);
    }
```

**Step 2: 変更前 src に対して実行し、全て緑になることを確認**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~SnapshotSearcherTests"
```

Expected: **PASS**(既存 + 新規すべて)。

赤が出たら止まること。特に `AtExactThreshold_uses_below_path_not_above` が赤なら閾値の向き(`>` か `>=` か)の理解が間違っている = 設計 §4.1 からやり直す。

**Step 3: 変異で網が効くことを確認(この場で 1 回だけ)**

`SnapshotSearcher.cs:157` の `IsLarge` を一時的に `>=` へ変える。

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~AtExactThreshold"
```

Expected: **FAIL**。確認したら `>` へ戻し、再ビルドして緑に戻す。

**Step 4: Commit**

```powershell
git add tests/yEdit.Core.Tests/Search/SnapshotSearcherTests.cs
git commit -m "test(core): 検索の閾値境界と閾値以下端点の特性化テストを追加"
```

---

## Task 2: `ISnapshotSearchStrategy` を定義し `LiteralWindow` を抽出

**Files:**
- Create: `src/yEdit.Core/Search/ISnapshotSearchStrategy.cs`
- Create: `src/yEdit.Core/Search/LiteralWindowSearchStrategy.cs`
- Modify: `src/yEdit.Core/Search/SnapshotSearcher.cs`

**Step 1: インターフェースを作る**

`src/yEdit.Core/Search/ISnapshotSearchStrategy.cs`:

```csharp
using yEdit.Core.Buffers;

namespace yEdit.Core.Search;

/// <summary>
/// <see cref="SnapshotSearcher"/> の照合方式。文書サイズと照合条件から
/// <see cref="SnapshotSearcher"/> が 1 つ選び、以後の照合を丸ごと委譲する。
/// </summary>
/// <remarks>
/// <b>実装者への契約</b>:
/// <list type="bullet">
///   <item>照合条件は有効(<see cref="TextSearcher.IsValid"/>=true)であることが保証される。
///     無効時の短絡は <see cref="SnapshotSearcher"/> 側が持つため、実装で再度ガードしない。</item>
///   <item>オフセットは全て UTF-16 コード単位。</item>
/// </list>
/// </remarks>
internal interface ISnapshotSearchStrategy
{
    // 注: ここに書く契約は「Task 5 適用後の最終形(=弱い保証)」で書くこと。
    // 当初案にあった「位置引数はクランプ済み」という bullet は G-2 の発覚により削除した
    // (Task 5 で FindPrev だけクランプが外れるため、強い保証で書くと嘘になる)。
    // 引数ごとの保証の強さは Task 2 レビュー I-1 の表を参照。
    /// <summary>snap 全体のヒット件数。</summary>
    int Count(TextSnapshot snap);

    /// <summary>from 以降で最初のヒット(折り返しなし)。</summary>
    MatchSpan? FindNext(TextSnapshot snap, int from);

    /// <summary>開始位置が before より厳密に前にある最後のヒット(折り返しなし)。</summary>
    MatchSpan? FindPrev(TextSnapshot snap, int before);

    /// <summary>span が全ヒット中の何件目か(1 始まり, total)。ヒットでなければ null。</summary>
    (int Ordinal, int Total)? Locate(TextSnapshot snap, MatchSpan span);

    /// <summary>span が実際のヒットなら置換文字列を返す。違えば null。</summary>
    string? ReplacementAt(TextSnapshot snap, MatchSpan span, string replacement);

    /// <summary>[start, start+length) に完全に収まるヒットだけ置換した断片と件数。</summary>
    (string Fragment, int Count) ReplaceInRange(
        TextSnapshot snap,
        int start,
        int length,
        string replacement
    );
}
```

**Step 2: `LiteralWindowSearchStrategy` へ 6+4 メソッドを移動する**

`src/yEdit.Core/Search/LiteralWindowSearchStrategy.cs` を作り、`SnapshotSearcher.cs` から以下を**カット&ペーストで移動**する(`:161-333` と `:505-521`)。

移動対象:

| 移動元メソッド | 移動後 |
|---|---|
| `GetLiteralComparison` | `private StringComparison GetLiteralComparison()` |
| `CountLiteralWindow` | `public int Count(TextSnapshot snap)` |
| `FindNextLiteralWindow` | `public MatchSpan? FindNext(TextSnapshot snap, int from)` |
| `FindPrevLiteralWindow` | `public MatchSpan? FindPrev(TextSnapshot snap, int before)` |
| `LocateLiteralWindow` | `public (int Ordinal, int Total)? Locate(TextSnapshot snap, MatchSpan span)` |
| `ReplacementAtLiteralWindow` | `public string? ReplacementAt(TextSnapshot snap, MatchSpan span, string replacement)` |
| `ReplaceInRangeLiteralWindow` | `public (string Fragment, int Count) ReplaceInRange(TextSnapshot snap, int start, int length, string replacement)` |
| `IsWordChar` / `IsBoundary` / `IsWordBoundaryMatch` | `private static` のまま移動 |

**本体は一切変えない。** 変えるのは次の 4 点だけ:

1. クラス宣言と `using`(`System.Text` / `yEdit.Core.Buffers` が要る)
2. `_opts` / `_windowSize` をコンストラクタ注入のフィールドにする
3. **メソッド名の変更に伴い、内部の相互呼び出しを新名へ差し替える**
   (`CountLiteralWindow` 内の `FindNextLiteralWindow(...)` → `FindNext(...)` 等。
   `Count` / `Locate` / `ReplaceInRange` の 3 つが `FindNext` を呼んでいる)
4. `ReplaceInRange` のシグネチャ差の吸収 — 移動元は `(snap, start, end, replacement)` で
   **end を受けていた**。インターフェースは `(snap, start, length, replacement)` なので、
   メソッド冒頭で `int end = start + length;` を置き、以降の本体は無変更にする
5. **`FindPrev` の冒頭に `before = Math.Min(before, snap.CharLength);` を置く**
   (下記の注を読むこと)
6. `Locate` の戻り型を `(int, int)?` → `(int Ordinal, int Total)?` にする
   (タプル要素名がインターフェースと一致していないと CS8141。メタデータのみで挙動影響ゼロ。
   Task 3 レビューで「5 点の列挙から漏れていた 6 点目」として指摘された)

> **`FindPrev` のクランプについて(Task 1 レビュー G-2)**
>
> 現行のファサードは `Math.Min(before, snap.CharLength)` を**閾値超経路にしか掛けていない**
> (`SnapshotSearcher.cs:106`)。閾値以下経路は生の `before` を `TextSearcher` へ渡している(`:103`)。
> この差は実挙動に出る:
>
> ```
> パターン b*(useRegex) / 文書 "ab"(CharLength=2)/ 閾値以下経路
>   FindPrev(snap, 3) → MatchSpan { Start = 2, Length = 0 }
>   FindPrev(snap, 2) → MatchSpan { Start = 1, Length = 1 }
> ```
>
> したがって **`FindPrev` のクランプはファサードへ集約できない**(集約すると挙動変更になる)。
> クランプを必要とする 2 つの戦略が**自分で行う**形にし、材質化戦略は生の値を使う。
> この段階ではファサード側もまだクランプしているので二重になるが、`Math.Min` は冪等なので無害。
> Task 5 でファサード側を外す。

```csharp
using System.Text;
using yEdit.Core.Buffers;

namespace yEdit.Core.Search;

/// <summary>
/// 閾値超のリテラル照合(窓照合)。全文を材質化せず、
/// <c>windowSize</c> のウィンドウ + パターン長 -1 の overlap で走査する。
/// </summary>
/// <remarks>
/// <b>壊れる契約</b>(<see cref="SnapshotSearcher"/> の要約表も参照):
/// <see cref="SearchOptions.WholeWord"/> はエンジン内蔵の Unicode <c>\b</c> ではなく
/// ASCII 単純判定(<see cref="IsWordChar"/>)= 全角英数境界で
/// <see cref="MaterializedSearchStrategy"/> と差が出うる。
/// </remarks>
internal sealed class LiteralWindowSearchStrategy : ISnapshotSearchStrategy
{
    private readonly SearchOptions _opts;
    private readonly int _windowSize;

    internal LiteralWindowSearchStrategy(SearchOptions opts, int windowSize)
    {
        _opts = opts;
        _windowSize = windowSize;
    }

    // ... ここへ移動した本体 ...
}
```

**Step 3: `SnapshotSearcher` を新戦略へ配線する**

この段階では**セレクタ化はまだしない**。既存の三重分岐の形を保ったまま、literal 側の呼び先だけ差し替える。

- フィールドに `private readonly LiteralWindowSearchStrategy _literal;` を追加し、ctor で
  `_literal = new LiteralWindowSearchStrategy(options, _windowSize);`(`_windowSize` 代入の**後**)
- 6 箇所の `XxxLiteralWindow(...)` 呼び出しを `_literal.Xxx(...)` に差し替える
- `ReplaceInRange` の literal 分岐は `_literal.ReplaceInRange(snap, s, end - s, replacement)`

**Step 4: ビルドとテスト**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~Search"
```

Expected: **PASS**(0 warning)。

**Step 5: Commit**

```powershell
git add src/yEdit.Core/Search/
git commit -m "refactor(core): 閾値超リテラル照合を LiteralWindowSearchStrategy へ抽出"
```

---

## Task 3: `RegexPerLineSearchStrategy` を抽出

Task 2 と同じ手順を regex 側に行う。

**Files:**
- Create: `src/yEdit.Core/Search/RegexPerLineSearchStrategy.cs`
- Modify: `src/yEdit.Core/Search/SnapshotSearcher.cs`

**Step 1: 6+1 メソッドを移動する**

`SnapshotSearcher.cs:335-503` から移動(`*RegexPerLine` 6 個 + `ReadLine`)。

コンストラクタは `(SearchOptions opts, TextSearcher inner)` を受ける。本体が使っているのは
`_inner` だけで `_opts` は使っていないため、**`_opts` は渡さない**(未使用フィールドは
`-warnaserror` 下で警告になる)。

`ReplaceInRange` は Task 2 と同じく冒頭で `int end = start + length;` を置いて本体を無変更にする。
`FindPrev` も Task 2 と同じく冒頭へ `before = Math.Min(before, snap.CharLength);` を置く
(理由は Task 2 の注を参照 — このクランプはファサードへ集約できない)。

```csharp
using System.Text;
using yEdit.Core.Buffers;

namespace yEdit.Core.Search;

/// <summary>
/// 閾値超の正規表現照合(行単位)。1 行ずつ切り出して
/// <see cref="TextSearcher"/> に適用し、行頭オフセットを足して文書座標へ戻す。
/// </summary>
/// <remarks>
/// <b>壊れる契約</b>(<see cref="SnapshotSearcher"/> の要約表も参照):
/// <list type="bullet">
///   <item>改行を跨ぐパターンは絶対にヒットしない。</item>
///   <item>アンカー(<c>^</c> / <c>$</c> / <c>\A</c> / <c>\Z</c> / <c>\G</c>)は
///     「文書の先頭/末尾」ではなく「行の先頭/末尾」に束縛される。
///     <c>SnapshotSearcherRegexAnchorTests</c> がこの挙動を凍結している。</item>
/// </list>
/// </remarks>
internal sealed class RegexPerLineSearchStrategy : ISnapshotSearchStrategy
{
    private readonly TextSearcher _inner;

    internal RegexPerLineSearchStrategy(TextSearcher inner) => _inner = inner;

    // ... ここへ移動した本体 ...
}
```

**Step 2: `SnapshotSearcher` を配線する**

`private readonly RegexPerLineSearchStrategy _regexPerLine;` を追加し、ctor で
`_regexPerLine = new RegexPerLineSearchStrategy(_inner);`(`_inner` 代入の**後**)。
6 箇所の `XxxRegexPerLine(...)` を `_regexPerLine.Xxx(...)` へ差し替える。

**Step 3: ビルドとテスト**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~Search"
```

Expected: **PASS**。特に `SnapshotSearcherRegexAnchorTests` が緑であること。

**Step 4: Commit**

```powershell
git add src/yEdit.Core/Search/
git commit -m "refactor(core): 閾値超 regex 照合を RegexPerLineSearchStrategy へ抽出"
```

---

## Task 4: `MaterializedSearchStrategy` を抽出しキャッシュを持たせる

ここが本テーマの中心。**キャッシュは新しい状態なので、テストを先に書く**(Task 1 の特性化テストとは性質が違う = 変更前では成立しない新しい不変条件)。

**Files:**
- Create: `src/yEdit.Core/Search/MaterializedSearchStrategy.cs`
- Create: `tests/yEdit.Core.Tests/Search/MaterializedSearchStrategyTests.cs`
- Modify: `src/yEdit.Core/Search/SnapshotSearcher.cs`

> **作業環境の注意(Task 3 実施時に判明)**: Git Bash の `sed -i` は CRLF ファイルを
> **LF に書き換える**(`.gitattributes` は `* text=auto eol=crlf`)。範囲抽出で移動する手法を採るなら、
> **commit 前に `dotnet csharpier format` を明示実行**して CRLF に戻すこと
> (`.csharpierrc.json` は `"endOfLine": "crlf"`)。入れ忘れても pre-commit フックが直すが、
> その場合フックが再ステージした内容を自分で検証していない状態になる。

**Step 1: 失敗するテストを書く**

`tests/yEdit.Core.Tests/Search/MaterializedSearchStrategyTests.cs`:

```csharp
using Xunit;
using yEdit.Core.Buffers;
using yEdit.Core.Search;

namespace yEdit.Core.Tests.Search;

/// <summary>
/// <see cref="MaterializedSearchStrategy"/> の材質化キャッシュ。
/// これは<b>新しい不変条件</b>であり、リファクタ前の src では成立しない
/// (キャッシュ自体が存在しないため)。よって「変更前で緑だったから挙動不変」の
/// 証明材料には数えない(設計書 §7.2)。
/// </summary>
public class MaterializedSearchStrategyTests
{
    private static MaterializedSearchStrategy Make(string pattern) =>
        new(new TextSearcher(new SearchOptions(pattern, MatchCase: true)));

    [Fact]
    public void SameSnapshot_reuses_materialized_text()
    {
        var snap = TextBuffer.FromString("ab ab").Current;
        var s = Make("ab");

        Assert.Equal(2, s.Count(snap));
        Assert.Equal(2, s.Count(snap)); // 2 回目はキャッシュから
        Assert.Equal(1, s.MaterializeCountForTest);
    }

    [Fact]
    public void DifferentSnapshot_rematerializes()
    {
        var s = Make("ab");
        var first = TextBuffer.FromString("ab").Current;
        var second = TextBuffer.FromString("ab ab").Current;

        Assert.Equal(1, s.Count(first));
        Assert.Equal(2, s.Count(second));
        Assert.Equal(2, s.MaterializeCountForTest);
    }

    [Fact]
    public void EditedBuffer_yields_new_snapshot_and_fresh_results()
    {
        // 本命の回帰: 編集後に同じ戦略インスタンスで検索すると新しい本文が見える
        // (参照同一性でのキャッシュ無効化が効いていることの証明)。
        var buffer = TextBuffer.FromString("ab");
        var s = Make("ab");
        Assert.Equal(1, s.Count(buffer.Current));

        buffer.Insert(2, " ab");

        Assert.Equal(2, s.Count(buffer.Current));
        Assert.Equal(2, s.MaterializeCountForTest);
    }
}
```

> **注意:** `buffer.Insert(2, " ab")` の API 名・引数順は `src/yEdit.Core/Buffer/TextBuffer.cs` の
> 実シグネチャに合わせること。合わない場合は `TextBuffer` の公開編集 API を確認して読み替える
> (このテストの本質は「編集して `Current` が別インスタンスになる」ことなので、
> どの編集 API でもよい)。

**Step 2: テストが失敗することを確認**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
```

Expected: **FAIL**(`MaterializedSearchStrategy` が存在しない = コンパイルエラー)。

**Step 3: 実装する**

`src/yEdit.Core/Search/MaterializedSearchStrategy.cs`:

```csharp
using yEdit.Core.Buffers;

namespace yEdit.Core.Search;

/// <summary>
/// 全文を材質化して <see cref="TextSearcher"/> に適用する照合。
/// <b>意味論の「正」</b>であり、他 2 戦略の差異はこの戦略との差として記述される。
/// </summary>
/// <remarks>
/// 材質化した文字列は<b>スナップショット単位で保持</b>する。
/// <see cref="TextSnapshot"/> は不変で、<see cref="TextBuffer.Current"/> は編集・Undo・Redo の
/// ときだけ差し替わるフィールド返しなので、参照同一性が「文書が変わっていない」の正当な signal になる
/// (同じ idiom を <see cref="TextBuffer.Modified"/> が既に採用している)。
/// 保持するのは常に最大 1 本で、スナップショットが変われば古い文字列は参照が切れる。
/// </remarks>
internal sealed class MaterializedSearchStrategy : ISnapshotSearchStrategy
{
    private readonly TextSearcher _inner;

    private TextSnapshot? _cachedSnapshot;
    private string _cachedText = string.Empty;

    /// <summary>テスト観測用: 実際に材質化した回数。キャッシュが効いていることを assert 化する seam。</summary>
    internal int MaterializeCountForTest { get; private set; }

    internal MaterializedSearchStrategy(TextSearcher inner) => _inner = inner;

    /// <summary>snap の全文。同一スナップショットの連続呼び出しでは前回の結果を返す。</summary>
    private string TextOf(TextSnapshot snap)
    {
        if (ReferenceEquals(_cachedSnapshot, snap))
            return _cachedText;
        _cachedText = snap.GetText(0, snap.CharLength);
        _cachedSnapshot = snap;
        MaterializeCountForTest++;
        return _cachedText;
    }

    public int Count(TextSnapshot snap) => _inner.Count(TextOf(snap));

    public MatchSpan? FindNext(TextSnapshot snap, int from) => _inner.FindNext(TextOf(snap), from);

    public MatchSpan? FindPrev(TextSnapshot snap, int before) =>
        _inner.FindPrev(TextOf(snap), before);

    public (int Ordinal, int Total)? Locate(TextSnapshot snap, MatchSpan span) =>
        _inner.Locate(TextOf(snap), span);

    public string? ReplacementAt(TextSnapshot snap, MatchSpan span, string replacement) =>
        _inner.ReplacementAt(TextOf(snap), span, replacement);

    public (string Fragment, int Count) ReplaceInRange(
        TextSnapshot snap,
        int start,
        int length,
        string replacement
    ) => _inner.ReplaceInRange(TextOf(snap), start, length, replacement);
}
```

**Step 4: `SnapshotSearcher` を配線する**

`private readonly MaterializedSearchStrategy _materialized;` を追加し、ctor で
`_materialized = new MaterializedSearchStrategy(_inner);`。
`Materialize(snap)` を使っていた 6 箇所を `_materialized.Xxx(snap, ...)` へ差し替え、
`private static string Materialize(...)` を削除する。

**`ReplaceInRange` の引数に注意。** 現行の材質化分岐は生の `start, length` を
`_inner.ReplaceInRange` へ渡している。ここを他戦略と揃えて `(s, end - s)` にしても結果は同一である:
`TextSearcher.ReplaceInRange` が再度 `s' = Clamp(s,0,L)`、`end' = Clamp(s + (end-s), s, L)` を
行い、`s <= end <= L` なので `s' == s` / `end' == end` になる(冪等)。
Task 1 の `ReplaceInRange_ClampsOutOfRangeArgs_below_threshold` がこの等価性を守る網である。

**Step 5: テストが通ることを確認**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~Search"
```

Expected: **PASS**(既存 + Task 1 + 新規すべて)。

**Step 6: Commit**

```powershell
git add src/yEdit.Core/Search/ tests/yEdit.Core.Tests/Search/MaterializedSearchStrategyTests.cs
git commit -m "refactor(core): 材質化照合を MaterializedSearchStrategy へ抽出しスナップショット単位で再利用"
```

---

## Task 5: `SnapshotSearcher` をセレクタ + ファサードへ畳む

3 戦略が揃ったので、6 箇所に散った三重分岐を 1 箇所へ集約する。

**Files:**
- Modify: `src/yEdit.Core/Search/SnapshotSearcher.cs`

**Step 1: セレクタを足して 6 メソッドを畳む**

```csharp
    /// <summary>snap のサイズと照合条件から戦略を選ぶ(分岐はこの 1 箇所だけ)。</summary>
    /// <remarks>
    /// 閾値超の 2 戦略は snapshot 非依存なので ctor で 1 個ずつ作って使い回す。
    /// 閾値判定は <c>&gt;</c>(ちょうど一致は「閾値以下」= 材質化経路)。
    /// <c>&gt;=</c> にすると閾値ちょうどの文書の意味論が変わる = 挙動変更になる
    /// (<c>AtExactThreshold_uses_below_path_not_above</c> が固定)。
    /// </remarks>
    private ISnapshotSearchStrategy StrategyFor(TextSnapshot snap) =>
        snap.CharLength <= _thresholdChars ? _materialized
        : _opts.UseRegex ? _regexPerLine
        : _literal;
```

public 6 メソッドは次の形に揃える(`IsValid` の短絡と位置引数のクランプはファサードに残す)。

```csharp
    public int Count(TextSnapshot snap) => IsValid ? StrategyFor(snap).Count(snap) : 0;

    public MatchSpan? FindNext(TextSnapshot snap, int from)
    {
        if (!IsValid)
            return null;
        if (from < 0)
            from = 0;
        if (from > snap.CharLength)
            return null;
        return StrategyFor(snap).FindNext(snap, from);
    }

    // Math.Min(before, snap.CharLength) を<b>ここへ集約してはいけない</b>(Task 1 レビュー G-2)。
    // 閾値以下経路は生の before を TextSearcher へ渡すのが現行挙動で、文書長を超える before と
    // ゼロ幅ヒットの組み合わせで結果が変わる。クランプは必要とする 2 戦略が自分で行う。
    public MatchSpan? FindPrev(TextSnapshot snap, int before) =>
        IsValid && before > 0 ? StrategyFor(snap).FindPrev(snap, before) : null;

    public (int Ordinal, int Total)? Locate(TextSnapshot snap, MatchSpan span) =>
        IsValid ? StrategyFor(snap).Locate(snap, span) : null;

    public string? ReplacementAt(TextSnapshot snap, MatchSpan span, string replacement) =>
        IsValid ? StrategyFor(snap).ReplacementAt(snap, span, replacement) : null;

    public (string Fragment, int Count) ReplaceInRange(
        TextSnapshot snap,
        int start,
        int length,
        string replacement
    )
    {
        int s = Math.Clamp(start, 0, snap.CharLength);
        int end = Math.Clamp(start + length, s, snap.CharLength);
        if (!IsValid)
            return (snap.GetText(s, end - s), 0);
        return StrategyFor(snap).ReplaceInRange(snap, s, end - s, replacement);
    }
```

`IsLarge` は `StrategyFor` に吸収されるので削除する。

**Step 2: クラスコメントを要約表に差し替える(設計 §6)**

「壊れる契約」の詳細は各戦略クラスへ移したので、ファサードには**どの条件でどれが選ばれるか**の表を残す。詳細は消さずに移動先を指すこと。

```csharp
/// <summary>
/// <see cref="TextSnapshot"/> ベースの検索/置換ファサード。
/// 文書サイズと照合条件から照合方式(<see cref="ISnapshotSearchStrategy"/>)を 1 つ選び委譲する。
/// <para>
/// <b>戦略の選択規則</b>:
/// <list type="table">
///   <item><term>CharLength &lt;= 閾値</term>
///     <description><see cref="MaterializedSearchStrategy"/> — 意味論の「正」</description></item>
///   <item><term>閾値超 かつ 正規表現</term>
///     <description><see cref="RegexPerLineSearchStrategy"/> — 改行跨ぎ不可・アンカーは行に束縛</description></item>
///   <item><term>閾値超 かつ リテラル</term>
///     <description><see cref="LiteralWindowSearchStrategy"/> — WholeWord が ASCII 判定</description></item>
/// </list>
/// 各方式の「壊れる契約」の詳細は、それぞれのクラスの remarks を参照。
/// </para>
/// <para>
/// <b>残る制約</b>: 閾値超の <see cref="ReplaceInRange"/> は依然として置換後 Fragment を
/// string で組み立てる(設計 2026-08-05 §8 S-1)。
/// </para>
/// </summary>
```

**Step 3: `StrategyFor` を `internal` にして戦略選択を直接固定する(Task 1 レビュー S-2)**

`src/yEdit.Core/yEdit.Core.csproj:12` に `InternalsVisibleTo("yEdit.Core.Tests")` が既にあるので、
`StrategyFor` を `internal` にすればテストから戦略型を直接 assert できる。

これまでの境界テストは「改行跨ぎ regex がヒットするか」という**意味論的帰結**で経路を観測していた。
これは間接観測で、将来 `RegexPerLineSearchStrategy` が改行跨ぎを拾えるようになると
**境界が反転していても緑のまま黙って無力化する**。型を直接見れば意味論に依存しない。

`tests/yEdit.Core.Tests/Search/SnapshotSearcherTests.cs` へ追加:

```csharp
    [Theory]
    [InlineData(5, false, typeof(MaterializedSearchStrategy))] // 境界ちょうど = 閾値以下
    [InlineData(4, false, typeof(LiteralWindowSearchStrategy))]
    [InlineData(4, true, typeof(RegexPerLineSearchStrategy))]
    [InlineData(5, true, typeof(MaterializedSearchStrategy))] // 境界ちょうどは regex でも材質化
    public void StrategyFor_selects_expected_strategy(int threshold, bool useRegex, Type expected)
    {
        var snap = Snap("ab\ncd"); // CharLength == 5
        var s = MakeLarge("b", useRegex: useRegex, matchCase: true, threshold: threshold, window: 6);
        Assert.IsType(expected, s.StrategyFor(snap));
    }
```

**既存の意味論ベースの境界テストは残すこと。** 直接観測へ置き換えるのではなく二重に張る
(型が正しくても委譲先を書き間違えれば、意味論テストだけが捕まえる)。

**さらに: 閾値超経路の `FindPrev(snap, CharLength + 1)` テストを足すこと(Task 2 レビュー由来)**

Task 1 で足した `FindPrev_BeforePastEnd_is_not_clamped_below_threshold` は `Make(...)` =
**閾値以下経路専用**で、閾値超経路には `before > CharLength` を叩くテストが 1 本も無い。

Task 2 レビューアの実測: `LiteralWindowSearchStrategy.FindPrev` 冒頭の
`before = Math.Min(before, snap.CharLength);` を**行ごと削除しても現在は全緑**である
(ファサード側のクランプがまだ残っているため)。**Task 5 でファサード側を外した瞬間に、
この行は load-bearing になるのに無防備**になる。

`MakeLarge` を使った以下を追加してから、ファサードのクランプを外すこと:

```csharp
    [Fact]
    public void FindPrev_BeforePastEnd_is_clamped_above_threshold()
    {
        // 閾値超経路は before を CharLength でクランプする(閾値以下経路との非対称は意図的)。
        // Task 5 でファサードのクランプを外した後、この網が戦略側のクランプを守る。
        var snap = Snap("ab XY ab");
        var s = MakeLarge("ab", matchCase: true, threshold: 4, window: 6);
        Assert.Equal(s.FindPrev(snap, snap.CharLength), s.FindPrev(snap, snap.CharLength + 100));
    }
```

期待値は実挙動から導出し、根拠を説明できること。

**さらに: 2 戦略の `FindPrev` クランプを戦略レベルで直接叩くこと(Task 3 レビュー N-1)**

Task 3 レビューアが実測: **`LiteralWindowSearchStrategy` / `RegexPerLineSearchStrategy` の
どちらの `FindPrev` クランプ行を削除しても、現在は Search 81 件が全緑**である
(ファサードが先にクランプするため)。両クランプのコメントは
「Task 5 でファサード側が外れるとこの 1 行が唯一の防御になる」と宣言しているのに、
**その昇格の瞬間に網が無い**。

`yEdit.Core.csproj:12` の `InternalsVisibleTo("yEdit.Core.Tests")` により、戦略を直接構築できる。
`new LiteralWindowSearchStrategy(...)` と `new RegexPerLineSearchStrategy(...)` の両方について
`FindPrev(snap, CharLength + 1)` を直接叩くテストを足すこと。上のファサード経由テストは
literal 経路しか通らないので、**2 戦略ぶん必要**。

**材質化戦略に届く位置引数は「完全に未正規化」(Task 4 申し送り S-F)**

Task 5 最大の地雷。ファサードの材質化分岐は**すべてのクランプ・早期 return より手前**にある:

```csharp
if (!IsLarge(snap))
    return _materialized.FindNext(snap, from);   // ← ここで返る
if (from < 0) from = 0;                          // ← 材質化経路は通らない
if (from > snap.CharLength) return null;
```

つまり `ISnapshotSearchStrategy` の契約表は、**Task 4 時点では 4 行中 2 行が材質化戦略に
当てはまらない**(正規化しているのは委譲先の `TextSearcher` 自身)。

> **訂正(Task 4 レビュー Important-1)**: 当初ここへ「**1 つも当てはまらない**」と書いたが誤り。
> `ReplaceInRange` のクランプ(`SnapshotSearcher.cs:151-152`)だけは元から材質化分岐より**手前**にあり、
> **Task 4 が `(s, end - s)` へ統一したことで、この行は既に真**になっている。
> `span` の行は「未検証」= 何も保証していないので破りようがなく、自明に真。
> **当てはまらないのは `FindNext` の `from` と `FindPrev` の `before` の 2 行だけ**。

契約表は「Task 5 適用後の最終形」で書いてあるので、**残る 2 行も Task 5 で畳んだ瞬間に真になる**。
それまでは doc がコードより先行している状態(ブランチ内に閉じるので main には出ない)。

畳むときの具体的な帰結:

| 正規化 | 前へ出せるか | 根拠 |
|---|---|---|
| `FindNext` の `from < 0 → 0` / `from > CharLength → null` | **出せる** | `TextSearcher.FindNext` が同じことをしている(`TextSearcher.cs:68-71`)。`text.Length == snap.CharLength` |
| `FindPrev` の `before <= 0 → null` | **出せる** | `TextSearcher.FindPrev` は `m.Index >= before` で break するので `before <= 0` は必ず null |
| `FindPrev` の `Math.Min(before, CharLength)` | **出せない** | 出すと材質化経路の挙動が変わる(反例: `b*` / `"ab"` で `FindPrev(3)`=`(2,0)` / `FindPrev(2)`=`(1,1)`) |
| `ReplaceInRange` の `Math.Clamp` | **出せる** | `TextSearcher.ReplaceInRange` の再クランプが冪等(Task 4 で `(s, end - s)` へ統一済み) |

**前進ガードの非対称は 3 対 3 ではなく 3 対 1(Task 3 fixup で判明)**

統合を検討するときに必ず踏む地雷なので先に書いておく。`Math.Max(1, ...)` の出現数は:

| 戦略 | 箇所 | 生死 |
|---|---|---|
| `LiteralWindowSearchStrategy` | **3 箇所**(`Count` / `Locate` / `ReplaceInRange`) | **全て実質デッド**(`plen == 0` は早期 return するため下流で `plen >= 1` が保証される) |
| `RegexPerLineSearchStrategy` | **1 箇所**(`Locate` のみ) | **生きている**(網 = `Locate_RegexZeroWidthHits_...`) |

regex 側の `Count` は `_inner.Count` へ、`ReplaceInRange` は `_inner.ReplaceInRange` へ委譲しており
自前の歩進ループを持たない。したがって「同じ形の式が両戦略に 3 つずつある」という見え方は誤りで、
**リテラル側 3 箇所は変異させても永久に kill されない**(テストを増やしても無意味=真にデッド)。
最終レビューのミューテーションでリテラル側が生存しても、それは欠陥ではなく戦略分離により
保証が閉じた結果である、と説明できること。

**中間状態の注記は 2 箇所ではなく 4 箇所(Task 4 品質レビュー S-2)**

下の 2 箇所に加え、**「現在はファサード側にも同じクランプがあり二重だが」という記述が
さらに 2 箇所**ある(`LiteralWindowSearchStrategy.cs:111-112` / `RegexPerLineSearchStrategy.cs:75-76`)。
Task 5 でファサードのクランプが外れると、この「二重だが」は**偽になる**(後半の
「唯一の防御になる」は真になる)。チェックリストを字義どおり追うと 2 箇所しか直らない。

**救い**: 4 箇所すべてが `Task 5` というリテラルを含む。**Task 5 を閉じる前に必ず
`rg "Task 5" src/yEdit.Core/Search/` で全件を洗うこと。**

**Task 5 完了時に消すべき「中間状態の注記」2 箇所(Task 4 fixup 申し送り)**

Task 4 fixup で、「契約表の `FindNext` / `FindPrev` の 2 行は中間状態では材質化戦略に適用されない」
という**同じ事実を 2 箇所に**書いた(意図的 — 契約側から読む人と実装側から読む人の両方が引っかかるように):

1. `ISnapshotSearchStrategy` の契約表**直後**の注記
2. `MaterializedSearchStrategy` の第 3 para の箇条書き

**Task 5 でファサードを畳むと 2 行とも真になるので、両方まとめて消すこと。**
片方だけ消すと再び片肺の記述が残る。

**`MaterializeCountForTest` seam を消さないこと(同上)**

`Cache_holds_at_most_one_snapshot`(A→B→A で `MaterializeCountForTest == 3`)は、
**「保持は最大 1 本」を守る唯一の網**である。結果値からは辞書実装と区別できない
(辞書なら 2 になるが、検索結果自体は同じ)。ファサードを畳む際にこの観測 seam を
消したくなっても消さないこと。

**さらに: `RegexPerLineSearchStrategy` のクラス doc に「選択の前提」節を足す(N-2)**

`LiteralWindowSearchStrategy` には Task 2 fixup で「この戦略は `UseRegex == false` のときだけ
選ばれる」という節が入ったが、regex 側に対応物が無い。`RegexPerLineSearchStrategy` は
`TextSearcher` へ丸投げするため `UseRegex=false` の options で構築しても壊れないが、
選択規則が暗黙の前提になっている。1 文足して 3 戦略の doc を対称にすること。

**Step 4: ビルドとテスト**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~Search"
```

Expected: **PASS**。

**Step 5: Commit**

```powershell
git add src/yEdit.Core/Search/SnapshotSearcher.cs tests/yEdit.Core.Tests/Search/SnapshotSearcherTests.cs
git commit -m "refactor(core): SnapshotSearcher を戦略セレクタ + ファサードへ畳む"
```

---

## Task 6: `IFindReplaceView` に Dismissed 通知を足す

設計 §5.2 精密化 1。ダイアログは**閉じない**(`OnFormClosing` が `UserClosing` をキャンセルして `Hide`)ので、「ユーザーが検索を終えた」を発生源から通知する。

**Files:**
- Modify: `src/yEdit.App/Abstractions/IFindReplaceView.cs`
- Modify: `src/yEdit.App/FindReplaceDialog.cs`
- Modify: `tests/yEdit.App.Tests/Fakes/FakeFindReplaceView.cs`

**Step 1: 契約を足す**

`IFindReplaceView` に追記:

```csharp
    /// <summary>
    /// ユーザーが検索を終えた(閉じるボタン / Escape / タイトルバーの×)。
    /// <para>
    /// <b>G-2 の自動 Hide では発火しない。</b> 「次を検索」成功後にダイアログが自らを
    /// Hide するのは一時退避であって終了ではなく、その後も F3 で検索は続く。
    /// したがって購読側は <see cref="Visible"/> を終了判定に使ってはならない
    /// (発生源でしか区別できない)。
    /// </para>
    /// </summary>
    event EventHandler? Dismissed;
```

**Step 2: `FindReplaceDialog` で発火させる**

`Hide()` を呼んでいる 6 箇所のうち、**ユーザー終了の 3 箇所だけ**を経由メソッドに差し替える。

```csharp
    public event EventHandler? Dismissed;

    /// <summary>ユーザー終了経路の Hide(G-2 の自動 Hide とは区別して Dismissed を発火する)。</summary>
    private void HideByUser()
    {
        Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
```

差し替え:

| 箇所 | 変更 |
|---|---|
| `_close.Click`(`:60`) | `Hide()` → `HideByUser()` |
| `ProcessCmdKey` の `Keys.Escape`(`:108`) | `Hide()` → `HideByUser()` |
| `OnFormClosing` の `UserClosing`(`:129`) | `Hide()` → `HideByUser()` |

**変えないもの**(G-2 の一時退避。ここを変えると F3 連打でキャッシュが毎回落ちる):
`_next.Click`(`:51`) / `_prev.Click`(`:56`) / `ProcessCmdKey` の `Keys.Enter`(`:118`)。

**Step 3: フェイクを追随させる**

`FakeFindReplaceView` に追記:

```csharp
    public event EventHandler? Dismissed;

    /// <summary>テストから「ユーザーが検索を終えた」を再現する(実ダイアログの閉じる/Escape/×相当)。</summary>
    public void RaiseDismissed()
    {
        Visible = false;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
```

**Step 4: ビルドとテスト**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~SearchController"
```

Expected: **PASS**(この時点では購読者がいないため挙動は完全に不変)。

`Dismissed` に購読者がいないことで CS0067(未使用イベント)が出る場合は、Task 7 で購読を足すまで一時的に発生しうる。**出たら Task 6 と Task 7 を 1 コミットにまとめる**(`-warnaserror` を緩めない)。

**Step 5: Commit**

```powershell
git add src/yEdit.App/Abstractions/IFindReplaceView.cs src/yEdit.App/FindReplaceDialog.cs tests/yEdit.App.Tests/Fakes/FakeFindReplaceView.cs
git commit -m "feat(app): 検索ダイアログにユーザー終了通知(Dismissed)を追加"
```

---

## Task 7: `SearchController` で searcher を保持する

**Files:**
- Modify: `src/yEdit.App/SearchController.cs`
- Test: `tests/yEdit.App.Tests/SearchControllerTests.cs`(**新規テストのみ追加・既存は無変更**)

**Step 1: 失敗するテストを書く**

`SearchControllerTests` の末尾に追加する。既存テストのヘルパ(fixture の作り方)は同ファイル冒頭に合わせること。

```csharp
    [Fact]
    public void Searcher_is_reused_while_options_unchanged_and_dropped_on_dismiss()
    {
        // 挙動としての観測点: Dismissed 後も検索は従来どおり動くこと(破棄が壊さない)。
        // キャッシュの有無そのものは Core 側 MaterializedSearchStrategyTests が固定する。
        var (controller, view, editor) = MakeFixture("ab ab");
        view.Pattern = "ab";
        controller.OpenFind();

        Assert.True(controller.FindNext());
        Assert.True(controller.FindNext());

        view.RaiseDismissed();

        // 破棄後も再検索できる(searcher が作り直される)
        Assert.True(controller.FindNext() || controller.FindPrev());
    }
```

> `MakeFixture` は既存テストの生成ヘルパ名に読み替えること。無ければ既存テストの
> セットアップをそのまま踏襲する(**既存テストの本体は変更しない**)。

**Step 2: テストが失敗することを確認**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
```

Expected: **FAIL**(`RaiseDismissed` は Task 6 で入っているのでコンパイルは通る。
`MakeFixture` が無ければコンパイルエラー → ヘルパ名を実体に合わせて修正してから進む)。

> **キャッシュの保持量について(Task 4 申し送り S-G)**
>
> `MaterializedSearchStrategy` はスナップショットとその全文 string を**最大 1 本ずつ強参照**で保持する。
> `SnapshotSearcher` が長寿命なら、最後に検索した文書の本文(と背後のピース木)がその間解放されない。
> **本タスクで `SearchController` が searcher を保持するようになるので、ここが効いてくる。**
> だからこそ下の破棄トリガ 3 つ((i) 照合条件の変化 /(ii) 文書切替 /(iii) Dismissed)が要る。
> 破棄が漏れると「検索を終えた後も文書 1 本ぶんが生き続ける」ことになる。

**Step 3: 実装する**

フィールドを足す:

```csharp
    // 照合条件が変わるまで searcher を使い回す。作り直すと内部の Regex が再コンパイルされ、
    // MaterializedSearchStrategy の材質化キャッシュも毎回捨てられる(打鍵ごとの UpdateCount で効く)。
    private SearchOptions? _searcherOptions;
    private SnapshotSearcher? _searcher;
```

解決メソッドを足す:

```csharp
    /// <summary>照合条件に対応する searcher を返す(条件が変われば作り直す)。条件が無効なら null。</summary>
    private SnapshotSearcher? ResolveSearcher()
    {
        var opts = CurrentOptions();
        if (opts is null)
        {
            // Task 4 品質レビュー I-3: 検索語を空にしたら保持中の searcher(とキャッシュ)を落とす。
            // 素の `return null;` だと _searcher に触れないため、空にしても保持が続く。
            DropSearcher();
            return null;
        }
        if (_searcher is null || _searcherOptions != opts)
        {
            _searcher = new SnapshotSearcher(opts);
            _searcherOptions = opts;
        }
        return _searcher;
    }

    /// <summary>保持中の searcher を捨てる(材質化キャッシュごと解放する)。</summary>
    private void DropSearcher()
    {
        _searcher = null;
        _searcherOptions = null;
    }
```

`SearchOptions` は record なので `!=` は構造的比較になる(`SearchOptions.cs:7`)。

4 箇所の `var searcher = new SnapshotSearcher(opts);` を差し替える。
各メソッドは既に直前で `CurrentOptions()` を呼んで null 判定しているので、
**`opts` のローカルはそのまま残し、`searcher` の生成だけを `ResolveSearcher()` に置き換える**
(`UpdateCount` / `Find` / `ReplaceOne` / `ReplaceAll` の 4 箇所)。
`ResolveSearcher()` が null を返すのは `opts` が null のときだけなので、
既存の null 判定と重複しても挙動は変わらない。

破棄の配線 — ctor の既存購読に追記:

```csharp
        _docs.ActiveDocumentChanged += (_, _) =>
        {
            _lastHit = null;
            _selectionScope = null;
            DropSearcher(); // 別文書の材質化キャッシュを持ち越さない
            if (_view?.Visible == true)
                UpdateCount();
        };
```

> **【設計変更】`ActiveDocumentChanged` はタブクローズで発火しない(Task 4 品質レビュー I-1)**
>
> 当初「発火するか確認し、しないなら別途手当てする」と書いたが、**答えはリポジトリ内に既にあった**。
> `MainForm.cs:954` に「選択タブ削除時の `TabControl.Selected` 発火は WinForms の仕様上保証されない」
> という注記があり、`DocumentManager.ActiveDocumentChanged` の唯一の発火源はその `_tabs.Selected`
> (`DocumentManager.cs:20,161`)。したがって**トリガ (ii) はタブを閉じる経路を覆わない**。
>
> `TryClose`(`DocumentManager.cs:113-122`)は `doc.Editor.Dispose()` まで済ませるのに、
> 材質化キャッシュは閉じた文書の `TextSnapshot` → ピース木 → `TextChunk` を掴んだままになる。
>
> - 最後の 1 枚を閉じる場合は `MainForm.CloseActiveTab` が `Close()`(アプリ終了)へ抜けるので無害
> - 問題は**複数タブのうち 1 枚を閉じる**ケース
> - `TryClose` の呼び出し元は `MainForm.cs:947` のほか `FileController.cs:132 / 145 / 661`
>
> **対応**: `DocumentManager` に `DocumentClosed` イベントを足し、`SearchController` が
> `DropSearcher()` を購読する。`CloseActiveTab` の「明示更新ブロック」に相乗りさせる案は
> `FileController` の 3 経路を取りこぼすので**採らない**。

> **保持されるのは string だけではない(Task 4 レビュー S-G の精密化)**
>
> `_cachedSnapshot` は `TextSnapshot` → ピース木 → `TextChunk` のバイト配列**全体**をピン留めする。
> string は最大 64MB(閾値 32M chars)だが、**背後の文書バイトは最大 512MB**
> (`TextBuffer.MaxTotalBytes`)。破棄トリガが漏れると「**閉じたタブの文書がまるごと生き残る**」形になる。
>
> したがってトリガ (ii) は**タブを閉じた場合、とりわけ最後のタブを閉じて文書ゼロになる場合を含むこと**。
> `ActiveDocumentChanged` がその経路で発火するかを実際に確認し、発火しないなら別途手当てすること。
> 戦略側に `Reset()` 相当(`_cachedSnapshot = null; _cachedText = string.Empty;`)を足して
> searcher 経由で叩く形が素直。

> **キャッシュは投機ではない — 今日の本番経路で既に load-bearing(Task 4 レビュー Important-2)**
>
> `SearchController.ReplaceOne` は **1 個の searcher を編集前 `snap` と編集後 `snap2` の両方に使う**
> (`:186/200/206` と `:228/237`)。つまり「searcher が複数スナップショットにまたがる」状況は
> **Task 7 を待たずに既に存在する**。参照同一性による無効化を壊す変異(「一度材質化したら二度と
> 無効化しない」)は、`App.Tests.SearchControllerTests.ReplaceOne_*` の 3 件が実際に検出する。
> **PR description に書く価値のある事実**(キャッシュ導入が投機的な作り込みではないことの根拠)。

`Open` でビューを生成する箇所に Dismissed 購読を足す:

```csharp
        if (_view is null || _view.IsDisposed)
        {
            _view = _viewFactory(new FindReplaceCallbacks(...));
            _view.Dismissed += (_, _) => DropSearcher();
        }
```

**`SnapshotSearcher` のクラス doc を更新すること(Task 4 fixup 申し送り)**

`SnapshotSearcher` のクラス doc に「利用者は `SearchController` の 4 箇所のみで、いずれも
UI スレッド」と書いてある(Task 4 品質レビュー I-4 でスレッド非安全を明記した際の記述)。
**本タスクで searcher が長寿命化すると、まさにこの doc が効く場面になる。** 更新漏れに注意。

**Step 4: テストが通ることを確認**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~SearchController"
```

Expected: **PASS**(既存 567 行ぶんが無変更で緑であること)。

**Step 5: Commit**

```powershell
git add src/yEdit.App/SearchController.cs tests/yEdit.App.Tests/SearchControllerTests.cs
git commit -m "refactor(app): SearchController が照合条件ごとに searcher を保持する"
```

---

## Task 8: 仕上げ(整形・全層テスト・自己レビュー)

**Step 0: 削除済みシンボルを指すコメントの一括棚卸し(Task 3 申し送り S-C)**

Task 2〜4 で `*LiteralWindow` / `*RegexPerLine` / `Materialize` という private メソッド名が消える。
テスト側のコメントがこれらを参照したまま残るため、まとめて現行 API 名へ読み替える。
各タスクは「`tests/` 無変更」の制約下で進めたので、ここが唯一の回収点になる。

```powershell
rg -n "RegexPerLine|LiteralWindow|Materialize|IsLarge" tests/
```

**既知のヒット(Task 5 実装者の申告)**: `tests/yEdit.Core.Tests/Search/SnapshotSearcherTests.cs`
の `:270 / :286 / :344` に、Task 5 で削除した `IsLarge` を指すコメントが 3 箇所残っている。
各タスクは「既存テスト無変更」の制約下で進めたため、ここが回収点。

ヒットしたコメントを現行のクラス名(`RegexPerLineSearchStrategy` 等)へ読み替える。
**コメントのみ。テストのロジックは触らない。**
先例: ブランチ外の commit `3aaade1`「削除済みシンボルを指すコメントを Core の現行 API へ読み替える」。

**Step 1: 整形**

```powershell
dotnet csharpier format .
```

差分が出たら commit する(pre-commit フックが staged ファイルを整形するが、先に揃えておく)。

**Step 2: 全層テスト**

```powershell
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests   -c Release --no-build
dotnet test tests/yEdit.Editor.Tests -c Release --no-build
dotnet test tests/yEdit.App.Tests    -c Release --no-build
```

Expected: 全て **PASS**・**0 warning**。

**Step 3: 既存テストが無変更であることを機械的に確認**

```powershell
git diff main --stat -- tests/yEdit.Core.Tests/Search/SnapshotSearcherRegexAnchorTests.cs tests/yEdit.Core.Tests/Search/TextSearcherTests.cs
```

Expected: **出力なし**(この 2 本は 1 行も変わっていないこと)。

`SnapshotSearcherTests.cs` と `SearchControllerTests.cs` は**追加のみ**であること:

```powershell
git diff main --numstat -- tests/yEdit.Core.Tests/Search/SnapshotSearcherTests.cs tests/yEdit.App.Tests/SearchControllerTests.cs
```

Expected: 削除列が **0**。1 行でも削除があれば既存テストを壊しているので戻すこと。

**Step 4: Commit**

```powershell
git add -A
git commit -m "chore: csharpier 整形"
```

---

## Task 9: 最終ブランチレビュー(2 パス)

CLAUDE.md §3 工程 5 / §4。**パスごとに独立した別エージェントを起動する**(1 起動に混載しない)。

**パス 1 — コード品質**

観点:
- 12 メソッドの移動が本当に「移動」か(本体に意図しない変更が混じっていないか)。
  `git diff` で移動前後を突き合わせること
- `StrategyFor` の選択規則が現行と一致しているか(特に `<=` / `>` の向き)
- `ReplaceInRange` の引数正規化の等価性(Task 4 Step 4 の論証が正しいか)
- 契約ドキュメントが散逸していないか(設計 §6)

ミューテーション検証のスポットチェック(実装行を一時変異させ、対象テストが赤になることを確認して復元):

| # | 変異 | 期待して赤になるテスト |
|---|---|---|
| 1 | `StrategyFor` の `<=` → `<` | `AtExactThreshold_uses_below_path_not_above` + `StrategyFor_selects_expected_strategy` |
| 2 | `StrategyFor` の `<=` → `<=` の右辺 +1 | `OneCharAboveThreshold_uses_above_path` + `StrategyFor_selects_expected_strategy` |
| 3 | `TextOf` の `ReferenceEquals` を常に true に | `DifferentSnapshot_rematerializes` / `EditedBuffer_yields_new_snapshot_and_fresh_results` |
| 4 | `StrategyFor` の `_opts.UseRegex` を反転 | `ReplacementAt_RegexAboveThreshold_expands_groups_per_line` + `StrategyFor_selects_expected_strategy` |
| 5 | `LiteralWindowSearchStrategy.FindPrev` 冒頭の `Math.Min` を削除 | 閾値超の `FindPrev` 系(削除して**緑のままなら網が無い**ので、その場で網を足すこと) |

**G-2 の確認を別途行うこと**: `SnapshotSearcher.FindPrev` が `before` を**クランプせずに**戦略へ
渡していること(クランプはファサードではなく `LiteralWindow` / `RegexPerLine` の各戦略が持つ)。
ここをファサードへ集約すると、文書長を超える `before` とゼロ幅ヒットの組み合わせで
閾値以下経路の挙動が変わる(Task 1 レビュー G-2)。`FindPrev_BeyondLength_...` のテストが網。

各変異のあと **必ず `dotnet build` してから** テストを流すこと(`--no-build` で変異前バイナリを叩く事故を避ける)。

**パス 2 — 脆弱性**

観点:
- 材質化文字列の保持がライフタイムを想定外に延ばしていないか(`SearchController` が
  `DropSearcher` を呼ぶ経路の網羅性)
- `Dismissed` の発火漏れ・二重発火
- 正規表現タイムアウト(`RegexMatchTimeoutException`)の伝播経路が戦略分離で変わっていないか

**指摘対応は 3 択で明示する**(① fixup commit / ② PR description に記載して受容 / ③ 理由付き却下)。修正は元 commit を書き換えず**別 fixup commit** で積む。

---

## Task 10: 品質ゲート → PR

**Step 1: ゲート**

```powershell
powershell -File tools\pre-merge-check.ps1
```

Expected: **EXIT 0**。

**Step 2: L5 実機 SR 検証をユーザーへ依頼**

設計 §9 の 8 項目。**必須**(SR 経路に触れる)。特に項目 3(ダイアログ非表示での F3 連打)と
項目 6(本文編集 → F3)がキャッシュの主経路。

**Step 3: PR 作成**

description に必ず含めること:

- 目的が**挙動不変の構造改善**であること。**性能数値は書かない**(実測をスキップした判断・設計 §0.1)
- レビュー経緯(2 パス + 指摘の 3 択処理)
- 申し送り S-1 / S-2 / S-3(設計 §8)
- 精密化 1(Dismissed へ差し替えた理由)と精密化 2(境界テストが無かった事実)
- **計画からの逸脱**(CLAUDE.md §2 は文書化を必須としている)— 下記「実施記録」を参照
- **受容した指摘**(下記「受容した指摘」節)
- 後続テーマ C → B → E(設計 §11)

---

## 受容した指摘(CLAUDE.md §4 の ②「PR description に記載して受容」)

修正せず受け入れると判断したもの。**PR description へ転記すること。**

| ID | Task | 内容 | 受容の理由 |
|---|---|---|---|
| A-1 | 7 | **同一タブ内のバッファ差し替えは破棄トリガに掛からない。** `FileController` の開き直し(`:209`)・保存失敗ロールバック(`:450`)・セッション/バックアップ復元(`:548 / 776 / 801`)・EOL 変換(`:389-395`)は、タブを切り替えずにバッファ参照ごと入れ替えるため `ActiveDocumentChanged` が発火しない。旧バッファがキャッシュにピン留めされ続ける | **キャッシュの正しさには影響しない**(参照同一性で必ず再材質化される)。純粋に保持量の問題で、影響は「次に検索するまで」に限られる。`FileController` から検索キャッシュへ手を伸ばす結合を新設するほうが害が大きい。必要になれば `doc.ClearCsvCache()`(`FileController.cs:203`)と同じ場所に 1 行足せる |
| A-2 | 7 | **破棄トリガ (iii) `Dismissed` は支配的フローでは一度も発火しない。** G-2 により検索モードでは「次を検索」成功時・Enter 成功時にダイアログが自分を Hide するため、`Ctrl+F → 入力 → Enter → 以後 F3` という最も普通の流れでユーザーが閉じる操作をする機会が無い | 仕様どおり。(iii) は「あれば効く」保険であり、常用フローの実効トリガは (i) 照合条件の変化 と (ii) 文書切替/クローズ。だからこそ (ii) の穴(I-1)を塞ぐことが重要 |
| A-3 | 5 | **`_materialized` フィールドを interface 型へ一般化しない。** 「畳んだついでに 3 フィールドを `ISnapshotSearchStrategy[]` へ」といった整理をしない | Task 7 で解放(`Reset()`)が必要になったとき、具象型のまま保持していれば interface を汚さずに呼べる。一般化すると退路が消える。なお「1 つだけが状態を持つ」非対称を型に出さない判断自体は正しい(呼び出し側から観測不能な差であり、型に出す唯一の形 `Reset()` / `IDisposable` は 2 実装に空実装を強いる) |

## 実施記録(計画からの逸脱)

CLAUDE.md §2「意図的な挙動変更・計画からの逸脱は、設計書または PR description に必ず文書化する」に従い、
実装中に生じた計画との差をここへ追記していく。**PR description へ転記すること。**

| # | Task | 逸脱 | 理由 |
|---|---|---|---|
| D-1 | 2 | `SnapshotSearcher._windowSize` フィールドを削除し、ctor 引数を直接戦略へ渡す形にした(計画 Task 2 Step 3 はフィールド保持を指示していた) | フィールドにすると ctor でしか読まれなくなり、SonarAnalyzer **S1450**(`Remove the field and declare it as a local variable`)が `-warnaserror` でビルドを落とす。挙動は同一(`ArgumentOutOfRangeException.ThrowIfNegativeOrZero` の実行順も従来どおり戦略構築より前)。テスト側に reflection 参照が無いことは grep 確認済み |
| D-5 | 5 | B-1 の網に、指示された `FindPrev(snap, CharLength + 100)` に加えて **`int.MaxValue` のアサートを追加**した | **指示された値ではリテラル戦略の `Math.Min` を消しても差が出ない**(kill できない)。リテラル `FindPrev` で `before` が効くのは `end = Math.Min(before + overlap, CharLength)` と `absStart < before` の 2 箇所だけで、どちらも `CharLength` で頭打ちになるため。差が出るのは `before + overlap` が int を溢れて `end` が負になり `while (end > 0)` が回らなくなるときだけ。`CharLength + 100` のアサートは意味論の凍結として残し、実際に kill できる `int.MaxValue` を足した。**regex 側は `CharLength + 100` でも殺せる**(`before - 1` が `GetLineIndexOfChar` へ直接渡り範囲外例外)= 防御の質が非対称 |
| D-4 | 4 | `MaterializedSearchStrategy` の doc から、計画にあった「同じ idiom を `TextBuffer.Modified` が既に採用している」という記述を**精密化**した。あわせて `ReplaceInRange` 内のコメント位置を `if` の上へ移した | **計画の記述が不正確だった。** `TextBuffer.Modified`(`TextBuffer.cs:46`)は `ReferenceEquals(_current.Root, _savedRoot)` で**スナップショットではなくピース木のルート参照**を比べており、「同じ idiom」だと同一の参照を比べていると読める。帰結も併記した=**Undo で同じルートへ戻ると新しい `TextSnapshot` インスタンスになるため、キャッシュは無駄に作り直すが古い本文を返すことはない(誤りは安全な側にしか倒れない)**。コメント位置は、このリポジトリが単文 `if` に brace を付けない様式のため |
| D-3 | 3 | `SnapshotSearcher.cs` から `using System.Text;` を削除した(**「本体は一切変えない」の許容範囲を超える任意変更**) | `StringBuilder` の唯一の利用者 `ReplaceInRangeRegexPerLine` が転出して未使用になったための衛生的削除。挙動影響ゼロ。**⚠ 当初「SonarAnalyzer S1128 が `-warnaserror` で落とすので必然」と記録したが、これは事実誤認だった**(Task 3 レビューで判明)。`using` を戻して Debug / Release / ソリューション全体をビルドしても **0 警告 0 エラー**。アナライザ自体は生きており(未読 private フィールドの探針で `error S4487` が出る)、**S1128 だけが有効化されていない**。原因は `Directory.Build.props:8` の `<EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>` と S1128 非有効の組み合わせ。**このリポジトリでは未使用 using はビルドを落とさない** |
| D-2 | 2 | 計画の `ISnapshotSearchStrategy` 案にあった「位置引数は呼び出し前に `SnapshotSearcher` が snap の範囲へクランプ済み」という契約 bullet を**採用しなかった** | **この記述は Task 5 で偽になる。** G-2(`FindPrev` のクランプはファサードへ集約できない)の発覚後、計画の Task 2/3/5 は修正したが、インターフェース案の bullet を直し忘れていた=**計画側のバグ**。実装者が正しく落とした。代わりに引数ごとの保証を表で書く(Task 2 レビュー I-1・弱い保証で書くことで Task 5 後も書き直し不要になる) |

---

## 完了の定義

- [ ] 既存テスト 4 本のうち 2 本は完全無変更、2 本は追加のみ(Task 8 Step 3 で機械確認)
- [ ] `dotnet build -warnaserror` が 0 warning
- [ ] 全 3 層のテストが緑
- [ ] ミューテーション 5 件が全て kill された(#5 は「網が無ければその場で足す」)
- [ ] `FindPrev` のクランプがファサードではなく各戦略側にある(G-2)
- [ ] `pre-merge-check.ps1` が EXIT 0
- [ ] L5 実機 SR 検証 8 項目が PASS
- [ ] PR description に申し送りと「性能数値を書かない」判断が記載されている
