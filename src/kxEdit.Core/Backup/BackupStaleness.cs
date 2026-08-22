namespace kxEdit.Core.Backup;

/// <summary>
/// バックアップ本文とディスク上のファイルの新旧を比較する純粋関数(設計 2026-08-22 §4.1)。
/// A-1 の第 2 層: 保存成功後にバックアップの即時削除を投入しても、背景ライターの削除が
/// ディスクへ届く前にクラッシュする残余窓が原理的に残るため、復元側でも陳腐化を検出する。
/// UI/スレッド/ファイルシステム非依存 = Core で単体テストできる。
/// </summary>
public static class BackupStaleness
{
    /// <summary>既定の許容差。FAT の 2 秒粒度と NTP の微調整を吸収する。</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// ディスク側がバックアップ取得時刻より新しい(= バックアップが陳腐化している疑いがある)か。
    /// <paramref name="diskLastWriteUtc"/> が null(ファイル無し・取得失敗)なら false
    /// =「判定しない」に倒す(呼び出し側は従来どおり復元する)。
    /// </summary>
    /// <remarks>
    /// Kind の扱い: <see cref="DateTimeKind.Unspecified"/> は契約どおり UTC とみなす。
    /// ToUniversalTime へ素通しすると Local 扱いで最大 ±14 時間ずれ、判定が反転する
    /// (BackupRecord は JSON 経由で Kind が落ちうる)。
    /// オーバーフロー: 攻撃者 JSON の TimestampUtc=<see cref="DateTime.MaxValue"/> で
    /// <c>backup + tolerance</c> が例外になり復元経路ごと落ちるのを防ぐため、加算前に判定する。
    /// </remarks>
    public static bool IsDiskNewer(
        DateTime? diskLastWriteUtc,
        DateTime backupTimestampUtc,
        TimeSpan tolerance
    )
    {
        if (diskLastWriteUtc is not DateTime disk)
            return false;
        if (tolerance < TimeSpan.Zero)
            tolerance = TimeSpan.Zero;

        DateTime backupUtc = AsUtc(backupTimestampUtc);
        if (backupUtc > DateTime.MaxValue - tolerance)
            return false; // 加算がオーバーフローする = これより新しいディスクは存在しない

        return AsUtc(disk) > backupUtc + tolerance;
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}
