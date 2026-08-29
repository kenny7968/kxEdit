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

    /// <summary><see cref="SetUnicodeText"/> の<b>試行</b>回数(throw する場合も数える)。
    /// 「クリップボードに触っていない」ことの検証に使う。</summary>
    public int SetCount { get; private set; }

    /// <summary><see cref="ContainsUnicodeText"/> の呼び出し回数。同上。</summary>
    public int ContainsCount { get; private set; }

    /// <summary>
    /// Throw* が立っているときに投げる例外を差し替える。既定(null)は
    /// <see cref="ExternalException"/>(実 Clipboard が他プロセス保持中に投げる型)。
    /// <b>用途</b>: 設計 §4.1 の「捕捉は <c>ExternalException</c> 限定で、
    /// 呼び出し側バグ(<c>ArgumentNullException</c> 等)は握り潰さない」を pin する
    /// (<c>catch (Exception)</c> へ広げる変異を kill するため)。
    /// </summary>
    public Exception? ThrowInstead { get; set; }

    // reason: 実 Clipboard が他プロセス保持中に投げる型そのものを再現するのが本フェイクの存在意義。EditorControl 側の catch は ExternalException 限定(設計 §4.1)なので、派生型で代用すると「実際に投げられる型を捕捉できるか」を検証したことにならない。テスト専用の fake に限定。
#pragma warning disable CA2201
    private Exception Failure() => ThrowInstead ?? new ExternalException("clipboard busy");

#pragma warning restore CA2201

    public bool ContainsUnicodeText()
    {
        ContainsCount++;
        if (ThrowOnContains)
            throw Failure();
        return HasText;
    }

    public string GetUnicodeText()
    {
        if (ThrowOnGet)
            throw Failure();
        return Text;
    }

    public void SetUnicodeText(string text)
    {
        SetCount++;
        if (ThrowOnSet)
            throw Failure();
        Text = text;
        HasText = true;
    }
}
