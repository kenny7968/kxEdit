# 上書き保存の符号化劣化警告(A-10)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Shift_JIS / EUC-JP 文書を Ctrl+S したとき、そのコードページで表せない文字が
無警告で `?` に置換されるのを止め、保存前に確認を出す。

**Architecture:** App 層のみの変更。既存の `FileController.CanEncodeBuffer`(現在 SaveAs 専用)を
`SaveDocument` からも呼び、重複タブガードの後・`WriteToPath` の前で `IUserPrompt.OkCancel`
確認を挟む。Core(`TextFileService` の `ReplacementFallback`)は不変。

**Tech Stack:** C# / .NET 9 / WinForms / xUnit(L3 = `tests/kxEdit.App.Tests`)/ CSharpier

**設計書:** `docs/plans/2026-08-28-save-encoding-loss-warning-design.md`(commit `1bfe85c`)

**ブランチ:** `feature/save-encoding-loss-warning`(main `86d7145` から作成済み)

---

## 実装者への前提(必読)

- **commit 粒度は本計画が上書きする。** CLAUDE.md §3「簡略化の基準」に該当する規模
  (単一 src ファイル・十数行)なので、**テストと実装をまとめて単一 commit** にする。
  Task 1〜3 の途中では commit しない(Task 4 で 1 回)。レビュー由来の修正だけ別 fixup commit
  で積む(CLAUDE.md §4)。
- **ミューテーション検証は実施しない。** CLAUDE.md §4.A の禁止側(ファイル I/O 処理)に該当する。
  代わりに Task 3 Step 3 のセルフチェックを必ず通す。
- **C# の編集に sed / python の複数行置換を使わない。** 本リポジトリは CRLF で、過去に
  「複数行 replace が黙って外れる」「BOM が silent 混入してゲートを全通過する」事故が起きている。
  Edit ツールで編集すること。
- **pre-commit フックを `--no-verify` で飛ばさない**(CSharpier 整形が走る)。
- 事実確認済み(2026-08-28・.NET 9 実測): 932 / 51932 の `EncoderFallback.ReplacementFallback` は
  **サロゲートペアを `?` 2 個**にする(`"こんにちは😀"` → `"こんにちは??"`)。`€`・`–`・**U+FFFD** も
  それぞれ `?` 1 個になる。テストの期待値はこの実測に基づく。

---

### Task 1: 赤の証明(A-10 の実在をテストで固定する)

実装より先にテストを書き、**変更前の src で落ちること**を確認する。これが「A-10 が実在した」
唯一の証拠になる(実装後は二度と観測できない)。

**Files:**
- Modify: `tests/kxEdit.App.Tests/FileControllerTests.cs`(`SaveAs_Utf8_SkipsLossyWarning` の直後・
  `// ===== 開く系(TryOpenOrActivate は path を開く唯一の経路) =====` の直前に挿入)

**Step 1: 失敗するテスト 2 本を書く**

挿入位置の目印(この行の**直前**に入れる):

```csharp
    // ===== 開く系(TryOpenOrActivate は path を開く唯一の経路) =====
```

挿入する内容:

