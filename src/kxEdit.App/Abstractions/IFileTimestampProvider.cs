namespace kxEdit.App;

/// <summary>
/// ファイルの最終更新時刻(UTC)を取得する DI シーム(設計 2026-08-22 §4.3)。
/// 本番は <see cref="FileTimestampProvider"/> / テストは Fake を差し込む。
/// 取得できない(ファイル不在・アクセス不可・I/O 失敗・不正パス)場合は null を返す契約で、
/// 呼び出し側は「判定しない = 従来どおり復元する」に倒す。
/// </summary>
public interface IFileTimestampProvider
{
    /// <summary>最終更新時刻(UTC)。取得できなければ null。例外は投げない。</summary>
    DateTime? GetLastWriteTimeUtc(string path);
}
