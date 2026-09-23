using kxEdit.Core.Text;

namespace kxEdit.App;

/// <summary>
/// <see cref="IFileDialogService"/> の本番実装。既存ダイアログ
/// (OpenFileDialog/SaveAsDialog/EncodingPickDialog)を表示し、結果だけを返す
/// 薄い Adapter(ロジックなし)。
/// </summary>
internal sealed class WinFormsFileDialogService : IFileDialogService
{
    public string? PickOpenPath(IWin32Window owner)
    {
        using var dlg = new OpenFileDialog
        {
            // 拡張子で絞り込まない(どの拡張子も最初から選べる)。
            Filter = "すべてのファイル (*.*)|*.*",
        };
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.FileName : null;
    }

    public SaveAsResult? PickSaveAs(IWin32Window owner, SaveAsRequest current)
    {
        using var dlg = new SaveAsDialog(
            current.Path,
            current.CodePage,
            current.HasBom,
            current.LineEnding
        );
        if (dlg.ShowDialog(owner) != DialogResult.OK)
            return null;
        return new SaveAsResult(
            dlg.SelectedPath,
            dlg.SelectedCodePage,
            dlg.SelectedHasBom,
            dlg.SelectedLineEnding
        );
    }

    public int? PickEncoding(IWin32Window owner, int currentCodePage)
    {
        using var dlg = new EncodingPickDialog(currentCodePage);
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.SelectedCodePage : null;
    }
}
