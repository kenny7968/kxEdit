# 複数インスタンス起動の禁止(単一インスタンス化) 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** kxEdit の 2 つ目の起動を禁止し、無言で終了して既存インスタンスのウィンドウを前面化する。

**Architecture:** `Local\` スコープの名前付き Mutex で「自分が 1 つ目か」を判定し、2 つ目は
名前付きパイプで既存インスタンスへ前面化を依頼してから終了する。Win32 に触る部分を
`WindowActivator` 1 点へ押し込め、それ以外(引数パース・プロトコル・名前生成・トランスポート・
判定)は実ウィンドウなしで自動テストできる形に分解する。

**Tech Stack:** C# / .NET 9 (`net9.0-windows`) / WinForms / `System.IO.Pipes`
(`NamedPipeServerStreamAcl` は追加 PackageReference 不要。2026-09-14 に実機で確認済み) /
xUnit。

**設計書:** [`2026-09-14-single-instance-design.md`](./2026-09-14-single-instance-design.md)
—— 判断の根拠(D1〜D5・★1〜★3・2 つのレース)はすべてそちら。本書は手順のみ。

---

## この計画の前提と共通事項

### コミットの作法

このリポジトリは SSH 署名が有効で、**Git Bash 同梱の `ssh-keygen` では無言ハングする**。
日本語のコミットメッセージは UTF-8 ファイルに書き、必ず次の形で commit すること:

```bash
git -c gpg.ssh.program="C:/Windows/System32/OpenSSH/ssh-keygen.exe" commit -F <msgfile>
```

`--no-gpg-sign` / `--no-verify` でのバイパスは禁止 (CLAUDE.md §6)。
コミットメッセージ末尾には `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>` を入れる。

### テストの実行

```powershell
dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~<TestClassName>"
```

`kxEdit.App.Tests` は `GlobalUsings.cs` で**並列実行が無効化**されている。それでも
Mutex / パイプ名はテストごとに一意にすること(将来並列化が戻ったとき、および
**実運用の kxEdit が同じ PC で動いていても干渉しない**ようにするため)。

### 整形

pre-commit フック (Husky.Net + CSharpier) が staged な `*.cs` を自動整形する。
手動で確認したい場合は `dotnet csharpier check .`。

### 各タスクの終わり

CLAUDE.md §3.4 に従い、**各タスク = 実装 → 仕様レビュー**。指摘を反映してから次タスクへ進む。
Task 4 は追加で**脆弱性レビュー**と**コード品質レビュー**を実施する(前倒しレビューの例外条件に
両方該当するため)。

---

## Task 1: `CommandLineOptions` — 引数パース

**Files:**
- Create: `src/kxEdit.App/CommandLineOptions.cs`
- Test: `tests/kxEdit.App.Tests/CommandLineOptionsTests.cs`

### Step 1: 失敗するテストを書く

`tests/kxEdit.App.Tests/CommandLineOptionsTests.cs`:

```csharp
namespace kxEdit.App.Tests;

/// <summary>
/// 設計 2026-09-14 §3「引数の扱い」: 今回は <c>--new-instance</c> だけを解釈し、
/// 未知の引数は<b>無視する</b>(将来ファイルパスを受けるときに後方非互換にしないため)。
/// </summary>
public class CommandLineOptionsTests
{
    [Fact]
    public void Parse_WithoutArguments_DoesNotRequestNewInstance()
    {
        Assert.False(CommandLineOptions.Parse([]).NewInstance);
    }

    [Fact]
    public void Parse_RecognizesNewInstanceSwitch()
    {
        Assert.True(CommandLineOptions.Parse(["--new-instance"]).NewInstance);
    }

    [Fact]
    public void Parse_MatchesSwitchCaseInsensitively()
    {
        // Windows の CLI 慣習。大文字で打ったユーザーが黙って単一インスタンス化されない。
        Assert.True(CommandLineOptions.Parse(["--New-Instance"]).NewInstance);
    }

    [Theory]
    [InlineData("--new-instances")] // 後方に余分
    [InlineData("-new-instance")] // ハイフン 1 本
    [InlineData("--new")] // 前方一致
    [InlineData("xx--new-instance")] // 部分一致
    public void Parse_DoesNotMatchNearMissSwitches(string arg)
    {
        // StartsWith / Contains / 前方一致への変異を殺す。ここを緩めると、
        // 将来ファイルパスに "--new" を含むものが来たときに排他が黙って外れる。
        Assert.False(CommandLineOptions.Parse([arg]).NewInstance);
    }

    [Fact]
    public void Parse_IgnoresUnknownArguments()
    {
        // 将来のファイルパス引数はここへ来る。例外にしない(設計 §3)。
        var options = CommandLineOptions.Parse([@"C:\work\memo.txt", "--verbose"]);
        Assert.False(options.NewInstance);
    }

    [Fact]
    public void Parse_FindsSwitchAmongUnknownArguments()
    {
        // 「先頭だけ見る」変異を殺す。
        var options = CommandLineOptions.Parse([@"C:\work\memo.txt", "--new-instance"]);
        Assert.True(options.NewInstance);
    }
}
```

### Step 2: テストが失敗することを確認

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~CommandLineOptionsTests"`
Expected: ビルド失敗 (`CommandLineOptions` が存在しない)

### Step 3: 実装

`src/kxEdit.App/CommandLineOptions.cs`:

```csharp
// CommandLineOptions.cs
// 設計 2026-09-14 §3「引数の扱い」。
using System.Diagnostics;

namespace kxEdit.App;

/// <summary>
/// kxEdit のコマンドライン引数。
/// </summary>
/// <remarks>
/// <b>未知の引数は無視する</b>のが本型の要点である。将来 <c>kxEdit.exe file.txt</c> で
/// ファイルを開けるようにする予定があり(設計 §6)、今日「未知の引数はエラー」の形にすると
/// そのとき後方非互換になる。無視した引数は Trace に残して post-mortem の手掛かりにする。
/// </remarks>
internal sealed record CommandLineOptions(bool NewInstance)
{
    /// <summary>
    /// 単一インスタンス制御を回避する開発・検証用スイッチ(設計 D3)。
    /// <b>説明書には載せない</b>——使うと複数インスタンス競合が復活する(設計 §7)。
    /// </summary>
    internal const string NewInstanceSwitch = "--new-instance";

    internal static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        bool newInstance = false;
        foreach (string arg in args)
        {
            // 完全一致で見る。前方一致にすると、将来のファイルパスが偶然前方一致したときに
            // 排他が黙って外れる(テスト Parse_DoesNotMatchNearMissSwitches)。
            if (string.Equals(arg, NewInstanceSwitch, StringComparison.OrdinalIgnoreCase))
            {
                newInstance = true;
                continue;
            }
            Trace.TraceInformation($"kxEdit args: ignored '{arg}'");
        }
        return new CommandLineOptions(newInstance);
    }
}
```

### Step 4: テストが通ることを確認

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~CommandLineOptionsTests"`
Expected: PASS (10 件 = Fact 6 件 + Theory 4 ケース)

> 【実施時の訂正 2026-09-14】策定時は「7 件」と書いていたが、策定時のテストコードの実際の
> 内訳は Fact 5 件 + Theory 4 ケース = 9 件だった(Task 1 実装時に判明)。
> さらに仕様レビュー指摘で `Parse_KeepsNewInstanceWhenUnknownArgumentFollows` を
> 1 本追加したため、最終は 10 件。追加の経緯は下記。

> 【仕様レビュー指摘の回収 2026-09-14・fixup d7d03e1】
> 策定時のテストは `--new-instance` が**最後**に来るケースしか無く、
> `newInstance = string.Equals(...)`(後勝ち = 最後の引数だけが効く)への変異が
> 9 ケースすべてを緑で通過した(実測)。設計 §6 が想定する将来の実起動形は
> `kxEdit.exe --new-instance C:\work\memo.txt` = **スイッチが先**であり、
> その順序が一度もテストされていなかった。CLAUDE.md §4B の
> 「no-change のテストは非既定状態から始める」にも当たるため、次を追加した:
>
> ```csharp
> [Fact]
> public void Parse_KeepsNewInstanceWhenUnknownArgumentFollows()
> {
>     var options = CommandLineOptions.Parse(["--new-instance", @"C:\work\memo.txt"]);
>     Assert.True(options.NewInstance);
> }
> ```
>
> 却下した指摘: `NewInstanceSwitch` の `private` 化(CLI 契約の自己文書化として
> `internal` が妥当)。ただし「テスト側はリテラル直書きを維持する」方針は採用した
> —— 定数を参照すると `Parse_RecognizesNewInstanceSwitch` が同語反復になり、
> near-miss テスト群の価値も同時に落ちるため。**後続タスクでもこの方針を維持すること。**

