// NativeMethods.cs
// 設計 2026-09-14 §4: 単一インスタンスの引き渡しで使う Win32 宣言。
// 宣言のスタイルは src/kxEdit.Editor/NativeMethods.cs に合わせる(DllImport + MarshalAs)。
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace kxEdit.App;

internal static class NativeMethods
{
    internal const int SW_RESTORE = 9;

    /// <summary>
    /// クライアント側のパイプハンドルから、サーバ側プロセスの PID を得る。
    /// 相手が本当に kxEdit かを確かめる起点(設計 §4 ★3)。
    /// </summary>
    /// <remarks>
    /// 引数を <c>nint</c> ではなく <see cref="SafePipeHandle"/> で受けるのは、
    /// <c>DangerousGetHandle</c> を避けるため(S3869)。マーシャラが呼び出しの間だけ
    /// 参照カウントを上げてくれるので、呼び出し中に GC / Dispose でハンドルが
    /// 閉じられて別のオブジェクトへ再利用される競合も同時に消える。
    /// </remarks>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle Pipe,
        out uint ServerProcessId
    );

    /// <summary>
    /// 自分が持つフォアグラウンド権を指定プロセスへ譲渡する(設計 §4 ★1)。
    /// これを呼ばないと相手の <c>SetForegroundWindow</c> は拒否され、
    /// タスクバーボタンが点滅するだけで前面に出ない。
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(uint dwProcessId);

    /// <summary>
    /// オーナーウィンドウに対する「現在有効な最前面のポップアップ」。
    /// モーダルダイアログ表示中にオーナーを前面化すると入力を吸われるため、
    /// 実際に前面化すべき相手をこれで求める(設計 §4 ★2・Task 5 で使用)。
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern nint GetLastActivePopup(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowEnabled(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);
}
