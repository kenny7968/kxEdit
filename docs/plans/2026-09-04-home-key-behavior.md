# Home キーの動作を設定で切り替える 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Home キーの移動先を「行の最初の文字へ(スマート・既定)」と「常に行頭へ」の 2 択にし、
設定ダイアログの[編集]タブで切り替えられるようにする。

**Architecture:** 移動先の算出は `kxEdit.Core` の純関数 1 つに閉じている。ここへ `bool skipIndent`
を足し、`EditorControl.SmartHome` プロパティ経由で `AppSettings.SmartHome` を渡す。折り返し ON では
両モードとも**視覚行(折り返しセグメント)の先頭**を基準に保ち、P8-1a の a11y 特性を壊さない。

**Tech Stack:** C# / .NET 9 / WinForms / xUnit(L1 = kxEdit.Core.Tests, L2 = kxEdit.Editor.Tests,
L3 = kxEdit.App.Tests)。設計書は `docs/plans/2026-09-04-home-key-behavior-design.md`。

**ブランチ:** `feature/home-key-behavior`(作成済・設計書 commit `05bdc84` が載っている)

---

## 前提知識(この計画を実行する人向け)

- **`TextSnapshot` は不変**。`GetLineIndexOfChar` / `GetLineStart` / `GetLineEnd(line, includeBreak)`
  / `GetChar` / `GetText` で読む。offset は UTF-16 char offset。
- **空白の定義**: このエディタは半角空白 `' '` とタブ `'\t'` のみを「行頭空白」とみなす。
  全角空白 U+3000 は本文扱い。`char.IsWhiteSpace` は改行を巻き込むので**使わない**。
- **折り返し(wrap)**: `wrapColumns` は半角換算の桁数。`<= 0` で折り返し OFF。
  ON のとき 1 論理行は複数の**視覚セグメント**に割れ、Home は「キャレットが属するセグメントの先頭」
  を基準にする。継続セグメント(2 つ目以降)ではトグルせず常にセグメント先頭へ行く。
- **テストは STA が要る**(WinForms コントロールを作るため)。L2 / L3 では `Sta.Run(() => { ... })`
  で包む。既存テストの形をそのまま真似ること。
- **`-warnaserror` が効いている**。警告 1 個でビルドが落ちる。整形は CSharpier(pre-commit フックが
  自動で走る)。
- **コミットメッセージは日本語本文**、prefix は `feat|fix|docs|test|refactor|chore(scope): 要約`。
- **`--no-verify` を使わない**(CLAUDE.md §6)。

---

## Task 1: Core — `MoveLineHome` へのリネームと `skipIndent` の追加

**Files:**
- Modify: `src/kxEdit.Core/Editing/NavigationCommands.cs:55-137`(`MoveHomeSmart` 2 オーバーロード)
- Modify: `src/kxEdit.Editor/InputRouter.cs:183-198`(呼び出し側 1 箇所。Task 1 では `true` 固定)
- Test: `tests/kxEdit.Core.Tests/Editing/NavigationCommandsTests.cs`

**なぜリネームするか**: `skipIndent=false` は「スマートではない」ので、`MoveHomeSmart` という名前が
嘘になる。`MoveHome`(空白を見ない既存の別メソッド・UIA 等が使う)とは別物なので、
`MoveLineHome` にする。

**Step 1: 新しい挙動の失敗テストを書く**

`tests/kxEdit.Core.Tests/Editing/NavigationCommandsTests.cs` の
`MoveHomeSmart_OnEmptyBuffer_ReturnsZero`(`:164` 付近)の直後、
`// ===== P8-1a: 視覚行ベース Home キー` のコメント行より前に追加する。