### Step 5: Commit

```
feat(app): コマンドライン引数パース(--new-instance)を追加
```

---

## Task 2: `SingleInstanceRequest` — 引き渡しプロトコル

**Files:**
- Create: `src/kxEdit.App/SingleInstanceRequest.cs`
- Test: `tests/kxEdit.App.Tests/SingleInstanceRequestTests.cs`

### Step 1: 失敗するテストを書く

```csharp
namespace kxEdit.App.Tests;

/// <summary>
/// 引き渡しメッセージの直列化とパース(設計 2026-09-14 §4)。
/// 今日のペイロードは空だが、将来ファイルパスが乗る器としてプロトコルタグを持つ。
/// <b>パースは厳格側に倒す</b>: 受信側は他プロセスからの入力を読む場所であり、
/// 緩めると将来ファイルパスを載せたときにそのまま攻撃面になる(設計 §4 ★3)。
/// </summary>
public class SingleInstanceRequestTests
{
    [Fact]
    public void Serialize_RoundTripsThroughTryParse()
    {
        string wire = SingleInstanceRequest.Activate.Serialize();
        Assert.NotNull(SingleInstanceRequest.TryParse(wire));
    }

    [Fact]
    public void Serialize_StartsWithProtocolTag()
    {
        Assert.StartsWith(SingleInstanceRequest.ProtocolTag, SingleInstanceRequest.Activate.Serialize(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ACTIVATE")] // タグ無し
    [InlineData("KXEDIT1")] // 動詞無し
    [InlineData("KXEDIT2 ACTIVATE")] // 別バージョン
    [InlineData("kxedit1 ACTIVATE")] // タグの大小(プロトコルは Ordinal)
    [InlineData("KXEDIT1 activate")] // 動詞の大小
    [InlineData("KXEDIT1 QUIT")] // 未定義の動詞
    [InlineData("KXEDIT1 ACTIVATE EXTRA")] // 余分なトークン
    [InlineData("KXEDIT1  ACTIVATE")] // 空白 2 つ
    public void TryParse_RejectsMalformedInput(string? wire)
    {
        Assert.Null(SingleInstanceRequest.TryParse(wire));
    }

    [Fact]
    public void Ack_DiffersFromRequest()
    {
        // ACK を要求と同じ文字列にする変異を殺す(自分の送信をエコーバックと
        // 取り違える形になり、引き渡し失敗が成功に見える)。
        Assert.NotEqual(SingleInstanceRequest.Activate.Serialize(), SingleInstanceRequest.Ack);
    }
}
```

### Step 2: 失敗を確認

Expected: ビルド失敗

### Step 3: 実装

`src/kxEdit.App/SingleInstanceRequest.cs`:

```csharp
// SingleInstanceRequest.cs
// 設計 2026-09-14 §4: 引き渡しメッセージ。今日は空ペイロードだが、
// 将来ファイルパスが乗る器として型を置く(設計 §6)。
namespace kxEdit.App;

/// <summary>
/// 2 つ目のインスタンスが既存インスタンスへ送る要求。
/// </summary>
/// <remarks>
/// 1 行 1 メッセージのテキストプロトコル。行区切りは <c>\n</c>(送受信側が付与・除去する)。
/// <para>
/// <b>パースは厳格側に倒す</b>。ここは<b>他プロセスが書いたバイト列</b>を読む場所である。
/// 今日のペイロードは空なので緩くても実害はないが、将来ファイルパスを載せた時点で
/// 攻撃面になる(設計 §4 ★3)。緩いパーサを後から締めるのは、締めたまま始めるより難しい。
/// </para>
/// </remarks>
internal sealed record SingleInstanceRequest
{
    /// <summary>プロトコルタグ。互換性のない変更をしたら数字を上げる。</summary>
    internal const string ProtocolTag = "KXEDIT1";

    private const string ActivateVerb = "ACTIVATE";

    /// <summary>受理したことを返す応答。要求と別の文字列であること。</summary>
    internal const string Ack = ProtocolTag + " OK";

    /// <summary>既存ウィンドウの前面化要求(今日はこれ 1 種類だけ)。</summary>
    internal static SingleInstanceRequest Activate { get; } = new();

    internal string Serialize() => $"{ProtocolTag} {ActivateVerb}";

    /// <summary>解釈できないものは <c>null</c>。呼び出し側は ACK を返さず切る。</summary>
    internal static SingleInstanceRequest? TryParse(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire))
            return null;
        // 将来ファイルパスが乗ったらトークン数の上限は緩むが、その時も
        // 「タグが一致し、動詞が既知」は据え置く。
        string[] parts = wire.Split(' ');
        if (parts.Length != 2)
            return null;
        if (!string.Equals(parts[0], ProtocolTag, StringComparison.Ordinal))
            return null;
        if (!string.Equals(parts[1], ActivateVerb, StringComparison.Ordinal))
            return null;
        return Activate;
    }
}
```

### Step 4: テスト通過を確認 / Step 5: Commit

```
feat(app): 単一インスタンス引き渡しのメッセージ形式を追加
```

---

## Task 3: `SingleInstanceNames` — Mutex / パイプ名

**Files:**
- Create: `src/kxEdit.App/SingleInstanceNames.cs`
- Test: `tests/kxEdit.App.Tests/SingleInstanceNamesTests.cs`

### Step 1: 失敗するテストを書く

```csharp
using System.Diagnostics;
using System.Security.Principal;

namespace kxEdit.App.Tests;

/// <summary>
/// 名前のスコープ(設計 2026-09-14 D2 / §4)。
/// Mutex はログオンセッション単位、パイプは OS 全体の名前空間なので
/// セッション ID と SID を名前に埋めて手動でスコープを作る。
/// </summary>
public class SingleInstanceNamesTests
{
    [Fact]
    public void MutexName_IsScopedToLogonSession()
    {
        // Global\ への変異を殺す。Global\ にすると別ユーザーの kxEdit まで止める(設計 D2)。
        Assert.StartsWith(@"Local\", SingleInstanceNames.MutexName, StringComparison.Ordinal);
    }

    [Fact]
    public void PipeName_DiffersAcrossSessions()
    {
        Assert.NotEqual(
            SingleInstanceNames.PipeName(1, "S-1-5-21-1-1-1-1001"),
            SingleInstanceNames.PipeName(2, "S-1-5-21-1-1-1-1001")
        );
    }

    [Fact]
    public void PipeName_DiffersAcrossUsers()
    {
        Assert.NotEqual(
            SingleInstanceNames.PipeName(1, "S-1-5-21-1-1-1-1001"),
            SingleInstanceNames.PipeName(1, "S-1-5-21-1-1-1-1002")
        );
    }

    [Fact]
    public void PipeName_IsStableForSameInputs()
    {
        Assert.Equal(
            SingleInstanceNames.PipeName(3, "S-1-5-21-1-1-1-1001"),
            SingleInstanceNames.PipeName(3, "S-1-5-21-1-1-1-1001")
        );
    }

    [Fact]
    public void PipeName_FitsWithinWindowsPipeNameLimit()
    {
        // \\.\pipe\ を含めて 256 文字。ドメインユーザーの SID は長い。
        string name = SingleInstanceNames.PipeName(
            int.MaxValue,
            "S-1-5-21-3623811015-3361044348-30300820-1013"
        );
        Assert.True(name.Length + @"\\.\pipe\".Length <= 256, $"パイプ名が長すぎる: {name.Length}");
    }

    [Fact]
    public void CurrentPipeName_MatchesCurrentSessionAndUser()
    {
        // 実プロセスの値と組み立て関数の配線が切れていないことを見る
        // (CurrentPipeName が定数を返す変異を殺す)。
        using var self = Process.GetCurrentProcess();
        string expected = SingleInstanceNames.PipeName(
            self.SessionId,
            WindowsIdentity.GetCurrent().User!.Value
        );
        Assert.Equal(expected, SingleInstanceNames.CurrentPipeName());
    }
}
```

### Step 3: 実装

`src/kxEdit.App/SingleInstanceNames.cs`:

```csharp
// SingleInstanceNames.cs
// 設計 2026-09-14 D2 / §4「名前」。
using System.Diagnostics;
using System.Security.Principal;

namespace kxEdit.App;

/// <summary>
/// 単一インスタンス機構が使う Mutex / パイプの名前。
/// </summary>
/// <remarks>
/// <b>2 つの名前空間の性質が違う</b>のがここの要点である。
/// Mutex は <c>Local\</c> を付ければカーネルがログオンセッション単位に切ってくれるが、
/// <b>名前付きパイプの名前空間は OS 全体で共有され、<c>Local\</c> に相当する仕組みが無い</b>。
/// そのためセッション ID と ユーザー SID を名前に埋めて手動で同じスコープを作る。
/// </remarks>
internal static class SingleInstanceNames
{
    /// <summary><c>Local\</c> = ログオンセッション単位(設計 D2)。</summary>
    internal const string MutexName = @"Local\kxEdit.SingleInstance";

    internal static string PipeName(int sessionId, string userSid) =>
        $"kxEdit.SingleInstance.{sessionId}.{userSid}";

    /// <summary>現在のプロセスのセッション / ユーザーに対応するパイプ名。</summary>
    internal static string CurrentPipeName()
    {
        using var self = Process.GetCurrentProcess();
        var user =
            WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("現在のユーザーの SID を取得できない");
        return PipeName(self.SessionId, user.Value);
    }
}
```

### Step 5: Commit

```
feat(app): 単一インスタンス用の Mutex / パイプ名を追加
```

---

## Task 4: トランスポート(サーバ / クライアント) ★重点タスク

> **このタスクは CLAUDE.md §3.4 の前倒しレビュー条件に 2 つとも該当する。**
> 実装後に**脆弱性レビュー**(外部入力のパース・プロセス起動)と
> **コード品質レビュー**(後続タスクが依存する抽象)を、**別々のエージェント**で実施すること。

**Files:**
- Create: `src/kxEdit.App/NativeMethods.cs`
- Create: `src/kxEdit.App/SingleInstanceServer.cs`
- Create: `src/kxEdit.App/SingleInstanceClient.cs`
- Test: `tests/kxEdit.App.Tests/SingleInstanceChannelTests.cs`

### Step 1: `NativeMethods` を用意する(テスト不要・Win32 宣言のみ)

`src/kxEdit.App/NativeMethods.cs`:

```csharp
// NativeMethods.cs
// 設計 2026-09-14 §4: 単一インスタンスの引き渡しで使う Win32 宣言。
// 宣言のスタイルは src/kxEdit.Editor/NativeMethods.cs に合わせる(DllImport + MarshalAs)。
using System.Runtime.InteropServices;

namespace kxEdit.App;

internal static class NativeMethods
{
    internal const int SW_RESTORE = 9;

    /// <summary>
    /// クライアント側のパイプハンドルから、サーバ側プロセスの PID を得る。
    /// 相手が本当に kxEdit かを確かめる起点(設計 §4 ★3)。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeServerProcessId(nint Pipe, out uint ServerProcessId);

    /// <summary>
    /// 自分が持つフォアグラウンド権を指定プロセスへ譲渡する(設計 §4 ★1)。
    /// これを呼ばないと相手の <c>SetForegroundWindow</c> は拒否され、
    /// タスクバーボタンが点滅するだけで前面に出ない。
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(uint dwProcessId);

    /// <summary>
    /// オーナーウィンドウに対する「現在有効な最前面のポップアップ」。
    /// モーダルダイアログ表示中にオーナーを前面化すると入力を吸われるため、
    /// 実際に前面化すべき相手をこれで求める(設計 §4 ★2)。
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern nint GetLastActivePopup(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowEnabled(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);
}
```

### Step 2: 失敗するテストを書く

`tests/kxEdit.App.Tests/SingleInstanceChannelTests.cs`:

```csharp
using System.IO.Pipes;
using System.Text;

namespace kxEdit.App.Tests;

/// <summary>
/// 引き渡しトランスポート(設計 2026-09-14 §4)。
/// <para>
/// 本テストは<b>実パイプを使う</b>。ウィンドウは作らないので STA も UI も不要。
/// パイプ名は <see cref="UniqueName"/> で毎回変える —— 実運用の kxEdit が同じ PC で
/// 動いていても干渉させないため。
/// </para>
/// </summary>
public class SingleInstanceChannelTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(800);

    private static string UniqueName() => $"kxEditTest.{Environment.ProcessId}.{Guid.NewGuid():N}";

    [Fact]
    public void HandOff_InvokesActivationOnServer_AndReportsSuccess()
    {
        string pipe = UniqueName();
        int activations = 0;
        using var server = new SingleInstanceServer(
            pipe,
            () =>
            {
                activations++;
                return true;
            }
        );
        Assert.True(server.Start());

        Assert.Equal(HandoffResult.Success, SingleInstanceClient.TryHandOff(pipe, ShortTimeout));
        Assert.Equal(1, activations);
    }

    [Fact]
    public void HandOff_ServesMultipleRequestsInSequence()
    {
        // 1 接続で待受ループが終わる変異を殺す(2 回目以降が NoResponse になる)。
        string pipe = UniqueName();
        int activations = 0;
        using var server = new SingleInstanceServer(
            pipe,
            () =>
            {
                activations++;
                return true;
            }
        );
        Assert.True(server.Start());

        Assert.Equal(HandoffResult.Success, SingleInstanceClient.TryHandOff(pipe, ShortTimeout));
        Assert.Equal(HandoffResult.Success, SingleInstanceClient.TryHandOff(pipe, ShortTimeout));
        Assert.Equal(2, activations);
    }

    [Fact]
    public void HandOff_WithoutServer_ReportsNoResponse()
    {
        Assert.Equal(
            HandoffResult.NoResponse,
            SingleInstanceClient.TryHandOff(UniqueName(), ShortTimeout)
        );
    }

    [Fact]
    public void HandOff_WhenActivationFails_ReportsNoResponse()
    {
        // 既存インスタンスの UI スレッドがハングしている状況(設計 D4)。
        // ACK を返さないので、B 側は「応答しない」と判定しなければならない。
        string pipe = UniqueName();
        using var server = new SingleInstanceServer(pipe, () => false);
        Assert.True(server.Start());

        Assert.Equal(HandoffResult.NoResponse, SingleInstanceClient.TryHandOff(pipe, ShortTimeout));
    }

    [Fact]
    public void Server_RejectsMalformedRequest_WithoutInvokingActivation()
    {
        // 他プロセスが書いた不正なバイト列で前面化を起こさせない(設計 §4 ★3)。
        string pipe = UniqueName();
        int activations = 0;
        using var server = new SingleInstanceServer(
            pipe,
            () =>
            {
                activations++;
                return true;
            }
        );
        Assert.True(server.Start());

        using (
            var client = new NamedPipeClientStream(
                ".",
                pipe,
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            )
        )
        {
            client.Connect((int)ShortTimeout.TotalMilliseconds);
            byte[] junk = Encoding.UTF8.GetBytes("KXEDIT1 QUIT\n");
            client.Write(junk, 0, junk.Length);
            client.Flush();
            // ACK は返らない。読もうとすると 0 バイトで切れる。
            Assert.Equal(0, client.Read(new byte[16], 0, 16));
        }

        Assert.Equal(0, activations);
        // 不正要求のあとも待受は生きている(1 件で死ぬ変異を殺す)。
        Assert.Equal(HandoffResult.Success, SingleInstanceClient.TryHandOff(pipe, ShortTimeout));
        Assert.Equal(1, activations);
    }

    [Fact]
    public void Server_RejectsOversizedRequest_WithoutInvokingActivation()
    {
        // 改行を送らずに延々と書き続ける相手でメモリを食わせない。
        string pipe = UniqueName();
        int activations = 0;
        using var server = new SingleInstanceServer(
            pipe,
            () =>
            {
                activations++;
                return true;
            }
        );
        Assert.True(server.Start());

        using (
            var client = new NamedPipeClientStream(
                ".",
                pipe,
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            )
        )
        {
            client.Connect((int)ShortTimeout.TotalMilliseconds);
            byte[] flood = Encoding.UTF8.GetBytes(new string('x', 64 * 1024)); // 上限の 16 倍
            try
            {
                client.Write(flood, 0, flood.Length);
                client.Flush();
            }
            catch (IOException)
            { /* 上限到達でサーバが切る。切られること自体が期待動作 */
            }
        }

        Assert.Equal(0, activations);
    }

    [Fact]
    public void Start_ReturnsFalse_WhenPipeNameIsAlreadyTaken()
    {
        // 悪意あるプロセスが先回りして同名パイプを作った場合(設計 §4 ★3 の逆向き)。
        // A は待受なしで通常起動を続ける = 起動を止めない。
        string pipe = UniqueName();
        using var squatter = new SingleInstanceServer(pipe, () => true);
        Assert.True(squatter.Start());

        using var second = new SingleInstanceServer(pipe, () => true);
        Assert.False(second.Start());
    }

    [Fact]
    public void Dispose_StopsListening()
    {
        string pipe = UniqueName();
        var server = new SingleInstanceServer(pipe, () => true);
        Assert.True(server.Start());
        server.Dispose();

        Assert.Equal(HandoffResult.NoResponse, SingleInstanceClient.TryHandOff(pipe, ShortTimeout));
    }
}
```