```csharp
    // ===== 符号化劣化警告(上書き保存経路・A-10) =====

    /// <summary>
    /// A-10: Shift_JIS 文書に SJIS で表せない文字(絵文字)を貼って Ctrl+S → 警告に「キャンセル」で
    /// ディスクにも保存点にも一切触れない。
    ///
    /// fixture の要点(CLAUDE.md §4.B「no-change は非既定状態から」): ディスクには **SJIS で書いた
    /// 元内容**を先に置く。「ファイルが存在しない」を assert すると、上書きが阻止されたのか
    /// そもそも作られなかったのかを区別できない。
    /// <see cref="Fakes.FakePrompt.OkCancelResult"/> は 1 つしか無いが取り合いにならない:
    /// Ctrl+S 経路で <c>OkCancel</c> を出すのはこの警告だけである(上書き確認は SaveAs 専用で、
    /// <c>WriteToPath</c> は <c>TryInspectSaveTarget</c> の <c>exists</c> を捨てる)。
    /// </summary>
    [Fact]
    public void Save_LossyEncoding_Cancel_WritesNothingAndKeepsModified() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            var sjis = EncodingCatalog.Get(932);
            File2.WriteAllText(path, "こんにちは", sjis);

            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "こんにちは";
            doc.State.Path = path;
            doc.State.Encoding = sjis;
            doc.Editor.ReplaceCharRange(5, 0, "\U0001F600"); // 絵文字を貼る=dirty かつ SJIS で表せない
            Assert.True(doc.Editor.Modified);

            host.Prompt.OkCancelResult = false; // 警告に「キャンセル」

            Assert.False(host.File.Save());

            Assert.Equal("こんにちは", File2.ReadAllText(path, sjis)); // 原本不変=? 置換が起きていない
            Assert.True(doc.Editor.Modified); // 保存点を打っていない=未保存であることが SR に伝わる
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "OkCancel" && e.Caption == "文字コードの警告"
            );
        });

    /// <summary>
    /// 「続行」を選べば従来どおり保存する(警告は保存を禁止するものではない)。
    /// 期待値の <c>"こんにちは??"</c> は .NET 9 実測に基づく: <c>ReplacementFallback</c> は
    /// サロゲートペアを <c>?</c> **2 個**にする(1 個と書くと落ちる)。
    /// </summary>
    [Fact]
    public void Save_LossyEncoding_OkProceedsAndWrites() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            var sjis = EncodingCatalog.Get(932);
            File2.WriteAllText(path, "こんにちは", sjis);

            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "こんにちは";
            doc.State.Path = path;
            doc.State.Encoding = sjis;
            doc.Editor.ReplaceCharRange(5, 0, "\U0001F600");

            host.Prompt.OkCancelResult = true; // 「続行」

            Assert.True(host.File.Save());

            Assert.Equal("こんにちは??", File2.ReadAllText(path, sjis)); // 承諾どおり ? で保存される
            Assert.False(doc.Editor.Modified); // 保存点が進む
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "OkCancel" && e.Caption == "文字コードの警告"
            );
        });
```

**Step 2: 赤を確認する**

Run: `dotnet test tests/kxEdit.App.Tests -c Debug --filter "FullyQualifiedName~Save_LossyEncoding"`

期待する結果: **2 本とも FAIL**。落ち方まで確認すること(これが A-10 の症状そのもの)。

- `Save_LossyEncoding_Cancel_...` → `Assert.Equal` が
  `Expected: こんにちは / Actual: こんにちは??` で落ちる
  = **警告が出ないまま上書きされ、絵文字が `?` 2 個に潰れた**
- `Save_LossyEncoding_OkProceedsAndWrites` → `Assert.Contains`(OkCancel ログ)で落ちる
  = 保存自体は通るが**確認が一度も出ていない**

**この 2 つの落ち方を実行ログからそのまま控えて Task 7 の PR description に貼る。**
要約して書き換えない(過去に台帳の転記で再現不能になった事故がある)。

**Step 3: commit しない**

Task 4 まで作業ツリーに置いたままにする。

---

### Task 2: 実装

**Files:**
- Modify: `src/kxEdit.App/FileController.cs`(`SaveDocument` 内・`return WriteToPath(...)` の直前)

**Step 1: 警告ブロックを追加する**

`SaveDocument` の末尾、次の 1 行の**直前**に挿入する:

```csharp
        return WriteToPath(doc, doc.State.Path);
```

挿入する内容:

```csharp
        // A-10: 上書き保存経路にも符号化劣化の事前確認を置く(SaveAs の C-2 追補 I-2 と対称)。
        // 従来は CanEncodeBuffer の呼出元が SaveAsDocument だけで、Ctrl+S は
        // TextFileService.Save の EncoderFallback.ReplacementFallback に素通りしていた
        // = 表せない文字が無警告で '?' になる。読込側の U+FFFD と違い、置換はディスク上でしか
        // 起きずバッファは元の文字を保持するので、画面にも文字数にも SR の読み上げにも痕跡が出ない。
        //
        // 位置は 3 点とも load-bearing:
        // (1) 重複タブガードより後 = 重複時はそもそも保存させないので全走査を無駄打ちしない。
        // (2) WriteToPath より前 = ApplyEol / ConvertEols の副作用を起こす前に短絡する。
        //     ConvertEols が触るのは CR / LF だけで、CR / LF は 932 / 51932 のどちらでも
        //     表現可能なので、判定を前に出しても答えは変わらない。
        // (3) State.Path is null 分岐より後 = 無題タブは SaveAsDocument が自前の警告を持つので
        //     二重に出ない。
        //
        // CodePage != 65001 のガードも load-bearing(性能): UTF-8 は BMP + astral を全表現できる
        // ので走査が常に無駄になる。外すと既定=UTF-8 の大半の保存に全走査が入る。
        //
        // 文言は SaveAs 版と共有しない。SaveAs は「選択した文字コード」(いまダイアログで選んだ値)、
        // ここは「現在の文字コード」(文書が持っている値)で主語が違う。逃げ道ボタンは出さず
        // (Ctrl+S が文書の文字コードを変える挙動変更を避ける)、文中で SaveAs へ誘導する。
        // 問いを末尾に置くのは既存 2 件と同じ形: SR は本文を頭から読む。
        //
        // defaultCancel: true は S-12 と対称。既定が OK 側だと、Ctrl+S 直後の Enter や
        // 閉じる確認「はい」からの連鎖で、確認を足したこと自体が無力化される。
        if (
            doc.State.Encoding.CodePage != 65001
            && !CanEncodeBuffer(doc.Editor.CurrentBuffer, doc.State.Encoding)
            && !_prompt.OkCancel(
                "現在の文字コードで表せない文字が含まれています。'?' として保存されデータが失われます。"
                    + "元の文字を残すには「名前を付けて保存」で UTF-8 を選んでください。続行しますか?",
                "文字コードの警告",
                defaultCancel: true
            )
        )
        {
            // SaveAs と違い戻る先のダイアログが無いので中止する(SaveAs は continue で再表示)。
            return false;
        }

```

**Step 2: `SaveDocument` の xmldoc に 1 行足す**

`SaveDocument` の `<summary>` 末尾(`A-7 (b) 残余...` の行の次)に追記:

```csharp
    /// A-10(2026-08-28): 符号化で表せない文字がある場合は保存前に確認する(SaveAs と対称)。
```

**Step 3: 緑を確認する**

Run: `dotnet test tests/kxEdit.App.Tests -c Debug --filter "FullyQualifiedName~Save_LossyEncoding"`

期待する結果: **2 本とも PASS**。

**Step 4: 既存テストの巻き添えが無いことを確認する**

Run: `dotnet test tests/kxEdit.App.Tests -c Debug`

期待する結果: **全件 PASS**。特に `Save_ExistingPath_WritesAndClearsModified` /
`Save_ReadOnlyDocument_*` / `ConfirmDiscardIfDirty_*` が緑のままであること
(これらは既定 UTF-8 なので新ガードで短絡し、挙動不変であるべき)。
**もし落ちたら実装ではなく既定エンコードの前提を疑う** — その場合は先に進まず報告する。

---

### Task 3: 網の補強

Task 1 の 2 本だけでは、次の変異が生き残る:

| 変異 | 生き残る理由 |
|---|---|
| `CodePage != 65001` ガードを削除 | 2 本とも SJIS 文書なのでガードの有無に関係なく緑 |
| `defaultCancel: true` → `false` | 2 本とも `Log` しか見ていない |
| `CanEncodeBuffer(...)` の否定を落とす / 常に警告 | SJIS で表現可能な文書を保存する対照群が無い |
| 警告ブロックを重複タブガードより**前**へ移す | 順序を見るテストが無い |
| 閉じる確認からの経路で `false` を無視 | `ConfirmDiscardIfDirty` を通るテストが無い |

**Files:**
- Modify: `tests/kxEdit.App.Tests/FileControllerTests.cs`

**Step 1: 対照群と pin を 4 本追加する**

Task 1 で作った `// ===== 符号化劣化警告(上書き保存経路・A-10) =====` 節の末尾に追記:

```csharp
    /// <summary>
    /// 対照群(過剰検知の防止・設計書 §4.3 ガードの網): UTF-8 文書は astral を含んでいても警告しない。
    /// **警告が出ないことだけでなく、絵文字が往復すること**まで見る。前者だけだと
    /// 「CanEncodeBuffer が UTF-8 でも false を返す」変異と区別できない。
    /// </summary>
    [Fact]
    public void Save_Utf8WithAstral_DoesNotWarn_AndRoundTrips() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");

            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "こんにちは";
            doc.State.Path = path; // State.Encoding は既定の UTF-8(65001)のまま
            doc.Editor.ReplaceCharRange(5, 0, "\U0001F600");
            Assert.Equal(65001, doc.State.Encoding.CodePage); // 既定の前提を明示(黙って変わると空振りする)

            Assert.True(host.File.Save());

            Assert.Empty(host.Prompt.OkCancelCalls);
            Assert.Equal("こんにちは\U0001F600", File2.ReadAllText(path, System.Text.Encoding.UTF8));
        });

    /// <summary>
    /// 対照群(空振り警告の防止): Shift_JIS でも**表現可能な本文**なら警告しない。
    /// <see cref="Save_Utf8WithAstral_DoesNotWarn_AndRoundTrips"/> とは別方向の網で、
    /// こちらが無いと「非 UTF-8 なら常に警告する」変異が生き残る。
    /// </summary>
    [Fact]
    public void Save_SjisEncodableContent_DoesNotWarn() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            var sjis = EncodingCatalog.Get(932);

            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "こんにちは";
            doc.State.Path = path;
            doc.State.Encoding = sjis;
            doc.Editor.ReplaceCharRange(5, 0, "世界"); // SJIS で表現可能

            Assert.True(host.File.Save());

            Assert.Empty(host.Prompt.OkCancelCalls);
            Assert.Equal("こんにちは世界", File2.ReadAllText(path, sjis));
        });

    /// <summary>
    /// 既定フォーカスはキャンセル側(S-12 / SaveAs の劣化警告と対称)。
    /// <c>defaultCancel: false</c> へ倒す変異をここで殺す。破壊的な確認で既定が OK だと、
    /// Ctrl+S 直後の Enter や閉じる確認「はい」からの連鎖で警告が知覚されずに確定する。
    /// </summary>
    [Fact]
    public void Save_LossyEncoding_WarnsWithCancelAsDefault() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            var sjis = EncodingCatalog.Get(932);

            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "こんにちは";
            doc.State.Path = path;
            doc.State.Encoding = sjis;
            doc.Editor.ReplaceCharRange(5, 0, "\U0001F600");
            host.Prompt.OkCancelResult = false;

            Assert.False(host.File.Save());

            Assert.Equal(("文字コードの警告", true), Assert.Single(host.Prompt.OkCancelCalls));
        });

    /// <summary>
    /// 閉じる確認「はい」→ 保存 → 劣化警告に「キャンセル」で、**クローズが中止される**
    /// (<c>ConfirmDiscardIfDirty</c> が false を返す)。A-10 修正に伴う挙動変更なので固定する。
    /// 先例 = <see cref="ConfirmDiscardIfDirty_Yes_WithoutPath_FallsBackToSaveAs_CancelMeansFalse"/>。
    /// <c>YesNoCancelResult</c> と <c>OkCancelResult</c> は別ノブなので取り合わない。
    /// </summary>
    [Fact]
    public void ConfirmDiscardIfDirty_Yes_LossyEncodingDeclined_ReturnsFalse() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            var sjis = EncodingCatalog.Get(932);
            File2.WriteAllText(path, "こんにちは", sjis);

            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "こんにちは";
            doc.State.Path = path;
            doc.State.Encoding = sjis;
            doc.Editor.ReplaceCharRange(5, 0, "\U0001F600");
            host.Prompt.YesNoCancelResult = DialogResult.Yes; // 「保存する」
            host.Prompt.OkCancelResult = false; // ただし劣化警告は「キャンセル」

            Assert.False(host.File.ConfirmDiscardIfDirty(doc)); // 閉じない

            Assert.Equal("こんにちは", File2.ReadAllText(path, sjis)); // 原本不変
            Assert.True(doc.Editor.Modified);
        });
```

**Step 2: 順序の網を既存テストへ 1 行足す**

`Save_PathAlsoOpenInAnotherTab_IsBlocked_AndFileKeepsOtherTabContent` の末尾
(`Assert.True(tabB.Editor.Modified);` の直後)に追記:

```csharp
            // A-10: 符号化劣化の確認は**この重複ガードより後**に置く契約(順序の網)。
            // 重複時は保存させないので、バッファ全走査を無駄打ちしてはいけない。
            Assert.Empty(host.Prompt.OkCancelCalls);
```

**Step 3: 網が実際に効くかセルフチェックする**

以下を**一時的に**適用し、対応するテストが赤になることを目視で確認してから元へ戻す
(commit しない・確認後は `git diff` が Task 1〜3 の意図した内容だけであることを見る)。

| 一時変更 | 赤になるべきテスト |
|---|---|
| `doc.State.Encoding.CodePage != 65001` の条件を削除 | `Save_Utf8WithAstral_DoesNotWarn_AndRoundTrips` |
| `!CanEncodeBuffer(...)` → `true` | `Save_SjisEncodableContent_DoesNotWarn` |
| `defaultCancel: true` → `defaultCancel: false` | `Save_LossyEncoding_WarnsWithCancelAsDefault` |
| 警告ブロックを `var other = _docs.FindByPath(...)` の直前へ移動 | `Save_PathAlsoOpenInAnotherTab_IsBlocked_AndFileKeepsOtherTabContent` |
| `return false;` → 削除(警告を無視して続行) | `Save_LossyEncoding_Cancel_...` と `ConfirmDiscardIfDirty_Yes_LossyEncodingDeclined_ReturnsFalse` |

**5 件すべてで期待どおり赤になること。**赤にならない行があれば、その網は空虚なので
fixture を組み替えてから次へ進む(過去に「期待値は正しいのに fixture が狭くて変異が生存」が
7 回連続した前例がある)。結果を Task 7 の PR description に控える。

**Step 4: L3 全件が緑であることを確認する**

Run: `dotnet test tests/kxEdit.App.Tests -c Debug`

期待する結果: 全件 PASS(新規 6 本 + 既存全部)。

---

### Task 4: 単一 commit

**Step 1: 差分が意図どおりか確認する**

Run: `git diff --stat`

期待する結果: **2 ファイルのみ**
(`src/kxEdit.App/FileController.cs` / `tests/kxEdit.App.Tests/FileControllerTests.cs`)。
Task 3 Step 3 の一時変更が残っていないことを `git diff src/kxEdit.App/FileController.cs` で目視する。

**Step 2: commit する**

```bash
git add src/kxEdit.App/FileController.cs tests/kxEdit.App.Tests/FileControllerTests.cs
git commit
```

コミットメッセージ(`-F -` のヒアドキュメント、または `-m` の複数指定で入れる):

