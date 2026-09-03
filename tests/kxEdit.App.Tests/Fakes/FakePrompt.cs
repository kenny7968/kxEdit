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

    /// <summary>
    /// <c>OkCancel</c> の呼出ごとの (Caption, defaultCancel)。破壊的な確認が安全側の既定
    /// (キャンセルにフォーカス)で出ているかを L3 で pin するための観測点(S-12)。
    /// <see cref="Log"/> のタプルへ足さないのは、形状を変えると既存の網が一斉に壊れるため。
    /// </summary>
    public List<(string Caption, bool DefaultCancel)> OkCancelCalls { get; } = new();

    public bool OkCancel(string text, string caption, bool defaultCancel)
    {
        Log.Add(("OkCancel", text, caption));
        OkCancelCalls.Add((caption, defaultCancel));
        return OkCancelResult;
    }

    public DialogResult YesNoCancel(string text, string caption)
    {
        Log.Add(("YesNoCancel", text, caption));
        return YesNoCancelResult;
    }

    /// <summary>許可側(はい)の既定。拒否経路のテストは明示的に false を入れる(<see cref="OkCancelResult"/> と同じ流儀)。</summary>
    public bool YesNoResult { get; set; } = true;

    /// <summary><c>YesNo</c> の呼出ごとの (Caption, defaultNo)。<see cref="OkCancelCalls"/> と対称(設計 2026-09-03 §3.6)。理由も同じ。</summary>
    public List<(string Caption, bool DefaultNo)> YesNoCalls { get; } = new();

    /// <summary>M-18 再入テスト用: 応答を返す直前に呼ぶ(ダイアログのモーダルループの中で
    /// 別の検知が届く状況を再現する)。</summary>
    public Action? OnYesNo { get; set; }

    public bool YesNo(string text, string caption, bool defaultNo)
    {
        Log.Add(("YesNo", text, caption));
        YesNoCalls.Add((caption, defaultNo));
        OnYesNo?.Invoke();
        return YesNoResult;
    }
}