### Step 3: 失敗を確認

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~SingleInstanceChannelTests"`
Expected: ビルド失敗

### Step 4: サーバを実装

`src/kxEdit.App/SingleInstanceServer.cs`:

```csharp
// SingleInstanceServer.cs
// 設計 2026-09-14 §4: 引き渡しの受け側。1 つ目のインスタンスが起動直後に開始する。
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace kxEdit.App;

/// <summary>
/// 2 つ目のインスタンスからの前面化要求を待ち受ける。
/// </summary>
/// <remarks>
/// <para>
/// <b>待受は Mutex 取得直後・<c>MainForm</c> 構築より前に始める</b>(設計 §4「起動中レース」)。
/// <c>MainForm</c> の構築は重く、ここを後ろに置くと「Mutex は取られているのにパイプが無い」
/// 窓が広がって、2 つ目が誤って「応答しません」エラーになる。
/// </para>
/// <para>
/// <b>インスタンス数は 1 に固定する</b>。名前付きパイプの名前空間は OS 全体で共有されるので、
/// 1 に固定して名前を専有し、他プロセスが同名の追加インスタンスを作れないようにする。
/// 引き渡しは一瞬で終わるので直列化のコストは問題にならない。
/// </para>
/// <para>
/// <paramref name="onActivate"/> は<b>パイプスレッドから呼ばれる</b>。UI スレッドへの
/// マーシャルは呼び出し側(<see cref="PendingActivation"/>)の責務。戻り値 <c>false</c> は
/// 「前面化できなかった」を意味し、その場合 ACK を返さない —— 2 つ目はそれを見て
/// 「応答しません」と判断する(設計 D4)。
/// </para>
/// </remarks>
internal sealed class SingleInstanceServer : IDisposable
{
    /// <summary>1 要求の上限。改行が来ないまま超えたら不正として切る。</summary>
    private const int MaxRequestBytes = 4096;

    /// <summary>1 接続あたりの上限時間。悪意ある / 壊れた相手で待受を詰まらせない。</summary>
    private static readonly TimeSpan PerConnectionTimeout = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly Func<bool> _onActivate;
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeServerStream? _pipe;
    private Task? _loop;

    internal SingleInstanceServer(string pipeName, Func<bool> onActivate)
    {
        _pipeName = pipeName;
        _onActivate = onActivate;
    }

    /// <summary>
    /// 待ち受けを開始する。<b>失敗しても起動は止めない</b>(呼び出し側は <c>false</c> を
    /// 受けても通常起動を続ける)。名前を先回りされた場合がここに来る。
    /// </summary>
    internal bool Start()
    {
        try
        {
            _pipe = CreatePipe(_pipeName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 名前を他プロセスに専有されている。攻撃者へデータを渡さない方向へ倒れる
            // (こちらからは一切接続しない)。以後の 2 つ目起動はエラーになる。
            Trace.TraceWarning($"single-instance: listen failed: {ex.Message}");
            return false;
        }
        _loop = Task.Run(() => RunAsync(_pipe));
        return true;
    }

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        // ACL は現在のユーザーのみ。パイプ名前空間は OS 全体共有なので、
        // 既定の DACL に頼らず明示的に絞る(設計 §4 ★3)。
        var user =
            WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("現在のユーザーの SID を取得できない");
        var security = new PipeSecurity();
        security.AddAccessRule(
            new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow)
        );
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security
        );
    }

    private async Task RunAsync(NamedPipeServerStream pipe)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return; // Dispose された
            }
            catch (IOException ex)
            {
                Trace.TraceWarning($"single-instance: accept failed: {ex.Message}");
                return;
            }

            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                deadline.CancelAfter(PerConnectionTimeout);
                await HandleAsync(pipe, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 1 件の失敗で待受を終わらせない(以後の起動が全部エラーになるため)。
                Trace.TraceWarning($"single-instance: handoff failed: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (pipe.IsConnected)
                        pipe.Disconnect();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    return;
                }
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        string? wire = await ReadLineAsync(pipe, ct).ConfigureAwait(false);
        if (SingleInstanceRequest.TryParse(wire) is null)
        {
            // ACK を返さずに切る。前面化もしない。
            Trace.TraceWarning("single-instance: rejected malformed request");
            return;
        }

        // ACK より前に前面化する。2 つ目は ACK を待ってから終了するので、
        // ここで前面化を終えておかないと譲渡されたフォアグラウンド権が消える(設計 §4 ★1)。
        if (!_onActivate())
        {
            Trace.TraceWarning("single-instance: activation reported failure; no ack");
            return;
        }

        byte[] ack = Encoding.UTF8.GetBytes(SingleInstanceRequest.Ack + "\n");
        await pipe.WriteAsync(ack, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 改行までを読む。<see cref="MaxRequestBytes"/> を超えたら <c>null</c>(=不正)。
    /// <c>StreamReader</c> を使わないのは、上限を自分で決めるため。
    /// </summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[MaxRequestBytes];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)
                .ConfigureAwait(false);
            if (read == 0)
                break; // 相手が閉じた
            int newline = Array.IndexOf(buffer, (byte)'\n', 0, total + read);
            total += read;
            if (newline >= 0)
                return Encoding.UTF8.GetString(buffer, 0, newline);
        }
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _pipe?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Trace.TraceWarning($"single-instance: pipe dispose failed: {ex.Message}");
        }
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        { /* 待受ループの終了例外は握る(終了処理を止めない) */
        }
        _cts.Dispose();
    }
}
```

### Step 5: クライアントを実装

`src/kxEdit.App/SingleInstanceClient.cs`:

```csharp
// SingleInstanceClient.cs
// 設計 2026-09-14 §4: 引き渡しの送り側。2 つ目のインスタンスが使う。
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace kxEdit.App;

/// <summary>引き渡しの結果。失敗は 2 種類に分かれる(設計 §4「失敗理由を 2 つに分ける」)。</summary>
internal enum HandoffResult
{
    /// <summary>既存インスタンスが前面化し、ACK を返した。</summary>
    Success,

    /// <summary>接続できない / ACK が返らない。既存インスタンスがハングしている等。</summary>
    NoResponse,

    /// <summary>接続先が自分と同じ実行ファイルではない(別ビルドの kxEdit・またはなりすまし)。</summary>
    ForeignPeer,
}

/// <summary>
/// 既存インスタンスへ前面化を依頼する。
/// </summary>
internal static class SingleInstanceClient
{
    internal static HandoffResult TryHandOff(string pipeName, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            );
            client.Connect((int)timeout.TotalMilliseconds);

            // ★3: 相手が本当に自分と同じ kxEdit か。今日のペイロードは空なので
            // 実害は無いが、将来ファイルパスを載せた時点で漏洩経路になる(設計 §6)。
            if (!TryVerifyPeer(client, out uint peerPid))
                return HandoffResult.ForeignPeer;

            // ★1: フォアグラウンド権を相手へ譲渡してから依頼する。
            // 失敗しても続行する(前面化されないだけで、引き渡し自体は成立しうる)。
            if (!NativeMethods.AllowSetForegroundWindow(peerPid))
                Trace.TraceWarning("single-instance: AllowSetForegroundWindow failed");

            byte[] payload = Encoding.UTF8.GetBytes(
                SingleInstanceRequest.Activate.Serialize() + "\n"
            );
            client.Write(payload, 0, payload.Length);
            client.Flush();

            // ★1: ACK を待たずに終了すると、譲渡した権利ごと消えて前面化が失敗する。
            return ReadAck(client) ? HandoffResult.Success : HandoffResult.NoResponse;
        }
        catch (Exception ex)
            when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"single-instance: handoff failed: {ex.Message}");
            return HandoffResult.NoResponse;
        }
    }

    /// <summary>
    /// パイプのサーバ側プロセスが、自分と同じ実行ファイルで動いているか。
    /// <b>確かめられない場合は拒否側へ倒す</b>。
    /// </summary>
    private static bool TryVerifyPeer(NamedPipeClientStream client, out uint peerPid)
    {
        peerPid = 0;
        if (
            !NativeMethods.GetNamedPipeServerProcessId(
                client.SafePipeHandle.DangerousGetHandle(),
                out peerPid
            )
        )
        {
            Trace.TraceWarning("single-instance: cannot resolve pipe server pid");
            return false;
        }
        try
        {
            using var peer = Process.GetProcessById((int)peerPid);
            string? peerImage = peer.MainModule?.FileName;
            string? selfImage = Environment.ProcessPath;
            if (peerImage is null || selfImage is null)
                return false;
            return string.Equals(peerImage, selfImage, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
            when (ex
                    is ArgumentException
                        or InvalidOperationException
                        or Win32Exception
                        or NotSupportedException
            )
        {
            Trace.TraceWarning($"single-instance: cannot verify pipe peer: {ex.Message}");
            return false;
        }
    }

    private static bool ReadAck(NamedPipeClientStream client)
    {
        byte[] buffer = new byte[64];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = client.Read(buffer, total, buffer.Length - total);
            if (read == 0)
                break;
            int newline = Array.IndexOf(buffer, (byte)'\n', 0, total + read);
            total += read;
            if (newline >= 0)
            {
                string line = Encoding.UTF8.GetString(buffer, 0, newline);
                return string.Equals(line, SingleInstanceRequest.Ack, StringComparison.Ordinal);
            }
        }
        return false;
    }
}
```

