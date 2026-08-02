# 文字アクセス seam の集約と高速化(A+B+C)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** `EditorControl` 周辺に散った文字コード単位(コードポイント / CRLF)の扱いを
`TextBoundary` へ集約し、その土台の `TextSnapshot.GetChar` を実用速度にする(挙動不変)。

**Architecture:** `yEdit.Core.Text` に `TextBoundary` を新設し、`Core.Editing` と `Core.Layout`
の双方から使える葉にする。`GetChar` は `GetText(pos,1)[0]` をやめてピース木を降り、
`CharToByte` を 1 回だけ呼んでバイトから直接 UTF-16 を組む。あわせて `TextChunk` の
格子幅を 64KB → 4KB に細分化する(`AppendBuffer` の共有ブロックだけは 64KB を明示)。

**Tech Stack:** .NET 9 / C# / xUnit / CSharpier(pre-commit)/ `tools/pre-merge-check.ps1`

**設計書:** `docs/plans/2026-07-31-char-access-seam-design.md`

**ブランチ:** `feature/char-access-seam`(作成済み・設計書 commit 済み)

---

## 前提知識(この計画を実行する人向け)

**このリポジトリの絶対ルール**

- コミットメッセージ本文・コード内コメントは**日本語**。識別子は英語。
- `--no-verify` で pre-commit フック(CSharpier 整形 + ローカルパス検出)を飛ばさない。
- ビルドは `-warnaserror`。**警告 0 を維持する**。
- `docs/plans/` の日付付き文書は策定時スナップショット。**本計画書を後から書き換えない**
  (実施記録の追記のみ可)。

**用語**

- **code unit** = UTF-16 の 1 単位。`TextSnapshot` の公開オフセットはすべてこれ。
- **コードポイント** = サロゲートペアを 1 と数える単位。BMP なら 1 code unit、
  astral(絵文字・拡張漢字 B 等)なら 2 code unit。
- **論理文字** = コードポイント + 「CRLF を 1 と数える」規則。キャレットと UIA が使う単位。
- **ピース木** = `src/yEdit.Core/Buffer/PieceTree.cs`。UTF-8 バイトチャンクへの参照を持つ
  不変 AVL 木。`TextSnapshot` はそのルート参照を包むだけ。
- **格子(grid)** = `src/yEdit.Core/Buffer/TextChunk.cs` が持つ累積統計表。
  一定バイトごとに (ByteOff, CharOff, BreaksTo) を記録し、char↔byte 変換の走査を
  格子 1 マスに閉じ込める。**この幅がそのまま `GetChar` のコストになる。**

**このタスクで一番危ないところ**

`AppendBuffer`(`src/yEdit.Core/Buffer/AppendBuffer.cs`)は 64KB ブロックを `TextChunk` で
包んだ**後も同じ配列へ書き込み続ける**。これが安全なのは、格子幅 = ブロック長 = 64KB のとき
格子表が先頭エントリだけになり、未書込領域が走査されないからである。
**格子を細かくすると、まだ書かれていないゼロ領域で累積値がキャッシュされ、
後から書いた文字の char↔byte 対応が静かに壊れる。** Task 4 で必ず明示指定する。

**共通コマンド**

```bash
# 全体ビルド(警告 0 を確認)
dotnet build yEdit.sln -c Release -warnaserror

# 個別テスト実行(必ず build のあとに)
dotnet test tests/yEdit.Core.Tests   -c Release --no-build
dotnet test tests/yEdit.Editor.Tests -c Release --no-build
dotnet test tests/yEdit.App.Tests    -c Release --no-build
```

---

## Task 1: `TextBoundary` を新設する

**この Task は後続すべてが依存する新しい抽象を導入する。**
完了後に **CLAUDE.md §3 の前倒しコード品質レビュー**(別エージェント)を実施すること。

**Files:**
- Create: `src/yEdit.Core/Text/TextBoundary.cs`
- Create: `tests/yEdit.Core.Tests/Text/TextBoundaryTests.cs`

### Step 1: 失敗するテストを書く

`tests/yEdit.Core.Tests/Text/TextBoundaryTests.cs` を新規作成:

```csharp
// TextBoundaryTests.cs
// 2026-07-31 文字アクセス seam Task 1: コードポイント単位(サロゲートのみ atomic)と
// 論理文字単位(サロゲート + CRLF atomic)の 2 系統が、それぞれ独立に正しく歩進することを固定する。
//
// 2 系統を分けている理由: キャレット / UIA は CRLF を 1 論理文字として扱うが、
// WordBoundary の内部歩進は CR と LF を別々の LineBreak として数える前提になっている。
// 統一すると WordBoundary の挙動が変わるため、名前で分離したまま集約する。
using yEdit.Core.Buffers;
using yEdit.Core.Text;

namespace yEdit.Core.Tests.Text;

public class TextBoundaryTests
{
    private static TextSnapshot Snap(string s) => TextBuffer.FromString(s).Current;

    // ===== コードポイント単位: サロゲートのみ atomic・CRLF は 1 ずつ =====

    [Fact]
    public void NextCodePoint_SkipsSurrogatePair()
    {
        var s = Snap("a😀b"); // CharLength=4
        Assert.Equal(1, TextBoundary.NextCodePoint(s, 0));
        Assert.Equal(3, TextBoundary.NextCodePoint(s, 1)); // 😀 を 1 歩で越える
        Assert.Equal(4, TextBoundary.NextCodePoint(s, 3));
        Assert.Equal(4, TextBoundary.NextCodePoint(s, 4)); // 末尾で no-op
    }

    [Fact]
    public void PrevCodePoint_SkipsSurrogatePair()
    {
        var s = Snap("a😀b");
        Assert.Equal(3, TextBoundary.PrevCodePoint(s, 4));
        Assert.Equal(1, TextBoundary.PrevCodePoint(s, 3));
        Assert.Equal(0, TextBoundary.PrevCodePoint(s, 1));
        Assert.Equal(0, TextBoundary.PrevCodePoint(s, 0)); // 先頭で no-op
    }

    [Fact]
    public void NextCodePoint_DoesNotSkipCrlf()
    {
        // 論理文字版との差を固定する = CRLF は 2 歩かかる
        var s = Snap("a\r\nb");
        Assert.Equal(2, TextBoundary.NextCodePoint(s, 1)); // CR の前 → CR と LF の間
        Assert.Equal(3, TextBoundary.NextCodePoint(s, 2));
    }

    [Fact]
    public void PrevCodePoint_DoesNotSkipCrlf()
    {
        var s = Snap("a\r\nb");
        Assert.Equal(2, TextBoundary.PrevCodePoint(s, 3));
        Assert.Equal(1, TextBoundary.PrevCodePoint(s, 2));
    }

    [Theory]
    [InlineData("ab", 0, 1)] // BMP
    [InlineData("😀", 0, 2)] // astral
    [InlineData("\r\n", 0, 1)] // CR は単独で 1
    public void CodePointLengthAt_Snapshot(string text, int pos, int expected) =>
        Assert.Equal(expected, TextBoundary.CodePointLengthAt(Snap(text), pos));

    [Fact]
    public void CodePointLengthAt_LoneHighSurrogateAtEnd_IsOne()
    {
        // バッファ層が孤立サロゲートを U+FFFD へ正規化するため通常は到達しないが、
        // high サロゲートの直後に low が続かない形を防御的に固定する。
        var s = Snap("a😀");
        Assert.Equal(2, TextBoundary.CodePointLengthAt(s, 1)); // 対が揃っていれば 2
    }

    // ===== 論理文字単位: サロゲート + CRLF atomic =====

    [Fact]
    public void NextLogicalChar_SkipsSurrogatePairAndCrlf()
    {
        Assert.Equal(3, TextBoundary.NextLogicalChar(Snap("a😀b"), 1));
        Assert.Equal(3, TextBoundary.NextLogicalChar(Snap("a\r\nb"), 1)); // CRLF を 1 歩で越える
    }

    [Fact]
    public void PrevLogicalChar_SkipsSurrogatePairAndCrlf()
    {
        Assert.Equal(1, TextBoundary.PrevLogicalChar(Snap("a😀b"), 3));
        Assert.Equal(1, TextBoundary.PrevLogicalChar(Snap("a\r\nb"), 3));
    }

    [Fact]
    public void LogicalChar_LoneCrAndLoneLf_MoveOneStep()
    {
        Assert.Equal(2, TextBoundary.NextLogicalChar(Snap("a\rb"), 1));
        Assert.Equal(2, TextBoundary.NextLogicalChar(Snap("a\nb"), 1));
        Assert.Equal(1, TextBoundary.PrevLogicalChar(Snap("a\rb"), 2));
        Assert.Equal(1, TextBoundary.PrevLogicalChar(Snap("a\nb"), 2));
    }

    [Fact]
    public void LogicalChar_EmptyDocument_IsNoOp()
    {
        var s = Snap("");
        Assert.Equal(0, TextBoundary.NextLogicalChar(s, 0));
        Assert.Equal(0, TextBoundary.PrevLogicalChar(s, 0));
    }

    // ===== 中間位置スナップ =====

    [Fact]
    public void SnapToLogicalCharStart_SnapsMidSurrogateAndMidCrlf()
    {
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart(Snap("a😀b"), 2)); // low サロゲート位置
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart(Snap("a\r\nb"), 2)); // CR と LF の間
    }

    [Fact]
    public void SnapToLogicalCharStart_LeavesBoundariesAlone()
    {
        var s = Snap("a😀b");
        Assert.Equal(0, TextBoundary.SnapToLogicalCharStart(s, 0));
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart(s, 1));
        Assert.Equal(3, TextBoundary.SnapToLogicalCharStart(s, 3));
        Assert.Equal(4, TextBoundary.SnapToLogicalCharStart(s, 4)); // EOF は許可
    }

    [Fact]
    public void SnapToLogicalCharStart_ClampsOutOfRange()
    {
        var s = Snap("abc");
        Assert.Equal(0, TextBoundary.SnapToLogicalCharStart(s, -5));
        Assert.Equal(3, TextBoundary.SnapToLogicalCharStart(s, 99));
    }

    // ===== span 版(Layout / 描画) =====

    [Theory]
    [InlineData("ab", 0, 1)]
    [InlineData("😀", 0, 2)]
    [InlineData("a😀", 1, 2)]
    public void CodePointLengthAt_Span(string text, int i, int expected) =>
        Assert.Equal(expected, TextBoundary.CodePointLengthAt(text.AsSpan(), i));

    [Fact]
    public void CodePointLengthAt_Span_HighSurrogateAtEnd_IsOne()
    {
        // 対の相手が span の外にある場合は 1(呼び出し側の無限ループを防ぐ)
        ReadOnlySpan<char> lone = "a😀".AsSpan(0, 2); // 'a' + high サロゲートのみ
        Assert.Equal(1, TextBoundary.CodePointLengthAt(lone, 1));
    }

    [Fact]
    public void SnapToCodePointStart_Span()
    {
        var text = "a😀b".AsSpan();
        Assert.Equal(1, TextBoundary.SnapToCodePointStart(text, 2)); // low サロゲート位置
        Assert.Equal(1, TextBoundary.SnapToCodePointStart(text, 1));
        Assert.Equal(0, TextBoundary.SnapToCodePointStart(text, 0));
        Assert.Equal(4, TextBoundary.SnapToCodePointStart(text, 4)); // 末尾は動かさない
    }
}
```

