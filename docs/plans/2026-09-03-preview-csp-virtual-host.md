# B6「プレビューの CSP と仮想ホスト」実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** プレビュー(WebView2)の仮想ホストと CSP まわりで実態と食い違っている点を全て潰す ——
V-2(未マップ時の実 DNS 解決)/ PR #57 申し送り(無境界 `Directory.Exists`)/ V-3(`%2f` 密輸)/
V-4〜V-6(実在しない防御を謳うコメント)/ M-23(サイズ判定前の全文 string 化)。

**Architecture:** 「マッピングは常に在る」を不変条件にして V-2 と UI スレッドのブロックを
同時に解く(**2026-09-03 改訂**: 実在確認は消せない —— `SetVirtualHostNameToFolderMapping`
自身が内蔵しているため。`Task.Run` 越しの境界付きプローブで UI スレッドから外す。設計書 §13.2)。
判断は純粋ロジック(`PreviewVirtualHostMapping`)へ出して App.Tests で網を張り、WebView2 実体に
残るのは呼び出し 1 行だけにする。V-3 は前置ガードではなく解決結果の事後条件で弾く(V-7 の教訓)。

**Tech Stack:** .NET 9(`net9.0-windows`)/ WinForms / WebView2 1.0.4022.49 / xUnit 2.9.2 /
CSharpier(pre-commit で自動整形)

**設計書:** `docs/plans/2026-09-03-preview-csp-virtual-host-design.md`(§番号は本計画から参照する)

---

## 前提と作業規約

- ブランチ: `feature/preview-csp-virtual-host`(作成済み。設計書 commit `71d63a0` が先頭)
- **ソースの編集は Write / Edit ツールで行う。** Bash の heredoc / `python -c` は Windows で
  BOM 混入・`\0` の実 NUL 化・ラッパ混入を起こす(過去に実害あり)。
- 各タスクは **RED → GREEN → commit**。commit は `--no-verify` を付けない
  (Husky.Net の CSharpier 整形とローカルパス検出を必ず通す)。
- 警告は 0 を維持(`-warnaserror` 稼働中)。
- テストのコメントは日本語。**「何を守る網か」を 1 行で書く**(このリポジトリの慣例)。

### よく使うコマンド

```powershell
# ビルド(警告=エラー)
dotnet build kxEdit.sln -c Release -warnaserror

# プロジェクト単位のテスト
dotnet test tests/kxEdit.Core.Tests -c Release
dotnet test tests/kxEdit.App.Tests  -c Release

# 1 クラスだけ走らせる
dotnet test tests/kxEdit.Core.Tests -c Release --filter "FullyQualifiedName~PreviewUrlResolverTests"

# マージ前ゲート(EXIT 0 を確認する)
powershell -File tools\pre-merge-check.ps1
```

---

## Task 0: スパイク — WebView2 と Uri の実挙動を測る

> **実施済み(2026-09-03・commit `175bfb9`)。結果は設計書 §13.1。**
> **①②は偽・③④は期待どおり**だったため、Task 1 を §13.2 の設計へ改訂した。

**なぜ最初か:** 設計 §4 は「不存在フォルダーでも例外を投げない」「不達 UNC でも呼び出しが
ブロックしない」を前提にしている。**この 2 つが偽なら §4 は作り直し**なので、kxEdit を触る前に測る。

**Files:**
- Create: `<scratchpad>/wv2probe/` — **リポジトリの外に作る**(作業中のリポジトリに
  使い捨てプロジェクトを置くと commit に混ざる)。`<scratchpad>` はセッションの
  スクラッチパッドディレクトリ(システムプロンプトに絶対パスが示されている)。

**Step 1: 使い捨てプローブを作る**

```powershell
cd <scratchpad>
dotnet new winforms -n wv2probe
cd wv2probe
dotnet add package Microsoft.Web.WebView2 --version 1.0.4022.49
```

**Step 2: `Program.cs` を次の内容で置き換える**(Write ツールで書く)

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace wv2probe;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var form = new Form { Width = 700, Height = 300, Text = "wv2probe" };
        var web = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(web);
        var log = new List<string>();

        form.Shown += async (_, _) =>
        {
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(
                    Path.GetTempPath(),
                    "wv2probe-" + Guid.NewGuid().ToString("N")
                )
            );
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;

            // ③ Uri が %2f をエスケープのまま保つか (V-3 のガードの前提)
            var u = new Uri(new Uri("https://kxedit.preview/"), "..%2f..%2fsecret.txt");
            log.Add("AbsoluteUri : " + u.AbsoluteUri);
            log.Add("AbsolutePath: " + u.AbsolutePath);

            // ① 不存在フォルダー
            Probe(core, "probe1.invalid", @"C:\no\such\folder\kxedit-probe", log);
            // ② 不達 UNC (RFC 5737 のドキュメント用 IP。経路が無い)
            Probe(core, "probe2.invalid", @"\\198.51.100.7\share\nosuch", log);
            // ④ MAX_PATH 超 (§4.3 の catch フィルタを決めるため例外型を見る)
            Probe(core, "probe3.invalid", @"C:\" + new string('a', 300), log);

            string logPath = Path.Combine(Path.GetTempPath(), "wv2probe.log");
            File.WriteAllLines(logPath, log, System.Text.Encoding.UTF8);
            MessageBox.Show(string.Join(Environment.NewLine, log), "wv2probe: " + logPath);
            form.Close();
        };

        Application.Run(form);
    }

    private static void Probe(CoreWebView2 core, string host, string folder, List<string> log)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            core.SetVirtualHostNameToFolderMapping(
                host,
                folder,
                CoreWebView2HostResourceAccessKind.Allow
            );
            sw.Stop();
            log.Add($"{host} -> OK ({sw.ElapsedMilliseconds} ms) [{folder}]");
        }
        catch (Exception ex)
        {
            sw.Stop();
            log.Add($"{host} -> {ex.GetType().FullName}: {ex.Message} ({sw.ElapsedMilliseconds} ms)");
        }
    }
}
```

**Step 3: 実行して結果を読む**

```powershell
dotnet run
```

期待(この 4 つが揃えば設計どおり):

| 観測 | 期待 | 外れたときの扱い |
|------|------|-----------------|
| ① 不存在フォルダー | `OK` | 例外なら §4.1 を「存在確認を残す」設計へ差し戻す。**ユーザーへ報告して停止** |
| ② 不達 UNC | `OK` かつ **1000 ms 未満** | 秒単位で待つなら登録を `Task.Run` へ逃がす設計へ変更。**ユーザーへ報告して停止** |
| ③ Uri | `AbsolutePath` が `/..%2f..%2fsecret.txt`(`%2f` 保持) | デコードされるなら Task 2 のガードを再設計(その場合 `..` はルートで打ち切られ安全側なので、ガード自体が不要になる) |
| ④ MAX_PATH 超 | 何らかの例外(型を記録) | 記録した型を Task 1 の catch フィルタに使う |

**Step 4: 設計書へ実測を書き戻す**

`docs/plans/2026-09-03-preview-csp-virtual-host-design.md` の末尾に `## 13. 実施記録` を作り、
`### 13.1 Task 0 スパイクの実測(YYYY-MM-DD)` として **ログの生の行**を貼る。
§3.3 の「未検証」は消さず、「→ §13.1 で実測」と追記する(策定時スナップショットを保つ)。

