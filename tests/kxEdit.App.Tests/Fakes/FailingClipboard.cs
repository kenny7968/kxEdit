using System.Runtime.InteropServices;
using kxEdit.Editor.Abstractions;

namespace kxEdit.App.Tests.Fakes;

/// <summary>
/// A-13: 常に失敗する <see cref="IClipboard"/>。App 層のテストで
/// 「実 <c>EditorControl</c> が実際に失敗する」経路を作るために使う。
/// </summary>
/// <remarks>
/// 失敗の詳細な場合分け(Contains / Get / Set のどれで落ちるか・成功時の挙動)は
/// Editor 層の <c>ClipboardFailureTests</c> が担保済み。ここは配線の検証だけが目的なので
/// 「必ず投げる」1 種類にとどめる。実クリップボードは触らない=CI でも走る。
/// </remarks>
internal sealed class FailingClipboard : IClipboard
{
    // reason: 実 Clipboard が他プロセス保持中に投げる型そのものを再現するのが本フェイクの存在意義。
    // EditorControl 側の catch は ExternalException 限定(設計 §4.1)。テスト専用 fake に限定。
#pragma warning disable CA2201
    public bool ContainsUnicodeText() => throw new ExternalException("clipboard busy");

    public string GetUnicodeText() => throw new ExternalException("clipboard busy");

    public void SetUnicodeText(string text) => throw new ExternalException("clipboard busy");
#pragma warning restore CA2201
}
