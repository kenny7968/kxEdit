using kxEdit.Editor.Abstractions;

namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// A-13: 常に成功する <see cref="IClipboard"/>。App 層の「失敗していないときは通知しない」
/// 対照テスト用。実クリップボード(プロセス横断のグローバル資源)を触らない。
/// </summary>
internal sealed class RecordingClipboard : IClipboard
{
    public string Text { get; private set; } = "";

    public bool ContainsUnicodeText() => Text.Length > 0;

    public string GetUnicodeText() => Text;

    public void SetUnicodeText(string text) => Text = text;
}