**注意**: `client.Read` はブロックする。相手がハングしたまま接続だけ受けた場合に備え、
`NamedPipeClientStream` に `ReadTimeout` は効かない(パイプはタイムアウトを持たない)。
**この点は Step 7 の脆弱性レビューで必ず検討すること** —— サーバ側に
`PerConnectionTimeout` があるので相手が kxEdit なら必ず切れるが、
なりすまし相手は ★3 で弾かれる前に接続だけは成立している。`ReadAck` を
`Task.Run` + `Wait(timeout)` にするか、`ReadAsync` + `CancellationToken` にするかを検討する。

### Step 6: テスト通過を確認

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~SingleInstanceChannelTests"`
Expected: PASS (8 件)

Run: `dotnet build kxEdit.sln -c Release -warnaserror`
Expected: 0 警告

### Step 7: Commit してから 2 つのレビューを回す

```
feat(app): 単一インスタンス引き渡しのパイプ送受信を追加
```

CLAUDE.md §3.4 の前倒しレビュー:

1. **脆弱性レビュー** (別エージェント) — 焦点: ★3 のなりすまし検証・パイプ ACL・
   `ReadLineAsync` の上限・`ReadAck` のブロック(上記の注意)・`DangerousGetHandle` の寿命。
2. **コード品質レビュー** (別エージェント) — 焦点: 後続 Task 6 / 7 が依存する抽象
   (`HandoffResult` / `Func<bool> onActivate` の契約)が妥当か。

指摘は CLAUDE.md §4 の 3 択(① fixup commit / ② PR description に記載して受容 / ③ 理由付き却下)
で明示し、修正は**別 fixup commit** で積む。

---

## Task 5: `WindowActivator` — 前面化

**Files:**
- Create: `src/kxEdit.App/WindowActivator.cs`
- Test: なし(実ウィンドウ必須。**L5 が担当**)

### Step 1: 実装

`src/kxEdit.App/WindowActivator.cs`:

```csharp
// WindowActivator.cs
// 設計 2026-09-14 §4 ★2 / §5: Win32 に触る部分をここ 1 点に押し込める。
namespace kxEdit.App;

/// <summary>
/// 既存インスタンスのウィンドウを前面へ出す。
/// </summary>
/// <remarks>
/// <b>この型に自動テストは無い</b>。実ウィンドウとフォアグラウンド状態が要るためで、
/// 検証は L5(実機・手動)が担当する(設計 §5)。逆に言えば、テストできる判断を
/// ここへ書かないこと —— ここに載せた分だけ自動検証の網から落ちる。
/// <para>呼び出しは UI スレッドから行うこと。</para>
/// </remarks>
internal static class WindowActivator
{
    internal static void Activate(Form form)
    {
        nint owner = form.Handle;

        // 最小化されていたら元のサイズへ戻す。WindowState を直接書かないのは、
        // 最大化していた場合に「元は最大化だった」を OS 側が覚えているため。
        if (NativeMethods.IsIconic(owner))
            NativeMethods.ShowWindow(owner, NativeMethods.SW_RESTORE);

        // モーダルダイアログ表示中はオーナーが無効化されている。オーナーを前面化すると
        // 入力をダイアログに吸われて操作不能に見えるので、有効なポップアップを狙う(★2)。
        nint target = NativeMethods.GetLastActivePopup(owner);
        if (target == 0 || !NativeMethods.IsWindowEnabled(target))
            target = owner;

        NativeMethods.SetForegroundWindow(target);
    }
}
```

### Step 2: ビルド確認

Run: `dotnet build kxEdit.sln -c Release -warnaserror`
Expected: 0 警告

### Step 3: Commit

```
feat(app): 既存ウィンドウの前面化(最小化復帰・モーダル考慮)を追加
```

---

## Task 6: `SingleInstanceGate` — 判定と分岐

**Files:**
- Create: `src/kxEdit.App/SingleInstanceGate.cs`
- Test: `tests/kxEdit.App.Tests/SingleInstanceGateTests.cs`

### Step 1: 失敗するテストを書く

```csharp
namespace kxEdit.App.Tests;

/// <summary>
/// 起動時の判定(設計 2026-09-14 §3「ゲートの 3 状態」/ §4「2 つのレース」)。
/// Mutex / パイプ名は毎回一意にする(実運用の kxEdit と干渉させない)。
/// </summary>
public class SingleInstanceGateTests : IDisposable
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(800);

    private readonly string _mutex = $@"Local\kxEditTest.{Guid.NewGuid():N}";
    private readonly string _pipe = $"kxEditTest.{Environment.ProcessId}.{Guid.NewGuid():N}";

    public void Dispose() => SingleInstanceGate.OnHandoffFailedForTest = null;

    [Fact]
    public void Acquire_FirstCall_BecomesFirstInstance()
    {
        var (outcome, gate) = SingleInstanceGate.Acquire(
            new CommandLineOptions(NewInstance: false),
            _mutex,
            _pipe,
            () => true,
            ShortTimeout
        );
        using (gate)
        {
            Assert.Equal(SingleInstanceOutcome.FirstInstance, outcome);
            Assert.NotNull(gate);
        }
    }

    [Fact]
    public void Acquire_SecondCall_HandsOffToFirst()
    {
        int activations = 0;
        var (first, gate) = SingleInstanceGate.Acquire(
            new CommandLineOptions(NewInstance: false),
            _mutex,
            _pipe,
            () =>
            {
                activations++;
                return true;
            },
            ShortTimeout
        );
        using (gate)
        {
            Assert.Equal(SingleInstanceOutcome.FirstInstance, first);

            var (second, secondGate) = SingleInstanceGate.Acquire(
                new CommandLineOptions(NewInstance: false),
                _mutex,
                _pipe,
                () => throw new InvalidOperationException("2 つ目は待受を始めてはならない"),
                ShortTimeout
            );
            Assert.Equal(SingleInstanceOutcome.HandedOff, second);
            Assert.Null(secondGate);
            Assert.Equal(1, activations);
        }
    }

    [Fact]
    public void Acquire_WithNewInstanceSwitch_SkipsExclusionEntirely()
    {
        // 抜け道(設計 D3)。2 回続けて FirstInstance になり、引き渡しも起きない。
        var options = new CommandLineOptions(NewInstance: true);
        var (first, a) = SingleInstanceGate.Acquire(
            options,
            _mutex,
            _pipe,
            () => throw new InvalidOperationException("引き渡しは起きてはならない"),
            ShortTimeout
        );
        using (a)
        {
            var (second, b) = SingleInstanceGate.Acquire(
                options,
                _mutex,
                _pipe,
                () => throw new InvalidOperationException("引き渡しは起きてはならない"),
                ShortTimeout
            );
            using (b)
            {
                Assert.Equal(SingleInstanceOutcome.FirstInstance, first);
                Assert.Equal(SingleInstanceOutcome.FirstInstance, second);
            }
        }
    }

    [Fact]
    public void Acquire_WhenMutexHeldButNobodyListens_ReportsNoResponse()
    {
        // 既存インスタンスがハングしている(設計 D4)。フォールバック起動はしない。
        using var held = new Mutex(initiallyOwned: true, _mutex, out bool createdNew);
        Assert.True(createdNew);

        var (outcome, gate) = SingleInstanceGate.Acquire(
            new CommandLineOptions(NewInstance: false),
            _mutex,
            _pipe,
            () => true,
            ShortTimeout
        );
        Assert.Equal(SingleInstanceOutcome.NoResponse, outcome);
        Assert.Null(gate);
    }

    [Fact]
    public void Acquire_RetriesMutexOnce_WhenPeerExitedDuringHandoff()
    {
        // 終了中レース(設計 §4): A が終了処理中で Mutex はまだ保持・パイプは閉じた。
        // 引き渡し失敗のあと Mutex を取り直せたら、自分が 1 つ目になる。
        // タイミング依存にしないため、テスト seam で「引き渡し失敗の直後」に解放する。
        var held = new Mutex(initiallyOwned: true, _mutex, out bool createdNew);
        Assert.True(createdNew);
        SingleInstanceGate.OnHandoffFailedForTest = () =>
        {
            held.ReleaseMutex();
            held.Dispose();
        };

        var (outcome, gate) = SingleInstanceGate.Acquire(
            new CommandLineOptions(NewInstance: false),
            _mutex,
            _pipe,
            () => true,
            ShortTimeout
        );
        using (gate)
        {
            Assert.Equal(SingleInstanceOutcome.FirstInstance, outcome);
            Assert.NotNull(gate);
        }
    }

    [Fact]
    public void Acquire_WhenMutexAccessIsDenied_ReportsNoResponse_WithoutThrowing()
    {
        // 昇格の順序(設計 §7 受容リスク)の代理再現。実際は高 IL プロセスが作った
        // Mutex を中 IL から開けない形だが、IL を跨がなくても「自分に権利を与えない
        // DACL の Mutex」で同じ UnauthorizedAccessException を起こせる。
        // ここで例外が漏れると、ゲートが D4 のエラーを出す前に起動時クラッシュする。
        var deny = new MutexSecurity();
        deny.AddAccessRule(
            new MutexAccessRule(
                WindowsIdentity.GetCurrent().User!,
                MutexRights.FullControl,
                AccessControlType.Deny
            )
        );
        using var blocker = MutexAcl.Create(
            initiallyOwned: false,
            _mutex,
            out _,
            mutexSecurity: deny
        );

        var (outcome, gate) = SingleInstanceGate.Acquire(
            new CommandLineOptions(NewInstance: false),
            _mutex,
            _pipe,
            () => true,
            ShortTimeout
        );
        Assert.Equal(SingleInstanceOutcome.NoResponse, outcome);
        Assert.Null(gate);
    }

    [Fact]
    public void Dispose_ReleasesMutex_SoNextLaunchBecomesFirstInstance()
    {
        var (_, gate) = SingleInstanceGate.Acquire(
            new CommandLineOptions(NewInstance: false),
            _mutex,
            _pipe,
            () => true,
            ShortTimeout
        );
        gate!.Dispose();

        var (outcome, second) = SingleInstanceGate.Acquire(
            new CommandLineOptions(NewInstance: false),
            _mutex,
            _pipe,
            () => true,
            ShortTimeout
        );
        using (second)
        {
            Assert.Equal(SingleInstanceOutcome.FirstInstance, outcome);
        }
    }
}
```

