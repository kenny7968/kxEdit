namespace yEdit.App;

/// <summary>
/// FindReplaceDialog 生成時に渡す Controller 側コールバック束。
/// ビュー→Controller 方向を delegate 化することで FindReplaceDialog から
/// SearchController への型参照(相互参照)を断つ(Phase 2 設計書 §5)。
/// FindNext/FindPrev の bool は「ヒットして選択を移動できた」— 検索モードの
/// ダイアログが自身を Hide するか(G-2)の判断に使う。
/// </summary>
public sealed record FindReplaceCallbacks(
    Func<bool> FindNext,
    Func<bool> FindPrev,
    Action ReplaceOne,
    Action ReplaceAll,
    Action UpdateCount,
    Action<bool> InSelectionToggled
);

/// <summary>
/// FindReplaceDialog の Controller 向け表面(Phase 2 設計書 §2.2)。
/// SearchController は入力値の読み取りとこの表示操作だけでビューを扱う。
/// IsDisposed は Open の再生成チェック(owner クローズ等での破棄検出)を
/// 従来コードのまま保存するために載せる(Form が既に持つ)。
/// </summary>
public interface IFindReplaceView
{
    string Pattern { get; }
    string Replacement { get; }
    bool MatchCase { get; }
    bool WholeWord { get; }
    bool UseRegex { get; }
    bool InSelection { get; }
    bool Visible { get; }
    bool IsDisposed { get; }
    void SetMode(bool replaceMode);
    void SetStatus(string text);

    /// <summary>従来の Open 手順を 1 メソッドに集約: 非表示なら Show(owner)し、常に Activate→検索語フォーカス。</summary>
    void ShowAndFocus(IWin32Window owner);

    /// <summary>
    /// ユーザーが検索を終えた(閉じるボタン / Escape / タイトルバーの×)。
    /// <para>
    /// <b>G-2 の自動 Hide では発火しない。</b> 「次を検索」成功後にダイアログが自らを
    /// Hide するのは一時退避であって終了ではなく、その後も F3 で検索は続く。
    /// したがって購読側は <see cref="Visible"/> を終了判定に使ってはならない
    /// (発生源でしか区別できない)。
    /// </para>
    /// </summary>
    event EventHandler? Dismissed;
}
