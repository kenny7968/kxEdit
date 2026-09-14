// SingleInstanceGate.cs
// 設計 2026-09-14 §3「ゲートの 3 状態」/ §4「2 つのレース」/ §7「受容するリスク」。
using System.Diagnostics;
using System.IO;

namespace kxEdit.App;

/// <summary>
/// 起動時の判定結果。
/// </summary>
/// <remarks>
/// 設計 §3 の表は 3 状態だったが、§4 の精密化で失敗を 2 つに分けた。
/// <see cref="NoResponse"/> と <see cref="ForeignPeer"/> は<b>出す文言が違う</b> ——
/// 別フォルダの別ビルドが動いている状況で「応答しません」と言うのは誤診であり、
/// ユーザーはタスク マネージャーを開いて「応答している kxEdit」を見ることになる。
/// </remarks>
internal enum SingleInstanceOutcome
{
    /// <summary>自分が 1 つ目。通常起動する。</summary>
    FirstInstance,

    /// <summary>既存インスタンスへ引き渡した。無言で終了する(設計 D1)。</summary>
    HandedOff,

    /// <summary>既存インスタンスが応答しない。エラーを出して終了する(設計 D4)。</summary>
    NoResponse,

    /// <summary>別の場所の kxEdit が起動している。エラーを出して終了する。</summary>
    ForeignPeer,
}

/// <summary>
/// 多重起動を禁止するゲート。1 つ目なら Mutex を保持して待受を始め、
/// 2 つ目なら既存インスタンスへ引き渡す。
/// </summary>
/// <remarks>
/// <para>
/// <b>Mutex はこのオブジェクトが握り続ける</b>。ローカル変数に置くと GC が
/// ファイナライズして解放しうるので、プロセス寿命と一致させるためフィールドで持つ
/// (設計 §3「Mutex の寿命」)。呼び出し側は <c>using (gate) { Application.Run(...); }</c> の
/// 形で、<b>アプリ終了まで</b>生かすこと。先に破棄すると排他が終了前に外れる。
/// </para>
/// <para>
/// <b>このゲートは例外を投げてはならない。</b>失敗経路はすべて
/// <see cref="SingleInstanceOutcome"/> のいずれかに落ちる。ここから例外が漏れると、
/// D4 のエラーダイアログを出す前に起動時例外で落ち、「必ず理由を伝えて終了する」という
/// 不変条件をゲート自身が破る(設計 §7)。
/// </para>
/// </remarks>
internal sealed class SingleInstanceGate : IDisposable
{
    private readonly Mutex _mutex;
    private readonly SingleInstanceServer? _server;

    private SingleInstanceGate(Mutex mutex, SingleInstanceServer? server)
    {
        _mutex = mutex;
        _server = server;
    }

