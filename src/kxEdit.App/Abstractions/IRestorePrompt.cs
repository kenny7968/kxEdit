using kxEdit.Core.Backup;

namespace kxEdit.App;

/// <summary>復元ダイアログのユーザー選択(Phase 2 Stage 5・上位文書 §2.2)。</summary>
public enum RestoreAction
{
    Later,
    Restore,
    DiscardAll,
}

/// <summary>復元ダイアログの結果 record。Action が Restore 以外なら Checked は空配列。</summary>
public sealed record RestoreOutcome(RestoreAction Action, IReadOnlyList<BackupRecord> Checked)
{
    public static readonly RestoreOutcome LaterEmpty = new(
        RestoreAction.Later,
        Array.Empty<BackupRecord>()
    );
}

/// <summary>
/// 起動時の復元ダイアログの Controller 向け表面。ダイアログを ShowDialog し、
/// ユーザー選択(Later/Restore/DiscardAll)+チェック済み records をまとめて返す。
/// </summary>
public interface IRestorePrompt
{
    RestoreOutcome Prompt(IWin32Window owner, IReadOnlyList<BackupRecord> records);
}
