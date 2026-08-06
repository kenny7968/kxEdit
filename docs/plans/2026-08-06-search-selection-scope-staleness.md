# 「選択範囲のみ」スコープの陳腐化検出 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 「選択範囲のみ」で捕捉したスコープが捕捉後の文書編集で陳腐化していたら、「すべて置換」を実行せずに拒否する。

**Architecture:** `SearchController._selectionScope` に捕捉元の `TextSnapshot` 参照を持たせ、`ReplaceAll` で参照同一性を比較する。`TextSnapshot` は編集のたびに新インスタンスになり、キャレット・選択の移動では変わらないため、「検索移動でクロバーされない」という捕捉方式の目的を壊さずに陳腐化だけを検出できる。新しい event / seam / 公開 API は増やさない。

**Tech Stack:** C# / .NET 9 / WinForms / xUnit。変更は `src/yEdit.App/SearchController.cs` 1 ファイルと `tests/yEdit.App.Tests/SearchControllerTests.cs` のみ。

**設計書:** [2026-08-06-search-selection-scope-staleness-design.md](./2026-08-06-search-selection-scope-staleness-design.md)

---

## 前提知識(この計画を実行する人向け)

- **`TextSnapshot` は不変。** `src/yEdit.Core/Buffer/TextSnapshot.cs`。ピース木(永続データ構造)のルートを持つ読み取り専用スナップショット。`TextBuffer` は編集のたびに新しい `TextSnapshot` を `Current` へ差し替える。よって**参照同一性 = 内容の世代**が成り立つ。同じ idiom を `TextBuffer.Modified`(`ReferenceEquals(_current.Root, _savedRoot)`)と `MaterializedSearchStrategy` のキャッシュ判定が既に使っている。
- **`TextSnapshot.Root` は `internal`**(`yEdit.Core` 内のみ)。App 層からは触れないので、`TextSnapshot` インスタンス自体を保持して比較する。
- **`_selectionScope` は「選択範囲のみ」トグル ON の瞬間に捕捉される。** 実選択に追随しないのは意図的で、「次を検索」で選択がヒット位置へ移ってもスコープが生き残るようにするため。既存テスト `ReplaceAll_CapturedScope_SurvivesFindMoves` がこの性質を固定している。**この性質を壊してはならない。**
- **テストは STA 必須。** 実 `DocumentManager` + 実 `EditorControl` を使い、Form 境界(`FakeFindReplaceView`)と通知(`FakeAnnouncer`)だけを偽物にする。`Sta.Run(() => { ... })` で包む。テストクラス冒頭の `private sealed class Host` が配線済みのホスト。
- **`host.NewDoc(text)` は新規タブを作ってアクティブ化する。** `Editor.Text` セッターは新規バッファを作る(= 新しい `TextSnapshot`)。
- **文書の編集は `doc.Editor.ReplaceCharRange(start, length, text)`。** 挿入は `length: 0` で行う。
- **発声の検証は `host.Announcer.Said[^1]`**(最後の発声)。

---

## Task 1: 陳腐化スコープの拒否

**Files:**
- Modify: `src/yEdit.App/SearchController.cs`(using / フィールド宣言 / `OnInSelectionToggled` / `ReplaceAll` の 4 箇所)
- Test: `tests/yEdit.App.Tests/SearchControllerTests.cs`

### Step 1: 失敗するテストを書く

`tests/yEdit.App.Tests/SearchControllerTests.cs` の `ReplaceAll_CapturedScope_SurvivesFindMoves` の**直後**(`ReplaceAll_InCsvMode_IsBlocked` の直前)に次の 4 本を挿入する。

