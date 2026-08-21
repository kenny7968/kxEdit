using System.Runtime.InteropServices;

namespace kxEdit.Editor;

internal static class NativeMethods
{
    public const int WM_GETOBJECT = 0x003D;

    /// <summary>UIA がプロバイダを要求するときの WM_GETOBJECT lParam。</summary>
    public const int UiaRootObjectId = -25;

    // P5 Task 7: ネイティブ表面原則 = 本文非公開。WM_GETTEXT / WM_GETTEXTLENGTH に応答しない
    // (SR は UIA 経路のみで読む・MSAA / GetWindowText 経由の漏洩を防ぐ)。
    public const int WM_GETTEXT = 0x000D;
    public const int WM_GETTEXTLENGTH = 0x000E;

    // Win32 システムキャレット API(UI スレッド専用)。P2 Task 10 で EditorControl から使用。
    // フォーカスを持つウィンドウ毎に 1 個だけ。CreateCaret 後 ShowCaret で表示・DestroyCaret で破棄。

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateCaret(nint hWnd, nint hBitmap, int nWidth, int nHeight);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCaretPos(int X, int Y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowCaret(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyCaret();

    // GetCaretPos は Task 7 レビュー C-1 の回帰テストで使用(EnsureVisibleCharRange 後に
    // OS 側キャレット位置が savedCaret に戻っていることを検証する)。
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCaretPos(out System.Drawing.Point lpPoint);

    // ==========================================================================================
    // P4 (IME) 用: WM_IME_* メッセージ / IMM32 API / 関連構造体
    // ==========================================================================================

    // Windows メッセージ(WinUser.h)
    public const int WM_IME_STARTCOMPOSITION = 0x010D;
    public const int WM_IME_ENDCOMPOSITION = 0x010E;
    public const int WM_IME_COMPOSITION = 0x010F;
    public const int WM_IME_SETCONTEXT = 0x0281;
    public const int WM_IME_NOTIFY = 0x0282;

    // WM_IME_SETCONTEXT lParam ビット(既定 UI 抑止用)
    // ISC_SHOWUICOMPOSITIONWINDOW = 0x80000000。C# は int リテラル 0x80000000 が uint 扱いなので
    // unchecked((int)0x80000000) を使う。
    public const int ISC_SHOWUICOMPOSITIONWINDOW = unchecked((int)0x80000000);

    // ImmGetCompositionString の dwIndex(GCS_ フラグ)
    public const int GCS_COMPSTR = 0x0008;
    public const int GCS_COMPATTR = 0x0010;
    public const int GCS_COMPCLAUSE = 0x0020;
    public const int GCS_CURSORPOS = 0x0080;
    public const int GCS_RESULTSTR = 0x0800;

    // ImmNotifyIME の dwAction / dwIndex(NI_COMPOSITIONSTR 用)
    public const int NI_COMPOSITIONSTR = 0x0015;
    public const int CPS_COMPLETE = 0x0001;
    public const int CPS_CANCEL = 0x0004;

    // COMPOSITIONFORM.dwStyle / CANDIDATEFORM.dwStyle
    public const int CFS_DEFAULT = 0x0000;
    public const int CFS_POINT = 0x0002;
    public const int CFS_CANDIDATEPOS = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CANDIDATEFORM
    {
        public int dwIndex;
        public int dwStyle;
        public POINT ptCurrentPos;
        public RECT rcArea;
    }

    // フォント伝達に使う LOGFONT(WinForms Font.ToLogFont に渡す)。
    // 【重要】 Font.ToLogFont(object) は内部で GCHandle.Alloc(Pinned) するため、
    // 引数の struct は blittable でなければならない(参照型フィールドが 1 つでもあると
    // 「Object contains references」で ArgumentException になる=Task 12 実装で判明)。
    // よって lfFaceName は `[MarshalAs(ByValTStr)] string` ではなく `fixed char lfFaceName[32]`
    // (blittable 固定バッファ)で表現する=struct 自体を unsafe にする(csproj で
    // AllowUnsafeBlocks を有効化)。バッファは LOGFONTW と同じ 32 wchar = 64 バイト。
    // 直接読む場面は無く(IME に流すだけ)、Font.ToLogFont が中で GDI GetObjectW 相当の
    // メモリコピーを行うことでフェイス名も自動で書き込まれる。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public unsafe struct LOGFONT
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        public fixed char lfFaceName[32];
    }

    [DllImport("imm32.dll")]
    public static extern nint ImmGetContext(nint hWnd);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ImmReleaseContext(nint hWnd, nint hIMC);

    // ImmGetCompositionString: バッファ長を返す(先に大きさ問い合わせ→バッファ確保→本呼び出し のパターン)
    // lpBuf に IntPtr.Zero を渡すと必要バイト数を返す(自身のバッファは要らない)。
    [DllImport("imm32.dll")]
    public static extern int ImmGetCompositionStringW(
        nint hIMC,
        int dwIndex,
        nint lpBuf,
        int dwBufLen
    );

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ImmSetCandidateWindow(nint hIMC, ref CANDIDATEFORM lpCandidate);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ImmSetCompositionFontW(nint hIMC, ref LOGFONT lplf);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ImmNotifyIME(nint hIMC, int dwAction, int dwIndex, int dwValue);
}
