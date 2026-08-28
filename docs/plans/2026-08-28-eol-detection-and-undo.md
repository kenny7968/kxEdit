# 改行コード判定窓と EOL 変換の Undo 消失(A-9 / A-11)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 上書き保存で本文が無警告に書き換わる導線(A-9)を塞ぎ、保存時の EOL 一括変換を Undo で戻せるようにする(A-11)。

**Architecture:** A-9 は `TextFileService` の改行判定を「読み込み後バッファ先頭 4,096 文字の `string` 化」から「PieceTree 全体の byte 走査」へ移す。A-11 は `UndoHistory` のエントリが永続木の root 参照だけを持つ性質を使い、`EditorControl.ConvertEols` の `ReplaceSource(新 TextBuffer)` を「同一 TextBuffer への 1 Undo 単位の全文差し替え」へ置き換える。差し替えが in-place になることで `FileController.WriteToPath` の保存失敗ロールバック(旧バッファ参照へ戻す機構)が成立しなくなるため、Undo ベースへ組み替える。

**Tech Stack:** C# / .NET 9 / WinForms、xUnit(L1 `kxEdit.Core.Tests` / L2 `kxEdit.Editor.Tests` / L3 `kxEdit.App.Tests`)、CSharpier(pre-commit フックで自動整形)

**設計書:** [2026-08-28-eol-detection-and-undo-design.md](./2026-08-28-eol-detection-and-undo-design.md)

**ブランチ:** `feature/eol-detection-and-undo`(作成済み・設計書 commit 済み)

---

## 実装前に読むもの

- 設計書 §4(A-9)・§5(A-11)・§7(テスト)
- `CLAUDE.md` §4-A(ミューテーション検証の適用範囲)・§4-B(テスト設計の教訓)・§6(品質ゲート)
- `src/kxEdit.Core/Buffer/UndoHistory.cs` 全体(30 行程度。Task 2 の前提)

## 全タスク共通の約束

- **タスクごとに commit する**。レビュー由来の修正は元 commit を書き換えず fixup commit で積む(CLAUDE.md §4)。
- コミットメッセージは `feat|fix|test|refactor|docs(scope): 要約` + 日本語本文。
- pre-commit フック(CSharpier 整形 + ローカルパス検出)を `--no-verify` で飛ばさない。
- ビルドは `-warnaserror` が効いている。**0 warning を維持する**。
- 新しいテストは **先に書いて赤を確認してから**実装する。特に A-9 / A-11 の回帰網は
  「旧実装で確実に落ちる」ことがテストの価値そのものなので、赤の確認を省略しない。

### ビルド / テストコマンド

```bash
# ビルド(0 warning ゲート)
dotnet build kxEdit.sln -c Release -warnaserror

# 層ごと
dotnet test tests/kxEdit.Core.Tests   -c Release --no-build
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests    -c Release --no-build

# 単一テスト
dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~Detect_LfFile_WithFirstLineLongerThanProbeWindow"
```

---

## Task 1: A-9 — 改行判定を全文バイト走査へ

**Files:**
- Modify: `src/kxEdit.Core/Text/LineEnding.cs`(`LineEndingDetector` に overload を追加)
- Modify: `src/kxEdit.Core/Text/TextFileService.cs:186-192`
- Test: `tests/kxEdit.Core.Tests/Text/LineEndingDetectorTests.cs`
- Test: `tests/kxEdit.Core.Tests/Text/TextFileServiceLoadAsBufferAutoTests.cs`

### Step 1: 失敗するテストを書く(A-9 の本体回帰網)

`tests/kxEdit.Core.Tests/Text/TextFileServiceLoadAsBufferAutoTests.cs` の末尾クラス内へ追加。
`using kxEdit.Core.Text;` は既存。

```csharp
    // A-9(監査 2026-08-22): 改行判定が先頭 4,096 文字窓だったため、1 行目が窓より長い
    // LF ファイル(ミニファイ JSON・長いヘッダ行の CSV)が CRLF と誤判定され、
    // Ctrl+S で全行 CRLF 化されていた(Modified も立たず警告も出ない)。
    // fixture の要件: 先頭 4,096 文字に改行を 1 つも含まないこと=旧実装が必ず落ちる形。
    [Fact]
    public void LoadAuto_LfFile_FirstLineLongerThanOldProbeWindow_DetectsLf()
    {
        string path = Path.GetTempFileName();
        try
        {
            string body = new string('a', 5000) + "\n" + new string('b', 10) + "\n";
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(body));
            var loaded = TextFileService.LoadAsBufferAuto(path);
            Assert.Equal(LineEnding.Lf, loaded.LineEnding);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A-9: CR 単独版。旧実装は同じく CRLF 既定へ倒れていた。
    [Fact]
    public void LoadAuto_CrFile_FirstLineLongerThanOldProbeWindow_DetectsCr()
    {
        string path = Path.GetTempFileName();
        try
        {
            string body = new string('a', 5000) + "\r" + new string('b', 10) + "\r";
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(body));
            var loaded = TextFileService.LoadAsBufferAuto(path);
            Assert.Equal(LineEnding.Cr, loaded.LineEnding);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A-9: 窓の外にある多数派が判定に効くこと。先頭 4,096 文字には CRLF が 1 つだけあり、
    // 窓の外に LF が多数ある = 旧実装は CRLF、新実装は LF を返す。
    // (「窓を撤廃した」ことの証拠であって、「改行 0 件のときだけ延長した」では緑にならない)
    [Fact]
    public void LoadAuto_MajorityLfOutsideOldProbeWindow_DetectsLf()
    {
        string path = Path.GetTempFileName();
        try
        {
            string body = new string('a', 4000) + "\r\n" + string.Concat(
                Enumerable.Repeat("x\n", 50)
            );
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(body));
            var loaded = TextFileService.LoadAsBufferAuto(path);
            Assert.Equal(LineEnding.Lf, loaded.LineEnding);
        }
        finally
        {
            File.Delete(path);
        }
    }
```

`Enumerable` を使うので、ファイル先頭に `using System.Linq;` が無ければ追加する。

