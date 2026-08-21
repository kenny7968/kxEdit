namespace kxEdit.App;

/// <summary>行番号を入力して移動するモーダルダイアログ。NumericUpDown で 1..最大行を選ぶ。</summary>
public sealed class GoToLineDialog : Form
{
    // IME を無効化し、JIS 環境で全角数字が入って受理されない事故を防ぐ。
    private readonly NumericUpDown _number = new()
    {
        Minimum = 1,
        Width = 120,
        ImeMode = ImeMode.Disable,
    };

    public GoToLineDialog(int current, int maxLine)
    {
        Text = "行へ移動";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _number.Maximum = Math.Max(1, maxLine);
        _number.Value = Math.Clamp(current, 1, (int)_number.Maximum);
        _number.AccessibleName = $"行番号 1 から {maxLine}";

        BuildLayout(maxLine);
    }

    public int LineNumber => (int)_number.Value;

    private void BuildLayout(int maxLine)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Padding = new Padding(10),
        };
        root.Controls.Add(
            new Label { Text = $"行番号(&L)（1〜{maxLine}）:", AutoSize = true },
            0,
            0
        );
        root.Controls.Add(_number, 1, 0);

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
        _number.Select(0, _number.Text.Length);
    }
}
