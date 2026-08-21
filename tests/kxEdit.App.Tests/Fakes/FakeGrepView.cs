namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IGrepView"/> のテスト用フェイク。入力値(Pattern/Folder/…)は直接設定し、
/// SetRunning/SetStatus/RaiseNotification/ShowAndFocus/SetFolder の呼び出しを順序どおり記録する。
/// ShowAndFocus は Visible=true にする(実ダイアログの Show 相当)。Hide 相当は
/// テストが Visible=false を直接設定する(現行 GrepDialog の HideAndCancel 経路の再現)。
/// </summary>
public sealed class FakeGrepView : IGrepView
{
    public string Pattern { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Filter { get; set; } = "*.*";
    public bool Recursive { get; set; } = true;
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }
    public bool UseRegex { get; set; }
    public bool Visible { get; set; }
    public bool IsDisposed { get; set; }

    public List<string> FolderLog { get; } = new();
    public List<bool> RunningLog { get; } = new();
    public List<string> StatusLog { get; } = new();
    public List<string> Notifications { get; } = new();
    public int ShowAndFocusCount;

    public string? Status => StatusLog.Count == 0 ? null : StatusLog[^1];

    public void SetFolder(string path)
    {
        Folder = path;
        FolderLog.Add(path);
    }

    public void SetRunning(bool running) => RunningLog.Add(running);

    public void SetStatus(string text) => StatusLog.Add(text);

    public void RaiseNotification(string message) => Notifications.Add(message);

    public void ShowAndFocus(IWin32Window owner)
    {
        ShowAndFocusCount++;
        Visible = true;
    }
}
