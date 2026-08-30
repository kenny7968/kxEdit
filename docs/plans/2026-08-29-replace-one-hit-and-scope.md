# 単発置換が「現ヒット」と「許容範囲」を取り違える 実装計画(A-14 / T-3)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 単発の「置換」が、CRLF 文書で別の出現を置換してしまう問題(A-14)と、
「選択範囲のみ」ON でも範囲外を置換してしまう問題(T-3)を直す。

**Architecture:** 原因は `SearchController.ReplaceOne` が「置換すべきヒット」と
「置換してよい範囲」をどちらも保持せず、エディタの選択範囲から再導出していること。
選択は CRLF / サロゲートを 1 論理文字として扱うためスナップされるので、実ヒットと必ずずれる。
(1) `Find` が選んだヒット本体をスナップショット世代付きで保持し、(2) 巻き込みを復元する
厳密置換 API を Editor に足し、(3) `ReplaceAll` のスコープ検証を `ReplaceOne` にも適用する。

**Tech Stack:** C# / .NET 9 / WinForms / xUnit。設計書は
`docs/plans/2026-08-29-replace-one-hit-and-scope-design.md`(commit `0912d0a`)。

---

## 前提知識(この計画を実行する人へ)

このリポジトリを初めて触る前提で、必要な背景をここにまとめる。

### レイヤ構成

| プロジェクト | 役割 | テスト |
|---|---|---|
| `src/kxEdit.Core` | テキストバッファ・検索・境界規則(UI 非依存) | `tests/kxEdit.Core.Tests`(L1) |
| `src/kxEdit.Editor` | 自作エディットコントロール `EditorControl` | `tests/kxEdit.Editor.Tests`(L2) |
| `src/kxEdit.App` | ダイアログ・コントローラ | `tests/kxEdit.App.Tests`(L3) |

### 知っておくべき不変条件

- **CRLF とサロゲートペアは「1 論理文字」**。キャレットと選択はその内側に立てない。
  規則の唯一の定義は `src/kxEdit.Core/Text/TextBoundary.cs`。
  `CaretController.SnapAndClamp` = `TextBoundary.SnapToLogicalCharStart` が入口。
- **検索(`SnapshotSearcher`)は生の UTF-16 オフセットで照合する。** 正規表現 `\n` は
  CRLF 文書の LF だけにヒットする。つまり**検索結果は選択で表現できないことがある**。
- **`TextSnapshot` は編集のたびに新インスタンスになる**が、キャレット・選択の移動では変わらない。
  この参照同一性が「文書が編集されたか」の判定に使われている(`SearchController._selectionScope`)。
- 単独の CR も改行として数えられる(`TextChunk` の `BreaksTo` 規約)。
  したがって `abc\rXdef` は正当な 2 行テキストである。

### ビルド・テストコマンド

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests   -c Release --no-build
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests    -c Release --no-build
```

単一テストだけ走らせるとき:

```bash
dotnet test tests/kxEdit.Core.Tests -c Release --filter "FullyQualifiedName~SnapToLogicalCharEnd"
```

**0 warning を維持すること**(`-warnaserror` が効いている)。
commit 時に pre-commit フック(CSharpier 整形 + ローカルパス検出)が走る。`--no-verify` で飛ばさない。

### この計画の作業ルール(CLAUDE.md 由来)

- コミットメッセージ本文・コメントは**日本語**。識別子は英語。
- Task 2 は後続タスクが依存する新 API を入れるため、**その場でコード品質レビュー**を行う(§3-4)。
- 全 Task 完了後に**最終ブランチレビュー 2 パス**(コード品質 / 脆弱性)を
  **別々のエージェント**で起動する(1 回に混載しない)。
- ブランチは `feature/replace-one-hit-and-scope`(作成済み)。

---

## Task 1: `TextBoundary.SnapToLogicalCharEnd`

論理文字の中間位置を**後方(pair 終端)へ**スナップする関数を足す。
既存の `SnapToLogicalCharStart` は前方へスナップするので、範囲の終端に使うと範囲が狭まり、
置換したい文字が範囲から落ちる。対になる「外側へ広げる」版が要る。

**Files:**
- Modify: `src/kxEdit.Core/Text/TextBoundary.cs`(末尾 `SnapToLogicalCharStart` の直後)
- Test: `tests/kxEdit.Core.Tests/Text/TextBoundaryTests.cs`(末尾に追記)

**Step 1: 失敗するテストを書く**

`tests/kxEdit.Core.Tests/Text/TextBoundaryTests.cs` の末尾(クラスの閉じ括弧の直前)に追記:

```csharp
    // ===== SnapToLogicalCharEnd: 論理文字の中間位置を後方(pair 終端)へ寄せる =====

    [Fact]
    public void SnapToLogicalCharEnd_MidCrlf_SnapsForwardToLf()
    {
        // "a\r\nb": 2 は CR と LF の間=論理文字の中間。Start は 1 へ、End は 3 へ寄せる。
        var s = Snap("a\r\nb");
        Assert.Equal(3, TextBoundary.SnapToLogicalCharEnd(s, 2));
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart(s, 2)); // 対であることを同じ fixture で示す
    }

    [Fact]
    public void SnapToLogicalCharEnd_MidSurrogatePair_SnapsForwardToLowEnd()
    {
        // "a😀b": 2 は low サロゲート位置=論理文字の中間。
        var s = Snap("a😀b");
        Assert.Equal(3, TextBoundary.SnapToLogicalCharEnd(s, 2));
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart(s, 2));
    }

    [Fact]
    public void SnapToLogicalCharEnd_OnBoundary_IsIdentity()
    {
        // 境界上は動かさない(no-change の検証は非既定位置=文書先頭でも末尾でもない 1 と 3 から始める)
        var s = Snap("a\r\nb"); // CharLength=4
        Assert.Equal(1, TextBoundary.SnapToLogicalCharEnd(s, 1)); // CR の前
        Assert.Equal(3, TextBoundary.SnapToLogicalCharEnd(s, 3)); // LF の後
    }

    [Fact]
    public void SnapToLogicalCharEnd_ClampsBothEnds()
    {
        var s = Snap("a\r\nb"); // CharLength=4
        Assert.Equal(0, TextBoundary.SnapToLogicalCharEnd(s, -5));
        Assert.Equal(0, TextBoundary.SnapToLogicalCharEnd(s, 0));
        Assert.Equal(4, TextBoundary.SnapToLogicalCharEnd(s, 4));
        Assert.Equal(4, TextBoundary.SnapToLogicalCharEnd(s, 99));
    }

    [Fact]
    public void SnapToLogicalCharEnd_LoneCrAndLoneLf_AreNotPairs()
    {
        // 単独 CR / 単独 LF は pair を作らない=どの位置も恒等。
        // "\r\n" 判定を「\n なら常に後退」へ弱めた変異をここで殺す。
        Assert.Equal(1, TextBoundary.SnapToLogicalCharEnd(Snap("a\rb"), 1));
        Assert.Equal(1, TextBoundary.SnapToLogicalCharEnd(Snap("a\nb"), 1));
        Assert.Equal(2, TextBoundary.SnapToLogicalCharEnd(Snap("a\nb"), 2));
    }
