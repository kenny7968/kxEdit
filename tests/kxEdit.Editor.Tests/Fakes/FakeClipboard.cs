using System.Runtime.InteropServices;
using kxEdit.Editor.Abstractions;

namespace kxEdit.Editor.Tests.Fakes;

/// <summary>
/// <see cref="IClipboard"/> のフェイク。<see cref="ThrowOnSet"/> / <see cref="ThrowOnGet"/> /
/// <see cref="ThrowOnContains"/> で <see cref="ExternalException"/> を注入して
/// A-13 の失敗経路を作る。
/// </summary>
/// <remarks>
/// 実クリップボード(プロセス横断のグローバル資源)を触らないので、
/// <c>ClipboardTests</c> と違い <c>Category=LocalOnly</c> 化が不要 = CI でも走る。
/// </remarks>
public sealed class FakeClipboard : IClipboard
{
    public string Text { get; set; } = "";
    public bool HasText { get; set; }
    public bool ThrowOnSet { get; set; }
    public bool ThrowOnGet { get; set; }
    public bool ThrowOnContains { get; set; }
    public int SetCount { get; private set; }

    // reason: 実 Clipboard が他プロセス保持中に投げる型そのものを再現するのが本フェイクの存在意義。
    // EditorControl 側の catch は ExternalException 限定(設計 §4.1)なので、派生型で代用すると
    // 「実際に投げられる型を捕捉できるか」を検証したことにならない。テスト専用の fake に限定。
#pragma warning disable CA2201
    public bool ContainsUnicodeText()
    {
        if (ThrowOnContains)
            throw new ExternalException("clipboard busy");
        return HasText;
    }

    public string GetUnicodeText()
    {
        if (ThrowOnGet)
            throw new ExternalException("clipboard busy");
        return Text;
    }

    public void SetUnicodeText(string text)
    {
        SetCount++;
        if (ThrowOnSet)
            throw new ExternalException("clipboard busy");
        Text = text;
        HasText = true;
    }
#pragma warning restore CA2201
}