**Step 5: commit**

```powershell
git add docs/plans/2026-09-03-preview-csp-virtual-host-design.md
git commit -m "docs(plans): B6 Task 0 スパイクの実測を設計書へ追記"
```

**Step 6: プローブを消す**

```powershell
Remove-Item -Recurse -Force <scratchpad>\wv2probe
```

---

## Task 1: V-2 + PR #57 申し送り — マッピングは常に在る(2026-09-03 改訂)

> **改訂の理由:** Task 0 の実測で「存在確認せずに `_baseDir` を渡す」が成立しないと分かった
> (`SetVirtualHostNameToFolderMapping` 自身が実在確認を内蔵し、不存在は
> `DirectoryNotFoundException`・不達 UNC は 21 秒ブロック)。**不変条件「マッピングは常に在る」は
> 維持し、到達手段を非同期の境界付きプローブへ差し替える。** 現行設計は設計書 **§13.2**。

**Files:**
- Create: `src/kxEdit.App/PreviewVirtualHostMapping.cs`
- Create: `tests/kxEdit.App.Tests/PreviewVirtualHostMappingTests.cs`
- Modify: `src/kxEdit.App/PreviewUserDataFolder.cs`(`EnsureEmptyBaseFolder` 追加)
- Modify: `tests/kxEdit.App.Tests/PreviewUserDataFolderTests.cs`(3 本追加)
- Modify: `src/kxEdit.App/MarkdownPreviewForm.cs`(ctor に probe / `InitAsync` の該当箇所 / クラス doc)
- Modify: `src/kxEdit.App/MainForm.cs`(`new MarkdownPreviewForm(...)` に probe を渡す)
- Modify: `src/kxEdit.App/RemoteAwareDirectory.cs`(申し送り節を「回収済み」へ)

**Step 1: 失敗するテストを書く** — `tests/kxEdit.App.Tests/PreviewVirtualHostMappingTests.cs`

```csharp
using System.Collections.Generic;
using System.IO;

namespace kxEdit.App.Tests;

/// <summary>
/// V-2 + PR #57 申し送り: 仮想ホストのマッピング先を決める純粋ロジック。
/// <para>
/// 守る不変条件は<b>「マッピングは常に在る」</b>。未マップの状態を作ると
/// <c>https://kxedit.preview/...</c> が実 DNS 解決へ出る (監査 §9 V-2)。
/// 「baseDir が無い」「実在しないと分かっている」「登録が失敗した」のどれも、
/// 未マップではなく空フォルダーへ倒す。
/// </para>
/// <para>
/// 実在判定そのものは<b>呼び出し側の責務</b>。SetVirtualHostNameToFolderMapping は
/// 内部で実在確認をしており、不達な共有では 21 秒返らない (設計書 §13.1 の実測)。
/// だから「実在が確定したフォルダーだけを渡す」形になっている。
/// </para>
/// </summary>
public class PreviewVirtualHostMappingTests
{
    private const string Fallback = @"C:\fallback\empty-base";
    private const string BaseDir = @"C:\docs";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoBaseDir_MapsFallback(string? baseDir)
    {
        var calls = new List<string>();
        PreviewVirtualHostMapping.Apply(baseDir, baseDirExists: false, () => Fallback, calls.Add);
        Assert.Equal(new[] { Fallback }, calls);
    }

    [Fact]
    public void BaseDirNotUsable_MapsFallbackInsteadOfLeavingUnmapped()
    {
        // 実在しないと分かっているものは渡さない (渡すと例外か 21 秒ブロック)。
        // ただし未マップにもしない = V-2 の状態を作らない。
        var calls = new List<string>();
        PreviewVirtualHostMapping.Apply(BaseDir, baseDirExists: false, () => Fallback, calls.Add);
        Assert.Equal(new[] { Fallback }, calls);
    }

    [Fact]
    public void UsableBaseDir_IsMapped()
    {
        var calls = new List<string>();
        PreviewVirtualHostMapping.Apply(BaseDir, baseDirExists: true, () => Fallback, calls.Add);
        Assert.Equal(new[] { BaseDir }, calls);
    }

    [Fact]
    public void UsableBaseDir_DoesNotTouchFallback()
    {
        // フォールバック用フォルダーは実ディレクトリを作る副作用を持つので、
        // 要らないときは呼ばれないことを固定する。
        int fallbackCalls = 0;
        PreviewVirtualHostMapping.Apply(
            BaseDir,
            baseDirExists: true,
            () =>
            {
                fallbackCalls++;
                return Fallback;
            },
            _ => { }
        );
        Assert.Equal(0, fallbackCalls);
    }

    [Theory]
    // 実測した型 (設計書 §13.1)。確認と登録の間に共有が落ちる競合で出る。
    [InlineData(typeof(DirectoryNotFoundException))]
    // 未実測の想定: アクセス拒否。プレビュー自体を失敗させるより空フォルダーへ倒す。
    [InlineData(typeof(UnauthorizedAccessException))]
    public void MapFailure_FallsBackInsteadOfLeavingUnmapped(Type exceptionType)
    {
        var calls = new List<string>();
        PreviewVirtualHostMapping.Apply(
            BaseDir,
            baseDirExists: true,
            () => Fallback,
            folder =>
            {
                calls.Add(folder);
                if (calls.Count == 1)
                    throw (Exception)Activator.CreateInstance(exceptionType)!;
            }
        );
        Assert.Equal(new[] { BaseDir, Fallback }, calls);
    }

    [Fact]
    public void FallbackFailure_Propagates()
    {
        // 2 回とも失敗したら握り潰さない (呼び出し側の catch がプレビュー失敗を出す)。
        var calls = new List<string>();
        Assert.Throws<DirectoryNotFoundException>(() =>
            PreviewVirtualHostMapping.Apply(
                BaseDir,
                baseDirExists: true,
                () => Fallback,
                folder =>
                {
                    calls.Add(folder);
                    throw new DirectoryNotFoundException("boom");
                }
            )
        );
        Assert.Equal(new[] { BaseDir, Fallback }, calls);
    }

    [Fact]
    public void UnexpectedException_IsNotSwallowed()
    {
        // 想定外の例外型までフォールバックへ倒すと、原因不明の「画像が出ない」に化ける。
        Assert.Throws<InvalidOperationException>(() =>
            PreviewVirtualHostMapping.Apply(
                BaseDir,
                baseDirExists: true,
                () => Fallback,
                _ => throw new InvalidOperationException("boom")
            )
        );
    }
}
```