```csharp
    [Fact]
    public void ReplaceAll_InSelection_AfterEdit_RefusesStaleScope() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3); // 後半の "abc" だけを選択
            host.Search.OnInSelectionToggled(true); // [4,7) を捕捉

            doc.Editor.ReplaceCharRange(0, 0, "QQQQ"); // 先頭へ挿入=捕捉位置が別の中身を指す

            host.Search.ReplaceAll();

            // 修正前はここで [4,7)=前半の "abc"(ユーザーが選択していない側)が置換され
            // "QQQQX abc" + 「1 件置換しました」になっていた。
            Assert.Equal("QQQQabc abc", doc.Editor.Text); // 一文字も書き換えない
            Assert.Equal(
                "選択範囲が変わりました。選択し直してください",
                host.Announcer.Said[^1]
            );
        });

    [Fact]
    public void ReplaceAll_InSelection_UnchangedSnapshot_StillReplacesScopeOnly() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // 中央だけを捕捉する=prefix "abc " と suffix " abc" の両方を除外する fixture
            // (全選択との区別。CLAUDE.md §4 のテスト設計の教訓)。
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3);
            host.Search.OnInSelectionToggled(true); // [4,7) を捕捉

            host.Search.ReplaceAll(); // 編集を挟まない=スナップショットは同一

            Assert.Equal("abc X abc", doc.Editor.Text); // 前後の 2 件は残る
            Assert.Equal("1 件置換しました", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceAll_InSelection_AfterBufferSwap_RefusesStaleScope() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(0, 3);
            host.Search.OnInSelectionToggled(true); // [0,3) を捕捉

            // 同一タブでバッファごと差し替え(開き直し・復元・EOL 変換の相当)。
            // 文書切替イベントは起きないので ActiveDocumentChanged では捕まらない経路。
            doc.Editor.Text = "abc zzz";

            host.Search.ReplaceAll();

            Assert.Equal("abc zzz", doc.Editor.Text); // 差し替え後の [0,3) は "abc" で一致するが置換しない
            Assert.Equal(
                "選択範囲が変わりました。選択し直してください",
                host.Announcer.Said[^1]
            );
        });

    [Fact]
    public void ReplaceAll_InSelection_RetoggleAfterEdit_RecapturesScope() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3);
            host.Search.OnInSelectionToggled(true); // 古いスコープ
            doc.Editor.ReplaceCharRange(0, 0, "QQQQ"); // 陳腐化させる

            doc.Editor.SelectCharRange(8, 3); // "QQQQabc abc" の後半 "abc"
            host.Search.OnInSelectionToggled(true); // 取り直す
            host.Search.ReplaceAll();

            Assert.Equal("QQQQabc X", doc.Editor.Text); // 取り直した範囲だけが置換される
            Assert.Equal("1 件置換しました", host.Announcer.Said[^1]);
        });
```

### Step 2: テストを走らせて赤を確認する

Run:
```
dotnet test tests/yEdit.App.Tests -c Debug --filter "FullyQualifiedName~ReplaceAll_InSelection_AfterEdit_RefusesStaleScope|FullyQualifiedName~ReplaceAll_InSelection_AfterBufferSwap_RefusesStaleScope|FullyQualifiedName~ReplaceAll_InSelection_RetoggleAfterEdit_RecapturesScope|FullyQualifiedName~ReplaceAll_InSelection_UnchangedSnapshot_StillReplacesScopeOnly"
```

Expected: **失敗 3 / 合格 1**。

- `AfterEdit_RefusesStaleScope` → 赤。実際の値は `Text = "QQQQX abc"` / `Said[^1] = "1 件置換しました"`(= 報告されているバグそのもの)
- `AfterBufferSwap_RefusesStaleScope` → 赤。`Text = "X zzz"` / `Said[^1] = "1 件置換しました"`
- `RetoggleAfterEdit_RecapturesScope` → **緑**(取り直しは修正前から動く。これは復帰経路の網であって回帰テストではない)
- `UnchangedSnapshot_StillReplacesScopeOnly` → **緑**(既存挙動の網。修正で壊さないことを固定する)

赤 2 本が**この期待値どおりの実測値で落ちている**ことを目視すること。別の理由(コンパイルエラー・NRE 等)で赤になっているなら、それは網が機能していない。

### Step 3: 実装する — using を足す

`src/yEdit.App/SearchController.cs` の 3 行目(`using yEdit.Core.Csv;` の直前)に挿入する。

```csharp
using yEdit.Core.Buffers;
```

変更後の using ブロック全体:

```csharp
using System.Text.RegularExpressions;
using yEdit.App.Speech;
using yEdit.Core.Buffers;
using yEdit.Core.Csv;
using yEdit.Core.Search;
using yEdit.Editor;
```

### Step 4: 実装する — フィールド宣言(21 行目付近)

変更前:
```csharp
    private (int Start, int End)? _selectionScope; // 「選択範囲のみ」ON 時に捕捉した置換対象範囲
```

変更後:
```csharp
    // 「選択範囲のみ」ON 時に捕捉した置換対象範囲。捕捉元の TextSnapshot を一緒に持つ:
    // 位置は絶対 char index なので、捕捉後に文書が編集されると同じ数値が別の中身を指す。
    // 参照同一性で世代を見て、ずれていたら使わない(TextBuffer.Modified と同じ idiom)。
    private (TextSnapshot Snap, int Start, int End)? _selectionScope;
```