### Step 2: 赤を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~OldProbeWindow"
```

期待: **3 件とも FAIL**。`Assert.Equal(LineEnding.Lf, ...)` が `Crlf` を受け取って落ちる。

> ここで緑になったら fixture が要件を満たしていない(先頭 4,096 文字に改行が入ってしまっている)。
> 先へ進まず fixture を直すこと。

### Step 3: `LineEndingDetector` に snapshot 版を足す

`src/kxEdit.Core/Text/LineEnding.cs` の `LineEndingDetector` へ追加する。
`TextSnapshot` / `PieceTree` は `kxEdit.Core.Buffers` 名前空間なので `using` を足す。

```csharp
using kxEdit.Core.Buffers;
```

```csharp
    /// <summary>
    /// A-9(2026-08-28): スナップショット全体を byte 走査して最も多い改行種別を返す。
    /// 改行が無ければ CRLF(Windows 既定=<see cref="Detect(string)"/> と同じ規則)。
    /// </summary>
    /// <remarks>
    /// <see cref="Detect(string)"/> と多数決の意味論は同一で、走査範囲だけが違う
    /// (旧実装は先頭 4,096 文字を <c>GetText</c> して string 化していた=1 行目が窓より長い
    /// LF ファイルが CRLF と誤判定され、保存時に全行が書き換わっていた)。
    /// string を実体化しないので 512MB 級の文書でもピークメモリは増えない。
    /// UTF-8 では 0x0D / 0x0A がマルチバイト文字の継続バイト(0x80 以上)として現れないため、
    /// byte 走査と char 走査は同じ結果になる。
    /// CR がピース境界を跨ぐケースは <c>pendingCr</c> で持ち越す(落とすと 4MB チャンク境界の
    /// CRLF が CR + LF に化けて多数決が反転しうる)。
    /// </remarks>
    public static LineEnding Detect(TextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int crlf = 0,
            lf = 0,
            cr = 0;
        bool pendingCr = false;
        foreach (var piece in PieceTree.Enumerate(snapshot.Root))
        {
            var span = piece.Chunk.Span.Slice(piece.ByteStart, piece.ByteLen);
            for (int i = 0; i < span.Length; i++)
            {
                byte b = span[i];
                if (pendingCr)
                {
                    // 前ピース末尾の CR を持ち越し中。今の byte が LF なら CRLF、
                    // それ以外なら CR 単独として数えてから今の byte を通常処理へ進める。
                    pendingCr = false;
                    if (b == 0x0A)
                    {
                        crlf++;
                        continue;
                    }
                    cr++;
                }
                if (b == 0x0D)
                {
                    if (i + 1 < span.Length)
                    {
                        if (span[i + 1] == 0x0A)
                        {
                            crlf++;
                            i++;
                        }
                        else
                            cr++;
                    }
                    else
                        pendingCr = true; // ピース末尾 CR=次ピース先頭を見ないと判別不能
                }
                else if (b == 0x0A)
                    lf++;
            }
        }
        if (pendingCr)
            cr++; // 文書末尾の単独 CR
        if (crlf == 0 && lf == 0 && cr == 0)
            return LineEnding.Crlf;
        if (crlf >= lf && crlf >= cr)
            return LineEnding.Crlf;
        return lf >= cr ? LineEnding.Lf : LineEnding.Cr;
    }
```

**判定の後半 5 行は `Detect(string)` と完全に同一にすること**(意味論を変えない)。

> `TextSnapshot.Root` / `Piece` / `TextChunk` は `internal` だが、`LineEndingDetector` は
> 同一アセンブリ(`kxEdit.Core`)なので参照できる。

### Step 4: `TextFileService` の窓を撤廃する

`src/kxEdit.Core/Text/TextFileService.cs:186-192` を置き換える。

置換前:

```csharp
        // 4) LineEnding 検出。バッファ先頭 4KB を GetText して LineEndingDetector に流す
        //    (空バッファなら 0 バイト=CRLF 既定)。
        var snap = buffer.Current;
        int probeChars = Math.Min(4096, snap.CharLength);
        string lineProbe = probeChars > 0 ? snap.GetText(0, probeChars) : string.Empty;
        LineEnding eol = LineEndingDetector.Detect(lineProbe);
```

置換後:

```csharp
        // 4) LineEnding 検出。A-9(2026-08-28): 先頭 4,096 文字窓を撤廃し、バッファ全体を
        //    byte 走査する(string 化なし)。旧実装は 1 行目が窓より長い LF ファイルを
        //    CRLF と誤判定し、Ctrl+S で全行を無警告に書き換えていた。
        //    窓は P6 Task 10 の Stream 化で入った退行で、旧 DecodeBytes は全文判定だった。
        LineEnding eol = LineEndingDetector.Detect(buffer.Current);
```

`Math` の using が他で使われていなければ警告になりうるので、ビルドの 0 warning で確認する。

### Step 5: 緑を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build
```

期待: 新規 3 件 PASS。**既存の `LoadAuto_*` が 1 件も落ちないこと**
(特に `LoadAuto_EmptyFile_UsesUtf8Default_AndReturnsEmptyBuffer` の `LineEnding.Crlf` 既定)。

### Step 6: 意味論不変とチャンク境界の網を足す

`tests/kxEdit.Core.Tests/Text/LineEndingDetectorTests.cs` へ追加。
`using kxEdit.Core.Buffers;` を足す。

```csharp
    // A-9: snapshot 版と string 版で多数決の意味論が一致すること(走査範囲だけが違う)。
    [Theory]
    [InlineData("a\r\nb")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\nb\nc\r\nd")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("a\r\nb\rc\nd\r\ne")]
    public void Snapshot_overload_matches_string_overload(string text) =>
        Assert.Equal(
            LineEndingDetector.Detect(text),
            LineEndingDetector.Detect(TextBuffer.FromString(text).Current)
        );

    // A-9: CRLF が 4MB チャンク境界を跨いでも CR + LF に割れないこと(pendingCr の持ち越し)。
    // 割れると CRLF 1 件が CR 1 件 + LF 1 件になり、多数決が反転しうる。
    // fixture は EditorControlConvertEolsTests の同名パターンに合わせる。
    [Fact]
    public void Snapshot_overload_counts_crlf_spanning_chunk_boundary_as_one()
    {
        int fill = 4 * 1024 * 1024 - 1; // TextBufferBuilder.TargetChunkBytes - 1
        string body = new string('a', fill) + "\r\n" + "tail\r\n";
        Assert.Equal(
            LineEnding.Crlf,
            LineEndingDetector.Detect(TextBuffer.FromString(body).Current)
        );
    }

    // A-9: 文書末尾の単独 CR が drain されること(foreach 後の `if (pendingCr) cr++`)。
    [Fact]
    public void Snapshot_overload_counts_trailing_lone_cr()
    {
        int fill = 4 * 1024 * 1024 - 1;
        string body = new string('a', fill) + "\r";
        Assert.Equal(LineEnding.Cr, LineEndingDetector.Detect(TextBuffer.FromString(body).Current));
    }
```

### Step 7: 走らせて緑を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build
```

### Step 8: commit

```bash
git add src/kxEdit.Core/Text/LineEnding.cs src/kxEdit.Core/Text/TextFileService.cs \
        tests/kxEdit.Core.Tests/Text/LineEndingDetectorTests.cs \
        tests/kxEdit.Core.Tests/Text/TextFileServiceLoadAsBufferAutoTests.cs
git commit -m "$(cat <<'EOF'
fix(core): 改行コード判定の 4,096 文字窓を撤廃する(A-9)

1 行目が 4,096 文字を超える LF / CR ファイル(ミニファイ JSON・
長いヘッダ行の CSV)が CRLF と誤判定され、Ctrl+S で全行が無警告に
書き換わっていた。判定を PieceTree の byte 走査へ移し、string 化なしで
全文を数える。多数決の規則は Detect(string) と同一に保つ。

