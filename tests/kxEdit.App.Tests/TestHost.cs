namespace kxEdit.App.Tests;

/// <summary>
/// テストホスト用フォーム(Stage 4 で 3 クラス目の複製が現れたため共通化)。
/// フォーカスを奪わないよう非アクティブ(ShowWithoutActivation)・画面外・タスクバー非表示で
/// 「可視状態」まで作る。TabControl の Selected/Deselecting/SelectedIndexChanged は
/// ハンドル生成だけではプログラム切替で発火せず、ウィンドウ可視のとき同期発火する
/// (Stage 1 プローブ実測)。実運用の MainForm は常に可視なので、可視で作るのが忠実な再現。
/// </summary>
internal sealed class HostForm : Form
{
    protected override bool ShowWithoutActivation => true;

    /// <summary>DocumentManager の TabHost を載せて可視状態まで作る(Controller テスト共通の土台)。</summary>
    public static (Form Form, DocumentManager Docs) CreateWithDocs()
    {
        var form = new HostForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000), // 画面外(テスト実行中のチラつき防止)
        };
        var docs = new DocumentManager(() => new EditorControl());
        form.Controls.Add(docs.TabHost);
        form.Show();
        return (form, docs);
    }
}
