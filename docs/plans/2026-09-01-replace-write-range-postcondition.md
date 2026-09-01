# 置換の書込範囲を事後条件で保証する 実装計画(B2)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 置換が「実際に内容を変える範囲」を当人へ問える seam を作り、選択範囲外への書込を
書く前に止める。併せて一括置換のゼロ幅挿入を単発置換に揃え、スコープ内での再照合漏れ(M-29)を直す。

**Architecture:** `EditorControl` が `ReplaceCharRangeExact` と範囲計算を共有する照会 API
`GetExactChangeRange` を公開し、`SearchController` が書く直前にスコープ包含を検査する。
`TextSearcher.ReplaceInRange` は走査を再アンカー化し、ゼロ幅マッチの挿入点を論理文字境界へ
後退させる。境界規則の所有者は `TextBoundary` のままで、`string` を扱う Core.Search のために
span 版を 1 本足す。

**Tech Stack:** C# / .NET (WinForms), xUnit, CSharpier, Husky.Net

**設計書:** `docs/plans/2026-09-01-replace-write-range-postcondition-design.md`
**ブランチ:** `feature/replace-write-range-postcondition`(main `06721e5` から分岐)

---

## この計画の読み方(実装者への前提)

**計画に書いたコードと期待値は「検証すべき仮説」であって正解ではない。**
このリポジトリでは計画のコードが実測で反証された事例が繰り返し出ている(PR #56 §9.1 / §9.4)。
本計画は次の 2 点をタスク内に**必須ステップ**として埋め込んである。必ず実行すること。

1. **挙動不変を主張するテストは、変更前の src で先に走らせて green を確認する。**
   期待値は計画の値ではなく**変更前の実測値**を正とする。
2. **バグ修正のテストは、変更前の src で先に走らせて red(=再現)を確認する。**
   red にならないなら fixture がバグへ到達していない。修正してから実装に入る。

期待値が計画と食い違ったら、**実測を正として計画のほうを疑うこと**。食い違いは
設計書 §9(実施記録)へ書き残す。

### 用語

| 語 | 意味 |
|----|------|
| スコープ | 「選択範囲のみ」ON のときに捕捉された置換対象範囲(`SearchController._selectionScope`) |
| 論理文字 | サロゲートペアと CRLF を 1 単位として扱う単位。`TextBoundary` が規則の所有者 |
| 巻き込み復元 | 要求範囲の端が論理文字の内側にあるとき、外側へ広げて削った半身を書き戻すこと |
| ゼロ幅マッチ | `\b` / `(?<=x)` / `a*` など長さ 0 のヒット。置換は純挿入になる |

### 共通コマンド

```powershell
# ビルド(警告=エラー)
dotnet build kxEdit.sln -c Release -warnaserror

# 個別テスト
dotnet test tests/kxEdit.Core.Tests   -c Release --filter "FullyQualifiedName~TextBoundaryTests"
dotnet test tests/kxEdit.Editor.Tests -c Release --filter "FullyQualifiedName~EditorControlReplaceExactTests"
dotnet test tests/kxEdit.App.Tests    -c Release --filter "FullyQualifiedName~SearchControllerTests"

# 品質ゲート(Task 6 で 1 回)
pwsh tools/pre-merge-check.ps1
```

`--no-build` は付けないこと(直前のビルド構成と食い違うと古い DLL を叩く)。
pre-commit フックが CSharpier 整形を掛けるので、整形は手で直さなくてよい。

---

## Task 1: `TextBoundary` に span 版 `SnapToLogicalCharStart` を足す

Core.Search は `string` を扱い `TextSnapshot` を持たないため、Task 2 で使う境界規則を
先に用意する。**後続タスクが依存する共通規則の追加**なので、完了時に
CLAUDE.md §3-4 の**前倒しコード品質レビュー**を行う。

**Files:**
- Modify: `src/kxEdit.Core/Text/TextBoundary.cs`
- Test: `tests/kxEdit.Core.Tests/Text/TextBoundaryTests.cs`

### Step 1: 失敗するテストを書く(狙い撃ち)

`tests/kxEdit.Core.Tests/Text/TextBoundaryTests.cs` の末尾に追加する。
既存の span 版テスト(`SnapToCodePointStart_Span`)の並びに置くこと。

```csharp
    // ===== span 版 SnapToLogicalCharStart(2026-09-01 B2 Task 1) =====
    // snapshot 版と違い CRLF も見る。Core.Search が材質化した本文 string を扱うため。

    [Fact]
    public void SnapToLogicalCharStart_Span_SnapsMidSurrogateAndMidCrlf()
    {
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart("a\U0001F600b".AsSpan(), 2));
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart("a\r\nb".AsSpan(), 2));
    }

    [Fact]
    public void SnapToLogicalCharStart_Span_LeavesBoundariesAlone()
    {
        var text = "a\r\nb".AsSpan();
        Assert.Equal(0, TextBoundary.SnapToLogicalCharStart(text, 0));
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart(text, 1));
        Assert.Equal(3, TextBoundary.SnapToLogicalCharStart(text, 3));
        Assert.Equal(4, TextBoundary.SnapToLogicalCharStart(text, 4)); // 末尾は許可
    }

    [Fact]
    public void SnapToLogicalCharStart_Span_LoneLfAndLoneLowSurrogateAreNotSnapped()
    {
        // 対を成さない片割れは論理文字を作らないので動かさない(snapshot 版と同じ規則)。
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart("a\nb".AsSpan(), 1));
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart("a\uDE00b".AsSpan(), 1));
    }

    [Fact]
    public void SnapToLogicalCharStart_Span_ClampsOutOfRange()
    {
        var text = "abc".AsSpan();
        Assert.Equal(0, TextBoundary.SnapToLogicalCharStart(text, -5));
        Assert.Equal(3, TextBoundary.SnapToLogicalCharStart(text, 99));
    }
```

`SnapToLogicalCharStart_Span_LoneLfAndLoneLowSurrogateAreNotSnapped` は
**非既定位置から始めている**(CLAUDE.md §4-B: no-change のテストは既定値と区別する)。
`pos = 1` は「動かさない」と「0 へクランプ」が区別できる唯一の位置。

### Step 2: 失敗を確認する

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
```

Expected: **ビルド失敗**。`CS1503` または `CS1929`(`ReadOnlySpan<char>` を受ける
オーバーロードが無い)。この時点でテストは走らない。

### Step 3: span 版を実装する

`src/kxEdit.Core/Text/TextBoundary.cs` の `SnapToLogicalCharStart(TextSnapshot, int)` の
**直後**に追加する(対になるメソッドを離さない)。

```csharp
    /// <summary>
    /// span 版。<c>TextSnapshot</c> を持たない呼び出し側(<c>Core.Search</c> が材質化した
    /// 本文 string 等)向け。論理文字の中間位置(low サロゲート位置 / CR と LF の間)を
    /// 前方(pair 先頭)へスナップする。[0, text.Length] の外はクランプ。
    /// </summary>
    /// <remarks>
    /// <see cref="SnapToCodePointStart"/>(サロゲートのみ)と違い <b>CRLF も 1 論理文字として見る</b>。
    /// 判定を述語へ括らずインラインで書いているのは他の span 版 2 メソッドと同じ理由
    /// (indexer 読みなので snapshot 版の述語と共有できない)。
    /// snapshot 版との<b>同値は全数テストで固定してある</b>
    /// (<c>SnapToLogicalCharStart_Span_MatchesSnapshotVersion_Exhaustive</c>)ので、
    /// 片方だけ直すと必ず赤くなる。
    /// </remarks>
    public static int SnapToLogicalCharStart(ReadOnlySpan<char> text, int pos)
    {
        if (pos <= 0)
            return 0;
        if (pos >= text.Length)
            return text.Length;
        char c = text[pos];
        bool endsSurrogatePair = char.IsLowSurrogate(c) && char.IsHighSurrogate(text[pos - 1]);
        bool endsCrlf = c == '\n' && text[pos - 1] == '\r';
        return endsSurrogatePair || endsCrlf ? pos - 1 : pos;
    }
```

### Step 4: class doc の陳腐化を直す

**この Step を飛ばさないこと。** 現在の class doc は span 版の適用範囲を
「行内テキスト(改行を含まない=CRLF 概念が不要)」と宣言しており、Step 3 でこの文が偽になる。
doc を直さずに API だけ足すと、次に読む人が誤った不変条件を信じる。

`src/kxEdit.Core/Text/TextBoundary.cs` の class doc、3 箇所を書き換える。

**(a) 「span 版 2 メソッド」→ 3 メソッド**(`:12` 付近)

```
/// <b>本ファイルの境界述語 4 つと span 版 2 メソッドだけ</b>で済む状態を保つこと。
```
↓
```
/// <b>本ファイルの境界述語 4 つと span 版 3 メソッドだけ</b>で済む状態を保つこと。
```

**(b) 述語共有の例外の説明**(`:21` 付近)

```
/// span 版 2 メソッドが例外なのは、<c>TextSnapshot</c> ではなく indexer で読むため述語を
/// 共有できないから(共有すると短絡が効かず読みが増える)。
```
↓
```
/// span 版 3 メソッドが例外なのは、<c>TextSnapshot</c> ではなく indexer で読むため述語を
/// 共有できないから(共有すると短絡が効かず読みが増える)。規則が 2 実装に分かれる分は、
/// snapshot 版との<b>同値を全数テストで固定して</b>drift を防いでいる。
```

**(c) span 版の適用範囲**(`:66` 付近)

```
/// <c>TextSnapshot</c> を受ける版は文書全体を、<c>ReadOnlySpan&lt;char&gt;</c> を受ける版は
/// Layout / 描画が扱う行内テキスト(改行を含まない=CRLF 概念が不要)を対象とする。
```
↓
```
/// <c>TextSnapshot</c> を受ける版は文書全体を、<c>ReadOnlySpan&lt;char&gt;</c> を受ける版は
/// <c>TextSnapshot</c> を持たない呼び出し側(Layout / 描画の行内テキスト・
/// <c>Core.Search</c> が材質化した本文 string)を対象とする。
/// <b>CRLF を見るかどうかは span / snapshot の別ではなく「コードポイント単位か論理文字単位か」で
/// 決まる</b>: <see cref="SnapToCodePointStart"/> はサロゲートだけを見るが、
/// <see cref="SnapToLogicalCharStart(ReadOnlySpan{char}, int)"/> は CRLF も 1 論理文字として見る
/// (2026-09-01 B2: 行内テキスト専用という旧宣言はここで失効した)。
```

### Step 5: 狙い撃ちテストが通ることを確認する

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --filter "FullyQualifiedName~TextBoundaryTests"
```

Expected: PASS(既存テストも含めて全件 green)。

### Step 6: snapshot 版との同値を全数で固定する

規則が 2 実装に分かれたことに対する唯一の網。`TextBoundaryTests.cs` に追加する。

```csharp
    /// <summary>
    /// 全数の材料: 論理文字境界に関わる 5 文字だけで長さ 4 以下の全文字列(781 本)を作る。
    /// 通常文字 / CR / LF / high サロゲート / low サロゲートで、
    /// 「対を成す・成さない」の全組合せが 4 文字以内に現れる。
    /// </summary>
    private static IEnumerable<string> ShortStringsOverBoundaryAlphabet()
    {
        // 絵文字は UTF-16 で 2 code unit なので、この文字列の Length は 5
        // (a / CR / LF / high サロゲート / low サロゲート)。
        const string alphabet = "a\r\n\U0001F600";
        var cur = new List<string> { "" };
        yield return "";
        for (int len = 1; len <= 4; len++)
        {
            var next = new List<string>(cur.Count * alphabet.Length);
            foreach (string s in cur)
                foreach (char c in alphabet)
                    next.Add(s + c);
            foreach (string s in next)
                yield return s;
            cur = next;
        }
    }

    [Fact]
    public void SnapToLogicalCharStart_Span_MatchesSnapshotVersion_Exhaustive()
    {
        foreach (string raw in ShortStringsOverBoundaryAlphabet())
        {
            var snap = Snap(raw);
            // 比較は「保存層を通った後の本文」で行う。TextBuffer は UTF-8 で保持するため
            // 孤立サロゲートは U+FFFD へ潰れ、raw と snapshot の中身は一致しない。
            string text = snap.GetText(0, snap.CharLength);
            for (int pos = -2; pos <= text.Length + 2; pos++)
                Assert.Equal(
                    TextBoundary.SnapToLogicalCharStart(snap, pos),
                    TextBoundary.SnapToLogicalCharStart(text.AsSpan(), pos)
                );
        }
    }

    [Fact]
    public void SnapToLogicalCharStart_Span_Exhaustive_NeverMovesForwardAndStaysInRange()
    {
        // snapshot を通さない=孤立サロゲートを含む入力も直接当てる(同値テストが届かない領域)。
        foreach (string text in ShortStringsOverBoundaryAlphabet())
        {
            for (int pos = -2; pos <= text.Length + 2; pos++)
            {
                int got = TextBoundary.SnapToLogicalCharStart(text.AsSpan(), pos);
                Assert.InRange(got, 0, text.Length);
                Assert.True(got <= Math.Max(0, Math.Min(pos, text.Length)), $"forward move: '{Escape(text)}' pos={pos} got={got}");
                Assert.True(Math.Max(0, Math.Min(pos, text.Length)) - got <= 1, $"moved more than 1: '{Escape(text)}' pos={pos} got={got}");
                // 結果は論理文字の内側を指さない。
                if (got > 0 && got < text.Length)
                {
                    Assert.False(char.IsLowSurrogate(text[got]) && char.IsHighSurrogate(text[got - 1]));
                    Assert.False(text[got] == '\n' && text[got - 1] == '\r');
                }
            }
        }
    }

    /// <summary>失敗メッセージ用。制御文字とサロゲートを可視化する。</summary>
    private static string Escape(string s) =>
        string.Concat(
            s.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $@"\u{(int)c:X4}")
        );
```

`using System.Collections.Generic;` / `using System.Linq;` が要るなら足す
(implicit usings の有無はビルドエラーで判る)。

### Step 7: 全数テストを走らせる

```powershell
dotnet test tests/kxEdit.Core.Tests -c Release --filter "FullyQualifiedName~TextBoundaryTests"
```

Expected: PASS。**落ちたら実装ではなく期待値を疑うこと**
(「1 つしか戻らない」「末尾は動かさない」は snapshot 版の実測契約から写した仮説)。

### Step 8: ミューテーション検証(スポットチェック)

CLAUDE.md §4-A の有効域(境界算出の中核ロジック)。**src を書き換えて赤くなることを確認し、
必ず元へ戻す**。

| # | 変異 | 期待 |
|---|------|------|
| 1 | `return ... ? pos - 1 : pos;` → `? pos : pos;` | 赤(狙い撃ち + 同値) |
| 2 | `endsSurrogatePair \|\| endsCrlf` → `endsSurrogatePair` | 赤(CRLF ケース) |
| 3 | `endsSurrogatePair \|\| endsCrlf` → `endsCrlf` | 赤(サロゲートケース) |
| 4 | `if (pos >= text.Length) return text.Length;` を削除 | 赤(クランプ) |

**OR ガードは条件ごとに 1 行ずつ変異させる**(#2 / #3。まとめて消すと片方の網の欠落を見逃す
= [[backup-savepoint-sync]] の教訓)。

ビルド失敗の判定は `grep -E " error [A-Z]+[0-9]+"` を使う
(`grep "error CS"` は Sonar の `error S###` を見落として古い DLL を叩く
= [[mutation-harness-exit-code-trap]])。

### Step 9: commit

```powershell
git add src/kxEdit.Core/Text/TextBoundary.cs tests/kxEdit.Core.Tests/Text/TextBoundaryTests.cs
git commit -m "feat(core): TextBoundary に span 版 SnapToLogicalCharStart を足す

Core.Search が材質化した本文 string で論理文字境界を求められるようにする
(B2 Task 2 の前提)。CRLF も 1 論理文字として見る点が SnapToCodePointStart と違う。

span 版は述語を共有できないため規則が 2 実装に分かれる。snapshot 版との同値を
長さ 4 以下の全文字列 x 全位置で固定し、片方だけ直すと赤くなるようにした。
class doc の「span 版は行内テキスト=CRLF 概念が不要」の宣言は失効したので書き換えた。"
```

### Step 10: 前倒しコード品質レビュー(CLAUDE.md §3-4)

**別エージェント**を起動し、次を渡してレビューさせる。

- 差分: `git diff main..HEAD -- src/kxEdit.Core/Text/TextBoundary.cs tests/kxEdit.Core.Tests/Text/TextBoundaryTests.cs`
- 設計書 §2.5
- 観点: (1) 規則の二重実装に対する網が本当に drift を捕まえるか / (2) class doc の
  書き換えに漏れがないか(「span 版 2 メソッド」等の陳腐化した数え上げが他に無いか) /
  (3) 全数テストの主張が実装のトートロジーになっていないか

指摘は CLAUDE.md §4 の 3 択(fixup / 受容を記載 / 理由付き却下)で処理し、
**修正は別 fixup commit で積む**。

---

## Task 2: `TextSearcher.ReplaceInRange` の再アンカー化とゼロ幅後退

M-29(スコープ内で再照合しない)と、一括置換のゼロ幅・サロゲート割りを同時に直す。
**外部入力(正規表現)のパース結果で書込範囲が決まる中核**なので、完了時に
CLAUDE.md §3-4 の**前倒し脆弱性レビュー**を行う(傘設計書 §8 が B2 を V-7 の教訓の
直接該当と指定している)。

**Files:**
- Modify: `src/kxEdit.Core/Search/TextSearcher.cs:145-175`
- Test: `tests/kxEdit.Core.Tests/Search/TextSearcherTests.cs`

### Step 1: 挙動不変の対照テストを**変更前の src で**書いて走らせる

**このステップを飛ばすと「全文置換は挙動不変」の主張が根拠を失う。**
新規テストを変更前の src で走らせるのが挙動不変の最強証明
(= [[large-line-wrap-perf-branch]] の手法)。

`tests/kxEdit.Core.Tests/Search/TextSearcherTests.cs` の `ReplaceInRange` 節に追加する。

```csharp
    [Theory]
    // 全文置換(s == 0)は Matches ベースの旧実装と 1 文字も変わらないこと。
    // 期待値は**変更前の src での実測値**(計画の値ではない)。
    [InlineData("ab_ab_ab", "ab", false, "X", "X_X_X", 3)]
    [InlineData("aaaa", "aa", true, "X", "XX", 2)] // 非重複・左端優先
    [InlineData("abc", "x*", true, "-", "-a-b-c-", 4)] // ゼロ幅は各位置で 1 件
    [InlineData("ab", "b*", true, "-", "-a-b-", 3)] // 空マッチと実マッチの混在
    [InlineData("a\r\nb", "\\r\\n", true, "X", "aXb", 1)] // CRLF 丸ごと
    [InlineData("aaa", "a", false, "", "", 3)] // 空置換
    public void ReplaceInRange_WholeText_KeepsMatchesSemantics(
        string text,
        string pattern,
        bool useRegex,
        string repl,
        string expected,
        int expectedCount
    )
    {
        var (fragment, count) = Make(pattern, useRegex: useRegex)
            .ReplaceInRange(text, 0, text.Length, repl);
        Assert.Equal(expected, fragment);
        Assert.Equal(expectedCount, count);
    }
```

```powershell
dotnet test tests/kxEdit.Core.Tests -c Release --filter "FullyQualifiedName~ReplaceInRange_WholeText_KeepsMatchesSemantics"
```

Expected: **PASS**(変更前の src で)。

**落ちた行があれば、計画の期待値が間違っている。** 実測値へ**書き換えてから**先へ進むこと。
書き換えたら「計画の期待値が実測で反証された」旨を設計書 §9 へ記録する。

commit する(この時点で src は無変更):

```powershell
git add tests/kxEdit.Core.Tests/Search/TextSearcherTests.cs
git commit -m "test(core): ReplaceInRange の全文置換の意味論を変更前に固定する

B2 Task 2 で走査を Matches から Match(text, scan) の再アンカーへ変える。
全文置換(s == 0)が挙動不変であることの対照群を、変更前の src で green に
しておく(変更後も green なら挙動不変が実測で示せる)。"
```

### Step 2: 新しい挙動のテストを書く(red を確認する)

同じファイルに追加する。**M-29 節**と**ゼロ幅後退節**に分ける。

```csharp
    // ----- M-29: スコープ内での再照合(2026-09-01 B2 Task 2) -----

    [Fact]
    public void ReplaceInRange_ReMatchesInsideRange_WhenWholeTextHitEatsRangeStart()
    {
        // M-29: 文書全体の照合では [0,2) の "aa" しか出ず m.Index < 1 で捨てられ 0 件だった。
        // 範囲始端へ再アンカーすれば [1,3) の "aa" が見つかる。
        // 単発置換(FindNext)は Match(text, from) なので元から当たっており、両者が食い違っていた。
        var (fragment, count) = Make("aa").ReplaceInRange("aaa", 1, 2, "X");
        Assert.Equal("X", fragment);
        Assert.Equal(1, count);
    }

    [Fact]
    public void ReplaceInRange_ReAnchorDoesNotCutInputContext()
    {
        // Match(text, startat) は入力を切らないので \b は全文文脈で評価される
        // = 位置 1 は語中なので \b は成立しない。
        // 範囲を substring("aa") へ切って照合する実装なら 1 件になる
        // = この fixture が「再アンカー」と「substring 化」を弁別する唯一の形。
        var (fragment, count) = Make(@"\baa", useRegex: true).ReplaceInRange("aaa", 1, 2, "X");
        Assert.Equal("aa", fragment);
        Assert.Equal(0, count);
    }

    // ----- ゼロ幅マッチの挿入点を論理文字境界へ後退させる(2026-09-01 B2 Task 2) -----

    [Fact]
    public void ReplaceInRange_ZeroWidthInsideCrlf_RetreatsToBoundary()
    {
        // 修正前は "a\rX\nb"=CRLF が 2 個の改行へ分裂した。
        // 単発置換(ReplaceCharRangeExact)は挿入点を 1 へ後退させるので、そちらへ揃える。
        var (fragment, count) = Make(@"(?<=\r)", useRegex: true).ReplaceInRange("a\r\nb", 0, 4, "X");
        Assert.Equal("aX\r\nb", fragment);
        Assert.Equal(1, count);
    }

    [Fact]
    public void ReplaceInRange_ZeroWidthInsideSurrogatePair_RetreatsToBoundary()
    {
        // 修正前はペアの内側へ挿入し、書き戻し時に孤立サロゲート 2 個が U+FFFD へ潰れた
        // = 無警告のデータ破壊。"a😀b" = a(0) high(1) low(2) b(3)。
        var (fragment, count) = Make(@"(?<=\ud83d)", useRegex: true)
            .ReplaceInRange("a\U0001F600b", 0, 4, "X");
        Assert.Equal("aX\U0001F600b", fragment);
        Assert.Equal(1, count);
        Assert.DoesNotContain('�', fragment);
    }

    [Fact]
    public void ReplaceInRange_ZeroWidthRetreatingBeforeRangeStart_IsSkippedAndNotCounted()
    {
        // 範囲 [2,4) の始端 2 は CRLF の内側。(?<=\r) はそこにヒットするが挿入点は 1 へ
        // 後退する=範囲外。断片は範囲の中身だけを返す契約なので書かずにスキップし、
        // 件数にも数えない("N 件置換しました" を嘘にしない)。
        var (fragment, count) = Make(@"(?<=\r)", useRegex: true).ReplaceInRange("a\r\nb", 2, 2, "X");
        Assert.Equal("\nb", fragment);
        Assert.Equal(0, count);
    }

    [Fact]
    public void ReplaceInRange_ZeroWidthRetreatingBeforeEmittedPosition_IsSkipped()
    {
        // "\r" を消費した直後、同じ位置(2)にゼロ幅が立つ。後退先 1 は既に出力済みなので
        // 書けない=スキップ。後退の判定は**元テキスト**で行う(出力側には既に CRLF が無い)。
        var (fragment, count) = Make(@"\r|(?<=\r)", useRegex: true).ReplaceInRange("a\r\nb", 0, 4, "X");
        Assert.Equal("aX\nb", fragment);
        Assert.Equal(1, count);
    }
```

```powershell
dotnet test tests/kxEdit.Core.Tests -c Release --filter "FullyQualifiedName~TextSearcherTests"
```

Expected: 上記 6 件が **FAIL**、既存テストは PASS。

**6 件すべてが赤になることを確認すること。** 赤にならない行があれば fixture がその欠陥へ
到達していない=網として無効なので、fixture を直してから先へ進む。
`(?<=\ud83d)` が期待どおり孤立 high サロゲートに照合するかは仮説なので、
赤の理由が「照合しない」なら fixture 側の問題。`Assert.Equal(1, Make(@"(?<=\ud83d)", useRegex: true).Count("a\U0001F600b"))`
のような一時アサートで先に確かめてよい。

### Step 3: 実装する

`src/kxEdit.Core/Search/TextSearcher.cs` の先頭に `using kxEdit.Core.Text;` を足し、
`ReplaceInRange` を丸ごと差し替える。**xmldoc も併せて更新する。**

```csharp
    /// <summary>
    /// [start, start+length) に完全に収まるヒットだけ置換し、その範囲の置換後断片と件数を返す。
    /// 範囲外・境界をまたぐヒットは対象外。start/length は text 範囲へクランプする。
    /// エディタはこの断片で当該文字範囲を差し替える。
    /// <para>
    /// <b>照合は範囲始端へ再アンカーする</b>(<c>Match(text, scan)</c>)。全文照合の結果を
    /// 捨てるだけだと、範囲直前から始まるヒットが範囲内の文字を食って範囲内のヒットが
    /// 消える(監査 M-29: <c>"aaa"</c> の <c>[1,3)</c> を <c>aa</c> で 0 件)。
    /// <c>startat</c> は<b>入力を切らない</b>ので <c>\b</c>・先読み・後読みは全文文脈のまま。
    /// </para>
    /// <para>
    /// <b>ゼロ幅マッチの挿入点は論理文字の境界まで後退させる</b>
    /// (<c>EditorControl.ReplaceCharRangeExact</c> と同じ規則。規則の所有者は
    /// <see cref="Text.TextBoundary"/>)。後退させないと CRLF やサロゲートペアの内側へ挿入して
    /// しまい、書き戻し時に孤立サロゲートが U+FFFD へ潰れる。後退先が範囲始端より前
    /// / 既に出力した位置より前になるマッチは<b>スキップし件数にも数えない</b>。
    /// 後退の判定は<b>元テキスト</b>に対して行う(単発置換も編集前スナップショットで判定する)。
    /// </para>
    /// 複雑な正規表現では RegexMatchTimeoutException が送出され得る（1秒）。
    /// </summary>
    public (string Fragment, int Count) ReplaceInRange(
        string text,
        int start,
        int length,
        string replacement
    )
    {
        int s = Math.Clamp(start, 0, text.Length);
        int end = Math.Clamp(start + length, s, text.Length);
        if (_regex is null)
            return (text.Substring(s, end - s), 0);

        var sb = new StringBuilder();
        int count = 0,
            pos = s,
            scan = s;
        while (scan <= end)
        {
            var m = _regex.Match(text, scan);
            if (!m.Success || m.Index + m.Length > end)
                break;
            int at =
                m.Length == 0 ? TextBoundary.SnapToLogicalCharStart(text, m.Index) : m.Index;
            if (at < pos)
            {
                // 範囲始端より前 / 既に出力した位置より前へは書けない。
                scan = m.Index + 1; // ゼロ幅なので必ず 1 進める(同位置の無限ループ回避)
                continue;
            }
            sb.Append(text, pos, at - pos);
            sb.Append(Expand(m, replacement));
            pos = at + m.Length;
            count++;
            scan = m.Index + Math.Max(1, m.Length);
        }
        sb.Append(text, pos, end - pos);
        return (sb.ToString(), count);
    }
```

**`at < pos` の 1 本が 2 つの不正を弾く**ことを理解して書くこと。`pos` は `s` で初期化されるので
最初は「範囲外」を、走査が進んだ後は「出力済み位置より前」を弾く。

### Step 4: テストを走らせる

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --filter "FullyQualifiedName~TextSearcherTests"
```

Expected: 全件 PASS。**Step 1 の対照群(`ReplaceInRange_WholeText_KeepsMatchesSemantics`)が
green のままであること**=全文置換の挙動不変が実測で示せた。

### Step 5: 委譲経路の回帰を確認する

```powershell
dotnet test tests/kxEdit.Core.Tests -c Release --filter "FullyQualifiedName~SnapshotSearcher|FullyQualifiedName~MaterializedSearchStrategy"
```

Expected: PASS。落ちたら**閾値超経路(`RegexPerLineSearchStrategy`)が行内 substring を
渡す設計と衝突している**可能性があるので、落ちたテストの主張を読んでから判断する
(行単位でしか M-29 が直らないのは設計書 §2.4 が認めた制約)。

`RegexPerLineSearchStrategy` が M-29 修正を**行内でだけ**引き継ぐことを 1 件足す
(既存の `MakeLarge(threshold: 4, ...)` の流儀に倣う)。閾値超 + 正規表現の経路。

```csharp
    [Fact]
    public void ReplaceInRange_above_threshold_regex_reanchors_within_the_line()
    {
        // 閾値超 + 正規表現 = RegexPerLineSearchStrategy。行内 substring へ委譲するので
        // M-29 の修正は行内でだけ効く(行頭より前から始まるヒットは依然拾えない)。
        var snap = TextBuffer.FromString("aaa\nbbb").Current;
        var (fragment, count) = MakeLarge("aa", useRegex: true).ReplaceInRange(snap, 1, 2, "X");
        Assert.Equal("X", fragment);
        Assert.Equal(1, count);
    }
```

期待値は仮説。**赤なら実測値を正として書き換え、なぜそうなるかを 1 行コメントで書く。**

### Step 6: ミューテーション検証(スポットチェック)

| # | 変異 | 期待 |
|---|------|------|
| 1 | `if (at < pos)` → `if (at < s)` | 赤(`_ZeroWidthRetreatingBeforeEmittedPosition_IsSkipped`) |
| 2 | `if (at < pos)` → 条件ごと削除 | 赤(`_ZeroWidthRetreatingBeforeRangeStart_...`) |
| 3 | `m.Length == 0 ? Snap... : m.Index` → `m.Index` | 赤(ゼロ幅後退 2 件) |
| 4 | `scan = m.Index + Math.Max(1, m.Length)` → `+ m.Length` | 赤 or ハング(ハングなら**タイムアウトも赤とみなす**) |
| 5 | `var m = _regex.Match(text, scan)` → `_regex.Match(text, s)` | 赤(全文置換の対照群) |
| 6 | `scan = m.Index + 1` → `scan = m.Index` | ハング(#4 と同じ扱い) |

#4 / #6 は無限ループになりうる。**テストプロセスを殺せるようにしてから走らせること**
(`dotnet test` に `--blame-hang-timeout 60s` を付ける)。

### Step 7: commit

```powershell
git add src/kxEdit.Core/Search/TextSearcher.cs tests/kxEdit.Core.Tests/Search/
git commit -m "fix(core): 範囲内置換を再アンカーし、ゼロ幅挿入を論理文字境界へ後退させる

M-29: 全文照合の結果を m.Index < s で捨てるだけだったため、範囲直前から始まる
ヒットが範囲内の文字を食い、範囲内のヒットが消えていた(\"aaa\" の [1,3) を
\"aa\" で 0 件)。単発置換(FindNext)は Match(text, from) で元から当たっており、
単発と一括が食い違っていた。走査を Match(text, scan) の再アンカーへ変えて揃える。
startat は入力を切らないので \\b・先読み・後読みは全文文脈のまま。

PR #56 §9.3 の申し送り: ゼロ幅マッチが論理文字の内側に立つとそこへ素直に挿入し、
サロゲートペアを割った場合は書き戻しで U+FFFD へ潰れていた(無警告のデータ破壊)。
CRLF も 2 個の改行へ分裂していた。単発置換と同じ規則で挿入点を境界へ後退させ、
後退先が範囲外 / 出力済み位置より前になるマッチはスキップして件数にも数えない。

全文置換(s == 0)の挙動不変は、対照テストを変更前の src で green にしてから
変更後も green であることで示した。"
```

### Step 8: 前倒し脆弱性レビュー(CLAUDE.md §3-4 / 傘設計書 §8)

**別エージェント**を起動する。観点:

- 正規表現(外部入力)で到達できる範囲外書込・無限ループ・OOM
- `scan` / `pos` / `at` の算術が範囲外・オーバーフローしないか
  (`start + length` の int 加算は既存のまま=呼び出し側が正規化している前提を確かめる)
- ゼロ幅の歩進が**必ず前進する**ことの証明(#4 / #6 の変異が実際にハングするなら、
  実装が前進を保証しているのは 1 箇所だけということ)
- 断片の契約「範囲の中身だけを返す」が全経路で保たれるか(スキップ経路を含む)
- `RegexMatchTimeoutException` の伝播が変わっていないか

---

## Task 3: `ExactRangeParts` の括り出しと `GetExactChangeRange`

**後続タスクが依存する新しい seam**なので、完了時に CLAUDE.md §3-4 の
**前倒しコード品質レビュー**を行う。

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.cs:1279-1315`
- Test: `tests/kxEdit.Editor.Tests/EditorControlReplaceExactTests.cs`

### Step 1: 失敗するテストを書く(3 形の弁別)

`tests/kxEdit.Editor.Tests/EditorControlReplaceExactTests.cs` の末尾に追加する。

```csharp
    // ===== GetExactChangeRange(2026-09-01 B2 Task 3) =====
    // 返すのは ReplaceCharRange へ渡す「広げた範囲」ではなく「内容が変わりうる範囲」。
    // 巻き込み復元は長さ保存なので CRLF の半身は無傷で戻る=内容は変わらない。
    // 孤立サロゲートになる半身だけが U+FFFD へ潰れる=そこだけ広げた範囲を返す。

    [Fact]
    public void GetExactChangeRange_CrlfSplit_DoesNotWiden() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r\ndef"));
            // LF だけの置換は書込が [3,5) へ広がるが、復元される CR は無傷。
            Assert.Equal((4, 5), ctrl.GetExactChangeRange(4, 1));
        });

    [Fact]
    public void GetExactChangeRange_SurrogateSplit_WidensToWholePair() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\U0001F600b")); // a(0) high(1) low(2) b(3)
            // high だけの置換は復元される low が U+FFFD へ潰れる=ペア全体が変わる。
            Assert.Equal((1, 3), ctrl.GetExactChangeRange(1, 1));
            // low だけの置換も同じ(始端側で潰れる)。
            Assert.Equal((1, 3), ctrl.GetExactChangeRange(2, 1));
        });

    [Fact]
    public void GetExactChangeRange_ZeroWidthInsideLogicalChar_RetreatsToBoundary() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r\ndef"));
            Assert.Equal((3, 3), ctrl.GetExactChangeRange(4, 0)); // CRLF の先頭へ後退
        });

    [Fact]
    public void GetExactChangeRange_ReadOnly_ReturnsEmptyRange() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r\ndef"));
            ctrl.ReadOnly = true;
            // no-op=何も変わらない。空範囲で表す(ReplaceCharRangeExact の no-op 契約と対)。
            Assert.Equal((4, 4), ctrl.GetExactChangeRange(4, 1));
        });
```

### Step 2: ビルド失敗を確認する

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
```

Expected: **ビルド失敗**(`CS1061`: `GetExactChangeRange` が無い)。

### Step 3: `ExactRangeParts` を括り出す(挙動不変)

`src/kxEdit.Editor/EditorControl.cs` の `ReplaceCharRangeExact` を差し替える。
**remarks は 1 文字も変えない**(既存の契約記述はそのまま残す)。

```csharp
    public int ReplaceCharRangeExact(int start, int length, string replacement)
    {
        if (IsComposing)
            CancelCompositionAndDefault();
        if (_buffer is null || ReadOnly)
            return Math.Clamp(start, 0, TextLength); // no-op=位置は動かない(returns 参照)
        ArgumentNullException.ThrowIfNull(replacement);
        var snap = _buffer.Current;
        var (s0, e0, s, e) = ExactRangeParts(snap, start, length);
        if (s0 == e0)
        {
            // 挿入点は境界へ後退しうるので、戻り値はスナップ後の位置から作る
            // (s0 から作ると論理文字 1 つ分ずれる=呼び出し側が start から導出するのと同じ誤り)。
            ReplaceCharRange(s, 0, replacement);
            return s + replacement.Length;
        }
        int prefixLen = s0 - s; // 復元する接頭辞。長さ保存で書き戻すので戻り値にも効く
        // 恒等ケース(s == s0 && e == e0)の分岐は置いていない。GetText(x, 0) は空を返し
        // (TextSnapshot.GetText の length == 0 早期 return。ただし範囲検査はその手前なので
        // 空が返るのは x ∈ [0, CharLength] のときだけ=s / e0 は常にこの範囲)、string 連結は空オペランドを
        // 短絡して残り 1 つの参照をそのまま返すため、分岐しても結果は同じ。
        string text = snap.GetText(s, prefixLen) + replacement + snap.GetText(e0, e - e0);
        ReplaceCharRange(s, e - s, text);
        return s + prefixLen + replacement.Length;
    }

    /// <summary>
    /// <see cref="ReplaceCharRangeExact"/> の範囲計算。要求範囲 <c>[S0,E0)</c>(クランプ済み)と、
    /// 巻き込み復元のために外側へ広げた範囲 <c>[S,E)</c> を返す。
    /// </summary>
    /// <remarks>
    /// <b>この計算をここ以外に書かないこと。</b> <see cref="ReplaceCharRangeExact"/> と
    /// <see cref="GetExactChangeRange"/> が同じ答えを出すことが後者の存在意義であり、
    /// 2 実装に分かれた瞬間に「問うた範囲と実際に書く範囲が違う」という最悪の形で腐る。
    /// <para>
    /// ゼロ幅(<c>S0 == E0</c>)は外側へ広げず、挿入点だけを境界へ後退させて
    /// <c>S == E == 後退後の位置</c> を返す(理由は
    /// <see cref="ReplaceCharRangeExact"/> の remarks「ゼロ幅は広げない」)。
    /// </para>
    /// </remarks>
    private static (int S0, int E0, int S, int E) ExactRangeParts(
        TextSnapshot snap,
        int start,
        int length
    )
    {
        int s0 = Math.Clamp(start, 0, snap.CharLength);
        // start + length は int 加算だとオーバーフローで負値になり得るため long 経由
        // (ReplaceCharRange / EnsureVisibleCharRange と同じ流儀)。
        long endLong = (long)start + Math.Max(0, length);
        int e0 = (int)Math.Clamp(endLong, s0, (long)snap.CharLength);
        if (s0 == e0)
        {
            int at = TextBoundary.SnapToLogicalCharStart(snap, s0);
            return (s0, e0, at, at);
        }
        return (
            s0,
            e0,
            TextBoundary.SnapToLogicalCharStart(snap, s0), // 外側へ(index が減る向き)
            TextBoundary.SnapToLogicalCharEnd(snap, e0) // 外側へ(index が増える向き)
        );
    }
```

### Step 4: 括り出しが挙動不変であることを確認する

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Editor.Tests -c Release --filter "FullyQualifiedName~EditorControlReplaceExactTests"
```

Expected: Step 1 で足した 4 件が FAIL(まだ `GetExactChangeRange` が無い)以外、
**既存テストは全件 PASS**。

ビルドが通らない場合は `GetExactChangeRange` 未実装が原因なので、Step 5 を先に済ませてよい。
その場合は Step 5 の後にもう一度「既存テストが全件 PASS」を確認すること
(**括り出しの挙動不変を単独で確かめる機会を失わないため**、可能なら
Step 1 のテストを一時的にコメントアウトしてここで一度走らせるのが望ましい)。

### Step 5: `GetExactChangeRange` を実装する

`ExactRangeParts` の直後に置く。

```csharp
    /// <summary>
    /// <see cref="ReplaceCharRangeExact"/> が同じ引数・同じ世代で呼ばれたときに、
    /// <b>本文の内容が変わりうる文字範囲</b>を、何も書かずに返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 返すのは <see cref="ReplaceCharRange"/> へ渡す「広げた範囲」<b>ではない</b>。巻き込み復元は
    /// 長さ保存で、広げた分の接頭辞 / 接尾辞はそのまま書き戻されるため、CRLF を割ったときの
    /// <c>\r</c> / <c>\n</c> は無傷で戻る=内容は変わらない。例外は復元する半身が
    /// <b>孤立サロゲートになる場合</b>で、このとき UTF-8 往復で U+FFFD へ潰れる
    /// (<see cref="ReplaceCharRangeExact"/> の remarks 参照)。この 1 形だけ広げた範囲を返す。
    /// </para>
    /// <para>
    /// ゼロ幅(純挿入)は挿入点が論理文字の境界まで<b>後退</b>しうるので、後退後の位置の空範囲を返す。
    /// 呼び出し側が <c>start</c> から導出してはならないのは
    /// <see cref="ReplaceCharRangeExact"/> の戻り値と同じ理由。
    /// </para>
    /// <para>
    /// <b>用途</b>: 「選択範囲のみ」の置換が、ユーザーの選んでいない位置を書き換えないことを
    /// <b>書く前に</b>確かめる(<c>SearchController</c>)。後退が起きる条件を呼び出し側で
    /// 数え上げるのは本クラスの規則の複製であり、規則が変われば黙って腐る。
    /// </para>
    /// <para>
    /// 書けない状態(<c>_buffer is null</c> / <see cref="ReadOnly"/>)では何も変わらないので
    /// クランプした位置の空範囲を返す。
    /// </para>
    /// </remarks>
    /// <returns>内容が変わりうる文字範囲 <c>[Start, End)</c>。この範囲の外側は変わらない。</returns>
    public (int Start, int End) GetExactChangeRange(int start, int length)
    {
        if (_buffer is null || ReadOnly)
        {
            int noop = Math.Clamp(start, 0, TextLength);
            return (noop, noop);
        }
        var snap = _buffer.Current;
        var (s0, e0, s, e) = ExactRangeParts(snap, start, length);
        if (s0 == e0)
            return (s, s); // ゼロ幅=後退後の挿入点
        // s < s0 になる後退要因は「s0 が low サロゲート」か「s0 が LF で直前が CR」の 2 つだけなので、
        // s0 の文字が low サロゲートかで弁別できる(終端側も同じ)。
        // s < s0 は s0 < CharLength を含意する(SnapToLogicalCharStart は EOF を動かさない)ので
        // GetChar(s0) は常に安全。
        int changeStart = s < s0 && char.IsLowSurrogate(snap.GetChar(s0)) ? s : s0;
        int changeEnd = e > e0 && char.IsLowSurrogate(snap.GetChar(e0)) ? e : e0;
        return (changeStart, changeEnd);
    }
```

### Step 6: 狙い撃ちテストが通ることを確認する

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Editor.Tests -c Release --filter "FullyQualifiedName~EditorControlReplaceExactTests"
```

Expected: 全件 PASS。**落ちたら期待値を疑う**(`(1,3)` / `(4,5)` / `(3,3)` はいずれも
計画時の机上計算であって実測値ではない)。

### Step 7: 契約そのものを全数プローブで固定する

**これが本 seam の契約の網**。「返した範囲の外側は、実際に置換した後も 1 文字も変わらない」。
`ReplaceCharRange` へ渡す範囲との一致ではないことに注意
(一致で書くと CRLF ケースを取り違える)。

```csharp
    [Fact]
    public void GetExactChangeRange_OutsideOfReturnedRangeNeverChanges_Exhaustive() =>
        Sta.Run(() =>
        {
            string[] docs =
            {
                "",
                "abc",
                "a\r\nb",
                "\r\n",
                "a\U0001F600b",
                "\U0001F600",
                "a\rb",
                "a\nb",
            };
            int[] lengths = { -1, 0, 1, 2, 3 };
            string[] repls = { "", "X", "\r\n", "\U0001F600" };

            using var ctrl = new EditorControl(); // 反復ごとに SetSource で作り直す
            foreach (string doc in docs)
                for (int start = -1; start <= doc.Length + 1; start++)
                    foreach (int len in lengths)
                        foreach (string repl in repls)
                        {
                            ctrl.SetSource(TextBuffer.FromString(doc));
                            string before = ctrl.Text;
                            var (cs, ce) = ctrl.GetExactChangeRange(start, len);

                            string ctx = $"doc='{before}' start={start} len={len} repl='{repl}'";
                            Assert.True(0 <= cs && cs <= ce && ce <= before.Length, ctx);

                            ctrl.ReplaceCharRangeExact(start, len, repl);
                            string after = ctrl.Text;

                            int tail = before.Length - ce;
                            Assert.True(after.Length >= cs + tail, ctx);
                            Assert.Equal(before[..cs], after[..cs]); // 前側は不変
                            Assert.Equal(before[ce..], after[(after.Length - tail)..]); // 後側は不変
                        }
        });
```

```powershell
dotnet test tests/kxEdit.Editor.Tests -c Release --filter "FullyQualifiedName~GetExactChangeRange_OutsideOfReturnedRangeNeverChanges_Exhaustive"
```

Expected: PASS。

**落ちたら実装が間違っている可能性が高い**(この主張は seam の定義そのものなので、
期待値を緩める方向で直してはいけない)。落ちた `ctx` を設計書 §9 へ記録し、
`GetExactChangeRange` の判定式を見直すこと。

### Step 8: ミューテーション検証(スポットチェック)

| # | 変異 | 期待 |
|---|------|------|
| 1 | `changeStart` の `s < s0 && char.IsLowSurrogate(...)` → `s < s0` | 赤(CRLF 弁別テスト) |
| 2 | `changeEnd` の `e > e0 && char.IsLowSurrogate(...)` → `e > e0` | 赤(CRLF 弁別テスト) |
| 3 | `changeStart` の条件全体を削除(常に `s0`) | 赤(サロゲート弁別 + 全数) |
| 4 | `changeEnd` の条件全体を削除(常に `e0`) | 赤(サロゲート弁別 + 全数) |
| 5 | `if (s0 == e0) return (s, s);` → `return (s0, s0);` | 赤(ゼロ幅弁別) |
| 6 | `ExactRangeParts` の `SnapToLogicalCharStart(snap, s0)` を `s0` へ | 赤(既存 `ReplaceCharRangeExact` テスト) |

**#1〜#4 は AND ガードを条件ごとに 1 つずつ変異させている。** まとめて消すと片側の
網の欠落を見逃す。

**#6 の注意**: `SnapToLogicalCharStart` ↔ `SnapToLogicalCharEnd` の**単純入替え**は
S4144(同一実装の重複メソッド)ではなく別の理由でビルドが通る場合があるが、
[[mutation-harness-exit-code-trap]] のとおり `grep -E " error [A-Z]+[0-9]+"` で
ビルド成否を判定すること。

### Step 9: commit

```powershell
git add src/kxEdit.Editor/EditorControl.cs tests/kxEdit.Editor.Tests/EditorControlReplaceExactTests.cs
git commit -m "feat(editor): 実際に内容が変わる範囲を問う GetExactChangeRange を足す

「選択範囲のみ」の置換が選択外を書き換えないことを、書く前に確かめられるようにする。
呼び出し側でゼロ幅後退の条件を数え上げるのは EditorControl の規則の複製であり、
規則が変われば黙って腐る(監査 §9 V-7: 前置ガードの列挙は原理的に漏れる)。

返すのは ReplaceCharRange へ渡す「広げた範囲」ではなく「内容が変わりうる範囲」。
巻き込み復元は長さ保存なので CRLF を割った \\r / \\n は無傷で戻る。内容が壊れるのは
復元する半身が孤立サロゲートになる場合だけで、そこだけ広げた範囲を返す。

範囲計算は ExactRangeParts へ括り出して ReplaceCharRangeExact と共有する
(2 実装に分かれると「問うた範囲と実際に書く範囲が違う」形で腐るため)。
括り出しは挙動不変(既存テスト全件 green)。"
```

### Step 10: 前倒しコード品質レビュー(CLAUDE.md §3-4)

**別エージェント**を起動する。観点:

- `ExactRangeParts` の括り出しが**本当に挙動不変**か(式を 1 つずつ突き合わせる)
- `GetExactChangeRange` の契約記述と実装が一致しているか
- `s < s0` が `s0 < CharLength` を含意するという主張の検証(`GetChar(s0)` の安全性)
- 全数プローブの主張が実装のトートロジーになっていないか
  (`GetExactChangeRange` の内部と同じ式で期待値を作っていないか)

---

## Task 4: `SearchController` に包含検査・`ReadOnly` ガードを入れる

**Files:**
- Modify: `src/kxEdit.App/SearchController.cs`(`ReplaceOne` `:268-` / `ReplaceAll` `:433-`)
- Test: `tests/kxEdit.App.Tests/SearchControllerTests.cs`

### Step 1: バグの再現を**変更前の src で**確かめる

**このステップを飛ばすと、fixture がバグへ到達していないまま「直した」と主張することになる。**
設計書 §1.1 の再現手順が現在の実装で本当に起きるかを、まず**バグの出力を期待値にして**確かめる。

`tests/kxEdit.App.Tests/SearchControllerTests.cs` の T-3 節の末尾に追加する。

```csharp
    // ===== B2: 実際に内容が変わる範囲でスコープ包含を検査する(2026-09-01) =====

    [Fact]
    public void ReplaceOne_InSelection_ZeroWidthHitRetreatingOutsideScope_DoesNotReplace() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("X\rYZ"); // X(0) \r(1) Y(2) Z(3)
            host.View.Pattern = "Y";
            host.View.Replacement = "\n";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(2, 2); // "YZ" を捕捉(prefix "X\r" を除外)
            host.Search.OnInSelectionToggled(true);

            host.Search.ReplaceOne(); // Y → \n。スコープ始端 2 が CRLF の内側になる
            Assert.Equal("X\r\nZ", doc.Editor.Text);

            // ゼロ幅ヒット [2,2) はスコープ内だが、挿入点は論理文字の境界 1 へ後退する。
            host.View.Pattern = @"(?<=\r)";
            host.View.Replacement = "Q";
            host.View.UseRegex = true;
            doc.Editor.SelectCharRange(2, 0); // 探索起点をスコープ始端へ戻す

            host.Search.ReplaceOne();

            // TODO(Step 1): ここは**変更前の src の実測値**を書く。設計書 §1.1 の予測は
            // "XQ\r\nZ"(スコープ外の位置 1 へ挿入)+ 成功発声。予測どおりなら
            // Step 4 で正しい期待値(本文不変 + 拒否文言)へ反転させる。
            Assert.Equal("XQ\r\nZ", doc.Editor.Text);
        });