**Step 2: 失敗を確認する**

```powershell
dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~PreviewVirtualHostMappingTests"
```

期待: **ビルドエラー**(`PreviewVirtualHostMapping` が存在しない)。
※ ビルドが割れているとテストは 1 本も走らない。**「テストが落ちた」と「ビルドが割れた」を
混同しない**(過去に 5 回踏んでいる)。

**Step 3: 実装する** — `src/kxEdit.App/PreviewVirtualHostMapping.cs`

```csharp
using System.IO;

namespace kxEdit.App;

/// <summary>
/// V-2 + PR #57 申し送り: プレビューの仮想ホストマッピング先を決める純粋ロジック。
/// <para>
/// <b>不変条件: マッピングは常に在る。</b> 未マップのまま文書を出すと、本文中の相対 URL は
/// <see cref="Core.Text.MarkdownRenderer.PreviewBaseHref"/> 基準で絶対化済みなので
/// <c>https://kxedit.preview/...</c> が<b>実 DNS 解決</b>へ出る (監査 §9 V-2)。
/// WebView2 のドキュメントは仮想ホストについて "There is no DNS resolution for host name" と
/// 明記しており、<b>マッピングさえ張れば DNS は起きない</b>。
/// </para>
/// <para>
/// <b>実在判定は呼び出し側の責務。</b> <c>SetVirtualHostNameToFolderMapping</c> は内部で
/// 実在確認をしており、不存在なら <see cref="DirectoryNotFoundException"/>、不達な UNC では
/// <b>21 秒返らない</b> (設計書 §13.1 の実測)。しかも <c>CoreWebView2</c> は UI スレッド専有で
/// 登録を背景スレッドへ逃がせない。だから呼び出し側 (<see cref="MarkdownPreviewForm"/>) が
/// 境界付きプローブで実在を確定し、<b>確定した結果だけ</b>を
/// <paramref name="baseDirExists"/> で渡す。
/// </para>
/// </summary>
internal static class PreviewVirtualHostMapping
{
    /// <summary>
    /// マッピング先を決めて <paramref name="map"/> へ渡す。<paramref name="map"/> が
    /// I/O 系の例外で失敗しても<b>未マップにはしない</b>。
    /// </summary>
    /// <param name="baseDir">.md のフォルダー。未保存タブでは null。</param>
    /// <param name="baseDirExists">
    /// 呼び出し側が境界付きで確定した実在フラグ。false なら <paramref name="baseDir"/> は使わない。
    /// </param>
    /// <param name="emptyFallback">マッピング専用の空フォルダーを作って返す (必要時のみ呼ぶ)。</param>
    /// <param name="map">
    /// <c>SetVirtualHostNameToFolderMapping(PreviewVirtualHost, folder, Allow)</c> の薄いラッパ。
    /// デリゲートにしてあるのは WebView2 実体なしでテストするため。
    /// </param>
    internal static void Apply(
        string? baseDir,
        bool baseDirExists,
        Func<string> emptyFallback,
        Action<string> map
    )
    {
        ArgumentNullException.ThrowIfNull(emptyFallback);
        ArgumentNullException.ThrowIfNull(map);

        if (string.IsNullOrEmpty(baseDir) || !baseDirExists)
        {
            map(emptyFallback());
            return;
        }

        try
        {
            map(baseDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 未マップへ戻さない。ここで諦めると V-2 の状態が復活する。
            // 実在確認と登録の間に共有が落ちる競合が主な経路 (DirectoryNotFoundException)。
            System.Diagnostics.Trace.TraceWarning(
                $"プレビュー仮想ホストのマッピングに失敗したので空フォルダーへ倒す: {ex.Message} ({baseDir})"
            );
            map(emptyFallback()); // ここが失敗したら呼び出し側へ送る (握り潰さない)
        }
    }
}
```

**Step 4: 緑を確認する**

```powershell
dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~PreviewVirtualHostMappingTests"
```

期待: PASS(9 ケース)。

**Step 5: `EnsureEmptyBaseFolder` の失敗するテストを書く**

`tests/kxEdit.App.Tests/PreviewUserDataFolderTests.cs` の末尾(`SafeCleanup` の直前)に追加:

```csharp
    [Fact]
    public void EnsureEmptyBaseFolder_CreatesEmptyDirectoryUnderPath()
    {
        // V-2: baseDir が使えないときのマッピング先。空であることが契約 (ここに何か置くと
        // プレビューへ露出する)。
        var sut = new PreviewUserDataFolder();
        try
        {
            string empty = sut.EnsureEmptyBaseFolder();
            Assert.True(System.IO.Directory.Exists(empty));
            Assert.Empty(System.IO.Directory.GetFileSystemEntries(empty));
            Assert.StartsWith(sut.Path, empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SafeCleanup(sut);
        }
    }

    [Fact]
    public void EnsureEmptyBaseFolder_Idempotent()
    {
        var sut = new PreviewUserDataFolder();
        try
        {
            string first = sut.EnsureEmptyBaseFolder();
            string second = sut.EnsureEmptyBaseFolder(); // 2 回目でも throw しない
            Assert.Equal(first, second);
            Assert.True(System.IO.Directory.Exists(second));
        }
        finally
        {
            SafeCleanup(sut);
        }
    }

    [Fact]
    public void Dispose_RemovesEmptyBaseFolder()
    {
        // 後始末の経路を増やさない設計 (親を消せば一緒に消える) の網。
        var sut = new PreviewUserDataFolder();
        string empty = sut.EnsureEmptyBaseFolder();
        try
        {
            sut.Dispose();
            Assert.False(System.IO.Directory.Exists(empty));
        }
        finally
        {
            if (System.IO.Directory.Exists(sut.Path))
                System.IO.Directory.Delete(sut.Path, recursive: true);
        }
    }
```