```csharp
    // ===== 2026-09-04: skipIndent=false(常に行頭)モード =====

    [Fact]
    public void MoveLineHome_NoSkipIndent_AlwaysReturnsLineStart()
    {
        // 非既定位置(本文内)から始める。1 回目で行頭へ、2 回目は動かない
        // (行頭から始めると「既定位置と同じ」なのか「動かなかった」のか区別できない)。
        var s = Snap("  hello");
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 4, skipIndent: false));
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 0, skipIndent: false)); // no-op(トグルしない)
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 2, skipIndent: false)); // firstNonWs でもトグルしない
    }

    [Fact]
    public void MoveLineHome_NoSkipIndent_SecondLine_ReturnsThatLineStart()
    {
        // 論理行 1 本目だけで検証すると lineStart==0 になり、"0 を返すだけ" の実装と区別できない。
        var s = Snap("abc\r\n  def");
        Assert.Equal(5, NavigationCommands.MoveLineHome(s, 8, skipIndent: false)); // 行1 の先頭
        Assert.Equal(5, NavigationCommands.MoveLineHome(s, 7, skipIndent: false)); // 行1 の firstNonWs でも
    }

    [Fact]
    public void MoveLineHome_NoSkipIndent_LineWithOnlyWhitespace_ReturnsLineStart()
    {
        // スマート版はここで lineStart ⇔ lineEnd をトグルする。非トグルであることを固定する。
        var s = Snap("   ");
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 3, skipIndent: false));
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 0, skipIndent: false));
    }

    [Fact]
    public void MoveLineHome_NoSkipIndent_EmptyLine_ReturnsLineStart()
    {
        var s = Snap("abc\n\nxyz"); // 行1 は空行(lineStart=lineEnd=4)
        Assert.Equal(4, NavigationCommands.MoveLineHome(s, 4, skipIndent: false));
    }

    [Fact]
    public void MoveLineHome_NoSkipIndent_WithWrap_FirstVisualSegment_ReturnsLineStart()
    {
        // "  hello world" を wrapColumns=8 で折り返し(seg 0=[0..8) / seg 1=[8..13))。
        // 第 1 セグメントでは firstNonWs(2)へ行かず lineStart(0)へ。
        var s = Snap("  hello world");
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 4, wrapColumns: 8, M, skipIndent: false));
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 2, wrapColumns: 8, M, skipIndent: false));
    }

    [Fact]
    public void MoveLineHome_NoSkipIndent_WithWrap_SecondVisualSegment_StaysOnVisualSegmentStart()
    {
        // 「常に行頭」でも継続セグメントでは論理行頭(0)へ飛ばず視覚行頭(8)に留まる。
        // = P8-1a(NVDA が視覚行の先頭から読む)の特性が両モードで保たれる、が本テストの主張。
        var s = Snap("  hello world");
        Assert.Equal(8, NavigationCommands.MoveLineHome(s, 10, wrapColumns: 8, M, skipIndent: false));
        Assert.Equal(8, NavigationCommands.MoveLineHome(s, 8, wrapColumns: 8, M, skipIndent: false));
    }

    [Fact]
    public void MoveLineHome_NoSkipIndent_WithWrap_Disabled_SameAsLogicalLine()
    {
        // wrapColumns<=0 は論理行版へ委譲される(委譲が skipIndent を落としていないことの網)。
        var s = Snap("  hello");
        Assert.Equal(0, NavigationCommands.MoveLineHome(s, 4, wrapColumns: 0, M, skipIndent: false));
    }
```

**Step 2: テストが失敗することを確認する**

```
dotnet test tests/kxEdit.Core.Tests --filter "FullyQualifiedName~MoveLineHome"
```
Expected: **ビルドエラー**(`'NavigationCommands' に 'MoveLineHome' の定義が含まれていません`)。
これが「まだ実装がない」ことの証明。

**Step 3: Core を実装する**

`src/kxEdit.Core/Editing/NavigationCommands.cs` の `MoveHomeSmart` 2 つを**そっくり置き換える**。

論理行版(既存 `:47-72`):

```csharp
    /// <summary>Home キーの移動先(論理行版)。</summary>
    /// <param name="skipIndent">true=行頭の空白を飛ばすスマート挙動(<c>firstNonWs</c> ⇔ <c>lineStart</c>
    /// のトグル)。false=常に行頭(<c>lineStart</c>)。設定 <c>AppSettings.SmartHome</c> に対応する。</param>
    /// <remarks>
    /// <para><paramref name="skipIndent"/> = true のとき:</para>
    /// - キャレットが行頭(lineStart)にいる → 先頭空白の後(firstNonWs)へ
    /// - キャレットが firstNonWs にいる → 行頭(lineStart)へ
    /// - それ以外(本文内) → firstNonWs へ
    /// 空白のみの行では firstNonWs == lineEnd。トグルは lineStart ↔ lineEnd 相当だが問題なし。
    /// <para><paramref name="skipIndent"/> = false のとき: 常に lineStart(トグルしない=
    /// 行頭で押しても動かない)。</para>
    /// 空白判定は半角空白(' ')とタブ('\t')のみ。全角空白(U+3000)や他の Unicode 空白は含めない
    /// (Scintilla 版 M6 と同じ挙動。char.IsWhiteSpace は改行を巻き込むため使わない)。
    /// </remarks>
    public static int MoveLineHome(TextSnapshot s, int caret, bool skipIndent)
    {
        int line = s.GetLineIndexOfChar(caret);
        int lineStart = s.GetLineStart(line);
        if (!skipIndent)
            return lineStart;
        int lineEnd = s.GetLineEnd(line, includeBreak: false);
        int firstNonWs = lineStart;
        while (firstNonWs < lineEnd)
        {
            char c = s.GetChar(firstNonWs);
            if (c != ' ' && c != '\t')
                break;
            firstNonWs++;
        }
        // すでに firstNonWs にいる → lineStart。それ以外 → firstNonWs
        if (caret == firstNonWs)
            return lineStart;
        return firstNonWs;
    }
```

折り返し版(既存 `:74-137`)は、シグネチャと 2 箇所だけ変える。**`skipIndent` は末尾のパラメータ**。

