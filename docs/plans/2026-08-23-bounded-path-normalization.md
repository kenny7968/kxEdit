# パス正規化を境界付きにする(Issue #48 / S-15)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** UI スレッドから無境界の `Path.GetFullPath` を無くし、不達ネットワーク共有上の `~` を含むパスで UI が約 21 秒固まる退行(S-15)と、同一機構の既存バグ(`RecentFilesList`)を解消する。

**Architecture:** 「境界を張る前に回数を減らす」。`PathKey` を生入力用 `For` と正規化済み用 `ForNormalized`(ファイルシステムに触れない)に割り、`DocumentState.Path` を「null か正規化済み絶対パス」の不変条件にすることで、`FindByPath` / `RecentFilesList.Add` の 1+N 回の `GetFullPath` を 0 回にする。残る「操作あたり 1 本」を `IReachabilityProbe` の 3 つ目のメンバーとして境界付きにする。

**Tech Stack:** .NET 9(`net9.0-windows`・SDK は 10.x)/ C# / WinForms / xUnit / CSharpier(pre-commit)/ Husky.Net

**設計書:** `docs/plans/2026-08-23-bounded-path-normalization-design.md`

---

## 実行者向けの前提知識

このリポジトリを初めて触る場合、着手前に以下を把握しておくこと。

- **プロセス規範は `CLAUDE.md`**。本計画はそれに従う。会話・コミットメッセージ・PR は日本語、コードと識別子は英語。
- **0 warning 必須**(`-warnaserror` 稼働中)。ビルドが警告を出したらそれは失敗。
- **pre-commit フックを `--no-verify` で飛ばさない**。CSharpier の整形とローカルパス検出が走る。テストコードに `%USERPROFILE%` 配下や `<drive>:\src\kxEdit` のような自分の環境の絶対パスを書くとフックが落ちる(検出規則は `tools/check-no-local-paths.ps1`)。テストで絶対パスが要るときは `TempDir`(`tests/kxEdit.App.Tests/TempDir.cs`)か、`@"C:\Temp\a.txt"` のような**存在しない一般的なダミー**を使う(既存テストがそうしている)。
- **WinForms のテストは STA スレッドが要る**。App 層のテストは `Sta.Run(() => { ... })` で包む(既存テストの書式に倣う)。
- **テスト数を文書に書かない**(CLAUDE.md §5)。「全緑」で表現する。

### ビルドとテストのコマンド

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests   -c Release --no-build
dotnet test tests/kxEdit.Editor.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests    -c Release --no-build
```

`--no-build` はビルド済みバイナリを使う。**ソースを変えたら必ず先に build する**(ミューテーション検証でこれを忘れると、変異していないバイナリを見て「変異が kill された」と誤読する)。

### この問題の機構(なぜ 21 秒か)

`Path.GetFullPath` は、正規化後のパスに `~` が含まれると Win32 の `GetLongPathName` を呼ぶ。これは実ファイルシステム / ネットワーク呼び出しで、タイムアウトの境界が無い。実測(2026-08-23・**.NET 10.0.9 のスクラッチパッド console app**。本体の `TargetFramework` は `net9.0-windows` だが、`~` 展開の機構は同一であることを Task 2 のレビュアーが 9.0.8 で確認済み):

```
\\198.51.100.7\share\PROGRA~1\a.txt   21002 ms   <- ディレクトリー成分の ~
\\203.0.113.9\share\notes~.txt        21004 ms   <- ファイル名の ~
\\198.51.100.8\share\plain.txt            0 ms
```

`~` は珍しくない。8.3 短縮名(`PROGRA~1`)、Office のロックファイル(`~$`)、Emacs / gedit 系のバックアップ(`file.txt~`)。

**再測定するときの注意**: Windows は不達ホストを否定キャッシュする。同一ホストで複数ケースを続けて測ると 2 件目以降が 0 ms に見え、誤った結論が出る。**ケースごとに別ホストを使う**(上の実測はそうしてある)。

---

## タスクの並び順が load-bearing である理由

**この順序を入れ替えてはいけない。** 途中で「不変条件がまだ成立していないのに比較側だけ先に変える」窓を作ると、その commit ではアプリが壊れる(相対パスや区切り違いのパスが同一ファイルと判定されなくなり、A-7 (b) の重複タブ検知が素通りする)。

正しい順序は「**入口で正規化するようにしてから、比較側を軽くする**」:

1. 追加のみ(挙動不変): `PathKey.ForNormalized` → seam
2. 入口を正規化に変える: `TryNormalizeSavePath` → `TryOpenOrActivate`
3. **その後で**比較側を軽くする: `FindByPath` → `RecentFilesList`
4. 最後に不変条件のアサートを置く(先に置くと 2 の途中で発火する)

---

## Task 1: `PathKey.ForNormalized` を新設する

**レビュー区分:** 通常(仕様レビューのみ)

**Files:**
- Modify: `src/kxEdit.Core/Text/PathKey.cs`
- Test: `tests/kxEdit.Core.Tests/Text/PathKeyTests.cs`

このタスクは**追加のみ**で、既存の `PathKey.For` の挙動を変えない。

**Step 1: 失敗するテストを書く**

`tests/kxEdit.Core.Tests/Text/PathKeyTests.cs` の末尾(クラス内)に追記する:

```csharp
    // ===== ForNormalized(Issue #48 / 設計書 §3.2)=====
    // 正規化済み絶対パス専用の契約。ToLowerInvariant のみで、ファイルシステムに触れない。
    // For との弁別が本体: For は GetFullPath を通すので不達ネットワーク共有で
    // UI を約 21 秒止めうる(S-15)。ForNormalized はそれを構造的に持たない。

    [Fact]
    public void ForNormalized_lowercases_only() =>
        Assert.Equal(@"c:\temp\memo.txt", PathKey.ForNormalized(@"C:\Temp\Memo.TXT"));

    [Fact]
    public void ForNormalized_same_path_different_case_yields_same_key() =>
        Assert.Equal(
            PathKey.ForNormalized(@"C:\Temp\Memo.txt"),
            PathKey.ForNormalized(@"c:\temp\memo.TXT")
        );

    [Fact]
    public void ForNormalized_does_not_normalize_separators()
    {
        // For との**弁別**。ForNormalized は正規化しないので区切り差は別キーになる。
        // 呼出側が正規化済みパスを渡す契約(Issue #48 / 設計書 §3.1)を、ここで明文化して固定する。
        // このテストが無いと「ForNormalized の中で GetFullPath も呼ぶ」書き損じが
        // 全緑で通り、S-15 が丸ごと戻る。
        Assert.NotEqual(PathKey.ForNormalized(@"C:\Temp\a.txt"), PathKey.ForNormalized("C:/Temp/a.txt"));
        Assert.Equal(PathKey.For(@"C:\Temp\a.txt"), PathKey.For("C:/Temp/a.txt")); // 対照群: For は吸収する
    }

    [Fact]
    public void ForNormalized_does_not_collapse_relative_segments() =>
        // 同上の弁別(2 軸目)。`x\..` を畳まないことが GetFullPath 非経由の証拠になる。
        Assert.NotEqual(
            PathKey.ForNormalized(@"C:\Temp\b.txt"),
            PathKey.ForNormalized(@"C:\Temp\x\..\b.txt")
        );

    [Fact]
    public void ForNormalized_empty_returns_empty() =>
        Assert.Equal(string.Empty, PathKey.ForNormalized(""));

    [Fact]
    public void ForNormalized_does_not_touch_filesystem_for_invalid_input() =>
        // For は NUL 混入を空文字へ落とす(CSV-L-8)。ForNormalized は正規化しないので
        // 落とす対象が無く、そのまま小文字化して返す = 契約が違うことを固定する。
        Assert.Equal("a\0b", PathKey.ForNormalized("a\0b"));
```

**Step 2: テストが落ちることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
```

期待: `PathKey.ForNormalized` が存在せず **CS0117 でコンパイルエラー**。

**Step 3: 最小の実装を書く**

`src/kxEdit.Core/Text/PathKey.cs` を全面的に次へ置き換える:

```csharp
namespace kxEdit.Core.Text;

/// <summary>
/// 同一ファイル判定用の正規化キー。Windows 前提で大文字小文字を無視する。
/// 入力の契約で 2 つに分かれる:
/// <see cref="For"/> は生入力用で <c>GetFullPath</c> を通し、
/// <see cref="ForNormalized"/> は正規化済み絶対パス用で**ファイルシステムに触れない**。
/// 小文字化の規則そのものは <see cref="ForNormalized"/> が single source。
/// </summary>
/// <remarks>
/// Issue #48 (S-15): <c>Path.GetFullPath</c> は正規化後のパスに <c>~</c> が含まれると
/// <c>GetLongPathName</c> を呼ぶ。これは境界の無い実ファイルシステム / ネットワーク呼び出しで、
/// 不達の共有に対して約 21 秒 UI スレッドを止める(実測 2026-08-23)。
/// このため<b>タブ数や履歴件数に比例して <see cref="For"/> を呼ぶ経路を作ってはいけない</b>。
/// そういう経路(<c>DocumentManager.FindByPath</c> / <c>RecentFilesList.Add</c>)は
/// <see cref="ForNormalized"/> を使い、正規化は操作あたり 1 回・境界付きで済ませる。
/// </remarks>
public static class PathKey
{
    /// <summary>
    /// 生入力用。<c>GetFullPath</c> で相対パス・区切り文字差を吸収してからキー化する。
    /// 正規化できない場合は空文字を返し、「invalid はまとめて 1 件」に集約する（CSV-L-8）。
    /// <b>実 I/O を伴いうる</b>(remarks 参照)。UI スレッドから 1 操作につき 1 回を超えて
    /// 呼ばないこと。
    /// </summary>
    public static string For(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            // CSV-L-8 (v0.11): GetFullPath 例外時は攻撃者制御の生 path を返すのを避け、
            // 空文字（= dedup 用の invariant「invalid はまとめて 1 件」）に落とす。
            return string.Empty;
        }
        return ForNormalized(full);
    }

    /// <summary>
    /// 正規化済み絶対パス用。小文字化するだけで、<b>ファイルシステムには一切触れない</b>。
    /// 呼出側が正規化済みパスを渡す契約(設計書 §3.1 の不変条件)。
    /// 区切り差(<c>/</c> と <c>\</c>)や <c>..</c> は吸収しない — 吸収させたくなったら
    /// それは呼出側が正規化を怠っているということなので、ここではなく呼出側を直す。
    /// </summary>
    public static string ForNormalized(string fullPath) =>
        string.IsNullOrEmpty(fullPath) ? string.Empty : fullPath.ToLowerInvariant();
}
```

**Step 4: テストが通ることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build
```

期待: 全緑・0 warning。既存の `PathKeyTests` 5 本(`For` 側)も緑のまま = `For` の挙動不変。

**Step 5: commit**

```bash
git add src/kxEdit.Core/Text/PathKey.cs tests/kxEdit.Core.Tests/Text/PathKeyTests.cs
git commit -m "feat(core): PathKey に ForNormalized を新設する(Issue #48 / 設計書 §3.2)

正規化済み絶対パス専用の契約を切り出す。小文字化のみでファイルシステムに
触れないため、タブ数や履歴件数に比例して呼んでも S-15 のブロックを起こさない。
For は ForNormalized への委譲に書き換え、小文字化の規則を 1 箇所に保つ。
For 側の挙動は不変(既存テストは一行も変えていない)。"
```

---

## Task 2: 境界付き正規化 seam を追加する

**レビュー区分:** **前倒しコード品質レビュー**(CLAUDE.md §3・新しい抽象/seam の導入)

**Files:**
- Modify: `src/kxEdit.App/Abstractions/IReachabilityProbe.cs`
- Modify: `src/kxEdit.App/FileReachabilityProbe.cs`
- Modify: `tests/kxEdit.App.Tests/Fakes/FakeReachabilityProbe.cs`
- Test: `tests/kxEdit.App.Tests/FileReachabilityProbeTests.cs`

このタスクも**追加のみ**。誰もまだ呼ばないので挙動不変。

### 設計上の要点(実装前に読むこと)

1. **フェイルセーフ値はヘルパー側に置く。** `WaitBounded(task, timeout, <定数>)` と直書きすると、その定数は 1 トークンの引数でしかなく、書き換えてもコンパイルが通り・ハングもせず・全緑で変異が生存する。既存の `RunSaveTargetProbe` / `RunFileExistsProbe` が同じ理由でこの形になっている(それぞれのコメントに実測付きで記録がある)。**同じ形に揃える。**
2. **3 状態が要る。** 「正規化できた」「入力が不正」「タイムアウト」を区別する。不正とタイムアウトを同じ文言で扱うと、到達不能が原因なのに利用者が自分の入力を疑い続ける。
3. **enum のゼロ値をフェイルセーフ側に置く。** `default(PathNormalizeResult)` が `TimedOut` になるようにし、初期化漏れが「成功」に転ばないようにする。

**Step 1: 失敗するテストを書く**

`tests/kxEdit.App.Tests/FileReachabilityProbeTests.cs` の末尾(クラス内)に追記する:

```csharp
    // ===== 境界付き正規化(Issue #48)=====

    [Fact]
    public void RunNormalizeProbe_WorkExceedsTimeout_FailsSafeToTimedOut()
    {
        // S-15 の本体。フェイルセーフ値が Ok へ変異すると、タイムアウトしたのに
        // 「正規化できた」と読んで空文字パスを保存先に採用してしまう。
        // 組み方は既存 2 本と対称: work は Ok を返すので、TimedOut が返ったなら
        // フェイルセーフ由来と確定する。
        var gate = new TaskCompletionSource();
        try
        {
            var result = FileReachabilityProbe.RunNormalizeProbe(
                () =>
                {
                    gate.Task.Wait();
                    return new PathNormalizeResult(PathNormalizeStatus.Ok, @"C:\Temp\a.txt");
                },
                TimeSpan.FromMilliseconds(50)
            );

            Assert.Equal(PathNormalizeStatus.TimedOut, result.Status);
            Assert.Equal(string.Empty, result.Full); // タイムアウトを「このパスで良い」と読ませない
        }
        finally
        {
            gate.SetResult(); // 退避スレッドを解放する(テスト後に leak させない)
        }
    }

    [Fact]
    public void RunNormalizeProbe_WorkCompletes_ReturnsWorkResult()
    {
        // 対照群。常にフェイルセーフ値を返す実装を kill する。
        var result = FileReachabilityProbe.RunNormalizeProbe(
            () => new PathNormalizeResult(PathNormalizeStatus.Ok, @"C:\Temp\a.txt"),
            Timeout
        );

        Assert.Equal(PathNormalizeStatus.Ok, result.Status);
        Assert.Equal(@"C:\Temp\a.txt", result.Full);
    }

    [Fact]
    public void PathNormalizeResult_default_is_TimedOut() =>
        // ゼロ値をフェイルセーフ側に置く設計(§Task 2 要点 3)の pin。
        // enum の並びを入れ替える変異(Ok = 0 にする)をここで kill する。
        Assert.Equal(PathNormalizeStatus.TimedOut, default(PathNormalizeResult).Status);

    [Fact]
    public void NormalizePath_RelativeInput_ReturnsRootedPath()
    {
        // 実実装の意味論(A-19 が要求する「絶対パスにする」)。Fake 経由では届かない。
        var result = new FileReachabilityProbe().NormalizePathWithTimeout("memo.txt", Timeout);

        Assert.Equal(PathNormalizeStatus.Ok, result.Status);
        Assert.True(System.IO.Path.IsPathFullyQualified(result.Full));
    }

    [Fact]
    public void NormalizePath_EmbeddedNul_ReturnsInvalid()
    {
        // 実実装の例外フィルタ。NUL 混入は ArgumentException(#47 Task 5 の実測)。
        // Invalid と TimedOut を弁別する(同じ値にする変異をここで kill する)。
        var result = new FileReachabilityProbe().NormalizePathWithTimeout("a\0b", Timeout);

        Assert.Equal(PathNormalizeStatus.Invalid, result.Status);
        Assert.Equal(string.Empty, result.Full);
    }
```

**Step 2: テストが落ちることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
```

期待: `PathNormalizeResult` / `PathNormalizeStatus` / `RunNormalizeProbe` / `NormalizePathWithTimeout` が未定義でコンパイルエラー。

**Step 3: 実装を書く**

`src/kxEdit.App/Abstractions/IReachabilityProbe.cs` の**先頭付近**(既存の `SaveTargetProbeResult` の隣)に追加する:

```csharp
/// <summary>
/// 境界付き正規化の結果状態(Issue #48 / S-15)。
/// <b>ゼロ値をフェイルセーフ側に置いてある</b>: 初期化漏れや <c>default</c> が
/// 「正規化できた」に転ばないようにするため、<see cref="TimedOut"/> を 0 にする。
/// </summary>
public enum PathNormalizeStatus
{
    /// <summary>期限内に終わらなかった。パスは確定していない。</summary>
    TimedOut = 0,

    /// <summary>入力が正規化できない(NUL 混入・総長超過など)。</summary>
    Invalid = 1,

    /// <summary>正規化できた。</summary>
    Ok = 2,
}

/// <summary>
/// 境界付き正規化の結果(Issue #48 / S-15)。
/// <c>Status</c> が <see cref="PathNormalizeStatus.Ok"/> のときだけ <c>Full</c> が意味を持つ。
/// それ以外は空文字。
/// </summary>
/// <param name="Status">結果状態。</param>
/// <param name="Full">正規化済み絶対パス(Ok のときのみ)。</param>
public readonly record struct PathNormalizeResult(PathNormalizeStatus Status, string Full);
```

同ファイルの `IReachabilityProbe` インターフェイスに 3 つ目のメンバーを追加する:

```csharp
    /// <summary>
    /// パスを境界付きで正規化する(Issue #48 / S-15)。
    /// <c>Path.GetFullPath</c> は正規化後のパスに <c>~</c> が含まれると
    /// <c>GetLongPathName</c> を呼び、不達の共有に対して約 21 秒 UI を止める。
    /// **UI スレッドから正規化するときは必ずこれを通す。**
    /// この 1 本だけは<b>正規化前の生パスを渡してよい</b>(他の 2 本と契約が違う。
    /// 正規化そのものが仕事なので)。
    /// </summary>
    PathNormalizeResult NormalizePathWithTimeout(string path, TimeSpan timeout);
```

`src/kxEdit.App/FileReachabilityProbe.cs` にヘルパーと実装を追加する。ヘルパーは既存 2 本の隣(`RunFileExistsProbe` の後)に置く:

