# CRLF を 1 論理文字として扱う(キャレット atomic 化)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** キャレット/選択/BS/Del/UIA NextChar/PrevChar/マウスクリック/文字数照会 のすべてで CRLF pair を 1 論理文字として扱い、SR が「復帰/改行」の 2 回発声する現象と ← → 2 回移動・文字数 2 加算を根治する。

**Architecture:** バッファ層は不変(UTF-16 code unit・byte 完全一致契約維持)。中央スナップ `CaretController.SnapAndClamp` を CRLF pair も snap するよう拡張(サロゲート atomic の対称)+ `NavigationCommands.MoveLeftChar/MoveRightChar` と UIA `NextChar/PrevChar` を CRLF pair 対応+ 論理文字数用の `TextSnapshot.CountCrlfPairs` を新設。App 層の位置読み上げが `SnapshotText.Length` を渡している所を論理文字数に差替。

**Tech Stack:** C# / .NET / xUnit / WinForms / UIA(System.Windows.Automation)

**設計書:** [2026-07-24-crlf-atomic-caret-design.md](2026-07-24-crlf-atomic-caret-design.md)

---

## 実装順序と依存

依存方向: **Core → Editor → App**

- Task 1(Core): 純ロジック追加(`SnapAndClamp` 拡張・`MoveLeftChar/MoveRightChar` 拡張・`CountCrlfPairs` 新設)
- Task 2(Editor): UIA adapter の `NextChar/PrevChar` 拡張。中央 snap 拡張の副次効果を回帰テストで固定
- Task 3(App): `AnnouncePosition` を論理文字数に差替
- Task 4(最終ブランチレビュー 2 パス): 品質パス+ 脆弱性パス
- Task 5(L5 実機検証依頼+ PR)

**前倒しレビュー(CLAUDE.md §3-4 例外)**:
- Task 1: 後続タスクが依存する新抽象(`CountCrlfPairs`)を導入= **コード品質レビュー**
- Task 2: SR 経路変更あり+マウス座標由来の位置 snap= **脆弱性レビュー** および **コード品質レビュー**
- Task 3: 位置読み上げ差替のみ=通常仕様レビュー

---

## Task 1: Core — 中央 snap 拡張 + CRLF ナビ atomic + CountCrlfPairs

**Files:**
- Modify: `src/yEdit.Editor/CaretController.cs`(SnapAndClamp に CRLF pair 分岐追加)
- Modify: `src/yEdit.Core/Editing/NavigationCommands.cs`(MoveLeftChar/MoveRightChar に CRLF pair 分岐追加)
- Modify: `src/yEdit.Core/Buffer/TextSnapshot.cs`(CountCrlfPairs 新設)
- Modify: `src/yEdit.Core/Reading/PositionFormatter.cs`(docstring コメントのみ「CRLF=1・サロゲート=2」に更新)
- Test: `tests/yEdit.Core.Tests/Editing/NavigationCommandsTests.cs`(CRLF pair 追加)
- Test: `tests/yEdit.Core.Tests/Buffer/TextSnapshotTests.cs`(CountCrlfPairs 追加)
- Test: `tests/yEdit.Editor.Tests/CaretControllerTests.cs`(存在する場合。無ければ EditorControl 側テストで代替)

### Step 1-1: `MoveLeftChar` の CRLF pair 分岐 — 失敗テスト

`tests/yEdit.Core.Tests/Editing/NavigationCommandsTests.cs` に追加:

```csharp
[Fact]
public void MoveLeftChar_SkipsCrlfPair()
{
    // "a\r\nb"(CharLength=4): pos=3(LF の後・=次行先頭) から Left で pos=1(CR の前・=行末) へ
    var s = Snap("a\r\nb");
    Assert.Equal(1, NavigationCommands.MoveLeftChar(s, 3));
}

[Fact]
public void MoveLeftChar_LoneCr_MovesOneStep()
{
    // 孤立 CR(Mac 型): "a\rb"(CharLength=3): pos=2 → pos=1(CR の前ではなく CR 自体を 1 code unit 越え)
    var s = Snap("a\rb");
    Assert.Equal(1, NavigationCommands.MoveLeftChar(s, 2));
}

[Fact]
public void MoveLeftChar_LoneLf_MovesOneStep()
{
    // 孤立 LF(Unix 型): "a\nb"
    var s = Snap("a\nb");
    Assert.Equal(1, NavigationCommands.MoveLeftChar(s, 2));
}
```

### Step 1-2: テストを走らせて失敗を確認

```powershell
dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~MoveLeftChar_SkipsCrlfPair|FullyQualifiedName~MoveLeftChar_LoneCr|FullyQualifiedName~MoveLeftChar_LoneLf" -v n
```

Expected: 1 件目(`MoveLeftChar_SkipsCrlfPair`)が失敗、`Expected 1 but Actual 2`(CRLF 途中で止まる)。孤立 CR/LF は既存挙動で pass。

### Step 1-3: `MoveLeftChar` に CRLF pair 分岐追加

`src/yEdit.Core/Editing/NavigationCommands.cs` の `MoveLeftChar`:

```csharp
public static int MoveLeftChar(TextSnapshot s, int caret)
{
    if (caret <= 0)
        return 0;
    int prev = caret - 1;
    if (
        prev > 0
        && char.IsLowSurrogate(s.GetChar(prev))
        && char.IsHighSurrogate(s.GetChar(prev - 1))
    )
        return prev - 1;
    // CRLF pair (2026-07-24: サロゲート atomic と対称=CR と LF の間にキャレットを立てない)
    if (prev > 0 && s.GetChar(prev) == '\n' && s.GetChar(prev - 1) == '\r')
        return prev - 1;
    return prev;
}
```

### Step 1-4: 失敗テスト pass 確認 + `MoveRightChar` 側の失敗テスト追加

Run: 上と同じ filter。Expected: 3 件 pass。

次に `MoveRightChar` 側:

```csharp
[Fact]
public void MoveRightChar_SkipsCrlfPair()
{
    var s = Snap("a\r\nb");
    Assert.Equal(3, NavigationCommands.MoveRightChar(s, 1)); // 行末(CR の前) → 次行先頭(LF の後)
}

[Fact]
public void MoveRightChar_LoneCr_MovesOneStep()
{
    var s = Snap("a\rb");
    Assert.Equal(2, NavigationCommands.MoveRightChar(s, 1));
}

[Fact]
public void MoveRightChar_LoneLf_MovesOneStep()
{
    var s = Snap("a\nb");
    Assert.Equal(2, NavigationCommands.MoveRightChar(s, 1));
}
```

Run: `dotnet test ... --filter "FullyQualifiedName~MoveRightChar_SkipsCrlfPair|FullyQualifiedName~MoveRightChar_LoneCr|FullyQualifiedName~MoveRightChar_LoneLf" -v n`
Expected: `MoveRightChar_SkipsCrlfPair` が失敗。

### Step 1-5: `MoveRightChar` に CRLF pair 分岐追加

```csharp
public static int MoveRightChar(TextSnapshot s, int caret)
{
    if (caret >= s.CharLength)
        return s.CharLength;
    char c = s.GetChar(caret);
    if (
        char.IsHighSurrogate(c)
        && caret + 1 < s.CharLength
        && char.IsLowSurrogate(s.GetChar(caret + 1))
    )
        return caret + 2;
    // CRLF pair (2026-07-24: サロゲート atomic と対称=CR と LF の間にキャレットを立てない)
    if (c == '\r' && caret + 1 < s.CharLength && s.GetChar(caret + 1) == '\n')
        return caret + 2;
    return caret + 1;
}
```

### Step 1-6: `MoveLeftChar/MoveRightChar` 追加テスト全 pass 確認

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~NavigationCommandsTests" -v n`
Expected: 全 pass(既存テストも含む)。

### Step 1-7: `CaretController.SnapAndClamp` の CRLF 分岐 — 失敗テスト

まず既存 `CaretControllerTests` の有無を確認:

```powershell
Get-ChildItem tests\yEdit.Editor.Tests\ -Filter "CaretControllerTests*"
```

存在すればそこへ、無ければ `NavigationCommandsTests` と対称に **Core Tests に別クラスは作らない**(CaretController は Editor 層 internal のため)。**Editor Tests に `CaretControllerSnapAndClampTests.cs` を新設**する。

`tests/yEdit.Editor.Tests/CaretControllerSnapAndClampTests.cs`(新規):

```csharp
using yEdit.Core.Buffers;
using yEdit.Editor;
using Xunit;

namespace yEdit.Editor.Tests;

public class CaretControllerSnapAndClampTests
{
    private static TextSnapshot Snap(string s) => TextBuffer.FromString(s).Current;

    [Fact]
    public void SnapAndClamp_MidCrlf_SnapsToCr()
    {
        // "a\r\nb"(CharLength=4): pos=2(CR と LF の間)→ pos=1(CR の前)
        var s = Snap("a\r\nb");
        Assert.Equal(1, CaretController.SnapAndClamp(2, s));
    }

    [Fact]
    public void SnapAndClamp_AtCr_NotSnapped()
    {
        // CR 位置(mid-CRLF ではなく "行末+改行の直前")は snap しない
        var s = Snap("a\r\nb");
        Assert.Equal(1, CaretController.SnapAndClamp(1, s));
    }

    [Fact]
    public void SnapAndClamp_AtLfPlus1_NotSnapped()
    {
        // LF の後(次行先頭)は snap しない
        var s = Snap("a\r\nb");
        Assert.Equal(3, CaretController.SnapAndClamp(3, s));
    }

    [Fact]
    public void SnapAndClamp_LoneLf_NotSnapped()
    {
        // "a\nb"(mid-LF は前が CR ではないので snap しない)
        var s = Snap("a\nb");
        Assert.Equal(1, CaretController.SnapAndClamp(1, s));
    }

    [Fact]
    public void SnapAndClamp_LoneCr_NotSnapped()
    {
        // "a\rb"(mid-CR は次が LF ではないので snap しない)
        var s = Snap("a\rb");
        Assert.Equal(2, CaretController.SnapAndClamp(2, s));
    }
}
```

Run: `dotnet test tests/yEdit.Editor.Tests/yEdit.Editor.Tests.csproj --filter "FullyQualifiedName~CaretControllerSnapAndClampTests" -v n`
Expected: `SnapAndClamp_MidCrlf_SnapsToCr` が失敗(Actual 2)。