```

**Step 2: 失敗を確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
```

Expected: FAIL —— `error CS0117: 'TextBoundary' に 'SnapToLogicalCharEnd' の定義がありません`

> **注意**: このリポジトリでは Sonar アナライザが `error S####` を出すことがある。
> ビルドログを grep するときは `grep "error CS"` ではなく
> `grep -E " error [A-Z]+[0-9]+"` を使うこと(`error CS` だけ見ると別のエラーを見落として
> 古い DLL でテストが通ったように見える)。

**Step 3: 最小実装**

`src/kxEdit.Core/Text/TextBoundary.cs` の `SnapToLogicalCharStart` の直後(クラス閉じ括弧の直前)に追加:

```csharp
    /// <summary>
    /// [0, CharLength] にクランプし、論理文字の中間位置(low サロゲート位置 / CR と LF の間)を
    /// 後方(pair 終端)へスナップする。<see cref="SnapToLogicalCharStart"/> の対。
    /// </summary>
    /// <remarks>
    /// 範囲の<b>終端</b>に使う。終端に <see cref="SnapToLogicalCharStart"/> を掛けると範囲が
    /// 狭まり、割ってはいけない論理文字ごと範囲から落ちる(例: CRLF の CR にヒットした
    /// <c>[3,4)</c> が <c>[3,3)</c> のゼロ幅に潰れる)。始端に Start・終端に End を掛けると、
    /// 元の範囲を必ず含み、かつ論理文字を割らない最小の範囲になる。
    /// </remarks>
    public static int SnapToLogicalCharEnd(TextSnapshot snap, int pos)
    {
        ArgumentNullException.ThrowIfNull(snap);
        if (pos <= 0)
            return 0;
        if (pos >= snap.CharLength)
            return snap.CharLength;
        // pos > 0 は前段の早期 return で保証済み(述語が pos - 1 を読める条件)
        char c = snap.GetChar(pos);
        if (IsSurrogatePairEndingAt(snap, pos, c) || IsCrlfEndingAt(snap, pos, c))
            return pos + 1;
        return pos;
    }
```

クラス冒頭の XML doc に一覧表(`<item><term>...`)があるので、
`SnapToLogicalCharStart` の行に倣って `SnapToLogicalCharEnd` の行(`クランプ / クランプ`)も足す。

**Step 4: テストが通ることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~TextBoundaryTests"
```

Expected: PASS(既存の `TextBoundaryTests` も全緑のまま)

**Step 5: commit**

```bash
git add src/kxEdit.Core/Text/TextBoundary.cs tests/kxEdit.Core.Tests/Text/TextBoundaryTests.cs
git commit -m "feat(core): 論理文字の中間位置を後方へ寄せる SnapToLogicalCharEnd を足す"
```

---

## Task 2: `EditorControl.ReplaceCharRangeExact`

`ReplaceCharRange` は両端に `SnapAndClamp` を掛けるので、CRLF の LF だけを指した `(4, 1)` を
渡すと `[3, 5)` = CRLF 全体が消える。**現ヒットを正しく特定できても、置換 API がそれを表現できない。**
巻き込んだ分を前後に足し戻す API を足す。

> **後続タスクが依存する新 API なので、このタスクの直後にコード品質レビューを行う**(CLAUDE.md §3-4)。

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.cs`(`ReplaceCharRange` の直後)
- Test: `tests/kxEdit.Editor.Tests/EditorControlReplaceExactTests.cs`(新規)

**Step 1: 失敗するテストを書く**

`tests/kxEdit.Editor.Tests/EditorControlReplaceExactTests.cs` を新規作成。
このテストプロジェクトは `GlobalUsings.cs` で `Xunit` / `System.Windows.Forms` /
`kxEdit.Core.Buffers` / `kxEdit.Editor` を通しているので、using は書かない
(既存 `EditorControlCrlfCaretTests.cs` と同じ形)。