    /// <summary>
    /// 自分が 1 つ目かを判定し、2 つ目なら既存インスタンスへ引き渡す。
    /// </summary>
    /// <param name="options">コマンドライン(<c>--new-instance</c> の判定に使う)。</param>
    /// <param name="mutexName">排他に使う Mutex 名(<see cref="SingleInstanceNames"/>)。</param>
    /// <param name="pipeName">引き渡しに使うパイプ名(<see cref="SingleInstanceNames"/>)。</param>
    /// <param name="onActivate">
    /// 前面化要求の受理ハンドラ。<b>パイプスレッドから呼ばれる</b> ——
    /// 契約は <see cref="SingleInstanceServer"/> の該当引数を参照。
    /// </param>
    /// <param name="connectTimeout">引き渡しの接続確立の上限。</param>
    /// <param name="ackTimeout">
    /// 引き渡しの ACK 待ちの上限。<b>受け側の前面化待ち(Task 7 の <c>ActivateTimeout</c>)より
    /// 明確に長く、かつ <see cref="SingleInstanceServer.DefaultPerConnectionTimeout"/> より
    /// 短いこと</b>。等号でも「前面化は成功しているのにエラーダイアログが出る」窓が開く。
    /// 予算の入れ子の全体像は <see cref="SingleInstanceServer"/> の remarks を参照。
    /// </param>
    /// <param name="handOff">
    /// 引き渡しの実処理。既定は <see cref="SingleInstanceClient.TryHandOff"/> へ
    /// 上の 2 つの期限を束ねたもの。<b>テストが 4 状態の写像と「終了中レース」を
    /// 決定論的に固定するための seam</b> —— 実パイプのタイミングに依存させると、
    /// <see cref="SingleInstanceOutcome.ForeignPeer"/> への写像や「引き渡し失敗の直後に
    /// 相手が Mutex を手放した」状況を狙って作れない。
    /// </param>
    /// <returns>
    /// 判定結果と、1 つ目だった場合のゲート。<b>1 つ目以外では <c>null</c> を返す</b>
    /// (<c>--new-instance</c> も排他しないので <c>null</c>)。
    /// 呼び出し側の <c>using (gate)</c> は <c>null</c> でも安全に no-op になる。
    /// </returns>
    internal static (SingleInstanceOutcome Outcome, SingleInstanceGate? Gate) Acquire(
        CommandLineOptions options,
        string mutexName,
        string pipeName,
        Func<CancellationToken, bool> onActivate,
        TimeSpan connectTimeout,
        TimeSpan ackTimeout,
        Func<string, HandoffResult>? handOff = null
    )
    {
        if (options.NewInstance)
        {
            // 開発・検証用の抜け道(設計 D3)。排他も待受もしない ——
            // 待受を始めると、本来の 1 つ目が居ないときに次の起動を引き受けてしまい、
            // 「--new-instance で起動したプロセスが単一インスタンスの主になる」
            // という、この抜け道の趣旨と正反対の状態になる。
            Trace.TraceInformation("single-instance: bypassed by --new-instance");
            return (SingleInstanceOutcome.FirstInstance, null);
        }

        if (TryBecomeFirst(mutexName, pipeName, onActivate) is { } first)
            return (SingleInstanceOutcome.FirstInstance, first);

        var result = (handOff ?? DefaultHandOff(connectTimeout, ackTimeout))(pipeName);
        if (result == HandoffResult.Success)
            return (SingleInstanceOutcome.HandedOff, null);
        if (result == HandoffResult.ForeignPeer)
        {
            // 相手は正常に動いている別ビルドなので、Mutex を取り直しに行かない。
            // 取り直せてしまうと 2 インスタンスが黙って並走する(設計 §4)。
            return (SingleInstanceOutcome.ForeignPeer, null);
        }

        // 終了中レース(設計 §4): 既存インスタンスが終了処理の途中で、Mutex はまだ
        // 保持しているがパイプは閉じていた可能性がある。【1 回だけ】取り直す。
        // これが無いと、kxEdit を閉じた直後の再起動が「応答しません」エラーになる。
        // 繰り返さないのは、本当にハングしている相手に対して起動が期限の倍数だけ
        // 待たされるのを避けるため —— 待たせるくらいなら D4 のエラーを早く出す。
        if (TryBecomeFirst(mutexName, pipeName, onActivate) is { } retried)
        {
            Trace.TraceInformation("single-instance: became first after peer exited");
            return (SingleInstanceOutcome.FirstInstance, retried);
        }

        return (SingleInstanceOutcome.NoResponse, null);
    }

    /// <summary>
    /// 既定の引き渡し。<b>2 つの期限を取り違えないこと</b> —— 接続と ACK 待ちは
    /// 別予算であり(<see cref="SingleInstanceClient.TryHandOff"/> の xmldoc)、
    /// 片方に寄せると所要時間が見積もれなくなるか、前面化の成功を待ちきれなくなる。
    /// </summary>
    private static Func<string, HandoffResult> DefaultHandOff(
        TimeSpan connectTimeout,
        TimeSpan ackTimeout
    ) => name => SingleInstanceClient.TryHandOff(name, connectTimeout, ackTimeout);

