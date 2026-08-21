namespace kxEdit.Core.Session;

/// <summary>通常終了時のタブ列スナップショット(順序=タブ順)。</summary>
public sealed record LastSessionSnapshot(List<SessionTabRecord> Tabs);
