using System.Runtime.ExceptionServices;

namespace kxEdit.Editor.Tests;

/// <summary>
/// xUnit v2 は <c>[Fact]</c> の STA 化を標準サポートしないため、
/// 各テスト本体を STA スレッド上で走らせるためのヘルパ。
/// 使い方: <c>[Fact] public void X() =&gt; Sta.Run(() =&gt; { /* 本体 */ });</c>
/// </summary>
/// <remarks>
/// - WinForms(Control/Form)の生成やメッセージポンプは STA を要求する。
/// - 例外はワーカースレッド側で捕捉し、<c>Join</c> 後に呼び出し元へ再スローする
///   (そうしないと xUnit がテスト失敗として拾えない)。
///   単純な <c>throw captured;</c> は元スタックトレースを throw 地点で上書きし、
///   xUnit の失敗レポートで <c>Assert.Equal</c> 行に飛べなくなるため、
///   <see cref="ExceptionDispatchInfo"/> でスタックトレースを保ったまま再スローする。
/// - 現時点では <see cref="Application.Run"/> は起こさない(=ハンドル生成と
///   同期的な API 呼び出しのみを検証する契約テスト用)。将来メッセージポンプが
///   必要になったら Sta 側にオーバーロードを足す方針。
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
