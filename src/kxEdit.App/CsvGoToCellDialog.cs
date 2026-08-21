using System.Globalization;

namespace kxEdit.App;

/// <summary>セル指定移動のダイアログ。「行,列」（1始まり・例 2,3）を1欄で受ける。
/// IME を無効化し JIS 環境で全角数字が入る事故を防ぐ。GoToLineDialog と同方式。</summary>
public sealed class CsvGoToCellDialog : Form
{
    private readonly TextBox _input = new() { Width = 140, ImeMode = ImeMode.Disable };

    public CsvGoToCellDialog(int currentRow, int currentCol)
    {
        Text = "セルへ移動";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _input.Text = $"{currentRow},{currentCol}";
        _input.AccessibleName = "行カンマ列。例 2,3";

        BuildLayout();
        _input.Select(0, _input.Text.Length);
    }

    /// <summary>入力を 1 始まりの (row, col) として解釈する。形式不正なら false。</summary>
    public bool TryGetCell(out int row, out int col)
    {
        row = col = 0;
        var parts = _input.Text.Split(',');
        if (parts.Length != 2)
            return false;
        if (
            !int.TryParse(
                parts[0].Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out row
            )
        )
            return false;
        if (
            !int.TryParse(
                parts[1].Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out col
            )
        )
            return false;
        return row >= 1 && col >= 1;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Padding = new Padding(10),
        };
        root.Controls.Add(new Label { Text = "行,列(&C)（例 2,3）:", AutoSize = true }, 0, 0);
        root.Controls.Add(_input, 1, 0);

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            AutoSize = true,
        };
        var cancel = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
        };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
        };
        buttons.Controls.AddRange(ok, cancel);
        root.Controls.Add(buttons, 0, 1);
        root.SetColumnSpan(buttons, 2);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