### Step 5: 実装する — 捕捉側 `OnInSelectionToggled`(317 行目付近)

変更前:
```csharp
            _selectionScope = e > s ? (s, e) : null;
```

変更後:
```csharp
            _selectionScope = e > s ? (ed.CurrentBuffer.Current, s, e) : null;
```

`else` 側の `_selectionScope = null;` と、コンストラクタの `ActiveDocumentChanged` ハンドラ内の `_selectionScope = null;` は**変更しない**(null 代入は型変更の影響を受けない)。

### Step 6: 実装する — 使用側 `ReplaceAll`(354 行目付近)

変更前:
```csharp
                if (_selectionScope is not { } scope)
                {
                    Announce("選択範囲がありません");
                    return;
                }
                rangeStart = scope.Start;
                rangeLen = scope.End - scope.Start;
```

変更後:
```csharp
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
                if (!ReferenceEquals(scope.Snap, snap))
                {
                    _selectionScope = null; // 旧ピース木の参照を即手放す
                    Announce("選択範囲が変わりました。選択し直してください");
                    return;
                }
                rangeStart = scope.Start;
                rangeLen = scope.End - scope.Start;
```

### Step 7: 新規 4 本が緑になることを確認する

Run: Step 2 と同じコマンド

Expected: **合格 4 / 失敗 0**

### Step 8: 過剰無効化していないことを確認する(既存の網)

`ReplaceAll_CapturedScope_SurvivesFindMoves` は「検索移動で実選択がクロバーされてもスコープは生きる」を固定している既存テスト。**粗すぎるトリガ(キャレット移動で無効化する等)を選んでいたらここが赤になる。**

Run:
```
dotnet test tests/yEdit.App.Tests -c Debug --filter "FullyQualifiedName~SearchControllerTests"
```

Expected: **失敗 0**(既存 + 新規すべて緑)

### Step 9: ミューテーション検証

**変異 1 — 判定を無効化(常に有効扱い):**

`ReplaceAll` の `if (!ReferenceEquals(scope.Snap, snap))` を `if (false)` に一時変更して Step 8 のコマンドを走らせる。

Expected: `ReplaceAll_InSelection_AfterEdit_RefusesStaleScope` と `ReplaceAll_InSelection_AfterBufferSwap_RefusesStaleScope` が**赤**。

**変異 2 — 常に拒否:**

同じ行を `if (true)` に一時変更して走らせる。

Expected: `ReplaceAll_InSelection_UnchangedSnapshot_StillReplacesScopeOnly`・`ReplaceAll_InSelection_RetoggleAfterEdit_RecapturesScope`・既存の `ReplaceAll_InSelection_ReplacesOnlyCapturedScope`・`ReplaceAll_CapturedScope_SurvivesFindMoves` が**赤**。

**両方の変異を確認したら必ず元へ戻す**(`if (!ReferenceEquals(scope.Snap, snap))`)。戻した後に Step 8 を再実行して全緑を確認すること。

> 変異を入れたまま `--no-build` で走らせると変異前のバイナリを見てしまう。上記コマンドは `--no-build` を付けていないので毎回ビルドされる。付け足さないこと。

### Step 10: 整形

Run: `dotnet csharpier format .`

(pre-commit フックが同じことをするが、先に流して差分を確認しておく。)

### Step 11: commit

