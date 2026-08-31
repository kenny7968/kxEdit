# ネットワーク/クラウドのパス喪失と UI 凍結(A-15 / A-16 / A-17)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** OneDrive などクラウド配下のファイルが hot exit 復元で無言の「無題」降格になる問題を根治し、
不達のネットワークパスで UI が最大 60 秒凍結する経路(復元・grep)を境界付きにする。

**Architecture:** 3 本の変更。(1) `OriginalPathValidator` の reparse 検査を「属性ビット」から
「reparse タグの name surrogate 判定」へ置き換える(Core 初の P/Invoke を `ReparseTagReader` に隔離)。
(2) reparse walk の skip 対象を UNC からリモート全体(`RemotePathDetector.IsRemote`)へ広げ、
`Check` 入口の無境界 `GetFullPath` を呼出側の境界付き正規化の後ろへ動かす。
(3) grep の `Directory.Exists` 2 箇所をリモート時のみ 5 秒プローブへ回す。

**Tech Stack:** .NET 9 / C# / WinForms / xUnit / Win32 P/Invoke (`kernel32.dll`)

**設計書:** `docs/plans/2026-08-31-network-cloud-path-freeze-design.md`(先に読むこと)

---

## この計画の読み方(実装者への注意)

- **本計画に書かれたコードは「検証すべき案」であって正解ではない。** 過去のブランチで、計画の
  コードが本体・fixture ともに複数箇所で誤っていた実績がある。**必ずテストで確かめてから採用**し、
  食い違ったら計画ではなく実測を信じて計画側にその旨を追記すること。
- **ビルドは常に Release** で行う。Core.Tests の Debug は既知の S-5(`WordBoundary.cs` の
  `Debug.Assert`)で 4 件赤になる。
- コミット前に pre-commit フック(CSharpier 整形 + ローカルパス検出)が走る。`--no-verify` で
  飛ばさない。**ドキュメントにユーザーホーム配下の実パスを書くとフックで弾かれる**ので
  `%USERPROFILE%` 等のプレースホルダーを使う(この規則を説明する文自体も対象になる)。
- 各タスクは「実装 → 仕様レビュー」で 1 単位(CLAUDE.md §3-4)。指摘を反映してから次へ進む。
  **Task 1 / Task 2 は脆弱性レビューを、Task 4 はコード品質レビューを前倒しで実施する。**

### 共通コマンド

```bash
# ビルド(0 warning を維持すること)
dotnet build kxEdit.sln -c Release -warnaserror

# 個別テスト
dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~<TestClass>"
dotnet test tests/kxEdit.App.Tests  -c Release --no-build --filter "FullyQualifiedName~<TestClass>"

# 全ゲート(マージ前)
pwsh tools/pre-merge-check.ps1
```

---

## Task 1: `ReparseTagReader`(Core 初の P/Invoke)

reparse point のタグを読み、name surrogate かどうかを判定する部品を作る。
**この時点では `OriginalPathValidator` は変更しない**(部品だけを先に確定させる)。

**Files:**
- Create: `src/kxEdit.Core/IO/ReparseTagReader.cs`
- Create: `tests/kxEdit.Core.Tests/IO/ReparsePointFixture.cs`
- Create: `tests/kxEdit.Core.Tests/IO/ReparseTagReaderTests.cs`

### Step 1.1: テスト用の reparse point 生成ヘルパを書く

**なぜ必要か:** 「Microsoft タグだが name surrogate ではない」= クラウドプレースホルダーと
同じ状況を、実ファイルシステム上に作るため。策定時の実測で
**非 Microsoft タグの reparse point は管理者権限なしで作れる**ことを確認済み
(ファイルシンボリックリンクは要管理者なので使えない)。

`tests/kxEdit.Core.Tests/IO/ReparsePointFixture.cs`:

```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace kxEdit.Core.Tests.IO;

/// <summary>
/// テスト用に「任意タグの reparse point」を作る。クラウドプレースホルダー
/// (Microsoft タグだが name surrogate ではない)と同じ形を実ファイルシステム上に用意するための道具。
///
/// 非 Microsoft タグ(bit31 = 0)は <c>REPARSE_GUID_DATA_BUFFER</c> 形式で書け、
/// 管理者権限を要しない(策定時の実測)。ファイルシンボリックリンクの作成は要管理者 /
/// 開発者モードなので、この経路では使わない。
///
/// 作成できない環境(非 NTFS / ポリシー制限 / CI)では <see cref="TryCreate"/> が false を返し、
/// 呼出側テストは既存の <c>Check_Rejects_PathThroughJunction</c> と同じく early return で
/// skip 相当にする。
/// </summary>
internal static class ReparsePointFixture
{
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;

    /// <summary>name surrogate ではない非 Microsoft タグ(クラウドプレースホルダーの代用)。</summary>
    internal const uint NonSurrogateTag = 0x00000123;

    /// <summary>name surrogate ビットを持つ非 Microsoft タグ。</summary>
    internal const uint SurrogateTag = 0x20000123;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        int nInBufferSize,
        nint lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        nint lpOverlapped
    );

    /// <summary>既存ファイルを指定タグの reparse point にする。成功したら true。</summary>
    internal static bool TryCreate(string path, uint tag)
    {
        try
        {
            using var handle = CreateFile(
                path,
                GENERIC_READ | GENERIC_WRITE,
                0,
                nint.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                nint.Zero
            );
            if (handle.IsInvalid)
                return false;

            // REPARSE_GUID_DATA_BUFFER: ReparseTag(4) ReparseDataLength(2) Reserved(2)
            //                           ReparseGuid(16) DataBuffer(n)
            const int dataLength = 8;
            var buffer = new byte[8 + 16 + dataLength];
            BitConverter.GetBytes(tag).CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)dataLength).CopyTo(buffer, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(buffer, 6);
            Guid.NewGuid().ToByteArray().CopyTo(buffer, 8);

            return DeviceIoControl(
                handle,
                FSCTL_SET_REPARSE_POINT,
                buffer,
                buffer.Length,
                nint.Zero,
                0,
                out _,
                nint.Zero
            );
        }
        catch
        {
            return false;
        }
    }

    /// <summary>使い捨ての一時ディレクトリを作って返す。</summary>
    internal static string CreateTempDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "kxedit_reparse_" + Guid.NewGuid().ToString("N"))
        ).FullName;
}
```