```
fix(app): 上書き保存でも符号化できない文字を確認する(A-10)

Shift_JIS / EUC-JP 文書に表せない文字(絵文字・€・– 等)を貼って Ctrl+S
すると、無警告で '?' に置換されて保存されていた。読込側の U+FFFD と違い
置換はディスク上でしか起きず、バッファは元の文字を保持するため、再オープン
まで喪失に気づけなかった。

判定器 CanEncodeBuffer は既にあったが呼出元が SaveAsDocument だけだった。
SaveDocument の重複タブガードの後・WriteToPath の前に同じ確認を置き、
SaveAs との非対称を解消する。CodePage != 65001 のガードで UTF-8 文書は
従来どおり素通りする。Core(TextFileService)は不変。

副次的に、誤った文字コードで開いて U+FFFD が入った文書の Ctrl+S も止まる
(U+FFFD は 932 / 51932 で表現不能)。

L3 に 6 本追加(劣化キャンセル/続行・UTF-8 と SJIS 表現可の対照群 2 本・
defaultCancel の pin・閉じる確認からの中止)+ 既存の重複タブテストへ順序の
網を 1 行追加。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

**Step 3: フックが通ったことを確認する**

CSharpier が整形した場合は差分が commit に含まれる。`git show --stat HEAD` で
2 ファイルであることを確認する。

---

### Task 5: 品質ゲート

**Step 1: ゲートを実行する**

Run: `powershell -File tools\pre-merge-check.ps1`

期待する結果: **EXIT 0**(0 warning・L1/L2/L3 全緑・`Category=LocalOnly` 込み)。

**Step 2: 落ちたら**

`-warnaserror` 稼働中なので警告 1 件でも赤になる。ログを読んで原因を直し、
**元 commit を書き換えず fixup commit** で積む(CLAUDE.md §4)。

---

### Task 6: 別エージェントレビュー(統合 1 回)

CLAUDE.md §3「簡略化の基準」により、最終ブランチレビューの 2 パスを **1 回に統合してよい**規模。
ただし**別エージェントによるレビューは省略しない**。

**Step 1: レビューを依頼する**

`superpowers:requesting-code-review` を使い、次を明示して 1 エージェントに投げる:

- 対象: `feature/save-encoding-loss-warning` の main からの全差分
- 観点: コード品質 + 脆弱性の統合パス
- 特に見てほしい点:
  1. 警告ブロックの**配置の 3 根拠**(重複ガードの後 / `WriteToPath` の前 /
     `State.Path is null` 分岐の後)が実際に成立しているか
  2. `ConvertEols` より前に判定してよいという主張(CR / LF は 932 / 51932 で表現可能)が正しいか
  3. `CodePage != 65001` ガードで抜ける経路に、UTF-8 で表現不能になる入力が無いか
  4. 閉じる確認 / アプリ終了 / Windows シャットダウン経路で、この MessageBox が
     **新しい待機を作っていない**か(A-8 の「STA の管理待機は SENT メッセージを配送する」再入問題)
  5. テストの網が空虚でないか(Task 3 Step 3 の結果を渡す)
- 渡す資料: 設計書 `docs/plans/2026-08-28-save-encoding-loss-warning-design.md`

**Step 2: 指摘を 3 択で処理する**

`superpowers:receiving-code-review` に従う。指摘は鵜呑みにせず技術的に検証する
(**レビュー提案も実測で検証すること** — 過去にレビュー提案が直したはずのバグを
再導入しかけた前例がある)。各指摘を ① fixup commit で修正 / ② PR description に記載して受容 /
③ 理由付き却下 のいずれかに明示的に振り分ける。

**Step 3: fixup 後にゲートを再実行する**

fixup を積んだら Task 5 をもう一度通す。

---

### Task 7: PR

**Step 1: push して PR を作る**

Run: `git push -u origin feature/save-encoding-loss-warning`

PR description(日本語)に必ず含める:

- **目的**: 監査 §4 A-10。設計書へのリンク
- **赤の証明**: Task 1 Step 2 の失敗ログを**そのまま**貼る(要約しない)
- **網のセルフチェック**: Task 3 Step 3 の 5 件の結果表
- **レビュー経緯**: Task 6 の指摘と 3 択の振り分け
- **申し送り**:
  - **L5 未実施**(下記 Step 2)
  - A-9(改行コード判定の 4,096 文字窓)は本 PR の対象外=別テーマ
  - 「続行」の承諾は記憶しないので、同じ文書を保存するたびに確認が出る(設計判断・設計書 §3)

**Step 2: L5(実機 SR 検証)をユーザーへ依頼する**

設計書 §6 のとおり **L5 は必要**。マージ前にユーザーへ次の 1 項目を依頼する:

> NVDA 起動状態で、Shift_JIS で保存したテキストファイルを開き、絵文字か `–`(en dash)を
> 貼り付けて Ctrl+S を押す。
> ① 「文字コードの警告」ダイアログが開き、本文が頭から通しで読み上げられるか
> ② 既定フォーカスが**キャンセル**側にあるか(Enter を押すと保存されずに戻るか)
> ③ 「OK」を選ぶと保存され、以後の操作が従来どおりか

**Step 3: マージはユーザー判断**

ゲート EXIT 0 + レビュー完了 + L5 の結果が揃ってから、
`superpowers:finishing-a-development-branch` でマージ方法を確認する。

---

## 完了条件

- [ ] Task 1 の 2 本が変更前 src で赤だったことを記録した
- [ ] L3 全件 PASS(新規 6 本 + 既存)
- [ ] Task 3 Step 3 のセルフチェック 5 件すべてで期待どおり赤になった
- [ ] `tools/pre-merge-check.ps1` が EXIT 0
- [ ] 別エージェントレビュー完了・指摘を 3 択で処理済み
- [ ] PR 作成済み・L5 をユーザーへ依頼済み