窓は P6 Task 10 の Stream 化で入った退行で、旧 DecodeBytes は全文判定だった。
EOF
)"
```

### Task 1 の仕様レビュー観点

- 新旧 overload の多数決規則が文字単位で一致しているか(コピー漏れ)。
- `pendingCr` の drain(foreach 後の `cr++`)が抜けていないか。
- 空バッファ(`Root == null`)で `PieceTree.Enumerate` が例外を投げないか。

---

## Task 2: A-11(Core)— 記録付きの全文差し替え API

**Files:**
- Modify: `src/kxEdit.Core/Buffer/TextBuffer.cs`
- Test: `tests/kxEdit.Core.Tests/Buffer/TextBufferReplaceAllTests.cs`(新規)

> **前倒しレビュー対象**: 後続 Task 3 / 4 が依存する新しい seam なので、CLAUDE.md §3 に従い
> このタスク完了時に**コード品質レビュー**を別エージェントで実施する。
> また CLAUDE.md §4-A の「UNDO/REDO の履歴管理アルゴリズム」に該当するため
> **ミューテーション検証**を行う(Task 2 の Step 7)。

### Step 0: 依存する不変条件を検証する(設計書 §5.1 の受け入れ条件)

設計は「`TextBufferBuilder` が作った別チャンク由来の root を、既存 `TextBuffer` へ取り込んでよい」
という前提に立つ。**依存する前に確認する**。

```bash
grep -rn "_append" src/kxEdit.Core/Buffer/TextBuffer.cs
grep -rn "class AppendBuffer" -A 40 src/kxEdit.Core/Buffer/AppendBuffer.cs
```

確認すること:

1. `TextBuffer._append` が**新規挿入テキストの置き場としてしか使われていない**
   (`Splice` の `_append.Append(insert)` 以外に参照が無い)。
2. `PieceTree` / `TextSnapshot` の読み取り経路が `Piece.Chunk` だけを見ており、
   「chunk が `_append` 由来である」ことを仮定していない。

**1 か 2 が成り立たなければ実装を止め、設計書 §5.1 へ反証を追記してユーザーへ報告する。**
成り立つ場合は、確認結果を設計書 §10 の申し送りへ 2〜3 行で記録する。

### Step 1: 失敗するテストを書く

`tests/kxEdit.Core.Tests/Buffer/TextBufferReplaceAllTests.cs` を新規作成。
名前空間は既存の同ディレクトリに合わせて `kxEdit.Core.Tests.Buffers`(**複数形**。
ディレクトリ名 `Buffer/` と一致しない)にする。

```csharp
using kxEdit.Core.Buffers;
using Xunit;

namespace kxEdit.Core.Tests.Buffers;

/// <summary>
/// A-11(監査 2026-08-22): 保存時の EOL 一括変換が ReplaceSource で新規 TextBuffer に
/// 差し替わり、Undo/Redo 履歴を全消去していた。全文差し替えを 1 Undo 単位として
/// 記録する API の契約テスト。
/// </summary>
public class TextBufferReplaceAllTests
{
    private static TextBuffer Rebuilt(string text) => TextBuffer.FromString(text);

    [Fact]
    public void ReplaceAllRecordingUndo_Undo_RestoresPreviousText()
    {
        var buf = TextBuffer.FromString("a\nb\nc");
        buf.ReplaceAllRecordingUndo(Rebuilt("a\r\nb\r\nc"));

        Assert.Equal("a\r\nb\r\nc", buf.Current.GetText(0, buf.Current.CharLength));
        Assert.True(buf.CanUndo);

        buf.Undo();
        Assert.Equal("a\nb\nc", buf.Current.GetText(0, buf.Current.CharLength));
    }

    [Fact]
    public void ReplaceAllRecordingUndo_Redo_ReappliesReplacement()
    {
        var buf = TextBuffer.FromString("a\nb");
        buf.ReplaceAllRecordingUndo(Rebuilt("a\r\nb"));
        buf.Undo();

        Assert.True(buf.CanRedo);
        buf.Redo();
        Assert.Equal("a\r\nb", buf.Current.GetText(0, buf.Current.CharLength));
    }

    // A-11 の本質的な回帰網: 差し替えの前に積んだ履歴が生き残ること。
    // 旧実装(ReplaceSource で新規 TextBuffer)ではここが 1 回目の Undo で頭打ちになっていた。
    [Fact]
    public void ReplaceAllRecordingUndo_PreservesEarlierHistory()
    {
        var buf = TextBuffer.FromString("a");
        buf.Insert(1, "\nX"); // 履歴 1
        buf.BreakUndoCoalescing();
        buf.Insert(3, "\nY"); // 履歴 2
        Assert.Equal("a\nX\nY", buf.Current.GetText(0, buf.Current.CharLength));

        buf.ReplaceAllRecordingUndo(Rebuilt("a\r\nX\r\nY")); // 履歴 3

        buf.Undo();
        Assert.Equal("a\nX\nY", buf.Current.GetText(0, buf.Current.CharLength));
        buf.Undo();
        Assert.Equal("a\nX", buf.Current.GetText(0, buf.Current.CharLength));
        buf.Undo();
        Assert.Equal("a", buf.Current.GetText(0, buf.Current.CharLength));
        Assert.False(buf.CanUndo);
    }

    // 保存点セマンティクス: _savedRoot を触らないので、差し替えで Modified が立ち、
    // Undo で保存点へ戻ると false へ復す(参照比較)。
    [Fact]
    public void ReplaceAllRecordingUndo_ModifiedTogglesWithSavePoint()
    {
        var buf = TextBuffer.FromString("a\nb");
        buf.MarkSaved();
        Assert.False(buf.Modified);

        buf.ReplaceAllRecordingUndo(Rebuilt("a\r\nb"));
        Assert.True(buf.Modified);

        buf.Undo();
        Assert.False(buf.Modified); // 保存点の root へ戻った
    }

    // coalescing 境界: 差し替えの直後に 1 文字入力しても差し替えエントリへ融合しない。
    // 融合すると Undo 1 回で「入力 + EOL 変換」がまとめて消える。
    // no-change 系ではないが、既定状態(履歴空)から始めると融合の有無を区別できないため
    // 履歴を 1 つ積んだ状態から始める(CLAUDE.md §4-B)。
    [Fact]
    public void ReplaceAllRecordingUndo_BreaksCoalescing()
    {
        var buf = TextBuffer.FromString("a\nb");
        buf.ReplaceAllRecordingUndo(Rebuilt("a\r\nb"));
        buf.Insert(4, "Z");

        buf.Undo(); // 直前の 1 文字入力だけが戻る
        Assert.Equal("a\r\nb", buf.Current.GetText(0, buf.Current.CharLength));
    }