### Step 1-8: `CaretController.SnapAndClamp` に CRLF 分岐追加

`src/yEdit.Editor/CaretController.cs` の `SnapAndClamp`:

```csharp
public static int SnapAndClamp(int offset, TextSnapshot snap)
{
    if (offset <= 0)
        return 0;
    if (offset >= snap.CharLength)
        return snap.CharLength;
    char c = snap.GetChar(offset);
    if (char.IsLowSurrogate(c))
    {
        char prev = snap.GetChar(offset - 1);
        if (char.IsHighSurrogate(prev))
            return offset - 1;
    }
    // CRLF pair の中間位置(pos-1='\r'・pos='\n')は CR の前へスナップ(行末位置=MoveEnd と同位置)
    // 2026-07-24: キャレット/選択のすべての位置設定入り口が本メソッドを通るため、
    // ここで一度スナップすれば mid-CRLF は不変条件として守られる。
    if (c == '\n' && snap.GetChar(offset - 1) == '\r')
        return offset - 1;
    return offset;
}
```

Run: 同じ filter。Expected: 全 5 件 pass。

### Step 1-9: 中央 snap 拡張の副次効果を EditorControl 側でも観測 — 失敗テスト

`tests/yEdit.Editor.Tests/EditorControlNavigationTests.cs`(既存に追加。無ければ新規):

```csharp
[Fact]
public void SetCaretCharOffset_MidCrlf_SnapsToCr()
{
    Sta.Run(() =>
    {
        using var ctrl = new EditorControl();
        ctrl.SetSource(TextBuffer.FromString("a\r\nb"));
        ctrl.SetCaretCharOffset(2); // mid-CRLF
        Assert.Equal(1, ctrl.CaretCharOffset);
    });
}

[Fact]
public void SetSelectionCharRange_MidCrlf_SnapsToCr()
{
    Sta.Run(() =>
    {
        using var ctrl = new EditorControl();
        ctrl.SetSource(TextBuffer.FromString("a\r\nb"));
        ctrl.SetSelectionCharRange(2, 3); // start=mid-CRLF, end=LF+1
        var (s, e) = ctrl.GetSelectionCharRange();
        Assert.Equal(1, s); // start snapped to CR-1
        Assert.Equal(3, e);
    });
}
```

`EditorControlNavigationTests` 既存の有無:

```powershell
Get-ChildItem tests\yEdit.Editor.Tests\ -Recurse -Filter "*.cs" | Select-String -Pattern "class EditorControl.*Tests" | Select-Object -First 10
```

無ければ `EditorControlCrlfCaretTests.cs` を新設。

Run: `dotnet test tests/yEdit.Editor.Tests/yEdit.Editor.Tests.csproj --filter "FullyQualifiedName~MidCrlf" -v n`
Expected: 上記 2 件が pass(中央 snap 拡張の副次効果=追加コードなし)。**Step 1-8 の実装で自動 pass するのを回帰保険として固定**。

### Step 1-10: `TextSnapshot.CountCrlfPairs` — 失敗テスト

`tests/yEdit.Core.Tests/Buffer/TextSnapshotTests.cs` に追加:

```csharp
[Fact]
public void CountCrlfPairs_EmptyRange_ReturnsZero()
{
    var s = Snap("a\r\nb");
    Assert.Equal(0, s.CountCrlfPairs(0, 0));
    Assert.Equal(0, s.CountCrlfPairs(2, 2));
    Assert.Equal(0, s.CountCrlfPairs(s.CharLength, s.CharLength));
}

[Fact]
public void CountCrlfPairs_FullRange_CountsCrlfPairs()
{
    var s = Snap("a\r\nb\r\nc");
    Assert.Equal(2, s.CountCrlfPairs(0, s.CharLength));
}

[Fact]
public void CountCrlfPairs_LoneCrLfMixed_CountsOnlyPairs()
{
    var s = Snap("a\rb\nc\r\nd\rd\ne"); // CRLF 1 個 + 孤立 CR 2 個 + 孤立 LF 2 個
    Assert.Equal(1, s.CountCrlfPairs(0, s.CharLength));
}

[Fact]
public void CountCrlfPairs_PartialRange_Correct()
{
    var s = Snap("a\r\nb\r\nc");
    // 部分範囲: 最初の CRLF(pos 1-3)のみ [0, 3) には CRLF 1 個
    Assert.Equal(1, s.CountCrlfPairs(0, 3));
    // 最初の CRLF の途中で切る(端点 mid-CRLF): CR で切れる=CRLF 未成立=0
    Assert.Equal(0, s.CountCrlfPairs(0, 2));
    // 2 つ目の CRLF のみ含む [3, 7): CRLF 1 個
    Assert.Equal(1, s.CountCrlfPairs(3, 7));
}

[Fact]
public void CountCrlfPairs_PieceBoundaryStraddled_CountsCrlfAcrossPieces()
{
    // ピース跨ぎ: 意図的に "a\r" | "\nb" の 2 ピース構成
    var snap = new TextSnapshot(PieceTree.BuildBalanced(
        new[] { P("a\r"), P("\nb") }));
    Assert.Equal(1, snap.CountCrlfPairs(0, snap.CharLength));
}

// 上の Piece / P ヘルパは TextSnapshotTests の既存 static ヘルパを利用(先頭に既にある)
```

