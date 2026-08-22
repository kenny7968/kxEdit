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
}
