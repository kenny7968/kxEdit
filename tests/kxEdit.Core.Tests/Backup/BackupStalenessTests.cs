using kxEdit.Core.Backup;
using Xunit;

namespace kxEdit.Core.Tests.Backup;

/// <summary>
/// A-1 第 2 層の判定核(設計 2026-08-22 §4.1)。ディスク mtime とバックアップ取得時刻の
/// 新旧比較を、境界・DateTimeKind・攻撃者 JSON 由来の極値まで純粋関数として固定する。
/// </summary>
public class BackupStalenessTests
{
    private static readonly DateTime Backup = new(2026, 08, 22, 12, 00, 00, DateTimeKind.Utc);

    private static readonly TimeSpan Two = TimeSpan.FromSeconds(2);

    [Fact]
    public void DefaultTolerance_IsTwoSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(2), BackupStaleness.DefaultTolerance);

    [Fact]
    public void NullDisk_ReturnsFalse() =>
        Assert.False(BackupStaleness.IsDiskNewer(null, Backup, Two));

    [Fact]
    public void DiskOlder_ReturnsFalse() =>
        Assert.False(BackupStaleness.IsDiskNewer(Backup.AddMinutes(-1), Backup, Two));

    [Fact]
    public void SameInstant_ReturnsFalse() =>
        Assert.False(BackupStaleness.IsDiskNewer(Backup, Backup, Two));

    [Fact]
    public void WithinTolerance_ReturnsFalse() =>
        Assert.False(BackupStaleness.IsDiskNewer(Backup.AddSeconds(1), Backup, Two));

    /// <summary>境界: ちょうど許容ぶん新しいだけでは陳腐化と見なさない(厳密な &gt; で判定する)。</summary>
    [Fact]
    public void ExactlyAtTolerance_ReturnsFalse() =>
        Assert.False(BackupStaleness.IsDiskNewer(Backup.AddSeconds(2), Backup, Two));

    [Fact]
    public void BeyondTolerance_ReturnsTrue() =>
        Assert.True(BackupStaleness.IsDiskNewer(Backup.AddSeconds(3), Backup, Two));

    /// <summary>Unspecified(JSON 由来で Kind が落ちた場合)は契約どおり UTC とみなす。
    /// ToUniversalTime に素通しすると Local 扱いで最大 ±14 時間ずれ、判定が反転する。</summary>
    [Fact]
    public void UnspecifiedKind_TreatedAsUtc_NotLocal()
    {
        var backupUnspecified = DateTime.SpecifyKind(Backup, DateTimeKind.Unspecified);
        Assert.True(
            BackupStaleness.IsDiskNewer(
                DateTime.SpecifyKind(Backup.AddSeconds(3), DateTimeKind.Unspecified),
                backupUnspecified,
                Two
            )
        );
        Assert.False(
            BackupStaleness.IsDiskNewer(
                DateTime.SpecifyKind(Backup.AddSeconds(-3), DateTimeKind.Unspecified),
                backupUnspecified,
                Two
            )
        );
    }

    /// <summary>Local Kind は UTC へ変換してから比較する(同一瞬間なら false)。</summary>
    [Fact]
    public void LocalKindDisk_ConvertedToUtc()
    {
        var diskLocal = Backup.ToLocalTime(); // Kind=Local・同一瞬間
        Assert.False(BackupStaleness.IsDiskNewer(diskLocal, Backup, Two));
        Assert.True(BackupStaleness.IsDiskNewer(diskLocal.AddSeconds(3), Backup, Two));
    }

    /// <summary>攻撃者 JSON が TimestampUtc=DateTime.MaxValue を持つ場合、
    /// 素の <c>backup + tolerance</c> は ArgumentOutOfRangeException で復元経路ごと落ちる。</summary>
    [Fact]
    public void BackupAtMaxValue_ReturnsFalse_WithoutOverflow() =>
        Assert.False(BackupStaleness.IsDiskNewer(DateTime.MaxValue, DateTime.MaxValue, Two));

    [Fact]
    public void NegativeTolerance_ClampedToZero()
    {
        // 負の許容をそのまま加算すると backup-5s より新しい = 同時刻でも true になる。
        Assert.False(BackupStaleness.IsDiskNewer(Backup, Backup, TimeSpan.FromSeconds(-5)));
        // クランプ後は素の比較 = 1 秒でも新しければ true(既定 2 秒の吸収は効かない)。
        Assert.True(
            BackupStaleness.IsDiskNewer(Backup.AddSeconds(1), Backup, TimeSpan.FromSeconds(-5))
        );
    }
}
