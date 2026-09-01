# B3: CSV F2 セル確定の座標陳腐化(M-25) 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** F2 セル編集の確定が、開始時に捕捉した char オフセットではなく**確定時のパースから
`(row, col)` で解決し直した範囲**へ書くようにし、解決先が別セルになっていたら書かずに発声する。

**Architecture:** `CsvController.BeginEdit` の `onCommit` クロージャから `start` / `length` を
削除する(陳腐化しうる値を持ち越さない)。確定時に `doc.ParseCsv()` → `GetField(row, col)` で
解決し、開始時のセル値と現在のセル値を **EOL 正規化して**比較する。一致したときだけ
`ReplaceCharRange` する。不一致 / セル消失なら `CsvAnnounceFormatter.CommitTargetChanged` を発声して書かない。

**Tech Stack:** C# / .NET 9 (WinForms) / xUnit。設計書は
`docs/plans/2026-09-01-csv-f2-commit-staleness-design.md`。

**ブランチ:** `feature/csv-f2-commit-staleness`(main `fd9205b` から分岐・設計書 commit `74ba741` 済)。

---

## 前提: この計画のコードは「検証すべき案」である

**計画に書いたコードは正解ではない**(過去 2 ブランチで実際に欠陥が混ざった)。各 Task は
必ず「先にテストを赤で見る」→「実装」→「緑を見る」の順で進め、計画のコードが期待どおり
動かなければ**計画ではなく実物に合わせる**こと。食い違いは Task の最後に設計書 §8 へ記録する。

## 共通コマンド

```bash
# ビルド(0 warning 必須。-warnaserror 稼働中)
dotnet build kxEdit.sln -c Release -warnaserror

# 各層のテスト
dotnet test tests/kxEdit.Core.Tests   -c Release --no-build
dotnet test tests/kxEdit.App.Tests    -c Release --no-build

# 1 本だけ走らせる
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~<TestName>"
```

CSharpier の整形は pre-commit フック(Husky.Net)が自動で行う。**`--no-verify` は使わない**。

---

## Task 1: セル値の EOL 正規化を `CsvWriter` に 1 つ置く

同一性検証(Task 3)と `CsvCellEditor.Commit` が**同じ正規化規則**を使う。規則の持ち主を
2 か所に増やさないため、先に共通化しておく。**挙動不変**の準備タスク。

**Files:**
- Modify: `src/kxEdit.Core/Csv/CsvWriter.cs`
- Modify: `src/kxEdit.App/CsvCellEditor.cs:112`
- Test: `tests/kxEdit.Core.Tests/Csv/CsvWriterTests.cs`

**Step 1: 失敗するテストを書く**

`tests/kxEdit.Core.Tests/Csv/CsvWriterTests.cs` の末尾(クラス閉じ括弧の直前)に足す:

```csharp
    // ===== NormalizeEols(F2 確定値と CsvParser の Value を同じ土俵に乗せる) =====
    // CsvParser は引用符内の CR / LF を literal のまま Value へ積む(CsvParser.cs:117-124)ため、
    // ConvertEols 後の Value は変換前と素の比較で一致しない。正規化はその差を吸収する。

    [Fact]
    public void NormalizeEols_Crlf_BecomesLf() =>
        Assert.Equal("a\nb", CsvWriter.NormalizeEols("a\r\nb"));

    [Fact]
    public void NormalizeEols_LoneCr_BecomesLf() =>
        Assert.Equal("a\nb", CsvWriter.NormalizeEols("a\rb"));

    [Fact]
    public void NormalizeEols_Mixed_AllBecomeLf() =>
        Assert.Equal("a\nb\nc\nd", CsvWriter.NormalizeEols("a\r\nb\rc\nd"));

    // 恒等性: すでに LF のみなら 1 文字も変えない(過剰置換=CRLF を 2 個の LF にする変異を殺す)。
    [Fact]
    public void NormalizeEols_LfOnly_IsIdentity() =>
        Assert.Equal("a\nb\n", CsvWriter.NormalizeEols("a\nb\n"));

    // 改行を含まない値は素通し(空文字列を含む)。
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("a,b\"c")]
    public void NormalizeEols_NoBreaks_IsIdentity(string s) =>
        Assert.Equal(s, CsvWriter.NormalizeEols(s));
```

