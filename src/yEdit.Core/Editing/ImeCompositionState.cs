using yEdit.Core.Text;

namespace yEdit.Core.Editing;

public readonly record struct ImeCompositionState(
    int Start,
    string Text,
    int CursorPos,
    byte[] Attrs,
    int[] Clauses
)
{
    public static ImeCompositionState Empty { get; } = new(0, "", 0, [], []);
    public bool IsActive => Text.Length > 0;

    /// <summary>GCS_COMPATTR のバイト列をそのままコピー(未確定文字列 UTF-16 code unit ごと 1 バイト)。</summary>
    public static byte[] ParseAttrs(ReadOnlySpan<byte> src)
    {
        if (src.Length == 0)
            return [];
        var buf = new byte[src.Length];
        src.CopyTo(buf);
        return buf;
    }

    /// <summary>GCS_COMPCLAUSE のバイト列を int32 (little-endian) 配列としてデコード。
    /// 半端バイト(4 の倍数でない末尾)は切り捨てる。</summary>
    public static int[] ParseClauses(ReadOnlySpan<byte> src)
    {
        int n = src.Length / 4;
        if (n == 0)
            return [];
        var buf = new int[n];
        for (int i = 0; i < n; i++)
        {
            int off = i * 4;
            buf[i] = src[off] | (src[off + 1] << 8) | (src[off + 2] << 16) | (src[off + 3] << 24);
        }
        return buf;
    }

    /// <summary>CursorPos がサロゲート pair の low 位置を指していたら high 位置にスナップ。
    /// あわせて [0, text.Length] へクランプする。</summary>
    /// <remarks>
    /// 2026-07-31: サロゲート判定の実体は <see cref="Text.TextBoundary.SnapToCodePointStart"/>
    /// へ移した(未確定文字列は改行を含まないので code-point 単位で足りる)。
    /// <paramref name="text"/> の null は明示ガードで <see cref="ArgumentNullException"/> にする=
    /// <c>AsSpan()</c> の暗黙変換に流すと null が静かに空 span 化して 0 を返すようになり、
    /// 呼び出し側のバグを隠すため(<see cref="Text.TextBoundary"/> の snapshot 版と同じ方針)。
    /// </remarks>
    public static int SnapCursorPos(string text, int cursor)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TextBoundary.SnapToCodePointStart(text.AsSpan(), cursor);
    }
}
