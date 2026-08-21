using System.Runtime.ExceptionServices;

namespace kxEdit.App.Tests;

/// <summary>
/// xUnit v2 は <c>[Fact]</c> の STA 化を標準サポートしないため、
/// 各テスト本体を STA スレッド上で走らせるためのヘルパ(Editor.Tests の Sta.cs と同一パターン)。
/// 使い方: <c>[Fact] public void X() =&gt; Sta.Run(() =&gt; { /* 本体 */ });</c>
/// </summary>
/// <remarks>
/// - WinForms(Control/Form)の生成やメッセージポンプは STA を要求する。
/// - 例外はワーカースレッド側で捕捉し、<c>Join</c> 後に呼び出し元へ再スローする
///   (そうしないと xUnit がテスト失敗として拾えない)。
///   単純な <c>throw captured;</c> は元スタックトレースを throw 地点で上書きし、
///   xUnit の失敗レポートで <c>Assert.Equal</c> 行に飛べなくなるため、
///   <see cref="ExceptionDispatchInfo"/> でスタックトレースを保ったまま再スローする。
/// - <see cref="Application.Run"/> は起こさないが、App.Tests は同期契約テストに
///   限らず TCS 駆動の async テスト(GrepControllerTests・SerialBackupWriterTests 等の
///   <c>await controller.RunAsync(...)</c> 経路)を含む。以下の TCS 規律を守ること。
///
/// TCS 規律(重要):
/// - <see cref="TaskCompletionSource{TResult}"/> は必ず <b>同一 STA スレッド</b>で
///   完了(<c>SetResult</c> を呼ぶ)させる。<c>TaskCreationOptions.RunContinuationsAsynchronously</c>
///   は使わない・<c>searchFn</c> の中に <c>Task.Run</c> を挟まない。
/// - 破ると <c>await</c> の継続がポンプされない WinFormsSynchronizationContext へ
///   Post され、<c>GetResult()</c> がハングする(CI 上の 20 分 timeout を燃やす)。
///   Sta.Run は <see cref="Application.Run"/> を回していないため、Post された継続は
///   誰も拾わない。
/// - GrepControllerTests 冒頭に定義される <c>SynchronousSyncContext</c> は
///   <see cref="System.Threading.SynchronizationContext.Post"/> を同期呼び出しに
///   置き換えるためのテストヘルパで、<see cref="Progress{T}"/> を観測するテスト
///   (Progress は ctor 時点の SC を捕捉して報告先に Post するため)でのみ必要。
///   通常の TCS 完了経路にはこれは要らない。
/// </remarks>
public static class Sta
{
    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null)
            ExceptionDispatchInfo.Capture(captured).Throw();
    }
}
