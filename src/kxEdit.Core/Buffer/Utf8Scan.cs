namespace kxEdit.Core.Buffers;

/// <summary>妥当なUTF-8バイト列の統計走査(改行セマンティクス: LF/単独CR/CRLF=1 break・末尾CRは単独扱い)。CR/LFはASCIIなので多バイト列と衝突しない。</summary>
internal static class Utf8Scan
{
    public static (int CharLen, int Breaks, bool FirstIsLf, bool LastIsCr) Stats(
        ReadOnlySpan<byte> s
    )
    {
        int chars = 0,
            breaks = 0;
        for (int i = 0; i < s.Length; i++)
        {
            byte b = s[i];
            if ((b & 0xC0) != 0x80)
                chars++; // 継続バイト以外=コード点先頭
            if (b >= 0xF0)
                chars++; // 4バイト文字はサロゲートペア=+1
            if (b == (byte)'\n')
                breaks++;
            else if (b == (byte)'\r' && (i + 1 >= s.Length || s[i + 1] != (byte)'\n'))
                breaks++;
        }
        bool firstLf = s.Length > 0 && s[0] == (byte)'\n';
        bool lastCr = s.Length > 0 && s[^1] == (byte)'\r';
        return (chars, breaks, firstLf, lastCr);
    }
}
