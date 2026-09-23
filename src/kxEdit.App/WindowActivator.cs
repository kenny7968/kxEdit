// WindowActivator.cs
// 設計 2026-09-14 §4 ★2 / §5: Win32 に触る部分をここ 1 点に押し込める。
namespace kxEdit.App;

/// <summary>
/// 既存インスタンスのウィンドウを前面へ出す。
/// </summary>
/// <remarks>
/// <para>
/// <b>この型に自動テストは無い</b>。実ウィンドウとフォアグラウンド状態が要るためで、
/// 検証は L5(実機・手動)が担当する(設計 §5)。逆に言えば、テストできる判断を
/// ここへ書かないこと —— ここに載せた分だけ自動検証の網から落ちる。
/// </para>
/// <para>
/// 呼び出しは UI スレッドから行うこと。<see cref="Control.Handle"/> はハンドル未生成なら
/// その場で生成しにいくため、UI スレッド以外から触ると生成スレッドが入れ替わる。
/// </para>
/// </remarks>
internal static class WindowActivator
{
    /// <summary>
    /// <paramref name="form"/>(またはその有効なポップアップ)を前面化する。
    /// </summary>
    /// <remarks>
    /// <b>戻り値を持たないのは意図的</b>。前面化の成否は呼び出し側の判断材料にならない
    /// (ACK を返すかどうかは「要求を引き受けたか」で決まる)。
    /// フォアグラウンド移動は OS の裁量で拒否されうる(相手側の
    /// <c>AllowSetForegroundWindow</c> 待ち・フルスクリーンアプリ等)が、
    /// そのときもタスクバーボタンが点滅して利用者に気づく手立ては残る。
    /// ここで失敗を投げ上げて起動経路を止める価値はない。
    /// </remarks>
    internal static void Activate(Form form)
    {
        nint owner = form.Handle;

        // モーダルダイアログ表示中はオーナーが無効化されている。オーナーを前面化すると
        // 入力をダイアログに吸われて操作不能に見えるので、有効なポップアップを狙う(★2)。
        // ポップアップが無ければ GetLastActivePopup はオーナー自身を返す仕様なので、
        // 下の分岐はそのまま「オーナーを狙う」に落ちる(失敗時の 0 も同じ扱い)。
        //
        // ★この取得は下の SW_RESTORE より「前」でなければならない★
        // SW_RESTORE はオーナーを再アクティブ化し、ウィンドウマネージャが持つ
        // "last active popup" の追跡値をオーナー自身へ書き戻す。復帰の後ろへ動かすと
        // 判断材料が消え、★2 が丸ごと無効化される。Task 5 レビューでの実測:
        //   復帰前: IsIconic(A)=True  GetLastActivePopup(A)=D  IsWindowEnabled(A)=False
        //   復帰後: IsIconic(A)=False GetLastActivePopup(A)=A  IsWindowEnabled(A)=False
        // 後者だと「無効化されたオーナー」を前面化することになり、利用者からは
        // 「前面には来るがキーを押すとビープが鳴るだけ・ダイアログはどこにも見えない」
        // (最小化中は所有ウィンドウが Visible=False)という詰み方に見える。
        // モーダル中でも Win+D / Win+M / タスクバー右クリックの最小化は効くので、
        // 「上書き確認を出したまま Win+D、その後ファイルをダブルクリック」で普通に踏む。
        // 順序に依存しない GetWindow(owner, GW_ENABLEDPOPUP) は代替にならない。
        // アクティブ化の履歴ではなく現在の所有関係を見るため、メニューのドロップダウンや
        // ツールチップまで返す(実測)。ツールチップの前面化は明白な退行。
        nint target = NativeMethods.GetLastActivePopup(owner);

        // 最小化されていたら元のサイズへ戻す。WindowState を直接書かないのは、
        // 最大化していた場合に「元は最大化だった」を OS 側が覚えているため。
        // WindowState = Normal と書くと最大化していたウィンドウが通常サイズに落ち、
        // 前面化のついでに利用者のウィンドウ状態を壊す。
        if (NativeMethods.IsIconic(owner))
            NativeMethods.ShowWindow(owner, NativeMethods.SW_RESTORE);

        // 有効性の確認は復帰の後で構わない(EnableWindow の状態は最小化でも変わらない)。
        if (target == 0 || !NativeMethods.IsWindowEnabled(target))
            target = owner;

        // 前面化だけは復帰の「後」であること。最小化中は所有ポップアップが非表示で、
        // 非表示ウィンドウへの SetForegroundWindow は失敗する。
        // 結果としてこの並びは MFC / MSDN の単一インスタンス化サンプルが昔から使う
        // GetLastActivePopup → IsIconic/ShowWindow → SetForegroundWindow と同じ順序になる。
        NativeMethods.SetForegroundWindow(target);
    }
}
