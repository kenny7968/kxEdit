using kxEdit.App.Speech;

namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IAnnouncer"/> のテスト用フェイク。Say された文言を順序どおり記録する。
/// 通知文言の検証(「N 件中 M 件目」等)は Stage 2 以降の Controller テストで使う。
/// </summary>
public sealed class FakeAnnouncer : IAnnouncer
{
    public List<string> Said { get; } = new();

    public void Say(string message) => Said.Add(message);
}