**Step 2: 赤を確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
```
Expected: **ビルド失敗** — `error CS0117: 'CsvWriter' に 'NormalizeEols' の定義がありません`。

**Step 3: 実装する**

`src/kxEdit.Core/Csv/CsvWriter.cs` の `EscapeField` の下に足す:

```csharp
    /// <summary>
    /// セル値の改行を LF へ正規化する。<see cref="CsvParser"/> は引用符内の CR / LF を
    /// literal のまま <c>CsvField.Value</c> へ積むため、<c>EditorControl.ConvertEols</c> の
    /// 前後でセル値の見かけが変わる。F2 確定値(<c>CsvCellEditor.Commit</c>)と
    /// パース結果の値を比較する側は、必ずこの規則で揃えてから比較すること
    /// (2026-09-01 設計書 §4.3)。
    /// </summary>
    public static string NormalizeEols(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace("\r\n", "\n").Replace("\r", "\n");
    }
```

続けて `src/kxEdit.App/CsvCellEditor.cs` の `Commit()` を寄せる:

```csharp
        string text = CsvWriter.NormalizeEols(_box.Text);
```

(置換前は `string text = _box.Text.Replace("\r\n", "\n").Replace("\r", "\n");`)

`CsvCellEditor.cs` は既に `using kxEdit.Core.Csv;` を持っているので using の追加は不要。
**確認すること**: 持っていなければ足す。

**Step 4: 緑を確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests  -c Release --no-build
```
Expected: 全部 PASS(App 側の既存 F2 テスト `BeginEdit_ThenCommit_...` / `BeginEdit_ThenCancel_...`
が緑のまま = 挙動不変の確認)。

**Step 5: commit**

```bash
git add src/kxEdit.Core/Csv/CsvWriter.cs src/kxEdit.App/CsvCellEditor.cs tests/kxEdit.Core.Tests/Csv/CsvWriterTests.cs
git commit -m "refactor(csv): セル値の EOL 正規化を CsvWriter に集約する

同一性検証(次タスク)と CsvCellEditor.Commit が同じ規則を使うため、
規則の持ち主を 1 つにする。挙動不変。"
```

**Step 6: レビュー(前倒し・コード品質)**

CLAUDE.md §3-4 の前倒し例外「後続タスクが依存する共通パターンを導入する」に該当するため、
**別エージェントによるコード品質レビュー**を 1 回行う。観点:
- `NormalizeEols` の置換順序(`\r\n` を先に潰さないと CRLF が LF 2 個になる)が網で守られているか
- `CsvCellEditor.Commit` の置換が挙動不変か(既存テストで足りているか)

---

## Task 2: 確定時に `(row, col)` から解決し直す(座標の持ち越しを消す)

**Files:**
- Modify: `src/kxEdit.App/CsvController.cs:250-272`
- Test: `tests/kxEdit.App.Tests/CsvControllerTests.cs`

### Step 1: テスト用の本文書き換えヘルパーを足す

CSV モード中は `EditorControl.ReadOnly = true` で、`ReplaceCharRange` / `SetSource` 系は
**ReadOnly のとき黙って no-op になる**(`EditorControl.cs:1182-1183`)。テストから本文を
差し替えるには production と同じく ReadOnly を一時的に落とす必要がある。
`CsvControllerTests.cs` の `GetOverlayBox` の下に足す:

```csharp
    /// <summary>F2 編集中に「別経路が本文を書き換えた」状況を作る。CSV モード中は
    /// ReadOnly=true で ReplaceCharRange が no-op になるため、production の onCommit と
    /// 同じ流儀で ReadOnly を一時的に落として書き、元へ戻す。</summary>
    private static void MutateBodyWhileEditing(EditorControl ed, Action<EditorControl> mutate)
    {
        bool wasRo = ed.ReadOnly;
        ed.ReadOnly = false;
        mutate(ed);
        ed.ReadOnly = wasRo;
    }
```

`ConvertEols` は ReadOnly を見ない(`EditorControl.cs:483-505` にガードが無い)。
これが**保存経路が CSV モード中でも本文を差し替えられる理由**そのものなので、
EOL 変換のテストではヘルパーを通さず直接呼んでよい。**実装時に確認すること**:
`ed.ConvertEols(LineEnding.Crlf)` が ReadOnly=true のまま `true` を返し本文が変わること。
返らないなら計画が誤り = ヘルパー経由に変える。

