# 未処理例外と入力の取りこぼし(A-13 / M-1 / A-20)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** クリップボード失敗を発生源で捕捉し、未処理例外を握って退避してから終了し、分割到着したサロゲートペアを結合する。

**Architecture:** Editor 層に `IClipboard` seam と `ClipboardFailed` イベントを足し、`Copy` / `Paste` を `bool` 化して `Cut` の既存契約(クリップボードに書けなければ本文を消さない)を守る。通知は `DocumentManager` 経由で App 層の `IAnnouncer` へ流す。`Program` は `CrashHandler` + `ICrashSink` を配線し、WinForms 既定の未処理例外ダイアログに到達させない。サロゲートは `OnKeyPress` で高位を 1 つだけ保留して次の低位と結合する。

**Tech Stack:** .NET 9 / WinForms / xUnit v2 / CSharpier(pre-commit)/ `-warnaserror`

**設計書:** [2026-08-29-unhandled-exception-safety-design.md](2026-08-29-unhandled-exception-safety-design.md)

---

## この計画のコードの扱い

**計画に書いたコードは「正解」ではなく「検証すべき案」**である(過去に計画の fixture 3 つが全部欠陥だった)。
各タスクの実装者は次を必ず自分で確かめること:

- そのテストが**変更前の src で赤くなる**か(赤くならないテストは何も守っていない)
- 期待値が**既定値と区別できる**か(CLAUDE.md §4-B: no-change は非既定位置から始める)
- 行番号は執筆時点(`9480648`)のもの。**必ず現物を読んでから編集する**

---

## Task 1: A-20 — サロゲートペアの結合

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.Input.cs`(`OnKeyDown` 41-45 / `OnKeyPress` 61-72)
- Modify: `src/kxEdit.Editor/EditorControl.cs`(`OnLostFocus` があれば。無ければ追加箇所を確認)
- Test: `tests/kxEdit.Editor.Tests/SurrogatePairInputTests.cs`(新規)

### Step 1: 失敗するテストを書く

`tests/kxEdit.Editor.Tests/SurrogatePairInputTests.cs` を新規作成する。
`TextInsertionTests.cs:23-31` の `SendKeyPress` と `MakeControl` を同じ形で持ち込む
(共通化は Task 1 の範囲外。3 個目の複製が出たら `TestHost` へ寄せる)。

```csharp
using System.Reflection;
using kxEdit.Core.Text;

namespace kxEdit.Editor.Tests;

/// <summary>
/// A-20(監査 2026-08-22 / 設計 2026-08-29 §6): WM_CHAR で高・低に分割到着する
/// サロゲートペアを 1 コードポイントとして結合する契約テスト。
/// 発現源は KEYEVENTF_UNICODE で WM_CHAR を 2 通送るツール(絵文字パネルは IME 経路で無事=
/// 設計書 §2.2)。実機再現は PostMessageW(WM_CHAR, 0xD83D) → (WM_CHAR, 0xDE02)。
/// </summary>
public class SurrogatePairInputTests
{
    private const char Hi = '\uD83D'; // U+1F602 😂 の高位
    private const char Lo = '\uDE02'; // 同 低位
    private const string Emoji = "😂";

    private static (Form f, EditorControl c) MakeControl(string text)
    {
        var f = new HostForm();
        var c = new EditorControl();
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    private static void SendKeyPress(EditorControl c, char ch)
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnKeyPress",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        mi!.Invoke(c, new object[] { new KeyPressEventArgs(ch) });
    }

    private static void SendKeyDown(EditorControl c, Keys keyData)
    {
        var mi = typeof(EditorControl).GetMethod(
            "OnKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        mi!.Invoke(c, new object[] { new KeyEventArgs(keyData) });
    }

    // ===== 結合 =====

    [Fact]
    public void HighThenLow_InsertsOneCodePoint() =>
        Sta.Run(() =>
        {
            // prefix/suffix を置いて「ペアだけが入った」ことを両端で固定する(CLAUDE.md §4-B)。
            var (f, c) = MakeControl("ab");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKeyPress(c, Hi);
                SendKeyPress(c, Lo);
                Assert.Equal("a" + Emoji + "b", c.GetText());
                Assert.Equal(3, c.CaretCharOffset); // 1 + 2 UTF-16 単位
            }
        });