Run: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj --filter "FullyQualifiedName~CountCrlfPairs" -v n`
Expected: 全件失敗(未実装)。

### Step 1-11: `TextSnapshot.CountCrlfPairs` 実装

`src/yEdit.Core/Buffer/TextSnapshot.cs` に追加(末尾の適切な位置):

```csharp
/// <summary>[start, endExclusive) に含まれる CRLF pair の数(=論理文字数計算用)。
/// ピース跨ぎ("...\r" | "\n...")も 1 pair としてカウントする。UTF-8 byte 走査
/// (0x0D → 次 byte が 0x0A なら pair)で全文 string 化しない。</summary>
public int CountCrlfPairs(int start, int endExclusive)
{
    if (start < 0 || endExclusive < 0)
        throw new ArgumentOutOfRangeException();
    if (endExclusive > CharLength)
        throw new ArgumentOutOfRangeException(nameof(endExclusive));
    if (start >= endExclusive)
        return 0;
    // CountCrlfPairs は「論理文字数計算用」の低頻度ホットキー経路のみ想定=
    // シンプルさ優先で GetText(start, length) を使う(既存 SnapshotReader 相当は
    // 局所走査に呼びにくいため。全文キャレット位置照会は 1 回 O(N) 許容)。
    string t = GetText(start, endExclusive - start);
    int count = 0;
    for (int i = 0; i + 1 < t.Length; i++)
    {
        if (t[i] == '\r' && t[i + 1] == '\n')
        {
            count++;
            i++; // 次の LF はスキップ(CRLF が重ならないよう)
        }
    }
    return count;
}
```

**注意**: 「境界跨ぎ("...\r" | "\n...")」の扱いは `GetText(start, len)` が全域を返せば自動的に成立する(start=0, len=CharLength の場合)。しかし部分範囲で「範囲の直前が CR・範囲の先頭が LF」のケースは範囲内に CRLF pair は存在しない=0 で正しい(境界を跨ぐ CRLF pair は範囲の内側ではない)。ピース跨ぎテスト(Step 1-10 の Piece 境界)は「範囲全域取得中に境界を跨ぐ CRLF」を保証する。

Run: 同じ filter。Expected: 全 5 件 pass。

### Step 1-12: PositionFormatter のコメント更新

`src/yEdit.Core/Reading/PositionFormatter.cs` の docstring:

```csharp
/// <summary>
/// 「行 L / 全 N、桁 C、文字数 M」を組み立てる。selectionLength&gt;0 なら「、選択 K 文字」、
/// overtype 時は「、上書き」を付ける（挿入/上書きモードを照会でも分かるようにする）。
/// line/column は 1 始まり、totalChars/selectionLength は論理文字数（CRLF=1・サロゲート=2 で数える。
/// 2026-07-24 に UTF-16 code unit 数から変更=CRLF を 1 論理文字として扱う統一）。
/// </summary>
```

コード変更なし=テスト影響なし。

### Step 1-13: Task 1 全体テスト・ゲート実行

```powershell
dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj -v n
dotnet test tests/yEdit.Editor.Tests/yEdit.Editor.Tests.csproj --filter "FullyQualifiedName~CaretControllerSnapAndClampTests|FullyQualifiedName~MidCrlf" -v n
```

Expected: 全 pass(既存テストの回帰なし)。

### Step 1-14: Task 1 commit

```powershell
git status
git add src/yEdit.Core/Editing/NavigationCommands.cs `
        src/yEdit.Editor/CaretController.cs `
        src/yEdit.Core/Buffer/TextSnapshot.cs `
        src/yEdit.Core/Reading/PositionFormatter.cs `
        tests/yEdit.Core.Tests/Editing/NavigationCommandsTests.cs `
        tests/yEdit.Core.Tests/Buffer/TextSnapshotTests.cs `
        tests/yEdit.Editor.Tests/CaretControllerSnapAndClampTests.cs
```

`EditorControlCrlfCaretTests.cs` を新設した場合はそれも `git add`。

Commit(HEREDOC):

```bash
git commit -m "$(cat <<'EOF'
feat(core): CRLF pair を 1 論理文字として扱う中央 snap + ナビ atomic 化

- NavigationCommands.MoveLeftChar/MoveRightChar に CRLF pair 分岐追加(サロゲート atomic の対称)
- CaretController.SnapAndClamp に CRLF 中間位置 → CR の前スナップ分岐追加(すべての位置設定入り口を通る中央箇所)
- TextSnapshot.CountCrlfPairs 新設(論理文字数計算用)
- PositionFormatter docstring を「CRLF=1・サロゲート=2」に更新
- L1(Core) + L2(Editor CaretController) テスト追加

設計書: docs/plans/2026-07-24-crlf-atomic-caret-design.md

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

### Step 1-15: Task 1 前倒しコード品質レビュー(subagent)

**REQUIRED**: 新抽象 `CountCrlfPairs` と中央 snap 拡張は Task 2/3 が依存するため。superpowers:subagent-driven-development に従い code-reviewer subagent へ依頼。指摘は fixup commit で反映。

---

## Task 2: Editor — UIA adapter の NextChar/PrevChar 拡張