```csharp
    /// <summary>
    /// 境界付き正規化の骨格。<paramref name="work"/> をバックグラウンドへ退避し、
    /// 期限内に終わらなければ「確定しなかった」= <see cref="PathNormalizeStatus.TimedOut"/> へ倒す。
    /// フェイルセーフ値をここに置く理由は <see cref="RunFileExistsProbe"/> と同じ:
    /// <c>WaitBounded(task, timeout, <定数>)</c> と直書きすると定数が 1 トークンの引数でしかなく、
    /// <see cref="PathNormalizeStatus.Ok"/> へ書き換えてもコンパイルが通り・ハングもせず・
    /// 全緑になってしまう(= タイムアウトを「正規化できた」と読み、空文字のパスを
    /// 保存先として採用する)。
    /// </summary>
    internal static PathNormalizeResult RunNormalizeProbe(
        Func<PathNormalizeResult> work,
        TimeSpan timeout
    ) =>
        WaitBounded(
            Task.Run(work),
            timeout,
            new PathNormalizeResult(PathNormalizeStatus.TimedOut, string.Empty)
        );

    /// <inheritdoc />
    public PathNormalizeResult NormalizePathWithTimeout(string path, TimeSpan timeout) =>
        RunNormalizeProbe(
            () =>
            {
                try
                {
                    return new PathNormalizeResult(
                        PathNormalizeStatus.Ok,
                        Path.GetFullPath(path)
                    );
                }
                // フィルタは FileController.TryNormalizeSavePath から**そのまま移設**した。
                // #47 の V-2 対策を落とさないこと: 総長が 32767 の直下に収まる窓では
                // GetFullPathNameW が ERROR_INVALID_NAME を返し、PathTooLongException ではなく
                // 素の IOException が飛ぶ。PathTooLongException だけを列挙するとこの窓が抜けて
                // 未捕捉例外ダイアログになる。
                catch (Exception ex)
                    when (ex
                            is ArgumentException
                                or NotSupportedException
                                or IOException
                                or System.Security.SecurityException
                    )
                {
                    return new PathNormalizeResult(PathNormalizeStatus.Invalid, string.Empty);
                }
            },
            timeout
        );
```

`FileReachabilityProbe` のクラス XML コメント冒頭を、3 本になったことに合わせて更新する(「読み取り側の…書き込み側の…」の列挙に正規化を足す)。

`tests/kxEdit.App.Tests/Fakes/FakeReachabilityProbe.cs` に観測点を追加する:

```csharp
    /// <summary>
    /// <c>NormalizePathWithTimeout</c> の応答。既定は「渡されたパスをそのまま Ok で返す」=
    /// 正規化が成功したものとして素通しする形(既存テストの挙動を変えないため)。
    /// null のときだけ既定動作、非 null ならその固定値を返す。
    /// </summary>
    public PathNormalizeResult? NormalizeResult { get; set; }

    public int NormalizeCallCount { get; private set; }

    /// <summary>直近の <c>NormalizePathWithTimeout</c> 呼出で渡された path。</summary>
    public string? NormalizeLastPath { get; private set; }

    /// <summary>直近の <c>NormalizePathWithTimeout</c> 呼出で渡された timeout(5s 契約の pin)。</summary>
    public TimeSpan NormalizeLastTimeout { get; private set; }

    public PathNormalizeResult NormalizePathWithTimeout(string path, TimeSpan timeout)
    {
        NormalizeCallCount++;
        NormalizeLastPath = path;
        NormalizeLastTimeout = timeout;
        // 既定は「実 GetFullPath と同じ答え」を返す。Fake が素通し(path をそのまま返す)だと
        // 相対パス入力のテストが「正規化されたつもり」で通ってしまい、A-19 の網が
        // vacuous になる(#47 の教訓: Fake を注入するテストは本番実装の性質を証人にできない)。
        return NormalizeResult
            ?? new FileReachabilityProbe().NormalizePathWithTimeout(path, timeout);
    }
```

**Step 4: テストが通ることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

期待: 全緑・0 warning。既存 App テストは一行も変えずに緑のまま(誰も新メンバーを呼んでいない)。

**Step 5: commit**

```bash
git add src/kxEdit.App/Abstractions/IReachabilityProbe.cs src/kxEdit.App/FileReachabilityProbe.cs tests/kxEdit.App.Tests/Fakes/FakeReachabilityProbe.cs tests/kxEdit.App.Tests/FileReachabilityProbeTests.cs
git commit -m "feat(app): 境界付きパス正規化の seam を追加する(Issue #48 / 設計書 §4)

IReachabilityProbe に NormalizePathWithTimeout を足し、既存 2 本と同じ
WaitBounded / Run*Probe の書式に揃える。フェイルセーフ値は RunNormalizeProbe
側に置く(引数に直書きすると変異が全緑で生存するため)。

3 状態(Ok / Invalid / TimedOut)にしたのは、不正入力と到達不能で文言を
変えるため。enum のゼロ値は TimedOut に置き、初期化漏れが成功に転ばない
ようにした。

まだ誰も呼ばないので挙動不変。"
```

**Step 6: 前倒しコード品質レビュー**

新しい seam を導入したので、CLAUDE.md §3 の前倒し例外に該当する。**別エージェント**でコード品質レビューを行い、指摘は ① fixup commit / ② PR description に記載して受容 / ③ 理由付き却下 の 3 択で明示する。

---

## Task 3: `TryNormalizeSavePath` を seam 経由にする

**レビュー区分:** **前倒し脆弱性レビュー**(パス操作・外部入力のパース)

**Files:**
- Modify: `src/kxEdit.App/FileController.cs`(`TryNormalizeSavePath` = `:552-611` 付近、呼出は `:422-432` 付近)
- Test: `tests/kxEdit.App.Tests/FileControllerTests.cs`

**Step 1: 失敗するテストを書く**

`tests/kxEdit.App.Tests/FileControllerTests.cs` に追記する:

```csharp
    // ===== 境界付き正規化(Issue #48 / S-15)=====

    [Fact]
    public void SaveAs_NormalizeTimesOut_ShowsReachabilityMessage_AndDoesNotSave() =>
        Sta.Run(() =>
        {
            // S-15: 不達共有上の `~` を含むパスは GetFullPath が約 21 秒 UI を止める。
            // seam のタイムアウトで中止し、**打ち間違いとは別の文言**を出すことを固定する
            // (同じ文言だと、到達不能が原因なのに利用者が入力を疑い続ける)。
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Probe.NormalizeResult = new PathNormalizeResult(
                PathNormalizeStatus.TimedOut,
                string.Empty
            );
            host.Dialogs.SaveAs = new SaveAsResult(@"C:\Temp\a.txt", 65001, false, LineEnding.Crlf);
            host.Dialogs.SaveAsCallsBeforeCancel = 1; // 再表示ループを 1 回で抜ける

            Assert.False(host.File.SaveAs());

            Assert.Null(doc.State.Path); // 保存されていない
            Assert.Contains("到達", host.Prompt.LastWarnOrErrorText);
        });

    [Fact]
    public void SaveAs_NormalizeInvalid_ShowsInvalidPathMessage() =>
        Sta.Run(() =>
        {
            // 対照群。Invalid と TimedOut を同じ文言にする変異をここで kill する。
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Probe.NormalizeResult = new PathNormalizeResult(
                PathNormalizeStatus.Invalid,
                string.Empty
            );
            host.Dialogs.SaveAs = new SaveAsResult(@"C:\Temp\a.txt", 65001, false, LineEnding.Crlf);
            host.Dialogs.SaveAsCallsBeforeCancel = 1;

            Assert.False(host.File.SaveAs());

            Assert.Null(doc.State.Path);
            Assert.Contains("正しくありません", host.Prompt.LastWarnOrErrorText);
            Assert.DoesNotContain("到達", host.Prompt.LastWarnOrErrorText);
        });

    [Fact]
    public void SaveAs_PassesFiveSecondTimeoutToNormalizeProbe() =>
        Sta.Run(() =>
        {
            // 5 秒契約の pin(既存 2 本の LastTimeout と同じ思想)。
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAs = new SaveAsResult(tmp.File("a.txt"), 65001, false, LineEnding.Crlf);

            Assert.True(host.File.SaveAs());

            Assert.Equal(TimeSpan.FromSeconds(5), host.Probe.NormalizeLastTimeout);
        });
```

> **実装者への注意**: `host.Prompt.LastWarnOrErrorText` / `host.Dialogs.SaveAsCallsBeforeCancel` は**既存の Fake に同名のものが無ければ、その場で最小の観測点として足す**。`tests/kxEdit.App.Tests/Fakes/` の既存 `FakePrompt` / `FakeFileDialogService` を読み、既にある観測点(例: `LastError` / `Warns` のリスト)があればそれを使い、新設しない。**既存の書式に合わせることを優先する。**

**Step 2: テストが落ちることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

期待: 3 本すべて FAIL(タイムアウト文言が無い / seam が呼ばれていない)。

**Step 3: 実装を書く**

`TryNormalizeSavePath` を static から**インスタンスメソッド**に変え、seam を経由させる。返り値を 3 状態にする:

