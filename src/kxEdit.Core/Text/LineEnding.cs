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
    /// (旧実装は先頭 4,096 code unit(UTF-16)を <c>GetText</c> して string 化していた=
    /// 1 行目がその窓より長い LF ファイルが CRLF と誤判定され、保存時に全行が書き換わっていた)。
    /// 窓は復活させないこと。string を実体化しないので 512MB 級の文書でもピークメモリは増えない。
    /// UTF-8 では 0x0D / 0x0A がマルチバイト文字の継続バイト(0x80 以上)として現れないため、
    /// byte 走査と char 走査は同じ結果になる。
    ///
    /// 改行の探索は <see cref="MemoryExtensions.IndexOfAny{T}(ReadOnlySpan{T}, T, T)"/> に任せる
    /// (SIMD 化されており、1 バイトずつの比較ループより速い。I-1: 255MB / LF / 40 byte 行で実測)。
    /// CR がピース境界を跨ぐケースは <c>pendingCr</c> で持ち越す(落とすと 4MB チャンク境界の
    /// CRLF が CR + LF に化けて多数決が反転しうる)。持ち越しが立つのは「ピース末尾の CR」だけなので、
    /// その処理はピース先頭の 1 回だけで済む=内側ループの外に置いてある。
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
            // ピースの担当範囲だけを見る(編集後は ByteStart != 0 になり、チャンクには
            // 削除済みバイトが残っている。チャンク全体を見ると消したはずの改行を数え直す)。
            var span = piece.Chunk.Span.Slice(piece.ByteStart, piece.ByteLen);
            // 空ピースガード。下の持ち越し処理が span[0] を無条件に読むための前提条件であり、
            // 同時に持ち越しを次ピースへ素通しする。現行の Piece 生成経路はどれも空ピースを
            // 作らないので到達不能=テストで覚えられない(この行を消す変異は生存する)。
            // それでも残すのは、その不変条件に依存したくないため。生成箇所は複数あり
            // 増減するので、ここで列挙して同期を取ろうとしない(列挙は必ず陳腐化する)。
            if (span.IsEmpty)
                continue;
            int i = 0;
            if (pendingCr)
            {
                // 前ピース末尾の CR を持ち越し中。先頭が LF なら CRLF、それ以外なら CR 単独。
                // 後者では先頭バイトを消費してはならない(それ自体が CR かもしれない)。
                pendingCr = false;
                if (span[0] == 0x0A)
                {
                    crlf++;
                    i = 1;
                }
                else
                    cr++;
            }
            while (i < span.Length)
            {
                int hit = span.Slice(i).IndexOfAny((byte)0x0D, (byte)0x0A);
                if (hit < 0)
                    break; // 残りに改行なし=次ピースへ(pendingCr は false のまま)
                i += hit;
                if (span[i] == 0x0A)
                {
                    lf++;
                    i++;
                }
                else if (i + 1 < span.Length)
                {
                    if (span[i + 1] == 0x0A)
                    {
                        crlf++;
                        i += 2;
                    }
                    else
                    {
                        cr++;
                        i++;
                    }
                }
                else
                {
                    pendingCr = true; // ピース末尾 CR=次ピース先頭を見ないと判別不能
                    break;
                }
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