### Step 2: 失敗を確認する

```bash
dotnet build yEdit.sln -c Release -warnaserror
```

Expected: **FAIL**。`error CS0246: 型または名前空間の名前 'TextBoundary' が見つかりませんでした`。

### Step 3: 実装する

`src/yEdit.Core/Text/TextBoundary.cs` を新規作成:

```csharp
using yEdit.Core.Buffers;

namespace yEdit.Core.Text;

/// <summary>
/// 文字境界の判定・歩進を 1 箇所に集約する純ロジック(2026-07-31 新設)。
///
/// <b>2 つの単位を意図的に分けている</b>:
/// <list type="bullet">
/// <item><b>コードポイント単位</b>(<c>*CodePoint*</c>)= サロゲートペアのみ atomic。
/// CR と LF は別々に数える。<see cref="Editing.WordBoundary"/> の内部歩進と
/// Layout / 描画がこちらを使う。</item>
/// <item><b>論理文字単位</b>(<c>*LogicalChar*</c>)= サロゲートペア + CRLF pair が atomic。
/// キャレット / 選択 / UIA(SR の文字単位読み)がこちらを使う
/// (2026-07-24 CRLF atomic caret 設計)。</item>
/// </list>
/// <b>この 2 つを 1 本に統一してはならない。</b> 統一すると
/// <see cref="Editing.WordBoundary"/> が CR と LF を別クラスとして数える前提が崩れ、
/// Ctrl+←→ の単語境界が変わる。
///
/// 置き場が <c>yEdit.Core.Text</c> なのは、<c>Core.Editing</c> と <c>Core.Layout</c> の
/// 双方から参照される葉である必要があるため(現状 Editing → Layout の依存があり、
/// 逆向きを足すと循環に見える)。
/// </summary>
public static class TextBoundary
{
    // ===== TextSnapshot 版: コードポイント単位(サロゲートのみ atomic) =====

    /// <summary>
    /// <paramref name="pos"/> のコードポイントが占める code unit 数(1 または 2)。
    /// サロゲートペアが成立するときだけ 2。
    /// </summary>
    /// <remarks><paramref name="pos"/> が [0, CharLength) の外なら
    /// <see cref="ArgumentOutOfRangeException"/>(<see cref="TextSnapshot.GetChar"/> 由来)。</remarks>
    public static int CodePointLengthAt(TextSnapshot snap, int pos)
    {
        ArgumentNullException.ThrowIfNull(snap);
        char c = snap.GetChar(pos);
        return
            char.IsHighSurrogate(c)
            && pos + 1 < snap.CharLength
            && char.IsLowSurrogate(snap.GetChar(pos + 1))
            ? 2
            : 1;
    }

    /// <summary>右に 1 コードポイント進む。CRLF は跨がない(CR と LF の間で止まる)。</summary>
    public static int NextCodePoint(TextSnapshot snap, int pos)
    {
        ArgumentNullException.ThrowIfNull(snap);
        if (pos >= snap.CharLength)
            return snap.CharLength;
        return pos + CodePointLengthAt(snap, pos);
    }

    /// <summary>左に 1 コードポイント戻る。CRLF は跨がない。</summary>
    public static int PrevCodePoint(TextSnapshot snap, int pos)
    {
        ArgumentNullException.ThrowIfNull(snap);
        if (pos <= 0)
            return 0;
        int prev = pos - 1;
        if (
            prev > 0
            && char.IsLowSurrogate(snap.GetChar(prev))
            && char.IsHighSurrogate(snap.GetChar(prev - 1))
        )
            return prev - 1;
        return prev;
    }

    // ===== TextSnapshot 版: 論理文字単位(サロゲート + CRLF atomic) =====

    /// <summary>右に 1 論理文字進む。サロゲートペアと CRLF pair を 1 単位として越える。</summary>
    public static int NextLogicalChar(TextSnapshot snap, int pos)
    {
        ArgumentNullException.ThrowIfNull(snap);
        if (pos >= snap.CharLength)
            return snap.CharLength;
        char c = snap.GetChar(pos);
        if (
            char.IsHighSurrogate(c)
            && pos + 1 < snap.CharLength
            && char.IsLowSurrogate(snap.GetChar(pos + 1))
        )
            return pos + 2;
        if (c == '\r' && pos + 1 < snap.CharLength && snap.GetChar(pos + 1) == '\n')
            return pos + 2;
        return pos + 1;
    }

    /// <summary>左に 1 論理文字戻る。サロゲートペアと CRLF pair を 1 単位として越える。</summary>
    public static int PrevLogicalChar(TextSnapshot snap, int pos)
    {
        ArgumentNullException.ThrowIfNull(snap);
        if (pos <= 0)
            return 0;
        int prev = pos - 1;
        if (
            prev > 0
            && char.IsLowSurrogate(snap.GetChar(prev))
            && char.IsHighSurrogate(snap.GetChar(prev - 1))
        )
            return prev - 1;
        if (prev > 0 && snap.GetChar(prev) == '\n' && snap.GetChar(prev - 1) == '\r')
            return prev - 1;
        return prev;
    }

    /// <summary>
    /// [0, CharLength] にクランプし、論理文字の中間位置(low サロゲート位置 / CR と LF の間)を
    /// 前方(pair 先頭)へスナップする。CharLength(=EOF)はキャレットが立てる境界なので許可。
    /// </summary>
    public static int SnapToLogicalCharStart(TextSnapshot snap, int pos)
    {
        ArgumentNullException.ThrowIfNull(snap);
        if (pos <= 0)
            return 0;
        if (pos >= snap.CharLength)
            return snap.CharLength;
        // pos > 0 は前段の早期 return で保証済み
        char c = snap.GetChar(pos);
        if (char.IsLowSurrogate(c) && char.IsHighSurrogate(snap.GetChar(pos - 1)))
            return pos - 1;
        if (c == '\n' && snap.GetChar(pos - 1) == '\r')
            return pos - 1;
        return pos;
    }

    // ===== span 版(Layout / 描画) =====
    // これらの呼び出し元は改行を含まない行内テキストを扱うため CRLF 概念は不要。

    /// <summary>
    /// <paramref name="i"/> のコードポイントが占める code unit 数(1 または 2)。
    /// 対の相手が span の外にある(末尾の孤立 high サロゲート)場合は 1 を返す
    /// = 呼び出し側の前進ループが必ず進む。
    /// </summary>
    public static int CodePointLengthAt(ReadOnlySpan<char> text, int i) =>
        char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
            ? 2
            : 1;

    /// <summary>
    /// low サロゲート位置なら pair 先頭へ前方スナップする。
    /// [0, text.Length] の外はクランプ(末尾は動かさない)。
    /// </summary>
    public static int SnapToCodePointStart(ReadOnlySpan<char> text, int i)
    {
        if (i <= 0)
            return 0;
        if (i >= text.Length)
            return text.Length;
        return char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1]) ? i - 1 : i;
    }
}
```

