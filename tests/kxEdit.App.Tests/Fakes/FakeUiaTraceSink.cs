using kxEdit.Accessibility;

namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// UIA-L-2: UiaAnnouncer.Raise の catch が確かに trace を呼ぶことを assert するための Fake。
/// カテゴリ・detail・保持した例外を検証可能に露出する。
/// 可視性は他 Fake (<see cref="FakeBackupTraceSink"/> 等) と揃えて public sealed。
/// </summary>
public sealed class FakeUiaTraceSink : IUiaTraceSink
{
    public List<(string Category, string Detail, Exception Exception)> Warnings { get; } = new();

    public void Warn(string category, string detail, Exception ex) =>
        Warnings.Add((category, detail, ex));
}
