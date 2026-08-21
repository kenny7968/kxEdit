using kxEdit.Core.Text;

namespace kxEdit.App;

/// <summary>
/// <see cref="IFileDialogService"/> の本番実装。既存ダイアログ
/// (OpenFileDialog/SaveAsDialog/EncodingPickDialog)を従来と同一の引数・フィルタで
/// 表示し、結果だけを返す薄い Adapter(ロジックなし=挙動不変)。
/// </summary>
internal sealed class WinFormsFileDialogService : IFileDialogService
{
    public string? PickOpenPath(IWin32Window owner)
    {
        using var dlg = new OpenFileDialog
        {
            Filter =
                "対応ファイル (*.txt, *.md, *.csv)|*.txt;*.md;*.csv|すべてのファイル (*.*)|*.*",
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