    // 無変化(同一 root)では履歴を汚さない=Splice の `return` と同じ契約。
    // 非既定状態(履歴を 1 つ積んだ後)から検証する(CLAUDE.md §4-B)。
    [Fact]
    public void ReplaceAllRecordingUndo_SameRoot_DoesNotRecord()
    {
        var buf = TextBuffer.FromString("a\nb");
        buf.Insert(3, "Z");
        var same = buf; // 同一インスタンス=同一 root

        buf.ReplaceAllRecordingUndo(same);

        buf.Undo(); // 記録されていれば 1 回目の Undo が no-op 相当になり "a\nbZ" が残る
        Assert.Equal("a\nb", buf.Current.GetText(0, buf.Current.CharLength));
        Assert.False(buf.CanUndo);
    }
}
```

> `ReplaceAllRecordingUndo_SameRoot_DoesNotRecord` は同一インスタンスを渡す。
> 実装が「自分自身は禁止」で例外を投げる設計なら、このテストは
> 「別インスタンスだが同一内容から作った root」ではなく **同一 root 参照**を作る形へ書き換える。
> どちらにするかは Step 3 の実装判断と揃えること(下の実装は同一インスタンスを許して早期 return する)。

### Step 2: 赤を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
```

期待: **コンパイルエラー**(`ReplaceAllRecordingUndo` が未定義)。

### Step 3: `TextBuffer` に API を足す

`src/kxEdit.Core/Buffer/TextBuffer.cs` の `ClearUndo()` の直後へ追加する。

```csharp
    /// <summary>
    /// A-11(2026-08-28): <paramref name="rebuilt"/> が保持する木へ全文を差し替え、
    /// **1 Undo 単位として記録する**。保存時の EOL 一括変換(<c>EditorControl.ConvertEols</c>)で
    /// Undo/Redo 履歴が全消去されるのを防ぐための経路。
    /// </summary>
    /// <remarks>
    /// エントリは永続木の root 参照だけを持つ(<see cref="UndoHistory.Entry"/>)ので、
    /// 全文差し替えでもテキストの実体化や差分計算は要らない。
    /// <c>_savedRoot</c> は**触らない**: 保存点は root の参照比較で判定されるため、
    /// Undo で変換前=保存点の root へ戻れば <see cref="Modified"/> も自動的に false へ復す。
    /// 文書サイズ上限は <see cref="TextBufferBuilder"/> 側が構築時に判定済みなので二重判定しない。
    /// <paramref name="rebuilt"/> の chunk は <see cref="TextBuffer"/> 自身の追記バッファ由来では
    /// ないが、<c>Piece</c> は自分の chunk 参照を持ち、読み取り経路は chunk の出自を仮定しない。
    /// </remarks>
    /// <param name="rebuilt">差し替え後の内容を保持するバッファ(root だけを取り込む)。</param>
    public void ReplaceAllRecordingUndo(TextBuffer rebuilt)
    {
        ArgumentNullException.ThrowIfNull(rebuilt);
        var rootBefore = _current.Root;
        var newRoot = rebuilt._current.Root;
        if (ReferenceEquals(rootBefore, newRoot))
            return; // 無変化=履歴を汚さない(Splice の早期 return と同じ契約)
        int removed = _current.CharLength;
        int inserted = rebuilt._current.CharLength;
        _current = new TextSnapshot(newRoot);
        // insertHasBreak: true = coalescing を必ず切る。EOL 変換は「≤2 文字の連続タイピング」では
        // ないので、直前のタイプ操作へ融合させてはならない。
        _history.Record(rootBefore, newRoot, 0, removed, inserted, insertHasBreak: true);
    }
```

### Step 4: 緑を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build
```

期待: 新規 6 件 PASS。既存の Core テストが 1 件も落ちないこと。

### Step 5: commit

```bash
git add src/kxEdit.Core/Buffer/TextBuffer.cs tests/kxEdit.Core.Tests/Buffer/TextBufferReplaceAllTests.cs
git commit -m "$(cat <<'EOF'
feat(core): 全文差し替えを 1 Undo 単位で記録する API を追加する(A-11)

UndoHistory のエントリは永続木の root 参照だけを持つため、全文差し替えを
1 エントリとして記録するコストはほぼゼロ(テキストの実体化も差分計算も不要)。
_savedRoot は触らないので、Undo で保存点の root へ戻れば Modified も
参照比較で自動的に false へ復す。

次タスクで EditorControl.ConvertEols の ReplaceSource をこれへ置き換える。
EOF
)"
```

### Step 6: 別エージェントによるコード品質レビュー(前倒し)

CLAUDE.md §3「後続タスクが依存する新しい抽象・seam を導入する」に該当。
`superpowers:requesting-code-review` で**コード品質パス**を 1 回起動する。
レビュー観点として渡すもの:

- `_savedRoot` を触らない判断が保存点セマンティクスを壊していないか。
- `insertHasBreak: true` が `UndoHistory.Record` の coalescing 判定に対して正しいか
  (`removed > 0 && inserted > 0` では `pureInsert` / `pureDelete` のどちらでもないので
  実は結果に効かない。それでも渡す意図が読めるか)。
- 早期 return の条件が `ReferenceEquals(rootBefore, newRoot)` でよいか
  (内容が同じでも別 root なら記録される。それが望ましい契約か)。
- 別 `TextBuffer` の root を取り込むことによる寿命 / 所有権の問題。

指摘は 3 択(fixup で修正 / PR description に記載して受容 / 理由付き却下)で明示する。

### Step 7: ミューテーション検証(CLAUDE.md §4-A 該当)

対象は `ReplaceAllRecordingUndo` の `_history.Record` 引数と早期 return のみ。
手で変異させ、**各変異でどのテストが落ちるか**を記録する:

| 変異 | 期待される検出者 |
|------|----------------|
| `_history.Record(...)` の呼び出しを削除 | `Undo_RestoresPreviousText` |
| `pos: 0` → `1` | `Undo` 後のキャレット位置を見るテストが無ければ**生存** → 網を足すか、Task 3 の L2 で捕まえる |
| `removed` と `inserted` を入れ替える | 同上(`UndoResult.CaretPos` にしか効かない) |
| 早期 return を削除 | `SameRoot_DoesNotRecord` |
| `_savedRoot = newRoot` を足す | `ModifiedTogglesWithSavePoint` |

**生存した変異は放置しない**。`pos` / `removed` / `inserted` は `Undo()` / `Redo()` の
戻り値 `UndoResult.CaretPos` にしか効かないので、Core 側でキャレット位置を直接見るテストを足す:

```csharp
    // ミューテーション検証で pos / removedLen が生存したため追加。
    // Undo の推奨キャレット位置は Pos + RemovedLen(削除が復元された末尾)。
    [Fact]
    public void ReplaceAllRecordingUndo_UndoResultCaretPos_IsEndOfRestoredText()
    {
        var buf = TextBuffer.FromString("a\nb");   // CharLength 3
        buf.ReplaceAllRecordingUndo(Rebuilt("a\r\nb")); // CharLength 4

        var undo = buf.Undo();
        Assert.NotNull(undo);
        Assert.Equal(3, undo!.Value.CaretPos); // Pos(0) + RemovedLen(3)

        var redo = buf.Redo();
        Assert.NotNull(redo);
        Assert.Equal(4, redo!.Value.CaretPos); // Pos(0) + InsertedLen(4)
    }