**Files:**
- Modify: `src/yEdit.Editor/UiaTextHostAdapter.cs`(NextChar/PrevChar に CRLF pair 分岐追加)
- Test: `tests/yEdit.Editor.Tests/UiaTextHostAdapterCrlfTests.cs`(新規。既存 UIA テストが別ファイルにあればそちらに追加)

### Step 2-1: 既存 UIA adapter テストファイル探索

```powershell
Get-ChildItem tests\yEdit.Editor.Tests\ -Recurse -Filter "*Uia*Test*.cs"
```

既存があればそこへ追加。無ければ `UiaTextHostAdapterCrlfTests.cs` を新設。

### Step 2-2: NextChar/PrevChar の CRLF pair — 失敗テスト

新設または既存に追加:

```csharp
using yEdit.Accessibility;
using yEdit.Core.Buffers;
using yEdit.Editor;
using Xunit;

namespace yEdit.Editor.Tests;

public class UiaTextHostAdapterCrlfTests
{
    [Fact]
    public void UiaNextChar_SkipsCrlfPair()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\r\nb"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(3, host.NextChar(1)); // CR の前 → LF の後(pair skip)
        });
    }

    [Fact]
    public void UiaPrevChar_SkipsCrlfPair()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\r\nb"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(1, host.PrevChar(3)); // LF の後 → CR の前
        });
    }

    [Fact]
    public void UiaNextChar_LoneCr_MovesOneStep()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\rb"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(2, host.NextChar(1));
        });
    }

    [Fact]
    public void UiaPrevChar_LoneLf_MovesOneStep()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(1, host.PrevChar(2));
        });
    }
}
```

Run: `dotnet test tests/yEdit.Editor.Tests/yEdit.Editor.Tests.csproj --filter "FullyQualifiedName~UiaTextHostAdapterCrlfTests" -v n`
Expected: `UiaNextChar_SkipsCrlfPair`(Actual 2)と `UiaPrevChar_SkipsCrlfPair`(Actual 2)が失敗。孤立 CR/LF は pass。

### Step 2-3: UiaTextHostAdapter.NextChar/PrevChar に CRLF pair 分岐追加

`src/yEdit.Editor/UiaTextHostAdapter.cs` の `IUiaTextHost.NextChar`:

```csharp
int IUiaTextHost.NextChar(int offset)
{
    var snap = _bufferSnapshot;
    if (snap is null)
        return 0;
    int o = Math.Clamp(offset, 0, snap.CharLength);
    if (o >= snap.CharLength)
        return snap.CharLength;
    char c = snap.GetChar(o);
    if (
        char.IsHighSurrogate(c)
        && o + 1 < snap.CharLength
        && char.IsLowSurrogate(snap.GetChar(o + 1))
    )
        return o + 2;
    // CRLF pair (2026-07-24: サロゲート atomic と対称)
    if (c == '\r' && o + 1 < snap.CharLength && snap.GetChar(o + 1) == '\n')
        return o + 2;
    return o + 1;
}
```

`IUiaTextHost.PrevChar`:

```csharp
int IUiaTextHost.PrevChar(int offset)
{
    var snap = _bufferSnapshot;
    if (snap is null)
        return 0;
    int o = Math.Clamp(offset, 0, snap.CharLength);
    if (o <= 0)
        return 0;
    if (
        char.IsLowSurrogate(snap.GetChar(o - 1))
        && o - 2 >= 0
        && char.IsHighSurrogate(snap.GetChar(o - 2))
    )
        return o - 2;
    // CRLF pair (2026-07-24: サロゲート atomic と対称)
    if (snap.GetChar(o - 1) == '\n' && o - 2 >= 0 && snap.GetChar(o - 2) == '\r')
        return o - 2;
    return o - 1;
}
```

Run: 同じ filter。Expected: 全 4 件 pass。

### Step 2-4: UIA SetSelection 経路が中央 snap を経由することを回帰テスト

Step 1-8 で `CaretController.SnapAndClamp` を拡張済み+ `IUiaTextHost.SetSelection` は `_host.SetSelectionCharRange` へ委譲(= `SnapAndClamp` を通る)ため、追加コードなしで snap される。回帰保険:

```csharp
[Fact]
public void UiaSetSelection_MidCrlf_SnapsToCr()
{
    Sta.Run(() =>
    {
        using var ctrl = new EditorControl();
        ctrl.SetSource(TextBuffer.FromString("a\r\nb"));
        var host = (IUiaTextHost)ctrl;
        host.SetSelection(2, 3); // start=mid-CRLF
        // SetSelection は UIA 契約=CaretController 経由=SnapAndClamp で mid-CRLF は snap される
        var (s, e) = host.GetSelection();
        Assert.Equal(1, s);
        Assert.Equal(3, e);
    });
}
```

Run: Expected pass(既存の Step 1-8 実装で自動 pass)。

### Step 2-5: マウスクリック mid-CRLF snap の観測回帰テスト

InputRouter マウス経路は `OffsetFromClientPoint` → `SetCaretCharOffset` を呼ぶ=CaretController 経由=snap される。仕込みは複雑(座標→文字位置の環境依存)。**代わりに: `OffsetFromClientPoint` の返り値を `SetCaretCharOffset` へ渡す統合を Step 1-9 の EditorControl テストでカバー済み**とする(mid-CRLF を setter に入れて snap されるのを確認)。

