// ClipboardFailureKind.cs
// A-13(設計 2026-08-29 §4.3): EditorControl.ClipboardFailed の引数。
namespace kxEdit.Editor;

/// <summary>
/// どのクリップボード操作が失敗したか。App 層が発声文言を選ぶために使う。
/// </summary>
/// <remarks>
/// Editor 層は <c>IAnnouncer</c>(App 層)を参照できない(層の向きが逆になる)ため、
/// 「何が起きたか」だけを渡して文言の決定は App 層に委ねる。
/// </remarks>
public enum ClipboardFailureKind
{
    /// <summary>コピー / 切り取りの書き込みが失敗した。</summary>
    Write,

    /// <summary>貼り付けの読み取りが失敗した。</summary>
    Read,
}
