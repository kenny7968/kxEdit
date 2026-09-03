namespace kxEdit.App;

/// <summary>
/// ファイルの最終更新時刻(UTC)を取得する DI シーム(設計 2026-08-22 §4.3 / M-18 設計 2026-09-03)。
/// 本番は <see cref="FileTimestampProvider"/> / テストは Fake を差し込む。
/// 呼び出し元は A-1 の起動時復元(陳腐化検出)と M-18 の外部変更検知(開く・保存・ウィンドウ復帰・
/// タブ切替)。取得できない(ファイル不在・アクセス不可・I/O 失敗・不正パス・リモート到達不能)場合は
/// null を返す契約で、呼び出し側は「判定しない」に倒す(復元は従来どおり復元し、
/// M-18 は <see cref="ExternalChangeOutcome.Skipped"/> = 聞かない)。
/// 2 本の違いはリモートの到達不能記憶(60 秒 TTL)を使うかどうかだけ。ローカルパスではどちらもプローブしない。
/// </summary>
public interface IFileTimestampProvider
{
    /// <summary>
    /// 最終更新時刻(UTC)。取得できなければ null。例外は投げない。
    /// 到達不能記憶を使う。復帰・タブ切替の検知と A-1 の起動時復元のように、頻繁または一括で呼ばれ、
    /// 5 秒を繰り返し払えない経路用。
    /// </summary>
    DateTime? GetLastWriteTimeUtc(string path);

    /// <summary>
    /// 記憶を無視して(リモートなら)有界プローブを行い、到達できたら記憶を捨ててから更新時刻を返す。
    /// 開く・保存・保存直前の確認のように、直前または直後に実 I/O を行う経路用(M-18 設計 2026-09-03 §11)。
    /// 記憶を使うと、共有が落ちた 60 秒以内に開く/保存した文書の基準が null になり、その文書の検知が
    /// 次の基準捕捉まで黙って止まる(最終脆弱性レビュー V-1)。落ちた共有への Ctrl+S は
    /// WriteToPath のプローブと合わせて最悪 10 秒になる(受容)。
    /// </summary>
    DateTime? ProbeLastWriteTimeUtc(string path);
}
