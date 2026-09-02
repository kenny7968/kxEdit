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

    /// <summary>M-20(B5): 書込<b>成功</b>の通知フック(<see cref="OnWriteFailed"/> の対)。
    /// 引数は書けた <c>BackupRecord.Id</c>。
    ///
    /// Coordinator が「バックアップが復旧した」を判定できる唯一の観測面である(B5 Task 4 の
    /// 遷移発声がこれに乗る) ——
    /// 失敗が来なくなったことだけでは、書込が成功したのか<b>そもそも投入していないのか</b>
    /// (dirty でない・署名一致で <c>BackupAction.None</c> になり <see cref="Write"/> 自体を
    /// 呼んでいない)を区別できず、後者を復旧と読むと「一度も書けていないのに再開した」という
    /// 虚偽の発声になる。
    ///
    /// 契約:
    /// - <see cref="Write"/> 1 件につき、本フックと <see cref="OnWriteFailed"/> が<b>両方鳴ることはない</b>
    ///   (実行されたジョブは成功か失敗のどちらか一方だけを通知する)。
    /// - 逆は成り立たない ——<b>鳴らないことは「成功しなかった」を意味しない</b>。投入自体が
    ///   捨てられた場合(締切済み/破棄済み)や、投入は受理されたがジョブが実行されないまま
    ///   終わる場合(<c>SerialBackupWriter</c> の Dispose が Join 上限で諦めた等)はどちらも鳴らない。
    /// - 呼び出しスレッドは実装依存で、<see cref="OnWriteFailed"/> と同じ扱いでよい
    ///   (<c>SerialBackupWriter</c> は背景スレッドから同期発火する)。スレッド越えの吸収は受け手責務。
    /// - フックは投げない前提。投げたときの扱いも <see cref="OnWriteFailed"/> と同じで実装依存
    ///   (<c>SerialBackupWriter</c> はジョブ単位の catch で握り潰し、ワーカーは次のジョブへ進む)。
    /// - null なら何もしない(=本フックを配線しない実装・テストの挙動は不変)。</summary>
    Action<string>? OnWriteSucceeded { get; set; }

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
    /// <paramref name="ids"/> は背景スレッドが後で読み得るため、**実装側が投入時に複写して
    /// 切り離す**契約(SerialBackupWriter は <c>ToArray</c>、Fake は同期消費+履歴コピー)。
    /// 呼び出し側は渡した後にコレクションを再利用してよい=複写義務を呼び出し側へ移さない
    /// (移すと、使い回しの List を渡す 2 人目の呼び出し側が現れた瞬間に別の集合を消す)。</summary>
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