    /// <summary>Mutex を作れたら 1 つ目。作れなければ <c>null</c>。</summary>
    private static SingleInstanceGate? TryBecomeFirst(
        string mutexName,
        string pipeName,
        Func<CancellationToken, bool> onActivate
    )
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, mutexName, out createdNew);
        }
        // 【名前付き Mutex の作成が投げうるのは次の 3 系統。どれも投げ返してはならない】
        // 根拠: 引数検証(ArgumentException 系)を除くと、new Mutex の失敗はすべて
        // Win32 エラーコードを Win32Marshal.GetExceptionForWin32Error が写したものになる。
        // その写像の行き先が UnauthorizedAccessException(ERROR_ACCESS_DENIED)・
        // WaitHandleCannotBeOpenedException(ERROR_INVALID_HANDLE = 同名が別種の
        // カーネルオブジェクト)・IOException(それ以外の Win32 エラーの既定の行き先)の 3 つ。
        //  - UnauthorizedAccessException: 昇格の順序(設計 §7 受容リスク)。同一ユーザーが
        //    管理者として先に起動していると名前は一致するが、高 IL プロセスが作った
        //    オブジェクトへの MUTEX_ALL_ACCESS 要求が中 IL から拒否される。
        //  - WaitHandleCannotBeOpenedException: 同名が別種のカーネルオブジェクト
        //    (イベント等)で先取りされている。同一セッションのコードなら誰でも作れる。
        //  - IOException: 上記以外の Win32 エラー。
        // ここを捕まえないと、D4 のエラーダイアログを出す前に起動時例外で落ち、
        // ゲート自身が「必ず理由を伝えて終了する」という不変条件を破る。
        // 【ArgumentException 系は意図的に捕まえない】名前は SingleInstanceNames が
        // 組み立てるので、不正名が来るのは実装のバグである。握り潰すと、名前がドリフトして
        // 排他が丸ごと効かなくなった状態が「正常起動」に見えてしまう。
        catch (Exception ex)
            when (ex
                    is UnauthorizedAccessException
                        or WaitHandleCannotBeOpenedException
                        or IOException
            )
        {
            Trace.TraceWarning(
                $"single-instance: mutex unavailable: {ex.GetType().Name}: {ex.Message}"
            );
            return null; // 呼び出し側は引き渡しを試み、失敗すれば NoResponse になる
        }
        if (!createdNew)
        {
            // 2 つ目だった。ここで捨てないと、引き渡し経路の間ずっとハンドルが残る。
            mutex.Dispose();
            return null;
        }

        // 待受は MainForm 構築より前に始める(設計 §4「起動中レース」)。
        // 作れなくても起動は止めない ——「待受なしの 1 つ目」に劣化するだけで、
        // Mutex は握ったままにする(手放すと 2 つ目が黙って並走する)。
        //
        // 【ここに try/finally が無いのは SingleInstanceServer.Start() が例外を出さないから】
        // Mutex を作ってからゲートへ格納するまでの間で投げるものがあると、その Mutex は
        // どこからも Dispose されない。Start() は「失敗しても起動は止めない」ために
        // 全例外を飲む実装で、Dispose も同様に飲む —— この 2 つに寄りかかっている。
        // 将来 Start() が throw するように変わると、ここはハンドルのリークではなく
        // 【起動時クラッシュ】という重い形で壊れる(ゲートの不変条件が破れる)。
        // 変えるときは、ここに try/finally を入れてから変えること。
        var server = new SingleInstanceServer(pipeName, onActivate);
        if (!server.Start())
        {
            server.Dispose();
            return new SingleInstanceGate(mutex, null);
        }
        return new SingleInstanceGate(mutex, server);
    }

    /// <summary>
    /// 待受を止め、Mutex を手放す。<b>2 回以上呼んでも安全</b>
    /// (<see cref="SingleInstanceServer.Dispose"/> と <see cref="WaitHandle.Close"/> は
    /// どちらも冪等)。
    /// </summary>
    public void Dispose()
    {
        // 順序が逆だと、Mutex を手放した後も待受が生きている窓が開く ——
        // そこへ来た 2 つ目は「引き渡しに成功して終了」するが、こちらも終了中なので
        // ユーザーからは kxEdit が消えたように見える。
        _server?.Dispose();

        // ReleaseMutex は取得したスレッドからでないと失敗する。ここは
        // 「プロセスが終わる」経路なので、ハンドルを閉じて OS に解放させる。
        _mutex.Dispose();
    }
}
