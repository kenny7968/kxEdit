namespace kxEdit.App;

/// <summary>
/// 文書情報を表示するモーダルダイアログ(GoToLineDialog パターン踏襲)。
/// 単一の Multiline/ReadOnly TextBox に全項目を \r\n 区切りで表示する。
/// SR は TextBox にフォーカスが入った時点で内容全体を通し読みし、以降はキャレット移動で
/// 行単位/文字単位に読める(kxEdit 側から IAnnouncer.Say は呼ばず OS/SR の標準動作に委ねる)。
/// 晴眼・弱視ユーザーには「ラベル: 値」の縦並びがそのままレイアウトとして機能する。
/// </summary>
public sealed class DocumentInfoDialog : Form
{
    public DocumentInfoDialog(string text)
    {
        Text = "文書情報";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        // AutoSize は使わない(TextBox の内容量でダイアログ寸法が揺れないようにする)。
        ClientSize = new Size(480, 280);

        var textBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            // 水平も出す: FixedDialog + WordWrap=false のため、これが無いと幅を超えた行
            // (OneDrive 同期フォルダや repos 配下の長いパス)へ晴眼・弱視ユーザーが到達できない。
            // WordWrap=false は「1 行=1 項目」という SR の読み単位を守るために維持する。
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Text = text,
            TabStop = true,
            TabIndex = 0, // 初期フォーカス=TextBox(SR が開いた直後に内容を読めるようにする)
            AccessibleName = "文書情報",
        };

        var closeBtn = new Button
        {
            Text = "閉じる(&C)",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
        };

        var buttonPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Padding = new Padding(6),
            TabIndex = 1,
        };
        buttonPanel.Controls.Add(closeBtn);

        // WinForms は z-order の逆順(末尾の子から)にドックを確定させる。Dock=Fill を先に追加して
        // 最小 index に置くことで、Bottom のボタン列が先に領域を確保し、TextBox が残りを埋める
        // (MainForm の TabHost=Fill → status/announce=Bottom → menu=Top と同じ並べ方)。
        Controls.Add(textBox);
        Controls.Add(buttonPanel);

        AcceptButton = closeBtn; // Enter で閉じる(Multiline TextBox は AcceptsReturn=false のため横取りしない)
        CancelButton = closeBtn; // Esc で閉じる
    }
}
