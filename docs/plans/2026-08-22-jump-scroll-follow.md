# ジャンプ/選択の追従スクロール(A-3 / M-32 / A-12)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 検索ジャンプ・Ctrl+G・grep ジャンプ・UIA `Select()` で画面がキャレットへ追従するようにし、
併せて UIA 矩形の水平ずれ(A-12)を直す。

**Architecture:** `EditorControl` の「絶対位置を外から指定する」2 つの setter
(`SetCaretCharOffset` / `SetSelectionCharRange`)の末尾に `BringCaretIntoView()` を足す。
アンカー相対で動かす 2 つ(`SetSelectionAnchored` / `MoveCaretWithSelection`)は据え置き
= Ctrl+A の非スクロール契約を保つ。UIA `Select()` は前者を通るため M-32 も同時に解消する。
A-12 は `UiaTextHostAdapter.ComputeBoundingRectangles` で X から `ScrollX` を引くだけ。

**Tech Stack:** C# / .NET 9 / WinForms / xUnit(STA)/ CSharpier / Husky.Net

**設計書:** [`2026-08-22-jump-scroll-follow-design.md`](./2026-08-22-jump-scroll-follow-design.md)
**起点:** ブランチ `feature/jump-scroll-follow` / main = `35d8eb9` / 設計書 commit = `c03c94a`

---

## 前提知識(この計画を実行する人向け)

### プロジェクト規範(CLAUDE.md より抜粋)

- **コミットメッセージ本文・コメント・ドキュメントは日本語**。コード・識別子は英語。
- 各タスク = 実装 → **仕様レビュー**(別エージェント)。指摘を反映してから次タスクへ。
- **0 warning 維持**(`-warnaserror` 稼働中)。ビルドが警告 1 個で落ちる。
- pre-commit フック(CSharpier 整形 + ローカルパス検出)を `--no-verify` で飛ばさない。
- テスト数を文書に書かない(正はテスト実行結果)。

### ビルド / テストコマンド

```bash
# ビルド(Release・警告=エラー)
dotnet build kxEdit.sln -c Release -warnaserror

# 個別テストプロジェクト
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests    -c Release --no-build
dotnet test tests/kxEdit.Core.Tests   -c Release --no-build

# 単一テストを走らせる(赤/緑の確認用)
dotnet test tests/kxEdit.Editor.Tests -c Release --filter "FullyQualifiedName~CaretScrollTests.SetCaretCharOffset_ScrollsCaretIntoView"

# マージ前の品質ゲート(pwsh 推奨)
pwsh tools/pre-merge-check.ps1
```