```csharp
namespace kxEdit.Editor.Tests;

/// <summary>
/// A-14(2026-08-29): <see cref="EditorControl.ReplaceCharRangeExact"/> が、両端が論理文字の
/// 内側を指していても巻き込んだ文字を復元することを固定する。
///
/// 既存 <c>ReplaceCharRange</c> は両端をスナップするため CRLF の LF だけを置換できない。
/// 一括置換(<c>ReplaceInRange</c> + 範囲丸ごと差し替え)は両端が境界に乗るので同じ問題を
/// 踏まず、正しい結果を出している。本 API は単発置換をその結果に揃えるために足した。
/// </summary>
public class EditorControlReplaceExactTests
{
    [Fact]
    public void ReplaceCharRangeExact_LfOfCrlf_KeepsCr() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r\ndef"));

            ctrl.ReplaceCharRangeExact(4, 1, "X"); // LF だけを置換

            Assert.Equal("abc\rXdef", ctrl.Text);
        });

    [Fact]
    public void ReplaceCharRangeExact_CrOfCrlf_KeepsLf() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r\ndef"));

            ctrl.ReplaceCharRangeExact(3, 1, "X"); // CR だけを置換

            Assert.Equal("abcX\ndef", ctrl.Text);
        });

    [Fact]
    public void ReplaceCharRangeExact_LowSurrogateOnly_KeepsHigh() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a😀b")); // "a😀b"

            ctrl.ReplaceCharRangeExact(2, 1, "X"); // low サロゲートだけを置換

            Assert.Equal("a\uD83DXb", ctrl.Text);
        });

    [Fact]
    public void ReplaceCharRangeExact_ExistingReplaceCharRange_SwallowsTheWholeCrlf() =>
        Sta.Run(() =>
        {
            // 対照群: 既存 API の契約(巻き込む)が変わっていないことを同じ入力で示す。
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r\ndef"));

            ctrl.ReplaceCharRange(4, 1, "X");

            Assert.Equal("abcXdef", ctrl.Text); // CR ごと消える=既存契約
        });

    [Fact]
    public void ReplaceCharRangeExact_OnLogicalBoundary_MatchesReplaceCharRange() =>
        Sta.Run(() =>
        {
            // 委譲の恒等性: 両端が境界に乗っていれば既存 API と同結果でなければならない。
            using var a = new EditorControl();
            using var b = new EditorControl();
            a.SetSource(TextBuffer.FromString("abc\r\ndef"));
            b.SetSource(TextBuffer.FromString("abc\r\ndef"));

            a.ReplaceCharRangeExact(5, 3, "XY"); // "def" を置換(境界上)
            b.ReplaceCharRange(5, 3, "XY");

            Assert.Equal("abc\r\nXY", a.Text);
            Assert.Equal(a.Text, b.Text);
        });

    [Fact]
    public void ReplaceCharRangeExact_ClampsOutOfRangeArgs() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abcd"));

            ctrl.ReplaceCharRangeExact(-3, 2, "X"); // 始端が負(終端 -3+2 = -1 も負)

            // 両端とも 0 へクランプ = [0,0) の純挿入。既存 ReplaceCharRange と同じ結果。
            Assert.Equal("Xabcd", ctrl.Text);
        });

    [Fact]
    public void ReplaceCharRangeExact_LengthOverflow_DoesNotWrapToNegative() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abcd"));

            ctrl.ReplaceCharRangeExact(2, int.MaxValue, "X"); // start + length が int を溢れる

            Assert.Equal("abX", ctrl.Text); // [2, CharLength) へクランプ(全文置換にならない)
        });

    [Fact]
    public void ReplaceCharRangeExact_IsOneUndoUnit() =>
        Sta.Run(() =>
        {
            // 巻き込み復元を「削除 + 挿入」の 2 手でやると Undo が 2 回必要になる。
            // 委譲によって 1 Undo 単位であることを固定する。
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r\ndef"));

            ctrl.ReplaceCharRangeExact(4, 1, "X");
            ctrl.Undo();

            Assert.Equal("abc\r\ndef", ctrl.Text);
        });
}
```

> `Undo()` は `EditorControl.cs:1415` の public API(確認済み)。

**Step 2: 失敗を確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
```

Expected: FAIL —— `'EditorControl' に 'ReplaceCharRangeExact' の定義がありません`

**Step 3: 最小実装**

`src/kxEdit.Editor/EditorControl.cs` の `ReplaceCharRange` メソッドの直後に追加。
`using kxEdit.Core.Text;` は既にファイル冒頭にあるので追加不要。

```csharp
    /// <summary>
    /// [start, start+length) だけを厳密に置換する。両端が CRLF / サロゲートペアの内側を指していても、
    /// <see cref="ReplaceCharRange"/> のように外側の文字を巻き込んで捨てず、はみ出し分を復元して書き戻す。
    /// </summary>
    /// <remarks>
    /// 検索の単発置換(A-14 / 2026-08-29)がこれを使う。正規表現 <c>\n</c> は CRLF 文書で LF
    /// だけにヒットするが、<see cref="ReplaceCharRange"/> は両端をスナップするので CR ごと消える。
    /// 一括置換(<c>SnapshotSearcher.ReplaceInRange</c> + 範囲丸ごと差し替え)は両端が
    /// 文書の端に乗るため同じ問題を踏まない。本 API は単発置換の結果を一括置換に揃える。
    /// <para>
    /// 実装は外側へ広げた範囲を <see cref="ReplaceCharRange"/> へ<b>委譲する</b>。委譲先の
    /// 再スナップは <c>s</c> / <c>e</c> が既に論理文字境界にあるため恒等であり、編集の副作用
    /// (<c>AfterEdit</c> / キャレット規約 / Undo 単位 / UIA イベント)は 1 箇所に保たれる。
    /// </para>
    /// <para>
    /// <b>IME 未確定の取消はスナップショットを読む前に行うこと。</b>
    /// <c>CancelCompositionAndDefault</c> はバッファを書き換えるので、順序を入れ替えると
    /// 取消前のスナップショットで境界を計算して別の位置を置換する。
    /// </para>
    /// </remarks>
    public void ReplaceCharRangeExact(int start, int length, string replacement)
    {
        if (IsComposing)
            CancelCompositionAndDefault();
        if (_buffer is null || ReadOnly)
            return;
        ArgumentNullException.ThrowIfNull(replacement);
        var snap = _buffer.Current;
        int s0 = Math.Clamp(start, 0, snap.CharLength);
        // start + length は int 加算だとオーバーフローで負値になり得るため long 経由
        // (ReplaceCharRange / EnsureVisibleCharRange と同じ流儀)。
        long endLong = (long)start + Math.Max(0, length);
        int e0 = (int)Math.Clamp(endLong, s0, (long)snap.CharLength);
        int s = TextBoundary.SnapToLogicalCharStart(snap, s0); // 外側へ(後退)
        int e = TextBoundary.SnapToLogicalCharEnd(snap, e0); // 外側へ(前進)
        string text =
            s == s0 && e == e0
                ? replacement
                : snap.GetText(s, s0 - s) + replacement + snap.GetText(e0, e - e0);
        ReplaceCharRange(s, e - s, text);
    }
```

**Step 4: テストが通ることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
```

Expected: PASS(新規 8 件 + 既存全件)

**Step 5: commit**

```bash
git add src/kxEdit.Editor/EditorControl.cs tests/kxEdit.Editor.Tests/EditorControlReplaceExactTests.cs
git commit -m "feat(editor): 論理文字の巻き込みを復元する ReplaceCharRangeExact を足す"
```

**Step 6: コード品質レビュー(CLAUDE.md §3-4 の前倒し)**

後続タスクが依存する新 API なので、ここで別エージェントのコード品質レビューを行う。
観点: 委譲の恒等性の根拠が成立しているか / `ReplaceCharRange` との契約差が doc で明確か /
IME・ReadOnly・クランプの前後関係が既存 API と一致しているか。
指摘は CLAUDE.md §4 の 3 択(fixup / 受容 / 却下)で処理し、修正は**別 fixup commit** で積む。

