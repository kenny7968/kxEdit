using kxEdit.Core.Backup;

namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IRestorePrompt"/> のテスト用フェイク。次回の Prompt 呼び出しで返す結果を
/// <see cref="NextOutcome"/> に事前登録する(既定は Later)。<see cref="LastRecords"/> で
/// Coordinator が渡した records(降順ソート済み)を検証できる。
/// </summary>
public sealed class FakeRestorePrompt : IRestorePrompt
{
    public RestoreOutcome NextOutcome { get; set; } = RestoreOutcome.LaterEmpty;
    public int PromptCount;
    public IReadOnlyList<BackupRecord>? LastRecords;

    public RestoreOutcome Prompt(IWin32Window owner, IReadOnlyList<BackupRecord> records)
    {
        PromptCount++;
        LastRecords = records;
        return NextOutcome;
    }
}