```

```powershell
dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~ZeroWidthHitRetreatingOutsideScope"
```

Expected: **PASS**(=バグが再現した)。

**PASS しない場合**(これは十分ありうる):

1. `doc.Editor.Text` が `"X\r\nZ"` でない → 1 回目の置換の前提が崩れている。
2. 本文が変わっていない(`"X\r\nZ"` のまま) → **ゼロ幅ヒットへ到達していない**。
   `ReplaceOne` の分岐 3 は `from = Math.Max(selStart, scope.Start)` から `FindNext` するので、
   `selStart` が 2 より後ろだとヒットを飛ばす。`SelectCharRange(2, 0)` が
   キャレットを 1 へスナップする可能性も込みで、`host.Announcer.Said[^1]` を出力して
   どの分岐へ落ちたか(「これ以上見つかりません」なら探索起点の問題)を確かめる。
3. どうしても到達しないなら、**設計書 §1.1 の再現条件のほうが誤っている**。
   その事実を設計書 §9 へ記録し、到達経路を探し直す(PR #56 §9.10 は実測と称しているので、
   PR #56 のブランチでの再現手順を読み直すこと)。

**「再現できなかったから網は書けない」で済ませないこと** = [[net-absence-claims-are-also-verifiable]]。

再現を確認したら、この時点では commit しない(次の Step で期待値を反転させる)。

### Step 2: `ReplaceAll` の拒否分岐に到達する fixture を探す

設計書 §2.1 が**未確定**とした点。スコープ端がサロゲートペアの内側に来る状態を作れるかを探す。

探索の指針:

- スコープ端が pair の内側に来るには、再捕捉した断片が**孤立 high サロゲートで終わる**
  (終端側)か、**孤立 low サロゲートで始まる**(始端側)必要がある。
- 断片は `ReplaceInRange` が組むので、置換文字列の端にサロゲートの片割れが来る必要がある。
  置換文字列は `TextBuffer` を経由しないので孤立サロゲートを含みうる**かもしれない**
  (`FakeFindReplaceView.Replacement` に直接代入できる)。
- `_selectionScope` の再捕捉は `ReplaceOne`(`grown`)と `ReplaceAll`
  (`rangeStart + fragment.Length`)の 2 経路がある。両方を試す。

**制限時間の目安: 30 分**。

- **到達 fixture が見つかった場合** → Step 3 で `ReplaceAll` にもガードを入れ、その fixture を
  L3 テストにする。
- **見つからなかった場合** → **`ReplaceAll` にはガードを入れない**。網の張れない分岐を
  残さない(ミューテーションで必ず生存する死んだガードになる)。設計書 §9 へ
  「探した範囲」と「入れなかった判断」を記録する。設計書 §2.1 の
  「`ReplaceAll` にも同じ検査を入れる」はこの結果で確定する。

**どちらの結論でも、探した範囲を書き残すこと。** 結論だけ書いて根拠を書かないのが
[[rationale-not-just-conclusion]] の失敗型。

### Step 3: `ReadOnly` ガードと包含検査を実装する

**(a) `ReplaceOne` の `ReadOnly` ガード**(`IsCsvModeActive` チェックの直後)

```csharp
        if (IsCsvModeActive)
        {
            Announce(CsvAnnounceFormatter.BlockedInCsvMode);
            return;
        }
        // 委譲先(ReplaceCharRangeExact)は ReadOnly のとき何も書かずに戻るが、ここから先は
        // それを見ずにスコープを更新し成功発声する(PR #56 §9.10)。snap2 == snap なので
        // 世代チェックを通る不正なスコープが残る。到達経路は実質無い(CSV モードは上で弾かれ、
        // 保存中の一時解除に ReplaceOne が割り込む経路がない)が、
        // 「呼び出し側が委譲先の no-op を見ていない」構造そのものをここで消す。
        // 発声しないのは、App に「読み取り専用」を告げる既存文言が無く、
        // 新文言を足しても L5 で確認できる操作が作れないため。
        if (ed.ReadOnly)
            return;