### Step 1.2: 失敗するテストを書く

`tests/kxEdit.Core.Tests/IO/ReparseTagReaderTests.cs`:

```csharp
using kxEdit.Core.IO;
using Xunit;

namespace kxEdit.Core.Tests.IO;

/// <summary>
/// A-15: reparse point を「名前を横取りするか」で分類する契約を固定する。
/// クラウドプレースホルダー(Microsoft タグだが name surrogate ではない)を
/// junction / symlink と区別できることが本体。
/// </summary>
public class ReparseTagReaderTests
{
    // ---- 純関数側: ビット判定 ----------------------------------------------------------

    [Theory]
    [InlineData(0xA0000003u, true)] // IO_REPARSE_TAG_MOUNT_POINT (junction)
    [InlineData(0xA000000Cu, true)] // IO_REPARSE_TAG_SYMLINK
    [InlineData(0x9000001Au, false)] // IO_REPARSE_TAG_CLOUD
    [InlineData(0x80000013u, false)] // IO_REPARSE_TAG_DEDUP
    [InlineData(0x80000017u, false)] // IO_REPARSE_TAG_WOF
    [InlineData(0x00000123u, false)] // 非 Microsoft・非 surrogate
    [InlineData(0x20000123u, true)] // 非 Microsoft・surrogate
    public void IsNameSurrogate_ClassifiesByBit(uint tag, bool expected) =>
        Assert.Equal(expected, ReparseTagReader.IsNameSurrogate(tag));

    // ---- タグ取得側: 実ファイルシステム ------------------------------------------------

    [Fact]
    public void TryRead_ReturnsNull_ForPlainFile()
    {
        var dir = ReparsePointFixture.CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "plain.txt");
            File.WriteAllText(file, "x");
            // reparse point でないパスはタグを持たない = 0 を返す契約。
            Assert.Equal(0u, ReparseTagReader.TryRead(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryRead_ReturnsNull_ForMissingPath()
    {
        var missing = Path.Combine(Path.GetTempPath(), "kxedit_nope_" + Guid.NewGuid().ToString("N"));
        Assert.Null(ReparseTagReader.TryRead(missing));
    }

    [Fact]
    public void TryRead_ReturnsTag_ForCustomReparsePoint()
    {
        var dir = ReparsePointFixture.CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "cloudish.txt");
            File.WriteAllText(file, "");
            if (!ReparsePointFixture.TryCreate(file, ReparsePointFixture.NonSurrogateTag))
                return; // Skip: reparse point を作れない環境(非 NTFS / ポリシー / CI)

            Assert.Equal(ReparsePointFixture.NonSurrogateTag, ReparseTagReader.TryRead(file));
            Assert.False(ReparseTagReader.IsNameSurrogate(ReparsePointFixture.NonSurrogateTag));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

> **注意(既知の不確定点):** `TryRead_ReturnsNull_ForPlainFile` の期待値を `0u` と書いたのは、
> `GetFileInformationByHandleEx(FileAttributeTagInfo)` が非 reparse point に対して
> `ReparseTag = 0` を返す想定に基づく。**策定時に直接は測っていない。** Step 1.4 で
> 実際の値を確認し、`null` を返す実装にするか `0` を返すかを**実測に合わせて確定**すること。
> 契約として重要なのは「呼出側が `null` と `0` のどちらでも surrogate と誤判定しないこと」だけ。

### Step 1.3: テストが失敗することを確認

```bash
dotnet build kxEdit.sln -c Release -warnaserror
```
Expected: FAIL — `ReparseTagReader` が存在しないためコンパイルエラー。

### Step 1.4: 実装を書く

`src/kxEdit.Core/IO/ReparseTagReader.cs`:

```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace kxEdit.Core.IO;