    [Fact]
    public void HighAlone_InsertsNothing_UntilLowArrives() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("ab");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKeyPress(c, Hi);
                // まだ何も入らない(U+FFFD も入らない)
                Assert.Equal("ab", c.GetText());
                Assert.Equal(1, c.CaretCharOffset);
            }
        });

    // ===== 破棄 =====

    [Fact]
    public void HighThenBmp_DropsHigh_InsertsBmpOnly() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("ab");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKeyPress(c, Hi);
                SendKeyPress(c, 'X');
                Assert.Equal("aXb", c.GetText()); // U+FFFD が残らないこと
            }
        });

    [Fact]
    public void LowAlone_InsertsNothing() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("ab");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKeyPress(c, Lo);
                Assert.Equal("ab", c.GetText());
            }
        });

    [Fact]
    public void HighThenHighThenLow_DropsFirstHigh() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("ab");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKeyPress(c, Hi);
                SendKeyPress(c, Hi);
                SendKeyPress(c, Lo);
                Assert.Equal("a" + Emoji + "b", c.GetText()); // ペアは 1 つだけ
            }
        });

    [Fact]
    public void HighThenKeyDown_DropsPending() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("ab");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                SendKeyPress(c, Hi);
                SendKeyDown(c, Keys.Right); // キー入力が挟まった
                SendKeyPress(c, Lo);
                Assert.Equal("ab", c.GetText()); // 何も入らない
            }
        });

    // ===== 上書きモード =====

    [Fact]
    public void Overtype_PairReplacesExactlyOneCodePoint() =>
        Sta.Run(() =>
        {
            // 監査書の「上書きモードでは既存 2 文字を潰す」の回帰。
            // prefix "a" / suffix "Yb" を置き、潰れるのが X 1 文字だけであることを両端で固定する。
            var (f, c) = MakeControl("aXYb");
            using (f)
            using (c)
            {
                c.Overtype = true;
                c.SetCaretCharOffset(1);
                SendKeyPress(c, Hi);
                SendKeyPress(c, Lo);
                Assert.Equal("a" + Emoji + "Yb", c.GetText());
            }
        });
}
```

**注意(実装者が検証すること):**

- `c.Overtype` に setter があるか。無ければ `Keys.Insert` の `SendKeyDown` でトグルする
  (`ClipboardTests.Insert_TogglesOvertype_StillWorks_AfterAddingCopyPaste` 参照)。
- `HostForm` は `Editor.Tests` の `TestHost.cs` にある(`CreateVisible()` ではなく `new HostForm()` で足りるかは
  `TextInsertionTests.MakeControl` に倣う=足りている)。
- `HighThenKeyDown_DropsPending` は**変更前の src では緑になりうる**
  (変更前は Hi の時点で U+FFFD が入るので `"ab"` にならない=赤)。Step 2 で必ず赤を確認する。

### Step 2: 赤を確認する

```
dotnet test tests/kxEdit.Editor.Tests -c Release --filter "FullyQualifiedName~SurrogatePairInputTests"
```

期待: **7 件すべて失敗**。失敗内容は「U+FFFD が入っている」系。
1 件でも緑なら、そのテストは何も守っていない。fixture を直してから進む。

### Step 3: 実装する

`src/kxEdit.Editor/EditorControl.Input.cs`:

```csharp
// A-20(設計 2026-08-29 §6): WM_CHAR は UTF-16 単位で届くため、サロゲートペアが
// 高・低の 2 通に分かれて到着する(KEYEVENTF_UNICODE の SendInput / PostMessage 経路)。
// 高位を 1 つだけ保留し、直後に低位が来たときだけペアで挿入する。
// 保留は「直後の WM_CHAR」にだけ効く契約(Scintilla の lastHighSurrogateChar と同じ)。
// '\0' = 保留なし。
private char _pendingHighSurrogate;

