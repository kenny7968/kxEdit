using System.IO;

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
            var probe = new Fakes.FakeReachabilityProbe();

            _ = new FileTimestampProvider(probe).GetLastWriteTimeUtc(path);

            Assert.Equal(0, probe.CallCount);
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
        var probe = new Fakes.FakeReachabilityProbe { Result = false };

        var actual = new FileTimestampProvider(probe).GetLastWriteTimeUtc(
            @"\\unreachable-host\share\a.txt"
        );

        Assert.Null(actual); // 到達不能 = 判定しない(従来どおり復元する)
        Assert.Equal(1, probe.CallCount);
        Assert.Equal(TimeSpan.FromSeconds(5), probe.LastTimeout);
    }

    /// <summary>同じ共有上の 2 件目以降はプローブし直さない。起動時復元は同一共有の文書を
    /// 何件も含みうるため、憶えないと「5 秒 × レコード数」が積み上がる(H-1 の増幅点)。</summary>
    [Fact]
    public void UnreachableUncRoot_IsProbedOnlyOnce()
    {
        var probe = new Fakes.FakeReachabilityProbe { Result = false };
        var sut = new FileTimestampProvider(probe);

        Assert.Null(sut.GetLastWriteTimeUtc(@"\\unreachable-host\share\a.txt"));
        Assert.Null(sut.GetLastWriteTimeUtc(@"\\unreachable-host\share\b.txt"));
        Assert.Null(sut.GetLastWriteTimeUtc(@"\\unreachable-host\share\sub\c.txt"));

        Assert.Equal(1, probe.CallCount);
    }

    /// <summary>別の共有は記録を共有しない(1 つが落ちていても他は判定する)。</summary>
    [Fact]
    public void UnreachableRoot_DoesNotSuppressOtherRoots()
    {
        var probe = new Fakes.FakeReachabilityProbe { Result = false };
        var sut = new FileTimestampProvider(probe);

        Assert.Null(sut.GetLastWriteTimeUtc(@"\\host-a\share\a.txt"));
        Assert.Null(sut.GetLastWriteTimeUtc(@"\\host-b\share\b.txt"));

        Assert.Equal(2, probe.CallCount);
    }
}
