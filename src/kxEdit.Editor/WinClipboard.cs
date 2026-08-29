// WinClipboard.cs
// A-13(設計 2026-08-29 §4): IClipboard の本番実装。
using kxEdit.Editor.Abstractions;

namespace kxEdit.Editor;

/// <summary>
/// <see cref="IClipboard"/> の本番実装。<see cref="Clipboard"/> をそのまま呼ぶだけで、
/// リトライも例外の握り潰しもしない(<see cref="Clipboard.SetText(string, TextDataFormat)"/> は
/// 内部で 10 回 × 100ms リトライ済み=設計 §10)。
/// </summary>
internal sealed class WinClipboard : IClipboard
{
    public bool ContainsUnicodeText() => Clipboard.ContainsText(TextDataFormat.UnicodeText);

    public string GetUnicodeText() => Clipboard.GetText(TextDataFormat.UnicodeText);

    public void SetUnicodeText(string text) => Clipboard.SetText(text, TextDataFormat.UnicodeText);
}