---

## Task 3: 現ヒットを世代付きで保持する(A-14 の主修正)

`_lastHit` を「ヒット本体 + 適用された選択 + スナップショット世代」に拡張し、
`Find` と `ReplaceOne` が選択からの再導出をやめる。

**Files:**
- Modify: `src/kxEdit.App/SearchController.cs`
- Test: `tests/kxEdit.App.Tests/SearchControllerTests.cs`

**Step 1: 失敗するテストを書く**

`tests/kxEdit.App.Tests/SearchControllerTests.cs` の `ReplaceOne_*` 群の直後に追記。
テストホストは実 `EditorControl` を使うので CRLF スナップは本物が走る。
`Editor.Text` セッターは `TextBuffer.FromString` なので EOL を正規化しない。

```csharp
    // ===== A-14: CRLF 文書で現ヒットを取り違えない =====

    [Fact]
    public void ReplaceOne_RegexLfInCrlfDocument_ReplacesTheSelectedHit() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // 2 行目以降を持たせて「次の出現を置換した」と「その位置を置換した」を弁別する。
            var doc = host.NewDoc("abc\r\ndef\r\nghi");
            host.View.Pattern = @"\n";
            host.View.Replacement = "X";
            host.View.UseRegex = true;
            host.Search.OpenReplace();
            host.Search.FindNext(); // 1 つ目の LF(index 4)にヒット=選択は [3,5) にスナップされる

            host.Search.ReplaceOne();

            // 修正前は ReplacementAt が外れて FindNext(5) に落ち、2 つ目の LF を置換して
            // "abc\r\ndef\rXghi" になっていた。
            Assert.Equal("abc\rXdef\r\nghi", doc.Editor.Text);
        });

    [Fact]
    public void ReplaceOne_RegexLfInCrlfDocument_MatchesReplaceAllResult() =>
        Sta.Run(() =>
        {
            // 単発を一括に揃える=同じ 1 件だけの文書で両者の結果が一致すること。
            using var one = new Host();
            using var all = new Host();
            var docOne = one.NewDoc("abc\r\ndef");
            var docAll = all.NewDoc("abc\r\ndef");
            foreach (var h in new[] { one, all })
            {
                h.View.Pattern = @"\n";
                h.View.Replacement = "X";
                h.View.UseRegex = true;
                h.Search.OpenReplace();
            }

            one.Search.FindNext();
            one.Search.ReplaceOne();
            all.Search.ReplaceAll();

            Assert.Equal(docAll.Editor.Text, docOne.Editor.Text);
            Assert.Equal("abc\rXdef", docOne.Editor.Text);
        });

    [Fact]
    public void FindNext_RegexCrInCrlfDocument_AdvancesToNextHit() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc\r\ndef\r\nghi");
            host.View.Pattern = @"\r";
            host.View.Replacement = "";
            host.View.UseRegex = true;
            host.Search.OpenFind();

            Assert.True(host.Search.FindNext()); // 1 つ目の CR(index 3)。選択は [3,3) に潰れる
            Assert.True(host.Search.FindNext()); // 修正前はここが同じ位置に留まっていた

            // 2 つ目の CR(index 8)へ進んだ = 選択の始端が 8 になっている
            Assert.Equal(8, doc.Editor.GetSelectionCharRange().Start);
        });

    [Fact]
    public void ReplaceOne_RegexCrInCrlfDocument_KeepsTheLf() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc\r\ndef");
            host.View.Pattern = @"\r";
            host.View.Replacement = "X";
            host.View.UseRegex = true;
            host.Search.OpenReplace();
            host.Search.FindNext();

            host.Search.ReplaceOne();

            Assert.Equal("abcX\ndef", doc.Editor.Text); // LF を巻き込まない
        });

    [Fact]
    public void ReplaceOne_AfterUserMovesSelection_FallsBackToSearchFromCaret() =>
        Sta.Run(() =>
        {
            // 現ヒットが「生きていない」ときは従来経路(次を検索して即置換)のままであること。
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.Search.OpenReplace();
            host.Search.FindNext(); // (0,3) を選択

            doc.Editor.SetCaretCharOffset(4); // ユーザーが選択を動かした=現ヒットは無効

            host.Search.ReplaceOne();

            Assert.Equal("abc X abc", doc.Editor.Text); // キャレット以降の最初のヒットを置換
        });

    [Fact]
    public void ReplaceOne_AfterExternalEdit_DoesNotReuseStaleHit() =>
        Sta.Run(() =>
        {
            // スナップショット世代が変われば現ヒットは死ぬ(位置は同じ数値でも中身が違う)。
            using var host = new Host();
            var doc = host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.Search.OpenReplace();
            host.Search.FindNext(); // (0,3)

            doc.Editor.ReplaceCharRange(0, 0, "QQQQ"); // 先頭へ挿入。選択も (4,7) へ動く
            host.Search.ReplaceOne();

            Assert.Equal("QQQQX abc", doc.Editor.Text); // ずれた (0,3) を使っていない
        });
```

**Step 2: 失敗を確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~SearchControllerTests"
```

Expected: FAIL —— 少なくとも
`ReplaceOne_RegexLfInCrlfDocument_ReplacesTheSelectedHit` /
`ReplaceOne_RegexLfInCrlfDocument_MatchesReplaceAllResult` /
`FindNext_RegexCrInCrlfDocument_AdvancesToNextHit` /
`ReplaceOne_RegexCrInCrlfDocument_KeepsTheLf` の 4 件が赤。

**この 4 件が赤であることを実測で確認してから次へ進む**(赤くならないテストは網になっていない)。

**Step 3: `_lastHit` を世代付きにする**

`src/kxEdit.App/SearchController.cs` のフィールド宣言(現状 21 行目付近)を差し替える。

変更前:

```csharp
    private MatchSpan? _lastHit; // 直前に選択したヒット（ゼロ幅でも前進できるよう歩進に使う）