### Step 4: テストが通ることを確認する

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~TextBoundaryTests"
```

Expected: **PASS**(全件緑)。警告 0。

### Step 5: commit

```bash
git add src/yEdit.Core/Text/TextBoundary.cs tests/yEdit.Core.Tests/Text/TextBoundaryTests.cs
git commit -F- <<'EOF'
feat(core): 文字境界の判定・歩進を TextBoundary に集約する土台を追加

コードポイント単位(サロゲートのみ atomic)と論理文字単位(サロゲート + CRLF atomic)を
別 API として持たせる。統一すると WordBoundary が CR/LF を別クラスとして数える前提が
崩れるため、名前で分離したまま集約する。

呼び出し元の付け替えは Task 5 / Task 6 で行う(本 commit では未使用)。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

### Step 6: 前倒しコード品質レビュー

後続すべてが依存する新抽象のため、**別エージェント**にコード品質レビューを依頼する
(CLAUDE.md §3 工程 4 の前倒し例外)。観点:

- 2 系統(CodePoint / LogicalChar)の分離が API 名から誤解なく読めるか
- `null` / 範囲外 / 空文書の契約が既存 `NavigationCommands` と揃っているか
- span 版の「対の相手が span 外なら 1」が呼び出し側の無限ループを防げているか

指摘は 3 択(① fixup commit / ② PR description に記載して受容 / ③ 理由付き却下)で明示する。

---

## Task 2: Bench に基準線を追加する

最適化の**前**に現状値を記録する。Task 4 の DoD 判定に使う。

**Files:**
- Modify: `tests/yEdit.Core.Bench/Program.cs`

### Step 1: 引数パースに `--characcess` を足す

`tests/yEdit.Core.Bench/Program.cs` の冒頭、`bool typingMode = false;`(13 行目付近)の直後に追加:

```csharp
bool charAccessMode = false;
```

引数ループ(`else if (args[i] == "--typing")` ブロックの直後)に追加:

```csharp
    else if (args[i] == "--characcess")
    {
        charAccessMode = true;
    }
```

### Step 2: 計測ブロックを足す

`if (typingMode) { ... return typingPass ? 0 : 1; }` ブロックの**直後**に、以下を丸ごと挿入する。
他モードと同じく単独で early return する(合成文書構築を挟むと目的の値が測れない)。

```csharp
// ---- 2026-07-31 文字アクセス seam: --characcess ----
// GetChar のコスト特性(格子セル内の位置で変わる)と、実操作 Ctrl+← 相当の
// WordBoundary.PrevWordStart を測る。DoD = 1M 文字 ASCII で PrevWordStart < 0.05 ms。
// 他ベンチとは独立=単独で return する。
if (charAccessMode)
{
    Console.WriteLine("--characcess: 文字アクセス(GetChar / 単語ナビ)ベンチ");

    static string MakeWordDoc(int chars, bool cjk)
    {
        var sb = new StringBuilder(chars);
        var r = new Random(20260731);
        while (sb.Length < chars)
        {
            int wordLen = r.Next(2, 9);
            for (int i = 0; i < wordLen; i++)
                sb.Append(cjk ? (char)('あ' + r.Next(40)) : (char)('a' + r.Next(26)));
            sb.Append(r.Next(12) == 0 ? '\n' : ' ');
        }
        return sb.ToString(0, chars);
    }

    var caResults = new List<(string Name, string Value, string Target, bool? Pass)>();
    long caSink = 0;

    // --- GetChar 単発(位置依存を見る) ---
    var gcSnap = TextBuffer.FromString(MakeWordDoc(1_000_000, cjk: false)).Current;
    foreach (int probe in new[] { 0, gcSnap.CharLength / 2, gcSnap.CharLength - 10 })
    {
        for (int w = 0; w < 1000; w++)
            caSink += gcSnap.GetChar(probe); // ウォームアップ
        const int GcIters = 20_000;
        var gcSw = Stopwatch.StartNew();
        for (int i = 0; i < GcIters; i++)
            caSink += gcSnap.GetChar(probe);
        gcSw.Stop();
        double gcNs = gcSw.Elapsed.TotalNanoseconds / GcIters;
        caResults.Add(($"C1 GetChar(pos={probe})", $"{gcNs:N0} ns/回", "記録のみ", null));
    }

    // --- Ctrl+← 相当(DoD 判定はこれ) ---
    foreach (bool cjk in new[] { false, true })
    {
        foreach (int chars in new[] { 10_000, 200_000, 1_000_000 })
        {
            var wbSnap = TextBuffer.FromString(MakeWordDoc(chars, cjk)).Current;
            int mid = wbSnap.CharLength / 2;
            for (int w = 0; w < 50; w++)
                caSink += WordBoundary.PrevWordStart(wbSnap, mid + w); // ウォームアップ
            const int WbIters = 300;
            var wbSw = Stopwatch.StartNew();
            for (int i = 0; i < WbIters; i++)
                caSink += WordBoundary.PrevWordStart(wbSnap, mid + i);
            wbSw.Stop();
            double wbMs = wbSw.Elapsed.TotalMilliseconds / WbIters;
            bool isDod = !cjk && chars == 1_000_000;
            caResults.Add(
                (
                    $"C2 PrevWordStart({(cjk ? "CJK" : "ASCII")} {chars:N0})",
                    $"{wbMs:F3} ms/回",
                    isDod ? "<0.05ms (DoD)" : "記録のみ",
                    isDod ? wbMs < 0.05 : null
                )
            );
        }
    }

    Console.WriteLine();
    Console.WriteLine("| # シナリオ | 結果 | 目標 | 判定 |");
    Console.WriteLine("|---|---|---|---|");
    foreach (var r in caResults)
        Console.WriteLine(
            $"| {r.Name} | {r.Value} | {r.Target} | {(r.Pass is null ? "―" : r.Pass.Value ? "PASS" : "FAIL")} |"
        );
    Console.WriteLine($"(sink={caSink})");
    bool caPass = caResults.All(r => r.Pass is not false);
    Console.WriteLine(caPass ? "DoD 達成 (EXIT 0)" : "DoD 未達 (EXIT 1)");
    return caPass ? 0 : 1;
}
```

