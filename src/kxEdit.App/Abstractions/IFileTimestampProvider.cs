namespace kxEdit.App;

/// <summary>
/// ファイルの最終更新時刻(UTC)を取得する DI シーム(設計 2026-08-22 §4.3 / M-18 設計 2026-09-03)。
/// 本番は <see cref="FileTimestampProvider"/> / テストは Fake を差し込む。
/// 呼び出し元は A-1 の起動時復元(陳腐化検出)と M-18 の外部変更検知(開く・保存・ウィンドウ復帰・
/// タブ切替)。取得できない(ファイル不在・アクセス不可・I/O 失敗・不正パス・リモート到達不能)場合は
/// null を返す契約で、呼び出し側は「判定しない = 変更なしとして扱う」に倒す
/// (復元は従来どおり復元し、M-18 は聞かない)。
/// </summary>
public interface IFileTimestampProvider
{
    /// <summary>最終更新時刻(UTC)。取得できなければ null。例外は投げない。</summary>
    DateTime? GetLastWriteTimeUtc(string path);
}
