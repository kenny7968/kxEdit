namespace yEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IFindReplaceView"/> のテスト用フェイク。入力値(Pattern 等)は直接設定し、
/// SetMode/SetStatus/ShowAndFocus の呼び出しをメソッドごとに記録する(メソッド間の相対順序は
/// 記録しない=表示手順の順序は diff レビューの担保範囲)。
/// ShowAndFocus は Visible=true にする(実ダイアログの Show 相当)。Hide 相当は
/// テストが Visible=false を直接設定する(G-2 の「次を検索」後 Hide の再現)。
/// </summary>
public sealed class FakeFindReplaceView : IFindReplaceView
{
    public string Pattern { get; set; } = "";
    public string Replacement { get; set; } = "";
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }
    public bool UseRegex { get; set; }
    public bool InSelection { get; set; }
    public bool Visible { get; set; }
    public bool IsDisposed { get; set; }

    public List<bool> ModeLog { get; } = new(); // SetMode(replaceMode) の履歴
    public List<string> StatusLog { get; } = new(); // SetStatus の履歴
    public int ShowAndFocusCount;

    /// <summary>現在表示中のステータス(未設定なら null)。</summary>
    public string? Status => StatusLog.Count == 0 ? null : StatusLog[^1];

    public void SetMode(bool replaceMode) => ModeLog.Add(replaceMode);

    public void SetStatus(string text) => StatusLog.Add(text);

    public void ShowAndFocus(IWin32Window owner)
    {
        ShowAndFocusCount++;
        Visible = true;
    }

    public event EventHandler? Dismissed;

    /// <summary>テストから「ユーザーが検索を終えた」を再現する(実ダイアログの閉じる/Escape/×相当)。</summary>
    public void RaiseDismissed()
    {
        Visible = false;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
}