```csharp
    /// <summary>
    /// A-19: 直入力の相対パス(memo.txt)を絶対パスへ正規化する。未正規化のまま State.Path に
    /// 残すと保存先が起動時のカレントディレクトリに依存し、hot exit 復元で無言の無題化を招く。
    /// 例外は握って呼出側で「入力し直し」に落とす: SR ユーザーの直入力がそのまま届く面なので
    /// 未捕捉例外ダイアログにしない。
    /// PathKey.For も内部で GetFullPath するが、あちらは失敗時に空文字へ落として dedup キーを
    /// 1 件へ集約する契約(CSV-L-8)= ユーザーに直させる本メソッドとは契約が違うので流用しない。
    /// </summary>
    /// <remarks>
    /// .NET 9 での実測(Task 5 実装時): <c>Path.GetFullPath</c> が投げるのは実質
    /// (a) NUL 文字混入 → <see cref="ArgumentException"/>(本テストの pin)、
    /// (b) 空 / 空白のみ → <see cref="ArgumentException"/>(手前の空白チェックが先に捕まえる)、
    /// (c) 総長 &gt; 32767 → <see cref="System.IO.PathTooLongException"/> の 3 つ。
    /// <c>&lt;</c> <c>|</c> <c>"</c> などの「無効文字」や予約デバイス名(CON / NUL)は
    /// **投げずに素通りする**ので、このフィルタは無効文字の門番ではない
    /// (デバイス名・ドライブルートは呼出側の「親フォルダーが取れるか」ガードが弾く)。
    /// <b>V-2(脆弱性レビューで解消済み)</b>: 総長が 32767 の直下に収まる窓
    /// (実測 CWD 110 文字 + 相対 32660 文字)では <c>GetFullPathNameW</c> が
    /// ERROR_INVALID_NAME を返し、<see cref="System.IO.PathTooLongException"/> ではなく
    /// **素の <see cref="System.IO.IOException"/>** が飛ぶ。派生関係は一方向なので
    /// <c>PathTooLongException</c> だけを列挙するとこの窓が抜けて未捕捉例外ダイアログになった。
    /// 設計書 §4.3 の列挙は実測と食い違っていたため、<c>IOException</c>(厳密な上位集合)へ
    /// 広げて訂正する。
    /// <para>
    /// <b>Issue #48 (S-15) による訂正</b>: 以前ここには
    /// 「<c>Path.GetFullPath</c> は <c>GetFullPathNameW</c> による名前解決のみで実 I/O を
    /// 行わないので、握り潰してはいけない実 I/O エラーを飲み込む余地はない」と書いてあった。
    /// <b>これは誤り。</b> 正規化後のパスに <c>~</c> が含まれると <c>GetLongPathName</c> を
    /// 呼び、これは境界の無い実ファイルシステム / ネットワーク呼び出しになる
    /// (不達共有で実測 21,002 ms・2026-08-23)。この誤認が S-15 を通した。
    /// 正規化は <see cref="IReachabilityProbe.NormalizePathWithTimeout"/> 経由の
    /// 境界付きで行う。
    /// </para>
    /// </remarks>
    private PathNormalizeResult NormalizeSavePath(string input) =>
        _reachabilityProbe.NormalizePathWithTimeout(input, TimeSpan.FromSeconds(5));
```

呼出側(`SaveAsDocument` ループ内・現行 `:422-432`)を差し替える:

```csharp
            var norm = NormalizeSavePath(picked.Path);
            if (
                norm.Status != PathNormalizeStatus.Ok
                || string.IsNullOrEmpty(System.IO.Path.GetDirectoryName(norm.Full))
            )
            {
                // S-15: 到達不能(タイムアウト)と打ち間違い(Invalid)で文言を分ける。
                // 同じ文言だと、原因がネットワークなのに利用者が入力を疑い続ける。
                // 親フォルダーが取れない場合(ドライブルート直打ち = #47 の V-1)は
                // 従来どおり「正しくありません」側に入れる。
                _prompt.Warn(
                    norm.Status == PathNormalizeStatus.TimedOut
                        ? $"保存先に到達できませんでした(5 秒)。ネットワーク接続を確認してください: {SanitizeForDisplay.OneLine(picked.Path, 200)}"
                        : $"パスが正しくありません: {SanitizeForDisplay.OneLine(picked.Path, 200)}",
                    "エラー"
                );
                continue;
            }
            string full = norm.Full;
```

以降の `full` を使う箇所は変更不要。

**Step 4: テストが通ることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

期待: 全緑・0 warning。**既存の SaveAs 系テストが 1 本も壊れていないこと**を確認する(Fake の既定が実実装へ委譲するので、正規化の答えは従来と同じ)。

**Step 5: commit**

```bash
git add src/kxEdit.App/FileController.cs tests/kxEdit.App.Tests/
git commit -m "fix(app): SaveAs の正規化を境界付きにする(Issue #48 / 設計書 §4.1)

TryNormalizeSavePath を seam 経由の NormalizeSavePath に置き換える。
到達不能(タイムアウト)と打ち間違い(Invalid)で文言を分けた: 同じ文言だと
原因がネットワークなのに利用者が入力を疑い続ける。

あわせて、S-15 を通した誤認のもとになっていたコメント
「GetFullPath は名前解決のみで実 I/O を行わない」を実測付きで訂正した。"
```

**Step 6: 前倒し脆弱性レビュー**

パス操作・外部入力のパースに触れたので、**別エージェント**で脆弱性レビューを行う。特に確認させる点:

- #47 の V-1(ドライブルート `C:\` 直打ちで未捕捉例外 + 無断で dirty が落ちる)の防御が生きているか。
- #47 の V-2(32767 境界の素の `IOException`)のフィルタが seam 側へ正しく移設されているか。
- `SanitizeForDisplay.OneLine` が新しい文言にも掛かっているか(CSV-L-5)。

---

## Task 4: `TryOpenOrActivate` の入口で正規化する

**レビュー区分:** **前倒し脆弱性レビュー**(パス操作)

**Files:**
- Modify: `src/kxEdit.App/FileController.cs`(`TryOpenOrActivate` = `:147-190` 付近)
- Test: `tests/kxEdit.App.Tests/FileControllerTests.cs`

これで `State.Path` に入る非 null 値がすべて正規化済みになる(設計書 §3.6)。

**Step 1: 失敗するテストを書く**

```csharp
    [Fact]
    public void TryOpenOrActivate_NormalizeTimesOut_ReturnsNull_AndLeavesNoTab() =>
        Sta.Run(() =>
        {
            // 作りかけタブを残さないこと(残すと次の RestoreSession が
            // initialEmpty を閉じられない等の二次汚染につながる = 既存 Task 5 review I-1 の論点)。
            using var host = new Host();
            int before = host.Docs.Count;
            host.Probe.NormalizeResult = new PathNormalizeResult(
                PathNormalizeStatus.TimedOut,
                string.Empty
            );

            Assert.Null(host.File.TryOpenOrActivate(@"C:\Temp\a.txt"));

            Assert.Equal(before, host.Docs.Count);
        });

    [Fact]
    public void TryOpenOrActivate_StoresNormalizedAbsolutePath() =>
        Sta.Run(() =>
        {
            // 不変条件(設計書 §3.1)の本体。区切りが混ざった入力でも State.Path は
            // 正規化済み絶対パスになる。Fake の既定は実実装へ委譲するので、
            // ここは本番の GetFullPath の答えを見ている。
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            System.IO.File.WriteAllText(path, "x");

            var doc = host.File.TryOpenOrActivate(path.Replace('\\', '/'))!;

            Assert.Equal(path, doc.State.Path);
        });
```

**Step 2: テストが落ちることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

期待: 2 本とも FAIL。

**Step 3: 実装を書く**

`TryOpenOrActivate` の冒頭に正規化を差し込む:

```csharp
    public Document? TryOpenOrActivate(string path, bool suppressAutoCsv = false)
    {
        // Issue #48 / 設計書 §3.6: ここが State.Path へ未正規化パスが入る唯一の入口だった。
        // 以降(FindByPath / LoadInto / RegisterRecent)はすべて正規化済みパスを受け取る契約に
        // なるので、ここで 1 回だけ、境界付きで正規化する。
        // 境界付きにする理由は S-15: `~` を含むパスでは GetFullPath が実ネットワーク呼び出しに
        // なり、不達共有で約 21 秒 UI を止める。
        var norm = _reachabilityProbe.NormalizePathWithTimeout(path, TimeSpan.FromSeconds(5));
        if (norm.Status != PathNormalizeStatus.Ok)
        {
            _prompt.Error(
                norm.Status == PathNormalizeStatus.TimedOut
                    ? $"ファイルに到達できませんでした(5 秒)。ネットワーク接続を確認してください: {SanitizeForDisplay.OneLine(path, 200)}"
                    : $"パスが正しくありません: {SanitizeForDisplay.OneLine(path, 200)}",
                "エラー"
            );
            return null;
        }
        path = norm.Full;

        var existing = _docs.FindByPath(path);
        // ...以降は現行のまま...
```