### Step 2: 失敗するテスト T1 / T2 を書く

`CsvControllerTests.cs` の F2 節(`BeginEdit_ThenCancel_...` の直後)に足す。
`using kxEdit.Core.Text;`(`LineEnding`)と `using kxEdit.Editor;`(`EditorControl`)が
ファイル先頭に無ければ足す。

fixture の EOL がそのまま本文になることは確認済み: `Host.NewCsvDoc` が使う
`EditorControl.Text` の setter は `TextBuffer.FromString(value)` に素通しするだけで
**EOL 正規化をしない**(`EditorControl.cs:258-262`)。`EolMode` の既定は `Crlf`
(`EditorControl.cs:1106`)だが、テストは `ConvertEols(LineEnding.Crlf)` を明示的に呼ぶので
既定値には依存しない。

```csharp
    // ===== M-25: F2 確定が「開始時の座標」を持ち越さないこと(2026-09-01 設計書) =====
    // 実運用の再現経路は「F2 編集中の Ctrl+S」。MainForm.ProcessCmdKey の CSV 素キー横取りは
    // !_csv.IsEditing で自分を無効化するため Ctrl+S はメニューショートカットへ素通りし、
    // FileController.SaveDocument が ConvertEols で本文を差し替える。ここではその 1 手
    // (ConvertEols)だけを直接呼んで、UI とファイル I/O を挟まずに同じ状態を作る。

    // セル内 LF を持つ混在 EOL。編集対象 (1,0) は自分自身に LF を含むので、ConvertEols で
    // 「長さ」も「Value」も変わる = 正規化を省いた同一性検証を殺せる fixture。
    // 前後に無傷であるべき行(a1,a2 / c1,c2)を置き、全書き換えと区別する。
    private const string MixedEolCsv = "a1,a2\r\n\"x\ny\",b2\r\nc1,c2";

    // 先行セルだけが LF を持つ混在 EOL。編集対象 (1,1)="b2" 自身は改行を含まないので、
    // 「オフセットが後ろへずれるだけ」のケースを T1 と分離できる。
    private const string ShiftOnlyCsv = "a1,\"p\nq\"\r\nb1,b2\r\nc1,c2";

    // kill 対象: onCommit が開始時の start/length を持ち越す変異(=修正前の実装そのもの)。
    [Fact]
    public void Commit_AfterEolConversion_WritesEditedCell_NotStaleOffsets() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, MixedEolCsv, 1, 0); // 非既定位置 (1,0) から開始
            host.Csv.BeginEdit();
            Assert.True(host.Csv.IsEditing);
            var editor = GetCellEditor(host.Csv);
            var box = GetOverlayBox(editor);
            box.Text = "NEW";

            // Ctrl+S 相当: 保存前の EOL 統一でバッファが差し替わる(セル内 LF → CRLF)。
            Assert.True(doc.Editor.ConvertEols(LineEnding.Crlf));

            editor.Commit();

            Assert.False(host.Csv.IsEditing);
            // (1,0) の "x\r\ny"(引用符込み 6 文字)だけが NEW になり、引用符も区切りも残らない。
            Assert.Equal("a1,a2\r\nNEW,b2\r\nc1,c2", doc.Editor.SnapshotText);
        });

    // kill 対象: 同上。編集セル自身は改行を含まず、オフセットだけがずれるケース。
    [Fact]
    public void Commit_AfterEolConversion_WritesShiftedCell_NotStaleOffsets() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, ShiftOnlyCsv, 1, 1); // 非既定位置 (1,1)="b2"
            host.Csv.BeginEdit();
            var editor = GetCellEditor(host.Csv);
            GetOverlayBox(editor).Text = "NEW";

            Assert.True(doc.Editor.ConvertEols(LineEnding.Crlf));

            editor.Commit();

            Assert.Equal("a1,\"p\r\nq\"\r\nb1,NEW\r\nc1,c2", doc.Editor.SnapshotText);
        });
```