```

**(b) `ReplaceAll` にも同じガード**(`IsCsvModeActive` チェックの直後。コメントは
「理由は `ReplaceOne` の同じガードを参照」と 1 行で済ませる)

**(c) `ReplaceOne` の包含検査**(`int afterRepl = ed.ReplaceCharRangeExact(...)` の直前)

```csharp
            // 事後条件: 実際に内容が変わる範囲がスコープに収まることを、書く前に確かめる。
            // WithinScope は生の UTF-16 span しか見ないので、ゼロ幅マッチの挿入点が論理文字の
            // 境界まで後退してスコープの外へ落ちる経路(設計書 §1.1)を防げない。
            // 後退条件をここで数え上げるのは EditorControl の規則の複製=規則が変われば腐るので、
            // 「実際に何を変えるか」を当人へ問う(監査 §9 V-7 の教訓)。
            if (scope is { } check)
            {
                var change = ed.GetExactChangeRange(span.Start, span.Length);
                if (change.Start < check.Start || change.End > check.End)
                {
                    Announce("選択範囲の外に及ぶため置換できません");
                    return;
                }
            }
```

**(d) `grown` の根拠コメントを訂正する**(`:378-391` の「既知の穴」の記述)

現在のコメントは「ゼロ幅では始端が後退するのでこの式は成り立たない=既知の穴」と書いている。
検査が入った今、その記述は**事実として偽**になる。次で置き換える。

```csharp
                // 置換後のスコープを新世代で捕捉し直す(ReplaceAll の復帰処理と同じ理由)。
                // これが無いと次の置換が世代不一致=「陳腐化」で拒否される。
                // 終端の差分が repl.Length - span.Length ちょうどなのは、ReplaceCharRangeExact の
                // 巻き込み復元が長さ保存(削った prefix / suffix をそのまま書き戻す)だから。
                // 始端を据え置ける根拠は、直前の GetExactChangeRange 検査を通ったヒットだけが
                // ここへ来ること: 実際に内容が変わる範囲は scope.Start 以降に収まっているので、
                // scope.Start より前の内容は不変。
                // 旧版はここに「ゼロ幅では始端が後退するのでスコープ外へ落ちる既知の穴がある」と
                // 書いていた(PR #56 §9.10)。その穴は上の検査で塞いだ(2026-09-01 B2)。
