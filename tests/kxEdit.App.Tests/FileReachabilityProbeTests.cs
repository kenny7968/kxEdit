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
        // A-4 の核。読み取り側の ProbeFileExistsWithTimeout(File.Exists 意味論)はここで false を返し、
        // 「ネットワークパスに到達できません」でネットワーク共有への新規保存を止めていた。
        // Reachable を `fileExists && dirExists` に変異させるとこのテストが kill する。
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
        // 親フォルダーが無ければ到達不能。親存在チェックを潰す変異
        // (`!IsNullOrEmpty(dir) && Directory.Exists(dir)` → `||` 等)を kill する。
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
        // このファイルで唯一 Path.GetDirectoryName が null を返す入力であり、
        // 「親が無ければパス自身にフォールバック」(`GetDirectoryName(path) ?? path`)という
        // 書き損じを kill するのはこのテストだけ(そう書くとルートが到達可能になる)。
        // ローカルパスをハードコードしない(pre-commit の no-local-paths 対策)ため
        // 一時フォルダのルートから導出する。
        using var tmp = new TempDir();
        string root = System.IO.Path.GetPathRoot(tmp.Root)!;
        Assert.True(Directory.Exists(root)); // 前提の自己検証(root が空なら以下は無意味)

        var result = new FileReachabilityProbe().ProbeSaveTargetWithTimeout(root, Timeout);

        Assert.False(result.Reachable);
        Assert.False(result.FileExists);
    }

    [Fact]
    public void ProbeSaveTarget_ExistingDirectory_ReportedAsNewFile_CurrentBehavior()
    {
        // 現状記録(意図した仕様ではない)。既存フォルダーのパスを保存先に渡すと
        // File.Exists=false・親フォルダー存在=true となり「新規ファイル・確認不要」と答える。
        // 設計書 §6(非目標)/ 申し送り S-1 が承知で範囲外にしている挙動。
        // S-1(保存先がフォルダーのときの扱い)を回収するときは、このテストが比較対象になる。
        using var tmp = new TempDir();

        var result = new FileReachabilityProbe().ProbeSaveTargetWithTimeout(tmp.Root, Timeout);

        Assert.True(result.Reachable);
        Assert.False(result.FileExists);
    }

    // ===== 境界付き待ちのフェイルセーフ(I-1 / I-3) =====
    // 実 I/O 経由でタイムアウトを起こすテストはフレーキーなので、待ちの判断だけを
    // WaitBounded / Run*Probe に切り出して決定的に検証する。
    //
    // 以下のタイムアウト系テストは「完了しないタスク」を渡して決定化している。そのため
    // 「タイムアウト経路で task.Result を読む」型の変異(三項の反転など)は red ではなく
    // **ハング**として現れる(xunit に既定のテストタイムアウトが無い)。ミューテーション検証で
    // 応答が返らなくなったら環境問題ではなく kill と読むこと。

    [Fact]
    public void WaitBounded_Timeout_ReturnsFailSafeValue()
    {
        // 一度も SetResult しない TCS なので Wait(Zero) は決定的に false(レースなし)。
        var never = new TaskCompletionSource<SaveTargetProbeResult>();

        var result = FileReachabilityProbe.WaitBounded(
            never.Task,
            TimeSpan.Zero,
            new SaveTargetProbeResult(Reachable: false, FileExists: false)
        );

        Assert.False(result.Reachable);
        Assert.False(result.FileExists); // タイムアウトを「未存在」と読ませない
    }

    [Fact]
    public void WaitBounded_Completed_ReturnsTaskResult()
    {
        // 対照群。フェイルセーフ値を常に返す実装(= onTimeout を無条件 return)を kill する。
        var done = Task.FromResult(new SaveTargetProbeResult(Reachable: true, FileExists: true));

        var result = FileReachabilityProbe.WaitBounded(
            done,
            TimeSpan.Zero,
            new SaveTargetProbeResult(Reachable: false, FileExists: false)
        );

        Assert.True(result.Reachable);
        Assert.True(result.FileExists);
    }

    [Fact]
    public void RunSaveTargetProbe_WorkExceedsTimeout_FailsSafeToUnreachable()
    {
        // I-1 の本体。WaitBounded の 2 本だけでは「フェイルセーフ値そのもの」が無被覆で、
        // (false,false) → (true,false) の変異が生存していた(= タイムアウトを
        // 「到達可能・未存在」と読み、Task 7 の上書き確認をスキップして無確認上書きになる)。
        // work を gate で止めるので Wait は決定的にタイムアウトする(レースなし)。
        // work は (true,true) を返すので、結果が (false,false) ならフェイルセーフ由来と確定する。
        var gate = new TaskCompletionSource();
        try
        {
            var result = FileReachabilityProbe.RunSaveTargetProbe(
                () =>
                {
                    gate.Task.Wait();
                    return new SaveTargetProbeResult(Reachable: true, FileExists: true);
                },
                TimeSpan.FromMilliseconds(50)
            );

            Assert.False(result.Reachable);
            Assert.False(result.FileExists);
        }
        finally
        {
            gate.SetResult(); // 退避スレッドを解放する(テスト後に leak させない)
        }
    }

    [Fact]
    public void RunFileExistsProbe_WorkExceedsTimeout_FailsSafeToNotFound()
    {
        // I-3。読み取り側のフェイルセーフ false → true の変異は、この 1 本が無いと全緑で生存する
        // (= タイムアウトを「ファイルは在る」と読み、切断済み UNC で実 read へ進んで
        // UI が 60 秒凍結する HIGH-6 の再導入)。組み方は保存側と対称:
        // work は true を返すので、false が返ったならフェイルセーフ由来と確定する。
        var gate = new TaskCompletionSource();
        try
        {
            bool result = FileReachabilityProbe.RunFileExistsProbe(
                () =>
                {
                    gate.Task.Wait();
                    return true;
                },
                TimeSpan.FromMilliseconds(50)
            );

            Assert.False(result);
        }
        finally
        {
            gate.SetResult(); // 退避スレッドを解放する(テスト後に leak させない)
        }
    }

    [Fact]
    public void RunFileExistsProbe_WorkCompletes_ReturnsWorkResult()
    {
        // 対照群。RunFileExistsProbe が常に false を返す実装を kill する
        // (これが無いと「常にフェイルセーフ」が上のテストだけでは通ってしまう)。
        Assert.True(FileReachabilityProbe.RunFileExistsProbe(() => true, Timeout));
    }

    [Fact]
    public void RunSaveTargetProbe_WorkCompletes_ReturnsWorkResult()
    {
        // 対照群。RunSaveTargetProbe が常にフェイルセーフ値を返す実装を kill する。
        var result = FileReachabilityProbe.RunSaveTargetProbe(
            () => new SaveTargetProbeResult(Reachable: true, FileExists: true),
            Timeout
        );

        Assert.True(result.Reachable);
        Assert.True(result.FileExists);
    }

    // ===== 境界付き正規化(Issue #48 / 設計書 §4)=====

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
        // ゼロ値をフェイルセーフ側に置く設計の pin。
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
        // 実実装の例外フィルタ。NUL 混入は ArgumentException(PR #47 Task 5 の実測)。
        // Invalid と TimedOut を弁別する(同じ値にする変異をここで kill する)。
        var result = new FileReachabilityProbe().NormalizePathWithTimeout("a\0b", Timeout);

        Assert.Equal(PathNormalizeStatus.Invalid, result.Status);
        Assert.Equal(string.Empty, result.Full);
    }
}
