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

    /// <summary>問い合わせを受けたパスの履歴(順序保持)。<see cref="ProbeLastWriteTimeUtc"/> もここに載る
    /// (経路によらず「問い合わせた回数」を数えるテストのため)。</summary>
    public List<string> Queries { get; } = new();

    /// <summary>M-18 V-1: 到達不能記憶を素通りする側(<see cref="ProbeLastWriteTimeUtc"/>)だけの履歴。
    /// 基準を捕捉する経路(開く・保存・保存直前の確認)がこちらを使い、復帰・タブ切替の検知と A-1 の復元が
    /// <see cref="GetLastWriteTimeUtc"/> を使うことを固定する観測点。</summary>
    public List<string> ProbeQueries { get; } = new();

    /// <summary>M-18: 問い合わせの瞬間に呼ぶ(「読む前に取る」「書いた後に取る」の順序を、
    /// この中でファイルを書き換える / 読むことで観測する)。どちらの経路でも呼ぶ。</summary>
    public Action<string>? OnQuery { get; set; }

    public DateTime? GetLastWriteTimeUtc(string path) => Query(path);

    public DateTime? ProbeLastWriteTimeUtc(string path)
    {
        ProbeQueries.Add(path);
        return Query(path);
    }

    private DateTime? Query(string path)
    {
        Queries.Add(path);
        OnQuery?.Invoke(path);
        return Times.TryGetValue(path, out var t) ? t : null;
    }
}