```csharp
    /// <summary>P8-1a: 視覚行(折り返し行)ベースの Home キー移動先。</summary>
    /// <param name="wrapColumns">折り返し桁数(半角換算)。&lt;=0 で折り返し無し=<see cref="MoveLineHome(TextSnapshot, int, bool)"/> と同じ論理行挙動。</param>
    /// <param name="metrics">文字幅計測(<see cref="LineLayout.Wrap"/> と同じ流儀)。</param>
    /// <param name="skipIndent">true=行頭の空白を飛ばすスマート挙動。false=常に視覚行の先頭。</param>
    /// <remarks>
    /// <para>折り返し ON 時: キャレットが属する視覚セグメントの先頭を基準にする=NVDA/ナレーターが
    /// 視覚行の先頭から読むように App 層キー入力を統一する(P7 チェックリスト N-3=論理行頭に飛んで
    /// 視覚行の先頭から読まれない問題の解消)。<b>この特性は <paramref name="skipIndent"/> の
    /// 両値で保たれる</b>(2026-09-04)。</para>
    /// <list type="bullet">
    /// <item>第 1 視覚セグメント(=論理行先頭を含む)は論理行版と同じ smart トグル
    /// (視覚 seg 内の firstNonWs ⇔ 視覚 seg 先頭=lineStart)。skipIndent=false なら視覚 seg 先頭固定。</item>
    /// <item>継続視覚セグメント(2 つ目以降)は視覚 seg 先頭に固定=トグルなし
    /// (継続 seg は通常 leading whitespace を持たないため firstNonWs 判定不要)。</item>
    /// <item>空行は視覚セグメントも [(0,0)] 1 個(<see cref="LineLayout.Wrap"/> 契約)=lineStart を返す。</item>
    /// </list>
    /// </remarks>
    public static int MoveLineHome(
        TextSnapshot s,
        int caret,
        int wrapColumns,
        ICharMetrics metrics,
        bool skipIndent
    )
    {
        // wrap OFF は既存論理行挙動へ委譲
        if (wrapColumns <= 0)
            return MoveLineHome(s, caret, skipIndent);
```

以降は既存のまま。**変更点は 2 箇所だけ**:

```csharp
        // 継続セグメント: 視覚 seg 先頭固定(トグルなし)
        if (segIdx > 0)
            return visualStart;

        // 「常に行頭」モード: 第 1 セグメントでも空白を飛ばさない(2026-09-04)
        if (!skipIndent)
            return visualStart;

        // 第 1 セグメント: smart トグル(視覚 seg 内の firstNonWs ⇔ 視覚 seg 先頭)
        int firstNonWs = visualStart;
```

> ⚠️ `if (segIdx > 0 || !skipIndent)` と 1 行にまとめない。条件ごとに独立して変異させたいため
> (CLAUDE.md §4-A / メモリー「OR ガードは条件ごとに 1 行ずつ変異させる」)。

**Step 4: 既存テストの呼び出し側を機械的に追随させる**

`tests/kxEdit.Core.Tests/Editing/NavigationCommandsTests.cs` の**既存**の
`MoveHomeSmart(` 呼び出しをすべて `MoveLineHome(` に変え、末尾引数に `skipIndent: true` を足す。
**期待値は 1 つも変えない**(挙動不変の証明になる)。テストメソッド名も
`MoveHomeSmart_` → `MoveLineHome_Smart_` に改名する。

該当は `:116` `:125` `:132` `:139` `:164` `:184` `:194` `:206` および `MoveHomeSmart_WithWrap_ThirdVisualSegment_...`
以降(ファイル末尾まで)。**grep で残りが 0 になるまで確認する**:

```
grep -rn "MoveHomeSmart" src tests
```
Expected: 0 件(`src/kxEdit.Core/Layout/VisualSegments.cs:7` と
`src/kxEdit.Core/Editing/WordBoundary.cs:8` の**コメント内の言及**も新名に直す)。

**Step 5: 呼び出し元(Editor)を通す**

`src/kxEdit.Editor/InputRouter.cs:188-195` を差し替える。**Task 1 では `true` 固定**
(設定の配線は Task 2)。

```csharp
        int target = ctrl
            ? 0
            : NavigationCommands.MoveLineHome(
                snap,
                ctx.Caret.Caret,
                ctx.Host.WrapColumns,
                ctx.Host.Metrics,
                skipIndent: true // Task 2 で EditorControl.SmartHome に差し替える
            );
```

**Step 6: テストを走らせて全部通ることを確認する**

```
dotnet test tests/kxEdit.Core.Tests
dotnet test tests/kxEdit.Editor.Tests
```
Expected: どちらも **Passed / 失敗 0**。既存の Home 系テストが期待値そのままで緑=挙動不変。

**Step 7: ミューテーション検証(スポットチェック)**

CLAUDE.md §4-A で「カーソル移動」は実施対象。**手動で src を書き換えて `dotnet test` の
exit code を見る**(メモリー「変異ハーネスの exit code 罠」: grep 判定に頼らない・
**exit code が唯一確実**・ビルドが割れても EXITCODE=1 になるので、
**落ちたテスト名と合格件数まで確認する**)。