### Step 3: 赤を確認する(欠陥の存在証明)

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~Commit_AfterEolConversion"
```
Expected: **2 件とも FAIL**。実際の差分(`Assert.Equal` の Actual)を**設計書 §8 へ逐語で控える**。
想定は次のとおりだが、**一致しなければ実測を正とする**:
- T1 Actual: `a1,a2\r\nNEW",b2\r\nc1,c2`(閉じ引用符が残る)
- T2 Actual: `a1,"p\r\nq"\r\nb1NEW\r\nc1,c2`(区切りカンマが食われる)

**この赤が出ないなら先へ進まない。** fixture が短すぎて実は変換が起きていない
(`ConvertEols` が fast-path で `false` を返した)可能性を先に潰すこと。

### Step 4: 実装する

`src/kxEdit.App/CsvController.cs` の `BeginEdit` を書き換える。**`int start, length;` の
宣言を消す**(`EnsureVisibleCharRange` と `CsvCellEditor.Begin` は `f` から直接読む)。

置換前(`:250-272`):

```csharp
        int start = f.Start,
            length = f.Length;
        // オーバーレイの配置座標（PointFromCharOffset）は可視領域基準なので、
        // ナビ後にリサイズ等で当該セルが視野外へずれていた場合に備えて明示的に可視化する。
        ed.EnsureVisibleCharRange(start, length);
        ...
            onCommit: text =>
            {
                string serialized = CsvWriter.EscapeField(text);
                bool wasRo = ed.ReadOnly;
                ed.ReadOnly = false;
                ed.ReplaceCharRange(start, length, serialized);
                ed.ReadOnly = wasRo;
```

置換後:

```csharp
        // オーバーレイの配置座標（PointFromCharOffset）は可視領域基準なので、
        // ナビ後にリサイズ等で当該セルが視野外へずれていた場合に備えて明示的に可視化する。
        ed.EnsureVisibleCharRange(f.Start, f.Length);
        ...
            onCommit: text =>
            {
                // M-25(2026-09-01): 開始時の f.Start / f.Length を**持ち越さない**。F2 開始から
                // 確定までの間に本文が差し替わりうるため(到達経路 = F2 編集中の Ctrl+S →
                // FileController.SaveDocument の ConvertEols。設計書 §2.2 / §3)、確定時の
                // パースから (row, col) で解決し直す。row / col は編集中に動かない
                // (ナビは TryContext 冒頭の _editor.IsEditing で撥ねられる)。
                // ParseCsv はスナップショット参照が同じなら開始時と同一インスタンスを返すので、
                // 本文が変わっていない通常経路に追加コストは無い。
                var csvNow = doc.ParseCsv();
                var target = csvNow.Ok ? csvNow.GetField(row, col) : null;
                if (target is null)
                {
                    _announcer.Say(CsvAnnounceFormatter.ParseError);
                    return;
                }
                string serialized = CsvWriter.EscapeField(text);
                bool wasRo = ed.ReadOnly;
                ed.ReadOnly = false;
                ed.ReplaceCharRange(target.Start, target.Length, serialized);
                ed.ReadOnly = wasRo;
```

> **Task 2 では `target is null` を `ParseError` で暫定的に受ける。** 専用文言と同一性検証は
> Task 3 で入れる(Task 3 のテストが赤で始まるようにするため)。

### Step 5: 緑を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```
Expected: 新規 2 件 PASS + 既存の F2 テスト(`BeginEdit_ThenCommit_...` /
`BeginEdit_ThenCancel_...`)も PASS(本文を変えない通常経路は挙動不変)。

### Step 6: commit

```bash
git add src/kxEdit.App/CsvController.cs tests/kxEdit.App.Tests/CsvControllerTests.cs
git commit -m "fix(app): F2 確定を開始時の座標ではなく確定時の (row,col) で解決する(M-25)

F2 開始時に捕捉した start/length は、確定までに本文が差し替わると
古い世界を指したまま残り別位置を書き換える。到達経路は F2 編集中の
Ctrl+S → ConvertEols。onCommit のクロージャから座標を消し、確定時の
パースから (row,col) で解決し直す。"
```

### Step 7: 仕様レビュー

別エージェントで「実装・テストが設計書 §4 / §4.1 どおりか」を確認する。特に:
- `start` / `length` のローカル変数が**残っていない**こと(残っていれば陳腐化の余地も残る)
- T1 / T2 の fixture が全書き換えと区別できること(前後の行が無傷であることを assert しているか)