/// <summary>
/// A-15: reparse point の**タグ**を読み、「名前を別の場所へ横取りするか」
/// (= Windows が name surrogate と呼ぶ種別か)を判定する。
///
/// <para><b>なぜ属性ビットでは足りないか</b>: <see cref="FileAttributes.ReparsePoint"/> は
/// junction / symbolic link だけでなく、OneDrive Files On-Demand のクラウドプレースホルダー・
/// 重複除去(DEDUP)・WOF 圧縮・AppExecLink にも立つ。これらは名前を横取りしないので、
/// 属性ビットだけで拒否すると**クラウド配下の普通のファイルを拒否**してしまう(= A-15)。</para>
///
/// <para><b>なぜタグの列挙ではなくビット判定か</b>: 「拒否したいタグの列挙」は原理的に漏れる
/// (<c>OriginalPathValidator</c> が事後条件の議論で確立した規律と同型)。name surrogate ビット
/// (<c>0x20000000</c>)は Windows 自身が「この reparse point は別の名前付き実体を表す」と
/// 宣言するためのビットで、パス解決が追従する種別と 1:1 で対応する。</para>
///
/// <para><b>なぜ <see cref="FileSystemInfo.LinkTarget"/> を使わないか</b>(P/Invoke を避けられる案):
/// 策定時の実測(2026-08-31・net9.0)で、<c>LinkTarget</c> は
/// <b>非 Microsoft の name surrogate タグ(<c>0x20000123</c>)にも <c>null</c> を返した</b>。
/// つまり <c>LinkTarget != null</c> は name surrogate 判定と等価ではなく、これに置き換えると
/// サードパーティ製フィルタドライバの surrogate が現状より緩く通る。同じ実測で
/// junction は解決先を返し、未対応タグでは例外ではなく <c>null</c> が返ることも確認している。</para>
///
/// <para><b>hydrate を誘発しない</b>: <c>FILE_FLAG_OPEN_REPARSE_POINT</c> を付けて開くので、
/// 未ダウンロードのクラウドファイルに対してダウンロードを起こさない(復元経路が
/// 通信を誘発しないための要件)。<c>FILE_FLAG_BACKUP_SEMANTICS</c> はディレクトリを
/// 同じ呼び出しで扱うために必要。</para>
/// </summary>
internal static class ReparseTagReader
{
    /// <summary>この reparse point は別の名前付き実体を表す、というビット
    /// (<c>IO_REPARSE_TAG_NAME_SURROGATE_BIT</c>)。</summary>
    internal const uint NameSurrogateBit = 0x20000000;

    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_READ_WRITE_DELETE = 0x00000007;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    /// <summary><c>FileAttributeTagInfo</c>(<c>FILE_INFO_BY_HANDLE_CLASS</c> の 9)。</summary>
    private const int FileAttributeTagInfoClass = 9;

    /// <summary><c>FILE_ATTRIBUTE_TAG_INFO</c>。DWORD 2 本のみでパディングの罠が無い
    /// (<c>WIN32_FIND_DATAW</c> 経由でタグを取る案は、先頭 DWORD の後に FILETIME が来るため
    /// 既定 Pack でフィールドがずれる。策定時に実際に踏んだので採らない)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        EntryPoint = "CreateFileW"
    )]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int FileInformationClass,
        out FileAttributeTagInfo lpFileInformation,
        int dwBufferSize
    );

    /// <summary>
    /// <paramref name="path"/> の reparse タグを返す。reparse point でなければ <c>0</c>、
    /// **読み取れなかった場合は <c>null</c>**(存在しない / アクセス不能 / API 失敗)。
    /// 呼出側は <c>null</c> を「安全と判明した」と読んではならない。
    /// </summary>
    internal static uint? TryRead(string path)
    {
        try
        {
            using var handle = CreateFile(
                path,
                FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ_WRITE_DELETE,
                nint.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                nint.Zero
            );
            if (handle.IsInvalid)
                return null;
            return GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfoClass,
                out var info,
                Marshal.SizeOf<FileAttributeTagInfo>()
            )
                ? info.ReparseTag
                : null;
        }
        catch (Exception ex)
            when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            // ネイティブが無い / パスが CreateFileW に渡せない形 = 判定不能。
            return null;
        }
    }

    /// <summary>タグが name surrogate(名前を横取りする種別)か。</summary>
    internal static bool IsNameSurrogate(uint tag) => (tag & NameSurrogateBit) != 0;
}
```

**確認すべき既知の risk 2 点:**

1. `dwDesiredAccess` に `FILE_READ_ATTRIBUTES` を使っている。策定時の実測プローブは
   `GENERIC_READ` で通した。もし `handle.IsInvalid` でテストが落ちるなら `GENERIC_READ`
   (`0x80000000`)へ変え、**変えた事実と理由をクラス doc に書く**。
2. `[DllImport]` に対して `SYSLIB1054`(`LibraryImport` を推奨)が `-warnaserror` で
   エラーになる可能性。`src/kxEdit.Editor/NativeMethods.cs` が同じ `[DllImport]` 形式で
   ビルドを通しているので通る見込みだが、落ちたら `docs/lint-format-setup.md` の
   抑止規約に従うか `[LibraryImport]` へ変える。

### Step 1.5: テストが通ることを確認

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~ReparseTagReaderTests"
```
Expected: PASS(全件)。`TryRead_ReturnsTag_ForCustomReparsePoint` が skip 経路(early return)で
通っていないか、一時的に `Assert.True(created)` を入れて**実際に fixture が作れていること**を
確認してから元に戻す。**skip したまま緑を「網がある」と読まないこと。**

### Step 1.6: Commit

```bash
git add src/kxEdit.Core/IO/ReparseTagReader.cs tests/kxEdit.Core.Tests/IO/
git commit -m "feat(core): reparse タグの name surrogate 判定を足す(A-15 の土台)"
```

### Step 1.7: 脆弱性レビュー(前倒し・CLAUDE.md §3-4)

別エージェントで実施する。観点:
- `TryRead` が失敗を `null` で返し切っているか(例外が漏れて呼出側の外側 catch に流れないか)。
- `FILE_FLAG_OPEN_REPARSE_POINT` が抜けると何が起きるか(リンク先を開いてしまう / hydrate する)。
- 名前 surrogate ビットの定数が `0x20000000` であること。
- fixture が作る reparse point が、判定したい実物(クラウドプレースホルダー)と
  **どの点で同じでどの点で違うか**を明示できているか。

---

## Task 2: `OriginalPathValidator` をタグ判定へ切り替える(A-15 本体)

**Files:**
- Modify: `src/kxEdit.Core/Backup/OriginalPathValidator.cs`(`RejectIfReparsePresent` の fast path)
- Modify: `tests/kxEdit.Core.Tests/Backup/OriginalPathValidatorTests.cs`

### Step 2.1: 失敗するテストを書く

`OriginalPathValidatorTests.cs` の末尾に追加:

```csharp
    [Fact]
    public void Check_ReturnsOk_ForNonSurrogateReparsePoint()
    {
        // A-15 本体: クラウドプレースホルダー(Microsoft タグだが name surrogate ではない)と
        // 同じ形の reparse point は、名前を横取りしないので Ok でなければならない。
        // 属性ビットだけを見ていた旧実装はここで Rejected を返す。
        var dir = kxEdit.Core.Tests.IO.ReparsePointFixture.CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "cloudish.txt");
            File.WriteAllText(file, "");
            if (
                !kxEdit.Core.Tests.IO.ReparsePointFixture.TryCreate(
                    file,
                    kxEdit.Core.Tests.IO.ReparsePointFixture.NonSurrogateTag
                )
            )
                return; // Skip: reparse point を作れない環境

            Assert.Equal(PathValidation.Ok, OriginalPathValidator.Check(file, out _));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Check_Rejects_NameSurrogateReparsePoint()
    {
        // 対照群: 同じ経路でも name surrogate ビットが立っていれば Rejected(挙動不変)。
        // この 2 本が対になって初めて「ビットで分けている」ことの証人になる。
        var dir = kxEdit.Core.Tests.IO.ReparsePointFixture.CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "surrogate.txt");
            File.WriteAllText(file, "");
            if (
                !kxEdit.Core.Tests.IO.ReparsePointFixture.TryCreate(
                    file,
                    kxEdit.Core.Tests.IO.ReparsePointFixture.SurrogateTag
                )
            )
                return; // Skip

            Assert.Equal(PathValidation.Rejected, OriginalPathValidator.Check(file, out _));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Check_Rejects_WhenParentIsNonSurrogateReparsePointButLeafIsSurrogate()
    {
        // walk が leaf だけでなく親も見ていることを、タグ判定へ変えた後も固定する。
        // 親を非 surrogate にしておくことで「親で早期に Rejected して通っただけ」を排除する。
        var dir = kxEdit.Core.Tests.IO.ReparsePointFixture.CreateTempDir();
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(dir, "sub")).FullName;
            if (!kxEdit.Core.Tests.IO.ReparsePointFixture.TryCreate(sub, ReparsePointFixtureNonSurrogate()))
                return; // Skip
            var file = Path.Combine(sub, "leaf.txt");
            // leaf は作れない(sub が reparse point になっているため)。親のみで判定されることを見る。
            Assert.Equal(PathValidation.Ok, OriginalPathValidator.Check(file, out _));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        static uint ReparsePointFixtureNonSurrogate() =>
            kxEdit.Core.Tests.IO.ReparsePointFixture.NonSurrogateTag;
    }
```

> **注意:** 3 本目はディレクトリに reparse point を設定できるかが不確実
> (`FSCTL_SET_REPARSE_POINT` はディレクトリにも使えるが、空である必要がある等の条件がある)。
> **実装時に成立しなければこのテストは落とし、代わりに「非 surrogate な親 + 通常の leaf」で
> walk が Ok を返すことだけを固定してよい。** その判断と理由をテストの doc コメントに残すこと。

### Step 2.2: テストが失敗することを確認

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~OriginalPathValidatorTests"
```
Expected: `Check_ReturnsOk_ForNonSurrogateReparsePoint` が FAIL
(`Rejected` が返る = A-15 の再現)。**この失敗を目で見ること**が A-15 の実在確認そのもの。

### Step 2.3: 実装を変える

`RejectIfReparsePresent` の fast path。現状:

```csharp
                var attrs = File.GetAttributes(cursor);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    return PathValidation.Rejected;
```

置き換え後:

```csharp
                var attrs = File.GetAttributes(cursor);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    // A-15: 属性ビットは「reparse point である」しか言わない。塞ぎたいのは
                    // 名前を BlockedRoot へ横取りされることなので、判定は name surrogate かどうかで行う。
                    // クラウドプレースホルダー / DEDUP / WOF は横取りしないので通す。
                    //
                    // ガードが開くのは「reparse point だが横取りしないと**積極的に判明した**」
                    // ときだけ。タグを読めなかった (null) 場合は従来どおり Rejected へ倒す
                    // = 「読めなかった」と「安全だと分かった」を混ぜない。
                    uint? tag = kxEdit.Core.IO.ReparseTagReader.TryRead(cursor);
                    if (tag is null || kxEdit.Core.IO.ReparseTagReader.IsNameSurrogate(tag.Value))
                        return PathValidation.Rejected;
                }
```

併せて `OriginalPathValidator` のクラス doc の該当箇所
(「OneDrive Files On-Demand 等 cloud placeholder は …… 将来検討」)を、
**解消済みとして書き換える**(doc は「現在」を説明しているので更新対象)。

### Step 2.4: テストが通ることを確認

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~OriginalPathValidatorTests"
```
Expected: PASS(既存 32 件 + 追加分)。特に `Check_Rejects_PathThroughJunction` が
**引き続き PASS** であること(junction の拒否は挙動不変)。

### Step 2.5: Commit

```bash
git add src/kxEdit.Core/Backup/OriginalPathValidator.cs tests/kxEdit.Core.Tests/Backup/OriginalPathValidatorTests.cs
git commit -m "fix(core): クラウドプレースホルダーの誤拒否を解消する(A-15)"
```

### Step 2.6: 脆弱性レビュー(前倒し)

別エージェントで実施する。観点:
- **junction / symlink / mount point が引き続き拒否されるか**(実際に作って確かめること)。
- `tag is null` の枝を消す変異(= 読めないときに通す)がテストで落ちるか。
- belt の `ResolveLinkTarget` 再照合が残っており、fast path の緩和で無効化されていないか。
- クラス doc の V-m-1 / V-m-2 / V-m-3 が主張する既知の穴が、本変更で広がっていないか。

---

## Task 3: reparse walk の対象をリモート全体へ(A-16-i)