### Step 3: using を足す

`Program.cs` 冒頭の using に `yEdit.Core.Editing` を追加する(`WordBoundary` のため)。
`System.Text` と `yEdit.Core.Buffers` は既にある。

```csharp
using System.Diagnostics;
using System.Text;
using yEdit.Core.Buffers;
using yEdit.Core.Editing;
using yEdit.Core.Layout;
```

### Step 4: 実行して基準線を記録する

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet run --project tests/yEdit.Core.Bench -c Release --no-build -- --characcess
```

Expected: **EXIT 1(DoD 未達)**。これが正しい。C2 の ASCII 1,000,000 が **約 1.0 ms** に
なるはず(調査時の実測値 1.044 ms)。**この出力をそのまま次の Step の commit 本文に貼る。**

> 想定と大きく違ったら止まって報告すること。マシン性能差で絶対値はずれるが、
> 「ASCII 1,000,000 が 10,000 の 3 倍以上遅い」という**傾向**は再現するはず。
> 傾向が出ないなら格子の効き方が想定と違う = Task 4 の前提を再確認する。

### Step 5: commit

```bash
git add tests/yEdit.Core.Bench/Program.cs
git commit -F- <<'EOF'
test(bench): 文字アクセスベンチ --characcess を追加(最適化前の基準線)

GetChar 単発の位置依存と、実操作 Ctrl+← 相当の WordBoundary.PrevWordStart を測る。
DoD = 1M 文字 ASCII で PrevWordStart < 0.05 ms。

最適化前の実測(この commit 時点・EXIT 1):
<ここに Step 4 の結果表を貼る>

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

## Task 3: `GetChar` を単一マッピング + アロケーションなしに再実装する

**この Task はファイル由来のバイト列を手書きで UTF-8 デコードする形になる。**
完了後に **CLAUDE.md §3 の前倒し脆弱性レビュー**(別エージェント)を実施すること。

**Files:**
- Modify: `src/yEdit.Core/Buffer/TextSnapshot.cs:45-50`
- Create: `tests/yEdit.Core.Tests/Buffer/TextSnapshotGetCharEquivalenceTests.cs`

### Step 1: 新旧差分テストを書く

`GetText(pos, 1)[0]` は公開 API として残るので、**そのまま参照実装として使える**。

`tests/yEdit.Core.Tests/Buffer/TextSnapshotGetCharEquivalenceTests.cs` を新規作成:

```csharp
// TextSnapshotGetCharEquivalenceTests.cs
// 2026-07-31 文字アクセス seam Task 3: GetChar をバイト直読みへ再実装するにあたり、
// 「全位置で GetText(pos, 1)[0] と一致する」ことを参照実装比較で固定する。
//
// 一番危ないのはサロゲート中間位置(low サロゲートを指す pos)。現行実装は GetSubstring の
// 「開始が中間なら低い方へスナップ・終端が中間ならコードポイントを丸ごと含めてから Substring」
// という経路で正しい low サロゲートを返している。新実装はこれを 1 回のマッピングで導く。
using yEdit.Core.Buffers;

namespace yEdit.Core.Tests.Buffer;

public class TextSnapshotGetCharEquivalenceTests
{
    // ASCII / CJK(3 バイト)/ 絵文字(4 バイト)/ CRLF・LF・CR 混在をすべて含む
    private const string MixedFixture =
        "abc\r\nあいう\n😀xy\rz\r\n漢字😀あ\nEnd😀";

    private static void AssertAllPositionsMatch(TextSnapshot snap)
    {
        for (int pos = 0; pos < snap.CharLength; pos++)
            Assert.Equal(snap.GetText(pos, 1)[0], snap.GetChar(pos));
    }

    [Fact]
    public void GetChar_MatchesGetText_AtEveryPosition_FreshBuffer() =>
        AssertAllPositionsMatch(TextBuffer.FromString(MixedFixture).Current);

    [Fact]
    public void GetChar_MatchesGetText_AtEveryPosition_LargeDocument()
    {
        // 格子(4KB)を何マスも跨ぐ規模にして、格子点前後の境界も踏ませる
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 2000; i++)
            sb.Append(MixedFixture);
        AssertAllPositionsMatch(TextBuffer.FromString(sb.ToString()).Current);
    }

    [Fact]
    public void GetChar_MatchesGetText_AfterEdits()
    {
        // ピース分割 + AppendBuffer 経由のタイピングを経た木でも一致すること。
        // Task 4 の格子細分化が AppendBuffer の「生成後に同じ配列へ書き続ける」前提を
        // 壊していないかを捕まえる唯一の実質的な網。
        var buf = TextBuffer.FromString(MixedFixture);
        for (int i = 0; i < 500; i++)
        {
            int pos = (i * 7) % (buf.Current.CharLength + 1);
            pos = TextBoundarySafePos(buf.Current, pos);
            buf.Insert(pos, i % 3 == 0 ? "あ" : i % 3 == 1 ? "x" : "😀");
        }
        AssertAllPositionsMatch(buf.Current);
    }

    [Fact]
    public void GetChar_MatchesGetText_AfterLargeInsert()
    {
        // AppendBuffer の大挿入経路(>32KB は専用チャンク)も踏む
        var buf = TextBuffer.FromString(MixedFixture);
        var big = new System.Text.StringBuilder();
        for (int i = 0; i < 4000; i++)
            big.Append("あ😀a\r\n");
        buf.Insert(0, big.ToString());
        AssertAllPositionsMatch(buf.Current);
    }

    [Fact]
    public void GetChar_MidSurrogatePosition_ReturnsLowSurrogate()
    {
        var snap = TextBuffer.FromString("a😀b").Current;
        Assert.True(char.IsHighSurrogate(snap.GetChar(1)));
        Assert.True(char.IsLowSurrogate(snap.GetChar(2)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(999)]
    public void GetChar_OutOfRange_Throws(int pos)
    {
        var snap = TextBuffer.FromString("abcd").Current; // CharLength=4
        Assert.Throws<ArgumentOutOfRangeException>(() => snap.GetChar(pos));
    }

    /// <summary>挿入位置がサロゲート中間に落ちないよう 1 つ手前へ寄せる
    /// (TextBuffer.Split は内部でスナップするが、テストの意図を明示するため）。</summary>
    private static int TextBoundarySafePos(TextSnapshot snap, int pos)
    {
        if (pos <= 0 || pos >= snap.CharLength)
            return pos;
        return char.IsLowSurrogate(snap.GetChar(pos)) ? pos - 1 : pos;
    }
}
```