```

追加後に再度変異させ、全変異が検出されることを確認して commit する。

```bash
git add tests/kxEdit.Core.Tests/Buffer/TextBufferReplaceAllTests.cs
git commit -m "test(core): ミューテーション検証で生存した pos/len を網に足す(A-11)"
```

---

## Task 3: A-11(Editor)— `ConvertEols` を in-place 化する

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.cs:447-565`(`ConvertEols`)
- Test: `tests/kxEdit.Editor.Tests/EditorControlConvertEolsTests.cs`

### Step 1: 現行契約を固定する対照群を確認する

`EditorControlConvertEolsTests.cs` の既存テスト(caret / anchor / チャンク境界 / 末尾単独 CR /
fast-path / mid-CRLF スナップ)は **1 件も消さない・書き換えない**。
これらが「in-place 化が挙動不変であること」の対照群になる。

まず現状で緑であることを確認する:

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build --filter "FullyQualifiedName~ConvertEols"
```

期待: 全件 PASS(変更前のベースライン)。

### Step 2: 失敗するテストを書く

同ファイルへ追加。

```csharp
    // A-11(監査 2026-08-22): 非 fast-path の ConvertEols が ReplaceSource で新規 TextBuffer に
    // 差し替わり、Undo/Redo 履歴を全消去していた。CRLF 文書に LF 混じりを貼って Ctrl+S すると
    // 直後の Ctrl+Z が無反応になる症状。
    [Fact]
    public void ConvertEols_NonFastPath_IsUndoable() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb\nc"));

            ctrl.ConvertEols(LineEnding.Crlf);
            Assert.Equal("a\r\nb\r\nc", ctrl.SnapshotText);
            Assert.True(ctrl.CanUndo);

            ctrl.Undo();
            Assert.Equal("a\nb\nc", ctrl.SnapshotText);
        });

    // A-11 の本質: 変換前に積んだ編集履歴が変換後も辿れること。
    [Fact]
    public void ConvertEols_NonFastPath_PreservesEarlierUndoHistory() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a"));
            ctrl.ReplaceCharRange(1, 0, "\nX");
            ctrl.CurrentBuffer.BreakUndoCoalescing();
            ctrl.ReplaceCharRange(3, 0, "\nY");
            Assert.Equal("a\nX\nY", ctrl.SnapshotText);

            ctrl.ConvertEols(LineEnding.Crlf);
            Assert.Equal("a\r\nX\r\nY", ctrl.SnapshotText);

            ctrl.Undo();
            Assert.Equal("a\nX\nY", ctrl.SnapshotText);
            ctrl.Undo();
            Assert.Equal("a\nX", ctrl.SnapshotText);
            ctrl.Undo();
            Assert.Equal("a", ctrl.SnapshotText);
        });

    // fast-path では履歴に何も積まれないこと。
    // no-change テストなので既定値(履歴空)ではなく、履歴を 1 つ積んだ非既定状態から始める
    // (CLAUDE.md §4-B)。積まれていれば 1 回目の Undo が変換の取り消しに消費され、
    // "a\r\nb" が残って落ちる。
    [Fact]
    public void ConvertEols_FastPath_RecordsNothingInHistory() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\r\nb"));
            ctrl.ReplaceCharRange(4, 0, "Z"); // 履歴 1
            Assert.Equal("a\r\nbZ", ctrl.SnapshotText);

            ctrl.ConvertEols(LineEnding.Crlf); // すでに CRLF 統一=fast-path

            ctrl.Undo();
            Assert.Equal("a\r\nb", ctrl.SnapshotText);
            Assert.False(ctrl.CanUndo);
        });

    // A-11: 変換後の Undo が本文だけでなくキャレットも変換前の論理位置へ戻すこと。
    [Fact]
    public void ConvertEols_Undo_RestoresCaretWithinDocument() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb\nc"));
            ctrl.ConvertEols(LineEnding.Crlf);
            ctrl.Undo();
            Assert.Equal("a\nb\nc", ctrl.SnapshotText);
            Assert.InRange(ctrl.CaretCharOffset, 0, ctrl.TextLength);
        });
```

> `ReplaceCharRange` / `CaretCharOffset` / `CurrentBuffer` の正確なシグネチャは
> 既存テスト(`TextEditingTests.cs` / `EditorControlConvertEolsTests.cs`)から確認して合わせる。
> 名前が違えば既存テストの呼び方に倣うこと。

### Step 3: 赤を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build --filter "FullyQualifiedName~ConvertEols"
```

期待: 新規 4 件のうち少なくとも `IsUndoable` / `PreservesEarlierUndoHistory` が **FAIL**
(`CanUndo` が false、または Undo が本文を戻さない)。
`FastPath_RecordsNothingInHistory` は現行実装でも緑になりうる(fast-path は元から履歴を触らない)。

### Step 4: `ConvertEols` の差し替え部を書き換える

`src/kxEdit.Editor/EditorControl.cs` の

```csharp
        ReplaceSource(builder.Build());
        int total = _buffer!.Current.CharLength;
```

を、以下へ置き換える。`ReplaceSource` が担っていた副作用のうち **EOL 変換で意味を失うものだけ**を
再現し、caret / 選択 / スクロールは直後の復元コードに任せる。

```csharp
        // A-11(2026-08-28): ReplaceSource(builder.Build()) をやめ、同一 TextBuffer への
        // 1 Undo 単位の全文差し替えにする。ReplaceSource はバッファ参照ごと置き換えるため
        // 新バッファの UndoHistory が空になり、変換前の履歴が到達不能になっていた
        // (CRLF 文書に LF 混じりを貼って Ctrl+S → 直後の Ctrl+Z が無反応)。
        // ReplaceSource が担っていた副作用のうち、EOL 変換で意味を失うものだけをここで再現する。
        // caret / anchor / topLine / topSegment / scrollX は下の復元コードが受け持つので潰さない
        // (旧経路が caret=0 の中間状態で UIA SelectionChanged を先に飛ばしていた副作用も消える)。
        if (IsComposing)
            CancelCompositionAndDefault(); // §4-6: 他の状態変異 API と同じく IME 未確定を先に確定キャンセル
        _buffer.ReplaceAllRecordingUndo(builder.Build());
        _cellHighlight = null; // EOL 変換で char オフセットが動く=セル強調は無効化
        MouseDragging = false; // ドラッグ選択の途中状態を破棄
        _wheelAccum = 0; // ホイール蓄積(1 tick = 120)をリセット
        _caretCtrl.DesiredXpx = -1;
        int total = _buffer.Current.CharLength;
```

続いて、既存の復元コード(`_caretCtrl.SetSelection(...)` → `SetTopPosition(...)` →
`ScrollX = savedScrollX;` → `if (_hasFocus) PositionCaret();`)は**そのまま残す**。
その直後、`EolMode = eol;` の**前**へ、`ReplaceSource` が持っていた通知群を明示的に置く:

```csharp
        // ReplaceSource が内部で打っていた通知を、caret / スクロール復元の**後**に明示的に打つ。
        // 順序は AfterEdit と同じ「スクロールバー再計算 → 再描画 → UIA → UpdateUI」。
        // AfterEdit をそのまま呼ばないのは BringCaretIntoView が入っており、
        // 復元した topLine / scrollX を追従スクロールで上書きしてしまうため。
        UpdateVerticalScrollbar();
        UpdateHorizontalScrollbar();
        Invalidate();
        _uia.OnSnapshotChanged(_buffer.Current);
        _uia.RaiseTextChanged();
        if (RaiseUiaSelectionEvents)
            _uia.RaiseSelectionChanged();
        // 保存点遷移: in-place 化で ConvertEols 後の Modified は true になる(旧経路は fresh バッファ
        // ＝ false だった)。AfterEdit と同じ state-first-then-fire で両方向を発火する。
        bool nowModified = Modified;
        bool shouldFireLeft = !_wasModified && nowModified;
        bool shouldFireReached = _wasModified && !nowModified;
        _wasModified = nowModified;
        if (shouldFireLeft)
            SavePointLeft?.Invoke(this, EventArgs.Empty);
        if (shouldFireReached)
            SavePointReached?.Invoke(this, EventArgs.Empty);
        UpdateUI?.Invoke(this, EventArgs.Empty);
```

**`ConvertEols` の XML doc(`:432-446`)を更新する。** 「`ReplaceSource` する」という記述と
「no-op fast-path では ReplaceSource によるキャレット/選択/スクロールリセット・UIA TextChanged
発火を回避する」という記述が古くなる。新しい契約(1 Undo 単位の in-place 差し替え・
fast-path は EolMode 更新のみ)へ書き換える。

### Step 5: 緑を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
```

期待: 新規 4 件 PASS。**既存の `ConvertEols_*` が 1 件も落ちないこと**(挙動不変の証明)。
落ちたものがあれば、それが「意図した挙動変更」なのか「壊した」のかを判別してから進む。

### Step 6: App 層への波及を確認する

`ConvertEols` の唯一の production 呼出元は `FileController.cs:843`。
この時点では Task 4 が未着手なのでロールバックは壊れているはずである。

```bash
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

期待: `Save_ExistingPathIsDriveRoot_ReportsError_AndRollsBackModified` が **FAIL**
(`ReferenceEquals(doc.Editor.CurrentBuffer, snapshotBefore)` が常に true になり
ロールバックが no-op になるため、`Assert.True(doc.Editor.Modified)` が落ちる、
あるいは本文が変換後のまま残る)。

**これは想定どおりの赤である**。Task 4 で直す。落ち方を記録してから次へ進む。
逆に**ここが緑のままなら**、ロールバックのテストが実は何も守っていない可能性があるので、
Task 4 の Step 1 でテストを強化する根拠として記録する。

### Step 7: commit

```bash
git add src/kxEdit.Editor/EditorControl.cs tests/kxEdit.Editor.Tests/EditorControlConvertEolsTests.cs
git commit -m "$(cat <<'EOF'
fix(editor): ConvertEols を 1 Undo 単位の in-place 変換にする(A-11)

保存時の EOL 一括変換が ReplaceSource で新規 TextBuffer に差し替わり、
Undo/Redo 履歴を全消去していた。同一 TextBuffer への記録付き全文差し替えに
切り替え、ReplaceSource が担っていた副作用(セル強調の無効化・IME 確定キャンセル・
スクロールバー再計算・UIA 通知・保存点遷移)を明示的に打つ。

挙動変更 1 件: 旧経路は caret=0 の中間状態で UIA SelectionChanged を先に
飛ばしていた(監査 A-11 が副作用として指摘)。in-place 化でこの中間状態が
消え、caret 復元後に 1 回だけ発火する。SR の実発声への影響は L5 で確認する。

App 層のロールバックはこの時点で壊れる(次タスクで組み替える)。
EOF
)"
```

### Task 3 の仕様レビュー観点

- 設計書 §5.2 の契約表 10 行が 1 行ずつ満たされているか(特に `_cellHighlight` と IME)。
- `BringCaretIntoView` を呼んでいないこと(復元した topLine / scrollX を壊さないため)。
- fast-path の経路が一切変わっていないこと。
- XML doc の記述が新しい実装と一致しているか。

---

## Task 4: A-11(App)— 保存失敗ロールバックを組み替える

**Files:**
- Modify: `src/kxEdit.Editor/EditorControl.cs`(ロールバック用 API を追加)
- Modify: `src/kxEdit.App/FileController.cs:812-892`(`WriteToPath` と XML doc)
- Test: `tests/kxEdit.App.Tests/FileControllerTests.cs`

> **仕様レビュー対象**: 設計書 §5.3 の no-op 要件(fast-path で余分に Undo すると
> 直前の編集が消える)を満たしているかを重点的に見る。

### Step 1: 既存のロールバック網を強化してから直す

`Save_ExistingPathIsDriveRoot_ReportsError_AndRollsBackModified`(`FileControllerTests.cs:591-616`)は
`Modified` しか見ていない。**本文が戻ることも見るよう強化する**。
`doc.Editor.Text = "a\r\nb\r\nc"` + `State.LineEnding = Lf` なので、
ロールバックが効かなければ本文は `"xa\nb\nc"` になる。

既存テストの末尾へ 1 行足す(既存の assertion は消さない):

```csharp
            Assert.True(doc.Editor.Modified); // ロールバック発火=未保存の本文が失われない
            // A-11: in-place 化で「旧バッファ参照へ戻す」機構が使えなくなったため、
            // 本文そのものが変換前へ戻ることも固定する(Modified だけでは EOL 書換を検出できない)。
            Assert.Equal("xa\r\nb\r\nc", doc.Editor.SnapshotText);
```

> 期待値 `"xa\r\nb\r\nc"` は「`Text` セッターで `"a\r\nb\r\nc"` を入れ、offset 0 に `"x"` を挿入した」
> 結果である。実際の値は実行して確認し、**ロールバック前の値**を書くこと
> (`"xa\nb\nc"` = 変換後の値を書いてしまうと網が反転する)。

### Step 2: fast-path の no-op 要件を固定するテストを書く(設計書 §5.3)

**これが本タスクで最も重要な網である。** fast-path では `ConvertEols` が何も記録しないので、
ロールバックが無条件に Undo すると**ユーザーの直前の編集が消える**。

`FileControllerTests.cs` へ追加。上の既存テストと同じ Host / TempDir パターンを使う。

