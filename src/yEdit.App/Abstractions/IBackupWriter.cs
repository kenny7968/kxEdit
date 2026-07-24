using yEdit.Core.Backup;

namespace yEdit.App;

/// <summary>
/// バックアップの背景書込ジョブ受け(Phase 2 Stage 5・上位文書 §2.1 の精密化)。
/// Coordinator が BackupStore への静的参照を持たないよう、Action 束ではなく型付きの
/// 3 メソッドで表面を切る。SerialBackupWriter が既存の BlockingCollection 直列実行で
/// 実装し、Fake は in-memory Dictionary で完全に I/O から独立する。
/// </summary>
public interface IBackupWriter : IDisposable
{
    /// <summary>書込失敗を UI スレッド側に通知するためのフック。
    /// Coordinator が ctor で失敗回復用の Enqueue を登録する(null なら握り潰す)。</summary>
    Action<string>? OnWriteFailed { get; set; }

    /// <summary>レイアウト書込失敗の通知(次 Reconcile で強制再書込)。</summary>
    Action? OnLayoutWriteFailed { get; set; }

    void Write(BackupRecord record);
    void Delete(string id);
    void DeleteAll();

    /// <summary>セッションレイアウトを path へ書き込むジョブを投入する(SessionLayoutStore.Save)。</summary>
    void WriteLayout(string path, yEdit.Core.Session.SessionLayout layout);

    /// <summary>セッションレイアウトを削除するジョブを投入する(OFF 終了時の stale 掃除)。</summary>
    void DeleteLayout(string path);
}