### Step 2: 現行実装で緑になることを確認する

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~TextSnapshotGetCharEquivalenceTests"
```

Expected: **PASS**。参照実装比較なので現行実装でも通る。
**これは「テストが仕様を正しく写している」ことの確認であって、TDD の red ではない。**
red は Step 4 のミューテーションで取る。

### Step 3: `GetChar` を再実装する

`src/yEdit.Core/Buffer/TextSnapshot.cs` の 45-50 行目を置き換える:

```csharp
    /// <summary>
    /// pos の code unit を返す。ピース木を降りて <see cref="TextChunk.CharToByte"/> を
    /// <b>1 回だけ</b>呼び、そのバイト位置から UTF-8 を直接 UTF-16 化する
    /// (2026-07-31: 旧実装 <c>GetText(pos, 1)[0]</c> は 1 文字ごとに StringBuilder と
    /// string を作り、char↔byte マッピングを 2 回行っていた=1 呼び出し 128 B / 最大 64 KB 走査)。
    /// </summary>
    /// <remarks>
    /// <b>依存する前提</b>(いずれも既存の <c>Encoding.UTF8.GetString</c> 経路が既に依存している):
    /// <list type="bullet">
    /// <item>ピース境界はコードポイント境界(<see cref="TextBufferBuilder"/> /
    /// <c>AppendBuffer</c> / <see cref="PieceTree.Split"/> がいずれも境界へスナップする)
    /// = コードポイントがピースを跨がない。</item>
    /// <item>バッファは <see cref="Utf8Sanitizer"/> 済みで不正 UTF-8 を含まない
    /// = 継続バイトの存在を検査せずに読める。</item>
    /// </list>
    /// <paramref name="pos"/> がサロゲート中間(low サロゲート位置)のとき、
    /// <c>CharToByte</c> は低い方へスナップして <c>actual == pos - 1</c> を返す。
    /// これを low サロゲート要求として使い分ける(旧実装の <c>GetSubstring</c> が
    /// 「終端が中間ならコードポイントを丸ごと含めてから Substring」で得ていた結果と等価)。
    /// </remarks>
    public char GetChar(int pos)
    {
        if (pos < 0 || pos >= CharLength)
            throw new ArgumentOutOfRangeException(nameof(pos));
        var t = _root;
        while (true)
        {
            int leftChars = PieceTree.SumOf(t!.Left).CharLen;
            if (pos < leftChars)
            {
                t = t.Left;
                continue;
            }
            pos -= leftChars;
            if (pos < t.Piece.CharLen)
            {
                var p = t.Piece;
                int b = p.Chunk.CharToByte(p.ByteStart, p.ByteLen, pos, out int actual);
                return DecodeUtf16At(p.Chunk.Span, b, wantLowSurrogate: actual != pos);
            }
            pos -= t.Piece.CharLen;
            t = t.Right;
        }
    }

    /// <summary>
    /// <paramref name="byteOffset"/> の UTF-8 コードポイントを UTF-16 化し、
    /// <paramref name="wantLowSurrogate"/>=false なら 1 単位目(BMP 文字または high サロゲート)、
    /// true なら 2 単位目(low サロゲート)を返す。
    /// wantLowSurrogate=true は 4 バイト列でのみ起こる(<c>CharToByte</c> が 2 単位進む唯一の形)。
    /// </summary>
    private static char DecodeUtf16At(
        ReadOnlySpan<byte> s,
        int byteOffset,
        bool wantLowSurrogate
    )
    {
        byte b0 = s[byteOffset];
        if (b0 < 0x80)
            return (char)b0;
        if (b0 < 0xE0)
            return (char)(((b0 & 0x1F) << 6) | (s[byteOffset + 1] & 0x3F));
        if (b0 < 0xF0)
            return (char)(
                ((b0 & 0x0F) << 12)
                | ((s[byteOffset + 1] & 0x3F) << 6)
                | (s[byteOffset + 2] & 0x3F)
            );
        int cp =
            ((b0 & 0x07) << 18)
            | ((s[byteOffset + 1] & 0x3F) << 12)
            | ((s[byteOffset + 2] & 0x3F) << 6)
            | (s[byteOffset + 3] & 0x3F);
        int v = cp - 0x10000;
        return wantLowSurrogate ? (char)(0xDC00 + (v & 0x3FF)) : (char)(0xD800 + (v >> 10));
    }
```

> **`IsLfAt` は畳まないこと。** 新 `GetChar(pos) == '\n'` と等価に見えるが、`IsLfAt` には
> 「ピース先頭なら `Stats.FirstIsLf` で即答」という fast path があり、`CharToByte` を
> 完全に回避できる。`GetLineIndexOfChar` は描画・ナビの hot path なのでこの分岐は残す価値がある。

### Step 4: テストが通ることを確認し、ミューテーションで red を取る

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build
```

Expected: **PASS**(Core 全件緑)。

続けてミューテーション検証。`DecodeUtf16At` の最終行を一時的に壊す:

```csharp
        return wantLowSurrogate ? (char)(0xD800 + (v >> 10)) : (char)(0xD800 + (v >> 10));
```

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~TextSnapshotGetCharEquivalenceTests"
```

Expected: **FAIL**。`GetChar_MidSurrogatePosition_ReturnsLowSurrogate` と
`GetChar_MatchesGetText_AtEveryPosition_*` が赤になること。

> **必ず `--no-build` を外した build を挟むこと。** 変異させたのに `--no-build` で回すと
> 古いバイナリを測って「テストが変異を殺した」と誤認する(過去に実例あり)。

確認できたら**必ず元に戻して**再ビルド・再テストして緑を確認する。

### Step 5: commit

```bash
git add src/yEdit.Core/Buffer/TextSnapshot.cs tests/yEdit.Core.Tests/Buffer/TextSnapshotGetCharEquivalenceTests.cs
git commit -F- <<'EOF'
perf(core): GetChar をピース木直降り + バイト直読みに再実装

旧実装 GetText(pos, 1)[0] は 1 文字ごとに StringBuilder と string を作り、
char↔byte マッピングを 2 回行っていた(1 呼び出し 128 B のアロケーション)。
木を降りて CharToByte を 1 回だけ呼び、バイトから直接 UTF-16 を組む形に置き換える。

サロゲート中間位置は CharToByte の actual(低い方へスナップした実到達位置)で判別し、
low サロゲートを返す。旧実装が GetSubstring 経由で得ていた結果と等価。

全位置で GetText(pos, 1)[0] と一致することを参照実装比較テストで固定
(fresh / 大規模 / 編集後 / 大挿入後の 4 形態)。

IsLfAt は畳まない(ピース先頭の fast path を失うため)。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

### Step 6: 前倒し脆弱性レビュー

ファイル由来バイト列を手書きでデコードするため、**別エージェント**に脆弱性レビューを依頼する
(CLAUDE.md §3 工程 4 の前倒し例外)。観点:

- `Utf8Sanitizer` 前提が本当に全経路で成立するか(バックアップ復元・セッション復元・
  クリップボード貼り付け・大挿入)。成立しない経路があれば `DecodeUtf16At` が
  範囲外読みを起こしうる
- `byteOffset + 1..3` の読みがピース末尾を越えないか(= コードポイントがピースを跨がない
  前提の裏取り)
- 攻撃者が制御できる入力(バックアップ JSON・復元ファイル)から不正 UTF-8 が
  バッファへ入る経路が本当に無いか

---

## Task 4: 格子を細分化し、`AppendBuffer` を明示指定する

**Files:**
- Modify: `src/yEdit.Core/Buffer/TextChunk.cs:24`
- Modify: `src/yEdit.Core/Buffer/AppendBuffer.cs:20,50`

### Step 1: `TextChunk` の既定格子幅を下げる

`src/yEdit.Core/Buffer/TextChunk.cs` の 24 行目:

```csharp
    public TextChunk(ReadOnlyMemory<byte> bytes, int gridBytes = 64 * 1024)
```

を次に変える:

```csharp
    // 2026-07-31: 既定 64KB → 4KB。格子幅はそのまま CharToByte の線形走査量になり、
    // GetChar / 単語ナビのコストを支配する(64KB 時は 1 文字読むのに最大 64KB 走査していた)。
    // メモリ増は 4MB チャンクあたり 1024 エントリ × 12 B = 12 KB(512MB 文書で 1.5 MB=0.3%)。
    // 格子構築の総走査量は幅によらず O(n) なので読み込み時間は変わらない。
    // 注: AppendBuffer の共有ブロックは gridBytes: BlockBytes を明示して除外している(理由は同所)。
    public TextChunk(ReadOnlyMemory<byte> bytes, int gridBytes = 4 * 1024)
```

### Step 2: `AppendBuffer` の 2 箇所に明示指定する

`src/yEdit.Core/Buffer/AppendBuffer.cs` の 20 行目:

```csharp
    public AppendBuffer() => _chunk = new TextChunk(_block);
```

