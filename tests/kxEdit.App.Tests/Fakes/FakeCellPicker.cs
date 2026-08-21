namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="ICellPicker"/> のテスト用フェイク。次回の Pick で返す結果を <see cref="NextResult"/> に
/// 事前登録する(既定は Canceled)。Coordinator が渡した現在セル(1 始まり)は
/// <see cref="LastCurrentRow1"/>/<see cref="LastCurrentCol1"/> で検証できる。
/// </summary>
public sealed class FakeCellPicker : ICellPicker
{
    public CellPickResult NextResult { get; set; } = CellPickResult.Canceled;
    public int PickCount;
    public int LastCurrentRow1;
    public int LastCurrentCol1;

    public CellPickResult Pick(IWin32Window owner, int currentRow1, int currentCol1)
    {
        PickCount++;
        LastCurrentRow1 = currentRow1;
        LastCurrentCol1 = currentCol1;
        return NextResult;
    }
}