| # | 変異 | 期待 |
|---|------|------|
| 1 | 論理行版の `if (!skipIndent) return lineStart;` を削除 | `MoveLineHome_NoSkipIndent_*` が赤 |
| 2 | 論理行版の `!skipIndent` を `skipIndent` に反転 | 既存 smart テストと新テストの両方が赤 |
| 3 | 折り返し版の `if (!skipIndent) return visualStart;` を削除 | `..._WithWrap_FirstVisualSegment_ReturnsLineStart` が赤 |
| 4 | 折り返し版の委譲 `MoveLineHome(s, caret, skipIndent)` を `MoveLineHome(s, caret, true)` に | `..._WithWrap_Disabled_SameAsLogicalLine` が赤 |

各変異ごとに `dotnet test tests/kxEdit.Core.Tests` を実行し、**exit code != 0** と
**落ちたテスト名**を記録する。生存した変異があれば網を足す。**検証後は必ず src を元に戻し、
戻したうえで再度 `dotnet test` が緑になることを確認する**(`Copy-Item` での復元はタイムスタンプ
引き継ぎで古い DLL を叩くことがある)。

> ⚠️ **`git checkout -- <path>` で戻さない**。この時点では実装がまだ未コミットなので、
> 実装ごと HEAD へ巻き戻して消える(Task 1 実行時に実際に踏んだ)。**変異を入れる前に
> 対象ファイルをスクラッチパッドへ退避し、そこから書き戻す**。実装がコミット済みなら
> `git checkout -- <path>` でよい。どちらの場合も、復元後に `dotnet test` が緑に戻ることを
> 必ず確認する(タイムスタンプ引き継ぎで古い DLL を叩くことがあるため)。

**Step 8: コミット**

```
git add src/kxEdit.Core/Editing/NavigationCommands.cs src/kxEdit.Core/Layout/VisualSegments.cs src/kxEdit.Core/Editing/WordBoundary.cs src/kxEdit.Editor/InputRouter.cs tests/kxEdit.Core.Tests/Editing/NavigationCommandsTests.cs
git commit
```
メッセージ:
```
refactor(core): MoveHomeSmart を MoveLineHome へ改名し skipIndent を追加

Home キーの移動先算出に「行頭の空白を飛ばすか」のパラメータを足す。
skipIndent=true は従来と完全に同一の挙動(既存テストは期待値無改変で緑)。
skipIndent=false は常に行頭(折り返し ON では視覚行の先頭)へ移動する。
既定値引数は付けない(新しい呼び出し元が黙ってスマート側に倒れるのを防ぐ)。
InputRouter は本 commit では true 固定。設定の配線は次タスク。
```

**Step 9: コード品質レビュー(前倒し・CLAUDE.md §3-4)**

後続タスクが依存する seam の変更なので、**別エージェント**でコード品質レビューを行う。
指摘は ①fixup commit ②PR description に記載して受容 ③理由付き却下 の 3 択で明示する。

---

## Task 2: Editor — `EditorControl.SmartHome` と設定の反映

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.cs`(`WrapColumns` プロパティ `:907` の直前あたりへ
  プロパティ追加 / `ApplyAppearance` `:2691` の表示設定反映ブロックへ 1 行追加 + xmldoc 更新)
- Modify: `src/kxEdit.Editor/InputRouter.cs`(Task 1 で置いた `skipIndent: true` を差し替え)
- Test: `tests/kxEdit.Editor.Tests/KeyboardNavigationTests.cs`

**Step 1: 失敗テストを書く**

`tests/kxEdit.Editor.Tests/KeyboardNavigationTests.cs` の `Home_MovesToSmartLineStart`(`:173`)の
直後に追加する。

> 折り返し ON のケースは**ここでは書かない**。`EditorControl` は実フォントの `GdiCharMetrics` を
> 使うため、セグメント境界の offset が環境フォントに依存してフレークする。折り返しの網は
> L1(`MonoCharMetrics` で決定的)に置く。ここで固定するのは**配線**だけ。

```csharp
    [Fact]
    public void SmartHome_DefaultsToTrue() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("  hello");
            using (f)
            using (c)
            {
                Assert.True(c.SmartHome); // 既存ユーザーの挙動不変
            }
        });

    [Fact]
    public void Home_WithSmartHomeOff_AlwaysMovesToLineStart() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("  hello");
            using (f)
            using (c)
            {
                c.SmartHome = false;
                c.SetCaretCharOffset(4); // 非既定位置(本文内)から始める
                SendKey(c, Keys.Home);
                Assert.Equal(0, c.CaretCharOffset); // 空白を飛ばさず行頭へ
                SendKey(c, Keys.Home);
                Assert.Equal(0, c.CaretCharOffset); // トグルしない(2 回目で firstNonWs へ戻らない)
            }
        });

    [Fact]
    public void ShiftHome_FollowsSmartHomeSetting() =>
        Sta.Run(() =>
        {
            // Shift+Home は移動先の算出を Home と共有する=設定に追従する。
            var (f, c) = MakeControl("  hello");
            using (f)
            using (c)
            {
                c.SmartHome = false;
                c.SetCaretCharOffset(4);
                SendKey(c, Keys.Home | Keys.Shift);
                Assert.Equal(0, c.CaretCharOffset);
                Assert.Equal(4, c.SelectionAnchor);
                Assert.Equal((0, 4), c.GetSelectionCharRange()); // インデントごと選択される
            }
        });

    [Fact]
    public void CtrlHome_IgnoresSmartHomeSetting() =>
        Sta.Run(() =>
        {
            // Ctrl+Home は文書先頭固定=設定の影響を受けない(HandleHome の ctrl 分岐)。
            // SmartHome=true(既定)で確かめると "たまたま 0" と区別できないので、
            // 2 行目の本文内から押して 0 に飛ぶことを見る。
            var (f, c) = MakeControl("abc\r\n  def");
            using (f)
            using (c)
            {
                c.SmartHome = true;
                c.SetCaretCharOffset(8);
                SendKey(c, Keys.Home | Keys.Control);
                Assert.Equal(0, c.CaretCharOffset);
            }
        });

    [Fact]
    public void ApplyAppearance_AppliesSmartHomeFromSettings() =>
        Sta.Run(() =>
        {
            // 配線の網: プロパティが存在しても ApplyAppearance が読まなければ設定は死ぬ。
            var (f, c) = MakeControl("  hello");
            using (f)
            using (c)
            {
                c.ApplyAppearance(new AppSettings { SmartHome = false });
                Assert.False(c.SmartHome);
                c.ApplyAppearance(new AppSettings { SmartHome = true });
                Assert.True(c.SmartHome);
            }
        });