**Step 6: 失敗を確認 → 実装 → 緑を確認**

```powershell
dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~PreviewUserDataFolderTests"
```

`src/kxEdit.App/PreviewUserDataFolder.cs` の `Path` プロパティの直後に追加:

```csharp
    /// <summary>
    /// V-2: 仮想ホストのマッピング先にする空フォルダー(<c>{Path}\empty-base</c>)を作って返す。
    /// <para>
    /// <b>契約: このフォルダーには何も置かない。</b> マッピング専用であり、ここに置いた
    /// ファイルは <c>https://kxedit.preview/</c> でプレビューから読める。
    /// WebView2 のプロファイル実体は <see cref="Path"/> 直下に作られるが、マッピングは
    /// このサブフォルダーに閉じるためプロファイルは露出しない。
    /// </para>
    /// <para>後始末は <see cref="Dispose"/> が親ごと消すので専用の経路を持たない。</para>
    /// </summary>
    public string EnsureEmptyBaseFolder()
    {
        string path = System.IO.Path.Combine(Path, "empty-base");
        System.IO.Directory.CreateDirectory(path); // idempotent: 既存でも throw しない
        return path;
    }
```

**Step 7: `MarkdownPreviewForm` を書き換える**

(1) ctor に `IReachabilityProbe` を足す(フィールド `_probe` を持つ)。
`FileController` と同じ流儀で、生成側が実装を選ぶ。

```csharp
    private readonly string? _baseDir;
    private readonly IReachabilityProbe _probe;

    public MarkdownPreviewForm(string html, string? baseDir, string fileName, IReachabilityProbe probe)
    {
        _html = html;
        _baseDir = baseDir;
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        // 以下は既存のまま
```

(2) `InitAsync` の `Directory.Exists` ブロック(現行 `:88-96`)を置き換える:

```csharp
            // V-2 + PR #57 申し送り(設計書 §13.2): マッピングは常に張る。未マップのままだと
            // 本文中の相対 URL(描画前に絶対化済み)が実 DNS 解決へ出る(監査 §9 V-2)。
            //
            // 実在確認は UI スレッドから外す: SetVirtualHostNameToFolderMapping 自身が
            // 実在確認を内蔵しており、不達な共有では 21 秒返らない(§13.1 の実測)。
            // CoreWebView2 は UI スレッド専有なので登録自体は逃がせない。したがって
            // 「実在が確定したフォルダーだけを UI スレッドで渡す」形にする。
            // RemoteAwareDirectory はローカルを Directory.Exists 直呼び、リモートのみ
            // 5 秒の境界付きプローブへ回す(grep と同じ契約)。
            bool baseDirExists =
                !string.IsNullOrEmpty(_baseDir)
                && await Task.Run(() => RemoteAwareDirectory.Exists(_probe, _baseDir!));
            if (IsDisposed || Disposing)
                return;

            PreviewVirtualHostMapping.Apply(
                _baseDir,
                baseDirExists,
                _userData.EnsureEmptyBaseFolder,
                folder =>
                    core.SetVirtualHostNameToFolderMapping(
                        MarkdownRenderer.PreviewVirtualHost,
                        folder,
                        CoreWebView2HostResourceAccessKind.Allow
                    )
            );
```

(3) クラス doc(`:9-10`)の「baseDir が null（未保存タブ等）の場合は仮想ホストを設定せず、
相対リソースは解決できない。」を訂正:

```csharp
/// baseDir が null（未保存タブ等）または実在しない/不達の場合は空フォルダーへマッピングする
/// （相対リソースは解決できないが、仮想ホストは常にローカルで応答する = 実 DNS 解決を
/// 起こさない・V-2）。
```

**Step 8: `MainForm` の生成側を直す**

`src/kxEdit.App/MainForm.cs` の `ShowMarkdownPreview` 内:

```csharp
        using var f = new MarkdownPreviewForm(
            html,
            dir,
            doc.State.DisplayName,
            new FileReachabilityProbe()
        );
```

**Step 9: `RemoteAwareDirectory` の申し送りを回収済みにする**

`src/kxEdit.App/RemoteAwareDirectory.cs` の XML doc のうち、`MarkdownPreviewForm.InitAsync` を
「同じバグクラスの未修正箇所」と書いている段落と「プレビュー側を『ついでに』境界付きに
してはいけない」の段落を、次の 1 段落へ差し替える:

```csharp
/// <para><b>grep 専用ではなくなった</b>(2026-09-03・B6)。
/// <c>MarkdownPreviewForm.InitAsync</c> も本クラスを使う —— ただし
/// <b>UI スレッドから直接ではなく <c>Task.Run</c> 越しに await する</b>形で、
/// UI スレッドはブロックしない(grep は同期呼び出しのまま)。
/// かつての申し送り「プレビュー側を『ついでに』境界付きにしてはいけない
/// (到達不能を『フォルダーが無い』に畳むと未マップになり監査 §9 V-2 を踏む)」は、
/// <b>プレビュー側が空フォルダーへ倒すフェイルセーフを持ったことで解消した</b>
/// (<c>docs/plans/2026-09-03-preview-csp-virtual-host-design.md</c> §13.2)。
/// 警告の本旨は「境界付きにするな」ではなく「フェイルセーフとセットでなければするな」だった。</para>
```

**Step 10: ビルドと全テスト**

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

期待: 警告 0・全緑。`MainFormSmokeTests` など既存テストが緑のままであること。

**Step 11: 手動スモーク(この段階でしか見えないもの)**

```powershell
dotnet run --project src\kxEdit.App -c Debug
```

1. 新規タブ(未保存)に `![](pic.png)` と書いて **表示 → マークダウンプレビュー** →
   窓が開き、画像は「読み込めない」表示。**エラーダイアログが出ないこと**。
2. 保存済みの .md を開いてプレビュー → 同フォルダーの画像が**従来どおり表示される**。
3. 適当な .md を開いた状態で、その親フォルダーを別プロセスでリネーム(= `_baseDir` を
   不存在にする)→ プレビュー → **エラーにならず窓が開き、画像だけが出ない**こと。

**Step 12: commit**

```powershell
git add src/kxEdit.App tests/kxEdit.App.Tests
git commit -m "fix(preview): 仮想ホストのマッピングを常に張り実在確認を UI スレッドから外す(V-2 / PR #57 申し送り)"
```

**Step 13: 脆弱性レビュー(前倒し・傘設計書 §8 の指定)**