```bash
git add src/yEdit.App/SearchController.cs tests/yEdit.App.Tests/SearchControllerTests.cs
git commit -m "$(cat <<'EOF'
fix(app): 陳腐化した「選択範囲のみ」スコープでの置換を拒否する

「選択範囲のみ」で捕捉したスコープは絶対 char 位置で固定され、捕捉後の
文書編集を追跡していなかった。このため捕捉と「すべて置換」の間に編集が
入ると、ユーザーが選択していない範囲へ置換が及び、しかも「N 件置換しました」
と成功発声していた(SR ユーザーには区別がつかない)。

スコープに捕捉元の TextSnapshot を持たせ、使用時に参照同一性で世代を比べる。
ずれていたら置換せず「選択範囲が変わりました。選択し直してください」と発声する。
TextSnapshot は編集のたびに新インスタンスになり、キャレット・選択の移動では
変わらないため、「検索移動でクロバーされない」捕捉方式の目的は壊れない。

PR #37 の申し送り S-7。同 PR の受容項目 A-1(同一タブのバッファ差し替え)と
A-6(文書が縮んだ場合)も同じ機構が覆う。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: L5 実機 SR 検証チェックリスト

**Files:**
- Create: `docs/plans/2026-08-06-search-selection-scope-staleness-l5-checklist.md`

新しい発声文言が `IAnnouncer.Say` 経由で 1 つ増えるため、CLAUDE.md §5 の「App の Speech 系に触れる変更」に該当し L5 が必要。

### Step 1: チェックリストを書く

最低限、次の項目を含めること(各項目に「操作手順 / 期待する発声 / 実測 / 判定」の欄を設ける)。

1. **陳腐化拒否の発声** — 範囲を選択 → 「選択範囲のみ」ON → 本文を編集 → 「すべて置換」。「選択範囲が変わりました。選択し直してください」が**1 回だけ**読まれ、本文が変わっていないこと。
2. **二重読みがないこと** — ダイアログ表示中は `Announce` が `IAnnouncer.Say` とダイアログ内ステータスの両方を更新する。NVDA が同じ文言を 2 回読まないこと。
3. **既存文言との区別** — 選択せずに「選択範囲のみ」ON → 「すべて置換」で「選択範囲がありません」が読まれ、1 と取り違えないこと。
4. **正常系の非退行** — 範囲を選択 → 「選択範囲のみ」ON → 編集せずに「すべて置換」。従来どおり「N 件置換しました」。
5. **検索移動後の非退行** — 範囲を選択 → 「選択範囲のみ」ON → 「次を検索」を数回 → 「すべて置換」。捕捉した範囲だけが置換され「N 件置換しました」。
6. **取り直し** — 1 の状態から選択し直して「選択範囲のみ」を OFF→ON → 「すべて置換」が通ること。

### Step 2: commit

```bash
git add docs/plans/2026-08-06-search-selection-scope-staleness-l5-checklist.md
git commit -m "$(cat <<'EOF'
docs(plans): 選択範囲スコープ陳腐化検出の L5 実機 SR 検証チェックリスト

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: 最終レビューと品質ゲート

CLAUDE.md §3 の簡略化基準に該当(src 数十行・単一ファイル)。最終ブランチレビューは
コード品質パスと脆弱性パスを**1 回に統合**してよい。ただし**別エージェントによるレビューは省略しない**。

### Step 1: 別エージェントでブランチ全体をレビューする

`git diff main` を対象に、コード品質(ミューテーション検証のスポットチェック込み)と
脆弱性の観点を統合した 1 パスでレビューを依頼する。

重点的に見てもらう論点:

- `TextSnapshot` を App 層で保持することの寿命・保持量(設計書 §4)。解放経路の漏れがないか。
- 参照同一性がこの目的に対して正しい粒度か。**過小**(編集を見逃す)・**過剰**(キャレット移動で無効化する)のどちらにも倒れていないか。
- 拒否時に `_selectionScope = null` を入れることで生じる 2 度目の文言変化が受容可能か。
- 新規テストが対象の変異を実際に殺すか(Task 1 Step 9 の結果を再現できるか)。

### Step 2: 指摘へ対応する

CLAUDE.md §4 の 3 択で明示する: ① fixup commit で修正 / ② PR description に記載して受容 / ③ 理由付き却下。
レビュー由来の修正は元 commit を書き換えず**別 fixup commit** で積む。

### Step 3: 品質ゲート

Run: `pwsh tools/pre-merge-check.ps1`

Expected: **EXIT 0**・0 warning。

### Step 4: push して PR を作る

PR description(日本語)に必ず含めるもの:

- **S-7 の記述が経路を取り違えていたこと**と、その実測根拠(タブ数 1/2/3 × 位置 3 通りで `ActiveDocumentChanged` が 6/6 発火)。
- **S-7 が指示した「`DocumentClosed` で 2 つ落とす 1 行」を却下した理由**(非アクティブタブのクローズで生きているスコープを壊すため)。
- 実際に修正した経路(捕捉後の編集)と再現手順。
- 受容項目 A-1 / A-6 が同じ機構で閉じたこと。
- **L5 未実施であること**とチェックリストへのリンク。
- 申し送り(設計書 §6): `Dismissed` でスコープを落とすか / `TabControl.Selected` の「保証されない」記述の扱い。