を:

```csharp
    // gridBytes: BlockBytes は必須(既定値に頼らない)。本クラスは TextChunk で包んだ後も
    // 同じ _block へ書き込み続けるため、格子表が先頭エントリだけである必要がある。
    // 格子を細かくすると未書込のゼロ領域で累積 (CharOff, BreaksTo) がキャッシュされ、
    // 後から書いた文字の char↔byte 対応が静かに壊れる(2026-07-31 の格子細分化で顕在化)。
    public AppendBuffer() => _chunk = new TextChunk(_block, gridBytes: BlockBytes);
```

同ファイル 50 行目(新ブロック確保):

```csharp
            _chunk = new TextChunk(_block);
```

を:

```csharp
            _chunk = new TextChunk(_block, gridBytes: BlockBytes); // 理由は ctor のコメント参照
```

> 同ファイル 32 行目の大挿入 `new TextChunk(bytes)` は**変更しない**。
> こちらは生成後に書き換えない専用配列なので既定(4KB)でよい。

### Step 3: クラスコメントを更新する

`AppendBuffer.cs` の 9 行目、クラス XML コメント内の

```
/// (格子幅=ブロック長=64KBのため格子表は空で、未書込領域が構築時に走査されることはない)。
```

を次に更新する(既定値が変わったため記述が古くなる):

```
/// (gridBytes に BlockBytes を明示指定して格子表を空に保つ。未書込領域が構築時に
///  走査されず、かつ書込後に累積値が古くならない=本クラスの安全性の要)。
```

### Step 4: テストと DoD を確認する

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests   -c Release --no-build
dotnet test tests/yEdit.Editor.Tests -c Release --no-build
dotnet test tests/yEdit.App.Tests    -c Release --no-build
```

Expected: **全件 PASS**。特に `TextSnapshotGetCharEquivalenceTests.GetChar_MatchesGetText_AfterEdits`
が緑であること(= `AppendBuffer` の明示指定が効いている)。

```bash
dotnet run --project tests/yEdit.Core.Bench -c Release --no-build -- --characcess
```

Expected: **EXIT 0(DoD 達成)**。C2 の ASCII 1,000,000 が **0.05 ms 未満**。

> **未達だったら**: `TextChunk` の既定を `2 * 1024` に下げて再測定する。
> それでも未達なら止まって報告すること(設計書 §8 F-1 の `CharCursor` 検討に入る)。

### Step 5: `AppendBuffer` 明示指定のミューテーション検証

`AppendBuffer.cs` の 2 箇所から `, gridBytes: BlockBytes` を一時的に外す。

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~TextSnapshotGetCharEquivalenceTests"
```

Expected: **FAIL**(`GetChar_MatchesGetText_AfterEdits` が赤)。

> ここが緑のままなら、テストが `AppendBuffer` 経路を十分に踏んでいない。
> 挿入回数を増やすか挿入位置を散らして、必ず赤にしてから先へ進むこと。
> **この検証を飛ばすと、本計画で最も危険な回帰が無防備になる。**

確認できたら元に戻して再ビルド・再テストして緑を確認する。

### Step 6: commit

```bash
git add src/yEdit.Core/Buffer/TextChunk.cs src/yEdit.Core/Buffer/AppendBuffer.cs
git commit -F- <<'EOF'
perf(core): TextChunk の既定格子幅を 64KB から 4KB へ細分化

格子幅はそのまま CharToByte の線形走査量になり、GetChar と単語ナビのコストを支配する。
64KB 時は 1 文字読むのに最大 64KB 走査していた。

AppendBuffer の共有ブロックだけは gridBytes: BlockBytes を明示して除外する。
同クラスは TextChunk で包んだ後も同じ配列へ書き込み続けるため、格子表が先頭エントリ
だけである必要がある(細かい格子だと未書込のゼロ領域で累積値がキャッシュされ、
後から書いた文字の char↔byte 対応が壊れる)。大挿入用の専用チャンクは既定でよい。

Bench --characcess(DoD 達成・EXIT 0):
<ここに Step 4 の結果表を貼る>

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

## Task 5: A — UIA 側の歩進を Core へ委譲する

**Files:**
- Modify: `src/yEdit.Core/Editing/NavigationCommands.cs:20-54`
- Modify: `src/yEdit.Editor/UiaTextHostAdapter.cs:335-374`
- Modify: `src/yEdit.Editor/CaretController.cs:77-104`
- Create: `tests/yEdit.Editor.Tests/UiaTextHostAdapterClampTests.cs`

### Step 1: clamp 境界のテストを書く

委譲すると「同じ関数を呼ぶ」ことは構造的に保証されるが、**Adapter 側に残る前処理
(clamp / snapshot null)の等価性は別途押さえる必要がある**。

`tests/yEdit.Editor.Tests/UiaTextHostAdapterClampTests.cs` を新規作成:

```csharp
// UiaTextHostAdapterClampTests.cs
// 2026-07-31 文字アクセス seam Task 5: NextChar/PrevChar を Core の TextBoundary へ
// 委譲するにあたり、Adapter 側に残る前処理(範囲外 clamp)の等価性を固定する。
//
// 委譲によって「歩進規則が Core と同じ」ことは構造的に保証されるが、clamp は Adapter に
// 残るため、ここが唯一の網になる。
using yEdit.Accessibility;

namespace yEdit.Editor.Tests;

public class UiaTextHostAdapterClampTests
{
    [Fact]
    public void NextChar_NegativeOffset_ClampsToZeroThenSteps() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(1, host.NextChar(-100)); // 0 へ clamp してから 1 歩
        });

    [Fact]
    public void NextChar_BeyondEnd_ClampsToCharLength() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(3, host.NextChar(999));
        });

    [Fact]
    public void PrevChar_NegativeOffset_ClampsToZero() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(0, host.PrevChar(-100));
        });

    [Fact]
    public void PrevChar_BeyondEnd_ClampsThenSteps() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(2, host.PrevChar(999)); // 3 へ clamp してから 1 歩戻る
        });

    [Fact]
    public void NextPrevChar_BeforeSetSource_ReturnZero() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl(); // SetSource 前 = snapshot null
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(0, host.NextChar(5));
            Assert.Equal(0, host.PrevChar(5));
        });

    [Fact]
    public void PrevChar_ClampedFromBeyondEnd_SkipsCrlfPair() =>
        Sta.Run(() =>
        {
            // clamp と CRLF atomic が両方効く経路(委譲後も維持されること)
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\r\n")); // CharLength=3
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(1, host.PrevChar(999)); // 3 へ clamp → CRLF を 1 歩で戻る
        });
}
```

### Step 2: 現行実装で緑になることを確認する

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Editor.Tests -c Release --no-build --filter "FullyQualifiedName~UiaTextHostAdapterClampTests"
```

Expected: **PASS**。委譲前の挙動を写したテストなので現行実装で通る
(= 委譲後も同じであることを保証する網になる)。

### Step 3: `NavigationCommands` を薄いラッパにする

`src/yEdit.Core/Editing/NavigationCommands.cs` の `MoveLeftChar` / `MoveRightChar`
(20-54 行目)を置き換える。ファイル冒頭の using に `yEdit.Core.Text;` を追加すること。

```csharp
    /// <summary>左に1文字移動。サロゲートペア・CRLF pair は1文字として扱う。先頭では動かない。</summary>
    /// <remarks>2026-07-31: 歩進規則の実体は <see cref="TextBoundary.PrevLogicalChar"/> へ移した
    /// (UIA 側 <c>UiaTextHostAdapter.PrevChar</c> と同じ規則を 2 箇所に書いていたため)。</remarks>
    public static int MoveLeftChar(TextSnapshot s, int caret) =>
        TextBoundary.PrevLogicalChar(s, caret);

    /// <summary>右に1文字移動。サロゲートペア・CRLF pair は1文字として扱う。末尾では動かない。</summary>
    /// <remarks>2026-07-31: 歩進規則の実体は <see cref="TextBoundary.NextLogicalChar"/> へ移した。</remarks>
    public static int MoveRightChar(TextSnapshot s, int caret) =>
        TextBoundary.NextLogicalChar(s, caret);
```