```

変更後:

```csharp
    // 直前に選択したヒット。3 つ組で持つ理由:
    //   Hit             = 照合が返した生の UTF-16 範囲。置換はこれを対象にする。
    //   SelStart/SelEnd = それを SelectCharRange した「結果」を読み戻した値。
    //   Snap            = 捕捉時のスナップショット(参照同一性で文書の編集を検出する)。
    // A-14(2026-08-29): 選択は CRLF / サロゲートを 1 論理文字として扱うため
    // (TextBoundary.SnapToLogicalCharStart)、Hit と実選択は一致しないことがある。
    // 例: CRLF 文書の \n ヒット (4,1) の実選択は [3,5)、\r ヒット (3,1) の実選択は [3,3) のゼロ幅。
    // ゆえに「現ヒットが生きているか」を Hit と選択の直接比較で判定してはならない。
    // 読み戻しにしているのは、スナップ規則を App 層へ複製しないため(規則が変わっても追随する)。
    // Snap を弱参照にする理由は _selectionScope と同じ(判定は変わらず、旧ピース木をピン留めしない)。
    private (WeakReference<TextSnapshot> Snap, MatchSpan Hit, int SelStart, int SelEnd)? _lastHit;
```

`ActiveDocumentChanged` 内の `_lastHit = null;` はそのままでよい。

**Step 4: ヘルパー 2 本を足す**

`private static WeakReference<TextSnapshot> Weak(...)` の直前あたりに追加:

```csharp
    /// <summary>直前ヒットを選択して <see cref="_lastHit"/> を更新する。
    /// 選択の<b>結果</b>を読み戻すことで、CRLF / サロゲートのスナップ規則を App 層に複製しない。</summary>
    private void SelectHit(EditorControl ed, MatchSpan hit)
    {
        ed.SelectCharRange(hit.Start, hit.Length);
        var (s, e) = ed.GetSelectionCharRange();
        _lastHit = (Weak(ed.CurrentBuffer.Current), hit, s, e);
    }

    /// <summary>「いま画面で選ばれているヒット」を返す(無ければ null)。
    /// 文書が編集されていない(スナップショット参照が同一)かつ ユーザーが選択を動かしていない
    /// (選択が捕捉時の読み戻し値と一致)ときだけ生きている。</summary>
    private MatchSpan? LiveHit(TextSnapshot snap, int selStart, int selEnd)
    {
        if (_lastHit is not { } h)
            return null;
        if (!h.Snap.TryGetTarget(out var captured) || !ReferenceEquals(captured, snap))
            return null;
        return selStart == h.SelStart && selEnd == h.SelEnd ? h.Hit : null;
    }
```

**Step 5: `Find` を書き換える**

`Find` の `try` ブロック内、`MatchSpan? hit;` から `_lastHit = hit;` までを差し替える。

変更前:

```csharp
            MatchSpan? hit;
            if (forward)
            {
                int from =
                    (_lastHit is { } h && selStart == h.Start && selEnd == h.End)
                        ? h.Start + Math.Max(1, h.Length) // 直前ヒットの次へ（ゼロ幅でも前進）
                        : selEnd;
                hit = searcher.FindNext(snap, from);
            }
            else
            {
                // 三項簡約: _lastHit 一致条件下で selStart == h.Start が成立するため両分岐同値。
                // Forward 側の `h.Start + Math.Max(1, h.Length)` はゼロ幅前進の意味があり温存。
                int before = selStart;
                hit = searcher.FindPrev(snap, before);
            }

            if (hit is null)
            {
                _lastHit = null;
                Announce("これ以上見つかりません");
                return false;
            }

            ed.SelectCharRange(hit.Value.Start, hit.Value.Length);
            _lastHit = hit;
```

変更後:

```csharp
            var live = LiveHit(snap, selStart, selEnd);
            MatchSpan? hit;
            if (forward)
            {
                int from =
                    live is { } h
                        ? h.Start + Math.Max(1, h.Length) // 直前ヒットの次へ（ゼロ幅でも前進）
                        : selEnd;
                hit = searcher.FindNext(snap, from);
            }
            else
            {
                // 現ヒットがあればその始端より前を探す。スナップで選択の始端がヒットより
                // 手前へ寄ることがある(CRLF の LF ヒット)ため、selStart のままだと
                // [selStart, Hit.Start) 内のヒットを取りこぼす。スナップが起きない
                // ケースでは h.Start == selStart なので挙動不変。
                int before = live is { } h2 ? h2.Start : selStart;
                hit = searcher.FindPrev(snap, before);
            }

            if (hit is null)
            {
                _lastHit = null;
                Announce("これ以上見つかりません");
                return false;
            }

            SelectHit(ed, hit.Value);
```

**Step 6: `ReplaceOne` の現ヒット判定を書き換える**

`ReplaceOne` の `try` ブロック内、`var (selStart, selEnd) = ...` から
`ed.ReplaceCharRange(span.Start, span.Length, repl);` までを差し替える。
(スコープ制約は Task 4 で足すので、ここではまだ入れない。)

変更前:

```csharp
            var (selStart, selEnd) = ed.GetSelectionCharRange();
            var span = new MatchSpan(selStart, selEnd - selStart);
            string? repl =
                selEnd > selStart ? searcher.ReplacementAt(snap, span, d.Replacement) : null;

            // G-3 修正: 現ヒット未選択なら次を検索してそのまま即置換する(VSCode 準拠)。
            // 未ヒットの前進先が見つからない場合は Find と同じ「これ以上見つかりません」で終了。
            if (repl is null)
            {
                var next0 = searcher.FindNext(snap, selEnd);
                if (next0 is null)
                {
                    Announce("これ以上見つかりません");
                    return;
                }
                var replCand = searcher.ReplacementAt(snap, next0.Value, d.Replacement);
                // ここは通常到達しない(直前の FindNext ヒットに対して同一 snap/searcher で
                // ReplacementAt が null を返すのは異常系)。防御としてユーザーへ明示する。
                if (replCand is null)
                {
                    Announce("置換できません");
                    return;
                }
                span = next0.Value;
                repl = replCand;
            }

            ed.ReplaceCharRange(span.Start, span.Length, repl);