### Step 2-6: Task 2 テスト・ゲート実行

```powershell
dotnet test tests/yEdit.Editor.Tests/yEdit.Editor.Tests.csproj -v n
```

Expected: 全 pass。既存 UIA テストの回帰なし。

### Step 2-7: Task 2 commit

```powershell
git status
git add src/yEdit.Editor/UiaTextHostAdapter.cs tests/yEdit.Editor.Tests/UiaTextHostAdapterCrlfTests.cs
```

Commit:

```bash
git commit -m "$(cat <<'EOF'
feat(editor): UIA NextChar/PrevChar を CRLF pair atomic 化

- UiaTextHostAdapter.NextChar/PrevChar に CRLF pair 分岐追加(NavigationCommands と対称)
- UIA SetSelection 経由の mid-CRLF は CaretController 経由で中央 snap(Task 1)により自動対応
- L2 テスト追加(UIA NextChar/PrevChar・SetSelection の mid-CRLF snap)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

### Step 2-8: Task 2 前倒しレビュー(subagent × 2)

- **コード品質レビュー**: superpowers:subagent-driven-development で code-reviewer 起動
- **脆弱性レビュー**: SR 経路変更+マウス座標由来の snap 経路=別セッション code-reviewer に「セキュリティ観点(RPC スレッドから触る field 分離が壊れていないか・座標 clamp/snap の境界)」で依頼

指摘は fixup commit で反映。

---

## Task 3: App — AnnouncePosition の論理文字数化

**Files:**
- Modify: `src/yEdit.App/MainForm.cs`(AnnouncePosition の文字数計算)
- Test: `tests/yEdit.App.Tests/AnnouncePositionTests.cs`(存在すれば追加、無ければ新設)

### Step 3-1: 既存 App 層テストの探索

```powershell
Get-ChildItem tests\yEdit.App.Tests\ -Recurse -Filter "*Position*.cs"
Get-ChildItem tests\yEdit.App.Tests\ -Recurse -Filter "*Announce*.cs"
```

多くの App 層テストは MainForm 経由が困難な設計(既存で helper 抽出済みなら active)。存在すればそこへ、無ければ **App 層 helper に抽出せず Core の PositionFormatter 側テストで機械固定**とする。

### Step 3-2: PositionFormatter に「Editor から論理文字数を引き出す helper」入口を追加(または既存を利用)

MainForm から直接 CountCrlfPairs を叩く形にすると SnapshotText.Length との差分が読みにくい。**新規 helper**:

`src/yEdit.Core/Reading/PositionFormatter.cs` に helper 追加(または `EditorControl` に extension property を検討 → シンプルさ優先で App 層で計算する形にする):

**方針決定**: MainForm 内で `snap.CountCrlfPairs(0, snap.CharLength)` を直接引く。理由=CountCrlfPairs 単体テスト(Task 1)で数え方は保証済み・App 層は「差し引くだけ」の透明な計算で helper 抽出のコスト対効果が低い。

### Step 3-3: MainForm.AnnouncePosition の変更 — 失敗テストは無理筋(MainForm SR は L5 が正)

**理由**: MainForm は UI/SR 統合層で「実際に読み上げられた文字列」の自動テストは Announcer/L5 の領域。**Task 3 は無テスト実装 → L3 App テスト(既存経路の Announce 経路をテスト可能な helper に抽出済みなら固定)+ L5 検証で確認**とする。

もし App.Tests に `AnnouncerFake` 経由で PositionFormatter 出力を検証する既存パターンがあれば、それに合わせて追加テストを 1 件書く(pos=CRLF ライン末尾で文字数が CRLF pair 数を引いた値になる)。

```powershell
Get-ChildItem tests\yEdit.App.Tests\ -Recurse -Filter "*.cs" | Select-String -Pattern "AnnouncerFake|Announcer.*Fake|FakeAnnouncer" | Select-Object -First 5
```

**存在すれば**: 既存パターン踏襲でテスト追加。**無ければ**: Task 3 はテストなし実装 → L5 検証で確認(SR が「文字数 M」を正しく読むこと)。

### Step 3-4: MainForm.AnnouncePosition の実装差替

`src/yEdit.App/MainForm.cs` の `AnnouncePosition`:

```csharp
private void AnnouncePosition()
{
    var ed = _docs.Active?.Editor;
    if (ed is null)
        return;
    int line = ed.CurrentLine + 1;
    int totalLines = ed.LineCount;
    int column = ed.GetColumn(ed.CurrentPosition) + 1;
    var (s, e) = ed.GetSelectionCharRange();
    // 2026-07-24: 論理文字数(CRLF=1・サロゲート=2)に統一。SnapshotText.Length は
    // UTF-16 code unit 数=CRLF を 2 として数えるため、CRLF pair 数を差し引く。
    var snap = ed.Buffer!.Current;
    int totalLogical = snap.CharLength - snap.CountCrlfPairs(0, snap.CharLength);
    int selLogical = (e - s) - snap.CountCrlfPairs(s, e);
    _announcer.Say(
        PositionFormatter.Format(
            line,
            totalLines,
            column,
            totalLogical,
            selLogical,
            ed.Overtype
        )
    );
}
```

**注意**: `ed.Buffer` が public/internal accessor で App から見えるかを確認。もし見えなければ `EditorControl` に `LogicalCharCount` / `LogicalSelectionLength(int, int)` の 2 property/method を追加する:

```csharp
// src/yEdit.Editor/EditorControl.cs (適切な位置に追加)
/// <summary>論理文字数(CRLF=1・サロゲート=2)。位置照会読み上げ用。低頻度のみ許容。</summary>
public int LogicalCharCount
{
    get
    {
        var snap = _buffer?.Current;
        if (snap is null) return 0;
        return snap.CharLength - snap.CountCrlfPairs(0, snap.CharLength);
    }
}

