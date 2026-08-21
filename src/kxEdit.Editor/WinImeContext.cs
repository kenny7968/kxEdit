// WinImeContext.cs
// Phase 3 (Task 3a) で導入した IImeContext の本番実装。ctor で ImmGetContext / Dispose で
// ImmReleaseContext を pair 化する (=旧 EditorControl.Ime.cs の try/finally パターンを
// using にリファクタ)。_himc == 0 (IME 無効 or 取得失敗) では全操作を no-op / null 返却で吸収。
using System.Drawing;
using System.Runtime.InteropServices;
using kxEdit.Editor.Abstractions;

namespace kxEdit.Editor;

/// <summary>
/// Task 3a: Imm32 P/Invoke を hwnd に紐付けてラップする本番 IImeContext。
/// 使い捨て (using / Dispose で ImmReleaseContext) 前提=ImeController の Func&lt;IImeContext&gt;
/// factory から呼び出しごとに new する。旧 EditorControl.Ime.cs の各 P/Invoke ロジックを
/// bit-perfect 移設 (buffer 確保順序 / Encoding / GCS_CURSORPOS の「戻り値そのものが値」パターン)。
/// </summary>
internal sealed class WinImeContext : IImeContext
{
    private readonly nint _hwnd;
    private nint _himc;
    private bool _disposed;

    public WinImeContext(nint hwnd)
    {
        _hwnd = hwnd;
        _himc = NativeMethods.ImmGetContext(hwnd);
    }

    /// <summary>himc != 0=IME context 取得成功。false は P/Invoke 失敗 or IME 無効。</summary>
    public bool IsAvailable => _himc != IntPtr.Zero;

    /// <summary>旧 <c>EditorControl.ReadImeString</c> 移設。ImmGetCompositionStringW の 2 段階呼び出しパターン。</summary>
    public string? GetCompositionString(long gcsFlags)
    {
        if (_himc == IntPtr.Zero)
            return null;
        int gcs = (int)gcsFlags;
        int byteLen = NativeMethods.ImmGetCompositionStringW(_himc, gcs, IntPtr.Zero, 0);
        if (byteLen <= 0)
            return "";
        nint buf = Marshal.AllocHGlobal(byteLen);
        try
        {
            // CA1806: 2 段呼び出しの 2 段目は buf を埋めるためだけの呼び出しで返却バイト数は
            // 事前計算した byteLen と同じ想定=戻り値を意図的に破棄する(`_ =`)。旧 ReadImeString と等価。
            _ = NativeMethods.ImmGetCompositionStringW(_himc, gcs, buf, byteLen);
            // byteLen は UTF-16 のバイト数=char 数は byteLen / 2 (旧 ReadImeString と同じ)。
            return Marshal.PtrToStringUni(buf, byteLen / 2) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>旧 <c>EditorControl.ReadImeBytes</c> 移設。GCS_COMPATTR / GCS_COMPCLAUSE の raw byte 取得。</summary>
    public byte[]? GetCompositionBytes(long gcsFlags)
    {
        if (_himc == IntPtr.Zero)
            return null;
        int gcs = (int)gcsFlags;
        int byteLen = NativeMethods.ImmGetCompositionStringW(_himc, gcs, IntPtr.Zero, 0);
        if (byteLen <= 0)
            return [];
        nint buf = Marshal.AllocHGlobal(byteLen);
        try
        {
            // CA1806: 2 段呼び出しの 2 段目は buf を埋めるためだけの呼び出しで返却バイト数は
            // 事前計算した byteLen と同じ想定=戻り値を意図的に破棄する(`_ =`)。旧 ReadImeBytes と等価。
            _ = NativeMethods.ImmGetCompositionStringW(_himc, gcs, buf, byteLen);
            var arr = new byte[byteLen];
            Marshal.Copy(buf, arr, 0, byteLen);
            return arr;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>旧 <c>EditorControl.ReadImeInt</c> 移設。GCS_CURSORPOS は「戻り値そのものが値」。</summary>
    public int GetCompositionInt(long gcsFlags)
    {
        if (_himc == IntPtr.Zero)
            return 0;
        return NativeMethods.ImmGetCompositionStringW(_himc, (int)gcsFlags, IntPtr.Zero, 0);
    }

    /// <summary>旧 <c>EditorControl.NotifyCandidateWindow</c> 内の ImmSetCandidateWindow 呼び出し部分を移設。</summary>
    public void SetCandidateWindow(int x, int y)
    {
        if (_himc == IntPtr.Zero)
            return;
        var form = new NativeMethods.CANDIDATEFORM
        {
            dwIndex = 0,
            dwStyle = NativeMethods.CFS_CANDIDATEPOS,
            ptCurrentPos = new NativeMethods.POINT { x = x, y = y },
        };
        NativeMethods.ImmSetCandidateWindow(_himc, ref form);
    }

    /// <summary>旧 <c>EditorControl.NotifyCompositionFont</c> の LOGFONT 変換 + ImmSetCompositionFontW 呼び出しを移設。</summary>
    /// <remarks>
    /// Font.ToLogFont(object) は boxing 経由でしか変異させないため box → 変異 → unbox する
    /// (Task 1 レビュー watchpoint)。ローカル struct 直渡しはコピーだけが書かれてローカルは 0 のまま。
    /// </remarks>
    public void SetCompositionFont(Font font)
    {
        if (_himc == IntPtr.Zero)
            return;
        object boxed = new NativeMethods.LOGFONT();
        font.ToLogFont(boxed);
        var lf = (NativeMethods.LOGFONT)boxed;
        NativeMethods.ImmSetCompositionFontW(_himc, ref lf);
    }

    /// <summary>旧 <c>EditorControl.CancelCompositionAndDefault</c> の ImmNotifyIME(CPS_CANCEL) を移設。</summary>
    public void CancelComposition()
    {
        if (_himc == IntPtr.Zero)
            return;
        NativeMethods.ImmNotifyIME(
            _himc,
            NativeMethods.NI_COMPOSITIONSTR,
            NativeMethods.CPS_CANCEL,
            0
        );
    }

    /// <summary>旧 <c>EditorControl.OnLostFocus</c> 内の ImmNotifyIME(CPS_COMPLETE) を移設。</summary>
    public void CompleteComposition()
    {
        if (_himc == IntPtr.Zero)
            return;
        NativeMethods.ImmNotifyIME(
            _himc,
            NativeMethods.NI_COMPOSITIONSTR,
            NativeMethods.CPS_COMPLETE,
            0
        );
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_himc != IntPtr.Zero)
        {
            NativeMethods.ImmReleaseContext(_hwnd, _himc);
            _himc = IntPtr.Zero;
        }
    }
}
