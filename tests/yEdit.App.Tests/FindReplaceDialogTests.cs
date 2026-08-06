using System.Reflection;

namespace yEdit.App.Tests;

/// <summary>
/// FindReplaceDialog の 6 箇所ある <c>Hide</c> のうち、どれが「ユーザーが検索を終えた」
/// (<see cref="IFindReplaceView.Dismissed"/> 発火)でどれが G-2 の一時退避かを機械固定する。
/// <para>
/// この非対称は SearchController が保持する searcher(材質化キャッシュ=文書 1 本ぶんの保持)の
/// 寿命そのものであり、「6 箇所を <c>HideByUser()</c> に揃えよう」という善意の統一で
/// 黙って壊れる(F3 のたびにキャッシュが落ちるだけで、他のテストは何も言わない)。
/// </para>
/// <para>
/// 実 Form を画面外に可視化し(ボタンの <see cref="Button.PerformClick"/> は可視・有効を要求する)、
/// private フィールドと protected override へリフレクションで届く
/// (既存パターン: GrepControllerTests / MainFormSmokeTests)。
/// </para>
/// </summary>
public class FindReplaceDialogTests
{
    private const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>コールバック束。FindNext/FindPrev は G-2 の Hide 条件(=移動成功)を満たす true を返す。</summary>
    private static FindReplaceCallbacks HitCallbacks() =>
        new(
            FindNext: () => true,
            FindPrev: () => true,
            ReplaceOne: () => { },
            ReplaceAll: () => { },
            UpdateCount: () => { },
            InSelectionToggled: _ => { }
        );

    /// <summary>検索モードのダイアログを画面外に可視化する(G-2 の自動 Hide は検索モードでのみ起きる)。</summary>
    private static FindReplaceDialog ShowOffScreen()
    {
        var dlg = new FindReplaceDialog(HitCallbacks())
        {
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000),
        };
        dlg.SetMode(replaceMode: false);
        dlg.Show();
        return dlg;
    }

    private static T Field<T>(FindReplaceDialog dlg, string name)
    {
        var f = typeof(FindReplaceDialog).GetField(name, Priv);
        Assert.NotNull(f);
        return (T)f!.GetValue(dlg)!;
    }

    /// <summary>protected override の ProcessCmdKey へキーを直接届ける
    /// (戻り値 true=ダイアログが自分で処理した。false ならフォーカス前提が崩れており、
    /// 「発火しない」を空振りで観測してしまうためテストごと落とす)。</summary>
    private static void SendCmdKey(FindReplaceDialog dlg, Keys keyData)
    {
        var m = typeof(FindReplaceDialog).GetMethod("ProcessCmdKey", Priv);
        Assert.NotNull(m);
        object?[] args = { default(Message), keyData };
        Assert.True((bool)m!.Invoke(dlg, args)!, $"{keyData} がダイアログで処理されていない");
    }

    // ===== ユーザー終了経路(Dismissed=1) =====

    [Fact]
    public void CloseButton_RaisesDismissed_Once() =>
        Sta.Run(() =>
        {
            using var dlg = ShowOffScreen();
            int dismissed = 0;
            dlg.Dismissed += (_, _) => dismissed++;

            Field<Button>(dlg, "_close").PerformClick();

            Assert.Equal(1, dismissed);
            Assert.False(dlg.Visible);
        });

    [Fact]
    public void Escape_RaisesDismissed_Once() =>
        Sta.Run(() =>
        {
            using var dlg = ShowOffScreen();
            int dismissed = 0;
            dlg.Dismissed += (_, _) => dismissed++;

            SendCmdKey(dlg, Keys.Escape);

            Assert.Equal(1, dismissed);
            Assert.False(dlg.Visible);
        });

    [Fact]
    public void UserClosing_RaisesDismissed_Once_AndKeepsInstanceAlive() =>
        Sta.Run(() =>
        {
            using var dlg = ShowOffScreen();
            int dismissed = 0;
            dlg.Dismissed += (_, _) => dismissed++;

            dlg.Close(); // Form.Close は CloseReason.UserClosing を立てる(タイトルバーの×と同じ扱い)

            Assert.Equal(1, dismissed);
            Assert.False(dlg.Visible);
            Assert.False(dlg.IsDisposed); // 閉じずに隠す=次の Ctrl+F で同じインスタンスを再利用する
        });

    // ===== G-2 の一時退避(Dismissed=0) =====
    // Visible の assert を先に置くのは空振り防止: Hide 自体が起きていなければ
    // 「Dismissed が 0」は何も証明しない(移動成功=Hide 条件が成立していることまで固定する)。

    [Fact]
    public void FindNextButton_HidesWithoutDismissed() =>
        Sta.Run(() =>
        {
            using var dlg = ShowOffScreen();
            int dismissed = 0;
            dlg.Dismissed += (_, _) => dismissed++;

            Field<Button>(dlg, "_next").PerformClick();

            Assert.False(dlg.Visible); // G-2: 移動成功で自分を隠す
            Assert.Equal(0, dismissed); // ただし検索は続く=終了ではない
        });

    [Fact]
    public void FindPrevButton_HidesWithoutDismissed() =>
        Sta.Run(() =>
        {
            using var dlg = ShowOffScreen();
            int dismissed = 0;
            dlg.Dismissed += (_, _) => dismissed++;

            Field<Button>(dlg, "_prev").PerformClick();

            Assert.False(dlg.Visible);
            Assert.Equal(0, dismissed);
        });

    [Fact]
    public void EnterInPattern_HidesWithoutDismissed() =>
        Sta.Run(() =>
        {
            using var dlg = ShowOffScreen();
            var pattern = Field<TextBox>(dlg, "_pattern");
            pattern.Focus();
            Assert.True(pattern.Focused, "検索語ボックスにフォーカスが無いと Enter 分岐へ入らない");
            int dismissed = 0;
            dlg.Dismissed += (_, _) => dismissed++;

            SendCmdKey(dlg, Keys.Enter);

            Assert.False(dlg.Visible);
            Assert.Equal(0, dismissed);
        });
}
