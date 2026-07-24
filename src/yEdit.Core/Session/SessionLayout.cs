namespace yEdit.Core.Session;

/// <summary>
/// hot exit 統合(設計 2026-07-23 §2.1)のタブ 1 個のレイアウト表現。dirty 文書の本文・
/// エンコーディングは BackupId が参照する BackupRecord 側が持つ(重複して持たない)。
/// LineEnding は「空の無題タブの枠」復元にのみ使う(パスありは復元時 auto-detect)。
/// </summary>
public sealed record SessionLayoutRecord(
    string? Path,
    int UntitledNumber,
    string? BackupId,
    bool IsActive,
    int CaretLine,
    int CaretColumn,
    int LineEnding
);

/// <summary>タブ列レイアウトのスナップショット(順序=タブ順)。SavedAtUtc は診断用。</summary>
public sealed record SessionLayout(List<SessionLayoutRecord> Tabs, DateTime SavedAtUtc);
