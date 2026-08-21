namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// <see cref="IUserPrompt"/> のテスト用フェイク。応答を事前登録し、
/// 呼ばれた種別・文言・キャプションを順序どおり記録する(Phase 2 設計書 §3 共通ユーティリティ)。
/// </summary>
public sealed class FakePrompt : IUserPrompt
{
    public List<(string Kind, string Text, string Caption)> Log { get; } = new();
    public bool OkCancelResult { get; set; } = true;
    public DialogResult YesNoCancelResult { get; set; } = DialogResult.Cancel;

    public void Info(string text, string caption) => Log.Add(("Info", text, caption));

    public void Warn(string text, string caption) => Log.Add(("Warn", text, caption));

    public void Error(string text, string caption) => Log.Add(("Error", text, caption));

    public bool OkCancel(string text, string caption)
    {
        Log.Add(("OkCancel", text, caption));
        return OkCancelResult;
    }

    public DialogResult YesNoCancel(string text, string caption)
    {
        Log.Add(("YesNoCancel", text, caption));
        return YesNoCancelResult;
    }
}
