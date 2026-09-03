using System.IO;
using kxEdit.App.Tests.Fakes;

namespace kxEdit.App.Tests;

/// <summary>
/// <see cref="FileTimestampProvider"/> の契約(設計 2026-08-22 §4.3 / M-18 設計 2026-09-03 §3.8)。
/// 実ファイルの 3 本(存在/不在/不正パス)は、Fake で固定値を返すテストでは「実装が本当に null を
/// 返すか」を検証できないため実 I/O で固定する。リモート経路(H-1 のプローブ前置)と到達不能記憶の
/// TTL は <see cref="FakeReachabilityProbe"/> / <see cref="FakeTimeProvider"/> 駆動で固定する
/// (リモートのテストはすべて Fake が「到達不能」か「到達できるが不在」を返す形にし、実 SMB へは触れない)。
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

            var sut = new FileTimestampProvider();
            var actual = sut.GetLastWriteTimeUtc(path);

            Assert.NotNull(actual);
            Assert.Equal(DateTimeKind.Utc, actual!.Value.Kind);
            Assert.True(actual.Value > before);
            Assert.Equal(actual, sut.ProbeLastWriteTimeUtc(path)); // 記憶を使わない側も同じ核を通る
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

    /// <summary>ローカルパスにプローブを挟まない(常時 5 秒の遅延を持ち込まない)。記憶を使わない側も同じ。</summary>
    [Fact]
    public void LocalPath_DoesNotProbe()
    {
        var dir = Directory.CreateTempSubdirectory("kxEditTs_").FullName;
        try
        {
            var path = Path.Combine(dir, "a.txt");
            File.WriteAllText(path, "x");
            var probe = new FakeReachabilityProbe();
            var sut = new FileTimestampProvider(probe);

            _ = sut.GetLastWriteTimeUtc(path);
            _ = sut.ProbeLastWriteTimeUtc(path);

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
    /// TTL 無しだと Alt+Tab のたびに 5 秒止まる。60 秒で「最悪 1 分に 1 回 5 秒」。
    /// 期限ちょうど(60 秒)で再プローブし、まだ到達不能なら再記憶される(61 秒では再び抑止)。</summary>
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

        clock.Advance(TimeSpan.FromSeconds(1)); // 計 60 秒 = 期限ちょうど → 再プローブ
        Assert.Null(sut.GetLastWriteTimeUtc(path));
        Assert.Equal(2, probe.SaveTargetCallCount); // 期限切れ → 再プローブ(まだ到達不能なので再記憶)

        clock.Advance(TimeSpan.FromSeconds(1)); // 計 61 秒: 60 秒時点で再記憶されたので抑止
        Assert.Null(sut.GetLastWriteTimeUtc(path));
        Assert.Equal(2, probe.SaveTargetCallCount);
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

    /// <summary>期限切れ後に到達できれば、記憶が到達可能を塞がないことを固定する
    /// (読みに進んだかは FileExists ゲートがあるので観測できない。プローブが走ったことだけを見る)。
    /// Fake は「到達できるが不在」を返す = FileExists ゲートで止まり、実 <c>File.Exists</c>(実 SMB)へは
    /// 進まない(最終コード品質レビュー Q-4: 以前は (true, true) で実 I/O へ到達していた)。パスは当時の
    /// 名残で即答する <c>\\localhost\</c> の存在しない共有のまま(<c>IsRemote</c> は先頭 <c>\\</c> で true。
    /// 今は I/O が起きないので何でもよい)。</summary>
    [Fact]
    public void UnreachableRoot_AfterTtl_ReachableAgain_IsNotBlockedByMemo()
    {
        var probe = new FakeReachabilityProbe
        {
            SaveTargetResult = new(Reachable: false, FileExists: false),
        };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero));
        var sut = new FileTimestampProvider(probe, clock);
        const string path = @"\\localhost\kxedit-no-such-share\a.txt";

        Assert.Null(sut.GetLastWriteTimeUtc(path));
        clock.Advance(TimeSpan.FromSeconds(61));
        probe.SaveTargetResult = new(Reachable: true, FileExists: false);

        // 到達できても不在なので null。プローブが走ったこと(=記憶に塞がれなかったこと)だけを見る。
        Assert.Null(sut.GetLastWriteTimeUtc(path));
        Assert.Equal(2, probe.SaveTargetCallCount);
    }

    /// <summary>復旧を確認した根の記憶は明示的に捨てる(<c>Remove</c>)。捨てないと、期限切れ後に復旧を
    /// 確認した後で壁時計が逆行した(手動変更・大幅 NTP 補正)とき、期限切れの記録が復活して再び抑止される。
    /// 一般命題としては閉じていない(期限切れ後に一度も再照会されていない根は until を持ったまま残る。
    /// 設計 §11.5「壁時計の逆行」)。</summary>
    [Fact]
    public void UnreachableRoot_RecoveredThenClockRegresses_IsNotSuppressed()
    {
        var probe = new FakeReachabilityProbe
        {
            SaveTargetResult = new(Reachable: false, FileExists: false),
        };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero));
        var sut = new FileTimestampProvider(probe, clock);
        const string path = @"\\unreachable-host\share\a.txt";

        Assert.Null(sut.GetLastWriteTimeUtc(path)); // 到達不能 → 記憶(期限 = +60 秒)
        clock.Advance(TimeSpan.FromSeconds(61));
        probe.SaveTargetResult = new(Reachable: true, FileExists: false);
        Assert.Null(sut.GetLastWriteTimeUtc(path)); // 期限切れ → 再プローブ → 復旧 → 記憶を捨てる(不在なので null)
        Assert.Equal(2, probe.SaveTargetCallCount);

        clock.Advance(TimeSpan.FromSeconds(-40)); // 壁時計の逆行: 計 21 秒 = 捨てていなければ期限(60 秒)内に戻る

        Assert.Null(sut.GetLastWriteTimeUtc(path));
        Assert.Equal(3, probe.SaveTargetCallCount); // Remove が無ければ 2 のまま(復活した記録に抑止される)
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

    // ===== 最終脆弱性レビュー V-1: 基準を捕捉する経路は到達不能記憶を素通りする =====

    /// <summary>記憶が効いている間でも <c>ProbeLastWriteTimeUtc</c> はプローブし、到達できたら記憶を捨てる
    /// (以後の <c>GetLastWriteTimeUtc</c> も TTL 内なのに抑止されない)。記憶を使うと、共有が落ちて記憶 →
    /// 復旧 → 60 秒以内に開く/保存、で基準が null になり、その文書の検知が次の基準捕捉まで黙って止まる。</summary>
    [Fact]
    public void ProbeLastWriteTimeUtc_IgnoresMemo_AndClearsItWhenReachable()
    {
        var probe = new FakeReachabilityProbe
        {
            SaveTargetResult = new(Reachable: false, FileExists: false),
        };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero));
        var sut = new FileTimestampProvider(probe, clock);
        const string path = @"\\unreachable-host\share\a.txt";

        Assert.Null(sut.GetLastWriteTimeUtc(path)); // 到達不能 → 記憶
        Assert.Null(sut.GetLastWriteTimeUtc(path));
        Assert.Equal(1, probe.SaveTargetCallCount); // 前提: 記憶が効いている
        probe.SaveTargetResult = new(Reachable: true, FileExists: false); // 復旧(不在なので実 I/O へは進まない)

        Assert.Null(sut.ProbeLastWriteTimeUtc(path));

        Assert.Equal(2, probe.SaveTargetCallCount); // 記憶を無視してプローブした
        Assert.Null(sut.GetLastWriteTimeUtc(path));
        Assert.Equal(3, probe.SaveTargetCallCount); // 記憶は捨てられている(TTL 内でも抑止されない)
    }

    /// <summary><c>ProbeLastWriteTimeUtc</c> で到達不能と判れば記憶は書く(到達不能の事実は経路によらない)。
    /// 以後の <c>GetLastWriteTimeUtc</c> は TTL 内は抑止される = 開く/保存で 5 秒払った直後の Alt+Tab で
    /// もう 5 秒払わない。</summary>
    [Fact]
    public void ProbeLastWriteTimeUtc_Unreachable_RemembersRoot()
    {
        var probe = new FakeReachabilityProbe
        {
            SaveTargetResult = new(Reachable: false, FileExists: false),
        };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero));
        var sut = new FileTimestampProvider(probe, clock);
        const string path = @"\\unreachable-host\share\a.txt";

        Assert.Null(sut.ProbeLastWriteTimeUtc(path));
        Assert.Equal(1, probe.SaveTargetCallCount);

        Assert.Null(sut.GetLastWriteTimeUtc(path));

        Assert.Equal(1, probe.SaveTargetCallCount); // 記憶に抑止された
    }
}