### Step 3: 実装

`src/kxEdit.App/SingleInstanceGate.cs`:

```csharp
// SingleInstanceGate.cs
// 設計 2026-09-14 §3「ゲートの 3 状態」/ §4「2 つのレース」。
using System.Diagnostics;

namespace kxEdit.App;

/// <summary>起動時の判定結果。</summary>
internal enum SingleInstanceOutcome
{
    /// <summary>自分が 1 つ目。通常起動する。</summary>
    FirstInstance,

    /// <summary>既存インスタンスへ引き渡した。無言で終了する。</summary>
    HandedOff,

    /// <summary>既存インスタンスが応答しない。エラーを出して終了する。</summary>
    NoResponse,

    /// <summary>別の場所の kxEdit が起動している。エラーを出して終了する。</summary>
    ForeignPeer,
}

/// <summary>
/// 多重起動を禁止するゲート。1 つ目なら Mutex を保持して待受を始め、
/// 2 つ目なら既存インスタンスへ引き渡す。
/// </summary>
/// <remarks>
/// <b>Mutex はこのオブジェクトが握り続ける</b>。ローカル変数に置くと GC が
/// ファイナライズして解放しうるので、プロセス寿命と一致させるためフィールドで持つ
/// (設計 §3「Mutex の寿命」)。
/// </remarks>
internal sealed class SingleInstanceGate : IDisposable
{
    /// <summary>
    /// 引き渡し失敗の直後・Mutex 取り直しの直前に呼ばれるテスト専用フック。
    /// 「終了中レース」を時間に頼らず再現するために置く(本番は常に <c>null</c>)。
    /// </summary>
    internal static Action? OnHandoffFailedForTest;

    private readonly Mutex _mutex;
    private readonly SingleInstanceServer? _server;

    private SingleInstanceGate(Mutex mutex, SingleInstanceServer? server)
    {
        _mutex = mutex;
        _server = server;
    }

    internal static (SingleInstanceOutcome Outcome, SingleInstanceGate? Gate) Acquire(
        CommandLineOptions options,
        string mutexName,
        string pipeName,
        Func<bool> onActivate,
        TimeSpan handoffTimeout
    )
    {
        if (options.NewInstance)
        {
            // 開発・検証用の抜け道(設計 D3)。排他も待受もしない ——
            // 待受を始めると、本来の 1 つ目が居ないとき次の起動を引き受けてしまう。
            Trace.TraceInformation("single-instance: bypassed by --new-instance");
            return (SingleInstanceOutcome.FirstInstance, null);
        }

        if (TryBecomeFirst(mutexName, pipeName, onActivate) is { } first)
            return (SingleInstanceOutcome.FirstInstance, first);

        var handoff = SingleInstanceClient.TryHandOff(pipeName, handoffTimeout);
        if (handoff == HandoffResult.Success)
            return (SingleInstanceOutcome.HandedOff, null);
        if (handoff == HandoffResult.ForeignPeer)
            return (SingleInstanceOutcome.ForeignPeer, null);

        OnHandoffFailedForTest?.Invoke();

        // 終了中レース(設計 §4): 既存インスタンスが終了処理の途中で、Mutex はまだ
        // 保持しているがパイプは閉じていた可能性がある。1 回だけ取り直す。
        // これが無いと、kxEdit を閉じた直後の再起動がエラーになる。
        if (TryBecomeFirst(mutexName, pipeName, onActivate) is { } retried)
            return (SingleInstanceOutcome.FirstInstance, retried);

        return (SingleInstanceOutcome.NoResponse, null);
    }

    /// <summary>Mutex を作れたら 1 つ目。作れなければ <c>null</c>。</summary>
    private static SingleInstanceGate? TryBecomeFirst(
        string mutexName,
        string pipeName,
        Func<bool> onActivate
    )
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, mutexName, out createdNew);
        }
        catch (UnauthorizedAccessException ex)
        {
            // 昇格の順序(設計 §7 受容リスク): 同一ユーザーが管理者として先に起動していると、
            // 名前は一致するが高 IL オブジェクトへの MUTEX_ALL_ACCESS が中 IL から拒否される。
            // ここを捕捉しないと D4 のエラーダイアログを出す前に起動時例外で落ち、
            // ゲート自身が「必ずエラーを出して終了する」という不変条件を破る。
            Trace.TraceWarning($"single-instance: mutex access denied: {ex.Message}");
            return null; // 呼び出し側は引き渡しを試み、失敗すれば NoResponse になる
        }
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        // 待受は MainForm 構築より前に始める(設計 §4「起動中レース」)。
        // 作れなくても起動は止めない ——「待受なしの 1 つ目」に劣化するだけ。
        var server = new SingleInstanceServer(pipeName, onActivate);
        if (!server.Start())
        {
            server.Dispose();
            return new SingleInstanceGate(mutex, null);
        }
        return new SingleInstanceGate(mutex, server);
    }

    public void Dispose()
    {
        _server?.Dispose();
        // ReleaseMutex は取得したスレッドからでないと失敗する。ここは
        // 「プロセスが終わる」経路なので、ハンドルを閉じて OS に解放させる。
        _mutex.Dispose();
    }
}
```

### Step 4: テスト通過を確認

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~SingleInstanceGateTests"`
Expected: PASS (7 件)