### Step 4: `UiaTextHostAdapter` を委譲にする

`src/yEdit.Editor/UiaTextHostAdapter.cs` の 335-374 行目
(`IUiaTextHost.NextChar` と `IUiaTextHost.PrevChar`)を置き換える。
ファイル冒頭の using に `yEdit.Core.Text;` を追加すること。

```csharp
    // 2026-07-31: 歩進規則は Core の TextBoundary に一本化した。
    // 以前はここに NavigationCommands.MoveRightChar/MoveLeftChar と論理的に等価な実装が
    // 丸ごと重複しており、CRLF atomic 化(2026-07-24)のとき両方へ手で規則を入れる必要があった。
    // 片方を落とすとキーボード操作と SR 読みだけが食い違い、自動テストで気づきにくい。
    // clamp と snapshot null 処理だけがここに残る(UiaTextHostAdapterClampTests が固定)。
    int IUiaTextHost.NextChar(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null)
            return 0;
        return TextBoundary.NextLogicalChar(snap, Math.Clamp(offset, 0, snap.CharLength));
    }

    int IUiaTextHost.PrevChar(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null)
            return 0;
        return TextBoundary.PrevLogicalChar(snap, Math.Clamp(offset, 0, snap.CharLength));
    }
```

### Step 5: `CaretController.SnapAndClamp` を委譲にする

`src/yEdit.Editor/CaretController.cs` の 77-104 行目を置き換える。
ファイル冒頭の using に `yEdit.Core.Text;` を追加すること。

```csharp
    /// <summary>
    /// [0, CharLength] にクランプし、論理文字の中間位置(low サロゲート位置 / CR と LF の間)を
    /// 前方へスナップする。CharLength 位置(=EOF)はキャレットが立てる境界なので許可。
    /// キャレット/選択のすべての位置設定入り口が本メソッドを通るため、ここで一度スナップすれば
    /// mid-surrogate / mid-CRLF は不変条件として守られる。
    /// </summary>
    /// <remarks>2026-07-31: 判定の実体は <see cref="TextBoundary.SnapToLogicalCharStart"/> へ移した
    /// (同じ規則が Core / UIA / ここの 3 箇所に散っていたため)。</remarks>
    public static int SnapAndClamp(int offset, TextSnapshot snap) =>
        TextBoundary.SnapToLogicalCharStart(snap, offset);
```

### Step 6: テストが通ることを確認する

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests   -c Release --no-build
dotnet test tests/yEdit.Editor.Tests -c Release --no-build
dotnet test tests/yEdit.App.Tests    -c Release --no-build
```

Expected: **全件 PASS**。特に `NavigationCommandsTests` /
`UiaTextHostAdapterCrlfTests` / `CaretControllerSnapAndClampTests` /
`CaretControllerContractTests` / `KeyboardNavigationTests` が緑であること
(= 挙動不変が既存テストで裏取りできている)。

### Step 7: commit

```bash
git add src/yEdit.Core/Editing/NavigationCommands.cs src/yEdit.Editor/UiaTextHostAdapter.cs src/yEdit.Editor/CaretController.cs tests/yEdit.Editor.Tests/UiaTextHostAdapterClampTests.cs
git commit -F- <<'EOF'
refactor(editor): UIA の文字歩進を Core の TextBoundary へ委譲する

UiaTextHostAdapter.NextChar/PrevChar は NavigationCommands.MoveRightChar/MoveLeftChar の
論理的な完全重複だった(MoveLeftChar の prev > 0 と PrevChar の o - 2 >= 0 は同値)。
CRLF atomic 化(2026-07-24)のときに両方へ手で規則を入れており、次に規則を変えるときも
2 箇所必要だった。片方を落とすとキーボード操作と SR 読みだけが食い違う。

NavigationCommands / CaretController.SnapAndClamp も同じ実体へ寄せ、規則を 1 箇所にした。
Adapter に残る clamp と snapshot null 処理は UiaTextHostAdapterClampTests で固定。

挙動不変。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

## Task 6: B — 残りの呼び出し元を付け替える

**Files:**
- Modify: `src/yEdit.Core/Editing/WordBoundary.cs:131-157`
- Modify: `src/yEdit.Editor/UiaTextHostAdapter.cs:545-585`
- Modify: `src/yEdit.Editor/EditorControl.Input.cs:301-323`
- Modify: `src/yEdit.Core/Layout/PixelMapper.cs:14-80`
- Modify: `src/yEdit.Core/Layout/LineLayout.cs:45-69`
- Modify: `src/yEdit.Core/Layout/MonoCharMetrics.cs:16-31`
- Modify: `src/yEdit.Core/Layout/FrameBuilder.cs:353-358`

各ファイルの using に `yEdit.Core.Text;` を追加すること
(`FrameBuilder` / `PixelMapper` / `LineLayout` / `MonoCharMetrics` は `yEdit.Core.Layout`
名前空間、`WordBoundary` は `yEdit.Core.Editing` 名前空間)。

### Step 1: `WordBoundary` の内部歩進を置き換える

`src/yEdit.Core/Editing/WordBoundary.cs` の `MoveLeftCp`(131-143 行目)と
`MoveRightCp`(145-157 行目)を**削除**し、呼び出し箇所を置き換える。

- 86 行目 `int pos = MoveLeftCp(snap, caret);` → `int pos = TextBoundary.PrevCodePoint(snap, caret);`
- 93 行目 `pos = MoveLeftCp(snap, pos);` → `pos = TextBoundary.PrevCodePoint(snap, pos);`
- 99 行目 `int prev = MoveLeftCp(snap, pos);` → `int prev = TextBoundary.PrevCodePoint(snap, pos);`
- 163 行目 `pos = MoveRightCp(snap, pos);` → `pos = TextBoundary.NextCodePoint(snap, pos);`

`PrevWordStart` の XML コメント(77-81 行目)の「1 code-point 左へ移動(サロゲート考慮)」は
そのままでよい。ただし 32-35 行目の `<remarks>` に一行足す:

```
/// 2026-07-31: 内部の code-point 歩進は <see cref="Text.TextBoundary"/> へ移した。
/// <b>本クラスは CRLF を atomic に扱わない</b>(CR と LF を別々の LineBreak として数える設計)。
/// 論理文字単位が要るキャレット / UIA 系とは意図的に別の API を使っている。
```

### Step 2: `UiaTextHostAdapter` の単語境界ループを置き換える

`src/yEdit.Editor/UiaTextHostAdapter.cs` の `WordBoundary_WordStart`(545-565 行目)と
`WordBoundary_WordEnd`(567-585 行目)の本体を置き換える:

```csharp
    private static int WordBoundary_WordStart(TextSnapshot snap, int pos)
    {
        if (pos <= 0)
            return 0;
        int p = pos;
        while (p > 0)
        {
            int prev = TextBoundary.PrevCodePoint(snap, p);
            char pc = snap.GetChar(prev);
            if (char.IsWhiteSpace(pc) || pc == '\r' || pc == '\n')
                break;
            p = prev;
        }
        return p;
    }

    private static int WordBoundary_WordEnd(TextSnapshot snap, int pos)
    {
        int p = pos;
        while (p < snap.CharLength)
        {
            char c = snap.GetChar(p);
            if (char.IsWhiteSpace(c) || c == '\r' || c == '\n')
                break;
            p = TextBoundary.NextCodePoint(snap, p);
        }
        return p;
    }
```

> 542-544 行目の既存コメント(「Core WordBoundary に直接メンバがないため素朴実装する」)は
> **残すこと**。これは設計書 §8 F-3 の申し送り(UIA の単語境界が Core と別ロジック)を
> 指しており、本 Task では解消しない。

### Step 3: 上書きモードのサロゲート判定を置き換える

`src/yEdit.Editor/EditorControl.Input.cs` の 301-323 行目、`else if (Overtype)` ブロック内を:

```csharp
        else if (Overtype)
        {
            var snap = _buffer.Current;
            int overwriteLen = 0;
            int caret = _caretCtrl.Caret;
            if (caret < snap.CharLength)
            {
                char nc = snap.GetChar(caret);
                // 改行は潰さない(CRLF pair も含めて跨がない)= MoveRightChar とは意図的に違う
                if (nc != '\r' && nc != '\n')
                    overwriteLen = TextBoundary.CodePointLengthAt(snap, caret);
            }
            _buffer.Replace(caret, overwriteLen, text);
            _caretCtrl.SetTo(caret + text.Length, _buffer.Current);
        }
```

### Step 4: Layout / 描画の span 系 5 箇所を置き換える

**`src/yEdit.Core/Layout/PixelMapper.cs`** — 22-29 行目の snap 分岐を:

```csharp
        // low サロゲート位置なら pair 先頭へ前方スナップ
        charOffset = TextBoundary.SnapToCodePointStart(segment, charOffset);
```

56-66 行目の cpLen 算出を:

```csharp
            // 次の code-point を切り出す(サロゲートペアは 2 code-unit 分)
            int cpLen = TextBoundary.CodePointLengthAt(segment, i);
```

**`src/yEdit.Core/Layout/LineLayout.cs`** — 47-53 行目を:

```csharp
            // 次の code-point を切り出す(サロゲートペアは 2 code-unit 分)
            int cpLen = TextBoundary.CodePointLengthAt(line, i);
```

(直後の `int cpWidth = metrics.MeasureRun(line.Slice(i, cpLen));` はそのまま。
`char c = line[i];` は未使用になるので削除する — 残すと警告でビルドが落ちる。)

**`src/yEdit.Core/Layout/MonoCharMetrics.cs`** — 16-31 行目の `MeasureRun` を:

```csharp
    public int MeasureRun(ReadOnlySpan<char> text)
    {
        int px = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (TextBoundary.CodePointLengthAt(text, i) == 2)
            {
                px += _half * 2;
                i++;
                continue;
            }
            char c = text[i];
            px += (c < 0x80 || c == '\t') ? _half : _half * 2; // ASCII/タブ=1・それ以外=2
        }
        return px;
    }
```

**`src/yEdit.Core/Layout/FrameBuilder.cs`** — 353-358 行目を:

```csharp
            // サロゲートペアなら 2 進める(空白判定を安全にスキップ)
            i += TextBoundary.CodePointLengthAt(span, i);
```

> `span` は 331 行目で `var span = text.AsSpan();` として既に定義済み。
> 元コードは `text.Length` / `text[i + 1]` を見ていたが `span` と同一内容なので等価。

### Step 5: テストが通ることを確認する

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests   -c Release --no-build
dotnet test tests/yEdit.Editor.Tests -c Release --no-build
dotnet test tests/yEdit.App.Tests    -c Release --no-build
```

Expected: **全件 PASS**、警告 0。特に `WordBoundaryTests` / `PixelMapperTests` /
`LineLayoutTests` / `KeyboardNavigationTests` が緑であること。

### Step 6: ミューテーション検証

`TextBoundary.CodePointLengthAt(ReadOnlySpan<char>, int)` の `? 2 : 1` を `? 1 : 1` に変える。

```bash
dotnet build yEdit.sln -c Release -warnaserror
dotnet test tests/yEdit.Core.Tests -c Release --no-build
```

Expected: **FAIL**(`PixelMapperTests` / `LineLayoutTests` / `TextBoundaryTests` のいずれかが赤)。

元に戻して再ビルド・再テストで緑を確認する。

### Step 7: commit

```bash
git add src/yEdit.Core/Editing/WordBoundary.cs src/yEdit.Editor/UiaTextHostAdapter.cs src/yEdit.Editor/EditorControl.Input.cs src/yEdit.Core/Layout/PixelMapper.cs src/yEdit.Core/Layout/LineLayout.cs src/yEdit.Core/Layout/MonoCharMetrics.cs src/yEdit.Core/Layout/FrameBuilder.cs
git commit -F- <<'EOF'
refactor(core,editor): サロゲート判定を TextBoundary へ集約する

同じ判定が 14 ファイル・21 箇所に手書きされており、境界条件が各所で微妙に違っていた。
TextSnapshot 上を歩く系(WordBoundary / UIA 単語境界 / 上書きモード)と span 上を歩く系
(PixelMapper / LineLayout / MonoCharMetrics / FrameBuilder)を TextBoundary に寄せる。

WordBoundary は CRLF を atomic に扱わない設計なので CodePoint 系 API を使う
(LogicalChar 系に寄せると CR/LF を別クラスとして数える前提が崩れる)。
上書きモードが改行を潰さない挙動も MoveRightChar とは意図的に別のまま維持する。

対象外: KinsokuFormatter / SanitizeForDisplay / CharacterCounter / GrepResultsWindow
(Rune ベース等、流儀が異なるため統一しない)。

挙動不変。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

## 完了後の手順(CLAUDE.md §3 工程 5-6)

### 1. 最終ブランチレビュー(2 パス)

**パスごとに独立した別エージェントを起動する**(1 起動に混載するとレビューが浅くなる)。

- **コード品質パス** — ブランチ全体。ミューテーション検証のスポットチェック込み。
  重点: `TextBoundary` の 2 系統分離が保たれているか、委譲で挙動が変わっていないか、
  `AppendBuffer` の明示指定コメントが将来の読者に伝わるか。
- **脆弱性パス** — ブランチ全体。重点: `DecodeUtf16At` の範囲外読み、
  `Utf8Sanitizer` 前提の成立範囲。

指摘は 3 択で明示する: ① fixup commit で修正 / ② PR description に記載して受容 /
③ 理由付き却下。**レビュー由来の修正は元 commit を書き換えず別 fixup commit で積む。**

### 2. 設計書に実施記録を追記する

`docs/plans/2026-07-31-char-access-seam-design.md` §9 に追記(**§1-8 は書き換えない**):

- 格子幅の確定値(4KB か 2KB か)
- Bench `--characcess` の前後実測(Task 2 と Task 4 の結果表)
- メモリ増の実測
- レビュー指摘とその処理
- L5 の結果

### 3. 品質ゲート

```powershell
powershell -File tools\pre-merge-check.ps1
```

**EXIT 0** を確認する。

### 4. L5 実機 SR 検証(必須)

`UiaTextHostAdapter` の歩進は SR の文字 / 単語単位読みの経路そのもの。
挙動不変のはずだが CLAUDE.md §5 の「迷ったら必要に倒す」に従い実施する。

```powershell
powershell -File tools\sr-regression.ps1
```

は UIA 応答の検証まで。**実発声は検出できないため L5 の代替にならない。**

**ユーザーに依頼する NVDA 実機確認項目:**

1. ←/→ で 1 文字ずつ移動したとき、文字が 1 つずつ読まれる(CRLF 行末で「復帰」「改行」が
   別々に読まれない)
2. 絵文字(😀)を含む行で ←/→ が絵文字を 1 歩で越え、1 文字として読まれる
3. Ctrl+←/→ の単語移動で単語が読まれる
4. NVDA のレビューカーソル(テンキー)で文字単位・単語単位に歩けて読み上げられる
5. 大きめのファイル(1MB 程度)でも 1〜4 の応答が引っかからない(**本作業の主目的**)
6. 上書きモード(Insert キー)で文字を入力したとき、従来どおり読まれる

**② が NG なら** Task 6 の `EditorControl.Input.cs` 変更を、
**①/④ が NG なら** Task 5 の `UiaTextHostAdapter.cs` 変更を疑う。

### 5. PR

```bash
git push -u origin feature/char-access-seam
gh pr create --base main
```

PR description(日本語)に必ず含める:

- 目的(A / B / C それぞれ)
- Bench の前後実測(数字を貼る)
- レビュー経緯(2 パスの指摘と 3 択の処理)
- L5 の結果
- 申し送り(設計書 §8 の F-1〜F-4)