---

## Task 3: 解決先が別セルなら書かずに発声する(同一性の検証)

**Files:**
- Modify: `src/kxEdit.Core/Csv/CsvAnnounceFormatter.cs`
- Modify: `src/kxEdit.App/CsvController.cs`(Task 2 で書き換えた `onCommit`)
- Test: `tests/kxEdit.App.Tests/CsvControllerTests.cs`
- Test: `tests/kxEdit.Core.Tests/Csv/CsvAnnounceFormatterTests.cs`(定数の存在確認が既存様式にあれば)

### Step 1: 失敗するテスト T3 / T4 を書く

Task 2 のテストの直後に足す:

```csharp
    // ===== M-25: (row,col) が別セルを指していたら書かない(設計書 §4.2) =====
    // この 2 本が踏む枝は、現行の配線では実運用から到達できない。到達経路は §3 の表のとおり
    // Ctrl+S → ConvertEols の 1 本だけで、ConvertEols は CSV の行列構造を変えないため
    // 同一性検証は必ず一致する。ここはテストからだけ踏める「将来配線が増えたときの受け皿」で、
    // 網があること自体を安全宣言に使ってはならない。

    // kill 対象: 同一性検証の削除(=(row,col) を無条件に信じて別セルへ書く変異)。
    [Fact]
    public void Commit_WhenCellAtRowColBecameAnotherCell_WritesNothing_AndAnnounces() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, Grid3x3, 1, 1); // (1,1)="b2" を編集開始
            host.Csv.BeginEdit();
            var editor = GetCellEditor(host.Csv);
            GetOverlayBox(editor).Text = "NEW";

            // (1,1) の中身だけを別物へ差し替える = 座標は生きているがセルは別物。
            MutateBodyWhileEditing(doc.Editor, ed => ed.ReplaceCharRange(12, 2, "ZZ"));
            string afterMutation = doc.Editor.SnapshotText;
            Assert.Equal("a1,a2,a3\nb1,ZZ,b3\nc1,c2,c3", afterMutation); // 前提の固定
            host.Announcer.Said.Clear();

            editor.Commit();

            Assert.False(host.Csv.IsEditing);
            Assert.Equal(afterMutation, doc.Editor.SnapshotText); // 1 文字も書いていない
            Assert.Equal(CsvAnnounceFormatter.CommitTargetChanged, host.Announcer.Said[^1]);
        });

    // kill 対象: GetField の null 判定削除(NullReferenceException)・ParseError 文言のまま放置。
    [Fact]
    public void Commit_WhenCellAtRowColDisappeared_WritesNothing_AndAnnounces() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, Grid3x3, 2, 2); // 最終行 (2,2)="c3" を編集開始
            host.Csv.BeginEdit();
            var editor = GetCellEditor(host.Csv);
            GetOverlayBox(editor).Text = "NEW";

            // 3 行目ごと削る = GetField(2,2) が null になる。
            MutateBodyWhileEditing(doc.Editor, ed => ed.ReplaceCharRange(17, 9, ""));
            string afterMutation = doc.Editor.SnapshotText;
            Assert.Equal("a1,a2,a3\nb1,b2,b3", afterMutation); // 前提の固定
            host.Announcer.Said.Clear();

            editor.Commit();

            Assert.False(host.Csv.IsEditing);
            Assert.Equal(afterMutation, doc.Editor.SnapshotText);
            Assert.Equal(CsvAnnounceFormatter.CommitTargetChanged, host.Announcer.Said[^1]);
        });
```

**オフセットは実装時に必ず数え直すこと。** 計画の `12` / `17` / `9` は
`Grid3x3 = "a1,a2,a3\nb1,b2,b3\nc1,c2,c3"` を前提にした手計算である
(`a1,a2,a3`=0..7 / `\n`=8 / `b1,b2,b3`=9..16 / `\n`=17 / `c1,c2,c3`=18..25)。
**`Assert.Equal(..., afterMutation)` の前提固定が先に落ちたら、そこで数え直す。**