```

`AppSettings` の using が要る。ファイル冒頭の `using kxEdit.Core.Editing;` の下に
`using kxEdit.Core.Settings;` を足す(既に GlobalUsings にあれば不要=ビルドの警告で分かる)。

> `AppSettings.SmartHome` は Task 3 で足す。**Task 2 の時点では Core 側に先に足す**
> (プロパティ 1 個の追加なので分割損のほうが大きい)。Task 3 では UI と L3 テストだけを扱う。

**Step 2: テストが失敗することを確認する**

```
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~SmartHome"
```
Expected: **ビルドエラー**(`EditorControl` に `SmartHome` が無い / `AppSettings` に `SmartHome` が無い)。

**Step 3: `AppSettings` にキーを足す**

`src/kxEdit.Core/Settings/AppSettings.cs` の `TabsToSpaces`(`:52`)の直後に追加する。

```csharp
    /// <summary>Home キーで行頭の空白(インデント)を飛ばすか(true=スマート・既定)。
    /// false のときは常に行頭(折り返し ON では視覚行の先頭)へ移動する。
    /// 既存 settings.json にキーが無くても既定値が効くため、データ移行は不要。</summary>
    public bool SmartHome { get; set; } = true;
```

**Step 4: `EditorControl` にプロパティを足す**

`src/kxEdit.Editor/EditorControl.cs` の `WrapColumns` プロパティ(`:907` の xmldoc から始まる)の
**直前**に挿入する。

```csharp
    /// <summary>
    /// Home キーで行頭の空白(インデント)を飛ばすか。true(既定)= 最初の非空白文字 ⇔ 行頭の
    /// トグル。false = 常に行頭(折り返し ON では視覚行の先頭)。
    /// <see cref="ApplyAppearance"/> で <c>AppSettings.SmartHome</c> から反映する。
    /// Ctrl+Home(文書先頭)は本設定の対象外。再描画は不要(次の Home 押下から効く)。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SmartHome { get; set; } = true;
```

**Step 5: `ApplyAppearance` で反映する**

`src/kxEdit.Editor/EditorControl.cs` の `_caretWidthPx` を設定している行
(`_caretWidthPx = Math.Clamp(settings.CaretWidth, 1, 5);`)の直後に追加する。

```csharp
        // 2026-09-04: Home キーの動作(再描画不要・次の Home 押下から効く)
        SmartHome = settings.SmartHome;
```

同メソッドの xmldoc(`:2673-2689`)の「- 表示設定: ...」の箇条書きの後に 1 行足す:

```
    /// - 入力挙動: <see cref="SmartHome"/> を反映(Home キーの動作。再描画は不要)。
```

> `- Task 13 では <c>TabWidth</c>/<c>TabsToSpaces</c> は反映しない(...)` の行は**残す**
> (まだ本当に未接続なので、消すと doc が嘘になる)。

**Step 6: `InputRouter` を配線する**

`src/kxEdit.Editor/InputRouter.cs` の `skipIndent: true` を差し替える。

```csharp
                skipIndent: ctx.Host.SmartHome