**Files:**
- Modify: `src/kxEdit.Core/Backup/OriginalPathValidator.cs`(`Check` の skip 条件)
- Modify: `tests/kxEdit.Core.Tests/Backup/OriginalPathValidatorTests.cs`

### Step 3.1: 失敗するテストを書く

マップドネットワークドライブは自動テストで用意できないため、**述語の合成**を固定する。
`RemotePathDetector.IsRemote` が true を返すパスで walk が走らないことを、
「reparse point であっても Rejected にならない」で観測する……のは UNC でしか作れないので、
ここで固定できるのは **UNC の挙動不変**と、**ローカルの挙動不変**の 2 つになる。

```csharp
    [Fact]
    public void Check_ReturnsOk_ForUncPath_Unchanged()
    {
        // 挙動不変の pin: UNC は元から walk を skip して Ok。
        // skip 条件を IsUnc から IsRemote へ広げても、UNC 側は変わってはいけない。
        Assert.Equal(
            PathValidation.Ok,
            OriginalPathValidator.Check(@"\\server\share\docs\memo.txt", out _)
        );
    }

    [Fact]
    public void Check_Rejects_PathThroughJunction_OnLocalDrive_Unchanged()
    {
        // 既存の Check_Rejects_PathThroughJunction がこの役割を果たしているので、
        // 新規テストは足さない。既存テストが緑のままであることを確認する。
    }
```

> **正直に書く:** 「マップドドライブで walk が skip される」ことの直接の網は L1 では張れない。
> 張れるのは「`IsRemote` が true のとき walk を呼ばない」という**構造**の網だけで、
> それには `Check` から walk 呼び出しを観測できる seam が要る。**seam をこのために増やさない。**
> 代わりに (a) `RemotePathDetectorTests`(既存)が述語を固定し、(b) L5-2 が実機で観測する、
> という二段で担保する。**「網が張れない」と宣言する前に、上の 2 つで足りるかを必ず検討すること。**

### Step 3.2: 実装を変える

現状:

```csharp
            bool isUnc = forCheck.StartsWith(@"\\", StringComparison.Ordinal);
            if (!isUnc && RejectIfReparsePresent(forCheck) == PathValidation.Rejected)
                return PathValidation.Rejected;
```

置き換え後:

```csharp
            // A-16: walk の契約は元から「ローカルドライブのみ対象」で、根拠は
            // 「UNC はサーバ側 NTFS でクライアントから検査不能」だった。マップドネットワーク
            // ドライブ (Z:\) も実体はサーバ側にあるので同じ根拠がそのまま当てはまる。
            // ここを広げるのは性能上の回避ではなく**契約の食い違いの是正**であり、副次的に
            // 不達共有での同期 I/O(GetAttributes を root まで直列)を消す。
            //
            // 代償: マップドドライブ上の junction は拒否されなくなる。ただしクラス doc が
            // 「subst / ネットワークドライブ割当はドライブ文字の許可リストでは原理的に閉じない」
            // と受容済みで、UNC 側は元から未検査。受容範囲の**形**は変わらず、境界が
            // 「ドライブ文字」から「リモートかどうか」へ移るだけ。
            bool isRemote = kxEdit.Core.IO.RemotePathDetector.IsRemote(forCheck);
            if (!isRemote && RejectIfReparsePresent(forCheck) == PathValidation.Rejected)
                return PathValidation.Rejected;
```