**別エージェント**を起動し、次を渡す: 設計書 §4 + §13.1 + §13.2 / 本タスクの差分 /
監査 §9 の V-2 と V-7。観点は「未マップの状態が作れる経路が残っていないか」
「空フォルダーに何かが置かれうるか」「例外フィルタから漏れる型で未マップになる経路」
「`Task.Run` 越しの await 中にフォームが閉じられたときの再入」。
指摘は §4 の 3 択(fixup / PR で受容 / 理由付き却下)で処理する。

---

## Task 2: V-3 — 密輸されたエスケープ区切りを全リンクの事後条件で潰す(2026-09-03 改訂)

> **改訂の理由:** Task 1 の脆弱性レビューで、旧 Task 2 のガード(`PreviewUrlResolver.TryResolve` の
> 事後条件)が**絶対 URL 形を一度も見ない**ことが実測で判明した。`TryResolve` は絶対 URL に
> 対して `false` を返す(`PreviewUrlResolver.cs:44`)ため、`https://kxedit.preview/` を前置する
> だけで迂回できる。しかも画像は `SafeLinkExtension` も通らない(`SafeLinkExtension.cs:82`)。
> **現行設計は設計書 §14。**

**Files:**
- Modify: `src/kxEdit.Core/Text/PreviewUrlResolver.cs`(`NeutralizeEncodedSeparators` を追加)
- Modify: `src/kxEdit.Core/Text/PreviewRelativeUrlExtension.cs`(全 `LinkInline` へ適用)
- Modify: `tests/kxEdit.Core.Tests/Text/PreviewUrlResolverTests.cs`
- Modify: `tests/kxEdit.Core.Tests/Text/MarkdownRendererTests.cs`(**出力まで見る網**)

**Step 1: 失敗するテストを書く(まず `Render` の出力を見る網から)**

`MarkdownRendererTests` に追加。**単体の関数だけ見ると §14.1 の見落としを繰り返す**ので、
end-to-end の出力を先に固定する:

```csharp
    [Theory]
    // V-3 (監査 §9): 区切り文字をエスケープで密輸する形。相対・絶対の両方を潰す。
    // 絶対形は TryResolve が触らない経路なので、ガードを resolver 側に置くと素通りする
    // (設計書 §14.1 の実測)。ここは Render の出力で固定する。
    [InlineData("![x](..%2f..%2fsecret.txt)")]
    [InlineData("![x](https://kxedit.preview/..%2f..%2fsecret.txt)")]
    [InlineData("![x](https://kxedit.preview/..%2F..%2FEBWebView/Default/Preferences)")]
    [InlineData("[a](https://kxedit.preview/..%5c..%5cx)")]
    [InlineData("[a](..%5C..%5Cx)")]
    public void Preview_EncodedSeparators_NeverReachOutput(string markdown)
    {
        string html = MarkdownRenderer.Render(markdown, Base);
        Assert.DoesNotContain("%2f", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%5c", html, StringComparison.OrdinalIgnoreCase);
        // 無害化の形も固定する (URL を空にはしない = <img src=""> の解決はブラウザ依存)。
        Assert.Contains("%25", html, StringComparison.Ordinal);
    }

    [Theory]
    // 非退化の対照: 他の percent-escape と通常の相対パスは従来どおり絶対化される。
    [InlineData("![x](my%20file.png)", "https://kxedit.preview/my%20file.png")]
    [InlineData("![x](sub/dir/pic.png)", "https://kxedit.preview/sub/dir/pic.png")]
    // 外部 URL は我々のマッピングではないので触らない。
    [InlineData("[a](https://example.com/a%2fb)", "https://example.com/a%2fb")]
    public void Preview_OtherUrls_AreUntouched(string markdown, string expectedUrl)
    {
        Assert.Contains(expectedUrl, MarkdownRenderer.Render(markdown, Base), StringComparison.Ordinal);
    }
```

`PreviewUrlResolverTests` には `NeutralizeEncodedSeparators` 自体の網を足す:

```csharp
    [Theory]
    // preview origin のパスに残った %2f / %5c は % 自身をエスケープして無害化する
    // (区切り文字を含まない 1 つのファイル名への要求になり、マッピング先で 404 で終わる)。
    [InlineData("https://kxedit.preview/..%2f..%2fx", "https://kxedit.preview/..%252f..%252fx")]
    [InlineData("https://kxedit.preview/..%2F..%2Fx", "https://kxedit.preview/..%252F..%252Fx")] // 大小保存
    [InlineData("https://kxedit.preview/a%5cb", "https://kxedit.preview/a%255cb")]
    // 対象外はそのまま返す (退化していないことの対照)
    [InlineData("https://kxedit.preview/my%20file.png", "https://kxedit.preview/my%20file.png")]
    [InlineData("https://example.com/a%2fb", "https://example.com/a%2fb")] // 外部 origin
    [InlineData("#anchor", "#anchor")]
    [InlineData("", "")]
    public void NeutralizeEncodedSeparators_Cases(string input, string expected) =>
        Assert.Equal(expected, PreviewUrlResolver.NeutralizeEncodedSeparators(input));
```

**Step 2: 失敗を確認する**

```powershell
dotnet test tests/kxEdit.Core.Tests -c Release --filter "FullyQualifiedName~PreviewUrlResolverTests|FullyQualifiedName~MarkdownRendererTests"
```

期待: `NeutralizeEncodedSeparators` が無いのでビルドエラー → 実装後、`Preview_EncodedSeparators_NeverReachOutput` の**絶対形 3 ケースが赤**になること(相対形は旧設計でも塞げるので、赤になるのは絶対形。**これが今回の本命**)。
※ ビルド割れとテスト失敗を混同しない。exit code と件数で判定する。

**Step 3: 実装する**

`src/kxEdit.Core/Text/PreviewUrlResolver.cs` に追加:

```csharp
    /// <summary>
    /// V-3 (監査 §9): preview 仮想ホストを指す URL のパスに残った <c>%2f</c> / <c>%5c</c> を
    /// 無害化する。<c>%</c> 自身をエスケープして <c>%252f</c> にするので、要求は
    /// 「区切り文字を含まない 1 つのファイル名」になりマッピング先で 404 で終わる。
    /// <para>
    /// <b>置き場所が要点。</b> <see cref="TryResolve"/> は絶対 URL に触らない
    /// (scheme 付きは早期 return する) ため、そちらの事後条件に置くと
    /// <c>![x](https://kxedit.preview/..%2f..%2fsecret.txt)</c> が素通りする (設計書 §14.1 の実測)。
    /// 本メソッドは <see cref="PreviewRelativeUrlExtension"/> が<b>全 LinkInline</b> に対して
    /// 呼ぶことで、相対・絶対の両方を 1 か所で覆う。
    /// </para>
    /// <para>
    /// 対象は preview ホストを指す URL だけ。外部 URL のパス解釈には口を出さない
    /// (仮想ホストのマッピングは我々のものだけなので、他所を書き換える理由が無い)。
    /// URL を空にする案は採らない: <c>&lt;img src=""&gt;</c> の解決は data: 文書に対して曖昧で、
    /// ブラウザ依存の要求が飛びうるため。
    /// </para>
    /// </summary>
    internal static string? NeutralizeEncodedSeparators(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url;
        }
        if (
            !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            || !string.Equals(parsed.Host, PreviewBase.Host, StringComparison.OrdinalIgnoreCase)
        )
        {
            return url;
        }
        if (
            !parsed.AbsolutePath.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            && !parsed.AbsolutePath.Contains("%5c", StringComparison.OrdinalIgnoreCase)
        )
        {
            return url;
        }
        // 大小は保存する (%2F → %252F)。%20 など他の escape は触らない。
        return EncodedSeparator.Replace(url, m => "%25" + m.Value[1..]);
    }

    private static readonly System.Text.RegularExpressions.Regex EncodedSeparator =
        new("%(2[fF]|5[cC])", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
```

> 実装メモ: ホスト判定は `http` / `https` の両方を通す(`Uri.Host` のみを見る)。F-7 で
> `http://kxedit.preview/...` は Block になるが、無害化はしておいて損がない。

`src/kxEdit.Core/Text/PreviewRelativeUrlExtension.cs` の `OnDocumentProcessed`:

```csharp
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (PreviewUrlResolver.TryResolve(link.Url, out string? absolute))
            {
                link.Url = absolute;
            }
            // V-3: 相対・絶対の両方をここで覆う。TryResolve は絶対 URL に触らないので、
            // 事後条件を resolver 側だけに置くと絶対 URL 形が素通りする (設計書 §14.1 の実測)。
            link.Url = PreviewUrlResolver.NeutralizeEncodedSeparators(link.Url);
        }
```

**Step 4: 緑を確認する**

```powershell
dotnet test tests/kxEdit.Core.Tests -c Release
```

期待: 全 PASS(既存 1431 + 新規)。既存の `Relative_IsResolved` / `NotRewritten` が緑のままであること。

**Step 5: ミューテーション検証(スポット・3 条件を 1 つずつ)**

1. `%2f` の条件を外す → `..%2f` 系のケースが赤
2. `%5c` の条件を外す → `..%5c` 系のケースが赤
3. ホスト判定を外す(全 URL を対象にする)→ **外部 URL の対照(`https://example.com/a%2fb`)が赤**
4. `StringComparison.OrdinalIgnoreCase` → `Ordinal` → 大文字ケースが赤

各回、**落ちたテスト名と合格件数**を確認する(exit code が唯一確実。ビルドが割れて 0 件実行になっていないか必ず見る)。

**Step 6: commit**

```powershell
git add src/kxEdit.Core tests/kxEdit.Core.Tests
git commit -m "fix(preview): 密輸されたエスケープ区切りを全リンクの事後条件で無害化(V-3)"
```

**Step 7: 脆弱性レビュー(前倒し)**

別エージェントに「このガードを迂回して `%2f` をマッピングへ届かせる入力があるか」を探させる
(二重エスケープ `%252f` の再デコード・Unicode 正規化・`LinkInline` 以外の経路・
`GetDynamicUrl` 経由・autolink・生 HTML)。

---

## Task 2b: F-7 — `http://kxedit.preview/…` が実 DNS へ出る(2026-09-03 追加)

> **追加の理由:** Task 1 の脆弱性レビューで発見。B6 の射程外だが**同じ不変条件
> (この名前を実 DNS に出さない)の唯一の残存経路**なので本ブランチで直す。設計書 §15。

**Files:**
- Modify: `src/kxEdit.App/PreviewNavigationPolicy.cs:78-90`
- Modify: `tests/kxEdit.App.Tests/PreviewNavigationPolicyTests.cs`(`Classify_HttpPreviewHost_ReturnsLaunchExternal` の期待値と名前)

**Step 1: 既存テストの期待値を変える(RED)**

`Classify_HttpPreviewHost_ReturnsLaunchExternal` を
`Classify_HttpPreviewHost_ReturnsBlock` へ改名し、期待を `Classification.Block` にする。
コメントに「`LaunchExternal` にすると既定ブラウザが `kxedit.preview` を**実 DNS 解決**する
(監査 V-2 と同じ漏れ方)」と書く。

**Step 2: 失敗を確認 → 実装 → 緑**

```csharp
            // http / https のどちらでも preview 仮想ホストは全面 Block。
            // かつてこのコメントは「kxedit.preview は実ホストではないので LaunchExternal しても
            // 無意味」と書いていたが、無意味ではない —— 既定ブラウザが実 DNS 解決を行い、
            // 企業 DNS の search suffix 等で「どの URL を踏ませたか」が外部へ漏れる
            // (監査 §9 V-2 と同じ経路。2026-09-03・F-7)。
            "http" or "https"
                when string.Equals(parsed.Host, PreviewHost, StringComparison.OrdinalIgnoreCase) =>
                Classification.Block,
```

**Step 3: commit**

```powershell
git add src/kxEdit.App tests/kxEdit.App.Tests
git commit -m "fix(preview): http の preview 仮想ホストも Block にする(F-7)"
```

---

## Task 3: V-4 / V-5 / V-6 — CSP の記述を実態に合わせる

**Files:**
- Modify: `src/kxEdit.Core/Text/MarkdownRenderer.cs:60-70`(コメント)/ `:86`(定数)
- Modify: `tests/kxEdit.Core.Tests/Text/MarkdownRendererTests.cs:439`(期待値)
- Modify: `src/kxEdit.App/PreviewCspHeaderInjector.cs:26-29`(XML doc)

**Step 1: 失敗するテストを書く**

`MarkdownRendererTests.PreviewCspHeader_ContainsAllDirectives` の該当行を差し替える:

```csharp
        // V-4: data: 文書の origin は opaque なので 'self' は何にもマッチしない。
        // <link> を実際に通しているのは https://kxedit.preview の方なので 'self' は置かない。
        Assert.Contains("style-src https://kxedit.preview", csp);
        Assert.DoesNotContain("'self'", csp);
```

**Step 2: 失敗を確認する**

