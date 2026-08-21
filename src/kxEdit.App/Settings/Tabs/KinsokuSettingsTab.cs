using kxEdit.App.Settings;
using kxEdit.Core.Settings;

namespace kxEdit.App.Settings.Tabs;

/// <summary>「禁則処理」タブ。行頭禁則・行末禁則・ぶら下げの文字セットを扱う。</summary>
public sealed class KinsokuSettingsTab : ISettingsTab
{
    public string Title => "禁則処理";

    private readonly TextBox _kinsokuStart = new() { Width = 320, AccessibleName = "行頭禁則文字" };
    private readonly TextBox _kinsokuEnd = new() { Width = 320, AccessibleName = "行末禁則文字" };
    private readonly TextBox _kinsokuHang = new() { Width = 320, AccessibleName = "ぶら下げ文字" };

    public Control BuildPage()
    {
        var root = SettingsTabLayoutHelper.NewRoot();
        SettingsTabLayoutHelper.AddRow(root, 0, "行頭禁則文字(&1):", _kinsokuStart, tabBase: 0);
        SettingsTabLayoutHelper.AddRow(root, 1, "行末禁則文字(&2):", _kinsokuEnd, tabBase: 2);
        SettingsTabLayoutHelper.AddRow(root, 2, "ぶら下げ文字(&3):", _kinsokuHang, tabBase: 4);
        return root;
    }

    public void LoadFrom(AppSettings s)
    {
        _kinsokuStart.Text = s.KinsokuLineStartChars;
        _kinsokuEnd.Text = s.KinsokuLineEndChars;
        _kinsokuHang.Text = s.KinsokuHangChars;
    }

    public void SaveTo(AppSettings r)
    {
        r.KinsokuLineStartChars = _kinsokuStart.Text;
        r.KinsokuLineEndChars = _kinsokuEnd.Text;
        r.KinsokuHangChars = _kinsokuHang.Text;
    }

    // CA1001 対応(Sub 3.4-B): BuildPage() 経由で Form の Controls ツリーに接続された
    // 場合は Form.Dispose 経由で二重に呼ばれるが、Control.Dispose は冪等なので安全。
    // BuildPage 未呼び出しで破棄された場合(異常系/テスト)のリーク防止が本 Dispose の主目的。
    public void Dispose()
    {
        _kinsokuStart.Dispose();
        _kinsokuEnd.Dispose();
        _kinsokuHang.Dispose();
    }
}
