// IImeContext.cs
// Phase 3 (Task 3a) で導入した Imm32 P/Invoke seam。
// 本番実装 = WinImeContext (Handle をラップ)。テスト実装 = FakeImeContext。
// null / 0 返却で「P/Invoke 失敗 or IME 無効」の 2 ケースを 1 パターンに集約する。
// Dispose = ImmReleaseContext (使い捨て・呼び出しごとに Func<IImeContext> factory で new)。
using System.Drawing;

namespace kxEdit.Editor.Abstractions;

/// <summary>
/// Task 3a: ImeController が Imm32 P/Invoke を叩くための seam。
/// factory で毎回 new / using で自動 Dispose = ImmGetContext / ImmReleaseContext の pair 化を強制。
/// </summary>
public interface IImeContext : IDisposable
{
    /// <summary>
    /// himc (IME context handle) が取得できているか。false = P/Invoke 失敗 or IME 無効。
    /// false のとき Get*/Set*/Cancel*/Complete* を呼んでも no-op (null/0/[] 返却 or 副作用なし)。
    /// caller (ImeController) は本 property で早期 return し、
    /// 元 <c>EditorControl.Ime.cs:122</c> の <c>if (hIMC == IntPtr.Zero) return;</c> と bit-perfect に揃える。
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>ImmGetCompositionStringW で UTF-16 文字列を取得 (GCS_COMPSTR / GCS_RESULTSTR)。</summary>
    /// <remarks>himc == 0 で null、byteLen <= 0 で "" (旧 ReadImeString と同挙動)。</remarks>
    string? GetCompositionString(long gcsFlags);

    /// <summary>ImmGetCompositionStringW で raw バイト列を取得 (GCS_COMPATTR / GCS_COMPCLAUSE)。</summary>
    /// <remarks>himc == 0 で null、byteLen <= 0 で [] (旧 ReadImeBytes と同挙動)。</remarks>
    byte[]? GetCompositionBytes(long gcsFlags);

    /// <summary>ImmGetCompositionStringW で int を取得 (GCS_CURSORPOS; 戻り値そのものが値)。</summary>
    /// <remarks>himc == 0 で 0 (旧 ReadImeInt と同挙動)。</remarks>
    int GetCompositionInt(long gcsFlags);

    /// <summary>ImmSetCandidateWindow で candidate window を client 座標 (x, y) に設定。himc == 0 は no-op。</summary>
    void SetCandidateWindow(int x, int y);

    /// <summary>ImmSetCompositionFontW で composition font を設定。himc == 0 は no-op。</summary>
    void SetCompositionFont(Font font);

    /// <summary>ImmNotifyIME(NI_COMPOSITIONSTR, CPS_CANCEL) で composition をキャンセル。himc == 0 は no-op。</summary>
    void CancelComposition();

    /// <summary>ImmNotifyIME(NI_COMPOSITIONSTR, CPS_COMPLETE) で composition を確定試行。himc == 0 は no-op。</summary>
    void CompleteComposition();
}