> **注意 1(訂正・Task 2 コード品質レビュー I-4)**: 当初この節には「復元経路は既に正規化済みのパスを渡すので seam の追加呼出は速い(0 ms)」と書いていた。**これは誤り。** `GetFullPath` の `~` 展開は**成功したときだけ** `~` を消す。不達共有では展開が失敗して**戻り値に `~` が残る**ため、「正規化済み」のパスでも呼ぶたびに同じコストを払う。実測(レビュアー・.NET 9.0.8):
>
> ```
> C:\PROGRA~1\a.txt    -> C:\Program Files\a.txt   (展開成功 = 実 I/O している証拠)
> C:\NOSUCH~1\a.txt    -> C:\NOSUCH~1\a.txt        (展開失敗・~ が残る)
> \\?\C:\PROGRA~1\...  -> 変化なし                  (device path は展開自体をバイパス)
> ```
>
> **正規化はコストの意味で冪等ではない。** 復元で不達共有上の `~` ファイルが K 件あれば、5 秒 × K を払う(main の 21 秒 × K よりは良いが「速い」ではない)。設計書 §3 の「操作あたり 1 本」は守られているが、「復元は速い」は成り立たない。

> **注意 2**: 復元経路(`:957` / `:1104`)からも `TryOpenOrActivate` が呼ばれる。**復元経路は `_suppressLoadErrorPrompt` でダイアログを抑止するスコープがある**。新しい `_prompt.Error` がそのスコープを尊重するかを確認し、既存の失敗経路(`LoadInto` の catch)と同じ扱いに揃えること。抑止スコープを見落とすと、起動時に復元ダイアログが増える。

> **注意 3(Task 2 コード品質レビュー m-7)**: `Path.GetFullPath` はデバイス名・デバイスパスを**例外を投げずに通す**。実測: `GetFullPath("CON")` → `\\.\CON`、`GetFullPath(@"\\?\")` → `\\?\` がどちらも `Ok` で返る。seam の `Ok` は「文字列として正規化できた」以上の意味を持たない。入口に正規化を差すと `State.Path` に `\\.\CON` が入りうるので、**PR #47 の V-1 相当の「親フォルダーが取れるか」ガードは正規化の後にも必要**。脆弱性レビューでここを重点的に見させること。

**Step 4: テストが通ることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

期待: 全緑・0 warning。特に復元系(`BackupCoordinatorTests` / `RestoreDialogTests` / `FileControllerTests` の復元節)が緑であること。

**Step 5: commit**

```bash
git add src/kxEdit.App/FileController.cs tests/kxEdit.App.Tests/FileControllerTests.cs
git commit -m "fix(app): TryOpenOrActivate の入口でパスを境界付き正規化する(Issue #48 / 設計書 §3.6)

State.Path へ未正規化パスが入る唯一の入口を塞ぐ。これで
「State.Path は null か正規化済み絶対パス」の不変条件が成立し、
FindByPath / RecentFilesList の比較側を軽くできる(次タスク)。"
```

**Step 6: 前倒し脆弱性レビュー**

Task 3 と同じ観点に加えて、**復元経路のダイアログ抑止スコープ**が守られているかを重点的に見させる。

---

## Task 4B: `OriginalPathValidator` の GLOBALROOT 回避を塞ぐ(スコープ追加・セキュリティ修正)

**レビュー区分:** **前倒し脆弱性レビュー**(パス操作・セキュリティ修正)

**Files:**
- Modify: `src/kxEdit.Core/Backup/OriginalPathValidator.cs:50-80` 付近
- Test: `tests/kxEdit.Core.Tests/Backup/OriginalPathValidatorTests.cs`

### 経緯(このタスクは当初の計画に無い)

Task 4 の脆弱性レビューが、**本ブランチ由来ではない既存の脆弱性**を発見した。ユーザー判断で本ブランチに含める(当初は非公開 Advisory の下書きを推奨したが、ブランチ内で直す方針を選択)。

CLAUDE.md §9 は「セキュリティ修正も §3 と同じプロセスで行う」と定める。本節がその設計に相当する。

### 欠陥

`OriginalPathValidator.Check` は DOS device path プレフィックスを**先頭 4 文字だけ剥がして** `BlockedRoots` と前方一致照合する。`\\?\GLOBALROOT\Device\HarddiskVolumeN\...` は剥がした残りが `GLOBALROOT\Device\...` になり、`C:\Windows\` と決して前方一致しない。

**実測(2026-08-23・ビルド済み `kxEdit.Core.dll` を反射で直接呼出)**:

```
plain hosts            Check=Rejected   readable=True   ← 正しく拒否
devicepath hosts       Check=Rejected   readable=True   ← \\?\C:\ は既に塞がれている
GLOBALROOT hosts       Check=Ok         readable=True   ← 素通り
GLOBALROOT win.ini     Check=Ok         readable=True
```

`HarddiskVolume6` がこの環境の C: に解決することも実測で確認した(`\\?\GLOBALROOT\Device\HarddiskVolume6\Windows\win.ini` が 92 バイトで読める)。ボリューム番号は環境依存なので、**テストで番号を決め打ちしないこと**(後述)。

**攻撃導線**: `%AppData%\kxEdit` のバックアップ JSON に `OriginalPath` = 上記綴り + 任意 `Content` を仕込むと、`FileController.RestoreDirtyFromBackup` が起動時に `State.Path` にそれを載せた **dirty タブ**を作る。以後ユーザーの Ctrl+S、または終了時の「保存しますか?」1 回で `hosts` が攻撃者内容に置き換わる。`BlockedRoots` はまさにこれを防ぐ機構なので、**既存のセキュリティ制御を無効化する**。`SECURITY.md` の「想定される攻撃面」の「バックアップファイル: 復元機能を悪用したパストラバーサル、任意ファイル上書き」に該当。

### 修正方針

**前置ガードの列挙で塞がない。** プレフィックスの種類を列挙して弾く方式は原理的に漏れる(PR #43 の教訓「前置ガードの列挙は原理的に漏れる → 事後条件で検査する」)。

**事後条件で検査する**: プレフィックス除去後の `forCheck` が、次の**どちらかの形であることを要求**する。該当しなければ `Rejected`。

1. ドライブ文字ルート形式 — `X:\...`(`Path.IsPathFullyQualified` かつ 2 文字目が `:` かつ 3 文字目が区切り)
2. UNC 形式 — `\\server\share\...`(サーバー名と共有名の両方がある)

これで `GLOBALROOT\Device\...` / `Volume{GUID}\...` / `Device\...` / `pipe\...` はすべて落ちる。**「何を拒否するか」ではなく「何を許可するか」を書く**ので、新しい device 名前空間が増えても漏れない。

**確認すべき経路(すべて現状どおり通ること)**:

| 入力 | 除去後 | 期待 |
|------|--------|------|
| `C:\work\a.txt` | 同左 | Ok |
| `\\?\C:\work\a.txt` | `C:\work\a.txt` | Ok |
| `\\.\C:\work\a.txt` | `C:\work\a.txt` | Ok |
| `\\server\share\a.txt` | 同左 | Ok |
| `\\?\UNC\server\share\a.txt` | `\\server\share\a.txt` | Ok |
| `C:\Windows\System32\drivers\etc\hosts` | 同左 | **Rejected**(BlockedRoots) |
| `\\?\C:\Windows\...\hosts` | `C:\Windows\...\hosts` | **Rejected**(BlockedRoots) |
| `\\?\GLOBALROOT\Device\HarddiskVolumeN\Windows\...\hosts` | `GLOBALROOT\Device\...` | **Rejected**(本タスクの新規) |
| `\\?\Volume{GUID}\a.txt` | `Volume{GUID}\a.txt` | **Rejected**(新規・後述の受容) |
| `\\.\PhysicalDrive0` | `PhysicalDrive0` | **Rejected**(新規) |
| `\\.\pipe\foo` | `pipe\foo` | **Rejected**(新規) |

### 意図的な挙動変更(受容)

**ボリューム GUID パス(`\\?\Volume{GUID}\...`)が拒否される。** ドライブ文字を割り当てずにマウントしたボリューム上のファイルを開いていた場合、hot exit 復元で**無題タブに降格**する(本文は残る・パスが失われる)。

受容の理由: (a) 「開く」ダイアログと `\\server\share` 経由の通常操作ではこの綴りは生まれない、(b) 降格は本文を失わない安全側の失敗、(c) 許可リストに `Volume{GUID}` を足すと `GLOBALROOT` との弁別が形式的に難しくなり、事後条件方式の利点が薄れる。**PR description に明記する。**

### テスト設計

`tests/kxEdit.Core.Tests/Backup/OriginalPathValidatorTests.cs` に追加する。

**ボリューム番号を決め打ちしないこと。** `HarddiskVolume6` は環境依存。攻撃綴りのテストは「**`BlockedRoots` 配下を指す GLOBALROOT 綴りが `Rejected` になる**」ことを固定すればよく、実ファイルが読めるかは検証対象ではない(読める番号を探す処理はテストを環境依存にする)。番号は固定値でよい — 検査は**文字列の形**だけを見るので、存在しないボリューム番号でも `Rejected` になるのが正しい。

| 網 | 内容 |
|----|------|
| GLOBALROOT 拒否 | `\\?\GLOBALROOT\Device\HarddiskVolume1\Windows\System32\drivers\etc\hosts` → `Rejected` |
| GLOBALROOT 拒否(BlockedRoots 外でも) | `\\?\GLOBALROOT\Device\HarddiskVolume1\Temp\a.txt` → `Rejected`(**形で弾くので配下は問わない**) |
| device 名前空間の拒否 | `\\.\PhysicalDrive0` / `\\.\pipe\foo` → `Rejected` |
| Volume GUID の拒否 | `\\?\Volume{00000000-0000-0000-0000-000000000000}\a.txt` → `Rejected` |
| **回帰(通ること)** | `C:\work\...` / `\\?\C:\...` / `\\.\C:\...` / `\\server\share\...` / `\\?\UNC\server\share\...` が従来どおり `Ok` |
| **回帰(拒否のまま)** | `C:\Windows\...\hosts` / `\\?\C:\Windows\...\hosts` が `Rejected`(**理由が BlockedRoots のままであること**) |

**ミューテーション検証**:

1. 新しい事後条件を丸ごと削除 → GLOBALROOT のテストが赤になるか。
2. 事後条件を「UNC 形式のみ許可」に変異(ドライブ文字を落とす)→ 回帰テスト(`C:\work\...` が Ok)が赤になるか。**赤にならないなら回帰の網が無い。**
3. 事後条件を「ドライブ文字のみ許可」に変異 → `\\server\share\...` の回帰テストが赤になるか。
4. `\\?\UNC\` の剥がし処理を壊す → `\\?\UNC\server\share\...` の回帰テストが赤になるか。

### 非目標

- **admin share 経由の pivot**(`\\host\C$\Windows\...`)は既存の受容のまま(クラス doc の「現状の許容」に明記済み)。本タスクで変えない。
- reparse point 検査(BK-M-1)の挙動は変えない。

### 申し送り

- マージ後に **GitHub Security Advisory の要否を検討**する(CLAUDE.md §9)。`SECURITY.md` は「深刻度が高い問題については、修正リリースと同時に GitHub Security Advisory を公開します」と約束している。

---

## Task 5: `DocumentManager.FindByPath` を `ForNormalized` にする

**レビュー区分:** 通常(仕様レビュー)

**Files:**
- Modify: `src/kxEdit.App/DocumentManager.cs:105-113`
- Test: `tests/kxEdit.App.Tests/DocumentManagerTests.cs:115-137`

**ここが S-15 の主犯**(1 + タブ数の `GetFullPath`)。

### 意図的な挙動変更

`FindByPath` は今後、**区切り差や相対セグメントを吸収しない**。呼出側が正規化済みパスを渡す契約になるため。**ユニットレベルの契約は変わる**ので、既存テスト `FindByPath_MatchesCaseAndSeparatorInsensitively` は新契約を固定する形に書き換える。CLAUDE.md §2 に従い、この変更は PR description に明記する。

### 【訂正・Task 4 の申し送り】呼出側は 4 箇所ではなく 5 箇所で、1 箇所が正規化されていない

当初この節には「App レベルでは呼出側 4 箇所すべてが正規化済みパスを渡すので挙動不変」と書いていた。**これは誤り。** 実際の `_docs.FindByPath` 呼出は 5 箇所ある:

| 場所 | 渡す値 | 正規化済みか |
|------|--------|-------------|
| `FileController.cs:214` | `full` | 済(Task 4) |
| `FileController.cs:417` | `doc.State.Path` | 済(§3.1 の不変条件) |
| `FileController.cs:535` | `full` | 済(Task 3・SaveAs) |
| **`FileController.cs:1025`** | **`rec.Path`** | **未**(レイアウト JSON 由来) |
| `FileController.cs:1172` | `normalized` | 済(`OriginalPathValidator.Check` 出力) |

`:1025` の `rec.Path` はレイアウト JSON 由来で、旧バージョンが書いたものや攻撃者 JSON では正規化されている保証がない。Task 4 までは同じ行の `TryOpenOrActivate(rec.Path)` と**両者が同じ生パスを見ていたので整合していた**が、Task 4 で `TryOpenOrActivate` が入口で正規化するようになったため、**`FindByPath` を `ForNormalized` にすると `:1025` だけが区切り差を吸収しなくなり `existedBefore` が false に倒れる**。

その先の `if (pathOnlyBk is not null && !existedBefore) adoptRestored(...)` は「fast-path activate(既存タブ)には adopt しない = Id 上書きで別のゾンビを作らない」ための門番なので、**崩れると既存タブの adopt を上書きする**。

**Task 5 の必須対応**: この呼出点で正規化済みパスを 1 本作って `FindByPath` と `TryOpenOrActivate` の両方に渡すか、`TryOpenOrActivate` に「既存タブを再利用したか」を返させる。**どちらを採るにせよ、`existedBefore` が正しく true になることを固定するテストを足すこと**(未正規化の `rec.Path` で既存タブがある fixture)。

**Step 1: 既存テストを新契約へ書き換え、回数の網を足す**

`tests/kxEdit.App.Tests/DocumentManagerTests.cs` の `FindByPath` 節を置き換える:

```csharp
    // ===== FindByPath(PathKey.ForNormalized 照合)=====
    // Issue #48: 以前は照会パスと**開いている全タブのパス**に PathKey.For を打っていた
    // (= 呼び出しあたり GetFullPath が 1 + タブ数回)。不達共有上の `~` タブが 1 つあるだけで
    // Ctrl+S / 開く / grep ジャンプ / 復元のすべてが約 21 秒固まった。
    // 呼出側が正規化済みパスを渡す契約に変え、ここはファイルシステムに触れない。

    [Fact]
    public void FindByPath_MatchesCaseInsensitively() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.State.Path = @"C:\Temp\A.TXT";
            Assert.Same(doc, host.Docs.FindByPath(@"c:\temp\a.txt")); // 大小文字は同一視
        });

    [Fact]
    public void FindByPath_DoesNotNormalizeSeparators_CallerMustNormalize() =>
        Sta.Run(() =>
        {
            // 新契約の pin(意図的な挙動変更)。ここで区切りを吸収させると
            // GetFullPath が戻り、S-15 が丸ごと再発する。
            // App レベルの呼出側は全員 TryOpenOrActivate / NormalizeSavePath を通るので
            // 実害は無い(設計書 §3.3)。
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.State.Path = @"C:\Temp\a.txt";
            Assert.Null(host.Docs.FindByPath("C:/Temp/a.txt"));
        });

    [Fact]
    public void FindByPath_IgnoresUntitled_AndReturnsNullWhenNoMatch() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            _ = host.Docs.CreateNew(); // 未保存(Path=null)は対象外
            var doc = host.Docs.CreateNew();
            doc.State.Path = @"C:\Temp\a.txt";
            Assert.Null(host.Docs.FindByPath(@"C:\Temp\other.txt"));
        });