```

**(e) `ReplaceAll` の包含検査** — Step 2 の結果が「到達 fixture あり」のときだけ入れる。
入れる場合は `ed.ReplaceCharRangeExact(rangeStart, rangeLen, fragment);` の直前に置く。

```csharp
            if (d.InSelection)
            {
                // ReplaceOne と同じ事後条件検査(片方だけ通る非一貫を作らない)。
                // 端が CRLF の内側の場合は復元が無傷なので通す
                // (PR #56 §9.9 が根治した挙動をここで打ち消さない)。
                var change = ed.GetExactChangeRange(rangeStart, rangeLen);
                if (change.Start < rangeStart || change.End > rangeStart + rangeLen)
                {
                    Announce("選択範囲の外に及ぶため置換できません");
                    return;
                }
            }
```

### Step 4: Step 1 のテストの期待値を反転させる

```csharp
            host.Search.ReplaceOne();

            // 修正前は "XQ\r\nZ"=スコープ外(位置 1)へ挿入したうえ成功発声していた。
            Assert.Equal("X\r\nZ", doc.Editor.Text); // 本文は 1 文字も変わらない
            Assert.Equal("選択範囲の外に及ぶため置換できません", host.Announcer.Said[^1]);
```

### Step 5: 残りの L3 テストを書く

```csharp
    [Fact]
    public void ReplaceOne_InSelection_RefusalDoesNotInvalidateScope() =>
        Sta.Run(() =>
        {
            // 拒否は「このヒットは置換できない」であってスコープの破棄ではない。
            // 拒否のあとで通常のヒットが置換できることで示す。
            using var host = new Host();
            var doc = host.NewDoc("X\rYZ");
            host.View.Pattern = "Y";
            host.View.Replacement = "\n";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(2, 2);
            host.Search.OnInSelectionToggled(true);
            host.Search.ReplaceOne();

            host.View.Pattern = @"(?<=\r)";
            host.View.Replacement = "Q";
            host.View.UseRegex = true;
            doc.Editor.SelectCharRange(2, 0);
            host.Search.ReplaceOne(); // 拒否される

            host.View.Pattern = "Z";
            host.View.Replacement = "W";
            host.View.UseRegex = false;
            host.Search.ReplaceOne();

            Assert.Equal("X\r\nW", doc.Editor.Text); // スコープは生きている
        });

    [Fact]
    public void ReplaceOne_InSelection_ScopeOnBoundaries_StillReplaces() =>
        Sta.Run(() =>
        {
            // 偽陽性の網。端が論理文字の境界に乗る通常のスコープでは従来どおり置換できる。
            // prefix "abc " と suffix " abc" の両方を除外できる fixture(全選択との区別)。
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3); // 中央の "abc" だけ
            host.Search.OnInSelectionToggled(true);

            host.Search.ReplaceOne();

            Assert.Equal("abc X abc", doc.Editor.Text);
        });

    [Fact]
    public void ReplaceOne_ReadOnly_ChangesNothingAndSaysNothing() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.Search.OpenReplace();
            doc.Editor.ReadOnly = true;
            int saidBefore = host.Announcer.Said.Count;

            host.Search.ReplaceOne();

            Assert.Equal("abc abc", doc.Editor.Text);
            Assert.Equal(saidBefore, host.Announcer.Said.Count);
        });

    [Fact]
    public void ReplaceAll_InSelection_ReMatchesInsideScope() =>
        Sta.Run(() =>
        {
            // M-29 の App 側。prefix "a" / suffix "a" を除外できる fixture(全選択との区別)。
            using var host = new Host();
            var doc = host.NewDoc("aaaa");
            host.View.Pattern = "aa";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(1, 2); // 中央の "aa" だけ
            host.Search.OnInSelectionToggled(true);

            host.Search.ReplaceAll();

            // 修正前は 0 件(全文ヒット [0,2) / [2,4) がどちらもスコープに収まらない)。
            Assert.Equal("aXa", doc.Editor.Text);
            Assert.Equal("1 件置換しました", host.Announcer.Said[^1]);
        });
