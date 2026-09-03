namespace kxEdit.App;

/// <summary>
/// <see cref="IUserPrompt"/> の本番実装。従来 FileController 内に直書きされていた
/// MessageBox.Show を同一引数のまま包むだけの薄い Adapter(ロジックなし=挙動不変)。
/// 唯一の写像が <c>OkCancel</c> の <c>defaultCancel</c> → <see cref="MessageBoxDefaultButton"/>
/// (S-12。既定値は持たない=呼出側が必ず側を選ぶ。<see cref="IUserPrompt.OkCancel"/> の doc 参照)。
/// </summary>
internal sealed class MessageBoxUserPrompt : IUserPrompt
{
    public void Info(string text, string caption) =>
        MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public void Warn(string text, string caption) =>
        MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public void Error(string text, string caption) =>
        MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);

    public bool OkCancel(string text, string caption, bool defaultCancel) =>
        MessageBox.Show(
            text,
            caption,
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            defaultCancel ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1
        ) == DialogResult.OK;

    public DialogResult YesNoCancel(string text, string caption) =>
        MessageBox.Show(text, caption, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);

    public bool YesNo(string text, string caption, bool defaultNo) =>
        MessageBox.Show(
            text,
            caption,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            defaultNo ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1
        ) == DialogResult.Yes;
}