```csharp
    /// <summary>
    /// A-11 設計書 §5.3: 保存失敗のロールバックを Undo ベースへ組み替えたことによる新しい罠。
    /// fast-path(すでに目的 EOL で統一済み)では ConvertEols が履歴に何も積まない。
    /// ロールバックが無条件に Undo すると、**ユーザーの直前の編集が消える**。
    /// fixture は「変換不要な EOL」かつ「直前に Undo 可能な編集が 1 つ積まれている」状態から始める。
    /// </summary>
    [Fact]
    public void Save_WriteFailure_OnFastPathEol_DoesNotUndoUserEdit() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string root = System.IO.Path.GetPathRoot(tmp.Root)!;

            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "a\r\nb\r\nc";
            doc.Editor.ReplaceCharRange(0, 0, "x"); // ユーザーの直前の編集
            doc.State.LineEnding = LineEnding.Crlf; // 既に CRLF 統一=ConvertEols は fast-path
            doc.State.Path = root; // 書き込み先が確定しない=WriteToPath が失敗する
            string before = doc.Editor.SnapshotText;

            Assert.False(host.File.Save());

            // ロールバックが余分に Undo していれば "x" が消えて "a\r\nb\r\nc" になる
            Assert.Equal(before, doc.Editor.SnapshotText);
            Assert.True(doc.Editor.Modified);
        });

    /// <summary>
    /// A-11: 非 fast-path の保存失敗で、EOL 変換だけが取り消され、
    /// その前のユーザー編集は残ること(1 つだけ戻す=戻しすぎない)。
    /// </summary>
    [Fact]
    public void Save_WriteFailure_OnNonFastPathEol_UndoesOnlyTheConversion() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string root = System.IO.Path.GetPathRoot(tmp.Root)!;

            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "a\r\nb\r\nc";
            doc.Editor.ReplaceCharRange(0, 0, "x"); // ユーザーの直前の編集
            doc.State.LineEnding = LineEnding.Lf; // CRLF → LF = 非 fast-path
            doc.State.Path = root;
            string before = doc.Editor.SnapshotText;

            Assert.False(host.File.Save());

            Assert.Equal(before, doc.Editor.SnapshotText); // 変換前へ戻る
            Assert.True(doc.Editor.Modified);
            Assert.True(doc.Editor.CanUndo); // "x" の編集はまだ Undo できる
        });
```

### Step 3: 赤を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~WriteFailure_On|FullyQualifiedName~DriveRoot"
```

期待: `NonFastPathEol_UndoesOnlyTheConversion` と `DriveRoot` が **FAIL**(ロールバックが no-op)。
`FastPathEol_DoesNotUndoUserEdit` は現時点では緑(まだ Undo していないため)だが、
**Step 4 の実装を誤ると赤になる**性質の網である。

### Step 4: `EditorControl` にロールバック用 API を足す

**`EditorControl.Undo()` を流用してはならない。** `Undo()` は `ReadOnly` で早期 return する
(`EditorControl.cs:1293`)。`WriteToPath` は `ConvertEols` の前後でだけ `ReadOnly` を外し、
catch 節に到達する時点では**復元済み**なので、CSV グリッドモード(`ReadOnly = true`)では
ロールバックが黙って no-op になる。

`EditorControl.cs` の `Undo()` の近くへ追加する:

```csharp
    /// <summary>
    /// A-11(2026-08-28): 直前の <see cref="ConvertEols"/> による EOL 変換を取り消す
    /// (保存失敗時のロールバック専用)。取り消せたら true。
    /// </summary>
    /// <remarks>
    /// <see cref="Undo"/> を流用できない理由が 2 つある:
    /// (1) <see cref="Undo"/> は <see cref="ReadOnly"/> で早期 return する。<c>WriteToPath</c> は
    ///     ConvertEols の前後でだけ ReadOnly を外すため、catch 節では復元済み= CSV グリッドモード
    ///     (ReadOnly=true)でロールバックが黙って no-op になる。
    /// (2) 変換していない(fast-path)ときに呼ばれても**絶対に Undo してはならない**。
    ///     余分に 1 つ戻すとユーザーの直前の編集が消える。呼出元が fast-path を判別できないため、
    ///     判別は本メソッドの契約に含める(<paramref name="conversionRecorded"/>)。
    /// Redo スタックは汚さない: 保存に失敗しただけの取り消しを Ctrl+Y で「やり直せる」のは
    /// ユーザーの意図と噛み合わないため。
    /// </remarks>
    /// <param name="conversionRecorded">
    /// <see cref="ConvertEols"/> が実際に履歴へ記録したか(fast-path なら false)。
    /// </param>
    public bool UndoEolConversion(bool conversionRecorded)
    {
        if (!conversionRecorded || _buffer is null)
            return false;
        var r = _buffer.Undo();
        if (r is null)
            return false;
        _buffer.DropRedo();
        int pos = Math.Clamp(r.Value.CaretPos, 0, _buffer.Current.CharLength);
        _caretCtrl.SetTo(pos, _buffer.Current);
        _caretCtrl.DesiredXpx = -1;
        AfterEdit();
        return true;
    }
```

`ConvertEols` を「記録したかどうか」を返す形へ変える:

- シグネチャを `public bool ConvertEols(LineEnding eol)` にする。
- `_buffer is null` の早期 return → `return false;`
- fast-path(`IsEolAlreadyUniform`)の return → `EolMode = eol; return false;`
- 末尾 → `EolMode = eol; return true;`

戻り値を無視する既存呼出元があってもコンパイルは通る(C# は戻り値の破棄を許す)ので、
既存テストは変更不要。

`TextBuffer` に Redo 破棄を足す(`ClearUndo` の近く):

```csharp
    /// <summary>A-11: 直前の Undo で積まれた Redo を捨てる(保存失敗ロールバック用)。</summary>
    public void DropRedo() => _history.ClearRedo();
```

`UndoHistory` へ:

```csharp
    public void ClearRedo() => _redo.Clear();
```

> **代案**: `DropRedo` / `ClearRedo` を足さず、Redo に残す判断もありうる。
> その場合は上の 2 メソッドを省き、`UndoEolConversion` から `_buffer.DropRedo();` を外して、
> 「保存失敗のロールバックは Ctrl+Y でやり直せる」ことを PR description に明記する。
> **どちらを採るかは実装時にユーザーへ確認し、設計書 §5.3 へ結論を追記する。**

### Step 5: `WriteToPath` を組み替える

`src/kxEdit.App/FileController.cs:821-892`。

置換前(要点):

```csharp
        var snapshotBefore = doc.Editor.CurrentBuffer;
        try
        {
            ApplyEol(doc);
            ...
                doc.Editor.ConvertEols(doc.Editor.EolMode);
            ...
        }
        catch (...)
        {
            if (!ReferenceEquals(doc.Editor.CurrentBuffer, snapshotBefore))
            {
                doc.Editor.SetOrReplaceSource(snapshotBefore);
            }
            ...
        }