/// <summary>保留中の高サロゲートを捨てる。挿入以外の入力が挟まったら呼ぶ。</summary>
private void DropPendingHighSurrogate() => _pendingHighSurrogate = '\0';
```

`OnKeyDown` の先頭(`base.OnKeyDown(e)` の前後どちらでもよいが `_input.RouteKey` より前):

```csharp
protected override void OnKeyDown(KeyEventArgs e)
{
    base.OnKeyDown(e);
    DropPendingHighSurrogate(); // A-20: 文字挿入以外が挟まったら保留は無効
    _input.RouteKey(e);
}
```

`OnKeyPress` の本体:

```csharp
protected override void OnKeyPress(KeyPressEventArgs e)
{
    base.OnKeyPress(e);
    if (_buffer is null || ReadOnly)
        return;
    char ch = e.KeyChar;

    // 制御文字(0x00〜0x1F, 0x7F)は無視。編集用途は OnKeyDown 経路で処理する(§0-9 温存)。
    // サロゲート(U+D800〜U+DFFF)はこの条件に掛からないので、判定順は問わない。
    if (ch < 0x20 || ch == 0x7F)
    {
        DropPendingHighSurrogate();
        return;
    }

    // A-20: 高位は保留して待つ。連続して高位が来たら前の保留は捨てる。
    if (char.IsHighSurrogate(ch))
    {
        _pendingHighSurrogate = ch;
        e.Handled = true;
        return;
    }

    if (char.IsLowSurrogate(ch))
    {
        char hi = _pendingHighSurrogate;
        _pendingHighSurrogate = '\0';
        // 対になる高位が無い低位は捨てる(U+FFFD を本文に残さない=設計 §3.3)。
        if (hi != '\0')
            InsertConfirmedText(new string(new[] { hi, ch }));
        e.Handled = true;
        return;
    }

    DropPendingHighSurrogate(); // BMP 文字が来た=保留は対にならない
    InsertConfirmedText(ch.ToString());
    e.Handled = true;
}
```

`OnLostFocus` での破棄。`EditorControl.cs` に `OnLostFocus` の override が既にあるか
`grep -n "OnLostFocus" src/kxEdit.Editor/*.cs` で確認し、あれば `DropPendingHighSurrogate();` を足す。
無ければ追加せず、**`OnKeyDown` と非低サロゲート char の 2 経路だけで足りる**と判断してよい
(設計 §6.2: 破棄しそこねても次の非低サロゲート char で破棄されるので本文は壊れない)。
どちらにしたかを設計書 §10 に記録すること。

IME 開始時の破棄は `ImeController.OnStartComposition` が `_host.DeleteSelectionForImeStart()` を
呼ぶので、`EditorControl` 側のその実装に `DropPendingHighSurrogate();` を足せる。
**ただし IME 開始は必ず `OnKeyDown` を伴う**ので二重になる。実装者が現物を見て、
冗長なら足さずに理由を §10 に書くこと。

### Step 4: 緑を確認する

```
dotnet test tests/kxEdit.Editor.Tests -c Release --filter "FullyQualifiedName~SurrogatePairInputTests"
```

期待: 7 件 PASS。続けて既存テストの非退行:

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
```

期待: 全件 PASS・0 warning。とくに `TextInsertionTests` と `EditorControlImeTests` が緑であること
(IME 確定経路は `InsertConfirmedText` を直接呼ぶので影響しないはず=それを確認する)。

### Step 5: commit

```
git add src/kxEdit.Editor/EditorControl.Input.cs tests/kxEdit.Editor.Tests/SurrogatePairInputTests.cs
git commit -m "fix(editor): 分割到着するサロゲートペアを結合する(A-20)"
```

---

## Task 2: A-13 — `IClipboard` seam と `Copy` / `Paste` の `bool` 化

**このタスクは後続が依存する seam を作る = タスク時に別エージェントのコード品質レビューを行う(CLAUDE.md §3-4)。**

**Files:**
- Create: `src/kxEdit.Editor/Abstractions/IClipboard.cs`
- Create: `src/kxEdit.Editor/WinClipboard.cs`
- Modify: `src/kxEdit.Editor/EditorControl.cs`(`Copy` 1572-1580 / `Cut` 1593-1607 / `Paste` 1620-1636 / イベント宣言 1110-1116 付近)
- Create: `tests/kxEdit.Editor.Tests/Fakes/FakeClipboard.cs`
- Create: `tests/kxEdit.Editor.Tests/ClipboardFailureTests.cs`

### Step 1: seam を書く

`src/kxEdit.Editor/Abstractions/IClipboard.cs`(`IImeContext` と同じ namespace・同じ公開度):

```csharp
// IClipboard.cs
// A-13(設計 2026-08-29 §4): System.Windows.Forms.Clipboard は静的クラスで、
// 失敗経路(他プロセスがクリップボードを保持中の ExternalException)をテストから作れない。
// EditorControl が叩く 3 操作だけを切り出した seam。
// 本番実装 = WinClipboard。テスト実装 = FakeClipboard。
namespace kxEdit.Editor.Abstractions;

/// <summary>
/// <see cref="System.Windows.Forms.Clipboard"/> の UnicodeText 操作 seam。
/// 実装は <b>例外を握らない</b>(捕捉は呼び出し側 = EditorControl の責務)。
/// </summary>
public interface IClipboard
{
    /// <summary>UnicodeText 形式のデータがあるか。</summary>
    bool ContainsUnicodeText();

    /// <summary>UnicodeText を読む。無ければ空文字列。</summary>
    string GetUnicodeText();

    /// <summary>UnicodeText を書く。</summary>
    void SetUnicodeText(string text);
}
```

`src/kxEdit.Editor/WinClipboard.cs`:

```csharp
using System.Windows.Forms;
using kxEdit.Editor.Abstractions;

namespace kxEdit.Editor;

/// <summary>
/// <see cref="IClipboard"/> の本番実装。<see cref="Clipboard"/> をそのまま呼ぶだけで、
/// リトライも例外の握り潰しもしない(<see cref="Clipboard.SetText(string, TextDataFormat)"/> は
/// 内部で 10 回 × 100ms リトライ済み=設計書 §10)。
/// </summary>
internal sealed class WinClipboard : IClipboard
{
    public bool ContainsUnicodeText() => Clipboard.ContainsText(TextDataFormat.UnicodeText);

    public string GetUnicodeText() => Clipboard.GetText(TextDataFormat.UnicodeText);

    public void SetUnicodeText(string text) =>
        Clipboard.SetText(text, TextDataFormat.UnicodeText);
}
```

`src/kxEdit.Editor/EditorControl.cs` に注入点(`MainForm.SetLastSessionBuffersPathForTest` と同じ命名で揃える):

```csharp
// A-13: 既定は実クリップボード。テストだけが差し替える(失敗経路を作るため)。
private IClipboard _clipboard = new WinClipboard();

internal void SetClipboardForTest(IClipboard clipboard) =>
    _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
```

イベント(既存の `SavePointReached` / `SavePointLeft` / `UpdateUI` の並びに足す):

```csharp
/// <summary>
/// A-13: クリップボード操作が <see cref="System.Runtime.InteropServices.ExternalException"/> で
/// 失敗した(他プロセスがクリップボードを保持中など)。App 層が SR へ通知するための唯一の通知源。
/// Editor 層は <c>IAnnouncer</c> を参照できない(層の向きが逆になる)ためイベントで上へ渡す。
/// </summary>
public event EventHandler<ClipboardFailureKind>? ClipboardFailed;
```

`ClipboardFailureKind` は `src/kxEdit.Editor/ClipboardFailureKind.cs` に置く(または EditorControl.cs 末尾):

```csharp
namespace kxEdit.Editor;

/// <summary>どのクリップボード操作が失敗したか。App 層が文言を選ぶために使う。</summary>
public enum ClipboardFailureKind
{
    /// <summary>Copy / Cut の書き込みが失敗した。</summary>
    Write,

    /// <summary>Paste の読み取りが失敗した。</summary>
    Read,
}
```

### Step 2: 失敗するテストを書く

`tests/kxEdit.Editor.Tests/Fakes/FakeClipboard.cs`:

```csharp
using System.Runtime.InteropServices;
using kxEdit.Editor.Abstractions;

namespace kxEdit.Editor.Tests.Fakes;

/// <summary>
/// <see cref="IClipboard"/> のフェイク。<see cref="ThrowOnSet"/> /
/// <see cref="ThrowOnGet"/> で ExternalException を注入して A-13 の失敗経路を作る。
/// 実クリップボード(プロセス横断のグローバル資源)を触らないので LocalOnly 化が不要。
/// </summary>
public sealed class FakeClipboard : IClipboard
{
    public string Text { get; set; } = "";
    public bool HasText { get; set; }
    public bool ThrowOnSet { get; set; }
    public bool ThrowOnGet { get; set; }
    public bool ThrowOnContains { get; set; }
    public int SetCount { get; private set; }

    public bool ContainsUnicodeText()
    {
        if (ThrowOnContains)
            throw new ExternalException("clipboard busy");
        return HasText;
    }

    public string GetUnicodeText()
    {
        if (ThrowOnGet)
            throw new ExternalException("clipboard busy");
        return Text;
    }

    public void SetUnicodeText(string text)
    {
        SetCount++;
        if (ThrowOnSet)
            throw new ExternalException("clipboard busy");
        Text = text;
        HasText = true;
    }
}
```

`tests/kxEdit.Editor.Tests/ClipboardFailureTests.cs`:

```csharp
using kxEdit.Core.Text;
using kxEdit.Editor.Tests.Fakes;

namespace kxEdit.Editor.Tests;

/// <summary>
/// A-13(監査 2026-08-22 / 設計 2026-08-29 §4): クリップボードが他プロセスに保持されている
/// 間の ExternalException を発生源で捕捉する契約テスト。
/// 実機再現: 別プロセスで OpenClipboard(NULL) を保持したまま Ctrl+C(設計書 §2.1)。
/// FakeClipboard を使うので実クリップボードを触らない = LocalOnly ではない。
/// </summary>
public class ClipboardFailureTests
{
    private static (Form f, EditorControl c, FakeClipboard cb) MakeControl(string text)
    {
        var f = new HostForm();
        var c = new EditorControl();
        var cb = new FakeClipboard();
        c.SetClipboardForTest(cb);
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c, cb);
    }

    // ===== Copy =====

    [Fact]
    public void Copy_ClipboardBusy_ReturnsFalse_AndRaisesEventOnce() =>
        Sta.Run(() =>
        {
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                cb.ThrowOnSet = true;
                var kinds = new List<ClipboardFailureKind>();
                c.ClipboardFailed += (_, k) => kinds.Add(k);

                c.SetSelectionCharRange(1, 4);
                Assert.False(c.Copy());
                Assert.Equal("hello", c.GetText()); // 本文不変
                Assert.Equal(new[] { ClipboardFailureKind.Write }, kinds);
            }
        });

    [Fact]
    public void Copy_Success_DoesNotRaiseEvent() =>
        Sta.Run(() =>
        {
            // no-change のテスト。非既定位置(1..4)から始める(CLAUDE.md §4-B)。
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                int raised = 0;
                c.ClipboardFailed += (_, _) => raised++;
                c.SetSelectionCharRange(1, 4);
                Assert.True(c.Copy());
                Assert.Equal("ell", cb.Text);
                Assert.Equal(0, raised);
            }
        });

    // ===== Cut(いちばん重要な回帰)=====

    [Fact]
    public void Cut_ClipboardBusy_DoesNotDeleteText() =>
        Sta.Run(() =>
        {
            // 既存契約(EditorControl.cs の Cut remarks):
            // 「クリップボードに書けなかったら本文を消さない」。
            // Copy の中で例外を握り潰すとここが壊れ、A-13 より重いデータ喪失に化ける。
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                cb.ThrowOnSet = true;
                c.SetSelectionCharRange(1, 4);
                c.Cut();
                Assert.Equal("hello", c.GetText()); // 本文が残っていること
                Assert.Equal((1, 4), c.GetSelectionCharRange()); // 選択も残ること
            }
        });

    [Fact]
    public void Cut_Success_StillDeletesText() =>
        Sta.Run(() =>
        {
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(1, 4);
                c.Cut();
                Assert.Equal("ho", c.GetText());
                Assert.Equal("ell", cb.Text);
            }
        });

    // ===== Paste =====

    [Fact]
    public void Paste_ContainsThrows_ReturnsFalse_AndRaisesRead() =>
        Sta.Run(() =>
        {
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                cb.ThrowOnContains = true;
                var kinds = new List<ClipboardFailureKind>();
                c.ClipboardFailed += (_, k) => kinds.Add(k);
                c.SetCaretCharOffset(2);
                Assert.False(c.Paste());
                Assert.Equal("hello", c.GetText());
                Assert.Equal(new[] { ClipboardFailureKind.Read }, kinds);
            }
        });

    [Fact]
    public void Paste_GetThrows_ReturnsFalse_AndRaisesRead() =>
        Sta.Run(() =>
        {
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                cb.HasText = true; // Contains は通り Get で落ちる
                cb.ThrowOnGet = true;
                var kinds = new List<ClipboardFailureKind>();
                c.ClipboardFailed += (_, k) => kinds.Add(k);
                c.SetCaretCharOffset(2);
                Assert.False(c.Paste());
                Assert.Equal("hello", c.GetText());
                Assert.Equal(new[] { ClipboardFailureKind.Read }, kinds);
            }
        });

    [Fact]
    public void Paste_EmptyClipboard_ReturnsFalse_WithoutRaising() =>
        Sta.Run(() =>
        {
            // 「空で no-op」は失敗ではない = イベントを上げない(既定と区別する)。
            var (f, c, cb) = MakeControl("hello");
            using (f)
            using (c)
            {
                cb.HasText = false;
                int raised = 0;
                c.ClipboardFailed += (_, _) => raised++;
                c.SetCaretCharOffset(2);
                Assert.False(c.Paste());
                Assert.Equal(0, raised);
            }
        });
}
```

**実装者が検証すること:** `Paste_EmptyClipboard_ReturnsFalse_WithoutRaising` の
「空クリップボードで `false`」は設計判断。`true`(=正常に no-op した)にする案もある。
戻り値の意味を「クリップボード操作が成功したか」ではなく「本文を変えたか」に寄せると
`Cut` の判定と食い違う。**`Copy` / `Paste` の戻り値は「クリップボード操作が成功したか」**で統一し、
空クリップボードは「成功したが何も無かった」= `true` が筋。上のテストは
`Assert.True` に直すべきかもしれない。**実装前に決めて、決めた根拠を XML doc に書くこと。**

### Step 3: 赤を確認する

```
dotnet test tests/kxEdit.Editor.Tests -c Release --filter "FullyQualifiedName~ClipboardFailureTests"
```

期待: `Copy` / `Paste` が `bool` を返さないのでそもそも**コンパイルエラー**。
seam(Step 1)を先に入れてあれば `SetClipboardForTest` は通り、
`Assert.False(c.Copy())` の行で CS0029 等になる。これが「赤」の形。

### Step 4: `Copy` / `Cut` / `Paste` を書き換える

```csharp
public bool Copy()
{
    if (_buffer is null)
        return false;
    var (s, en) = GetSelectionCharRange();
    if (s == en)
        return false;
    string text = _buffer.Current.GetText(s, en - s);
    try
    {
        _clipboard.SetUnicodeText(text);
    }
    catch (ExternalException)
    {
        // A-13: 他プロセスがクリップボードを保持中。本文には触っていないので状態は無傷。
        ClipboardFailed?.Invoke(this, ClipboardFailureKind.Write);
        return false;
    }
    return true;
}

public void Cut()
{
    if (IsComposing)
        CancelCompositionAndDefault();
    if (_buffer is null || ReadOnly)
        return;
    var (s, en) = GetSelectionCharRange();
    if (s == en)
        return;
    // A-13: Copy が false のときは本文を消さない。これは既存の throw 契約の置き換えで、
    // 「クリップボードに入っていないのに本文が消える」事故を防ぐ唯一の砦。
    if (!Copy())
        return;
    _buffer.Replace(s, en - s, "");
    _caretCtrl.SetTo(s, _buffer.Current);
    _caretCtrl.DesiredXpx = -1;
    AfterEdit();
}

public bool Paste()
{
    if (IsComposing)
        CancelCompositionAndDefault();
    if (_buffer is null || ReadOnly)
        return false;
    string text;
    try
    {
        if (!_clipboard.ContainsUnicodeText())
            return true; // 空 = 失敗ではない(no-op)
        text = _clipboard.GetUnicodeText();
    }
    catch (ExternalException)
    {
        ClipboardFailed?.Invoke(this, ClipboardFailureKind.Read);
        return false;
    }
    if (string.IsNullOrEmpty(text))
        return true;
    var (s, en) = GetSelectionCharRange();
    _buffer.Replace(s, en - s, text);
    _caretCtrl.SetTo(s + text.Length, _buffer.Current);
    _caretCtrl.DesiredXpx = -1;
    AfterEdit();
    return true;
}
```

`Cut` の戻り値は `void` のままにする(呼び出し側は誰も見ない・`Copy` が既にイベントを上げている)。

`using System.Runtime.InteropServices;` を `EditorControl.cs` の using に足す。

**既存の XML doc(`Cut` の remarks)を必ず更新する。** 現行の
「`Clipboard.SetText` が例外を投げると本メソッドも上に throw して `AfterEdit` へ到達しない」は
もう成立しない。新しい根拠(`Copy` の戻り値で止める)に書き換える。
古い説明を残すと、次にここを読む人が誤った不変条件を信じる。

### Step 5: 緑を確認する

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
```

期待: 全件 PASS・0 warning。既存 `ClipboardTests`(LocalOnly・実クリップボード)も緑であること
= 本番経路が `WinClipboard` 経由に変わっても挙動が同じことの確認。

### Step 6: commit + コード品質レビュー

```
git add src/kxEdit.Editor tests/kxEdit.Editor.Tests
git commit -m "fix(editor): クリップボード失敗を発生源で捕捉する(A-13・Editor 層)"
```

commit 後、**別エージェントにコード品質レビューを依頼する**(CLAUDE.md §3-4)。
観点: `IClipboard` の粒度・`SetClipboardForTest` の可視性・`Copy` の戻り値の意味・
`Cut` の XML doc が新しい不変条件を正しく説明しているか。

---

## Task 3: A-13 — App 層の配線と発声

**Files:**
- Modify: `src/kxEdit.App/DocumentManager.cs`(イベント宣言 48 付近 / `CreateNew` の購読 82-83)
- Modify: `src/kxEdit.App/MainForm.cs`(購読 153-160 付近)
- Test: `tests/kxEdit.App.Tests/DocumentManagerTests.cs`(追記)

### Step 1: 失敗するテストを書く

`DocumentManagerTests.cs` の末尾に足す:

```csharp
// ===== A-13: ClipboardFailed の再送 =====

[Fact]
public void ClipboardFailed_FromAnyEditor_IsForwarded() =>
    Sta.Run(() =>
    {
        using var host = new Host();
        var doc1 = host.Docs.CreateNew();
        var doc2 = host.Docs.CreateNew(); // 非アクティブ側からも飛ぶこと
        var kinds = new List<ClipboardFailureKind>();
        host.Docs.ClipboardFailed += (_, k) => kinds.Add(k);

        doc1.Editor.RaiseClipboardFailedForTest(ClipboardFailureKind.Write);
        doc2.Editor.RaiseClipboardFailedForTest(ClipboardFailureKind.Read);

        Assert.Equal(
            new[] { ClipboardFailureKind.Write, ClipboardFailureKind.Read },
            kinds
        );
    });
```

**実装者が検証すること:** `RaiseClipboardFailedForTest` という internal フックを
`EditorControl` に足すか、`FakeClipboard` を注入して実経路で起こすかの二択。
`kxEdit.Editor` の `InternalsVisibleTo` に `kxEdit.App.Tests` は**入っていない**
(現状 `kxEdit.Editor.Smoke` と `kxEdit.Editor.Tests` のみ)。
`SetClipboardForTest` も `RaiseClipboardFailedForTest` も App.Tests からは見えない。

したがって次のどちらかを選ぶ:

- (a) `kxEdit.Editor.csproj` の `InternalsVisibleTo` に `kxEdit.App.Tests` を足す
- (b) `EditorControl.ClipboardFailed` を **public event** のまま、テストからは
  `EditorControl` を継承したテスト用サブクラスで発火させる(`sealed` なので不可)
- (c) `IClipboard` を public にしてあるので、`SetClipboardForTest` だけを
  `public` にはせず、**App.Tests では DocumentManager の再送を直接検証せず**、
  MainForm 側の文言だけを検証する

**(a) を推奨**。理由: 既存の `InternalsVisibleTo` 追加は Editor.Tests / Editor.Smoke で前例があり、
テスト専用 seam を public に昇格させるより副作用が小さい。決めた根拠を設計書 §10 に記録すること。

### Step 2: 赤を確認する

```
dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~ClipboardFailed"
```

期待: `DocumentManager.ClipboardFailed` が無いのでコンパイルエラー。

### Step 3: 実装する

`src/kxEdit.App/DocumentManager.cs`:

```csharp
/// <summary>A-13: いずれかの文書でクリップボード操作が失敗した。
/// <see cref="ActiveDirtyChanged"/> と違いアクティブ限定にしない
/// (失敗した操作は必ずユーザーの直前の操作 = そのタブがアクティブ、だが
/// 将来の非アクティブ経路でも取りこぼさない)。MainForm が Announcer へ流す。</summary>
public event EventHandler<ClipboardFailureKind>? ClipboardFailed;
```

`CreateNew` の購読列(`editor.SavePointLeft += ...` の並び)に:

```csharp
editor.ClipboardFailed += (_, kind) => ClipboardFailed?.Invoke(this, kind);
```

**`CreateNew` 以外に `EditorControl` を作る経路が無いか必ず確認する。**
`grep -n "_editorFactory()" src/kxEdit.App/DocumentManager.cs` で全箇所を洗い、
すべてに購読を足す(1 箇所だけ足して他が漏れるのが典型的な事故)。

`src/kxEdit.App/MainForm.cs`(`_docs.KeyBasedSwitch += ...` の隣):

```csharp
// A-13: クリップボードが他プロセスに保持されていると Copy/Cut/Paste が失敗する。
// 発生源(EditorControl)で捕捉済み = ここでは通知だけ行う。
_docs.ClipboardFailed += (_, kind) =>
    _announcer.Say(
        kind == ClipboardFailureKind.Write
            ? "クリップボードにコピーできません。他のアプリが使用中の可能性があります"
            : "クリップボードから貼り付けられません。他のアプリが使用中の可能性があります"
    );
```

### Step 4: 緑を確認する

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

### Step 5: commit

```
git add src/kxEdit.App tests/kxEdit.App.Tests src/kxEdit.Editor/kxEdit.Editor.csproj
git commit -m "fix(app): クリップボード失敗をSRへ通知する(A-13・App 層配線)"
```

---

## Task 4: M-1 — `CrashHandler` と `Program` の配線

**このタスクはプロセス終了経路の新しい抽象を作る = タスク時に別エージェントのコード品質レビューを行う。**

**Files:**
- Create: `src/kxEdit.App/CrashHandler.cs`
- Modify: `src/kxEdit.App/Program.cs`
- Modify: `src/kxEdit.App/MainForm.cs`(`FlushBackupsForCrash` を追加)
- Create: `tests/kxEdit.App.Tests/CrashHandlerTests.cs`

### Step 1: 失敗するテストを書く

`tests/kxEdit.App.Tests/CrashHandlerTests.cs`:

```csharp
namespace kxEdit.App.Tests;

/// <summary>
/// M-1(監査 2026-08-22 / 設計 2026-08-29 §5): 未処理例外を握って
/// 「退避 → 通知 → 終了」の順に処理する。
/// 実機での必要性: WinForms 既定の未処理例外ダイアログの「終了」は保存確認の
/// キャンセルを無視して落ち、hot exit バックアップが書かれないことがある(設計書 §2.1)。
/// </summary>
public class CrashHandlerTests
{
    private sealed class FakeSink : ICrashSink
    {
        public List<string> Calls { get; } = new();
        public bool FlushResult { get; set; } = true;
        public bool? NotifiedFlushed { get; private set; }

        public bool FlushBackups()
        {
            Calls.Add("flush");
            return FlushResult;
        }

        public void Notify(bool flushed, Exception? ex)
        {
            Calls.Add("notify");
            NotifiedFlushed = flushed;
        }

        public void Exit()
        {
            Calls.Add("exit");
        }
    }

    [Fact]
    public void Handle_CallsFlushThenNotifyThenExit()
    {
        var sink = new FakeSink();
        new CrashHandler(sink).Handle(new InvalidOperationException("boom"));
        Assert.Equal(new[] { "flush", "notify", "exit" }, sink.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Handle_PassesFlushResultToNotify(bool flushed)
    {
        var sink = new FakeSink { FlushResult = flushed };
        new CrashHandler(sink).Handle(new InvalidOperationException("boom"));
        Assert.Equal(flushed, sink.NotifiedFlushed);
    }

    [Fact]
    public void Handle_Twice_ExitsOnlyOnce()
    {
        // 再入ガード。ハンドラ内で再び例外が出ても無限ループしない。
        var sink = new FakeSink();
        var h = new CrashHandler(sink);
        h.Handle(new InvalidOperationException("first"));
        h.Handle(new InvalidOperationException("second"));
        Assert.Equal(1, sink.Calls.Count(x => x == "exit"));
        Assert.Equal(1, sink.Calls.Count(x => x == "flush"));
    }

    [Fact]
    public void Handle_FlushThrows_StillNotifiesAndExits()
    {
        // 退避で落ちても通知と終了までは必ず到達する(ここで止まると
        // 「例外ダイアログも出ずに固まる」= 既定挙動より悪くなる)。
        var sink = new ThrowingFlushSink();
        new CrashHandler(sink).Handle(new InvalidOperationException("boom"));
        Assert.Equal(new[] { "notify", "exit" }, sink.Calls);
        Assert.False(sink.NotifiedFlushed);
    }

    private sealed class ThrowingFlushSink : ICrashSink
    {
        public List<string> Calls { get; } = new();
        public bool? NotifiedFlushed { get; private set; }

        public bool FlushBackups() => throw new InvalidOperationException("flush failed");

        public void Notify(bool flushed, Exception? ex)
        {
            Calls.Add("notify");
            NotifiedFlushed = flushed;
        }

        public void Exit() => Calls.Add("exit");
    }
}
```

### Step 2: 赤を確認する

```
dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~CrashHandlerTests"
```

期待: `CrashHandler` / `ICrashSink` が無いのでコンパイルエラー。

### Step 3: 実装する

`src/kxEdit.App/CrashHandler.cs`:

```csharp
namespace kxEdit.App;

/// <summary>
/// M-1(設計 2026-08-29 §5.4): <see cref="CrashHandler"/> の副作用 seam。
/// 本番実装は MainForm とプロセス終了に触るため、順序の検証はこの interface 越しに行う。
/// </summary>
public interface ICrashSink
{
    /// <summary>編集中の本文を退避する。true = 退避できたと言い切れる。</summary>
    bool FlushBackups();

    /// <summary>ユーザーへ通知する。<paramref name="flushed"/> で文面を変える。</summary>
    void Notify(bool flushed, Exception? ex);

    /// <summary>プロセスを終了する。</summary>
    void Exit();
}

/// <summary>
/// M-1: 未処理例外を「退避 → 通知 → 終了」に一本化する。
/// WinForms 既定の未処理例外ダイアログには到達させない(設計 §3.2)。
/// 「続行」は出さない = 壊れた状態で走り続けるより、退避して落ちるほうが結果が読める。
/// </summary>
public sealed class CrashHandler
{
    private readonly ICrashSink _sink;
    private int _entered;

    public CrashHandler(ICrashSink sink) =>
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <summary>
    /// 未処理例外を処理する。<b>2 回目以降は何もしない</b>
    /// (ハンドラ内で再び例外が出ても無限ループしない)。
    /// </summary>
    public void Handle(Exception? ex)
    {
        if (Interlocked.Exchange(ref _entered, 1) != 0)
            return;

        bool flushed = false;
        try
        {
            flushed = _sink.FlushBackups();
        }
        catch
        {
            // 退避で落ちても通知と終了までは必ず到達させる。ここで throw すると
            // 「例外ダイアログも出ずに固まる」= WinForms 既定より悪くなる。
        }

        try
        {
            _sink.Notify(flushed, ex);
        }
        catch
        {
            // 通知に失敗しても終了は行う。
        }

        _sink.Exit();
    }
}
```

`src/kxEdit.App/MainForm.cs` に internal メソッドを追加:

```csharp
/// <summary>
/// M-1: 未処理例外からの退避。<see cref="BackupCoordinator.FinalFlushForRestore"/> と
/// <see cref="BackupCoordinator.WaitForFinalFlush"/> は<b>必ず対でこの順に</b>呼ぶ
/// (BackupCoordinator の WaitForFinalFlush remarks を参照)。
/// <b>UI スレッドから呼ぶこと</b>(BackupCoordinator は UI スレッド専有)。
/// </summary>
internal bool FlushBackupsForCrash()
{
    _backup.FinalFlushForRestore();
    return _backup.WaitForFinalFlush();
}
```

本番 sink(`Program.cs` 内の private sealed class でよい):

```csharp
private sealed class MainFormCrashSink(MainForm form) : ICrashSink
{
    public bool FlushBackups()
    {
        // AppDomain.UnhandledException は任意のスレッドで発火する。BackupCoordinator は
        // UI スレッド専有なので marshal する。UI スレッドが死んでいる/ブロックされている
        // 場合に戻ってこないため、タイムアウトで諦める(設計 §5.3)。
        if (!form.IsHandleCreated || form.IsDisposed)
            return false;
        if (!form.InvokeRequired)
            return form.FlushBackupsForCrash();

        bool result = false;
        var done = new ManualResetEventSlim(false);
        try
        {
            form.BeginInvoke(() =>
            {
                try
                {
                    result = form.FlushBackupsForCrash();
                }
                finally
                {
                    done.Set();
                }
            });
        }
        catch (InvalidOperationException)
        {
            return false; // ハンドル破棄と競合した
        }
        return done.Wait(TimeSpan.FromSeconds(5)) && result;
    }

    public void Notify(bool flushed, Exception? ex) =>
        MessageBox.Show(
            flushed
                ? "予期しないエラーが発生したため kxEdit を終了します。\n"
                    + "編集中の内容は退避したので、次回起動時に復元できます。"
                : "予期しないエラーが発生したため kxEdit を終了します。\n"
                    + "編集中の内容を退避できなかった可能性があります。",
            "kxEdit",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error
        );

    public void Exit() => Environment.Exit(1);
}
```

`src/kxEdit.App/Program.cs`:

```csharp
[STAThread]
static void Main()
{
    EncodingCatalog.EnsureRegistered();
    var markdigVersion = typeof(Markdig.Markdown).Assembly.GetName().Version;
    Trace.TraceInformation($"kxEdit deps: Markdig={markdigVersion}");
    var settings = SettingsStore.Load(SettingsStore.DefaultPath);
    ApplicationConfiguration.Initialize();

    // M-1(設計 2026-08-29 §5): WinForms 既定の未処理例外ダイアログに到達させない。
    // SetUnhandledExceptionMode はウィンドウ生成前に呼ぶ必要がある = MainForm より前。
    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

    var form = new MainForm(settings);
    var crash = new CrashHandler(new MainFormCrashSink(form));
    Application.ThreadException += (_, e) => crash.Handle(e.Exception);
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        // IsTerminating=false(現状 .NET では実質来ない)では既存の続行を邪魔しない。
        if (e.IsTerminating)
            crash.Handle(e.ExceptionObject as Exception);
    };

    Application.Run(form);
}
```

**実装者が検証すること:**

- `Program` は現在 `static class Program` で `Main` のみ。`MainFormCrashSink` を
  `Program` の入れ子にするか別ファイルにするかは実装者の判断。
  `Program.cs` は App.Tests から触れない(テストは `CrashHandler` 側で担保する)。
- `form.BeginInvoke(Action)` のオーバーロードが .NET 9 の `Control` にあるか
  (`BeginInvoke(Delegate)` しか無ければ `new Action(...)` で包む)。
- `MessageBox.Show` を非 UI スレッドから呼ぶと別のメッセージループが立つ。
  `Notify` も marshal すべきか、それとも「もう落ちる直前なので素で出す」でよいかを決めて
  設計書 §10 に記録する。

### Step 4: 緑を確認する

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

### Step 5: 手動スモーク(自動テストで代替できない)

Release ビルドを起動し、別プロセスでクリップボードを 12 秒保持した状態で Ctrl+C する。

```powershell
# 保持側(別ウィンドウで実行)
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;
public static class C{[DllImport("user32.dll")]public static extern bool OpenClipboard(IntPtr h);
[DllImport("user32.dll")]public static extern bool CloseClipboard();}' -Language CSharp
[void][C]::OpenClipboard([IntPtr]::Zero); Start-Sleep 12; [void][C]::CloseClipboard()
```

期待:

- **未処理例外ダイアログが出ない**(Task 2/3 の効果)
- 「クリップボードにコピーできません…」が発声される
- 本文と選択が残っている

### Step 6: commit + コード品質レビュー

```
git add src/kxEdit.App tests/kxEdit.App.Tests
git commit -m "fix(app): 未処理例外を退避してから終了する(M-1)"
```

commit 後、**別エージェントにコード品質レビューを依頼する**。
観点: `ICrashSink` の粒度・再入ガードの正しさ・非 UI スレッドからの marshal とタイムアウト・
`Environment.Exit` を選んだ根拠(`Application.Exit` との違い)。

---

## 最終ブランチレビュー(CLAUDE.md §3-5)

**コード品質パス**と**脆弱性パス**を、**それぞれ独立した別エージェント**で実施する
(1 起動に混載しない)。指摘は fixup commit で反映し、元 commit は書き換えない。

- コード品質パス: `Cut` の不変条件が新しい形で守られているか / `IClipboard` seam の粒度 /
  サロゲート保留の寿命(設計 §6.2 の「列挙は漏れる」への対処)/ XML doc と実装の一致
- 脆弱性パス: 例外ハンドラ内でのユーザー入力の扱い(例外メッセージをそのまま
  MessageBox に出していないか)/ `Environment.Exit` 前に握るリソース

ミューテーション検証は**行わない**(設計 §8.4)。

## 品質ゲート(CLAUDE.md §6)

```
powershell -File tools\pre-merge-check.ps1
```

**EXIT 0** と 0 warning を確認する。

## L5(実機 SR 検証)

設計書 §9 の 6 項目を実施する。`docs/plans/2026-08-29-unhandled-exception-safety-l5-checklist.md` に
チェックリストを作り、結果を記録する。

## PR(CLAUDE.md §7)

description に次を必ず書く:

- 監査書 §4 の A-20 の説明(絵文字パネルで U+FFFD 化)が**事実と違った**こと、
  および現在の正は設計書 §2.2 であること
- A-13 が監査書の記述より重い(キャンセルが効かない・バックアップが不定)こと
- 残りの未対応項目と実害順(設計書 §11)