```

変更後:

```csharp
            var (selStart, selEnd) = ed.GetSelectionCharRange();

            // A-14: 置換対象を選択範囲から再導出しない。Find が選んだヒット本体を使う。
            // 選択から作った MatchSpan は CRLF / サロゲートのスナップで実ヒットとずれ、
            // ReplacementAt が外れて「次の出現」を置換していた。
            MatchSpan span;
            string repl;
            if (
                LiveHit(snap, selStart, selEnd) is { } hit
                && searcher.ReplacementAt(snap, hit, d.Replacement) is { } liveRepl
            )
            {
                span = hit;
                repl = liveRepl;
            }
            else
            {
                // 現ヒットが無い(まだ検索していない / ユーザーが選択を動かした)。
                // G-3: 次を検索してそのまま即置換する(VSCode 準拠)。
                // 前進先が無い場合は Find と同じ「これ以上見つかりません」で終了。
                var next0 = searcher.FindNext(snap, selEnd);
                if (next0 is null)
                {
                    Announce("これ以上見つかりません");
                    return;
                }
                var replCand = searcher.ReplacementAt(snap, next0.Value, d.Replacement);
                // ここは通常到達しない(直前の FindNext ヒットに対して同一 snap/searcher で
                // ReplacementAt が null を返すのは異常系)。防御としてユーザーへ明示する。
                if (replCand is null)
                {
                    Announce("置換できません");
                    return;
                }
                span = next0.Value;
                repl = replCand;
            }

            // 論理文字の内側を指すヒット(CRLF の LF だけ等)でも巻き込みを復元する(Task 2)。
            ed.ReplaceCharRangeExact(span.Start, span.Length, repl);
```

さらに `ReplaceOne` 末尾の次ヒット選択を `SelectHit` に置き換える。

変更前:

```csharp
            ed.SelectCharRange(next.Value.Start, next.Value.Length);
            _lastHit = next;
```

変更後:

```csharp
            SelectHit(ed, next.Value);
```

**Step 7: テストが通ることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

Expected: PASS。**既存の `ReplaceOne_*` / `FindNext_*` / `ReplaceAll_*` が全緑のままであること**を
必ず確認する(特に `ReplaceOne_EmptyReplacement_DoesNotSkipAdjacentHit` と
`ReplaceAll_CapturedScope_SurvivesFindMoves`)。

**Step 8: commit**

```bash
git add src/kxEdit.App/SearchController.cs tests/kxEdit.App.Tests/SearchControllerTests.cs
git commit -m "fix(app): 単発置換が CRLF ヒットで別の出現を置換する問題を直す(A-14)"
```

**Step 9: 仕様レビュー**

実装とテストが設計書 §2.1 / §2.2 どおりかを別エージェントで確認する。
指摘を反映してから Task 4 へ進む。

---

## Task 4: 置換操作を選択範囲に閉じる(T-3)

`ReplaceAll` のスコープ検証をヘルパーへ括り出し、`ReplaceOne` にも適用する。

**Files:**
- Modify: `src/kxEdit.App/SearchController.cs`
- Test: `tests/kxEdit.App.Tests/SearchControllerTests.cs`

**Step 1: 失敗するテストを書く**

`ReplaceAll_InSelection_*` 群の直後に追記。fixture は前後に非ヒット部を持たせ、
全選択と部分選択を弁別できる形にする(CLAUDE.md §4-B)。

```csharp
    // ===== T-3: 「選択範囲のみ」を単発置換にも効かせる =====

    [Fact]
    public void ReplaceOne_InSelection_DoesNotReplaceOutsideScope() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // prefix "abc " と suffix " abc" の両方を除外できる fixture(全選択との区別)。
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3); // 中央の "abc" だけを捕捉
            host.Search.OnInSelectionToggled(true);
            doc.Editor.SetCaretCharOffset(8); // キャレットをスコープの外(3 件目の先頭)へ

            host.Search.ReplaceOne();

            // 修正前は 3 件目が置換され "abc abc X" + 成功発声になっていた。
            Assert.Equal("abc abc abc", doc.Editor.Text);
            Assert.Equal("これ以上見つかりません", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceOne_InSelection_ReplacesInsideScope() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3);
            host.Search.OnInSelectionToggled(true);

            host.Search.ReplaceOne();

            Assert.Equal("abc X abc", doc.Editor.Text); // 前後の 2 件は残る
        });

    [Fact]
    public void ReplaceOne_InSelection_CaretBeforeScope_SkipsForwardIntoScope() =>
        Sta.Run(() =>
        {
            // 起点をスコープ先頭までクランプする=スコープより前のヒットを置換しない。
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3);
            host.Search.OnInSelectionToggled(true);
            doc.Editor.SetCaretCharOffset(0); // キャレットをスコープより前へ

            host.Search.ReplaceOne();

            Assert.Equal("abc X abc", doc.Editor.Text); // 1 件目ではなく 2 件目が置換される
        });

    [Fact]
    public void ReplaceOne_InSelection_TwiceInARow_SecondIsNotRefused() =>
        Sta.Run(() =>
        {
            // 置換のたびにスコープを伸縮させて捕捉し直さないと 2 回目が「陳腐化」で拒否される。
            using var host = new Host();
            var doc = host.NewDoc("zz abc abc zz");
            host.View.Pattern = "abc";
            host.View.Replacement = "XY"; // 長さが変わる=伸縮の計算を効かせる
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(3, 7); // "abc abc" を捕捉
            host.Search.OnInSelectionToggled(true);

            host.Search.ReplaceOne();
            host.Search.ReplaceOne();

            Assert.Equal("zz XY XY zz", doc.Editor.Text);
        });

    [Fact]
    public void ReplaceOne_InSelection_LastHitInScope_AnnouncesNoMore() =>
        Sta.Run(() =>
        {
            // 置換後の「次」がスコープ外なら、そこへ飛ばずに終わる。
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3);
            host.Search.OnInSelectionToggled(true);

            host.Search.ReplaceOne();

            Assert.Equal("置換しました。これ以上見つかりません", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceOne_InSelection_WithoutCapturedScope_Announces() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            host.Search.OnInSelectionToggled(true); // 選択なしで ON=捕捉されない

            host.Search.ReplaceOne();

            Assert.Equal("選択範囲がありません", host.Announcer.Said[^1]);
            Assert.Equal("abc", doc.Editor.Text);
        });

    [Fact]
    public void ReplaceOne_InSelection_AfterEdit_RefusesStaleScope() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3); // 後半だけを捕捉
            host.Search.OnInSelectionToggled(true);

            doc.Editor.ReplaceCharRange(0, 0, "QQQQ"); // 捕捉位置が別の中身を指すようになる

            host.Search.ReplaceOne();

            Assert.Equal("QQQQabc abc", doc.Editor.Text); // 一文字も書き換えない
            Assert.Equal("選択範囲が変わりました。選択し直してください", host.Announcer.Said[^1]);
        });
```

**Step 2: 失敗を確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~ReplaceOne_InSelection"
```