> 【Task 3 レビューによる追加 2026-09-14】`Acquire_WhenMutexAccessIsDenied_...` は
> 設計 §7 の「昇格の順序」リスクを回収するテスト。
>
> **実機で確認済み(2026-09-14)**: `MutexAcl` / `MutexSecurity` / `MutexAccessRule` は
> `NamedPipeServerStreamAcl`(追加参照不要)とは異なり、**PackageReference が必要**。
> `tests/kxEdit.App.Tests/kxEdit.App.Tests.csproj` にのみ次を足すこと
> (製品コードは ACL を読まないので `kxEdit.App` へは足さない):
>
> ```xml
> <PackageReference Include="System.Threading.AccessControl" Version="9.0.0" />
> ```
>
> 「自分に Deny を与える DACL の Mutex」を先に作ってから `new Mutex(true, name, out _)` すると、
> 実測で `UnauthorizedAccessException: Access to the path '...' is denied.` が出る
> ——昇格順序のケースと同じ例外型なので、IL を跨がずに代理再現できる。
> テスト側の using は `System.Security.AccessControl` と `System.Security.Principal`。

### Step 5: Commit

```
feat(app): 単一インスタンス判定ゲートを追加
```

---

## Task 7: `Program.Main` への配線

**Files:**
- Create: `src/kxEdit.App/PendingActivation.cs`
- Modify: `src/kxEdit.App/Program.cs:10`(`Main` のシグネチャと本体)
- Test: `tests/kxEdit.App.Tests/PendingActivationTests.cs`
- Test: `tests/kxEdit.App.Tests/MainFormSmokeTests.cs`(IL 順序テストを 1 本追加)

### Step 1: `PendingActivation` のテストを書く

```csharp
namespace kxEdit.App.Tests;

/// <summary>
/// 前面化要求の受け皿(設計 2026-09-14 §4「起動中レース」)。
/// フォーム生成前に来た要求を取りこぼさないことを固定する。
/// </summary>
public class PendingActivationTests
{
    [Fact]
    public void Request_BeforeFormExists_IsAcceptedAndDeferred()
    {
        // ウィンドウがまだ無い時点の要求は「失敗」ではない。
        // false を返す変異は、起動直後の 2 つ目起動を誤ってエラーにする。
        var pending = new PendingActivation();
        Assert.True(pending.Request());
    }

    [Fact]
    public void Attach_ConsumesDeferredRequest()
    {
        int activated = 0;
        var pending = new PendingActivation(_ => activated++);
        Assert.True(pending.Request());

        pending.Attach(form: null!);
        Assert.Equal(1, activated);
    }

    [Fact]
    public void Attach_WithoutDeferredRequest_DoesNotActivate()
    {
        // 起動のたびに勝手に前面化しない(通常起動では要求が無い)。
        int activated = 0;
        var pending = new PendingActivation(_ => activated++);

        pending.Attach(form: null!);
        Assert.Equal(0, activated);
    }

    [Fact]
    public void Attach_ConsumesDeferredRequestOnlyOnce()
    {
        int activated = 0;
        var pending = new PendingActivation(_ => activated++);
        Assert.True(pending.Request());

        pending.Attach(form: null!);
        pending.Attach(form: null!);
        Assert.Equal(1, activated);
    }
}
```

**注**: `PendingActivation` は前面化の実処理を `Action<Form>` として受け取る形にする
(既定は `WindowActivator.Activate`)。こうしないと `Attach` の分岐がテストできない。
テストは `form: null!` を渡すが、注入した fake は `Form` を使わないので安全。

### Step 2: `PendingActivation` を実装

`src/kxEdit.App/PendingActivation.cs`:

```csharp
// PendingActivation.cs
// 設計 2026-09-14 §4「起動中レース」: 待受は MainForm 構築より前に始まるため、
// ウィンドウがまだ無い時点で前面化要求が来うる。取りこぼさずに保留する。
using System.Diagnostics;

namespace kxEdit.App;

/// <summary>
/// パイプスレッドから来る前面化要求を UI スレッドへ渡す受け皿。
/// </summary>
/// <remarks>
/// 今日は「どのみち起動して前面に出る」ので保留の消化は実質 no-op だが、
/// <b>将来ファイル引数が乗ると取りこぼしが実害になる</b>(設計 §6)。配線を先に作っておく。
/// </remarks>
internal sealed class PendingActivation
{
    /// <summary>UI スレッドがハングしている場合に待ち続けないための上限(設計 D4)。</summary>
    private static readonly TimeSpan ActivateTimeout = TimeSpan.FromSeconds(3);

    private readonly Action<Form> _activate;
    private readonly object _sync = new();
    private Form? _form;
    private bool _deferred;

    internal PendingActivation(Action<Form>? activate = null) =>
        _activate = activate ?? WindowActivator.Activate;

    /// <summary>
    /// <b>パイプスレッドから呼ばれる。</b>UI スレッドへマーシャルして前面化する。
    /// 戻り値は「引き渡しが成立したか」——<c>false</c> なら 2 つ目は
    /// 「応答しません」エラーになる(設計 D4)。
    /// </summary>
    internal bool Request()
    {
        Form? form;
        lock (_sync)
        {
            form = _form;
            if (form is null)
            {
                // まだウィンドウが無い = 自分が今まさに起動中。成立扱いにする。
                _deferred = true;
                return true;
            }
        }
        try
        {
            // Invoke ではなく BeginInvoke + 期限付き待機。UI スレッドが固まっていても
            // パイプの待受スレッドを道連れにしない。
            var async = form.BeginInvoke(() => _activate(form));
            return async.AsyncWaitHandle.WaitOne(ActivateTimeout);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            Trace.TraceWarning($"single-instance: activation failed: {ex.Message}");
            return false;
        }
    }

    /// <summary><b>UI スレッドから呼ぶ。</b>ウィンドウが出来たことを知らせ、保留を消化する。</summary>
    internal void Attach(Form form)
    {
        bool deferred;
        lock (_sync)
        {
            _form = form;
            deferred = _deferred;
            _deferred = false;
        }
        if (deferred)
            _activate(form);
    }
}
```

### Step 3: `Program.Main` を書き換える

`src/kxEdit.App/Program.cs` —— `Main` のシグネチャを変え、`EncodingCatalog` /
Markdig ログのブロックの**直後**、`PreviewUserDataSweeper.SweepIfSoleInstance()` の**直前**に
ゲートを挿す。既存のコメントと順序は一切動かさないこと。

```csharp
    [STAThread]
    static void Main(string[] args)
    {
        // ...(既存の ImeStartup ブロックはそのまま)...
        // ...(EncodingCatalog.EnsureRegistered / Markdig の Trace もそのまま)...

        // 単一インスタンス(設計 2026-09-14 §3)。ここは ImeStartup より後・
        // PreviewUserDataSweeper / SettingsStartup.Prepare より前でなければならない:
        //   - ImeStartup より後 = 失敗経路が MessageBox を出す(ウィンドウを作る)ため、
        //     「IME 推測の無効化は最初のウィンドウより前」の不変条件をまたがない。
        //   - Sweeper / Prepare より前 = どちらも共有状態を書き換える。特に Prepare は
        //     壊れた settings.json を退避するので、2 つ目に実行させてはならない。
        // この前後関係は ProgramMain_gates_single_instance_before_touching_shared_state が固定する。
        var pending = new PendingActivation();
        var (outcome, gate) = SingleInstanceGate.Acquire(
            CommandLineOptions.Parse(args),
            SingleInstanceNames.CurrentMutexName(),
            SingleInstanceNames.CurrentPipeName(),
            pending.Request,
            HandoffTimeout
        );
        using (gate)
        {
            switch (outcome)
            {
                case SingleInstanceOutcome.HandedOff:
                    // 既存ウィンドウが前面に出た。無言で終わる(設計 D1)。
                    return;
                case SingleInstanceOutcome.NoResponse:
                    ShowStartupError(
                        "kxEdit は既に起動していますが応答しません。\n"
                            + "タスク マネージャーで kxEdit を終了してから、もう一度実行してください。"
                    );
                    return;
                case SingleInstanceOutcome.ForeignPeer:
                    ShowStartupError(
                        "別の場所にある kxEdit が既に起動しています。\n"
                            + "先にそちらを終了してから、もう一度実行してください。"
                    );
                    return;
            }

            PreviewUserDataSweeper.SweepIfSoleInstance();
            ApplicationConfiguration.Initialize();

            // ...(既存の SetUnhandledExceptionMode / CrashHandler 配線はそのまま)...

            var form = CreateMainForm(SettingsStore.DefaultPath);
            // ...(crash 配線はそのまま)...

            // ウィンドウが出来たことを PendingActivation へ知らせる。MainForm 側は触らない
            // (ctor 引数を増やすと CreateMainForm の呼び出し側すべてに波及するため)。
            form.Shown += (_, _) => pending.Attach(form);

            Application.Run(form);
        }
    }

    /// <summary>単一インスタンス判定の失敗をユーザーへ伝える(設計 D4)。</summary>
    private static void ShowStartupError(string message) =>
        MessageBox.Show(message, "kxEdit", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>引き渡しの待ち時間(設計 §4)。</summary>
    private static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(3);
```