```

置換後(要点):

```csharp
        // A-11(2026-08-28): ConvertEols は in-place の 1 Undo 単位になったため、
        // 旧機構(ConvertEols 前のバッファ参照を握って差し戻す)は成立しない
        // = ReferenceEquals が常に true になり、ロールバックが黙って no-op になる。
        // 代わりに「変換を記録したか」を受け取り、記録したときだけ 1 つ取り消す。
        bool eolConverted = false;
        try
        {
            ApplyEol(doc);
            bool wasReadOnly = doc.Editor.ReadOnly;
            if (wasReadOnly)
                doc.Editor.ReadOnly = false;
            try
            {
                eolConverted = doc.Editor.ConvertEols(doc.Editor.EolMode);
            }
            finally
            {
                if (wasReadOnly)
                    doc.Editor.ReadOnly = true;
            }
            ...
        }
        catch (...)
        {
            // 変換を記録したときだけ 1 つ取り消す。fast-path(記録なし)で取り消すと
            // ユーザーの直前の編集が消える(設計書 §5.3)。
            doc.Editor.UndoEolConversion(eolConverted);
            ...
        }
```

**XML doc(`:812-819`)を書き換える。** 旧機構の説明(「旧 TextBuffer 参照を保存前に握っておき…
`SetOrReplaceSource` で参照だけを戻せば…」)は新実装と食い違うので残さない。
新しい説明に、`Undo()` を流用しない理由(ReadOnly ガード)と fast-path の no-op 要件を書く。

### Step 6: 緑を確認する

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
dotnet test tests/kxEdit.Core.Tests -c Release --no-build
```

期待: 3 層とも全件 PASS。

### Step 7: commit

```bash
git add src/kxEdit.Editor/EditorControl.cs src/kxEdit.Core/Buffer/TextBuffer.cs \
        src/kxEdit.Core/Buffer/UndoHistory.cs src/kxEdit.App/FileController.cs \
        tests/kxEdit.App.Tests/FileControllerTests.cs
git commit -m "$(cat <<'EOF'
fix(app): 保存失敗ロールバックを Undo ベースへ組み替える(A-11)

ConvertEols の in-place 化で「変換前のバッファ参照へ戻す」旧機構が
成立しなくなった(ReferenceEquals が常に true=ロールバックが黙って
no-op になる)。ConvertEols が「記録したか」を返すようにし、
記録したときだけ 1 つ取り消す。

EditorControl.Undo を流用しない理由は 2 つ:
- Undo は ReadOnly で早期 return する。WriteToPath の catch 節では
  ReadOnly が復元済みなので CSV グリッドモードで no-op になる
- fast-path では ConvertEols が何も記録しない。無条件に Undo すると
  ユーザーの直前の編集が消える
EOF
)"
```

### Task 4 の仕様レビュー観点

- fast-path で `UndoEolConversion` が確実に no-op か(`conversionRecorded` の伝播)。
- `ReadOnly = true`(CSV グリッドモード)でロールバックが効くか。
- `ConvertEols` の戻り値を取りこぼしている経路が無いか。
- XML doc に旧機構の説明が残っていないか。

---

## Task 5: ブランチ全体の最終レビューと品質ゲート

### Step 1: 最終ブランチレビュー(2 パス・別エージェント)

CLAUDE.md §3-5 に従い、**コード品質パス**と**脆弱性パス**を**独立した別エージェント**で起動する
(1 起動に混載しない)。`superpowers:requesting-code-review` を使う。

コード品質パスへ渡す焦点:

- 設計書 §5.2 の契約表 10 行が実装で満たされているか。
- `ConvertEols` の in-place 化で「旧 `ReplaceSource` がやっていたが漏らしたもの」が無いか。
- A-9 の byte 走査に `IsEolAlreadyUniform` との重複が生まれていないか(共通化すべきか)。
- ミューテーション検証のスポットチェック(Task 2 の結果を渡す)。

脆弱性パスへ渡す焦点:

- A-9 の全文走査が病的入力(巨大 1 行・大量の孤立 CR)で異常なコストにならないか。
- `ReplaceAllRecordingUndo` が `MaxTotalBytes` を迂回していないか
  (`TextBufferBuilder` 側のガードだけで足りるか)。
- `DocumentTooLargeException` が `WriteToPath` の catch フィルタに無いこと
  (**既存の問題**。本ブランチで悪化していないかだけ見て、悪化していなければ申し送り)。

指摘は 3 択(fixup で修正 / PR description に記載して受容 / 理由付き却下)で明示し、
**fixup commit で積む**(元 commit を書き換えない)。

### Step 2: 設計書へ実施記録を追記する

`docs/plans/2026-08-28-eol-detection-and-undo-design.md` の §10 申し送りへ:

- Task 2 Step 0 の不変条件検証の結果。
- ミューテーション検証で生存した変異と、足した網。
- Redo 破棄の判断(Step 4 の代案のどちらを採ったか)。
- レビュー指摘のうち「受容」「却下」にしたものと理由。

CLAUDE.md §8 に従い、**§1〜§9 は書き換えず追記のみ**にする。

```bash
git add docs/plans/2026-08-28-eol-detection-and-undo-design.md
git commit -m "docs(plans): A-9 / A-11 設計書に実施記録を追記する"
```

### Step 3: 品質ゲート

```bash
pwsh tools/pre-merge-check.ps1
```

**EXIT 0 を確認する**。0 warning が維持されていること。

### Step 4: L5 チェックリストを作る

`docs/plans/2026-08-28-eol-detection-and-undo-l5-checklist.md` を作成する。
設計書 §8 の 3 項目を出発点に、既存の l5-checklist(例:
`2026-08-23-saveas-target-validation-l5-checklist.md`)の書式へ合わせる。

**L5 は必須**(UIA の発火経路が変わる)。ユーザーへ実機 NVDA 検証を依頼する。

### Step 5: PR

CLAUDE.md §7 に従う。PR description(日本語)に必ず書くこと:

- 目的(A-9 / A-11 と監査への参照)。
- **意図的な挙動変更 1 件**: 旧経路が caret=0 の中間状態で UIA `SelectionChanged` を
  先に飛ばしていたのが、caret 復元後の 1 回になる(監査 A-11 が副作用として指摘した挙動の解消)。
- **意図的な挙動変更 2 件目**: `ConvertEols` 後の `Modified` が false → true になる
  (成功パスでは直後の `SetSavePoint` で false に戻るため最終状態は不変)。
- Redo 破棄の判断。
- レビュー経緯(前倒しコード品質レビュー + 最終 2 パス)と受容した指摘。
- 申し送り(設計書 §10 の「書き出し側 EOL 変換案」・A-9 の残余)。

---

## 完了の定義

- [ ] Task 1〜4 が各々 commit され、L1 / L2 / L3 が全件緑
- [ ] `dotnet build kxEdit.sln -c Release -warnaserror` が 0 warning
- [ ] `tools/pre-merge-check.ps1` が EXIT 0
- [ ] 別エージェントによる最終レビュー 2 パス実施済み・指摘が 3 択で処理済み
- [ ] 設計書 §10 に実施記録を追記済み
- [ ] L5 チェックリスト作成済み・ユーザーへ実機検証を依頼済み
- [ ] PR 作成済み(挙動変更 2 件を description に明記)
