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

    /// <summary>
    /// OK/キャンセル(警告アイコン)。OK で true。文字コード劣化警告など。
    /// <paramref name="defaultCancel"/> = true でフォーカス既定をキャンセル側に置く(S-12)。
    /// **破壊的な確認では必ず true にすること**: SaveAs ダイアログは AcceptButton = OK なので、
    /// SR ユーザーの主経路は「ファイル名を打つ → Enter」であり、読み上げが遅いときの Enter 連打で
    /// 直後の MessageBox まで確定してしまう。既定が OK 側だと、確認を足したこと自体が
    /// その打鍵パターンで無力化される。
    /// </summary>
    bool OkCancel(string text, string caption, bool defaultCancel = false);

    /// <summary>はい/いいえ/キャンセル(警告アイコン)。未保存確認。</summary>
    DialogResult YesNoCancel(string text, string caption);
}