```

**Step 7: テストを走らせて通ることを確認する**

```
dotnet test tests/kxEdit.Editor.Tests
dotnet test tests/kxEdit.Core.Tests
```
Expected: 両方 **Passed / 失敗 0**。

**Step 8: 配線の変異チェック(1 件)**

`InputRouter` の `skipIndent: ctx.Host.SmartHome` を `skipIndent: true` に戻すと
`Home_WithSmartHomeOff_AlwaysMovesToLineStart` と `ShiftHome_FollowsSmartHomeSetting` が赤になること、
`ApplyAppearance` の `SmartHome = settings.SmartHome;` を削ると
`ApplyAppearance_AppliesSmartHomeFromSettings` が赤になることを、
それぞれ `dotnet test` の **exit code と落ちたテスト名**で確認する。

> ⚠️ 復元は Task 1 Step 7 と同じ注意。**この時点では実装が未コミット**なので
> `git checkout -- <path>` は使わない。変異前にスクラッチパッドへ退避し、そこから書き戻して
> `dotnet test` が緑に戻ることを確認する。

**Step 9: コミット**

```
git add src/kxEdit.Core/Settings/AppSettings.cs src/kxEdit.Editor/EditorControl.cs src/kxEdit.Editor/InputRouter.cs tests/kxEdit.Editor.Tests/KeyboardNavigationTests.cs
git commit
```
メッセージ:
```
feat(editor): Home キーの動作を EditorControl.SmartHome で切り替える

AppSettings.SmartHome(既定 true=従来挙動)を追加し、ApplyAppearance 経由で
EditorControl へ反映する。InputRouter.HandleHome が MoveLineHome へ渡す。
Shift+Home は移動先算出を共有するため設定に追従し、Ctrl+Home(文書先頭)は
従来どおり対象外。折り返し ON の視覚行基準は両モードで保たれる(網は L1)。
```

---

## Task 3: App — 設定ダイアログ[編集]タブの UI

**Files:**
- Modify: `src/kxEdit.App/Settings/Tabs/EditSettingsTab.cs`
- Create: `tests/kxEdit.App.Tests/EditSettingsTabTests.cs`

**Step 1: 失敗テストを書く**

新規ファイル `tests/kxEdit.App.Tests/EditSettingsTabTests.cs`:

```csharp
using kxEdit.App.Settings.Tabs;
using kxEdit.Core.Settings;

namespace kxEdit.App.Tests;

/// <summary>
/// [編集]タブの Home キー動作ラジオ(2026-09-04)の構造と往復を固定する。
/// RadioButton はアプリ全体で初出のため、①排他が実際に効く配置になっているか
/// ②アクセスキーが既存項目と衝突していないか を機械で見る。
/// 実発声・実レイアウトは L5 実機検証でしか確認できない(CLAUDE.md §2 a11y 鉄則)。
/// </summary>
public class EditSettingsTabTests
{
    private static (EditSettingsTab tab, Control page) Build()
    {
        var tab = new EditSettingsTab();
        var page = tab.BuildPage();
        return (tab, page);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c))
                yield return d;
        }
    }

    private static RadioButton Radio(Control page, string startsWith) =>
        Descendants(page).OfType<RadioButton>().Single(r => r.Text.StartsWith(startsWith, StringComparison.Ordinal));

    [Fact]
    public void Loads_smart_home_true_as_the_first_radio() =>
        Sta.Run(() =>
        {
            var (tab, page) = Build();
            using (tab)
            {
                tab.LoadFrom(new AppSettings { SmartHome = true });
                Assert.True(Radio(page, "行の最初の文字").Checked);
                Assert.False(Radio(page, "常に行頭").Checked);

                var saved = new AppSettings();
                tab.SaveTo(saved);
                Assert.True(saved.SmartHome);
            }
        });

    [Fact]
    public void Loads_smart_home_false_as_the_second_radio() =>
        Sta.Run(() =>
        {
            var (tab, page) = Build();
            using (tab)
            {
                tab.LoadFrom(new AppSettings { SmartHome = false });
                Assert.False(Radio(page, "行の最初の文字").Checked);
                Assert.True(Radio(page, "常に行頭").Checked);

                // 既定 true の AppSettings に対して SaveTo が false を書けること
                // (書き込み漏れだと既定値のまま緑になるので、非既定側から検証する)
                var saved = new AppSettings();
                Assert.True(saved.SmartHome);
                tab.SaveTo(saved);
                Assert.False(saved.SmartHome);
            }
        });

    [Fact]
    public void The_two_radios_are_mutually_exclusive() =>
        Sta.Run(() =>
        {
            // 同一コンテナ(GroupBox 内)に置かれていることの機械的な証明。
            // 別々のコンテナに散ると両方 Checked になり、設定が意味を失う。
            var (tab, page) = Build();
            using (tab)
            {
                var smart = Radio(page, "行の最初の文字");
                var always = Radio(page, "常に行頭");

                always.Checked = true;
                Assert.False(smart.Checked);
                smart.Checked = true;
                Assert.False(always.Checked);
            }
        });

    [Fact]
    public void Radios_live_inside_a_named_group_box() =>
        Sta.Run(() =>
        {
            // SR がフォーカス時にグループ名を読むための前提。
            var (tab, page) = Build();
            using (tab)
            {
                var group = Descendants(page).OfType<GroupBox>().Single();
                Assert.Equal("Home キーの動作", group.Text);
                Assert.Contains(Radio(page, "行の最初の文字"), Descendants(group));
                Assert.Contains(Radio(page, "常に行頭"), Descendants(group));
            }
        });

    [Fact]
    public void Access_keys_in_the_tab_are_unique() =>
        Sta.Run(() =>
        {
            // 新規の &F / &B が既存(&W &K &T &S)と衝突していないこと。
            var (tab, page) = Build();
            using (tab)
            {
                var keys = Descendants(page)
                    .Select(c => c.Text)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .SelectMany(AccessKeysOf)
                    .ToList();
                Assert.Equal(keys.Count, keys.Distinct().Count());
                Assert.Contains('F', keys);
                Assert.Contains('B', keys);
            }
        });

    private static IEnumerable<char> AccessKeysOf(string text)
    {
        for (int i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] != '&')
                continue;
            if (text[i + 1] == '&')
            {
                i++; // "&&" はリテラルの & (アクセスキーではない)
                continue;
            }
            yield return char.ToUpperInvariant(text[i + 1]);
        }
    }
}
```

`using System.Collections.Generic;` / `System.Linq` が GlobalUsings に無ければ足す
(`tests/kxEdit.App.Tests/GlobalUsings.cs` には `System` / `System.Windows.Forms` などがある。
ビルドエラーを見てから足すこと)。

**Step 2: テストが失敗することを確認する**

```
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~EditSettingsTabTests"
```
Expected: **ビルドエラー**または `Single()` が `InvalidOperationException`
(GroupBox / RadioButton がまだ無い)。

**Step 3: [編集]タブに UI を足す**

`src/kxEdit.App/Settings/Tabs/EditSettingsTab.cs`。

(a) クラスの xmldoc を更新:
```csharp
/// <summary>「編集」タブ。表示折り返しの ON/OFF と桁数、タブ幅・タブ→スペース、
/// Home キーの動作を扱う。</summary>
```

(b) フィールド追加(`_tabsToSpaces` の直後):
```csharp
    private readonly GroupBox _homeGroup = new()
    {
        Text = "Home キーの動作",
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
    };
    private readonly RadioButton _homeSmart = new()
    {
        Text = "行の最初の文字へ移動する(もう一度押すと行頭)(&F)",
        AutoSize = true,
    };
    private readonly RadioButton _homeLineStart = new()
    {
        Text = "常に行頭へ移動する(&B)",
        AutoSize = true,
    };
