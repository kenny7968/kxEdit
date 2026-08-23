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
    public void PathNormalizeResult_default_is_TimedOut()
    {
        // ゼロ値をフェイルセーフ側に置く設計の pin。
        // enum の並びを入れ替える変異(Ok = 0 にする)をここで kill する。
        Assert.Equal(PathNormalizeStatus.TimedOut, default(PathNormalizeResult).Status);

        // Full も同じ原則に乗せる(I-2)。positional record
        // (record struct X(Status, string Full))へ戻す変異=default の Full が null になる変異を
        // ここで kill する。Task 3/4 の消費形は SanitizeForDisplay.OneLine(...) と
        // State.Path = ... なので、null が漏れると NRE か「Path が null の無題タブ」になる。
        Assert.Equal(string.Empty, default(PathNormalizeResult).Full);
    }

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

    // V-2 の網を seam 自身に持たせる。実害は「Invalid を返さない」では済まない: フィルタ外の
    // 例外は work から task の fault として出て、WaitBounded がそれを投げ直すので
    // **UI スレッドの未捕捉例外ダイアログ**になる(= PR #47 の V-2 が戻る)。
    // FileControllerTests 側の V-2 網は Task 3 で 2 段の間接経由になるため、フィルタが住んでいる
    // このファイルに直接の網を置く。
    //
    // **2 つの窓を両方回すのが load-bearing**(再レビュー I-1)。40,000 文字 1 本だけだと
    // `or IOException` → `or PathTooLongException` と**狭める**変異が全緑で生存し、
    // PR #47 の V-2 が直したまさにその窓(素の IOException)は無網のまま残る。
    // Path.GetFullPath の実測マップ(net9.0.8。CWD 長 106 と 157 の両方で同一):
    //   入力長 32765 → 素の System.IO.IOException
    //   入力長 32766 → 素の System.IO.IOException   ← V-2 の窓。GetFullPathNameW が
    //                                                 ERROR_INVALID_NAME を返す
    //   入力長 32767 → PathTooLongException         ← ここからマネージド事前検査(Win32 に届かない)
    //   入力長 40000 → PathTooLongException
    // 変異 `or IOException` → `or PathTooLongException` を当てると、32766 のケースだけが
    // 「System.IO.IOException : ファイル名、ディレクトリ名、またはボリューム ラベルの構文が
    // 間違っています」を呼出側へ漏らして赤になる(40000 のケースは緑のまま)= 上表の実地確認。
    //
    // **上端は入力長だけで決まり CWD 長に依存しない**: 総長 = CWD + 1 + 入力長 なので、入力長が
    // 32766 ならどんな CWD でも総長は 32767 を超える。ゆえに
    // FileControllerTests.SaveAs_OverLongPath_WarnsAndReopens の doc にある
    // **「素の IOException の窓は CWD 長に依存する fixture になるため自動テストにしない」という
    // PR #47 の判断は誤りだった** — 固定長 32766 で決定的に再現できる(Task 3 で当該 doc ごと回収する)。
    [Theory]
    [InlineData(32766)] // 素の IOException(V-2 の窓)
    [InlineData(40000)] // PathTooLongException(マネージド事前検査の窓)
    public void NormalizePath_OverLongPath_ReturnsInvalid(int length)
    {
        var result = new FileReachabilityProbe().NormalizePathWithTimeout(
            new string('a', length),
            Timeout
        );

        Assert.Equal(PathNormalizeStatus.Invalid, result.Status);
        Assert.Equal(string.Empty, result.Full);
    }

    // ===== 「timeout が実際に境界として使われる」ことの網 =====
    //
    // Run*Probe を直接叩くテストは work を差し替えられるので決定的だが、**公開メソッドが
    // その timeout を素通しで渡しているか**は別の網が要る。末尾の timeout を
    // Timeout.InfiniteTimeSpan へ替える変異は、それが無いと全緑で生存する
    // (正規化なら S-15 の 21 秒凍結、既存 2 本なら HIGH-6 の 60 秒凍結が丸ごと戻る)。
    //
    // 1 回の呼び出しでは決定的にならない: Task.Wait(TimeSpan.Zero) はスピンせず IsCompleted を
    // 返すだけだが、Task.Run 直後にプール側が先に完走することが**実測 20,000 回中 62 回
    // (0.31%)**ある。単発 assert は CI でフレークする。そこで「N 回中 1 回でもフェイルセーフ値」
    // を見る: 変異側(timeout 無視=無限待ち)は N 回とも本来値を返すので確実に kill でき、
    // 未変異側が N 回連続で完走する確率は 0.0031^N ≒ 0 で安定する。
    private const int ZeroTimeoutAttempts = 20;

    [Fact]
    public void NormalizePath_ZeroTimeout_FailsSafeToTimedOut()
    {
        var seen = new List<PathNormalizeStatus>();
        for (int i = 0; i < ZeroTimeoutAttempts; i++)
            seen.Add(
                new FileReachabilityProbe()
                    .NormalizePathWithTimeout(@"C:\Temp\a.txt", TimeSpan.Zero)
                    .Status
            );

        Assert.Contains(PathNormalizeStatus.TimedOut, seen);
    }

    [Fact]
    public void ProbeFileExists_ZeroTimeout_FailsSafeToNotFound()
    {
        // **本ブランチが作った穴ではない**が、既存 2 本にも同じ無網があったので同時に塞ぐ
        // (同じファイル内で同型に書けるため)。存在するファイルを渡すので、境界が
        // 効いていなければ 20 回とも true が返る。
        using var tmp = new TempDir();
        string path = tmp.File("a.txt");
        File2.WriteAllText(path, "x");

        var seen = new List<bool>();
        for (int i = 0; i < ZeroTimeoutAttempts; i++)
            seen.Add(new FileReachabilityProbe().ProbeFileExistsWithTimeout(path, TimeSpan.Zero));

        Assert.Contains(false, seen);
    }

    [Fact]
    public void ProbeSaveTarget_ZeroTimeout_FailsSafeToUnreachable()
    {
        // 同上(**本ブランチが作った穴ではない**)。到達可能なパスを渡すので、境界が
        // 効いていなければ 20 回とも Reachable=true が返る。
        using var tmp = new TempDir();

        var seen = new List<bool>();
        for (int i = 0; i < ZeroTimeoutAttempts; i++)
            seen.Add(
                new FileReachabilityProbe()
                    .ProbeSaveTargetWithTimeout(tmp.File("not-yet.txt"), TimeSpan.Zero)
                    .Reachable
            );

        Assert.Contains(false, seen);
    }

    private static bool ThrowsForWaitBoundedTest() => throw new InvalidOperationException("boom");

    [Fact]
    public void WaitBounded_FaultedTask_RethrowsOriginalExceptionType()
    {
        // I-6。NormalizePathWithTimeout の絞り込みフィルタを抜けた例外(=ロジックバグ)を、
        // 移設前と同じ姿=元の型のまま・元のスタックのまま呼出スレッドへ届けるための網。
        //
        // 実測 net9.0.8: faulted task では Wait() / Wait(TimeSpan) / Result の**いずれも**
        // 中身を AggregateException で包み、包まないのは GetAwaiter().GetResult() だけ。
        // それでも三項の Result 側だけを差し替えても直らない(条件の Wait が先に評価されて
        // そこで投げるため)。**「包むのは Result ではなく Wait のほう」と書いていたのは誤り**
        // だった(再レビュー I-2)。両方が包む。効かない理由は評価順。
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FileReachabilityProbe.WaitBounded(
                Task.Run(ThrowsForWaitBoundedTest),
                Timeout,
                onTimeout: false
            )
        );

        Assert.Equal("boom", ex.Message);

        // 型だけでなく**スタック**も保つこと(再レビュー I-3)。これが無いと
        // ExceptionDispatchInfo を素の `throw ex.InnerExceptions[0];` に替える変異が全緑で
        // 生存する — 型は保たれるがスタックが投げ直し地点にリセットされ、work 内のバグ地点が
        // 消える(= EDI をわざわざ選んだ理由そのものが失われる)。
        // null 合体は「StackTrace が null なら当然この assert は落ちる」の意で、緩和ではない。
        Assert.Contains(nameof(ThrowsForWaitBoundedTest), ex.StackTrace ?? string.Empty);
    }

    [Fact]
    public void PathNormalizeResult_default_equals_explicit_failsafe()
    {
        // m-3。「ゼロ値=フェイルセーフ値」を等値でも成立させる、手書き Equals/GetHashCode の pin。
        // この 2 行を消して自動生成へ戻すと、backing field(null)と string.Empty が別物として
        // 比較され赤になる。**positional 版でも同じく等値にならないので、これは手書き化の代償では
        // ない**(実測)。
        var failSafe = new PathNormalizeResult(PathNormalizeStatus.TimedOut, string.Empty);

        Assert.Equal(failSafe, default(PathNormalizeResult));
        Assert.Equal(failSafe.GetHashCode(), default(PathNormalizeResult).GetHashCode());
    }

    [Fact]
    public void FakeNormalize_DefaultDelegatesToRealImplementation()
    {
        // Fake の既定が実装への委譲であることを pin する。素通しに変えると
        // 「正規化されたつもり」のテストが黙って通り、網が vacuous になる
        // (PR #47 の教訓)。Task 3 以降の V-2 網はこの委譲に依存する。
        var fake = new Fakes.FakeReachabilityProbe();

        var result = fake.NormalizePathWithTimeout("memo.txt", Timeout);

        Assert.Equal(PathNormalizeStatus.Ok, result.Status);
        Assert.True(System.IO.Path.IsPathFullyQualified(result.Full)); // 素通しなら "memo.txt" のまま
        Assert.Equal(1, fake.NormalizeCallCount);
    }
}