**注意**: `--filter` で絞ったままミューテーション検証を行うと結論を誤る(PR #43 の教訓)。
ミューテーション検証はプロジェクト単位で回すこと。

### テストの書き方(このリポジトリの流儀)

- WinForms コントロールを触るテストは **必ず `Sta.Run(() => { ... })` で包む**(STA スレッド required)。
- `Enumerable` を使うなら `using System.Linq;` をファイル先頭に足す(global usings に無い)。
- Editor.Tests の global usings: `System` / `System.Threading` / `System.Windows.Forms` /
  `kxEdit.Core.Buffers` / `kxEdit.Editor` / `Xunit`。
- **no-change(変化しないこと)のテストは非既定位置・非既定状態から検証を始める**
  (既定値と区別するため。CLAUDE.md §4)。本計画では `TopLine = 0` ではなく `TopLine = 3` などから始める。

---

## Task 1: 既存 `CaretScrollTests` の網を setter 変更に耐える形へ組み替える

**なぜ最初にやるか**: 対象 3 本は「`SetCaretCharOffset` で caret を置く → `BringCaretIntoView()` →
`TopLine` を検証」という順。Task 2 で setter 側が先にスクロールするようになると、
**`BringCaretIntoView` の実装を壊しても緑のまま**になる(vacuous 化)。
先に「caret を置く → **後から** `TopLine` をずらす → `BringCaretIntoView()`」へ組み替える。

このタスクは **src を一切触らない**。組み替え後も緑であること = 挙動不変の証明になる。

**Files:**
- Modify: `tests/kxEdit.Editor.Tests/CaretScrollTests.cs`

### Step 1: 組み替え前に緑であることを確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
```

Expected: 全緑(ベースライン)。

### Step 2: `BringCaretIntoView_ScrollsDown_WhenCaretBelowVisible` を組み替える

`tests/kxEdit.Editor.Tests/CaretScrollTests.cs` の該当箇所(現在 47〜56 行目付近)。

変更前:

```csharp
                c.TopLine = 0;
                int lineHeight = c.LineHeightPx;
                int visibleRows = Math.Max(1, c.ClientSize.Height / lineHeight);

                // 末尾行(index 9)にキャレットを置いて BringCaretIntoView 呼び出し
                int lineStart = text.LastIndexOf('\n') + 1;
                c.SetCaretCharOffset(lineStart);
                c.BringCaretIntoView();
```

変更後:

```csharp
                int lineHeight = c.LineHeightPx;
                int visibleRows = Math.Max(1, c.ClientSize.Height / lineHeight);

                // 末尾行(index 9)にキャレットを置いてから TopLine を先頭へ戻し、
                // BringCaretIntoView 単体がスクロールを起こすことを検証する。
                // 順序が逆(TopLine=0 → SetCaretCharOffset)だと、setter 自身の追従スクロール
                // (A-3 修正)で先に TopLine が動いてしまい、BringCaretIntoView を壊しても
                // 緑のまま通る=網が vacuous になる。
                int lineStart = text.LastIndexOf('\n') + 1;
                c.SetCaretCharOffset(lineStart);
                c.TopLine = 0; // ★ caret を置いた「後」に可視域を先頭へ戻す
                c.BringCaretIntoView();
```

### Step 3: `BringCaretIntoView_ScrollsUp_WhenCaretAboveVisible` を組み替える

現在 74〜78 行目付近。

変更前:

```csharp
                c.TopLine = 5; // 可視領域を下方向にずらす

                // 先頭行にキャレットを置いて呼び出し
                c.SetCaretCharOffset(0);
                c.BringCaretIntoView();
```

変更後:

```csharp
                // 先頭行にキャレットを置いてから可視領域を下方向へずらす
                // (順序が逆だと setter 自身の追従スクロールで網が vacuous になる=Task 1 参照)。
                c.SetCaretCharOffset(0);
                c.TopLine = 5; // ★ caret を置いた「後」に可視域をずらす
                c.BringCaretIntoView();
```

### Step 4: `BringCaretIntoView_ScrollsDown_WhenCaretHiddenByHScrollBar` を組み替える

**Task 7 レビュー I-1 の回帰テスト**なので特に丁寧に。現在 251〜257 行目付近。

変更前:

```csharp
                c.WrapColumns = 0; // 折り返し OFF(念のため明示・既定 0)
                c.TopLine = 0;

                // 論理行 9(末尾)にキャレット
                int line9Start = text.LastIndexOf('\n') + 1;
                c.SetCaretCharOffset(line9Start);
                c.BringCaretIntoView();
```

変更後:

```csharp
                c.WrapColumns = 0; // 折り返し OFF(念のため明示・既定 0)

                // 論理行 9(末尾)にキャレットを置いてから TopLine を先頭へ戻す
                // (順序が逆だと setter 自身の追従スクロールで網が vacuous になる=Task 1 参照)。
                int line9Start = text.LastIndexOf('\n') + 1;
                c.SetCaretCharOffset(line9Start);
                c.TopLine = 0; // ★ caret を置いた「後」に可視域を先頭へ戻す
                c.BringCaretIntoView();
```

### Step 5: 組み替え後も緑であることを確認する(挙動不変の証明)

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
```

Expected: 全緑。**ここで赤が出たら組み替えを間違えている**(まだ src は変えていないので、
赤 = fixture の前提が壊れた)。

### Step 6: 組み替えた 3 本が本当に網になっていることをミューテーションで確認する

`src/kxEdit.Editor/EditorControl.Caret.cs` の `BringCaretIntoView()` 冒頭に一時的に
`return;` を追加(`if (_buffer is null) return;` の直後):

```csharp
    public void BringCaretIntoView()
    {
        if (_buffer is null)
            return;
        return; // ★ 一時的な変異
        var snap = _buffer.Current;
```

`return;` の後に到達不能コードがあると CS0162 警告 → `-warnaserror` で落ちるため、
変異は `-p:TreatWarningsAsErrors=false` で走らせる:

```bash
dotnet build kxEdit.sln -c Release -p:TreatWarningsAsErrors=false
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build --filter "FullyQualifiedName~CaretScrollTests"
```

Expected: **`BringCaretIntoView_ScrollsDown_WhenCaretBelowVisible` /
`_ScrollsUp_WhenCaretAboveVisible` / `_ScrollsDown_WhenCaretHiddenByHScrollBar` の 3 本が赤**。

確認後、**必ず変異を元に戻す**(過去に「レビューエージェントが変異を戻さず返す」事故あり)。

```bash
git diff src/   # 空であること
```

### Step 7: コミット

```bash
git add tests/kxEdit.Editor.Tests/CaretScrollTests.cs
git commit -m "test(editor): CaretScrollTests の網を setter 追従に耐える形へ組み替える

A-3 の修正で SetCaretCharOffset が自ら追従スクロールするようになると、
「TopLine を先に 0 にしてから caret を置く」順の 3 本は setter 側で
既にスクロール済みになり、BringCaretIntoView を壊しても緑のまま通る
(vacuous 化)。caret を置いた後に TopLine をずらす順へ組み替えて網を維持する。

src は未変更=組み替え後も全緑であることが挙動不変の証明。
BringCaretIntoView に early return を入れる変異で 3 本が赤化することを確認済み。"
```

---

## Task 2: `SetCaretCharOffset` / `SetSelectionCharRange` に追従スクロールを足す(A-3 中核)

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.Caret.cs`(`SetCaretCharOffset` / `SetSelectionCharRange`)
- Test: `tests/kxEdit.Editor.Tests/CaretScrollTests.cs`(新規テストを追記)

### Step 1: 失敗するテストを書く

`tests/kxEdit.Editor.Tests/CaretScrollTests.cs` の末尾(クラス閉じ括弧の直前)に追記。

```csharp
    // ===== A-3: 絶対位置指定 setter の追従スクロール(2026-08-22 設計書 §2)=====
    //
    // 契約: キャレット/選択の「絶対位置を外から指定する」API はキャレットを可視域に入れる。
    //       アンカー相対で動かす API は呼び出し側がスクロールを判断する。
    // 検索ジャンプ / Ctrl+G / grep ジャンプ / UIA Select() はすべてこの 2 メソッドを通る。

    /// <summary>30 行の文書と 3 行程度の可視域を作る(末尾行が必ず初期ビューポート外になる)。</summary>
    private static (Form f, EditorControl c, string text) MakeTallDocument()
    {
        var text = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line{i}"));
        var (f, c) = MakeControl(text, width: 400, height: 60);
        return (f, c, text);
    }

    [Fact]
    public void SetCaretCharOffset_ScrollsCaretIntoView() =>
        Sta.Run(() =>
        {
            var (f, c, text) = MakeTallDocument();
            using (f)
            using (c)
            {
                c.TopLine = 0;
                int visibleRows = Math.Max(1, c.ClientSize.Height / c.LineHeightPx);
                // fixture 前提: 末尾行(index 29)が初期ビューポートの外にあること。
                // これが崩れると以降の assertion が空振りする。
                Assert.True(visibleRows < 29, $"fixture 前提崩れ: visibleRows={visibleRows}");

                int lineStart = text.LastIndexOf('\n') + 1; // 論理行 29 の先頭
                c.SetCaretCharOffset(lineStart); // ★ BringCaretIntoView は呼ばない

                Assert.True(
                    c.TopLine >= 29 - visibleRows + 1,
                    $"expected TopLine >= {29 - visibleRows + 1}, got {c.TopLine}"
                );
            }
        });

    [Fact]
    public void SetSelectionCharRange_ScrollsRangeEndIntoView() =>
        Sta.Run(() =>
        {
            var (f, c, text) = MakeTallDocument();
            using (f)
            using (c)
            {
                c.TopLine = 0;
                int visibleRows = Math.Max(1, c.ClientSize.Height / c.LineHeightPx);
                Assert.True(visibleRows < 29, $"fixture 前提崩れ: visibleRows={visibleRows}");

                int lineStart = text.LastIndexOf('\n') + 1;
                c.SetSelectionCharRange(lineStart, lineStart + 4); // ★ 検索ヒット選択と同じ経路

                Assert.True(
                    c.TopLine >= 29 - visibleRows + 1,
                    $"expected TopLine >= {29 - visibleRows + 1}, got {c.TopLine}"
                );
            }
        });

    // ----- 非対象 API が「スクロールしない」ことの固定 -----
    // no-change テストは非既定位置から始める(CLAUDE.md §4)= TopLine を 0 以外に置く。

    [Fact]
    public void SetSelectionAnchored_DoesNotScroll() =>
        Sta.Run(() =>
        {
            var (f, c, text) = MakeTallDocument();
            using (f)
            using (c)
            {
                // 非既定位置から開始: caret を先頭に置いた後、可視域を 3 行目へずらす。
                c.SetCaretCharOffset(0);
                c.TopLine = 3;

                // Ctrl+A 相当。キャレットは末尾(可視域外)へ動くが画面は動かない契約。
                c.SetSelectionAnchored(0, text.Length);

                Assert.Equal(3, c.TopLine);
            }
        });

    [Fact]
    public void SelectAll_DoesNotScroll() =>
        Sta.Run(() =>
        {
            // Ctrl+A のユーザー可視契約(Task 6 レビュー I-1 の判断)を直接固定する。
            var (f, c, _) = MakeTallDocument();
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(0);
                c.TopLine = 3;

                c.SelectAll();

                Assert.Equal(3, c.TopLine);
            }
        });

    [Fact]
    public void MoveCaretWithSelection_DoesNotScroll() =>
        Sta.Run(() =>
        {
            // shift+移動の共通経路。追従は呼び出し側(InputRouter)の責務=setter は動かさない。
            var (f, c, text) = MakeTallDocument();
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(0);
                c.TopLine = 3;

                c.MoveCaretWithSelection(text.Length);

                Assert.Equal(3, c.TopLine);
            }
        });
