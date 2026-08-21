namespace kxEdit.App;

/// <summary>
/// セル指定ダイアログの WinForms Adapter(Phase 2 Stage 6・上位文書 §2.2)。
/// <see cref="CsvGoToCellDialog"/> を ShowDialog し、DialogResult+TryGetCell の 2 段判定を
/// App 層公開の <see cref="CellPickResult"/> にマップする(Cancel/InvalidFormat/Ok の 3 相を保存)。
/// </summary>
public sealed class WinFormsCellPicker : ICellPicker
{
    public CellPickResult Pick(IWin32Window owner, int currentRow1, int currentCol1)
    {
        using var dlg = new CsvGoToCellDialog(currentRow1, currentCol1);
        if (dlg.ShowDialog(owner) != DialogResult.OK)
            return CellPickResult.Canceled;
        if (!dlg.TryGetCell(out int r1, out int c1))
            return CellPickResult.InvalidFormat;
        return CellPickResult.Ok(r1, c1);
    }
}
