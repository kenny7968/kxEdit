namespace kxEdit.App;

/// <summary>セル指定ダイアログの結果 kind(Phase 2 Stage 6・上位文書 §2.2)。</summary>
public enum CellPickKind
{
    /// <summary>ユーザーが Cancel/Esc/×ボタンで閉じた(無音扱い)。</summary>
    Canceled,

    /// <summary>OK を押したが入力書式が不正("行,列" として解釈不能)。呼び出し側が「書式が不正です」を通知する。</summary>
    InvalidFormat,

    /// <summary>OK+書式 OK。<see cref="CellPickResult.Row1"/>/<see cref="CellPickResult.Col1"/> は 1 始まり。範囲外判定は呼び出し側(CsvController)が行う。</summary>
    Ok,
}

/// <summary>
/// セル指定ダイアログの結果 record。Kind ごとに Row1/Col1 の意味が変わる(Ok 以外は既定 0)。
/// Stage 5 の <see cref="RestoreOutcome"/> と同型(sentinel readonly+Ok ファクトリ)。
/// </summary>
public sealed record CellPickResult(CellPickKind Kind, int Row1, int Col1)
{
    public static readonly CellPickResult Canceled = new(CellPickKind.Canceled, 0, 0);
    public static readonly CellPickResult InvalidFormat = new(CellPickKind.InvalidFormat, 0, 0);

    public static CellPickResult Ok(int row1, int col1) => new(CellPickKind.Ok, row1, col1);
}

/// <summary>
/// CSV モードのセル指定移動(G キー)ダイアログの Controller 向け表面。
/// 実装は既存 <c>CsvGoToCellDialog</c> をラップする Adapter(<c>WinFormsCellPicker</c>)。
/// 現在セルは 1 始まりで渡す(現行ダイアログの初期値表示と同じ座標系)。
/// </summary>
public interface ICellPicker
{
    CellPickResult Pick(IWin32Window owner, int currentRow1, int currentCol1);
}