```

`using System.Linq;` は既にファイル先頭にあることを確認する(無ければ追加)。

### Step 2: テストを走らせて失敗を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build --filter "FullyQualifiedName~CaretScrollTests"
```

Expected:
- `SetCaretCharOffset_ScrollsCaretIntoView` — **FAIL**(`expected TopLine >= N, got 0`)
- `SetSelectionCharRange_ScrollsRangeEndIntoView` — **FAIL**(同上)
- `SetSelectionAnchored_DoesNotScroll` / `SelectAll_DoesNotScroll` /
  `MoveCaretWithSelection_DoesNotScroll` — **PASS**(現状も動かないため。実装後も PASS であることが契約)

### Step 3: 実装する

`src/kxEdit.Editor/EditorControl.Caret.cs` の `SetCaretCharOffset`。

変更前:

```csharp
        _caretCtrl.SetTo(snapped, _buffer.Current); // 単純キャレット移動は選択解除
        PositionCaret();
        Invalidate();
```

変更後:

```csharp
        _caretCtrl.SetTo(snapped, _buffer.Current); // 単純キャレット移動は選択解除
        PositionCaret();
        BringCaretIntoView();
        Invalidate();
```

同ファイルの `SetSelectionCharRange`。

変更前:

```csharp
        _caretCtrl.SetSelection(s, e, _buffer.Current);
        PositionCaret();
        Invalidate();
```

変更後:

```csharp
        _caretCtrl.SetSelection(s, e, _buffer.Current);
        PositionCaret();
        BringCaretIntoView();
        Invalidate();
```

### Step 4: 契約を doc comment に書く

`SetCaretCharOffset` の `<summary>` の末尾に 1 行足す:

```csharp
    /// <summary>
    /// キャレット位置を UTF-16 文字オフセットで設定する(選択はクリアされる=Anchor=Caret=snapped)。
    /// サロゲートペア中間位置(low)は前方(high)にスナップ。範囲外は [0, CharLength] にクランプ。
    /// SetSource 前の呼び出しは no-op(_buffer が null のため)。
    /// 位置が実際に変わったときは <see cref="BringCaretIntoView"/> で可視域へ追従する。
    /// </summary>
    /// <remarks>
    /// A-3 修正(2026-08-22): 「キャレット/選択の絶対位置を外から指定する API は可視域に入れる」
    /// という規約でこの追従を持たせている。<see cref="SetSelectionAnchored"/> /
    /// <see cref="MoveCaretWithSelection"/>(アンカー相対で動かす API)には**足さない**
    /// = Ctrl+A(<see cref="SelectAll"/>)が文書末尾まで画面を飛ばさないための意図的な非対称
    /// (Task 6 レビュー I-1 の判断を維持する)。shift+移動系は呼び出し側の
    /// <c>InputRouter</c> が直後に <see cref="BringCaretIntoView"/> を呼ぶ。
    ///
    /// 呼び出しは早期 return(位置無変化)の**後**に置く。UIA クライアントは無変化の
    /// <c>Select()</c> を高頻度で投げてくるため、前に置くと水平分岐の
    /// <c>ComputeCaretPoint</c> が毎回走る(<see cref="ScrollCharRangeIntoView"/> が
    /// 無変化呼び出しの早期 return を設けたのと同じ理由)。代償として「キャレットは既にその位置
    /// にあるが画面だけスクロールで離れている」ケースでは追従しない=受容する。
    ///
    /// 順序は <c>PositionCaret</c> → <c>BringCaretIntoView</c> → <c>Invalidate</c> で
    /// <see cref="AfterEdit"/> と揃える(先出しの PositionCaret が要る理由も同メソッドの remarks 参照)。
    /// </remarks>
```

