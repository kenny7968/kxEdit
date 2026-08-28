using kxEdit.Core.Buffers;

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

    /// <summary>
    /// A-9(2026-08-28): スナップショット全体を byte 走査して最も多い改行種別を返す。
    /// 改行が無ければ CRLF(Windows 既定=<see cref="Detect(string)"/> と同じ規則)。
    /// </summary>
    /// <remarks>
    /// <see cref="Detect(string)"/> と多数決の意味論は同一で、走査範囲だけが違う
    /// (旧実装は先頭 4,096 文字を <c>GetText</c> して string 化していた=1 行目が窓より長い
    /// LF ファイルが CRLF と誤判定され、保存時に全行が書き換わっていた)。
    /// string を実体化しないので 512MB 級の文書でもピークメモリは増えない。
    /// UTF-8 では 0x0D / 0x0A がマルチバイト文字の継続バイト(0x80 以上)として現れないため、
    /// byte 走査と char 走査は同じ結果になる。
    /// CR がピース境界を跨ぐケースは <c>pendingCr</c> で持ち越す(落とすと 4MB チャンク境界の
    /// CRLF が CR + LF に化けて多数決が反転しうる)。
    /// </remarks>
    public static LineEnding Detect(TextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int crlf = 0,
            lf = 0,
            cr = 0;
        bool pendingCr = false;
        foreach (var piece in PieceTree.Enumerate(snapshot.Root))
        {
            var span = piece.Chunk.Span.Slice(piece.ByteStart, piece.ByteLen);
            for (int i = 0; i < span.Length; i++)
            {
                byte b = span[i];
                if (pendingCr)
                {
                    // 前ピース末尾の CR を持ち越し中。今の byte が LF なら CRLF、
                    // それ以外なら CR 単独として数えてから今の byte を通常処理へ進める。
                    pendingCr = false;
                    if (b == 0x0A)
                    {
                        crlf++;
                        continue;
                    }
                    cr++;
                }
                if (b == 0x0D)
                {
                    if (i + 1 < span.Length)
                    {
                        if (span[i + 1] == 0x0A)
                        {
                            crlf++;
                            i++;
                        }
                        else
                            cr++;
                    }
                    else
                        pendingCr = true; // ピース末尾 CR=次ピース先頭を見ないと判別不能
                }
                else if (b == 0x0A)
                    lf++;
            }
        }
        if (pendingCr)
            cr++; // 文書末尾の単独 CR
        if (crlf == 0 && lf == 0 && cr == 0)
            return LineEnding.Crlf;
        if (crlf >= lf && crlf >= cr)
            return LineEnding.Crlf;
        return lf >= cr ? LineEnding.Lf : LineEnding.Cr;
    }
}
