using kxEdit.Core.IO;

namespace kxEdit.Core.Tests.IO;

/// <summary>
/// CSV-M-1: マップドネットワークドライブ(X: → \\server\share)を UNC と同様に
/// 「リモート」判定する述語のテスト。UncPathDetector (先頭 `\\` の純粋文字列判定)を
/// 内包しつつ、DOS ドライブ文字の場合は <see cref="System.IO.DriveInfo.DriveType"/> が
/// Network なら true を返す拡張述語。
///
/// 実マップドドライブは CI で用意できないため、Network 経路の true を実データで
/// 検証することは行わない(実機/統合テストの領分)。ここでは以下を網羅する:
///  - UNC は UncPathDetector と対称に true(既存契約の維持)
///  - null / 空文字 / 相対パスは false(early return)
///  - 存在しないドライブ文字("Q:\\...")は false(silent fallback)
///  - ローカル固定ドライブ("C:\\...")は false(CI ランナー前提=Fixed)
///  - 不正パス文字は例外を握って false(silent fallback)
/// </summary>
public class RemotePathDetectorTests
{
    [Theory]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData(@"\\?\UNC\server\share\file.txt")]
    [InlineData(@"\\.\PhysicalDrive0")]
    public void IsRemote_UncPath_ReturnsTrue(string path)
    {
        // UNC は UncPathDetector と対称=先頭 `\\` の純粋判定が true を返す経路。
        // Network drive の追加チェック無しで直ちに true を返す(early true)。
        Assert.True(RemotePathDetector.IsRemote(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(@".\relative.txt")]
    [InlineData("relative.txt")]
    public void IsRemote_EmptyOrRelative_ReturnsFalse(string? path)
    {
        // 空文字/null は UncPathDetector 側で早期 false。相対パスは Path.GetPathRoot が
        // 空文字を返すため DriveInfo にかけずに false 返却(early return)。
        // null は null-forgiving でそのまま渡す(実装の null-safety 契約を documentation として pin する
        // =`?? ""` でコアレスすると実質「空文字を 2 回テスト」になり null 経路を検証できない)。
        Assert.False(RemotePathDetector.IsRemote(path!));
    }

    [Fact]
    public void IsRemote_NonExistentDriveLetter_ReturnsFalse()
    {
        // 存在しないドライブ文字は DriveInfo コンストラクタ自体は成功するが
        // (DriveInfo は「マップされていないドライブ」も受け入れる契約=DriveType=NoRootDirectory)、
        // DriveType が Network 以外(NoRootDirectory)なので false を返す。silent fallback。
        // Q:\ は多くの環境で未使用(CI ランナーでもマップされていない前提)。
        Assert.False(RemotePathDetector.IsRemote(@"Q:\foo\bar.txt"));
    }

    [Fact]
    public void IsRemote_LocalFixedDrive_ReturnsFalse()
    {
        // CI ランナー / 開発機の C:\ は Fixed(または CDRom / Removable)であり Network ではない。
        // 実マップドネットワークドライブが存在する環境で C:\ が Network になることはないため、
        // この検証は robust。挙動: HIGH-6 の「ローカルはプローブスキップ」を維持することが目的。
        Assert.False(RemotePathDetector.IsRemote(@"C:\Windows\System32\notepad.exe"));
    }

    [Theory]
    [InlineData(":::")]
    [InlineData("<>")]
    [InlineData("con:")]
    public void IsRemote_InvalidPathCharacters_ReturnsFalse(string path)
    {
        // 不正パス文字は Path.GetPathRoot / DriveInfo が ArgumentException 等を投げる可能性。
        // 呼出側(FileController.LoadInto)は try/catch を持たない設計なので、ここで silent
        // fallback して false を返す(=プローブスキップ→通常経路へ→ TextFileService 側で
        // 適切な IOException を発火させる)。silent fallback は
        // 「不明パスは『非リモート』扱いにして既存経路へ落とす」意図。
        Assert.False(RemotePathDetector.IsRemote(path));
    }
}