`SetSelectionCharRange` の `<remarks>` には短く足す:

```csharp
    /// A-3 修正(2026-08-22): 位置が実際に変わったときは <see cref="BringCaretIntoView"/> で
    /// 可視域へ追従する。<c>Caret = Max(start, end)</c> にマップするため**範囲末尾**が
    /// 可視化される(<see cref="EnsureVisibleCharRange"/> の仕様と一致)。規約の詳細は
    /// <see cref="SetCaretCharOffset"/> の remarks を参照。
```

### Step 5: テストを走らせて緑を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests    -c Release --no-build
dotnet test tests/kxEdit.Core.Tests   -c Release --no-build
```

Expected: 全緑。**Editor/App のどこかが赤になったら、そこが「スクロールしない前提」に
依存していた場所** = 設計書 §3 の波及表に載っていない経路の発見。潰す前に記録すること。

### Step 6: コミット

```bash
git add src/kxEdit.Editor/EditorControl.Caret.cs tests/kxEdit.Editor.Tests/CaretScrollTests.cs
git commit -m "fix(editor): 絶対位置指定 setter でキャレットを可視域へ追従させる(A-3)

SetCaretCharOffset / SetSelectionCharRange が PositionCaret+Invalidate のみで
BringCaretIntoView を呼ばず、検索ジャンプ・Ctrl+G・grep ジャンプで画面が動かなかった
(旧 ScintillaHost.SelectCharRange の SCI_SCROLLCARET 契約が P6 移行で落ちていた)。

「絶対位置を外から指定する API は可視域に入れる / アンカー相対で動かす API は
呼び出し側が判断する」という規約で 2 メソッドにのみ追従を足す。
SetSelectionAnchored / MoveCaretWithSelection には足さない=Ctrl+A が文書末尾へ
画面を飛ばさない契約(Task 6 レビュー I-1)を維持し、テストで固定した。

