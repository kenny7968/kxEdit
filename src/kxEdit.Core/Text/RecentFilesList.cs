namespace kxEdit.Core.Text;

/// <summary>
/// 「最近のファイル」リストの純ロジック（UI 非依存・テスト可能）。先頭が最新。
/// PathKey で正規化して重複を除き（同一ファイルの大小/区切り違いを 1 件に）、上限でクランプする。
/// </summary>
public static class RecentFilesList
{
    /// <summary>
    /// 「最近のファイル」の恒久上限（single-source-of-truth）。
    /// メニュー表示件数と、settings.json ロード時の防御的キャップの双方で参照する。
    /// </summary>
    /// <remarks>
    /// CSV-L-4: 攻撃 settings.json に 10 万件の RecentFiles を仕込まれても Deserialize 直後に
    /// <see cref="Truncate"/> でここへ押し込み、後段(Add / メニュー再構築 / 各所の走査)を
    /// O(MaxItems) に固定する。Deserialize 自体は依然 O(N)(System.Text.Json 側の仕様)。
    /// </remarks>
    public const int MaxItems = 10;

    /// <summary>
    /// source の先頭から最大 <see cref="MaxItems"/> 件を採用したリストを返す。null は空リストに正規化する。
    /// </summary>
    /// <remarks>
    /// CSV-L-4: settings.json 由来の巨大配列(10 万件級)を Normalize 段階でここに通し、
    /// 後段の Add / メニュー再構築を O(MaxItems) に押し込める防御関数。
    /// </remarks>
    public static List<string> Truncate(IEnumerable<string> source) =>
        source is null ? new List<string>() : source.Take(MaxItems).ToList();

    /// <summary>
    /// current の先頭に path を加えた新リストを返す。path と同一（PathKey 一致）の既存項目は除き、
    /// 全体を max 件にクランプする。max が 0 以下なら空リスト。
    /// </summary>
    public static List<string> Add(IEnumerable<string> current, string path, int max)
    {
        var result = new List<string>();
        if (max <= 0)
            return result;

        result.Add(path);
        string key = PathKey.For(path);
        foreach (string p in current)
        {
            if (result.Count >= max)
                break; // 追加前に上限判定（max==1 の超過を防ぐ）
            if (PathKey.For(p) == key)
                continue; // 同一ファイルは先頭の 1 件に集約
            result.Add(p);
        }
        return result;
    }
}