```

`ReplaceOne_ReadOnly_ChangesNothingAndSaysNothing` は **`Said.Count` の増分**で
「発声しない」を見ている(`Said[^1]` だと直前の `OpenReplace` の発声を拾って
偽陽性になる)。

### Step 6: テストを走らせる

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~SearchControllerTests"
```

Expected: 全件 PASS。

**特に確認すること**: 既存の `ReplaceAll_InSelection_ScopeEndInsideCrlf_DoesNotDuplicateCr`
(`:1063`)と `ReplaceAll_InSelection_ScopeStartInsideCrlf_DoesNotDeleteOutsideCr`(`:1093`)が
**green のまま**であること。これが赤なら設計書 §2.1 の「CRLF は通す」規則が壊れている
(= PR #56 §9.9 の修正を打ち消している)。**この 2 件のために新規テストは書かない**
(同じ主張の重複を足さない)。

### Step 7: ミューテーション検証(スポットチェック)

| # | 変異 | 期待 |
|---|------|------|
| 1 | `change.Start < check.Start` を削除(`change.End > check.End` だけ) | 赤(ゼロ幅後退テスト) |
| 2 | `change.End > check.End` を削除 | 赤 or **生存**(生存なら終端側の網が無い=網を足すか、無い理由を記録する) |
| 3 | `ed.GetExactChangeRange(span.Start, span.Length)` → `(span.Start, span.End)` | 赤(ゼロ幅後退テスト) |
| 4 | `if (ed.ReadOnly) return;` を削除 | 赤(`ReplaceOne_ReadOnly_...`) |

**#2 が生存したら「網が無い」ことが判明したということ**。書けるはずの網を書かずに
済ませない = [[net-absence-claims-are-also-verifiable]]。終端側でスコープを越える形
(スコープ末尾が論理文字の内側 + そこにヒット)を探し、見つからなければ**探した範囲を記録する**。

### Step 8: commit

```powershell
git add src/kxEdit.App/SearchController.cs tests/kxEdit.App.Tests/SearchControllerTests.cs
git commit -m "fix(app): 置換が実際に変える範囲でスコープ包含を検査する

PR #56 §9.10 の申し送り 2 件。

(1) ゼロ幅ヒットのスコープ外書込: WithinScope は生の UTF-16 span しか見ないが、
ReplaceCharRangeExact はゼロ幅の挿入点を論理文字の境界まで後退させる。scope.Start が
論理文字の内側にあると、判定を通ったヒットの挿入がスコープの外へ落ち、成功発声する。
書く直前に GetExactChangeRange で「実際に内容が変わる範囲」を問い、収まらなければ
書かずに拒否する。後退条件を App 側で数え上げるのは EditorControl の規則の複製に
なるため採らない(監査 §9 V-7)。

(2) ReadOnly の no-op でのスコープ伸縮: 委譲先が何も書かずに戻るのに、呼び出し側が
それを見ずにスコープを更新して成功発声していた。早期 return で塞ぐ。

grown の根拠コメントにあった「ゼロ幅では始端が後退する既知の穴」の記述は、(1) の
検査で塞いだので事実として偽になった。検査を通ったヒットだけが到達するという
正しい根拠へ書き換えた。"
```

---

## Task 5: L5 チェックリストを起こす

**Files:**
- Create: `docs/plans/2026-09-01-replace-write-range-postcondition-l5-checklist.md`

### Step 1: 既存チェックリストの形を読む

```powershell
Get-Content docs/plans/2026-08-29-replace-one-hit-and-scope-l5-checklist.md -TotalCount 60
```

同じ subsystem(置換 + 選択範囲のみ)なので、**項目の書き方と操作手順の粒度をそこに合わせる**。

### Step 2: 項目を起こす

最低限、次を含めること。**実装後に実際に起きる挙動を確かめてから書く**
(予測で書くと L5 実施者が到達できない手順になる)。

| # | 確認内容 | 期待 |
|---|---------|------|
| 1 | 新文言「選択範囲の外に及ぶため置換できません」が NVDA で読まれる | 逐語一致(スピーチビューアーで確認) |
| 2 | 拒否時にキャレット / 選択が動かない | UIA の選択変更イベントが飛ばないこと。**飛ばないことが正しい** |
| 3 | 「選択範囲のみ」の一括置換で件数発声が変わる(M-29) | 選択直前から始まるヒットがある文書で、修正前 0 件 → 修正後 N 件 |
| 4 | ゼロ幅正規表現(`\b` / `(?<=x)`)での単発置換が通常どおり動く | 退行が無いこと |
| 5 | 一括置換で絵文字・CRLF が壊れない | ゼロ幅パターンで置換して本文が無傷 |

**項目 1 は PR #53 で確立した NVDA スピーチビューアーによる逐語検証手法に従う**
(= [[save-encoding-loss-warning]])。

### Step 3: 傘設計書の L5 台帳へ載せる

`docs/plans/2026-08-31-v0.2-remaining-work-design.md` §7.1 の表の「新規」行が
本ブランチの項目を含むことを確認する。**傘設計書は策定時スナップショットなので
本文は書き換えない**(CLAUDE.md §8)。台帳の統合は L5 実施時に行う。

### Step 4: commit

```powershell
git add docs/plans/2026-09-01-replace-write-range-postcondition-l5-checklist.md
git commit -m "docs(plans): B2 の L5 実機 SR 検証チェックリストを起こす"
```

---

## Task 6: 最終ブランチレビュー 2 パス → 品質ゲート → PR

### Step 1: 設計書へ実施記録を追記する

`docs/plans/2026-09-01-replace-write-range-postcondition-design.md` に `## 8. 実装時の追記(実施記録)`
を足し、次を書く。**§1〜§7 の策定内容は書き換えない**(CLAUDE.md §8)。

- 計画の期待値が実測で反証された箇所(あれば全件)
- Task 4 Step 2 の `ReplaceAll` 到達 fixture 探索の結果と、入れた / 入れなかった判断
- Task 4 Step 7 の #2 変異が生存したか、生存したならその後どうしたか
- ミューテーション検証で潰した範囲(再監査時の省力化のため)

### Step 2: 最終レビュー 2 パスを**別エージェントで独立に**起動する

CLAUDE.md §3-5。**1 起動に混載しない**(混載するとレビューが浅くなる)。

**パス 1(コード品質)** — ミューテーション検証のスポットチェック込み。
`git diff main..HEAD` 全体と設計書を渡す。

**パス 2(脆弱性)** — 同じ差分を渡す。観点は Task 2 Step 8 の脆弱性観点に加えて:
- 「実際に変わる範囲」の判定が偽陰性(見逃し)を起こす入力
- スコープ検査の追加で新たに到達可能になった経路
- `ReplaceOne` の分岐 1 / 2 / 3 のどこから来ても検査が効くか

指摘は CLAUDE.md §4 の 3 択で処理し、**修正は別 fixup commit で積む**(元 commit を書き換えない)。

### Step 3: 品質ゲート

```powershell
pwsh tools/pre-merge-check.ps1
```

Expected: **EXIT 0**。6 つのテストステップ(Release 3 本 + Debug 3 本)がすべて green。

Debug が赤なら `Debug.Assert` に引っかかっている。**assert を消して通すのではなく**、
契約違反の実体を調べること(B1 で復権させたばかりのゲートである)。

### Step 4: push して PR を作る

```powershell
git push -u origin feature/replace-write-range-postcondition
gh pr create --base main --title "置換の書込範囲を事後条件で保証する(B2)" --body-file <(...)
```

PR description は**日本語**で、目的・レビュー経緯・申し送りを書く(CLAUDE.md §7)。
必ず含めること:

- 4 件(ゼロ幅スコープ外書込 / 一括置換のゼロ幅・サロゲート割り / `ReadOnly` no-op / M-29)の対応
- **L5 未実施**であること(傘設計書 §7 の台帳へ統合してまとめて実施する)
- 設計書 §6 の申し送り
- Task 4 Step 2 の判断(`ReplaceAll` ガードの有無とその根拠)

---

## 完了条件

- [ ] Task 1〜5 の commit がブランチに積まれている
- [ ] 前倒しレビュー 3 回(Task 1 / 2 / 3)と最終 2 パスを**別エージェント**で実施した
- [ ] レビュー指摘が 3 択(fixup / 受容を記載 / 却下)で処理されている
- [ ] `tools/pre-merge-check.ps1` が EXIT 0
- [ ] 設計書に実施記録(§8)が追記されている
- [ ] L5 チェックリストが存在し、傘設計書 §7.1 の「新規」に含まれる
- [ ] PR が作成され、L5 未実施であることが description に明記されている
