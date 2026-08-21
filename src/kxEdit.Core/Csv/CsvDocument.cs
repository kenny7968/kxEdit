namespace kxEdit.Core.Csv;

/// <summary>CSV のパース結果。Rows は論理行×フィールド。Ok=false は不正（引用符未終端）。</summary>
public sealed partial class CsvDocument
{
    public IReadOnlyList<IReadOnlyList<CsvField>> Rows { get; }
    public bool Ok { get; }

    public CsvDocument(IReadOnlyList<IReadOnlyList<CsvField>> rows, bool ok)
    {
        Rows = rows;
        Ok = ok;
    }

    /// <summary>キャレット（UTF-16 オフセット）を含むセルを返す。含むセルが無ければ直前のセル、無ければ原点。</summary>
    public (int row, int col) FindCell(int caretOffset)
    {
        (int row, int col) best = (0, 0);
        int bestStart = -1;
        bool found = false;
        for (int r = 0; r < Rows.Count; r++)
        {
            var row = Rows[r];
            for (int c = 0; c < row.Count; c++)
            {
                var f = row[c];
                if (caretOffset >= f.Start && caretOffset <= f.Start + f.Length)
                    return (r, c);
                if (f.Start <= caretOffset && f.Start > bestStart)
                {
                    bestStart = f.Start;
                    best = (r, c);
                    found = true;
                }
            }
        }
        return found ? best : (0, 0);
    }

    /// <summary>row,col から方向 dir に1セル移動した座標。範囲外・端で動けなければ null。上下移動は列数不足の行で末尾列にクランプ。</summary>
    public (int row, int col)? MoveCell(int row, int col, Direction dir)
    {
        if (row < 0 || row >= Rows.Count)
            return null;
        if (col < 0 || col >= Rows[row].Count)
            return null;
        return dir switch
        {
            Direction.Left => col > 0 ? (row, col - 1) : null,
            Direction.Right => col < Rows[row].Count - 1 ? (row, col + 1) : null,
            Direction.Up => row > 0 ? (row - 1, ClampCol(row - 1, col)) : null,
            Direction.Down => row < Rows.Count - 1 ? (row + 1, ClampCol(row + 1, col)) : null,
            _ => null,
        };
    }

    private int ClampCol(int row, int col)
    {
        int last = Rows[row].Count - 1;
        if (last < 0)
            return 0; // 空行（Parser は生成しないが、手組み CsvDocument 対策）
        return col > last ? last : col;
    }

    /// <summary>row,col のフィールド。範囲外は null。</summary>
    public CsvField? GetField(int row, int col)
    {
        if (row < 0 || row >= Rows.Count)
            return null;
        var r = Rows[row];
        if (col < 0 || col >= r.Count)
            return null;
        return r[col];
    }

    /// <summary>col 列の先頭セル（見出し＝0行目）。範囲外は null。</summary>
    public CsvField? Header(int col) => GetField(0, col);

    /// <summary>row 行の左端セル (row,0)。行が無い/空ならnull。</summary>
    public (int row, int col)? RowStart(int row) =>
        (row >= 0 && row < Rows.Count && Rows[row].Count > 0) ? (row, 0) : null;

    /// <summary>row 行の右端セル。行が無い/空ならnull。</summary>
    public (int row, int col)? RowEnd(int row) =>
        (row >= 0 && row < Rows.Count && Rows[row].Count > 0) ? (row, Rows[row].Count - 1) : null;

    /// <summary>col 列を持つ最初の行のセル (r,col)。どの行も持たなければnull。</summary>
    public (int row, int col)? ColumnTop(int col)
    {
        if (col < 0)
            return null;
        for (int r = 0; r < Rows.Count; r++)
            if (Rows[r].Count > col)
                return (r, col);
        return null;
    }

    /// <summary>col 列を持つ最後の行のセル (r,col)。どの行も持たなければnull。</summary>
    public (int row, int col)? ColumnBottom(int col)
    {
        if (col < 0)
            return null;
        for (int r = Rows.Count - 1; r >= 0; r--)
            if (Rows[r].Count > col)
                return (r, col);
        return null;
    }

    /// <summary>左上セル (0,0)。データ無しならnull。</summary>
    public (int row, int col)? TopLeft() => (Rows.Count > 0 && Rows[0].Count > 0) ? (0, 0) : null;

    /// <summary>右下セル（最終行の最終列）。データ無しならnull。</summary>
    public (int row, int col)? BottomRight()
    {
        if (Rows.Count == 0)
            return null;
        int r = Rows.Count - 1;
        return Rows[r].Count > 0 ? (r, Rows[r].Count - 1) : null;
    }

    /// <summary>(row,col) が有効なら そのまま返す。範囲外はnull。</summary>
    public (int row, int col)? GoTo(int row, int col) =>
        (row >= 0 && row < Rows.Count && col >= 0 && col < Rows[row].Count) ? (row, col) : null;
}