/// <summary>指定範囲の論理文字数(選択長計算用)。</summary>
public int LogicalSelectionLength(int start, int end)
{
    var snap = _buffer?.Current;
    if (snap is null) return 0;
    int s = Math.Clamp(Math.Min(start, end), 0, snap.CharLength);
    int e = Math.Clamp(Math.Max(start, end), 0, snap.CharLength);
    return (e - s) - snap.CountCrlfPairs(s, e);
}
```

App 側は:

```csharp
int totalLogical = ed.LogicalCharCount;
int selLogical = ed.LogicalSelectionLength(s, e);
```

**判定**: `ed.Buffer` の可視性を Read で確認してから決定。既存 API 追加を最小化するため優先順位=(1) `ed.Buffer` が直接見えるならそのまま、(2) 見えなければ property/method 追加。

### Step 3-5: 全ゲート実行

```powershell
dotnet build yEdit.sln
dotnet test yEdit.sln -v n
```

Expected: 全 pass・0 warning。

### Step 3-6: Task 3 commit

```powershell
git status
git add src/yEdit.App/MainForm.cs
# もし EditorControl に property/method 追加なら:
# git add src/yEdit.Editor/EditorControl.cs
# もし App.Tests に helper 経由テスト追加なら:
# git add tests/yEdit.App.Tests/AnnouncePositionTests.cs
```

Commit:

```bash
git commit -m "$(cat <<'EOF'
feat(app): 位置読み上げの文字数を論理文字数(CRLF=1)に統一

- MainForm.AnnouncePosition で SnapshotText.Length ではなく snap.CountCrlfPairs 差し引き
- (必要に応じて EditorControl に LogicalCharCount/LogicalSelectionLength 追加)
- ユーザー可視: Ctrl+? 系ホットキーで CRLF 改行 1 個が「文字数 +1」で読まれる

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

### Step 3-7: Task 3 通常仕様レビュー(subagent)

差替のみで抽象追加なし=通常仕様レビュー 1 パス。

---

## Task 4: 最終ブランチレビュー(2 パス)

CLAUDE.md §3-5 に従い、**独立した別 subagent で 2 パス**:

### Step 4-1: コード品質パス

- 対象: ブランチ全体の変更(Task 1 + Task 2 + Task 3)
- 観点: DRY・YAGNI・命名・境界事例・テストカバレッジ・境界テストの品質
- **ミューテーション検証(スポットチェック)**:
  - `MoveLeftChar/MoveRightChar` の CRLF pair 分岐を一時的に無効化 → 該当テストが赤になることを確認 → 復元
  - `SnapAndClamp` の CRLF 分岐を無効化 → Step 1-9 テストが赤 → 復元
  - `CountCrlfPairs` のカウント条件を `>=` に変えるなど → Step 1-10 テストが赤 → 復元
- superpowers:requesting-code-review → superpowers:receiving-code-review

### Step 4-2: 脆弱性パス

- 対象: 同上
- 観点: RPC スレッド安全性(UIA adapter の CRLF 分岐は _bufferSnapshot 参照のみ=変わらない)・境界外読み(GetChar のインデックス範囲)・座標由来入力の snap 境界・DoS(CountCrlfPairs の O(N) が敵性入力で問題ないか)
- 独立した別 subagent 起動(コード品質パスと混載しない)

### Step 4-3: 指摘反映

- ① fixup commit / ② PR description に受容記載 / ③ 理由付き却下 の 3 択で明示
- fixup commit は元 commit を書き換えない(履歴保存)

---

## Task 5: L5 検証依頼 + 品質ゲート + PR

### Step 5-1: pre-merge-check 実行

```powershell
pwsh tools\pre-merge-check.ps1
```

Expected: EXIT 0(CI と同種のゲート)。

### Step 5-2: L5 実機検証をユーザーへ依頼

依頼テキスト(User に提示):

> L5 実機検証(NVDA)をお願いします。以下のシナリオを CRLF ファイル + LF ファイル + CR-only ファイル + 混在ファイルで確認してください。
> 1. ← → で改行を 1 step で越える(SR が「改行」1 回だけ発声することを確認)
> 2. BS/Del で改行が 1 回で消える
> 3. Shift+← → で改行を 1 括選択
> 4. 位置照会(Ctrl+? 系)で文字数が期待どおり(CRLF 1 個 = +1)
> 5. 既存の空行能動発声・CSV モード・折り返しに退行なし

**もし NVDA が「復帰」「改行」と別読みするなら**: 設計書 §10 F-1(UIA GetTextRange の CRLF → LF 正規化)を追加 fixup として検討。

