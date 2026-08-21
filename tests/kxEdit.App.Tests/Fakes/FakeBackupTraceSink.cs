using kxEdit.App;

namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// BackupCoordinator の catch 4 箇所が確かに trace を呼ぶことを assert するための Fake。
/// カテゴリ・回数・保持した例外を検証可能に露出する。可視性は他 Fake(FakeBackupWriter 等)と
/// 揃えて public sealed(App.Tests 内でのみ消費・他プロジェクトからは参照されない)。
/// </summary>
public sealed class FakeBackupTraceSink : IBackupTraceSink
{
    public List<(string Category, string Detail, Exception? Ex)> Warnings { get; } = new();

    public void Warn(string category, string detail, Exception? ex) =>
        Warnings.Add((category, detail, ex));
}