```

**Step 2: テストが落ちることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

期待: `FindByPath_DoesNotNormalizeSeparators_CallerMustNormalize` が FAIL(現行は吸収するので `Same` が返る)。

**Step 3: 実装を書く**

`src/kxEdit.App/DocumentManager.cs`:

```csharp
    /// <summary>
    /// 保存済みの同一パスを開いているタブを探す（未保存タブは対象外）。
    /// <b>引数は正規化済み絶対パス</b>(Issue #48 / 設計書 §3.1 の不変条件)。
    /// ここではファイルシステムに触れない — 触ると開いているタブ数に比例して
    /// <c>GetFullPath</c> が走り、不達共有上の <c>~</c> パスが 1 つあるだけで
    /// UI が約 21 秒固まる(S-15)。正規化は呼出側が
    /// <see cref="IReachabilityProbe.NormalizePathWithTimeout"/> で 1 回だけ行う。
    /// </summary>
    public Document? FindByPath(string path)
    {
        string key = PathKey.ForNormalized(path);
        foreach (var d in _docs)
            if (d.State.Path is not null && PathKey.ForNormalized(d.State.Path) == key)
                return d;
        return null;
    }
```

**Step 4: テストが通ることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

期待: 全緑・0 warning。特に A-7 (b) の重複タブ検知テスト群(`FileControllerTests` の `:784-900` 付近)が緑であること = ガードが生きている証拠。

**Step 5: commit**

```bash
git add src/kxEdit.App/DocumentManager.cs tests/kxEdit.App.Tests/DocumentManagerTests.cs
git commit -m "fix(app): FindByPath をファイルシステム非依存にする(Issue #48 / 設計書 §3.3)

照会パス + 開いている全タブのパスに PathKey.For を打っていたため、
呼び出しあたり GetFullPath が 1+N 回走っていた。不達共有上の ~ タブが
1 つあるだけで Ctrl+S / 開く / grep ジャンプ / 復元が約 21 秒固まる。

呼出側が正規化済みパスを渡す契約(前タスクで成立)に変え、ここは
ToLowerInvariant のみにした。