呼び出しは早期 return の後・PositionCaret と Invalidate の間(AfterEdit と同順序)。"
```

---

## Task 3: App 互換 API と UIA `Select()` の追従を固定する(M-32)

Task 2 の実装で既に通るはずの経路を、**契約として明示的に固定する**タスク。
`GoToLine` は監査書が名指しした被覆対象、UIA `Select()` は M-32 そのもの。

**Files:**
- Test: `tests/kxEdit.Editor.Tests/EditorControlCompatApiTests.cs`(`GoToLine` の追従)
- Test: `tests/kxEdit.Editor.Tests/CaretScrollTests.cs`(UIA `Select()` の追従)

### Step 1: `GoToLine` の追従テストを書く

`tests/kxEdit.Editor.Tests/EditorControlCompatApiTests.cs` に追記。
既存の `GoToLine_MovesCaretToLineStart` はハンドル無しの裸コントロールで書かれているが、
スクロールの検証にはサイズを持つコントロールが要るので**別テストとして足す**
(既存テストは変更しない)。

ファイル先頭に `using System.Linq;` を追加。クラス末尾に追記:

```csharp
    // A-3(2026-08-22): Ctrl+G「行へ移動」で画面が追従することの固定。
    // 監査書 docs/plans/2026-08-22-v0.2-release-bug-audit.md の A-3 が名指しした被覆。
    [Fact]
    public void GoToLine_ScrollsTargetLineIntoView()
    {
        Sta.Run(() =>
        {
            var text = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line{i}"));
            using var form = new Form { Size = new System.Drawing.Size(400, 60) };
            var ctrl = new EditorControl { Dock = DockStyle.Fill };
            form.Controls.Add(ctrl);
            _ = form.Handle;
            ctrl.SetSource(TextBuffer.FromString(text));
            try
            {
                ctrl.TopLine = 0;
                int visibleRows = Math.Max(1, ctrl.ClientSize.Height / ctrl.LineHeightPx);
                Assert.True(visibleRows < 29, $"fixture 前提崩れ: visibleRows={visibleRows}");

                ctrl.GoToLine(29);

                Assert.True(
                    ctrl.TopLine >= 29 - visibleRows + 1,
                    $"expected TopLine >= {29 - visibleRows + 1}, got {ctrl.TopLine}"
                );
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }
```

### Step 2: UIA `Select()` の追従テストを書く(M-32)

`tests/kxEdit.Editor.Tests/CaretScrollTests.cs` の末尾に追記。
ファイル先頭に `using kxEdit.Accessibility;` を追加(`EditorControlBoundingRectsTests.cs` と同じ流儀)。

```csharp
    // M-32(2026-08-22): UIA Select() がキャレットを可視域へスクロールしない。
    // Adapter の IUiaTextHost.SetSelection は BeginInvoke で UI スレッドへ渡した後
    // EditorControl.SetSelectionCharRange を呼ぶため、A-3 の修正で同時に解消する。
    // 本テストは UI スレッド上で呼ぶので InvokeRequired=false=直接経路を通る。
    [Fact]
    public void UiaSetSelection_ScrollsSelectionIntoView() =>
        Sta.Run(() =>
        {
            var (f, c, text) = MakeTallDocument();
            using (f)
            using (c)
            {
                c.TopLine = 0;
                int visibleRows = Math.Max(1, c.ClientSize.Height / c.LineHeightPx);
                Assert.True(visibleRows < 29, $"fixture 前提崩れ: visibleRows={visibleRows}");

                int lineStart = text.LastIndexOf('\n') + 1;
                IUiaTextHost host = c;
                host.SetSelection(lineStart, lineStart + 4);

                Assert.True(
                    c.TopLine >= 29 - visibleRows + 1,
                    $"expected TopLine >= {29 - visibleRows + 1}, got {c.TopLine}"
                );
            }
        });
```

### Step 3: 走らせて緑を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
```

Expected: 全緑(Task 2 の実装で既に通る)。

**もし `UiaSetSelection_ScrollsSelectionIntoView` が赤なら** — `_host.IsHandleCreated` ガードで
早期 return している可能性がある。`MakeControl` は `_ = f.Handle` でフォームのハンドルを作るので
子コントロールのハンドルも生成されるはずだが、赤になったら `f.Show()` を使う形へ変える
(`EditorControlBoundingRectsTests` の `HostForm.CreateVisible()` パターン)。

### Step 4: 追加したテストが本当に網になっていることを確認する

`src/kxEdit.Editor/EditorControl.Caret.cs` の `SetSelectionCharRange` に足した
`BringCaretIntoView();` を**一時的に削除**:

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
```

Expected: `SetSelectionCharRange_ScrollsRangeEndIntoView` と
`UiaSetSelection_ScrollsSelectionIntoView` が赤。

同様に `SetCaretCharOffset` 側を削除して `SetCaretCharOffset_ScrollsCaretIntoView` と
`GoToLine_ScrollsTargetLineIntoView` が赤になることを確認。

**必ず両方戻す。** `git diff src/` で追加した 2 行だけが残っていることを確認する。

### Step 5: コミット

```bash
git add tests/kxEdit.Editor.Tests/
git commit -m "test(editor): GoToLine と UIA Select() の追従スクロールを固定する(A-3 / M-32)

Task 2 の setter 修正で既に通る経路を契約として明示的に固定する。
- GoToLine_ScrollsTargetLineIntoView: 監査書 A-3 が名指しした被覆
- UiaSetSelection_ScrollsSelectionIntoView: M-32(UIA Select が可視域へ入れない)

各 setter の BringCaretIntoView を削除する変異で対応するテストが赤化することを確認済み。"
```

---

## Task 4: App 層のジャンプ経路を固定する

**Files:**
- Test: `tests/kxEdit.App.Tests/SearchControllerTests.cs`(検索ジャンプ)
- Test: `tests/kxEdit.App.Tests/MainFormSmokeTests.cs`(grep ジャンプ)

### Step 1: 検索ジャンプの追従テストを書く

`tests/kxEdit.App.Tests/SearchControllerTests.cs` に追記。
ファイル先頭に `using System.Linq;` を追加。

`Host` は `HostForm.CreateWithDocs()`(既定サイズ・可視)を使う。文書を 200 行にして
末尾ヒットが必ずビューポート外に来るようにする。

```csharp
    // ===== A-3(2026-08-22): 検索ジャンプの追従スクロール =====

    [Fact]
    public void FindNext_ScrollsHitIntoView() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // 200 行 + 末尾に唯一のヒット。既定サイズのホストフォームでも必ず可視域外になる。
            var doc = host.NewDoc(
                string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line{i}")) + "\nNEEDLE"
            );
            doc.Editor.TopLine = 0;
            host.View.Pattern = "NEEDLE";
            host.Search.OpenFind();

            Assert.True(host.Search.FindNext());

            // 追従が無いと TopLine=0 のまま=晴眼ユーザーにはヒットが見えない(A-3)。
            Assert.True(doc.Editor.TopLine > 0, $"expected TopLine > 0, got {doc.Editor.TopLine}");
        });
```

### Step 2: grep ジャンプの追従テストを書く

`tests/kxEdit.App.Tests/MainFormSmokeTests.cs` に追記(既存 `OpenAndSelect_*` テストの隣)。
ファイル先頭に `using System.Linq;` が無ければ追加。

```csharp
    // A-3(2026-08-22): grep 結果からのジャンプで画面が追従することの固定。
    [Fact]
    public void OpenAndSelect_ScrollsTargetIntoView() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string path = tmp.File("many-lines.txt");
            var lines = Enumerable.Range(0, 200).Select(i => $"line{i}").ToArray();
            File2.WriteAllText(path, string.Join("\r\n", lines));
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: false), tmp);

            // 末尾行の先頭オフセット(CRLF なので 1 行 = "lineNNN" + 2)。
            int offset = string.Join("\r\n", lines.Take(199)).Length + 2;
            form.OpenAndSelect(path, offset, length: 4);

            var doc = form.FileForTest.TryOpenOrActivate(path);
            Assert.NotNull(doc);
            Assert.True(
                doc!.Editor.TopLine > 0,
                $"expected TopLine > 0, got {doc.Editor.TopLine}"
            );
        });
```

### Step 3: 走らせて緑を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

Expected: 全緑。

**フレークしたら**: `MainFormSmokeTests` は実 `MainForm` を可視化するため、
ホストのサイズが環境依存になる。`Assert.True(TopLine > 0)` は「200 行が 1 画面に入らない」
という緩い前提しか置いていないので通るはずだが、通らなければ行数を 2000 に増やす。

### Step 4: セッション復元時の追従を確認する(設計書 §3 の「要確認」の回収)

設計書 §3 で挙げた懸念 — 非アクティブタブはハンドル未生成で `ClientSize` が暫定値のため
`TopLine` が最適値にならない可能性 — を**探針で確認する**。

`tests/kxEdit.App.Tests/MainFormSmokeTests.cs` の既存の統合復元テスト
(`OnShown_UnifiedOn_LayoutAndBackups_RestoredSilently_E2E` など)を読み、
復元後の `doc.Editor.TopLine` と `doc.Editor.CurrentLine` を一時的に出力するテストを書いて観測する。

判定:

- **キャレット行が可視域に入っている** → 改善が確認できた。テストとして残す
  (「復元キャレットが可視域に入る」)。
- **入っていないが `TopLine=0` の現状よりマシ、または同等** → 悪化なし。テストは残さず、
  設計書 §7 の申し送りへ「非アクティブタブでは最適化されない」と実測結果を追記する。
- **悪化している**(例: `TopLine` が行き過ぎてキャレットが上に消える)→ 設計書 §7 の方針どおり
  `BringCaretIntoView` にガードを入れるのではなく、**復元側でタブ表示後に再追従させる**。
  この場合は Task 4 を分割して別途設計する。

観測結果は本計画の末尾「実施記録」に必ず書き残す(判断の根拠を後から追えるようにする)。

### Step 5: コミット

```bash
git add tests/kxEdit.App.Tests/
git commit -m "test(app): 検索ジャンプと grep ジャンプの追従スクロールを固定する(A-3)

