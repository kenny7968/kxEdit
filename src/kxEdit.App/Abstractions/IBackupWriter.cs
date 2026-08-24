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

    /// <summary>E-2: 復元ダイアログ「すべて破棄」の実体。<paramref name="ids"/> のバックアップを
    /// <paramref name="baseDir"/> 配下(flat + 全 <c>session-*</c>)を横断して削除する。
    ///
    /// <see cref="Write"/> / <see cref="Delete"/> が ctor で受けた自セッション dir に束縛されるのに対し、
    /// 本 API は**意図的にそのスコープを外れる**(名前で明示している)。旧 <c>DeleteAll()</c> は
    /// 自セッション dir だけを消していたため、提示した孤児が一件も消えなかった。
    ///
    /// 契約: 呼び出し側は「**ユーザーに提示した record の Id**」だけを渡すこと。一覧に出していない
    /// Id を渡すと、同時起動している別インスタンスのライブバックアップを消し得る。
    /// ただし逆は成り立たない=**提示した Id なら安全、ではない**。一覧(<c>BackupStore.LoadAll</c>)は
    /// 他インスタンスの <c>session-*</c> まで広く拾うため、一覧に載った他インスタンスのライブも
    /// 「すべて破棄」で消える(設計 2026-08-24 §5 で受容したトレードオフ・申し送り S-E2-1)。
    /// <paramref name="ids"/> は背景スレッドが後で読むため、呼び出し側で不変のスナップショットを渡す。</summary>
    void DeleteAcrossSessions(string baseDir, IReadOnlyList<string> ids);

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
