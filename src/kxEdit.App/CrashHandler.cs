// CrashHandler.cs
// M-1(監査 2026-08-22 / 設計 2026-08-29 §5): 未処理例外を「退避 → 通知 → 終了」に一本化する。
namespace kxEdit.App;

/// <summary>
/// <see cref="CrashHandler"/> の副作用 seam(設計 §5.4)。
/// </summary>
/// <remarks>
/// 本番実装は <c>MainForm</c> とプロセス終了に触るため、順序の検証はこの interface 越しに行う
/// (<c>CrashHandlerTests</c>)。実装は<b>例外を投げてもよい</b>:
/// <see cref="CrashHandler.Handle"/> が握って先へ進む契約になっている。
/// </remarks>
public interface ICrashSink
{
    /// <summary>編集中の本文を退避する。</summary>
    /// <returns>true = 退避できた<b>と言い切れる</b>(次回起動で復元できる)。
    /// 疑わしいときは false を返すこと(「退避した」という嘘の安全宣言を出さない)。</returns>
    bool FlushBackups();

    /// <summary>ユーザーへ通知する。<paramref name="flushed"/> で文面を変える。</summary>
    /// <param name="flushed"><see cref="FlushBackups"/> の結果(例外時は false)。</param>
    /// <param name="ex">原因の例外。<c>AppDomain.UnhandledException</c> 経由では
    /// <c>ExceptionObject</c> が <see cref="Exception"/> でないことがあるため null もあり得る。</param>
    void Notify(bool flushed, Exception? ex);

    /// <summary>プロセスを終了する。</summary>
    void Exit();
}

/// <summary>
/// M-1: 未処理例外を「退避 → 通知 → 終了」に一本化する。
/// WinForms 既定の未処理例外ダイアログには<b>到達させない</b>(設計 §3.2)。
/// </summary>
/// <remarks>
/// 「続行」は出さない= 壊れた状態で走り続けるより、退避して落ちるほうが結果が読める。
/// 既定ダイアログを避ける理由は騒がしさではなく実害で、実機では「終了」が保存確認の
/// キャンセルを無視して落ち、hot exit バックアップが書かれないことがある(設計 §2.1)。
/// </remarks>
public sealed class CrashHandler
{
    private readonly ICrashSink _sink;
    private int _entered;

    public CrashHandler(ICrashSink sink) =>
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <summary>
    /// 未処理例外を処理する。<b>2 回目以降は何もしない</b>
    /// (ハンドラ内で再び例外が出ても無限ループしない)。
    /// </summary>
    /// <remarks>
    /// 3 手順のうち<b>どれが落ちても後続は必ず走らせる</b>。ここで throw すると
    /// 「例外ダイアログも出ずに固まる」= WinForms 既定より悪くなる。
    /// <see cref="ICrashSink.Exit"/> だけは try で包まない: 包んでも先が無く、
    /// 例外が出れば元の未処理例外経路へ戻るだけ(=既定ダイアログが出る)で、
    /// 「通知まで済ませたのにプロセスが残る」より結果が読める。
    /// </remarks>
    public void Handle(Exception? ex)
    {
        if (Interlocked.Exchange(ref _entered, 1) != 0)
            return;

        bool flushed = false;
        try
        {
            flushed = _sink.FlushBackups();
        }
        catch (Exception flushEx)
        {
            // 退避で落ちても通知と終了までは必ず到達させる。flushed は false のまま=
            // 「退避できたと言い切れない」側の文面になる。
            System.Diagnostics.Trace.TraceError($"kxEdit crash flush failed: {flushEx}");
        }

        try
        {
            _sink.Notify(flushed, ex);
        }
        catch (Exception notifyEx)
        {
            // 通知に失敗しても終了は行う(プロセスを残さない)。
            System.Diagnostics.Trace.TraceError($"kxEdit crash notify failed: {notifyEx}");
        }

        _sink.Exit();
    }
}
