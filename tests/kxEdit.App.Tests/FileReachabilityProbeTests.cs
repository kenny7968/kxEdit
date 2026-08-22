using Directory = System.IO.Directory;
using File2 = System.IO.File;

namespace kxEdit.App.Tests;

/// <summary>
/// 本番プローブ <see cref="FileReachabilityProbe"/> の意味論テスト。
/// v0.2 監査 A-4 が「FakeReachabilityProbe で固定値を返すため実 Probe の意味論は未検証」と
/// 名指しした穴を塞ぐ。<c>Reachable = FileExists || 親フォルダー存在</c> の <c>||</c> を
/// kill できるのはこのファイルだけ(FileControllerTests は Fake 経由なので届かない)。
/// </summary>
public class FileReachabilityProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void ProbeSaveTarget_ExistingFile_ReachableAndExists()
    {
        using var tmp = new TempDir();
        string path = tmp.File("a.txt");
        File2.WriteAllText(path, "x");

        var result = new FileReachabilityProbe().ProbeSaveTargetWithTimeout(path, Timeout);

        Assert.True(result.Reachable);
        Assert.True(result.FileExists); // 上書き確認(A-7 (a))の入力
    }

    [Fact]
    public void ProbeSaveTarget_NewNameInExistingDir_ReachableAndNotExists()
    {
        // A-4 の核。旧 ProbeWithTimeout(File.Exists 意味論)はここで false を返し、
        // 「ネットワークパスに到達できません」でネットワーク共有への新規保存を止めていた。
        using var tmp = new TempDir();

        var result = new FileReachabilityProbe().ProbeSaveTargetWithTimeout(
            tmp.File("not-yet.txt"),
            Timeout
        );

        Assert.True(result.Reachable);
        Assert.False(result.FileExists);
    }

    [Fact]
    public void ProbeSaveTarget_UnderMissingDir_NotReachable()
    {
        using var tmp = new TempDir();

        var result = new FileReachabilityProbe().ProbeSaveTargetWithTimeout(
            System.IO.Path.Combine(tmp.Root, "no-such-dir", "a.txt"),
            Timeout
        );

        Assert.False(result.Reachable);
        Assert.False(result.FileExists);
    }

    [Fact]
    public void ProbeSaveTarget_DriveRoot_NotReachable()
    {
        // ルート自体("C:\")はファイルとして保存できない=親フォルダーが無い。
        // ローカルパスをハードコードしない(pre-commit の no-local-paths 対策)ため
        // 一時フォルダのルートから導出する。
        using var tmp = new TempDir();
        string root = System.IO.Path.GetPathRoot(tmp.Root)!;
        Assert.True(Directory.Exists(root)); // 前提の自己検証(root が空なら以下は無意味)

        var result = new FileReachabilityProbe().ProbeSaveTargetWithTimeout(root, Timeout);

        Assert.False(result.Reachable);
    }
}
