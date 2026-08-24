using kxEdit.Core.Backup;

namespace kxEdit.App;

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
    void WriteLayout(string path, kxEdit.Core.Session.SessionLayout layout);

    /// <summary>セッションレイアウトを削除するジョブを投入する(OFF 終了時の stale 掃除)。</summary>
    void DeleteLayout(string path);

    /// <summary>A-8: 投入済みジョブが全て実行し終わるまで待つ。<see cref="IDisposable.Dispose"/>
    /// は**しない**=終了がキャンセルされてもライターは生き続ける(これが本 API の中核契約)。
    /// <paramref name="timeout"/> 内に完了を確認できたら true。
    ///
    /// 投入を受け付けられない場合(締切済み/破棄済み)も true を返すが、これは
    /// 「保留ジョブが無い」保証ではなく「**これ以上の投入は無く**、残ジョブのドレインは
    /// <see cref="IDisposable.Dispose"/> の Join に委ねられている」の意=待てないものは待たない。
    /// 実際、Join 満了後(ワーカーが保留ジョブを抱えたまま生存)では保留が残ったまま true になる。
    /// Dispose 進行中も同じ状態だが、下記の UI スレッド単一契約を守る限り到達しない
    /// (UI スレッド自身が Dispose の中にいるため)=警戒すべきは Join 満了後の方。
    /// timeout の false(=確認できない=安全側で失敗扱い)とは極性が逆である点に注意し、
    /// **破棄済みライターに対して本 API を事後条件検査として使ってはならない**。
    ///
    /// 呼び出しは UI スレッド前提(=待っている間に誰も新しいジョブを積めないことが
    /// 「末尾バリア＝全保留ジョブの完了」の前提。実装側の不変条件コメントも参照)。
    /// hot exit の確認スキップ前に「本当に書けたか」を事後条件として検査するための seam。</summary>
    bool WaitForPendingJobs(TimeSpan timeout);
}
