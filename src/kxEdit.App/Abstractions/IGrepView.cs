namespace kxEdit.App;

/// <summary>
/// GrepDialog 生成時に渡す Controller 側コールバック束(Phase 2 Stage 7・上位文書 §2.2)。
/// ビュー→Controller 方向を delegate 化することで GrepDialog から GrepController への型参照
/// (相互参照)を断つ(Stage 4 の <see cref="FindReplaceCallbacks"/> と同型)。
/// RunAsync は Func&lt;Task&gt;: UI 側は <c>async (_, _) =&gt; await cb.RunAsync();</c> で
/// fire-and-forget、テストは戻り値の Task を await できる。
/// </summary>
public sealed record GrepCallbacks(Func<Task> RunAsync, Action Cancel);

/// <summary>
/// GrepDialog の Controller 向け表面(Phase 2 設計書 §2.2)。
/// GrepController は入力値の読み取りとこの表示操作だけでビューを扱う。
/// IsDisposed は Progress コールバック/await 後の再入判定で従来コードを一字一句保存するために載せる
/// (Form が既に持つ)。Visible は Stage 4 の <see cref="IFindReplaceView"/> との対称性のため公開
/// (実ビューは自クラス内の <c>ShowAndFocus</c> で Form.Visible を直接参照・Fake は Show/Hide の可視状態検証に用いる)。
/// </summary>
public interface IGrepView
{
    string Pattern { get; }
    string Folder { get; }
    string Filter { get; }
    bool Recursive { get; }
    bool MatchCase { get; }
    bool WholeWord { get; }
    bool UseRegex { get; }
    bool Visible { get; }
    bool IsDisposed { get; }

    void SetFolder(string path);
    void SetRunning(bool running);
    void SetStatus(string text);
    void RaiseNotification(string message);

    /// <summary>従来の Open 手順「非表示なら Show(owner)→Activate→FocusPattern」を 1 メソッドに集約(Stage 4 と同型)。</summary>
    void ShowAndFocus(IWin32Window owner);
}
