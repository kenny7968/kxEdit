using System.Text;

namespace kxEdit.Core.Buffers;

/// <summary>不正UTF-8のU+FFFD置換。妥当な入力はゼロコピーでそのまま返す。</summary>
internal static class Utf8Sanitizer
{
    public static (ReadOnlyMemory<byte> Clean, bool Replaced) Sanitize(ReadOnlyMemory<byte> input)
    {
        if (System.Text.Unicode.Utf8.IsValid(input.Span))
            return (input, false);
        // 稀ケースなので decode→re-encode で十分(チャンク単位=最大4MB)
        string s = Encoding.UTF8.GetString(input.Span); // 既定で U+FFFD 置換
        return (Encoding.UTF8.GetBytes(s), true);
    }
}
