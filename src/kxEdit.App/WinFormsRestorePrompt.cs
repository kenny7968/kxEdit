using kxEdit.Core.Backup;

namespace kxEdit.App;

/// <summary>
/// 起動時復元ダイアログの WinForms Adapter(Phase 2 Stage 5・上位文書 §2.2)。
/// RestoreDialog を ShowDialog し、内部 enum(RestoreDialog.RestoreAction)を
/// App 層公開 enum(<see cref="RestoreAction"/>)にマップして結果 record を返す。
/// </summary>
public sealed class WinFormsRestorePrompt : IRestorePrompt
{
    public RestoreOutcome Prompt(IWin32Window owner, IReadOnlyList<BackupRecord> records)
    {
        using var dlg = new RestoreDialog(records);
        dlg.ShowDialog(owner);
        return dlg.Action switch
        {
            RestoreDialog.RestoreAction.Restore => new RestoreOutcome(
                RestoreAction.Restore,
                dlg.Checked
            ),
            RestoreDialog.RestoreAction.DiscardAll => new RestoreOutcome(
                RestoreAction.DiscardAll,
                Array.Empty<BackupRecord>()
            ),
            _ => RestoreOutcome.LaterEmpty,
        };
    }
}
