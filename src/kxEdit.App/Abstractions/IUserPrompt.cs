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
    /// **既定値を持たせないのは意図的**(最終品質パス I-5): 破壊的な確認で OK 側の既定を選ぶと、
    /// SaveAs ダイアログは AcceptButton = OK なので「ファイル名を打つ → Enter」という
    /// SR ユーザーの主経路そのもの(読み上げが遅いときの Enter 連打)で、確認を足したこと自体が
    /// 無力化される。「破壊的なら true を渡すこと」という散文の約束ではなく、
    /// **呼出のたびにコンパイラが側を選ばせる**ことで機構にしてある。
    /// 安全側は常に true。false を渡してよいのは「押し間違えても失うものが無い」確認だけ。
    /// </summary>
    bool OkCancel(string text, string caption, bool defaultCancel);

    /// <summary>はい/いいえ/キャンセル(警告アイコン)。未保存確認。</summary>
    DialogResult YesNoCancel(string text, string caption);
}