### Step 2: 赤を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
```
Expected: **ビルド失敗** — `CsvAnnounceFormatter.CommitTargetChanged` が未定義。
定数だけ先に足してから再実行し、**テストが FAIL** になることを見る:
- T3: 本文が `a1,a2,a3\nb1,NEW,b3\nc1,c2,c3` になる(別セルへ書いてしまう)
- T4: `ParseError` を発声している(文言不一致)

### Step 1b: 形の変化を踏むテストを 2 本足す(2026-09-01 Task 1 品質レビュー由来の精密化)

値の一致だけでは同一性の代用として弱い。**「別セルになったが値は同じ」を素通しする**からで、
CSV では空セルや繰り返し値がありふれているため、§4.2 が名指しする「行が消える・列が増える」が
実際に起きても値が一致すれば guard を通ってしまう。開始時の `Rows.Count` と
`Rows[row].Count` も併せて比べる(2 比較)。下の 2 本は**その形の検査だけが殺せる**網である。

```csharp
    // ===== M-25: 形が変われば値が一致していても書かない =====
    // 下 2 本は「値の一致」だけの guard では素通りする。形(行数・その行の列数)の検査だけが殺す。

    // 行が消えて (row,col) が「同じ値の別セル」を指す。
    [Fact]
    public void Commit_WhenRowCountChanged_AndValueCoincides_WritesNothing_AndAnnounces() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, "p,q\nX,Y\nX,Y", 1, 0); // (1,0)="X" を編集開始
            host.Csv.BeginEdit();
            var editor = GetCellEditor(host.Csv);
            GetOverlayBox(editor).Text = "NEW";

            // 先頭行 "p,q\n" を削る → (1,0) は 3 行目だった "X" を指す = 値は一致するが別セル。
            MutateBodyWhileEditing(doc.Editor, ed => ed.ReplaceCharRange(0, 4, ""));
            string afterMutation = doc.Editor.SnapshotText;
            Assert.Equal("X,Y\nX,Y", afterMutation);
            host.Announcer.Said.Clear();

            editor.Commit();

            Assert.Equal(afterMutation, doc.Editor.SnapshotText);
            Assert.Equal(CsvAnnounceFormatter.CommitTargetChanged, host.Announcer.Said[^1]);
        });

    // 列が増えて (row,col) が「同じ値の別セル」を指す。
    [Fact]
    public void Commit_WhenColumnCountChanged_AndValueCoincides_WritesNothing_AndAnnounces() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, "p,q\nX,X", 1, 1); // (1,1)="X" を編集開始
            host.Csv.BeginEdit();
            var editor = GetCellEditor(host.Csv);
            GetOverlayBox(editor).Text = "NEW";

            // 2 行目の先頭へ列を 1 つ挿す → (1,1) は元 (1,0) だった "X" を指す。
            MutateBodyWhileEditing(doc.Editor, ed => ed.ReplaceCharRange(4, 0, "X,"));
            string afterMutation = doc.Editor.SnapshotText;
            Assert.Equal("p,q\nX,X,X", afterMutation);
            host.Announcer.Said.Clear();

            editor.Commit();

            Assert.Equal(afterMutation, doc.Editor.SnapshotText);
            Assert.Equal(CsvAnnounceFormatter.CommitTargetChanged, host.Announcer.Said[^1]);
        });
```

**オフセットは実装時に必ず数え直すこと**(`"p,q\nX,Y\nX,Y"` は `p`=0 / `\n`=3 / `"p,q\n"`=0..3、
`"p,q\nX,X"` は 2 行目が 4 から始まる、という手計算)。前提固定の `Assert.Equal` が先に落ちたら
そこで数え直す。

**この検査でも弁別できない残りの限界**(行数・列数・値がすべて一致する別セル。例: 2 行の入れ替え)は
コメントに明記すること。**「同一性を検証している」と読める書き方をしない**(強すぎる安全宣言になる)。

### Step 3: 実装する

`src/kxEdit.Core/Csv/CsvAnnounceFormatter.cs` の `BlockedInCsvMode` の下に足す:

```csharp
    /// <summary>F2 セル編集の確定時に、編集対象のセルが別物へ変わっていた/消えていたときの読み上げ。
    /// 本文へは一切書かずにこれだけを発声する(2026-09-01 設計書 §4.2)。</summary>
    public const string CommitTargetChanged = "本文が変わったため確定できません";
