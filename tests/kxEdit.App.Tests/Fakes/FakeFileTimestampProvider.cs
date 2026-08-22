namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IFileTimestampProvider"/> のテスト用フェイク。<see cref="Times"/> に載せた
/// パスだけ時刻を返し、それ以外は null(= 不在)。<see cref="Queries"/> は
/// 「検証 NG のパスへ I/O しない」契約(設計 2026-08-22 §4.3)を assert するための観測点。
/// </summary>
public sealed class FakeFileTimestampProvider : IFileTimestampProvider
{
    /// <summary>パス → 最終更新時刻(UTC)。載っていないパスは null(不在)を返す。</summary>
    public Dictionary<string, DateTime> Times { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>問い合わせを受けたパスの履歴(順序保持)。</summary>
    public List<string> Queries { get; } = new();

    public DateTime? GetLastWriteTimeUtc(string path)
    {
        Queries.Add(path);
        return Times.TryGetValue(path, out var t) ? t : null;
    }
}