Expected: FAIL —— 7 件中 少なくとも
`DoesNotReplaceOutsideScope` / `CaretBeforeScope_SkipsForwardIntoScope` /
`LastHitInScope_AnnouncesNoMore` / `WithoutCapturedScope_Announces` /
`AfterEdit_RefusesStaleScope` が赤。

**Step 3: スコープ検証を共通ヘルパーへ括り出す(挙動不変)**

`SearchController` に追加(`LiveHit` の隣):

```csharp
    /// <summary>「選択範囲のみ」の捕捉済みスコープを、現世代で使える形に解決する。
    /// 使えないときは理由を発声して null を返す(呼び出し側は素直に return してよい)。</summary>
    /// <remarks>
    /// 捕捉後に文書が編集されると、同じ char 位置が別の中身を指す。そのまま置換すると
    /// ユーザーが選択していない範囲を書き換えたうえ成功発声する(SR ユーザーには区別がつかない)。
    /// 世代判定は捕捉元スナップショットとの参照同一性で行う。TextSnapshot は編集のたびに
    /// 新インスタンスになり、キャレット・選択の移動では変わらない=「検索移動でクロバーされない」
    /// という捕捉方式の目的は壊れない。Undo で捕捉時と同一内容へ戻しても陳腐化と扱う=安全側。
    /// </remarks>
    private (int Start, int End)? TryResolveScope(TextSnapshot snap)
    {
        if (_selectionScope is not { } scope)
        {
            Announce("選択範囲がありません");
            return null;
        }
        if (!scope.Snap.TryGetTarget(out var captured) || !ReferenceEquals(captured, snap))
        {
            _selectionScope = null; // 旧ピース木の参照を即手放す
            Announce("選択範囲が変わりました。選択し直してください");
            return null;
        }
        return (scope.Start, scope.End);
    }

    /// <summary>ヒットがスコープに完全に収まるか(スコープなし=全文なら常に true)。</summary>
    private static bool WithinScope(MatchSpan hit, (int Start, int End)? scope) =>
        scope is not { } s || (hit.Start >= s.Start && hit.End <= s.End);
```

`ReplaceAll` の該当ブロックを差し替える(**挙動不変のリファクタ**。文言も判定も同じ)。

変更前:

```csharp
            if (d.InSelection)
            {
                if (_selectionScope is not { } scope)
                {
                    Announce("選択範囲がありません");
                    return;
                }
                // 捕捉後に文書が編集されると、同じ char 位置が別の中身を指す。そのまま置換すると
                // ユーザーが選択していない範囲を書き換えたうえ「N 件置換しました」と成功発声する
                // (SR ユーザーには区別がつかない)。使わずに拒否する。
                // TextSnapshot は編集のたびに新インスタンスになり、キャレット・選択の移動では
                // 変わらない=「検索移動でクロバーされない」という捕捉方式の目的は壊れない。
                if (!scope.Snap.TryGetTarget(out var captured) || !ReferenceEquals(captured, snap))
                {
                    _selectionScope = null; // 旧ピース木の参照を即手放す
                    Announce("選択範囲が変わりました。選択し直してください");
                    return;
                }
                rangeStart = scope.Start;
                rangeLen = scope.End - scope.Start;
            }
```

変更後:

```csharp
            if (d.InSelection)
            {
                if (TryResolveScope(snap) is not { } scope)
                    return; // 理由は TryResolveScope が発声済み
                rangeStart = scope.Start;
                rangeLen = scope.End - scope.Start;
            }
```

ここで一度ビルドしてテストを走らせ、**`ReplaceAll_*` が全緑のまま**であることを確認する
(括り出しが挙動不変であることの確認。まだ `ReplaceOne_InSelection_*` は赤でよい)。

**Step 4: `ReplaceOne` にスコープ制約を足す**

Task 3 で書き換えた `ReplaceOne` の `try` ブロックを、最終形に仕上げる。

`var (selStart, selEnd) = ed.GetSelectionCharRange();` の直後に挿入:

```csharp
            // T-3: 「選択範囲のみ」ON なら置換対象をスコープ内に閉じる。
            // ReplaceAll と同じ判定・同じ文言を使う(片方だけ通る非一貫を作らない)。
            (int Start, int End)? scope = null;
            if (d.InSelection)
            {
                if (TryResolveScope(snap) is not { } resolved)
                    return; // 理由は TryResolveScope が発声済み
                scope = resolved;
            }
```

現ヒットの採用条件に包含判定を足す(Task 3 の `if` を差し替え):

```csharp
            if (
                LiveHit(snap, selStart, selEnd) is { } hit
                && WithinScope(hit, scope)
                && searcher.ReplacementAt(snap, hit, d.Replacement) is { } liveRepl
            )
```

`else` ブロックの探索を、スコープ先頭までクランプした起点にする:

```csharp
                // 起点: スコープなしは従来どおり選択の終端から前進する(挙動不変)。
                // スコープありは選択の<b>始端</b>を起点にしてスコープ先頭までクランプする。
                // 終端を使うと、範囲を選んで「選択範囲のみ」を ON にした直後
                // (選択 == スコープ)に起点がスコープの外に出てしまい、範囲内に未置換の
                // ヒットがあるのに 1 回目の「置換」が空振りする。始端なら
                //   ・トグル直後(選択 == スコープ)→ スコープ先頭から
                //   ・スコープより前にキャレット → スコープ先頭へ繰り上がる
                //   ・スコープより後ろにキャレット → そのまま前方=スコープ外なので下で弾かれる
                // が一様に成り立つ。クランプ後は hit.Start >= scope.Start が保証されるので、
                // 包含判定は End 側だけで足りる。
                int from = scope is { } sc ? Math.Max(selStart, sc.Start) : selEnd;
                var next0 = searcher.FindNext(snap, from);
                if (next0 is null || !WithinScope(next0.Value, scope))
                {
                    Announce("これ以上見つかりません");
                    return;
                }
```

`ed.ReplaceCharRangeExact(...)` と `var snap2 = ed.CurrentBuffer.Current;` の直後に、
スコープの伸縮と再捕捉を足す:

```csharp
            // 置換で範囲の長さが変わる。伸縮させて新世代で捕捉し直さないと、次の置換が
            // 「陳腐化」で拒否される(ReplaceAll と同じ復帰処理)。span はスコープに完全に
            // 含まれるので、始端は動かず終端だけが差分ぶん動く。
            if (scope is { } before)
            {
                scope = (before.Start, before.End + repl.Length - span.Length);
                _selectionScope = (Weak(snap2), scope.Value.Start, scope.Value.End);
            }
```

