namespace kxEdit.Core.Text;

/// <summary>
/// 「最近のファイル」リストの純ロジック（UI 非依存・テスト可能）。先頭が最新。
/// <see cref="PathKey.ForNormalized"/> で重複を除き（同一ファイルの大小違いを 1 件に）、
/// 上限でクランプする。<b>ファイルシステムには一切触れない</b>(Issue #48 / S-15。
/// 区切り違いを吸収しなくなった経緯は <see cref="Add"/> の remarks 参照)。
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
    /// current の先頭に path を加えた新リストを返す。path と同一（<see cref="PathKey.ForNormalized"/>
    /// 一致）の既存項目は除き、全体を max 件にクランプする。max が 0 以下なら空リスト。
    /// <b>path と current の各項目は正規化済み絶対パス</b>(Issue #48 / 設計書 §3.1 の不変条件)。
    /// </summary>
    /// <remarks>
    /// Issue #48: 以前はここで <see cref="PathKey.For"/>(= <c>GetFullPath</c>)を
    /// 1 + 履歴件数だけ呼んでいた。<c>RegisterRecent</c> は開くたび・保存が成功するたびに走り、
    /// 最近のファイルは設定に永続するので、一度でも不達共有上の <c>~</c> パスを開くと
    /// 以後すべての開く・保存が約 21 秒固まった(S-15 と同一機構・#47 以前からの既存バグ)。
    /// 既存 settings.json に残る未正規化エントリーは dedup されなくなるが、
    /// データ損失は無く 1 度開き直せば解消する(設計書 §3.4 の受容)。
    /// 同じ理由で「正規化できない入力はまとめて 1 件」という <see cref="PathKey.For"/> 側の
    /// 集約(CSV-L-8)もここでは効かなくなるが、件数は max で頭打ちなので増幅は起きない。
    /// </remarks>
    public static List<string> Add(IEnumerable<string> current, string path, int max)
    {
        var result = new List<string>();
        if (max <= 0)
            return result;

        result.Add(path);
        string key = PathKey.ForNormalized(path);
        foreach (string p in current)
        {
            if (result.Count >= max)
                break; // 追加前に上限判定（max==1 の超過を防ぐ）
            if (PathKey.ForNormalized(p) == key)
                continue; // 同一ファイルは先頭の 1 件に集約
            result.Add(p);
        }
        return result;
    }
}
