namespace kxEdit.App;

/// <summary>
/// ユーザーへの確認・警告(MessageBox のラップ)。Phase 2 設計書 §2.1。
/// テストではフェイクに差し替え、本番は MessageBoxUserPrompt が同一引数の MessageBox を出す。
/// </summary>
public interface IUserPrompt
{
    void Info(string text, string caption);
    void Warn(string text, string caption);
    void Error(string text, string caption);

    /// <summary>OK/キャンセル(警告アイコン)。OK で true。文字コード劣化警告など。</summary>
    bool OkCancel(string text, string caption);

    /// <summary>はい/いいえ/キャンセル(警告アイコン)。未保存確認。</summary>
    DialogResult YesNoCancel(string text, string caption);
}
