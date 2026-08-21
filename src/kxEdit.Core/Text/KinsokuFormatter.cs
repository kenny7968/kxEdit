using System.Text;

namespace kxEdit.Core.Text;

/// <summary>
/// 日本語の禁則処理付き折り返し整形（純アルゴリズム・UI/Scintilla 非依存）。
/// 指定桁(半角換算)を超える論理行に実改行(eol)を挿入して分割し、行頭禁則(追い出し)・
/// 行末禁則・句読点ぶら下げで折り位置を調整する。既存の改行は保持する。
/// </summary>
public static class KinsokuFormatter
{
    private readonly record struct Cell(int Idx, int Len, int Cp);

    /// <summary>
    /// text を columns 桁(半角換算)で禁則整形する。columns&lt;=0 や空文字はそのまま返す。
    /// 挿入する改行は eol。各禁則文字集合は空文字でそのルールを無効化する。
    /// </summary>
    public static string Format(
        string text,
        int columns,
        string lineStartChars,
        string lineEndChars,
        string hangChars,
        string eol,
        int tabWidth = 8
    )
    {
        if (string.IsNullOrEmpty(text) || columns <= 0)
            return text;
        if (tabWidth <= 0)
            tabWidth = 8; // 公開API防御: width % tabWidth のゼロ除算/負値を防ぐ

        var lineStart = ToSet(lineStartChars);
        var lineEnd = ToSet(lineEndChars);
        var hang = ToSet(hangChars);

        var sb = new StringBuilder(text.Length + text.Length / 8 + 16);
        int i = 0,
            n = text.Length;
        while (i < n)
        {
            int contentBeg = i,
                j = i;
            while (j < n && text[j] != '\n' && text[j] != '\r')
                j++;
            int contentEnd = j;
            string term = "";
            if (j < n)
            {
                if (text[j] == '\r' && j + 1 < n && text[j + 1] == '\n')
                {
                    term = "\r\n";
                    j += 2;
                }
                else
                {
                    term = text[j].ToString();
                    j += 1;
                }
            }
            WrapLine(
                text,
                contentBeg,
                contentEnd,
                columns,
                tabWidth,
                lineStart,
                lineEnd,
                hang,
                eol,
                sb
            );
            sb.Append(term);
            i = j;
        }
        return sb.ToString();
    }

    private static void WrapLine(
        string text,
        int beg,
        int end,
        int columns,
        int tabWidth,
        HashSet<int> lineStart,
        HashSet<int> lineEnd,
        HashSet<int> hang,
        string eol,
        StringBuilder sb
    )
    {
        var cells = BuildCells(text, beg, end);
        if (cells.Count == 0)
            return;

        int startCell = 0;
        while (startCell < cells.Count)
        {
            int cut = FindCut(cells, startCell, columns, tabWidth);
            if (cut >= cells.Count)
            {
                AppendCells(sb, text, cells, startCell, cells.Count);
                return;
            }
            cut = AdjustForHang(cells, cut, hang);
            if (cut >= cells.Count)
            {
                AppendCells(sb, text, cells, startCell, cells.Count);
                return;
            }
            cut = AdjustForKinsoku(cells, startCell, cut, lineStart, lineEnd);
            AppendCells(sb, text, cells, startCell, cut);
            sb.Append(eol);
            startCell = cut;
        }
    }

    /// <summary>startCell から貪欲に columns 桁まで詰め、次行先頭になるセル index を返す（最低1セル前進）。</summary>
    private static int FindCut(List<Cell> cells, int startCell, int columns, int tabWidth)
    {
        int width = 0,
            k = startCell;
        while (k < cells.Count)
        {
            int cp = cells[k].Cp;
            int w = cp == '\t' ? tabWidth - (width % tabWidth) : EastAsianWidth.ColumnWidth(cp);
            if (width + w > columns && k > startCell)
                break;
            width += w;
            k++;
        }
        return k;
    }

    /// <summary>cut（次行先頭）がぶら下げ文字なら現在行へ取り込む（桁超過を許容）。</summary>
    private static int AdjustForHang(List<Cell> cells, int cut, HashSet<int> hang)
    {
        if (hang.Count == 0)
            return cut;
        while (cut < cells.Count && hang.Contains(cells[cut].Cp))
            cut++;
        return cut;
    }

    /// <summary>行頭禁則(追い出し)・行末禁則で cut を上限付きに戻す。隣も禁則/空行化なら処理せず違反許容。</summary>
    private static int AdjustForKinsoku(
        List<Cell> cells,
        int startCell,
        int cut,
        HashSet<int> lineStart,
        HashSet<int> lineEnd
    )
    {
        const int maxPush = 8; // 戻し回数の上限。通常は連鎖ガードで早く止まる。超えたら違反を許容して幾何位置で折る。
        for (int g = 0; g < maxPush; g++)
        {
            bool startBad = cut < cells.Count && lineStart.Contains(cells[cut].Cp);
            bool endBad = cut - 1 > startCell && lineEnd.Contains(cells[cut - 1].Cp);
            if (!startBad && !endBad)
                break;
            if (cut - 1 <= startCell)
                break; // 現在行に最低1セル残す
            if (startBad && lineStart.Contains(cells[cut - 1].Cp))
                break; // 連鎖防止(行頭)
            if (endBad && lineEnd.Contains(cells[cut - 2].Cp))
                break; // 連鎖防止(行末)
            cut--;
        }
        return cut;
    }

    private static List<Cell> BuildCells(string text, int beg, int end)
    {
        var cells = new List<Cell>(end - beg);
        int p = beg;
        while (p < end)
        {
            int len = 1,
                cp;
            if (char.IsHighSurrogate(text[p]) && p + 1 < end && char.IsLowSurrogate(text[p + 1]))
            {
                cp = char.ConvertToUtf32(text, p);
                len = 2;
            }
            else
                cp = text[p];
            cells.Add(new Cell(p, len, cp));
            p += len;
        }
        return cells;
    }

    private static void AppendCells(
        StringBuilder sb,
        string text,
        List<Cell> cells,
        int from,
        int to
    )
    {
        if (to <= from)
            return;
        int charStart = cells[from].Idx;
        int charEnd = to < cells.Count ? cells[to].Idx : cells[to - 1].Idx + cells[to - 1].Len;
        sb.Append(text, charStart, charEnd - charStart);
    }

    private static HashSet<int> ToSet(string chars)
    {
        var set = new HashSet<int>();
        if (string.IsNullOrEmpty(chars))
            return set; // null/空セット = そのルール無効（手編集 null での NRE も防ぐ）
        for (int i = 0; i < chars.Length; )
        {
            int len = 1,
                cp;
            if (
                char.IsHighSurrogate(chars[i])
                && i + 1 < chars.Length
                && char.IsLowSurrogate(chars[i + 1])
            )
            {
                cp = char.ConvertToUtf32(chars, i);
                len = 2;
            }
            else
                cp = chars[i];
            set.Add(cp);
            i += len;
        }
        return set;
    }
}
