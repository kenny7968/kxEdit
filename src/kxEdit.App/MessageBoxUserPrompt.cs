namespace kxEdit.App;

/// <summary>
/// <see cref="IUserPrompt"/> の本番実装。従来 FileController 内に直書きされていた
/// MessageBox.Show を同一引数のまま包むだけの薄い Adapter(ロジックなし=挙動不変)。
/// </summary>
internal sealed class MessageBoxUserPrompt : IUserPrompt
{
    public void Info(string text, string caption) =>
        MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public void Warn(string text, string caption) =>
        MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public void Error(string text, string caption) =>
        MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);

    public bool OkCancel(string text, string caption) =>
        MessageBox.Show(text, caption, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning)
        == DialogResult.OK;

    public DialogResult YesNoCancel(string text, string caption) =>
        MessageBox.Show(text, caption, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
}
