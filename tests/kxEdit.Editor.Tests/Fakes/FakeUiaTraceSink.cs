using kxEdit.Accessibility;

namespace kxEdit.Editor.Tests.Fakes;

/// <summary>
/// UIA-L-2: UiaTextHostAdapter.RaiseUia の catch が確かに trace を呼ぶことを assert するための Fake。
/// カテゴリ・detail・保持した例外を検証可能に露出する。App.Tests 側 <c>FakeUiaTraceSink</c> と同型。
/// </summary>
public sealed class FakeUiaTraceSink : IUiaTraceSink
{
    public List<(string Category, string Detail, Exception Exception)> Warnings { get; } = new();

    public void Warn(string category, string detail, Exception ex) =>
        Warnings.Add((category, detail, ex));
}