意図的な挙動変更: 区切り差・相対セグメントを吸収しなくなる。App レベルの
呼出側は全員 seam を通るので実害は無い。"
```

---

## Task 6: `RecentFilesList.Add` を `ForNormalized` にする

**レビュー区分:** 通常(仕様レビュー)

**Files:**
- Modify: `src/kxEdit.Core/Text/RecentFilesList.cs:33-51`
- Test: `tests/kxEdit.Core.Tests/Text/RecentFilesListTests.cs`

**これは #47 由来ではなく既存バグ**(v0.2 監査にも載っていない)。`RegisterRecent` は**開くたび・保存が成功するたび**に走り、最近のファイルは設定に永続するので、一度不達共有上の `~` パスを開けば以後ずっと踏む。

### 受容する劣化

既存 `settings.json` に残る未正規化エントリーは dedup されなくなる(同一ファイルが最大 1 件重複して並びうる)。データ損失は無く、1 度開き直せば正規化済みで入り直す。設計書 §3.4 の判断。PR description に記載する。

**Step 1: 既存テストを新契約へ書き換え、受容を明示的に固定する**

`tests/kxEdit.Core.Tests/Text/RecentFilesListTests.cs` の `Dedup_is_pathkey_normalized_case_and_separators` を置き換える:

```csharp
    [Fact]
    public void Dedup_is_case_insensitive()
    {
        // 同一ファイルの大小違いは 1 件に集約される。
        var r = RecentFilesList.Add(new[] { @"C:\Dir\A.TXT" }, @"c:\dir\a.txt", 10);
        Assert.Single(r);
        Assert.Equal(@"c:\dir\a.txt", r[0]); // 新規入力が先頭
    }

    [Fact]
    public void Dedup_does_not_normalize_separators_accepted_degradation()
    {
        // Issue #48 / 設計書 §3.4 の**受容**を明示的に固定する。
        // 以前は PathKey.For(= GetFullPath)で区切り差を吸収していたが、それは
        // 履歴件数に比例した実 I/O を意味し、不達共有上の `~` パスで
        // 開く・保存のたびに約 21 秒 UI が固まっていた(S-15 と同一機構)。
        //
        // 本バージョンが書き込むエントリーは正規化済みなのでこの経路には入らない。
        // 既存 settings.json に残るレガシーエントリーだけが、1 度開き直すまで
        // 重複して並びうる。データ損失は無い。
        var r = RecentFilesList.Add(new[] { "c:/dir/a.txt" }, @"C:\Dir\a.txt", 10);
        Assert.Equal(2, r.Count); // 吸収しない = 2 件並ぶ
    }
```

**Step 2: テストが落ちることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build
```

期待: `Dedup_does_not_normalize_separators_accepted_degradation` が FAIL(現行は 1 件に集約する)。

**Step 3: 実装を書く**

`src/kxEdit.Core/Text/RecentFilesList.cs`:

```csharp
    /// <summary>
    /// current の先頭に path を加えた新リストを返す。path と同一（<see cref="PathKey.ForNormalized"/>
    /// 一致）の既存項目は除き、全体を max 件にクランプする。max が 0 以下なら空リスト。
    /// <b>path と current の各項目は正規化済み絶対パス</b>(Issue #48 / 設計書 §3.1 の不変条件)。
    /// </summary>
    /// <remarks>
    /// Issue #48: 以前はここで <see cref="PathKey.For"/>(= <c>GetFullPath</c>)を
    /// 1 + 履歴件数だけ呼んでいた。<c>RegisterRecent</c> は開くたび・保存が成功するたびに走り、
    /// 最近のファイルは設定に永続するので、一度でも不達共有上の <c>~</c> パスを開くと
    /// 以後すべての開く・保存が約 21 秒固まった(S-15 と同一機構・#47 以前からの既存バグ)。
    /// 既存 settings.json に残る未正規化エントリーは dedup されなくなるが、
    /// データ損失は無く 1 度開き直せば解消する(設計書 §3.4 の受容)。
    /// </remarks>
    public static List<string> Add(IEnumerable<string> current, string path, int max)
    {
        var result = new List<string>();
        if (max <= 0)
            return result;

        result.Add(path);
        string key = PathKey.ForNormalized(path);
        foreach (string p in current)
        {
            if (result.Count >= max)
                break; // 追加前に上限判定（max==1 の超過を防ぐ）
            if (PathKey.ForNormalized(p) == key)
                continue; // 同一ファイルは先頭の 1 件に集約
            result.Add(p);
        }
        return result;
    }
```

**Step 4: テストが通ることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests  -c Release --no-build
```

期待: 全緑・0 warning。

**Step 5: commit**

```bash
git add src/kxEdit.Core/Text/RecentFilesList.cs tests/kxEdit.Core.Tests/Text/RecentFilesListTests.cs
git commit -m "fix(core): RecentFilesList.Add をファイルシステム非依存にする(Issue #48 / 設計書 §3.4)

#47 由来ではない既存バグ。RegisterRecent は開くたび・保存が成功するたびに
走り、最近のファイルは設定に永続するので、一度でも不達共有上の ~ パスを
開くと以後すべての開く・保存が約 21 秒固まっていた。

既存 settings.json のレガシーエントリーは dedup されなくなる(データ損失
なし・1 度開き直せば解消)。受容としてテストで明示的に固定した。"
```

---

## Task 7: 不変条件を `Debug.Assert` で担保する

**レビュー区分:** 通常(仕様レビュー)

**Files:**
- Modify: `src/kxEdit.App/DocumentState.cs`
- Test: `tests/kxEdit.App.Tests/DocumentManagerTests.cs`

**これを先のタスクに前倒ししてはいけない。** Task 4 より前に置くと、まだ正規化されていない入口で発火する。

**Step 1: まず現状の Debug 構成の赤を数える**

既知事項 **S-5**(main の Core テストが Debug 構成で 4 件赤 = `WordBoundary.cs:258` の `Debug.Assert`)がある。**このタスクの前後で赤の件数が増えていないこと**を確認するため、先にベースラインを取る:

```bash
dotnet build kxEdit.sln -c Debug
dotnet test tests/kxEdit.Core.Tests -c Debug --no-build
dotnet test tests/kxEdit.App.Tests  -c Debug --no-build
```

失敗件数をメモする(App は 0 のはず)。

**Step 2: 失敗するテストを書く**

```csharp
    [Fact]
    public void DocumentState_Path_RejectsRelativePath_InDebugBuild() =>
        Sta.Run(() =>
        {
            // Issue #48 / 設計書 §3.5: 「State.Path は null か正規化済み絶対パス」の不変条件を
            // I/O 無しで守る網。A-19(相対パスが State.Path に残り保存先が CWD 依存になる)の
            // 再発を Debug ビルドで捕まえる。
            // Release では Debug.Assert が消えるので、このテストも Debug 構成でのみ意味を持つ。
#if DEBUG
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            Assert.ThrowsAny<Exception>(() => doc.State.Path = "memo.txt");
#endif
        });

    [Fact]
    public void DocumentState_Path_AcceptsNullAndAbsolute() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.State.Path = @"C:\Temp\a.txt"; // 絶対パスは通る
            doc.State.Path = null; // null も通る(無題タブ)
            Assert.Null(doc.State.Path);
        });
```

> **実装者への注意**: xUnit のプロセスで `Debug.Assert` が既定でどう振る舞うか(ダイアログを出す / `TraceListener` が例外を投げる)は環境依存。**まず 1 本走らせて実際の振る舞いを確認し**、ダイアログが出るなら `Trace.Listeners` を差し替える形にテストを組み直すこと。**確認せずに `Assert.ThrowsAny` を書いたまま先へ進まない。** 振る舞いを制御できないと判断したら、このテスト 2 本目(`AcceptsNullAndAbsolute`)だけを残し、1 本目は落として理由をコミットメッセージに書く — アサート自体は網として価値があるので残す。

**Step 3: 実装を書く**

`src/kxEdit.App/DocumentState.cs`:

```csharp
    private string? _path;

    /// <summary>
    /// 未保存なら null。非 null のときは<b>正規化済みの絶対パス</b>
    /// (Issue #48 / 設計書 §3.1 の不変条件)。
    /// <c>DocumentManager.FindByPath</c> と <c>RecentFilesList.Add</c> は、この不変条件に
    /// 依拠して <c>PathKey.ForNormalized</c>(ファイルシステム非依存)で比較する。
    /// ここに未正規化パスが入ると、同一ファイルの重複タブ検知(A-7 (b))がすり抜ける。
    /// </summary>
    public string? Path
    {
        get => _path;
        set
        {
            // I/O を伴わない構造チェック。IsPathFullyQualified は純粋な文字列判定。
            // 相対パス(= A-19 の再発)を Debug ビルドで捕まえる。
            System.Diagnostics.Debug.Assert(
                value is null || System.IO.Path.IsPathFullyQualified(value),
                "State.Path は null か正規化済み絶対パスであること(Issue #48 / 設計書 §3.1)"
            );
            _path = value;
        }
    }
```

**Step 4: テストが通ることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
dotnet build kxEdit.sln -c Debug
dotnet test tests/kxEdit.Core.Tests -c Debug --no-build
dotnet test tests/kxEdit.App.Tests  -c Debug --no-build
```

期待: Release 全緑・0 warning。Debug の失敗件数が **Step 1 のベースラインから増えていない**こと。

**Step 5: commit**

```bash
git add src/kxEdit.App/DocumentState.cs tests/kxEdit.App.Tests/DocumentManagerTests.cs
git commit -m "test(app): State.Path の不変条件を Debug.Assert で担保する(Issue #48 / 設計書 §3.5)

「null か正規化済み絶対パス」を I/O 無しの構造チェックで守る。
FindByPath / RecentFilesList がこの不変条件に依拠して
ファイルシステム非依存の比較をしているため、破れると A-7 (b) の
重複タブ検知がすり抜ける。"
```

---

## Task 8: 回数削減そのものに網を張る

**レビュー区分:** 通常(仕様レビュー)

**Files:**
- Test: `tests/kxEdit.App.Tests/FileControllerTests.cs`

ここまでのタスクは「境界がある」ことを固定した。このタスクは「**回数が減った**」ことを固定する。設計書 §5 のミューテーション項目 2 で先に見つけてある網の穴を埋める。

**Step 1: テストを書く**

