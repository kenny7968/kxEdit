using yEdit.Core.Backup;

namespace yEdit.Core.Session;

/// <summary>
/// PR #22 形式(LastSessionSnapshot + buffers)を統合復元の入力へ一回限り変換する(設計 §8/§10)。
/// BufferKey(Guid.N)を合成 BackupRecord の Id に流用する。合成レコードは in-memory のみで
/// ディスクへ書かない(復元後の Reconcile が新セッション dir へ通常書込して保護する)。
/// 旧「無題かつ BufferKey なし」(cap 超過の枠だけ保存)は skip する=PR #22 E4 の意味論を保存
/// (新形式の「無題 BackupId なし=空枠」とは意味が異なる)。
/// </summary>
public static class LegacySessionConverter
{
    public static (SessionLayout Layout, List<BackupRecord> Backups) Convert(
        LastSessionSnapshot snap,
        IReadOnlyDictionary<string, string> buffers,
        DateTime nowUtc
    )
    {
        var tabs = new List<SessionLayoutRecord>();
        var backups = new List<BackupRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in snap.Tabs)
        {
            if (t is null)
                continue;
            string? backupId = null;
            if (
                t.BufferKey is not null
                && BackupIdValidator.IsValid(t.BufferKey)
                && seen.Add(t.BufferKey)
                && buffers.TryGetValue(t.BufferKey, out var content)
            )
            {
                backupId = t.BufferKey;
                backups.Add(
                    new BackupRecord(
                        Id: t.BufferKey,
                        OriginalPath: t.Path,
                        UntitledNumber: t.UntitledNumber,
                        CodePage: t.CodePage,
                        HasBom: t.HasBom,
                        LineEndingId: t.LineEnding,
                        Content: content,
                        TimestampUtc: nowUtc
                    )
                );
            }
            if (t.Path is null && backupId is null)
                continue; // 旧形式: 本文の無い無題は skip(E4 意味論の保存)
            tabs.Add(
                new SessionLayoutRecord(
                    t.Path,
                    t.UntitledNumber,
                    backupId,
                    t.IsActive,
                    t.CaretLine,
                    t.CaretColumn,
                    t.LineEnding
                )
            );
        }
        return (SessionLayoutStore.Normalize(new SessionLayout(tabs, nowUtc)), backups);
    }
}