```powershell
dotnet test tests/kxEdit.Core.Tests -c Release --filter "FullyQualifiedName~MarkdownRendererTests"
```

期待: `PreviewCspHeader_ContainsAllDirectives` が FAIL。

**Step 3: 定数とコメントを直す**

`src/kxEdit.Core/Text/MarkdownRenderer.cs` の定数(`:86` 付近):

```csharp
        + "style-src https://"
        + PreviewVirtualHost
        + "; "
```

同ファイルの XML doc、`style-src` の `<item>`(`:64-66`)を差し替える:

```csharp
    ///   <item><c>style-src https://kxedit.preview</c>: inline <c>&lt;style&gt;</c> 撤去に伴い
    ///     <c>'unsafe-inline'</c> を削除。<b>V-4 (2026-09-03): <c>'self'</c> も削除した</b> ——
    ///     プレビュー文書の origin は <c>data:text/html</c> = opaque なので <c>'self'</c> は
    ///     何にもマッチせず、防御として機能していなかった (監査 §9 V-4)。実際に
    ///     <c>&lt;link&gt;</c> を通しているのは <c>https://kxedit.preview</c> の方である。</item>
```

`frame-ancestors` について新しい `<item>` を同じリストへ足す(V-5):

```csharp
    ///   <item><b>V-5 (2026-09-03): <c>frame-ancestors</c> は meta 配信では仕様上無視される</b>
    ///     (HTTP header 側でのみ有効)。プレビュー文書は data: 起点でヘッダを注入できないため、
    ///     <b>現在この directive が効く経路は無い</b> (監査 §9 V-5)。
    ///     <see cref="MarkdownPreviewForm"/> は iframe に置かれないので実害は無く、将来
    ///     ヘッダ経路で文書を配信するときのために定数へは残すが、<b>「多層防御が在る」とは
    ///     読まないこと</b>。</item>
```

> `<see cref="MarkdownPreviewForm"/>` は Core からは参照できない。**`<c>MarkdownPreviewForm</c>`
> と書く**(cref は解決できないと警告=エラーになる)。

**Step 4: V-6 のコメントを直す** — `src/kxEdit.App/PreviewCspHeaderInjector.cs:26-29`

```csharp
///   <item>本 Injector が付ける HTTP header CSP: <b>強制されない</b>。CSP はドキュメントと
///     ワーカーにのみ適用され、CSS レスポンスに付けたヘッダをブラウザは評価しない
///     (監査 §9 V-6)。<c>@import</c> / <c>url(...)</c> を実際に縛っているのは<b>文書側</b>の
///     <c>style-src</c> / <c>img-src</c> / <c>font-src</c> である。ヘッダの送出自体は
///     <see cref="MarkdownRenderer.PreviewCspHeader"/> の single source of truth 共有として
///     残すが、<b>防御層として数えない</b>。</item>
```

**Step 5: 緑を確認する**

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests  -c Release --no-build
```

期待: 警告 0・全緑。`Meta_And_HttpHeader_Use_SameCspString` が緑のままであること
(meta と header が同一定数を参照している契約は変えていない)。

**Step 6: commit**

```powershell
git add src/kxEdit.Core src/kxEdit.App tests/kxEdit.Core.Tests
git commit -m "fix(preview): CSP の実態に合わせて style-src の 'self' を削除しコメントを訂正(V-4/V-5/V-6)"
```

---

## Task 4: M-23 — サイズは材料化の前に測る

**Files:**
- Modify: `src/kxEdit.Core/Text/MarkdownRenderer.cs`(述語と文言を追加・`Render` から使う)
- Modify: `tests/kxEdit.Core.Tests/Text/MarkdownRendererTests.cs`
- Modify: `src/kxEdit.App/MainForm.cs:1678-1707`

**Step 1: 失敗するテストを書く**

`MarkdownRendererTests` の既存 cap テスト群(`:289-320` 付近)の後ろに追加:

```csharp
    [Theory]
    [InlineData(0, false)]
    [InlineData(MarkdownRenderer.MaxMarkdownChars - 1, false)]
    [InlineData(MarkdownRenderer.MaxMarkdownChars, false)] // 境界ちょうどは通す
    [InlineData(MarkdownRenderer.MaxMarkdownChars + 1, true)]
    public void ExceedsMaxChars_UsesSameBoundaryAsRenderCap(int charCount, bool expected)
    {
        // M-23: caller が全文 string 化の前に判定するための述語。Render 内の cap と
        // 不等号がずれると「事前判定は通ったのに Render が投げる」二重基準になる。
        Assert.Equal(expected, MarkdownRenderer.ExceedsMaxChars(charCount));
    }

    [Fact]
    public void Render_TooLargeMessage_ComesFromTooLargeDetail()
    {
        // M-23: 事前判定した caller のダイアログと Render の例外で文面を一致させる。
        var md = new string('a', MarkdownRenderer.MaxMarkdownChars + 1);
        var ex = Assert.Throws<DocumentTooLargeException>(() => MarkdownRenderer.Render(md, ""));
        Assert.Equal(
            MarkdownRenderer.TooLargeDetail(MarkdownRenderer.MaxMarkdownChars + 1),
            ex.Message
        );
    }
```

**Step 2: 失敗を確認する**

```powershell
dotnet test tests/kxEdit.Core.Tests -c Release --filter "FullyQualifiedName~MarkdownRendererTests"
```

期待: ビルドエラー(`ExceedsMaxChars` / `TooLargeDetail` が無い)。

**Step 3: Core を実装する**

`src/kxEdit.Core/Text/MarkdownRenderer.cs` の `MaxMarkdownChars` 定数の直後に追加:

```csharp
    /// <summary>
    /// M-23: <paramref name="charCount"/> 文字の本文が <see cref="MaxMarkdownChars"/> を
    /// 超えるか。<b>全文を string 化する前に</b> caller が判定できるようにするための述語で、
    /// <see cref="Render"/> 内の cap と同じ不等号を使う (二重基準を作らない)。
    /// </summary>
    public static bool ExceedsMaxChars(int charCount) => charCount > MaxMarkdownChars;

    /// <summary>
    /// M-23: 上限超過をユーザーへ伝える詳細文言。<see cref="Render"/> が投げる
    /// <see cref="DocumentTooLargeException"/> と、事前判定した caller のダイアログで
    /// <b>同じ文面</b>を使うための single source of truth。
    /// </summary>
    public static string TooLargeDetail(int charCount) =>
        $"マークダウン本文が上限を超えました({charCount:N0}/{MaxMarkdownChars:N0} 文字)";
