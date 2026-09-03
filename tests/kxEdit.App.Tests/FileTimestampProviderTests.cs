using System.IO;
using kxEdit.App.Tests.Fakes;

namespace kxEdit.App.Tests;

/// <summary>
/// <see cref="FileTimestampProvider"/> の実 I/O 契約(設計 2026-08-22 §4.3)。
/// Fake で固定値を返すテストでは「実装が本当に null を返すか」を検証できないため、
/// 実ファイルで存在/不在/不正パスの 3 分岐だけを固定する(FakeReachabilityProbe と同じ思想)。
/// </summary>
public class FileTimestampProviderTests
{
    [Fact]
    public void ExistingFile_ReturnsUtcTimestamp()
    {
        var dir = Directory.CreateTempSubdirectory("kxEditTs_").FullName;
        try
        {
            var path = Path.Combine(dir, "a.txt");
            var before = DateTime.UtcNow.AddMinutes(-1);
            File.WriteAllText(path, "x");

            var actual = new FileTimestampProvider().GetLastWriteTimeUtc(path);

            Assert.NotNull(actual);
            Assert.Equal(DateTimeKind.Utc, actual!.Value.Kind);
            Assert.True(actual.Value > before);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>不在は null。File.GetLastWriteTimeUtc をそのまま返すと 1601-01-01 になり、
    /// 「非常に古いディスク」として判定が黙って歪む。</summary>
    [Fact]
    public void MissingFile_ReturnsNull()
    {
        var dir = Directory.CreateTempSubdirectory("kxEditTs_").FullName;
        try
        {
            Assert.Null(
                new FileTimestampProvider().GetLastWriteTimeUtc(Path.Combine(dir, "missing.txt"))
            );
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>不正なパス文字列でも例外を投げず null を返す(復元経路を落とさない契約)。</summary>
    [Fact]
    public void InvalidPath_ReturnsNull_WithoutThrowing() =>
        Assert.Null(new FileTimestampProvider().GetLastWriteTimeUtc("::invalid::\0path"));

    // ===== レビュー H-1: リモートパスは 5 秒プローブを前置する =====

    /// <summary>ローカルパスにプローブを挟まない(常時 5 秒の遅延を持ち込まない)。</summary>
    [Fact]
    public void LocalPath_DoesNotProbe()
    {
        var dir = Directory.CreateTempSubdirectory("kxEditTs_").FullName;
        try
        {
            var path = Path.Combine(dir, "a.txt");
            File.WriteAllText(path, "x");
            var probe = new FakeReachabilityProbe();

            _ = new FileTimestampProvider(probe).GetLastWriteTimeUtc(path);

            Assert.Equal(0, probe.CallCount);
            Assert.Equal(0, probe.SaveTargetCallCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>UNC は 5 秒プローブを通す。<c>OriginalPathValidator</c> は isUnc のとき
    /// reparse 検査(唯一の I/O)をスキップするため、ここが復元経路で最初の同期 I/O になる
    /// = 切断済み共有で SMB タイムアウト(約 60 秒)まで UI が返らない。</summary>
    [Fact]
    public void UncPath_ProbesWithFiveSecondTimeout()
    {
        var probe = new FakeReachabilityProbe
        {
            SaveTargetResult = new(Reachable: false, FileExists: false),
        };

        var actual = new FileTimestampProvider(probe).GetLastWriteTimeUtc(
            @"\\unreachable-host\share\a.txt"
        );

        Assert.Null(actual); // 到達不能 = 判定しない(復元は従来どおり・M-18 は聞かない)
        Assert.Equal(1, probe.SaveTargetCallCount);
        Assert.Equal(TimeSpan.FromSeconds(5), probe.SaveTargetLastTimeout);
    }

    /// <summary>同じ共有上の 2 件目以降はプローブし直さない。起動時復元は同一共有の文書を
    /// 何件も含みうるため、憶えないと「5 秒 × レコード数」が積み上がる(H-1 の増幅点)。</summary>
    [Fact]
    public void UnreachableUncRoot_IsProbedOnlyOnce()
    {
        var probe = new FakeReachabilityProbe
        {
            SaveTargetResult = new(Reachable: false, FileExists: false),
        };
        var sut = new FileTimestampProvider(probe);

        Assert.Null(sut.GetLastWriteTimeUtc(@"\\unreachable-host\share\a.txt"));
        Assert.Null(sut.GetLastWriteTimeUtc(@"\\unreachable-host\share\b.txt"));
        Assert.Null(sut.GetLastWriteTimeUtc(@"\\unreachable-host\share\sub\c.txt"));

        Assert.Equal(1, probe.SaveTargetCallCount);
    }

    /// <summary>別の共有は記録を共有しない(1 つが落ちていても他は判定する)。</summary>
    [Fact]
    public void UnreachableRoot_DoesNotSuppressOtherRoots()
    {
        var probe = new FakeReachabilityProbe
        {
            SaveTargetResult = new(Reachable: false, FileExists: false),
        };
        var sut = new FileTimestampProvider(probe);

        Assert.Null(sut.GetLastWriteTimeUtc(@"\\host-a\share\a.txt"));
        Assert.Null(sut.GetLastWriteTimeUtc(@"\\host-b\share\b.txt"));

        Assert.Equal(2, probe.SaveTargetCallCount);
    }

    // ===== M-18(設計 2026-09-03 §3.8): 到達不能の記憶は 60 秒で切れる =====

    /// <summary>プロセス寿命のままだと、一度落ちた共有の文書は再起動まで検知が黙って止まる。
    /// TTL 無しだと Alt+Tab のたびに 5 秒止まる。60 秒で「最悪 1 分に 1 回 5 秒」。</summary>
    [Fact]
    public void UnreachableRoot_IsProbedAgainAfterTtl()
    {
        var probe = new FakeReachabilityProbe
        {
            SaveTargetResult = new(Reachable: false, FileExists: false),
        };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero));
        var sut = new FileTimestampProvider(probe, clock);
        const string path = @"\\unreachable-host\share\a.txt";

        Assert.Null(sut.GetLastWriteTimeUtc(path));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(sut.GetLastWriteTimeUtc(path));
        Assert.Equal(1, probe.SaveTargetCallCount); // TTL 内は記憶が効く(既存テストの意味は保たれる)

        clock.Advance(TimeSpan.FromSeconds(2)); // 計 61 秒
        Assert.Null(sut.GetLastWriteTimeUtc(path));
        Assert.Equal(2, probe.SaveTargetCallCount); // 期限切れ → 再プローブ
    }

    /// <summary>TTL は ctor で差し替えられる(既定 60 秒が唯一の値ではないことの配線確認)。</summary>
    [Fact]
    public void UnreachableTtl_IsInjectable()
    {
        var probe = new FakeReachabilityProbe
        {
            SaveTargetResult = new(Reachable: false, FileExists: false),
        };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero));
        var sut = new FileTimestampProvider(probe, clock, unreachableTtl: TimeSpan.FromSeconds(5));
        const string path = @"\\unreachable-host\share\a.txt";

        Assert.Null(sut.GetLastWriteTimeUtc(path));
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Null(sut.GetLastWriteTimeUtc(path));

        Assert.Equal(2, probe.SaveTargetCallCount);
    }

    /// <summary>期限切れ後に到達できれば値の取得へ進む(記憶が到達可能を塞がない)。</summary>
    [Fact]
    public void UnreachableRoot_AfterTtl_ReachableAgain_ProceedsToRead()
    {
        var probe = new FakeReachabilityProbe
        {
            SaveTargetResult = new(Reachable: false, FileExists: false),
        };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero));
        var sut = new FileTimestampProvider(probe, clock);
        const string path = @"\\unreachable-host\share\a.txt";

        Assert.Null(sut.GetLastWriteTimeUtc(path));
        clock.Advance(TimeSpan.FromSeconds(61));
        probe.SaveTargetResult = new(Reachable: true, FileExists: true);

        // 到達できても実ファイルは無いので null。プローブが走ったこと(=読みに進んだこと)だけを見る。
        Assert.Null(sut.GetLastWriteTimeUtc(path));
        Assert.Equal(2, probe.SaveTargetCallCount);
    }

    /// <summary>脆弱性レビュー L-1: 到達できる共有上でファイルが無いだけならルートを記憶しない。
    /// 記憶すると、別ツールの delete→recreate や rename 保存の途中の一瞬で、その共有の全文書の検知が
    /// 60 秒黙って止まる。</summary>
    [Fact]
    public void ReachableRoot_MissingFile_ReturnsNull_WithoutRememberingRoot()
    {
        var probe = new FakeReachabilityProbe
        {
            SaveTargetResult = new(Reachable: true, FileExists: false),
        };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero));
        var sut = new FileTimestampProvider(probe, clock);

        Assert.Null(sut.GetLastWriteTimeUtc(@"\\reachable-host\share\gone.txt"));
        Assert.Null(sut.GetLastWriteTimeUtc(@"\\reachable-host\share\other.txt"));

        Assert.Equal(2, probe.SaveTargetCallCount); // 2 件目も記憶に阻まれずプローブされる
    }
}
