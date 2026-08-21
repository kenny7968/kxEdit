using kxEdit.Core.Text;

namespace kxEdit.App;

/// <summary>
/// 文字コードを選ぶアクセシブルなダイアログ（DropDownList の ComboBox + OK/キャンセル）。
/// ラベルにアクセスキー(&amp;E)を付け、TabIndex でラベル→コンボの順にして
/// Alt+E でコンボへフォーカスが移るようにする（ラベルはニーモニックを次のコントロールへ転送）。
/// </summary>
public sealed class EncodingPickDialog : Form
{
    private readonly ComboBox _combo = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 240,
        AccessibleName = "文字コード",
    };

    // 選択肢の定義は Core（EncodingCatalog）へ集約。表示順もそのまま使う。
    private static readonly IReadOnlyList<EncodingCatalog.EncodingOption> Choices =
        EncodingCatalog.SelectableEncodings;

    /// <summary>
    /// 現在の選択コードページ。ShowDialog 戻り後に呼び出し側が読む（Click 発火順に非依存）。
    /// コンストラクタで SelectedIndex を必ず設定するため常に有効。
    /// </summary>
    public int SelectedCodePage => Choices[_combo.SelectedIndex].CodePage;

    public EncodingPickDialog(int currentCodePage)
    {
        Text = "文字コードを指定して開き直す";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(320, 110);

        var label = new Label
        {
            Text = "文字コード(&E):",
            AutoSize = true,
            Left = 12,
            Top = 16,
            TabIndex = 0,
        };

        _combo.Left = 12;
        _combo.Top = 38;
        _combo.TabIndex = 1;
        int selected = 0;
        for (int i = 0; i < Choices.Count; i++)
        {
            _combo.Items.Add(Choices[i].DisplayName);
            if (Choices[i].CodePage == currentCodePage)
                selected = i;
        }
        _combo.SelectedIndex = selected;

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Left = 150,
            Top = 72,
            Width = 75,
            TabIndex = 2,
        };
        var cancel = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Left = 232,
            Top = 72,
            Width = 75,
            TabIndex = 3,
        };

        Controls.AddRange(label, _combo, ok, cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