### Step 5-3: sr-regression 実行(a11y 変更のため必須)

```powershell
pwsh tools\sr-regression.ps1
```

Expected: 全 pass。

### Step 5-4: push + PR 作成

```powershell
git push -u origin feature/crlf-atomic-caret
```

PR 作成(gh CLI・HEREDOC):

```bash
gh pr create --title "fix(editor): CRLF を 1 論理文字として扱う(キャレット atomic 化)" --body "$(cat <<'EOF'
## 目的

CRLF 改行のファイルで以下の症状を根治する:
1. ←/→ で改行が 2 回移動になる(CR と LF が別位置扱い)
2. SR が「復帰(CR)」「改行(LF)」を別々に読み上げる
3. 「文字数」照会で改行 1 回が 2 として計上される

## 方針(設計書=`docs/plans/2026-07-24-crlf-atomic-caret-design.md`)

- バッファ層は不変(UTF-16 code unit・byte 完全一致契約維持)
- ナビゲーション/UIA/表示層で「CRLF pair を 1 論理文字」として振る舞う
- サロゲート atomic の対称=CR と LF の間にキャレットを立てない
- スナップ方向は常に CR の前(=行末位置・Notepad/VSCode 同方向)
- 文字数=CRLF=1・サロゲートは従来 2 のまま(保守契約維持)

## 変更点

- **Core**: NavigationCommands.MoveLeftChar/MoveRightChar に CRLF pair 分岐、CaretController.SnapAndClamp に mid-CRLF snap 分岐、TextSnapshot.CountCrlfPairs 新設
- **Editor**: UiaTextHostAdapter.NextChar/PrevChar に CRLF pair 分岐(UIA SetSelection 経路は中央 snap 拡張で自動対応)
- **App**: MainForm.AnnouncePosition で論理文字数採用(CRLF pair 数を差し引き)

## レビュー経緯

- Task 1 前倒しコード品質レビュー(新抽象): [結果を追記]
- Task 2 前倒しレビュー 2 パス(SR 経路・脆弱性): [結果を追記]
- Task 3 通常仕様レビュー: [結果を追記]
- 最終ブランチレビュー 2 パス(品質・脆弱性・ミューテーション検証): [結果を追記]

## テスト

- L1(Core): NavigationCommands / TextSnapshot に CRLF pair テスト追加
- L2(Editor): CaretControllerSnapAndClampTests + UiaTextHostAdapterCrlfTests 新設
- L3(App): 位置読み上げ helper 経由テスト(可能なら)
- L5 実機検証(NVDA): [ユーザー検証結果を追記]

## 申し送り

設計書 §10 F-1〜F-3 参照。L5 で SR 別読みが残った場合のみ UIA GetTextRange の CRLF 正規化を fixup として検討。

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

### Step 5-5: PR URL を返す・ユーザーのマージ判断を待つ

---

## 補助情報

### 変更ファイル一覧(見積り)

| Path | 変更種別 | 行数見積り |
|---|---|---|
| `src/yEdit.Core/Editing/NavigationCommands.cs` | 2 分岐追加 | +6 |
| `src/yEdit.Editor/CaretController.cs` | 1 分岐追加 | +3 |
| `src/yEdit.Core/Buffer/TextSnapshot.cs` | 1 メソッド新設 | +30 |
| `src/yEdit.Core/Reading/PositionFormatter.cs` | docstring 更新 | +1 -1 |
| `src/yEdit.Editor/UiaTextHostAdapter.cs` | 2 分岐追加 | +6 |
| `src/yEdit.Editor/EditorControl.cs` | (必要なら)LogicalCharCount 追加 | +20 |
| `src/yEdit.App/MainForm.cs` | AnnouncePosition 差替 | +4 -1 |
| `tests/yEdit.Core.Tests/Editing/NavigationCommandsTests.cs` | テスト追加 | +30 |
| `tests/yEdit.Core.Tests/Buffer/TextSnapshotTests.cs` | テスト追加 | +40 |
| `tests/yEdit.Editor.Tests/CaretControllerSnapAndClampTests.cs` | 新設 | +50 |
| `tests/yEdit.Editor.Tests/UiaTextHostAdapterCrlfTests.cs` | 新設 | +60 |

計約 250 行の追加(うちテスト 180 行)。

### 検証コマンド一覧

- Core のみ: `dotnet test tests/yEdit.Core.Tests/yEdit.Core.Tests.csproj -v n`
- Editor のみ: `dotnet test tests/yEdit.Editor.Tests/yEdit.Editor.Tests.csproj -v n`
- App のみ: `dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj -v n`
- 全体: `dotnet test yEdit.sln -v n`
- pre-merge-check: `pwsh tools\pre-merge-check.ps1`(EXIT 0 必須)
- sr-regression: `pwsh tools\sr-regression.ps1`

### 参考する既存 skill

- @superpowers:test-driven-development(各 Step の failing test → 実装の TDD ループ)
- @superpowers:subagent-driven-development(前倒しレビュー・最終 2 パス・code-reviewer 起動)
- @superpowers:requesting-code-review + @superpowers:receiving-code-review(最終ブランチレビュー)
- @superpowers:verification-before-completion(完了判定=ゲート pass 確認)
- @superpowers:finishing-a-development-branch(PR 作成・マージ)