### Step 3.3: テストが通ることを確認

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter "FullyQualifiedName~OriginalPathValidator"
```
Expected: PASS(既存すべて + 追加分)。

### Step 3.4: Commit

```bash
git add src/kxEdit.Core/Backup/OriginalPathValidator.cs tests/kxEdit.Core.Tests/Backup/OriginalPathValidatorTests.cs
git commit -m "fix(core): reparse 検査の skip をリモート全体へ広げる(A-16 前半)"
```

---

## Task 4: `ProbeDirectoryExistsWithTimeout` seam を足す

**Files:**
- Modify: `src/kxEdit.App/Abstractions/IReachabilityProbe.cs`
- Modify: `src/kxEdit.App/FileReachabilityProbe.cs`
- Modify: `tests/kxEdit.App.Tests/Fakes/FakeReachabilityProbe.cs`
- Modify: `tests/kxEdit.App.Tests/FileReachabilityProbeTests.cs`

### Step 4.1: 失敗するテストを書く

`FileReachabilityProbeTests.cs` に追加:

```csharp
    [Fact]
    public void ProbeDirectoryExists_ReturnsTrue_ForExistingDirectory()
    {
        var probe = new FileReachabilityProbe();
        Assert.True(
            probe.ProbeDirectoryExistsWithTimeout(Path.GetTempPath(), TimeSpan.FromSeconds(5))
        );
    }

    [Fact]
    public void ProbeDirectoryExists_ReturnsFalse_ForMissingDirectory()
    {
        var probe = new FileReachabilityProbe();
        var missing = Path.Combine(Path.GetTempPath(), "kxedit_nope_" + Guid.NewGuid().ToString("N"));
        Assert.False(probe.ProbeDirectoryExistsWithTimeout(missing, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void RunDirectoryExistsProbe_TimesOut_ToFalse()
    {
        // フェイルセーフ値の網。work を差し替えて決定的にタイムアウトさせる。
        // 定数を true へ書き換える変異がここで落ちること(= 不達共有で「フォルダは在る」と
        // 読んで実 I/O へ進む退行を作らないこと)を固定する。
        bool result = FileReachabilityProbe.RunDirectoryExistsProbe(
            () =>
            {
                Thread.Sleep(2000);
                return true;
            },
            TimeSpan.FromMilliseconds(50)
        );
        Assert.False(result);
    }
```

### Step 4.2: テストが失敗することを確認

```bash
dotnet build kxEdit.sln -c Release -warnaserror
```
Expected: FAIL — メソッドが存在しないためコンパイルエラー。

### Step 4.3: 実装を書く

`IReachabilityProbe` に追加:

```csharp
    /// <summary>
    /// フォルダーの存在を境界付きで確認する(A-17)。存在を確認できた = true /
    /// タイムアウト・到達不可・未存在 = false。
    /// <see cref="ProbeFileExistsWithTimeout"/> のフォルダー版で、意味論も同じく
    /// 「未存在」と「到達不能」を区別しない。grep のフォルダー指定は
    /// 「そこを検索できるか」だけを問うので、この粗さで足りる。
    /// </summary>
    bool ProbeDirectoryExistsWithTimeout(string path, TimeSpan timeout);
```

`FileReachabilityProbe` に追加:

```csharp
    /// <summary>
    /// フォルダー存在プローブの骨格。<paramref name="work"/> をバックグラウンドへ退避し、
    /// 期限内に終わらなければ「存在を確認できなかった」= false へ倒す。
    /// フェイルセーフ値をここに置く理由は <see cref="RunFileExistsProbe"/> と同じ:
    /// <c>WaitBounded(task, timeout, false)</c> と直書きすると定数が 1 トークンの引数でしかなく、
    /// true へ書き換えてもコンパイルが通り・ハングもせず・全緑になってしまう
    /// (= タイムアウトを「フォルダは在る」と読み、切断済み共有で grep 本体へ進んで
    /// UI が 60 秒凍結する A-17 の再導入)。
    /// </summary>
    internal static bool RunDirectoryExistsProbe(Func<bool> work, TimeSpan timeout) =>
        WaitBounded(Task.Run(work), timeout, false);

    /// <inheritdoc />
    public bool ProbeDirectoryExistsWithTimeout(string path, TimeSpan timeout) =>
        RunDirectoryExistsProbe(
            () =>
            {
                try
                {
                    return Directory.Exists(path);
                }
                catch
                {
                    // Directory.Exists は通常例外を投げないが、UNC 未到達などで稀に
                    // IOException 系が出る可能性を吸って false 扱いにする
                    // (ProbeFileExistsWithTimeout と同方針)。
                    return false;
                }
            },
            timeout
        );
```

`FakeReachabilityProbe` に追加:

```csharp
    /// <summary><c>ProbeDirectoryExistsWithTimeout</c> の応答。<see cref="Result"/> とは
    /// **独立**に設定できる必要がある(ファイルは在るがフォルダーは不達、の形を作れるように)。</summary>
    public bool DirectoryResult { get; set; } = true;

    public int DirectoryCallCount { get; private set; }

    /// <summary>直近の <c>ProbeDirectoryExistsWithTimeout</c> 呼出で渡された path。</summary>
    public string? DirectoryLastPath { get; private set; }

    /// <summary>直近の <c>ProbeDirectoryExistsWithTimeout</c> 呼出で渡された timeout(5s 契約の pin)。</summary>
    public TimeSpan DirectoryLastTimeout { get; private set; }

    public bool ProbeDirectoryExistsWithTimeout(string path, TimeSpan timeout)
    {
        DirectoryCallCount++;
        DirectoryLastPath = path;
        DirectoryLastTimeout = timeout;
        return DirectoryResult;
    }
```

### Step 4.4: テストが通ることを確認

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~FileReachabilityProbeTests"
```
Expected: PASS

### Step 4.5: Commit

```bash
git add src/kxEdit.App/Abstractions/IReachabilityProbe.cs src/kxEdit.App/FileReachabilityProbe.cs tests/kxEdit.App.Tests/
git commit -m "feat(app): フォルダー存在の境界付きプローブを足す(A-17 の土台)"
```

### Step 4.6: コード品質レビュー(前倒し・CLAUDE.md §3-4)

新しい seam を足すため。観点:
- 既存 3 本との対称性(フェイルセーフ値の置き場所・catch 方針・XML doc の粒度)。
- `RunDirectoryExistsProbe` のフェイルセーフ定数を `true` へ変える変異が
  **実際に落ちるか**(Step 4.1 の 3 本目を手で変異させて確認する)。
- `FakeReachabilityProbe` の既定値が、既存テストの意味を変えていないか。

---

## Task 5: grep の 2 か所を境界付きにする(A-17 本体)

**Files:**
- Create: `src/kxEdit.App/RemoteAwareDirectory.cs`
- Modify: `src/kxEdit.App/GrepController.cs:90` 付近
- Modify: `src/kxEdit.App/GrepDialog.cs:48`(ctor)/ `:123-131`(`BrowseFolder`)
- Modify: `src/kxEdit.App/MainForm.cs:191`(必要なら)
- Modify: `tests/kxEdit.App.Tests/GrepControllerTests.cs`(既存があれば。無ければ Create)

### Step 5.1: 失敗するテストを書く

```csharp
    [Fact]
    public async Task RunAsync_RemoteUnreachableFolder_NotifiesAndDoesNotSearch()
    {
        // A-17: 不達のリモートフォルダーは 5 秒プローブで打ち切り、grep 本体へ進まない。
        // (進むと GrepService が実 I/O に入り UI が 60 秒返らない)
        var probe = new FakeReachabilityProbe { DirectoryResult = false };
        // ... 既存 GrepControllerTests の組み立てに合わせて controller を作る。
        // view.Folder = @"\\unreachable-host\share" (UNC = IsRemote が true)

        await controller.RunAsync();

        Assert.Contains("フォルダが見つかりません", view.LastNotification);
        Assert.Equal(0, searchCallCount);
        Assert.Equal(TimeSpan.FromSeconds(5), probe.DirectoryLastTimeout);
    }

    [Fact]
    public async Task RunAsync_LocalFolder_DoesNotProbe()
    {
        // ローカルは挙動不変 = プローブを通さない(退避スレッドを作らない)。
        var probe = new FakeReachabilityProbe();
        // view.Folder = Path.GetTempPath()

        await controller.RunAsync();

        Assert.Equal(0, probe.DirectoryCallCount);
    }
```

> **実装者へ:** `GrepControllerTests` の既存の組み立て方(fake view / searchFn の差し替え)を
> 先に読んでから書くこと。上のコードは**骨だけ**で、そのままでは通らない。

### Step 5.2: テストが失敗することを確認

```bash
dotnet build kxEdit.sln -c Release -warnaserror
```
Expected: FAIL(コンパイルエラー or アサーション失敗)

### Step 5.3: 共通ヘルパを作る

`src/kxEdit.App/RemoteAwareDirectory.cs`:

```csharp
using System.IO;
using kxEdit.Core.IO;

namespace kxEdit.App;

/// <summary>
/// A-17: 「フォルダーが在るか」を UI スレッドから聞くときの唯一の入口。
/// リモート(UNC / マップドネットワークドライブ)のときだけ境界付きプローブへ回し、
/// ローカルは <see cref="Directory.Exists"/> 直呼び = **挙動不変・退避スレッドも作らない**。
///
/// 2 呼出点(<c>GrepController.RunAsync</c> / <c>GrepDialog.BrowseFolder</c>)で
/// 同じ判断を繰り返さないために切り出す。タイムアウトの 5 秒は HIGH-6 / CSV-M-1 /
/// <see cref="FileTimestampProvider"/> と同じ契約。
/// </summary>
internal static class RemoteAwareDirectory
{
    /// <summary>HIGH-6 / CSV-M-1 と同じ 5 秒契約。</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    internal static bool Exists(IReachabilityProbe probe, string path) =>
        RemotePathDetector.IsRemote(path)
            ? probe.ProbeDirectoryExistsWithTimeout(path, ProbeTimeout)
            : Directory.Exists(path);
}
```

### Step 5.4: 呼出点を差し替える

`GrepController`:
- フィールド `private readonly IReachabilityProbe _probe;` を足す。
- コンストラクタ末尾の引数に `IReachabilityProbe? probe = null` を足し、
  `_probe = probe ?? new FileReachabilityProbe();`(`FileTimestampProvider` と同型)。
- `:90` の `if (!Directory.Exists(d.Folder))` を
  `if (!RemoteAwareDirectory.Exists(_probe, d.Folder))` へ。

`GrepDialog`:
- ctor を `public GrepDialog(GrepCallbacks callbacks, IAnnouncer announcer, IReachabilityProbe? probe = null)` に。
- フィールド `_probe` を持ち、`BrowseFolder` の `if (Directory.Exists(_folder.Text))` を
  `if (RemoteAwareDirectory.Exists(_probe, _folder.Text))` へ。
- `MainForm:191` の `new GrepDialog(cb, _announcer)` は既定引数で通るので**変更不要**。

### Step 5.5: テストが通ることを確認

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~Grep"
```
Expected: PASS

### Step 5.6: Commit

```bash
git add src/kxEdit.App/RemoteAwareDirectory.cs src/kxEdit.App/GrepController.cs src/kxEdit.App/GrepDialog.cs tests/kxEdit.App.Tests/
git commit -m "fix(app): grep のフォルダー確認を境界付きにする(A-17)"
```

---

## Task 6: 復元経路の正規化を境界付きにする(A-16-ii)

`OriginalPathValidator.Check` 入口の `Path.GetFullPath` は、正規化後のパスに `~` が含まれると
`GetLongPathName` を呼び、不達共有で約 21 秒ブロックする(Issue #48 / S-15)。
呼出側が既存の `NormalizePathWithTimeout` を先に通す。

**Files:**
- Modify: `src/kxEdit.App/FileController.cs`(`:968` / `:1238` / `:1326` の 3 呼出点)
- Modify: `tests/kxEdit.App.Tests/FileControllerTests.cs`

### Step 6.1: 失敗するテストを書く

```csharp
    [Fact]
    public void RestoreFromBackup_NormalizeTimesOut_FallsBackToUntitled()
    {
        // A-16 (ii): 正規化が期限内に終わらなければ Check を呼ばずに無題降格へ倒す
        // (無境界の GetFullPath を UI スレッドで走らせない)。
        var host = new Host();
        host.Probe.NormalizeResult = new PathNormalizeResult(PathNormalizeStatus.TimedOut, string.Empty);

        var doc = host.Controller.RestoreFromBackup(
            new BackupRecord { /* OriginalPath = リモート風のパス, Content = "x" */ }
        );

        Assert.Null(doc.State.Path);
        Assert.True(doc.State.UntitledNumber > 0);
    }

    [Fact]
    public void RestoreFromBackup_NormalizeOk_UsesNormalizedPath()
    {
        // 挙動不変の pin: 正常系では従来どおりパス付きで復元される。
        // 非既定の位置から見る = 入力を「正規化で形が変わるパス」にして、
        // Check へ渡っているのが**正規化後**であることを観測する。
    }
```

> **実装者へ:** `FileControllerTests` の `Host` ヘルパ(`:32` に `FakeReachabilityProbe Probe`)を
> 先に読むこと。`BackupRecord` の必須フィールドも既存テストから写す。

### Step 6.2: テストが失敗することを確認

```bash
dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~RestoreFromBackup_Normalize"
```
Expected: FAIL

### Step 6.3: 実装を変える

3 呼出点それぞれで、`OriginalPathValidator.Check(<path>, out var normalized)` の**手前**に
境界付き正規化を差し込む。パターン(`RestoreFromBackup` の例):

```csharp
            // A-16 (ii): Check 入口の Path.GetFullPath は、正規化後のパスに `~` が含まれると
            // GetLongPathName を呼び、不達共有で約 21 秒 UI を止める(Issue #48 / S-15)。
            // 先に境界付き正規化を通し、その出力を Check へ渡す。Check 自身の正規化は
            // 自衛として残す(再正規化の順序が load-bearing なので触らない)。
            // 正規化が確定しなかった場合は Check を呼ばず、既存の Rejected と同じ扱いにする
            // = 新しい分岐を増やさない。
            var norm = _reachabilityProbe.NormalizePathWithTimeout(rec.OriginalPath!, NormalizeTimeout);
            var status =
                norm.Status == PathNormalizeStatus.Ok
                    ? OriginalPathValidator.Check(norm.Full, out normalized)
                    : PathValidation.Rejected;
```

`normalized` の宣言位置を `out var` から事前宣言(`string normalized = string.Empty;`)へ
変える必要がある。**3 箇所とも同じ形に揃えること。**

### Step 6.4: テストが通ることを確認

```bash
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build --filter "FullyQualifiedName~FileControllerTests"
```
Expected: PASS(既存すべて + 追加分)。既存の復元系テストが緑のままであることが
「挙動不変」の証人。

### Step 6.5: Commit

```bash
git add src/kxEdit.App/FileController.cs tests/kxEdit.App.Tests/FileControllerTests.cs
git commit -m "fix(app): 復元経路の正規化を境界付きにする(A-16 後半 / S-15 の残り)"
```

---

## Task 7: ドキュメントと L5 チェックリスト

**Files:**
- Create: `docs/plans/2026-08-31-network-cloud-path-freeze-l5-checklist.md`
- Modify: `src/kxEdit.Core/Backup/OriginalPathValidator.cs`(クラス doc の受容記述を現状へ)

### Step 7.1: L5 チェックリストを起こす

設計書 §7 の 4 点を、実施可能な手順に落とす。各項目に
「操作 → 期待する画面 → **期待する発声**」を書く。ノウハウ:

- windows-mcp の `Snapshot` は `use_ui_tree=false` + スクリーンショットの座標読みで完走する。
- `Type` はバックスラッシュを取りこぼす。パス入力は `Clipboard` set → Ctrl+V。
- kxEdit はコマンドライン引数を受け取らない。Ctrl+O のダイアログ出現を
  スクリーンショットで確認してから打つ。
- NVDA スピーチビューアーで実発声を逐語検証する。

**項目 1(A-15)では、プレースホルダーの属性とタグを実測して設計書 §2.2 に追記する。**

### Step 7.2: 監査文書は書き換えない

`docs/plans/2026-08-22-v0.2-release-bug-audit.md` は策定時スナップショット(CLAUDE.md §8)。
**A-15 / A-16 / A-17 を「解消済み」と書き戻さない。** 解消の記録は本計画と PR に残す。

### Step 7.3: Commit

```bash
git add docs/plans/2026-08-31-network-cloud-path-freeze-l5-checklist.md src/kxEdit.Core/Backup/OriginalPathValidator.cs
git commit -m "docs(plans): L5 チェックリストを起こす"
```

---

## Task 8: 最終ブランチレビュー(2 パス)と品質ゲート

### Step 8.1: コード品質パス

**独立した別エージェント**を起動する(脆弱性パスと混載しない)。観点:
- P/Invoke の隔離が守られているか(Core の他所へ漏れていないか)。
- `RemoteAwareDirectory` が 2 呼出点で一貫して使われているか。
- Task 6 の 3 呼出点が同じ形になっているか。
- **ミューテーション検証のスポットチェック**は `ReparseTagReader.IsNameSurrogate` の
  ビット定数のみ(CLAUDE.md §4-A により I/O・イベント配線は対象外)。

### Step 8.2: 脆弱性パス

**別の独立したエージェント**を起動する。観点:
- A-15 の緩和で BlockedRoots へのバイパスが増えていないか
  (junction / symlink / mount point を実際に作って確かめる)。
- Task 3 でマップドドライブの検査を外したことの影響範囲。
- `OriginalPathValidator` クラス doc の V-m-1 / V-m-2 / V-m-3 の前提が動いていないか。
- Task 6 で正規化の**順序**が変わったことで、事後条件と BlockedRoots の照合対象が
  ずれていないか。

### Step 8.3: 指摘の反映

CLAUDE.md §4 に従い、3 択で明示する: ① fixup commit で修正 / ② PR description に記載して受容 /
③ 理由付き却下。**元 commit は書き換えず別 fixup commit で積む。**

### Step 8.4: 品質ゲート

```bash
pwsh tools/pre-merge-check.ps1
```
Expected: **EXIT 0**。0 warning を維持していること。

### Step 8.5: L5 実施

`docs/plans/2026-08-31-network-cloud-path-freeze-l5-checklist.md` の 4 項目を実施し、
結果を同ファイルに追記して commit する。**SR 経路に触れる変更なので省略しない。**

### Step 8.6: PR

```bash
git push -u origin feature/network-cloud-path-freeze
gh pr create --title "fix: ネットワーク/クラウド配下のパス喪失と UI 凍結(A-15 / A-16 / A-17)" --body "..."
```

PR description(日本語)に書くこと:
- 目的と、監査 §4 のどの項目を閉じたか。
- **受容したトレードオフ**: マップドドライブ上の junction を拒否しなくなること(§6-2)、
  正規化タイムアウトで無題降格が増えうること(§6-1)。
- レビュー経緯(前倒し 2 回 + 最終 2 パス)と、却下した指摘があればその理由。
- 申し送り: A-18 / V-2〜V-6 / `RemotePathDetector` 自体のコスト未実測(§6-4)。