置換後の次ヒット判定にも包含判定を足す:

```csharp
            var next = searcher.FindNext(snap2, span.Start + repl.Length);
            if (next is null || !WithinScope(next.Value, scope))
            {
                _lastHit = null;
                Announce("置換しました。これ以上見つかりません");
                return;
            }
```

**Step 5: テストが通ることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

Expected: PASS(新規 7 件 + 既存全件)

**Step 6: commit**

```bash
git add src/kxEdit.App/SearchController.cs tests/kxEdit.App.Tests/SearchControllerTests.cs
git commit -m "fix(app): 「選択範囲のみ」を単発置換にも効かせる(T-3)"
```

**Step 7: 仕様レビュー**

設計書 §2.3 どおりか(文言の一致・起点クランプ・伸縮の式)を別エージェントで確認する。

---

## Task 5: 監査書への実施記録と申し送りの整理

**Files:**
- Modify: `docs/plans/2026-08-29-replace-one-hit-and-scope-design.md`(§9 として実施記録を追記)

設計時に想定していなかった事実が出ていれば、設計書の末尾に「§9 実装時の追記」として記録する
(§1〜§8 は**書き換えない**。CLAUDE.md §8)。少なくとも次を確認して書く:

- 設計書 §2 のコードが実物と食い違った点(計画のコードは正解ではない前提で書かれている)
- 赤にならなかったテストがあれば、その fixture の欠陥と差し替え内容
- 変異が生存した箇所と、それを殺すために足した網

`docs/plans/2026-08-22-v0.2-release-bug-audit.md` は**策定時スナップショットなので書き換えない**。

```bash
git add docs/plans/2026-08-29-replace-one-hit-and-scope-design.md
git commit -m "docs(plans): A-14 / T-3 の実装記録を追記"
```

---

## Task 6: 最終ブランチレビュー(2 パス)

CLAUDE.md §3-5。**パスごとに独立した別エージェントを起動する**(1 起動に混載しない)。

**パス A: コード品質**

観点:
- `SelectHit` / `LiveHit` / `TryResolveScope` / `WithinScope` の 4 ヘルパーが責務どおりか、
  呼び忘れ経路が残っていないか(`_lastHit` に直接代入している箇所が残っていないか)
- `ReplaceCharRangeExact` の委譲が恒等である根拠が崩れていないか
- ミューテーション検証のスポットチェック(設計書 §4):
  - `SnapToLogicalCharEnd` の `pos + 1` を `pos` 固定 → CRLF/サロゲートのテストが赤になるか
  - `WithinScope` を `true` 固定 / `false` 固定 → それぞれ対応するテストが赤になるか
  - `LiveHit` の 2 条件を片方ずつ `true` 固定 → 対応するテストが赤になるか
  - 起点 `Math.Max(selStart, sc.Start)` を `selStart` 単体へ戻す → `CaretBeforeScope_*` が赤になるか
  - 同じく `selEnd` へ差し替える → `ReplacesInsideScope` / `TwiceInARow_*` が赤になるか
    (トグル直後に 1 回目が空振りする退行を殺す網)

> **変異ハーネスの注意**: 変異は 1 か所だけにし、アナライザに叱られない形で書く。
> ビルド失敗の検出は `grep -E " error [A-Z]+[0-9]+"` を使う(`grep "error CS"` だと
> Sonar の `error S###` を見落として古い DLL でテストが通ったように見える)。

**パス B: 脆弱性**

観点: `ReplaceCharRangeExact` の算術(オーバーフロー・負値・クランプ順序)、
`GetText` に渡す長さが常に非負であること、スコープ伸縮の式が負の範囲を作らないこと。

指摘は CLAUDE.md §4 の 3 択(① fixup commit / ② PR description に記載して受容 / ③ 理由付き却下)で
処理し、修正は**元 commit を書き換えず別 fixup commit** で積む。

---

## Task 7: L5 チェックリストの作成と品質ゲート

**Step 1: L5 チェックリストを作る**

`docs/plans/2026-08-29-replace-one-hit-and-scope-l5-checklist.md` を新規作成する。
設計書 §7 のとおり L5 は**必要**。既存の
`docs/plans/2026-08-28-eol-detection-and-undo-l5-checklist.md` を雛形にする。

最低限の項目:

1. CRLF 文書で正規表現 `\n` を単発置換 → NVDA が「置換しました。N 件中 M 件目」を 1 回だけ読む
2. 同、置換後のキャレット位置と読み上げ行が視覚表示と一致する
3. 正規表現 `\r` で F3 を連打 → 1 件ずつ前進し、同じ位置に留まらない
4. 「選択範囲のみ」ON + スコープ未捕捉で「置換」→「選択範囲がありません」を 1 回だけ読む
5. 「選択範囲のみ」ON + 編集後に「置換」→「選択範囲が変わりました。選択し直してください」を
   1 回だけ読む(既存の「選択範囲がありません」と取り違えないこと)
6. 「選択範囲のみ」ON で範囲末まで置換 →「置換しました。これ以上見つかりません」で止まる

NVDA スピーチビューアーで実発声を逐語確認する(UIA 応答の確認だけでは L5 の代替にならない)。

**Step 2: 品質ゲート**

```bash
powershell -File tools\pre-merge-check.ps1
```

Expected: **EXIT 0**(Core / Editor / App 全緑・0 warning)

**Step 3: commit と push**

```bash
git add docs/plans/2026-08-29-replace-one-hit-and-scope-l5-checklist.md
git commit -m "docs(plans): A-14 / T-3 の L5 チェックリストを作成"
git push -u origin feature/replace-one-hit-and-scope
```

**Step 4: PR 作成**

description は日本語で、目的・レビュー経緯・申し送り(設計書 §6 の 4 件)を記載する。
**L5 が未実施であることを明記する**(実施はユーザーに依頼する)。

---

## 完了条件

- [ ] Task 1〜4 の実装とテストが緑、`-warnaserror` で 0 warning
- [ ] Task 3 Step 2 の 4 件が**修正前に赤**であることを実測で確認した
- [ ] Task 6 の 2 パスを別エージェントで実施し、指摘を 3 択で処理した
- [ ] `tools/pre-merge-check.ps1` が EXIT 0
- [ ] L5 チェックリストを作成し、ユーザーへ実機検証を依頼した
