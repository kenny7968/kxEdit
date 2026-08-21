namespace kxEdit.Editor.Tests;

/// <summary>
/// Editor.Tests 用の可視ホスト(App.Tests の <c>HostForm</c> と同型)。
/// CI の非対話セッションでフォーカス奪取・チラつき・アクティブ化例外を招かないよう、
/// 非アクティブ(<see cref="ShowWithoutActivation"/>=true)・画面外(-32000,-32000)・
/// タスクバー非表示で「可視状態」まで作る。
/// </summary>
/// <remarks>
/// フォーカス依存のテスト(<c>ctrl.Focus()</c> や <c>form.Activate()</c> を必要とする系)は
/// このホストの適用対象外。該当テストは従来どおり <c>new Form()</c> + <c>form.Show()</c> で
/// アクティブ化するか、LocalOnly 化して CI から除外する。
/// </remarks>
internal sealed class HostForm : Form
{
    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// 画面外・非アクティブ・タスクバー非表示で可視状態まで作った HostForm を返す。
    /// レイアウト確定のためだけに Show している系(UIA プロバイダの WM_GETOBJECT・
    /// BoundingRectangles など)を、CI 非対話で安全に走らせる用途。
    /// 呼び出し側は返却後に <c>form.Controls.Add(ctrl)</c> で子を載せる(App.Tests の
    /// <c>CreateWithDocs</c> は Add→Show の順だが、Editor.Tests の対象群は Show 後 Add でも
    /// 問題なく動くことを実測済み=BoundingRects/UiaGetObject/NativeSurface)。
    /// </summary>
    public static HostForm CreateVisible()
    {
        var form = new HostForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000),
        };
        form.Show();
        return form;
    }
}