```

`src/kxEdit.App/CsvController.cs` の `_editor.Begin(...)` の**手前**(まだ `f` が生きている位置)で、
確定時に要る値を**スカラーだけ**捕捉する:

```csharp
        // 確定時の同一性検査に要る値を、ここでスカラーとして取り出す。
        // CsvField f そのものをクロージャへ捕捉してはいけない —— Start / Length が構造的に
        // 残り、Task 2 で消した陳腐化の余地が f 経由で復活する。設計書 §4 の芯
        //(陳腐化しうる値を持ち越さない)は字面で守られて初めて後続の改変に耐える。
        // 正規化は開始時に 1 回だけ行う(単一セルは最大 8M chars = CsvParser.MaxFieldChars)。
        string startValue = CsvWriter.NormalizeEols(f.Value);
        int startRowCount = csv.Rows.Count;
        int startColCount = csv.Rows[row].Count;
```

そのうえで `onCommit`(Task 2 で入れた null 判定)を差し替える:

```csharp
                var csvNow = doc.ParseCsv();
                var target = csvNow.Ok ? csvNow.GetField(row, col) : null;
                // (row, col) が生きていても、本文が変わっていればそこが指すセルは別物でありうる
                // (行が消える・列が増える等)。そこへ書けば座標が陳腐化しているのと同じ
                // データ破壊になるので、「同じセルらしさ」が崩れていたら書かない。
                //  - 値の一致だけでは弱い。「別セルになったが値は同じ」を素通しする
                //    (CSV では空セルや繰り返し値がありふれている)ので、形も見る。
                //  - EOL を正規化して比べるのは、ConvertEols がセル内改行を書き換えて
                //    Value 自体を変えるため(設計書 §4.3)。
                // これは同一性の**代用**であって同一性の証明ではない。行数・列数・値が
                // すべて一致する別セル(例: 2 行の入れ替え)は弁別できない。
                if (
                    target is null
                    || csvNow.Rows.Count != startRowCount
                    || csvNow.Rows[row].Count != startColCount
                    || !string.Equals(
                        CsvWriter.NormalizeEols(target.Value),
                        startValue,
                        StringComparison.Ordinal
                    )
                )
                {
                    _announcer.Say(CsvAnnounceFormatter.CommitTargetChanged);
                    return;
                }
```

`csvNow.Rows[row]` が安全なのは `target is null` を**先に**短絡しているからである
(`GetField` が非 null を返した = `row` / `col` は範囲内)。**この短絡順序を入れ替えないこと。**

条件式が長いので private static ヘルパーへ括ってよいが、**括るなら引数もスカラーにする**
(`CsvField` を渡すと捕捉禁止の趣旨が呼び出し側から見えなくなる)。整形は CSharpier に任せる。

### Step 4: 緑を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests  -c Release --no-build
```
Expected: 全 PASS。**特に Task 2 の T1 が緑のままであること**を確認する ——
T1 は編集セル自身の `Value` が `x\ny` → `x\r\ny` に変わるので、**正規化を忘れると
ここで確定が拒否され赤になる**。T1 は正規化の網でもある。

### Step 5: commit

```bash
git add src/kxEdit.Core/Csv/CsvAnnounceFormatter.cs src/kxEdit.App/CsvController.cs tests/kxEdit.App.Tests/CsvControllerTests.cs
git commit -m "fix(app): 確定先が別セルになっていたら書かずに知らせる(M-25)

(row,col) が生きていても、本文が変われば指すセルは別物になりうる。
開始時のセル値と確定時のセル値を EOL 正規化して比較し、一致しなければ
本文へ触れずに発声する。EOL 正規化は ConvertEols がセル内改行を
書き換えるため必須(設計書 §4.3)。"
```

### Step 6: 仕様レビュー

別エージェントで確認する。観点:
- T3 / T4 のコメントが「到達不能な枝である」ことを明記しているか(嘘の安全宣言を作らない)
- 正規化を外す変異で T1 が落ちること(= T1 が正規化の網であるという主張の裏取り)を
  **実際に手で 1 回試して**確認する(規則の削除 → 赤 → 戻す)

---

## Task 4: L5 チェックリストと設計書の実施記録

**Files:**
- Create: `docs/plans/2026-09-01-csv-f2-commit-staleness-l5-checklist.md`
- Modify: `docs/plans/2026-09-01-csv-f2-commit-staleness-design.md`(§8 実施記録)

