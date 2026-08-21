namespace kxEdit.Core.Text;

public enum LineEnding
{
    Crlf,
    Lf,
    Cr,
}

/// <summary>LineEnding の表現変換。改行の意味論は Core に集約し、App/Editor はこれを参照する。</summary>
public static class LineEndingExtensions
{
    /// <summary>実際の改行文字列（"\r\n" / "\n" / "\r"）。整形・挿入用。</summary>
    public static string ToEolString(this LineEnding eol) =>
        eol switch
        {
            LineEnding.Lf => "\n",
            LineEnding.Cr => "\r",
            _ => "\r\n",
        };

    /// <summary>短い表示名（"CRLF" / "LF" / "CR"）。ステータスバー等の表示用。</summary>
    public static string ToDisplayString(this LineEnding eol) =>
        eol switch
        {
            LineEnding.Lf => "LF",
            LineEnding.Cr => "CR",
            _ => "CRLF",
        };
}

public static class LineEndingDetector
{
    /// <summary>本文中で最も多い改行種別を返す。改行が無ければ CRLF（Windows 既定）。</summary>
    public static LineEnding Detect(string text)
    {
        int crlf = 0,
            lf = 0,
            cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                    cr++;
            }
            else if (c == '\n')
                lf++;
        }
        if (crlf == 0 && lf == 0 && cr == 0)
            return LineEnding.Crlf;
        if (crlf >= lf && crlf >= cr)
            return LineEnding.Crlf;
        return lf >= cr ? LineEnding.Lf : LineEnding.Cr;
    }
}