```

(c) `BuildPage()` の `return root;` の直前に追加:
```csharp
        // 4 行目: Home キーの動作(2 択)。GroupBox で囲うのは 2 つの理由から:
        // ① WinForms の RadioButton は同一コンテナ内で排他になるため、既存 CheckBox 群と
        //    同じ TableLayoutPanel に直置きすると将来別のラジオを足したとき混線する。
        // ② SR がフォーカス時にグループ名(GroupBox.Text)を読む。
        var homePanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Dock = DockStyle.Fill,
        };
        _homeSmart.TabIndex = 0;
        _homeLineStart.TabIndex = 1;
        homePanel.Controls.Add(_homeSmart);
        homePanel.Controls.Add(_homeLineStart);
        _homeGroup.Controls.Add(homePanel);
        _homeGroup.TabIndex = 6; // 既存の末尾(5 = タブ→スペース)に続く
        root.Controls.Add(_homeGroup, 0, 3);
        root.SetColumnSpan(_homeGroup, 2);
```

(d) `LoadFrom` の末尾:
```csharp
        _homeSmart.Checked = s.SmartHome;
        _homeLineStart.Checked = !s.SmartHome;
```

(e) `SaveTo` の末尾:
```csharp
        r.SmartHome = _homeSmart.Checked;
```

(f) `Dispose` の末尾(CA1001 対応・既存方針どおり):
```csharp
        _homeSmart.Dispose();
        _homeLineStart.Dispose();
        _homeGroup.Dispose();
```

**Step 4: テストを走らせて通ることを確認する**

```
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~EditSettingsTabTests"
```
Expected: **Passed / 失敗 0**。

**Step 5: 実アプリで目視確認する**

```
dotnet run --project src/kxEdit.App
```
[設定]→[編集]タブを開き、次を確認する(SR 無しの目視でよい。実発声は L5):
- グループ枠「Home キーの動作」の中にラジオ 2 つが**縦に並び、他の項目と重なっていない**
- Alt+F / Alt+B で選択が切り替わる・↑↓ でも切り替わる
- OK → インデント行で Home を押し、選んだ動作になる(タブを開いたまま即時反映される)

> レイアウトの崩れ(GroupBox の AutoSize と FlowLayoutPanel の Dock=Fill の組み合わせ)は
> ここでしか見つからない。崩れていたら `Dock` を外して `Padding` で調整する。

**Step 6: コミット**

```
git add src/kxEdit.App/Settings/Tabs/EditSettingsTab.cs tests/kxEdit.App.Tests/EditSettingsTabTests.cs
git commit
```
メッセージ:
```
feat(app): 設定[編集]タブに Home キーの動作の 2 択を追加

