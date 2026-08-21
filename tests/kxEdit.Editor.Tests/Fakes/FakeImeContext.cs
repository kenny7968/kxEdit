// FakeImeContext.cs
// Phase 3 (Task 3a) で ImeController の pure テスト用に IImeContext を差し替える fake。
// Strings/Bytes/Ints で GCS_ フラグ毎に返却値を制御。Cancel/Complete/Dispose/SetCandidateWindow/
// SetCompositionFont は呼び出し観察用フラグに記録。
using System.Drawing;
using kxEdit.Editor.Abstractions;

namespace kxEdit.Editor.Tests.Fakes;

internal sealed class FakeImeContext : IImeContext
{
    /// <summary>IMP-1 fixup: 既定 true (既存テスト互換)。false = himc==0 相当 (P/Invoke 失敗 or IME 無効)。</summary>
    public bool IsAvailable { get; set; } = true;

    public Dictionary<long, string?> Strings { get; } = new();
    public Dictionary<long, byte[]?> Bytes { get; } = new();
    public Dictionary<long, int> Ints { get; } = new();
    public (int X, int Y)? CandidateWindow { get; private set; }
    public Font? CompositionFont { get; private set; }
    public bool CancelCalled { get; private set; }
    public bool CompleteCalled { get; private set; }
    public bool Disposed { get; private set; }

    public string? GetCompositionString(long gcsFlags) =>
        Strings.TryGetValue(gcsFlags, out var s) ? s : null;

    public byte[]? GetCompositionBytes(long gcsFlags) =>
        Bytes.TryGetValue(gcsFlags, out var b) ? b : null;

    public int GetCompositionInt(long gcsFlags) => Ints.TryGetValue(gcsFlags, out var i) ? i : 0;

    public void SetCandidateWindow(int x, int y) => CandidateWindow = (x, y);

    public void SetCompositionFont(Font font) => CompositionFont = font;

    public void CancelComposition() => CancelCalled = true;

    public void CompleteComposition() => CompleteCalled = true;

    public void Dispose() => Disposed = true;
}