App 層から見た A-3 の症状(SearchController.FindNext / MainForm.OpenAndSelect で
画面が動かない)を TopLine で固定する。Editor 層の setter 修正が App 経路まで
届いていることの確認。

セッション復元時の追従は探針で観測した(結果は実装計画の実施記録に記載)。"
```

---

## Task 5: A-12 — `GetBoundingRectangles` から `ScrollX` を減算する

**Files:**
- Modify: `src/kxEdit.Editor/UiaTextHostAdapter.cs`(`ComputeBoundingRectangles`・現在 590〜629 行目)
- Test: `tests/kxEdit.Editor.Tests/EditorControlBoundingRectsTests.cs`

### Step 1: 失敗するテストを書く

`tests/kxEdit.Editor.Tests/EditorControlBoundingRectsTests.cs` に追記。

hscroll を表示させるには「折り返し OFF + ウィンドウ幅より長い行 + 可視域に長行がある」が要る。
fixture は `CaretScrollTests.BringCaretIntoView_ScrollsDown_WhenCaretHiddenByHScrollBar` を流用する。

```csharp
    // A-12(2026-08-22): GetBoundingRectangles が _scrollX を引かず、折り返し OFF で
    // 右へスクロールした状態では NVDA のフォーカスハイライト矩形が実描画より右にずれる。
    // 描画(Paint.cs)・PointFromCharOffset・逆変換 OffsetFromClientPoint は引いており、
    // ここだけが往復非対称だった。
    [Fact]
    public void GetBoundingRectangles_SubtractsScrollX()
    {
        Sta.Run(() =>
        {
            // 長文行 1 本 + 短い行数本。幅を絞って hscroll を表示状態にする。
            var text = new string('x', 400) + "\nl1\nl2\nl3";
            using var form = HostForm.CreateVisible();
            var ctrl = new EditorControl { Dock = DockStyle.Fill };
            form.Controls.Add(ctrl);
            ctrl.SetSource(TextBuffer.FromString(text));
            try
            {
                form.ClientSize = new System.Drawing.Size(120, 100);
                form.PerformLayout();
                ctrl.WrapColumns = 0; // 折り返し OFF
                ctrl.TopLine = 0;
                ctrl.Invalidate();
                Application.DoEvents(); // 描画を 1 回起こしてレイアウトを確定

                IUiaTextHost host = ctrl;
                var before = host.GetBoundingRectangles(0, 4);
                Assert.NotEmpty(before); // fixture 前提: 行 0 は可視

                ctrl.ScrollX = 50;
                // fixture 前提: hscroll が表示されていないと ScrollX setter は no-op。
                Assert.True(ctrl.ScrollX > 0, "fixture 前提崩れ: hscroll 非表示で ScrollX を置けない");

                var after = host.GetBoundingRectangles(0, 4);
                Assert.NotEmpty(after);

                // X は ScrollX 分だけ左へ寄る。幅は差分なので不変。
                Assert.Equal(before[0] - ctrl.ScrollX, after[0]);
                Assert.Equal(before[2], after[2]);
            }
            finally
            {
                ctrl.Dispose();
                form.Close();
            }
        });
    }
```

### Step 2: 走らせて失敗を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build --filter "FullyQualifiedName~EditorControlBoundingRectsTests"
```

Expected: **FAIL**(`Assert.Equal() Failure: Expected: <before-50>, Actual: <before>`)。

**fixture 前提崩れで落ちた場合**(`ScrollX > 0` が false)— ウィンドウ幅・行長を調整する。
`CaretScrollTests.BringCaretIntoView_ScrollsDown_WhenCaretHiddenByHScrollBar` の
「長文行は line 0 に置く」注記(`UpdateHorizontalScrollbar` は `_topLine` から
probeHeight 分の視覚行しか走査しない)を守ること。

### Step 3: 実装する

`src/kxEdit.Editor/UiaTextHostAdapter.cs` の `ComputeBoundingRectangles`。

変更前:

```csharp
        int csx = _clientToScreenX,
            csy = _clientToScreenY;
        int lineHeight = _host.Metrics.LineHeightPx;
```

変更後:

```csharp
        int csx = _clientToScreenX,
            csy = _clientToScreenY;
        // A-12(2026-08-22): ComputeCaretPointForUia は _scrollX 適用前の X(描画原点座標)を返す。
        // 描画(EditorControl.Paint.cs)・PointFromCharOffset・逆変換 OffsetFromClientPoint は
        // いずれも _scrollX を引いており、ここだけ引いていないと往復が非対称になる
        //(折り返し OFF で右へスクロールした状態で NVDA のハイライト矩形が右にずれる)。
        // 本メソッドは GetBoundingRectangles の InvokeRequired 判定の内側=UI スレッド上でのみ
        // 走るため、_host.ScrollX の読みは a11y 鉄則に抵触しない。
        int sx = _host.ScrollX;
        int lineHeight = _host.Metrics.LineHeightPx;
```

さらに矩形の組み立て箇所。

変更前:

```csharp
            if (visible)
            {
                double w = Math.Max(1, x2 - x1);
                rects.Add(csx + x1);
                rects.Add(csy + y1);
                rects.Add(w);
                rects.Add(lineHeight);
            }
```

変更後:

```csharp
            if (visible)
            {
                // 幅 w は差分なので _scrollX の影響を受けない(両端から同量を引くため)。
                double w = Math.Max(1, x2 - x1);
                rects.Add(csx + x1 - sx);
                rects.Add(csy + y1);
                rects.Add(w);
                rects.Add(lineHeight);
            }
```

