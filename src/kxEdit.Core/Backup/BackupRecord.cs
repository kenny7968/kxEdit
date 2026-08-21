namespace kxEdit.Core.Backup;

/// <summary>
/// 1 文書ぶんのバックアップ記録（サイドカー JSON の中身）。本文とともに、復元に必要な
/// メタ（元パス・文字コード・改行・無題連番）を持つ。Id はファイル名（&lt;Id&gt;.json）に使う GUID。
/// BK-M-3 (v0.11): <see cref="Content"/> は nullable。上限 (BackupCoordinator.MaxBackupChars=32M chars)
/// を超えた文書は Content=null (path-only fallback) で保存される。復元経路は
/// <c>rec.Content ?? string.Empty</c> で空文字扱いにし、RestoreDialog が「本文なし」の marker を表示する。
/// </summary>
public sealed record BackupRecord(
    string Id,
    string? OriginalPath,
    int UntitledNumber,
    int CodePage,
    bool HasBom,
    int LineEndingId,
    string? Content,
    DateTime TimestampUtc
);