**注意**: `using (gate)` で `Application.Run` まで囲むこと。`gate` は `Mutex` を握っており、
先に破棄すると排他が終了前に外れる。

### Step 4: IL 順序テストを追加

`tests/kxEdit.App.Tests/MainFormSmokeTests.cs` に既存の IME テストと同じ様式で追加:

```csharp
    /// <summary>
    /// 単一インスタンスのゲートが<b>共有状態を書き換える処理より前</b>にあることを固定する
    /// (設計 2026-09-14 §3)。<c>SettingsStartup.Prepare</c> は壊れた <c>settings.json</c> を
    /// 退避するので、2 つ目のプロセスにそこまで到達させてはならない。
    /// <para>
    /// <c>Prepare</c> は <c>CreateMainForm</c> の中なので、<c>Main</c> の IL では
    /// <c>CreateMainForm</c> をアンカーにする(<c>Prepare</c> 自体は現れない)。
    /// <c>SweepIfSoleInstance</c> は <c>Main</c> に直接現れるので両方を見る。
    /// </para>
    /// <para>
    /// <b>守らないもの</b>: 実行順(固定できるのは IL 上の出現順まで)。
    /// 既存の IME テストの xmldoc と同じ制約である。
    /// </para>
    /// </summary>
    [Fact]
    public void ProgramMain_gates_single_instance_before_touching_shared_state()
    {
        var main = typeof(Program).GetMethod(
            "Main",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
        );
        Assert.NotNull(main);
        var called = IlCallees.OfIncludingNewobj(main!);

        int gate = called.FindIndex(m =>
            m.DeclaringType == typeof(SingleInstanceGate)
            && m.Name == nameof(SingleInstanceGate.Acquire)
        );
        int sweep = called.FindIndex(m =>
            m.DeclaringType == typeof(PreviewUserDataSweeper)
            && m.Name == nameof(PreviewUserDataSweeper.SweepIfSoleInstance)
        );
        int compose = called.FindIndex(m =>
            m.DeclaringType == typeof(Program) && m.Name == nameof(Program.CreateMainForm)
        );

        Assert.True(gate >= 0, "Program.Main が SingleInstanceGate.Acquire を呼んでいない(設計 §3)");
        Assert.True(sweep >= 0, "アンカーが消えた: PreviewUserDataSweeper.SweepIfSoleInstance");
        Assert.True(compose >= 0, "アンカーが消えた: Program.CreateMainForm");
        Assert.True(
            gate < sweep,
            $"単一インスタンス判定は共有状態の掃除より前でなければならない。IL 出現順: gate={gate}, sweep={sweep}"
        );
        Assert.True(
            gate < compose,
            $"単一インスタンス判定は設定読込(CreateMainForm 内の SettingsStartup.Prepare)より前でなければならない。IL 出現順: gate={gate}, compose={compose}"
        );
    }
```

### Step 5: 既存テストが壊れていないことを確認

Run: `dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~MainFormSmokeTests"`
Expected: PASS。特に `ProgramMain_suppresses_ime_mode_inference_before_creating_any_window`
が引き続き緑であること(`Main` のシグネチャ変更で反射が壊れていないかの確認も兼ねる)。

### Step 6: 全体ビルドとテスト

```powershell
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```
Expected: 0 警告・全緑

### Step 7: 手で動かして確認(自動テストでは見えない部分)

1. `dotnet build -c Release` 後、`src/kxEdit.App/bin/Release/net9.0-windows/kxEdit.exe` を起動。
2. もう一度同じ exe を起動 → **新しいウィンドウが出ず、既存ウィンドウが前面に出る**こと。
3. 既存を最小化してから 2 つ目を起動 → 復元して前面に出ること。
4. `kxEdit.exe --new-instance` → 2 つ目のウィンドウが出ること。
5. 全部閉じてから起動 → 普通に起動すること(終了中レースの取りこぼしが無いこと)。

### Step 8: Commit

```
feat(app): 多重起動を禁止し既存インスタンスへフォーカスを移す
```

---

## Task 8: L5 チェックリストと説明書

**Files:**
- Create: `docs/plans/2026-09-14-single-instance-l5-checklist.md`
- Modify: `説明書/kxEdit説明書.md`(**提案のみ。ユーザー校閲前提**)

### Step 1: L5 チェックリストを書く

既存の `docs/plans/2026-09-*-l5-checklist.md` の書式に合わせ、最低限次を含める:

| # | 手順 | 期待 |
|---|---|---|
| 1 | kxEdit を起動 → もう一度起動 | 新しいウィンドウが出ない。既存が前面に出て、SR が既存のフォーカス位置を読む |
| 2 | 既存を最小化 → 2 つ目を起動 | 元のサイズに復元して前面化。SR が読む |
| 3 | 既存を**最大化**して最小化 → 2 つ目を起動 | **最大化状態**に復元される(`SW_RESTORE` が効いている) |
| 4 | 既存で設定ダイアログを開く → 2 つ目を起動 | **設定ダイアログ**が前面化し操作できる。メインウィンドウが前に出てダイアログに吸われない(★2) |
| 5 | 既存でファイルを開き未保存にする → 2 つ目を起動 | 未保存内容が失われない。バックアップが消えない |
| 6 | `kxEdit.exe --new-instance` | 2 つ目が起動する |
| 7 | 別フォルダにコピーした kxEdit.exe を起動 | 「別の場所にある kxEdit が既に起動しています」が出る |
| 8 | 全部終了 → すぐ起動 | 普通に起動する(「応答しません」が出ない) |

各項目に L5 の記録欄(実施日・結果・所見)を設ける。

### Step 2: 説明書の追記を**提案**する

`説明書/kxEdit説明書.md` は**ユーザー編集版が正**(CLAUDE.md §8)。勝手に改稿しない。
差分案を PR description に載せ、ユーザーの校閲を受けてから反映する。案:

> **kxEdit は同時に 1 つだけ起動します。** すでに kxEdit が起動している状態でもう一度
> 起動すると、新しいウィンドウは開かず、すでに開いている kxEdit のウィンドウが前面に出ます。

### Step 3: Commit

```
docs(plans): 単一インスタンス化の L5 チェックリストを追加
```

---

## Task 9: 最終ブランチレビューと統合

CLAUDE.md §3.5 / §6 / §7 に従う。

### Step 1: 最終ブランチレビュー(2 パス・**別々のエージェント**)

1. **コード品質パス** — ミューテーション検証のスポットチェック込み。
   ただし**本機能自体へのミューテーション検証は行わない**(CLAUDE.md §4A の禁止領域:
   プロセス間 I/O・イベント配線)。対象は `CommandLineOptions` /
   `SingleInstanceRequest` のパース部に限ってよい。
2. **脆弱性パス** — Task 4 の前倒しレビューと重複するが、ブランチ全体で見る。

並走させる場合は、**エージェントごとに専用のワークツリーを割り当て、リポジトリ本体では
ビルドさせない**こと(並走時のビルド衝突を避けるため)。

指摘は 3 択(① fixup commit / ② PR description に記載して受容 / ③ 理由付き却下)で明示し、
修正は**別 fixup commit** で積む(元 commit を書き換えない)。

### Step 2: 品質ゲート

```powershell
powershell -File tools\pre-merge-check.ps1
```
Expected: **EXIT 0**

### Step 3: L5 実施

`docs/plans/2026-09-14-single-instance-l5-checklist.md` をユーザーへ渡して実機検証を依頼する。
不具合が出たら修正 → 再実施。

### Step 4: PR

```bash
git push -u origin feature/single-instance
gh pr create --base main --title "多重起動を禁止し既存インスタンスへフォーカスを移す" --body-file <file>
```

PR description(日本語)に含めるもの:
- 目的と背景(M9+ 申し送りの回収)
- 設計上の決定(D1〜D5)へのリンク
- レビュー経緯(前倒し 2 本 + 最終 2 パス)と受容した指摘
- L5 実施結果
- **申し送り**: 同一ユーザー複数セッションでの `%APPDATA%` 競合(受容)/
  ファイル関連付け + コマンドライン引数でのファイルオープン(未実装)
- 説明書の追記案(ユーザー校閲待ち)