「行の最初の文字へ移動する(もう一度押すと行頭)」(既定)と
「常に行頭へ移動する」を GroupBox 内のラジオで切り替える。
アクセスキーは既存タブで未使用の F / B。RadioButton はアプリ初出のため、
排他が効く配置とアクセスキーの一意性をテストで固定した。
```

---

## Task 4: 説明書の追記

**Files:**
- Modify: `説明書/kxEdit説明書.md`

> ⚠️ **この文書はユーザー編集版が正**(CLAUDE.md §8)。**勝手に改稿しない**。
> 追記案を作ってユーザーに提示し、校閲を受けてから反映する。既存の[編集]タブの説明箇所と
> キー一覧(Home の記述)を探して、追記すべき位置と文面を提案すること。

該当箇所が見つからない場合はこのタスクを飛ばし、PR description に「説明書の追記は未実施」と
明記する(黙って落とさない)。

---

## Task 5: L5 チェックリストの作成

**Files:**
- Create: `docs/plans/2026-09-04-home-key-behavior-l5-checklist.md`

既存の L5 チェックリスト(`docs/plans/2026-09-03-external-change-detection-l5-checklist.md` など)の
書式に合わせる。項目は設計書 §5 の 5 本:

1. 折り返し OFF・スマート: インデント行で Home → 最初の文字から読む / もう一度 Home → 行頭
2. 折り返し OFF・常に行頭: インデント行で Home → 行頭から読む / もう一度 Home で動かない
3. 折り返し ON・常に行頭: 継続セグメント上で Home → 視覚行の先頭から読む(論理行頭へ飛ばない)
4. Shift+Home の選択範囲が両モードで正しく読み上げられる
5. 設定[編集]タブでグループ名「Home キーの動作」と各ラジオが読み上げられ、↑↓ で排他選択でき、
   Alt+F / Alt+B が効く

各項目に**再現手順・期待する発声・PASS/FAIL 欄**を書く。手順は「どのファイルを開き、
キャレットをどこへ置き、何を押すか」まで具体的に書く(メモリー: L5 手順書が空振りした前例あり)。

コミット: `docs(plans): Home キー動作切替の L5 チェックリスト`

---

## Task 6: 最終ブランチレビュー(2 パス)

CLAUDE.md §3-5。**パスごとに独立した別エージェント**を起動する(1 起動に混載しない)。

1. **コード品質パス** — ブランチ全体の差分。ミューテーション検証のスポットチェック込み。
2. **脆弱性パス** — ブランチ全体の差分。本件は外部入力のパース・パス操作・プロセス起動に
   触れないため指摘は薄い見込みだが、**省略しない**。

指摘は ①fixup commit で修正 ②PR description に記載して受容 ③理由付き却下 の 3 択で明示。
**元 commit は書き換えず、別 fixup commit で積む**(CLAUDE.md §4)。

---

## Task 7: 品質ゲート → PR

**Step 1: 品質ゲート**

```
pwsh -File tools/pre-merge-check.ps1
```
Expected: **EXIT 0**。0 warning(`-warnaserror` 稼働中)。

**Step 2: push して PR を作成**

```
git push -u origin feature/home-key-behavior
gh pr create --base main --title "feat: Home キーの動作を設定で切り替えられるようにする"
```

PR description(日本語)に書くこと:
- **目的**: v0.2 リリース前の最後の機能追加。Home の移動先を 2 択に。
- **既定は従来挙動**(`SmartHome = true`)。既存 settings.json の移行不要。
- **設計判断**: 「常に行頭」でも折り返し ON では視覚行の先頭を基準に保つ(P8-1a の a11y 特性維持)。
- **レビュー経緯**: Task 1 の前倒しコード品質レビュー + 最終 2 パスの指摘と対応(3 択)。
- **申し送り**: `TabWidth` / `TabsToSpaces` が `ApplyAppearance` 未反映のまま(本件範囲外)/
  End キーに対応するトグルは無い / 説明書の追記状況。
- **L5**: `docs/plans/2026-09-04-home-key-behavior-l5-checklist.md`(**未実施**であることを明記)。

**Step 3: マージはユーザーが行う**(L5 実施後)。

---

## 完了の定義

- [ ] L1 / L2 / L3 すべて緑(`dotnet test` の exit code 0)
- [ ] 既存の Home 系テストが**期待値無改変**で緑(挙動不変の証明)
- [ ] ミューテーション検証のスポットチェック 5 件が全て kill された(exit code とテスト名を記録)
- [ ] `grep -rn "MoveHomeSmart" src tests` が 0 件
- [ ] 実アプリでラジオのレイアウト・アクセスキー・即時反映を目視確認済み
- [ ] 別エージェントによる最終レビュー 2 パス完了・指摘に 3 択で対応済み
- [ ] `tools/pre-merge-check.ps1` が EXIT 0
- [ ] L5 チェックリスト作成済み(実施はユーザー)