```

`Render` 内の cap(`:185` 付近)を書き換える:

```csharp
        if (markdown != null && ExceedsMaxChars(markdown.Length))
        {
            long attemptedBytes = (long)markdown.Length * 2L;
            throw new DocumentTooLargeException(attemptedBytes, TooLargeDetail(markdown.Length));
        }
```

**Step 4: 緑を確認する**

```powershell
dotnet test tests/kxEdit.Core.Tests -c Release --filter "FullyQualifiedName~MarkdownRendererTests"
```

期待: 全 PASS(既存の `Render_DocumentTooLargeException_ReportsAttemptedBytes` も緑のまま)。

**Step 5: App 側で材料化の前に弾く**

`src/kxEdit.App/MainForm.cs` の `ShowMarkdownPreview` を次の形にする:

```csharp
    private void ShowMarkdownPreview()
    {
        var doc = _docs.Active;
        if (doc is null)
            return;

        // M-23: cap 超過は SnapshotText を呼ぶ前に弾く。全文 string 化してから Render 内で
        // 判定すると、1G 文字級の文書では string 化そのものが OutOfMemoryException になり
        // 未捕捉で落ちる。TextLength は材料化せずに文字数を返す。
        if (MarkdownRenderer.ExceedsMaxChars(doc.Editor.TextLength))
        {
            ShowPreviewTooLarge(MarkdownRenderer.TooLargeDetail(doc.Editor.TextLength));
            return;
        }

        string markdown = doc.Editor.SnapshotText; // 編集中バッファ（未保存も反映）
        string? dir = System.IO.Path.GetDirectoryName(doc.State.Path);
        string html;
        try
        {
            html = MarkdownRenderer.Render(markdown, MarkdownRenderer.PreviewBaseHref);
        }
        catch (DocumentTooLargeException ex)
        {
            // 上の事前判定と同じ壁の二重化。Render は将来 caller が増えうるので残す。
            ShowPreviewTooLarge(ex.Message);
            return;
        }

        using var f = new MarkdownPreviewForm(html, dir, doc.State.DisplayName);
        f.ShowDialog(this);
        _docs.Active?.FocusTarget.Focus(); // 戻り後は編集領域へフォーカス
    }

    /// <summary>
    /// MD-L-3 / M-23: 上限超過でプレビューを開けないことを伝える。事前判定と
    /// <see cref="DocumentTooLargeException"/> 経路で<b>同じ文面</b>を出すために切り出す。
    /// MainForm には IUserPrompt が注入されていないため MessageBox.Show を直接使う。
    /// </summary>
    private void ShowPreviewTooLarge(string detail)
    {
        MessageBox.Show(
            this,
            $"プレビューを表示できません。マークダウン本文が大きすぎます。\n\n詳細: {detail}",
            "プレビューを表示できません",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning
        );
        _docs.Active?.FocusTarget.Focus(); // 成功パスと対称: 戻り後は編集領域へフォーカス
    }
```

`ShowMarkdownPreview` の XML doc(`:1672-1677`)の「MD-L-3 L5 検証」の記述はそのまま残し、
最後に 1 行足す: `M-23: cap 判定は TextLength で行い SnapshotText を呼ばない。`

**Step 6: ビルドと全テスト**

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build
dotnet test tests/kxEdit.App.Tests  -c Release --no-build
```

**Step 7: commit**

```powershell
git add src/kxEdit.Core src/kxEdit.App tests/kxEdit.Core.Tests
git commit -m "fix(preview): 上限判定を全文 string 化の前に行う(M-23)"
```

---

## Task 5: 最終ブランチレビュー・ゲート・PR

**Step 1: 最終ブランチレビュー(2 パス・**別エージェントを 2 回独立に起動**)**

CLAUDE.md §3-5。1 起動に混載しない。

- **コード品質パス**: ブランチ全体の差分 + 設計書。ミューテーション検証のスポットチェック込み
  (Task 2 の 2 条件は実施済みなので、その再現手順と結果を渡す)。
- **脆弱性パス**: ブランチ全体の差分 + 監査 §9 + 設計書 §3(根拠の検証状態)。
  「実測と未検証を取り違えていないか」を明示的に見てもらう。

指摘は 3 択(fixup commit / PR description に記載して受容 / 理由付き却下)。
**fixup は元 commit を書き換えず別 commit で積む。**

**Step 2: 設計書へ実施記録を書く**

`docs/plans/2026-09-03-preview-csp-virtual-host-design.md` の `## 13. 実施記録` に追記:

- 13.2 結果(何を直したか・テスト増減)
- 13.3 本設計書と実装計画が含んでいた誤り(**必ず書く**。計画のコードは正解ではない)
- 13.4 却下した指摘とその理由
- 13.5 L5 の実施状況(未実施なら「未実施」と明記する)

**Step 3: 品質ゲート**

```powershell
powershell -File tools\pre-merge-check.ps1
```

**EXIT 0 を確認する**(出力の目視ではなく exit code)。

```powershell
echo $LASTEXITCODE
```

**Step 4: push して PR を作る**

```powershell
git push -u origin feature/preview-csp-virtual-host
gh pr create --title "fix(preview): CSP と仮想ホストの実態不一致を解消(B6)" --body-file <本文>
```

PR description(日本語)に書くこと:

- 目的と射程(V-2 / PR #57 申し送り / V-3 / V-4〜V-6 / M-23)
- **設計判断の要**: 「マッピングは常に在る」で V-2 と 21 秒凍結を同時に解いたこと。
  根拠は WebView2 ドキュメントの "There is no DNS resolution for host name" と Task 0 の実測
- レビュー経緯(前倒し 2 回 + 最終 2 パス)と受容した指摘
- **申し送り**: L5 3 項目が未実施であること(§10)・`Allow` → `DenyCors` の見直し

**Step 5: L5 の依頼**

設計書 §10 の 3 項目 + MD-L-3 の再確認をユーザーへ依頼する。
チェックリストは `docs/plans/2026-09-03-preview-csp-virtual-host-l5-checklist.md` として起こし、
傘設計書 §7.1 の台帳(「B6 の V-3」1 項目)を**3 項目へ更新する**。

---

## 完了の定義

1. Task 0〜4 の commit が積まれ、`tools/pre-merge-check.ps1` が **EXIT 0**
2. 最終 2 パスのレビュー指摘が 3 択で処理済み
3. 設計書 §13 に実施記録(誤りの記録を含む)がある
4. PR 作成済み・L5 チェックリストが起きている