### Step 1: L5 チェックリストを書く

既存の `docs/plans/2026-08-31-grep-jump-line-resolution-l5-checklist.md` の様式に合わせる
(**着手時に必ず 1 本開いて様式を確認すること**)。項目:

1. **混在 EOL の CSV で F2 → Ctrl+S → Enter。** 事前に用意する fixture は
   「セル内改行を持つセル」と「その後ろの行のセル」の 2 種類。確定後に
   **編集したセルの値が読み上げられる**こと、左右/上下へ移動して前後のセルが壊れていないこと。
2. **項目 1 のあと Ctrl+S → タブを閉じて開き直す。** 内容が画面と一致すること。
3. **F2 → Ctrl+S → Esc**(取消側)。本文が 1 文字も変わらないこと。
4. `CommitTargetChanged`(「本文が変わったため確定できません」)の発声。
   **現行の配線では到達経路が無いため、実機で踏めない見込み**。踏めないなら
   「未確認・到達経路なし」と**そう書く**(「確認済み」にしない)。

### Step 2: 設計書 §8 に実施記録を書く

Task 1〜3 で**計画と実物が食い違った点**をすべて書く。最低限:
- Task 2 Step 3 で観測した赤の Actual(逐語)
- `ConvertEols` が ReadOnly=true でも本文を差し替えたか(§4.4 の論証の実測化)
- fixture のオフセット手計算が合っていたか

**§4.4 は「論証であって実測ではない」と書いてある。** T1 / T2 が緑になった時点で
「`ConvertEols` の前後で `(row,col)` の指すセルの正規化後 `Value` が不変」は実測されたので、
§8 でその旨を記録して昇格させる(§4.4 の本文は書き換えない = スナップショット原則)。

### Step 3: commit

```bash
git add docs/plans/
git commit -m "docs(plans): B3 の L5 チェックリストと実施記録を書く"
```

### Step 4: レビュー

docs のみの変更でも CLAUDE.md §4 により**別エージェントレビューは省略しない**。

---

## Task 5: 最終ブランチレビュー(2 パス)

CLAUDE.md §3-5。**パスごとに独立した別エージェントを起動する**(1 起動に混載しない)。

1. **コード品質パス** — ブランチ全体(`git diff main...HEAD`)。観点:
   - `onCommit` に陳腐化しうる値が残っていないか
   - 同一性検証の条件式が「網が無い」まま入っていないか(条件ごとに 1 行ずつ変異させて確かめる)
   - テストの fixture が主張の範囲を実際に覆っているか(全書き換えと区別できているか)
2. **脆弱性パス** — 同じ差分。本ブランチはパーサ・パス操作・プロセス起動・WebView・
   ネットワークのいずれにも触れないため、観点は**データ完全性**に絞る:
   書込先の決定に外部入力(CSV 本文)がどう効くか、極端なセル値(巨大・制御文字・
   サロゲート)で書込先がずれないか。

指摘は CLAUDE.md §4 の 3 択(① fixup commit / ② PR description に記載して受容 /
③ 理由付き却下)で明示し、**元 commit を書き換えず fixup commit で積む**。

**ミューテーション検証は行わない**(設計書 §5.1・CLAUDE.md §4-A の「有効」列に当たらない)。

---

## Task 6: 品質ゲート → PR

### Step 1: ゲート

```bash
pwsh tools/pre-merge-check.ps1
```
Expected: **EXIT 0**。0 warning を維持していること。

### Step 2: PR

```bash
git push -u origin feature/csv-f2-commit-staleness
gh pr create --base main --title "fix: CSV F2 セル確定の座標陳腐化を直す(M-25 / B3)" --body "..."
```

PR description は日本語で、目的・レビュー経緯・申し送りを書く(CLAUDE.md §7)。
**申し送りに必ず含める**(設計書 §7):
- F2 編集中の Ctrl+S は「編集前の内容」をディスクへ書く(修正後も変わらない)。
- `CsvWriter.EscapeField` はセル内改行を常に `\n` で書く(本件以前からの挙動)。
- **L5 は未実施**。傘設計書 §7.1 の「新規」欄へ合流させ、B1〜B6 完了後にまとめて回す。