### Step 4: 緑を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
dotnet test tests/kxEdit.Core.Tests   -c Release --no-build
```

Expected: 全緑。

### Step 5: ミューテーションで網を確認する

`- sx` を削除 → `GetBoundingRectangles_SubtractsScrollX` が赤になることを確認 → 戻す。

### Step 6: コミット

```bash
git add src/kxEdit.Editor/UiaTextHostAdapter.cs tests/kxEdit.Editor.Tests/EditorControlBoundingRectsTests.cs
git commit -m "fix(editor): UIA 矩形から水平スクロール量を減算する(A-12)

ComputeBoundingRectangles が ComputeCaretPointForUia の返す描画原点座標を
そのままスクリーン座標へ足しており、折り返し OFF で右へスクロールした状態では
NVDA のフォーカスハイライト/キャレット矩形が実描画より ScrollX px 右にずれていた。

描画(Paint.cs)・PointFromCharOffset・逆変換 OffsetFromClientPoint はいずれも
引いており、ここだけが往復非対称だった。本メソッドは InvokeRequired 判定の内側=
UI スレッド上でのみ走るため _host.ScrollX の読みは a11y 鉄則に抵触しない。"
```

---

## Task 6: 最終ブランチレビュー(2 パス)+ 品質ゲート + PR

CLAUDE.md §3-5 / §4 / §6 / §7 に従う。

### Step 1: 品質ゲート

```bash
pwsh tools/pre-merge-check.ps1
```

Expected: **EXIT 0**。0 warning。

```bash
pwsh tools/sr-regression.ps1
```

a11y 関連変更なので手動実行する(UIA 応答の検証まで。**L5 の代替にはならない**)。

### Step 2: 最終ブランチレビュー(2 パス・**別々のエージェントを起動する**)

1 起動に混載するとレビューが浅くなる(CLAUDE.md §3-5)。
並列で走らせるなら **作業ツリーの担当を分ける**(PR #43 の教訓)。

**パス A — コード品質パス**(ミューテーション検証のスポットチェック込み)

レビュー観点として渡すこと:

- 設計書 §2 の規約(絶対位置 setter は追従 / アンカー相対 setter は据え置き)が
  実装とコメントで一貫しているか。
- 設計書 §3 の波及表に**漏れている呼び出し経路が無いか**
  (`SetCaretCharOffset` / `SetSelectionCharRange` の全呼び出し元を独立に再列挙させる)。
- 早期 return の後に置いた判断が doc comment で説明されているか。
- Task 1 の fixture 組み替えで**元の網の意図が失われていないか**
  (特に `_ScrollsDown_WhenCaretHiddenByHScrollBar` = Task 7 レビュー I-1 の回帰テスト)。
- ミューテーション: 本計画で挙げた 5 変異(Task 2 / 3 / 5 の各 `BringCaretIntoView` 削除、
  `SetSelectionAnchored` への追加、A-12 の `- sx` 削除)を実際に走らせて kill を確認。
  **`--filter` で絞らずプロジェクト単位で回す**。変異は**必ず戻す**。

**パス B — 脆弱性パス**

主眼は a11y 鉄則(RPC スレッド分離)。

- `IUiaTextHost.SetSelection` → `SetSelectionCharRange` → `BringCaretIntoView` の経路が
  UI スレッド上でのみ走ることの再確認(`BeginInvoke` マーシャリング)。
- `ComputeBoundingRectangles` で `_host.ScrollX` を読むことが RPC スレッドから起きないことの再確認
  (`Control.InvokeRequired` は Handle 未生成 / 破棄後に **false を返す**ため、
  `IsHandleCreated` ガードが `InvokeRequired` の手前にあることが要点)。
- `BringCaretIntoView` が `TopLine` / `ScrollX` setter 経由で `PositionCaret` → `SetCaretPos`
  を呼ぶ経路に、破棄後 / Handle 未生成で踏める窓が新たに増えていないか。
- 追従スクロールが**新たな DoS 面**を作っていないか(巨大 1 行 + 折り返し ON で
  `ComputeCaretPoint` が重い経路に高頻度で入らないか。早期 return の位置が効いているか)。

### Step 3: 指摘対応

CLAUDE.md §4 の 3 択で明示する:
① fixup commit で修正 / ② PR description に記載して受容 / ③ 理由付き却下。

**レビュー由来の修正は元 commit を書き換えず、別 fixup commit で積む**(履歴保存)。

### Step 4: 設計書へ実施記録を追記

`docs/plans/2026-08-22-jump-scroll-follow-design.md` の §7 申し送りに、
Task 4 Step 4 の観測結果(セッション復元時の `TopLine` 挙動)を追記する。
**§1〜§6 は策定時スナップショットなので書き換えない**(CLAUDE.md §8)。

### Step 5: PR 作成

```bash
git push -u origin feature/jump-scroll-follow
gh pr create --title "fix: ジャンプ/選択の追従スクロールを回復する(A-3 / M-32 / A-12)" --body "..."
```

PR description(日本語)に必ず書くこと:

- **目的**: 監査書 A-3(優先度 1)/ A-12(優先度 2)/ M-32 の解消。
- **契約変更の明示**: 「絶対位置指定 setter は追従 / アンカー相対 setter は据え置き」という
  非対称な規約を導入したこと。Ctrl+A の非スクロールは維持。
- **折り返し ON の制約**: A-6 の近似が残るため、折り返し ON では部分的にしか直らない
  (設計書 §4)。A-5 / E-1 と合わせて別テーマ送り。
- **L5 未実施**: SR 経路に触れるため L5 必須。監査書 §5 の「PR #36〜#39 分をまとめて 1 回」に
  相乗りする予定であること。L5 の確認項目は設計書 §6 の 4 項目。
- レビュー経緯(2 パスの指摘と 3 択の判断)。

---

## L5(実機 SR 検証)チェックリスト

マージ後、監査書 §5 のまとめ実施で確認する項目。

1. 折り返し OFF で Ctrl+G → 遠い行 → **画面がスクロールし**、NVDA が移動先の行を読む。
2. 検索(F3)/ grep 結果からのジャンプで同上。
3. NVDA のレビューカーソル移動で**画面が飛ばない**
   (`ScrollIntoView` の「既に可視なら動かさない」原則が保たれている)。
4. 折り返し OFF で右へスクロールした状態で、NVDA のフォーカスハイライト矩形が実描画と一致する(A-12)。
5. 折り返し ON で Ctrl+G → 遠い行 → **段落先頭までは寄る**(A-6 の制約が残ることの確認。
   ここが完全に直っていなくても本 PR の期待どおり)。

---

## 実施記録

### Task 4 Step 4 — セッション復元時の追従(設計書 §3「要確認」の回収)

**結論: 判定 (i)「キャレットが可視域内」。悪化なし。観測用テストを
`MainFormSmokeTests.OnShown_UnifiedOn_RestoredCaret_ScrollsIntoView` として残した。**

fixture: 200 行 / `CaretLine = 190` / タブ 2 枚(#0 = disk 再オープン・非アクティブ、
#1 = バックアップからの無題復元・アクティブ)。実装者とレビュアーが**独立に観測して同値**を得た。

| タブ | 経路 | active | `IsHandleCreated` | `TopLine` | `CurrentLine` | `ClientSize.Height` | `LineHeightPx` |
|---|---|---|---|---|---|---|---|
| #0 `many.txt` | disk 再オープン | False | True | 160 | 190 | 505 | 16 |
| #1 無題 | バックアップ復元 | True | True | 160 | 190 | 505 | 16 |

`visibleRows = 505 / 16 = 31` に対し `TopLine = 190 - 31 + 1 = 160` の理論値ぴったり。
暫定サイズで計算されていれば別の値になるため、復元時点で `ClientSize` が確定していたことの裏付け。

**懸念が到達しない機序**(レビュアーが `TabControl.Selected` イベントのフックで特定):
`src/kxEdit.App/DocumentManager.cs:100` の `_tabs.SelectedTab = page;` により、
**CreateNew したタブは生成直後に必ず選択される**。したがってどのタブも「作られた瞬間は選択中=
`ClientSize` が実値」の状態で `SetCaretByLineColumn` を受け、その後に次のタブへ選択が移って
非アクティブになる。「一度も選択されない TabPage」は現在のコードベースでは到達不能。

**将来の注意**: `DocumentManager.CreateNew()` から作成時選択を外すと、この前提は黙って崩れる。
そのときは復元側でタブ表示後に再追従させる(設計書 §7 の方針)。

補強実験(レビュアー実施): 非アクティブタブに対して直接 `SetCaretByLineColumn(190, 0)` を
呼んでも `TopLine = 160` になる=非アクティブそのものが追従を妨げるわけではない。

### 網の穴として発見し塞いだもの(各タスクの仕様レビュー由来)

| 発見 | 内容 | 対応 |
|---|---|---|
| Task 1 レビュー | fixture 組み替えの理由コメントが機序を取り違えていた(「`BringCaretIntoView` を壊しても緑」は成立しない。実際は「検証対象が `SetCaretCharOffset` へすり替わる」) | fixup `ea6f475` |
| Task 2 レビュー | `SetSelectionCharRange_ScrollsRangeEndIntoView` の選択範囲が両端とも同一論理行で、「範囲**末尾**を可視化する」契約を検証できていなかった(実装を範囲先頭の可視化へ差し替える変異が生存) | fixup `b2cd9c1`(範囲を行 0〜29 にまたがせた) |
| Task 2 レビュー | fixture の可視行数は実測 **1**(`MakeControl` の height は `Form.Size` なので `ClientSize.Height` は約 21 px)。計画の「3 行程度」は誤り | fixup `b2cd9c1`(実測値を remarks に記録) |
| Task 4 レビュー | 復元テストのコメントが「非アクティブページのハンドル遅延生成」を前提に書かれていたが、その性質はこの網では原理的に検証できない | fixup(機序を上記のとおり訂正) |
| Task 4 レビュー | grep テストの `Assert.Equal(199, hitLine)` だけでは `+2` 忘れ(`offset = 1679`)を捕まえられない | fixup(選択開始が桁 0 であることを追加。`+2` を外すと実際に赤化することを確認) |

### 受容した指摘

| 指摘 | 判断 |
|---|---|
| `BringCaretIntoView();` を `PositionCaret();` の**前**へ移す変異が生存する | **② 受容**。`PositionCaret` は `SetCaretPos` を呼ぶだけで `BringCaretIntoView` が読む状態を作らない=等価変異。順序は `AfterEdit` との一貫性・自己文書化のための規約でありテストで固定できる観測可能な契約ではない |
| doc comment で `<c>AfterEdit</c>` を選んだ理由付け(CS1574 回避)が誤り(cref でも解決できる) | **③ 却下**。コードに誤った記述は残っておらず実害なし |

### 未回収の申し送り

- 復元経路の被覆は 2/3。`FileController.cs:824`(パスあり dirty をバックアップから復元)は
  テスト未被覆。同じ `SetCaretByLineColumn` 委譲なので YAGNI と判断した。
- テスト側 `visibleRows` は `ClientSize.Height / LineHeightPx`、実装側は
  `PaintHeightPx`(hscroll 分を引く)ベース。現 fixture は hscroll 非表示で両者一致するが、
  hscroll が出る fixture を将来足すときはテスト側の上限が 1 行ゆるくなる(上界方向なので
  嘘の緑は生まないが、境界の kill 力は落ちる)。
- `InputRouter` のマウス経路(`HandleMouseDown` / `HandleMouseMove` / `HandleMouseDoubleClick`)の
  `BringCaretIntoView()` は本ブランチ後も load-bearing だが、テスト未被覆のまま
  (本変更による退行ではなく既存の空白)。