```csharp
    [Fact]
    public void SaveDocument_ExistingPath_DoesNotNormalizeAtAll() =>
        Sta.Run(() =>
        {
            // Issue #48 の成果そのもの。Ctrl+S は不変条件(State.Path は正規化済み)により
            // GetFullPath を 1 回も打たなくなる。この網が無いと、将来
            // 「念のため正規化しておく」という一見無害な追加で S-15 が戻る。
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            System.IO.File.WriteAllText(path, "old");
            var doc = host.File.TryOpenOrActivate(path)!;
            doc.Editor.Text = "new";

            int before = host.Probe.NormalizeCallCount; // 開く時の 1 回を除く

            Assert.True(host.File.SaveDocument(doc));

            Assert.Equal(before, host.Probe.NormalizeCallCount);
        });

    [Fact]
    public void SaveDocument_WithManyOpenTabs_DoesNotScaleNormalizeCalls() =>
        Sta.Run(() =>
        {
            // 1+N の N 側が消えたことの網。設計書 §5 のミューテーション項目 2 が
            // 指摘した穴 —「seam の呼び出し回数」だけを見ていると
            // FindByPath を PathKey.For に戻す変異を kill できない(For は seam を通らない)—
            // への対処として、**タブ数を変えても回数が変わらない**ことを固定する。
            using var host = new Host();
            using var tmp = new TempDir();
            for (int i = 0; i < 5; i++)
            {
                string p = tmp.File($"t{i}.txt");
                System.IO.File.WriteAllText(p, "x");
                Assert.NotNull(host.File.TryOpenOrActivate(p));
            }
            string target = tmp.File("t0.txt");
            var doc = host.Docs.FindByPath(target)!;
            doc.Editor.Text = "new";

            int before = host.Probe.NormalizeCallCount;
            Assert.True(host.File.SaveDocument(doc));

            Assert.Equal(before, host.Probe.NormalizeCallCount);
        });
```

**Step 2: テストが通ることを確認する**

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

期待: 全緑。**このタスクのテストは実装変更なしで最初から緑になる**(前タスクまでで達成済みのため)。これは正常。網は「今の状態を固定する」ためのもの。

> **注意**: TDD の「まず赤」を確認したい場合は、`DocumentManager.FindByPath` の `ForNormalized` を一時的に `For` へ戻し、`SaveDocument_WithManyOpenTabs_DoesNotScaleNormalizeCalls` が**赤にならない**ことを実際に見ること。これが設計書 §5 が予告した穴で、`PathKey.For` 自体の呼び出し回数を見ないと kill できない。**この網では kill できないと分かったうえで置いている**ことを、テストのコメントに書いた形で残す。Task 9 のミューテーション検証で、Task 1 の `ForNormalized_does_not_normalize_separators` が実際の kill 役であることを確認する。

**Step 3: commit**

```bash
git add tests/kxEdit.App.Tests/FileControllerTests.cs
git commit -m "test(app): Ctrl+S が正規化を 1 回も打たないことを固定する(Issue #48 / 設計書 §5)

境界があることではなく「回数が減ったこと」の網。タブ数を変えても
正規化回数が変わらないことまで固定し、1+N の N 側が消えたことを示す。"
```

---

## Task 9: 最終ブランチレビュー(2 パス)と品質ゲート

**Files:** なし(レビューと検証)

CLAUDE.md §3 工程 5 / §6。

**Step 1: コード品質パス**

**別エージェント**を 1 つ起動し、ブランチ全体をコード品質の観点でレビューさせる。ミューテーション検証のスポットチェックを含める:

| # | 変異 | 期待 |
|---|------|------|
| 1 | `RunNormalizeProbe` のフェイルセーフを `(Ok, "")` へ | `RunNormalizeProbe_WorkExceedsTimeout_FailsSafeToTimedOut` が赤 |
| 2 | `PathNormalizeStatus` の並びを `Ok = 0` へ | `PathNormalizeResult_default_is_TimedOut` が赤 |
| 3 | `PathKey.ForNormalized` を `For` の別名にする | `ForNormalized_does_not_normalize_separators` が赤 |
| 4 | `FindByPath` の `ForNormalized` を `For` へ戻す | **Task 8 の網では赤にならない**(設計書 §5 が予告した穴)。`FindByPath_DoesNotNormalizeSeparators_CallerMustNormalize` が赤になることを確認する |
| 5 | `NormalizeSavePath` の文言分岐を潰す(両方 Invalid 文言に) | `SaveAs_NormalizeTimesOut_ShowsReachabilityMessage_AndDoesNotSave` が赤 |
| 6 | `TryOpenOrActivate` の正規化を削る | `TryOpenOrActivate_StoresNormalizedAbsolutePath` が赤 |

ミューテーション検証の作法(過去に踏んだ罠):

- **変異後は必ず build し直し、「ビルドに成功しました」まで確認する。** `--no-build` のまま走らせると変異していないバイナリを見て「kill された」と誤読する。
  **これは理屈ではなく本ブランチで 2 度踏んだ実害**(Task 2)。`-warnaserror` 環境ではアナライザが**変異自体をビルドエラーにする**ため、build の失敗を見落とすと `--no-build` が**前の変異のバイナリ**を黙って実行し、当該変異と何の関係もない「それらしい赤」を返す(実際に `失敗: 1 / 合格: 554` という、当てた変異と無関係のテスト名が返った)。
  実際に変異を弾いたアナライザ: `S1144`(未使用フィールド)/ `CA1822`・`S2325`(static にできる)/ `S1125`(不要な bool リテラル)。
  **ビルドが落ちた変異は「生存」でも「kill」でもない。書き換え方を変えて再実施する。**
- **`--filter` で対象を絞りすぎない。** 絞ると「他のテストが実は kill していた」のを見落とし、結論を誤る。
- **fixture が狙った失敗モードを実際に踏んでいるか確かめる。** Task 2 では「40,000 文字の入力で `IOException` フィルタを網羅した」つもりのテストが、実際には `PathTooLongException`(派生型)しか踏んでおらず、`or IOException` → `or PathTooLongException` の変異が全緑で生存した(= 狙った窓が無網のまま)。**期待値が正しくても fixture が狭いと変異は生存する。**
- **変異は必ず復元する。** レビューエージェントが変異を戻さずに返すことが過去にあった。レビュー後に `git status` と `git diff` で作業ツリーが変異前と一致することを自分で確認する。

**Step 2: 脆弱性パス**

**別エージェント**をもう 1 つ起動する(パスごとに独立したエージェント。1 起動に混載しない)。重点:

- 新しい seam(`NormalizePathWithTimeout`)が外部入力(SR ユーザーの直入力・攻撃者制御の `settings.json` / バックアップ JSON 由来のパス)をどう扱うか。
- #47 の V-1 / V-2 の防御が生きているか。
- 新しい文言 2 種に `SanitizeForDisplay.OneLine` が掛かっているか(CSV-L-5)。
- タイムアウト時にバックグラウンドスレッドが leak する受容が、新経路で悪化していないか(復元経路は不達パスの件数だけ leak しうる)。

**Step 3: 指摘への対応**

3 択で明示する: ① fixup commit で修正 / ② PR description に記載して受容 / ③ 理由付き却下。
**元 commit は書き換えず、別 fixup commit で積む。**

**Step 4: 品質ゲート**

```bash
powershell -File tools\pre-merge-check.ps1
```

**EXIT 0** を確認する。

**Step 5: L5 チェックリストを書く**

`docs/plans/2026-08-23-bounded-path-normalization-l5-checklist.md` を作る。項目は設計書 §7 の 1 件:

- 到達不能な共有パスを「名前を付けて保存」に入力 → 5 秒後にタイムアウト文言が NVDA で読み上げられ、ダイアログへ戻ること。

PR #36〜#47 分の L5 と合わせて 1 回で実施する(v0.2 監査 §8 手順 5)。

**Step 6: PR を作る**

```bash
git push -u origin feature/bounded-path-normalization
gh pr create --base main
```

PR description(日本語)に必ず含める:

- 目的と Issue #48 へのリンク(`Closes #48`)。
- **Issue の見立てが狭かったこと**: 発火点は 3 箇所ではなく、`FindByPath` は 1+N、`RecentFilesList.Add` にも同じ 1+N があった(後者は #47 由来ではない既存バグ)。
- **意図的な挙動変更 2 件**(CLAUDE.md §2):
  - `FindByPath` が区切り差・相対セグメントを吸収しなくなる(呼出側が正規化済みパスを渡す契約へ)。
  - `RecentFilesList` のレガシー未正規化エントリーが dedup されなくなる(データ損失なし)。
- **実測値**(21,002 ms / 21,004 ms / 0 ms)と、測定時に SMB 否定キャッシュで誤った結論を出しかけたこと。
- レビュー経緯(前倒し 3 回 + 最終 2 パス)と指摘への 3 択対応。
- 申し送り S-1(A-16)/ S-2(レガシー recents の遅延正規化案)。
- **L5 未実施**であること。

---

## 完了の定義

- `tools/pre-merge-check.ps1` が **EXIT 0**。
- 0 warning(`-warnaserror`)。
- Debug 構成の失敗件数が S-5 のベースラインから増えていない。
- 前倒しレビュー 3 回(Task 2 品質 / Task 3・4 脆弱性)+ 最終 2 パスを実施済み。
- UI スレッドの無境界 `GetFullPath` が `OriginalPathValidator.Check`(= A-16・申し送り S-1)だけになっている。
